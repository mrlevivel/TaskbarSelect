using System;
using System.Threading;

namespace TaskbarSelect
{
    internal enum SnapChordNoticeKind { Started, ArrowDown, Completed, Cancelled }
    internal sealed class SnapIntentProof
    {
        internal SnapCorrectionTarget Target;
        internal SnapCorrectionArea Area;
        internal long Nonce, ReceiptGeneration, WakeEpoch, Context, At;
        internal long OriginNonce, OriginGeneration;
        internal Native.Rect Outer, Visible, Desired, DesiredVisible;
        internal bool Verified, Normal, Arranged, Maximized, OwnedAreaReady, ActionReady, ReceiptReady;
        internal bool LineageVerified, OldMarkerRetired;
        internal SnapIntentProof Copy() { return (SnapIntentProof)MemberwiseClone(); }
        internal bool Valid
        { get { return Verified && Target.Valid && Area != null && Nonce > 0 && ReceiptGeneration > 0 && WakeEpoch >= 0 && Context > 0 && At >= 0 && OwnedAreaReady && ActionReady && ReceiptReady; } }
        internal bool IsNormal { get { return Valid && Normal && !Arranged && !Maximized; } }
        internal bool IsArranged { get { return Valid && Arranged && !Normal && !Maximized; } }
        internal bool SameBinding(SnapIntentProof other)
        { return other != null && Area != null && other.Area != null && Target.Equals(other.Target) && Area.Equals(other.Area) && Nonce == other.Nonce && ReceiptGeneration == other.ReceiptGeneration && WakeEpoch == other.WakeEpoch && Context == other.Context; }
        internal bool GeometryValid
        { get { return TaskbarMover.ValidVisibleFrame(Outer, Visible) && TaskbarMover.ValidVisibleFrame(Desired, DesiredVisible) && !AreaPolicy.Equal(Outer, Desired); } }
        internal bool SameGeometry(SnapIntentProof other)
        { return other != null && AreaPolicy.Equal(Outer, other.Outer) && AreaPolicy.Equal(Visible, other.Visible) && AreaPolicy.Equal(Desired, other.Desired) && AreaPolicy.Equal(DesiredVisible, other.DesiredVisible); }
    }
    internal sealed class SnapChordPacket
    {
        internal int Key;
        internal bool Down;
        internal uint Flags, NativeTime, CurrentNativeTick;
        internal IntPtr Window;
        internal long At, Generation;
        internal SnapIntentProof Before;
    }
    internal sealed class SnapChordNotice
    {
        internal SnapChordNoticeKind Kind;
        internal long Sequence, StartedAt, ArrowAt, At, Generation;
        internal IntPtr Window;
        internal int Direction;
        internal SnapIntentProof Before;
        internal SnapChordPolicy Source;
        internal object Seal;
        internal SnapChordNotice Copy()
        { var copy = (SnapChordNotice)MemberwiseClone(); copy.Before = Before == null ? null : Before.Copy(); return copy; }
    }
    // Fixed metadata only. No key delivery, native provider, content, timers or
    // persistence occurs here. Every output requires one fresh physical cycle.
    internal sealed class SnapChordPolicy
    {
        readonly long frequency, generation, registeredAt;
        readonly uint registeredNativeTick;
        readonly bool initialKeysReleased;
        long highWater = -1, serial, startedAt, arrowAt;
        ulong downKeys;
        bool active, blocked, arrowSeen;
        int winKey, direction;
        IntPtr window;
        SnapIntentProof before;
        SnapChordNotice issued;
        internal SnapChordPolicy(long frequency, long generation, long registeredAt, bool initialKeysReleased)
            : this(frequency, generation, registeredAt, 0, initialKeysReleased) { }
        internal SnapChordPolicy(long frequency, long generation, long registeredAt, uint registeredNativeTick, bool initialKeysReleased)
        {
            if (frequency <= 0 || frequency > Int64.MaxValue / 2 || generation <= 0 || registeredAt < 0) throw new ArgumentException();
            this.frequency = frequency; this.generation = generation; this.registeredAt = registeredAt;
            this.registeredNativeTick = registeredNativeTick; this.initialKeysReleased = initialKeysReleased;
        }
        static int Index(int key)
        {
            switch (key) {
                case 0x5B: return 0; case 0x5C: return 1; case 0x25: return 2; case 0x27: return 3;
                case 0x10: return 4; case 0xA0: return 5; case 0xA1: return 6;
                case 0x11: return 7; case 0xA2: return 8; case 0xA3: return 9;
                case 0x12: return 10; case 0xA4: return 11; case 0xA5: return 12;
                default: return -1;
            }
        }
        SnapChordNotice Notice(SnapChordNoticeKind kind, long at)
        { return new SnapChordNotice { Kind = kind, Sequence = serial, Window = window, Direction = direction, StartedAt = startedAt, ArrowAt = arrowAt, At = at, Generation = generation, Before = before == null ? null : before.Copy() }; }
        SnapChordNotice Cancel(long at)
        {
            Volatile.Write(ref issued, null);
            var result = Notice(SnapChordNoticeKind.Cancelled, at);
            active = false; blocked = downKeys != 0; arrowSeen = false; before = null;
            return result;
        }
        internal void Invalidate()
        { Volatile.Write(ref issued, null); active = false; blocked = downKeys != 0 || !initialKeysReleased; arrowSeen = false; before = null; }
        internal bool Issued(SnapChordNotice notice)
        {
            var expected = Volatile.Read(ref issued);
            return expected != null && notice != null && Object.ReferenceEquals(notice.Source, this) && Object.ReferenceEquals(notice.Seal, expected.Seal) &&
                notice.Kind == SnapChordNoticeKind.Completed && notice.Sequence == expected.Sequence && notice.Generation == expected.Generation &&
                notice.Window == expected.Window && notice.Direction == expected.Direction && notice.StartedAt == expected.StartedAt &&
                notice.ArrowAt == expected.ArrowAt && notice.At == expected.At && notice.Before != null && expected.Before != null &&
                notice.Before.SameBinding(expected.Before) && notice.Before.At == expected.Before.At && notice.Before.IsNormal && expected.Before.IsNormal;
        }
        internal SnapChordNotice Observe(SnapChordPacket packet)
        {
            if (packet == null) return Cancel(highWater);
            int index = Index(packet.Key); ulong bit = index < 0 ? 0 : 1UL << index;
            bool wasDown = bit != 0 && (downKeys & bit) != 0;
            if (bit != 0) { if (packet.Down) downKeys |= bit; else downKeys &= ~bit; }
            bool clock = packet.At >= registeredAt && packet.At >= highWater;
            if (packet.At >= 0) highWater = Math.Max(highWater, packet.At);
            bool fresh = unchecked(packet.CurrentNativeTick - packet.NativeTime) <= 2000 &&
                (packet.At - registeredAt >= frequency * 2 || unchecked((int)(packet.NativeTime - registeredNativeTick)) >= 0);
            bool upFlag = (packet.Flags & 0x80) != 0;
            if (!initialKeysReleased || !clock || !fresh || packet.Generation != generation || (packet.Flags & ~0xB3U) != 0 ||
                (packet.Flags & 0x32) != 0 || packet.Down == upFlag)
                return Cancel(packet.At);
            if (blocked) { if (downKeys == 0 && !packet.Down) blocked = false; return Cancel(packet.At); }
            bool win = packet.Key == 0x5B || packet.Key == 0x5C;
            bool arrow = packet.Key == 0x25 || packet.Key == 0x27;
            bool modifier = index >= 4;
            if (modifier || (!win && !arrow)) { if (packet.Down) Volatile.Write(ref issued, null); return active ? Cancel(packet.At) : null; }
            if (packet.Down && win) {
                if (active || wasDown || (downKeys & ~bit) != 0 || packet.Window == IntPtr.Zero || serial == Int64.MaxValue) return Cancel(packet.At);
                Volatile.Write(ref issued, null);
                serial++; active = true; winKey = packet.Key; direction = 0; arrowSeen = false;
                window = packet.Window; startedAt = packet.At; arrowAt = 0; before = null;
                return Notice(SnapChordNoticeKind.Started, packet.At);
            }
            if (!active) return null;
            if (packet.Window != window || packet.At - startedAt > frequency * 2) return Cancel(packet.At);
            if (packet.Down && arrow) {
                ulong winBit = winKey == 0x5B ? 1UL : 2UL;
                if (wasDown || arrowSeen || (downKeys & winBit) == 0 || (downKeys & ~(winBit | bit)) != 0) return Cancel(packet.At);
                arrowSeen = true; direction = packet.Key; arrowAt = packet.At;
                before = packet.Before == null ? null : packet.Before.Copy();
                return Notice(SnapChordNoticeKind.ArrowDown, packet.At);
            }
            if (!packet.Down) {
                if (!wasDown || (win && packet.Key != winKey) || (arrow && packet.Key != direction) || !arrowSeen) return Cancel(packet.At);
                if (downKeys != 0) return null;
                active = false;
                var completed = Notice(SnapChordNoticeKind.Completed, packet.At); before = null;
                completed.Source = this; completed.Seal = new object(); Volatile.Write(ref issued, completed.Copy()); return completed;
            }
            return Cancel(packet.At);
        }
    }
    internal sealed class SnapIntentPermit
    {
        internal readonly SnapIntentPolicy Owner;
        internal readonly long Sequence, Context;
        internal readonly SnapIntentProof Binding;
        internal bool Consumed;
        internal SnapIntentPermit(SnapIntentPolicy owner, long sequence, long context, SnapIntentProof binding)
        { Owner = owner; Sequence = sequence; Context = context; Binding = binding.Copy(); }
    }
    internal sealed class SnapIntentReservation
    {
        internal readonly SnapIntentPolicy Owner;
        internal readonly SnapIntentPermit Permit;
        internal readonly SnapIntentProof Binding;
        internal readonly long ReservedAt;
        internal bool Cancelled, NativeAuthorized;
        internal SnapIntentReservation(SnapIntentPolicy owner, SnapIntentPermit permit, SnapIntentProof binding, long at)
        { Owner = owner; Permit = permit; Binding = binding.Copy(); ReservedAt = at; }
    }
    // A physical chord is intent only. The caller still supplies fresh, exact
    // native arrangement/receipt/bar/work-area proof before every consumption.
    internal sealed class SnapIntentPolicy
    {
        readonly long frequency;
        long highWater = -1, latestSequence, latestGeneration, deliveredRevision;
        internal long Context { get; private set; }
        SnapChordNotice completed;
        SnapIntentPermit permit;
        SnapIntentReservation reservation;
        Func<long, bool> currentRevision;
        bool checkingAuthority;
        internal SnapIntentPolicy(long frequency, long context)
        { if (frequency <= 0 || frequency > Int64.MaxValue / 2 || context <= 0) throw new ArgumentException(); this.frequency = frequency; Context = context; }
        internal void CancelPendingInput()
        { if (reservation != null) reservation.Cancelled = true; completed = null; permit = null; reservation = null; currentRevision = null; }
        internal void Invalidate()
        { CancelPendingInput(); if (Context < Int64.MaxValue) Context++; }
        bool Clock(long now)
        {
            if (now < 0 || now < highWater) { Invalidate(); return false; }
            highWater = now; return true;
        }
        bool AuthorityCurrent()
        {
            if (checkingAuthority) return false;
            var observed = completed; var provider = currentRevision;
            long context = Context, revision = deliveredRevision;
            if (observed == null || observed.Source == null || provider == null || !observed.Source.Issued(observed)) return false;
            checkingAuthority = true;
            try {
                bool current = provider(revision);
                return current && Object.ReferenceEquals(observed, completed) && Object.ReferenceEquals(provider, currentRevision) &&
                    Context == context && deliveredRevision == revision && observed.Source.Issued(observed);
            }
            catch (Exception) { return false; }
            finally { checkingAuthority = false; }
        }
        // A callback-delivery revision is mandatory. The one-slot adapter may
        // overwrite Cancelled/Started before the UI polls those notices.
        internal bool AdmitCompleted(SnapChordNotice notice) { return false; }
        internal bool AdmitCompleted(SnapChordNotice notice, long revision, Func<long, bool> revisionCurrent)
        {
            if (notice == null || !Clock(notice.At) || notice.Kind != SnapChordNoticeKind.Completed ||
                notice.Generation < latestGeneration || (notice.Generation == latestGeneration && notice.Sequence <= latestSequence) ||
                notice.Generation <= 0 || notice.Window == IntPtr.Zero || (notice.Direction != 0x25 && notice.Direction != 0x27) ||
                notice.Source == null || !notice.Source.Issued(notice) || revision <= 0 || revisionCurrent == null ||
                notice.StartedAt < 0 || notice.ArrowAt < notice.StartedAt || notice.At < notice.ArrowAt || notice.At - notice.StartedAt > frequency * 2)
                return false;
            var proof = notice.Before;
            if (proof == null || !proof.IsNormal || proof.Context != Context || proof.Target.Window != notice.Window ||
                proof.At < notice.StartedAt || proof.At > notice.ArrowAt) return false;
            try { if (!revisionCurrent(revision)) return false; } catch (Exception) { return false; }
            CancelPendingInput(); latestSequence = notice.Sequence; latestGeneration = notice.Generation;
            completed = notice.Copy(); deliveredRevision = revision; currentRevision = revisionCurrent; return true;
        }
        internal bool ObserveNotice(SnapChordNotice notice)
        {
            if (notice == null) return false;
            if (notice.Kind == SnapChordNoticeKind.Cancelled) { CancelPendingInput(); return false; }
            return notice.Kind == SnapChordNoticeKind.Completed && AdmitCompleted(notice);
        }
        bool Eligible(SnapIntentProof proof, long now)
        {
            return Clock(now) && AuthorityCurrent() && completed.Before != null && proof != null && proof.IsArranged && proof.GeometryValid &&
                proof.Context == Context && completed.Before.SameBinding(proof) && proof.At >= completed.ArrowAt && proof.At <= now &&
                now >= completed.At && now - completed.StartedAt <= frequency * 2;
        }
        internal bool TryPermit(SnapIntentProof proof, long now, out SnapIntentPermit result)
        {
            result = null;
            if (!Eligible(proof, now) || (permit != null && permit.Consumed)) return false;
            if (permit == null) permit = new SnapIntentPermit(this, completed.Sequence, Context, proof);
            result = permit; return true;
        }
        internal bool TryReserve(SnapIntentPermit value, SnapIntentProof proof, long now, out SnapIntentReservation result)
        {
            result = null;
            if (!Current(value) || value.Consumed || !Eligible(proof, now) || !value.Binding.SameBinding(proof)) return false;
            value.Consumed = true;
            reservation = new SnapIntentReservation(this, value, proof, now); result = reservation; return true;
        }
        internal bool Current(SnapIntentPermit value)
        { return value != null && Object.ReferenceEquals(value.Owner, this) && Object.ReferenceEquals(value, permit) && value.Context == Context && AuthorityCurrent() && value.Sequence == completed.Sequence; }
        internal void Abort(SnapIntentReservation value)
        { if (value != null && Object.ReferenceEquals(value.Owner, this)) value.Cancelled = true; }
        internal bool CanPrepare(SnapIntentReservation value, SnapIntentProof oldCandidate, long now)
        {
            return Clock(now) && value != null && Object.ReferenceEquals(value.Owner, this) && Object.ReferenceEquals(value, reservation) &&
                !value.Cancelled && !value.NativeAuthorized && Current(value.Permit) && !value.Cancelled && !value.NativeAuthorized &&
                Object.ReferenceEquals(value, reservation) && value.Permit.Consumed && oldCandidate != null &&
                oldCandidate.IsArranged && oldCandidate.GeometryValid && oldCandidate.Context == Context &&
                oldCandidate.SameBinding(value.Binding) && oldCandidate.SameGeometry(value.Binding) &&
                oldCandidate.At <= now && now >= value.ReservedAt && now - completed.StartedAt <= frequency * 2;
        }
        bool PreparedCurrent(SnapIntentReservation value, SnapIntentProof prepared, long now, bool authorized)
        {
            if (!Clock(now) || value == null || !Object.ReferenceEquals(value.Owner, this) || !Object.ReferenceEquals(value, reservation) ||
                value.Cancelled || value.NativeAuthorized != authorized || !Current(value.Permit) || value.Cancelled || value.NativeAuthorized != authorized ||
                !Object.ReferenceEquals(value, reservation) || !value.Permit.Consumed || prepared == null ||
                !prepared.IsArranged || !prepared.GeometryValid || !prepared.LineageVerified || prepared.Context != Context ||
                !prepared.Target.Equals(value.Binding.Target) || !prepared.Area.Equals(value.Binding.Area) ||
                prepared.WakeEpoch != value.Binding.WakeEpoch || !prepared.SameGeometry(value.Binding) ||
                prepared.At < value.ReservedAt || prepared.At > now || now < value.ReservedAt || now - completed.StartedAt > frequency * 2 ||
                prepared.OriginNonce != value.Binding.Nonce || prepared.OriginGeneration != value.Binding.ReceiptGeneration) return false;
            // Same candidate increments its generation. Replacement must be an
            // actually retired old marker plus a distinct monotonic nonce.
            return (prepared.Nonce == prepared.OriginNonce && prepared.OriginGeneration < Int64.MaxValue && prepared.ReceiptGeneration == prepared.OriginGeneration + 1) ||
                (prepared.OldMarkerRetired && prepared.Nonce > prepared.OriginNonce && prepared.ReceiptGeneration == 1);
        }
        internal bool CanConsume(SnapIntentReservation value, SnapIntentProof prepared, long now)
        { return PreparedCurrent(value, prepared, now, false); }
        // Last managed fence after ledger MarkInvoked's fallible native reads.
        // This confirms an already-spent authorization; it never spends again.
        internal bool NativeAuthorizationStillCurrent(SnapIntentReservation value, SnapIntentProof prepared, long now)
        { return PreparedCurrent(value, prepared, now, true); }
        internal bool Authorize(SnapIntentReservation value, SnapIntentProof prepared, long now)
        { if (!CanConsume(value, prepared, now)) return false; value.NativeAuthorized = true; return true; }
        internal void RetireMatching(SnapCorrectionTarget target, SnapCorrectionArea area, long wakeEpoch)
        {
            if (completed != null && completed.Before != null && completed.Before.Target.Equals(target) && completed.Before.Area.Equals(area) && completed.Before.WakeEpoch == wakeEpoch)
                CancelPendingInput();
        }
    }
}
