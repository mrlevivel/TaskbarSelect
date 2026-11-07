using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace TaskbarSelect
{
    // Catalog entries contain only installed-app launch metadata. Query text is
    // never a path, an argument, a URL, or a command passed to a launcher.
    internal sealed class GhostApp
    {
        internal string Id, Name, AppKey, LaunchPath, PackageId;
        internal string TargetPath, Arguments, WorkingDirectory, ShortcutAppId;
        internal bool CatalogShortcut;
        internal GhostApp Copy() { return (GhostApp)MemberwiseClone(); }
        public override string ToString() { return Name ?? ""; }
    }

    internal sealed class GhostLaunchResult
    {
        internal bool Started;
        internal uint ProcessId;
        internal string Error;
    }

    internal static class GhostAppSearch
    {
        static string Normalize(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            var result = new StringBuilder(); bool space = true;
            foreach (char character in value.Trim()) {
                if (Char.IsLetterOrDigit(character)) { result.Append(Char.ToUpperInvariant(character)); space = false; }
                else if (!space) { result.Append(' '); space = true; }
            }
            return result.ToString().Trim();
        }

        static int Rank(string name, string query, string[] words)
        {
            if (query.Length == 0) return 0;
            if (name == query) return 0;
            if (name.StartsWith(query, StringComparison.Ordinal)) return 10;
            int score = 20;
            foreach (string word in words) {
                int at = name.IndexOf(word, StringComparison.Ordinal);
                if (at < 0) return Int32.MaxValue;
                score += at == 0 || name[at - 1] == ' ' ? 0 : 10;
            }
            return score;
        }

        internal static List<GhostApp> Find(IEnumerable<GhostApp> apps, string query, int limit)
        {
            if (apps == null || limit <= 0) return new List<GhostApp>();
            // Both work and results are bounded even for pasted text.
            if (query != null && query.Length > 256) return new List<GhostApp>();
            string normalized = Normalize(query);
            string[] words = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return apps.Where(a => a != null && !String.IsNullOrWhiteSpace(a.Id) && !String.IsNullOrWhiteSpace(a.Name))
                .Select(a => new { App = a, Score = Rank(Normalize(a.Name), normalized, words) })
                .Where(a => a.Score < Int32.MaxValue)
                .OrderBy(a => a.Score).ThenBy(a => a.App.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.App.Id, StringComparer.OrdinalIgnoreCase)
                .GroupBy(a => a.App.Id, StringComparer.OrdinalIgnoreCase).Select(a => a.First().App)
                .Take(Math.Min(limit, 50)).ToList();
        }
    }

    internal static class GhostAppCatalog
    {
        const int MaximumEntries = 4096, MaximumDirectories = 1024, MaximumDepth = 8;
        static readonly Guid ShellLinkClass = new Guid("00021401-0000-0000-C000-000000000046");
        static readonly Guid ActivationClass = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        static readonly Guid AppIdProperty = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        static readonly HashSet<string> CommandHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec",
            "explorer", "hh", "control", "regedit", "schtasks", "conhost", "wt", "python", "pythonw", "node",
            "java", "javaw", "dotnet", "bash", "wsl", "sh"
        };

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLink
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int command);
            void SetShowCmd(int command);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }
        [StructLayout(LayoutKind.Sequential)] struct PropertyKey { internal Guid Format; internal uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropertyVariant
        { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal IntPtr Pointer; }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropertyVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyVariant value);
            [PreserveSig] int Commit();
        }
        [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationActivationManager
        {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
            [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr items,
                [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
            [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr items, out uint processId);
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ShellExecuteInfo
        {
            internal int Size; internal uint Mask; internal IntPtr Window;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Verb, File, Parameters, Directory;
            internal int Show; internal IntPtr Instance, IdList;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Class;
            internal IntPtr ClassKey; internal uint Hotkey; internal IntPtr Icon, Process;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
        [DllImport("kernel32.dll")] static extern uint GetProcessId(IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropertyVariant value);

        static bool Cancelled(Func<bool> cancelled)
        {
            try { return cancelled != null && cancelled(); }
            catch (Exception) { return true; }
        }
        static void Release(object value)
        { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        static string FullPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0) return "";
            try {
                string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
                if (expanded.Length < 3 || !Char.IsLetter(expanded[0]) || expanded[1] != ':' ||
                    (expanded[2] != '\\' && expanded[2] != '/') || expanded.StartsWith("\\\\", StringComparison.Ordinal)) return "";
                return Path.GetFullPath(expanded);
            } catch (ArgumentException) { return ""; }
            catch (NotSupportedException) { return ""; }
            catch (PathTooLongException) { return ""; }
        }
        static bool NameValid(string value)
        { return !String.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(Char.IsControl); }
        static bool Helper(string name)
        {
            string value = name.Trim().ToLowerInvariant();
            return value.StartsWith("unins", StringComparison.Ordinal) || value.StartsWith("uninstall", StringComparison.Ordinal) ||
                value.StartsWith("remove ", StringComparison.Ordinal) || value == "remove" || value == "setup" ||
                value.StartsWith("install ", StringComparison.Ordinal) || value == "installer" || value == "help" ||
                value.EndsWith(" help", StringComparison.Ordinal) || value == "readme";
        }
        internal static bool ValidPackageId(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 200) return false;
            int bang = value.IndexOf('!'), underscore = value.LastIndexOf('_', bang < 0 ? 0 : bang);
            if (bang <= 0 || bang != value.LastIndexOf('!') || underscore < 3 || bang - underscore != 14 || bang == value.Length - 1) return false;
            for (int index = 0; index < value.Length; index++) {
                char character = value[index];
                if (!(character >= 'A' && character <= 'Z') && !(character >= 'a' && character <= 'z') &&
                    !(character >= '0' && character <= '9') && character != '.' && character != '-' && character != '_' && character != '!') return false;
            }
            return true;
        }
        static bool AppArguments(string target, string arguments)
        {
            if (String.IsNullOrWhiteSpace(arguments)) return true;
            if (arguments.Length > 32760 || arguments.Any(Char.IsControl)) return false;
            string value = arguments.Trim();
            string lower = value.ToLowerInvariant();
            if (lower.Contains("uninstall") || lower.Contains("/remove") || lower.Contains("--remove")) return false;
            if (lower == "/c" || lower.StartsWith("/c ", StringComparison.Ordinal) || lower == "/k" ||
                lower.StartsWith("/k ", StringComparison.Ordinal) || lower.StartsWith("-command", StringComparison.Ordinal) ||
                lower.StartsWith("-encodedcommand", StringComparison.Ordinal) || lower.StartsWith("--command", StringComparison.Ordinal)) return false;
            // Shortcut parameters may select an app profile or a launcher child.
            // A positional document, URL, script or command is not an app entry.
            if (!(value.StartsWith("-", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal))) return false;
            string[] documentTypes = { ".pdf", ".txt", ".doc", ".rtf", ".xls", ".ppt", ".html", ".htm", ".chm", ".url", ".ps1", ".bat", ".cmd", ".vbs", ".js" };
            if (documentTypes.Any(extension => lower.Contains(extension)) || lower.Contains("://") || lower.Contains("shell:")) return false;
            // Launcher-style shortcuts are retained, but never given authority
            // over all windows sharing their generic parent executable.
            return !CommandHosts.Contains(Path.GetFileNameWithoutExtension(target));
        }

        static bool ChromeLaunchAttribution(string target, string arguments, string appId)
        {
            if (!String.Equals(Path.GetFileName(target), "chrome.exe", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(appId, "Chrome", StringComparison.Ordinal)) return false;
            // Chromium's shortcut-location switch records launch metrics; it
            // does not select a profile or web app. Allow only this sole switch
            // and retain the exact explicit Chrome window identity below.
            switch (arguments.Trim()) {
                case "--source-shortcut-location=desktop":
                case "--source-shortcut-location=start-menu":
                case "--source-shortcut-location=taskbar": return true;
                default: return false;
            }
        }
        internal static GhostApp FromShortcut(string name, string shortcut, string target, string arguments,
            string workingDirectory, string appId, Func<string, bool> exists)
        {
            string path = FullPath(target), link = FullPath(shortcut);
            arguments = arguments ?? ""; appId = appId ?? "";
            if (!NameValid(name) || Helper(name) || path.Length == 0 || link.Length == 0 ||
                !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetExtension(link).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                Helper(Path.GetFileNameWithoutExtension(path)) || CommandHosts.Contains(Path.GetFileNameWithoutExtension(path)) ||
                !AppArguments(path, arguments) || exists == null || !exists(path)) return null;
            bool direct = (String.IsNullOrWhiteSpace(arguments) || ChromeLaunchAttribution(path, arguments, appId)) &&
                !Path.GetFileNameWithoutExtension(path).Equals("Update", StringComparison.OrdinalIgnoreCase);
            string identity = direct ? (String.IsNullOrWhiteSpace(appId) ? "exe:" + path : "app:" + appId) : "";
            return new GhostApp {
                Id = "shortcut:" + path.ToUpperInvariant() + "\n" + arguments,
                Name = name.Trim(), AppKey = identity, LaunchPath = link, PackageId = "",
                TargetPath = path, Arguments = arguments, WorkingDirectory = workingDirectory ?? "", ShortcutAppId = appId,
                CatalogShortcut = true
            };
        }

        static GhostApp ReadShortcut(string filename)
        {
            object instance = null;
            try {
                if (String.IsNullOrWhiteSpace(filename) || FullPath(filename).Length == 0) return null;
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass));
                ((IPersistFile)instance).Load(filename, 0);
                var link = (IShellLink)instance;
                var target = new StringBuilder(32768); var arguments = new StringBuilder(32768); var directory = new StringBuilder(32768);
                // Read only: never Resolve(), Save(), search for moved targets,
                // or invoke an installer advertised by a shortcut.
                link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
                link.GetArguments(arguments, arguments.Capacity);
                link.GetWorkingDirectory(directory, directory.Capacity);
                string appId = "";
                var store = instance as IPropertyStore;
                if (store != null) {
                    var key = new PropertyKey { Format = AppIdProperty, Id = 5 }; var value = new PropertyVariant();
                    try {
                        if (store.GetValue(ref key, out value) >= 0 && value.Type == 31 && value.Pointer != IntPtr.Zero)
                            appId = Marshal.PtrToStringUni(value.Pointer) ?? "";
                    } finally { PropVariantClear(ref value); }
                }
                return FromShortcut(Path.GetFileNameWithoutExtension(filename), filename, target.ToString(), arguments.ToString(), directory.ToString(), appId, File.Exists);
            } catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidCastException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            finally { Release(instance); }
        }

        internal static List<GhostApp> ReadRoots(IEnumerable<string> roots, Func<bool> cancelled)
        {
            var apps = new List<GhostApp>();
            if (roots == null || Cancelled(cancelled)) return apps;
            var directories = new Queue<Tuple<string, int>>(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots) { string path = FullPath(root); if (path.Length > 0) directories.Enqueue(Tuple.Create(path, 0)); }
            int filesSeen = 0;
            while (directories.Count > 0 && visited.Count < MaximumDirectories && filesSeen < MaximumEntries) {
                if (Cancelled(cancelled)) return new List<GhostApp>();
                var directory = directories.Dequeue();
                if (!visited.Add(directory.Item1)) continue;
                try {
                    if ((File.GetAttributes(directory.Item1) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (string filename in Directory.EnumerateFiles(directory.Item1, "*.lnk", SearchOption.TopDirectoryOnly)) {
                        if (Cancelled(cancelled)) return new List<GhostApp>();
                        if (++filesSeen > MaximumEntries) break;
                        if ((File.GetAttributes(filename) & FileAttributes.ReparsePoint) != 0) continue;
                        GhostApp app = ReadShortcut(filename); if (app != null) apps.Add(app);
                    }
                    if (directory.Item2 < MaximumDepth) {
                        foreach (string child in Directory.EnumerateDirectories(directory.Item1)) {
                            if (Cancelled(cancelled)) return new List<GhostApp>();
                            if (directories.Count + visited.Count >= MaximumDirectories) break;
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                                directories.Enqueue(Tuple.Create(child, directory.Item2 + 1));
                        }
                    }
                } catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (ArgumentException) { }
                catch (System.Security.SecurityException) { }
            }
            return Deduplicate(apps);
        }

        static object Invoke(object value, string name, BindingFlags flags, params object[] arguments)
        { return value.GetType().InvokeMember(name, flags, null, value, arguments); }
        static string Property(object value, string name)
        { return Convert.ToString(Invoke(value, "ExtendedProperty", BindingFlags.InvokeMethod, name)); }
        static void ReadPackaged(List<GhostApp> apps, Func<bool> cancelled)
        {
            object shell = null, folder = null, items = null;
            try {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                folder = Invoke(shell, "NameSpace", BindingFlags.InvokeMethod, "shell:AppsFolder");
                if (folder == null) return;
                items = Invoke(folder, "Items", BindingFlags.InvokeMethod);
                int count = Math.Min(MaximumEntries, Convert.ToInt32(Invoke(items, "Count", BindingFlags.GetProperty)));
                for (int index = 0; index < count && !Cancelled(cancelled); index++) {
                    object item = null;
                    try {
                        item = Invoke(items, "Item", BindingFlags.InvokeMethod, index);
                        string appId = Property(item, "System.AppUserModel.ID");
                        string name = Convert.ToString(Invoke(item, "Name", BindingFlags.GetProperty));
                        if (!ValidPackageId(appId) || !NameValid(name) || Helper(name)) continue;
                        apps.Add(new GhostApp { Id = "package:" + appId, Name = name.Trim(), AppKey = "app:" + appId, PackageId = appId, LaunchPath = "" });
                    } catch (TargetInvocationException) { }
                    catch (COMException) { }
                    catch (ArgumentException) { }
                    finally { Release(item); }
                }
            } catch (TargetInvocationException) { }
            catch (COMException) { }
            catch (ArgumentException) { }
            finally { Release(items); Release(folder); Release(shell); }
        }
        internal static List<GhostApp> Deduplicate(IEnumerable<GhostApp> apps)
        {
            return (apps ?? new GhostApp[0]).Where(app => app != null && NameValid(app.Name) && !String.IsNullOrWhiteSpace(app.Id))
                .GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        internal static List<GhostApp> Read(Func<bool> cancelled)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Installed-app discovery requires its own STA worker.");
            var apps = ReadRoots(new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) }, cancelled);
            if (Cancelled(cancelled)) return new List<GhostApp>();
            ReadPackaged(apps, cancelled);
            return Cancelled(cancelled) ? new List<GhostApp>() : Deduplicate(apps);
        }

        internal static bool SameShortcut(GhostApp expected, GhostApp current)
        {
            return expected != null && current != null && expected.CatalogShortcut && current.CatalogShortcut &&
                String.Equals(expected.Id, current.Id, StringComparison.Ordinal) &&
                String.Equals(expected.LaunchPath, current.LaunchPath, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(expected.TargetPath, current.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(expected.Arguments, current.Arguments, StringComparison.Ordinal) &&
                String.Equals(expected.WorkingDirectory, current.WorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(expected.ShortcutAppId, current.ShortcutAppId, StringComparison.Ordinal) &&
                String.Equals(expected.AppKey, current.AppKey, StringComparison.Ordinal) &&
                String.Equals(expected.PackageId, current.PackageId, StringComparison.Ordinal);
        }

        internal static bool CanMatchUnidentifiedExecutable(GhostApp app)
        {
            if (app == null || !app.CatalogShortcut || !String.IsNullOrEmpty(app.PackageId) ||
                !String.IsNullOrEmpty(app.Arguments) || String.IsNullOrWhiteSpace(app.ShortcutAppId) ||
                app.ShortcutAppId.Length > 200 || app.ShortcutAppId.Any(Char.IsControl) ||
                !String.Equals(app.AppKey, "app:" + app.ShortcutAppId, StringComparison.Ordinal)) return false;
            // A plain installed shortcut already has exact executable authority
            // when it omits an app ID. Its optional grouping ID must not erase
            // that authority for a window with a verifiably absent HWND ID.
            // Reconstruct the catalog shape: no profile arguments, generic
            // command host, update broker, package entry or fabricated ID.
            var canonical = FromShortcut(app.Name, app.LaunchPath, app.TargetPath, app.Arguments,
                app.WorkingDirectory, app.ShortcutAppId, delegate { return true; });
            return SameShortcut(app, canonical);
        }

        internal sealed class NewWindowLaunchPlan
        {
            internal readonly string File, Parameters, Directory, Verb;
            internal NewWindowLaunchPlan(string file, string parameters, string directory)
            { File = file; Parameters = parameters; Directory = directory; Verb = "open"; }
        }

        internal static bool CanOpenNewWindow(GhostApp app)
        {
            string target = app == null ? "" : FullPath(app.TargetPath);
            if (app == null || !app.CatalogShortcut || !String.IsNullOrEmpty(app.PackageId) ||
                !String.Equals(app.AppKey, "app:Chrome", StringComparison.Ordinal) ||
                !String.Equals(app.ShortcutAppId, "Chrome", StringComparison.Ordinal) ||
                !String.Equals(Path.GetFileName(target), "chrome.exe", StringComparison.OrdinalIgnoreCase) ||
                !(String.IsNullOrWhiteSpace(app.Arguments) || ChromeLaunchAttribution(app.TargetPath, app.Arguments, app.ShortcutAppId))) return false;
            // Reconstruct only the already supported catalog format. This pure
            // capability check reads no shortcut, executable or browser state.
            var canonical = FromShortcut(app.Name, app.LaunchPath, app.TargetPath, app.Arguments,
                app.WorkingDirectory, app.ShortcutAppId, delegate { return true; });
            return SameShortcut(app, canonical);
        }

        internal static bool TryNewWindowPlan(GhostApp expected, GhostApp reread, out NewWindowLaunchPlan plan)
        {
            plan = null;
            if (!CanOpenNewWindow(expected) || !CanOpenNewWindow(reread) || !SameShortcut(expected, reread)) return false;
            string attribution = String.IsNullOrWhiteSpace(expected.Arguments) ? "" : " " + expected.Arguments.Trim();
            plan = new NewWindowLaunchPlan(expected.TargetPath, "--new-window" + attribution, expected.WorkingDirectory);
            return true;
        }

        internal static GhostLaunchResult LaunchNewWindow(GhostApp app)
        {
            // Separate from ordinary Launch: no existing entry, shortcut
            // arguments, or original launch behavior is modified by this action.
            var expected = app == null ? null : app.Copy();
            if (!CanOpenNewWindow(expected)) return new GhostLaunchResult { Error = "Open new window is unavailable for this app entry." };
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                return new GhostLaunchResult { Error = "The app launcher is unavailable. Try again." };
            try {
                using (var shortcut = new FileStream(expected.LaunchPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    NewWindowLaunchPlan plan;
                    if (!TryNewWindowPlan(expected, ReadShortcut(expected.LaunchPath), out plan))
                        return new GhostLaunchResult { Error = "This app shortcut changed or is unavailable. Refresh search and try again." };
                    // ShellExecuteInfo.lpParameters is an application-parameter
                    // contract. Target the verified exe rather than appending to
                    // a .lnk, retaining its working directory and attribution.
                    // Chromium kOpenInNewWindow is the fixed --new-window switch.
                    var info = new ShellExecuteInfo { Size = Marshal.SizeOf(typeof(ShellExecuteInfo)), Mask = 0x40 | 0x100 | 0x400,
                        Verb = plan.Verb, File = plan.File, Parameters = plan.Parameters, Directory = plan.Directory, Show = 1 };
                    if (!ShellExecuteEx(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try {
                        uint processId = info.Process == IntPtr.Zero ? 0 : GetProcessId(info.Process);
                        return new GhostLaunchResult { Started = true, ProcessId = processId, Error = "" };
                    } finally { if (info.Process != IntPtr.Zero) CloseHandle(info.Process); }
                }
            } catch (COMException error) {
                return new GhostLaunchResult { Error = "Windows could not open this app (0x" + error.ErrorCode.ToString("X8") + ")." };
            } catch (Win32Exception error) {
                return new GhostLaunchResult { Error = error.NativeErrorCode == 1223 ? "Opening this app was cancelled." :
                    "Windows could not open this app (error " + error.NativeErrorCode + ")." };
            } catch (ArgumentException) {
                return new GhostLaunchResult { Error = "This app entry is no longer available. Refresh search and try again." };
            } catch (UnauthorizedAccessException) {
                return new GhostLaunchResult { Error = "Windows did not allow this app to open." };
            } catch (IOException) {
                return new GhostLaunchResult { Error = "This app shortcut changed or is unavailable. Refresh search and try again." };
            }
        }
        internal static GhostLaunchResult Launch(GhostApp app)
        {
            if (app == null) return new GhostLaunchResult { Error = "Choose an installed app first." };
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                return new GhostLaunchResult { Error = "The app launcher is unavailable. Try again." };
            object activation = null;
            try {
                if (!String.IsNullOrWhiteSpace(app.PackageId)) {
                    if (!ValidPackageId(app.PackageId) || app.Id != "package:" + app.PackageId || app.AppKey != "app:" + app.PackageId)
                        return new GhostLaunchResult { Error = "This app entry is no longer available. Refresh search and try again." };
                    activation = Activator.CreateInstance(Type.GetTypeFromCLSID(ActivationClass)); uint processId;
                    int result = ((IApplicationActivationManager)activation).ActivateApplication(app.PackageId, null, 2, out processId);
                    if (result < 0) Marshal.ThrowExceptionForHR(result);
                    return new GhostLaunchResult { Started = true, ProcessId = processId, Error = "" };
                }
                if (!app.CatalogShortcut || String.IsNullOrWhiteSpace(app.LaunchPath) || FullPath(app.LaunchPath).Length == 0)
                    return new GhostLaunchResult { Error = "This app shortcut changed or is unavailable. Refresh search and try again." };
                // Hold the selected shortcut against edits/replacement while
                // validating and passing it to Shell, which only needs read access.
                using (var shortcut = new FileStream(app.LaunchPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    if (!SameShortcut(app, ReadShortcut(app.LaunchPath)))
                        return new GhostLaunchResult { Error = "This app shortcut changed or is unavailable. Refresh search and try again." };
                    var info = new ShellExecuteInfo { Size = Marshal.SizeOf(typeof(ShellExecuteInfo)), Mask = 0x40 | 0x100 | 0x400,
                        Verb = "open", File = app.LaunchPath, Show = 1 };
                    // Preserve the installed shortcut's arguments, working directory,
                    // compatibility and broker behavior. Suppress Shell error UI;
                    // report any failure in the search panel instead.
                    if (!ShellExecuteEx(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try {
                        uint processId = info.Process == IntPtr.Zero ? 0 : GetProcessId(info.Process);
                        return new GhostLaunchResult { Started = true, ProcessId = processId, Error = "" };
                    } finally { if (info.Process != IntPtr.Zero) CloseHandle(info.Process); }
                }
            } catch (COMException error) {
                return new GhostLaunchResult { Error = "Windows could not open this app (0x" + error.ErrorCode.ToString("X8") + ")." };
            } catch (Win32Exception error) {
                return new GhostLaunchResult { Error = error.NativeErrorCode == 1223 ? "Opening this app was cancelled." :
                    "Windows could not open this app (error " + error.NativeErrorCode + ")." };
            } catch (ArgumentException) {
                return new GhostLaunchResult { Error = "This app entry is no longer available. Refresh search and try again." };
            } catch (UnauthorizedAccessException) {
                return new GhostLaunchResult { Error = "Windows did not allow this app to open." };
            } catch (IOException) {
                return new GhostLaunchResult { Error = "This app shortcut changed or is unavailable. Refresh search and try again." };
            } finally { Release(activation); }
        }
    }
}
