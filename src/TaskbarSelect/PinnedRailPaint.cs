using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    // A retained pin cue is a static, neutral top edge. Its bitmap and HWND
    // region use one coverage function, so there are no opaque client pixels
    // and no curve fringe cut by a different region approximation.
    internal static class PinnedRailPaint
    {
        const int Coverage = 64;
        sealed class Geometry
        {
            internal FocusIndicatorPaint.RoundBox Outer, Middle, Inner;
            internal int Bottom;
            internal Geometry(FocusIndicatorVisual visual)
            {
                Rectangle frame = FocusIndicatorPaint.LocalFrame(visual);
                int corner = FocusIndicatorPaint.Corner(visual), inset = visual.PinRailInset;
                Outer = new FocusIndicatorPaint.RoundBox(frame, inset, Math.Max(0, corner - inset));
                Middle = new FocusIndicatorPaint.RoundBox(frame, inset + visual.OuterThickness, Math.Max(0, corner - inset - visual.OuterThickness));
                Inner = new FocusIndicatorPaint.RoundBox(frame, inset + visual.Thickness, Math.Max(0, corner - inset - visual.Thickness));
                Bottom = frame.Top + Math.Max(corner, inset + visual.Thickness);
            }
        }
        static void Validate(FocusIndicatorVisual visual)
        {
            FocusIndicatorPaint.Validate(visual);
            if (!visual.PinRail || visual.PinRailInset < 0 || visual.PinRailInset > 32 || visual.GlowWidth != 0 ||
                visual.Lighting != FocusLightingMode.None ||
                (long)(visual.PinRailInset + visual.Thickness) * 2 >= Math.Min(visual.Target.Frame.Width, visual.Target.Frame.Height))
                throw new ArgumentException("Pinned top rail geometry unavailable.");
            foreach (Rectangle monitor in visual.Target.Monitors)
                if (!FocusIndicatorVisual.Valid(monitor)) throw new ArgumentException("Pinned top rail monitor geometry unavailable.");
        }
        static bool OnMonitor(FocusIndicatorVisual visual, int x, int y)
        {
            var physical = new Point(x + visual.Bounds.X, y + visual.Bounds.Y);
            foreach (Rectangle monitor in visual.Target.Monitors) if (monitor.Contains(physical)) return true;
            return false;
        }
        static int Pixel(FocusIndicatorVisual visual, Geometry geometry, int x, int y)
        {
            if (y >= geometry.Bottom || !OnMonitor(visual, x, y)) return 0;
            int outer = geometry.Outer.Count(x, y), inner = geometry.Inner.Count(x, y);
            int count = outer - inner;
            if (count <= 0) return 0;
            int middle = geometry.Middle.Count(x, y);
            int first = Math.Max(0, outer - middle), second = Math.Max(0, middle - inner);
            int alpha = (255 * count + Coverage / 2) / Coverage;
            int red = (visual.Outer.R * first + visual.Inner.R * second + Coverage / 2) / Coverage;
            int green = (visual.Outer.G * first + visual.Inner.G * second + Coverage / 2) / Coverage;
            int blue = (visual.Outer.B * first + visual.Inner.B * second + Coverage / 2) / Coverage;
            return unchecked((int)((uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | (uint)blue));
        }
        internal static Bitmap Render(FocusIndicatorVisual visual)
        {
            Validate(visual);
            var geometry = new Geometry(visual);
            var image = ShelfPaint.Canvas(visual.Bounds.Size, Math.Max(96, Math.Min(768, visual.Target.Dpi)));
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
