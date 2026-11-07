using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace TaskbarSelect
{
    // Deterministic workers never launch a real app, change a display or move a
    // personal window. The production coordinator consumes exactly these results.
    internal static class GhostLaunchCoordinatorTests
    {
        sealed class Fixture : IDisposable
        {
            internal readonly List<Display> Displays = new List<Display>();
            internal readonly List<AppRule> Rules = new List<AppRule>();
            internal readonly List<ShelfWindow> Windows = new List<ShelfWindow>();
            internal readonly Queue<Action> Workers = new Queue<Action>();
            internal readonly GhostApp App = new GhostApp { Id = "fixture", Name = "Fixture app", AppKey = @"exe:C:\Fixtures\App.exe", LaunchPath = @"C:\Fixtures\App.lnk" };
            internal readonly Display Target = new Display { Device = @"\\.\DISPLAY4", Identity = "physical-fixture", X = -1920, Y = -1080, Width = 1920, Height = 1080, WorkHeight = 1040 };
            internal GhostLaunchCoordinator Coordinator;
            internal GhostLaunchResult LaunchResult = new GhostLaunchResult { Started = true, ProcessId = 17 };
            internal bool Allowed = true, MoveResult = true, Chooser, ThrowRead, NullRead, ThrowLaunch, ThrowMove, ThrowTargets;
            internal string Version = "settings-1";
            internal string ExplicitId = "";
            internal bool ThrowIdentity;
            internal long Now = 100;
            internal int Reads, Launches, NewWindowLaunches, Moves, Notifications, IdentityReads;
            internal Action DuringRead, DuringLaunch, DuringMove, DuringIdentity;
            internal Display MovedDestination;
            internal ShelfWindow MovedWindow;
            internal GhostApp LaunchedApp;
            internal Fixture(bool newWindowLaunchAvailable = true)
            {
                Displays.Add(Target);
                Func<GhostApp, GhostLaunchResult> newLauncher = null;
                if (newWindowLaunchAvailable) newLauncher = delegate(GhostApp app) { return Launch(app, true); };
                Coordinator = new GhostLaunchCoordinator(delegate { if (ThrowTargets) throw new InvalidOperationException(); return Displays.ToList(); },
                    delegate { return Rules.ToList(); }, delegate { return Allowed; },
                    delegate { Reads++; if (DuringRead != null) DuringRead(); if (ThrowRead) throw new InvalidOperationException(); return NullRead ? null : Windows.ToList(); },
                    delegate(ShelfWindow selected, Display display) {
                        Moves++; Chooser = selected.SelectionOrigin == ShelfSelectionOrigin.Chooser;
                        MovedWindow = selected; MovedDestination = display;
                        if (DuringMove != null) DuringMove(); if (ThrowMove) throw new InvalidOperationException(); return MoveResult;
                    }, delegate(GhostApp app) { return Launch(app, false); },
                    delegate { return Version; }, delegate { return Now; }, 1000, delegate(Action worker) { Workers.Enqueue(worker); },
                    delegate { IdentityReads++; if (DuringIdentity != null) DuringIdentity(); if (ThrowIdentity) throw new InvalidOperationException(); return ExplicitId; }, newLauncher);
                Coordinator.Changed += delegate { Notifications++; };
            }
            GhostLaunchResult Launch(GhostApp app, bool newWindow)
            {
                Launches++; if (newWindow) NewWindowLaunches++; LaunchedApp = app.Copy();
                if (DuringLaunch != null) DuringLaunch(); if (ThrowLaunch) throw new InvalidOperationException(); return LaunchResult;
            }
            internal ShelfWindow Window(int handle = 42, uint pid = 7, string key = null)
            {
                return new ShelfWindow { Handle = new IntPtr(handle), Process = pid, ProcessStarted = 1234,
                    AppKey = key ?? App.AppKey, Title = "Owned fixture" };
            }
            internal void Worker()
            {
                if (Workers.Count == 0) throw new Exception("Expected a queued Ghost search fixture worker.");
                Workers.Dequeue()();
            }
            internal void Step() { Worker(); Coordinator.Poll(); }
            internal void Advance(int milliseconds) { Now += Stopwatch.Frequency * (long)milliseconds / 1000; }
            public void Dispose() { Coordinator.Dispose(); while (Workers.Count != 0) Workers.Dequeue()(); }
        }

        internal static void Run(Action<bool, string> check)
        {
            Admission(check); Existing(check); LaunchAndWait(check); Identity(check); ExplicitWindowIdentity(check); ChromeAttributionPlacement(check); NewWindowMode(check); Cancellation(check); Failures(check); CrossSessionLaunchLease(check); WorkerContract(check);
        }
        static void NewWindowMode(Action<bool, string> check)
        {
            const string authority = @"exe:C:\Fixtures\chrome.exe";
            using (var f = new Fixture()) {
                check(!f.Coordinator.Begin(f.App, f.Target, true) && f.Workers.Count == 0 && f.Reads == 0 && f.Launches == 0,
                    "Unsupported app new-window requests refuse before workers or launch");
            }
            using (var f = new Fixture(false)) {
                check(!f.Coordinator.Begin(ChromeShortcut(), f.Target, true) && f.Workers.Count == 0 && f.Launches == 0,
                    "A capable app still requires an explicit new-window launcher before admission");
            }
            using (var f = new Fixture()) {
                f.NullRead = true; f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step();
                check(!f.Coordinator.Busy && f.Launches == 0 && f.Moves == 0 && !f.Coordinator.Succeeded,
                    "An unavailable baseline snapshot is not treated as proof of zero existing windows");
            }
            for (int variant = 0; variant < 4; variant++) using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; var old = f.Window(42, 7, authority); f.Windows.Add(old); f.Windows.Add(f.Window(44, 8, authority));
                var created = f.Window(variant < 2 ? 43 : 42, variant == 0 ? 17u : variant == 2 ? 9u : 7u, authority);
                if (variant == 3) created.ProcessStarted++;
                f.LaunchResult.ProcessId = 7; // Must not select the preexisting launch-PID window.
                f.DuringLaunch = delegate { if (created.Handle == old.Handle) f.Windows.Remove(old); f.Windows.Add(created); };
                check(f.Coordinator.Begin(ChromeShortcut(), f.Target, true), "Explicit Chrome new-window request is admitted at identity variant " + variant);
                f.Step();
                check(f.Moves == 0 && f.Launches == 0 && f.Workers.Count == 1,
                    "Complete baseline with multiple old matches schedules launch without moving or choosing among them at variant " + variant);
                f.Step();
                check(f.NewWindowLaunches == 1 && f.Launches == 1 && f.Coordinator.Started && f.Moves == 0,
                    "New-window mode uses its dedicated launcher exactly once before any placement at variant " + variant);
                f.Step();
                check(f.Coordinator.Succeeded && f.Moves == 1 && Object.ReferenceEquals(f.MovedWindow, created) &&
                    f.MovedWindow.AppKey == authority && CapturedChromeDestination(f) && !f.Coordinator.Busy && f.NewWindowLaunches == 1,
                    "Only the new lifetime is placed, including same-PID/new-HWND and recycled identities at variant " + variant);
                check(f.LaunchedApp.AppKey == "app:Chrome" && f.LaunchedApp.Arguments == "--source-shortcut-location=start-menu",
                    "New-window delegate receives the immutable canonical catalog selection at variant " + variant);
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; var old = f.Window(42, 17, authority); f.Windows.Add(old);
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step(); f.Step(); f.Step();
                check(f.Coordinator.Busy && f.Launches == 1 && f.NewWindowLaunches == 1 && f.Moves == 0,
                    "A matching old launch-PID window is not a fallback when no new window appears");
                for (int retry = 0; retry < 2; retry++) { f.Advance(150); f.Coordinator.Poll(); f.Step(); }
                f.Advance(1000); f.Coordinator.Poll();
                check(!f.Coordinator.Busy && !f.Coordinator.Succeeded && f.Coordinator.Started && f.Moves == 0 && f.Launches == 1 &&
                    f.Coordinator.Status.Contains("no new eligible window"), "Waiting only for a new lifetime times out without moving old windows or relaunching");
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; var old = f.Window(42, 7, authority); f.Windows.Add(old);
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Worker();
                old.Handle = new IntPtr(99); // Mutate the returned record after the worker captured its values.
                f.Coordinator.Poll();
                f.DuringLaunch = delegate { f.Windows.Clear(); f.Windows.Add(f.Window(42, 7, authority)); };
                f.Step(); f.Step(); f.Advance(1000); f.Coordinator.Poll();
                check(f.Moves == 0 && f.NewWindowLaunches == 1 && !f.Coordinator.Succeeded,
                    "Baseline lifetime values are copied on the probe worker before mutable result records can change");
            }
            foreach (string initialIdentity in new[] { "", "Chrome.Other" }) using (var f = new Fixture()) {
                f.ExplicitId = initialIdentity; f.Windows.Add(f.Window(42, 17, authority));
                f.DuringLaunch = delegate { f.ExplicitId = "Chrome"; };
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step(); f.Step(); f.Step(); f.Advance(1000); f.Coordinator.Poll();
                check(f.Moves == 0 && f.NewWindowLaunches == 1 && !f.Coordinator.Succeeded && !f.Coordinator.Busy &&
                    f.Coordinator.Status.Contains("no new eligible window"),
                    "A preexisting lifetime with initially unknown/nonmatching explicit identity cannot become a new window later: " + initialIdentity);
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; var old = f.Window(42, 17, ""); f.Windows.Add(old);
                f.DuringLaunch = delegate { old.AppKey = authority; };
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step(); f.Step(); f.Step(); f.Advance(1000); f.Coordinator.Poll();
                check(f.Moves == 0 && f.NewWindowLaunches == 1 && !f.Coordinator.Succeeded && !f.Coordinator.Busy,
                    "A preexisting lifetime with initially missing process app identity remains baseline when metadata becomes eligible");
            }
            foreach (bool missingProcess in new[] { false, true }) using (var f = new Fixture()) {
                f.ExplicitId = "Chrome";
                var placeholder = f.Window(42, missingProcess ? 0u : 17u, "");
                placeholder.ProcessStarted = missingProcess ? 1234 : 0; f.Windows.Add(placeholder);
                f.DuringLaunch = delegate {
                    f.Windows.Clear(); f.Windows.Add(f.Window(42, 17, authority));
                };
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step(); f.Step(); f.Step(); f.Advance(1000); f.Coordinator.Poll();
                check(f.Moves == 0 && f.NewWindowLaunches == 1 && !f.Coordinator.Succeeded && !f.Coordinator.Busy &&
                    f.Coordinator.Status.Contains("no new eligible window"),
                    "A preexisting metadata-only HWND cannot become new when missing PID/start becomes readable, missingProcess=" + missingProcess);
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; f.Windows.Add(f.Window(42, 7, authority)); f.LaunchResult.ProcessId = 17;
                f.DuringLaunch = delegate { f.Windows.Add(f.Window(43, 17, authority)); f.Windows.Add(f.Window(44, 18, authority)); };
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step(); f.Step(); f.Step();
                check(!f.Coordinator.Succeeded && !f.Coordinator.Busy && f.Moves == 0 && f.NewWindowLaunches == 1 && f.Coordinator.Status.Contains("multiple new"),
                    "Multiple new windows remain ambiguous even when one matches the returned launcher PID");
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome.Other"; f.Windows.Add(f.Window(42, 7, "app:Chrome"));
                f.DuringLaunch = delegate { f.Windows.Add(f.Window(43, 17, authority)); };
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step(); f.Step(); f.Step(); f.Advance(1000); f.Coordinator.Poll();
                check(f.Moves == 0 && f.NewWindowLaunches == 1 && !f.Coordinator.Succeeded && !f.Coordinator.Busy,
                    "A new Chrome executable window with the wrong explicit app identity grants no placement authority");
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; f.Windows.Add(f.Window(42, 7, authority));
                f.DuringLaunch = delegate { f.Windows.Add(f.Window(43, 17, authority)); };
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step(); f.Step(); f.Worker(); f.ExplicitId = "Chrome.Other"; f.Coordinator.Poll();
                check(f.Moves == 0 && !f.Coordinator.Succeeded && f.Coordinator.Status.Contains("identity changed"),
                    "New-window app identity is rechecked immediately before placement");
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; var old = f.Window(42, 7, authority); f.Windows.Add(old); var created = f.Window(43, 17, authority);
                f.DuringLaunch = delegate { f.Windows.Add(created); };
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step(); f.Step(); f.Worker();
                f.DuringIdentity = delegate { created.Handle = old.Handle; created.Process = old.Process; created.ProcessStarted = old.ProcessStarted; };
                f.Coordinator.Poll();
                check(f.Moves == 0 && !f.Coordinator.Succeeded && f.Coordinator.Status.Contains("identity changed"),
                    "A candidate changed back to a baseline lifetime during final identity verification cannot move");
            }
            foreach (bool cancel in new[] { false, true }) for (int phase = 0; phase < 3; phase++) using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; f.Windows.Add(f.Window(42, 7, authority));
                f.DuringLaunch = delegate { f.Windows.Add(f.Window(43, 17, authority)); };
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true);
                for (int step = 0; step < phase; step++) f.Step();
                if (cancel) f.Coordinator.Cancel(); else f.Version = "settings-2";
                f.Step();
                check(f.Moves == 0 && !f.Coordinator.Succeeded && !f.Coordinator.Busy && f.NewWindowLaunches == (phase == 2 ? 1 : 0),
                    "New-window cancellation/context change fences baseline, queued launch and late placement at cancel=" + cancel + ", phase=" + phase);
            }
            using (var f = new Fixture()) using (var replacement = new Fixture()) {
                bool blocked = false; f.ExplicitId = "Chrome";
                f.DuringLaunch = delegate { f.Coordinator.Cancel(); blocked = GhostLaunchCoordinator.NativeLaunchPending &&
                    !replacement.Coordinator.Begin(ChromeShortcut(), replacement.Target, true) && replacement.Workers.Count == 0; };
                f.Coordinator.Begin(ChromeShortcut(), f.Target, true); f.Step(); f.Step();
                check(blocked && f.NewWindowLaunches == 1 && f.Moves == 0 && replacement.NewWindowLaunches == 0 && !GhostLaunchCoordinator.NativeLaunchPending,
                    "Cancelled new-window launch retains the process-wide lease until its external launcher returns");
            }
        }
        static GhostApp ChromeShortcut()
        {
            return GhostAppCatalog.FromShortcut("Neutral Chrome", @"C:\Fixtures\Chrome.lnk", @"C:\Fixtures\chrome.exe",
                "--source-shortcut-location=start-menu", @"C:\Fixtures", "Chrome", delegate { return true; });
        }
        static bool CapturedChromeDestination(Fixture f)
        {
            var moved = f.MovedDestination;
            return moved != null && !Object.ReferenceEquals(moved, f.Target) && moved.Device == f.Target.Device && moved.Identity == f.Target.Identity &&
                moved.X == -1920 && moved.Y == -1080 && moved.Width == 1920 && moved.Height == 1080;
        }
        static void ChromeAttributionPlacement(Action<bool, string> check)
        {
            const string processAuthority = @"exe:C:\Fixtures\chrome.exe";
            using (var f = new Fixture()) {
                var app = ChromeShortcut(); f.ExplicitId = "Chrome";
                var window = f.Window(42, 7, processAuthority); f.Windows.Add(window);
                check(app != null && app.AppKey == "app:Chrome" && f.Coordinator.Begin(app, f.Target),
                    "Coordinator receives the actual catalog classification for a Chrome attribution shortcut");
                f.Step();
                check(f.Coordinator.Succeeded && !f.Coordinator.Started && f.Moves == 1 && f.Launches == 0 && f.IdentityReads == 2 &&
                    f.MovedWindow.Handle == window.Handle && f.MovedWindow.AppKey == processAuthority && window.AppKey == processAuthority && CapturedChromeDestination(f),
                    "An existing exact Chrome HWND identity moves to the captured monitor without launch or changing process authority");
            }
            for (int handoff = 0; handoff < 3; handoff++) using (var f = new Fixture()) {
                var app = ChromeShortcut(); f.ExplicitId = "Chrome";
                uint windowProcess = handoff == 0 ? 17u : 555u;
                f.LaunchResult.ProcessId = handoff == 1 ? 0u : 17u;
                f.DuringLaunch = delegate { f.Windows.Add(f.Window(42, windowProcess, processAuthority)); };
                f.Coordinator.Begin(app, f.Target); f.Step(); f.Step(); f.Step();
                check(f.Coordinator.Started && f.Coordinator.Succeeded && f.Launches == 1 && f.Moves == 1 &&
                    f.MovedWindow.Process == windowProcess && f.MovedWindow.AppKey == processAuthority && CapturedChromeDestination(f),
                    "Fresh Chrome launch or unique single-instance handoff places the exact app on its captured monitor at case " + handoff);
                check(f.LaunchedApp != null && !Object.ReferenceEquals(f.LaunchedApp, app) && f.LaunchedApp.AppKey == "app:Chrome" &&
                    f.LaunchedApp.Arguments == "--source-shortcut-location=start-menu" && f.LaunchedApp.ShortcutAppId == "Chrome" &&
                    f.LaunchedApp.LaunchPath == @"C:\Fixtures\Chrome.lnk",
                    "Injected launch receives the original Chrome attribution link and arguments at case " + handoff);
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; f.Windows.Add(f.Window(42, 7, processAuthority)); f.Windows.Add(f.Window(43, 8, processAuthority));
                f.Coordinator.Begin(ChromeShortcut(), f.Target); f.Step();
                check(!f.Coordinator.Succeeded && !f.Coordinator.Busy && f.Launches == 0 && f.Moves == 0 && f.Coordinator.Status.Contains("multiple"),
                    "Chrome attribution does not choose among multiple existing exact windows");
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; f.LaunchResult.ProcessId = 17;
                f.DuringLaunch = delegate { f.Windows.Add(f.Window(42, 555, processAuthority)); f.Windows.Add(f.Window(43, 556, processAuthority)); };
                f.Coordinator.Begin(ChromeShortcut(), f.Target); f.Step(); f.Step(); f.Step();
                check(f.Coordinator.Started && !f.Coordinator.Succeeded && !f.Coordinator.Busy && f.Launches == 1 && f.Moves == 0 && f.Coordinator.Status.Contains("multiple"),
                    "A launcher PID cannot resolve multiple matching Chrome handoff windows in other processes");
            }
            foreach (string wrongIdentity in new[] { "", "Chromium", "Chrome.Other", "Chrome.Profile", " Chrome" }) using (var f = new Fixture()) {
                f.ExplicitId = wrongIdentity; f.Windows.Add(f.Window(42, 17, processAuthority));
                f.Coordinator.Begin(ChromeShortcut(), f.Target); f.Step(); f.Step(); f.Step(); f.Advance(1000); f.Coordinator.Poll();
                check(f.Moves == 0 && f.Launches == 1 && !f.Coordinator.Succeeded && !f.Coordinator.Busy,
                    "Chrome executable and launcher PID cannot replace an exact explicit HWND app identity: " + wrongIdentity);
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; f.Windows.Add(f.Window(42, 7, processAuthority));
                f.Coordinator.Begin(ChromeShortcut(), f.Target); f.Worker(); f.ExplicitId = "Chrome.Other"; f.Coordinator.Poll();
                check(f.Moves == 0 && f.Launches == 0 && !f.Coordinator.Succeeded && f.Coordinator.Status.Contains("identity changed"),
                    "Chrome identity is rechecked after discovery immediately before movement");
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; f.Windows.Add(f.Window(42, 7, processAuthority));
                f.Coordinator.Begin(ChromeShortcut(), f.Target); f.Worker(); f.Version = "settings-2"; f.Coordinator.Poll();
                check(f.Moves == 0 && f.Launches == 0 && !f.Coordinator.Succeeded && !f.Coordinator.Busy,
                    "Changed captured context cancels a discovered Chrome window before placement");
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; f.Coordinator.Begin(ChromeShortcut(), f.Target); f.Step();
                f.Version = "settings-2"; f.Step();
                check(f.Moves == 0 && f.Launches == 0 && !f.Coordinator.Succeeded && !f.Coordinator.Busy,
                    "Changed context prevents an already queued Chrome launch");
            }
            using (var f = new Fixture()) {
                f.ExplicitId = "Chrome"; f.Coordinator.Begin(ChromeShortcut(), f.Target); f.Step(); f.Step();
                f.Coordinator.Cancel(); f.Windows.Add(f.Window(42, 555, processAuthority)); f.Step();
                check(f.Launches == 1 && f.Moves == 0 && f.Coordinator.Started && !f.Coordinator.Succeeded && !f.Coordinator.Busy,
                    "Cancellation after Chrome launch discards a later eligible handoff window without another launch");
            }
        }
        static void Admission(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                check(!f.Coordinator.Busy && !f.Coordinator.Started && !f.Coordinator.Succeeded && f.Coordinator.Status == "",
                    "Ghost app placement starts idle without implied launch or movement success");
                check(f.Coordinator.Begin(f.App, f.Target) && f.Coordinator.Busy && f.Workers.Count == 1,
                    "Ghost app admission schedules work and immediately owns one operation slot");
                check(f.Reads == 0 && f.Launches == 0 && f.Moves == 0,
                    "Ghost app admission does not probe windows or launch an app on the UI thread");
                check(!f.Coordinator.Begin(f.App, f.Target) && f.Workers.Count == 1,
                    "Repeated Enter cannot launch twice or replace a pending destination");
                f.Coordinator.Cancel();
                check(!f.Coordinator.Succeeded && f.Coordinator.Busy, "Cancelled queued work retains its ownership barrier until the worker unwinds");
                f.Step();
                check(!f.Coordinator.Busy && f.Launches == 0 && f.Moves == 0,
                    "Cancelling before a worker starts prevents all launch and movement effects");
                check(f.Coordinator.Begin(f.App, f.Target), "A completed cancelled request permits a deliberate new search request");
            }
            using (var f = new Fixture()) {
                check(!f.Coordinator.Begin(null, f.Target) && !f.Coordinator.Begin(f.App, null) && f.Workers.Count == 0,
                    "Ghost app admission rejects missing app or destination without worker effects");
                f.Allowed = false;
                check(!f.Coordinator.Begin(f.App, f.Target), "A paused or closed Ghost search cannot admit app requests");
                f.Allowed = true; f.Rules.Add(new AppRule { Key = f.App.AppKey });
                check(!f.Coordinator.Begin(f.App, f.Target) && f.Launches == 0,
                    "An excluded installed app cannot be opened through a placement request");
            }
            using (var f = new Fixture()) {
                f.Target.Identity = "";
                check(!f.Coordinator.Begin(f.App, f.Target), "A device name without physical monitor identity cannot authorize Ghost app placement");
                f.Coordinator.Dispose();
                check(!f.Coordinator.Begin(f.App, f.Target), "A disposed Ghost launch coordinator cannot be revived");
            }
        }
        static void Existing(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                var window = f.Window(); f.Windows.Add(window);
                window.SelectionOrigin = ShelfSelectionOrigin.DirectIcon; window.SelectionTimestamp = 88; window.SelectionForeground = new IntPtr(99);
                check(f.Coordinator.Begin(f.App, f.Target), "An exact running app can be selected from Ghost app search"); f.Step();
                check(f.Coordinator.Succeeded && !f.Coordinator.Started && f.Moves == 1 && f.Launches == 0 && !f.Coordinator.Busy,
                    "An existing exact app window is moved without opening a duplicate app instance");
                check(f.Chooser && f.MovedDestination.Identity == f.Target.Identity && !Object.ReferenceEquals(f.MovedDestination, f.Target),
                    "Ghost search uses chooser semantics and an owned physical destination snapshot");
                check(window.SelectionOrigin == ShelfSelectionOrigin.DirectIcon && window.SelectionTimestamp == 88 && window.SelectionForeground == new IntPtr(99),
                    "Ghost search restores transient selection metadata after the shared mover returns");
                f.Coordinator.Poll(); f.Coordinator.Poll();
                check(f.Moves == 1 && f.Launches == 0, "Completed Ghost placement never retries a move on later timer ticks");
            }
            using (var f = new Fixture()) {
                f.Windows.Add(f.Window()); f.Windows.Add(f.Window()); f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(f.Coordinator.Succeeded && f.Moves == 1, "Duplicate snapshots of the same HWND/process identity do not create false window ambiguity");
            }
            using (var f = new Fixture()) {
                f.Windows.Add(f.Window()); f.Windows.Add(f.Window(43)); f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(!f.Coordinator.Succeeded && f.Launches == 0 && f.Moves == 0 && f.Coordinator.Status.Contains("multiple"),
                    "Multiple exact running windows require deliberate window choice instead of moving an arbitrary one");
            }
        }
        static void LaunchAndWait(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(f.Reads == 1 && f.Launches == 0 && f.Workers.Count == 1 && !f.Coordinator.Started,
                    "No eligible existing window schedules one explicit app launch on a worker");
                f.Step();
                check(f.Launches == 1 && f.Coordinator.Started && !f.Coordinator.Succeeded && f.Workers.Count == 1,
                    "Successful app launch is reported separately from pending monitor placement");
                f.Step(); f.Coordinator.Poll();
                check(f.Launches == 1 && f.Workers.Count == 0 && f.Moves == 0,
                    "An app without a window is not relaunched or immediately busy-polled");
                f.Windows.Add(f.Window(42, 17)); f.Advance(150); f.Coordinator.Poll(); f.Step();
                check(f.Coordinator.Succeeded && f.Coordinator.Started && f.Launches == 1 && f.Moves == 1,
                    "A late exact app window is placed after one bounded explicit launch");
            }
            using (var f = new Fixture()) {
                f.LaunchResult.ProcessId = 0; f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step();
                f.Windows.Add(f.Window(42, 555)); f.Step();
                check(f.Coordinator.Succeeded && f.MovedWindow.Process == 555,
                    "A shell single-instance handoff can place its unique exact app window without relying on launcher PID");
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step();
                f.Windows.Add(f.Window(42, 17)); f.Windows.Add(f.Window(43, 88)); f.Step();
                check(f.Coordinator.Succeeded && f.MovedWindow.Process == 17,
                    "An explicit launch PID selects one window only when the exact app identity also corroborates it");
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step();
                f.Windows.Add(f.Window(42, 17)); f.Windows.Add(f.Window(43, 17)); f.Step();
                check(!f.Coordinator.Succeeded && f.Moves == 0 && f.Coordinator.Started && f.Coordinator.Status.Contains("multiple"),
                    "A launched process with several matching windows is not guessed by HWND order");
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step(); f.Step(); f.Advance(1000); f.Coordinator.Poll();
                check(!f.Coordinator.Busy && f.Coordinator.Started && !f.Coordinator.Succeeded && f.Coordinator.Status.Contains("no eligible window") && f.Launches == 1,
                    "A launch with no eligible window times out truthfully without repeated launches or fake placement success");
            }
            using (var f = new Fixture()) {
                f.App.AppKey = ""; f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step();
                check(f.Coordinator.Started && !f.Coordinator.Succeeded && f.Moves == 0 && !f.Coordinator.Busy && f.Coordinator.Status.Contains("identity"),
                    "An app with unverifiable window identity can open but must be placed manually");
            }
        }
        static void Identity(Action<bool, string> check)
        {
            foreach (string key in new[] { @"exe:C:\Other\App.exe", "app:Other.Package!App", "", @"exe:C:\Fixtures\App.exe.bak" }) {
                using (var f = new Fixture()) {
                    f.Windows.Add(f.Window(42, 17, key)); f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step(); f.Step();
                    check(f.Moves == 0 && !f.Coordinator.Succeeded && f.Launches == 1,
                        "A foreground/new/launcher-PID window with a different exact identity is never moved: " + key);
                }
            }
            using (var f = new Fixture()) {
                f.App.AppKey = "app:Fixture.Package!App"; f.Windows.Add(f.Window(42, 7, "APP:fixture.package!app"));
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(f.Coordinator.Succeeded && f.Launches == 0, "Full packaged app identity supports case-insensitive exact running-window reuse");
            }
            foreach (int invalid in new[] { 0, 1, 2 }) {
                using (var f = new Fixture()) {
                    var stale = f.Window();
                    if (invalid == 0) stale.Handle = IntPtr.Zero;
                    if (invalid == 1) stale.Process = 0;
                    if (invalid == 2) stale.ProcessStarted = 0;
                    f.Windows.Add(stale); f.Coordinator.Begin(f.App, f.Target); f.Step();
                    check(f.Moves == 0 && !f.Coordinator.Succeeded && f.Workers.Count == 1,
                        "Missing HWND/process/birth identity cannot authorize app placement: " + invalid);
                }
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.App.AppKey = "app:changed"; f.App.Name = "Changed";
                f.Windows.Add(f.Window(42, 7, @"exe:C:\Fixtures\App.exe")); f.Step();
                check(f.Coordinator.Succeeded, "Changing a catalog result object cannot change the admitted app identity snapshot");
            }
        }
        static void Cancellation(Action<bool, string> check)
        {
            for (int phase = 0; phase < 3; phase++) {
                for (int reason = 0; reason < 7; reason++) {
                    using (var f = new Fixture()) {
                        f.Coordinator.Begin(f.App, f.Target);
                        if (phase > 0) f.Step();
                        if (phase > 1) f.Step();
                        if (reason == 0) f.Allowed = false;
                        if (reason == 1) f.Version = "settings-2";
                        if (reason == 2) f.Displays.Clear();
                        if (reason == 3) f.Target.Identity = "replacement-monitor";
                        if (reason == 4) f.Target.Y += 1080;
                        if (reason == 5) f.Rules.Add(new AppRule { Key = f.App.AppKey });
                        if (reason == 6) f.Displays.Add(f.Target);
                        f.Coordinator.Poll();
                        while (f.Workers.Count != 0) f.Worker();
                        f.Coordinator.Poll();
                        check(f.Moves == 0 && f.Launches == (phase > 1 ? 1 : 0) && !f.Coordinator.Succeeded && !f.Coordinator.Busy,
                            "Pause/settings/disconnect/replacement/layout/exclusion/ambiguous-display changes fence every app request phase: " + phase + "/" + reason);
                    }
                }
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Coordinator.Cancel(); f.Worker(); f.Coordinator.Poll();
                check(f.Launches == 0 && f.Moves == 0, "Closing search after probe but before queued launch prevents late app launch");
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step(); f.Coordinator.Cancel();
                f.Windows.Add(f.Window(42, 17)); f.Worker(); f.Coordinator.Poll();
                check(f.Launches == 1 && f.Moves == 0 && f.Coordinator.Started && !f.Coordinator.Succeeded,
                    "Closing search after launch leaves the app alive and discards late placement results");
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Advance(1000); f.Coordinator.Poll();
                check(f.Coordinator.Busy && !f.Coordinator.Begin(f.App, f.Target),
                    "A timed-out queued worker cannot admit a duplicate request before unwinding");
                f.Worker(); f.Coordinator.Poll();
                check(!f.Coordinator.Busy && f.Reads == 0 && f.Launches == 0 && f.Moves == 0,
                    "An expired queued request has no late probe, launch or movement side effects");
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                f.Target.Device = @"\\.\DISPLAY9"; f.Worker(); f.Coordinator.Poll();
                check(f.Launches == 0 && f.Moves == 0, "Destination identity is rechecked on the worker immediately before app launch");
            }
            using (var f = new Fixture()) {
                f.Windows.Add(f.Window()); f.Coordinator.Begin(f.App, f.Target); f.Worker(); f.Target.Width = 1280; f.Coordinator.Poll();
                check(f.Moves == 0 && f.Launches == 0, "Destination geometry is rechecked after an eligible window is found and before moving it");
            }
            using (var f = new Fixture()) {
                f.Windows.Add(f.Window()); f.DuringRead = delegate { f.Allowed = false; };
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(f.Moves == 0 && f.Launches == 0 && !f.Coordinator.Succeeded,
                    "A pause occurring during the window probe invalidates its completed candidate before movement");
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step(); f.Coordinator.Dispose();
                f.Windows.Add(f.Window(42, 17)); f.Worker(); f.Coordinator.Poll();
                check(f.Moves == 0 && f.Launches == 1 && !f.Coordinator.Begin(f.App, f.Target),
                    "Disposing Ghost search fences late app-window results permanently without closing the launched app");
            }
        }
        static void ExplicitWindowIdentity(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                string authority = f.App.AppKey;
                f.App.AppKey = "app:Fixture.CustomID"; f.ExplicitId = "fixture.customid";
                var window = f.Window(42, 7, authority); f.Windows.Add(window);
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(f.Coordinator.Succeeded && f.Launches == 0 && f.IdentityReads == 2 && f.MovedWindow.AppKey == authority && window.AppKey == authority,
                    "An exact explicit HWND app identity reuses a classic app while preserving its exe process authority for the shared mover");
            }
            using (var f = new Fixture()) {
                string authority = f.App.AppKey; f.App.AppKey = "app:Fixture.CustomID"; f.ExplicitId = "Fixture.CustomID";
                f.Windows.Add(f.Window(42, 7, authority)); f.Coordinator.Begin(f.App, f.Target); f.Worker(); f.ExplicitId = "Other.CustomID"; f.Coordinator.Poll();
                check(f.Moves == 0 && f.Launches == 0 && !f.Coordinator.Succeeded && f.Coordinator.Status.Contains("identity changed"),
                    "A same-process explicit HWND app identity change is rechecked immediately before movement and fails closed");
            }
            foreach (string identity in new[] { "Fixture.CustomID.Other", "Fixture", " Fixture.CustomID", "" }) {
                using (var f = new Fixture()) {
                    string authority = f.App.AppKey; f.App.AppKey = "app:Fixture.CustomID"; f.ExplicitId = identity;
                    f.Windows.Add(f.Window(42, 17, authority)); f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step(); f.Step();
                    check(f.Moves == 0 && !f.Coordinator.Succeeded && f.Launches == 1,
                        "Partial/title/trimmed/missing explicit HWND app IDs cannot authorize search movement: " + identity);
                }
            }
            using (var f = new Fixture()) {
                f.Windows.Add(f.Window(42, 7, @"exe:C:\Other\App.exe")); f.ExplicitId = "Fixture.CustomID";
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(f.IdentityReads == 0 && f.Moves == 0,
                    "Executable app identity never falls back to an explicit HWND app ID for a different executable");
            }
            using (var f = new Fixture()) {
                f.App.AppKey = "app:Fixture.CustomID"; f.Windows.Add(f.Window()); f.ThrowIdentity = true;
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(f.Coordinator.Succeeded && f.IdentityReads == 0,
                    "An exact process app identity does not require an unrelated explicit HWND identity query");
            }
            using (var f = new Fixture()) {
                string authority = f.App.AppKey; f.App.AppKey = "app:Fixture.CustomID"; f.ExplicitId = "Fixture.CustomID";
                f.Windows.Add(f.Window(42, 7, authority)); f.ThrowIdentity = true; f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(!f.Coordinator.Succeeded && f.Moves == 0 && f.Launches == 0 && !f.Coordinator.Busy,
                    "A failed HWND identity query does not launch a duplicate app or guess its window");
            }
            using (var f = new Fixture()) {
                string authority = f.App.AppKey; f.App.AppKey = "app:Fixture.CustomID"; f.ExplicitId = "Fixture.CustomID";
                f.Windows.Add(f.Window(42, 7, authority)); f.Coordinator.Begin(f.App, f.Target); f.Worker();
                f.DuringIdentity = delegate { f.Target.Width = 1280; }; f.Coordinator.Poll();
                check(f.Moves == 0 && !f.Coordinator.Succeeded,
                    "Display changes during the final native HWND identity check invalidate placement before the shared mover");
            }
        }
        static void Failures(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                f.Windows.Add(f.Window()); f.MoveResult = false; f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(!f.Coordinator.Succeeded && !f.Coordinator.Started && f.Moves == 1 && f.Coordinator.Status.Contains("could not be moved"),
                    "Shared mover fullscreen/exclusion/focus failure is propagated without claiming successful app placement");
                f.Coordinator.Poll();
                check(f.Moves == 1 && f.Launches == 0, "A failed existing-window move is never retried or replaced with another launch");
            }
            using (var f = new Fixture()) {
                f.MoveResult = false; f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Step(); f.Windows.Add(f.Window(42, 17)); f.Step();
                check(f.Coordinator.Started && !f.Coordinator.Succeeded && f.Coordinator.Status.Contains("opened") && f.Moves == 1,
                    "Opened-but-placement-failed status retains the distinction between app launch and verified monitor placement");
            }
            for (int fault = 0; fault < 4; fault++) {
                using (var f = new Fixture()) {
                    if (fault == 0) f.ThrowRead = true;
                    if (fault == 1) f.ThrowLaunch = true;
                    if (fault == 2) { f.Windows.Add(f.Window()); f.ThrowMove = true; }
                    if (fault == 3) { f.LaunchResult.Started = false; f.LaunchResult.Error = "Owned launch failure"; }
                    f.Coordinator.Begin(f.App, f.Target); f.Step();
                    if (f.Workers.Count != 0) f.Step();
                    check(!f.Coordinator.Succeeded && !f.Coordinator.Busy,
                        "Probe/launch/mover exceptions and explicit launch failures end with a failed bounded request: " + fault);
                }
            }
            using (var f = new Fixture()) {
                f.Windows.Add(f.Window()); f.Coordinator.Begin(f.App, f.Target); f.Worker(); f.ThrowTargets = true; f.Coordinator.Poll();
                check(f.Moves == 0 && !f.Coordinator.Succeeded, "An unavailable display recheck fails closed before shared movement");
            }
            using (var f = new Fixture()) {
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                f.DuringLaunch = delegate { f.Coordinator.Cancel(); }; f.Worker(); f.Coordinator.Poll();
                check(f.Launches == 1 && f.Moves == 0 && !f.Coordinator.Succeeded && !f.Coordinator.Busy,
                    "Cancellation during launch discards its result without killing the app or granting placement authority");
            }
            using (var f = new Fixture()) {
                f.Windows.Add(f.Window()); f.DuringMove = delegate { f.Coordinator.Cancel(); };
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(f.Moves == 1 && !f.Coordinator.Succeeded && !f.Coordinator.Busy,
                    "Cancellation while the shared mover unwinds cannot overwrite the cancelled result with success");
            }
            using (var f = new Fixture()) {
                f.Windows.Add(f.Window()); f.DuringMove = delegate { f.Coordinator.Poll(); f.Coordinator.Poll(); };
                f.Coordinator.Begin(f.App, f.Target); f.Step();
                check(f.Coordinator.Succeeded && f.Moves == 1 && f.Workers.Count == 0 && f.Launches == 0,
                    "Reentrant UI timer ticks during shared native placement cannot queue probes or recursively move the selected app");
            }
            using (var f = new Fixture()) {
                bool fenced = false;
                f.DuringLaunch = delegate { f.Advance(1000); f.Coordinator.Poll(); fenced = f.Coordinator.Busy && !f.Coordinator.Begin(f.App, f.Target); };
                f.Coordinator.Begin(f.App, f.Target); f.Step(); f.Worker(); f.Coordinator.Poll();
                check(fenced && !f.Coordinator.Busy && !f.Coordinator.Succeeded && f.Launches == 1 && f.Moves == 0,
                    "A launch exceeding its deadline cannot admit a duplicate until it unwinds and never moves a late window");
            }
        }
        static void WorkerContract(Action<bool, string> check)
        {
            int owner = Thread.CurrentThread.ManagedThreadId, launched = 0, reads = 0;
            bool workerOnly = true, staOnly = true, notificationsOnOwner = true;
            var target = new Display { Device = @"\\.\DISPLAY2", Identity = "worker-fixture", X = 0, Y = 0, Width = 1920, Height = 1080 };
            var app = new GhostApp { Id = "worker-fixture", Name = "Worker fixture", AppKey = @"exe:C:\Fixtures\Worker.exe", LaunchPath = @"C:\Fixtures\Worker.lnk" };
            using (var coordinator = new GhostLaunchCoordinator(delegate { return new List<Display> { target }; },
                delegate { return new List<AppRule>(); }, delegate { return true; },
                delegate {
                    workerOnly &= Thread.CurrentThread.ManagedThreadId != owner;
                    staOnly &= Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                    Interlocked.Increment(ref reads);
                    return Thread.VolatileRead(ref launched) == 0 ? new List<ShelfWindow>() : new List<ShelfWindow> {
                        new ShelfWindow { Handle = new IntPtr(42), Process = 17, ProcessStarted = 1234, AppKey = app.AppKey } };
                }, delegate { return true; }, delegate {
                    workerOnly &= Thread.CurrentThread.ManagedThreadId != owner;
                    staOnly &= Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                    Interlocked.Increment(ref launched); return new GhostLaunchResult { Started = true, ProcessId = 17 };
                })) {
                coordinator.Changed += delegate { notificationsOnOwner &= Thread.CurrentThread.ManagedThreadId == owner; };
                coordinator.Begin(app, target);
                var wait = Stopwatch.StartNew();
                while (coordinator.Busy && wait.ElapsedMilliseconds < 3000) { coordinator.Poll(); Thread.Sleep(5); }
                check(coordinator.Succeeded && launched == 1 && reads >= 2 && workerOnly && staOnly,
                    "Default Ghost window probes and app launches run on background STA workers rather than the UI thread");
                check(notificationsOnOwner, "Default Ghost coordinator publishes status changes exclusively on the owning UI thread");
            }
        }
        static void CrossSessionLaunchLease(Action<bool, string> check)
        {
            using (var old = new Fixture())
            using (var replacement = new Fixture()) {
                bool rejected = false, busy = false;
                old.DuringLaunch = delegate {
                    old.Coordinator.Dispose();
                    rejected = !replacement.Coordinator.Begin(replacement.App, replacement.Target);
                    busy = replacement.Coordinator.Busy && GhostLaunchCoordinator.NativeLaunchPending;
                };
                old.Coordinator.Begin(old.App, old.Target); old.Step(); old.Worker(); old.Coordinator.Poll();
                check(rejected && busy && replacement.Launches == 0 && replacement.Workers.Count == 0,
                    "Dispose and a replacement coordinator cannot launch a second app while an old uncancellable native launch is entered");
                check(!GhostLaunchCoordinator.NativeLaunchPending && !replacement.Coordinator.Busy && replacement.Coordinator.Status.Contains("previous app request"),
                    "The process-wide native launch lease releases after the old request unwinds and explains why retry was blocked");
                check(replacement.Coordinator.Begin(replacement.App, replacement.Target),
                    "A deliberate request in a new search is admitted after the prior native launcher returns");
            }
            using (var old = new Fixture())
            using (var competing = new Fixture()) {
                old.Coordinator.Begin(old.App, old.Target); old.Step();
                competing.Coordinator.Begin(competing.App, competing.Target); competing.Step();
                old.DuringLaunch = delegate { competing.Worker(); competing.Coordinator.Poll(); };
                old.Worker(); old.Coordinator.Poll();
                check(old.Launches == 1 && competing.Launches == 0 && competing.Coordinator.Status.Contains("Another app request"),
                    "Requests admitted before either native launch still compete atomically for a single process-wide launch lease");
                check(!GhostLaunchCoordinator.NativeLaunchPending, "A lost native-launch admission race does not retain or overwrite the winner's lease");
            }
            using (var old = new Fixture())
            using (var replacement = new Fixture()) {
                bool blockedWhileEntered = false; old.ThrowLaunch = true;
                old.DuringLaunch = delegate {
                    old.Coordinator.Cancel();
                    blockedWhileEntered = !replacement.Coordinator.Begin(replacement.App, replacement.Target);
                };
                old.Coordinator.Begin(old.App, old.Target); old.Step(); old.Worker(); old.Coordinator.Poll();
                check(blockedWhileEntered && !GhostLaunchCoordinator.NativeLaunchPending && replacement.Coordinator.Begin(replacement.App, replacement.Target),
                    "A thrown cancelled native launcher releases the process-wide lease in finally without allowing an overlapping launch");
            }
        }
    }
}
