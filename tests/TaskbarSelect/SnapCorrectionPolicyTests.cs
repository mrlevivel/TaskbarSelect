using System;

namespace TaskbarSelect
{
    // Value ledgers/injected callbacks and the disabled release adapter run
    // here. Its timer is never started and no native observer is registered.
    // No native window/process provider, input, profile or filesystem is accessed.
    internal static class SnapCorrectionPolicyTests
    {
        sealed class Clock
        {
            internal long Now;
            internal bool Throw;
            internal readonly SnapCorrectionPolicy Policy;
            internal Clock() { Policy = new SnapCorrectionPolicy(delegate { if (Throw) throw new InvalidOperationException("Injected clock failure"); return Now; }, 1000); }
            internal void Settle() { Now += 5000; }
        }
        static SnapCorrectionPolicy Policy() { return new Clock().Policy; }
        static Native.Rect Rect(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        static AreaRecord Record(string identity = "neutral-monitor")
        {
            return new AreaRecord { Identity = identity, Monitor = Rect(-1920, 0, 0, 1080),
                Original = Rect(-1920, 0, 0, 1032), Applied = Rect(-1920, 0, 0, 1080) };
        }
        static SnapCorrectionArea Area(AreaRecord record)
        {
            SnapCorrectionArea area;
            if (!SnapCorrectionArea.TryCapture(record, out area)) throw new Exception("Invalid pure area fixture");
            return area;
        }
        static SnapCorrectionTarget Target(long handle = 1, uint process = 2, uint thread = 3, long started = 4)
        { return new SnapCorrectionTarget { Window = new IntPtr(handle), Process = process, Thread = thread, ProcessStarted = started }; }
        static bool Repair(SnapCorrectionPolicy policy, SnapCorrectionTarget target, SnapCorrectionArea area, Action mutation)
        {
            if (!policy.TryConsume(target, area)) return false;
            mutation(); return true;
        }
        static void ReleaseContainment(Action<bool, string> check)
        {
            check(!SnapWorkAreas.AutomaticGeometryCorrectionEnabled,
                "Private beta14 disables automatic geometry for both maximized and arranged windows");
            // Exercise the actual constructor/controller-facing lifecycle. A
            // process-filtered helper must not bypass the immutable policy.
            foreach (uint process in new uint[] { 0, 17 }) {
                using (var runtime = new SnapWorkAreas(process)) {
                    check(runtime.AutomaticGeometryInactive && runtime.PendingLayouts == 0,
                        "Construction starts no location hook, keyboard observer, timer or receipt");
                    runtime.EnableKeyboardIntent(delegate { throw new Exception("Disabled input must not ask for action authority"); });
                    runtime.ObserveInputForeground(new IntPtr(1));
                    runtime.PollKeyboardIntent();
                    check(runtime.AutomaticGeometryInactive,
                        "Desktop keyboard enablement and foreground polling cannot start physical-arrow credit");
                    // Null intake would fail before discovery if Update tried
                    // to consume it. Nonempty intake cannot trigger discovery.
                    runtime.Update(null);
                    runtime.Update(new[] { new SnapArea { Record = Record() } });
                    Native.Rect before, desired;
                    check(!runtime.TryCorrection(new IntPtr(1), out before, out desired) &&
                        AreaPolicy.Equal(before, new Native.Rect()) && AreaPolicy.Equal(desired, new Native.Rect()),
                        "Direct correction admission returns before any target read or mode-specific geometry");
                    runtime.BeginWakeEpoch(1);
                    runtime.Update(new[] { new SnapArea { Record = Record() } });
                    runtime.BeginWakeEpoch(2);
                    runtime.EnableKeyboardIntent(delegate { return true; });
                    check(runtime.AutomaticGeometryInactive && runtime.PendingLayouts == 0,
                        "Wake and rediscovery cannot enable correction or allocate even a resolved receipt");
                    var snapshot = runtime.DiagnosticSnapshot();
                    check(snapshot["HasAttempt"] == 0 && snapshot["SnapCorrectionAttempts"] == 0 &&
                        snapshot["EpochAttemptCount"] == 0,
                        "Disabled runtime reports no native correction attempt after wake and input paths");
                    var selected = new[] { Area(Record()) };
                    runtime.QuiesceForRestore(selected);
                    check(runtime.TryFinishRestore() && runtime.ResumeAfterRestore() && runtime.AutomaticGeometryInactive,
                        "Empty-receipt restoration can finish and resume without changing the automatic policy");
                    check(runtime.TryFinishExit(true) && runtime.PendingLayouts == 0 && runtime.AutomaticGeometryInactive,
                        "Exit has no foreign-window receipt to invert or retain after native intake stays off");
                }
            }
        }
        internal static int RunPureTest()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool value, string name) {
                if (!value) throw new Exception(name); count++;
            };
            try {
                ReleaseContainment(check);
                check(SnapCorrectionPolicy.IsApplicationRoot(new IntPtr(1), new IntPtr(1), "CabinetWClass"),
                    "A normal top-level Explorer application window remains eligible");
                check(!SnapCorrectionPolicy.IsApplicationRoot(new IntPtr(2), new IntPtr(1), "ApplicationClass") &&
                    !SnapCorrectionPolicy.IsApplicationRoot(IntPtr.Zero, IntPtr.Zero, "ApplicationClass"),
                    "Child and missing HWNDs cannot be native correction targets");
                check(!SnapCorrectionPolicy.IsApplicationRoot(new IntPtr(1), new IntPtr(1), null) &&
                    !SnapCorrectionPolicy.IsApplicationRoot(new IntPtr(1), new IntPtr(1), ""),
                    "Unavailable native class identity fails closed");
                foreach (string shell in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame" })
                    check(!SnapCorrectionPolicy.IsApplicationRoot(new IntPtr(1), new IntPtr(1), shell) &&
                        !SnapCorrectionPolicy.IsApplicationRoot(new IntPtr(1), new IntPtr(1), shell.ToLowerInvariant()),
                        "Taskbar, desktop and task-switcher roots cannot receive a snap correction");

                var maxArea = new AreaRecord { Identity = "neutral-max-monitor", Monitor = Rect(2560, 367, 4480, 1447),
                    Original = Rect(2560, 367, 4480, 1399), Applied = Rect(2560, 367, 4480, 1447) };
                var maxOuter = Rect(2552, 359, 4488, 1407); Native.Rect maxResult;
                check(SnapCorrectionGeometry.TryExpand(true, false, maxOuter, maxArea.Original, maxArea, out maxResult) &&
                    AreaPolicy.Equal(maxResult, Rect(2552, 359, 4488, 1455)),
                    "Exact full-area maximization reclaims 48 pixels while preserving every invisible outer margin");
                check(!SnapCorrectionGeometry.TryExpand(false, false, maxOuter, maxArea.Original, maxArea, out maxResult),
                    "A restored ordinary window cannot borrow maximized full-area authority");
                check(!SnapCorrectionGeometry.TryExpand(true, true, maxOuter, maxArea.Original, maxArea, out maxResult),
                    "Contradictory maximized and arranged observations fail closed");
                foreach (var mismatch in new[] { Rect(2561, 367, 4480, 1399), Rect(2560, 368, 4480, 1399),
                    Rect(2560, 367, 4479, 1399), Rect(2560, 367, 4480, 1398), Rect(2560, 367, 3520, 1399),
                    Rect(2560, 367, 4480, 1447), Rect(0, 0, 1920, 1032) })
                    check(!SnapCorrectionGeometry.TryExpand(true, false, maxOuter, mismatch, maxArea, out maxResult),
                        "Maximized repair rejects one-pixel, partial, already-expanded, oversized or foreign visible geometry");
                check(!SnapCorrectionGeometry.TryExpand(true, false, maxArea.Original, maxOuter, maxArea, out maxResult),
                    "A visible frame outside the outer rectangle cannot authorize maximized recovery");
                var edgeMonitor = Rect(-1920, -1080, 0, 0);
                foreach (var reserved in new[] { Rect(-1880, -1080, 0, 0), Rect(-1920, -1040, 0, 0),
                    Rect(-1920, -1080, -40, 0), Rect(-1920, -1080, 0, -40) }) {
                    var edgeArea = new AreaRecord { Identity = "neutral-edge", Monitor = edgeMonitor, Original = reserved, Applied = edgeMonitor };
                    var edgeOuter = Rect(reserved.Left - 8, reserved.Top - 8, reserved.Right + 8, reserved.Bottom + 8);
                    check(SnapCorrectionGeometry.TryExpand(true, false, edgeOuter, reserved, edgeArea, out maxResult) &&
                        AreaPolicy.Equal(maxResult, Rect(-1928, -1088, 8, 8)),
                        "Maximized recovery supports every reclaimed edge and negative monitor coordinates without rescaling borders");
                }
                var maxDock = new AreaRecord { Identity = "neutral-dock", Monitor = edgeMonitor,
                    Original = Rect(-1890, -1080, 0, -48), Applied = Rect(-1890, -1080, 0, 0) };
                check(SnapCorrectionGeometry.TryExpand(true, false, Rect(-1898, -1088, 8, -40), maxDock.Original, maxDock, out maxResult) &&
                    AreaPolicy.Equal(maxResult, Rect(-1898, -1088, 8, 8)),
                    "Maximized recovery preserves an unrelated appbar reservation on another edge");
                var overflowArea = new AreaRecord { Identity = "neutral-overflow", Monitor = Rect(0, 0, 640, Int32.MaxValue),
                    Original = Rect(0, 0, 640, Int32.MaxValue - 48), Applied = Rect(0, 0, 640, Int32.MaxValue) };
                check(!SnapCorrectionGeometry.TryExpand(true, false, Rect(-8, -8, 648, Int32.MaxValue), overflowArea.Original, overflowArea, out maxResult),
                    "Maximized border expansion rejects coordinate overflow instead of wrapping a native request");
                var wideArea = new AreaRecord { Identity = "neutral-wide", Monitor = Rect(Int32.MinValue, 0, 0, 1080),
                    Original = Rect(Int32.MinValue, 0, 0, 1032), Applied = Rect(Int32.MinValue, 0, 0, 1080) };
                check(!SnapCorrectionGeometry.TryExpand(true, false, Rect(Int32.MinValue, -8, 8, 1040), wideArea.Original, wideArea, out maxResult),
                    "Maximized correction rejects dimensions that cannot fit SetWindowPos integer width or height");
                var arrangedOuter = Rect(2552, 359, 3528, 1407); var arrangedVisible = Rect(2560, 367, 3520, 1399); Native.Rect legacyResult;
                check(SnapAreaPolicy.ExpandFrame(arrangedOuter, arrangedVisible, maxArea, out legacyResult) &&
                    SnapCorrectionGeometry.TryExpand(false, true, arrangedOuter, arrangedVisible, maxArea, out maxResult) &&
                    AreaPolicy.Equal(legacyResult, maxResult), "The existing arranged-window geometry path retains exactly its previous result");

                var target = Target(); var area = Area(Record());
                var unchanged = Policy(); int unchangedCalls = 0;
                check(Repair(unchanged, target, area, delegate { unchangedCalls++; }), "The first verified correction is admitted");
                for (int i = 0; i < 1000; i++) Repair(unchanged, target, Area(Record()), delegate { unchangedCalls++; });
                check(unchangedCalls == 1 && unchanged.AttemptCount == 1 && unchanged.AreaCount == 1,
                    "An ignored unchanged correction stays bounded across 1000 rediscovery and callback attempts");
                var sharedModeBudget = Policy();
                check(sharedModeBudget.TryConsume(target, Area(maxArea)) && !sharedModeBudget.TryConsume(target, Area(maxArea)),
                    "Arranged and maximized paths share one target-area budget instead of rearming through mode changes");

                var oscillation = Policy(); int oscillatingCalls = 0;
                Native.Rect frame = Rect(-1920, 0, -960, 1032);
                for (int i = 0; i < 1000; i++) {
                    // Native snap may alternate a split, reach our requested edge,
                    // then revert; none of those observations is a new authority.
                    frame = i % 3 == 0 ? Rect(-1920, 0, -960, 1032) :
                        i % 3 == 1 ? Rect(-1920, 0, -640, 1032) : Rect(-1920, 0, -960, 1080);
                    Repair(oscillation, target, area, delegate { oscillatingCalls++; frame.Bottom = 1080; });
                }
                check(oscillatingCalls == 1 && oscillation.AttemptCount == 1 && frame.Bottom > frame.Top,
                    "A/B snap splits and temporary success followed by reversion do not rearm correction");

                var rejected = Policy(); int rejectedCalls = 0; bool threw = false;
                try { Repair(rejected, target, area, delegate { rejectedCalls++; throw new InvalidOperationException("Injected native refusal"); }); }
                catch (InvalidOperationException) { threw = true; }
                check(threw && !Repair(rejected, target, area, delegate { rejectedCalls++; }) && rejectedCalls == 1,
                    "Mutation exceptions consume authority before the call and cannot create a retry loop");
                var reentrant = Policy(); int reentrantCalls = 0; bool nested = true;
                Repair(reentrant, target, area, delegate {
                    reentrantCalls++;
                    nested = Repair(reentrant, target, area, delegate { reentrantCalls++; });
                });
                check(!nested && reentrantCalls == 1, "A callback during the native request sees the already-consumed ledger");

                var snapshots = Policy(); var mutable = Record(); var before = Area(mutable);
                check(snapshots.TryConsume(target, before), "A valid immutable physical-area snapshot is accepted");
                mutable.Identity = "other-monitor"; var mutableOriginal = mutable.Original; mutableOriginal.Bottom--; mutable.Original = mutableOriginal;
                check(!snapshots.TryConsume(target, Area(Record("NEUTRAL-MONITOR"))),
                    "Fresh AreaRecord objects and identity casing cannot replenish a consumed physical area");
                check(before.Equals(Area(Record())) && before.GetHashCode() == Area(Record()).GetHashCode(),
                    "Later mutation of an AreaRecord cannot alter an already-stored fingerprint");
                check(snapshots.CanAttempt(Target(2), before), "A genuinely new verified window can still receive its first correction");
                check(!snapshots.CanAttempt(target, null) && !snapshots.CanAttempt(target, before),
                    "Temporary absence of a target does not clear its correction history");

                foreach (var invalid in new[] { Target(0), Target(1, 0), Target(1, 2, 0), Target(1, 2, 3, 0), Target(1, 2, 3, -1) }) {
                    var policy = Policy();
                    check(!policy.TryConsume(invalid, area) && policy.AttemptCount == 0 && policy.AreaCount == 0,
                        "Unavailable window/process/thread/creation identity fails closed without storing authority");
                }
                var reused = Policy(); reused.TryConsume(target, area);
                foreach (var replacement in new[] { Target(1, 9), Target(1, 2, 9), Target(1, 2, 3, 9), Target(9) })
                    check(!target.Equals(replacement) && reused.TryConsume(replacement, area),
                        "A changed HWND, process, thread or process creation is distinguished from the earlier target");

                SnapCorrectionArea invalidArea;
                check(!SnapCorrectionArea.TryCapture(null, out invalidArea), "A missing ownership record cannot authorize correction");
                var invalidRecord = Record(); invalidRecord.Identity = " ";
                check(!SnapCorrectionArea.TryCapture(invalidRecord, out invalidArea), "An unknown physical identity cannot authorize correction");
                invalidRecord = Record(new string('x', 513));
                check(!SnapCorrectionArea.TryCapture(invalidRecord, out invalidArea), "Physical identity storage has an explicit size limit");
                invalidRecord = Record(); invalidRecord.Original = invalidRecord.Applied;
                check(!SnapCorrectionArea.TryCapture(invalidRecord, out invalidArea), "An area with no reclaimed edge is rejected");
                invalidRecord = Record(); var invalidApplied = invalidRecord.Applied; invalidApplied.Bottom++; invalidRecord.Applied = invalidApplied;
                check(!SnapCorrectionArea.TryCapture(invalidRecord, out invalidArea), "An applied area outside its monitor is rejected");
                invalidRecord = Record(); var invalidOriginal = invalidRecord.Original; invalidOriginal.Left--; invalidRecord.Original = invalidOriginal;
                check(!SnapCorrectionArea.TryCapture(invalidRecord, out invalidArea), "An original area outside its applied claim is rejected");

                var dimensions = Policy(); dimensions.TryConsume(target, area);
                var monitorChanged = Record(); var changedMonitor = monitorChanged.Monitor; changedMonitor.Right++; monitorChanged.Monitor = changedMonitor;
                var originalChanged = Record(); var changedOriginal = originalChanged.Original; changedOriginal.Bottom--; originalChanged.Original = changedOriginal;
                var appliedChanged = Record(); var changedApplied = appliedChanged.Applied; changedApplied.Bottom--; appliedChanged.Applied = changedApplied;
                foreach (var changed in new[] { Record("second-monitor"), monitorChanged, originalChanged, appliedChanged })
                    check(!area.Equals(Area(changed)) && dimensions.TryConsume(target, Area(changed)),
                        "Physical identity and every ownership rectangle participate in the area fingerprint");
                check(!dimensions.TryConsume(target, area), "Returning to an earlier area in a topology cycle does not rearm it");

                var areaBudget = Policy();
                for (int i = 0; i < SnapCorrectionPolicy.MaximumAreas; i++)
                    if (!areaBudget.TryConsume(target, Area(Record("area-" + i)))) throw new Exception("Area budget fixture failed");
                check(!areaBudget.TryConsume(target, Area(Record("over-budget"))) && areaBudget.AreaCount == SnapCorrectionPolicy.MaximumAreas,
                    "New physical-area cycles stop at the fixed area-memory cap without eviction");
                check(areaBudget.TryConsume(Target(2), Area(Record("area-0"))), "An already-known area can admit a new window within the total budget");
                check(!areaBudget.TryConsume(target, Area(Record("area-0"))), "Area budget exhaustion never evicts and rearms earlier attempts");

                var epochClock = new Clock(); var totalBudget = epochClock.Policy;
                for (int i = 0; i < SnapCorrectionPolicy.MaximumAttempts; i++)
                    if (!totalBudget.TryConsume(Target(i + 1), area)) throw new Exception("Total budget fixture failed");
                for (int i = 0; i < 1000; i++) totalBudget.TryConsume(Target(1000 + i), Area(Record("extra-area-" + i)));
                check(totalBudget.AttemptCount == SnapCorrectionPolicy.MaximumAttempts && totalBudget.AreaCount == 1,
                    "Window and area churn cannot grow stored entries or native mutation authority past the global cap");
                check(!totalBudget.TryConsume(target, area) && !totalBudget.TryConsume(Target(9000), area),
                    "Global exhaustion fails closed for repeated and new windows instead of clearing the ledger");

                check(!totalBudget.BeginWakeEpoch(0) && !totalBudget.BeginWakeEpoch(-1) &&
                    totalBudget.AttemptCount == SnapCorrectionPolicy.MaximumAttempts && totalBudget.WakeEpoch == 0,
                    "Initial-On and invalid epoch values cannot replenish the startup mutation ledger");
                check(totalBudget.BeginWakeEpoch(1) && totalBudget.WakeEpoch == 1 && totalBudget.AttemptCount == 0 && totalBudget.AreaCount == 0,
                    "A new authoritative wake epoch retires old entries instead of accumulating epoch history");
                check(!totalBudget.CanAttempt(target, area) && !totalBudget.TryConsume(target, area) && totalBudget.AttemptCount == 0,
                    "Queue and final commit both deny native correction immediately after a verified wake");
                epochClock.Now = 4999;
                check(!totalBudget.CanAttempt(target, area), "The five-second post-wake quiet interval is not rounded down");
                epochClock.Settle();
                int wakeCalls = 0;
                check(Repair(totalBudget, target, area, delegate { wakeCalls++; }),
                    "A previously corrected live window can receive its one genuinely post-wake correction");
                check(!totalBudget.BeginWakeEpoch(1) && !totalBudget.BeginWakeEpoch(0) &&
                    !Repair(totalBudget, target, area, delegate { wakeCalls++; }) && wakeCalls == 1,
                    "Duplicate and stale power notifications cannot rearm an unchanged or reverted post-wake target");
                for (int epoch = 2; epoch <= 4; epoch++) {
                    if (!totalBudget.BeginWakeEpoch(epoch)) throw new Exception("Authoritative wake fixture failed");
                    epochClock.Settle();
                    Repair(totalBudget, target, area, delegate { wakeCalls++; });
                    for (int repeat = 0; repeat < 1000; repeat++) Repair(totalBudget, target, Area(Record()), delegate { wakeCalls++; });
                }
                check(wakeCalls == 4 && totalBudget.AttemptCount == 1 && totalBudget.AreaCount == 1 && totalBudget.WakeEpoch == 4,
                    "Three further actual wake epochs each admit one correction while repeated callbacks remain bounded");
                var staleClock = new Clock(); var stalePending = staleClock.Policy; long observedEpoch = stalePending.WakeEpoch;
                stalePending.BeginWakeEpoch(1);
                staleClock.Settle();
                check(!stalePending.TryConsume(target, area, observedEpoch) && stalePending.AttemptCount == 0,
                    "A before-wake pending snapshot cannot consume the new epoch ledger after a nested notification");
                check(stalePending.TryConsume(target, area, 1) && !stalePending.TryConsume(target, area, observedEpoch),
                    "Only a fresh current-epoch observation can commit after pending work was invalidated");

                var rollbackClock = new Clock(); rollbackClock.Now = 10000; rollbackClock.Policy.BeginWakeEpoch(1);
                rollbackClock.Now = 15000;
                check(rollbackClock.Policy.CanAttempt(target, area), "Five monotonic seconds opens the post-wake queue gate");
                rollbackClock.Now = 14999;
                check(!rollbackClock.Policy.TryConsume(target, area) && rollbackClock.Policy.AttemptCount == 0,
                    "Clock rollback between queue admission and final commit denies mutation without spending authority");
                rollbackClock.Now = 15000;
                check(rollbackClock.Policy.TryConsume(target, area), "Recovering the prior clock high-water mark allows one stable commit");
                rollbackClock.Policy.BeginWakeEpoch(2); rollbackClock.Now = 14000;
                check(!rollbackClock.Policy.CanAttempt(target, area), "A later epoch cannot exploit a backwards clock to shorten settling");
                rollbackClock.Now = 20000;
                check(rollbackClock.Policy.TryConsume(target, area), "The later epoch receives its own full five-second quiet interval");
                var brokenClock = new Clock(); brokenClock.Throw = true; brokenClock.Policy.BeginWakeEpoch(1);
                brokenClock.Throw = false; brokenClock.Now = 10000;
                check(!brokenClock.Policy.TryConsume(target, area), "An unavailable wake-start clock fails closed for that epoch");

                var diagnostic = new SnapCorrectionDiagnostics(); var emptySnapshot = diagnostic.Snapshot(0, 0, 0);
                check(emptySnapshot["HasAttempt"] == 0 && emptySnapshot["SnapCorrectionAttempts"] == 0 &&
                    emptySnapshot["LastReadbackValid"] == 0, "An unused diagnostic does not fabricate native activity or readback");
                var fromRect = Rect(-1920, 0, -960, 1032); var toRect = Rect(-1920, 0, -960, 1080);
                diagnostic.RecordAttempt(1, fromRect, toRect); diagnostic.RecordAccepted();
                var acceptedSnapshot = diagnostic.Snapshot(1, 1, 1);
                check(acceptedSnapshot["SnapCorrectionAttempts"] == 1 && acceptedSnapshot["SnapCorrectionAccepted"] == 1 &&
                    acceptedSnapshot["LastAttemptEpoch"] == 1 && acceptedSnapshot["LastObservedMatchesDesired"] == 0 &&
                    acceptedSnapshot["LastReadbackValid"] == 0,
                    "Native acceptance is counted separately and cannot imply asynchronous placement completion");
                diagnostic.Observe(true, true, fromRect); var ignoredSnapshot = diagnostic.Snapshot(1, 1, 1);
                check(ignoredSnapshot["LastIdentityValid"] == 1 && ignoredSnapshot["LastReadbackValid"] == 1 &&
                    ignoredSnapshot["LastObservedMatchesDesired"] == 0 && ignoredSnapshot["ObservedBottom"] == 1032,
                    "An accepted but unchanged native rectangle is honestly observable");
                diagnostic.Observe(true, true, toRect); var observedSnapshot = diagnostic.Snapshot(2, 0, 0);
                check(observedSnapshot["WakeEpoch"] == 2 && observedSnapshot["LastAttemptEpoch"] == 1 &&
                    observedSnapshot["LastObservedMatchesDesired"] == 1 && observedSnapshot["BeforeBottom"] == 1032 &&
                    observedSnapshot["DesiredBottom"] == 1080 && observedSnapshot["ObservedBottom"] == 1080,
                    "A later matching readback remains attributed to the epoch of its actual native request");
                diagnostic.Observe(false, true, toRect); var invalidSnapshot = diagnostic.Snapshot(2, 0, 0);
                check(invalidSnapshot["LastIdentityValid"] == 0 && invalidSnapshot["LastReadbackValid"] == 0 &&
                    invalidSnapshot["LastObservedMatchesDesired"] == 0 && invalidSnapshot["ObservedBottom"] == 0,
                    "Lost target identity invalidates and clears readback rather than exposing another window's rectangle");
                check(invalidSnapshot.Count == 24 && !invalidSnapshot.ContainsKey("Window") && !invalidSnapshot.ContainsKey("Process") &&
                    !invalidSnapshot.ContainsKey("Thread") && !invalidSnapshot.ContainsKey("ProcessStarted") &&
                    !invalidSnapshot.ContainsKey("Title") && !invalidSnapshot.ContainsKey("Class"),
                    "The diagnostic snapshot has fixed numeric fields without target identity or content");
                diagnostic.RecordAttempt(2, fromRect, toRect, true); diagnostic.Observe(true, true, toRect, true);
                var maximizedSnapshot = diagnostic.Snapshot(2, 1, 1);
                check(maximizedSnapshot["LastModeMax"] == 1 && maximizedSnapshot["LastObservedMaximized"] == 1 &&
                    maximizedSnapshot["LastReadbackValid"] == 1,
                    "Maximized request mode and identity-verified observed zoomed state have separate numeric proof");
                diagnostic.Observe(false, true, toRect, true);
                check(diagnostic.Snapshot(2, 1, 1)["LastObservedMaximized"] == 0,
                    "An invalid target identity cannot claim a preserved maximized state");

                Console.WriteLine("PASS: " + count + " managed snap-convergence and disabled-adapter checks; no native window/process provider, observer registration, timer start, input, profile or filesystem access.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
