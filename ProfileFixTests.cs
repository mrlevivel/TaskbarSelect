using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSelect
{
    internal static class ProfileFixTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var normal = AppProfile.Select(new string[0]);
            check(AppProfile.ProductionSafe(normal), "Default launch selects the released production profile");
            check(normal.Directory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarSelect"), "Production settings location is unchanged");
            check(!AppProfile.ProductionSafe(new ProfileSelection(normal.Directory, true)) &&
                !AppProfile.ProductionSafe(new ProfileSelection(normal.Directory + "-other", false)) &&
                !AppProfile.ProductionSafe(null), "Selection metadata cannot mark a test, alternate, or absent profile as production");
            check(Program.ConfigPath == Path.Combine(AppProfile.Directory, "settings.json") &&
                WorkAreas.JournalPath == Path.Combine(AppProfile.Directory, "workarea-recovery.json"),
                "Settings and recovery consumers use the selected profile directory");
            check(AppProfile.ProductionSafe() == (!AppProfile.IsTest && AppProfile.AllowsStartup &&
                String.Equals(AppProfile.Directory, AppProfile.ProductionDirectory, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Program.ConfigPath, Path.Combine(AppProfile.ProductionDirectory, "settings.json"), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(WorkAreas.JournalPath, Path.Combine(AppProfile.ProductionDirectory, "workarea-recovery.json"), StringComparison.OrdinalIgnoreCase)),
                "The live production invariant verifies mode, Startup, settings, and recovery paths");
            foreach (string command in new[] { "--startup", "--restore", "--uninstall", "--prepare-upgrade", "--self-test-startup" })
                check(AppProfile.ProductionSafe(AppProfile.Select(new[] { command })), "Production lifecycle remains available: " + command);
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect-profile-fixture");
            var test = AppProfile.Select(new[] { "--test-profile", directory });
            check(test.IsTest && test.Directory == AppProfile.ResolveDirectory(directory) && !AppProfile.ProductionSafe(test), "Test profile requires explicit selection and fails production packaging invariant");
            check(AppProfile.EventName("fixture-sid", normal) == @"Local\TaskbarSelect.fixture-sid", "Production IPC remains compatible with beta.7");
            check(AppProfile.EventName("fixture-sid", test) != AppProfile.EventName("fixture-sid", normal), "Test stop/show events cannot signal the regular app");
            check(AppProfile.EventName("fixture-sid", test) == AppProfile.EventName("fixture-sid", AppProfile.Select(new[] { "--test-profile", directory.ToUpperInvariant() })), "Test IPC is stable across Windows path casing");
            check(AppProfile.EventName("fixture-sid", test) != AppProfile.EventName("fixture-sid", AppProfile.Select(new[] { "--test-profile", directory + "-other" })), "Different test profiles have separate IPC");
            foreach (string command in new[] { "--startup", "--self-test-startup", "--uninstall", "--prepare-upgrade" })
                Reject(check, new[] { "--test-profile", directory, command }, "Test mode cannot change Startup or uninstall: " + command);
            check(AppProfile.Select(new[] { "--test-profile", directory, "--restore" }).IsTest, "Explicit test recovery uses its own profile");
            Reject(check, new[] { "--test-profile" }, "Missing test directory fails closed");
            Reject(check, new[] { "--test-profile", "relative" }, "Relative test directory is rejected");
            Reject(check, new[] { "--test-profile", @"C:relative" }, "Drive-relative test directory is rejected");
            Reject(check, new[] { "--test-profile", @"\relative" }, "Current-drive-relative test directory is rejected");
            Reject(check, new[] { "--test-profile", directory, "--test-profile", directory }, "Duplicate test profile arguments are rejected");
            Reject(check, new[] { "--test-profile", AppProfile.ProductionDirectory }, "Test profile cannot select regular settings");
            Reject(check, new[] { "--test-profile", Path.Combine(AppProfile.ProductionDirectory, "test") }, "Test profile cannot nest within regular settings");
            Reject(check, new[] { "--test-profile", Path.GetDirectoryName(AppProfile.ProductionDirectory) }, "Test cleanup cannot encompass the regular profile");
            Reject(check, new[] { "--test-profile", Path.GetPathRoot(directory) }, "Drive root cannot be a test profile");
            Reject(check, new[] { "--test-profile", @"\\?\" + AppProfile.ProductionDirectory }, "Extended production alias is rejected before profile access");
            Reject(check, new[] { "--test-profile", @"\\.\" + AppProfile.ProductionDirectory }, "Device production alias is rejected before profile access");
            Reject(check, new[] { "--test-profile", @"\\localhost\C$\Users\profile-fixture" }, "UNC test profiles are rejected before share access");
            SwitchDirectories(check);
            CanonicalDirectories(check);
        }
        static void SwitchDirectories(Action<bool, string> check)
        {
            bool originalTest = AppProfile.IsTest;
            string originalDirectory = AppProfile.Directory;
            string testDirectory = Path.Combine(Path.GetTempPath(), "TaskbarSelect-ProfileSwitch-" + Guid.NewGuid().ToString("N"));
            try {
                AppProfile.Initialize(new string[0]);
                check(AppProfile.ProductionSafe() && Program.ConfigPath == Path.Combine(AppProfile.ProductionDirectory, "settings.json") &&
                    WorkAreas.JournalPath == Path.Combine(AppProfile.ProductionDirectory, "workarea-recovery.json"),
                    "Production verification reads the live settings and recovery paths");
                AppProfile.Initialize(new[] { "--test-profile", testDirectory });
                check(AppProfile.IsTest && !AppProfile.AllowsStartup && !AppProfile.ProductionSafe() &&
                    Program.ConfigPath == Path.Combine(AppProfile.Directory, "settings.json") &&
                    WorkAreas.JournalPath == Path.Combine(AppProfile.Directory, "workarea-recovery.json") && !Directory.Exists(testDirectory),
                    "Selecting a test profile updates both live paths without creating its directory");
            } finally {
                AppProfile.Initialize(originalTest ? new[] { "--test-profile", originalDirectory } : new string[0]);
            }
            check(AppProfile.IsTest == originalTest && AppProfile.Directory == originalDirectory &&
                Program.ConfigPath == Path.Combine(originalDirectory, "settings.json") &&
                WorkAreas.JournalPath == Path.Combine(originalDirectory, "workarea-recovery.json") && AppProfile.ProductionSafe() == !originalTest,
                "Restoring the original profile updates both paths and the production invariant");
        }
        [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetShortPathName(string path, StringBuilder shortPath, uint length);
        static void CanonicalDirectories(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect-ProfileAliases-" + Guid.NewGuid().ToString("N"));
            string fixtureProduction = Path.Combine(directory, "owned-production-fixture");
            Directory.CreateDirectory(fixtureProduction);
            try {
                string physical = AppProfile.ResolveDirectory(fixtureProduction);
                check(AppProfile.ResolveDirectory(physical) == physical, "Resolving an existing owned directory is idempotent");
                string future = Path.Combine(fixtureProduction, "not-created", "child");
                check(AppProfile.ResolveDirectory(future) == Path.Combine(physical, "not-created", "child") && !Directory.Exists(future),
                    "Missing descendants append to the physical existing ancestor without creating anything");
                check(AppProfile.ResolvedOverlap(future, fixtureProduction) && AppProfile.ResolvedOverlap(directory, fixtureProduction),
                    "Both physical descendants and ancestors overlap the owned production fixture");
                check(!AppProfile.ResolvedOverlap(Path.Combine(directory, "sibling"), fixtureProduction), "Separate physical fixture directories remain eligible");
                string file = Path.Combine(directory, "owned-file-fixture"); File.WriteAllText(file, "owned metadata fixture");
                bool rejected = false;
                try { AppProfile.ResolveDirectory(Path.Combine(file, "child")); } catch (ArgumentException) { rejected = true; }
                check(rejected, "An existing file cannot masquerade as a profile directory ancestor");
                File.Delete(file);
                var shortPath = new StringBuilder(32768);
                uint length = GetShortPathName(fixtureProduction, shortPath, (uint)shortPath.Capacity);
                check(length > 0 && length < shortPath.Capacity, "The owned directory has a resolvable DOS path representation");
                string alias = shortPath.ToString();
                check(AppProfile.ResolveDirectory(alias) == physical && AppProfile.ResolvedOverlap(alias, fixtureProduction),
                    "A DOS directory alias resolves to the same physical owned production fixture");
                check(AppProfile.EventName("fixture-sid", AppProfile.Select(new[] { "--test-profile", alias })) ==
                    AppProfile.EventName("fixture-sid", AppProfile.Select(new[] { "--test-profile", fixtureProduction })),
                    "Aliases of one physical test directory share the same isolated stop/show events");
                if (String.Equals(alias, fixtureProduction, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine("SKIP: this volume does not provide a distinct 8.3 alias for the owned profile fixture.");
            } finally {
                // Only these exact empty fixture directories were created above.
                string file = Path.Combine(directory, "owned-file-fixture"); if (File.Exists(file)) File.Delete(file);
                Directory.Delete(fixtureProduction); Directory.Delete(directory);
            }
        }
        static void Reject(Action<bool, string> check, string[] args, string name)
        {
            bool rejected = false;
            try { AppProfile.Select(args); } catch (ArgumentException) { rejected = true; }
            check(rejected, name);
        }
    }
}
