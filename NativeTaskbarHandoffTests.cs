using System;

namespace TaskbarSelect
{
    internal static class NativeTaskbarHandoffTests
    {
        static readonly IntPtr Target = new IntPtr(10), Replacement = new IntPtr(20), OtherReplacement = new IntPtr(30);

        internal static void Run(Action<bool, string> check)
        {
            Ordering(check);
            Invalidations(check);
            EventChronology(check);
            PendingProof(check);
        }

        static bool Observe(NativeTaskbarHandoff handoff, bool verified, IntPtr foreground, uint now)
        { return handoff.Observe(verified, Target, foreground, false, false, now); }
        static bool Consume(NativeTaskbarHandoff handoff, bool verified, IntPtr foreground, uint now)
        { return handoff.TryConsume(verified, Target, foreground, false, false, now); }

        static void Ordering(Action<bool, string> check)
        {
            var handoff = new NativeTaskbarHandoff(1000);
            check(!Observe(handoff, true, Target, 1100), "Iconic-first transition waits while Task Manager is still foreground");
            check(!Observe(handoff, true, IntPtr.Zero, 1125), "A transient missing foreground cannot start a handoff");
            check(!Observe(handoff, true, Replacement, 1150), "Replacement foreground starts observation without moving");
            check(!Observe(handoff, true, Replacement, 1199), "Replacement foreground has not settled at 49ms");
            check(Observe(handoff, true, Replacement, 1200), "Replacement foreground settles at the exact 50ms boundary");
            check(Consume(handoff, true, Replacement, 1200), "A freshly revalidated stable handoff may be consumed");
            check(!Observe(handoff, true, Replacement, 1250) && !Consume(handoff, true, Replacement, 1250),
                "A consumed gesture cannot authorize a second move");

            handoff = new NativeTaskbarHandoff(1000);
            check(!Observe(handoff, false, Replacement, 1100) && !Observe(handoff, false, Replacement, 1200),
                "Foreground-first transition waits without actual native minimize proof");
            check(!Observe(handoff, true, Replacement, 1225) && !Observe(handoff, true, Replacement, 1274) &&
                Consume(handoff, true, Replacement, 1275), "Foreground-first transition settles only after iconic proof appears");

            handoff = new NativeTaskbarHandoff(1000);
            check(!handoff.Observe(true, Target, Replacement, true, false, 1100) &&
                !handoff.Observe(true, Target, Replacement, true, false, 1200), "An actual taskbar foreground never qualifies");
            check(!Observe(handoff, true, Replacement, 1225) && Consume(handoff, true, Replacement, 1275),
                "A desktop or other non-taskbar Explorer window is an eligible replacement");
        }

        static void Invalidations(Action<bool, string> check)
        {
            foreach (IntPtr interruption in new[] { IntPtr.Zero, Target, OtherReplacement }) {
                var handoff = new NativeTaskbarHandoff(1000);
                Observe(handoff, true, Replacement, 1100);
                check(!Observe(handoff, true, interruption, 1125), "Foreground interruption restarts handoff stability: " + interruption);
                check(!Observe(handoff, true, Replacement, 1150) && !Observe(handoff, true, Replacement, 1199) &&
                    Consume(handoff, true, Replacement, 1200), "Returning foreground needs a fresh full stable interval: " + interruption);
            }
            var changed = new NativeTaskbarHandoff(1000);
            Observe(changed, true, Replacement, 1100);
            check(!Consume(changed, true, OtherReplacement, 1150), "A final foreground change cannot consume an earlier ready observation");
            check(!Observe(changed, true, OtherReplacement, 1199) && Consume(changed, true, OtherReplacement, 1200),
                "A different stable replacement can settle within the original gesture budget");

            var restored = new NativeTaskbarHandoff(1000);
            Observe(restored, true, Replacement, 1100);
            check(!Consume(restored, false, Replacement, 1150), "A target restored or invalidated before admission cannot consume the handoff");
            check(!Observe(restored, true, Replacement, 1175) && Consume(restored, true, Replacement, 1225),
                "Re-established proof must start a new stable interval");

            var modified = new NativeTaskbarHandoff(1000);
            Observe(modified, true, Replacement, 1100);
            check(!modified.Observe(true, Target, Replacement, false, true, 1150) &&
                !Consume(modified, true, Replacement, 1250), "A modifier aborts the gesture even after that modifier is released");

            var expired = new NativeTaskbarHandoff(1000);
            Observe(expired, true, Replacement, 1949);
            check(Observe(expired, true, Replacement, 1999) && !Consume(expired, true, Replacement, 2000),
                "The original gesture expires exactly at one second even after stability was reached");
            check(!Observe(expired, true, OtherReplacement, 2025) && !Consume(expired, true, OtherReplacement, 2100),
                "A replacement change cannot restart an expired gesture budget");
            check(!NativeTaskbarHandoff.WithinDeadline(1000, 999) && NativeTaskbarHandoff.WithinDeadline(1000, 1999) &&
                !NativeTaskbarHandoff.WithinDeadline(1000, 2000), "Future and expired handoff clocks are rejected at both boundaries");
            var wrapped = new NativeTaskbarHandoff(UInt32.MaxValue - 100);
            Observe(wrapped, true, Replacement, UInt32.MaxValue - 25);
            check(Consume(wrapped, true, Replacement, 24), "Stable monotonic handoff timing survives a native tick wrap");
        }

        static void EventChronology(Action<bool, string> check)
        {
            var target = new TaskbarTargets.Target { NeedsNativeSelection = true };
            var click = new TaskbarMover.Click { NativeGesture = true, NativeDownTime = 1000, Taskbar = new IntPtr(50),
                Verification = new TaskbarTargets.Probe { Complete = true, Result = target } };
            check(!TaskbarNativeSelection.ObserveForegroundForClick(click, 999, 1100),
                "A queued pre-click foreground event cannot overwrite current selection evidence");
            check(TaskbarNativeSelection.ObserveForegroundForClick(click, 1050, 1100),
                "A later fresh foreground event remains observable after a stale event was ignored");
            check(!TaskbarNativeSelection.ObserveForegroundForClick(click, 1101, 1100) &&
                !TaskbarNativeSelection.ObserveForegroundForClick(click, 1000, 2001),
                "Future or expired foreground events cannot poison the physical click's selection slot");
            click.PreviewOnly = true;
            check(TaskbarNativeSelection.ObserveForegroundForClick(click, 999, 1100), "Preview selection keeps its own native observation policy");
            click.PreviewOnly = false; click.NativeGesture = false;
            check(TaskbarNativeSelection.ObserveForegroundForClick(click, 999, 1100), "Ordinary injected-input handling is unchanged");
            click.NativeGesture = true; target.NeedsNativeSelection = false;
            check(TaskbarNativeSelection.ObserveForegroundForClick(click, 999, 1100), "Verified ordinary application activation behavior is unchanged");
            target.NeedsNativeSelection = true; click.Verification.Complete = false;
            check(TaskbarNativeSelection.ObserveForegroundForClick(click, 999, 1100),
                "Events before asynchronous target verification keep existing delivery behavior; the native consumer checks chronology later");
            check(TaskbarNativeSelection.ObserveForegroundForClick(null, 999, 1100), "Foreground tracking outside a taskbar click is unchanged");
        }

        static void PendingProof(Action<bool, string> check)
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            var target = new TaskbarTargets.Target {
                Button = new TaskbarMover.Click { AppId = "Microsoft.AutoGenerated.{12345678-1234-1234-1234-1234567890ab}",
                    ButtonBounds = new Native.Rect { Left = 0, Top = 0, Right = 40, Bottom = 40 } },
                Taskbar = new IntPtr(50), Window = Target, Process = 60, ProcessStarted = 70, NeedsNativeSelection = true,
                Captured = now.AddMilliseconds(-100),
                Windows = new[] { new TaskbarTargets.WindowIdentity { Window = Target, Process = 60, ProcessStarted = 70, NeedsNativeSelection = true } }
            };
            var click = new TaskbarMover.Click { AppId = target.Button.AppId, Position = new TaskbarMover.Point { X = 20, Y = 20 },
                Taskbar = target.Taskbar, PriorWindow = Target, PriorProcess = target.Process, NativeGesture = true,
                NativeDownTime = 1000, Time = now.AddMilliseconds(-200), ReleasedAt = now.AddMilliseconds(-150),
                Verification = new TaskbarTargets.Probe { Complete = true, Result = target } };
            check(TaskbarNativeSelection.CanAwaitSelection(click, target, 1200, now),
                "A fully shaped native gesture may wait without admitting movement");
            check(!TaskbarNativeSelection.HasMinimizeProof(click, target, Target, false, 1200, now) &&
                !TaskbarNativeSelection.HasActivationProof(click, target, new IntPtr(90), true, false, 1100, 1200, now) &&
                TaskbarNativeSelection.CanAwaitSelection(click, target, 1200, now),
                "An unrelated intermediate event before iconic state remains pending without proving any move");
            check(TaskbarNativeSelection.HasMinimizeProof(click, target, Target, true, 1250, now.AddMilliseconds(50)),
                "A later actual minimize transition supplies proof while the original gesture is still fresh");
            check(!TaskbarNativeSelection.HasActivationProof(click, target, Target, true, false, 999, 1200, now) &&
                TaskbarNativeSelection.HasActivationProof(click, target, Target, true, false, 1150, 1200, now),
                "A stale event never authorizes movement while a later genuine activation can");
            check(!TaskbarNativeSelection.CanAwaitSelection(click, target, 2000, now.AddMilliseconds(800)),
                "An unrelated event that never resolves cannot extend selection past one second");
            click.NativeGesture = false;
            check(!TaskbarNativeSelection.CanAwaitSelection(click, target, 1200, now), "Injected mouse input cannot enter the native waiting path");
            click.NativeGesture = true; click.Verification.Complete = false;
            check(!TaskbarNativeSelection.CanAwaitSelection(click, target, 1200, now), "Incomplete or retired verification cannot retain a native click");
            click.Verification.Complete = true; target.Windows[0].ProcessStarted++;
            check(!TaskbarNativeSelection.CanAwaitSelection(click, target, 1200, now), "A reused process identity cannot retain a native click");
        }
    }
}
