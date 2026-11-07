using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal sealed class ShelfPeekDwell
    {
        internal const int DelayMilliseconds = 300;
        int candidate = -1;
        long entered;
        bool attempted;
        internal void Reset() { candidate = -1; entered = 0; attempted = false; }
        internal bool Update(int item, long now, bool quiet)
        {
            if (item < 0 || !quiet) { Reset(); return false; }
            if (candidate != item || now < entered) { candidate = item; entered = now; attempted = false; return false; }
            if (attempted || now - entered < DelayMilliseconds) return false;
            attempted = true; return true;
        }
    }

    internal static class ShelfPeekPlacement
    {
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        internal static Rectangle Place(Rectangle source, Rectangle work, bool local)
        {
            if (source.Width < 1 || source.Height < 1 || work.Width < 1 || work.Height < 1) return Rectangle.Empty;
            double scale = Math.Min(1, Math.Min(work.Width / (double)source.Width, work.Height / (double)source.Height));
            int width = Math.Max(1, (int)Math.Floor(source.Width * scale));
            int height = Math.Max(1, (int)Math.Floor(source.Height * scale));
            int x = local ? Math.Max(work.Left, Math.Min(source.Left, work.Right - width)) : work.Left + (work.Width - width) / 2;
            int y = local ? Math.Max(work.Top, Math.Min(source.Top, work.Bottom - height)) : work.Top + (work.Height - height) / 2;
            return new Rectangle(x, y, width, height);
        }
        internal static bool TryRead(ShelfWindow source, Display target, bool onSourceMonitor, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            // Hover is visual only. Do not restore minimized or tray-hidden sources to obtain pixels.
            if (source == null || target == null || !Native.IsWindowVisible(source.Handle) || IsIconic(source.Handle)) return false;
            var current = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            Native.MonitorInfo destination;
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(source.Handle, 2), ref current) ||
                !Native.TryMonitor(target.Device, out destination)) return false;
            if (onSourceMonitor) destination = current;
            Native.Rect frame;
            if (Native.DwmGetWindowAttribute(source.Handle, 9, out frame, Marshal.SizeOf(typeof(Native.Rect))) != 0 ||
                frame.Right <= frame.Left || frame.Bottom <= frame.Top) return false;
            var sourceBounds = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            var work = Rectangle.FromLTRB(destination.Work.Left, destination.Work.Top, destination.Work.Right, destination.Work.Bottom);
            bounds = Place(sourceBounds, work, String.Equals(current.Device, destination.Device, StringComparison.OrdinalIgnoreCase));
            return !bounds.IsEmpty;
        }
    }

    internal static class ShelfPeekInteractionChecks
    {
        internal static void Run(Action<bool, string> assert)
        {
            var dwell = new ShelfPeekDwell();
            assert(!dwell.Update(0, 100, true), "Peek never begins on the initial hover");
            assert(!dwell.Update(0, 399, true), "Peek waits for the complete dwell");
            assert(dwell.Update(0, 400, true), "A stable thumbnail hover opens one peek");
            assert(!dwell.Update(0, 1000, true), "A held hover cannot repeatedly create overlays");
            assert(!dwell.Update(1, 1000, true) && dwell.Update(1, 1300, true), "Another thumbnail starts a new dwell");
            assert(!dwell.Update(-2, 1400, true), "Paging arrows cancel thumbnail dwell");
            assert(!dwell.Update(1, 1500, false) && !dwell.Update(1, 1800, true), "Pressed or animating input resets dwell");
            assert(!dwell.Update(1, 1700, true) && dwell.Update(1, 2000, true), "A reversed clock restarts the bounded dwell");
            dwell.Reset();
            assert(!dwell.Update(1, 3000, true), "Dismissal clears the previous hover");
            var work = new Rectangle(-1920, 200, 1920, 1040);
            var local = new Rectangle(-1700, 350, 900, 600);
            assert(ShelfPeekPlacement.Place(local, work, true) == local, "Local peek preserves visible source placement");
            assert(ShelfPeekPlacement.Place(local, work, false) == new Rectangle(-1410, 420, 900, 600), "Remote peek centers without enlarging the source");
            var oversized = ShelfPeekPlacement.Place(new Rectangle(2000, 0, 2560, 1440), work, false);
            assert(work.Contains(oversized) && oversized.Width == 1848 && oversized.Height == 1040, "Large remote previews fit the usable area without distortion");
            var clipped = ShelfPeekPlacement.Place(new Rectangle(-2000, 100, 900, 600), work, true);
            assert(clipped == new Rectangle(-1920, 200, 900, 600), "Partially offscreen sources keep their size while fitting the preview");
            assert(ShelfPeekPlacement.Place(Rectangle.Empty, work, true).IsEmpty && ShelfPeekPlacement.Place(local, Rectangle.Empty, false).IsEmpty,
                "Invalid source or work dimensions cannot create an overlay");
        }
    }
}
