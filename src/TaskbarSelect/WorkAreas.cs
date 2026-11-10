using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace TaskbarSelect
{
    internal sealed class AreaRecord
    {
        public string Identity { get; set; }
        public Native.Rect Monitor { get; set; }
        public Native.Rect Original { get; set; }
        public Native.Rect Applied { get; set; }
        public AreaRecord() { }
    }

    internal static class AreaPolicy
    {
        internal static bool Equal(Native.Rect a, Native.Rect b)
        {
            return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        internal static bool Expand(Native.MonitorInfo info, Native.Rect bar, out Native.Rect result)
        {
            result = info.Work;
            if ((info.Flags & 1) != 0) return false;
            // Reclaim only an edge occupied by this taskbar; preserve other appbar margins.
            if (bar.Left == info.Monitor.Left && bar.Right == info.Monitor.Right) {
                if (bar.Bottom == info.Monitor.Bottom && bar.Top == info.Work.Bottom) result.Bottom = info.Monitor.Bottom;
                else if (bar.Top == info.Monitor.Top && bar.Bottom == info.Work.Top) result.Top = info.Monitor.Top;
            } else if (bar.Top == info.Monitor.Top && bar.Bottom == info.Monitor.Bottom) {
                if (bar.Left == info.Monitor.Left && bar.Right == info.Work.Left) result.Left = info.Monitor.Left;
                else if (bar.Right == info.Monitor.Right && bar.Left == info.Work.Right) result.Right = info.Monitor.Right;
            }
            return !Equal(result, info.Work);
        }

        internal static bool CanRestore(AreaRecord record, Display display, Native.MonitorInfo info)
        {
            return !display.Primary && display.Identity == record.Identity &&
                Contains(record.Monitor, record.Applied) && Contains(record.Applied, record.Original) &&
                Equal(record.Monitor, info.Monitor) && Equal(record.Applied, info.Work);
        }
        internal static bool Contains(Native.Rect outer, Native.Rect inner)
        {
            return inner.Left < inner.Right && inner.Top < inner.Bottom && outer.Left <= inner.Left &&
                outer.Top <= inner.Top && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;
        }
    }

    // Keep the reconciliation tests entirely outside the Windows desktop.
    internal interface IWorkAreaPlatform
    {
        bool OwnedHidden(Bar bar);
        Native.MonitorInfo? Monitor(Bar bar);
        Native.MonitorInfo? Monitor(string device);
        Native.Rect? Bounds(Bar bar);
        List<Display> Displays();
        void Set(Native.Rect rect);
    }

    internal sealed class NativeWorkAreaPlatform : IWorkAreaPlatform
    {
        internal long Attempts, Accepted;
        internal Native.Rect LastRequested;
        public bool OwnedHidden(Bar bar)
        {
            return Native.GetProp(new IntPtr(bar.Handle), Native.Marker) == new IntPtr(1) &&
                !Native.IsWindowVisible(new IntPtr(bar.Handle)) && Native.IsSecondaryTaskbar(bar);
        }
        public Native.MonitorInfo? Monitor(Bar bar)
        {
            var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            return Native.GetMonitorInfo(Native.MonitorFromWindow(new IntPtr(bar.Handle), 0), ref info) ? info : (Native.MonitorInfo?)null;
        }
        public Native.MonitorInfo? Monitor(string device)
        {
            Native.MonitorInfo info;
            return Native.TryMonitor(device, out info) ? info : (Native.MonitorInfo?)null;
        }
        public Native.Rect? Bounds(Bar bar)
        {
            Native.Rect rect;
            return Native.GetWindowRect(new IntPtr(bar.Handle), out rect) ? rect : (Native.Rect?)null;
        }
        public List<Display> Displays() { return Native.Displays(); }
        public void Set(Native.Rect rect)
        {
            Attempts++; LastRequested = rect;
            // Broadcasting this change to Explorer makes it reserve the hidden bars again.
            // No persistence flag: this changes only the current desktop session.
            if (!Native.SystemParametersInfo(0x2F, 0, ref rect, 0))
                throw new System.ComponentModel.Win32Exception();
            Accepted++;
        }
    }

    internal sealed class WorkAreas
    {
        internal static string JournalPath { get { return Path.Combine(Path.GetDirectoryName(Program.ConfigPath), "workarea-recovery.json"); } }
        readonly List<AreaRecord> records;
        readonly string journalPath;
        readonly IWorkAreaPlatform platform;
        readonly Action<List<AreaRecord>> injectedSave;
        readonly HashSet<AreaRecord> attemptedRestores = new HashSet<AreaRecord>();
        readonly Func<long> clock = MonotonicMilliseconds;
        sealed class ReclaimState
        {
            internal bool Spent, Observing, Committing;
            internal long Since, LastObservation;
            internal ReclaimSnapshot Snapshot;
        }
        struct ReclaimSnapshot
        {
            internal long Handle;
            internal string Device;
            internal Native.Rect Monitor, Work, Edge;
            internal bool Same(ReclaimSnapshot other)
            {
                return Handle == other.Handle && String.Equals(Device, other.Device, StringComparison.OrdinalIgnoreCase) &&
                    AreaPolicy.Equal(Monitor, other.Monitor) && AreaPolicy.Equal(Work, other.Work) && AreaPolicy.Equal(Edge, other.Edge);
            }
        }
        readonly Dictionary<AreaRecord, ReclaimState> reclaims = new Dictionary<AreaRecord, ReclaimState>();
        internal const long ReclaimStabilityMilliseconds = 5000;
        internal const long ReclaimMaximumObservationGapMilliseconds = 3000;
        internal const long WakeEpochCooldownMilliseconds = 30000;
        long lastWakeSequenceSeen, acceptedWakeSequence, lastWakeEpochAt, recoveryExpansionAttempts, updateGeneration;
        bool hasWakeEpoch;
        static long MonotonicMilliseconds() { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }
        internal readonly List<SnapArea> SnapTargets = new List<SnapArea>();
        internal WorkAreas() : this(JournalPath) { }
        internal WorkAreas(string path) : this(path, new NativeWorkAreaPlatform()) { }
        internal WorkAreas(string path, IWorkAreaPlatform platform)
        {
            if (platform == null) throw new ArgumentNullException("platform");
            this.platform = platform;
            journalPath = Path.GetFullPath(path);
            if (!File.Exists(journalPath)) { records = new List<AreaRecord>(); return; }
            string json = File.ReadAllText(journalPath);
            try {
                records = new JavaScriptSerializer().Deserialize<List<AreaRecord>>(json);
                if (records == null || records.Any(r => r == null || String.IsNullOrEmpty(r.Identity)))
                    throw new InvalidDataException("Recovery records must have monitor identities.");
            } catch (Exception error) {
                if (error is ArgumentException || error is InvalidOperationException || error is InvalidDataException ||
                    error is FormatException || error is OverflowException)
                    throw new CorruptRecoveryJournalException(journalPath, error);
                throw;
            }
        }
        internal WorkAreas(List<AreaRecord> initial, IWorkAreaPlatform platform, Action<List<AreaRecord>> save)
            : this(initial, platform, save, MonotonicMilliseconds) { }
        internal WorkAreas(List<AreaRecord> initial, IWorkAreaPlatform platform, Action<List<AreaRecord>> save, Func<long> clock)
        {
            if (initial == null || platform == null || save == null || clock == null) throw new ArgumentNullException();
            records = initial; this.platform = platform; injectedSave = save;
            this.clock = clock;
        }
        void Save()
        {
            if (injectedSave != null) { injectedSave(records); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
            string temp = journalPath + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(records));
            if (File.Exists(journalPath)) File.Replace(temp, journalPath, null);
            else File.Move(temp, journalPath);
        }
        void Retire(AreaRecord record)
        {
            int index = records.IndexOf(record);
            if (index < 0) return;
            records.RemoveAt(index);
            try { Save(); }
            catch { records.Insert(index, record); throw; }
            attemptedRestores.Remove(record);
            reclaims.Remove(record);
        }
        void Restore(AreaRecord record, List<Display> displays, bool explicitRetry)
        {
            var matches = displays.Where(d => d.Identity == record.Identity).ToList();
            // A temporarily missing/ambiguous identity cannot retire recovery ownership.
            if (matches.Count != 1) return;
            var info = platform.Monitor(matches[0].Device);
            if (!info.HasValue) return;
            if (!String.Equals(info.Value.Device, matches[0].Device, StringComparison.OrdinalIgnoreCase)) return;
            if (AreaPolicy.CanRestore(record, matches[0], info.Value)) {
                // Polling cannot turn an ignored restoration into another SPI loop.
                if (!explicitRetry && attemptedRestores.Contains(record)) return;
                attemptedRestores.Add(record);
                platform.Set(record.Original);
                var after = platform.Monitor(matches[0].Device);
                if (!after.HasValue || !String.Equals(after.Value.Device, matches[0].Device, StringComparison.OrdinalIgnoreCase) ||
                    !AreaPolicy.Equal(record.Monitor, after.Value.Monitor) || !AreaPolicy.Equal(record.Original, after.Value.Work)) {
                    if (explicitRetry) throw new InvalidOperationException("Work-area restoration was not confirmed. Recovery records were retained.");
                    return;
                }
            }
            Retire(record);
        }
        internal void RestoreAll()
        {
            if (RestoreAvailable() != 0)
                throw new InvalidOperationException("Work-area recovery is incomplete because a saved display is unavailable or ambiguous. Recovery records were retained.");
        }
        internal int RestoreAvailable()
        {
            SnapTargets.Clear();
            foreach (var state in reclaims.Values) state.Observing = false;
            if (records.Count == 0) return 0;
            var displays = platform.Displays();
            foreach (var record in records.ToArray()) Restore(record, displays, true);
            return records.Count;
        }
        internal IEnumerable<SnapCorrectionArea> RestorationAreas(Func<SnapCorrectionArea, bool> selected)
        {
            var result = new List<SnapCorrectionArea>();
            foreach (var record in records) {
                SnapCorrectionArea area;
                if (SnapCorrectionArea.TryCapture(record, out area) && selected(area)) result.Add(area);
            }
            return result;
        }
        internal void RestoreSelected(IEnumerable<SnapCorrectionArea> selected)
        {
            var scope = new HashSet<SnapCorrectionArea>(selected);
            SnapTargets.RemoveAll(a => { SnapCorrectionArea area; return SnapCorrectionArea.TryCapture(a.Record, out area) && scope.Contains(area); });
            var displays = platform.Displays();
            foreach (var record in records.ToArray()) {
                SnapCorrectionArea area;
                if (!SnapCorrectionArea.TryCapture(record, out area) || !scope.Contains(area)) continue;
                ReclaimState state;
                if (reclaims.TryGetValue(record, out state)) state.Observing = false;
                Restore(record, displays, true);
            }
        }
        // Only the controller's independently observed session-display Off->On
        // transition may call this. Polling, discovery and HWND changes do not.
        internal bool BeginWakeEpoch(long verifiedSequence)
        {
            if (verifiedSequence <= 0 || verifiedSequence <= lastWakeSequenceSeen) return false;
            lastWakeSequenceSeen = verifiedSequence;
            long now = clock();
            if (now < 0 || (hasWakeEpoch && (now < lastWakeEpochAt || now - lastWakeEpochAt < WakeEpochCooldownMilliseconds))) return false;
            hasWakeEpoch = true; lastWakeEpochAt = now;
            acceptedWakeSequence = verifiedSequence;
            foreach (var state in reclaims.Values) { state.Spent = false; state.Observing = false; }
            return true;
        }
        bool ReclaimSnapshotFor(AreaRecord record, Bar bar, Display display, Native.MonitorInfo info,
            List<Display> displays, Settings settings, out ReclaimSnapshot snapshot)
        {
            snapshot = new ReclaimSnapshot();
            Native.Rect expanded;
            Native.Rect? edge = null;
            bool eligible = Policy.Hide(bar, displays, settings) && !display.Primary && (info.Flags & 1) == 0 &&
                String.Equals(info.Device, display.Device, StringComparison.OrdinalIgnoreCase) &&
                AreaPolicy.Equal(record.Monitor, info.Monitor) && AreaPolicy.Equal(record.Original, info.Work) &&
                AreaPolicy.Contains(record.Monitor, record.Applied) && AreaPolicy.Contains(record.Applied, record.Original) &&
                (edge = platform.Bounds(bar)).HasValue && AreaPolicy.Expand(info, edge.Value, out expanded) &&
                AreaPolicy.Equal(expanded, record.Applied) && platform.OwnedHidden(bar) && Policy.Hide(bar, displays, settings);
            if (!eligible) return false;
            snapshot = new ReclaimSnapshot { Handle = bar.Handle, Device = info.Device,
                Monitor = info.Monitor, Work = info.Work, Edge = edge.Value };
            return true;
        }
        bool CurrentClaim(AreaRecord record, ReclaimState state, long phase, long generation)
        {
            ReclaimState current;
            return phase == acceptedWakeSequence && generation == updateGeneration && records.Contains(record) &&
                reclaims.TryGetValue(record, out current) && Object.ReferenceEquals(state, current);
        }
        void ObserveReclaim(AreaRecord record, Bar bar, Display display, Native.MonitorInfo info,
            List<Display> displays, Settings settings, long generation)
        {
            long phase = acceptedWakeSequence;
            ReclaimState state;
            if (!reclaims.TryGetValue(record, out state)) {
                state = new ReclaimState(); reclaims.Add(record, state);
            }
            if (state.Spent || state.Committing) return;
            ReclaimSnapshot snapshot;
            if (!ReclaimSnapshotFor(record, bar, display, info, displays, settings, out snapshot) ||
                !CurrentClaim(record, state, phase, generation)) { state.Observing = false; return; }
            long now = clock();
            if (now < 0 || !CurrentClaim(record, state, phase, generation)) { state.Observing = false; return; }
            if (!state.Observing || !state.Snapshot.Same(snapshot) || now < state.LastObservation ||
                now - state.LastObservation > ReclaimMaximumObservationGapMilliseconds) {
                state.Observing = true; state.Snapshot = snapshot; state.Since = state.LastObservation = now; return;
            }
            state.LastObservation = now;
            if (now - state.Since < ReclaimStabilityMilliseconds) return;
            state.Committing = true;
            try {
                // Commit requires a fresh complete native snapshot, not just the
                // Update-start observation that completed the stability wait.
                var fresh = platform.Monitor(bar);
                ReclaimSnapshot commit;
                if (!fresh.HasValue || !ReclaimSnapshotFor(record, bar, display, fresh.Value, displays, settings, out commit) ||
                    !commit.Same(snapshot) || !CurrentClaim(record, state, phase, generation) || state.Spent) {
                    state.Observing = false; return;
                }
                // Consume before Windows, including failure. Only a later
                // authoritative, rate-gated wake can renew this allowance.
                state.Spent = true; state.Observing = false;
                recoveryExpansionAttempts++;
                platform.Set(record.Applied);
            } finally { state.Committing = false; }
        }
        internal Dictionary<string, long> DiagnosticSnapshot()
        {
            var native = platform as NativeWorkAreaPlatform;
            Native.Rect request = native == null ? new Native.Rect() : native.LastRequested;
            return new Dictionary<string, long> {
                { "Claims", records.Count }, { "NativeAttempts", native == null ? 0 : native.Attempts },
                { "NativeAccepted", native == null ? 0 : native.Accepted }, { "RecoveryExpansionAttempts", recoveryExpansionAttempts },
                { "WakeSequenceSeen", lastWakeSequenceSeen }, { "WakeSequenceAccepted", acceptedWakeSequence },
                { "SpentClaims", reclaims.Values.Count(s => s.Spent) }, { "ObservingClaims", reclaims.Values.Count(s => s.Observing) },
                { "LastRequestedLeft", request.Left }, { "LastRequestedTop", request.Top },
                { "LastRequestedRight", request.Right }, { "LastRequestedBottom", request.Bottom }
            };
        }
        internal void Update(List<Bar> bars, List<Display> displays, Settings settings, bool enabled)
        {
            long generation = ++updateGeneration;
            SnapTargets.Clear();
            var targets = enabled ? bars.Where(b => Policy.Hide(b, displays, settings) && platform.OwnedHidden(b)).ToList() : new List<Bar>();
            foreach (var record in records.ToArray()) {
                // Loss of a taskbar/identity during wake is not a user request to restore.
                if (!enabled || !settings.HiddenIdentities.Contains(record.Identity)) Restore(record, displays, false);
                if (!targets.Any(b => displays.Any(d => d.Device == b.Device && d.Identity == record.Identity))) {
                    ReclaimState state;
                    if (reclaims.TryGetValue(record, out state)) state.Observing = false;
                }
            }
            foreach (Bar bar in targets) {
                Display display = displays.Find(d => d.Device == bar.Device);
                if (display == null) continue;
                var record = records.Find(r => r.Identity == display.Identity);
                var current = platform.Monitor(bar);
                if (!current.HasValue || !String.Equals(current.Value.Device, display.Device, StringComparison.OrdinalIgnoreCase)) {
                    ReclaimState unverified;
                    if (record != null && reclaims.TryGetValue(record, out unverified)) unverified.Observing = false;
                    continue;
                }
                var info = current.Value;
                if (record != null) {
                    // Windows or another app may reset the work area after an accepted Set.
                    // Keep the first baseline and never fight that reset on every poll.
                    // One delayed retry is allowed only for the exact old reservation,
                    // never a foreign appbar change or new monitor geometry.
                    ObserveReclaim(record, bar, display, info, displays, settings, generation);
                    continue;
                }
                Native.Rect expanded;
                var taskbar = platform.Bounds(bar);
                if (!taskbar.HasValue || !AreaPolicy.Expand(info, taskbar.Value, out expanded)) continue;
                record = new AreaRecord { Identity = display.Identity, Monitor = info.Monitor, Original = info.Work, Applied = expanded };
                long phase = acceptedWakeSequence;
                var initial = new ReclaimSnapshot { Handle = bar.Handle, Device = info.Device,
                    Monitor = info.Monitor, Work = info.Work, Edge = taskbar.Value };
                records.Add(record);
                try { Save(); }
                catch { records.Remove(record); throw; }
                var fresh = platform.Monitor(bar);
                ReclaimSnapshot commit;
                if (phase == acceptedWakeSequence && generation == updateGeneration && records.Contains(record) && fresh.HasValue &&
                    ReclaimSnapshotFor(record, bar, display, fresh.Value, displays, settings, out commit) && initial.Same(commit) &&
                    phase == acceptedWakeSequence && generation == updateGeneration && records.Contains(record)) platform.Set(expanded);
            }
            foreach (Bar bar in targets) {
                Display display = displays.Find(d => d.Device == bar.Device);
                if (display == null) continue;
                AreaRecord record = records.Find(r => r.Identity == display.Identity);
                var current = platform.Monitor(bar);
                // Set's success result is not proof that Explorer retained the request.
                if (record != null && displays.Count(d => d.Identity == record.Identity) == 1 &&
                    current.HasValue && String.Equals(current.Value.Device, display.Device, StringComparison.OrdinalIgnoreCase) &&
                    (current.Value.Flags & 1) == 0 &&
                    AreaPolicy.Equal(record.Monitor, current.Value.Monitor) && AreaPolicy.Equal(record.Applied, current.Value.Work) &&
                    platform.OwnedHidden(bar)) SnapTargets.Add(new SnapArea { Record = record, Bar = bar });
            }
        }
    }
}
