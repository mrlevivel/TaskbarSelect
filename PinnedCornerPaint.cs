using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    // One retained pin cue shares the original native perimeter. The white
    // core, small accent halo and both fading ends are sampled together into
    // premultiplied pixels; focused/unfocused state never changes this light.
    internal static class PinnedCornerPaint
    {
        const int Samples = 8, Coverage = Samples * Samples;
        sealed class Geometry
        {
            internal readonly FocusIndicatorPaint.RoundBox Outer, Inner, ContrastInner;
            internal readonly double Left, Top, CenterX, CenterY, Radius, HalfArc, EndX, EndY, Length, Hold;
            internal Geometry(FocusIndicatorVisual visual)
            {
                Rectangle frame = FocusIndicatorPaint.LocalFrame(visual);
                int corner = FocusIndicatorPaint.Corner(visual);
                double inset = visual.Thickness / 2.0, start = Math.Max(corner, inset);
                Outer = new FocusIndicatorPaint.RoundBox(frame, 0, corner);
                Inner = new FocusIndicatorPaint.RoundBox(frame, visual.Thickness, Math.Max(0, corner - visual.Thickness));
                double rim = .5 * Math.Max(96, Math.Min(768, visual.Target.Dpi)) / 96.0;
                ContrastInner = new FocusIndicatorPaint.RoundBox(frame, visual.Thickness + rim, Math.Max(0, corner - visual.Thickness - rim));
                Left = frame.Left + inset; Top = frame.Top + inset;
                CenterX = frame.Left + start; CenterY = frame.Top + start;
                Radius = Math.Max(0, corner - inset); HalfArc = Radius * Math.PI / 4;
                EndX = frame.Left + visual.PinCornerLength; EndY = frame.Top + visual.PinCornerLength;
                Length = HalfArc + visual.PinCornerLength - start;
                Hold = Math.Min(Length * .30, HalfArc + 8 * Math.Max(96, Math.Min(768, visual.Target.Dpi)) / 96.0);
            }
            internal void Nearest(double x, double y, out double distance, out double along)
            {
                if (x <= CenterX && y <= CenterY) {
                    double dx = x - CenterX, dy = y - CenterY;
                    distance = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - Radius);
                    double angle = Math.Atan2(dy, dx); if (angle < 0) angle += Math.PI * 2;
                    along = Radius * (Clamp(angle, Math.PI, Math.PI * 1.5) - Math.PI * 1.25);
                } else if (y <= CenterY || (x >= CenterX && y - Top <= x - Left)) {
                    distance = Math.Abs(y - Top); along = HalfArc + x - CenterX;
                } else {
                    distance = Math.Abs(x - Left); along = -HalfArc - y + CenterY;
                }
            }
        }
        static double Clamp(double value, double low, double high) { return Math.Max(low, Math.Min(high, value)); }
        static double Smooth(double low, double high, double value)
        {
            double t = Clamp((value - low) / Math.Max(.000001, high - low), 0, 1);
            return t * t * (3 - 2 * t);
        }
        static void Validate(FocusIndicatorVisual visual)
        {
            FocusIndicatorPaint.Validate(visual);
            if (!visual.PinRail || !visual.PinCorner || visual.PinRailInset != 0 || visual.Lighting != FocusLightingMode.None ||
                visual.GlowWidth < 1 || visual.GlowWidth > 24 || visual.PinCornerLength <= visual.Thickness + visual.GlowWidth ||
                visual.PinCornerLength > 320 || visual.PinCornerLength > Math.Min(visual.Target.Frame.Width, visual.Target.Frame.Height) / 2 ||
                FocusIndicatorPaint.Corner(visual) + visual.Thickness >= visual.PinCornerLength)
                throw new ArgumentException("Pinned corner geometry unavailable.");
            Rectangle allowed = FocusIndicatorVisual.Expand(new Rectangle(visual.Target.Frame.X, visual.Target.Frame.Y,
                visual.PinCornerLength, visual.PinCornerLength), visual.GlowWidth);
            if (!FocusIndicatorVisual.Valid(allowed) || visual.Bounds.Left < allowed.Left || visual.Bounds.Top < allowed.Top ||
                visual.Bounds.Right > allowed.Right || visual.Bounds.Bottom > allowed.Bottom)
                throw new ArgumentException("Pinned corner crop unavailable.");
            foreach (Rectangle monitor in visual.Target.Monitors)
                if (!FocusIndicatorVisual.Valid(monitor)) throw new ArgumentException("Pinned corner monitor geometry unavailable.");
        }
        static bool OnMonitor(FocusIndicatorVisual visual, int x, int y)
        {
            var physical = new Point(x + visual.Bounds.X, y + visual.Bounds.Y);
            foreach (Rectangle monitor in visual.Target.Monitors) if (monitor.Contains(physical)) return true;
            return false;
        }
        static int Pixel(FocusIndicatorVisual visual, Geometry geometry, int x, int y)
        {
            if (!OnMonitor(visual, x, y)) return 0;
            double totalAlpha = 0, red = 0, green = 0, blue = 0;
            for (int sy = 0; sy < Samples; sy++) for (int sx = 0; sx < Samples; sx++) {
                double px = x + (sx + .5) / Samples, py = y + (sy + .5) / Samples;
                double distance, along; geometry.Nearest(px, py, out distance, out along);
                double taper = 1 - Smooth(geometry.Hold, geometry.Length, Math.Abs(along));
                if (taper <= 0) continue;
                double core = geometry.Outer.Contains(px, py) && !geometry.Inner.Contains(px, py) ? taper : 0;
                double rim = geometry.Inner.Contains(px, py) && !geometry.ContrastInner.Contains(px, py) ? .28 * taper : 0;
                double gap = Math.Max(0, distance - visual.Thickness / 2.0), halo = 0;
                if (gap < visual.GlowWidth) {
                    double scaled = gap / (visual.GlowWidth * .42);
                    halo = visual.GlowColor.A / 255.0 * Math.Exp(-.5 * scaled * scaled) *
                        (1 - Smooth(visual.GlowWidth * .65, visual.GlowWidth, gap)) * taper;
                }
                double behind = halo * (1 - rim), uncovered = 1 - core;
                totalAlpha += core + (rim + behind) * uncovered;
                red += visual.Outer.R * core + (16 * rim + visual.AccentColor.R * behind) * uncovered;
                green += visual.Outer.G * core + (21 * rim + visual.AccentColor.G * behind) * uncovered;
                blue += visual.Outer.B * core + (18 * rim + visual.AccentColor.B * behind) * uncovered;
            }
            int a = (int)Math.Floor(totalAlpha * 255 / Coverage + .5);
            if (a <= 0) return 0;
            int r = Math.Min(a, (int)Math.Floor(red / Coverage + .5));
            int g = Math.Min(a, (int)Math.Floor(green / Coverage + .5));
            int b = Math.Min(a, (int)Math.Floor(blue / Coverage + .5));
            return unchecked((int)((uint)a << 24 | (uint)r << 16 | (uint)g << 8 | (uint)b));
        }
        internal static Bitmap Render(FocusIndicatorVisual visual)
        {
            Validate(visual);
            var image = ShelfPaint.Canvas(visual.Bounds.Size, Math.Max(96, Math.Min(768, visual.Target.Dpi)));
            var geometry = new Geometry(visual);
            try {
                BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try {
                    var row = new int[image.Width];
                    for (int y = 0; y < image.Height; y++) {
                        for (int x = 0; x < image.Width; x++) row[x] = Pixel(visual, geometry, x, y);
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                    }
                } finally { image.UnlockBits(data); }
                return image;
            } catch { image.Dispose(); throw; }
        }
        internal static Region Shape(FocusIndicatorVisual visual)
        {
            Validate(visual);
            var geometry = new Geometry(visual);
            var shape = new Region(); shape.MakeEmpty();
            try {
                for (int y = 0; y < visual.Bounds.Height; y++) {
                    int start = -1;
                    for (int x = 0; x <= visual.Bounds.Width; x++) {
                        bool covered = x < visual.Bounds.Width && Pixel(visual, geometry, x, y) != 0;
                        if (covered && start < 0) start = x;
                        else if (!covered && start >= 0) { shape.Union(new Rectangle(start, y, x - start, 1)); start = -1; }
                    }
                }
                return shape;
            } catch { shape.Dispose(); throw; }
        }
    }
}
