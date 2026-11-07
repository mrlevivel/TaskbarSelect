using System;

namespace TaskbarSelect
{
    // This observes Explorer's focus handoff; it never supplies window identity
    // or permission to move. The caller must revalidate native selection before
    // observing and immediately before consuming a ready handoff.
    internal sealed class NativeTaskbarHandoff
    {
        internal const int StableMilliseconds = 50;
        readonly uint gestureDown;
        IntPtr selectedWindow, replacement;
        uint stableSince;
        bool tracking, consumed, cancelled;

        internal NativeTaskbarHandoff(uint gestureDown) { this.gestureDown = gestureDown; }

        internal static bool WithinDeadline(uint down, uint now)
        {
            int elapsed = unchecked((int)(now - down));
            return elapsed >= 0 && elapsed < TaskbarNativeSelection.GestureMilliseconds;
        }

        internal bool Observe(bool selectionVerified, IntPtr selected, IntPtr foreground,
            bool foregroundIsTaskbar, bool modified, uint now)
        {
            if (modified) cancelled = true;
            if (consumed || cancelled || !WithinDeadline(gestureDown, now)) { Clear(); return false; }
            if (!selectionVerified || selected == IntPtr.Zero || foreground == IntPtr.Zero ||
                foreground == selected || foregroundIsTaskbar) { Clear(); return false; }
            if (!tracking || selectedWindow != selected || replacement != foreground) {
                selectedWindow = selected; replacement = foreground; stableSince = now; tracking = true;
                return false;
            }
            int elapsed = unchecked((int)(now - stableSince));
            return elapsed >= StableMilliseconds;
        }

        internal bool TryConsume(bool selectionVerified, IntPtr selected, IntPtr foreground,
            bool foregroundIsTaskbar, bool modified, uint now)
        {
            if (!Observe(selectionVerified, selected, foreground, foregroundIsTaskbar, modified, now)) return false;
            consumed = true;
            return true;
        }

        void Clear() { selectedWindow = replacement = IntPtr.Zero; stableSince = 0; tracking = false; }
    }
}
