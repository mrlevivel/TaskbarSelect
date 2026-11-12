using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

// Disposable hosted I/O probe only. It does not launch an app or helper, create
// a scheduled task, change a token/security policy, or claim protocol/UAC proof.
// Its sole authority is the host's pre-existing HIGH token. AccessCheck uses an
// independently measured actual OS-linked MED token, never fabricated metadata.
internal static class PersistentMaintenanceAdapterProbe
{
    internal static bool ProductionVersion(string version)
    { return version == "1.0.0-beta.12" || version == "1.0.0-beta.13" || version == "1.0.0-beta.14" || version == "1.0.0-beta.15" || version == "1.0.0-beta.16" || version == "1.0.0-beta.17" || version == "1.0.0-beta.18" || version == "1.0.0-beta.19" || version == "1.0.0-beta.20"; }
    static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static Type persistent, cacheType, ledgerType;
    static int checks, phase;
    static string scope, sid;
    static readonly List<string> cases = new List<string>();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(SafeFileHandle token, int kind, IntPtr value, uint length, out uint returned);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(SafeFileHandle existing, uint access, IntPtr security, int level, int kind, out SafeFileHandle duplicate);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributesW(string path);

    static void Check(bool value, string label)
    { checks++; if (!value) throw new InvalidOperationException(label); }
    static Exception Actual(Exception value)
    { while (value is TargetInvocationException && value.InnerException != null) value = value.InnerException; return value; }
    static object Invoke(MethodInfo method, object target, params object[] args)
    { try { return method.Invoke(target, args); } catch (Exception error) { throw Actual(error); } }
    static object Fixed(string name, params object[] args)
    { return Invoke(persistent.GetMethod(name, Static), null, args); }
    static object Call(object value, string name, params object[] args)
    { return Invoke(value.GetType().GetMethod(name, Instance), value, args); }
    static object Property(object value, string name)
    { return value.GetType().GetProperty(name, Instance).GetValue(value, null); }
    static void Close(object value) { if (value != null) ((IDisposable)value).Dispose(); }
    static string Hash(byte[] bytes)
    { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static object Ledger(byte[] bytes, string controller)
    {
        var value = Activator.CreateInstance(ledgerType, true);
        foreach (var entry in new Dictionary<string, string> { { "Scope", scope }, { "Sid", sid }, { "ControllerHash", controller }, { "NativeHash", Hash(bytes) } })
            ledgerType.GetField(entry.Key, Instance).SetValue(value, entry.Value);
        return value;
    }
    static object Open(SafeFileHandle medium, bool create)
    { return Invoke(cacheType.GetMethod("Open", Static), null, scope, sid, create, medium); }
    static string State(object cache) { return Property(cache, "State").ToString(); }
    static uint TokenNumber(SafeFileHandle token, int kind)
    {
        // TokenElevation requires exactly sizeof(TOKEN_ELEVATION), not an
        // oversized scratch buffer (Windows returns ERROR_BAD_LENGTH otherwise).
        uint capacity = kind == 25 ? 8192u : 4u;
        var memory = Marshal.AllocHGlobal((int)capacity);
        try {
            uint returned;
            if (!GetTokenInformation(token, kind, memory, capacity, out returned)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (kind == 25) {
                if (returned < IntPtr.Size + 4 || returned > 8192) throw new IOException("Invalid actual integrity token length.");
                IntPtr label = Marshal.ReadIntPtr(memory); byte count = Marshal.ReadByte(GetSidSubAuthorityCount(label));
                if (count < 1 || count > 15) throw new IOException("Invalid actual integrity SID.");
                return unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(label, (uint)count - 1)));
            }
            if (returned != 4) throw new IOException("Invalid actual scalar token length.");
            return unchecked((uint)Marshal.ReadInt32(memory));
        } finally { Marshal.FreeHGlobal(memory); }
    }
    static string TokenSid(SafeFileHandle token)
    { using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User.Value; }
    static long TokenAuthentication(SafeFileHandle token)
    {
        IntPtr value = Marshal.AllocHGlobal(56);
        try { uint size; if (!GetTokenInformation(token, 10, value, 56, out size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Check(size == 56, "Actual token statistics size is exact."); return Marshal.ReadInt64(value, 8); }
        finally { Marshal.FreeHGlobal(value); }
    }
    static SafeFileHandle LinkedMedium(SafeFileHandle high, SafeFileHandle sourceMedium, Dictionary<string, object> result)
    {
        IntPtr memory = Marshal.AllocHGlobal(IntPtr.Size);
        SafeFileHandle linked = null, duplicate = null;
        try {
            uint returned;
            if (!GetTokenInformation(high, 19, memory, (uint)IntPtr.Size, out returned)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (returned != IntPtr.Size) throw new IOException("Invalid actual linked-token length.");
            linked = new SafeFileHandle(Marshal.ReadIntPtr(memory), true);
            if (linked.IsInvalid) throw new IOException("The host has no actual linked token.");
            uint level = TokenNumber(linked, 25), elevated = TokenNumber(linked, 20);
            bool sameUser = TokenSid(linked) == sid;
            result["linkedIntegrity"] = level; result["linkedElevated"] = elevated; result["linkedSameUser"] = sameUser;
            if (level != 8192 || elevated != 0 || !sameUser) throw new InvalidOperationException("No actual same-user MED linked token is available.");
            // Hosted runas may expose only an identification-level linked
            // context. Use the retained actual MED sponsor primary when the
            // authenticated launcher supplies it, while independently proving
            // its exact linked AuthenticationId, role, SID and actual session.
            SafeFileHandle source = sourceMedium ?? linked;
            if (sourceMedium != null) Check(TokenNumber(sourceMedium, 8) == 1 && TokenNumber(sourceMedium, 25) == 8192 &&
                TokenNumber(sourceMedium, 20) == 0 && TokenSid(sourceMedium) == sid &&
                TokenNumber(sourceMedium, 12) == TokenNumber(high, 12) && TokenAuthentication(sourceMedium) == TokenAuthentication(linked),
                "The retained genuine MED sponsor primary matches the HIGH linked context and actual session.");
            if (!DuplicateTokenEx(source, 8, IntPtr.Zero, 2, 2, out duplicate)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Check(TokenNumber(duplicate, 25) == 8192 && TokenNumber(duplicate, 20) == 0 && TokenNumber(duplicate, 8) == 2 && TokenSid(duplicate) == sid,
                "The effective AccessCheck token is an actual same-user MED impersonation token.");
            var retained = duplicate; duplicate = null; return retained;
        } finally { if (duplicate != null) duplicate.Dispose(); if (linked != null) linked.Dispose(); Marshal.FreeHGlobal(memory); }
    }
    static void AssertReadLeaseBlocks(SafeFileHandle medium, string folder, string leaf, bool directory)
    {
        SafeFileHandle lease = (SafeFileHandle)Fixed("OpenNt", IntPtr.Zero, directory ? folder : folder + "\\" + leaf, directory, false, null, false);
        using (lease) {
            object unexpected = null; bool blocked = false;
            try { unexpected = Open(medium, false); }
            catch (Exception error) {
                var field = error.GetType().GetField("Status", Instance);
                blocked = field != null && (int)field.GetValue(error) == unchecked((int)0xC0000043);
            } finally { Close(unexpected); }
            Check(blocked, "An existing ordinary read lease must prevent maintenance with STATUS_SHARING_VIOLATION.");
        }
        object verified = null;
        try { verified = Open(medium, false); Check(State(verified) == "Complete", "A refused maintenance lease preserves the complete approved pair."); }
        finally { Close(verified); }
    }
    static void Adapter(SafeFileHandle medium)
    {
        // No executable PE is needed for a non-executing file-identity adapter
        // test. These four owned bytes are hashed and never loaded as code.
        byte[] bytes = new byte[] { 0x4d, 0x5a, 0x31, 0x32 };
        string controller = new string('a', 64), folder = null;
        object cache = null;
        try {
            cache = Open(medium, true); Check(State(cache) == "Empty", "A fresh owned scope must be empty.");
            Call(cache, "Create", Ledger(bytes, controller), bytes, (Action)delegate { });
            Check(State(cache) == "Complete" && Property(cache, "Action") != null, "The exact production adapter writes a protected bound pair and physical action.");
            folder = (string)cacheType.GetField("folder", Instance).GetValue(cache);
        } finally { Close(cache); cache = null; }
        cases.Add("protected-pair-create");
        AssertReadLeaseBlocks(medium, folder, "", true); cases.Add("scope-read-lease-refusal");
        AssertReadLeaseBlocks(medium, folder, "AdministratorBootstrap.exe", false); cases.Add("native-read-lease-refusal");
        AssertReadLeaseBlocks(medium, folder, "registration.bin", false); cases.Add("ledger-read-lease-refusal");
        try {
            cache = Open(medium, false); Call(cache, "DeleteNative");
            Check(State(cache) == "LedgerOnly" && Property(cache, "Action") == null && Property(cache, "Registration") != null,
                "Actual handle-relative native deletion retains its authenticated ledger and clears the obsolete action.");
            // Simulate an interruption before ledger deletion by closing only our
            // own leases. No task has been registered or run by this probe.
        } finally { Close(cache); cache = null; }
        cases.Add("native-delete-ledger-retained");
        try {
            cache = Open(medium, false); Check(State(cache) == "LedgerOnly", "A fresh exact adapter accepts ledger-only retained recovery state.");
            Call(cache, "DeleteLedger"); Call(cache, "ConfirmEmpty");
            Check(State(cache) == "Empty" && Property(cache, "Registration") == null && Property(cache, "Action") == null,
                "Actual ledger deletion proves the protected scope empty.");
        } finally { Close(cache); cache = null; }
        cases.Add("ledger-only-retry-delete");
        int approvalChecks = 0; bool creationInterrupted = false;
        try {
            cache = Open(medium, false);
            try { Call(cache, "Create", Ledger(bytes, controller), bytes, (Action)delegate { if (++approvalChecks == 3) throw new UnauthorizedAccessException("Owned test approval expiry."); }); }
            catch (UnauthorizedAccessException) { creationInterrupted = true; }
            Check(creationInterrupted && approvalChecks == 3, "Expired approval interrupts creation after the ownership ledger, before native bytes.");
        } finally { Close(cache); cache = null; }
        try {
            cache = Open(medium, false); Check(State(cache) == "LedgerOnly", "Interrupted ledger-first creation preserves a valid retry record.");
            Call(cache, "DeleteLedger"); Call(cache, "ConfirmEmpty");
        } finally { Close(cache); cache = null; }
        cases.Add("interrupted-ledger-first-create-retry");
        try {
            cache = Open(medium, false); Call(cache, "ConfirmEmpty");
            // A newly approved controller hash can occupy the same legitimate
            // user/profile scope after full revocation; no old bytes are reused.
            bytes[3] = 0x33;
            Call(cache, "Create", Ledger(bytes, new string('b', 64)), bytes, (Action)delegate { });
            Check(State(cache) == "Complete", "A fully revoked scope permits a new exact bound build.");
        } finally { Close(cache); cache = null; }
        // Create verifies the new pair through ordinary read leases. Product
        // revocation later opens a fresh maintenance transaction to acquire its
        // exclusive DELETE handles; the creation transaction is not mutable.
        try {
            cache = Open(medium, false);
            Check(State(cache) == "Complete" && Property(cache, "Action") != null &&
                (string)ledgerType.GetField("ControllerHash", Instance).GetValue(Property(cache, "Registration")) == new string('b', 64) &&
                (string)ledgerType.GetField("NativeHash", Instance).GetValue(Property(cache, "Registration")) == Hash(bytes),
                "A fresh maintenance transaction reacquires the exact newly approved pair before revocation.");
            Call(cache, "DeleteNative"); Call(cache, "DeleteLedger"); Call(cache, "ConfirmEmpty");
        } finally { Close(cache); cache = null; }
        cases.Add("same-scope-new-build-and-empty");
    }
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("RUNNER_OS") != "Windows" ||
            Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/codex/persistent-admin-support") return 2;
        return RunCore(args, Environment.GetEnvironmentVariable("GITHUB_SHA"), false, null);
    }
    // Called only by the fixture's HIGH callback after its authenticated hosted
    // sponsor/runas grant. The HIGH process need not inherit GitHub environment
    // variables; it independently measures the real HIGH and linked MED tokens.
    internal static int RunAuthenticatedCi(string app, string receipt, string source, IntPtr actualMediumPrimary)
    { using (var token = new SafeFileHandle(actualMediumPrimary, false)) return RunCore(new[] { app, receipt }, source, true, token); }
    static int RunCore(string[] args, string sourceSha, bool production, SafeFileHandle sourceMedium)
    {
        checks = 0; phase = 0; cases.Clear();
        var result = new Dictionary<string, object> { { "status", "failed" }, { "adapterIoOnly", true }, { "productionAuthenticationTested", false },
            { "taskSchedulerTested", false }, { "uacTested", false }, { "installedAppChanged", false }, { "securityPolicyChanged", false }, { "cases", cases } };
        int exit = 1;
        try {
            phase = 1;
            Check(args.Length == 2 && Environment.Is64BitProcess && sourceSha != null && System.Text.RegularExpressions.Regex.IsMatch(sourceSha, "^[a-f0-9]{40}$"), "Require the exact admitted disposable source.");
            string app = Path.GetFullPath(args[0]);
            Check(File.Exists(app) && (production ? ProductionVersion(FileVersionInfo.GetVersionInfo(app).ProductVersion) : FileVersionInfo.GetVersionInfo(app).ProductVersion == "1.0.0-beta.12-persistent-test"), "Require the exact admitted application version.");
            result["appSha256"] = Hash(File.ReadAllBytes(app)); result["sourceSha"] = sourceSha;
            phase = 2; SafeFileHandle high;
            if (!OpenProcessToken(GetCurrentProcess(), 8, out high)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using (high) {
                sid = TokenSid(high); uint level = TokenNumber(high, 25), elevated = TokenNumber(high, 20);
                result["hostIntegrity"] = level; result["hostElevated"] = elevated;
                Check(level == 12288 && elevated == 1, "Require the host's pre-existing actual HIGH token.");
                phase = 3;
                using (var medium = LinkedMedium(high, sourceMedium, result)) {
                    phase = 4;
                    string cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TaskbarSelect.Persistent");
                    uint attributes = GetFileAttributesW(cacheRoot); int error = Marshal.GetLastWin32Error();
                    Check(attributes == UInt32.MaxValue && (error == 2 || error == 3), "A pre-existing or inaccessible protected cache blocks the fresh adapter test.");
                    Check(Process.GetProcessesByName("TaskbarSelect").Length == 0 && Process.GetProcessesByName("AdministratorPayload").Length == 0 && Process.GetProcessesByName("AdministratorBootstrap").Length == 0,
                        "No live app or administrator helper may exist on the disposable host.");
                    persistent = Assembly.LoadFile(app).GetType("TaskbarSelect.PersistentAdministrator", true);
                    cacheType = persistent.GetNestedType("MaintenanceCache", BindingFlags.NonPublic);
                    ledgerType = persistent.GetNestedType("Ledger", BindingFlags.NonPublic);
                    Check(cacheType != null && ledgerType != null, "Exact private production adapter types must be present.");
                    string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "TaskbarSelectMaintenanceAdapter." + Guid.NewGuid().ToString("N"), "profile");
                    scope = (string)Fixed("ComputeScope", sid, profile, true); result["ownedScope"] = scope;
                    phase = 5; Adapter(medium);
                    result["status"] = "passed"; result["ownedFixedFilesRemoved"] = true; result["ownedEmptyDirectoriesRetained"] = true; exit = 0;
                }
            }
        } catch (Exception error) {
            error = Actual(error); result["status"] = phase == 2 || phase == 3 ? "blocked" : "failed";
            result["errorType"] = error.GetType().Name; result["hresult"] = "0x" + unchecked((uint)error.HResult).ToString("X8", CultureInfo.InvariantCulture);
            var native = error as System.ComponentModel.Win32Exception;
            if (native != null) result["win32"] = native.NativeErrorCode;
            var field = error.GetType().GetField("Status", Instance);
            if (field != null) result["ntstatus"] = "0x" + unchecked((uint)(int)field.GetValue(error)).ToString("X8", CultureInfo.InvariantCulture);
            result["error"] = error.Message;
        } finally {
            result["phase"] = phase; result["checks"] = checks;
            string json = new JavaScriptSerializer().Serialize(result);
            if (args.Length == 2) File.WriteAllText(args[1], json, new UTF8Encoding(false));
            Console.WriteLine(json);
        }
        return exit;
    }
}
