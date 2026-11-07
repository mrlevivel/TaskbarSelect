using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// CI-only adapter around the existing genuine temporary-account sponsor chain.
// No production token, installer admission or permission policy is changed.
internal static class InstallerCiFixture
{
    [STAThread] static int Main(string[] args) { return AdministratorCiLaunchProbe.InstallerMain(args); }
}

internal static partial class AdministratorCiLaunchProbe
{
    static readonly string[] InstallerCommonInputs = {
        ".github/scripts/Invoke-InstallerCiChild.ps1", ".github/scripts/Invoke-OwnedCompiler.ps1", ".github/scripts/Test-Installer.ps1",
        ".github/scripts/Installer-Guards.ps1", ".github/scripts/Test-InstallerGuards.ps1",
        ".github/scripts/Test-InstallerPolicy.ps1", ".github/scripts/InstallerLifecyclePolicy.iss",
        ".github/scripts/Test-RecoveryGuards.ps1", ".github/scripts/Test-InstallerRefusal.ps1",
        ".github/scripts/Package-Guards.ps1", "InstallerLifecycle.iss", "installer.iss",
        "docs/package/README.md", "RELEASE_NOTES.md", "TESTING.md", "SECURITY.md",
        "CHANGELOG.md", "LICENSE", "assets/ARTWORK.md", "artifacts/TaskbarSelect.exe",
        "dist/SHA256SUMS.txt"
    };
    static string InstallerVersion(string app)
    {
        string version = FileVersionInfo.GetVersionInfo(app).ProductVersion;
        Require(InstallerProductionVersion(version)); return version;
    }
    static bool InstallerProductionVersion(string version)
    { return version == "1.0.0-beta.12" || version == "1.0.0-beta.13" || version == "1.0.0-beta.14" || version == "1.0.0-beta.15" || version == "1.0.0-beta.16" || version == "1.0.0-beta.17"; }
    static string InstallerPreviousVersion(string version)
    {
        Require(InstallerProductionVersion(version));
        return version == "1.0.0-beta.12" ? "1.0.0-beta.11" :
            version == "1.0.0-beta.13" ? "1.0.0-beta.12" :
            version == "1.0.0-beta.14" ? "1.0.0-beta.13" :
            version == "1.0.0-beta.15" ? "1.0.0-beta.14" :
            version == "1.0.0-beta.16" ? "1.0.0-beta.15" : "1.0.0-beta.16";
    }
    static string[] InstallerInputs(string version)
    {
        Require(InstallerProductionVersion(version));
        return InstallerCommonInputs.Concat(new[] { "dist/TaskbarSelect-" + version + "-Setup.exe" }).ToArray();
    }
    static readonly List<string> installerStaged = new List<string>();
    internal static int InstallerMain(string[] args)
    {
        try {
            boundaryStageExtension = StageInstallerInputs;
            boundaryRemoveExtension = RemoveInstallerInputs;
            boundaryCopyExtension = CopyInstallerEvidence;
            boundaryReceiveTimeout = 170000;
            int result;
            if (TryBoundaryEntry(args, InstallerHigh, out result)) return result;
            Hosted(); selfPath = Assembly.GetExecutingAssembly().Location;
            using (var own = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.Read)) selfHash = Hash(own);
            if (args.Length == 8 && args[0] == "--medium") return InstallerMedium(args);
            return 2;
        } catch (Exception error) { Console.Error.WriteLine("Installer fixture refusal hr=0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture)); return 2; }
    }
    static void SafeInput(string path)
    {
        Require(Path.IsPathRooted(path) && File.Exists(path) && new FileInfo(path).Length <= 64 * 1024 * 1024);
        for (string current = path; current != null; current = Path.GetDirectoryName(current.TrimEnd('\\')))
            Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0);
    }
    static void StageInstallerFile(string source, string root, string relative)
    {
        Require(InstallerRelativeInput(relative));
        SafeInput(source); string target = Path.GetFullPath(Path.Combine(root, relative));
        Require(target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target, false);
        Require(FileHashForBoundary(source) == FileHashForBoundary(target)); installerStaged.Add(target);
    }
    static bool InstallerRelativeInput(string value)
    {
        return !String.IsNullOrEmpty(value) && value.Length <= 240 &&
            value.IndexOfAny(new[] { ':', '"', '\0', '\r', '\n', '*', '?' }) < 0 && !Path.IsPathRooted(value) &&
            value.Split(new[] { '/', '\\' }).All(part => part.Length > 0 && part != "." && part != "..");
    }
    sealed class InstallerPhase
    {
        public string schema = "taskbar-select-installer-phase-v1";
        public string role, phase, failurePhase, errorType;
        public long elapsedMs, waitElapsedMs, cloneBytes;
        public int checks, cloneFiles, cloneTotal;
        public uint? childPid;
        public bool? childExited, stdoutComplete, stderrComplete;
        public int? childExitCode, nativeError, hresult;
    }
    static void WriteInstallerPhase(string runtime, InstallerPhase progress, Stopwatch clock)
    {
        // Numeric observations and fixed phase names only: never account/SID,
        // environment values, paths, arbitrary exception messages or app data.
        Require(progress.role == "high" || progress.role == "medium");
        progress.elapsedMs = clock.ElapsedMilliseconds;
        string path = Path.Combine(runtime, "installer-" + progress.role + "-phase.json");
        if (File.Exists(path)) Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
        string json = new JavaScriptSerializer().Serialize(progress); Require(json.Length <= 4096);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) {
            byte[] bytes = new UTF8Encoding(false).GetBytes(json); file.Write(bytes, 0, bytes.Length); file.Flush(true);
        }
    }
    static void InstallerFailure(InstallerPhase progress, Exception error)
    {
        progress.failurePhase = progress.phase; progress.phase = "failed"; progress.errorType = error.GetType().Name;
        progress.hresult = error.HResult;
        var native = error as NativeFailure; progress.nativeError = native == null ? (int?)null : native.Code;
    }
    static int InstallerWaitSlice(long elapsed, int deadline)
    {
        if (elapsed < 0 || deadline <= 0) throw new ArgumentOutOfRangeException();
        return elapsed >= deadline ? 0 : (int)Math.Min(1000, deadline - elapsed);
    }
    [StructLayout(LayoutKind.Sequential)]
    struct InstallerMessage
    {
        internal IntPtr Window;
        internal uint Id;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool PeekMessage(out InstallerMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref InstallerMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr DispatchMessage(ref InstallerMessage message);
    static bool WaitInstallerMedium(Life child, int deadline)
    {
        var clock = Stopwatch.StartNew(); IntPtr[] handles = { child.Handle };
        for (;;) {
            if (child.Exited(0)) return true;
            int remaining = InstallerWaitSlice(clock.ElapsedMilliseconds, deadline); if (remaining == 0) return false;
            // This HIGH STA owns InstallerTarget. Raw WaitForSingleObject can
            // starve synchronous broadcasts from the child and its installers.
            uint state = MsgWaitForMultipleObjectsEx(1, handles, (uint)remaining, 0x04ff, 4);
            if (state == 0) return true;
            if (state == 258) continue;
            if (state == UInt32.MaxValue) throw new NativeFailure(Last());
            Require(state == 1);
            for (int count = 0; count < 64; count++) {
                InstallerMessage message; if (!PeekMessage(out message, IntPtr.Zero, 0, 0, 1)) break;
                if (message.Id == 0x12) throw new NativeFailure(1223);
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
        }
    }
    internal static int RunInstallerPredicateChecks()
    {
        int count = 0;
        Action<bool> check = delegate(bool value) { Require(value); count++; };
        foreach (string version in new[] { "1.0.0-beta.12", "1.0.0-beta.13", "1.0.0-beta.14", "1.0.0-beta.15", "1.0.0-beta.16", "1.0.0-beta.17" }) {
            string[] inputs = InstallerInputs(version);
            check(inputs.Length == 22 && inputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() == inputs.Length);
            foreach (string relative in inputs) check(InstallerRelativeInput(relative));
            check(inputs.Count(path => path.EndsWith("-Setup.exe", StringComparison.Ordinal)) == 1 && inputs.Contains("dist/TaskbarSelect-" + version + "-Setup.exe"));
            check(!inputs.Any(path => path.IndexOf("profile", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("settings.json", StringComparison.OrdinalIgnoreCase) >= 0));
            check(inputs.Contains(".github/scripts/Test-Installer.ps1") && inputs.Contains(".github/scripts/Test-InstallerRefusal.ps1") &&
                inputs.Contains(".github/scripts/Test-RecoveryGuards.ps1") && inputs.Contains(".github/scripts/Test-InstallerPolicy.ps1"));
            check(PersistentMaintenanceAdapterProbe.ProductionVersion(version));
        }
        check(InstallerPreviousVersion("1.0.0-beta.12") == "1.0.0-beta.11");
        check(InstallerPreviousVersion("1.0.0-beta.13") == "1.0.0-beta.12");
        check(InstallerPreviousVersion("1.0.0-beta.14") == "1.0.0-beta.13");
        check(InstallerPreviousVersion("1.0.0-beta.15") == "1.0.0-beta.14");
        check(InstallerPreviousVersion("1.0.0-beta.16") == "1.0.0-beta.15");
        check(InstallerPreviousVersion("1.0.0-beta.17") == "1.0.0-beta.16");
        foreach (string badVersion in new[] { "", "1.0.0-beta.11", "1.0.0-beta.18", "1.0.0-beta.013", "1.0.0-beta.015", "1.0.0-beta.016", "1.0.0-beta.017", "1.0.0-beta.13-test", "1.0.0-beta.14-test", "1.0.0-beta.15-test", "1.0.0-beta.16-test", "1.0.0-beta.17-test", "1.0.0-beta.15-persistent-test", "1.0.0-beta.12-persistent-test", "1.0.0-BETA.13", "1.0.0-BETA.15", "1.0.0-BETA.16", "1.0.0-BETA.17" }) {
            check(!InstallerProductionVersion(badVersion)); check(!PersistentMaintenanceAdapterProbe.ProductionVersion(badVersion));
            bool refused = false; try { InstallerInputs(badVersion); } catch (NativeFailure) { refused = true; } check(refused);
            refused = false; try { InstallerPreviousVersion(badVersion); } catch (NativeFailure) { refused = true; } check(refused);
        }
        foreach (string bad in new[] { "", "../outside", "a/../outside", "a/./file", "a//file", "/absolute", @"C:\absolute", @"\\server\share", "name:stream", "a*", "a?", "a\nfile", "a\0file", "a\"file", "a/" }) check(!InstallerRelativeInput(bad));
        check(InstallerWaitSlice(0, 160000) == 1000);
        check(InstallerWaitSlice(159000, 160000) == 1000);
        check(InstallerWaitSlice(159999, 160000) == 1);
        check(InstallerWaitSlice(160000, 160000) == 0);
        check(InstallerWaitSlice(Int64.MaxValue, 160000) == 0);
        return count;
    }
    static void StageInstallerInputs(string source)
    {
        string repo = Path.GetDirectoryName(Path.GetDirectoryName(boundaryInputApp));
        string root = Path.Combine(source, "installer-inputs"); Require(!Directory.Exists(root)); Directory.CreateDirectory(root);
        string version = InstallerVersion(boundaryInputApp), previousVersion = InstallerPreviousVersion(version);
        foreach (string relative in InstallerInputs(version)) StageInstallerFile(Path.Combine(repo, relative), root, relative);
        string baseline = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP"), "TaskbarSelect-beta" + previousVersion.Substring("1.0.0-beta.".Length) + "-baseline");
        foreach (string name in new[] { "TaskbarSelect-" + previousVersion + "-Setup.exe", "SHA256SUMS.txt" })
            StageInstallerFile(Path.Combine(baseline, name), root, "baseline/" + name);
        string compiler = Environment.GetEnvironmentVariable("TASKBARSELECT_CI_ISCC");
        Require(compiler != null && Path.GetFileName(compiler).Equals("ISCC.exe", StringComparison.OrdinalIgnoreCase)); SafeInput(compiler);
        string compilerRoot = Path.GetDirectoryName(compiler);
        string[] compilerFiles = Directory.GetFiles(compilerRoot, "*", SearchOption.AllDirectories);
        Require(compilerFiles.Length > 0 && compilerFiles.Length <= 256);
        foreach (string path in compilerFiles) StageInstallerFile(path, root, "compiler/" + path.Substring(compilerRoot.Length + 1));
        Require(installerStaged.Count <= 300);
    }
    static void RemoveInstallerInputs(string source)
    {
        string root = Path.Combine(source, "installer-inputs");
        foreach (string path in installerStaged) { Require(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); SafeInput(path); File.Delete(path); }
        // The fixed inputs are the only created descendants; delete empty owned
        // directories, never recursively erase a computed or failed runtime.
        foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) {
            Require(directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0);
            Directory.Delete(directory, false);
        }
        Directory.Delete(root, false);
    }
    static void CopyInstallerEvidence(string runtime)
    {
        string[] fixedFiles = { "maintenance-adapter.json", "installer-child.stdout.log", "installer-child.stderr.log", "installer-high-phase.json", "installer-medium-phase.json",
            "installer-inputs/artifacts/installer-smoke/result.json", "installer-inputs/artifacts/installer-policy/result.json",
            "installer-inputs/artifacts/installer-policy/policy.log", "installer-inputs/artifacts/installer-policy/metadata-compilers.json" };
        fixedFiles = fixedFiles.Concat(new[] { "legacy", "production", "persistent-test", "malformed", "unicode", "oversize" }
            .SelectMany(name => new[] { "stdout", "stderr" }.Select(channel => "installer-inputs/artifacts/installer-policy/metadata-" + name + "." + channel + ".log"))).ToArray();
        fixedFiles = fixedFiles.Concat(new[] { "metadata-legacy", "metadata-production", "metadata-persistent-test", "metadata-malformed", "metadata-unicode", "metadata-oversize", "pascal-policy", "pascal-production-code" }
            .SelectMany(name => new[] { "json", "stdout.log", "stderr.log" }.Select(suffix => "installer-inputs/artifacts/installer-policy/" + name + "." + suffix)))
            .Concat(new[] { "json", "stdout.log", "stderr.log" }.Select(suffix => "installer-inputs/artifacts/installer-smoke/refusal-fixture." + suffix)).Distinct().ToArray();
        foreach (string relative in fixedFiles) {
            string input = Path.Combine(runtime, relative); if (!File.Exists(input)) continue;
            Require(new FileInfo(input).Length <= 262144 && (File.GetAttributes(input) & FileAttributes.ReparsePoint) == 0);
            string output = Path.Combine(boundaryEvidence, relative); Directory.CreateDirectory(Path.GetDirectoryName(output)); File.Copy(input, output, false);
        }
    }
    sealed class InstallerTarget : NativeWindow, IDisposable
    {
        internal InstallerTarget() { CreateHandle(new CreateParams { Caption = "Owned installer CI identity target", Width = 1, Height = 1, X = -30000, Y = -30000 }); }
        public void Dispose() { DestroyHandle(); }
    }
    static int InstallerHigh(PipeStream sponsor, string app, string runtime, uint sponsorPid, long sponsorStarted, string source)
    {
        int checks = 0; var clock = Stopwatch.StartNew();
        var progress = new InstallerPhase { role = "high", phase = "version" };
        try {
            WriteInstallerPhase(runtime, progress, clock);
            string version = InstallerVersion(app); checks++;
            progress.phase = "maintenance-adapter"; progress.checks = checks; WriteInstallerPhase(runtime, progress, clock);
            IntPtr mediumToken = IntPtr.Zero;
            using (var retainedSponsor = Life.Open(sponsorPid, sponsorStarted)) try {
                retainedSponsor.Pin(selfPath); Require(CloneMatches(ProcessToken(retainedSponsor, 11, out mediumToken), boundaryMedium));
                int adapter = PersistentMaintenanceAdapterProbe.RunAuthenticatedCi(app, Path.Combine(runtime, "maintenance-adapter.json"), source, mediumToken);
                Require(adapter == 0); checks++;
            } finally { if (mediumToken != IntPtr.Zero) CloseHandle(mediumToken); }
            using (var target = new InstallerTarget()) {
                progress.phase = "request-medium"; progress.checks = checks; WriteInstallerPhase(runtime, progress, clock);
                CaseIdentity child = RequestBoundaryCase(sponsor, 0, Guid.NewGuid().ToString("N"), target.Handle);
                Require(VerifyBoundaryCase(child)); checks++;
                using (var owned = Life.Open(child.Id, child.Started)) {
                    progress.childPid = child.Id; progress.phase = "pin-medium"; progress.checks = checks; WriteInstallerPhase(runtime, progress, clock);
                    owned.Pin(selfPath); progress.phase = "wait-medium"; WriteInstallerPhase(runtime, progress, clock);
                    var waitClock = Stopwatch.StartNew(); bool exited = WaitInstallerMedium(owned, 160000);
                    progress.waitElapsedMs = waitClock.ElapsedMilliseconds; progress.childExited = exited;
                    if (exited) progress.childExitCode = owned.Exit();
                    progress.phase = "medium-observed"; WriteInstallerPhase(runtime, progress, clock);
                    Require(exited && progress.childExitCode == 0); checks++;
                }
            }
            progress.phase = "validate-smoke-receipt"; progress.checks = checks; WriteInstallerPhase(runtime, progress, clock);
            string smoke = Path.Combine(runtime, "installer-inputs/artifacts/installer-smoke/result.json");
            Require(File.Exists(smoke) && new FileInfo(smoke).Length <= 65536);
            var receipt = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(smoke));
            Require((string)receipt["status"] == "passed" && (string)receipt["sourceSha"] == source &&
                (string)receipt["version"] == version && (string)receipt["upgradeFrom"] == InstallerPreviousVersion(version) && String.Equals((string)receipt["exeSha256"], FileHashForBoundary(app), StringComparison.OrdinalIgnoreCase) &&
                Convert.ToInt32(receipt["checks"], CultureInfo.InvariantCulture) >= 120); checks++;
            File.WriteAllText(Path.Combine(runtime, "result.json"), new JavaScriptSerializer().Serialize(new {
                status = "passed", sourceSha = source, appSha256 = FileHashForBoundary(app), version = version, checks = checks,
                adapterIoPassed = true, genuineMediumInstallerPassed = true, interactiveUacTested = false, rebootTested = false,
                rememberedApprovalLifecycleExecuted = false, cases = new[] { "production-maintenance-adapter", "production-medium-installer" }
            }), new UTF8Encoding(false));
            progress.phase = "passed"; progress.checks = checks; WriteInstallerPhase(runtime, progress, clock); return 0;
        } catch (Exception error) {
            InstallerFailure(progress, error); progress.checks = checks;
            try { WriteInstallerPhase(runtime, progress, clock); } catch (Exception) { }
            File.WriteAllText(Path.Combine(runtime, "result.json"), new JavaScriptSerializer().Serialize(new {
                status = "failed", sourceSha = source, appSha256 = FileHashForBoundary(app), checks = checks,
                errorType = error.GetType().Name, hresult = "0x" + unchecked((uint)error.HResult).ToString("X8", CultureInfo.InvariantCulture),
                phase = progress.failurePhase, nativeError = progress.nativeError, childExited = progress.childExited, childExitCode = progress.childExitCode,
                interactiveUacTested = false, rebootTested = false, cases = new object[0]
            }), new UTF8Encoding(false)); return 2;
        }
    }
    static int InstallerMedium(string[] args)
    {
        Require(args[4] == BoundaryScenarios[0] && FreshNonce(args[3]));
        string app = Path.GetFullPath(args[1]), runtime = Path.GetFullPath(args[2]);
        string source = Path.GetDirectoryName(selfPath);
        Require(String.Equals(app, Path.Combine(source, "TaskbarSelect.exe"), StringComparison.OrdinalIgnoreCase) &&
            String.Equals(runtime, Path.Combine(Path.GetDirectoryName(source), Path.GetFileName(source).Replace("TaskbarSelectCiSource.", "TaskbarSelectCiRuntime.")), StringComparison.OrdinalIgnoreCase));
        IntPtr current = IntPtr.Zero, elevated = IntPtr.Zero;
        try {
            if (!OpenProcessToken(GetCurrentProcess(), 8, out current)) throw new NativeFailure(Last()); Token own = Read(current);
            Require(own.Type == 1 && Medium(own));
            using (var high = Life.Open(UInt32.Parse(args[5], CultureInfo.InvariantCulture), Int64.Parse(args[6], CultureInfo.InvariantCulture))) {
                high.Pin(selfPath); Require(ActualPair(ProcessToken(high, 8, out elevated), own));
                uint owner; Require(GetWindowThreadProcessId(new IntPtr(Int64.Parse(args[7], CultureInfo.InvariantCulture)), out owner) != 0 && owner == high.Pid);
            }
        } finally { if (elevated != IntPtr.Zero) CloseHandle(elevated); if (current != IntPtr.Zero) CloseHandle(current); }
        string input = Path.Combine(source, "installer-inputs"), clone = Path.Combine(runtime, "installer-inputs");
        var clock = Stopwatch.StartNew(); var progress = new InstallerPhase { role = "medium", phase = "streams-ready" };
        TextWriter previousOutput = Console.Out, previousError = Console.Error;
        using (var output = new StreamWriter(new FileStream(Path.Combine(runtime, "installer-child.stdout.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
        using (var errors = new StreamWriter(new FileStream(Path.Combine(runtime, "installer-child.stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))) {
        output.AutoFlush = true; errors.AutoFlush = true; Console.SetOut(output); Console.SetError(errors);
        try {
        WriteInstallerPhase(runtime, progress, clock);
        Require(!Directory.Exists(clone)); Directory.CreateDirectory(clone);
        string[] files = Directory.GetFiles(input, "*", SearchOption.AllDirectories); Require(files.Length > 0 && files.Length <= 300);
        progress.cloneTotal = files.Length; progress.phase = "clone-inputs"; WriteInstallerPhase(runtime, progress, clock);
        foreach (string path in files) {
            SafeInput(path); string target = Path.Combine(clone, path.Substring(input.Length + 1)); Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(path, target, false); Require(FileHashForBoundary(path) == FileHashForBoundary(target));
            progress.cloneFiles++; progress.cloneBytes += new FileInfo(target).Length;
            if (progress.cloneFiles % 16 == 0 || progress.cloneFiles == files.Length) WriteInstallerPhase(runtime, progress, clock);
        }
        progress.phase = "start-powershell"; WriteInstallerPhase(runtime, progress, clock);
        string driver = Path.Combine(clone, ".github/scripts/Invoke-InstallerCiChild.ps1");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(driver) + " -Runtime " + Quote(runtime)) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = clone, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var child = Process.Start(start)) {
            int stdoutClosed = 0, stderrClosed = 0;
            child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs line) { if (line.Data == null) Interlocked.Exchange(ref stdoutClosed, 1); else Console.WriteLine(line.Data); };
            child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs line) { if (line.Data == null) Interlocked.Exchange(ref stderrClosed, 1); else Console.Error.WriteLine(line.Data); };
            child.BeginOutputReadLine(); child.BeginErrorReadLine();
            progress.childPid = unchecked((uint)child.Id); progress.phase = "wait-powershell"; WriteInstallerPhase(runtime, progress, clock);
            var waitClock = Stopwatch.StartNew(); bool exited = child.WaitForExit(150000);
            progress.waitElapsedMs = waitClock.ElapsedMilliseconds; progress.childExited = exited;
            if (exited) progress.childExitCode = child.ExitCode;
            progress.phase = "powershell-observed"; WriteInstallerPhase(runtime, progress, clock);
            if (!exited) throw new NativeFailure(1460);
            bool drained = Wait(delegate { return Interlocked.CompareExchange(ref stdoutClosed, 0, 0) != 0 && Interlocked.CompareExchange(ref stderrClosed, 0, 0) != 0; }, 2000);
            progress.stdoutComplete = Interlocked.CompareExchange(ref stdoutClosed, 0, 0) != 0;
            progress.stderrComplete = Interlocked.CompareExchange(ref stderrClosed, 0, 0) != 0;
            progress.phase = drained ? "powershell-drained" : "powershell-drain-timeout"; WriteInstallerPhase(runtime, progress, clock);
            if (!drained) throw new NativeFailure(1460);
            return child.ExitCode;
        }
        } catch (Exception error) {
            InstallerFailure(progress, error); try { WriteInstallerPhase(runtime, progress, clock); } catch (Exception) { }
            errors.WriteLine("Owned installer medium failure: " + error.GetType().Name + "; native=" + (progress.nativeError.HasValue ? progress.nativeError.Value.ToString(CultureInfo.InvariantCulture) : "none"));
            throw;
        } finally { Console.SetOut(previousOutput); Console.SetError(previousError); }
        }
    }
}
