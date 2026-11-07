using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class WindowShelfTests
    {
        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        sealed class CatalogFixture : Form
        { protected override bool ShowWithoutActivation { get { return true; } } }
        static Display DisplayAt(int x, int y, int width, int height)
        { return new Display { Identity = "shelf-display", Device = "test-display", X = x, Y = y, Width = width, Height = height }; }
        static ShelfWindow Window(int handle, string key, string title)
        { return new ShelfWindow { Handle = new IntPtr(handle), Process = 10, ProcessStarted = 1000, AppKey = key, AppName = key, Title = title }; }

        internal static void RunMinimizeRegistry(Action<bool, string> assert)
        {
            using (var registry = new ShelfMinimizeRegistry()) {
                registry.RecordSuccessfulMinimize(null);
                registry.RecordSuccessfulMinimize(new ShelfWindow());
                assert(registry.Snapshot().Length == 0, "Minimize ledger rejects missing exact identity");
                var original = Window(1, "exe:neutral", "Never retain this title");
                original.Path = "Never retain this path";
                registry.RecordSuccessfulMinimize(original);
                original.Process++;
                var snapshot = registry.Snapshot();
                assert(snapshot.Length == 1 && snapshot[0].Process == 10 && snapshot[0].Title == null && snapshot[0].Path == null && snapshot[0].AppName == null,
                    "Ledger copies only HWND/PID/start/AppKey, never mutable catalog metadata");
                snapshot[0].AppKey = "changed-snapshot";
                assert(registry.Snapshot()[0].AppKey == "exe:neutral", "Background snapshots cannot mutate ledger identities");
                for (int index = 1; index <= ShelfMinimizeRegistry.Capacity + 1; index++)
                    registry.RecordSuccessfulMinimize(Window(index, "exe:neutral", ""));
                snapshot = registry.Snapshot();
                assert(snapshot.Length == ShelfMinimizeRegistry.Capacity && snapshot[0].Handle == new IntPtr(2), "Ledger bounds session records and evicts oldest first");
                registry.RecordSuccessfulMinimize(Window(2, "exe:replacement", ""));
                assert(registry.Snapshot().Count(w => w.Handle == new IntPtr(2)) == 1, "A repeated handle never creates duplicate authority");
                registry.RemoveExcluded(new List<AppRule> { new AppRule { Key = "EXE:REPLACEMENT" } });
                assert(!registry.Snapshot().Any(w => w.Handle == new IntPtr(2)), "New case-insensitive exclusions revoke retained authority");
                var writer = System.Threading.Tasks.Task.Run(delegate {
                    for (int index = 1; index <= 1000; index++) registry.RecordSuccessfulMinimize(Window(index, "exe:concurrent", ""));
                });
                for (int index = 0; index < 1000; index++) {
                    snapshot = registry.Snapshot();
                    assert(snapshot.Length <= ShelfMinimizeRegistry.Capacity && snapshot.Select(w => w.Handle).Distinct().Count() == snapshot.Length,
                        "Concurrent background snapshot is bounded and duplicate-free");
                }
                writer.Wait(); registry.Dispose();
                registry.RecordSuccessfulMinimize(Window(1, "exe:neutral", ""));
                assert(registry.Snapshot().Length == 0, "Disposal clears authority and rejects late records");
            }
        }

        internal static void RunActivation(Action<bool, string> assert)
        {
            var display = Native.Displays().First(d => d.Primary);
            foreach (bool chooser in new[] { false, true }) {
                using (var active = new Form { Text = "Neutral shelf foreground fixture", ShowInTaskbar = true, Opacity = 0,
                    StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(display.X + 80, display.Y + 80, 400, 300) })
                using (var popup = new ShelfPopup("Neutral nonactivating child fixture", chooser) {
                    Bounds = new Rectangle(display.X + 100, display.Y + 100, chooser ? 220 : 64, chooser ? 120 : 56) }) {
                    Control child;
                    var list = chooser ? new ShelfWindowList { Bounds = new Rectangle(6, 6, 208, 108), IntegralHeight = false, ItemHeight = 24 } : null;
                    if (list != null) {
                        for (int index = 0; index < 30; index++) list.Items.Add(Window(index + 1, "neutral:app", "Neutral window " + index));
                        child = list;
                    } else child = new ShelfButton(96) { Location = new Point(8, 7) };
                    popup.Controls.Add(child);
                    active.Show();
                    // A hidden self-test launch can suppress the first ShowWindow call.
                    Native.ShowWindowAsync(active.Handle, 5); Application.DoEvents();
                    popup.Show(); Application.DoEvents();
                    // Exercise child mouse handling on this thread without claiming the
                    // desktop foreground, which can change while the suite runs.
                    active.Activate(); Application.DoEvents();
                    assert(GetActiveWindow() == active.Handle, "Activation fixture is active on the test thread before child input");
                    int callbacks = 0; IntPtr foregroundAtSelection = IntPtr.Zero;
                    Action select = delegate { callbacks++; foregroundAtSelection = GetActiveWindow(); };
                    if (chooser) child.MouseUp += delegate { select(); }; else child.Click += delegate { select(); };
                    var point = new IntPtr((10 << 16) | 10);
                    IntPtr activation = SendMessage(child.Handle, 0x0021, popup.Handle, new IntPtr((0x0201 << 16) | 1));
                    assert(activation == new IntPtr(3), "Child mouse activation keeps the click while refusing foreground activation");
                    SendMessage(child.Handle, 0x0201, new IntPtr(1), point);
                    assert(GetActiveWindow() == active.Handle && !popup.ContainsFocus,
                        "Native mouse-down must not focus the " + (chooser ? "chooser list" : "shelf icon button"));
                    assert(child.Capture, "Nonactivating child still captures the press for reliable release and drag cancellation");
                    if (list != null) assert(list.SelectedIndex == 0, "Nonactivating chooser still selects the pressed native item");
                    SendMessage(child.Handle, 0x0202, IntPtr.Zero, point);
                    assert(callbacks == 1 && foregroundAtSelection == active.Handle && GetActiveWindow() == active.Handle,
                        "Selection callback sees the real active application after child mouse-up");
                    assert(!child.Capture, "Nonactivating child releases capture after the click");
                    if (list != null) {
                        SendMessage(list.Handle, 0x0115, new IntPtr(1), IntPtr.Zero);
                        assert(list.TopIndex > 0 && GetActiveWindow() == active.Handle, "Native chooser scrolling remains usable without foreground activation");
                    }
                    var action = WindowShelfPolicy.LocalAction(false, foregroundAtSelection == active.Handle,
                        chooser ? ShelfSelectionOrigin.Chooser : ShelfSelectionOrigin.DirectIcon);
                    assert(action == (chooser ? ShelfLocalAction.Focus : ShelfLocalAction.Minimize),
                        "Actual child input distinguishes chooser focus from direct-icon minimize");
                }
            }
        }

        internal static void RunRestoreCatalog(Action<bool, string> assert)
        {
            var displays = Native.Displays();
            var target = displays[displays.Count - 1];
            var excluded = new List<AppRule>();
            using (var local = new CatalogFixture { Text = "Neutral local restore fixture", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(target.X + 80, target.Y + 80, 360, 260) }) {
                local.Show(); Application.DoEvents();
                ShelfWindow selected;
                assert(WindowShelfCatalog.TryReadWindow(local.Handle, out selected), "Local catalog fixture is otherwise eligible");
                assert(WindowShelfCatalog.Read(target, excluded).Any(w => w.Handle == local.Handle), "Regression: catalog includes an eligible visible destination window");
                local.WindowState = FormWindowState.Minimized; Application.DoEvents();
                assert(WindowShelfCatalog.Read(target, excluded).Any(w => w.Handle == local.Handle),
                    "Regression: minimizing on the destination keeps the window accessible in its shelf");
                ShelfWindow current;
                assert(WindowShelfCatalog.TryReadSelection(selected, target, excluded, out current), "Selection shares local minimized catalog eligibility");
                excluded.Add(new AppRule { Key = selected.AppKey, Name = "Neutral fixture" });
                assert(!WindowShelfCatalog.TryReadSelection(selected, target, excluded, out current), "New exclusions invalidate a local minimized selection");
                excluded.Clear();
                if (displays.Count > 1) {
                    var source = displays[0];
                    using (var remote = new CatalogFixture { Text = "Neutral remote restore fixture", ShowInTaskbar = true, Opacity = 0,
                        StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(source.X + 80, source.Y + 80, 360, 260) }) {
                        remote.Show(); Application.DoEvents();
                        local.WindowState = FormWindowState.Normal; Application.DoEvents();
                        var windows = WindowShelfCatalog.Read(target, excluded);
                        assert(windows.Any(w => w.Handle == remote.Handle), "Catalog includes an eligible normal window from another display");
                        var own = windows.Where(w => w.Handle == local.Handle || w.Handle == remote.Handle).ToList();
                        var groups = WindowShelfPolicy.Group(own);
                        assert(groups.Count == 1 && groups[0].Count == 2, "Regression: visible local plus remote app windows retain both chooser entries");
                        local.WindowState = FormWindowState.Minimized; Application.DoEvents();
                        own = WindowShelfCatalog.Read(target, excluded).Where(w => w.Handle == local.Handle || w.Handle == remote.Handle).ToList();
                        assert(WindowShelfPolicy.Group(own).Single().Count == 2, "Minimizing the local member does not change its app group membership");
                        remote.WindowState = FormWindowState.Minimized; Application.DoEvents();
                        assert(WindowShelfCatalog.Read(target, excluded).Any(w => w.Handle == remote.Handle), "Catalog still includes minimized remote windows");
                        excluded.Add(new AppRule { Key = selected.AppKey, Name = "Neutral fixture" });
                        assert(!WindowShelfCatalog.Read(target, excluded).Any(w => w.Handle == local.Handle || w.Handle == remote.Handle),
                            "Exclusions omit both local minimized and remote members of an app group");
                        excluded.Clear();
                    }
                }
                local.WindowState = FormWindowState.Maximized; Application.DoEvents();
                assert(WindowShelfCatalog.Read(target, excluded).Any(w => w.Handle == local.Handle), "Catalog retains a visible maximized destination window");
                local.WindowState = FormWindowState.Minimized; Application.DoEvents();
                assert(WindowShelfCatalog.Read(target, excluded).Any(w => w.Handle == local.Handle), "Catalog includes a locally minimized maximized window");
                local.WindowState = FormWindowState.Normal; Application.DoEvents();
                assert(WindowShelfCatalog.Read(target, excluded).Any(w => w.Handle == local.Handle), "Restoring locally keeps the window represented in subsequent catalogs");
                assert(WindowShelfCatalog.TryReadSelection(selected, target, excluded, out current), "A local window restored since reveal remains eligible for focus or minimize");
                assert(!WindowShelfCatalog.TryReadSelection(selected, new Display { Device = "disconnected" }, excluded, out current),
                    "Expanded local eligibility still rejects disconnected destination selections");
                local.Enabled = false;
                assert(!WindowShelfCatalog.TryReadSelection(selected, target, excluded, out current), "Disabled local windows remain protected from shelf actions");
                local.Enabled = true;
                excluded.Add(new AppRule { Key = selected.AppKey, Name = "Neutral fixture" });
                assert(!WindowShelfCatalog.Read(target, excluded).Any(w => w.Handle == local.Handle) &&
                    !WindowShelfCatalog.TryReadSelection(selected, target, excluded, out current), "Visible local windows still honor catalog and selection exclusions");
                local.Close(); Application.DoEvents();
                assert(!WindowShelfCatalog.TryReadSelection(selected, target, excluded, out current), "Closing a local window invalidates its selected identity");
            }
        }

        internal static void RunRendering(Action<bool, string> assert)
        {
            assert(ShelfAppearance.TransparencyValue(null) && ShelfAppearance.TransparencyValue(1), "Missing/default transparency preference permits the ghost shelf");
            assert(!ShelfAppearance.TransparencyValue(0) && !ShelfAppearance.TransparencyValue("1"), "Disabled or malformed transparency preference selects opaque rendering");
            assert(ShelfAppearance.GhostAllowed(false, true, false, false), "Ghost mode requires an eligible shelf surface");
            assert(!ShelfAppearance.GhostAllowed(true, true, false, false), "High contrast always selects an opaque surface");
            assert(!ShelfAppearance.GhostAllowed(false, false, false, false), "Disabled transparency selects an opaque surface");
            assert(!ShelfAppearance.GhostAllowed(false, true, true, false), "Chooser remains opaque");
            assert(!ShelfAppearance.GhostAllowed(false, true, false, true), "Failed layered presentation selects an opaque fallback");
            using (var popup = new ShelfPopup("Neutral paint fixture") { ClientSize = new Size(192, 56) }) {
                using (var clear = popup.Render(true, false)) {
                    assert(clear.GetPixel(96, 28).A == 0 && clear.GetPixel(96, 54).A == 0 && clear.GetPixel(0, 0).A == 0,
                        "Ghost background, border and corners are completely clear");
                }
                using (var opaque = popup.Render(false, false)) assert(opaque.GetPixel(96, 28).ToArgb() == Ui.Surface.ToArgb(), "Failed layered presentation has a fully opaque readable surface");
                using (var contrast = popup.Render(false, true)) assert(contrast.GetPixel(96, 28).ToArgb() == SystemColors.Window.ToArgb(), "High-contrast fallback uses Windows accessibility colors");
                assert(popup.Opacity == 1 && popup.TransparencyKey.IsEmpty && popup.GhostEnabled && !popup.IsHandleCreated, "Ghost rendering does not use overall opacity or a color key");
                var parameters = (CreateParams)typeof(ShelfPopup).GetProperty("CreateParams", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(popup, null);
                assert((parameters.ExStyle & 0x08080080) == 0x08080080 && (parameters.ExStyle & 0x20) == 0,
                    "Layered ghost preserves tool/noactivate without the click-through style");
                popup.Controls.Add(new ShelfButton(96) { Location = new Point(8, 7) });
                popup.Controls.Add(new ShelfButton(96) { Location = new Point(52, 7) });
                using (var composed = popup.Render(true, false)) {
                    assert(composed.GetPixel(8, 7).A == 1 && composed.GetPixel(47, 46).A >= 1 && composed.GetPixel(52, 7).A == 1,
                        "Composited frame preserves complete rectangular button hit coverage");
                    assert(composed.GetPixel(49, 20).A == 0 && composed.GetPixel(4, 20).A == 0 && composed.GetPixel(96, 20).A == 0,
                        "Only hit targets intercept input; inter-button gaps and outer margins stay alpha zero");
                }
            }
            using (var chooser = new ShelfPopup("Neutral chooser", true)) {
                var parameters = (CreateParams)typeof(ShelfPopup).GetProperty("CreateParams", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chooser, null);
                assert(!chooser.GhostEnabled && !chooser.UsesLayeredSurface && (parameters.ExStyle & 0x80000) == 0, "Chooser uses ordinary opaque native controls");
            }
            assert(!ShelfPaint.ValidSelection(IntPtr.Zero), "Null SelectObject results abort presentation before BitBlt");
            assert(!ShelfPaint.ValidSelection(new IntPtr(-1)), "HGDI_ERROR aborts presentation without restoring an invalid object");
            assert(ShelfPaint.ValidSelection(new IntPtr(123)), "Valid previous bitmap handles allow presentation and restoration");
            foreach (int dpi in new[] { 96, 144, 192 }) {
                using (var icon = new Bitmap(8, 8))
                using (var button = new ShelfButton(dpi) { Image = icon }) {
                    using (var graphics = Graphics.FromImage(icon)) graphics.Clear(Color.Black);
                    using (var idle = button.Render(true, false, false, false)) {
                        bool hitCoverage = true;
                        for (int y = 0; y < idle.Height; y++) for (int x = 0; x < idle.Width; x++) hitCoverage &= idle.GetPixel(x, y).A >= 1;
                        assert(hitCoverage && idle.GetPixel(0, 0).A == 1, "Every point in the stable button rectangle catches input at each DPI");
                        assert(idle.GetPixel(idle.Width / 2, idle.Height / 2).ToArgb() == Color.Black.ToArgb(), "Even black icon pixels stay fully opaque at each DPI");
                        assert(idle.GetPixel((idle.Width - icon.Width) / 2, (idle.Height - icon.Height) / 2).ToArgb() == Color.Black.ToArgb(),
                            "Icon edges preserve their original opaque pixels without interpolation fading");
                    }
                    using (var idle = button.Render(true, false, false, false))
                    using (var hover = button.Render(true, false, true, false)) {
                        int changes = 0, maxEffectAlpha = 0; bool belowIcon = true;
                        for (int y = 0; y < hover.Height; y++) for (int x = 0; x < hover.Width; x++) {
                            if (hover.GetPixel(x, y) != idle.GetPixel(x, y)) { changes++; belowIcon &= y >= WindowShelfPolicy.Scale(31, dpi); }
                            if (y >= WindowShelfPolicy.Scale(32, dpi)) maxEffectAlpha = Math.Max(maxEffectAlpha, hover.GetPixel(x, y).A);
                        }
                        assert(changes > 0 && belowIcon && hover.GetPixel(6, 6).A == 1, "Hover pool stays shallow beneath the icon, with no enclosing highlight");
                        assert(maxEffectAlpha < 128, "Concept C hover remains faint rather than becoming a bright reflection");
                    }
                    foreach (int count in new[] { 3, 12, 105 }) {
                        var bounds = ShelfPaint.BadgeBounds(button.ClientSize, dpi, count);
                        assert(bounds.Width == WindowShelfPolicy.Scale(count == 3 ? 14 : count == 12 ? 20 : 26, dpi), "Count badge width follows one digit, two digits and 99+ at each DPI");
                        assert(button.ClientRectangle.Contains(bounds) && bounds.Right == button.Width - WindowShelfPolicy.Scale(1, dpi) && bounds.Bottom == button.Height,
                            "Compact badges stay bottom-right within the unchanged hit target");
                    }
                    button.Count = 3;
                    using (var badge = button.Render(true, false, false, false)) assert(badge.GetPixel(button.Width - WindowShelfPolicy.Scale(6, dpi), button.Height - WindowShelfPolicy.Scale(7, dpi)).A == 255, "Count badge has an opaque legibility surface at each DPI");
                    assert(button.Size == new Size(WindowShelfPolicy.Scale(40, dpi), WindowShelfPolicy.Scale(40, dpi)) && !button.IsHandleCreated, "Painting preserves stable DPI-scaled hit targets without live UI");
                }
            }
            using (var bitmap = ShelfPaint.Canvas(new Size(40, 40), 96)) {
                ShelfPaint.WithPixels(bitmap, delegate { return true; });
                uint before = GetGuiResources(GetCurrentProcess(), 0);
                for (int index = 0; index < 20; index++) {
                    assert(ShelfPaint.WithPixels(bitmap, delegate { return true; }), "DIB presentation success propagates");
                    assert(!ShelfPaint.WithPixels(bitmap, delegate { return false; }), "DIB presentation failure propagates");
                    try { ShelfPaint.WithPixels(bitmap, delegate { throw new InvalidOperationException("Neutral presentation failure"); }); }
                    catch (InvalidOperationException) { }
                }
                assert(GetGuiResources(GetCurrentProcess(), 0) == before, "DIBs and DCs are released after success, failure and exceptions");
            }
            IntPtr ownedHandle;
            using (var popup = new ShelfPopup("Hidden layered lifecycle fixture") { ClientSize = new Size(60, 56) }) {
                ownedHandle = popup.Handle;
                using (var frame = popup.Render(true, false)) assert(ShelfPaint.PresentLayered(ownedHandle, frame, Point.Empty), "UpdateLayeredWindow accepts the owned hidden premultiplied surface");
                assert(!popup.Visible && !Native.IsWindowVisible(ownedHandle), "Native resource fixture never shows or activates its HWND");
            }
            assert(!Native.IsWindow(ownedHandle), "Disposing the hidden layered fixture releases its HWND");
        }

        internal static void RunMotion(Action<bool, string> assert)
        {
            assert(ShelfMotion.RevealMilliseconds == 150 && ShelfMotion.DismissMilliseconds == 100 && ShelfMotion.RisePixels == 5,
                "Shelf motion retains the short reveal/dismiss timings and slight rise");
            assert(ShelfMotion.Allowed(true, true, false) && !ShelfMotion.Allowed(false, true, false) &&
                !ShelfMotion.Allowed(true, false, false) && !ShelfMotion.Allowed(true, true, true),
                "Reduced animation, opaque fallback and high contrast suppress motion");
            var motion = new ShelfMotion();
            motion.Reset(false); motion.Begin(true, 1000, true);
            assert(motion.Value(1000) == 0 && motion.Value(900) == 0 && motion.Active, "Reveal starts invisible without advancing on a reversed timestamp");
            assert(Math.Abs(motion.Value(1075) - .875) < .000001, "Reveal uses elapsed-time cubic ease-out, not tick counts");
            motion.Begin(true, 1075, true);
            assert(motion.Value(1150) == 1 && !motion.Active, "Repeated reveal requests do not restart the animation");
            assert(motion.Value(9000) == 1 && !motion.Active, "Completed reveal stays crisp and idle indefinitely");
            motion.Begin(false, 10000, true);
            assert(Math.Abs(motion.Value(10050) - .125) < .000001 && motion.Dismissing, "Dismiss is a short cubic fade from the current surface");
            motion.Begin(false, 10050, true);
            assert(motion.Value(10100) == 0 && !motion.Active, "Repeated dismissal requests do not postpone completion");
            motion.Reset(true); motion.Begin(false, 20000, true);
            double partial = motion.Value(20030);
            motion.Begin(true, 20030, true);
            assert(motion.Value(20030) == partial && !motion.Dismissing, "Re-entering reverses the current fade without an opacity jump");
            double resumed = motion.Value(20060);
            motion.Begin(false, 20060, true);
            assert(motion.Value(20060) == resumed && motion.Dismissing, "Leaving again continues from the in-flight reveal rather than flashing");
            motion.Reset(true);
            assert(motion.Value(999999) == 1 && !motion.Active && !motion.Dismissing, "Immediate cancellation removes all pending fade state");
            motion.Reset(false); motion.Begin(true, 0, false);
            assert(motion.Value(0) == 1 && !motion.Active, "Reduced-motion reveal is immediately complete");
            motion.Begin(false, 0, false);
            assert(motion.Value(0) == 0 && !motion.Active, "Reduced-motion dismissal is immediate");
            var dense = new ShelfMotion(); var sparse = new ShelfMotion();
            dense.Reset(false); sparse.Reset(false); dense.Begin(true, 0, true); sparse.Begin(true, 0, true);
            for (int elapsed = 0; elapsed < 149; elapsed++) dense.Value(elapsed);
            assert(dense.Value(149) == sparse.Value(149) && dense.Value(150) == sparse.Value(150), "Dropped frames do not slow or change animation completion");

            foreach (int dpi in new[] { 96, 144, 192 }) {
                using (var icon = new Bitmap(WindowShelfPolicy.Scale(24, dpi), WindowShelfPolicy.Scale(24, dpi)))
                using (var button = new ShelfButton(dpi) { Image = icon }) {
                    using (var graphics = Graphics.FromImage(icon)) graphics.Clear(Color.Black);
                    var bounds = button.Bounds;
                    foreach (bool reveal in new[] { true, false }) {
                        var phase = new ShelfMotion(); phase.Reset(!reveal); phase.Begin(reveal, 0, true);
                        int duration = reveal ? ShelfMotion.RevealMilliseconds : ShelfMotion.DismissMilliseconds;
                        for (int elapsed = 0; elapsed <= duration; elapsed++) {
                            double visibility = phase.Value(elapsed);
                            using (var bitmap = button.Render(true, false, false, false)) {
                                ShelfPaint.AnimateButton(bitmap, visibility, WindowShelfPolicy.Scale(ShelfMotion.RisePixels, dpi));
                                bool covered = true;
                                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) covered &= bitmap.GetPixel(x, y).A >= 1;
                                if (!covered) { assert(false, "Animation never erases hit coverage at any elapsed millisecond or DPI"); return; }
                                if (elapsed == 0 || elapsed == duration) assert(bitmap.GetPixel(0, 0).A == 1,
                                    "First and last animation frames retain alpha-floor corner click coverage");
                            }
                        }
                    }
                    assert(button.Bounds == bounds && button.Enabled && !button.IsHandleCreated, "Motion never moves or disables the stable native button target");
                    using (var baseline = button.Render(true, false, false, false))
                    using (var final = button.Render(true, false, false, false)) {
                        ShelfPaint.AnimateButton(final, 1, WindowShelfPolicy.Scale(ShelfMotion.RisePixels, dpi));
                        bool identical = true;
                        for (int y = 0; y < final.Height; y++) for (int x = 0; x < final.Width; x++) identical &= final.GetPixel(x, y) == baseline.GetPixel(x, y);
                        assert(identical && final.GetPixel(final.Width / 2, final.Height / 2).A == 255, "Final artwork is pixel-identical and icons fully opaque at each DPI");
                    }
                    using (var midway = button.Render(true, false, false, false)) {
                        int rise = WindowShelfPolicy.Scale(ShelfMotion.RisePixels, dpi), top = (button.Height - icon.Height) / 2;
                        ShelfPaint.AnimateButton(midway, .5, rise);
                        assert(midway.GetPixel(button.Width / 2, top).A == 1 &&
                            midway.GetPixel(button.Width / 2, top + (int)Math.Round(rise * .5)).A == 128,
                            "Intermediate artwork has the slight DPI-scaled rise inside its fixed hit area");
                    }
                }
            }
            using (var popup = new ShelfPopup("Neutral motion paint fixture") { ClientSize = new Size(104, 56) }) {
                popup.Controls.Add(new ShelfButton(96) { Location = new Point(8, 7) });
                popup.Controls.Add(new ShelfButton(96) { Location = new Point(52, 7), Count = 3 });
                foreach (double amount in new[] { 0, .001, .125, .5, .875, 1 }) {
                    using (var frame = popup.Render(true, false, amount)) {
                        assert(frame.GetPixel(8, 7).A == 1 && frame.GetPixel(47, 46).A >= 1 && frame.GetPixel(52, 7).A == 1,
                            "Composited animation preserves alpha-floor corners for icons and grouped buttons");
                        assert(frame.GetPixel(49, 20).A == 0 && frame.GetPixel(4, 20).A == 0,
                            "Animated shelves keep inter-button gaps and outer margins fully clear");
                    }
                    using (var frame = popup.Render(false, true, amount)) assert(frame.GetPixel(4, 20).A == 255,
                        "High-contrast fallback never inherits transient visual opacity");
                }
                assert(!popup.AnimationRunning && !popup.Visible && !popup.IsHandleCreated, "Deterministic motion painting starts no timer or HWND");
            }
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            using (var popup = new ShelfPopup("Hidden motion lifecycle fixture")) {
                IntPtr handle = popup.Handle;
                var timer = (Timer)typeof(ShelfPopup).GetField("motionTimer", flags).GetValue(popup);
                var state = (ShelfMotion)typeof(ShelfPopup).GetField("motion", flags).GetValue(popup);
                typeof(ShelfPopup).GetField("clientAnimation", flags).SetValue(popup, true);
                typeof(ShelfPopup).GetField("transparency", flags).SetValue(popup, true);
                typeof(ShelfPopup).GetProperty("HighContrast", flags).SetValue(popup, false, null);
                typeof(ShelfPopup).GetMethod("PrepareReveal", flags).Invoke(popup, null);
                assert(state.Active && !state.Dismissing && !popup.AnimationRunning && !Native.IsWindowVisible(handle),
                    "SetVisibleCore preparation primes the first reveal frame before visibility without starting a hidden timer");
                int generation = (int)typeof(ShelfPopup).GetField("frameGeneration", flags).GetValue(popup);
                typeof(ShelfPopup).GetMethod("SetVisibleCore", flags).Invoke(popup, new object[] { false });
                popup.ScheduleFrame();
                assert(!state.Active && !popup.AnimationRunning && !(bool)typeof(ShelfPopup).GetField("scheduled", flags).GetValue(popup) &&
                    (int)typeof(ShelfPopup).GetField("frameGeneration", flags).GetValue(popup) > generation,
                    "Immediate hide invalidates old frame generations and refuses new hidden paint callbacks");
                int completed = 0;
                popup.DismissAnimated(delegate { completed++; }, delegate { return false; });
                assert(completed == 1 && !popup.AnimationRunning && !popup.Visible, "A hidden popup completes dismissal without starting a transient timer");
                state.Reset(false); state.Begin(true, 0, true); timer.Start();
                typeof(ShelfPopup).GetMethod("AnimateFrame", flags).Invoke(popup, null);
                assert(!popup.AnimationRunning && !state.Active, "An already-queued animation tick stops immediately on a hidden popup");
                state.Begin(false, 0, true); timer.Start();
                typeof(ShelfPopup).GetField("dismissed", flags).SetValue(popup, (Action)delegate { completed++; });
                popup.Dispose();
                assert(completed == 1 && !timer.Enabled && !Native.IsWindow(handle), "Disposal cancels the exit callback and releases the transient timer and owned HWND");
                Application.DoEvents();
                assert(completed == 1, "Queued work cannot finish dismissal again after disposal");
            }
            using (var popup = new ShelfPopup("Hidden native fallback fixture")) {
                IntPtr handle = popup.Handle;
                var state = (ShelfMotion)typeof(ShelfPopup).GetField("motion", flags).GetValue(popup);
                var timer = (Timer)typeof(ShelfPopup).GetField("motionTimer", flags).GetValue(popup);
                state.Reset(false); state.Begin(true, 0, true); timer.Start();
                typeof(ShelfPopup).GetMethod("UseOpaqueFallback", flags).Invoke(popup, null);
                assert(!popup.AnimationRunning && !state.Active && !popup.AnimationEnabled && !Native.IsWindowVisible(handle),
                    "Native fallback immediately stops reveal and remains opaque without showing a hidden HWND");
            }
            using (var popup = new ShelfPopup("Hidden exit fallback fixture")) {
                IntPtr handle = popup.Handle;
                var state = (ShelfMotion)typeof(ShelfPopup).GetField("motion", flags).GetValue(popup);
                var timer = (Timer)typeof(ShelfPopup).GetField("motionTimer", flags).GetValue(popup);
                int completed = 0;
                state.Begin(false, 0, true); timer.Start();
                typeof(ShelfPopup).GetField("dismissed", flags).SetValue(popup, (Action)delegate { completed++; });
                typeof(ShelfPopup).GetMethod("UseOpaqueFallback", flags).Invoke(popup, null);
                assert(completed == 1 && !popup.AnimationRunning && !Native.IsWindowVisible(handle),
                    "Native failure during exit completes dismissal once instead of leaving a solid clickable row");
            }
            // Earlier native-control fixtures may leave finalizers pending; drain them
            // on both sides so unrelated collection cannot change the handle baseline.
            Application.DoEvents(); GC.Collect(); GC.WaitForPendingFinalizers(); Application.DoEvents();
            uint gdi = GetGuiResources(GetCurrentProcess(), 0), user = GetGuiResources(GetCurrentProcess(), 1);
            for (int index = 0; index < 20; index++) using (var popup = new ShelfPopup("Hidden timer disposal fixture")) {
                IntPtr handle = popup.Handle;
                var timer = (Timer)typeof(ShelfPopup).GetField("motionTimer", flags).GetValue(popup);
                timer.Start();
            }
            Application.DoEvents(); GC.Collect(); GC.WaitForPendingFinalizers(); Application.DoEvents();
            assert(GetGuiResources(GetCurrentProcess(), 0) == gdi && GetGuiResources(GetCurrentProcess(), 1) == user,
                "Repeated popup disposal releases transient timer HWNDs, popup HWNDs and GDI resources");
        }

        internal static void RunSensitivity(Action<bool, string> assert)
        {
            var monitor = DisplayAt(-1920, -1080, 1920, 1080);
            assert(WindowShelfPolicy.Trigger(monitor) == WindowShelfPolicy.Trigger(monitor, 480) &&
                WindowShelfPolicy.DefaultHoverDelayMs == 100, "Default sensitivity uses the existing 480px edge and 100ms dwell");
            assert(WindowShelfPolicy.MinActivationHeight == 1 && WindowShelfPolicy.MaxActivationHeight == 96 &&
                WindowShelfPolicy.DefaultActivationHeight == 6 &&
                WindowShelfPolicy.Trigger(monitor, 480) == WindowShelfPolicy.Trigger(monitor, 480, 6),
                "The default trigger uses six physical pixels while configurable height permits every value from 1px to 96px");
            foreach (int displayWidth in new[] { 50, 160, 320, 1920, 7680 }) {
                var display = DisplayAt(-displayWidth, 120, displayWidth, 600);
                foreach (int requested in new[] { Int32.MinValue, 80, 360, 480, 960, Int32.MaxValue }) {
                    int expected = Math.Min(displayWidth, Math.Max(80, Math.Min(960, requested)));
                    Rectangle trigger = WindowShelfPolicy.Trigger(display, requested);
                    assert(trigger == new Rectangle(display.X + (display.Width - expected) / 2, 714, expected, 6) &&
                        WindowShelfPolicy.Bounds(display).Contains(trigger), "Custom activation widths stay centered in physical pixels and fit the display");
                    assert(trigger.Contains(trigger.Left, trigger.Top) && trigger.Contains(trigger.Right - 1, trigger.Bottom - 1) &&
                        !trigger.Contains(trigger.Right, trigger.Bottom - 1) && !trigger.Contains(trigger.Left, trigger.Top - 1),
                        "Custom trigger includes its first and last pixels but excludes the next pixel and content above it");
                }
            }
            foreach (int displayHeight in new[] { 1, 5, 6, 24, 80, 160, 1080 }) {
                var upper = DisplayAt(-1920, -displayHeight, 1920, displayHeight);
                var lower = DisplayAt(-1920, 0, 1920, 1080);
                Rectangle defaultStrip = WindowShelfPolicy.Trigger(upper, 360);
                foreach (int requested in new[] { Int32.MinValue, -1, 0, 1, 2, 3, 4, 5, 6, 12, 24, 48, 96, 97, Int32.MaxValue }) {
                    int expected = Math.Min(displayHeight, Math.Max(1, Math.Min(96, requested)));
                    Rectangle strip = WindowShelfPolicy.Trigger(upper, 360, requested);
                    assert(strip == new Rectangle(-1140, -expected, 360, expected) && strip.X == defaultStrip.X &&
                        strip.Width == defaultStrip.Width && WindowShelfPolicy.Bounds(upper).Contains(strip),
                        "Custom physical height is bottom-aligned, bounded to the monitor and independent of activation width");
                    int center = strip.Left + strip.Width / 2;
                    assert(strip.Contains(center, strip.Top) && strip.Contains(center, strip.Bottom - 1) &&
                        !strip.Contains(center, strip.Top - 1) && !strip.Contains(center, strip.Bottom) &&
                        !strip.IntersectsWith(WindowShelfPolicy.Bounds(lower)),
                        "Configured height includes its first and last pixels without spilling across a stacked-monitor seam");
                }
            }
            foreach (int requested in new[] { Int32.MinValue, 100, 275, 400, 1500, Int32.MaxValue }) {
                int delay = Math.Max(100, Math.Min(1500, requested));
                assert(!WindowShelfPolicy.DwellReady(1000, 1000 + delay - 1, true, true, requested) &&
                    WindowShelfPolicy.DwellReady(1000, 1000 + delay, true, true, requested), "Custom dwell opens exactly at its bounded delay");
                assert(!WindowShelfPolicy.DwellReady(1000, 999, true, true, requested) &&
                    !WindowShelfPolicy.DwellReady(1000, 3000, false, true, requested) &&
                    !WindowShelfPolicy.DwellReady(1000, 3000, true, false, requested), "Custom dwell retains clock, input and hit-area guards");
            }
            var activation = new ShelfActivationDwell();
            foreach (int delay in new[] { 100, 350, 1500 }) {
                activation.Configure(360, delay); activation.Reset();
                for (int cycle = 0; cycle < 5; cycle++) {
                    long start = 10000 + delay * 10L * cycle;
                    assert(!activation.Update(start, true, true) && !activation.Update(start + delay - 1, true, true),
                        "Repeated deliberate hover starts a fresh complete dwell");
                    assert(!activation.Update(start + delay, true, false) && !activation.Update(start + delay + 1, true, true) &&
                        !activation.Update(start + 2 * delay, true, true) && activation.Update(start + 2 * delay + 1, true, true),
                        "Leaving and reentering cannot reuse elapsed hover time");
                    assert(!activation.Update(start + 2 * delay + 2, false, true) && !activation.Update(start + 2 * delay + 3, true, true) &&
                        activation.Update(start + 3 * delay + 3, true, true), "Held input cancels hover and release requires another full dwell");
                    activation.Reset();
                }
            }
            activation.Configure(480, 400); activation.Update(100, true, true);
            assert(activation.Configure(480, 100) && !activation.Update(499, true, true) &&
                !activation.Update(598, true, true) && activation.Update(599, true, true), "Reducing delay during hover does not reveal immediately from old elapsed time");
            assert(activation.Configure(960, 100) && !activation.Update(600, true, true) && activation.Update(700, true, true),
                "Changing width alone also starts a fresh dwell");
            assert(!activation.Configure(Int32.MaxValue, Int32.MinValue) && activation.Update(701, true, true),
                "Equivalent bounded sensitivity values do not reset an established dwell");
            assert(!activation.Update(500, true, true) && !activation.Update(599, true, true) && activation.Update(600, true, true),
                "A reversed observation clock restarts dwell instead of retaining a ready state");
            assert(activation.Configure(360, 48, 350) && activation.Height == 48 && activation.Width == 360 && activation.DelayMs == 350 &&
                !activation.Update(2000, true, true) && !activation.Update(2349, true, true) && activation.Update(2350, true, true),
                "Custom height is an independent part of a complete sensitivity configuration");
            assert(activation.Configure(360, 1, 350) && !activation.Update(2351, true, true) &&
                !activation.Update(2700, true, true) && activation.Update(2701, true, true),
                "Reducing height alone cannot reuse the ready dwell from a larger activation area");
            assert(!activation.Configure(360, Int32.MinValue, 350) && activation.Update(2702, true, true),
                "An equivalent bounded height preserves established dwell instead of resetting it repeatedly");
            assert(activation.Configure(360, 350) && activation.Height == 6 && !activation.Update(2800, true, true),
                "The compatibility configure overload deliberately returns to the current default height");

            var settings = Settings.CreateFresh();
            using (var manager = new WindowShelfManager(delegate { return new List<Display>(); }, delegate { return new List<AppRule>(); },
                delegate { throw new InvalidOperationException("Sensitivity tests must not move windows"); }, null, delegate { return settings; })) {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                Type type = typeof(WindowShelfManager);
                var state = (ShelfActivationDwell)type.GetField("activation", flags).GetValue(manager);
                assert(!manager.RefreshSensitivity(), "A live provider with default values preserves existing manager state");
                foreach (string dimension in new[] { "width", "height", "delay", "height", "width", "delay" }) {
                    int generation = (int)type.GetField("generation", flags).GetValue(manager);
                    var request = new ShelfRequest(generation);
                    type.GetField("pending", flags).SetValue(manager, request);
                    type.GetField("target", flags).SetValue(manager, monitor);
                    type.GetField("latched", flags).SetValue(manager, true);
                    state.Update(100, true, true);
                    if (dimension == "width") settings.WindowShelfActivationWidth = settings.WindowShelfActivationWidth == 480 ? 960 : 480;
                    else if (dimension == "height") settings.WindowShelfActivationHeight = settings.WindowShelfActivationHeight == 24 ? 96 : 24;
                    else settings.WindowShelfHoverDelayMs = settings.WindowShelfHoverDelayMs == 400 ? 100 : 400;
                    assert(manager.RefreshSensitivity() && request.Cancelled && type.GetField("pending", flags).GetValue(manager) == null &&
                        type.GetField("target", flags).GetValue(manager) == null && !(bool)type.GetField("latched", flags).GetValue(manager),
                        "Each live " + dimension + " change cancels the pending catalog, clears target and releases the old Escape latch");
                    var late = new ShelfRequest.Result(); request.Complete(late);
                    assert(request.Take() == null && !WindowShelfPolicy.AcceptResult(generation, (int)type.GetField("generation", flags).GetValue(manager), false, true),
                        "A result from before the setting change cannot reopen the shelf");
                    assert(!state.Update(1000, true, true) && !manager.RefreshSensitivity() &&
                        !state.Update(1000 + state.DelayMs - 1, true, true) && state.Update(1000 + state.DelayMs, true, true),
                        "Settings changes require a fresh dwell while unchanged refreshes preserve its progress");
                }
                settings = new Settings { WindowShelfActivationWidth = 360, WindowShelfActivationHeight = 48, WindowShelfHoverDelayMs = 750 };
                assert(manager.RefreshSensitivity() && state.Width == 360 && state.Height == 48 && state.DelayMs == 750,
                    "Replacing the settings object applies all three live sensitivity values");
                settings = null;
                assert(manager.RefreshSensitivity() && state.Width == 480 && state.Height == 6 && state.DelayMs == 100,
                    "A temporarily missing live settings object returns to safe default sensitivity");
            }
        }

        internal static void RunStackedActivation(Action<bool, string> assert)
        {
            // Synthetic desktop layouts exercise shared seams without changing
            // Windows' monitor topology or sending pointer/keyboard input.
            var layouts = new[] {
                new[] { DisplayAt(0, -1080, 1920, 1080), DisplayAt(0, 0, 1920, 1080) },
                new[] { DisplayAt(-3840, -2200, 3840, 2160), DisplayAt(-1920, -40, 1920, 1080) },
                new[] { DisplayAt(2560, -1080, 1920, 1080), DisplayAt(3000, 0, 1920, 1080) },
                new[] { DisplayAt(-80, -160, 160, 160), DisplayAt(-960, 0, 1920, 1080) },
                new[] { DisplayAt(-500, -1200, 1920, 1080), DisplayAt(-500, -60, 1920, 1080) },
                new[] { DisplayAt(-1920, 120, 1920, 1080), DisplayAt(0, 120, 1920, 1080) },
                new[] { DisplayAt(0, 1080, 1920, 1080), DisplayAt(0, 0, 1920, 1080) }
            };
            for (int layoutIndex = 0; layoutIndex < layouts.Length; layoutIndex++) {
                var layout = layouts[layoutIndex];
                Display display = layout[0], neighbor = layout[1];
                foreach (int width in new[] { 80, 480, 960 }) {
                    Rectangle strip = WindowShelfPolicy.Trigger(display, width, 24);
                    Point hold = new Point(strip.Left + strip.Width / 2, strip.Bottom - 12);
                    var oldStrip = new Rectangle(strip.X, strip.Bottom - 6, strip.Width, 6);
                    assert(strip.Contains(hold) && !oldStrip.Contains(hold),
                        "An explicitly enlarged custom zone still supports a hold twelve pixels inside an upper/shared edge");
                    assert(strip.Bottom == display.Y + display.Height && strip.Height == 24 &&
                        !strip.Contains(new Point(hold.X, strip.Bottom)) && !strip.IntersectsWith(WindowShelfPolicy.Bounds(neighbor)),
                        "Activation stays wholly inside its own bottom edge, including stacked, offset, gapped and side-by-side layouts");
                    assert(!strip.Contains(strip.Left - 1, hold.Y) && !strip.Contains(strip.Right, hold.Y) &&
                        !strip.Contains(hold.X, strip.Top - 1),
                        "The reachable zone still excludes neighboring content outside the configured bottom-center width");
                    foreach (int delay in new[] { 100, 350, 1500 }) {
                        var dwell = new ShelfActivationDwell(); dwell.Configure(width, 24, delay);
                        assert(!dwell.Update(1000, true, strip.Contains(hold)), "A shared-edge hold begins a fresh dwell");
                        for (int elapsed = 50; elapsed < delay; elapsed += 50)
                            assert(!dwell.Update(1000 + elapsed, true, strip.Contains(new Point(hold.X, strip.Bottom - 18))),
                                "Small pointer adjustments within the reachable band cannot open before the selected hover delay");
                        assert(dwell.Update(1000 + delay, true, strip.Contains(hold)),
                            "A deliberate shared-edge hold opens at its selected delay without relying on a hard desktop edge");
                        dwell.Reset();
                        assert(!dwell.Update(2000, true, strip.Contains(new Point(hold.X, strip.Bottom - 1))) &&
                            !dwell.Update(2050, true, strip.Contains(new Point(hold.X, strip.Bottom + 1))) &&
                            !dwell.Update(3000, true, strip.Contains(hold)) &&
                            !dwell.Update(3000 + delay - 1, true, strip.Contains(hold)) &&
                            dwell.Update(3000 + delay, true, strip.Contains(hold)),
                            "Passing through the seam cancels hover; returning requires a complete new dwell");
                        assert(!dwell.Update(5000, false, strip.Contains(hold)) &&
                            !dwell.Update(6000, true, strip.Contains(hold)) && dwell.Update(6000 + delay, true, strip.Contains(hold)),
                            "Held buttons and modifiers still suppress shared-edge activation until release plus a fresh dwell");
                    }
                }
                foreach (int requestedDpi in new[] { 96, 144, 192, 384 }) {
                    int effectiveDpi = Math.Max(96, Math.Min(requestedDpi, Math.Min(display.Width, display.Height) * 96 / 160));
                    var preview = ShelfActivationPreviewLayout.Create(display, 480, requestedDpi);
                    assert(preview.Area == WindowShelfPolicy.Trigger(display, 480) && WindowShelfPolicy.Bounds(display).Contains(preview.Bounds),
                        "Mixed-DPI preview retains the same physical activation pixels and fits minimum-size and stacked displays");
                    int capacity = WindowShelfPolicy.Capacity(display.Width, effectiveDpi);
                    Rectangle row = WindowShelfPolicy.Fit(display, WindowShelfPolicy.Scale(16, effectiveDpi) +
                        capacity * WindowShelfPolicy.Scale(44, effectiveDpi) + WindowShelfPolicy.Scale(88, effectiveDpi),
                        WindowShelfPolicy.Scale(56, effectiveDpi), display.Y + display.Height);
                    var previews = ShelfPreviewLayout.Create(display, effectiveDpi, row.Top - WindowShelfPolicy.Scale(6, effectiveDpi), 7);
                    assert(WindowShelfPolicy.Bounds(display).Contains(row) && WindowShelfPolicy.Bounds(display).Contains(previews.Bounds) &&
                        previews.Bounds.Bottom <= row.Top && row.Bottom == display.Y + display.Height,
                        "Ghost Bar and grouped previews stay on their intended stacked monitor at mixed DPI and minimum supported size");
                    // Hidden Form bounds otherwise inherit the real desktop's
                    // MaxWindowTrackSize. A synthetic high-DPI row can be wider
                    // than a hosted runner's one monitor, despite fitting its
                    // synthetic display. Give only this owned paint fixture an
                    // explicit viewport; retain the exact size/pixel checks.
                    using (var popup = new ShelfPopup("Neutral stacked Ghost Bar fixture") { MaximumSize = row.Size, Bounds = row, ClientSize = row.Size }) {
                        var button = new ShelfButton(effectiveDpi) { Location = new Point(WindowShelfPolicy.Scale(8, effectiveDpi), WindowShelfPolicy.Scale(7, effectiveDpi)) };
                        popup.Controls.Add(button);
                        using (var frame = popup.Render(true, false)) {
                            int alpha = frame.GetPixel(button.Left + 1, button.Top + 1).A;
                            assert(frame.Size == row.Size && popup.ClientRectangle.Contains(button.Bounds) &&
                                alpha > 0 && !popup.Visible && !popup.IsHandleCreated,
                                "Owned stacked Ghost Bar UI renders a complete selectable icon without showing, focusing or moving any real application" +
                                "; layout=" + layoutIndex + ", requestedDpi=" + requestedDpi + ", effectiveDpi=" + effectiveDpi +
                                ", row=" + row + ", popup=" + popup.Bounds + ", client=" + popup.ClientRectangle +
                                ", frame=" + frame.Size + ", button=" + button.Bounds + ", alpha=" + alpha +
                                ", Visible=" + popup.Visible + ", IsHandleCreated=" + popup.IsHandleCreated +
                                ", hostMaxTrack=" + SystemInformation.MaxWindowTrackSize);
                        }
                    }
                    // Paint the real activation-preview control while hidden;
                    // Present would deliberately show its topmost overlay.
                    using (var surface = new ShelfActivationPreviewSurface { MaximumSize = preview.Bounds.Size, Bounds = preview.Bounds, ClientSize = preview.Bounds.Size }) {
                        typeof(ShelfActivationPreviewSurface).GetField("layout", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(surface, preview);
                        using (var frame = new Bitmap(surface.Width, surface.Height)) {
                            surface.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
                            Color ink = frame.GetPixel(preview.Area.Left - preview.Bounds.Left + preview.Area.Width / 2,
                                preview.Area.Top - preview.Bounds.Top + preview.Area.Height / 2);
                            assert(ink.R == 65 && ink.G == 220 && ink.B == 255 && !surface.Visible,
                                "The owned preview control paints the expanded reachable zone while staying hidden and passing input through");
                        }
                    }
                }
            }
            var available = new List<Display> { layouts[0][0] };
            using (var manager = new WindowShelfManager(delegate { return available; }, delegate { return new List<AppRule>(); },
                delegate { throw new InvalidOperationException("Stacked lifecycle checks must not move applications"); })) {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                Type type = typeof(WindowShelfManager);
                var timer = (Timer)type.GetField("timer", flags).GetValue(manager);
                var dwell = (ShelfActivationDwell)type.GetField("activation", flags).GetValue(manager);
                assert(timer.Interval == 50 && timer.Interval <= WindowShelfPolicy.DefaultHoverDelayMs / 2,
                    "Polling can observe the default dwell across multiple samples inside the shared-edge band");
                foreach (string interruption in new[] { "pause", "disconnect", "geometry change" }) {
                    manager.Refresh();
                    var request = new ShelfRequest((int)type.GetField("generation", flags).GetValue(manager));
                    type.GetField("pending", flags).SetValue(manager, request);
                    type.GetField("target", flags).SetValue(manager, available[0]);
                    assert(!dwell.Update(100, true, true) && dwell.Update(200, true, true), "Lifecycle fixture begins with ready shared-edge hover");
                    if (interruption == "geometry change") {
                        available = new List<Display> { DisplayAt(100, -1080, 1920, 1080) };
                    } else available = new List<Display>();
                    manager.Refresh();
                    assert(request.Cancelled && type.GetField("pending", flags).GetValue(manager) == null &&
                        type.GetField("target", flags).GetValue(manager) == null && !dwell.Update(300, true, true),
                        "Pause, disconnect and topology changes cancel in-progress catalog work and old shared-edge dwell: " + interruption);
                    request.Complete(new ShelfRequest.Result());
                    assert(request.Take() == null && (interruption == "geometry change" || !timer.Enabled),
                        "Late catalog data cannot reopen a Ghost Bar after interruption: " + interruption);
                    available = new List<Display> { layouts[0][0] }; manager.Refresh();
                    assert(!dwell.Update(1000, true, true) && !dwell.Update(1099, true, true) && dwell.Update(1100, true, true),
                        "Resuming or reconnecting requires a complete new deliberate hover: " + interruption);
                    dwell.Reset();
                }
            }
        }

        internal static void Run(Action<bool, string> assert)
        {
            RunSensitivity(assert);
            RunStackedActivation(assert);
            RunActivation(assert);
            RunRendering(assert);
            RunMotion(assert);
            RunRestoreCatalog(assert);
            assert(WindowShelfCatalog.PreferCleanAppIcon(@"C:\Apps\Chrome\chrome.exe") &&
                WindowShelfCatalog.PreferCleanAppIcon(@"C:\Apps\CHROME.EXE"), "Chrome desktop groups prefer the plain application icon");
            assert(!WindowShelfCatalog.PreferCleanAppIcon(@"C:\Apps\chrome_proxy.exe") &&
                !WindowShelfCatalog.PreferCleanAppIcon(@"C:\Apps\other.exe"), "Other applications and Chrome web-app proxies retain window icons");
            assert(!WindowShelfCatalog.PreferCleanAppIcon(null) && !WindowShelfCatalog.PreferCleanAppIcon("chrome.exe") &&
                !WindowShelfCatalog.PreferCleanAppIcon(@"\\example.invalid\share\chrome.exe"), "Clean icon selection rejects missing, relative and UNC paths");
            string iconDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TaskbarSelect-icon-" + Guid.NewGuid().ToString("N"));
            string iconPath = System.IO.Path.Combine(iconDirectory, "chrome.exe");
            System.IO.Directory.CreateDirectory(iconDirectory);
            try {
                assert(WindowShelfCatalog.ReadCleanAppIcon(iconPath, 24) == null, "Missing executable clean icon falls back without failing the shelf");
                System.IO.File.Copy(Assembly.GetExecutingAssembly().Location, iconPath);
                using (var source = Icon.ExtractAssociatedIcon(iconPath))
                using (var bitmap = source.ToBitmap())
                using (var expected = new Bitmap(bitmap, 24, 24))
                using (var actual = WindowShelfCatalog.ReadCleanAppIcon(iconPath, 24)) {
                    bool equal = actual != null && actual.Size == expected.Size;
                    for (int y = 0; equal && y < 24; y++) for (int x = 0; x < 24; x++)
                        if (actual.GetPixel(x, y) != expected.GetPixel(x, y)) { equal = false; break; }
                    assert(equal, "Clean icon pixels come from the executable without a per-window profile overlay");
                }
                assert(WindowShelfCatalog.ReadCleanAppIcon(iconPath, 0) == null, "Invalid clean icon dimensions fail safely");
            } finally {
                if (System.IO.File.Exists(iconPath)) System.IO.File.Delete(iconPath);
                System.IO.Directory.Delete(iconDirectory);
            }
            assert(WindowShelfPolicy.CanListOnTarget("display-a", "DISPLAY-A"), "Known local displays allow both visible and minimized eligible windows");
            assert(WindowShelfPolicy.CanListOnTarget("display-a", "display-b"), "Known remote displays keep their existing eligible windows");
            assert(!WindowShelfPolicy.CanListOnTarget(null, "display-b") && !WindowShelfPolicy.CanListOnTarget("display-a", " "),
                "Unknown source and destination devices still fail closed");
            assert(WindowShelfPolicy.LocalAction(false, true, ShelfSelectionOrigin.DirectIcon) == ShelfLocalAction.Minimize,
                "A direct icon minimizes its visible foreground local window");
            foreach (var origin in new[] { ShelfSelectionOrigin.DirectIcon, ShelfSelectionOrigin.Chooser }) {
                assert(WindowShelfPolicy.LocalAction(false, false, origin) == ShelfLocalAction.Focus, "Other visible local selections focus without minimizing");
                assert(WindowShelfPolicy.LocalAction(true, false, origin) == ShelfLocalAction.Restore && WindowShelfPolicy.LocalAction(true, true, origin) == ShelfLocalAction.Restore,
                    "Minimized local selections restore from either origin regardless of foreground");
            }
            assert(WindowShelfPolicy.LocalAction(false, true, ShelfSelectionOrigin.Chooser) == ShelfLocalAction.Focus,
                "Chooser selections never minimize even when the local window was foreground at click time");
            assert(WindowShelfPolicy.LocalAction(false, true, (ShelfSelectionOrigin)99) == ShelfLocalAction.Focus,
                "Only an explicit direct-icon origin permits minimizing");
            long stamp = System.Diagnostics.Stopwatch.Frequency * 10;
            assert(WindowShelfPolicy.FreshSelection(stamp, stamp) && WindowShelfPolicy.FreshSelection(stamp, stamp + System.Diagnostics.Stopwatch.Frequency),
                "Foreground context is valid only within the bounded click dispatch interval");
            assert(!WindowShelfPolicy.FreshSelection(0, stamp) && !WindowShelfPolicy.FreshSelection(stamp, stamp - 1) &&
                !WindowShelfPolicy.FreshSelection(stamp, stamp + System.Diagnostics.Stopwatch.Frequency + 1), "Missing, future and expired click snapshots cannot drive a local toggle");
            var monitor = DisplayAt(-1920, -1080, 1920, 1080);
            var trigger = WindowShelfPolicy.Trigger(monitor);
            assert(trigger == new Rectangle(-1200, -6, 480, 6), "Shelf trigger covers a centered strip of six physical edge pixels, including negative coordinates");
            assert(trigger.Contains(-960, -1) && !trigger.Contains(-960, 0) && !trigger.Contains(-1800, -1), "Shelf trigger does not cover the whole edge or the adjacent monitor");
            assert(trigger.Contains(-1160, -5) && trigger.Contains(-760, -5) && !trigger.Contains(-1400, -5) && !trigger.Contains(-960, -7),
                "Shelf reveal accepts approaches well off center without covering ordinary content above the edge");
            foreach (int width in new[] { 160, 320, 800, 1920, 3840, 7680 }) {
                var display = DisplayAt(-width, 120, width, 600);
                var edge = WindowShelfPolicy.Trigger(display);
                assert(WindowShelfPolicy.Bounds(display).Contains(edge) && edge.Bottom == display.Y + display.Height &&
                    edge.Width == Math.Min(480, width),
                    "Moderate edge trigger remains centered and inside narrow and ultrawide displays");
            }
            assert(WindowShelfPolicy.DwellReady(100, 200, true, true), "Shelf deliberate dwell opens after 100 ms");
            assert(!WindowShelfPolicy.DwellReady(100, 199, true, true), "Shelf cannot open before its dwell");
            assert(!WindowShelfPolicy.DwellReady(100, 50, true, true), "Shelf dwell rejects a reversed clock");
            assert(!WindowShelfPolicy.DwellReady(100, 600, false, true), "Shelf dwell rejects held buttons and modifiers");
            assert(!WindowShelfPolicy.DwellReady(100, 600, true, false), "Leaving the edge invalidates shelf dwell");
            assert(!WindowShelfPolicy.HideReady(-1, 1000), "Inside shelf or chooser does not arm dismissal");
            assert(!WindowShelfPolicy.HideReady(100, 499) && WindowShelfPolicy.HideReady(100, 500), "Shelf hide delay allows crossing into the chooser");
            assert(!WindowShelfPolicy.HideReady(100, 50), "Hide delay rejects a reversed clock");
            var chooserHover = new ShelfChooserHover();
            assert(!chooserHover.ShouldDismiss(false, false, 100) && !chooserHover.ShouldDismiss(false, false, 349) &&
                chooserHover.ShouldDismiss(false, false, 350), "Leaving an app's preview area gets a separate 250ms grace period");
            assert(!chooserHover.ShouldDismiss(true, false, 400) && !chooserHover.ShouldDismiss(false, false, 800) &&
                !chooserHover.ShouldDismiss(false, false, 1049) && chooserHover.ShouldDismiss(false, false, 1050),
                "Reentry cancels the old deadline so a later exit receives the full grace period");
            assert(!chooserHover.ShouldDismiss(false, true, 1100) && !chooserHover.ShouldDismiss(false, false, 1500) &&
                chooserHover.ShouldDismiss(false, false, 1750), "A captured preview press is retained until release plus a fresh grace period");
            chooserHover.Reset();
            assert(!chooserHover.ShouldDismiss(false, false, 2000) && !chooserHover.ShouldDismiss(false, false, 1900) &&
                !chooserHover.ShouldDismiss(false, false, 2149) && chooserHover.ShouldDismiss(false, false, 2150),
                "A reversed clock resets the preview deadline instead of hiding early");
            foreach (int scale in new[] { 1, 2, 3 }) {
                var icon = new Rectangle(-500 * scale, 200 * scale, 40 * scale, 40 * scale);
                var previewArea = new Rectangle(-700 * scale, -50 * scale, 488 * scale, 230 * scale);
                assert(ShelfChooserHover.Contains(new Point(-480 * scale, 220 * scale), icon, previewArea, 4 * scale),
                    "The originating icon retains its own preview group");
                assert(ShelfChooserHover.Contains(new Point(-680 * scale, 50 * scale), icon, previewArea, 4 * scale),
                    "The full chooser including its title and paging area retains its group");
                assert(ShelfChooserHover.Contains(new Point(-480 * scale, 190 * scale), icon, previewArea, 4 * scale),
                    "A narrow bridge lets the pointer cross from icon into previews");
                assert(!ShelfChooserHover.Contains(new Point(-436 * scale, 220 * scale), icon, previewArea, 4 * scale),
                    "A neighboring shelf icon no longer keeps the old group open");
                assert(!ShelfChooserHover.Contains(new Point(-620 * scale, 190 * scale), icon, previewArea, 4 * scale),
                    "The bridge does not turn the blank strip across the whole shelf into a keep-open area");
                assert(!ShelfChooserHover.Contains(new Point(-710 * scale, 20 * scale), icon, previewArea, 4 * scale),
                    "Leaving both icon and preview boundaries arms dismissal on negative-coordinate displays");
            }
            var dragThreshold = TaskbarInput.DirectDragThreshold(new Size(4, 4), 96);
            assert(!WindowShelfPolicy.Dragged(Point.Empty, new Point(5, 5), dragThreshold), "Shelf clicks tolerate ordinary hand movement beyond the tiny native drag box");
            assert(WindowShelfPolicy.Dragged(Point.Empty, new Point(8, 0), dragThreshold), "Shelf direct-drag cancellation includes the threshold boundary");
            assert(WindowShelfPolicy.Dragged(new Point(5, 5), new Point(5, -3), dragThreshold), "Final release position can cancel a shelf drag without an intermediate move event");
            assert(!WindowShelfPolicy.Dragged(Point.Empty, new Point(15, 15), TaskbarInput.DirectDragThreshold(new Size(4, 4), 192)), "High-DPI shelf clicks use the mover's scaled direct-drag tolerance");

            foreach (int width in new[] { 160, 320, 800, 1920, 7680 }) {
                var display = DisplayAt(-width, -400, width, 400);
                int capacity = WindowShelfPolicy.Capacity(width);
                assert(capacity >= 1 && capacity <= 10, "Shelf app controls are bounded on every display width");
                var bounds = WindowShelfPolicy.Fit(display, 16 + capacity * 44 + 88, 56, 0);
                assert(WindowShelfPolicy.Bounds(display).Contains(bounds), "Paged shelf stays inside its display");
                assert(bounds.Bottom == display.Y + display.Height, "Shelf can sit flush with the monitor edge without losing its full hit area");
                assert(bounds.Width >= capacity * 44 + 104, "Shelf paging controls and icons never overlap");
                var chooser = WindowShelfPolicy.Fit(display, 360, 320, bounds.Top - 6);
                assert(WindowShelfPolicy.Bounds(display).Contains(chooser), "Bounded chooser stays inside negative-coordinate displays");
            }
            assert(WindowShelfPolicy.Fit(monitor, 5000, 5000, 5000) == WindowShelfPolicy.Bounds(monitor), "Oversized popup dimensions clamp to the destination");
            foreach (int dpi in new[] { 96, 120, 144, 192, 288, 384 }) {
                int capacity = WindowShelfPolicy.Capacity(1920, dpi);
                assert(WindowShelfPolicy.Scale(16, dpi) + capacity * WindowShelfPolicy.Scale(44, dpi) + WindowShelfPolicy.Scale(88, dpi) <= 1920,
                    "High-DPI shelf retains space for app buttons and both overflow controls");
                assert(WindowShelfPolicy.Scale(32, dpi) >= WindowShelfPolicy.Scale(12, dpi) + WindowShelfPolicy.Scale(12, dpi),
                    "High-DPI chooser rows leave vertical padding around 9-point text");
                assert(WindowShelfPolicy.Scale(25, dpi) + WindowShelfPolicy.Scale(15, dpi) <= WindowShelfPolicy.Scale(40, dpi) + 1,
                    "High-DPI count badge stays inside its app button");
            }
            var same = DisplayAt(-1920, -1080, 1920, 1080);
            assert(WindowShelfPolicy.SameTarget(monitor, same), "An unchanged destination snapshot remains valid");
            same.X++;
            assert(!WindowShelfPolicy.SameTarget(monitor, same), "Display position changes invalidate shelf results");
            same.X--; same.Identity = "replacement";
            assert(!WindowShelfPolicy.SameTarget(monitor, same), "A replacement display cannot inherit shelf state");
            same.Identity = monitor.Identity; same.Primary = true;
            assert(!WindowShelfPolicy.SameTarget(monitor, same), "A destination becoming primary invalidates shelf state");

            var original = Window(1, "exe:app", "First"); var current = Window(1, "EXE:APP", "Renamed");
            assert(WindowShelfPolicy.SameWindow(original, current), "Window titles are not persisted as identity, while app keys ignore case");
            current.Handle = new IntPtr(2);
            assert(!WindowShelfPolicy.SameWindow(original, current), "Different HWND cannot replace a selected window");
            current.Handle = original.Handle; current.Process++;
            assert(!WindowShelfPolicy.SameWindow(original, current), "Reused HWND from another PID cannot pass selection");
            current.Process = original.Process; current.ProcessStarted++;
            assert(!WindowShelfPolicy.SameWindow(original, current), "Reused PID from a restarted process cannot pass selection");
            current.ProcessStarted = original.ProcessStarted; current.AppKey = "exe:different";
            assert(!WindowShelfPolicy.SameWindow(original, current), "Changed app identity cannot pass selection");
            assert(!WindowShelfPolicy.SameWindow(null, current), "Missing windows fail closed");
            assert(original.ToString() == "First", "Chooser accessibility exposes the window title, not a CLR type name");
            assert(Window(2, "app:Fallback", null).ToString() == "app:Fallback", "Untitled accessibility fallback uses the application name");
            var windows = new List<ShelfWindow> { original, Window(2, "EXE:APP", "Second") };
            for (int i = 0; i < 137; i++) windows.Add(Window(i + 3, "app:" + i, "Title " + i));
            var groups = WindowShelfPolicy.Group(windows);
            assert(groups.Count == 138 && groups.Sum(g => g.Count) == 139, "App grouping preserves every window beyond a shelf page");
            assert(groups.Single(g => g[0].AppKey.StartsWith("exe:", StringComparison.OrdinalIgnoreCase)).Count == 2, "Same-app windows share one chooser");
            assert(groups.SelectMany(g => g).Select(w => w.Handle).Distinct().Count() == windows.Count, "Overflow does not drop or duplicate window access");

            var rect = new Rectangle(0, 0, 1920, 1080);
            assert(WindowShelfPolicy.Fullscreen(rect, rect, false, false, false), "Borderless fullscreen is recognized without consulting foreground");
            assert(WindowShelfPolicy.Fullscreen(rect, rect, false, true, false), "Borderless maximized fullscreen still suppresses the destination");
            assert(!WindowShelfPolicy.Fullscreen(rect, rect, false, true, true), "Ordinary maximized captioned windows do not suppress the shelf");
            assert(!WindowShelfPolicy.Fullscreen(rect, rect, true, false, false), "Minimized windows do not occupy a fullscreen destination");
            assert(!WindowShelfPolicy.Fullscreen(new Rectangle(0, 0, 800, 600), rect, false, false, false), "A smaller borderless window is not fullscreen");
            assert(WindowShelfPolicy.TaskSwitchable(true, false, true, 0, false, false, true), "Normal movable titled app windows are eligible");
            assert(!WindowShelfPolicy.TaskSwitchable(true, true, true, 0, false, false, true), "Owned windows are excluded");
            assert(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0x80, false, false, true), "Tool windows are excluded");
            assert(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0x08000000, false, false, true), "No-activate windows are excluded");
            assert(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0, true, false, true), "Cloaked windows are excluded");
            assert(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0, false, true, true), "Fullscreen source windows are excluded");
            assert(!WindowShelfPolicy.TaskSwitchable(false, false, true, 0, false, false, true), "Hidden windows are excluded");
            assert(!WindowShelfPolicy.TaskSwitchable(true, false, false, 0, false, false, true), "Disabled modal parents are excluded");
            assert(!WindowShelfPolicy.TaskSwitchable(true, false, true, 0, false, false, false), "Untitled windows are excluded");

            assert(WindowShelfPolicy.AcceptResult(3, 3, false, true), "Current eligible asynchronous result is accepted");
            assert(!WindowShelfPolicy.AcceptResult(2, 3, false, true), "Late hover results cannot reopen a dismissed shelf");
            assert(!WindowShelfPolicy.AcceptResult(3, 3, true, true), "Disposed manager rejects asynchronous results");
            assert(!WindowShelfPolicy.AcceptResult(3, 3, false, false), "Pause and target invalidation reject asynchronous results");
            var late = new ShelfRequest.Result(); late.Icons.Add("app", new Bitmap(1, 1));
            using (var request = new ShelfRequest(4)) {
                request.Dispose(); request.Complete(late);
                assert(request.Cancelled && request.Take() == null && late.Icons.Count == 0, "Cancellation disposes late icon clones without publishing them");
            }
            var ready = new ShelfRequest.Result(); ready.Icons.Add("app", new Bitmap(1, 1));
            using (var request = new ShelfRequest(5)) {
                request.Complete(ready); request.Dispose();
                assert(request.Take() == null && ready.Icons.Count == 0, "Dismissal disposes completed but unconsumed images");
            }
            using (var request = new ShelfRequest(6)) {
                request.Complete(new ShelfRequest.Result());
                using (var result = request.Take()) assert(result != null && request.Take() == null, "A result transfers ownership to the UI exactly once");
            }
            using (var popup = new ShelfPopup("Neutral shelf fixture")) {
                var parameters = (CreateParams)typeof(ShelfPopup).GetProperty("CreateParams", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(popup, null);
                assert((parameters.ExStyle & 0x08000080) == 0x08000080 && !popup.ShowInTaskbar, "Shelf fixture is a non-activating tool window without a taskbar button");
                assert(!popup.Visible, "Shelf fixture tests never show a window or change foreground");
            }
            int moved = 0;
            using (var manager = new WindowShelfManager(delegate { return new List<Display>(); }, delegate { return new List<AppRule>(); }, delegate { moved++; return false; })) {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var tips = (ToolTip)typeof(WindowShelfManager).GetField("tips", flags).GetValue(manager);
                int clicked = 0;
                foreach (string name in new[] { "Neutral app", "Neutral app (2)" }) {
                    using (var button = (ShelfButton)typeof(WindowShelfManager).GetMethod("Button", flags).Invoke(manager,
                        new object[] { name, null, new EventHandler(delegate { clicked++; }) })) {
                        assert(String.IsNullOrEmpty(tips.GetToolTip(button)), "Shelf app icons have no native name tooltip");
                        assert(button.AccessibleName == name, "Tooltip-free shelf icons retain their accessible names");
                        typeof(ShelfButton).GetMethod("OnClick", flags).Invoke(button, new object[] { EventArgs.Empty });
                    }
                }
                assert(clicked == 2, "Removing app tooltips preserves icon click handlers");
                manager.Refresh(); manager.Dispose(); manager.Refresh();
                assert(moved == 0, "Disabled and disposed shelf lifecycle never invokes movement");
            }
            var area = Screen.PrimaryScreen.WorkingArea;
            using (var fixture = new CatalogFixture { Text = "Neutral shelf catalog fixture", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(area.X + 40, area.Y + 40, 360, 260) }) {
                fixture.Show(); Application.DoEvents();
                ShelfWindow first, second;
                assert(WindowShelfCatalog.TryReadWindow(fixture.Handle, out first), "Catalog accepts its own disposable normal top-level window");
                assert(WindowShelfCatalog.TryReadWindow(fixture.Handle, out second) && WindowShelfPolicy.SameWindow(first, second), "Repeated own-process reads preserve PID, start time and app identity");
                IntPtr handle = fixture.Handle;
                fixture.Close(); Application.DoEvents();
                assert(!WindowShelfCatalog.TryReadWindow(handle, out second), "Catalog rejects the closed disposable fixture");
            }
        }
    }
}
