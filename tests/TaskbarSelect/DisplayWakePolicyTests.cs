using System;

namespace TaskbarSelect
{
    internal static class DisplayWakePolicyTests
    {
        internal static int RunPureTest()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool value, string name) { if (!value) throw new Exception(name); count++; };
            try {
                var initial = new DisplayWakePolicy(1000);
                check(initial.Observe(1, 0) == 0 && initial.Observe(1, 100000) == 0, "Initial and repeated On cannot authorize recovery");
                check(initial.Observe(0, 100001) == 0 && initial.Observe(1, 102000) == 0, "Short off interval cannot authorize recovery");
                check(initial.Observe(0, 103000) == 0 && initial.Observe(1, 105000) == 1, "A real off to on grants epoch one at the exact two-second boundary");
                for (int i = 0; i < 1000; i++) check(initial.Observe(1, 105001 + i) == 0, "Duplicate On never rearms");
                initial.Observe(0, 110000);
                check(initial.Observe(1, 120000) == 0 && initial.Sequence == 1, "Rapid power flicker consumes no additional allowance");
                check(initial.Observe(1, 180000) == 0, "A throttled wake cannot be replayed by a later duplicate On");
                initial.Observe(0, 181000);
                check(initial.Observe(1, 183000) == 2, "A later distinct genuine wake grants the next epoch");
                initial.Observe(0, 243000);
                check(initial.Observe(1, 245000) == 3, "Repeated real wakes remain supported in one app lifetime");
                var broken = new DisplayWakePolicy(1000);
                broken.Observe(0, 0); broken.Observe(2, 3000);
                check(broken.Observe(1, 4000) == 0, "Dim interrupts Off to On continuity");
                broken.Observe(0, 5000); broken.Observe(-1, 6000);
                check(broken.Observe(1, 9000) == 0, "Malformed state interrupts continuity");
                broken.Observe(0, 10000); broken.Observe(1, 9999);
                check(broken.Observe(1, 15000) == 0, "Clock rollback cannot leave an armed stale Off");
                broken.Observe(0, 16000); broken.Observe(0, 17000);
                check(broken.Observe(1, 18000) == 1, "Duplicate Off preserves the genuine original off time");
                var negative = new DisplayWakePolicy(1000); negative.Observe(0, 0); negative.Observe(1, -1);
                check(negative.Observe(1, 5000) == 0, "Invalid timestamp breaks continuity");
                var unregistered = new DisplayWakePolicy(1000); unregistered.Observe(true, 0, 0);
                check(unregistered.Observe(false, 1, 3000) == 0 && unregistered.Sequence == 0,
                    "Registration failure cannot authorize a wake");
                check(unregistered.Observe(true, 1, 6000) == 0, "Registration failure also clears an earlier Off");
                var overnight = new DisplayWakePolicy(1000); overnight.Observe(0, 1);
                check(overnight.Observe(1, 24L * 60 * 60 * 1000) == 1, "Long display off supports an ordinary next-day wake");
                bool rejected = false; try { new DisplayWakePolicy(0); } catch (ArgumentOutOfRangeException) { rejected = true; }
                check(rejected, "Zero frequency refused");
                rejected = false; try { new DisplayWakePolicy(Int64.MaxValue); } catch (ArgumentOutOfRangeException) { rejected = true; }
                check(rejected, "Overflowing frequency refused");
                Console.WriteLine("Display wake policy checks passed: " + count); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
