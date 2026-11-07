using System;
using System.Globalization;

namespace TaskbarSelect
{
    // Evidence policy only: no coordinates, UI, hooks, input, files, or recovery actions.
    // Caller must sample the latest actual native callback counter before ShouldRecover.
    internal sealed class HookDeliveryWatch
    {
        internal enum Observation { Baseline, Idle, Movement, Suspect, Callback, PumpGap, CounterReset, ClockReset, Invalid, Installed, RecoveryAttempted }
        readonly long frequency, maximumGap, maximumSampleAge, cooldown;
        readonly int limit;
        bool sampled, moving, suspect, attempted;
        long lastSample, lastCallbackCount, movementBegan, lastAttempt;
        int movementSamples, samples, resets, recoveries;
        Observation last;

        internal HookDeliveryWatch(long frequency, int maximumAutomaticRecoveries = 3)
        {
            if (frequency <= 0 || frequency > Int64.MaxValue / 20) throw new ArgumentOutOfRangeException("frequency");
            if (maximumAutomaticRecoveries < 1 || maximumAutomaticRecoveries > 8) throw new ArgumentOutOfRangeException("maximumAutomaticRecoveries");
            this.frequency = frequency; maximumGap = frequency * 2; maximumSampleAge = Math.Max(1, frequency / 2);
            cooldown = frequency * 10; limit = maximumAutomaticRecoveries;
        }
        internal bool Suspect { get { return suspect; } }
        internal int RecoveryAttempts { get { return recoveries; } }
        internal int RemainingRecoveries { get { return limit - recoveries; } }
        internal int MovementSampleCount { get { return movementSamples; } }
        internal Observation LastObservation { get { return last; } }
        static int Increment(int value) { return value == Int32.MaxValue ? value : value + 1; }
        void ClearEvidence() { moving = suspect = false; movementSamples = 0; movementBegan = 0; }
        void Baseline(long now, long nativeCallbackCount, Observation reason)
        {
            sampled = true; lastSample = now; lastCallbackCount = nativeCallbackCount;
            ClearEvidence(); resets = Increment(resets); last = reason;
        }
        internal void ResetAfterInstall(long now, long nativeCallbackCount)
        {
            if (now < 0 || nativeCallbackCount < 0) { sampled = false; ClearEvidence(); last = Observation.Invalid; return; }
            // Deliberately preserve the lifetime recovery cap and the last-attempt cooldown.
            Baseline(now, nativeCallbackCount, Observation.Installed);
        }
        internal void Observe(long now, long nativeCallbackCount, bool pointerChanged)
        {
            samples = Increment(samples);
            if (now < 0 || nativeCallbackCount < 0) { sampled = false; ClearEvidence(); last = Observation.Invalid; return; }
            if (!sampled) { Baseline(now, nativeCallbackCount, Observation.Baseline); return; }
            if (now < lastSample) { Baseline(now, nativeCallbackCount, Observation.ClockReset); return; }
            if (now - lastSample > maximumGap) { Baseline(now, nativeCallbackCount, Observation.PumpGap); return; }
            if (nativeCallbackCount < lastCallbackCount) { Baseline(now, nativeCallbackCount, Observation.CounterReset); return; }
            if (nativeCallbackCount > lastCallbackCount) { Baseline(now, nativeCallbackCount, Observation.Callback); return; }
            lastSample = now;
            if (!pointerChanged) { ClearEvidence(); last = Observation.Idle; return; }
            if (!moving) { moving = true; movementBegan = now; movementSamples = 0; }
            movementSamples = Increment(movementSamples);
            suspect = movementSamples >= 3 && now - movementBegan >= frequency;
            last = suspect ? Observation.Suspect : Observation.Movement;
        }
        internal bool ShouldRecover(long now)
        {
            if (!sampled || !suspect || recoveries >= limit || now < lastSample || now - lastSample > maximumSampleAge) return false;
            return !attempted || (now >= lastAttempt && now - lastAttempt >= cooldown);
        }
        // Call exactly once when a safe, evidence-backed recovery is about to be attempted,
        // even if retirement or installation later fails. Never retry an input or an action here.
        internal bool AttemptedRecovery(long now)
        {
            if (!ShouldRecover(now)) return false;
            recoveries++; attempted = true; lastAttempt = now; ClearEvidence(); last = Observation.RecoveryAttempted;
            return true;
        }
        internal string Format(long now)
        {
            long age = !sampled || now < lastSample ? -1 : (long)Math.Min(99999.0, (now - lastSample) * 1000.0 / frequency);
            long remaining = !attempted ? 0 : now < lastAttempt ? 10000 : (long)Math.Max(0.0, 10000.0 - (now - lastAttempt) * 1000.0 / frequency);
            return "suspect=" + (suspect ? "1" : "0") + " samples=" + samples.ToString(CultureInfo.InvariantCulture) +
                " movementSamples=" + movementSamples.ToString(CultureInfo.InvariantCulture) +
                " nativeCallbacks=" + lastCallbackCount.ToString(CultureInfo.InvariantCulture) +
                " sampleAgeMs=" + age.ToString(CultureInfo.InvariantCulture) + " resets=" + resets.ToString(CultureInfo.InvariantCulture) +
                " recoveryAttempts=" + recoveries.ToString(CultureInfo.InvariantCulture) +
                " remaining=" + RemainingRecoveries.ToString(CultureInfo.InvariantCulture) +
                " cooldownMs=" + remaining.ToString(CultureInfo.InvariantCulture) + " observation=" + ((int)last).ToString(CultureInfo.InvariantCulture);
        }
    }
}
