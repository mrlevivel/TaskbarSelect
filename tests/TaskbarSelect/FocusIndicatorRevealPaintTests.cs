using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    // Offscreen images only: no windows, desktop input, profiles or settings.
    internal static class FocusIndicatorRevealPaintTests
    {
        [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        static FocusIndicatorTarget Target(int dpi = 96, int corner = 8)
        {
            return new FocusIndicatorTarget {
                Identity = new FocusedWindowSnapshot { Window = new IntPtr(59), Root = new IntPtr(59), Process = 2,
                    Thread = 3, ProcessStarted = 4, Visible = true, ClassName = "Neutral tapered reveal fixture" },
                Frame = new Rectangle(30, 40, 160, 120), Dpi = dpi, Corner = corner,
                Monitors = new[] { new Rectangle(-1000, -1000, 4000, 4000) }
            };
        }
        static FocusIndicatorVisual Visual(FocusIndicatorTarget target, string accent, int finish, int lighting, bool dark = true)
        { return FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, accent, dark, false, lighting, finish); }
        static int[] Pixels(Bitmap image)
        {
            int width = image.Width, height = image.Height;
            var values = new int[width * height];
            var data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), values, y * width, width); }
            finally { image.UnlockBits(data); }
            return values;
        }
        static int Alpha(int pixel) { return (int)((uint)pixel >> 24); }
        static int Light(int pixel) { return (((pixel >> 16) & 255) * 3 + ((pixel >> 8) & 255) * 6 + (pixel & 255)) / 10; }
        static int At(FocusIndicatorVisual visual, int[] values, int physicalX, int physicalY)
        { return values[(physicalY - visual.Bounds.Y) * visual.Bounds.Width + physicalX - visual.Bounds.X]; }
        static bool Same(int[] first, int[] second) { return first.SequenceEqual(second); }
        static bool SameAlpha(int[] first, int[] second)
        { for (int i = 0; i < first.Length; i++) if (Alpha(first[i]) != Alpha(second[i])) return false; return true; }
        static bool Safe(FocusIndicatorVisual visual, Bitmap image, int[] values)
        {
            int width = image.Width, height = image.Height;
            using (Region region = FocusIndicatorPaint.Shape(visual)) using (Graphics graphics = Graphics.FromImage(image)) {
                IntPtr native = region.GetHrgn(graphics);
                try {
                    // Test the same integer pixel membership Windows retains
                    // for the HWND. GDI+ float-point queries can round a pixel
                    // center on the right/bottom boundary out of that region.
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
                        int pixel = values[y * width + x], alpha = Alpha(pixel);
                        if (((pixel >> 16) & 255) > alpha || ((pixel >> 8) & 255) > alpha || (pixel & 255) > alpha ||
                            (alpha == 0 && pixel != 0) || (alpha > 0 && !PtInRegion(native, x, y))) return false;
                        if (alpha > 0 && !visual.Target.Monitors.Any(m => m.Contains(x + visual.Bounds.X, y + visual.Bounds.Y))) return false;
                    }
                } finally { DeleteObject(native); }
            }
            Rectangle frame = FocusIndicatorPaint.LocalFrame(visual);
            return values[(frame.Top + frame.Height / 2) * width + frame.Left + frame.Width / 2] == 0;
        }
        static bool Mirrors(int[] values, int width, int height)
        {
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                if (values[y * width + x] != values[y * width + width - x - 1]) return false;
            return true;
        }
        static bool Between(int[] unlit, int[] half, int[] full)
        {
            for (int i = 0; i < unlit.Length; i++) for (int shift = 0; shift <= 16; shift += 8) {
                int a = (unlit[i] >> shift) & 255, b = (half[i] >> shift) & 255, c = (full[i] >> shift) & 255;
                if (a > b || b > c) return false;
            }
            return true;
        }
        internal static void Run(Action<bool, string> check)
        { Taper(check); PaletteAndSettling(check); CornersAndClipping(check); CachedAndInvalid(check); }

        static void Taper(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 144, 192, 288, 384 }) foreach (int finish in new[] { 0, 1 })
                foreach (int lighting in new[] { 0, 1, 2 }) {
                    double scale = dpi / 96.0;
                    var target = Target(dpi, 0);
                    target.Frame = new Rectangle(40, 40, (int)Math.Round(320 * scale), (int)Math.Round(240 * scale));
                    var visual = Visual(target, "Forest Green", finish, lighting);
                    var perimeter = new FocusIndicatorPaint.Perimeter(FocusIndicatorPaint.LocalFrame(visual), visual.Thickness, 0);
                    double front = perimeter.HalfLength * .20;
                    using (Bitmap baseline = FocusIndicatorPaint.Render(visual, .20, 0))
                        using (Bitmap lit = FocusIndicatorPaint.Render(visual, .20, 0, 1)) {
                            int[] before = Pixels(baseline), after = Pixels(lit);
                            Func<double, int> lift = delegate(double behind) {
                                int x = (int)Math.Floor(target.Frame.Left + target.Frame.Width / 2.0 + front - behind * scale);
                                int y = target.Frame.Top + Math.Max(0, visual.Thickness / 2 - 1);
                                return Light(At(visual, after, x, y)) - Light(At(visual, before, x, y));
                            };
                            int peak = lift(8), early = lift(2), middle = lift(30), tail = lift(50);
                            check(peak > early && peak > middle && middle > tail && lift(58) == 0 && peak >= 5,
                                "A narrow light has a distinct smooth peak eight DIP behind the front and fades by56DIP at " + dpi + "/" + finish + "/" + lighting);
                            // Fractional DPI makes one-DIP sampling alternate
                            // one/two physical pixels. A per-pixel slope bound
                            // must inspect consecutive pixels, not that sparse
                            // resampled ramp, while DIP peak/fade checks above
                            // continue to verify the intended logical lengths.
                            int firstX = (int)Math.Floor(target.Frame.Left + target.Frame.Width / 2.0 + front - 2 * scale);
                            int lastX = (int)Math.Floor(target.Frame.Left + target.Frame.Width / 2.0 + front - 55 * scale);
                            int row = target.Frame.Top + Math.Max(0, visual.Thickness / 2 - 1);
                            int[] ramp = Enumerable.Range(lastX, firstX - lastX + 1).Select(x =>
                                Light(At(visual, after, x, row)) - Light(At(visual, before, x, row))).ToArray();
                            int levels = ramp.Distinct().Count(), maxStep = ramp.Zip(ramp.Skip(1), (a, b) => Math.Abs(a - b)).Max();
                            check(levels >= 7 && maxStep <= 24,
                                "Brightness changes through many levels without a flat bright dash or abrupt cutoff at " + dpi + "/" + finish + "/" + lighting +
                                "; consecutive physical pixels=" + ramp.Length + "; levels=" + levels + "; maxStep=" + maxStep);
                            check(SameAlpha(before, after) && Mirrors(after, lit.Width, lit.Height) && Safe(visual, lit, after),
                                "Mirrored reveal keeps the exact stroke/halo alpha, hollow center and monitor shape at " + dpi + "/" + finish + "/" + lighting);
                        }
                }
            // The same physical-distance profile has a continuous slope at its
            // ends; the image checks above verify that it reaches real pixels.
            double[] distances = { -.001, 0, .001, 7.999, 8, 8.001, 55.999, 56, 56.001 };
            double[] weights = distances.Select(d => FocusIndicatorPaint.RevealWeight(.5, 200 - d, 400, 1)).ToArray();
            check(weights[0] == 0 && weights[1] == 0 && weights[2] < .000001 &&
                weights[3] > .999999 && weights[4] == 1 && weights[5] > .999999 &&
                weights[6] < .000001 && weights[7] == 0 && weights[8] == 0,
                "The profile smoothly reaches zero at both ends and has no brightness jump at its peak");
        }

        static void PaletteAndSettling(Action<bool, string> check)
        {
            foreach (string accent in FocusAppearance.Presets) foreach (bool dark in new[] { false, true })
                foreach (int finish in new[] { 0, 1 }) for (int lighting = 0; lighting <= 3; lighting++) {
                    var visual = Visual(Target(), accent, finish, lighting, dark);
                    const double phase = .31;
                    using (Bitmap old = FocusIndicatorPaint.Render(visual, 1, phase))
                        using (Bitmap full = FocusIndicatorPaint.Render(visual, 1, phase, 1))
                        using (Bitmap half = FocusIndicatorPaint.Render(visual, 1, phase, .5))
                        using (Bitmap settled = FocusIndicatorPaint.Render(visual, 1, phase, 0)) {
                            int[] baseline = Pixels(old), bright = Pixels(full), fading = Pixels(half), final = Pixels(settled);
                            check(Same(baseline, final) && SameAlpha(baseline, bright) && SameAlpha(baseline, fading) &&
                                Between(baseline, fading, bright) && Safe(visual, full, bright),
                                "Settling fades only local brightness and returns byte-for-byte to released pixels at " + accent + "/" + dark + "/" + finish + "/" + lighting);
                            check(!Same(baseline, bright) && (lighting == 3 || Mirrors(bright, full.Width, full.Height)),
                                "Every selected palette receives the tapered cue, with one mirrored light and no additive meet-point at " + accent + "/" + dark + "/" + finish + "/" + lighting);
                        }
                }
        }

        static void CornersAndClipping(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 192, 288, 384 }) foreach (int finish in new[] { 0, 1 })
                for (int lighting = 0; lighting <= 3; lighting++) foreach (string geometry in new[] { "round", "tiny", "clipped" }) {
                    var target = Target(dpi, (12 * dpi + 48) / 96);
                    if (geometry == "tiny") target.Frame = new Rectangle(30, 40, 27, 25);
                    if (geometry == "clipped") {
                        target.Frame = new Rectangle(-60, -30, 160, 120);
                        target.Monitors = new[] { new Rectangle(-200, -200, 150, 400), new Rectangle(-20, -100, 200, 300) };
                    }
                    var visual = Visual(target, "Sand", finish, lighting);
                    foreach (double progress in new[] { .03, .50, .94, 1.0 }) {
                        using (Bitmap baseline = FocusIndicatorPaint.Render(visual, progress, .27))
                            using (Bitmap image = FocusIndicatorPaint.Render(visual, progress, .27, 1)) {
                                int[] before = Pixels(baseline), after = Pixels(image);
                                check(SameAlpha(before, after) && Safe(visual, image, after),
                                    "The taper follows AA corners/tiny contours and actual clipped edges without widening or invented monitor seams at " + dpi + "/" + finish + "/" + lighting + "/" + geometry + "/" + progress);
                            }
                    }
                }
        }

        static void CachedAndInvalid(Action<bool, string> check)
        {
            foreach (int finish in new[] { 0, 1 }) foreach (int lighting in new[] { 2, 3 }) {
                var visual = Visual(Target(144, 12), "Coral", finish, lighting);
                using (var renderer = new FocusIndicatorLightingRenderer(visual)) {
                    Bitmap retained = renderer.Render(1, .45); int[] baseline = Pixels(retained);
                    int cached = renderer.CachedPixels; bool reused = true;
                    for (int tick = 0; tick <= 30; tick++) reused &= Object.ReferenceEquals(retained, renderer.Render(tick / 30.0, .45, 1));
                    check(reused && renderer.CachedPixels == cached && Same(baseline, Pixels(renderer.Render(1, .45, 0))),
                        "Taper ticks reuse the premium bitmap/perimeter cache and discard no settled Energy pixels at " + finish + "/" + lighting);
                    foreach (double invalid in new[] { Double.NaN, Double.NegativeInfinity, Double.PositiveInfinity, -1.0 })
                        check(Same(baseline, Pixels(renderer.Render(1, .45, invalid))),
                            "Invalid or negative reveal strength safely selects exact static/idle pixels at " + finish + "/" + lighting + "/" + invalid);
                    int[] maximum = Pixels(renderer.Render(.7, .45, 1));
                    check(Same(maximum, Pixels(renderer.Render(.7, .45, 2))), "Strength above one cannot overbrighten the tapered cue");
                    renderer.Render(0, .45, 1);
                    check(Pixels(retained).All(pixel => pixel == 0), "A hidden start remains entirely transparent even with full reveal light");
                }
            }
            foreach (int finish in new[] { 0, 1 }) foreach (int lighting in new[] { 0, 1 }) {
                var visual = Visual(Target(), "Mint", finish, lighting);
                using (Bitmap old = FocusIndicatorPaint.Render(visual)) {
                    int[] baseline = Pixels(old);
                    foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, -1.0 })
                        using (Bitmap image = FocusIndicatorPaint.Render(visual, 1, 0, invalid))
                            check(Same(baseline, Pixels(image)), "Nonpremium invalid strength keeps released static colors at " + finish + "/" + lighting);
                    using (Bitmap image = FocusIndicatorPaint.Render(visual, 0, 0, 1))
                        check(Pixels(image).All(pixel => pixel == 0), "Nonpremium hidden start remains transparent without a bright dot");
                }
            }
        }
    }
}
