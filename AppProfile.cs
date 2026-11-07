using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TaskbarSelect
{
    internal sealed class ProfileSelection
    {
        internal readonly string Directory;
        internal readonly bool IsTest;
        internal ProfileSelection(string directory, bool test) { Directory = directory; IsTest = test; }
    }

    // Profile isolation is opt-in at launch, never a restriction baked into a release.
    // Taskbar controllers still share one desktop lock: profiles cannot safely manage
    // the same Explorer taskbars concurrently. Only stop/show events are profile-specific.
    internal static class AppProfile
    {
        internal static readonly string ProductionDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarSelect");
        static ProfileSelection selection = new ProfileSelection(ProductionDirectory, false);
        internal static bool IsTest { get { return selection.IsTest; } }
        internal static bool AllowsStartup { get { return !IsTest; } }
        internal static string Directory { get { return selection.Directory; } }
        internal static string Title { get { return IsTest ? "Taskbar Select - isolated test" : "Taskbar Select"; } }
        internal static void Initialize(string[] args) { selection = Select(args); }
        internal static ProfileSelection Select(string[] args)
        {
            int index = Array.IndexOf(args, "--test-profile");
            if (index < 0) return new ProfileSelection(ProductionDirectory, false);
            if (args.Count(a => a == "--test-profile") != 1 || index + 1 >= args.Length ||
                String.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !LocalAbsolutePath(args[index + 1]))
                throw new ArgumentException("--test-profile requires one absolute local-drive, separate profile directory.");
            string requested = Path.GetFullPath(args[index + 1]);
            if (IsDriveRoot(requested))
                throw new ArgumentException("The test profile must not overlap the production profile or a drive root.");
            string directory = ResolveDirectory(requested);
            string production = ResolveDirectory(ProductionDirectory);
            if (IsDriveRoot(directory) ||
                Overlaps(directory, production))
                throw new ArgumentException("The test profile must not overlap the production profile or a drive root.");
            if (args.Any(a => a == "--startup" || a == "--self-test-startup" || a == "--uninstall" || a == "--prepare-upgrade"))
                throw new ArgumentException("An isolated test profile cannot change Windows Startup or run production uninstall.");
            return new ProfileSelection(directory, true);
        }
        static bool Overlaps(string a, string b)
        {
            return String.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        static bool LocalAbsolutePath(string path)
        {
            // Device/extended namespaces and UNC paths must not disguise ordinary
            // profile paths or introduce remote/share aliases into test IPC.
            return path != null && path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/') && Path.IsPathRooted(path);
        }
        static bool IsDriveRoot(string path)
        { return String.Equals(path.TrimEnd('\\', '/'), Path.GetPathRoot(path).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        [StructLayout(LayoutKind.Sequential)]
        struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint FinalDirectory(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        internal static string ResolveDirectory(string path)
        {
            if (!LocalAbsolutePath(path)) throw new ArgumentException("Profile resolution requires an absolute local-drive directory.");
            string existing = Path.GetFullPath(path);
            if (!IsDriveRoot(existing)) existing = existing.TrimEnd('\\', '/');
            var missing = new System.Collections.Generic.Stack<string>();
            while (true) {
                // Desired access 0 reads metadata only. Share all ordinary access;
                // follow reparse points to resolve junction/SUBST/8.3 aliases.
                using (var handle = OpenDirectory(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
                    if (!handle.IsInvalid) {
                        FileInformation information;
                        if (!GetFileInformationByHandle(handle, out information)) throw ResolutionError();
                        if ((information.Attributes & 0x10) == 0) throw new ArgumentException("The profile path or its existing ancestor is not a directory.");
                        var final = new StringBuilder(512);
                        uint length = FinalDirectory(handle, final, (uint)final.Capacity, 0);
                        if (length == 0) throw ResolutionError();
                        if (length >= final.Capacity) {
                            final = new StringBuilder(checked((int)length + 1));
                            length = FinalDirectory(handle, final, (uint)final.Capacity, 0);
                            if (length == 0 || length >= final.Capacity) throw ResolutionError();
                        }
                        string physical = final.ToString();
                        if (physical.StartsWith(@"\\?\", StringComparison.Ordinal)) physical = physical.Substring(4);
                        if (!LocalAbsolutePath(physical)) throw new ArgumentException("The resolved test profile must remain on a local drive.");
                        physical = Path.GetFullPath(physical);
                        if (!IsDriveRoot(physical)) physical = physical.TrimEnd('\\', '/');
                        foreach (string segment in missing) physical = Path.Combine(physical, segment);
                        return physical;
                    }
                    int error = Marshal.GetLastWin32Error();
                    // Only nonexistent paths may be appended to a resolved parent.
                    // Access-denied and all other metadata failures must fail closed.
                    if (error != 2 && error != 3) throw ResolutionError(error);
                }
                string parent = Path.GetDirectoryName(existing);
                if (String.IsNullOrEmpty(parent)) throw new ArgumentException("The profile has no accessible existing directory ancestor.");
                missing.Push(Path.GetFileName(existing)); existing = parent;
            }
        }
        static ArgumentException ResolutionError(int error = -1)
        {
            if (error < 0) error = Marshal.GetLastWin32Error();
            return new ArgumentException("Could not resolve profile directory metadata: " + new Win32Exception(error).Message);
        }
        internal static bool ResolvedOverlap(string a, string b)
        {
            return Overlaps(ResolveDirectory(a), ResolveDirectory(b));
        }
        internal static bool ProductionSafe(ProfileSelection profile)
        {
            return profile != null && !profile.IsTest && String.Equals(profile.Directory, ProductionDirectory, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool ProductionSafe()
        {
            // Check the live consumers too: correct selection metadata alone must
            // not conceal a settings/recovery path or Startup restriction regression.
            return ProductionSafe(selection) && !IsTest && AllowsStartup &&
                String.Equals(Directory, ProductionDirectory, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Program.ConfigPath, Path.Combine(ProductionDirectory, "settings.json"), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(WorkAreas.JournalPath, Path.Combine(ProductionDirectory, "workarea-recovery.json"), StringComparison.OrdinalIgnoreCase);
        }
        internal static string DesktopName(string sid) { return @"Local\TaskbarSelect." + sid; }
        internal static string EventName(string sid) { return EventName(sid, selection); }
        internal static string EventName(string sid, ProfileSelection profile)
        {
            if (!profile.IsTest) return DesktopName(sid);
            using (var hash = SHA256.Create()) {
                string suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(profile.Directory.ToUpperInvariant()))).Replace("-", "");
                return DesktopName(sid) + ".test." + suffix;
            }
        }
    }
}
