using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace TaskbarSelect
{
    internal static class Branding
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr icon);

        internal static Icon LoadIcon(int size)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TaskbarSelect.Icon")) {
                if (stream == null) throw new InvalidDataException("The application icon is missing.");
                using (var icon = new Icon(stream, size, size)) return (Icon)icon.Clone();
            }
        }

        // The existing dark-backed mark has three source colors: its neutral
        // background/outline and the green check/stripe. Recover just that
        // accent's coverage, retaining the asset's antialiasing and alpha.
        internal static Bitmap AccentBitmap(Bitmap source, Color accent)
        {
            if (source == null) throw new ArgumentNullException("source");
            var result = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            const double ur = 242 - 36, ug = 245 - 43, ub = 243 - 42;
            const double vr = 147 - 36, vg = 239 - 43, vb = 191 - 42;
            const double uu = ur * ur + ug * ug + ub * ub, uv = ur * vr + ug * vg + ub * vb;
            const double denominator = uu * (vr * vr + vg * vg + vb * vb) - uv * uv;
            try {
                for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++) {
                    Color pixel = source.GetPixel(x, y);
                    if (pixel.A != 0 && pixel.G - pixel.R > 2 && pixel.G - pixel.B > 2) {
                        double r = pixel.R - 36, g = pixel.G - 43, b = pixel.B - 42;
                        double coverage = Math.Max(0, Math.Min(1, ((r * vr + g * vg + b * vb) * uu - (r * ur + g * ug + b * ub) * uv) / denominator));
                        pixel = Color.FromArgb(pixel.A, Channel(pixel.R + coverage * (accent.R - 147)),
                            Channel(pixel.G + coverage * (accent.G - 239)), Channel(pixel.B + coverage * (accent.B - 191)));
                    }
                    result.SetPixel(x, y, pixel);
                }
                return result;
            } catch { result.Dispose(); throw; }
        }
        static int Channel(double value) { return Math.Max(0, Math.Min(255, (int)Math.Round(value))); }

        // The icon itself has a dark background in both application themes,
        // so use the palette's bright ink rather than its light-surface ink.
        internal static Color AccentIconColor(string preset)
        {
            Color color = System.Windows.Forms.SystemInformation.HighContrast ? SystemColors.Highlight : FocusAppearance.Accent(preset, true);
            // Cache a color snapshot: a KnownColor otherwise resolves to the
            // new system color later and can conceal a required icon refresh.
            return Color.FromArgb(color.ToArgb());
        }

        internal static Icon LoadAccentIcon(int size, string preset)
        {
            if (size < 1 || size > 256) throw new ArgumentOutOfRangeException("size");
            using (var original = LoadIcon(size))
            using (var source = original.ToBitmap())
            using (var image = AccentBitmap(source, AccentIconColor(preset))) {
                IntPtr native = image.GetHicon();
                if (native == IntPtr.Zero) throw new InvalidDataException("The accent icon could not be created.");
                try { using (var borrowed = Icon.FromHandle(native)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(native); }
            }
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int pathLength, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemList);
        void SetIDList(IntPtr itemList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int length, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    internal static class Startup
    {
        internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string ValueName = "TaskbarSelect";
        internal const string ShortcutName = "Taskbar Select.lnk";
        const string ApprovalPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        internal sealed class State
        {
            internal bool Enabled, WindowsBlocked, CanChange;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

        internal static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName); }
        }
        internal static string CommandFor(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable) || executable.IndexOf('"') >= 0)
                throw new ArgumentException("Startup requires an absolute executable path.");
            return "\"" + executable + "\" --startup";
        }
        internal static bool IsOwnedTarget(string executable, string arguments)
        {
            return !String.IsNullOrWhiteSpace(executable) && Path.IsPathRooted(executable) &&
                String.Equals(Path.GetFileName(executable), "TaskbarSelect.exe", StringComparison.OrdinalIgnoreCase) &&
                String.Equals((arguments ?? "").Trim(), "--startup", StringComparison.Ordinal);
        }
        internal static bool IsOwned(object value)
        {
            string command = value as string;
            if (String.IsNullOrWhiteSpace(command)) return false;
            int count;
            IntPtr args = CommandLineToArgvW(command, out count);
            if (args == IntPtr.Zero) return false;
            try {
                if (count != 2) return false;
                return IsOwnedTarget(Marshal.PtrToStringUni(Marshal.ReadIntPtr(args)),
                    Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, IntPtr.Size)));
            } catch (ArgumentException) { return false; }
            finally { LocalFree(args); }
        }
        static object ReadLegacy()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                return key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        static object ReadShortcut()
        {
            if (!File.Exists(ShortcutPath)) return null;
            IShellLinkW link = null;
            try {
                link = (IShellLinkW)new ShellLink();
                ((IPersistFile)link).Load(ShortcutPath, 0);
                var executable = new StringBuilder(1024);
                var arguments = new StringBuilder(1024);
                link.GetPath(executable, executable.Capacity, IntPtr.Zero, 4);
                link.GetArguments(arguments, arguments.Capacity);
                return CommandForTarget(executable.ToString(), arguments.ToString());
            } catch (COMException) { return ""; }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
            finally { if (link != null) Marshal.FinalReleaseComObject(link); }
        }
        static string CommandForTarget(string executable, string arguments)
        {
            if (String.IsNullOrWhiteSpace(executable)) return "";
            return "\"" + executable + "\" " + (arguments ?? "").Trim();
        }
        static void CreateShortcut(string executable)
        {
            CommandFor(executable);
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath));
            string temporary = Path.Combine(Path.GetDirectoryName(ShortcutPath), "Taskbar Select." + Guid.NewGuid().ToString("N") + ".lnk");
            try {
                var link = (IShellLinkW)new ShellLink();
                try {
                    link.SetPath(executable);
                    link.SetArguments("--startup");
                    link.SetWorkingDirectory(Path.GetDirectoryName(executable));
                    link.SetDescription("Start Taskbar Select quietly with Windows");
                    link.SetIconLocation(executable, 0);
                    link.SetShowCmd(1);
                    ((IPersistFile)link).Save(temporary, true);
                } finally { Marshal.FinalReleaseComObject(link); }
                if (File.Exists(ShortcutPath)) File.Replace(temporary, ShortcutPath, null);
                else File.Move(temporary, ShortcutPath);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        static void RemoveOwnedLegacy()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, true)) {
                if (key == null) return;
                object current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (IsOwned(current)) key.DeleteValue(ValueName, false);
            }
        }
        internal static object Read()
        {
            object shortcut = ReadShortcut();
            return shortcut ?? ReadLegacy();
        }
        static object ReadApproval(string category, string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(ApprovalPath + category))
                return key == null ? null : key.GetValue(name, null);
        }
        internal static bool ApprovalAllows(object value)
        {
            if (value == null) return true;
            // StartupApproved is a Windows implementation detail, not a write API.
            // Recognize known enabled states; leave disabled/unknown states to Windows.
            var bytes = value as byte[];
            return bytes != null && bytes.Length >= 12 &&
                (BitConverter.ToUInt32(bytes, 0) == 2 || BitConverter.ToUInt32(bytes, 0) == 6);
        }
        internal static State Describe(object shortcut, object legacy, object folderApproval, object runApproval)
        {
            bool owned = IsOwned(shortcut), legacyOwned = IsOwned(legacy);
            bool allowed = shortcut != null ? ApprovalAllows(folderApproval) : legacyOwned ? ApprovalAllows(runApproval) : ApprovalAllows(folderApproval);
            return new State {
                Enabled = (owned || (shortcut == null && legacyOwned)) && allowed,
                WindowsBlocked = (shortcut == null || owned) && !allowed,
                CanChange = (shortcut == null || owned) && (legacy == null || legacyOwned)
            };
        }
        internal static State ReadState()
        {
            if (!AppProfile.AllowsStartup) return new State();
            return Describe(ReadShortcut(), ReadLegacy(), ReadApproval("StartupFolder", ShortcutName), ReadApproval("Run", ValueName));
        }
        internal static bool Enabled { get { return ReadState().Enabled; } }
        internal static bool CanMigrate(object legacy, object shortcut, object runApproval, object folderApproval)
        {
            return IsOwned(legacy) && (shortcut == null || IsOwned(shortcut)) && ApprovalAllows(runApproval) && ApprovalAllows(folderApproval);
        }
        internal static void MigrateLegacy()
        {
            if (!AppProfile.AllowsStartup) return;
            object legacy = ReadLegacy();
            object shortcut = ReadShortcut();
            if (!CanMigrate(legacy, shortcut, ReadApproval("Run", ValueName), ReadApproval("StartupFolder", ShortcutName))) return;
            CreateShortcut(System.Windows.Forms.Application.ExecutablePath);
            RemoveOwnedLegacy();
        }
        internal static void SetEnabled(bool enabled)
        {
            if (!AppProfile.AllowsStartup) throw new InvalidOperationException("Windows Startup is unavailable in an explicitly isolated test profile.");
            object shortcut = ReadShortcut();
            if (enabled) {
                if (shortcut != null && !IsOwned(shortcut))
                    throw new InvalidOperationException("Another startup shortcut already uses the name Taskbar Select. It was left unchanged.");
                if (shortcut == null && IsOwned(ReadLegacy()) && !ApprovalAllows(ReadApproval("Run", ValueName)))
                    throw new InvalidOperationException("Taskbar Select is disabled in Windows Startup apps. Enable it there first. Your Windows preference was left unchanged.");
                CreateShortcut(System.Windows.Forms.Application.ExecutablePath);
                if (!ApprovalAllows(ReadApproval("StartupFolder", ShortcutName)))
                    throw new InvalidOperationException("Taskbar Select is disabled in Windows Startup apps. Enable it there to start at sign-in. Your Windows preference was left unchanged.");
                RemoveOwnedLegacy();
            } else {
                if (IsOwned(shortcut)) File.Delete(ShortcutPath);
                RemoveOwnedLegacy();
            }
        }
    }
}
