using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace TaskbarSelect
{
    // The dedicated per-user launcher cache contains only a pinned, native, kernel32-only
    // bootstrap. Managed code is admitted only from its protected cache.
    internal static class AdministratorLaunch
    {
        internal const string PayloadConfiguration = "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.8\"/></startup></configuration>\n";
        const string BootstrapResource = "TaskbarSelect.AdministratorBootstrap", HashResource = "TaskbarSelect.AdministratorPayloadHash";
        const string CacheName = "TaskbarSelect.Admin", PayloadName = "AdministratorPayload.exe", ConfigurationName = "AdministratorPayload.exe.config", TempName = "tmp";
        const string Administrators = "S-1-5-32-544", LocalSystem = "S-1-5-18", Users = "S-1-5-32-545";
        const uint GenericRead = 0x80000000, OpenExisting = 3, OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
        const uint DirectoryAttribute = 0x10, ReparseAttribute = 0x400;
        const int MaximumBootstrapBytes = 16 * 1024 * 1024, MaximumPayloadBytes = 64 * 1024 * 1024;
        static readonly byte[] configuration = new UTF8Encoding(false, true).GetBytes(PayloadConfiguration);
        static readonly object namespaceGate = new object();
        static List<IDisposable> namespaceLeases;
        static string namespaceIdentity;

        static bool LauncherPathsOverlap(string first, string second)
        {
            return String.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
                first.StartsWith(second + "\\", StringComparison.OrdinalIgnoreCase) ||
                second.StartsWith(first + "\\", StringComparison.OrdinalIgnoreCase);
        }
        internal static string LauncherDirectory(string localData, string profileRoot, bool isolated)
        {
            const string invalid = "The administrator launcher storage selection is invalid.";
            string local = CanonicalDosPath(localData), profile = CanonicalDosPath(profileRoot);
            if (local == null || profile == null || local.Length == 3 || profile.Length == 3) throw new IOException(invalid);
            string production = System.IO.Path.Combine(local, "TaskbarSelect");
            // A fixed SpecialFolder-derived Temp location, never ambient TMP or
            // a fallback after a refused Stage. All Stage guards still apply.
            string cache = System.IO.Path.Combine(local, "Temp", "TaskbarSelect.Launchers");
            if (CanonicalDosPath(production) == null || CanonicalDosPath(cache) == null || LauncherPathsOverlap(profile, cache) ||
                (isolated ? LauncherPathsOverlap(profile, production) : !String.Equals(profile, production, StringComparison.OrdinalIgnoreCase)))
                throw new IOException(invalid);
            string selected = isolated ? System.IO.Path.Combine(cache, "tests", Hash(Encoding.UTF8.GetBytes(profile.ToUpperInvariant()))) :
                System.IO.Path.Combine(cache, "production");
            // Reserve the exact length of Stage's hash and executable suffix.
            // A canonical selected root alone must not admit an unusable image path.
            string staged = System.IO.Path.Combine(selected, "administrator-launchers", new string('0', 64), "AdministratorBootstrap.exe");
            if (CanonicalDosPath(selected) == null || CanonicalDosPath(staged) == null || LauncherPathsOverlap(profile, selected))
                throw new IOException(invalid);
            return selected;
        }
        internal static string StageFailureDetail(IOException error)
        {
            if (error == null) return "The administrator launcher staging checks failed.";
            string message = error.Message;
            foreach (string prefix in new[] {
                "The administrator launcher directory could not be created. [nt=0x",
                "The administrator bootstrap could not be staged. [nt=0x" }) {
                uint status;
                if (message != null && message.Length == prefix.Length + 9 && message.StartsWith(prefix, StringComparison.Ordinal) &&
                    message.EndsWith("]", StringComparison.Ordinal) && UInt32.TryParse(message.Substring(prefix.Length, 8),
                        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out status))
                    return prefix + status.ToString("X8", CultureInfo.InvariantCulture) + "]";
            }
            // Exact reviewed guard texts only; ordinary filesystem exception
            // messages can contain private paths and are never exposed.
            foreach (string guard in new[] {
                "The administrator launcher storage selection is invalid.", "The administrator launcher directory is not a safe local directory.",
                "The administrator launcher path is invalid.", "The administrator bootstrap resource is invalid.",
                "The administrator payload identity is invalid.", "The new administrator bootstrap identity is unsafe.",
                "The administrator bootstrap could not be pinned.", "The existing administrator bootstrap does not match this application.",
                "The administrator launcher directory contains an unexpected file.", "The administrator bootstrap directory changed.",
                "An administrator launcher directory changed.", "An administrator directory is not local NTFS.",
                "An administrator directory alias changed.", "An administrator file path is invalid.",
                "An administrator file could not be pinned.", "An administrator file identity is unsafe.",
                "An administrator file could not be resolved.", "The administrator resource is unavailable.",
                "The administrator resource is incomplete.", "The administrator resource changed.",
                "The administrator directory could not be inspected.", "The administrator directory contains unexpected entries.",
                "The administrator directory changed during inspection.",
                "Windows loader override policy could not be verified for administrator support.",
                "Windows loader override DevOverrideEnable must be disabled for administrator support.",
                "Windows loader override PreferExternalManifest must be disabled for administrator support." })
                if (String.Equals(message, guard, StringComparison.Ordinal)) return guard;
            return "The administrator launcher staging checks failed.";
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetProcessId(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern SafeWaitHandle OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr process, out NativeProcessIdentity.FileTime created,
            out NativeProcessIdentity.FileTime exited, out NativeProcessIdentity.FileTime kernel, out NativeProcessIdentity.FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int count);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInformation
        {
            internal uint Size; internal IntPtr Reserved, Desktop, Title;
            internal uint X, Y, Width, Height, CharactersX, CharactersY, Fill, Flags;
            internal ushort Show, ReservedBytes; internal IntPtr ReservedData, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] struct ProcessInformation
        { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
            bool inherit, uint flags, IntPtr environment, string directory, ref StartupInformation startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exit);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exit);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr information, int length, out int required);
        [StructLayout(LayoutKind.Sequential)] struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)] struct ObjectAttributes
        { internal int Length; internal IntPtr Root, Name; internal uint Attributes; internal IntPtr Security, Quality; }
        [StructLayout(LayoutKind.Sequential)] struct IoStatus { internal IntPtr Status; internal UIntPtr Information; }
        [DllImport("ntdll.dll")] static extern int NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes, out IoStatus status,
            IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr extended, uint extendedLength);
        [DllImport("ntdll.dll")] static extern int NtOpenDirectoryObject(out IntPtr handle, uint access, ref ObjectAttributes attributes);
        [DllImport("ntdll.dll")] static extern int NtOpenSymbolicLinkObject(out IntPtr handle, uint access, ref ObjectAttributes attributes);
        [DllImport("ntdll.dll")] static extern int NtQuerySymbolicLinkObject(SafeFileHandle handle, ref UnicodeString target, out uint required);
        [DllImport("ntdll.dll")] static extern int NtQueryObject(SafeFileHandle handle, int kind, IntPtr information, uint size, out uint required);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern uint GetSecurityInfo(SafeFileHandle handle, int kind, uint requested, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FindData
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] internal string Alternate;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstFile(string pattern, out FindData data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool FindNextFile(IntPtr handle, out FindData data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr handle);

        internal sealed class ApprovalIdentity
        {
            internal uint Process, Session, Integrity;
            internal long Started;
            internal string Sid;
            internal bool Elevated;
        }
        internal sealed class ApprovalStartException : IOException, IDisposable
        {
            internal readonly Process Child;
            internal readonly bool NeverResumed, ImageVerified;
            readonly SafeWaitHandle retained;
            internal ApprovalStartException(string message, Process process, IntPtr handle, bool neverResumed, bool verified, Exception cause) : base(message, cause)
            { Child = process; NeverResumed = neverResumed; ImageVerified = verified; retained = new SafeWaitHandle(handle, true); }
            internal bool WaitForExit(int milliseconds)
            {
                if (retained.IsClosed || retained.IsInvalid) throw new InvalidOperationException("The owned approval host handle is unavailable.");
                bool reference = false;
                try { retained.DangerousAddRef(ref reference); return WaitForSingleObject(retained.DangerousGetHandle(), (uint)Math.Max(0, milliseconds)) == 0; }
                finally { if (reference) retained.DangerousRelease(); }
            }
            internal int ExitCode
            {
                get {
                    uint value;
                    if (!GetExitCodeProcess(retained, out value)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (value == 259) throw new InvalidOperationException("The owned approval host has not stopped.");
                    return unchecked((int)value);
                }
            }
            public void Dispose() { retained.Dispose(); if (Child != null) Child.Dispose(); }
        }

        internal sealed class BootstrapImage : IDisposable
        {
            readonly List<IDisposable> leases;
            readonly FileStream image;
            readonly string bootstrapHash;
            bool disposed;
            internal string Path { get; private set; }
            internal string PayloadHash { get; private set; }
            internal string BootstrapHash { get { return bootstrapHash; } }
            internal BootstrapImage(string path, string payloadHash, string hash, FileStream stream, List<IDisposable> held)
            { Path = path; PayloadHash = payloadHash; bootstrapHash = hash; image = stream; leases = held; }
            internal string PayloadPath(string token)
            {
                if (disposed || !ValidToken(token)) throw new InvalidOperationException("The administrator launch is no longer available.");
                return ExpectedPayloadPath(PayloadHash, token);
            }
            internal Process StartApprovalHost(uint parent, long started, string token)
            { return StartApproval(false, parent, started, token); }
            internal Process StartApprovalSelfTest(uint parent, long started, string token)
            { return StartApproval(true, parent, started, token); }
            Process StartApproval(bool selfTest, uint parent, long started, string token)
            {
                if (disposed || !ValidToken(token) || parent == 0 || started <= 0)
                    throw new IOException("The administrator approval host arguments are invalid.");
                ApprovalIdentity owner = ReadApprovalIdentity(GetCurrentProcess());
                if (owner == null || owner.Process != parent || owner.Started != started ||
                    owner.Integrity != NativeProcessIdentity.Medium || owner.Elevated)
                    throw new UnauthorizedAccessException("Administrator approval requires the current standard Windows session.");
                string executable = ExtendedDosPath(image.SafeFileHandle);
                using (var actual = OpenFile(executable.Substring(4))) if (!SameFile(image.SafeFileHandle, actual.SafeFileHandle) ||
                    !String.Equals(Hash(image), bootstrapHash, StringComparison.Ordinal)) throw new IOException("The administrator launcher identity changed.");
                string args = (selfTest ? "--approval-self-test " : "--request-administrator ") + token + " " +
                    parent.ToString(CultureInfo.InvariantCulture) + " " + started.ToString(CultureInfo.InvariantCulture);
                var startup = new StartupInformation { Size = (uint)Marshal.SizeOf(typeof(StartupInformation)) };
                ProcessInformation created;
                // DOS resolution is used only to create a MED process that is
                // still suspended. Its retained physical image and token must
                // agree with the pinned file before any native code can run.
                if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" " + args), IntPtr.Zero, IntPtr.Zero, false,
                    0x08000004, IntPtr.Zero, Environment.SystemDirectory, ref startup, out created))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The administrator approval host could not be created.");
                Process process = null;
                bool resumeAttempted = false, imageVerified = false;
                try {
                    ApprovalIdentity child = ReadApprovalIdentity(created.Process);
                    if (!SameApprovalIdentity(owner, child) || child.Process != created.ProcessId) throw new UnauthorizedAccessException("The administrator approval host token could not be verified.");
                    process = Process.GetProcessById((int)created.ProcessId);
                    long actualStarted;
                    if (GetProcessId(process.Handle) != created.ProcessId || !VerifyBootstrapProcess(process, out actualStarted) || actualStarted != child.Started)
                        throw new UnauthorizedAccessException("The administrator approval host image could not be verified.");
                    imageVerified = true;
                    // The private raw thread handle belongs to this exact
                    // newly created MED process; no PID-based resume or kill.
                    resumeAttempted = true;
                    if (ResumeThread(created.Thread) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "The administrator approval host could not be resumed.");
                    return process;
                } catch (Exception error) {
                    TerminateProcess(created.Process, 112);
                    bool drained = WaitForSingleObject(created.Process, 5000) == 0;
                    if (!drained) {
                        // The caller must retain this exact child and the Stage
                        // image lease instead of claiming that no process exists.
                        var failure = new ApprovalStartException("The administrator approval host did not confirm shutdown.", process, created.Process, !resumeAttempted, imageVerified, error);
                        created.Process = IntPtr.Zero; // Exact raw handle ownership transfers even if the managed Process wrapper failed.
                        throw failure;
                    }
                    if (process != null) process.Dispose();
                    throw;
                } finally {
                    CloseHandle(created.Thread); if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
                }
            }
            internal bool VerifyBootstrapProcess(uint process, long started)
            {
                if (disposed || process == 0 || started <= 0) return false;
                try {
                    // A discovered HIGH process must not use Framework's
                    // Process.Handle getter, which requests full process access.
                    // Metadata and lifetime observation need only these rights.
                    using (var handle = OpenProcess(0x101000, false, process)) {
                        if (handle == null || handle.IsInvalid) return false;
                        bool reference = false;
                        try {
                            handle.DangerousAddRef(ref reference);
                            long actualStarted;
                            return VerifyBootstrapHandle(handle.DangerousGetHandle(), process, out actualStarted) && actualStarted == started;
                        } finally { if (reference) handle.DangerousRelease(); }
                    }
                } catch (Exception) { return false; }
            }
            internal bool VerifyBootstrapProcess(Process process)
            { long started; return VerifyBootstrapProcess(process, out started); }
            internal bool VerifyBootstrapProcess(Process process, out long started)
            {
                started = 0;
                if (disposed || process == null) return false;
                try { return VerifyBootstrapHandle(process.Handle, unchecked((uint)process.Id), out started); }
                catch (Exception) { return false; }
            }
            bool VerifyBootstrapHandle(IntPtr handle, uint expectedProcess, out long started)
            {
                started = 0;
                if (disposed || handle == IntPtr.Zero || handle == new IntPtr(-1) || expectedProcess == 0) return false;
                try {
                    // This retained handle remains attached to the created
                    // process after a benign early refusal. Reopening its PID
                    // can fail or identify a later process after reuse.
                    uint pid = GetProcessId(handle);
                    NativeProcessIdentity.FileTime created, exited, kernel, user;
                    if (pid == 0 || pid != expectedProcess || !GetProcessTimes(handle, out created, out exited, out kernel, out user)) return false;
                    long creationTicks = NativeProcessIdentity.CreationTicks(created);
                    if (creationTicks <= 0) return false;
                    // Prefer the kernel audit image name so a DOS device alias
                    // cannot choose the identity being authenticated. It also
                    // remains available after Win32 spelling disappears at exit.
                    // Only this API-produced physical device name is admitted,
                    // opened directly in NT so no DOS alias selects the file.
                    var name = new StringBuilder(32768); int length = name.Capacity;
                    if (QueryFullProcessImageName(handle, 1, name, ref length)) {
                        if (length <= 0 || length >= name.Capacity || name.Length != length || !ValidProcessPathKernel(name.ToString())) return false;
                        using (var peer = OpenKernelImage(name.ToString())) {
                            bool verified = peer != null && !peer.IsInvalid && SameFile(image.SafeFileHandle, peer) &&
                                String.Equals(Hash(image), bootstrapHash, StringComparison.Ordinal) && GetProcessId(handle) == pid;
                            if (verified) started = creationTicks;
                            return verified;
                        }
                    }
                    // Only an unavailable kernel query permits the older safe
                    // Win32 spelling, still bound to the same held file/hash.
                    name.Clear(); length = name.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, name, ref length) || length <= 0 || length >= name.Capacity || name.Length != length || !InputPath(name.ToString())) return false;
                    using (var peer = OpenFile(name.ToString())) {
                        bool verified = SameFile(image.SafeFileHandle, peer.SafeFileHandle) &&
                            String.Equals(Hash(image), bootstrapHash, StringComparison.Ordinal) && GetProcessId(handle) == pid;
                        if (verified) started = creationTicks;
                        return verified;
                    }
                } catch (Exception) { return false; }
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose();
                leases.Clear();
            }
        }

        static string ExtendedDosPath(SafeFileHandle file)
        {
            var path = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
            string value = path.ToString();
            if (length == 0 || length >= path.Capacity || value.Length != length || !value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                CanonicalDosPath(value.Substring(4)) == null) throw new IOException("The administrator executable DOS spelling is unavailable.");
            return value;
        }
        static bool ValidProcessPathKernel(string native)
        {
            const string prefix = @"\Device\HarddiskVolume";
            if (native == null || !native.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            int separator = native.IndexOf('\\', prefix.Length);
            if (separator <= prefix.Length || !SafeSegments(native.Substring(separator + 1))) return false;
            for (int index = prefix.Length; index < separator; index++) if (native[index] < '0' || native[index] > '9') return false;
            return true;
        }
        static SafeFileHandle OpenKernelImage(string native)
        {
            if (!ValidProcessPathKernel(native) || native.Length >= 32768) throw new IOException("The process kernel image name is invalid.");
            IntPtr text = Marshal.StringToHGlobalUni(native), unicode = IntPtr.Zero;
            try {
                var value = new UnicodeString { Length = checked((ushort)(native.Length * 2)), MaximumLength = checked((ushort)(native.Length * 2)), Buffer = text };
                unicode = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString))); Marshal.StructureToPtr(value, unicode, false);
                var attributes = new ObjectAttributes { Length = Marshal.SizeOf(typeof(ObjectAttributes)), Name = unicode, Root = IntPtr.Zero, Attributes = 0x1040 };
                IntPtr raw; IoStatus information;
                int status = NtCreateFile(out raw, GenericRead | 0x100000, ref attributes, out information, IntPtr.Zero, 0x80, 1, 1,
                    OpenReparsePoint | 0x20 | 0x40, IntPtr.Zero, 0);
                var handle = new SafeFileHandle(raw, true);
                if (status < 0 || handle.IsInvalid) { handle.Dispose(); throw new IOException("The process kernel image could not be pinned."); }
                try {
                    FileInformation file;
                    if (!GetFileInformationByHandle(handle, out file) || !SafeInformation(file, false)) throw new IOException("The process kernel image identity is unsafe.");
                    return handle;
                } catch { handle.Dispose(); throw; }
            } finally { if (unicode != IntPtr.Zero) Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
        }
        internal static bool SameApprovalIdentity(ApprovalIdentity parent, ApprovalIdentity child)
        {
            return parent != null && child != null && parent.Process != 0 && child.Process != 0 && parent.Process != child.Process &&
                parent.Started > 0 && child.Started > 0 && parent.Integrity == NativeProcessIdentity.Medium && child.Integrity == NativeProcessIdentity.Medium &&
                !parent.Elevated && !child.Elevated && !String.IsNullOrEmpty(parent.Sid) && parent.Sid == child.Sid && parent.Session == child.Session;
        }
        static ApprovalIdentity ReadApprovalIdentity(IntPtr process)
        {
            IntPtr token = IntPtr.Zero, buffer = IntPtr.Zero;
            try {
                uint pid = GetProcessId(process);
                NativeProcessIdentity.FileTime created, exited, kernel, user;
                if (pid == 0 || !GetProcessTimes(process, out created, out exited, out kernel, out user) || !OpenProcessToken(process, 8, out token)) return null;
                long started = NativeProcessIdentity.CreationTicks(created);
                if (started <= 0) return null;
                buffer = Marshal.AllocHGlobal(4096);
                int required;
                if (!GetTokenInformation(token, 20, buffer, 4, out required) || required != 4) return null;
                int elevated = Marshal.ReadInt32(buffer); if (elevated < 0 || elevated > 1) return null;
                if (!GetTokenInformation(token, 12, buffer, 4, out required) || required != 4) return null;
                uint session = unchecked((uint)Marshal.ReadInt32(buffer));
                if (!GetTokenInformation(token, 25, buffer, 4096, out required) || required < IntPtr.Size + 4 || required > 4096) return null;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                long offset = sid.ToInt64() - buffer.ToInt64();
                if (offset < IntPtr.Size + 4 || offset + 8 > required) return null;
                int count = Marshal.ReadByte(sid, 1);
                if (count <= 0 || count > 15 || offset + 8 + count * 4 > required) return null;
                uint integrity = unchecked((uint)Marshal.ReadInt32(sid, 8 + (count - 1) * 4));
                string userSid;
                using (var identity = new WindowsIdentity(token)) userSid = identity.User == null ? null : identity.User.Value;
                return String.IsNullOrEmpty(userSid) ? null : new ApprovalIdentity { Process = pid, Started = started, Session = session,
                    Integrity = integrity, Elevated = elevated != 0, Sid = userSid };
            } catch (Exception) { return null; }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); if (token != IntPtr.Zero) CloseHandle(token); }
        }

        internal static BootstrapImage Stage(string profileRoot)
        {
            // Never overwrite, repair or execute a different existing image.
            CheckMachineLoaderPolicy();
            string root = CanonicalDosPath(profileRoot);
            if (root == null || String.Equals(root.TrimEnd('\\'), System.IO.Path.GetPathRoot(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || !LocalNtfs(root))
                throw new IOException("The administrator launcher directory is not a safe local directory.");
            byte[] bytes = ResourceBytes(BootstrapResource, MaximumBootstrapBytes);
            if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new IOException("The administrator bootstrap resource is invalid.");
            byte[] hashBytes = ResourceBytes(HashResource, 64);
            string payloadHash = Encoding.ASCII.GetString(hashBytes);
            if (hashBytes.Length != 64 || !ValidHash(payloadHash)) throw new IOException("The administrator payload identity is invalid.");
            string hash = Hash(bytes), launchers = System.IO.Path.Combine(root, "administrator-launchers"), folder = System.IO.Path.Combine(launchers, hash);
            string path = System.IO.Path.Combine(folder, "AdministratorBootstrap.exe");
            if (CanonicalDosPath(path) == null) throw new IOException("The administrator launcher path is invalid.");
            var leases = new List<IDisposable>();
            try {
                string physicalRoot = PinDirectories(root, false, leases, true);
                string physicalLaunchers = physicalRoot.TrimEnd('\\') + "\\administrator-launchers";
                string physicalFolder = physicalLaunchers + "\\" + hash;
                var rootLease = (SafeFileHandle)leases[leases.Count - 1];
                var launchersLease = CreatePinnedDirectory(rootLease, "administrator-launchers", physicalLaunchers, leases);
                var folderLease = CreatePinnedDirectory(launchersLease, hash, physicalFolder, leases);
                string physicalPath = physicalFolder + "\\AdministratorBootstrap.exe";
                SafeFileHandle created; int creation = RelativeFile(folderLease, "AdministratorBootstrap.exe", false, true, out created);
                using (created) {
                    if (creation >= 0 && created != null && !created.IsInvalid) {
                        FileInformation information;
                        if (!GetFileInformationByHandle(created, out information) || !SafeInformation(information, false) ||
                            !String.Equals(FinalPath(created), physicalPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("The new administrator bootstrap identity is unsafe.");
                        using (var output = new FileStream(created, FileAccess.Write)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                    } else if (creation != unchecked((int)0xC0000035)) // STATUS_OBJECT_NAME_COLLISION
                        throw new IOException("The administrator bootstrap could not be staged. [nt=0x" + unchecked((uint)creation).ToString("X8", CultureInfo.InvariantCulture) + "]");
                }
                SafeFileHandle imageHandle;
                if (RelativeFile(folderLease, "AdministratorBootstrap.exe", false, false, out imageHandle) < 0) { if (imageHandle != null) imageHandle.Dispose(); throw new IOException("The administrator bootstrap could not be pinned."); }
                FileStream image = ReadStream(imageHandle, physicalPath); leases.Add(image);
                if (image.Length != bytes.Length || !String.Equals(Hash(image), hash, StringComparison.Ordinal))
                    throw new IOException("The existing administrator bootstrap does not match this application.");
                // The native image's embedded manifest plus disabled machine
                // overrides prevent later .local/external-manifest redirection.
                // Existing sidecars still fail closed rather than being removed.
                string[] siblings = DirectoryNames(physicalFolder, 2);
                if (siblings.Length != 1 || siblings[0] != "AdministratorBootstrap.exe")
                    throw new IOException("The administrator launcher directory contains an unexpected file.");
                string finalImage = FinalPath(image.SafeFileHandle);
                if (!String.Equals(finalImage, physicalPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("The administrator bootstrap directory changed.");
                return new BootstrapImage(finalImage, payloadHash, hash, image, leases);
            } catch {
                for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose();
                throw;
            }
        }
        internal static void CheckMachineLoaderPolicy()
        {
            try {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) {
                    CheckMachineFlag(machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", "DevOverrideEnable");
                    CheckMachineFlag(machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\SideBySide", "PreferExternalManifest");
                }
            } catch (IOException) { throw; }
            catch (Exception) { throw new IOException("Windows loader override policy could not be verified for administrator support."); }
        }
        static void CheckMachineFlag(RegistryKey machine, string path, string name)
        {
            using (RegistryKey key = machine.OpenSubKey(path, false)) {
                if (key == null) return;
                object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) return;
                if (!SafeLoaderFlag(value, key.GetValueKind(name)))
                    throw new IOException("Windows loader override " + name + " must be disabled for administrator support.");
            }
        }
        internal static bool SafeLoaderFlag(object value, RegistryValueKind kind)
        { return value == null || (kind == RegistryValueKind.DWord && value is int && (int)value == 0); }
        static byte[] ResourceBytes(string name, int maximum)
        {
            using (Stream stream = typeof(AdministratorLaunch).Assembly.GetManifestResourceStream(name)) {
                if (stream == null || !stream.CanRead || !stream.CanSeek || stream.Length <= 0 || stream.Length > maximum) throw new IOException("The administrator resource is unavailable.");
                byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
                while (offset < bytes.Length) { int read = stream.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new IOException("The administrator resource is incomplete."); offset += read; }
                if (stream.ReadByte() != -1) throw new IOException("The administrator resource changed.");
                return bytes;
            }
        }
        static SafeFileHandle CreatePinnedDirectory(SafeFileHandle parent, string name, string path, List<IDisposable> leases)
        {
            SafeFileHandle handle;
            int status = RelativeFile(parent, name, true, true, out handle);
            if (status < 0) { if (handle != null) handle.Dispose(); throw new IOException("The administrator launcher directory could not be created. [nt=0x" + unchecked((uint)status).ToString("X8", CultureInfo.InvariantCulture) + "]"); }
            try {
                FileInformation information;
                if (handle == null || handle.IsInvalid || !GetFileInformationByHandle(handle, out information) || !SafeInformation(information, true) ||
                    !String.Equals(FinalPath(handle).TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new IOException("An administrator launcher directory changed.");
                leases.Add(handle); return handle;
            } catch { if (handle != null) handle.Dispose(); throw; }
        }
        static string PinDirectories(string path, bool protectedCache, List<IDisposable> leases, bool createMissing = false)
        {
            string canonical = CanonicalDosPath(path);
            if (canonical == null || !LocalNtfs(canonical)) throw new IOException("An administrator directory is not local NTFS.");
            string drive = System.IO.Path.GetPathRoot(canonical), current = drive;
            var driveLease = OpenDirectory(current); leases.Add(driveLease);
            string physical = FinalPath(driveLease).TrimEnd('\\');
            bool protectedPart = false;
            string cache = protectedCache ? System.IO.Path.Combine(CommonData(), CacheName) : null;
            foreach (string segment in canonical.Substring(drive.Length).Split('\\')) {
                current = System.IO.Path.Combine(current, segment);
                physical += "\\" + segment;
                SafeFileHandle parentLease = (SafeFileHandle)leases[leases.Count - 1];
                SafeFileHandle directoryLease;
                if (createMissing) directoryLease = CreatePinnedDirectory(parentLease, segment, physical, leases);
                else { directoryLease = OpenDirectory(physical); leases.Add(directoryLease); }
                if (!String.Equals(FinalPath(directoryLease).TrimEnd('\\'), physical, StringComparison.OrdinalIgnoreCase)) throw new IOException("An administrator directory alias changed.");
                if (String.Equals(current, cache, StringComparison.OrdinalIgnoreCase)) protectedPart = true;
                if (protectedCache && protectedPart && !Security(directoryLease, true))
                    throw new UnauthorizedAccessException("An administrator directory is not protected.");
            }
            if (protectedCache && !protectedPart) throw new UnauthorizedAccessException("The administrator cache is outside its fixed directory.");
            return physical;
        }
        static int RelativeFile(SafeFileHandle parent, string name, bool directory, bool create, out SafeFileHandle file)
        {
            file = null;
            if (parent == null || parent.IsInvalid || parent.IsClosed || String.IsNullOrEmpty(name) || name.IndexOf('\\') >= 0 || !SafeSegments(name)) return unchecked((int)0xC000000D);
            bool retained = false;
            IntPtr text = Marshal.StringToHGlobalUni(name), unicode = IntPtr.Zero;
            try {
                parent.DangerousAddRef(ref retained);
                var value = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = text };
                unicode = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString))); Marshal.StructureToPtr(value, unicode, false);
                var attributes = new ObjectAttributes { Length = Marshal.SizeOf(typeof(ObjectAttributes)), Root = parent.DangerousGetHandle(), Name = unicode,
                    Attributes = 0x1040 }; // OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE
                IntPtr opened; IoStatus status;
                // NtCreateFile validates SYNCHRONOUS_IO_NONALERT before mapping
                // generic rights. Unlike CreateFileW, the native API needs an
                // explicit SYNCHRONIZE bit even when GENERIC_READ includes it
                // after mapping. Without it the native call rejects parameter0x20.
                uint access = (directory || !create ? GenericRead : GenericRead | 0x40000000) | 0x00100000;
                uint disposition = directory ? (create ? 3u : 1u) : (create ? 2u : 1u);
                // FILE_ATTRIBUTE_DIRECTORY describes observed metadata; it is
                // not a valid creation attribute. DIRECTORY_FILE below selects
                // directory creation while its input attributes remain NORMAL.
                int result = NtCreateFile(out opened, access, ref attributes, out status, IntPtr.Zero, 0x80,
                    directory ? 3u : (create ? 0u : 1u), disposition, (directory ? 1u : 0x40u) | OpenReparsePoint | 0x20u, IntPtr.Zero, 0);
                if (opened != IntPtr.Zero && opened != new IntPtr(-1)) file = new SafeFileHandle(opened, true);
                return result;
            } finally { if (retained) parent.DangerousRelease(); if (unicode != IntPtr.Zero) Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
        }
        static FileStream ReadStream(SafeFileHandle handle, string expected)
        {
            try {
                FileInformation information;
                if (handle == null || handle.IsInvalid || !GetFileInformationByHandle(handle, out information) || !SafeInformation(information, false) ||
                    !String.Equals(FinalPath(handle), expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("The administrator bootstrap file identity changed.");
                return new FileStream(handle, FileAccess.Read);
            } catch { if (handle != null) handle.Dispose(); throw; }
        }
        static string CommonData()
        {
            string value = CanonicalDosPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            if (value == null) throw new IOException("The Windows common application directory is unavailable.");
            return value;
        }
        internal static string ExpectedPayloadPath(string hash, string token)
        {
            if (!ValidHash(hash) || !ValidToken(token)) throw new ArgumentException("The administrator cache identity is invalid.");
            return System.IO.Path.Combine(CommonData(), CacheName, hash, token, PayloadName);
        }
        internal static byte[] ReadNativeStopDiagnostic(string hash, string token)
        {
            // Advisory failure evidence only. Never supply paths from the trace,
            // run code, clean a session, or use its contents as a drain grant.
            string expected = ExpectedPayloadPath(hash, token);
            var leases = new List<IDisposable>();
            try {
                string folder = PinDirectories(System.IO.Path.GetDirectoryName(expected), true, leases);
                string temp = folder + "\\" + TempName;
                using (SafeFileHandle directory = OpenDirectory(temp)) {
                    if (!Security(directory, true) || !String.Equals(FinalPath(directory).TrimEnd('\\'), temp, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The native diagnostic directory is not protected.");
                    string path = temp + "\\NativeStop.bin";
                    using (FileStream record = OpenFile(path)) {
                        if (!Security(record.SafeFileHandle, false) || !String.Equals(FinalPath(record.SafeFileHandle), path, StringComparison.OrdinalIgnoreCase) ||
                            record.Length != NativeStopDiagnostics.RecordLength) throw new IOException("The native diagnostic identity is invalid.");
                        byte[] bytes = new byte[NativeStopDiagnostics.RecordLength]; int offset = 0;
                        while (offset < bytes.Length) { int count = record.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new IOException("The native diagnostic is incomplete."); offset += count; }
                        if (record.ReadByte() != -1) throw new IOException("The native diagnostic length changed.");
                        return bytes;
                    }
                }
            } finally { for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose(); }
        }
        internal static bool VerifyPayload(string path, string expectedHash, string token)
        { string physical; return VerifyPayload(path, expectedHash, token, out physical); }
        static bool VerifyPayload(string path, string expectedHash, string token, out string physical)
        {
            physical = null;
            if (!ValidHash(expectedHash) || !ValidToken(token) || !InputPath(path)) return false;
            var leases = new List<IDisposable>();
            try {
                string expected = ExpectedPayloadPath(expectedHash, token), folder = System.IO.Path.GetDirectoryName(expected);
                string physicalFolder = PinDirectories(folder, true, leases);
                var payload = OpenFile(physicalFolder + "\\" + PayloadName); leases.Add(payload);
                var config = OpenFile(physicalFolder + "\\" + ConfigurationName); leases.Add(config);
                string finalPayload = FinalPath(payload.SafeFileHandle);
                if (!String.Equals(path, expected, StringComparison.OrdinalIgnoreCase) && !String.Equals(path, finalPayload, StringComparison.OrdinalIgnoreCase)) return false;
                using (var supplied = OpenFile(path))
                    if (!SameFile(payload.SafeFileHandle, supplied.SafeFileHandle)) return false;
                if (payload.Length <= 0 || payload.Length > MaximumPayloadBytes || !String.Equals(Hash(payload), expectedHash, StringComparison.Ordinal) || !ExactConfiguration(config)) return false;
                if (!Security(payload.SafeFileHandle, false) || !Security(config.SafeFileHandle, false)) return false;
                bool temp = false; int files = 0;
                foreach (string name in DirectoryNames(physicalFolder, 4)) {
                    if (String.Equals(name, PayloadName, StringComparison.Ordinal) || String.Equals(name, ConfigurationName, StringComparison.Ordinal)) { files++; continue; }
                    if (temp || name != TempName) return false;
                    string entry = physicalFolder + "\\" + name;
                    using (var held = OpenDirectory(entry)) {
                        if (!Security(held, true) || !BenignTemp(entry, 0, new int[1])) return false;
                    }
                    temp = true;
                }
                // Native always creates this protected working directory before
                // resuming managed code. It is also the only permitted temp path.
                if (files != 2 || !temp) return false;
                physical = finalPayload; return true;
            } catch (Exception) { return false; }
            finally { for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose(); }
        }
        static bool BenignTemp(string folder, int depth, int[] count)
        {
            if (depth > 4) return false;
            foreach (string name in DirectoryNames(folder, 257)) {
                if (++count[0] > 256 || !BenignTempName(name)) return false;
                string entry = folder + "\\" + name;
                using (SafeFileHandle held = CreateFile(entry, GenericRead, 1, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero)) {
                    FileInformation information;
                    if (held.IsInvalid || !GetFileInformationByHandle(held, out information)) return false;
                    bool directory = (information.Attributes & DirectoryAttribute) != 0;
                    if (!SafeInformation(information, directory) || (directory && !BenignTemp(entry, depth + 1, count))) return false;
                    if (!directory) using (var contents = new FileStream(held, FileAccess.Read)) if (!BenignTempHeader(contents)) return false;
                }
            }
            return true;
        }
        internal static bool BenignTempName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)) return false;
            string extension = System.IO.Path.GetExtension(name);
            foreach (string forbidden in new[] { ".exe", ".dll", ".config", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".js", ".scr", ".cpl", ".hta", ".msi", ".msp", ".sys", ".ocx", ".manifest", ".local" })
                if (String.Equals(extension, forbidden, StringComparison.OrdinalIgnoreCase)) return false;
            return name.IndexOfAny(new[] { '\\', '/', ':', '\0' }) < 0;
        }
        internal static bool BenignTempHeader(Stream stream)
        { return stream != null && !(stream.ReadByte() == 'M' && stream.ReadByte() == 'Z'); }
        internal static bool IsProtectedPayloadCurrent
        {
            get {
                string hash, token;
                string path = typeof(AdministratorLaunch).Assembly.Location;
                return PayloadComponents(path, out hash, out token) && VerifyPayload(path, hash, token);
            }
        }
        internal static bool PayloadComponents(string path, out string hash, out string token)
        {
            hash = token = null;
            if (!InputPath(path)) return false;
            string[] parts = path.Split('\\');
            if (parts.Length < 4 || parts[parts.Length - 1] != PayloadName || parts[parts.Length - 4] != CacheName) return false;
            hash = parts[parts.Length - 3]; token = parts[parts.Length - 2];
            return ValidHash(hash) && ValidToken(token);
        }
        internal static ProcessStartInfo CreateWorkerStartInfo(string arguments)
        {
            string[] values;
            if (!WorkerArguments(arguments, out values) || !HighToken(NativeProcessIdentity.Current))
                throw new UnauthorizedAccessException("Administrator worker launch is not authorized.");
            string executable = typeof(AdministratorLaunch).Assembly.Location;
            string hash, token;
            if (!PayloadComponents(executable, out hash, out token)) throw new UnauthorizedAccessException();
            string physical;
            if (!VerifyPayload(executable, hash, token, out physical)) throw new UnauthorizedAccessException("The administrator payload cache is not protected.");
            // The native job owner retains the physical cache file/directory
            // leases for this entire session, including every managed child.
            using (var pinned = OpenFile(physical)) {
                string dos = ExtendedDosPath(pinned.SafeFileHandle);
                VerifyHighNamespace(token, pinned.SafeFileHandle, dos, Environment.SystemDirectory);
                using (var actual = OpenFile(dos.Substring(4))) if (!SameFile(pinned.SafeFileHandle, actual.SafeFileHandle))
                    throw new UnauthorizedAccessException("The protected worker DOS spelling changed.");
                // Framework 4 rejects extended executable spellings when it
                // reads Assembly.Location. The sealed map and SameFile proof
                // above remain the authority for this execution spelling.
                executable = ClrLaunchPath(dos);
            }
            string working = Environment.SystemDirectory;
            string temp = executable.Substring(0, executable.LastIndexOf('\\')) + "\\" + TempName;
            var start = new ProcessStartInfo { FileName = executable, Arguments = arguments, WorkingDirectory = working,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            CleanEnvironment(start, Environment.GetFolderPath(Environment.SpecialFolder.Windows), temp);
            return start;
        }
        internal static string ClrLaunchPath(string extended)
        {
            string path = extended != null && extended.StartsWith(@"\\?\", StringComparison.Ordinal) ? CanonicalDosPath(extended.Substring(4)) : null;
            if (path == null) throw new UnauthorizedAccessException("The protected CLR execution spelling is invalid.");
            return path;
        }
        static void VerifyHighNamespace(string token, SafeFileHandle file, string executable, string working)
        {
            ApprovalIdentity current = ReadApprovalIdentity(GetCurrentProcess());
            if (current == null || current.Integrity != NativeProcessIdentity.High || !current.Elevated || !ValidToken(token))
                throw new UnauthorizedAccessException("A protected high process is required for worker namespace verification.");
            string path = executable.StartsWith(@"\\?\", StringComparison.Ordinal) ? CanonicalDosPath(executable.Substring(4)) : null;
            string system = CanonicalDosPath(working);
            if (path == null || system == null) throw new UnauthorizedAccessException("The worker namespace uses an invalid drive spelling.");
            string expected = @"\Sessions\" + current.Session.ToString(CultureInfo.InvariantCulture) + @"\BaseNamedObjects\TaskbarSelect.AdminApproval.High." + token;
            var leases = new List<IDisposable>();
            try {
                var directory = OpenNamespaceObject(expected, true); leases.Add(directory);
                if (!String.Equals(ObjectName(directory), expected, StringComparison.OrdinalIgnoreCase) ||
                    !ProtectedNamespaceSecurity(ReadSecurity(directory, 6), current.Sid, true)) throw new UnauthorizedAccessException("The inherited worker namespace is not sealed.");
                string fileTarget = null;
                foreach (char drive in path[0] == system[0] ? new[] { path[0] } : new[] { path[0], system[0] }) {
                    var effective = OpenNamespaceObject(@"\??\" + drive + ":", false); leases.Add(effective);
                    var global = OpenNamespaceObject(@"\GLOBAL??\" + drive + ":", false); leases.Add(global);
                    string name = expected + "\\" + drive + ":", target = LinkTarget(effective), globalTarget = LinkTarget(global);
                    if (!String.Equals(ObjectName(effective), name, StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(ObjectName(global), @"\GLOBAL??\" + drive + ":", StringComparison.OrdinalIgnoreCase) ||
                        !ProtectedNamespaceSecurity(ReadSecurity(effective, 6), current.Sid, false) || !PhysicalVolume(target) ||
                        !String.Equals(target, globalTarget, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The inherited worker drive mapping is not immutable and physical.");
                    if (drive == path[0]) fileTarget = target;
                }
                var native = new StringBuilder(32768);
                uint count = GetFinalPathNameByHandle(file, native, (uint)native.Capacity, 2);
                if (count == 0 || count >= native.Capacity || native.Length != count || fileTarget == null ||
                    !native.ToString().StartsWith(fileTarget + "\\", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The worker image volume does not match the sealed global mapping.");
                // Native already checked the actual MED parent token's denied
                // rights before creating this map. Managed code repeats its
                // exact raw ACL/identity contract for the inherited namespace.
                lock (namespaceGate) {
                    if (namespaceLeases == null) { namespaceIdentity = expected; namespaceLeases = leases; leases = null; }
                    else if (!String.Equals(namespaceIdentity, expected, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The worker namespace session changed.");
                }
            } finally { if (leases != null) for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose(); }
        }
        static SafeFileHandle OpenNamespaceObject(string path, bool directory)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 4096) throw new UnauthorizedAccessException();
            IntPtr text = Marshal.StringToHGlobalUni(path), unicode = IntPtr.Zero;
            try {
                var value = new UnicodeString { Length = checked((ushort)(path.Length * 2)), MaximumLength = checked((ushort)((path.Length + 1) * 2)), Buffer = text };
                unicode = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString))); Marshal.StructureToPtr(value, unicode, false);
                var attributes = new ObjectAttributes { Length = Marshal.SizeOf(typeof(ObjectAttributes)), Name = unicode, Attributes = 0x40 };
                IntPtr raw;
                int status = directory ? NtOpenDirectoryObject(out raw, 0x20003, ref attributes) : NtOpenSymbolicLinkObject(out raw, 0x20001, ref attributes);
                if (status < 0 || raw == IntPtr.Zero || raw == new IntPtr(-1)) {
                    if (raw != IntPtr.Zero && raw != new IntPtr(-1)) CloseHandle(raw);
                    throw new UnauthorizedAccessException("The protected worker namespace could not be opened.");
                }
                return new SafeFileHandle(raw, true);
            } finally { if (unicode != IntPtr.Zero) Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
        }
        static string ObjectName(SafeFileHandle handle)
        {
            IntPtr buffer = Marshal.AllocHGlobal(8192);
            try {
                uint required;
                if (NtQueryObject(handle, 1, buffer, 8192, out required) < 0 || required > 8192) throw new UnauthorizedAccessException();
                var value = (UnicodeString)Marshal.PtrToStructure(buffer, typeof(UnicodeString));
                long offset = value.Buffer.ToInt64() - buffer.ToInt64();
                if (value.Length == 0 || (value.Length & 1) != 0 || offset < Marshal.SizeOf(typeof(UnicodeString)) || offset + value.Length > 8192)
                    throw new UnauthorizedAccessException();
                return Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        static string LinkTarget(SafeFileHandle handle)
        {
            IntPtr buffer = Marshal.AllocHGlobal(8192);
            try {
                var value = new UnicodeString { Buffer = buffer, MaximumLength = 8190 };
                uint required;
                if (NtQuerySymbolicLinkObject(handle, ref value, out required) < 0 || value.Buffer != buffer || value.Length == 0 ||
                    (value.Length & 1) != 0 || value.Length > 8190 || required > 8192) throw new UnauthorizedAccessException();
                return Marshal.PtrToStringUni(buffer, value.Length / 2);
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        static bool PhysicalVolume(string target)
        {
            const string prefix = @"\Device\HarddiskVolume";
            if (target == null || !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || target.Length <= prefix.Length) return false;
            for (int index = prefix.Length; index < target.Length; index++) if (target[index] < '0' || target[index] > '9') return false;
            return true;
        }
        internal static bool ProtectedNamespaceSecurity(RawSecurityDescriptor descriptor, string sid, bool directory)
        {
            if (descriptor == null || String.IsNullOrEmpty(sid) || descriptor.Owner == null || descriptor.Owner.Value != sid ||
                descriptor.Group == null || descriptor.Group.Value != Users || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl == null || descriptor.DiscretionaryAcl.Count != 5) return false;
            string[] identities = { "S-1-3-4", sid, sid, LocalSystem, Administrators };
            uint[] masks = { directory ? 0xD000Cu : 0xD0002u, directory ? 0xD000Cu : 0xD0002u,
                directory ? 0x20003u : 0x20001u, directory ? 0xF000Fu : 0xF0001u, directory ? 0xF000Fu : 0xF0001u };
            for (int index = 0; index < identities.Length; index++) {
                var ace = descriptor.DiscretionaryAcl[index] as CommonAce;
                if (ace == null || ace.IsCallback || ace.AceFlags != AceFlags.None || ace.SecurityIdentifier == null || ace.SecurityIdentifier.Value != identities[index] ||
                    ace.AceQualifier != (index < 2 ? AceQualifier.AccessDenied : AceQualifier.AccessAllowed)) return false;
                uint actual = unchecked((uint)ace.AccessMask);
                // Windows can remove unsupported symbolic-link right 2 from
                // the two DENYs. DELETE/WRITE_DAC/WRITE_OWNER remain mandatory;
                // every other ACE and bit still has one exact allowed value.
                if (actual != masks[index] && !(!directory && index < 2 && actual == 0xD0000u)) return false;
            }
            return true;
        }
        internal static void CleanEnvironment(ProcessStartInfo start, string windows, string temp)
        {
            string system = CanonicalDosPath(windows);
            if (start == null || system == null || !InputPath(temp)) throw new ArgumentException("The administrator environment is invalid.");
            start.EnvironmentVariables.Clear();
            start.EnvironmentVariables["SystemRoot"] = system;
            start.EnvironmentVariables["WINDIR"] = system;
            start.EnvironmentVariables["SystemDrive"] = System.IO.Path.GetPathRoot(system).TrimEnd('\\');
            start.EnvironmentVariables["PATH"] = System.IO.Path.Combine(system, "System32");
            start.EnvironmentVariables["TEMP"] = temp;
            start.EnvironmentVariables["TMP"] = temp;
        }
        internal static bool HighToken(NativeProcessIdentity.Info current)
        { return current != null && current.IntegrityAvailable && current.IntegrityLevel == NativeProcessIdentity.High && current.ElevationAvailable && current.Elevated; }
        internal static bool WorkerArguments(string arguments, out string[] values)
        {
            values = null;
            if (String.IsNullOrEmpty(arguments) || arguments.Length > 1024 || arguments.IndexOfAny(new[] { '\r', '\n', '\t', '"', '\0' }) >= 0) return false;
            string[] parsed = arguments.Split(' ');
            if (!WorkerArguments(parsed)) return false;
            values = parsed; return true;
        }
        internal static bool WorkerArguments(string[] args)
        {
            if (args == null || args.Length < 6 || args[0] != "--window-move-worker") return false;
            int op; long window, started, deadline; uint process;
            if (!Decimal(args[1]) || !Int32.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out op) ||
                !Decimal(args[2]) || !Int64.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out window) || window == 0 ||
                !Decimal(args[3]) || !UInt32.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out process) || process == 0 ||
                !Decimal(args[4]) || !Int64.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out started) || started <= 0 ||
                !Decimal(args[5]) || !Int64.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out deadline) || deadline <= 0) return false;
            if ((op == 0 && args.Length != 11) || (op == 1 && args.Length != 16) ||
                (op >= 2 && op <= 5 && args.Length != 6) || (op == 6 && args.Length != 7) || op < 0 || op > 6) return false;
            int[] numbers = new int[args.Length - 6];
            for (int index = 0; index < numbers.Length; index++)
                if (!Decimal(args[index + 6]) || !Int32.TryParse(args[index + 6], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[index])) return false;
            return AdministratorSupport.ValidValues(op, window, process, started, numbers);
        }
        static bool Decimal(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            int start = value[0] == '-' ? 1 : 0;
            if (start == value.Length) return false;
            for (int index = start; index < value.Length; index++) if (value[index] < '0' || value[index] > '9') return false;
            return true;
        }
        internal static bool ValidHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            foreach (char value in hash) if (!(value >= '0' && value <= '9') && !(value >= 'a' && value <= 'f')) return false;
            return true;
        }
        internal static bool ValidToken(string token)
        {
            Guid value;
            return token != null && token.Length == 32 && token == token.ToLowerInvariant() &&
                Guid.TryParseExact(token, "N", out value) && value != Guid.Empty;
        }
        internal static bool ProtectedSecurity(RawSecurityDescriptor security, bool directory)
        {
            // FileSystemSecurity normalizes/merges duplicate allow ACEs. Inspect
            // the original descriptor first; normalization must never turn an
            // unexpected fourth ACE into the three-rule launch contract.
            if (security == null || (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                (security.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || security.DiscretionaryAcl == null || security.DiscretionaryAcl.Count != 3) return false;
            SecurityIdentifier owner = security.Owner;
            if (owner == null || (owner.Value != Administrators && owner.Value != LocalSystem)) return false;
            bool administrators = false, system = false, users = false;
            foreach (GenericAce item in security.DiscretionaryAcl) {
                var rule = item as CommonAce;
                AceFlags inherited = AceFlags.ContainerInherit | AceFlags.ObjectInherit;
                if (rule == null || rule.IsCallback || rule.AceQualifier != AceQualifier.AccessAllowed ||
                    (directory ? rule.AceFlags != inherited : rule.AceFlags != AceFlags.None && rule.AceFlags != inherited)) return false;
                SecurityIdentifier sid = rule.SecurityIdentifier;
                if (sid == null) return false;
                uint rights = unchecked((uint)rule.AccessMask);
                if (sid.Value == Administrators || sid.Value == LocalSystem) {
                    if (rights != (uint)FileSystemRights.FullControl && rights != 0x10000000) return false;
                    if (sid.Value == Administrators) { if (administrators) return false; administrators = true; }
                    else { if (system) return false; system = true; }
                } else if (sid.Value == Users) {
                    if (users || (rights != (uint)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize) && rights != 0xA0000000)) return false;
                    users = true;
                } else return false;
            }
            return administrators && system && users;
        }
        internal static bool SafeInformation(FileInformation information, bool directory)
        {
            return (information.Attributes & ReparseAttribute) == 0 && ((information.Attributes & DirectoryAttribute) != 0) == directory &&
                information.Links == 1 && (information.IndexHigh != 0 || information.IndexLow != 0);
        }
        static SafeFileHandle OpenHandle(string path, bool directory)
        {
            if (!InputPath(path)) throw new IOException("An administrator file path is invalid.");
            // Files disallow write/delete sharing. Directories disallow delete
            // sharing, pinning each ancestor while children are opened/created.
            SafeFileHandle handle = CreateFile(path, GenericRead, directory ? 3u : 1u, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            if (handle == null || handle.IsInvalid) { if (handle != null) handle.Dispose(); throw new IOException("An administrator file could not be pinned."); }
            try {
                FileInformation information;
                if (!GetFileInformationByHandle(handle, out information) || !SafeInformation(information, directory) || !InputPath(FinalPath(handle)))
                    throw new IOException("An administrator file identity is unsafe.");
                return handle;
            } catch { handle.Dispose(); throw; }
        }
        static SafeFileHandle OpenDirectory(string path) { return OpenHandle(path, true); }
        internal static FileStream OpenFile(string path) { return new FileStream(OpenHandle(path, false), FileAccess.Read); }
        static bool Security(SafeFileHandle handle, bool directory)
        { return ProtectedSecurity(ReadSecurity(handle, 1), directory); }
        static RawSecurityDescriptor ReadSecurity(SafeFileHandle handle, int kind)
        {
            IntPtr owner, group, dacl, sacl, descriptor;
            if (GetSecurityInfo(handle, kind, kind == 6 ? 7u : 5u, out owner, out group, out dacl, out sacl, out descriptor) != 0 || descriptor == IntPtr.Zero)
                throw new UnauthorizedAccessException("The administrator cache security could not be read.");
            try {
                uint length = GetSecurityDescriptorLength(descriptor);
                if (length < 20 || length > 65536) throw new UnauthorizedAccessException();
                byte[] bytes = new byte[(int)length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                return new RawSecurityDescriptor(bytes, 0);
            } finally { LocalFree(descriptor); }
        }
        static string[] DirectoryNames(string folder, int maximum)
        {
            var names = new List<string>(); FindData data;
            IntPtr find = FindFirstFile(folder.TrimEnd('\\') + "\\*", out data);
            if (find == new IntPtr(-1)) { if (Marshal.GetLastWin32Error() == 2) return names.ToArray(); throw new IOException("The administrator directory could not be inspected."); }
            try {
                do {
                    if (data.Name == "." || data.Name == "..") continue;
                    if (String.IsNullOrEmpty(data.Name) || data.Name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || names.Count >= maximum) throw new IOException("The administrator directory contains unexpected entries.");
                    names.Add(data.Name);
                } while (FindNextFile(find, out data));
                if (Marshal.GetLastWin32Error() != 18) throw new IOException("The administrator directory changed during inspection.");
                return names.ToArray();
            } finally { FindClose(find); }
        }
        static string FinalPath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1); // VOLUME_NAME_GUID
            if (length == 0 || length >= buffer.Capacity || buffer.Length != length) throw new IOException("An administrator file could not be resolved.");
            return buffer.ToString();
        }
        static bool SameFile(SafeFileHandle left, SafeFileHandle right)
        {
            FileInformation first, second;
            return GetFileInformationByHandle(left, out first) && GetFileInformationByHandle(right, out second) &&
                SafeInformation(first, false) && SafeInformation(second, false) && first.Volume == second.Volume &&
                first.IndexHigh == second.IndexHigh && first.IndexLow == second.IndexLow &&
                String.Equals(FinalPath(left), FinalPath(right), StringComparison.OrdinalIgnoreCase);
        }
        internal static bool ExactConfiguration(Stream stream)
        {
            if (stream == null || !stream.CanSeek || stream.Length != configuration.Length) return false;
            stream.Position = 0;
            foreach (byte value in configuration) if (stream.ReadByte() != value) return false;
            return stream.ReadByte() == -1;
        }
        internal static string Hash(Stream stream)
        {
            stream.Position = 0;
            using (var digest = SHA256.Create()) return Hex(digest.ComputeHash(stream));
        }
        static string Hash(byte[] bytes) { using (var digest = SHA256.Create()) return Hex(digest.ComputeHash(bytes)); }
        static string Hex(byte[] bytes) { var output = new StringBuilder(bytes.Length * 2); foreach (byte value in bytes) output.Append(value.ToString("x2", CultureInfo.InvariantCulture)); return output.ToString(); }
        static bool LocalNtfs(string path)
        {
            try { var drive = new DriveInfo(System.IO.Path.GetPathRoot(path)); return drive.DriveType == DriveType.Fixed && String.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }
        internal static string CanonicalDosPath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length >= 260 || path.Length < 3 || !Char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' || !SafeSegments(path.Substring(3))) return null;
            try { string full = System.IO.Path.GetFullPath(path); return String.Equals(full, path, StringComparison.OrdinalIgnoreCase) ? full.TrimEnd('\\') + (full.Length == 3 ? "\\" : "") : null; }
            catch (Exception) { return null; }
        }
        internal static bool InputPath(string path)
        {
            if (CanonicalDosPath(path) != null) return true;
            const string prefix = "\\\\?\\Volume{";
            if (String.IsNullOrEmpty(path) || path.Length >= 1024 || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            int closing = path.IndexOf('}', prefix.Length);
            Guid volume;
            return closing == prefix.Length + 36 && closing + 1 < path.Length && path[closing + 1] == '\\' &&
                Guid.TryParseExact(path.Substring(prefix.Length, 36), "D", out volume) && volume != Guid.Empty && SafeSegments(path.Substring(closing + 2));
        }
        static bool SafeSegments(string remainder)
        {
            if (remainder.IndexOfAny(new[] { '/', ':', '*', '?', '"', '<', '>', '|', '\0', '\r', '\n' }) >= 0) return false;
            if (remainder.Length == 0) return true;
            string[] segments = remainder.Split('\\');
            for (int index = 0; index < segments.Length; index++) {
                string part = segments[index];
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)) return false;
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9')) return false;
            }
            return true;
        }
    }
}
