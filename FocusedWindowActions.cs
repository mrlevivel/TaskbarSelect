using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSelect
{
    internal sealed class FocusedWindowSnapshot
    {
        internal IntPtr Window, Root;
        internal uint Process, Thread;
        internal long ProcessStarted;
        internal int ExtendedStyle, Error;
        internal bool Visible, Shell;
        internal string ClassName, Device, ProcessImage;
        internal FocusedWindowSnapshot Copy() { return (FocusedWindowSnapshot)MemberwiseClone(); }
    }

    internal sealed class FocusedClosePostResult
    {
        internal bool Accepted;
        internal int Error;
    }

    internal sealed class FocusedCloseResult
    {
        internal bool Requested { get; private set; }
        internal int Error { get; private set; }
        internal string Status { get; private set; }
        internal FocusedCloseResult(bool requested, int error, string status)
        { Requested = requested; Error = error; Status = status; }
    }

    // Native boundaries are injectable. These actions neither activate a window
    // nor modify movement history, taskbars, app settings or process lifetime.
    internal sealed class FocusedWindowApi
    {
        internal Func<IntPtr> Foreground = FocusedWindowNative.Foreground;
        internal Func<IntPtr, FocusedWindowSnapshot> Read = FocusedWindowNative.Read;
        internal Func<IntPtr, FocusedClosePostResult> PostClose = FocusedWindowNative.PostClose;
    }

    internal sealed class FocusedWindowActions
    {
        readonly FocusedWindowApi api;
        readonly Func<IntPtr, bool> ignoreWindow;
        internal FocusedWindowActions(Func<IntPtr, bool> ignoreWindow = null)
            : this(new FocusedWindowApi(), ignoreWindow) { }
        internal FocusedWindowActions(FocusedWindowApi api, Func<IntPtr, bool> ignoreWindow = null)
        {
            if (api == null) throw new ArgumentNullException("api");
            this.api = api; this.ignoreWindow = ignoreWindow ?? delegate { return false; };
        }
        internal FocusedCloseResult RequestClose(IntPtr capturedForeground)
        {
            if (FocusedWindowPolicy.SpecialHandle(capturedForeground) || api.Foreground() != capturedForeground)
                return Refused("Focused window changed or is unavailable.", 0);
            var original = ReadCopy(capturedForeground);
            if (!FocusedWindowPolicy.Eligible(original, ignoreWindow) || original.Window != capturedForeground)
                return original != null && original.Error != 0 ?
                    Refused("Focused window could not be verified", original.Error) :
                    Refused("This window cannot receive a close request.", 0);
            // Do not substitute a new foreground or a reused HWND/process after
            // the user invoked the action. No message pumping occurs here.
            var current = ReadCopy(capturedForeground);
            if (!FocusedWindowPolicy.Eligible(current, ignoreWindow) || current.Window != capturedForeground ||
                !FocusedWindowPolicy.SameWindow(original, current) || api.Foreground() != capturedForeground)
                return current != null && current.Error != 0 ?
                    Refused("Focused window could not be verified", current.Error) :
                    Refused("Focused window changed; close request was not sent.", 0);
            var posted = api.PostClose(capturedForeground);
            if (posted == null || !posted.Accepted)
                return Refused("Windows refused the close request", posted == null ? 0 : posted.Error);
            // An accepted WM_CLOSE can prompt to save, hide a tray app, or be
            // ignored by the app. It is not evidence that a window has closed.
            return new FocusedCloseResult(true, 0, "Close requested. The app may ask to save or remain open.");
        }
        FocusedWindowSnapshot ReadCopy(IntPtr window)
        { var value = api.Read(window); return value == null ? null : value.Copy(); }
        static FocusedCloseResult Refused(string status, int error)
        {
            if (error != 0) status += " (Windows error " + error + ": " + new Win32Exception(error).Message + ").";
            else if (!status.EndsWith(".", StringComparison.Ordinal)) status += ".";
            return new FocusedCloseResult(false, error, status);
        }
    }

    internal sealed class LastFocusedMonitor
    {
        readonly FocusedWindowApi api;
        readonly Func<IntPtr, bool> ignoreWindow;
        string identity, device;
        FocusedWindowSnapshot observedForeground;
        bool preserveSearchRestore;
        internal LastFocusedMonitor(Func<IntPtr, bool> ignoreWindow = null)
            : this(new FocusedWindowApi(), ignoreWindow) { }
        internal LastFocusedMonitor(FocusedWindowApi api, Func<IntPtr, bool> ignoreWindow = null)
        {
            if (api == null) throw new ArgumentNullException("api");
            this.api = api; this.ignoreWindow = ignoreWindow ?? delegate { return false; };
        }
        internal void Observe(IntPtr current, IEnumerable<Display> displays)
        { Current(current, Connected(displays), false); }
        internal void ObserveForeground(IntPtr current, IEnumerable<Display> displays)
        { Current(current, Connected(displays), true); }
        internal void ObservePointerDown(Point point, IEnumerable<Display> displays)
        {
            var connected = Connected(displays);
            var matches = connected.Where(d => point.X >= d.X && point.Y >= d.Y &&
                (long)point.X < (long)d.X + d.Width && (long)point.Y < (long)d.Y + d.Height).ToArray();
            if (matches.Length != 1) return;
            var found = matches[0];
            if (String.IsNullOrWhiteSpace(found.Identity) ||
                connected.Count(d => String.Equals(d.Identity, found.Identity, StringComparison.OrdinalIgnoreCase)) != 1 ||
                connected.Count(d => String.Equals(d.Device, found.Device, StringComparison.OrdinalIgnoreCase)) != 1) return;
            // Mouse-down can happen on blank desktop without a foreground
            // change. Snapshot the current app so unchanged timer polls cannot
            // overwrite this user's more recent physical display selection.
            IntPtr foreground = api.Foreground();
            var current = ReadForeground(foreground);
            if (current != null) observedForeground = current;
            preserveSearchRestore = current == null && observedForeground != null &&
                !FocusedWindowPolicy.SpecialHandle(foreground) && ignoreWindow(foreground);
            identity = found.Identity; device = found.Device;
        }
        internal Display Resolve(IntPtr current, IEnumerable<Display> displays)
        {
            var connected = Connected(displays);
            var focused = Current(current, connected, false);
            if (focused != null) return focused;
            if (!String.IsNullOrEmpty(identity)) {
                var physical = connected.Where(d => String.Equals(d.Identity, identity, StringComparison.OrdinalIgnoreCase)).ToArray();
                // Duplicate physical identities are ambiguous; a device slot
                // cannot make them safe. Never follow a reused slot alone.
                if (physical.Length == 1) { device = physical[0].Device; return physical[0]; }
            }
            var primary = connected.Where(d => d.Primary).ToArray();
            return primary.Length == 1 ? primary[0] : null;
        }
        FocusedWindowSnapshot ReadForeground(IntPtr current)
        {
            if (FocusedWindowPolicy.SpecialHandle(current) || api.Foreground() != current) return null;
            var value = api.Read(current);
            if (!FocusedWindowPolicy.Eligible(value, ignoreWindow) || value.Window != current || api.Foreground() != current || String.IsNullOrEmpty(value.Device)) return null;
            return value.Copy();
        }
        Display Current(IntPtr current, List<Display> displays, bool foregroundEvent)
        {
            var value = ReadForeground(current);
            if (value == null) return null;
            var matches = displays.Where(d => String.Equals(d.Device, value.Device, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return null;
            var found = matches[0];
            bool changed = !FocusedWindowPolicy.SameWindow(observedForeground, value) ||
                !String.Equals(observedForeground.Device, value.Device, StringComparison.OrdinalIgnoreCase);
            bool restoringSearch = preserveSearchRestore && !changed;
            observedForeground = value;
            // Closing Search can restore the same app that had focus before an
            // outside desktop click. That restoration is not a newer user
            // display choice. Polling must not consume its native event guard.
            if (changed || foregroundEvent) preserveSearchRestore = false;
            if (restoringSearch || (!foregroundEvent && !changed)) return String.IsNullOrEmpty(identity) ? found : null;
            // Without physical identity, this current target remains usable,
            // but it must not erase the last safely remembered app display.
            if (!String.IsNullOrEmpty(found.Identity)) { identity = found.Identity; device = found.Device; }
            return found;
        }
        static List<Display> Connected(IEnumerable<Display> displays)
        {
            if (displays == null) return new List<Display>();
            return displays.Where(d => d != null && !String.IsNullOrEmpty(d.Device) && d.Width > 0 && d.Height > 0).ToList();
        }
        internal string LastIdentity { get { return identity; } }
        internal string LastDevice { get { return device; } }
    }

    internal static class FocusedWindowPolicy
    {
        internal static bool SpecialHandle(IntPtr window)
        { return window == IntPtr.Zero || window == new IntPtr(-1) || window == new IntPtr(0xFFFF); }
        internal static bool Eligible(FocusedWindowSnapshot window, Func<IntPtr, bool> ignored)
        {
            return window != null && !SpecialHandle(window.Window) && window.Root == window.Window && window.Visible &&
                !window.Shell && (window.ExtendedStyle & 0x08000000) == 0 && window.Process != 0 && window.Thread != 0 &&
                window.ProcessStarted > 0 && window.Error == 0 && !String.IsNullOrEmpty(window.ClassName) &&
                !new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame" }
                    .Contains(window.ClassName, StringComparer.OrdinalIgnoreCase) && !ignored(window.Window);
        }
        internal static bool SameWindow(FocusedWindowSnapshot a, FocusedWindowSnapshot b)
        {
            return a != null && b != null && a.Window == b.Window && a.Root == b.Root && a.Process == b.Process &&
                a.Thread == b.Thread && a.ProcessStarted == b.ProcessStarted &&
                String.Equals(a.ClassName, b.ClassName, StringComparison.Ordinal);
        }
    }

    internal static class FocusedWindowNative
    {
        [StructLayout(LayoutKind.Sequential)] struct FileTime { internal uint Low, High; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool PostMessage(IntPtr window, uint message, IntPtr parameter, IntPtr detail);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder image, ref int length);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        internal static IntPtr Foreground() { return GetForegroundWindow(); }
        internal static FocusedWindowSnapshot Read(IntPtr window)
        { return Read(window, false); }
        // Only the optional focus decoration needs the image path. Shared close,
        // navigation and monitor selection retain their existing metadata reads.
        internal static FocusedWindowSnapshot Read(IntPtr window, bool includeProcessImage)
        {
            if (FocusedWindowPolicy.SpecialHandle(window) || !Native.IsWindow(window)) return null;
            var result = new FocusedWindowSnapshot { Window = window, Root = GetAncestor(window, 2),
                Visible = Native.IsWindowVisible(window), Shell = window == Native.GetShellWindow() };
            result.Thread = Native.GetWindowThreadProcessId(window, out result.Process);
            result.ExtendedStyle = IntPtr.Size == 8 ? unchecked((int)GetWindowLongPtr64(window, -20).ToInt64()) : GetWindowLong32(window, -20);
            var name = new StringBuilder(256); Native.GetClassName(window, name, name.Capacity); result.ClassName = name.ToString();
            var process = OpenProcess(0x1000, false, result.Process);
            if (process == IntPtr.Zero) result.Error = Marshal.GetLastWin32Error();
            else try {
                FileTime creation, exit, kernel, user;
                if (GetProcessTimes(process, out creation, out exit, out kernel, out user))
                    result.ProcessStarted = unchecked((long)(((ulong)creation.High << 32) | creation.Low));
                else result.Error = Marshal.GetLastWin32Error();
                if (includeProcessImage && result.Error == 0) {
                    // Query the same retained process handle as the raw FILETIME;
                    // never combine a cached PID's image with another lifetime.
                    var image = new StringBuilder(32768); int length = image.Capacity;
                    if (!QueryFullProcessImageName(process, 0, image, ref length) || length <= 0 || length >= image.Capacity || image.Length != length) {
                        result.Error = Marshal.GetLastWin32Error(); if (result.Error == 0) result.Error = 13;
                    } else result.ProcessImage = image.ToString();
                }
            } finally { CloseHandle(process); }
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            if (Native.GetMonitorInfo(Native.MonitorFromWindow(window, 0), ref monitor)) result.Device = monitor.Device;
            uint latestProcess;
            if (!Native.IsWindow(window) || Native.GetWindowThreadProcessId(window, out latestProcess) != result.Thread || latestProcess != result.Process) return null;
            return result;
        }
        internal static FocusedClosePostResult PostClose(IntPtr window)
        {
            bool accepted = PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return new FocusedClosePostResult { Accepted = accepted, Error = accepted ? 0 : Marshal.GetLastWin32Error() };
        }
    }
}
