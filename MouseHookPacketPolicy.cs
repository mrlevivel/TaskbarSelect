namespace TaskbarSelect
{
    internal static class MouseHookPacketPolicy
    {
        internal static bool Current(uint generation, uint registeredGeneration, uint packetTime,
            uint registeredTime, uint now, bool registrationFenceActive)
        {
            // Native timestamps wrap every ~49.7 days. Freshness uses unsigned
            // age; the registration lower bound is needed only for the first
            // two monotonic seconds, never as a permanent signed-time comparison.
            return generation == registeredGeneration && unchecked(now - packetTime) <= 2000 &&
                (!registrationFenceActive || unchecked((int)(packetTime - registeredTime)) >= 0);
        }
    }
}
