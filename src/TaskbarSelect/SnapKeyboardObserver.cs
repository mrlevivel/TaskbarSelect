using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarSelect
{
    // Optional passive Left/Right observer only. Constructing this object does
    // not install a hook. Start is explicit, asynchronous and may run once.
    // UI integration must re-prove lifetime/area/nonce/action ownership later;
    // an input notice is never permission or geometry authority.
    internal sealed class SnapKeyboardObserver : IDisposable
    {
        const int KeyboardLowLevel = 13;
        const uint StartMessage = 0x8000 + 0x314, StopMessage = StartMessage + 1;
        const uint KeyDown = 0x100, KeyUp = 0x101, SystemKeyDown = 0x104, SystemKeyUp = 0x105;
        const uint Injected = 0x12, UpFlag = 0x80, AllowedFlags = 0xB3;
        static long nextGeneration;
        static readonly object rootsGate = new object();
        // Even confirmed Unhook can leave a callback in flight. A successful
        // registration's small pass-through owner stays rooted to process exit.
        // Integration owns one observer for its lifetime, with no auto-rearm.
        static readonly List<Hook> retiredCallbacks = new List<Hook>();
        delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)]
        struct KeyboardData { internal uint Key, ScanCode, Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct Message { internal IntPtr Window; internal uint Kind; internal UIntPtr WParam; internal IntPtr LParam; internal uint Time; internal Point Position; internal uint Private; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int kind, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool PeekMessage(out Message message, IntPtr window, uint first, uint last, uint remove);
        [DllImport("user32.dll")] static extern int GetMessage(out Message message, IntPtr window, uint first, uint last);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll", SetLastError = true)] static extern bool PostThreadMessage(uint thread, uint message, UIntPtr first, IntPtr second);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowArranged(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern IntPtr GetProperty(IntPtr window, string name);

        sealed class Witness
        {
            internal readonly SnapIntentProof Proof;
            internal readonly string Property;
            internal readonly long Revision;
            internal Witness(SnapIntentProof proof, string property, long revision)
            { Proof = CopyProof(proof); Property = property; Revision = revision; }
        }
        sealed class Delivery
        {
            internal readonly SnapChordNotice Notice;
            internal readonly long ContextRevision, IntentRevision;
            internal Delivery(SnapChordNotice notice, long contextRevision, long intentRevision)
            { Notice = notice; ContextRevision = contextRevision; IntentRevision = intentRevision; }
        }
        readonly long generation = Interlocked.Increment(ref nextGeneration);
        readonly object lifetimeGate = new object();
        Thread thread;
        Hook callback;
        IntPtr hook;
        volatile uint ownerThread;
        uint registeredTick;
        long registeredAt, revision, parserRevision, intentRevision;
        // 0: no retirement result/request; 1: asynchronous stop pending;
        // 2: native absence confirmed; 3: stop/unhook outcome unknown.
        int started, stopped, available, installError, retireError, retirementState;
        SnapChordPolicy parser;
        Witness witness;
        Delivery delivery;

        // A registration claim, not proof of future hook delivery. Windows can
        // remove low-level hooks silently; there is deliberately no auto-rearm.
        internal bool Available { get { return Interlocked.CompareExchange(ref available, 0, 0) != 0 && Interlocked.CompareExchange(ref stopped, 0, 0) == 0; } }
        internal int InstallError { get { return Interlocked.CompareExchange(ref installError, 0, 0); } }
        internal int RetireError { get { return Interlocked.CompareExchange(ref retireError, 0, 0); } }
        internal bool StopPending { get { return Interlocked.CompareExchange(ref retirementState, 0, 0) == 1; } }
        internal bool StoppedConfirmed { get { return Interlocked.CompareExchange(ref retirementState, 0, 0) == 2; } }
        internal bool RetirementUncertain { get { return Interlocked.CompareExchange(ref retirementState, 0, 0) == 3; } }
        internal long Generation { get { return generation; } }
        internal bool Start()
        {
            if (Interlocked.CompareExchange(ref stopped, 0, 0) != 0 || Interlocked.CompareExchange(ref started, 1, 0) != 0) return false;
            try {
                thread = new Thread(Run) { IsBackground = true, Name = "Taskbar Select snap intent" };
                thread.Start(); return true;
            } catch (Exception) {
                Interlocked.Exchange(ref stopped, 1);
                Interlocked.Exchange(ref retirementState, 2); // Thread never started; no registration was possible.
                return false;
            }
        }
        internal void PublishWitness(SnapIntentProof proof, string nonceProperty)
        {
            if (Interlocked.CompareExchange(ref stopped, 0, 0) != 0) return;
            if (proof == null || !proof.Valid ||
                String.IsNullOrEmpty(nonceProperty) || nonceProperty.Length > 128) { Invalidate(); return; }
            long current = Interlocked.Read(ref revision);
            Interlocked.Exchange(ref witness, new Witness(proof, nonceProperty, current));
        }
        // Call on focus/lifetime/wake/quiesce/busy/receipt-context changes, not
        // for routine publication of the same still-authoritative witness.
        internal void Invalidate()
        {
            Interlocked.Increment(ref revision);
            Interlocked.Increment(ref intentRevision);
            Interlocked.Exchange(ref witness, null);
            Interlocked.Exchange(ref delivery, null);
        }
        // This is a mandatory independent final fence, including when a later
        // Started/Cancelled notice was replaced before the UI could read it.
        internal bool Current(long deliveredRevision)
        { return Available && deliveredRevision == Interlocked.Read(ref intentRevision); }
        internal bool TryTake(out SnapChordNotice notice, out long deliveredRevision)
        {
            notice = null; deliveredRevision = -1;
            Delivery candidate = Interlocked.Exchange(ref delivery, null);
            if (candidate == null || !Current(candidate.IntentRevision) || candidate.ContextRevision != Interlocked.Read(ref revision)) return false;
            long now = Stopwatch.GetTimestamp();
            if (now < candidate.Notice.At || now < candidate.Notice.StartedAt ||
                now - candidate.Notice.StartedAt > Stopwatch.Frequency * 2) return false;
            notice = candidate.Notice; deliveredRevision = candidate.IntentRevision; return true;
        }
        void Run()
        {
            bool registered = false;
            try {
                Message message;
                PeekMessage(out message, IntPtr.Zero, 0, 0, 0); // Make this thread's queue before posting.
                uint currentOwner = GetCurrentThreadId();
                lock (lifetimeGate) ownerThread = currentOwner;
                if (Interlocked.CompareExchange(ref stopped, 0, 0) != 0 || !PostThreadMessage(currentOwner, StartMessage, UIntPtr.Zero, IntPtr.Zero)) return;
                int result;
                while ((result = GetMessage(out message, IntPtr.Zero, 0, 0)) > 0) {
                    if (Interlocked.CompareExchange(ref stopped, 0, 0) != 0 || message.Kind == StopMessage) break;
                    if (message.Kind == StartMessage) {
                        if (registered || callback != null) continue;
                        registeredAt = Stopwatch.GetTimestamp(); registeredTick = unchecked((uint)Environment.TickCount);
                        callback = Keyboard;
                        hook = SetWindowsHookEx(KeyboardLowLevel, callback, GetModuleHandle(null), 0);
                        if (hook == IntPtr.Zero) { Interlocked.Exchange(ref installError, Marshal.GetLastWin32Error()); break; }
                        registered = true;
                        // These are initialization reads outside the callback.
                        // A held/ambiguous startup chord must not create intent.
                        bool released = true;
                        foreach (int key in new[] { 0x5B, 0x5C, 0x25, 0x27, 0x10, 0x11, 0x12 })
                            if ((GetAsyncKeyState(key) & 0x8000) != 0) released = false;
                        parser = new SnapChordPolicy(Stopwatch.Frequency, generation, registeredAt, registeredTick, released);
                        parserRevision = Interlocked.Read(ref revision);
                        Interlocked.Exchange(ref available, 1);
                        continue;
                    }
                    TranslateMessage(ref message); DispatchMessage(ref message);
                }
            } catch (Exception) { /* Optional observation fails closed; ordinary input remains native. */ }
            finally {
                Interlocked.Exchange(ref available, 0); Invalidate();
                if (parser != null) parser.Invalidate();
                parser = null;
                bool confirmed = !registered;
                if (registered) {
                    lock (rootsGate) retiredCallbacks.Add(callback);
                    bool removed = false;
                    try { removed = UnhookWindowsHookEx(hook); }
                    catch (Exception) { }
                    confirmed = removed;
                    if (!removed) {
                        MarkRetirementUncertain();
                        Interlocked.Exchange(ref retireError, Marshal.GetLastWin32Error());
                    }
                }
                hook = IntPtr.Zero;
                // Clear before this OS thread can exit/reuse its ID. Dispose
                // holds this same short gate while reading and posting, so it
                // cannot retain an ID across this publication boundary.
                lock (lifetimeGate) {
                    ownerThread = 0;
                    if (confirmed) Interlocked.Exchange(ref retirementState, 2);
                }
            }
        }
        IntPtr Keyboard(int code, IntPtr message, IntPtr data)
        {
            try {
                if (code >= 0 && Available && parser != null && GetCurrentThreadId() == ownerThread) {
                    KeyboardData input = (KeyboardData)Marshal.PtrToStructure(data, typeof(KeyboardData));
                    uint kind = unchecked((uint)message.ToInt64());
                    bool down = kind == KeyDown || kind == SystemKeyDown;
                    bool up = kind == KeyUp || kind == SystemKeyUp;
                    long now = Stopwatch.GetTimestamp();
                    uint age = unchecked((uint)Environment.TickCount - input.Time);
                    bool current = age <= 2000 && now >= registeredAt &&
                        (now - registeredAt > Stopwatch.Frequency * 2 || unchecked((int)(input.Time - registeredTick)) >= 0);
                    bool valid = (down || up) && down == ((input.Flags & UpFlag) == 0) && (input.Flags & 0x20) == 0 &&
                        (input.Flags & ~AllowedFlags) == 0 && current;
                    int key = SanitizedKey(input.Key);
                    long currentRevision = Interlocked.Read(ref revision);
                    if (currentRevision != parserRevision) { parser.Invalidate(); parserRevision = currentRevision; }
                    var packet = new SnapChordPacket { Key = valid ? key : 0, Down = down,
                        Flags = valid ? input.Flags : Injected, Window = key == 0 ? IntPtr.Zero : GetForegroundWindow(),
                        NativeTime = input.Time, CurrentNativeTick = unchecked((uint)Environment.TickCount),
                        At = now, Generation = generation };
                    if (valid && down && (key == 0x25 || key == 0x27) && (input.Flags & Injected) == 0)
                        packet.Before = CaptureBefore(packet.Window, now, currentRevision);
                    SnapChordNotice notice = parser.Observe(packet);
                    if (notice != null) {
                        if (notice.Kind == SnapChordNoticeKind.Started || notice.Kind == SnapChordNoticeKind.Cancelled) {
                            Interlocked.Increment(ref intentRevision);
                            Interlocked.Exchange(ref delivery, null);
                        }
                        // One slot: later state replaces earlier metadata; there
                        // is never a backlog of keyboard events or corrections.
                        Interlocked.Exchange(ref delivery, new Delivery(notice, currentRevision, Interlocked.Read(ref intentRevision)));
                    }
                }
            } catch (Exception) { Invalidate(); }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        SnapIntentProof CaptureBefore(IntPtr foreground, long now, long currentRevision)
        {
            Witness expected = Interlocked.CompareExchange(ref witness, null, null);
            if (expected == null || expected.Revision != currentRevision || expected.Proof.Target.Window != foreground ||
                now < expected.Proof.At || now - expected.Proof.At > Stopwatch.Frequency * 2 ||
                GetProperty(foreground, expected.Property).ToInt64() != expected.Proof.Nonce ||
                IsIconic(foreground) || IsZoomed(foreground) || IsWindowArranged(foreground) ||
                GetForegroundWindow() != foreground || currentRevision != Interlocked.Read(ref revision)) return null;
            SnapIntentProof result = CopyProof(expected.Proof);
            result.At = now; result.Normal = true; result.Arranged = result.Maximized = false;
            return result;
        }
        static int SanitizedKey(uint key)
        {
            switch (key) {
                case 0x5B: case 0x5C: case 0x25: case 0x27:
                case 0x10: case 0x11: case 0x12: case 0xA0: case 0xA1: case 0xA2: case 0xA3: case 0xA4: case 0xA5: return (int)key;
                default: return 0; // No text/scan code/key-name/history is retained.
            }
        }
        static SnapIntentProof CopyProof(SnapIntentProof proof)
        {
            return new SnapIntentProof { Target = proof.Target, Area = proof.Area, Nonce = proof.Nonce,
                ReceiptGeneration = proof.ReceiptGeneration, WakeEpoch = proof.WakeEpoch, Context = proof.Context, At = proof.At,
                Verified = proof.Verified, Normal = proof.Normal, Arranged = proof.Arranged, Maximized = proof.Maximized,
                OwnedAreaReady = proof.OwnedAreaReady, ActionReady = proof.ActionReady, ReceiptReady = proof.ReceiptReady };
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            Interlocked.CompareExchange(ref retirementState, 1, 0);
            Interlocked.Exchange(ref available, 0); Invalidate();
            // Reserve the never-started slot atomically against a racing Start.
            if (Interlocked.CompareExchange(ref started, -1, 0) == 0) {
                Interlocked.Exchange(ref retirementState, 2); return;
            }
            lock (lifetimeGate) {
                uint owner = ownerThread;
                if (owner != 0 && !PostThreadMessage(owner, StopMessage, UIntPtr.Zero, IntPtr.Zero)) {
                    MarkRetirementUncertain();
                    Interlocked.Exchange(ref retireError, Marshal.GetLastWin32Error());
                }
            }
            // Never Join from UI; a callback may currently be returning to OS.
        }
        void MarkRetirementUncertain()
        {
            // A late failed post must not overwrite a concurrent positive
            // native retirement result already recorded by Run.finally.
            Interlocked.CompareExchange(ref retirementState, 3, 0);
            Interlocked.CompareExchange(ref retirementState, 3, 1);
        }
    }
}
