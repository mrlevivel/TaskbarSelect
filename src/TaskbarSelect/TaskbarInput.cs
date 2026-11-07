using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class TaskbarInput
    {
        internal static readonly IntPtr ReplayTag = new IntPtr(0x54534252);
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        {
            internal int X, Y;
            internal uint Data, Flags, Time;
            internal IntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Input
        {
            internal uint Type;
            internal MouseInput Mouse;
        }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] input, int size);

        internal static bool IsDrag(TaskbarMover.Point origin, TaskbarMover.Point current, Size threshold)
        {
            return Math.Abs((long)current.X - origin.X) >= Math.Max(1, threshold.Width / 2) ||
                Math.Abs((long)current.Y - origin.Y) >= Math.Max(1, threshold.Height / 2);
        }
        internal static Size DirectDragThreshold(Size nativeDragSize, uint dpi)
        {
            int minimum = (int)Math.Ceiling(8d * (dpi == 0 ? 96 : dpi) / 96d);
            return new Size(Math.Max(minimum, Math.Max(1, nativeDragSize.Width / 2)),
                Math.Max(minimum, Math.Max(1, nativeDragSize.Height / 2)));
        }
        internal static bool IsDirectDrag(TaskbarMover.Point origin, TaskbarMover.Point current, Size threshold)
        {
            return Math.Abs((long)current.X - origin.X) >= threshold.Width ||
                Math.Abs((long)current.Y - origin.Y) >= threshold.Height;
        }
        internal static int Normalize(int coordinate, int start, int size)
        {
            return (int)Math.Max(0, Math.Min(65535, ((long)coordinate - start) * 65536 / Math.Max(1, size) + 32768 / Math.Max(1, size)));
        }
        static Input At(TaskbarMover.Point point, uint flags, Rectangle desktop)
        {
            return new Input { Mouse = new MouseInput {
                X = Normalize(point.X, desktop.Left, desktop.Width), Y = Normalize(point.Y, desktop.Top, desktop.Height),
                Flags = flags | 0xE001, ExtraInfo = ReplayTag
            } };
        }
        internal static Input[] ReplayEvents(TaskbarMover.Point origin, TaskbarMover.Point current, bool released, Rectangle desktop)
        {
            // Return the swallowed down at its real origin, followed by the drag or
            // click release. A tagged batch prevents recursion and interleaving.
            return new[] { At(origin, 2, desktop), At(current, released ? 4U : 0U, desktop) };
        }
        internal static bool Replay(TaskbarMover.Point origin, TaskbarMover.Point current, bool released)
        {
            Input[] events = ReplayEvents(origin, current, released, SystemInformation.VirtualScreen);
            uint sent = SendInput((uint)events.Length, events, Marshal.SizeOf(typeof(Input)));
            if (sent == events.Length) return true;
            if (sent != 0) SendInput(1, new[] { At(current, 4, SystemInformation.VirtualScreen) }, Marshal.SizeOf(typeof(Input)));
            return false;
        }
    }
}
