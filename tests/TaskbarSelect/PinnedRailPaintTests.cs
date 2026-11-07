using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal static class PinnedRailPaintTests
    {
        [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        static FocusIndicatorTarget Target(int dpi = 96, int corner = 8)
        {
            return new FocusIndicatorTarget {
                Identity = new FocusedWindowSnapshot { Window = new IntPtr(71), Root = new IntPtr(71), Process = 12,
                    Thread = 13, ProcessStarted = 14, Visible = true, ClassName = "Neutral rail paint fixture" },
                Frame = new Rectangle(-80, -40, 240, 180), Monitors = new[] { new Rectangle(-500, -500, 1600, 1600) },
                Dpi = dpi, Corner = corner
            };
        }
        static int[] Pixels(Bitmap image)
        {
            var pixels = new int[image.Width * image.Height];
            BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < image.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * image.Width, image.Width); }
            finally { image.UnlockBits(data); }
            return pixels;
        }
        static int Alpha(int pixel) { return (int)((uint)pixel >> 24); }
        internal static void Run(Action<bool, string> check)
        {
            CoverageAndState(check); ClippingAndGuards(check); CurrentVisualState(check);
        }
        static void CoverageAndState(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 144, 192, 288, 768 }) foreach (bool dark in new[] { false, true })
                foreach (bool focused in new[] { false, true }) foreach (bool square in new[] { false, true }) {
                    int corner = square ? 0 : (8 * dpi + 48) / 96;
                    var target = Target(dpi, corner);
                    var focus = focused ? FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, "Coral", dark) : null;
                    var visual = FocusIndicatorVisual.CreatePinRail(target, focused, dark, focus);
                    check(visual != null && visual.PinRail && visual.PinRailFocused == focused && visual.GlowWidth == 0 &&
                        visual.Lighting == FocusLightingMode.None && visual.Bounds.Height == Math.Max(corner, visual.PinRailInset + visual.Thickness),
                        "Pin rail is a bounded static top-strip visual at " + dpi + "/" + dark + "/" + focused + "/" + square);
                    using (Bitmap image = FocusIndicatorPaint.Render(visual, .1, .2, 1))
                    using (Bitmap settled = FocusIndicatorPaint.Render(visual, 1, .8, 0))
                    using (Region shape = FocusIndicatorPaint.Shape(visual))
                    using (Graphics graphics = Graphics.FromImage(image)) {
                        IntPtr native = shape.GetHrgn(graphics);
                        try {
                            int[] a = Pixels(image), b = Pixels(settled);
                            bool symmetric = true, premultiplied = true, covered = true, smooth = false, staticPaint = true;
                            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                                int pixel = a[y * image.Width + x], alpha = Alpha(pixel);
                                symmetric &= alpha == Alpha(a[y * image.Width + image.Width - x - 1]);
                                premultiplied &= ((pixel >> 16) & 255) <= alpha && ((pixel >> 8) & 255) <= alpha && (pixel & 255) <= alpha && (alpha != 0 || pixel == 0);
                                smooth |= alpha > 0 && alpha < 255;
                                covered &= alpha == 0 || PtInRegion(native, x, y);
                                staticPaint &= pixel == b[y * image.Width + x];
                            }
                            check(symmetric && premultiplied && covered && staticPaint && (square || smooth),
                                "Pin rail has mirrored AA, premultiplied alpha, matching HRGN and no reveal/lighting animation at " + dpi + "/" + dark + "/" + focused + "/" + square);
                            bool straight = true;
                            for (int y = 0; y < image.Height; y++) {
                                int pixel = a[y * image.Width + image.Width / 2];
                                bool rail = y >= visual.PinRailInset && y < visual.PinRailInset + visual.Thickness;
                                straight &= Alpha(pixel) == (rail ? 255 : 0);
                            }
                            check(straight && !PtInRegion(native, image.Width / 2, target.Frame.Height / 2),
                                "Pin rail preserves focus pixels and contains no client, bottom or full-height side region at " + dpi + "/" + dark + "/" + focused + "/" + square);
                        } finally { DeleteObject(native); }
                    }
                }
            var retained = Target();
            var red = FocusIndicatorVisual.Create(retained, FocusIndicatorMode.InsetEdge, "Coral", true);
            var green = FocusIndicatorVisual.Create(retained, FocusIndicatorMode.InsetEdge, "Forest Green", true);
            var aRail = FocusIndicatorVisual.CreatePinRail(retained, true, true, red);
            var bRail = FocusIndicatorVisual.CreatePinRail(retained, true, true, green);
            check(aRail.Outer == bRail.Outer && aRail.Inner == bRail.Inner && aRail.PinRailInset == red.Thickness,
                "Pinning uses neutral pewter geometry independently of the selected focus accent");
            var copy = aRail.Copy(); copy.Target.Monitors[0] = Rectangle.Empty; copy.Target.Frame = Rectangle.Empty;
            check(aRail.Target.Frame == retained.Frame && aRail.Target.Monitors[0] != Rectangle.Empty,
                "Copied rail visuals do not share mutable target geometry or monitor arrays");
            green.Target.Identity.Window = new IntPtr(72);
            check(FocusIndicatorVisual.CreatePinRail(retained, true, true, green).PinRailInset == 0,
                "An unrelated focused-window frame cannot offset another window's pin rail");
        }
        static void ClippingAndGuards(Action<bool, string> check)
        {
            var target = Target(); target.Frame = new Rectangle(-80, -40, 240, 180);
            target.Monitors = new[] { new Rectangle(-100, -100, 100, 100), new Rectangle(40, -100, 160, 100) };
            var visual = FocusIndicatorVisual.CreatePinRail(target, false, true);
            using (Bitmap image = FocusIndicatorPaint.Render(visual)) using (Region shape = FocusIndicatorPaint.Shape(visual)) {
                bool clipped = true, noSeam = true;
                for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                    var physical = new Point(x + visual.Bounds.X, y + visual.Bounds.Y);
                    bool monitor = false; foreach (Rectangle rectangle in target.Monitors) monitor |= rectangle.Contains(physical);
                    if (!monitor) clipped &= image.GetPixel(x, y).A == 0 && !shape.IsVisible(x, y);
                    if (physical.X == 40 && physical.Y > target.Frame.Top + visual.Thickness) noSeam &= image.GetPixel(x, y).A == 0;
                }
                check(clipped && noSeam, "Disconnected and negative-coordinate monitor clips remain transparent and do not invent a vertical seam");
            }
            target.Monitors = new[] { new Rectangle(-100, 0, 500, 500) };
            check(FocusIndicatorVisual.CreatePinRail(target, false, true) == null, "A wholly off-monitor top rail does not allocate a blank overlay");
            target = Target(); target.ShellSurface = true;
            check(FocusIndicatorVisual.CreatePinRail(target, false, true) == null, "Shell surfaces cannot construct pinned rail geometry");
            target = Target(); target.Monitors = new[] { new Rectangle(0, 0, 0, 20) };
            check(FocusIndicatorVisual.CreatePinRail(target, false, true) == null, "Malformed monitor bounds refuse the pin visual");
            target = Target(); target.Frame = new Rectangle(0, 0, 20000, 20000); target.Monitors = new[] { target.Frame };
            check(FocusIndicatorVisual.CreatePinRail(target, false, true) == null, "Pin rail retains the existing full-target allocation guard before cropping");
            target = Target(); visual = FocusIndicatorVisual.CreatePinRail(target, false, true); visual.PinRailInset = -1;
            bool rejected = false; try { using (Bitmap image = FocusIndicatorPaint.Render(visual)) { } } catch (ArgumentException) { rejected = true; }
            check(rejected, "A corrupt negative pin inset is refused before bitmap publication");
        }
        sealed class Surface : IFocusIndicatorSurface
        {
            public IntPtr Window { get { return new IntPtr(701); } }
            public void Present(FocusIndicatorVisual visual) { }
            public void Dispose() { }
        }
        sealed class Events : IFocusIndicatorEvents { public void Dispose() { } }
        static void CurrentVisualState(Action<bool, string> check)
        {
            var target = Target();
            var api = new FocusIndicatorApi { Foreground = delegate { return target.Identity.Window; }, Read = delegate { return target; },
                DesktopAllowed = delegate { return true; }, Dark = delegate { return true; }, GlowAllowed = delegate { return true; },
                Now = delegate { return 1000; }, PointerButtonDown = delegate { return false; }, Watch = delegate { return new Events(); } };
            using (var indicator = new FocusIndicator(api, delegate { return new Surface(); })) {
                check(indicator.CurrentVisual == null, "No focus visual is exposed before successful presentation");
                indicator.Update(2, "Forest Green", false);
                var visual = indicator.CurrentVisual;
                check(visual != null && visual.Target.Frame == target.Frame, "Successful injected focus presentation exposes its actual visual to the pin controller");
                visual.Target.Frame = Rectangle.Empty; visual.Target.Monitors[0] = Rectangle.Empty;
                check(indicator.CurrentVisual.Target.Frame == target.Frame && indicator.CurrentVisual.Target.Monitors[0] != Rectangle.Empty,
                    "The current focus visual is a defensive snapshot");
                indicator.Hide();
                check(indicator.CurrentVisual == null, "Hiding focus clears the published visual before a pin rail can reuse it");
            }
        }
    }
}
