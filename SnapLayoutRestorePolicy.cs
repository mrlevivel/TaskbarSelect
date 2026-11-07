using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskbarSelect
{
    // One explicit restoration owns one immutable affected-area set. Polling
    // never repeats area/taskbar recovery. Each receipt independently permits
    // at most one inverse, including rejection or an unknown native outcome.
    internal sealed class SnapLayoutRestorePolicy
    {
        internal const int MaximumReadbacks = 16;
        readonly SnapCorrectionArea[] areas;
        readonly Action quiesce, recover;
        readonly Func<SnapCorrectionArea, SnapLayoutAreaState> readArea;
        readonly Func<bool> finish, resume;
        bool started, quiet, preparing, recovered, complete;
        int remaining;
        internal SnapLayoutRestorePolicy(IEnumerable<SnapCorrectionArea> areas, Action quiesce, Action recover,
            Func<SnapCorrectionArea, SnapLayoutAreaState> readArea, Func<bool> finish, Func<bool> resume)
        {
            this.areas = areas == null ? new SnapCorrectionArea[0] : areas.Where(a => a != null).Distinct().ToArray();
            if (this.areas.Length == 0 || quiesce == null || recover == null || readArea == null || finish == null || resume == null)
                throw new ArgumentException("A restoration requires affected areas and explicit boundaries.");
            this.quiesce = quiesce; this.recover = recover; this.readArea = readArea; this.finish = finish; this.resume = resume;
        }
        internal bool Pending { get { return !complete; } }
        internal IEnumerable<SnapCorrectionArea> Areas { get { return areas.ToArray(); } }
        internal string Error { get; private set; }
        internal bool TryComplete(bool explicitAttempt)
        {
            if (complete) return true;
            if (preparing) return false;
            preparing = true;
            try {
                if (!quiet) {
                    if (started && !explicitAttempt) return false;
                    // Publish the fence before any fallible/reentrant boundary.
                    started = true;
                    try { quiesce(); quiet = true; }
                    catch (Exception) { Error = "Window correction could not be paused safely."; return false; }
                }
                if (explicitAttempt) {
                    recovered = false; remaining = MaximumReadbacks;
                    try { recover(); recovered = true; }
                    catch (Exception) { Error = "Taskbar restoration was not confirmed. Retry Pause all or Restore and exit."; return false; }
                }
                if (!recovered || remaining == 0) return false;
                remaining--;
                foreach (var area in areas) {
                    var state = readArea(area);
                    if (state != SnapLayoutAreaState.OriginalReady && state != SnapLayoutAreaState.Changed) {
                        Error = "Waiting for taskbar and window recovery. Movement is paused; retry Pause all or Restore and exit.";
                        return false;
                    }
                }
                // Readiness can arrive after ShowWindowAsync. The receipt budget,
                // not polling, owns the one initial inverse request.
                if (!finish()) {
                    Error = "Window recovery is not confirmed. Movement is paused; retry Pause all or Restore and exit.";
                    return false;
                }
                if (!resume()) { Error = "Window recovery remains paused."; return false; }
                complete = true; Error = null; return true;
            }
            catch (Exception) { Error = "Window recovery is not confirmed. Movement is paused; retry Pause all or Restore and exit."; return false; }
            finally { preparing = false; }
        }
    }
}
