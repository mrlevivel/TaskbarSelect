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
    // Real native geometry on alpha-zero owned HWNDs only. The custom-caption
    // fixture has the user's observed desktop styles and respects native work
    // areas when maximized. No Explorer, work-area, settings or focus mutation.
    internal static class CaptionlessMaximizedTests
    {
        const int NoActivate = 0x08000000;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [StructLayout(LayoutKind.Sequential)] struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo
        { internal Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
        {
            internal uint Size, Style; internal WindowProcedure Procedure;
            internal int ClassExtra, WindowExtra; internal IntPtr Instance, Icon, Cursor, Background;
            internal string Menu, Name; internal IntPtr SmallIcon;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        sealed class OwnedWindow : IDisposable
        {
            readonly WindowProcedure procedure = Dispatch;
            readonly string name = "TaskbarSelect.CaptionlessMaximized." + Guid.NewGuid().ToString("N");
            readonly IntPtr instance = GetModuleHandle(null);
            internal readonly IntPtr Handle;
            static IntPtr Dispatch(IntPtr window, uint message, IntPtr wparam, IntPtr lparam)
            {
                if (message == 0x84) return new IntPtr(-1); // HTTRANSPARENT.
                if (message == 0x21) return new IntPtr(3); // MA_NOACTIVATE.
                if (message == 0x24) {
                    var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                    if (Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref info)) {
                        var limits = (MinMaxInfo)Marshal.PtrToStructure(lparam, typeof(MinMaxInfo));
                        limits.MaxPosition = new Point { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
                        limits.MaxSize = new Point { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
                        limits.MinTrackSize = new Point { X = 100, Y = 80 };
                        Marshal.StructureToPtr(limits, lparam, false); return IntPtr.Zero;
                    }
                }
                return DefWindowProc(window, message, wparam, lparam);
            }
            internal OwnedWindow(Native.Rect bounds, bool popup = false, bool helper = false)
            {
                var registration = new WindowClass { Size = (uint)Marshal.SizeOf(typeof(WindowClass)), Procedure = procedure, Instance = instance, Name = name };
                if (RegisterClassEx(ref registration) == 0) throw new InvalidOperationException("Owned fixture class registration failed.");
                Handle = CreateWindowEx(0x00080000 | 0x00040000 | (helper ? 0x80 | NoActivate : 0), name, "Neutral custom-caption maximized fixture",
                    popup ? 0x80000000u : 0x04070000u, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                    IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (Handle == IntPtr.Zero) { UnregisterClass(name, instance); throw new InvalidOperationException("Owned fixture HWND creation failed."); }
                if (!SetLayeredWindowAttributes(Handle, 0, 0, 2)) { Dispose(); throw new InvalidOperationException("Owned fixture alpha-zero setup failed."); }
                WithoutActivation(delegate { return ShowWindow(Handle, 4); }); Pump();
                // Windows gives ordinary overlapped HWNDs a default caption.
                // Custom-caption apps remove it after creating the native root.
                if (!popup) {
                    SetWindowLong(Handle, -16, GetWindowLong(Handle, -16) & ~0x00C00000);
                    SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x237);
                }
            }
            internal T WithoutActivation<T>(Func<T> action)
            {
                int original = GetWindowLong(Handle, -20); SetWindowLong(Handle, -20, original | NoActivate);
                try { return action(); } finally { SetWindowLong(Handle, -20, original); }
            }
            internal void Maximize() { WithoutActivation(delegate { return ShowWindow(Handle, 3); }); Pump(); }
            public void Dispose() { DestroyWindow(Handle); UnregisterClass(name, instance); GC.KeepAlive(procedure); }
        }
        static void Pump() { Application.DoEvents(); }
        static Native.Rect Bounds(IntPtr window)
        { Native.Rect result; if (!Native.GetWindowRect(window, out result)) throw new InvalidOperationException("Owned fixture bounds unavailable."); return result; }
        static Native.MonitorInfo Info(Display display)
        { Native.MonitorInfo info; if (!Native.TryMonitor(display.Device, out info)) throw new InvalidOperationException("Fixture monitor unavailable."); return info; }
        static IntPtr MonitorHandle(Display display)
        {
            IntPtr result = IntPtr.Zero;
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(handle, ref info) && info.Device == display.Device) { result = handle; return false; }
                return true;
            }, IntPtr.Zero);
            return result;
        }
        static object Field(TaskbarMover mover, string name) { return typeof(TaskbarMover).GetField(name, Fields).GetValue(mover); }
        static void Field(TaskbarMover mover, string name, object value) { typeof(TaskbarMover).GetField(name, Fields).SetValue(mover, value); }
        static TaskbarTargets.Target Target(OwnedWindow app, OwnedWindow fakeBar)
        {
            uint pid; Native.GetWindowThreadProcessId(app.Handle, out pid);
            var rule = AppCatalog.ForWindow(app.Handle); var bounds = Bounds(fakeBar.Handle);
            return new TaskbarTargets.Target { Window = app.Handle, Process = pid, AppKey = rule.Key,
                Taskbar = fakeBar.Handle, TaskbarBounds = bounds, Captured = DateTime.UtcNow,
                Windows = new[] { new TaskbarTargets.WindowIdentity { Window = app.Handle, Process = pid } },
                Button = new TaskbarMover.Click { AppId = Process.GetCurrentProcess().ProcessName, AppName = "Neutral fixture", ButtonBounds = bounds,
                    Position = new TaskbarMover.Point { X = bounds.Left + 8, Y = bounds.Top + 8 } } };
        }
        static void WaitDirect(TaskbarMover mover)
        {
            var clock = Stopwatch.StartNew();
            while (Field(mover, "directRelease") != null) {
                if (clock.ElapsedMilliseconds > 4000) throw new InvalidOperationException("Owned direct fixture did not complete.");
                Pump(); mover.ProcessClick(); System.Threading.Thread.Sleep(5);
            }
        }
        internal static void Run(Action<bool, string> check)
        {
            var displays = Native.Displays();
            if (displays.Count == 0) { Console.WriteLine("SKIP: custom-caption fixtures require a monitor."); return; }
            IntPtr foreground = GetForegroundWindow(); int helpers = WindowMoveWorker.ActiveHelpers;
            var info = Info(displays[0]);
            using (var desktop = new OwnedWindow(info.Monitor)) {
                desktop.Maximize();
                check(SetWindowPos(desktop.Handle, IntPtr.Zero, info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, 0x214), "Owned desktop fixture reproduces full-monitor maximized geometry");
                int style = GetWindowLong(desktop.Handle, -16);
                check(IsZoomed(desktop.Handle) && (style & 0x00C00000) == 0 && (style & 0x00070000) == 0x00070000 && (style & unchecked((int)0x80000000)) == 0,
                    "Owned desktop fixture reproduces maximized, captionless resize/minimize/maximize style 0x15070000; actual=0x" + style.ToString("X") + "; zoomed=" + IsZoomed(desktop.Handle));
                check(!WindowShelfCatalog.IsFullscreen(desktop.Handle), "Custom-caption resizable maximized desktop is ordinary even at full monitor bounds");
                var rect = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
                check(WindowShelfPolicy.Fullscreen(rect, rect, false, true, false), "Existing classifier without desktop-style metadata retains captionless fullscreen semantics");
                check(WindowShelfPolicy.Fullscreen(rect, rect, false, true, false, unchecked((int)0x95070000)), "Popup fullscreen remains fullscreen even if it retains resize and maximize styles");
                check(WindowShelfPolicy.Fullscreen(rect, rect, false, true, false, 0x15030000), "Captionless maximized window without desktop resize style retains fullscreen semantics");
                check(WindowShelfPolicy.Fullscreen(rect, rect, false, true, false, 0x15060000), "Captionless maximized window without maximize control retains fullscreen semantics");
                check(WindowShelfPolicy.Fullscreen(rect, rect, false, false, false, 0x14070000), "Nonmaximized full-monitor window retains existing fullscreen semantics");
                check(!WindowShelfPolicy.Fullscreen(rect, rect, true, true, false, 0x15070000), "Minimized custom-caption window does not occupy a fullscreen destination");
            }
            using (var fullscreen = new OwnedWindow(info.Monitor, true)) {
                check(WindowShelfCatalog.IsFullscreen(fullscreen.Handle), "Genuine popup borderless full-monitor HWND remains fullscreen");
                using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new InvalidOperationException("Replay forbidden."); }, delegate { return fullscreen.Handle; }, false)) {
                    mover.MoveApi.Modifiers = delegate { return 0; };
                    int mutations = 0; mover.MoveApi.Position = delegate { mutations++; return false; };
                    check(!mover.MoveToMonitor(fullscreen.Handle, MonitorHandle(displays[0])) && mutations == 0 && !mover.CanUndo && mover.LastStatus.Contains("Enable Allow"),
                        "Genuine fullscreen OFF gate precedes same-monitor completion without mutation or Undo");
                }
            }
            var hidden = displays.FirstOrDefault(d => AreaPolicy.Equal(Info(d).Work, Info(d).Monitor));
            var visible = displays.FirstOrDefault(d => !AreaPolicy.Equal(Info(d).Work, Info(d).Monitor));
            if (hidden == null || visible == null) Console.WriteLine("SKIP: native hidden-work-area to reserved-work-area movement needs existing monitors of both kinds; no desktop changes made.");
            else foreach (bool relative in new[] { false, true }) foreach (string path in new[] { "shortcut", "native taskbar", "cold native minimize", "Ghost Bar" })
                Roundtrip(check, hidden, visible, relative, path);
            check(GetForegroundWindow() == foreground, "All custom-caption fixtures preserve real desktop foreground");
            check(WindowMoveWorker.ActiveHelpers == helpers && WindowMoveWorker.UncertainHelpers == 0, "All custom-caption fixtures leave no movement helpers running");
        }
        static void Roundtrip(Action<bool, string> check, Display hidden, Display visible, bool relative, string path)
        {
            string mode = path + "/" + (relative ? "relative" : "fixed");
            var hiddenInfo = Info(hidden); var visibleInfo = Info(visible); var hiddenHandle = MonitorHandle(hidden); var visibleHandle = MonitorHandle(visible);
            var initial = new Native.Rect { Left = hiddenInfo.Work.Left + 50, Top = hiddenInfo.Work.Top + 50, Right = hiddenInfo.Work.Left + 410, Bottom = hiddenInfo.Work.Top + 310 };
            IntPtr desktopForeground = GetForegroundWindow();
            using (var app = new OwnedWindow(initial))
            using (var fakeBar = new OwnedWindow(new Native.Rect { Left = visibleInfo.Work.Left + 10, Top = visibleInfo.Work.Top + 10, Right = visibleInfo.Work.Left + 170, Bottom = visibleInfo.Work.Top + 50 }, helper: true)) {
                TaskbarTargets.Target fresh = null; IntPtr reportedForeground = app.Handle;
                using (var resolver = new TaskbarTargets(delegate { return fresh; }))
                using (var mover = new TaskbarMover(resolver, delegate { throw new InvalidOperationException("Replay forbidden."); }, delegate { return reportedForeground; }, false)) {
                    check(new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" }.All(name => (IntPtr)Field(mover, name) == IntPtr.Zero), mode + ": no desktop hooks installed");
                    mover.Enabled = true; mover.AllowMovingFullscreenApps = false; mover.ScaleWindowSizeToMonitor = relative;
                    mover.MoveApi.Modifiers = delegate { return 0; };
                    mover.MoveApi.Focus = delegate(IntPtr window) { if (window != app.Handle) throw new InvalidOperationException("Foreign focus forbidden."); reportedForeground = window; return true; };
                    var boundedPlacement = mover.MoveApi.Placement;
                    mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                        if (window != app.Handle) throw new InvalidOperationException("Foreign placement forbidden.");
                        var copy = placement; return app.WithoutActivation(delegate { return boundedPlacement(window, ref copy); });
                    };
                    app.Maximize(); var original = WindowHistory.Capture(app.Handle);
                    check(original != null && IsZoomed(app.Handle) && AreaPolicy.Equal(Bounds(app.Handle), hiddenInfo.Monitor), mode + ": owned source maximizes to existing reclaimed full-monitor area");
                    check(!WindowShelfCatalog.IsFullscreen(app.Handle), mode + ": source is ordinary with fullscreen option OFF");
                    for (int iteration = 0; iteration < 2; iteration++) {
                        if (path == "shortcut") check(mover.MoveFocusedWindow(app.Handle, visible, 1), mode + ": hotkey moves the custom-caption maximized window");
                        else if (path == "native taskbar") {
                            fresh = Target(app, fakeBar);
                            check(mover.TryStartDirectMove(fresh, visibleHandle, fresh.Button.Position), mode + ": first native taskbar press intercepts ordinary maximized app");
                            mover.CompleteDirectPress(true); WaitDirect(mover);
                        } else if (path == "cold native minimize") {
                            fresh = Target(app, fakeBar); uint pid; Native.GetWindowThreadProcessId(app.Handle, out pid);
                            var probe = new TaskbarTargets.Probe { Complete = false };
                            var click = new TaskbarMover.Click { Taskbar = fakeBar.Handle, Position = fresh.Button.Position,
                                PriorWindow = app.Handle, PriorProcess = pid, PriorMonitor = hiddenHandle, TargetMonitor = visibleHandle,
                                AppId = fresh.Button.AppId, Time = DateTime.UtcNow, ReleasedAt = DateTime.UtcNow, Verification = probe };
                            mover.CancelPending(); Field(mover, "click", click);
                            app.WithoutActivation(delegate { return ShowWindow(app.Handle, 7); }); Pump(); mover.ProcessClick();
                            check(IsIconic(app.Handle) && Field(mover, "click") == click, mode + ": cold first native minimize waits for exact verification");
                            fresh.Captured = DateTime.UtcNow; probe.Result = fresh; probe.Complete = true;
                            check(TaskbarMover.NativeMinimizeMatches(click, fresh), mode + ": native minimize verifies the same cross-monitor HWND");
                            mover.ProcessClick();
                        } else {
                            ShelfWindow selected; check(WindowShelfCatalog.TryReadWindow(app.Handle, out selected), mode + ": source exposes eligible Ghost Bar identity with fullscreen OFF");
                            selected.SelectionForeground = app.Handle; selected.SelectionTimestamp = Stopwatch.GetTimestamp(); selected.SelectionOrigin = ShelfSelectionOrigin.DirectIcon;
                            check(mover.MoveFromShelf(selected, visible), mode + ": Ghost Bar movement accepts ordinary maximized app");
                        }
                        check(Native.MonitorFromWindow(app.Handle, 2) == visibleHandle && IsZoomed(app.Handle) && !IsIconic(app.Handle) && AreaPolicy.Equal(Bounds(app.Handle), visibleInfo.Work),
                            mode + ": destination respects reserved taskbar work area and stays maximized; status=" + mover.LastStatus);
                        check(mover.CanUndo && !WindowShelfCatalog.IsFullscreen(app.Handle), mode + ": ordinary movement retains Undo and avoids fullscreen observation");
                        check(mover.MoveFocusedWindow(app.Handle, hidden, 4) && IsZoomed(app.Handle) && AreaPolicy.Equal(Bounds(app.Handle), hiddenInfo.Work), mode + ": repeated shortcut return uses reclaimed area intact");
                        check(app.WithoutActivation(mover.Undo) && Native.MonitorFromWindow(app.Handle, 2) == visibleHandle && IsZoomed(app.Handle) && AreaPolicy.Equal(Bounds(app.Handle), visibleInfo.Work), mode + ": first Undo restores reserved destination");
                        check(app.WithoutActivation(mover.Undo) && Native.MonitorFromWindow(app.Handle, 2) == hiddenHandle && IsZoomed(app.Handle) && AreaPolicy.Equal(Bounds(app.Handle), hiddenInfo.Work) && !mover.CanUndo, mode + ": second Undo restores reclaimed source");
                        var after = WindowHistory.Capture(app.Handle);
                        check(after != null && after.Placement.ShowCommand == original.Placement.ShowCommand && AreaPolicy.Equal(after.Placement.NormalPosition, original.Placement.NormalPosition), mode + ": two Undo steps preserve original restore bounds");
                    }
                    check(GetForegroundWindow() == desktopForeground, mode + ": native moves and Undo preserve actual desktop foreground");
                }
            }
        }
    }
}
