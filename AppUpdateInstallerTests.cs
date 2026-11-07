using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TaskbarSelect
{
    internal static class AppUpdateInstallerTests
    {
        static AppUpdateInstaller.EligibilityEvidence Installed()
        {
            return new AppUpdateInstaller.EligibilityEvidence {
                DesktopRuntime = true, Production = true, RegistrationPresent = true, RegistrationValid = true,
                ExecutableSafe = true, UninstallerSafe = true,
                Identity = new NativeProcessIdentity.Info { IntegrityAvailable = true, ElevationAvailable = true, IntegrityLevel = NativeProcessIdentity.Medium },
                ExecutablePath = @"C:\Owned App\TaskbarSelect.exe", InstallLocation = @"C:\Owned App\",
                UninstallString = "\"C:\\Owned App\\unins000.exe\"", DefaultLocation = @"C:\Users\Fixture\AppData\Local\Programs\Taskbar Select"
            };
        }

        static AppUpdateInstaller.PermissionEvidence Off()
        { return new AppUpdateInstaller.PermissionEvidence { Available = true, Status = "Off" }; }

        static string IdentityDirectory(string path) { return path.TrimEnd('\\'); }

        internal static void Run(Action<bool, string> check)
        {
            check(AppUpdateInstaller.EligibilityError(Installed(), IdentityDirectory) == null,
                "A registered custom per-user installation with positive ordinary-token evidence can offer installation");
            var equivalent = Installed(); equivalent.ExecutablePath = @"c:\owned app\TaskbarSelect.exe";
            check(AppUpdateInstaller.EligibilityError(equivalent, IdentityDirectory) == null,
                "Installation identity compares canonical Windows paths without case sensitivity");
            var alias = Installed(); alias.ExecutablePath = @"C:\Owned Alias\TaskbarSelect.exe";
            check(AppUpdateInstaller.EligibilityError(alias, delegate(string path) { return path == @"C:\Owned Alias" ? @"C:\Owned App" : IdentityDirectory(path); }) == null,
                "Physical directory resolution handles a legitimate registered installation alias");

            var denials = new List<Action<AppUpdateInstaller.EligibilityEvidence>> {
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.DesktopRuntime = false; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.Production = false; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.Identity = null; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.Identity.IntegrityAvailable = false; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.Identity.ElevationAvailable = false; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.Identity.Elevated = true; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.Identity.IntegrityLevel = NativeProcessIdentity.Low; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.Identity.IntegrityLevel = NativeProcessIdentity.MediumPlus; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.Identity.IntegrityLevel = NativeProcessIdentity.High; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.RegistrationPresent = false; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.RegistrationValid = false; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.ExecutableSafe = false; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.UninstallerSafe = false; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.ExecutablePath = @"C:\Portable\TaskbarSelect.exe"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.ExecutablePath = @"C:\Owned App\Other.exe"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.ExecutablePath = @"\\server\Owned App\TaskbarSelect.exe"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.ExecutablePath = @"C:\Owned App\..\Portable\TaskbarSelect.exe"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.InstallLocation = @"C:\Other Installation"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.InstallLocation = @"C:\Owned App\child"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.InstallLocation = @"C:\"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.InstallLocation = @"%LOCALAPPDATA%\Programs\Taskbar Select"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.UninstallString = "\"C:\\Portable\\unins000.exe\""; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.UninstallString = "\"C:\\Owned App\\unins000.exe\" /silent"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.UninstallString = "cmd.exe /c anything"; },
                delegate(AppUpdateInstaller.EligibilityEvidence e) { e.UninstallString = null; }
            };
            for (int index = 0; index < denials.Count; index++) {
                var evidence = Installed(); denials[index](evidence);
                check(AppUpdateInstaller.EligibilityError(evidence, IdentityDirectory) != null,
                    "Portable, isolated, elevated or ambiguous installation evidence cannot launch an update " + index);
            }
            check(AppUpdateInstaller.EligibilityError(Installed(), delegate(string path) { throw new IOException("Owned unavailable directory"); }) != null,
                "Directory metadata failure cannot grant installation eligibility");
            check(AppUpdateInstaller.EligibilityError(null, IdentityDirectory) != null && AppUpdateInstaller.EligibilityError(Installed(), null) != null,
                "Missing evidence or canonical resolver fails closed");
            var fallback = Installed(); fallback.InstallLocation = null;
            fallback.ExecutablePath = Path.Combine(fallback.DefaultLocation, "TaskbarSelect.exe");
            fallback.UninstallString = "\"" + Path.Combine(fallback.DefaultLocation, "unins000.exe") + "\"";
            check(AppUpdateInstaller.EligibilityError(fallback, IdentityDirectory) == null,
                "Older registration without InstallLocation can identify only the fixed default directory");
            fallback.ExecutablePath = @"C:\Portable\TaskbarSelect.exe"; fallback.UninstallString = "\"C:\\Portable\\unins000.exe\"";
            check(AppUpdateInstaller.EligibilityError(fallback, IdentityDirectory) != null,
                "An omitted installation directory cannot be inferred from an arbitrary portable executable");
            foreach (string command in new[] { @"C:\Tools\unins000.exe", "\"C:\\Owned App\\unins001.exe\"" })
                check(AppUpdateInstaller.UninstallerPath(command) != null, "Only a single inert uninstall-image path is accepted as registry evidence");
            foreach (string command in new[] { "", "\"C:\\Owned App\\unins000.exe\" ", @"C:\Tools\unins000.exe /SILENT", @"C:\Tools\other.exe", @"\\server\app\unins000.exe", @"C:\Tools\unins000.exe:extra", @"C:\Tools\unins000.exe.bat", "\"C:\\Tools\\unins000.exe\"\"" })
                check(AppUpdateInstaller.UninstallerPath(command) == null, "Malformed or command-bearing uninstall registry data is never accepted");

            int probes = 0;
            check(AppUpdateInstaller.PermissionError(Off(), delegate { probes++; return false; }) == null && probes == 1,
                "Fully Off permission evidence still requires a fresh positive saved-approval absence probe");
            check(AppUpdateInstaller.PermissionError(Off(), delegate { return true; }) != null,
                "Preference and helper absence cannot conceal retained saved approval");
            check(AppUpdateInstaller.PermissionError(Off(), delegate { throw new IOException("Owned unreadable approval"); }) != null,
                "Unreadable or corrupt saved approval cannot authorize an update");
            var permissionDenials = new Action<AppUpdateInstaller.PermissionEvidence>[] {
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Available = false; },
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Busy = true; },
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Checked = true; },
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Active = true; },
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Enabling = true; },
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Uncertain = true; },
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Remembered = true; },
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Status = "Off not confirmed"; },
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Status = "Recovery pending"; },
                delegate(AppUpdateInstaller.PermissionEvidence e) { e.Status = null; }
            };
            for (int index = 0; index < permissionDenials.Length; index++) {
                var evidence = Off(); permissionDenials[index](evidence); int approvalCalls = 0;
                check(AppUpdateInstaller.PermissionError(evidence, delegate { approvalCalls++; return false; }) != null && approvalCalls == 0,
                    "On, cancelled Off, busy and uncertain states block installation without changing permissions " + index);
            }
            check(AppUpdateInstaller.PermissionError(null, delegate { return false; }) != null && AppUpdateInstaller.PermissionError(Off(), null) != null,
                "Unavailable permission evidence or protected probe cannot authorize installation");

            ProcessStartInfo start = AppUpdateInstaller.StartInfo(@"C:\Owned Update\TaskbarSelect-1.0.0-beta.15-Setup.exe");
            check(start.UseShellExecute && start.Verb == "open" && start.Arguments == "" && !start.ErrorDialog &&
                start.FileName == @"C:\Owned Update\TaskbarSelect-1.0.0-beta.15-Setup.exe" && start.WorkingDirectory == @"C:\Owned Update",
                "Setup launch uses the fixed absolute verified file through normal Shell open without elevation, shell commands or arguments");
            foreach (string invalid in new[] { @"\\server\app\TaskbarSelect-1.0.0-Setup.exe", @"C:\Owned Update\other.exe", @"C:\Owned Update\TaskbarSelect-1.0.0-Setup.exe /silent", @"C:\Owned Update\TaskbarSelect-1.0.0-Setup.exe.bat", @"C:\Owned Update\..\TaskbarSelect-1.0.0-Setup.exe", "https://example.invalid/TaskbarSelect-1.0.0-Setup.exe" }) {
                bool rejected = false; try { AppUpdateInstaller.StartInfo(invalid); } catch (InvalidOperationException) { rejected = true; }
                check(rejected, "Only a local canonical versioned Setup path can become a process launch target");
            }

            var calls = new List<string>();
            object sentinel = new object();
            object launched = AppUpdateInstaller.VerifiedLaunch("1.0.0-beta.15", "1.0.0-beta.14",
                delegate { calls.Add("guard"); return null; }, delegate { calls.Add("verify"); }, delegate { calls.Add("start"); return sentinel; });
            check(Object.ReferenceEquals(launched, sentinel) && String.Join(",", calls) == "guard,verify,guard,start",
                "An explicit install rechecks installation and approval after held-file verification immediately before starting Setup");
            foreach (string target in new[] { "1.0.0-beta.14", "1.0.0-beta.9", "1.0.0-beta.15-persistent-test", "v1.0.0-beta.15", "invalid" }) {
                calls.Clear(); bool rejected = false;
                try { AppUpdateInstaller.VerifiedLaunch(target, "1.0.0-beta.14", delegate { calls.Add("guard"); return null; }, delegate { calls.Add("verify"); }, delegate { calls.Add("start"); return sentinel; }); }
                catch (AppUpdateException) { rejected = true; }
                check(rejected && calls.Count == 0, "Downgrades, same version, malformed and experimental target versions cannot reach installer launch");
            }
            calls.Clear(); bool badCurrent = false;
            try { AppUpdateInstaller.VerifiedLaunch("1.0.0-beta.15", "1.0.0-beta.14-test", delegate { calls.Add("guard"); return null; }, delegate { calls.Add("verify"); }, delegate { calls.Add("start"); return sentinel; }); }
            catch (AppUpdateException) { badCurrent = true; }
            check(badCurrent && calls.Count == 0, "An unsupported current build cannot install into production through the updater");

            for (int deniedPass = 0; deniedPass < 2; deniedPass++) {
                calls.Clear(); int pass = 0; int denied = deniedPass; bool refused = false;
                try { AppUpdateInstaller.VerifiedLaunch("1.0.0-beta.15", "1.0.0-beta.14",
                    delegate { calls.Add("guard"); return pass++ == denied ? "Owned denial" : null; },
                    delegate { calls.Add("verify"); }, delegate { calls.Add("start"); return sentinel; }); }
                catch (AppUpdateException) { refused = true; }
                check(refused && String.Join(",", calls) == (deniedPass == 0 ? "guard" : "guard,verify,guard"),
                    "Unavailable installation or approval reappearing during verification never starts an installer " + deniedPass);
            }
            calls.Clear(); bool fileChanged = false;
            try { AppUpdateInstaller.VerifiedLaunch("1.0.0-beta.15", "1.0.0-beta.14", delegate { calls.Add("guard"); return null; },
                delegate { calls.Add("verify"); throw new IOException("Owned staged-file verification failure"); }, delegate { calls.Add("start"); return sentinel; }); }
            catch (IOException) { fileChanged = true; }
            check(fileChanged && String.Join(",", calls) == "guard,verify", "Changed staged bytes never reach the process launcher");
            calls.Clear(); bool launchFailed = false;
            try { AppUpdateInstaller.VerifiedLaunch<object>("1.0.0-beta.15", "1.0.0-beta.14", delegate { calls.Add("guard"); return null; },
                delegate { calls.Add("verify"); }, delegate { calls.Add("start"); throw new IOException("Owned Shell cancellation fixture"); }); }
            catch (IOException) { launchFailed = true; }
            check(launchFailed && String.Join(",", calls) == "guard,verify,guard,start", "Installer launch failure propagates without changing settings or permission state");
            check(AppUpdateInstaller.VerifiedLaunch("1.0.0", "1.0.0-beta.65535", delegate { return null; }, delegate { }, delegate { return sentinel; }) == sentinel,
                "A final release supersedes the beta channel through the same guarded launch policy");
        }
    }
}
