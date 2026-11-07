using System;

namespace TaskbarSelect
{
    // The UI supplies milliseconds from one monotonic clock. This helper never
    // starts timers, reads Windows settings, or modifies a window itself.
    internal sealed class FocusIndicatorMotion
    {
        internal const int RevealMilliseconds = 420;
        internal const int SettleMilliseconds = 180;
        internal const int TotalMilliseconds = RevealMilliseconds + SettleMilliseconds;
        readonly Func<bool> animationsAllowed;
        long started, latest;
        double value;

        internal FocusIndicatorMotion(Func<bool> animationsAllowed)
        {
            if (animationsAllowed == null) throw new ArgumentNullException("animationsAllowed");
            this.animationsAllowed = animationsAllowed;
        }

        internal bool Active { get; private set; }
        internal bool Visible { get; private set; }
        internal double Strength { get; private set; }

        internal void Show(long now)
        {
            // Repainting or changing a palette while shown must not repeatedly
            // start a new border. A new reveal requires Hide followed by Show.
            if (Visible) { Value(now); return; }
            Visible = true;
            started = latest = now;
            Active = Allowed();
            value = Active ? 0 : 1;
            Strength = Active ? 1 : 0;
        }

        internal double Value(long now)
        {
            if (!Visible) return 0;
            if (!Active) return 1;
            if (!Allowed()) { Active = false; value = 1; Strength = 0; return value; }

            // Backward samples cannot rewind a reveal. Unsigned subtraction
            // after signed ordering also handles large clocks without overflow
            // or losing short intervals to double-precision timestamp rounding.
            if (now > latest) latest = now;
            ulong elapsed = unchecked((ulong)latest - (ulong)started);
            if (elapsed >= TotalMilliseconds) { Active = false; value = 1; Strength = 0; return value; }
            if (elapsed >= RevealMilliseconds) {
                value = 1;
                double settling = (elapsed - RevealMilliseconds) / (double)SettleMilliseconds;
                Strength = 1 - settling * settling * (3 - 2 * settling);
                return value;
            }
            // Keep the traversal legible: the old cubic ease-out consumed most
            // of the contour before the first few budgeted Energy frames.
            value = .5 - .5 * Math.Cos(Math.PI * elapsed / RevealMilliseconds);
            Strength = 1;
            return value;
        }

        internal void Hide()
        {
            Visible = Active = false;
            value = 0;
            Strength = 0;
            started = latest = 0;
        }

        bool Allowed()
        {
            // A failed optional preference read must yield a static cue rather
            // than enabling motion or breaking focus/movement handling.
            try { return animationsAllowed(); } catch (Exception) { return false; }
        }
    }
}
