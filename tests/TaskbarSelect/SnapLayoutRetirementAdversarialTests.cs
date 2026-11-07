using System;
using System.Collections.Generic;

namespace TaskbarSelect
{
    // Independent retirement regressions. Every observation/mutation is injected.
    // The real pipeline and native adapter's Track method are exercised, but
    // Track performs only managed candidate validation/dictionary operations;
    // no adapter HWND/property/geometry method or native API is invoked.
    internal static class SnapLayoutRetirementAdversarialTests
    {
        sealed class FakeApi : ISnapLayoutApi
        {
            internal readonly Dictionary<SnapCorrectionTarget, SnapLayoutObservation> Windows = new Dictionary<SnapCorrectionTarget, SnapLayoutObservation>();
            internal Action OnRead, OnRemove;
            internal int Reads, Marks, Removes, Bounds, AreaReads, UnknownReads;
            internal bool RemoveAccepted = true, RemoveEffect = true, ApplyBounds = true;
            internal SnapLayoutCandidate Current;
            static void Once(ref Action callback) { var action = callback; callback = null; if (action != null) action(); }
            public SnapLayoutObservation Read(SnapCorrectionTarget target)
            {
                Reads++;
                if (UnknownReads > 0) { UnknownReads--; return new SnapLayoutObservation { Kind = SnapLayoutReadKind.Unknown }; }
                SnapLayoutObservation value;
                var result = Windows.TryGetValue(target, out value) ? value.Copy() : new SnapLayoutObservation { Kind = SnapLayoutReadKind.Gone };
                Once(ref OnRead); return result;
            }
            public bool SetNonce(SnapCorrectionTarget target, long nonce) { Marks++; Windows[target].Nonce = nonce; return true; }
            public bool RemoveNonce(SnapCorrectionTarget target, long nonce)
            {
                Removes++;
                if (Windows[target].Nonce != nonce) return false;
                if (RemoveEffect) Windows[target].Nonce = 0;
                Once(ref OnRemove); return RemoveAccepted;
            }
            public SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area)
            { AreaReads++; return SnapLayoutAreaState.OriginalReady; }
            public bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> beforeNativeSet)
            {
                if (!beforeNativeSet()) return false;
                Bounds++;
                if (ApplyBounds) { Windows[target].Outer = outer; Windows[target].Visible = Current.BeforeVisible; }
                return true;
            }
        }
        sealed class Fixture
        {
            internal readonly FakeApi Api = new FakeApi();
            internal readonly SnapLayoutReceiptLedger Ledger;
            internal readonly SnapCorrectionPolicy Policy;
            readonly SnapLayoutNative tracked = new SnapLayoutNative();
            internal readonly SnapLayoutCandidate Max, Half, Wide, Other;
            internal long Now = 10000;
            long sequence;
            internal int TrackCalls;
            internal Fixture()
            {
                Ledger = new SnapLayoutReceiptLedger(Api); Policy = new SnapCorrectionPolicy(delegate { return Now; }, 1000);
                SnapCorrectionArea area;
                if (!SnapCorrectionArea.TryCapture(new AreaRecord { Identity = "neutral-retirement-monitor",
                    Monitor = R(0, 0, 800, 600), Original = R(0, 0, 800, 560), Applied = R(0, 0, 800, 600) }, out area))
                    throw new InvalidOperationException("Invalid injected area.");
                Max = Candidate(1, area, true, 800); Half = Candidate(1, area, false, 400);
                Wide = Candidate(1, area, false, 600); Other = Candidate(2, area, true, 800);
                Api.Windows.Add(Max.Target, Observation(Max)); Api.Windows.Add(Other.Target, Observation(Other)); Api.Current = Max;
            }
            static SnapLayoutCandidate Candidate(long handle, SnapCorrectionArea area, bool max, int width)
            {
                var candidate = new SnapLayoutCandidate { Target = new SnapCorrectionTarget { Window = new IntPtr(handle), Process = 10, Thread = 11, ProcessStarted = 12 },
                    Area = area, Maximized = max, Before = R(-8, -8, width + 8, 568), BeforeVisible = R(0, 0, width, 560),
                    Desired = R(-8, -8, width + 8, 608), DesiredVisible = R(0, 0, width, 600) };
                if (!candidate.Valid) throw new InvalidOperationException("Invalid injected candidate."); return candidate;
            }
            static SnapLayoutObservation Observation(SnapLayoutCandidate candidate)
            { return new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = candidate.Target,
                Outer = candidate.Before, Visible = candidate.BeforeVisible, Maximized = candidate.Maximized, Arranged = !candidate.Maximized }; }
            internal SnapLayoutObservation Window { get { return Api.Windows[Max.Target]; } }
            internal bool Track(SnapLayoutCandidate candidate, bool replace)
            { TrackCalls++; return tracked.Track(candidate, replace); }
            internal bool Admit(SnapLayoutCandidate candidate, out SnapLayoutReceiptLedger.Ticket ticket)
            { return SnapWorkAreas.TryPrepareOwnedLayout(Policy, Ledger, candidate, Track, out ticket); }
            internal void Changed(SnapLayoutCandidate candidate)
            {
                Window.Kind = SnapLayoutReadKind.Current; Window.Target = candidate.Target;
                Window.Outer = candidate.Before; Window.Visible = candidate.BeforeVisible;
                Window.Maximized = candidate.Maximized; Window.Arranged = !candidate.Maximized; Window.Hidden = Window.Iconic = Window.Ineligible = false;
                Api.Current = candidate;
            }
            internal SnapLayoutReceiptLedger.Ticket Invoke(SnapLayoutCandidate candidate)
            {
                SnapLayoutReceiptLedger.Ticket ticket;
                if (!Admit(candidate, out ticket) || !Policy.TryConsume(candidate.Target, candidate.Area) || !Ledger.MarkInvoked(ticket))
                    throw new InvalidOperationException("Injected pipeline unexpectedly refused.");
                return ticket;
            }
            internal void Desired(SnapLayoutCandidate candidate)
            { Api.Windows[candidate.Target].Outer = candidate.Desired; Api.Windows[candidate.Target].Visible = candidate.DesiredVisible; }
            internal SnapLayoutReceiptLedger.Ticket Confirm(SnapLayoutCandidate candidate)
            { var ticket = Invoke(candidate); Desired(candidate); Ledger.RecordReturned(ticket, true); return ticket; }
            internal void NextWake()
            { Now += 10000; if (!Policy.BeginWakeEpoch(++sequence)) throw new InvalidOperationException("Injected wake refused."); Now += 5000; }
        }
        static Native.Rect R(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        static int checks;
        static void Check(bool okay, string message) { if (!okay) throw new InvalidOperationException(message); checks++; }
        internal static void Run()
        {
            checks = 0;
            {
                var f = new Fixture(); var old = f.Confirm(f.Max); f.Changed(f.Half);
                SnapLayoutReceiptLedger.Ticket denied;
                Check(!f.Admit(f.Half, out denied) && f.Api.Removes == 0 && f.TrackCalls == 1 && old.Receipt.Confirmed,
                    "An already-spent same-wake pair cannot retire its old receipt or enter native Track.");
                f.NextWake(); SnapLayoutReceiptLedger.Ticket current = null;
                Check(f.Policy.CanAttempt(f.Half.Target, f.Half.Area) && f.Admit(f.Half, out current),
                    "A genuine later wake admits the changed arranged candidate through the actual retire-before-Track pipeline.");
                Check(old.Receipt.Resolved && current.Receipt.Nonce > old.Receipt.Nonce && f.Window.Nonce == current.Receipt.Nonce &&
                    f.Api.Removes == 1 && f.Api.Bounds == 0 && f.Api.AreaReads == 0 && f.Ledger.Count == 2,
                    "Positive changed-shape retirement removes only the old marker, with no inverse or native-bar readiness reads.");
                Check(f.Policy.TryConsume(f.Half.Target, f.Half.Area) && f.Ledger.MarkInvoked(current),
                    "The new receipt still requires the ordinary per-wake budget and invocation proof.");
                f.Desired(f.Half); f.Ledger.RecordReturned(current, true);
                f.Ledger.RecordReturned(old, false); f.Ledger.RecordUnknown(old); f.Ledger.CancelUnissued(old);
                Check(!f.Ledger.MarkInvoked(old) && current.Receipt.Returned && current.Receipt.Accepted && current.Receipt.Confirmed &&
                    f.Window.Nonce == current.Receipt.Nonce && f.Api.Removes == 1,
                    "Old return/cancel/invoke tickets cannot touch a replaced receipt or its fresh marker.");
                f.Changed(f.Wide); f.NextWake();
                Check(f.Admit(f.Wide, out current) && f.Api.Removes == 2 && f.Ledger.Count == 3 && f.Api.Bounds == 0,
                    "A later legitimate arranged-width change can also replace the immutable candidate without inverse geometry.");
            }
            {
                var f = new Fixture(); var pending = f.Invoke(f.Max); f.Desired(f.Max); f.Changed(f.Half); f.NextWake();
                SnapLayoutReceiptLedger.Ticket denied;
                Check(!f.Admit(f.Half, out denied) && !pending.Receipt.Returned && !pending.Receipt.Resolved && f.Api.Removes == 0,
                    "An executing old native forward retains ownership even when a new wake and different candidate are eligible.");
                f = new Fixture(); var unknown = f.Invoke(f.Max); f.Desired(f.Max); f.Ledger.RecordUnknown(unknown); f.Changed(f.Half); f.NextWake();
                Check(unknown.Receipt.Confirmed && !unknown.Receipt.ResultKnown && !f.Admit(f.Half, out denied) && f.Api.Removes == 0,
                    "A positively observed shape cannot retire an unknown old native outcome under the narrow contract.");
                f = new Fixture(); var ignored = f.Invoke(f.Max); f.Ledger.RecordReturned(ignored, true); f.Changed(f.Half); f.NextWake();
                Check(!ignored.Receipt.Confirmed && !f.Admit(f.Half, out denied) && f.Api.Removes == 0,
                    "An accepted old forward without its own confirmed readback cannot be retired by a new shape.");
            }
            {
                var f = new Fixture(); var old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake();
                f.Window.Nonce = 900; SnapLayoutReceiptLedger.Ticket denied;
                Check(!f.Admit(f.Half, out denied) && f.Window.Nonce == 900 && f.Api.Removes == 0 && !old.Receipt.Resolved,
                    "A foreign positive marker is not removal authority for changed-candidate retirement.");
                f = new Fixture(); old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake();
                var different = f.Window.Outer; different.Right++; f.Window.Outer = different;
                Check(!f.Admit(f.Half, out denied) && f.Api.Removes == 0 && !old.Receipt.Resolved,
                    "A changed window must exactly match the new candidate's fresh Before shape; a stale snapshot cannot retire it.");
                f = new Fixture(); old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake(); f.Api.UnknownReads = 1;
                Check(!f.Admit(f.Half, out denied) && f.Api.Removes == 0 && !old.Receipt.Resolved,
                    "Unreadable current-window evidence retains old receipt authority.");
                f = new Fixture(); old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake();
                var replacement = f.Window.Target; replacement.ProcessStarted++; f.Window.Target = replacement;
                Check(!f.Admit(f.Half, out denied) && f.Api.Removes == 0 && !old.Receipt.Resolved,
                    "A different live target lifetime cannot retire the old marker or replace its candidate.");
            }
            {
                var f = new Fixture(); var old = f.Confirm(f.Max); SnapLayoutReceiptLedger.Ticket other;
                if (!f.Admit(f.Other, out other)) throw new InvalidOperationException("Other unissued reservation fixture failed.");
                f.Changed(f.Half); f.NextWake(); bool nestedPrepared = true, nestedInvoked = true, nestedRetired = true, resolvedInside = true;
                int tracksBefore = f.TrackCalls, tracksInside = -1;
                f.Api.OnRemove = delegate {
                    SnapLayoutReceiptLedger.Ticket nested;
                    nestedPrepared = f.Admit(f.Half, out nested); nestedInvoked = f.Ledger.MarkInvoked(other);
                    nestedRetired = f.Ledger.TryRetireChanged(f.Half); resolvedInside = old.Receipt.Resolved; tracksInside = f.TrackCalls;
                };
                SnapLayoutReceiptLedger.Ticket current;
                Check(f.Admit(f.Half, out current) && !nestedPrepared && !nestedInvoked && !nestedRetired && !resolvedInside &&
                    tracksInside == tracksBefore && !other.Receipt.Invoked && f.Api.Removes == 1,
                    "Marker removal reentry cannot prepare/invoke another generation or call native Track before removal returns.");
                Check(current.Receipt.Nonce > old.Receipt.Nonce && f.Window.Nonce == current.Receipt.Nonce && f.Api.Bounds == 0,
                    "Only the outer completed retirement may install the replacement marker.");
            }
            {
                var f = new Fixture(); var old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake();
                f.Api.OnRemove = delegate { f.Api.UnknownReads = 1; }; SnapLayoutReceiptLedger.Ticket current;
                Check(!f.Admit(f.Half, out current) && !old.Receipt.Resolved && old.Receipt.ReleaseReturned && f.Window.Nonce == 0 && f.Api.Removes == 1,
                    "Successful removal with unreadable post-read cannot yet grant new candidate authority.");
                Check(f.Admit(f.Half, out current) && old.Receipt.Resolved && f.Api.Removes == 1 && f.Window.Nonce == current.Receipt.Nonce,
                    "Later exact absent-marker readback confirms retirement without repeating removal.");
            }
            foreach (int failure in new[] { 0, 1, 2 }) {
                var f = new Fixture(); var old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake();
                if (failure == 0) f.Api.RemoveAccepted = false; // May have taken effect, but return is not confirmed.
                if (failure == 1) f.Api.OnRemove = delegate { throw new InvalidOperationException("Injected removal outcome unknown."); };
                if (failure == 2) f.Api.RemoveEffect = false; // Accepted but marker still present.
                SnapLayoutReceiptLedger.Ticket denied;
                for (int i = 0; i < 1000; i++) if (f.Admit(f.Half, out denied)) throw new InvalidOperationException("Unconfirmed retirement admitted a replacement.");
                Check(f.Api.Removes == 1 && !old.Receipt.Resolved && f.Ledger.Count == 1 && f.Api.Bounds == 0,
                    "Failed, thrown or ignored removal retains authority and cannot be reissued by candidate polling.");
            }
            {
                var f = new Fixture(); var old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake();
                f.Api.OnRead = delegate { f.Ledger.RecordUnknown(old); }; SnapLayoutReceiptLedger.Ticket denied;
                Check(!f.Admit(f.Half, out denied) && f.Api.Removes == 0 && !old.Receipt.ResultKnown && !old.Receipt.Resolved,
                    "A reentrant unknown latest return invalidates the retirement snapshot before any property removal.");
                f = new Fixture(); old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake();
                f.Api.OnRead = delegate { f.Ledger.BeginRestore(new[] { f.Max.Area }); };
                Check(!f.Admit(f.Half, out denied) && f.Api.Removes == 0 && f.Ledger.Restoring && !old.Receipt.Resolved,
                    "A reentrant restoration fence vetoes retirement before marker mutation.");
            }
            {
                var f = new Fixture(); var old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake();
                old.Receipt.InverseAttempted = old.Receipt.InverseReturned = true;
                SnapLayoutReceiptLedger.Ticket denied;
                Check(!f.Admit(f.Half, out denied) && f.Api.Removes == 0 && !old.Receipt.Resolved,
                    "An outstanding inverse remains owned and cannot be converted into changed-forward retirement.");
            }
            {
                var f = new Fixture(); var old = f.Confirm(f.Max); f.Changed(f.Max); f.Window.Nonce = 0; f.NextWake();
                SnapLayoutReceiptLedger.Ticket current;
                Check(f.Admit(f.Max, out current) && old.Receipt.Resolved && !Object.ReferenceEquals(old.Receipt, current.Receipt) &&
                    current.Receipt.Nonce > old.Receipt.Nonce && f.Window.Nonce == current.Receipt.Nonce && f.Api.Removes == 0,
                    "Known completed same-candidate marker absence yields its old claim and requires a fresh nonce without removing foreign state.");
                f.Ledger.RecordReturned(old, false); f.Ledger.RecordUnknown(old);
                Check(!f.Ledger.MarkInvoked(old) && !current.Receipt.Invoked && !current.Receipt.Returned && f.Window.Nonce == current.Receipt.Nonce,
                    "Missing-marker replacement also fences stale tickets with the same numerical generation.");
                f = new Fixture(); old = f.Confirm(f.Max); f.Changed(f.Max); f.NextWake();
                Check(f.Admit(f.Max, out current) && Object.ReferenceEquals(old.Receipt, current.Receipt) && current.Generation > old.Generation &&
                    f.Ledger.Count == 1 && f.Api.Removes == 0 && f.Api.Marks == 1,
                    "Ordinary exact same-candidate reuse retains its established marker and generation path rather than allocating a replacement.");
            }
            {
                var f = new Fixture(); var old = f.Confirm(f.Max); f.Changed(f.Half); f.NextWake();
                int beforeReads = f.Api.Reads; f.Api.UnknownReads = 10000;
                SnapLayoutReceiptLedger.Ticket denied;
                for (int i = 0; i < 1000; i++) if (f.Admit(f.Half, out denied)) throw new InvalidOperationException("Unknown retirement unexpectedly admitted a candidate.");
                Check(f.Api.Reads - beforeReads == SnapLayoutReceiptLedger.MaximumRetirementProbes && f.Api.Removes == 0 &&
                    !old.Receipt.Resolved && old.Receipt.RetirementProbes == SnapLayoutReceiptLedger.MaximumRetirementProbes,
                    "A stable unresolved candidate cannot produce unbounded native retirement reads inside one correction epoch.");
                f.Api.UnknownReads = 0;
                Check(!f.Policy.BeginWakeEpoch(f.Policy.WakeEpoch) && !f.Admit(f.Half, out denied) &&
                    !f.Ledger.TryRetireChanged(f.Half, f.Policy.WakeEpoch - 1) && f.Api.Reads - beforeReads == SnapLayoutReceiptLedger.MaximumRetirementProbes,
                    "Same/stale epochs and newly readable state cannot replenish an exhausted retirement probe quota.");
                f.NextWake();
                Check(f.Admit(f.Half, out denied) && f.Api.Removes == 1 && old.Receipt.Resolved && f.Ledger.Count == 2,
                    "Only an already accepted newer correction epoch can renew bounded retirement observation.");
            }
            {
                var f = new Fixture(); bool nestedPrepared = true;
                f.Api.OnRead = delegate {
                    f.Changed(f.Half); f.NextWake(); SnapLayoutReceiptLedger.Ticket nested;
                    nestedPrepared = f.Admit(f.Half, out nested);
                };
                SnapLayoutReceiptLedger.Ticket outer;
                Check(!f.Admit(f.Max, out outer) && !nestedPrepared && f.Ledger.PendingCount == 1 && f.Api.Removes == 0 && f.Api.Bounds == 0,
                    "Changed-candidate reentry cannot retire or replace a still-preparing old reservation.");
            }
            {
                var f = new Fixture(); var current = f.Confirm(f.Max); SnapLayoutCandidate next = f.Half;
                for (int i = 1; i < SnapLayoutReceiptLedger.MaximumReceipts; i++) {
                    f.Changed(next); f.NextWake(); var replacement = f.Confirm(next);
                    if (replacement.Receipt.Nonce <= current.Receipt.Nonce) throw new InvalidOperationException("Retirement reused a nonce.");
                    current = replacement; next = Object.ReferenceEquals(next, f.Half) ? f.Max : f.Half;
                }
                f.Changed(next); f.NextWake(); SnapLayoutReceiptLedger.Ticket denied;
                Check(!f.Admit(next, out denied) && f.Ledger.Count == 128 && f.Api.Marks == 128 && f.Api.Bounds == 0,
                    "Fresh verified wakes and completed changed-shape retirements never reset the cumulative128 receipt cap.");
            }
            Console.WriteLine("PASS: " + checks + " independent changed-candidate retirement checks; injected/managed only.");
        }
        internal static int RunPureTest()
        { try { Run(); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
    }
}
