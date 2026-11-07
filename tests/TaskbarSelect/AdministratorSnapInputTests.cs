using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;

namespace TaskbarSelect
{
    internal static class AdministratorSnapInputTests
    {
        static int count;
        static void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        static void Set(object value, string name, object data)
        { value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(value, data); }
        static long[] Config(long epoch, long now)
        { return new long[] { 1, epoch, 123, 42, 1000, now + Stopwatch.Frequency, 0, 0 }; }
        static AdministratorSnapInput.Target Publish(AdministratorSnapInput input, long epoch, long now)
        {
            var target = new AdministratorSnapInput.Target { Window = new IntPtr(123), Process = 42, Started = 1000, Epoch = epoch, Validated = now };
            Set(input, "published", target);
            Set(input, "validate", new Func<AdministratorSnapInput.Target, bool>(delegate { return true; }));
            return target;
        }
        static OwnedSnapKeyboard.Notice Notice(AdministratorSnapInput.Target target, long now)
        { return new OwnedSnapKeyboard.Notice { Target = new OwnedSnapKeyboard.Target { Window = target.Window, State = target }, Epoch = target.Epoch, Created = now, Left = true }; }
        internal static int Run()
        {
            count = 0;
            try {
                var input = new AdministratorSnapInput(); long now = Stopwatch.GetTimestamp();
                Check(!input.CanClaim(), "A fresh helper cannot intercept without explicit configuration");
                Check(AdministratorSnapProtocol.ValidReply(input.Reply(0, 0)), "Default receipt has no observation");
                var config = Config(1, now); input.Configure(config); config[2] = 456;
                var target = Publish(input, 1, now);
                Check(input.CanClaim(), "Immutable exact target and live short lease permit interception");
                var wrong = Publish(input, 2, now); Check(!input.CanClaim(), "An earlier publication cannot use a new epoch");
                target = Publish(input, 1, now); target.Process++; Check(!input.CanClaim(), "PID changes cannot reuse the arm lease");
                target = Publish(input, 1, now); target.Started++; Check(!input.CanClaim(), "Process lifetime changes cannot reuse the arm lease");
                target = Publish(input, 1, now); target.Window = new IntPtr(456); Check(!input.CanClaim(), "Another foreground hint cannot use the lease");
                target = Publish(input, 1, now - Stopwatch.Frequency); Check(!input.CanClaim(), "Delayed helper UI publication cannot authorize a new chord");
                target = Publish(input, 1, now + Stopwatch.Frequency); Check(!input.CanClaim(), "A publication from a future clock fails closed");
                target = Publish(input, 1, Stopwatch.GetTimestamp());
                input.Accept(Notice(target, Stopwatch.GetTimestamp())); var first = input.Reply(0, 0);
                Check(first[3] == 1 && first[4] == 1 && first[5] == 123 && first[8] == 1 && AdministratorSnapProtocol.ValidReply(first),
                    "One fixed direction observation binds exact identity and epoch");
                input.Accept(Notice(target, Stopwatch.GetTimestamp()));
                Check(input.Reply(0, 0)[3] == 1, "An outstanding observation drops repeated chords without a queue");
                Check(input.Reply(0, 0)[3] == 1, "Polling retains an unacknowledged observation");
                var ack = Config(1, Stopwatch.GetTimestamp()); ack[7] = 1; input.Configure(ack);
                Check(input.Reply(0, 0)[3] == 0, "Exact acknowledgement retires the outstanding observation");
                target = Publish(input, 1, Stopwatch.GetTimestamp()); input.Accept(Notice(target, Stopwatch.GetTimestamp()));
                Check(input.Reply(0, 0)[3] == 2, "New observation uses a monotonic id after acknowledgement");
                input.Configure(Config(2, Stopwatch.GetTimestamp()));
                Check(!input.CanClaim() && input.Reply(0, 0)[3] == 0, "New scope invalidates both arm publication and outstanding action");
                target = Publish(input, 2, Stopwatch.GetTimestamp());
                var stale = Notice(target, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2); input.Accept(stale);
                Check(input.Reply(0, 0)[3] == 0, "Delayed STA delivery cannot renew an old physical key observation");
                var badEpoch = Notice(target, Stopwatch.GetTimestamp()); badEpoch.Epoch--; input.Accept(badEpoch);
                Check(input.Reply(0, 0)[3] == 0, "Queued key from a retired epoch cannot produce an observation");
                Set(input, "validate", new Func<AdministratorSnapInput.Target, bool>(delegate { return false; }));
                input.Accept(Notice(target, Stopwatch.GetTimestamp())); Check(input.Reply(0, 0)[3] == 0, "Fresh metadata refusal issues no action");
                input.Configure(new long[] { 0, 2, 0, 0, 0, 0, 0, 0 });
                Check(!input.CanClaim() && input.Reply(0, 0)[3] == 0, "Explicit Off fences the hook before its next UI tick");
                bool refused = false; try { input.Configure(Config(1, Stopwatch.GetTimestamp())); } catch (IOException) { refused = true; }
                Check(refused, "Regressing scope cannot rearm an old action");
                var expired = Config(3, Stopwatch.GetTimestamp()); expired[5] = Stopwatch.GetTimestamp() - 1; input.Configure(expired);
                Check(!input.CanClaim(), "Expired well-formed lease disarms while preserving the heartbeat");
                refused = false; try { input.Configure(new long[] { 1 }); } catch (IOException) { refused = true; }
                Check(refused, "Malformed configuration is not mistaken for a harmless expiry");
                ProfileAdmission();
                BrokerExchange(false, false, false, false);
                BrokerExchange(true, true, false, false);
                BrokerExchange(true, false, true, false);
                BrokerExchange(true, false, false, true);
                var release = new OwnedSnapReleaseGate(); var ownership = new OwnedSnapKeyOwnership();
                release.Observe(true); ownership.Accept(0x25); release.Reset(); release.Observe(false);
                Check(!release.Ready && ownership.ConsumeExisting(0x25, true), "New scope requires all-up while draining the old owned arrow-up");
                release.Observe(true); Check(release.Ready, "Observed full release permits a deliberate chord in the new scope");
                Console.WriteLine("PASS: administrator snap single-notice and lease checks (" + count + "); no hook, process or desktop input.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        static void ProfileAdmission()
        {
            var profile = typeof(AppProfile).GetField("selection", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = profile.GetValue(null);
            try {
                foreach (bool isolated in new[] { false, true }) using (var broker = new AdministratorSupport()) {
                    profile.SetValue(null, new ProfileSelection(@"C:\OwnedSnapMetadataOnly", isolated));
                    // Prevent transport scheduling: only the real broker's
                    // state admission is exercised, never a helper or hook.
                    Set(broker, "focusQueued", true); Set(broker, "active", true); Set(broker, "generation", 41);
                    try {
                        broker.UpdateSnapInput(false, 1, null, IntPtr.Zero);
                        Check(!(bool)Get(broker, "snapInputWanted") && !(bool)Get(broker, "snapInputConfigured"),
                            "Approved helper state alone cannot opt a fresh session into snap input, profile=" + isolated);
                        broker.UpdateSnapInput(true, 1, null, IntPtr.Zero);
                        Check((bool)Get(broker, "snapInputWanted") && (bool)Get(broker, "snapInputConfigured") &&
                            (int)Get(broker, "snapInputSession") == 41,
                            "Either profile may explicitly opt into the current approved helper session, profile=" + isolated);
                        broker.UpdateSnapInput(false, 2, null, IntPtr.Zero);
                        Check(!(bool)Get(broker, "snapInputWanted") && (bool)Get(broker, "snapInputConfigured"),
                            "After opting in either profile must still send explicit Off, profile=" + isolated);
                        Set(broker, "active", false); broker.UpdateSnapInput(true, 3, null, IntPtr.Zero);
                        Check(!(bool)Get(broker, "snapInputWanted"),
                            "Opt-in cannot arm an unapproved or disconnected helper, profile=" + isolated);
                    } finally { Set(broker, "active", false); Set(broker, "focusQueued", false); }
                }
            } finally { profile.SetValue(null, previous); }
        }
        static object Get(object value, string name)
        { return value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }
        static void BrokerExchange(bool sameSession, bool stalePublication, bool busy, bool uncertain)
        {
            string name = "TaskbarSelect.SnapReceiptTest." + Guid.NewGuid().ToString("N");
            Exception peerError = null; long[] request = null;
            using (var finished = new ManualResetEvent(false))
            using (var broker = new AdministratorSupport())
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)) {
                Set(broker, "active", true); Set(broker, "generation", 2); Set(broker, "snapInputSession", sameSession ? 2 : 1);
                Set(broker, "snapInputConfigured", true); Set(broker, "snapInputWanted", true); Set(broker, "snapInputEpoch", 9L);
                Set(broker, "snapInputPublished", Stopwatch.GetTimestamp() - (stalePublication ? Stopwatch.Frequency * 2 : 0));
                var waiter = server.BeginWaitForConnection(null, null);
                var peer = new Thread(delegate() {
                    try { using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) {
                        client.Connect(2000); int kind, id; request = AdministratorSupport.Decode(client, out kind, out id);
                        if (kind != 20 || !AdministratorSnapProtocol.ValidConfiguration(request, Stopwatch.GetTimestamp())) throw new IOException("Unexpected request.");
                        var reply = new long[AdministratorSnapProtocol.ReplyLength]; reply[0] = busy ? 1 : 0; reply[1] = uncertain ? 1 : 0;
                        if (busy || uncertain) {
                            reply[2] = 1; reply[3] = 1; reply[4] = 9; reply[5] = 123; reply[6] = 42; reply[7] = 1000; reply[8] = 1; reply[9] = Stopwatch.GetTimestamp();
                        }
                        byte[] bytes = AdministratorSupport.Encode(21, id, reply); client.Write(bytes, 0, bytes.Length); client.Flush();
                    } } catch (Exception error) { peerError = error; } finally { finished.Set(); }
                }); peer.IsBackground = true; peer.Start();
                try {
                    if (!waiter.AsyncWaitHandle.WaitOne(2000)) throw new IOException("Owned pipe did not connect.");
                    server.EndWaitForConnection(waiter);
                    typeof(AdministratorSupport).GetMethod("ExchangeSnapInput", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(broker,
                        new object[] { server, 2, new AdministratorSupport.FocusExchangePhase() });
                    Check(finished.WaitOne(2000) && peer.Join(2000) && peerError == null, "Owned broker exchange completes and joins its peer");
                    Check(request[0] == (sameSession && !stalePublication ? 1 : 0), "A retired helper generation or stopped controller cannot renew interception");
                    Check(broker.TakeSnapInput() == null, "Busy/uncertain receipts and disabled arms deliver no action");
                    if (uncertain) Check(broker.HasUncertainOperation, "Helper uncertainty is retained before admitting another movement");
                } finally {
                    Set(broker, "active", false); Set(broker, "uncertain", false); Set(broker, "snapInputWanted", false);
                    server.Dispose(); finished.WaitOne(2000); peer.Join(2000);
                }
            }
        }
    }
}
