using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarSelect
{
    // All entry points and notifications belong to the shelf UI thread. Only
    // catalog/launch work runs on an STA worker; its results are consumed by Poll.
    // Snapshot delegates must be thread-safe and must not access UI controls.
    internal sealed class GhostLaunchCoordinator : IDisposable
    {
        internal const int DefaultTimeoutMilliseconds = 15000;
        const int ProbeIntervalMilliseconds = 150;
        // Shell activation can outlive a dismissed search or even its manager.
        // Hold this process-wide lease until the external launch returns; a new
        // coordinator cannot create a second launch while that outcome is unknown.
        static int nativeLaunchPending;
        sealed class Operation
        {
            internal long Token, Deadline, NextProbe;
            internal GhostApp App;
            internal string AppKey, Context, Rules;
            internal Display Destination;
            internal bool LaunchRequested, Started, Moving, NewWindow;
            internal uint ProcessId;
            internal HashSet<Tuple<long, uint, long>> Baseline;
        }
        sealed class WorkResult
        {
            internal long Token;
            internal List<ShelfWindow> Windows;
            internal GhostLaunchResult Launch;
            internal HashSet<Tuple<long, uint, long>> Baseline;
            internal bool IsLaunch;
            internal string Invalid, Failure;
        }
        readonly object gate = new object();
        readonly Func<List<Display>> targets;
        readonly Func<List<AppRule>> exclusions;
        readonly Func<bool> allowed;
        readonly Func<List<ShelfWindow>> windows;
        readonly Func<ShelfWindow, Display, bool> move;
        readonly Func<GhostApp, GhostLaunchResult> launch, launchNewWindow;
        readonly Func<string> contextVersion;
        readonly Func<ShelfWindow, string> windowIdentity;
        readonly Func<ShelfWindow, bool> windowIdentityAbsentVerified;
        readonly Func<long> clock;
        readonly Action<Action> schedule;
        readonly long timeoutTicks, probeTicks;
        Operation operation;
        WorkResult ready;
        long generation;
        bool disposed, workPending;
        string status = "";
        bool started, succeeded;
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        internal GhostLaunchCoordinator(Func<List<Display>> targets, Func<List<AppRule>> exclusions,
            Func<bool> allowed, Func<List<ShelfWindow>> windows, Func<ShelfWindow, Display, bool> move,
            Func<GhostApp, GhostLaunchResult> launch, Func<string> contextVersion = null,
            Func<ShelfWindow, string> windowIdentity = null, Func<GhostApp, GhostLaunchResult> launchNewWindow = null,
            Func<ShelfWindow, bool> windowIdentityAbsentVerified = null)
            : this(targets, exclusions, allowed, windows, move, launch, contextVersion,
                Stopwatch.GetTimestamp, DefaultTimeoutMilliseconds, ScheduleSta, windowIdentity, launchNewWindow, windowIdentityAbsentVerified) { }

        internal GhostLaunchCoordinator(Func<List<Display>> targets, Func<List<AppRule>> exclusions,
            Func<bool> allowed, Func<List<ShelfWindow>> windows, Func<ShelfWindow, Display, bool> move,
            Func<GhostApp, GhostLaunchResult> launch, Func<string> contextVersion,
            Func<long> clock, int timeoutMilliseconds, Action<Action> schedule,
            Func<ShelfWindow, string> windowIdentity = null, Func<GhostApp, GhostLaunchResult> launchNewWindow = null,
            Func<ShelfWindow, bool> windowIdentityAbsentVerified = null)
        {
            if (targets == null || exclusions == null || allowed == null || windows == null || move == null ||
                launch == null || clock == null || schedule == null) throw new ArgumentNullException("callbacks");
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            this.targets = targets; this.exclusions = exclusions; this.allowed = allowed; this.windows = windows;
            this.move = move; this.launch = launch; this.launchNewWindow = launchNewWindow; this.contextVersion = contextVersion ?? delegate { return ""; };
            this.windowIdentity = windowIdentity ?? delegate(ShelfWindow w) { return WindowAppIdentity.Read(w.Handle); };
            this.windowIdentityAbsentVerified = windowIdentityAbsentVerified ?? delegate(ShelfWindow w) {
                string identity;
                return WindowAppIdentity.TryRead(w.Handle, out identity) && String.IsNullOrEmpty(identity);
            };
            this.clock = clock; this.schedule = schedule;
            timeoutTicks = Math.Max(1, Stopwatch.Frequency * (long)timeoutMilliseconds / 1000);
            probeTicks = Math.Max(1, Stopwatch.Frequency * ProbeIntervalMilliseconds / 1000);
        }

        static void ScheduleSta(Action action)
        {
            var thread = new Thread(delegate() { action(); });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        internal event Action Changed;
        internal static bool NativeLaunchPending { get { return Interlocked.CompareExchange(ref nativeLaunchPending, 0, 0) != 0; } }
        internal bool Busy { get { lock (gate) return operation != null || workPending || NativeLaunchPending; } }
        internal bool Started { get { return started; } }
        internal bool Succeeded { get { return succeeded; } }
        internal string Status { get { return status; } }

        static Display CopyDisplay(Display d)
        {
            return new Display { Device = d.Device, Identity = d.Identity, Name = d.Name, Primary = d.Primary,
                X = d.X, Y = d.Y, Width = d.Width, Height = d.Height, WorkHeight = d.WorkHeight };
        }
        static string RuleVersion(List<AppRule> rules)
        {
            return String.Join("\n", (rules ?? new List<AppRule>()).Where(r => r != null)
                .Select(r => r.Key ?? "").OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        static bool SameDestination(Display a, Display b)
        {
            return a != null && b != null && !String.IsNullOrWhiteSpace(a.Device) && !String.IsNullOrWhiteSpace(a.Identity) &&
                String.Equals(a.Device, b.Device, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.Identity, b.Identity, StringComparison.OrdinalIgnoreCase) &&
                a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height &&
                a.Width > 0 && a.Height > 0;
        }
        string Validate(Operation request, out Display destination)
        {
            destination = null;
            try {
                if (!allowed()) return "Search is no longer active. Placement was cancelled.";
                if (!String.Equals(request.Context, contextVersion() ?? "", StringComparison.Ordinal))
                    return "Settings changed. Placement was cancelled.";
                var rules = exclusions() ?? new List<AppRule>();
                if (!String.Equals(request.Rules, RuleVersion(rules), StringComparison.Ordinal))
                    return "App exclusions changed. Placement was cancelled.";
                if (AppCatalog.Matches(rules, request.AppKey)) return "This app is excluded from window movement.";
                var displays = targets() ?? new List<Display>();
                var matches = displays.Where(d => SameDestination(request.Destination, d)).ToList();
                if (matches.Count != 1) return "The destination display changed or disconnected. Placement was cancelled.";
                destination = CopyDisplay(matches[0]);
                return null;
            } catch (Exception) { return "Could not recheck the destination. Placement was cancelled."; }
        }
        bool Current(Operation request)
        {
            lock (gate) return !disposed && Object.ReferenceEquals(operation, request) &&
                request.Token == generation && clock() < request.Deadline;
        }
        void Notify(string message)
        {
            status = message;
            var callback = Changed; if (callback != null) callback();
        }
        internal bool Begin(GhostApp app, Display destination)
        { return Begin(app, destination, false); }
        internal bool Begin(GhostApp app, Display destination, bool newWindow)
        {
            lock (gate) { if (disposed || operation != null || workPending) return false; }
            started = succeeded = false;
            if (NativeLaunchPending) { Notify("A previous app request is still opening. Wait for it to finish."); return false; }
            if (app == null || destination == null || String.IsNullOrWhiteSpace(app.Id)) {
                started = succeeded = false; Notify("Select an installed app and a connected display."); return false;
            }
            if (newWindow && (launchNewWindow == null || !GhostAppCatalog.CanOpenNewWindow(app))) {
                Notify("This app does not support opening a new window here."); return false;
            }
            Operation request;
            try {
                long now = clock();
                request = new Operation { App = app.Copy(), AppKey = app.AppKey ?? "", Destination = CopyDisplay(destination),
                    Context = contextVersion() ?? "", Rules = RuleVersion(exclusions()), NewWindow = newWindow,
                    Deadline = now > Int64.MaxValue - timeoutTicks ? Int64.MaxValue : now + timeoutTicks };
            } catch (Exception) { Notify("Could not prepare the app request."); return false; }
            Display checkedDestination; string invalid = Validate(request, out checkedDestination);
            if (invalid != null) { started = succeeded = false; Notify(invalid); return false; }
            lock (gate) {
                if (disposed || operation != null || workPending) return false;
                request.Token = ++generation; operation = request; ready = null;
            }
            started = succeeded = false; Notify(newWindow ? "Checking existing windows before opening a new window..." : "Checking for an existing window...");
            Queue(request, false); return true;
        }

        void Queue(Operation request, bool isLaunch)
        {
            lock (gate) {
                if (disposed || !Object.ReferenceEquals(operation, request) || workPending) return;
                workPending = true;
            }
            try {
                schedule(delegate {
                    var result = new WorkResult { Token = request.Token, IsLaunch = isLaunch };
                    try {
                        Display destination;
                        if (!Current(request)) result.Invalid = "Placement was cancelled before the app request began.";
                        else if ((result.Invalid = Validate(request, out destination)) == null) {
                            // A cancelled queued launch must never open an app later.
                            if (!Current(request)) result.Invalid = "Placement was cancelled before the app request began.";
                            else if (isLaunch) {
                                if (Interlocked.CompareExchange(ref nativeLaunchPending, 1, 0) != 0)
                                    result.Invalid = "Another app request is still opening. Wait for it to finish.";
                                else try {
                                    if (!Current(request)) result.Invalid = "Placement was cancelled before the app request began.";
                                    else result.Launch = (request.NewWindow ? launchNewWindow : launch)(request.App);
                                } finally { Interlocked.Exchange(ref nativeLaunchPending, 0); }
                            }
                            else {
                                var snapshot = windows();
                                if (request.NewWindow && snapshot == null) throw new InvalidOperationException("Existing-window snapshot unavailable.");
                                // Copy lifetime values on this worker before
                                // identity queries or launch. Even an unknown
                                // app identity can become readable later; that
                                // does not make an existing lifetime new.
                                if (request.NewWindow && !request.LaunchRequested)
                                    result.Baseline = new HashSet<Tuple<long, uint, long>>(snapshot.Where(w => w != null &&
                                        w.Handle != IntPtr.Zero).Select(BaselineLifetime));
                                result.Windows = MatchingWindows(request, snapshot ?? new List<ShelfWindow>());
                            }
                        }
                    } catch (Exception) { result.Failure = isLaunch ? "The app could not be opened." : "Could not inspect the app's windows."; }
                    lock (gate) {
                        workPending = false;
                        if (!disposed && Object.ReferenceEquals(operation, request) && result.Token == generation) ready = result;
                    }
                });
            } catch (Exception) {
                lock (gate) { workPending = false; }
                Finish(request, "The app request could not be started.", false);
            }
        }
        void Finish(Operation request, string message, bool placed)
        {
            lock (gate) {
                if (!Object.ReferenceEquals(operation, request)) return;
                operation = null; ready = null; ++generation;
            }
            started = request.Started; succeeded = placed; Notify(message);
        }
        List<ShelfWindow> MatchingWindows(Operation request, List<ShelfWindow> candidates)
        {
            if (String.IsNullOrWhiteSpace(request.AppKey)) return new List<ShelfWindow>();
            return candidates.Where(w => w != null && w.Handle != IntPtr.Zero && w.Process != 0 && w.ProcessStarted > 0 &&
                !String.IsNullOrWhiteSpace(w.AppKey) && MatchesIdentity(request, w))
                .GroupBy(w => new { w.Handle, w.Process, w.ProcessStarted }).Select(g => g.First()).ToList();
        }
        static Tuple<long, uint, long> Lifetime(ShelfWindow window)
        { return Tuple.Create(window.Handle.ToInt64(), window.Process, window.ProcessStarted); }
        static Tuple<long, uint, long> BaselineLifetime(ShelfWindow window)
        {
            // Missing process metadata is an exclusion, never evidence that
            // the already observed HWND was created by this launch.
            return Tuple.Create(window.Handle.ToInt64(), window.Process,
                window.Process == 0 || window.ProcessStarted <= 0 ? 0L : window.ProcessStarted);
        }
        static bool ExistedBeforeLaunch(Operation request, ShelfWindow window)
        {
            long handle = window.Handle.ToInt64();
            return request.Baseline.Contains(Lifetime(window)) ||
                request.Baseline.Contains(Tuple.Create(handle, window.Process, 0L)) ||
                request.Baseline.Contains(Tuple.Create(handle, 0u, 0L));
        }
        bool MatchesIdentity(Operation request, ShelfWindow window)
        {
            if (String.Equals(window.AppKey, request.AppKey, StringComparison.OrdinalIgnoreCase)) return true;
            // Classic app shortcuts can publish their identity on each HWND
            // while retaining exe: process authority. Preserve that authority
            // for the shared mover, and compare only the complete explicit ID.
            if (!request.AppKey.StartsWith("app:", StringComparison.OrdinalIgnoreCase)) return false;
            string explicitIdentity = windowIdentity(window) ?? "";
            if (String.Equals(request.AppKey, "app:" + explicitIdentity, StringComparison.OrdinalIgnoreCase)) return true;
            // The exact executable remains authority only for canonical plain
            // shortcuts. A failed query or a different explicit window ID is
            // never evidence of absence, and profile/broker entries stay strict.
            return String.IsNullOrEmpty(explicitIdentity) && GhostAppCatalog.CanMatchUnidentifiedExecutable(request.App) &&
                String.Equals(window.AppKey, "exe:" + request.App.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                windowIdentityAbsentVerified(window);
        }
        internal void Poll()
        {
            Operation request; WorkResult result;
            lock (gate) { request = operation; result = ready; ready = null; }
            if (request == null) return;
            Display destination; string invalid = Validate(request, out destination);
            if (invalid != null) { Finish(request, invalid, false); return; }
            if (clock() >= request.Deadline) {
                Finish(request, request.Started ? request.NewWindow ? "The app opened, but no new eligible window appeared in time. Placement was unavailable." :
                    "The app opened, but no eligible window appeared in time. Placement was unavailable." :
                    "The request timed out. If the app opened, its window placement was cancelled.", false); return;
            }
            // The native shared mover can pump Windows messages while waiting
            // for placement. A timer tick must not probe or enter a second move
            // during that unwind, but it may still invalidate changed context.
            if (request.Moving) return;
            if (result == null) {
                bool pending; lock (gate) pending = workPending;
                if (!pending && clock() >= request.NextProbe) Queue(request, false);
                return;
            }
            if (result.Token != request.Token) return;
            if (result.Invalid != null || result.Failure != null) { Finish(request, result.Invalid ?? result.Failure, false); return; }
            if (result.IsLaunch) {
                if (result.Launch == null || !result.Launch.Started) {
                    Finish(request, result.Launch != null && !String.IsNullOrWhiteSpace(result.Launch.Error) ? result.Launch.Error : "The app could not be opened.", false); return;
                }
                request.Started = started = true; request.ProcessId = result.Launch.ProcessId;
                if (String.IsNullOrWhiteSpace(request.AppKey)) {
                    Finish(request, "The app opened, but its window identity could not be verified. Place it manually.", false); return;
                }
                Notify(request.NewWindow ? "App opened. Waiting for a new window..." : "App opened. Waiting for its window..."); request.NextProbe = clock(); Queue(request, false); return;
            }
            var matches = result.Windows ?? new List<ShelfWindow>();
            if (request.NewWindow) {
                if (!request.LaunchRequested) {
                    if (result.Baseline == null) { Finish(request, "Could not verify existing app windows. Opening was cancelled.", false); return; }
                    request.Baseline = result.Baseline; request.LaunchRequested = true;
                    Notify("Opening a new window..."); Queue(request, true); return;
                }
                if (request.Baseline == null) { Finish(request, "Could not verify the new app window. Placement was cancelled.", false); return; }
                matches = matches.Where(w => !ExistedBeforeLaunch(request, w)).ToList();
            }
            if (!request.NewWindow && matches.Count > 1 && request.ProcessId != 0) {
                var launched = matches.Where(w => w.Process == request.ProcessId).ToList();
                if (launched.Count == 1) matches = launched;
            }
            if (matches.Count > 1) {
                Finish(request, request.Started ? request.NewWindow ? "The app opened multiple new matching windows. Choose the window to move manually." :
                    "The app opened multiple matching windows. Choose the window to move manually." :
                    "This app has multiple windows. Choose the window to move from the Ghost Bar.", false); return;
            }
            if (matches.Count == 0) {
                if (!request.LaunchRequested) {
                    request.LaunchRequested = true; Notify("Opening app..."); Queue(request, true);
                } else request.NextProbe = clock() > Int64.MaxValue - probeTicks ? Int64.MaxValue : clock() + probeTicks;
                return;
            }
            invalid = Validate(request, out destination);
            if (invalid != null || !Current(request)) {
                Finish(request, invalid ?? "Placement was cancelled before movement.", false); return;
            }
            var selected = matches[0];
            try {
                if (!MatchesIdentity(request, selected) || (request.NewWindow && ExistedBeforeLaunch(request, selected))) {
                    Finish(request, "The app window identity changed. Placement was cancelled.", false); return;
                }
            } catch (Exception) {
                Finish(request, "The app window identity could not be rechecked. Placement was cancelled.", false); return;
            }
            invalid = Validate(request, out destination);
            if (invalid != null || !Current(request)) {
                Finish(request, invalid ?? "Placement was cancelled before movement.", false); return;
            }
            IntPtr foreground = selected.SelectionForeground; long timestamp = selected.SelectionTimestamp;
            ShelfSelectionOrigin origin = selected.SelectionOrigin;
            bool placed = false;
            try {
                request.Moving = true;
                selected.SelectionOrigin = ShelfSelectionOrigin.Chooser;
                selected.SelectionForeground = GetForegroundWindow(); selected.SelectionTimestamp = Stopwatch.GetTimestamp();
                // The shared mover rechecks HWND/process identity, fullscreen,
                // exclusions, relative sizing, focus and Undo immediately here.
                placed = move(selected, destination);
            } catch (Exception) { placed = false; }
            finally { request.Moving = false; selected.SelectionForeground = foreground; selected.SelectionTimestamp = timestamp; selected.SelectionOrigin = origin; }
            Finish(request, placed ? "Window is ready on this display." : request.Started ?
                "The app opened, but its window could not be placed on this display." : "The existing window could not be moved to this display.", placed);
        }
        internal void Cancel()
        {
            Operation request; lock (gate) request = operation;
            if (request != null) Finish(request, request.Started ? "The app opened. Further window placement was cancelled." : "App request cancelled.", false);
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Cancel(); lock (gate) { ready = null; ++generation; }
            Changed = null;
        }
    }
}
