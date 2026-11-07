using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Explicitly selected boundary test only; never part of any pure suite.
    // Every native target is the message-only HWND created by this invocation.
    internal static class SnapLayoutPropertyNativeTests
    {
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        static Native.Rect Rect(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }
        internal static int Run()
        {
            var window = new NativeWindow();
            var adapter = new SnapLayoutNative();
            var target = new SnapCorrectionTarget();
            try {
                // HWND_MESSAGE has no display surface, z-order or activation.
                window.CreateHandle(new CreateParams { Parent = new IntPtr(-3), Style = 0, ExStyle = 0 });
                uint process; uint thread = Native.GetWindowThreadProcessId(window.Handle, out process);
                Require(process == GetCurrentProcessId() && thread == GetCurrentThreadId(), "Message-only target must belong to this test process and thread.");
                target = new SnapCorrectionTarget { Window = window.Handle, Process = process, Thread = thread,
                    ProcessStarted = NativeProcessIdentity.Started(process) };
                Require(target.Valid, "Own test lifetime metadata must be readable.");
                var absent = adapter.Read(target);
                Require(absent.Kind == SnapLayoutReadKind.Current && absent.Target.Equals(target) && absent.Hidden && absent.Nonce == 0,
                    "Actual adapter must report the absent property as zero on its own verified hidden HWND.");

                // Synthetic values authorize only SetNonce. No geometry API,
                // monitor/taskbar lookup, SetBounds or visible form is used.
                SnapCorrectionArea area;
                Require(SnapCorrectionArea.TryCapture(new AreaRecord { Identity = "own-message-only-property-test",
                    Monitor = Rect(0, 0, 640, 480), Original = Rect(0, 0, 640, 432), Applied = Rect(0, 0, 640, 480) }, out area),
                    "Synthetic property-only area fixture must be valid.");
                var candidate = new SnapLayoutCandidate { Target = target, Area = area, Maximized = true,
                    Before = Rect(-8, -8, 648, 440), BeforeVisible = Rect(0, 0, 640, 432),
                    Desired = Rect(-8, -8, 648, 488), DesiredVisible = Rect(0, 0, 640, 480) };
                Require(adapter.Track(candidate), "Property-only candidate must be tracked without native geometry access.");
                Require(adapter.SetNonce(target, 1), "Actual SetProp and exact nonce readback must succeed on own HWND.");
                var marked = adapter.Read(target);
                Require(marked.Kind == SnapLayoutReadKind.Current && marked.Target.Equals(target) && marked.Hidden && marked.Nonce == 1,
                    "Actual adapter must read the exact nonzero nonce on the same own lifetime.");
                Require(adapter.RemoveNonce(target, 1), "Actual removal must return the owned nonce.");
                var removed = adapter.Read(target);
                Require(removed.Kind == SnapLayoutReadKind.Current && removed.Target.Equals(target) && removed.Hidden && removed.Nonce == 0,
                    "Actual adapter must report absence again after removing its property.");
                Console.WriteLine("PASS: own message-only HWND property absence, set, exact readback, remove and absence; no geometry or desktop mutation.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally {
                if (window.Handle != IntPtr.Zero) {
                    // Exact nonce and lifetime guards keep cleanup confined to
                    // this adapter's own property on this invocation's HWND.
                    try { if (target.Valid) adapter.RemoveNonce(target, 1); } catch (Exception) { }
                    window.DestroyHandle();
                }
            }
        }
    }
}
