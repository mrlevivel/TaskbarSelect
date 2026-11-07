using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class SnapIntentActionBoundaryTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;
        static void Check(bool okay, string message) { if (!okay) throw new Exception(message); checks++; }
        static void Set(object value, string field, object data) { value.GetType().GetField(field, Fields).SetValue(value, data); }
        internal static int Run()
        {
            checks = 0;
            // No constructor is invoked: ordinary mover/controller constructors
            // own native resources. Only actual managed admission methods run.
            var mover = (TaskbarMover)FormatterServices.GetUninitializedObject(typeof(TaskbarMover));
            var movement = new WindowMoveCoordinator(delegate { return 1000; }, 1000);
            Set(mover, "movement", movement); Set(mover, "minimizeAction", new MinimizeActionGate());
            Set(mover, "foregroundWindow", new Func<IntPtr>(delegate { return new IntPtr(1); }));
            Set(mover, "movementRuleKeys", new List<string>()); mover.ExcludedApps = new List<AppRule>();
            Set(mover, "MoveApi", new TaskbarMover.WindowMoveApi { Modifiers = delegate { return 0; } });
            int notifications = 0;
            var intentFixture = new SnapIntentPolicyTests.Fixture(); SnapIntentReservation reservation; SnapIntentProof old;
            Check(intentFixture.Reserve(1010, out reservation, out old), "Action-boundary test begins with an actually reserved chord.");
            mover.ControllerActionStarting = delegate { notifications++; intentFixture.Intent.Invalidate(); };
            mover.ControllerActionGate = delegate { return false; };
            var begin = typeof(TaskbarMover).GetMethod("BeginMovement", Fields);
            Check(!(bool)begin.Invoke(mover, new object[] { false, false }) && !mover.TryBeginMinimizeAction() && notifications == 0,
                "Rejected disabled action admissions send no notification.");
            mover.ControllerActionGate = delegate { return true; };
            Check(movement.TryEnter(), "Busy fixture owns the action slot.");
            Check(!(bool)begin.Invoke(mover, new object[] { false, false }) && !mover.TryBeginMinimizeAction() && notifications == 0,
                "Rejected busy move/minimize admissions send no notification.");
            movement.Exit();
            Check((bool)begin.Invoke(mover, new object[] { false, false }) && notifications == 1 && reservation.Cancelled,
                "Successful action admission cancels input before any native window operation.");
            movement.Exit(); // Entire action can complete between desktop polls.
            Check(!intentFixture.Intent.CanPrepare(reservation, old, intentFixture.Now), "A short complete action between80ms polls cannot leave a reusable reservation.");
            // Timer objects remain disabled and have no message HWND; Stop only
            // takes their managed no-handle path. No timer is started here.
            using (var timer = new Timer()) using (var fullscreen = new Timer()) {
                Set(mover, "timer", timer); Set(mover, "fullscreenObservation", fullscreen);
                Set(mover, "gestureDiagnostics", new TaskbarGestureDiagnostics());
                Check(mover.TryBeginMinimizeAction() && notifications == 2, "Successful minimize admission notifies exactly once.");
                Check(!mover.TryBeginMinimizeAction() && notifications == 2, "Already-pending minimize cannot duplicate its admission notification.");
            }
            var snap = (SnapWorkAreas)FormatterServices.GetUninitializedObject(typeof(SnapWorkAreas));
            var observer = new SnapKeyboardObserver(); // Constructor has no hook.
            Set(snap, "keyboardIntent", observer);
            int readyCalls = 0; Set(snap, "inputActionReady", new Func<bool>(delegate { readyCalls++; return true; }));
            for (int i = 0; i < 1000; i++) snap.PollKeyboardIntent();
            Check(!SnapWorkAreas.AutomaticGeometryCorrectionEnabled && readyCalls == 0 && !observer.Available && !observer.StopPending,
                "1000 handoffs under release containment cannot ask for action authority or start an observer, even with an injected observer field.");
            var controller = (Controller)FormatterServices.GetUninitializedObject(typeof(Controller));
            Set(controller, "snapAreas", snap); int before = readyCalls; controller.PollSnapIntent();
            Check(readyCalls == before, "A non-desktop controller never polls or activates the optional observer.");
            Console.WriteLine("PASS: " + checks + " managed action-admission/empty-handoff checks; no hook, timer start or application constructor.");
            return checks;
        }
    }
}
