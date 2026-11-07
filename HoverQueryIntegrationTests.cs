using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace TaskbarSelect
{
    // All readers, preview data, foreground preference and cadence clocks are
    // supplied here. No UIA provider, actual HWND lookup or input is invoked.
    internal static class HoverQueryIntegrationTests
    {
        sealed class Clock
        {
            long value = 1000;
            internal long Read() { return Interlocked.Read(ref value); }
            internal void Set(long next) { Interlocked.Exchange(ref value, next); }
        }
        static readonly TaskbarTargets.PreferredWindow preferred = new TaskbarTargets.PreferredWindow(new IntPtr(42), 7, 99);
        static readonly FieldInfo hoverField = typeof(TaskbarTargets).GetField("hover", BindingFlags.Instance | BindingFlags.NonPublic);
        static bool Wait(Func<bool> condition, int milliseconds = 1200)
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.ElapsedMilliseconds >= milliseconds) return condition(); Thread.Sleep(2); }
            return true;
        }
        static void Drain(Action<bool, string> check)
        { check(Wait(delegate { return TaskbarTargets.ActiveReaders == 0; }, 2000), "Injected hover fixtures release every real reader lease"); }
        static TaskbarMover.Point Point(int x = 10) { return new TaskbarMover.Point { X = x, Y = 20 }; }
        static TaskbarTargets.Target Target(int value = 1)
        {
            return new TaskbarTargets.Target { Taskbar = new IntPtr(value), Window = new IntPtr(40 + value), Process = 7, ProcessStarted = 99,
                Button = new TaskbarMover.Click { AppId = "Neutral.App", Position = Point() }, Captured = DateTime.UtcNow,
                Windows = new[] { new TaskbarTargets.WindowIdentity { Window = new IntPtr(40 + value), Process = 7, ProcessStarted = 99 } } };
        }
        internal static void Run(Action<bool, string> check)
        { Stationary(check); FailureRetry(check); Preview(check); ClickLease(check); LeavePreservesChecks(check); QueuedBackoff(check); }
        static void Stationary(Action<bool, string> check)
        {
            var clock = new Clock(); int calls = 0; var target = Target();
            using (var resolver = new TaskbarTargets(delegate { Interlocked.Increment(ref calls); return target; }, delegate { return null; }, new HoverQueryPolicy(clock.Read, 1000))) {
                resolver.Request(target.Taskbar, Point(), preferred); Drain(check);
                for (int i = 0; i < 1000; i++) { clock.Set(1200 + i * 200); resolver.Request(target.Taskbar, Point(), preferred); }
                check(calls == 1, "One thousand unchanged hover requests issue one injected provider call");
                var captured = target.Captured; clock.Set(300000); resolver.Request(target.Taskbar, Point(11), preferred); Drain(check);
                check(calls == 2 && target.Captured == captured, "Changed-point speculation runs once without retimestamping cached target authority");
                resolver.LeaveHover(); clock.Set(300200); resolver.Request(target.Taskbar, Point(11), preferred); Drain(check);
                check(calls == 3, "Real source exit permits same-coordinate reentry to rewarm once");
            }
            Drain(check);
        }
        static void FailureRetry(Action<bool, string> check)
        {
            var clock = new Clock(); int calls = 0; var target = Target();
            using (var resolver = new TaskbarTargets(delegate { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("Injected provider unavailable."); return target; },
                delegate { return null; }, new HoverQueryPolicy(clock.Read, 1000))) {
                resolver.Request(target.Taskbar, Point(), preferred); Drain(check);
                clock.Set(1999); resolver.Request(target.Taskbar, Point(), preferred); check(calls == 1, "A first failed hover waits for provider backoff");
                var fresh = resolver.Verify(target.Taskbar, Point(), preferred);
                check(Wait(delegate { return fresh.Complete; }) && Object.ReferenceEquals(fresh.Result, target), "Fresh click verification bypasses speculative backoff"); Drain(check);
                clock.Set(2000); resolver.Request(target.Taskbar, Point(), preferred); Drain(check);
                check(calls == 3, "The first failed unchanged hover gets its one delayed retry despite intervening explicit verification");
                clock.Set(100000); resolver.Request(target.Taskbar, Point(), preferred); check(calls == 3, "A recovered stationary hover does not restart a background query loop");
            }
            Drain(check);
            calls = 0; clock = new Clock();
            using (var resolver = new TaskbarTargets(delegate { Interlocked.Increment(ref calls); return null; }, delegate { return null; }, new HoverQueryPolicy(clock.Read, 1000))) {
                resolver.Request(new IntPtr(1), Point(), preferred); Drain(check); clock.Set(2000); resolver.Request(new IntPtr(1), Point(), preferred); Drain(check);
                clock.Set(1000000); resolver.Request(new IntPtr(1), Point(), preferred);
                check(calls == 2, "Repeated unchanged misses stop after one automatic retry");
            }
            Drain(check);
        }
        static void Preview(Action<bool, string> check)
        {
            var clock = new Clock(); int reads = 0; var origin = Target(); var preview = new IntPtr(90);
            using (var resolver = new TaskbarTargets(delegate { throw new InvalidOperationException("No origin fallback expected."); },
                delegate { if (Interlocked.Increment(ref reads) == 1) return null;
                    return new TaskbarTargets.PreviewSelection { Origin = origin, Preview = preview, Window = origin.Window, Process = origin.Process, Captured = DateTime.UtcNow }; },
                new HoverQueryPolicy(clock.Read, 1000))) {
                resolver.RequestPreview(origin, preview, Point()); Drain(check); clock.Set(1350); resolver.RequestPreview(origin, preview, Point()); Drain(check);
                check(reads == 2, "An initially empty thumbnail provider gets one retry instead of being mistaken for a matched origin");
                clock.Set(10000); resolver.RequestPreview(origin, preview, Point()); check(reads == 2, "A populated stationary preview remains quiescent");
            }
            Drain(check);
            clock = new Clock(); int originReads = 0, previewReads = 0;
            using (var resolver = new TaskbarTargets(delegate { Interlocked.Increment(ref originReads); throw new InvalidOperationException("Injected prefetch failure."); },
                delegate { Interlocked.Increment(ref previewReads); return new TaskbarTargets.PreviewSelection { Origin = origin, Preview = preview, Window = origin.Window, Process = origin.Process }; },
                new HoverQueryPolicy(clock.Read, 1000))) {
                resolver.Request(origin.Taskbar, Point(), preferred); Drain(check);
                // With no preview snapshot, VerifyPreview short-circuits its
                // optional native cache check and queues only this injected reader.
                var probe = resolver.VerifyPreview(origin, preview, Point());
                check(Wait(delegate { return probe.Complete; }) && probe.Selection != null && previewReads == 1 && originReads == 1,
                    "Explicit thumbnail verification bypasses hover backoff without any native cache lookup");
            }
            Drain(check);
        }
        static void ClickLease(Action<bool, string> check)
        {
            var clock = new Clock(); int reads = 0; var target = Target();
            using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false)) {
                var resolver = new TaskbarTargets(delegate { if (Interlocked.Increment(ref reads) == 1) { entered.Set(); release.WaitOne(); } return target; },
                    delegate { return null; }, new HoverQueryPolicy(clock.Read, 1000));
                try {
                    resolver.Request(target.Taskbar, Point(), preferred); check(entered.WaitOne(1000), "The injected speculative provider holds its real reader lease");
                    var click = resolver.Verify(target.Taskbar, Point(), preferred);
                    check(Wait(delegate { return click.Complete; }) && Object.ReferenceEquals(click.Result, target), "A fresh click uses the second slot while a retired hover remains blocked");
                    check(Wait(delegate { return TaskbarTargets.ActiveReaders == 1; }), "Retiring hover publication does not release its still-running provider lease");
                    clock.Set(3000); resolver.Request(new IntPtr(2), Point(30), preferred);
                    check(reads == 2 && TaskbarTargets.ActiveReaders == 1, "A new speculative query cannot occupy the click's reserved second slot");
                    click = resolver.Verify(target.Taskbar, Point(), preferred);
                    check(Wait(delegate { return click.Complete; }) && reads == 3, "Further real click verification remains available while the old hover is blocked");
                } finally { release.Set(); resolver.Dispose(); Drain(check); }
            }
        }
        static void LeavePreservesChecks(Action<bool, string> check)
        {
            var clock = new Clock(); int reads = 0; var target = Target();
            using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false)) {
                var resolver = new TaskbarTargets(delegate { if (Interlocked.Increment(ref reads) == 1) { entered.Set(); release.WaitOne(); } return target; },
                    delegate { return null; }, new HoverQueryPolicy(clock.Read, 1000));
                try {
                    var first = resolver.Verify(target.Taskbar, Point(), preferred); check(entered.WaitOne(1000), "The explicit verification fixture is pending");
                    resolver.Request(new IntPtr(2), Point(30), preferred);
                    var second = resolver.Verify(target.Taskbar, Point(), preferred); resolver.LeaveHover();
                    check(!second.Complete, "Leaving a hover source does not discard queued explicit verification");
                    release.Set();
                    check(Wait(delegate { return first.Complete && second.Complete; }) && first.Result != null && second.Result != null && reads == 2,
                        "Queued clicks finish in order after speculative work is dropped");
                } finally { release.Set(); resolver.Dispose(); Drain(check); }
            }
        }
        static void QueuedBackoff(Action<bool, string> check)
        {
            var clock = new Clock(); int reads = 0;
            using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false)) {
                var resolver = new TaskbarTargets(delegate { Interlocked.Increment(ref reads); entered.Set(); release.WaitOne(); return Target(); },
                    delegate { return null; }, new HoverQueryPolicy(clock.Read, 1000));
                try {
                    resolver.Request(new IntPtr(1), Point(), preferred); check(entered.WaitOne(1000), "A speculative timeout fixture entered its injected provider");
                    // Keep the queued deadline distinctly later than the active
                    // deadline. Only the real watchdog uses elapsed wall time.
                    Thread.Sleep(80);
                    clock.Set(1200); resolver.Request(new IntPtr(2), Point(30), preferred);
                    var queued = (TaskbarTargets.Probe)hoverField.GetValue(resolver);
                    check(queued != null && Wait(delegate { return queued.Complete; }) && queued.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.HoverBackoff && reads == 1,
                        "A hover queued before timeout is stopped by the newly learned backoff before provider dispatch");
                } finally { release.Set(); resolver.Dispose(); Drain(check); }
            }
        }
    }
}
