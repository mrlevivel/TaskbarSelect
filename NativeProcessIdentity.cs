using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TaskbarSelect
{
    // Only limited metadata access. No process VM/module access, token writes,
    // privilege adjustment, impersonation, elevation or desktop/window calls.
    internal static class NativeProcessIdentity
    {
        internal const uint Untrusted = 0, Low = 0x1000, Medium = 0x2000, MediumPlus = 0x2100, High = 0x3000, System = 0x4000;
        const uint QueryLimitedInformation = 0x1000, TokenQuery = 8;
        const int TokenElevation = 20, TokenIntegrityLevel = 25, MaximumTokenBytes = 4096, MaximumPathCharacters = 32768;
        const int InvalidData = 13;

        internal sealed class Info
        {
            internal uint Process;
            internal long Started;
            internal uint IntegrityLevel;
            internal bool IntegrityAvailable, Elevated, ElevationAvailable;
            internal int Error;
            internal Info Copy() { return (Info)MemberwiseClone(); }
        }
        sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public KernelHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct FileTime { internal uint Low, High; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern KernelHandle OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(KernelHandle process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(KernelHandle process, uint flags, StringBuilder name, ref int length);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(KernelHandle process, uint access, out KernelHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(KernelHandle token, int kind, IntPtr information, int length, out int required);

        static readonly Info self = Read(GetCurrentProcessId());
        internal static Info Current { get { return self.Copy(); } }

        internal static long CreationTicks(FileTime creation)
        {
            ulong raw = ((ulong)creation.High << 32) | creation.Low;
            if (raw == 0 || raw > Int64.MaxValue) return 0;
            try { return DateTime.FromFileTimeUtc((long)raw).Ticks; }
            catch (ArgumentOutOfRangeException) { return 0; }
        }
        static long Started(KernelHandle process)
        {
            FileTime creation, exit, kernel, user;
            return GetProcessTimes(process, out creation, out exit, out kernel, out user) ? CreationTicks(creation) : 0;
        }
        internal static long Started(uint process)
        {
            if (process == 0) return 0;
            using (var handle = OpenProcess(QueryLimitedInformation, false, process))
                return handle.IsInvalid ? 0 : Started(handle);
        }
        internal static string Path(uint process)
        {
            if (process == 0) return null;
            using (var handle = OpenProcess(QueryLimitedInformation, false, process)) {
                if (handle.IsInvalid) return null;
                var name = new StringBuilder(MaximumPathCharacters);
                int length = name.Capacity;
                if (!QueryFullProcessImageName(handle, 0, name, ref length) || length <= 0 || length >= MaximumPathCharacters || name.Length != length)
                    return null;
                return name.ToString();
            }
        }
        internal static Info Read(uint process)
        {
            var result = new Info { Process = process };
            if (process == 0) { result.Error = 87; return result; }
            using (var handle = OpenProcess(QueryLimitedInformation, false, process)) {
                if (handle.IsInvalid) { result.Error = Marshal.GetLastWin32Error(); return result; }
                result.Started = Started(handle);
                if (result.Started == 0) { result.Error = Marshal.GetLastWin32Error(); if (result.Error == 0) result.Error = InvalidData; return result; }
                KernelHandle token;
                if (!OpenProcessToken(handle, TokenQuery, out token)) {
                    result.Error = Marshal.GetLastWin32Error(); if (token != null) token.Dispose(); return result;
                }
                using (token) {
                    int error;
                    uint integrity;
                    result.IntegrityAvailable = ReadIntegrity(token, out integrity, out error);
                    result.IntegrityLevel = integrity;
                    if (!result.IntegrityAvailable) result.Error = error;
                    bool elevated;
                    result.ElevationAvailable = ReadElevation(token, out elevated, out error);
                    result.Elevated = elevated;
                    if (!result.ElevationAvailable && result.Error == 0) result.Error = error;
                }
            }
            return result;
        }
        static bool ReadElevation(KernelHandle token, out bool elevated, out int error)
        {
            elevated = false; error = 0;
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try {
                int size;
                if (!GetTokenInformation(token, TokenElevation, buffer, 4, out size)) { error = Marshal.GetLastWin32Error(); return false; }
                int value = Marshal.ReadInt32(buffer);
                if (size != 4 || value < 0 || value > 1) { error = InvalidData; return false; }
                elevated = value != 0; return true;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        static bool ReadIntegrity(KernelHandle token, out uint integrity, out int error)
        {
            integrity = 0; error = 0;
            int required;
            bool sized = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out required);
            int sizingError = Marshal.GetLastWin32Error();
            if (sized || sizingError != 122 || required < IntPtr.Size + 4 || required > MaximumTokenBytes) {
                error = sizingError == 0 || sized ? InvalidData : sizingError; return false;
            }
            IntPtr buffer = Marshal.AllocHGlobal(required);
            try {
                int returned;
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, required, out returned)) { error = Marshal.GetLastWin32Error(); return false; }
                if (returned < IntPtr.Size + 4 || returned > required) { error = InvalidData; return false; }
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                long offset = sid.ToInt64() - buffer.ToInt64();
                if (offset < IntPtr.Size + 4 || offset > returned - 12 || Marshal.ReadByte(sid) != 1) { error = InvalidData; return false; }
                int count = Marshal.ReadByte(sid, 1);
                if (count < 1 || count > 15 || offset + 8 + count * 4 > returned) { error = InvalidData; return false; }
                // SECURITY_MANDATORY_LABEL_AUTHORITY, represented big endian.
                for (int index = 2; index < 7; index++) if (Marshal.ReadByte(sid, index) != 0) { error = InvalidData; return false; }
                if (Marshal.ReadByte(sid, 7) != 16) { error = InvalidData; return false; }
                integrity = unchecked((uint)Marshal.ReadInt32(sid, 8 + (count - 1) * 4));
                return true;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        internal static bool RequiresAdministrator(uint currentIntegrity, uint targetIntegrity)
        { return targetIntegrity > currentIntegrity; }
        internal static bool RequiresAdministrator(Info current, Info target)
        {
            return current != null && target != null && current.IntegrityAvailable && target.IntegrityAvailable &&
                RequiresAdministrator(current.IntegrityLevel, target.IntegrityLevel);
        }
        internal static bool RequiresAdministrator(uint process)
        { return process != 0 && RequiresAdministrator(self, Read(process)); }
    }
}
