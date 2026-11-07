using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class SnapArea
    {
        internal AreaRecord Record;
        internal Bar Bar;
    }

    internal static class SnapAreaPolicy
    {
        internal static bool Expand(Native.Rect visible, AreaRecord area, out Native.Rect result)
        {
            result = visible;
            if (area == null || !AreaPolicy.Contains(area.Monitor, area.Applied) ||
                !AreaPolicy.Contains(area.Applied, area.Original) || AreaPolicy.Equal(area.Original, area.Applied) ||
                visible.Right <= visible.Left || visible.Bottom <= visible.Top) return false;
            var old = area.Original;
            // Snap Assist can retain the pre-hide reservation. Extend only an edge
            // touching that boundary, without rescaling the user's snap split.
            if ((long)visible.Left < old.Left - 2L || (long)visible.Top < old.Top - 2L ||
                (long)visible.Right > old.Right + 2L || (long)visible.Bottom > old.Bottom + 2L) return false;
            if (area.Applied.Left < old.Left && Near(visible.Left, old.Left)) result.Left = area.Applied.Left;
            if (area.Applied.Top < old.Top && Near(visible.Top, old.Top)) result.Top = area.Applied.Top;
            if (area.Applied.Right > old.Right && Near(visible.Right, old.Right)) result.Right = area.Applied.Right;
            if (area.Applied.Bottom > old.Bottom && Near(visible.Bottom, old.Bottom)) result.Bottom = area.Applied.Bottom;
            return !AreaPolicy.Equal(result, visible);
        }
        static bool Near(int a, int b) { return Math.Abs((long)a - b) <= 2; }
        internal static bool ExpandFrame(Native.Rect outer, Native.Rect visible, AreaRecord area, out Native.Rect result)
        {
            result = outer;
            if (area == null || !TaskbarMover.ValidVisibleFrame(outer, visible)) return false;
            var contact = visible;
            // Earlier builds could align the invisible outer border to the old edge.
            // Accept that exact contact too, but only on the edge we actually reclaimed.
            if (area.Applied.Bottom > area.Original.Bottom && outer.Bottom == area.Original.Bottom) contact.Bottom = outer.Bottom;
            if (area.Applied.Top < area.Original.Top && outer.Top == area.Original.Top) contact.Top = outer.Top;
            if (area.Applied.Right > area.Original.Right && outer.Right == area.Original.Right) contact.Right = outer.Right;
            if (area.Applied.Left < area.Original.Left && outer.Left == area.Original.Left) contact.Left = outer.Left;
            Native.Rect expanded;
            if (!Expand(contact, area, out expanded)) return false;
            result = new Native.Rect {
                Left = outer.Left + expanded.Left - visible.Left, Top = outer.Top + expanded.Top - visible.Top,
                Right = outer.Right + expanded.Right - visible.Right, Bottom = outer.Bottom + expanded.Bottom - visible.Bottom
            };
            return true;
        }
    }

    internal sealed partial class SnapWorkAreas : IDisposable
    {
        // Private beta14 release containment. Keep work-area restoration and
        // explicit window commands, but issue no automatic per-window geometry
        // requests (maximized or arranged). This is not a user setting: wake,
        // rediscovery, process filters and physical arrows cannot enable it.
        internal static bool AutomaticGeometryCorrectionEnabled { get { return false; } }
        delegate void WinEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time);
        delegate bool EnumWindow(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr data);
        [DllImport("user32.dll")] static extern bool IsWindowArranged(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, uint attr, out int value, int size);
        sealed class Pending
        {
            internal Native.Rect Bounds, Corrected;
            internal SnapCorrectionTarget Target;
            internal SnapCorrectionArea Area;
            internal bool Maximized;
            internal long Since, WakeEpoch;
            internal SnapIntentReservation Intent;
            internal SnapLayoutReceiptLedger.IntentAnchor Anchor;
            internal SnapIntentProof OldProof;
        }
        readonly Dictionary<IntPtr, Pending> pending = new Dictionary<IntPtr, Pending>();
        readonly SnapCorrectionPolicy correctionPolicy = new SnapCorrectionPolicy();
        readonly SnapCorrectionDiagnostics diagnostics = new SnapCorrectionDiagnostics();
        SnapCorrectionTarget lastDiagnosticTarget;
        readonly Timer timer = new Timer { Interval = 60 };
        readonly WinEvent callback;
        readonly IntPtr hook;
        readonly uint onlyProcess;
        List<SnapArea> areas = new List<SnapArea>();
        readonly SnapLayoutNative layoutNative = new SnapLayoutNative();
        readonly SnapLayoutReceiptLedger layouts;
        bool disposed, quiesced, exitQuiesced;
        internal SnapWorkAreas() : this(0) { }
        internal SnapWorkAreas(uint process)
        {
            onlyProcess = process;
            layouts = new SnapLayoutReceiptLedger(layoutNative);
            callback = Changed;
            hook = AutomaticGeometryCorrectionEnabled ?
                SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, callback, process, 0, 0) : IntPtr.Zero;
            timer.Tick += delegate { ProcessPending(); };
        }
        internal void BeginWakeEpoch(long sequence)
        {
            if (disposed || quiesced || !correctionPolicy.BeginWakeEpoch(sequence)) return;
            CancelInputIntent();
            // Before-wake pending geometry is not authority for the new epoch.
            // Controller calls this only for the verified display-power path.
            pending.Clear(); timer.Stop();
        }
        internal void Update(IEnumerable<SnapArea> active)
        {
            if (!AutomaticGeometryCorrectionEnabled || disposed || quiesced) return;
            layouts.ObserveForward();
            if (disposed || quiesced) return;
            areas = active.ToList();
            if (keyboardIntent != null) RefreshInputWitness(TaskbarMover.ForegroundWindow());
            // An empty list can be a transient work-area reset during wake. It
            // must not erase correction history or replenish the mutation budget.
            if (areas.Count == 0) { pending.Clear(); timer.Stop(); return; }
            // Also catches snaps completed before startup or work-area expansion.
            EnumWindows(delegate(IntPtr window, IntPtr data) { Queue(window); return true; }, IntPtr.Zero);
        }
        void ObserveLastCorrection()
        {
            long attempt = diagnostics.AttemptCount;
            SnapCorrectionTarget expected = lastDiagnosticTarget, current;
            Native.Rect observed = new Native.Rect();
            bool sameIdentity = !disposed && diagnostics.HasAttempt && CaptureTarget(expected.Window, out current) && current.Equals(expected);
            bool readable = sameIdentity && Native.GetWindowRect(expected.Window, out observed);
            bool maximized = readable && IsZoomed(expected.Window);
            if (readable) sameIdentity = CaptureTarget(expected.Window, out current) && current.Equals(expected);
            readable = readable && sameIdentity;
            // A nested request can replace the last target while native reads
            // are in progress. Never attribute the older sample to that request.
            if (attempt != diagnostics.AttemptCount || !expected.Equals(lastDiagnosticTarget)) return;
            diagnostics.Observe(sameIdentity, readable, observed, maximized);
        }
        internal Dictionary<string, long> DiagnosticSnapshot()
        {
            // Only the last actual target is read, after verifying its limited
            // lifetime metadata again. Never export that identity or contents.
            ObserveLastCorrection();
            return diagnostics.Snapshot(correctionPolicy.WakeEpoch, correctionPolicy.AttemptCount, correctionPolicy.AreaCount);
        }
        void Changed(IntPtr ignored, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            if (AutomaticGeometryCorrectionEnabled && !disposed && !quiesced && objectId == 0 && childId == 0 && window != IntPtr.Zero) {
                if (keyboardIntent != null && window == TaskbarMover.ForegroundWindow()) {
                    SnapCorrectionTarget target;
                    if (CaptureTarget(window, out target)) layouts.ObserveForward(target);
                    RefreshInputWitness(window);
                }
                Queue(window);
            }
        }
        void Queue(IntPtr window)
        {
            if (!AutomaticGeometryCorrectionEnabled || disposed || quiesced) return;
            long observedEpoch = correctionPolicy.WakeEpoch;
            Native.Rect outer, corrected;
            SnapCorrectionArea area;
            SnapCorrectionTarget target;
            bool maximized;
            if (!TryCorrection(window, out outer, out corrected, out area, out maximized) ||
                !CaptureTarget(window, out target) || disposed || quiesced || observedEpoch != correctionPolicy.WakeEpoch ||
                !correctionPolicy.CanAttempt(target, area)) return;
            Pending state;
            if (!pending.TryGetValue(window, out state) || state.WakeEpoch != observedEpoch ||
                !state.Target.Equals(target) || !state.Area.Equals(area) || state.Maximized != maximized ||
                !AreaPolicy.Equal(state.Bounds, outer) || !AreaPolicy.Equal(state.Corrected, corrected)) {
                if (state == null && pending.Count >= SnapCorrectionPolicy.MaximumAttempts) return;
                pending[window] = new Pending { Bounds = outer, Corrected = corrected, Target = target, Area = area,
                    Since = Stopwatch.GetTimestamp(), WakeEpoch = observedEpoch, Maximized = maximized };
            }
            if (disposed || quiesced || observedEpoch != correctionPolicy.WakeEpoch) { pending.Remove(window); return; }
            timer.Start();
        }
        bool IsCurrent(IntPtr window, Pending state)
        {
            Pending current;
            return AutomaticGeometryCorrectionEnabled && !disposed && !quiesced && state.WakeEpoch == correctionPolicy.WakeEpoch && pending.TryGetValue(window, out current) &&
                Object.ReferenceEquals(current, state);
        }
        static bool CaptureTarget(IntPtr window, out SnapCorrectionTarget result)
        {
            result = new SnapCorrectionTarget();
            uint process, current;
            uint thread = Native.GetWindowThreadProcessId(window, out process);
            if (window == IntPtr.Zero || thread == 0 || process == 0) return false;
            // Reuse the existing limited-information creation-time reader. Its
            // SafeHandle closes each metadata query; no process memory is read.
            long started = NativeProcessIdentity.Started(process);
            if (started <= 0 || Native.GetWindowThreadProcessId(window, out current) != thread || current != process ||
                NativeProcessIdentity.Started(process) != started) return false;
            result = new SnapCorrectionTarget { Window = window, Process = process, Thread = thread, ProcessStarted = started };
            return true;
        }
        internal bool TryCorrection(IntPtr window, out Native.Rect outer, out Native.Rect corrected)
        {
            SnapCorrectionArea area;
            bool maximized;
            return TryCorrection(window, out outer, out corrected, out area, out maximized);
        }
        bool ModeStillMatches(IntPtr window, bool maximized)
        {
            if (!Native.IsWindow(window) || !Native.IsWindowVisible(window) || IsIconic(window) || IsZoomed(window) != maximized) return false;
            try { return IsWindowArranged(window) != maximized; }
            catch (EntryPointNotFoundException) { return false; }
        }
        bool TryCorrection(IntPtr window, out Native.Rect outer, out Native.Rect corrected, out SnapCorrectionArea fingerprint, out bool maximized)
        {
            outer = corrected = new Native.Rect();
            fingerprint = null;
            maximized = false;
            if (!AutomaticGeometryCorrectionEnabled) return false;
            uint process;
            if (onlyProcess != 0 && (Native.GetWindowThreadProcessId(window, out process) == 0 || process != onlyProcess)) return false;
            if (areas.Count == 0 || !Native.IsWindow(window) || !Native.IsWindowVisible(window) ||
                IsIconic(window) || GetWindow(window, 4) != IntPtr.Zero) return false;
            var className = new System.Text.StringBuilder(256);
            if (Native.GetClassName(window, className, className.Capacity) <= 0 ||
                !SnapCorrectionPolicy.IsApplicationRoot(window, GetAncestor(window, 2), className.ToString())) return false;
            maximized = IsZoomed(window);
            bool arranged;
            try { arranged = IsWindowArranged(window); }
            catch (EntryPointNotFoundException) { return false; }
            if (arranged == maximized) return false;
            int cloaked;
            if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) != 0 || cloaked != 0) return false;
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(window, 0), ref info) || (info.Flags & 1) != 0) return false;
            SnapArea area = areas.FirstOrDefault(a => a.Bar.Device == info.Device);
            if (area == null || !AreaPolicy.Equal(area.Record.Monitor, info.Monitor) ||
                !AreaPolicy.Equal(area.Record.Applied, info.Work) || !Native.IsSecondaryTaskbar(area.Bar) ||
                Native.IsWindowVisible(new IntPtr(area.Bar.Handle)) ||
                Native.GetProp(new IntPtr(area.Bar.Handle), Native.Marker) != new IntPtr(1)) return false;
            if (!SnapCorrectionArea.TryCapture(area.Record, out fingerprint)) return false;
            Native.Rect visible;
            if (!Native.GetWindowRect(window, out outer) ||
                Native.DwmGetWindowAttribute(window, 9, out visible, Marshal.SizeOf(typeof(Native.Rect))) != 0 ||
                !SnapCorrectionGeometry.TryExpand(maximized, arranged, outer, visible, area.Record, out corrected)) return false;
            return ModeStillMatches(window, maximized);
        }
        void ProcessPending()
        {
            if (!AutomaticGeometryCorrectionEnabled || disposed || quiesced) { pending.Clear(); timer.Stop(); return; }
            long now = Stopwatch.GetTimestamp();
            foreach (var item in pending.ToArray()) {
                if (!IsCurrent(item.Key, item.Value)) continue;
                // Never fight an active snap drag or shared-border resize.
                if ((GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0) {
                    item.Value.Since = now; continue;
                }
                Native.Rect outer, corrected;
                SnapCorrectionArea area;
                SnapCorrectionTarget target;
                bool maximized;
                if (!TryCorrection(item.Key, out outer, out corrected, out area, out maximized) || !CaptureTarget(item.Key, out target) ||
                    !target.Equals(item.Value.Target) || !area.Equals(item.Value.Area) || maximized != item.Value.Maximized ||
                    !(item.Value.Intent == null ? correctionPolicy.CanAttempt(target, area) :
                        correctionPolicy.CanPrepareIntent(target, area, item.Value.WakeEpoch, item.Value.Intent, item.Value.OldProof, now))) {
                    if (IsCurrent(item.Key, item.Value)) pending.Remove(item.Key);
                    continue;
                }
                // Native reads can deliver a nested event. A replacement entry,
                // cleared list or newer wake invalidates this snapshot as well.
                if (!IsCurrent(item.Key, item.Value)) continue;
                if (!AreaPolicy.Equal(outer, item.Value.Bounds) || !AreaPolicy.Equal(corrected, item.Value.Corrected)) {
                    item.Value.Bounds = outer; item.Value.Corrected = corrected; item.Value.Since = now; continue;
                }
                if ((double)(now - item.Value.Since) / Stopwatch.Frequency < 0.12) continue;
                if (!ModeStillMatches(item.Key, maximized)) { if (IsCurrent(item.Key, item.Value)) pending.Remove(item.Key); continue; }
                Native.Rect visible;
                if (!Native.GetWindowRect(item.Key, out outer) ||
                    Native.DwmGetWindowAttribute(item.Key, 9, out visible, Marshal.SizeOf(typeof(Native.Rect))) != 0) continue;
                var candidate = new SnapLayoutCandidate { Target = target, Area = area, Before = outer, BeforeVisible = visible,
                    Desired = corrected, DesiredVisible = new Native.Rect {
                        Left = visible.Left + corrected.Left - outer.Left, Top = visible.Top + corrected.Top - outer.Top,
                        Right = visible.Right + corrected.Right - outer.Right, Bottom = visible.Bottom + corrected.Bottom - outer.Bottom }, Maximized = maximized };
                SnapLayoutReceiptLedger.Ticket receipt;
                if (!IsCurrent(item.Key, item.Value)) continue;
                if (item.Value.Intent != null) { ProcessIntentCandidate(item.Key, item.Value, candidate); continue; }
                if (!TryPrepareOwnedLayout(correctionPolicy, layouts, candidate, layoutNative.Track, out receipt)) {
                    if (IsCurrent(item.Key, item.Value)) pending.Remove(item.Key);
                    continue;
                }
                // SetProp and every geometry/identity read may deliver reentrant
                // callbacks. The reservation is not authority to use stale work.
                Native.Rect freshOuter, freshCorrected; SnapCorrectionArea freshArea; SnapCorrectionTarget freshTarget; bool freshMax;
                if (!IsCurrent(item.Key, item.Value) ||
                    !TryCorrection(item.Key, out freshOuter, out freshCorrected, out freshArea, out freshMax) ||
                    !CaptureTarget(item.Key, out freshTarget) || !freshTarget.Equals(target) || !area.Equals(freshArea) || freshMax != maximized ||
                    !AreaPolicy.Equal(outer, freshOuter) || !AreaPolicy.Equal(corrected, freshCorrected) ||
                    !IsCurrent(item.Key, item.Value) || !correctionPolicy.TryConsume(target, area, item.Value.WakeEpoch)) {
                    layouts.CancelUnissued(receipt); continue;
                }
                if (!layouts.MarkInvoked(receipt)) { layouts.CancelUnissued(receipt); continue; }
                if (!IsCurrent(item.Key, item.Value)) { layouts.RecordReturned(receipt, false); continue; }
                pending.Remove(item.Key);
                lastDiagnosticTarget = target;
                diagnostics.RecordAttempt(item.Value.WakeEpoch, outer, corrected, maximized);
                try {
                    bool accepted = SetWindowPos(item.Key, IntPtr.Zero, corrected.Left, corrected.Top,
                        corrected.Right - corrected.Left, corrected.Bottom - corrected.Top, 0x0014 | 0x0200 | 0x4000);
                    layouts.RecordReturned(receipt, accepted);
                    if (accepted) diagnostics.RecordAccepted();
                } catch (Exception) { layouts.RecordUnknown(receipt); }
                ObserveLastCorrection();
            }
            if (pending.Count == 0) timer.Stop();
        }
        internal static bool TryPrepareOwnedLayout(SnapCorrectionPolicy policy, SnapLayoutReceiptLedger ledger,
            SnapLayoutCandidate candidate, Func<SnapLayoutCandidate, bool, bool> track, out SnapLayoutReceiptLedger.Ticket receipt)
        {
            receipt = null;
            if (policy == null || ledger == null || candidate == null || track == null || !policy.CanAttempt(candidate.Target, candidate.Area)) return false;
            // Keep ordinary same-candidate reuse on its established path. If
            // its old marker is absent, the separately guarded ownership-yield
            // path below may retire only a known completed old invocation.
            if (ledger.HasCurrentCandidate(candidate) && track(candidate, false) && ledger.TryPrepareForward(candidate, out receipt)) return true;
            if (!ledger.TryRetireChanged(candidate, policy.WakeEpoch)) return false;
            return track(candidate, ledger.CanReplaceCandidate(candidate.Target)) && ledger.TryPrepareForward(candidate, out receipt);
        }
        // Prototype-only explicit intent admission. Old proof is used only to
        // reserve preparation; the final budget call requires NEW ticket lineage.
        internal static bool TryPrepareOwnedLayout(SnapCorrectionPolicy policy, SnapLayoutReceiptLedger ledger,
            SnapLayoutCandidate candidate, Func<SnapLayoutCandidate, bool, bool> track, SnapIntentReservation reservation,
            SnapIntentProof oldCandidate, Func<long> now, out SnapLayoutReceiptLedger.Ticket receipt)
        {
            receipt = null;
            if (policy == null || ledger == null || track == null || reservation == null || now == null) return false;
            bool success = false;
            try {
                // Input authorizes this exact normal-to-arranged boundary only.
                // Reject substitution before even tracking or reading a marker.
                if (candidate == null || !candidate.Valid || candidate.Maximized || oldCandidate == null || !oldCandidate.IsArranged ||
                    !oldCandidate.Target.Equals(candidate.Target) || !oldCandidate.Area.Equals(candidate.Area) ||
                    !AreaPolicy.Equal(oldCandidate.Outer, candidate.Before) || !AreaPolicy.Equal(oldCandidate.Visible, candidate.BeforeVisible) ||
                    !AreaPolicy.Equal(oldCandidate.Desired, candidate.Desired) || !AreaPolicy.Equal(oldCandidate.DesiredVisible, candidate.DesiredVisible)) return false;
                if (!policy.CanPrepareIntent(candidate.Target, candidate.Area, policy.WakeEpoch, reservation, oldCandidate, now())) return false;
                if (ledger.HasCurrentCandidate(candidate) && track(candidate, false) && ledger.TryPrepareForward(candidate, out receipt)) {
                    success = policy.CanPrepareIntent(candidate.Target, candidate.Area, policy.WakeEpoch, reservation, oldCandidate, now());
                    return success;
                }
                if (!ledger.TryRetireChanged(candidate, policy.WakeEpoch) ||
                    !policy.CanPrepareIntent(candidate.Target, candidate.Area, policy.WakeEpoch, reservation, oldCandidate, now()) ||
                    !track(candidate, ledger.CanReplaceCandidate(candidate.Target)) ||
                    !ledger.TryPrepareForward(candidate, out receipt)) return false;
                success = policy.CanPrepareIntent(candidate.Target, candidate.Area, policy.WakeEpoch, reservation, oldCandidate, now());
                return success;
            } catch (Exception) { return false; }
            finally {
                if (!success) { if (receipt != null) ledger.CancelUnissued(receipt); reservation.Owner.Abort(reservation); }
            }
        }
        internal void QuiesceForExit()
        {
            if (exitQuiesced) return;
            quiesced = exitQuiesced = true; layouts.BeginExit(); layoutNative.EndRestore();
            StopKeyboardIntent();
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
            timer.Stop(); pending.Clear(); areas.Clear();
        }
        internal bool TryFinishExit(bool allowInverse)
        { QuiesceForExit(); return layouts.TryFinishExit(allowInverse); }
        internal IEnumerable<SnapCorrectionArea> RestorationAreas(Func<SnapCorrectionArea, bool> selected)
        { return layouts.PendingAreas.Where(selected).ToArray(); }
        internal void QuiesceForRestore(IEnumerable<SnapCorrectionArea> selected)
        {
            if (disposed || exitQuiesced) throw new InvalidOperationException("Window correction is exiting.");
            var scope = selected.ToArray();
            CancelInputIntent();
            quiesced = true; timer.Stop(); pending.Clear(); areas.Clear();
            if (!layouts.BeginRestore(scope)) throw new InvalidOperationException("Another window restoration is pending.");
            layoutNative.BeginRestore(scope);
        }
        internal SnapLayoutAreaState RestoreAreaState(SnapCorrectionArea area)
        { return layoutNative.RestoreAreaState(area); }
        internal bool TryFinishRestore()
        { return !disposed && !exitQuiesced && layouts.TryFinishRestore(true); }
        internal bool ResumeAfterRestore()
        {
            if (disposed || exitQuiesced || !layouts.EndRestore()) return false;
            layoutNative.EndRestore(); quiesced = false; return true;
        }
        internal int PendingLayouts { get { return layouts.PendingCount; } }
        // Managed-only evidence for the release containment regression. Count
        // includes resolved receipts, so an attempted correction cannot pass by
        // merely cleaning itself up before this observation.
        internal bool AutomaticGeometryInactive { get { return !AutomaticGeometryCorrectionEnabled && hook == IntPtr.Zero &&
            keyboardIntent == null && !timer.Enabled && pending.Count == 0 && areas.Count == 0 && layouts.Count == 0; } }
        public void Dispose()
        {
            if (disposed) return;
            QuiesceForExit();
            if (!layouts.TryFinishExit(false)) throw new InvalidOperationException("Window layout recovery is not confirmed. Taskbar Select must remain open and paused.");
            disposed = true;
            timer.Dispose();
        }
    }
}
