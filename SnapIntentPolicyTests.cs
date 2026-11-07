using System;

namespace TaskbarSelect
{
    internal static class SnapIntentPolicyTests
    {
        sealed class Api : ISnapLayoutApi
        {
            internal SnapLayoutObservation Window;
            internal int Reads, Marks, Removes, NativePositions;
            public SnapLayoutObservation Read(SnapCorrectionTarget target) { Reads++; return Window.Copy(); }
            public bool SetNonce(SnapCorrectionTarget target, long nonce) { Marks++; Window.Nonce = nonce; return true; }
            public bool RemoveNonce(SnapCorrectionTarget target, long nonce) { Removes++; if (Window.Nonce != nonce) return false; Window.Nonce = 0; return true; }
            public SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area) { throw new Exception("No area provider in this harness."); }
            public bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> guard) { throw new Exception("No native mutation in this harness."); }
        }
        internal sealed class Fixture
        {
            internal long Now = 1000, Revision = 1;
            internal readonly SnapChordPolicy Chord = new SnapChordPolicy(1000, 1, 1000, 1000, true);
            internal readonly SnapIntentPolicy Intent = new SnapIntentPolicy(1000, 1);
            internal readonly SnapCorrectionPolicy Budget;
            readonly Api api = new Api();
            internal readonly SnapLayoutReceiptLedger Ledger;
            internal readonly SnapLayoutCandidate Candidate;
            internal SnapLayoutReceiptLedger.Ticket Ticket;
            internal Fixture()
            {
                Budget = new SnapCorrectionPolicy(delegate { return Now; }, 1000) { IntentPolicy = Intent };
                SnapCorrectionArea area;
                SnapCorrectionArea.TryCapture(new AreaRecord { Identity = "pure-intent-owned-area", Monitor = R(2560, 367, 4480, 1447), Original = R(2560, 367, 4480, 1399), Applied = R(2560, 367, 4480, 1447) }, out area);
                Candidate = new SnapLayoutCandidate { Target = new SnapCorrectionTarget { Window = new IntPtr(1), Process = 2, Thread = 3, ProcessStarted = 4 }, Area = area,
                    Maximized = false, Before = R(2553, 367, 3527, 1406), BeforeVisible = R(2560, 367, 3520, 1399),
                    Desired = R(2553, 367, 3527, 1454), DesiredVisible = R(2560, 367, 3520, 1447) };
                api.Window = new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = Candidate.Target, Outer = Candidate.Before, Visible = Candidate.BeforeVisible, Arranged = true,
                    Maximized = false, Iconic = false, Hidden = false, Ineligible = false };
                Ledger = new SnapLayoutReceiptLedger(api);
                if (!Candidate.Valid || !SnapWorkAreas.TryPrepareOwnedLayout(Budget, Ledger, Candidate, delegate { return true; }, out Ticket) ||
                    !Budget.TryConsume(Candidate.Target, area) || !Ledger.MarkInvoked(Ticket)) throw new Exception("Initial automatic fixture denied.");
                Complete(Ticket);
            }
            internal void Complete(SnapLayoutReceiptLedger.Ticket ticket)
            { api.NativePositions++; api.Window.Outer = Candidate.Desired; api.Window.Visible = Candidate.DesiredVisible; Ledger.RecordReturned(ticket, true); }
            internal SnapIntentProof Proof(bool normal, long at)
            {
                return new SnapIntentProof { Target = Candidate.Target, Area = Candidate.Area, Nonce = api.Window.Nonce, ReceiptGeneration = Ticket.Receipt.Generation,
                    WakeEpoch = Budget.WakeEpoch, Context = Intent.Context, At = at, Verified = true, Normal = normal, Arranged = !normal, Maximized = false,
                    OwnedAreaReady = true, ActionReady = true, ReceiptReady = true, LineageVerified = false, OldMarkerRetired = false,
                    Outer = Candidate.Before, Visible = Candidate.BeforeVisible, Desired = Candidate.Desired, DesiredVisible = Candidate.DesiredVisible };
            }
            internal SnapChordNotice Packet(int key, bool down, long at, uint extra = 0, SnapIntentProof proof = null)
            {
                Now = at;
                return Chord.Observe(new SnapChordPacket { Key = key, Down = down, Flags = extra | (down ? 0U : 0x80U), Window = Candidate.Target.Window,
                    At = at, Generation = 1, NativeTime = (uint)at, CurrentNativeTick = (uint)at, Before = proof });
            }
            internal SnapChordNotice Cycle(long start, uint flags = 0)
            {
                Packet(0x5B, true, start, flags);
                Packet(0x25, true, start + 10, flags, Proof(true, start + 10));
                Packet(0x25, false, start + 20, flags);
                return Packet(0x5B, false, start + 30, flags);
            }
            internal bool Reserve(long start, out SnapIntentReservation reservation, out SnapIntentProof old)
            {
                reservation = null; old = null;
                var notice = Cycle(start); Revision++;
                if (!Intent.AdmitCompleted(notice, Revision, delegate(long revision) { return revision == Revision; })) return false;
                api.Window.Outer = Candidate.Before; api.Window.Visible = Candidate.BeforeVisible;
                old = Proof(false, start + 31); Now = start + 31;
                SnapIntentPermit permit;
                return Intent.TryPermit(old, Now, out permit) && Intent.TryReserve(permit, old, Now, out reservation);
            }
            internal SnapIntentProof Prepare(SnapIntentReservation reservation, SnapIntentProof old)
            {
                SnapLayoutReceiptLedger.Ticket prepared;
                if (!SnapWorkAreas.TryPrepareOwnedLayout(Budget, Ledger, Candidate, delegate { return true; }, reservation, old, delegate { return ++Now; }, out prepared)) return null;
                Ticket = prepared;
                var proof = Proof(false, ++Now); proof.OriginNonce = old.Nonce; proof.OriginGeneration = old.ReceiptGeneration; proof.LineageVerified = true;
                return proof;
            }
            internal int NativePositions { get { return api.NativePositions; } }
            internal int ApiReads { get { return api.Reads; } }
            internal int PropertyWrites { get { return api.Marks + api.Removes; } }
            internal SnapLayoutObservation Observed { get { return api.Window.Copy(); } set { api.Window = value.Copy(); } }
        }
        static Native.Rect R(int left, int top, int right, int bottom) { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        static int checks;
        static void Check(bool value, string text) { if (!value) throw new Exception(text); checks++; }
        internal static int Run()
        {
            checks = 0;
            {
                var f = new Fixture(); SnapLayoutReceiptLedger.Ticket rejected;
                Check(!f.Budget.CanAttempt(f.Candidate.Target, f.Candidate.Area), "Counterexample retains spent automatic target/area pair.");
                Check(!SnapWorkAreas.TryPrepareOwnedLayout(f.Budget, f.Ledger, f.Candidate, delegate { return true; }, out rejected), "Original admission still refuses repeated same-wake arrangement without intent.");
                SnapIntentReservation reservation; SnapIntentProof old;
                Check(f.Reserve(1010, out reservation, out old), "One complete physical WinLeft reserves intent against the old confirmed receipt.");
                long oldGeneration = old.ReceiptGeneration;
                var prepared = f.Prepare(reservation, old);
                Check(prepared != null && prepared.ReceiptGeneration == oldGeneration + 1 && prepared.Nonce == old.Nonce,
                    "Actual production admission and ledger Prepare cross the old-to-new generation boundary.");
                Check(f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, reservation, prepared, f.Now) && f.Ledger.MarkInvoked(f.Ticket),
                    "Only fresh prepared lineage authorizes the sole extra attempt before MarkInvoked.");
                f.Complete(f.Ticket);
                Check(f.NativePositions == 2 && f.Budget.AttemptCount == 2 && f.Budget.WakeEpoch == 0 && f.Ticket.Receipt.Confirmed,
                    "Second explicit snap corrects with unchanged wake and two total native authorizations.");
                Check(!f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, reservation, prepared, f.Now) && f.NativePositions == 2,
                    "A consumed reservation has no second native authorization or refund.");
            }
            foreach (uint flags in new[] { 0x10U, 2U, 0x20U, 4U, 0x40U }) {
                var f = new Fixture(); var notice = f.Cycle(1010, flags);
                Check(notice == null || notice.Kind != SnapChordNoticeKind.Completed || !f.Intent.AdmitCompleted(notice, 1, delegate { return true; }),
                    "Injected, lower-IL, Alt or reserved flags cannot create physical intent.");
            }
            {
                var f = new Fixture(); var previous = f.Cycle(1010); f.Chord.Invalidate(); var next = f.Cycle(1060);
                Check(!f.Chord.Issued(previous) && next != null && next.Kind == SnapChordNoticeKind.Completed,
                    "Known-neutral invalidation clears the old seal while permitting the next complete fresh physical cycle.");
            }
            {
                var f = new Fixture(); f.Packet(0x5B, true, 1010); f.Packet(0x25, true, 1020, 0, f.Proof(true, 1020));
                f.Chord.Invalidate(); f.Packet(0x25, false, 1030); var partial = f.Packet(0x5B, false, 1040); var next = f.Cycle(1060);
                Check((partial == null || partial.Kind != SnapChordNoticeKind.Completed) && next != null && next.Kind == SnapChordNoticeKind.Completed,
                    "Held invalidation grants nothing until observed release and a wholly new physical cycle.");
            }
            {
                var f = new Fixture(); f.Packet(0x5B, true, 1010); f.Packet(0x25, true, 1020, 0x10, f.Proof(true, 1020));
                f.Chord.Invalidate(); f.Packet(0x25, false, 1030, 0x10); var mixed = f.Packet(0x5B, false, 1040); var next = f.Cycle(1060);
                Check((mixed == null || mixed.Kind != SnapChordNoticeKind.Completed) && next != null && next.Kind == SnapChordNoticeKind.Completed,
                    "Mixed injected cancellation cannot grant credit; release followed by fresh physical input can.");
                var unknown = new SnapChordPolicy(1000, 1, 1000, 1000, false);
                unknown.Invalidate(); bool completed = false;
                for (int i = 0; i < 4; i++) {
                    int key = i == 0 || i == 3 ? 0x5B : 0x25; bool down = i < 2; long at = 1100 + i * 10;
                    var n = unknown.Observe(new SnapChordPacket { Key = key, Down = down, Flags = down ? 0U : 0x80U, Window = f.Candidate.Target.Window,
                        At = at, Generation = 1, NativeTime = (uint)at, CurrentNativeTick = (uint)at, Before = f.Proof(true, at) });
                    completed |= n != null && n.Kind == SnapChordNoticeKind.Completed;
                }
                Check(!completed, "Unknown startup key state stays fail-closed across invalidation and a full cycle.");
            }
            {
                var f = new Fixture(); f.Packet(0x5B, true, 1010); f.Packet(0x25, true, 1020, 0, f.Proof(true, 1020)); f.Packet(0x25, true, 1021);
                f.Packet(0x25, false, 1030); var notice = f.Packet(0x5B, false, 1040);
                Check(notice == null || notice.Kind != SnapChordNoticeKind.Completed, "Held-arrow autorepeat cannot issue multiple credits.");
            }
            {
                var f = new Fixture(); var notice = f.Cycle(1010);
                Check(!f.Intent.AdmitCompleted(notice), "Delivery without an independent cancellation-revision fence fails closed.");
                Check(f.Intent.AdmitCompleted(notice, 1, delegate { return true; }), "Current exact revision admits the issued physical notice.");
                f.Packet(0x5B, true, 1050);
                SnapIntentPermit permit;
                Check(!f.Intent.TryPermit(f.Proof(false, 1051), 1051, out permit), "A new Started invalidates old completed intent even when cancellation delivery is lost.");
            }
            {
                var f = new Fixture(); SnapIntentReservation reservation; SnapIntentProof old;
                Check(f.Reserve(1010, out reservation, out old), "Expiry fixture reserves a valid physical chord.");
                var prepared = f.Prepare(reservation, old); f.Now = 4000;
                Check(!f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, reservation, prepared, f.Now) && f.Budget.AttemptCount == 1,
                    "A preparation stall beyond two seconds cannot authorize SetPos.");
            }
            {
                var f = new Fixture(); SnapIntentReservation reservation; SnapIntentProof old;
                Check(f.Reserve(1010, out reservation, out old), "Revision fixture reserves valid intent.");
                var prepared = f.Prepare(reservation, old); f.Revision++;
                Check(!f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, reservation, prepared, f.Now), "Overwritten Cancelled revision independently vetoes final authorization.");
            }
            {
                var f = new Fixture(); SnapIntentPermit permit;
                for (int i = 0; i < 100; i++) Check(!f.Intent.TryPermit(f.Proof(false, 1010 + i), 1010 + i, out permit), "LOCATIONCHANGE, normal oscillation and no chord grant zero credits.");
                Check(f.Budget.AttemptCount == 1 && f.NativePositions == 1, "Metadata oscillation neither resets mutation budget nor touches geometry.");
            }
            for (int variant = 0; variant < 9; variant++) {
                var f = new Fixture(); SnapIntentReservation reservation; SnapIntentProof old;
                Check(f.Reserve(1010, out reservation, out old), "Candidate substitution fixture reserves one physical chord.");
                var c = new SnapLayoutCandidate { Target = f.Candidate.Target, Area = f.Candidate.Area, Before = f.Candidate.Before,
                    BeforeVisible = f.Candidate.BeforeVisible, Desired = f.Candidate.Desired, DesiredVisible = f.Candidate.DesiredVisible, Maximized = false };
                if (variant == 0) c.Before.Left--;
                if (variant == 1) c.BeforeVisible.Left++;
                if (variant == 2) c.Desired.Right++;
                if (variant == 3) c.DesiredVisible.Right--;
                if (variant == 4) { c.Before.Right += 64; c.BeforeVisible.Right += 64; c.Desired.Right += 64; c.DesiredVisible.Right += 64; }
                if (variant == 5) c.Maximized = true;
                if (variant == 6) c.Target.ProcessStarted++;
                if (variant == 7) c.Area = null;
                if (variant == 8) c = null;
                int tracks = 0, reads = f.ApiReads, writes = f.PropertyWrites; SnapLayoutReceiptLedger.Ticket rejected;
                Check(!SnapWorkAreas.TryPrepareOwnedLayout(f.Budget, f.Ledger, c, delegate { tracks++; return true; }, reservation, old, delegate { return ++f.Now; }, out rejected),
                    "A different Before/Desired/frame/lifetime/mode or null candidate cannot reach preparation.");
                Check(rejected == null && tracks == 0 && f.ApiReads == reads && f.PropertyWrites == writes && reservation.Cancelled,
                    "Rejected candidate performs no Track, property read or write and does not refund input.");
            }
            {
                var f = new Fixture(); SnapIntentReservation reservation; SnapIntentProof old;
                Check(f.Reserve(1010, out reservation, out old), "Malformed area fixture reserves one physical chord.");
                var malformed = old.Copy(); malformed.Area = null;
                Check(!f.Budget.CanPrepareIntent(f.Candidate.Target, f.Candidate.Area, 0, reservation, malformed, f.Now),
                    "Malformed old proof with null Area fails the budget boundary without throwing.");
                var prepared = f.Prepare(reservation, old); malformed = prepared.Copy(); malformed.Area = null;
                Check(!f.Budget.CanAttempt(f.Candidate.Target, f.Candidate.Area, 0, reservation, malformed, f.Now) &&
                    !f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, reservation, malformed, f.Now),
                    "Malformed prepared proof with null Area cannot throw or consume a native authorization.");
                Check(f.Budget.AttemptCount == 1 && f.NativePositions == 1, "Malformed area proof leaves shared mutation count unchanged.");
            }
            {
                var f = new Fixture(); long time = 1010;
                for (int i = 0; i < 127; i++) {
                    SnapIntentReservation reservation; SnapIntentProof old;
                    if (!f.Reserve(time, out reservation, out old)) throw new Exception("Physical repeated cap fixture failed.");
                    var prepared = f.Prepare(reservation, old);
                    if (prepared == null || !f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, reservation, prepared, f.Now) || !f.Ledger.MarkInvoked(f.Ticket)) throw new Exception("Shared cap fixture refused before128.");
                    f.Complete(f.Ticket); time += 100;
                }
                Check(f.Budget.AttemptCount == 128 && f.NativePositions == 128 && f.Ledger.Count == 1, "Shared hard cap counts128 actual authorizations of one pair, not128 distinct pairs.");
                SnapIntentReservation last; SnapIntentProof before;
                Check(f.Reserve(time, out last, out before) && f.Prepare(last, before) == null && f.Budget.AttemptCount == 128,
                    "A fresh physical chord cannot exceed the shared128 cap.");
            }
            Console.WriteLine("PASS: " + checks + " first-slice pure WinArrow intent checks; no native provider, application startup or desktop.");
            return checks;
        }
    }
}
