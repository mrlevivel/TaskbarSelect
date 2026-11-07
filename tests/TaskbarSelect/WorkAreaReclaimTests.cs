using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace TaskbarSelect
{
    // All monitor, bar, mutation, journal and monotonic-time operations are
    // injected. These tests do not inspect or change the Windows desktop.
    internal static class WorkAreaReclaimTests
    {
        static void Tick(WorkAreaGuardTests.Fixture f, int count = 1)
        { for (int i = 0; i < count; i++) { f.Clock += 1000; f.Update(); } }
        static WorkAreaGuardTests.Fixture Reset()
        {
            var f = new WorkAreaGuardTests.Fixture(); f.Update();
            f.Platform.Info.Work = f.Original; f.Update();
            return f;
        }
        internal static int RunPureTest()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool value, string name) {
                if (!value) throw new Exception(name); count++;
            };
            try {
                var exact = Reset(); string journal = new JavaScriptSerializer().Serialize(exact.Records);
                Tick(exact, 4); exact.Clock += 999; exact.Update();
                check(exact.Platform.Sets.Count == 1 && exact.Areas.SnapTargets.Count == 0,
                    "Recovery waits through 4999 continuously observed monotonic milliseconds");
                exact.Clock++; exact.Update();
                check(exact.Platform.Sets.Count == 2 && exact.Areas.SnapTargets.Count == 1 &&
                    AreaPolicy.Equal(exact.Platform.Info.Work, exact.Records[0].Applied),
                    "Exact known reservation gets one recovery expansion at the five-second boundary and verified snap publication");
                bool followsReadback = true;
                for (int i = 0; i < 1000; i++) {
                    exact.Platform.Info.Work = i % 2 == 0 ? exact.Original : exact.Records[0].Applied;
                    exact.Bar.Handle = i % 3 + 1; exact.Clock += 1000; exact.Update();
                    followsReadback &= exact.Areas.SnapTargets.Count == (i % 2 == 0 ? 0 : 1);
                }
                check(exact.Platform.Sets.Count == 2 && followsReadback,
                    "One thousand work/HWND bounces cannot replenish recovery or publish an unverified area");
                check(exact.Saved.Count == 1 && new JavaScriptSerializer().Serialize(exact.Records) == journal,
                    "Recovery and all later polling preserve the exact first journal and Original baseline");

                var ignored = Reset(); ignored.Platform.Apply = false; Tick(ignored, 5);
                for (int i = 0; i < 1000; i++) Tick(ignored);
                check(ignored.Platform.Sets.Count == 2 && ignored.Areas.SnapTargets.Count == 0 && ignored.Saved.Count == 1,
                    "Accepted-but-ignored recovery consumes the last expansion attempt and never authorizes snap");

                var refused = Reset(); refused.Platform.Denied = true; bool refusal = false;
                try { Tick(refused, 5); } catch (InvalidOperationException) { refusal = true; }
                refused.Platform.Denied = false;
                for (int i = 0; i < 1000; i++) {
                    refused.Bar.Handle = i % 3 + 1;
                    refused.Platform.Info.Monitor.Bottom = i % 2 == 0 ? 1081 : 1080;
                    refused.Clock += 1000; refused.Update();
                }
                check(refusal && refused.Platform.Sets.Count == 2 && refused.Records.Count == 1 && refused.Saved.Count == 1,
                    "Refused recovery spends budget before Windows and survives HWND/geometry bouncing without retries");

                var gates = new Action<WorkAreaGuardTests.Fixture>[] {
                    delegate(WorkAreaGuardTests.Fixture f) { f.Platform.Info.Work.Right--; },
                    delegate(WorkAreaGuardTests.Fixture f) { f.Platform.Info.Work.Bottom--; },
                    delegate(WorkAreaGuardTests.Fixture f) { f.Platform.Info.Monitor.Bottom++; },
                    delegate(WorkAreaGuardTests.Fixture f) { f.Platform.Info.Flags = 1; },
                    delegate(WorkAreaGuardTests.Fixture f) { f.Display.Primary = true; },
                    delegate(WorkAreaGuardTests.Fixture f) { f.Displays.Add(new Display { Device = "ambiguous-device", Identity = f.Display.Identity }); },
                    delegate(WorkAreaGuardTests.Fixture f) { f.Platform.Owned = false; },
                    delegate(WorkAreaGuardTests.Fixture f) { f.Platform.BarBounds.Top--; },
                    delegate(WorkAreaGuardTests.Fixture f) { f.Platform.Info.Device = "different-device"; }
                };
                foreach (var change in gates) {
                    var f = Reset(); change(f); Tick(f, 20);
                    check(f.Platform.Sets.Count == 1 && f.Saved.Count == 1 && f.Records.Count == 1 && f.Areas.SnapTargets.Count == 0,
                        "Foreign margin, monitor, primary, ambiguity, ownership, edge or device mismatch cannot recover");
                }

                var handle = Reset(); Tick(handle, 3); handle.Bar.Handle = 2; Tick(handle);
                Tick(handle, 4);
                check(handle.Platform.Sets.Count == 1, "A changed native taskbar handle restarts the complete stability wait");
                Tick(handle); check(handle.Platform.Sets.Count == 2, "Stable replacement handle can finish the same unspent claim wait once");

                var unreadable = Reset(); Tick(unreadable, 3); unreadable.Platform.Readable = false; Tick(unreadable);
                unreadable.Platform.Readable = true; Tick(unreadable); Tick(unreadable, 4);
                check(unreadable.Platform.Sets.Count == 1, "An unreadable sample breaks continuity instead of counting as stable time");
                Tick(unreadable); check(unreadable.Platform.Sets.Count == 2, "Fresh readable samples must rebuild five seconds of stability");

                var absent = Reset(); Tick(absent, 3); absent.Displays.Clear(); Tick(absent);
                absent.Displays.Add(absent.Display); Tick(absent); Tick(absent, 4);
                check(absent.Platform.Sets.Count == 1, "Temporary missing identity breaks observation continuity without rearming ownership");
                Tick(absent); check(absent.Platform.Sets.Count == 2, "Unique return still permits only the same one recovery budget");

                var rollback = Reset(); Tick(rollback, 3); rollback.Clock -= 2000; rollback.Update(); Tick(rollback, 4);
                check(rollback.Platform.Sets.Count == 1, "A monotonic clock rollback restarts the stability wait");
                Tick(rollback); check(rollback.Platform.Sets.Count == 2, "Rollback does not permanently disable an unspent recovery");
                rollback.Clock = -100; rollback.Platform.Info.Work = rollback.Original; rollback.Update();
                rollback.Clock = 100000; Tick(rollback, 10);
                check(rollback.Platform.Sets.Count == 2, "Clock reset after consumption never replenishes the spent budget");

                var stall = Reset(); Tick(stall, 3); stall.Clock += 10000; stall.Update(); Tick(stall, 4);
                check(stall.Platform.Sets.Count == 1, "A long unobserved time gap cannot masquerade as five stable seconds");
                Tick(stall); check(stall.Platform.Sets.Count == 2, "Continuous post-stall observations can finish the unspent wait");

                var changing = Reset();
                for (int i = 0; i < 1000; i++) { changing.Bar.Handle = i % 2 + 1; Tick(changing); }
                check(changing.Platform.Sets.Count == 1, "One thousand unstable handle snapshots cannot prematurely recover");
                Tick(changing, 5); check(changing.Platform.Sets.Count == 2, "An unstable period does not create or consume extra attempts");

                var edge = Reset(); Tick(edge, 3); edge.Platform.BarBounds.Top--; Tick(edge);
                edge.Platform.BarBounds.Top++; Tick(edge); Tick(edge, 4);
                check(edge.Platform.Sets.Count == 1, "A changed or unproved edge restarts stability");
                Tick(edge); check(edge.Platform.Sets.Count == 2, "The exact proven edge must remain stable for its own five-second wait");

                var reentrant = Reset(); int reentries = 0;
                reentrant.Platform.BeforeSet = delegate { reentries++; reentrant.Update(); };
                Tick(reentrant, 5);
                check(reentrant.Platform.Sets.Count == 2 && reentries == 1,
                    "Recovery budget is consumed before Set so reentrant refresh cannot issue a third request");

                var dock = new WorkAreaGuardTests.Fixture(); dock.Platform.Info.Work.Left += 30; dock.Update();
                var dockOriginal = dock.Records[0].Original; dock.Platform.Info.Work = dockOriginal; dock.Update(); Tick(dock, 5);
                check(dock.Platform.Sets.Count == 2 && dock.Platform.Info.Work.Left == 1030 && dock.Records[0].Original.Left == 1030,
                    "Known Original recovery preserves an unrelated appbar margin in both applied area and baseline");

                var paused = Reset(); Tick(paused, 5); paused.Update(false);
                check(paused.Records.Count == 0 && paused.Platform.Sets.Count == 3 && AreaPolicy.Equal(paused.Platform.Info.Work, paused.Original),
                    "Normal Pause restores and retires a recovered claim through existing ownership checks");
                paused.Update(); paused.Platform.Info.Work = paused.Original; paused.Update(); Tick(paused, 5);
                check(paused.Platform.Sets.Count == 5 && paused.Records.Count == 1,
                    "Explicit retirement and re-enable creates one fresh ownership period with its own bounded recovery");

                var wakes = Reset(); string wakeJournal = new JavaScriptSerializer().Serialize(wakes.Records);
                check(wakes.Areas.BeginWakeEpoch(1), "First positive authoritative wake establishes an epoch");
                wakes.Update(); Tick(wakes, 5);
                bool threeWakes = wakes.Platform.Sets.Count == 2;
                for (long sequence = 2; sequence <= 3; sequence++) {
                    wakes.Clock += WorkAreas.WakeEpochCooldownMilliseconds;
                    wakes.Platform.Info.Work = wakes.Original;
                    threeWakes &= wakes.Areas.BeginWakeEpoch(sequence);
                    wakes.Update(); Tick(wakes, 5);
                    threeWakes &= wakes.Platform.Sets.Count == sequence + 1;
                }
                check(threeWakes && wakes.Saved.Count == 1 && new JavaScriptSerializer().Serialize(wakes.Records) == wakeJournal,
                    "Three genuine rate-separated wakes each permit one recovery while retaining the exact first journal");
                wakes.Platform.Info.Work = wakes.Original;
                wakes.Clock += 1000;
                check(!wakes.Areas.BeginWakeEpoch(0) && !wakes.Areas.BeginWakeEpoch(3) && !wakes.Areas.BeginWakeEpoch(2) && !wakes.Areas.BeginWakeEpoch(4),
                    "Invalid, duplicate, decreasing and rapid-flicker wake sequences cannot rearm");
                wakes.Clock += WorkAreas.WakeEpochCooldownMilliseconds;
                check(!wakes.Areas.BeginWakeEpoch(4), "A cooldown-rejected wake sequence is consumed and cannot be replayed later");
                Tick(wakes, 20); check(wakes.Platform.Sets.Count == 4, "Clock passage and old sequence replay never replenish spent wake allowance");
                check(wakes.Areas.BeginWakeEpoch(5), "A genuinely later sequence can renew allowance after cooldown");
                wakes.Update(); Tick(wakes, 4);
                check(wakes.Platform.Sets.Count == 4, "Accepted wake restarts full five-second stability rather than borrowing old observations");
                Tick(wakes); check(wakes.Platform.Sets.Count == 5, "Later accepted wake can complete its own one bounded recovery");
                long acceptedAt = wakes.Clock;
                wakes.Clock -= 10000;
                check(!wakes.Areas.BeginWakeEpoch(6), "Clock rollback cannot satisfy the global wake cooldown");
                wakes.Clock = acceptedAt + WorkAreas.WakeEpochCooldownMilliseconds;
                check(!wakes.Areas.BeginWakeEpoch(6) && wakes.Areas.BeginWakeEpoch(7),
                    "Rollback-rejected sequence remains consumed while a fresh authoritative wake can recover later");

                var changedCommit = Reset(); Tick(changedCommit, 4);
                int changedAt = changedCommit.Platform.MonitorReads + 2;
                changedCommit.Platform.DuringMonitorRead = delegate(int reads) { if (reads == changedAt) changedCommit.Platform.Info.Work.Right--; };
                Tick(changedCommit);
                check(changedCommit.Platform.Sets.Count == 1 && changedCommit.Saved.Count == 1,
                    "A foreign work-area change at the final native read cancels commit without spending allowance");
                changedCommit.Platform.DuringMonitorRead = null; changedCommit.Platform.Info.Work = changedCommit.Original;
                changedCommit.Update(); Tick(changedCommit, 5);
                check(changedCommit.Platform.Sets.Count == 2, "Cancelled stale commit can build a new stable snapshot with the same unspent allowance");

                var phaseCommit = Reset(); Tick(phaseCommit, 4);
                int phaseAt = phaseCommit.Platform.MonitorReads + 2;
                phaseCommit.Platform.DuringMonitorRead = delegate(int reads) {
                    if (reads == phaseAt) { phaseCommit.Platform.DuringMonitorRead = null; phaseCommit.Areas.BeginWakeEpoch(1); }
                };
                Tick(phaseCommit);
                check(phaseCommit.Platform.Sets.Count == 1 && phaseCommit.Areas.DiagnosticSnapshot()["RecoveryExpansionAttempts"] == 0,
                    "An authoritative epoch arriving during commit cannot spend the new phase on an old stable snapshot");
                phaseCommit.Update(); Tick(phaseCommit, 5);
                check(phaseCommit.Platform.Sets.Count == 2, "The new phase retains its allowance for a complete new stability wait");

                var nestedCommit = Reset(); Tick(nestedCommit, 4);
                int nestedAt = nestedCommit.Platform.MonitorReads + 2;
                nestedCommit.Platform.DuringMonitorRead = delegate(int reads) {
                    if (reads == nestedAt) { nestedCommit.Platform.DuringMonitorRead = null; nestedCommit.Update(); }
                };
                Tick(nestedCommit);
                check(nestedCommit.Platform.Sets.Count == 1, "A nested refresh cannot recursively commit or let the old update spend afterward");
                nestedCommit.Update(); Tick(nestedCommit, 5);
                check(nestedCommit.Platform.Sets.Count == 2, "A cancelled nested-update commit restarts observation without consuming budget");

                var pauseCommit = Reset(); Tick(pauseCommit, 4);
                int pauseAt = pauseCommit.Platform.MonitorReads + 2;
                pauseCommit.Platform.DuringMonitorRead = delegate(int reads) {
                    if (reads == pauseAt) { pauseCommit.Platform.DuringMonitorRead = null; pauseCommit.Update(false); }
                };
                Tick(pauseCommit);
                check(pauseCommit.Platform.Sets.Count == 1 && pauseCommit.Records.Count == 0,
                    "A nested Pause retires the original claim and prevents stale expansion afterward");

                var initialChanged = new WorkAreaGuardTests.Fixture();
                initialChanged.DuringSave = delegate { initialChanged.Platform.Info.Monitor.Bottom++; };
                initialChanged.Update();
                check(initialChanged.Platform.Sets.Count == 0 && initialChanged.Records.Count == 1 && initialChanged.Saved.Count == 1,
                    "Initial Save-to-Set geometry change retains the journal but never applies the old rectangle");
                initialChanged.DuringSave = null; initialChanged.Platform.Info.Monitor.Bottom--;
                initialChanged.Update(); Tick(initialChanged, 5);
                check(initialChanged.Platform.Sets.Count == 1 && initialChanged.Saved.Count == 1,
                    "Skipped initial application can later use only the delayed proven recovery path");

                var initialPhase = new WorkAreaGuardTests.Fixture();
                initialPhase.DuringSave = delegate { initialPhase.Areas.BeginWakeEpoch(1); };
                initialPhase.Update();
                check(initialPhase.Platform.Sets.Count == 0 && initialPhase.Records.Count == 1,
                    "Wake phase change during initial journal commit cannot authorize an old initial Set");

                var initialPause = new WorkAreaGuardTests.Fixture();
                initialPause.DuringSave = delegate { initialPause.DuringSave = null; initialPause.Update(false); };
                initialPause.Update();
                check(initialPause.Platform.Sets.Count == 0 && initialPause.Records.Count == 0,
                    "Nested Pause during initial Save prevents mutation after claim retirement");

                Console.WriteLine("PASS: " + count + " injected delayed-reclaim checks; no native, UI, provider, input or filesystem access.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
