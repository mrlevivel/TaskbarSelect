using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class GlobalShortcut : NativeWindow, IDisposable
    {
        internal const Keys Default = Keys.Control | Keys.Shift | Keys.Back;
        const int HotkeyMessage = 0x0312;
        const uint NoRepeat = 0x4000;
        int activeId, nextId = 1;
        internal Keys Current { get; private set; }
        internal event EventHandler Pressed;
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);

        internal GlobalShortcut()
        {
            CreateHandle(new CreateParams { Caption = "Taskbar Select shortcuts", Parent = new IntPtr(-3) });
        }
        internal static Keys[] AvailableKeys()
        {
            var keys = new List<Keys> { Keys.Back, Keys.Space, Keys.Left, Keys.Right, Keys.Up, Keys.Down };
            for (Keys key = Keys.A; key <= Keys.Z; key++) keys.Add(key);
            for (Keys key = Keys.D0; key <= Keys.D9; key++) keys.Add(key);
            for (Keys key = Keys.F1; key <= Keys.F24; key++) if (key != Keys.F12) keys.Add(key);
            return keys.ToArray();
        }
        internal static bool IsValid(Keys shortcut)
        {
            if (shortcut == Keys.None) return true;
            int count = ((shortcut & Keys.Control) != 0 ? 1 : 0) + ((shortcut & Keys.Alt) != 0 ? 1 : 0) + ((shortcut & Keys.Shift) != 0 ? 1 : 0);
            return count >= 2 && (shortcut & ~(Keys.KeyCode | Keys.Control | Keys.Alt | Keys.Shift)) == 0 &&
                Array.IndexOf(AvailableKeys(), shortcut & Keys.KeyCode) >= 0;
        }
        internal static uint Modifiers(Keys shortcut)
        {
            return ((shortcut & Keys.Alt) != 0 ? 1u : 0) | ((shortcut & Keys.Control) != 0 ? 2u : 0) | ((shortcut & Keys.Shift) != 0 ? 4u : 0);
        }
        internal static string KeyName(Keys key)
        {
            if (key == Keys.Back) return "Backspace";
            if (key == Keys.Space) return "Space";
            if (key == Keys.Left) return "Left Arrow";
            if (key == Keys.Right) return "Right Arrow";
            if (key == Keys.Up) return "Up Arrow";
            if (key == Keys.Down) return "Down Arrow";
            if (key >= Keys.D0 && key <= Keys.D9) return ((int)key - (int)Keys.D0).ToString();
            return key.ToString();
        }
        internal static string Format(Keys shortcut)
        {
            if (shortcut == Keys.None) return "Off";
            return ((shortcut & Keys.Control) != 0 ? "Ctrl + " : "") + ((shortcut & Keys.Alt) != 0 ? "Alt + " : "") +
                ((shortcut & Keys.Shift) != 0 ? "Shift + " : "") + KeyName(shortcut & Keys.KeyCode);
        }
        internal void Apply(Keys shortcut, Action commit)
        {
            if (!IsValid(shortcut)) throw new InvalidOperationException("Choose at least two modifiers and a supported key.");
            if (shortcut == Current) { commit(); return; }
            int candidateId = shortcut == Keys.None ? 0 : nextId++;
            if (nextId > 0xBFFF) nextId = 1;
            if (candidateId != 0 && !RegisterHotKey(Handle, candidateId, Modifiers(shortcut) | NoRepeat, (uint)(shortcut & Keys.KeyCode))) {
                int code = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(Format(shortcut) + (code == 1409 ? " is already registered by another app." : " could not be registered (Windows error " + code + ")."));
            }
            // Keep the old binding until persistence succeeds, including when disabling.
            try { commit(); }
            catch { if (candidateId != 0) UnregisterHotKey(Handle, candidateId); throw; }
            if (activeId != 0) UnregisterHotKey(Handle, activeId);
            activeId = candidateId; Current = shortcut;
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HotkeyMessage) {
                if (MatchesMessage(activeId, Current, message.WParam, message.LParam) && Pressed != null) Pressed(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref message);
        }
        internal static bool MatchesMessage(int id, Keys shortcut, IntPtr parameter, IntPtr detail)
        {
            long data = detail.ToInt64();
            return id != 0 && parameter.ToInt64() == id && (uint)(data & 0xFFFF) == Modifiers(shortcut) &&
                (Keys)((data >> 16) & 0xFFFF) == (shortcut & Keys.KeyCode);
        }
        public void Dispose()
        {
            if (activeId != 0) UnregisterHotKey(Handle, activeId);
            activeId = 0; Current = Keys.None;
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
