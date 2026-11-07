using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace TaskbarSelect
{
    // Click checks take priority over hover refreshes. A stalled UI Automation
    // reader is retired without aborting it, so a second reader can recover.
    // The process-wide limit includes retired readers until they actually exit:
    // two permanently stalled providers fail closed without accumulating threads.
    internal sealed class TaskbarTargets : IDisposable
    {
        internal enum DiagnosticStage { Queued, Reader, ReadAppButton, TaskbarRectangle, ResolveWindows, WindowAppIdentity, AppCatalogIdentity, Preview, VerifyOrigin, Complete }
        internal enum DiagnosticOutcome { Pending, Matched, ReaderMiss, ReaderError, ReadAppButtonMiss, TaskbarRectangleFailed, QueueTimeout, ReaderTimeout, ReaderLimit, QueueEvicted, HoverReplaced, Disposed, ReaderStartFailed, HoverPreempted, HoverBackoff }
        internal enum ShellProofOutcome { NotApplicable, Pending, Grouped, BudgetSkipped, TargetMismatch, ShellMismatch, CacheHit, ReaderBusy, ReaderTimeout, ReadError, IdentityMismatch, RevalidationFailed, ProbeExpired, Proved }
        internal enum ShellProofPhase { None, Target, Shell, Cache, Reader, Identity, Complete }
        internal enum ButtonIdentityKind { None, Generated, Other }
        internal sealed class Diagnostic
        {
            internal DiagnosticStage Stage;
            internal DiagnosticOutcome Outcome;
            internal int ElapsedMilliseconds, QueueMilliseconds, ReaderMilliseconds, UiaElements, UiaScanned,
                EnumeratedWindows, CandidateWindows, MatchedWindows, ReadersAtQueue, ReadersAtStart, ErrorHResult;
            internal string TaskManagerAppId;
            internal ButtonIdentityKind ButtonKind;
            internal bool ButtonGrouped;
            internal ShellProofOutcome ShellProof;
            internal ShellProofPhase ShellPhase;
            internal int ShellBudgetMilliseconds, ShellMilliseconds, ShellTargetMilliseconds, ShellTaskbarMilliseconds,
                ShellCacheMilliseconds, ShellReaderMilliseconds, ShellIdentityMilliseconds;
        }
        internal sealed class DiagnosticState
        {
            readonly object sync = new object();
            readonly long queued = Stopwatch.GetTimestamp();
            long reading;
            long shellBegan, shellPhaseBegan, shellFinished;
            bool frozen;
            readonly Diagnostic value = new Diagnostic { Stage = DiagnosticStage.Queued, ReadersAtQueue = Math.Min(ReaderLimit, ActiveReaders) };
            internal void Begin()
            { lock (sync) if (!frozen) { reading = Stopwatch.GetTimestamp(); value.Stage = DiagnosticStage.Reader; value.ReadersAtStart = Math.Min(ReaderLimit, ActiveReaders); } }
            internal void Stage(DiagnosticStage stage) { lock (sync) if (!frozen) value.Stage = stage; }
            internal void Outcome(DiagnosticOutcome outcome) { lock (sync) if (!frozen) value.Outcome = outcome; }
            internal void Uia(int total, int scanned) { lock (sync) if (!frozen) { value.UiaElements = Bounded(total); value.UiaScanned = Bounded(scanned); } }
            internal void Window(int kind) { lock (sync) if (!frozen) {
                if (kind == 0) value.EnumeratedWindows = Bounded(value.EnumeratedWindows + 1);
                else if (kind == 1) value.CandidateWindows = Bounded(value.CandidateWindows + 1);
                else value.MatchedWindows = Bounded(value.MatchedWindows + 1);
            } }
            internal void Error(int error) { lock (sync) if (!frozen) { value.ErrorHResult = error; value.Outcome = DiagnosticOutcome.ReaderError; } }
            internal void Button(TaskbarMover.Click button)
            {
                lock (sync) if (!frozen) {
                    value.TaskManagerAppId = SafeTaskManagerId(button);
                    value.ButtonKind = ClassifyButtonIdentity(button == null ? null : button.AppId);
                    value.ButtonGrouped = button != null && button.Grouped;
                }
            }
            internal void ShellBegin(int budget)
            {
                lock (sync) if (!frozen) {
                    shellBegan = shellPhaseBegan = Stopwatch.GetTimestamp(); shellFinished = 0;
                    value.ShellBudgetMilliseconds = BoundedMilliseconds(budget);
                    value.ShellProof = ShellProofOutcome.Pending; value.ShellPhase = ShellProofPhase.Target;
                    value.ShellMilliseconds = value.ShellTargetMilliseconds = value.ShellTaskbarMilliseconds =
                        value.ShellCacheMilliseconds = value.ShellReaderMilliseconds = value.ShellIdentityMilliseconds = 0;
                }
            }
            internal void ShellPhase(ShellProofPhase phase)
            {
                lock (sync) if (!frozen && shellBegan != 0 && shellFinished == 0) {
                    long now = Stopwatch.GetTimestamp(); AccumulateShellPhase(value, now);
                    value.ShellPhase = phase; shellPhaseBegan = now;
                }
            }
            internal void ShellEnd(ShellProofOutcome outcome)
            {
                lock (sync) if (!frozen && shellBegan != 0 && shellFinished == 0) {
                    shellFinished = Stopwatch.GetTimestamp(); AccumulateShellPhase(value, shellFinished);
                    shellPhaseBegan = 0; value.ShellProof = outcome;
                    value.ShellMilliseconds = Milliseconds(shellBegan, shellFinished);
                }
            }
            internal void Close(DiagnosticOutcome outcome)
            {
                lock (sync) {
                    if (frozen) return;
                    long now = Stopwatch.GetTimestamp();
                    if (value.ShellProof == ShellProofOutcome.Pending &&
                        (outcome == DiagnosticOutcome.ReaderTimeout || outcome == DiagnosticOutcome.QueueTimeout))
                        value.ShellProof = ShellProofOutcome.ProbeExpired;
                    value.Outcome = outcome; CaptureTime(now); AccumulateShellPhase(value, now); shellPhaseBegan = 0; frozen = true;
                }
            }
            void CaptureTime(long now)
            {
                value.ElapsedMilliseconds = Milliseconds(queued, now);
                value.QueueMilliseconds = Milliseconds(queued, reading == 0 ? now : reading);
                value.ReaderMilliseconds = reading == 0 ? 0 : Milliseconds(reading, now);
                value.ShellMilliseconds = shellBegan == 0 ? 0 : Milliseconds(shellBegan, shellFinished == 0 ? now : shellFinished);
            }
            void AccumulateShellPhase(Diagnostic target, long now)
            {
                if (shellPhaseBegan == 0) return;
                int elapsed = Milliseconds(shellPhaseBegan, now);
                switch (value.ShellPhase) {
                    case ShellProofPhase.Target: target.ShellTargetMilliseconds = BoundedMilliseconds(target.ShellTargetMilliseconds + elapsed); break;
                    case ShellProofPhase.Shell: target.ShellTaskbarMilliseconds = BoundedMilliseconds(target.ShellTaskbarMilliseconds + elapsed); break;
                    case ShellProofPhase.Cache: target.ShellCacheMilliseconds = BoundedMilliseconds(target.ShellCacheMilliseconds + elapsed); break;
                    case ShellProofPhase.Reader: target.ShellReaderMilliseconds = BoundedMilliseconds(target.ShellReaderMilliseconds + elapsed); break;
                    case ShellProofPhase.Identity: target.ShellIdentityMilliseconds = BoundedMilliseconds(target.ShellIdentityMilliseconds + elapsed); break;
                }
            }
            internal Diagnostic Snapshot()
            {
                lock (sync) {
                    long now = Stopwatch.GetTimestamp();
                    if (!frozen) CaptureTime(now);
                    var result = new Diagnostic { Stage = value.Stage, Outcome = value.Outcome, ElapsedMilliseconds = value.ElapsedMilliseconds,
                        QueueMilliseconds = value.QueueMilliseconds, ReaderMilliseconds = value.ReaderMilliseconds, UiaElements = value.UiaElements,
                        UiaScanned = value.UiaScanned, EnumeratedWindows = value.EnumeratedWindows, CandidateWindows = value.CandidateWindows,
                        MatchedWindows = value.MatchedWindows, ReadersAtQueue = value.ReadersAtQueue, ReadersAtStart = value.ReadersAtStart,
                        ErrorHResult = value.ErrorHResult, TaskManagerAppId = value.TaskManagerAppId,
                        ButtonKind = value.ButtonKind, ButtonGrouped = value.ButtonGrouped,
                        ShellProof = value.ShellProof, ShellPhase = value.ShellPhase, ShellBudgetMilliseconds = value.ShellBudgetMilliseconds,
                        ShellMilliseconds = value.ShellMilliseconds, ShellTargetMilliseconds = value.ShellTargetMilliseconds,
                        ShellTaskbarMilliseconds = value.ShellTaskbarMilliseconds, ShellCacheMilliseconds = value.ShellCacheMilliseconds,
                        ShellReaderMilliseconds = value.ShellReaderMilliseconds, ShellIdentityMilliseconds = value.ShellIdentityMilliseconds };
                    if (!frozen) AccumulateShellPhase(result, now);
                    return result;
                }
            }
            static int Bounded(int count) { return Math.Max(0, Math.Min(65535, count)); }
            static int BoundedMilliseconds(int milliseconds) { return Math.Max(0, Math.Min(9999, milliseconds)); }
            static int Milliseconds(long before, long after) { return after <= before ? 0 : (int)Math.Min(9999, (after - before) * 1000.0 / Stopwatch.Frequency); }
        }
        [ThreadStatic] static Probe activeProbe;
        [ThreadStatic] static bool activeHoverProbe;
        internal static bool IsHoverProbe { get { return activeHoverProbe; } }
        internal static int RemainingProbeMilliseconds
        {
            get {
                if (activeProbe == null) return Int32.MaxValue;
                long remaining = activeProbe.Deadline - Stopwatch.GetTimestamp();
                return remaining <= 0 ? 0 : (int)Math.Min(Int32.MaxValue, remaining * 1000.0 / Stopwatch.Frequency);
            }
        }
        internal static void NoteStage(DiagnosticStage stage) { if (activeProbe != null) activeProbe.evidence.Stage(stage); }
        internal static void NoteUiaElements(int total, int scanned) { if (activeProbe != null) activeProbe.evidence.Uia(total, scanned); }
        internal static void NoteReaderError(int errorHResult) { if (activeProbe != null) activeProbe.evidence.Error(errorHResult); }
        internal static void NoteButton(TaskbarMover.Click button) { if (activeProbe != null) activeProbe.evidence.Button(button); }
        internal static void NoteShellProofBegin(int budget) { if (activeProbe != null) activeProbe.evidence.ShellBegin(budget); }
        internal static void NoteShellProofPhase(ShellProofPhase phase) { if (activeProbe != null) activeProbe.evidence.ShellPhase(phase); }
        internal static void NoteShellProofOutcome(ShellProofOutcome outcome) { if (activeProbe != null) activeProbe.evidence.ShellEnd(outcome); }
        static void NoteWindow(int kind) { if (activeProbe != null) activeProbe.evidence.Window(kind); }
        static string SafeTaskManagerId(TaskbarMover.Click button)
        {
            if (button == null || !String.Equals((button.AppName ?? "").Trim(), "Task Manager", StringComparison.OrdinalIgnoreCase) || String.IsNullOrEmpty(button.AppId)) return null;
            string id = button.AppId;
            if (id.Length > 128) return "<non-identifier>";
            foreach (char item in id)
                if (!(Char.IsLetterOrDigit(item) || item == '.' || item == '-' || item == '_' || item == '!' || item == '{' || item == '}')) return "<non-identifier>";
            return id;
        }
        internal static ButtonIdentityKind ClassifyButtonIdentity(string identity)
        {
            return String.IsNullOrEmpty(identity) ? ButtonIdentityKind.None :
                TaskbarNativeSelection.IsGeneratedIdentity(identity) ? ButtonIdentityKind.Generated : ButtonIdentityKind.Other;
        }
        internal sealed class Target
        {
            internal TaskbarMover.Click Button;
            internal IntPtr Taskbar, Window;
            internal uint Process;
            internal bool NeedsNativeSelection;
            internal bool ShellOnly;
            internal long ProcessStarted;
            internal TaskManagerShellIdentity.Proof ShellProof;
            internal string AppKey;
            internal DateTime Captured;
            internal Native.Rect TaskbarBounds;
            internal WindowIdentity[] Windows;
            internal Diagnostic Diagnostics;
        }
        internal sealed class WindowIdentity
        {
            internal IntPtr Window;
            internal uint Process;
            internal bool NeedsNativeSelection;
            internal bool ShellOnly;
            internal long ProcessStarted;
            internal TaskManagerShellIdentity.Proof ShellProof;
        }
        internal sealed class Probe
        {
            internal readonly DiagnosticState evidence = new DiagnosticState();
            internal Diagnostic Diagnostics { get { return evidence.Snapshot(); } }
            internal string TaskManagerAppId { get { return Diagnostics.TaskManagerAppId; } }
            internal IntPtr Taskbar;
            internal TaskbarMover.Point Point;
            internal Target Result;
            internal volatile bool Complete;
            internal IntPtr Preview, SelectedWindow;
            internal TaskbarMover.Point PreviewPoint;
            internal Target Origin;
            internal PreferredWindow Preferred;
            internal PreviewSelection Selection;
            internal bool NativeFallback, VerifyOrigin;
            internal long Deadline;
            internal HoverQueryPolicy.Ticket HoverTicket;
        }
        internal sealed class PreferredWindow
        {
            internal readonly IntPtr Window;
            internal readonly uint Process;
            internal readonly long ProcessStarted;
            internal PreferredWindow(IntPtr window, uint process, long started)
            { Window = window; Process = process; ProcessStarted = started; }
        }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        internal static PreferredWindow CapturePreferredWindow(IntPtr window)
        {
            uint process;
            if (window == IntPtr.Zero || GetAncestor(window, 2) != window ||
                Native.GetWindowThreadProcessId(window, out process) == 0 || process == 0) return null;
            long started = NativeProcessIdentity.Started(process);
            uint current;
            return started > 0 && Native.GetWindowThreadProcessId(window, out current) != 0 && current == process &&
                NativeProcessIdentity.Started(process) == started ?
                new PreferredWindow(window, process, started) : null;
        }
        sealed class ReaderWork
        {
            internal Probe Probe;
            internal long Generation;
            internal bool Hover;
        }
        internal sealed class PreviewSelection
        {
            internal Target Origin;
            internal IntPtr Preview, Window;
            internal uint Process;
            internal Native.Rect PreviewBounds, ItemBounds;
            internal DateTime Captured;
        }
        internal sealed class PreviewElement
        {
            internal string Name;
            internal Native.Rect Bounds;
            internal uint Process;
        }
        delegate bool EnumWindow(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
        readonly object gate = new object();
        readonly Queue<Probe> checks = new Queue<Probe>();
        readonly Func<IntPtr, TaskbarMover.Point, Target> reader;
        readonly Func<Target, IntPtr, TaskbarMover.Point, PreviewSelection> previewReader;
        readonly HoverQueryPolicy hoverPolicy;
        volatile Target[] snapshots = new Target[0];
        volatile PreviewSelection previewSnapshot;
        // Queue time and both preview stages share a monotonic budget below the
        // click caller's 750ms deadline. Cache TTLs and hover throttles stay separate.
        internal const int ProbeTimeoutMilliseconds = 450;
        const int ReaderLimit = 2;
        static int activeReaders;
        internal static int ActiveReaders { get { return Interlocked.CompareExchange(ref activeReaders, 0, 0); } }
        readonly Timer watchdog;
        bool disposed, watching;
        long generation;
        ReaderWork active;
        Probe hover;

        internal TaskbarTargets() : this(ReadTarget) { }
        internal TaskbarTargets(Func<IntPtr, TaskbarMover.Point, Target> reader) : this(reader, ReadPreview) { }
        internal TaskbarTargets(Func<IntPtr, TaskbarMover.Point, Target> reader,
            Func<Target, IntPtr, TaskbarMover.Point, PreviewSelection> previewReader)
            : this(reader, previewReader, new HoverQueryPolicy(Stopwatch.GetTimestamp, Stopwatch.Frequency)) { }
        internal TaskbarTargets(Func<IntPtr, TaskbarMover.Point, Target> reader,
            Func<Target, IntPtr, TaskbarMover.Point, PreviewSelection> previewReader, HoverQueryPolicy hoverPolicy)
        {
            if (reader == null || previewReader == null || hoverPolicy == null) throw new ArgumentNullException("reader");
            this.reader = reader; this.previewReader = previewReader;
            this.hoverPolicy = hoverPolicy;
            watchdog = new Timer(Watch, null, Timeout.Infinite, Timeout.Infinite);
        }

        internal void Request(IntPtr taskbar, TaskbarMover.Point point)
        { Request(taskbar, point, null); }
        internal void Request(IntPtr taskbar, TaskbarMover.Point point, PreferredWindow preferred)
        {
            lock (gate) {
                HoverQueryPolicy.Ticket ticket;
                if (disposed || !hoverPolicy.TryAdmit(new HoverQueryPolicy.Key(taskbar, point), 150, out ticket)) return;
                if (hover != null) CompleteMiss(hover, DiagnosticOutcome.HoverReplaced);
                hover = new Probe { Taskbar = taskbar, Point = point, Deadline = NewDeadline(),
                    Preferred = preferred ?? CapturePreferredWindow(GetForegroundWindow()), HoverTicket = ticket };
                StartWorker();
            }
        }

        internal Probe Verify(IntPtr taskbar, TaskbarMover.Point point)
        { return Verify(taskbar, point, CapturePreferredWindow(GetForegroundWindow())); }
        internal Probe Verify(IntPtr taskbar, TaskbarMover.Point point, PreferredWindow preferred)
        {
            return QueueCheck(new Probe { Taskbar = taskbar, Point = point, Preferred = preferred });
        }
        internal void LeaveHover()
        {
            lock (gate) {
                if (disposed) return;
                hoverPolicy.Leave();
                if (hover != null) { CompleteMiss(hover, DiagnosticOutcome.HoverReplaced); hover = null; }
                if (active != null && active.Hover) Retire(active, DiagnosticOutcome.HoverPreempted);
                StartWorker();
            }
        }
        internal void RequestPreview(Target origin, IntPtr preview, TaskbarMover.Point point)
        {
            if (origin == null) return;
            lock (gate) {
                HoverQueryPolicy.Ticket ticket;
                if (disposed || !hoverPolicy.TryAdmit(new HoverQueryPolicy.Key(preview, point, true, origin), 100, out ticket)) return;
                if (hover != null) CompleteMiss(hover, DiagnosticOutcome.HoverReplaced);
                hover = new Probe { Origin = origin, Preview = preview, PreviewPoint = point, Deadline = NewDeadline(), HoverTicket = ticket };
                StartWorker();
            }
        }
        internal Probe VerifyPreview(Target origin, IntPtr preview, TaskbarMover.Point point)
        {
            var probe = new Probe { Origin = origin, Preview = preview, PreviewPoint = point, VerifyOrigin = true, Deadline = NewDeadline() };
            PreviewSelection snapshot;
            long capturedGeneration;
            lock (gate) {
                if (disposed) { CompleteMiss(probe, DiagnosticOutcome.Disposed); return probe; }
                snapshot = previewSnapshot; capturedGeneration = generation;
            }
            Native.Rect bounds;
            bool fits = snapshot != null && Native.GetWindowRect(preview, out bounds) && PreviewFits(snapshot, origin, preview, point, bounds, DateTime.UtcNow);
            lock (gate) {
                if (disposed || Expired(probe, Stopwatch.GetTimestamp())) { CompleteMiss(probe, disposed ? DiagnosticOutcome.Disposed : DiagnosticOutcome.QueueTimeout); return probe; }
                if (fits && generation == capturedGeneration && Object.ReferenceEquals(snapshot, previewSnapshot)) {
                    probe.Result = origin; probe.Selection = snapshot; probe.SelectedWindow = snapshot.Window;
                    probe.evidence.Stage(DiagnosticStage.Complete); probe.evidence.Close(DiagnosticOutcome.Matched); probe.Complete = true;
                    return probe;
                }
            }
            return QueueCheck(probe);
        }
        Probe QueueCheck(Probe probe)
        {
            lock (gate) {
                if (disposed) { CompleteMiss(probe, DiagnosticOutcome.Disposed); return probe; }
                if (probe.Deadline == 0) probe.Deadline = NewDeadline();
                while (checks.Count >= 8) CompleteMiss(checks.Dequeue(), DiagnosticOutcome.QueueEvicted);
                checks.Enqueue(probe);
                // A click must not spend its own budget behind a speculative
                // hover. Retire that publication only; its real provider thread
                // retains the global lease until exit. Existing click/preview
                // checks stay ordered and ReserveReader still enforces two.
                if (active != null && active.Hover) Retire(active,
                    Expired(active.Probe, Stopwatch.GetTimestamp()) ? DiagnosticOutcome.ReaderTimeout : DiagnosticOutcome.HoverPreempted);
                StartWorker();
            }
            return probe;
        }

        void StartWorker()
        {
            if (disposed) return;
            ExpirePending(Stopwatch.GetTimestamp());
            if (active != null) { SetWatching(true); return; }
            if (checks.Count == 0 && hover == null) { SetWatching(false); return; }
            bool isHover = checks.Count == 0;
            if (isHover && !hoverPolicy.MayStart(hover.HoverTicket)) {
                CompleteMiss(hover, DiagnosticOutcome.HoverBackoff); hover = null; SetWatching(false); return;
            }
            if (!ReserveReader(isHover)) {
                if (isHover) { CompleteMiss(hover, DiagnosticOutcome.ReaderLimit); hover = null; SetWatching(false); return; }
                // A provider cannot be forcibly cancelled. Retry on a later request
                // once a retired reader returns, rather than creating more threads.
                ClearSnapshots(); CompletePending(DiagnosticOutcome.ReaderLimit); SetWatching(false); return;
            }
            Probe probe;
            if (checks.Count != 0) probe = checks.Dequeue();
            else { probe = hover; hover = null; }
            var work = new ReaderWork { Probe = probe, Generation = generation, Hover = isHover };
            active = work;
            SetWatching(true);
            try {
                var thread = new Thread(delegate() { Read(work); });
                thread.IsBackground = true; thread.Name = "Taskbar target reader";
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }
            catch (Exception) {
                Interlocked.Decrement(ref activeReaders);
                Retire(work, DiagnosticOutcome.ReaderStartFailed); CompletePending(DiagnosticOutcome.ReaderStartFailed); SetWatching(false);
            }
        }

        static long NewDeadline()
        { return Stopwatch.GetTimestamp() + Stopwatch.Frequency * ProbeTimeoutMilliseconds / 1000; }
        static bool Expired(Probe probe, long now) { return now >= probe.Deadline; }
        static bool ReserveReader(bool speculative)
        {
            int count;
            do {
                count = ActiveReaders;
                // A speculative provider call may occupy only the first lease.
                // The second remains available to a fresh click even when an
                // expired hover's real COM call has not returned.
                if (count >= (speculative ? 1 : ReaderLimit)) return false;
            } while (Interlocked.CompareExchange(ref activeReaders, count + 1, count) != count);
            return true;
        }
        static void CompleteMiss(Probe probe, DiagnosticOutcome outcome = DiagnosticOutcome.ReaderMiss)
        {
            if (probe.Complete) return;
            probe.Result = null; probe.Selection = null; probe.SelectedWindow = IntPtr.Zero; probe.NativeFallback = false;
            CompleteHoverPolicy(probe, outcome);
            probe.evidence.Close(outcome); probe.Complete = true;
        }
        static void CompleteHoverPolicy(Probe probe, DiagnosticOutcome outcome)
        {
            if (probe.HoverTicket == null) return;
            HoverQueryPolicy.Completion completion;
            if (outcome == DiagnosticOutcome.Matched) completion = HoverQueryPolicy.Completion.Success;
            else if (outcome == DiagnosticOutcome.ReaderMiss || outcome == DiagnosticOutcome.ReadAppButtonMiss) completion = HoverQueryPolicy.Completion.Miss;
            else if (outcome == DiagnosticOutcome.ReaderTimeout || outcome == DiagnosticOutcome.QueueTimeout ||
                outcome == DiagnosticOutcome.ReaderError || outcome == DiagnosticOutcome.ReaderStartFailed || outcome == DiagnosticOutcome.TaskbarRectangleFailed)
                completion = HoverQueryPolicy.Completion.Failure;
            else if (outcome == DiagnosticOutcome.ReaderLimit || outcome == DiagnosticOutcome.HoverBackoff) completion = HoverQueryPolicy.Completion.Deferred;
            else completion = HoverQueryPolicy.Completion.Abandoned;
            probe.HoverTicket.Owner.Complete(probe.HoverTicket, completion);
        }
        void ClearSnapshots() { snapshots = new Target[0]; previewSnapshot = null; }
        void CompletePending(DiagnosticOutcome outcome = DiagnosticOutcome.ReaderMiss)
        {
            while (checks.Count != 0) CompleteMiss(checks.Dequeue(), outcome);
            if (hover != null) { CompleteMiss(hover, outcome); hover = null; }
        }
        void ExpirePending(long now)
        {
            int count = checks.Count;
            for (int index = 0; index < count; index++) {
                var probe = checks.Dequeue();
                if (Expired(probe, now)) CompleteMiss(probe, DiagnosticOutcome.QueueTimeout);
                else checks.Enqueue(probe);
            }
            if (hover != null && Expired(hover, now)) { CompleteMiss(hover, DiagnosticOutcome.QueueTimeout); hover = null; }
        }
        void SetWatching(bool value)
        {
            if (watching == value) return;
            watching = value;
            watchdog.Change(value ? 25 : Timeout.Infinite, value ? 25 : Timeout.Infinite);
        }
        void Retire(ReaderWork work, DiagnosticOutcome outcome = DiagnosticOutcome.ReaderTimeout)
        {
            if (!Object.ReferenceEquals(active, work)) return;
            active = null; generation++; ClearSnapshots(); CompleteMiss(work.Probe, outcome);
        }
        void Watch(object state)
        {
            lock (gate) {
                if (disposed) return;
                long now = Stopwatch.GetTimestamp();
                ExpirePending(now);
                if (active != null && Expired(active.Probe, now)) Retire(active);
                StartWorker();
            }
        }
        bool MayRead(ReaderWork work)
        {
            lock (gate) return !disposed && Object.ReferenceEquals(active, work) && generation == work.Generation &&
                !work.Probe.Complete && !Expired(work.Probe, Stopwatch.GetTimestamp());
        }
        void Read(ReaderWork work)
        {
            var probe = work.Probe;
            Probe previousProbe = activeProbe; activeProbe = probe;
            bool previousHoverProbe = activeHoverProbe; activeHoverProbe = work.Hover;
            Target result = null;
            PreviewSelection selection = null;
            bool nativeFallback = false;
            try {
                if (MayRead(work)) {
                    probe.evidence.Begin();
                    if (probe.Preview != IntPtr.Zero) {
                        result = probe.Origin;
                        NoteStage(DiagnosticStage.Preview);
                        try { selection = previewReader(probe.Origin, probe.Preview, probe.PreviewPoint); }
                        catch (Exception error) { NoteReaderError(error.HResult); }
                        if (selection == null && probe.VerifyOrigin && probe.Origin != null && MayRead(work)) {
                            NoteStage(DiagnosticStage.VerifyOrigin); result = reader(probe.Origin.Taskbar, probe.Origin.Button.Position);
                            nativeFallback = SameButton(probe.Origin, result) && SameWindows(probe.Origin.Windows, result.Windows);
                        }
                    } else result = reader(probe.Taskbar, probe.Point);
                }
            }
            catch (Exception error) { NoteReaderError(error.HResult); }
            finally {
                activeProbe = previousProbe;
                activeHoverProbe = previousHoverProbe;
                try {
                    lock (gate) {
                        if (!disposed && Object.ReferenceEquals(active, work) && generation == work.Generation && !probe.Complete) {
                            if (Expired(probe, Stopwatch.GetTimestamp())) Retire(work);
                            else {
                                if (probe.Preview != IntPtr.Zero) previewSnapshot = selection;
                                else {
                                    // A miss or changed layout invalidates every older icon on this taskbar.
                                    var recent = new List<Target>();
                                    if (result != null) recent.Add(result);
                                    foreach (var item in snapshots)
                                        if (recent.Count < 16 && item.Taskbar != probe.Taskbar &&
                                            DateTime.UtcNow - item.Captured < TimeSpan.FromSeconds(2)) recent.Add(item);
                                    snapshots = recent.ToArray();
                                }
                                probe.Result = result; probe.Selection = selection;
                                probe.SelectedWindow = selection == null ? IntPtr.Zero : selection.Window;
                                probe.NativeFallback = nativeFallback;
                                DiagnosticOutcome outcome = result != null ? DiagnosticOutcome.Matched : probe.Diagnostics.Outcome;
                                if (outcome == DiagnosticOutcome.Pending) outcome = DiagnosticOutcome.ReaderMiss;
                                if (result != null) probe.evidence.Stage(DiagnosticStage.Complete);
                                DiagnosticOutcome hoverOutcome = outcome;
                                if (probe.Preview != IntPtr.Zero && selection == null)
                                    hoverOutcome = probe.Diagnostics.Outcome == DiagnosticOutcome.ReaderError ? DiagnosticOutcome.ReaderError : DiagnosticOutcome.ReaderMiss;
                                CompleteHoverPolicy(probe, hoverOutcome);
                                probe.evidence.Close(outcome);
                                if (result != null && result.Diagnostics == null) result.Diagnostics = probe.Diagnostics;
                                probe.Complete = true; active = null;
                            }
                        }
                    }
                }
                finally {
                    Interlocked.Decrement(ref activeReaders);
                    lock (gate) StartWorker();
                }
            }
        }

        internal static WindowIdentity[] ResolveWindows(TaskbarMover.Click button)
        {
            var windows = new List<WindowIdentity>();
            EnumWindows(delegate(IntPtr window, IntPtr data) {
                NoteWindow(0);
                if (!WindowShelfCatalog.IsTaskbarCandidate(window)) return true;
                NoteWindow(1); NoteStage(DiagnosticStage.WindowAppIdentity);
                bool matched = TaskbarMover.MatchesApp(button, window);
                bool needsNativeSelection = false; long started = 0;
                TaskManagerShellIdentity.Proof shellProof = null;
                // A verified official Task Manager executable can identify the
                // button's possible window, but cannot authorize moving it.
                // The click caller must also observe fresh native selection, or
                // separately validate the optional exact shell proof for a direct move.
                if (!matched && TaskbarNativeSelection.TryIdentifyTaskManager(button, window, out started)) {
                    matched = true; needsNativeSelection = true;
                    // Optional shell resolution strengthens only direct admission.
                    // A miss keeps the original native-selection fallback intact.
                    if (button.Taskbar != IntPtr.Zero)
                        TaskManagerShellIdentity.TryProve(button, window, started, button.Taskbar, out shellProof);
                }
                NoteStage(DiagnosticStage.ResolveWindows);
                if (!matched) return true;
                uint process;
                if (Native.GetWindowThreadProcessId(window, out process) == 0 || process == 0 ||
                    (needsNativeSelection && (started <= 0 || NativeProcessIdentity.Started(process) != started))) return true;
                NoteWindow(2);
                windows.Add(new WindowIdentity { Window = window, Process = process,
                    NeedsNativeSelection = needsNativeSelection,
                    ProcessStarted = needsNativeSelection ? started : 0, ShellProof = shellProof });
                return true;
            }, IntPtr.Zero);
            // Ordinary matching and the existing Task Manager fallback run first.
            // Only a missing identity may try the one captured official MMC window.
            PreferredWindow preferred = activeProbe == null ? null : activeProbe.Preferred;
            TaskManagerShellIdentity.Proof managementProof;
            if (windows.Count == 0 && button != null && button.Taskbar != IntPtr.Zero && preferred != null &&
                TaskManagerShellIdentity.TryProveManagementConsole(button, preferred, button.Taskbar, out managementProof) &&
                WindowShelfCatalog.IsTaskbarCandidate(managementProof.Window) && TaskManagerShellIdentity.ProofIsCurrent(managementProof)) {
                windows.Add(new WindowIdentity { Window = managementProof.Window, Process = managementProof.Process,
                    ProcessStarted = managementProof.ProcessStarted, NeedsNativeSelection = true, ShellOnly = true, ShellProof = managementProof });
                NoteWindow(2);
            }
            return windows.ToArray();
        }

        static Target ReadTarget(IntPtr taskbar, TaskbarMover.Point point)
        {
            var button = new TaskbarMover.Click { Position = point, Taskbar = taskbar };
            Native.Rect bounds;
            NoteStage(DiagnosticStage.ReadAppButton);
            if (!TaskbarMover.ReadAppButton(button, taskbar)) {
                if (activeProbe != null && activeProbe.Diagnostics.Outcome != DiagnosticOutcome.ReaderError)
                    activeProbe.evidence.Outcome(DiagnosticOutcome.ReadAppButtonMiss);
                return null;
            }
            NoteButton(button); NoteStage(DiagnosticStage.TaskbarRectangle);
            if (!Native.GetWindowRect(taskbar, out bounds)) {
                if (activeProbe != null) activeProbe.evidence.Outcome(DiagnosticOutcome.TaskbarRectangleFailed);
                return null;
            }
            var target = new Target { Button = button, Taskbar = taskbar, TaskbarBounds = bounds };
            NoteStage(DiagnosticStage.ResolveWindows);
            var windows = ResolveWindows(button);
            target.Windows = windows;
            if (!button.Grouped && windows.Length == 1) {
                NoteStage(DiagnosticStage.AppCatalogIdentity);
                var app = AppCatalog.ForWindow(windows[0].Window);
                if (app != null) {
                    target.Window = windows[0].Window; target.Process = windows[0].Process; target.AppKey = app.Key;
                    target.NeedsNativeSelection = windows[0].NeedsNativeSelection; target.ProcessStarted = windows[0].ProcessStarted;
                    target.ShellOnly = windows[0].ShellOnly;
                    target.ShellProof = windows[0].ShellProof;
                }
            }
            target.Captured = DateTime.UtcNow;
            return target;
        }

        static PreviewSelection ReadPreview(Target origin, IntPtr preview, TaskbarMover.Point point)
        {
            if (origin == null) return null;
            Native.Rect bounds;
            if (!Native.IsWindowVisible(preview) || !Native.GetWindowRect(preview, out bounds) || !TaskbarMover.Contains(bounds, point)) return null;
            var position = new System.Windows.Point(point.X, point.Y);
            var previewRoot = AutomationElement.FromHandle(preview);
            uint shellPid; Native.GetWindowThreadProcessId(preview, out shellPid);
            // Some shells expose named descendants even when point lookup returns
            // only the host. Others expose neither and need native selection events.
            var elements = previewRoot.FindAll(TreeScope.Subtree, Condition.TrueCondition);
            var candidates = new List<PreviewElement>();
            for (int index = 0; index < elements.Count; index++) {
                var current = elements[index].Current;
                var item = current.BoundingRectangle;
                if (!item.Contains(position)) continue;
                candidates.Add(new PreviewElement { Name = current.Name, Process = (uint)current.ProcessId,
                    Bounds = new Native.Rect { Left = (int)item.Left, Top = (int)item.Top, Right = (int)item.Right, Bottom = (int)item.Bottom } });
            }
            var hit = SelectPreviewElement(candidates, point, shellPid,
                name => ResolvePreviewWindow(origin.Button, new List<string> { name }));
            if (hit == null) return null;
            hit.Origin = origin; hit.Preview = preview; hit.PreviewBounds = bounds; hit.Captured = DateTime.UtcNow;
            Native.GetWindowThreadProcessId(hit.Window, out hit.Process);
            return hit;
        }

        internal static PreviewSelection SelectPreviewElement(IEnumerable<PreviewElement> elements, TaskbarMover.Point point,
            uint shellProcess, Func<string, IntPtr> resolveWindow)
        {
            PreviewSelection hit = null;
            foreach (var element in elements) {
                if (element.Process != shellProcess || String.IsNullOrWhiteSpace(element.Name) || !TaskbarMover.Contains(element.Bounds, point)) continue;
                IntPtr window = resolveWindow(element.Name);
                if (window == IntPtr.Zero) continue;
                if (hit != null && hit.Window != window) return null;
                if (hit == null || RectArea(element.Bounds) < RectArea(hit.ItemBounds))
                    hit = new PreviewSelection { Window = window, ItemBounds = element.Bounds };
            }
            return hit;
        }

        static long RectArea(Native.Rect rect)
        {
            return (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
        }

        internal static bool PreviewFits(PreviewSelection selection, Target origin, IntPtr preview,
            TaskbarMover.Point point, Native.Rect bounds, DateTime now)
        {
            return selection != null && selection.Window != IntPtr.Zero && selection.Preview == preview &&
                now >= selection.Captured && now - selection.Captured <= TimeSpan.FromMilliseconds(400) &&
                SameButton(selection.Origin, origin) && AreaPolicy.Equal(selection.PreviewBounds, bounds) &&
                TaskbarMover.Contains(selection.ItemBounds, point);
        }

        static IntPtr ResolvePreviewWindow(TaskbarMover.Click button, List<string> names)
        {
            IntPtr selected = IntPtr.Zero;
            int matches = 0;
            EnumWindows(delegate(IntPtr window, IntPtr data) {
                if (!Native.IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero || !TaskbarMover.MatchesApp(button, window)) return true;
                var title = new StringBuilder(1024);
                if (GetWindowText(window, title, title.Capacity) == 0 || !names.Contains(title.ToString())) return true;
                selected = window; matches++;
                return matches < 2;
            }, IntPtr.Zero);
            return matches == 1 ? selected : IntPtr.Zero;
        }

        internal static bool SameButton(Target expected, Target actual)
        {
            return expected != null && actual != null && expected.Button != null && actual.Button != null &&
                !String.IsNullOrEmpty(expected.Button.AppId) &&
                String.Equals(expected.Button.AppId, actual.Button.AppId, StringComparison.OrdinalIgnoreCase) &&
                expected.Taskbar == actual.Taskbar && expected.Window == actual.Window && expected.Process == actual.Process &&
                expected.NeedsNativeSelection == actual.NeedsNativeSelection && expected.ProcessStarted == actual.ProcessStarted &&
                expected.ShellOnly == actual.ShellOnly &&
                expected.Button.Grouped == actual.Button.Grouped &&
                AreaPolicy.Equal(expected.Button.ButtonBounds, actual.Button.ButtonBounds) &&
                AreaPolicy.Equal(expected.TaskbarBounds, actual.TaskbarBounds);
        }
        internal static string SameButtonDifference(Target expected, Target actual)
        {
            if (expected == null) return "origin-missing";
            if (actual == null) return "result-missing";
            if (expected.Button == null || actual.Button == null) return "button-missing";
            var differences = new List<string>();
            if (String.IsNullOrEmpty(expected.Button.AppId)) differences.Add("origin-id-empty");
            else if (!String.Equals(expected.Button.AppId, actual.Button.AppId, StringComparison.OrdinalIgnoreCase)) differences.Add("app-id");
            if (expected.Taskbar != actual.Taskbar) differences.Add("taskbar-handle");
            if (expected.Window != actual.Window) differences.Add("window-handle");
            if (expected.Process != actual.Process) differences.Add("process-id");
            if (expected.NeedsNativeSelection != actual.NeedsNativeSelection) differences.Add("selection-provenance");
            if (expected.ShellOnly != actual.ShellOnly) differences.Add("shell-provenance");
            if (expected.ProcessStarted != actual.ProcessStarted) differences.Add("process-start");
            if (expected.Button.Grouped != actual.Button.Grouped) differences.Add("grouping");
            if (!AreaPolicy.Equal(expected.Button.ButtonBounds, actual.Button.ButtonBounds)) differences.Add("button-bounds");
            if (!AreaPolicy.Equal(expected.TaskbarBounds, actual.TaskbarBounds)) differences.Add("taskbar-bounds");
            return differences.Count == 0 ? "matched" : String.Join(",", differences.ToArray());
        }
        internal static string FailureDescription(Probe probe)
        {
            if (probe == null) return "probe-missing";
            Diagnostic evidence = probe.Diagnostics;
            string text = evidence.Stage + "/" + evidence.Outcome + " " + evidence.ElapsedMilliseconds + "ms" +
                " q=" + evidence.QueueMilliseconds + " r=" + evidence.ReaderMilliseconds +
                " uia=" + evidence.UiaScanned + "/" + evidence.UiaElements + " win=" + evidence.CandidateWindows + "/" + evidence.EnumeratedWindows +
                " match=" + evidence.MatchedWindows + " readers=" + evidence.ReadersAtStart + "/" + ReaderLimit;
            if (evidence.ErrorHResult != 0) text += " hr=0x" + evidence.ErrorHResult.ToString("X8");
            if (!String.IsNullOrEmpty(evidence.TaskManagerAppId)) text += " TaskManagerId=" + evidence.TaskManagerAppId;
            return text.Length <= 250 ? text : text.Substring(0, 250);
        }
        internal static string ShellProofDescription(Probe probe)
        { return ShellProofDescription(probe == null ? null : probe.Diagnostics); }
        internal static string ShellProofDescription(Diagnostic evidence)
        {
            if (evidence == null) return "shell=NotApplicable/None";
            string outcome = Enum.IsDefined(typeof(ShellProofOutcome), evidence.ShellProof) ? evidence.ShellProof.ToString() : "Unknown";
            string phase = Enum.IsDefined(typeof(ShellProofPhase), evidence.ShellPhase) ? evidence.ShellPhase.ToString() : "Unknown";
            string kind = Enum.IsDefined(typeof(ButtonIdentityKind), evidence.ButtonKind) ? evidence.ButtonKind.ToString() : "Unknown";
            return "button=" + kind + " grouped=" + (evidence.ButtonGrouped ? "1" : "0") + " shell=" + outcome + "/" + phase + " b=" + DiagnosticMilliseconds(evidence.ShellBudgetMilliseconds) +
                " t=" + DiagnosticMilliseconds(evidence.ShellMilliseconds) + " target=" + DiagnosticMilliseconds(evidence.ShellTargetMilliseconds) +
                " bar=" + DiagnosticMilliseconds(evidence.ShellTaskbarMilliseconds) + " cache=" + DiagnosticMilliseconds(evidence.ShellCacheMilliseconds) +
                " reader=" + DiagnosticMilliseconds(evidence.ShellReaderMilliseconds) + " id=" + DiagnosticMilliseconds(evidence.ShellIdentityMilliseconds) + "ms";
        }
        static int DiagnosticMilliseconds(int value) { return Math.Max(0, Math.Min(9999, value)); }

        internal static bool SameWindows(WindowIdentity[] expected, WindowIdentity[] actual)
        {
            if (expected == null || actual == null || expected.Length == 0 || expected.Length != actual.Length) return false;
            var seen = new HashSet<IntPtr>();
            foreach (var item in expected) {
                if (!seen.Add(item.Window)) return false;
                int matches = 0;
                foreach (var current in actual)
                    if (item.Window == current.Window && item.Process == current.Process && item.Window != IntPtr.Zero && item.Process != 0 &&
                        item.NeedsNativeSelection == current.NeedsNativeSelection && item.ProcessStarted == current.ProcessStarted &&
                        item.ShellOnly == current.ShellOnly) matches++;
                if (matches != 1) return false;
            }
            return true;
        }

        internal static bool NativePreviewMatches(Probe probe, IntPtr selected)
        {
            if (probe == null || !probe.Complete || !probe.NativeFallback || !SameButton(probe.Origin, probe.Result) ||
                !SameWindows(probe.Origin.Windows, probe.Result.Windows)) return false;
            bool found = false;
            foreach (var item in probe.Origin.Windows) {
                uint process;
                // Closing a thumbnail can focus its neighbor. Never treat that as selection.
                if (!Native.IsWindowVisible(item.Window) || Native.GetWindowThreadProcessId(item.Window, out process) == 0 || process != item.Process) return false;
                if (item.Window == selected) found = true;
            }
            return found;
        }

        internal Target Find(IntPtr taskbar, TaskbarMover.Point point)
        {
            Target[] captured;
            long capturedGeneration;
            lock (gate) {
                if (disposed) return null;
                captured = snapshots; capturedGeneration = generation;
            }
            foreach (var result in captured) {
                Native.Rect bounds;
                if (result.Taskbar != taskbar || DateTime.UtcNow - result.Captured > TimeSpan.FromMilliseconds(600) ||
                    !Native.GetWindowRect(taskbar, out bounds) || !AreaPolicy.Equal(bounds, result.TaskbarBounds)) continue;
                if (TaskbarMover.Contains(result.Button.ButtonBounds, point)) {
                    lock (gate) return !disposed && generation == capturedGeneration && Object.ReferenceEquals(captured, snapshots) ? result : null;
                }
            }
            return null;
        }
        public void Dispose()
        {
            lock (gate) {
                if (disposed) return;
                disposed = true; generation++; ClearSnapshots();
                hoverPolicy.Dispose();
                if (active != null) { CompleteMiss(active.Probe, DiagnosticOutcome.Disposed); active = null; }
                CompletePending(DiagnosticOutcome.Disposed); watchdog.Dispose();
            }
        }
    }
}
