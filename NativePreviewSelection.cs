using System;

namespace TaskbarSelect
{
    // Explorer must activate this popup, then a single window after mouse-up.
    // Native event timestamps exclude queued events from before the click.
    internal sealed class NativePreviewSelection
    {
        readonly IntPtr preview;
        readonly uint pressed;
        uint released;
        bool hasRelease, sawPreview, conflicting;
        IntPtr selected;

        internal NativePreviewSelection(IntPtr preview, uint pressed)
        { this.preview = preview; this.pressed = pressed; }

        internal void Release(uint time)
        { released = time; hasRelease = true; }

        internal void Observe(IntPtr window, uint time)
        {
            if (window == IntPtr.Zero || Elapsed(time, pressed) < 0) return;
            if (hasRelease && Elapsed(time, released) > 500) return;
            if (window == preview) {
                if (selected != IntPtr.Zero) conflicting = true;
                sawPreview = true;
                return;
            }
            if (!hasRelease || !sawPreview || Elapsed(time, released) < 0) return;
            if (selected != IntPtr.Zero && selected != window) conflicting = true;
            else selected = window;
        }

        internal IntPtr WindowAt(uint now)
        {
            return hasRelease && sawPreview && !conflicting && Elapsed(now, released) >= 0 && Elapsed(now, released) <= 500
                ? selected : IntPtr.Zero;
        }

        static int Elapsed(uint after, uint before) { return unchecked((int)(after - before)); }
    }
}
