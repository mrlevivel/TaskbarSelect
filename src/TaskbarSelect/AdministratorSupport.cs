using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace TaskbarSelect
{
    // The standard UI owns a medium-integrity pipe. Its elevated client uses
    // anonymous impersonation, so this server cannot borrow the high token.
    // Only fixed numeric window operations and appearance choices cross IPC.
    internal sealed partial class AdministratorSupport : IDisposable
    {
        const string Mode = "--administrator-helper", Prefix = "TaskbarSelect.Admin.";
        const int MaximumPacket = 128, Request = 1, Authorize = 2, Focus = 3, PersistentSetup = 4,
            Ready = 10, FocusReply = 11, ResultReply = 12, SetupReady = 13, SetupReply = 14, SetupAuthorize = 15,
            ReleasePins = 16, ReleaseReady = 17, ReleaseAuthorize = 18, ReleaseReply = 19,
            SnapPoll = 20, SnapReply = 21;
        static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;
        readonly object gate = new object(), ioGate = new object();
        readonly Peer owner;
        readonly HeartbeatScheduler heartbeat;
        readonly AdministratorPinRecovery pinRecovery;
        readonly Func<IntPtr, FocusedWindowSnapshot> readPinAuthorization;
        NamedPipeServerStream pipe;
        ProcessLifetime helper;
        LaunchSession launchSession;
        int pendingShutdowns, pendingOperations;
        bool retirementConfirmed;
        bool active, enabling, disposed, uncertain, focusQueued, setupPending, savedApprovalAvailable, shutdownConfirmed = true;
        bool permissionChangePending, disablePending, pinReleasePending, pinsMayRemain;
        internal bool PermissionChangePending
        {
            get { lock (gate) return permissionChangePending; }
            set { lock (gate) permissionChangePending = value; }
        }
        int generation, sequence, focusRevision, failureGeneration = -1;
        int[] focus = { 0, 0, 1, 0, -1, 0, 1 };
        long focusSent;
        Lease operation;
        ReconnectCandidate reconnectCandidate;
        bool reconnectUsed;
        int reconnectRearmGeneration = -1;
        LaunchSession reconnectRearmSession;
        Lease reconnectRearmLease;
        readonly int controllerThread = Thread.CurrentThread.ManagedThreadId;
        string lastIncident = "";
        int lastIncidentGeneration = -1;
        string lastError = "";
        internal static bool IsHelperProcess { get; private set; }
        internal bool Active { get { lock (gate) return active; } }
        // Numeric, read-only recognition of our already authenticated helper's
        // focus HWND. This only orders our visual overlays; it grants no window
        // operation, helper access, or authority to the target-binding marker.
        internal bool OwnsFocusSurface(IntPtr window, IntPtr target)
        {
            if (FocusedWindowPolicy.SpecialHandle(window) || FocusedWindowPolicy.SpecialHandle(target)) return false;
            try { lock (gate) {
                if (!active || disposed || uncertain || permissionChangePending || helper == null || helper.HasExited) return false;
                uint process;
                if (GetWindowThreadProcessId(window, out process) == 0 || process != helper.Id ||
                    NativeProcessIdentity.Started(process) != helper.Started) return false;
                return FocusIndicatorSurface.MarksFocusSurface(window, target) && !helper.HasExited;
            } } catch (Exception) { return false; }
        }
        // UI evidence only. Admission always reopens and verifies protected
        // registration; a settings preference never grants elevated access.
        internal bool SavedApprovalAvailable { get { lock (gate) return savedApprovalAvailable; } }
        internal bool Enabling { get { lock (gate) return enabling; } }
        internal bool HasUncertainOperation { get { lock (gate) return uncertain; } }
        internal string LastError { get { lock (gate) return lastError; } }
        internal string LastIncident { get { lock (gate) return lastIncident; } }
        internal string Status { get { lock (gate) return uncertain ? "Recovery pending" : active ? "On" : enabling ? "Waiting for approval" : lastError.Length != 0 ? "Unavailable" : "Off"; } }
        internal AdministratorSupport()
            : this(new AdministratorPinRecovery(), delegate(IntPtr window) { return FocusedWindowNative.Read(window, true); }) { }
        internal AdministratorSupport(AdministratorPinRecovery recovery, Func<IntPtr, FocusedWindowSnapshot> readPin)
        {
            if (recovery == null || readPin == null) throw new ArgumentNullException();
            pinRecovery = recovery; readPinAuthorization = readPin;
            owner = ReadPeer((uint)Process.GetCurrentProcess().Id);
            if (owner == null) lastError = "This Windows account could not be verified.";
            // Focus receipts are also the helper's liveness proof. They must
            // not depend on the GUI STA being able to render or service timers.
            // Publication of Active still follows authenticated admission.
            heartbeat = new HeartbeatScheduler(Heartbeat);
        }
        internal sealed class HeartbeatScheduler : IDisposable
        {
            readonly object sync = new object();
            readonly System.Threading.Timer timer;
            readonly Action beat;
            int busy;
            bool running, disposed;
            internal HeartbeatScheduler(Action callback)
            {
                beat = callback;
                timer = new System.Threading.Timer(delegate { Tick(); }, null, Timeout.Infinite, Timeout.Infinite);
            }
            internal void Start()
            { lock (sync) { if (disposed) return; running = true; timer.Change(0, 100); } }
            internal void Stop()
            { lock (sync) { running = false; if (!disposed) timer.Change(Timeout.Infinite, Timeout.Infinite); } }
            void Tick()
            {
                if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
                try {
                    lock (sync) if (!running || disposed) return;
                    beat();
                } finally { Interlocked.Exchange(ref busy, 0); }
            }
            public void Dispose()
            { lock (sync) { if (disposed) return; running = false; disposed = true; timer.Dispose(); } }
        }
        void StartHeartbeat()
        { lock (gate) if (!disposed && active) heartbeat.Start(); }
        void Heartbeat()
        {
            int token;
            lock (gate) { if (disposed || !active) return; token = generation; }
            try { Poll(); }
            catch (Exception error) { Fail(token, FailureEvidence("Administrator support disconnected. Pending recovery was retained.", "HeartbeatPoll", 0, -1, 0, error, false)); }
        }
        internal enum FocusIoPhase { Admission, Write, Read, Reply }
        internal enum FocusReadStage { None, Header, Body, Complete }
        internal sealed class FocusReadSnapshot
        {
            internal readonly FocusIoPhase Phase;
            internal readonly FocusReadStage Stage;
            internal readonly int HeaderBytes, BodyBytes, BodyExpected;
            internal FocusReadSnapshot(FocusIoPhase phase, FocusReadStage stage, int header, int body, int expected)
            { Phase = phase; Stage = stage; HeaderBytes = header; BodyBytes = body; BodyExpected = expected; }
            internal string Summary()
            { return " io=" + Phase + " read=" + ((int)Stage).ToString(Numbers) + " header=" + HeaderBytes.ToString(Numbers) +
                "/4 body=" + BodyBytes.ToString(Numbers) + "/" + BodyExpected.ToString(Numbers); }
        }
        internal sealed class FocusExchangePhase
        {
            readonly object sync = new object();
            FocusIoPhase phase;
            FocusReadStage readStage;
            int headerBytes, bodyBytes, bodyExpected;
            internal void Set(FocusIoPhase value)
            { lock (sync) { phase = value; if (value == FocusIoPhase.Read) BeginReadLocked(false, 4); } }
            internal string Name { get { lock (sync) return phase.ToString(); } }
            void BeginReadLocked(bool body, int expected)
            {
                readStage = body ? FocusReadStage.Body : FocusReadStage.Header;
                if (body) { bodyExpected = Math.Max(0, Math.Min(MaximumPacket, expected)); bodyBytes = 0; }
                else { headerBytes = bodyBytes = bodyExpected = 0; }
            }
            internal void BeginRead(bool body, int expected) { lock (sync) BeginReadLocked(body, expected); }
            internal void ReadProgress(bool body, int count)
            { lock (sync) { if (body) bodyBytes = Math.Max(0, Math.Min(bodyExpected, count)); else headerBytes = Math.Max(0, Math.Min(4, count)); } }
            internal void ReadComplete() { lock (sync) readStage = FocusReadStage.Complete; }
            internal FocusReadSnapshot Snapshot()
            { lock (sync) return new FocusReadSnapshot(phase, readStage, headerBytes, bodyBytes, bodyExpected); }
        }
        internal sealed class FocusPipeAvailability
        {
            internal bool Sampled, Success, Capped;
            internal uint Available;
            internal int NativeError, HResult;
            internal string Summary()
            { return " avail=" + Available.ToString(Numbers) + " capped=" + (Capped ? "1" : "0") + " peek=" + (Success ? "1" : "0") +
                " sampled=" + (Sampled ? "1" : "0") + " peekWin32=" + NativeError.ToString(Numbers) + " peekHR=0x" + HResult.ToString("X8", Numbers); }
        }
        internal static FocusPipeAvailability SampleFocusAvailability(PipeStream stream)
        {
            var evidence = new FocusPipeAvailability();
            SafePipeHandle handle = null; bool held = false;
            try {
                // Synchronous handles can block in Peek on another reader.
                // This advisory call never reads bytes, duplicates a handle or
                // owns the transport. Lease only this exact asynchronous handle.
                if (stream == null || !stream.IsAsync) return evidence;
                handle = stream.SafePipeHandle;
                handle.DangerousAddRef(ref held);
                uint available;
                evidence.Sampled = true;
                evidence.Success = PeekNamedPipeAvailable(handle.DangerousGetHandle(), IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero);
                evidence.NativeError = evidence.Success ? 0 : Marshal.GetLastWin32Error();
                if (evidence.Success) { evidence.Available = Math.Min(512U, available); evidence.Capped = available > 512; }
            } catch (Exception error) { evidence.HResult = error.HResult; }
            finally { if (held) handle.DangerousRelease(); }
            return evidence;
        }
        string FocusDeadlineDiagnostic(int token, PipeStream stream, FocusExchangePhase progress)
        {
            FocusReadSnapshot snapshot = progress.Snapshot();
            bool current;
            lock (gate) current = !disposed && active && generation == token && Object.ReferenceEquals(pipe, stream);
            // A stale watcher has no authority to sample the next session's
            // handle. Fail independently keeps its generation/first-cause fence.
            return snapshot.Summary() + (current ? SampleFocusAvailability(stream).Summary() : " stale=1");
        }
        sealed class Peer
        {
            internal uint Process, Session;
            internal long Started;
            internal string Sid, Path;
            internal uint Integrity;
            internal bool Elevated;
        }
        sealed class Lease
        {
            internal volatile bool Ready, Authorized, CancelRequested, Terminal, Complete;
            internal bool Mutation;
            internal int Sequence;
            internal long Began;
            internal WindowMoveWorker.Result Result;
        }
        sealed class ReconnectCandidate
        {
            internal int Generation;
            internal Lease Lease;
            internal LaunchSession Session;
            internal long Began;
        }
        // Advisory numeric evidence from the same predicate invocation that
        // refused admission. It cannot authorize work or change cancellation.
        internal interface IValidityDiagnostic { int LastValidityReason { get; } }
        static int ValidityReason(Func<bool> predicate)
        {
            try {
                var source = predicate == null ? null : predicate.Target as IValidityDiagnostic;
                int reason = source == null ? 0 : source.LastValidityReason;
                return reason >= 0 && reason <= 31 ? reason : 0;
            } catch (Exception) { return 0; }
        }
        static string OperationEvidence(Lease lease, int reason)
        {
            return " validity=" + reason.ToString(Numbers) + " ready=" + (lease.Ready ? "1" : "0") +
                " authorized=" + (lease.Authorized ? "1" : "0") + " terminal=" + (lease.Terminal ? "1" : "0") +
                " complete=" + (lease.Complete ? "1" : "0");
        }
        void PublishTerminal(Lease lease, WindowMoveWorker.Result result, int uncertainWorkers)
        { PublishOperationTerminal(lease, result, uncertainWorkers, -1, -1); }
        void PublishOperationTerminal(Lease lease, WindowMoveWorker.Result result, int uncertainWorkers, int ownedPins, int token)
        {
            // A validated terminal reply and its mutation uncertainty are one
            // publication. Complete remains the separate worker-finally fence
            // after ioGate is released, including for an unconfirmed mutation.
            lock (gate) {
                lease.Result = result;
                uncertain |= lease.Mutation && lease.Authorized && (!result.CallerExited || uncertainWorkers != 0);
                // Only a live same-generation terminal op6 receipt can replace
                // the conservative authorization latch with actual ownership.
                if (ownedPins >= 0 && token == generation && active && !disposed && failureGeneration != token && Object.ReferenceEquals(operation, lease))
                    { pinsMayRemain = ownedPins != 0; if (ownedPins == 0) pinRecovery.Reset(); }
                lease.Terminal = true;
            }
        }
        sealed class LaunchSession
        {
            internal readonly object Gate = new object();
            internal NamedPipeServerStream Pipe, Approval;
            internal AdministratorLaunch.BootstrapImage Image;
            internal AdministratorLaunch.ApprovalStartException PendingStartFailure;
            internal Process ApprovalHost;
            internal PersistentAdministrator.Image PersistentImage;
            internal ProcessLifetime ScheduledBootstrap;
            internal ProcessLifetime DiagnosticBootstrap;
            internal string DiagnosticNonce, DiagnosticPayloadHash;
            internal bool UsePersistent;
            internal bool RetirementComplete, RetirementDrained;
            internal int RetirementGeneration;
            internal int? RetirementExit;
            internal Action<AdministratorLaunch.BootstrapImage, string, uint, long> HostedLaunch;
            internal Func<bool> HostedTrusted;
            internal Action<uint, long> BeforeScheduledGrant;
            internal bool StartComplete, ImageVerified, NeverResumed, DirectStartAttempted;
            internal int Retired;
        }
        // Retain the authenticated process object with query-only access.
        // Framework Process.HasExited requests QUERY_INFORMATION, which a
        // medium process cannot obtain for an elevated helper.
        internal sealed class ProcessLifetime : IDisposable
        {
            readonly SafeWaitHandle handle;
            internal readonly uint Id;
            internal readonly long Started;
            ProcessLifetime(SafeWaitHandle value, uint pid, long started)
            { handle = value; Id = pid; Started = started; }
            internal static ProcessLifetime Open(uint pid, long started)
            {
                if (pid == 0 || started <= 0) throw new UnauthorizedAccessException();
                var value = OpenProcessLifetime(0x101000, false, pid);
                if (value == null || value.IsInvalid) {
                    int error = Marshal.GetLastWin32Error();
                    if (value != null) value.Dispose();
                    throw new Win32Exception(error);
                }
                try {
                    NativeProcessIdentity.FileTime creation, exit, kernel, user;
                    if (!GetProcessTimes(value, out creation, out exit, out kernel, out user))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (NativeProcessIdentity.CreationTicks(creation) != started) throw new UnauthorizedAccessException();
                    return new ProcessLifetime(value, pid, started);
                } catch { value.Dispose(); throw; }
            }
            internal bool HasExited { get { return WaitForExit(0); } }
            internal bool WaitForExit(int milliseconds)
            {
                uint result = WaitForSingleObject(handle, (uint)Math.Max(0, milliseconds));
                if (result == 0) return true;
                if (result == 258) return false;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            internal int ExitCode
            {
                get {
                    if (!HasExited) throw new InvalidOperationException("The owned helper has not exited.");
                    uint code;
                    if (!GetExitCodeProcess(handle, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return unchecked((int)code);
                }
            }
            public void Dispose() { handle.Dispose(); }
        }
        internal sealed class DeadlineWatch : IDisposable
        {
            readonly object sync = new object();
            readonly System.Threading.Timer timer;
            int armed = 1;
            bool disposed;
            internal DeadlineWatch(Action expired, int milliseconds, Action claimed = null, Action completed = null)
            {
                timer = new System.Threading.Timer(delegate {
                    if (Interlocked.CompareExchange(ref armed, 0, 1) != 1) return;
                    try {
                        if (claimed != null) claimed();
                        // Expiry and completion own the same fence. Once Dispose
                        // returns, even a callback that already claimed this timer
                        // cannot later close the next healthy pipe exchange.
                        lock (sync) {
                            if (disposed) return;
                            expired();
                        }
                    } finally { if (completed != null) completed(); }
                }, null, milliseconds, Timeout.Infinite);
            }
            public void Dispose() { lock (sync) { disposed = true; Interlocked.Exchange(ref armed, 0); timer.Dispose(); } }
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(uint process, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)] static extern SafeWaitHandle OpenProcessLifetime(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeWaitHandle process, out NativeProcessIdentity.FileTime creation,
            out NativeProcessIdentity.FileTime exit, out NativeProcessIdentity.FileTime kernel, out NativeProcessIdentity.FileTime user);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint code);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr value);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool PeekNamedPipe(IntPtr pipe, IntPtr buffer, uint count, IntPtr read, IntPtr available, IntPtr left);
        [DllImport("kernel32.dll", EntryPoint = "PeekNamedPipe", SetLastError = true)] static extern bool PeekNamedPipeAvailable(IntPtr pipe, IntPtr buffer, uint count, IntPtr read, out uint available, IntPtr left);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint process);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        static Peer ReadPeer(uint pid)
        {
            IntPtr process = IntPtr.Zero, token = IntPtr.Zero;
            try {
                var metadata = NativeProcessIdentity.Read(pid);
                uint session;
                if (metadata == null || metadata.Started <= 0 || !metadata.IntegrityAvailable || !metadata.ElevationAvailable ||
                    !ProcessIdToSessionId(pid, out session)) return null;
                process = OpenProcess(0x1000, false, pid);
                if (process == IntPtr.Zero || !OpenProcessToken(process, 8, out token)) return null;
                string sid;
                using (var identity = new WindowsIdentity(token)) sid = identity.User == null ? null : identity.User.Value;
                string path = NativeProcessIdentity.Path(pid);
                if (String.IsNullOrEmpty(sid) || String.IsNullOrEmpty(path)) return null;
                return new Peer { Process = pid, Started = metadata.Started, Session = session, Sid = sid, Path = path,
                    Integrity = metadata.IntegrityLevel, Elevated = metadata.Elevated };
            } catch (Exception) { return null; }
            finally { if (token != IntPtr.Zero) CloseHandle(token); if (process != IntPtr.Zero) CloseHandle(process); }
        }
        static bool SameSession(Peer parent, Peer other)
        { return parent != null && other != null && parent.Session == other.Session && parent.Sid == other.Sid; }
        static bool SamePeer(Peer expected, Peer actual)
        {
            return SameSession(expected, actual) && expected.Process == actual.Process && expected.Started == actual.Started &&
                String.Equals(expected.Path, actual.Path, StringComparison.OrdinalIgnoreCase);
        }
        static bool ParentAlive(Peer parent)
        { return parent != null && NativeProcessIdentity.Started(parent.Process) == parent.Started; }
        static bool Connected(PipeStream stream)
        {
            try { return stream != null && stream.IsConnected && !stream.SafePipeHandle.IsClosed &&
                PeekNamedPipe(stream.SafePipeHandle.DangerousGetHandle(), IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception) { return false; }
        }
        static bool Valid(Func<bool> predicate)
        { try { return predicate == null || predicate(); } catch (Exception) { return false; } }
        static WindowMoveWorker.Result Rejection(WindowMoveWorker.Outcome outcome = WindowMoveWorker.Outcome.Rejected)
        { return new WindowMoveWorker.Result { Outcome = outcome, CallerExited = true }; }
        internal void Enable()
        { lock (gate) { ResetReconnect(); BeginEnable(false, null, null, null); } }
        internal void EnablePersistent()
        { lock (gate) { ResetReconnect(); BeginEnable(true, null, null, null); } }
        void ClearReconnectRearm()
        { reconnectRearmGeneration = -1; reconnectRearmSession = null; reconnectRearmLease = null; }
        void ResetReconnect() { RefreshReconnectQuiescence(); reconnectCandidate = null; reconnectUsed = false; ClearReconnectRearm(); }
        void StageReconnectBudget(Lease lease, WindowMoveWorker.Result result, int activeWorkers, int uncertainWorkers, int token)
        {
            // Only completed, newly authorized work can prove that a recovered
            // session is useful. Admission, heartbeats and cancelled requests
            // cannot replenish a reconnect allowance or create a retry loop.
            if (!reconnectUsed || token != generation || token != reconnectRearmGeneration || disposed || !active || enabling ||
                uncertain || setupPending || permissionChangePending || launchSession == null ||
                !Object.ReferenceEquals(launchSession, reconnectRearmSession) || !launchSession.ImageVerified || launchSession.Retired != 0 ||
                lease == null || !Object.ReferenceEquals(operation, lease) || pendingOperations != 1 || !lease.Mutation || !lease.Authorized || lease.CancelRequested ||
                result == null || result.Outcome != WindowMoveWorker.Outcome.Success || !result.MutationAttempted || !result.CallerExited ||
                activeWorkers != 0 || uncertainWorkers != 0) return;
            reconnectRearmLease = lease;
        }
        void RefreshReconnectBudget()
        {
            var lease = reconnectRearmLease;
            if (!reconnectUsed || lease == null || !lease.Terminal || !lease.Complete || operation != null || pendingOperations != 0 ||
                pendingShutdowns != 0 || disposed || !active || enabling || uncertain || setupPending || permissionChangePending ||
                reconnectRearmGeneration != generation || launchSession == null ||
                !Object.ReferenceEquals(launchSession, reconnectRearmSession) || !launchSession.ImageVerified || launchSession.Retired != 0 ||
                WindowMoveWorker.UncertainHelpers != 0) return;
            // Native retirement of the old session was independently proved
            // before its saved reconnect. This session is intentionally live;
            // shutdownConfirmed describes inactive retirement, not this fence.
            reconnectUsed = false; ClearReconnectRearm();
        }
        bool ConfirmedReconnectRetirement(ReconnectCandidate candidate)
        {
            var session = candidate.Session;
            return session != null && session.ImageVerified && session.Retired == 1 && session.RetirementComplete &&
                session.RetirementGeneration == candidate.Generation && session.RetirementDrained && session.RetirementExit == 0;
        }
        void RefreshReconnectQuiescence()
        {
            // This latch is published only by normal independent retirement,
            // never by task absence or terminal-result publication. Keep the
            // callback count even after cancellation clears the operation slot,
            // so Disable/expiry cannot strand or prematurely clear recovery.
            if (retirementConfirmed && pendingOperations == 0 && operation == null && pendingShutdowns == 0 &&
                shutdownConfirmed && !active && !enabling && !setupPending && WindowMoveWorker.ActiveHelpers == 0 && WindowMoveWorker.UncertainHelpers == 0) {
                if (pinsMayRemain && pinRecovery.TryConfirmReleased()) { pinsMayRemain = false; pinRecovery.Reset(); }
                if (!pinsMayRemain) uncertain = false;
            }
        }
        bool TryTakeReconnect(bool allowed, bool ready)
        {
            var candidate = reconnectCandidate;
            if (!allowed || permissionChangePending) { reconnectCandidate = null; return false; }
            double age = candidate == null ? 0 : (Stopwatch.GetTimestamp() - candidate.Began) * 1000.0 / Stopwatch.Frequency;
            if (candidate != null && (age < 0 || age > 10000)) { reconnectCandidate = null; return false; }
            if (!ready || candidate == null || reconnectUsed || disposed || active || enabling || setupPending || pinsMayRemain ||
                candidate.Generation != generation || failureGeneration != generation || !candidate.Lease.Complete || pendingOperations != 0 || operation != null ||
                !ConfirmedReconnectRetirement(candidate) || pendingShutdowns != 0 || !shutdownConfirmed || uncertain ||
                WindowMoveWorker.UncertainHelpers != 0 || age < 250)
                return false;
            // Consume before any task/cache reads or launch attempt. This
            // reconnect is never a retry of the cancelled window mutation.
            reconnectUsed = true; reconnectCandidate = null;
            return true;
        }
        internal bool TryReconnectRemembered(bool allowed, bool ready)
        {
            if (Thread.CurrentThread.ManagedThreadId != controllerThread || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return false;
            lock (gate) {
                RefreshReconnectQuiescence();
                if (!TryTakeReconnect(allowed, ready)) return false;
                // Ordinary persistent admission revalidates the protected
                // controller, task, native image and peer with a fresh nonce.
                // No manual/UAC fallback and no original request is retained.
                int previousGeneration = generation;
                BeginEnable(true, null, null, null);
                if (generation != previousGeneration && enabling && launchSession != null && launchSession.UsePersistent) {
                    reconnectRearmGeneration = generation; reconnectRearmSession = launchSession; reconnectRearmLease = null;
                }
                return true;
            }
        }
        internal void EnableForHostedSponsor(Action<AdministratorLaunch.BootstrapImage, string, uint, long> launch, Func<bool> trusted)
        {
            if (!AppProfile.IsTest || launch == null || trusted == null || !trusted()) throw new UnauthorizedAccessException();
            lock (gate) { ResetReconnect(); BeginEnable(false, launch, trusted, null); }
        }
        internal void EnablePersistentForHosted(Action<uint, long> beforeGrant, Func<bool> trusted)
        {
            if (!AppProfile.IsTest || beforeGrant == null || trusted == null || !trusted()) throw new UnauthorizedAccessException();
            lock (gate) { ResetReconnect(); BeginEnable(true, null, trusted, beforeGrant); }
        }
        void BeginEnable(bool persistent, Action<AdministratorLaunch.BootstrapImage, string, uint, long> launch,
            Func<bool> trusted, Action<uint, long> beforeGrant)
        {
            NamedPipeServerStream created; int token;
            lock (gate) {
                if (disposed || active || enabling || pendingShutdowns != 0 || pendingOperations != 0 || operation != null) return;
                if (owner == null || uncertain || pinsMayRemain) { lastError = pinsMayRemain ? "Pinned administrator windows could not be confirmed restored. Close or unpin them, then try again." :
                    uncertain ? "Restart Taskbar Select after reviewing pending recovery." : "This Windows account could not be verified."; return; }
                if (owner.Elevated) { lastError = "Taskbar Select is already running as administrator."; return; }
                try {
                    string name = Prefix + Guid.NewGuid().ToString("N");
                    created = CreatePipe(name);
                    pipe = created; enabling = true; lastError = ""; token = ++generation;
                    if (persistent) savedApprovalAvailable = false;
                    var session = new LaunchSession { UsePersistent = persistent, HostedLaunch = launch,
                        HostedTrusted = trusted, BeforeScheduledGrant = beforeGrant, DiagnosticNonce = name.Substring(Prefix.Length) };
                    launchSession = session; shutdownConfirmed = retirementConfirmed = false;
                    session.Pipe = CreatePipe("TaskbarSelect.AdminBootstrap." + name.Substring(Prefix.Length));
                    session.Approval = CreatePipe("TaskbarSelect.AdminApproval." + name.Substring(Prefix.Length));
                    var launcher = new Thread(delegate() { Launch(created, name, token, session); });
                    launcher.IsBackground = true; launcher.SetApartmentState(ApartmentState.MTA); launcher.Name = "Administrator support approval"; launcher.Start();
                } catch (Exception) {
                    Close(pipe); pipe = null; enabling = false; lastError = "Administrator support could not be started.";
                    var failed = launchSession; launchSession = null;
                    if (failed != null) { lock (failed.Gate) failed.StartComplete = true; Retire(failed, generation); }
                }
            }
        }
        NamedPipeServerStream CreatePipe(string name)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(owner.Sid), PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512, security);
        }
        bool Current(int token)
        { lock (gate) return !disposed && generation == token && enabling; }
        internal sealed class ConnectionWait
        {
            readonly NamedPipeServerStream server;
            Exception error;
            internal volatile bool Complete;
            internal ConnectionWait(NamedPipeServerStream stream)
            {
                server = stream;
                server.BeginWaitForConnection(delegate(IAsyncResult result) {
                    try { server.EndWaitForConnection(result); }
                    catch (Exception failure) { error = failure; }
                    finally {
                        // Framework signals its event before invoking this
                        // user callback. Cancellation can make EndWait throw
                        // before its normal event cleanup, so this callback
                        // alone closes it after signaling has finished.
                        try { result.AsyncWaitHandle.Close(); }
                        catch (Exception failure) { if (error == null) error = failure; }
                        Complete = true;
                    }
                }, null);
            }
            internal void ThrowIfFailed()
            {
                if (!Complete) throw new InvalidOperationException("The pipe connection has not completed.");
                if (error != null) throw error;
            }
        }
        void Connect(NamedPipeServerStream server, Process process, int token, bool awaitingApproval = false)
        { ConnectCore(server, delegate { return process.HasExited; }, delegate { return process.ExitCode; }, token, awaitingApproval); }
        void ConnectPayload(NamedPipeServerStream server, ProcessLifetime process, int token)
        { ConnectCore(server, delegate { return process.HasExited; }, delegate { return process.ExitCode; }, token, false); }
        void ConnectCore(NamedPipeServerStream server, Func<bool> hasExited, Func<int> exitCode, int token, bool awaitingApproval)
        {
            // Framework owns the asynchronous result's event. Closing its
            // AsyncWaitHandle can race the IO callback's SetEvent, even after
            // IsCompleted publishes true. Observe our own callback completion;
            // it ends the operation and captures cancellation without throwing
            // on the IO-completion thread. Session retirement still owns server.
            var waiting = new ConnectionWait(server);
            var watch = Stopwatch.StartNew();
            while (!waiting.Complete) {
                if (!Current(token) || (!awaitingApproval && watch.ElapsedMilliseconds >= 10000)) throw new IOException();
                if (hasExited()) {
                    if (awaitingApproval && exitCode() == 111) throw new Win32Exception(1223);
                    throw new IOException();
                }
                Thread.Sleep(20);
            }
            waiting.ThrowIfFailed();
            if (!Current(token)) throw new IOException();
        }
        internal static bool ValidBootstrapHello(long[] values, uint parent, long parentStarted, uint native, long nativeStarted)
        { return values != null && values.Length == 4 && values[0] == parent && values[1] == parentStarted && values[2] == native && values[3] == nativeStarted; }
        internal static bool ValidBootstrapChild(long[] values)
        { return values != null && values.Length == 2 && values[0] > 0 && values[0] <= UInt32.MaxValue && values[1] > 0; }
        internal static bool ValidApprovalChild(long[] values, uint actual, long actualStarted)
        { return ValidBootstrapChild(values) && values[0] == actual && values[1] == actualStarted; }
        internal static bool BootstrapDrained(int exitCode, bool verifiedNativeImage)
        {
            // The verified medium approval host maps a confirmed HIGH drain
            // or no-child refusal to zero. 100..108, 111 (cancel), and 112
            // (namespace refusal) are before any elevated child is created.
            // Unknown exits, including 90, retain recovery.
            return verifiedNativeImage && (exitCode == 0 || (exitCode >= 100 && exitCode <= 108) || exitCode == 111 || exitCode == 112);
        }
        void Launch(NamedPipeServerStream server, string name, int token, LaunchSession session)
        {
            ProcessLifetime payload = null;
            string phase = "LauncherDirectory";
            try {
                string nonce = name.Substring(Prefix.Length);
                if (!Current(token)) return;
                if (session.UsePersistent || session.HostedLaunch != null) {
                    phase = session.UsePersistent ? "PersistentRegistration" : "HostedLauncherStage";
                    LaunchDirect(server, nonce, token, session, delegate(string next) { phase = next; });
                    return;
                }
                string launcherDirectory = AdministratorLaunch.LauncherDirectory(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.Directory, AppProfile.IsTest);
                phase = "LauncherStage";
                session.Image = AdministratorLaunch.Stage(launcherDirectory);
                if (!Current(token)) return;
                phase = "WindowsApproval";
                Process launched = session.Image.StartApprovalHost(owner.Process, owner.Started, nonce);
                // Publish completion only after retained-handle image proof.
                // Disable/exit may already be waiting to retire this session.
                lock (session.Gate) session.ApprovalHost = launched;
                if (launched == null) throw new InvalidOperationException();
                phase = "ApprovalHostIdentity";
                long hostStarted;
                session.ImageVerified = session.Image.VerifyBootstrapProcess(launched, out hostStarted);
                Peer host = ReadPeer((uint)launched.Id);
                if (!SameSession(owner, host) || host.Elevated || host.Integrity != 8192 ||
                    !session.ImageVerified || host.Started != hostStarted) throw new UnauthorizedAccessException();
                uint client;
                phase = "ApprovalHostPipe"; Connect(session.Approval, launched, token);
                if (!GetNamedPipeClientProcessId(session.Approval.SafePipeHandle.DangerousGetHandle(), out client) || client != host.Process ||
                    !SamePeer(host, ReadPeer(client))) throw new UnauthorizedAccessException();
                long[] highChild;
                using (var timeout = new DeadlineWatch(delegate { Close(session.Approval); }, 10000)) {
                    int hostKind, hostNumber;
                    long[] hostHello = Read(session.Approval, out hostKind, out hostNumber);
                    if (hostKind != 30 || hostNumber != 1 || !ValidBootstrapHello(hostHello, owner.Process, owner.Started, host.Process, host.Started) ||
                        !Current(token)) throw new UnauthorizedAccessException();
                    Write(session.Approval, 31, 1, new long[] { 1 });
                }
                // The user can take time deciding on UAC. Closing this owned
                // pipe cancels admission, including an approval received late.
                phase = "WindowsApproval";
                int approvalKind, approvalNumber;
                highChild = Read(session.Approval, out approvalKind, out approvalNumber);
                if (approvalKind != 32 || approvalNumber != 1 || !ValidBootstrapChild(highChild) || !Current(token))
                    throw new UnauthorizedAccessException();
                using (var timeout = new DeadlineWatch(delegate { Close(session.Approval); }, 10000)) {
                    Peer approved = ReadPeer((uint)highChild[0]);
                    if (!SameSession(owner, approved) || !approved.Elevated || approved.Integrity != 12288 ||
                        !ValidApprovalChild(highChild, approved.Process, approved.Started) ||
                        !session.Image.VerifyBootstrapProcess(approved.Process, approved.Started))
                        throw new UnauthorizedAccessException();
                    Write(session.Approval, 33, 1, new long[] { 1 });
                }
                phase = "BootstrapPipe"; Connect(session.Pipe, launched, token);
                phase = "BootstrapIdentity";
                if (!GetNamedPipeClientProcessId(session.Pipe.SafePipeHandle.DangerousGetHandle(), out client) || client != highChild[0])
                    throw new UnauthorizedAccessException();
                Peer elevated = ReadPeer(client);
                if (!SameSession(owner, elevated) || !elevated.Elevated || elevated.Integrity != 12288 ||
                    elevated.Started != highChild[1] || !session.Image.VerifyBootstrapProcess(client, elevated.Started) ||
                    !SamePeer(elevated, ReadPeer(client)))
                    throw new UnauthorizedAccessException();
                session.DiagnosticBootstrap = TryRetainDiagnosticProcess(client, elevated.Started);
                session.DiagnosticPayloadHash = session.Image.PayloadHash;
                using (var timeout = new DeadlineWatch(delegate { Close(session.Pipe); }, 10000)) {
                    int kind, number;
                    long[] hello = Read(session.Pipe, out kind, out number);
                    if (kind != 20 || number != 1 || !ValidBootstrapHello(hello, owner.Process, owner.Started, elevated.Process, elevated.Started) ||
                        !Current(token)) throw new UnauthorizedAccessException();
                    phase = "PayloadStage";
                    Write(session.Pipe, 21, 1, new long[] { 1 });
                    long[] child = Read(session.Pipe, out kind, out number);
                    if (kind != 22 || number != 1 || !ValidBootstrapChild(child) || !Current(token)) throw new UnauthorizedAccessException();
                    phase = "PayloadIdentity"; Peer childPeer = ReadPeer((uint)child[0]);
                    if (!SameSession(owner, childPeer) || childPeer.Started != child[1] || !childPeer.Elevated || childPeer.Integrity <= owner.Integrity ||
                        !AdministratorLaunch.VerifyPayload(childPeer.Path, session.Image.PayloadHash, nonce)) throw new UnauthorizedAccessException();
                    payload = ProcessLifetime.Open(childPeer.Process, childPeer.Started);
                    if (!SamePeer(childPeer, ReadPeer(payload.Id)) || !Current(token)) throw new UnauthorizedAccessException();
                    Write(session.Pipe, 23, 1, new long[] { 1 });
                    phase = "ManagedPipe"; ConnectPayload(server, payload, token);
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out client) || client != childPeer.Process ||
                        !SamePeer(childPeer, ReadPeer(client))) throw new UnauthorizedAccessException();
                }
                lock (gate) {
                    if (disposed || generation != token) return;
                    helper = payload; payload = null; active = true; enabling = false; focusSent = 0;
                    StartHeartbeat();
                }
                QueueFocus();
            } catch (AdministratorLaunch.ApprovalStartException error) {
                lock (session.Gate) {
                    session.PendingStartFailure = error; session.NeverResumed = error.NeverResumed;
                    session.ImageVerified = error.ImageVerified;
                }
                Fail(token, "Administrator support could not verify its suspended approval host.");
            }
            catch (Win32Exception error) { Fail(token, error.NativeErrorCode == 1223 ? "Administrator approval was cancelled." :
                "Administrator support could not be started. [" + phase + " win32=" + error.NativeErrorCode.ToString(Numbers) + "]" + PayloadExitEvidence(payload)); }
            catch (Exception error) {
                bool cancelled = false;
                try { cancelled = session.ApprovalHost != null && session.ApprovalHost.HasExited && session.ApprovalHost.ExitCode == 111; }
                catch (Exception) { }
                bool staging = (phase == "LauncherDirectory" || phase == "LauncherStage") && error is IOException;
                Fail(token, cancelled ? "Administrator approval was cancelled." : staging ?
                    "Administrator support could not stage its launcher. [" + phase + "] " + AdministratorLaunch.StageFailureDetail((IOException)error) :
                    "Administrator support could not authenticate its helper. [" + phase + " hr=0x" + error.HResult.ToString("X8", Numbers) + "]" + PayloadExitEvidence(payload));
            }
            finally {
                lock (session.Gate) session.StartComplete = true;
                Close(payload);
                bool keep;
                lock (gate) { keep = generation == token && active && Object.ReferenceEquals(launchSession, session); if (!keep) Close(server); }
                if (!keep) Retire(session, token);
            }
        }
        internal static string PayloadExitEvidence(ProcessLifetime payload)
        {
            try { return payload != null && payload.HasExited ? " payloadExit=" + payload.ExitCode.ToString(Numbers) : ""; }
            catch (InvalidOperationException) { return ""; }
            catch (Win32Exception) { return ""; }
        }
        void LaunchDirect(NamedPipeServerStream server, string nonce, int token, LaunchSession session, Action<string> phase)
        {
            ProcessLifetime payload = null;
            try {
                string payloadHash;
                if (session.UsePersistent) {
                    phase("PersistentRegistration");
                    session.PersistentImage = PersistentAdministrator.Inspect(PersistentAdministrator.ScopeForCurrentProfile());
                    if (!session.PersistentImage.Available) throw new UnauthorizedAccessException(session.PersistentImage.Reason);
                    if (!Current(token) || !Valid(session.HostedTrusted)) throw new IOException();
                    lock (gate) { if (disposed || generation != token || !enabling) throw new IOException(); savedApprovalAvailable = true; }
                    payloadHash = session.PersistentImage.PayloadHash;
                    session.DirectStartAttempted = true;
                    phase("PersistentTaskRun");
                    session.PersistentImage.Run(nonce, owner.Process, owner.Started);
                } else {
                    phase("HostedLauncherStage");
                    if (!AppProfile.IsTest || session.HostedLaunch == null || session.HostedTrusted == null || !Valid(session.HostedTrusted))
                        throw new UnauthorizedAccessException();
                    session.Image = AdministratorLaunch.Stage(AdministratorLaunch.LauncherDirectory(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.Directory, true));
                    payloadHash = session.Image.PayloadHash;
                    session.DirectStartAttempted = true;
                    phase("HostedLauncherRun");
                    session.HostedLaunch(session.Image, nonce, owner.Process, owner.Started);
                }
                phase("BootstrapConnect");
                ConnectCore(session.Pipe, delegate { return !Valid(session.HostedTrusted); }, delegate { return 0; }, token, false);
                phase("BootstrapIdentity");
                uint client;
                if (!GetNamedPipeClientProcessId(session.Pipe.SafePipeHandle.DangerousGetHandle(), out client)) throw new UnauthorizedAccessException();
                Peer elevated = ReadPeer(client);
                if (!SameSession(owner, elevated) || !elevated.Elevated || elevated.Integrity != 12288 ||
                    !(session.UsePersistent ? session.PersistentImage.VerifyBootstrapProcess(client, elevated.Started) : session.Image.VerifyBootstrapProcess(client, elevated.Started)) ||
                    !SamePeer(elevated, ReadPeer(client))) throw new UnauthorizedAccessException();
                session.ScheduledBootstrap = ProcessLifetime.Open(client, elevated.Started);
                session.DiagnosticBootstrap = session.ScheduledBootstrap;
                session.DiagnosticPayloadHash = payloadHash;
                session.ImageVerified = true;
                using (var timeout = new DeadlineWatch(delegate { Close(session.Pipe); }, 10000)) {
                    phase("BootstrapHello");
                    int kind, number;
                    long[] hello = Read(session.Pipe, out kind, out number);
                    if (kind != 20 || number != 1 || !ValidBootstrapHello(hello, owner.Process, owner.Started, client, elevated.Started) ||
                        !Current(token) || !Valid(session.HostedTrusted)) throw new UnauthorizedAccessException();
                    if (session.BeforeScheduledGrant != null) {
                        phase("HostedBootstrapSupervision");
                        if (!AppProfile.IsTest || session.HostedTrusted == null) throw new UnauthorizedAccessException();
                        session.BeforeScheduledGrant(client, elevated.Started);
                        if (!Current(token) || !Valid(session.HostedTrusted)) throw new UnauthorizedAccessException();
                    }
                    phase("BootstrapGrant");
                    Write(session.Pipe, 21, 1, new long[] { 1 });
                    long[] child = Read(session.Pipe, out kind, out number);
                    if (kind != 22 || number != 1 || !ValidBootstrapChild(child) || !Current(token)) throw new UnauthorizedAccessException();
                    phase("PayloadIdentity");
                    Peer childPeer = ReadPeer((uint)child[0]);
                    if (!SameSession(owner, childPeer) || childPeer.Started != child[1] || !childPeer.Elevated || childPeer.Integrity <= owner.Integrity ||
                        !AdministratorLaunch.VerifyPayload(childPeer.Path, payloadHash, nonce)) throw new UnauthorizedAccessException();
                    payload = ProcessLifetime.Open(childPeer.Process, childPeer.Started);
                    if (!SamePeer(childPeer, ReadPeer(payload.Id)) || !Current(token)) throw new UnauthorizedAccessException();
                    phase("PayloadResume");
                    Write(session.Pipe, 23, 1, new long[] { 1 });
                    phase("PayloadConnect");
                    ConnectPayload(server, payload, token);
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out client) || client != childPeer.Process ||
                        !SamePeer(childPeer, ReadPeer(client))) throw new UnauthorizedAccessException();
                }
                lock (gate) {
                    if (disposed || generation != token) return;
                    helper = payload; payload = null; active = true; enabling = false; focusSent = 0;
                    StartHeartbeat();
                }
                QueueFocus();
            } finally { Close(payload); }
        }
        void Retire(LaunchSession session, int token)
        {
            if (session == null || Interlocked.Exchange(ref session.Retired, 1) != 0) return;
            lock (gate) { session.RetirementGeneration = token; pendingShutdowns++; uncertain = true; shutdownConfirmed = false; }
            Close(session.Pipe); Close(session.Approval);
            ThreadPool.QueueUserWorkItem(delegate {
                bool drained = false;
                int? nativeExit = null;
                try {
                    // UAC can still be pending in the verified native approval
                    // host. Retain its pinned image through that whole lifetime.
                    while (true) { lock (session.Gate) if (session.StartComplete) break; Thread.Sleep(50); }
                    Process process = session.ApprovalHost;
                    var startFailure = session.PendingStartFailure;
                    if (startFailure != null) {
                        // Even failed managed Process construction can leave a
                        // suspended native child. Keep its original raw handle.
                        while (!startFailure.WaitForExit(1000)) { }
                        nativeExit = startFailure.ExitCode;
                        drained = session.NeverResumed || BootstrapDrained(nativeExit.Value, session.ImageVerified);
                    }
                    else if (session.UsePersistent || session.HostedLaunch != null) {
                        var native = session.ScheduledBootstrap;
                        if (native != null) {
                            while (!native.WaitForExit(1000)) { }
                            nativeExit = native.ExitCode;
                            drained = session.ImageVerified && (nativeExit == 0 || (nativeExit >= 100 && nativeExit <= 108));
                        } else drained = !session.DirectStartAttempted || (session.UsePersistent && session.PersistentImage != null &&
                            !session.PersistentImage.TaskStartAttempted && session.PersistentImage.WaitForTaskDrain(0));
                        if (session.UsePersistent) {
                            if (drained && session.PersistentImage != null) drained = session.PersistentImage.WaitForTaskDrain(5000);
                        }
                    }
                    else if (process == null) drained = true;
                    else if (process.WaitForExit(5000)) { nativeExit = process.ExitCode; drained = session.NeverResumed || BootstrapDrained(nativeExit.Value, session.ImageVerified); }
                    // Do not kill an approval host while UAC or its HIGH child
                    // is pending. Retain the image lease and fail closed.
                    else { while (!process.WaitForExit(1000)) { } nativeExit = process.ExitCode; drained = session.NeverResumed || BootstrapDrained(nativeExit.Value, session.ImageVerified); }
                } catch (Exception) { }
                finally {
                    Close(session.ApprovalHost); Close(session.PendingStartFailure); Close(session.Pipe); Close(session.Approval);
                    if (drained) {
                        if (!Object.ReferenceEquals(session.DiagnosticBootstrap, session.ScheduledBootstrap)) Close(session.DiagnosticBootstrap);
                        Close(session.ScheduledBootstrap); Close(session.Image); Close(session.PersistentImage);
                    }
                    else RetainUnconfirmedSession(session);
                    lock (gate) {
                        session.RetirementComplete = true; session.RetirementDrained = drained; session.RetirementExit = nativeExit;
                        pendingShutdowns--;
                        if (drained && pendingShutdowns == 0 && !active) shutdownConfirmed = retirementConfirmed = true;
                        if (generation == token && failureGeneration == token && nativeExit.HasValue) lastError += " bootstrapExit=" + nativeExit.Value.ToString(Numbers);
                        if (!drained && generation == token) lastError += " bootstrapDrain=unconfirmed";
                        if (lastIncidentGeneration == token && nativeExit.HasValue) lastIncident += " bootstrapExit=" + nativeExit.Value.ToString(Numbers);
                        if (lastIncidentGeneration == token && !drained) lastIncident += " bootstrapDrain=unconfirmed";
                        RefreshReconnectQuiescence();
                    }
                    if (!drained && nativeExit == 90) QueueNativeStopDiagnostic(session, token);
                }
            });
        }
        internal static ProcessLifetime TryRetainDiagnosticProcess(uint pid, long started)
        {
            try { return ProcessLifetime.Open(pid, started); }
            catch (Win32Exception) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        void QueueNativeStopDiagnostic(LaunchSession session, int token)
        {
            // Evidence is optional and has no authority over retirement. The
            // retained actual HIGH process must have exited before reading its
            // fixed protected record. Filesystem I/O stays off the UI and the
            // required drain/lease path, and can never make exit 90 successful.
            uint native; long started;
            try {
                var process = session.DiagnosticBootstrap;
                if (owner == null || !session.ImageVerified || process == null || !process.HasExited || process.ExitCode != 90 ||
                    !AdministratorLaunch.ValidHash(session.DiagnosticPayloadHash) || !ValidToken(session.DiagnosticNonce)) return;
                native = process.Id; started = process.Started;
            } catch (Exception) { return; }
            ThreadPool.QueueUserWorkItem(delegate {
                lock (gate) if (disposed || generation != token || failureGeneration != token) return;
                string evidence;
                try {
                    byte[] bytes = AdministratorLaunch.ReadNativeStopDiagnostic(session.DiagnosticPayloadHash, session.DiagnosticNonce);
                    evidence = NativeStopDiagnostics.Parse(bytes, session.DiagnosticPayloadHash, session.DiagnosticNonce,
                        native, started, owner.Process, owner.Started).Summary();
                } catch (Exception error) { evidence = "nativeDiagnostic=unavailable hr=0x" + error.HResult.ToString("X8", Numbers); }
                lock (gate) if (!disposed && generation == token && failureGeneration == token) lastError += " " + evidence;
            });
        }
        // Unconfirmed task launches keep their immutable image handles alive;
        // dropping the session would silently release a security lease.
        static readonly System.Collections.Generic.List<LaunchSession> unconfirmedSessions = new System.Collections.Generic.List<LaunchSession>();
        static void RetainUnconfirmedSession(LaunchSession session)
        { lock (unconfirmedSessions) unconfirmedSessions.Add(session); }
        void Fail(int token, string message)
        {
            lock (gate) {
                if (generation != token) return;
                if (pinsMayRemain || (operation != null && operation.Authorized && operation.Mutation)) uncertain = true;
                // Closing a timed-out pipe wakes its other readers with EOF.
                // Their later catches must not erase the cause that closed it.
                if (failureGeneration == token) return;
                failureGeneration = token;
                ClearReconnectRearm();
                int? exit = HelperExitCode();
                active = enabling = false; lastError = message + (exit.HasValue ? " helperExit=" + exit.Value.ToString(Numbers) : "");
                if (reconnectCandidate != null && reconnectCandidate.Generation == token &&
                    Object.ReferenceEquals(reconnectCandidate.Lease, operation) && Object.ReferenceEquals(reconnectCandidate.Session, launchSession)) {
                    lastIncident = lastError; lastIncidentGeneration = token;
                }
                heartbeat.Stop();
                Close(pipe); pipe = null;
                ProcessLifetime stopped = helper; helper = null;
                LaunchSession session = launchSession; launchSession = null;
                Retire(session, token);
                if (stopped != null && !exit.HasValue) CollectHelperExit(stopped, token);
                else Close(stopped);
            }
        }
        int? HelperExitCode()
        {
            try { return helper != null && helper.HasExited ? (int?)helper.ExitCode : null; }
            catch (InvalidOperationException) { return null; }
            catch (Win32Exception) { return null; }
        }
        void CollectHelperExit(ProcessLifetime stopped, int token)
        {
            // EOF precedes the helper's next STA shutdown tick. Retain only
            // this exact retained query-only handle for a bounded exit read;
            // never wait on the UI or replace the first failure evidence.
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    if (stopped.WaitForExit(1000)) {
                        int code = stopped.ExitCode;
                        lock (gate) if (generation == token && failureGeneration == token)
                            lastError += " helperExit=" + code.ToString(Numbers);
                        lock (gate) if (lastIncidentGeneration == token) lastIncident += " helperExit=" + code.ToString(Numbers);
                    }
                } catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                finally { Close(stopped); }
            });
        }
        static string FailureEvidence(string message, string phase, int number, int op, long began, Exception error, bool deadline)
        {
            int elapsed = began == 0 ? 0 : (int)Math.Min(9999, Math.Max(0, (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency));
            return message + " [" + phase + " seq=" + number.ToString(Numbers) + " op=" + op.ToString(Numbers) +
                " hr=0x" + (error == null ? 0 : error.HResult).ToString("X8", Numbers) + " deadline=" + (deadline ? "1" : "0") +
                " elapsed=" + elapsed.ToString(Numbers) + "ms]";
        }
        internal void Disable()
        {
            lock (gate) {
                RefreshReconnectQuiescence();
                if (disablePending) throw new InvalidOperationException("Administrator support is already stopping.");
                disablePending = true;
            }
            try {
                // Closing the bootstrap channel drains its job immediately. The
                // payload must acknowledge restoration before that channel closes.
                if (!ReleaseOwnedPins(2000)) throw new InvalidOperationException("Pinned administrator windows could not be confirmed restored. Close or unpin them, then try again.");
                lock (gate) {
                    reconnectCandidate = null;
                    ClearReconnectRearm();
                    generation++; active = enabling = false;
                    heartbeat.Stop();
                    if (operation != null && operation.Authorized && operation.Mutation) uncertain = true;
                    Close(pipe); pipe = null; Close(helper); helper = null;
                    LaunchSession session = launchSession; launchSession = null; Retire(session, generation);
                    focus = new[] { 0, 0, 1, 0, -1, 0, 1 };
                }
            } finally { lock (gate) disablePending = false; }
        }
        internal static bool ExitQuiescent(int pending, bool confirmed, bool uncertainOperation, bool operationPending)
        { return pending == 0 && confirmed && !uncertainOperation && !operationPending; }
        internal bool StopForExit(int milliseconds)
        {
            try { Disable(); } catch (InvalidOperationException) { return false; }
            var watch = Stopwatch.StartNew();
            do {
                lock (gate) {
                    bool pending = pendingOperations != 0 || (operation != null && operation.Authorized && !operation.Complete);
                    if (shutdownConfirmed && pendingShutdowns == 0 && !pending && !active && !pinsMayRemain) uncertain = false;
                    if (ExitQuiescent(pendingShutdowns, shutdownConfirmed, uncertain, pending)) return true;
                }
                Application.DoEvents(); Thread.Sleep(5);
            } while (watch.ElapsedMilliseconds < Math.Max(0, Math.Min(5000, milliseconds)));
            return false;
        }
        internal static bool ValidPinReleaseRequest(long[] values, long now)
        { return values != null && values.Length == 1 && values[0] > now && values[0] - now <= 2 * Stopwatch.Frequency; }
        internal static bool ValidPinReleaseReply(long[] values)
        {
            return values != null && values.Length == 4 && Boolean(values[0]) && values[1] >= 0 && values[1] <= OwnedWindowPins.Limit &&
                values[2] >= 0 && values[2] <= 2 && values[3] >= 0 && values[3] <= values[2] &&
                (values[0] == 0 || (values[1] == 0 && values[2] == 0 && values[3] == 0));
        }
        internal static bool ValidPinReleaseAuthorization(int kind, int number, int expected, long[] values)
        { return kind == ReleaseAuthorize && number == expected && values != null && values.Length == 1 && values[0] == 1; }
        internal static long[] ReleasePinValues(OwnedWindowPins pins, long deadline)
        {
            if (pins == null) throw new ArgumentNullException("pins");
            bool restored = pins.ReleaseAll(deadline);
            int owned = pins.Count, workers = WindowMoveWorker.ActiveHelpers, pending = WindowMoveWorker.UncertainHelpers;
            bool confirmed = restored && owned == 0 && workers == 0 && pending == 0;
            return new long[] { confirmed ? 1 : 0, owned, workers, pending };
        }
        bool ReleaseOwnedPins(int milliseconds)
        {
            int token;
            long deadline = Stopwatch.GetTimestamp() + Math.Max(1, Math.Min(2000, milliseconds)) * Stopwatch.Frequency / 1000;
            lock (gate) {
                if (!pinsMayRemain) return true;
                if (disposed || !active || pinReleasePending || setupPending || pendingOperations != 0 || operation != null) return false;
                pinReleasePending = true; token = generation;
            }
            bool entered = false;
            try {
                while (!(entered = Monitor.TryEnter(ioGate))) {
                    if (Stopwatch.GetTimestamp() >= deadline) return false;
                    Application.DoEvents(); Thread.Sleep(2);
                }
                PipeStream stream; int number;
                lock (gate) {
                    if (!active || disposed || generation != token || pendingOperations != 0 || operation != null || Stopwatch.GetTimestamp() >= deadline) return false;
                    stream = pipe; number = NextSequence();
                }
                int due = Math.Max(1, (int)Math.Ceiling((deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency));
                using (var timeout = new DeadlineWatch(delegate { Fail(token, "Administrator pin cleanup was not confirmed. Recovery was retained."); }, due)) {
                    Write(stream, ReleasePins, number, new long[] { deadline });
                    int kind, id; long[] reply = Read(stream, out kind, out id);
                    if (kind != ReleaseReady || id != number || reply.Length != 0) throw new IOException();
                    lock (gate) if (!active || disposed || generation != token || Stopwatch.GetTimestamp() >= deadline) throw new IOException();
                    Write(stream, ReleaseAuthorize, number, new long[] { 1 });
                    reply = Read(stream, out kind, out id);
                    if (kind != ReleaseReply || id != number || !ValidPinReleaseReply(reply)) throw new IOException();
                    lock (gate) {
                        if (!active || disposed || generation != token || Stopwatch.GetTimestamp() >= deadline) throw new IOException();
                        if (reply[0] == 1) { pinsMayRemain = false; pinRecovery.Reset(); return true; }
                        lastError = "Pinned administrator windows have not confirmed restoration. Try switching off again.";
                        return false;
                    }
                }
            } catch (Exception) { Fail(token, "Administrator pin cleanup disconnected. Recovery was retained."); return false; }
            finally {
                if (entered) Monitor.Exit(ioGate);
                lock (gate) pinReleasePending = false;
                QueueFocus();
            }
        }
        internal void Poll()
        {
            int token; bool exited = false;
            lock (gate) {
                if (disposed || !active) return;
                token = generation;
                try { exited = helper == null || helper.HasExited; } catch (Exception) { exited = true; }
            }
            if (exited) Fail(token, "Administrator support disconnected. Enable it again when needed.");
            else QueueFocus();
        }
        internal void UpdateFocus(int style, string accent, bool paused, bool glow, int lighting, int finish, bool pinsPaused)
        {
            int index = Array.IndexOf(FocusAppearance.Presets, FocusAppearance.NormalizeAccent(accent));
            bool permissionPending = PermissionChangePending;
            int[] next = { (int)FocusAppearance.NormalizeStyle(style), Math.Max(0, index), paused || permissionPending ? 1 : 0, glow ? 1 : 0,
                FocusAppearance.NormalizeLighting(lighting), (int)FocusAppearance.NormalizeFinish(finish), pinsPaused || permissionPending ? 1 : 0 };
            lock (gate) if (!focus.SequenceEqual(next)) { focus = next; focusRevision++; focusSent = 0; }
            QueueFocus();
        }
        internal void HideFocus()
        { lock (gate) { focus = (int[])focus.Clone(); focus[2] = 1; focusRevision++; focusSent = 0; } QueueFocus(); }
        internal void HidePins()
        { lock (gate) { focus = (int[])focus.Clone(); focus[6] = 1; focusRevision++; focusSent = 0; } QueueFocus(); }
        void QueueFocus()
        {
            int token;
            lock (gate) {
                if (disposed || !active || focusQueued || setupPending || pinReleasePending || pendingOperations != 0 || operation != null ||
                    (focusSent != 0 && (Stopwatch.GetTimestamp() - focusSent) * 1000.0 / Stopwatch.Frequency < 250)) return;
                focusQueued = true; token = generation;
            }
            ThreadPool.QueueUserWorkItem(delegate {
                string phase = "FocusAdmission"; int number = 0; long began = Stopwatch.GetTimestamp();
                var ioPhase = new FocusExchangePhase();
                try {
                    lock (ioGate) {
                        PipeStream stream; int[] config; int sentRevision;
                        lock (gate) {
                            if (!active || generation != token || setupPending || pinReleasePending || pendingOperations != 0 || operation != null) return;
                            stream = pipe; config = (int[])focus.Clone(); number = NextSequence(); sentRevision = focusRevision;
                        }
                        using (var timeout = new DeadlineWatch(delegate {
                            string diagnostic = "";
                            try { diagnostic = FocusDeadlineDiagnostic(token, stream, ioPhase); }
                            catch (Exception error) { diagnostic = " diagnosticHR=0x" + error.HResult.ToString("X8", Numbers); }
                            finally { Fail(token, FailureEvidence("Administrator support did not respond.", "FocusDeadline", number, -1, began, null, true) + diagnostic); }
                        }, 2000)) {
                            phase = "FocusWrite";
                            ioPhase.Set(FocusIoPhase.Write);
                            Write(stream, Focus, number, config);
                            phase = "FocusRead";
                            ioPhase.Set(FocusIoPhase.Read);
                            int kind, id; long[] values = Read(stream, out kind, out id, ioPhase);
                            phase = "FocusReply";
                            ioPhase.Set(FocusIoPhase.Reply);
                            if (kind != FocusReply || id != number || values.Length != 2 || values[0] < 0 || values[1] < 0) throw new IOException();
                            lock (gate) {
                                if (generation == token && active && !disposed && failureGeneration != token) {
                                    focusSent = focusRevision == sentRevision ? Stopwatch.GetTimestamp() : 0;
                                    if (values[0] == 0 && values[1] == 0 && pendingShutdowns == 0) uncertain = false;
                                }
                            }
                            phase = "SnapInputExchange";
                            ExchangeSnapInput(stream, token, ioPhase);
                        }
                    }
                } catch (Exception error) { Fail(token, FailureEvidence("Administrator support disconnected. Pending recovery was retained.", phase, number, -1, began, error, false)); }
                finally { lock (gate) focusQueued = false; QueueFocus(); }
            });
        }
        // Allocate a new exchange number only while holding ioGate, in the
        // same order that exchanges reach the wire. gate protects publication.
        int NextSequence() { sequence = sequence == Int32.MaxValue ? 1 : sequence + 1; return sequence; }

        internal WindowMoveWorker.Result Execute(int op, IntPtr window, uint pid, long started, long deadline, Func<bool> stillValid, int[] values)
        {
            if (deadline <= Stopwatch.GetTimestamp()) return Rejection(WindowMoveWorker.Outcome.TimedOut);
            if (!Valid(stillValid)) return Rejection(WindowMoveWorker.Outcome.Cancelled);
            if (!ValidValues(op, window.ToInt64(), pid, started, values) || WindowMoveWorker.UncertainHelpers != 0) return Rejection();
            Lease lease; int token;
            lock (gate) {
                if (disposed || !active || uncertain || permissionChangePending || disablePending || setupPending || pinReleasePending || pendingOperations != 0 || operation != null) return Rejection();
                operation = lease = new Lease { Mutation = op != 3, Began = Stopwatch.GetTimestamp() }; pendingOperations++; token = generation;
            }
            var request = new long[5 + values.Length];
            request[0] = op; request[1] = window.ToInt64(); request[2] = pid; request[3] = started; request[4] = deadline;
            for (int i = 0; i < values.Length; i++) request[5 + i] = values[i];
            ThreadPool.QueueUserWorkItem(delegate {
                string phase = "RequestAdmission"; long began = Stopwatch.GetTimestamp();
                try {
                    lock (ioGate) {
                        PipeStream stream;
                        lock (gate) {
                            if (!active || generation != token) { lease.Result = Rejection(); return; }
                            stream = pipe; lease.Sequence = NextSequence();
                        }
                        phase = "RequestWrite"; Write(stream, Request, lease.Sequence, request);
                        phase = "RequestReadyRead";
                        int kind, id; long[] reply = Read(stream, out kind, out id);
                        phase = "RequestReadyReply";
                        if (kind == ResultReply && id == lease.Sequence) {
                            WindowMoveWorker.Result rejected; int workers, pending, owned;
                            if (!ParseOperationResult(op, reply, out rejected, out workers, out pending, out owned) || !PreauthorizationRejection(rejected) || workers != 0 || pending != 0) throw new IOException();
                            PublishOperationTerminal(lease, rejected, pending, owned, token);
                            return;
                        }
                        if (kind != Ready || id != lease.Sequence || reply.Length != 0) throw new IOException();
                        lease.Ready = true;
                        phase = "MainAuthorization";
                        while (!lease.Authorized && !lease.CancelRequested) {
                            // READY is still unread by the helper until one
                            // authorization decision is exchanged. Expiry must
                            // retire the pipe rather than publish callback-only
                            // completion and accidentally reuse that channel.
                            if (deadline <= Stopwatch.GetTimestamp()) throw new TimeoutException();
                            lock (gate) if (!active || generation != token) { lease.Result = Rejection(WindowMoveWorker.Outcome.Cancelled); return; }
                            Thread.Sleep(2);
                        }
                        bool negative;
                        lock (gate) {
                            if (disposed || !active || generation != token || deadline <= Stopwatch.GetTimestamp()) throw new IOException();
                            negative = lease.CancelRequested;
                            if (negative && lease.Authorized) throw new IOException();
                        }
                        if (negative) {
                            phase = "CancelAuthorizeWrite"; Write(stream, Authorize, lease.Sequence, new long[] { 0 });
                            phase = "CancelResultRead";
                            reply = Read(stream, out kind, out id);
                            phase = "CancelResultReply";
                            int workers, pending, owned; WindowMoveWorker.Result cancelled;
                            if (kind != ResultReply || id != lease.Sequence || !ParseOperationResult(op, reply, out cancelled, out workers, out pending, out owned) ||
                                !PreauthorizationCancellation(cancelled, workers, pending)) throw new IOException();
                            lock (gate) {
                                if (disposed || !active || generation != token || lease.Authorized || !lease.CancelRequested || deadline <= Stopwatch.GetTimestamp()) throw new IOException();
                                PublishOperationTerminal(lease, cancelled, 0, owned, token);
                            }
                            return;
                        }
                        if (op == 2 && helper != null) AllowSetForegroundWindow((uint)helper.Id);
                        phase = "AuthorizeWrite"; Write(stream, Authorize, lease.Sequence, new long[] { 1 });
                        phase = "ResultRead";
                        reply = Read(stream, out kind, out id);
                        phase = "ResultReply";
                        int activeWorkers, uncertainWorkers, ownedPins; WindowMoveWorker.Result completed;
                        if (kind != ResultReply || id != lease.Sequence || !ParseOperationResult(op, reply, out completed, out activeWorkers, out uncertainWorkers, out ownedPins) ||
                            (!lease.Mutation && completed.MutationAttempted)) throw new IOException();
                        lock (gate) {
                            StageReconnectBudget(lease, completed, activeWorkers, uncertainWorkers, token);
                            PublishOperationTerminal(lease, completed, uncertainWorkers, ownedPins, token);
                        }
                    }
                } catch (Exception error) {
                    lease.Result = new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Failed,
                        MutationAttempted = lease.Mutation && lease.Authorized, CallerExited = !lease.Authorized };
                    Fail(token, FailureEvidence("Administrator support disconnected. Pending recovery was retained.", phase, lease.Sequence, op, began, error, deadline <= Stopwatch.GetTimestamp()));
                } finally { lock (gate) { lease.Complete = true; pendingOperations--; RefreshReconnectQuiescence(); RefreshReconnectBudget(); } }
            });
            try {
                while (!lease.Complete) {
                    bool expired = deadline <= Stopwatch.GetTimestamp();
                    bool cancelled = !expired && !lease.CancelRequested && !Valid(stillValid);
                    if (expired || cancelled) {
                        int reason = cancelled ? ValidityReason(stillValid) : 0;
                        lock (gate) {
                            // Validity can change while its callback runs. A
                            // terminal reply that won this publication fence
                            // keeps its actual outcome, including uncertainty.
                            if (lease.Terminal || lease.Complete) return lease.Result ?? Rejection();
                            if (cancelled && !lease.Authorized && active && !disposed && generation == token) {
                                // Consume READY and a same-sequence negative
                                // authorization receipt before reusing this pipe.
                                lease.CancelRequested = true;
                            } else {
                                if (cancelled && !reconnectUsed && generation == token && failureGeneration != token && active && launchSession != null)
                                    reconnectCandidate = new ReconnectCandidate { Generation = token, Lease = lease, Session = launchSession, Began = Stopwatch.GetTimestamp() };
                                Fail(token, FailureEvidence("Administrator operation was cancelled; pending recovery was retained.", expired ? "MainDeadline" : "MainValidity", lease.Sequence, op,
                                    lease.Began, null, expired) + OperationEvidence(lease, reason));
                                return new WindowMoveWorker.Result { Outcome = expired ? WindowMoveWorker.Outcome.TimedOut : WindowMoveWorker.Outcome.Cancelled,
                                    MutationAttempted = lease.Mutation && lease.Authorized, CallerExited = !lease.Authorized };
                            }
                        }
                    }
                    if (lease.Ready && !lease.Authorized && !lease.CancelRequested) {
                        // Main-thread admission is deliberately repeated after
                        // READY, immediately before the helper may launch USER.
                        bool allowed = deadline > Stopwatch.GetTimestamp() && Valid(stillValid);
                        lock (gate) {
                            if (!disposed && active && generation == token && !lease.Terminal && !lease.Complete && !lease.Authorized && !lease.CancelRequested && deadline > Stopwatch.GetTimestamp()) {
                                bool pinRetained = true;
                                if (allowed && !disablePending && !permissionChangePending && !pinReleasePending && op == 6 && values[0] == 1) {
                                    try {
                                        var snapshot = readPinAuthorization(window);
                                        pinRetained = WindowStatePolicy.Eligible(snapshot, delegate { return false; }) && snapshot.Window == window &&
                                            SnapshotLifetime(snapshot, pid, started) && !WindowStatePolicy.Topmost(snapshot) && pinRecovery.Remember(snapshot);
                                    } catch (Exception) { pinRetained = false; }
                                }
                                if (allowed && !disablePending && !permissionChangePending && !pinReleasePending && pinRetained) { if (op == 6 && values[0] == 1) pinsMayRemain = true; lease.Authorized = true; }
                                else lease.CancelRequested = true;
                            }
                        }
                    }
                    Application.DoEvents(); Thread.Sleep(2);
                }
                return lease.Result ?? Rejection();
            } finally { lock (gate) { if (Object.ReferenceEquals(operation, lease)) operation = null; RefreshReconnectQuiescence(); RefreshReconnectBudget(); } }
        }
        internal static bool Boolean(long value) { return value == 0 || value == 1; }
        internal bool InstallPersistent(string scope, bool isTest) { return SetupPersistent(1, scope, isTest); }
        internal bool RemovePersistent(string scope, bool isTest) { return SetupPersistent(2, scope, isTest); }
        bool SetupPersistent(int action, string scope, bool isTest)
        {
            if (scope != PersistentAdministrator.ScopeForCurrentProfile() || isTest != AppProfile.IsTest) throw new UnauthorizedAccessException();
            int token, number; string nativeHash;
            lock (gate) {
                if (!active || disposed || uncertain || setupPending || pinReleasePending || pendingOperations != 0 || operation != null || launchSession == null ||
                    launchSession.UsePersistent || launchSession.Image == null) return false;
                nativeHash = launchSession.Image.BootstrapHash;
                token = generation; setupPending = true;
            }
            bool authorized = false;
            try {
                lock (ioGate) {
                    PipeStream stream;
                    lock (gate) { if (!active || generation != token) return false; stream = pipe; number = NextSequence(); }
                    using (var timeout = new DeadlineWatch(delegate { Fail(token, "Persistent administrator setup did not finish. Its registration was retained for review."); }, 30000)) {
                        Write(stream, PersistentSetup, number, SetupValues(action, isTest, scope, nativeHash));
                        int kind, id; long[] reply = Read(stream, out kind, out id);
                        if (kind != SetupReady || id != number || reply.Length != 0) throw new IOException();
                        lock (gate) {
                            if (!active || generation != token) throw new IOException();
                            authorized = true; savedApprovalAvailable = false;
                        }
                        Write(stream, SetupAuthorize, number, new long[] { 1 });
                        reply = Read(stream, out kind, out id);
                        if (kind != SetupReply || id != number || !ValidSetupReply(reply)) throw new IOException();
                        if (reply[0] != 1) {
                            lock (gate) lastError = "Persistent administrator setup was refused. [hr=0x" + unchecked((int)reply[1]).ToString("X8", Numbers) + "]";
                            return false;
                        }
                        lock (gate) {
                            if (disposed || generation != token || !active) throw new IOException();
                            savedApprovalAvailable = action == 1;
                        }
                        return true;
                    }
                }
            } catch (Exception) {
                lock (gate) if (authorized) uncertain = true;
                Fail(token, "Persistent administrator setup disconnected. Any partial registration was retained for review.");
                return false;
            } finally { lock (gate) setupPending = false; QueueFocus(); }
        }
        internal static long[] SetupValues(int action, bool isTest, string scope, string nativeHash)
        {
            if ((action != 1 && action != 2) || !AdministratorLaunch.ValidHash(scope) || !AdministratorLaunch.ValidHash(nativeHash)) throw new ArgumentException();
            var result = new long[10]; result[0] = action; result[1] = isTest ? 1 : 0;
            byte[] scopeBytes = HexBytes(scope), nativeBytes = HexBytes(nativeHash);
            for (int i = 0; i < 4; i++) { result[2 + i] = BitConverter.ToInt64(scopeBytes, i * 8); result[6 + i] = BitConverter.ToInt64(nativeBytes, i * 8); }
            return result;
        }
        internal static bool ValidSetupReply(long[] values)
        { return values != null && values.Length == 2 && Boolean(values[0]) && values[1] >= Int32.MinValue && values[1] <= Int32.MaxValue && (values[0] != 1 || values[1] == 0); }
        static byte[] HexBytes(string value)
        { var bytes = new byte[32]; for (int i = 0; i < bytes.Length; i++) bytes[i] = Byte.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, Numbers); return bytes; }
        internal static bool ParseSetup(long[] values, out int action, out bool isTest, out string scope, out string nativeHash)
        {
            action = 0; isTest = false; scope = nativeHash = null;
            if (values == null || values.Length != 10 || (values[0] != 1 && values[0] != 2) || !Boolean(values[1])) return false;
            byte[] scopeBytes = new byte[32], nativeBytes = new byte[32];
            for (int i = 0; i < 4; i++) { Buffer.BlockCopy(BitConverter.GetBytes(values[2 + i]), 0, scopeBytes, i * 8, 8); Buffer.BlockCopy(BitConverter.GetBytes(values[6 + i]), 0, nativeBytes, i * 8, 8); }
            scope = BitConverter.ToString(scopeBytes).Replace("-", "").ToLowerInvariant();
            nativeHash = BitConverter.ToString(nativeBytes).Replace("-", "").ToLowerInvariant();
            if (!AdministratorLaunch.ValidHash(scope) || !AdministratorLaunch.ValidHash(nativeHash)) return false;
            action = (int)values[0]; isTest = values[1] == 1; return true;
        }
        internal static bool ParseResult(long[] values, out WindowMoveWorker.Result result, out int active, out int pending)
        {
            result = null; active = pending = 0;
            if (values == null || values.Length != 8 || values[0] < 0 || values[0] > 4 || !Boolean(values[1]) || !Boolean(values[2]) ||
                values[3] < 0 || values[3] > Int32.MaxValue || values[4] < 0 || values[4] > Int32.MaxValue ||
                values[5] < 0 || values[5] > Int32.MaxValue || values[6] < 0 || values[6] > 2 || values[7] < 0 || values[7] > values[6] ||
                (values[0] == (int)WindowMoveWorker.Outcome.Success && (values[2] != 1 || values[7] != 0)) ||
                (values[0] == (int)WindowMoveWorker.Outcome.Rejected && (values[1] != 0 || values[2] != 1))) return false;
            result = new WindowMoveWorker.Result { Outcome = (WindowMoveWorker.Outcome)values[0], MutationAttempted = values[1] == 1,
                CallerExited = values[2] == 1, MinimumWidth = (int)values[3], MinimumHeight = (int)values[4], NativeError = (int)values[5] };
            active = (int)values[6]; pending = (int)values[7]; return true;
        }
        internal static bool ParseOperationResult(int operation, long[] values, out WindowMoveWorker.Result result,
            out int active, out int pending, out int ownedPins)
        {
            result = null; active = pending = 0; ownedPins = -1;
            if (operation < 0 || operation > 6 || values == null) return false;
            if (operation != 6) return ParseResult(values, out result, out active, out pending);
            if (values.Length != 9 || values[8] < 0 || values[8] > OwnedWindowPins.Limit ||
                !ParseResult(values.Take(8).ToArray(), out result, out active, out pending)) return false;
            if (values[8] == 0 && (!result.CallerExited || active != 0 || pending != 0)) return false;
            ownedPins = (int)values[8]; return true;
        }
        internal static bool PreauthorizationRejection(WindowMoveWorker.Result result)
        { return result != null && result.Outcome != WindowMoveWorker.Outcome.Success && !result.MutationAttempted && result.CallerExited; }
        internal static bool PreauthorizationCancellation(WindowMoveWorker.Result result, int workers, int pending)
        { return result != null && result.Outcome == WindowMoveWorker.Outcome.Cancelled && !result.MutationAttempted && result.CallerExited &&
            result.MinimumWidth == 0 && result.MinimumHeight == 0 && result.NativeError == 0 && workers == 0 && pending == 0; }
        internal static bool ValidValues(int op, long window, uint pid, long started, int[] values)
        {
            if (window == 0 || pid == 0 || started <= 0 || values == null || op < 0 || op > 6) return false;
            if (op == 0) return values.Length == 5 && values[2] > 0 && values[3] > 0 && values[4] == 0x214 &&
                (long)values[0] + values[2] <= Int32.MaxValue && (long)values[1] + values[3] <= Int32.MaxValue;
            if (op == 1) return values.Length == 10 && values[0] >= 0 && values[0] <= 3 && values[1] >= 0 && values[1] <= 11 &&
                values[8] > values[6] && values[9] > values[7] &&
                (long)values[8] - values[6] <= Int32.MaxValue && (long)values[9] - values[7] <= Int32.MaxValue;
            if (op == 6) return values.Length == 1 && Boolean(values[0]);
            return values.Length == 0;
        }
        // Focus snapshots retain GetProcessTimes' raw FILETIME for identity.
        // Peer/IPC lifetimes use UTC DateTime ticks, like the existing movers.
        internal static bool SnapshotLifetime(FocusedWindowSnapshot snapshot, uint process, long utcStarted)
        { return snapshot != null && process != 0 && utcStarted > 0 && snapshot.Process == process && WindowStatePolicy.WorkerStarted(snapshot) == utcStarted; }
        internal static bool ValidFocus(long[] values)
        {
            return values != null && values.Length == 7 && values[0] >= 0 && values[0] <= 2 && values[1] >= 0 && values[1] < FocusAppearance.Presets.Length &&
                Boolean(values[2]) && Boolean(values[3]) && values[4] >= -1 && values[4] <= 3 && values[5] >= 0 && values[5] <= 1 && Boolean(values[6]);
        }
        internal static bool ValidToken(string value)
        { Guid parsed; return value != null && value.Length == 32 && value == value.ToLowerInvariant() && Guid.TryParseExact(value, "N", out parsed) && parsed != Guid.Empty; }
        static void Write(PipeStream stream, int kind, int sequence, int[] values)
        { Write(stream, kind, sequence, values.Select(value => (long)value).ToArray()); }
        static void Write(PipeStream stream, int kind, int sequence, long[] values)
        {
            byte[] packet = Encode(kind, sequence, values);
            stream.Write(packet, 0, packet.Length); stream.Flush();
        }
        internal static byte[] Encode(int kind, int sequence, long[] values)
        {
            if (values == null || values.Length > (MaximumPacket - 8) / 8 || sequence <= 0) throw new IOException();
            using (var memory = new MemoryStream()) using (var writer = new BinaryWriter(memory)) {
                writer.Write(8 + values.Length * 8); writer.Write(kind); writer.Write(sequence);
                foreach (long value in values) writer.Write(value);
                return memory.ToArray();
            }
        }
        static long[] Read(PipeStream stream, out int kind, out int sequence, FocusExchangePhase progress = null)
        { return Decode(stream, out kind, out sequence, progress); }
        internal static long[] Decode(Stream stream, out int kind, out int sequence)
        { return Decode(stream, out kind, out sequence, null); }
        internal static long[] Decode(Stream stream, out int kind, out int sequence, FocusExchangePhase progress)
        {
            if (progress != null) progress.BeginRead(false, 4);
            byte[] header = new byte[4]; ReadExact(stream, header, progress, false);
            int length = BitConverter.ToInt32(header, 0);
            if (length < 8 || length > MaximumPacket || (length - 8) % 8 != 0) throw new IOException();
            if (progress != null) progress.BeginRead(true, length);
            byte[] payload = new byte[length]; ReadExact(stream, payload, progress, true);
            using (var memory = new MemoryStream(payload, false)) using (var reader = new BinaryReader(memory)) {
                kind = reader.ReadInt32(); sequence = reader.ReadInt32();
                if (sequence <= 0) throw new IOException();
                var values = new long[(length - 8) / 8];
                for (int i = 0; i < values.Length; i++) values[i] = reader.ReadInt64();
                if (progress != null) progress.ReadComplete();
                return values;
            }
        }
        static void ReadExact(Stream stream, byte[] bytes, FocusExchangePhase progress = null, bool body = false)
        { int offset = 0; while (offset < bytes.Length) { int read = stream.Read(bytes, offset, bytes.Length - offset); if (read <= 0) throw new EndOfStreamException(); offset += read; if (progress != null) progress.ReadProgress(body, offset); } }
        static void Close(IDisposable value) { if (value != null) try { value.Dispose(); } catch (Exception) { } }
        public void Dispose() { Disable(); lock (gate) { disposed = true; heartbeat.Dispose(); } }

        internal enum HelperStopReason { None, ParentUnavailable, PipeDisconnected, HeartbeatExpired, ProtocolOrder, ProtocolPacket, FocusConfiguration, Authorization, Deadline, Transport, PinCleanup }
        sealed class HelperState
        {
            internal readonly object Gate = new object();
            internal readonly OwnedWindowPins Pins = new OwnedWindowPins();
            internal readonly ReservedPinFocus PinFocus;
            internal Peer Parent;
            internal PipeStream Pipe;
            internal volatile bool Stop, CleanupComplete;
            internal bool CleanupConfirmed;
            internal long Seen, SetupAuthorizedUntil;
            internal HelperStopReason StopReason;
            internal int StopHResult;
            internal int[] Focus = { 0, 0, 1, 0, -1, 0, 1 };
            internal readonly AdministratorSnapInput SnapInput = new AdministratorSnapInput();
            internal HelperState()
            {
                PinFocus = new ReservedPinFocus(delegate(IntPtr window) { return FocusedWindowNative.Read(window, true); },
                    Pins.MatchesPinnedObservation, Native.IsWindow);
            }
            internal void StopWith(HelperStopReason reason, int error)
            {
                lock (Gate) {
                    if (StopReason == HelperStopReason.None) { StopReason = reason; StopHResult = error; }
                    Stop = true;
                }
            }
            internal bool HeartbeatExpired()
            {
                lock (Gate) {
                    // Read the latest receipt under its publication lock. A
                    // timer's earlier snapshot must not expire a heartbeat
                    // that Serve accepted while liveness was being checked.
                    if (Stop || Stopwatch.GetTimestamp() < SetupAuthorizedUntil || (Stopwatch.GetTimestamp() - Seen) * 1000.0 / Stopwatch.Frequency <= 2500) return false;
                    if (StopReason == HelperStopReason.None) StopReason = HelperStopReason.HeartbeatExpired;
                    Stop = true; return true;
                }
            }
            internal bool Live()
            {
                if (Stop) return false;
                if (!ParentAlive(Parent)) { StopWith(HelperStopReason.ParentUnavailable, 0); return false; }
                if (!Connected(Pipe)) { StopWith(HelperStopReason.PipeDisconnected, 0); return false; }
                return true;
            }
        }
        internal static bool TryRun(string[] args)
        {
            if (args == null || args.Length == 0 || args[0] != Mode) return false;
            IsHelperProcess = true; Environment.ExitCode = 2;
            uint pid; long started;
            if (args.Length != 4 || !ValidToken(args[1]) || !UInt32.TryParse(args[2], NumberStyles.None, Numbers, out pid) ||
                !Int64.TryParse(args[3], NumberStyles.None, Numbers, out started)) return true;
            Peer parent = ReadPeer(pid), self = ReadPeer((uint)Process.GetCurrentProcess().Id);
            if (!AdministratorPayloadEntry.IsPayloadProcess || !AdministratorLaunch.IsProtectedPayloadCurrent ||
                parent == null || parent.Started != started || !SameSession(parent, self) || parent.Elevated ||
                !self.Elevated || self.Integrity <= parent.Integrity) return true;
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            using (var client = new NamedPipeClientStream(".", Prefix + args[1], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous)) {
                try {
                    client.Connect(5000);
                    uint server;
                    if (!GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out server) || server != pid ||
                        !SamePeer(parent, ReadPeer(server))) return true;
                    var state = new HelperState { Parent = parent, Pipe = client, Seen = Stopwatch.GetTimestamp() };
                    var reader = new Thread(delegate() { Serve(state); });
                    reader.IsBackground = true; reader.SetApartmentState(ApartmentState.MTA); reader.Name = "Administrator window requests"; reader.Start();
                    using (var context = new HelperContext(state)) Application.Run(context);
                    Close(client);
                    // Unexpected transport loss is best effort. Normal Off and
                    // Exit already received the explicit restoration receipt.
                    if (!state.CleanupComplete) reader.Join(2200);
                    if (!state.CleanupComplete || !state.CleanupConfirmed) state.StopWith(HelperStopReason.PinCleanup, 0);
                    lock (state.Gate) Environment.ExitCode = state.StopReason == HelperStopReason.None ? 0 : 40 + (int)state.StopReason;
                } catch (Exception) { Environment.ExitCode = 2; }
            }
            return true;
        }
        sealed class HelperContext : ApplicationContext
        {
            readonly HelperState state;
            readonly AdministratorWindowIndicators indicators;
            readonly OwnedSnapKeyboard snapKeyboard;
            readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 80 };
            internal HelperContext(HelperState state)
            {
                this.state = state;
                var focus = new FocusIndicator(delegate(IntPtr window) { return !TargetAllowed(state.Parent, window, 0, 0, false); });
                var pins = new PinnedWindowIndicator(delegate(IntPtr window) {
                    return !TargetAllowed(state.Parent, window, 0, 0, false) ||
                        !state.PinFocus.Matches(FocusedWindowNative.Read(window, true));
                }, delegate(IntPtr window, IntPtr target) {
                    return window == focus.Handle && FocusIndicatorSurface.MarksFocusSurface(window, target);
                });
                indicators = new AdministratorWindowIndicators(focus, pins, state.PinFocus);
                snapKeyboard = new OwnedSnapKeyboard(state.SnapInput.Accept, delegate { return !state.Stop && state.SnapInput.CanClaim(); });
                timer.Tick += Tick; timer.Start();
            }
            void Tick(object sender, EventArgs args)
            {
                int[] choices;
                lock (state.Gate) choices = (int[])state.Focus.Clone();
                bool live = state.Live();
                if (!live || state.HeartbeatExpired()) {
                    state.SnapInput.Stop(snapKeyboard); indicators.Hide(); Close(state.Pipe); ExitThread(); return;
                }
                state.SnapInput.Tick(snapKeyboard, delegate(AdministratorSnapInput.Target target) {
                    if (!TargetAllowed(state.Parent, target.Window, target.Process, target.Started, true)) return false;
                    var identity = NativeProcessIdentity.Read(target.Process);
                    return identity != null && identity.Process == target.Process && identity.Started == target.Started &&
                        identity.ElevationAvailable && identity.Elevated && identity.IntegrityAvailable && identity.IntegrityLevel == NativeProcessIdentity.High &&
                        !WindowTilingNative.IsIconic(target.Window) && !AdministratorSnapInput.IsZoomed(target.Window) && WindowTilingNative.Resizable(target.Window) &&
                        !WindowShelfCatalog.IsFullscreenForMovement(target.Window);
                });
                indicators.Update(choices);
            }
            protected override void Dispose(bool disposing)
            { if (disposing) { timer.Stop(); timer.Tick -= Tick; timer.Dispose(); state.SnapInput.Stop(snapKeyboard); snapKeyboard.Dispose(); indicators.Dispose(); } base.Dispose(disposing); }
        }
        static bool TargetAllowed(Peer parent, IntPtr window, uint expected, long started, bool focused,
            bool stateAction = false, bool minimizedCompletion = false)
        {
            uint pid;
            if (window == IntPtr.Zero || !Native.IsWindow(window) || GetAncestor(window, 2) != window ||
                GetWindowThreadProcessId(window, out pid) == 0 || (expected != 0 && pid != expected)) return false;
            Peer target = ReadPeer(pid);
            if (!SameSession(parent, target) || target.Integrity <= parent.Integrity || (started != 0 && target.Started != started)) return false;
            if (focused) {
                var snapshot = FocusedWindowNative.Read(window, stateAction);
                if ((FocusedWindowNative.Foreground() != window && !(minimizedCompletion && WindowStateNative.IsMinimized(window))) ||
                    !(stateAction ? WindowStatePolicy.Eligible(snapshot, delegate { return false; }) : FocusedWindowPolicy.Eligible(snapshot, delegate { return false; })) ||
                    snapshot.Root != window || !SnapshotLifetime(snapshot, pid, target.Started)) return false;
            }
            return true;
        }
        static void Serve(HelperState state)
        {
            int last = 0;
            HelperStopReason phase = HelperStopReason.Transport;
            try {
                while (state.Live()) {
                    phase = HelperStopReason.Transport;
                    int kind, number; long[] values = Read(state.Pipe, out kind, out number);
                    phase = HelperStopReason.ProtocolOrder;
                    if (number <= last && !(last == Int32.MaxValue && number == 1)) throw new IOException();
                    last = number;
                    lock (state.Gate) state.Seen = Stopwatch.GetTimestamp();
                    if (kind == SnapPoll) {
                        phase = HelperStopReason.ProtocolPacket;
                        state.SnapInput.Configure(values);
                        phase = HelperStopReason.Transport;
                        Write(state.Pipe, SnapReply, number, state.SnapInput.Reply(WindowMoveWorker.ActiveHelpers, WindowMoveWorker.UncertainHelpers));
                        continue;
                    }
                    if (kind == Focus) {
                        phase = HelperStopReason.FocusConfiguration;
                        if (!ValidFocus(values)) throw new IOException();
                        lock (state.Gate) state.Focus = values.Select(value => (int)value).ToArray();
                        phase = HelperStopReason.Transport;
                        Write(state.Pipe, FocusReply, number, new long[] { WindowMoveWorker.ActiveHelpers, WindowMoveWorker.UncertainHelpers });
                        continue;
                    }
                    if (kind == PersistentSetup) {
                        int setupAction; bool isTest; string scope, nativeHash;
                        phase = HelperStopReason.ProtocolPacket;
                        if (!ParseSetup(values, out setupAction, out isTest, out scope, out nativeHash)) throw new IOException();
                        long setupDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30L;
                        using (var timeout = new DeadlineWatch(delegate { state.StopWith(HelperStopReason.Deadline, 0); Close(state.Pipe); }, 30000)) {
                            Write(state.Pipe, SetupReady, number, new long[0]);
                            int action, id; long[] authorization = Read(state.Pipe, out action, out id);
                            phase = HelperStopReason.Authorization;
                            if (action != SetupAuthorize || id != number || authorization.Length != 1 || authorization[0] != 1 || !state.Live()) throw new IOException();
                            lock (state.Gate) state.SetupAuthorizedUntil = setupDeadline;
                            bool success = false; int error = 0;
                            try {
                                // The scope and native digest are numeric fixed-size identities.
                                // Install derives code and controller bytes from authenticated processes.
                                Action authorized = delegate {
                                    if (!state.Live() || Stopwatch.GetTimestamp() >= setupDeadline) throw new IOException("Administrator maintenance authorization expired.");
                                };
                                success = setupAction == 1 ? PersistentAdministrator.Install(scope, state.Parent.Process, state.Parent.Started, nativeHash, authorized) :
                                    PersistentAdministrator.Remove(scope, state.Parent.Process, state.Parent.Started, nativeHash, authorized);
                            } catch (Exception failure) { error = failure.HResult; }
                            finally { lock (state.Gate) { state.SetupAuthorizedUntil = 0; state.Seen = Stopwatch.GetTimestamp(); } }
                            if (!state.Live() || Stopwatch.GetTimestamp() >= setupDeadline) throw new IOException();
                            Write(state.Pipe, SetupReply, number, new long[] { success ? 1 : 0, error });
                        }
                        continue;
                    }
                    if (kind == ReleasePins) {
                        phase = HelperStopReason.ProtocolPacket;
                        if (!ValidPinReleaseRequest(values, Stopwatch.GetTimestamp())) throw new IOException();
                        long cleanupDeadline = values[0];
                        int cleanupDue = Math.Max(1, (int)Math.Ceiling((cleanupDeadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency));
                        using (var timeout = new DeadlineWatch(delegate { state.StopWith(HelperStopReason.Deadline, 0); Close(state.Pipe); }, cleanupDue + 100)) {
                            Write(state.Pipe, ReleaseReady, number, new long[0]);
                            int action, id; long[] authorization = Read(state.Pipe, out action, out id);
                            phase = HelperStopReason.Authorization;
                            if (!ValidPinReleaseAuthorization(action, id, number, authorization) ||
                                !state.Live() || Stopwatch.GetTimestamp() >= cleanupDeadline) throw new IOException();
                            // No caller-selected handle or native parameters
                            // exist here. The registry restores only its pins.
                            state.PinFocus.Clear();
                            long[] receipt = ReleasePinValues(state.Pins, cleanupDeadline);
                            lock (state.Gate) state.Seen = Stopwatch.GetTimestamp();
                            phase = HelperStopReason.Transport;
                            Write(state.Pipe, ReleaseReply, number, receipt);
                        }
                        continue;
                    }
                    phase = HelperStopReason.ProtocolPacket;
                    if (kind != Request || values.Length < 5 || values.Length > 15 || values[0] < 0 || values[0] > 6 || values[2] <= 0 || values[2] > UInt32.MaxValue) throw new IOException();
                    int op = (int)values[0]; long handle = values[1], started = values[3], deadline = values[4]; uint pid = (uint)values[2];
                    int[] arguments = new int[values.Length - 5];
                    for (int i = 0; i < arguments.Length; i++) { long value = values[5 + i]; if (value < Int32.MinValue || value > Int32.MaxValue) throw new IOException(); arguments[i] = (int)value; }
                    if (!ValidValues(op, handle, pid, started, arguments) || deadline <= Stopwatch.GetTimestamp() ||
                        (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency > 2000 || !TargetAllowed(state.Parent, new IntPtr(handle), pid, started, op >= 4, op >= 5)) {
                        WriteResult(state.Pipe, number, Rejection(), op == 6 ? state.Pins : null); continue;
                    }
                    int due = Math.Max(1, (int)Math.Ceiling((deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency));
                    using (var timeout = new DeadlineWatch(delegate { state.StopWith(HelperStopReason.Deadline, 0); Close(state.Pipe); }, due + 150)) {
                        phase = HelperStopReason.Transport;
                        Write(state.Pipe, Ready, number, new long[0]);
                        phase = HelperStopReason.Authorization;
                        if (!ReceiveOperationAuthorization(state, number, deadline, op == 6 ? state.Pins : null)) continue;
                        Func<bool> valid = delegate { return state.Live() && Stopwatch.GetTimestamp() < deadline &&
                            TargetAllowed(state.Parent, new IntPtr(handle), pid, started, op >= 4, op >= 5, op == 5); };
                        FocusedWindowSnapshot pin = null;
                        if (op == 6 && arguments[0] == 1 && valid()) {
                            pin = FocusedWindowNative.Read(new IntPtr(handle), true);
                            if (!WindowStatePolicy.Eligible(pin, delegate { return false; }) || !SnapshotLifetime(pin, pid, started) ||
                                WindowStatePolicy.Topmost(pin)) {
                                WriteResult(state.Pipe, number, Rejection(), state.Pins); continue;
                            }
                            state.PinFocus.ForgetObservation(pin.Window);
                            if (!state.Pins.Remember(pin)) {
                                WriteResult(state.Pipe, number, Rejection(), state.Pins); continue;
                            }
                        }
                        // RunLocal preserves the existing launch lease,
                        // authorization, deadline, kill and quarantine rules.
                        var result = valid() ? WindowMoveWorker.RunLocal(op, new IntPtr(handle), pid, started, deadline, valid, arguments) : Rejection();
                        if (pin != null && !result.MutationAttempted && result.CallerExited) state.Pins.Forget(pin);
                        if (op == 6 && arguments[0] == 0 && result.Outcome == WindowMoveWorker.Outcome.Success && result.CallerExited) {
                            var current = FocusedWindowNative.Read(new IntPtr(handle), true);
                            if (WindowStatePolicy.Verified(current) && SnapshotLifetime(current, pid, started) && !WindowStatePolicy.Topmost(current)) {
                                state.Pins.Forget(current);
                                state.PinFocus.RecordResult(current, false, result);
                            }
                        }
                        if (pin != null) state.PinFocus.RecordResult(pin, true, result);
                        lock (state.Gate) state.Seen = Stopwatch.GetTimestamp();
                        phase = HelperStopReason.Transport;
                        WriteResult(state.Pipe, number, result, op == 6 ? state.Pins : null);
                    }
                }
            } catch (Exception error) { state.StopWith(phase, error.HResult); }
            finally {
                state.PinFocus.Clear();
                try { state.CleanupConfirmed = state.Pins.ReleaseAll(Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency) &&
                    WindowMoveWorker.ActiveHelpers == 0 && WindowMoveWorker.UncertainHelpers == 0 && state.Pins.Count == 0; }
                catch (Exception) { state.CleanupConfirmed = false; }
                if (!state.CleanupConfirmed) state.StopWith(HelperStopReason.PinCleanup, 0);
                state.CleanupComplete = true; Close(state.Pipe);
            }
        }
        static bool ReceiveAuthorization(HelperState state, int number, long deadline)
        { return ReceiveOperationAuthorization(state, number, deadline, null); }
        static bool ReceiveOperationAuthorization(HelperState state, int number, long deadline, OwnedWindowPins pins)
        {
            int action, id; long[] authorization = Read(state.Pipe, out action, out id);
            if (action != Authorize || id != number || authorization.Length != 1 || !Boolean(authorization[0])) throw new IOException();
            if (authorization[0] == 1) return true;
            if (!state.Live() || Stopwatch.GetTimestamp() >= deadline || WindowMoveWorker.ActiveHelpers != 0 || WindowMoveWorker.UncertainHelpers != 0)
                throw new IOException();
            lock (state.Gate) state.Seen = Stopwatch.GetTimestamp();
            WriteResult(state.Pipe, number, Rejection(WindowMoveWorker.Outcome.Cancelled), pins);
            return false;
        }
        static void WriteResult(PipeStream stream, int number, WindowMoveWorker.Result result, OwnedWindowPins pins = null)
        {
            long[] values = { (int)result.Outcome, result.MutationAttempted ? 1 : 0, result.CallerExited ? 1 : 0,
                result.MinimumWidth, result.MinimumHeight, result.NativeError, WindowMoveWorker.ActiveHelpers, WindowMoveWorker.UncertainHelpers };
            Write(stream, ResultReply, number, pins == null ? values : values.Concat(new long[] { pins.Count }).ToArray());
        }
    }
}
