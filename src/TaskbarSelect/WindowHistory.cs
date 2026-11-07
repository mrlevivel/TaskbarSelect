using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarSelect
{
    internal sealed class WindowHistory
    {
        internal enum EntryKind { Move, Minimize }
        enum MinimizeOwnership { Unknown, Stale, Owned }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Placement
        {
            public int Length, Flags, ShowCommand;
            public Point MinPosition, MaxPosition;
            public Native.Rect NormalPosition;
        }
        internal sealed class Entry
        {
            internal EntryKind Kind;
            internal IntPtr Window;
            internal uint Process;
            internal long ProcessStarted;
            internal Placement Placement;
            internal Native.MonitorInfo Monitor;
            internal bool IsNormal;
            internal Native.Rect? ArrangedBounds;
            internal Native.Rect? FullscreenBounds;
            internal Native.Rect? VisibleBounds;
            internal FocusedWindowSnapshot FocusedIdentity;
            internal Entry Copy()
            {
                var copy = (Entry)MemberwiseClone();
                copy.FocusedIdentity = FocusedIdentity == null ? null : FocusedIdentity.Copy();
                return copy;
            }
        }
        // Observations are injectable without replacing the restore delegates or
        // the shared history stack. Pure checks never touch desktop windows.
        internal sealed class ObservationApi
        {
            internal Func<Entry, bool> Alive = NativeAlive;
            internal Func<IntPtr, bool> Exists = Native.IsWindow;
            internal Func<IntPtr, bool> Minimized = IsIconic, Maximized = IsZoomed;
            internal Func<string, Native.MonitorInfo?> Monitor = ReadMonitor;
            internal Func<IntPtr, Native.MonitorInfo?> WindowMonitor = ReadWindowMonitor;
            internal Func<IntPtr, Native.Rect?> Bounds = ReadBounds;
            internal Func<IntPtr, Placement?> Placement = ReadPlacement;
            internal Func<IntPtr, FocusedWindowSnapshot> Identity = delegate(IntPtr window) { return FocusedWindowNative.Read(window, true); };
            internal Func<long> Now = Stopwatch.GetTimestamp;
            internal Action Pause = delegate { Thread.Sleep(5); };
        }
        readonly Stack<Entry> entries = new Stack<Entry>();
        readonly ObservationApi observations;
        internal bool LastUndoFullscreen { get; private set; }
        internal bool LastUndoMinimized { get; private set; }
        internal bool LastUndoMinimizeUnavailable { get; private set; }
        internal WindowHistory() : this(new ObservationApi()) { }
        internal WindowHistory(ObservationApi api)
        { if (api == null) throw new ArgumentNullException("api"); observations = api; }
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
        internal bool Available { get { Prune(); return entries.Count > 0; } }
        static Native.MonitorInfo? ReadMonitor(string device)
        { Native.MonitorInfo monitor; return Native.TryMonitor(device, out monitor) ? monitor : (Native.MonitorInfo?)null; }
        static Native.MonitorInfo? ReadWindowMonitor(IntPtr window)
        {
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            return Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref monitor) ? monitor : (Native.MonitorInfo?)null;
        }
        static Native.Rect? ReadBounds(IntPtr window)
        { Native.Rect bounds; return Native.GetWindowRect(window, out bounds) ? bounds : (Native.Rect?)null; }
        static Placement? ReadPlacement(IntPtr window)
        {
            var placement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
            return GetWindowPlacement(window, ref placement) ? placement : (Placement?)null;
        }

        static long ProcessStarted(uint pid)
        {
            return NativeProcessIdentity.Started(pid);
        }
        internal static Entry Capture(IntPtr window)
        {
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            var placement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            long started = ProcessStarted(pid);
            if (started == 0 || !GetWindowPlacement(window, ref placement) ||
                !Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref monitor)) return null;
            // The taskbar may have just minimized an active window during this click.
            if (placement.ShowCommand == 2) placement.ShowCommand = (placement.Flags & 2) != 0 ? 3 : 1;
            bool normal = !IsIconic(window) && !IsZoomed(window);
            Native.Rect? actual = ReadBounds(window);
            Native.Rect? arranged = normal && TaskbarMover.Arranged(window) ? actual : null;
            return new Entry { Window = window, Process = pid, ProcessStarted = started, Placement = placement,
                Monitor = monitor, IsNormal = normal, ArrangedBounds = arranged, VisibleBounds = actual };
        }
        internal void Remember(Entry entry)
        {
            if (entry == null) return;
            if (entries.Count >= 10) {
                var recent = entries.ToArray(); entries.Clear();
                for (int index = 8; index >= 0; index--) entries.Push(recent[index]);
            }
            entries.Push(entry.Copy());
        }
        internal bool RememberMinimized(Entry entry)
        {
            try {
                if (entry == null || entry.Kind != EntryKind.Minimize || MinimizeState(entry) != MinimizeOwnership.Owned) return false;
                Remember(entry); return true;
            } catch (Exception) { return false; }
        }
        static bool NativeAlive(Entry entry)
        {
            uint pid;
            Native.GetWindowThreadProcessId(entry.Window, out pid);
            return Native.IsWindow(entry.Window) && pid == entry.Process && ProcessStarted(pid) == entry.ProcessStarted;
        }
        bool Alive(Entry entry)
        {
            return entry.Kind == EntryKind.Minimize ? observations.Exists(entry.Window) &&
                WindowStatePolicy.SameIdentity(entry.FocusedIdentity, observations.Identity(entry.Window)) : observations.Alive(entry);
        }
        void Prune()
        {
            // An obsolete minimize is an explicit chronology barrier. One Undo
            // consumes it without unexpectedly moving an older window instead.
            while (entries.Count > 0 && entries.Peek().Kind != EntryKind.Minimize && !Alive(entries.Peek())) entries.Pop();
        }
        static bool SameMonitor(Native.MonitorInfo left, Native.MonitorInfo right)
        { return String.Equals(left.Device, right.Device, StringComparison.OrdinalIgnoreCase) && AreaPolicy.Equal(left.Monitor, right.Monitor) && AreaPolicy.Equal(left.Work, right.Work); }
        MinimizeOwnership MinimizeState(Entry entry)
        {
            try {
            if (entry.FocusedIdentity == null || entry.Window != entry.FocusedIdentity.Window || entry.Process != entry.FocusedIdentity.Process ||
                entry.ProcessStarted == 0 || entry.ProcessStarted != WindowStatePolicy.WorkerStarted(entry.FocusedIdentity) ||
                !entry.VisibleBounds.HasValue) return MinimizeOwnership.Stale;
            if (!observations.Exists(entry.Window)) return MinimizeOwnership.Stale;
            var current = observations.Identity(entry.Window);
            if (!WindowStatePolicy.ReadableIdentity(current)) return MinimizeOwnership.Unknown;
            if (!WindowStatePolicy.SameIdentity(entry.FocusedIdentity, current) || !WindowStatePolicy.Eligible(current, null) ||
                !observations.Minimized(entry.Window)) return MinimizeOwnership.Stale;
            var placement = observations.Placement(entry.Window);
            var monitor = observations.WindowMonitor(entry.Window);
            if (!placement.HasValue || !monitor.HasValue) return MinimizeOwnership.Unknown;
            return AreaPolicy.Equal(placement.Value.NormalPosition, entry.Placement.NormalPosition) && SameMonitor(entry.Monitor, monitor.Value)
                ? MinimizeOwnership.Owned : MinimizeOwnership.Stale;
            } catch (Exception) { return MinimizeOwnership.Unknown; }
        }
        bool RestoredMinimize(Entry entry)
        {
            if (!Alive(entry) || observations.Minimized(entry.Window) ||
                observations.Maximized(entry.Window) != (entry.Placement.ShowCommand == 3)) return false;
            var actual = observations.Bounds(entry.Window);
            var monitor = observations.WindowMonitor(entry.Window);
            return actual.HasValue && entry.VisibleBounds.HasValue && AreaPolicy.Equal(actual.Value, entry.VisibleBounds.Value) &&
                monitor.HasValue && SameMonitor(entry.Monitor, monitor.Value);
        }
        internal bool Undo()
        { return Undo(SetWindowPos, SetWindowPlacement, delegate(Entry entry) { }); }
        internal bool Undo(TaskbarMover.MovePosition positionApi, TaskbarMover.MovePlacement placementApi, Action<Entry> beforeMutation)
        {
            LastUndoFullscreen = false;
            LastUndoMinimized = false;
            LastUndoMinimizeUnavailable = false;
            Prune();
            if (entries.Count == 0) return false;
            Entry entry = entries.Peek();
            MinimizeOwnership minimized = entry.Kind == EntryKind.Minimize ? MinimizeState(entry) : MinimizeOwnership.Owned;
            if (minimized == MinimizeOwnership.Unknown) return false;
            if (minimized == MinimizeOwnership.Stale) {
                // Positive target evidence takes precedence over an unavailable
                // saved display, so stale history cannot become a permanent barrier.
                entries.Pop(); LastUndoMinimizeUnavailable = true; return false;
            }
            Native.MonitorInfo? found = observations.Monitor(entry.Monitor.Device);
            if (!found.HasValue && entry.Kind == EntryKind.Minimize) return false;
            if (!found.HasValue || !SameMonitor(entry.Monitor, found.Value)) {
                LastUndoMinimizeUnavailable = entry.Kind == EntryKind.Minimize;
                entries.Pop();
                return false;
            }
            Native.MonitorInfo current = found.Value;
            beforeMutation(entry);
            if (entry.Kind == EntryKind.Minimize) {
                // Recheck after the mover's bounded-operation admission. Do not
                // restore a window the user has already restored independently.
                minimized = MinimizeState(entry);
                if (minimized == MinimizeOwnership.Unknown) return false;
                if (minimized == MinimizeOwnership.Stale) { entries.Pop(); LastUndoMinimizeUnavailable = true; return false; }
                var restore = entry.Placement;
                // Undo is an explicit request to bring this window back. Keep
                // the captured visible state so a normal restore can activate
                // it, just as ordinary movement Undo already does.
                restore.ShowCommand = entry.Placement.ShowCommand == 3 ? 3 : 1;
                if (entry.ArrangedBounds.HasValue) {
                    restore.NormalPosition = entry.ArrangedBounds.Value;
                    restore.NormalPosition.Left += current.Monitor.Left - current.Work.Left;
                    restore.NormalPosition.Right += current.Monitor.Left - current.Work.Left;
                    restore.NormalPosition.Top += current.Monitor.Top - current.Work.Top;
                    restore.NormalPosition.Bottom += current.Monitor.Top - current.Work.Top;
                    restore.Flags = 0;
                }
                if (!placementApi(entry.Window, ref restore)) return false;
                long deadline = observations.Now() + Stopwatch.Frequency / 2;
                for (int pass = 0; pass < 100 && observations.Now() < deadline; pass++) {
                    if (RestoredMinimize(entry)) {
                        entries.Pop(); LastUndoMinimized = true; return true;
                    }
                    if (!Alive(entry)) return false;
                    observations.Pause();
                }
                return false;
            }
            var placement = entry.Placement;
            if (entry.FullscreenBounds.HasValue) {
                var bounds = entry.FullscreenBounds.Value;
                if (observations.Minimized(entry.Window)) {
                    var restore = placement;
                    restore.ShowCommand = restore.ShowCommand == 3 ? 3 : 4;
                    if (!placementApi(entry.Window, ref restore) || observations.Minimized(entry.Window)) return false;
                }
                if (!positionApi(entry.Window, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x10 | 0x200 | 0x4)) return false;
                var observed = observations.Bounds(entry.Window);
                if (!Alive(entry) || !observed.HasValue || !FullscreenMovePolicy.Matches(observed.Value, bounds)) return false;
                entries.Pop(); LastUndoFullscreen = true; return true;
            }
            if (entry.ArrangedBounds.HasValue) {
                var actual = entry.ArrangedBounds.Value;
                // Arranged WINDOWPLACEMENT contains the old floating restore rectangle,
                // not the visible snapped geometry. Keep an already-normal window intact.
                if (observations.Maximized(entry.Window) || observations.Minimized(entry.Window)) {
                    placement.NormalPosition = actual;
                    placement.NormalPosition.Left += current.Monitor.Left - current.Work.Left;
                    placement.NormalPosition.Right += current.Monitor.Left - current.Work.Left;
                    placement.NormalPosition.Top += current.Monitor.Top - current.Work.Top;
                    placement.NormalPosition.Bottom += current.Monitor.Top - current.Work.Top;
                    placement.ShowCommand = 4; placement.Flags = 0;
                    if (!placementApi(entry.Window, ref placement)) return false;
                }
                if (!positionApi(entry.Window, IntPtr.Zero, actual.Left, actual.Top,
                    actual.Right - actual.Left, actual.Bottom - actual.Top, 0x10 | 0x200 | 0x4)) return false;
                entries.Pop();
                return true;
            }
            bool keepMaximized = placement.ShowCommand == 3 && observations.Maximized(entry.Window);
            if (keepMaximized) {
                // Use the same relocation as a maximized move, keeping the window
                // maximized before applying its saved floating restore bounds.
                var monitor = observations.WindowMonitor(entry.Window);
                if (!monitor.HasValue) return false;
                var work = current.Work;
                if (!SameMonitor(current, monitor.Value) && !positionApi(entry.Window, IntPtr.Zero, work.Left, work.Top,
                    work.Right - work.Left, work.Bottom - work.Top, 0x10 | 0x200 | 0x4)) return false;
            } else if (placement.ShowCommand == 3 || observations.Maximized(entry.Window)) {
                // Leave the current maximized monitor before applying saved placement.
                // Otherwise Windows can reuse that monitor's restore bounds, even when
                // the saved state is normal rather than maximized.
                var restored = placement;
                restored.ShowCommand = 4;
                if (!placementApi(entry.Window, ref restored)) return false;
            }
            if (!placementApi(entry.Window, ref placement)) return false;
            if (keepMaximized) {
                var observed = observations.Placement(entry.Window);
                var monitor = observations.WindowMonitor(entry.Window);
                if (!Alive(entry) || !observed.HasValue || observed.Value.ShowCommand != 3 ||
                    !monitor.HasValue || !SameMonitor(current, monitor.Value) ||
                    !AreaPolicy.Equal(observed.Value.NormalPosition, entry.Placement.NormalPosition)) return false;
            }
            entries.Pop();
            return true;
        }
    }
}
