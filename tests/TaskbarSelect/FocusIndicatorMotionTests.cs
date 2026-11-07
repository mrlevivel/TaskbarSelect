using System;

namespace TaskbarSelect
{
    internal static class FocusIndicatorMotionTests
    {
        internal static void Run(Action<bool, string> check)
        {
            Timeline(check);
            Lifecycle(check);
            Preference(check);
            ClockBounds(check);
        }

        static bool Near(double value, double expected) { return Math.Abs(value - expected) < 0.000000001; }

        static void Timeline(Action<bool, string> check)
        {
            var motion = new FocusIndicatorMotion(delegate { return true; });
            check(FocusIndicatorMotion.RevealMilliseconds == 420 && FocusIndicatorMotion.SettleMilliseconds == 180 && FocusIndicatorMotion.TotalMilliseconds == 600,
                "The designed reveal gives its traveling highlight 420ms followed by a 180ms settling tail");
            check(!motion.Visible && !motion.Active && motion.Value(100) == 0 && motion.Strength == 0,
                "A new focus border has no active motion or visible progress");
            motion.Show(1000);
            check(motion.Visible && motion.Active && motion.Value(1000) == 0 && motion.Strength == 1,
                "A newly shown border starts at exactly zero without changing visibility");
            check(Near(motion.Value(1105), (1 - Math.Sqrt(.5)) / 2) && motion.Strength == 1 && motion.Active,
                "A quarter of the reveal travels gently instead of racing toward completion");
            check(Near(motion.Value(1210), .5) && motion.Strength == 1 && motion.Active,
                "At half the reveal duration the eased border has filled exactly half its perimeter");
            check(Near(motion.Value(1315), (1 + Math.Sqrt(.5)) / 2) && motion.Strength == 1 && motion.Active,
                "The reveal slows toward completion after its midpoint");
            check(motion.Value(1419) < 1 && motion.Strength == 1 && motion.Active,
                "The border remains active just before the reveal deadline");
            check(motion.Value(1420) == 1 && motion.Strength == 1 && motion.Active && motion.Visible,
                "The complete border starts settling at exactly 420ms while its highlight timer remains active");
            check(motion.Value(1465) == 1 && Near(motion.Strength, .84375) && motion.Active,
                "The completed reveal retains a softly decreasing highlight one quarter into its settling tail");
            check(motion.Value(1510) == 1 && Near(motion.Strength, .5) && motion.Active,
                "Half of the settling tail retains exactly half the reveal highlight strength");
            check(motion.Value(1555) == 1 && Near(motion.Strength, .15625) && motion.Active,
                "The highlight approaches the final frame smoothly instead of snapping off");
            check(motion.Value(1599) == 1 && motion.Strength > 0 && motion.Strength < .001 && motion.Active,
                "The last settling frame stays active with a bounded near-zero highlight");
            check(motion.Value(1600) == 1 && motion.Strength == 0 && !motion.Active && motion.Visible,
                "At exactly 600ms the border keeps its settled pixels and stops reveal work");
            check(motion.Value(100000) == 1 && motion.Strength == 0 && !motion.Active,
                "Completed border progress and zero reveal strength remain static during idle updates");

            motion.Hide(); motion.Show(0);
            double previous = 0, previousStrength = 1;
            for (int millisecond = 0; millisecond <= 650; millisecond++) {
                double value = motion.Value(millisecond);
                check(value >= previous && value >= 0 && value <= 1 && motion.Strength >= 0 && motion.Strength <= previousStrength &&
                    (millisecond < 420 ? value < 1 && motion.Strength == 1 : value == 1) && motion.Active == (millisecond < 600),
                    "Every reveal and settling frame is bounded, monotonic and active only within its fixed lifetime at millisecond " + millisecond);
                previous = value; previousStrength = motion.Strength;
            }
            check(!motion.Active && motion.Visible && motion.Strength == 0, "The full deterministic timeline ends with a static visible border");
        }

        static void Lifecycle(Action<bool, string> check)
        {
            var motion = new FocusIndicatorMotion(delegate { return true; });
            motion.Show(0); double before = motion.Value(60);
            motion.Show(60);
            check(Near(motion.Value(60), before) && motion.Active,
                "Repeated Show while visible preserves the in-flight progress rather than restarting");
            motion.Show(120); motion.Show(420);
            check(motion.Value(420) == 1 && motion.Strength == 1 && motion.Active,
                "Idle/theme/recolor updates keep the original reveal and settling deadlines");
            motion.Show(510);
            check(motion.Value(510) == 1 && Near(motion.Strength, .5) && motion.Active,
                "Repeated Show during settling neither restarts nor skips the final highlight fade");
            check(motion.Value(600) == 1 && motion.Strength == 0 && !motion.Active,
                "Repeated presentation still finishes all reveal work at the original 600ms deadline");
            motion.Show(1000);
            check(motion.Value(1000) == 1 && motion.Strength == 0 && !motion.Active,
                "A static visible border cannot restart without a visibility transition");

            foreach (string reason in new[] { "hide", "pause", "Off", "foreground change", "drag/resize" }) {
                motion.Hide(); motion.Show(1000); motion.Value(1050); motion.Hide(); motion.Hide();
                check(!motion.Active && !motion.Visible && motion.Value(1100) == 0 && motion.Strength == 0,
                    "Cancellation resets motion immediately and idempotently on " + reason);
                motion.Show(2000);
                check(motion.Active && motion.Visible && motion.Value(2000) == 0 && motion.Strength == 1 && Near(motion.Value(2210), .5),
                    "A fresh show or settled-move return starts a new reveal after " + reason);
            }
            motion.Hide(); motion.Show(0); motion.Value(510); motion.Hide();
            check(!motion.Visible && !motion.Active && motion.Strength == 0 && motion.Value(550) == 0,
                "Cancellation during the settling tail immediately removes its highlight and prevents a late upload");
            motion.Hide();
            check(motion.Value(Int64.MaxValue) == 0 && !motion.Active && motion.Strength == 0,
                "Late timer callbacks after cancellation cannot revive a border");
        }

        static void Preference(Action<bool, string> check)
        {
            bool allowed = true; int reads = 0;
            var motion = new FocusIndicatorMotion(delegate { reads++; return allowed; });
            motion.Show(0);
            check(reads == 1 && motion.Active,
                "Show consults the injectable animation policy exactly once");
            motion.Value(30); allowed = false;
            check(motion.Value(45) == 1 && motion.Strength == 0 && !motion.Active && motion.Visible,
                "Disabling Windows animation policy during a reveal immediately finishes the static border");
            int after = reads; allowed = true; motion.Show(100); motion.Value(1000);
            check(!motion.Active && reads == after && motion.Value(1000) == 1 && motion.Strength == 0,
                "Re-enabling animation cannot restart or poll an already static visible border");
            motion.Hide(); motion.Show(2000);
            check(motion.Active && motion.Value(2000) == 0 && motion.Strength == 1,
                "The next true visibility transition uses the new animation preference");
            motion.Value(2510); allowed = false;
            check(motion.Value(2520) == 1 && motion.Strength == 0 && !motion.Active && motion.Visible,
                "Reduced motion during settling immediately removes the remaining highlight without hiding the complete cue");

            var reduced = new FocusIndicatorMotion(delegate { return false; });
            reduced.Show(100);
            check(reduced.Visible && !reduced.Active && reduced.Value(100) == 1 && reduced.Strength == 0,
                "Reduced-animation policy presents a complete border with no live animation");

            int failures = 0;
            var unavailable = new FocusIndicatorMotion(delegate { failures++; throw new InvalidOperationException("fixture"); });
            unavailable.Show(0);
            check(unavailable.Visible && !unavailable.Active && unavailable.Value(0) == 1 && unavailable.Strength == 0 && failures == 1,
                "An unavailable preference read safely gives a static border without repeated failures");
            bool failLater = false;
            var interrupted = new FocusIndicatorMotion(delegate { if (failLater) throw new InvalidOperationException("fixture"); return true; });
            interrupted.Show(0); interrupted.Value(30); failLater = true;
            check(interrupted.Value(60) == 1 && interrupted.Strength == 0 && !interrupted.Active,
                "A preference read failing during motion safely completes instead of breaking UI handling");
            bool refused = false;
            try { new FocusIndicatorMotion(null); } catch (ArgumentNullException) { refused = true; }
            check(refused, "A missing animation policy is rejected explicitly");
        }

        static void ClockBounds(Action<bool, string> check)
        {
            var motion = new FocusIndicatorMotion(delegate { return true; });
            motion.Show(1000); double progress = motion.Value(1210);
            check(motion.Value(1020) == progress && motion.Value(-100) == progress && motion.Strength == 1 && motion.Active,
                "Backward clock samples never rewind an in-flight reveal");
            check(motion.Value(1510) == 1 && Near(motion.Strength, .5) && motion.Active && motion.Value(1450) == 1 && Near(motion.Strength, .5),
                "Backward samples during settling cannot bring a faded reveal highlight back");
            check(motion.Value(1600) == 1 && motion.Strength == 0 && !motion.Active,
                "An increasing clock still completes after backward samples");
            motion.Hide(); motion.Show(Int64.MaxValue - 600);
            check(Near(motion.Value(Int64.MaxValue - 390), .5) && motion.Strength == 1 &&
                motion.Value(Int64.MaxValue - 90) == 1 && Near(motion.Strength, .5) && motion.Value(Int64.MaxValue) == 1 && motion.Strength == 0 && !motion.Active,
                "Near-maximum timestamps preserve millisecond accuracy without floating timestamp rounding");
            motion.Hide(); motion.Show(Int64.MinValue);
            check(Near(motion.Value(Int64.MinValue + 210), .5) && motion.Strength == 1 &&
                motion.Value(Int64.MinValue + 510) == 1 && Near(motion.Strength, .5) && motion.Value(Int64.MinValue + 600) == 1 && motion.Strength == 0 && !motion.Active,
                "Near-minimum timestamps preserve exact elapsed intervals");
            motion.Hide(); motion.Show(Int64.MinValue);
            check(motion.Value(Int64.MaxValue) == 1 && motion.Strength == 0 && !motion.Active,
                "An extreme monotonic jump completes without signed elapsed-time overflow");
            motion.Hide(); motion.Show(-210);
            check(Near(motion.Value(0), .5) && motion.Value(210) == 1 && motion.Strength == 1 &&
                motion.Value(300) == 1 && Near(motion.Strength, .5) && motion.Value(390) == 1 && motion.Strength == 0 && !motion.Active,
                "A clock crossing zero keeps continuous bounded reveal progress");
        }
    }
}
