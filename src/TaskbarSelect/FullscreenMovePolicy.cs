using System;
namespace TaskbarSelect
{
    internal static class FullscreenMovePolicy
    {
        internal static bool Matches(Native.Rect actual, Native.Rect target)
        { return Math.Abs((long)actual.Left - target.Left) <= 2 && Math.Abs((long)actual.Top - target.Top) <= 2 &&
            Math.Abs((long)actual.Right - target.Right) <= 2 && Math.Abs((long)actual.Bottom - target.Bottom) <= 2; }
        internal static bool TryRestoreBounds(Native.Rect placement, Native.MonitorInfo monitor, bool toolWindow, out Native.Rect bounds)
        {
            bounds = new Native.Rect();
            long dx = toolWindow ? 0 : (long)monitor.Work.Left - monitor.Monitor.Left;
            long dy = toolWindow ? 0 : (long)monitor.Work.Top - monitor.Monitor.Top;
            long left = placement.Left + dx, right = placement.Right + dx;
            long top = placement.Top + dy, bottom = placement.Bottom + dy;
            if (right <= left || bottom <= top || right - left > Int32.MaxValue || bottom - top > Int32.MaxValue ||
                left < Int32.MinValue || top < Int32.MinValue || right > Int32.MaxValue || bottom > Int32.MaxValue) return false;
            bounds = new Native.Rect { Left = (int)left, Top = (int)top, Right = (int)right, Bottom = (int)bottom };
            return true;
        }
    }
}
