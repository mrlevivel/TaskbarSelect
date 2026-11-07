using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSelect
{
    public sealed class AppRule
    {
        public string Key { get; set; }
        public string Name { get; set; }
    }

    internal sealed class RunningApp
    {
        internal string Key, Name, Path;
    }

    internal static class AppCatalog
    {
        delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder path, ref uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr handle, ref uint size, StringBuilder id);

        internal static string IdentityKey(string appId, string path)
        {
            if (!String.IsNullOrWhiteSpace(appId)) return "app:" + appId;
            return String.IsNullOrWhiteSpace(path) ? "" : "exe:" + path;
        }

        internal static RunningApp ForWindow(IntPtr window)
        {
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) return null;
            try {
                uint size = 32768;
                var buffer = new StringBuilder((int)size);
                string path = QueryFullProcessImageName(handle, 0, buffer, ref size) != 0 ? buffer.ToString() : "";
                uint length = 0;
                GetApplicationUserModelId(handle, ref length, null);
                string appId = "";
                if (length > 0 && length < 32768) {
                    var id = new StringBuilder((int)length);
                    if (GetApplicationUserModelId(handle, ref length, id) == 0) appId = id.ToString();
                }
                string key = IdentityKey(appId, path);
                if (key.Length == 0) return null;
                return new RunningApp { Key = key, Name = Path.GetFileNameWithoutExtension(path), Path = path };
            } finally { CloseHandle(handle); }
        }

        internal static bool Matches(IEnumerable<AppRule> rules, string key)
        {
            return !String.IsNullOrEmpty(key) && rules.Any(rule => String.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        internal static List<RunningApp> Read()
        {
            var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
            EnumWindows(delegate(IntPtr window, IntPtr parameter) {
                if (!Native.IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
                int cloaked;
                if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var title = new StringBuilder(2);
                if (GetWindowText(window, title, title.Capacity) == 0) return true;
                var app = ForWindow(window);
                if (app == null || String.IsNullOrWhiteSpace(app.Name) || app.Name.Equals("TaskbarSelect", StringComparison.OrdinalIgnoreCase)) return true;
                if (!apps.ContainsKey(app.Key)) {
                    try {
                        string description = FileVersionInfo.GetVersionInfo(app.Path).FileDescription;
                        if (!String.IsNullOrWhiteSpace(description)) app.Name = description;
                    } catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    apps.Add(app.Key, app);
                }
                return true;
            }, IntPtr.Zero);
            return apps.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }
}
