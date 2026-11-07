using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal sealed partial class SnapWorkAreas
    {
        SnapKeyboardObserver keyboardIntent;
        SnapIntentPolicy inputIntent;
        SnapLayoutReceiptLedger.IntentAnchor inputAnchor;
        Func<bool> inputActionReady;
        IntPtr inputForeground;
        bool inputStopped, inputCommitBusy;

        // Explicit desktop-only factory. Neither constructor enables this
        // observer, so worker and test construction remains observer-free.
        internal void EnableKeyboardIntent(Func<bool> actionReady)
        {
            if (!AutomaticGeometryCorrectionEnabled || keyboardIntent != null || inputStopped || disposed || quiesced || onlyProcess != 0 || actionReady == null) return;
            inputActionReady = actionReady;
            inputIntent = new SnapIntentPolicy(Stopwatch.Frequency, 1);
            correctionPolicy.IntentPolicy = inputIntent;
            keyboardIntent = new SnapKeyboardObserver();
            if (!keyboardIntent.Start()) CancelInputIntent();
        }
        bool InputActionReady()
        {
            if (!AutomaticGeometryCorrectionEnabled || disposed || quiesced || inputStopped || inputActionReady == null) return false;
            try { return inputActionReady(); } catch (Exception) { return false; }
        }
        internal void CancelInputIntent()
        {
            if (inputIntent != null) inputIntent.Invalidate();
            if (keyboardIntent != null) keyboardIntent.Invalidate();
            inputAnchor = null;
            foreach (var item in pending.Where(p => p.Value.Intent != null).ToArray()) pending.Remove(item.Key);
        }
        internal void ObserveInputForeground(IntPtr window)
        {
            if (keyboardIntent == null) return;
            if (window != inputForeground) { CancelInputIntent(); inputForeground = window; }
            RefreshInputWitness(window);
        }
        bool InputAreaReady(SnapLayoutReceiptLedger.IntentAnchor anchor)
        {
            if (!InputActionReady() || keyboardIntent == null || !keyboardIntent.Available || anchor == null ||
                TaskbarMover.ForegroundWindow() != anchor.Target.Window) return false;
            bool active = areas.Any(a => { SnapCorrectionArea current; return SnapCorrectionArea.TryCapture(a.Record, out current) && current.Equals(anchor.Area); });
            return active && layoutNative.IntentAreaReady(anchor.Target, anchor.Area) && InputActionReady() &&
                TaskbarMover.ForegroundWindow() == anchor.Target.Window;
        }
        void RefreshInputWitness(IntPtr foreground)
        {
            if (keyboardIntent == null) return;
            if (foreground != inputForeground) { CancelInputIntent(); inputForeground = foreground; }
            if (inputCommitBusy) return; // Preparation has deliberately changed the receipt generation.
            SnapCorrectionTarget target; SnapLayoutReceiptLedger.IntentAnchor anchor; SnapIntentProof proof;
            if (!InputActionReady() || !CaptureTarget(foreground, out target) || !layouts.TryIntentAnchor(target, out anchor) ||
                !layouts.TryIntentWitness(anchor, correctionPolicy.WakeEpoch, inputIntent.Context, Stopwatch.GetTimestamp(),
                    delegate { return InputAreaReady(anchor); }, out proof)) { CancelInputIntent(); return; }
            if (inputAnchor != null && (inputAnchor.Nonce != anchor.Nonce || inputAnchor.Generation != anchor.Generation ||
                !inputAnchor.Target.Equals(anchor.Target) || !inputAnchor.Area.Equals(anchor.Area))) {
                CancelInputIntent();
                // Proof was captured in the retired controller context.
                return;
            }
            inputAnchor = anchor;
            keyboardIntent.PublishWitness(proof, layoutNative.NonceProperty);
        }
        // Called by the existing desktop 80ms UI pump. An empty handoff reads
        // only managed/Interlocked state: no process, monitor, DWM or window API.
        internal void PollKeyboardIntent()
        {
            if (keyboardIntent == null) return;
            if (!InputActionReady()) { if (inputAnchor != null) CancelInputIntent(); return; }
            SnapChordNotice notice; long revision;
            if (!keyboardIntent.TryTake(out notice, out revision)) return;
            if (notice.Kind != SnapChordNoticeKind.Completed) {
                if (notice.Kind == SnapChordNoticeKind.Cancelled) inputIntent.CancelPendingInput();
                return;
            }
            var anchor = inputAnchor;
            if (anchor == null || notice.Before == null || notice.Before.Nonce != anchor.Nonce || notice.Before.ReceiptGeneration != anchor.Generation ||
                !notice.Before.Target.Equals(anchor.Target) || !notice.Before.Area.Equals(anchor.Area) ||
                !inputIntent.AdmitCompleted(notice, revision, keyboardIntent.Current)) return;
            Native.Rect outer, corrected, visible; SnapCorrectionArea area; SnapCorrectionTarget target; bool max;
            if (!TryCorrection(notice.Window, out outer, out corrected, out area, out max) || max || !CaptureTarget(notice.Window, out target) ||
                !target.Equals(anchor.Target) || !area.Equals(anchor.Area) ||
                Native.DwmGetWindowAttribute(notice.Window, 9, out visible, Marshal.SizeOf(typeof(Native.Rect))) != 0) { CancelInputIntent(); return; }
            var candidate = new SnapLayoutCandidate { Target = target, Area = area, Before = outer, BeforeVisible = visible, Desired = corrected,
                DesiredVisible = new Native.Rect { Left = visible.Left + corrected.Left - outer.Left, Top = visible.Top + corrected.Top - outer.Top,
                    Right = visible.Right + corrected.Right - outer.Right, Bottom = visible.Bottom + corrected.Bottom - outer.Bottom } };
            SnapIntentProof old; SnapIntentPermit permit; SnapIntentReservation reservation;
            long now = Stopwatch.GetTimestamp();
            if (!layouts.TryIntentCandidate(anchor, candidate, correctionPolicy.WakeEpoch, inputIntent.Context, now,
                    delegate { return InputAreaReady(anchor); }, out old) || !inputIntent.TryPermit(old, now, out permit) ||
                !inputIntent.TryReserve(permit, old, now, out reservation) ||
                !correctionPolicy.CanPrepareIntent(target, area, correctionPolicy.WakeEpoch, reservation, old, now)) { CancelInputIntent(); return; }
            pending[notice.Window] = new Pending { Target = target, Area = area, Bounds = outer, Corrected = corrected, Maximized = false,
                Since = now, WakeEpoch = correctionPolicy.WakeEpoch, Intent = reservation, Anchor = anchor, OldProof = old };
            timer.Start();
        }
        void ProcessIntentCandidate(IntPtr window, Pending state, SnapLayoutCandidate candidate)
        {
            if (!AutomaticGeometryCorrectionEnabled || inputCommitBusy || state.Intent == null) return;
            inputCommitBusy = true;
            SnapLayoutReceiptLedger.Ticket ticket = null;
            bool invoked = false, returned = false;
            try {
                SnapIntentProof old, prepared;
                if (!IsCurrent(window, state) || !layouts.TryIntentCandidate(state.Anchor, candidate, state.WakeEpoch, inputIntent.Context,
                        Stopwatch.GetTimestamp(), delegate { return InputAreaReady(state.Anchor); }, out old) ||
                    !TryPrepareOwnedLayout(correctionPolicy, layouts, candidate, layoutNative.Track, state.Intent, old,
                        Stopwatch.GetTimestamp, out ticket)) return;
                Native.Rect outer, desired; SnapCorrectionArea area; SnapCorrectionTarget target; bool max;
                if (!IsCurrent(window, state) || !TryCorrection(window, out outer, out desired, out area, out max) || max ||
                    !CaptureTarget(window, out target) || !target.Equals(candidate.Target) || !area.Equals(candidate.Area) ||
                    !AreaPolicy.Equal(outer, candidate.Before) || !AreaPolicy.Equal(desired, candidate.Desired) ||
                    !layouts.TryIntentPrepared(state.Anchor, ticket, candidate, state.WakeEpoch, inputIntent.Context, Stopwatch.GetTimestamp(),
                        delegate { return InputAreaReady(state.Anchor); }, out prepared) || !IsCurrent(window, state) ||
                    !correctionPolicy.TryConsume(target, area, state.WakeEpoch, state.Intent, prepared, Stopwatch.GetTimestamp())) return;
                if (!layouts.MarkInvoked(ticket)) return;
                invoked = true;
                // No fallible native provider follows this last authorization
                // fence before SetWindowPos. MarkInvoked performed the final
                // lifetime/nonce/exact-shape native read for this actual ticket.
                if (!IsCurrent(window, state) || !InputActionReady() || !layouts.IntentInvocationCurrent(state.Anchor, ticket, candidate) ||
                    !inputIntent.NativeAuthorizationStillCurrent(state.Intent, prepared, Stopwatch.GetTimestamp())) {
                    layouts.RecordReturned(ticket, false); returned = true; return;
                }
                pending.Remove(window);
                lastDiagnosticTarget = target;
                diagnostics.RecordAttempt(state.WakeEpoch, candidate.Before, candidate.Desired, false);
                bool accepted = SetWindowPos(window, IntPtr.Zero, candidate.Desired.Left, candidate.Desired.Top,
                    candidate.Desired.Right - candidate.Desired.Left, candidate.Desired.Bottom - candidate.Desired.Top, 0x0014 | 0x0200 | 0x4000);
                layouts.RecordReturned(ticket, accepted); returned = true;
                if (accepted) diagnostics.RecordAccepted();
                ObserveLastCorrection();
            } catch (Exception) {
                if (invoked && !returned) { layouts.RecordUnknown(ticket); returned = true; }
            }
            finally {
                if (ticket != null && !invoked) layouts.CancelUnissued(ticket);
                if (IsCurrent(window, state)) pending.Remove(window);
                CancelInputIntent(); inputCommitBusy = false;
            }
        }
        void StopKeyboardIntent()
        {
            CancelInputIntent(); inputStopped = true;
            if (keyboardIntent != null) keyboardIntent.Dispose();
        }
    }
}
