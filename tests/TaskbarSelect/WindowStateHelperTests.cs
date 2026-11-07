using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace TaskbarSelect
{
    // Pure/injected checks only: no desktop windows, native mutations,
    // elevated processes, global input, persistent approval or real profiles.
    internal static class WindowStateHelperTests
    {
        static readonly DateTime StartedUtc = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        static readonly long RawStarted = StartedUtc.ToFileTimeUtc(), ProtocolStarted = StartedUtc.Ticks;
        internal static void Run(Action<bool, string> check)
        { Values(check); LifetimeBoundary(check); StateBoundary(check); ReleaseProtocol(check); ReleaseOwnership(check); CleanupExchange(check); PreauthorizationFence(check); PinOwnershipReceipts(check); LateOwnershipReceipt(check); ShutdownRetention(check); }

        static FocusedWindowSnapshot Window(bool topmost = false)
        {
            return new FocusedWindowSnapshot { Window = new IntPtr(42), Root = new IntPtr(42), Process = 100, Thread = 200,
                ProcessStarted = RawStarted, Visible = true, ClassName = "DemoWindow", ProcessImage = @"C:\Demo\Demo.exe", ExtendedStyle = topmost ? 8 : 0 };
        }
        sealed class StateFixture
        {
            internal FocusedWindowSnapshot Window = WindowStateHelperTests.Window();
            internal IntPtr Foreground = new IntPtr(42);
            internal long Now = 100;
            internal bool Minimized, NativeAccepted = true, Complete = true, InvalidAfterMutation;
            internal int Mutations, Reads;
            internal Action BeforeRead;
            internal WindowMoveWorker.StateApi Api;
            internal StateFixture()
            {
                Api = new WindowMoveWorker.StateApi {
                    Read = delegate(IntPtr w) { Reads++; if (BeforeRead != null) BeforeRead(); return InvalidAfterMutation && Mutations != 0 ? null : Window.Copy(); },
                    Foreground = delegate { return Foreground; }, Now = delegate { return Now; }, Pause = delegate { Now++; },
                    IsMinimized = delegate { return Minimized; }, IsTopmost = delegate { return (Window.ExtendedStyle & 8) != 0; },
                    Minimize = delegate { Mutations++; if (NativeAccepted && Complete) { Minimized = true; Foreground = IntPtr.Zero; } return NativeAccepted; },
                    SetTopmost = delegate(IntPtr w, bool desired) { Mutations++; if (NativeAccepted && Complete) Window.ExtendedStyle = desired ? 8 : 0; return NativeAccepted; }
                };
            }
            internal int Run(int operation, int[] values, out bool attempted)
            { attempted = false; return WindowMoveWorker.StateOperation(operation, new IntPtr(42), 100, ProtocolStarted, 110, values, ref attempted, Api); }
        }
        static void LifetimeBoundary(Action<bool, string> check)
        {
            var snapshot = Window();
            check(RawStarted != ProtocolStarted && AdministratorSupport.SnapshotLifetime(snapshot, 100, ProtocolStarted),
                "Elevated Close/minimize/pin admission converts raw FILETIME snapshots to the existing UTC-tick protocol epoch");
            check(!AdministratorSupport.SnapshotLifetime(snapshot, 100, RawStarted) && !AdministratorSupport.SnapshotLifetime(snapshot, 100, ProtocolStarted + 1) &&
                !AdministratorSupport.SnapshotLifetime(snapshot, 101, ProtocolStarted) && !AdministratorSupport.SnapshotLifetime(null, 100, ProtocolStarted),
                "Raw protocol values, another process epoch, mismatched PID and unavailable snapshot cannot authorize elevated actions");
            bool attempted = false; var fixture = new StateFixture();
            check(WindowMoveWorker.StateOperation(5, snapshot.Window, snapshot.Process, RawStarted, 110, new int[0], ref attempted, fixture.Api) == 0 && !attempted,
                "State worker rejects raw FILETIME supplied where the UTC-tick protocol epoch is required");
            check(snapshot.ProcessStarted == RawStarted && WindowMoveWorker.StateIdentity(snapshot, snapshot.Copy()),
                "Cross-boundary conversion leaves raw snapshot-to-snapshot lifetime identity unchanged");
        }
        static void Values(Action<bool, string> check)
        {
            check(AdministratorSupport.ValidValues(5, 42, 100, 300, new int[0]), "Minimize has exactly no caller-selected native arguments");
            foreach (int desired in new[] { 0, 1 }) check(AdministratorSupport.ValidValues(6, 42, 100, 300, new[] { desired }), "Topmost accepts the fixed Boolean desired state " + desired);
            foreach (int desired in new[] { -1, 2, Int32.MinValue, Int32.MaxValue }) check(!AdministratorSupport.ValidValues(6, 42, 100, 300, new[] { desired }), "Topmost refuses non-Boolean state " + desired);
            foreach (int op in new[] { 5, 6 }) {
                check(!AdministratorSupport.ValidValues(op, 42, 100, 300, null), "State operation rejects missing values " + op);
                check(!AdministratorSupport.ValidValues(op, 0, 100, 300, new int[0]), "State operation rejects zero HWND " + op);
                check(!AdministratorSupport.ValidValues(op, 42, 0, 300, new int[0]), "State operation rejects zero PID " + op);
                check(!AdministratorSupport.ValidValues(op, 42, 100, 0, new int[0]), "State operation rejects missing lifetime " + op);
                check(!AdministratorSupport.ValidValues(op, 42, 100, 300, new[] { 0, 1 }), "State operation rejects argument expansion " + op);
            }
            check(!AdministratorSupport.ValidValues(5, 42, 100, 300, new[] { 1 }) && !AdministratorSupport.ValidValues(6, 42, 100, 300, new int[0]), "Minimize and topmost shapes cannot substitute for one another");
        }
        static void StateBoundary(Action<bool, string> check)
        {
            bool attempted; var f = new StateFixture();
            check(f.Run(5, new int[0], out attempted) == 1 && attempted && f.Mutations == 1 && f.Foreground == IntPtr.Zero,
                "Minimize succeeds after its expected foreground loss without substituting another target");
            f = new StateFixture(); check(f.Run(6, new[] { 1 }, out attempted) == 1 && attempted && f.Mutations == 1 && (f.Window.ExtendedStyle & 8) != 0,
                "Pin confirms the captured window became topmost");
            f = new StateFixture { Window = Window(true) }; check(f.Run(6, new[] { 0 }, out attempted) == 1 && attempted && f.Mutations == 1,
                "Explicit unpin can remove native pre-existing topmost");
            foreach (int op in new[] { 5, 6 }) {
                int[] values = op == 5 ? new int[0] : new[] { 1 };
                f = new StateFixture { Foreground = new IntPtr(999) };
                check(f.Run(op, values, out attempted) == 0 && !attempted && f.Mutations == 0, "State operation requires the captured foreground " + op);
                f = new StateFixture(); f.Window.ProcessStarted++;
                check(f.Run(op, values, out attempted) == 0 && !attempted, "State operation rejects a reused process " + op);
                f = new StateFixture(); f.Window.ExtendedStyle = 0x80;
                check(f.Run(op, values, out attempted) == 0 && !attempted, "State operation rejects tool windows " + op);
                f = new StateFixture { NativeAccepted = false };
                check(f.Run(op, values, out attempted) == 2 && attempted && f.Mutations == 1, "Native refusal remains an attempted failure " + op);
                f = new StateFixture { InvalidAfterMutation = true };
                check(f.Run(op, values, out attempted) == 2 && attempted, "Disappearing or unreadable HWND cannot prove completion " + op);
                f = new StateFixture { Complete = false };
                check(f.Run(op, values, out attempted) == 2 && attempted && f.Now >= 110 && f.Mutations == 1, "Unsettled state expires without reposting " + op);
                f = new StateFixture { Now = 110 };
                check(f.Run(op, values, out attempted) == 0 && !attempted, "Expired state operation performs no native request " + op);
                f = new StateFixture(); var captured = f;
                f.BeforeRead = delegate { if (captured.Reads == 2) captured.Window.Thread++; };
                check(f.Run(op, values, out attempted) == 0 && !attempted, "Thread identity change between admission reads is rejected " + op);
            }
            f = new StateFixture { Window = Window(true) };
            check(f.Run(6, new[] { 1 }, out attempted) == 0 && !attempted && f.Mutations == 0, "Pin never adopts a pre-existing topmost window");
            f = new StateFixture { Minimized = true };
            check(f.Run(5, new int[0], out attempted) == 0 && !attempted, "Already minimized window cannot authorize a new minimize request");
            f = new StateFixture(); f.Window.ProcessImage = @"C:\Other\Demo.exe"; var first = f.Window.Copy(); f.Window.ProcessImage = @"C:\Demo\Demo.exe";
            check(!WindowMoveWorker.StateIdentity(first, f.Window) && !WindowMoveWorker.StateIdentity(first, null), "State completion includes process image and rejects unavailable identity");
        }
        static void ReleaseProtocol(Action<bool, string> check)
        {
            long now = Stopwatch.GetTimestamp();
            check(AdministratorSupport.ValidPinReleaseRequest(new[] { now + Stopwatch.Frequency }, now), "Pin cleanup accepts one bounded monotonic deadline");
            foreach (long[] bad in new[] { null, new long[0], new[] { now }, new[] { now - 1 }, new[] { now + 2 * Stopwatch.Frequency + 1 }, new[] { now + 1, 42L } })
                check(!AdministratorSupport.ValidPinReleaseRequest(bad, now), "Pin cleanup rejects expired, expanded or unbounded authority");
            check(AdministratorSupport.ValidPinReleaseAuthorization(18, 5, 5, new long[] { 1 }), "Pin cleanup requires an exact positive authorization in its sequence");
            check(!AdministratorSupport.ValidPinReleaseAuthorization(2, 5, 5, new long[] { 1 }) &&
                !AdministratorSupport.ValidPinReleaseAuthorization(18, 4, 5, new long[] { 1 }) &&
                !AdministratorSupport.ValidPinReleaseAuthorization(18, 5, 5, new long[] { 0 }) &&
                !AdministratorSupport.ValidPinReleaseAuthorization(18, 5, 5, new long[] { 1, 42 }), "Window authorization, replay, negative and expanded authorization cannot release pins");
            check(AdministratorSupport.ValidPinReleaseReply(new long[] { 1, 0, 0, 0 }) && AdministratorSupport.ValidPinReleaseReply(new long[] { 0, 2, 1, 1 }), "Cleanup receipts distinguish confirmed restoration from retained recovery");
            foreach (long[] bad in new[] { null, new long[0], new long[] { 1, 1, 0, 0 }, new long[] { 1, 0, 1, 0 }, new long[] { 1, 0, 0, 1 }, new long[] { 2, 0, 0, 0 }, new long[] { 0, 33, 0, 0 }, new long[] { 0, 0, 0, 1 } })
                check(!AdministratorSupport.ValidPinReleaseReply(bad), "Cleanup cannot claim success with owned pins, busy workers or malformed counters");
            WindowMoveWorker.Result result; int active, pending, owned;
            long[] complete = { 0, 1, 1, 0, 0, 0, 0, 0 };
            check(AdministratorSupport.ParseOperationResult(6, complete.Concat(new long[] { 0 }).ToArray(), out result, out active, out pending, out owned) && owned == 0,
                "Only a fixed op6 receipt carries authenticated owned-pin count");
            check(!AdministratorSupport.ParseOperationResult(6, complete, out result, out active, out pending, out owned) &&
                !AdministratorSupport.ParseOperationResult(5, complete.Concat(new long[] { 0 }).ToArray(), out result, out active, out pending, out owned),
                "Pin ownership evidence cannot be omitted or attached to a different operation");
            foreach (long count in new long[] { -1, 33, Int64.MaxValue })
                check(!AdministratorSupport.ParseOperationResult(6, complete.Concat(new[] { count }).ToArray(), out result, out active, out pending, out owned), "Pin ownership count stays bounded " + count);
            check(!AdministratorSupport.ParseOperationResult(6, new long[] { 2, 1, 0, 0, 0, 0, 0, 0, 0 }, out result, out active, out pending, out owned) &&
                !AdministratorSupport.ParseOperationResult(6, new long[] { 2, 1, 1, 0, 0, 0, 1, 0, 0 }, out result, out active, out pending, out owned),
                "An empty-owned claim cannot clear authorization while a worker is unconfirmed or still active");
        }
        static void ReleaseOwnership(Action<bool, string> check)
        {
            var owned = Window(); var unrelated = Window(true); unrelated.Window = unrelated.Root = new IntPtr(99);
            long now = 100; int mutations = 0; IntPtr marker = IntPtr.Zero;
            var current = owned.Copy();
            var pins = new OwnedWindowPins(new OwnedWindowPinApi {
                Read = delegate(IntPtr w) { return w == owned.Window ? current.Copy() : unrelated.Copy(); }, Exists = delegate { return true; },
                Now = delegate { return now; }, Pause = delegate { now++; }, Quiescent = delegate { return true; },
                SetMarker = delegate(IntPtr w, string key, IntPtr value) { marker = value; return true; },
                ReadMarker = delegate { return marker; }, RemoveMarker = delegate { IntPtr old = marker; marker = IntPtr.Zero; return old; },
                SetTopmost = delegate(IntPtr w, bool desired) { check(w == owned.Window && !desired, "Helper cleanup has authority only over its recorded pin"); mutations++; current.ExtendedStyle = 0; return true; }
            });
            check(pins.Remember(owned) && !pins.Remember(unrelated), "Helper ownership is reserved only from a non-topmost identity");
            current.ExtendedStyle = 8;
            long[] reply = AdministratorSupport.ReleasePinValues(pins, 200);
            check(reply.SequenceEqual(new long[] { 1, 0, 0, 0 }) && mutations == 1 && unrelated.ExtendedStyle == 8, "Confirmed cleanup leaves unrelated pre-existing topmost unchanged");
            current.ExtendedStyle = 0; now = 100; mutations = 0; marker = IntPtr.Zero; bool readable = true;
            pins = new OwnedWindowPins(new OwnedWindowPinApi { Read = delegate { return readable ? current.Copy() : null; }, Exists = delegate { return true; }, Now = delegate { return now; },
                SetMarker = delegate(IntPtr w, string key, IntPtr value) { marker = value; return true; },
                ReadMarker = delegate { return marker; }, RemoveMarker = delegate { IntPtr old = marker; marker = IntPtr.Zero; return old; },
                Pause = delegate { now++; }, Quiescent = delegate { return true; }, SetTopmost = delegate { mutations++; return true; } });
            check(pins.Remember(owned), "An owned candidate can be retained before an uncertain native attempt");
            current.ExtendedStyle = 8; readable = false;
            reply = AdministratorSupport.ReleasePinValues(pins, 105);
            check(reply.SequenceEqual(new long[] { 0, 1, 0, 0 }) && mutations == 0, "Unreadable live owned window retains cleanup uncertainty without mutating another target");
        }
        static void ShutdownRetention(Action<bool, string> check)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using (var support = new AdministratorSupport()) {
                typeof(AdministratorSupport).GetField("pinsMayRemain", flags).SetValue(support, true);
                typeof(AdministratorSupport).GetField("uncertain", flags).SetValue(support, true);
                typeof(AdministratorSupport).GetField("retirementConfirmed", flags).SetValue(support, true);
                typeof(AdministratorSupport).GetMethod("RefreshReconnectQuiescence", flags).Invoke(support, null);
                check(support.HasUncertainOperation && (bool)Get(support, "pinsMayRemain"), "Independent process retirement cannot erase unacknowledged HIGH pin restoration");
                check(!support.StopForExit(0), "Drained processes cannot confirm exit while HIGH pin restoration is unknown");
                check(!support.Active, "Refused inactive cleanup never launches or grants administrator support");
                // Only the synthetic field is reset, so disposing this pure
                // fixture cannot affect live processes or desktop windows.
                typeof(AdministratorSupport).GetField("pinsMayRemain", flags).SetValue(support, false);
            }
            using (var peer = new CleanupPeer(0)) using (var support = new AdministratorSupport()) {
                Bind(support, peer);
                object lease = Activator.CreateInstance(typeof(AdministratorSupport).GetNestedType("Lease", BindingFlags.NonPublic), true);
                Set(lease, "Authorized", true); Set(lease, "Mutation", true);
                Set(support, "operation", lease); Set(support, "pendingOperations", 1);
                check(!support.StopForExit(0) && support.Active && peer.Server.IsConnected && peer.Requests == 0 && (bool)Get(support, "pinsMayRemain"),
                    "Off during an authorized pin retains its live channel until the operation and owned cleanup settle");
                check(!(bool)Get(support, "disablePending"), "Refused shutdown releases its admission fence for a controlled retry");
                Reset(support);
            }
            var target = Window(); bool exists = true;
            Func<IntPtr, FocusedWindowSnapshot> observe = delegate { return exists ? target.Copy() : null; };
            var recovery = new AdministratorPinRecovery(new AdministratorPinRecoveryApi { Read = observe, Exists = delegate { return exists; } });
            check(recovery.Remember(target), "A HIGH authorization is retained for observational retirement recovery");
            target.ExtendedStyle = 8;
            using (var support = new AdministratorSupport(recovery, observe)) {
                Set(support, "pinsMayRemain", true); Set(support, "uncertain", true); Set(support, "retirementConfirmed", true);
                check(!support.StopForExit(0) && (bool)Get(support, "pinsMayRemain"), "Live topmost target keeps post-retirement shutdown unconfirmed");
                exists = false;
                check(support.StopForExit(0) && !(bool)Get(support, "pinsMayRemain") && !support.HasUncertainOperation,
                    "Off or Exit retry positively reobserves a now-closed target after independently confirmed retirement");
            }
        }
        static object Get(object value, string field)
        { return value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        static void Set(object value, string field, object content)
        { value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, content); }
        static void Send(Stream stream, int kind, int sequence, long[] values)
        { byte[] packet = AdministratorSupport.Encode(kind, sequence, values); stream.Write(packet, 0, packet.Length); stream.Flush(); }
        // A local fake peer speaks only the fixed cleanup exchange. It cannot
        // authenticate as HIGH, run a worker, or receive native targets.
        sealed class CleanupPeer : IDisposable
        {
            internal readonly NamedPipeServerStream Server;
            readonly ManualResetEvent finished = new ManualResetEvent(false);
            readonly Thread thread;
            internal Exception Failure;
            internal int Requests;
            internal bool Authorized;
            internal CleanupPeer(int mode)
            {
                string name = "TaskbarSelect.OwnedPinRelease." + Guid.NewGuid().ToString("N");
                Server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                thread = new Thread(delegate() {
                    try { using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                        client.Connect(1000);
                        int kind, number; long[] request = AdministratorSupport.Decode(client, out kind, out number);
                        if (mode >= 4) {
                            if (kind != 1 || request.Length != 6 || request[0] != 6 || !AdministratorSupport.Boolean(request[5]) || request[3] != ProtocolStarted || request[3] == RawStarted) throw new IOException("Unexpected fixed pin request or lifetime epoch.");
                            Interlocked.Increment(ref Requests);
                            if (mode == 10) { Send(client, 12, number, new long[] { 1, 0, 1, 0, 0, 0, 0, 0, 0 }); client.ReadByte(); return; }
                            Send(client, 10, number, new long[0]);
                            long[] response = AdministratorSupport.Decode(client, out kind, out number);
                            Authorized = kind == 2 && response.Length == 1 && response[0] == 1;
                            long[] terminal = mode == 7 ? new long[] { 1, 0, 1, 0, 0, 0, 0, 0, 0 } :
                                new long[] { 0, 1, 1, 0, 0, 0, 0, 0, mode == 5 ? 1 : 0 };
                            Send(client, 12, mode == 9 ? number + 1 : number, mode == 8 ? terminal.Take(8).ToArray() : terminal);
                            client.ReadByte(); return;
                        }
                        if (kind != 16 || !AdministratorSupport.ValidPinReleaseRequest(request, Stopwatch.GetTimestamp())) throw new IOException("Unexpected cleanup request.");
                        Interlocked.Increment(ref Requests);
                        Send(client, 17, number, new long[0]);
                        long[] authorization = AdministratorSupport.Decode(client, out kind, out number);
                        Authorized = AdministratorSupport.ValidPinReleaseAuthorization(kind, number, 1, authorization);
                        if (!Authorized) throw new IOException("Unexpected cleanup authorization.");
                        Send(client, 19, mode == 2 ? number + 1 : number, mode == 1 ? new long[] { 0, 1, 0, 0 } : mode == 3 ? new long[] { 1, 1, 0, 0 } : new long[] { 1, 0, 0, 0 });
                        client.ReadByte(); // Exact owned server closure ends the fixture.
                    } } catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (Exception error) { Failure = error; }
                    finally { finished.Set(); }
                });
                thread.IsBackground = true; thread.Start();
                IAsyncResult pending = Server.BeginWaitForConnection(null, null);
                if (!pending.AsyncWaitHandle.WaitOne(1500)) throw new IOException("Owned cleanup fixture did not connect.");
                Server.EndWaitForConnection(pending); pending.AsyncWaitHandle.Close();
            }
            public void Dispose()
            {
                Server.Dispose();
                if (!finished.WaitOne(1500) || !thread.Join(1000)) throw new IOException("Owned cleanup fixture did not finish.");
                finished.Dispose();
            }
        }
        static void Bind(AdministratorSupport support, CleanupPeer peer)
        {
            Set(support, "active", true); Set(support, "pipe", peer.Server); Set(support, "pinsMayRemain", true);
            Set(support, "focusQueued", true); // This fixture owns cleanup exclusively.
        }
        static void Reset(AdministratorSupport support)
        { Set(support, "pinsMayRemain", false); Set(support, "operation", null); Set(support, "pendingOperations", 0); }
        static AdministratorSupport InjectedSupport()
        {
            Func<IntPtr, FocusedWindowSnapshot> read = delegate { return Window(); };
            return new AdministratorSupport(new AdministratorPinRecovery(new AdministratorPinRecoveryApi { Read = read, Exists = delegate { return true; } }), read);
        }
        static void CleanupExchange(Action<bool, string> check)
        {
            var release = typeof(AdministratorSupport).GetMethod("ReleaseOwnedPins", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (int mode in new[] { 0, 1, 2, 3 }) using (var peer = new CleanupPeer(mode)) using (var support = new AdministratorSupport()) {
                Bind(support, peer);
                try {
                    bool released = (bool)release.Invoke(support, new object[] { 1000 });
                    check(peer.Requests == 1 && peer.Authorized && peer.Failure == null, "Cleanup uses one fixed READY/positive-authorization exchange " + mode);
                    if (mode == 0) check(released && !(bool)Get(support, "pinsMayRemain") && support.Active, "Only a confirmed cleanup receipt clears HIGH pin ownership");
                    else {
                        check(!released && (bool)Get(support, "pinsMayRemain"), "Rejected, stale or malformed cleanup cannot erase HIGH pin ownership " + mode);
                        if (mode == 1) {
                            check(support.Active && peer.Server.IsConnected, "Responsive cleanup refusal preserves the live helper channel for retry");
                            Set(support, "pendingOperations", 1);
                            bool threw = false; try { support.Dispose(); } catch (InvalidOperationException) { threw = true; }
                            // The fake peer consumes exactly one exchange; a
                            // second attempt is deliberately not made here.
                            check(threw && !(bool)Get(support, "disposed"), "Disposal cannot abandon an unconfirmed owned pin or claim it stopped");
                        } else check(!support.Active && support.HasUncertainOperation, "Unusable cleanup reply retires transport but retains recovery uncertainty " + mode);
                    }
                } finally { Reset(support); }
            }
        }
        static void PreauthorizationFence(Action<bool, string> check)
        {
            using (var peer = new CleanupPeer(4)) using (var support = InjectedSupport()) {
                Bind(support, peer); Set(support, "pinsMayRemain", false);
                bool stopped = false;
                Func<bool> valid = delegate {
                    object lease = Get(support, "operation");
                    if (!stopped && lease != null && (bool)Get(lease, "Ready")) { stopped = true; support.Disable(); }
                    return !stopped;
                };
                var result = support.Execute(6, new IntPtr(42), 100, ProtocolStarted, Stopwatch.GetTimestamp() + Stopwatch.Frequency, valid, new[] { 1 });
                var wait = Stopwatch.StartNew();
                while ((int)Get(support, "pendingOperations") != 0 && wait.ElapsedMilliseconds < 1000) Thread.Sleep(2);
                check(stopped && peer.Requests == 1 && !peer.Authorized && !support.Active && !(bool)Get(support, "pinsMayRemain") &&
                    !result.MutationAttempted && result.CallerExited && (int)Get(support, "pendingOperations") == 0,
                    "Disable between READY and authorization fences the pin before any native authority is sent");
            }
        }
        static void PinOwnershipReceipts(Action<bool, string> check)
        {
            foreach (int mode in new[] { 5, 6, 7, 8, 9, 10 }) using (var peer = new CleanupPeer(mode)) using (var support = InjectedSupport()) {
                Bind(support, peer);
                Set(support, "pinsMayRemain", mode != 5 && mode != 7);
                try {
                    int desired = mode == 6 || mode == 8 || mode == 9 ? 0 : 1;
                    var result = support.Execute(6, new IntPtr(42), 100, ProtocolStarted, Stopwatch.GetTimestamp() + Stopwatch.Frequency,
                        delegate { return true; }, new[] { desired });
                    var wait = Stopwatch.StartNew();
                    while ((int)Get(support, "pendingOperations") != 0 && wait.ElapsedMilliseconds < 1000) Thread.Sleep(2);
                    if (mode == 5) check(result.Outcome == WindowMoveWorker.Outcome.Success && (bool)Get(support, "pinsMayRemain"), "Authenticated positive pin receipt retains live helper-owned pins");
                    else if (mode == 6) check(result.Outcome == WindowMoveWorker.Outcome.Success && !(bool)Get(support, "pinsMayRemain"), "Last explicit HIGH unpin clears the conservative latch only with authenticated empty ownership");
                    else if (mode == 7 || mode == 10) check(result.Outcome == WindowMoveWorker.Outcome.Rejected && !result.MutationAttempted && !(bool)Get(support, "pinsMayRemain"),
                        "Proved no-mutation pin rejection with empty owned registry does not strand later shutdown " + mode);
                    else check((bool)Get(support, "pinsMayRemain") && !support.Active && support.HasUncertainOperation, "Missing or stale owned-count receipt cannot clear pending HIGH pin recovery " + mode);
                } finally { Reset(support); }
            }
        }
        static void LateOwnershipReceipt(Action<bool, string> check)
        {
            foreach (int scenario in new[] { 0, 1, 2 }) using (var support = InjectedSupport()) {
                object lease = Activator.CreateInstance(typeof(AdministratorSupport).GetNestedType("Lease", BindingFlags.NonPublic), true);
                Set(support, "operation", lease); Set(support, "pinsMayRemain", true); Set(support, "generation", 7);
                Set(support, "active", scenario != 0); Set(support, "failureGeneration", scenario == 1 ? 7 : -1);
                typeof(AdministratorSupport).GetMethod("PublishOperationTerminal", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(support,
                    new object[] { lease, new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Success, CallerExited = true }, 0, 0, scenario == 2 ? 6 : 7 });
                check((bool)Get(support, "pinsMayRemain"), "Late empty ownership cannot clear a stopped, failed or different generation " + scenario);
                Reset(support);
            }
        }
    }
}
