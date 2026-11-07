using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal static class FocusIndicatorPaintTests
    {
        [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        static FocusIndicatorTarget Target(int dpi = 96, int corner = 8)
        {
            return new FocusIndicatorTarget {
                Identity = new FocusedWindowSnapshot { Window = new IntPtr(91), Root = new IntPtr(91), Process = 1,
                    Thread = 2, ProcessStarted = 3, Visible = true, ClassName = "Neutral paint fixture" },
                Frame = new Rectangle(33, -99, 240, 180), Monitors = new[] { new Rectangle(-500, -500, 1600, 1600) },
                Dpi = dpi, Corner = corner
            };
        }
        static int[] Pixels(Bitmap image)
        {
            var pixels = new int[image.Width * image.Height];
            BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try {
                for (int row = 0; row < image.Height; row++) Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * image.Width, image.Width);
            } finally { image.UnlockBits(data); }
            return pixels;
        }
        static int Alpha(int pixel) { return (int)((uint)pixel >> 24); }
        static bool Premultiplied(int[] pixels)
        {
            foreach (int pixel in pixels) {
                int alpha = Alpha(pixel);
                if (((pixel >> 16) & 255) > alpha || ((pixel >> 8) & 255) > alpha || (pixel & 255) > alpha || (alpha == 0 && pixel != 0)) return false;
            }
            return true;
        }
        static bool NativeShapeCovers(FocusIndicatorVisual visual, Bitmap image, int[] pixels)
        {
            using (Region shape = FocusIndicatorPaint.Shape(visual)) using (Graphics graphics = Graphics.FromImage(image)) {
                IntPtr native = shape.GetHrgn(graphics);
                try {
                    if (PtInRegion(native, image.Width / 2, image.Height / 2)) return false;
                    for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                        if (Alpha(pixels[y * image.Width + x]) != 0 && !PtInRegion(native, x, y)) return false;
                    return true;
                } finally { DeleteObject(native); }
            }
        }
        internal static void Run(Action<bool, string> check)
        {
            PalettesAndCoverage(check); ShapeAndSquare(check); FractionalCornerRegression(check); Reveal(check); MonitorClipping(check); InvalidInputs(check);
        }
        static void PalettesAndCoverage(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 144, 168, 192, 288, 384, 768 }) foreach (bool dark in new[] { false, true })
                foreach (string palette in FocusAppearance.Presets) {
                    var target = Target(dpi, (8 * dpi + 48) / 96);
                    var muted = FocusIndicatorVisual.Create(target, FocusIndicatorMode.MutedFrame, palette, dark);
                    var inset = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, palette, dark);
                    using (Bitmap first = FocusIndicatorPaint.Render(muted)) using (Bitmap second = FocusIndicatorPaint.Render(inset)) {
                        int[] a = Pixels(first), b = Pixels(second); bool equalAlpha = true, symmetric = true, smooth = false, straight = true;
                        for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++) {
                            int at = y * first.Width + x, alpha = Alpha(a[at]);
                            equalAlpha &= alpha == Alpha(b[at]);
                            symmetric &= alpha == Alpha(a[y * first.Width + first.Width - x - 1]) &&
                                alpha == Alpha(a[(first.Height - y - 1) * first.Width + x]);
                            smooth |= alpha > 0 && alpha < 255;
                        }
                        for (int y = 0; y < muted.Thickness; y++) {
                            straight &= a[y * first.Width + first.Width / 2] == muted.Outer.ToArgb();
                            straight &= b[y * first.Width + first.Width / 2] == (y < inset.OuterThickness ? inset.Outer : inset.Inner).ToArgb();
                        }
                        check(first.PixelFormat == PixelFormat.Format32bppPArgb && second.PixelFormat == PixelFormat.Format32bppPArgb &&
                            Premultiplied(a) && Premultiplied(b), "AA pixels are premultiplied BGRA without hidden RGB for " + dpi + "/" + dark + "/" + palette);
                        check(smooth && symmetric && equalAlpha, "Both rounded styles have symmetric fractional coverage and no inset transparency crack for " + dpi + "/" + dark + "/" + palette);
                        check(straight && Alpha(a[muted.Thickness * first.Width + first.Width / 2]) == 0 &&
                            Alpha(b[inset.Thickness * first.Width + first.Width / 2]) == 0 &&
                            a[(first.Height / 2) * first.Width + first.Width / 2] == 0 && b[(first.Height / 2) * first.Width + first.Width / 2] == 0,
                            "Both styles preserve the exact two-logical-pixel colors and zero-alpha client interior for " + dpi + "/" + dark + "/" + palette);
                    }
                }
        }
        static void ShapeAndSquare(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 144, 168, 192, 288, 384, 768 }) foreach (int corner in new[] { 0, (8 * dpi + 48) / 96 })
                foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge }) {
                    var visual = FocusIndicatorVisual.Create(Target(dpi, corner), mode, "Sky", true);
                    using (Bitmap image = FocusIndicatorPaint.Render(visual)) {
                        int[] pixels = Pixels(image); bool square = true, untouched = true;
                        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                            int alpha = Alpha(pixels[y * image.Width + x]);
                            if (corner == 0) square &= alpha == ((x < visual.Thickness || x >= image.Width - visual.Thickness ||
                                y < visual.Thickness || y >= image.Height - visual.Thickness) ? 255 : 0);
                            if (x > visual.Thickness + corner && x < image.Width - visual.Thickness - corner &&
                                y > visual.Thickness + corner && y < image.Height - visual.Thickness - corner) untouched &= alpha == 0;
                        }
                        check(NativeShapeCovers(visual, image, pixels) && untouched,
                            "Actual native HRGN retains every AA fringe pixel and remains deeply hollow at " + dpi + "/" + corner + "/" + mode);
                        check(square, "Square/maximized frame uses pixel-aligned exact thickness without AA growth at " + dpi + "/" + corner + "/" + mode);
                    }
                }
        }
        static void Reveal(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 168, 192 }) foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge }) {
                var visual = FocusIndicatorVisual.Create(Target(dpi, (8 * dpi + 48) / 96), mode, "Coral", false);
                int[] previous = null, complete;
                using (Bitmap settled = FocusIndicatorPaint.Render(visual)) complete = Pixels(settled);
                foreach (double progress in new[] { 0.0, .05, .25, .5, .75, .95, 1.0 }) {
                    using (Bitmap image = FocusIndicatorPaint.Render(visual, progress)) {
                        int[] pixels = Pixels(image); bool monotonic = true, symmetric = true, bounded = true, transparent = true, final = true;
                        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                            int at = y * image.Width + x, alpha = Alpha(pixels[at]);
                            if (previous != null) monotonic &= alpha >= Alpha(previous[at]);
                            symmetric &= alpha == Alpha(pixels[y * image.Width + image.Width - x - 1]);
                            bounded &= alpha <= Alpha(complete[at]); transparent &= pixels[at] == 0; final &= pixels[at] == complete[at];
                        }
                        check(monotonic && symmetric && bounded && Premultiplied(pixels) && NativeShapeCovers(visual, image, pixels),
                            "Perimeter reveal is monotonic, mirrored, coverage-bounded and retained by HRGN at " + dpi + "/" + mode + "/" + progress);
                        if (progress == 0) check(transparent, "Reveal starts entirely transparent with no start dot at " + dpi + "/" + mode);
                        if (progress > 0 && progress < 1) check(Alpha(pixels[image.Width / 2]) > 0 &&
                            Alpha(pixels[(image.Height - 1) * image.Width + image.Width / 2]) == 0,
                            "Reveal leaves from top center and has not reached bottom center at " + dpi + "/" + mode + "/" + progress);
                        if (progress == 1) check(final && Alpha(pixels[image.Width / 2]) == 255 &&
                            Alpha(pixels[(image.Height - 1) * image.Width + image.Width / 2]) == 255,
                            "Reveal completion exactly matches settled pixels with no top/bottom endpoint seam at " + dpi + "/" + mode);
                        previous = pixels;
                    }
                }
            }
        }
        static void FractionalCornerRegression(Action<bool, string> check)
        {
            foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge }) {
                var visual = FocusIndicatorVisual.Create(Target(120, 10), mode, "Original", false);
                using (Bitmap image = FocusIndicatorPaint.Render(visual)) using (Region shape = FocusIndicatorPaint.Shape(visual))
                    using (Graphics graphics = Graphics.FromImage(image)) {
                        IntPtr native = shape.GetHrgn(graphics);
                        try {
                            foreach (Point point in new[] { new Point(233, 175), new Point(235, 173), new Point(6, 175),
                                new Point(4, 173), new Point(233, 4), new Point(235, 6), new Point(6, 4), new Point(4, 6) })
                                check(image.GetPixel(point.X, point.Y).A == 12 && PtInRegion(native, point.X, point.Y),
                                    "125% DPI corner retains exact3/64 AA fringe coverage in pixels and actual HWND shape at " + mode + "/" + point);
                        } finally { DeleteObject(native); }
                    }
            }
        }
        static void MonitorClipping(Action<bool, string> check)
        {
            var stacked = Target(); stacked.Frame = new Rectangle(-90, -80, 300, 220);
            stacked.Monitors = new[] { new Rectangle(-400, -200, 400, 200), new Rectangle(0, 0, 400, 200) };
            foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge })
                foreach (double progress in new[] { .25, .8, 1.0 }) {
                    var visual = FocusIndicatorVisual.Create(stacked, mode, "Lavender", true);
                    using (Bitmap image = FocusIndicatorPaint.Render(visual, progress)) {
                        int[] pixels = Pixels(image); bool gaps = true;
                        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                            Point physical = new Point(x + visual.Bounds.X, y + visual.Bounds.Y);
                            bool onMonitor = false; foreach (Rectangle monitor in stacked.Monitors) onMonitor |= monitor.Contains(physical);
                            if (!onMonitor) gaps &= pixels[y * image.Width + x] == 0;
                        }
                        check(gaps && NativeShapeCovers(visual, image, pixels) && pixels[120 * image.Width + 90] == 0,
                            "Negative/stacked disconnected monitors retain transparent gaps and no invented seam edge at " + mode + "/" + progress);
                    }
                }
            var partlyVisible = Target(); partlyVisible.Frame = new Rectangle(-50, 100, 240, 180);
            partlyVisible.Monitors = new[] { new Rectangle(0, 0, 1920, 1080) };
            var clipped = FocusIndicatorVisual.Create(partlyVisible, FocusIndicatorMode.InsetEdge, "Peach", false);
            using (Bitmap image = FocusIndicatorPaint.Render(clipped)) {
                int[] pixels = Pixels(image);
                check(image.Width == 190 && pixels[90 * image.Width] == 0 && Alpha(pixels[90 * image.Width + image.Width - 1]) == 255 &&
                    NativeShapeCovers(clipped, image, pixels), "Offscreen clipping follows original rounded edges without painting the clipping rectangle");
            }
        }
        static void InvalidInputs(Action<bool, string> check)
        {
            var visual = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.MutedFrame, "Original", true);
            int[] complete;
            using (Bitmap image = FocusIndicatorPaint.Render(visual)) complete = Pixels(image);
            foreach (double value in new[] { Double.NaN, Double.NegativeInfinity, Double.PositiveInfinity, 2.0 })
                using (Bitmap image = FocusIndicatorPaint.Render(visual, value)) {
                    int[] pixels = Pixels(image); bool equal = pixels.Length == complete.Length;
                    for (int i = 0; equal && i < pixels.Length; i++) equal &= pixels[i] == complete[i];
                    check(equal, "Invalid/nonfinite or completed progress safely renders the full static frame " + value);
                }
            using (Bitmap image = FocusIndicatorPaint.Render(visual, -1)) {
                bool empty = true; foreach (int pixel in Pixels(image)) empty &= pixel == 0;
                check(empty, "Negative progress is clamped to a fully transparent start");
            }
            var invalid = new Action<FocusIndicatorVisual>[] {
                delegate(FocusIndicatorVisual v) { v.Bounds = new Rectangle(0, 0, 16000, 16000); },
                delegate(FocusIndicatorVisual v) { v.Bounds = new Rectangle(0, 0, 16385, 1); },
                delegate(FocusIndicatorVisual v) { v.Target.Monitors = null; },
                delegate(FocusIndicatorVisual v) { v.Thickness = Int32.MaxValue; },
                delegate(FocusIndicatorVisual v) { v.Mode = FocusIndicatorMode.Off; },
                delegate(FocusIndicatorVisual v) { v.Target.Frame = new Rectangle(Int32.MaxValue - 2, 0, 20, 20); }
            };
            for (int index = 0; index < invalid.Length; index++) {
                var changed = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.MutedFrame, "Original", true); invalid[index](changed);
                bool renderRefused = false, shapeRefused = false;
                try { using (Bitmap image = FocusIndicatorPaint.Render(changed)) { } } catch (ArgumentException) { renderRefused = true; }
                try { using (Region shape = FocusIndicatorPaint.Shape(changed)) { } } catch (ArgumentException) { shapeRefused = true; }
                check(renderRefused && shapeRefused, "Malformed geometry is refused before bitmap/native-region allocation " + index);
            }
        }
    }
}
