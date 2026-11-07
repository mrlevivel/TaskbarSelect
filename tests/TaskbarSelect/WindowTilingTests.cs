using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class WindowTilingTests
    {
        class OwnedWindow : Form
        {
            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
            [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr window, int index, int value);
            protected override bool ShowWithoutActivation { get { return true; } }
            internal bool WithoutActivation(Func<bool> action)
            {
                int style = GetWindowLong(Handle, -20);
                SetWindowLong(Handle, -20, style | 0x08000000);
                try { return action(); }
                finally { SetWindowLong(Handle, -20, style); }
            }
            internal void SetState(FormWindowState state)
            { WithoutActivation(delegate { WindowState = state; return true; }); Pump(); }
        }
        static bool Undo(TaskbarMover mover, OwnedWindow window)
        { return window.WithoutActivation(mover.Undo); }
        static void Pump()
        { for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); } }
        static Native.Rect Bounds(IntPtr window)
        { Native.Rect value; if (!Native.GetWindowRect(window, out value)) throw new Exception("Owned fixture bounds unavailable"); return value; }
        static Native.MonitorInfo Monitor(IntPtr window)
        {
            var value = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref value)) throw new Exception("Owned fixture monitor unavailable");
            return value;
        }
        static ShelfWindow Selection(Form window)
        {
            ShelfWindow value;
            if (!WindowShelfCatalog.TryReadWindow(window.Handle, true, out value)) throw new Exception("Owned fixture selection unavailable");
            return value;
        }
        static TaskbarMover Mover()
        {
            var mover = new TaskbarMover(new TaskbarTargets(), TaskbarInput.Replay, TaskbarMover.ForegroundWindow, false);
            mover.MoveApi.Modifiers = delegate { return 0; }; mover.MoveApi.Focus = delegate { return false; };
            return mover;
        }
        internal static void Run(Action<bool, string> check)
        {
            var foreground = TaskbarMover.ForegroundWindow();
            MinimumGeometry(check);
            OwnedLayouts(check);
            check(TaskbarMover.ForegroundWindow() == foreground, "Owned tiling and Undo fixtures preserve desktop foreground");
            OwnedFailures(check);
            check(TaskbarMover.ForegroundWindow() == foreground, "Owned failed tiling fixtures preserve desktop foreground");
            FullscreenAndFixedSize(check);
            check(TaskbarMover.ForegroundWindow() == foreground, "Owned fullscreen and fixed-size fixtures preserve desktop foreground");
        }
        static void MinimumGeometry(Action<bool, string> check)
        {
            foreach (Rectangle work in new[] { new Rectangle(-1920, -1080, 1920, 1032), new Rectangle(0, 0, 801, 601), new Rectangle(20, -1500, 1600, 1460) }) {
                foreach (WindowTile tile in Enum.GetValues(typeof(WindowTile))) {
                    var wanted = WindowTilingPolicy.Target(work, new Rectangle(work.X, work.Y, 400, 300), tile);
                    var fitted = WindowTilingNative.FitMinimum(work, wanted, new Size(700, 500), tile);
                    check(work.Contains(fitted) && fitted.Width >= 700 && fitted.Height >= 500, "Minimum-size layout remains within negative/stacked/odd physical work area: " + tile);
                    check(WindowTilingNative.FitMinimum(work, wanted, new Size(work.Width + 1, 1), tile).IsEmpty,
                        "A minimum wider than this display refuses without producing oversized bounds: " + tile);
                }
            }
        }
        static void OwnedLayouts(Action<bool, string> check)
        {
            using (var window = new OwnedWindow { Text = "Owned focus and tiling regression", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(Screen.PrimaryScreen.WorkingArea.Left + 100, Screen.PrimaryScreen.WorkingArea.Top + 100, 560, 400) })
            using (var mover = Mover()) {
                window.Show(); Pump();
                foreach (WindowTile tile in Enum.GetValues(typeof(WindowTile))) {
                    var before = WindowHistory.Capture(window.Handle); var original = Bounds(window.Handle); var source = Monitor(window.Handle);
                    check(mover.TileWindow(Selection(window), source, original, tile), "Owned native tiling observes requested geometry for " + tile + ": " + mover.LastStatus);
                    var actual = WindowTilingNative.VisibleBounds(window.Handle, Bounds(window.Handle));
                    var work = Rectangle.FromLTRB(source.Work.Left, source.Work.Top, source.Work.Right, source.Work.Bottom);
                    check(work.Contains(actual), "Owned native visible frame respects usable monitor area for " + tile);
                    check(mover.CanUndo, "Owned native tiling records shared Undo for " + tile);
                    check(mover.TileWindow(Selection(window), source, Bounds(window.Handle), tile), "Repeating an owned layout is accepted without extra movement for " + tile);
                    check(Undo(mover, window), "Shared Undo restores owned tiling for " + tile);
                    Pump();
                    var restored = WindowHistory.Capture(window.Handle);
                    check(AreaPolicy.Equal(original, Bounds(window.Handle)) && restored.Placement.ShowCommand == before.Placement.ShowCommand && !mover.CanUndo,
                        "One Undo restores exact original bounds/state and consumes only one layout record for " + tile);
                }
                window.SetState(FormWindowState.Maximized);
                var maxBefore = WindowHistory.Capture(window.Handle);
                check(mover.TileWindow(Selection(window), Monitor(window.Handle), Bounds(window.Handle), WindowTile.Left), "Owned maximized window can be tiled: " + mover.LastStatus);
                check(window.WindowState == FormWindowState.Normal && mover.CanUndo, "Tiling leaves maximized mode with a recovery entry");
                check(Undo(mover, window), "Maximized tiling uses shared Undo"); Pump();
                var maxAfter = WindowHistory.Capture(window.Handle);
                check(maxAfter.Placement.ShowCommand == 3 && AreaPolicy.Equal(maxBefore.Placement.NormalPosition, maxAfter.Placement.NormalPosition),
                    "Undo restores maximized state and its original floating restore rectangle");
                window.SetState(FormWindowState.Normal);
                window.MinimumSize = new Size(850, 700); Pump();
                var minOriginal = Bounds(window.Handle);
                check(mover.TileWindow(Selection(window), Monitor(window.Handle), minOriginal, WindowTile.TopRight), "Owned minimum-size window gets a fitting expanded tile: " + mover.LastStatus);
                var fitted = Bounds(window.Handle);
                check(fitted.Right - fitted.Left >= 850 && fitted.Bottom - fitted.Top >= 700, "Native tiling honors real app minimum tracking dimensions");
                check(Undo(mover, window) && AreaPolicy.Equal(Bounds(window.Handle), minOriginal), "Expanded minimum-size layout preserves exact shared Undo");
            }
        }
        static void OwnedFailures(Action<bool, string> check)
        {
            using (var window = new OwnedWindow { Text = "Owned tiling refusal regression", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(Screen.PrimaryScreen.WorkingArea.Left + 80, Screen.PrimaryScreen.WorkingArea.Top + 80, 520, 380) })
            using (var mover = Mover()) {
                window.Show(); Pump(); var selected = Selection(window); var source = Monitor(window.Handle); var original = Bounds(window.Handle);
                mover.TileMinimum = delegate { return new WindowTileMinimum { Error = 5 }; };
                check(!mover.TileWindow(selected, source, original, WindowTile.Left) && !mover.CanUndo && AreaPolicy.Equal(original, Bounds(window.Handle)) && mover.LastStatus.Contains("error 5"),
                    "Unavailable/elevated minimum query refuses without mutation or Undo and accurately reports native failure");
                mover.TileMinimum = delegate { return new WindowTileMinimum { Available = true, Minimum = new Size(Int32.MaxValue, Int32.MaxValue) }; };
                check(!mover.TileWindow(selected, source, original, WindowTile.Left) && !mover.CanUndo && AreaPolicy.Equal(original, Bounds(window.Handle)),
                    "An impossible minimum size refuses before mutation");
                mover.TileMinimum = null;
                var stale = new ShelfWindow { Handle = selected.Handle, Process = selected.Process, ProcessStarted = selected.ProcessStarted + 1, AppKey = selected.AppKey };
                check(!mover.TileWindow(stale, source, original, WindowTile.Left) && !mover.CanUndo, "Reused process identity cannot authorize tiling");
                var staleMonitor = source; staleMonitor.Work.Right--;
                check(!mover.TileWindow(selected, staleMonitor, original, WindowTile.Left) && !mover.CanUndo, "Changed topology/work area invalidates a captured tiling request");
                var staleBounds = original; staleBounds.Left++;
                check(!mover.TileWindow(selected, source, staleBounds, WindowTile.Left) && !mover.CanUndo, "A manually moved/resized target invalidates the layout picker capture");
                mover.ExcludedApps.Add(new AppRule { Key = selected.AppKey });
                check(!mover.TileWindow(selected, source, original, WindowTile.Left) && !mover.CanUndo, "App exclusions apply to the tiling path");
                mover.ExcludedApps.Clear();
                check(mover.TileWindow(selected, source, original, WindowTile.Left) && mover.CanUndo, "A successful earlier layout establishes recovery before later failure");
                var position = mover.MoveApi.Position;
                mover.MoveApi.Position = delegate { return false; };
                check(!mover.TileWindow(Selection(window), source, Bounds(window.Handle), WindowTile.Right) && mover.CanUndo,
                    "Failed subsequent tiling preserves the previous Undo record");
                mover.MoveApi.Position = position;
                check(Undo(mover, window) && AreaPolicy.Equal(original, Bounds(window.Handle)), "Undo after a rejected layout restores the last successful operation");
                window.SetState(FormWindowState.Maximized);
                mover.MoveApi.Position = delegate { return false; };
                check(!mover.TileWindow(Selection(window), Monitor(window.Handle), Bounds(window.Handle), WindowTile.Right) && mover.CanUndo,
                    "Failure after successful maximized restore retains recovery for the partial operation");
                mover.MoveApi.Position = position;
                check(Undo(mover, window), "Partial maximized layout recovery can be retried"); Pump();
                check(window.WindowState == FormWindowState.Maximized, "Partial layout Undo restores maximized state");
            }
        }
        static void FullscreenAndFixedSize(Action<bool, string> check)
        {
            using (var window = new OwnedWindow { Text = "Owned fullscreen tiling gate regression", ShowInTaskbar = true, Opacity = 0,
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Screen.PrimaryScreen.Bounds })
            using (var mover = Mover()) {
                window.Show(); Pump(); var source = Monitor(window.Handle); var before = Bounds(window.Handle); var selected = Selection(window);
                check(WindowShelfCatalog.IsFullscreenForMovement(window.Handle), "Owned borderless full-monitor fixture uses the shared fullscreen classifier");
                check(!mover.TileWindow(selected, source, before, WindowTile.Left) && !mover.CanUndo && AreaPolicy.Equal(before, Bounds(window.Handle)),
                    "Fullscreen OFF gates tiling before any native geometry mutation");
                mover.AllowMovingFullscreenApps = true;
                check(mover.TileWindow(selected, source, before, WindowTile.Left) && mover.CanUndo, "Fullscreen ON permits the authorized owned layout");
                check(Undo(mover, window) && AreaPolicy.Equal(before, Bounds(window.Handle)), "Fullscreen layout Undo restores exact full-monitor geometry");
                var position = mover.MoveApi.Position;
                mover.MoveApi.Position = delegate(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags) {
                    position(handle, after, x, y, width, height, flags); return false;
                };
                check(!mover.TileWindow(Selection(window), source, before, WindowTile.Right) && mover.CanUndo,
                    "A partial fullscreen operation retains recovery even if its final native result is failure");
                mover.MoveApi.Position = position;
                check(Undo(mover, window) && AreaPolicy.Equal(before, Bounds(window.Handle)), "Partial fullscreen failure can be recovered exactly");
            }
            using (var window = new OwnedWindow { Text = "Owned fixed-size centering regression", ShowInTaskbar = true, Opacity = 0,
                FormBorderStyle = FormBorderStyle.FixedSingle, StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(Screen.PrimaryScreen.WorkingArea.Left + 30, Screen.PrimaryScreen.WorkingArea.Top + 30, 440, 300) })
            using (var mover = Mover()) {
                window.Show(); Pump(); var before = Bounds(window.Handle); var source = Monitor(window.Handle);
                check(!mover.TileWindow(Selection(window), source, before, WindowTile.Left) && !mover.CanUndo && AreaPolicy.Equal(before, Bounds(window.Handle)),
                    "A fixed-size app rejects resizing layouts without creating Undo");
                check(mover.TileWindow(Selection(window), source, before, WindowTile.Center), "Fixed-size windows can be centered without resizing");
                var centered = Bounds(window.Handle);
                check(centered.Right - centered.Left == before.Right - before.Left && centered.Bottom - centered.Top == before.Bottom - before.Top,
                    "Fixed-size Center retains exact outer dimensions");
                check(Undo(mover, window) && AreaPolicy.Equal(before, Bounds(window.Handle)), "Centering a fixed-size app supports exact Undo");
                window.Size = new Size(source.Work.Right - source.Work.Left + 200, 300); Pump();
                var oversized = Bounds(window.Handle); var visible = WindowTilingNative.VisibleBounds(window.Handle, oversized);
                if (visible.Width > source.Work.Right - source.Work.Left)
                    check(!mover.TileWindow(Selection(window), source, oversized, WindowTile.Center) && !mover.CanUndo && AreaPolicy.Equal(oversized, Bounds(window.Handle)),
                        "Oversized fixed-size Center refuses instead of resizing the app");
                else Console.WriteLine("SKIP: Windows clamped the owned oversized fixed-size fixture before the Center test.");
            }
        }
    }
}
