using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class GhostSearchIntegrationTests
    {
        static void Set(object target, string name, object value)
        { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        internal static void Run(Action<bool, string> assert)
        {
            var identity = new ShelfWindow { Handle = new IntPtr(42), Process = 7, ProcessStarted = 99, AppKey = "exe:fixture" };
            var destination = new Display { Device = "fixture", Identity = "owned", X = -1920, Y = -1080, Width = 1920, Height = 1080 };
            var full = new Native.Rect { Left = -1920, Top = -1080, Right = 0, Bottom = 0 };
            assert(GhostSearchPlacement.Matches(identity, identity, destination, "fixture", full, false, true), "Placement verifies identity and exact negative-coordinate fullscreen bounds");
            assert(!GhostSearchPlacement.Matches(identity, identity, destination, "other", full, false, false), "Accepted native movement cannot report placement on the wrong monitor");
            assert(!GhostSearchPlacement.Matches(identity, identity, destination, "fixture", full, true, false), "A minimized app is not reported ready");
            var resized = full; resized.Bottom = -40;
            assert(!GhostSearchPlacement.Matches(identity, identity, destination, "fixture", resized, false, true), "Fullscreen partial bounds cannot report success");
            var reused = new ShelfWindow { Handle = identity.Handle, Process = 7, ProcessStarted = 100, AppKey = identity.AppKey };
            assert(!GhostSearchPlacement.Matches(identity, reused, destination, "fixture", full, false, false), "A reused HWND cannot satisfy placement verification");
            foreach (int dpi in new[] { 96, 120, 144, 192, 288 })
                foreach (int width in new[] { 160, 240, 480, 1920, 3840 }) {
                    int effective = Math.Max(96, Math.Min(dpi, width * 96 / 160));
                    int capacity = GhostBarSearchLayout.Capacity(width, effective), slot = GhostBarSearchLayout.Slot(width, effective);
                    assert(capacity >= 1 && capacity <= 10, "Search shelf capacity is bounded at " + width + "/" + effective);
                    int row = GhostBarSearchLayout.Width(width, effective, capacity, true);
                    assert(row <= width && row >= WindowShelfPolicy.Scale(16, effective) + (capacity + 3) * slot,
                        "Apps, both page controls and Search fit inside the monitor at " + width + "/" + effective);
                    using (var icon = GhostBarSearchLayout.Icon(effective))
                        assert(icon.Width == WindowShelfPolicy.Scale(20, effective), "Search icon follows monitor DPI");
                }
            using (var session = new GhostSearchSession()) {
                var displays = new List<Display> { new Display { Device = "fixture", Identity = "owned", Width = 1920, Height = 1080 } };
                var rules = new List<AppRule> { new AppRule { Key = "exe:fixture" } };
                session.Update(displays, rules, "before"); displays[0].Width = 1; rules[0].Key = "changed";
                assert(session.Targets[0].Width == 1920 && session.Rules[0].Key == "exe:fixture", "Workers use copied UI snapshots");
                var copied = session.Targets; copied[0].Width = 2;
                assert(session.Targets[0].Width == 1920, "Worker snapshots cannot mutate the next validation");
                session.Update(new Display[0], new AppRule[0], "paused");
                assert(session.Targets.Count == 0 && session.Version == "paused", "Pause and disconnect invalidate the worker destination");
                session.Dispose(); session.Update(displays, rules, "stale");
                assert(!session.Active && session.Version == "paused", "A dismissed session cannot become active again");
            }
            using (var done = new ManualResetEvent(false))
            using (var session = new GhostSearchSession()) {
                bool sta = false;
                session.Read(delegate { sta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA; done.Set(); return new List<GhostApp>(); });
                assert(done.WaitOne(5000), "App discovery completes on a background worker");
                List<GhostApp> apps; bool failed; bool taken = false;
                for (int i = 0; i < 100 && !taken; i++) { taken = session.Take(out apps, out failed); if (!taken) Thread.Sleep(5); }
                assert(sta && taken, "App discovery uses STA and publishes its result once");
                assert(!session.Take(out apps, out failed), "A catalog result cannot be consumed twice");
            }
            using (var started = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var done = new ManualResetEvent(false))
            using (var session = new GhostSearchSession()) {
                bool cancelled = false;
                session.Read(delegate(Func<bool> stop) { started.Set(); release.WaitOne(5000); cancelled = stop(); done.Set(); return new List<GhostApp>(); });
                try { assert(started.WaitOne(5000), "Cancellation fixture starts"); session.Dispose(); }
                finally { release.Set(); }
                assert(done.WaitOne(5000) && cancelled, "Closing search cancels a worker already in progress");
                List<GhostApp> apps; bool failed;
                assert(!session.Take(out apps, out failed), "A late discovery result cannot reopen a dismissed popup");
            }
            foreach (int width in new[] { 160, 480, 1920 })
                foreach (int count in new[] { 0, 1, 30 })
                    using (var manager = new WindowShelfManager(delegate { return new List<Display>(); },
                        delegate { return new List<AppRule>(); }, delegate { throw new Exception("No movement is authorized in layout tests"); })) {
                        var target = new Display { Device = "fixture", Identity = "owned", Width = width, Height = 1080 };
                        var groups = Enumerable.Range(0, count).Select(i => new List<ShelfWindow> {
                            new ShelfWindow { Title = "Fixture " + i, AppName = "Fixture", AppKey = "exe:fixture" + i } }).ToList();
                        var popup = new ShelfPopup("Fixture Ghost Bar");
                        Set(manager, "target", target); Set(manager, "groups", groups); Set(manager, "shelf", popup);
                        typeof(WindowShelfManager).GetMethod("BuildShelf", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, new object[] { "No available windows" });
                        var search = popup.Controls.OfType<ShelfButton>().Single(b => b.AccessibleName == "Search apps");
                        assert(search != null && popup.ClientRectangle.Contains(search.Bounds), "Search is available even with no windows at " + width + "/" + count);
                        assert(popup.Controls.OfType<ShelfButton>().All(b => popup.ClientRectangle.Contains(b.Bounds)), "Every app and page button remains inside the shelf");
                        assert(popup.Controls.OfType<ShelfButton>().All(b => b == search || !b.Bounds.IntersectsWith(search.Bounds)), "Search never overlaps an existing app/page control");
                        assert(count < 30 || popup.Controls.OfType<ShelfButton>().Count(b => b.AccessibleName == "Previous apps" || b.AccessibleName == "More apps") == 2,
                            "Search retains both paging controls for many windows");
                    }
            KeyboardSearchManagerTests.Run(assert);
        }
    }

    internal static class KeyboardSearchManagerTests
    {
        static T Get<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        static void Set(object target, string name, object value)
        { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        static void Poll(WindowShelfManager manager)
        { typeof(WindowShelfManager).GetMethod("PollSearch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null); }
        static void UpdateContext(WindowShelfManager manager)
        { typeof(WindowShelfManager).GetMethod("UpdateSearchContext", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null); }
        static Native.Rect Rect(Rectangle value)
        { return new Native.Rect { Left = value.Left, Top = value.Top, Right = value.Right, Bottom = value.Bottom }; }

        // This fixture never shows a popup or calls a production mover, launcher,
        // settings store, Controller, or display configuration API.
        sealed class Fixture : IDisposable
        {
            internal readonly List<Display> Connected = new List<Display> {
                new Display { Device = "owned-primary", Identity = "owned-primary-id", Primary = true, X = 0, Y = 0, Width = 1920, Height = 1080 },
                new Display { Device = "owned-upper", Identity = "owned-upper-id", X = -1600, Y = -900, Width = 1600, Height = 900 }
            };
            internal readonly Dictionary<string, int> Dpis = new Dictionary<string, int> { { "owned-primary", 96 }, { "owned-upper", 192 } };
            internal readonly Dictionary<string, Rectangle> Work = new Dictionary<string, Rectangle> {
                { "owned-primary", new Rectangle(0, 0, 1920, 1040) }, { "owned-upper", new Rectangle(-1552, -900, 1552, 900) }
            };
            internal readonly Settings Settings = new Settings();
            internal readonly WindowShelfManager Manager;
            internal readonly GhostApp App = new GhostApp { Id = "owned-fixture", Name = "Owned Fixture", AppKey = "exe:owned-fixture", LaunchPath = "never-launched" };
            internal bool Paused, BlankIdentity, UnreadableIdentity;
            internal int Presented, CatalogReads, Moves, Launches, CancelledMoves;
            internal Display MovedTo;
            internal Func<Display, List<AppRule>, Func<bool>, List<ShelfWindow>> WindowReader;
            internal Fixture()
            {
                Manager = new WindowShelfManager(delegate { return new List<Display>(); }, delegate { return new List<AppRule>(); },
                    delegate { throw new Exception("Hover movement is forbidden in keyboard search tests"); }, null, delegate { return Settings; },
                    delegate { CancelledMoves++; }, delegate { return Paused ? new List<Display>() : Connected; },
                    delegate(ShelfWindow window, Display destination) { Moves++; MovedTo = KeyboardSearchDestination.Copy(destination); return false; });
                Manager.SearchDpiReader = delegate(Display display) { return Dpis[display.Device]; };
                Manager.SearchIdentityReader = delegate(Display display) {
                    if (UnreadableIdentity) throw new InvalidOperationException("Owned native identity read failure");
                    var current = Connected.FirstOrDefault(d => d.Device == display.Device);
                    return BlankIdentity || current == null ? "" : current.Identity;
                };
                Manager.SearchMonitorReader = delegate(Display display) {
                    var current = Connected.FirstOrDefault(d => d.Device == display.Device);
                    if (current == null) return null;
                    return (Native.MonitorInfo?)new Native.MonitorInfo { Device = current.Device, Flags = current.Primary ? 1u : 0u,
                        Monitor = Rect(WindowShelfPolicy.Bounds(current)), Work = Rect(Work[current.Device]) };
                };
                Manager.SearchPresenter = delegate { Presented++; };
                Manager.SearchCatalogReader = delegate { Interlocked.Increment(ref CatalogReads); return new List<GhostApp> { App.Copy() }; };
                Manager.SearchAppLauncher = delegate { Interlocked.Increment(ref Launches); throw new Exception("Actual app launches are forbidden in keyboard search tests"); };
                WindowReader = delegate { return new List<ShelfWindow> { new ShelfWindow {
                    Handle = new IntPtr(0x0BADF00D), Process = 77, ProcessStarted = 99, AppKey = App.AppKey, AppName = App.Name, Title = App.Name } }; };
                Manager.SearchWindowReader = delegate(Display display, List<AppRule> rules, Func<bool> cancelled) { return WindowReader(display, rules, cancelled); };
            }
            internal GhostSearchPopup Popup { get { return Get<GhostSearchPopup>(Manager, "search"); } }
            internal GhostSearchSession Session { get { return Get<GhostSearchSession>(Manager, "searchSession"); } }
            internal GhostLaunchCoordinator Launch { get { return Get<GhostLaunchCoordinator>(Manager, "searchLaunch"); } }
            internal bool TimerActive { get { return Get<System.Windows.Forms.Timer>(Manager, "timer").Enabled; } }
            internal bool Open(int index) { return Manager.OpenSearchOnDisplay(KeyboardSearchDestination.Copy(Connected[index])); }
            internal void Select()
            { Popup.SetCatalog(new List<GhostApp> { App.Copy() }); Popup.Query = "Owned"; Popup.HandleSearchKey(Keys.Enter); }
            internal bool WaitIdle()
            {
                for (int i = 0; i < 600; i++) {
                    Poll(Manager); var launch = Launch;
                    if (launch == null || !launch.Busy) return true;
                    Thread.Sleep(5);
                }
                return false;
            }
            public void Dispose() { Manager.Dispose(); }
        }

        static void OutsidePointerChecks(Action<bool, string> assert)
        {
            foreach (int monitor in new[] { 0, 1 })
                using (var fixture = new Fixture()) {
                    assert(fixture.Open(monitor), "Pointer fixture opens on monitor " + monitor);
                    var popup = fixture.Popup; var session = fixture.Session;
                    popup.Query = "Typing continues";
                    Point center = new Point(popup.Left + popup.Width / 2, popup.Top + popup.Height / 2);
                    assert(!fixture.Manager.ObservePointerDown(center) && fixture.Popup == popup && session.Active && popup.Query == "Typing continues",
                        "Inside mouse-down preserves search and typed text on monitor " + monitor);
                    Poll(fixture.Manager);
                    assert(fixture.Popup == popup && session.Active && !popup.IsHandleCreated,
                        "Search polling never treats ordinary pointer position as an outside click on monitor " + monitor);
                    // Simulate a partially expanded frame, without creating an HWND.
                    popup.Bounds = new Rectangle(popup.Left + popup.Width / 3, popup.Top + popup.Height / 3,
                        Math.Max(32, popup.Width / 3), Math.Max(32, popup.Height / 3));
                    center = new Point(popup.Left + popup.Width / 2, popup.Top + popup.Height / 2);
                    assert(!fixture.Manager.ObservePointerDown(center) && fixture.Popup == popup,
                        "Inside classification follows the current animated frame on monitor " + monitor);
                    Point outside = new Point(popup.Right, center.Y);
                    assert(fixture.Manager.ObservePointerDown(outside) && fixture.Popup == null && !session.Active && popup.IsDisposed,
                        "Mouse-down at the exclusive frame boundary cancels search immediately on monitor " + monitor);
                    assert(!fixture.Manager.ObservePointerDown(outside) && fixture.CancelledMoves == 0,
                        "Repeated outside clicks are idempotent and do not cancel unrelated moves on monitor " + monitor);
                }

            Func<Rectangle, Point>[] outsidePoints = {
                b => new Point(b.Left - 1, b.Top + b.Height / 2),
                b => new Point(b.Right, b.Top + b.Height / 2),
                b => new Point(b.Left + b.Width / 2, b.Top - 1),
                b => new Point(b.Left + b.Width / 2, b.Bottom),
                b => new Point(-800, -450)
            };
            for (int i = 0; i < outsidePoints.Length; i++)
                using (var fixture = new Fixture()) {
                    fixture.Open(0); var popup = fixture.Popup; var session = fixture.Session;
                    assert(fixture.Manager.ObservePointerDown(outsidePoints[i](popup.Bounds)) && fixture.Popup == null && !session.Active,
                        "Blank desktop and other-monitor mouse-down dismiss without a foreground change " + i);
                    assert(fixture.Moves == 0 && fixture.Launches == 0 && fixture.CancelledMoves == 0,
                        "Outside mouse-down does not perform an app action or cancel unrelated movement " + i);
                }
            using (var fixture = new Fixture()) {
                fixture.Open(0); var popup = fixture.Popup;
                if (popup.Region != null && !popup.Region.IsVisible(0, 0))
                    assert(fixture.Manager.ObservePointerDown(popup.Location), "An excluded rounded corner counts as an outside click");
            }

            using (var fixture = new Fixture())
            using (var started = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var done = new ManualResetEvent(false)) {
                fixture.WindowReader = delegate { started.Set(); release.WaitOne(5000); done.Set(); return new List<ShelfWindow>(); };
                fixture.Open(0); fixture.Select(); var popup = fixture.Popup; var session = fixture.Session;
                try {
                    assert(started.WaitOne(5000) && popup.Busy, "Busy outside-click fixture starts its background request");
                    assert(fixture.Manager.ObservePointerDown(new Point(popup.Right + 10, popup.Top)) && fixture.Popup == null && !session.Active,
                        "Outside mouse-down immediately cancels a Busy search instead of waiting for deactivation");
                } finally { release.Set(); }
                assert(done.WaitOne(5000) && fixture.WaitIdle() && fixture.Moves == 0 && fixture.Launches == 0,
                    "A worker completing after outside mouse-down cannot open or move an app");
            }
            using (var fixture = new Fixture()) {
                fixture.Open(0); var popup = fixture.Popup; var session = fixture.Session;
                Set(fixture.Manager, "searchMoving", true);
                try {
                    assert(fixture.Manager.ObservePointerDown(new Point(popup.Left - 10, popup.Top)) && !session.Active && fixture.CancelledMoves > 0,
                        "Outside mouse-down fences an owned native search move immediately");
                    int cancellations = fixture.CancelledMoves;
                    assert(!fixture.Manager.ObservePointerDown(new Point(popup.Left - 10, popup.Top)) && fixture.CancelledMoves == cancellations,
                        "Repeated outside mouse-down cannot recancel an already dismissed operation");
                } finally { Set(fixture.Manager, "searchMoving", false); }
            }
            using (var fixture = new Fixture()) {
                fixture.Open(0); var popup = fixture.Popup;
                fixture.Session.Dispose(); fixture.Launch.Cancel();
                Set(popup, "dismissing", true);
                assert(fixture.Manager.ObservePointerDown(new Point(popup.Right + 10, popup.Top)) && fixture.Popup == popup && popup.IsDismissing && !fixture.Session.Active,
                    "Mouse-down during dismissal preserves the cancelled session and existing animation without reopening search");
            }
        }

        static void WithKeyboardController(Fixture fixture, Action<Controller> action)
        {
            var controller = new Controller(false) { Settings = fixture.Settings, Enabled = true };
            Set(controller, "shelf", fixture.Manager);
            try { action(controller); }
            finally {
                // This hook-free controller never manages desktop taskbars.
                // Release only its owned resources; Dispose would run global
                // recovery and is deliberately excluded from these fixtures.
                controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                foreach (var binding in controller.MonitorShortcuts) binding.Dispose();
                foreach (var binding in controller.ActionShortcuts) binding.Dispose();
                foreach (var binding in controller.NavigationShortcuts) binding.Dispose();
            }
        }

        static void KeyboardToggleChecks(Action<bool, string> assert)
        {
            foreach (int monitor in new[] { 0, 1 })
                using (var fixture = new Fixture()) {
                    assert(fixture.Open(monitor), "Keyboard toggle fixture opens on monitor " + monitor);
                    var popup = fixture.Popup; var session = fixture.Session;
                    popup.Query = "Discarded on close";
                    WithKeyboardController(fixture, delegate(Controller controller) {
                        var beforeDisplays = controller.Displays;
                        controller.OpenAppSearch(IntPtr.Zero);
                        assert(fixture.Popup == null && popup.IsDisposed && !session.Active,
                            "Second shortcut closes Search and its session even without an eligible foreground " + monitor);
                        assert(controller.Mover.LastStatus == "Search closed" && controller.Revision == 1 &&
                            Object.ReferenceEquals(controller.Displays, beforeDisplays),
                            "Toggle reports closure before any monitor discovery or resolution " + monitor);
                        assert(fixture.Moves == 0 && fixture.Launches == 0 && fixture.CancelledMoves == 0,
                            "Idle toggle does not touch an app or unrelated movement " + monitor);
                        assert(fixture.Open(monitor) && fixture.Popup != popup && fixture.Session != session && fixture.Popup.Query == "",
                            "Search reopens with a fresh panel and query after toggle dismissal " + monitor);
                        controller.OpenAppSearch(new IntPtr(0xFFFF));
                        assert(fixture.Popup == null && controller.Revision == 2 && controller.Mover.LastStatus == "Search closed",
                            "Reopened Search can be toggled closed again " + monitor);
                    });
                }
            using (var fixture = new Fixture())
            using (var started = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var done = new ManualResetEvent(false)) {
                fixture.WindowReader = delegate { started.Set(); release.WaitOne(5000); done.Set(); return new List<ShelfWindow>(); };
                fixture.Open(0); fixture.Select(); var popup = fixture.Popup; var session = fixture.Session;
                try {
                    assert(started.WaitOne(5000) && popup.Busy, "Keyboard toggle starts a pending background request");
                    WithKeyboardController(fixture, delegate(Controller controller) { controller.OpenAppSearch(IntPtr.Zero); });
                    assert(fixture.Popup == null && !session.Active && popup.IsDisposed,
                        "Second shortcut cancels Busy Search immediately");
                } finally { release.Set(); }
                assert(done.WaitOne(5000) && fixture.WaitIdle() && fixture.Moves == 0 && fixture.Launches == 0,
                    "A worker finishing after shortcut dismissal cannot launch or move an app");
            }
            using (var fixture = new Fixture()) {
                fixture.Open(0); var session = fixture.Session;
                Set(fixture.Manager, "searchMoving", true);
                try {
                    WithKeyboardController(fixture, delegate(Controller controller) { controller.OpenAppSearch(IntPtr.Zero); });
                    assert(!session.Active && fixture.CancelledMoves > 0 && fixture.Popup == null,
                        "Keyboard dismissal cancels an owned native Search move");
                } finally { Set(fixture.Manager, "searchMoving", false); }
            }
            using (var fixture = new Fixture()) {
                fixture.Open(0); var popup = fixture.Popup; var session = fixture.Session;
                session.Dispose(); fixture.Launch.Cancel(); Set(popup, "dismissing", true);
                WithKeyboardController(fixture, delegate(Controller controller) {
                    controller.OpenAppSearch(IntPtr.Zero); controller.OpenAppSearch(new IntPtr(42));
                    assert(fixture.Popup == popup && popup.IsDismissing && fixture.Presented == 1 && !session.Active &&
                        controller.Mover.LastStatus == "Search closed" && controller.Revision == 2,
                        "Rapid shortcut repeats during dismissal cannot reopen or reactivate Search");
                });
            }
        }

        internal static void Run(Action<bool, string> assert)
        {
            var primary = new Display { Device = "DISPLAY", Identity = "stable", Primary = true, Width = 1920, Height = 1080 };
            assert(KeyboardSearchDestination.Eligible(primary), "Keyboard Search accepts a connected primary display");
            assert(!WindowShelfPolicy.SameTarget(primary, primary), "Primary display support does not change Ghost Bar hover eligibility");
            assert(!KeyboardSearchDestination.Eligible(null) && !KeyboardSearchDestination.Eligible(new Display { Device = "DISPLAY", Width = 1920, Height = 1080 }),
                "Keyboard Search rejects missing display identities");
            var copy = KeyboardSearchDestination.Copy(primary); copy.Device = "display"; copy.Identity = "STABLE";
            assert(KeyboardSearchDestination.Same(primary, copy), "Keyboard destination matching preserves case-insensitive Windows identities");
            copy.Primary = false;
            assert(!KeyboardSearchDestination.Same(primary, copy), "Keyboard destination matching captures primary status");
            copy = KeyboardSearchDestination.Copy(primary); copy.Y = -1080;
            assert(!KeyboardSearchDestination.Same(primary, copy), "Keyboard destination matching captures stacked monitor geometry");
            copy = KeyboardSearchDestination.Copy(primary); copy.Width = 159;
            assert(!KeyboardSearchDestination.Eligible(copy), "Keyboard Search rejects unusably small display surfaces");
            OutsidePointerChecks(assert);
            KeyboardToggleChecks(assert);

            using (var fixture = new Fixture()) {
                assert(fixture.Open(0), "Keyboard Search opens on primary without any configured Ghost Bar");
                var popup = fixture.Popup; var session = fixture.Session;
                assert(popup != null && !popup.Visible && !popup.IsHandleCreated && fixture.Presented == 1,
                    "Manager fixtures create search without showing or activating an HWND");
                assert(fixture.TimerActive && Get<List<Display>>(fixture.Manager, "enabled").Count == 0,
                    "Search polling remains active when Ghost Bar target count is zero");
                assert(fixture.Work[fixture.Connected[0].Device].Contains(popup.ExpandedBounds), "Primary Search fits the actual taskbar work area");
                popup.Query = "Retained query";
                assert(fixture.Open(0) && fixture.Popup == popup && fixture.Session == session && popup.Query == "Retained query" && fixture.Presented == 1,
                    "Repeated direct Search opening retains its destination, query and catalog session");
                fixture.Manager.Refresh();
                assert(fixture.Popup == popup && fixture.TimerActive, "Refresh does not dismiss manual Search when there are no Ghost Bars");
                assert(!fixture.Manager.IsSearchWindow(IntPtr.Zero) && !fixture.Manager.IsSearchWindow(new IntPtr(42)) && !popup.IsHandleCreated,
                    "Search HWND checks never create a hidden topmost window");
                assert(fixture.Open(1) && fixture.Popup != popup && popup.IsDisposed && !session.Active,
                    "Search on another monitor cancels the previous panel before opening the next");
                assert(fixture.Work[fixture.Connected[1].Device].Contains(fixture.Popup.ExpandedBounds) && Get<int>(fixture.Manager, "dpi") == 192,
                    "Manual Search fits negative coordinates, side taskbars and mixed DPI");
                var destination = Get<Display>(fixture.Manager, "target");
                assert(destination.Device == "owned-upper" && destination.X == -1600 && destination.Y == -900,
                    "Search captures the selected upper monitor independently of pointer position");
                assert(fixture.Manager.DismissSearch() && fixture.Popup == null && !fixture.Manager.DismissSearch(),
                    "Explicit Search dismissal is idempotent and reports whether a panel existed");
                fixture.Manager.Refresh();
                assert(!fixture.TimerActive, "Closing the final manual panel stops polling with no Ghost Bars");
                assert(fixture.Open(0) && fixture.Presented == 3, "A dismissed keyboard Search can be reopened cleanly");
            }

            foreach (int index in new[] { 0, 1 })
                using (var fixture = new Fixture()) {
                    assert(fixture.Open(index), "Existing-window Search fixture opens on display " + index);
                    fixture.Select();
                    assert(fixture.WaitIdle() && fixture.Moves == 1 && fixture.Launches == 0 &&
                        KeyboardSearchDestination.Same(fixture.MovedTo, fixture.Connected[index]),
                        "Manual Search routes an existing app to the captured display through the manual shared mover " + index);
                    assert(fixture.Popup != null && !fixture.Launch.Succeeded && fixture.Popup.StatusText.IndexOf("could not be moved", StringComparison.OrdinalIgnoreCase) >= 0,
                        "A failed move keeps Search and its truthful failure status readable " + index);
                }

            Action<Fixture>[] invalidations = {
                f => f.Paused = true,
                f => f.Connected.Clear(),
                f => f.Connected.Add(KeyboardSearchDestination.Copy(f.Connected[0])),
                f => f.Dpis[f.Connected[0].Device] = 144,
                f => f.Work[f.Connected[0].Device] = new Rectangle(48, 0, 1872, 1040),
                f => f.Connected[0].Y = 80,
                f => f.Connected[0].Identity = "replacement-display",
                f => f.Connected[0].Primary = false,
                f => f.Settings.AllowMovingFullscreenApps = !f.Settings.AllowMovingFullscreenApps,
                f => f.BlankIdentity = true,
                f => f.UnreadableIdentity = true
            };
            for (int i = 0; i < invalidations.Length; i++)
                using (var fixture = new Fixture()) {
                    assert(fixture.Open(0), "Invalidation fixture opens " + i);
                    var popup = fixture.Popup; var session = fixture.Session;
                    invalidations[i](fixture); fixture.Manager.Refresh();
                    assert(fixture.Popup == null && popup.IsDisposed && !session.Active && !fixture.TimerActive && fixture.Moves == 0 && fixture.Launches == 0,
                        "Pause/disconnect/identity/DPI/work-area/settings changes cancel manual Search safely " + i);
                }
            using (var fixture = new Fixture()) {
                fixture.Paused = true;
                assert(!fixture.Open(0) && fixture.Popup == null && fixture.Presented == 0, "Paused movement cannot open manual Search");
                fixture.Paused = false; fixture.Manager.SetActivationPreview(true);
                assert(!fixture.Open(0), "Activation preview suspends keyboard Search");
                fixture.Manager.SetActivationPreview(false);
                assert(fixture.Open(0), "Keyboard Search recovers after preview closes");
                fixture.Manager.Dispose();
                assert(!fixture.Open(0) && !fixture.Manager.DismissSearch(), "Disposed managers cannot reopen Search");
            }

            Action<Settings>[] contextChanges = {
                s => s.ScaleWindowSizeToMonitor = !s.ScaleWindowSizeToMonitor,
                s => s.CloseWindowShortcut = 0,
                s => s.SearchAppsShortcut = 0,
                s => s.UndoMoveShortcut = 0,
                s => s.MonitorMoveShortcuts[0] = 0
            };
            for (int i = 0; i < contextChanges.Length; i++)
                using (var fixture = new Fixture())
                using (var started = new ManualResetEvent(false))
                using (var release = new ManualResetEvent(false))
                using (var done = new ManualResetEvent(false)) {
                    fixture.WindowReader = delegate { started.Set(); release.WaitOne(5000); done.Set(); return new List<ShelfWindow>(); };
                    assert(fixture.Open(0), "Settings cancellation fixture opens " + i);
                    fixture.Select();
                    try {
                        assert(started.WaitOne(5000), "Settings cancellation worker starts " + i);
                        string before = fixture.Session.Version; contextChanges[i](fixture.Settings); UpdateContext(fixture.Manager);
                        assert(fixture.Session.Version != before, "Manual Search context captures changed settings " + i);
                        Poll(fixture.Manager);
                        assert(!fixture.Launch.Succeeded && fixture.Launch.Status.IndexOf("Settings changed", StringComparison.OrdinalIgnoreCase) >= 0,
                            "Changed settings cancel an app operation before it can launch or move " + i);
                    } finally { release.Set(); }
                    assert(done.WaitOne(5000) && fixture.WaitIdle() && fixture.Moves == 0 && fixture.Launches == 0,
                        "Late worker completion after settings changes cannot execute an app request " + i);
                }

            // Execute the exact old gap: the first worker queues shell activation,
            // then settings are saved and Refresh runs before either another Poll
            // or the queued activation worker. No shell is invoked by this fixture.
            for (int i = 0; i < contextChanges.Length; i++)
                using (var fixture = new Fixture()) {
                    assert(fixture.Open(0), "Synchronous settings fence fixture opens " + i);
                    var session = fixture.Session; var popup = fixture.Popup;
                    var queued = new List<Action>();
                    fixture.Launch.Dispose();
                    var launch = new GhostLaunchCoordinator(delegate { return session.Targets; }, delegate { return session.Rules; },
                        delegate { return session.Active; }, delegate { return new List<ShelfWindow>(); },
                        delegate { fixture.Moves++; return false; }, delegate { fixture.Launches++; return new GhostLaunchResult(); },
                        delegate { return session.Version; }, Stopwatch.GetTimestamp, GhostLaunchCoordinator.DefaultTimeoutMilliseconds, queued.Add);
                    Set(fixture.Manager, "searchLaunch", launch);
                    fixture.Select();
                    assert(queued.Count == 1, "Existing-window read is queued before the shell request " + i);
                    Action read = queued[0]; queued.Clear(); read(); Poll(fixture.Manager);
                    assert(queued.Count == 1 && launch.Busy, "Shell request waits in the deterministic queue " + i);
                    contextChanges[i](fixture.Settings); fixture.Manager.Refresh();
                    assert(!session.Active && fixture.Popup == null && popup.IsDisposed,
                        "Settings Refresh synchronously invalidates the old session before Poll " + i);
                    queued[0]();
                    assert(fixture.Launches == 0 && fixture.Moves == 0 && !launch.Busy,
                        "A launch queued before settings Refresh cannot execute afterward " + i);
                }

            Action<Fixture>[] nativeChanges = {
                f => f.Connected.Clear(),
                f => f.Dpis[f.Connected[0].Device] = 144,
                f => f.Work[f.Connected[0].Device] = new Rectangle(0, 48, 1920, 992),
                f => f.Connected[0].X = 64,
                f => f.Connected[0].Primary = false,
                f => f.Connected[0].Identity = "same-geometry-replacement-id",
                f => f.BlankIdentity = true,
                f => f.UnreadableIdentity = true
            };
            for (int i = 0; i < nativeChanges.Length; i++)
                using (var fixture = new Fixture()) {
                    assert(fixture.Open(0), "Native launch fence fixture opens " + i);
                    var session = fixture.Session; var queued = new List<Action>();
                    var nativeGuard = Get<Func<GhostApp, GhostLaunchResult>>(fixture.Launch, "launch");
                    fixture.Launch.Dispose();
                    var launch = new GhostLaunchCoordinator(delegate { return session.Targets; }, delegate { return session.Rules; },
                        delegate { return session.Active; }, delegate { return new List<ShelfWindow>(); },
                        delegate { fixture.Moves++; return false; }, nativeGuard,
                        delegate { return session.Version; }, Stopwatch.GetTimestamp, GhostLaunchCoordinator.DefaultTimeoutMilliseconds, queued.Add);
                    Set(fixture.Manager, "searchLaunch", launch); fixture.Select();
                    Action read = queued[0]; queued.Clear(); read(); Poll(fixture.Manager);
                    assert(queued.Count == 1, "App opening is queued before a native topology change " + i);
                    nativeChanges[i](fixture);
                    // Intentionally execute before Refresh or Poll publishes any
                    // new UI snapshots: the worker must recheck native geometry.
                    queued[0]();
                    assert(fixture.Launches == 0 && fixture.Moves == 0,
                        "Native destination changes block queued app opening before the next UI refresh " + i);
                    fixture.Manager.Refresh();
                    assert(fixture.Popup == null && !session.Active, "The next Refresh closes a natively invalid search destination " + i);
                }

            var lease = typeof(GhostLaunchCoordinator).GetField("nativeLaunchPending", BindingFlags.Static | BindingFlags.NonPublic);
            int previousLease = (int)lease.GetValue(null);
            try {
                lease.SetValue(null, 1);
                using (var fixture = new Fixture()) {
                    assert(fixture.Open(0) && fixture.Popup.Busy && fixture.Popup.StatusText.IndexOf("previous app request", StringComparison.OrdinalIgnoreCase) >= 0,
                        "A reopened Search applies the process-wide launch lease before its first selection");
                    fixture.Select();
                    assert(fixture.Moves == 0 && fixture.Launches == 0 && String.IsNullOrEmpty(fixture.Launch.Status),
                        "Busy Search cannot submit a second app request while an earlier shell launch is pending");
                    lease.SetValue(null, 0); Poll(fixture.Manager);
                    assert(!fixture.Popup.Busy && fixture.Popup.StatusText.IndexOf("previous app request", StringComparison.OrdinalIgnoreCase) < 0,
                        "Blank coordinator status still refreshes Busy when the old launch finishes");
                }
            } finally { lease.SetValue(null, previousLease); }
            using (var fixture = new Fixture()) {
                fixture.Manager.SearchCatalogReader = delegate { throw new Exception("Owned catalog error"); };
                assert(fixture.Open(0), "Catalog-error fixture opens");
                for (int i = 0; i < 600 && fixture.Popup.StatusText.IndexOf("Could not load", StringComparison.OrdinalIgnoreCase) < 0; i++) {
                    Poll(fixture.Manager); Thread.Sleep(5);
                }
                Poll(fixture.Manager);
                assert(fixture.Popup.StatusText.IndexOf("Could not load installed apps", StringComparison.OrdinalIgnoreCase) >= 0 && !fixture.Popup.Busy,
                    "Refreshing blank coordinator status preserves installed-app catalog errors");
            }

            foreach (var action in new[] {
                new { Title = "Close focused window shortcut", Default = Keys.Control | Keys.Shift | Keys.W },
                new { Title = "Search apps shortcut", Default = Keys.Control | Keys.Shift | Keys.Space }
            })
                using (var dialog = new ShortcutDialog(action.Title, Keys.None, delegate { throw new Exception("Hidden dialogs cannot save settings"); }, action.Default)) {
                    var toggle = Get<ToggleSwitch>(dialog, "enabled");
                    var choice = typeof(ShortcutDialog).GetProperty("SelectedShortcut", BindingFlags.Instance | BindingFlags.NonPublic);
                    assert(dialog.Text == action.Title && dialog.AccessibleName == action.Title && (Keys)choice.GetValue(dialog, null) == Keys.None,
                        "Action shortcut dialog preserves its exact label and Off state: " + action.Title);
                    toggle.Checked = true;
                    assert((Keys)choice.GetValue(dialog, null) == action.Default && !dialog.Visible,
                        "Re-enabling an Off action proposes its own shortcut rather than Undo: " + action.Title);
                }
            using (var dialog = new ShortcutDialog("Search apps shortcut", Keys.Control | Keys.Alt | Keys.F, delegate { }, Keys.Control | Keys.Shift | Keys.Space)) {
                var choice = typeof(ShortcutDialog).GetProperty("SelectedShortcut", BindingFlags.Instance | BindingFlags.NonPublic);
                assert((Keys)choice.GetValue(dialog, null) == (Keys.Control | Keys.Alt | Keys.F), "Action dialog preserves an existing custom shortcut");
            }
            using (var dialog = new ShortcutDialog(Keys.None, delegate { })) {
                Get<ToggleSwitch>(dialog, "enabled").Checked = true;
                var choice = typeof(ShortcutDialog).GetProperty("SelectedShortcut", BindingFlags.Instance | BindingFlags.NonPublic);
                assert(dialog.Text == "Undo shortcut" && (Keys)choice.GetValue(dialog, null) == GlobalShortcut.Default,
                    "Undo dialog describes window actions and retains its default binding");
            }
        }
    }
}
