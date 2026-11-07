using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    // Window operations are injected. The epoch contract reads metadata for
    // this test process only. No check changes the real foreground, mouse,
    // window styles, taskbar or administrator approval.
    internal static class WindowStateActionsTests
    {
        internal sealed class Fixture
        {
            internal readonly Dictionary<IntPtr, FocusedWindowSnapshot> Windows = new Dictionary<IntPtr, FocusedWindowSnapshot>();
            internal readonly Dictionary<string, IntPtr> Markers = new Dictionary<string, IntPtr>();
            internal readonly HashSet<IntPtr> Iconic = new HashSet<IntPtr>();
            internal IntPtr Foreground = new IntPtr(42);
            internal long Time = 100;
            internal int Reads, Captures, WorkerCalls, Mutations, CleanupCalls, Pauses;
            internal bool Quiet = true, Local = true, MarkerAccepted = true, ReturnNullRead;
            internal bool WorkerMutates = true, WorkerSucceeds = true, WorkerExited = true, ReportMutation = true;
            internal bool CleanupApplies = true, CleanupAccepted = true;
            internal Action<int> DuringRead;
            internal Action BeforeWorker, DuringPause;
            internal readonly FocusedWindowStateApi Api;
            internal Fixture()
            {
                Windows[Foreground] = Snapshot(42);
                Api = new FocusedWindowStateApi {
                    Foreground = delegate { return Foreground; }, Read = Read,
                    Exists = delegate(IntPtr w) { return Windows.ContainsKey(w); },
                    IsMinimized = delegate(IntPtr w) { return Iconic.Contains(w); },
                    CaptureMinimize = delegate(IntPtr w) { Captures++; return CaptureEntry(Windows[w]); },
                    Now = delegate { return Time; },
                    Pause = delegate { Pauses++; Time += Math.Max(1, Stopwatch.Frequency / 100); if (DuringPause != null) DuringPause(); },
                    Quiescent = delegate { return Quiet; }, TrackLocally = delegate { return Local; },
                    CleanupTopmost = Cleanup,
                    SetMarker = delegate(IntPtr w, string name, IntPtr value) {
                        if (!MarkerAccepted) return false; Markers[Key(w, name)] = value; return true;
                    },
                    ReadMarker = delegate(IntPtr w, string name) {
                        IntPtr value; return Markers.TryGetValue(Key(w, name), out value) ? value : IntPtr.Zero;
                    },
                    RemoveMarker = delegate(IntPtr w, string name) {
                        IntPtr value; Markers.TryGetValue(Key(w, name), out value); Markers.Remove(Key(w, name)); return value;
                    },
                    Minimize = delegate(FocusedWindowSnapshot w, long deadline, Func<bool> valid) { return Work(w, null, valid); },
                    SetTopmost = delegate(FocusedWindowSnapshot w, bool topmost, long deadline, Func<bool> valid) { return Work(w, topmost, valid); }
                };
            }
            internal FocusedWindowSnapshot Window { get { return Windows[new IntPtr(42)]; } }
            static string Key(IntPtr w, string name) { return w.ToInt64() + ":" + name; }
            internal FocusedWindowSnapshot Read(IntPtr w)
            {
                Reads++; if (DuringRead != null) DuringRead(Reads);
                FocusedWindowSnapshot result; return ReturnNullRead || !Windows.TryGetValue(w, out result) ? null : result;
            }
            WindowMoveWorker.Result Work(FocusedWindowSnapshot w, bool? topmost, Func<bool> valid)
            {
                WorkerCalls++; if (BeforeWorker != null) BeforeWorker();
                if (!valid() || (topmost.HasValue && WindowStatePolicy.Topmost(Windows[w.Window]) == topmost.Value))
                    return Result(WindowMoveWorker.Outcome.Rejected, false, true);
                if (WorkerMutates) {
                    Mutations++;
                    if (topmost.HasValue) Style(w.Window, topmost.Value);
                    else { Iconic.Add(w.Window); Foreground = new IntPtr(43); }
                }
                bool complete = valid();
                return Result(WorkerSucceeds && complete ? WindowMoveWorker.Outcome.Success : WindowMoveWorker.Outcome.Failed, ReportMutation, WorkerExited);
            }
            internal bool Cleanup(IntPtr w, bool topmost)
            { CleanupCalls++; if (CleanupApplies) Style(w, topmost); return CleanupAccepted; }
            internal void Style(IntPtr w, bool topmost)
            { if (topmost) Windows[w].ExtendedStyle |= 8; else Windows[w].ExtendedStyle &= ~8; }
            internal OwnedWindowPins Pins()
            {
                return new OwnedWindowPins(new OwnedWindowPinApi { Read = Api.Read, Exists = Api.Exists,
                    SetTopmost = Api.CleanupTopmost, SetMarker = Api.SetMarker, ReadMarker = Api.ReadMarker,
                    RemoveMarker = Api.RemoveMarker, Now = Api.Now, Pause = Api.Pause, Quiescent = Api.Quiescent });
            }
            internal long Deadline { get { return Time + 5 * Stopwatch.Frequency; } }
        }
        static FocusedWindowSnapshot Snapshot(int handle)
        {
            return new FocusedWindowSnapshot { Window = new IntPtr(handle), Root = new IntPtr(handle), Process = 7,
                Thread = 8, ProcessStarted = 99, Visible = true, ClassName = "Neutral app", Device = "DISPLAY2",
                ProcessImage = @"C:\Neutral\App.exe" };
        }
        static WindowMoveWorker.Result Result(WindowMoveWorker.Outcome outcome, bool attempted, bool exited)
        { return new WindowMoveWorker.Result { Outcome = outcome, MutationAttempted = attempted, CallerExited = exited }; }
        static WindowHistory.Entry CaptureEntry(FocusedWindowSnapshot snapshot)
        {
            var bounds = new Native.Rect { Left = 40, Top = 60, Right = 560, Bottom = 400 };
            return new WindowHistory.Entry { Window = snapshot.Window, Process = snapshot.Process,
                ProcessStarted = WindowStatePolicy.WorkerStarted(snapshot), IsNormal = true, VisibleBounds = bounds,
                Placement = new WindowHistory.Placement { Length = Marshal.SizeOf(typeof(WindowHistory.Placement)), ShowCommand = 1, NormalPosition = bounds },
                Monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)), Device = "DISPLAY2",
                    Monitor = new Native.Rect { Right = 1920, Bottom = 1080 }, Work = new Native.Rect { Right = 1920, Bottom = 1040 } } };
        }
        internal static void Run(Action<bool, string> check)
        {
            WorkerEpochBoundary(check); Admission(check); IdentityRaces(check); Minimize(check); MinimizeCapture(check); PinOwnership(check); Cleanup(check); Bounds(check);
        }
        static void WorkerEpochBoundary(Action<bool, string> check)
        {
            var created = new DateTime(2026, 10, 24, 15, 30, 0, DateTimeKind.Utc);
            long raw = created.ToFileTimeUtc(), expected = created.Ticks;
            var snapshot = Snapshot(42); snapshot.ProcessStarted = raw;
            int minimizes = 0, pins = 0; bool routed = true;
            Func<bool> valid = delegate { return true; };
            var worker = new WindowStateWorkerApi {
                Minimize = delegate(IntPtr window, uint process, long started, long deadline, Func<bool> guard) {
                    minimizes++; routed &= window == snapshot.Window && process == snapshot.Process && started == expected && deadline == 1234 && guard == valid;
                    return Result(WindowMoveWorker.Outcome.Success, true, true);
                },
                SetTopmost = delegate(IntPtr window, uint process, long started, long deadline, Func<bool> guard, bool desired) {
                    pins++; routed &= window == snapshot.Window && process == snapshot.Process && started == expected && deadline == 1234 && guard == valid && desired == (pins == 1);
                    return Result(WindowMoveWorker.Outcome.Success, true, true);
                }
            };
            var api = new FocusedWindowStateApi(worker);
            api.Minimize(snapshot, 1234, valid); api.SetTopmost(snapshot, true, 1234, valid); api.SetTopmost(snapshot, false, 1234, valid);
            check(raw != expected && routed && minimizes == 1 && pins == 2 && snapshot.ProcessStarted == raw,
                "Actual default minimize/pin dispatch converts raw FILETIME to worker UTC DateTime ticks without changing retained snapshots");
            check(WindowStatePolicy.WorkerStarted(snapshot) == expected,
                "State worker process epochs use the same UTC DateTime tick representation as NativeProcessIdentity");
            foreach (long invalid in new[] { 0L, -1L, Int64.MaxValue }) {
                snapshot.ProcessStarted = invalid;
                var minimize = api.Minimize(snapshot, 1234, valid); var pin = api.SetTopmost(snapshot, true, 1234, valid);
                check(minimize.Outcome == WindowMoveWorker.Outcome.Rejected && minimize.CallerExited && !minimize.MutationAttempted &&
                    pin.Outcome == WindowMoveWorker.Outcome.Rejected && pin.CallerExited && !pin.MutationAttempted && minimizes == 1 && pins == 2,
                    "Invalid raw process creation epochs are rejected before worker dispatch " + invalid);
            }
            check(WindowStatePolicy.WorkerStarted(null) == 0,
                "A missing focused snapshot cannot supply an invented worker epoch");
            using (var current = Process.GetCurrentProcess()) {
                var actual = Snapshot(42); actual.ProcessStarted = current.StartTime.ToUniversalTime().ToFileTimeUtc();
                long workerEpoch = NativeProcessIdentity.Started((uint)current.Id);
                check(workerEpoch > 0 && WindowStatePolicy.WorkerStarted(actual) == workerEpoch && actual.ProcessStarted != workerEpoch,
                    "Real current-process FILETIME converts exactly to NativeProcessIdentity's worker epoch without USER calls");
            }
        }
        static void Admission(Action<bool, string> check)
        {
            foreach (IntPtr bad in new[] { IntPtr.Zero, new IntPtr(-1), new IntPtr(0xFFFF) }) {
                var f = new Fixture(); f.Foreground = bad; var actions = new FocusedWindowStateActions(f.Api);
                check(!actions.Minimize(bad).Success && !actions.TogglePin(bad).Success && f.WorkerCalls == 0 && f.Reads == 0,
                    "Window-state actions never target a null, special or broadcast HWND " + bad);
            }
            var changes = new Action<FocusedWindowSnapshot>[] {
                w => w.Shell = true, w => w.Root = new IntPtr(43), w => w.Visible = false,
                w => w.Process = 0, w => w.Thread = 0, w => w.ProcessStarted = 0,
                w => w.ClassName = "", w => w.ProcessImage = "", w => w.Error = 5,
                w => w.ExtendedStyle = 0x08000000, w => w.ExtendedStyle = 0x80,
                w => w.Window = new IntPtr(43)
            };
            for (int i = 0; i < changes.Length; i++) {
                var f = new Fixture(); changes[i](f.Window); var actions = new FocusedWindowStateActions(f.Api);
                check(!actions.Minimize(f.Foreground).Success && !actions.TogglePin(f.Foreground).Success && f.WorkerCalls == 0 && f.Markers.Count == 0,
                    "State actions reject unverified/excluded/transient identity before native mutation " + i);
            }
            foreach (string shell in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame" }) {
                var f = new Fixture(); f.Window.ClassName = shell;
                check(!new FocusedWindowStateActions(f.Api).TogglePin(f.Foreground).Success && f.WorkerCalls == 0,
                    "State actions exclude the Windows shell class " + shell);
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (string image in new[] {
                windows + @"\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe",
                windows + @"\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe" }) {
                var f = new Fixture(); f.Window.ClassName = "Windows.UI.Core.CoreWindow"; f.Window.ProcessImage = image;
                check(!new FocusedWindowStateActions(f.Api).Minimize(f.Foreground).Success && f.WorkerCalls == 0,
                    "Official Start/Search host windows never receive state actions " + System.IO.Path.GetFileName(image));
            }
            var core = new Fixture(); core.Window.ClassName = "Windows.UI.Core.CoreWindow";
            check(new FocusedWindowStateActions(core.Api).TogglePin(core.Foreground).Success,
                "A normal app using CoreWindow remains eligible; class alone is not mistaken for Start/Search");
            var excluded = new Fixture();
            check(!new FocusedWindowStateActions(excluded.Api, delegate { return true; }).TogglePin(excluded.Foreground).Success && excluded.WorkerCalls == 0,
                "Controller exclusions apply to user pin requests");
            var busy = new Fixture { Quiet = false };
            check(!new FocusedWindowStateActions(busy.Api).TogglePin(busy.Foreground).Success && busy.Reads == 0,
                "An outstanding or uncertain worker prevents a new state request");
        }
        static void IdentityRaces(Action<bool, string> check)
        {
            var changes = new Action<Fixture>[] {
                f => f.Windows.Remove(new IntPtr(42)), f => f.Window.Process++, f => f.Window.Thread++,
                f => f.Window.ProcessStarted++, f => f.Window.ClassName += " changed", f => f.Window.ProcessImage += ".changed",
                f => f.Window.Root = new IntPtr(43), f => f.Window.Visible = false, f => f.Window.Error = 5,
                f => f.Foreground = new IntPtr(43), f => f.Window.ExtendedStyle |= 8
            };
            for (int i = 0; i < changes.Length; i++) {
                var f = new Fixture(); var change = changes[i]; f.DuringRead = n => { if (n == 2) change(f); };
                check(!new FocusedWindowStateActions(f.Api).TogglePin(new IntPtr(42)).Success && f.WorkerCalls == 0 && f.Markers.Count == 0,
                    "Pin rechecks foreground and HWND/PID/thread/epoch/class/image/state races " + i);
            }
            var stale = new Fixture { Foreground = new IntPtr(43) };
            check(!new FocusedWindowStateActions(stale.Api).Minimize(new IntPtr(42)).Success && stale.WorkerCalls == 0 && stale.Reads == 0,
                "Minimize never substitutes a different current foreground for the captured window");
            var late = new Fixture(); late.BeforeWorker = delegate { late.Foreground = new IntPtr(43); };
            var actions = new FocusedWindowStateActions(late.Api);
            check(!actions.TogglePin(new IntPtr(42)).Success && late.Mutations == 0 && actions.PinnedCount == 0 && late.Markers.Count == 0,
                "Late foreground change rejects the worker and rolls back its provisional ownership marker");
            var external = new Fixture(); external.BeforeWorker = delegate { external.Style(new IntPtr(42), true); };
            actions = new FocusedWindowStateActions(external.Api);
            check(!actions.TogglePin(new IntPtr(42)).Success && external.Mutations == 0 && actions.PinnedCount == 0 && WindowStatePolicy.Topmost(external.Window),
                "A concurrent external pin is not adopted when the guarded worker proves no mutation");
        }
        static void Minimize(Action<bool, string> check)
        {
            var f = new Fixture(); var actions = new FocusedWindowStateActions(f.Api); var result = actions.Minimize(f.Foreground);
            check(result.Success && f.Mutations == 1 && f.Iconic.Contains(new IntPtr(42)) && f.Foreground == new IntPtr(43) && actions.PinnedCount == 0 && result.UndoEntry != null && f.Captures >= 2,
                "Minimize accepts expected post-minimize foreground loss without pinning, closing or activating a window");
            f.Foreground = new IntPtr(42);
            check(!actions.Minimize(f.Foreground).Success && f.WorkerCalls == 1,
                "An already minimized window is not minimized again");
            var unconfirmed = new Fixture { WorkerMutates = false };
            var unconfirmedResult = new FocusedWindowStateActions(unconfirmed.Api).Minimize(unconfirmed.Foreground);
            check(!unconfirmedResult.Success && unconfirmedResult.UndoEntry == null,
                "A worker success without an observed minimized state is not reported as success");
            var pending = new Fixture { WorkerExited = false };
            var pendingResult = new FocusedWindowStateActions(pending.Api).Minimize(pending.Foreground);
            check(!pendingResult.Success && pendingResult.UndoEntry == null,
                "A worker that has not exited cannot certify completed minimize");
            var elevated = new Fixture { Local = false };
            check(new FocusedWindowStateActions(elevated.Api).Minimize(elevated.Foreground).Success,
                "The same bounded minimize route supports authenticated administrator routing");
        }
        static void MinimizeCapture(Action<bool, string> check)
        {
            var f = new Fixture(); var before = CaptureEntry(f.Window);
            f.Api.CaptureMinimize = delegate { return before; };
            var result = new FocusedWindowStateActions(f.Api).Minimize(f.Foreground); var undo = result.UndoEntry;
            check(result.Success && undo != null && undo.Kind == WindowHistory.EntryKind.Minimize && undo.Window == new IntPtr(42) &&
                undo.Process == f.Window.Process && undo.ProcessStarted == WindowStatePolicy.WorkerStarted(f.Window) &&
                undo.FocusedIdentity != null && WindowStatePolicy.SameIdentity(f.Window, undo.FocusedIdentity) && undo.FocusedIdentity.ProcessStarted == 99,
                "Confirmed minimize returns one pre-action history entry with normalized worker epoch and retained raw focused identity");
            before.Placement.NormalPosition.Left = 900; before.Monitor.Device = "changed"; f.Window.ClassName = "changed";
            check(undo != null && undo.Placement.NormalPosition.Left == 40 && undo.Monitor.Device == "DISPLAY2" && undo.FocusedIdentity.ClassName == "Neutral app",
                "Returned minimize Undo data is copied from mutable capture and raw snapshot sources");
            foreach (string state in new[] { "maximized", "arranged", "fullscreen" }) {
                var fixture = new Fixture(); var capture = CaptureEntry(fixture.Window);
                if (state == "maximized") { capture.IsNormal = false; capture.Placement.ShowCommand = 3; capture.Placement.Flags = 2; capture.VisibleBounds = capture.Monitor.Work; }
                if (state == "arranged") capture.ArrangedBounds = capture.VisibleBounds;
                if (state == "fullscreen") capture.FullscreenBounds = capture.VisibleBounds;
                fixture.Api.CaptureMinimize = delegate { return capture; };
                var saved = new FocusedWindowStateActions(fixture.Api).Minimize(fixture.Foreground).UndoEntry;
                check(saved != null && saved.Placement.ShowCommand == capture.Placement.ShowCommand && saved.Placement.Flags == capture.Placement.Flags &&
                    saved.IsNormal == capture.IsNormal && Nullable.Equals(saved.ArrangedBounds, capture.ArrangedBounds) &&
                    Nullable.Equals(saved.FullscreenBounds, capture.FullscreenBounds) && Nullable.Equals(saved.VisibleBounds, capture.VisibleBounds),
                    "Minimize preserves pre-action geometry and state for " + state + " Undo");
            }
            var invalidCaptures = new Action<WindowHistory.Entry>[] {
                e => e.Window = new IntPtr(43), e => e.Process++, e => e.ProcessStarted = 99,
                e => e.Placement.Length = 0, e => e.Placement.ShowCommand = 2, e => e.Placement.ShowCommand = 0,
                e => e.Placement.Flags = -1, e => e.Placement.Flags = 4, e => e.Placement.NormalPosition = new Native.Rect(),
                e => e.Placement.NormalPosition = new Native.Rect { Left = Int32.MinValue, Top = 0, Right = Int32.MaxValue, Bottom = 100 },
                e => e.Monitor.Device = "", e => e.Monitor.Size = 0, e => e.Monitor.Monitor = new Native.Rect(),
                e => e.Monitor.Work = new Native.Rect(), e => e.VisibleBounds = null, e => e.VisibleBounds = new Native.Rect()
            };
            for (int i = 0; i < invalidCaptures.Length; i++) {
                var fixture = new Fixture(); var capture = CaptureEntry(fixture.Window); invalidCaptures[i](capture);
                fixture.Api.CaptureMinimize = delegate { return capture; };
                result = new FocusedWindowStateActions(fixture.Api).Minimize(fixture.Foreground);
                check(!result.Success && result.UndoEntry == null && fixture.WorkerCalls == 0 && fixture.Mutations == 0,
                    "Invalid minimize HWND/PID/epoch/placement/monitor/visible-bounds capture prevents mutation " + i);
            }
            foreach (bool throws in new[] { false, true }) {
                var fixture = new Fixture(); fixture.Api.CaptureMinimize = delegate {
                    if (throws) throw new InvalidOperationException("Injected capture failure"); return null;
                };
                result = new FocusedWindowStateActions(fixture.Api).Minimize(fixture.Foreground);
                check(!result.Success && result.UndoEntry == null && fixture.WorkerCalls == 0,
                    "Unavailable or throwing read-only minimize capture refuses the action without inventing Undo " + throws);
            }
            var changes = new Action<WindowHistory.Entry>[] {
                e => e.Placement.NormalPosition.Left++, e => e.Placement.Flags++, e => e.Placement.ShowCommand = 3,
                e => e.Placement.MinPosition.X++, e => e.Placement.MaxPosition.Y++,
                e => e.Monitor.Device = "DISPLAY1", e => e.Monitor.Monitor.Right++, e => e.Monitor.Work.Bottom--,
                e => e.IsNormal = false, e => e.ArrangedBounds = e.VisibleBounds, e => e.FullscreenBounds = e.VisibleBounds,
                e => { var bounds = e.VisibleBounds.Value; bounds.Left++; e.VisibleBounds = bounds; }
            };
            for (int i = 0; i < changes.Length; i++) {
                var fixture = new Fixture(); var change = changes[i]; int captures = 0;
                fixture.Api.CaptureMinimize = delegate {
                    var capture = CaptureEntry(fixture.Window); if (++captures == 2) change(capture); return capture;
                };
                result = new FocusedWindowStateActions(fixture.Api).Minimize(fixture.Foreground);
                check(!result.Success && result.UndoEntry == null && fixture.WorkerCalls == 0 && captures == 2,
                    "Fresh pre-mutation capture rejects changed restore state or monitor topology " + i);
            }
            var races = new Action<Fixture>[] {
                x => x.Foreground = new IntPtr(43), x => x.Window.ProcessStarted++, x => x.Window.Thread++,
                x => x.Window.ClassName += " changed", x => x.Iconic.Add(new IntPtr(42))
            };
            for (int i = 0; i < races.Length; i++) {
                var fixture = new Fixture(); var change = races[i];
                fixture.Api.CaptureMinimize = delegate { var capture = CaptureEntry(fixture.Window); change(fixture); return capture; };
                result = new FocusedWindowStateActions(fixture.Api).Minimize(fixture.Foreground);
                check(!result.Success && result.UndoEntry == null && fixture.WorkerCalls == 0,
                    "Foreground, raw identity and iconic state are rechecked after read-only minimize capture " + i);
            }
            foreach (bool visibleOnly in new[] { false, true }) {
                var fixture = new Fixture(); var capture = CaptureEntry(fixture.Window); int captures = 0;
                fixture.Api.CaptureMinimize = delegate { captures++; return capture; };
                fixture.BeforeWorker = delegate {
                    if (visibleOnly) { var bounds = capture.VisibleBounds.Value; bounds.Left++; capture.VisibleBounds = bounds; }
                    else capture.Placement.NormalPosition.Left++;
                };
                result = new FocusedWindowStateActions(fixture.Api).Minimize(fixture.Foreground);
                check(!result.Success && result.UndoEntry == null && fixture.WorkerCalls == 1 && fixture.Mutations == 0 && captures >= 4,
                    "Worker authorization rechecks geometry changed after the initial minimize captures " + visibleOnly);
            }
            foreach (bool exited in new[] { false, true }) {
                var fixture = new Fixture { WorkerSucceeds = false, WorkerExited = exited };
                result = new FocusedWindowStateActions(fixture.Api).Minimize(fixture.Foreground);
                check(!result.Success && result.UndoEntry == null && fixture.Mutations == 1,
                    "Failed or uncertain minimize completion never appends a successful Undo record " + exited);
            }
            var reused = new Fixture(); reused.Api.Minimize = delegate(FocusedWindowSnapshot w, long deadline, Func<bool> valid) {
                reused.Iconic.Add(w.Window); reused.Window.ProcessStarted++; return Result(WindowMoveWorker.Outcome.Success, true, true);
            };
            result = new FocusedWindowStateActions(reused.Api).Minimize(reused.Foreground);
            check(!result.Success && result.UndoEntry == null,
                "An apparent worker success cannot publish Undo for a changed post-minimize process lifetime");
            var already = new Fixture(); already.Iconic.Add(already.Foreground);
            result = new FocusedWindowStateActions(already.Api).Minimize(already.Foreground);
            check(!result.Success && result.UndoEntry == null && already.Captures == 0 && already.WorkerCalls == 0,
                "Already minimized input creates neither duplicate history nor a new capture");
            var pin = new Fixture(); var actions = new FocusedWindowStateActions(pin.Api);
            result = actions.TogglePin(pin.Foreground); var unpin = actions.TogglePin(pin.Foreground);
            check(result.Success && unpin.Success && result.UndoEntry == null && unpin.UndoEntry == null && pin.Captures == 0,
                "Pin and unpin remain outside movement/minimize Undo history");
        }
        static void PinOwnership(Action<bool, string> check)
        {
            var f = new Fixture(); var actions = new FocusedWindowStateActions(f.Api);
            var result = actions.TogglePin(f.Foreground);
            check(result.Success && result.Pinned == true && actions.PinnedCount == 1 && f.Markers.Count == 1 && actions.IsTopmost(f.Foreground),
                "A successful new ordinary pin owns one stable window with one ephemeral numeric marker");
            result = actions.TogglePin(f.Foreground);
            check(result.Success && result.Pinned == false && actions.PinnedCount == 0 && f.Markers.Count == 0 && !actions.IsTopmost(f.Foreground),
                "A second pin shortcut unpins the focused window and releases its ownership marker");
            var preexisting = new Fixture(); preexisting.Style(preexisting.Foreground, true); actions = new FocusedWindowStateActions(preexisting.Api);
            check(actions.IsTopmost(preexisting.Foreground) && actions.PinnedCount == 0 && actions.TryReleasePins(preexisting.Deadline) && preexisting.CleanupCalls == 0,
                "Observing a pre-existing topmost window never adopts or cleans it up");
            check(actions.TogglePin(preexisting.Foreground).Success && actions.PinnedCount == 0 && preexisting.Markers.Count == 0 && !WindowStatePolicy.Topmost(preexisting.Window),
                "The explicit focused unpin shortcut can remove an existing pin without adopting it");
            var high = new Fixture { Local = false }; actions = new FocusedWindowStateActions(high.Api);
            check(actions.TogglePin(high.Foreground).Success && actions.PinnedCount == 0 && high.Markers.Count == 0,
                "Administrator pins belong to the authenticated helper registry rather than ordinary-process cleanup");
            var noMutation = new Fixture { WorkerMutates = false, WorkerSucceeds = false, ReportMutation = false };
            actions = new FocusedWindowStateActions(noMutation.Api);
            check(!actions.TogglePin(noMutation.Foreground).Success && actions.PinnedCount == 0 && noMutation.Markers.Count == 0,
                "A proved non-mutating failure removes its provisional marker and ownership record");
            var uncertain = new Fixture { WorkerSucceeds = false, WorkerExited = false };
            actions = new FocusedWindowStateActions(uncertain.Api); result = actions.TogglePin(uncertain.Foreground); uncertain.Quiet = false;
            check(!result.Success && result.Status.Contains("recovery") && actions.PinnedCount == 1 && !actions.TryReleasePins(uncertain.Deadline) && uncertain.CleanupCalls == 0,
                "An uncertain mutating worker retains pin ownership and blocks cleanup until quiescent");
            uncertain.Quiet = true;
            check(actions.TryReleasePins(uncertain.Deadline) && actions.PinnedCount == 0 && uncertain.Markers.Count == 0,
                "A retained uncertain pin can be released once workers are proved quiescent");
            var throws = new Fixture(); throws.Api.SetTopmost = delegate(FocusedWindowSnapshot w, bool b, long d, Func<bool> v) {
                throws.Style(w.Window, true); throw new InvalidOperationException("Injected uncertain reply");
            };
            actions = new FocusedWindowStateActions(throws.Api); bool thrown = false;
            try { actions.TogglePin(throws.Foreground); } catch (InvalidOperationException) { thrown = true; }
            check(thrown && actions.PinnedCount == 1 && actions.TryReleasePins(throws.Deadline),
                "A thrown reply after possible mutation preserves ownership for bounded cleanup");
            var markerFail = new Fixture { MarkerAccepted = false }; actions = new FocusedWindowStateActions(markerFail.Api);
            check(!actions.TogglePin(markerFail.Foreground).Success && markerFail.WorkerCalls == 0 && actions.PinnedCount == 0,
                "Pin fails before mutation when its HWND-lifetime marker cannot be installed");
            var rollback = new Fixture(); var pins = rollback.Pins(); rollback.DuringRead = n => { if (n == 2) rollback.Window.ProcessStarted++; };
            check(!pins.Remember(Snapshot(42)) && pins.Count == 0 && rollback.Markers.Count == 0,
                "A changed identity after marker placement rolls back the unused marker");
            var readThrows = new Fixture(); pins = readThrows.Pins(); readThrows.DuringRead = n => { if (n == 2) throw new InvalidOperationException("Injected read failure"); };
            check(!pins.Remember(Snapshot(42)) && pins.Count == 0 && readThrows.Markers.Count == 0,
                "A failed post-marker identity read rolls back the unused marker before any topmost request");
        }
        static void Cleanup(Action<bool, string> check)
        {
            bool ignored = false; var f = new Fixture(); var actions = new FocusedWindowStateActions(f.Api, delegate { return ignored; });
            actions.TogglePin(f.Foreground); ignored = true; f.Window.Visible = false; f.Foreground = new IntPtr(43);
            check(actions.TryReleasePins(f.Deadline) && f.CleanupCalls == 1 && !WindowStatePolicy.Topmost(f.Window) && f.Markers.Count == 0,
                "Normal cleanup releases its hidden/newly-excluded pin without foreground or controller-enabled requirements");
            var gone = new Fixture(); var pins = gone.Pins(); pins.Remember(gone.Window); gone.Windows.Clear(); gone.Markers.Clear();
            check(pins.ReleaseAll(gone.Deadline) && pins.Count == 0 && gone.CleanupCalls == 0,
                "A destroyed owned HWND drops its record without any native mutation");
            var changes = new Action<FocusedWindowSnapshot>[] {
                w => w.Process++, w => w.Thread++, w => w.ProcessStarted++, w => w.ClassName += " changed", w => w.ProcessImage += ".changed"
            };
            for (int i = 0; i < changes.Length; i++) {
                var reused = new Fixture(); pins = reused.Pins(); pins.Remember(reused.Window); reused.Style(reused.Foreground, true);
                changes[i](reused.Window); reused.Markers.Clear();
                check(pins.ReleaseAll(reused.Deadline) && reused.CleanupCalls == 0 && WindowStatePolicy.Topmost(reused.Window),
                    "Cleanup never changes a reused HWND with different PID/thread/epoch/class/image " + i);
            }
            var same = new Fixture(); pins = same.Pins(); pins.Remember(same.Window); same.Style(same.Foreground, true); same.Markers.Clear();
            check(!pins.ReleaseAll(same.Deadline) && pins.Count == 1 && same.CleanupCalls == 0 && WindowStatePolicy.Topmost(same.Window),
                "Same-process/thread/class HWND reuse without its lifetime marker is never unpinned or falsely confirmed cleaned");
            var unreadable = new Fixture(); pins = unreadable.Pins(); pins.Remember(unreadable.Window); unreadable.Style(unreadable.Foreground, true); unreadable.ReturnNullRead = true;
            check(!pins.ReleaseAll(unreadable.Deadline) && pins.Count == 1 && unreadable.CleanupCalls == 0,
                "An existing but unreadable owned HWND retains recovery without mutation");
            var badEpoch = new Fixture(); pins = badEpoch.Pins(); pins.Remember(badEpoch.Window); badEpoch.Style(badEpoch.Foreground, true); badEpoch.Window.ProcessStarted = 0;
            check(!pins.ReleaseAll(badEpoch.Deadline) && pins.Count == 1 && badEpoch.CleanupCalls == 0,
                "An unverified process epoch cannot certify HWND destruction or safe cleanup");
            var cleared = new Fixture(); pins = cleared.Pins(); pins.Remember(cleared.Window);
            check(pins.ReleaseAll(cleared.Deadline) && cleared.CleanupCalls == 0 && cleared.Markers.Count == 0,
                "A provisionally remembered window that remained non-topmost needs only record/marker removal");
            var delayed = new Fixture { CleanupApplies = false, CleanupAccepted = false }; pins = delayed.Pins(); pins.Remember(delayed.Window); delayed.Style(delayed.Foreground, true);
            delayed.DuringPause = delegate { delayed.Style(new IntPtr(42), false); };
            check(pins.ReleaseAll(delayed.Deadline) && delayed.CleanupCalls == 1 && pins.Count == 0,
                "Cleanup certifies the observed result, including a delayed unpin despite an inconclusive native return");
            var stuck = new Fixture { CleanupApplies = false }; pins = stuck.Pins(); pins.Remember(stuck.Window); stuck.Style(stuck.Foreground, true);
            check(!pins.ReleaseAll(stuck.Deadline) && stuck.CleanupCalls == 1 && pins.Count == 1 && stuck.Markers.Count == 1,
                "An unconfirmed async unpin is posted once, bounded, and retains its ownership marker");
            var changed = new Fixture(); pins = changed.Pins(); pins.Remember(changed.Window); changed.Style(changed.Foreground, true);
            int baseline = changed.Reads; changed.DuringRead = n => { if (n == baseline + 2) changed.Markers.Clear(); };
            check(!pins.ReleaseAll(changed.Deadline) && changed.CleanupCalls == 0 && pins.Count == 1,
                "Cleanup rechecks the lifetime marker immediately before posting an unpin");
            var fail = new Fixture(); pins = fail.Pins(); pins.Remember(fail.Window); fail.Style(fail.Foreground, true);
            fail.Api.CleanupTopmost = delegate { throw new InvalidOperationException(); };
            // Pins captures its API at construction; explicitly inject failure in a fresh registry.
            var api = new OwnedWindowPinApi { Read = fail.Api.Read, Exists = fail.Api.Exists, SetTopmost = fail.Api.CleanupTopmost,
                SetMarker = fail.Api.SetMarker, ReadMarker = fail.Api.ReadMarker, RemoveMarker = fail.Api.RemoveMarker,
                Now = fail.Api.Now, Pause = fail.Api.Pause, Quiescent = fail.Api.Quiescent };
            fail.Style(fail.Foreground, false); pins = new OwnedWindowPins(api); pins.Remember(fail.Window); fail.Style(fail.Foreground, true);
            check(!pins.ReleaseAll(fail.Deadline) && pins.Count == 1,
                "Native cleanup exceptions retain recovery and cannot claim success");
        }
        static void Bounds(Action<bool, string> check)
        {
            var f = new Fixture(); var pins = f.Pins(); pins.Remember(f.Window); f.Style(f.Foreground, true);
            check(!pins.ReleaseAll(f.Time) && f.CleanupCalls == 0 && pins.Count == 1,
                "An expired aggregate cleanup deadline causes no mutation and retains the pin");
            f.Quiet = false;
            check(!pins.ReleaseAll(f.Deadline) && f.CleanupCalls == 0 && pins.Count == 1,
                "Uncertain or active helpers prevent cleanup from racing their mutations");
            f.Quiet = true; f.CleanupApplies = false;
            check(!pins.ReleaseAll(f.Deadline) && f.Time <= 100 + 2 * Stopwatch.Frequency + Stopwatch.Frequency / 100,
                "A caller's longer deadline is capped to two seconds for the entire local pin registry");
            var frozen = new Fixture { CleanupApplies = false }; frozen.Api.Pause = delegate { frozen.Pauses++; }; pins = frozen.Pins(); pins.Remember(frozen.Window); frozen.Style(frozen.Foreground, true);
            check(!pins.ReleaseAll(frozen.Deadline) && frozen.Pauses == 256 && frozen.CleanupCalls == 1,
                "A stalled injected clock is fenced by a finite cleanup iteration bound");
            var many = new Fixture(); pins = many.Pins();
            for (int i = 0; i < OwnedWindowPins.Limit; i++) {
                var w = Snapshot(100 + i); many.Windows[w.Window] = w; if (!pins.Remember(w)) throw new Exception("Fixture pin reservation failed"); many.Style(w.Window, true);
            }
            var overflow = Snapshot(1000); many.Windows[overflow.Window] = overflow;
            check(!pins.Remember(overflow) && pins.Count == OwnedWindowPins.Limit && many.Markers.Count == OwnedWindowPins.Limit,
                "The bounded registry rejects the 33rd pin without installing another marker");
            check(pins.ReleaseAll(many.Deadline) && pins.Count == 0 && many.CleanupCalls == OwnedWindowPins.Limit && many.Markers.Count == 0,
                "One aggregate cleanup releases all owned windows and only its own markers");
            var cycles = new Fixture(); var actions = new FocusedWindowStateActions(cycles.Api); bool succeeded = true;
            for (int i = 0; i < OwnedWindowPins.Limit + 10; i++) {
                var w = Snapshot(1000 + i); cycles.Windows[w.Window] = w; cycles.Foreground = w.Window;
                succeeded &= actions.TogglePin(w.Window).Success;
                cycles.Windows.Remove(w.Window); cycles.Markers.Clear();
            }
            check(succeeded && actions.PinnedCount == 1 && cycles.CleanupCalls == 0,
                "More than 32 pin-then-close cycles reclaim proved closed ownership before the next admission");
            var replace = new Fixture(); pins = replace.Pins(); pins.Remember(replace.Window); replace.Style(replace.Foreground, true);
            replace.Window.ProcessStarted++; var next = Snapshot(43); replace.Windows[next.Window] = next;
            check(pins.Remember(next) && pins.Count == 1 && replace.CleanupCalls == 0 && WindowStatePolicy.Topmost(replace.Window),
                "Admission prunes a proved reused process epoch without mutating the replacement window");
            var unpinned = new Fixture(); pins = unpinned.Pins(); pins.Remember(unpinned.Window);
            next = Snapshot(43); unpinned.Windows[next.Window] = next;
            check(pins.Remember(next) && pins.Count == 1 && unpinned.Markers.Count == 1 && unpinned.CleanupCalls == 0,
                "Admission prunes a readable non-topmost provisional pin and removes only its own marker");
            var unknown = new Fixture(); pins = unknown.Pins(); pins.Remember(unknown.Window); unknown.Style(unknown.Foreground, true);
            var stale = unknown.Window.Copy(); stale.ExtendedStyle = 0; stale.ProcessStarted++;
            check(!pins.Remember(stale) && pins.Count == 1 && unknown.CleanupCalls == 0,
                "An unverified replacement snapshot cannot erase ownership of the still-pinned original window");
            unknown.Window.ProcessStarted = 0; next = Snapshot(43); unknown.Windows[next.Window] = next;
            check(pins.Remember(next) && pins.Count == 2 && unknown.CleanupCalls == 0,
                "Admission retains an existing pin whose process identity is no longer readable");
            var lost = new Fixture(); pins = lost.Pins(); pins.Remember(lost.Window); lost.Style(lost.Foreground, true); lost.Markers.Clear();
            next = Snapshot(43); lost.Windows[next.Window] = next;
            check(pins.Remember(next) && pins.Count == 2 && lost.CleanupCalls == 0,
                "Admission retains a still-topmost same-identity record with an unconfirmed lifetime marker");
        }
    }
}
