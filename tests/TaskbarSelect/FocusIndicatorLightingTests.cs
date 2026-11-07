using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal static class FocusIndicatorLightingTests
    {
        [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        static FocusIndicatorTarget Target(int dpi = 96, int corner = 8)
        {
            return new FocusIndicatorTarget { Identity = new FocusedWindowSnapshot { Window = new IntPtr(91), Root = new IntPtr(91),
                    Process = 1, Thread = 2, ProcessStarted = 3, Visible = true, ClassName = "Neutral illuminated fixture" },
                Frame = new Rectangle(100, 200, 260, 220), Monitors = new[] { new Rectangle(-500, -500, 2000, 2000) }, Dpi = dpi, Corner = corner };
        }
        static FocusIndicatorVisual Visual(FocusIndicatorTarget target, FocusIndicatorMode mode, string accent, bool dark, FocusLightingMode lighting)
        { return FocusIndicatorVisual.Create(target, mode, accent, dark, false, (int)lighting); }
        static int[] Pixels(Bitmap image)
        {
            var values = new int[image.Width * image.Height];
            BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < image.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), values, y * image.Width, image.Width); }
            finally { image.UnlockBits(data); }
            return values;
        }
        static int Alpha(int value) { return (int)((uint)value >> 24); }
        static int Light(int value) { return ((value >> 16) & 255) * 3 + ((value >> 8) & 255) * 6 + (value & 255); }
        static bool Same(int[] first, int[] second)
        { if (first.Length != second.Length) return false; for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false; return true; }
        static bool Premultiplied(int[] pixels)
        {
            foreach (int value in pixels) {
                int alpha = Alpha(value);
                if (((value >> 16) & 255) > alpha || ((value >> 8) & 255) > alpha || (value & 255) > alpha || (alpha == 0 && value != 0)) return false;
            }
            return true;
        }
        static bool Covered(FocusIndicatorVisual visual, Bitmap image, int[] values)
        {
            using (Region shape = FocusIndicatorPaint.Shape(visual)) using (Graphics graphics = Graphics.FromImage(image)) {
                IntPtr native = shape.GetHrgn(graphics);
                try {
                    if (PtInRegion(native, image.Width / 2, image.Height / 2)) return false;
                    for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                        if (Alpha(values[y * image.Width + x]) > 0 && !PtInRegion(native, x, y)) return false;
                    return true;
                } finally { DeleteObject(native); }
            }
        }
        internal static void Run(Action<bool, string> check)
        { Matrix(check); Flow(check); RevealAndCache(check); Clipping(check); LegacyAndInvalid(check); }
        static void Matrix(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 192, 768 }) foreach (bool dark in new[] { false, true })
                foreach (string accent in FocusAppearance.Presets) foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge }) {
                    var target = Target(dpi, (8 * dpi + 48) / 96);
                    var steady = Visual(target, mode, accent, dark, FocusLightingMode.Illuminated);
                    var flowing = Visual(target, mode, accent, dark, FocusLightingMode.EnergyFlow);
                    using (var lit = new FocusIndicatorLightingRenderer(steady)) using (var flow = new FocusIndicatorLightingRenderer(flowing)) {
                        Bitmap first = lit.Render(1, 0), second = flow.Render(1, .37);
                        int[] a = Pixels(first), b = Pixels(second); bool body = true, symmetric = true, equalAlpha = true;
                        int offset = steady.GlowWidth, peak = dark ? 96 : 68;
                        for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++) {
                            int at = y * first.Width + x, alpha = Alpha(a[at]);
                            equalAlpha &= alpha == Alpha(b[at]);
                            symmetric &= alpha == Alpha(a[y * first.Width + first.Width - x - 1]) && alpha == Alpha(a[(first.Height - y - 1) * first.Width + x]);
                            if (x > offset + target.Corner + steady.Thickness && x < first.Width - offset - target.Corner - steady.Thickness &&
                                y > offset + target.Corner + steady.Thickness && y < first.Height - offset - target.Corner - steady.Thickness) body &= a[at] == 0 && b[at] == 0;
                        }
                        int center = first.Width / 2;
                        int ridge = a[offset * first.Width + center], inner = a[(offset + steady.Thickness - 1) * first.Width + center];
                        check(steady.GlowWidth == (6 * dpi + 48) / 96 && steady.Bounds == FocusIndicatorVisual.Expand(target.Frame, steady.GlowWidth) &&
                            steady.Thickness == (2 * dpi + 48) / 96 && Alpha(ridge) == 255 && Alpha(inner) == 255 && Light(ridge) > Light(inner) &&
                            a[(offset + steady.Thickness) * first.Width + center] == 0 && body &&
                            Alpha(a[(offset - 1) * first.Width + center]) > 32 && Alpha(a[(offset - 1) * first.Width + center]) <= peak,
                            "Illuminated edge has a pale fine ridge, accent body, bounded soft halo and exact hollow two-DIP core at " + dpi + "/" + dark + "/" + accent + "/" + mode);
                        check(first.PixelFormat == PixelFormat.Format32bppPArgb && second.PixelFormat == PixelFormat.Format32bppPArgb &&
                            Premultiplied(a) && Premultiplied(b) && symmetric && equalAlpha && Covered(steady, first, a) && Covered(flowing, second, b),
                            "Premium lighting preserves symmetric AA coverage and native hollow region; energy changes local light without opacity pulsing at " + dpi + "/" + dark + "/" + accent + "/" + mode);
                    }
                }
        }
        static void Flow(Action<bool, string> check)
        {
            var target = Target(); target.Frame = new Rectangle(100, 200, 260, 260);
            var steady = Visual(target, FocusIndicatorMode.InsetEdge, "Blaze Orange", true, FocusLightingMode.Illuminated);
            var visual = Visual(target, FocusIndicatorMode.InsetEdge, "Blaze Orange", true, FocusLightingMode.EnergyFlow);
            using (var renderer = new FocusIndicatorLightingRenderer(visual)) using (var baseline = new FocusIndicatorLightingRenderer(steady)) {
                int[] basePixels = Pixels(baseline.Render(1, .75)), first = Pixels(renderer.Render(1, 0));
                int width = visual.Bounds.Width, offset = visual.GlowWidth;
                int[] cardinal = { offset * width + width / 2, (width / 2) * width + width - offset - 1,
                    (width - offset - 1) * width + width / 2, (width / 2) * width + offset };
                for (int part = 0; part < 4; part++) {
                    int[] values = Pixels(renderer.Render(1, part / 4.0));
                    int changed = 0, solid = 0, difference = 0;
                    for (int i = 0; i < values.Length; i++) {
                        if (Alpha(basePixels[i]) == 255) { solid++; if (values[i] != basePixels[i]) changed++; }
                        difference = Math.Max(difference, Light(values[i]) - Light(basePixels[i]));
                    }
                    check(Light(values[cardinal[part]]) > Light(values[cardinal[(part + 2) % 4]]) && difference > 100 && changed < solid * .70,
                        "A localized broad bright head advances clockwise without brightening the entire frame at phase " + part + "/4");
                    check(Premultiplied(values) && Covered(visual, renderer.Render(1, part / 4.0), values),
                        "Energy flow keeps premultiplied pixels and existing hollow/clipped native shape at phase " + part + "/4");
                }
                check(Same(first, Pixels(renderer.Render(1, 1))) && Same(first, Pixels(renderer.Render(1, Double.NaN))) &&
                    Same(first, Pixels(renderer.Render(1, Double.PositiveInfinity))) &&
                    Same(Pixels(renderer.Render(1, .75)), Pixels(renderer.Render(1, -.25))) &&
                    Same(Pixels(renderer.Render(1, .25)), Pixels(renderer.Render(1, 1024.25))),
                    "Energy phase wraps seamlessly, supports negative and large phases, and handles invalid phase without stale pixels");
                int[] before = Pixels(renderer.Render(1, 1 - .000001)), after = Pixels(renderer.Render(1, .000001)); bool seamless = true;
                for (int i = 0; i < before.Length; i++) seamless &= Alpha(before[i]) == Alpha(after[i]) && Math.Abs(Light(before[i]) - Light(after[i])) <= 10;
                check(seamless, "Flow head and trailing light remain continuous across the top-center loop seam");
                check(Same(basePixels, Pixels(baseline.Render(1, 0))) && Same(basePixels, Pixels(baseline.Render(1, .5))),
                    "Illuminated mode remains completely still at every phase");
            }
        }
        static void RevealAndCache(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 768 }) foreach (FocusLightingMode mode in new[] { FocusLightingMode.Illuminated, FocusLightingMode.EnergyFlow }) {
                var visual = Visual(Target(dpi, (8 * dpi + 48) / 96), FocusIndicatorMode.MutedFrame, "Mint", true, mode);
                var renderer = new FocusIndicatorLightingRenderer(visual);
                try {
                    Bitmap image = renderer.Render(1, .41); int[] final = Pixels(image), previous = null;
                    foreach (double progress in new[] { 0.0, .05, .2, .5, .8, .95, 1.0 }) {
                        Bitmap current = renderer.Render(progress, .41); int[] values = Pixels(current);
                        bool monotonic = true, bounded = true, symmetric = true, empty = true;
                        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                            int at = y * image.Width + x, alpha = Alpha(values[at]);
                            if (previous != null) monotonic &= alpha >= Alpha(previous[at]);
                            bounded &= alpha <= Alpha(final[at]); empty &= values[at] == 0;
                            symmetric &= alpha == Alpha(values[y * image.Width + image.Width - x - 1]);
                        }
                        check(Object.ReferenceEquals(image, current) && monotonic && bounded && symmetric && Premultiplied(values) &&
                            Covered(visual, current, values) && (progress != 0 || empty) && (progress != 1 || Same(values, final)),
                            "Premium cached lighting shares the monotonic AA perimeter reveal and exact settled image at " + dpi + "/" + mode + "/" + progress);
                        previous = values;
                    }
                    check(renderer.Size == visual.Bounds.Size && renderer.CachedPixels > 0 && renderer.CachedPixels < image.Width * image.Height &&
                        Same(final, Pixels(renderer.Render(Double.NaN, .41))),
                        "Lighting cache stores only the clipped perimeter band and normalizes progress without replacing its image at " + dpi + "/" + mode);
                    for (int frame = 0; frame < 120; frame++)
                        if (!Object.ReferenceEquals(image, renderer.Render(1, frame / 120.0))) throw new InvalidOperationException("Lighting cache replaced its upload image.");
                    check(Object.ReferenceEquals(image, renderer.Render(0, .41)) && Same(final, Pixels(renderer.Render(1, .41))),
                        "Repeated motion ticks and cancellation reuse one bitmap and do not retain stale reveal pixels at " + dpi + "/" + mode);
                } finally { renderer.Dispose(); renderer.Dispose(); }
                bool disposed = false; try { renderer.Render(1, 0); } catch (ObjectDisposedException) { disposed = true; }
                check(disposed, "Disposed lighting cache rejects late ticks and repeated disposal is safe at " + dpi + "/" + mode);
            }
        }
        static void Clipping(Action<bool, string> check)
        {
            var target = Target(); target.Frame = new Rectangle(-90, -80, 300, 220);
            target.Monitors = new[] { new Rectangle(-400, -200, 400, 200), new Rectangle(0, 0, 400, 200) };
            foreach (FocusLightingMode mode in new[] { FocusLightingMode.Illuminated, FocusLightingMode.EnergyFlow }) {
                var visual = Visual(target, FocusIndicatorMode.InsetEdge, "Forest Green", false, mode);
                using (var renderer = new FocusIndicatorLightingRenderer(visual)) foreach (double progress in new[] { .4, .8, 1.0 }) {
                    Bitmap image = renderer.Render(progress, .68); int[] values = Pixels(image); bool clips = true;
                    for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                        Point physical = new Point(x + visual.Bounds.Left, y + visual.Bounds.Top);
                        bool onMonitor = false; foreach (Rectangle monitor in target.Monitors) onMonitor |= monitor.Contains(physical);
                        if (!onMonitor) clips &= values[y * image.Width + x] == 0;
                    }
                    check(clips && Premultiplied(values) && Covered(visual, image, values),
                        "Premium illumination/reveal clips monitor gaps and stacked negative coordinates without inventing seam edges at " + mode + "/" + progress);
                }
            }
            var clipped = Target(); clipped.Frame = new Rectangle(-50, 100, 240, 180); clipped.Monitors = new[] { new Rectangle(0, 0, 1920, 1080) };
            var offscreen = Visual(clipped, FocusIndicatorMode.MutedFrame, "Peach", true, FocusLightingMode.EnergyFlow);
            using (var renderer = new FocusIndicatorLightingRenderer(offscreen)) {
                Bitmap image = renderer.Render(1, .7);
                check(offscreen.Bounds.Left == 0 && image.GetPixel(0, image.Height / 2).A == 0 && Covered(offscreen, image, Pixels(image)),
                    "Partly offscreen premium frame follows the original silhouette instead of drawing a false edge at the monitor crop");
            }
            var unpadded = Visual(Target(), FocusIndicatorMode.InsetEdge, "Coral", true, FocusLightingMode.EnergyFlow);
            unpadded.GlowWidth = 0; unpadded.GlowColor = Color.Empty; unpadded.Bounds = unpadded.Target.Frame;
            using (var renderer = new FocusIndicatorLightingRenderer(unpadded)) {
                Bitmap image = renderer.Render(1, .1);
                check(image.GetPixel(image.Width / 2, 0).A == 255 && image.GetPixel(image.Width / 2, image.Height / 2).A == 0,
                    "Allocation padding fallback preserves the illuminated core and flowing light while omitting the halo");
            }
        }
        static void LegacyAndInvalid(Action<bool, string> check)
        {
            foreach (bool glow in new[] { false, true }) {
                var legacy = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.InsetEdge, "Sky", true, glow);
                foreach (double progress in new[] { 0.0, .35, 1.0 }) using (Bitmap first = FocusIndicatorPaint.Render(legacy, progress))
                    using (Bitmap second = FocusIndicatorPaint.Render(legacy, progress, .57))
                        check(Same(Pixels(first), Pixels(second)), "New phase overload leaves existing unlit/subtle appearance bit-identical at " + glow + "/" + progress);
                bool refused = false; try { using (var renderer = new FocusIndicatorLightingRenderer(legacy)) { } } catch (ArgumentException) { refused = true; }
                check(refused, "Reusable lighting renderer refuses a legacy visual instead of silently changing it at " + glow);
            }
            var invalid = Visual(Target(), FocusIndicatorMode.MutedFrame, "Mint", true, FocusLightingMode.Illuminated); invalid.GlowWidth = 49;
            bool rejected = false; try { using (var renderer = new FocusIndicatorLightingRenderer(invalid)) { } } catch (ArgumentException) { rejected = true; }
            check(rejected, "Premium cache rejects glow geometry beyond the DPI-bounded 48-pixel maximum");
            check(FocusIndicatorLightingRenderer.NormalizePhase(1) == 0 && FocusIndicatorLightingRenderer.NormalizePhase(-.25) == .75 &&
                FocusIndicatorLightingRenderer.NormalizePhase(Double.NaN) == 0,
                "Phase normalization is deterministic for wrap, negative phase and unavailable animation time");
        }
    }
}
