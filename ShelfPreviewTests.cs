using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class ShelfPreviewTests
    {
        sealed class FakeApi : IShelfThumbnailApi
        {
            internal int Registered, Released, Updated, RegisterResult, QueryResult, UpdateResult;
            internal bool ZeroHandle;
            internal Size SourceSize = new Size(1920, 1080);
            internal Rectangle Bounds;
            internal byte Opacity;
            internal IntPtr Target, Source;
            internal IntPtr FailedUpdateHandle;
            internal Action OnQuery, OnUpdate;
            internal readonly HashSet<IntPtr> Live = new HashSet<IntPtr>();
            internal readonly Dictionary<IntPtr, Rectangle> LastBounds = new Dictionary<IntPtr, Rectangle>();
            internal readonly Dictionary<IntPtr, byte> LastOpacity = new Dictionary<IntPtr, byte>();
            public int Register(IntPtr destination, IntPtr source, out IntPtr thumbnail)
            {
                Registered++; Target = destination; Source = source;
                thumbnail = ZeroHandle ? IntPtr.Zero : new IntPtr(Registered);
                if (thumbnail != IntPtr.Zero) Live.Add(thumbnail);
                return RegisterResult;
            }
            public int Query(IntPtr thumbnail, out Size size) { size = SourceSize; if (OnQuery != null) OnQuery(); return QueryResult; }
            public int Update(IntPtr thumbnail, Rectangle bounds, byte opacity)
            {
                Updated++; Bounds = bounds; Opacity = opacity;
                LastBounds[thumbnail] = bounds; LastOpacity[thumbnail] = opacity;
                if (OnUpdate != null) OnUpdate();
                return thumbnail == FailedUpdateHandle ? -1 : UpdateResult;
            }
            public void Unregister(IntPtr thumbnail)
            { if (!Live.Remove(thumbnail)) throw new InvalidOperationException("Duplicate thumbnail release"); Released++; }
        }
        static ShelfWindow Window(int number)
        { return new ShelfWindow { Handle = new IntPtr(number), Process = 12, ProcessStarted = 1234, AppKey = "neutral:fixture", AppName = "Neutral fixture", Title = "Neutral window " + number }; }
        static Display DisplayAt(int width, int height)
        { return new Display { Device = "neutral:display", Identity = "neutral:display", X = -width, Y = -height, Width = width, Height = height }; }
        internal static void Run(Action<bool, string> assert)
        {
            assert(Marshal.SizeOf(typeof(ShelfDwmApi.Properties)) == 48, "DWM thumbnail properties have the native 48-byte layout");
            assert(Marshal.OffsetOf(typeof(ShelfDwmApi.Properties), "Visible").ToInt32() == 40 &&
                Marshal.OffsetOf(typeof(ShelfDwmApi.Properties), "SourceClientAreaOnly").ToInt32() == 44, "DWM BOOLs occupy four bytes after opacity padding");
            var properties = ShelfDwmApi.ForBounds(new Rectangle(13, 17, 200, 120));
            assert(properties.Flags == 29 && properties.Visible && properties.Opacity == 255 && !properties.SourceClientAreaOnly &&
                properties.Destination.Left == 13 && properties.Destination.Top == 17 && properties.Destination.Right == 213 && properties.Destination.Bottom == 137,
                "DWM updates include explicit visibility, opacity and full-source aspect-fit destination coordinates");
            RunMotion(assert);
            RunActionLabels(assert);

            foreach (int dpi in new[] { 96, 120, 144, 192, 288, 384 })
            foreach (int width in new[] { 160, 320, 800, 1920, 3840 })
            foreach (int height in new[] { 160, 300, 600, 1080 })
            foreach (int count in new[] { 1, 2, 3, 5, 137 }) {
                int effectiveDpi = Math.Min(dpi, Math.Min(width, height) * 96 / 160);
                var display = DisplayAt(width, height);
                int bottom = display.Y + display.Height - WindowShelfPolicy.Scale(62, effectiveDpi);
                var layout = ShelfPreviewLayout.Create(display, effectiveDpi, bottom, count);
                var client = new Rectangle(Point.Empty, layout.Bounds.Size);
                assert(WindowShelfPolicy.Bounds(display).Contains(layout.Bounds) && layout.Bounds.Bottom <= bottom, "Preview layout remains above the shelf on negative-coordinate/DPI displays");
                assert(layout.Capacity >= 1 && layout.Capacity <= 4 && layout.Pages * layout.Capacity >= count, "Every grouped window remains reachable with at most four registrations per page");
                for (int slot = 0; slot < layout.Capacity; slot++) {
                    var tile = layout.Tile(slot); var image = layout.Image(slot); var title = layout.Title(slot);
                    assert(client.Contains(tile) && tile.Contains(image) && tile.Contains(title) && image.Width > 0 && image.Height > 0 && !image.IntersectsWith(title),
                        "Tile, image and title dimensions fit without overlaps");
                    var fitted = ShelfPreviewLayout.AspectFit(new Size(1920, 1080), image);
                    var caption = layout.Title(slot, fitted);
                    assert(tile.Contains(caption) && caption.Top > fitted.Bottom && !caption.IntersectsWith(fitted),
                        "Hover caption follows the actual fitted preview without leaving its stable tile");
                    if (slot > 0) assert(layout.Tile(slot - 1).Right < tile.Left, "Adjacent previews retain a pointer/hover gutter");
                }
                if (layout.Pages > 1) assert(client.Contains(layout.Previous) && client.Contains(layout.Next) && client.Contains(layout.PageLabel) &&
                    !layout.Previous.IntersectsWith(layout.Next) && !layout.Tile(0).IntersectsWith(layout.Previous), "Paging hit targets and counter fit below the previews");
            }
            var two = ShelfPreviewLayout.Create(DisplayAt(1920, 1080), 96, -62, 2);
            assert(two.Capacity == 2 && two.Pages == 1 && two.Bounds.Width == 488 && two.Bounds.Height == 202,
                "Two windows have a compact side-by-side preview without paging chrome");
            foreach (var source in new[] { new Size(1920, 1080), new Size(800, 1400), new Size(500, 500), new Size(4000, 200), new Size(1, 10000) }) {
                var bounds = new Rectangle(17, 23, 216, 140);
                var fit = ShelfPreviewLayout.AspectFit(source, bounds);
                assert(bounds.Contains(fit) && fit.Width > 0 && fit.Height > 0 && (fit.Width == bounds.Width || fit.Height == bounds.Height), "Portrait, landscape and square previews letterbox without crop");
                assert(Math.Abs(fit.Width - fit.Height * source.Width / (double)source.Height) <= Math.Max(1, source.Width / (double)source.Height), "Aspect-fit error is limited to pixel rounding");
            }
            assert(ShelfPreviewLayout.AspectFit(Size.Empty, two.Image(0)).IsEmpty && ShelfPreviewLayout.AspectFit(new Size(10, 10), Rectangle.Empty).IsEmpty,
                "Unknown source sizes use placeholders instead of invalid DWM rectangles");

            var dwell = new ShelfGroupDwell(); object first = new object(), second = new object();
            assert(!dwell.Update(first, 100, true) && !dwell.Update(first, 449, true) && dwell.Update(first, 450, true), "Grouped hover requires 350ms of dwell");
            assert(!dwell.Update(second, 600, true) && !dwell.Update(second, 949, true) && dwell.Update(second, 950, true), "Moving between groups restarts the full dwell");
            assert(!dwell.Update(second, 1000, false) && !dwell.Update(second, 2000, true), "Held input suppresses hover until the pointer leaves that icon");
            dwell.Update(null, 2100, true);
            assert(!dwell.Update(second, 2200, true) && dwell.Update(second, 2550, true), "Returning after a drag can start a new deliberate hover");
            dwell.Suppress(second);
            assert(!dwell.Update(second, 3000, true), "A mouse-down between poll ticks still suppresses hover");
            dwell.Reset(); dwell.Update(first, 100, true);
            assert(!dwell.Update(first, 50, true) && !dwell.Update(first, 399, true) && dwell.Update(first, 400, true), "Clock reversal restarts rather than satisfying dwell");
            assert(!WindowShelfPolicy.HideReady(100, 499), "Existing dismissal grace keeps the icon-to-chooser gap usable");

            var api = new FakeApi(); bool valid = true; var window = Window(10);
            var thumbnail = new ShelfThumbnail(api, new IntPtr(20), window, delegate { return valid; });
            thumbnail.Refresh(two.Image(0));
            assert(thumbnail.Available && api.Registered == 1 && api.Updated == 1 && api.Target == new IntPtr(20) && api.Source == window.Handle,
                "Preview explicitly registers and updates the source into its top-level owner");
            thumbnail.Refresh(two.Image(0));
            assert(api.Registered == 1 && api.Updated == 2, "Periodic geometry/identity refresh reuses the current registration");
            api.SourceSize = new Size(600, 1000); thumbnail.Refresh(two.Image(0));
            assert(api.Bounds == ShelfPreviewLayout.AspectFit(api.SourceSize, two.Image(0)), "Source resize updates the aspect-fit destination");
            valid = false; thumbnail.Refresh(two.Image(0));
            assert(thumbnail.Invalid && !thumbnail.Available && api.Live.Count == 0 && api.Released == 1, "Disappearance, exclusion or invalid identity immediately releases DWM resources");
            valid = true; thumbnail.Refresh(two.Image(0));
            assert(api.Registered == 1, "An invalidated page entry cannot silently bind to a replacement source");
            thumbnail.Dispose(); thumbnail.Dispose();
            assert(api.Released == 1, "Repeated cleanup releases each DWM handle only once");
            for (int failure = 0; failure < 5; failure++) {
                api = new FakeApi();
                if (failure == 0) api.RegisterResult = -1;
                if (failure == 1) api.ZeroHandle = true;
                if (failure == 2) api.QueryResult = -1;
                if (failure == 3) api.UpdateResult = -1;
                if (failure == 4) api.SourceSize = Size.Empty;
                using (thumbnail = new ShelfThumbnail(api, new IntPtr(20), window, delegate { return true; })) {
                    thumbnail.Refresh(two.Image(0));
                    assert(!thumbnail.Available && api.Live.Count == 0, "Failed DWM operations leave only a safe placeholder and no owned registration");
                }
            }
            foreach (bool duringUpdate in new[] { false, true }) {
                api = new FakeApi(); valid = true;
                if (duringUpdate) api.OnUpdate = delegate { valid = false; }; else api.OnQuery = delegate { valid = false; };
                using (thumbnail = new ShelfThumbnail(api, new IntPtr(20), window, delegate { return valid; })) {
                    thumbnail.Refresh(two.Image(0));
                    assert(thumbnail.Invalid && !thumbnail.Available && api.Live.Count == 0 && (duringUpdate || api.Updated == 0),
                        "Identity changes during a native call cannot publish a retained preview");
                }
            }
            foreach (int identityChange in new[] { 0, 1, 2, 3 }) {
                var current = Window(10); api = new FakeApi();
                using (thumbnail = new ShelfThumbnail(api, new IntPtr(20), window, delegate(ShelfWindow expected) { return WindowShelfPolicy.SameWindow(expected, current); })) {
                    thumbnail.Refresh(two.Image(0));
                    if (identityChange == 0) current.Handle = new IntPtr(999);
                    if (identityChange == 1) current.Process++;
                    if (identityChange == 2) current.ProcessStarted++;
                    if (identityChange == 3) current.AppKey = "different";
                    thumbnail.Refresh(two.Image(0));
                    assert(thumbnail.Invalid && api.Live.Count == 0, "HWND, PID, process-start and application identity all invalidate a live preview");
                }
            }
            api = new FakeApi();
            using (thumbnail = new ShelfThumbnail(api, new IntPtr(20), window, delegate { return true; })) thumbnail.Refresh(two.Image(0));
            assert(api.Released == 1 && api.Live.Count == 0, "Page disposal releases a valid registration");
            assert(WindowShelfPolicy.LocalAction(false, true, ShelfSelectionOrigin.Chooser) == ShelfLocalAction.Focus &&
                WindowShelfPolicy.LocalAction(true, true, ShelfSelectionOrigin.Chooser) == ShelfLocalAction.Restore, "Preview selection preserves focus-only and minimized-restore semantics");
            RunPaint(assert);
        }

        // Injected monitor identities and fake thumbnails only: no native source
        // lookup, shown window, selection callback or desktop interaction.
        internal static void RunActionLabels(Action<bool, string> assert)
        {
            var display = DisplayAt(1920, 1080);
            var local = Window(1); var remote = Window(2);
            local.Title = remote.Title = "Same window title";
            var devices = new Dictionary<IntPtr, string> { { local.Handle, display.Device }, { remote.Handle, "neutral:other" } };
            int reads = 0;
            Func<ShelfWindow, string> sourceDevice = delegate(ShelfWindow window) { reads++; string device; return devices.TryGetValue(window.Handle, out device) ? device : null; };
            assert(ShelfPreviewAction.Label(local, display, sourceDevice) == "Focus here" &&
                ShelfPreviewAction.Label(remote, display, sourceDevice) == "Bring to this screen",
                "Windows in the same app group with identical titles receive individual local or remote actions");
            devices[local.Handle] = display.Device.ToUpperInvariant();
            assert(ShelfPreviewAction.Label(local, display, sourceDevice) == "Focus here", "Source and destination monitor identities compare without case sensitivity");
            foreach (bool minimized in new[] { false, true }) {
                assert(WindowShelfPolicy.LocalAction(minimized, true, ShelfSelectionOrigin.Chooser) == (minimized ? ShelfLocalAction.Restore : ShelfLocalAction.Focus) &&
                    ShelfPreviewAction.Label(local, display, sourceDevice) == "Focus here" && ShelfPreviewAction.Label(remote, display, sourceDevice) == "Bring to this screen",
                    "Preview verbs remain truthful for local focus or in-place restore and remote minimized-window bring");
            }
            devices[local.Handle] = "neutral:other";
            assert(ShelfPreviewAction.Label(local, display, sourceDevice) == "Bring to this screen", "A changed source monitor receives a fresh action label");
            devices[local.Handle] = display.Device;
            assert(ShelfPreviewAction.Label(local, display, sourceDevice) == "Focus here", "Returning a source to the target changes its action back to focus");
            devices.Remove(local.Handle);
            assert(ShelfPreviewAction.Label(local, display, sourceDevice) == String.Empty &&
                ShelfPreviewAction.Label(local, display, delegate { return " "; }) == String.Empty &&
                ShelfPreviewAction.Label(local, display, delegate { throw new InvalidOperationException("Unavailable source monitor"); }) == String.Empty,
                "Missing or failed source-monitor reads never guess that a window can be brought or focused locally");
            int before = reads;
            assert(ShelfPreviewAction.Label(null, display, sourceDevice) == String.Empty &&
                ShelfPreviewAction.Label(new ShelfWindow { Process = 12 }, display, sourceDevice) == String.Empty &&
                ShelfPreviewAction.Label(new ShelfWindow { Handle = new IntPtr(1) }, display, sourceDevice) == String.Empty &&
                ShelfPreviewAction.Label(remote, null, sourceDevice) == String.Empty &&
                ShelfPreviewAction.Label(remote, new Display(), sourceDevice) == String.Empty &&
                ShelfPreviewAction.Label(remote, display, null) == String.Empty && reads == before,
                "Incomplete source or destination identity is rejected before invoking the source reader");
            assert(ShelfPreviewAction.DefaultAction("Focus here") == "Focus here" &&
                ShelfPreviewAction.DefaultAction("Bring to this screen") == "Bring to this screen" && ShelfPreviewAction.DefaultAction(String.Empty) == "Select" &&
                ShelfPreviewAction.Description("Focus here").Contains("restore it in place") && ShelfPreviewAction.Description("Bring to this screen").Contains("Bring this window") &&
                ShelfPreviewAction.Description(String.Empty) == "Select this window.",
                "Accessibility uses the same contextual action and describes local minimized restore without implying a move");

            devices[local.Handle] = display.Device;
            var windows = new List<ShelfWindow> { local, remote, Window(3), Window(4), Window(5) };
            devices[windows[4].Handle] = "neutral:other";
            var api = new FakeApi(); int selected = 0;
            using (var chooser = new ShelfPreviewChooser(windows, display, 96, -100, null, delegate { return true; }, delegate { selected++; }, api, sourceDevice)) {
                Prime(chooser, windows, api);
                assert(chooser.ActionLabel(0) == "Focus here" && chooser.ActionLabel(1) == "Bring to this screen", "Chooser captions resolve each visible grouped window independently");
                devices[local.Handle] = "neutral:other";
                assert(chooser.ActionLabel(0) == "Bring to this screen", "An existing chooser updates its caption when a source moves away from the target");
                devices[local.Handle] = display.Device;
                assert(chooser.ActionLabel(0) == "Focus here", "An existing chooser updates its caption when the source returns without rebuilding the page");
                var itemType = typeof(ShelfPreviewChooser).GetNestedType("ItemAccessible", BindingFlags.NonPublic);
                var item = (AccessibleObject)Activator.CreateInstance(itemType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { chooser, null, 0, 0, 0 }, null);
                before = reads;
                assert(item.Name == local.ToString() && item.DefaultAction == "Select" && item.Description == "Select this window." && reads == before,
                    "Hidden accessibility entries preserve exact window names and never read a source for stale action descriptions");
                SetField(chooser, "page", 1); Prime(chooser, windows, api);
                assert(chooser.ActionLabel(0) == "Bring to this screen" && chooser.ActionLabel(1) == String.Empty && chooser.ActionLabel(-1) == String.Empty,
                    "Later preview pages resolve their own source and reject unused slots or paging controls");
                before = reads;
                item.DoDefaultAction();
                assert(item.DefaultAction == "Select" && item.State == AccessibleStates.Unavailable && reads == before && selected == 0,
                    "A retained accessibility item from a prior page cannot read, focus or move the new page's window");
                var thumbnails = (List<ShelfThumbnail>)Field(chooser, "thumbnails");
                thumbnails[0].Dispose();
                thumbnails[0] = new ShelfThumbnail(api, new IntPtr(20), windows[4], delegate { return false; });
                thumbnails[0].Refresh(chooser.LayoutInfo.Image(0)); before = reads;
                assert(chooser.ActionLabel(0) == String.Empty && reads == before, "Invalidated preview entries omit contextual verbs without probing a replacement source");
                chooser.Dispose(); before = reads;
                assert(chooser.ActionLabel(0) == String.Empty && item.DefaultAction == "Select" && item.Description == "Select this window." && reads == before,
                    "Disposed choosers and retained accessibility entries never resolve or advertise a live action");
                assert(selected == 0 && !chooser.IsHandleCreated, "Label and accessibility tests create no native host and never invoke the selection callback");
            }
            assert(api.Live.Count == 0 && api.Registered == api.Released, "Context label and paging tests release all fake thumbnail registrations");

            foreach (int dpi in new[] { 96, 120, 144, 192, 288 })
            foreach (var sourceSize in new[] { new Size(1920, 1080), new Size(800, 1400), new Size(4000, 200) }) {
                api = new FakeApi { SourceSize = sourceSize };
                var pair = new List<ShelfWindow> { local, remote };
                using (var chooser = new ShelfPreviewChooser(pair, DisplayAt(3840, 2160), dpi, -100, null, delegate { return true; }, delegate { selected++; }, api, sourceDevice)) {
                    Prime(chooser, pair, api);
                    var layout = chooser.LayoutInfo;
                    var caption = layout.Title(0, chooser.PreviewBounds(0)); Rectangle titleRow, actionRow;
                    ShelfPreviewAction.CaptionRows(caption, out titleRow, out actionRow);
                    assert(titleRow.Width == caption.Width && actionRow.Width == caption.Width && titleRow.Height > 0 && actionRow.Height > 0 &&
                        caption.Contains(titleRow) && caption.Contains(actionRow) && !titleRow.IntersectsWith(actionRow) && Rectangle.Union(titleRow, actionRow) == caption,
                        "Two explicit caption rows preserve the existing caption and hit geometry at each DPI and source aspect ratio");
                    using (var idle = chooser.RenderLayout(true, false)) assert(Clear(idle, caption), "Available Ghost previews keep both contextual caption rows hidden until hover");
                    SetField(chooser, "hovered", 0);
                    using (var hot = chooser.RenderLayout(true, false)) {
                        using (var graphics = Graphics.FromImage(hot)) assert(((Font)Field(chooser, "captionFont")).GetHeight(graphics) <= titleRow.Height &&
                            ((Font)Field(chooser, "captionFont")).GetHeight(graphics) <= actionRow.Height,
                            "Both caption fonts fit their full line height inside the existing rows at the simulated DPI");
                        assert(!Clear(hot, titleRow) && !Clear(hot, actionRow), "Hovered previews paint readable window title and contextual action in separate rows");
                        assert(Clear(hot, layout.Title(1, chooser.PreviewBounds(1))) && Pixel(hot, caption.Left, caption.Top).A == 0,
                            "Context captions preserve the neighboring preview's transparency and have no opaque title backplate");
                    }
                    SetField(chooser, "hovered", 1);
                    using (var hot = chooser.RenderLayout(true, false)) {
                        Rectangle remoteTitle, remoteAction;
                        ShelfPreviewAction.CaptionRows(layout.Title(1, chooser.PreviewBounds(1)), out remoteTitle, out remoteAction);
                        assert(!Clear(hot, remoteTitle) && !Clear(hot, remoteAction) && chooser.ActionLabel(1) == "Bring to this screen",
                            "Remote grouped preview action is painted on its own hovered tile");
                    }
                    using (var opaque = chooser.RenderLayout(false, false))
                    using (var contrast = chooser.RenderLayout(true, true)) {
                        assert(!Clear(opaque, titleRow) && !Clear(opaque, actionRow) && !Clear(contrast, titleRow) && !Clear(contrast, actionRow),
                            "Opaque fallback and high contrast retain both contextual caption rows without requiring hover");
                    }
                    assert(!chooser.IsHandleCreated && selected == 0, "DPI/aspect-ratio action paint tests create no HWND and perform no window action");
                }
                assert(api.Live.Count == 0 && api.Registered == api.Released, "Action paint disposal releases every fake thumbnail");
            }
            api = new FakeApi();
            using (var chooser = new ShelfPreviewChooser(new List<ShelfWindow> { remote }, DisplayAt(160, 300), 96, -62, null, delegate { return true; }, delegate { }, api, sourceDevice)) {
                Prime(chooser, new List<ShelfWindow> { remote }, api); SetField(chooser, "hovered", 0);
                Rectangle nameRow, actionRow; var caption = chooser.LayoutInfo.Title(0, chooser.PreviewBounds(0));
                ShelfPreviewAction.CaptionRows(caption, out nameRow, out actionRow);
                using (var pixels = chooser.RenderLayout(true, false)) assert(!Clear(pixels, nameRow) && !Clear(pixels, actionRow) && !chooser.IsHandleCreated,
                    "Narrow supported previews retain both caption rows with normal text ellipsis and no native host");
            }
            assert(api.Live.Count == 0 && api.Registered == api.Released, "Narrow caption test releases its fake thumbnail");
        }

        static void RunMotion(Action<bool, string> assert)
        {
            var motion = new ShelfPreviewMotion(); motion.Reset(0); motion.Begin(true, 100, true);
            assert(motion.Active && !motion.Dismissing && motion.Value(100) == 0 && motion.Value(99) == 0,
                "Reveal begins at the icon with a clamped monotonic-time phase");
            double previous = 0;
            for (int elapsed = 0; elapsed < 160; elapsed += 10) {
                double phase = motion.Value(100 + elapsed);
                assert(phase >= previous && phase < 1 && motion.Active, "Reveal is monotonic and bounded without overshoot");
                previous = phase;
            }
            assert(motion.Value(260) == 1 && !motion.Active, "Reveal stops after 160ms without an idle animation timer");
            motion.Begin(false, 300, true);
            assert(motion.Dismissing && motion.Value(300) == 1 && motion.Value(409) > 0 && motion.Value(410) == 0 && !motion.Active,
                "Full dismissal completes in 110ms");
            motion.Reset(1); motion.Begin(false, 500, true);
            double before = motion.Value(535); motion.Begin(true, 535, true);
            assert(motion.Value(535) == before && !motion.Dismissing && motion.Active && motion.Value(540) > before,
                "Reentry reverses the close continuously from its current eased phase");
            motion.Begin(false, 540, true); before = motion.Value(560); motion.Begin(true, 560, true);
            assert(motion.Value(560) == before && motion.Value(720) == 1 && !motion.Active,
                "Repeated leave and reentry neither snaps nor extends beyond one reveal duration");
            foreach (bool reveal in new[] { false, true }) {
                motion.Reset(reveal ? 0 : 1); motion.Begin(reveal, 0, false);
                assert(!motion.Active && motion.Value(0) == (reveal ? 1 : 0), "Disabled animations reach either endpoint synchronously");
            }
            foreach (byte opacity in new byte[] { 0, 1, 127, 255 }) {
                var properties = ShelfDwmApi.ForBounds(new Rectangle(4, 5, 6, 7), opacity);
                assert(properties.Opacity == opacity && properties.Visible == (opacity != 0) && properties.Flags == 29,
                    "DWM frames explicitly update opacity and zero-alpha visibility without changing host style");
            }
            foreach (int dpi in new[] { 96, 144, 288 })
            foreach (int count in new[] { 1, 2, 5 }) {
                var layout = ShelfPreviewLayout.Create(DisplayAt(3840, 2160), dpi, -150, count);
                var icon = new Rectangle(-3000, -120, WindowShelfPolicy.Scale(40, dpi), WindowShelfPolicy.Scale(40, dpi));
                var host = Rectangle.Union(layout.Bounds, icon);
                var full = new Rectangle(Point.Empty, layout.Bounds.Size);
                for (int tick = 0; tick <= 20; tick++) {
                    double phase = tick / 20.0;
                    var frame = ShelfPreviewMotion.Transform(full, layout.Bounds, icon, host.Location, phase);
                    var screen = frame; screen.Offset(host.Location);
                    assert(host.Contains(screen), "Stable union host contains the complete icon-to-preview path on negative-coordinate DPI displays");
                    assert(Math.Abs(frame.Width - frame.Height * full.Width / (double)full.Height) <= 1 + full.Width / (double)full.Height,
                        "Motion uses uniform scale with only device-pixel aspect rounding");
                    if (tick == 0) assert(icon.Contains(screen), "The collapsed group fits inside the actual originating icon");
                    if (tick == 20) assert(screen == layout.Bounds, "The revealed frame is the exact unchanged final chooser rectangle");
                }
            }
            var api = new FakeApi(); bool valid = true;
            using (var thumbnail = new ShelfThumbnail(api, new IntPtr(42), Window(9), delegate { return valid; })) {
                var original = new Rectangle(5, 10, 216, 140);
                thumbnail.Refresh(original); var fitted = thumbnail.Destination;
                for (int frame = 0; frame < 30; frame++) thumbnail.Present(new Rectangle(frame, frame, 100, 56), (byte)(frame * 8));
                assert(api.Registered == 1 && api.Updated == 31 && api.Opacity == 232 && thumbnail.Destination == fitted,
                    "Animation updates reuse the live registration and preserve canonical aspect-fit layout");
                valid = false; thumbnail.Present(fitted, 128);
                assert(api.Live.Count == 0 && thumbnail.Invalid && api.Updated == 31, "Animation revalidates source identity before every DWM update");
            }
            api = new FakeApi();
            using (var thumbnail = new ShelfThumbnail(api, new IntPtr(42), Window(9), delegate { return true; })) {
                thumbnail.Refresh(new Rectangle(5, 10, 216, 140)); api.UpdateResult = -1;
                assert(!thumbnail.Present(new Rectangle(1, 2, 100, 56), 100) && api.Live.Count == 0,
                    "Failed animation update releases its DWM handle immediately");
                thumbnail.Present(new Rectangle(1, 2, 100, 56), 100);
                assert(api.Registered == 1 && api.Updated == 2, "Animation frames never retry registration after a native failure");
            }
            api = new FakeApi(); valid = true;
            using (var thumbnail = new ShelfThumbnail(api, new IntPtr(42), Window(9), delegate { return valid; })) {
                thumbnail.Refresh(new Rectangle(5, 10, 216, 140)); api.OnUpdate = delegate { valid = false; };
                assert(!thumbnail.Present(new Rectangle(1, 2, 100, 56), 100) && thumbnail.Invalid && api.Live.Count == 0,
                    "Identity invalidation inside an animated native update immediately releases the registration");
            }
            var windows = new List<ShelfWindow> { Window(1), Window(2) }; api = new FakeApi();
            using (var chooser = new ShelfPreviewChooser(windows, DisplayAt(1920, 1080), 96, -100, null, delegate { return true; }, delegate { }, api)) {
                var final = chooser.LayoutInfo.Bounds; var icon = new Rectangle(-1500, -80, 40, 40);
                chooser.SetRevealOrigin(icon); Prime(chooser, windows, api);
                assert(chooser.LayoutInfo.Bounds == final && chooser.Bounds == Rectangle.Union(final, icon) && !chooser.AnimationRunning,
                    "Setting the origin preserves final hover geometry and prepares one stable union host without starting a timer");
                MouseMove(chooser, new Point(final.X - chooser.Left + 100, final.Y - chooser.Top + 50));
                using (var visible = chooser.Render(true, false)) {
                    assert(visible.Size == chooser.ClientSize, "Animated software frames use the actual union host's client size");
                    SetField(chooser, "visibility", 0.0);
                    using (var hidden = chooser.Render(true, false)) assert(Clear(hidden, new Rectangle(Point.Empty, hidden.Size)),
                        "Collapsed alpha-DIB chrome is completely transparent without cached source-window pixels");
                    SetField(chooser, "visibility", 1.0);
                    using (var again = chooser.Render(true, false)) {
                        assert(visible.Size == again.Size, "Repeated animation frames retain the same host viewport before pixel comparison");
                        bool identical = true;
                        for (int y = 0; y < visible.Height; y++)
                        for (int x = 0; x < visible.Width; x++) identical &= visible.GetPixel(x, y) == again.GetPixel(x, y);
                        assert(identical, "Returning to full reveal preserves the exact original C-style software pixels");
                    }
                }
            }
            assert(api.Live.Count == 0, "Motion geometry disposal releases fake registrations without creating a native handle");
            foreach (int failedSlot in new[] { 0, 1 }) {
                api = new FakeApi();
                using (var chooser = new ShelfPreviewChooser(windows, DisplayAt(1920, 1080), 96, -100, null, delegate { return true; }, delegate { }, api)) {
                    var final = chooser.LayoutInfo.Bounds; chooser.SetRevealOrigin(new Rectangle(-1500, -80, 40, 40));
                    var host = chooser.Bounds; Prime(chooser, windows, api);
                    var phase = (ShelfPreviewMotion)Field(chooser, "motion"); phase.Reset(0.45); phase.Begin(true, 0, true);
                    SetField(chooser, "visibility", 0.45); Invoke(chooser, "ApplyMotion");
                    var retained = new IntPtr(failedSlot == 0 ? 2 : 1);
                    var expected = chooser.PreviewBounds(failedSlot == 0 ? 1 : 0);
                    expected.Offset(final.X - host.X, final.Y - host.Y);
                    assert(api.LastBounds[retained] != expected && api.LastOpacity[retained] < 255,
                        "Failure regression begins with an actual partial-scale partial-opacity retained thumbnail");
                    api.FailedUpdateHandle = new IntPtr(failedSlot + 1); Invoke(chooser, "ApplyMotion");
                    assert(api.Live.Count == 1 && api.Live.Contains(retained) && api.Released == 1 && api.Registered == 2,
                        "A native animation failure releases only the failing registration without re-registering retained sources");
                    assert(api.LastBounds[retained] == expected && api.LastOpacity[retained] == 255,
                        "Failure fallback settles every retained DWM destination to its exact final host-local rectangle and full opacity regardless of update order");
                    assert((double)Field(chooser, "visibility") == 1 && !phase.Active && !chooser.AnimationRunning &&
                        chooser.Bounds == host && chooser.LayoutInfo.Bounds == final && !chooser.IsHandleCreated,
                        "Failure fallback settles software chrome and stops motion while preserving stable host and hover bounds");
                }
                assert(api.Live.Count == 0 && api.Released == 2, "Failure fallback disposal releases the retained DWM registration exactly once");
            }
        }

        static object Field(object instance, string name)
        { return instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
        static void SetField(object instance, string name, object value)
        { instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value); }
        static object Invoke(object instance, string name, params object[] arguments)
        { return instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, arguments); }
        static void MouseMove(ShelfPreviewChooser chooser, Point point)
        {
            typeof(ShelfPreviewChooser).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(chooser,
                new object[] { new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0) });
        }
        static bool Clear(Bitmap bitmap, Rectangle bounds)
        {
            RequireBounds(bitmap, bounds);
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            for (int x = bounds.Left; x < bounds.Right; x++) if (bitmap.GetPixel(x, y).A != 0) return false;
            return true;
        }
        static Rectangle InkBounds(Bitmap bitmap, Rectangle bounds)
        {
            RequireBounds(bitmap, bounds);
            int left = bounds.Right, top = bounds.Bottom, right = bounds.Left, bottom = bounds.Top;
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            for (int x = bounds.Left; x < bounds.Right; x++) {
                if (bitmap.GetPixel(x, y).A == 0) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
            }
            return right > left && bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
        }
        static bool DarkGlyphEdge(Bitmap bitmap, Rectangle bounds)
        {
            RequireBounds(bitmap, bounds);
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            for (int x = bounds.Left; x < bounds.Right; x++) {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A >= 180 && pixel.R < 32 && pixel.G < 32 && pixel.B < 32) return true;
            }
            return false;
        }
        static void RequireBounds(Bitmap bitmap, Rectangle bounds)
        {
            if (bounds.Width < 0 || bounds.Height < 0 || !new Rectangle(Point.Empty, bitmap.Size).Contains(bounds))
                throw new ArgumentOutOfRangeException("bounds", bounds, "Paint sample must fit the bitmap " + bitmap.Size);
        }
        static Color Pixel(Bitmap bitmap, int x, int y)
        { RequireBounds(bitmap, new Rectangle(x, y, 1, 1)); return bitmap.GetPixel(x, y); }
        static Bitmap PaintLayout(ShelfPreviewChooser chooser, bool transparent, bool contrast, Action<bool, string> assert)
        {
            var bitmap = chooser.RenderLayout(transparent, contrast);
            assert(bitmap.Size == chooser.LayoutInfo.Bounds.Size, "Software paint tests sample the full layout canvas regardless of the host viewport");
            return bitmap;
        }
        static void RunRenderBounds(Action<bool, string> assert)
        {
            foreach (int dpi in new[] { 96, 144, 192, 288 }) {
                var windows = new List<ShelfWindow> { Window(1), Window(2) }; var api = new FakeApi();
                using (var chooser = new ShelfPreviewChooser(windows, DisplayAt(3840, 2160), dpi, -100, null, delegate { return true; }, delegate { }, api)) {
                    Prime(chooser, windows, api);
                    var layout = chooser.LayoutInfo; var fit = chooser.PreviewBounds(0);
                    chooser.ClientSize = new Size(320, 120);
                    chooser.Location = new Point(layout.Bounds.X - 10, layout.Bounds.Y - 12);
                    assert(chooser.ClientSize.Width < layout.Bounds.Width && chooser.ClientSize.Height < layout.Bounds.Height,
                        "Paint regression deliberately uses a host smaller than the simulated 4K layout at every DPI");
                    using (var pixels = PaintLayout(chooser, false, false, assert))
                    using (var frame = chooser.Render(false, false)) {
                        assert(frame.Size == chooser.ClientSize && frame.Size != pixels.Size,
                            "Actual-window rendering retains the smaller client viewport instead of returning layout-sized pixels");
                        var sample = new Point(fit.Left + 2, fit.Top + 2); var translated = new Point(sample.X + 10, sample.Y + 12);
                        assert(Pixel(frame, translated.X, translated.Y) == Pixel(pixels, sample.X, sample.Y) && Pixel(frame, 0, 0).A == 0,
                            "Actual-window rendering translates layout pixels into host coordinates and leaves the surrounding viewport clear");
                        var secondTitle = layout.Title(1, chooser.PreviewBounds(1));
                        assert(!new Rectangle(Point.Empty, frame.Size).Contains(secondTitle) && !Clear(pixels, secondTitle),
                            "Full layout paint retains the second caption beyond the clipped host, without reducing sampling coverage");
                        bool rejected = false;
                        try { Clear(frame, secondTitle); } catch (ArgumentOutOfRangeException error) { rejected = error.ParamName == "bounds"; }
                        assert(rejected, "Paint sampling rejects out-of-bitmap rectangles explicitly before GetPixel");
                    }
                    SetField(chooser, "visibility", 0.0);
                    using (var pixels = PaintLayout(chooser, false, false, assert))
                    using (var hidden = chooser.Render(true, false)) {
                        assert(!Clear(pixels, layout.Title(1, chooser.PreviewBounds(1))) && Clear(hidden, new Rectangle(Point.Empty, hidden.Size)),
                            "Layout paint stays deterministic while the actual host applies its collapsed animation alpha");
                    }
                    assert(!chooser.IsHandleCreated && api.Registered == 2 && api.Live.Count == 2,
                        "Viewport regression creates no native host and preserves the fake thumbnail registrations");
                }
                assert(api.Live.Count == 0 && api.Released == 2, "Viewport regression disposal releases all fake thumbnails");
            }
        }
        static void Prime(ShelfPreviewChooser chooser, List<ShelfWindow> windows, FakeApi api)
        {
            var thumbnails = (List<ShelfThumbnail>)Field(chooser, "thumbnails");
            foreach (var thumbnail in thumbnails) thumbnail.Dispose();
            thumbnails.Clear();
            int start = chooser.Page * chooser.LayoutInfo.Capacity;
            for (int index = 0; index < Math.Min(chooser.LayoutInfo.Capacity, windows.Count - start); index++) {
                var thumbnail = new ShelfThumbnail(api, new IntPtr(20), windows[start + index], delegate { return true; });
                thumbnail.Refresh(chooser.LayoutInfo.Image(index)); thumbnails.Add(thumbnail);
            }
        }
        // Pure software pixels and fake thumbnails: no shown windows, composition changes or user input.
        static void RunPaint(Action<bool, string> assert)
        {
            RunRenderBounds(assert);
            var margins = ShelfPreviewGlass.Frame(true); var reset = ShelfPreviewGlass.Frame(false);
            assert(Marshal.SizeOf(typeof(ShelfPreviewGlass.Margins)) == 16 && margins.Left == -1 && margins.Right == -1 && margins.Top == -1 && margins.Bottom == -1,
                "Ghost host extends the entire native frame with a correctly laid-out MARGINS structure");
            assert(reset.Left == 0 && reset.Right == 0 && reset.Top == 0 && reset.Bottom == 0,
                "High contrast or a failed alpha presentation resets the extended frame");
            foreach (bool contrast in new[] { false, true })
            foreach (bool transparency in new[] { false, true })
            foreach (bool failed in new[] { false, true }) {
                bool requested = ShelfAppearance.GhostAllowed(contrast, transparency, false, failed);
                var frame = ShelfPreviewGlass.Frame(requested);
                assert(requested == (!contrast && transparency && !failed) && frame.Left == (requested ? -1 : 0),
                    "Chooser frame policy shares the shelf's read-only transparency preference, high-contrast and presentation-failure gates");
            }
            foreach (int dpi in new[] { 96, 144, 192, 288 })
            foreach (var source in new[] { new Size(1920, 1080), new Size(800, 1400), new Size(500, 500), new Size(4000, 200), new Size(1, 10000) }) {
                var windows = new List<ShelfWindow> { Window(1), Window(2) };
                var api = new FakeApi { SourceSize = source };
                using (var chooser = new ShelfPreviewChooser(windows, DisplayAt(3840, 2160), dpi, -100, null, delegate { return true; }, delegate { }, api)) {
                    Prime(chooser, windows, api);
                    var layout = chooser.LayoutInfo; var fit = chooser.PreviewBounds(0);
                    assert(chooser.Location == layout.Bounds.Location, "Nonanimated paint hit and fallback-region checks share layout-local coordinates");
                    var title = layout.Title(0, fit); var secondTitle = layout.Title(1, chooser.PreviewBounds(1));
                    var parameters = (CreateParams)typeof(ShelfPreviewChooser).GetProperty("CreateParams", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chooser, null);
                    bool buffered = (bool)typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chooser, null);
                    assert(!chooser.IsHandleCreated && !buffered && !chooser.TopMost && !chooser.ShowInTaskbar &&
                        (parameters.ExStyle & 0x08000088) == 0x08000088 && (parameters.ExStyle & 0x80000) == 0 && (parameters.ClassStyle & 0x20000) == 0,
                        "Ghost painting preserves the nonlayered nonactivating native-topmost host without an opaque managed buffer or class shadow");
                    var gutter = Rectangle.FromLTRB(layout.Tile(0).Right, 0, layout.Tile(1).Left, layout.Bounds.Height);
                    using (var idle = PaintLayout(chooser, true, false, assert)) {
                        assert(idle.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppPArgb && Clear(idle, gutter) &&
                            Clear(idle, new Rectangle(0, 0, idle.Width, layout.Padding / 2)), "Ghost canvas and surrounding gutters contain zero-alpha pixels, not a translucent slab");
                        assert(Clear(idle, title) && Clear(idle, secondTitle), "Available idle previews have no visible captions or footers");
                        assert(Pixel(idle, Center(fit).X, Center(fit).Y).A == 0, "The live preview has no managed image-box fill over or behind it");
                        assert(Pixel(idle, fit.Left - 1, Center(fit).Y).A <= 16, "Idle fitted previews have only a very faint shadow, no selection outline");
                    }
                    MouseMove(chooser, Center(layout.Tile(0)));
                    using (var hot = PaintLayout(chooser, true, false, assert)) {
                        var edge = Pixel(hot, fit.Left - 1, Center(fit).Y);
                        assert(edge.ToArgb() == Color.FromArgb(154, 220, 196).ToArgb() && Pixel(hot, fit.Left - 2, Center(fit).Y).A <= 16,
                            "Mint hover outline is exactly one device pixel immediately outside the actual aspect-fit image");
                        assert(!Clear(hot, title) && Clear(hot, secondTitle) && Pixel(hot, title.Left, title.Top).A == 0 && Clear(hot, gutter),
                            "Only the hovered caption appears, without a title backplate or opaque gutter");
                        using (var textOnly = ShelfPaint.Canvas(hot.Size, dpi)) {
                            using (var graphics = Graphics.FromImage(textOnly)) {
                                graphics.Clear(Color.Transparent);
                                ShelfPaint.Text(graphics, windows[0].ToString(), chooser.Font, title, Color.FromArgb(235, 237, 240), true);
                            }
                            var bare = InkBounds(textOnly, title); var outlined = InkBounds(hot, title);
                            int radius = Math.Max(1, WindowShelfPolicy.Scale(1, dpi));
                            var limit = bare; limit.Inflate(radius, radius);
                            assert(limit.Contains(outlined) && outlined.Left < bare.Left && outlined.Top < bare.Top && outlined.Right > bare.Right && outlined.Bottom > bare.Bottom,
                                "Hover title has a multidirectional glyph outline confined to one DPI-scaled pixel, not a background");
                            assert(DarkGlyphEdge(hot, outlined), "Caption outline has dark high-alpha pixels that remain visible over bright content");
                        }
                        assert(((ToolTip)Field(chooser, "tips")).GetToolTip(chooser) == "", "Thumbnail tiles do not create redundant white tooltips");
                    }
                    MouseMove(chooser, new Point(gutter.Left, gutter.Top));
                    using (var idleAgain = PaintLayout(chooser, true, false, assert)) assert(Clear(idleAgain, title), "Leaving the tile removes its hover caption immediately without animation");
                    using (var contrast = PaintLayout(chooser, true, true, assert))
                    using (var region = chooser.FallbackRegion()) {
                        assert(Pixel(contrast, title.Left, title.Top).ToArgb() == SystemColors.Window.ToArgb() && !Clear(contrast, title),
                            "High contrast forces readable opaque system-color captions even without hover");
                        assert(region.IsVisible(Center(fit)) && region.IsVisible(Center(title)) && !region.IsVisible(Center(gutter)) && !region.IsVisible(0, 0),
                            "Non-glass fallback clips to separate previews and captions, never a shared outer panel");
                    }
                    using (var fallback = PaintLayout(chooser, ShelfAppearance.GhostAllowed(false, false, false, false), false, assert))
                        assert(!Clear(fallback, title) && Clear(fallback, gutter),
                            "Disabled Windows transparency keeps titles readable and gaps absent without changing the Windows preference");
                    assert(api.Live.Count == 2 && api.Registered == 2 && !chooser.IsHandleCreated,
                        "Software presentation changes neither thumbnail registrations nor native window lifetime");
                }
                assert(api.Live.Count == 0 && api.Released == 2, "Paint-test disposal releases every fake thumbnail");
            }
            var pageWindows = new List<ShelfWindow> { Window(1), Window(2), Window(3), Window(4), Window(5) };
            var pageApi = new FakeApi();
            using (var chooser = new ShelfPreviewChooser(pageWindows, DisplayAt(1920, 1080), 96, -100, null, delegate { return true; }, delegate { }, pageApi)) {
                Prime(chooser, pageWindows, pageApi);
                SetField(chooser, "page", 1); Prime(chooser, pageWindows, pageApi);
                var layout = chooser.LayoutInfo;
                using (var lastPage = PaintLayout(chooser, true, false, assert)) {
                    assert(Clear(lastPage, layout.Tile(1)) && !Clear(lastPage, layout.Previous) && !Clear(lastPage, layout.PageLabel),
                        "Last-page unused slots stay transparent while paging symbols and count remain visible");
                    assert(Pixel(lastPage, layout.Previous.Left, layout.Previous.Top).A == 0 && Pixel(lastPage, layout.PageLabel.Left, layout.PageLabel.Top).A == 0,
                        "Paging controls float without a shared footer strip");
                }
                pageApi.QueryResult = -1;
                var thumbnail = ((List<ShelfThumbnail>)Field(chooser, "thumbnails"))[0];
                thumbnail.Refresh(layout.Image(0));
                using (var missing = PaintLayout(chooser, true, false, assert)) assert(!Clear(missing, layout.Title(0, chooser.PreviewBounds(0))) && pageApi.Live.Count == 0,
                    "Unavailable or minimized-without-a-frame fallback retains a visible title without retaining failed DWM registrations");
                assert((int)Field(chooser, "hovered") == -1, "Fallback readability does not manufacture a hover state");
            }
        }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        sealed class Fixture : Form { protected override bool ShowWithoutActivation { get { return true; } } }
        sealed class TrackedDwm : IShelfThumbnailApi
        {
            readonly ShelfDwmApi native = new ShelfDwmApi();
            internal readonly HashSet<IntPtr> Live = new HashSet<IntPtr>();
            internal int Released, Updated;
            internal int Registers, Queries, LastRegister, LastQuery, LastUpdate;
            internal Rectangle LastBounds;
            internal byte LastOpacity;
            internal bool TopLevel = true;
            public int Register(IntPtr destination, IntPtr source, out IntPtr thumbnail)
            {
                TopLevel &= GetAncestor(destination, 2) == destination;
                int result = native.Register(destination, source, out thumbnail);
                Registers++; LastRegister = result;
                if (thumbnail != IntPtr.Zero) Live.Add(thumbnail);
                return result;
            }
            public int Query(IntPtr thumbnail, out Size size) { Queries++; LastQuery = native.Query(thumbnail, out size); return LastQuery; }
            public int Update(IntPtr thumbnail, Rectangle bounds, byte opacity)
            { LastBounds = bounds; LastOpacity = opacity; LastUpdate = native.Update(thumbnail, bounds, opacity); if (LastUpdate == 0) Updated++; return LastUpdate; }
            public void Unregister(IntPtr thumbnail)
            { if (!Live.Remove(thumbnail)) throw new InvalidOperationException("Unexpected DWM handle release"); Released++; native.Unregister(thumbnail); }
        }
        static IntPtr Position(Point point) { return new IntPtr((point.Y << 16) | (point.X & 0xffff)); }
        static Point Center(Rectangle rectangle) { return new Point(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2); }
        static void Click(ShelfPreviewChooser chooser, Point point)
        {
            SendMessage(chooser.Handle, 0x0201, new IntPtr(1), Position(point));
            SendMessage(chooser.Handle, 0x0202, IntPtr.Zero, Position(point));
        }
        static void Pump(int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            do { Application.DoEvents(); System.Threading.Thread.Sleep(2); } while (clock.ElapsedMilliseconds < milliseconds);
        }
        static void RunNativeMotion(List<ShelfWindow> windows, Display display, Func<ShelfWindow, bool> validate, IntPtr active, Action<bool, string> assert)
        {
            var api = new TrackedDwm(); int completed = 0, activations = 0, selected = 0; IntPtr selectedWindow = IntPtr.Zero;
            using (var chooser = new ShelfPreviewChooser(windows.GetRange(0, 2), display, 96, display.Y + display.Height - 100, null, validate,
                delegate(ShelfWindow window) { selected++; selectedWindow = window.Handle; }, api)) {
                var final = chooser.LayoutInfo.Bounds;
                var icon = new Rectangle(final.Left - 70, final.Bottom + 20, 40, 40);
                chooser.SetRevealOrigin(icon); chooser.Activated += delegate { activations++; };
                chooser.Show();
                bool enabled = (bool)typeof(ShelfPreviewChooser).GetProperty("AnimationEnabled", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chooser, null);
                var host = chooser.Bounds; int registrations = api.Registers;
                Pump(190);
                assert(chooser.Bounds == host && chooser.LayoutInfo.Bounds == final && !chooser.AnimationRunning && api.Registers == registrations,
                    "Native reveal keeps both host and final layout stationary and registers each source only once");
                assert(api.LastOpacity == 255 && api.LastUpdate == 0 && (GetWindowLong(chooser.Handle, -20) & 0x80000) == 0,
                    "Completed reveal retains full-opacity live DWM in a nonlayered native host");
                assert(SendMessage(chooser.Handle, 0x0084, IntPtr.Zero, Position(Center(icon))) == new IntPtr(-1),
                    "Union drawing surface passes hit testing through at the originating icon");
                var motion = (ShelfPreviewMotion)Field(chooser, "motion");
                if (enabled) {
                    chooser.BeginDismiss(delegate { completed++; }); Pump(22);
                    double closing = (double)Field(chooser, "visibility");
                    assert(chooser.IsDismissing && chooser.AnimationRunning && closing > 0 && closing < 1 && api.LastOpacity < 255,
                        "Native dismissal updates live-thumbnail opacity while retaining registrations");
                    Invoke(chooser, "OnMouseEnter", EventArgs.Empty);
                    assert(!chooser.IsDismissing && chooser.AnimationRunning && (double)Field(chooser, "visibility") <= closing && completed == 0,
                        "Chooser mouse-enter reverses immediately without waiting for manager polling");
                    Pump(180);
                    chooser.BeginDismiss(delegate { completed++; }); Pump(20);
                    var target = chooser.PointToClient(Center(final));
                    SendMessage(chooser.Handle, 0x0200, IntPtr.Zero, Position(target));
                    assert(!chooser.IsDismissing && completed == 0 && chooser.Visible, "Native mouse-move also cancels a pending dismissal");
                    Pump(180);
                    motion.Reset(0.8); SetField(chooser, "visibility", 0.8); Invoke(chooser, "ApplyMotion"); Invoke(chooser, "ResumeReveal");
                    var moving = (Rectangle)Invoke(chooser, "VisualBounds", chooser.LayoutInfo.Tile(1));
                    var point = Center(moving); var phase = (double)Field(chooser, "visibility");
                    SendMessage(chooser.Handle, 0x0201, new IntPtr(1), Position(point));
                    var held = api.LastBounds; int queries = api.Queries;
                    Pump(185); chooser.RefreshPreviews();
                    assert(chooser.Capture && !chooser.AnimationRunning && (double)Field(chooser, "visibility") == phase && api.LastBounds == held && api.Queries == queries,
                        "A native press pins exact visual hit geometry and defers source-size refresh until release");
                    chooser.BeginDismiss(delegate { completed++; });
                    assert(!chooser.IsDismissing && completed == 0, "A captured preview press cannot be dismissed under the pointer");
                    SendMessage(chooser.Handle, 0x0202, IntPtr.Zero, Position(point));
                    assert(selected == 1 && selectedWindow == windows[1].Handle && completed == 0,
                        "Releasing after the original animation deadline selects the originally pressed window, never its neighbor");
                    Pump(180);
                    chooser.BeginDismiss(delegate { completed++; }); Pump(160);
                    assert(completed == 1 && !chooser.Visible && !chooser.IsDismissing && !chooser.AnimationRunning && api.Live.Count == 0,
                        "Completed native dismissal hides first, releases every DWM handle and invokes its callback once");
                    Pump(140); assert(completed == 1, "No idle animation timer repeats a completed dismiss callback");
                    chooser.Show(); Pump(20); chooser.BeginDismiss(delegate { completed++; }); chooser.Hide(); Pump(130);
                    assert(completed == 1 && !chooser.AnimationRunning && !chooser.IsDismissing && api.Live.Count == 0,
                        "Immediate hide cancels both animation and pending callback while releasing live resources");
                    chooser.Show(); Pump(20); chooser.BeginDismiss(delegate { completed++; });
                    SetField(chooser, "clientAnimation", false); chooser.CancelDismiss();
                    assert(chooser.Visible && !chooser.AnimationRunning && !chooser.IsDismissing && api.LastOpacity == 255 && completed == 1,
                        "A disabled-animation reversal restores the final frame synchronously");
                } else assert(!chooser.AnimationRunning, "Windows animation/transparency policy disables native animation without changing preferences");
                SetField(chooser, "clientAnimation", false);
                chooser.BeginDismiss(delegate { completed++; });
                assert(!chooser.Visible && !chooser.AnimationRunning && api.Live.Count == 0,
                    "Disabled animation dismissal is synchronous and releases native resources");
                chooser.Show(); Pump(40); chooser.BeginDismiss(delegate { completed++; }); int callbacks = completed;
                chooser.Dispose(); Pump(180);
                assert(api.Live.Count == 0 && completed == callbacks && !chooser.AnimationRunning,
                    "Disposal during motion releases DWM and cancels all pending animation callbacks");
                assert(activations == 0 && GetActiveWindow() == active && !chooser.TopMost,
                    "Reveal, reversal, press, dismiss and disposal cause no activation or managed TopMost changes");
            }
        }
        // Opt-in only: this creates and briefly shows its own neutral windows. Never run from Run().
        internal static void RunNative(Action<bool, string> assert)
        {
            var display = Native.Displays().Find(d => d.Primary);
            var exclusions = new List<AppRule>(); var fixtures = new List<Fixture>(); var windows = new List<ShelfWindow>();
            var api = new TrackedDwm(); int selections = 0, rejected = 0, activations = 0; IntPtr activeAtSelection = IntPtr.Zero;
            ShelfPreviewChooser chooser = null;
            try {
                for (int index = 0; index < 5; index++) {
                    var fixture = new Fixture { Text = "Neutral preview fixture " + index, ShowInTaskbar = true,
                        StartPosition = FormStartPosition.Manual, BackColor = index % 2 == 0 ? Color.Teal : Color.Maroon,
                        Bounds = new Rectangle(display.X + 50 + index * 15, display.Y + 50, index == 1 ? 240 : 420, 300) };
                    fixtures.Add(fixture); fixture.Show(); Native.ShowWindowAsync(fixture.Handle, 8); Application.DoEvents();
                    ShelfWindow window;
                    bool read = WindowShelfCatalog.TryReadWindow(fixture.Handle, out window);
                    assert(read, "Native preview test reads only its own neutral top-level fixture");
                    if (!read) throw new InvalidOperationException("Neutral fixture not catalog eligible");
                    windows.Add(window);
                }
                Func<ShelfWindow, bool> validate = delegate(ShelfWindow expected) {
                    ShelfWindow current; bool valid = WindowShelfCatalog.TryReadSelection(expected, display, exclusions, out current);
                    if (!valid) rejected++; return valid;
                };
                chooser = new ShelfPreviewChooser(windows, display, 96, display.Y + display.Height - 100, null, validate,
                    delegate { selections++; activeAtSelection = GetActiveWindow(); }, api);
                chooser.Activated += delegate { activations++; };
                var parameters = (CreateParams)typeof(ShelfPreviewChooser).GetProperty("CreateParams", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chooser, null);
                assert((parameters.ExStyle & 0x08000088) == 0x08000088 && (parameters.ExStyle & 0x80000) == 0 && !chooser.ShowInTaskbar && !chooser.TopMost,
                    "Preview destination is a nonlayered nonactivating top-level tool window");
                fixtures[0].Activate(); Application.DoEvents(); IntPtr active = GetActiveWindow();
                chooser.Show(); Native.ShowWindowAsync(chooser.Handle, 8); Application.DoEvents();
                string state = "Native preview state: activeBefore=" + active + ", activeAfter=" + GetActiveWindow() + ", chooser=" + chooser.Handle +
                    ", visible=" + chooser.Visible + ", children=" + chooser.Controls.Count + ", topLevel=" + api.TopLevel + ", entries=" + chooser.PreviewCount +
                    ", live=" + api.Live.Count + ", registers=" + api.Registers + ", queries=" + api.Queries + ", updates=" + api.Updated + ", released=" + api.Released +
                    ", rejected=" + rejected + ", registerHR=" + api.LastRegister.ToString("X8") + ", queryHR=" + api.LastQuery.ToString("X8") + ", updateHR=" + api.LastUpdate.ToString("X8");
                assert(true, state);
                assert(GetActiveWindow() == active, "Showing the preview preserves the test thread's active window");
                assert(activations == 0 && !chooser.ContainsFocus && (GetWindowLong(chooser.Handle, -20) & 0x8) != 0,
                    "Native topmost presentation causes no transient managed activation or focus");
                assert(chooser.Controls.Count == 0 && api.TopLevel, "All DWM destinations are top-level with no child HWNDs covering previews");
                assert(chooser.PreviewCount > 0, "Showing the chooser builds its initial preview entries");
                assert(api.Live.Count == chooser.PreviewCount && api.Live.Count > 0, "Every initial neutral source owns a DWM registration");
                assert(api.Updated >= chooser.PreviewCount, "Every initial neutral source receives a successful visible DWM update");
                var accessible = chooser.AccessibilityObject;
                var firstChild = accessible.GetChild(0);
                assert(Object.ReferenceEquals(firstChild, accessible.GetChild(0)) && firstChild.State != AccessibleStates.Unavailable,
                    "Repeated accessibility lookup preserves the same usable child object");
                assert(firstChild.Name == windows[0].ToString(), "Hover-only captions retain the full untruncated accessible window name");
                assert(chooser.ActionLabel(0) == "Focus here" && firstChild.DefaultAction == "Focus here" &&
                    firstChild.Description == ShelfPreviewAction.Description("Focus here"),
                    "Native local preview exposes the same focus action in its caption and current accessibility entry");
                var point = Center(chooser.LayoutInfo.Tile(0));
                assert(SendMessage(chooser.Handle, 0x0021, chooser.Handle, new IntPtr((0x0201 << 16) | 1)) == new IntPtr(3), "Preview mouse activation returns MA_NOACTIVATE");
                Click(chooser, point);
                assert(selections == 1 && activeAtSelection == active && GetActiveWindow() == active && !chooser.ContainsFocus,
                    "Preview press/release selects once without focus theft");
                SendMessage(chooser.Handle, 0x0201, new IntPtr(1), Position(point));
                var far = new Point(point.X + 40, point.Y);
                SendMessage(chooser.Handle, 0x0200, new IntPtr(1), Position(far));
                SendMessage(chooser.Handle, 0x0200, new IntPtr(1), Position(point));
                SendMessage(chooser.Handle, 0x0202, IntPtr.Zero, Position(point));
                assert(selections == 1, "Dragging away and back cannot select a preview");
                SendMessage(chooser.Handle, 0x0201, new IntPtr(1), Position(point));
                SendMessage(chooser.Handle, 0x0202, IntPtr.Zero, Position(far));
                assert(selections == 1, "Release-only displacement also cancels preview selection");
                SendMessage(chooser.Handle, 0x0201, new IntPtr(1), Position(point));
                chooser.Capture = false;
                SendMessage(chooser.Handle, 0x0202, IntPtr.Zero, Position(point));
                assert(selections == 1, "Unrelated capture loss still cancels the pending preview press");
                int released = api.Released;
                Click(chooser, Center(chooser.LayoutInfo.Next));
                assert(chooser.Page == 1 && api.Released > released && api.Live.Count == chooser.PreviewCount && GetActiveWindow() == active,
                    "Nonactivating page control releases the previous page before new registrations");
                assert(firstChild.State == AccessibleStates.Unavailable && firstChild.Bounds.IsEmpty, "Leaving a page invalidates its cached accessibility children");
                assert(firstChild.DefaultAction == "Select" && firstChild.Description == "Select this window.",
                    "Native stale accessibility entries stop advertising the previous window's contextual action");
                var pageChild = accessible.GetChild(0);
                released = api.Released; chooser.Rebuild();
                assert(api.Released > released && api.Live.Count == chooser.PreviewCount, "Rebuilding cannot leak page registrations");
                assert(pageChild.State == AccessibleStates.Unavailable && !Object.ReferenceEquals(pageChild, accessible.GetChild(0)) &&
                    Object.ReferenceEquals(accessible.GetChild(0), accessible.GetChild(0)), "A rebuild replaces children once, then preserves stable identities");
                chooser.ChangePage(-1);
                firstChild.DoDefaultAction();
                assert(firstChild.State == AccessibleStates.Unavailable && !Object.ReferenceEquals(firstChild, accessible.GetChild(0)) && selections == 1,
                    "Returning to page zero cannot resurrect or activate a stale page-zero accessibility object");
                chooser.Hide();
                RunNativeMotion(windows, display, validate, active, assert);
                chooser.Show(); Application.DoEvents();
                fixtures[0].WindowState = FormWindowState.Minimized; Application.DoEvents(); chooser.RefreshPreviews();
                assert(IsIconic(fixtures[0].Handle), "A minimized source stays minimized while DWM tries its last frame");
                assert(chooser.PreviewCount <= 4, "Minimized fallback does not add registrations");
                exclusions.Add(new AppRule { Key = windows[0].AppKey }); chooser.RefreshPreviews();
                assert(api.Live.Count == 0, "New exclusion releases every affected thumbnail immediately");
                Click(chooser, point); assert(selections == 1, "An excluded preview cannot select");
                exclusions.Clear(); chooser.Rebuild();
                int live = api.Live.Count; fixtures[1].Close(); Application.DoEvents(); chooser.RefreshPreviews();
                assert(api.Live.Count < live, "Closing a source releases its live thumbnail");
                chooser.Hide(); assert(api.Live.Count == 0, "Hide releases all remaining DWM registrations");
                chooser.Show(); Application.DoEvents(); chooser.Dispose();
                assert(api.Live.Count == 0, "Chooser disposal releases all re-created registrations");
            } finally {
                if (chooser != null) chooser.Dispose();
                foreach (var fixture in fixtures) fixture.Dispose();
            }
        }
    }
}
