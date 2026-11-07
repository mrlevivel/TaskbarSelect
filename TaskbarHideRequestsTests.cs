using System;

namespace TaskbarSelect
{
    // No native APIs, window creation, provider calls, input or power changes.
    internal static class TaskbarHideRequestsTests
    {
        static TaskbarHideRequests.Identity Id(long window = 1, uint process = 2, long started = 3, uint thread = 4)
        { return new TaskbarHideRequests.Identity(new IntPtr(window), process, started, thread); }
        internal static void Run(Action<bool, string> check)
        {
            long now = 0; int posts = 0;
            var ledger = new TaskbarHideRequests(delegate { return now; });
            Func<bool> post = delegate { posts++; return true; };
            check(ledger.Request(Id(), true, true, post).Accepted && posts == 1, "First proved visible taskbar receives one hide request");
            now = 1999;
            check(!ledger.Request(Id(), true, true, post).Attempted && posts == 1, "Accepted pending hide is coalesced before its first retry");
            now = 2000; ledger.Request(Id(), true, true, post);
            now = 9999;
            check(!ledger.Request(Id(), true, true, post).Attempted && posts == 2, "Second retry waits eight seconds after the previous request");
            now = 10000; ledger.Request(Id(), true, true, post);
            for (int second = 11; second <= 36000; second++) { now = second * 1000L; ledger.Request(Id(), true, true, post); }
            check(posts == 3 && ledger.Count == 1 && ledger.Request(Id(), true, true, post).CircuitOpen,
                "Ten simulated overnight hours and rediscovered identity objects produce at most three unresolved hide requests");
            ledger.Request(Id(), true, false, post);
            check(ledger.Request(Id(), true, true, post).CircuitOpen && posts == 3, "Policy off/on does not reset an unanswered hide budget");
            check(ledger.Request(Id(), false, false, post).Status == TaskbarHideRequests.RequestStatus.Hidden && ledger.Count == 0 && posts == 3,
                "Observed hidden acknowledgement releases only the in-memory request budget");
            check(ledger.Request(Id(), true, true, post).Accepted && posts == 4, "A genuine hidden-to-visible cycle can hide the same taskbar again");

            foreach (int failure in new[] { 0, 1 }) {
                now = 0; int attempts = 0;
                var failing = new TaskbarHideRequests(delegate { return now; });
                Func<bool> rejected = delegate { attempts++; if (failure == 1) throw new InvalidOperationException("Injected post failure"); return false; };
                for (int second = 0; second < 50; second++) { now = second * 1000L; failing.Request(Id(), true, true, rejected); }
                check(attempts == 3 && failing.Request(Id(), true, true, rejected).CircuitOpen,
                    "Rejected or throwing requests consume the same bounded attempt budget");
            }

            now = 0; ledger = new TaskbarHideRequests(delegate { return now; }); posts = 0;
            ledger.Request(Id(), true, true, post);
            ledger.Request(Id(process: 5), true, true, post);
            ledger.Request(Id(started: 6), true, true, post);
            ledger.Request(Id(thread: 7), true, true, post);
            ledger.Request(Id(window: 8), true, true, post);
            check(posts == 5 && ledger.Count == 5, "A changed shell process lifetime, HWND or thread receives an independent budget");
            ledger.RetireDestroyed(Id());
            check(ledger.Count == 4 && ledger.Request(Id(), true, true, post).Accepted && posts == 6,
                "Caller-proved HWND destruction can retire the exact remembered lifetime");
            var copy = ledger.RememberedIdentities; copy[0] = Id(window: 99);
            check(ledger.RememberedIdentities.Length == 5 && ledger.Count == 5, "Remembered identities are a bounded independent copy");

            now = 0; ledger = new TaskbarHideRequests(delegate { return now; }); posts = 0;
            for (int i = 1; i <= TaskbarHideRequests.MaximumEntries; i++) ledger.Request(Id(window: i), true, true, post);
            check(ledger.Request(Id(window: 1000), true, true, post).CircuitOpen && posts == TaskbarHideRequests.MaximumEntries && ledger.Count == TaskbarHideRequests.MaximumEntries,
                "Topology churn cannot evict unresolved budgets or grow the ledger beyond its cap");
            ledger.Request(Id(), false, false, post);
            check(ledger.Request(Id(window: 1000), true, true, post).Accepted, "Hidden acknowledgement frees capacity for a newly verified lifetime");

            now = 0; ledger = new TaskbarHideRequests(delegate { return now; }); posts = 0;
            var nested = ledger.Request(Id(), true, true, delegate {
                posts++; now = 20000;
                check(!ledger.Request(Id(), true, true, post).Attempted, "An in-flight native request cannot recursively enqueue a second hide");
                return true;
            });
            check(nested.Accepted && posts == 1, "The in-flight claim precedes the injected native call");
            now = -1;
            check(!ledger.Request(Id(), true, true, post).Attempted, "Clock rollback cannot rearm a pending request");
            now = 2000;
            check(ledger.Request(Id(), true, true, post).Attempted && posts == 2, "A normal monotonic deadline remains live after a rejected rollback sample");
            int before = posts;
            foreach (var invalid in new[] { Id(window: 0), Id(process: 0), Id(started: 0), Id(thread: 0), null })
                check(!ledger.Request(invalid, true, true, post).Attempted, "Unproved native identity cannot enqueue a hide request");
            check(posts == before, "All invalid identity refusals are side-effect free");

            now = 0; ledger = new TaskbarHideRequests(delegate { return now; }); posts = 0;
            ledger.Request(Id(), true, true, delegate {
                posts++;
                ledger.Request(Id(), false, false, post);
                now = 20000;
                check(!ledger.Request(Id(), true, true, post).Attempted && posts == 1,
                    "Reentrant hidden acknowledgement cannot allow a second post before the first delegate returns");
                return true;
            });
            check(ledger.Count == 0 && ledger.Request(Id(), true, true, post).Accepted && posts == 2,
                "Acknowledgement rearms a new visible cycle after the prior in-flight delegate exits");
        }
    }
}
