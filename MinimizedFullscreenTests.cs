using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Cross-products use only alpha-zero HWNDs owned by this test process.
    // Foreground, focus and modifier dependencies are injected; no desktop hooks,
    // Explorer taskbars, profile writes, Startup changes or input are requested.
    internal static class MinimizedFullscreenTests
    {
        const int NoActivate = 0x08000000;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo
        { internal NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
        {
            internal uint Size, Style; internal WindowProcedure Procedure;
            internal int ClassExtra, WindowExtra; internal IntPtr Instance, Icon, Cursor, Background;
            internal string Menu, Name; internal IntPtr SmallIcon;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string name, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        sealed class OwnedWindow : IDisposable
        {
            readonly WindowProcedure procedure;
            readonly string name = "TaskbarSelect.MinimizedFullscreen." + Guid.NewGuid().ToString("N");
            readonly IntPtr instance = GetModuleHandle(null);
            internal readonly IntPtr Handle;
            readonly bool fullMaximize;
            IntPtr Dispatch(IntPtr window, uint message, IntPtr wparam, IntPtr lparam)
            {
                if (message == 0x84) return new IntPtr(-1);
                if (message == 0x21) return new IntPtr(3);
                if (message == 0x24 && fullMaximize) {
                    var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                    if (Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref info)) {
                        var limits = (MinMaxInfo)Marshal.PtrToStructure(lparam, typeof(MinMaxInfo));
                        limits.MaxPosition = new NativePoint { X = 0, Y = 0 };
                        limits.MaxSize = new NativePoint { X = info.Monitor.Right - info.Monitor.Left, Y = info.Monitor.Bottom - info.Monitor.Top };
                        Marshal.StructureToPtr(limits, lparam, false); return IntPtr.Zero;
                    }
                }
                return DefWindowProc(window, message, wparam, lparam);
            }
            internal OwnedWindow(Native.Rect bounds, bool popup = false, bool helper = false, bool maximizeToFull = false)
            {
                fullMaximize = maximizeToFull; procedure = Dispatch;
                var registration = new WindowClass { Size = (uint)Marshal.SizeOf(typeof(WindowClass)), Procedure = procedure, Instance = instance, Name = name };
                if (RegisterClassEx(ref registration) == 0) throw new InvalidOperationException("Owned minimized-fullscreen class registration failed.");
                Handle = CreateWindowEx(0x00080000 | 0x00040000 | (helper ? 0x80 | NoActivate : 0), name, "Neutral minimized-fullscreen fixture",
                    popup ? 0x80030000u : 0x00CF0000u, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                    IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (Handle == IntPtr.Zero) { UnregisterClass(name, instance); throw new InvalidOperationException("Owned minimized-fullscreen creation failed."); }
                if (!SetLayeredWindowAttributes(Handle, 0, 0, 2)) { Dispose(); throw new InvalidOperationException("Owned fixture could not be made invisible."); }
                State(4);
            }
            internal T WithoutActivation<T>(Func<T> action)
            {
                int style = GetWindowLong(Handle, -20); SetWindowLong(Handle, -20, style | NoActivate);
                try { return action(); } finally { SetWindowLong(Handle, -20, style); }
            }
            internal void State(int command) { WithoutActivation(delegate { return ShowWindow(Handle, command); }); Application.DoEvents(); }
            public void Dispose() { DestroyWindow(Handle); UnregisterClass(name, instance); GC.KeepAlive(procedure); }
        }
        static Native.MonitorInfo Info(Display display)
        { Native.MonitorInfo result; if (!Native.TryMonitor(display.Device, out result)) throw new InvalidOperationException("Owned fixture monitor unavailable."); return result; }
        static IntPtr MonitorHandle(Display display)
        {
            IntPtr result = IntPtr.Zero;
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(handle, ref info) && info.Device == display.Device) { result = handle; return false; } return true;
            }, IntPtr.Zero); return result;
        }
        static Native.Rect Bounds(IntPtr window)
        { Native.Rect result; if (!Native.GetWindowRect(window, out result)) throw new InvalidOperationException("Owned fixture geometry unavailable."); return result; }
        static void Field(TaskbarMover mover, string name, object value)
        { typeof(TaskbarMover).GetField(name, Fields).SetValue(mover, value); }
        internal static void Run(Action<bool, string> check)
        {
            IntPtr foreground = GetForegroundWindow(); Point pointer = Cursor.Position;
            int helpers = WindowMoveWorker.ActiveHelpers;
            var displays = Native.Displays();
            if (displays.Count == 0) { Console.WriteLine("SKIP: minimized-fullscreen fixtures require a monitor."); return; }
            SameMonitorOffGate(check, displays[0]);
            RestoreGeometry(check);
            MinimizedMaximizedCases(check, displays[0]);
            if (displays.Count < 2) Console.WriteLine("SKIP: minimized-fullscreen cross-monitor fixtures require two monitors; single-monitor OFF gate passed.");
            else foreach (bool relative in new[] { false, true }) foreach (bool allow in new[] { false, true })
                foreach (bool ghost in new[] { false, true }) CrossMonitor(check, displays[0], displays[1], relative, allow, ghost);
            if (displays.Count >= 2) foreach (string failure in new[] { "restore refused", "restore cancelled", "restore timed out", "destination refused" })
                FailedRestore(check, displays[0], displays[1], failure);
            bool foregroundUnchanged = GetForegroundWindow() == foreground;
            bool pointerUnchanged = Cursor.Position == pointer;
            Console.WriteLine("Minimized-fullscreen desktop check: foreground unchanged=" + foregroundUnchanged +
                ", pointer unchanged=" + pointerUnchanged + ".");
            check(foregroundUnchanged, "Minimized-fullscreen fixtures preserve the actual foreground window");
            check(pointerUnchanged, "Minimized-fullscreen fixtures preserve the actual pointer position");
            check(WindowMoveWorker.ActiveHelpers == helpers && WindowMoveWorker.UncertainHelpers == 0, "Minimized-fullscreen fixtures leave no movement helpers");
        }
        static void RestoreGeometry(Action<bool, string> check)
        {
            var monitor = new Native.MonitorInfo { Monitor = new Native.Rect { Left = -1920, Top = -1080, Right = 0, Bottom = 0 },
                Work = new Native.Rect { Left = -1880, Top = -1032, Right = 0, Bottom = 0 } };
            var workspace = new Native.Rect { Left = -1960, Top = -1128, Right = -40, Bottom = -48 }; Native.Rect bounds;
            check(FullscreenMovePolicy.TryRestoreBounds(workspace, monitor, false, out bounds) && AreaPolicy.Equal(bounds, monitor.Monitor), "Minimized restore classification converts top/left workspace offsets to physical screen pixels");
            check(FullscreenMovePolicy.TryRestoreBounds(monitor.Monitor, monitor, true, out bounds) && AreaPolicy.Equal(bounds, monitor.Monitor), "Tool-window placement remains in screen coordinates");
            check(!FullscreenMovePolicy.TryRestoreBounds(new Native.Rect(), monitor, false, out bounds), "Invalid empty restore geometry is rejected");
            check(!FullscreenMovePolicy.TryRestoreBounds(new Native.Rect { Left = Int32.MaxValue - 20, Right = Int32.MaxValue, Bottom = 100 }, monitor, false, out bounds), "Overflowed restore geometry is rejected rather than wrapping into another monitor");
        }
        internal static void MinimizedMaximizedCases(Action<bool, string> check, Display source)
        {
            var info = Info(source); var small = new Native.Rect { Left = info.Work.Left + 40, Top = info.Work.Top + 40, Right = info.Work.Left + 340, Bottom = info.Work.Top + 240 };
            foreach (bool maximized in new[] { false, true }) foreach (bool captionless in new[] { false, true })
                using (var app = new OwnedWindow(small)) {
                    if (captionless) SetWindowLong(app.Handle, -16, GetWindowLong(app.Handle, -16) & ~0x00C00000);
                    if (maximized) app.State(3);
                    app.State(7); bool fullscreen; Native.Rect bounds;
                    check(IsIconic(app.Handle) && WindowShelfCatalog.TryReadFullscreenMovement(app.Handle, out fullscreen, out bounds) && !fullscreen,
                        "Ordinary minimized " + (captionless ? "custom-caption " : "captioned ") + (maximized ? "maximized" : "normal") + " window stays outside fullscreen policy");
                    ShelfWindow selected; check(WindowShelfCatalog.TryReadWindow(app.Handle, out selected), "Ordinary minimized desktop identity remains available with fullscreen OFF");
                }
            using (var popup = new OwnedWindow(small, true, maximizeToFull: true))
            using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new InvalidOperationException("Replay forbidden."); }, delegate { return popup.Handle; }, false)) {
                popup.State(3);
                check(IsZoomed(popup.Handle) && FullscreenMovePolicy.Matches(Bounds(popup.Handle), info.Monitor) && WindowShelfCatalog.IsFullscreen(popup.Handle), "Owned maximized-popup counterexample really occupies full monitor bounds");
                popup.State(7); var placement = WindowHistory.Capture(popup.Handle);
                check(placement != null && (placement.Placement.Flags & 2) != 0 && placement.Placement.NormalPosition.Right - placement.Placement.NormalPosition.Left < info.Monitor.Right - info.Monitor.Left,
                    "Minimized maximized-fullscreen popup retains WPF_RESTORETOMAXIMIZED and smaller floating NormalPosition");
                bool fullscreen; Native.Rect bounds;
                check(!WindowShelfCatalog.IsFullscreen(popup.Handle) && !WindowShelfCatalog.TryReadFullscreenMovement(popup.Handle, out fullscreen, out bounds) && WindowShelfCatalog.IsFullscreenForMovement(popup.Handle),
                    "Ambiguous maximized-popup restore geometry fails closed for movement while retaining nonoccupancy");
                int calls = 0; mover.MoveApi.Modifiers = delegate { return 0; }; mover.MoveApi.Position = delegate { calls++; return true; }; mover.MoveApi.Placement = delegate { calls++; return true; };
                foreach (bool allow in new[] { false, true }) {
                    mover.AllowMovingFullscreenApps = allow;
                    check(!mover.MoveToMonitor(popup.Handle, MonitorHandle(source)) && calls == 0 && IsIconic(popup.Handle) && !mover.CanUndo,
                        "Unproven maximized-popup source geometry cannot bypass OFF or invent opt-in recovery bounds: " + allow);
                    check(mover.LastStatus.StartsWith("Restore this window first"), "Ambiguous restore geometry explains the safe next action rather than claiming a move");
                }
            }
        }
        static void SameMonitorOffGate(Action<bool, string> check, Display source)
        {
            var info = Info(source);
            using (var app = new OwnedWindow(info.Monitor, true))
            using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new InvalidOperationException("Input replay forbidden."); }, delegate { return app.Handle; }, false)) {
                mover.MoveApi.Modifiers = delegate { return 0; }; mover.MoveApi.Focus = delegate { throw new InvalidOperationException("Focus forbidden for an OFF-gated move."); };
                check(WindowShelfCatalog.IsFullscreen(app.Handle), "Popup fixture begins in genuine fullscreen");
                ShelfWindow selected; check(WindowShelfCatalog.TryReadWindow(app.Handle, true, out selected), "Owned fullscreen identity is readable with opt-in");
                app.State(7);
                check(IsIconic(app.Handle) && !WindowShelfCatalog.IsFullscreen(app.Handle), "Minimized fullscreen does not occupy or suppress its monitor");
                int mutations = 0;
                mover.MoveApi.Position = delegate { mutations++; return true; };
                mover.MoveApi.Placement = delegate { mutations++; return true; };
                check(!mover.MoveToMonitor(app.Handle, MonitorHandle(source)) && mutations == 0 && !mover.CanUndo && IsIconic(app.Handle),
                    "Fullscreen OFF gate survives minimization and precedes same-monitor success without mutation or Undo");
                check(!WindowShelfCatalog.TryReadWindow(app.Handle, out selected), "Minimized fullscreen is not listed as an ordinary OFF-profile app");
            }
        }
        static TaskbarTargets.Target Target(OwnedWindow app, OwnedWindow fakeBar)
        {
            uint pid; Native.GetWindowThreadProcessId(app.Handle, out pid);
            var appInfo = AppCatalog.ForWindow(app.Handle); var bounds = Bounds(fakeBar.Handle);
            return new TaskbarTargets.Target { Window = app.Handle, Process = pid, AppKey = appInfo.Key,
                Taskbar = fakeBar.Handle, TaskbarBounds = bounds, Captured = DateTime.UtcNow,
                Windows = new[] { new TaskbarTargets.WindowIdentity { Window = app.Handle, Process = pid } },
                Button = new TaskbarMover.Click { AppId = Process.GetCurrentProcess().ProcessName, AppName = "Neutral fixture", ButtonBounds = bounds,
                    Position = new TaskbarMover.Point { X = bounds.Left + 8, Y = bounds.Top + 8 } } };
        }
        static void CrossMonitor(Action<bool, string> check, Display source, Display destination, bool relative, bool allow, bool ghost)
        {
            string mode = (ghost ? "Ghost Bar after opt-in minimize" : "cold native taskbar minimize") + "/" + (allow ? "ON" : "OFF") + "/" + (relative ? "relative" : "fixed");
            var sourceInfo = Info(source); var targetInfo = Info(destination);
            var priorBounds = new Native.Rect { Left = sourceInfo.Work.Left + 30, Top = sourceInfo.Work.Top + 30, Right = sourceInfo.Work.Left + 330, Bottom = sourceInfo.Work.Top + 230 };
            using (var prior = new OwnedWindow(priorBounds))
            using (var app = new OwnedWindow(sourceInfo.Monitor, true))
            using (var fakeBar = new OwnedWindow(priorBounds, helper: true)) {
                IntPtr reportedForeground = prior.Handle;
                using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new InvalidOperationException("Input replay forbidden."); }, delegate { return reportedForeground; }, false)) {
                    mover.MoveApi.Modifiers = delegate { return 0; };
                    mover.MoveApi.Focus = delegate(IntPtr window) { check(window == prior.Handle || window == app.Handle, mode + ": focus dependency accepts only owned HWNDs"); reportedForeground = window; return true; };
                    int placementCalls = 0, positionCalls = 0;
                    var positionApi = mover.MoveApi.Position;
                    mover.MoveApi.Position = delegate(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags) {
                        positionCalls++; return positionApi(window, after, x, y, width, height, flags);
                    };
                    var placementApi = mover.MoveApi.Placement;
                    mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                        placementCalls++;
                        if (window != app.Handle && window != prior.Handle) throw new InvalidOperationException("Only owned placement is permitted.");
                        var copy = placement; var owned = window == app.Handle ? app : prior;
                        return owned.WithoutActivation(delegate { return placementApi(window, ref copy); });
                    };
                    check(new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" }.All(name => (IntPtr)typeof(TaskbarMover).GetField(name, Fields).GetValue(mover) == IntPtr.Zero), mode + ": no desktop hooks");
                    mover.ScaleWindowSizeToMonitor = relative;
                    check(mover.MoveToMonitor(prior.Handle, MonitorHandle(destination)) && mover.CanUndo, mode + ": an earlier ordinary move establishes Undo");
                    reportedForeground = app.Handle;
                    ShelfWindow selected; check(WindowShelfCatalog.TryReadWindow(app.Handle, true, out selected), mode + ": captures fullscreen identity before Explorer minimizes");
                    if (ghost) { mover.AllowMovingFullscreenApps = true; mover.ShelfMinimizes.RecordSuccessfulMinimize(selected); }
                    app.State(7); mover.AllowMovingFullscreenApps = allow;
                    var minimized = WindowHistory.Capture(app.Handle);
                    placementCalls = positionCalls = 0;
                    check(minimized != null && IsIconic(app.Handle) && !WindowShelfCatalog.IsFullscreen(app.Handle), mode + ": real native minimized placement retains occupancy semantics");
                    if (ghost) {
                        bool moved = mover.MoveFromShelf(selected, destination);
                        check(moved == allow, mode + ": remote Ghost Bar selection honors fullscreen opt-in after minimization");
                    } else {
                        var target = Target(app, fakeBar);
                        var probe = new TaskbarTargets.Probe { Complete = true, Result = target };
                        var click = new TaskbarMover.Click { Taskbar = fakeBar.Handle, Position = target.Button.Position, PriorWindow = app.Handle,
                            PriorProcess = target.Process, PriorMonitor = MonitorHandle(source), TargetMonitor = MonitorHandle(destination),
                            AppId = target.Button.AppId, Time = DateTime.UtcNow, ReleasedAt = DateTime.UtcNow, Verification = probe };
                        mover.Enabled = true; Field(mover, "click", click);
                        check(TaskbarMover.NativeMinimizeMatches(click, target), mode + ": completed cold lookup proves exact fullscreen foreground minimize");
                        mover.ProcessClick();
                    }
                    check(mover.CanUndo, mode + ": earlier recovery remains available");
                    if (allow) {
                        check(!IsIconic(app.Handle) && FullscreenMovePolicy.Matches(Bounds(app.Handle), targetInfo.Monitor), mode + ": opted-in restore requests full destination monitor, independent of sizing mode");
                        check(mover.Undo() && !IsIconic(app.Handle) && FullscreenMovePolicy.Matches(Bounds(app.Handle), sourceInfo.Monitor), mode + ": fullscreen Undo restores original full bounds");
                        check(mover.CanUndo, mode + ": fullscreen Undo retains earlier ordinary move");
                    } else {
                        check(placementCalls == 0 && positionCalls == 0, mode + ": OFF refusal performs no native placement or position call");
                        var after = WindowHistory.Capture(app.Handle);
                        check(IsIconic(app.Handle) && after != null && AreaPolicy.Equal(after.Placement.NormalPosition, minimized.Placement.NormalPosition), mode + ": OFF refusal does not restore or rewrite native placement");
                        if (ghost) check(mover.ShelfMinimizes.Snapshot().Length == 1, mode + ": OFF refusal retains session recovery authority");
                    }
                    check(mover.Undo() && Native.MonitorFromWindow(prior.Handle, 2) == MonitorHandle(source) && !mover.CanUndo, mode + ": ordinary earlier Undo remains exact and is consumed once");
                }
            }
        }
        static void FailedRestore(Action<bool, string> check, Display source, Display destination, string failure)
        {
            var sourceInfo = Info(source);
            var ordinary = new Native.Rect { Left = sourceInfo.Work.Left + 40, Top = sourceInfo.Work.Top + 40, Right = sourceInfo.Work.Left + 340, Bottom = sourceInfo.Work.Top + 240 };
            using (var prior = new OwnedWindow(ordinary))
            using (var app = new OwnedWindow(sourceInfo.Monitor, true)) {
                IntPtr reportedForeground = prior.Handle;
                using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new InvalidOperationException("Replay forbidden."); }, delegate { return reportedForeground; }, false)) {
                    mover.MoveApi.Modifiers = delegate { return 0; };
                    mover.MoveApi.Focus = delegate(IntPtr window) { reportedForeground = window; return true; };
                    var placementApi = mover.MoveApi.Placement;
                    mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                        var copy = placement; var owned = window == app.Handle ? app : prior;
                        return owned.WithoutActivation(delegate { return placementApi(window, ref copy); });
                    };
                    check(mover.MoveToMonitor(prior.Handle, MonitorHandle(destination)) && mover.CanUndo, failure + ": earlier Undo established");
                    reportedForeground = app.Handle; mover.AllowMovingFullscreenApps = true;
                    ShelfWindow selected; check(WindowShelfCatalog.TryReadWindow(app.Handle, true, out selected), failure + ": fullscreen selection captured");
                    app.State(7);
                    int positions = 0, placements = 0;
                    var restoreApi = mover.MoveApi.Placement;
                    mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                        placements++;
                        if (failure == "restore refused") return false;
                        if (failure == "restore timed out") { System.Threading.Thread.Sleep(1100); return true; }
                        bool restored = restoreApi(window, ref placement);
                        if (failure == "restore cancelled") mover.CancelPending();
                        return restored;
                    };
                    mover.MoveApi.Position = delegate { positions++; return false; };
                    check(!mover.MoveFromShelf(selected, destination) && placements == 1 && positions == (failure == "destination refused" ? 1 : 0) && mover.CanUndo,
                        failure + ": refused/interrupted restore never reports success or queues an unauthorized destination step");
                    if (failure == "restore refused") check(IsIconic(app.Handle), failure + ": failed restore preserves minimized state and earlier Undo only");
                    if (failure == "restore cancelled") check(mover.LastStatus.Contains("cancelled"), failure + ": cancellation remains explicit");
                    if (failure == "restore timed out") check(mover.LastStatus.Contains("timed out") && IsIconic(app.Handle), failure + ": timeout is explicit and uncertain restore retains its snapshot");
                    if (failure == "restore timed out") {
                        var history = (WindowHistory)typeof(TaskbarMover).GetField("history", Fields).GetValue(mover);
                        int undoPositions = 0, undoPlacements = 0;
                        TaskbarMover.MovePosition blockedPosition = delegate { undoPositions++; return false; };
                        TaskbarMover.MovePlacement refusedPlacement = delegate(IntPtr window, ref WindowHistory.Placement placement) { undoPlacements++; return false; };
                        check(!history.Undo(blockedPosition, refusedPlacement, delegate { }) && history.Available && undoPlacements == 1 && undoPositions == 0 && IsIconic(app.Handle),
                            "Refused iconic fullscreen Undo restore retains its entry and never attempts full positioning");
                        TaskbarMover.MovePlacement unobservedRestore = delegate(IntPtr window, ref WindowHistory.Placement placement) { undoPlacements++; return true; };
                        check(!history.Undo(blockedPosition, unobservedRestore, delegate { }) && history.Available && undoPlacements == 2 && undoPositions == 0 && IsIconic(app.Handle),
                            "Accepted but unobserved iconic fullscreen Undo restore retains its entry and never reports success or attempts full positioning");
                    }
                    if (failure != "restore refused") {
                        check(app.WithoutActivation(mover.Undo) && !IsIconic(app.Handle) && FullscreenMovePolicy.Matches(Bounds(app.Handle), sourceInfo.Monitor) && mover.CanUndo,
                            failure + ": fullscreen Undo restores even an iconic uncertain target and keeps prior history");
                    }
                    check(prior.WithoutActivation(mover.Undo) && !mover.CanUndo && Native.MonitorFromWindow(prior.Handle, 2) == MonitorHandle(source), failure + ": earlier ordinary Undo remains exactly once");
                }
            }
        }
    }
}
