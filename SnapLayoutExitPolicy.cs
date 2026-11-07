using System;
namespace TaskbarSelect
{
    // Shared injected ordering used by the real controller and inert tests.
    // Explicit user attempts may restore areas/bars once. Settling ticks never
    // repeat those writes or authorize a new inverse geometry request.
    internal sealed class SnapLayoutExitPolicy
    {
        readonly Action quiesce;
        readonly Func<RecoveryResult> recover;
        readonly Func<bool, bool> finish;
        bool ready, preparing;
        internal SnapLayoutExitPolicy(Action quiesce, Func<RecoveryResult> recover, Func<bool, bool> finish)
        { this.quiesce = quiesce; this.recover = recover; this.finish = finish; }
        internal string Error { get; private set; }
        internal bool TryPrepare(bool explicitAttempt)
        {
            if (preparing) return false;
            preparing = true; Error = null;
            try {
                bool quiet = false;
                try { quiesce(); quiet = true; }
                catch (Exception) { Error = "Window correction could not be paused safely."; }
                // Even failed quiescence cannot skip the native taskbar finally
                // in the controller's Recovery.Run delegate.
                if (explicitAttempt) {
                    ready = false;
                    try { var result = recover(); ready = result != null && result.Success; }
                    catch (Exception) { Error = "Taskbar recovery is not confirmed."; }
                }
                if (!quiet || !ready) return false;
                try { return finish(explicitAttempt); }
                catch (Exception) { Error = "Window recovery is not confirmed."; return false; }
            } finally { preparing = false; }
        }
    }
}
