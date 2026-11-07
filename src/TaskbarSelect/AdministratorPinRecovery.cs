using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskbarSelect
{
    internal sealed class AdministratorPinRecoveryApi
    {
        internal Func<IntPtr, FocusedWindowSnapshot> Read = delegate(IntPtr window) { return FocusedWindowNative.Read(window, true); };
        internal Func<IntPtr, bool> Exists = Native.IsWindow;
    }

    // An in-memory observation ledger, never a source of USER authority. The
    // HIGH helper owns the pins and their lifetime markers. After independently
    // confirmed helper retirement, this ledger can only prove that every
    // authorized target is gone, belongs to another lifetime, or is not topmost.
    internal sealed class AdministratorPinRecovery
    {
        readonly object gate = new object();
        readonly Dictionary<IntPtr, FocusedWindowSnapshot> entries = new Dictionary<IntPtr, FocusedWindowSnapshot>();
        readonly AdministratorPinRecoveryApi api;
        bool recordedAuthorization;
        internal AdministratorPinRecovery() : this(new AdministratorPinRecoveryApi()) { }
        internal AdministratorPinRecovery(AdministratorPinRecoveryApi api)
        { if (api == null) throw new ArgumentNullException("api"); this.api = api; }
        internal int Count { get { lock (gate) return entries.Count; } }

        // Call before the positive pin authorization. Failure must reject that
        // authorization, so the ledger can never silently omit a touched HWND.
        internal bool Remember(FocusedWindowSnapshot snapshot)
        {
            if (!WindowStatePolicy.Eligible(snapshot, null) || WindowStatePolicy.Topmost(snapshot)) return false;
            lock (gate) {
                try {
                    var current = Copy(api.Read(snapshot.Window));
                    if (!api.Exists(snapshot.Window) || !WindowStatePolicy.Eligible(current, null) ||
                        !WindowStatePolicy.SameIdentity(snapshot, current) || WindowStatePolicy.Topmost(current)) return false;
                    PruneReleased();
                    if (!entries.ContainsKey(snapshot.Window) && entries.Count >= OwnedWindowPins.Limit) return false;
                    entries[snapshot.Window] = current.Copy(); recordedAuthorization = true; return true;
                } catch (Exception) { return false; }
            }
        }

        // Caller supplies the independent retirement/drain/worker fence. A
        // never-recorded or reset empty ledger cannot clear an unknown latch.
        internal bool TryConfirmReleased()
        {
            lock (gate) {
                if (!recordedAuthorization) return false;
                PruneReleased();
                if (entries.Count != 0) return false;
                recordedAuthorization = false; return true;
            }
        }

        // Only an authenticated live helper receipt proving zero owned pins
        // or a successful explicit cleanup receipt may reset active records.
        internal void Reset()
        { lock (gate) { entries.Clear(); recordedAuthorization = false; } }

        void PruneReleased()
        {
            foreach (var entry in entries.ToArray()) {
                try {
                    if (!api.Exists(entry.Key)) { entries.Remove(entry.Key); continue; }
                    var current = Copy(api.Read(entry.Key));
                    if (!WindowStatePolicy.ReadableIdentity(current)) continue;
                    if (!WindowStatePolicy.SameIdentity(entry.Value, current) || !WindowStatePolicy.Topmost(current)) entries.Remove(entry.Key);
                } catch (Exception) { /* Unreadable state cannot prove release. */ }
            }
        }
        static FocusedWindowSnapshot Copy(FocusedWindowSnapshot value) { return value == null ? null : value.Copy(); }
    }
}
