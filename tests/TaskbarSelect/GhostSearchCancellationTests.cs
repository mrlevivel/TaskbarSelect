using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Native preparation uses only an alpha-zero, nonactivating owned HWND.
    // All movement/focus boundaries are injected; no desktop hooks or input,
    // app launch, Explorer taskbars, profiles, Startup or display changes.
    internal static class GhostSearchCancellationTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        sealed class OwnedWindow : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }
        sealed class Monitor
        {
            internal IntPtr Handle;
            internal Native.MonitorInfo Info;
        }
        static void Set(object target, string field, object value)
        { target.GetType().GetField(field, Fields).SetValue(target, value); }
        static void Call(WindowShelfManager manager, string method)
        { typeof(WindowShelfManager).GetMethod(method, Fields).Invoke(manager, null); }
        static GhostSearchPopup Popup()
        {
            return new GhostSearchPopup(new Display { Device = "fixture", Identity = "fixture", Width = 1920, Height = 1080 },
                new Rectangle(0, 0, 1920, 1040), 96, 1000, delegate { throw new Exception("App launch is forbidden in cancellation tests."); }, null);
        }
        static WindowShelfManager Manager(Action cancel)
        {
            return new WindowShelfManager(delegate { return new List<Display>(); }, delegate { return new List<AppRule>(); },
                delegate { throw new Exception("Unexpected native move through a cancellation fixture manager."); }, null, null, cancel);
        }
        internal static void Run(Action<bool, string> check)
        {
            Ownership(check);
            ClosedManagerLaunchLease(check);
            var monitors = new List<Monitor>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, ref Native.Rect bounds, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(handle, ref info)) monitors.Add(new Monitor { Handle = handle, Info = info });
                return true;
            }, IntPtr.Zero);
            if (monitors.Count < 2) { Console.WriteLine("SKIP: Ghost search native movement cancellation requires two monitors; pure ownership checks passed."); return; }
            IntPtr foreground = GetForegroundWindow(); Point pointer = Cursor.Position;
            int helpers = WindowMoveWorker.ActiveHelpers;
            try { SharedMoveCancellation(check, monitors[0], monitors[1]); }
            finally {
                check(GetForegroundWindow() == foreground && Cursor.Position == pointer,
                    "Ghost search cancellation fixtures preserve actual foreground and pointer");
                check(WindowMoveWorker.ActiveHelpers == helpers,
                    "Ghost search cancellation fixtures leave no movement helper processes");
            }
        }
        static void Ownership(Action<bool, string> check)
        {
            int cancellations = 0;
            using (var manager = Manager(delegate { cancellations++; })) {
                var session = new GhostSearchSession(); var popup = Popup();
                Set(manager, "search", popup); Set(manager, "searchSession", session);
                Call(manager, "CloseSearchAnimated");
                check(cancellations == 0 && !session.Active && popup.IsDisposed,
                    "Closing an idle search cancels only its search session and preserves unrelated native movement");
                Call(manager, "Dismiss");
                check(cancellations == 0, "Ordinary shelf dismissal does not invoke the search movement cancellation callback");
            }
            using (var manager = Manager(delegate { cancellations++; })) {
                Set(manager, "searchMoving", true);
                Call(manager, "Dismiss");
                check(cancellations == 1, "Pause/disconnect dismissal fences a native move only when owned Ghost search movement is active");
                Set(manager, "searchMoving", false); Call(manager, "Dismiss");
                check(cancellations == 1, "Repeated dismissal after search movement unwinds cannot cancel unrelated movement");
            }
        }
        static void SharedMoveCancellation(Action<bool, string> check, Monitor source, Monitor destination)
        {
            int width = source.Info.Work.Right - source.Info.Work.Left, height = source.Info.Work.Bottom - source.Info.Work.Top;
            using (var window = new OwnedWindow { Text = "Neutral Ghost search cancellation fixture", Opacity = 0, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, AutoScaleMode = AutoScaleMode.None,
                Bounds = new Rectangle(source.Info.Work.Left + Math.Min(30, Math.Max(0, width / 10)),
                    source.Info.Work.Top + Math.Min(30, Math.Max(0, height / 10)), Math.Max(1, Math.Min(400, width / 2)), Math.Max(1, Math.Min(300, height / 2))) })
            using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new Exception("Input replay is forbidden in Ghost cancellation tests."); },
                delegate { return window.Handle; }, false))
            using (var manager = Manager(mover.CancelPending)) {
                window.Show(); window.WindowState = FormWindowState.Maximized; Application.DoEvents();
                check(IsZoomed(window.Handle) && Native.MonitorFromWindow(window.Handle, 2) == source.Handle,
                    "Owned invisible cancellation fixture is maximized on its source monitor");
                check(!WindowShelfCatalog.IsFullscreenForMovement(window.Handle),
                    "Owned captioned maximized cancellation fixture remains an ordinary movable app");
                foreach (string hook in new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" })
                    check((IntPtr)typeof(TaskbarMover).GetField(hook, Fields).GetValue(mover) == IntPtr.Zero,
                        "Ghost cancellation fixture has no desktop " + hook);
                var before = WindowHistory.Capture(window.Handle);
                check(before != null && !mover.CanUndo, "Ghost cancellation fixture starts with a real recovery snapshot and empty history");
                var session = new GhostSearchSession(); var popup = Popup();
                Set(manager, "searchMoving", true); Set(manager, "search", popup); Set(manager, "searchSession", session);
                int positions = 0, placements = 0, focus = 0;
                bool invalidWhileEntered = false;
                var guard = (WindowMoveCoordinator)typeof(TaskbarMover).GetField("movement", Fields).GetValue(mover);
                mover.MoveApi.Modifiers = delegate { return 0; };
                mover.MoveApi.Position = delegate {
                    positions++;
                    Call(manager, "CloseSearchAnimated");
                    invalidWhileEntered = guard.Busy && guard.Cancelled && !guard.Current;
                    return true; // An accepted/possibly partial first native mutation.
                };
                mover.MoveApi.Placement = delegate { placements++; return true; };
                mover.MoveApi.Focus = delegate { focus++; return true; };
                try {
                    bool moved = mover.MoveToMonitor(window.Handle, destination.Handle);
                    check(!moved && mover.LastStatus.Contains("cancelled") && invalidWhileEntered,
                        "Escape/search close during accepted shared movement invalidates its native operation guard immediately");
                    check(positions == 1 && placements == 0 && focus == 0,
                        "After Ghost search cancellation no later placement, correction, or focus request is queued");
                    check(!session.Active && popup.IsDisposed && !guard.Busy,
                        "Native cancellation closes the owned search session and releases its operation after unwind");
                    check(mover.CanUndo, "A possibly accepted partial Ghost move retains Undo recovery when cancellation interrupts the next substep");
                    var history = (WindowHistory)typeof(TaskbarMover).GetField("history", Fields).GetValue(mover);
                    var records = (Stack<WindowHistory.Entry>)typeof(WindowHistory).GetField("entries", Fields).GetValue(history);
                    var saved = records.Peek();
                    check(saved.Window == before.Window && saved.Process == before.Process && saved.ProcessStarted == before.ProcessStarted &&
                        saved.Placement.ShowCommand == before.Placement.ShowCommand && AreaPolicy.Equal(saved.Placement.NormalPosition, before.Placement.NormalPosition),
                        "Interrupted Ghost movement retains the original HWND/process/birth and pre-move placement recovery record");
                    Application.DoEvents();
                    check(positions == 1 && placements == 0 && focus == 0,
                        "Later UI messages cannot resume cancelled native Ghost move substeps");
                } finally { Set(manager, "searchMoving", false); }
            }
        }
        static void ClosedManagerLaunchLease(Action<bool, string> check)
        {
            var destination = new Display { Device = "fixture", Identity = "physical-fixture", Width = 1920, Height = 1080 };
            var app = new GhostApp { Id = "fixture", Name = "Fixture app", AppKey = @"exe:C:\Fixtures\App.exe", LaunchPath = @"C:\Fixtures\App.lnk" };
            var oldWorkers = new Queue<Action>(); var newWorkers = new Queue<Action>();
            int oldLaunches = 0, newLaunches = 0, selections = 0;
            using (var oldManager = Manager(delegate { throw new Exception("No native movement is active in the cross-session fixture."); }))
            using (var replacementManager = Manager(delegate { throw new Exception("No native movement is active in the replacement fixture."); }))
            using (var oldSession = new GhostSearchSession())
            using (var newSession = new GhostSearchSession()) {
                var oldPopup = Popup();
                GhostLaunchCoordinator replacement = null;
                using (var newPopup = new GhostSearchPopup(destination, new Rectangle(0, 0, 1920, 1040), 96, 1000,
                    delegate { selections++; replacement.Begin(app, destination); }, null))
                using (replacement = new GhostLaunchCoordinator(delegate { return new List<Display> { destination }; },
                    delegate { return new List<AppRule>(); }, delegate { return newSession.Active; }, delegate { return new List<ShelfWindow>(); },
                    delegate { throw new Exception("Fake cross-session app has no native window to move."); },
                    delegate { newLaunches++; return new GhostLaunchResult { Started = true }; }, null,
                    delegate { return 100; }, 1000, delegate(Action worker) { newWorkers.Enqueue(worker); }))
                using (var old = new GhostLaunchCoordinator(delegate { return new List<Display> { destination }; },
                    delegate { return new List<AppRule>(); }, delegate { return oldSession.Active; }, delegate { return new List<ShelfWindow>(); },
                    delegate { throw new Exception("Cancelled fixture must never move a window."); },
                    delegate {
                        oldLaunches++;
                        // The real manager close path disposes its coordinator.
                        Call(oldManager, "CloseSearchAnimated");
                        check(!oldSession.Active && oldPopup.IsDisposed && GhostLaunchCoordinator.NativeLaunchPending,
                            "Closing a manager-owned search leaves its entered external launch lease reserved");
                        Set(replacementManager, "search", newPopup); Set(replacementManager, "searchSession", newSession);
                        Set(replacementManager, "searchLaunch", replacement);
                        newPopup.SetCatalog(new List<GhostApp> { app }); newPopup.Query = "Fixture";
                        Call(replacementManager, "UpdateSearchStatus"); newPopup.HandleSearchKey(Keys.Enter);
                        check(newPopup.Busy && selections == 0 && newWorkers.Count == 0 && newLaunches == 0,
                            "A newly created manager's search can load and filter but cannot submit a second app launch while the old launch is entered");
                        check(!replacement.Begin(app, destination) && replacement.Status.Contains("previous app request"),
                            "Direct retry admission is also blocked across manager recreation while native launch is pending");
                        return new GhostLaunchResult { Started = true };
                    }, null, delegate { return 100; }, 1000, delegate(Action worker) { oldWorkers.Enqueue(worker); })) {
                    Set(oldManager, "search", oldPopup); Set(oldManager, "searchSession", oldSession); Set(oldManager, "searchLaunch", old);
                    old.Begin(app, destination); oldWorkers.Dequeue()(); old.Poll(); oldWorkers.Dequeue()(); old.Poll();
                    check(oldLaunches == 1 && newLaunches == 0 && !GhostLaunchCoordinator.NativeLaunchPending,
                        "An old cancelled launch returning releases its lease without automatically retrying from a reopened search");
                    Call(replacementManager, "UpdateSearchStatus"); newPopup.HandleSearchKey(Keys.Enter);
                    check(!newPopup.Busy && selections == 1 && newWorkers.Count == 1,
                        "The reopened search permits an explicit selection after the prior external launch unwinds");
                    newWorkers.Dequeue()(); replacement.Poll(); newWorkers.Dequeue()(); replacement.Poll();
                    check(newLaunches == 1 && replacement.Started,
                        "Only the deliberate post-unwind selection starts the replacement search's app launch");
                    replacement.Cancel(); while (newWorkers.Count != 0) newWorkers.Dequeue()();
                }
            }
        }
    }
}
