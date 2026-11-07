using System;
using System.Diagnostics;

namespace TaskbarSelect
{
    // Exercise the production managed guard without hooks, HWNDs, helpers or UI.
    internal static class WindowMoveCoordinatorTests
    {
        static void ConstructorPolicy(Action<bool, string> assert)
        {
            bool missingClock = false, zeroBudget = false, negativeBudget = false;
            try { new WindowMoveCoordinator(null, 1000); }
            catch (ArgumentNullException) { missingClock = true; }
            try { new WindowMoveCoordinator(delegate { return 0; }, 0); }
            catch (ArgumentOutOfRangeException) { zeroBudget = true; }
            try { new WindowMoveCoordinator(delegate { return 0; }, -1); }
            catch (ArgumentOutOfRangeException) { negativeBudget = true; }
            assert(missingClock && zeroBudget && negativeBudget, "Movement requires a monotonic clock and a positive whole-operation budget");
            using (var coordinator = new WindowMoveCoordinator()) {
                long before = Stopwatch.GetTimestamp();
                assert(coordinator.TryEnter(), "A default movement guard admits its first operation");
                long after = Stopwatch.GetTimestamp();
                long budget = Stopwatch.Frequency * 1000 / 1000;
                assert(coordinator.Deadline >= before + budget && coordinator.Deadline <= after + budget,
                    "The default movement budget is exactly one second from admission");
                coordinator.Exit();
            }
        }

        static void AdmissionAndDeadline(Action<bool, string> assert)
        {
            long now = 100; int clockReads = 0;
            using (var coordinator = new WindowMoveCoordinator(delegate { clockReads++; return now; }, 250)) {
                assert(!coordinator.Busy && !coordinator.Current && !coordinator.Cancelled && !coordinator.Expired && coordinator.Deadline == 0,
                    "An idle movement guard has no operation, outcome or deadline");
                assert(coordinator.TryEnter() && coordinator.Busy && coordinator.Current && !coordinator.Cancelled && !coordinator.Expired,
                    "Admission creates one current bounded operation");
                long generation = coordinator.Generation, deadline = coordinator.Deadline;
                int reads = clockReads;
                assert(!coordinator.TryEnter() && coordinator.Generation == generation && coordinator.Deadline == deadline && clockReads == reads,
                    "A nested movement cannot change the owner's generation, deadline or clock state");
                now = deadline - 1;
                assert(coordinator.Current && !coordinator.Expired, "An operation remains current at the last tick before its deadline");
                now = deadline;
                assert(!coordinator.Current && coordinator.Expired && !coordinator.Cancelled && coordinator.Busy,
                    "The exact deadline expires the operation without releasing its owned slot");
                reads = clockReads;
                assert(!coordinator.TryEnter() && coordinator.Deadline == deadline && coordinator.Generation == generation && clockReads == reads,
                    "A nested attempt cannot restart an exhausted operation's budget");
                now++;
                assert(!coordinator.Current && coordinator.Expired, "An operation stays expired after its deadline");
                coordinator.Exit();
                assert(!coordinator.Busy && !coordinator.Current && !coordinator.Cancelled && !coordinator.Expired && coordinator.Deadline == 0,
                    "Only owner exit releases the slot and clears its active budget");
                assert(coordinator.TryEnter() && coordinator.Current && coordinator.Generation > generation && coordinator.Deadline > deadline,
                    "A completed expired operation does not prevent a fresh independently bounded move");
                coordinator.Exit();
            }
        }

        static void CancellationAndRestart(Action<bool, string> assert)
        {
            long now = 100;
            using (var coordinator = new WindowMoveCoordinator(delegate { return now; }, 1000)) {
                coordinator.Cancel();
                assert(!coordinator.Busy && !coordinator.Current && !coordinator.Cancelled && !coordinator.Expired,
                    "Idle cancellation never creates pending or current work");
                assert(coordinator.TryEnter() && coordinator.Current, "An idle cancellation does not poison the next movement");
                long generation = coordinator.Generation, deadline = coordinator.Deadline;
                coordinator.Cancel();
                assert(coordinator.Busy && coordinator.Cancelled && !coordinator.Current && !coordinator.Expired && coordinator.Generation != generation,
                    "Cancellation fences the active generation while retaining its cleanup slot");
                coordinator.Cancel(); coordinator.Cancel();
                assert(coordinator.Busy && coordinator.Cancelled && !coordinator.Current && coordinator.Deadline == deadline,
                    "Repeated cancellation keeps the cancelled outcome and original deadline");
                assert(!coordinator.TryEnter() && coordinator.Cancelled && coordinator.Deadline == deadline,
                    "Cancelled work cannot admit a nested replacement before owner cleanup");
                coordinator.Exit();
                assert(!coordinator.Busy && !coordinator.Cancelled, "Owner exit finishes cancelled work without leaving an active outcome");
                now = deadline;
                assert(coordinator.TryEnter() && coordinator.Current && !coordinator.Cancelled && !coordinator.Expired,
                    "A new movement receives fresh authority and budget after cancellation cleanup");
                coordinator.Exit();
            }
        }

        static void Disposal(Action<bool, string> assert)
        {
            long now = 100;
            var idle = new WindowMoveCoordinator(delegate { return now; }, 1000);
            idle.Dispose();
            long generation = idle.Generation;
            idle.Dispose(); idle.Exit();
            assert(!idle.TryEnter() && !idle.Busy && !idle.Current && !idle.Cancelled && !idle.Expired && idle.Generation == generation,
                "Repeated disposal and idle exit cannot revive a disposed movement guard");
            var active = new WindowMoveCoordinator(delegate { return now; }, 1000);
            assert(active.TryEnter(), "The disposal fixture starts an active movement");
            long deadline = active.Deadline;
            active.Dispose(); generation = active.Generation;
            assert(active.Busy && active.Cancelled && !active.Current && !active.TryEnter() && active.Deadline == deadline,
                "Disposal fences active work without releasing resources before its owner exits");
            active.Dispose();
            assert(active.Generation == generation && active.Busy && active.Cancelled,
                "Repeated active disposal preserves the same cancelled operation");
            now = deadline;
            assert(active.Expired && active.Cancelled && !active.Current,
                "Disposed work can reach its deadline without regaining authority");
            active.Exit(); active.Exit();
            assert(!active.Busy && !active.Cancelled && !active.Expired && active.Deadline == 0 && !active.TryEnter(),
                "Cleanup clears the disposed operation while future movement stays permanently rejected");
        }

        static void WholeOperationBudget(Action<bool, string> assert)
        {
            long now = 100;
            using (var coordinator = new WindowMoveCoordinator(delegate { return now; }, 1000)) {
                assert(coordinator.TryEnter(), "The multi-step fixture admits one movement transaction");
                long deadline = coordinator.Deadline, generation = coordinator.Generation;
                long budget = deadline - now;
                now += budget / 3;
                assert(coordinator.Current && coordinator.Deadline == deadline, "The first native substep consumes the original shared budget");
                now += budget / 3;
                assert(coordinator.Current && !coordinator.TryEnter() && coordinator.Deadline == deadline && coordinator.Generation == generation,
                    "A later substep or nested request cannot extend the whole movement deadline");
                now = deadline;
                assert(!coordinator.Current && coordinator.Expired, "Remaining substeps lose authority at the original whole-operation deadline");
                coordinator.Exit();
            }
        }

        internal static void Run(Action<bool, string> assert)
        {
            ConstructorPolicy(assert);
            AdmissionAndDeadline(assert);
            CancellationAndRestart(assert);
            Disposal(assert);
            WholeOperationBudget(assert);
        }
    }
}
