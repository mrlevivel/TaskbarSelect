using System;

namespace TaskbarSelect
{
    // One user action owns the whole permission change. This STA coordinator
    // survives closing Settings; ordinary startup never calls Request(true).
    internal sealed class AdministratorPermissionSwitch : IDisposable
    {
        internal interface IBackend
        {
            bool Active { get; }
            bool Enabling { get; }
            bool Uncertain { get; }
            bool SavedApproval { get; }
            bool Remembered { get; }
            string Error { get; }
            void Remember(bool value);
            void BlockOperations(bool value);
            bool RequiresRemoval();
            bool Stop();
            void EnableManual();
            void EnableRemembered();
            bool Install();
            bool Remove();
        }

        enum Phase { Idle, Remembered, Install, Remove }
        readonly IBackend backend;
        Phase phase;
        bool executing, initialized, approvalMayRemain, incomplete, offRequested, disposed;
        int generation;
        string notice = "";
        internal const string Explanation = "Controls apps such as Task Manager. On remembers approval for this build; Off stops support and removes approval.\nWindows may ask permission. Search still opens apps normally.";
        internal AdministratorPermissionSwitch(IBackend value)
        { if (value == null) throw new ArgumentNullException("value"); backend = value; }
        internal bool Busy { get { return executing || phase != Phase.Idle; } }
        internal bool ReconnectAllowed { get { return !disposed && !Busy && !offRequested; } }
        internal bool Checked { get { return backend.Active || backend.Enabling || backend.Uncertain || backend.Remembered || approvalMayRemain || incomplete || (Busy && !offRequested); } }
        internal string Status
        {
            get {
                if (Busy) return phase == Phase.Remembered ? "Reconnecting" : backend.Enabling ? "Waiting for Windows approval" : offRequested ? "Turning off" : "Turning on";
                if (incomplete || backend.Uncertain) return offRequested ? "Off not confirmed" : "Needs attention";
                if (backend.Active) return backend.Remembered && backend.SavedApproval ? "On" : "On \u2014 this session only";
                if (backend.Enabling) return "Reconnecting";
                if (backend.Remembered || approvalMayRemain) return String.IsNullOrEmpty(backend.Error) ? "Saved approval needs attention" : "Disconnected";
                return "Off";
            }
        }
        internal string Detail
        {
            get {
                if (!Busy && !incomplete && !offRequested && !backend.Active && !backend.Enabling &&
                    (backend.Remembered || approvalMayRemain) && !String.IsNullOrEmpty(backend.Error))
                    return "Support disconnected. Saved approval is retained.\n" + backend.Error + "\n" + Explanation;
                return String.IsNullOrEmpty(notice) ? Explanation : notice + "\n" + Explanation;
            }
        }

        // Strictly read-only. A missing preference is not proof of revocation.
        void ObserveApproval()
        {
            try { approvalMayRemain = backend.RequiresRemoval(); }
            catch { approvalMayRemain = true; throw; }
            initialized = true;
        }
        bool Current(int token) { return !disposed && token == generation; }
        void Fail(string message)
        {
            incomplete = true;
            string error = backend.Error;
            notice = message + (String.IsNullOrEmpty(error) ? "" : " " + error);
            phase = Phase.Idle;
        }
        internal void Request(bool enabled)
        {
            if (disposed || Busy) return;
            int token = ++generation;
            executing = true; offRequested = !enabled; notice = ""; incomplete = false;
            try {
                backend.BlockOperations(true);
                if (enabled) {
                    if (backend.Active && backend.SavedApproval && backend.Remembered) { backend.BlockOperations(false); return; }
                    if (!initialized) ObserveApproval();
                    // This fallback belongs only to this explicit user request.
                    if (backend.Remembered && approvalMayRemain && !backend.Active) {
                        phase = Phase.Remembered;
                        backend.EnableRemembered();
                    } else BeginManual(token, Phase.Install);
                } else {
                    // Persist Off intent before pumping messages. A cancelled
                    // removal must not silently enable support at the next boot.
                    backend.Remember(false);
                    if (!Current(token)) return;
                    if (!backend.Stop()) { Fail("Support has not confirmed that it stopped. Try switching off again."); return; }
                    if (!Current(token)) return;
                    ObserveApproval();
                    if (!approvalMayRemain) { CompleteOff(); return; }
                    phase = Phase.Remove;
                    backend.EnableManual();
                }
            } catch (Exception error) {
                if (Current(token)) Fail("Could not " + (enabled ? "enable" : "turn off") + " administrator support. " + error.Message + " Try the switch again.");
            } finally { executing = false; }
            Poll();
        }
        void BeginManual(int token, Phase next)
        {
            if (!backend.Stop()) { Fail("The previous helper has not confirmed that it stopped. Try the switch again."); return; }
            if (!Current(token)) return;
            phase = next;
            backend.EnableManual();
        }
        void CompleteOff()
        {
            if (backend.Active || backend.Enabling || backend.Uncertain || backend.Remembered || approvalMayRemain) {
                Fail("Could not confirm that support and saved approval are off. Try switching off again.");
                return;
            }
            approvalMayRemain = incomplete = false; initialized = true; phase = Phase.Idle;
            notice = "Support is off and saved approval is removed.";
        }
        // Called by the controller, including while Settings is closed. A fence
        // prevents DoEvents in Stop from consuming the same request twice.
        internal void Poll()
        {
            if (disposed || executing) return;
            executing = true; int token = generation;
            try {
                if (!initialized && phase == Phase.Idle) {
                    try { ObserveApproval(); }
                    catch { initialized = true; Fail("Saved approval could not be checked. Try switching off to review and remove it."); }
                }
                if (phase == Phase.Idle || backend.Enabling) return;
                if (phase == Phase.Remembered) {
                    if (backend.Active && backend.SavedApproval) { phase = Phase.Idle; backend.BlockOperations(false); return; }
                    BeginManual(token, Phase.Install);
                    return;
                }
                if (!backend.Active) {
                    bool stopped = backend.Stop();
                    if (!Current(token)) return;
                    // Cancellation may leave protected state. Confirm absence
                    // before reverting an On attempt to an unchecked switch.
                    try { ObserveApproval(); } catch { }
                    incomplete = offRequested || !stopped || backend.Uncertain || approvalMayRemain || backend.Remembered;
                    notice = (offRequested ? "Turning off was not completed. Try switching off again." : "Approval was cancelled or support could not start. Try switching on again.") +
                        (String.IsNullOrEmpty(backend.Error) ? "" : " " + backend.Error);
                    phase = Phase.Idle;
                    return;
                }
                if (phase == Phase.Install) {
                    approvalMayRemain = true; // A failed install can be partial.
                    bool installed = backend.Install();
                    if (!Current(token)) return;
                    if (installed && backend.Active && backend.SavedApproval) {
                        backend.Remember(true);
                        if (!Current(token)) return;
                        incomplete = false; phase = Phase.Idle; notice = "Approval is remembered for this build. Start with Windows is unchanged.";
                        backend.BlockOperations(false);
                    } else {
                        bool stopped = backend.Stop();
                        if (!Current(token)) return;
                        try { ObserveApproval(); } catch { }
                        Fail("Could not finish enabling remembered support. " + (stopped ? "Try the switch again." : "Helper recovery is pending."));
                    }
                } else {
                    bool removed = backend.Remove();
                    if (!Current(token)) return;
                    bool stopped = backend.Stop();
                    if (!Current(token)) return;
                    ObserveApproval();
                    if (removed && stopped && !approvalMayRemain && !backend.Active && !backend.Enabling && !backend.Uncertain && !backend.Remembered) CompleteOff();
                    else Fail("Could not confirm that support and saved approval are off. Try switching off again.");
                }
            } catch (Exception error) {
                if (Current(token)) Fail("The permission change could not be completed. " + error.Message + " Try the switch again.");
            } finally { executing = false; }
        }
        internal void CancelForExit()
        {
            generation++; phase = Phase.Idle; offRequested = true;
            backend.BlockOperations(true);
        }
        public void Dispose() { CancelForExit(); disposed = true; }
    }
}
