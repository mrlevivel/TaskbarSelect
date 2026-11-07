using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal static class SearchPointerInputTests
    {
        static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        static void Send(TaskbarMover mover, int message, int code, IntPtr extra)
        {
            var type = typeof(TaskbarMover).GetNestedType("MouseData", BindingFlags.NonPublic);
            object value = Activator.CreateInstance(type);
            type.GetField("Position", Fields).SetValue(value, new TaskbarMover.Point { X = -123, Y = 456 });
            type.GetField("ExtraInfo", Fields).SetValue(value, extra);
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(type));
            try {
                Marshal.StructureToPtr(value, memory, false);
                typeof(TaskbarMover).GetMethod("MouseEvent", Fields).Invoke(mover, new object[] { code, new IntPtr(message), memory });
            } finally { Marshal.FreeHGlobal(memory); }
        }
        internal static void Run(Action<bool, string> check)
        {
            using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new Exception("Input replay forbidden"); }, delegate { return IntPtr.Zero; }, false)) {
                int clicks = 0; Point point = Point.Empty;
                mover.PointerDown += delegate(Point received) { clicks++; point = received; };
                foreach (string field in new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" })
                    check((IntPtr)typeof(TaskbarMover).GetField(field, Fields).GetValue(mover) == IntPtr.Zero, "Pointer forwarding fixture has no desktop " + field);
                check(!mover.Enabled, "Pointer forwarding is independent of taskbar movement being enabled");
                foreach (int message in new[] { 0x201, 0x204, 0x207, 0x20B }) {
                    int previous = clicks; Send(mover, message, 0, IntPtr.Zero);
                    check(clicks == previous + 1 && point == new Point(-123, 456), "Every button-down forwards exact negative desktop coordinates: " + message);
                }
                foreach (int message in new[] { 0x200, 0x202, 0x205, 0x208, 0x20C, 0x20A, 0x20E }) {
                    int previous = clicks; Send(mover, message, 0, IntPtr.Zero);
                    check(clicks == previous, "Motion, releases and wheels cannot select a search monitor or dismiss Search: " + message);
                }
                foreach (int message in new[] { 0x201, 0x204, 0x207, 0x20B }) {
                    int previous = clicks;
                    Send(mover, message, -1, IntPtr.Zero);
                    check(clicks == previous, "Negative hook codes do not forward pointer input");
                    Send(mover, message, 0, TaskbarInput.ReplayTag);
                    check(clicks == previous, "Taskbar Select replay cannot retarget or dismiss Search");
                }
                var movement = (WindowMoveCoordinator)typeof(TaskbarMover).GetField("movement", Fields).GetValue(mover);
                foreach (int message in new[] { 0x201, 0x204, 0x207, 0x20B }) {
                    check(movement.TryEnter(), "Owned movement slot admits cancellation fixture");
                    int previous = clicks; Send(mover, message, 0, IntPtr.Zero);
                    check(clicks == previous + 1 && movement.Cancelled, "Pointer event is forwarded even during busy movement and still cancels it: " + message);
                    movement.Exit();
                }
                mover.PointerDown += delegate { throw new Exception("Owned observer failure"); };
                check(movement.TryEnter(), "Observer exception fixture owns movement slot");
                Send(mover, 0x201, 0, IntPtr.Zero);
                check(movement.Cancelled, "Observer failure cannot escape the native hook or block movement cancellation");
                movement.Exit(); mover.Dispose();
                int stopped = clicks; Send(mover, 0x201, 0, IntPtr.Zero);
                check(clicks == stopped, "Disposed mover cannot forward late pointer events");
            }
        }
    }
}
