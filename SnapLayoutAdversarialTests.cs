using System;
using System.Collections.Generic;

namespace TaskbarSelect
{
    // Production receipt policy with an entirely injected backend. No HWND,
    // process, timer, desktop, native provider, profile or filesystem is read.
    internal static class SnapLayoutAdversarialTests
    {
        sealed class FakeApi : ISnapLayoutApi
        {
            internal readonly Dictionary<SnapCorrectionTarget, SnapLayoutObservation> Windows =
                new Dictionary<SnapCorrectionTarget, SnapLayoutObservation>();
            internal Action OnRead, OnSetNonce, OnRemoveNonce, OnArea, OnBounds, OnBoundsGuard;
            internal bool NonceAccepted = true, RemoveAccepted = true, BoundsAccepted = true, ApplyBounds = true, BoundsGuardAccepted = true;
            internal int NonceSets, NonceRemoves, BoundsSets;
            internal SnapLayoutAreaState Area = SnapLayoutAreaState.OriginalReady;
            internal SnapLayoutCandidate Current;
            static void Once(ref Action callback)
            { var action = callback; callback = null; if (action != null) action(); }
            public SnapLayoutObservation Read(SnapCorrectionTarget target)
            {
                SnapLayoutObservation observation;
                var result = Windows.TryGetValue(target, out observation) ? observation.Copy() :
                    new SnapLayoutObservation { Kind = SnapLayoutReadKind.Gone };
                // A native call may return a snapshot captured before a nested
                // callback. The policy must not grant authority to stale data.
                Once(ref OnRead); return result;
            }
            public bool SetNonce(SnapCorrectionTarget target, long nonce)
            {
                NonceSets++;
                if (NonceAccepted) Windows[target].Nonce = nonce;
                Once(ref OnSetNonce); return NonceAccepted;
            }
            public bool RemoveNonce(SnapCorrectionTarget target, long nonce)
            {
                NonceRemoves++;
                if (RemoveAccepted && Windows[target].Nonce == nonce) Windows[target].Nonce = 0;
                Once(ref OnRemoveNonce); return RemoveAccepted;
            }
            public SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area)
            { var result = Area; Once(ref OnArea); return result; }
            public bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> beforeNativeSet)
            {
                Once(ref OnBoundsGuard);
                if (!BoundsGuardAccepted || !beforeNativeSet()) return false;
                BoundsSets++;
                if (ApplyBounds) {
                    Windows[target].Outer = outer;
                    Windows[target].Visible = Current.BeforeVisible;
                }
                Once(ref OnBounds); return BoundsAccepted;
            }
        }
        sealed class Fixture
        {
            internal readonly FakeApi Api = new FakeApi();
            internal readonly SnapLayoutReceiptLedger Ledger;
            internal readonly SnapLayoutCandidate Candidate;
            internal Fixture(bool maximized = true)
            { Ledger = new SnapLayoutReceiptLedger(Api); Candidate = Add(1, maximized); Api.Current = Candidate; }
            internal SnapLayoutCandidate Add(long window, bool maximized = true)
            {
                var record = new AreaRecord { Identity = "adversarial-neutral-monitor",
                    Monitor = Rect(2560, 367, 4480, 1447), Original = Rect(2560, 367, 4480, 1399),
                    Applied = Rect(2560, 367, 4480, 1447) };
                SnapCorrectionArea area;
                if (!SnapCorrectionArea.TryCapture(record, out area)) throw new Exception("Invalid injected receipt area");
                var candidate = new SnapLayoutCandidate {
                    Target = new SnapCorrectionTarget { Window = new IntPtr(window), Process = 2, Thread = 3, ProcessStarted = 4 },
                    Area = area, Maximized = maximized,
                    Before = Rect(2552, 359, maximized ? 4488 : 3528, 1407),
                    BeforeVisible = Rect(2560, 367, maximized ? 4480 : 3520, 1399),
                    Desired = Rect(2552, 359, maximized ? 4488 : 3528, 1455),
                    DesiredVisible = Rect(2560, 367, maximized ? 4480 : 3520, 1447)
                };
                if (!candidate.Valid) throw new Exception("Invalid injected receipt candidate");
                Api.Windows.Add(candidate.Target, new SnapLayoutObservation {
                    Kind = SnapLayoutReadKind.Current, Target = candidate.Target,
                    Outer = candidate.Before, Visible = candidate.BeforeVisible,
                    Maximized = maximized, Arranged = !maximized
                });
                return candidate;
            }
            internal SnapLayoutObservation Window { get { return Api.Windows[Candidate.Target]; } }
            internal SnapLayoutReceiptLedger.Ticket Prepare()
            {
                SnapLayoutReceiptLedger.Ticket ticket;
                if (!Ledger.TryPrepareForward(Candidate, out ticket)) throw new Exception("Injected preparation unexpectedly denied");
                return ticket;
            }
            internal SnapLayoutReceiptLedger.Ticket Invoke()
            { var ticket = Prepare(); if (!Ledger.MarkInvoked(ticket)) throw new Exception("Injected invocation unexpectedly denied"); return ticket; }
            internal void Desired() { Window.Outer = Candidate.Desired; Window.Visible = Candidate.DesiredVisible; }
            internal void Before() { Window.Outer = Candidate.Before; Window.Visible = Candidate.BeforeVisible; }
            internal SnapLayoutReceiptLedger.Ticket Confirm()
            { var ticket = Invoke(); Desired(); Ledger.RecordReturned(ticket, true); return ticket; }
        }
        static Native.Rect Rect(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        static int checks;
        static void Check(bool condition, string name)
        { if (!condition) throw new Exception(name); checks++; }

        internal static void Run()
        {
            checks = 0;
            foreach (bool maximized in new[] { true, false }) {
                var f = new Fixture(maximized); var forward = f.Invoke(); f.Desired();
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0 && f.Ledger.PendingCount == 1,
                    "Desired readback during an executing forward cannot authorize inverse or exit");
                f.Ledger.RecordReturned(forward, true);
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1 && f.Window.Nonce == 0,
                    "Matching forward return followed by exact readback permits one inverse for either mode");

                f = new Fixture(maximized); f.Confirm(); bool nestedClean = true; int nestedPending = 0; long nestedNonce = 0;
                f.Api.OnBounds = delegate {
                    nestedClean = f.Ledger.TryFinishExit(true); nestedPending = f.Ledger.PendingCount; nestedNonce = f.Window.Nonce;
                };
                Check(f.Ledger.TryFinishExit(true) && !nestedClean && nestedPending == 1 && nestedNonce != 0 && f.Api.BoundsSets == 1,
                    "A reentrant exit seeing inverse Before cannot release authority until inverse native return");
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "An already completed cleanup remains idempotent on another explicit exit");
            }

            {
                var f = new Fixture(); var ticket = f.Invoke(); f.Api.ApplyBounds = false; f.Ledger.RecordReturned(ticket, true);
                for (int i = 0; i < 1000; i++) if (f.Ledger.TryFinishExit(true)) throw new Exception("Ignored accepted forward escaped pending state");
                Check(f.Ledger.PendingCount == 1 && f.Api.BoundsSets == 0 && f.Window.Nonce != 0,
                    "Accepted forward still at Before remains owned through 1000 polls without an unverified inverse");
                f.Desired(); f.Api.ApplyBounds = true;
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "A late delivered forward can be cleaned exactly once after actual Desired is observed");
            }
            {
                var f = new Fixture(); f.Confirm(); f.Api.ApplyBounds = false;
                for (int i = 0; i < 1000; i++) if (f.Ledger.TryFinishExit(true)) throw new Exception("Ignored inverse escaped pending state");
                Check(f.Api.BoundsSets == 1 && f.Ledger.PendingCount == 1,
                    "Accepted but ignored inverse stays pending with one native request across 1000 explicit retries");
                long nonce = f.Window.Nonce; f.Window.Nonce = 0; f.Before();
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1 && f.Ledger.PendingCount == 1,
                    "Same live identity with lost nonce cannot confirm an ambiguous inverse even at Before");
                f.Window.Nonce = nonce;
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "Later exact inverse proof with restored authority resolves without another native request");
            }
            {
                var f = new Fixture(); var ticket = f.Invoke(); f.Ledger.RecordReturned(ticket, true);
                long nonce = f.Window.Nonce; f.Window.Nonce = 0;
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0 && f.Ledger.PendingCount == 1,
                    "Missing nonce on the same live identity cannot retire a pending accepted forward");
                f.Desired();
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "Desired geometry without the nonce cannot borrow receipt ownership");
                f.Window.Nonce = nonce;
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "Restored nonce and exact late Desired permits one inverse");
            }
            {
                var f = new Fixture(); var old = f.Confirm(); f.Before(); var current = f.Invoke();
                Check(!current.Receipt.Confirmed && !current.Receipt.Returned,
                    "Reused immutable receipt resets latest-forward completion and confirmation");
                f.Ledger.RecordReturned(old, false); f.Ledger.RecordUnknown(old);
                Check(!current.Receipt.Returned && !current.Receipt.ResultKnown && !current.Receipt.Confirmed,
                    "Old return tokens cannot overwrite the latest generation");
                f.Ledger.RecordReturned(current, true);
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "An older confirmed wake cannot clean a newer ignored forward generation");
                f.Desired();
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "Latest generation alone supplies cleanup authority after its own observed application");
            }
            {
                var f = new Fixture(); var current = f.Invoke(); f.Desired(); f.Ledger.ObserveForward(); f.Before();
                SnapLayoutReceiptLedger.Ticket nested;
                Check(!f.Ledger.TryPrepareForward(f.Candidate, out nested) && !current.Receipt.Returned,
                    "Geometry observed before forward return cannot admit another generation on the executing stack");
                f.Ledger.RecordUnknown(current);
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "A thrown or unknown forward outcome remains pending at Before");
            }
            {
                long now = 10000;
                var budget = new SnapCorrectionPolicy(delegate { return now; }, 1000);
                var f = new Fixture(); long queuedEpoch = budget.WakeEpoch;
                f.Api.OnSetNonce = delegate { budget.BeginWakeEpoch(1); now += 5000; };
                var ticket = f.Prepare();
                Check(!budget.TryConsume(f.Candidate.Target, f.Candidate.Area, queuedEpoch),
                    "An epoch advancing inside reservation invalidates the pre-reservation budget token");
                f.Ledger.CancelUnissued(ticket);
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "An epoch-invalidated unissued reservation can be released without geometry mutation");
                now = 20000; budget = new SnapCorrectionPolicy(delegate { return now; }, 1000);
                f = new Fixture(); var executing = f.Invoke();
                Check(budget.TryConsume(f.Candidate.Target, f.Candidate.Area), "The startup budget admits the first simulated native call");
                budget.BeginWakeEpoch(1); now += 5000; SnapLayoutReceiptLedger.Ticket nested;
                Check(budget.CanAttempt(f.Candidate.Target, f.Candidate.Area) &&
                    !f.Ledger.TryPrepareForward(f.Candidate, out nested) && !executing.Receipt.Returned,
                    "A reentrant settled wake budget cannot erase an older native call still executing");
                f.Ledger.RecordReturned(executing, true);
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "Reentrant wake admission cannot retire the old accepted but unobserved forward");
            }
            {
                var f = new Fixture(); bool nestedPrepared = true;
                f.Api.OnSetNonce = delegate {
                    f.Api.OnRead = delegate {
                        SnapLayoutReceiptLedger.Ticket nested;
                        nestedPrepared = f.Ledger.TryPrepareForward(f.Candidate, out nested);
                        if (nestedPrepared) { f.Ledger.MarkInvoked(nested); f.Desired(); f.Ledger.RecordReturned(nested, true); }
                    };
                };
                var outer = f.Prepare();
                Check(!nestedPrepared && outer.Receipt.Generation == outer.Generation && !outer.Receipt.Invoked,
                    "Marked-read callback cannot prepare a second generation or overwrite a nested invocation");
                f.Ledger.CancelUnissued(outer);
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "Cancelling the sole unissued prepared receipt removes only its nonce");
            }
            {
                var f = new Fixture(); bool nestedFinished = true;
                f.Api.OnSetNonce = delegate { f.Ledger.BeginExit(); nestedFinished = f.Ledger.TryFinishExit(true); };
                SnapLayoutReceiptLedger.Ticket ticket;
                Check(!f.Ledger.TryPrepareForward(f.Candidate, out ticket) && ticket == null && f.Api.BoundsSets == 0,
                    "Exit reentry during nonce reservation vetoes forward admission");
                Check(!nestedFinished && f.Ledger.TryFinishExit(true) && f.Window.Nonce == 0,
                    "Preparing receipt remains owned during nested exit and can retire after preparation unwinds");
            }
            {
                var f = new Fixture(); var ticket = f.Prepare();
                f.Api.OnRead = delegate { f.Ledger.BeginExit(); f.Ledger.TryFinishExit(true); };
                Check(!f.Ledger.MarkInvoked(ticket) && f.Api.BoundsSets == 0 && f.Ledger.TryFinishExit(true),
                    "Exit reentry during final invocation read cannot issue a stale forward");
            }
            {
                var f = new Fixture(); f.Api.NonceAccepted = false; SnapLayoutReceiptLedger.Ticket ticket;
                Check(!f.Ledger.TryPrepareForward(f.Candidate, out ticket) && ticket == null && f.Api.BoundsSets == 0,
                    "Nonce reservation denial vetoes all forward mutation");
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "A denied unissued nonce reservation requires no inverse");
            }
            {
                var f = new Fixture(); f.Api.OnSetNonce = delegate { throw new InvalidOperationException("Injected reservation failure after effect"); };
                bool threw = false;
                try { f.Prepare(); } catch (InvalidOperationException) { threw = true; }
                Check(threw && f.Window.Nonce != 0 && f.Ledger.PendingCount == 1,
                    "Reservation failure after marker effect keeps its pre-mutation receipt authority");
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0 && f.Window.Nonce == 0,
                    "A thrown reservation can release its own marker without ever issuing forward or inverse geometry");
            }
            {
                var f = new Fixture(); f.Window.Nonce = 999; SnapLayoutReceiptLedger.Ticket ticket;
                Check(!f.Ledger.TryPrepareForward(f.Candidate, out ticket) && f.Api.NonceSets == 0,
                    "A foreign preexisting nonce is never overwritten by receipt admission");
            }
            {
                var f = new Fixture(); var ticket = f.Invoke(); f.Ledger.RecordReturned(ticket, false);
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "Rejected forward with matching Before can release without inverse");
                f = new Fixture(); ticket = f.Invoke(); f.Desired(); f.Ledger.RecordReturned(ticket, false);
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "Rejected BOOL with actual Desired readback still requires one verified inverse");
            }
            {
                var f = new Fixture(); f.Confirm(); var changed = f.Window.Outer; changed.Left += 20; f.Window.Outer = changed;
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0 && f.Window.Outer.Left == changed.Left,
                    "A positive user geometry change after confirmed forward is preserved without inverse");
                f = new Fixture(); f.Confirm(); f.Window.Maximized = false; f.Window.Arranged = true;
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "A positive user mode change before inverse is preserved without mutation");
                f = new Fixture(); f.Confirm(); f.Api.ApplyBounds = false; f.Ledger.TryFinishExit(true);
                f.Window.Maximized = false; f.Window.Arranged = true; f.Before();
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "Mode loss after an inverse was issued cannot masquerade as a safe pre-inverse user change");
            }
            {
                var f = new Fixture(); f.Confirm(); f.Window.Hidden = true;
                f.Window.Outer = f.Window.Visible = new Native.Rect();
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0 && f.Window.Nonce == 0,
                    "Positive hidden-to-tray change after confirmed forward releases its marker without reading geometry or moving the window");
                f = new Fixture(); var ticket = f.Invoke(); f.Ledger.RecordReturned(ticket, true); f.Window.Hidden = true;
                f.Window.Outer = f.Window.Visible = new Native.Rect();
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0 && f.Window.Nonce != 0,
                    "Hidden lifetime with an unobserved accepted forward remains pending instead of guessing the queued write finished");
                f = new Fixture(); ticket = f.Invoke(); f.Desired(); f.Window.Hidden = true;
                Check(!f.Ledger.TryFinishExit(true) && !ticket.Receipt.Returned && f.Api.BoundsSets == 0,
                    "Hiding during the executing native forward cannot cross its required return boundary");
                f = new Fixture(); f.Confirm(); f.Api.ApplyBounds = false; f.Ledger.TryFinishExit(true); f.Window.Hidden = true;
                f.Window.Outer = f.Window.Visible = new Native.Rect();
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1 && f.Window.Nonce != 0,
                    "Hidden target after an unconfirmed inverse stays pending with no second geometry request");
            }
            {
                var f = new Fixture(); f.Confirm(); f.Api.Area = SnapLayoutAreaState.Unknown;
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "Unknown area or native-bar readiness retains authority without an inverse");
                f.Api.Area = SnapLayoutAreaState.OriginalReady;
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "Fresh Original area and all-bars readiness can qualify a later explicit exit");
                f = new Fixture(); f.Confirm(); f.Api.OnArea = delegate { f.Api.Area = SnapLayoutAreaState.Unknown; };
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "Readiness changing after an earlier captured area result vetoes inverse commit");
            }
            {
                var f = new Fixture(); f.Confirm();
                f.Api.OnBoundsGuard = delegate {
                    var changed = f.Window.Outer; changed.Left += 20; f.Window.Outer = changed;
                    f.Api.BoundsGuardAccepted = false;
                };
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0 && f.Ledger.PendingCount == 1,
                    "Final adapter guard refusal after a user change does not invent a queued native inverse");
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0 && f.Window.Outer.Left == f.Candidate.Desired.Left + 20,
                    "Fresh positive user-change proof after zero native requests allows a clean skip without clobbering it");
            }
            {
                var f = new Fixture(); var ticket = f.Invoke(); f.Ledger.RecordReturned(ticket, true);
                f.Window.Kind = SnapLayoutReadKind.Unknown;
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "Unreadable metadata cannot prove a pending forward lifetime gone");
                f.Window.Kind = SnapLayoutReadKind.Gone;
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0,
                    "Definite old-lifetime destruction retires a queued-forward claim without touching a replacement");
                f = new Fixture(); f.Confirm(); f.Api.ApplyBounds = false; f.Ledger.TryFinishExit(true);
                f.Window.Kind = SnapLayoutReadKind.Unknown;
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "Unreadable metadata also retains an already-issued inverse");
                var replacement = f.Window.Target; replacement.ProcessStarted++; f.Window.Target = replacement;
                f.Window.Kind = SnapLayoutReadKind.Current;
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "A proved new process lifetime ends the old claim without a second inverse");
            }
            {
                var f = new Fixture(); var ticket = f.Invoke(); f.Ledger.RecordReturned(ticket, true);
                for (int i = 2; i <= SnapLayoutReceiptLedger.MaximumReceipts; i++) {
                    var c = f.Add(i); SnapLayoutReceiptLedger.Ticket t;
                    if (!f.Ledger.TryPrepareForward(c, out t) || !f.Ledger.MarkInvoked(t)) throw new Exception("Capacity fixture failed early");
                    f.Ledger.RecordReturned(t, true);
                }
                var overflow = f.Add(SnapLayoutReceiptLedger.MaximumReceipts + 1); SnapLayoutReceiptLedger.Ticket denied;
                Check(f.Ledger.Count == 128 && f.Ledger.PendingCount == 128 &&
                    !f.Ledger.TryPrepareForward(overflow, out denied) && f.Api.NonceSets == 128,
                    "Full non-evicted receipt ledger denies a new forward instead of discarding unresolved authority");
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 0 && f.Ledger.PendingCount == 128,
                    "Capacity pressure and exit polling cannot convert unobserved accepted forwards into cleanup success");
            }
            {
                var f = new Fixture(); f.Confirm(); f.Api.ApplyBounds = false; f.Api.BoundsAccepted = false;
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "Rejected inverse consumes its one mutation allowance and remains pending without proof");
                for (int i = 0; i < 1000; i++) f.Ledger.TryFinishExit(true);
                Check(f.Api.BoundsSets == 1 && f.Ledger.PendingCount == 1,
                    "Rejected inverse is never retried by later explicit exits");
            }
            {
                var f = new Fixture(); f.Confirm(); f.Api.ApplyBounds = false;
                f.Api.OnBounds = delegate { throw new InvalidOperationException("Injected inverse outcome unknown"); };
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "A thrown inverse outcome remains owned and spends the sole inverse request");
                for (int i = 0; i < 1000; i++) f.Ledger.TryFinishExit(true);
                Check(f.Api.BoundsSets == 1 && f.Ledger.PendingCount == 1,
                    "A thrown inverse cannot be retried by a later explicit exit");
                f.Before();
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1,
                    "Later exact Before readback can finish an unwound unknown inverse without repeating it");
            }
            {
                var f = new Fixture(); var ticket = f.Prepare(); f.Ledger.CancelUnissued(ticket);
                f.Api.RemoveAccepted = false;
                for (int i = 0; i < 1000; i++) if (f.Ledger.TryFinishExit(true)) throw new Exception("Rejected nonce removal escaped pending state");
                Check(f.Api.NonceRemoves == 1 && f.Api.BoundsSets == 0 && f.Ledger.PendingCount == 1,
                    "Nonce cleanup failure retains authority with one removal request across 1000 exit retries");
                f.Window.Nonce = 0;
                Check(f.Ledger.TryFinishExit(true) && f.Api.NonceRemoves == 1,
                    "A later confirmed nonce disappearance can resolve without repeating failed cleanup mutation");
            }
            {
                var f = new Fixture(); f.Confirm();
                f.Api.OnRemoveNonce = delegate {
                    f.Window.Kind = SnapLayoutReadKind.Unknown;
                    f.Api.OnRead = delegate { f.Window.Kind = SnapLayoutReadKind.Current; };
                };
                Check(!f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1 && f.Api.NonceRemoves == 1,
                    "Transient read failure after verified inverse and own nonce removal retains uncertain cleanup");
                Check(f.Ledger.TryFinishExit(true) && f.Api.BoundsSets == 1 && f.Api.NonceRemoves == 1,
                    "Later same-identity nonce-absent read confirms already-authorized release without another mutation");
            }
        }
        internal static int RunPureTest()
        {
            try {
                Run(); Console.WriteLine("PASS: " + checks + " adversarial receipt checks; injected state only, no native or runtime actions."); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
