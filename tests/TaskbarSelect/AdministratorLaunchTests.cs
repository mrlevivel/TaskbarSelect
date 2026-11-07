using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32;

namespace TaskbarSelect
{
    // Pure admission policy and owned ordinary temporary files only. These
    // checks never request UAC, create ProgramData caches or run helper modes.
    internal static class AdministratorLaunchTests
    {
        const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string Token = "74a5a692966c4c07a97e374ed87a066e";
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(IntPtr source, IntPtr handle, IntPtr target,
            out IntPtr duplicate, uint access, bool inherit, uint options);
        internal static void Run(Action<bool, string> check)
        { RunManaged(check); RunNative(check); }
        internal static void RunManaged(Action<bool, string> check)
        { Names(check); LauncherDirectories(check); StageFailureDiagnostics(check); Configuration(check); Security(check); Arguments(check); ApprovalPolicies(check); NamespaceSecurity(check); KernelImagePolicy(check); EnvironmentPolicy(check); }
        internal static void RunNative(Action<bool, string> check)
        { OwnedFailureHandle(check); OwnedReadLease(check); OwnedStage(check); }
        static void LauncherDirectories(Action<bool, string> check)
        {
            const string local = @"C:\Owned Launcher Policy\Local";
            string productionProfile = Path.Combine(local, "TaskbarSelect");
            string launcherBase = Path.Combine(local, "Temp", "TaskbarSelect.Launchers");
            string production = Path.Combine(launcherBase, "production");
            check(AdministratorLaunch.LauncherDirectory(local, productionProfile, false) == production,
                "Production administrator launcher bytes use the dedicated LocalAppData cache, separate from the fixed settings directory");
            check(AdministratorLaunch.LauncherDirectory(local.ToLowerInvariant(), productionProfile.ToLowerInvariant(), false).Equals(production, StringComparison.OrdinalIgnoreCase),
                "Production selection is stable across canonical Windows path casing without consulting profile files");
            const string profile = @"X:\Owned Isolated Profiles\Alpha";
            string isolated = AdministratorLaunch.LauncherDirectory(local, profile, true);
            const string identity = "bf58c42ab8edc973cbdae9a2209ab1a1d341c7322b0302c6732f92ed353c8e75";
            check(isolated == Path.Combine(launcherBase, "tests", identity) && AdministratorLaunch.ValidHash(Path.GetFileName(isolated)),
                "An isolated profile selects only the fixed test cache plus a lowercase canonical-profile SHA256 identity");
            check(AdministratorLaunch.LauncherDirectory(local, profile.ToLowerInvariant(), true) == isolated,
                "Canonical profile casing does not create another isolated launcher cache");
            check(AdministratorLaunch.LauncherDirectory(local, @"X:\Owned Isolated Profiles\Beta", true) != isolated &&
                AdministratorLaunch.LauncherDirectory(local, @"X:\Owned Isolated Profiles\Alpha-more", true) != isolated &&
                isolated != production, "Different isolated profiles and production never share launcher cache directories");
            check(!isolated.Contains(profile) && !isolated.Contains("Alpha") && !isolated.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase),
                "The selected launcher cache contains no profile path or nickname and does not stage in user preferences");
            string otherLocal = @"X:\Owned Other Local";
            check(AdministratorLaunch.LauncherDirectory(otherLocal, Path.Combine(otherLocal, "TaskbarSelect"), false) ==
                Path.Combine(otherLocal, "Temp", "TaskbarSelect.Launchers", "production"),
                "Production root selection uses only the supplied canonical LocalAppData directory");
            foreach (string invalid in new[] { null, "", "relative", @"C:relative", @"\\server\share\local", @"\\?\C:\local", @"\\.\C:\local",
                @"C:\local\..\cache", @"C:\local\.\cache", @"C:\", @"C:/local", @"C:\local\\cache", @"C:\local ", @"C:\local.", @"C:\local:stream" }) {
                check(LauncherDirectoryRejected(invalid, profile, true), "Invalid LocalAppData spellings fail before launcher selection without a fallback");
                check(LauncherDirectoryRejected(local, invalid, true), "Invalid isolated profile spellings fail before hashing or staging");
                check(LauncherDirectoryRejected(local, invalid, false), "Invalid production profile spellings cannot redirect the dedicated launcher cache");
            }
            foreach (string invalid in new[] { productionProfile, local, productionProfile + "\\child", launcherBase, production,
                Path.Combine(launcherBase, "tests", identity), launcherBase + "\\child", @"C:\Owned Launcher Policy" })
                check(LauncherDirectoryRejected(local, invalid, true), "Isolated settings cannot overlap production settings or any launcher-cache ancestor/descendant");
            check(LauncherDirectoryRejected(local, @"X:\Other Production Profile", false) &&
                LauncherDirectoryRejected(local, productionProfile + "\\child", false) &&
                LauncherDirectoryRejected(local, production, false), "Production selection rejects arbitrary/test/cache profile roots rather than falling back");
            check(AdministratorLaunch.LauncherDirectory(local, productionProfile + "-test", true).StartsWith(launcherBase + "\\tests\\", StringComparison.Ordinal) &&
                AdministratorLaunch.LauncherDirectory(local, launcherBase + "-test", true).StartsWith(launcherBase + "\\tests\\", StringComparison.Ordinal),
                "Sibling paths with similar prefixes remain distinct instead of becoming false overlap matches");
            string tooLongLocal = @"C:\" + new string('a', 150);
            check(AdministratorLaunch.CanonicalDosPath(tooLongLocal) != null &&
                LauncherDirectoryRejected(tooLongLocal, Path.Combine(tooLongLocal, "TaskbarSelect"), false) &&
                LauncherDirectoryRejected(tooLongLocal, profile, true),
                "Canonical input paths still fail when the exact future staged image would exceed the supported path bound");
        }
        static bool LauncherDirectoryRejected(string local, string profile, bool isolated)
        {
            try { AdministratorLaunch.LauncherDirectory(local, profile, isolated); return false; }
            catch (IOException error) {
                return !String.IsNullOrEmpty(error.Message) && (String.IsNullOrEmpty(profile) || !error.Message.Contains(profile)) &&
                    (String.IsNullOrEmpty(local) || !error.Message.Contains(local));
            }
        }
        static void StageFailureDiagnostics(Action<bool, string> check)
        {
            const string generic = "The administrator launcher staging checks failed.";
            check(AdministratorLaunch.StageFailureDetail(null) == generic && AdministratorLaunch.StageFailureDetail(new IOException()) == generic,
                "Unavailable or unknown staging exceptions produce only a fixed safe diagnostic");
            foreach (string guard in new[] { "The administrator launcher storage selection is invalid.",
                "The existing administrator bootstrap does not match this application.", "The administrator launcher directory contains an unexpected file.",
                "An administrator file identity is unsafe.", "Windows loader override DevOverrideEnable must be disabled for administrator support." })
                check(AdministratorLaunch.StageFailureDetail(new IOException(guard, new Exception("owned-test-secret C:\\private\\profile"))) == guard,
                    "Reviewed selection, image, sidecar, identity and loader diagnostics remain useful without exposing inner exception text");
            foreach (string prefix in new[] { "The administrator launcher directory could not be created. [nt=0x",
                "The administrator bootstrap could not be staged. [nt=0x" }) {
                foreach (string status in new[] { "c000050b", "00000000", "FFFFFFFF", "80000005" })
                    check(AdministratorLaunch.StageFailureDetail(new IOException(prefix + status + "]")) == prefix + status.ToUpperInvariant() + "]",
                        "An exact eight-digit native staging status is preserved with canonical hexadecimal casing");
                foreach (string suffix in new[] { "C00050B]", "0C000050B]", "G000050B]", "C000050B", "C000050B]]",
                    " C00050B]", "C00050B ]", "C000050B] ", "C000050B]\n", "C000050B] C:\\private\\profile owned-test-secret" })
                    check(AdministratorLaunch.StageFailureDetail(new IOException(prefix + suffix)) == generic,
                        "Malformed, whitespace or extended native-status messages fail closed instead of leaking a suffix");
            }
            foreach (string message in new[] { "Access to C:\\private\\profile\\settings.json was denied.", "owned-test-secret",
                "The administrator resource is unavailable. C:\\private\\profile", "The administrator file could not be pinned.\nowned-test-secret",
                " The administrator launcher storage selection is invalid.", "The administrator launcher storage selection is invalid. ",
                "the administrator launcher storage selection is invalid.", "The administrator bootstrap could not be staged. [nt=0xc000050b]\r\nowned-test-secret" })
                check(AdministratorLaunch.StageFailureDetail(new IOException(message)) == generic,
                    "Arbitrary private paths, secrets and near-matching exception text are replaced by the fixed generic staging diagnostic");
        }
        static void Names(Action<bool, string> check)
        {
            check(AdministratorLaunch.ValidHash(Hash), "A fixed64 lowercase SHA256 identity is accepted");
            foreach (string bad in new[] { null, "", Hash.ToUpperInvariant(), Hash.Substring(1), Hash + "0", new string('g', 64), Hash + "\n", "..\\" + Hash })
                check(!AdministratorLaunch.ValidHash(bad), "Malformed or noncanonical payload hashes fail closed");
            check(AdministratorLaunch.ValidToken(Token), "A fixed lowercase nonempty launch nonce is accepted");
            foreach (string bad in new[] { null, "", Token.ToUpperInvariant(), new string('0', 32), Token + "x", Token.Substring(1), "..\\settings.json" })
                check(!AdministratorLaunch.ValidToken(bad), "Malformed, empty and path-like launch nonces fail closed");
            check(AdministratorLaunch.CanonicalDosPath(@"C:\ProgramData\TaskbarSelect.Admin") != null &&
                AdministratorLaunch.CanonicalDosPath(@"X:\Owned Test Profile") != null, "Canonical local DOS paths permit ordinary directory spaces");
            foreach (string bad in new[] { null, "", "relative", @"C:relative", @"\\server\share\app.exe", @"\\?\C:\app.exe", @"\\.\C:\app.exe",
                @"C:\cache\..\app.exe", @"C:\cache\.\app.exe", @"C:\cache\\app.exe", @"C:\cache\app.exe:stream", @"C:\cache \app.exe", @"C:\cache.\app.exe",
                @"C:\cache\CON.exe", @"C:\cache\COM1", "C:\\cache\\app.exe\n", "C:/cache/app.exe" })
                check(!AdministratorLaunch.InputPath(bad), "UNC/device/relative/alias/stream/control paths cannot cross the launch boundary");
            string volume = @"\\?\Volume{74a5a692-966c-4c07-a97e-374ed87a066e}\ProgramData\TaskbarSelect.Admin\" + Hash + "\\" + Token + "\\AdministratorPayload.exe";
            check(AdministratorLaunch.InputPath(volume), "Canonical volume-GUID cache spelling is supported");
            check(!AdministratorLaunch.InputPath(volume.Replace("Volume{", "GLOBALROOT\\Volume{")) &&
                !AdministratorLaunch.InputPath(volume.Replace("ProgramData", "..")), "Volume spelling cannot admit device aliases or traversal");
            string hash, token;
            check(AdministratorLaunch.PayloadComponents(volume, out hash, out token) && hash == Hash && token == Token,
                "The payload path exposes only its fixed cache hash and nonce components");
            foreach (string bad in new[] { volume.Replace("AdministratorPayload.exe", "TaskbarSelect.exe"), volume.Replace("TaskbarSelect.Admin", "OtherCache"), volume.Replace(Token, new string('0', 32)), volume + ".config" })
                check(!AdministratorLaunch.PayloadComponents(bad, out hash, out token), "Unexpected payload basename/cache/session paths are rejected before file access");
            foreach (string name in new[] { "clr.tmp", "scratch.txt", "a.dat" }) check(AdministratorLaunch.BenignTempName(name), "Benign runtime temporary names remain available");
            foreach (string name in new[] { "a.exe", "a.DLL", "a.exe.config", "a.config", "a.cmd", "a.ps1", "a.cpl", "a.manifest", "a.local", "a.dll.", "a.dll ", "a:stream", "..\\a.tmp" })
                check(!AdministratorLaunch.BenignTempName(name), "Temporary directories cannot contain executable/configuration/alias names");
            using (var data = new MemoryStream(new byte[] { 77, 90, 0 })) check(!AdministratorLaunch.BenignTempHeader(data), "A renamed PE executable cannot pass as benign temporary data");
            using (var data = new MemoryStream(new byte[] { 77, 0 })) check(AdministratorLaunch.BenignTempHeader(data), "Nonexecutable temporary contents are accepted");
            check(!AdministratorLaunch.VerifyPayload(@"C:\ordinary\AdministratorPayload.exe", "bad", Token) &&
                !AdministratorLaunch.VerifyPayload(@"\\server\share\AdministratorPayload.exe", Hash, Token), "Invalid payload identity/location rejects without opening protected caches");
        }
        static void Configuration(Action<bool, string> check)
        {
            byte[] expected = new UTF8Encoding(false, true).GetBytes(AdministratorLaunch.PayloadConfiguration);
            using (var exact = new MemoryStream(expected, false)) check(AdministratorLaunch.ExactConfiguration(exact), "Only the exact no-BOM .NET4.8 configuration is accepted");
            foreach (byte[] altered in new[] { Encoding.UTF8.GetPreamble().Concat(expected).ToArray(), expected.Take(expected.Length - 1).ToArray(),
                Encoding.UTF8.GetBytes(AdministratorLaunch.PayloadConfiguration.Replace("\n", "\r\n")), Encoding.UTF8.GetBytes(AdministratorLaunch.PayloadConfiguration.Replace("Version=v4.8", "Version=v4.0")),
                Encoding.UTF8.GetBytes(AdministratorLaunch.PayloadConfiguration.Replace("<startup>", "<runtime><appDomainManagerAssembly value=\"unsafe\"/></runtime><startup>")) })
                using (var stream = new MemoryStream(altered, false)) check(!AdministratorLaunch.ExactConfiguration(stream), "BOM/newline/runtime-manager/configuration modifications fail exact byte admission");
            check(expected.Length == 156 && expected[expected.Length - 1] == 10 && !expected.Contains((byte)13), "The native and managed configuration contract is156bytes with one terminal LF");
        }
        static RawSecurityDescriptor Descriptor(string sddl) { return new RawSecurityDescriptor(sddl); }
        static void Security(Action<bool, string> check)
        {
            const string dir = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)";
            const string file = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;BU)";
            check(AdministratorLaunch.ProtectedSecurity(Descriptor(dir), true) && AdministratorLaunch.ProtectedSecurity(Descriptor(dir), false) &&
                AdministratorLaunch.ProtectedSecurity(Descriptor(file), false) && AdministratorLaunch.ProtectedSecurity(Descriptor(file.Replace("O:BA", "O:SY")), false) &&
                AdministratorLaunch.ProtectedSecurity(Descriptor(dir.Replace("FA;;;", "GA;;;")), true),
                "Protected BA/SY owners and exactly BA/SY full plus Users read/execute accept native generic or mapped masks");
            int fixtureIndex = 0;
            foreach (string altered in new[] { dir.Replace("O:BA", "O:BU"), dir.Replace("D:P", "D:"), dir.Replace("GRGX;;;BU", "FA;;;BU"),
                dir + "(A;OICI;FR;;;WD)", dir + "(A;OICI;FA;;;BA)", dir.Replace("(A;OICI;GRGX;;;BU)", ""),
                dir.Replace("OICI;GRGX", "OICIIO;GRGX"), dir.Replace("(A;OICI;FA;;;SY)", "(D;OICI;FA;;;SY)"),
                dir.Replace("OICI;FA;;;BA", ";FA;;;BA"), dir.Replace("GRGX;;;BU", "GR;;;BU"), dir.Replace("OICI;GRGX", "OICI;GWGX") })
                check(!AdministratorLaunch.ProtectedSecurity(Descriptor(altered), true), "Unexpected owners/DACLs/trustees/write grants/duplicates/inheritance fail raw protection policy at fixture " + fixtureIndex++);
            check(!AdministratorLaunch.ProtectedSecurity(null, true) && !AdministratorLaunch.ProtectedSecurity(Descriptor(file), true), "Directories require inheritable protection and null descriptors fail closed");
            var duplicate = Descriptor(dir + "(A;OICI;FA;;;BA)");
            var normalized = new DirectorySecurity(); normalized.SetSecurityDescriptorSddlForm(dir + "(A;OICI;FA;;;BA)");
            check(duplicate.DiscretionaryAcl.Count == 4 && normalized.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count == 3 &&
                !AdministratorLaunch.ProtectedSecurity(duplicate, true), "The actual raw descriptor rejects a duplicate ACE even though FileSystemSecurity merges it to three rules");
            byte[] duplicateBytes = new byte[duplicate.BinaryLength]; duplicate.GetBinaryForm(duplicateBytes, 0);
            check(!AdministratorLaunch.ProtectedSecurity(new RawSecurityDescriptor(duplicateBytes, 0), true), "Pinned-handle binary descriptors reject extra ACEs before any managed normalization");
            var safe = new AdministratorLaunch.FileInformation { Attributes = 0x80, Links = 1, IndexLow = 8 };
            check(AdministratorLaunch.SafeInformation(safe, false), "Owned regular single-link file identity is accepted");
            safe.Attributes |= 0x400; check(!AdministratorLaunch.SafeInformation(safe, false), "Reparse file identities are rejected");
            safe.Attributes = 0x80; safe.Links = 2; check(!AdministratorLaunch.SafeInformation(safe, false), "Hardlink aliases are rejected");
            safe.Links = 1; safe.IndexLow = 0; check(!AdministratorLaunch.SafeInformation(safe, false), "Missing stable file identifiers are rejected");
            safe.IndexLow = 8; safe.Attributes = 0x10;
            check(AdministratorLaunch.SafeInformation(safe, true) && !AdministratorLaunch.SafeInformation(safe, false), "Directory and file identities cannot be substituted");
            foreach (object value in new object[] { 1, -1, 2, "0", (long)0, new byte[] { 0 } })
                check(!AdministratorLaunch.SafeLoaderFlag(value, value is string ? RegistryValueKind.String : RegistryValueKind.DWord), "Enabled or malformed machine loader overrides fail closed");
            check(AdministratorLaunch.SafeLoaderFlag(null, RegistryValueKind.None) && AdministratorLaunch.SafeLoaderFlag(0, RegistryValueKind.DWord) &&
                !AdministratorLaunch.SafeLoaderFlag(0, RegistryValueKind.QWord), "Only absent or disabled DWORD loader override policy is accepted");
        }
        static void Arguments(Action<bool, string> check)
        {
            string[] parsed;
            var high = new NativeProcessIdentity.Info { IntegrityAvailable = true, IntegrityLevel = NativeProcessIdentity.High, ElevationAvailable = true, Elevated = true };
            foreach (string args in new[] { "--window-move-worker 0 12 3 4 5 -2560 -1080 640 480 532", "--window-move-worker 1 12 3 4 5 0 3 -1 -1 -1 -1 -2560 -1080 -1920 -600",
                "--window-move-worker 2 12 3 4 5", "--window-move-worker 3 12 3 4 5", "--window-move-worker 4 12 3 4 5",
                "--window-move-worker 5 12 3 4 5", "--window-move-worker 6 12 3 4 5 0", "--window-move-worker 6 12 3 4 5 1" }) {
                var words = args.Split(' ');
                check(AdministratorLaunch.WorkerArguments(args, out parsed) && parsed.SequenceEqual(words), "Protected worker string grammar admits every fixed operation without changing its arguments");
                check(AdministratorLaunch.WorkerArguments(words) && AdministratorPayloadEntry.AllowedArguments(words), "Protected payload array grammar admits the same fixed operation");
                check(AdministratorPayloadEntry.Admit(words, high, true), "Every fixed worker operation reaches the protected HIGH payload entry policy");
                check(!AdministratorPayloadEntry.Admit(words, high, false), "No fixed worker operation can enter from an unprotected payload");
            }
            foreach (string bad in new[] { null, "", "--window-move-worker 7 12 3 4 5", "--window-move-worker -1 12 3 4 5", "--window-move-worker 2147483648 12 3 4 5",
                "--window-move-worker 5 12 3 4 5 0", "--window-move-worker 5 0 3 4 5", "--window-move-worker 5 12 -3 4 5", "--window-move-worker 5 12 4294967296 4 5",
                "--window-move-worker 5 12 3 -4 5", "--window-move-worker 5 12 3 4 -5", "--window-move-worker 5 12 3 4 5\t",
                "--window-move-worker 6 12 3 4 5", "--window-move-worker 6 12 3 4 5 0 1", "--window-move-worker 6 12 3 4 5 -1", "--window-move-worker 6 12 3 4 5 2",
                "--window-move-worker 6 12 3 4 5 2147483647", "--window-move-worker 6 12 3 4 5 2147483648", "--window-move-worker 6 12 3 4 5 1\0",
                "--window-move-worker 6 0 3 4 5 1", "--window-move-worker 6 12 0 4 5 1", "--window-move-worker 6 12 3 0 5 1", "--window-move-worker 6 12 3 4 0 1",
                "--window-move-worker 4 0 3 4 5", "--window-move-worker 4 12 0 4 5",
                "--window-move-worker 4 12 3 0 5", "--window-move-worker 4 12 3 4 0", "--window-move-worker 4 12 3 4 5 16", "--window-move-worker 4 12 3 4 5 --uninstall",
                "--window-move-worker 4 12 3 4 5\n--uninstall", "--window-move-worker 4 \"12\" 3 4 5", "--window-move-worker 4  12 3 4 5", "--window-move-worker 4 +12 3 4 5",
                "--window-move-worker 0 12 3 4 5 0 0 0 480 532", "--window-move-worker 0 12 3 4 5 0 0 640 480 0", "--window-move-worker 0 12 3 4 5 2147483647 0 2 2 532",
                "--window-move-worker 1 12 3 4 5 0 3 -1 -1 -1 -1 5 5 4 4", "--administrator-helper " + Token + " 3 4", "--uninstall", "--test-profile test" }) {
                var words = bad == null ? null : bad.Split(' ');
                check(!AdministratorLaunch.WorkerArguments(bad, out parsed) && parsed == null, "Malformed/unbounded/arbitrary worker string arguments are never launched");
                check(!AdministratorLaunch.WorkerArguments(words), "Malformed/unbounded/arbitrary worker array arguments are never launched");
                if (words != null && words.Length > 0 && words[0] == "--window-move-worker")
                    check(!AdministratorPayloadEntry.AllowedArguments(words) && !AdministratorPayloadEntry.Admit(words, high, true), "Malformed worker arguments cannot bypass launch validation at the protected entry");
            }
            string[] helper = { "--administrator-helper", Token, "3", "4" };
            check(AdministratorPayloadEntry.AllowedArguments(helper) && AdministratorPayloadEntry.Admit(helper, high, true), "Protected payload admits only an elevated high token and fixed helper arguments");
            check(!AdministratorPayloadEntry.Admit(helper, high, false) && !AdministratorPayloadEntry.Admit(helper, null, true), "Writable payloads and unreadable tokens cannot enter a helper mode");
            foreach (uint integrity in new[] { NativeProcessIdentity.Untrusted, NativeProcessIdentity.Low, NativeProcessIdentity.Medium, NativeProcessIdentity.MediumPlus, NativeProcessIdentity.System }) {
                var other = new NativeProcessIdentity.Info { IntegrityAvailable = true, IntegrityLevel = integrity, ElevationAvailable = true, Elevated = true };
                check(!AdministratorPayloadEntry.Admit(helper, other, true), "Only the high session token is admitted; alternative integrity levels fail closed");
            }
            high.Elevated = false; check(!AdministratorPayloadEntry.Admit(helper, high, true), "A high label without elevation does not authorize protected workers");
            high.Elevated = true; high.ElevationAvailable = false; check(!AdministratorPayloadEntry.Admit(helper, high, true), "Unavailable elevation metadata fails closed");
            foreach (string[] bad in new[] { new string[0], new[] { "--uninstall" }, new[] { "--show" }, new[] { "--test-profile", "x" },
                new[] { "--administrator-helper", Token, "0", "4" }, new[] { "--administrator-helper", Token, "3", "0" },
                new[] { "--administrator-helper", Token, "3", "4", "extra" }, new[] { "--administrator-helper", Token, " 3", "4" } })
                check(!AdministratorPayloadEntry.AllowedArguments(bad), "Payload entry cannot reach normal UI/profile/Startup/uninstall or malformed helper commands");
            check(!AdministratorPayloadEntry.IsPayloadProcess, "Policy tests never activate the protected payload process role");
        }
        static void EnvironmentPolicy(Action<bool, string> check)
        {
            var start = new ProcessStartInfo { UseShellExecute = false };
            foreach (string key in new[] { "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "COMPLUS_Version", "COMPLUS_ApplicationMigrationRuntimeActivationConfigPath", "APPDOMAIN_MANAGER_ASM", "APPDOMAIN_MANAGER_TYPE", "DEVPATH", "DOTNET_STARTUP_HOOKS", "__COMPAT_LAYER" })
                start.EnvironmentVariables[key] = "owned-test-poison";
            string temp = @"\\?\Volume{74a5a692-966c-4c07-a97e-374ed87a066e}\ProgramData\TaskbarSelect.Admin\" + Hash + "\\" + Token + "\\tmp";
            AdministratorLaunch.CleanEnvironment(start, @"C:\Windows", temp);
            check(start.EnvironmentVariables.Count == 6 && start.EnvironmentVariables["SystemRoot"] == @"C:\Windows" && start.EnvironmentVariables["WINDIR"] == @"C:\Windows" &&
                start.EnvironmentVariables["SystemDrive"] == "C:" && start.EnvironmentVariables["PATH"] == @"C:\Windows\System32" && start.EnvironmentVariables["TEMP"] == temp && start.EnvironmentVariables["TMP"] == temp,
                "Administrator child environment contains only six fixed Windows/System32/protected-temp entries");
            foreach (string key in new[] { "COR_ENABLE_PROFILING", "COR_PROFILER", "COMPLUS_Version", "APPDOMAIN_MANAGER_ASM", "APPDOMAIN_MANAGER_TYPE", "DEVPATH", "DOTNET_STARTUP_HOOKS", "__COMPAT_LAYER" })
                check(!start.EnvironmentVariables.ContainsKey(key), "Inherited CLR/profiler/manager/compatibility injection is removed from child environment");
        }
        static void ApprovalPolicies(Action<bool, string> check)
        {
            Func<AdministratorLaunch.ApprovalIdentity> parent = delegate { return new AdministratorLaunch.ApprovalIdentity {
                Process = 11, Started = 12, Session = 5, Sid = "owned-synthetic-account", Integrity = NativeProcessIdentity.Medium }; };
            Func<AdministratorLaunch.ApprovalIdentity> child = delegate { return new AdministratorLaunch.ApprovalIdentity {
                Process = 13, Started = 14, Session = 5, Sid = "owned-synthetic-account", Integrity = NativeProcessIdentity.Medium }; };
            check(AdministratorLaunch.SameApprovalIdentity(parent(), child()), "Approval host admits only a distinct genuine MED process from the same account/session");
            check(!AdministratorLaunch.SameApprovalIdentity(null, child()) && !AdministratorLaunch.SameApprovalIdentity(parent(), null),
                "Unavailable retained token metadata fails approval-host admission");
            foreach (Action<AdministratorLaunch.ApprovalIdentity> alter in new Action<AdministratorLaunch.ApprovalIdentity>[] {
                delegate(AdministratorLaunch.ApprovalIdentity value) { value.Process = 0; },
                delegate(AdministratorLaunch.ApprovalIdentity value) { value.Process = 11; },
                delegate(AdministratorLaunch.ApprovalIdentity value) { value.Started = 0; },
                delegate(AdministratorLaunch.ApprovalIdentity value) { value.Sid = null; },
                delegate(AdministratorLaunch.ApprovalIdentity value) { value.Sid = "other-owned-synthetic-account"; },
                delegate(AdministratorLaunch.ApprovalIdentity value) { value.Session++; },
                delegate(AdministratorLaunch.ApprovalIdentity value) { value.Elevated = true; } }) {
                var invalid = child(); alter(invalid);
                check(!AdministratorLaunch.SameApprovalIdentity(parent(), invalid), "Wrong PID/start/account/session or elevation rejects a suspended approval host");
            }
            foreach (uint integrity in new[] { NativeProcessIdentity.Untrusted, NativeProcessIdentity.Low, NativeProcessIdentity.MediumPlus, NativeProcessIdentity.High, NativeProcessIdentity.System }) {
                var invalid = child(); invalid.Integrity = integrity;
                check(!AdministratorLaunch.SameApprovalIdentity(parent(), invalid), "A suspended approval host must be exactly MED at alternative integrity " + integrity);
                invalid = parent(); invalid.Integrity = integrity;
                check(!AdministratorLaunch.SameApprovalIdentity(invalid, child()), "An alternative parent integrity cannot authorize the MED approval path " + integrity);
            }
            var elevatedParent = parent(); elevatedParent.Elevated = true;
            check(!AdministratorLaunch.SameApprovalIdentity(elevatedParent, child()), "A high/elevated parent cannot enter the normal MED approval path");
        }
        static void OwnedFailureHandle(Action<bool, string> check)
        {
            IntPtr copy;
            if (!DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(), out copy, 0, false, 2))
                throw new IOException("The owned test process handle could not be duplicated.");
            var failure = new AdministratorLaunch.ApprovalStartException("owned synthetic startup failure", null, copy, true, false, new IOException());
            try {
                check(failure.Child == null && failure.NeverResumed && !failure.ImageVerified && !failure.WaitForExit(0),
                    "An absent managed Process wrapper retains the exact owned raw handle and cannot report an unconfirmed child as drained");
                bool pending = false;
                try { failure.ExitCode.ToString(); }
                catch (InvalidOperationException) { pending = true; }
                check(pending, "A still-running exact owned handle cannot provide a completed native exit code");
            } finally { failure.Dispose(); }
            bool closed = false;
            try { failure.WaitForExit(0); } catch (InvalidOperationException) { closed = true; }
            check(closed && NativeProcessIdentity.Started(NativeProcessIdentity.Current.Process) == NativeProcessIdentity.Current.Started,
                "Disposing a retained failure handle closes only its duplicate and never stops or reopens the owned test process");
        }
        static void KernelImagePolicy(Action<bool, string> check)
        {
            var predicate = typeof(AdministratorLaunch).GetMethod("ValidProcessPathKernel", BindingFlags.NonPublic | BindingFlags.Static);
            Func<string, bool> allowed = delegate(string value) { return (bool)predicate.Invoke(null, new object[] { value }); };
            check(allowed(@"\Device\HarddiskVolume12\owned\AdministratorBootstrap.exe") &&
                allowed(@"\device\harddiskvolume123\owned\AdministratorBootstrap.exe"),
                "Only API-produced physical disk audit image paths admit safe file segments and case-insensitive native prefix");
            foreach (string invalid in new[] { null, "", @"\Device\HarddiskVolume\owned.exe", @"\Device\HarddiskVolume12", @"\Device\HarddiskVolumeX\owned.exe",
                @"\Device\HarddiskVolume12\..\owned.exe", @"\Device\HarddiskVolume12\owned.exe:stream", @"\Device\HarddiskVolume12\owned.\app.exe",
                @"\Device\HarddiskVolume12\CON\app.exe", @"\Device\Mup\server\owned.exe", @"C:\owned.exe", @"\\?\GLOBALROOT\Device\HarddiskVolume12\owned.exe" })
                check(!allowed(invalid), "Malformed/relative/device/network/stream audit names fail the private retained-image policy");
            check(!AdministratorLaunch.InputPath(@"\\?\GLOBALROOT\Device\HarddiskVolume12\owned.exe"),
                "Kernel audit-name verification never broadens caller-controlled launch or cache path admission");
        }
        static void NamespaceSecurity(Action<bool, string> check)
        {
            const string sid = "S-1-5-21-11-22-33-1001"; // Synthetic owned policy fixture, never a queried user SID.
            string directory = "O:" + sid + "G:BUD:P(D;;0xD000C;;;OW)(D;;0xD000C;;;" + sid + ")(A;;0x20003;;;" + sid + ")(A;;0xF000F;;;SY)(A;;0xF000F;;;BA)";
            string link = directory.Replace("0xD000C", "0xD0002").Replace("0x20003", "0x20001").Replace("0xF000F", "0xF0001");
            check(AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(directory), sid, true) &&
                AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(link), sid, false),
                "Exact native HIGH-map directory/link ACLs seal same-user and OWNER_RIGHTS mutation while permitting only fixed read/system/admin masks");
            int index = 0;
            foreach (string invalid in new[] { directory.Replace("O:" + sid, "O:BA"), directory.Replace("G:BU", "G:BA"), directory.Replace("D:P", "D:"),
                directory.Replace("(D;;0xD000C;;;OW)", ""), directory.Replace("(D;;0xD000C;;;" + sid + ")", ""),
                directory.Replace("0xD000C", "0xD0004"), directory.Replace("0x20003", "0xF000F"), directory.Replace("(D;;", "(A;;"),
                directory.Replace("(D;;0xD000C;;;OW)", "(D;CI;0xD000C;;;OW)"), directory + "(A;;0xF000F;;;WD)",
                directory.Replace("(A;;0xF000F;;;SY)(A;;0xF000F;;;BA)", "(A;;0xF000F;;;BA)(A;;0xF000F;;;SY)") })
                check(!AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(invalid), sid, true), "Changed namespace owners/groups/protection/denials/grants/trustees/order reject native map at fixture " + index++);
            string canonicalLink = link.Replace("0xD0002", "0xD0000");
            check(AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(canonicalLink), sid, false) &&
                AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(link.Replace("(D;;0xD0002;;;OW)", "(D;;0xD0000;;;OW)")), sid, false) &&
                AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(canonicalLink.Replace("(D;;0xD0000;;;OW)", "(D;;0xD0002;;;OW)")), sid, false),
                "The kernel-canonical symbolic-link DENYs accept only D0000 or D0002 while retaining all three supported mutation denials");
            foreach (string invalid in new[] { canonicalLink.Replace("0xD0000", "0xC0000"), canonicalLink.Replace("0xD0000", "0x90000"),
                canonicalLink.Replace("0xD0000", "0x50000"), canonicalLink.Replace("0xD0000", "0xD0004"),
                canonicalLink.Replace("0x20001", "0x60001"), canonicalLink.Replace("0x20001", "0x20003"), canonicalLink.Replace("0xF0001", "0xF0003") })
                check(!AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(invalid), sid, false),
                    "Canonical symbolic-link ACLs still reject missing DELETE/WRITE_DAC/WRITE_OWNER denials, unexpected denial bits or extra allow grants");
            check(!AdministratorLaunch.ProtectedNamespaceSecurity(null, sid, true) &&
                !AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(directory), null, true) &&
                !AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(directory), "S-1-5-21-11-22-33-1002", true) &&
                !AdministratorLaunch.ProtectedNamespaceSecurity(Descriptor(directory), sid, false),
                "Unavailable/wrong current SID or the wrong NT object kind fails namespace protection policy");
            var raw = Descriptor(link); byte[] bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
            check(AdministratorLaunch.ProtectedNamespaceSecurity(new RawSecurityDescriptor(bytes, 0), sid, false),
                "Raw native namespace descriptor bytes preserve the five ordered ACE identities/masks without normalization");
        }
        static bool Rejected(Action action)
        { try { action(); return false; } catch (IOException) { return true; } catch (UnauthorizedAccessException) { return true; } }
        static void OwnedReadLease(Action<bool, string> check)
        {
            string folder = Path.Combine(Path.GetTempPath(), "TaskbarSelect-LaunchRead-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "owned.bin"); byte[] data = Encoding.ASCII.GetBytes("owned launch file identity test\n");
            try {
                File.WriteAllBytes(path, data);
                string before;
                using (var lease = AdministratorLaunch.OpenFile(path)) {
                    before = AdministratorLaunch.Hash(lease);
                    check(before.Length == 64 && before == AdministratorLaunch.Hash(lease), "An owned pinned read handle hashes reproducibly without path rereads");
                    check(Rejected(delegate { using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { writer.WriteByte(0); } }), "A bootstrap-style read lease prevents another writer from changing the file");
                    check(Rejected(delegate { File.Move(path, path + ".moved"); }), "A bootstrap-style read lease prevents delete/rename until native launch lifetime ends");
                    check(lease.CanRead && !lease.CanWrite && File.ReadAllBytes(path).SequenceEqual(data), "Lease checks leave the owned file unchanged and read only");
                }
                using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) writer.WriteByte(0);
                using (var lease = AdministratorLaunch.OpenFile(path)) check(AdministratorLaunch.Hash(lease) != before, "Post-lease changes are detected by the next exact byte identity check");
            } finally {
                foreach (string owned in new[] { path, path + ".moved" }) if (File.Exists(owned)) File.Delete(owned);
                Directory.Delete(folder);
            }
        }
        static bool NativeSelfTest(AdministratorLaunch.BootstrapImage image, string ownedDosPath, string args, int expectedExit)
        {
            // Only an explicitly owned fixture and fixed native pure test args.
            // This control is not the production administrator launch path.
            using (Process child = Process.Start(new ProcessStartInfo { FileName = @"\\?\" + ownedDosPath, Arguments = args,
                WorkingDirectory = Environment.SystemDirectory, UseShellExecute = false, CreateNoWindow = true })) {
                if (child == null) return false;
                if (!child.WaitForExit(5000)) {
                    // Only this exact, positively created pure self-test child;
                    // it has no managed helper/cache/job mutation path.
                    child.Kill(); child.WaitForExit(5000); return false;
                }
                long started;
                return child.ExitCode == expectedExit && image.VerifyBootstrapProcess(child, out started) && started > 0 && image.VerifyBootstrapProcess(child);
            }
        }
        static bool ApprovalSelfTest(AdministratorLaunch.BootstrapImage image)
        {
            var current = NativeProcessIdentity.Current;
            using (Process child = image.StartApprovalSelfTest(current.Process, current.Started, Token)) {
                if (child == null) return false;
                if (!child.WaitForExit(10000)) { child.Kill(); child.WaitForExit(5000); return false; }
                long started;
                return child.ExitCode == 0 && image.VerifyBootstrapProcess(child, out started) && started > 0;
            }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint operation, byte[] input, int inputBytes,
            IntPtr output, int outputBytes, out int returned, IntPtr overlapped);
        static void CreateOwnedJunction(string path, string target, string fixture)
        {
            string prefix = Path.GetFullPath(fixture).TrimEnd('\\') + "\\";
            if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(path) || !Directory.Exists(target)) throw new IOException("The junction fixture is not wholly owned.");
            Directory.CreateDirectory(path);
            try {
                byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
                byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
                byte[] buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
                Array.Copy(BitConverter.GetBytes(unchecked((uint)0xA0000003)), 0, buffer, 0, 4);
                Array.Copy(BitConverter.GetBytes((ushort)(buffer.Length - 8)), 0, buffer, 4, 2);
                Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, buffer, 10, 2);
                Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, buffer, 12, 2);
                Array.Copy(BitConverter.GetBytes((ushort)print.Length), 0, buffer, 14, 2);
                Array.Copy(substitute, 0, buffer, 16, substitute.Length);
                Array.Copy(print, 0, buffer, 18 + substitute.Length, print.Length);
                using (var directory = CreateFile(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) {
                    int returned;
                    if (directory.IsInvalid || !DeviceIoControl(directory, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                        throw new IOException("The owned junction fixture could not be created. [win32=" + Marshal.GetLastWin32Error() + "]");
                }
            } catch { Directory.Delete(path); throw; }
        }
        static void OwnedStage(Action<bool, string> check)
        {
            string fixture = Path.Combine(Path.GetTempPath(), "TS-LaunchStage-" + Guid.NewGuid().ToString("N"));
            string profile = Path.Combine(fixture, "TaskbarSelect"), path = null, hashFolder = null;
            string selectedRoot = AdministratorLaunch.LauncherDirectory(fixture, profile, false);
            Directory.CreateDirectory(fixture);
            try {
                AdministratorLaunch.BootstrapImage image;
                try { image = AdministratorLaunch.Stage(selectedRoot); }
                catch (IOException error) {
                    if (error.Message.StartsWith("Windows loader override", StringComparison.Ordinal)) {
                        check(true, "SKIP owned Stage/native launch: machine loader policy safely disables administrator support"); return;
                    }
                    throw;
                }
                byte[] original;
                using (image) {
                    string[] pieces = image.Path.Split('\\');
                    string bootstrapHash = pieces[pieces.Length - 2];
                    hashFolder = Path.Combine(selectedRoot, "administrator-launchers", bootstrapHash);
                    path = Path.Combine(hashFolder, "AdministratorBootstrap.exe");
                    original = File.ReadAllBytes(path);
                    check(Directory.Exists(selectedRoot) && !Directory.Exists(profile) && AdministratorLaunch.ValidHash(bootstrapHash) &&
                        image.Path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && AdministratorLaunch.InputPath(image.Path) &&
                        AdministratorLaunch.ValidHash(image.PayloadHash), "First-run Stage creates only the selected dedicated owned cache, leaves settings absent and returns a canonical volume-GUID image");
                    check(original.Length > 2 && original[0] == 'M' && original[1] == 'Z', "Stage extracts the embedded native PE image with no managed sidecars");
                    Directory.CreateDirectory(profile);
                    string preferences = Path.Combine(profile, "settings.json");
                    byte[] preferencesBytes = Encoding.UTF8.GetBytes("owned invalid preferences; never launcher input\n");
                    File.WriteAllBytes(preferences, preferencesBytes);
                    using (var again = AdministratorLaunch.Stage(AdministratorLaunch.LauncherDirectory(fixture, profile, false)))
                        check(again.Path == image.Path && again.PayloadHash == image.PayloadHash && File.ReadAllBytes(path).SequenceEqual(original),
                            "A second Stage shares leases for the identical embedded image without replacing bytes");
                    check(File.ReadAllBytes(preferences).SequenceEqual(preferencesBytes) && Directory.GetFiles(profile).SequenceEqual(new[] { preferences }),
                        "Staging in the dedicated cache leaves owned invalid preferences untouched and adds no profile files");
                    check(Rejected(delegate { using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) writer.WriteByte(0); }) &&
                        Rejected(delegate { File.Move(path, path + ".moved"); }), "Held bootstrap Stage lease prevents write and rename before elevation can launch it");
                    check(Rejected(delegate { Directory.Move(selectedRoot, selectedRoot + "-moved"); }) && Rejected(delegate { Directory.Move(hashFolder, hashFolder + "-moved"); }),
                        "Held physical ancestor leases prevent selected cache and launcher-directory rename");
                    var ownedIdentity = NativeProcessIdentity.Current;
                    check(!image.VerifyBootstrapProcess(0, 0) && !image.VerifyBootstrapProcess(ownedIdentity.Process, 0) &&
                        !image.VerifyBootstrapProcess(ownedIdentity.Process, ownedIdentity.Started) &&
                        !image.VerifyBootstrapProcess(ownedIdentity.Process, ownedIdentity.Started + 1),
                        "Read-only limited-handle bootstrap proof rejects zero/stale identities and the unrelated owned test process without full process access");
                    check(Rejected(delegate { using (image.StartApprovalHost(0, 1, Token)) { } }) &&
                        Rejected(delegate { using (image.StartApprovalSelfTest(1, 0, Token)) { } }) &&
                        Rejected(delegate { using (image.StartApprovalHost(1, 1, "..\\unsafe")) { } }),
                        "Malformed fixed host identities/tokens fail before creating any process or requesting approval");
                    var current = NativeProcessIdentity.Current;
                    check(Rejected(delegate { using (image.StartApprovalHost(current.Process, current.Started + 1, Token)) { } }),
                        "A stale parent creation time fails before requesting UAC or creating a host");
                    check(NativeSelfTest(image, path, "--bootstrap-self-test", 0), "Owned native direct self-test exits0 and its retained process handle verifies the exact staged image");
                    check(NativeSelfTest(image, path, "--bootstrap-self-test extra", 100), "A fast native CLI refusal exits100 with retained-handle image proof before any protected-cache/helper path");
                    if (current.IntegrityAvailable && current.IntegrityLevel == NativeProcessIdentity.Medium && current.ElevationAvailable && !current.Elevated)
                        check(ApprovalSelfTest(image), "Suspended MED host verifies token/image before resuming and its sealed-map pure child completes without UAC or protected-cache writes");
                    else check(true, "SKIP MED approval-host native fixture: this test runner is not a genuine non-elevated MED token; no UAC fallback attempted");
                }
                check(!image.VerifyBootstrapProcess(0, 0), "Disposed bootstrap lease cannot authenticate a later process");
                using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) writer.WriteByte(0);
                check(Rejected(delegate { using (var unexpected = AdministratorLaunch.Stage(selectedRoot)) { } }) && File.ReadAllBytes(path)[0] == 0,
                    "Stage rejects a tampered existing bootstrap without repairing or replacing it");
                File.WriteAllBytes(path, original); // Restore only this explicit owned fixture between independent negative cases.
                foreach (string suffix in new[] { ".config", ".local", ".manifest", ".dll" }) {
                    string sidecar = path + suffix; File.WriteAllText(sidecar, "owned-untrusted-sidecar", new UTF8Encoding(false));
                    try {
                        check(Rejected(delegate { using (var unexpected = AdministratorLaunch.Stage(selectedRoot)) { } }) && File.ReadAllText(sidecar) == "owned-untrusted-sidecar" && File.ReadAllBytes(path).SequenceEqual(original),
                            "Stage rejects unexpected native-loader/config/DLL siblings without deleting or modifying them");
                    } finally { File.Delete(sidecar); }
                }
                string savedFolder = hashFolder + "-owned-target";
                Directory.Move(hashFolder, savedFolder);
                try {
                    CreateOwnedJunction(hashFolder, savedFolder, fixture);
                    check(Rejected(delegate { using (var unexpected = AdministratorLaunch.Stage(selectedRoot)) { } }) &&
                        (File.GetAttributes(hashFolder) & FileAttributes.ReparsePoint) != 0 &&
                        File.ReadAllBytes(Path.Combine(savedFolder, "AdministratorBootstrap.exe")).SequenceEqual(original),
                        "A junction to identical owned launcher bytes is rejected without repairing the junction or its target");
                } finally {
                    if (Directory.Exists(hashFolder)) Directory.Delete(hashFolder);
                    Directory.Move(savedFolder, hashFolder);
                }
                using (var admitted = AdministratorLaunch.Stage(selectedRoot)) check(admitted.Path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase),
                    "An intact owned fixture remains launchable after all negative leases dispose");
            } finally {
                // Exact GUID-owned fixture only; no user profile/cache/install
                // paths are enumerated or recursively removed by these checks.
                string resolved = Path.GetFullPath(fixture);
                if (resolved == fixture && Path.GetFileName(resolved).StartsWith("TS-LaunchStage-", StringComparison.Ordinal) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }
    }
}
