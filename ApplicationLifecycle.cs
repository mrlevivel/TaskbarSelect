using System;

namespace TaskbarSelect
{
    internal enum LifecycleAction { Upgrade, Uninstall }

    // Permission changes remain an explicit Settings action. Installers may
    // proceed only after protected approval is positively absent, regardless
    // of the preference value or whether a helper is currently visible.
    internal static class ApplicationLifecycle
    {
        internal const string Capability = "TASKBARSELECT_INSTALLER_LIFECYCLE_V1";
        internal const string ApprovalRequired = "Open Taskbar Select Settings, turn Administrator support off, approve Windows if requested, and wait until it is fully off before retrying. Settings and Startup were retained.";

        internal interface IEnvironment
        {
            bool Production { get; }
            bool OrdinaryUser { get; }
            bool RequiresApprovalRemoval();
            IDisposable StopAndAcquire();
            RecoveryResult Restore();
            void RemoveUserState();
        }

        internal static bool HasCommand(string[] args)
        { return args != null && (Array.IndexOf(args, "--prepare-upgrade") >= 0 || Array.IndexOf(args, "--uninstall") >= 0); }

        internal static LifecycleAction Parse(string[] args)
        {
            if (args == null || args.Length != 1 || (args[0] != "--prepare-upgrade" && args[0] != "--uninstall"))
                throw new ArgumentException("Use exactly one production lifecycle command without additional options.");
            return args[0] == "--uninstall" ? LifecycleAction.Uninstall : LifecycleAction.Upgrade;
        }

        internal static RecoveryResult Run(LifecycleAction action, IEnvironment environment)
        {
            var result = new RecoveryResult();
            try {
                if (environment == null || !environment.Production || !environment.OrdinaryUser)
                    throw new InvalidOperationException("Run the production installer normally, without Run as administrator. Isolated profiles cannot perform upgrade or uninstall.");
                if (action != LifecycleAction.Upgrade && action != LifecycleAction.Uninstall) throw new ArgumentException("Unknown lifecycle operation.");
                // Do not close the Settings UI that the person needs to revoke
                // approval. Missing/corrupt/partial probes must fail closed.
                if (environment.RequiresApprovalRemoval()) throw new InvalidOperationException(ApprovalRequired);
                using (var stopped = environment.StopAndAcquire()) {
                    if (stopped == null) throw new InvalidOperationException("The running app has not confirmed that it stopped. Recovery and settings were retained.");
                    result = environment.Restore();
                    if (result == null) throw new InvalidOperationException("Recovery could not be confirmed.");
                    if (!result.Success) return result;
                    // Close the preflight-to-stop race before deleting any
                    // preferences/Startup or permitting binary replacement.
                    if (environment.RequiresApprovalRemoval()) throw new InvalidOperationException(ApprovalRequired);
                    if (action == LifecycleAction.Uninstall) environment.RemoveUserState();
                }
            } catch (Exception error) {
                if (result == null) result = new RecoveryResult();
                result.Success = false;
                result.Messages.Add("Taskbar Select lifecycle stopped: " + error.Message);
            }
            return result;
        }
    }
}
