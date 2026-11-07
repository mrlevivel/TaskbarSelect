using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace TaskbarSelect
{
    // Protocol/owned local pipes and one fixed hidden exit child only. No
    // UAC, elevated helpers, windows, user settings, Startup or security changes.
    internal static class AdministratorSupportTests
    {
        static bool Throws(Action action)
        { try { action(); return false; } catch (IOException) { return true; } }
        internal static void Run(Action<bool, string> check)
        { Values(check); RunPinVisibility(check); PersistentSetupProtocol(check); BootstrapProtocol(check); ShutdownAdmission(check); ShutdownRecoveryRetry(check); ClrExecutionSpellingChecks(check); ProcessLifetimeChecks(check); OptionalDiagnosticCapture(check); ConnectionLifecycle(check); Frames(check); Results(check); DeadlineDisposal(check); FailureRetention(check); IndependentHeartbeat(check); HeartbeatLifecycle(check); FocusDeadlinePhase(check); FocusTransportDiagnostics(check); RejectionThenSuccess(check); FocusBetweenRequests(check); FocusSnapRequestOrdering(check); ExpiredServeRequests(check); SnapServeRequests(check); OperationCancellation(check); ConfirmedReconnectPolicy(check); }
        internal static void RunPinVisibility(Action<bool, string> check)
        {
            foreach (long last in new[] { -1L, 2L, Int64.MaxValue })
                check(!AdministratorSupport.ValidFocus(new long[] { 2, 0, 0, 0, 3, 0, last }),
                    "The helper pin-visibility field rejects values outside a strict numeric Boolean");
            check(!AdministratorSupport.ValidFocus(new long[] { 2, 0, 0, 0, 3, 0 }) &&
                !AdministratorSupport.ValidFocus(new long[] { 2, 0, 0, 0, 3, 0, 0, 42 }),
                "Helper pin appearance rejects both the old arity and appended arbitrary target data");
            using (var support = new AdministratorSupport()) {
                support.UpdateFocus(2, "Forest Green", true, false, 3, 0, false);
                var choices = (int[])Get(support, "focus");
                check(choices.Length == 7 && choices[2] == 1 && choices[6] == 0,
                    "Pause moves sends paused focus and visible HIGH pins as independent appearance choices");
                support.HideFocus(); choices = (int[])Get(support, "focus");
                check(choices[2] == 1 && choices[6] == 0, "Immediate focus hiding cannot hide HIGH pins during Pause moves");
                support.HidePins(); choices = (int[])Get(support, "focus");
                check(choices[2] == 1 && choices[6] == 1, "Pause all can immediately fence the helper's HIGH pin surfaces");
                support.UpdateFocus(2, "Forest Green", false, false, 3, 0, true);
                choices = (int[])Get(support, "focus");
                check(choices[2] == 0 && choices[6] == 1, "Pin visibility cannot accidentally pause unrelated helper focus updates");
                support.PermissionChangePending = true;
                support.UpdateFocus(2, "Forest Green", false, false, 3, 0, false);
                choices = (int[])Get(support, "focus");
                check(choices[2] == 1 && choices[6] == 1,
                    "Pending administrator permission changes fence both helper surfaces regardless of requested appearance");
            }
        }
        static void OptionalDiagnosticCapture(Action<bool, string> check)
        {
            check(AdministratorSupport.TryRetainDiagnosticProcess(0, 0) == null,
                "Unavailable diagnostic identity cannot abort otherwise authenticated admission");
            uint pid = (uint)Process.GetCurrentProcess().Id;
            long started = NativeProcessIdentity.Started(pid);
            check(AdministratorSupport.TryRetainDiagnosticProcess(pid, started + 1) == null,
                "Optional diagnostic capture rejects a mismatched process generation without retaining its handle");
            using (var retained = AdministratorSupport.TryRetainDiagnosticProcess(pid, started))
                check(retained != null && !retained.HasExited && retained.Id == pid && retained.Started == started,
                    "An exact diagnostic lifetime is retained with query-only access and disposed without stopping its process");
        }
        static void PersistentSetupProtocol(Action<bool, string> check)
        {
            string scope = new string('a', 64), native = new string('b', 64);
            int action; bool isTest; string parsedScope, parsedNative;
            foreach (int op in new[] { 1, 2 }) foreach (bool test in new[] { false, true }) {
                long[] packet = AdministratorSupport.SetupValues(op, test, scope, native);
                check(AdministratorSupport.ParseSetup(packet, out action, out isTest, out parsedScope, out parsedNative) &&
                    action == op && isTest == test && parsedScope == scope && parsedNative == native,
                    "Persistent setup binds fixed action, profile role, scope and approved native digest");
            }
            foreach (long[] invalid in new[] { null, new long[0], new long[9], new long[11],
                new long[] { 3, 1, 0, 0, 0, 0, 0, 0, 0, 0 }, new long[] { 1, 2, 0, 0, 0, 0, 0, 0, 0, 0 } })
                check(!AdministratorSupport.ParseSetup(invalid, out action, out isTest, out parsedScope, out parsedNative),
                    "Persistent setup rejects malformed or arbitrary command payloads");
            using (var support = new AdministratorSupport()) {
                check(!support.InstallPersistent(PersistentAdministrator.ScopeForCurrentProfile(), AppProfile.IsTest) &&
                    !support.RemovePersistent(PersistentAdministrator.ScopeForCurrentProfile(), AppProfile.IsTest),
                    "Registration and revocation require an authenticated active manual helper, with no implicit UAC or task changes");
            }
            check(!new Settings().PersistentAdministratorSupport, "Existing profiles default to per-session support until explicit opt-in");
            check(AdministratorSupport.ValidSetupReply(new long[] { 1, 0 }) && AdministratorSupport.ValidSetupReply(new long[] { 0, -1 }),
                "Persistent setup reply accepts confirmed success or fixed numeric failure");
            foreach (long[] invalid in new[] { null, new long[0], new long[] { 1 }, new long[] { 1, 0, 0 },
                new long[] { 2, 0 }, new long[] { 1, -1 }, new long[] { 0, (long)Int32.MaxValue + 1 }, new long[] { 0, (long)Int32.MinValue - 1 } })
                check(!AdministratorSupport.ValidSetupReply(invalid), "Persistent setup rejects contradictory or out-of-range replies");
        }
        static void ShutdownAdmission(Action<bool, string> check)
        {
            using (var broker = new AdministratorSupport()) {
                var type = typeof(AdministratorSupport);
                object session = Activator.CreateInstance(type.GetNestedType("LaunchSession", BindingFlags.NonPublic), true);
                Set(broker, "launchSession", session); Set(broker, "shutdownConfirmed", false);
                try {
                    check(!broker.StopForExit(30) && broker.HasUncertainOperation && (int)Get(broker, "pendingShutdowns") == 1,
                        "Pending approval/startup blocks exit without creating a process or abandoning its launch session");
                } finally { Set(session, "StartComplete", true); }
                check(Wait(delegate { return (int)Get(broker, "pendingShutdowns") == 0; }, 1000) && broker.StopForExit(100),
                    "A later cancelled start with positively no launched process permits retry and releases recovery quarantine");
            }
            using (var broker = new AdministratorSupport()) check(broker.StopForExit(0), "A never-enabled session exits immediately without UAC, files or desktop actions");
        }
        static void ShutdownRecoveryRetry(Action<bool, string> check)
        {
            using (var broker = new AdministratorSupport()) {
                object lease = Activator.CreateInstance(typeof(AdministratorSupport).GetNestedType("Lease", BindingFlags.NonPublic), true);
                Set(lease, "Authorized", true); Set(lease, "Mutation", true);
                Set(broker, "operation", lease); Set(broker, "uncertain", true);
                // These are independent injected completion signals, not a
                // launched helper. A stopped native job must never substitute
                // for the managed callback's own completion fence.
                Set(broker, "shutdownConfirmed", AdministratorSupport.BootstrapDrained(102, true));
                try {
                    check(!broker.StopForExit(0) && broker.HasUncertainOperation && Object.ReferenceEquals(Get(broker, "operation"), lease),
                        "Verified native refusal cannot release recovery while an authorized mutation callback remains pending");
                    check(!broker.StopForExit(0) && broker.HasUncertainOperation,
                        "Repeated exit attempts retain pending mutation quarantine without losing its callback record");
                    Set(lease, "Complete", true);
                    Set(broker, "shutdownConfirmed", AdministratorSupport.BootstrapDrained(102, false));
                    check(!broker.StopForExit(0) && broker.HasUncertainOperation,
                        "Callback completion alone cannot clear quarantine after an unverified native refusal");
                    Set(broker, "shutdownConfirmed", AdministratorSupport.BootstrapDrained(102, true));
                    check(broker.StopForExit(100) && !broker.HasUncertainOperation && Object.ReferenceEquals(Get(broker, "operation"), lease),
                        "A later verified no-child refusal and completed callback allow exit retry without discarding recovery evidence early");
                } finally {
                    Set(lease, "Complete", true); Set(broker, "operation", null); Set(broker, "shutdownConfirmed", true);
                }
            }
        }
        static void BootstrapProtocol(Action<bool, string> check)
        {
            long[] hello = { 12, 34, 56, 78 };
            check(AdministratorSupport.ValidBootstrapHello(hello, 12, 34, 56, 78), "Native bootstrap hello binds both process IDs and start times");
            for (int i = 0; i < hello.Length; i++) {
                long[] changed = (long[])hello.Clone(); changed[i]++;
                check(!AdministratorSupport.ValidBootstrapHello(changed, 12, 34, 56, 78), "Native bootstrap rejects mismatched identity field " + i);
            }
            check(!AdministratorSupport.ValidBootstrapHello(null, 12, 34, 56, 78) &&
                !AdministratorSupport.ValidBootstrapHello(new long[3], 12, 34, 56, 78), "Native bootstrap requires exactly four identity numbers");
            check(AdministratorSupport.ValidBootstrapChild(new long[] { 9, 10 }) &&
                !AdministratorSupport.ValidBootstrapChild(new long[] { 0, 10 }) &&
                !AdministratorSupport.ValidBootstrapChild(new long[] { 9, 0 }) &&
                !AdministratorSupport.ValidBootstrapChild(new long[] { (long)UInt32.MaxValue + 1, 10 }) &&
                !AdministratorSupport.ValidBootstrapChild(new long[] { 9, 10, 11 }), "Suspended payload reply admits only one positive PID and creation time");
            check(AdministratorSupport.ValidApprovalChild(new long[] { 9, 10 }, 9, 10),
                "MED approval reply binds the actual authenticated HIGH native PID and retained creation time");
            foreach (long[] invalid in new[] { null, new long[0], new long[] { 9 }, new long[] { 9, 10, 11 },
                new long[] { 0, 10 }, new long[] { 9, 0 }, new long[] { (long)UInt32.MaxValue + 1, 10 }, new long[] { 8, 10 }, new long[] { 9, 11 } })
                check(!AdministratorSupport.ValidApprovalChild(invalid, 9, 10),
                    "Malformed or mismatched MED-to-HIGH approval identity cannot authorize payload staging");
            check(!AdministratorSupport.ValidApprovalChild(new long[] { 9, 10 }, 0, 10) &&
                !AdministratorSupport.ValidApprovalChild(new long[] { 9, 10 }, 9, 0), "Unavailable actual HIGH identity fails approval admission");
            check(AdministratorSupport.BootstrapDrained(0, true) && !AdministratorSupport.BootstrapDrained(0, false) &&
                !AdministratorSupport.BootstrapDrained(90, true) && !AdministratorSupport.BootstrapDrained(-1, true),
                "Unconfirmed native exit or job drain cannot clear recovery uncertainty");
            for (int code = 100; code <= 108; code++)
                check(AdministratorSupport.BootstrapDrained(code, true) && !AdministratorSupport.BootstrapDrained(code, false),
                    "A verified native refusal before any child permits recovery retry at exit " + code);
            foreach (int code in new[] { 111, 112 })
                check(AdministratorSupport.BootstrapDrained(code, true) && !AdministratorSupport.BootstrapDrained(code, false),
                    "Verified MED host cancellation or pre-elevation namespace refusal permits shutdown retry at exit " + code);
            foreach (int code in new[] { 1, 89, 90, 99, 109, 110, 113, Int32.MaxValue, Int32.MinValue })
                check(!AdministratorSupport.BootstrapDrained(code, true), "Unknown or unconfirmed native status retains recovery at exit " + code);
            check(AdministratorSupport.ExitQuiescent(0, true, false, false), "No-admin or verified drained sessions permit immediate normal exit");
            check(!AdministratorSupport.ExitQuiescent(1, true, false, false) && !AdministratorSupport.ExitQuiescent(0, false, false, false) &&
                !AdministratorSupport.ExitQuiescent(0, true, true, false) && !AdministratorSupport.ExitQuiescent(0, true, false, true),
                "Pending approval, unconfirmed native drain, uncertainty and authorized pending work block mutex release and cleanup");
        }
        static void Values(Action<bool, string> check)
        {
            int[] position = { -2560, -1080, 640, 480, 0x214 };
            int[] placement = { 0, 3, -1, -1, -1, -1, -2560, -1080, -1920, -600 };
            check(AdministratorSupport.ValidValues(0, 12, 3, 4, position) && AdministratorSupport.ValidValues(1, 12, 3, 4, placement),
                "Administrator protocol permits fixed movement/placement and negative stacked monitor coordinates");
            foreach (int op in new[] { 2, 3, 4 }) check(AdministratorSupport.ValidValues(op, 12, 3, 4, new int[0]), "Foreground/minimum/close accept no message or command arguments at operation " + op);
            check(!AdministratorSupport.ValidValues(0, 12, 3, 4, new[] { Int32.MaxValue, 0, 2, 2, 0x214 }) &&
                !AdministratorSupport.ValidValues(0, 12, 3, 4, new[] { 0, Int32.MaxValue, 2, 2, 0x214 }),
                "Position rejects endpoints that overflow native coordinates");
            check(!AdministratorSupport.ValidValues(1, 12, 3, 4, new[] { 0, 1, 0, 0, 0, 0, Int32.MinValue, 0, Int32.MaxValue, 1 }) &&
                !AdministratorSupport.ValidValues(1, 12, 3, 4, new[] { 0, 1, 0, 0, 0, 0, 0, Int32.MinValue, 1, Int32.MaxValue }),
                "Placement rejects spans that exceed a native integer size");
            foreach (int op in new[] { -1, 7, Int32.MaxValue }) check(!AdministratorSupport.ValidValues(op, 12, 3, 4, new int[0]), "Unknown administrator operation is rejected " + op);
            check(!AdministratorSupport.ValidValues(0, 0, 3, 4, position) && !AdministratorSupport.ValidValues(0, 12, 0, 4, position) &&
                !AdministratorSupport.ValidValues(0, 12, 3, 0, position), "Zero HWND/PID/start time cannot authorize elevated work");
            check(!AdministratorSupport.ValidValues(0, 12, 3, 4, new[] { 0, 0, 1, 1, 0 }) &&
                !AdministratorSupport.ValidValues(0, 12, 3, 4, new[] { 0, 0, 0, 1, 0x214 }) &&
                !AdministratorSupport.ValidValues(1, 12, 3, 4, new[] { 0, 3, 0, 0, 0, 0, 5, 5, 4, 4 }) &&
                !AdministratorSupport.ValidValues(4, 12, 3, 4, new[] { 0x10 }), "Invalid flags/sizes/placement or arbitrary close messages are rejected");
            for (int index = 0; index < FocusAppearance.Presets.Length; index++)
                check(AdministratorSupport.ValidFocus(new long[] { 2, index, 0, 1, 3, 1, 0 }), "Focus accepts a numeric allowlisted accent index " + index);
            check(!AdministratorSupport.ValidFocus(new long[] { 2, FocusAppearance.Presets.Length, 0, 1, 3, 1, 0 }) &&
                !AdministratorSupport.ValidFocus(new long[] { 2, 0, 2, 1, 3, 1, 0 }) &&
                !AdministratorSupport.ValidFocus(new long[] { 2, 0, 0, 1, 4, 1, 0 }) &&
                !AdministratorSupport.ValidFocus(new long[] { 2, 0, 0, 1, 3, 1, 2 }), "Focus rejects arbitrary color indices, boolean values and lighting modes");
            check(AdministratorSupport.ValidToken("74a5a692966c4c07a97e374ed87a066e") && !AdministratorSupport.ValidToken(new string('0', 32)) &&
                !AdministratorSupport.ValidToken("..\\settings.json") && !AdministratorSupport.ValidToken("74a5a692-966c-4c07-a97e-374ed87a066e"),
                "Launch mode requires a nonempty fixed32-character GUID; paths and arbitrary arguments are rejected");
            check(!AdministratorSupport.ValidToken("74A5A692966C4C07A97E374ED87A066E"), "Administrator tokens use one canonical lowercase spelling");
        }
        static void Frames(Action<bool, string> check)
        {
            // SetWindowPlacement needs5 identity/deadline fields plus10values.
            long[] placement = { 1, 19, 3, 4, 5, 0, 3, -1, -1, -1, -1, -2560, -1080, -1920, -600 };
            byte[] encoded = AdministratorSupport.Encode(1, 7, placement);
            using (var stream = new MemoryStream(encoded, false)) {
                int kind, number; long[] decoded = AdministratorSupport.Decode(stream, out kind, out number);
                check(encoded.Length == 132 && BitConverter.ToInt32(encoded, 0) == 128 && kind == 1 && number == 7 &&
                    placement.SequenceEqual(decoded) && stream.Position == stream.Length,
                    "Maximum15-number placement packet roundtrips exactly within128-byte body cap");
            }
            check(Throws(delegate { AdministratorSupport.Encode(1, 7, new long[16]); }) &&
                Throws(delegate { AdministratorSupport.Encode(1, 0, new long[0]); }), "Overlong packets and invalid request sequences fail before writing");
            foreach (int length in new[] { -1, 0, 7, 9, 129, Int32.MaxValue }) {
                using (var stream = new MemoryStream(BitConverter.GetBytes(length), false))
                    check(Throws(delegate { int kind, number; AdministratorSupport.Decode(stream, out kind, out number); }), "Invalid IPC length is rejected without an unbounded allocation " + length);
            }
            using (var stream = new MemoryStream(encoded.Take(encoded.Length - 1).ToArray(), false))
                check(Throws(delegate { int kind, number; AdministratorSupport.Decode(stream, out kind, out number); }), "A truncated packet cannot authorize a partial operation");
        }
        static void Results(Action<bool, string> check)
        {
            WindowMoveWorker.Result result; int active, pending;
            check(AdministratorSupport.ParseResult(new long[] { 1, 0, 1, 0, 0, 0, 0, 0 }, out result, out active, out pending) &&
                AdministratorSupport.PreauthorizationRejection(result), "An authentic prelaunch rejection proves no mutation and caller exit");
            check(!AdministratorSupport.ParseResult(new long[] { 1, 1, 1, 0, 0, 0, 0, 0 }, out result, out active, out pending),
                "A rejection cannot claim mutation was attempted");
            check(!AdministratorSupport.ParseResult(new long[] { 0, 1, 0, 0, 0, 0, 1, 0 }, out result, out active, out pending) &&
                !AdministratorSupport.ParseResult(new long[] { 0, 1, 1, 0, 0, 0, 1, 1 }, out result, out active, out pending),
                "Success cannot conceal an unconfirmed caller exit or quarantined worker");
            check(AdministratorSupport.ParseResult(new long[] { 3, 1, 0, 0, 0, 0, 1, 1 }, out result, out active, out pending) &&
                !AdministratorSupport.PreauthorizationRejection(result) && result.MutationAttempted && !result.CallerExited,
                "Timed out authorized work retains attempted mutation, unconfirmed exit and quarantine");
            check(!AdministratorSupport.ParseResult(new long[] { 2, 0, 1, 0, 0, 0, 0, 1 }, out result, out active, out pending),
                "Pending workers cannot exceed the authenticated active-worker count");
        }
        static void Set(object item, string name, object value)
        { item.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(item, value); }
        static object Get(object item, string name)
        { return item.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(item); }
        static bool Wait(Func<bool> condition, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.ElapsedMilliseconds >= milliseconds) return condition(); Thread.Sleep(2); }
            return true;
        }
        static void Connect(NamedPipeServerStream server)
        {
            var pending = new AdministratorSupport.ConnectionWait(server);
            if (!Wait(delegate { return pending.Complete; }, 1000)) throw new IOException();
            pending.ThrowIfFailed();
        }
        static AdministratorSupport.ProcessLifetime OwnedLifetime()
        {
            using (var process = Process.GetCurrentProcess()) {
                uint pid = (uint)process.Id;
                return AdministratorSupport.ProcessLifetime.Open(pid, NativeProcessIdentity.Started(pid));
            }
        }
        static void ClrExecutionSpellingChecks(Action<bool, string> check)
        {
            foreach (string path in new[] { @"C:\ProgramData\TaskbarSelect.Admin\cache\AdministratorPayload.exe", @"X:\Owned CLR fixture\AdministratorPayload.exe" })
                check(AdministratorLaunch.ClrLaunchPath(@"\\?\" + path) == path && AdministratorLaunch.InputPath(path) && !AdministratorLaunch.InputPath(@"\\?\" + path),
                    "A handle-derived extended DOS execution spelling becomes CLR-compatible without widening caller path admission");
            foreach (string invalid in new[] { null, "", @"C:\payload.exe", @"payload.exe", @"\\?\payload.exe", @"\\.\C:\payload.exe",
                @"\\?\UNC\server\share\payload.exe", @"\\?\GLOBALROOT\Device\HarddiskVolume1\payload.exe",
                @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\payload.exe", @"\\?\C:\a\..\payload.exe", @"\\?\C:\a\payload.exe:ads" }) {
                bool rejected = false;
                try { AdministratorLaunch.ClrLaunchPath(invalid); } catch (UnauthorizedAccessException) { rejected = true; }
                check(rejected, "CLR execution conversion rejects non-DOS, relative, redirected or noncanonical spellings");
            }
        }
        static void ProcessLifetimeChecks(Action<bool, string> check)
        {
            using (var own = OwnedLifetime()) {
                check(own.Id != 0 && own.Started == NativeProcessIdentity.Started(own.Id) && !own.HasExited && !own.WaitForExit(0),
                    "A retained query-only lifetime binds the owned PID/start and observes its running state without requesting full process information");
                check(AdministratorSupport.PayloadExitEvidence(own) == "" && AdministratorSupport.PayloadExitEvidence(null) == "",
                    "Startup diagnostics do not invent an exit code for an absent or still-running retained payload");
                bool stale = false, missing = false, early = false;
                try { using (var ignored = AdministratorSupport.ProcessLifetime.Open(own.Id, own.Started + 1)) { } }
                catch (UnauthorizedAccessException) { stale = true; }
                try { using (var ignored = AdministratorSupport.ProcessLifetime.Open(0, own.Started)) { } }
                catch (UnauthorizedAccessException) { missing = true; }
                try { int ignored = own.ExitCode; GC.KeepAlive(ignored); }
                catch (InvalidOperationException) { early = true; }
                check(stale && missing && early, "Stale/missing identities and a running process's exit code fail closed on the retained lifetime");
                using (var broker = new AdministratorSupport()) {
                    Set(broker, "helper", own); Set(broker, "active", true); Set(broker, "focusQueued", true);
                    broker.Poll();
                    check(broker.Active && broker.LastError.Length == 0,
                        "The production Poll path keeps a live query-only helper active instead of mistaking unavailable full-information access for exit");
                }
            }
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/d /q") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var child = Process.Start(start)) {
                if (child == null) throw new IOException("The owned exit fixture did not start.");
                try {
                    uint pid = (uint)child.Id;
                    using (var lifetime = AdministratorSupport.ProcessLifetime.Open(pid, NativeProcessIdentity.Started(pid))) {
                        check(!lifetime.WaitForExit(0), "The owned hidden exit child remains alive before its fixed exit command");
                        child.StandardInput.WriteLine("exit /b 37"); child.StandardInput.Flush();
                        check(lifetime.WaitForExit(2000) && lifetime.HasExited && lifetime.ExitCode == 37,
                            "The retained query-only wait signals the owned child's exit and reports its exact native exit code");
                        check(AdministratorSupport.PayloadExitEvidence(lifetime) == " payloadExit=37",
                            "A pre-connect payload failure preserves its exact exit code from the local retained handle before cleanup");
                        using (var broker = new AdministratorSupport()) {
                            Set(broker, "helper", lifetime); Set(broker, "active", true); Set(broker, "focusQueued", true);
                            broker.Poll();
                            check(!broker.Active && broker.LastError.Contains("helperExit=37"),
                                "Production Poll/HelperExitCode record an exited retained helper without a PID reopen or Framework process query");
                        }
                    }
                } finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); } }
            }
        }
        static void ConnectionLifecycle(Action<bool, string> check)
        {
            for (int iteration = 0; iteration < 24; iteration++) {
                string name = "TaskbarSelect.OwnedConnection." + Guid.NewGuid().ToString("N");
                using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)) {
                    var waiting = new AdministratorSupport.ConnectionWait(server);
                    check(!waiting.Complete, "An owned pipe without a client remains pending before cancellation " + iteration);
                    server.Dispose();
                    check(Wait(delegate { return waiting.Complete; }, 1000), "Disposed pending pipe publishes its completed IO callback without a disposed wait-event crash " + iteration);
                    bool cancelled = false;
                    try { waiting.ThrowIfFailed(); } catch (IOException) { cancelled = true; } catch (ObjectDisposedException) { cancelled = true; }
                    check(cancelled, "Cancelled connection reports its owned transport failure to the caller instead of escaping the completion thread " + iteration);
                }
                name = "TaskbarSelect.OwnedConnectionRetry." + Guid.NewGuid().ToString("N");
                using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512))
                using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                    var waiting = new AdministratorSupport.ConnectionWait(server);
                    client.Connect(1000);
                    check(Wait(delegate { return waiting.Complete; }, 1000), "A fresh owned connection completes after the previous cancellation " + iteration);
                    waiting.ThrowIfFailed(); client.WriteByte(37);
                    check(server.IsConnected && server.ReadByte() == 37, "Successfully observed connection remains usable and ends exactly once before teardown " + iteration);
                }
            }
            for (int iteration = 0; iteration < 8; iteration++) {
                string name = "TaskbarSelect.OwnedConnectionGeneration." + Guid.NewGuid().ToString("N");
                using (var broker = new AdministratorSupport())
                using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                using (var ownProcess = OwnedLifetime())
                using (var began = new ManualResetEvent(false))
                using (var completed = new ManualResetEvent(false)) {
                    Set(broker, "generation", 7); Set(broker, "enabling", true);
                    Exception failure = null;
                    var thread = new Thread(delegate() {
                        began.Set();
                        try { typeof(AdministratorSupport).GetMethod("ConnectPayload", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(broker, new object[] { server, ownProcess, 7 }); }
                        catch (TargetInvocationException error) { failure = error.InnerException; }
                        catch (Exception error) { failure = error; }
                        finally { completed.Set(); }
                    });
                    thread.IsBackground = true; thread.Start();
                    try {
                        if (!began.WaitOne(1000)) throw new IOException("Owned connection cancellation thread did not start.");
                        Thread.Sleep(30); broker.Disable();
                        check(completed.WaitOne(1000) && failure is IOException,
                            "The real connection wait exits within its bound when its generation is cancelled " + iteration);
                    } finally { server.Dispose(); thread.Join(1000); }
                    check(!broker.Active && !broker.HasUncertainOperation,
                        "Cancelled owned connection never admits a helper or invents authorized-mutation uncertainty " + iteration);
                }
            }
        }
        static void Send(Stream stream, int kind, int sequence, long[] values)
        { byte[] packet = AdministratorSupport.Encode(kind, sequence, values); stream.Write(packet, 0, packet.Length); stream.Flush(); }
        static long[] Receive(Stream stream, out int kind, out int sequence)
        {
            using (var timeout = new AdministratorSupport.DeadlineWatch(delegate { stream.Dispose(); }, 1000))
                return AdministratorSupport.Decode(stream, out kind, out sequence);
        }
        static void DeadlineDisposal(Action<bool, string> check)
        {
            using (var claimed = new ManualResetEvent(false)) using (var resume = new ManualResetEvent(false))
                using (var resumed = new ManualResetEvent(false)) {
                    int expired = 0;
                    using (var watch = new AdministratorSupport.DeadlineWatch(delegate { Interlocked.Increment(ref expired); }, 1,
                        delegate { claimed.Set(); resume.WaitOne(); }, delegate { resumed.Set(); })) {
                        try {
                            check(claimed.WaitOne(1000), "The actual deadline timer claims its callback before the deterministic completion barrier");
                            var elapsed = Stopwatch.StartNew(); watch.Dispose();
                            check(expired == 0 && elapsed.ElapsedMilliseconds < 500,
                                "Disposal cancels an already claimed but not executing expiry without waiting for that callback to resume");
                        } finally { resume.Set(); }
                        check(resumed.WaitOne(1000) && expired == 0,
                            "A claimed callback resumed after completion cannot mutate its old session");
                    }
                }
            using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false))
                using (var disposed = new ManualResetEvent(false)) {
                    int expired = 0; Exception failure = null;
                    using (var watch = new AdministratorSupport.DeadlineWatch(delegate {
                        entered.Set(); release.WaitOne(); Interlocked.Increment(ref expired);
                    }, 1)) {
                        Thread disposer = null;
                        try {
                            check(entered.WaitOne(1000), "A second actual deadline timer enters its fenced expiry action");
                            disposer = new Thread(delegate() {
                                try { watch.Dispose(); } catch (Exception error) { failure = error; } finally { disposed.Set(); }
                            }); disposer.IsBackground = true; disposer.Start();
                            check(!disposed.WaitOne(30) && expired == 0,
                                "Disposal cannot return while an in-progress expiry can still mutate the session");
                        } finally { release.Set(); }
                        check(disposed.WaitOne(1000) && failure == null && expired == 1,
                            "An already executing expiry finishes before disposal returns, with no lock inversion");
                        check(disposer == null || disposer.Join(1000), "The owned deadline disposal fixture leaves no blocked thread");
                    }
                }
        }
        static void FailureRetention(Action<bool, string> check)
        {
            var type = typeof(AdministratorSupport); const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var describe = type.GetMethod("FailureEvidence", flags); var fail = type.GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic);
            string first = (string)describe.Invoke(null, new object[] { "Administrator support did not respond.", "FocusDeadline", 17, -1,
                Stopwatch.GetTimestamp(), new IOException(@"Private C:\secret\file"), true });
            using (var broker = new AdministratorSupport()) {
                Set(broker, "generation", 7); Set(broker, "active", true);
                fail.Invoke(broker, new object[] { 7, first });
                string recorded = broker.LastError;
                object lease = Activator.CreateInstance(type.GetNestedType("Lease", BindingFlags.NonPublic), true);
                Set(lease, "Authorized", true); Set(lease, "Mutation", true); Set(broker, "operation", lease);
                fail.Invoke(broker, new object[] { 7, "A later generic EOF" });
                check(broker.LastError == recorded && recorded.Contains("FocusDeadline seq=17 op=-1") && recorded.Contains("deadline=1") &&
                    recorded.Contains("hr=0x") && recorded.Length < 250 && !recorded.Contains("secret") && broker.HasUncertainOperation && !broker.Active,
                    "Later EOF cannot overwrite the first bounded content-free phase evidence or erase authorized recovery uncertainty");
                Set(broker, "generation", 8); Set(broker, "operation", null);
                fail.Invoke(broker, new object[] { 7, "Stale generation" });
                check(broker.LastError == recorded, "A stale session cannot overwrite another generation's diagnostic evidence");
                fail.Invoke(broker, new object[] { 8, "Current generation failure" });
                check(broker.LastError == "Current generation failure", "A new generation can record its own first failure");
            }
            var stateType = type.GetNestedType("HelperState", BindingFlags.NonPublic);
            object state = Activator.CreateInstance(stateType, true);
            var stop = stateType.GetMethod("StopWith", BindingFlags.Instance | BindingFlags.NonPublic);
            stop.Invoke(state, new object[] { AdministratorSupport.HelperStopReason.Deadline, 0 });
            stop.Invoke(state, new object[] { AdministratorSupport.HelperStopReason.Transport, unchecked((int)0x80004005) });
            check((AdministratorSupport.HelperStopReason)Get(state, "StopReason") == AdministratorSupport.HelperStopReason.Deadline && (int)Get(state, "StopHResult") == 0,
                "The helper retains its first stop reason rather than a subsequent pipe-close exception");
            state = Activator.CreateInstance(stateType, true); Set(state, "Seen", Stopwatch.GetTimestamp());
            var heartbeat = stateType.GetMethod("HeartbeatExpired", BindingFlags.Instance | BindingFlags.NonPublic);
            check(!(bool)heartbeat.Invoke(state, null) && !(bool)Get(state, "Stop"),
                "The heartbeat expiry fence sees the latest accepted receipt without a stale earlier snapshot");
            Set(state, "Seen", Stopwatch.GetTimestamp() - 3 * Stopwatch.Frequency);
            check((bool)heartbeat.Invoke(state, null) && (AdministratorSupport.HelperStopReason)Get(state, "StopReason") == AdministratorSupport.HelperStopReason.HeartbeatExpired,
                "The unchanged2500ms heartbeat bound still stops an actually expired session");
        }
        // Only owned test state is injected, as in the existing broker tests.
        // Production starts this scheduler only after authenticating the HIGH
        // peer. This fake peer accepts Focus exclusively and cannot act on HWNDs.
        sealed class OwnedFocusPipe : IDisposable
        {
            internal readonly NamedPipeServerStream Server;
            internal readonly ManualResetEvent ReplyAllowed = new ManualResetEvent(true);
            readonly ManualResetEvent finished = new ManualResetEvent(false);
            readonly Thread reader;
            internal Exception Failure;
            int received;
            internal int Received { get { return Interlocked.CompareExchange(ref received, 0, 0); } }
            internal OwnedFocusPipe(bool reply = true)
            {
                string name = "TaskbarSelect.OwnedHeartbeat." + Guid.NewGuid().ToString("N");
                Server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512);
                if (!reply) ReplyAllowed.Reset();
                reader = new Thread(delegate() {
                    try {
                        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                            client.Connect(1000); int last = 0;
                            for (;;) {
                                int kind, number; long[] values = AdministratorSupport.Decode(client, out kind, out number);
                                if (kind != 3 || number <= last || !AdministratorSupport.ValidFocus(values)) throw new InvalidOperationException("The owned heartbeat peer received a non-focus or unordered packet.");
                                last = number; Interlocked.Increment(ref received);
                                if (!ReplyAllowed.WaitOne(4000)) throw new InvalidOperationException("The owned reply barrier was not released.");
                                Send(client, 11, number, new long[] { 0, 0 });
                            }
                        }
                    } catch (IOException) { /* Broker cancellation closes this owned pipe. */ }
                    catch (ObjectDisposedException) { }
                    catch (Exception error) { Failure = error; }
                    finally { finished.Set(); }
                });
                reader.IsBackground = true; reader.Start(); Connect(Server);
            }
            public void Dispose()
            {
                Server.Dispose(); ReplyAllowed.Set();
                if (!finished.WaitOne(1500) || !reader.Join(1000)) throw new IOException("The owned heartbeat peer did not finish.");
                ReplyAllowed.Dispose(); finished.Dispose();
            }
        }
        static void BindHeartbeat(AdministratorSupport broker, OwnedFocusPipe peer, int generation)
        {
            lock (Get(broker, "gate")) {
                Set(broker, "generation", generation); Set(broker, "pipe", peer.Server);
                Set(broker, "helper", OwnedLifetime()); Set(broker, "active", true); Set(broker, "focusSent", 0L);
            }
            typeof(AdministratorSupport).GetMethod("StartHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(broker, null);
        }
        static void IndependentHeartbeat(Action<bool, string> check)
        {
            using (var peer = new OwnedFocusPipe()) using (var broker = new AdministratorSupport()) {
                BindHeartbeat(broker, peer, 17);
                // Deliberately service no GUI messages and never call Poll or
                // UpdateFocus for longer than the helper's liveness allowance.
                Thread.Sleep(2800);
                check(peer.Received >= 6 && broker.Active && broker.LastError.Length == 0 && peer.Failure == null,
                    "Independent owned-pipe heartbeats continue beyond2500ms with no GUI Poll, render or message-loop progress");
                lock (Get(broker, "gate")) Set(broker, "setupPending", true);
                check(Wait(delegate { return !(bool)Get(broker, "focusQueued"); }, 1000), "An already admitted focus exchange settles before setup quiescence");
                int count = peer.Received; Thread.Sleep(350);
                check(peer.Received == count && broker.Active, "Independent scheduling sends no focus packet while setup owns the I/O admission gate");
                object lease = Activator.CreateInstance(typeof(AdministratorSupport).GetNestedType("Lease", BindingFlags.NonPublic), true);
                lock (Get(broker, "gate")) { Set(broker, "setupPending", false); Set(broker, "operation", lease); }
                Thread.Sleep(350);
                check(peer.Received == count && broker.Active, "Independent scheduling cannot interleave focus with a pending operation or invent an authorization");
                lock (Get(broker, "gate")) Set(broker, "operation", null);
                check(Wait(delegate { return peer.Received > count; }, 1000) && broker.Active,
                    "Heartbeats resume after operation quiescence without a GUI callback");
                broker.Disable(); count = peer.Received; Thread.Sleep(350);
                check(peer.Received == count && !broker.Active && !broker.HasUncertainOperation,
                    "Disable stops independent scheduling and closes only the owned pipe/query handle");
            }
        }
        static void HeartbeatLifecycle(Action<bool, string> check)
        {
            using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false))
                using (var returned = new ManualResetEvent(false)) {
                    int calls = 0;
                    using (var scheduler = new AdministratorSupport.HeartbeatScheduler(delegate {
                        Interlocked.Increment(ref calls); entered.Set(); release.WaitOne(); returned.Set();
                    })) {
                        scheduler.Start();
                        try {
                            check(entered.WaitOne(1000), "The independent heartbeat scheduler enters its owned deterministic callback");
                            Thread.Sleep(350);
                            check(calls == 1, "A blocked scheduler callback never overlaps another100ms callback");
                            var elapsed = Stopwatch.StartNew(); scheduler.Stop(); scheduler.Dispose();
                            check(elapsed.ElapsedMilliseconds < 500 && calls == 1,
                                "Stop and disposal cancel future callbacks without waiting on an already admitted callback");
                            scheduler.Start();
                        } finally { release.Set(); }
                        check(returned.WaitOne(1000), "The admitted owned callback can finish after disposal");
                        Thread.Sleep(250);
                        check(calls == 1, "A disposed heartbeat scheduler cannot be restarted or publish another callback");
                    }
                }
            using (var oldPeer = new OwnedFocusPipe(false)) using (var newPeer = new OwnedFocusPipe()) using (var broker = new AdministratorSupport()) {
                BindHeartbeat(broker, oldPeer, 31);
                check(Wait(delegate { return oldPeer.Received == 1; }, 1000), "The old generation reaches an owned focus-read barrier");
                broker.Disable(); BindHeartbeat(broker, newPeer, 33); oldPeer.ReplyAllowed.Set();
                check(Wait(delegate { return newPeer.Received >= 2; }, 1500) && broker.Active && broker.LastError.Length == 0 && newPeer.Failure == null,
                    "A cancelled generation's late EOF/reply cannot disable the next independently scheduled session");
                broker.Dispose(); int count = newPeer.Received; Thread.Sleep(350);
                check(newPeer.Received == count && !broker.Active,
                    "Disposal during a live heartbeat session prevents further packets and leaves no timer restart path");
            }
        }
        static void FocusDeadlinePhase(Action<bool, string> check)
        {
            using (var peer = new OwnedFocusPipe(false)) using (var broker = new AdministratorSupport()) {
                BindHeartbeat(broker, peer, 43);
                check(Wait(delegate { return peer.Received == 1; }, 1000), "The strict deadline fixture consumes the complete owned focus packet but withholds its reply");
                check(Wait(delegate { return !broker.Active; }, 3000) && broker.LastError.Contains("FocusDeadline seq=1 op=-1") &&
                    broker.LastError.Contains("deadline=1") && broker.LastError.Contains("io=Read"),
                    "The unchanged2000ms deadline reports the actual stalled read phase and retains its first cause");
                check(broker.LastError.Contains("read=1 header=0/4 body=0/0") && broker.LastError.Contains("avail=0") &&
                    broker.LastError.Contains("peek=1 sampled=1 peekWin32=0 peekHR=0x00000000"),
                    "A missing owned ACK is diagnosed as a pending header with no native pipe bytes, without reading its contents");
                string first = broker.LastError;
                typeof(AdministratorSupport).GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(broker, new object[] { 43, "Later EOF" });
                check(broker.LastError == first, "A later cancellation/EOF cannot replace the first deadline's atomic IO-phase evidence");
                peer.ReplyAllowed.Set();
            }
        }
        sealed class HeldFocusStream : MemoryStream
        {
            readonly int prefix;
            internal readonly ManualResetEvent Held = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            internal HeldFocusStream(byte[] packet, int prefix) : base(packet, false) { this.prefix = prefix; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position >= prefix) {
                    Held.Set(); Release.WaitOne(2000);
                    throw new IOException("Owned diagnostic read barrier ended.");
                }
                return base.Read(buffer, offset, Math.Min(count, prefix - (int)Position));
            }
            protected override void Dispose(bool disposing)
            { if (disposing) { Release.Set(); Held.Dispose(); Release.Dispose(); } base.Dispose(disposing); }
        }
        sealed class OwnedDiagnosticPipe : IDisposable
        {
            internal readonly NamedPipeServerStream Server;
            internal readonly NamedPipeClientStream Client;
            internal OwnedDiagnosticPipe(bool asynchronous)
            {
                string name = "TaskbarSelect.OwnedPipeAvailability." + Guid.NewGuid().ToString("N");
                Server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    asynchronous ? PipeOptions.Asynchronous : PipeOptions.None, 1024, 1024);
                Client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
                Exception failure = null;
                var connector = new Thread(delegate() { try { Client.Connect(1000); } catch (Exception error) { failure = error; } });
                connector.IsBackground = true; connector.Start(); Server.WaitForConnection();
                if (!connector.Join(1000) || failure != null) throw new IOException("The owned diagnostic pipe did not connect.", failure);
            }
            public void Dispose() { Client.Dispose(); Server.Dispose(); }
        }
        static void FocusTransportDiagnostics(Action<bool, string> check)
        {
            byte[] packet = AdministratorSupport.Encode(11, 7, new long[] { 0, 0 });
            var progress = new AdministratorSupport.FocusExchangePhase(); progress.Set(AdministratorSupport.FocusIoPhase.Read);
            int kind, number; long[] values;
            using (var stream = new MemoryStream(packet, false)) values = AdministratorSupport.Decode(stream, out kind, out number, progress);
            var complete = progress.Snapshot();
            check(kind == 11 && number == 7 && values.SequenceEqual(new long[] { 0, 0 }) &&
                complete.Stage == AdministratorSupport.FocusReadStage.Complete && complete.HeaderBytes == 4 && complete.BodyBytes == 24 && complete.BodyExpected == 24,
                "Healthy focused decoding retains the exact wire result and publishes bounded header/body completion");
            using (var stream = new MemoryStream(packet, false))
                check(AdministratorSupport.Decode(stream, out kind, out number).SequenceEqual(values) && kind == 11 && number == 7,
                    "Uninstrumented non-focus decoding preserves its existing signature and protocol behavior");
            foreach (int prefix in new[] { 0, 2, 4, 12 }) using (var stream = new HeldFocusStream(packet, prefix)) {
                var partial = new AdministratorSupport.FocusExchangePhase(); partial.Set(AdministratorSupport.FocusIoPhase.Read);
                Exception failure = null;
                var reader = new Thread(delegate() {
                    try { int tag, sequence; AdministratorSupport.Decode(stream, out tag, out sequence, partial); }
                    catch (Exception error) { failure = error; }
                }); reader.IsBackground = true; reader.Start();
                try {
                    check(stream.Held.WaitOne(1000), "The owned partial reply reaches its diagnostic barrier at " + prefix + " bytes");
                    var snapshot = partial.Snapshot();
                    check(snapshot.Stage == (prefix < 4 ? AdministratorSupport.FocusReadStage.Header : AdministratorSupport.FocusReadStage.Body) &&
                        snapshot.HeaderBytes == Math.Min(4, prefix) && snapshot.BodyBytes == Math.Max(0, prefix - 4) && snapshot.BodyExpected == (prefix < 4 ? 0 : 24),
                        "Missing, partial-header and partial-body replies report only successful bounded read counts at " + prefix + " bytes");
                } finally { stream.Release.Set(); }
                check(reader.Join(1000) && failure is IOException && partial.Snapshot().Stage != AdministratorSupport.FocusReadStage.Complete,
                    "Diagnostic progress cannot convert an incomplete or failed owned read into a complete packet");
            }
            progress.BeginRead(true, Int32.MaxValue); progress.ReadProgress(false, Int32.MaxValue); progress.ReadProgress(true, Int32.MaxValue);
            var bounded = progress.Snapshot();
            check(bounded.HeaderBytes == 4 && bounded.BodyBytes == 128 && bounded.BodyExpected == 128,
                "Advisory read counts and expected body bytes remain capped even for out-of-range diagnostic input");
            using (var pipe = new OwnedDiagnosticPipe(true)) {
                pipe.Client.Write(new byte[600], 0, 600);
                var first = AdministratorSupport.SampleFocusAvailability(pipe.Server);
                var second = AdministratorSupport.SampleFocusAvailability(pipe.Server);
                check(first.Sampled && first.Success && first.Available == 512 && first.Capped && first.NativeError == 0 && first.HResult == 0 &&
                    second.Sampled && second.Success && second.Available == first.Available && second.Capped,
                    "Available-only Peek caps its numeric evidence and leaves the exact owned pipe's unread bytes untouched");
                // Independently drain only our known fixture bytes before
                // closing its peer. Windows retains unread data after close;
                // that state is readable data rather than a Peek failure.
                var owned = new byte[600]; int consumed = 0;
                while (consumed < owned.Length) consumed += pipe.Server.Read(owned, consumed, owned.Length - consumed);
                check(consumed == 600, "The optional sampler left all owned fixture bytes available for their ordinary reader");
                pipe.Client.Dispose();
                var broken = AdministratorSupport.SampleFocusAvailability(pipe.Server);
                check(broken.Sampled && !broken.Success && broken.NativeError != 0 && broken.HResult == 0 && broken.Available == 0,
                    "A failed native Peek retains its actual Win32 error separately from managed HRESULT evidence");
                pipe.Server.Dispose();
                var closed = AdministratorSupport.SampleFocusAvailability(pipe.Server);
                check(!closed.Success && closed.HResult != 0 && closed.Available == 0,
                    "A concurrently unavailable or closed exact handle reports bounded advisory failure without a reopen or native read");
            }
            using (var pipe = new OwnedDiagnosticPipe(false)) {
                var sync = AdministratorSupport.SampleFocusAvailability(pipe.Server);
                check(!sync.Sampled && !sync.Success && sync.NativeError == 0 && sync.HResult == 0,
                    "A synchronous pipe is never passed to the optional Peek sampler that could block its concurrent reader");
            }
            using (var support = new AdministratorSupport()) using (var pipe = new OwnedDiagnosticPipe(true)) {
                Set(support, "generation", 83); Set(support, "active", true); Set(support, "pipe", pipe.Server);
                string stale = (string)Call(support, "FocusDeadlineDiagnostic", 82, pipe.Server, progress);
                check(stale.Contains("stale=1") && !stale.Contains("sampled=1") && support.Active && support.LastError.Length == 0,
                    "A stale diagnostic generation neither samples the new session nor changes its admission or failure state");
                pipe.Server.Dispose();
                string unavailable = (string)Call(support, "FocusDeadlineDiagnostic", 83, pipe.Server, progress);
                check(unavailable.Contains("peek=0") && unavailable.Contains("peekHR=0x") && support.Active && support.LastError.Length == 0,
                    "Even an exact-session diagnostic failure has no authority to disconnect, authorize, clear recovery or replace a first cause");
            }
        }
        static void FocusBetweenRequests(Action<bool, string> check)
        {
            string name = "TaskbarSelect.OwnedFocusBrokerTest." + Guid.NewGuid().ToString("N");
            Exception failure = null; int received = 0; bool ordered = true;
            using (var finished = new ManualResetEvent(false)) using (var broker = new AdministratorSupport())
                using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)) {
                    var clientThread = new Thread(delegate() {
                        try {
                            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) {
                                client.Connect(1000); int last = 0;
                                for (int index = 0; index < 5; index++) {
                                    int kind, number; long[] values = Receive(client, out kind, out number);
                                    ordered &= number > last; last = number; Interlocked.Increment(ref received);
                                    if (index % 2 == 0) {
                                        if (kind != 3 || !AdministratorSupport.ValidFocus(values)) throw new IOException();
                                        Send(client, 11, number, new long[] { 0, 0 });
                                    } else {
                                        if (kind != 1 || values.Length != (index == 1 ? 10 : 15) || values[0] != (index == 1 ? 0 : 1)) throw new IOException();
                                        Send(client, 12, number, new long[] { 1, 0, 1, 0, 0, 0, 0, 0 });
                                    }
                                }
                            }
                        } catch (Exception error) { failure = error; }
                        finally { finished.Set(); }
                    }); clientThread.IsBackground = true; clientThread.Start(); Connect(server);
                    Set(broker, "pipe", server); Set(broker, "active", true); Set(broker, "helper", OwnedLifetime());
                    var queueFocus = typeof(AdministratorSupport).GetMethod("QueueFocus", BindingFlags.Instance | BindingFlags.NonPublic);
                    uint pid = (uint)Process.GetCurrentProcess().Id; long started = NativeProcessIdentity.Started(pid);
                    for (int index = 0; index < 3; index++) {
                        Set(broker, "focusSent", 0L); queueFocus.Invoke(broker, null);
                        check(Wait(delegate { return !(bool)Get(broker, "focusQueued") && (long)Get(broker, "focusSent") != 0; }, 1000) && broker.Active,
                            "The real broker completes a healthy focus exchange before/between/after movement-shaped rejection " + index);
                        if (index == 2) break;
                        int[] arguments = index == 0 ? new[] { 0, 0, 320, 240, 0x214 } : new[] { 0, 3, -1, -1, -1, -1, -2560, -1080, -1920, -600 };
                        var rejected = broker.Execute(index, new IntPtr(19), pid, started, Stopwatch.GetTimestamp() + Stopwatch.Frequency,
                            delegate { return true; }, arguments);
                        check(rejected.Outcome == WindowMoveWorker.Outcome.Rejected && rejected.CallerExited && !rejected.MutationAttempted &&
                            broker.Active && !broker.HasUncertainOperation && broker.LastError.Length == 0,
                            "A rejected position/placement request retains the authenticated focus session without authorization " + index);
                    }
                    check(finished.WaitOne(1000) && failure == null && received == 5 && ordered,
                        "Actual focus and operation packets remain strictly ordered through the broker's shared I/O fence");
                }
        }
        static bool SequenceFollows(int previous, int current)
        { return current > previous || (previous == Int32.MaxValue && current == 1); }
        static void FocusSnapRequestOrdering(Action<bool, string> check)
        {
            // Hold the actual focus exchange's ioGate until Execute has
            // admitted a lease. The snap poll must then take a wire sequence
            // before the request does. Reserving at lease admission instead
            // deterministically produces Focus101, Snap103, Request102.
            foreach (int initialSequence in new[] { 100, Int32.MaxValue - 2 }) foreach (bool cancel in new[] { false, true }) {
                string name = "TaskbarSelect.OwnedFocusSnapOrder." + Guid.NewGuid().ToString("N");
                Exception failure = null; int cancelRequested = 0, authorization = -1;
                bool admittedWhileFocusHeld = false; int[] wire = new int[5];
                using (var focusSeen = new ManualResetEvent(false)) using (var followup = new ManualResetEvent(false))
                    using (var finished = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false))
                    using (var broker = new AdministratorSupport())
                    using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)) {
                        var peer = new Thread(delegate() {
                            try {
                                using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) {
                                    client.Connect(1000); int kind, number;
                                    long[] values = Receive(client, out kind, out number);
                                    if (kind != 3 || !AdministratorSupport.ValidFocus(values)) throw new IOException("Expected initial owned focus packet.");
                                    wire[0] = number; focusSeen.Set();
                                    object gate = Get(broker, "gate");
                                    if (!Wait(delegate { lock (gate) return Get(broker, "operation") != null && (int)Get(broker, "pendingOperations") == 1; }, 1000))
                                        throw new IOException("Execute did not admit its lease behind the held focus exchange.");
                                    admittedWhileFocusHeld = true;
                                    Send(client, 11, wire[0], new long[] { 0, 0 });
                                    values = Receive(client, out kind, out number); wire[1] = number;
                                    if (kind != 20 || !SequenceFollows(wire[0], number) || !AdministratorSnapProtocol.ValidConfiguration(values, Stopwatch.GetTimestamp()))
                                        throw new IOException("Expected an ordered snap poll before the pending request.");
                                    Send(client, 21, number, new long[AdministratorSnapProtocol.ReplyLength]);
                                    values = Receive(client, out kind, out number); wire[2] = number;
                                    if (kind != 1 || !SequenceFollows(wire[1], number) || values.Length != 10 || values[0] != 0)
                                        throw new IOException("Request sequence regressed behind the preceding snap poll.");
                                    if (cancel) Interlocked.Exchange(ref cancelRequested, 1);
                                    Send(client, 10, number, new long[0]);
                                    int requestNumber = number;
                                    values = Receive(client, out kind, out number);
                                    if (kind != 2 || number != requestNumber || values.Length != 1 || values[0] != (cancel ? 0 : 1))
                                        throw new IOException("READY did not receive its exact same-sequence authorization decision.");
                                    authorization = (int)values[0];
                                    // Synthetic terminal receipts only: this
                                    // strict peer never dispatches USER work.
                                    Send(client, 12, number, new long[] { (long)(cancel ? WindowMoveWorker.Outcome.Cancelled : WindowMoveWorker.Outcome.Success),
                                        cancel ? 0 : 1, 1, 0, 0, 0, 0, 0 });
                                    values = Receive(client, out kind, out number); wire[3] = number;
                                    if (kind != 3 || !SequenceFollows(wire[2], number) || !AdministratorSupport.ValidFocus(values))
                                        throw new IOException("Follow-up focus lost the original ordered connection.");
                                    Send(client, 11, number, new long[] { 0, 0 });
                                    values = Receive(client, out kind, out number); wire[4] = number;
                                    if (kind != 20 || !SequenceFollows(wire[3], number) || !AdministratorSnapProtocol.ValidConfiguration(values, Stopwatch.GetTimestamp()))
                                        throw new IOException("Follow-up snap poll lost the original ordered connection.");
                                    Send(client, 21, number, new long[AdministratorSnapProtocol.ReplyLength]);
                                    followup.Set();
                                    if (!release.WaitOne(2000)) throw new IOException("Owned ordered peer was not released after verification.");
                                }
                            } catch (Exception error) { failure = error; }
                            finally { focusSeen.Set(); followup.Set(); finished.Set(); }
                        }); peer.IsBackground = true; peer.Start();
                        try {
                            Connect(server);
                            Set(broker, "pipe", server); Set(broker, "active", true); Set(broker, "helper", OwnedLifetime());
                            Set(broker, "generation", 71); Set(broker, "sequence", initialSequence);
                            Set(broker, "snapInputConfigured", true); // Disabled scope0 still requires a snap exchange.
                            var queueFocus = typeof(AdministratorSupport).GetMethod("QueueFocus", BindingFlags.Instance | BindingFlags.NonPublic);
                            queueFocus.Invoke(broker, null);
                            int[] expected = initialSequence == 100 ? new[] { 101, 102, 103, 104, 105 } : new[] { Int32.MaxValue - 1, Int32.MaxValue, 1, 2, 3 };
                            string fixture = " at cancel=" + cancel + ", initialSequence=" + initialSequence;
                            check(focusSeen.WaitOne(1000) && failure == null && wire[0] == expected[0] && (bool)Get(broker, "focusQueued"),
                                "Actual focus holds the I/O fence before Execute admission" + fixture);
                            uint pid = (uint)Process.GetCurrentProcess().Id;
                            var result = broker.Execute(0, new IntPtr(19), pid, NativeProcessIdentity.Started(pid),
                                Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency,
                                delegate { return Interlocked.CompareExchange(ref cancelRequested, 0, 0) == 0; }, new[] { 0, 0, 320, 240, 0x214 });
                            check(admittedWhileFocusHeld && wire.Take(3).SequenceEqual(expected.Take(3)) && failure == null,
                                "A lease admitted behind focus sends its request after the intervening snap poll, including wrap" + fixture);
                            check(authorization == (cancel ? 0 : 1) && result.Outcome == (cancel ? WindowMoveWorker.Outcome.Cancelled : WindowMoveWorker.Outcome.Success) &&
                                result.CallerExited && result.MutationAttempted == !cancel && broker.Active && !broker.HasUncertainOperation && broker.LastError.Length == 0,
                                "Ordered request preserves READY and exact authorize/cancel terminal semantics" + fixture);
                            check(Wait(delegate { return !(bool)Get(broker, "focusQueued"); }, 1000), "Initial owned focus/snap worker drains" + fixture);
                            Set(broker, "focusSent", 0L); queueFocus.Invoke(broker, null);
                            check(followup.WaitOne(1000) && failure == null && wire.SequenceEqual(expected) &&
                                Wait(delegate { return !(bool)Get(broker, "focusQueued"); }, 1000) && broker.Active && !finished.WaitOne(0) &&
                                (int)Get(broker, "generation") == 71 && (int)Get(broker, "pendingOperations") == 0 && Get(broker, "operation") == null,
                                "Original session and owned peer stay live for ordered focus/snap traffic after the request" + fixture);
                        } finally {
                            Set(broker, "active", false); release.Set(); server.Dispose();
                            if (!finished.WaitOne(2000) || !peer.Join(1000) || !Wait(delegate { return (int)Get(broker, "pendingOperations") == 0 && !(bool)Get(broker, "focusQueued"); }, 2000))
                                throw new IOException("The owned focus/snap ordering fixture did not drain.");
                        }
                    }
            }
        }
        static void ExpiredServeRequests(Action<bool, string> check)
        {
            string name = "TaskbarSelect.OwnedServeDeadlineTest." + Guid.NewGuid().ToString("N");
            object state = null; Exception failure = null;
            var type = typeof(AdministratorSupport); var stateType = type.GetNestedType("HelperState", BindingFlags.NonPublic);
            const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
            using (var ready = new ManualResetEvent(false)) using (var finished = new ManualResetEvent(false))
                using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)) {
                    var clientThread = new Thread(delegate() {
                        try {
                            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) {
                                client.Connect(1000); state = Activator.CreateInstance(stateType, true);
                                Set(state, "Parent", type.GetMethod("ReadPeer", methods).Invoke(null, new object[] { (uint)Process.GetCurrentProcess().Id }));
                                Set(state, "Pipe", client); Set(state, "Seen", Stopwatch.GetTimestamp()); ready.Set();
                                type.GetMethod("Serve", methods).Invoke(null, new[] { state });
                            }
                        } catch (Exception error) { failure = error; }
                        finally { ready.Set(); finished.Set(); }
                    }); clientThread.IsBackground = true; clientThread.Start(); Connect(server);
                    check(ready.WaitOne(1000) && state != null && Get(state, "Parent") != null,
                        "The actual helper serve fixture uses only owned current-process metadata and a local pipe");
                    uint pid = (uint)Process.GetCurrentProcess().Id; long started = NativeProcessIdentity.Started(pid);
                    for (int index = 0; index < 5; index++) {
                        bool focus = index % 2 == 0; int number = index + 1;
                        long[] values = focus ? new long[] { 2, 0, 0, 1, 3, 1, 0 } : index == 1 ?
                            new long[] { 0, 19, pid, started, Stopwatch.GetTimestamp() - 1, 0, 0, 320, 240, 0x214 } :
                            new long[] { 1, 19, pid, started, Stopwatch.GetTimestamp() - 1, 0, 3, -1, -1, -1, -1, -2560, -1080, -1920, -600 };
                        Send(server, focus ? 3 : 1, number, values);
                        int kind, id; long[] reply = Receive(server, out kind, out id);
                        if (focus) check(kind == 11 && id == number && reply.Length == 2 && !(bool)Get(state, "Stop"),
                            "Actual Serve retains healthy focus before/between/after expired movement requests " + index);
                        else {
                            WindowMoveWorker.Result result; int workers, pending;
                            check(kind == 12 && id == number && AdministratorSupport.ParseResult(reply, out result, out workers, out pending) &&
                                AdministratorSupport.PreauthorizationRejection(result) && !(bool)Get(state, "Stop"),
                                "An actually expired position/placement packet receives a terminal rejection without READY, windows or session shutdown " + index);
                        }
                    }
                    Send(server, 3, 5, new long[] { 2, 0, 0, 1, 3, 1, 0 });
                    check(finished.WaitOne(1000) && failure == null &&
                        (AdministratorSupport.HelperStopReason)Get(state, "StopReason") == AdministratorSupport.HelperStopReason.ProtocolOrder &&
                        (int)Get(state, "StopHResult") != 0,
                        "A real duplicate-sequence fault still fails closed and preserves its fixed helper stop reason and numeric HRESULT");
                }
        }
        static void SnapServeRequests(Action<bool, string> check)
        {
            // Invoke Serve alone, never TryRun/HelperContext: these owned
            // pipes exercise real packet dispatch without constructing UI or
            // an OwnedSnapKeyboard, observing input, or launching a process.
            foreach (string fault in new[] { "duplicate", "out-of-order", "arity", "boolean" }) {
                string name = "TaskbarSelect.OwnedServeSnapTest." + Guid.NewGuid().ToString("N");
                object state = null; Exception failure = null;
                var type = typeof(AdministratorSupport); var stateType = type.GetNestedType("HelperState", BindingFlags.NonPublic);
                const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
                using (var ready = new ManualResetEvent(false)) using (var finished = new ManualResetEvent(false))
                    using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)) {
                        var clientThread = new Thread(delegate() {
                            try {
                                using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) {
                                    client.Connect(1000); state = Activator.CreateInstance(stateType, true);
                                    Set(state, "Parent", type.GetMethod("ReadPeer", methods).Invoke(null, new object[] { (uint)Process.GetCurrentProcess().Id }));
                                    Set(state, "Pipe", client); Set(state, "Seen", Stopwatch.GetTimestamp()); ready.Set();
                                    type.GetMethod("Serve", methods).Invoke(null, new[] { state });
                                }
                            } catch (Exception error) { failure = error; }
                            finally { ready.Set(); finished.Set(); }
                        }); clientThread.IsBackground = true; clientThread.Start();
                        try {
                            Connect(server);
                            check(ready.WaitOne(1000) && state != null && Get(state, "Parent") != null,
                                "Snap Serve fixture binds owned current-process metadata without helper UI at " + fault);
                            var input = (AdministratorSnapInput)Get(state, "SnapInput");
                            int sequence = 0;
                            Action<long[], string> poll = delegate(long[] configuration, string message) {
                                int sent = ++sequence; Send(server, 20, sent, configuration);
                                int kind, id; long[] reply = Receive(server, out kind, out id);
                                check(kind == 21 && id == sent && reply.Length == AdministratorSnapProtocol.ReplyLength &&
                                    AdministratorSnapProtocol.ValidReply(reply) && reply[2] == 0 && reply[3] == 0 && !(bool)Get(state, "Stop"), message);
                            };
                            Action<string> focus = delegate(string message) {
                                int sent = ++sequence; long[] configuration = { 2, 0, 0, 1, 3, 1, 0 };
                                Send(server, 3, sent, configuration);
                                int kind, id; long[] reply = Receive(server, out kind, out id);
                                check(kind == 11 && id == sent && reply.Length == 2 && reply.All(value => value >= 0) &&
                                    ((int[])Get(state, "Focus")).SequenceEqual(configuration.Select(value => (int)value)) && !(bool)Get(state, "Stop"), message);
                            };
                            poll(new long[AdministratorSnapProtocol.ConfigurationLength], "Disabled scope0 returns the fixed snap receipt with no arm or observation at " + fault);
                            check(!input.CanClaim() && Get(input, "published") == null && Get(input, "pending") == null &&
                                ((long[])Get(input, "configuration")).All(value => value == 0),
                                "Disabled Serve scope contains no input publication, pending notice, target or lease at " + fault);
                            focus("Normal seven-field focus still returns its original two-field receipt at " + fault);
                            if (fault == "duplicate") {
                                poll(new long[] { 1, 1, 0, 0, 0, Stopwatch.GetTimestamp() + Stopwatch.Frequency, 0, 0 },
                                    "Enabled epoch1 with no target stays unarmed through Serve without a hook or UI");
                                check(!input.CanClaim() && Get(input, "published") == null && ((long[])Get(input, "configuration"))[1] == 1,
                                    "A targetless configuration does not create a native input publication");
                                focus("Focus remains healthy between enabled and expired snap polls");
                                poll(new long[] { 1, 2, 0, 0, 0, Stopwatch.GetTimestamp() - 1, 0, 0 },
                                    "Expired well-formed epoch2 produces a safe empty snap receipt and retains the connection");
                                long[] expired = (long[])Get(input, "configuration");
                                check(expired.SequenceEqual(new long[] { 0, 2, 0, 0, 0, 0, 0, 0 }) && !input.CanClaim(),
                                    "Actual Serve converts an expired configuration to disabled while preserving its epoch");
                                poll(new long[] { 1, 3, 0, 0, 0, Stopwatch.GetTimestamp() + Stopwatch.Frequency, 0, 0 },
                                    "A fresh valid epoch3 snap poll succeeds on the original connection after expiry");
                                check(((long[])Get(input, "configuration"))[1] == 3 && !input.CanClaim() && Get(input, "pending") == null,
                                    "Fresh configuration after expiry retains the new scope without manufacturing an observation");
                                focus("Focus retains its original reply shape after expiry and fresh snap reconfiguration");
                            }
                            int badSequence = fault == "duplicate" ? sequence : fault == "out-of-order" ? sequence - 1 : sequence + 1;
                            long[] malformed = fault == "arity" ? new long[AdministratorSnapProtocol.ConfigurationLength - 1] :
                                fault == "boolean" ? new long[] { 2, 0, 0, 0, 0, 0, 0, 0 } : new long[AdministratorSnapProtocol.ConfigurationLength];
                            Send(server, 20, badSequence, malformed);
                            var expected = fault == "duplicate" || fault == "out-of-order" ?
                                AdministratorSupport.HelperStopReason.ProtocolOrder : AdministratorSupport.HelperStopReason.ProtocolPacket;
                            check(finished.WaitOne(1000) && failure == null && (bool)Get(state, "Stop") &&
                                (AdministratorSupport.HelperStopReason)Get(state, "StopReason") == expected &&
                                (int)Get(state, "StopHResult") != 0 && (bool)Get(state, "CleanupComplete") &&
                                !input.CanClaim() && Get(input, "pending") == null,
                                "Actual snap dispatch fails closed with its fixed stop reason and no pending observation at " + fault);
                        } finally {
                            server.Dispose();
                            if (!clientThread.Join(1000)) throw new IOException("The owned snap Serve thread did not finish.");
                        }
                    }
            }
        }
        static void RejectionThenSuccess(Action<bool, string> check)
        {
            string name = "TaskbarSelect.OwnedBrokerTest." + Guid.NewGuid().ToString("N");
            Exception failure = null; bool authorized = false;
            using (var finished = new ManualResetEvent(false)) using (var broker = new AdministratorSupport())
                using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)) {
                    // Only test state is injected. Production Enable() always
                    // authenticates the elevated peer before publishing Active.
                    var clientThread = new Thread(delegate() {
                        try {
                            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) {
                                client.Connect(1000);
                                for (int request = 0; request < 2; request++) {
                                    int kind, number; long[] data = AdministratorSupport.Decode(client, out kind, out number);
                                    if (kind != 1 || data.Length != 10) throw new IOException();
                                    if (request == 0) Send(client, 12, number, new long[] { 1, 0, 1, 0, 0, 0, 0, 0 });
                                    else {
                                        Send(client, 10, number, new long[0]);
                                        int action, id; long[] token = AdministratorSupport.Decode(client, out action, out id);
                                        authorized = action == 2 && id == number && token.Length == 1 && token[0] == 1;
                                        if (!authorized) throw new IOException();
                                        Send(client, 12, number, new long[] { 0, 1, 1, 0, 0, 0, 0, 0 });
                                    }
                                }
                            }
                        } catch (Exception error) { failure = error; }
                        finally { finished.Set(); }
                    });
                    clientThread.IsBackground = true; clientThread.Start(); server.WaitForConnection();
                    Set(broker, "pipe", server); Set(broker, "active", true);
                    Set(broker, "helper", OwnedLifetime());
                    uint pid = (uint)Process.GetCurrentProcess().Id; long started = NativeProcessIdentity.Started(pid);
                    int[] values = { 0, 0, 320, 240, 0x214 };
                    long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    var rejected = broker.Execute(0, new IntPtr(19), pid, started, deadline, delegate { return true; }, values);
                    check(rejected.Outcome == WindowMoveWorker.Outcome.Rejected && !rejected.MutationAttempted && rejected.CallerExited && broker.Active &&
                        !broker.HasUncertainOperation && !authorized, "A prelaunch terminal rejection retains the authenticated session without authorizing work");
                    deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    var success = broker.Execute(0, new IntPtr(19), pid, started, deadline, delegate { return true; }, values);
                    check(finished.WaitOne(1000) && failure == null && authorized && success.Outcome == WindowMoveWorker.Outcome.Success &&
                        success.CallerExited && broker.Active && !broker.HasUncertainOperation,
                        "A healthy request succeeds in the same owned session after rejection through READY and fresh main authorization");
                }
        }
        enum OperationBarrier { BeforeReady, BeforeAuthorize, AfterAuthorize, Completed, Uncertain, Malformed }
        sealed class OwnedOperationPeer : IDisposable
        {
            internal readonly NamedPipeServerStream Server;
            internal readonly ManualResetEvent RequestSeen = new ManualResetEvent(false), AuthorizationSeen = new ManualResetEvent(false),
                ValidityEntered = new ManualResetEvent(false), Finished = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            readonly Thread reader;
            readonly OperationBarrier barrier;
            internal Exception Failure;
            internal bool Authorized;
            internal OwnedOperationPeer(OperationBarrier barrier)
            {
                this.barrier = barrier;
                string name = "TaskbarSelect.OwnedOperationCancellation." + Guid.NewGuid().ToString("N");
                Server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                reader = new Thread(delegate() {
                    try {
                        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) {
                            client.Connect(1000); int kind, number;
                            long[] values = AdministratorSupport.Decode(client, out kind, out number);
                            if (kind != 1 || values.Length != 15 || values[0] != 1) throw new IOException("Owned placement request was not exact.");
                            RequestSeen.Set();
                            if (barrier == OperationBarrier.BeforeReady && !ValidityEntered.WaitOne(1000)) throw new IOException("The pre-READY validity barrier was not reached.");
                            Send(client, 10, number, new long[0]);
                            try {
                                int action, id; long[] token = AdministratorSupport.Decode(client, out action, out id);
                                if (action == 2 && id == number && token.Length == 1 && token[0] == 0 &&
                                    (barrier == OperationBarrier.BeforeReady || barrier == OperationBarrier.BeforeAuthorize)) {
                                    Send(client, 12, number, new long[] { 4, 0, 1, 0, 0, 0, 0, 0 });
                                    Release.WaitOne(1500); return;
                                }
                                if (barrier == OperationBarrier.BeforeReady || barrier == OperationBarrier.BeforeAuthorize)
                                    throw new InvalidOperationException("Cancellation authorized an owned request before its admission barrier.");
                                if (action != 2 || id != number || token.Length != 1 || token[0] != 1) throw new IOException();
                                Authorized = true; AuthorizationSeen.Set();
                                if (barrier == OperationBarrier.AfterAuthorize) {
                                    AdministratorSupport.Decode(client, out action, out id);
                                    throw new InvalidOperationException("The cancelled owned operation unexpectedly received another packet.");
                                }
                                if (!ValidityEntered.WaitOne(1000)) throw new IOException("The owned terminal reply missed its validity barrier.");
                                long[] reply = barrier == OperationBarrier.Uncertain ? new long[] { 3, 1, 0, 0, 0, 0, 1, 1 } :
                                    barrier == OperationBarrier.Malformed ? new long[] { 0, 1, 0, 0, 0, 0, 0, 0 } : new long[] { 0, 1, 1, 0, 0, 0, 0, 0 };
                                Send(client, 12, number, reply);
                                Release.WaitOne(1500);
                            } catch (IOException) {
                                if (barrier != OperationBarrier.BeforeReady && barrier != OperationBarrier.BeforeAuthorize && barrier != OperationBarrier.AfterAuthorize) throw;
                            }
                        }
                    } catch (Exception error) { Failure = error; }
                    finally { Finished.Set(); }
                });
                reader.IsBackground = true; reader.Start(); Connect(Server);
            }
            public void Dispose()
            {
                Release.Set(); Server.Dispose();
                if (!Finished.WaitOne(2000) || !reader.Join(1000)) throw new IOException("The owned cancellation peer did not stop.");
                RequestSeen.Dispose(); AuthorizationSeen.Dispose(); ValidityEntered.Dispose(); Finished.Dispose(); Release.Dispose();
            }
        }
        sealed class OperationValidity : AdministratorSupport.IValidityDiagnostic
        {
            internal readonly AdministratorSupport Broker;
            internal readonly OwnedOperationPeer Peer;
            internal readonly OperationBarrier Barrier;
            internal object Lease;
            internal int Calls;
            internal bool TerminalWasComplete;
            internal OperationValidity(AdministratorSupport broker, OwnedOperationPeer peer, OperationBarrier barrier)
            { Broker = broker; Peer = peer; Barrier = barrier; }
            public int LastValidityReason { get { return 7; } }
            internal bool Current()
            {
                if (++Calls == 1) return true;
                Lease = Get(Broker, "operation");
                if (Lease == null) throw new InvalidOperationException("The cancellation fixture has no admitted lease.");
                if (Barrier == OperationBarrier.BeforeReady) {
                    if (!Peer.RequestSeen.WaitOne(1000)) throw new IOException("The owned request did not reach its pre-READY barrier.");
                    Peer.ValidityEntered.Set();
                    return false;
                }
                if (Barrier == OperationBarrier.BeforeAuthorize) {
                    if (!Wait(delegate { return (bool)Get(Lease, "Ready"); }, 1000)) throw new IOException("The owned request did not reach READY.");
                    return false;
                }
                if (!Peer.AuthorizationSeen.WaitOne(0)) return true;
                if (Barrier == OperationBarrier.AfterAuthorize) return false;
                // Force the real caller-validity check to overlap an actual
                // validated terminal receipt. No USER call, privilege or GUI
                // focus is used to trigger this completion/cancellation race.
                Peer.ValidityEntered.Set();
                if (!Wait(delegate { return (bool)Get(Lease, "Complete"); }, 1000)) throw new IOException("The real terminal completion was not published.");
                TerminalWasComplete = true;
                return false;
            }
        }
        sealed class DiagnosticOnlyValidity : AdministratorSupport.IValidityDiagnostic
        {
            internal int Reason;
            internal bool Throw;
            public int LastValidityReason { get { if (Throw) throw new IOException("Owned advisory failure"); return Reason; } }
            internal bool Current() { return false; }
        }
        static void OperationCancellation(Action<bool, string> check)
        {
            foreach (OperationBarrier barrier in Enum.GetValues(typeof(OperationBarrier)))
                using (var peer = new OwnedOperationPeer(barrier)) using (var broker = new AdministratorSupport()) {
                    Set(broker, "pipe", peer.Server); Set(broker, "active", true); Set(broker, "helper", OwnedLifetime());
                    Set(broker, "focusQueued", true); // This fixture exercises operation admission only.
                    var validity = new OperationValidity(broker, peer, barrier);
                    uint pid = (uint)Process.GetCurrentProcess().Id; long started = NativeProcessIdentity.Started(pid);
                    var result = broker.Execute(1, new IntPtr(19), pid, started, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2,
                        validity.Current, new[] { 0, 3, -1, -1, -1, -1, 0, 0, 320, 240 });
                    peer.Release.Set();
                    check(peer.Finished.WaitOne(1000) && peer.Failure == null && validity.Lease != null &&
                        Wait(delegate { return (bool)Get(validity.Lease, "Complete"); }, 1000) && Get(broker, "operation") == null,
                        "The real owned operation settles its callback and clears admission at " + barrier);
                    bool early = barrier == OperationBarrier.BeforeReady || barrier == OperationBarrier.BeforeAuthorize;
                    if (early) {
                        check(result.Outcome == WindowMoveWorker.Outcome.Cancelled && !result.MutationAttempted && result.CallerExited && !peer.Authorized &&
                            broker.Active && !broker.HasUncertainOperation && (bool)Get(validity.Lease, "Terminal") && broker.LastError.Length == 0,
                            "A negative authorization consumes its no-mutation terminal receipt and retains the healthy session at " + barrier);
                    } else if (barrier == OperationBarrier.AfterAuthorize) {
                        check(result.Outcome == WindowMoveWorker.Outcome.Cancelled && result.MutationAttempted == !early && result.CallerExited == early &&
                            peer.Authorized == !early && !broker.Active && broker.HasUncertainOperation == !early && !(bool)Get(validity.Lease, "Terminal"),
                            "Actual cancellation preserves original authorization, attempted-mutation and uncertainty fences at " + barrier);
                        string first = broker.LastError;
                        check(first.Contains("MainValidity seq=1 op=1") && first.Contains("validity=7") &&
                            first.Contains("ready=" + (barrier == OperationBarrier.BeforeReady ? "0" : "1")) &&
                            first.Contains("authorized=" + (early ? "0" : "1")) && first.Contains("terminal=0 complete=0"),
                            "Cancellation records only bounded numeric validity and the actual admission state at " + barrier);
                        typeof(AdministratorSupport).GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(broker, new object[] { 0, "Late owned cancellation" });
                        check(broker.LastError == first, "Later owned transport/cancellation evidence cannot replace the first cause at " + barrier);
                    } else if (barrier == OperationBarrier.Completed) {
                        check(validity.TerminalWasComplete && peer.Authorized && (bool)Get(validity.Lease, "Terminal") &&
                            result.Outcome == WindowMoveWorker.Outcome.Success && result.MutationAttempted && result.CallerExited &&
                            broker.Active && !broker.HasUncertainOperation && broker.LastError.Length == 0,
                            "An already authenticated safe terminal completion wins the caller-validity publication race without a false disconnect");
                    } else if (barrier == OperationBarrier.Uncertain) {
                        check(validity.TerminalWasComplete && peer.Authorized && (bool)Get(validity.Lease, "Terminal") &&
                            result.Outcome == WindowMoveWorker.Outcome.TimedOut && result.MutationAttempted && !result.CallerExited &&
                            broker.Active && broker.HasUncertainOperation && broker.Status == "Recovery pending",
                            "Atomic terminal publication never promotes an unconfirmed mutation to success or clears its retained recovery fence");
                    } else {
                        check(validity.TerminalWasComplete && peer.Authorized && !(bool)Get(validity.Lease, "Terminal") &&
                            result.Outcome == WindowMoveWorker.Outcome.Failed && result.MutationAttempted && !result.CallerExited &&
                            !broker.Active && broker.HasUncertainOperation && broker.LastError.Contains("ResultReply seq=1 op=1") && !broker.LastError.Contains("MainValidity"),
                            "An invalid terminal packet still fails closed before terminal publication and keeps its first protocol failure");
                    }
                }
            var reasonMethod = typeof(AdministratorSupport).GetMethod("ValidityReason", BindingFlags.Static | BindingFlags.NonPublic);
            var advisory = new DiagnosticOnlyValidity { Reason = 9 };
            check((int)reasonMethod.Invoke(null, new object[] { (Func<bool>)advisory.Current }) == 9,
                "A numeric reason is read from the exact owned predicate target without changing its decision");
            foreach (int invalid in new[] { -1, 32, Int32.MaxValue }) {
                advisory.Reason = invalid;
                check((int)reasonMethod.Invoke(null, new object[] { (Func<bool>)advisory.Current }) == 0,
                    "An out-of-range advisory reason remains unknown instead of entering failure evidence");
            }
            advisory.Throw = true;
            check((int)reasonMethod.Invoke(null, new object[] { (Func<bool>)advisory.Current }) == 0 &&
                (int)reasonMethod.Invoke(null, new object[] { (Func<bool>)delegate { return false; } }) == 0,
                "A failed or unsupported advisory getter cannot escape cancellation or invent a validity reason");
        }
        static object Call(AdministratorSupport support, string name, params object[] args)
        { return typeof(AdministratorSupport).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(support, args); }
        static object InjectReconnect(AdministratorSupport support)
        {
            var type = typeof(AdministratorSupport);
            object lease = Activator.CreateInstance(type.GetNestedType("Lease", BindingFlags.NonPublic), true);
            object session = Activator.CreateInstance(type.GetNestedType("LaunchSession", BindingFlags.NonPublic), true);
            object candidate = Activator.CreateInstance(type.GetNestedType("ReconnectCandidate", BindingFlags.NonPublic), true);
            Set(lease, "Complete", true); Set(session, "ImageVerified", true); Set(session, "Retired", 1);
            Set(session, "RetirementComplete", true); Set(session, "RetirementDrained", true); Set(session, "RetirementExit", (int?)0); Set(session, "RetirementGeneration", 71);
            Set(candidate, "Generation", 71); Set(candidate, "Lease", lease); Set(candidate, "Session", session);
            Set(candidate, "Began", Stopwatch.GetTimestamp() - Stopwatch.Frequency);
            Set(support, "generation", 71); Set(support, "failureGeneration", 71); Set(support, "retirementConfirmed", true); Set(support, "reconnectCandidate", candidate);
            Set(support, "lastIncident", "Owned MainValidity seq=7 op=1 validity=8"); Set(support, "lastIncidentGeneration", 71);
            return candidate;
        }
        static void ConfirmedReconnectPolicy(Action<bool, string> check)
        {
            // Synthetic authority/completion state exercises only the private
            // reservation policy. These fixtures never call BeginEnable, run
            // a task, read a protected cache, authorize USER or claim a drain.
            foreach (string block in new[] { "callback", "operation", "native90", "nativeRefusal", "unknownExit", "unconfirmedDrain", "unverifiedImage", "pendingDrain",
                "retirementGeneration", "pendingShutdown", "callbackCount", "shutdown", "uncertain", "active", "enabling", "setup", "disposed", "generation", "firstFailure", "spentBudget", "backoff", "expired", "future", "worker" })
                using (var support = new AdministratorSupport()) {
                    object candidate = InjectReconnect(support), lease = Get(candidate, "Lease"), session = Get(candidate, "Session");
                    if (block == "callback") Set(lease, "Complete", false);
                    else if (block == "operation") Set(support, "operation", lease);
                    else if (block == "native90") Set(session, "RetirementExit", (int?)90);
                    else if (block == "nativeRefusal") Set(session, "RetirementExit", (int?)102);
                    else if (block == "unknownExit") Set(session, "RetirementExit", null);
                    else if (block == "unconfirmedDrain") Set(session, "RetirementDrained", false);
                    else if (block == "unverifiedImage") Set(session, "ImageVerified", false);
                    else if (block == "pendingDrain") Set(session, "RetirementComplete", false);
                    else if (block == "retirementGeneration") Set(session, "RetirementGeneration", 70);
                    else if (block == "pendingShutdown") Set(support, "pendingShutdowns", 1);
                    else if (block == "callbackCount") Set(support, "pendingOperations", 1);
                    else if (block == "shutdown") Set(support, "shutdownConfirmed", false);
                    else if (block == "uncertain") Set(support, "uncertain", true);
                    else if (block == "active") Set(support, "active", true);
                    else if (block == "enabling") Set(support, "enabling", true);
                    else if (block == "setup") Set(support, "setupPending", true);
                    else if (block == "disposed") Set(support, "disposed", true);
                    else if (block == "generation") Set(support, "generation", 72);
                    else if (block == "firstFailure") Set(support, "failureGeneration", 70);
                    else if (block == "spentBudget") Set(support, "reconnectUsed", true);
                    else if (block == "backoff") Set(candidate, "Began", Stopwatch.GetTimestamp());
                    else if (block == "expired") Set(candidate, "Began", Stopwatch.GetTimestamp() - Stopwatch.Frequency * 11);
                    else if (block == "future") Set(candidate, "Began", Stopwatch.GetTimestamp() + Stopwatch.Frequency);
                    int previousWorkers = WindowMoveWorker.UncertainHelpers;
                    try {
                        if (block == "worker") typeof(WindowMoveWorker).GetField("uncertainHelpers", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 1);
                        check(!(bool)Call(support, "TryTakeReconnect", true, true), "A synthetic reconnect reservation cannot bypass " + block);
                    } finally {
                        if (block == "worker") typeof(WindowMoveWorker).GetField("uncertainHelpers", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, previousWorkers);
                        Set(support, "operation", null); Set(support, "pendingShutdowns", 0); Set(support, "pendingOperations", 0);
                    }
                }
            foreach (bool drainFirst in new[] { true, false }) using (var support = new AdministratorSupport()) {
                object candidate = InjectReconnect(support), lease = Get(candidate, "Lease"), session = Get(candidate, "Session");
                Set(lease, "Complete", false); Set(support, "operation", lease); Set(support, "pendingOperations", 1); Set(support, "uncertain", true);
                Set(support, "retirementConfirmed", false);
                Set(session, "RetirementComplete", false); Set(support, "pendingShutdowns", 1); Set(support, "shutdownConfirmed", false);
                if (drainFirst) {
                    Set(session, "RetirementComplete", true); Set(support, "pendingShutdowns", 0); Set(support, "shutdownConfirmed", true); Set(support, "retirementConfirmed", true);
                } else { Set(lease, "Complete", true); Set(support, "pendingOperations", 0); Set(support, "operation", null); }
                Call(support, "RefreshReconnectQuiescence");
                check(support.HasUncertainOperation && !(bool)Call(support, "TryTakeReconnect", true, true),
                    "The first of independent native drain and actual callback completion cannot authorize reconnect: drainFirst=" + drainFirst);
                if (drainFirst) {
                    Set(lease, "Terminal", true); Call(support, "RefreshReconnectQuiescence");
                    check(support.HasUncertainOperation && !(bool)Call(support, "TryTakeReconnect", true, true),
                        "Terminal publication alone cannot replace the callback-finally fence or clear retained recovery");
                    Set(lease, "Complete", true); Set(support, "pendingOperations", 0);
                    Call(support, "RefreshReconnectQuiescence");
                    check(support.HasUncertainOperation, "Actual callback completion cannot substitute for clearing the original operation slot");
                    Set(support, "operation", null);
                } else {
                    Set(session, "RetirementComplete", true); Set(support, "pendingShutdowns", 0); Set(support, "shutdownConfirmed", true); Set(support, "retirementConfirmed", true);
                }
                Call(support, "RefreshReconnectQuiescence");
                check(!support.HasUncertainOperation && (bool)Call(support, "TryTakeReconnect", true, true),
                    "Only matching confirmed drain, callback completion and operation clearing reserve one reconnect: drainFirst=" + drainFirst);
                check(!(bool)Call(support, "TryTakeReconnect", true, true) && (bool)Get(support, "reconnectUsed"),
                    "Repeated polls cannot reserve a second launch or replenish the consumed reconnect budget");
                Set(support, "generation", 72); Set(support, "lastError", "");
                check(support.LastIncident.Contains("MainValidity") && support.LastIncident.Contains("validity=8"),
                    "The cancelled operation's bounded incident evidence survives new-generation status clearing");
            }
            foreach (string invalidate in new[] { "optOut", "disable", "exit", "dispose", "explicitEnable" }) using (var support = new AdministratorSupport()) {
                InjectReconnect(support);
                if (invalidate == "optOut") Call(support, "TryTakeReconnect", false, true);
                else if (invalidate == "disable") support.Disable();
                else if (invalidate == "exit") support.StopForExit(0);
                else if (invalidate == "dispose") support.Dispose();
                else { Set(support, "reconnectUsed", true); Call(support, "ResetReconnect"); }
                check(Get(support, "reconnectCandidate") == null && !(bool)Call(support, "TryTakeReconnect", true, true),
                    "A stale synthetic reconnect candidate is invalidated by " + invalidate);
                if (invalidate == "explicitEnable") check(!(bool)Get(support, "reconnectUsed") && support.LastIncident.Contains("MainValidity"),
                    "Only explicit Enable's reset replenishes the budget while preserving the preceding incident");
            }
            using (var support = new AdministratorSupport()) {
                object candidate = InjectReconnect(support);
                check(!(bool)Call(support, "TryTakeReconnect", true, false) && Get(support, "reconnectCandidate") == candidate && !(bool)Get(support, "reconnectUsed"),
                    "A busy or temporarily paused GUI defers its one reservation only within the fixed incident TTL");
                check((bool)Call(support, "TryTakeReconnect", true, true), "A ready GUI can reserve after the temporary readiness fence clears");
            }
            using (var support = new AdministratorSupport()) {
                object candidate = InjectReconnect(support), lease = Get(candidate, "Lease");
                Set(lease, "Complete", false); Set(support, "pendingOperations", 1); Set(support, "uncertain", true); Set(candidate, "Began", Stopwatch.GetTimestamp() - Stopwatch.Frequency * 11);
                check(!(bool)Call(support, "TryTakeReconnect", true, true) && Get(support, "reconnectCandidate") == null,
                    "Expired activation is discarded while only independently proven retirement can later clear quiescence");
                Set(lease, "Complete", true); Set(support, "pendingOperations", 0); Call(support, "RefreshReconnectQuiescence");
                check(!support.HasUncertainOperation && !(bool)Call(support, "TryTakeReconnect", true, true) && !(bool)Get(support, "reconnectUsed"),
                    "Late confirmed quiescence clears recovery without reviving an expired launch or spending/replenishing its budget");
            }
            using (var support = new AdministratorSupport()) {
                object candidate = InjectReconnect(support), lease = Get(candidate, "Lease");
                Set(lease, "Complete", false); Set(support, "pendingOperations", 1); Set(support, "uncertain", true); Set(support, "reconnectUsed", true);
                support.Disable(); Call(support, "ResetReconnect");
                check(support.HasUncertainOperation && Get(support, "reconnectCandidate") == null,
                    "Disable and an explicit Enable reset cannot clear proven-drain recovery while its original callback remains unfinished");
                Set(lease, "Complete", true); Set(support, "pendingOperations", 0); Call(support, "RefreshReconnectQuiescence");
                check(!support.HasUncertainOperation && !(bool)Call(support, "TryTakeReconnect", true, true),
                    "Later callback completion after Disable permits ordinary explicit recovery without reviving automatic activation");
            }
            using (var support = new AdministratorSupport()) {
                object lease = Activator.CreateInstance(typeof(AdministratorSupport).GetNestedType("Lease", BindingFlags.NonPublic), true);
                Set(lease, "Authorized", true); Set(lease, "Mutation", true);
                Call(support, "PublishTerminal", lease, new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Success, CallerExited = true, MutationAttempted = true }, 0);
                check((bool)Get(lease, "Terminal") && !(bool)Get(lease, "Complete") && !support.HasUncertainOperation,
                    "Safe terminal publication leaves actual callback completion exclusively to worker finally after the I/O fence");
            }
            using (var peer = new OwnedOperationPeer(OperationBarrier.AfterAuthorize)) using (var support = new AdministratorSupport()) {
                Set(support, "pipe", peer.Server); Set(support, "active", true); Set(support, "helper", OwnedLifetime()); Set(support, "focusQueued", true);
                object session = Activator.CreateInstance(typeof(AdministratorSupport).GetNestedType("LaunchSession", BindingFlags.NonPublic), true);
                Set(session, "StartComplete", true); Set(support, "launchSession", session);
                var validity = new OperationValidity(support, peer, OperationBarrier.AfterAuthorize);
                uint pid = (uint)Process.GetCurrentProcess().Id;
                var result = support.Execute(1, new IntPtr(19), pid, NativeProcessIdentity.Started(pid), Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2,
                    validity.Current, new[] { 0, 3, -1, -1, -1, -1, 0, 0, 320, 240 });
                object candidate = Get(support, "reconnectCandidate");
                check(result.Outcome == WindowMoveWorker.Outcome.Cancelled && candidate != null && Get(candidate, "Lease") == validity.Lease &&
                    Get(candidate, "Session") == session && support.LastIncident.Contains("MainValidity"),
                    "Only the real winning MainValidity path arms its exact operation/session/generation and retains the first incident");
                check(Wait(delegate { return (bool)Get(session, "RetirementComplete"); }, 1000) && Get(session, "RetirementExit") == null &&
                    !(bool)Call(support, "TryTakeReconnect", true, true),
                    "A synthetic never-launched session's ordinary cleanup is not mistaken for verified native exit zero");
            }
        }
    }
}
