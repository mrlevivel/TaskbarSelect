using System;

namespace TaskbarSelect
{
    internal static class HoverQueryPolicyTests
    {
        static HoverQueryPolicy.Key Key(int source = 1, int x = 10, bool preview = false, TaskbarTargets.Target origin = null)
        { return new HoverQueryPolicy.Key(new IntPtr(source), new TaskbarMover.Point { X = x, Y = 20 }, preview, origin); }
        internal static void Run(Action<bool, string> check)
        { Stationary(check); FloorsAndKeys(check); RetryAndBackoff(check); Lifetime(check); }
        static void Stationary(Action<bool, string> check)
        {
            long now = 1000; var policy = new HoverQueryPolicy(delegate { return now; }, 1000);
            HoverQueryPolicy.Ticket ticket;
            check(policy.TryAdmit(Key(), 150, out ticket), "A newly entered taskbar point admits one speculative lookup");
            HoverQueryPolicy.Ticket unused;
            check(!policy.TryAdmit(Key(), 150, out unused), "A pending same-point lookup is coalesced");
            policy.Complete(ticket, HoverQueryPolicy.Completion.Success);
            bool quiet = true;
            for (int i = 0; i < 2000; i++) { now += 200; quiet &= !policy.TryAdmit(Key(), 150, out unused); }
            now += 24L * 60 * 60 * 1000;
            check(quiet && !policy.TryAdmit(Key(), 150, out unused), "Thousands of stationary polls and overnight clock advance do not refresh a successful hover");
            policy.Leave();
            check(policy.TryAdmit(Key(), 150, out ticket), "A real leave and same-coordinate reentry can rewarm the taskbar");
            policy.Complete(ticket, HoverQueryPolicy.Completion.Success); policy.Dispose();
        }
        static void FloorsAndKeys(Action<bool, string> check)
        {
            long now = 1000; var policy = new HoverQueryPolicy(delegate { return now; }, 1000);
            HoverQueryPolicy.Ticket first, next;
            policy.TryAdmit(Key(), 150, out first); policy.Complete(first, HoverQueryPolicy.Completion.Success);
            now = 1149; check(!policy.TryAdmit(Key(x: 11), 150, out next), "Moving within a taskbar retains the speculative rate floor");
            now = 1150; check(policy.TryAdmit(Key(x: 11), 150, out next), "A changed point admits one lookup after the rate floor"); policy.Complete(next, HoverQueryPolicy.Completion.Success);
            var origin = new TaskbarTargets.Target { Taskbar = new IntPtr(1), Window = new IntPtr(42), Process = 7, ProcessStarted = 99,
                Button = new TaskbarMover.Click { AppId = "Neutral.App" } };
            now = 1300; check(policy.TryAdmit(Key(9, 11, true, origin), 100, out next), "Native preview appearance is a distinct hover source even at an unchanged point"); policy.Complete(next, HoverQueryPolicy.Completion.Success);
            now = 1400; check(!policy.TryAdmit(Key(9, 11, true, origin), 100, out next), "A stable preview is not traversed repeatedly");
            origin.Button.AppId = "Neutral.Other";
            check(policy.TryAdmit(Key(9, 11, true, origin), 100, out next), "Changing the logical preview origin permits a fresh lookup"); policy.Complete(next, HoverQueryPolicy.Completion.Success);
            now = 1500; check(policy.TryAdmit(Key(10, 11, true, origin), 100, out next), "A replacement preview HWND receives its own lookup"); policy.Complete(next, HoverQueryPolicy.Completion.Success);
            policy.Leave();
            check(!policy.TryAdmit(Key(10, 11, true, origin), 100, out next), "Leaving does not erase the current rate floor");
            now = 1600; check(policy.TryAdmit(Key(10, 11, true, origin), 100, out next), "A later same-preview reentry can refresh exactly once"); policy.Dispose();
            now = 1000; policy = new HoverQueryPolicy(delegate { return now; }, 1000);
            policy.TryAdmit(Key(3), 150, out first);
            check(policy.TryAdmit(Key(4), 150, out next), "A new native source immediately replaces queued stale hover work despite the cadence floor");
            check(!policy.TryAdmit(Key(4, 11), 150, out first), "A changed point on that same source still respects its cadence floor"); policy.Dispose();
        }
        static void RetryAndBackoff(Action<bool, string> check)
        {
            long now = 1000; var miss = new HoverQueryPolicy(delegate { return now; }, 1000);
            HoverQueryPolicy.Ticket ticket, retry;
            miss.TryAdmit(Key(), 150, out ticket); miss.Complete(ticket, HoverQueryPolicy.Completion.Miss);
            now = 1349; check(!miss.TryAdmit(Key(), 150, out retry), "A newly empty provider is given a delay before its one automatic retry");
            now = 1350; check(miss.TryAdmit(Key(), 150, out retry), "A stationary first miss gets one bounded recovery retry"); miss.Complete(retry, HoverQueryPolicy.Completion.Miss);
            now += 1000000; check(!miss.TryAdmit(Key(), 150, out retry), "A second miss cannot create a perpetual stationary retry loop"); miss.Dispose();
            now = 1000; var failure = new HoverQueryPolicy(delegate { return now; }, 1000);
            failure.TryAdmit(Key(), 150, out ticket); failure.Complete(ticket, HoverQueryPolicy.Completion.Failure);
            now = 1999; check(!failure.TryAdmit(Key(2, 30), 150, out retry), "Provider failure cooldown applies across speculative source and point changes");
            now = 2000; check(failure.TryAdmit(Key(), 150, out retry), "A timed-out stationary query can use its one retry after cooldown");
            failure.Complete(retry, HoverQueryPolicy.Completion.Failure);
            now = 4000; check(!failure.TryAdmit(Key(), 150, out ticket), "Repeated stationary provider failure becomes quiescent after two attempts");
            check(failure.TryAdmit(Key(x: 12), 150, out ticket), "A genuinely changed point can start a new bounded hover attempt"); failure.Complete(ticket, HoverQueryPolicy.Completion.Failure);
            now = 7999; check(!failure.TryAdmit(Key(x: 13), 150, out retry), "The third provider failure receives four seconds of speculative backoff");
            now = 8000; failure.TryAdmit(Key(x: 13), 150, out retry); failure.Complete(retry, HoverQueryPolicy.Completion.Failure);
            now = 15999; check(!failure.TryAdmit(Key(x: 14), 150, out ticket), "The failure backoff grows to a bounded eight seconds");
            now = 16000; check(failure.TryAdmit(Key(x: 14), 150, out ticket), "The bounded backoff eventually permits fresh changed-point speculation");
            failure.Complete(ticket, HoverQueryPolicy.Completion.Success); now = 16150;
            check(failure.TryAdmit(Key(x: 15), 150, out retry), "A successful current lookup restores ordinary changed-point responsiveness"); failure.Dispose();
            now = 1000; var leave = new HoverQueryPolicy(delegate { return now; }, 1000);
            leave.TryAdmit(Key(), 150, out ticket); leave.Complete(ticket, HoverQueryPolicy.Completion.Failure);
            leave.Leave(); now = 1999;
            check(!leave.TryAdmit(Key(2, 30), 150, out retry), "Actual leave/reentry does not bypass a failing provider's cooldown"); leave.Dispose();
            now = 1000; var queued = new HoverQueryPolicy(delegate { return now; }, 1000);
            queued.TryAdmit(Key(), 150, out ticket); now = 1150; queued.TryAdmit(Key(x: 11), 150, out retry);
            queued.Complete(ticket, HoverQueryPolicy.Completion.Failure);
            check(!queued.MayStart(retry), "A queued hover cannot bypass a provider failure learned after its admission");
            queued.Complete(retry, HoverQueryPolicy.Completion.Deferred); queued.Dispose();
        }
        static void Lifetime(Action<bool, string> check)
        {
            long now = 1000; var policy = new HoverQueryPolicy(delegate { return now; }, 1000); HoverQueryPolicy.Ticket ticket;
            policy.TryAdmit(Key(), 150, out ticket); now = 999;
            check(!policy.MayStart(ticket), "A backwards monotonic clock fails speculative startup closed");
            now = 1000000; policy.Leave(); check(!policy.TryAdmit(Key(), 150, out ticket), "Clock failure cannot be cleared by source churn or leave/reentry"); policy.Dispose();
            now = 1000; policy = new HoverQueryPolicy(delegate { return now; }, 1000); policy.TryAdmit(Key(), 150, out ticket); policy.Dispose();
            check(!policy.MayStart(ticket) && !policy.TryAdmit(Key(x: 99), 150, out ticket), "Disposed speculative policy cannot start or admit provider work");
        }
    }
}
