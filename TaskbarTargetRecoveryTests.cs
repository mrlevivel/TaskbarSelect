using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace TaskbarSelect
{
    // These providers deliberately block real background readers. No HWNDs,
    // UI Automation providers or desktop state are needed by the fixtures.
    internal static class TaskbarTargetRecoveryTests
    {
        // Inject preferred identity too: fake target providers must never fall
        // through to foreground-window/property-store reads on the real desktop.
        static TaskbarTargets.Probe Verify(TaskbarTargets targets, IntPtr bar, TaskbarMover.Point point)
        { return targets.Verify(bar, point, new TaskbarTargets.PreferredWindow(IntPtr.Zero, 0, 0)); }
        static void Request(TaskbarTargets targets, IntPtr bar, TaskbarMover.Point point)
        { targets.Request(bar, point, new TaskbarTargets.PreferredWindow(IntPtr.Zero, 0, 0)); }
        static readonly BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo snapshots = typeof(TaskbarTargets).GetField("snapshots", fields);
        static readonly FieldInfo previewSnapshot = typeof(TaskbarTargets).GetField("previewSnapshot", fields);
        static readonly MethodInfo queueCheck = typeof(TaskbarTargets).GetMethod("QueueCheck", fields);

        static bool Wait(Func<bool> condition, int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition()) {
                if (elapsed.ElapsedMilliseconds >= milliseconds) return condition();
                Thread.Sleep(2);
            }
            return true;
        }
        static void Drain(Action<bool, string> assert)
        { assert(Wait(delegate { return TaskbarTargets.ActiveReaders == 0; }, 2000), "Released recovery fixtures return every global reader lease"); }
        static void Miss(TaskbarTargets.Probe probe, Action<bool, string> assert, string name)
        {
            assert(probe.Complete && probe.Result == null && probe.Selection == null &&
                probe.SelectedWindow == IntPtr.Zero && !probe.NativeFallback, name);
        }
        static TaskbarTargets.Target Target(int value)
        {
            return new TaskbarTargets.Target {
                Taskbar = new IntPtr(value), Window = new IntPtr(100 + value), Process = (uint)(1000 + value),
                AppKey = "fixture-" + value, Captured = DateTime.UtcNow,
                TaskbarBounds = new Native.Rect { Left = 0, Top = 900, Right = 1600, Bottom = 960 },
                Button = new TaskbarMover.Click { AppId = "fixture-" + value,
                    ButtonBounds = new Native.Rect { Left = 10, Top = 900, Right = 50, Bottom = 950 } },
                Windows = new[] { new TaskbarTargets.WindowIdentity { Window = new IntPtr(100 + value), Process = (uint)(1000 + value) } }
            };
        }
        static TaskbarTargets.PreviewSelection Selection(TaskbarTargets.Target origin, int preview)
        {
            return new TaskbarTargets.PreviewSelection { Origin = origin, Preview = new IntPtr(preview),
                Window = origin.Window, Process = origin.Process, Captured = DateTime.UtcNow };
        }
        static TaskbarTargets.Probe Preview(TaskbarTargets targets, TaskbarTargets.Target origin, int preview)
        {
            // Queue the production preview work directly so the pure fixture does
            // not use VerifyPreview's native rectangle cache shortcut.
            return (TaskbarTargets.Probe)queueCheck.Invoke(targets, new object[] {
                new TaskbarTargets.Probe { Origin = origin, Preview = new IntPtr(preview), VerifyOrigin = true }
            });
        }
        static TaskbarTargets.Target[] Cached(TaskbarTargets targets)
        { return (TaskbarTargets.Target[])snapshots.GetValue(targets); }

        static void HealthyPolicy(Action<bool, string> assert)
        {
            var first = Target(1); var second = Target(2); var replacement = Target(1);
            TaskbarTargets.Target response = first;
            using (var targets = new TaskbarTargets(delegate { return response; })) {
                var one = Verify(targets, first.Taskbar, new TaskbarMover.Point());
                assert(Wait(delegate { return one.Complete; }, 700) && Object.ReferenceEquals(one.Result, first), "Healthy taskbar verification still returns its exact target");
                Drain(assert);
                response = second;
                var two = Verify(targets, second.Taskbar, new TaskbarMover.Point());
                assert(Wait(delegate { return two.Complete; }, 700) && Cached(targets).Length == 2, "Healthy cache retains a recent target on another taskbar");
                Drain(assert);
                response = replacement;
                var changed = Verify(targets, first.Taskbar, new TaskbarMover.Point());
                assert(Wait(delegate { return changed.Complete; }, 700), "Changed icon verification completes");
                var cache = Cached(targets);
                assert(cache.Length == 2 && Object.ReferenceEquals(cache[0], replacement) && Object.ReferenceEquals(cache[1], second), "A changed icon replaces only that taskbar's previous snapshot");
                Drain(assert);
                response = null;
                var missing = Verify(targets, first.Taskbar, new TaskbarMover.Point());
                assert(Wait(delegate { return missing.Complete; }, 700), "Healthy miss completes");
                Miss(missing, assert, "Healthy misses are empty completed probes");
                cache = Cached(targets);
                assert(cache.Length == 1 && Object.ReferenceEquals(cache[0], second), "A normal miss retains the other taskbar cache policy");
            }
            Drain(assert);
            int calls = 0;
            using (var targets = new TaskbarTargets(delegate {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("Injected target reader failure");
                return first;
            })) {
                var failed = Verify(targets, first.Taskbar, new TaskbarMover.Point());
                assert(Wait(delegate { return failed.Complete; }, 700), "A reader exception completes without escaping its background thread");
                Miss(failed, assert, "A reader exception fails closed"); Drain(assert);
                var recovered = Verify(targets, first.Taskbar, new TaskbarMover.Point());
                assert(Wait(delegate { return recovered.Complete; }, 700) && Object.ReferenceEquals(recovered.Result, first), "A reader exception does not prevent the next healthy lookup");
            }
            Drain(assert);
        }

        static void TimeoutAndLateReturn(Action<bool, string> assert)
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false)) {
                var old = Target(1); var fresh = Target(2); var cached = Target(9);
                var targets = new TaskbarTargets(delegate(IntPtr taskbar, TaskbarMover.Point point) {
                    if (taskbar == old.Taskbar) { entered.Set(); release.WaitOne(); return old; }
                    return taskbar == cached.Taskbar ? cached : fresh;
                });
                try {
                    var warm = Verify(targets, cached.Taskbar, new TaskbarMover.Point());
                    assert(Wait(delegate { return warm.Complete; }, 700), "Recovery fixture warms the old cache"); Drain(assert);
                    var elapsed = Stopwatch.StartNew();
                    var stalled = Verify(targets, old.Taskbar, new TaskbarMover.Point());
                    assert(entered.WaitOne(700), "The first injected provider really enters a blocked reader");
                    Thread.Sleep(100);
                    var next = Verify(targets, fresh.Taskbar, new TaskbarMover.Point());
                    assert(Wait(delegate { return stalled.Complete; }, 700) && elapsed.ElapsedMilliseconds < 750,
                        "A stalled provider completes before the caller's 750ms abandonment deadline");
                    Miss(stalled, assert, "A timed-out provider has no usable target or preview data");
                    assert(stalled.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.ReaderTimeout && stalled.Diagnostics.ReaderMilliseconds > 0,
                        "A retired running provider reports reader timeout separately from queue wait");
                    assert(Wait(delegate { return next.Complete; }, 700) && Object.ReferenceEquals(next.Result, fresh),
                        "A healthy queued lookup recovers while the original provider remains blocked");
                    assert(TaskbarTargets.ActiveReaders == 1 || TaskbarTargets.ActiveReaders == 2, "A retired blocked provider keeps its global reader lease");
                    var cache = Cached(targets);
                    assert(cache.Length == 1 && Object.ReferenceEquals(cache[0], fresh), "Recovery invalidates pre-stall snapshots before publishing a fresh target");
                    release.Set(); Drain(assert);
                    Miss(stalled, assert, "A late successful provider return cannot modify its already completed probe");
                    cache = Cached(targets);
                    assert(cache.Length == 1 && Object.ReferenceEquals(cache[0], fresh), "A late old target cannot replace the newer snapshot");
                }
                finally { release.Set(); targets.Dispose(); Drain(assert); }
            }
        }

        static void QueueDeadlineAndPriority(Action<bool, string> assert)
        {
            using (var firstEntered = new ManualResetEvent(false))
            using (var releaseFirst = new ManualResetEvent(false))
            using (var secondEntered = new ManualResetEvent(false))
            using (var releaseSecond = new ManualResetEvent(false)) {
                int hoverReads = 0;
                var targets = new TaskbarTargets(delegate(IntPtr taskbar, TaskbarMover.Point point) {
                    if (taskbar == new IntPtr(1)) { firstEntered.Set(); releaseFirst.WaitOne(); }
                    if (taskbar == new IntPtr(2)) { secondEntered.Set(); releaseSecond.WaitOne(); }
                    if (taskbar == new IntPtr(3)) Interlocked.Increment(ref hoverReads);
                    return Target(taskbar.ToInt32());
                });
                try {
                    var first = Verify(targets, new IntPtr(1), new TaskbarMover.Point());
                    assert(firstEntered.WaitOne(700), "Queued deadline fixture starts its first blocked reader");
                    Request(targets, new IntPtr(3), new TaskbarMover.Point());
                    var elapsed = Stopwatch.StartNew();
                    var second = Verify(targets, new IntPtr(2), new TaskbarMover.Point());
                    // Spend most of the second probe's total budget in the queue.
                    Thread.Sleep(300); releaseFirst.Set();
                    assert(Wait(delegate { return second.Complete; }, 390) && elapsed.ElapsedMilliseconds < 700,
                        "A queued probe's deadline is measured from submission rather than restarting when reading begins");
                    Miss(second, assert, "A provider that exhausts its remaining queue deadline fails closed");
                    bool entered = secondEntered.WaitOne(0);
                    assert(entered ? second.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.ReaderTimeout :
                        second.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.QueueTimeout ||
                        second.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.ReaderTimeout,
                        "An expired queued probe reports reader timeout when entered, or queue/assigned-reader timeout when scheduling prevents entry");
                    assert(Wait(delegate { return hoverReads == 0 && first.Complete; }, 50) && hoverReads == 0,
                        "A hover that expires while a click is reading is never sent to its provider");
                    releaseSecond.Set(); Drain(assert);
                    Miss(second, assert, "A late queued reader remains empty after its successful return");
                    assert(hoverReads == 0, "An expired hover stays discarded after the blocked click returns");
                }
                finally { releaseFirst.Set(); releaseSecond.Set(); targets.Dispose(); Drain(assert); }
            }
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false)) {
                var order = new List<int>(); var lastHover = new ManualResetEvent(false);
                var targets = new TaskbarTargets(delegate(IntPtr taskbar, TaskbarMover.Point point) {
                    lock (order) order.Add(taskbar.ToInt32());
                    if (taskbar == new IntPtr(1)) { entered.Set(); release.WaitOne(); }
                    if (taskbar == new IntPtr(4)) lastHover.Set();
                    return Target(taskbar.ToInt32());
                });
                try {
                    Verify(targets, new IntPtr(1), new TaskbarMover.Point()); assert(entered.WaitOne(700), "Hover coalescing fixture starts its blocked reader");
                    Request(targets, new IntPtr(3), new TaskbarMover.Point());
                    Request(targets, new IntPtr(4), new TaskbarMover.Point());
                    var click = Verify(targets, new IntPtr(2), new TaskbarMover.Point()); release.Set();
                    assert(Wait(delegate { return click.Complete; }, 700) && lastHover.WaitOne(700), "A queued click and the newest coalesced hover both run"); Drain(assert);
                    lock (order) assert(order.Count == 3 && order[0] == 1 && order[1] == 2 && order[2] == 4,
                        "Click priority and hover coalescing retain their existing ordering");
                }
                finally { release.Set(); targets.Dispose(); Drain(assert); lastHover.Dispose(); }
            }
        }

        static void QueueBoundAndDispose(Action<bool, string> assert)
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false)) {
                int reads = 0;
                var targets = new TaskbarTargets(delegate(IntPtr taskbar, TaskbarMover.Point point) {
                    Interlocked.Increment(ref reads);
                    if (taskbar == new IntPtr(1)) { entered.Set(); release.WaitOne(); }
                    return Target(taskbar.ToInt32());
                });
                try {
                    var active = Verify(targets, new IntPtr(1), new TaskbarMover.Point()); assert(entered.WaitOne(700), "Dispose fixture has a real in-flight provider");
                    var pending = new List<TaskbarTargets.Probe>();
                    for (int index = 0; index < 12; index++) pending.Add(Verify(targets, new IntPtr(10 + index), new TaskbarMover.Point()));
                    for (int index = 0; index < 4; index++) Miss(pending[index], assert, "The bounded queue evicts older verification " + index);
                    assert(pending[0].Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.QueueEvicted &&
                        pending[0].Diagnostics.Stage == TaskbarTargets.DiagnosticStage.Queued,
                        "Queue eviction is diagnosed without pretending its provider timed out");
                    for (int index = 4; index < 12; index++) assert(!pending[index].Complete, "Only eight waiting clicks are retained " + index);
                    Request(targets, new IntPtr(40), new TaskbarMover.Point());
                    var elapsed = Stopwatch.StartNew(); targets.Dispose();
                    assert(elapsed.ElapsedMilliseconds < 250, "Dispose never waits for a blocked UI Automation provider");
                    Miss(active, assert, "Dispose immediately completes the in-flight probe");
                    assert(active.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.Disposed, "Disposal records a distinct closed probe outcome");
                    foreach (var probe in pending) Miss(probe, assert, "Dispose immediately completes every queued probe");
                    assert(TaskbarTargets.ActiveReaders == 1 && reads == 1 && Cached(targets).Length == 0 && previewSnapshot.GetValue(targets) == null,
                        "Dispose clears caches while the blocked reader still owns its lease");
                    Miss(Verify(targets, new IntPtr(2), new TaskbarMover.Point()), assert, "Verification after Dispose fails closed synchronously");
                    Miss(targets.VerifyPreview(Target(2), IntPtr.Zero, new TaskbarMover.Point()), assert, "Disposed preview verification cannot reuse a cached shortcut or call a provider");
                    release.Set(); Drain(assert);
                    Miss(active, assert, "A disposed reader's late return cannot publish any result");
                    assert(reads == 1, "Disposal does not start any queued or hover provider calls");
                }
                finally { release.Set(); targets.Dispose(); Drain(assert); }
            }
        }

        static void GlobalBudgetAndRecovery(Action<bool, string> assert)
        {
            using (var enteredOne = new ManualResetEvent(false))
            using (var enteredTwo = new ManualResetEvent(false))
            using (var releaseOne = new ManualResetEvent(false))
            using (var releaseTwo = new ManualResetEvent(false)) {
                var one = new TaskbarTargets(delegate { enteredOne.Set(); releaseOne.WaitOne(); return Target(1); });
                var two = new TaskbarTargets(delegate { enteredTwo.Set(); releaseTwo.WaitOne(); return Target(2); });
                try {
                    var first = Verify(one, new IntPtr(1), new TaskbarMover.Point()); assert(enteredOne.WaitOne(700), "Global budget fixture blocks its first provider"); one.Dispose();
                    var second = Verify(two, new IntPtr(2), new TaskbarMover.Point()); assert(enteredTwo.WaitOne(700), "Global budget fixture blocks a second provider in another instance");
                    assert(TaskbarTargets.ActiveReaders == 2, "The process-wide limit includes a disposed instance's still-running reader");
                    int rejectedReads = 0;
                    for (int index = 0; index < 12; index++) {
                        using (var extra = new TaskbarTargets(delegate { Interlocked.Increment(ref rejectedReads); return Target(3); })) {
                            var elapsed = Stopwatch.StartNew();
                            var rejected = Verify(extra, new IntPtr(3), new TaskbarMover.Point());
                            Miss(rejected, assert, "An exhausted global reader budget fails closed in fresh instance " + index);
                            assert(rejected.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.ReaderLimit &&
                                rejected.Diagnostics.ReadersAtQueue == 2 && rejected.Diagnostics.ReaderMilliseconds == 0,
                                "Reader-cap rejection records saturation without invoking or timing out a provider " + index);
                            assert(elapsed.ElapsedMilliseconds < 100 && TaskbarTargets.ActiveReaders == 2,
                                "A new resolver cannot accumulate more blocked readers " + index);
                            Request(extra, new IntPtr(4), new TaskbarMover.Point());
                        }
                    }
                    assert(rejectedReads == 0, "An exhausted global budget never invokes rejected providers");
                    assert(Wait(delegate { return second.Complete; }, 700), "The second blocked instance also completes at its deadline");
                    Miss(first, assert, "A disposed first instance stays empty at saturation"); Miss(second, assert, "A second timed-out instance stays empty at saturation");
                    releaseOne.Set();
                    assert(Wait(delegate { return TaskbarTargets.ActiveReaders == 1; }, 700), "A real provider return releases exactly one global lease");
                    var healthy = Target(5);
                    using (var recovered = new TaskbarTargets(delegate { return healthy; })) {
                        var probe = Verify(recovered, healthy.Taskbar, new TaskbarMover.Point());
                        assert(Wait(delegate { return probe.Complete; }, 700) && Object.ReferenceEquals(probe.Result, healthy),
                            "Fresh lookups recover once one lease returns even while the other provider remains blocked");
                    }
                    releaseTwo.Set(); Drain(assert);
                    Miss(second, assert, "A saturated reader's late return remains rejected after capacity recovers");
                }
                finally { releaseOne.Set(); releaseTwo.Set(); one.Dispose(); two.Dispose(); Drain(assert); }
            }
        }

        static void PreviewExpiry(Action<bool, string> assert, bool fallback)
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false)) {
                int fallbackReads = 0;
                var origin = Target(1); var fresh = Selection(origin, 20);
                var targets = new TaskbarTargets(delegate {
                    Interlocked.Increment(ref fallbackReads);
                    if (fallback) { entered.Set(); release.WaitOne(); }
                    return origin;
                }, delegate(TaskbarTargets.Target target, IntPtr preview, TaskbarMover.Point point) {
                    if (preview == new IntPtr(20)) return fresh;
                    if (!fallback) { entered.Set(); release.WaitOne(); }
                    return null;
                });
                try {
                    var old = Preview(targets, origin, 10);
                    assert(entered.WaitOne(700), "The injected " + (fallback ? "origin fallback" : "preview reader") + " really blocks");
                    assert(Wait(delegate { return old.Complete; }, 700), "A blocked preview stage completes at its total deadline");
                    Miss(old, assert, "An expired preview stage clears target, selection and native fallback");
                    var next = Preview(targets, origin, 20);
                    assert(Wait(delegate { return next.Complete; }, 700) && Object.ReferenceEquals(next.Selection, fresh) && next.SelectedWindow == origin.Window,
                        "A fresh preview selection recovers while its older stage remains blocked");
                    release.Set(); Drain(assert);
                    Miss(old, assert, "A late preview or origin return cannot revive its old probe");
                    assert(Object.ReferenceEquals(previewSnapshot.GetValue(targets), fresh), "Late preview work cannot replace a newer preview cache");
                    assert(fallbackReads == (fallback ? 1 : 0), "An expired preview reader never starts a new origin fallback stage");
                }
                finally { release.Set(); targets.Dispose(); Drain(assert); }
            }
            var expected = Target(2);
            using (var targets = new TaskbarTargets(delegate { return expected; }, delegate { throw new InvalidOperationException("Injected preview failure"); })) {
                var probe = Preview(targets, expected, 30);
                assert(Wait(delegate { return probe.Complete; }, 700) && probe.NativeFallback && Object.ReferenceEquals(probe.Result, expected),
                    "A prompt preview exception preserves the existing verified-origin fallback policy");
            }
            Drain(assert);
        }

        internal static void Run(Action<bool, string> assert)
        {
            Drain(assert);
            HealthyPolicy(assert);
            TimeoutAndLateReturn(assert);
            QueueDeadlineAndPriority(assert);
            QueueBoundAndDispose(assert);
            GlobalBudgetAndRecovery(assert);
            PreviewExpiry(assert, false);
            PreviewExpiry(assert, true);
            DiagnosticIsolation(assert);
            DiagnosticDifferences(assert);
            WeakSelectionFreshness(assert);
            HoverPreemption(assert);
            ActiveChecksNotPreempted(assert, false);
            ActiveChecksNotPreempted(assert, true);
        }
        static TaskbarTargets.Probe ActiveProbe(TaskbarTargets targets)
        {
            object work = typeof(TaskbarTargets).GetField("active", fields).GetValue(targets);
            return work == null ? null : (TaskbarTargets.Probe)work.GetType().GetField("Probe", fields).GetValue(work);
        }
        static void HoverPreemption(Action<bool, string> assert)
        {
            using (var hoverEntered = new ManualResetEvent(false)) using (var releaseHover = new ManualResetEvent(false))
                using (var clickEntered = new ManualResetEvent(false)) using (var releaseClick = new ManualResetEvent(false)) {
                    int reads = 0; var old = Target(1); var fresh = Target(2); var following = Target(3);
                    using (var targets = new TaskbarTargets(delegate(IntPtr taskbar, TaskbarMover.Point point) {
                        Interlocked.Increment(ref reads);
                        if (taskbar == old.Taskbar) { hoverEntered.Set(); releaseHover.WaitOne(); return old; }
                        if (taskbar == fresh.Taskbar) { clickEntered.Set(); releaseClick.WaitOne(); return fresh; }
                        return following;
                    })) {
                        try {
                            Request(targets, old.Taskbar, new TaskbarMover.Point());
                            assert(hoverEntered.WaitOne(700), "Priority fixture starts one speculative hover provider");
                            var hover = ActiveProbe(targets);
                            Thread.Sleep(230);
                            var elapsed = Stopwatch.StartNew();
                            var clicked = Verify(targets, fresh.Taskbar, new TaskbarMover.Point());
                            assert(clickEntered.WaitOne(150), "Click verification starts immediately without waiting for a blocked hover's450ms expiry");
                            assert(hover != null && hover.Complete && hover.Result == null && hover.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.HoverPreempted &&
                                TaskbarTargets.ActiveReaders == 2, "Hover publication is preempted while both real reader leases remain counted");
                            var next = Verify(targets, following.Taskbar, new TaskbarMover.Point());
                            assert(!clicked.Complete && !next.Complete && reads == 2 && TaskbarTargets.ActiveReaders == 2,
                                "A following click cannot preempt the active click or create a third provider thread");
                            Thread.Sleep(300); releaseClick.Set();
                            assert(Wait(delegate { return clicked.Complete; }, 200) && Object.ReferenceEquals(clicked.Result, fresh) && elapsed.ElapsedMilliseconds < 600 &&
                                clicked.Diagnostics.QueueMilliseconds < 150 && clicked.Diagnostics.ReaderMilliseconds >= 290,
                                "The priority click receives its full unchanged450ms budget and succeeds after300ms of provider work");
                            assert(Wait(delegate { return next.Complete; }, 500) && Object.ReferenceEquals(next.Result, following),
                                "The next click stays ordered and runs after the active click finishes");
                            releaseHover.Set(); Drain(assert);
                            var cache = Cached(targets);
                            assert(hover.Result == null && old.Diagnostics == null && hover.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.HoverPreempted &&
                                cache.Length == 2 && Object.ReferenceEquals(cache[0], following) && Object.ReferenceEquals(cache[1], fresh),
                                "A late retired hover cannot publish a result, rewrite its outcome or replace either newer click snapshot");
                        } finally { releaseHover.Set(); releaseClick.Set(); }
                    }
                    Drain(assert);
                }
        }
        static void ActiveChecksNotPreempted(Action<bool, string> assert, bool preview)
        {
            using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false)) {
                int secondReads = 0; var first = Target(1); var second = Target(2);
                using (var targets = new TaskbarTargets(delegate(IntPtr taskbar, TaskbarMover.Point point) {
                    if (taskbar == first.Taskbar) { entered.Set(); release.WaitOne(); return first; }
                    Interlocked.Increment(ref secondReads); return second;
                }, delegate(TaskbarTargets.Target origin, IntPtr window, TaskbarMover.Point point) {
                    entered.Set(); release.WaitOne(); return Selection(origin, window.ToInt32());
                })) {
                    try {
                        var active = preview ? Preview(targets, first, 30) : Verify(targets, first.Taskbar, new TaskbarMover.Point());
                        assert(entered.WaitOne(700), "The active " + (preview ? "preview check" : "click check") + " reaches its owned provider");
                        var pending = Verify(targets, second.Taskbar, new TaskbarMover.Point());
                        Thread.Sleep(50);
                        assert(!active.Complete && !pending.Complete && secondReads == 0 && TaskbarTargets.ActiveReaders == 1,
                            "A new click preserves an already active " + (preview ? "preview check" : "click check") + " and its existing ordering");
                        release.Set();
                        assert(Wait(delegate { return active.Complete && pending.Complete; }, 700) && active.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.Matched &&
                            Object.ReferenceEquals(pending.Result, second), "Active priority checks complete normally before the next click is admitted");
                    } finally { release.Set(); }
                }
                Drain(assert);
            }
        }
        static void DiagnosticIsolation(Action<bool, string> assert)
        {
            using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false)) {
                var old = Target(1); old.Button.AppName = "Task Manager"; old.Button.AppId = "Microsoft.Windows.TaskManager";
                var fresh = Target(2); fresh.Button.AppName = "Private document title"; fresh.Button.AppId = @"C:\Private\application.exe";
                using (var targets = new TaskbarTargets(delegate(IntPtr bar, TaskbarMover.Point point) {
                    TaskbarTargets.NoteStage(TaskbarTargets.DiagnosticStage.ReadAppButton);
                    if (bar == old.Taskbar) {
                        TaskbarTargets.NoteUiaElements(27, 3); TaskbarTargets.NoteButton(old.Button);
                        entered.Set(); release.WaitOne();
                        // Retired providers can continue returning, but these
                        // notes must not rewrite their closed diagnostics.
                        TaskbarTargets.NoteStage(TaskbarTargets.DiagnosticStage.ResolveWindows);
                        TaskbarTargets.NoteUiaElements(Int32.MaxValue, Int32.MaxValue);
                        TaskbarTargets.NoteReaderError(unchecked((int)0x80004005));
                        return old;
                    }
                    TaskbarTargets.NoteUiaElements(12, 12); TaskbarTargets.NoteButton(fresh.Button);
                    return fresh;
                })) {
                    try {
                        var stalled = Verify(targets, old.Taskbar, new TaskbarMover.Point());
                        assert(entered.WaitOne(700), "Diagnostic fixture enters its owned blocked ReadAppButton stage");
                        var queued = new TaskbarTargets.Probe { Taskbar = new IntPtr(3), Deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 25 };
                        queueCheck.Invoke(targets, new object[] { queued });
                        assert(Wait(delegate { return queued.Complete; }, 300) && queued.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.QueueTimeout &&
                            queued.Diagnostics.Stage == TaskbarTargets.DiagnosticStage.Queued && queued.Diagnostics.QueueMilliseconds > 0 && queued.Diagnostics.ReaderMilliseconds == 0,
                            "A queued expiry is explicitly diagnosed without calling its provider");
                        assert(Wait(delegate { return stalled.Complete; }, 700), "The diagnostic blocked reader keeps the unchanged450ms budget");
                        var frozen = stalled.Diagnostics; string failure = TaskbarTargets.FailureDescription(stalled);
                        assert(frozen.Stage == TaskbarTargets.DiagnosticStage.ReadAppButton && frozen.Outcome == TaskbarTargets.DiagnosticOutcome.ReaderTimeout &&
                            frozen.UiaElements == 27 && frozen.UiaScanned == 3 && frozen.TaskManagerAppId == "Microsoft.Windows.TaskManager" &&
                            failure.Contains("ReadAppButton/ReaderTimeout") && failure.Length <= 250,
                            "Timeout freezes the exact stalled stage, numeric UIA progress and allowlisted Task Manager identity");
                        var recovered = Verify(targets, fresh.Taskbar, new TaskbarMover.Point());
                        assert(Wait(delegate { return recovered.Complete; }, 700) && recovered.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.Matched &&
                            recovered.Diagnostics.UiaElements == 12 && recovered.TaskManagerAppId == null && fresh.Diagnostics != null,
                            "A second reader has independent successful evidence and never stores another app's identifier or title");
                        TaskbarTargets.NoteReaderError(123); TaskbarTargets.NoteUiaElements(999, 999);
                        release.Set(); Drain(assert);
                        var after = stalled.Diagnostics;
                        assert(after.Stage == frozen.Stage && after.Outcome == frozen.Outcome && after.ElapsedMilliseconds == frozen.ElapsedMilliseconds &&
                            after.UiaElements == frozen.UiaElements && after.UiaScanned == frozen.UiaScanned && after.ErrorHResult == 0 &&
                            stalled.Result == null && old.Diagnostics == null && recovered.Diagnostics.UiaElements == 12 && recovered.Diagnostics.ErrorHResult == 0,
                            "Late/outside-thread notes cannot revive a stale result, mutate frozen evidence or cross-talk into the newer probe");
                    } finally { release.Set(); }
                }
                Drain(assert);
            }
            var manager = Target(4); manager.Button.AppName = "Task Manager"; manager.Button.AppId = @"C:\Private\taskmgr.exe";
            using (var targets = new TaskbarTargets(delegate {
                TaskbarTargets.NoteStage(TaskbarTargets.DiagnosticStage.ReadAppButton);
                TaskbarTargets.NoteUiaElements(Int32.MaxValue, Int32.MaxValue); TaskbarTargets.NoteButton(manager.Button);
                throw new InvalidOperationException("A private path or title must never enter diagnostic output");
            })) {
                var failed = Verify(targets, manager.Taskbar, new TaskbarMover.Point());
                assert(Wait(delegate { return failed.Complete; }, 700) && failed.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.ReaderError &&
                    failed.Diagnostics.ErrorHResult != 0 && failed.Diagnostics.UiaElements == 65535 && failed.Diagnostics.UiaScanned == 65535 &&
                    failed.TaskManagerAppId == "<non-identifier>" && !TaskbarTargets.FailureDescription(failed).Contains("Private"),
                    "Reader errors retain only HRESULT/bounded counters, with no exception text or paths even for a Task Manager button");
            }
            Drain(assert);
            using (var targets = new TaskbarTargets(delegate { TaskbarTargets.NoteStage(TaskbarTargets.DiagnosticStage.ReadAppButton); return null; })) {
                var missed = Verify(targets, IntPtr.Zero, new TaskbarMover.Point());
                assert(Wait(delegate { return missed.Complete; }, 700) && missed.Diagnostics.Outcome == TaskbarTargets.DiagnosticOutcome.ReaderMiss &&
                    !TaskbarTargets.FailureDescription(missed).Contains("Timeout"), "A prompt null result is a miss rather than a falsely reported timeout");
            }
            Drain(assert);
        }
        static void DiagnosticDifferences(Action<bool, string> assert)
        {
            var expected = Target(5);
            assert(TaskbarTargets.SameButtonDifference(expected, Target(5)) == "matched", "Identical taskbar identity has no invented mismatch");
            foreach (string field in new[] { "app-id", "taskbar-handle", "window-handle", "process-id", "selection-provenance", "process-start", "grouping", "button-bounds", "taskbar-bounds" }) {
                var changed = Target(5);
                if (field == "app-id") changed.Button.AppId = @"C:\Private\different.exe";
                if (field == "taskbar-handle") changed.Taskbar = new IntPtr(8);
                if (field == "window-handle") changed.Window = new IntPtr(8);
                if (field == "process-id") changed.Process++;
                if (field == "selection-provenance") changed.NeedsNativeSelection = true;
                if (field == "process-start") changed.ProcessStarted = 123456789;
                if (field == "grouping") changed.Button.Grouped = true;
                if (field == "button-bounds") changed.Button.ButtonBounds.Right++;
                if (field == "taskbar-bounds") changed.TaskbarBounds.Right++;
                assert(!TaskbarTargets.SameButton(expected, changed) && TaskbarTargets.SameButtonDifference(expected, changed) == field,
                    "Stale identity reports only the changed field category, preserving rejection and omitting actual values: " + field);
            }
            assert(TaskbarTargets.SameButtonDifference(expected, null) == "result-missing" && TaskbarTargets.FailureDescription(null) == "probe-missing",
                "Missing probes/results have distinct content-free diagnostic categories");
        }
        static TaskbarTargets.Target WeakTarget(int value, long started)
        {
            var target = Target(value);
            target.NeedsNativeSelection = true; target.ProcessStarted = started;
            target.Windows[0].NeedsNativeSelection = true; target.Windows[0].ProcessStarted = started;
            return target;
        }
        static void WeakSelectionFreshness(Action<bool, string> assert)
        {
            const long started = 638958240000000000;
            var strong = Target(6); var expected = WeakTarget(6, started); var current = WeakTarget(6, started);
            assert(!strong.NeedsNativeSelection && strong.ProcessStarted == 0 && !strong.Windows[0].NeedsNativeSelection && strong.Windows[0].ProcessStarted == 0,
                "Existing strong taskbar identities keep their original default provenance and lifetime metadata");
            assert(TaskbarTargets.SameButton(expected, current) && TaskbarTargets.SameWindows(expected.Windows, current.Windows),
                "The same weak native-selection provenance and verified process lifetime can be reverified");
            assert(!TaskbarTargets.SameButton(expected, strong) && !TaskbarTargets.SameWindows(expected.Windows, strong.Windows),
                "Freshness cannot silently upgrade a weak target into a directly movable strong target");
            var restarted = WeakTarget(6, started + 1);
            assert(!TaskbarTargets.SameButton(expected, restarted) && !TaskbarTargets.SameWindows(expected.Windows, restarted.Windows) &&
                TaskbarTargets.SameButtonDifference(expected, restarted) == "process-start",
                "The same HWND and PID from another process lifetime reject the old weak target without exposing timestamps");
            foreach (int change in new[] { 0, 1, 2, 3 }) {
                var response = WeakTarget(6, started);
                if (change == 1) response.Windows[0].NeedsNativeSelection = false;
                if (change == 2) response.Windows[0].ProcessStarted++;
                if (change == 3) { response.NeedsNativeSelection = false; response.ProcessStarted = 0; }
                using (var targets = new TaskbarTargets(delegate { return response; }, delegate { return null; })) {
                    var probe = Preview(targets, expected, 30);
                    assert(Wait(delegate { return probe.Complete; }, 700) && probe.NativeFallback == (change == 0) && probe.SelectedWindow == IntPtr.Zero,
                        "Verified-origin fallback preserves weak provenance and lifetime and never invents a selected window: " + change);
                }
                Drain(assert);
            }
        }
    }
}
