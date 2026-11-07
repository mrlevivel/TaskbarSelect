using System;
using System.Collections.Generic;

namespace TaskbarSelect
{
    // Independent transition regressions. The backend is entirely injected;
    // this file never reads HWNDs, processes, profiles, files or desktop state.
    internal static class SnapLayoutTransitionAdversarialTests
    {
        sealed class FakeApi : ISnapLayoutApi
        {
            internal readonly Dictionary<SnapCorrectionTarget, SnapLayoutObservation> Windows = new Dictionary<SnapCorrectionTarget, SnapLayoutObservation>();
            internal readonly Dictionary<SnapCorrectionTarget, SnapLayoutCandidate> Candidates = new Dictionary<SnapCorrectionTarget, SnapLayoutCandidate>();
            internal readonly Dictionary<SnapCorrectionTarget, int> BoundsCalls = new Dictionary<SnapCorrectionTarget, int>();
            internal readonly Dictionary<SnapCorrectionTarget, int> RemoveCalls = new Dictionary<SnapCorrectionTarget, int>();
            internal readonly Dictionary<SnapCorrectionTarget, int> AreaCalls = new Dictionary<SnapCorrectionTarget, int>();
            internal readonly Dictionary<SnapCorrectionArea, SnapLayoutAreaState> Areas = new Dictionary<SnapCorrectionArea, SnapLayoutAreaState>();
            internal readonly HashSet<SnapCorrectionTarget> IgnoreBounds = new HashSet<SnapCorrectionTarget>();
            internal Action OnRead, OnMark, OnBounds;
            static void Once(ref Action action) { var value = action; action = null; if (value != null) value(); }
            static void Increment(Dictionary<SnapCorrectionTarget, int> values, SnapCorrectionTarget target)
            { int count; values.TryGetValue(target, out count); values[target] = count + 1; }
            internal static int Count(Dictionary<SnapCorrectionTarget, int> values, SnapCorrectionTarget target)
            { int count; return values.TryGetValue(target, out count) ? count : 0; }
            public SnapLayoutObservation Read(SnapCorrectionTarget target)
            {
                SnapLayoutObservation value;
                var result = Windows.TryGetValue(target, out value) ? value.Copy() : new SnapLayoutObservation { Kind = SnapLayoutReadKind.Gone };
                Once(ref OnRead); return result;
            }
            public bool SetNonce(SnapCorrectionTarget target, long nonce)
            { Windows[target].Nonce = nonce; Once(ref OnMark); return true; }
            public bool RemoveNonce(SnapCorrectionTarget target, long nonce)
            {
                Increment(RemoveCalls, target);
                if (Windows[target].Nonce != nonce) return false;
                Windows[target].Nonce = 0; return true;
            }
            public SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area)
            {
                Increment(AreaCalls, target); SnapLayoutAreaState result;
                return Areas.TryGetValue(area, out result) ? result : SnapLayoutAreaState.Unknown;
            }
            public bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> beforeNativeSet)
            {
                if (!beforeNativeSet()) return false;
                Increment(BoundsCalls, target);
                if (!IgnoreBounds.Contains(target)) {
                    Windows[target].Outer = outer; Windows[target].Visible = Candidates[target].BeforeVisible;
                }
                Once(ref OnBounds); return true;
            }
        }
        sealed class Fixture
        {
            internal readonly FakeApi Api = new FakeApi();
            internal readonly SnapLayoutReceiptLedger Ledger;
            internal readonly SnapLayoutCandidate A, B;
            internal Fixture(bool maximized = true)
            { Ledger = new SnapLayoutReceiptLedger(Api); A = Add(1, 0, maximized); B = Add(2, 800, !maximized); }
            SnapLayoutCandidate Add(long handle, int left, bool maximized)
            {
                SnapCorrectionArea area;
                if (!SnapCorrectionArea.TryCapture(new AreaRecord { Identity = "neutral-transition-" + handle,
                    Monitor = Rect(left, 0, left + 800, 600), Original = Rect(left, 0, left + 800, 560), Applied = Rect(left, 0, left + 800, 600) }, out area))
                    throw new InvalidOperationException("Invalid injected area fixture.");
                var candidate = new SnapLayoutCandidate { Target = new SnapCorrectionTarget { Window = new IntPtr(handle), Process = 10, Thread = 11, ProcessStarted = 12 },
                    Area = area, Maximized = maximized,
                    Before = Rect(left - 8, -8, left + (maximized ? 808 : 408), 568), BeforeVisible = Rect(left, 0, left + (maximized ? 800 : 400), 560),
                    Desired = Rect(left - 8, -8, left + (maximized ? 808 : 408), 608), DesiredVisible = Rect(left, 0, left + (maximized ? 800 : 400), 600) };
                if (!candidate.Valid) throw new InvalidOperationException("Invalid injected candidate fixture.");
                Api.Candidates.Add(candidate.Target, candidate);
                Api.Windows.Add(candidate.Target, new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = candidate.Target,
                    Outer = candidate.Before, Visible = candidate.BeforeVisible, Maximized = maximized, Arranged = !maximized });
                Api.Areas.Add(area, SnapLayoutAreaState.OriginalReady); return candidate;
            }
            internal SnapLayoutObservation Window(SnapLayoutCandidate c) { return Api.Windows[c.Target]; }
            internal void Before(SnapLayoutCandidate c) { Window(c).Outer = c.Before; Window(c).Visible = c.BeforeVisible; }
            internal SnapLayoutReceiptLedger.Ticket Confirm(SnapLayoutCandidate c)
            {
                SnapLayoutReceiptLedger.Ticket ticket;
                if (!Ledger.TryPrepareForward(c, out ticket) || !Ledger.MarkInvoked(ticket)) throw new InvalidOperationException("Injected forward unexpectedly refused.");
                Window(c).Outer = c.Desired; Window(c).Visible = c.DesiredVisible; Ledger.RecordReturned(ticket, true); return ticket;
            }
        }
        static Native.Rect Rect(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        static int checks;
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        internal static void Run()
        {
            checks = 0;
            foreach (bool maximized in new[] { true, false }) {
                var f = new Fixture(maximized); f.Confirm(f.A); f.Confirm(f.B); long nonceB = f.Window(f.B).Nonce;
                f.Api.Areas[f.B.Area] = SnapLayoutAreaState.Unknown;
                Check(f.Ledger.BeginRestore(new[] { f.A.Area }), "One physical-area restore starts without closing the entire ledger.");
                SnapLayoutReceiptLedger.Ticket denied;
                Check(!f.Ledger.TryPrepareForward(f.B, out denied) && !f.Ledger.Closing,
                    "A transition fences all forward admissions until its restoration completes.");
                Check(f.Ledger.TryFinishRestore(true) && f.Ledger.PendingCount == 1 && f.Window(f.A).Nonce == 0 &&
                    f.Window(f.B).Nonce == nonceB && AreaPolicy.Equal(f.Window(f.B).Outer, f.B.Desired) &&
                    FakeApi.Count(f.Api.BoundsCalls, f.B.Target) == 0 && FakeApi.Count(f.Api.RemoveCalls, f.B.Target) == 0 && FakeApi.Count(f.Api.AreaCalls, f.B.Target) == 0,
                    "Selected cleanup neither reads readiness nor moves/releases an unrelated still-hidden area's receipt.");
                Check(f.Ledger.EndRestore() && !f.Ledger.Restoring && !f.Ledger.Closing,
                    "Resolved selected receipts allow resumption while retaining another area's ownership.");
            }
            {
                var f = new Fixture(); f.Confirm(f.A); f.Confirm(f.B); f.Api.IgnoreBounds.Add(f.A.Target);
                Check(f.Ledger.BeginRestore(new[] { f.A.Area }) && !f.Ledger.TryFinishRestore(true) && !f.Ledger.EndRestore(),
                    "An accepted but unobserved inverse retains the active transition and its forward fence.");
                SnapLayoutReceiptLedger.Ticket denied;
                for (int i = 0; i < 1000; i++) {
                    if (f.Ledger.TryFinishRestore(true) || f.Ledger.EndRestore() || f.Ledger.TryPrepareForward(f.A, out denied))
                        throw new InvalidOperationException("Pending inverse escaped its transition fence.");
                }
                Check(FakeApi.Count(f.Api.BoundsCalls, f.A.Target) == 1 && FakeApi.Count(f.Api.BoundsCalls, f.B.Target) == 0,
                    "Repeated settling/resume attempts cannot reissue inverse geometry or mutate another area.");
                f.Before(f.A);
                Check(f.Ledger.TryFinishRestore(false) && f.Ledger.EndRestore() && f.Window(f.A).Nonce == 0,
                    "Later exact inverse readback resolves with no new geometry allowance.");
            }
            {
                var f = new Fixture(); f.Confirm(f.A); f.Confirm(f.B); f.Api.IgnoreBounds.Add(f.A.Target);
                f.Ledger.BeginRestore(new[] { f.A.Area }); f.Ledger.TryFinishRestore(true); f.Ledger.BeginExit();
                Check(!f.Ledger.TryFinishExit(true) && FakeApi.Count(f.Api.BoundsCalls, f.A.Target) == 1 &&
                    FakeApi.Count(f.Api.BoundsCalls, f.B.Target) == 1 && f.Ledger.Closing && !f.Ledger.EndRestore(),
                    "Exit promotes pending cleanup to all receipts without reissuing the selected inverse.");
                f.Before(f.A);
                Check(f.Ledger.TryFinishExit(false) && f.Ledger.PendingCount == 0 && !f.Ledger.BeginRestore(new[] { f.A.Area }),
                    "Promoted exit confirms delayed selected readback and never reopens forward intake.");
            }
            {
                var f = new Fixture(); var old = f.Confirm(f.A);
                f.Ledger.BeginRestore(new[] { f.A.Area }); f.Ledger.TryFinishRestore(true); f.Ledger.EndRestore();
                long oldNonce = old.Receipt.Nonce; var current = f.Confirm(f.A);
                Check(current.Receipt.Nonce > oldNonce && f.Ledger.Count == 2 && !Object.ReferenceEquals(current.Receipt, old.Receipt),
                    "Confirmed marker retirement permits a fresh receipt with a new monotonic nonce and allocation.");
                f.Ledger.RecordReturned(old, false); f.Ledger.RecordUnknown(old); f.Ledger.CancelUnissued(old);
                Check(!f.Ledger.MarkInvoked(old) && current.Receipt.Returned && current.Receipt.Accepted && current.Receipt.Confirmed &&
                    f.Window(f.A).Nonce == current.Receipt.Nonce,
                    "Old tickets cannot alter a fresh same-window generation even when its generation number matches.");
                f.Ledger.BeginRestore(new[] { f.A.Area }); f.Ledger.TryFinishRestore(true); f.Ledger.EndRestore();
                for (int i = 2; i < SnapLayoutReceiptLedger.MaximumReceipts; i++) {
                    var next = f.Confirm(f.A);
                    if (next.Receipt.Nonce != i + 1 || !f.Ledger.BeginRestore(new[] { f.A.Area }) ||
                        !f.Ledger.TryFinishRestore(true) || !f.Ledger.EndRestore()) throw new InvalidOperationException("Repeated retirement fixture failed.");
                }
                SnapLayoutReceiptLedger.Ticket denied;
                Check(f.Ledger.Count == 128 && f.Ledger.PendingCount == 0 && !f.Ledger.TryPrepareForward(f.A, out denied),
                    "Retired receipt replacement never replenishes the controller's cumulative lifetime allocation cap.");
            }
            {
                var f = new Fixture(); bool nestedClean = true, nestedEnded = true;
                f.Api.OnMark = delegate {
                    f.Ledger.BeginRestore(new[] { f.A.Area });
                    nestedClean = f.Ledger.TryFinishRestore(true); nestedEnded = f.Ledger.EndRestore();
                };
                SnapLayoutReceiptLedger.Ticket denied;
                Check(!f.Ledger.TryPrepareForward(f.A, out denied) && denied == null && !nestedClean && !nestedEnded && f.Window(f.A).Nonce != 0,
                    "Restore reentry while marking blocks the stale forward and retains its in-flight reservation.");
                Check(f.Ledger.TryFinishRestore(true) && f.Ledger.EndRestore() && FakeApi.Count(f.Api.BoundsCalls, f.A.Target) == 0,
                    "A vetoed marked preparation releases its nonce after return without forward or inverse geometry.");
            }
            {
                var f = new Fixture(); SnapLayoutReceiptLedger.Ticket prepared;
                if (!f.Ledger.TryPrepareForward(f.A, out prepared)) throw new InvalidOperationException("Preparation fixture failed.");
                f.Api.OnRead = delegate { f.Ledger.BeginRestore(new[] { f.A.Area }); };
                Check(!f.Ledger.MarkInvoked(prepared) && !prepared.Receipt.Invoked,
                    "Restore reentry during final forward read vetoes invocation using the earlier snapshot.");
                Check(f.Ledger.TryFinishRestore(true) && f.Ledger.EndRestore() && FakeApi.Count(f.Api.BoundsCalls, f.A.Target) == 0,
                    "The unissued reservation can complete its scoped cleanup without a geometry write.");
            }
            {
                var f = new Fixture(); f.Confirm(f.A); f.Ledger.BeginRestore(new[] { f.A.Area });
                bool nestedClean = true, nestedEnded = true;
                f.Api.OnBounds = delegate { nestedClean = f.Ledger.TryFinishRestore(true); nestedEnded = f.Ledger.EndRestore(); };
                Check(f.Ledger.TryFinishRestore(true) && !nestedClean && !nestedEnded && FakeApi.Count(f.Api.BoundsCalls, f.A.Target) == 1 && f.Ledger.EndRestore(),
                    "Reentrant completion cannot cross the executing inverse's return boundary or prematurely unblock intake.");
            }
            {
                var f = new Fixture(); f.Confirm(f.A); f.Confirm(f.B); f.Api.Areas[f.A.Area] = SnapLayoutAreaState.Unknown;
                f.Ledger.BeginRestore(new[] { f.A.Area });
                Check(!f.Ledger.TryFinishRestore(true) && !f.Ledger.EndRestore() && FakeApi.Count(f.Api.BoundsCalls, f.A.Target) == 0,
                    "Unreadable selected-area readiness defers both inversion and resumption.");
                f.Api.Areas[f.A.Area] = SnapLayoutAreaState.OriginalReady;
                Check(f.Ledger.TryFinishRestore(true) && f.Ledger.EndRestore() && FakeApi.Count(f.Api.BoundsCalls, f.A.Target) == 1,
                    "Later positive selected-area readiness can grant the sole still-unused inverse.");
            }
            {
                // No ledger receipt exists here: a queued taskbar Show still
                // needs positive area/bar readback before intake can resume.
                var f = new Fixture(); int quiet = 0, recovery = 0, reads = 0, finish = 0, resume = 0;
                var state = SnapLayoutAreaState.Unknown;
                var policy = new SnapLayoutRestorePolicy(new[] { f.A.Area }, delegate { quiet++; }, delegate { recovery++; },
                    delegate(SnapCorrectionArea area) { reads++; return state; }, delegate { finish++; return true; }, delegate { resume++; return true; });
                Check(!policy.TryComplete(true) && policy.Pending && finish == 0 && resume == 0,
                    "An empty receipt set cannot bypass unresolved taskbar Show or area readback.");
                for (int i = 0; i < 1000; i++) if (policy.TryComplete(false)) throw new InvalidOperationException("Unreadable area unexpectedly resumed.");
                Check(quiet == 1 && recovery == 1 && reads == SnapLayoutRestorePolicy.MaximumReadbacks && finish == 0 && resume == 0,
                    "Passive settling is bounded and never repeats explicit work-area or taskbar recovery.");
                state = SnapLayoutAreaState.OriginalReady;
                Check(!policy.TryComplete(false) && recovery == 1 && resume == 0,
                    "Exhausted passive sampling remains paused instead of silently rearming its budget.");
                Check(policy.TryComplete(true) && !policy.Pending && recovery == 2 && finish == 1 && resume == 1,
                    "A fresh explicit recovery attempt can confirm completed Show and resume an empty ledger.");
                Check(policy.TryComplete(true) && recovery == 2 && finish == 1 && resume == 1,
                    "A completed transition cannot be replayed by another UI poll or explicit attempt.");
            }
            {
                var f = new Fixture(); bool nestedQuiet = true, nestedRecovery = true, nestedFinish = true;
                int recovery = 0, resumed = 0; SnapLayoutRestorePolicy policy = null;
                policy = new SnapLayoutRestorePolicy(new[] { f.A.Area },
                    delegate { nestedQuiet = policy.TryComplete(true); },
                    delegate { recovery++; nestedRecovery = policy.TryComplete(true); },
                    delegate(SnapCorrectionArea area) { return SnapLayoutAreaState.OriginalReady; },
                    delegate { nestedFinish = policy.TryComplete(true); return true; }, delegate { resumed++; return true; });
                Check(policy.TryComplete(true) && !nestedQuiet && !nestedRecovery && !nestedFinish && recovery == 1 && resumed == 1,
                    "Nested callbacks cannot duplicate quiesce, recovery or completion while the transition call is executing.");
            }
            Console.WriteLine("PASS: " + checks + " independent scoped-layout transition checks; no desktop or profile accessed.");
        }
        internal static int RunPureTest()
        {
            try { Run(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
