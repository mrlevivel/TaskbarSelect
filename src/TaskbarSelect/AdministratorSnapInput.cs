using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    // This channel transports a fixed physical Win+Left/Right observation. It
    // grants no window-operation authority; the controller/worker reauthorize
    // every movement through the existing minimum/position/Undo protocol.
    internal sealed partial class AdministratorSupport
    {
        internal sealed class SnapInputNotice
        {
            internal IntPtr Window;
            internal uint Process;
            internal long Started, Epoch, Created, Id;
            internal int SessionGeneration;
            internal bool Left;
        }
        bool snapInputWanted, snapInputConfigured, snapInputArmed;
        long snapInputEpoch, snapInputAck, snapInputSeen, snapInputReplyAt, snapInputPublished;
        int snapInputSession;
        ShelfWindow snapInputTarget;
        IntPtr snapInputRejected;
        SnapInputNotice snapInputNotice;

        internal int SnapInputGeneration
        {
            get { lock (gate) return !disposed && active && !uncertain && !permissionChangePending && !disablePending &&
                !setupPending && failureGeneration != generation ? generation : 0; }
        }
        internal void UpdateSnapInput(bool enabled, long epoch, ShelfWindow target, IntPtr rejectedWindow)
        {
            lock (gate) {
                int current = SnapInputGeneration;
                bool wanted = enabled && epoch > 0 && current != 0;
                if (snapInputSession != current) {
                    snapInputSession = current; snapInputAck = snapInputSeen = 0; snapInputNotice = null; snapInputArmed = false;
                }
                if (!wanted || epoch != snapInputEpoch) { snapInputNotice = null; snapInputArmed = false; }
                bool changed = wanted != snapInputWanted || epoch != snapInputEpoch || snapInputRejected != rejectedWindow ||
                    (target == null) != (snapInputTarget == null) ||
                    (target != null && !WindowShelfPolicy.SameWindow(target, snapInputTarget));
                snapInputWanted = wanted; snapInputEpoch = Math.Max(0, epoch);
                snapInputPublished = wanted ? Stopwatch.GetTimestamp() : 0;
                snapInputTarget = wanted ? target : null; snapInputRejected = wanted ? rejectedWindow : IntPtr.Zero;
                // The normal heartbeat stays unchanged until this session opts
                // in. After opting in, explicit Off must still be sent.
                snapInputConfigured |= wanted;
                if (changed) focusSent = 0;
            }
            QueueFocus();
        }
        internal SnapInputNotice TakeSnapInput()
        {
            lock (gate) {
                var value = snapInputNotice; snapInputNotice = null;
                if (value == null) return null;
                snapInputAck = Math.Max(snapInputAck, value.Id); focusSent = 0;
                return snapInputWanted && value.Epoch == snapInputEpoch && value.SessionGeneration == SnapInputGeneration &&
                    AdministratorSnapProtocol.Fresh(value.Created, Stopwatch.GetTimestamp()) ? value : null;
            }
        }
        internal string SnapInputDiagnostic()
        {
            lock (gate) return "Administrator snap input: " + (!snapInputWanted ? "Off" :
                snapInputArmed && snapInputSession == SnapInputGeneration && AdministratorSnapProtocol.Fresh(snapInputReplyAt, Stopwatch.GetTimestamp()) ?
                "Armed" : "Waiting for an eligible elevated window");
        }
        void ExchangeSnapInput(PipeStream stream, int token, FocusExchangePhase progress)
        {
            long[] config; int number;
            lock (gate) {
                if (!snapInputConfigured || !active || generation != token) return;
                bool wanted = snapInputWanted && snapInputSession == token && SnapInputGeneration == token;
                var target = wanted ? snapInputTarget : null;
                long now = Stopwatch.GetTimestamp();
                // Independent liveness receipts cannot renew a stopped UI's
                // routing permission. Only fresh controller publication can.
                long expires = wanted && snapInputPublished > 0 && snapInputPublished <= Int64.MaxValue - Stopwatch.Frequency ?
                    snapInputPublished + Stopwatch.Frequency : 0;
                wanted &= now >= snapInputPublished && now < expires;
                config = new long[] { wanted ? 1 : 0, snapInputEpoch,
                    wanted && target != null ? target.Handle.ToInt64() : 0,
                    wanted && target != null ? target.Process : 0,
                    wanted && target != null ? target.ProcessStarted : 0,
                    wanted ? expires : 0, wanted ? snapInputRejected.ToInt64() : 0, snapInputAck };
                number = NextSequence();
            }
            if (!AdministratorSnapProtocol.ValidConfiguration(config, Stopwatch.GetTimestamp())) throw new IOException("Invalid snap input configuration.");
            progress.Set(FocusIoPhase.Write); Write(stream, SnapPoll, number, config);
            progress.Set(FocusIoPhase.Read);
            int kind, id; long[] values = Read(stream, out kind, out id, progress);
            progress.Set(FocusIoPhase.Reply);
            if (kind != SnapReply || id != number || !AdministratorSnapProtocol.ValidReply(values)) throw new IOException("Invalid snap input receipt.");
            lock (gate) {
                if (generation != token || snapInputSession != token || SnapInputGeneration != token || !snapInputWanted) return;
                if (values[0] != 0 || values[1] != 0) {
                    snapInputArmed = false;
                    if (values[1] != 0) uncertain = true;
                    if (values[3] > snapInputSeen) snapInputSeen = snapInputAck = values[3];
                    snapInputNotice = null;
                    return;
                }
                snapInputReplyAt = Stopwatch.GetTimestamp(); snapInputArmed = values[2] == 1;
                if (values[3] <= snapInputSeen) return;
                snapInputSeen = values[3];
                if (values[4] != snapInputEpoch || !AdministratorSnapProtocol.Fresh(values[9], snapInputReplyAt)) {
                    snapInputAck = values[3]; return;
                }
                if (snapInputNotice == null) snapInputNotice = new SnapInputNotice { Id = values[3], Epoch = values[4],
                    Window = new IntPtr(values[5]), Process = (uint)values[6], Started = values[7], Left = values[8] == 1,
                    Created = values[9], SessionGeneration = token };
            }
        }
    }

    // Helper STA owns all hook/native metadata work. Serve only exchanges
    // immutable configuration and one outstanding acknowledged observation.
    internal sealed class AdministratorSnapInput
    {
        internal sealed class Target
        {
            internal IntPtr Window;
            internal uint Process;
            internal long Started, Epoch, Validated;
        }
        readonly object gate = new object();
        volatile long[] configuration = new long[AdministratorSnapProtocol.ConfigurationLength];
        volatile Target published;
        Func<Target, bool> validate;
        long sequence, appliedEpoch = -1;
        long[] pending;
        volatile bool armed;
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr window);
        internal void Configure(long[] values)
        {
            long now = Stopwatch.GetTimestamp();
            // A delayed but well-formed lease disarms without destroying the
            // authenticated heartbeat. Malformed packets still fail closed.
            if (!AdministratorSnapProtocol.ValidConfiguration(values, now)) {
                if (values == null || values.Length != AdministratorSnapProtocol.ConfigurationLength || values[0] != 1 ||
                    values[5] <= 0 || values[5] > now || !AdministratorSnapProtocol.ValidConfiguration(values, values[5] - 1))
                    throw new IOException("Invalid snap input configuration.");
                values = new long[] { 0, values[1], 0, 0, 0, 0, 0, values[7] };
            }
            lock (gate) {
                long[] old = configuration;
                if (values[1] < old[1]) throw new IOException("Snap input epoch regressed.");
                if (values[0] == 0 || values[1] != old[1]) { pending = null; published = null; armed = false; }
                if (pending != null && values[7] >= pending[0]) pending = null;
                configuration = (long[])values.Clone();
            }
        }
        internal bool CanClaim()
        {
            long[] config = configuration; Target target = published; long now = Stopwatch.GetTimestamp();
            // Only arithmetic/immutable routing observations run in the hook.
            // Expiry fences input even if the helper UI timer is delayed.
            return config[0] == 1 && target != null && target.Epoch == config[1] && target.Window.ToInt64() == config[2] &&
                target.Process == config[3] && target.Started == config[4] && now < config[5] &&
                now >= target.Validated && now - target.Validated <= Stopwatch.Frequency / 4;
        }
        internal void Tick(OwnedSnapKeyboard keyboard, Func<Target, bool> validateTarget)
        {
            long[] config = configuration; long now = Stopwatch.GetTimestamp(); validate = validateTarget;
            if (config[0] != 1 || now >= config[5] || WindowMoveWorker.UncertainHelpers != 0) { Stop(keyboard); return; }
            if (appliedEpoch != config[1]) {
                published = null; keyboard.ClearTargets(); keyboard.RequireRelease(); keyboard.AdvanceEpoch(config[1]); appliedEpoch = config[1];
            }
            if (!keyboard.Enabled && !keyboard.Enable()) { published = null; armed = false; return; }
            keyboard.ClearTargets();
            var target = config[2] == 0 ? null : new Target { Window = new IntPtr(config[2]), Process = (uint)config[3],
                Started = config[4], Epoch = config[1], Validated = now };
            bool allowed = target != null && target.Window.ToInt64() != config[6] && validateTarget(target);
            published = allowed ? target : null;
            keyboard.Publish(!allowed ? null : new OwnedSnapKeyboard.Target { Window = target.Window, Published = now, State = target });
            armed = CanClaim();
        }
        internal void Accept(OwnedSnapKeyboard.Notice notice)
        {
            var target = notice == null || notice.Target == null ? null : notice.Target.State as Target;
            long now = Stopwatch.GetTimestamp();
            if (!CanClaim() || target == null || notice.Epoch != target.Epoch ||
                !AdministratorSnapProtocol.Fresh(notice.Created, now) || validate == null || !validate(target)) return;
            lock (gate) {
                if (!CanClaim() || pending != null || sequence == Int64.MaxValue) return;
                pending = new long[] { ++sequence, target.Epoch, target.Window.ToInt64(), target.Process, target.Started,
                    notice.Left ? 1 : 0, notice.Created };
            }
        }
        internal long[] Reply(int workers, int uncertain)
        {
            lock (gate) {
                var result = new long[AdministratorSnapProtocol.ReplyLength];
                result[0] = workers; result[1] = uncertain; result[2] = armed && CanClaim() ? 1 : 0;
                if (pending != null) Array.Copy(pending, 0, result, 3, pending.Length);
                return result;
            }
        }
        internal void Stop(OwnedSnapKeyboard keyboard)
        {
            published = null; armed = false; keyboard.Disable(); keyboard.ClearTargets();
            lock (gate) pending = null;
        }
    }
}
