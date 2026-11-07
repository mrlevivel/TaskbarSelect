using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal static class FocusIndicatorGlowTests
    {
        [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        static FocusIndicatorTarget Target(int dpi = 96, int corner = 8)
        {
            return new FocusIndicatorTarget { Identity = new FocusedWindowSnapshot { Window = new IntPtr(91), Root = new IntPtr(91),
                    Process = 1, Thread = 2, ProcessStarted = 3, Visible = true, ClassName = "Neutral glow fixture" },
                Frame = new Rectangle(100, 200, 200, 160), Monitors = new[] { new Rectangle(-500, -500, 2000, 2000) }, Dpi = dpi, Corner = corner };
        }
        static int[] Pixels(Bitmap image)
        {
            int[] pixels = new int[image.Width * image.Height];
            BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < image.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * image.Width, image.Width); }
            finally { image.UnlockBits(data); }
            return pixels;
        }
        static int Alpha(int value) { return (int)((uint)value >> 24); }
        static bool Premultiplied(int[] pixels)
        {
            foreach (int pixel in pixels) {
                int alpha = Alpha(pixel);
                if (((pixel >> 16) & 255) > alpha || ((pixel >> 8) & 255) > alpha || (pixel & 255) > alpha || (alpha == 0 && pixel != 0)) return false;
            }
            return true;
        }
        static bool Same(int[] first, int[] second)
        { if (first.Length != second.Length) return false; for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false; return true; }
        static bool Covered(FocusIndicatorVisual visual, Bitmap image, int[] pixels)
        {
            using (Region shape = FocusIndicatorPaint.Shape(visual)) using (Graphics graphics = Graphics.FromImage(image)) {
                IntPtr native = shape.GetHrgn(graphics);
                try {
                    int centerX = visual.Target.Frame.Left + visual.Target.Frame.Width / 2 - visual.Bounds.Left;
                    int centerY = visual.Target.Frame.Top + visual.Target.Frame.Height / 2 - visual.Bounds.Top;
                    if (PtInRegion(native, centerX, centerY)) return false;
                    for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                        if (Alpha(pixels[y * image.Width + x]) > 0 && !PtInRegion(native, x, y)) return false;
                    return true;
                } finally { DeleteObject(native); }
            }
        }
        internal static void Run(Action<bool, string> check)
        { Matrix(check); Falloff(check); LightingDistinction(check); Reveal(check); Clipping(check); InvalidAndOff(check); }
        static void Matrix(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 144, 168, 192, 288, 384, 768 }) foreach (bool dark in new[] { false, true })
                foreach (string accent in FocusAppearance.Presets) foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge }) {
                    var target = Target(dpi, (8 * dpi + 48) / 96);
                    var normal = FocusIndicatorVisual.Create(target, mode, accent, dark);
                    var glow = FocusIndicatorVisual.Create(target, mode, accent, dark, true);
                    using (Bitmap original = FocusIndicatorPaint.Render(normal)) using (Bitmap image = FocusIndicatorPaint.Render(glow)) {
                        int[] before = Pixels(original), pixels = Pixels(image); bool core = true, body = true, symmetric = true;
                        int offsetX = normal.Bounds.Left - glow.Bounds.Left, offsetY = normal.Bounds.Top - glow.Bounds.Top;
                        for (int y = 0; y < original.Height; y++) for (int x = 0; x < original.Width; x++) {
                            int value = pixels[(y + offsetY) * image.Width + x + offsetX], old = before[y * original.Width + x];
                            if (Alpha(old) == 255) core &= value == old;
                            if (x > target.Corner + normal.Thickness && x < original.Width - target.Corner - normal.Thickness &&
                                y > target.Corner + normal.Thickness && y < original.Height - target.Corner - normal.Thickness) body &= value == 0;
                            core &= Alpha(value) >= Alpha(old);
                        }
                        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                            symmetric &= Alpha(pixels[y * image.Width + x]) == Alpha(pixels[y * image.Width + image.Width - x - 1]) &&
                                Alpha(pixels[y * image.Width + x]) == Alpha(pixels[(image.Height - y - 1) * image.Width + x]);
                        check(glow.GlowWidth == Math.Min(32, (7 * dpi + 48) / 96) && glow.Bounds == FocusIndicatorVisual.Expand(target.Frame, glow.GlowWidth) &&
                            glow.Thickness == normal.Thickness && core && body && pixels[(image.Height / 2) * image.Width + image.Width / 2] == 0,
                            "Glow preserves opaque two-pixel partitions and the hollow body at " + dpi + "/" + dark + "/" + accent + "/" + mode);
                        check(image.PixelFormat == PixelFormat.Format32bppPArgb && Premultiplied(pixels) && symmetric && Covered(glow, image, pixels),
                            "Halo is symmetric premultiplied alpha and its complete fringe survives actual HRGN at " + dpi + "/" + dark + "/" + accent + "/" + mode);
                    }
                }
        }
        static void Falloff(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 168, 192, 384, 768 }) foreach (bool dark in new[] { false, true })
                foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge }) {
                    var visual = FocusIndicatorVisual.Create(Target(dpi, 0), mode, "Sky", dark, true);
                    using (Bitmap image = FocusIndicatorPaint.Render(visual)) {
                        int[] pixels = Pixels(image); int x = image.Width / 2, width = visual.GlowWidth, prior = 255; bool fades = true;
                        for (int distance = 0; distance < width; distance++) {
                            int alpha = Alpha(pixels[(width - distance - 1) * image.Width + x]);
                            fades &= alpha <= prior && alpha <= visual.GlowColor.A; prior = alpha;
                        }
                        check(fades && prior <= 1 && Alpha(pixels[(width - 1) * image.Width + x]) > 0 &&
                            pixels[(width + visual.Thickness) * image.Width + x] == 0,
                            "Soft halo fades monotonically to zero outside, never fills the body, at " + dpi + "/" + dark + "/" + mode);
                        if (dpi == 96) {
                            int near = Alpha(pixels[(width - 1) * image.Width + x]);
                            int nearby = Alpha(pixels[(width - 3) * image.Width + x]);
                            int farther = Alpha(pixels[(width - 5) * image.Width + x]);
                            check(near >= (dark ? 75 : 60) && nearby >= (dark ? 35 : 28) && farther >= (dark ? 10 : 8) &&
                                near > nearby && nearby > farther,
                                "Soft glow is visibly present next to the frame and several pixels beyond, with a gentle fade at " + dark + "/" + mode);
                        }
                    }
                }
            var rounded = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.InsetEdge, "Mint", true, true);
            var noGlow = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.InsetEdge, "Mint", true);
            using (Bitmap image = FocusIndicatorPaint.Render(rounded)) using (Bitmap normal = FocusIndicatorPaint.Render(noGlow))
                check(normal.GetPixel(0, 0).A == 0 && image.GetPixel(rounded.GlowWidth, rounded.GlowWidth).A > 0 &&
                    image.GetPixel(rounded.GlowWidth, rounded.GlowWidth).A <= rounded.GlowColor.A,
                    "Rounded silhouette glow naturally reaches the outside corner triangle instead of a square bounding-box halo");
        }
        static void LightingDistinction(Action<bool, string> check)
        {
            foreach (bool dark in new[] { false, true }) foreach (string accent in FocusAppearance.Presets) {
                var target = Target(96, 0);
                var none = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, accent, dark, false, (int)FocusLightingMode.None);
                var soft = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, accent, dark, false, (int)FocusLightingMode.SubtleGlow);
                var lit = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, accent, dark, false, (int)FocusLightingMode.Illuminated);
                using (Bitmap plain = FocusIndicatorPaint.Render(none)) using (Bitmap halo = FocusIndicatorPaint.Render(soft))
                    using (Bitmap ridge = FocusIndicatorPaint.Render(lit)) {
                    int x = target.Frame.Width / 2;
                    bool sameCore = true;
                    for (int y = 0; y < none.Thickness; y++)
                        sameCore &= plain.GetPixel(x, y).ToArgb() == halo.GetPixel(x + soft.GlowWidth, y + soft.GlowWidth).ToArgb();
                    check(none.GlowWidth == 0 && plain.Height == target.Frame.Height &&
                        halo.GetPixel(x + soft.GlowWidth, soft.GlowWidth - 2).A >= (dark ? 50 : 40) && sameCore &&
                        halo.GetPixel(x + soft.GlowWidth, soft.GlowWidth).ToArgb() != ridge.GetPixel(x + lit.GlowWidth, lit.GlowWidth).ToArgb(),
                        "None is an unlit outline, Soft glow adds a visible halo without changing its core, and Illuminated adds a distinct lit ridge at " + dark + "/" + accent);
                }
            }
        }
        static void Reveal(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 168, 768 }) foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge }) {
                var visual = FocusIndicatorVisual.Create(Target(dpi, (8 * dpi + 48) / 96), mode, "Lavender", true, true);
                int[] previous = null, final;
                using (Bitmap image = FocusIndicatorPaint.Render(visual)) final = Pixels(image);
                foreach (double progress in new[] { 0.0, .1, .4, .75, .95, 1.0 }) using (Bitmap image = FocusIndicatorPaint.Render(visual, progress)) {
                    int[] pixels = Pixels(image); bool monotonic = true, bounded = true, mirrored = true, empty = true;
                    for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                        int at = y * image.Width + x, alpha = Alpha(pixels[at]);
                        if (previous != null) monotonic &= alpha >= Alpha(previous[at]);
                        bounded &= alpha <= Alpha(final[at]); empty &= pixels[at] == 0;
                        mirrored &= alpha == Alpha(pixels[y * image.Width + image.Width - x - 1]);
                    }
                    check(monotonic && bounded && mirrored && Premultiplied(pixels) && Covered(visual, image, pixels) &&
                        (progress != 0 || empty) && (progress != 1 || Same(pixels, final)),
                        "Glow follows the same monotonic mirrored perimeter reveal with exact settled pixels at " + dpi + "/" + mode + "/" + progress);
                    previous = pixels;
                }
                var motion = new FocusIndicatorMotion(delegate { return true; }); motion.Show(1000); motion.Value(1060); motion.Hide();
                using (Bitmap cancelled = FocusIndicatorPaint.Render(visual, motion.Value(1080))) {
                    bool empty = true; foreach (int pixel in Pixels(cancelled)) empty &= pixel == 0;
                    check(!motion.Active && empty, "Cancelled motion produces no lingering halo or frame pixels at " + dpi + "/" + mode);
                }
            }
        }
        static void Clipping(Action<bool, string> check)
        {
            var target = Target(); target.Frame = new Rectangle(-90, -80, 300, 220);
            target.Monitors = new[] { new Rectangle(-400, -200, 400, 200), new Rectangle(0, 0, 400, 200) };
            foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge })
                foreach (double progress in new[] { .4, .8, 1.0 }) {
                    var visual = FocusIndicatorVisual.Create(target, mode, "Forest Green", false, true);
                    using (Bitmap image = FocusIndicatorPaint.Render(visual, progress)) {
                        int[] pixels = Pixels(image); bool gaps = true;
                        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                            Point physical = new Point(x + visual.Bounds.Left, y + visual.Bounds.Top);
                            bool onMonitor = false; foreach (Rectangle monitor in target.Monitors) onMonitor |= monitor.Contains(physical);
                            if (!onMonitor) gaps &= pixels[y * image.Width + x] == 0;
                        }
                        check(gaps && Covered(visual, image, pixels) && pixels[(120 - visual.Bounds.Top) * image.Width - visual.Bounds.Left] == 0,
                            "Glow/AA clipping preserves negative stacked monitor gaps and invents no seam at " + mode + "/" + progress);
                    }
                }
            var offscreen = Target(); offscreen.Frame = new Rectangle(-50, 100, 240, 180);
            offscreen.Monitors = new[] { new Rectangle(0, 0, 1920, 1080) };
            var clipped = FocusIndicatorVisual.Create(offscreen, FocusIndicatorMode.InsetEdge, "Peach", true, true);
            using (Bitmap image = FocusIndicatorPaint.Render(clipped))
                check(clipped.Bounds.Left == 0 && image.GetPixel(0, 90).A == 0 && Covered(clipped, image, Pixels(image)),
                    "A partly offscreen halo follows original window edges without drawing a false clipped edge");
        }
        static void InvalidAndOff(Action<bool, string> check)
        {
            var target = Target();
            foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge }) {
                var defaultVisual = FocusIndicatorVisual.Create(target, mode, "Original", true);
                var disabled = FocusIndicatorVisual.Create(target, mode, "Original", true, false);
                disabled.GlowColor = Color.FromArgb(32, Color.Coral);
                using (Bitmap expected = FocusIndicatorPaint.Render(defaultVisual)) using (Bitmap actual = FocusIndicatorPaint.Render(disabled))
                    check(defaultVisual.GlowWidth == 0 && defaultVisual.Bounds == target.Frame && Same(Pixels(expected), Pixels(actual)),
                        "Disabled glow preserves exact existing bounds/pixels and ignores a leftover tint at " + mode);
                var silent = FocusIndicatorVisual.Create(target, mode, "Original", true, true); silent.GlowColor = Color.Transparent;
                using (Bitmap normal = FocusIndicatorPaint.Render(defaultVisual)) using (Bitmap expanded = FocusIndicatorPaint.Render(silent)) {
                    int[] pixels = Pixels(expanded), basePixels = Pixels(normal); bool equal = true, padding = true;
                    for (int y = 0; y < expanded.Height; y++) for (int x = 0; x < expanded.Width; x++) {
                        int localX = x - silent.GlowWidth, localY = y - silent.GlowWidth;
                        bool inside = localX >= 0 && localY >= 0 && localX < normal.Width && localY < normal.Height;
                        if (inside) equal &= pixels[y * expanded.Width + x] == basePixels[localY * normal.Width + localX];
                        else padding &= pixels[y * expanded.Width + x] == 0;
                    }
                    check(equal && padding, "An alpha-zero tint safely renders only the unchanged original frame at " + mode);
                }
            }
            var invalid = new Action<FocusIndicatorVisual>[] {
                delegate(FocusIndicatorVisual v) { v.GlowWidth = -1; }, delegate(FocusIndicatorVisual v) { v.GlowWidth = 33; },
                delegate(FocusIndicatorVisual v) { v.Bounds = FocusIndicatorVisual.Expand(v.Target.Frame, v.GlowWidth + 1); },
                delegate(FocusIndicatorVisual v) { v.GlowWidth = 0; },
                delegate(FocusIndicatorVisual v) { v.Bounds = new Rectangle(0, 0, 16000, 16000); },
                delegate(FocusIndicatorVisual v) { v.Target.Frame = new Rectangle(Int32.MaxValue - 202, 0, 200, 160); }
            };
            for (int i = 0; i < invalid.Length; i++) {
                var visual = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, "Sky", true, true); invalid[i](visual);
                bool pixelsRefused = false, shapeRefused = false;
                try { using (Bitmap image = FocusIndicatorPaint.Render(visual)) { } } catch (ArgumentException) { pixelsRefused = true; }
                try { using (Region shape = FocusIndicatorPaint.Shape(visual)) { } } catch (ArgumentException) { shapeRefused = true; }
                check(pixelsRefused && shapeRefused, "Malformed halo extent/bounds is rejected before allocation " + i);
            }
        }
    }
}
