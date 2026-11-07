using System;

namespace TaskbarSelect
{
    // Speculative query cadence only. A policy decision never supplies target
    // identity, extends a cache timestamp or admits a click/move operation.
    internal sealed class HoverQueryPolicy : IDisposable
    {
        internal enum Completion { Success, Miss, Failure, Deferred, Abandoned }
        internal sealed class Key
        {
            internal readonly IntPtr Source, OriginTaskbar, OriginWindow;
            internal readonly uint OriginProcess;
            internal readonly long OriginStarted;
            internal readonly int X, Y;
            internal readonly bool Preview;
            internal readonly string OriginApp;
            internal Key(IntPtr source, TaskbarMover.Point point, bool preview = false, TaskbarTargets.Target origin = null)
            {
                Source = source; X = point.X; Y = point.Y; Preview = preview;
                if (origin != null) {
                    OriginTaskbar = origin.Taskbar; OriginWindow = origin.Window; OriginProcess = origin.Process;
                    OriginStarted = origin.ProcessStarted; OriginApp = origin.Button == null ? null : origin.Button.AppId;
                }
            }
            internal bool Same(Key other)
            {
                return other != null && Source == other.Source && X == other.X && Y == other.Y && Preview == other.Preview &&
                    OriginTaskbar == other.OriginTaskbar && OriginWindow == other.OriginWindow && OriginProcess == other.OriginProcess &&
                    OriginStarted == other.OriginStarted && String.Equals(OriginApp, other.OriginApp, StringComparison.OrdinalIgnoreCase);
            }
        }
        internal sealed class Ticket
        {
            internal readonly HoverQueryPolicy Owner;
            internal readonly Key Query;
            internal bool Finished;
            internal Ticket(HoverQueryPolicy owner, Key query) { Owner = owner; Query = query; }
        }
        readonly object gate = new object();
        readonly Func<long> clock;
        readonly long frequency;
        Ticket current;
        Key last;
        long lastNow = -1, nextAllowed, coolUntil, retryAt;
        int attempts, failures;
        bool successful, retryPending, clockFailed, disposed;
        internal HoverQueryPolicy(Func<long> clock, long frequency)
        {
            if (clock == null) throw new ArgumentNullException("clock");
            if (frequency <= 0 || frequency > Int64.MaxValue / 10000) throw new ArgumentOutOfRangeException("frequency");
            this.clock = clock; this.frequency = frequency;
        }
        internal bool TryAdmit(Key query, int minimumMilliseconds, out Ticket ticket)
        {
            ticket = null;
            lock (gate) {
                long now;
                if (disposed || query == null || query.Source == IntPtr.Zero || minimumMilliseconds < 0 || minimumMilliseconds > 1000 || !Now(out now)) return false;
                bool same = last != null && last.Same(query);
                // Preserve latest-source coalescing: moving from a native bar
                // into a preview (or another bar) must replace queued old work
                // immediately. Provider failure cooldown still spans sources.
                bool sourceChanged = last != null && (last.Source != query.Source || last.Preview != query.Preview);
                if (same && ((current != null && !current.Finished) || successful || !retryPending || attempts >= 2)) return false;
                if ((!sourceChanged && now < nextAllowed) || now < coolUntil || (same && now < retryAt)) return false;
                if (!same) { attempts = 0; successful = retryPending = false; last = query; }
                attempts++; retryPending = false;
                current = ticket = new Ticket(this, query);
                nextAllowed = After(now, minimumMilliseconds); return true;
            }
        }
        internal bool MayStart(Ticket ticket)
        {
            lock (gate) {
                long now;
                return !disposed && ticket != null && Object.ReferenceEquals(ticket.Owner, this) && !ticket.Finished &&
                    Now(out now) && now >= coolUntil;
            }
        }
        internal void Complete(Ticket ticket, Completion completion)
        {
            lock (gate) {
                if (ticket == null || !Object.ReferenceEquals(ticket.Owner, this) || ticket.Finished) return;
                ticket.Finished = true;
                long now;
                if (disposed || !Now(out now)) return;
                bool latest = Object.ReferenceEquals(ticket, current);
                if (completion == Completion.Success) {
                    if (latest) { failures = 0; coolUntil = 0; successful = true; retryPending = false; }
                    return;
                }
                if (completion == Completion.Failure) {
                    failures = Math.Min(4, failures + 1);
                    coolUntil = Math.Max(coolUntil, After(now, 1000 << (failures - 1)));
                }
                if (!latest) return;
                successful = false;
                retryPending = completion != Completion.Abandoned && attempts < 2;
                // One delayed retry helps a newly waking provider. A second
                // failure/miss never turns a parked pointer into a retry loop.
                retryAt = Math.Max(coolUntil, After(now, completion == Completion.Miss ? 350 : 150));
            }
        }
        internal void Leave()
        {
            lock (gate) {
                if (disposed) return;
                current = null; last = null; attempts = 0; successful = retryPending = false;
                // Leaving a hover surface is not evidence that a failing
                // provider recovered. Preserve its cooldown and rate floor.
            }
        }
        bool Now(out long now)
        {
            now = 0;
            if (clockFailed) return false;
            try { now = clock(); }
            catch (Exception) { clockFailed = true; return false; }
            if (now < 0 || (lastNow >= 0 && now < lastNow)) { clockFailed = true; return false; }
            lastNow = now; return true;
        }
        long After(long now, int milliseconds)
        {
            long ticks = frequency / 1000 * milliseconds + frequency % 1000 * milliseconds / 1000;
            return now > Int64.MaxValue - ticks ? Int64.MaxValue : now + ticks;
        }
        public void Dispose() { lock (gate) { disposed = true; current = null; last = null; retryPending = false; } }
    }
}
