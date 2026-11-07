using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarSelect
{
    // An update is an ordinary per-user installer launch. It never gains
    // administrator authority or bypasses the installer's lifecycle checks.
    internal static class AppUpdateInstaller
    {
        internal const string RegistrationPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A446A470-529B-42E0-AD2D-D24B31864DFC}_is1";
        internal const string PortableError = "This is a portable copy. Use the release page to update it manually, or open the installed Taskbar Select.";
        internal const string ApprovalError = "Turn Administrator support Off in Settings and approve Windows if requested. Wait until support is fully Off before installing the update.";
        const string VerificationError = "The installed app could not be verified. Use the release page to update it manually.";

        internal sealed class EligibilityEvidence
        {
            internal bool DesktopRuntime, Production, RegistrationPresent, RegistrationValid;
            internal bool ExecutableSafe, UninstallerSafe;
            internal NativeProcessIdentity.Info Identity;
            internal string ExecutablePath, InstallLocation, UninstallString, DefaultLocation;
        }

        internal sealed class PermissionEvidence
        {
            internal bool Available, Busy, Checked, Active, Enabling, Uncertain, Remembered;
            internal string Status;
        }

        internal static string EligibilityError(bool desktopRuntime)
        {
            try { return EligibilityError(ReadEligibility(desktopRuntime), AppProfile.ResolveDirectory); }
            catch (Exception) { return VerificationError; }
        }

        internal static string EligibilityError(EligibilityEvidence evidence, Func<string, string> resolveDirectory)
        {
            if (evidence == null || resolveDirectory == null) return VerificationError;
            if (!evidence.DesktopRuntime) return "Open the installed Taskbar Select normally to install an update.";
            if (!evidence.Production) return "An isolated tester can check for updates, but cannot replace your installed app.";
            var identity = evidence.Identity;
            if (identity == null || !identity.IntegrityAvailable || !identity.ElevationAvailable || identity.Elevated ||
                identity.IntegrityLevel != NativeProcessIdentity.Medium)
                return "Open the installed Taskbar Select normally, without Run as administrator, to install an update.";
            if (!evidence.RegistrationPresent) return PortableError;
            if (!evidence.RegistrationValid || !evidence.ExecutableSafe || !evidence.UninstallerSafe) return VerificationError;
            try {
                string executable = AdministratorLaunch.CanonicalDosPath(evidence.ExecutablePath);
                if (executable == null || !String.Equals(Path.GetFileName(executable), "TaskbarSelect.exe", StringComparison.OrdinalIgnoreCase)) return VerificationError;
                string location = DirectoryPath(evidence.InstallLocation);
                // Some older installer registrations omit InstallLocation. They
                // may identify only the fixed default installation, never an
                // arbitrary portable folder chosen by the running executable.
                if (location == null && String.IsNullOrEmpty(evidence.InstallLocation)) location = DirectoryPath(evidence.DefaultLocation);
                string uninstaller = UninstallerPath(evidence.UninstallString);
                if (location == null || uninstaller == null) return VerificationError;
                string physical = resolveDirectory(location);
                if (String.IsNullOrEmpty(physical) || !SameDirectory(physical, resolveDirectory(Path.GetDirectoryName(executable))) ||
                    !SameDirectory(physical, resolveDirectory(Path.GetDirectoryName(uninstaller)))) return PortableError;
                return null;
            } catch (Exception) { return VerificationError; }
        }

        static bool SameDirectory(string left, string right)
        { return !String.IsNullOrEmpty(left) && !String.IsNullOrEmpty(right) && String.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }

        static string DirectoryPath(string path)
        {
            if (String.IsNullOrEmpty(path)) return null;
            string value = AdministratorLaunch.CanonicalDosPath(path.TrimEnd('\\'));
            return value == null || value.Length <= 3 ? null : value;
        }

        internal static string UninstallerPath(string command)
        {
            if (String.IsNullOrEmpty(command)) return null;
            // Registry strings are identity evidence, never executable commands.
            // Accept one quoted path or one space-free path without arguments.
            string path;
            if (command[0] == '"') {
                if (command.Length < 3 || command[command.Length - 1] != '"' || command.IndexOf('"', 1) != command.Length - 1) return null;
                path = command.Substring(1, command.Length - 2);
            } else {
                if (command.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"' }) >= 0) return null;
                path = command;
            }
            path = AdministratorLaunch.CanonicalDosPath(path);
            return path != null && Regex.IsMatch(Path.GetFileName(path), @"\Aunins[0-9]{3}\.exe\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? path : null;
        }

        static bool OrdinaryFile(string path)
        {
            if (AdministratorLaunch.CanonicalDosPath(path) == null || !File.Exists(path)) return false;
            return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }

        static EligibilityEvidence ReadEligibility(bool desktopRuntime)
        {
            var result = new EligibilityEvidence {
                DesktopRuntime = desktopRuntime, Production = !AppProfile.IsTest && AppProfile.ProductionSafe(),
                Identity = NativeProcessIdentity.Current, ExecutablePath = Application.ExecutablePath,
                DefaultLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Taskbar Select")
            };
            if (!result.DesktopRuntime || !result.Production) return result;
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var key = hive.OpenSubKey(RegistrationPath, false)) {
                if (key == null) return result;
                result.RegistrationPresent = true;
                object location = key.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                object uninstall = key.GetValue("UninstallString", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                result.RegistrationValid = (location == null || location is string) && uninstall is string &&
                    (location == null || key.GetValueKind("InstallLocation") == RegistryValueKind.String) && key.GetValueKind("UninstallString") == RegistryValueKind.String;
                if (!result.RegistrationValid) return result;
                result.InstallLocation = location as string;
                result.UninstallString = (string)uninstall;
                string uninstaller = UninstallerPath(result.UninstallString);
                result.ExecutableSafe = OrdinaryFile(result.ExecutablePath);
                result.UninstallerSafe = uninstaller != null && OrdinaryFile(uninstaller) && OrdinaryFile(Path.ChangeExtension(uninstaller, ".dat"));
            }
            return result;
        }

        internal static string InstallError(Controller controller)
        {
            if (controller == null) return VerificationError;
            string eligibility = EligibilityError(controller.DesktopRuntime);
            if (eligibility != null) return eligibility;
            try {
                var permissions = controller.AdministratorPermissions;
                var support = controller.Administrator;
                return PermissionError(new PermissionEvidence {
                    Available = permissions != null && support != null && controller.Settings != null,
                    Busy = permissions != null && permissions.Busy, Checked = permissions != null && permissions.Checked,
                    Status = permissions == null ? null : permissions.Status,
                    Active = support != null && support.Active, Enabling = support != null && support.Enabling,
                    Uncertain = support != null && support.HasUncertainOperation,
                    Remembered = controller.Settings != null && controller.Settings.PersistentAdministratorSupport
                }, delegate { return PersistentAdministrator.RequiresApprovalRemoval(PersistentAdministrator.ScopeForCurrentProfile()); });
            } catch (Exception) { return "Administrator support and saved approval could not be checked. Finish recovery or turn support Off before retrying the update."; }
        }

        internal static string PermissionError(PermissionEvidence evidence, Func<bool> requiresApprovalRemoval)
        {
            if (evidence == null || !evidence.Available || requiresApprovalRemoval == null) return ApprovalError;
            if (evidence.Busy || evidence.Checked || evidence.Active || evidence.Enabling || evidence.Uncertain || evidence.Remembered ||
                !String.Equals(evidence.Status, "Off", StringComparison.Ordinal)) return ApprovalError;
            try { return requiresApprovalRemoval() ? ApprovalError : null; }
            catch (Exception) { return "Saved administrator approval could not be verified as removed. Finish turning support Off before installing the update."; }
        }

        internal static ProcessStartInfo StartInfo(string path)
        {
            string canonical = AdministratorLaunch.CanonicalDosPath(path);
            if (canonical == null || !Regex.IsMatch(Path.GetFileName(canonical), @"\ATaskbarSelect-[0-9]+\.[0-9]+\.[0-9]+(?:-beta\.[0-9]+)?-Setup\.exe\z", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("The verified update installer path is invalid.");
            return new ProcessStartInfo {
                FileName = canonical, Arguments = "", WorkingDirectory = Path.GetDirectoryName(canonical),
                Verb = "open", UseShellExecute = true, ErrorDialog = false
            };
        }

        internal static T VerifiedLaunch<T>(string target, string current, Func<string> preflight, Action verify, Func<T> start)
        {
            AppUpdateVersion candidate, installed;
            if (!AppUpdateVersion.TryParse(target, out candidate) || !AppUpdateVersion.TryParse(current, out installed) || candidate.CompareTo(installed) <= 0)
                throw new AppUpdateException("This installer is not a newer supported release. Check for updates again.");
            if (preflight == null || verify == null || start == null) throw new AppUpdateException("The update launcher is unavailable.");
            string error = preflight();
            if (error != null) throw new AppUpdateException(error);
            verify();
            // Verification can take time. A changed permission or installation
            // state must not slip through the earlier UI/pre-download check.
            error = preflight();
            if (error != null) throw new AppUpdateException(error);
            return start();
        }

        static readonly object launchGate = new object();
        static bool launching;
        static LaunchSession activeLaunch;

        internal static Process Launch(VerifiedAppUpdate update, Controller controller)
        {
            if (update == null) throw new AppUpdateException("Download and verify the update before installing it.");
            lock (launchGate) {
                if (launching || activeLaunch != null) throw new AppUpdateException("An update installer is already open. Finish or close it before retrying.");
                launching = true;
            }
            try {
                Process process = VerifiedLaunch(update.Version, Application.ProductVersion,
                    delegate { return InstallError(controller); }, update.VerifyForLaunch,
                    delegate { return Process.Start(StartInfo(update.InstallerPath)); });
                if (process == null) {
                    // Shell may have started an executable without returning a
                    // process handle. Keep its backing bytes even on this error.
                    update.MarkLaunched();
                    throw new AppUpdateException("Windows did not return an installer process. Check whether Setup opened before trying again.");
                }
                update.MarkLaunched();
                var session = new LaunchSession(update, process);
                lock (launchGate) activeLaunch = session;
                session.Watch();
                // Successful launch transfers descriptor ownership from the UI.
                // The installer independently performs stop and safe recovery;
                // this method never exits the app or changes administrator state.
                return process;
            } finally { lock (launchGate) launching = false; }
        }

        sealed class LaunchSession
        {
            readonly VerifiedAppUpdate update;
            readonly Process process;
            readonly object gate = new object();
            bool finished;
            internal LaunchSession(VerifiedAppUpdate value, Process started) { update = value; process = started; }
            internal void Watch()
            {
                try {
                    process.Exited += Exited;
                    process.EnableRaisingEvents = true;
                    if (process.HasExited) Complete();
                } catch (Exception) {
                    // An unobservable launched installer is not proof of exit.
                    // Retain the descriptor until ordinary process shutdown.
                }
            }
            void Exited(object sender, EventArgs args) { Complete(); }
            void Complete()
            {
                lock (gate) { if (finished) return; finished = true; }
                try { process.Exited -= Exited; } catch (Exception) { }
                try { update.Dispose(); } catch (Exception) { }
                try { process.Dispose(); } catch (Exception) { }
                lock (launchGate) { if (Object.ReferenceEquals(activeLaunch, this)) activeLaunch = null; }
            }
        }
    }
}
