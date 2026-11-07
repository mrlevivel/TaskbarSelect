using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
namespace TaskbarSelect
{
    internal static class Beta7FullscreenTests
    {
        sealed class QuietForm : Form { protected override bool ShowWithoutActivation { get { return true; } } }
        sealed class QuietHelper : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }
        sealed class Monitor { internal IntPtr Handle; internal Native.MonitorInfo Info; }
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr window, int index, int value);
        internal static int RunFocused()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool result, string name) { if (!result) throw new Exception(name); count++; };
            try {
                RunFeatureChecks(check); ProfileFixTests.Run(check); FeatureFixTests.Run(check);
                MovementFixTests.RunManaged(check); MovementFixTests.RunOwned(check);
                int featureCount = count;
                WindowMoveCoordinatorTests.Run(check);
                TaskbarTargetRecoveryTests.Run(check);
                ActivationPreviewTests.Run(check);
                int placement = Tests.RunWindowPlacementCalculations();
                int persistence = Tests.RunGhostBarSensitivityPersistenceChecks();
                Console.WriteLine("PASS: " + featureCount + " focused beta7/fullscreen/shortcut assertions; " + (count - featureCount) +
                    " coordinator/lookup/activation-preview assertions; " + placement + " placement and " + persistence + " persistence assertions. Total " + (count + placement + persistence) + ".");
                Console.WriteLine("No desktop hooks, native focus actions, normal app instance, game actions or Explorer taskbar mutations were requested.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine("FAIL after " + count + ": " + error); return 1; }
        }
        internal static void RunFeatureChecks(Action<bool, string> check)
        { SettingsAndPolicy(check); BindingTransactions(check); Layout(check); OwnWindowShortcuts(check); OwnedMoves(check); MinimizedFullscreenTests.Run(check); }
        static void SettingsAndPolicy(Action<bool, string> check)
        {
            var legacy = new JavaScriptSerializer().Deserialize<Settings>("{\"HiddenIdentities\":[]}");
            check(!legacy.AllowMovingFullscreenApps && legacy.WindowShelfHoverDelayMs == 100 && legacy.WindowShelfActivationWidth == 480 && legacy.WindowShelfActivationHeight == 24, "Legacy beta7 defaults retained with fullscreen off and reachable height");
            check(legacy.MonitorMoveShortcuts.SequenceEqual(MonitorShortcutPolicy.Defaults()), "Legacy profiles get requested four chords");
            var saved = new JavaScriptSerializer().Deserialize<Settings>("{\"HiddenIdentities\":[],\"WindowShelfHoverDelayMs\":645,\"WindowShelfActivationWidth\":160,\"WindowShelfActivationHeight\":72}");
            saved.AllowMovingFullscreenApps = true; saved.MonitorMoveShortcuts[2] = 0;
            var copy = saved.Copy(); var history = new SettingsHistory(); history.Push(saved, null, "Test");
            saved.MonitorMoveShortcuts[0] = 0; saved.AllowMovingFullscreenApps = false; saved.WindowShelfHoverDelayMs = 100; saved.WindowShelfActivationHeight = 6;
            check(copy.AllowMovingFullscreenApps && copy.MonitorMoveShortcuts[0] != 0 && copy.MonitorMoveShortcuts[2] == 0 && copy.WindowShelfHoverDelayMs == 645 && copy.WindowShelfActivationWidth == 160 && copy.WindowShelfActivationHeight == 72, "Copy preserves all sensitivity settings and deep-copies chord array");
            copy.MonitorMoveShortcuts[1] = 0;
            check(history.Peek.Settings.AllowMovingFullscreenApps && history.Peek.Settings.MonitorMoveShortcuts[0] != 0 && history.Peek.Settings.MonitorMoveShortcuts[1] != 0 && history.Peek.Settings.WindowShelfHoverDelayMs == 645 && history.Peek.Settings.WindowShelfActivationHeight == 72, "History snapshot owns independent chord array, opt-in and explicit timing/height");
            check(WindowShelfPolicy.MinHoverDelayMs == 100 && WindowShelfPolicy.MaxHoverDelayMs == 1500 && WindowShelfPolicy.DefaultHoverDelayMs == 100, "Beta7 hover delay bounds/default retained");
            check(WindowShelfPolicy.MinActivationWidth == 80 && WindowShelfPolicy.MaxActivationWidth == 960 && WindowShelfPolicy.DefaultActivationWidth == 480, "Beta7 activation width bounds/default retained");
            check(Path.GetDirectoryName(Program.ConfigPath) == AppProfile.Directory && Path.GetDirectoryName(WorkAreas.JournalPath) == AppProfile.Directory, "Settings and recovery use the selected profile");
            check(AppProfile.AllowsStartup == !AppProfile.IsTest, "Only explicit test mode restricts Startup");
            bool rejected = false;
            for (int i = 1; i <= 4; i++) {
                var display = new Display { Device = @"\\.\DISPLAY" + i, Width = 1920, Height = 1080 };
                check(MonitorShortcutPolicy.Target(new[] { display }, i) == display, "Mapping uses visible device number " + i);
                check(MonitorShortcutPolicy.Target(new[] { display, display }, i) == null && MonitorShortcutPolicy.Target(new Display[0], i) == null, "Ambiguous/missing mapping never falls back " + i);
                check(GlobalShortcut.Format((Keys)MonitorShortcutPolicy.Defaults()[i - 1]) == "Ctrl + Shift + " + i, "Requested default chord " + i);
            }
            var sample = new[] { new Display { Device = @"\\.\DISPLAY4", Width = 100, Height = 100 }, new Display { Device = @"\\.\DISPLAY2", Width = 100, Height = 100, Primary = true } };
            check(MonitorShortcutPolicy.Target(sample, 2) == sample[1] && MonitorShortcutPolicy.Target(sample, 1) == null && MonitorShortcutPolicy.Target(sample, 5) == null, "Mapping ignores list order/main position and rejects unknown slots");
            var duplicate = new Settings(); duplicate.MonitorMoveShortcuts[1] = duplicate.MonitorMoveShortcuts[0];
            rejected = false; try { MonitorShortcutPolicy.Validate(duplicate); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Duplicate shortcuts cannot steal another app action");
            duplicate = new Settings(); duplicate.MonitorMoveShortcuts[0] = duplicate.UndoMoveShortcut;
            rejected = false; try { MonitorShortcutPolicy.Validate(duplicate); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Undo chord remains independent");
            check(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0, false, true, true) && WindowShelfPolicy.TaskSwitchable(true, false, true, 0, false, true, true, true), "Fullscreen listing requires explicit opt-in");
            foreach (bool allow in new[] { false, true }) {
                check(WindowShelfPolicy.TaskSwitchable(true, false, true, 0, false, false, true, allow), "Normal eligibility remains unchanged");
                check(!WindowShelfPolicy.TaskSwitchable(false, false, true, 0, false, true, true, allow), "Visibility exclusion retained");
                check(!WindowShelfPolicy.TaskSwitchable(true, true, true, 0, false, true, true, allow), "Owned exclusion retained");
                check(!WindowShelfPolicy.TaskSwitchable(true, false, false, 0, false, true, true, allow), "Disabled exclusion retained");
                check(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0x80, false, true, true, allow), "Tool exclusion retained");
                check(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0x08000000, false, true, true, allow), "No-activate exclusion retained");
                check(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0, true, true, true, allow), "Cloaking exclusion retained");
                check(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0, false, true, false, allow), "Untitled exclusion retained");
            }
            check(!FullscreenMovePolicy.Matches(new Native.Rect { Right = 1920, Bottom = 1032 }, new Native.Rect { Right = 1920, Bottom = 1080 }), "Work area cannot pass full-monitor verification");
            var sensitivity = Settings.CreateFresh();
            using (var manager = new WindowShelfManager(delegate { return new List<Display>(); }, delegate { return new List<AppRule>(); }, delegate { return false; }, null, delegate { return sensitivity; })) {
                check(!manager.RefreshSensitivity(), "Beta7 sensitivity defaults need no reset");
                sensitivity.AllowMovingFullscreenApps = true;
                check(manager.RefreshSensitivity() && !manager.RefreshSensitivity(), "Fullscreen catalog change invalidates stale chooser once");
                sensitivity.WindowShelfHoverDelayMs = 645;
                check(manager.RefreshSensitivity(), "Saved delay changes still reset the activation dwell");
            }
        }
        static void BindingTransactions(Action<bool, string> check)
        {
            var pool = Enumerable.Range(0, 6).Select(i => new GlobalShortcut()).ToArray(); var chords = new List<Keys>();
            try {
                for (Keys key = Keys.F13; key <= Keys.F24 && chords.Count < pool.Length; key++) {
                    Keys chord = Keys.Control | Keys.Alt | Keys.Shift | key;
                    try { pool[chords.Count].Apply(chord, delegate { }); chords.Add(chord); } catch (InvalidOperationException) { }
                }
                check(chords.Count == 6, "Six temporary unused test bindings found without stealing");
                var bindings = pool.Take(4).ToArray(); var originals = chords.Take(4).ToArray(); pool[5].Apply(Keys.None, delegate { });
                var choices = originals.Select(k => (int)k).ToArray(); choices[0] = 0; choices[1] = (int)chords[5]; choices[2] = (int)chords[4];
                bool committed = false, rejected = false;
                try { MonitorShortcutPolicy.Apply(bindings, choices, delegate { committed = true; }); } catch (InvalidOperationException) { rejected = true; }
                check(rejected && !committed && bindings.Select(b => b.Current).SequenceEqual(originals), "Conflict keeps every previous registration and never commits");
                pool[5].Apply(chords[5], delegate { }); check(pool[5].Current == chords[5], "Conflict releases tentative registrations"); pool[5].Apply(Keys.None, delegate { }); choices[2] = (int)originals[2];
                rejected = false; try { MonitorShortcutPolicy.Apply(bindings, choices, delegate { throw new IOException("Injected save failure"); }); } catch (IOException) { rejected = true; }
                check(rejected && bindings.Select(b => b.Current).SequenceEqual(originals), "Save failure rolls back rebinds and disable");
                MonitorShortcutPolicy.Apply(bindings, choices, delegate { committed = true; });
                check(committed && bindings[0].Current == Keys.None && bindings[1].Current == chords[5], "Successful transaction commits new bindings");
                pool[5].Apply(originals[0], delegate { }); check(pool[5].Current == originals[0], "Commit releases old chord");
            } finally { foreach (var binding in pool) binding.Dispose(); }
        }
        static IEnumerable<Control> Controls(Control parent)
        { foreach (Control child in parent.Controls) { yield return child; foreach (var sub in Controls(child)) yield return sub; } }
        static void Layout(Action<bool, string> check)
        {
            string before = File.Exists(Program.ConfigPath) ? File.ReadAllText(Program.ConfigPath) : null;
            var controller = new Controller(false) { Settings = new Settings { WindowShelfHoverDelayMs = 645, WindowShelfActivationWidth = 160, WindowShelfActivationHeight = 48 }, Enabled = false,
                Displays = FeatureFixTests.ShortcutDisplays(1, 2, 3, 4) };
            try {
                using (var form = new SettingsForm(controller)) { // Never shown or activated.
                    var fields = Controls(form).OfType<NumericUpDown>().ToArray();
                    check(form.Text == AppProfile.Title, "UI identifies the selected profile");
                    check(fields.Any(f => f.AccessibleName == "Ghost Bar hover delay" && f.Minimum == 100 && f.Maximum == 1500 && f.Value == 645), "Real beta7 timing control preserves explicit delay");
                    check(fields.Any(f => f.AccessibleName == "Ghost Bar activation width" && f.Minimum == 80 && f.Maximum == 960 && f.Value == 160), "Real beta7 width control preserves explicit width");
                    check(fields.Any(f => f.AccessibleName == "Ghost Bar activation height" && f.Minimum == 1 && f.Maximum == 96 && f.Value == 48), "Real height control allows one pixel and preserves explicit custom height without changing released controls");
                    check(Controls(form).OfType<ToggleSwitch>().Any(t => t.Text == "Allow moving fullscreen apps" && !t.Checked), "Fullscreen control defaults off");
                    check(Controls(form).OfType<Button>().Count(b => (b.AccessibleName ?? "").StartsWith("Change move to Display")) == 4, "Four configurable shortcut controls retained");
                    check(Controls(form).OfType<Label>().Any(l => l.Text.Contains("including this settings window")) &&
                        !Controls(form).OfType<Label>().Any(l => l.Text.Contains("own settings window stays put")), "UI describes own settings shortcut support");
                    check(Controls(form).OfType<ToggleSwitch>().Any(t => AppProfile.IsTest ? t.Text == "Startup disabled in local test" && !t.Enabled : t.Text.StartsWith("Start with Windows")), "Startup control matches selected profile");
                    check((File.Exists(Program.ConfigPath) ? File.ReadAllText(Program.ConfigPath) : null) == before, "Control construction writes no profile data");
                }
            } finally { controller.Mover.Dispose(); controller.UndoShortcut.Dispose(); foreach (var binding in controller.MonitorShortcuts) binding.Dispose(); foreach (var binding in controller.ActionShortcuts) binding.Dispose(); foreach (var binding in controller.NavigationShortcuts) binding.Dispose(); }
        }
        static void OwnedMoves(Action<bool, string> check)
        {
            var monitors = new List<Monitor>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) }; if (Native.GetMonitorInfo(handle, ref info)) monitors.Add(new Monitor { Handle = handle, Info = info }); return true;
            }, IntPtr.Zero);
            if (monitors.Count < 2) { Console.WriteLine("SKIP: owned movement fixtures require two monitors"); return; }
            var a = monitors[0]; var b = monitors[1];
            var target = new Display { Device = b.Info.Device, X = b.Info.Monitor.Left, Y = b.Info.Monitor.Top, Width = b.Info.Monitor.Right - b.Info.Monitor.Left, Height = b.Info.Monitor.Bottom - b.Info.Monitor.Top };
            using (var window = new QuietForm { Text = "Owned beta7 fullscreen regression fixture", Opacity = 0, ShowInTaskbar = true, StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(a.Info.Work.Left + 60, a.Info.Work.Top + 60, 320, 240) })
            using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new Exception("No input replay is allowed"); }, delegate { return window.Handle; }, false)) {
                window.Show(); Application.DoEvents();
                mover.MoveApi.Modifiers = delegate { return 0; };
                int focusCalls = 0;
                mover.MoveApi.Focus = delegate(IntPtr hwnd) { check(hwnd == window.Handle, "Injected focus never targets another window"); focusCalls++; return true; };
                var fields = BindingFlags.NonPublic | BindingFlags.Instance;
                check(new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" }.All(n => (IntPtr)typeof(TaskbarMover).GetField(n, fields).GetValue(mover) == IntPtr.Zero), "Owned mover installs no desktop hooks");
                var native = mover.MoveApi.Position;
                mover.MoveApi.Position = delegate(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags) {
                    check(hwnd == window.Handle && (flags & 0x10) != 0, "Position changes only owned fixture without activation");
                    return SetWindowPos(hwnd, after, x, y, width, height, flags);
                };
                check(mover.MoveFocusedWindow(window.Handle, target, 2), "Ordinary same-process focused-window shortcut uses shared mover without a PID override");
                check(Native.MonitorFromWindow(window.Handle, 2) == b.Handle && focusCalls == 1, "Ordinary window reaches selected monitor without native focus");
                check(!mover.MoveFocusedWindow(window.Handle, null, 4) && mover.LastStatus.Contains("unavailable"), "Missing target preserves window with clear feedback");
                check(!mover.MoveFocusedWindow(IntPtr.Zero, target, 2) && mover.LastStatus.Contains("focused window changed"), "Stale foreground capture cannot substitute another window");
                window.FormBorderStyle = FormBorderStyle.None;
                window.Bounds = Rectangle.FromLTRB(a.Info.Monitor.Left, a.Info.Monitor.Top, a.Info.Monitor.Right, a.Info.Monitor.Bottom); Application.DoEvents();
                ShelfWindow selected, current;
                check(!WindowShelfCatalog.TryReadWindow(window.Handle, out selected) && WindowShelfCatalog.TryReadWindow(window.Handle, true, out selected), "Fullscreen catalog is opt-in");
                check(!mover.MoveFocusedWindow(window.Handle, target, 2) && mover.LastStatus.Contains("Enable Allow"), "Fullscreen shortcut rejects default/off");
                mover.AllowMovingFullscreenApps = true;
                mover.ExcludedApps.Add(new AppRule { Key = selected.AppKey });
                check(!mover.MoveFocusedWindow(window.Handle, target, 2), "Shortcut retains fullscreen app exclusions");
                mover.ExcludedApps.Clear();
                var stale = new ShelfWindow { Handle = selected.Handle, Process = selected.Process, ProcessStarted = selected.ProcessStarted + 1, AppKey = selected.AppKey };
                check(!WindowShelfCatalog.TryReadSelection(stale, target, new List<AppRule>(), null, out current, true), "Fullscreen selection rejects replaced process identity");
                var original = a.Info.Monitor;
                check(mover.MoveFromShelf(selected, target), "Ghost Bar and shortcuts share opt-in full-monitor branch"); Pump(1150);
                Native.Rect actual;
                check(Native.GetWindowRect(window.Handle, out actual) && FullscreenMovePolicy.Matches(actual, b.Info.Monitor) && mover.LastStatus.StartsWith("Fullscreen window observed"), "Full destination bounds and bounded observation verified");
                check(mover.CanUndo && mover.Undo(), "Fullscreen Undo uses the preserved bounded worker");
                check(Native.GetWindowRect(window.Handle, out actual) && FullscreenMovePolicy.Matches(actual, original) && mover.LastStatus.StartsWith("Fullscreen restore observed"), "Undo verifies original full bounds before reporting success");
                check(mover.MoveFromShelf(selected, target), "Second fullscreen attempt is accepted");
                window.Bounds = Rectangle.FromLTRB(original.Left, original.Top, original.Right, original.Bottom); Pump(1150);
                check(mover.LastStatus.Contains("refused, moved back") && mover.CanUndo, "Snapback is honest and retains Undo"); check(mover.Undo(), "Snapback recovery remains bounded");
                mover.MoveApi.Position = delegate { return false; };
                check(!mover.MoveFromShelf(selected, target), "Position refusal is not success");
                mover.MoveApi.Position = delegate { Thread.Sleep(1100); return true; };
                check(!mover.MoveFocusedWindow(window.Handle, target, 2) && mover.LastStatus.Contains("timed out") && mover.CanUndo, "Hotkey preserves deadline interruption and uncertain-mutation recovery");
                mover.MoveApi.Position = native;
                check(mover.Undo(), "Timeout recovery uses bounded worker and original identity");
                mover.MoveApi.Position = delegate { mover.CancelPending(); return true; };
                check(!mover.MoveFocusedWindow(window.Handle, target, 2) && mover.LastStatus.Contains("cancelled") && mover.CanUndo,
                    "Hotkey cancellation is not replaced by success and preserves uncertain recovery");
                mover.MoveApi.Position = native;
                check(mover.Undo(), "Cancellation recovery retains the bounded worker path");
                var capture = mover.MoveApi.Capture;
                mover.MoveApi.Capture = delegate { return null; };
                check(!mover.MoveFocusedWindow(window.Handle, target, 2), "Missing recovery snapshot blocks fullscreen mutation");
                mover.MoveApi.Capture = delegate(IntPtr hwnd) { var entry = capture(hwnd); entry.ProcessStarted++; return entry; };
                check(!mover.MoveFocusedWindow(window.Handle, target, 2), "Replaced capture identity is rejected before fullscreen mutation");
                mover.MoveApi.Capture = capture;
                var changedTarget = new Display { Device = target.Device, X = target.X + 1, Y = target.Y, Width = target.Width, Height = target.Height };
                check(!mover.MoveFocusedWindow(window.Handle, changedTarget, 2) && mover.LastStatus.Contains("changed or is unavailable"),
                    "Hotkey fences changed destination geometry before mutation");
                var recovery = (WindowHistory)typeof(TaskbarMover).GetField("history", fields).GetValue(mover);
                var invalid = WindowHistory.Capture(window.Handle); invalid.FullscreenBounds = new Native.Rect(); recovery.Remember(invalid);
                check(!mover.Undo() && mover.CanUndo && mover.LastStatus.Contains("refused the restore"), "Rejected fullscreen restore retains its Undo entry rather than claiming success");
                check(focusCalls >= 3, "Fixture focus requests were injected rather than native");
            }
        }
        static T WithoutActivation<T>(IntPtr window, Func<T> action)
        {
            int original = GetWindowLong(window, -20);
            SetWindowLong(window, -20, original | 0x08000000);
            try { return action(); } finally { SetWindowLong(window, -20, original); }
        }
        static void ShowOwned(Form form, bool maximized)
        {
            IntPtr handle = form.Handle;
            WithoutActivation(handle, delegate { return ShowWindow(handle, 4); });
            if (maximized) WithoutActivation(handle, delegate { return ShowWindow(handle, 3); });
            Application.DoEvents();
        }
        static void DisposeControllerFixtures(Controller controller)
        {
            // Controller.Dispose restores Explorer by design; fixture cleanup
            // disposes only the hooks-free components it constructed.
            controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
            foreach (var binding in controller.MonitorShortcuts) binding.Dispose();
            foreach (var binding in controller.ActionShortcuts) binding.Dispose();
            foreach (var binding in controller.NavigationShortcuts) binding.Dispose();
        }
        static void CheckNoHooks(TaskbarMover mover, Action<bool, string> check, string mode)
        {
            var fields = BindingFlags.NonPublic | BindingFlags.Instance;
            check(new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" }.All(name =>
                (IntPtr)typeof(TaskbarMover).GetField(name, fields).GetValue(mover) == IntPtr.Zero), mode + ": no desktop hooks installed");
        }
        static bool ForegroundIsNotFixture()
        {
            IntPtr foreground = GetForegroundWindow(); uint pid;
            return foreground == IntPtr.Zero || Native.GetWindowThreadProcessId(foreground, out pid) != 0 && pid != (uint)Process.GetCurrentProcess().Id;
        }
        static Display DisplayFor(Monitor monitor)
        {
            return new Display { Device = monitor.Info.Device, X = monitor.Info.Monitor.Left, Y = monitor.Info.Monitor.Top,
                Width = monitor.Info.Monitor.Right - monitor.Info.Monitor.Left, Height = monitor.Info.Monitor.Bottom - monitor.Info.Monitor.Top };
        }
        // Actual SettingsForm HWNDs in this process, not a PID-policy override.
        // Alpha zero and nonactivating show/placement wrappers protect the user's
        // foreground; movement still uses the real bounded native worker.
        internal static void OwnWindowShortcuts(Action<bool, string> check)
        {
            var monitors = new List<Monitor>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(handle, ref info)) monitors.Add(new Monitor { Handle = handle, Info = info });
                return true;
            }, IntPtr.Zero);
            // The real settings window has a minimum size. Pick available work
            // areas that can hold it without changing the user's display setup.
            monitors = monitors.Where(m => m.Info.Work.Right - m.Info.Work.Left >= 900 && m.Info.Work.Bottom - m.Info.Work.Top >= 850)
                .OrderByDescending(m => (long)(m.Info.Work.Right - m.Info.Work.Left) * (m.Info.Work.Bottom - m.Info.Work.Top)).ToList();
            if (monitors.Count < 2) { Console.WriteLine("SKIP: real own SettingsForm movement requires two existing work areas at least 900x850; no display changes made."); return; }
            var source = monitors[0]; var target = monitors[1];
            int helpers = WindowMoveWorker.ActiveHelpers;
            string before = File.Exists(Program.ConfigPath) ? File.ReadAllText(Program.ConfigPath) : null;
            foreach (bool relative in new[] { false, true }) foreach (bool maximized in new[] { false, true }) {
                string mode = "Own SettingsForm/" + (relative ? "relative" : "fixed") + "/" + (maximized ? "maximized" : "normal");
                var controller = new Controller(false) { Settings = new Settings(), Enabled = false };
                try {
                    using (var form = new SettingsForm(controller) { Opacity = 0, StartPosition = FormStartPosition.Manual,
                        Location = new Point(source.Info.Work.Left + 30, source.Info.Work.Top + 30) })
                    using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new InvalidOperationException("Real input replay forbidden in own-window tests."); }, delegate { return form.Handle; }, false)) {
                        CheckNoHooks(controller.Mover, check, mode + "/controller"); CheckNoHooks(mover, check, mode);
                        mover.Enabled = true; mover.ScaleWindowSizeToMonitor = relative; mover.MoveApi.Modifiers = delegate { return 0; };
                        int focusCalls = 0;
                        mover.MoveApi.Focus = delegate(IntPtr window) { check(window == form.Handle, mode + ": focus adapter targets only its owned settings HWND"); focusCalls++; return true; };
                        var placementApi = mover.MoveApi.Placement;
                        mover.MoveApi.Placement = delegate(IntPtr window, ref WindowHistory.Placement placement) {
                            if (window != form.Handle) throw new InvalidOperationException("Foreign placement forbidden.");
                            var copy = placement; return WithoutActivation(window, delegate { return placementApi(window, ref copy); });
                        };
                        ShowOwned(form, maximized);
                        ShelfWindow selected;
                        check(WindowShelfCatalog.TryReadWindow(form.Handle, out selected), mode + ": real own settings HWND passes shared selection without a PID override");
                        var original = WindowHistory.Capture(form.Handle);
                        check(original != null && Native.MonitorFromWindow(form.Handle, 2) == source.Handle && IsZoomed(form.Handle) == maximized, mode + ": native starting placement is captured");
                        for (int repeat = 0; repeat < 2; repeat++) {
                            check(mover.MoveFocusedWindow(form.Handle, DisplayFor(target), 1), mode + ": focused shortcut moves actual own settings window");
                            check(Native.MonitorFromWindow(form.Handle, 2) == target.Handle && IsZoomed(form.Handle) == maximized && !IsIconic(form.Handle) && mover.CanUndo,
                                mode + ": own settings arrives without minimizing and retains recovery");
                            var outgoing = WindowHistory.Capture(form.Handle);
                            check(outgoing != null, mode + ": captures settings placement on the other monitor");
                            bool returned = mover.MoveFocusedWindow(form.Handle, DisplayFor(source), 2);
                            check(returned && Native.MonitorFromWindow(form.Handle, 2) == source.Handle && IsZoomed(form.Handle) == maximized,
                                mode + ": repeated return preserves native state; moved=" + returned + "; status=" + mover.LastStatus + "; actualMonitor=" + Native.MonitorFromWindow(form.Handle, 2).ToInt64() +
                                "; expectedMonitor=" + source.Handle.ToInt64() + "; zoomed=" + IsZoomed(form.Handle));
                            CheckOwnUndo(form, mover, outgoing, maximized, mode + "/first Undo", check);
                            CheckOwnUndo(form, mover, original, maximized, mode + "/second Undo", check);
                            check(!mover.CanUndo, mode + ": both Undo entries were consumed");
                        }
                        check(focusCalls == 4 && ForegroundIsNotFixture(), mode + ": all four focus requests are injected and no fixture becomes real foreground; focusCalls=" + focusCalls +
                            "; actualHWND=" + GetForegroundWindow().ToInt64() + "; ownedHWND=" + form.Handle.ToInt64());
                    }
                } finally { DisposeControllerFixtures(controller); }
            }
            OwnHelpersRemainBlocked(check, source, target);
            check(ForegroundIsNotFixture(), "Own settings and helper fixtures never become actual desktop foreground");
            check(WindowMoveWorker.ActiveHelpers == helpers && WindowMoveWorker.UncertainHelpers == 0, "Own settings fixtures leave no movement helpers running");
            check((File.Exists(Program.ConfigPath) ? File.ReadAllText(Program.ConfigPath) : null) == before, "Own settings shortcut tests do not write profile data");
        }
        static void CheckOwnUndo(Form form, TaskbarMover mover, WindowHistory.Entry expected, bool maximized, string stage, Action<bool, string> check)
        {
            var elapsed = Stopwatch.StartNew();
            bool accepted = WithoutActivation(form.Handle, mover.Undo);
            var observed = WindowHistory.Capture(form.Handle);
            string device = observed == null ? "unreadable" : observed.Monitor.Device;
            bool zoomed = IsZoomed(form.Handle), minimized = IsIconic(form.Handle);
            string detail = "; monitor=" + device + "; expected=" + expected.Monitor.Device + "; maximized=" + zoomed +
                "; minimized=" + minimized + "; undoAvailable=" + mover.CanUndo + "; elapsedMs=" + elapsed.ElapsedMilliseconds +
                "; " + mover.MovementDiagnostic + "; status=" + mover.LastStatus;
            check(accepted, stage + " was accepted" + detail);
            check(observed != null && TaskbarMover.ReturnPlacement.SameMonitor(expected.Monitor, observed.Monitor), stage + " restores the expected monitor" + detail);
            check(zoomed == maximized && !minimized, stage + " restores the expected visible state" + detail);
            check(observed != null && AreaPolicy.Equal(observed.Placement.NormalPosition, expected.Placement.NormalPosition),
                stage + " preserves the floating restore bounds" + detail);
        }
        static void OwnHelpersRemainBlocked(Action<bool, string> check, Monitor source, Monitor target)
        {
            using (var owner = new QuietForm { Text = "Neutral owned modal parent", Opacity = 0, StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(source.Info.Work.Left + 20, source.Info.Work.Top + 20, 280, 180) }) {
                ShowOwned(owner, false);
                foreach (string kind in new[] { "Ghost Bar noactivate", "disabled", "owned modal", "tool window" }) {
                    Form window = kind == "Ghost Bar noactivate" ? (Form)new QuietHelper() : new QuietForm();
                    using (window) {
                        window.Text = "Neutral owned helper rejection fixture"; window.Opacity = 0; window.StartPosition = FormStartPosition.Manual;
                        window.Bounds = new Rectangle(source.Info.Work.Left + 40, source.Info.Work.Top + 40, 260, 160);
                        if (kind == "owned modal") window.Owner = owner;
                        if (kind == "tool window") { window.FormBorderStyle = FormBorderStyle.FixedToolWindow; window.ShowInTaskbar = false; }
                        ShowOwned(window, false);
                        if (kind == "disabled") window.Enabled = false;
                        using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new InvalidOperationException("Replay forbidden."); }, delegate { return window.Handle; }, false)) {
                            CheckNoHooks(mover, check, kind);
                            mover.Enabled = true; mover.MoveApi.Modifiers = delegate { return 0; };
                            int mutations = 0, focus = 0;
                            mover.MoveApi.Position = delegate { mutations++; return false; };
                            mover.MoveApi.Placement = delegate(IntPtr handle, ref WindowHistory.Placement placement) { mutations++; return false; };
                            mover.MoveApi.Focus = delegate { focus++; return false; };
                            ShelfWindow selected;
                            check(!WindowShelfCatalog.TryReadWindow(window.Handle, out selected), kind + ": own helper remains excluded by shared native selection");
                            check(!mover.MoveFocusedWindow(window.Handle, DisplayFor(target), 1) && mover.LastStatus.Contains("unavailable or excluded"), kind + ": shortcut fails closed through shared selection");
                            check(mutations == 0 && focus == 0 && !mover.CanUndo && Native.MonitorFromWindow(window.Handle, 2) == source.Handle,
                                kind + ": rejected own helper has no mutation, focus or recovery entry");
                        }
                    }
                }
            }
        }
        static void Pump(int ms)
        { var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * ms / 1000; while (Stopwatch.GetTimestamp() < until) { Application.DoEvents(); Thread.Sleep(5); } }
    }
}
