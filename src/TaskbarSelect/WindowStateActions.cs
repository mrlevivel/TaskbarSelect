using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarSelect
{
    internal sealed class WindowStateActionResult
    {
        internal bool Success { get; private set; }
        internal int Error { get; private set; }
        internal string Status { get; private set; }
        internal bool? Pinned { get; private set; }
        internal WindowHistory.Entry UndoEntry { get; private set; }
        internal WindowStateActionResult(bool success, string status, int error = 0, bool? pinned = null, WindowHistory.Entry undoEntry = null)
        { Success = success; Status = status; Error = error; Pinned = pinned; UndoEntry = undoEntry == null ? null : undoEntry.Copy(); }
    }

    // Only the two user actions require the captured foreground. Restoring a
    // pin is a separate, retained-identity operation and must work while hidden
    // or after the controller has paused. It never changes foreground itself.
    internal sealed class WindowStateWorkerApi
    {
        internal Func<IntPtr, uint, long, long, Func<bool>, WindowMoveWorker.Result> Minimize = WindowMoveWorker.Minimize;
        internal Func<IntPtr, uint, long, long, Func<bool>, bool, WindowMoveWorker.Result> SetTopmost = WindowMoveWorker.SetTopmost;
    }
    internal sealed class FocusedWindowStateApi
    {
        internal Func<IntPtr> Foreground = FocusedWindowNative.Foreground;
        internal Func<IntPtr, FocusedWindowSnapshot> Read = delegate(IntPtr window) { return FocusedWindowNative.Read(window, true); };
        internal Func<IntPtr, bool> Exists = Native.IsWindow;
        internal Func<IntPtr, bool> IsMinimized = WindowStateNative.IsMinimized;
        internal Func<IntPtr, WindowHistory.Entry> CaptureMinimize = WindowHistory.Capture;
        internal Func<long> Now = Stopwatch.GetTimestamp;
        internal Action Pause = delegate { Thread.Sleep(10); };
        internal Func<bool> Quiescent = delegate { return WindowMoveWorker.ActiveHelpers == 0 && WindowMoveWorker.UncertainHelpers == 0; };
        internal Func<FocusedWindowSnapshot, bool> TrackLocally = delegate(FocusedWindowSnapshot window) { return !NativeProcessIdentity.RequiresAdministrator(window.Process); };
        internal Func<IntPtr, bool, bool> CleanupTopmost = WindowStateNative.SetTopmost;
        internal Func<IntPtr, string, IntPtr, bool> SetMarker = WindowStateNative.SetPinMarker;
        internal Func<IntPtr, string, IntPtr> ReadMarker = WindowStateNative.ReadPinMarker;
        internal Func<IntPtr, string, IntPtr> RemoveMarker = WindowStateNative.RemovePinMarker;
        internal Func<FocusedWindowSnapshot, long, Func<bool>, WindowMoveWorker.Result> Minimize;
        internal Func<FocusedWindowSnapshot, bool, long, Func<bool>, WindowMoveWorker.Result> SetTopmost;
        internal FocusedWindowStateApi() : this(new WindowStateWorkerApi()) { }
        internal FocusedWindowStateApi(WindowStateWorkerApi worker)
        {
            if (worker == null) throw new ArgumentNullException("worker");
            Minimize = delegate(FocusedWindowSnapshot window, long deadline, Func<bool> valid) {
                long started = WindowStatePolicy.WorkerStarted(window);
                return started == 0 ? InvalidEpoch() : worker.Minimize(window.Window, window.Process, started, deadline, valid);
            };
            SetTopmost = delegate(FocusedWindowSnapshot window, bool topmost, long deadline, Func<bool> valid) {
                long started = WindowStatePolicy.WorkerStarted(window);
                return started == 0 ? InvalidEpoch() : worker.SetTopmost(window.Window, window.Process, started, deadline, valid, topmost);
            };
        }
        static WindowMoveWorker.Result InvalidEpoch()
        { return new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Rejected, CallerExited = true }; }
    }

    internal sealed class FocusedWindowStateActions
    {
        readonly FocusedWindowStateApi api;
        readonly Func<IntPtr, bool> ignored;
        readonly OwnedWindowPins pins;
        readonly object indicatedGate = new object();
        readonly Dictionary<IntPtr, PinnedWindowObservation> indicated = new Dictionary<IntPtr, PinnedWindowObservation>();
        long indicatedEpoch;
        internal FocusedWindowStateActions(Func<IntPtr, bool> ignoreWindow = null)
            : this(new FocusedWindowStateApi(), ignoreWindow) { }
        internal FocusedWindowStateActions(FocusedWindowStateApi api, Func<IntPtr, bool> ignoreWindow = null)
        {
            if (api == null) throw new ArgumentNullException("api");
            this.api = api; ignored = ignoreWindow ?? delegate { return false; };
            pins = new OwnedWindowPins(new OwnedWindowPinApi { Read = api.Read, Exists = api.Exists,
                SetTopmost = api.CleanupTopmost, Now = api.Now, Pause = api.Pause, Quiescent = api.Quiescent,
                SetMarker = api.SetMarker, ReadMarker = api.ReadMarker, RemoveMarker = api.RemoveMarker });
        }
        internal int PinnedCount { get { return pins.Count; } }
        // Presentation records are not pin authority. Both local and HIGH
        // results enter only after confirmed completion; the existing cleanup
        // ledgers and lifetime markers remain the sole restoration authority.
        internal PinnedWindowObservation[] IndicatedPins
        { get { lock (indicatedGate) return indicated.Values.Select(value => value.Copy()).ToArray(); } }
        void Indicate(FocusedWindowSnapshot window, bool pinned, bool helperOwned)
        {
            lock (indicatedGate) {
                indicated.Remove(window.Window);
                if (!pinned) return;
                if (indicated.Count >= OwnedWindowPins.Limit * 2) {
                    foreach (var pair in indicated.ToArray()) {
                        try {
                            var current = Read(pair.Key);
                            if (!api.Exists(pair.Key) || (WindowStatePolicy.ReadableIdentity(current) &&
                                (!WindowStatePolicy.SameIdentity(pair.Value.Identity, current) || !WindowStatePolicy.Topmost(current))))
                                indicated.Remove(pair.Key);
                        } catch (Exception) { /* Uncertain observations do not grant new display capacity. */ }
                    }
                }
                if (indicated.Count >= OwnedWindowPins.Limit * 2) return;
                indicated[window.Window] = new PinnedWindowObservation { Identity = window.Copy(), Epoch = ++indicatedEpoch, HelperOwned = helperOwned };
            }
        }
        internal bool TryReleasePins(long deadlineTicks) { return pins.ReleaseAll(deadlineTicks); }
        internal bool IsTopmost(IntPtr window)
        {
            var snapshot = Read(window);
            return WindowStatePolicy.Eligible(snapshot, ignored) && WindowStatePolicy.Topmost(snapshot);
        }
        internal WindowStateActionResult Minimize(IntPtr capturedForeground)
        {
            FocusedWindowSnapshot original;
            if (!Admit(capturedForeground, out original) || api.IsMinimized(capturedForeground))
                return Refused("The focused window cannot be minimized.");
            var undo = Capture(original.Window);
            if (!ValidCapture(original, undo) || !Valid(original, false) || api.IsMinimized(original.Window))
                return Refused("The window could not be safely captured for Undo; nothing was minimized.");
            var rechecked = Capture(original.Window);
            if (!ValidCapture(original, rechecked) || !SameCapture(undo, rechecked) ||
                !Valid(original, false) || api.IsMinimized(original.Window))
                return Refused("The focused window changed; nothing was minimized.");
            undo.Kind = WindowHistory.EntryKind.Minimize;
            undo.FocusedIdentity = original.Copy();
            long deadline = api.Now() + Stopwatch.Frequency;
            Func<bool> valid = delegate { return ValidMinimize(original, undo); };
            if (!valid()) return Refused("Focused window changed; nothing was minimized.");
            var result = api.Minimize(original.Copy(), deadline, valid);
            var current = Read(original.Window);
            if (Completed(result) && WindowStatePolicy.SameIdentity(original, current) && api.IsMinimized(original.Window))
                return new WindowStateActionResult(true, "Window minimized.", 0, null, undo);
            return Refused(result, "Windows did not confirm that the window was minimized.");
        }
        internal WindowStateActionResult TogglePin(IntPtr capturedForeground)
        {
            FocusedWindowSnapshot original;
            if (!Admit(capturedForeground, out original)) return Refused("The focused window cannot be pinned.");
            bool desired = !WindowStatePolicy.Topmost(original);
            long deadline = api.Now() + Stopwatch.Frequency;
            Func<bool> valid = delegate { return Valid(original, false); };
            if (!valid()) return Refused("Focused window changed; its pin was not changed.");
            bool local = desired && api.TrackLocally(original);
            // Reserve ownership before an operation can mutate. If its outcome
            // is uncertain this provisional record is needed for safe cleanup.
            if (local && !pins.Remember(original)) return Refused(pins.Count >= OwnedWindowPins.Limit
                ? "The pinned-window limit has been reached." : "The window could not be safely pinned.");
            var result = api.SetTopmost(original.Copy(), desired, deadline, valid);
            if (local && result != null && !result.MutationAttempted && result.CallerExited) pins.Forget(original);
            var current = Read(original.Window);
            if (Completed(result) && WindowStatePolicy.SameIdentity(original, current) && WindowStatePolicy.Topmost(current) == desired) {
                if (!desired) pins.Forget(original);
                Indicate(current, desired, desired && !local);
                return new WindowStateActionResult(true, desired ? "Window pinned: always on top." : "Window unpinned.", 0, desired);
            }
            return Refused(result, "Windows did not confirm the pin change.");
        }
        bool Admit(IntPtr window, out FocusedWindowSnapshot original)
        {
            original = null;
            if (FocusedWindowPolicy.SpecialHandle(window) || api.Foreground() != window || !api.Quiescent()) return false;
            var first = Read(window);
            if (!WindowStatePolicy.Eligible(first, ignored) || first.Window != window) return false;
            var current = Read(window);
            if (!WindowStatePolicy.Eligible(current, ignored) || !WindowStatePolicy.SameIdentity(first, current) ||
                api.Foreground() != window || WindowStatePolicy.Topmost(first) != WindowStatePolicy.Topmost(current)) return false;
            original = first; return true;
        }
        bool Valid(FocusedWindowSnapshot original, bool allowMinimizedCompletion)
        {
            var current = Read(original.Window);
            return WindowStatePolicy.Eligible(current, ignored) && WindowStatePolicy.SameIdentity(original, current) &&
                (api.Foreground() == original.Window || (allowMinimizedCompletion && api.IsMinimized(original.Window)));
        }
        bool ValidMinimize(FocusedWindowSnapshot original, WindowHistory.Entry captured)
        {
            if (!Valid(original, true)) return false;
            if (api.IsMinimized(original.Window)) return true;
            // Worker launch and READY authorization can take time. Until the
            // actual minimize starts, the saved geometry must still describe
            // the window we are about to minimize. Iconic completion naturally
            // changes placement/foreground and uses only retained identity.
            var current = Capture(original.Window);
            if (api.IsMinimized(original.Window)) return Valid(original, true);
            return ValidCapture(original, current) && SameCapture(captured, current) && Valid(original, false);
        }
        FocusedWindowSnapshot Read(IntPtr window)
        { var snapshot = api.Read(window); return snapshot == null ? null : snapshot.Copy(); }
        WindowHistory.Entry Capture(IntPtr window)
        {
            try { var entry = api.CaptureMinimize(window); return entry == null ? null : entry.Copy(); }
            catch (Exception) { return null; }
        }
        static bool ValidCapture(FocusedWindowSnapshot original, WindowHistory.Entry entry)
        {
            return entry != null && entry.Window == original.Window && entry.Process == original.Process &&
                entry.ProcessStarted > 0 && entry.ProcessStarted == WindowStatePolicy.WorkerStarted(original) &&
                entry.Placement.Length == Marshal.SizeOf(typeof(WindowHistory.Placement)) &&
                entry.Placement.Flags >= 0 && entry.Placement.Flags <= 3 && Positive(entry.Placement.NormalPosition) &&
                (entry.Placement.ShowCommand == 1 || entry.Placement.ShowCommand == 3) &&
                !String.IsNullOrEmpty(entry.Monitor.Device) && entry.Monitor.Size == Marshal.SizeOf(typeof(Native.MonitorInfo)) &&
                Positive(entry.Monitor.Monitor) && Positive(entry.Monitor.Work) && entry.VisibleBounds.HasValue && Positive(entry.VisibleBounds.Value);
        }
        static bool Positive(Native.Rect value)
        { return value.Right > value.Left && value.Bottom > value.Top &&
            (long)value.Right - value.Left <= Int32.MaxValue && (long)value.Bottom - value.Top <= Int32.MaxValue; }
        static bool SameRect(Native.Rect? a, Native.Rect? b)
        { return a.HasValue == b.HasValue && (!a.HasValue || AreaPolicy.Equal(a.Value, b.Value)); }
        static bool SameCapture(WindowHistory.Entry a, WindowHistory.Entry b)
        {
            var x = a.Placement; var y = b.Placement;
            return a.Window == b.Window && a.Process == b.Process && a.ProcessStarted == b.ProcessStarted &&
                x.Length == y.Length && x.Flags == y.Flags && x.ShowCommand == y.ShowCommand &&
                x.MinPosition.X == y.MinPosition.X && x.MinPosition.Y == y.MinPosition.Y &&
                x.MaxPosition.X == y.MaxPosition.X && x.MaxPosition.Y == y.MaxPosition.Y && AreaPolicy.Equal(x.NormalPosition, y.NormalPosition) &&
                String.Equals(a.Monitor.Device, b.Monitor.Device, StringComparison.OrdinalIgnoreCase) && a.Monitor.Flags == b.Monitor.Flags &&
                AreaPolicy.Equal(a.Monitor.Monitor, b.Monitor.Monitor) && AreaPolicy.Equal(a.Monitor.Work, b.Monitor.Work) &&
                a.IsNormal == b.IsNormal && SameRect(a.ArrangedBounds, b.ArrangedBounds) && SameRect(a.FullscreenBounds, b.FullscreenBounds) &&
                SameRect(a.VisibleBounds, b.VisibleBounds);
        }
        static bool Completed(WindowMoveWorker.Result result)
        { return result != null && result.Outcome == WindowMoveWorker.Outcome.Success && result.CallerExited; }
        static WindowStateActionResult Refused(WindowMoveWorker.Result result, string status)
        {
            int error = result == null ? 0 : result.NativeError;
            if (result != null && result.MutationAttempted && !result.CallerExited)
                status = "Window action is not confirmed; recovery was retained.";
            return Refused(status, error);
        }
        static WindowStateActionResult Refused(string status, int error = 0)
        {
            if (error != 0) status += " (Windows error " + error + ": " + new Win32Exception(error).Message + ").";
            return new WindowStateActionResult(false, status, error);
        }
    }

    internal static class WindowStatePolicy
    {
        // FocusedWindowNative keeps the raw GetProcessTimes FILETIME for
        // retained HWND identity. Worker IPC uses NativeProcessIdentity's UTC
        // DateTime ticks. Convert only at that boundary, never the snapshots.
        internal static long WorkerStarted(FocusedWindowSnapshot snapshot)
        {
            if (snapshot == null || snapshot.ProcessStarted <= 0) return 0;
            try { return DateTime.FromFileTimeUtc(snapshot.ProcessStarted).Ticks; }
            catch (ArgumentOutOfRangeException) { return 0; }
        }
        internal static bool Topmost(FocusedWindowSnapshot snapshot)
        { return snapshot != null && (snapshot.ExtendedStyle & 0x00000008) != 0; }
        internal static bool Verified(FocusedWindowSnapshot snapshot)
        {
            return ReadableIdentity(snapshot) && !snapshot.Shell &&
                !new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame" }
                    .Contains(snapshot.ClassName, StringComparer.OrdinalIgnoreCase) &&
                !FocusIndicatorNative.IsShellSurface(snapshot.ProcessImage, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }
        internal static bool ReadableIdentity(FocusedWindowSnapshot snapshot)
        {
            return snapshot != null && !FocusedWindowPolicy.SpecialHandle(snapshot.Window) && snapshot.Root == snapshot.Window &&
                snapshot.Process != 0 && snapshot.Thread != 0 && snapshot.ProcessStarted > 0 && snapshot.Error == 0 &&
                !String.IsNullOrEmpty(snapshot.ClassName) && !String.IsNullOrEmpty(snapshot.ProcessImage);
        }
        internal static bool Eligible(FocusedWindowSnapshot snapshot, Func<IntPtr, bool> ignored)
        {
            return Verified(snapshot) && FocusedWindowPolicy.Eligible(snapshot, ignored ?? delegate { return false; }) &&
                (snapshot.ExtendedStyle & 0x00000080) == 0;
        }
        internal static bool SameIdentity(FocusedWindowSnapshot original, FocusedWindowSnapshot current)
        {
            return ReadableIdentity(original) && ReadableIdentity(current) && FocusedWindowPolicy.SameWindow(original, current) &&
                String.Equals(original.ProcessImage, current.ProcessImage, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class OwnedWindowPinApi
    {
        internal Func<IntPtr, FocusedWindowSnapshot> Read = delegate(IntPtr window) { return FocusedWindowNative.Read(window, true); };
        internal Func<IntPtr, bool> Exists = Native.IsWindow;
        internal Func<IntPtr, bool, bool> SetTopmost = WindowStateNative.SetTopmost;
        internal Func<IntPtr, string, IntPtr, bool> SetMarker = WindowStateNative.SetPinMarker;
        internal Func<IntPtr, string, IntPtr> ReadMarker = WindowStateNative.ReadPinMarker;
        internal Func<IntPtr, string, IntPtr> RemoveMarker = WindowStateNative.RemovePinMarker;
        internal Func<long> Now = Stopwatch.GetTimestamp;
        internal Action Pause = delegate { Thread.Sleep(10); };
        internal Func<bool> Quiescent = delegate { return WindowMoveWorker.ActiveHelpers == 0 && WindowMoveWorker.UncertainHelpers == 0; };
    }

    // A registry belongs to one controller/helper session. Remember is called
    // only for the target that was non-topmost before an authorized pin. A
    // pre-existing always-on-top window is never adopted just by observing it.
    internal sealed class OwnedWindowPins
    {
        internal const int Limit = 32;
        readonly object gate = new object();
        readonly Dictionary<IntPtr, FocusedWindowSnapshot> entries = new Dictionary<IntPtr, FocusedWindowSnapshot>();
        readonly OwnedWindowPinApi api;
        readonly string marker = "TaskbarSelect.PinnedWindow." + Guid.NewGuid().ToString("N");
        static readonly IntPtr markerValue = new IntPtr(1);
        internal OwnedWindowPins() : this(new OwnedWindowPinApi()) { }
        internal OwnedWindowPins(OwnedWindowPinApi api)
        { if (api == null) throw new ArgumentNullException("api"); this.api = api; }
        internal int Count { get { lock (gate) return entries.Count; } }
        // Observation only: a visual cue cannot borrow cleanup authority or
        // adopt a pre-existing topmost window. No marker or entry is changed.
        internal bool MatchesPinnedObservation(FocusedWindowSnapshot snapshot)
        {
            try { lock (gate) {
                FocusedWindowSnapshot retained;
                if (!WindowStatePolicy.Verified(snapshot) || !WindowStatePolicy.Topmost(snapshot) ||
                    !entries.TryGetValue(snapshot.Window, out retained) || !WindowStatePolicy.SameIdentity(retained, snapshot)) return false;
                var current = Copy(api.Read(snapshot.Window));
                return WindowStatePolicy.SameIdentity(snapshot, current) && WindowStatePolicy.Topmost(current) &&
                    api.Exists(snapshot.Window) && api.ReadMarker(snapshot.Window, marker) == markerValue;
            } } catch (Exception) { return false; }
        }
        internal bool Remember(FocusedWindowSnapshot snapshot)
        {
            if (!WindowStatePolicy.Eligible(snapshot, null) || WindowStatePolicy.Topmost(snapshot)) return false;
            lock (gate) {
                PruneInactive();
                var current = Copy(api.Read(snapshot.Window));
                if (!WindowStatePolicy.SameIdentity(snapshot, current) || !WindowStatePolicy.Eligible(current, null) ||
                    WindowStatePolicy.Topmost(current) || !api.Exists(snapshot.Window)) return false;
                FocusedWindowSnapshot previous;
                if (entries.TryGetValue(snapshot.Window, out previous) && !WindowStatePolicy.SameIdentity(previous, current)) entries.Remove(snapshot.Window);
                if (!entries.ContainsKey(snapshot.Window) && entries.Count >= Limit) return false;
                // A private numeric property is tied to this HWND lifetime.
                // A reused HWND in the same process/thread/class loses it.
                if (!api.SetMarker(snapshot.Window, marker, markerValue)) return false;
                try {
                    current = Copy(api.Read(snapshot.Window));
                    if (!WindowStatePolicy.SameIdentity(snapshot, current) || WindowStatePolicy.Topmost(current) ||
                        api.ReadMarker(snapshot.Window, marker) != markerValue) {
                        RollbackMarker(snapshot.Window); return false;
                    }
                } catch (Exception) { RollbackMarker(snapshot.Window); return false; }
                entries[snapshot.Window] = snapshot.Copy(); return true;
            }
        }
        void RollbackMarker(IntPtr window)
        {
            try { if (api.ReadMarker(window, marker) == markerValue) api.RemoveMarker(window, marker); }
            catch (Exception) { /* No topmost request has been issued yet. */ }
        }
        void PruneInactive()
        {
            // Reclaim capacity without changing any window state. A failed
            // read or lost lifetime marker alone does not prove a pin gone.
            foreach (var pair in entries.ToArray()) {
                try {
                    if (!api.Exists(pair.Key)) { entries.Remove(pair.Key); continue; }
                    var current = Copy(api.Read(pair.Key));
                    if (!WindowStatePolicy.ReadableIdentity(current)) continue;
                    if (!WindowStatePolicy.SameIdentity(pair.Value, current)) entries.Remove(pair.Key);
                    else if (!WindowStatePolicy.Topmost(current)) Forget(current);
                } catch (Exception) { /* Retain an unverified record. */ }
            }
        }
        internal void Forget(FocusedWindowSnapshot snapshot)
        {
            if (snapshot == null) return;
            lock (gate) { FocusedWindowSnapshot previous;
                if (entries.TryGetValue(snapshot.Window, out previous) && WindowStatePolicy.SameIdentity(previous, snapshot)) {
                    var current = Copy(api.Read(snapshot.Window));
                    if (WindowStatePolicy.SameIdentity(previous, current) && api.ReadMarker(snapshot.Window, marker) == markerValue)
                        api.RemoveMarker(snapshot.Window, marker);
                    entries.Remove(snapshot.Window);
                } }
        }
        internal bool ReleaseAll(long deadline)
        {
            try { lock (gate) {
                if (entries.Count == 0) return true;
                long began = api.Now();
                if (deadline <= began || !api.Quiescent()) return false;
                deadline = Math.Min(deadline, began + 2 * Stopwatch.Frequency);
                var posted = new HashSet<IntPtr>();
                // The iteration bound is a second fence for an injected/failing
                // clock; ordinary operation is bounded by the monotonic budget.
                for (int pass = 0; pass < 256 && api.Now() < deadline; pass++) {
                    if (!api.Quiescent()) return false;
                    foreach (var pair in entries.ToArray()) {
                        if (api.Now() >= deadline || !api.Quiescent()) return false;
                        var original = pair.Value;
                        if (!api.Exists(pair.Key)) { entries.Remove(pair.Key); continue; }
                        var current = Copy(api.Read(pair.Key));
                        if (current == null || !WindowStatePolicy.ReadableIdentity(current)) continue;
                        if (!WindowStatePolicy.SameIdentity(original, current)) { entries.Remove(pair.Key); continue; }
                        if (!WindowStatePolicy.Topmost(current)) { Forget(current); continue; }
                        if (!WindowStatePolicy.Verified(current) || api.ReadMarker(pair.Key, marker) != markerValue) continue;
                        if (posted.Contains(pair.Key)) continue;
                        // Re-read just before the one async request. Hidden or
                        // newly excluded owned windows still need restoration.
                        current = Copy(api.Read(pair.Key));
                        if (!WindowStatePolicy.SameIdentity(original, current) || api.ReadMarker(pair.Key, marker) != markerValue) continue;
                        if (!WindowStatePolicy.Topmost(current)) { Forget(current); continue; }
                        if (api.Now() >= deadline || !api.Quiescent()) return false;
                        posted.Add(pair.Key); api.SetTopmost(pair.Key, false);
                    }
                    if (entries.Count == 0) return true;
                    api.Pause();
                }
                // Allow a final nonmutating observation at the deadline.
                foreach (var pair in entries.ToArray()) {
                    if (!api.Exists(pair.Key)) { entries.Remove(pair.Key); continue; }
                    var current = Copy(api.Read(pair.Key));
                    if (current != null && WindowStatePolicy.ReadableIdentity(current)) {
                        if (!WindowStatePolicy.SameIdentity(pair.Value, current)) entries.Remove(pair.Key);
                        else if (!WindowStatePolicy.Topmost(current)) Forget(current);
                    }
                }
                return entries.Count == 0;
            } } catch (Exception) { return false; }
        }
        static FocusedWindowSnapshot Copy(FocusedWindowSnapshot value) { return value == null ? null : value.Copy(); }
    }

    internal static class WindowStateNative
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr window, string name);
        internal static bool Minimize(IntPtr window) { return ShowWindowAsync(window, 6); }
        internal static bool SetTopmost(IntPtr window, bool topmost)
        { return SetWindowPos(window, new IntPtr(topmost ? -1 : -2), 0, 0, 0, 0, 0x4013); }
        internal static bool IsMinimized(IntPtr window) { return IsIconic(window); }
        internal static bool IsTopmost(IntPtr window)
        { return ((IntPtr.Size == 8 ? GetWindowLongPtr64(window, -20).ToInt64() : GetWindowLong32(window, -20)) & 8) != 0; }
        internal static bool SetPinMarker(IntPtr window, string name, IntPtr value) { return SetProp(window, name, value); }
        internal static IntPtr ReadPinMarker(IntPtr window, string name) { return GetProp(window, name); }
        internal static IntPtr RemovePinMarker(IntPtr window, string name) { return RemoveProp(window, name); }
    }
}
