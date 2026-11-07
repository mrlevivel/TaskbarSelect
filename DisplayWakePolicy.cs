using System;

namespace TaskbarSelect
{
    // An external display-power transition, not a work-area/discovery event,
    // grants one new recovery epoch. All times are injected monotonic ticks.
    internal sealed class DisplayWakePolicy
    {
        readonly long minimumOff, cooldown;
        long offSince, lastObserved = -1, lastAccepted = -1, sequence;
        bool off;
        internal long Sequence { get { return sequence; } }
        internal DisplayWakePolicy(long frequency)
        {
            if (frequency <= 0 || frequency > Int64.MaxValue / 60) throw new ArgumentOutOfRangeException("frequency");
            minimumOff = frequency * 2;
            cooldown = frequency * 60;
        }
        internal long Observe(bool registered, int state, long now)
        {
            if (!registered) { off = false; return 0; }
            return Observe(state, now);
        }
        internal long Observe(int state, long now)
        {
            if (now < 0) { off = false; return 0; }
            if (lastObserved >= 0 && now < lastObserved) {
                off = false; lastObserved = now; return 0;
            }
            lastObserved = now;
            if (state < 0 || state > 2) { off = false; return 0; }
            if (state == 0) {
                if (!off) { off = true; offSince = now; }
                return 0;
            }
            bool qualified = state == 1 && off && now - offSince >= minimumOff;
            // Initial On, dim, malformed input and duplicate On never grant recovery.
            off = false;
            if (!qualified || (lastAccepted >= 0 && (now < lastAccepted || now - lastAccepted < cooldown)) ||
                sequence == Int64.MaxValue) return 0;
            lastAccepted = now;
            return ++sequence;
        }
    }
}
