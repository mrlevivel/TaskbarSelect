using System;
using System.Collections.Generic;

namespace TaskbarSelect
{
    // HIGH-helper visual metadata only. Confirmation never creates pin
    // authority, sends a worker request, or changes the existing cleanup ledger.
    internal sealed class ReservedPinFocus
    {
        readonly object gate = new object();
        readonly Dictionary<IntPtr, FocusedWindowSnapshot> confirmed = new Dictionary<IntPtr, FocusedWindowSnapshot>();
        readonly Dictionary<IntPtr, long> epochs = new Dictionary<IntPtr, long>();
        readonly Func<IntPtr, FocusedWindowSnapshot> read;
        readonly Func<FocusedWindowSnapshot, bool> owned;
        readonly Func<IntPtr, bool> exists;
        int generation;
        long epoch;
        internal ReservedPinFocus(Func<IntPtr, FocusedWindowSnapshot> read, Func<FocusedWindowSnapshot, bool> owned, Func<IntPtr, bool> exists)
        {
            if (read == null || owned == null || exists == null) throw new ArgumentNullException("api");
            this.read = read; this.owned = owned; this.exists = exists;
        }
        internal int Count { get { lock (gate) return confirmed.Count; } }
        // Read-only presentation candidates. Callers must still recheck
        // Matches against the live owned marker before publishing any cue.
        internal PinnedWindowObservation[] Observations
        {
            get { lock (gate) {
                var result = new List<PinnedWindowObservation>();
                foreach (var pair in confirmed)
                    result.Add(new PinnedWindowObservation { Identity = pair.Value.Copy(), Epoch = epochs[pair.Key], HelperOwned = true });
                return result.ToArray();
            } }
        }
        internal bool RecordResult(FocusedWindowSnapshot expected, bool pinned, WindowMoveWorker.Result result)
        {
            if (result == null || result.Outcome != WindowMoveWorker.Outcome.Success || !result.CallerExited ||
                !WindowStatePolicy.Verified(expected)) return false;
            int token; lock (gate) token = generation;
            try {
                var current = Copy(read(expected.Window));
                if (!WindowStatePolicy.SameIdentity(expected, current) || WindowStatePolicy.Topmost(current) != pinned ||
                    (pinned && !owned(current))) return false;
                var after = Copy(read(expected.Window));
                if (!WindowStatePolicy.SameIdentity(current, after) || WindowStatePolicy.Topmost(after) != pinned) return false;
                lock (gate) {
                    if (token != generation) return false;
                    if (!pinned) {
                        FocusedWindowSnapshot prior;
                        if (confirmed.TryGetValue(after.Window, out prior) && WindowStatePolicy.SameIdentity(prior, after)) {
                            confirmed.Remove(after.Window); epochs.Remove(after.Window);
                        }
                        return true;
                    }
                    if (!confirmed.ContainsKey(after.Window) && confirmed.Count >= OwnedWindowPins.Limit) {
                        foreach (var pair in new List<KeyValuePair<IntPtr, FocusedWindowSnapshot>>(confirmed)) {
                            try {
                                var observed = Copy(read(pair.Key));
                                if (!exists(pair.Key) || (WindowStatePolicy.ReadableIdentity(observed) &&
                                    (!WindowStatePolicy.SameIdentity(pair.Value, observed) || !WindowStatePolicy.Topmost(observed)))) {
                                    confirmed.Remove(pair.Key); epochs.Remove(pair.Key);
                                }
                            } catch (Exception) { /* Uncertain reads do not grant fresh visual capacity. */ }
                        }
                    }
                    if (token != generation || (!confirmed.ContainsKey(after.Window) && confirmed.Count >= OwnedWindowPins.Limit)) return false;
                    confirmed[after.Window] = after.Copy(); epochs[after.Window] = ++epoch; return true;
                }
            } catch (Exception) { return false; }
        }
        internal bool Matches(FocusedWindowSnapshot target)
        {
            if (!WindowStatePolicy.Eligible(target, null) || !WindowStatePolicy.Topmost(target)) return false;
            FocusedWindowSnapshot retained; int token;
            lock (gate) {
                if (!confirmed.TryGetValue(target.Window, out retained) || !WindowStatePolicy.SameIdentity(retained, target)) return false;
                retained = retained.Copy(); token = generation;
            }
            try {
                var current = Copy(read(target.Window));
                if (!WindowStatePolicy.SameIdentity(retained, current) || !WindowStatePolicy.Eligible(current, null) ||
                    !WindowStatePolicy.Topmost(current) || !owned(current)) return false;
                var after = Copy(read(target.Window));
                if (!WindowStatePolicy.SameIdentity(current, after) || !WindowStatePolicy.Eligible(after, null) || !WindowStatePolicy.Topmost(after)) return false;
                lock (gate) {
                    FocusedWindowSnapshot latest;
                    return token == generation && confirmed.TryGetValue(target.Window, out latest) && WindowStatePolicy.SameIdentity(latest, retained);
                }
            } catch (Exception) { return false; }
        }
        internal void Clear() { lock (gate) { generation++; confirmed.Clear(); epochs.Clear(); } }
        internal void ForgetObservation(IntPtr window) { lock (gate) { generation++; confirmed.Remove(window); epochs.Remove(window); } }
        static FocusedWindowSnapshot Copy(FocusedWindowSnapshot value) { return value == null ? null : value.Copy(); }
    }
}
