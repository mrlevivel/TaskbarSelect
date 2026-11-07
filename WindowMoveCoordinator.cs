using System;
using System.Diagnostics;

namespace TaskbarSelect
{
    // One operation owns the slot until its caller unwinds and calls Exit.
    // Cancellation invalidates that operation without admitting nested work.
    internal sealed class WindowMoveCoordinator : IDisposable
    {
        internal const int DefaultTimeoutMilliseconds = 1000;
        readonly object gate = new object();
        readonly Func<long> clock;
        readonly long timeoutTicks;
        bool busy, disposed;
        long generation, startedGeneration, deadline;

        internal WindowMoveCoordinator() : this(Stopwatch.GetTimestamp, DefaultTimeoutMilliseconds) { }
        internal WindowMoveCoordinator(Func<long> clock, int timeoutMilliseconds)
        {
            if (clock == null) throw new ArgumentNullException("clock");
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            this.clock = clock;
            timeoutTicks = Math.Max(1, Stopwatch.Frequency * timeoutMilliseconds / 1000);
        }

        internal bool TryEnter()
        {
            lock (gate) {
                if (busy || disposed) return false;
                long now = clock();
                deadline = now > Int64.MaxValue - timeoutTicks ? Int64.MaxValue : now + timeoutTicks;
                startedGeneration = ++generation;
                busy = true;
                return true;
            }
        }
        internal bool Busy { get { lock (gate) return busy; } }
        internal long Generation { get { lock (gate) return generation; } }
        internal long Deadline { get { lock (gate) return deadline; } }
        internal bool Current
        {
            get { lock (gate) return busy && !disposed && generation == startedGeneration && clock() < deadline; }
        }
        internal bool Cancelled
        {
            get { lock (gate) return busy && (disposed || generation != startedGeneration); }
        }
        internal bool Expired
        {
            get { lock (gate) return busy && clock() >= deadline; }
        }
        internal void Cancel()
        {
            lock (gate) generation++;
        }
        internal void Exit()
        {
            lock (gate) { busy = false; deadline = 0; }
        }
        public void Dispose()
        {
            lock (gate) {
                if (disposed) return;
                disposed = true;
                generation++;
            }
        }
    }
}
