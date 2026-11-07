using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class MinimumWorkerTests
    {
        [StructLayout(LayoutKind.Sequential)] struct MinimumInfo
        { internal Point Reserved, MaximumSize, MaximumPosition, MinimumTrack, MaximumTrack; }
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        class OwnedWindow : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override void WndProc(ref Message message)
            {
                base.WndProc(ref message);
                if (message.Msg == 0x0024 && message.LParam != IntPtr.Zero) {
                    var info = (MinimumInfo)Marshal.PtrToStructure(message.LParam, typeof(MinimumInfo));
                    info.MinimumTrack = new Point(320, 240);
                    Marshal.StructureToPtr(info, message.LParam, false);
                }
            }
        }
        sealed class BlockingWindow : OwnedWindow
        {
            readonly ManualResetEvent entered, resume, returned;
            internal bool Armed, Blocked;
            internal volatile bool WatchdogExpired;
            internal BlockingWindow(ManualResetEvent entered, ManualResetEvent resume, ManualResetEvent returned)
            { this.entered = entered; this.resume = resume; this.returned = returned; }
            protected override void WndProc(ref Message message)
            {
                bool wait = Armed && !Blocked && message.Msg == 0x0024;
                if (wait) {
                    Blocked = true; entered.Set();
                    // A kernel wait avoids managed STA message pumping. This
                    // target owns no user app and is always released in Dispose.
                    if (WaitForSingleObject(resume.SafeWaitHandle.DangerousGetHandle(), 5000) != 0) WatchdogExpired = true;
                }
                base.WndProc(ref message);
                if (wait) returned.Set();
            }
        }
        sealed class Geometry
        {
            internal Native.Rect Bounds;
            internal WindowHistory.Entry Placement;
            internal static Geometry Read(IntPtr window)
            {
                Native.Rect bounds; var placement = WindowHistory.Capture(window);
                return placement != null && Native.GetWindowRect(window, out bounds) ? new Geometry { Bounds = bounds, Placement = placement } : null;
            }
        }
        sealed class BlockingFixture : IDisposable
        {
            readonly ManualResetEvent ready = new ManualResetEvent(false);
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Resume = new ManualResetEvent(false), Returned = new ManualResetEvent(false);
            readonly Thread thread;
            BlockingWindow form;
            Exception error;
            volatile bool closing;
            internal IntPtr Window;
            internal Geometry Before;
            internal BlockingFixture()
            {
                thread = new Thread(delegate() {
                    try {
                        using (var window = new BlockingWindow(Entered, Resume, Returned) { Text = "Owned minimum-size timeout fixture",
                            ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Bounds = FixtureBounds() }) {
                            form = window;
                            if (closing) return;
                            window.Show(); Pump(30);
                            Window = window.Handle; Before = Geometry.Read(Window);
                            window.Armed = true; ready.Set();
                            if (!closing) Application.Run(window);
                        }
                    } catch (Exception caught) { error = caught; }
                    finally { ready.Set(); }
                });
                thread.IsBackground = true; thread.Name = "Owned minimum-size target"; thread.SetApartmentState(ApartmentState.STA);
                try {
                    thread.Start();
                    if (!Wait(ready, 5000)) throw new TimeoutException("Owned minimum-size target did not initialize.");
                    if (error != null) throw new InvalidOperationException("Owned minimum-size target failed.", error);
                    if (Window == IntPtr.Zero || Before == null) throw new InvalidOperationException("Owned minimum-size target has no verified native snapshot.");
                } catch { Dispose(); throw; }
            }
            internal bool WatchdogExpired { get { return form != null && form.WatchdogExpired; } }
            internal void Release()
            {
                Resume.Set();
                if (!Wait(Returned, 1000)) throw new TimeoutException("Owned minimum-size target did not return from its native wait.");
            }
            public void Dispose()
            {
                closing = true; Resume.Set(); var window = form;
                if (window != null && !window.IsDisposed && window.IsHandleCreated) {
                    try { window.BeginInvoke((MethodInvoker)delegate { window.Close(); }); }
                    catch (InvalidOperationException) { }
                }
                var elapsed = Stopwatch.StartNew();
                while (thread.IsAlive && elapsed.ElapsedMilliseconds < 6000) Pump(5);
                if (thread.IsAlive) throw new TimeoutException("Owned minimum-size target did not exit; no thread was aborted.");
                ready.Dispose(); Entered.Dispose(); Resume.Dispose(); Returned.Dispose();
            }
        }
        static Rectangle FixtureBounds()
        {
            var screen = Screen.PrimaryScreen; var work = screen == null ? new Rectangle(0, 0, 800, 600) : screen.WorkingArea;
            return new Rectangle(work.Left + Math.Min(20, work.Width / 8), work.Top + Math.Min(20, work.Height / 8),
                Math.Max(1, Math.Min(480, work.Width * 3 / 4)), Math.Max(1, Math.Min(360, work.Height * 3 / 4)));
        }
        static void Pump(int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            do { Application.DoEvents(); Thread.Sleep(5); } while (elapsed.ElapsedMilliseconds < milliseconds);
        }
        static bool Wait(WaitHandle handle, int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (!handle.WaitOne(0) && elapsed.ElapsedMilliseconds < milliseconds) Pump(5);
            return handle.WaitOne(0);
        }
        static long Deadline(int milliseconds = 1000)
        { return Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000; }
        static bool SameGeometry(Geometry a, Geometry b)
        {
            return a != null && b != null && AreaPolicy.Equal(a.Bounds, b.Bounds) &&
                a.Placement.Placement.ShowCommand == b.Placement.Placement.ShowCommand &&
                a.Placement.Placement.Flags == b.Placement.Placement.Flags &&
                AreaPolicy.Equal(a.Placement.Placement.NormalPosition, b.Placement.Placement.NormalPosition);
        }
        static void CheckHelpers(Action<bool, string> check)
        {
            var elapsed = Stopwatch.StartNew();
            while ((WindowMoveWorker.ActiveHelpers != 0 || WindowMoveWorker.UncertainHelpers != 0) && elapsed.ElapsedMilliseconds < 1000) Pump(5);
            check(WindowMoveWorker.ActiveHelpers == 0 && WindowMoveWorker.UncertainHelpers == 0,
                "Minimum query leaves no active or quarantined helper before the next operation");
        }
        internal static void Run(Action<bool, string> check)
        { Parser(check); NativeQueries(check); BlockedQuery(false, check); BlockedQuery(true, check); }
        static bool Parse(string value, out int status, out int width, out int height, out int error)
        { var bytes = Encoding.ASCII.GetBytes(value); return WindowMoveWorker.ParseMinimumOutput(bytes, bytes.Length, out status, out width, out height, out error); }
        static void Parser(Action<bool, string> check)
        {
            int status, width, height, error;
            check(Parse("1 320 240 0\n", out status, out width, out height, out error) && status == 1 && width == 320 && height == 240 && error == 0,
                "Minimum parser accepts the exact four-field success protocol");
            check(Parse("1 320 240 0\r\n", out status, out width, out height, out error) && width == 320 && height == 240,
                "Minimum parser accepts Windows CRLF framing");
            check(Parse("2 0 0 5\n", out status, out width, out height, out error) && status == 2 && error == 5,
                "Minimum parser preserves a native access-denied error without inventing dimensions");
            check(Parse("0 0 0 0\n", out status, out width, out height, out error) && status == 0,
                "Minimum parser distinguishes pre-call rejection from a successful read");
            check(Parse("2 0 0 1460\n", out status, out width, out height, out error) && error == 1460,
                "Minimum parser preserves native ERROR_TIMEOUT");
            check(Parse("1 2147483647 2147483647 0\n", out status, out width, out height, out error) && width == Int32.MaxValue && height == Int32.MaxValue,
                "Minimum parser accepts integer boundary values without wrapping");
            string[] invalid = {
                "", "\n", "1 320 240 0", "1 320 240 0\r", "1 320 240 0\ntrailing", "1 320 240 0\n\n",
                "1 320 240 0 0\n", "1 320 240\n", "1  320 240 0\n", " 1 320 240 0\n", "1 320 240 0 \n",
                "1\t320 240 0\n", "1 320\r240 0\n", "1 3e2 240 0\n", "1 320.0 240 0\n", "1 0x140 240 0\n",
                "3 320 240 0\n", "-1 320 240 0\n", "1 -320 240 0\n", "1 320 -240 0\n", "1 0 240 0\n", "1 320 0 0\n",
                "1 320 240 5\n", "1 320 240 -5\n", "0 -1 0 0\n", "2 0 -1 0\n", "2 0 0 -1\n",
                "1 2147483648 240 0\n", "1 320 2147483648 0\n", "2 0 0 2147483648\n", "1 --1 240 0\n",
                "1 320 240 \0\n", "1 320 240 0000000000000000000000000000000000000000000000000000000\n"
            };
            for (int index = 0; index < invalid.Length; index++)
                check(!Parse(invalid[index], out status, out width, out height, out error), "Minimum parser rejects malformed, negative, overflowing or unframed payload " + index);
            var good = Encoding.ASCII.GetBytes("1 320 240 0\n");
            check(!WindowMoveWorker.ParseMinimumOutput(null, 1, out status, out width, out height, out error), "Minimum parser rejects a null byte buffer");
            check(!WindowMoveWorker.ParseMinimumOutput(good, -1, out status, out width, out height, out error) &&
                !WindowMoveWorker.ParseMinimumOutput(good, good.Length + 1, out status, out width, out height, out error),
                "Minimum parser checks supplied length before indexing the buffer");
            var oversized = new byte[49]; for (int index = 0; index < oversized.Length - 1; index++) oversized[index] = 32; oversized[48] = 10;
            check(!WindowMoveWorker.ParseMinimumOutput(oversized, oversized.Length, out status, out width, out height, out error),
                "Minimum parser refuses payloads exceeding its fixed byte budget");
        }
        static void NativeQueries(Action<bool, string> check)
        {
            using (var window = new OwnedWindow { Text = "Owned minimum-size healthy fixture", ShowInTaskbar = false, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = FixtureBounds() }) {
                window.Show(); Pump(30); var before = Geometry.Read(window.Handle);
                check(before != null, "Minimum fixture captures an owned native window before any query");
                var identity = before.Placement;
                var result = WindowMoveWorker.Minimum(window.Handle, identity.Process, identity.ProcessStarted, Deadline(), delegate { return true; });
                check(result.Outcome == WindowMoveWorker.Outcome.Success && result.MinimumWidth == 320 && result.MinimumHeight == 240 &&
                    result.NativeError == 0 && !result.MutationAttempted && result.CallerExited,
                    "Minimum worker returns the owned app's actual positive tracking dimensions without mutation");
                check(SameGeometry(before, Geometry.Read(window.Handle)), "Successful minimum query preserves window bounds, placement and state");
                CheckHelpers(check);
                result = WindowMoveWorker.Minimum(window.Handle, identity.Process + 1, identity.ProcessStarted, Deadline(), delegate { return true; });
                check(result.Outcome == WindowMoveWorker.Outcome.Rejected && !result.MutationAttempted && result.CallerExited,
                    "Minimum worker rejects a stale PID before querying the window"); CheckHelpers(check);
                result = WindowMoveWorker.Minimum(window.Handle, identity.Process, identity.ProcessStarted + 1, Deadline(), delegate { return true; });
                check(result.Outcome == WindowMoveWorker.Outcome.Rejected && !result.MutationAttempted && result.CallerExited,
                    "Minimum worker rejects a reused process birth time before querying"); CheckHelpers(check);
                result = WindowMoveWorker.Minimum(new IntPtr(-1), identity.Process, identity.ProcessStarted, Deadline(), delegate { return true; });
                check(result.Outcome == WindowMoveWorker.Outcome.Rejected && !result.MutationAttempted,
                    "Minimum worker rejects a stale or invalid native handle"); CheckHelpers(check);
                result = WindowMoveWorker.Minimum(IntPtr.Zero, identity.Process, identity.ProcessStarted, Deadline(), delegate { return true; });
                check(result.Outcome == WindowMoveWorker.Outcome.Rejected && !result.MutationAttempted && result.CallerExited,
                    "A null minimum target is rejected without launching a helper");
                result = WindowMoveWorker.Minimum(window.Handle, 0, identity.ProcessStarted, Deadline(), delegate { return true; });
                check(result.Outcome == WindowMoveWorker.Outcome.Rejected && !result.MutationAttempted, "Minimum worker refuses a missing process identity");
                result = WindowMoveWorker.Minimum(window.Handle, identity.Process, 0, Deadline(), delegate { return true; });
                check(result.Outcome == WindowMoveWorker.Outcome.Rejected && !result.MutationAttempted, "Minimum worker refuses a missing process birth time");
                result = WindowMoveWorker.Minimum(window.Handle, identity.Process, identity.ProcessStarted, Deadline(), delegate { return false; });
                check(result.Outcome == WindowMoveWorker.Outcome.Cancelled && !result.MutationAttempted && result.CallerExited,
                    "Cancelled minimum query never launches or claims a mutation");
                result = WindowMoveWorker.Minimum(window.Handle, identity.Process, identity.ProcessStarted, Deadline(), delegate { throw new InvalidOperationException("Owned cancelled validation"); });
                check(result.Outcome == WindowMoveWorker.Outcome.Cancelled && !result.MutationAttempted, "Failed minimum authorization is cancellation, with no native mutation");
                result = WindowMoveWorker.Minimum(window.Handle, identity.Process, identity.ProcessStarted, Stopwatch.GetTimestamp() - 1, delegate { return true; });
                check(result.Outcome == WindowMoveWorker.Outcome.TimedOut && !result.MutationAttempted && result.CallerExited,
                    "Expired minimum operation never invokes USER or reports geometry uncertainty");
                check(SameGeometry(before, Geometry.Read(window.Handle)), "Rejected, cancelled and expired reads preserve the owned window exactly");
                CheckHelpers(check);
            }
        }
        static void BlockedQuery(bool cancel, Action<bool, string> check)
        {
            using (var target = new BlockingFixture())
            using (var heartbeat = new System.Windows.Forms.Timer { Interval = 10 })
            using (var cancellation = new System.Windows.Forms.Timer { Interval = 10 }) {
                bool current = true, cancelled = false; int beats = 0;
                long lastBeat = Stopwatch.GetTimestamp(), largestGap = 0;
                heartbeat.Tick += delegate {
                    long now = Stopwatch.GetTimestamp(); largestGap = Math.Max(largestGap, now - lastBeat); lastBeat = now; beats++;
                };
                cancellation.Tick += delegate {
                    if (!target.Entered.WaitOne(0)) return;
                    cancelled = true; current = false; cancellation.Stop();
                };
                heartbeat.Start(); if (cancel) cancellation.Start();
                var elapsed = Stopwatch.StartNew(); WindowMoveWorker.Result result;
                long duration, finalGap; int duringBeats;
                try {
                    result = WindowMoveWorker.Minimum(target.Window, target.Before.Placement.Process, target.Before.Placement.ProcessStarted,
                        Deadline(), delegate { return current; });
                    duration = elapsed.ElapsedMilliseconds; duringBeats = beats;
                    finalGap = Math.Max(largestGap, Stopwatch.GetTimestamp() - lastBeat);
                } finally { heartbeat.Stop(); cancellation.Stop(); }
                check(target.Entered.WaitOne(0) && !target.WatchdogExpired,
                    "Minimum request really enters the owned target's blocked WM_GETMINMAXINFO handler");
                check(result.Outcome != WindowMoveWorker.Outcome.Success && !result.MutationAttempted && duration < 1500,
                    "Blocked minimum query returns within native/operation timeout and remains explicitly read-only");
                check(duringBeats >= 1 && finalGap * 1000 / Stopwatch.Frequency < 300,
                    "Parent UI timer remains responsive while the owned minimum handler is blocked");
                if (cancel) check(cancelled && result.Outcome == WindowMoveWorker.Outcome.Cancelled && duration < 800,
                    "Live UI cancellation stops an entered minimum query promptly without mutation");
                else check(result.Outcome == WindowMoveWorker.Outcome.Failed || result.Outcome == WindowMoveWorker.Outcome.TimedOut,
                    "A blocked minimum query reports native failure or operation timeout, never success");
                CheckHelpers(check);
                var held = Geometry.Read(target.Window);
                check(SameGeometry(target.Before, held), "Timed-out minimum request does not change target geometry while its handler is held");
                target.Release(); Pump(60);
                check(SameGeometry(held, Geometry.Read(target.Window)), "Releasing a blocked minimum handler causes no delayed movement");
                result = WindowMoveWorker.Minimum(target.Window, target.Before.Placement.Process, target.Before.Placement.ProcessStarted,
                    Deadline(), delegate { return true; });
                check(result.Outcome == WindowMoveWorker.Outcome.Success && result.MinimumWidth == 320 && result.MinimumHeight == 240 && !result.MutationAttempted,
                    "A healthy minimum retry succeeds after timeout or cancellation");
                CheckHelpers(check);
                Console.WriteLine("PASS: native minimum " + (cancel ? "cancellation" : "timeout") + ", " + duration + " ms, " + duringBeats + " UI timer ticks.");
            }
        }
    }
}
