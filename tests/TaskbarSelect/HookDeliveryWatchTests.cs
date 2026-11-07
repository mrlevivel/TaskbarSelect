using System;

namespace TaskbarSelect
{
    internal static class HookDeliveryWatchTests
    {
        static long Sustain(HookDeliveryWatch watch, long baseline, long callbacks)
        {
            watch.ResetAfterInstall(baseline, callbacks);
            for (int i = 1; i <= 5; ++i) watch.Observe(baseline + i * 250, callbacks, true);
            return baseline + 1250;
        }
        internal static void Run(Action<bool, string> check)
        {
            var idle = new HookDeliveryWatch(1000);
            idle.ResetAfterInstall(0, 0);
            for (int i = 1; i <= 100; ++i) {
                idle.Observe(i * 250, 0, false);
                check(!idle.Suspect && !idle.ShouldRecover(i * 250), "An idle pointer never justifies reinstalling a desktop hook");
            }
            var observed = new HookDeliveryWatch(1000);
            observed.ResetAfterInstall(0, 17);
            observed.Observe(250, 17, true); observed.Observe(500, 17, true);
            check(!observed.ShouldRecover(500), "Two short movement samples are not sustained delivery-loss evidence");
            observed.Observe(750, 17, true); observed.Observe(1000, 17, true);
            check(!observed.ShouldRecover(1000), "The movement streak needs a full second from its first changed sample");
            observed.Observe(1250, 17, true);
            check(observed.Suspect && observed.ShouldRecover(1250) && observed.MovementSampleCount == 5,
                "Sustained changed-pointer samples without a single native callback become suspect");
            check(!observed.ShouldRecover(1751), "A stale suspect observation cannot trigger recovery later on a blind timer");
            observed.Observe(1400, 18, true);
            check(!observed.Suspect && !observed.ShouldRecover(1400) && observed.LastObservation == HookDeliveryWatch.Observation.Callback,
                "A fresh native callback clears suspicion even when the pointer also moved");
            observed.Observe(1650, 18, true); observed.Observe(1900, 18, true); observed.Observe(2150, 18, true); observed.Observe(2400, 18, true); observed.Observe(2650, 18, true);
            check(observed.ShouldRecover(2650), "Movement must accumulate a new full evidence interval after a fresh callback");
            observed.Observe(2700, 18, false);
            check(!observed.Suspect && !observed.ShouldRecover(2700), "A stationary sample clears suspect evidence instead of installing during idle");

            var gap = new HookDeliveryWatch(1000);
            Sustain(gap, 0, 0);
            gap.Observe(4000, 0, true);
            check(!gap.Suspect && !gap.ShouldRecover(4000) && gap.LastObservation == HookDeliveryWatch.Observation.PumpGap,
                "A pump gap above two seconds resets the baseline and is never rearm evidence by itself");
            for (int i = 1; i <= 4; ++i) gap.Observe(4000 + i * 250, 0, true);
            check(!gap.ShouldRecover(5000), "A changed sample after a pump gap starts a fresh evidence window");
            gap.Observe(5250, 0, true);
            check(gap.ShouldRecover(5250), "Post-gap sustained movement can independently justify suspicion");
            gap.Observe(5300, 1, true); gap.Observe(5550, 0, true);
            check(!gap.Suspect && gap.LastObservation == HookDeliveryWatch.Observation.CounterReset,
                "A regressed callback counter is a new baseline, not delivery-loss evidence");
            gap.Observe(5000, 0, true);
            check(!gap.Suspect && gap.LastObservation == HookDeliveryWatch.Observation.ClockReset,
                "Backward monotonic samples invalidate the evidence interval");
            gap.Observe(-1, 0, true);
            check(!gap.Suspect && !gap.ShouldRecover(6000) && gap.LastObservation == HookDeliveryWatch.Observation.Invalid,
                "Invalid clocks cannot authorize rearm");
            gap.Observe(6000, -1, true);
            check(!gap.ShouldRecover(6000), "Invalid callback counts cannot authorize rearm");

            var limited = new HookDeliveryWatch(1000);
            check(!limited.AttemptedRecovery(0) && limited.RecoveryAttempts == 0,
                "Calling recovery without current evidence consumes nothing and performs no action");
            long first = Sustain(limited, 0, 0);
            check(limited.AttemptedRecovery(first) && limited.RecoveryAttempts == 1 && !limited.Suspect,
                "One evidence-backed attempt is counted once and consumes its movement evidence");
            check(!limited.AttemptedRecovery(first), "The same evidence cannot cause repeated recovery attempts");
            Sustain(limited, 9000, 0);
            limited.Observe(10500, 0, true); limited.Observe(10750, 0, true); limited.Observe(11000, 0, true); limited.Observe(11249, 0, true);
            check(limited.Suspect && !limited.ShouldRecover(11249) && !limited.AttemptedRecovery(11249),
                "Even new sustained evidence cannot bypass the ten-second recovery cooldown");
            limited.Observe(11250, 0, true);
            check(limited.ShouldRecover(11250) && limited.AttemptedRecovery(11250) && limited.RecoveryAttempts == 2,
                "A second automatic attempt is admitted exactly at the cooldown boundary");
            long third = Sustain(limited, 20000, 0);
            check(third == 21250 && limited.AttemptedRecovery(third) && limited.RecoveryAttempts == 3 && limited.RemainingRecoveries == 0,
                "The third automatic attempt exhausts the lifetime budget");
            long exhausted = Sustain(limited, 40000, 0);
            check(limited.Suspect && !limited.ShouldRecover(exhausted) && !limited.AttemptedRecovery(exhausted) && limited.RecoveryAttempts == 3,
                "A successful-install reset cannot replenish the lifetime cap or allow endless automatic rearming");
            limited.ResetAfterInstall(50000, 9);
            check(!limited.Suspect && limited.RecoveryAttempts == 3 && limited.Format(50000).Contains("remaining=0"),
                "Install reset clears only evidence and keeps the cap visible in numeric diagnostics");

            var scaled = new HookDeliveryWatch(10000000);
            scaled.ResetAfterInstall(0, 0);
            scaled.Observe(2500000, 0, true); scaled.Observe(7500000, 0, true); scaled.Observe(12500000, 0, true);
            check(scaled.ShouldRecover(12500000), "The sustained threshold uses supplied monotonic frequency rather than assuming milliseconds");
            string diagnostic = scaled.Format(12500000);
            check(diagnostic.Contains("nativeCallbacks=0") && diagnostic.Contains("suspect=1") && !diagnostic.Contains("window") && !diagnostic.Contains("path") && !diagnostic.Contains("X=") && !diagnostic.Contains("Y="),
                "Watch diagnostics expose numeric delivery evidence without pointer coordinates or window identity");
        }
    }
}
