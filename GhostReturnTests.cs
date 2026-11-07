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
    // Neutral owned HWNDs only. No Explorer UIA, desktop input hooks, real
    // foreground requests, settings, Startup or taskbar-management operations.
    internal static class GhostReturnTests
    {
        const int ExStyle = -20, NoActivate = 0x08000000;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WindowClass
        {
            internal uint Size, Style;
            internal WindowProcedure Procedure;
            internal int ClassExtra, WindowExtra;
            internal IntPtr Instance, Icon, Cursor, Background;
            internal string Menu, Name;
            internal IntPtr SmallIcon;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(int extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr window, bool enabled);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);

        sealed class OwnedWindow : IDisposable
        {
            readonly WindowProcedure procedure = Dispatch;
            readonly string name;
            readonly IntPtr instance;
            internal readonly IntPtr Handle;
            static IntPtr Dispatch(IntPtr window, uint message, IntPtr wparam, IntPtr lparam)
            {
                if (message == 0x0084) return new IntPtr(-1); // HTTRANSPARENT.
                if (message == 0x0021) return new IntPtr(3); // MA_NOACTIVATE.
                return DefWindowProc(window, message, wparam, lparam);
            }
            internal OwnedWindow(Rectangle bounds, string shellClass = null, int extraStyle = 0, IntPtr owner = default(IntPtr), bool popup = false)
            {
                name = shellClass ?? "TaskbarSelect.GhostReturn." + Guid.NewGuid().ToString("N");
                instance = GetModuleHandle(null);
                var registration = new WindowClass { Size = (uint)Marshal.SizeOf(typeof(WindowClass)), Procedure = procedure, Instance = instance, Name = name };
                if (RegisterClassEx(ref registration) == 0) throw new InvalidOperationException("Could not register the neutral fixture class: " + Marshal.GetLastWin32Error());
                Handle = CreateWindowEx(0x00080000 | 0x00040000 | extraStyle, name, "Neutral owned Ghost return fixture", popup ? 0x80000000u : 0x00CF0000u,
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height, owner, IntPtr.Zero, instance, IntPtr.Zero);
                if (Handle == IntPtr.Zero) { UnregisterClass(name, instance); throw new InvalidOperationException("Could not create the neutral fixture window."); }
                if (!SetLayeredWindowAttributes(Handle, 0, 0, 2)) { Dispose(); throw new InvalidOperationException("Could not make the owned fixture invisible."); }
                ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE; still visible/task-switchable.
            }
            internal T WithoutActivation<T>(Func<T> action)
            {
                int original = GetWindowLong(Handle, ExStyle);
                SetWindowLong(Handle, ExStyle, original | NoActivate);
                try { return action(); }
                finally { SetWindowLong(Handle, ExStyle, original); }
            }
            internal void State(int command) { WithoutActivation(delegate { return ShowWindow(Handle, command); }); Pump(); }
            public void Dispose()
            {
                if (Handle != IntPtr.Zero) DestroyWindow(Handle);
                UnregisterClass(name, instance); GC.KeepAlive(procedure);
            }
        }

        static void Pump() { Application.DoEvents(); }
        static void Wait(Func<bool> ready)
        {
            var until = Stopwatch.StartNew();
            while (!ready()) {
                if (until.ElapsedMilliseconds > 4000) throw new InvalidOperationException("The owned Ghost return fixture did not settle.");
                Pump(); System.Threading.Thread.Sleep(5);
            }
        }
        static object Field(TaskbarMover mover, string name) { return typeof(TaskbarMover).GetField(name, Fields).GetValue(mover); }
        static void Field(TaskbarMover mover, string name, object value) { typeof(TaskbarMover).GetField(name, Fields).SetValue(mover, value); }
        static Native.Rect Bounds(IntPtr window)
        { Native.Rect result; if (!Native.GetWindowRect(window, out result)) throw new InvalidOperationException("Owned HWND bounds are unavailable."); return result; }
        static Native.MonitorInfo Monitor(Display display)
        { Native.MonitorInfo result; if (!Native.TryMonitor(display.Device, out result)) throw new InvalidOperationException("Fixture monitor is unavailable."); return result; }
        static IntPtr MonitorHandle(IntPtr window) { return Native.MonitorFromWindow(window, 2); }
        static uint OwnProcess { get { return (uint)Process.GetCurrentProcess().Id; } }
        static TaskbarMover.Click Button()
        { return new TaskbarMover.Click { AppId = Process.GetCurrentProcess().ProcessName, AppName = "Neutral fixture", Grouped = false }; }
        static TaskbarTargets.WindowIdentity[] ResolveOwn(TaskbarMover.Click button)
        { return TaskbarTargets.ResolveWindows(button).Where(identity => identity.Process == OwnProcess).ToArray(); }
        // This is the exact pre-fix native admission predicate, applied only to
        // known fixture handles. It reads no titles, file paths or Explorer UIA.
        static bool LegacyAdmits(TaskbarMover.Click button, IntPtr window)
        {
            int cloaked;
            return Native.IsWindowVisible(window) && GetWindow(window, 4) == IntPtr.Zero &&
                !(DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) && TaskbarMover.MatchesApp(button, window);
        }

        internal static void Run(Action<bool, string> check)
        {
            int before = WindowMoveWorker.ActiveHelpers;
            Resolver(check);
            var displays = Native.Displays();
            if (displays.Count < 2) Console.WriteLine("SKIP: Ghost return movement requires two connected monitors.");
            else {
                var source = displays.FirstOrDefault(d => d.Primary) ?? displays[0];
                var destination = displays.FirstOrDefault(d => d.Device == @"\\.\DISPLAY4" && !d.Primary) ?? displays.First(d => d.Device != source.Device);
                foreach (bool relative in new[] { false, true }) foreach (bool maximized in new[] { false, true })
                    foreach (bool cold in new[] { false, true }) Return(check, source, destination, relative, maximized, cold);
            }
            check(WindowMoveWorker.ActiveHelpers == before && WindowMoveWorker.UncertainHelpers == 0, "Ghost return fixtures leave no owned movement helpers running");
        }

        static void Resolver(Action<bool, string> check)
        {
            var display = Native.Displays()[0]; var monitor = Monitor(display);
            var area = new Rectangle(monitor.Work.Left + 20, monitor.Work.Top + 20, 280, 180);
            IntPtr foreground = GetForegroundWindow();
            using (var app = new OwnedWindow(area)) {
                var helpers = new List<OwnedWindow>();
                try {
                    foreach (string name in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame" }) helpers.Add(new OwnedWindow(area, name));
                    helpers.Add(new OwnedWindow(area, extraStyle: 0x80));
                    helpers.Add(new OwnedWindow(area, extraStyle: NoActivate));
                    var disabled = new OwnedWindow(area); helpers.Add(disabled); EnableWindow(disabled.Handle, false);
                    var owned = new OwnedWindow(area, owner: app.Handle); helpers.Add(owned);
                    var button = Button();
                    check(WindowShelfCatalog.IsTaskbarCandidate(app.Handle), "Owned visible captioned alpha-zero app is a native taskbar candidate");
                    check(helpers.All(helper => !WindowShelfCatalog.IsTaskbarCandidate(helper.Handle)), "Shell classes, tool/noactivate, disabled and owned helper HWNDs are not taskbar candidates");
                    check(helpers.Count(helper => LegacyAdmits(button, helper.Handle)) >= 7, "Exact old resolver admits same-process shell/helper duplicates despite one real app window");
                    var resolved = ResolveOwn(button);
                    check(resolved.Length == 1 && resolved[0].Window == app.Handle,
                        "Production native resolver selects the single exact app instead of same-process shell/helper HWNDs; own count=" + resolved.Length);
                    app.State(7); // SW_SHOWMINNOACTIVE.
                    check(IsIconic(app.Handle) && WindowShelfCatalog.IsTaskbarCandidate(app.Handle), "Visible minimized app remains eligible for native restore-and-move");
                    resolved = ResolveOwn(button);
                    check(resolved.Length == 1 && resolved[0].Window == app.Handle, "Minimized app identity survives production candidate filtering");
                    app.State(4);
                    using (var peer = new OwnedWindow(area)) {
                        resolved = ResolveOwn(button);
                        check(resolved.Length == 2 && resolved.Any(i => i.Window == app.Handle) && resolved.Any(i => i.Window == peer.Handle),
                            "Two real app windows stay ambiguous rather than collapsing into a single target");
                    }
                    app.State(0);
                    using (var fullscreen = new OwnedWindow(Rectangle.FromLTRB(monitor.Monitor.Left, monitor.Monitor.Top, monitor.Monitor.Right, monitor.Monitor.Bottom), popup: true)) {
                        check(WindowShelfCatalog.IsFullscreen(fullscreen.Handle) && WindowShelfCatalog.IsTaskbarCandidate(fullscreen.Handle), "Borderless fullscreen stays a resolver candidate independently of movement opt-in");
                        resolved = ResolveOwn(button);
                        check(resolved.Length == 1 && resolved[0].Window == fullscreen.Handle, "Production resolver retains the exact fullscreen identity");
                    }
                    check(GetForegroundWindow() == foreground, "Resolver fixtures preserve real desktop foreground");
                } finally { foreach (var helper in helpers) helper.Dispose(); }
            }
        }

        static TaskbarTargets.Target Target(OwnedWindow app, OwnedWindow taskbar)
        {
            var identities = ResolveOwn(Button());
            var button = Button(); button.ButtonBounds = Bounds(taskbar.Handle);
            button.Position = new TaskbarMover.Point { X = button.ButtonBounds.Left + 8, Y = button.ButtonBounds.Top + 8 };
            var result = new TaskbarTargets.Target { Button = button, Taskbar = taskbar.Handle, TaskbarBounds = button.ButtonBounds, Windows = identities, Captured = DateTime.UtcNow };
            if (identities.Length == 1) {
                var identity = AppCatalog.ForWindow(identities[0].Window);
                if (identity != null) { result.Window = identities[0].Window; result.Process = identities[0].Process; result.AppKey = identity.Key; }
            }
            return result;
        }

        static void Return(Action<bool, string> check, Display source, Display destination, bool relative, bool maximized, bool cold)
        {
            string mode = (relative ? "relative" : "fixed") + "/" + (maximized ? "maximized" : "normal") + "/" + (cold ? "cold native minimize" : "warm direct");
            var sourceInfo = Monitor(source); var sourceBounds = new Rectangle(sourceInfo.Work.Left + 50, sourceInfo.Work.Top + 50, 380, 260);
            IntPtr desktopForeground = GetForegroundWindow();
            using (var app = new OwnedWindow(sourceBounds))
            using (var fakeTaskbar = new OwnedWindow(new Rectangle(sourceInfo.Work.Left + 10, sourceInfo.Work.Top + 10, 160, 40), extraStyle: 0x80 | NoActivate)) {
                IntPtr reportedForeground = app.Handle; TaskbarTargets.Target fresh = null; int replays = 0;
                using (var resolver = new TaskbarTargets(delegate { return fresh; }))
                using (var mover = new TaskbarMover(resolver, delegate { replays++; return false; }, delegate { return reportedForeground; }, false)) {
                    check(new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" }.All(name => (IntPtr)Field(mover, name) == IntPtr.Zero), mode + ": no desktop callbacks registered");
                    mover.MoveApi.Focus = delegate(IntPtr window) { reportedForeground = window; return true; };
                    mover.MoveApi.Modifiers = delegate { return 0; };
                    var boundedPlacement = mover.MoveApi.Placement;
                    mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                        if (window != app.Handle) throw new InvalidOperationException("The placement adapter only accepts its owned HWND.");
                        var copy = placement;
                        return app.WithoutActivation(delegate { return boundedPlacement(window, ref copy); });
                    };
                    mover.ScaleWindowSizeToMonitor = relative;
                    if (maximized) app.State(3);
                    var original = WindowHistory.Capture(app.Handle); var originalBounds = Bounds(app.Handle); var sourceMonitor = MonitorHandle(app.Handle);
                    check(original != null && !IsIconic(app.Handle) && IsZoomed(app.Handle) == maximized, mode + ": fixture begins in its requested native state");
                    ShelfWindow selected;
                    check(WindowShelfCatalog.TryReadWindow(app.Handle, out selected), mode + ": app exposes fresh shelf identity");
                    selected.SelectionForeground = app.Handle; selected.SelectionTimestamp = Stopwatch.GetTimestamp(); selected.SelectionOrigin = ShelfSelectionOrigin.DirectIcon;
                    try { check(mover.MoveFromShelf(selected, destination), mode + ": one Ghost Bar selection moves from source to destination"); }
                    finally { selected.SelectionForeground = IntPtr.Zero; selected.SelectionTimestamp = 0; }
                    var remoteMonitor = MonitorHandle(app.Handle);
                    check(remoteMonitor != sourceMonitor && !IsIconic(app.Handle) && IsZoomed(app.Handle) == maximized && mover.CanUndo, mode + ": outgoing move preserves state and Undo");
                    fresh = Target(app, fakeTaskbar);
                    check(fresh.Window == app.Handle && fresh.Process == OwnProcess && fresh.Windows.Length == 1, mode + ": return resolves the exact app with a same-process tool/helper present");
                    mover.Enabled = true;
                    if (!cold) {
                        check(mover.TryStartDirectMove(fresh, sourceMonitor, fresh.Button.Position), mode + ": first active-window native press is intercepted");
                        mover.CompleteDirectPress(true);
                        Wait(delegate { mover.ProcessClick(); return Field(mover, "directRelease") == null; });
                    } else {
                        var probe = new TaskbarTargets.Probe { Complete = false };
                        var click = new TaskbarMover.Click { Taskbar = fakeTaskbar.Handle, Position = fresh.Button.Position,
                            PriorWindow = app.Handle, PriorProcess = OwnProcess, PriorMonitor = remoteMonitor, TargetMonitor = sourceMonitor,
                            AppId = fresh.Button.AppId, Time = DateTime.UtcNow, ReleasedAt = DateTime.UtcNow, Verification = probe, Origin = null };
                        mover.CancelPending(); Field(mover, "click", click);
                        app.State(7); // Windows' original foreground click minimized it.
                        mover.ProcessClick();
                        check(Field(mover, "click") == click && !click.Inspected && IsIconic(app.Handle), mode + ": initially missing probe waits without speculative movement");
                        fresh.Captured = DateTime.UtcNow; probe.Result = fresh; probe.Complete = true;
                        check(TaskbarMover.NativeMinimizeMatches(click, fresh), mode + ": completed first-click probe proves the exact cross-monitor minimize");
                        mover.ProcessClick();
                    }
                    check(MonitorHandle(app.Handle) == sourceMonitor && !IsIconic(app.Handle) && IsZoomed(app.Handle) == maximized,
                        mode + ": first return click moves visibly to source without a second activation: " + mover.LastStatus);
                    check(replays == 0 && mover.CanUndo && Field(mover, "click") == null && Field(mover, "directRelease") == null, mode + ": one gesture leaves no replay or pending return and retains recovery");
                    check(app.WithoutActivation(mover.Undo) && MonitorHandle(app.Handle) == remoteMonitor && !IsIconic(app.Handle) && IsZoomed(app.Handle) == maximized,
                        mode + ": first Undo restores the outgoing Ghost Bar destination");
                    check(app.WithoutActivation(mover.Undo) && MonitorHandle(app.Handle) == sourceMonitor && !IsIconic(app.Handle) && IsZoomed(app.Handle) == maximized && !mover.CanUndo,
                        mode + ": second Undo restores original state and consumes exactly two moves");
                    var restored = WindowHistory.Capture(app.Handle);
                    check(restored != null && restored.Placement.ShowCommand == original.Placement.ShowCommand && AreaPolicy.Equal(restored.Placement.NormalPosition, original.Placement.NormalPosition) &&
                        (maximized || AreaPolicy.Equal(Bounds(app.Handle), originalBounds)), mode + ": roundtrip Undo preserves original placement and geometry");
                    check(GetForegroundWindow() == desktopForeground, mode + ": owned native movement preserves actual desktop foreground");
                }
            }
        }
    }
}
