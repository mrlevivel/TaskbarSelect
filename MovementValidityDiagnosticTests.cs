using System;
using System.Collections.Generic;
using System.Reflection;

namespace TaskbarSelect
{
    internal static class MovementValidityDiagnosticTests
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        static object Call(TaskbarMover mover, string name, params object[] args)
        { return typeof(TaskbarMover).GetMethod(name, Instance).Invoke(mover, args); }
        static void Set(TaskbarMover mover, string name, object value)
        { typeof(TaskbarMover).GetField(name, Instance).SetValue(mover, value); }
        static TaskbarMover Owned()
        {
            var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new Exception("Input replay is forbidden in validity diagnostics."); },
                delegate { return IntPtr.Zero; }, false);
            mover.MoveApi.Modifiers = delegate { return 0; };
            foreach (string name in new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" })
                if ((IntPtr)typeof(TaskbarMover).GetField(name, Instance).GetValue(mover) != IntPtr.Zero)
                    throw new Exception("Validity diagnostic fixture registered a desktop hook.");
            return mover;
        }
        static void Begin(TaskbarMover mover)
        { if (!(bool)Call(mover, "BeginMovement", false, false)) throw new Exception("Owned movement slot was not admitted."); }
        static void Reject(Action<bool, string> check, TaskbarMover mover, TaskbarMover.MovementValidityReason reason, string label)
        {
            check(!(bool)Call(mover, "MovementCurrent") && ((AdministratorSupport.IValidityDiagnostic)mover).LastValidityReason == (int)reason,
                "Movement cancellation preserves its guard and reports bounded reason: " + label);
        }
        internal static void Run(Action<bool, string> check)
        {
            using (var mover = Owned()) {
                Reject(check, mover, TaskbarMover.MovementValidityReason.MovementLifetime, "no current movement");
                Begin(mover);
                check((bool)Call(mover, "MovementCurrent") && ((AdministratorSupport.IValidityDiagnostic)mover).LastValidityReason == 0,
                    "A valid non-window fixture clears stale diagnostic reasons without movement or input");
                mover.CancelPending();
                Reject(check, mover, TaskbarMover.MovementValidityReason.MovementLifetime, "cancelled movement generation");
            }
            using (var mover = Owned()) {
                Begin(mover); Set(mover, "disposed", true);
                try { Reject(check, mover, TaskbarMover.MovementValidityReason.Disposed, "disposed owner"); }
                finally { Set(mover, "disposed", false); }
            }
            using (var mover = Owned()) {
                Begin(mover); mover.ExcludedApps = new List<AppRule>();
                Reject(check, mover, TaskbarMover.MovementValidityReason.ExclusionIdentity, "replaced exclusions");
            }
            using (var mover = Owned()) {
                Begin(mover); mover.MoveApi.Modifiers = delegate { return 1; };
                Reject(check, mover, TaskbarMover.MovementValidityReason.NewModifier, "new modifier");
                mover.MoveApi.Modifiers = delegate { return 0; };
                check((bool)Call(mover, "MovementCurrent") && ((AdministratorSupport.IValidityDiagnostic)mover).LastValidityReason == 0,
                    "Validity diagnostic state has no authority over a later actual predicate result");
            }
            using (var mover = Owned()) {
                Begin(mover); mover.ExcludedApps.Add(new AppRule { Key = "owned-diagnostic-rule" });
                Reject(check, mover, TaskbarMover.MovementValidityReason.ExclusionRules, "changed exclusion count");
            }
            using (var mover = Owned()) {
                mover.ExcludedApps.Add(new AppRule { Key = "owned-diagnostic-rule" }); Begin(mover);
                mover.ExcludedApps[0].Key = "owned-changed-rule";
                Reject(check, mover, TaskbarMover.MovementValidityReason.ExclusionRules, "changed exclusion key");
            }
            using (var mover = Owned()) {
                Begin(mover); Set(mover, "movementTopology", true);
                Set(mover, "movementSource", new Native.MonitorInfo { Device = "TaskbarSelect.Owned.MissingMonitor" });
                Reject(check, mover, TaskbarMover.MovementValidityReason.MonitorTopology, "missing captured monitor");
            }
            using (var mover = Owned()) {
                Begin(mover); Set(mover, "movementBefore", new WindowHistory.Entry { Window = IntPtr.Zero, Process = 1 });
                Reject(check, mover, TaskbarMover.MovementValidityReason.WindowIdentity, "invalid captured window");
            }
            using (var mover = Owned()) {
                Begin(mover); Set(mover, "ownedMovementGuard", new Func<bool>(delegate { return false; }));
                Reject(check, mover, TaskbarMover.MovementValidityReason.OwnedSnapGuard, "revoked experimental snap scope");
                Set(mover, "ownedMovementGuard", new Func<bool>(delegate { return true; }));
                check((bool)Call(mover, "MovementCurrent"), "Renewed actual scope is evaluated independently of the old diagnostic");
            }
            check(Enum.GetValues(typeof(TaskbarMover.MovementValidityReason)).Length == 10 &&
                (int)TaskbarMover.MovementValidityReason.Foreground == 8 && (int)TaskbarMover.MovementValidityReason.OwnedSnapGuard == 9,
                "Movement diagnostics contain only the fixed finite zero-through-nine reasons");
            check(TaskbarMoveDiagnostics.Format(9, false, false, false, false).StartsWith("lastGuard=9 ") &&
                TaskbarMoveDiagnostics.Format(10, false, false, false, false).StartsWith("lastGuard=unknown "),
                "Experimental scope rejection has a bounded numeric diagnostic; undefined reasons stay unknown");
        }
    }
}
