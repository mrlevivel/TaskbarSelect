using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class ShelfWindow
    {
        internal IntPtr Handle;
        internal uint Process;
        internal string AppKey, AppName, Title, Path;
        internal long ProcessStarted;
        internal IntPtr SelectionForeground;
        internal long SelectionTimestamp;
        internal ShelfSelectionOrigin SelectionOrigin;
        public override string ToString() { return String.IsNullOrWhiteSpace(Title) ? (AppName ?? "Application") : Title; }
    }

    internal enum ShelfLocalAction { Focus, Minimize, Restore }
    internal enum ShelfSelectionOrigin { DirectIcon, Chooser }

    // Session-only authority for a successful explicit local shelf minimize. No titles or history.
    internal sealed class ShelfMinimizeRegistry : IDisposable
    {
        internal const int Capacity = 64, GraceSeconds = 3;
        sealed class Entry
        {
            internal ShelfWindow Identity;
            internal long Requested;
            internal bool ObservedIconic, RestoreMaximized, RestoreRequested;
        }
        readonly object gate = new object();
        readonly List<Entry> entries = new List<Entry>();
        readonly Func<long> timestamp;
        bool disposed;
        internal ShelfMinimizeRegistry() : this(Stopwatch.GetTimestamp) { }
        internal ShelfMinimizeRegistry(Func<long> timestamp) { this.timestamp = timestamp; }
        static ShelfWindow Identity(ShelfWindow window)
        { return new ShelfWindow { Handle = window.Handle, Process = window.Process, ProcessStarted = window.ProcessStarted, AppKey = window.AppKey }; }
        internal void RecordSuccessfulMinimize(ShelfWindow window, bool restoreMaximized = false)
        {
            if (!WindowShelfPolicy.SameWindow(window, window)) return;
            lock (gate) {
                if (disposed) return;
                entries.RemoveAll(e => e.Identity.Handle == window.Handle);
                if (entries.Count == Capacity) entries.RemoveAt(0);
                entries.Add(new Entry { Identity = Identity(window), Requested = timestamp(), RestoreMaximized = restoreMaximized });
            }
        }
        internal void RemoveExcluded(List<AppRule> exclusions)
        {
            if (exclusions == null) return;
            lock (gate) entries.RemoveAll(e => AppCatalog.Matches(exclusions, e.Identity.AppKey));
        }
        internal ShelfWindow[] Snapshot()
        { lock (gate) return entries.Select(e => Identity(e.Identity)).ToArray(); }
        internal bool TryRead(ShelfWindow expected, List<AppRule> exclusions, out ShelfWindow current)
        {
            current = null;
            if (expected == null) return false;
            Entry entry;
            lock (gate) entry = entries.FirstOrDefault(e => WindowShelfPolicy.SameWindow(e.Identity, expected));
            if (entry == null) return false;
            ShelfWindow fresh; bool iconic;
            // Native calls run outside the lock; a concurrent clear/replacement wins below.
            bool valid = WindowShelfCatalog.TryReadTrackedWindow(entry.Identity, out fresh, out iconic) &&
                !AppCatalog.Matches(exclusions ?? new List<AppRule>(), entry.Identity.AppKey);
            lock (gate) {
                if (disposed || !entries.Contains(entry)) return false;
                long elapsed = timestamp() - entry.Requested;
                if (!valid || (!iconic && (entry.ObservedIconic || elapsed < 0 || elapsed > GraceSeconds * Stopwatch.Frequency))) {
                    entries.Remove(entry); return false;
                }
                if (iconic) entry.ObservedIconic = true;
                current = fresh; return true;
            }
        }
        internal bool RestoreMaximized(ShelfWindow expected, List<AppRule> exclusions)
        {
            ShelfWindow current;
            if (!TryRead(expected, exclusions, out current)) return false;
            lock (gate) return entries.Any(e => WindowShelfPolicy.SameWindow(e.Identity, current) && e.RestoreMaximized);
        }
        internal void RequestedRestore(ShelfWindow expected)
        {
            lock (gate) {
                var entry = entries.FirstOrDefault(e => WindowShelfPolicy.SameWindow(e.Identity, expected));
                if (entry != null) entry.RestoreRequested = true;
            }
        }
        internal void ObserveRestore(IntPtr window, List<AppRule> exclusions)
        {
            Entry entry;
            lock (gate) entry = entries.FirstOrDefault(e => e.Identity.Handle == window && e.RestoreRequested);
            if (entry == null) return;
            ShelfWindow current; bool iconic;
            bool restored = WindowShelfCatalog.TryReadTrackedWindow(entry.Identity, out current, out iconic) && !iconic &&
                !AppCatalog.Matches(exclusions ?? new List<AppRule>(), entry.Identity.AppKey);
            // EVENT_SYSTEM_MINIMIZEEND proves the queued native transition occurred,
            // including when no catalog read observed its intermediate iconic state.
            if (restored) lock (gate) entries.Remove(entry);
        }
        public void Dispose() { lock (gate) { disposed = true; entries.Clear(); } }
    }

    // Hidden sources can yield an empty DWM surface despite successful registration.
    // Keep their existing selectable icon fallback instead of displaying a blank preview.
    internal sealed class ShelfRecoveryThumbnailApi : IShelfThumbnailApi
    {
        readonly IShelfThumbnailApi native = new ShelfDwmApi();
        readonly Dictionary<IntPtr, IntPtr> sources = new Dictionary<IntPtr, IntPtr>();
        public int Register(IntPtr destination, IntPtr source, out IntPtr thumbnail)
        {
            thumbnail = IntPtr.Zero;
            if (!Native.IsWindowVisible(source)) return -1;
            int result = native.Register(destination, source, out thumbnail);
            if (result == 0 && thumbnail != IntPtr.Zero) sources[thumbnail] = source;
            return result;
        }
        bool Visible(IntPtr thumbnail)
        { IntPtr source; return sources.TryGetValue(thumbnail, out source) && Native.IsWindowVisible(source); }
        public int Query(IntPtr thumbnail, out Size size)
        { size = Size.Empty; return Visible(thumbnail) ? native.Query(thumbnail, out size) : -1; }
        public int Update(IntPtr thumbnail, Rectangle bounds, byte opacity)
        { return Visible(thumbnail) ? native.Update(thumbnail, bounds, opacity) : -1; }
        public void Unregister(IntPtr thumbnail) { sources.Remove(thumbnail); native.Unregister(thumbnail); }
    }

    internal static class WindowShelfPolicy
    {
        internal const int DefaultActivationWidth = 480, MinActivationWidth = 80, MaxActivationWidth = 960;
        internal const int DefaultActivationHeight = 6, MinActivationHeight = 1, MaxActivationHeight = 96;
        internal const int DefaultHoverDelayMs = 100, MinHoverDelayMs = 100, MaxHoverDelayMs = 1500;
        internal const int ActivationDepth = DefaultActivationHeight, ActivationPollIntervalMs = 50;
        internal static int ClampActivationWidth(int value) { return Math.Max(MinActivationWidth, Math.Min(MaxActivationWidth, value)); }
        internal static int ClampActivationHeight(int value) { return Math.Max(MinActivationHeight, Math.Min(MaxActivationHeight, value)); }
        internal static int ClampHoverDelayMs(int value) { return Math.Max(MinHoverDelayMs, Math.Min(MaxHoverDelayMs, value)); }
        internal static Rectangle Bounds(Display d) { return new Rectangle(d.X, d.Y, d.Width, d.Height); }
        internal static Rectangle Trigger(Display d)
        { return Trigger(d, DefaultActivationWidth); }
        internal static Rectangle Trigger(Display d, int activationWidth)
        { return Trigger(d, activationWidth, DefaultActivationHeight); }
        internal static Rectangle Trigger(Display d, int activationWidth, int activationHeight)
        {
            int width = Math.Min(ClampActivationWidth(activationWidth), d.Width);
            // A shared desktop seam does not stop the pointer like an outer
            // edge. Keep a reachable hover band entirely inside this monitor.
            int height = Math.Min(ClampActivationHeight(activationHeight), d.Height);
            return new Rectangle(d.X + (d.Width - width) / 2, d.Y + d.Height - height, width, height);
        }
        internal static bool SameTarget(Display a, Display b)
        {
            return a != null && b != null && !a.Primary && !b.Primary && !String.IsNullOrEmpty(a.Identity) &&
                String.Equals(a.Identity, b.Identity, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.Device, b.Device, StringComparison.OrdinalIgnoreCase) && Bounds(a) == Bounds(b);
        }
        internal static bool SameWindow(ShelfWindow a, ShelfWindow b)
        {
            return a != null && b != null && a.Handle != IntPtr.Zero && a.Handle == b.Handle &&
                a.Process != 0 && a.Process == b.Process && a.ProcessStarted > 0 && a.ProcessStarted == b.ProcessStarted &&
                !String.IsNullOrEmpty(a.AppKey) && String.Equals(a.AppKey, b.AppKey, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool CanListOnTarget(string source, string target)
        { return !String.IsNullOrWhiteSpace(source) && !String.IsNullOrWhiteSpace(target); }
        internal static ShelfLocalAction LocalAction(bool minimized, bool foreground, ShelfSelectionOrigin origin)
        { return minimized ? ShelfLocalAction.Restore : foreground && origin == ShelfSelectionOrigin.DirectIcon ? ShelfLocalAction.Minimize : ShelfLocalAction.Focus; }
        internal static bool FreshSelection(long captured, long now)
        { return captured > 0 && now >= captured && (now - captured) / (double)Stopwatch.Frequency <= 1; }
        internal static int Scale(int pixels, int dpi) { return (int)Math.Round(pixels * Math.Max(96, dpi) / 96.0); }
        internal static int Capacity(int width) { return Capacity(width, 96); }
        internal static int Capacity(int width, int dpi) { return Math.Max(1, Math.Min(10, (width - Scale(116, dpi)) / Scale(44, dpi))); }
        internal static Rectangle Fit(Display d, int width, int height, int bottom)
        {
            width = Math.Max(1, Math.Min(width, d.Width)); height = Math.Max(1, Math.Min(height, d.Height));
            return new Rectangle(d.X + (d.Width - width) / 2, Math.Max(d.Y, Math.Min(bottom - height, d.Y + d.Height - height)), width, height);
        }
        internal static List<List<ShelfWindow>> Group(IEnumerable<ShelfWindow> windows)
        {
            return windows.GroupBy(w => w.AppKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.First().AppName, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => g.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList()).ToList();
        }
        internal static bool AcceptResult(int expected, int current, bool disposed, bool eligible)
        { return expected == current && !disposed && eligible; }
        internal static bool DwellReady(long entered, long now, bool quiet, bool inside)
        { return DwellReady(entered, now, quiet, inside, DefaultHoverDelayMs); }
        internal static bool DwellReady(long entered, long now, bool quiet, bool inside, int hoverDelayMs)
        { return quiet && inside && now >= entered && now - entered >= ClampHoverDelayMs(hoverDelayMs); }
        internal static bool HideReady(long outside, long now)
        { return outside >= 0 && now >= outside && now - outside >= 400; }
        internal static bool Fullscreen(Rectangle window, Rectangle monitor, bool minimized, bool maximized, bool caption)
        {
            return !minimized && (!maximized || !caption) && window.Left <= monitor.Left + 2 && window.Top <= monitor.Top + 2 &&
                window.Right >= monitor.Right - 2 && window.Bottom >= monitor.Bottom - 2;
        }
        internal static bool Fullscreen(Rectangle window, Rectangle monitor, bool minimized, bool maximized, bool caption, int style)
        {
            // Custom-caption desktop apps still have native resize/maximize
            // controls. Their maximized work area can equal the whole monitor
            // when its taskbar is hidden; moving them must use the destination's
            // reserved work area. Popup/borderless fullscreen keeps its opt-in.
            if (maximized && (style & unchecked((int)0x80000000)) == 0 && (style & 0x00050000) == 0x00050000) return false;
            return Fullscreen(window, monitor, minimized, maximized, caption);
        }
        internal static bool TaskSwitchable(bool visible, bool owned, bool enabled, int extendedStyle, bool cloaked, bool fullscreen, bool titled, bool allowFullscreen = false)
        { return visible && !owned && enabled && (extendedStyle & (0x80 | 0x08000000)) == 0 && !cloaked && (!fullscreen || allowFullscreen) && titled; }
        internal static bool Dragged(Point origin, Point current, Size threshold)
        { return TaskbarInput.IsDirectDrag(new TaskbarMover.Point { X = origin.X, Y = origin.Y }, new TaskbarMover.Point { X = current.X, Y = current.Y }, threshold); }
    }

    // Shared by the manager and deterministic hover tests; changing sensitivity starts a fresh dwell.
    internal sealed class ShelfActivationDwell
    {
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal int DelayMs { get; private set; }
        long entered = -1, observed = -1;
        internal ShelfActivationDwell()
        { Width = WindowShelfPolicy.DefaultActivationWidth; Height = WindowShelfPolicy.DefaultActivationHeight; DelayMs = WindowShelfPolicy.DefaultHoverDelayMs; }
        internal bool Configure(int width, int delayMs)
        { return Configure(width, WindowShelfPolicy.DefaultActivationHeight, delayMs); }
        internal bool Configure(int width, int height, int delayMs)
        {
            width = WindowShelfPolicy.ClampActivationWidth(width); height = WindowShelfPolicy.ClampActivationHeight(height);
            delayMs = WindowShelfPolicy.ClampHoverDelayMs(delayMs);
            if (Width == width && Height == height && DelayMs == delayMs) return false;
            Width = width; Height = height; DelayMs = delayMs; Reset(); return true;
        }
        internal bool Update(long now, bool quiet, bool inside)
        {
            if (!quiet || !inside) { Reset(); return false; }
            if (entered < 0 || now < observed) entered = now;
            observed = now;
            return WindowShelfPolicy.DwellReady(entered, now, quiet, inside, DelayMs);
        }
        internal void Reset() { entered = observed = -1; }
    }

    internal static class WindowShelfCatalog
    {
        delegate bool WindowCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr window, ref WindowHistory.Placement placement);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr handle, out long created, out long exited, out long kernel, out long user);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, StringBuilder text, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")] static extern uint GetClassLong(IntPtr window, int index);
        static readonly uint ownProcess = GetCurrentProcessId();
        // Test-only scope. No captions, app keys, paths or foreign data are retained.
        [ThreadStatic] internal static IntPtr TraceOwnedWindow;
        [ThreadStatic] internal static string OwnedWindowTrace;
        static bool TraceOwned(IntPtr hwnd, string stage, bool success)
        {
            if (hwnd != IntPtr.Zero && hwnd == TraceOwnedWindow) {
                uint process;
                if (Native.GetWindowThreadProcessId(hwnd, out process) != 0 && process == ownProcess) {
                    string entry = stage + "=" + success;
                    if (String.IsNullOrEmpty(OwnedWindowTrace)) OwnedWindowTrace = entry;
                    else if (OwnedWindowTrace.Length + entry.Length + 1 <= 2048) OwnedWindowTrace += ";" + entry;
                } else {
                    // Loss of proof stops observation without retaining foreign identity.
                    const string lost = ";trace.ownedProof=False";
                    string previous = OwnedWindowTrace ?? "";
                    OwnedWindowTrace = previous.Substring(0, Math.Min(previous.Length, 2048 - lost.Length)) + lost;
                    TraceOwnedWindow = IntPtr.Zero;
                }
            }
            return success;
        }

        static bool VisibleApp(IntPtr hwnd) { return EligibleApp(hwnd, false); }
        static bool EligibleApp(IntPtr hwnd, bool tracked)
        {
            if (!Native.IsWindow(hwnd)) return TraceOwned(hwnd, "eligible.window", false);
            if (!Native.IsWindowVisible(hwnd) && !(tracked && IsIconic(hwnd))) return TraceOwned(hwnd, "eligible.visible", false);
            if (GetAncestor(hwnd, 2) != hwnd) return TraceOwned(hwnd, "eligible.root", false);
            if (hwnd == Native.GetShellWindow()) return TraceOwned(hwnd, "eligible.notShell", false);
            uint pid;
            if (Native.GetWindowThreadProcessId(hwnd, out pid) == 0 || pid == 0) return TraceOwned(hwnd, "eligible.process", false);
            // Our full-size peek must not be mistaken for a fullscreen app that suppresses the shelf.
            if (pid == ownProcess && (GetWindowLong(hwnd, -20) & 0x08000000) != 0) return TraceOwned(hwnd, "eligible.ownNoActivate", false);
            int cloaked;
            if (DwmGetWindowAttribute(hwnd, 14, out cloaked, sizeof(int)) != 0) return TraceOwned(hwnd, "eligible.cloakRead", false);
            if (cloaked != 0) return TraceOwned(hwnd, "eligible.notCloaked", false);
            var name = new StringBuilder(256);
            Native.GetClassName(hwnd, name, name.Capacity);
            return TraceOwned(hwnd, "eligible.class", !new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame" }.Contains(name.ToString()));
        }
        internal static bool IsTaskbarCandidate(IntPtr hwnd)
        {
            // Resolve an app's taskbar windows without counting its shell/helper windows.
            // Titles and fullscreen policy belong to later selection/movement checks.
            return EligibleApp(hwnd, false) && WindowShelfPolicy.TaskSwitchable(true, GetWindow(hwnd, 4) != IntPtr.Zero,
                IsWindowEnabled(hwnd), GetWindowLong(hwnd, -20), false, false, true, true);
        }
        static bool Monitor(IntPtr hwnd, out Native.MonitorInfo info)
        {
            info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            return Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), ref info);
        }
        internal static bool IsFullscreen(IntPtr hwnd)
        {
            if (IsIconic(hwnd)) return false;
            Native.Rect rect; Native.MonitorInfo monitor;
            if (!Monitor(hwnd, out monitor) || !Native.GetWindowRect(hwnd, out rect)) return true;
            int style = GetWindowLong(hwnd, -16);
            return WindowShelfPolicy.Fullscreen(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
                Rectangle.FromLTRB(monitor.Monitor.Left, monitor.Monitor.Top, monitor.Monitor.Right, monitor.Monitor.Bottom),
                false, IsZoomed(hwnd), (style & 0x00C00000) != 0, style);
        }
        // Occupancy and movement have different minimized semantics. An iconic
        // fullscreen app does not cover a monitor, but restoring it during a move
        // must still require the opt-in and use full monitor bounds.
        internal static bool IsFullscreenForMovement(IntPtr hwnd)
        {
            bool fullscreen; Native.Rect bounds;
            bool read = TryReadFullscreenMovement(hwnd, out fullscreen, out bounds);
            TraceOwned(hwnd, "fullscreen.read", read);
            return !read || fullscreen;
        }
        internal static bool TryReadFullscreenMovement(IntPtr hwnd, out bool fullscreen, out Native.Rect bounds)
        {
            fullscreen = false; bounds = new Native.Rect();
            Native.MonitorInfo monitor;
            if (!Monitor(hwnd, out monitor)) return false;
            int style = GetWindowLong(hwnd, -16); bool maximized = IsZoomed(hwnd);
            if (IsIconic(hwnd)) {
                var placement = new WindowHistory.Placement { Length = Marshal.SizeOf(typeof(WindowHistory.Placement)) };
                if (!GetWindowPlacement(hwnd, ref placement) || !FullscreenMovePolicy.TryRestoreBounds(placement.NormalPosition,
                    monitor, (GetWindowLong(hwnd, -20) & 0x80) != 0, out bounds)) return false;
                maximized = (placement.Flags & 2) != 0;
                // A maximized popup can retain a smaller floating restore rect.
                // Its fullscreen max bounds are app-defined, so that rectangle
                // cannot authorize either ordinary movement or invented Undo
                // bounds. Restore the app visibly first, then retry the move.
                // Ordinary captioned/custom-caption desktop maximization is known.
                var full = Rectangle.FromLTRB(monitor.Monitor.Left, monitor.Monitor.Top, monitor.Monitor.Right, monitor.Monitor.Bottom);
                if (maximized && WindowShelfPolicy.Fullscreen(full, full, false, true, (style & 0x00C00000) != 0, style)) return false;
            } else if (!Native.GetWindowRect(hwnd, out bounds)) return false;
            fullscreen = WindowShelfPolicy.Fullscreen(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                Rectangle.FromLTRB(monitor.Monitor.Left, monitor.Monitor.Top, monitor.Monitor.Right, monitor.Monitor.Bottom),
                false, maximized, (style & 0x00C00000) != 0, style);
            return true;
        }
        internal static bool DestinationFullscreen(Display target)
        {
            bool found = false;
            bool enumerated = EnumWindows(delegate(IntPtr hwnd, IntPtr unused) {
                Native.MonitorInfo monitor;
                if (VisibleApp(hwnd) && !IsIconic(hwnd) && Monitor(hwnd, out monitor) &&
                    String.Equals(monitor.Device, target.Device, StringComparison.OrdinalIgnoreCase) && IsFullscreen(hwnd)) {
                    found = true; return false;
                }
                return true;
            }, IntPtr.Zero);
            return found || !enumerated;
        }
        static long Started(uint pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return 0;
            try {
                long created, exited, kernel, user;
                return GetProcessTimes(process, out created, out exited, out kernel, out user) ? DateTime.FromFileTimeUtc(created).Ticks : 0;
            } finally { CloseHandle(process); }
        }
        internal static bool TryReadWindow(IntPtr hwnd, out ShelfWindow window)
        { return ReadCandidate(hwnd, false, out window); }
        internal static bool TryReadWindow(IntPtr hwnd, bool allowFullscreen, out ShelfWindow window)
        { return ReadCandidate(hwnd, false, out window, allowFullscreen); }
        internal static bool TryReadTrackedWindow(ShelfWindow expected, out ShelfWindow window, out bool iconic)
        {
            iconic = false;
            // Session recovery authority survives a fullscreen option change;
            // the final selection and shared mover apply the current policy.
            if (!ReadCandidate(expected.Handle, true, out window, true) || !WindowShelfPolicy.SameWindow(expected, window)) return false;
            iconic = IsIconic(expected.Handle);
            return Native.IsWindowVisible(expected.Handle) || iconic;
        }
        static bool ReadCandidate(IntPtr hwnd, bool tracked, out ShelfWindow window, bool allowFullscreen = false)
        {
            window = null;
            if (!EligibleApp(hwnd, tracked)) return TraceOwned(hwnd, "candidate.eligible", false);
            // Preserve the original left-to-right native argument evaluations.
            bool owned = GetWindow(hwnd, 4) != IntPtr.Zero;
            bool enabled = IsWindowEnabled(hwnd);
            int extendedStyle = GetWindowLong(hwnd, -20);
            bool fullscreen = IsFullscreenForMovement(hwnd);
            bool switchable = WindowShelfPolicy.TaskSwitchable(true, owned, enabled, extendedStyle, false, fullscreen, true, allowFullscreen);
            TraceOwned(hwnd, "switch.ownerAbsent", !owned);
            TraceOwned(hwnd, "switch.enabled", enabled);
            TraceOwned(hwnd, "switch.style", (extendedStyle & (0x80 | 0x08000000)) == 0);
            TraceOwned(hwnd, "switch.fullscreenAllowed", !fullscreen || allowFullscreen);
            if (!switchable) return TraceOwned(hwnd, "candidate.switchable", false);
            var placement = new WindowHistory.Placement { Length = Marshal.SizeOf(typeof(WindowHistory.Placement)) };
            if (!GetWindowPlacement(hwnd, ref placement)) return TraceOwned(hwnd, "placement.read", false);
            if (placement.NormalPosition.Right <= placement.NormalPosition.Left || placement.NormalPosition.Bottom <= placement.NormalPosition.Top)
                return TraceOwned(hwnd, "placement.positive", false);
            uint pid, after;
            if (Native.GetWindowThreadProcessId(hwnd, out pid) == 0) return TraceOwned(hwnd, "candidate.process", false);
            var title = new StringBuilder(4096);
            if (pid == ownProcess) {
                IntPtr length;
                if (SendMessageTimeout(hwnd, 0x000D, new IntPtr(title.Capacity), title, 0x0002 | 0x0020, 80, out length) == IntPtr.Zero)
                    return TraceOwned(hwnd, "title.owned80ms", false);
            } else if (GetWindowText(hwnd, title, title.Capacity) == 0) return TraceOwned(hwnd, "title.foreignRead", false);
            if (String.IsNullOrWhiteSpace(title.ToString())) return TraceOwned(hwnd, "title.nonempty", false);
            long started = Started(pid);
            var app = AppCatalog.ForWindow(hwnd);
            if (started == 0) return TraceOwned(hwnd, "identity.started", false);
            if (app == null) return TraceOwned(hwnd, "identity.app", false);
            if (String.IsNullOrWhiteSpace(app.Key)) return TraceOwned(hwnd, "identity.keyNonempty", false);
            if (Native.GetWindowThreadProcessId(hwnd, out after) == 0 || pid != after) return TraceOwned(hwnd, "final.process", false);
            if (!EligibleApp(hwnd, tracked)) return TraceOwned(hwnd, "final.eligible", false);
            if (Started(pid) != started) return TraceOwned(hwnd, "final.started", false);
            window = new ShelfWindow { Handle = hwnd, Process = pid, ProcessStarted = started, AppKey = app.Key,
                AppName = String.IsNullOrWhiteSpace(app.Name) ? "Application" : app.Name, Path = app.Path, Title = title.ToString() };
            return TraceOwned(hwnd, "candidate.selected", true);
        }
        internal static bool CanListOnTarget(IntPtr hwnd, Display target)
        {
            Native.MonitorInfo monitor, destination;
            return target != null && Native.IsWindow(hwnd) && Monitor(hwnd, out monitor) &&
                Native.TryMonitor(target.Device, out destination) && WindowShelfPolicy.CanListOnTarget(monitor.Device, destination.Device);
        }
        internal static bool TryReadSelection(ShelfWindow expected, Display target, List<AppRule> exclusions, out ShelfWindow current)
        { return TryReadSelection(expected, target, exclusions, null, out current); }
        internal static bool TryReadSelection(ShelfWindow expected, Display target, List<AppRule> exclusions, ShelfMinimizeRegistry minimized, out ShelfWindow current, bool allowFullscreen = false)
        {
            current = null;
            if (minimized != null) minimized.RemoveExcluded(exclusions);
            return expected != null && ((minimized != null && minimized.TryRead(expected, exclusions, out current)) ||
                TryReadWindow(expected.Handle, allowFullscreen, out current)) && WindowShelfPolicy.SameWindow(expected, current) &&
                !AppCatalog.Matches(exclusions ?? new List<AppRule>(), current.AppKey) &&
                (allowFullscreen || !IsFullscreenForMovement(current.Handle)) && CanListOnTarget(current.Handle, target);
        }
        internal static List<ShelfWindow> Read(Display target, List<AppRule> exclusions)
        { return Read(target, exclusions, delegate { return false; }); }
        internal static List<ShelfWindow> Read(Display target, List<AppRule> exclusions, Func<bool> cancelled)
        { return Read(target, exclusions, cancelled, null); }
        internal static List<ShelfWindow> Read(Display target, List<AppRule> exclusions, Func<bool> cancelled, ShelfMinimizeRegistry minimized,
            bool allowFullscreen = false, bool includeLifetimes = false)
        {
            var windows = new List<ShelfWindow>(); var elapsed = Stopwatch.StartNew(); bool stopped = false;
            var processStarts = new Dictionary<uint, long>();
            bool success = EnumWindows(delegate(IntPtr hwnd, IntPtr unused) {
                if (cancelled() || elapsed.ElapsedMilliseconds > 5000) { stopped = true; return false; }
                if (includeLifetimes) {
                    // Search's explicit new-window action must also exclude old
                    // hidden/cloaked/unreadable windows, not just eligible apps.
                    // These records carry no titles, paths or movement authority.
                    uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
                    long started = 0;
                    if (pid != 0 && !processStarts.TryGetValue(pid, out started)) {
                        started = Started(pid); processStarts[pid] = started;
                    }
                    windows.Add(new ShelfWindow { Handle = hwnd, Process = pid, ProcessStarted = started });
                }
                ShelfWindow window;
                if (CanListOnTarget(hwnd, target) && TryReadWindow(hwnd, allowFullscreen, out window) &&
                    !AppCatalog.Matches(exclusions ?? new List<AppRule>(), window.AppKey) && CanListOnTarget(hwnd, target)) windows.Add(window);
                return true;
            }, IntPtr.Zero);
            // Never present a silently truncated catalog; the next deliberate hover can retry.
            if (stopped || !success) throw new OperationCanceledException();
            if (minimized != null) {
                minimized.RemoveExcluded(exclusions);
                foreach (var expected in minimized.Snapshot()) {
                    if (cancelled() || elapsed.ElapsedMilliseconds > 5000) throw new OperationCanceledException();
                    ShelfWindow current;
                    if (minimized.TryRead(expected, exclusions, out current) && (allowFullscreen || !IsFullscreenForMovement(current.Handle)) && CanListOnTarget(current.Handle, target) &&
                        !windows.Any(w => w.Handle == current.Handle && !String.IsNullOrWhiteSpace(w.AppKey))) windows.Add(current);
                }
            }
            return windows;
        }
        internal static bool PreferCleanAppIcon(string path)
        {
            try {
                return !String.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathRooted(path) &&
                    !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                    String.Equals(System.IO.Path.GetFileName(path), "chrome.exe", StringComparison.OrdinalIgnoreCase);
            } catch (ArgumentException) { return false; }
        }
        internal static Bitmap ReadCleanAppIcon(string path, int pixels)
        {
            if (!PreferCleanAppIcon(path) || pixels < 1) return null;
            try {
                if (new System.IO.DriveInfo(System.IO.Path.GetPathRoot(path)).DriveType == System.IO.DriveType.Network) return null;
                using (var icon = Icon.ExtractAssociatedIcon(path)) {
                    if (icon == null) return null;
                    using (var bitmap = icon.ToBitmap()) return new Bitmap(bitmap, pixels, pixels);
                }
            } catch (ArgumentException) { return null; }
            catch (System.IO.IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (ExternalException) { return null; }
        }
        internal static Bitmap ReadIcon(ShelfWindow window, int pixels)
        {
            uint pid; IntPtr icon = IntPtr.Zero;
            if (Native.GetWindowThreadProcessId(window.Handle, out pid) == 0 || pid != window.Process || Started(pid) != window.ProcessStarted) return null;
            // Chrome's group represents all its windows, not one profile's badged icon.
            var clean = ReadCleanAppIcon(window.Path, pixels);
            if (clean != null) return clean;
            foreach (int size in new[] { 1, 2, 0 }) {
                if (SendMessageTimeout(window.Handle, 0x007F, new IntPtr(size), IntPtr.Zero, 0x0002 | 0x0020, 35, out icon) == IntPtr.Zero) break;
                if (icon != IntPtr.Zero) break;
            }
            if (icon == IntPtr.Zero) icon = IntPtr.Size == 8 ? GetClassLongPtr(window.Handle, -14) : new IntPtr(unchecked((int)GetClassLong(window.Handle, -14)));
            if (icon == IntPtr.Zero) return null;
            try {
                using (var borrowed = Icon.FromHandle(icon))
                using (var copy = (Icon)borrowed.Clone())
                using (var bitmap = copy.ToBitmap()) return new Bitmap(bitmap, pixels, pixels);
            } catch (ArgumentException) { return null; }
            catch (System.Runtime.InteropServices.ExternalException) { return null; }
        }
    }

    // A request owns its images until the UI takes them. Cancellation also disposes late results.
    internal sealed class ShelfRequest : IDisposable
    {
        internal sealed class Result : IDisposable
        {
            internal List<List<ShelfWindow>> Groups;
            internal readonly Dictionary<string, Bitmap> Icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            internal bool Failed;
            public void Dispose() { foreach (var icon in Icons.Values) icon.Dispose(); Icons.Clear(); }
        }
        readonly object gate = new object();
        internal readonly int Generation;
        internal volatile bool Cancelled;
        Result result;
        internal ShelfRequest(int generation) { Generation = generation; }
        internal void Complete(Result value) { lock (gate) { if (Cancelled) value.Dispose(); else { if (result != null) result.Dispose(); result = value; } } }
        internal Result Take() { lock (gate) { var value = result; result = null; return value; } }
        public void Dispose() { lock (gate) { Cancelled = true; if (result != null) result.Dispose(); result = null; } }
    }

    internal static class ShelfAppearance
    {
        internal static bool TransparencyValue(object value) { return value == null || (value is int && (int)value == 1); }
        internal static bool TransparencyEnabled()
        {
            try {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return TransparencyValue(key == null ? null : key.GetValue("EnableTransparency"));
            } catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.IO.IOException) { return false; }
        }
        internal static bool GhostAllowed(bool highContrast, bool transparency, bool opaque, bool failed)
        { return !highContrast && transparency && !opaque && !failed; }
    }

    internal sealed class ShelfMotion
    {
        internal const int RevealMilliseconds = 150, DismissMilliseconds = 100, RisePixels = 5;
        double from = 1, target = 1;
        long started;
        int duration;
        internal bool Active { get; private set; }
        internal bool Dismissing { get { return target == 0; } }
        internal static bool Allowed(bool clientAnimation, bool ghost, bool highContrast)
        { return clientAnimation && ghost && !highContrast; }
        internal void Reset(bool visible) { from = target = visible ? 1 : 0; Active = false; }
        internal double Value(long now)
        {
            if (!Active) return target;
            double progress = Math.Max(0, Math.Min(1, (now - started) / (double)duration));
            if (progress >= 1) Active = false;
            double remaining = 1 - progress;
            return from + (target - from) * (1 - remaining * remaining * remaining);
        }
        internal void Begin(bool visible, long now, bool animate)
        {
            double next = visible ? 1 : 0;
            if (target == next) return;
            from = Value(now); target = next; started = now;
            duration = (int)Math.Ceiling((visible ? RevealMilliseconds : DismissMilliseconds) * Math.Abs(target - from));
            Active = animate && duration > 0;
        }
    }

    internal static class ShelfPaint
    {
        [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
        {
            internal uint Size; internal int Width, Height; internal ushort Planes, Bits;
            internal uint Compression, ImageSize; internal int X, Y; internal uint Colors, Important;
        }
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { internal byte Operation, Flags, Alpha, Format; }
        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination,
            ref Size size, IntPtr source, ref Point origin, uint key, ref Blend blend, uint flags);
        internal static Color Surface(bool highContrast) { return highContrast ? SystemColors.Window : Ui.Surface; }
        internal static Color Ink(bool highContrast) { return highContrast ? SystemColors.WindowText : Ui.Text; }
        internal static Color Border(bool highContrast) { return highContrast ? SystemColors.WindowText : Ui.Dark ? Color.FromArgb(94, 101, 108) : Color.FromArgb(183, 190, 196); }
        internal static bool ValidSelection(IntPtr value) { return value != IntPtr.Zero && value != new IntPtr(-1); }
        internal static Rectangle BadgeBounds(Size size, int dpi, int count)
        {
            int width = WindowShelfPolicy.Scale(count < 10 ? 14 : count < 100 ? 20 : 26, dpi), height = WindowShelfPolicy.Scale(15, dpi);
            return new Rectangle(size.Width - WindowShelfPolicy.Scale(1, dpi) - width, size.Height - height, width, height);
        }
        internal static Bitmap Canvas(Size size, int dpi)
        {
            var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
            bitmap.SetResolution(dpi, dpi); return bitmap;
        }
        internal static void AnimateButton(Bitmap bitmap, double visibility, int rise)
        {
            visibility = Math.Max(0, Math.Min(1, visibility));
            if (visibility == 1) return;
            int offset = (int)Math.Round(rise * (1 - visibility)), width = bitmap.Width * 4;
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try {
                var pixels = new byte[width * bitmap.Height]; var row = new byte[width];
                for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * width, width);
                for (int y = 0; y < bitmap.Height; y++) {
                    Array.Clear(row, 0, row.Length);
                    for (int x = 0; x < width; x += 4) {
                        int source = (y - offset) * width + x;
                        if (y >= offset) for (int channel = 0; channel < 4; channel++) row[x + channel] = (byte)Math.Round(pixels[source + channel] * visibility);
                        // The visual fades and rises inside a stationary hit rectangle.
                        // Keep premultiplied alpha nonzero even in the very first frame.
                        row[x + 3] = Math.Max((byte)1, row[x + 3]);
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), width);
                }
            } finally { bitmap.UnlockBits(data); }
        }
        internal static GraphicsPath Round(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath(); float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
        }
        internal static void Text(Graphics graphics, string text, Font font, Rectangle bounds, Color color, bool center)
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = center ? StringAlignment.Center : StringAlignment.Near,
                LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                graphics.DrawString(text, font, brush, bounds, format);
        }
        static void Pool(Graphics graphics, RectangleF bounds, Color color)
        {
            using (var path = new GraphicsPath()) {
                path.AddEllipse(bounds);
                using (var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = new[] { Color.FromArgb(0, color) } }) graphics.FillPath(brush, path);
            }
        }
        internal static void Ground(Graphics graphics, Size size, int dpi, bool hover, bool down)
        {
            float scale = dpi / 96f, x = size.Width / 2f, y = size.Height - 5 * scale;
            Pool(graphics, new RectangleF(x - 11 * scale, y - scale, 22 * scale, 5 * scale), Color.FromArgb(hover ? 23 : 17, 0, 0, 0));
            if (hover || down) {
                Pool(graphics, new RectangleF(x - 13 * scale, y - 2 * scale, 26 * scale, 5 * scale), Color.FromArgb(down ? 30 : 21, 162, 226, 207));
                using (var faint = new Pen(Color.FromArgb(19, 182, 228, 212), .55f * scale)) graphics.DrawEllipse(faint, x - 15 * scale, y - 2.5f * scale, 30 * scale, 6 * scale);
                using (var ripple = new Pen(Color.FromArgb(down ? 116 : 94, 179, 233, 215), .7f * scale)) graphics.DrawEllipse(ripple, x - 11 * scale, y - 1.5f * scale, 22 * scale, 4 * scale);
            } else using (var crescent = new Pen(Color.FromArgb(83, 219, 227, 223), .6f * scale))
                graphics.DrawArc(crescent, x - 6 * scale, y - scale, 12 * scale, 2.5f * scale, 20, 140);
        }
        internal static bool WithPixels(Bitmap bitmap, Func<IntPtr, bool> draw)
        {
            // A top-down premultiplied DIB is shared by layered presentation and opaque fallback.
            var info = new BitmapInfo { Size = 40, Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, Bits = 32 };
            IntPtr bits, dc = CreateCompatibleDC(IntPtr.Zero), dib = CreateDIBSection(IntPtr.Zero, ref info, 0, out bits, IntPtr.Zero, 0);
            IntPtr previous = IntPtr.Zero;
            try {
                if (dc == IntPtr.Zero || dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
                var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try {
                    var row = new byte[bitmap.Width * 4];
                    for (int y = 0; y < bitmap.Height; y++) {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(bits, y * row.Length), row.Length);
                    }
                } finally { bitmap.UnlockBits(data); }
                previous = SelectObject(dc, dib);
                if (!ValidSelection(previous)) { previous = IntPtr.Zero; return false; }
                return draw(dc);
            } finally {
                if (previous != IntPtr.Zero && dc != IntPtr.Zero) SelectObject(dc, previous);
                if (dib != IntPtr.Zero) DeleteObject(dib); if (dc != IntPtr.Zero) DeleteDC(dc);
            }
        }
        internal static bool Present(Graphics graphics, Bitmap bitmap, Point location)
        {
            return WithPixels(bitmap, delegate(IntPtr source) {
                IntPtr target = graphics.GetHdc();
                try { return BitBlt(target, location.X, location.Y, bitmap.Width, bitmap.Height, source, 0, 0, 0x00CC0020); }
                finally { graphics.ReleaseHdc(target); }
            });
        }
        internal static bool PresentLayered(IntPtr window, Bitmap bitmap, Point location)
        {
            return WithPixels(bitmap, delegate(IntPtr source) {
                var origin = Point.Empty; var size = bitmap.Size; var blend = new Blend { Alpha = 255, Format = 1 };
                return UpdateLayeredWindow(window, IntPtr.Zero, ref location, ref size, source, ref origin, 0, ref blend, 2);
            });
        }
    }

    internal sealed class ShelfPopup : Form
    {
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
        readonly Font popupFont = new Font("Segoe UI", 9);
        readonly bool opaque;
        readonly ShelfMotion motion = new ShelfMotion();
        readonly Stopwatch motionClock = Stopwatch.StartNew();
        readonly Timer motionTimer = new Timer { Interval = 15 };
        Action dismissed;
        Func<bool> reentered;
        int frameGeneration;
        bool clientAnimation;
        bool failed, transparency = true, scheduled, rendering;
        internal bool UsesLayeredSurface { get { return !opaque && !failed; } }
        internal bool GhostEnabled { get { return ShelfAppearance.GhostAllowed(HighContrast, transparency, opaque, failed); } }
        internal string MaterialKind { get { return GhostEnabled ? "Ghost" : "Opaque"; } }
        internal bool HighContrast { get; private set; }
        internal bool AnimationEnabled { get { return ShelfMotion.Allowed(clientAnimation, GhostEnabled, HighContrast); } }
        internal bool AnimationRunning { get { return motionTimer.Enabled; } }
        internal bool IsDismissing { get { return motion.Dismissing; } }
        internal double RevealProgress { get { return motion.Value(motionClock.ElapsedMilliseconds); } }
        internal string EmptyText = "";
        internal int EmptyTextInsetRight;
        internal ShelfPopup(string name) : this(name, false) { }
        internal ShelfPopup(string name, bool opaque)
        {
            this.opaque = opaque;
            Text = name; AccessibleName = name; FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; BackColor = Ui.Surface; ForeColor = Ui.Text;
            Font = popupFont; DoubleBuffered = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.Selectable, false);
            motionTimer.Tick += delegate { AnimateFrame(); };
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyMaterial(); }
        protected override void OnHandleDestroyed(EventArgs e) { StopMotion(); base.OnHandleDestroyed(e); }
        void ApplyMaterial()
        {
            HighContrast = SystemInformation.HighContrast;
            transparency = ShelfAppearance.TransparencyEnabled();
            int enabled;
            clientAnimation = SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0;
            BackColor = ShelfPaint.Surface(HighContrast); ForeColor = ShelfPaint.Ink(HighContrast);
            foreach (var list in Controls.OfType<ListBox>()) { list.BackColor = BackColor; list.ForeColor = ForeColor; }
            if (!AnimationEnabled) {
                if (motion.Dismissing) { FinishDismiss(); return; }
                StopMotion();
            }
            Invalidate(true);
            ScheduleFrame();
        }
        void UseOpaqueFallback()
        {
            if (failed) return;
            bool leaving = motion.Dismissing; Action complete = dismissed;
            failed = true; UpdateStyles(); Invalidate(true);
            StopMotion();
            if (leaving) { Hide(); if (complete != null) complete(); }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; if (UsesLayeredSurface) p.ExStyle |= 0x80000; return p; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
            base.WndProc(ref m);
            if (IsHandleCreated && (m.Msg == 0x001A || m.Msg == 0x031A || m.Msg == 0x031E)) ApplyMaterial();
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); PaintLayer(); }
        void PrepareReveal()
        {
            if (!IsHandleCreated) CreateHandle();
            motion.Reset(false); motion.Begin(true, motionClock.ElapsedMilliseconds, AnimationEnabled);
            PaintLayer(AnimationEnabled ? 0 : 1);
        }
        protected override void SetVisibleCore(bool value)
        {
            // Prime the layered surface before WS_VISIBLE, avoiding an opaque first frame.
            if (value && !Visible) PrepareReveal();
            else if (!value) StopMotion();
            base.SetVisibleCore(value);
            if (value && Visible && motion.Active) motionTimer.Start();
        }
        internal void DismissAnimated(Action complete, Func<bool> returned)
        {
            if (IsDisposed || Disposing || dismissed != null) return;
            dismissed = complete; reentered = returned;
            motion.Begin(false, motionClock.ElapsedMilliseconds, AnimationEnabled && Visible);
            if (motion.Active) motionTimer.Start(); else FinishDismiss();
        }
        internal void CancelDismiss()
        {
            if (!motion.Dismissing || IsDisposed || Disposing) return;
            dismissed = null; reentered = null;
            motion.Begin(true, motionClock.ElapsedMilliseconds, AnimationEnabled && Visible);
            if (motion.Active) motionTimer.Start(); else motionTimer.Stop();
            ScheduleFrame();
        }
        void FinishDismiss()
        {
            Action complete = dismissed;
            StopMotion(); Hide();
            if (complete != null) complete();
        }
        void StopMotion()
        {
            motionTimer.Stop(); motion.Reset(true); dismissed = null; reentered = null;
            frameGeneration++; scheduled = false;
        }
        void AnimateFrame()
        {
            if (!Visible || IsDisposed || Disposing || !IsHandleCreated) { StopMotion(); return; }
            if (motion.Dismissing && reentered != null && reentered()) CancelDismiss();
            PaintLayer();
            if (IsDisposed || Disposing) return;
            if (!motion.Active) {
                motionTimer.Stop();
                if (motion.Dismissing) FinishDismiss();
            }
        }
        protected override void OnInvalidated(InvalidateEventArgs e) { base.OnInvalidated(e); ScheduleFrame(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ScheduleFrame(); }
        protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); ScheduleFrame(); }
        protected override void OnControlRemoved(ControlEventArgs e) { base.OnControlRemoved(e); ScheduleFrame(); }
        internal void ScheduleFrame()
        {
            if (!UsesLayeredSurface || !Visible || scheduled || rendering || !IsHandleCreated || IsDisposed || Disposing) return;
            scheduled = true; int expected = frameGeneration;
            BeginInvoke((Action)delegate {
                if (expected != frameGeneration) return;
                scheduled = false;
                if (!IsDisposed && !Disposing && IsHandleCreated && Visible) PaintLayer();
            });
        }
        void PaintLayer()
        { PaintLayer(RevealProgress); }
        void PaintLayer(double visibility)
        {
            if (!UsesLayeredSurface || rendering || !IsHandleCreated || ClientSize.Width < 1 || ClientSize.Height < 1) return;
            rendering = true;
            try { using (var bitmap = Render(GhostEnabled, HighContrast, visibility)) if (!ShelfPaint.PresentLayered(Handle, bitmap, Location)) UseOpaqueFallback(); }
            finally { rendering = false; }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        internal Bitmap Render(bool ghost, bool highContrast)
        { return Render(ghost, highContrast, 1); }
        internal Bitmap Render(bool ghost, bool highContrast, double visibility)
        {
            visibility = ghost && !highContrast ? Math.Max(0, Math.Min(1, visibility)) : 1;
            var bitmap = ShelfPaint.Canvas(ClientSize, DeviceDpi);
            using (var graphics = Graphics.FromImage(bitmap)) {
                graphics.Clear(ghost ? Color.Transparent : ShelfPaint.Surface(highContrast)); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (!ghost) using (var path = ShelfPaint.Round(new RectangleF(.5f, .5f, bitmap.Width - 1, bitmap.Height - 1), WindowShelfPolicy.Scale(4, DeviceDpi)))
                using (var pen = new Pen(ShelfPaint.Border(highContrast))) graphics.DrawPath(pen, path);
                var textBounds = ClientRectangle; textBounds.Width = Math.Max(0, textBounds.Width - EmptyTextInsetRight);
                textBounds.Offset(0, (int)Math.Round(WindowShelfPolicy.Scale(ShelfMotion.RisePixels, DeviceDpi) * (1 - visibility)));
                ShelfPaint.Text(graphics, EmptyText, Font, textBounds, Color.FromArgb((int)Math.Round(255 * visibility), ShelfPaint.Ink(highContrast)), true);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
                foreach (var button in Controls.OfType<ShelfButton>())
                    using (var icon = button.RenderCurrent(ghost, highContrast)) {
                        ShelfPaint.AnimateButton(icon, visibility, WindowShelfPolicy.Scale(ShelfMotion.RisePixels, (int)icon.HorizontalResolution));
                        graphics.DrawImage(icon, button.Bounds, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel);
                    }
            }
            return bitmap;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (UsesLayeredSurface) { ScheduleFrame(); return; }
            using (var bitmap = Render(false, HighContrast)) {
                if (ShelfPaint.Present(e.Graphics, bitmap, Point.Empty)) return;
            }
            using (var bitmap = Render(false, HighContrast)) e.Graphics.DrawImageUnscaled(bitmap, Point.Empty);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) StopMotion();
            base.Dispose(disposing);
            if (disposing) { motionTimer.Dispose(); motionClock.Stop(); popupFont.Dispose(); }
        }
    }

    internal sealed class ShelfButton : Button
    {
        Point origin;
        Size dragThreshold;
        bool dragged, hovered, pressed;
        internal int Count;
        readonly int dpi;
        internal ShelfButton(int dpi)
        {
            this.dpi = dpi;
            Size = new Size(WindowShelfPolicy.Scale(40, dpi), WindowShelfPolicy.Scale(40, dpi)); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Ui.Background; FlatAppearance.MouseDownBackColor = Ui.Line;
            BackColor = Ui.Surface; ForeColor = Ui.Text; TabStop = false; DoubleBuffered = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
            // MA_NOACTIVATE does not prevent Control.WmMouseDown from calling Focus.
            SetStyle(ControlStyles.UserMouse, true); SetStyle(ControlStyles.Selectable, false);
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            origin = e.Location; dragged = false; pressed = e.Button == MouseButtons.Left; Invalidate();
            dragThreshold = TaskbarInput.DirectDragThreshold(SystemInformation.DragSize, (uint)DeviceDpi);
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.None && WindowShelfPolicy.Dragged(origin, e.Location, dragThreshold)) dragged = true;
            base.OnMouseMove(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
            // Button dispatches Click before MouseUp; inspect release coordinates first.
            if (m.Msg == 0x0202 && WindowShelfPolicy.Dragged(origin,
                new Point(unchecked((short)m.LParam.ToInt64()), unchecked((short)(m.LParam.ToInt64() >> 16))), dragThreshold)) dragged = true;
            base.WndProc(ref m);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false; Invalidate();
            if (WindowShelfPolicy.Dragged(origin, e.Location, dragThreshold)) dragged = true;
            base.OnMouseUp(e);
        }
        protected override void OnClick(EventArgs e) { if (!dragged) base.OnClick(e); }
        protected override void OnInvalidated(InvalidateEventArgs e) { base.OnInvalidated(e); var popup = Parent as ShelfPopup; if (popup != null) popup.ScheduleFrame(); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        internal Bitmap RenderCurrent(bool ghost, bool highContrast) { return Render(ghost, highContrast, hovered, pressed); }
        internal Bitmap Render(bool ghost, bool highContrast, bool hover, bool down)
        {
            var bitmap = ShelfPaint.Canvas(ClientSize, dpi);
            using (var graphics = Graphics.FromImage(bitmap)) {
                // Layered HWNDs pass clicks through alpha-zero pixels. This imperceptible floor
                // keeps the entire existing button rectangle interactive, not just icon pixels.
                graphics.Clear(ghost ? Color.FromArgb(1, 0, 0, 0) : ShelfPaint.Surface(highContrast)); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (ghost) ShelfPaint.Ground(graphics, ClientSize, dpi, hover, down);
                else if (hover || down) using (var path = ShelfPaint.Round(new RectangleF(1, 1, Width - 2, Height - 2), WindowShelfPolicy.Scale(4, dpi)))
                using (var brush = new SolidBrush(highContrast ? SystemColors.Highlight : Color.FromArgb(down ? 48 : 25, Ui.Dark ? Color.White : Color.Black))) graphics.FillPath(brush, path);
                if (Image != null) {
                    var state = graphics.Save();
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    graphics.DrawImage(Image, new Rectangle((Width - Image.Width) / 2, (Height - Image.Height) / 2, Image.Width, Image.Height),
                        0, 0, Image.Width, Image.Height, GraphicsUnit.Pixel); graphics.Restore(state);
                }
                Color ink = highContrast && (hover || down) ? SystemColors.HighlightText : ShelfPaint.Ink(highContrast);
                if (!String.IsNullOrEmpty(Text)) ShelfPaint.Text(graphics, Text, Font, ClientRectangle, ink, true);
                if (Count > 1) {
                    var bounds = ShelfPaint.BadgeBounds(ClientSize, dpi, Count);
                    using (var path = ShelfPaint.Round(bounds, WindowShelfPolicy.Scale(4, dpi)))
                    using (var brush = new SolidBrush(ShelfPaint.Surface(highContrast))) graphics.FillPath(brush, path);
                    ShelfPaint.Text(graphics, Count > 99 ? "99+" : Count.ToString(), Font, bounds, ShelfPaint.Ink(highContrast), true);
                }
            }
            return bitmap;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var popup = Parent as ShelfPopup; bool highContrast = popup == null ? SystemInformation.HighContrast : popup.HighContrast;
            if (popup != null && popup.UsesLayeredSurface) return;
            using (var bitmap = Render(false, highContrast, hovered, pressed)) {
                if (ShelfPaint.Present(e.Graphics, bitmap, Point.Empty)) return;
            }
            using (var opaque = Render(false, highContrast, hovered, pressed)) e.Graphics.DrawImageUnscaled(opaque, Point.Empty);
        }
    }

    internal sealed class ShelfWindowList : ListBox
    {
        internal ShelfWindowList()
        {
            TabStop = false;
            SetStyle(ControlStyles.UserMouse, true); SetStyle(ControlStyles.Selectable, false);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
            base.WndProc(ref m);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            // Skip the native mouse-down focus request, retaining native item selection
            // and scrolling; the manager still validates same-row release and dragging.
            if (e.Button == MouseButtons.Left) SelectedIndex = IndexFromPoint(e.Location);
            base.OnMouseDown(e);
        }
    }

    internal sealed class ShelfChooserHover
    {
        internal const int LeaveMilliseconds = 250;
        long outside = -1;
        internal void Reset() { outside = -1; }
        internal bool ShouldDismiss(bool inside, bool captured, long now)
        {
            if (inside || captured) { Reset(); return false; }
            if (outside < 0 || now < outside) { outside = now; return false; }
            return now - outside >= LeaveMilliseconds;
        }
        internal static bool Contains(Point point, Rectangle icon, Rectangle previews, int margin)
        {
            var target = icon; target.Inflate(margin, margin);
            if (target.Contains(point) || previews.Contains(point)) return true;
            // Only the narrow path from this icon to its chooser bridges the gap.
            // Other shelf icons must not keep this app's previews open.
            return target.Width > 0 && previews.Bottom <= target.Top &&
                Rectangle.FromLTRB(target.Left, previews.Bottom, target.Right, target.Top + 1).Contains(point);
        }
    }

    internal static class KeyboardSearchDestination
    {
        internal static bool Eligible(Display display)
        { return display != null && display.Width >= 160 && display.Height >= 160 && !String.IsNullOrWhiteSpace(display.Device) && !String.IsNullOrWhiteSpace(display.Identity); }
        internal static bool Same(Display first, Display second)
        {
            return Eligible(first) && Eligible(second) && first.Primary == second.Primary &&
                String.Equals(first.Identity, second.Identity, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(first.Device, second.Device, StringComparison.OrdinalIgnoreCase) &&
                first.X == second.X && first.Y == second.Y && first.Width == second.Width && first.Height == second.Height;
        }
        internal static Display Copy(Display display)
        { return new Display { Device = display.Device, Identity = display.Identity, Name = display.Name, Primary = display.Primary,
            X = display.X, Y = display.Y, Width = display.Width, Height = display.Height, WorkHeight = display.WorkHeight }; }
        internal static bool NativeMatches(Display display, Rectangle expectedWork, int expectedDpi, Native.MonitorInfo? monitor, int actualDpi)
        {
            if (!Eligible(display) || !monitor.HasValue || expectedDpi != actualDpi) return false;
            var info = monitor.Value;
            var bounds = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
            var work = Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
            return String.Equals(info.Device, display.Device, StringComparison.OrdinalIgnoreCase) && bounds == WindowShelfPolicy.Bounds(display) &&
                ((info.Flags & 1) != 0) == display.Primary && work.Width > 0 && work.Height > 0 && (expectedWork.IsEmpty || work == expectedWork);
        }
    }

    internal sealed class WindowShelfManager : IDisposable
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        readonly Func<List<Display>> targets;
        readonly Func<List<AppRule>> exclusions;
        readonly Func<ShelfWindow, Display, bool> move;
        readonly ShelfMinimizeRegistry minimized;
        readonly Func<Settings> sensitivity;
        readonly Action cancelSearchMovement;
        readonly Func<List<Display>> manualSearchTargets;
        readonly Func<ShelfWindow, Display, bool> manualMove;
        readonly ShelfActivationDwell activation = new ShelfActivationDwell();
        readonly Timer timer = new Timer { Interval = WindowShelfPolicy.ActivationPollIntervalMs };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly ToolTip tips = new ToolTip { ShowAlways = true, InitialDelay = 450, ReshowDelay = 100 };
        readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        List<Display> enabled = new List<Display>();
        List<AppRule> rules = new List<AppRule>();
        List<List<ShelfWindow>> groups;
        Display target;
        ShelfPopup shelf;
        ShelfPreviewChooser chooser;
        List<ShelfWindow> chooserGroup;
        readonly ShelfGroupDwell groupDwell = new ShelfGroupDwell();
        readonly ShelfChooserHover chooserHover = new ShelfChooserHover();
        Rectangle chooserOrigin;
        readonly Dictionary<ShelfButton, List<ShelfWindow>> groupedButtons = new Dictionary<ShelfButton, List<ShelfWindow>>();
        ShelfRequest pending;
        Bitmap fallback;
        int generation, page, dpi = 96;
        long outside = -1;
        bool disposed, latched, activationPreview;
        bool fullscreenEnabled;
        GhostSearchPopup search;
        GhostSearchSession searchSession;
        GhostLaunchCoordinator searchLaunch;
        bool searchMoving, manualSearch;
        Rectangle searchWorkArea;
        string searchSettingsVersion, searchCatalogError;
        internal Func<Func<bool>, List<GhostApp>> SearchCatalogReader = GhostAppCatalog.Read;
        internal Func<GhostApp, GhostLaunchResult> SearchAppLauncher = GhostAppCatalog.Launch;
        internal Func<GhostApp, GhostLaunchResult> SearchNewWindowLauncher = GhostAppCatalog.LaunchNewWindow;
        internal Func<Display, List<AppRule>, Func<bool>, List<ShelfWindow>> SearchWindowReader = null;
        internal Func<Display, int> SearchDpiReader = ReadDpi;
        internal Func<Display, Native.MonitorInfo?> SearchMonitorReader = ReadSearchMonitor;
        internal Func<Display, string> SearchIdentityReader = ReadSearchIdentity;
        internal Action<GhostSearchPopup> SearchPresenter = delegate(GhostSearchPopup popup) { popup.Show(); };
        static Native.MonitorInfo? ReadSearchMonitor(Display display)
        { Native.MonitorInfo monitor; return Native.TryMonitor(display.Device, out monitor) ? (Native.MonitorInfo?)monitor : null; }
        static string ReadSearchIdentity(Display display)
        {
            if (display == null || String.IsNullOrWhiteSpace(display.Device)) return "";
            var device = new Native.DisplayDevice { Size = Marshal.SizeOf(typeof(Native.DisplayDevice)) };
            // Match Native.Displays identity discovery without its mutable cache
            // or friendly-name/WMI lookup on the background launch worker.
            return Native.EnumDisplayDevices(display.Device, 0, ref device, 1) ? device.Identity ?? "" : "";
        }

        internal void SetActivationPreview(bool visible)
        {
            if (disposed || activationPreview == visible) return;
            activationPreview = visible; Dismiss(); latched = false;
            if (visible) timer.Stop(); else Refresh();
        }

        internal WindowShelfManager(Func<List<Display>> targets, Func<List<AppRule>> exclusions, Func<ShelfWindow, Display, bool> move)
            : this(targets, exclusions, move, null) { }
        internal WindowShelfManager(Func<List<Display>> targets, Func<List<AppRule>> exclusions, Func<ShelfWindow, Display, bool> move, ShelfMinimizeRegistry minimized)
            : this(targets, exclusions, move, minimized, null) { }
        internal WindowShelfManager(Func<List<Display>> targets, Func<List<AppRule>> exclusions, Func<ShelfWindow, Display, bool> move,
            ShelfMinimizeRegistry minimized, Func<Settings> sensitivity)
            : this(targets, exclusions, move, minimized, sensitivity, null) { }
        internal WindowShelfManager(Func<List<Display>> targets, Func<List<AppRule>> exclusions, Func<ShelfWindow, Display, bool> move,
            ShelfMinimizeRegistry minimized, Func<Settings> sensitivity, Action cancelSearchMovement,
            Func<List<Display>> manualSearchTargets = null, Func<ShelfWindow, Display, bool> manualMove = null)
        {
            if (targets == null || exclusions == null || move == null) throw new ArgumentNullException();
            this.targets = targets; this.exclusions = exclusions; this.move = move;
            this.minimized = minimized;
            this.sensitivity = sensitivity;
            this.cancelSearchMovement = cancelSearchMovement;
            this.manualSearchTargets = manualSearchTargets; this.manualMove = manualMove;
            timer.Tick += delegate { try { Tick(); } catch (Exception) { Dismiss(); } };
        }
        internal void Refresh()
        {
            if (disposed) return;
            if (activationPreview) { timer.Stop(); return; }
            RefreshSensitivity();
            var next = (targets() ?? new List<Display>()).Where(d => d != null && !d.Primary && d.Width >= 160 && d.Height >= 160 &&
                !String.IsNullOrWhiteSpace(d.Identity)).Select(d => new Display { Device = d.Device, Identity = d.Identity,
                    X = d.X, Y = d.Y, Width = d.Width, Height = d.Height }).ToList();
            var nextRules = (exclusions() ?? new List<AppRule>()).Where(r => r != null).Select(r => new AppRule { Key = r.Key }).ToList();
            if (next.Count != enabled.Count || next.Any(d => !enabled.Any(e => WindowShelfPolicy.SameTarget(d, e))) ||
                !new HashSet<string>(rules.Select(r => r.Key), StringComparer.OrdinalIgnoreCase).SetEquals(nextRules.Select(r => r.Key))) Dismiss();
            enabled = next; rules = nextRules;
            if (minimized != null) minimized.RemoveExcluded(rules);
            if (search != null && !ValidSearchTarget(target)) Dismiss();
            // Refresh runs synchronously when settings are saved. Fence workers
            // here, before a queued shell launch can observe the old session.
            if (search != null && !String.Equals(searchSettingsVersion, SearchSettingsVersion(), StringComparison.Ordinal)) Dismiss();
            if (search != null) UpdateSearchContext();
            if (enabled.Count == 0 && search == null) { timer.Stop(); Dismiss(); } else timer.Start();
        }
        internal bool RefreshSensitivity()
        {
            if (disposed) return false;
            var settings = sensitivity == null ? null : sensitivity();
            bool include = settings != null && settings.AllowMovingFullscreenApps;
            bool changed = activation.Configure(settings == null ? WindowShelfPolicy.DefaultActivationWidth : settings.WindowShelfActivationWidth,
                settings == null ? WindowShelfPolicy.DefaultActivationHeight : settings.WindowShelfActivationHeight,
                settings == null ? WindowShelfPolicy.DefaultHoverDelayMs : settings.WindowShelfHoverDelayMs);
            changed |= include != fullscreenEnabled; fullscreenEnabled = include;
            if (!changed) return false;
            Dismiss(); latched = false; return true;
        }
        bool SensitivityMatches()
        {
            var settings = sensitivity == null ? null : sensitivity();
            return activation.Width == WindowShelfPolicy.ClampActivationWidth(settings == null ? WindowShelfPolicy.DefaultActivationWidth : settings.WindowShelfActivationWidth) &&
                activation.Height == WindowShelfPolicy.ClampActivationHeight(settings == null ? WindowShelfPolicy.DefaultActivationHeight : settings.WindowShelfActivationHeight) &&
                activation.DelayMs == WindowShelfPolicy.ClampHoverDelayMs(settings == null ? WindowShelfPolicy.DefaultHoverDelayMs : settings.WindowShelfHoverDelayMs) &&
                fullscreenEnabled == (settings != null && settings.AllowMovingFullscreenApps);
        }
        static bool QuietInput()
        {
            foreach (int key in new[] { 1, 2, 4, 5, 6, 16, 17, 18, 91, 92 }) if ((GetAsyncKeyState(key) & 0x8000) != 0) return false;
            return true;
        }
        internal static int ReadDpi(Display display)
        {
            uint x = 96, y;
            try { if (GetDpiForMonitor(MonitorFromPoint(new Point(display.X, display.Y), 2), 0, out x, out y) != 0) x = 96; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return Math.Max(96, Math.Min((int)x, Math.Min(display.Width, display.Height) * 96 / 160));
        }
        int Px(int pixels) { return WindowShelfPolicy.Scale(pixels, dpi); }
        int Capacity { get { return GhostBarSearchLayout.Capacity(target.Width, dpi); } }
        int Slot { get { return GhostBarSearchLayout.Slot(target.Width, dpi); } }
        bool ValidTarget(Display value)
        {
            Native.MonitorInfo monitor;
            return !activationPreview && SensitivityMatches() && value != null && ReadDpi(value) == dpi && (targets() ?? new List<Display>()).Any(d => WindowShelfPolicy.SameTarget(d, value)) &&
                Native.TryMonitor(value.Device, out monitor) && (monitor.Flags & 1) == 0 &&
                monitor.Monitor.Left == value.X && monitor.Monitor.Top == value.Y &&
                monitor.Monitor.Right == value.X + value.Width && monitor.Monitor.Bottom == value.Y + value.Height &&
                !WindowShelfCatalog.DestinationFullscreen(value);
        }
        List<Display> ManualTargets()
        { return (manualSearchTargets == null ? new List<Display>() : manualSearchTargets() ?? new List<Display>()).Where(KeyboardSearchDestination.Eligible).Select(KeyboardSearchDestination.Copy).ToList(); }
        bool ValidSearchTarget(Display value)
        {
            try {
                if (value == null || String.IsNullOrWhiteSpace(value.Identity) ||
                    !String.Equals(value.Identity, SearchIdentityReader(value), StringComparison.OrdinalIgnoreCase)) return false;
                if (!manualSearch) return ValidTarget(value);
                if (disposed || activationPreview || !SensitivityMatches() || !KeyboardSearchDestination.Eligible(value) || SearchDpiReader(value) != dpi ||
                    ManualTargets().Count(d => KeyboardSearchDestination.Same(d, value)) != 1) return false;
                return KeyboardSearchDestination.NativeMatches(value, search == null ? Rectangle.Empty : searchWorkArea, dpi, SearchMonitorReader(value), SearchDpiReader(value));
            } catch (Exception) { return false; }
        }
        bool Inside(Point point)
        {
            return target != null && (WindowShelfPolicy.Trigger(target, activation.Width, activation.Height).Contains(point) ||
                (shelf != null && shelf.Bounds.Contains(point)) || InsideChooser(point));
        }
        bool InsideChooser(Point point)
        { return chooser != null && ShelfChooserHover.Contains(point, chooserOrigin, chooser.LayoutInfo.Bounds, Px(4)); }
        void KeepChooserOpen(List<ShelfWindow> windows)
        {
            if (chooser == null || !Object.ReferenceEquals(chooserGroup, windows)) return;
            chooserHover.Reset(); chooser.CancelDismiss();
        }
        void Tick()
        {
            // The shared mover pumps messages while it verifies the move. Its
            // own cancellation checks remain active; another shelf tick must
            // not reject the newly arriving fullscreen window or restart search.
            if (searchMoving) {
                if ((GetAsyncKeyState(27) & 0x8001) != 0) { Dismiss(); latched = true; }
                return;
            }
            Refresh(); if (disposed || activationPreview || (enabled.Count == 0 && search == null)) return;
            Point point = Cursor.Position; long now = clock.ElapsedMilliseconds;
            if (target != null && !(search != null ? ValidSearchTarget(target) : ValidTarget(target))) { Dismiss(); return; }
            if (target != null && (GetAsyncKeyState(27) & 0x8001) != 0) {
                if (search != null) CloseSearchAnimated(); else Dismiss();
                latched = true; return;
            }
            if (search != null) { PollSearch(); return; }
            if (shelf != null) {
                if (Inside(point)) { outside = -1; shelf.CancelDismiss(); }
                else if (outside < 0) outside = now;
                else if (WindowShelfPolicy.HideReady(outside, now)) {
                    var leaving = shelf;
                    leaving.DismissAnimated(delegate { if (shelf == leaving) Dismiss(); }, delegate {
                        if (!Inside(Cursor.Position)) return false;
                        outside = -1; return true;
                    });
                    return;
                }
                CompleteRequest();
                if (shelf != null) CheckGroupHover(point, now);
                return;
            }
            var hovered = enabled.FirstOrDefault(d => WindowShelfPolicy.Trigger(d, activation.Width, activation.Height).Contains(point));
            if (hovered == null || !QuietInput()) { Dismiss(); latched = false; return; }
            if (latched) return;
            if (!WindowShelfPolicy.SameTarget(target, hovered)) { Dismiss(); target = hovered; dpi = ReadDpi(target); GetAsyncKeyState(27); }
            if (pending != null) { CompleteRequest(); return; }
            if (activation.Update(now, QuietInput(), true) && ValidTarget(target)) StartCatalog();
        }
        void StartCatalog()
        {
            if (RefreshSensitivity() || disposed || activationPreview || target == null) return;
            var request = new ShelfRequest(++generation); pending = request;
            var destination = target; var excluded = rules.ToList(); bool includeFullscreen = fullscreenEnabled; int capacity = Capacity, pixels = Px(24);
            Task.Run(delegate {
                var result = new ShelfRequest.Result();
                try {
                    result.Groups = WindowShelfPolicy.Group(WindowShelfCatalog.Read(destination, excluded, delegate { return request.Cancelled; }, minimized, includeFullscreen));
                    LoadIcons(result, result.Groups.Take(capacity).Select(g => g[0]), request, pixels);
                } catch (Exception) { result.Failed = true; }
                request.Complete(result);
            });
        }
        static void LoadIcons(ShelfRequest.Result result, IEnumerable<ShelfWindow> windows, ShelfRequest request, int pixels)
        {
            foreach (var window in windows) {
                if (request.Cancelled) break;
                var icon = WindowShelfCatalog.ReadIcon(window, pixels);
                if (icon != null) result.Icons.Add(window.AppKey, icon);
            }
        }
        void CompleteRequest()
        {
            if (RefreshSensitivity() || disposed || activationPreview) return;
            if (pending == null) return;
            var result = pending.Take(); if (result == null) return;
            int expected = pending.Generation; pending.Dispose(); pending = null;
            using (result) {
                if (!WindowShelfPolicy.AcceptResult(expected, generation, disposed, ValidTarget(target)) ||
                    (shelf == null && (!WindowShelfPolicy.Trigger(target, activation.Width, activation.Height).Contains(Cursor.Position) || !QuietInput()))) { Dismiss(); return; }
                foreach (var item in result.Icons) { Bitmap previous; if (icons.TryGetValue(item.Key, out previous)) previous.Dispose(); icons[item.Key] = item.Value; }
                result.Icons.Clear();
                if (shelf == null) {
                    groups = result.Groups ?? new List<List<ShelfWindow>>();
                    using (var application = SystemIcons.Application.ToBitmap()) fallback = new Bitmap(application, Px(24), Px(24));
                    shelf = new ShelfPopup("Ghost Bar");
                    BuildShelf(result.Failed ? "Windows unavailable" : "No available windows"); shelf.Show();
                } else UpdateIcons();
            }
        }
        ShelfButton Button(string name, Bitmap icon, EventHandler click)
        {
            var button = new ShelfButton(dpi) { AccessibleName = name, Image = icon };
            button.Click += click; return button;
        }
        void BuildShelf(string empty)
        {
            CloseChooser(); groupDwell.Reset(); groupedButtons.Clear();
            foreach (Control control in shelf.Controls.Cast<Control>().ToArray()) control.Dispose();
            int capacity = Capacity, count = Math.Min(capacity, groups.Count);
            bool paged = groups.Count > capacity; int slot = Slot;
            int width = groups.Count == 0 ? Math.Min(target.Width, Px(220) + slot) : GhostBarSearchLayout.Width(target.Width, dpi, count, paged);
            shelf.Bounds = WindowShelfPolicy.Fit(target, width, Px(56), target.Y + target.Height);
            shelf.EmptyText = groups.Count == 0 ? empty : ""; shelf.AccessibleDescription = shelf.EmptyText;
            shelf.EmptyTextInsetRight = groups.Count == 0 ? slot + Px(8) : 0;
            AddSearchButton(shelf.Width - slot - Px(8));
            if (groups.Count == 0) { shelf.Invalidate(); return; }
            int x = Px(8);
            if (paged) { AddPageButton(x, -1); x += slot; }
            foreach (var group in groups.Skip(page * capacity).Take(capacity)) {
                var captured = group;
                string name = group.Count == 1 ? group[0].Title : group[0].AppName + " (" + group.Count + ")";
                var button = Button(name, fallback, delegate { if (captured.Count == 1) Select(captured[0], ShelfSelectionOrigin.DirectIcon); else OpenChooser(captured); });
                button.Count = group.Count; button.Tag = group[0].AppKey; PlaceButton(button, x); shelf.Controls.Add(button); x += slot;
                if (captured.Count > 1) {
                    groupedButtons.Add(button, captured);
                    button.MouseEnter += delegate { KeepChooserOpen(captured); groupDwell.Update(captured, clock.ElapsedMilliseconds, QuietInput()); };
                    button.MouseLeave += delegate { groupDwell.Update(null, clock.ElapsedMilliseconds, true); };
                    button.MouseDown += delegate { groupDwell.Suppress(captured); };
                }
            }
            if (paged) AddPageButton(shelf.Width - slot * 2 - Px(8), 1);
            UpdateIcons();
        }
        void AddPageButton(int x, int direction)
        {
            var bitmap = Ui.LoadToolbarIcon("play", Px(18));
            if (bitmap != null && direction < 0) bitmap.RotateFlip(RotateFlipType.RotateNoneFlipX);
            var button = Button(direction < 0 ? "Previous apps" : "More apps", bitmap, delegate {
                if (RefreshSensitivity() || disposed || shelf == null || groups == null) return;
                int pages = (groups.Count + Capacity - 1) / Capacity;
                page = (page + direction + pages) % pages; BuildShelf(""); RequestPageIcons();
            });
            tips.SetToolTip(button, button.AccessibleName);
            if (bitmap == null) button.Text = direction < 0 ? "<" : ">";
            button.Disposed += delegate { if (bitmap != null) bitmap.Dispose(); };
            PlaceButton(button, x); shelf.Controls.Add(button);
        }
        void PlaceButton(ShelfButton button, int x)
        {
            int size = Math.Max(1, Math.Min(Px(40), Slot - Px(4)));
            button.Size = new Size(size, size); button.Location = new Point(x, Math.Max(0, (shelf.Height - size) / 2));
        }
        void AddSearchButton(int x)
        {
            var icon = GhostBarSearchLayout.Icon(dpi);
            var button = Button("Search apps", icon, delegate { OpenSearch(); });
            button.AccessibleDescription = "Search installed apps to open or bring to this display.";
            tips.SetToolTip(button, "Search apps on this display");
            button.Disposed += delegate { icon.Dispose(); };
            PlaceButton(button, x); shelf.Controls.Add(button);
        }
        string SearchSettingsVersion()
        {
            var settings = sensitivity == null ? null : sensitivity();
            return fullscreenEnabled + ":" + (settings == null || settings.ScaleWindowSizeToMonitor) + ":" + (settings == null ? "" :
                    settings.CloseWindowShortcut.ToString() + ":" + settings.SearchAppsShortcut + ":" + settings.UndoMoveShortcut + ":" +
                    String.Join(",", (settings.MonitorMoveShortcuts ?? new int[0]).Select(key => key.ToString()).ToArray()) + ":" +
                    String.Join(",", (settings.AdditionalMonitorMoveShortcuts ?? new Dictionary<string, int>())
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + "=" + entry.Value).ToArray()));
        }
        void UpdateSearchContext()
        {
            if (searchSession == null) return;
            searchSession.Update(manualSearch ? ManualTargets() : enabled, rules, generation.ToString() + ":" + manualSearch + ":" + SearchSettingsVersion());
        }
        internal bool IsSearchWindow(IntPtr window)
        { return search != null && search.OwnsWindow(window); }
        internal bool DismissSearch()
        {
            if (search == null) return false;
            CloseSearchAnimated(); return true;
        }
        internal bool ObservePointerDown(Point screenPoint)
        {
            var popup = search;
            if (disposed || popup == null || popup.IsDisposed) return false;
            if (popup.ContainsScreenPoint(screenPoint)) return false;
            // Observe the original mouse-down without consuming it. Desktop and
            // blank-monitor clicks can leave the foreground HWND unchanged, so
            // deactivation alone is not a reliable outside-click signal.
            CloseSearchAnimated(); return true;
        }
        internal bool OpenSearchOnDisplay(Display destination)
        {
            if (disposed || activationPreview || searchMoving || manualSearchTargets == null || !KeyboardSearchDestination.Eligible(destination)) return false;
            try {
                Refresh();
                var matches = ManualTargets().Where(d => KeyboardSearchDestination.Same(d, destination)).ToList();
                if (matches.Count != 1) return false;
                if (manualSearch && search != null && !search.IsDismissing && KeyboardSearchDestination.Same(target, destination) && ValidSearchTarget(target)) {
                    if (search.Visible) search.RequestInputFocus();
                    return true;
                }
                Dismiss(); manualSearch = true; target = KeyboardSearchDestination.Copy(matches[0]); dpi = SearchDpiReader(target);
                if (!ValidSearchTarget(target)) { Dismiss(); return false; }
                var monitor = SearchMonitorReader(target);
                if (!monitor.HasValue) { Dismiss(); return false; }
                var work = Rectangle.FromLTRB(monitor.Value.Work.Left, monitor.Value.Work.Top, monitor.Value.Work.Right, monitor.Value.Work.Bottom);
                StartSearch(target, work, work.Bottom - Px(8), Rectangle.Empty);
                return true;
            } catch (Exception) { Dismiss(); if (enabled.Count == 0) timer.Stop(); return false; }
        }
        void OpenSearch()
        {
            if (RefreshSensitivity() || disposed || activationPreview || search != null || shelf == null || !ValidTarget(target)) return;
            Native.MonitorInfo monitor;
            if (!Native.TryMonitor(target.Device, out monitor)) return;
            manualSearch = false;
            var button = shelf.Controls.OfType<ShelfButton>().FirstOrDefault(b => b.AccessibleName == "Search apps");
            var origin = button == null ? Rectangle.Empty : button.RectangleToScreen(button.ClientRectangle);
            StartSearch(target, Rectangle.FromLTRB(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right, monitor.Work.Bottom), shelf.Top - Px(6), origin);
        }
        void StartSearch(Display requestedDestination, Rectangle work, int popupBottom, Rectangle origin)
        {
            CloseChooser(); groupDwell.Reset();
            if (pending != null) { pending.Dispose(); pending = null; }
            var destination = KeyboardSearchDestination.Copy(requestedDestination);
            var requestedMove = manualSearch && manualMove != null ? manualMove : move;
            searchWorkArea = work; searchSettingsVersion = SearchSettingsVersion(); searchCatalogError = null;
            int capturedDpi = dpi;
            // These readers call native monitor APIs only in production. The STA
            // launcher must not read Controller state or access any UI control.
            var nativeMonitorReader = SearchMonitorReader; var nativeDpiReader = SearchDpiReader;
            var nativeIdentityReader = SearchIdentityReader; var launcher = SearchAppLauncher;
            var newWindowLauncher = SearchNewWindowLauncher;
            var session = new GhostSearchSession(); searchSession = session; UpdateSearchContext();
            Func<GhostApp, Func<GhostApp, GhostLaunchResult>, GhostLaunchResult> launchOnDestination = delegate(GhostApp app, Func<GhostApp, GhostLaunchResult> requestedLauncher) {
                try {
                    if (!session.Active || !String.Equals(destination.Identity, nativeIdentityReader(destination), StringComparison.OrdinalIgnoreCase) ||
                        !KeyboardSearchDestination.NativeMatches(destination, work, capturedDpi,
                        nativeMonitorReader(destination), nativeDpiReader(destination)))
                        return new GhostLaunchResult { Error = "The destination display changed. App opening was cancelled." };
                } catch (Exception) { return new GhostLaunchResult { Error = "Could not recheck the destination display. App opening was cancelled." }; }
                if (!session.Active) return new GhostLaunchResult { Error = "Search closed. App opening was cancelled." };
                return requestedLauncher == null ? new GhostLaunchResult { Error = "This app cannot open a new window from search." } : requestedLauncher(app);
            };
            searchLaunch = new GhostLaunchCoordinator(delegate { return session.Targets; }, delegate { return session.Rules; },
                delegate { return session.Active; }, delegate {
                    var reader = SearchWindowReader;
                    return reader != null ? reader(destination, session.Rules, delegate { return !session.Active; }) :
                        WindowShelfCatalog.Read(destination, session.Rules, delegate { return !session.Active; }, minimized, true, true);
                }, delegate(ShelfWindow window, Display display) {
                    if (!session.Active || disposed || !SensitivityMatches() || !ValidSearchTarget(display)) return false;
                    searchMoving = true;
                    try {
                        bool fullscreen = WindowShelfCatalog.IsFullscreenForMovement(window.Handle);
                        if (!requestedMove(window, display)) return false;
                        var observation = Stopwatch.StartNew();
                        do {
                            if (!session.Active || disposed || !SensitivityMatches()) return false;
                            bool arrived = GhostSearchPlacement.Observe(window, display, fullscreen);
                            if (arrived && (!fullscreen || observation.ElapsedMilliseconds >= 950)) return true;
                            Application.DoEvents(); System.Threading.Thread.Sleep(10);
                        } while (observation.ElapsedMilliseconds < 1000);
                        return session.Active && GhostSearchPlacement.Observe(window, display, fullscreen);
                    }
                    finally { searchMoving = false; }
                }, delegate(GhostApp app) {
                    return launchOnDestination(app, launcher);
                }, delegate { return session.Version; }, null, delegate(GhostApp app) {
                    return launchOnDestination(app, newWindowLauncher);
                });
            search = new GhostSearchPopup(destination, work, dpi, popupBottom, delegate(GhostApp app) {
                    if (searchLaunch == null || searchSession != session || !session.Active || !ValidSearchTarget(destination)) { Dismiss(); return; }
                    UpdateSearchContext(); searchLaunch.Begin(app, destination); UpdateSearchStatus();
                }, delegate { CloseSearchAnimated(); }, delegate(GhostApp app) {
                    if (searchLaunch == null || searchSession != session || !session.Active || !ValidSearchTarget(destination)) { Dismiss(); return; }
                    UpdateSearchContext(); searchLaunch.Begin(app, destination, true); UpdateSearchStatus();
                });
            if (!origin.IsEmpty) search.SetRevealOrigin(origin);
            if (shelf != null) shelf.Hide(); outside = -1; GetAsyncKeyState(27);
            UpdateSearchStatus(); session.Read(SearchCatalogReader); timer.Start(); SearchPresenter(search);
        }
        void UpdateSearchStatus()
        {
            if (search == null || searchLaunch == null || search.IsDismissing) return;
            string status = !String.IsNullOrWhiteSpace(searchLaunch.Status) ? searchLaunch.Status : GhostLaunchCoordinator.NativeLaunchPending ?
                "A previous app request is still opening. Wait for it to finish." : searchCatalogError;
            search.SetStatus(status, searchLaunch.Busy);
            if (searchLaunch.Succeeded) CloseSearchAnimated();
        }
        void PollSearch()
        {
            if (search == null || search.IsDismissing) return;
            UpdateSearchContext();
            List<GhostApp> apps; bool failed;
            if (searchSession.Take(out apps, out failed)) {
                search.SetCatalog(apps.Where(app => app != null && !AppCatalog.Matches(rules, app.AppKey)).ToList());
                searchCatalogError = failed ? "Could not load installed apps. Close search and try again." : null;
            }
            searchLaunch.Poll();
            UpdateSearchStatus();
        }
        void CloseSearchAnimated()
        {
            var closing = search;
            if (closing == null || closing.IsDismissing) return;
            // Cancellation is immediate even while the short dismiss animation runs.
            if (searchMoving && cancelSearchMovement != null) cancelSearchMovement();
            if (searchSession != null) searchSession.Dispose();
            if (searchLaunch != null) searchLaunch.Cancel();
            closing.BeginDismiss(delegate { if (search == closing) { Dismiss(); latched = true; } });
        }
        void UpdateIcons()
        {
            foreach (var button in shelf.Controls.OfType<ShelfButton>()) {
                Bitmap icon; var key = button.Tag as string;
                if (key != null) button.Image = icons.TryGetValue(key, out icon) ? icon : fallback;
            }
        }
        void RequestPageIcons()
        {
            if (RefreshSensitivity() || disposed || activationPreview || target == null || groups == null) return;
            if (pending != null) pending.Dispose();
            var request = new ShelfRequest(++generation); pending = request;
            int capacity = Capacity, pixels = Px(24);
            var windows = groups.Skip(page * capacity).Take(capacity).Select(g => g[0]).Where(w => !icons.ContainsKey(w.AppKey)).ToList();
            Task.Run(delegate {
                var result = new ShelfRequest.Result();
                try { LoadIcons(result, windows, request, pixels); } catch (Exception) { result.Failed = true; }
                request.Complete(result);
            });
        }
        void CheckGroupHover(Point point, long now)
        {
            if (chooser != null) {
                if (InsideChooser(point) || chooser.Capture) KeepChooserOpen(chooserGroup);
                if (chooserHover.ShouldDismiss(InsideChooser(point), chooser.Capture, now) && !chooser.IsDismissing) {
                    var leaving = chooser;
                    leaving.BeginDismiss(delegate { if (chooser == leaving) CloseChooser(); });
                }
            }
            List<ShelfWindow> hovered = null;
            foreach (var pair in groupedButtons) {
                if (!pair.Key.IsDisposed && pair.Key.RectangleToScreen(pair.Key.ClientRectangle).Contains(point)) { hovered = pair.Value; break; }
            }
            if (groupDwell.Update(hovered, now, QuietInput()) && !Object.ReferenceEquals(chooserGroup, hovered)) OpenChooser(hovered);
        }
        void OpenChooser(List<ShelfWindow> windows)
        {
            if (RefreshSensitivity() || disposed || activationPreview) return;
            if (!ValidTarget(target) || !QuietInput()) { Dismiss(); return; }
            if (windows == null || windows.Count == 0) return;
            if (Object.ReferenceEquals(chooserGroup, windows)) { KeepChooserOpen(windows); return; }
            CloseChooser();
            Bitmap icon; if (!icons.TryGetValue(windows[0].AppKey, out icon)) icon = fallback;
            var destination = target;
            chooser = new ShelfPreviewChooser(windows, destination, dpi, shelf.Top - Px(6), icon,
                delegate(ShelfWindow expected) {
                    ShelfWindow current;
                    return SensitivityMatches() && WindowShelfCatalog.TryReadSelection(expected, destination, exclusions(), minimized, out current, fullscreenEnabled);
                }, delegate(ShelfWindow selected) { Select(selected, ShelfSelectionOrigin.Chooser); }, new ShelfRecoveryThumbnailApi());
            chooser.PeekAnchor = shelf.Handle;
            var originButton = groupedButtons.FirstOrDefault(pair => Object.ReferenceEquals(pair.Value, windows)).Key;
            chooserOrigin = originButton != null && !originButton.IsDisposed ? originButton.RectangleToScreen(originButton.ClientRectangle) :
                new Rectangle(shelf.Left + (shelf.Width - Px(32)) / 2, shelf.Top + Px(7), Px(32), Px(32));
            chooser.SetRevealOrigin(chooserOrigin);
            chooserGroup = windows; chooserHover.Reset(); chooser.Show(); outside = -1;
        }
        void Select(ShelfWindow expected, ShelfSelectionOrigin origin)
        {
            if (RefreshSensitivity() || disposed || activationPreview) return;
            ShelfWindow current; Display destination = target;
            // Capture identity again immediately before delegating; the mover repeats this check.
            if (!QuietInput() || !ValidTarget(destination) ||
                !WindowShelfCatalog.TryReadSelection(expected, destination, exclusions(), minimized, out current, fullscreenEnabled)) {
                Dismiss(); latched = true; return;
            }
            // Only this click's foreground decides the local toggle; disposing the popup
            // must not turn an inactive selection into a minimize action.
            current.SelectionForeground = GetForegroundWindow(); current.SelectionTimestamp = Stopwatch.GetTimestamp();
            current.SelectionOrigin = origin;
            try { Dismiss(); latched = true; move(current, destination); }
            finally { current.SelectionForeground = IntPtr.Zero; current.SelectionTimestamp = 0; current.SelectionOrigin = ShelfSelectionOrigin.DirectIcon; }
        }
        void CloseChooser()
        {
            var closing = chooser; chooser = null; chooserGroup = null;
            chooserOrigin = Rectangle.Empty; chooserHover.Reset();
            if (closing != null) closing.Dispose();
        }
        void Dismiss()
        {
            generation++; if (pending != null) { pending.Dispose(); pending = null; }
            if (searchMoving && cancelSearchMovement != null) cancelSearchMovement();
            if (searchSession != null) { searchSession.Dispose(); searchSession = null; }
            if (searchLaunch != null) { searchLaunch.Dispose(); searchLaunch = null; }
            var closingSearch = search; search = null; if (closingSearch != null) closingSearch.Dispose();
            CloseChooser(); groupDwell.Reset(); groupedButtons.Clear(); if (shelf != null) { shelf.Dispose(); shelf = null; }
            tips.RemoveAll(); foreach (var icon in icons.Values) icon.Dispose(); icons.Clear();
            if (fallback != null) { fallback.Dispose(); fallback = null; }
            groups = null; target = null; page = 0; outside = -1; activation.Reset();
            manualSearch = false; searchWorkArea = Rectangle.Empty; searchSettingsVersion = null; searchCatalogError = null;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; timer.Stop(); timer.Dispose(); Dismiss(); tips.Dispose(); clock.Stop();
        }
    }
}
