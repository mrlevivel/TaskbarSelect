using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Controller-level fixture: real owned HWND/process identity and read-only
    // Explorer geometry, asynchronous fake target evidence, no desktop hooks,
    // input injection, native move, foreground request, profile or helper launch.
    internal static class OrdinaryDeferredClickIntegrationTests
    {
        const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly MethodInfo Begin = typeof(TaskbarMover).GetMethod("TryStartDeferredClick", Hidden);
        static readonly MethodInfo Release = typeof(TaskbarMover).GetMethod("ReleaseDeferredClick", Hidden);
        static readonly MethodInfo ProcessPending = typeof(TaskbarMover).GetMethod("ProcessDeferredClick", Hidden);
        static readonly FieldInfo Pending = typeof(TaskbarMover).GetField("deferredClick", Hidden);
        delegate bool EnumProc(IntPtr hwnd, IntPtr state);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr state);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetCursorPos(out TaskbarMover.Point point);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

        sealed class Subject : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            internal Subject(string title, Rectangle bounds)
            {
                Text = title; Bounds = bounds; StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = true;
                // Do not add WS_EX_NOACTIVATE: own peek windows are deliberately
                // excluded from the app catalog. ShowWithoutActivation alone
                // displays a normal candidate without changing actual focus.
                FormBorderStyle = FormBorderStyle.FixedSingle;
            }
        }
        sealed class Context
        {
            internal IntPtr Taskbar, Destination;
            internal Native.Rect TaskbarBounds;
            internal TaskbarMover.Point Point;
            internal Rectangle Work;
        }
        sealed class Fixture : IDisposable
        {
            internal readonly ManualResetEvent ReaderEntered = new ManualResetEvent(false), ReaderRelease = new ManualResetEvent(false);
            internal readonly TaskbarTargets Targets;
            internal readonly TaskbarMover Mover;
            internal int ReplayCount, CaptureCount, NativeMutationCount;
            internal bool LastReplayReleased;
            internal readonly bool HasReplayObservation;
            internal TaskbarMover.Point LastReplayStart, LastReplayEnd;
            internal object Request;
            readonly Context context;

            internal Fixture(Context context, Subject subject, Subject other, bool unrelated)
            {
                this.context = context;
                IntPtr subjectWindow = subject.Handle, otherWindow = other.Handle;
                Targets = new TaskbarTargets(delegate(IntPtr taskbar, TaskbarMover.Point point) {
                    ReaderEntered.Set();
                    if (!ReaderRelease.WaitOne(3000)) return null;
                    IntPtr selected = unrelated ? otherWindow : subjectWindow;
                    return TargetFor(context, selected);
                });
                Func<TaskbarMover.Point, TaskbarMover.Point, bool, bool> replay = delegate(TaskbarMover.Point from, TaskbarMover.Point to, bool released) {
                    ReplayCount++; LastReplayStart = from; LastReplayEnd = to; LastReplayReleased = released; return true;
                };
                Func<IntPtr> foreground = delegate { return subjectWindow; };
                Type observationType = typeof(TaskbarMover).GetNestedType("ReplayObservation", BindingFlags.NonPublic);
                if (observationType == null) {
                    // Keeps this regression buildable against the old controller:
                    // its ordinary admission assertion fails before replay is used.
                    Mover = new TaskbarMover(Targets, replay, foreground, false);
                } else {
                    object observation = Activator.CreateInstance(observationType);
                    observationType.GetField("Cursor", Hidden).SetValue(observation, new Func<TaskbarMover.Point?>(delegate { return context.Point; }));
                    observationType.GetField("RootAtPoint", Hidden).SetValue(observation, new Func<TaskbarMover.Point, IntPtr>(delegate { return context.Taskbar; }));
                    observationType.GetField("LeftButtonDown", Hidden).SetValue(observation, new Func<bool>(delegate { return false; }));
                    var constructor = typeof(TaskbarMover).GetConstructors(Hidden).Single(c => c.GetParameters().Length == 5 && c.GetParameters()[4].ParameterType == observationType);
                    Mover = (TaskbarMover)constructor.Invoke(new object[] { Targets, replay, foreground, false, observation });
                    HasReplayObservation = true;
                }
                Mover.MoveApi.Capture = delegate { CaptureCount++; return null; };
                Mover.MoveApi.Position = delegate { NativeMutationCount++; throw new InvalidOperationException("Fixture must never request native positioning"); };
                Mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                    NativeMutationCount++; throw new InvalidOperationException("Fixture must never request native placement");
                };
                Mover.MoveApi.Focus = delegate { NativeMutationCount++; throw new InvalidOperationException("Fixture must never request foreground"); };
                Mover.MoveApi.Modifiers = delegate { return 0; };
                Mover.Enabled = true;
            }
            internal bool Admit()
            {
                // Simulate the ordinary injected down provenance directly at
                // the controller seam. No OS mouse event is generated.
                bool result = (bool)Begin.Invoke(Mover, new object[] { null, context.Taskbar, context.Destination,
                    context.Point, false, unchecked((uint)Environment.TickCount) });
                Request = Pending.GetValue(Mover); return result;
            }
            internal void Up()
            {
                Type type = typeof(TaskbarMover).GetNestedType("MouseData", BindingFlags.NonPublic);
                object mouse = Activator.CreateInstance(type);
                type.GetField("Position", Hidden).SetValue(mouse, context.Point);
                type.GetField("Flags", Hidden).SetValue(mouse, 1u); // Match the simulated injected down.
                type.GetField("Time", Hidden).SetValue(mouse, unchecked((uint)Environment.TickCount));
                Release.Invoke(Mover, new[] { mouse });
            }
            internal TaskbarTargets.Probe Probe
            { get { return Request == null ? null : (TaskbarTargets.Probe)Request.GetType().GetField("Verification", Hidden).GetValue(Request); } }
            internal void Process() { ProcessPending.Invoke(Mover, null); }
            public void Dispose()
            {
                ReaderRelease.Set(); Mover.Enabled = false; Mover.Dispose();
                Wait(delegate { return TaskbarTargets.ActiveReaders == 0; }, 2000);
                ReaderEntered.Dispose(); ReaderRelease.Dispose();
            }
        }
        static bool Wait(Func<bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (!condition()) { if (clock.ElapsedMilliseconds >= milliseconds) return condition(); Thread.Sleep(2); }
            return true;
        }
        static TaskbarTargets.Target TargetFor(Context context, IntPtr selected)
        {
            uint process; Native.GetWindowThreadProcessId(selected, out process);
            var app = AppCatalog.ForWindow(selected);
            return new TaskbarTargets.Target {
                Taskbar = context.Taskbar, TaskbarBounds = context.TaskbarBounds,
                Window = selected, Process = process, ProcessStarted = NativeProcessIdentity.Started(process),
                AppKey = app == null ? "" : app.Key, Captured = DateTime.UtcNow,
                Button = new TaskbarMover.Click { Taskbar = context.Taskbar, Position = context.Point,
                    AppId = System.Diagnostics.Process.GetCurrentProcess().ProcessName,
                    AppName = "Owned deferred regression subject", ButtonBounds = context.TaskbarBounds },
                Windows = new[] { new TaskbarTargets.WindowIdentity { Window = selected, Process = process } }
            };
        }
        static Context FindContext()
        {
            IntPtr bar = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused) {
                if (!IsWindowVisible(hwnd)) return true;
                var name = new StringBuilder(64); GetClassName(hwnd, name, name.Capacity);
                if (name.ToString() == "Shell_TrayWnd") { bar = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            Native.Rect bounds; uint shell; long shellStarted;
            if (bar == IntPtr.Zero || !Native.GetWindowRect(bar, out bounds) ||
                !TaskManagerShellIdentity.ReadTaskbar(bar, out shell, out shellStarted)) return null;
            var destinationScreen = Screen.FromHandle(bar);
            var source = Screen.AllScreens.Where(s => s.DeviceName != destinationScreen.DeviceName && s.WorkingArea.Width >= 520 && s.WorkingArea.Height >= 360)
                .OrderByDescending(s => (long)s.WorkingArea.Width * s.WorkingArea.Height).FirstOrDefault();
            if (source == null) return null;
            return new Context { Taskbar = bar, Destination = Native.MonitorFromWindow(bar, 2), TaskbarBounds = bounds,
                Point = new TaskbarMover.Point { X = bounds.Left + (bounds.Right - bounds.Left) / 2, Y = bounds.Top + (bounds.Bottom - bounds.Top) / 2 }, Work = source.WorkingArea };
        }
        internal static void Run(Action<bool, string> check)
        {
            if (Begin == null || Release == null || ProcessPending == null || Pending == null) throw new InvalidOperationException("Deferred controller seams unavailable");
            var support = WindowMoveWorker.Administrator;
            if (support != null && (support.Active || support.HasUncertainOperation)) {
                Console.WriteLine("SKIP ordinary deferred controller: administrator support must already be Off; fixture never changes it."); return;
            }
            foreach (int key in new[] { 1, 2, 4, 5, 6, 16, 17, 18, 91, 92 }) if ((GetAsyncKeyState(key) & 0x8000) != 0) {
                Console.WriteLine("SKIP ordinary deferred controller: input is held; no desktop input was generated."); return;
            }
            Context context = FindContext();
            if (context == null) { Console.WriteLine("SKIP ordinary deferred controller: needs two existing monitors and a readable visible native main taskbar; setup is unchanged."); return; }
            var identity = NativeProcessIdentity.Current;
            if (!identity.IntegrityAvailable || identity.Started <= 0) { Console.WriteLine("SKIP ordinary deferred controller: current process integrity/start is unreadable."); return; }
            IntPtr foreground = GetForegroundWindow(); TaskbarMover.Point cursor;
            if (!GetCursorPos(out cursor)) throw new InvalidOperationException("Cannot record unchanged cursor prerequisite");
            int width = Math.Min(640, context.Work.Width - 40), height = Math.Min(420, context.Work.Height - 40);
            using (var subject = new Subject("Owned ordinary deferred controller subject", new Rectangle(context.Work.X + 20, context.Work.Y + 20, width, height)))
            using (var other = new Subject("Owned unrelated deferred controller subject", new Rectangle(context.Work.X + 35, context.Work.Y + 35, width, height))) {
                subject.Show(); other.Show();
                check(GetForegroundWindow() == foreground, "Owned controller subjects show without requesting or changing actual focus");
                check(WindowShelfCatalog.IsTaskbarCandidate(subject.Handle), "The no-activation display operation still creates an ordinary eligible app window");
                Rectangle original = subject.Bounds, otherOriginal = other.Bounds;
                MatchingProof(context, subject, other, check);
                UnrelatedProof(context, subject, other, check);
                TimedOutProof(context, subject, other, check);
                WarmProvenance(context, subject, other, check);
                check(subject.Bounds == original && other.Bounds == otherOriginal && subject.WindowState == FormWindowState.Normal && other.WindowState == FormWindowState.Normal,
                    "Controller regression never moves or minimizes either owned subject");
            }
            TaskbarMover.Point after; check(GetCursorPos(out after) && after.X == cursor.X && after.Y == cursor.Y && GetForegroundWindow() == foreground,
                "Controller fixture leaves actual mouse position and foreground unchanged");
            check(TaskbarTargets.ActiveReaders == 0, "Controller fixture releases every asynchronous target reader");
        }
        static bool Admitted(Fixture fixture, Action<bool, string> check, string name)
        {
            bool admitted = fixture.Admit();
            check(admitted && fixture.Request != null, name + ": cold ordinary cross-monitor down is owned with administrator support Off");
            if (!admitted) return false;
            check(fixture.HasReplayObservation, name + ": replay observes injected read-only cursor/hit state rather than the user's actual mouse");
            check(new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" }.All(field => (IntPtr)typeof(TaskbarMover).GetField(field, Hidden).GetValue(fixture.Mover) == IntPtr.Zero),
                name + ": the controller registers no desktop hook");
            check(fixture.CaptureCount == 0 && fixture.ReplayCount == 0, name + ": down alone neither moves nor replays before release/proof");
            fixture.Up();
            check(Wait(delegate { return fixture.ReaderEntered.WaitOne(0); }, 300), name + ": release starts the real asynchronous target verification");
            fixture.Process();
            check(fixture.CaptureCount == 0 && fixture.ReplayCount == 0 && Pending.GetValue(fixture.Mover) != null,
                name + ": a released cold gesture remains pending until evidence arrives");
            return true;
        }
        static void MatchingProof(Context context, Subject subject, Subject other, Action<bool, string> check)
        {
            using (var fixture = new Fixture(context, subject, other, false)) {
                if (!Admitted(fixture, check, "Matching proof")) return;
                fixture.ReaderRelease.Set();
                check(Wait(delegate { return fixture.Probe != null && fixture.Probe.Complete; }, 350), "Matching proof: fresh owned target evidence completes within the original budget");
                fixture.Process(); fixture.Process();
                check(fixture.CaptureCount == 1 && fixture.NativeMutationCount == 0,
                    "Matching proof reaches one actual controller movement dispatch, stopped safely before any native mutation");
                check(fixture.ReplayCount == 1 && fixture.LastReplayReleased && Pending.GetValue(fixture.Mover) == null,
                    "A verified move rejected before mutation restores the owned native click exactly once");
                check(fixture.LastReplayStart.X == context.Point.X && fixture.LastReplayStart.Y == context.Point.Y &&
                    fixture.LastReplayEnd.X == context.Point.X && fixture.LastReplayEnd.Y == context.Point.Y,
                    "Replay uses the captured gesture coordinates through the no-input test delegate");
            }
        }
        static void UnrelatedProof(Context context, Subject subject, Subject other, Action<bool, string> check)
        {
            using (var fixture = new Fixture(context, subject, other, true)) {
                if (!Admitted(fixture, check, "Unrelated proof")) return;
                fixture.ReaderRelease.Set();
                check(Wait(delegate { return fixture.Probe != null && fixture.Probe.Complete; }, 350), "Unrelated proof completes as fresh evidence rather than a fabricated timeout");
                fixture.Process(); fixture.Process();
                check(fixture.CaptureCount == 0 && fixture.NativeMutationCount == 0 && fixture.ReplayCount == 1 && Pending.GetValue(fixture.Mover) == null,
                    "Fresh evidence for another owned window cannot move it and returns the original native click once");
            }
        }
        static void TimedOutProof(Context context, Subject subject, Subject other, Action<bool, string> check)
        {
            using (var fixture = new Fixture(context, subject, other, false)) {
                if (!Admitted(fixture, check, "Expired proof")) return;
                Thread.Sleep(DeferredTaskbarClick.TimeoutMilliseconds + 25);
                fixture.Process(); fixture.Process();
                check(fixture.CaptureCount == 0 && fixture.NativeMutationCount == 0 && fixture.ReplayCount == 1 && Pending.GetValue(fixture.Mover) == null,
                    "The original bounded wait expires without movement and replays the released click once");
                fixture.ReaderRelease.Set(); Wait(delegate { return TaskbarTargets.ActiveReaders == 0; }, 1000); fixture.Process();
                check(fixture.CaptureCount == 0 && fixture.ReplayCount == 1, "A late provider result cannot revive the settled gesture or replay it twice");
            }
        }
        static void WarmProvenance(Context context, Subject subject, Subject other, Action<bool, string> check)
        {
            FieldInfo press = typeof(TaskbarMover).GetField("directPress", Hidden), released = typeof(TaskbarMover).GetField("directRelease", Hidden);
            foreach (bool physicalDown in new[] { false, true }) foreach (bool physicalUp in new[] { false, true }) {
                using (var fixture = new Fixture(context, subject, other, false)) {
                    string name = "Warm ordinary " + (physicalDown ? "physical" : "injected") + " down / " + (physicalUp ? "physical" : "injected") + " up";
                    bool admitted = fixture.Mover.TryStartDirectMove(TargetFor(context, subject.Handle), context.Destination, context.Point,
                        physicalDown, unchecked((uint)Environment.TickCount));
                    check(admitted && press.GetValue(fixture.Mover) != null, name + ": a proven ordinary cached target admits either input provenance");
                    if (!admitted) continue;
                    fixture.Mover.CompleteDirectPress(true, physicalUp, unchecked((uint)Environment.TickCount));
                    bool matching = physicalDown == physicalUp;
                    check(press.GetValue(fixture.Mover) == null && (released.GetValue(fixture.Mover) != null) == matching,
                        name + ": only matching down/up provenance queues fresh verification");
                    check(fixture.CaptureCount == 0 && fixture.NativeMutationCount == 0 && fixture.ReplayCount == 0,
                        name + ": completion alone performs no native move or replay before fresh evidence");
                    // Dispose settles this queued request before it can dispatch;
                    // the blocked provider is released only during fixture cleanup.
                }
            }
        }
    }
}
