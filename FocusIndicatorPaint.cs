using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    // Subpixel coverage is calculated only in the thin perimeter band. The
    // full-size allocation remains the one production PArgb upload image.
    internal static class FocusIndicatorPaint
    {
        const int Samples = 8, Coverage = Samples * Samples;
        const long MaximumPixels = 16777216;
        internal struct RoundBox
        {
            internal double Left, Top, Right, Bottom, Radius;
            internal RoundBox(Rectangle frame, double inset, double radius)
            {
                Left = frame.Left + inset; Top = frame.Top + inset;
                Right = frame.Right - inset; Bottom = frame.Bottom - inset;
                Radius = Math.Max(0, Math.Min(radius, Math.Min(Right - Left, Bottom - Top) / 2));
            }
            internal bool Contains(double x, double y)
            {
                if (x < Left || x > Right || y < Top || y > Bottom || Right <= Left || Bottom <= Top) return false;
                if (Radius == 0 || (x >= Left + Radius && x <= Right - Radius) ||
                    (y >= Top + Radius && y <= Bottom - Radius)) return true;
                double dx = x - Clamp(x, Left + Radius, Right - Radius);
                double dy = y - Clamp(y, Top + Radius, Bottom - Radius);
                return dx * dx + dy * dy <= Radius * Radius;
            }
            internal int Count(int x, int y)
            {
                if (x + 1 <= Left || x >= Right || y + 1 <= Top || y >= Bottom) return 0;
                if (Contains(x, y) && Contains(x + 1, y) && Contains(x, y + 1) && Contains(x + 1, y + 1)) return Coverage;
                int count = 0;
                for (int sy = 0; sy < Samples; sy++) for (int sx = 0; sx < Samples; sx++)
                    if (Contains(x + (sx + .5) / Samples, y + (sy + .5) / Samples)) count++;
                return count;
            }
            internal double ExteriorDistance(double x, double y)
            {
                double dx = Math.Abs(x - (Left + Right) / 2) - ((Right - Left) / 2 - Radius);
                double dy = Math.Abs(y - (Top + Bottom) / 2) - ((Bottom - Top) / 2 - Radius);
                double outsideX = Math.Max(0, dx), outsideY = Math.Max(0, dy);
                return Math.Sqrt(outsideX * outsideX + outsideY * outsideY) + Math.Min(Math.Max(dx, dy), 0) - Radius;
            }
        }
        internal sealed class Perimeter
        {
            readonly double center, top, right, bottom, radius, topLength, sideLength, arc;
            internal readonly double HalfLength;
            internal Perimeter(Rectangle frame, int thickness, int corner)
            {
                double inset = thickness / 2.0;
                center = frame.Left + frame.Width / 2.0; top = frame.Top + inset;
                right = frame.Right - inset; bottom = frame.Bottom - inset;
                radius = Math.Max(0, Math.Min(corner - inset, Math.Min(right - center, (bottom - top) / 2)));
                topLength = right - radius - center; sideLength = bottom - top - radius * 2;
                arc = radius * Math.PI / 2; HalfLength = topLength * 2 + sideLength + arc * 2;
            }
            internal double Distance(double x, double y)
            {
                // Both branches start at top center and meet at bottom center.
                x = center + Math.Abs(x - center);
                double best = Double.MaxValue, distance = 0;
                double px = Clamp(x, center, right - radius);
                Choose(x, y, px, top, px - center, ref best, ref distance);
                if (radius > 0) {
                    double angle = Clamp(Math.Atan2(y - top - radius, x - right + radius), -Math.PI / 2, 0);
                    Choose(x, y, right - radius + radius * Math.Cos(angle), top + radius + radius * Math.Sin(angle),
                        topLength + radius * (angle + Math.PI / 2), ref best, ref distance);
                }
                double py = Clamp(y, top + radius, bottom - radius);
                Choose(x, y, right, py, topLength + arc + py - top - radius, ref best, ref distance);
                if (radius > 0) {
                    double angle = Clamp(Math.Atan2(y - bottom + radius, x - right + radius), 0, Math.PI / 2);
                    Choose(x, y, right - radius + radius * Math.Cos(angle), bottom - radius + radius * Math.Sin(angle),
                        topLength + arc + sideLength + radius * angle, ref best, ref distance);
                }
                px = Clamp(x, center, right - radius);
                Choose(x, y, px, bottom, topLength + arc * 2 + sideLength + right - radius - px, ref best, ref distance);
                return distance;
            }
            static void Choose(double x, double y, double px, double py, double along, ref double best, ref double distance)
            {
                double dx = x - px, dy = y - py, squared = dx * dx + dy * dy;
                if (squared < best) { best = squared; distance = along; }
            }
        }
        static double Clamp(double value, double minimum, double maximum)
        { return Math.Max(minimum, Math.Min(maximum, value)); }
        internal static double NormalizeProgress(double progress)
        { return Double.IsNaN(progress) || Double.IsInfinity(progress) ? 1 : Clamp(progress, 0, 1); }
        internal static double NormalizeRevealStrength(double strength)
        { return Double.IsNaN(strength) || Double.IsInfinity(strength) ? 0 : Clamp(strength, 0, 1); }
        static double SmoothStep(double start, double end, double value)
        {
            double amount = Clamp((value - start) / Math.Max(.000000001, end - start), 0, 1);
            return amount * amount * (3 - 2 * amount);
        }
        internal static double RevealWeight(double progress, double distanceDip, double halfLengthDip, double strength)
        {
            strength = NormalizeRevealStrength(strength);
            if (strength == 0 || halfLengthDip <= 0 || Double.IsNaN(distanceDip) || Double.IsInfinity(distanceDip)) return 0;
            double front = NormalizeProgress(progress) * halfLengthDip;
            // Lengths follow the target's DPI, not its window dimensions. Very
            // small contours shorten the same taper rather than wrap a head
            // around both fronts or turn the whole border into a bright block.
            double length = Math.Min(56, halfLengthDip * .45), peak = length * (8.0 / 56);
            double behind = front - distanceDip;
            if (behind <= 0 || behind >= length) return 0;
            return strength * SmoothStep(0, peak, behind) * (1 - SmoothStep(peak, length, behind)) *
                SmoothStep(0, Math.Min(12, halfLengthDip * .2), front);
        }
        internal static int RevealPixel(int pixel, int baseline, double weight)
        {
            if (weight <= 0 || pixel == 0 || baseline == 0) return pixel;
            int alpha = (int)((uint)pixel >> 24), originalAlpha = (int)((uint)baseline >> 24);
            if (alpha == 0 || originalAlpha == 0) return pixel;
            // One tint per pixel keeps both mirrored fronts from adding light
            // at their joins. Alpha, AA coverage and the stroke width stay
            // unchanged. Existing Energy may already be brighter; retain it.
            double lift = .80 * Clamp(weight, 0, 1);
            int red = RevealChannel((pixel >> 16) & 255, (baseline >> 16) & 255, originalAlpha, alpha, 250, lift);
            int green = RevealChannel((pixel >> 8) & 255, (baseline >> 8) & 255, originalAlpha, alpha, 252, lift);
            int blue = RevealChannel(pixel & 255, baseline & 255, originalAlpha, alpha, 255, lift);
            return unchecked((int)((uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | (uint)blue));
        }
        static int RevealChannel(int current, int baseline, int originalAlpha, int alpha, int light, double lift)
        {
            double straight = baseline * 255.0 / originalAlpha;
            int tinted = (int)Math.Floor((straight + (light - straight) * lift) * alpha / 255 + .5);
            return Math.Min(alpha, Math.Max(current, tinted));
        }
        internal static bool Premium(FocusIndicatorVisual visual)
        { return visual != null && (visual.Lighting == FocusLightingMode.Illuminated || visual.Lighting == FocusLightingMode.EnergyFlow); }
        internal static void Validate(FocusIndicatorVisual visual)
        {
            if (visual == null || visual.Target == null || !FocusIndicatorVisual.Valid(visual.Bounds) ||
                visual.Bounds.Width > 16384 || visual.Bounds.Height > 16384 ||
                (long)visual.Bounds.Width * visual.Bounds.Height > MaximumPixels ||
                !FocusIndicatorVisual.Valid(visual.Target.Frame) || visual.Target.Monitors == null || visual.Target.Monitors.Length == 0 ||
                visual.Target.Monitors.Length > 128 || visual.GlowWidth < 0 || visual.GlowWidth > (Premium(visual) ? 48 : 32) ||
                visual.Thickness < 1 || visual.OuterThickness < 1 || visual.OuterThickness > visual.Thickness ||
                (long)visual.Thickness * 2 >= visual.Target.Frame.Width || (long)visual.Thickness * 2 >= visual.Target.Frame.Height ||
                (visual.Finish != FocusFrameFinish.MutedEdge && visual.Finish != FocusFrameFinish.AccentOutline) ||
                (visual.Mode != FocusIndicatorMode.MutedFrame && visual.Mode != FocusIndicatorMode.InsetEdge))
                throw new ArgumentException("Focus indicator paint geometry unavailable.");
            Rectangle envelope = FocusIndicatorVisual.Expand(visual.Target.Frame, visual.GlowWidth);
            if (!FocusIndicatorVisual.Valid(envelope) || visual.Bounds.Left < envelope.Left || visual.Bounds.Top < envelope.Top ||
                (long)visual.Bounds.Right > envelope.Right || (long)visual.Bounds.Bottom > envelope.Bottom)
                throw new ArgumentException("Focus indicator glow geometry unavailable.");
        }
        internal static Rectangle LocalFrame(FocusIndicatorVisual visual)
        {
            Rectangle frame = visual.Target.Frame; frame.Offset(-visual.Bounds.X, -visual.Bounds.Y); return frame;
        }
        internal static int Corner(FocusIndicatorVisual visual)
        { return Math.Max(0, Math.Min(visual.Target.Corner, Math.Min(visual.Target.Frame.Width, visual.Target.Frame.Height) / 2)); }
        internal static Region Shape(FocusIndicatorVisual visual)
        {
            if (visual != null && visual.PinCorner) return PinnedCornerPaint.Shape(visual);
            if (visual != null && visual.PinRail) return PinnedRailPaint.Shape(visual);
            Validate(visual); Rectangle frame = LocalFrame(visual);
            // Allow two extra transparent pixels inside the ideal ring, and the
            // outer corner's transparent triangle. A binary HWND region must
            // never chip away partially covered alpha pixels at a curve. Its
            // integer-coordinate rasterization can differ by a diagonal pixel
            // from the pixel-center subpixel coverage, so one pixel is unsafe.
            Rectangle envelope = FocusIndicatorVisual.Expand(frame, visual.GlowWidth);
            if (!FocusIndicatorVisual.Valid(envelope)) throw new ArgumentException("Focus indicator glow geometry unavailable.");
            var shape = new Region(envelope);
            int inset = visual.Thickness + 2;
            Rectangle inner = new Rectangle(frame.X + inset, frame.Y + inset,
                Math.Max(0, frame.Width - inset * 2), Math.Max(0, frame.Height - inset * 2));
            try {
                if (inner.Width > 0 && inner.Height > 0)
                    using (GraphicsPath path = Rounded(inner, Math.Max(0, Corner(visual) - inset))) shape.Exclude(path);
                using (var monitors = new Region()) {
                    monitors.MakeEmpty();
                    foreach (Rectangle monitor in visual.Target.Monitors) {
                        if (!FocusIndicatorVisual.Valid(monitor)) throw new ArgumentException("Focus indicator monitor geometry unavailable.");
                        Rectangle clip = Rectangle.Intersect(monitor, visual.Bounds); clip.Offset(-visual.Bounds.X, -visual.Bounds.Y);
                        if (clip.Width > 0 && clip.Height > 0) monitors.Union(clip);
                    }
                    shape.Intersect(monitors);
                }
                return shape;
            } catch { shape.Dispose(); throw; }
        }
        static GraphicsPath Rounded(Rectangle frame, int radius)
        {
            var path = new GraphicsPath();
            radius = Math.Min(radius, Math.Min(frame.Width, frame.Height) / 2);
            if (radius <= 0) { path.AddRectangle(frame); return path; }
            int diameter = radius * 2;
            path.AddArc(frame.Left, frame.Top, diameter, diameter, 180, 90);
            path.AddArc(frame.Right - diameter, frame.Top, diameter, diameter, 270, 90);
            path.AddArc(frame.Right - diameter, frame.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(frame.Left, frame.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        internal static Bitmap Render(FocusIndicatorVisual visual, double progress = 1)
        { return RenderCore(visual, progress, 0); }
        static Bitmap RenderCore(FocusIndicatorVisual visual, double progress, double revealStrength)
        {
            if (visual != null && visual.PinCorner) return PinnedCornerPaint.Render(visual);
            if (visual != null && visual.PinRail) return PinnedRailPaint.Render(visual);
            if (Premium(visual)) return Render(visual, progress, 0, revealStrength);
            Validate(visual); progress = NormalizeProgress(progress);
            revealStrength = NormalizeRevealStrength(revealStrength);
            var image = ShelfPaint.Canvas(visual.Bounds.Size, Math.Max(96, Math.Min(768, visual.Target.Dpi)));
            try {
                using (Graphics graphics = Graphics.FromImage(image)) {
                    graphics.CompositingMode = CompositingMode.SourceCopy; graphics.Clear(Color.Transparent);
                }
                if (progress == 0) return image;
                Rectangle frame = LocalFrame(visual); int corner = Corner(visual);
                var outer = new RoundBox(frame, 0, corner);
                var middle = new RoundBox(frame, visual.OuterThickness, Math.Max(0, corner - visual.OuterThickness));
                var inner = new RoundBox(frame, visual.Thickness, Math.Max(0, corner - visual.Thickness));
                var perimeter = progress < 1 ? new Perimeter(frame, visual.Thickness, corner) : null;
                var revealPerimeter = revealStrength > 0 ? perimeter ?? new Perimeter(frame, visual.Thickness, corner) : null;
                double scale = Math.Max(96, Math.Min(768, visual.Target.Dpi)) / 96.0;
                BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
                try {
                    int[] row = new int[image.Width];
                    using (Region shape = Shape(visual)) using (var matrix = new Matrix()) {
                        RectangleF[] scans = shape.GetRegionScans(matrix);
                        foreach (RectangleF scan in scans) {
                            int left = Math.Max(0, (int)Math.Floor(scan.Left)), right = Math.Min(image.Width, (int)Math.Ceiling(scan.Right));
                            int top = Math.Max(0, (int)Math.Floor(scan.Top)), bottom = Math.Min(image.Height, (int)Math.Ceiling(scan.Bottom));
                            for (int y = top; y < bottom; y++) {
                                for (int x = left; x < right; x++) {
                                    int framePixel = Pixel(x, y, outer, middle, inner, visual, perimeter, progress);
                                    int pixel = visual.GlowWidth == 0 || visual.GlowColor.A == 0 ? framePixel :
                                        GlowUnder(framePixel, x, y, outer, visual, perimeter, progress);
                                    if (revealPerimeter != null && pixel != 0) {
                                        double weight = RevealWeight(progress, revealPerimeter.Distance(x + .5, y + .5) / scale,
                                            revealPerimeter.HalfLength / scale, revealStrength);
                                        pixel = RevealPixel(pixel, pixel, weight);
                                    }
                                    double quiet = ReservedCornerWeight(visual, x, y);
                                    if (quiet > 0) pixel = BlendReserved(pixel, framePixel, quiet);
                                    row[x] = pixel;
                                }
                                // Region scans are disjoint horizontal bands.
                                // Upload just this band, retaining other bands
                                // in the same row without a full-frame buffer.
                                Marshal.Copy(row, left, IntPtr.Add(data.Scan0, y * data.Stride + left * 4), right - left);
                            }
                        }
                    }
                } finally { image.UnlockBits(data); }
                return image;
            } catch { image.Dispose(); throw; }
        }
        internal static Bitmap Render(FocusIndicatorVisual visual, double progress, double phase)
        { return Render(visual, progress, phase, 0); }
        internal static Bitmap Render(FocusIndicatorVisual visual, double progress, double phase, double revealStrength)
        {
            if (visual != null && visual.PinCorner) return PinnedCornerPaint.Render(visual);
            if (visual != null && visual.PinRail) return PinnedRailPaint.Render(visual);
            if (!Premium(visual)) return RenderCore(visual, progress, revealStrength);
            using (var renderer = new FocusIndicatorLightingRenderer(visual)) {
                Bitmap image = renderer.Render(progress, phase, revealStrength);
                return image.Clone(new Rectangle(Point.Empty, image.Size), PixelFormat.Format32bppPArgb);
            }
        }
        internal static int UnlitPixel(int x, int y, RoundBox outer, RoundBox middle, RoundBox inner,
            FocusIndicatorVisual visual, Perimeter perimeter, double progress)
        { return Pixel(x, y, outer, middle, inner, visual, perimeter, progress); }
        internal static double ReservedCornerWeight(FocusIndicatorVisual visual, int x, int y)
        {
            if (visual == null || !visual.ReservedPinCorner || visual.PinRail) return 0;
            Rectangle frame = LocalFrame(visual);
            double scale = Math.Max(96, Math.Min(768, visual.Target.Dpi)) / 96.0;
            double distance = Math.Max(x + .5 - frame.Left, y + .5 - frame.Top) / scale;
            if (distance <= 36) return 1;
            if (distance >= 62) return 0;
            double t = (distance - 36) / 26;
            return 1 - t * t * (3 - 2 * t);
        }
        internal static int BlendReserved(int original, int unlit, double quiet)
        {
            if (quiet <= 0 || original == unlit) return original;
            if (quiet >= 1) return unlit;
            int oa = (int)((uint)original >> 24), ua = (int)((uint)unlit >> 24);
            int alpha = (int)Math.Floor(oa + (ua - oa) * quiet + .5);
            int red = Math.Min(alpha, (int)Math.Floor(((original >> 16) & 255) + (((unlit >> 16) & 255) - ((original >> 16) & 255)) * quiet + .5));
            int green = Math.Min(alpha, (int)Math.Floor(((original >> 8) & 255) + (((unlit >> 8) & 255) - ((original >> 8) & 255)) * quiet + .5));
            int blue = Math.Min(alpha, (int)Math.Floor((original & 255) + ((unlit & 255) - (original & 255)) * quiet + .5));
            return unchecked((int)((uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | (uint)blue));
        }
        static int Pixel(int x, int y, RoundBox outer, RoundBox middle, RoundBox inner,
            FocusIndicatorVisual visual, Perimeter perimeter, double progress)
        {
            int outerCount, middleCount, innerCount;
            if (perimeter == null) {
                outerCount = outer.Count(x, y); innerCount = inner.Count(x, y);
                middleCount = visual.Mode == FocusIndicatorMode.InsetEdge ? middle.Count(x, y) : innerCount;
            } else {
                double limit = perimeter.HalfLength * progress, distance = perimeter.Distance(x + .5, y + .5);
                double margin = Math.Max(4, visual.Thickness * 2);
                if (distance > limit + margin) return 0;
                if (distance < limit - margin) {
                    outerCount = outer.Count(x, y); innerCount = inner.Count(x, y);
                    middleCount = visual.Mode == FocusIndicatorMode.InsetEdge ? middle.Count(x, y) : innerCount;
                } else {
                    outerCount = middleCount = innerCount = 0;
                    for (int sy = 0; sy < Samples; sy++) for (int sx = 0; sx < Samples; sx++) {
                        double px = x + (sx + .5) / Samples, py = y + (sy + .5) / Samples;
                        if (perimeter.Distance(px, py) > limit || !outer.Contains(px, py)) continue;
                        outerCount++;
                        if (inner.Contains(px, py)) innerCount++;
                        if (visual.Mode == FocusIndicatorMode.InsetEdge && middle.Contains(px, py)) middleCount++;
                    }
                    if (visual.Mode != FocusIndicatorMode.InsetEdge) middleCount = innerCount;
                }
            }
            int outside = Math.Max(0, outerCount - middleCount), inside = Math.Max(0, middleCount - innerCount);
            // Compose both colored partitions once. Sequential SourceCopy
            // draws would punch a transparent seam at their shared AA edge.
            Color first = visual.Outer, second = visual.Inner;
            int alpha = (outside * first.A + inside * second.A + Coverage / 2) / Coverage;
            int divisor = Coverage * 255, rounding = divisor / 2;
            int red = (outside * first.A * first.R + inside * second.A * second.R + rounding) / divisor;
            int green = (outside * first.A * first.G + inside * second.A * second.G + rounding) / divisor;
            int blue = (outside * first.A * first.B + inside * second.A * second.B + rounding) / divisor;
            return unchecked((int)((uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | (uint)blue));
        }
        static int GlowUnder(int framePixel, int x, int y, RoundBox outer, FocusIndicatorVisual visual, Perimeter perimeter, double progress)
        {
            int frameAlpha = (int)((uint)framePixel >> 24);
            if (frameAlpha == 255 || (outer.Contains(x, y) && outer.Contains(x + 1, y) &&
                outer.Contains(x, y + 1) && outer.Contains(x + 1, y + 1))) return framePixel;
            // A wider halo remains a soft accent fade; the frame partitions
            // above are untouched, unlike the illuminated renderer's pale ridge.
            int width = visual.GlowWidth, peak = Math.Min(visual.Dark ? 90 : 72, (int)visual.GlowColor.A);
            bool complete = perimeter == null; double limit = complete ? 0 : perimeter.HalfLength * progress;
            if (!complete) {
                double distance = perimeter.Distance(x + .5, y + .5), margin = Math.Max(4, (width + visual.Thickness) * 2);
                if (distance > limit + margin) return framePixel;
                complete = distance < limit - margin;
            }
            double alpha = 0;
            // Straight outside bands need only eight samples across the fade,
            // rather than a full 8x8 grid. Curves/reveal tips keep full coverage.
            bool horizontal = x >= outer.Left + outer.Radius && x + 1 <= outer.Right - outer.Radius;
            bool vertical = y >= outer.Top + outer.Radius && y + 1 <= outer.Bottom - outer.Radius;
            if (complete && (horizontal || vertical)) {
                for (int sample = 0; sample < Samples; sample++) {
                    double px = x + (horizontal ? .5 : (sample + .5) / Samples);
                    double py = y + (horizontal ? (sample + .5) / Samples : .5);
                    alpha += GlowAlpha(outer.ExteriorDistance(px, py), width, peak);
                }
                alpha /= Samples;
            } else {
                for (int sy = 0; sy < Samples; sy++) for (int sx = 0; sx < Samples; sx++) {
                    double px = x + (sx + .5) / Samples, py = y + (sy + .5) / Samples;
                    if (!complete && perimeter.Distance(px, py) > limit) continue;
                    alpha += GlowAlpha(outer.ExteriorDistance(px, py), width, peak);
                }
                alpha /= Coverage;
            }
            int haloAlpha = (int)Math.Floor(alpha + .5);
            if (haloAlpha == 0) return framePixel;
            Color color = visual.GlowColor;
            int inverse = 255 - frameAlpha;
            int resultAlpha = frameAlpha + (haloAlpha * inverse + 127) / 255;
            int red = ((framePixel >> 16) & 255) + (((haloAlpha * color.R + 127) / 255) * inverse + 127) / 255;
            int green = ((framePixel >> 8) & 255) + (((haloAlpha * color.G + 127) / 255) * inverse + 127) / 255;
            int blue = (framePixel & 255) + (((haloAlpha * color.B + 127) / 255) * inverse + 127) / 255;
            red = Math.Min(resultAlpha, red); green = Math.Min(resultAlpha, green); blue = Math.Min(resultAlpha, blue);
            return unchecked((int)((uint)resultAlpha << 24 | (uint)red << 16 | (uint)green << 8 | (uint)blue));
        }
        static double GlowAlpha(double distance, int width, int peak)
        {
            if (distance <= 0 || distance >= width) return 0;
            double remaining = 1 - distance / width;
            return peak * remaining * remaining;
        }
    }

    // Geometry, coverage and perimeter positions are immutable for one visual.
    // A flowing frame updates only this narrow band and reuses its upload image.
    internal sealed class FocusIndicatorLightingRenderer : IDisposable
    {
        const int Samples = 8, SweepSteps = 4096;
        struct Entry
        {
            internal int Base, Bright, Plain;
            internal float Along, RevealStart, RevealEnd, RevealDistanceDip, Quiet;
        }
        struct Band
        {
            internal int Left, Right, Row, Entry;
            internal Band(int left, int right, int row, int entry)
            { Left = left; Right = right; Row = row; Entry = entry; }
        }
        static readonly double[] sweep = MakeSweep();
        readonly FocusIndicatorVisual visual;
        readonly Entry[] entries;
        readonly Band[] bands;
        readonly int[] row;
        readonly double halfLengthDip;
        Bitmap image;
        internal int CachedPixels { get { return entries.Length; } }
        internal Size Size { get { return visual.Bounds.Size; } }
        internal FocusIndicatorLightingRenderer(FocusIndicatorVisual visual)
        {
            FocusIndicatorPaint.Validate(visual);
            if (!FocusIndicatorPaint.Premium(visual)) throw new ArgumentException("Illuminated focus frame unavailable.");
            this.visual = visual;
            image = ShelfPaint.Canvas(visual.Bounds.Size, Math.Max(96, Math.Min(768, visual.Target.Dpi)));
            try {
                using (Graphics graphics = Graphics.FromImage(image)) {
                    graphics.CompositingMode = CompositingMode.SourceCopy; graphics.Clear(Color.Transparent);
                }
                Rectangle frame = FocusIndicatorPaint.LocalFrame(visual);
                int corner = FocusIndicatorPaint.Corner(visual);
                var outer = new FocusIndicatorPaint.RoundBox(frame, 0, corner);
                var perimeter = new FocusIndicatorPaint.Perimeter(frame, visual.Thickness, corner);
                halfLengthDip = perimeter.HalfLength / (Math.Max(96, Math.Min(768, visual.Target.Dpi)) / 96.0);
                var pixelList = new List<Entry>(); var bandList = new List<Band>();
                using (Region shape = FocusIndicatorPaint.Shape(visual)) using (var matrix = new Matrix()) {
                    foreach (RectangleF scan in shape.GetRegionScans(matrix)) {
                        int left = Math.Max(0, (int)Math.Floor(scan.Left)), right = Math.Min(image.Width, (int)Math.Ceiling(scan.Right));
                        int top = Math.Max(0, (int)Math.Floor(scan.Top)), bottom = Math.Min(image.Height, (int)Math.Ceiling(scan.Bottom));
                        for (int y = top; y < bottom; y++) {
                            bandList.Add(new Band(left, right, y, pixelList.Count));
                            for (int x = left; x < right; x++) pixelList.Add(Cache(x, y, outer, perimeter, frame));
                        }
                    }
                }
                entries = pixelList.ToArray(); bands = bandList.ToArray(); row = new int[image.Width];
            } catch { image.Dispose(); image = null; throw; }
        }
        static double[] MakeSweep()
        {
            var values = new double[SweepSteps + 1];
            for (int i = 0; i <= SweepSteps; i++) {
                double distance = i / (double)SweepSteps;
                if (distance > .5) distance -= 1;
                // The broad tail follows the brighter, shorter leading edge.
                double spread = distance >= 0 ? .105 : .034;
                double normalized = distance / spread;
                values[i] = Math.Exp(-.5 * normalized * normalized);
            }
            values[SweepSteps] = values[0]; return values;
        }
        internal static double NormalizePhase(double phase)
        {
            if (Double.IsNaN(phase) || Double.IsInfinity(phase)) return 0;
            return phase - Math.Floor(phase);
        }
        static double Clamp(double value, double lower, double upper)
        { return Math.Max(lower, Math.Min(upper, value)); }
        static double Mix(double first, double second, double amount)
        { return first + (second - first) * amount; }
        Entry Cache(int x, int y, FocusIndicatorPaint.RoundBox outer, FocusIndicatorPaint.Perimeter perimeter, Rectangle frame)
        {
            double baseAlpha = 0, baseRed = 0, baseGreen = 0, baseBlue = 0;
            double brightAlpha = 0, brightRed = 0, brightGreen = 0, brightBlue = 0;
            double scale = Math.Max(96, Math.Min(768, visual.Target.Dpi)) / 96.0;
            Color accent = visual.AccentColor, neutral = FocusAppearance.Neutral(visual.Dark);
            bool accentOutline = visual.Finish == FocusFrameFinish.AccentOutline;
            // A lit accent stays one DIP wide, with a modest continuous light
            // and a localized brighter head. The earlier headroom-only lift was
            // invisible on already-bright presets; a tinted light reflection
            // gives every accent a readable moving highlight instead.
            double tint = visual.Mode == FocusIndicatorMode.MutedFrame ? .84 : .93;
            double bodyRed = Mix(neutral.R, accent.R, tint), bodyGreen = Mix(neutral.G, accent.G, tint), bodyBlue = Mix(neutral.B, accent.B, tint);
            int peak = Math.Min(accentOutline ? (visual.Dark ? 132 : 108) : (visual.Dark ? 96 : 68), (int)visual.GlowColor.A);
            int headPeak = accentOutline ? (visual.Dark ? 220 : 190) : peak;
            bool horizontal = x >= outer.Left + outer.Radius && x + 1 <= outer.Right - outer.Radius;
            bool vertical = y >= outer.Top + outer.Radius && y + 1 <= outer.Bottom - outer.Radius;
            int across = horizontal || vertical ? 1 : Samples;
            int total = across * Samples;
            for (int a = 0; a < across; a++) for (int b = 0; b < Samples; b++) {
                double px = x + (horizontal ? .5 : (b + .5) / Samples);
                double py = y + (horizontal ? (b + .5) / Samples : vertical ? .5 : (a + .5) / Samples);
                double distance = outer.ExteriorDistance(px, py), alpha = 0, red = 0, green = 0, blue = 0, lit = 0, headAlpha = 0;
                if (distance <= 0 && distance > -visual.Thickness) {
                    alpha = 255;
                    if (accentOutline) {
                        red = Mix(accent.R, 250, .22); green = Mix(accent.G, 252, .22); blue = Mix(accent.B, 255, .22); lit = .80;
                    }
                    else {
                        double ridgeDistance = (-distance - .34 * scale) / (.30 * scale);
                        double ridge = .62 * Math.Exp(-.5 * ridgeDistance * ridgeDistance);
                        // A fine pale ridge and a colored inner body give the
                        // existing muted edge restrained depth.
                        red = Mix(bodyRed, 250, ridge); green = Mix(bodyGreen, 252, ridge); blue = Mix(bodyBlue, 255, ridge);
                        lit = .68;
                    }
                } else if (distance > 0 && visual.GlowWidth > 0 && distance < visual.GlowWidth) {
                    double remaining = 1 - distance / visual.GlowWidth;
                    alpha = peak * remaining * remaining;
                    red = accent.R; green = accent.G; blue = accent.B; lit = .42;
                    if (accentOutline) { headAlpha = headPeak * remaining * remaining; lit = .16; }
                }
                if (headAlpha == 0) headAlpha = alpha;
                baseAlpha += alpha; baseRed += alpha * red / 255; baseGreen += alpha * green / 255; baseBlue += alpha * blue / 255;
                brightAlpha += headAlpha;
                brightRed += headAlpha * Mix(red, 250, lit) / 255;
                brightGreen += headAlpha * Mix(green, 252, lit) / 255;
                brightBlue += headAlpha * Mix(blue, 255, lit) / 255;
            }
            int pixel = Pack(baseAlpha / total, baseRed / total, baseGreen / total, baseBlue / total);
            int bright = Pack(brightAlpha / total, brightRed / total, brightGreen / total, brightBlue / total);
            double center = perimeter.Distance(x + .5, y + .5), low = Double.MaxValue, high = 0;
            for (int sx = 0; sx <= 1; sx++) for (int sy = 0; sy <= 1; sy++) {
                double at = perimeter.Distance(x + sx, y + sy); low = Math.Min(low, at); high = Math.Max(high, at);
            }
            double clockwise = center / (perimeter.HalfLength * 2);
            if (x + .5 < frame.Left + frame.Width / 2.0) clockwise = 1 - clockwise;
            double quiet = FocusIndicatorPaint.ReservedCornerWeight(visual, x, y);
            int plain = 0;
            if (quiet > 0) {
                var middle = new FocusIndicatorPaint.RoundBox(frame, visual.OuterThickness, Math.Max(0, FocusIndicatorPaint.Corner(visual) - visual.OuterThickness));
                var inner = new FocusIndicatorPaint.RoundBox(frame, visual.Thickness, Math.Max(0, FocusIndicatorPaint.Corner(visual) - visual.Thickness));
                plain = FocusIndicatorPaint.UnlitPixel(x, y, outer, middle, inner, visual, null, 1);
            }
            return new Entry { Base = pixel, Bright = bright, Plain = plain, Quiet = (float)quiet, Along = (float)(NormalizePhase(clockwise) * SweepSteps),
                RevealStart = (float)(low / perimeter.HalfLength), RevealEnd = (float)(high / perimeter.HalfLength),
                RevealDistanceDip = (float)(center / scale) };
        }
        static int Pack(double alpha, double red, double green, double blue)
        {
            int a = Math.Max(0, Math.Min(255, (int)Math.Floor(alpha + .5)));
            int r = Math.Max(0, Math.Min(a, (int)Math.Floor(red + .5)));
            int g = Math.Max(0, Math.Min(a, (int)Math.Floor(green + .5)));
            int b = Math.Max(0, Math.Min(a, (int)Math.Floor(blue + .5)));
            return unchecked((int)((uint)a << 24 | (uint)r << 16 | (uint)g << 8 | (uint)b));
        }
        int Pixel(Entry entry, double progress, double phase, bool energy, double revealStrength)
        {
            if ((entry.Base == 0 && entry.Plain == 0) || progress == 0 || progress < entry.RevealStart) return 0;
            double coverage = progress >= entry.RevealEnd || progress == 1 ? 1 :
                Clamp((progress - entry.RevealStart) / Math.Max(.000000001, entry.RevealEnd - entry.RevealStart), 0, 1);
            double shine = 0;
            if (energy) {
                double at = phase - entry.Along;
                if (at < 0) at += SweepSteps;
                if (at >= SweepSteps) at -= SweepSteps;
                int index = (int)at; double fraction = at - index;
                shine = Mix(sweep[index], sweep[index + 1], fraction);
            }
            int pixel = Pack(Mix((uint)entry.Base >> 24, (uint)entry.Bright >> 24, shine) * coverage,
                Mix((entry.Base >> 16) & 255, (entry.Bright >> 16) & 255, shine) * coverage,
                Mix((entry.Base >> 8) & 255, (entry.Bright >> 8) & 255, shine) * coverage,
                Mix(entry.Base & 255, entry.Bright & 255, shine) * coverage);
            if (revealStrength > 0) pixel = FocusIndicatorPaint.RevealPixel(pixel, entry.Base,
                FocusIndicatorPaint.RevealWeight(progress, entry.RevealDistanceDip, halfLengthDip, revealStrength));
            if (entry.Quiet > 0) {
                int plain = coverage == 1 ? entry.Plain : Pack(((uint)entry.Plain >> 24) * coverage,
                    ((entry.Plain >> 16) & 255) * coverage, ((entry.Plain >> 8) & 255) * coverage, (entry.Plain & 255) * coverage);
                pixel = FocusIndicatorPaint.BlendReserved(pixel, plain, entry.Quiet);
            }
            return pixel;
        }
        internal Bitmap Render(double progress, double phase, double revealStrength = 0)
        {
            if (image == null) throw new ObjectDisposedException("FocusIndicatorLightingRenderer");
            progress = FocusIndicatorPaint.NormalizeProgress(progress);
            revealStrength = FocusIndicatorPaint.NormalizeRevealStrength(revealStrength);
            phase = NormalizePhase(phase) * SweepSteps;
            bool energy = visual.Lighting == FocusLightingMode.EnergyFlow;
            BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try {
                foreach (Band band in bands) {
                    int at = band.Entry;
                    for (int x = band.Left; x < band.Right; x++) row[x] = Pixel(entries[at++], progress, phase, energy, revealStrength);
                    Marshal.Copy(row, band.Left, IntPtr.Add(data.Scan0, band.Row * data.Stride + band.Left * 4), band.Right - band.Left);
                }
            } finally { image.UnlockBits(data); }
            return image;
        }
        public void Dispose()
        { Bitmap prior = image; image = null; if (prior != null) prior.Dispose(); }
    }
}
