using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSelect
{
    public sealed class Display
    {
        public string Device { get; set; }
        public string Identity { get; set; }
        public string Name { get; set; }
        public bool Primary { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int WorkHeight { get; set; }
        public string Label { get { return Device.Replace(@"\\.\DISPLAY", "Display ") + " - " + Name; } }
    }

    public sealed class Bar
    {
        public long Handle { get; set; }
        public string Device { get; set; }
        public bool Primary { get; set; }
        public bool Visible { get; set; }
        public bool Managed { get; set; }
    }

    internal static class Native
    {
        internal const string Marker = "TaskbarSelect.Hidden.v1.9F89C156";
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MonitorInfo
        {
            public int Size;
            public Rect Monitor, Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Identity;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        }
        internal delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
        [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SystemParametersInfo(uint action, uint parameter, ref Rect rect, uint flags);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out Rect rect, int size);
        [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr RemoveProp(IntPtr window, string name);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        internal static List<Display> Displays()
        {
            var result = new List<Display>();
            Dictionary<string, string> monitorNames = MonitorNames.Read();
            var activeTargets = DisplayConfigMonitors.Read();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                string identity = "", friendlyName = "Monitor";
                DisplayConfigMonitors.Target target;
                if (activeTargets.TryGetValue(info.Device, out target)) {
                    var legacy = new DisplayDevice { Size = Marshal.SizeOf(typeof(DisplayDevice)) };
                    identity = EnumDisplayDevices(info.Device, 0, ref legacy, 1) ?
                        DisplayConfigMonitors.PreserveIdentitySpelling(target.Identity, legacy.Identity) : target.Identity;
                    if (!monitorNames.TryGetValue(MonitorNames.NormalizeWindowsIdentity(identity), out friendlyName))
                        friendlyName = String.IsNullOrWhiteSpace(target.Name) ? "Monitor" : target.Name;
                }
                result.Add(new Display {
                    Device = info.Device, Identity = identity,
                    Name = friendlyName, Primary = (info.Flags & 1) != 0,
                    X = info.Monitor.Left, Y = info.Monitor.Top,
                    Width = info.Monitor.Right - info.Monitor.Left,
                    Height = info.Monitor.Bottom - info.Monitor.Top,
                    WorkHeight = info.Work.Bottom - info.Work.Top
                });
                return true;
            }, IntPtr.Zero);
            return result.OrderBy(d => d.Device, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static bool TryMonitor(string device, out MonitorInfo result)
        {
            MonitorInfo found = new MonitorInfo();
            bool success = false;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data) {
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (GetMonitorInfo(monitor, ref info) && info.Device == device) {
                    found = info; success = true; return false;
                }
                return true;
            }, IntPtr.Zero);
            result = found;
            return success;
        }

        internal static List<Bar> Bars()
        {
            var result = new List<Bar>();
            uint shellPid;
            GetWindowThreadProcessId(GetShellWindow(), out shellPid);
            if (shellPid == 0) return result;
            foreach (string className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" }) {
                IntPtr window = IntPtr.Zero;
                while ((window = FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero) {
                    uint pid;
                    GetWindowThreadProcessId(window, out pid);
                    if (pid != shellPid) continue;
                    var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                    if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) continue;
                    result.Add(new Bar { Handle = window.ToInt64(), Device = info.Device,
                        Primary = className == "Shell_TrayWnd" || (info.Flags & 1) != 0,
                        Visible = IsWindowVisible(window), Managed = GetProp(window, Marker) == new IntPtr(1) });
                }
            }
            return result;
        }

        internal static bool IsSecondaryTaskbar(Bar bar)
        {
            if (bar.Primary) return false;
            var window = new IntPtr(bar.Handle);
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() != "Shell_SecondaryTrayWnd") return false;
            uint pid, shellPid;
            GetWindowThreadProcessId(window, out pid);
            GetWindowThreadProcessId(GetShellWindow(), out shellPid);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            return pid != 0 && pid == shellPid && GetMonitorInfo(MonitorFromWindow(window, 0), ref info) &&
                (info.Flags & 1) == 0 && info.Device == bar.Device;
        }

        internal static bool Hide(Bar bar)
        {
            if (bar.Primary || (!bar.Visible && !bar.Managed)) return false;
            IntPtr window = new IntPtr(bar.Handle);
            // A window property dies with its HWND, avoiding stale-handle crash recovery.
            if (!SetProp(window, Marker, new IntPtr(1))) return false;
            if (ShowWindowAsync(window, 0)) return true;
            if (!bar.Managed) RemoveProp(window, Marker);
            return false;
        }

        // Per-call boundaries keep recovery tests off Explorer without replacing
        // the production request/result and marker-ownership logic.
        internal sealed class TaskbarRestoreApi
        {
            internal Func<IntPtr, bool> Marked = delegate(IntPtr window) { return GetProp(window, Marker) == new IntPtr(1); };
            internal Func<IntPtr, bool> Show = delegate(IntPtr window) { return ShowWindowAsync(window, 8); };
            internal Func<IntPtr, bool> Release = delegate(IntPtr window) { return RemoveProp(window, Marker) != IntPtr.Zero; };
        }

        internal static bool Restore(Bar bar)
        { return Restore(bar, new TaskbarRestoreApi()); }

        internal static bool Restore(Bar bar, TaskbarRestoreApi api)
        {
            IntPtr window = new IntPtr(bar.Handle);
            if (!api.Marked(window)) return true;
            // A rejected request must retain its HWND marker for a later retry.
            if (!api.Show(window)) return false;
            return api.Release(window);
        }

        internal static void RestoreAll()
        { RestoreAll(Bars(), new TaskbarRestoreApi()); }

        internal static void RestoreAll(IEnumerable<Bar> bars, TaskbarRestoreApi api)
        {
            var failures = new List<Exception>();
            foreach (var bar in bars) {
                try {
                    if (!Restore(bar, api)) failures.Add(new InvalidOperationException("Windows rejected restoring taskbar " +
                        (String.IsNullOrWhiteSpace(bar.Device) ? "HWND " + bar.Handle.ToString("X") : bar.Device) + "; recovery ownership was retained for retry."));
                }
                catch (Exception error) { failures.Add(error); }
            }
            // Continue after individual failures so every remaining marked bar
            // gets a request. Recovery receives all failures and blocks cleanup.
            if (failures.Count != 0) throw new AggregateException("Native taskbar restoration did not finish.", failures);
        }
    }
}
