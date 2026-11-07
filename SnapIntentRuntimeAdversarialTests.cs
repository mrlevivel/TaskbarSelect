using System;
using System.Collections.Generic;
using System.Reflection;

namespace TaskbarSelect
{
    // Independent native-free protocol tests. Every observation/property action
    // below uses this injected in-memory API; no HWND API or observer is loaded.
    internal static class SnapIntentRuntimeAdversarialTests
    {
        sealed class Api : ISnapLayoutApi
        {
            internal SnapLayoutObservation Window;
            internal int Reads, Marks, Removes;
            internal Action OnRead;
            public SnapLayoutObservation Read(SnapCorrectionTarget target)
            { Reads++; if (OnRead != null) OnRead(); return Window.Copy(); }
            public bool SetNonce(SnapCorrectionTarget target, long nonce)
            { Marks++; Window.Nonce = nonce; return true; }
            public bool RemoveNonce(SnapCorrectionTarget target, long nonce)
            { Removes++; if (Window.Nonce != nonce) return false; Window.Nonce = 0; return true; }
            public SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area)
            { throw new InvalidOperationException("No area provider belongs to this pure harness."); }
            public bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> guard)
            { throw new InvalidOperationException("No native mutation belongs to this pure harness."); }
        }
        sealed class F
        {
            internal readonly Api Api = new Api();
            internal readonly SnapLayoutReceiptLedger Ledger;
            internal readonly SnapLayoutCandidate Candidate;
            internal SnapLayoutReceiptLedger.Ticket Ticket;
            internal readonly SnapIntentPolicy Intent = new SnapIntentPolicy(1000, 1);
            internal readonly SnapChordPolicy Chord = new SnapChordPolicy(1000, 1, 1000, 1000, true);
            internal readonly SnapCorrectionPolicy Budget;
            internal long Now = 1000, Revision = 1;
            internal F()
            {
                SnapCorrectionArea area;
                if (!SnapCorrectionArea.TryCapture(new AreaRecord { Identity = "independent-owned-area", Monitor = R(2560, 367, 4480, 1447), Original = R(2560, 367, 4480, 1399), Applied = R(2560, 367, 4480, 1447) }, out area)) throw new Exception("Area fixture invalid.");
                Candidate = new SnapLayoutCandidate { Target = new SnapCorrectionTarget { Window = new IntPtr(41), Process = 42, Thread = 43, ProcessStarted = 44 }, Area = area,
                    Before = R(2553, 367, 3527, 1406), BeforeVisible = R(2560, 367, 3520, 1399),
                    Desired = R(2553, 367, 3527, 1454), DesiredVisible = R(2560, 367, 3520, 1447) };
                Before(Candidate); Ledger = new SnapLayoutReceiptLedger(Api);
                Budget = new SnapCorrectionPolicy(delegate { return Now; }, 1000) { IntentPolicy = Intent };
                if (!Candidate.Valid || !SnapWorkAreas.TryPrepareOwnedLayout(Budget, Ledger, Candidate, delegate { return true; }, out Ticket) ||
                    !Budget.TryConsume(Candidate.Target, area) || !Ledger.MarkInvoked(Ticket)) throw new Exception("Completed receipt fixture invalid.");
                Api.Window.Outer = Candidate.Desired; Api.Window.Visible = Candidate.DesiredVisible;
                Ledger.RecordReturned(Ticket, true);
            }
            internal void Before(SnapLayoutCandidate candidate)
            {
                long nonce = Api.Window == null ? 0 : Api.Window.Nonce;
                Api.Window = new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = candidate.Target, Nonce = nonce, Outer = candidate.Before, Visible = candidate.BeforeVisible, Arranged = true };
            }
            internal void Normal()
            { Api.Window.Outer = R(2600, 500, 3240, 980); Api.Window.Visible = R(2607, 500, 3233, 973); Api.Window.Arranged = false; }
            internal SnapLayoutReceiptLedger.IntentAnchor Anchor()
            { SnapLayoutReceiptLedger.IntentAnchor a; if (!Ledger.TryIntentAnchor(Candidate.Target, out a)) throw new Exception("Expected completed anchor."); return a; }
            internal SnapChordNotice Packet(int key, bool down, long at, SnapIntentProof proof)
            { Now = at; return Chord.Observe(new SnapChordPacket { Key = key, Down = down, Flags = down ? 0U : 0x80U, Window = Candidate.Target.Window, At = at, NativeTime = (uint)at, CurrentNativeTick = (uint)at, Generation = 1, Before = proof }); }
            internal SnapIntentReservation Reserve(SnapLayoutReceiptLedger.IntentAnchor a, SnapLayoutCandidate candidate, out SnapIntentProof old)
            {
                Normal(); SnapIntentProof normal;
                Packet(0x5B, true, 1011, null);
                if (!Ledger.TryIntentWitness(a, 0, 1, 1012, delegate { return true; }, out normal) || !normal.IsNormal) throw new Exception("Normal witness invalid.");
                Packet(0x25, true, 1020, normal); Packet(0x25, false, 1030, null);
                var completed = Packet(0x5B, false, 1040, null);
                if (!Intent.AdmitCompleted(completed, Revision, delegate(long rev) { return rev == Revision; })) throw new Exception("Physical completion refused.");
                Before(candidate); Now = 1041;
                if (!Ledger.TryIntentCandidate(a, candidate, 0, 1, Now, delegate { return true; }, out old)) throw new Exception("Candidate proof refused.");
                SnapIntentPermit permit; SnapIntentReservation reservation;
                if (!Intent.TryPermit(old, Now, out permit) || !Intent.TryReserve(permit, old, Now, out reservation)) throw new Exception("Reservation refused.");
                return reservation;
            }
            internal SnapIntentProof Prepare(SnapLayoutReceiptLedger.IntentAnchor a, SnapLayoutCandidate candidate, SnapIntentReservation reservation, SnapIntentProof old)
            {
                if (!SnapWorkAreas.TryPrepareOwnedLayout(Budget, Ledger, candidate, delegate { return true; }, reservation, old, delegate { return ++Now; }, out Ticket)) throw new Exception("Actual helper refused.");
                SnapIntentProof prepared;
                if (!Ledger.TryIntentPrepared(a, Ticket, candidate, 0, 1, ++Now, delegate { return true; }, out prepared)) throw new Exception("Actual prepared lineage refused.");
                return prepared;
            }
        }
        static Native.Rect R(int l, int t, int r, int b) { return new Native.Rect { Left = l, Top = t, Right = r, Bottom = b }; }
        static readonly List<string> failures = new List<string>();
        static int checks;
        static void Check(bool okay, string message) { checks++; if (!okay) throw new InvalidOperationException(message); }
        static void Case(string name, Action action) { try { action(); } catch (Exception error) { failures.Add(name + ": " + error.GetType().Name + " " + error.Message); } }
        static SnapChordNotice DirectPacket(SnapChordPolicy chord, int key, bool down, long at, SnapIntentProof proof, uint extra)
        { return chord.Observe(new SnapChordPacket { Key = key, Down = down, Flags = extra | (down ? 0U : 0x80U), Window = new IntPtr(1), At = at, NativeTime = (uint)at, CurrentNativeTick = (uint)at, Generation = 1, Before = proof }); }
        static SnapChordNotice DirectCycle(SnapChordPolicy chord, SnapIntentPolicyTests.Fixture fixture, long start, uint extra)
        {
            DirectPacket(chord, 0x5B, true, start, null, extra); DirectPacket(chord, 0x25, true, start + 10, fixture.Proof(true, start + 10), extra);
            DirectPacket(chord, 0x25, false, start + 20, null, extra); return DirectPacket(chord, 0x5B, false, start + 30, null, extra);
        }
        internal static int Run()
        {
            checks = 0; failures.Clear();
            Case("Actual same-receipt anchor to invocation", delegate {
                var f = new F(); var a = f.Anchor(); SnapIntentProof old; var r = f.Reserve(a, f.Candidate, out old); var p = f.Prepare(a, f.Candidate, r, old);
                Check(p.LineageVerified && !p.OldMarkerRetired && p.Nonce == a.Nonce && p.ReceiptGeneration == a.Generation + 1, "Same receipt must derive exact incremented generation.");
                SnapIntentProof stale;
                Check(!f.Ledger.TryIntentWitness(a, 0, 1, f.Now, delegate { return true; }, out stale), "Preparation invalidates the old witness anchor.");
                Check(!f.Intent.NativeAuthorizationStillCurrent(r, p, f.Now) && !f.Ledger.IntentInvocationCurrent(a, f.Ticket, f.Candidate), "Neither final fence is true before spending/invoking.");
                Check(f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, r, p, f.Now) && f.Ledger.MarkInvoked(f.Ticket), "Fresh actual lineage authorizes before invocation.");
                Check(f.Intent.NativeAuthorizationStillCurrent(r, p, f.Now) && f.Ledger.IntentInvocationCurrent(a, f.Ticket, f.Candidate), "Both nonspending invocation fences admit the current exact ticket.");
                Check(f.Budget.AttemptCount == 2 && f.Intent.NativeAuthorizationStillCurrent(r, p, f.Now) && f.Budget.AttemptCount == 2, "Final revalidation cannot spend another budget credit.");
                f.Api.Window.Outer = f.Candidate.Desired; f.Api.Window.Visible = f.Candidate.DesiredVisible; f.Ledger.RecordReturned(f.Ticket, true);
                Check(!f.Ledger.IntentInvocationCurrent(a, f.Ticket, f.Candidate), "A returned native invocation is never executable again.");
            });
            Case("Changed width derives actual retirement", delegate {
                var f = new F(); var a = f.Anchor(); var c = new SnapLayoutCandidate { Target = f.Candidate.Target, Area = f.Candidate.Area,
                    Before = R(2553, 367, 3727, 1406), BeforeVisible = R(2560, 367, 3720, 1399), Desired = R(2553, 367, 3727, 1454), DesiredVisible = R(2560, 367, 3720, 1447) };
                SnapIntentProof old; var r = f.Reserve(a, c, out old); var p = f.Prepare(a, c, r, old);
                Check(f.Api.Removes == 1 && p.OldMarkerRetired && p.LineageVerified && p.Nonce > a.Nonce && p.ReceiptGeneration == 1, "Replacement must be backed by actual successful removal/readback and new nonce.");
                Check(f.Budget.TryConsume(c.Target, c.Area, 0, r, p, f.Now) && f.Ledger.MarkInvoked(f.Ticket) && f.Ledger.IntentInvocationCurrent(a, f.Ticket, c), "Actual replacement lineage can fund one exact invocation.");
                Check(!f.Ledger.IntentInvocationCurrent(a, new SnapLayoutReceiptLedger.Ticket(a.Receipt), c), "Old receipt ticket cannot invoke a replacement.");
            });
            Case("Unknown completed result is no anchor", delegate {
                var f = new F(); f.Ticket.Receipt.ResultKnown = false; SnapLayoutReceiptLedger.IntentAnchor a;
                Check(!f.Ledger.TryIntentAnchor(f.Candidate.Target, out a), "Unknown result cannot become input authority even if confirmed geometry is visible.");
            });
            Case("Foreign/missing identity blocks new anchor", delegate {
                foreach (int mutation in new[] { 0, 1, 2, 3, 4, 5 }) {
                    var f = new F(); if (mutation == 0) f.Api.Window.Nonce++; if (mutation == 1) f.Api.Window.Nonce = 0;
                    if (mutation == 2) f.Api.Window.Target.ProcessStarted++; if (mutation == 3) f.Api.Window.Kind = SnapLayoutReadKind.Unknown;
                    if (mutation == 4) f.Api.Window.Maximized = true; if (mutation == 5) f.Api.Window.Hidden = true;
                    SnapLayoutReceiptLedger.IntentAnchor a; Check(!f.Ledger.TryIntentAnchor(f.Candidate.Target, out a), "Foreign identity, marker, unknown or hidden state must block anchor mutation=" + mutation);
                }
            });
            Case("Cross-owner anchor rejects before reads", delegate {
                var f = new F(); var other = new F(); var a = f.Anchor(); SnapIntentProof p; int reads = other.Api.Reads;
                Check(!other.Ledger.TryIntentWitness(a, 0, 1, 1010, delegate { return true; }, out p) && reads == other.Api.Reads, "An anchor from another ledger never reads or proves ownership.");
            });
            Case("Read/ready reentrancy fences mutation", delegate {
                var f = new F(); var a = f.Anchor(); f.Normal(); bool nested = true; SnapIntentProof p;
                f.Api.OnRead = delegate { SnapLayoutReceiptLedger.Ticket ticket; nested = f.Ledger.TryPrepareForward(f.Candidate, out ticket); };
                Check(f.Ledger.TryIntentWitness(a, 0, 1, 1010, delegate { return true; }, out p) && !nested, "Stepping prevents preparation reentry during a native witness read.");
                f.Api.OnRead = null; int calls = 0;
                Check(!f.Ledger.TryIntentWitness(a, 0, 1, 1011, delegate { if (++calls == 2) f.Ledger.BeginExit(); return true; }, out p), "Exit started in the final readiness callback invalidates witness.");
            });
            Case("MarkInvoked return is freshly checked", delegate {
                var f = new F(); var a = f.Anchor(); SnapIntentProof old; var r = f.Reserve(a, f.Candidate, out old); var p = f.Prepare(a, f.Candidate, r, old);
                Check(f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, r, p, f.Now), "Reservation authorizes before a fallible read.");
                f.Api.OnRead = delegate { f.Revision++; f.Api.OnRead = null; };
                Check(f.Ledger.MarkInvoked(f.Ticket) && !f.Intent.NativeAuthorizationStillCurrent(r, p, f.Now), "A cancellation revision changed during MarkInvoked forbids native call despite spent credit.");
                Check(f.Budget.AttemptCount == 2, "Cancelled final authorization has no budget refund.");
            });
            Case("Last fence rejects deadline after native read", delegate {
                var f = new F(); var a = f.Anchor(); SnapIntentProof old; var r = f.Reserve(a, f.Candidate, out old); var p = f.Prepare(a, f.Candidate, r, old);
                Check(f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, r, p, f.Now) && f.Ledger.MarkInvoked(f.Ticket), "Deadline fixture authorized once.");
                Check(!f.Intent.NativeAuthorizationStillCurrent(r, p, 4000), "The final nonspending fence rechecks expiration after native reads.");
            });
            Case("Abort reentry during final authorization", delegate {
                var f = new F(); var a = f.Anchor(); SnapIntentProof old; var r = f.Reserve(a, f.Candidate, out old); var p = f.Prepare(a, f.Candidate, r, old);
                typeof(SnapIntentPolicy).GetField("currentRevision", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Intent,
                    new Func<long, bool>(delegate { f.Intent.Abort(r); return true; }));
                Check(!f.Intent.Authorize(r, p, f.Now) && !r.NativeAuthorized, "Provider cancellation during authorization must remain a terminal abort.");
            });
            Case("Abort reentry before actual preparation", delegate {
                var f = new F(); var a = f.Anchor(); SnapIntentProof old; var r = f.Reserve(a, f.Candidate, out old);
                typeof(SnapIntentPolicy).GetField("currentRevision", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Intent,
                    new Func<long, bool>(delegate { f.Intent.Abort(r); return true; }));
                int reads = f.Api.Reads, marks = f.Api.Marks, removes = f.Api.Removes; SnapLayoutReceiptLedger.Ticket ticket;
                Check(!SnapWorkAreas.TryPrepareOwnedLayout(f.Budget, f.Ledger, f.Candidate, delegate { return true; }, r, old, delegate { return ++f.Now; }, out ticket), "Cancelled preparation must refuse the ticket.");
                Check(reads == f.Api.Reads && marks == f.Api.Marks && removes == f.Api.Removes, "Cancellation inside the preparation revision callback must precede all ledger/property reads and writes.");
            });
            Case("Abort reentry during final nonspending fence", delegate {
                var f = new F(); var a = f.Anchor(); SnapIntentProof old; var r = f.Reserve(a, f.Candidate, out old); var p = f.Prepare(a, f.Candidate, r, old);
                Check(f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, r, p, f.Now) && f.Ledger.MarkInvoked(f.Ticket), "Final-abort fixture authorized once.");
                typeof(SnapIntentPolicy).GetField("currentRevision", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Intent,
                    new Func<long, bool>(delegate { f.Intent.Abort(r); return true; }));
                Check(!f.Intent.NativeAuthorizationStillCurrent(r, p, f.Now), "Provider cancellation during last managed fence must block the native call.");
            });
            Case("Known neutral context invalidation", delegate {
                var f = new SnapIntentPolicyTests.Fixture(); var first = f.Cycle(1010);
                Check(first != null && first.Kind == SnapChordNoticeKind.Completed, "Initial physical cycle establishes known-neutral tracked key state.");
                f.Chord.Invalidate(); var next = f.Cycle(1050);
                Check(next != null && next.Kind == SnapChordNoticeKind.Completed, "After context cancellation at known neutral state, the next complete fresh physical cycle must remain usable.");
                Check(!f.Chord.Issued(first), "Context invalidation still permanently cancels the prior issued completion seal.");
            });
            Case("Held context invalidation remains fenced", delegate {
                var f = new SnapIntentPolicyTests.Fixture(); f.Packet(0x5B, true, 1010); f.Chord.Invalidate();
                f.Packet(0x25, true, 1020, 0, f.Proof(true, 1020)); f.Packet(0x25, false, 1030); var held = f.Packet(0x5B, false, 1040);
                Check(held == null || held.Kind != SnapChordNoticeKind.Completed, "Context cancellation mid-held chord cannot create a completion.");
                var fresh = f.Cycle(1050);
                Check(fresh != null && fresh.Kind == SnapChordNoticeKind.Completed, "A fresh full cycle after all observed keys release can recover from held cancellation.");
            });
            Case("Ambiguous startup remains unavailable", delegate {
                var f = new SnapIntentPolicyTests.Fixture(); var chord = new SnapChordPolicy(1000, 1, 1000, 1000, false);
                chord.Invalidate(); var first = DirectCycle(chord, f, 1010, 0); chord.Invalidate(); var second = DirectCycle(chord, f, 1050, 0);
                Check((first == null || first.Kind != SnapChordNoticeKind.Completed) && (second == null || second.Kind != SnapChordNoticeKind.Completed), "Known-neutral optimization cannot recover an unknown or initially held registration.");
            });
            Case("Mixed held modifiers retain release barrier", delegate {
                var f = new SnapIntentPolicyTests.Fixture(); f.Packet(0x11, true, 1001); f.Chord.Invalidate();
                var mixed = f.Cycle(1010); Check(mixed == null || mixed.Kind != SnapChordNoticeKind.Completed, "An observed held Ctrl survives context invalidation and blocks WinLeft.");
                f.Packet(0x11, false, 1041); var fresh = f.Cycle(1050);
                Check(fresh != null && fresh.Kind == SnapChordNoticeKind.Completed, "Only the actual observed final modifier release permits the next fresh cycle.");
            });
            Case("Injected mixed cycle never funds new context", delegate {
                var f = new SnapIntentPolicyTests.Fixture(); var first = f.Cycle(1010); f.Chord.Invalidate();
                f.Packet(0x5B, true, 1050); f.Packet(0x25, true, 1060, 0x10U, f.Proof(true, 1060));
                f.Packet(0x25, false, 1070, 0x10U); var mixed = f.Packet(0x5B, false, 1080);
                Check(mixed == null || mixed.Kind != SnapChordNoticeKind.Completed, "A mixed physical/injected cycle after neutral invalidation cannot complete.");
                Check(!f.Chord.Issued(first), "The previous context seal remains revoked after mixed packets.");
                var fresh = f.Cycle(1090); Check(fresh != null && fresh.Kind == SnapChordNoticeKind.Completed, "A later separate fully physical cycle may recover at observed neutral without resetting serial or key history.");
            });
            Console.WriteLine("Independent runtime intent assertions: " + checks + "; failing cases: " + failures.Count);
            foreach (var failure in failures) Console.WriteLine("FAIL " + failure);
            if (failures.Count != 0) throw new InvalidOperationException("Independent runtime intent protocol failures: " + failures.Count);
            return checks;
        }
    }
}
