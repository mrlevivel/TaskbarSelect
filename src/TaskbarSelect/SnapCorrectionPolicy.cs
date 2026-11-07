using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TaskbarSelect
{
    // Private numeric evidence only. Native acceptance is not completion: the
    // later observed rectangle has its own validity and match flags.
    internal sealed class SnapCorrectionDiagnostics
    {
        long attempts, accepted, lastEpoch;
        Native.Rect before, desired, observed;
        bool identityValid, readbackValid, lastModeMax, observedMaximized;
        internal bool HasAttempt { get { return attempts != 0; } }
        internal long AttemptCount { get { return attempts; } }
        internal void RecordAttempt(long epoch, Native.Rect from, Native.Rect to, bool maximized = false)
        {
            attempts++; lastEpoch = epoch; before = from; desired = to;
            lastModeMax = maximized;
            observed = new Native.Rect(); identityValid = readbackValid = observedMaximized = false;
        }
        internal void RecordAccepted() { accepted++; }
        internal void Observe(bool sameIdentity, bool valid, Native.Rect current, bool maximized = false)
        {
            identityValid = HasAttempt && sameIdentity;
            readbackValid = identityValid && valid;
            observed = readbackValid ? current : new Native.Rect();
            observedMaximized = readbackValid && maximized;
        }
        static void AddRect(Dictionary<string, long> result, string prefix, Native.Rect rect)
        {
            result[prefix + "Left"] = rect.Left; result[prefix + "Top"] = rect.Top;
            result[prefix + "Right"] = rect.Right; result[prefix + "Bottom"] = rect.Bottom;
        }
        internal Dictionary<string, long> Snapshot(long epoch, int epochAttempts, int epochAreas)
        {
            var result = new Dictionary<string, long> {
                { "WakeEpoch", epoch }, { "SnapCorrectionAttempts", attempts }, { "SnapCorrectionAccepted", accepted },
                { "LastAttemptEpoch", lastEpoch }, { "HasAttempt", HasAttempt ? 1 : 0 },
                { "LastIdentityValid", identityValid ? 1 : 0 }, { "LastReadbackValid", readbackValid ? 1 : 0 },
                { "LastObservedMatchesDesired", readbackValid && AreaPolicy.Equal(observed, desired) ? 1 : 0 },
                { "LastModeMax", lastModeMax ? 1 : 0 }, { "LastObservedMaximized", observedMaximized ? 1 : 0 },
                { "EpochAttemptCount", epochAttempts }, { "EpochAreaCount", epochAreas }
            };
            AddRect(result, "Before", before); AddRect(result, "Desired", desired); AddRect(result, "Observed", observed);
            return result;
        }
    }

    internal static class SnapCorrectionGeometry
    {
        internal static bool TryExpand(bool maximized, bool arranged, Native.Rect outer, Native.Rect visible,
            AreaRecord area, out Native.Rect result)
        {
            result = outer;
            if (!maximized) return arranged && SnapAreaPolicy.ExpandFrame(outer, visible, area, out result);
            // Maximized recovery is deliberately narrower than snap repair:
            // exact full pre-reclaim visible area, with no rounding tolerance.
            if (arranged || area == null || !AreaPolicy.Contains(area.Monitor, area.Applied) ||
                !AreaPolicy.Contains(area.Applied, area.Original) || AreaPolicy.Equal(area.Original, area.Applied) ||
                !TaskbarMover.ValidVisibleFrame(outer, visible) || !AreaPolicy.Equal(visible, area.Original)) return false;
            long left = (long)outer.Left + area.Applied.Left - area.Original.Left;
            long top = (long)outer.Top + area.Applied.Top - area.Original.Top;
            long right = (long)outer.Right + area.Applied.Right - area.Original.Right;
            long bottom = (long)outer.Bottom + area.Applied.Bottom - area.Original.Bottom;
            if (left < Int32.MinValue || left > Int32.MaxValue || top < Int32.MinValue || top > Int32.MaxValue ||
                right < Int32.MinValue || right > Int32.MaxValue || bottom < Int32.MinValue || bottom > Int32.MaxValue ||
                right <= left || bottom <= top || right - left > Int32.MaxValue || bottom - top > Int32.MaxValue) return false;
            result = new Native.Rect { Left = (int)left, Top = (int)top, Right = (int)right, Bottom = (int)bottom };
            return !AreaPolicy.Equal(result, outer);
        }
    }

    internal struct SnapCorrectionTarget : IEquatable<SnapCorrectionTarget>
    {
        internal IntPtr Window;
        internal uint Process, Thread;
        internal long ProcessStarted;
        internal bool Valid { get { return Window != IntPtr.Zero && Process != 0 && Thread != 0 && ProcessStarted > 0; } }
        public bool Equals(SnapCorrectionTarget other)
        {
            return Window == other.Window && Process == other.Process && Thread == other.Thread && ProcessStarted == other.ProcessStarted;
        }
        public override bool Equals(object value) { return value is SnapCorrectionTarget && Equals((SnapCorrectionTarget)value); }
        public override int GetHashCode()
        {
            unchecked { return (((Window.GetHashCode() * 397) ^ (int)Process) * 397 ^ (int)Thread) * 397 ^ ProcessStarted.GetHashCode(); }
        }
    }

    // Value snapshot: replacing AreaRecord or a taskbar HWND cannot rearm correction.
    internal sealed class SnapCorrectionArea : IEquatable<SnapCorrectionArea>
    {
        readonly string identity;
        readonly Native.Rect monitor, original, applied;
        SnapCorrectionArea(AreaRecord record)
        { identity = record.Identity; monitor = record.Monitor; original = record.Original; applied = record.Applied; }
        internal AreaRecord ToRecord()
        { return new AreaRecord { Identity = identity, Monitor = monitor, Original = original, Applied = applied }; }
        internal static bool TryCapture(AreaRecord record, out SnapCorrectionArea result)
        {
            result = null;
            if (record == null || String.IsNullOrWhiteSpace(record.Identity) || record.Identity.Length > 512 ||
                !AreaPolicy.Contains(record.Monitor, record.Applied) || !AreaPolicy.Contains(record.Applied, record.Original) ||
                AreaPolicy.Equal(record.Original, record.Applied)) return false;
            result = new SnapCorrectionArea(record); return true;
        }
        public bool Equals(SnapCorrectionArea other)
        {
            return other != null && String.Equals(identity, other.identity, StringComparison.OrdinalIgnoreCase) &&
                AreaPolicy.Equal(monitor, other.monitor) && AreaPolicy.Equal(original, other.original) && AreaPolicy.Equal(applied, other.applied);
        }
        public override bool Equals(object value) { return Equals(value as SnapCorrectionArea); }
        static int RectHash(Native.Rect rect)
        { unchecked { return ((rect.Left * 397 ^ rect.Top) * 397 ^ rect.Right) * 397 ^ rect.Bottom; } }
        public override int GetHashCode()
        {
            unchecked { return ((StringComparer.OrdinalIgnoreCase.GetHashCode(identity) * 397 ^ RectHash(monitor)) * 397 ^ RectHash(original)) * 397 ^ RectHash(applied); }
        }
    }

    // Private convergence policy. Each authoritative wake epoch gets one attempt
    // per verified window/physical-area pair, regardless of native success or
    // later geometry. Only the separately verified display-power Off-to-On path
    // may call BeginWakeEpoch. Location/discovery events, empty target lists,
    // mouse state, temporary success and AreaRecord replacement never replenish
    // this budget. Within an epoch subsequent same-window snaps/resizes rely on
    // native snapping. This is a conservative private policy, not a final UX design.
    internal sealed class SnapCorrectionPolicy
    {
        internal const int MaximumAreas = 16, MaximumAttempts = 128;
        internal const int WakeQuietSeconds = 5;
        readonly Func<long> clock;
        readonly long frequency;
        long wakeBegan, lastClock = Int64.MinValue;
        bool wakeClockValid;
        internal SnapCorrectionPolicy() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }
        internal SnapCorrectionPolicy(Func<long> clock, long frequency)
        {
            if (clock == null || frequency <= 0) throw new ArgumentException("A monotonic clock and positive frequency are required.");
            this.clock = clock; this.frequency = frequency;
        }
        internal static bool IsApplicationRoot(IntPtr window, IntPtr root, string className)
        {
            if (window == IntPtr.Zero || root != window || String.IsNullOrEmpty(className)) return false;
            foreach (string shell in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame" })
                if (String.Equals(className, shell, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        sealed class Attempt : IEquatable<Attempt>
        {
            internal SnapCorrectionTarget Target;
            internal SnapCorrectionArea Area;
            public bool Equals(Attempt other) { return other != null && Target.Equals(other.Target) && Area.Equals(other.Area); }
            public override bool Equals(object value) { return Equals(value as Attempt); }
            public override int GetHashCode() { unchecked { return Target.GetHashCode() * 397 ^ Area.GetHashCode(); } }
        }
        readonly HashSet<SnapCorrectionArea> areas = new HashSet<SnapCorrectionArea>();
        readonly HashSet<Attempt> attempts = new HashSet<Attempt>();
        long wakeEpoch;
        int authorizedAttempts;
        internal SnapIntentPolicy IntentPolicy { get; set; }
        internal int AttemptCount { get { return authorizedAttempts; } }
        internal int AreaCount { get { return areas.Count; } }
        internal long WakeEpoch { get { return wakeEpoch; } }
        internal bool BeginWakeEpoch(long sequence)
        {
            // The caller must supply the authoritative, rate-limited power
            // observer sequence. Duplicate, stale and invalid notifications do
            // not discard history. Keep only this epoch's fixed-size ledger.
            if (sequence <= wakeEpoch || sequence <= 0) return false;
            wakeEpoch = sequence; attempts.Clear(); areas.Clear(); authorizedAttempts = 0;
            if (IntentPolicy != null) IntentPolicy.Invalidate();
            try {
                long now = clock();
                wakeClockValid = now >= 0;
                wakeBegan = Math.Max(now, lastClock); lastClock = wakeBegan;
            } catch (Exception) { wakeClockValid = false; }
            return true;
        }
        bool QuietGateOpen()
        {
            if (wakeEpoch == 0) return true;
            if (!wakeClockValid) return false;
            long now;
            try { now = clock(); } catch (Exception) { return false; }
            // Preserve the high-water mark so rollback cannot shorten the gate.
            if (now < 0 || now < wakeBegan || now < lastClock) return false;
            lastClock = now;
            return now - wakeBegan >= (double)frequency * WakeQuietSeconds;
        }
        internal bool CanAttempt(SnapCorrectionTarget target, SnapCorrectionArea area)
        {
            return target.Valid && area != null && QuietGateOpen() && authorizedAttempts < MaximumAttempts &&
                (areas.Contains(area) || areas.Count < MaximumAreas) && !attempts.Contains(new Attempt { Target = target, Area = area });
        }
        internal bool TryConsume(SnapCorrectionTarget target, SnapCorrectionArea area)
        { return TryConsume(target, area, wakeEpoch); }
        internal bool TryConsume(SnapCorrectionTarget target, SnapCorrectionArea area, long observedWakeEpoch)
        {
            if (observedWakeEpoch != wakeEpoch || !CanAttempt(target, area)) return false;
            // Consume before entering SetWindowPos, including rejection/exception.
            areas.Add(area); attempts.Add(new Attempt { Target = target, Area = area }); authorizedAttempts++;
            if (IntentPolicy != null) IntentPolicy.RetireMatching(target, area, wakeEpoch);
            return true;
        }
        internal bool CanPrepareIntent(SnapCorrectionTarget target, SnapCorrectionArea area, long observedWakeEpoch,
            SnapIntentReservation reservation, SnapIntentProof oldCandidate, long now)
        {
            return observedWakeEpoch == wakeEpoch && target.Valid && area != null && QuietGateOpen() &&
                authorizedAttempts < MaximumAttempts && (areas.Contains(area) || areas.Count < MaximumAreas) &&
                reservation != null && Object.ReferenceEquals(reservation.Owner, IntentPolicy) && oldCandidate != null &&
                oldCandidate.Target.Equals(target) && oldCandidate.Area != null && oldCandidate.Area.Equals(area) && oldCandidate.WakeEpoch == wakeEpoch &&
                IntentPolicy.CanPrepare(reservation, oldCandidate, now);
        }
        internal bool CanAttempt(SnapCorrectionTarget target, SnapCorrectionArea area, long observedWakeEpoch,
            SnapIntentReservation reservation, SnapIntentProof prepared, long now)
        {
            return observedWakeEpoch == wakeEpoch && target.Valid && area != null && QuietGateOpen() &&
                authorizedAttempts < MaximumAttempts && (areas.Contains(area) || areas.Count < MaximumAreas) &&
                reservation != null && Object.ReferenceEquals(reservation.Owner, IntentPolicy) && prepared != null &&
                prepared.Target.Equals(target) && prepared.Area != null && prepared.Area.Equals(area) && prepared.WakeEpoch == wakeEpoch &&
                IntentPolicy.CanConsume(reservation, prepared, now);
        }
        internal bool TryConsume(SnapCorrectionTarget target, SnapCorrectionArea area, long observedWakeEpoch,
            SnapIntentReservation reservation, SnapIntentProof prepared, long now)
        {
            if (!CanAttempt(target, area, observedWakeEpoch, reservation, prepared, now) || !IntentPolicy.Authorize(reservation, prepared, now)) {
                if (reservation != null) reservation.Owner.Abort(reservation);
                return false;
            }
            areas.Add(area); attempts.Add(new Attempt { Target = target, Area = area }); authorizedAttempts++;
            return true;
        }
    }
}
