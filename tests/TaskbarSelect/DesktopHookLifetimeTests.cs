using System;

namespace TaskbarSelect
{
    internal static class DesktopHookLifetimeTests
    {
        internal static void Run(Action<bool, string> check)
        {
            int installs = 0, removals = 0;
            var life = new DesktopHookLifetime(delegate { installs++; return true; }, delegate { removals++; return true; });
            check(installs == 0 && removals == 0 && !life.RegistrationClaimed,
                "Constructing hook lifetime performs no registration before the application pump");
            for (int i = 0; i < 20; i++) check(!life.Start(false, true), "Repeated pre-pump startup cannot register a desktop hook");
            check(!life.Start(true, false) && installs == 0 && life.LastOutcome == DesktopHookLifetime.Outcome.WrongThread,
                "A ready pump on the wrong owner thread cannot register");
            check(life.Start(true, true) && installs == 1 && life.RegistrationClaimed,
                "First explicit ready owner-pump start makes exactly one registration claim");
            for (int i = 0; i < 20; i++) check(life.Start(true, true), "Repeated refresh keeps the existing registration claim");
            check(installs == 1 && removals == 0 && life.Format().Contains("callbackHealth=unknown"),
                "Refresh does not duplicate hooks or misrepresent a stored registration as callback health");
            check(!life.TryRearm(true, true, false) && installs == 1 && removals == 0,
                "Unsafe held-input/gesture/movement boundary refuses rearm without any hook change");
            check(!life.TryRearm(false, true, true) && !life.TryRearm(true, false, true) && installs == 1 && removals == 0,
                "Rearm still requires both the owner pump and owner thread");
            check(life.TryRearm(true, true, true) && installs == 2 && removals == 1 && life.RearmCount == 1,
                "Explicit safe rearm retires exactly once before replacement installation");
            life.Dispose(); life.Dispose();
            check(removals == 2 && !life.RegistrationClaimed && life.IsDisposed && !life.Start(true, true) && !life.TryRearm(true, true, true),
                "Dispose unregisters the owned registration once and permanently prevents restart");

            int failedInstalls = 0;
            using (var failed = new DesktopHookLifetime(delegate { failedInstalls++; return false; }, delegate { throw new Exception("No hook is owned"); })) {
                check(!failed.Start(true, true) && !failed.RegistrationClaimed && failed.LastOutcome == DesktopHookLifetime.Outcome.InstallFailed,
                    "Rejected registration remains unavailable instead of claiming a valid hook");
                check(!failed.Start(true, true) && failed.ConsecutiveInstallFailures == 2,
                    "An explicit second attempt consumes the bounded startup retry");
                for (int i = 0; i < 20; i++) check(!failed.Start(true, true), "Exhausted startup budget cannot loop native registration");
                check(failedInstalls == 2 && failed.LastOutcome == DesktopHookLifetime.Outcome.RetryLimit && failed.RetirementAttempts == 0,
                    "Retry exhaustion is truthful and never uninstalls an unowned hook");
            }
            int recoveryInstalls = 0, recoveryRemovals = 0;
            using (var recovered = new DesktopHookLifetime(delegate { recoveryInstalls++; return recoveryInstalls != 1 && recoveryInstalls != 3; }, delegate { recoveryRemovals++; return true; })) {
                check(!recovered.Start(true, true) && recovered.Start(true, true) && recovered.ConsecutiveInstallFailures == 0,
                    "A successful explicit retry resets only the consecutive installation failure budget");
                check(!recovered.TryRearm(true, true, true) && !recovered.RegistrationClaimed && recoveryRemovals == 1,
                    "Replacement install failure after confirmed retirement leaves no stale claim");
                check(recovered.Start(true, true) && recoveryInstalls == 4,
                    "A later explicit retry can recover within the reset bounded budget");
            }
            check(recoveryRemovals == 2, "Recovered registration receives one disposal retirement");

            int uncertainInstalls = 0, uncertainRemovals = 0;
            var uncertain = new DesktopHookLifetime(delegate { uncertainInstalls++; return true; }, delegate { uncertainRemovals++; return false; });
            check(uncertain.Start(true, true) && !uncertain.TryRearm(true, true, true) && uncertain.RegistrationClaimed && uncertain.RetirementUncertain,
                "Unconfirmed removal retains its ownership claim and reports uncertainty");
            check(!uncertain.Start(true, true) && !uncertain.TryRearm(true, true, true) && uncertainInstalls == 1 && uncertainRemovals == 1,
                "Unconfirmed removal cannot create a duplicate registration or repeated rearm removal");
            uncertain.Dispose(); uncertain.Dispose();
            check(uncertainRemovals == 2 && uncertain.IsDisposed && uncertain.RetirementUncertain,
                "Disposal makes one final owned cleanup attempt and does not fabricate successful removal");

            int exceptions = 0;
            using (var throwing = new DesktopHookLifetime(delegate { exceptions++; throw new Exception("PRIVATE FIXTURE CONTENT"); }, delegate { return true; }, 1)) {
                check(!throwing.Start(true, true) && throwing.LastOutcome == DesktopHookLifetime.Outcome.InstallException && !throwing.RegistrationClaimed,
                    "Adapter exception is contained without a false registration claim");
                check(!throwing.Start(true, true) && exceptions == 1 && !throwing.Format().Contains("PRIVATE"),
                    "Exception retries are bounded and diagnostic text contains no exception content");
            }
            var beforeStart = new DesktopHookLifetime(delegate { throw new Exception("Disposed fixture cannot install"); }, delegate { throw new Exception("Disposed fixture owns nothing"); });
            beforeStart.Dispose(); beforeStart.Dispose();
            check(!beforeStart.Start(true, true) && beforeStart.InstallAttempts == 0 && beforeStart.RetirementAttempts == 0,
                "Disposal before queued startup makes both adapters unreachable");

            DesktopHookLifetime reentrant = null;
            int reentrantInstalls = 0, reentrantRemovals = 0;
            reentrant = new DesktopHookLifetime(delegate {
                reentrantInstalls++;
                check(!reentrant.Start(true, true) && reentrant.LastOutcome == DesktopHookLifetime.Outcome.Busy,
                    "Reentrant startup cannot install a second hook while the first adapter is running");
                reentrant.Dispose(); return true;
            }, delegate { reentrantRemovals++; return true; });
            check(!reentrant.Start(true, true) && reentrant.IsDisposed && !reentrant.RegistrationClaimed && reentrantInstalls == 1 && reentrantRemovals == 1,
                "Disposal during registration retires its eventual claim exactly once before returning");
            reentrant.Dispose();
            check(reentrantRemovals == 1, "Repeated disposal after deferred cleanup cannot retire twice");
        }
    }
}
