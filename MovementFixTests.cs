using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class MovementFixTests
    {
        sealed class RestoreFixture
        {
            internal readonly Dictionary<long, bool> Markers = new Dictionary<long, bool>();
            internal readonly List<long> Requests = new List<long>();
            internal readonly List<long> Released = new List<long>();
            internal readonly Native.TaskbarRestoreApi Api;
            internal Func<long, bool> Show = delegate { return true; };
            internal bool ReleaseAccepted = true;
            internal RestoreFixture()
            {
                Api = new Native.TaskbarRestoreApi {
                    Marked = delegate(IntPtr window) { bool marked; return Markers.TryGetValue(window.ToInt64(), out marked) && marked; },
                    Show = delegate(IntPtr window) { Requests.Add(window.ToInt64()); return Show(window.ToInt64()); },
                    Release = delegate(IntPtr window) {
                        if (!ReleaseAccepted) return false;
                        Markers[window.ToInt64()] = false; Released.Add(window.ToInt64()); return true;
                    }
                };
            }
            internal Bar Add(long handle)
            { Markers.Add(handle, true); return new Bar { Handle = handle, Device = "Owned recovery fixture " + handle, Managed = true }; }
        }

        internal static void RunManaged(Action<bool, string> check)
        {
            var fixture = new RestoreFixture(); var bar = fixture.Add(1);
            fixture.Show = delegate { return false; };
            check(!Native.Restore(bar, fixture.Api) && fixture.Markers[1] && fixture.Released.Count == 0,
                "Rejected ShowWindowAsync preserves taskbar ownership and propagates false");
            bool areasRestored = false, cleaned = false;
            var result = Recovery.Run(delegate { areasRestored = true; },
                delegate { Native.RestoreAll(new[] { bar }, fixture.Api); }, delegate { cleaned = true; });
            check(areasRestored && !result.Success && !cleaned && result.Messages.Count == 1 && result.Messages[0].Contains(bar.Device),
                "A false native restore is reported after work-area recovery and blocks uninstall cleanup");
            check(fixture.Markers[1], "Taskbar marker remains the pending native recovery record after areas succeed");
            check(Recovery.Report(result) == 1, "Rejected native recovery returns a failure exit code");
            fixture.Show = delegate { return true; };
            result = Recovery.Run(delegate { }, delegate { Native.RestoreAll(new[] { bar }, fixture.Api); }, delegate { cleaned = true; });
            check(result.Success && cleaned && !fixture.Markers[1] && fixture.Released.Count == 1,
                "A successful retry releases the marker exactly once and permits cleanup");
            int requests = fixture.Requests.Count;
            check(Native.Restore(bar, fixture.Api) && fixture.Requests.Count == requests,
                "Repeated recovery does not claim or show an unmarked window");

            fixture = new RestoreFixture(); var rejected = fixture.Add(11); var denied = fixture.Add(12); var healthy = fixture.Add(13);
            fixture.Show = delegate(long handle) {
                if (handle == 12) throw new Win32Exception(5, "Owned elevated-denial boundary fixture");
                return handle != 11;
            };
            cleaned = false;
            result = Recovery.Run(delegate { }, delegate { Native.RestoreAll(new[] { rejected, denied, healthy }, fixture.Api); }, delegate { cleaned = true; });
            check(!result.Success && !cleaned && result.Messages.Count == 2 && fixture.Requests.Count == 3,
                "Restoration aggregates rejection and access-denial errors while attempting every taskbar");
            check(fixture.Markers[11] && fixture.Markers[12] && !fixture.Markers[13] && fixture.Released.Count == 1,
                "Only accepted native requests release ownership; failed bars remain independently retryable");
            check(result.Messages[0].Contains(rejected.Device) && result.Messages[1].Contains("elevated-denial"),
                "Recovery reports each native restoration failure rather than hiding aggregate details");
            fixture.Show = delegate { return true; };
            requests = fixture.Requests.Count;
            result = Recovery.Run(delegate { }, delegate { Native.RestoreAll(new[] { rejected, denied, healthy }, fixture.Api); }, delegate { cleaned = true; });
            check(result.Success && cleaned && fixture.Requests.Count == requests + 2 && fixture.Released.Count == 3,
                "Retry requests only the two pending marked taskbars and then permits cleanup");

            fixture = new RestoreFixture(); bar = fixture.Add(21); fixture.ReleaseAccepted = false; cleaned = false;
            result = Recovery.Run(delegate { }, delegate { Native.RestoreAll(new[] { bar }, fixture.Api); }, delegate { cleaned = true; });
            check(!result.Success && !cleaned && fixture.Markers[21], "Marker-release rejection also retains retry ownership and prevents cleanup");
            fixture.ReleaseAccepted = true; fixture.Show = delegate { return false; };
            result = Recovery.Run(delegate { throw new IOException("Owned pending work-area recovery fixture"); },
                delegate { Native.RestoreAll(new[] { bar }, fixture.Api); }, delegate { cleaned = true; });
            check(!result.Success && !cleaned && result.Messages.Count == 2 && fixture.Markers[21],
                "Work-area failure cannot skip native restoration or hide its separate rejection");

            var monitor = new Rectangle(-1920, 0, 1920, 1080);
            check(WindowShelfPolicy.Fullscreen(monitor, monitor, false, true, false), "Captionless maximized full-monitor windows use the shared fullscreen classifier");
            check(!WindowShelfPolicy.Fullscreen(monitor, monitor, false, true, true), "Captioned maximized windows retain ordinary movement eligibility");
            check(!WindowShelfPolicy.Fullscreen(monitor, monitor, true, true, false), "Minimized windows retain ordinary restore handling");
            foreach (bool allow in new[] { false, true }) {
                check(TaskbarMover.DirectMoveAllowed(true, false, new IntPtr(1), new IntPtr(2), true, allow) == allow,
                    "Taskbar fullscreen admission follows the opt-in for either maximized classification");
                check(TaskbarMover.DirectMoveAllowed(true, false, new IntPtr(1), new IntPtr(2), false, allow),
                    "Normal taskbar admission is independent of fullscreen opt-in");
            }
        }

        sealed class QuietForm : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; } }
        }
        sealed class Monitor { internal IntPtr Handle; internal Native.MonitorInfo Info; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        // Native preparation and reads touch only a nonactivating owned window.
        // Every attempted movement/focus boundary below is injected per mover.
        internal static void RunOwned(Action<bool, string> check)
        {
            var monitors = new List<Monitor>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(handle, ref info)) monitors.Add(new Monitor { Handle = handle, Info = info });
                return true;
            }, IntPtr.Zero);
            if (monitors.Count < 2) { Console.WriteLine("SKIP: fullscreen admission fixtures require two monitors."); return; }
            var source = monitors[0]; var target = monitors[1]; IntPtr foreground = GetForegroundWindow();
            using (var window = new QuietForm { Text = "Owned fullscreen admission regression", Opacity = 0, ShowInTaskbar = true,
                StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None,
                Bounds = new Rectangle(source.Info.Work.Left + 60, source.Info.Work.Top + 60, 320, 240) })
            using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new Exception("Input replay is forbidden in movement regression fixtures"); }, delegate { return window.Handle; }, false)) {
                window.Show(); window.WindowState = FormWindowState.Maximized; Application.DoEvents();
                check(SetWindowPos(window.Handle, IntPtr.Zero, source.Info.Monitor.Left, source.Info.Monitor.Top,
                    source.Info.Monitor.Right - source.Info.Monitor.Left, source.Info.Monitor.Bottom - source.Info.Monitor.Top, 0x214),
                    "Owned captionless maximized fixture reaches exact monitor bounds without activation");
                Application.DoEvents();
                check(IsZoomed(window.Handle) && WindowShelfCatalog.IsFullscreen(window.Handle),
                    "Native captionless fixture is both maximized and classified fullscreen");
                mover.Enabled = true; mover.MoveApi.Modifiers = delegate { return 0; };
                int positionCalls = 0, placementCalls = 0, focusCalls = 0;
                var boundedPosition = mover.MoveApi.Position;
                mover.MoveApi.Position = delegate { positionCalls++; return false; };
                mover.MoveApi.Placement = delegate(IntPtr hwnd, ref WindowHistory.Placement placement) { placementCalls++; return false; };
                mover.MoveApi.Focus = delegate { focusCalls++; return true; };
                var fields = BindingFlags.Instance | BindingFlags.NonPublic;
                foreach (string name in new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" })
                    check((IntPtr)typeof(TaskbarMover).GetField(name, fields).GetValue(mover) == IntPtr.Zero, "Movement fixture owns no desktop " + name);
                uint pid; Native.GetWindowThreadProcessId(window.Handle, out pid);
                var direct = new TaskbarTargets.Target { Window = window.Handle, Process = pid, AppKey = AppCatalog.ForWindow(window.Handle).Key,
                    Button = new TaskbarMover.Click { AppId = System.Diagnostics.Process.GetCurrentProcess().ProcessName } };
                check(!mover.TryStartDirectMove(direct, target.Handle), "Fullscreen OFF rejects captionless maximized taskbar mouse-down");
                check(!mover.MoveToMonitor(window.Handle, target.Handle) && mover.LastStatus.Contains("Enable Allow"),
                    "Fullscreen OFF rejects captionless maximized movement through the shared core");
                check(!mover.MoveToMonitor(window.Handle, source.Handle), "Fullscreen OFF is checked before same-monitor completion");
                var display = new Display { Device = target.Info.Device, X = target.Info.Monitor.Left, Y = target.Info.Monitor.Top,
                    Width = target.Info.Monitor.Right - target.Info.Monitor.Left, Height = target.Info.Monitor.Bottom - target.Info.Monitor.Top };
                check(!mover.MoveFocusedWindow(window.Handle, display, 2) && mover.LastStatus.Contains("Enable Allow"),
                    "Fullscreen OFF rejects the same captionless fixture through monitor shortcuts");
                check(positionCalls == 0 && placementCalls == 0 && focusCalls == 0 && !mover.CanUndo,
                    "Rejected fullscreen entry points never request mutation, focus or recovery history");
                mover.AllowMovingFullscreenApps = true;
                check(mover.TryStartDirectMove(direct, target.Handle), "Fullscreen ON admits the same captionless taskbar window"); mover.CancelPending();

                // The catalog excludes our own NOACTIVATE peek helpers. Remove
                // that bit only after nonactivating setup, while all movement
                // requests retain SWP_NOACTIVATE and focus remains injected.
                int extendedStyle = GetWindowLong(window.Handle, -20);
                check(SetWindowLong(window.Handle, -20, extendedStyle & ~0x08000000) != 0,
                    "Owned fullscreen control becomes catalog-eligible without activation");
                check(GetWindow(window.Handle, 4) == IntPtr.Zero && (GetWindowLong(window.Handle, -20) & (0x80 | 0x08000000)) == 0,
                    "Owned fullscreen control has no owner, tool-window or no-activate catalog exclusion");
                ShelfWindow selected;
                check(WindowShelfCatalog.TryReadWindow(window.Handle, true, out selected),
                    "The production catalog accepts the owned captionless maximized identity with opt-in");
                bool accepted = false;
                mover.MoveApi.Position = delegate(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags) {
                    check(hwnd == window.Handle && (flags & 0x10) != 0, "Fullscreen native request is confined to the owned window without activation");
                    positionCalls++; accepted = boundedPosition(hwnd, after, x, y, width, height, flags); return accepted;
                };
                mover.MoveApi.Focus = delegate(IntPtr hwnd) { check(hwnd == window.Handle, "Fullscreen focus is injected only for the owned fixture"); focusCalls++; return true; };
                bool moved = mover.MoveToMonitor(window.Handle, target.Handle); Native.Rect actual;
                check(moved == accepted && (!accepted || mover.CanUndo), "Accepted captionless maximized native movement records recovery before observation");
                check(Native.GetWindowRect(window.Handle, out actual), "Owned fullscreen control reports its native post-request geometry");
                if (mover.CanUndo) {
                    if (accepted && FullscreenMovePolicy.Matches(actual, target.Info.Monitor))
                        check(true, "Captionless maximized ON control reaches full destination monitor bounds");
                    else if (accepted) {
                        Pump(1150);
                        check(mover.CanUndo && mover.LastStatus.Contains("refused, moved back"),
                            "A captionless app's observed refusal is reported while retaining safe Undo");
                    }
                    else check(!moved, "An uncertain or failed native request retains its own recovery record without claiming movement success");
                    mover.AllowMovingFullscreenApps = false;
                    bool restored = mover.Undo();
                    check(restored ? Native.GetWindowRect(window.Handle, out actual) && FullscreenMovePolicy.Matches(actual, source.Info.Monitor) :
                        mover.CanUndo && (mover.LastStatus.Contains("retry") || mover.LastStatus.Contains("refused the restore")),
                        "Fullscreen recovery with opt-in OFF verifies original full bounds or retains a refused restore for retry");
                }
                else check(!accepted && FullscreenMovePolicy.Matches(actual, source.Info.Monitor),
                    "A rejected unmutated fullscreen request leaves original bounds intact without adding history");
                check(GetForegroundWindow() == foreground, "Captionless ON movement and bounded Undo preserve the user's foreground");
                mover.CancelPending();
                // If the controlled app refused its Undo, do not mix that retained
                // record into the separate ordinary-window cancellation controls.
                if (mover.CanUndo) { Console.WriteLine("SKIP: separate captioned-window controls after a refused owned fullscreen Undo."); return; }
                SetWindowLong(window.Handle, -20, extendedStyle);
                positionCalls = placementCalls = focusCalls = 0;
                mover.MoveApi.Position = delegate { positionCalls++; return false; };
                mover.MoveApi.Focus = delegate { focusCalls++; return true; };

                window.WindowState = FormWindowState.Normal; window.FormBorderStyle = FormBorderStyle.Sizable;
                window.Bounds = new Rectangle(source.Info.Work.Left + 60, source.Info.Work.Top + 60, 320, 240);
                window.WindowState = FormWindowState.Maximized; Application.DoEvents();
                direct.Window = window.Handle;
                check(IsZoomed(window.Handle) && !WindowShelfCatalog.IsFullscreen(window.Handle),
                    "Native captioned maximized fixture remains ordinary rather than fullscreen");
                mover.AllowMovingFullscreenApps = false;
                check(mover.TryStartDirectMove(direct, target.Handle), "Fullscreen OFF still admits ordinary captioned maximized taskbar windows"); mover.CancelPending();
                check(!mover.MoveToMonitor(window.Handle, target.Handle) && positionCalls == 1,
                    "Captioned maximized core reaches its injected native request with fullscreen OFF");

                mover.MoveApi.Position = delegate { positionCalls++; mover.Enabled = false; return false; };
                check(!mover.MoveToMonitor(window.Handle, target.Handle) && mover.LastStatus.Contains("cancelled") && !mover.CanUndo,
                    "Pausing during an entered request fences further movement and adds no history for a rejected request");
                mover.Enabled = true; mover.AllowMovingFullscreenApps = true;
                mover.MoveApi.Position = delegate { positionCalls++; mover.AllowMovingFullscreenApps = false; return false; };
                check(!mover.MoveToMonitor(window.Handle, target.Handle) && mover.LastStatus.Contains("cancelled") && !mover.CanUndo,
                    "Turning fullscreen movement OFF during a request cancels its admitted generation");
                var recovery = new WindowHistory(); var snapshot = WindowHistory.Capture(window.Handle);
                check(snapshot != null, "Owned topology fixture captures the production recovery identity");
                snapshot.Monitor.Work.Bottom--;
                recovery.Remember(snapshot);
                int undoRequests = 0, tracked = 0;
                TaskbarMover.MovePosition undoPosition = delegate { undoRequests++; return false; };
                TaskbarMover.MovePlacement undoPlacement = delegate(IntPtr hwnd, ref WindowHistory.Placement placement) { undoRequests++; return false; };
                check(!recovery.Undo(undoPosition, undoPlacement, delegate { tracked++; }) && !recovery.Available && undoRequests == 0 && tracked == 0,
                    "A saved work-area mismatch prunes unsafe Undo before any mutation boundary");
                snapshot = WindowHistory.Capture(window.Handle); snapshot.Monitor.Device = "Owned-disconnected-" + Guid.NewGuid().ToString("N");
                recovery.Remember(snapshot);
                check(!recovery.Undo(undoPosition, undoPlacement, delegate { tracked++; }) && !recovery.Available && undoRequests == 0 && tracked == 0,
                    "A saved disconnected-display identity cannot queue restoration on a replacement monitor");
                check(placementCalls == 0 && focusCalls == 0 && GetForegroundWindow() == foreground,
                    "Owned admission and cancellation fixtures never place, focus or change the user's foreground");
            }
            check(GetForegroundWindow() == foreground, "Disposing the owned fixture preserves the user's foreground");
        }

        static void Pump(int milliseconds)
        {
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000;
            while (Stopwatch.GetTimestamp() < deadline) { Application.DoEvents(); Thread.Sleep(5); }
        }
    }
}
