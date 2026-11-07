using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Native regression fixtures are all disposable and never save app settings.
    internal static class WindowMoveRecoveryTests
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        static readonly FieldInfo returnRecord = typeof(TaskbarMover).GetField("lastMove", BindingFlags.Instance | BindingFlags.NonPublic);

        sealed class FixtureWindow : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
        }
        sealed class Fixture : IDisposable
        {
            internal readonly FixtureWindow Prior, Candidate;
            internal readonly TaskbarMover Mover;
            internal readonly IntPtr Source, Target;
            internal readonly Native.MonitorInfo TargetInfo;
            internal readonly WindowHistory.Entry PriorBefore, CandidateBefore;
            internal readonly object EarlierReturn;
            internal readonly FormWindowState State;

            internal Fixture(List<IntPtr> monitors, List<Native.MonitorInfo> infos,
                bool relative, FormWindowState state, Action<bool, string> assert)
            {
                Source = monitors[0]; Target = monitors[1]; TargetInfo = infos[1]; State = state;
                Prior = new FixtureWindow { Text = "Owned earlier Undo fixture", Opacity = 0, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Bounds = FixtureBounds(infos[0].Work, 640, 480) };
                Candidate = new FixtureWindow { Text = "Owned movement recovery fixture", Opacity = 0, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Bounds = FixtureBounds(infos[0].Work, 620, 460) };
                Mover = new TaskbarMover(); Mover.ScaleWindowSizeToMonitor = relative;
                try {
                    Prior.Show(); Candidate.Show(); Application.DoEvents();
                    PriorBefore = WindowHistory.Capture(Prior.Handle);
                    assert(PriorBefore != null && Mover.MoveToMonitor(Prior.Handle, Target) && Mover.CanUndo,
                        "Recovery fixture establishes a real earlier move with Undo");
                    Application.DoEvents();
                    EarlierReturn = returnRecord.GetValue(Mover);
                    assert(EarlierReturn != null, "Recovery fixture establishes immediate-return memory");
                    Candidate.WindowState = state; Application.DoEvents();
                    CandidateBefore = WindowHistory.Capture(Candidate.Handle);
                    assert(CandidateBefore != null, "Recovery fixture captures native state before an induced failure");
                } catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                Mover.Dispose(); Candidate.Dispose(); Prior.Dispose();
            }
            internal void CheckEarlierUndo(Action<bool, string> assert)
            {
                assert(Mover.Undo(), "An earlier move remains undoable after a rejected move"); Application.DoEvents();
                var restored = WindowHistory.Capture(Prior.Handle);
                assert(restored != null && SamePlacement(PriorBefore, restored) && Native.MonitorFromWindow(Prior.Handle, 2) == Source && !Mover.CanUndo,
                    "Rejected movement leaves exactly the earlier Undo and its original source placement");
            }
            internal void CheckPartialUndo(Action<bool, string> assert)
            {
                assert(returnRecord.GetValue(Mover) == null, "A partial native mutation invalidates the earlier immediate-return record");
                assert(Mover.CanUndo && Mover.Undo(), "A partially applied move retains its own recovery Undo"); Application.DoEvents();
                var restored = WindowHistory.Capture(Candidate.Handle);
                assert(restored != null && SamePlacement(CandidateBefore, restored) && Native.MonitorFromWindow(Candidate.Handle, 2) == Source &&
                    Candidate.WindowState == (State == FormWindowState.Minimized ? FormWindowState.Normal : State),
                    "Partial-move Undo restores the original monitor, bounds and visible normal/maximized state");
                assert(Mover.CanUndo, "Undoing the partial move preserves the older move underneath it");
                CheckEarlierUndo(assert);
            }
        }
        static bool SamePlacement(WindowHistory.Entry a, WindowHistory.Entry b)
        {
            return a.Placement.ShowCommand == b.Placement.ShowCommand &&
                AreaPolicy.Equal(a.Placement.NormalPosition, b.Placement.NormalPosition);
        }
        static Rectangle FixtureBounds(Native.Rect work, int preferredWidth, int preferredHeight)
        {
            int width = work.Right - work.Left, height = work.Bottom - work.Top;
            int marginX = Math.Min(40, Math.Max(0, width / 6)), marginY = Math.Min(40, Math.Max(0, height / 6));
            return new Rectangle(work.Left + marginX, work.Top + marginY,
                Math.Max(1, Math.Min(preferredWidth, width - 2 * marginX)),
                Math.Max(1, Math.Min(preferredHeight, height - 2 * marginY)));
        }
        static bool WaitForRestore(FixtureWindow window)
        {
            var elapsed = Stopwatch.StartNew();
            while (IsIconic(window.Handle) && elapsed.ElapsedMilliseconds < 1000) {
                Application.DoEvents(); Thread.Sleep(5);
            }
            return !IsIconic(window.Handle) && window.WindowState == FormWindowState.Normal;
        }
        static void MissingCapture(List<IntPtr> monitors, List<Native.MonitorInfo> infos, bool relative,
            FormWindowState state, Action<bool, string> assert)
        {
            using (var fixture = new Fixture(monitors, infos, relative, state, assert)) {
                int positions = 0, placements = 0, captures = 0;
                fixture.Mover.MoveApi.Capture = delegate(IntPtr window) { captures++; return null; };
                fixture.Mover.MoveApi.Position = delegate(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags) {
                    positions++; return true;
                };
                fixture.Mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                    placements++; return true;
                };
                assert(!fixture.Mover.MoveToMonitor(fixture.Candidate.Handle, fixture.Target) && captures == 1 && positions == 0 && placements == 0,
                    "Missing pre-move capture rejects " + state + " before any native mutation, relative=" + relative);
                var unchanged = WindowHistory.Capture(fixture.Candidate.Handle);
                assert(unchanged != null && SamePlacement(fixture.CandidateBefore, unchanged) && fixture.Candidate.WindowState == state &&
                    Native.MonitorFromWindow(fixture.Candidate.Handle, 2) == fixture.Source,
                    "Missing capture leaves the owned window's placement and state unchanged");
                assert(Object.ReferenceEquals(returnRecord.GetValue(fixture.Mover), fixture.EarlierReturn),
                    "Missing capture preserves the previous immediate-return record");
                fixture.CheckEarlierUndo(assert);
            }
        }
        static void InitialFailure(List<IntPtr> monitors, List<Native.MonitorInfo> infos, bool relative,
            FormWindowState state, Action<bool, string> assert)
        {
            using (var fixture = new Fixture(monitors, infos, relative, state, assert)) {
                int positions = 0, placements = 0;
                fixture.Mover.MoveApi.Position = delegate(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags) {
                    positions++; return false;
                };
                fixture.Mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                    placements++; return false;
                };
                assert(!fixture.Mover.MoveToMonitor(fixture.Candidate.Handle, fixture.Target) &&
                    positions == (state == FormWindowState.Minimized ? 0 : 1) && placements == (state == FormWindowState.Minimized ? 1 : 0),
                    "Initial native failure does not run a later mutation for " + state + ", relative=" + relative);
                var unchanged = WindowHistory.Capture(fixture.Candidate.Handle);
                assert(unchanged != null && SamePlacement(fixture.CandidateBefore, unchanged) && fixture.Candidate.WindowState == state &&
                    Native.MonitorFromWindow(fixture.Candidate.Handle, 2) == fixture.Source,
                    "A failed initial native operation leaves state and placement unchanged");
                assert(Object.ReferenceEquals(returnRecord.GetValue(fixture.Mover), fixture.EarlierReturn),
                    "Initial native failure preserves previous immediate-return memory");
                fixture.CheckEarlierUndo(assert);
            }
        }
        static void LaterPlacementFailure(List<IntPtr> monitors, List<Native.MonitorInfo> infos, bool relative,
            bool throws, Action<bool, string> assert)
        {
            using (var fixture = new Fixture(monitors, infos, relative, FormWindowState.Maximized, assert)) {
                int placements = 0;
                fixture.Mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                    placements++;
                    if (throws) throw new InvalidOperationException("Owned placement failure");
                    return false;
                };
                bool moved = false, caught = false;
                try { moved = fixture.Mover.MoveToMonitor(fixture.Candidate.Handle, fixture.Target); }
                catch (InvalidOperationException error) { if (!throws || error.Message != "Owned placement failure") throw; caught = true; }
                assert(!moved && caught == throws && placements == 1 && Native.MonitorFromWindow(fixture.Candidate.Handle, 2) == fixture.Target,
                    "Second-step placement " + (throws ? "exception" : "failure") + " follows a real first-step native move, relative=" + relative);
                fixture.CheckPartialUndo(assert);
            }
        }
        static void CorrectivePositionFailure(List<IntPtr> monitors, List<Native.MonitorInfo> infos, bool relative,
            Action<bool, string> assert)
        {
            using (var fixture = new Fixture(monitors, infos, relative, FormWindowState.Minimized, assert)) {
                var placementApi = fixture.Mover.MoveApi.Placement;
                int placements = 0, positions = 0;
                fixture.Mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                    placements++;
                    bool accepted = placementApi(window, ref placement);
                    if (accepted) {
                        assert(WaitForRestore(fixture.Candidate), "Owned minimized fixture completes the real native restore before its correction");
                        // Simulate an app imposing its own floating bounds during restore.
                        // Only this disposable source window changes; the native restore is real.
                        fixture.Candidate.Bounds = FixtureBounds(fixture.TargetInfo.Work, 400, 300);
                        var requested = placement.NormalPosition;
                        int dx = fixture.TargetInfo.Work.Left - fixture.TargetInfo.Monitor.Left;
                        int dy = fixture.TargetInfo.Work.Top - fixture.TargetInfo.Monitor.Top;
                        requested.Left += dx; requested.Right += dx; requested.Top += dy; requested.Bottom += dy;
                        Native.Rect actual;
                        if (Native.GetWindowRect(window, out actual) && AreaPolicy.Equal(actual, requested)) fixture.Candidate.Left++;
                        Application.DoEvents();
                    }
                    return accepted;
                };
                fixture.Mover.MoveApi.Position = delegate(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags) {
                    positions++; return false;
                };
                assert(!fixture.Mover.MoveToMonitor(fixture.Candidate.Handle, fixture.Target) && placements == 1 && positions == 1 &&
                    fixture.Candidate.WindowState == FormWindowState.Normal && Native.MonitorFromWindow(fixture.Candidate.Handle, 2) == fixture.Target,
                    "A failed corrective position follows a completed native restore and real app-imposed bounds, relative=" + relative);
                fixture.CheckPartialUndo(assert);
            }
        }
        static void LaterCaptureFailure(List<IntPtr> monitors, List<Native.MonitorInfo> infos, bool relative,
            bool throws, Action<bool, string> assert)
        {
            using (var fixture = new Fixture(monitors, infos, relative, FormWindowState.Normal, assert)) {
                var captureApi = fixture.Mover.MoveApi.Capture;
                int captures = 0;
                fixture.Mover.MoveApi.Capture = delegate(IntPtr window) {
                    if (++captures == 1) return captureApi(window);
                    if (throws) throw new InvalidOperationException("Owned post-move capture failure");
                    return null;
                };
                bool moved = false, caught = false;
                try { moved = fixture.Mover.MoveToMonitor(fixture.Candidate.Handle, fixture.Target); }
                catch (InvalidOperationException error) { if (!throws || error.Message != "Owned post-move capture failure") throw; caught = true; }
                assert(captures == 2 && moved == !throws && caught == throws && Native.MonitorFromWindow(fixture.Candidate.Handle, 2) == fixture.Target,
                    "Post-mutation capture " + (throws ? "exception" : "unavailability") + " follows a real normal move, relative=" + relative);
                fixture.CheckPartialUndo(assert);
            }
        }
        internal static void Run(Action<bool, string> assert)
        {
            var monitors = new List<IntPtr>(); var infos = new List<Native.MonitorInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(monitor, ref info)) { monitors.Add(monitor); infos.Add(info); } return true;
            }, IntPtr.Zero);
            if (monitors.Count < 2) { Console.WriteLine("SKIP: movement recovery requires two monitors."); return; }
            int checks = 0; Action<bool, string> check = delegate(bool condition, string message) { checks++; assert(condition, message); };
            IntPtr prior = GetForegroundWindow();
            try {
                foreach (bool relative in new[] { false, true }) {
                    foreach (var state in new[] { FormWindowState.Normal, FormWindowState.Maximized, FormWindowState.Minimized }) {
                        MissingCapture(monitors, infos, relative, state, check);
                        InitialFailure(monitors, infos, relative, state, check);
                    }
                    LaterPlacementFailure(monitors, infos, relative, false, check);
                    LaterPlacementFailure(monitors, infos, relative, true, check);
                    CorrectivePositionFailure(monitors, infos, relative, check);
                    LaterCaptureFailure(monitors, infos, relative, false, check);
                    LaterCaptureFailure(monitors, infos, relative, true, check);
                }
                Console.WriteLine("PASS: " + checks + " native movement recovery checks.");
            } finally { if (prior != IntPtr.Zero && Native.IsWindow(prior)) SetForegroundWindow(prior); }
        }
    }
}
