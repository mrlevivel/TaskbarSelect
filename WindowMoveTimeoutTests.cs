using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // The target thread owns only an invisible disposable Form. Native waits
    // deliberately stop its message processing without the STA pumping that a
    // managed wait can perform. Always release it; never abort or kill a thread.
    internal static class WindowMoveTimeoutTests
    {
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

        class OwnedWindow : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
        }
        sealed class BlockingWindow : OwnedWindow
        {
            internal readonly ManualResetEvent Entered, Resume, Returned;
            internal readonly int MessageToBlock;
            internal bool Armed, Blocked;
            internal volatile bool WatchdogExpired;

            internal BlockingWindow(int message, ManualResetEvent entered, ManualResetEvent resume, ManualResetEvent returned)
            { MessageToBlock = message; Entered = entered; Resume = resume; Returned = returned; }
            protected override void WndProc(ref Message message)
            {
                if (Armed && !Blocked && message.Msg == MessageToBlock) {
                    Blocked = true; Entered.Set();
                    if (WaitForSingleObject(Resume.SafeWaitHandle.DangerousGetHandle(), 5000) != 0) WatchdogExpired = true;
                    Returned.Set();
                }
                base.WndProc(ref message);
            }
        }
        sealed class Snapshot
        {
            internal Native.Rect Bounds;
            internal WindowHistory.Entry Entry;
            internal static Snapshot Read(IntPtr window)
            {
                Native.Rect bounds;
                var entry = WindowHistory.Capture(window);
                if (entry == null || !Native.GetWindowRect(window, out bounds)) return null;
                return new Snapshot { Bounds = bounds, Entry = entry };
            }
        }
        sealed class TargetFixture : IDisposable
        {
            readonly ManualResetEvent ready = new ManualResetEvent(false);
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
            internal readonly ManualResetEvent Resume = new ManualResetEvent(false);
            internal readonly ManualResetEvent Returned = new ManualResetEvent(false);
            readonly Thread thread;
            BlockingWindow form;
            Exception error;
            volatile bool closing;
            internal IntPtr Window;
            internal Snapshot Before;

            internal TargetFixture(Native.Rect work, int blockingMessage)
            {
                thread = new Thread(delegate() {
                    try {
                        using (var window = new BlockingWindow(blockingMessage, Entered, Resume, Returned) {
                            Text = "Owned native movement timeout target", ShowInTaskbar = false, Opacity = 0,
                            StartPosition = FormStartPosition.Manual, Bounds = FixtureBounds(work) }) {
                            form = window;
                            if (closing) return;
                            window.Show(); Pump(50);
                            Window = window.Handle; Before = Snapshot.Read(Window);
                            window.Armed = true; ready.Set();
                            if (!closing) Application.Run(window);
                        }
                    } catch (Exception caught) { error = caught; }
                    finally { ready.Set(); }
                });
                thread.IsBackground = true; thread.Name = "Owned movement timeout fixture";
                thread.SetApartmentState(ApartmentState.STA);
                try {
                    thread.Start();
                    if (!Wait(ready, 5000)) throw new TimeoutException("Owned movement timeout fixture did not initialize.");
                    if (error != null) throw new InvalidOperationException("Owned movement timeout fixture failed.", error);
                    if (Window == IntPtr.Zero || Before == null) throw new InvalidOperationException("Owned movement timeout fixture has no native snapshot.");
                } catch { Dispose(); throw; }
            }
            internal bool WatchdogExpired { get { return form != null && form.WatchdogExpired; } }
            internal void Release()
            {
                Resume.Set();
                if (!Wait(Returned, 1000)) throw new TimeoutException("Owned target did not leave its native wait after release.");
            }
            public void Dispose()
            {
                closing = true; Resume.Set();
                var window = form;
                if (window != null && !window.IsDisposed && window.IsHandleCreated) {
                    try { window.BeginInvoke((MethodInvoker)delegate { window.Close(); }); }
                    catch (InvalidOperationException) { }
                }
                var elapsed = Stopwatch.StartNew();
                while (thread.IsAlive && elapsed.ElapsedMilliseconds < 6000) Pump(5);
                if (thread.IsAlive) throw new TimeoutException("Owned target did not exit after release; its thread was not aborted.");
                ready.Dispose(); Entered.Dispose(); Resume.Dispose(); Returned.Dispose();
            }
        }
        static bool Wait(WaitHandle handle, int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (!handle.WaitOne(0) && elapsed.ElapsedMilliseconds < milliseconds) Pump(5);
            return handle.WaitOne(0);
        }
        static void Pump(int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            do { Application.DoEvents(); Thread.Sleep(5); } while (elapsed.ElapsedMilliseconds < milliseconds);
        }
        static Rectangle FixtureBounds(Native.Rect work)
        {
            int width = work.Right - work.Left, height = work.Bottom - work.Top;
            int mx = Math.Min(40, Math.Max(0, width / 6)), my = Math.Min(40, Math.Max(0, height / 6));
            return new Rectangle(work.Left + mx, work.Top + my,
                Math.Max(1, Math.Min(620, width - mx * 2)), Math.Max(1, Math.Min(460, height - my * 2)));
        }
        static bool SameGeometry(Snapshot a, Snapshot b)
        {
            return a != null && b != null && AreaPolicy.Equal(a.Bounds, b.Bounds) &&
                a.Entry.Placement.ShowCommand == b.Entry.Placement.ShowCommand &&
                AreaPolicy.Equal(a.Entry.Placement.NormalPosition, b.Entry.Placement.NormalPosition);
        }
        static void CheckHelpers(Action<bool, string> assert)
        {
            var elapsed = Stopwatch.StartNew();
            while (WindowMoveWorker.ActiveHelpers != 0 && elapsed.ElapsedMilliseconds < 1000) Pump(5);
            assert(WindowMoveWorker.ActiveHelpers == 0, "Native timeout helpers exit before another operation");
        }
        static void DetachDesktopHooks(TaskbarMover mover, Action<bool, string> assert)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags native = BindingFlags.Static | BindingFlags.NonPublic;
            // Exercise the real native movement path without accepting desktop
            // input or receiving unrelated window events during the UI pump.
            foreach (string name in new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" }) {
                var field = typeof(TaskbarMover).GetField(name, instance);
                var hook = (IntPtr)field.GetValue(mover);
                if (hook != IntPtr.Zero) {
                    string method = name == "mouseHook" ? "UnhookWindowsHookEx" : "UnhookWinEvent";
                    if (!(bool)typeof(TaskbarMover).GetMethod(method, native).Invoke(null, new object[] { hook }))
                        throw new Exception("Could not detach the timeout fixture's " + name);
                    field.SetValue(mover, IntPtr.Zero);
                }
                assert((IntPtr)field.GetValue(mover) == IntPtr.Zero,
                    "Native timeout fixture detaches " + name + " before enabling movement");
            }
        }
        static void HealthyRetry(TaskbarMover mover, Native.Rect sourceWork, IntPtr targetMonitor, Action<bool, string> assert)
        {
            using (var window = new OwnedWindow { Text = "Owned healthy movement retry", ShowInTaskbar = false, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = FixtureBounds(sourceWork) }) {
                window.Show(); Pump(30); var before = Snapshot.Read(window.Handle);
                assert(before != null && mover.MoveToMonitor(window.Handle, targetMonitor), "Healthy owned window moves after a native timeout or cancellation");
                assert(!SameGeometry(before, Snapshot.Read(window.Handle)), "Healthy retry applies real native movement");
                assert(mover.Undo(), "Healthy retry retains its own Undo"); Pump(80);
                assert(SameGeometry(before, Snapshot.Read(window.Handle)) && !mover.CanUndo, "Healthy retry Undo restores exact placement without duplicate history");
            }
            CheckHelpers(assert);
        }
        static void Scenario(Native.Rect sourceWork, IntPtr targetMonitor, bool afterChange, bool cancel, Action<bool, string> assert)
        {
            using (var target = new TargetFixture(sourceWork, afterChange ? 0x0047 : 0x0046))
            using (var mover = new TaskbarMover())
            using (var heartbeat = new System.Windows.Forms.Timer { Interval = 20 })
            using (var cancellation = new System.Windows.Forms.Timer { Interval = 20 }) {
                DetachDesktopHooks(mover, assert);
                mover.Enabled = true;
                int beats = 0; long lastBeat = Stopwatch.GetTimestamp(), largestGap = 0;
                bool cancelled = false;
                heartbeat.Tick += delegate {
                    long now = Stopwatch.GetTimestamp(); largestGap = Math.Max(largestGap, now - lastBeat); lastBeat = now; beats++;
                };
                cancellation.Tick += delegate {
                    if (!target.Entered.WaitOne(0)) return;
                    cancellation.Stop(); cancelled = true; mover.Enabled = false; mover.CancelPending();
                };
                heartbeat.Start(); if (cancel) cancellation.Start();
                var elapsed = Stopwatch.StartNew(); bool moved;
                long duration, finalGap; int duringBeats;
                try {
                    moved = mover.MoveToMonitor(target.Window, targetMonitor);
                    duration = elapsed.ElapsedMilliseconds; duringBeats = beats;
                    finalGap = Math.Max(largestGap, Stopwatch.GetTimestamp() - lastBeat);
                } finally { heartbeat.Stop(); cancellation.Stop(); }
                assert(target.Entered.WaitOne(0) && !target.WatchdogExpired, "Owned target enters its native handler wait and does not need its watchdog");
                assert(!moved && duration < 1500, "Native move returns within its 1000ms budget and bounded cleanup");
                assert(duringBeats >= (cancel ? 1 : 3) && finalGap * 1000 / Stopwatch.Frequency < 300,
                    "Main UI timer continues throughout the native wait, including the trailing interval");
                if (cancel) assert(cancelled && duration < 800 && mover.LastStatus == "Window move cancelled",
                    "CancelPending from a live UI event promptly stops an entered native move and reports cancellation");
                else assert(duration >= WindowMoveCoordinator.DefaultTimeoutMilliseconds - 100,
                    "An actual native timeout consumes its operation budget rather than failing early");
                CheckHelpers(assert);
                IntPtr foreground = GetForegroundWindow();
                assert(foreground != target.Window && mover.CanUndo, "A timed-out target is not focused and its uncertain mutation remains undoable");
                // Read the committed native state while the target is still held.
                // A second move immediately on resume must not become our baseline.
                var held = Snapshot.Read(target.Window);
                target.Release(); Pump(100); var after = Snapshot.Read(target.Window);
                Pump(200); var stable = Snapshot.Read(target.Window);
                assert(SameGeometry(held, after) && SameGeometry(after, stable) && GetForegroundWindow() == foreground,
                    "Resuming the owned target produces no delayed movement or focus");
                assert(afterChange ? !SameGeometry(target.Before, held) : SameGeometry(target.Before, held),
                    "Native handler stage distinguishes already-applied geometry from a cancelled initial change");
                mover.Enabled = true;
                assert(mover.Undo(), "Uncertain native move can be undone after its target resumes"); Pump(80);
                assert(SameGeometry(target.Before, Snapshot.Read(target.Window)) && !mover.CanUndo,
                    "Uncertain Undo restores original native placement exactly once");
                HealthyRetry(mover, sourceWork, targetMonitor, assert);
                Console.WriteLine("PASS: native movement " + (cancel ? "cancellation" : "timeout") +
                    " at " + (afterChange ? "WM_WINDOWPOSCHANGED" : "WM_WINDOWPOSCHANGING") +
                    ", " + duration + " ms, " + duringBeats + " UI timer ticks.");
            }
        }
        internal static void Run(Action<bool, string> assert)
        {
            var handles = new List<IntPtr>(); var infos = new List<Native.MonitorInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Native.Rect bounds, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(monitor, ref info)) { handles.Add(monitor); infos.Add(info); } return true;
            }, IntPtr.Zero);
            if (handles.Count < 2) { Console.WriteLine("SKIP: native movement timeout fixtures require two monitors."); return; }
            IntPtr prior = GetForegroundWindow();
            try {
                Scenario(infos[0].Work, handles[1], false, false, assert);
                Scenario(infos[0].Work, handles[1], true, false, assert);
                Scenario(infos[0].Work, handles[1], false, true, assert);
            } finally { if (prior != IntPtr.Zero && Native.IsWindow(prior)) SetForegroundWindow(prior); }
        }
    }
}
