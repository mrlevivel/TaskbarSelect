using System;

namespace TaskbarSelect
{
    // Actual anchor -> native-before witness -> exact old candidate -> ledger
    // Prepare -> derived new lineage -> MarkInvoked -> last fence protocol.
    // All external observations/mutations remain the injected pure fixture.
    internal static class SnapIntentRuntimeTests
    {
        static int checks;
        static void Check(bool okay, string message) { if (!okay) throw new Exception(message); checks++; }
        static void Observe(SnapIntentPolicyTests.Fixture f, SnapLayoutCandidate c, bool normal, bool desired)
        {
            var o = f.Observed;
            o.Arranged = !normal; o.Maximized = false;
            o.Outer = desired ? c.Desired : c.Before; o.Visible = desired ? c.DesiredVisible : c.BeforeVisible;
            if (normal) { o.Outer.Right = o.Outer.Left + 640; o.Outer.Bottom = o.Outer.Top + 480;
                o.Visible.Left = o.Outer.Left + 7; o.Visible.Right = o.Outer.Right - 7; o.Visible.Bottom = o.Outer.Bottom - 7; }
            f.Observed = o;
        }
        static bool Reserve(SnapIntentPolicyTests.Fixture f, SnapLayoutCandidate c,
            out SnapLayoutReceiptLedger.IntentAnchor anchor, out SnapIntentReservation reservation, out SnapIntentProof old)
        {
            reservation = null; old = null;
            if (!f.Ledger.TryIntentAnchor(c.Target, out anchor)) return false;
            Observe(f, c, true, false); SnapIntentProof witness;
            f.Now = 1010;
            if (!f.Ledger.TryIntentWitness(anchor, f.Budget.WakeEpoch, f.Intent.Context, f.Now, delegate { return true; }, out witness)) return false;
            f.Packet(0x5B, true, 1010); witness.At = 1020;
            f.Packet(0x25, true, 1020, 0, witness); f.Packet(0x25, false, 1030);
            var notice = f.Packet(0x5B, false, 1040); f.Revision++;
            if (!f.Intent.AdmitCompleted(notice, f.Revision, delegate(long revision) { return revision == f.Revision; })) return false;
            Observe(f, c, false, false); f.Now = 1041;
            SnapIntentPermit permit;
            return f.Ledger.TryIntentCandidate(anchor, c, f.Budget.WakeEpoch, f.Intent.Context, f.Now, delegate { return true; }, out old) &&
                f.Intent.TryPermit(old, f.Now, out permit) && f.Intent.TryReserve(permit, old, f.Now, out reservation);
        }
        internal static int Run()
        {
            checks = 0;
            foreach (bool different in new[] { false, true }) {
                var f = new SnapIntentPolicyTests.Fixture(); var c = f.Candidate;
                if (different) { c = new SnapLayoutCandidate { Target = c.Target, Area = c.Area, Before = c.Before, BeforeVisible = c.BeforeVisible,
                    Desired = c.Desired, DesiredVisible = c.DesiredVisible }; c.Before.Right += 64; c.BeforeVisible.Right += 64; c.Desired.Right += 64; c.DesiredVisible.Right += 64; }
                SnapLayoutReceiptLedger.IntentAnchor anchor; SnapIntentReservation reservation; SnapIntentProof old, prepared;
                Check(Reserve(f, c, out anchor, out reservation, out old), "Actual completed ledger anchor and before witness must reserve physical input.");
                SnapLayoutReceiptLedger.Ticket ticket;
                Check(SnapWorkAreas.TryPrepareOwnedLayout(f.Budget, f.Ledger, c, delegate { return true; }, reservation, old, delegate { return ++f.Now; }, out ticket),
                    "Actual intent helper must admit a spent automatic pair through old proof.");
                Check(f.Ledger.TryIntentPrepared(anchor, ticket, c, 0, f.Intent.Context, ++f.Now, delegate { return true; }, out prepared) && prepared.LineageVerified,
                    "Prepared lineage must be issued by actual anchor/ticket checks.");
                Check(different ? prepared.OldMarkerRetired && prepared.Nonce > old.Nonce && prepared.ReceiptGeneration == 1 :
                    !prepared.OldMarkerRetired && prepared.Nonce == old.Nonce && prepared.ReceiptGeneration == old.ReceiptGeneration + 1,
                    "Same receipt or actually removed predecessor determines new marker lineage.");
                Check(!f.Intent.NativeAuthorizationStillCurrent(reservation, prepared, f.Now), "Last fence cannot create an unspent native authorization.");
                Check(f.Budget.TryConsume(c.Target, c.Area, 0, reservation, prepared, f.Now) && f.Ledger.MarkInvoked(ticket), "Budget spends once before real MarkInvoked.");
                Check(f.Ledger.IntentInvocationCurrent(anchor, ticket, c) && f.Intent.NativeAuthorizationStillCurrent(reservation, prepared, ++f.Now),
                    "Post-MarkInvoked final fence accepts exact spent authorization and actual ticket lineage.");
                Check(f.Budget.AttemptCount == 2 && !f.Budget.TryConsume(c.Target, c.Area, 0, reservation, prepared, f.Now), "Final fence does not spend a third shared attempt.");
                Observe(f, c, false, true); f.Ledger.RecordReturned(ticket, true);
                Check(!f.Ledger.IntentInvocationCurrent(anchor, ticket, c), "Returned ticket cannot satisfy a fresh native boundary.");
                SnapLayoutReceiptLedger.IntentAnchor latest;
                Check(f.Ledger.TryIntentAnchor(c.Target, out latest) && latest.Nonce == prepared.Nonce && latest.Generation == prepared.ReceiptGeneration,
                    "Confirmed actual return publishes only the latest anchor.");
            }
            {
                var f = new SnapIntentPolicyTests.Fixture(); SnapLayoutReceiptLedger.IntentAnchor anchor; SnapIntentReservation reservation; SnapIntentProof old, prepared = null;
                Check(Reserve(f, f.Candidate, out anchor, out reservation, out old), "Late cancellation fixture reserves actual input.");
                SnapLayoutReceiptLedger.Ticket ticket;
                Check(SnapWorkAreas.TryPrepareOwnedLayout(f.Budget, f.Ledger, f.Candidate, delegate { return true; }, reservation, old, delegate { return ++f.Now; }, out ticket) &&
                    f.Ledger.TryIntentPrepared(anchor, ticket, f.Candidate, 0, f.Intent.Context, ++f.Now, delegate { return true; }, out prepared) &&
                    f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, reservation, prepared, f.Now) && f.Ledger.MarkInvoked(ticket), "Late cancellation reaches actual authorized invocation boundary.");
                f.Revision++;
                // Recreate the derived proof only in a fresh fixture below;
                // a cancellation after native MarkInvoked must veto SetPos.
                Check(!f.Intent.NativeAuthorizationStillCurrent(reservation, prepared, ++f.Now), "Revision supersession after MarkInvoked prevents native SetPos.");
                Check(f.Budget.AttemptCount == 2, "Rejected final fence never refunds or replenishes budget.");
            }
            {
                var f = new SnapIntentPolicyTests.Fixture(); SnapLayoutReceiptLedger.IntentAnchor anchor;
                Check(f.Ledger.TryIntentAnchor(f.Candidate.Target, out anchor), "Ready-anchor fixture begins confirmed.");
                var o = f.Observed; o.Nonce++; f.Observed = o;
                SnapIntentProof proof;
                Check(!f.Ledger.TryIntentWitness(anchor, 0, f.Intent.Context, ++f.Now, delegate { return true; }, out proof), "Changed native marker prevents witness publication.");
                o.Nonce--; f.Observed = o; f.Ledger.BeginExit();
                Check(!f.Ledger.TryIntentWitness(anchor, 0, f.Intent.Context, ++f.Now, delegate { return true; }, out proof), "Exit invalidates opaque anchor even with the old marker still present.");
            }
            Console.WriteLine("PASS: " + checks + " actual-ledger intent runtime protocol checks; injected API only, no observer/application/native startup.");
            return checks;
        }
    }
}
