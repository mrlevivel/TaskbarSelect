using System;
using System.Globalization;

namespace TaskbarSelect
{
    // Registration is a local ownership claim, never proof that Windows still delivers callbacks.
    // Caller supplies actual pump/thread/safe-gesture observations; this class performs no native work.
    internal sealed class DesktopHookLifetime : IDisposable
    {
        internal enum Outcome
        {
            Created, Registered, AlreadyRegistered, PumpNotReady, WrongThread, UnsafeRearm,
            RetryLimit, InstallFailed, InstallException, UninstallFailed, UninstallException, Busy, Disposed
        }
        readonly object gate = new object();
        readonly Func<bool> install, uninstall;
        readonly int maximumAttempts;
        bool registrationClaimed, disposed, operating, retirementUncertain, disposeRetirementAttempted;
        int attempts, consecutiveFailures, retirements, rearms;
        Outcome last;

        internal DesktopHookLifetime(Func<bool> install, Func<bool> uninstall, int maximumConsecutiveInstallAttempts = 2)
        {
            if (install == null || uninstall == null) throw new ArgumentNullException();
            if (maximumConsecutiveInstallAttempts < 1 || maximumConsecutiveInstallAttempts > 16)
                throw new ArgumentOutOfRangeException("maximumConsecutiveInstallAttempts");
            this.install = install; this.uninstall = uninstall; maximumAttempts = maximumConsecutiveInstallAttempts;
        }
        internal bool RegistrationClaimed { get { lock (gate) return registrationClaimed; } }
        internal bool IsDisposed { get { lock (gate) return disposed; } }
        internal bool RetirementUncertain { get { lock (gate) return retirementUncertain; } }
        internal int InstallAttempts { get { lock (gate) return attempts; } }
        internal int ConsecutiveInstallFailures { get { lock (gate) return consecutiveFailures; } }
        internal int RetirementAttempts { get { lock (gate) return retirements; } }
        internal int RearmCount { get { lock (gate) return rearms; } }
        internal Outcome LastOutcome { get { lock (gate) return last; } }
        static int Increment(int value) { return value == Int32.MaxValue ? value : value + 1; }
        bool MayStart(bool pumpReady, bool ownerThreadMatch)
        {
            if (disposed) { last = Outcome.Disposed; return false; }
            if (operating) { last = Outcome.Busy; return false; }
            if (!pumpReady) { last = Outcome.PumpNotReady; return false; }
            if (!ownerThreadMatch) { last = Outcome.WrongThread; return false; }
            if (retirementUncertain) { last = Outcome.UninstallFailed; return false; }
            return true;
        }
        internal bool Start(bool pumpReady, bool ownerThreadMatch)
        {
            lock (gate) {
                if (!MayStart(pumpReady, ownerThreadMatch)) return false;
                if (registrationClaimed) { last = Outcome.AlreadyRegistered; return true; }
                return Install();
            }
        }
        // Caller must require independent liveness/pump-gap evidence as well as a safe boundary.
        // Never call this on a blind periodic timer or while an owned down/up/replay is outstanding.
        internal bool TryRearm(bool pumpReady, bool ownerThreadMatch, bool safeToRearm)
        {
            lock (gate) {
                if (!MayStart(pumpReady, ownerThreadMatch)) return false;
                if (!safeToRearm) { last = Outcome.UnsafeRearm; return false; }
                if (registrationClaimed && !Retire()) return false;
                if (disposed) return false;
                bool installed = Install();
                if (installed) rearms = Increment(rearms);
                return installed;
            }
        }
        bool Install()
        {
            if (consecutiveFailures >= maximumAttempts) { last = Outcome.RetryLimit; return false; }
            operating = true; attempts = Increment(attempts);
            bool accepted = false;
            try {
                accepted = install();
                last = accepted ? Outcome.Registered : Outcome.InstallFailed;
            } catch (Exception) { last = Outcome.InstallException; }
            finally {
                registrationClaimed = accepted;
                if (accepted) consecutiveFailures = 0; else consecutiveFailures = Increment(consecutiveFailures);
                operating = false; CompleteDispose();
            }
            return accepted && !disposed;
        }
        bool Retire()
        {
            operating = true; retirements = Increment(retirements);
            bool confirmed = false;
            try {
                confirmed = uninstall();
                if (!confirmed) last = Outcome.UninstallFailed;
            } catch (Exception) { last = Outcome.UninstallException; }
            finally {
                if (confirmed) { registrationClaimed = false; retirementUncertain = false; }
                else retirementUncertain = true;
                operating = false; CompleteDispose();
            }
            return confirmed;
        }
        void CompleteDispose()
        {
            if (!disposed || operating || disposeRetirementAttempted) return;
            disposeRetirementAttempted = true;
            if (!registrationClaimed || Retire()) last = Outcome.Disposed;
        }
        public void Dispose()
        {
            lock (gate) {
                if (disposed) return;
                disposed = true;
                CompleteDispose();
            }
        }
        internal string Format()
        {
            lock (gate) return "registrationClaimed=" + (registrationClaimed ? "1" : "0") +
                " callbackHealth=unknown disposed=" + (disposed ? "1" : "0") +
                " retirementUncertain=" + (retirementUncertain ? "1" : "0") +
                " installs=" + attempts.ToString(CultureInfo.InvariantCulture) +
                " failures=" + consecutiveFailures.ToString(CultureInfo.InvariantCulture) +
                " retirements=" + retirements.ToString(CultureInfo.InvariantCulture) +
                " rearms=" + rearms.ToString(CultureInfo.InvariantCulture) +
                " outcome=" + ((int)last).ToString(CultureInfo.InvariantCulture);
        }
    }
}
