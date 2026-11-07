using System;

namespace TaskbarSelect
{
    internal enum DeferredTaskbarAction { None, Move, ReplayDown, ReplayClick, Drop }

    // Owns one swallowed gesture. Window/button identity and native
    // mutation stay outside this pure state machine and must be freshly verified.
    internal sealed class DeferredTaskbarClick
    {
        internal const int TimeoutMilliseconds = 650;
        readonly long began, deadline;
        readonly bool physicalDown;
        bool released, cancelled, moving, settled;
        internal bool Released { get { return released; } }
        internal bool Moving { get { return moving; } }
        internal bool Settled { get { return settled; } }
        internal DeferredTaskbarClick(long began, long frequency)
            : this(began, frequency, true) { }
        internal DeferredTaskbarClick(long began, long frequency, bool physicalDown)
        {
            if (began < 0) throw new ArgumentOutOfRangeException("began");
            if (frequency <= 0) throw new ArgumentOutOfRangeException("frequency");
            this.began = began;
            this.physicalDown = physicalDown;
            // Integer arithmetic keeps the deadline valid even for extreme test
            // frequencies, without overflowing a double-to-Int64 conversion.
            long ticks = Math.Max(1, frequency / 1000 * TimeoutMilliseconds +
                frequency % 1000 * TimeoutMilliseconds / 1000);
            deadline = began > Int64.MaxValue - ticks ? Int64.MaxValue : began + ticks;
        }
        internal static bool CanBegin(bool physical, bool modified, bool enabled, bool busy, bool eligible,
            bool elevated, bool administratorAvailable, bool crossMonitor, bool cachedUsable, bool uncertainOperation = false)
        {
            return !modified && enabled && !busy && eligible && !uncertainOperation && crossMonitor && !cachedUsable &&
                (!elevated || (physical && administratorAvailable));
        }
        internal static bool IdentityCurrent(NativeProcessIdentity.Info caller, NativeProcessIdentity.Info expected,
            NativeProcessIdentity.Info actual, bool requiresAdministrator)
        {
            return caller != null && caller.Process != 0 && caller.Started > 0 && caller.IntegrityAvailable &&
                expected != null && expected.Process != 0 && expected.Started > 0 && expected.IntegrityAvailable &&
                actual != null && actual.IntegrityAvailable && actual.Process == expected.Process && actual.Started == expected.Started &&
                actual.IntegrityLevel == expected.IntegrityLevel &&
                NativeProcessIdentity.RequiresAdministrator(caller, actual) == requiresAdministrator;
        }
        internal static bool ProtectedInput(bool requiresAdministrator, bool expectedNativeSelection, bool freshNativeSelection)
        { return requiresAdministrator || expectedNativeSelection || freshNativeSelection; }
        internal static bool MatchesTarget(TaskbarTargets.Target target, IntPtr window, uint process, long started)
        {
            return target != null && target.Window == window && window != IntPtr.Zero && target.Process == process && process != 0 && started > 0 &&
                (target.ProcessStarted == 0 || target.ProcessStarted == started) && target.Button != null &&
                !target.Button.Grouped && !target.Button.PreviewOnly && !target.Button.SelectionStarted &&
                target.Windows != null && target.Windows.Length == 1 && target.Windows[0] != null &&
                target.Windows[0].Window == window && target.Windows[0].Process == process &&
                (target.Windows[0].ProcessStarted == 0 || target.Windows[0].ProcessStarted == started);
        }
        internal static bool GestureCurrent(bool protectedInput, bool physicalDown, bool physicalUp,
            uint downTime, uint upTime, uint nowTime, bool modified)
        {
            return !modified && physicalDown == physicalUp && (!protectedInput || physicalDown) &&
                NativeTaskbarHandoff.WithinDeadline(downTime, nowTime) &&
                TaskbarNativeSelection.IsFreshEvent(downTime, upTime, nowTime);
        }
        bool Expired(long now) { return now < began || now >= deadline; }
        internal void Release(bool physical, bool modified, bool inside, bool dragged, long now)
        {
            if (settled || moving || released) return;
            released = true;
            cancelled |= physical != physicalDown || modified || !inside || dragged || Expired(now);
        }
        internal DeferredTaskbarAction Evaluate(bool verificationComplete, bool verified, bool contextCurrent, long now)
        {
            if (settled || moving) return DeferredTaskbarAction.None;
            if (cancelled || Expired(now) || !contextCurrent) return Abort(true);
            if (!released || !verificationComplete) return DeferredTaskbarAction.None;
            if (!verified) return Abort(true);
            moving = true; // A reentrant abort may not replay once native work begins.
            return DeferredTaskbarAction.Move;
        }
        internal DeferredTaskbarAction AfterMove(bool success, bool mutation, bool interrupted)
        {
            if (settled || !moving) return DeferredTaskbarAction.None;
            moving = false;
            if (success || mutation || interrupted) { settled = true; return DeferredTaskbarAction.Drop; }
            return Abort(true);
        }
        internal DeferredTaskbarAction Abort(bool allowReplay)
        {
            if (settled) return DeferredTaskbarAction.None;
            settled = true;
            if (moving || !allowReplay) { moving = false; return DeferredTaskbarAction.Drop; }
            return released ? DeferredTaskbarAction.ReplayClick : DeferredTaskbarAction.ReplayDown;
        }
    }
}
