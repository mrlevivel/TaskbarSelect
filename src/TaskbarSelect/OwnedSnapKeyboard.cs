using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class OwnedSnapKeyOwnership
    {
        readonly HashSet<uint> keys = new HashSet<uint>();
        internal bool ConsumeExisting(uint key, bool up) { return up ? keys.Remove(key) : keys.Contains(key); }
        internal void Accept(uint key) { keys.Add(key); }
        internal void Clear() { keys.Clear(); }
    }
    internal sealed class OwnedSnapReleaseGate
    {
        internal bool Ready { get; private set; }
        internal void Reset() { Ready = false; }
        internal void Observe(bool released) { if (released) Ready = true; }
    }
    // Routing hints only. A hint never authorizes a move: the UI thread must
    // freshly prove the exact process/start identity and all movement guards.
    internal sealed class OwnedSnapTargetCache
    {
        internal const int Capacity = 128;
        readonly Dictionary<IntPtr, OwnedSnapKeyboard.Target> targets = new Dictionary<IntPtr, OwnedSnapKeyboard.Target>();
        readonly long lease;
        internal OwnedSnapTargetCache(long leaseTicks) { if (leaseTicks <= 0) throw new ArgumentOutOfRangeException("leaseTicks"); lease = leaseTicks; }
        internal int Count { get { return targets.Count; } }
        internal void Add(OwnedSnapKeyboard.Target value)
        {
            if (value == null || value.Window == IntPtr.Zero || value.State == null || value.Published <= 0) return;
            if (!targets.ContainsKey(value.Window) && targets.Count >= Capacity) {
                IntPtr oldest = IntPtr.Zero; long time = Int64.MaxValue;
                foreach (var pair in targets) if (pair.Value.Published < time) { oldest = pair.Key; time = pair.Value.Published; }
                targets.Remove(oldest);
            }
            targets[value.Window] = value;
        }
        internal OwnedSnapKeyboard.Target Find(IntPtr foreground, long now)
        {
            OwnedSnapKeyboard.Target value;
            if (!targets.TryGetValue(foreground, out value) || now < value.Published || now - value.Published > lease) return null;
            return value;
        }
        internal void Remove(IntPtr window) { targets.Remove(window); }
        internal void Clear() { targets.Clear(); }
    }
    // Input ownership only. Window metadata and geometry work stay out of the
    // hook callback. One pending notice; no backlog and no native-key replay.
    internal sealed class OwnedSnapKeyboard : NativeWindow, IDisposable
    {
        internal sealed class Target
        {
            internal IntPtr Window;
            internal long Published;
            internal object State;
        }
        internal sealed class Notice
        {
            internal Target Target;
            internal bool Left;
            internal long Epoch, Created;
        }
        const int HookKind = 13, NoticeMessage = 0x8000 + 0x391;
        const uint KeyDown = 0x100, KeyUp = 0x101, SysDown = 0x104, SysUp = 0x105;
        delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        static readonly List<HookProc> retired = new List<HookProc>();
        [StructLayout(LayoutKind.Sequential)] struct Packet { internal uint Key, Scan, Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct Keyboard { internal ushort Key, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct Mouse { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] struct InputData { [FieldOffset(0)] internal Keyboard Keyboard; [FieldOffset(0)] internal Mouse Mouse; }
        [StructLayout(LayoutKind.Sequential)] struct Input { internal uint Type; internal InputData Data; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, int message, IntPtr first, IntPtr second);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
        readonly Action<Notice> receive;
        readonly Func<bool> claimCurrent;
        readonly HookProc callback;
        readonly OwnedSnapKeyOwnership owned = new OwnedSnapKeyOwnership();
        readonly OwnedSnapReleaseGate release = new OwnedSnapReleaseGate();
        readonly OwnedSnapTargetCache targets = new OwnedSnapTargetCache(Stopwatch.Frequency * 300);
        IntPtr hook;
        Notice pending;
        long epoch;
        bool enabled, disposed;
        internal string Error { get; private set; }
        internal long BusyDrops { get; private set; }
        internal bool Enabled { get { return enabled; } }
        internal OwnedSnapKeyboard(Action<Notice> handler, Func<bool> claimCurrent = null)
        {
            receive = handler; this.claimCurrent = claimCurrent; callback = Callback;
            CreateHandle(new CreateParams { Caption = "Taskbar Select experimental snap", Parent = new IntPtr(-3) });
        }
        internal void AdvanceEpoch(long value) { epoch = value; pending = null; }
        internal void RequireRelease() { release.Reset(); pending = null; }
        internal void ClearTargets() { targets.Clear(); }
        internal void Publish(Target value, IntPtr rejectedWindow = default(IntPtr))
        {
            release.Observe(!Down(0x5B) && !Down(0x5C) && !Down(0x25) && !Down(0x27) && !Down(0x10) && !Down(0x11) && !Down(0x12));
            if (value != null) targets.Add(value);
            else if (rejectedWindow != IntPtr.Zero) targets.Remove(rejectedWindow);
        }
        internal bool Enable()
        {
            if (disposed) return false;
            if (enabled) return true;
            if (hook != IntPtr.Zero) { Error = "Previous keyboard hook cleanup is not confirmed"; return false; }
            hook = SetWindowsHookEx(HookKind, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) { Error = "Keyboard hook unavailable (Windows error " + Marshal.GetLastWin32Error() + ")"; return false; }
            release.Reset(); Error = null; enabled = true; return true;
        }
        internal void Disable()
        {
            enabled = false; pending = null; release.Reset(); owned.Clear();
            if (hook == IntPtr.Zero) return;
            if (UnhookWindowsHookEx(hook)) { hook = IntPtr.Zero; owned.Clear(); }
            else Error = "Keyboard hook cleanup is not confirmed; interception is disabled";
            // An already-dispatched callback may finish after successful unhook.
            if (!retired.Contains(callback)) retired.Add(callback);
        }
        static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        bool CanClaim()
        { try { return claimCurrent == null || claimCurrent(); } catch (Exception) { return false; } }
        internal static bool Eligible(uint key, uint flags, bool windows, bool modifiers, bool current, bool fresh)
        {
            // Ordinary injected input is supported in this opt-in experiment.
            // Lower-integrity injections, key-up packets and malformed flags
            // cannot claim a new chord. The production snap observer is unchanged.
            return (key == 0x25 || key == 0x27) && (flags & 0xA2) == 0 &&
                (flags & ~0xB3u) == 0 && windows && !modifiers && current && fresh;
        }
        static bool MaskStartMenu()
        {
            // Like PowerToys' centralized keyboard hook, a dummy key-up marks
            // an owned Win chord as used, without suppressing Windows-key up.
            // https://github.com/microsoft/PowerToys/blob/main/src/runner/centralized_kb_hook.cpp
            var input = new Input { Type = 1, Data = new InputData { Keyboard = new Keyboard { Key = 0xFF, Flags = 2, Extra = new UIntPtr(0x54534E50u) } } };
            return SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input))) == 1;
        }
        IntPtr Callback(int code, IntPtr message, IntPtr data)
        {
            try {
                uint kind = unchecked((uint)message.ToInt64());
                if (code >= 0 && data != IntPtr.Zero && (kind == KeyDown || kind == KeyUp || kind == SysDown || kind == SysUp)) {
                    var packet = (Packet)Marshal.PtrToStructure(data, typeof(Packet));
                    bool up = kind == KeyUp || kind == SysUp;
                    if (owned.ConsumeExisting(packet.Key, up)) return new IntPtr(1);
                    bool windows = Down(0x5B) || Down(0x5C), modifiers = Down(0x10) || Down(0x11) || Down(0x12);
                    if (!up && enabled && release.Ready && CanClaim() && pending != null && Eligible(packet.Key, packet.Flags, windows, modifiers,
                        GetForegroundWindow() == pending.Target.Window, true)) {
                        if (!MaskStartMenu()) return CallNextHookEx(hook, code, message, data);
                        owned.Accept(packet.Key); BusyDrops++; return new IntPtr(1);
                    }
                    var current = targets.Find(GetForegroundWindow(), Stopwatch.GetTimestamp());
                    if (!up && enabled && release.Ready && CanClaim() && pending == null && Eligible(packet.Key, packet.Flags, windows,
                        modifiers, current != null && GetForegroundWindow() == current.Window, current != null)) {
                        if (!MaskStartMenu()) { Error = "Windows refused the experimental key handoff"; return CallNextHookEx(hook, code, message, data); }
                        pending = new Notice { Target = current, Left = packet.Key == 0x25, Epoch = epoch, Created = Stopwatch.GetTimestamp() };
                        if (PostMessage(Handle, NoticeMessage, IntPtr.Zero, IntPtr.Zero)) { owned.Accept(packet.Key); return new IntPtr(1); }
                        pending = null; Error = "Experimental key handoff unavailable";
                    }
                }
            } catch (Exception) { enabled = false; Error = "Experimental keyboard handling stopped"; }
            return CallNextHookEx(hook, code, message, data);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NoticeMessage) {
                var notice = pending;
                try { if (enabled && notice != null) receive(notice); }
                finally { pending = null; }
                return;
            }
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (disposed) return;
            Disable(); targets.Clear(); disposed = true;
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
