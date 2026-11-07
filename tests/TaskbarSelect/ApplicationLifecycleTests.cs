using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskbarSelect
{
    internal static class ApplicationLifecycleTests
    {
        sealed class Environment : ApplicationLifecycle.IEnvironment
        {
            internal readonly List<string> Calls = new List<string>();
            internal bool IsProduction = true, IsOrdinary = true, Approval, ApprovalAfterStop;
            internal bool ProbeFails, RestoreFails, StopFails, CleanupFails, NullLease, NullRecovery;
            int probes;
            public bool Production { get { return IsProduction; } }
            public bool OrdinaryUser { get { return IsOrdinary; } }
            public bool RequiresApprovalRemoval()
            {
                Calls.Add("probe");
                if (ProbeFails) throw new InvalidOperationException("Owned corrupt/unknown registration fixture");
                return ++probes == 1 ? Approval : ApprovalAfterStop;
            }
            public IDisposable StopAndAcquire()
            {
                Calls.Add("stop");
                if (StopFails) throw new InvalidOperationException("Owned unconfirmed retirement fixture");
                return NullLease ? null : new Lease(Calls);
            }
            public RecoveryResult Restore()
            { Calls.Add("restore"); return NullRecovery ? null : new RecoveryResult { Success = !RestoreFails }; }
            public void RemoveUserState()
            { Calls.Add("cleanup"); if (CleanupFails) throw new InvalidOperationException("Owned user-state cleanup failure"); }
        }
        sealed class Lease : IDisposable
        {
            readonly List<string> calls;
            internal Lease(List<string> value) { calls = value; }
            public void Dispose() { calls.Add("release"); }
        }
        internal static void Run(Action<bool, string> check)
        {
            foreach (var action in new[] { LifecycleAction.Upgrade, LifecycleAction.Uninstall }) {
                var success = new Environment();
                check(ApplicationLifecycle.Run(action, success).Success, "Owned lifecycle succeeds only after confirmed absence and recovery " + action);
                check(String.Join(",", success.Calls) == (action == LifecycleAction.Uninstall ? "probe,stop,restore,probe,cleanup,release" : "probe,stop,restore,probe,release"),
                    "Lifecycle holds stop ownership across final approval check and user cleanup " + action);
                var approval = new Environment { Approval = true };
                var refused = ApplicationLifecycle.Run(action, approval);
                check(!refused.Success && refused.Messages.Any(line => line.Contains("turn Administrator support off")), "Present approval requires explicit Settings action " + action);
                check(String.Join(",", approval.Calls) == "probe", "Present approval preserves running UI/settings/Startup " + action);
                var corrupt = new Environment { ProbeFails = true };
                check(!ApplicationLifecycle.Run(action, corrupt).Success && String.Join(",", corrupt.Calls) == "probe", "Unknown/corrupt approval is never treated as missing " + action);
                var race = new Environment { ApprovalAfterStop = true };
                check(!ApplicationLifecycle.Run(action, race).Success && String.Join(",", race.Calls) == "probe,stop,restore,probe,release", "Approval reappearing after preflight blocks upgrade and uninstall " + action);
                var recovery = new Environment { RestoreFails = true };
                check(!ApplicationLifecycle.Run(action, recovery).Success && String.Join(",", recovery.Calls) == "probe,stop,restore,release", "Failed recovery retains settings and Startup " + action);
                var stop = new Environment { StopFails = true };
                check(!ApplicationLifecycle.Run(action, stop).Success && String.Join(",", stop.Calls) == "probe,stop", "Unconfirmed stop prevents restore/cleanup " + action);
                var nullStop = new Environment { NullLease = true };
                check(!ApplicationLifecycle.Run(action, nullStop).Success && !nullStop.Calls.Contains("restore"), "Missing stop lease cannot authorize lifecycle " + action);
                var nullRecovery = new Environment { NullRecovery = true };
                check(!ApplicationLifecycle.Run(action, nullRecovery).Success && !nullRecovery.Calls.Contains("cleanup"), "Missing recovery result cannot authorize user-state deletion " + action);
                foreach (var denied in new[] { new Environment { IsProduction = false }, new Environment { IsOrdinary = false } })
                    check(!ApplicationLifecycle.Run(action, denied).Success && denied.Calls.Count == 0, "Wrong profile/token rejected before signaling desktop " + action);
            }
            var cleanup = new Environment { CleanupFails = true };
            check(!ApplicationLifecycle.Run(LifecycleAction.Uninstall, cleanup).Success && cleanup.Calls.Last() == "release", "User cleanup failure remains installer failure and releases confirmed desktop lease");
            check(!ApplicationLifecycle.Run((LifecycleAction)42, new Environment()).Success, "Unknown lifecycle action cannot change state");
            check(!ApplicationLifecycle.Run(LifecycleAction.Upgrade, null).Success, "Missing lifecycle environment cannot change state");
            check(ApplicationLifecycle.Parse(new[] { "--prepare-upgrade" }) == LifecycleAction.Upgrade && ApplicationLifecycle.Parse(new[] { "--uninstall" }) == LifecycleAction.Uninstall,
                "Only fixed upgrade and uninstall modes are accepted");
            foreach (string[] malformed in new[] { new string[0], new[] { "--restore" }, new[] { "--uninstall", "--inspect" }, new[] { "--prepare-upgrade", "--test-profile", "C:\\Owned" }, new[] { "--prepare-upgrade", "--prepare-upgrade" } }) {
                bool rejected = false; try { ApplicationLifecycle.Parse(malformed); } catch (ArgumentException) { rejected = true; }
                check(rejected, "Malformed lifecycle arguments fail before any desktop interaction");
            }
            check(ApplicationLifecycle.HasCommand(new[] { "--uninstall", "--inspect" }) && ApplicationLifecycle.HasCommand(new[] { "--prepare-upgrade" }) &&
                !ApplicationLifecycle.HasCommand(new[] { "--self-test-lifecycle" }) && !ApplicationLifecycle.HasCommand(null), "Lifecycle commands are recognized even when malformed and never fall through to UI");
        }
    }
}
