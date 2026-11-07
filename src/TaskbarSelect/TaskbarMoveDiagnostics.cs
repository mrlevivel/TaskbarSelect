using System;
using System.Globalization;

namespace TaskbarSelect
{
    // Numeric state only: no window titles, handles, executable paths or input
    // content. This has no authority over movement or recovery decisions.
    internal static class TaskbarMoveDiagnostics
    {
        internal static string Format(int reason, bool interrupted, bool mutation, bool knownFailure, bool busy)
        {
            string guard = reason >= 0 && reason <= (int)TaskbarMover.MovementValidityReason.OwnedSnapGuard ?
                reason.ToString(CultureInfo.InvariantCulture) : "unknown";
            return "lastGuard=" + guard + " interrupted=" + Bit(interrupted) + " mutation=" + Bit(mutation) +
                " knownFailure=" + Bit(knownFailure) + " busy=" + Bit(busy);
        }
        static string Bit(bool value) { return value ? "1" : "0"; }
    }
    internal enum TaskbarDirectDecision
    {
        Unknown, Accepted, Disposed, Busy, Disabled, Input, NoTarget, MissingButton, Shape,
        MissingShellProof, StaleShellProof, TaskbarChanged, ProcessChanged, Hidden, Excluded,
        MonitorUnknown, SameMonitor, Grouped, Fullscreen, Foreground, PointChanged
    }
    internal enum TaskbarGestureRoute
    {
        Down, DirectPending, Direct, NativePending, NativeMinimize, NativeActivation, FreshForeground,
        Preview, ProbeMiss, ChangedIcon, TimedOut, NormalMinimize, UnrelatedActivation, Cancelled, Excluded,
        DeferredPending, Deferred, DeferredReplay
    }
    // One bounded receipt. Probe text is frozen when observed; no mutable worker
    // object, window title, application ID, path, HWND or pointer position is retained.
    internal sealed class TaskbarGestureDiagnostics
    {
        readonly object gate = new object();
        int sequence, age, windows, verified = -1;
        bool primary, cacheFound, physical, native, proof, current, foreground, sameMonitor;
        bool freshFound, freshNative, freshProof, freshCurrent, stopped, terminal, success, replayed;
        TaskbarDirectDecision decision;
        TaskbarGestureRoute route;
        string cached = "none", fresh = "none";
        static string Bit(bool value) { return value ? "1" : "0"; }
        static int Bound(int value) { return Math.Max(0, Math.Min(9999, value)); }
        static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string Name(Enum value) { return Enum.IsDefined(value.GetType(), value) ? value.ToString() : "Unknown"; }
        internal void Begin(bool isPrimary, bool found, bool physicalInput, bool nativeSelection,
            bool proofPresent, bool proofCurrent, bool isForeground, bool sourceSameMonitor, int cacheAge,
            TaskbarTargets.Diagnostic cachedProbe)
        {
            lock (gate) {
                sequence = sequence >= 9999 ? 1 : sequence + 1;
                primary = isPrimary; cacheFound = found; physical = physicalInput; native = nativeSelection;
                proof = proofPresent; current = proofCurrent; foreground = isForeground; sameMonitor = sourceSameMonitor;
                age = Bound(cacheAge); windows = 0; verified = -1;
                freshFound = freshNative = freshProof = freshCurrent = stopped = terminal = success = replayed = false;
                decision = TaskbarDirectDecision.Unknown; route = TaskbarGestureRoute.Down;
                cached = ProbeText(cachedProbe); fresh = "none";
            }
        }
        internal void Direct(TaskbarDirectDecision value)
        { lock (gate) if (sequence != 0 && !terminal) { decision = value; if (value == TaskbarDirectDecision.Accepted) route = TaskbarGestureRoute.DirectPending; } }
        internal void Cache(bool found, bool nativeSelection, bool proofPresent, bool proofFresh, int cacheAge, TaskbarTargets.Diagnostic evidence)
        { lock (gate) if (sequence != 0 && !terminal) { cacheFound = found; native = nativeSelection; proof = proofPresent; current = proofFresh; age = Bound(cacheAge); cached = ProbeText(evidence); } }
        internal void DownContext(bool isForeground, bool sourceSameMonitor)
        { lock (gate) if (sequence != 0 && !terminal) { foreground = isForeground; sameMonitor = sourceSameMonitor; } }
        internal void Probe(TaskbarTargets.Diagnostic evidence, bool found, int count, bool nativeSelection, bool proofPresent, bool proofCurrent)
        {
            lock (gate) if (sequence != 0 && !terminal) {
                fresh = ProbeText(evidence); freshFound = found; windows = Bound(count);
                freshNative = nativeSelection; freshProof = proofPresent; freshCurrent = proofCurrent;
            }
        }
        internal void Verified(bool value) { lock (gate) if (sequence != 0 && !terminal) verified = value ? 1 : 0; }
        internal void Waiting(TaskbarGestureRoute value) { lock (gate) if (sequence != 0 && !terminal) route = value; }
        internal void Complete(TaskbarGestureRoute value, bool moved, bool inputReplayed)
        { lock (gate) if (sequence != 0 && !terminal) { route = value; success = moved; replayed = inputReplayed; terminal = true; } }
        internal void Stopped() { lock (gate) if (sequence != 0 && !terminal) stopped = true; }
        static string ProbeText(TaskbarTargets.Diagnostic value)
        {
            if (value == null) return "none";
            return Name(value.Stage) + "/" + Name(value.Outcome) + " ms=" + Number(Bound(value.ElapsedMilliseconds)) +
                " q=" + Number(Bound(value.QueueMilliseconds)) + " r=" + Number(Bound(value.ReaderMilliseconds)) +
                " uia=" + Number(Bound(value.UiaScanned)) + "/" + Number(Bound(value.UiaElements)) +
                " win=" + Number(Bound(value.CandidateWindows)) + "/" + Number(Bound(value.EnumeratedWindows)) +
                " match=" + Number(Bound(value.MatchedWindows)) + " " + TaskbarTargets.ShellProofDescription(value);
        }
        internal string Format()
        {
            lock (gate) {
                if (sequence == 0) return "none";
                return "seq=" + Number(sequence) + " bar=" + (primary ? "Primary" : "Secondary") +
                    " cache=" + Bit(cacheFound) + " age=" + Number(age) + " physical=" + Bit(physical) +
                    " native=" + Bit(native) + " proofPresent=" + Bit(proof) + " proofFresh=" + Bit(current) +
                    " foreground=" + Bit(foreground) + " sameMonitor=" + Bit(sameMonitor) +
                    " direct=" + Name(decision) + " route=" + Name(route) + " verified=" + Number(verified) +
                    " fresh=" + Bit(freshFound) + " windows=" + Number(windows) + " freshNative=" + Bit(freshNative) +
                    " freshProof=" + Bit(freshProof) + " freshProofFresh=" + Bit(freshCurrent) + " moved=" + Bit(success) +
                    " replayed=" + Bit(replayed) + " stopped=" + Bit(stopped) + " terminal=" + Bit(terminal) +
                    " cached[" + cached + "] fresh[" + fresh + "]";
            }
        }
    }
    internal enum TaskbarHookRoot { Primary, Secondary, ShellDesktop, ShellOther, App, Missing }
    internal sealed class TaskbarHookDiagnostics
    {
        readonly object gate = new object();
        int physical, injected, primary, secondary, shellOther, app, missing;
        long recognized, lastShellOther, lastDown;
        TaskbarHookRoot lastShellKind;
        static int Increment(int value) { return Math.Min(9999, value + 1); }
        internal void Record(bool physicalInput, bool left, TaskbarHookRoot root, long tick)
        {
            lock (gate) {
                lastDown = tick;
                if (physicalInput) physical = Increment(physical); else injected = Increment(injected);
                if (!left) return;
                if (root == TaskbarHookRoot.Primary) { primary = Increment(primary); recognized = tick; }
                else if (root == TaskbarHookRoot.Secondary) { secondary = Increment(secondary); recognized = tick; }
                else if (root == TaskbarHookRoot.ShellDesktop || root == TaskbarHookRoot.ShellOther) { shellOther = Increment(shellOther); lastShellOther = tick; lastShellKind = root; }
                else if (root == TaskbarHookRoot.Missing) missing = Increment(missing);
                else app = Increment(app);
            }
        }
        static string Age(long before, long now, long frequency)
        {
            if (before <= 0 || frequency <= 0 || now < before) return "-1";
            return ((int)Math.Min(9999, (now - before) * 1000.0 / frequency)).ToString(CultureInfo.InvariantCulture);
        }
        internal string Format(long now, long frequency)
        {
            lock (gate) return "physicalDown=" + physical.ToString(CultureInfo.InvariantCulture) +
                " injectedDown=" + injected.ToString(CultureInfo.InvariantCulture) + " primaryLeft=" + primary.ToString(CultureInfo.InvariantCulture) +
                " secondaryLeft=" + secondary.ToString(CultureInfo.InvariantCulture) + " shellOtherLeft=" + shellOther.ToString(CultureInfo.InvariantCulture) +
                " appLeft=" + app.ToString(CultureInfo.InvariantCulture) + " missingLeft=" + missing.ToString(CultureInfo.InvariantCulture) +
                " lastDownAge=" + Age(lastDown, now, frequency) + " recognizedAge=" + Age(recognized, now, frequency) + " shellOtherAge=" + Age(lastShellOther, now, frequency) +
                " shellOtherKind=" + (lastShellOther == 0 ? "None" : lastShellKind.ToString());
        }
    }
    internal sealed partial class TaskbarMover
    {
        readonly TaskbarGestureDiagnostics gestureDiagnostics = new TaskbarGestureDiagnostics();
        readonly TaskbarHookDiagnostics hookDiagnostics = new TaskbarHookDiagnostics();
        TaskbarDirectDecision lastDirectCheck;
        bool diagnosticGestureBegan;
        internal string TaskbarGestureDiagnostic { get { return gestureDiagnostics.Format(); } }
        internal string TaskbarHookDiagnostic
        {
            get { return "enabled=" + (Enabled ? "1" : "0") + " registeredMouse=" + (mouseHook != IntPtr.Zero ? "1" : "0") +
                " registeredForeground=" + (foregroundHook != IntPtr.Zero ? "1" : "0") + " " +
                hookDiagnostics.Format(System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency); }
        }
        internal string MovementDiagnostic
        {
            get { return TaskbarMoveDiagnostics.Format(lastValidityReason, movementInterrupted, movementMayHaveChanged,
                movementKnownFailure, movement.Busy); }
        }
    }
}
