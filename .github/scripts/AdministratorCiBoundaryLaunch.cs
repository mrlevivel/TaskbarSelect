using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

// Test-only adapter around the measured credential sponsor / Windows runas chain.
// It is never compiled into Taskbar Select or its administrator payload.
internal static partial class AdministratorCiLaunchProbe
{
    internal static readonly string[] BoundaryScenarios = { "move-placement-undo", "absent-boot-grant", "absent-resume", "absent-authorization", "bad-sequence", "late-authorization", "parent-death", "unresponsive-owned-target", "active-worker-parent-death", "approval-host-route" };
    internal sealed class CaseIdentity { internal uint Id; internal long Started; }
    static bool boundaryMode;
    static string boundaryInputApp, boundaryApp, boundaryRuntime, boundarySourceSha, boundaryEvidence;
    static Token boundaryMedium;
    static Func<PipeStream, string, string, uint, long, string, int> boundaryRunner;
    // Optional fixed lifecycle-fixture assets. These callbacks are null in the
    // original administrator and persistent protocol fixtures.
    static Action<string> boundaryStageExtension = null, boundaryRemoveExtension = null, boundaryCopyExtension = null;
    static int boundaryReceiveTimeout = 90000;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string file, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inherit, uint flags, IntPtr environment, string directory, ref StartInfo startup, out ProcessInfo process);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    internal static bool TryBoundaryEntry(string[] args, Func<PipeStream, string, string, uint, long, string, int> runner, out int result)
    {
        result = 0;
        bool root = args.Length == 3 && args[0] == "--run-ci";
        bool sponsor = args.Length == 8 && args[0] == "--sponsor-boundary";
        bool elevated = args.Length == 10 && args[0] == "--high-boundary";
        if (!root && !sponsor && !elevated) return false;
        boundaryMode = true; boundaryRunner = runner; highRole = elevated; highPhase = 1;
        try {
            Require(runner != null);
            if (elevated) Require(Environment.Is64BitProcess); else Hosted();
            selfPath = Assembly.GetExecutingAssembly().Location;
            using (var image = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                selfHash = Hash(image);
                if (!root) {
                    int offset = sponsor ? 5 : 7;
                    boundaryApp = args[offset]; boundaryRuntime = args[offset + 1]; boundarySourceSha = args[offset + 2];
                    VerifyBoundaryPaths();
                    result = sponsor ? Sponsor(args) : HighProof(args); return true;
                }
                boundaryInputApp = Path.GetFullPath(args[1]); boundaryEvidence = Path.GetFullPath(args[2]);
                boundarySourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
                Require(SourceSha(boundarySourceSha) && Path.GetFileName(boundaryInputApp) == "TaskbarSelect.exe" &&
                    File.Exists(boundaryInputApp) && !Directory.Exists(boundaryEvidence) && !File.Exists(boundaryEvidence));
                Facts facts = Run();
                Directory.CreateDirectory(boundaryEvidence);
                File.WriteAllText(Path.Combine(boundaryEvidence, "launcher.json"), new JavaScriptSerializer().Serialize(facts), new UTF8Encoding(false));
                CopyBoundaryEvidence(facts.Probe);
                bool clean = facts.Probe != null && facts.Probe.Status == "passed" && facts.Probe.Drained &&
                    facts.Probe.ProtectedSourceRemoved && facts.AccountRemoved && !facts.CleanupUncertain;
                if (!File.Exists(Path.Combine(boundaryEvidence, "result.json"))) {
                    File.WriteAllText(Path.Combine(boundaryEvidence, "result.json"), new JavaScriptSerializer().Serialize(new {
                        status = "blocked", reason = "Genuine CI sponsor chain did not complete; see numeric launcher evidence.",
                        checks = 0, sourceSha = boundarySourceSha, appSha256 = FileHashForBoundary(boundaryInputApp),
                        version = System.Diagnostics.FileVersionInfo.GetVersionInfo(boundaryInputApp).ProductVersion,
                        cases = new object[0], scope = "Disposable genuine CI account and authenticated sponsor chain only"
                    }), new UTF8Encoding(false));
                }
                result = clean ? 0 : 3; return true;
            }
        } catch (NativeFailure error) { result = elevated ? Refusal(error.Code, false) : error.Code == 0 ? 2 : error.Code; return true; }
          catch (Exception error) { Console.Error.WriteLine("Boundary launcher failure hr=0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture)); result = elevated ? Refusal(error.HResult, true) : 2; return true; }
    }
    static bool SourceSha(string value) { return value != null && value.Length == 40 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
    static string FileHashForBoundary(string path) { using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return Hash(file); }
    static void VerifyBoundaryPaths()
    {
        string source = Path.GetDirectoryName(selfPath), leaf = Path.GetFileName(source);
        Require(leaf.StartsWith("TaskbarSelectCiSource.", StringComparison.Ordinal) && FreshNonce(leaf.Substring("TaskbarSelectCiSource.".Length)));
        Require(String.Equals(boundaryApp, Path.Combine(source, "TaskbarSelect.exe"), StringComparison.OrdinalIgnoreCase) &&
            String.Equals(boundaryRuntime, Path.Combine(Path.GetDirectoryName(source), leaf.Replace("TaskbarSelectCiSource.", "TaskbarSelectCiRuntime.")), StringComparison.OrdinalIgnoreCase) && SourceSha(boundarySourceSha));
        foreach (string path in new[] { source, boundaryRuntime }) Require(Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
    }
    static DirectorySecurity RuntimeSecurity(string sid, bool owned)
    {
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(owned ? sid : "S-1-5-32-544"));
        foreach (string who in new[] { "S-1-5-18", "S-1-5-32-544", sid }) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(who), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }
    sealed class BoundaryAsset : IDisposable
    {
        internal string AppPath; FileStream input, copied;
        internal BoundaryAsset(string sid, string source, ProbeFacts facts)
        {
            AppPath = Path.Combine(source, "TaskbarSelect.exe"); boundaryApp = AppPath;
            boundaryRuntime = Path.Combine(Path.GetDirectoryName(source), Path.GetFileName(source).Replace("TaskbarSelectCiSource.", "TaskbarSelectCiRuntime."));
            Require(!Directory.Exists(boundaryRuntime) && !File.Exists(boundaryRuntime));
            input = new FileStream(boundaryInputApp, FileMode.Open, FileAccess.Read, FileShare.Read);
            try {
                string expected = Hash(input); input.Position = 0;
                using (var output = new FileStream(AppPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) input.CopyTo(output);
                copied = new FileStream(AppPath, FileMode.Open, FileAccess.Read, FileShare.Read); Require(Hash(copied) == expected);
                Directory.CreateDirectory(boundaryRuntime, RuntimeSecurity(sid, false));
                facts.BoundaryRuntimePath = boundaryRuntime; facts.BoundaryAppSha256 = expected;
                if (boundaryStageExtension != null) boundaryStageExtension(source);
            } catch { Dispose(); throw; }
        }
        public void Dispose() { if (copied != null) { copied.Dispose(); copied = null; } if (input != null) { input.Dispose(); input = null; } }
    }
    static void PrepareBoundaryRuntime(string sid)
    {
        VerifyBoundaryPaths(); Require(boundarySourceSha == Environment.GetEnvironmentVariable("GITHUB_SHA") && Directory.GetFileSystemEntries(boundaryRuntime).Length == 0);
        Directory.SetAccessControl(boundaryRuntime, RuntimeSecurity(sid, true));
        var actual = Directory.GetAccessControl(boundaryRuntime);
        Require(actual.AreAccessRulesProtected && ((SecurityIdentifier)actual.GetOwner(typeof(SecurityIdentifier))).Value == sid);
    }
    internal static CaseIdentity RequestBoundaryCase(PipeStream pipe, int index, string nonce, IntPtr window)
    {
        Require(boundaryMode && index >= 0 && index < BoundaryScenarios.Length && FreshNonce(nonce) && window != IntPtr.Zero);
        Write(pipe, 81, index, unchecked((long)UInt64.Parse(nonce.Substring(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            unchecked((long)UInt64.Parse(nonce.Substring(16), NumberStyles.HexNumber, CultureInfo.InvariantCulture)), window.ToInt64());
        Packet ready = Receive(pipe); Expected(ready, 82, 2); Require(ready.Values[0] > 0 && ready.Values[0] <= UInt32.MaxValue && ready.Values[1] > 0);
        return new CaseIdentity { Id = (uint)ready.Values[0], Started = ready.Values[1] };
    }
    internal static bool VerifyBoundaryCase(CaseIdentity identity)
    {
        try { Require(boundaryMode && boundaryMedium != null && identity != null);
            using (var child = Life.Open(identity.Id, identity.Started)) {
                child.Pin(selfPath); IntPtr token = IntPtr.Zero;
                try { return !child.Exited(0) && CloneMatches(ProcessToken(child, 8, out token), boundaryMedium); }
                finally { if (token != IntPtr.Zero) CloseHandle(token); }
            }
        } catch { return false; }
    }
    sealed class BoundaryChild : IDisposable
    {
        internal Life Process; internal Job Job;
        internal bool Drain() { bool zero = Job.Drain(); return zero && Process.Exited(1000); }
        public void Dispose() { if (Process != null) Process.Dispose(); if (Job != null) Job.Dispose(); }
    }
    static Packet ReceiveBoundaryProof(PipeStream pipe, Token own, uint highPid, long highStarted)
    {
        var children = new List<BoundaryChild>(); var nonces = new HashSet<string>(StringComparer.Ordinal); bool unknown = false;
        try {
            for (;;) {
                Packet request = Receive(pipe, boundaryReceiveTimeout);
                if (request.Kind == 75) { Expected(request, 75, 15); return request; }
                Expected(request, 81, 4); int index = checked((int)request.Values[0]);
                string nonce = unchecked((ulong)request.Values[1]).ToString("x16", CultureInfo.InvariantCulture) + unchecked((ulong)request.Values[2]).ToString("x16", CultureInfo.InvariantCulture);
                uint owner; IntPtr window = new IntPtr(request.Values[3]);
                Require(BoundaryRequestShape(children.Count, index, nonce, window.ToInt64()) && nonces.Add(nonce) && GetWindowThreadProcessId(window, out owner) != 0 && owner == highPid);
                foreach (var previous in children) Require(Wait(delegate { return previous.Process.Exited(0) && previous.Job.Active() == 0; }, 1000));
                ProcessInfo created = new ProcessInfo(); BoundaryChild lease = null;
                try {
                    string command = String.Join(" ", new[] { selfPath, "--medium", boundaryApp, boundaryRuntime, nonce, BoundaryScenarios[index], highPid.ToString(CultureInfo.InvariantCulture), highStarted.ToString(CultureInfo.InvariantCulture), window.ToInt64().ToString(CultureInfo.InvariantCulture) }.Select(Quote));
                    Require(command.Length <= 4096); var startup = new StartInfo { Size = Marshal.SizeOf(typeof(StartInfo)) };
                    if (!CreateProcessW(selfPath, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero, Path.GetDirectoryName(selfPath), ref startup, out created)) { int error = Last(); unknown = PossibleChild(created); throw new NativeFailure(error); }
                    lease = new BoundaryChild(); lease.Process = Life.Open(created.Pid, Started(created.Process)); lease.Job = new Job(new ProbeFacts());
                    lease.Process.Pin(selfPath); IntPtr token = IntPtr.Zero;
                    try { Require(CloneMatches(ProcessToken(lease.Process, 8, out token), own)); } finally { if (token != IntPtr.Zero) CloseHandle(token); }
                    lease.Job.Add(created.Process); children.Add(lease); lease = null;
                    if (ResumeThread(created.Thread) == UInt32.MaxValue) throw new NativeFailure(Last());
                    Write(pipe, 82, created.Pid, children[children.Count - 1].Process.Start);
                } finally {
                    if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
                    if (created.Process != IntPtr.Zero) {
                        if (lease != null || !children.Any(x => x.Process.Pid == created.Pid)) { bool exited = (TerminateProcess(created.Process, 91) || WaitForSingleObject(created.Process, 0) == 0) && WaitForSingleObject(created.Process, 4000) == 0; unknown = !exited; }
                        CloseHandle(created.Process);
                    }
                    if (lease != null) lease.Dispose();
                }
            }
        } finally {
            foreach (var child in children) { try { if (!child.Drain()) unknown = true; } catch { unknown = true; } finally { child.Dispose(); } }
            if (unknown) throw new NativeFailure(113);
        }
    }
    static bool BoundaryRequestShape(int expected, int index, string nonce, long window)
    { return expected >= 0 && expected < BoundaryScenarios.Length && index == expected && FreshNonce(nonce) && window != 0; }
    static void ObserveReverseLinkedHigh(IntPtr source, Token medium, Token high)
    {
        IntPtr linked = IntPtr.Zero, data = Marshal.AllocHGlobal(IntPtr.Size);
        Token context = null; int windowsError = 0; int? managedHResult = null;
        try {
            int size; Marshal.WriteIntPtr(data, IntPtr.Zero);
            if (!GetTokenInformation(source, 19, data, IntPtr.Size, out size)) windowsError = Last();
            else if (size != IntPtr.Size || (linked = Marshal.ReadIntPtr(data)) == IntPtr.Zero) windowsError = 13;
            else context = Read(linked);
        } catch (NativeFailure error) { windowsError = error.Code; }
          catch (Exception error) { managedHResult = error.HResult; }
        finally { if (linked != IntPtr.Zero) CloseHandle(linked); Marshal.FreeHGlobal(data); }
        byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(new {
            schema = "taskbar-select-reverse-linked-high-observation-v1", diagnosticOnly = true,
            authorizationPredicateChanged = false, sourceMediumSession = medium.Session, actualHighSession = high.Session,
            linkedHighRawSession = context == null ? (uint?)null : context.Session,
            linkedHighType = context == null ? (int?)null : context.Type,
            linkedHighSameSid = context == null ? (bool?)null : context.Sid == medium.Sid,
            linkedHighRole = context == null ? (bool?)null : High(context),
            linkedHighAuthenticationMatchesActualHigh = context == null ? (bool?)null : context.Authentication == high.Authentication,
            windowsError = windowsError, managedHResult = managedHResult
        }));
        Require(bytes.Length <= 4096);
        using (var file = new FileStream(Path.Combine(boundaryRuntime, "linked-high-observation.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            file.Write(bytes, 0, bytes.Length);
    }
    static void CopyBoundaryEvidence(ProbeFacts facts)
    {
        if (facts == null || !facts.Drained || facts.BoundaryRuntimePath == null || !Directory.Exists(facts.BoundaryRuntimePath)) return;
        foreach (string name in new[] { "result.json", "stdout.log", "stderr.log", "linked-high-observation.json" }) {
            string path = Path.Combine(facts.BoundaryRuntimePath, name); if (!File.Exists(path)) continue;
            Require(new FileInfo(path).Length <= 262144); File.Copy(path, Path.Combine(boundaryEvidence, name), false);
        }
        string[] recovery = Directory.GetFiles(facts.BoundaryRuntimePath, "owned-recovery-*.json"); Require(recovery.Length <= BoundaryScenarios.Length);
        foreach (string path in recovery) { Require(new FileInfo(path).Length <= 65536); File.Copy(path, Path.Combine(boundaryEvidence, Path.GetFileName(path)), false); }
        string[] mediumLogs = Directory.GetFiles(facts.BoundaryRuntimePath, "owned-medium-*.stdout.log")
            .Concat(Directory.GetFiles(facts.BoundaryRuntimePath, "owned-medium-*.stderr.log")).ToArray();
        Require(mediumLogs.Length <= BoundaryScenarios.Length * 2);
        foreach (string path in mediumLogs) { Require(OwnedMediumLogName(Path.GetFileName(path)) && new FileInfo(path).Length <= 262144); File.Copy(path, Path.Combine(boundaryEvidence, Path.GetFileName(path)), false); }
        if (boundaryCopyExtension != null) boundaryCopyExtension(facts.BoundaryRuntimePath);
    }
    static bool OwnedMediumLogName(string name)
    {
        const string prefix = "owned-medium-";
        return name != null && name.Length == prefix.Length + 32 + 11 && name.StartsWith(prefix, StringComparison.Ordinal) &&
            FreshNonce(name.Substring(prefix.Length, 32)) &&
            (name.EndsWith(".stdout.log", StringComparison.Ordinal) || name.EndsWith(".stderr.log", StringComparison.Ordinal));
    }
}
