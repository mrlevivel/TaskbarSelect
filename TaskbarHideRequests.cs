using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TaskbarSelect
{
    // Posting a hide request is not evidence that Explorer processed it. Keep a
    // bounded budget until visibility actually changes, even across rediscovery.
    // This ledger owns no HWND marker and cannot release recovery ownership.
    internal sealed class TaskbarHideRequests
    {
        internal sealed class Identity : IEquatable<Identity>
        {
            internal readonly IntPtr Window;
            internal readonly uint Process, Thread;
            internal readonly long ProcessStarted;
            internal Identity(IntPtr window, uint process, long processStarted, uint thread)
            { Window = window; Process = process; ProcessStarted = processStarted; Thread = thread; }
            internal bool Valid { get { return Window != IntPtr.Zero && Process != 0 && ProcessStarted > 0 && Thread != 0; } }
            public bool Equals(Identity other)
            { return other != null && Window == other.Window && Process == other.Process && ProcessStarted == other.ProcessStarted && Thread == other.Thread; }
            public override bool Equals(object other) { return Equals(other as Identity); }
            public override int GetHashCode()
            { unchecked { return (((Window.GetHashCode() * 397) ^ Process.GetHashCode()) * 397 ^ ProcessStarted.GetHashCode()) * 397 ^ Thread.GetHashCode(); } }
        }
        internal enum RequestStatus { Invalid, Hidden, Idle, Waiting, Attempted, CircuitOpen, Capacity }
        internal sealed class Result
        {
            internal RequestStatus Status;
            internal bool Accepted;
            internal int Attempts;
            internal bool Attempted { get { return Status == RequestStatus.Attempted; } }
            internal bool CircuitOpen { get { return Status == RequestStatus.CircuitOpen || Status == RequestStatus.Capacity; } }
        }
        sealed class Entry { internal int Attempts; internal long LastAttempt; internal bool InFlight, HiddenAcknowledged; }
        internal const int MaximumEntries = 128;
        internal const int MaximumAttempts = 3;
        readonly object gate = new object();
        readonly Dictionary<Identity, Entry> pending = new Dictionary<Identity, Entry>();
        readonly Func<long> clock;
        internal TaskbarHideRequests() : this(delegate { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }) { }
        internal TaskbarHideRequests(Func<long> clock) { if (clock == null) throw new ArgumentNullException("clock"); this.clock = clock; }
        internal int Count { get { lock (gate) return pending.Count; } }
        internal Identity[] RememberedIdentities
        { get { lock (gate) { var copy = new Identity[pending.Count]; pending.Keys.CopyTo(copy, 0); return copy; } } }
        internal void RetireDestroyed(Identity identity)
        { if (identity != null) lock (gate) pending.Remove(identity); }
        static Result Outcome(RequestStatus status, Entry entry)
        { return new Result { Status = status, Attempts = entry == null ? 0 : entry.Attempts }; }

        internal Result Request(Identity identity, bool visible, bool shouldHide, Func<bool> hide)
        {
            if (identity == null || !identity.Valid) return Outcome(RequestStatus.Invalid, null);
            Entry entry;
            lock (gate) {
                pending.TryGetValue(identity, out entry);
                if (!visible) {
                    if (entry != null && entry.InFlight) entry.HiddenAcknowledged = true;
                    else pending.Remove(identity);
                    return Outcome(RequestStatus.Hidden, entry);
                }
                // Settings, power events and new Bar objects cannot replenish an
                // unanswered request budget. Restore is independent of this class.
                if (!shouldHide) return Outcome(RequestStatus.Idle, entry);
                if (hide == null) return Outcome(RequestStatus.Invalid, entry);
                if (entry == null) {
                    if (pending.Count >= MaximumEntries) return Outcome(RequestStatus.Capacity, null);
                    entry = new Entry(); pending.Add(identity, entry);
                }
                if (entry.Attempts >= MaximumAttempts) return Outcome(RequestStatus.CircuitOpen, entry);
                if (entry.InFlight) return Outcome(RequestStatus.Waiting, entry);
                long now = clock();
                if (entry.Attempts != 0) {
                    long elapsed = unchecked(now - entry.LastAttempt);
                    long delay = entry.Attempts == 1 ? 2000 : 8000;
                    if (now < entry.LastAttempt || elapsed < 0 || elapsed < delay) return Outcome(RequestStatus.Waiting, entry);
                }
                entry.Attempts++; entry.LastAttempt = now; entry.InFlight = true;
            }
            bool accepted = false;
            try { accepted = hide(); }
            catch (Exception) { /* A failed post still consumes its bounded attempt. */ }
            finally {
                lock (gate) {
                    entry.InFlight = false;
                    Entry current;
                    if (entry.HiddenAcknowledged && pending.TryGetValue(identity, out current) && Object.ReferenceEquals(current, entry))
                        pending.Remove(identity);
                }
            }
            var result = Outcome(RequestStatus.Attempted, entry); result.Accepted = accepted; return result;
        }
    }
}
