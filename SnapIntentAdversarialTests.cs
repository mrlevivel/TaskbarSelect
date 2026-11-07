using System;
using System.Collections.Generic;
using System.Reflection;

namespace TaskbarSelect
{
    // Independent metadata-only regressions. The shared fixture runs actual
    // selected policy/ledger/helper source against its injected window API.
    // No observer, hook, application startup or native provider is used.
    internal static class SnapIntentAdversarialTests
    {
        static int checks;
        static readonly List<string> failures = new List<string>();
        static void Check(bool okay, string message) { checks++; if (!okay) throw new InvalidOperationException(message); }
        static void Case(string name, Action action)
        { try { action(); } catch (Exception error) { failures.Add(name + ": " + error.GetType().Name + " " + error.Message); } }
        static SnapIntentPolicyTests.Fixture Prepared(out SnapIntentReservation reservation, out SnapIntentProof old, out SnapIntentProof prepared)
        {
            var f = new SnapIntentPolicyTests.Fixture();
            if (!f.Reserve(1010, out reservation, out old)) throw new InvalidOperationException("Independent reservation fixture refused.");
            prepared = f.Prepare(reservation, old);
            if (prepared == null) throw new InvalidOperationException("Independent prepared-lineage fixture refused."); return f;
        }
        static void ReplaceRevisionProvider(SnapIntentPolicy owner, Func<long, bool> provider)
        {
            // Replace the injected provider only in this pure fixture. This is
            // equivalent to exercising its public admission callback contract.
            typeof(SnapIntentPolicy).GetField("currentRevision", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, provider);
        }
        internal static int Run()
        {
            checks = 0; failures.Clear();
            Case("Real old-to-new generation boundary", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                Check(prepared.ReceiptGeneration == old.ReceiptGeneration + 1 && prepared.Nonce == old.Nonce,
                    "Actual ledger preparation must advance the generation before final authorization.");
                Check(!f.Intent.CanConsume(r, old, f.Now) && f.Intent.CanConsume(r, prepared, f.Now),
                    "Old receipt proof cannot substitute for fresh prepared lineage.");
            });
            Case("Ownership and geometry barriers", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                for (int i = 0; i < 13; i++) {
                    var p = prepared.Copy();
                    if (i == 0) p.Nonce = old.Nonce - 1; if (i == 1) p.ReceiptGeneration = old.ReceiptGeneration;
                    if (i == 2) p.OriginNonce++; if (i == 3) p.OriginGeneration++; if (i == 4) p.LineageVerified = false;
                    if (i == 5) p.Context++; if (i == 6) p.WakeEpoch++; if (i == 7) p.ActionReady = false;
                    if (i == 8) p.ReceiptReady = false; if (i == 9) p.Verified = false; if (i == 10) p.OwnedAreaReady = false;
                    if (i == 11) { var rect = p.Outer; rect.Right++; p.Outer = rect; }
                    if (i == 12) { var target = p.Target; target.ProcessStarted++; p.Target = target; }
                    Check(!f.Intent.CanConsume(r, p, f.Now), "A stale lineage, changed frame or unavailable ownership barrier was admitted.");
                }
                Check(f.Intent.CanConsume(r, prepared, f.Now), "Read-only rejection queries must not invent a new reservation or corrupt the valid proof.");
            });
            Case("Retired marker replacement lineage", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                var p = prepared.Copy(); p.Nonce = old.Nonce + 1; p.ReceiptGeneration = 1; p.OldMarkerRetired = false;
                Check(!f.Intent.CanConsume(r, p, f.Now), "A fresh-looking marker alone does not prove retirement of the old marker.");
                p.OldMarkerRetired = true;
                Check(f.Intent.Authorize(r, p, f.Now) && !f.Intent.Authorize(r, p, f.Now), "Verified replacement authorizes exactly once.");
            });
            Case("Coalesced new Started", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                f.Packet(0x5B, true, f.Now + 1); // Deliberately do not deliver the notice to the UI owner.
                Check(!f.Intent.CanConsume(r, prepared, f.Now), "A lost Started delivery must still revoke the old completion seal.");
            });
            Case("Unrelated key after completion", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                f.Packet(0, true, f.Now + 1);
                Check(!f.Intent.CanConsume(r, prepared, f.Now), "Unrelated physical input must invalidate the old seal without storing its key.");
            });
            Case("Revision exception and revision loss", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                ReplaceRevisionProvider(f.Intent, delegate { throw new InvalidOperationException("Injected observation unavailable."); });
                Check(!f.Intent.CanConsume(r, prepared, f.Now), "An unavailable cancellation provider must fail closed.");
                ReplaceRevisionProvider(f.Intent, delegate { return false; });
                Check(!f.Intent.CanConsume(r, prepared, f.Now), "An overwritten cancellation revision must fail closed.");
            });
            Case("Deadline and failed preflight are terminal", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                f.Now = 4000;
                Check(!f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, r, prepared, f.Now) && f.Budget.AttemptCount == 1 && r.Cancelled,
                    "Expired final authorization spends the intent without increasing mutation count.");
                f = Prepared(out r, out old, out prepared); var wrong = prepared.Copy(); wrong.LineageVerified = false;
                Check(!f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, r, wrong, f.Now) && r.Cancelled,
                    "Failed final preflight must terminate this reservation.");
                Check(!f.Budget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, r, prepared, f.Now) && f.Budget.AttemptCount == 1,
                    "A corrected later proof cannot reuse the terminated reservation.");
            });
            Case("Automatic allowance retires matching gesture", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                var freshBudget = new SnapCorrectionPolicy(delegate { return f.Now; }, 1000) { IntentPolicy = f.Intent };
                Check(freshBudget.TryConsume(f.Candidate.Target, f.Candidate.Area) && freshBudget.AttemptCount == 1,
                    "A fresh automatic allowance should still work.");
                Check(r.Cancelled && !freshBudget.TryConsume(f.Candidate.Target, f.Candidate.Area, 0, r, prepared, f.Now) && freshBudget.AttemptCount == 1,
                    "The same gesture cannot fund a second repair after an automatic correction.");
            });
            Case("All physical packet flag barriers", delegate {
                foreach (uint flags in new[] { 2U, 0x10U, 0x20U, 4U, 8U, 0x40U, 0x100U }) {
                    var f = new SnapIntentPolicyTests.Fixture(); var notice = f.Cycle(1010, flags);
                    Check(notice == null || notice.Kind != SnapChordNoticeKind.Completed || !f.Intent.AdmitCompleted(notice, 1, delegate { return true; }),
                        "Injected, Alt or reserved flags cannot become a physical chord.");
                }
            });
            Case("Invalid normal witness must refuse without throwing", delegate {
                var f = new SnapIntentPolicyTests.Fixture(); f.Packet(0x5B, true, 1010);
                var invalid = f.Proof(true, 1020); invalid.Area = null;
                f.Packet(0x25, true, 1020, 0, invalid); f.Packet(0x25, false, 1030); var notice = f.Packet(0x5B, false, 1040);
                Check(!f.Intent.AdmitCompleted(notice, 1, delegate { return true; }), "An unknown area cannot issue intent.");
            });
            Case("Reentrant cancellation provider must not escape or authorize", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                bool once = true;
                ReplaceRevisionProvider(f.Intent, delegate {
                    if (once) { once = false; f.Intent.CancelPendingInput(); }
                    return true;
                });
                Check(!f.Intent.CanConsume(r, prepared, f.Now), "Cancellation during the injected provider must revoke this reservation.");
            });
            Case("Reentrant authorization remains one-use", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                bool once = true, nested = false;
                ReplaceRevisionProvider(f.Intent, delegate {
                    if (once) { once = false; nested = f.Intent.Authorize(r, prepared, f.Now); }
                    return true;
                });
                bool outer = f.Intent.Authorize(r, prepared, f.Now);
                Check(!(outer && nested), "A single reservation cannot report two successful native authorizations through provider reentry.");
            });
            Case("Wake invalidates old input without resetting same wake", delegate {
                SnapIntentReservation r; SnapIntentProof old, prepared; var f = Prepared(out r, out old, out prepared);
                Check(!f.Budget.BeginWakeEpoch(0) && f.Intent.CanConsume(r, prepared, f.Now), "A duplicate/stale wake must not silently replace input context.");
                Check(f.Budget.BeginWakeEpoch(1) && !f.Intent.CanConsume(r, prepared, f.Now), "Only a genuine newer epoch invalidates stale intent and starts its quiet gate.");
            });
            foreach (string failure in failures) Console.WriteLine("FAIL: independent " + failure);
            if (failures.Count != 0) throw new InvalidOperationException(failures.Count + " independent pure intent case(s) failed; " + checks + " assertions evaluated.");
            Console.WriteLine("PASS: " + checks + " independent pure intent assertions; no native, observer or application startup.");
            return checks;
        }
    }
}
