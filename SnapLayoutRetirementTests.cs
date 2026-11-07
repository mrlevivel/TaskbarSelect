using System;

namespace TaskbarSelect
{
    // Actual correction-policy / admission / ledger / managed Track pipeline.
    // Track only copies candidate values. The injected backend never invokes
    // a native provider, HWND, process, timer, profile or desktop operation.
    internal static class SnapLayoutRetirementTests
    {
        sealed class Api : ISnapLayoutApi
        {
            internal SnapLayoutObservation Window;
            internal int Reads, Marks, Removes;
            public SnapLayoutObservation Read(SnapCorrectionTarget target) { Reads++; return Window.Copy(); }
            public bool SetNonce(SnapCorrectionTarget target, long nonce) { Marks++; Window.Nonce = nonce; return true; }
            public bool RemoveNonce(SnapCorrectionTarget target, long nonce)
            { Removes++; if (Window.Nonce != nonce) return false; Window.Nonce = 0; return true; }
            public SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area)
            { throw new Exception("Retirement must not poll any work area or taskbar."); }
            public bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> beforeNativeSet)
            { throw new Exception("Receipt retirement never issues inverse geometry."); }
        }
        sealed class Fixture
        {
            internal long Now;
            internal readonly Api Api = new Api();
            internal readonly SnapLayoutReceiptLedger Ledger;
            internal readonly SnapLayoutNative Tracker = new SnapLayoutNative();
            internal readonly SnapCorrectionPolicy Policy;
            internal readonly SnapLayoutCandidate Old;
            internal readonly SnapLayoutReceiptLedger.Ticket OldTicket;
            internal Fixture(bool max = true, int width = 1920)
            {
                Policy = new SnapCorrectionPolicy(delegate { return Now; }, 1000);
                Ledger = new SnapLayoutReceiptLedger(Api); Old = Candidate(max, width);
                Api.Window = new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = Old.Target };
                Before(Old); OldTicket = Forward(Old);
            }
            internal void Before(SnapLayoutCandidate candidate)
            { Api.Window.Outer = candidate.Before; Api.Window.Visible = candidate.BeforeVisible; Api.Window.Maximized = candidate.Maximized; Api.Window.Arranged = !candidate.Maximized; }
            internal void Desired(SnapLayoutCandidate candidate)
            { Api.Window.Outer = candidate.Desired; Api.Window.Visible = candidate.DesiredVisible; Api.Window.Maximized = candidate.Maximized; Api.Window.Arranged = !candidate.Maximized; }
            internal bool Admit(SnapLayoutCandidate candidate, out SnapLayoutReceiptLedger.Ticket ticket)
            { return SnapWorkAreas.TryPrepareOwnedLayout(Policy, Ledger, candidate, Tracker.Track, out ticket); }
            internal SnapLayoutReceiptLedger.Ticket Forward(SnapLayoutCandidate candidate)
            {
                SnapLayoutReceiptLedger.Ticket ticket;
                if (!Admit(candidate, out ticket) || !Policy.TryConsume(candidate.Target, candidate.Area) || !Ledger.MarkInvoked(ticket))
                    throw new Exception("Injected forward unexpectedly refused.");
                Desired(candidate); Ledger.RecordReturned(ticket, true);
                if (!ticket.Receipt.Confirmed) throw new Exception("Injected forward failed confirmation.");
                return ticket;
            }
            internal void Wake(long sequence)
            { if (!Policy.BeginWakeEpoch(sequence)) throw new Exception("Injected authoritative wake unexpectedly refused."); Now += 5000; }
        }
        static Native.Rect R(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        static SnapLayoutCandidate Candidate(bool max, int width)
        {
            SnapCorrectionArea area;
            if (!SnapCorrectionArea.TryCapture(new AreaRecord { Identity = "retirement-owned-neutral-monitor",
                Monitor = R(2560, 367, 4480, 1447), Original = R(2560, 367, 4480, 1399), Applied = R(2560, 367, 4480, 1447) }, out area))
                throw new Exception("Invalid fixture area.");
            var candidate = new SnapLayoutCandidate { Target = new SnapCorrectionTarget { Window = new IntPtr(1), Process = 2, Thread = 3, ProcessStarted = 4 },
                Area = area, Maximized = max, Before = R(2552, 359, 2568 + width, 1407), BeforeVisible = R(2560, 367, 2560 + width, 1399),
                Desired = R(2552, 359, 2568 + width, 1455), DesiredVisible = R(2560, 367, 2560 + width, 1447) };
            if (!candidate.Valid) throw new Exception("Invalid fixture candidate.");
            return candidate;
        }
        static int checks;
        static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        internal static void Run()
        {
            checks = 0;
            foreach (int scenario in new[] { 0, 1, 2 }) {
                var f = scenario == 0 ? new Fixture() : new Fixture(false, 960);
                var next = scenario == 2 ? Candidate(true, 1920) : Candidate(false, scenario == 0 ? 960 : 1280);
                f.Before(next); SnapLayoutReceiptLedger.Ticket ticket;
                Check(f.OldTicket.Receipt.Confirmed && f.OldTicket.Receipt.Returned && !f.Tracker.Track(next, false),
                    "The exact old immutable Track counterexample is present before new admission.");
                int reads = f.Api.Reads;
                Check(!f.Admit(next, out ticket) && f.Api.Removes == 0 && f.Api.Reads == reads && f.Policy.AttemptCount == 1,
                    "Changing width/mode cannot invent same-wake correction credit or retirement work.");
                f.Wake(1);
                Check(f.Admit(next, out ticket) && ticket != null && f.OldTicket.Receipt.Resolved && f.OldTicket.Receipt.Skipped,
                    "A genuine new wake admits changed width/mode after yielding the known completed old receipt.");
                Check(f.Api.Removes == 1 && f.Api.Marks == 2 && f.Ledger.Count == 2 && ticket.Receipt.Nonce != f.OldTicket.Receipt.Nonce,
                    "Actual managed Track accepts replacement only after retirement; fresh forward acquires a distinct nonce.");
                f.Ledger.RecordUnknown(f.OldTicket);
                Check(!ticket.Receipt.Invoked && !ticket.Receipt.Returned && f.Api.Window.Nonce == ticket.Receipt.Nonce,
                    "The old receipt's late callback cannot alter the new generation or marker.");
                Check(f.Policy.TryConsume(next.Target, next.Area) && f.Ledger.MarkInvoked(ticket) && f.Policy.AttemptCount == 1,
                    "The replacement forward consumes exactly the existing new-wake policy allowance.");
                f.Desired(next); f.Ledger.RecordReturned(ticket, true);
                Check(ticket.Receipt.Confirmed && !f.Policy.CanAttempt(next.Target, next.Area),
                    "The new geometry is confirmed and does not replenish mutation credit.");
            }
            {
                var f = new Fixture(); f.Before(f.Old); f.Wake(1); SnapLayoutReceiptLedger.Ticket ticket;
                Check(f.Admit(f.Old, out ticket) && Object.ReferenceEquals(ticket.Receipt, f.OldTicket.Receipt) &&
                    f.Api.Removes == 0 && f.Api.Marks == 1 && f.Ledger.Count == 1,
                    "Unchanged owned same-candidate reuse keeps its established nonce and receipt path.");
            }
            foreach (bool same in new[] { false, true }) {
                var f = new Fixture(); var next = same ? f.Old : Candidate(false, 960);
                f.Before(next); f.Api.Window.Nonce = 0; f.Wake(1); SnapLayoutReceiptLedger.Ticket ticket;
                Check(f.Admit(next, out ticket) && f.OldTicket.Receipt.Resolved && f.Api.Removes == 0 &&
                    f.Api.Marks == 2 && ticket.Receipt.Nonce != f.OldTicket.Receipt.Nonce,
                    "Known complete ownership with an absent property yields without removal, including same-owner handle reuse; new SetNonce is still required.");
            }
            {
                var f = new Fixture(); var next = Candidate(false, 960); f.Before(next); f.Wake(1);
                f.Api.Window.Kind = SnapLayoutReadKind.Unknown; int before = f.Api.Reads; SnapLayoutReceiptLedger.Ticket ticket;
                for (int i = 0; i < 1000; i++) if (f.Admit(next, out ticket)) throw new Exception("Unknown snapshot admitted new geometry.");
                Check(f.Api.Reads - before == SnapLayoutReceiptLedger.MaximumRetirementProbes && f.Api.Removes == 0 && f.Ledger.Count == 1,
                    "Unreadable retirement has a fixed native-read allowance, without mutation or allocation loops.");
                f.Api.Window.Kind = SnapLayoutReadKind.Current;
                Check(!f.Admit(next, out ticket) && f.Api.Reads - before == SnapLayoutReceiptLedger.MaximumRetirementProbes,
                    "Refresh or another candidate cannot reset this epoch's exhausted retirement read allowance.");
                f.Before(f.Old);
                Check(f.Admit(f.Old, out ticket) && Object.ReferenceEquals(ticket.Receipt, f.OldTicket.Receipt) && f.Api.Removes == 0,
                    "Exhausted changed-shape probes do not affect the existing same-candidate forward path.");
            }
            {
                var f = new Fixture(); var next = Candidate(false, 960); f.Before(next); f.Wake(1);
                f.Api.Window.Kind = SnapLayoutReadKind.Unknown; SnapLayoutReceiptLedger.Ticket ticket;
                for (int i = 0; i < 1000; i++) f.Admit(next, out ticket);
                f.Api.Window.Kind = SnapLayoutReadKind.Current; f.Wake(2);
                Check(f.Admit(next, out ticket) && f.Api.Removes == 1 && f.Ledger.Count == 2 && f.Policy.AttemptCount == 0,
                    "Only a later accepted wake renews read probes; it does not consume or invent a mutation attempt.");
            }
            Console.WriteLine("PASS: " + checks + " coupled receipt retirement checks; no native or desktop accessed.");
        }
        internal static int RunPureTest()
        { try { Run(); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
    }
}
