using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace TaskbarSelect
{
    // Runs real WorkAreas reconciliation with injected data only. No forms,
    // native platform, profile, filesystem, shell or display access is used.
    internal static class WorkAreaGuardTests
    {
        static Native.Rect Rect(int l, int t, int r, int b)
        { return new Native.Rect { Left = l, Top = t, Right = r, Bottom = b }; }
        internal sealed class FakePlatform : IWorkAreaPlatform
        {
            internal Native.MonitorInfo Info;
            internal Native.Rect BarBounds;
            internal List<Display> Connected;
            internal bool Owned = true, Readable = true, Apply = true, Denied;
            internal int MonitorReads;
            internal int FailRead;
            internal Action<int> DuringMonitorRead;
            internal Action BeforeSet;
            internal readonly List<Native.Rect> Sets = new List<Native.Rect>();
            public bool OwnedHidden(Bar bar) { return Owned; }
            public Native.MonitorInfo? Monitor(Bar bar)
            { MonitorReads++; if (DuringMonitorRead != null) DuringMonitorRead(MonitorReads); return !Readable || MonitorReads == FailRead ? (Native.MonitorInfo?)null : Info; }
            public Native.MonitorInfo? Monitor(string device) { return Readable ? Info : (Native.MonitorInfo?)null; }
            public Native.Rect? Bounds(Bar bar) { return BarBounds; }
            public List<Display> Displays() { return Connected; }
            public void Set(Native.Rect rect)
            {
                if (BeforeSet != null) BeforeSet();
                Sets.Add(rect);
                if (Denied) throw new InvalidOperationException("Injected Set refusal");
                if (Apply) Info.Work = rect;
            }
        }
        internal sealed class Fixture
        {
            internal readonly List<AreaRecord> Records = new List<AreaRecord>();
            internal readonly List<string> Saved = new List<string>();
            internal readonly Display Display = new Display { Device = "neutral-device", Identity = "neutral-identity" };
            internal readonly Bar Bar = new Bar { Handle = 1, Device = "neutral-device" };
            internal readonly Settings Settings = new Settings();
            internal readonly FakePlatform Platform = new FakePlatform();
            internal readonly WorkAreas Areas;
            internal readonly Native.Rect Original;
            internal bool FailSave;
            internal Action DuringSave;
            internal long Clock = 1000;
            internal List<Display> Displays;
            internal Fixture()
            {
                Original = Rect(1000, 0, 2920, 1032);
                Platform.Info = new Native.MonitorInfo { Device = Display.Device, Monitor = Rect(1000, 0, 2920, 1080), Work = Original };
                Platform.BarBounds = Rect(1000, 1032, 2920, 1080);
                Displays = new List<Display> { Display }; Platform.Connected = Displays;
                Settings.HiddenIdentities.Add(Display.Identity);
                Areas = new WorkAreas(Records, Platform, delegate(List<AreaRecord> records) {
                    if (FailSave) throw new IOException("Injected journal failure");
                    Saved.Add(new JavaScriptSerializer().Serialize(records));
                    if (DuringSave != null) DuringSave();
                }, delegate { return Clock; });
            }
            internal void Update(bool enabled = true) { Areas.Update(new List<Bar> { Bar }, Displays, Settings, enabled); }
        }
        internal static int RunPureTest()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool value, string name) {
                if (!value) throw new Exception(name); count++;
            };
            try {
                var ignored = new Fixture(); ignored.Platform.Apply = false;
                ignored.Platform.BeforeSet = delegate { check(ignored.Saved.Count == 1 && ignored.Records.Count == 1,
                    "Initial mutation is preceded by its recovery journal"); };
                ignored.Update();
                string originalJournal = ignored.Saved.Single();
                for (int i = 0; i < 1000; i++) ignored.Update();
                check(ignored.Platform.Sets.Count == 1, "Accepted-but-ignored SPI has one attempt across 1001 updates");
                check(ignored.Saved.Count == 1 && ignored.Saved[0] == originalJournal && AreaPolicy.Equal(ignored.Records[0].Original, ignored.Original),
                    "Ignored mutation retains the first baseline without journal rewrites");
                check(ignored.Areas.SnapTargets.Count == 0, "Ignored SPI never authorizes snap repair");

                var refused = new Fixture(); refused.Platform.Denied = true;
                bool threw = false;
                try { refused.Update(); } catch (InvalidOperationException) { threw = true; }
                refused.Platform.Denied = false;
                for (int i = 0; i < 100; i++) refused.Update();
                check(threw && refused.Platform.Sets.Count == 1 && refused.Records.Count == 1 && refused.Saved.Count == 1,
                    "A refused SPI remains journaled and is not retried by polling");

                var resets = new Fixture(); resets.Update();
                check(resets.Areas.SnapTargets.Count == 1, "Verified initial application publishes a snap target");
                var applied = resets.Records[0].Applied;
                bool publicationsFollowReadback = true;
                for (int i = 0; i < 1000; i++) {
                    resets.Platform.Info.Work = i % 2 == 0 ? resets.Original : applied; resets.Update();
                    publicationsFollowReadback &= resets.Areas.SnapTargets.Count == (i % 2 == 0 ? 0 : 1);
                }
                check(publicationsFollowReadback, "Across 1000 resets snap publication follows fresh observation rather than historical Set success");
                check(resets.Platform.Sets.Count == 1 && resets.Saved.Count == 1 && AreaPolicy.Equal(resets.Records[0].Original, resets.Original),
                    "Repeated reset/recovery never rebases or reissues the first claim");

                var unreadable = new Fixture(); unreadable.Platform.FailRead = 3; unreadable.Update();
                check(unreadable.Areas.SnapTargets.Count == 0 && unreadable.Platform.Sets.Count == 1,
                    "Failed post-Set readback withholds a snap target");
                unreadable.Update();
                check(unreadable.Areas.SnapTargets.Count == 1 && unreadable.Platform.Sets.Count == 1,
                    "A later confirmed readback can publish without another Set");

                var wrong = new Fixture(); wrong.Platform.Info.Device = "replacement-device"; wrong.Update();
                check(wrong.Platform.Sets.Count == 0 && wrong.Records.Count == 0,
                    "A taskbar mapped to a different device cannot create ownership");

                var missing = new Fixture(); missing.Update();
                missing.Displays = new List<Display>(); missing.Platform.Connected = missing.Displays;
                missing.Update();
                bool cleanupRan = false;
                var incomplete = Recovery.Run(missing.Areas.RestoreAll, delegate { }, delegate { cleanupRan = true; });
                check(missing.Records.Count == 1 && missing.Saved.Count == 1 && missing.Platform.Sets.Count == 1 && missing.Areas.SnapTargets.Count == 0,
                    "Missing identity retains recovery ownership in Update and RestoreAll");
                check(!incomplete.Success && !cleanupRan && incomplete.Messages.Count == 1, "Unresolved recovery is reported as incomplete before lifecycle cleanup");
                missing.Displays = new List<Display> { missing.Display }; missing.Platform.Connected = missing.Displays;
                missing.Update();
                check(missing.Areas.SnapTargets.Count == 1 && missing.Platform.Sets.Count == 1,
                    "A uniquely returning identity reuses its verified claim");
                missing.Areas.RestoreAll();
                check(missing.Records.Count == 0 && missing.Platform.Sets.Count == 2 && AreaPolicy.Equal(missing.Platform.Info.Work, missing.Original),
                    "Returning unique identity restores the original exact owned area");

                var duplicate = new Fixture(); duplicate.Update();
                duplicate.Displays.Add(new Display { Device = "other-device", Identity = duplicate.Display.Identity });
                duplicate.Update(); bool ambiguous = false;
                try { duplicate.Areas.RestoreAll(); } catch (InvalidOperationException) { ambiguous = true; }
                check(duplicate.Records.Count == 1 && duplicate.Saved.Count == 1 && duplicate.Platform.Sets.Count == 1 && duplicate.Areas.SnapTargets.Count == 0,
                    "Duplicate identities cannot mutate, publish or retire a recovery record");
                check(ambiguous, "Ambiguous identity cannot falsely confirm full recovery");
                duplicate.Displays.RemoveAt(1); duplicate.Update(); duplicate.Areas.RestoreAll();
                check(duplicate.Records.Count == 0 && duplicate.Platform.Sets.Count == 2,
                    "Resolving ambiguity permits normal exact restoration");

                var absentBar = new Fixture(); absentBar.Update(); absentBar.Platform.Owned = false;
                absentBar.Areas.Update(new List<Bar>(), absentBar.Displays, absentBar.Settings, true);
                check(absentBar.Records.Count == 1 && absentBar.Saved.Count == 1 && absentBar.Areas.SnapTargets.Count == 0,
                    "A disappearing taskbar does not retire a still-selected claim");
                absentBar.Bar.Handle = 2; absentBar.Platform.Owned = true; absentBar.Update();
                check(absentBar.Platform.Sets.Count == 1 && absentBar.Areas.SnapTargets.Count == 1,
                    "A replaced taskbar handle does not rearm mutation");

                var paused = new Fixture(); paused.Update(); paused.Update(false);
                check(paused.Records.Count == 0 && paused.Areas.SnapTargets.Count == 0 && paused.Platform.Sets.Count == 2 &&
                    AreaPolicy.Equal(paused.Platform.Info.Work, paused.Original), "Pause restores the exact area and retires its claim");
                paused.Update();
                check(paused.Records.Count == 1 && paused.Platform.Sets.Count == 3 && paused.Areas.SnapTargets.Count == 1,
                    "Explicit re-enable starts one new ownership period");
                paused.Settings.HiddenIdentities.Clear(); paused.Update();
                check(paused.Records.Count == 0 && paused.Platform.Sets.Count == 4 && AreaPolicy.Equal(paused.Platform.Info.Work, paused.Original),
                    "Deselection restores through real Update reconciliation");

                var dock = new Fixture(); dock.Platform.Info.Work.Left += 30; dock.Update();
                check(dock.Records[0].Original.Left == 1030 && dock.Records[0].Applied.Left == 1030,
                    "Initial expansion preserves an unrelated appbar margin");
                dock.Platform.Info.Work.Right -= 20; var external = dock.Platform.Info.Work;
                dock.Update(); dock.Update(false);
                check(dock.Platform.Sets.Count == 1 && AreaPolicy.Equal(dock.Platform.Info.Work, external) && dock.Records.Count == 0,
                    "Conflict and explicit restore do not overwrite another appbar's later reservation");

                var geometry = new Fixture(); geometry.Update();
                geometry.Platform.Info.Monitor.Bottom = 1200; geometry.Platform.Info.Work = Rect(1000, 0, 2920, 1152);
                geometry.Platform.BarBounds = Rect(1000, 1152, 2920, 1200);
                for (int i = 0; i < 100; i++) geometry.Update();
                check(geometry.Platform.Sets.Count == 1 && geometry.Records.Count == 1 && geometry.Saved.Count == 1 && geometry.Areas.SnapTargets.Count == 0,
                    "Changed geometry conservatively retains the original journal without guessed reapplication");
                geometry.Update(false); geometry.Update();
                check(geometry.Platform.Sets.Count == 2 && geometry.Records.Count == 1 && geometry.Records[0].Monitor.Bottom == 1200 &&
                    geometry.Records[0].Original.Bottom == 1152 && geometry.Areas.SnapTargets.Count == 1,
                    "Explicit disable/re-enable safely establishes ownership at a genuine new resolution");

                var ignoredRestore = new Fixture(); ignoredRestore.Update(); ignoredRestore.Platform.Apply = false;
                ignoredRestore.Update(false);
                for (int i = 0; i < 1000; i++) ignoredRestore.Update(false);
                check(ignoredRestore.Platform.Sets.Count == 2 && ignoredRestore.Records.Count == 1 && ignoredRestore.Saved.Count == 1,
                    "Ignored restoration is attempted once and remains journaled across 1001 paused polls");
                bool ignoredCleanup = false;
                var ignoredResult = Recovery.Run(ignoredRestore.Areas.RestoreAll, delegate { }, delegate { ignoredCleanup = true; });
                check(!ignoredResult.Success && !ignoredCleanup && ignoredRestore.Platform.Sets.Count == 3 && ignoredRestore.Records.Count == 1,
                    "Explicit recovery retries once, requires Original readback and blocks cleanup when ignored");
                ignoredRestore.Platform.Apply = true; ignoredRestore.Areas.RestoreAll();
                check(ignoredRestore.Platform.Sets.Count == 4 && ignoredRestore.Records.Count == 0 && ignoredRestore.Saved.Count == 2,
                    "A later explicit retry retires ownership after confirmed restoration");

                var saveFailure = new Fixture(); saveFailure.Update(); saveFailure.FailSave = true;
                var firstSaveFailure = Recovery.Run(saveFailure.Areas.RestoreAll, delegate { });
                var secondSaveFailure = Recovery.Run(saveFailure.Areas.RestoreAll, delegate { });
                check(!firstSaveFailure.Success && !secondSaveFailure.Success && saveFailure.Records.Count == 1 && saveFailure.Saved.Count == 1 &&
                    saveFailure.Platform.Sets.Count == 2, "Failed journal retirement rolls back in-memory ownership on the same object without redundant Set");
                saveFailure.FailSave = false; saveFailure.Areas.RestoreAll();
                check(saveFailure.Records.Count == 0 && saveFailure.Saved.Count == 2 && saveFailure.Platform.Sets.Count == 2,
                    "The retained same-object claim can finish journal retirement after saving recovers");

                var initialSaveFailure = new Fixture(); initialSaveFailure.FailSave = true;
                bool initialFailed = false;
                try { initialSaveFailure.Update(); } catch (IOException) { initialFailed = true; }
                check(initialFailed && initialSaveFailure.Platform.Sets.Count == 0 && initialSaveFailure.Records.Count == 0,
                    "Initial journal failure cannot mutate Windows or leave an unpersisted in-memory claim");

                var startupMissing = new Fixture(); startupMissing.Update(); startupMissing.Displays.Clear();
                int startupNativeCalls = 0;
                var startup = Recovery.ForStartup(startupMissing.Areas.RestoreAvailable, delegate { startupNativeCalls++; });
                var duplicateStartup = Recovery.ForStartup(startupMissing.Areas.RestoreAvailable, delegate { startupNativeCalls++; });
                check(startup.Success && duplicateStartup.Success && startup.PendingWorkAreas == 1 && duplicateStartup.PendingWorkAreas == 1 &&
                    startupMissing.Records.Count == 1 && startupNativeCalls == 2 && startupMissing.Platform.Sets.Count == 1,
                    "Both startup recovery passes defer an unavailable display without losing ownership or skipping native recovery");
                var strictMissing = Recovery.Run(startupMissing.Areas.RestoreAll, delegate { }, delegate { startupNativeCalls += 100; });
                check(!strictMissing.Success && startupNativeCalls == 2 && startupMissing.Records.Count == 1,
                    "Strict lifecycle recovery rejects exactly the same deferred startup claim");
                startupMissing.Displays.Add(startupMissing.Display); startupMissing.Areas.RestoreAll();
                check(startupMissing.Records.Count == 0 && startupMissing.Platform.Sets.Count == 2,
                    "A later unique reconnect can finish strict exact restoration");

                var startupAmbiguous = new Fixture(); startupAmbiguous.Update();
                startupAmbiguous.Displays.Add(new Display { Device = "second-device", Identity = startupAmbiguous.Display.Identity });
                var ambiguousStartup = Recovery.ForStartup(startupAmbiguous.Areas.RestoreAvailable, delegate { });
                check(ambiguousStartup.Success && ambiguousStartup.PendingWorkAreas == 1 && startupAmbiguous.Records.Count == 1 &&
                    startupAmbiguous.Platform.Sets.Count == 1, "Startup defers ambiguous identity without retiring or writing geometry");
                startupAmbiguous.Displays.RemoveAt(1); startupAmbiguous.Platform.Readable = false;
                var unreadableStartup = Recovery.ForStartup(startupAmbiguous.Areas.RestoreAvailable, delegate { });
                check(unreadableStartup.Success && unreadableStartup.PendingWorkAreas == 1 && startupAmbiguous.Records.Count == 1,
                    "Startup defers an unreadable monitor without guessing geometry");

                var startupIgnored = new Fixture(); startupIgnored.Update(); startupIgnored.Platform.Apply = false;
                var ignoredStartup = Recovery.ForStartup(startupIgnored.Areas.RestoreAvailable, delegate { });
                check(!ignoredStartup.Success && startupIgnored.Records.Count == 1 && startupIgnored.Platform.Sets.Count == 2,
                    "Startup does not mistake accepted-but-ignored restoration for unavailable-display deferral");
                var startupDenied = new Fixture(); startupDenied.Update(); startupDenied.Platform.Denied = true;
                int deniedNative = 0;
                var deniedStartup = Recovery.ForStartup(startupDenied.Areas.RestoreAvailable, delegate { deniedNative++; });
                check(!deniedStartup.Success && startupDenied.Records.Count == 1 && deniedNative == 1,
                    "Startup propagates a real Set failure while still running native recovery");
                var startupSave = new Fixture(); startupSave.Update(); startupSave.FailSave = true;
                var saveStartup = Recovery.ForStartup(startupSave.Areas.RestoreAvailable, delegate { });
                check(!saveStartup.Success && startupSave.Records.Count == 1,
                    "Startup propagates journal failure while retaining the in-memory claim");
                var nativeStartup = Recovery.ForStartup(delegate { return 0; }, delegate { throw new IOException("Injected native recovery failure"); });
                check(!nativeStartup.Success, "Startup does not swallow native recovery failure");

                Console.WriteLine("PASS: " + count + " injected WorkAreas checks; no native, UI, process-control, input or profile/filesystem access.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
