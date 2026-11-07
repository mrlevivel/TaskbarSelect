using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    // All native observations and restore calls are injected. These checks
    // create no windows, register no shortcuts and do not alter a user profile.
    internal static class MinimizeUndoTests
    {
        sealed class State
        {
            internal bool Alive = true, Minimized, Maximized;
            internal FocusedWindowSnapshot Identity;
            internal WindowHistory.Placement Placement;
            internal Native.Rect Bounds, ExpectedRestore;
            internal Native.MonitorInfo Monitor;
        }
        sealed class Fixture
        {
            internal readonly Dictionary<IntPtr, State> Windows = new Dictionary<IntPtr, State>();
            internal readonly List<IntPtr> Restores = new List<IntPtr>();
            internal readonly List<WindowHistory.Placement> Requests = new List<WindowHistory.Placement>();
            internal readonly WindowHistory History;
            internal readonly Native.MonitorInfo Monitor;
            internal long Time = 100;
            internal bool Accept = true, Apply = true, Topology = true, MonitorChanged, WrongBounds, KeepIconic, WrongMaximized;
            internal bool UnknownIdentity, UnknownPlacement, UnknownWindowMonitor;
            internal int PositionCalls;
            internal Fixture()
            {
                Monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)), Device = "DISPLAY1",
                    Monitor = Rect(0, 0, 1920, 1080), Work = Rect(0, 40, 1920, 1080) };
                History = new WindowHistory(new WindowHistory.ObservationApi {
                    Alive = delegate(WindowHistory.Entry entry) {
                        State state; return Windows.TryGetValue(entry.Window, out state) && state.Alive &&
                            entry.Process == state.Identity.Process && entry.ProcessStarted == WindowStatePolicy.WorkerStarted(state.Identity);
                    },
                    Exists = delegate(IntPtr window) { State state; return Windows.TryGetValue(window, out state) && state.Alive; },
                    Minimized = delegate(IntPtr window) { State state; return Windows.TryGetValue(window, out state) && state.Minimized; },
                    Maximized = delegate(IntPtr window) { State state; return Windows.TryGetValue(window, out state) && state.Maximized; },
                    Monitor = delegate { var monitor = Monitor; if (MonitorChanged) monitor.Work.Bottom--; return Topology ? monitor : (Native.MonitorInfo?)null; },
                    WindowMonitor = delegate(IntPtr window) { return UnknownWindowMonitor ? null : (Native.MonitorInfo?)Windows[window].Monitor; },
                    Bounds = delegate(IntPtr window) { return Windows[window].Bounds; },
                    Placement = delegate(IntPtr window) { return UnknownPlacement ? null : (WindowHistory.Placement?)Windows[window].Placement; },
                    Identity = delegate(IntPtr window) { State state; return !UnknownIdentity && Windows.TryGetValue(window, out state) && state.Alive ? state.Identity.Copy() : null; },
                    Now = delegate { return Time; }, Pause = delegate { Time += Math.Max(1, Stopwatch.Frequency / 100); }
                });
            }
            internal WindowHistory.Entry Add(int number, WindowHistory.EntryKind kind, bool maximized = false, bool snapped = false)
            {
                var window = new IntPtr(number);
                DateTime started = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc).AddSeconds(number);
                var normal = Rect(30 + number, 45, 630 + number, 445);
                var bounds = maximized ? Rect(-8, 32, 1928, 1088) : snapped ? Rect(0, 40, 960, 1080) : Rect(normal.Left, normal.Top + 40, normal.Right, normal.Bottom + 40);
                var placement = new WindowHistory.Placement { Length = Marshal.SizeOf(typeof(WindowHistory.Placement)), ShowCommand = maximized ? 3 : 1,
                    Flags = maximized ? 2 : 0, NormalPosition = normal };
                var identity = new FocusedWindowSnapshot { Window = window, Root = window, Process = (uint)(1000 + number), Thread = (uint)(2000 + number),
                    ProcessStarted = started.ToFileTimeUtc(), Visible = true, ClassName = "Neutral window", ProcessImage = @"C:\Neutral\Demo.exe", Device = Monitor.Device };
                var state = new State { Identity = identity, Placement = placement, Bounds = bounds, ExpectedRestore = bounds, Monitor = Monitor,
                    Minimized = kind == WindowHistory.EntryKind.Minimize, Maximized = maximized && kind != WindowHistory.EntryKind.Minimize };
                if (state.Minimized) { var iconic = placement; iconic.ShowCommand = 2; state.Placement = iconic; }
                Windows.Add(window, state);
                return new WindowHistory.Entry { Window = window, Process = identity.Process, ProcessStarted = started.Ticks,
                    Kind = kind, FocusedIdentity = kind == WindowHistory.EntryKind.Minimize ? identity.Copy() : null,
                    Placement = placement, Monitor = Monitor, IsNormal = !maximized, VisibleBounds = bounds,
                    ArrangedBounds = snapped ? bounds : (Native.Rect?)null };
            }
            internal bool Undo(Action<WindowHistory.Entry> before = null)
            {
                return History.Undo(delegate(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags) {
                    PositionCalls++; return Accept;
                }, delegate(IntPtr window, ref WindowHistory.Placement placement) {
                    Restores.Add(window); Requests.Add(placement);
                    if (!Accept) return false;
                    if (Apply) {
                        State state = Windows[window]; state.Minimized = KeepIconic;
                        state.Maximized = WrongMaximized ? placement.ShowCommand != 3 : placement.ShowCommand == 3;
                        state.Placement = placement;
                        state.Bounds = WrongBounds ? Rect(1, 2, 300, 400) : state.ExpectedRestore;
                    }
                    return true;
                }, before ?? delegate { });
            }
        }
        static Native.Rect Rect(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        internal static void Run(Action<bool, string> check)
        { Chronology(check); RestoreStates(check); StaleBarriers(check); UnknownReads(check); FailureRetention(check); CaptureOwnership(check); PendingPublication(check); Limit(check); }

        static void Chronology(Action<bool, string> check)
        {
            var f = new Fixture(); var moveA = f.Add(1, WindowHistory.EntryKind.Move); var minB = f.Add(2, WindowHistory.EntryKind.Minimize); var moveC = f.Add(3, WindowHistory.EntryKind.Move);
            f.History.Remember(moveA); check(f.History.RememberMinimized(minB), "A confirmed still-minimized window enters the shared history"); f.History.Remember(moveC);
            check(f.Undo() && !f.History.LastUndoMinimized && f.Restores.SequenceEqual(new[] { moveC.Window }), "Move A, minimize B, move C first undoes C");
            check(f.Undo() && f.History.LastUndoMinimized && f.Restores.SequenceEqual(new[] { moveC.Window, minB.Window }) && !f.Windows[minB.Window].Minimized,
                "The next shared Undo restores minimized B");
            check(f.Undo() && !f.History.LastUndoMinimized && f.Restores.SequenceEqual(new[] { moveC.Window, minB.Window, moveA.Window }) && !f.History.Available,
                "The third Undo returns A and leaves no separate minimize stack");
        }
        static void RestoreStates(Action<bool, string> check)
        {
            foreach (int state in new[] { 0, 1, 2 }) {
                var f = new Fixture(); var entry = f.Add(42, WindowHistory.EntryKind.Minimize, state == 1, state == 2);
                check(f.History.RememberMinimized(entry) && f.Undo() && f.History.LastUndoMinimized && !f.History.Available,
                    "Undo confirms captured normal, maximized or snapped placement before consuming minimize: " + state);
                check(!f.Windows[entry.Window].Minimized && f.Windows[entry.Window].Maximized == (state == 1) && f.PositionCalls == 0 && f.Restores.Count == 1 &&
                    AreaPolicy.Equal(f.Windows[entry.Window].Bounds, entry.VisibleBounds.Value), "Restore uses one bounded placement and observes the original geometry: " + state);
                check(f.Requests[0].ShowCommand == (state == 1 ? 3 : 1), "Explicit Undo restores the captured visible normal or maximized state: " + state);
                if (state == 2) check(AreaPolicy.Equal(f.Requests[0].NormalPosition, Rect(0, 0, 960, 1040)), "Snapped screen bounds convert to the saved monitor's workspace coordinates");
            }
        }
        static void StaleBarriers(Action<bool, string> check)
        {
            foreach (int stale in Enumerable.Range(0, 9)) {
                var f = new Fixture(); var older = f.Add(1, WindowHistory.EntryKind.Move); var entry = f.Add(2, WindowHistory.EntryKind.Minimize);
                f.History.Remember(older); f.History.RememberMinimized(entry); var window = f.Windows[entry.Window];
                if (stale == 0) window.Minimized = false;
                if (stale == 1) window.Alive = false;
                if (stale == 2) window.Identity.Thread++;
                if (stale == 3) window.Identity.ClassName += " replaced";
                if (stale == 4) window.Identity.ProcessImage += ".other";
                if (stale == 5) window.Identity.ProcessStarted++;
                if (stale == 6) { var placement = window.Placement; placement.NormalPosition.Left++; window.Placement = placement; }
                if (stale == 7) { var monitor = window.Monitor; monitor.Device = "DISPLAY2"; window.Monitor = monitor; }
                if (stale == 8) f.MonitorChanged = true;
                check(f.History.Available && !f.Undo() && f.History.LastUndoMinimizeUnavailable && f.Restores.Count == 0,
                    "A stale/manual-restored minimize consumes one Undo without touching its window or older history: " + stale);
                f.MonitorChanged = false;
                check(f.History.Available && f.Undo() && f.Restores.SequenceEqual(new[] { older.Window }), "Only the following Undo reaches the older move after stale minimize: " + stale);
            }
            var during = new Fixture(); var changed = during.Add(42, WindowHistory.EntryKind.Minimize); during.History.RememberMinimized(changed);
            check(!during.Undo(delegate { during.Windows[changed.Window].Minimized = false; }) && during.Restores.Count == 0 && !during.History.Available,
                "A manual restore during bounded-operation admission prevents the native restore request");
            foreach (bool closed in new[] { false, true }) {
                var f = new Fixture(); var older = f.Add(1, WindowHistory.EntryKind.Move); var entry = f.Add(42, WindowHistory.EntryKind.Minimize);
                f.History.Remember(older); f.History.RememberMinimized(entry); f.Topology = false;
                if (closed) f.Windows[entry.Window].Alive = false; else f.Windows[entry.Window].Minimized = false;
                check(!f.Undo() && f.History.LastUndoMinimizeUnavailable && f.Restores.Count == 0,
                    "A positively closed or restored target consumes stale minimize even when its saved display query fails: " + closed);
                f.Topology = true;
                check(f.Undo() && f.Restores.SequenceEqual(new[] { older.Window }), "Unavailable saved display cannot permanently strand older history behind a stale minimize: " + closed);
            }
        }
        static void PendingPublication(Action<bool, string> check)
        {
            var f = new Fixture(); var older = f.Add(1, WindowHistory.EntryKind.Move); var entry = f.Add(42, WindowHistory.EntryKind.Minimize);
            f.History.Remember(older); var gate = new MinimizeActionGate();
            check(gate.TryBegin(false) && gate.Pending && !gate.TryBegin(false), "One pending minimize owns the publication fence and blocks a second minimize");
            for (int request = 0; request < 3; request++) {
                if (!gate.QueueUndo()) f.Undo();
                check(gate.Pending && gate.UndoQueued && f.Restores.Count == 0, "Reentrant Undo is queued once without touching older history before minimize publication: " + request);
            }
            bool recorded = f.History.RememberMinimized(entry);
            if (gate.Complete(recorded)) f.Undo();
            check(recorded && !gate.Pending && !gate.UndoQueued && f.Restores.SequenceEqual(new[] { entry.Window }) && f.History.LastUndoMinimized,
                "Confirmed minimize history is published before exactly one queued Undo restores it");
            check(!gate.Complete(true) && f.Undo() && f.Restores.Last() == older.Window, "Completing twice cannot replay queued Undo and the older move requires its next explicit request");
            foreach (bool uncertain in new[] { false, true }) {
                f = new Fixture(); older = f.Add(1, WindowHistory.EntryKind.Move); f.History.Remember(older); gate = new MinimizeActionGate();
                gate.TryBegin(false); gate.QueueUndo();
                bool dispatched = false;
                try { if (uncertain) throw new InvalidOperationException("Injected unconfirmed minimize"); }
                catch (InvalidOperationException) { }
                finally { dispatched = gate.Complete(false); if (dispatched) f.Undo(); }
                check(!dispatched && !gate.Pending && !gate.UndoQueued && f.Restores.Count == 0 && f.History.Available,
                    "Failed or exceptional minimize consumes queued Undo as a no-op and clears its fence: " + uncertain);
                check(f.Undo() && f.Restores.SequenceEqual(new[] { older.Window }), "Failed minimize preserves the older action for a later explicit Undo: " + uncertain);
            }
            gate = new MinimizeActionGate();
            check(!gate.TryBegin(true) && !gate.Pending && !gate.QueueUndo(), "An existing movement blocks minimize admission without creating a queue");
            check(gate.TryBegin(false) && gate.Pending && gate.QueueUndo(), "A first minimize can expose pending Undo even before any older history exists");
            check(!gate.Complete(false) && gate.TryBegin(false), "A completed failure cannot leave the next minimize blocked");
            gate.Complete(false);
        }
        static void UnknownReads(Action<bool, string> check)
        {
            foreach (int missing in new[] { 0, 1, 2, 3 }) {
                var f = new Fixture(); var older = f.Add(1, WindowHistory.EntryKind.Move); var entry = f.Add(42, WindowHistory.EntryKind.Minimize);
                f.History.Remember(older); f.History.RememberMinimized(entry);
                if (missing == 0) f.UnknownIdentity = true;
                if (missing == 1) f.UnknownPlacement = true;
                if (missing == 2) f.UnknownWindowMonitor = true;
                if (missing == 3) f.Topology = false;
                check(f.History.Available && !f.Undo() && !f.History.LastUndoMinimizeUnavailable && f.Restores.Count == 0 && f.History.Available,
                    "Transient unavailable metadata retains minimize recovery without native restore or older movement: " + missing);
                f.UnknownIdentity = f.UnknownPlacement = f.UnknownWindowMonitor = false; f.Topology = true;
                check(f.Undo() && f.History.LastUndoMinimized && f.Restores.SequenceEqual(new[] { entry.Window }),
                    "A healthy retry restores the same minimized entry after unavailable observations: " + missing);
                check(f.Undo() && f.Restores.Last() == older.Window, "Unknown-read recovery leaves the prior move in order: " + missing);
            }
            var admission = new Fixture(); var current = admission.Add(42, WindowHistory.EntryKind.Minimize); admission.History.RememberMinimized(current);
            check(!admission.Undo(delegate { admission.UnknownPlacement = true; }) && admission.Restores.Count == 0 && admission.History.Available,
                "An unavailable placement query after operation admission retains the entry without mutating");
            admission.UnknownPlacement = false;
            check(admission.Undo() && admission.History.LastUndoMinimized, "A later healthy Undo can recover an admission-time query failure");
        }
        static void FailureRetention(Action<bool, string> check)
        {
            foreach (int mode in new[] { 0, 1, 2, 3, 4 }) {
                var f = new Fixture(); var older = f.Add(1, WindowHistory.EntryKind.Move); var entry = f.Add(42, WindowHistory.EntryKind.Minimize);
                f.History.Remember(older); f.History.RememberMinimized(entry);
                if (mode == 0) f.Accept = false;
                if (mode == 1) f.Apply = false;
                if (mode == 2) f.KeepIconic = true;
                if (mode == 3) f.WrongBounds = true;
                if (mode == 4) f.WrongMaximized = true;
                check(!f.Undo() && !f.History.LastUndoMinimized && f.History.Available && f.Restores.SequenceEqual(new[] { entry.Window }),
                    "Refused or unconfirmed restore never reports success or consumes the older move: " + mode);
                if (f.Windows[entry.Window].Minimized) {
                    f.Accept = f.Apply = true; f.KeepIconic = f.WrongBounds = f.WrongMaximized = false;
                    check(f.Undo() && f.History.LastUndoMinimized && f.Restores.Last() == entry.Window,
                        "A still-iconic failed restoration remains retryable: " + mode);
                } else check(!f.Undo() && f.History.LastUndoMinimizeUnavailable && f.Restores.Count == 1,
                    "A partial restore is later consumed as a no-op instead of overwriting its visible geometry: " + mode);
                check(f.History.Available && f.Undo() && f.Restores.Last() == older.Window, "Earlier movement remains after failed minimize recovery: " + mode);
            }
            var snapped = new Fixture(); var prior = snapped.Add(1, WindowHistory.EntryKind.Move); var snap = snapped.Add(42, WindowHistory.EntryKind.Minimize, false, true);
            snapped.History.Remember(prior); snapped.History.RememberMinimized(snap); snapped.KeepIconic = true;
            check(!snapped.Undo() && snapped.Windows[snap.Window].Minimized && snapped.History.Available &&
                !AreaPolicy.Equal(snapped.Windows[snap.Window].Placement.NormalPosition, snap.Placement.NormalPosition),
                "An incomplete snapped restore can alter its restore rectangle while remaining iconic without claiming success");
            snapped.KeepIconic = false;
            check(!snapped.Undo() && snapped.History.LastUndoMinimizeUnavailable && snapped.Restores.Count == 1,
                "A changed iconic snapped restore is consumed as stale instead of replaying geometry or claiming a retry");
            check(snapped.History.Available && snapped.Undo() && snapped.Restores.Last() == prior.Window,
                "An incomplete snapped restore preserves the older move for the following explicit Undo");
        }
        static void CaptureOwnership(Action<bool, string> check)
        {
            var f = new Fixture(); var entry = f.Add(42, WindowHistory.EntryKind.Minimize);
            check(f.History.RememberMinimized(entry), "A valid minimized capture is accepted before copy-isolation checks");
            entry.FocusedIdentity.ProcessStarted++; entry.VisibleBounds = Rect(0, 0, 1, 1); entry.Kind = WindowHistory.EntryKind.Move;
            check(f.Undo() && f.History.LastUndoMinimized, "Remember stores an independent capture and full identity snapshot");
            foreach (int invalid in new[] { 0, 1, 2, 3 }) {
                f = new Fixture(); entry = f.Add(42, WindowHistory.EntryKind.Minimize);
                if (invalid == 0) entry.Kind = WindowHistory.EntryKind.Move;
                if (invalid == 1) entry.ProcessStarted = entry.FocusedIdentity.ProcessStarted;
                if (invalid == 2) entry.VisibleBounds = null;
                if (invalid == 3) f.Windows[entry.Window].Minimized = false;
                check(!f.History.RememberMinimized(entry) && !f.History.Available && f.Restores.Count == 0,
                    "Invalid, wrong-epoch or already-restored minimize captures cannot become Undo authority: " + invalid);
            }
        }
        static void Limit(Action<bool, string> check)
        {
            var f = new Fixture();
            foreach (int number in Enumerable.Range(1, 12)) {
                var entry = f.Add(number, number % 2 == 0 ? WindowHistory.EntryKind.Minimize : WindowHistory.EntryKind.Move);
                if (entry.Kind == WindowHistory.EntryKind.Minimize) f.History.RememberMinimized(entry); else f.History.Remember(entry);
            }
            for (int number = 12; number >= 3; number--) check(f.Undo() && f.Restores.Last() == new IntPtr(number), "Mixed history keeps its newest ten chronological entries: " + number);
            check(!f.History.Available && !f.Undo() && f.Restores.Count == 10, "The single ten-entry limit applies across both kinds of action");
        }
    }
}
