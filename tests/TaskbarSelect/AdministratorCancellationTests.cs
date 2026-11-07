using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;

namespace TaskbarSelect
{
    // Owned local pipes and query-only identity of this process. Peer admission
    // is injected in test objects only; no helper, HWND, UAC or USER work runs.
    internal static class AdministratorCancellationTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        static void Set(object item, string name, object value) { item.GetType().GetField(name, Fields).SetValue(item, value); }
        static object Get(object item, string name) { return item.GetType().GetField(name, Fields).GetValue(item); }
        static object Call(object item, string name, params object[] arguments) { return item.GetType().GetMethod(name, Fields).Invoke(item, arguments); }
        static bool Wait(Func<bool> ready, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (!ready()) { if (watch.ElapsedMilliseconds >= milliseconds) return ready(); Thread.Sleep(2); }
            return true;
        }
        static void Send(Stream pipe, int kind, int sequence, params long[] values)
        { byte[] packet = AdministratorSupport.Encode(kind, sequence, values); pipe.Write(packet, 0, packet.Length); pipe.Flush(); }
        static long[] Read(Stream pipe, out int kind, out int sequence)
        {
            using (var timeout = new AdministratorSupport.DeadlineWatch(delegate { pipe.Dispose(); }, 1500))
                return AdministratorSupport.Decode(pipe, out kind, out sequence);
        }
        sealed class Peer : IDisposable
        {
            internal readonly NamedPipeServerStream Server;
            internal readonly ManualResetEvent Release = new ManualResetEvent(false), Finished = new ManualResetEvent(false);
            internal Exception Failure;
            readonly Thread reader;
            internal Peer(Action<NamedPipeClientStream> serve)
            {
                string name = "TaskbarSelect.OwnedNegativeAuthorization." + Guid.NewGuid().ToString("N");
                Server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                reader = new Thread(delegate() {
                    try {
                        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) {
                            client.Connect(1000); serve(client); Release.WaitOne(4000);
                        }
                    } catch (Exception error) { Failure = error; }
                    finally { Finished.Set(); }
                });
                reader.IsBackground = true; reader.Start();
                var connection = new AdministratorSupport.ConnectionWait(Server);
                if (!Wait(delegate { return connection.Complete; }, 1000)) throw new IOException("Owned cancellation peer did not connect.");
                connection.ThrowIfFailed();
            }
            public void Dispose()
            {
                Release.Set(); Server.Dispose();
                if (!Finished.WaitOne(2000) || !reader.Join(1000)) throw new IOException("Owned cancellation peer did not drain.");
                Release.Dispose(); Finished.Dispose();
            }
        }
        static void Bind(AdministratorSupport support, Peer peer)
        {
            uint process = (uint)Process.GetCurrentProcess().Id;
            Set(support, "pipe", peer.Server); Set(support, "active", true); Set(support, "focusQueued", true);
            Set(support, "helper", AdministratorSupport.ProcessLifetime.Open(process, NativeProcessIdentity.Started(process)));
        }
        static WindowMoveWorker.Result Execute(AdministratorSupport support, Func<bool> valid, int milliseconds = 1200)
        {
            uint process = (uint)Process.GetCurrentProcess().Id;
            return support.Execute(0, new IntPtr(19), process, NativeProcessIdentity.Started(process),
                Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000, valid, new[] { 0, 0, 320, 240, 0x214 });
        }
        static void Request(NamedPipeClientStream client, out int sequence)
        {
            int kind; long[] values = Read(client, out kind, out sequence);
            if (kind != 1 || values.Length != 10 || values[0] != 0) throw new IOException("Expected one exact owned movement-shaped request.");
        }
        static void Authorization(NamedPipeClientStream client, int sequence, long decision)
        {
            int kind, number; long[] values = Read(client, out kind, out number);
            if (kind != 2 || number != sequence || values.Length != 1 || values[0] != decision)
                throw new IOException("Owned authorization decision was not exact.");
        }
        static Func<bool> CancelAfterAdmission() { int count = 0; return delegate { return ++count == 1; }; }

        internal static void Run(Action<bool, string> check)
        {
            ReceiptPolicy(check);
            RepeatedCancellation(check);
            InvalidReceipts(check);
            DeadlineAndLifetime(check);
            ReadyExpiryFence(check);
            ReentrantAdmission(check);
            ActualHelperAuthorization(check);
        }
        static void ReceiptPolicy(Action<bool, string> check)
        {
            var valid = new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Cancelled, CallerExited = true };
            check(AdministratorSupport.PreauthorizationCancellation(valid, 0, 0), "Only a fixed no-mutation cancellation receipt permits healthy-session reuse");
            foreach (var result in new[] {
                new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Success, CallerExited = true },
                new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Rejected, CallerExited = true },
                new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Cancelled, CallerExited = true, MutationAttempted = true },
                new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Cancelled },
                new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Cancelled, CallerExited = true, MinimumWidth = 1 },
                new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Cancelled, CallerExited = true, MinimumHeight = 1 },
                new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Cancelled, CallerExited = true, NativeError = 5 }
            }) check(!AdministratorSupport.PreauthorizationCancellation(result, 0, 0), "A receipt with another outcome, mutation, caller or extra result data cannot authorize session reuse");
            check(!AdministratorSupport.PreauthorizationCancellation(valid, 1, 0) && !AdministratorSupport.PreauthorizationCancellation(valid, 1, 1) &&
                !AdministratorSupport.PreauthorizationCancellation(null, 0, 0), "Active or uncertain workers and absent receipts fail closed");
        }
        static void RepeatedCancellation(Action<bool, string> check)
        {
            int negatives = 0, positives = 0, focuses = 0;
            using (var peer = new Peer(delegate(NamedPipeClientStream client) {
                int last = 0;
                for (int index = 0; index < 9; index++) {
                    int sequence; Request(client, out sequence);
                    if (sequence <= last) throw new IOException("Movement sequences were not increasing."); last = sequence;
                    Send(client, 10, sequence); Authorization(client, sequence, index == 8 ? 1 : 0);
                    if (index == 8) { positives++; Send(client, 12, sequence, 0, 1, 1, 0, 0, 0, 0, 0); }
                    else { negatives++; Send(client, 12, sequence, 4, 0, 1, 0, 0, 0, 0, 0); }
                }
                int kind, number; long[] values = Read(client, out kind, out number);
                if (kind != 3 || number <= last || !AdministratorSupport.ValidFocus(values)) throw new IOException("Fresh focus packet did not follow the terminal receipts.");
                focuses++; Send(client, 11, number, 0, 0);
            })) using (var support = new AdministratorSupport()) {
                Bind(support, peer); Set(support, "reconnectUsed", true);
                object session = Activator.CreateInstance(typeof(AdministratorSupport).GetNestedType("LaunchSession", BindingFlags.NonPublic), true);
                Set(session, "ImageVerified", true); Set(session, "StartComplete", true);
                Set(support, "launchSession", session); Set(support, "reconnectRearmSession", session);
                Set(support, "reconnectRearmGeneration", (int)Get(support, "generation"));
                try {
                for (int index = 0; index < 8; index++) {
                    var result = Execute(support, CancelAfterAdmission());
                    check(result.Outcome == WindowMoveWorker.Outcome.Cancelled && !result.MutationAttempted && result.CallerExited && support.Active &&
                        !support.HasUncertainOperation && support.LastError.Length == 0 && Get(support, "operation") == null && (int)Get(support, "pendingOperations") == 0,
                        "Repeated preauthorization cancellation drains its exact receipt and retains the same healthy broker " + index);
                    check((bool)Get(support, "reconnectUsed") && Get(support, "reconnectRearmLease") == null,
                        "Actual negative receipts cannot stage or rearm a spent saved-reconnect allowance");
                }
                var completed = Execute(support, delegate { return true; });
                check(completed.Outcome == WindowMoveWorker.Outcome.Success && completed.MutationAttempted && completed.CallerExited && support.Active,
                    "A fresh authorized request succeeds on the original pipe after repeated cancellations");
                check(!(bool)Get(support, "reconnectUsed") && Get(support, "reconnectRearmSession") == null &&
                    Get(support, "reconnectRearmLease") == null && (int)Get(support, "reconnectRearmGeneration") == -1,
                    "The actual successful Execute receipt plus callback and main admission cleanup rearms its exact recovered session once");
                Set(support, "reconnectUsed", true); Call(support, "RefreshReconnectBudget");
                check((bool)Get(support, "reconnectUsed"), "Consumed positive-operation evidence cannot rearm a later exhausted allowance a second time");
                Set(support, "focusQueued", false); Set(support, "focusSent", 0L); Call(support, "QueueFocus");
                check(Wait(delegate { return !(bool)Get(support, "focusQueued") && (long)Get(support, "focusSent") != 0; }, 1000) && support.Active &&
                    negatives == 8 && positives == 1 && focuses == 1 && peer.Failure == null,
                    "A subsequent real broker focus exchange succeeds without unread packets, reconnect or a second helper");
                check((bool)Get(support, "reconnectUsed") && Get(support, "reconnectCandidate") == null && support.LastIncident.Length == 0,
                    "Focus cannot replenish a reconnect allowance after the positive operation's one-use proof was consumed");
                } finally {
                    // These are memory-only policy stamps, never real launcher
                    // leases. Do not send them through production retirement.
                    Set(support, "launchSession", null); Set(support, "reconnectRearmSession", null);
                    Set(support, "reconnectRearmLease", null); Set(support, "reconnectRearmGeneration", -1);
                }
            }
        }
        static void InvalidReceipts(Action<bool, string> check)
        {
            foreach (string fault in new[] { "kind", "sequence", "mutation", "caller", "active", "uncertain", "outcome", "size", "error", "short" })
                using (var peer = new Peer(delegate(NamedPipeClientStream client) {
                    int number; Request(client, out number); Send(client, 10, number); Authorization(client, number, 0);
                    long[] reply = { 4, 0, 1, 0, 0, 0, 0, 0 };
                    if (fault == "mutation") reply[1] = 1;
                    if (fault == "caller") reply[2] = 0;
                    if (fault == "active" || fault == "uncertain") reply[6] = 1;
                    if (fault == "uncertain") reply[7] = 1;
                    if (fault == "outcome") reply[0] = 0;
                    if (fault == "size") reply[3] = 1;
                    if (fault == "error") reply[5] = 5;
                    if (fault == "short") reply = new long[] { 4, 0, 1, 0, 0, 0, 0 };
                    Send(client, fault == "kind" ? 11 : 12, fault == "sequence" ? number + 1 : number, reply);
                })) using (var support = new AdministratorSupport()) {
                    Bind(support, peer);
                    var result = Execute(support, CancelAfterAdmission());
                    check(result.Outcome == WindowMoveWorker.Outcome.Failed && !result.MutationAttempted && !support.Active && !support.HasUncertainOperation &&
                        support.LastError.Contains("CancelResultReply") && Wait(delegate { return (int)Get(support, "pendingOperations") == 0; }, 1000),
                        "Malformed negative-authorization receipt retires the session before reuse: " + fault);
                }
        }
        static void DeadlineAndLifetime(Action<bool, string> check)
        {
            foreach (string transition in new[] { "deadline", "generation", "disable", "dispose" }) {
                AdministratorSupport current = null;
                using (var peer = new Peer(delegate(NamedPipeClientStream client) {
                    int number; Request(client, out number); Send(client, 10, number); Authorization(client, number, 0);
                    if (transition == "deadline") return;
                    if (transition == "generation") { Set(current, "generation", (int)Get(current, "generation") + 1); Send(client, 12, number, 4, 0, 1, 0, 0, 0, 0, 0); }
                    else if (transition == "disable") current.Disable();
                    else current.Dispose();
                })) using (var support = new AdministratorSupport()) {
                    current = support; Bind(support, peer);
                    var watch = Stopwatch.StartNew();
                    var result = Execute(support, CancelAfterAdmission(), transition == "deadline" ? 180 : 1000);
                    check(result.Outcome != WindowMoveWorker.Outcome.Success && !result.MutationAttempted &&
                        Wait(delegate { return (int)Get(support, "pendingOperations") == 0; }, 1000) && Get(support, "operation") == null,
                        "Negative authorization cannot publish success or leave an admitted lease after " + transition);
                    if (transition == "deadline") check(!support.Active && watch.ElapsedMilliseconds < 1000 && support.LastError.Contains("MainDeadline"),
                        "Missing cancellation ACK is bounded by the original deadline and retires the pipe");
                    if (transition == "generation") check(result.Outcome == WindowMoveWorker.Outcome.Failed,
                        "An old negative receipt cannot complete a lease in a newer broker generation");
                    if (transition == "disable" || transition == "dispose") check(!support.Active,
                        "Explicit disable or disposal wins against an in-flight negative authorization");
                }
            }
        }
        static void ReentrantAdmission(Action<bool, string> check)
        {
            using (var readyAllowed = new ManualResetEvent(false)) using (var peer = new Peer(delegate(NamedPipeClientStream client) {
                int number; Request(client, out number);
                if (!readyAllowed.WaitOne(1000)) throw new IOException("Owned pre-READY cancellation was not reached.");
                Send(client, 10, number); Authorization(client, number, 0); Send(client, 12, number, 4, 0, 1, 0, 0, 0, 0, 0);
            })) using (var support = new AdministratorSupport()) {
                Bind(support, peer); int calls = 0; WindowMoveWorker.Result nested = null;
                var result = Execute(support, delegate {
                    if (++calls == 1) return true;
                    nested = Execute(support, delegate { return true; }); readyAllowed.Set(); return false;
                });
                check(nested != null && nested.Outcome == WindowMoveWorker.Outcome.Rejected && result.Outcome == WindowMoveWorker.Outcome.Cancelled && support.Active &&
                    Get(support, "operation") == null && peer.Failure == null,
                    "Pre-READY cancellation retains operation ownership until ACK and rejects reentrant competing requests");
            }
        }
        static void ReadyExpiryFence(Action<bool, string> check)
        {
            bool closed = false, workerFinishedInsideValidity = false;
            using (var peer = new Peer(delegate(NamedPipeClientStream client) {
                int number; Request(client, out number); Send(client, 10, number);
                bool received = false;
                try { int kind, id; Read(client, out kind, out id); received = true; }
                catch (IOException) { closed = true; }
                if (received) throw new InvalidOperationException("Expired READY unexpectedly received an authorization packet.");
            })) using (var support = new AdministratorSupport()) {
                Bind(support, peer); int calls = 0;
                uint process = (uint)Process.GetCurrentProcess().Id;
                long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
                var result = support.Execute(0, new IntPtr(19), process, NativeProcessIdentity.Started(process), deadline, delegate {
                    if (++calls == 1) return true;
                    object lease = Get(support, "operation");
                    if (lease == null || !Wait(delegate { return (bool)Get(lease, "Ready"); }, 600)) throw new IOException("Owned READY did not arrive.");
                    // Hold only this synthetic predicate: the background worker
                    // must expire before the UI caller can check its deadline.
                    workerFinishedInsideValidity = Wait(delegate { return (bool)Get(lease, "Complete"); }, 700);
                    return true;
                }, new[] { 0, 0, 320, 240, 0x214 });
                check(workerFinishedInsideValidity && !support.Active && !result.MutationAttempted &&
                    support.LastError.Contains("MainAuthorization") && Wait(delegate { return closed; }, 500) && peer.Failure == null,
                    "Worker-first READY expiry retires the unresolved channel before callback completion can let the UI reuse it");
            }
        }
        static void ActualHelperAuthorization(Action<bool, string> check)
        {
            object state = null; int cancelled = 0; bool serveHealthy = false;
            var type = typeof(AdministratorSupport); var stateType = type.GetNestedType("HelperState", BindingFlags.NonPublic);
            var receive = type.GetMethod("ReceiveAuthorization", Static);
            using (var peer = new Peer(delegate(NamedPipeClientStream client) {
                state = Activator.CreateInstance(stateType, true);
                uint process = (uint)Process.GetCurrentProcess().Id;
                Set(state, "Parent", type.GetMethod("ReadPeer", Static).Invoke(null, new object[] { process }));
                Set(state, "Pipe", client); Set(state, "Seen", Stopwatch.GetTimestamp());
                // Exercise the real post-admission stage directly: a MED test
                // process cannot impersonate the helper's HIGH target admission.
                for (int number = 1; number <= 3; number++) {
                    Send(client, 10, number);
                    bool authorized = (bool)receive.Invoke(null, new object[] { state, number, Stopwatch.GetTimestamp() + Stopwatch.Frequency });
                    if (authorized) throw new IOException("Negative authorization was promoted to USER permission.");
                    cancelled++;
                }
                serveHealthy = !(bool)Get(state, "Stop");
                type.GetMethod("Serve", Static).Invoke(null, new[] { state });
            })) {
                for (int number = 1; number <= 3; number++) {
                    int kind, sequence; long[] ready = Read(peer.Server, out kind, out sequence);
                    check(kind == 10 && sequence == number && ready.Length == 0, "Actual helper authorization stage offers the expected READY before a negative decision");
                    Send(peer.Server, 2, number, 0);
                    long[] reply = Read(peer.Server, out kind, out sequence); WindowMoveWorker.Result result; int workers, pending;
                    check(kind == 12 && sequence == number && AdministratorSupport.ParseResult(reply, out result, out workers, out pending) &&
                        AdministratorSupport.PreauthorizationCancellation(result, workers, pending),
                        "Actual helper negative stage returns fixed no-mutation cancellation without launching USER work");
                }
                Send(peer.Server, 3, 4, 2, 0, 0, 1, 3, 1, 0);
                int response, id; long[] focus = Read(peer.Server, out response, out id);
                check(response == 11 && id == 4 && focus.Length == 2 && focus[0] == 0 && focus[1] == 0 && serveHealthy && cancelled == 3,
                    "Actual Serve handles a fresh focus request on the same pipe after repeated negative authorizations");
                Send(peer.Server, 3, 4, 2, 0, 0, 1, 3, 1, 0);
                check(Wait(delegate { return state != null && (bool)Get(state, "Stop"); }, 1000) &&
                    (AdministratorSupport.HelperStopReason)Get(state, "StopReason") == AdministratorSupport.HelperStopReason.ProtocolOrder,
                    "A genuine helper sequence fault remains fatal after safe cancellation");
            }
            foreach (long[] values in new[] { new long[] { -1 }, new long[] { 2 }, new long[0], new long[] { 0, 0 } }) {
                bool refused = false;
                using (var finished = new ManualResetEvent(false)) using (var peer = new Peer(delegate(NamedPipeClientStream client) {
                    object owned = Activator.CreateInstance(stateType, true);
                    Set(owned, "Pipe", client);
                    try { receive.Invoke(null, new object[] { owned, 1, Stopwatch.GetTimestamp() + Stopwatch.Frequency }); }
                    catch (TargetInvocationException error) { refused = error.InnerException is IOException; }
                    finally { finished.Set(); }
                })) {
                    Send(peer.Server, 2, 1, values);
                    check(finished.WaitOne(1000) && refused, "Actual helper authorization stage rejects malformed non-Boolean decisions without USER execution");
                }
            }
        }
    }
}
