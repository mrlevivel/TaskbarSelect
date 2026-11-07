using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

// Disposable hosted-runner fixture only. This source is not compiled into or
// shipped with Taskbar Select. It never modifies a token, answers a UAC prompt,
// changes security policy, or targets any window except its own HIGH HWND.
// Movement cases keep it hidden; the approval-route state-action phase briefly
// shows and focuses that same owned Form without bypassing foreground policy.
// Real runas is tested only under the runner's existing automatic consent.
internal static class AdministratorBoundaryFixture
{
    const uint QueryToken = 8, Medium = 0x2000, High = 0x3000;
    const int BlockedExit = 3;
    const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags HiddenInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly string[] Cases = { "move-placement-undo", "absent-boot-grant", "absent-resume", "absent-authorization", "bad-sequence", "late-authorization", "parent-death", "unresponsive-owned-target", "active-worker-parent-death", "approval-host-route" };
    static int checks;
    static string evidence, app;
    static Assembly product;
    static Type launch;
    static string currentScenario = "token-preflight";
    static string currentPhase = "entry", failureMethod;
    static int? failureWindowsError;
    static int? approvalHostExitCode;
    static int? diagnosticNativeExitCode, diagnosticPayloadExitCode;
    static Lifetime diagnosticNative, diagnosticPayload, diagnosticHost;
    static List<object> completedCases;
    static string ownedCacheHash, ownedCacheRoot;
    static string ciSourceSha;
    static Func<int, string, IntPtr, AdministratorCiLaunchProbe.CaseIdentity> ciSpawner;

    sealed class Blocked : Exception { internal Blocked(string reason) : base(reason) { } }
    sealed class AssertionFailure : Exception { internal AssertionFailure(string reason) : base(reason) { } }
    sealed class TokenView { internal string Sid; internal uint Integrity, Session; internal bool Elevated; }
    sealed class Packet { internal int Kind, Sequence; internal long[] Values; }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfo
    {
        internal int Size; internal IntPtr Reserved, Desktop, Title;
        internal int X, Y, Width, Height, XCount, YCount, Fill, Flags;
        internal short Show, ReservedBytes; internal IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Placement
    { internal int Length, Flags, Show; internal Point Minimum, Maximum; internal Rect Normal; }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr data, int size, out int returned);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessWithTokenW(IntPtr token, uint logon,
        string application, StringBuilder arguments, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)] static extern SafeWaitHandle OpenLifetime(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeWaitHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exit);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exit);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessW(string application, StringBuilder command,
        IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObjectW(IntPtr security, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int kind, IntPtr data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool assigned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(IntPtr job, int kind, IntPtr data, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool RemoveDirectoryW(string path);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [STAThread] static int Main(string[] args)
    {
        try {
            // Read-only managed contract preflight, before any hosted/native
            // entry. Pin the selected candidate against replacement while its
            // actual validator is inspected; never invoke its application Main.
            if (args.Length == 2 && args[0] == "--check-protocol") {
                string candidate = Path.GetFullPath(args[1]);
                using (var held = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    int protocolChecks = RunProtocolChecks(Assembly.LoadFrom(candidate));
                    Console.WriteLine("PASS: " + protocolChecks + " actual managed boundary Focus contract checks; no native or hosted fixture entry executed.");
                }
                return 0;
            }
            int boundaryResult;
            if (AdministratorCiLaunchProbe.TryBoundaryEntry(args, RunCiCases, out boundaryResult)) return boundaryResult;
            HostedOnly();
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            if (args.Length == 3 && args[0] == "--run") { app = args[1]; evidence = args[2]; return Orchestrate(); }
            if (args.Length == 8 && args[0] == "--medium") { app = args[1]; evidence = args[2]; return Parent(args); }
            throw new InvalidOperationException("Invalid fixed fixture entry arguments.");
        } catch (Blocked error) {
            Console.Error.WriteLine("BLOCKED: " + error.Message);
            if (evidence != null) Save("blocked", error.Message, null);
            return BlockedExit;
        } catch (Exception error) {
            string reason = FailureReason(error);
            Console.Error.WriteLine("FAILED: " + reason);
            if (evidence != null && ((args.Length > 0 && args[0] == "--run") || ciSourceSha != null)) Save("failed", reason, null);
            return 1;
        }
    }
    static void HostedOnly()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("RUNNER_OS") != "Windows" || !Environment.Is64BitProcess)
            throw new Blocked("Requires a disposable 64-bit GitHub-hosted Windows runner.");
    }
    static void Check(bool condition, string label)
    { if (!condition) throw new AssertionFailure(label); checks++; Console.WriteLine("PASS: " + label); }
    static string FailureReason(Exception error)
    {
        string method = error.TargetSite == null ? null : error.TargetSite.Name;
        failureMethod = method != null && method.Length > 0 && method.Length <= 64 &&
            method.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_') ? method : "unavailable";
        var native = error as Win32Exception; failureWindowsError = native == null ? (int?)null : native.NativeErrorCode;
        string reason = error is AssertionFailure ? error.Message : error.GetType().Name + " hr=0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture);
        return reason + " phase=" + currentPhase + " method=" + failureMethod +
            (approvalHostExitCode.HasValue ? " approvalHostExit=" + approvalHostExitCode.Value.ToString(CultureInfo.InvariantCulture) : "") +
            (diagnosticNativeExitCode.HasValue ? " nativeExit=" + diagnosticNativeExitCode.Value.ToString(CultureInfo.InvariantCulture) : "") +
            (diagnosticPayloadExitCode.HasValue ? " payloadExit=" + diagnosticPayloadExitCode.Value.ToString(CultureInfo.InvariantCulture) : "");
    }
    static void Save(string status, string reason, object cases)
    {
        Directory.CreateDirectory(evidence);
        var data = new Dictionary<string, object> {
            { "status", status }, { "reason", reason }, { "checks", checks }, { "sourceSha", ciSourceSha ?? Environment.GetEnvironmentVariable("GITHUB_SHA") },
            { "appSha256", FileHash(app) }, { "version", FileVersionInfo.GetVersionInfo(app).ProductVersion }, { "cases", cases ?? completedCases },
            { "scenario", currentScenario },
            { "phase", currentPhase }, { "failureMethod", failureMethod }, { "failureWindowsError", failureWindowsError },
            { "approvalHostExitCode", approvalHostExitCode },
            { "scope", "Nine direct HIGH protocol cases plus the real MED approval-host/private-map/runas route; genuine same-SID/session tokens, hidden owned movement HWND, then briefly foreground-owned HIGH state-action HWND" },
            { "untested", new[] { "In-app Enable button/UI", "Interactive UAC prompt/cancellation", "Different-account UAC rejection", "Unresponsive foreign applications", "Production controller recovery persistence (covered by other suites)", "Direct inspection of the managed payload environment", "Instrumented exact USER entry inside a timed-out worker" } }
        };
        File.WriteAllText(Path.Combine(evidence, "result.json"), new JavaScriptSerializer().Serialize(data), new UTF8Encoding(false));
    }
    static string FileHash(string path)
    { using (var sha = System.Security.Cryptography.SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    static TokenView ReadToken(IntPtr token)
    {
        int length; IntPtr data = Marshal.AllocHGlobal(4096);
        try {
            Check(GetTokenInformation(token, 25, data, 4096, out length) && length >= IntPtr.Size && length <= 4096, "Token integrity is genuinely readable");
            IntPtr sid = Marshal.ReadIntPtr(data); var integritySid = new SecurityIdentifier(sid);
            string text = integritySid.Value; uint integrity = UInt32.Parse(text.Substring(text.LastIndexOf('-') + 1), CultureInfo.InvariantCulture);
            Check(GetTokenInformation(token, 20, data, 4, out length) && length == 4, "Token elevation is genuinely readable");
            bool elevated = Marshal.ReadInt32(data) != 0;
            Check(GetTokenInformation(token, 12, data, 4, out length) && length == 4, "Token session is genuinely readable");
            uint session = unchecked((uint)Marshal.ReadInt32(data));
            Check(GetTokenInformation(token, 8, data, 4, out length) && length == 4 && Marshal.ReadInt32(data) == 1, "Actual process token is a primary token");
            string user; using (var identity = new WindowsIdentity(token)) user = identity.User.Value;
            return new TokenView { Sid = user, Integrity = integrity, Elevated = elevated, Session = session };
        } finally { Marshal.FreeHGlobal(data); }
    }
    static TokenView Current()
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), QueryToken, out token)) throw new Blocked("Current token cannot be queried.");
        try { return ReadToken(token); } finally { CloseHandle(token); }
    }
    static TokenView Peer(uint pid)
    {
        IntPtr process = OpenProcess(0x1000, false, pid), token = IntPtr.Zero;
        try { if (process == IntPtr.Zero || !OpenProcessToken(process, QueryToken, out token)) throw new InvalidOperationException("Peer token unavailable."); return ReadToken(token); }
        finally { if (token != IntPtr.Zero) CloseHandle(token); if (process != IntPtr.Zero) CloseHandle(process); }
    }
    static void SameUser(TokenView expected, TokenView actual, uint integrity, bool elevated)
    { Check(expected.Sid == actual.Sid && expected.Session == actual.Session && actual.Integrity == integrity && actual.Elevated == elevated, "Actual peer SID/session/integrity/elevation match the fixed boundary"); }
    sealed class Lifetime : IDisposable
    {
        readonly SafeWaitHandle handle;
        internal readonly uint Id;
        internal readonly long Started;
        Lifetime(SafeWaitHandle value, uint pid, long started) { handle = value; Id = pid; Started = started; }
        internal static Lifetime Open(uint pid, long expected = 0)
        {
            if (pid == 0 || expected < 0) throw new InvalidOperationException("Invalid owned process identity.");
            var value = OpenLifetime(0x101000, false, pid);
            if (value == null || value.IsInvalid) { int error = Marshal.GetLastWin32Error(); if (value != null) value.Dispose(); throw new Win32Exception(error); }
            try {
                long created, exited, kernel, user;
                if (!GetProcessTimes(value, out created, out exited, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
                long started = DateTime.FromFileTimeUtc(created).Ticks;
                if (started <= 0 || (expected != 0 && expected != started)) throw new InvalidOperationException("Stale owned process identity.");
                return new Lifetime(value, pid, started);
            } catch { value.Dispose(); throw; }
        }
        internal bool HasExited { get { return WaitForExit(0); } }
        internal bool WaitForExit(int milliseconds)
        {
            uint result = WaitForSingleObject(handle, (uint)Math.Max(0, milliseconds));
            if (result == 0) return true; if (result == 258) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        internal int ExitCode { get { if (!HasExited) throw new InvalidOperationException("The owned process is still running."); uint code;
            if (!GetExitCodeProcess(handle, out code)) throw new Win32Exception(Marshal.GetLastWin32Error()); return unchecked((int)code); } }
        public void Dispose() { handle.Dispose(); }
    }
    static long Started(Process process) { using (var lifetime = Lifetime.Open((uint)process.Id)) return lifetime.Started; }
    static long Started(Lifetime process) { return process.Started; }
    sealed class ConnectionWait
    {
        Exception error; internal volatile bool Complete;
        internal ConnectionWait(NamedPipeServerStream server)
        {
            server.BeginWaitForConnection(delegate(IAsyncResult result) {
                try { server.EndWaitForConnection(result); } catch (Exception failure) { error = failure; }
                finally { try { result.AsyncWaitHandle.Close(); } catch (Exception failure) { if (error == null) error = failure; } Complete = true; }
            }, null);
        }
        internal void End() { if (!Complete) throw new InvalidOperationException(); if (error != null) throw error; }
    }
    sealed class ReadWait
    {
        Exception error; internal volatile bool Complete; int received;
        internal ReadWait(PipeStream pipe, byte[] bytes, int offset, int count)
        {
            pipe.BeginRead(bytes, offset, count, delegate(IAsyncResult result) {
                try { received = pipe.EndRead(result); } catch (Exception failure) { error = failure; }
                finally { try { result.AsyncWaitHandle.Close(); } catch (Exception failure) { if (error == null) error = failure; } Complete = true; }
            }, null);
        }
        internal int End() { if (!Complete) throw new InvalidOperationException(); if (error != null) throw error; return received; }
    }
    static bool Wait(Func<bool> completed, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!completed()) { if (watch.ElapsedMilliseconds >= milliseconds) return completed(); Thread.Sleep(2); }
        return true;
    }
    static NamedPipeServerStream Server(string name, string sid)
    {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512, security);
    }
    static void Connect(NamedPipeServerStream server, Process child)
    { using (var lifetime = Lifetime.Open((uint)child.Id, Started(child))) Connect(server, lifetime); }
    static void Connect(NamedPipeServerStream server, Lifetime child)
    {
        var waiting = new ConnectionWait(server);
        if (!Wait(delegate { return waiting.Complete || child.HasExited; }, 8000) || !waiting.Complete) { server.Dispose(); throw new IOException("Owned pipe connection deadline or process exit."); }
        waiting.End(); uint pid;
        Check(!child.HasExited && GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out pid) && pid == child.Id, "Pipe client is the exact owned process");
    }
    static void Write(PipeStream pipe, int kind, int sequence, params long[] values)
    {
        Check(sequence > 0 && values.Length <= 15, "Fixture emits a bounded numeric-only packet");
        var memory = new MemoryStream(); var writer = new BinaryWriter(memory);
        writer.Write(8 + values.Length * 8); writer.Write(kind); writer.Write(sequence); foreach (long value in values) writer.Write(value);
        byte[] bytes = memory.ToArray(); pipe.Write(bytes, 0, bytes.Length); pipe.Flush(); writer.Dispose(); memory.Dispose();
    }
    static byte[] Exact(PipeStream pipe, int count, int milliseconds)
    {
        byte[] bytes = new byte[count]; int offset = 0; var watch = Stopwatch.StartNew();
        while (offset < count) {
            var read = new ReadWait(pipe, bytes, offset, count - offset);
            if (!Wait(delegate { return read.Complete; }, Math.Max(1, milliseconds - (int)watch.ElapsedMilliseconds))) { pipe.Dispose(); throw new IOException("Owned pipe read deadline."); }
            int received = read.End(); if (received <= 0) throw new EndOfStreamException(); offset += received;
            if (watch.ElapsedMilliseconds > milliseconds) throw new IOException("Owned packet read deadline.");
        }
        return bytes;
    }
    static Packet Read(PipeStream pipe, int milliseconds = 8000)
    {
        int size = BitConverter.ToInt32(Exact(pipe, 4, milliseconds), 0);
        Check(size >= 8 && size <= 128 && (size - 8) % 8 == 0, "Incoming packet is capped and numerically framed");
        byte[] bytes = Exact(pipe, size, milliseconds); var values = new long[(size - 8) / 8];
        for (int i = 0; i < values.Length; i++) values[i] = BitConverter.ToInt64(bytes, 8 + i * 8);
        return new Packet { Kind = BitConverter.ToInt32(bytes, 0), Sequence = BitConverter.ToInt32(bytes, 4), Values = values };
    }
    static void Product()
    {
        product = Assembly.LoadFrom(app); launch = product.GetType("TaskbarSelect.AdministratorLaunch", true);
        Check(RunProtocolChecks(product) == 4, "Fixture Focus packets match the actual product contract before staging any administrator payload");
    }
    static long[] PausedFocusConfiguration()
    { return new long[] { 0, 0, 1, 0, -1, 0, 1 }; }
    internal static int RunProtocolChecks(Assembly selectedProduct)
    {
        Type support = selectedProduct.GetType("TaskbarSelect.AdministratorSupport", true);
        MethodInfo valid = support.GetMethod("ValidFocus", HiddenStatic, null, new[] { typeof(long[]) }, null);
        if (valid == null) throw new InvalidOperationException("The product Focus contract is unavailable.");
        Func<long[], bool> accepts = delegate(long[] values) { return (bool)valid.Invoke(null, new object[] { values }); };
        long[] paused = PausedFocusConfiguration();
        if (paused.Length != 7 || paused[2] != 1 || paused[6] != 1 || !accepts(paused))
            throw new InvalidOperationException("The fixture paused Focus packet does not match the actual product contract.");
        if (accepts(paused.Take(6).ToArray())) throw new InvalidOperationException("The product admitted the obsolete six-field Focus packet.");
        long[] invalid = PausedFocusConfiguration(); invalid[6] = 2;
        if (accepts(invalid)) throw new InvalidOperationException("The product admitted a non-Boolean pin pause field.");
        if (accepts(paused.Concat(new long[] { 1 }).ToArray())) throw new InvalidOperationException("The product admitted an unsupported trailing Focus field.");
        return 4;
    }
    static object Stage(string profile) { return launch.GetMethod("Stage", HiddenStatic).Invoke(null, new object[] { profile }); }
    static string ImageString(object image, string name) { return (string)image.GetType().GetProperty(name, HiddenInstance).GetValue(image, null); }
    static string PayloadPath(object image, string nonce) { return (string)image.GetType().GetMethod("PayloadPath", HiddenInstance).Invoke(image, new object[] { nonce }); }
    static bool VerifyBootstrap(object image, Process process)
    { return VerifyBootstrapIdentity(image, (uint)process.Id, Started(process)); }
    static bool VerifyBootstrapIdentity(object image, uint process, long started)
    { return (bool)image.GetType().GetMethod("VerifyBootstrapProcess", HiddenInstance, null, new[] { typeof(uint), typeof(long) }, null).Invoke(image, new object[] { process, started }); }
    static bool VerifyBootstrap(object image, Lifetime process) { return VerifyBootstrapIdentity(image, process.Id, process.Started); }
    static string HeldDosPath(FileStream held)
    {
        string dos = (string)launch.GetMethod("ExtendedDosPath", HiddenStatic).Invoke(null, new object[] { held.SafeFileHandle });
        using (var actual = (FileStream)launch.GetMethod("OpenFile", HiddenStatic).Invoke(null, new object[] { dos.Substring(4) }))
            Check((bool)launch.GetMethod("SameFile", HiddenStatic).Invoke(null, new object[] { held.SafeFileHandle, actual.SafeFileHandle }),
                "API-derived DOS spelling names the exact held physical file");
        return dos;
    }
    static Process StartPinnedNative(object image, ProcessStartInfo start, OuterJob outer)
    {
        var held = (FileStream)image.GetType().GetField("image", HiddenInstance).GetValue(image);
        string dos = HeldDosPath(held);
        var environment = new StringBuilder();
        foreach (string name in start.EnvironmentVariables.Keys.Cast<string>().OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            environment.Append(name).Append('=').Append(start.EnvironmentVariables[name]).Append('\0');
        environment.Append('\0');
        IntPtr block = Marshal.StringToHGlobalUni(environment.ToString());
        var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)) }; ProcessInfo created = new ProcessInfo();
        Process process = null; bool resumed = false;
        try {
            if (!CreateProcessW(dos, new StringBuilder(Quote(dos) + " " + start.Arguments), IntPtr.Zero, IntPtr.Zero, false,
                0x08000404, block, Environment.SystemDirectory, ref startup, out created)) throw new Win32Exception(Marshal.GetLastWin32Error());
            outer.Assign(created.Process);
            using (var lifetime = Lifetime.Open(created.ProcessId)) {
                Check(VerifyBootstrapIdentity(image, lifetime.Id, lifetime.Started), "Suspended direct native image matches the retained Stage file before any code executes");
                SameUser(Current(), Peer(lifetime.Id), High, true);
                process = Process.GetProcessById((int)lifetime.Id);
                Check(process.Handle != IntPtr.Zero && Started(process) == lifetime.Started, "Exact owned HIGH direct process handle/PID/start remains retained before resume");
                if (ResumeThread(created.Thread) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
                resumed = true; return process;
            }
        } finally {
            Marshal.FreeHGlobal(block);
            if (!resumed && created.Process != IntPtr.Zero) {
                // Only our exact CREATE_SUSPENDED handle may be terminated.
                // Keep Stage leases and containment alive until exit is proved.
                TerminateProcess(created.Process, 112);
                using (var completion = new SafeWaitHandle(created.Process, false)) while (WaitForSingleObject(completion, 1000) != 0) { }
                if (process != null) process.Dispose();
            }
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
        }
    }
    static bool VerifyPayload(string path, string hash, string nonce)
    { return (bool)launch.GetMethod("VerifyPayload", HiddenStatic, null, new[] { typeof(string), typeof(string), typeof(string) }, null).Invoke(null, new object[] { path, hash, nonce }); }
    static string VerifiedPhysicalPayload(uint process, string hash, string nonce)
    {
        IntPtr handle = OpenProcess(0x1000, false, process);
        try {
            var path = new StringBuilder(4096); uint size = 4096;
            Check(handle != IntPtr.Zero && QueryFullProcessImageNameW(handle, 0, path, ref size) && size > 0 && size < 4096,
                "Actual HIGH payload executable path is independently read from the pinned process");
            object[] values = { path.ToString(), hash, nonce, null };
            bool valid = (bool)launch.GetMethod("VerifyPayload", HiddenStatic, null, new[] { typeof(string), typeof(string), typeof(string), typeof(string).MakeByRefType() }, null).Invoke(null, values);
            Check(valid && values[3] is string, "Actual HIGH process image passes the app's protected-file identity and exact payload/config verification");
            return (string)values[3];
        } finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }
    static string Quote(string text) { if (text.IndexOf('"') >= 0 || text.IndexOf('\0') >= 0) throw new InvalidOperationException(); return "\"" + text + "\""; }
    static string PhysicalParent(string path) { int at = path.LastIndexOf('\\'); if (at <= 0) throw new InvalidOperationException(); return path.Substring(0, at); }
    static bool Missing(string path)
    { uint attributes = GetFileAttributesW(path); int error = Marshal.GetLastWin32Error(); return attributes == UInt32.MaxValue && (error == 2 || error == 3); }
    static void RequireNonInteractiveConsent()
    {
        // Read existing hosted-machine policy only. Never change UAC or attempt
        // to answer a prompt in unattended CI. Genuine linked MED is separately
        // required above; automatic consent does not stand in for token proof.
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var policy = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", false)) {
            object consent = policy == null ? null : policy.GetValue("ConsentPromptBehaviorAdmin", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (!(consent is int) || (int)consent != 0)
                throw new Blocked("The hosted runner's existing administrator consent policy would require an interactive prompt; it will not be changed or automated.");
            Check(true, "Existing hosted UAC policy permits the real runas route without an interactive prompt or policy change");
        }
    }
    static int Orchestrate()
    {
        TokenView high = Current();
        if (!high.Elevated || high.Integrity != High) throw new Blocked("The runner has no genuine HIGH user token; no UAC will be requested.");
        IntPtr current, linked = IntPtr.Zero;
        if (!OpenProcessToken(GetCurrentProcess(), QueryToken, out current)) throw new Blocked("Current linked token cannot be queried.");
        try {
            IntPtr data = Marshal.AllocHGlobal(IntPtr.Size);
            try { int length; if (!GetTokenInformation(current, 19, data, IntPtr.Size, out length) || length != IntPtr.Size) throw new Blocked("No genuine linked medium token is available on this hosted runner."); linked = Marshal.ReadIntPtr(data); }
            finally { Marshal.FreeHGlobal(data); }
            TokenView medium = ReadToken(linked);
            if (medium.Elevated || medium.Integrity != Medium || medium.Sid != high.Sid || medium.Session != high.Session) throw new Blocked("The linked token is not the same-SID/session non-elevated MED primary token.");
            Product();
            var results = new List<object>(); completedCases = results;
            using (var target = new OwnTarget()) {
                foreach (string scenario in Cases) { currentScenario = scenario; results.Add(RunCase(linked, high, target, scenario)); }
            }
            Check(ownedCacheHash != null && ownedCacheRoot != null, "Cleanup paths came only from the authenticated app's fixed payload path");
            Check(RemoveDirectoryW(ownedCacheHash), "Only the created, now-empty protected payload hash directory is removed");
            Check(RemoveDirectoryW(ownedCacheRoot) && Missing(ownedCacheRoot), "Only the created, now-empty protected fixture cache root is removed");
            currentPhase = "completed"; Save("passed", "", results); return 0;
        } finally { if (linked != IntPtr.Zero) CloseHandle(linked); CloseHandle(current); }
    }
    // Called only by the authenticated disposable CI sponsor chain. This does
    // not relax HostedOnly for the original orchestrator or ordinary MED entry.
    internal static int RunCiCases(PipeStream sponsorControl, string appPath, string runtimePath,
        uint sponsorPid, long sponsorStarted, string sourceSha)
    {
        if (sourceSha == null || sourceSha.Length != 40 || !sourceSha.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
            throw new InvalidOperationException("Invalid authenticated CI source metadata.");
        app = appPath; evidence = runtimePath; ciSourceSha = sourceSha;
        TextWriter previousOut = Console.Out, previousError = Console.Error;
        try {
            using (var output = new StreamWriter(new FileStream(Path.Combine(evidence, "stdout.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
            using (var errors = new StreamWriter(new FileStream(Path.Combine(evidence, "stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))) {
                output.AutoFlush = true; errors.AutoFlush = true;
                Console.SetOut(output); Console.SetError(errors);
                try {
                    currentPhase = "ci-admission";
                    SetProcessDpiAwarenessContext(new IntPtr(-4));
                    Check(Cases.SequenceEqual(AdministratorCiLaunchProbe.BoundaryScenarios), "Authenticated sponsor retains the exact ten boundary scenarios and order");
                    TokenView high = Current();
                    Check(high.Elevated && high.Integrity == High, "Actual CI boundary orchestrator is genuinely HIGH with a PRIMARY process token");
                    using (Lifetime sponsor = Lifetime.Open(sponsorPid, sponsorStarted)) {
                        Check(!sponsor.HasExited && sponsor.Started == sponsorStarted, "Authenticated genuine MED sponsor lifetime remains pinned for all cases");
                        SameUser(high, Peer(sponsorPid), Medium, false);
                        ciSpawner = delegate(int index, string nonce, IntPtr window) {
                            Check(index >= 0 && index < Cases.Length && !sponsor.HasExited,
                                "Only a fixed boundary case may request a fresh child from the live authenticated MED sponsor");
                            var identity = AdministratorCiLaunchProbe.RequestBoundaryCase(sponsorControl, index, nonce, window);
                            Check(!sponsor.HasExited && identity != null && identity.Id != 0 && identity.Started > 0 &&
                                AdministratorCiLaunchProbe.VerifyBoundaryCase(identity),
                                "Sponsor-created child retains the exact fixture image, lifetime and genuine MED authentication context");
                            return identity;
                        };
                        Product();
                        var results = new List<object>(); completedCases = results;
                        using (var target = new OwnTarget()) {
                            foreach (string scenario in Cases) { currentScenario = scenario; results.Add(RunCase(IntPtr.Zero, high, target, scenario)); }
                        }
                        Check(ownedCacheHash != null && ownedCacheRoot != null, "Cleanup paths came only from the authenticated app's fixed payload path");
                        Check(RemoveDirectoryW(ownedCacheHash), "Only the created, now-empty protected payload hash directory is removed");
                        Check(RemoveDirectoryW(ownedCacheRoot) && Missing(ownedCacheRoot), "Only the created, now-empty protected fixture cache root is removed");
                        currentPhase = "completed"; Save("passed", "", results); return 0;
                    }
                } catch (Blocked error) {
                    Console.Error.WriteLine("BLOCKED: " + error.Message); Save("blocked", error.Message, null); return BlockedExit;
                } catch (Exception error) {
                    string reason = FailureReason(error);
                    Console.Error.WriteLine("FAILED: " + reason); Save("failed", reason, null); return 1;
                } finally { ciSpawner = null; Console.SetOut(previousOut); Console.SetError(previousError); }
            }
        } finally { ciSpawner = null; Console.SetOut(previousOut); Console.SetError(previousError); }
    }
    static object RunCase(IntPtr linked, TokenView high, OwnTarget target, string scenario)
    {
        currentPhase = "target-responsive-fence";
        target.RequireResponsive();
        currentPhase = "case-start";
        if (scenario == "approval-host-route") RequireNonInteractiveConsent();
        IntPtr window = target.Window;
        string nonce = Guid.NewGuid().ToString("N"); int before = checks;
        // MED creates the control server below. HIGH connects anonymously;
        // this mirrors the product boundary and needs no object-label change.
        using (var control = new NamedPipeClientStream(".", "TaskbarSelectBoundary." + nonce, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous)) {
            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)) }; ProcessInfo created = new ProcessInfo();
            string self = Assembly.GetExecutingAssembly().Location;
            string arguments = String.Join(" ", new[] { self, "--medium", app, evidence, nonce, scenario,
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), Started(Process.GetCurrentProcess()).ToString(CultureInfo.InvariantCulture), window.ToInt64().ToString(CultureInfo.InvariantCulture) }.Select(Quote));
            long expectedParentStarted = 0;
            if (ciSpawner != null) {
                var identity = ciSpawner(Array.IndexOf(Cases, scenario), nonce, window);
                created.ProcessId = identity.Id; expectedParentStarted = identity.Started;
            } else {
                if (!CreateProcessWithTokenW(linked, 0, self, new StringBuilder(arguments), 0x08000000, IntPtr.Zero, Path.GetDirectoryName(self), ref startup, out created))
                    throw new Blocked("CreateProcessWithTokenW cannot start the genuine linked MED parent; error=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                CloseHandle(created.Thread); CloseHandle(created.Process);
            }
            using (Process parent = Process.GetProcessById((int)created.ProcessId))
            using (Lifetime parentLifetime = Lifetime.Open(created.ProcessId, expectedParentStarted)) {
                long parentStarted = Started(parent); SameUser(high, Peer(created.ProcessId), Medium, false);
                Check(!parentLifetime.HasExited && parentStarted == parentLifetime.Started, "Fresh owned MED parent lifetime remains pinned before control authentication");
                control.Connect(8000); uint controlOwner;
                Check(GetNamedPipeServerProcessId(control.SafePipeHandle.DangerousGetHandle(), out controlOwner) && controlOwner == parent.Id && Started(parent) == parentStarted,
                    "HIGH fixture authenticates the exact freshly created MED control server");
                Packet ready = Read(control);
                Check(ready.Kind == 30 && ready.Sequence == 1 && ready.Values.Length == 2 && ready.Values[0] == parent.Id && ready.Values[1] == parentStarted, "Genuine MED parent staged and is ready for the actual native bootstrap");
                // MED created/staged this profile first. HIGH now independently
                // pins the exact embedded bytes, without making MED depend on
                // HIGH-created writable objects or integrity-label changes.
                using (var image = (IDisposable)Stage(Path.Combine(evidence, "profile"))) {
                if (scenario == "approval-host-route") return RunApprovalHigh(image, parent, parentLifetime, control, high, nonce, before, target);
                var start = new ProcessStartInfo { FileName = ImageString(image, "Path"),
                    Arguments = "--administrator-bootstrap " + nonce + " " + parent.Id.ToString(CultureInfo.InvariantCulture) + " " + parentStarted.ToString(CultureInfo.InvariantCulture),
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System) };
                // Deliberately poison only this owned child's inherited CLR environment.
                start.EnvironmentVariables["COR_ENABLE_PROFILING"] = "1";
                start.EnvironmentVariables["COR_PROFILER"] = "{11111111-1111-1111-1111-111111111111}";
                start.EnvironmentVariables["COR_PROFILER_PATH"] = Path.Combine(evidence, "missing-profiler.dll");
                start.EnvironmentVariables["COMPLUS_ApplicationMigrationRuntimeActivationConfigPath"] = Path.Combine(evidence, "missing-runtime.config");
                currentPhase = "native-start";
                using (var outer = new OuterJob())
                using (Process native = StartPinnedNative(image, start, outer))
                using (Lifetime nativeLifetime = Lifetime.Open((uint)native.Id, Started(native))) {
                    // The MED parent cannot send Allow21 until control31 below.
                    // Assign the real native process before any payload exists.
                    long nativeStarted = Started(native); SameUser(high, Peer((uint)native.Id), High, true);
                    Check(VerifyBootstrap(image, native), "Actual HIGH native image matches the app's pinned embedded bootstrap");
                    Write(control, 31, 1, native.Id, nativeStarted);
                    Packet child = Read(control);
                    if (scenario == "absent-boot-grant") {
                        Check(child.Kind == 39 && child.Sequence == 1 && child.Values.Length == 1 && child.Values[0] > 0 && child.Values[0] <= 4096,
                            "MED authenticated the actual native hello then withheld the first boot grant");
                        currentPhase = "parent-exit";
                        Check(parentLifetime.WaitForExit(10000) && parentLifetime.ExitCode == 0, "Owned MED no-grant parent exits within ten seconds");
                        currentPhase = "native-exit";
                        Check(nativeLifetime.WaitForExit(10000) && nativeLifetime.ExitCode == 104,
                            "Pinned actual native prechild exit104 rejects the missing Allow21 grant");
                        Check(WaitForOuterJobZero(outer), "Independent outer job confirms no native/payload/worker remains after prechild refusal");
                        Check(ownedCacheHash != null && Missing(ownedCacheHash + "\\" + nonce),
                            "No-grant refusal never creates a protected payload/config session");
                        checks += (int)child.Values[0];
                        return new { name = scenario, status = "passed", assertions = checks - before, genuineMedium = true, genuineHigh = true,
                            nativeExit = 104, prechildRefusal = true, protectedSessionNeverCreated = true };
                    }
                    Check(child.Kind == 32 && child.Sequence == 1 && child.Values.Length == 2 && child.Values[0] > 0, "Actual suspended payload identity is received before Resume");
                    currentPhase = "payload-lifetime";
                    using (Process payload = Process.GetProcessById((int)child.Values[0]))
                    using (Lifetime payloadLifetime = Lifetime.Open((uint)payload.Id, child.Values[1])) {
                        Check(Started(payload) == child.Values[1], "HIGH orchestrator independently pins the real payload process lifetime");
                        string physicalPayload = VerifiedPhysicalPayload((uint)payload.Id, ImageString(image, "PayloadHash"), nonce);
                        ownedCacheHash = PhysicalParent(PhysicalParent(physicalPayload)); ownedCacheRoot = PhysicalParent(ownedCacheHash);
                        Write(control, 34, 1, 1);
                        currentPhase = "scenario-result";
                        Packet done = Read(control);
                        while (done.Kind == 35 || done.Kind == 37) {
                            Check(done.Sequence == 1, "Owned fixture-control sequence remains fixed");
                            if (done.Kind == 35) {
                                Check(done.Values.SequenceEqual(new long[] { 2500 }), "Only the owned HWND may be made unresponsive for a fixed 2.5 seconds");
                                target.Block(); Write(control, 36, 1, 1);
                            } else {
                                Check(done.Values.Length == 0, "Independent active-worker proof has no caller-controlled values");
                                var activeWatch = Stopwatch.StartNew(); uint active;
                                do { active = outer.ActiveProcesses(); if (active > 2) break; Thread.Sleep(5); } while (activeWatch.ElapsedMilliseconds < 800);
                                Check(active > 2, "Independent job accounting observes a real authorized HIGH worker before MED parent death");
                                Write(control, 38, 1, active);
                            }
                            done = Read(control);
                        }
                        Check(done.Kind == 33 && done.Sequence == 1 && done.Values.Length == 1 && done.Values[0] > 0 && done.Values[0] <= 4096, "MED parent completed the scenario's genuine boundary assertions");
                        currentPhase = "parent-exit";
                        Check(parentLifetime.WaitForExit(10000) && parentLifetime.ExitCode == 0, "Owned MED parent exits successfully within ten seconds");
                        currentPhase = "native-exit";
                        Check(nativeLifetime.WaitForExit(10000) && nativeLifetime.ExitCode == 0, "Authenticated native exit zero proves its real job drained within ten seconds");
                        currentPhase = "payload-exit";
                        Check(payloadLifetime.WaitForExit(1000), "Real HIGH payload process is exited after native drain");
                        currentPhase = "outer-job-drain";
                        Check(WaitForOuterJobZero(outer), "Independent outer job reports zero native/payload/worker descendants before its handle is closed");
                        string session = PhysicalParent(physicalPayload);
                        currentPhase = "protected-session-cleanup";
                        Check(Missing(session), "Actual protected payload/config/temp session is cleaned after descendant drain");
                        checks += (int)done.Values[0];
                    }
                }
                return new { name = scenario, status = "passed", assertions = checks - before, genuineMedium = true, genuineHigh = true, nativeExit = 0, protectedSessionRemoved = true };
                }
            }
        }
    }
    static object RunApprovalHigh(object image, Process parent, Lifetime parentLifetime, PipeStream control, TokenView high, string nonce, int before, OwnTarget target)
    {
        currentPhase = "approval-host-admission";
        using (var outer = new OuterJob()) {
            Write(control, 40, 1, 1);
            Packet hostIdentity = Read(control);
            Check(hostIdentity.Kind == 41 && hostIdentity.Sequence == 1 && ValidIdentity(hostIdentity.Values),
                "Actual shared StartApprovalHost API returns one freshly verified MED native host");
            using (Lifetime hostLifetime = Lifetime.Open((uint)hostIdentity.Values[0], hostIdentity.Values[1]))
            using (Process host = OpenLiveApprovalHost(hostLifetime)) {
                Check(Started(host) == hostIdentity.Values[1] && VerifyBootstrap(image, host), "HIGH independently pins the actual MED approval-host image and lifetime");
                SameUser(high, Peer((uint)host.Id), Medium, false);
                // The host is waiting for Approval31. Contain it before any
                // Shell runas request is permitted, without a product test hook.
                outer.AssignIfNeeded(host); Write(control, 42, 1, 1);
                Packet highIdentity = Read(control);
                Check(highIdentity.Kind == 43 && highIdentity.Sequence == 1 && ValidIdentity(highIdentity.Values),
                    "Actual Shell runas HIGH identity arrives before Approval33 or Bootstrap21");
                using (Process native = Process.GetProcessById((int)highIdentity.Values[0]))
                using (Lifetime nativeLifetime = Lifetime.Open((uint)native.Id, highIdentity.Values[1])) {
                    Check(Started(native) == highIdentity.Values[1] && VerifyBootstrap(image, native), "HIGH independently verifies the actual elevated Shell process image and lifetime");
                    SameUser(high, Peer((uint)native.Id), High, true);
                    // AppInfo may already inherit the host's job. Query that
                    // genuine state instead of assuming it; assign if needed.
                    outer.AssignIfNeeded(native); Write(control, 44, 1, 1);
                    Packet child = Read(control);
                    Check(child.Kind == 32 && child.Sequence == 1 && ValidIdentity(child.Values), "Actual approval route creates one suspended protected HIGH payload after containment");
                    using (Process payload = Process.GetProcessById((int)child.Values[0]))
                    using (Lifetime payloadLifetime = Lifetime.Open((uint)payload.Id, child.Values[1])) {
                        Check(Started(payload) == child.Values[1], "Approval-route payload lifetime is independently pinned");
                        SameUser(high, Peer((uint)payload.Id), High, true);
                        string physicalPayload = VerifiedPhysicalPayload((uint)payload.Id, ImageString(image, "PayloadHash"), nonce);
                        ownedCacheHash = PhysicalParent(PhysicalParent(physicalPayload)); ownedCacheRoot = PhysicalParent(ownedCacheHash);
                        Check(outer.Contains(payload), "Actual protected payload belongs to the independent outer job before Resume");
                        Write(control, 34, 1, 1);
                        int presentations = 0;
                        long[] presentationOrder = { 1, 1, 0 };
                        Packet done = Read(control);
                        while (done.Kind == 45) {
                            Check(done.Sequence == 1 && done.Values.Length == 1 && presentations < presentationOrder.Length && done.Values[0] == presentationOrder[presentations++],
                                "Authenticated state phase may only show/focus or hide the same fixed owned HIGH Form");
                            target.Present(done.Values[0] == 1);
                            Write(control, 46, 1, done.Values[0]);
                            done = Read(control);
                        }
                        Check(presentations == presentationOrder.Length && !IsWindowVisible(target.Window) && !Topmost(target.Window) &&
                            done.Kind == 33 && done.Sequence == 1 && done.Values.Length == 1 && done.Values[0] > 0 && done.Values[0] <= 4096,
                            "MED completed real approval-host movement, state workers, owned pin cleanup and authenticated native drain checks");
                        currentPhase = "parent-exit";
                        Check(parentLifetime.WaitForExit(10000) && parentLifetime.ExitCode == 0, "Owned MED approval-route fixture exits within ten seconds");
                        currentPhase = "native-exit";
                        Check(nativeLifetime.WaitForExit(10000) && nativeLifetime.ExitCode == 0, "Actual HIGH bootstrap exits0 after its protected payload/worker job drains");
                        currentPhase = "approval-host-exit";
                        Check(hostLifetime.WaitForExit(10000) && hostLifetime.ExitCode == 0, "Real MED private-map approval host reports only the verified HIGH drain");
                        currentPhase = "payload-exit";
                        Check(payloadLifetime.WaitForExit(1000), "Actual approval-route managed payload exits after native drain");
                        currentPhase = "outer-job-drain";
                        Check(WaitForOuterJobZero(outer), "Independent outer job has zero MED-host/HIGH-bootstrap/payload/worker descendants before closing");
                        currentPhase = "protected-session-cleanup";
                        Check(Missing(PhysicalParent(physicalPayload)), "Actual approval-route protected session is removed only after descendant drain");
                        checks += (int)done.Values[0];
                    }
                }
            }
        }
        return new { name = "approval-host-route", status = "passed", assertions = checks - before, genuineMedium = true, genuineHigh = true,
            realApprovalHostRoute = true, sourceStageApi = true, interactivePromptTested = false, mediumHostExit = 0, nativeExit = 0, protectedSessionRemoved = true,
            minimizeWorkerConfirmed = true, fixedMinimizePlacementRestored = true, pinUnpinWorkersConfirmed = true, hiddenOwnedPinCleanupConfirmed = true,
            controllerMinimizeUndoTested = false };
    }
    static Process OpenLiveApprovalHost(Lifetime lifetime)
    {
        // The MED parent retains this exact process too. Query the supplied
        // lifetime before asking Process to enumerate only currently live PIDs.
        if (lifetime.HasExited) {
            approvalHostExitCode = lifetime.ExitCode;
            throw new InvalidOperationException("The authenticated MED approval host exited before HIGH admission.");
        }
        Process process = null;
        try {
            process = Process.GetProcessById((int)lifetime.Id);
            if (lifetime.HasExited) {
                approvalHostExitCode = lifetime.ExitCode;
                throw new InvalidOperationException("The authenticated MED approval host exited during HIGH admission.");
            }
            return process;
        } catch {
            if (lifetime.HasExited) approvalHostExitCode = lifetime.ExitCode;
            if (process != null) process.Dispose();
            throw;
        }
    }
    static bool ValidIdentity(long[] values)
    { return values != null && values.Length == 2 && values[0] > 0 && values[0] <= UInt32.MaxValue && values[1] > 0; }
    static int Parent(string[] args)
    {
        string nonce = args[3], scenario = args[4]; uint highPid = UInt32.Parse(args[5], CultureInfo.InvariantCulture);
        long highStarted = Int64.Parse(args[6], CultureInfo.InvariantCulture); IntPtr window = new IntPtr(Int64.Parse(args[7], CultureInfo.InvariantCulture));
        Check(Cases.Contains(scenario) && nonce.Length == 32 && nonce.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'), "Fixed fixture scenario and fresh nonce are valid");
        TokenView medium = Current(); Check(!medium.Elevated && medium.Integrity == Medium, "Parent really runs at medium integrity without elevation");
        SameUser(medium, Peer(highPid), High, true);
        using (Lifetime high = Lifetime.Open(highPid, highStarted)) Check(Started(high) == highStarted, "HIGH fixture process lifetime is freshly verified");
        Directory.CreateDirectory(evidence);
        currentScenario = scenario; currentPhase = "medium-stage";
        return CaptureMedium(nonce, delegate {
        Product(); object image = Stage(Path.Combine(evidence, "profile"));
        try {
            using (var control = Server("TaskbarSelectBoundary." + nonce, medium.Sid))
            using (var bootstrap = Server("TaskbarSelect.AdminBootstrap." + nonce, medium.Sid))
            using (var managed = Server("TaskbarSelect.Admin." + nonce, medium.Sid)) {
                using (Lifetime high = Lifetime.Open(highPid, highStarted)) {
                    Check(Started(high) == highStarted, "HIGH fixture lifetime remains current before control authentication");
                    Connect(control, high);
                }
                Write(control, 30, 1, Process.GetCurrentProcess().Id, Started(Process.GetCurrentProcess()));
                if (scenario == "approval-host-route") return ApprovalParent(image, control, bootstrap, managed, medium, nonce, window, highPid, highStarted);
                Packet start = Read(control); Check(start.Kind == 31 && start.Sequence == 1 && start.Values.Length == 2, "Owned HIGH native process identity is received");
                using (Lifetime native = Lifetime.Open((uint)start.Values[0], start.Values[1])) {
                    diagnosticNative = Lifetime.Open(native.Id, native.Started);
                    Check(Started(native) == start.Values[1] && VerifyBootstrap(image, native), "MED independently verifies the freshly launched pinned native image");
                    SameUser(medium, Peer((uint)native.Id), High, true); Connect(bootstrap, native);
                    Packet hello = Read(bootstrap);
                    Check(hello.Kind == 20 && hello.Sequence == 1 && hello.Values.SequenceEqual(new long[] { Process.GetCurrentProcess().Id, Started(Process.GetCurrentProcess()), native.Id, Started(native) }), "Actual native 20 hello authenticates both process lifetimes");
                    if (scenario == "absent-boot-grant") {
                        bootstrap.Dispose();
                        Write(control, 39, 1, checks);
                        return 0;
                    }
                    Write(bootstrap, 21, 1, 1); Packet child = Read(bootstrap);
                    Check(child.Kind == 22 && child.Sequence == 1 && child.Values.Length == 2 && child.Values[0] > 0, "Native created its actual suspended HIGH payload in the kill-on-close job");
                    using (Lifetime payload = Lifetime.Open((uint)child.Values[0], child.Values[1])) {
                        diagnosticPayload = Lifetime.Open(payload.Id, payload.Started);
                        Check(Started(payload) == child.Values[1], "MED verifies the actual suspended payload lifetime"); SameUser(medium, Peer((uint)payload.Id), High, true);
                        string expected = PayloadPath(image, nonce);
                        Check(VerifyPayload(expected, ImageString(image, "PayloadHash"), nonce), "Real protected payload/config/ACL/physical-cache identity validates before Resume");
                        VerifiedPhysicalPayload((uint)payload.Id, ImageString(image, "PayloadHash"), nonce);
                        Denied(expected, 2, "MED cannot open the actual payload for write before Resume");
                        Denied(expected + ".config", 2, "MED cannot open the actual CLR config for write before Resume");
                        Denied(expected + ".config", 0x40000, "MED cannot obtain WRITE_DAC on actual CLR config before Resume");
                        Denied(expected, 0x10000, "MED cannot obtain delete/replace access to actual payload before Resume");
                        Write(control, 32, 1, payload.Id, Started(payload));
                        Packet pinned = Read(control); Check(pinned.Kind == 34 && pinned.Sequence == 1 && pinned.Values.SequenceEqual(new long[] { 1 }), "HIGH fixture pinned the suspended payload before any Resume or cancellation");
                        if (scenario == "absent-resume") { bootstrap.Dispose(); }
                        else {
                            Write(bootstrap, 23, 1, 1); Connect(managed, payload);
                            Rect original; Check(GetWindowRect(window, out original), "Only the HIGH fixture's own HWND bounds are captured");
                            IntPtr foreground = GetForegroundWindow(); Focus(managed, 1);
                            if (scenario == "move-placement-undo") {
                                Placement placement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
                                Check(GetWindowPlacement(window, ref placement), "Owned HWND recovery placement is captured before mutation");
                                Result(managed, 2, window, highPid, highStarted, new[] { original.Left + 31, original.Top + 23, original.Right - original.Left, original.Bottom - original.Top, 0x214 }, false);
                                Check(WaitBounds(window, original.Left + 31, original.Top + 23, original.Right - original.Left, original.Bottom - original.Top), "Actual elevated worker moves only the owned HIGH HWND"); Focus(managed, 3);
                                int[] undo = { placement.Flags, placement.Show, placement.Minimum.X, placement.Minimum.Y, placement.Maximum.X, placement.Maximum.Y,
                                    placement.Normal.Left, placement.Normal.Top, placement.Normal.Right, placement.Normal.Bottom };
                                Result(managed, 4, window, highPid, highStarted, undo, true);
                                Check(WaitBounds(window, original.Left, original.Top, original.Right - original.Left, original.Bottom - original.Top), "Actual 15-field elevated placement worker restores the captured position"); Focus(managed, 5);
                                Minimum(managed, 6, window, highPid, highStarted); Focus(managed, 7);
                                Check(GetForegroundWindow() == foreground, "Owned move/placement and paused focus leave unrelated foreground unchanged");
                            } else if (scenario == "unresponsive-owned-target" || scenario == "active-worker-parent-death") {
                                Write(control, 35, 1, 2500); Packet blocked = Read(control);
                                Check(blocked.Kind == 36 && blocked.Sequence == 1 && blocked.Values.SequenceEqual(new long[] { 1 }), "Only the owned HIGH window message loop is deliberately blocked");
                                // Keep the real helper's existing 2s maximum and use
                                // its ordinary 1.5s budget. The owned pump remains
                                // blocked longer so cold CLR startup has margin
                                // before the attempted USER operation times out.
                                long due = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.5);
                                Write(managed, 1, 2, 0, window.ToInt64(), highPid, highStarted, due, original.Left + 47, original.Top + 43, original.Right - original.Left, original.Bottom - original.Top, 0x214);
                                Packet ready = Read(managed); Check(ready.Kind == 10 && ready.Sequence == 2 && ready.Values.Length == 0, "Actual helper reaches READY for the owned unresponsive HWND");
                                Write(managed, 2, 2, 1);
                                if (scenario == "active-worker-parent-death") {
                                    Write(control, 37, 1); Packet active = Read(control);
                                    Check(active.Kind == 38 && active.Sequence == 1 && active.Values.Length == 1 && active.Values[0] > 2, "HIGH orchestrator independently confirms an active worker before intentional parent death");
                                    Write(control, 33, 1, checks); Environment.Exit(0);
                                }
                                var responseWatch = Stopwatch.StartNew(); Packet cancelled = Read(managed, 2000);
                                Check(responseWatch.ElapsedMilliseconds <= 2000 && cancelled.Kind == 12 && cancelled.Sequence == 2 && cancelled.Values.Length == 8 && cancelled.Values[0] != 0 && cancelled.Values[1] == 1 && cancelled.Values[2] >= 0 && cancelled.Values[2] <= 1,
                                    "Authorized unresponsive request is bounded and retains conservative mutation uncertainty");
                                // This is fixture-owned recovery evidence. It does not
                                // claim production controller/history persistence.
                                File.WriteAllText(Path.Combine(evidence, "owned-recovery-" + nonce + ".json"), new JavaScriptSerializer().Serialize(new {
                                    originalBounds = new[] { original.Left, original.Top, original.Right, original.Bottom },
                                    outcome = cancelled.Values[0], mutationAttempted = cancelled.Values[1], callerExited = cancelled.Values[2],
                                    retainedBeforeNativeDrain = true
                                }), new UTF8Encoding(false));
                                Check(File.Exists(Path.Combine(evidence, "owned-recovery-" + nonce + ".json")), "Owned pre-operation recovery record is retained through bounded cancellation");
                            } else if (scenario == "bad-sequence") { Write(managed, 3, 1, PausedFocusConfiguration()); PeerClosed(managed, 2000, "Actual helper rejects duplicate sequence by closing its endpoint before fixture cancellation"); }
                            else if (scenario == "parent-death") {
                                Write(control, 33, 1, checks); Environment.Exit(0);
                            } else {
                                long due = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (scenario == "late-authorization" ? 0.12 : 1.5));
                                Write(managed, 1, 2, 0, window.ToInt64(), highPid, highStarted, due, original.Left + 71, original.Top + 59, original.Right - original.Left, original.Bottom - original.Top, 0x214);
                                Packet ready = Read(managed); Check(ready.Kind == 10 && ready.Sequence == 2 && ready.Values.Length == 0, "Real helper reaches READY before any window mutation");
                                if (scenario == "late-authorization") {
                                    Thread.Sleep(180); Check(Stopwatch.GetTimestamp() > due, "Actual request deadline expired before late authorization");
                                    try { Write(managed, 2, 2, 1); Packet rejected = Read(managed);
                                        Check(rejected.Kind == 12 && rejected.Sequence == 2 && rejected.Values.Length == 8 && rejected.Values[0] != 0 && rejected.Values[1] == 0 && rejected.Values[2] == 1,
                                            "Late actual authorization is rejected before mutation, with a confirmed caller exit"); }
                                    catch (IOException) { Check(Stopwatch.GetTimestamp() > due, "Expired authorization pipe failed closed; actual native drain and unchanged HWND are still required"); }
                                } else PeerClosed(managed, 2500, "Actual missing authorization expires and closes the helper endpoint before fixture cancellation");
                            }
                            if (scenario != "move-placement-undo" && scenario != "unresponsive-owned-target") Check(WaitBounds(window, original.Left, original.Top, original.Right - original.Left, original.Bottom - original.Top), "Pre-mutation negative case never moves the owned HIGH HWND");
                            managed.Dispose(); Packet drained = Read(bootstrap);
                            Check(drained.Kind == 24 && drained.Sequence == 1 && drained.Values.Length == 2 && drained.Values[1] == 0, "Actual native 24 reply reports zero active job descendants");
                        }
                        Write(control, 33, 1, checks);
                    }
                }
            }
        } finally { ((IDisposable)image).Dispose(); }
        return 0;
        });
    }
    static int CaptureMedium(string nonce, Func<int> run)
    {
        TextWriter previousOut = Console.Out, previousError = Console.Error;
        try {
            using (var output = new StreamWriter(new FileStream(Path.Combine(evidence, "owned-medium-" + nonce + ".stdout.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
            using (var errors = new StreamWriter(new FileStream(Path.Combine(evidence, "owned-medium-" + nonce + ".stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))) {
                output.AutoFlush = true; errors.AutoFlush = true;
                Console.SetOut(output); Console.SetError(errors);
                try { return run(); }
                catch (Blocked error) { ObserveMediumExits(); Console.Error.WriteLine("BLOCKED: " + FailureReason(error)); return BlockedExit; }
                catch (Exception error) { ObserveMediumExits(); Console.Error.WriteLine("FAILED: " + FailureReason(error)); return 1; }
                finally { Console.SetOut(previousOut); Console.SetError(previousError); }
            }
        } finally {
            Console.SetOut(previousOut); Console.SetError(previousError);
            foreach (Lifetime lifetime in new[] { diagnosticPayload, diagnosticNative, diagnosticHost }) if (lifetime != null) lifetime.Dispose();
            diagnosticPayload = diagnosticNative = diagnosticHost = null;
        }
    }
    static void ObserveMediumExit(Lifetime lifetime, string role, Action<int> record)
    {
        if (lifetime == null) return;
        try {
            bool exited = lifetime.HasExited;
            Console.Error.WriteLine("Retained " + role + " exited=" + exited.ToString(CultureInfo.InvariantCulture));
            if (exited) { int code = lifetime.ExitCode; record(code); Console.Error.WriteLine("Retained " + role + " exit=" + code.ToString(CultureInfo.InvariantCulture)); }
        } catch (Exception error) { Console.Error.WriteLine("Retained " + role + " observation hr=0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture)); }
    }
    static void ObserveMediumExits()
    {
        ObserveMediumExit(diagnosticPayload, "payload", delegate(int code) { diagnosticPayloadExitCode = code; });
        ObserveMediumExit(diagnosticNative, "native", delegate(int code) { diagnosticNativeExitCode = code; });
        ObserveMediumExit(diagnosticHost, "approval-host", delegate(int code) { approvalHostExitCode = code; });
    }
    static int ApprovalParent(object image, PipeStream control, NamedPipeServerStream bootstrap, NamedPipeServerStream managed,
        TokenView medium, string nonce, IntPtr window, uint highPid, long highStarted)
    {
        RequireNonInteractiveConsent();
        currentPhase = "approval-host-admission";
        using (var approval = Server("TaskbarSelect.AdminApproval." + nonce, medium.Sid)) {
            Packet start = Read(control);
            Check(start.Kind == 40 && start.Sequence == 1 && start.Values.SequenceEqual(new long[] { 1 }), "HIGH fixture permits only the fixed real approval-host scenario");
            uint ownPid = (uint)Process.GetCurrentProcess().Id; long ownStarted = Started(Process.GetCurrentProcess());
            using (Process host = (Process)image.GetType().GetMethod("StartApprovalHost", HiddenInstance, null,
                new[] { typeof(uint), typeof(long), typeof(string) }, null).Invoke(image, new object[] { ownPid, ownStarted, nonce }))
            using (Lifetime hostLifetime = Lifetime.Open((uint)host.Id, Started(host))) {
                diagnosticHost = Lifetime.Open(hostLifetime.Id, hostLifetime.Started);
                long hostStarted = Started(host);
                Check(VerifyBootstrapIdentity(image, (uint)host.Id, hostStarted), "Actual shared API launched the pinned MED native image");
                SameUser(medium, Peer((uint)host.Id), Medium, false);
                Write(control, 41, 1, host.Id, hostStarted);
                Packet hostContained = Read(control);
                Check(hostContained.Kind == 42 && hostContained.Sequence == 1 && hostContained.Values.SequenceEqual(new long[] { 1 }),
                    "Independent HIGH fixture contained the actual MED host before Approval31");
                Connect(approval, host); Packet hello = Read(approval);
                Check(hello.Kind == 30 && hello.Sequence == 1 && hello.Values.SequenceEqual(new long[] { ownPid, ownStarted, host.Id, hostStarted }),
                    "Production Approval30 authenticates both original MED parent and actual native host lifetimes");
                Write(approval, 31, 1, 1); Packet elevated = Read(approval);
                Check(elevated.Kind == 32 && elevated.Sequence == 1 && ValidIdentity(elevated.Values),
                    "Real MED private-map Shell runas returns the fixed authenticated HIGH process identity");
                using (Lifetime native = Lifetime.Open((uint)elevated.Values[0], elevated.Values[1])) {
                    diagnosticNative = Lifetime.Open(native.Id, native.Started);
                    Check(Started(native) == elevated.Values[1] && VerifyBootstrapIdentity(image, (uint)native.Id, elevated.Values[1]),
                        "MED independently verifies actual HIGH image/lifetime with read-only process rights");
                    SameUser(medium, Peer((uint)native.Id), High, true);
                    Write(control, 43, 1, native.Id, elevated.Values[1]); Packet nativeContained = Read(control);
                    Check(nativeContained.Kind == 44 && nativeContained.Sequence == 1 && nativeContained.Values.SequenceEqual(new long[] { 1 }),
                        "Independent HIGH fixture contained the elevated bootstrap before Approval33 or Bootstrap21");
                    Write(approval, 33, 1, 1);
                    Connect(bootstrap, native); Packet nativeHello = Read(bootstrap);
                    Check(nativeHello.Kind == 20 && nativeHello.Sequence == 1 && nativeHello.Values.SequenceEqual(new long[] { ownPid, ownStarted, native.Id, elevated.Values[1] }),
                        "Actual approval-route Bootstrap20 binds the original MED parent and contained HIGH image");
                    Write(bootstrap, 21, 1, 1); Packet child = Read(bootstrap);
                    Check(child.Kind == 22 && child.Sequence == 1 && ValidIdentity(child.Values), "Real approval route creates its suspended protected HIGH payload only after both grants");
                    using (Lifetime payload = Lifetime.Open((uint)child.Values[0], child.Values[1])) {
                        diagnosticPayload = Lifetime.Open(payload.Id, payload.Started);
                        Check(Started(payload) == child.Values[1], "MED pins the actual approval-route suspended payload lifetime");
                        SameUser(medium, Peer((uint)payload.Id), High, true);
                        string expected = PayloadPath(image, nonce);
                        Check(VerifyPayload(expected, ImageString(image, "PayloadHash"), nonce), "Real approval-route protected payload/config/ACL identity validates before Resume");
                        VerifiedPhysicalPayload((uint)payload.Id, ImageString(image, "PayloadHash"), nonce);
                        Denied(expected, 2, "MED cannot write the actual approval-route payload before Resume");
                        Denied(expected + ".config", 2, "MED cannot write the actual approval-route CLR config before Resume");
                        Denied(expected + ".config", 0x40000, "MED cannot change the actual approval-route CLR config DACL before Resume");
                        Denied(expected, 0x10000, "MED cannot delete or replace the actual approval-route payload before Resume");
                        Write(control, 32, 1, payload.Id, child.Values[1]); Packet pinned = Read(control);
                        Check(pinned.Kind == 34 && pinned.Sequence == 1 && pinned.Values.SequenceEqual(new long[] { 1 }),
                            "Independent HIGH proof confirms exact protected payload and outer-job membership before Resume");
                        currentPhase = "approval-resume";
                        Write(bootstrap, 23, 1, 1);
                        currentPhase = "approval-managed-connect";
                        Connect(managed, payload);
                        Rect original; Check(GetWindowRect(window, out original), "Real approval route captures only the fixture-owned hidden HIGH HWND");
                        IntPtr foreground = GetForegroundWindow(); currentPhase = "approval-initial-focus"; Focus(managed, 1);
                        Placement placement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
                        Check(GetWindowPlacement(window, ref placement), "Real approval route captures owned recovery placement before movement");
                        currentPhase = "approval-move";
                        Result(managed, 2, window, highPid, highStarted,
                            new[] { original.Left + 31, original.Top + 23, original.Right - original.Left, original.Bottom - original.Top, 0x214 }, false);
                        Check(WaitBounds(window, original.Left + 31, original.Top + 23, original.Right - original.Left, original.Bottom - original.Top),
                            "Real approval-route HIGH worker moves only the fixture-owned hidden HWND"); currentPhase = "approval-moved-focus"; Focus(managed, 3);
                        int[] undo = { placement.Flags, placement.Show, placement.Minimum.X, placement.Minimum.Y, placement.Maximum.X, placement.Maximum.Y,
                            placement.Normal.Left, placement.Normal.Top, placement.Normal.Right, placement.Normal.Bottom };
                        currentPhase = "approval-undo";
                        Result(managed, 4, window, highPid, highStarted, undo, true);
                        Check(WaitBounds(window, original.Left, original.Top, original.Right - original.Left, original.Bottom - original.Top),
                            "Real approval-route 15-field placement worker restores the captured owned HWND"); currentPhase = "approval-restored-focus"; Focus(managed, 5);
                        currentPhase = "approval-minimum"; Minimum(managed, 6, window, highPid, highStarted);
                        currentPhase = "approval-final-focus"; Focus(managed, 7);
                        Check(GetForegroundWindow() == foreground, "Real approval-route move/Undo and paused focus preserve unrelated foreground");
                        // State workers must pass the real visible/foreground
                        // policy. Only this already authenticated HIGH target
                        // is presented; no foreign foreground is activated.
                        currentPhase = "approval-state-owned-foreground";
                        PresentOwned(control, window, true);
                        RequireStateTarget(window, highPid, highStarted);
                        Placement statePlacement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
                        Rect stateBounds = new Rect();
                        Check(GetWindowPlacement(window, ref statePlacement) && statePlacement.Show == 1 && GetWindowRect(window, out stateBounds),
                            "Visible owned HIGH placement is captured before actual minimize");
                        currentPhase = "approval-state-minimize";
                        StateResult(managed, 8, 5, window, highPid, highStarted, null, 0);
                        Check(Wait(delegate { return IsIconic(window); }, 1000), "Actual protected HIGH op5 worker minimizes only its owned foreground HWND");
                        int[] restore = { statePlacement.Flags, statePlacement.Show, statePlacement.Minimum.X, statePlacement.Minimum.Y,
                            statePlacement.Maximum.X, statePlacement.Maximum.Y, statePlacement.Normal.Left, statePlacement.Normal.Top,
                            statePlacement.Normal.Right, statePlacement.Normal.Bottom };
                        currentPhase = "approval-state-placement-restore";
                        Result(managed, 9, window, highPid, highStarted, restore, true);
                        Check(Wait(delegate { return !IsIconic(window); }, 1000) &&
                            WaitBounds(window, stateBounds.Left, stateBounds.Top, stateBounds.Right - stateBounds.Left, stateBounds.Bottom - stateBounds.Top),
                            "Actual protected op1 restores the captured HIGH placement after minimize; controller Undo is not claimed");
                        PresentOwned(control, window, true);
                        RequireStateTarget(window, highPid, highStarted);
                        currentPhase = "approval-state-pin";
                        StateResult(managed, 10, 6, window, highPid, highStarted, 1, 1);
                        Check(Wait(delegate { return Topmost(window); }, 1000), "Actual protected HIGH op6 pin sets the owned HWND topmost flag");
                        RequireStateTarget(window, highPid, highStarted);
                        currentPhase = "approval-state-unpin";
                        StateResult(managed, 11, 6, window, highPid, highStarted, 0, 0);
                        Check(Wait(delegate { return !Topmost(window); }, 1000), "Actual protected HIGH op6 unpin clears the owned HWND topmost flag");
                        RequireStateTarget(window, highPid, highStarted);
                        currentPhase = "approval-state-retained-pin";
                        StateResult(managed, 12, 6, window, highPid, highStarted, 1, 1);
                        Check(Wait(delegate { return Topmost(window); }, 1000), "A second actual HIGH pin retains exactly one cleanup-owned HWND");
                        PresentOwned(control, window, false);
                        Check(!IsWindowVisible(window) && Topmost(window), "Only the pinned owned HIGH HWND is hidden before retained cleanup");
                        currentPhase = "approval-state-pin-release";
                        ReleasePins(managed, 13);
                        Check(Wait(delegate { return !Topmost(window); }, 1000) && !IsWindowVisible(window),
                            "Production pin release clears the retained hidden HWND topmost flag without showing it");
                        Focus(managed, 14);
                        currentPhase = "approval-native-drain";
                        managed.Dispose(); Packet drained = Read(bootstrap);
                        Check(drained.Kind == 24 && drained.Sequence == 1 && drained.Values.Length == 2 && drained.Values[1] == 0,
                            "Real approval-route native24 confirms its payload/worker job drained");
                    }
                    currentPhase = "native-exit";
                    Check(native.WaitForExit(10000) && native.ExitCode == 0, "MED observes actual HIGH native exit0 after drain");
                }
                currentPhase = "approval-host-exit";
                Check(hostLifetime.WaitForExit(10000) && hostLifetime.ExitCode == 0, "MED observes actual private-map approval host exit0 after verified HIGH drain");
                Write(control, 33, 1, checks);
            }
        }
        return 0;
    }
    static void Denied(string path, uint access, string label)
    {
        IntPtr handle = CreateFileW(path, access, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); int error = Marshal.GetLastWin32Error();
        if (handle != new IntPtr(-1)) { CloseHandle(handle); throw new InvalidOperationException(label); }
        Check(error == 5, label + " (ACCESS_DENIED, independent of sharing locks)");
    }
    static void PeerClosed(PipeStream pipe, int milliseconds, string label)
    {
        byte[] one = new byte[1]; var read = new ReadWait(pipe, one, 0, 1);
        if (!Wait(delegate { return read.Complete; }, milliseconds)) { pipe.Dispose(); throw new InvalidOperationException("Peer did not reject the negative scenario within its real deadline."); }
        try { Check(read.End() == 0, label); } catch (IOException) { Check(true, label); }
    }
    static void Focus(PipeStream pipe, int sequence)
    { Write(pipe, 3, sequence, PausedFocusConfiguration()); Packet reply = Read(pipe); Check(reply.Kind == 11 && reply.Sequence == sequence && reply.Values.Length == 2 && reply.Values[1] == 0, "Actual managed IPC accepts paused focus and pins and retains zero uncertain workers"); }
    static void Result(PipeStream pipe, int sequence, IntPtr window, uint pid, long started, int[] values, bool placement)
    {
        var timing = Stopwatch.StartNew();
        long due = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.5);
        var data = new List<long> { placement ? 1 : 0, window.ToInt64(), pid, started, due }; data.AddRange(values.Select(value => (long)value));
        Write(pipe, 1, sequence, data.ToArray()); Packet ready = Read(pipe);
        Check(ready.Kind == 10 && ready.Sequence == sequence && ready.Values.Length == 0, "Actual helper validates the owned operation before authorization");
        Write(pipe, 2, sequence, 1); Packet result = Read(pipe);
        LogOperationReply(placement ? "placement" : "position", result, timing);
        Check(result.Kind == 12 && result.Sequence == sequence && result.Values.Length == 8 && result.Values[0] == 0 && result.Values[1] == 1 && result.Values[2] == 1 && result.Values[7] == 0,
            "Actual HIGH helper-to-worker operation succeeds with mutation and confirmed caller exit");
    }
    static void Minimum(PipeStream pipe, int sequence, IntPtr window, uint pid, long started)
    {
        var timing = Stopwatch.StartNew();
        long due = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.5);
        Write(pipe, 1, sequence, 3, window.ToInt64(), pid, started, due); Packet ready = Read(pipe);
        Check(ready.Kind == 10 && ready.Sequence == sequence && ready.Values.Length == 0, "Actual elevated minimum-size query is validated before authorization");
        Write(pipe, 2, sequence, 1); Packet result = Read(pipe);
        LogOperationReply("minimum", result, timing);
        Check(result.Kind == 12 && result.Sequence == sequence && result.Values.Length == 8 && result.Values[0] == 0 && result.Values[1] == 0 && result.Values[2] == 1 && result.Values[3] > 0 && result.Values[4] > 0 && result.Values[7] == 0,
            "Actual HIGH minimum-size worker succeeds without mutation and with confirmed caller exit");
    }
    static bool Topmost(IntPtr window) { return (GetWindowLongPtr(window, -20).ToInt64() & 8) != 0; }
    static void PresentOwned(PipeStream control, IntPtr window, bool show)
    {
        Write(control, 45, 1, show ? 1 : 0);
        Packet reply = Read(control, 3000);
        Check(reply.Kind == 46 && reply.Sequence == 1 && reply.Values.SequenceEqual(new long[] { show ? 1 : 0 }),
            "Authenticated HIGH owner acknowledges presentation of only its fixed target");
        Check(IsWindowVisible(window) == show && (!show || GetForegroundWindow() == window),
            "Actual owned HIGH target visibility and foreground match the requested state");
    }
    static void RequireStateTarget(IntPtr window, uint pid, long started)
    {
        var native = product.GetType("TaskbarSelect.FocusedWindowNative", true);
        var policy = product.GetType("TaskbarSelect.WindowStatePolicy", true);
        object snapshot = native.GetMethod("Read", HiddenStatic, null, new[] { typeof(IntPtr), typeof(bool) }, null).Invoke(null, new object[] { window, true });
        Check(snapshot != null, "Actual production state snapshot reads the owned HIGH HWND");
        Type type = snapshot.GetType();
        Check((IntPtr)type.GetField("Window", HiddenInstance).GetValue(snapshot) == window &&
            (IntPtr)type.GetField("Root", HiddenInstance).GetValue(snapshot) == window &&
            (uint)type.GetField("Process", HiddenInstance).GetValue(snapshot) == pid &&
            (bool)type.GetField("Visible", HiddenInstance).GetValue(snapshot) &&
            ((int)type.GetField("ExtendedStyle", HiddenInstance).GetValue(snapshot) & 0x08000080) == 0 &&
            (long)policy.GetMethod("WorkerStarted", HiddenStatic).Invoke(null, new[] { snapshot }) == started &&
            (bool)policy.GetMethod("Eligible", HiddenStatic).Invoke(null, new object[] { snapshot, new Func<IntPtr, bool>(delegate { return false; }) }) &&
            GetForegroundWindow() == window && !IsIconic(window),
            "Owned HIGH state target is genuinely visible, root, same lifetime, eligible, foreground and neither tool-window nor NOACTIVATE");
    }
    static void StateResult(PipeStream pipe, int sequence, int operation, IntPtr window, uint pid, long started, int? desired, int owned)
    {
        Check((operation == 5 && !desired.HasValue && owned == 0) ||
            (operation == 6 && desired.HasValue && (desired.Value == 0 || desired.Value == 1) && owned == desired.Value),
            "Fixture state request uses only fixed minimize or Boolean pin shapes");
        var timing = Stopwatch.StartNew();
        long due = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.5);
        var data = new List<long> { operation, window.ToInt64(), pid, started, due };
        if (desired.HasValue) data.Add(desired.Value);
        Write(pipe, 1, sequence, data.ToArray()); Packet ready = Read(pipe);
        Check(ready.Kind == 10 && ready.Sequence == sequence && ready.Values.Length == 0,
            "Actual HIGH helper validates real foreground state action before authorization");
        Write(pipe, 2, sequence, 1); Packet result = Read(pipe);
        LogOperationReply(operation == 5 ? "minimize" : desired.Value == 1 ? "pin" : "unpin", result, timing);
        long[] expected = operation == 5 ? new long[] { 0, 1, 1, 0, 0, 0, 0, 0 } : new long[] { 0, 1, 1, 0, 0, 0, 0, 0, owned };
        Check(result.Kind == 12 && result.Sequence == sequence && result.Values.SequenceEqual(expected),
            "Actual protected HIGH state worker succeeds with exact reply shape, mutation, confirmed exit and retained pin count");
    }
    static void ReleasePins(PipeStream pipe, int sequence)
    {
        Write(pipe, 16, sequence, Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency);
        Packet ready = Read(pipe);
        Check(ready.Kind == 17 && ready.Sequence == sequence && ready.Values.Length == 0,
            "Production owned-pin release requires its separate READY authorization");
        Write(pipe, 18, sequence, 1); Packet released = Read(pipe);
        Check(released.Kind == 19 && released.Sequence == sequence && released.Values.SequenceEqual(new long[] { 1, 0, 0, 0 }),
            "Production owned-pin release confirms restored state and zero retained pins, active workers and uncertain workers");
    }
    static void LogOperationReply(string operation, Packet result, Stopwatch timing)
    {
        Console.WriteLine("Owned operation numeric reply: " + new JavaScriptSerializer().Serialize(new {
            operation = operation, kind = result.Kind, sequence = result.Sequence,
            valueCount = result.Values.Length, values = result.Values.Take(8).ToArray(),
            elapsedMilliseconds = timing.ElapsedMilliseconds, budgetMilliseconds = 1500
        }));
    }
    static bool WaitBounds(IntPtr window, int left, int top, int width, int height)
    {
        var watch = Stopwatch.StartNew(); Rect bounds;
        do { if (GetWindowRect(window, out bounds) && bounds.Left == left && bounds.Top == top && bounds.Right - bounds.Left == width && bounds.Bottom - bounds.Top == height) return true; Thread.Sleep(10); } while (watch.ElapsedMilliseconds < 1000);
        return false;
    }
    sealed class OwnTarget : IDisposable
    {
        sealed class PumpAcknowledgement { internal volatile bool Complete; internal Exception Failure; }
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Thread thread; Form form; Exception failure;
        internal IntPtr Window;
        internal OwnTarget()
        {
            thread = new Thread((ThreadStart)delegate {
                try {
                    SetProcessDpiAwarenessContext(new IntPtr(-4));
                    form = new Form { Text = "Owned administrator boundary fixture", ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = new System.Drawing.Rectangle(80, 80, 320, 240) };
                    Window = form.Handle; ready.Set(); Application.Run(new ApplicationContext());
                } catch (Exception error) { failure = error; ready.Set(); }
                finally { if (form != null) form.Dispose(); }
            }); thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!ready.WaitOne(5000) || failure != null || Window == IntPtr.Zero) throw new InvalidOperationException("Owned hidden HIGH HWND did not initialize.");
        }
        internal void Block()
        {
            var entered = new ManualResetEvent(false);
            form.BeginInvoke((Action)delegate { entered.Set(); Thread.Sleep(2500); });
            if (!entered.WaitOne(1000)) throw new InvalidOperationException("Owned HWND block did not begin.");
            entered.Dispose();
        }
        internal void RequireResponsive()
        {
            var acknowledgement = new PumpAcknowledgement();
            form.BeginInvoke((Action)delegate { acknowledgement.Complete = true; });
            Check(Wait(delegate { return acknowledgement.Complete; }, 3000),
                "Owned hidden HIGH window pump acknowledges readiness before the next case within three seconds");
        }
        internal void Present(bool show)
        {
            var acknowledgement = new PumpAcknowledgement();
            form.BeginInvoke((Action)delegate {
                try {
                    if (show) { form.Show(); form.Activate(); SetForegroundWindow(Window); }
                    else form.Hide();
                } catch (Exception error) { acknowledgement.Failure = error; }
                finally { acknowledgement.Complete = true; }
            });
            Check(Wait(delegate { return acknowledgement.Complete; }, 2000) && acknowledgement.Failure == null,
                "Only the owned HIGH Form completes bounded presentation on its own message pump");
            Check(form.Handle == Window && IsWindowVisible(Window) == show && (!show || (GetForegroundWindow() == Window && !IsIconic(Window))) &&
                (GetWindowLongPtr(Window, -20).ToInt64() & 0x08000080) == 0,
                "Owned HIGH presentation genuinely satisfies visibility and foreground without tool-window or NOACTIVATE styles");
        }
        public void Dispose()
        {
            if (form != null && form.IsHandleCreated) form.BeginInvoke((Action)delegate { Application.ExitThread(); });
            Check(thread.Join(5000), "Only the owned hidden HIGH target thread exits within five seconds"); ready.Dispose();
        }
    }
    static bool WaitForOuterJobZero(OuterJob outer)
    {
        var elapsed = Stopwatch.StartNew();
        uint initial = outer.ActiveProcesses(), final = initial;
        bool zero = initial == 0 || Wait(delegate { final = outer.ReadActiveProcesses(); return final == 0; }, 1000);
        final = outer.ReadActiveProcesses();
        Console.WriteLine("Outer job accounting initialActive=" + initial.ToString(CultureInfo.InvariantCulture) +
            " finalActive=" + final.ToString(CultureInfo.InvariantCulture) +
            " elapsedMs=" + elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
        return zero && final == 0;
    }
    sealed class OuterJob : IDisposable
    {
        IntPtr handle;
        internal OuterJob()
        {
            handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Owned outer job could not be created.");
            IntPtr limits = Marshal.AllocHGlobal(144);
            try {
                Marshal.Copy(new byte[144], 0, limits, 144);
                Marshal.WriteInt32(limits, 16, 0x2000); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                Check(SetInformationJobObject(handle, 9, limits, 144), "Owned outer job contains failed-fixture descendants without changing security policy");
            } finally { Marshal.FreeHGlobal(limits); }
        }
        internal void Assign(Process process)
        { Check(AssignProcessToJobObject(handle, process.Handle), "Exact native process joins independent fixture job before any Allow21 grant"); }
        internal void Assign(IntPtr process)
        { Check(AssignProcessToJobObject(handle, process), "Exact suspended native process joins the independent fixture job before any code or Allow21 grant"); }
        internal bool Contains(Process process)
        {
            bool assigned;
            Check(IsProcessInJob(process.Handle, handle, out assigned), "Independent outer-job membership is genuinely queried for the exact owned process");
            return assigned;
        }
        internal void AssignIfNeeded(Process process)
        {
            if (!Contains(process)) Check(AssignProcessToJobObject(handle, process.Handle), "Exact approval-route process joins independent outer job before its next fixed grant");
            Check(Contains(process), "Exact approval-route process is contained before its next authorization grant");
        }
        internal uint ActiveProcesses()
        {
            uint active = ReadActiveProcesses();
            Check(true, "Independent job accounting is genuinely queried");
            return active;
        }
        internal uint ReadActiveProcesses()
        {
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Owned outer job is disposed.");
            IntPtr data = Marshal.AllocHGlobal(48);
            try {
                if (!QueryInformationJobObject(handle, 1, data, 48, IntPtr.Zero))
                    throw new InvalidOperationException("Independent job accounting is genuinely queried");
                return unchecked((uint)Marshal.ReadInt32(data, 40));
            } finally { Marshal.FreeHGlobal(data); }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
}
