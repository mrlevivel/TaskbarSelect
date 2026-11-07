using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TaskbarSelect
{
    // All fixtures are offscreen pixels or injected observers. They never show
    // a native window, read a user's settings, or send mouse/keyboard input.
    internal static class FocusFrameFinishTests
    {
        [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        static FocusIndicatorTarget Target(int dpi = 96, int corner = 8)
        {
            return new FocusIndicatorTarget {
                Identity = new FocusedWindowSnapshot { Window = new IntPtr(67), Root = new IntPtr(67), Process = 3,
                    Thread = 4, ProcessStarted = 5, Visible = true, ClassName = "Neutral accent outline fixture" },
                Frame = new Rectangle(20, 30, 96, 76), Dpi = dpi, Corner = corner,
                Monitors = new[] { new Rectangle(-500, -500, 2000, 2000) }
            };
        }
        static FocusIndicatorVisual Visual(FocusIndicatorTarget target, string accent, bool dark, int lighting, int finish = 1)
        { return FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, accent, dark, false, lighting, finish); }
        static int[] Pixels(Bitmap image)
        {
            var values = new int[image.Width * image.Height];
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < image.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), values, y * image.Width, image.Width); }
            finally { image.UnlockBits(data); }
            return values;
        }
        static int Alpha(int pixel) { return (int)((uint)pixel >> 24); }
        static int Light(int pixel) { return (((pixel >> 16) & 255) * 3 + ((pixel >> 8) & 255) * 6 + (pixel & 255)) / 10; }
        static bool Same(int[] a, int[] b)
        { if (a.Length != b.Length) return false; for (int index = 0; index < a.Length; index++) if (a[index] != b[index]) return false; return true; }
        static string Digest(int[] pixels)
        {
            var bytes = new byte[pixels.Length * 4]; Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
        static bool SafePixels(FocusIndicatorVisual visual, Bitmap image, int[] pixels, bool hollowCenter = true)
        {
            using (Region shape = FocusIndicatorPaint.Shape(visual)) using (Graphics graphics = Graphics.FromImage(image)) {
                IntPtr native = shape.GetHrgn(graphics);
                try {
                    if (hollowCenter && PtInRegion(native, image.Width / 2, image.Height / 2)) return false;
                    for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                        int pixel = pixels[y * image.Width + x], alpha = Alpha(pixel);
                        if (((pixel >> 16) & 255) > alpha || ((pixel >> 8) & 255) > alpha || (pixel & 255) > alpha ||
                            (alpha == 0 && pixel != 0) || (alpha > 0 && !PtInRegion(native, x, y))) return false;
                    }
                    return true;
                } finally { DeleteObject(native); }
            }
        }
        internal static void Run(Action<bool, string> check)
        { Compatibility(check); Matrix(check); RevealAndFlow(check); LightingVisibility(check); TinyContours(check); ClippingAndFallback(check); Controller(check); }
        static void Compatibility(Action<bool, string> check)
        {
            // Captured from the warning-as-error B17200C3 build before adding
            // finish selection, using this exact offscreen fixture and phase.
            string[,] golden = {
                { "06F29A09DC98A5FBEBA8441A9FF0C76A470B5664883735C93C3100183217168A", "2A3EC14506EC18EF4D72463E047A362525A94C21917FE2A3B430BEF79D917D18", "88F8DD44EE7CC94B1DBD67C7256B0D506CB9CD1C931E7167694BC9365B0C8A4A", "9BB450784F82E7E5F36F82439786C1310BA2E62BF5D7BB535B84FFFCA0D89911" },
                { "5B7B8AE3A000F202BB45A2FD33D6809C4FAD7170DB7FB8102865A1ACC860F351", "2F14E84BDBEB10C20F5B1AE5E586CF2BD8C181ECA1A020DA89CEBBCAAFA41A3D", "360B3E4C500A5935606EBEF54A8EF2B2E7EF3C5CAA72386F4CC3CC5585178A42", "063933FDA99109B27922EFA89805B0155B4EDA92373814C2FF67FDA81EF27F1C" }
            };
            foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge }) for (int lighting = 0; lighting <= 3; lighting++) {
                var prior = FocusIndicatorVisual.Create(Target(), mode, "Original", true, false, lighting);
                var explicitDefault = FocusIndicatorVisual.Create(Target(), mode, "Original", true, false, lighting, 0);
                using (Bitmap a = FocusIndicatorPaint.Render(prior, 1, .31)) using (Bitmap b = FocusIndicatorPaint.Render(explicitDefault, 1, .31)) {
                    int[] first = Pixels(a), second = Pixels(b);
                    check(prior.Finish == FocusFrameFinish.MutedEdge && prior.SameAppearance(explicitDefault) && Same(first, second) &&
                        Digest(first) == golden[(int)mode - 1, lighting], "Existing muted finish preserves frozen raw pixels and six-argument API at " + mode + "/" + lighting);
                }
                foreach (int invalid in new[] { -1, 2, Int32.MinValue, Int32.MaxValue })
                    check(prior.SameAppearance(FocusIndicatorVisual.Create(Target(), mode, "Original", true, false, lighting, invalid)),
                        "Unknown finish falls back to the existing muted edge at " + mode + "/" + lighting + "/" + invalid);
            }
            foreach (bool glow in new[] { false, true }) {
                var old = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.InsetEdge, "Mint", true, glow);
                check(old.SameAppearance(FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.InsetEdge, "Mint", true, glow, -1, 0)),
                    "Five-argument legacy glow overload preserves muted finish and resolved lighting at " + glow);
            }
            // Captured independently from the prior1A6507FF accent build.
            string[,] plainGolden = {
                { "74B1DFA03625C479349FC692215CDEF4820DEF12789E85DF3EB8229A88C1DDF0", "DCE5D16243E494BA7321CA71D858D60E832346B5E7D0C8C322F9FC12DFC72616" },
                { "CF743CE9E79D2FE13016EBA8FDB6C5C6323607B4519A411C55503925189228D9", "DB1B590A21C90F27069B2F738DD465AEB38686B23B94DBAA06151171E78265CA" }
            };
            foreach (bool dark in new[] { false, true }) for (int lighting = 0; lighting <= 1; lighting++)
                using (Bitmap image = FocusIndicatorPaint.Render(Visual(Target(), "Forest Green", dark, lighting), 1, .31))
                    check(Digest(Pixels(image)) == plainGolden[dark ? 1 : 0, lighting], "Accent None/Soft glow retain frozen raw pixels at " + dark + "/" + lighting);
        }
        static void Matrix(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 144, 192, 288 }) foreach (bool dark in new[] { false, true })
                foreach (string accent in FocusAppearance.Presets) for (int lighting = 0; lighting <= 3; lighting++)
                    foreach (int corner in new[] { 0, (8 * dpi + 48) / 96 }) {
                        var visual = Visual(Target(dpi, corner), accent, dark, lighting);
                        using (Bitmap image = FocusIndicatorPaint.Render(visual, 1, .37)) {
                            int[] pixels = Pixels(image); int stroke = Math.Max(1, (dpi + 48) / 96), padding = visual.GlowWidth;
                            Color selected = FocusAppearance.Accent(accent, dark);
                            bool crisp = true, symmetric = true;
                            for (int y = 0; y < stroke; y++) {
                                int pixel = pixels[(padding + y) * image.Width + image.Width / 2];
                                crisp &= Alpha(pixel) == 255;
                                if (lighting <= (int)FocusLightingMode.SubtleGlow) crisp &= pixel == selected.ToArgb();
                                else crisp &= ((pixel >> 16) & 255) >= selected.R && ((pixel >> 8) & 255) >= selected.G && (pixel & 255) >= selected.B;
                            }
                            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                                int at = y * image.Width + x;
                                symmetric &= Alpha(pixels[at]) == Alpha(pixels[y * image.Width + image.Width - x - 1]) &&
                                    Alpha(pixels[at]) == Alpha(pixels[(image.Height - y - 1) * image.Width + x]);
                            }
                            check(visual.Finish == FocusFrameFinish.AccentOutline && visual.Thickness == stroke && visual.OuterThickness == stroke &&
                                visual.Outer == selected && visual.Inner == selected && crisp &&
                                pixels[(padding + stroke) * image.Width + image.Width / 2] == 0 &&
                                pixels[(image.Height / 2) * image.Width + image.Width / 2] == 0,
                                "Accent outline keeps one-DIP opaque geometry and exact plain/Soft hue with intentionally lit premium color at " + dpi + "/" + dark + "/" + accent + "/" + lighting + "/" + corner);
                            check(image.PixelFormat == PixelFormat.Format32bppPArgb && (lighting == (int)FocusLightingMode.EnergyFlow || symmetric) && SafePixels(visual, image, pixels),
                                "Accent outline has smooth rounded geometry, premultiplied alpha and hollow native region; only local energy bloom varies at " + dpi + "/" + dark + "/" + accent + "/" + lighting + "/" + corner);
                        }
                    }
        }
        static void RevealAndFlow(Action<bool, string> check)
        {
            for (int lighting = 0; lighting <= 3; lighting++) {
                var visual = Visual(Target(144, 12), "Forest Green", true, lighting);
                int[] previous = null;
                using (Bitmap finalImage = FocusIndicatorPaint.Render(visual, 1, .2)) {
                    int[] final = Pixels(finalImage);
                    foreach (double progress in new[] { 0.0, .05, .2, .5, .8, 1.0 })
                        using (Bitmap image = FocusIndicatorPaint.Render(visual, progress, .2)) {
                            int[] pixels = Pixels(image); bool monotonic = true, empty = true;
                            for (int index = 0; index < pixels.Length; index++) {
                                monotonic &= Alpha(pixels[index]) <= Alpha(final[index]) && (previous == null || Alpha(pixels[index]) >= Alpha(previous[index]));
                                empty &= pixels[index] == 0;
                            }
                            check(monotonic && SafePixels(visual, image, pixels) && (progress != 0 || empty) && (progress != 1 || Same(pixels, final)),
                                "Accent finish reveals monotonically along the existing perimeter and cancels to a transparent image at " + lighting + "/" + progress);
                            previous = pixels;
                        }
                }
            }
            var a = Visual(Target(), "Mint", true, 2); var b = Visual(Target(), "Mint", true, 2, 0);
            check(!a.SameAppearance(b), "Changing finish invalidates the cached visual even when target and lighting are retained");
            b.Thickness = a.Thickness; b.OuterThickness = a.OuterThickness; b.Outer = a.Outer; b.Inner = a.Inner;
            check(!a.SameAppearance(b), "Finish identity participates in cache comparison independently of dimensions and colors");
        }
        static int At(FocusIndicatorVisual visual, int[] pixels, Point physical)
        {
            int x = physical.X - visual.Bounds.X, y = physical.Y - visual.Bounds.Y;
            return x < 0 || y < 0 || x >= visual.Bounds.Width || y >= visual.Bounds.Height ? 0 : pixels[y * visual.Bounds.Width + x];
        }
        static void LightingVisibility(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 144, 192, 288 }) foreach (bool dark in new[] { false, true })
                foreach (string accent in FocusAppearance.Presets) {
                    var target = Target(dpi, (8 * dpi + 48) / 96);
                    var soft = Visual(target, accent, dark, 1); var lit = Visual(target, accent, dark, 2); var energy = Visual(target, accent, dark, 3);
                    using (Bitmap softImage = FocusIndicatorPaint.Render(soft)) using (Bitmap litImage = FocusIndicatorPaint.Render(lit))
                        using (var renderer = new FocusIndicatorLightingRenderer(energy)) {
                            int[] softPixels = Pixels(softImage), basePixels = Pixels(litImage);
                            Bitmap cached = renderer.Render(1, 0); int[] first = Pixels(cached), shifted = Pixels(renderer.Render(1, .5));
                            var halo = new Point(target.Frame.Left + target.Frame.Width / 2, target.Frame.Top - (3 * dpi + 48) / 96);
                            int coreDifference = 0, haloDifference = 0, solid = 0, highlighted = 0; bool opaqueCore = true;
                            for (int index = 0; index < first.Length; index++) {
                                if (Alpha(basePixels[index]) == 255) {
                                    solid++; opaqueCore &= Alpha(first[index]) == 255 && Alpha(shifted[index]) == 255;
                                    coreDifference = Math.Max(coreDifference, Math.Abs(Light(first[index]) - Light(shifted[index])));
                                    if (Light(first[index]) - Light(basePixels[index]) > 10) highlighted++;
                                } else haloDifference = Math.Max(haloDifference, Math.Abs(Alpha(first[index]) - Alpha(shifted[index])));
                            }
                            check(lit.GlowWidth == Math.Min(48, (9 * dpi + 48) / 96) && lit.Thickness == soft.Thickness &&
                                Alpha(At(lit, basePixels, halo)) >= Alpha(At(soft, softPixels, halo)) + 20,
                                "Illuminated accent adds at least20 alpha levels at three logical pixels beyond Soft glow while preserving stroke width at " + dpi + "/" + dark + "/" + accent);
                            check(coreDifference >= 20 && haloDifference >= 30 && opaqueCore && highlighted > 0 && highlighted < solid * .70 &&
                                SafePixels(energy, cached, shifted) && Same(first, Pixels(renderer.Render(1, 1))) &&
                                Object.ReferenceEquals(cached, renderer.Render(1, .2)),
                                "Accent Energy has a visible traveling light (>=20 core luminance and30 local halo alpha), remains localized/opaque/hollow and reuses its bitmap at " + dpi + "/" + dark + "/" + accent);
                            using (Bitmap still = FocusIndicatorPaint.Render(lit, 1, .75))
                                check(Same(basePixels, Pixels(still)), "Stronger Illuminated accent remains fully steady at " + dpi + "/" + dark + "/" + accent);
                        }
                }
            foreach (int dpi in new[] { 384, 768 }) {
                var visual = Visual(Target(dpi), "Original", true, 3);
                using (Bitmap image = FocusIndicatorPaint.Render(visual, 1, .2))
                    check(visual.GlowWidth == Math.Min(48, (9 * dpi + 48) / 96) && visual.GlowWidth <= 48 &&
                        visual.Bounds == FocusIndicatorVisual.Expand(visual.Target.Frame, visual.GlowWidth) && SafePixels(visual, image, Pixels(image)),
                        "Stronger accent bloom respects the48-pixel safe geometry cap at extreme DPI " + dpi);
            }
        }
        static void TinyContours(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 144, 288 }) foreach (bool dark in new[] { false, true })
                foreach (string accent in FocusAppearance.Presets) for (int crop = 0; crop < 3; crop++) {
                    int corner = (8 * dpi + 48) / 96, stroke = Math.Max(1, (dpi + 48) / 96);
                    var target = Target(dpi, corner);
                    if (crop == 0) target.Monitors = new[] { new Rectangle(target.Frame.Right - stroke, target.Frame.Top + corner, stroke, 8) };
                    if (crop == 1) target.Monitors = new[] { new Rectangle(target.Frame.Left + corner - 4, target.Frame.Top, 6, 6) };
                    if (crop == 2) target.Frame = new Rectangle(20, 30, 14, 11);
                    var visual = Visual(target, accent, dark, 3);
                    using (var renderer = new FocusIndicatorLightingRenderer(visual)) {
                        int count = visual.Bounds.Width * visual.Bounds.Height;
                        var minimum = new int[count * 3]; var maximum = new int[count * 3];
                        for (int index = 0; index < minimum.Length; index++) minimum[index] = 255;
                        bool safe = true; Bitmap retained = null;
                        for (int phase = 0; phase < 16; phase++) {
                            Bitmap image = renderer.Render(1, phase / 16.0); int[] pixels = Pixels(image);
                            if (retained == null) retained = image;
                            safe &= Object.ReferenceEquals(retained, image) && SafePixels(visual, image, pixels, crop == 2);
                            for (int index = 0; index < pixels.Length; index++) for (int channel = 0; channel < 3; channel++) {
                                int value = (pixels[index] >> (channel * 8)) & 255, at = index * 3 + channel;
                                minimum[at] = Math.Min(minimum[at], value); maximum[at] = Math.Max(maximum[at], value);
                            }
                        }
                        int visibleDifference = 0;
                        for (int index = 0; index < minimum.Length; index++) visibleDifference = Math.Max(visibleDifference, maximum[index] - minimum[index]);
                        check(safe && visibleDifference >= 20, "Accent traveling light remains visible and safely clipped in a tiny side/corner/window contour at " + dpi + "/" + dark + "/" + accent + "/" + crop);
                    }
                }
        }
        static void ClippingAndFallback(Action<bool, string> check)
        {
            var target = Target(); target.Frame = new Rectangle(-40, -30, 96, 76);
            target.Monitors = new[] { new Rectangle(-500, -500, 500, 500), new Rectangle(0, 0, 500, 500) };
            for (int lighting = 0; lighting <= 3; lighting++) {
                var visual = Visual(target, "Peach", true, lighting);
                using (Bitmap image = FocusIndicatorPaint.Render(visual, 1, .4)) {
                    int[] pixels = Pixels(image); bool clips = true;
                    for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                        var physical = new Point(x + visual.Bounds.X, y + visual.Bounds.Y);
                        if (!target.Monitors[0].Contains(physical) && !target.Monitors[1].Contains(physical)) clips &= pixels[y * image.Width + x] == 0;
                    }
                    check(clips && SafePixels(visual, image, pixels), "Accent finish clips stacked monitor gaps and negative coordinates at lighting " + lighting);
                }
                var crop = Target(); crop.Frame = new Rectangle(-40, 30, 96, 76); crop.Monitors = new[] { new Rectangle(0, 0, 200, 200) };
                using (Bitmap image = FocusIndicatorPaint.Render(Visual(crop, "Peach", true, lighting), 1, .4))
                    check(image.GetPixel(0, image.Height / 2).A == 0, "Monitor crop never introduces a false accent stroke along its seam at lighting " + lighting);
                var overflow = Target(); overflow.Frame = new Rectangle(Int32.MinValue, 30, 96, 76);
                overflow.Monitors = new[] { new Rectangle(Int32.MinValue, 0, 200, 200) };
                var fallback = Visual(overflow, "Peach", true, lighting);
                using (Bitmap image = FocusIndicatorPaint.Render(fallback, 1, .4))
                    check(fallback.Finish == FocusFrameFinish.AccentOutline && fallback.GlowWidth == 0 && fallback.Thickness == 1 &&
                        image.GetPixel(image.Width / 2, 0).A == 255 && image.GetPixel(image.Width / 2, image.Height / 2).A == 0,
                        "Halo padding overflow preserves one-DIP accent finish and transparent center at lighting " + lighting);
            }
        }
        sealed class Surface : IFocusIndicatorSurface
        {
            internal FocusIndicatorVisual Last;
            internal int Presents, Disposals;
            public IntPtr Window { get { return new IntPtr(68); } }
            public void Present(FocusIndicatorVisual visual) { Last = visual; Presents++; }
            public void Dispose() { Disposals++; }
        }
        sealed class Observer : IFocusIndicatorEvents { public void Dispose() { } }
        static void Controller(Action<bool, string> check)
        {
            var target = Target(); var surface = new Surface(); int reads = 0, created = 0; bool glowAllowed = true;
            var api = new FocusIndicatorApi {
                Foreground = delegate { return target.Identity.Window; }, Read = delegate { reads++; return target; },
                DesktopAllowed = delegate { return true; }, Dark = delegate { return true; }, GlowAllowed = delegate { return glowAllowed; },
                Now = delegate { return 1000; }, PointerButtonDown = delegate { return false; },
                Watch = delegate { return new Observer(); }
            };
            using (var controller = new FocusIndicator(api, delegate { created++; return surface; })) {
                for (int lighting = 0; lighting <= 3; lighting++) {
                    controller.Update(2, "Coral", false, false, lighting, 1);
                    check(created == 1 && surface.Last.Finish == FocusFrameFinish.AccentOutline && surface.Last.Thickness == 1 &&
                        surface.Last.Lighting == (FocusLightingMode)lighting, "Injected controller propagates accent finish through lighting " + lighting);
                }
                controller.Update(2, "Coral", false, false, 3);
                check(surface.Last.Finish == FocusFrameFinish.MutedEdge && surface.Last.Thickness == 2,
                    "Existing controller call signature retains its muted finish");
                controller.Update(2, "Coral", false, false, 3, 99);
                check(surface.Last.Finish == FocusFrameFinish.MutedEdge, "Controller safely normalizes unknown finish values");
                glowAllowed = false; controller.Update(2, "Coral", false, false, 3, 1);
                check(surface.Last.Finish == FocusFrameFinish.AccentOutline && surface.Last.Lighting == FocusLightingMode.None && surface.Last.GlowWidth == 0,
                    "Injected high-contrast/reduced-animation policy suppresses lighting while preserving selected accent outline");
                int presents = surface.Presents; target.Fullscreen = true; controller.Update(2, "Coral", false, false, 3, 1);
                check(!controller.Visible && surface.Presents == presents && surface.Disposals == 1,
                    "Accent finish follows the existing fullscreen exclusion and destroys its surface");
                int before = reads; controller.Update(0, "Coral", false, false, 3, 1); controller.Update(2, "Coral", true, false, 3, 1);
                check(reads == before && !controller.Visible, "OFF and pause gates avoid target reads for every finish");
            }
        }
    }
}
