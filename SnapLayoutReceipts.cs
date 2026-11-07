using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskbarSelect
{
    internal enum SnapLayoutReadKind { Unknown, Gone, Current }
    internal enum SnapLayoutAreaState { Unknown, OriginalReady, Changed }
    internal sealed class SnapLayoutObservation
    {
        internal SnapLayoutReadKind Kind;
        internal SnapCorrectionTarget Target;
        internal long Nonce;
        internal Native.Rect Outer, Visible;
        internal bool Maximized, Arranged, Iconic, Hidden, Ineligible;
        internal SnapLayoutObservation Copy() { return (SnapLayoutObservation)MemberwiseClone(); }
    }
    internal interface ISnapLayoutApi
    {
        SnapLayoutObservation Read(SnapCorrectionTarget target);
        bool SetNonce(SnapCorrectionTarget target, long nonce);
        bool RemoveNonce(SnapCorrectionTarget target, long nonce);
        SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area);
        bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> beforeNativeSet);
    }
    internal sealed class SnapLayoutCandidate
    {
        internal SnapCorrectionTarget Target;
        internal SnapCorrectionArea Area;
        internal Native.Rect Before, BeforeVisible, Desired, DesiredVisible;
        internal bool Maximized;
        internal bool Valid
        {
            get {
                Native.Rect expected;
                return Target.Valid && Area != null && !AreaPolicy.Equal(Before, Desired) &&
                    TaskbarMover.ValidVisibleFrame(Before, BeforeVisible) && TaskbarMover.ValidVisibleFrame(Desired, DesiredVisible) &&
                    SnapCorrectionGeometry.TryExpand(Maximized, !Maximized, Before, BeforeVisible, Area.ToRecord(), out expected) &&
                    AreaPolicy.Equal(expected, Desired) &&
                    (long)DesiredVisible.Left - BeforeVisible.Left == (long)Desired.Left - Before.Left &&
                    (long)DesiredVisible.Top - BeforeVisible.Top == (long)Desired.Top - Before.Top &&
                    (long)DesiredVisible.Right - BeforeVisible.Right == (long)Desired.Right - Before.Right &&
                    (long)DesiredVisible.Bottom - BeforeVisible.Bottom == (long)Desired.Bottom - Before.Bottom;
            }
        }
    }
    internal sealed class SnapLayoutReceiptLedger
    {
        internal const int MaximumReceipts = 128;
        internal const int MaximumRetirementProbes = 8;
        internal sealed class Receipt
        {
            internal readonly SnapCorrectionTarget Target;
            internal readonly SnapCorrectionArea Area;
            internal readonly Native.Rect Before, BeforeVisible, Desired, DesiredVisible;
            internal readonly bool Maximized;
            internal readonly long Nonce;
            internal long Generation;
            internal bool Preparing, Invoked, Returned, ResultKnown, Accepted, Confirmed, InverseAttempted, InverseReturned, ReleaseAttempted, ReleaseReturned, Resolved, Skipped;
            internal bool ChangedRelease, ChangedReleaseSucceeded;
            internal bool HasRetirementEpoch;
            internal long RetirementEpoch;
            internal int RetirementProbes;
            internal Receipt(SnapLayoutCandidate c, long nonce)
            { Target = c.Target; Area = c.Area; Before = c.Before; BeforeVisible = c.BeforeVisible; Desired = c.Desired; DesiredVisible = c.DesiredVisible; Maximized = c.Maximized; Nonce = nonce; }
            internal bool Same(SnapLayoutCandidate c)
            { return Target.Equals(c.Target) && Area.Equals(c.Area) && Maximized == c.Maximized && AreaPolicy.Equal(Before, c.Before) && AreaPolicy.Equal(BeforeVisible, c.BeforeVisible) && AreaPolicy.Equal(Desired, c.Desired) && AreaPolicy.Equal(DesiredVisible, c.DesiredVisible); }
        }
        internal sealed class Ticket
        {
            internal readonly Receipt Receipt;
            internal readonly long Generation;
            internal Ticket(Receipt receipt) { Receipt = receipt; Generation = receipt.Generation; }
        }
        // An opaque snapshot of one actually completed receipt. All use sites
        // revalidate owner/reference/generation; a copied proof is not authority.
        internal sealed class IntentAnchor
        {
            internal readonly SnapLayoutReceiptLedger Owner;
            internal readonly Receipt Receipt;
            internal readonly SnapCorrectionTarget Target;
            internal readonly SnapCorrectionArea Area;
            internal readonly long Nonce, Generation;
            internal IntentAnchor(SnapLayoutReceiptLedger owner, Receipt receipt)
            { Owner = owner; Receipt = receipt; Target = receipt.Target; Area = receipt.Area; Nonce = receipt.Nonce; Generation = receipt.Generation; }
        }
        readonly ISnapLayoutApi api;
        readonly Dictionary<SnapCorrectionTarget, Receipt> receipts = new Dictionary<SnapCorrectionTarget, Receipt>();
        readonly HashSet<SnapCorrectionArea> restoring = new HashSet<SnapCorrectionArea>();
        int allocated;
        bool closing, stepping;
        internal SnapLayoutReceiptLedger(ISnapLayoutApi api) { if (api == null) throw new ArgumentNullException("api"); this.api = api; }
        internal int Count { get { return allocated; } }
        internal int PendingCount { get { return receipts.Values.Count(r => !r.Resolved); } }
        internal bool Closing { get { return closing; } }
        internal bool Restoring { get { return restoring.Count != 0; } }
        internal bool CanReplaceCandidate(SnapCorrectionTarget target)
        { Receipt receipt; return receipts.TryGetValue(target, out receipt) && receipt.Resolved; }
        internal bool HasCurrentCandidate(SnapLayoutCandidate candidate)
        {
            Receipt receipt;
            return !closing && !Restoring && !stepping && candidate != null && receipts.TryGetValue(candidate.Target, out receipt) &&
                !receipt.Resolved && receipt.Same(candidate);
        }
        internal IEnumerable<SnapCorrectionArea> PendingAreas
        { get { return receipts.Values.Where(r => !r.Resolved).Select(r => r.Area).Distinct().ToArray(); } }
        static bool Identity(SnapLayoutObservation o, Receipt r)
        { return o != null && o.Kind == SnapLayoutReadKind.Current && o.Target.Equals(r.Target); }
        static bool Mode(SnapLayoutObservation o, Receipt r)
        { return !o.Ineligible && !o.Hidden && !o.Iconic && o.Maximized == r.Maximized && o.Arranged != r.Maximized; }
        static bool Shape(SnapLayoutObservation o, Receipt r, bool desired)
        { return Mode(o, r) && AreaPolicy.Equal(o.Outer, desired ? r.Desired : r.Before) && AreaPolicy.Equal(o.Visible, desired ? r.DesiredVisible : r.BeforeVisible); }
        bool Current(Ticket ticket)
        { Receipt current; return ticket != null && receipts.TryGetValue(ticket.Receipt.Target, out current) && Object.ReferenceEquals(current, ticket.Receipt) && current.Generation == ticket.Generation && !current.Resolved; }
        bool AnchorReady(IntentAnchor anchor)
        {
            Receipt current;
            return anchor != null && Object.ReferenceEquals(anchor.Owner, this) && !closing && !Restoring &&
                receipts.TryGetValue(anchor.Target, out current) && Object.ReferenceEquals(current, anchor.Receipt) &&
                current.Generation == anchor.Generation && current.Nonce == anchor.Nonce && !current.Resolved && !current.Preparing &&
                current.Invoked && current.Returned && current.ResultKnown && current.Accepted && current.Confirmed &&
                !current.InverseAttempted && !current.ReleaseAttempted;
        }
        static bool EligibleIntentObservation(SnapLayoutObservation o, IntentAnchor anchor)
        { return o != null && o.Kind == SnapLayoutReadKind.Current && o.Target.Equals(anchor.Target) && o.Nonce == anchor.Nonce &&
                !o.Ineligible && !o.Hidden && !o.Iconic && !o.Maximized && TaskbarMover.ValidVisibleFrame(o.Outer, o.Visible); }
        internal bool TryIntentAnchor(SnapCorrectionTarget target, out IntentAnchor anchor)
        {
            anchor = null; Receipt r;
            if (stepping || !receipts.TryGetValue(target, out r)) return false;
            var candidate = new IntentAnchor(this, r);
            if (!AnchorReady(candidate)) return false;
            stepping = true;
            try {
                var o = api.Read(target);
                if (!AnchorReady(candidate) || !EligibleIntentObservation(o, candidate)) return false;
                anchor = candidate; return true;
            } catch (Exception) { return false; }
            finally { stepping = false; }
        }
        static SnapIntentProof IntentProof(IntentAnchor anchor, SnapLayoutObservation o, SnapLayoutCandidate candidate,
            long epoch, long context, long now)
        {
            return new SnapIntentProof { Target = o.Target, Area = anchor.Area, Nonce = o.Nonce, ReceiptGeneration = anchor.Generation,
                WakeEpoch = epoch, Context = context, At = now, Verified = true, ReceiptReady = true, OwnedAreaReady = true, ActionReady = true,
                Normal = !o.Arranged && !o.Maximized, Arranged = o.Arranged, Maximized = o.Maximized,
                Outer = candidate == null ? o.Outer : candidate.Before, Visible = candidate == null ? o.Visible : candidate.BeforeVisible,
                Desired = candidate == null ? o.Outer : candidate.Desired, DesiredVisible = candidate == null ? o.Visible : candidate.DesiredVisible };
        }
        bool TryIntentOldProof(IntentAnchor anchor, SnapLayoutCandidate candidate, long epoch, long context, long now,
            Func<bool> ready, out SnapIntentProof proof)
        {
            proof = null;
            if (stepping || epoch < 0 || context <= 0 || now < 0 || ready == null || !AnchorReady(anchor) ||
                (candidate != null && (!candidate.Valid || candidate.Maximized || !candidate.Target.Equals(anchor.Target) || !candidate.Area.Equals(anchor.Area)))) return false;
            stepping = true;
            try {
                if (!ready() || !AnchorReady(anchor)) return false;
                var o = api.Read(anchor.Target);
                if (!AnchorReady(anchor) || !EligibleIntentObservation(o, anchor) || (candidate != null && !CandidateBefore(o, candidate)) ||
                    !ready() || !AnchorReady(anchor)) return false;
                proof = IntentProof(anchor, o, candidate, epoch, context, now); return true;
            } catch (Exception) { return false; }
            finally { stepping = false; }
        }
        internal bool TryIntentWitness(IntentAnchor anchor, long epoch, long context, long now, Func<bool> ready, out SnapIntentProof proof)
        { return TryIntentOldProof(anchor, null, epoch, context, now, ready, out proof); }
        internal bool TryIntentCandidate(IntentAnchor anchor, SnapLayoutCandidate candidate, long epoch, long context, long now, Func<bool> ready, out SnapIntentProof proof)
        { proof = null; return candidate != null && TryIntentOldProof(anchor, candidate, epoch, context, now, ready, out proof); }
        bool IntentLineage(IntentAnchor anchor, Ticket ticket, SnapLayoutCandidate candidate)
        {
            if (anchor == null || !Object.ReferenceEquals(anchor.Owner, this) || closing || Restoring || !Current(ticket) || candidate == null || !candidate.Valid || candidate.Maximized ||
                !anchor.Target.Equals(candidate.Target) || !anchor.Area.Equals(candidate.Area) || !ticket.Receipt.Same(candidate) || ticket.Receipt.Preparing ||
                ticket.Receipt.InverseAttempted || ticket.Receipt.ReleaseAttempted) return false;
            var r = ticket.Receipt;
            return Object.ReferenceEquals(r, anchor.Receipt) ? r.Nonce == anchor.Nonce && anchor.Generation < Int64.MaxValue && r.Generation == anchor.Generation + 1 :
                anchor.Receipt.Generation == anchor.Generation && anchor.Receipt.Resolved && anchor.Receipt.ChangedRelease && anchor.Receipt.ChangedReleaseSucceeded &&
                anchor.Receipt.ReleaseAttempted && anchor.Receipt.ReleaseReturned && !anchor.Receipt.InverseAttempted && r.Nonce > anchor.Nonce && r.Generation == 1;
        }
        internal bool TryIntentPrepared(IntentAnchor anchor, Ticket ticket, SnapLayoutCandidate candidate, long epoch, long context, long now,
            Func<bool> ready, out SnapIntentProof proof)
        {
            proof = null;
            if (stepping || ready == null || epoch < 0 || context <= 0 || now < 0 || !IntentLineage(anchor, ticket, candidate) || ticket.Receipt.Invoked || ticket.Receipt.Returned) return false;
            stepping = true;
            try {
                if (!ready() || !IntentLineage(anchor, ticket, candidate)) return false;
                var o = api.Read(candidate.Target);
                if (!IntentLineage(anchor, ticket, candidate) || o == null || o.Nonce != ticket.Receipt.Nonce || !CandidateBefore(o, candidate) ||
                    !ready() || !IntentLineage(anchor, ticket, candidate) || ticket.Receipt.Invoked || ticket.Receipt.Returned) return false;
                proof = IntentProof(anchor, o, candidate, epoch, context, now);
                proof.Nonce = ticket.Receipt.Nonce; proof.ReceiptGeneration = ticket.Generation;
                proof.OriginNonce = anchor.Nonce; proof.OriginGeneration = anchor.Generation; proof.LineageVerified = true;
                proof.OldMarkerRetired = !Object.ReferenceEquals(ticket.Receipt, anchor.Receipt); return true;
            } catch (Exception) { return false; }
            finally { stepping = false; }
        }
        internal bool IntentInvocationCurrent(IntentAnchor anchor, Ticket ticket, SnapLayoutCandidate candidate)
        { return !stepping && IntentLineage(anchor, ticket, candidate) && ticket.Receipt.Invoked && !ticket.Receipt.Returned; }
        static bool CandidateBefore(SnapLayoutObservation observed, SnapLayoutCandidate candidate)
        {
            return observed != null && observed.Kind == SnapLayoutReadKind.Current && observed.Target.Equals(candidate.Target) &&
                !observed.Ineligible && !observed.Hidden && !observed.Iconic && observed.Maximized == candidate.Maximized &&
                observed.Arranged != candidate.Maximized && AreaPolicy.Equal(observed.Outer, candidate.Before) &&
                AreaPolicy.Equal(observed.Visible, candidate.BeforeVisible);
        }
        bool Retirable(Receipt r, SnapLayoutCandidate candidate, long generation)
        {
            Receipt current;
            return !closing && !Restoring && receipts.TryGetValue(r.Target, out current) && Object.ReferenceEquals(r, current) &&
                !r.Resolved && !r.Preparing && r.Generation == generation &&
                r.Invoked && r.Returned && r.ResultKnown && r.Accepted && r.Confirmed && !r.InverseAttempted;
        }
        // Called only for a fresh, stable candidate already admitted by the
        // existing correction policy. Yield a completed old shape that the
        // user positively changed; never inverse it or replenish any budget.
        internal bool TryRetireChanged(SnapLayoutCandidate candidate) { return TryRetireChanged(candidate, 0); }
        internal bool TryRetireChanged(SnapLayoutCandidate candidate, long wakeEpoch)
        {
            if (closing || Restoring || stepping || candidate == null || !candidate.Valid || wakeEpoch < 0) return false;
            Receipt r;
            if (!receipts.TryGetValue(candidate.Target, out r) || r.Resolved) return true;
            long generation = r.Generation;
            if (!Retirable(r, candidate, generation))
                return r.Same(candidate) && !r.Preparing && !r.ReleaseAttempted && !r.InverseAttempted;
            // Failed or unreadable retirement cannot add a high-frequency
            // native poll loop. Only the already accepted correction epoch can
            // renew this read allowance; no mutation credit is created here.
            if (!r.HasRetirementEpoch || wakeEpoch > r.RetirementEpoch) {
                r.HasRetirementEpoch = true; r.RetirementEpoch = wakeEpoch; r.RetirementProbes = 0;
            }
            if (wakeEpoch != r.RetirementEpoch || r.RetirementProbes >= MaximumRetirementProbes) return false;
            r.RetirementProbes++;
            stepping = true;
            try {
                // A successful earlier removal with unreadable immediate
                // readback may be confirmed later, without another removal.
                if (r.ReleaseAttempted) {
                    if (!r.ChangedRelease || !r.ReleaseReturned || !r.ChangedReleaseSucceeded) return false;
                    var retired = api.Read(r.Target);
                    if (!Retirable(r, candidate, generation) || !CandidateBefore(retired, candidate) || retired.Nonce != 0) return false;
                    r.Resolved = r.Skipped = true; return true;
                }
                var observed = api.Read(r.Target);
                if (!Retirable(r, candidate, generation) || !CandidateBefore(observed, candidate) || Shape(observed, r, true)) return false;
                // An old, positively completed receipt with a missing HWND
                // property has no remaining ownership to remove. Yield it,
                // including same-owner handle reuse, only on fresh exact new
                // candidate proof. New forward still requires its own SetProp.
                if (observed.Nonce != 0 && observed.Nonce != r.Nonce) return false;
                var fresh = api.Read(r.Target);
                if (!Retirable(r, candidate, generation) || !Identity(fresh, r) || fresh.Nonce != observed.Nonce ||
                    !CandidateBefore(fresh, candidate) || Shape(fresh, r, true)) return false;
                if (fresh.Nonce == 0) { r.Resolved = r.Skipped = true; return true; }
                // Ordinary same-candidate wake reuse keeps its current marker
                // and generation handling; there is no reason to remove it.
                if (r.Same(candidate)) return true;
                r.ChangedRelease = r.ReleaseAttempted = true;
                try { r.ChangedReleaseSucceeded = api.RemoveNonce(r.Target, r.Nonce); }
                finally { r.ReleaseReturned = true; }
                if (!r.ChangedReleaseSucceeded || !Retirable(r, candidate, generation)) return false;
                var after = api.Read(r.Target);
                if (!Retirable(r, candidate, generation) || !CandidateBefore(after, candidate) || after.Nonce != 0) return false;
                r.Resolved = r.Skipped = true; return true;
            }
            catch (Exception) { return false; }
            finally { stepping = false; }
        }
        internal bool TryPrepareForward(SnapLayoutCandidate candidate, out Ticket ticket)
        {
            ticket = null;
            if (closing || Restoring || stepping || candidate == null || !candidate.Valid) return false;
            Receipt r;
            bool created = !receipts.TryGetValue(candidate.Target, out r) || r.Resolved;
            if (created) {
                if (allocated >= MaximumReceipts) return false;
                r = new Receipt(candidate, ++allocated); receipts[r.Target] = r;
            } else if (r.Preparing || r.Resolved || r.InverseAttempted || r.ReleaseAttempted || !r.Same(candidate) ||
                (r.Invoked && (!r.Returned || (!r.Confirmed && (!r.ResultKnown || r.Accepted))))) return false;
            r.Preparing = true;
            long generation = r.Generation;
            try {
                var before = api.Read(r.Target);
                if (closing || Restoring || stepping || r.Resolved || r.Generation != generation || !Identity(before, r) || !Shape(before, r, false) ||
                    (created ? before.Nonce != 0 : before.Nonce != r.Nonce)) return false;
                if (created && !api.SetNonce(r.Target, r.Nonce)) return false;
                var marked = api.Read(r.Target);
                if (closing || Restoring || stepping || r.Resolved || r.Generation != generation || !Identity(marked, r) || marked.Nonce != r.Nonce || !Shape(marked, r, false)) return false;
                // Reset for this invocation before any native forward call.
                r.Generation++; r.Invoked = r.Returned = r.ResultKnown = r.Accepted = r.Confirmed = false;
                ticket = new Ticket(r); return true;
            } finally { r.Preparing = false; }
        }
        internal bool MarkInvoked(Ticket ticket)
        {
            if (closing || Restoring || stepping || !Current(ticket) || ticket.Receipt.Preparing || ticket.Receipt.Invoked || ticket.Receipt.ReleaseAttempted) return false;
            var o = api.Read(ticket.Receipt.Target);
            if (closing || Restoring || stepping || !Current(ticket) || ticket.Receipt.Preparing || ticket.Receipt.Invoked || ticket.Receipt.ReleaseAttempted || !Identity(o, ticket.Receipt) ||
                o.Nonce != ticket.Receipt.Nonce || !Shape(o, ticket.Receipt, false)) return false;
            ticket.Receipt.Invoked = true; return true;
        }
        internal void CancelUnissued(Ticket ticket)
        {
            if (!Current(ticket) || ticket.Receipt.Invoked) return;
            ticket.Receipt.Returned = ticket.Receipt.ResultKnown = true; ticket.Receipt.Accepted = false;
        }
        internal void RecordReturned(Ticket ticket, bool accepted)
        {
            if (!Current(ticket) || !ticket.Receipt.Invoked) return;
            ticket.Receipt.Returned = ticket.Receipt.ResultKnown = true; ticket.Receipt.Accepted = accepted;
            Observe(ticket.Receipt);
        }
        internal void RecordUnknown(Ticket ticket)
        {
            if (!Current(ticket) || !ticket.Receipt.Invoked) return;
            ticket.Receipt.Returned = true; ticket.Receipt.ResultKnown = false;
            Observe(ticket.Receipt);
        }
        void Observe(Receipt r)
        {
            if (r.Resolved || r.InverseAttempted || !r.Invoked || !r.Returned || r.Confirmed) return;
            long generation = r.Generation;
            var o = api.Read(r.Target);
            if (!r.Resolved && !r.InverseAttempted && r.Generation == generation && Identity(o, r) && o.Nonce == r.Nonce && Shape(o, r, true)) r.Confirmed = true;
        }
        internal void ObserveForward()
        { foreach (var r in receipts.Values.ToArray()) Observe(r); }
        internal void ObserveForward(SnapCorrectionTarget target)
        { Receipt r; if (!stepping && receipts.TryGetValue(target, out r)) Observe(r); }
        internal void BeginExit() { closing = true; }
        internal bool BeginRestore(IEnumerable<SnapCorrectionArea> areas)
        {
            if (closing || areas == null) return false;
            var selection = new HashSet<SnapCorrectionArea>(areas.Where(a => a != null));
            if (Restoring) return restoring.SetEquals(selection);
            foreach (var area in selection) restoring.Add(area);
            return Restoring;
        }
        internal bool TryFinishRestore(bool allowInverse)
        {
            if (closing || !Restoring || stepping) return false;
            stepping = true;
            try {
                foreach (var r in receipts.Values.Where(r => restoring.Contains(r.Area)).ToArray()) {
                    try { Step(r, allowInverse); } catch (Exception) { }
                }
                return !receipts.Values.Any(r => !r.Resolved && restoring.Contains(r.Area));
            } finally { stepping = false; }
        }
        internal bool EndRestore()
        {
            if (closing || stepping || !Restoring || receipts.Values.Any(r => !r.Resolved && restoring.Contains(r.Area))) return false;
            restoring.Clear(); return true;
        }
        bool Release(Receipt r, SnapLayoutObservation o)
        {
            if (!Identity(o, r) || o.Nonce != r.Nonce) { r.Resolved = true; return true; }
            if (r.ReleaseAttempted) return false;
            r.ReleaseAttempted = true;
            bool removed;
            try { removed = api.RemoveNonce(r.Target, r.Nonce); }
            finally { r.ReleaseReturned = true; }
            if (!removed) return false;
            var after = api.Read(r.Target);
            if (after == null || after.Kind == SnapLayoutReadKind.Unknown) return false;
            if (after.Kind == SnapLayoutReadKind.Gone || !after.Target.Equals(r.Target) || after.Nonce != r.Nonce) { r.Resolved = true; return true; }
            return false;
        }
        void Step(Receipt r, bool allowInverse)
        {
            if (r.Resolved || r.Preparing) return;
            var o = api.Read(r.Target);
            if (o == null || o.Kind == SnapLayoutReadKind.Unknown) return;
            if (o.Kind == SnapLayoutReadKind.Gone || !o.Target.Equals(r.Target)) { r.Resolved = true; r.Skipped = true; return; }
            if (o.Nonce != r.Nonce) {
                // Release was authorized only after a safe final shape/skip;
                // a later read can confirm it without repeating RemoveProp.
                if (r.ReleaseAttempted && r.ReleaseReturned) { r.Resolved = true; return; }
                if (r.InverseAttempted) return; // Latest inverse ambiguity cannot borrow old forward confirmation.
                if (!r.Invoked || r.Confirmed || (r.Returned && r.ResultKnown && !r.Accepted)) { r.Resolved = true; r.Skipped = true; }
                return; // Same live identity + pending forward + missing nonce is ambiguous.
            }
            if (r.InverseAttempted) { if (r.InverseReturned && Shape(o, r, false)) Release(r, o); return; }
            if (!r.Invoked) { Release(r, o); return; }
            if (!r.Returned) return; // Never invert while the latest native forward call is still executing.
            if (Shape(o, r, true)) r.Confirmed = true;
            if (!r.Confirmed) {
                if (r.ResultKnown && !r.Accepted && Shape(o, r, false)) Release(r, o);
                return; // Accepted but still Before/other shape can be a late queued write.
            }
            if (!Shape(o, r, true)) { r.Skipped = true; Release(r, o); return; }
            var ready = api.AreaState(r.Target, r.Area);
            if (ready == SnapLayoutAreaState.Changed) { r.Skipped = true; Release(r, o); return; }
            if (!allowInverse || ready != SnapLayoutAreaState.OriginalReady) return;
            var fresh = api.Read(r.Target);
            if (!Identity(fresh, r) || fresh.Nonce != r.Nonce || !Shape(fresh, r, true) ||
                api.AreaState(r.Target, r.Area) != SnapLayoutAreaState.OriginalReady || r.Resolved || r.InverseAttempted) return;
            fresh = api.Read(r.Target);
            if (!Identity(fresh, r) || fresh.Nonce != r.Nonce || !Shape(fresh, r, true) || r.Resolved || r.InverseAttempted) return;
            // At most one inverse request, including rejection/unknown outcome.
            try {
                api.SetBounds(r.Target, r.Before, delegate {
                    if (r.Resolved || r.InverseAttempted) return false;
                    r.InverseAttempted = true; return true;
                });
            } finally { if (r.InverseAttempted) r.InverseReturned = true; }
            // A fresh adapter guard can veto before any native call. Do not
            // invent a queued inverse; later positive user change may skip.
            if (!r.InverseAttempted) return;
            var after = api.Read(r.Target);
            if (Identity(after, r) && after.Nonce == r.Nonce && Shape(after, r, false)) Release(r, after);
        }
        internal bool TryFinishExit(bool allowInverse)
        {
            closing = true;
            if (stepping) return false;
            stepping = true;
            try {
                foreach (var r in receipts.Values.ToArray()) {
                    try { Step(r, allowInverse); } catch (Exception) { /* Retain uncertain ownership; no retry mutation. */ }
                }
                return PendingCount == 0;
            } finally { stepping = false; }
        }
    }
}
