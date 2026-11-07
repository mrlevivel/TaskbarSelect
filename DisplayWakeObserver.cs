using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class DisplayWakeObservation
    {
        internal bool Registered;
        internal long Notifications, Sequence;
        internal int State, Error;
    }

    // Passive notification receiver; never requests power, input or policy changes.
    internal sealed class DisplayWakeObserver : NativeWindow, IDisposable
    {
        internal static readonly Guid SessionDisplayStatus = new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
        [StructLayout(LayoutKind.Sequential)] struct Header { internal Guid Setting; internal uint Length; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterPowerSettingNotification(IntPtr registration);
        readonly Action<long> wake;
        readonly Action<DisplayWakeObservation> observe;
        readonly DisplayWakePolicy policy = new DisplayWakePolicy(Stopwatch.Frequency);
        IntPtr registration;
        bool disposed;
        long notifications;
        int lastState = -1, error;
        internal DisplayWakeObserver(Action<long> wake, Action<DisplayWakeObservation> observe = null)
        {
            if (wake == null) throw new ArgumentNullException("wake");
            this.wake = wake; this.observe = observe;
            try {
                // Invisible, nonactivating, app-owned regular HWND. Its lifetime does
                // not depend on whether Settings is visible or minimized to the tray.
                CreateHandle(new CreateParams { Caption = "Taskbar Select display notifications", Width = 0, Height = 0, ExStyle = 0x08000000 });
                Guid setting = SessionDisplayStatus;
                registration = RegisterPowerSettingNotification(Handle, ref setting, 0);
                if (registration == IntPtr.Zero) error = Marshal.GetLastWin32Error();
            } catch (Exception) { error = -1; }
            Publish();
        }
        void Publish()
        {
            if (observe == null) return;
            try { observe(new DisplayWakeObservation { Registered = registration != IntPtr.Zero,
                Notifications = notifications, Sequence = policy.Sequence, State = lastState, Error = error }); }
            catch (Exception) { /* Diagnostics cannot alter power/recovery behavior. */ }
        }
        protected override void WndProc(ref Message message)
        {
            if (!disposed && message.Msg == 0x0218 && message.WParam.ToInt64() == 0x8013) {
                if (registration == IntPtr.Zero || message.LParam == IntPtr.Zero) {
                    // An unregistered receiver never grants recovery. Malformed
                    // notifications cannot preserve an earlier armed Off state.
                    lastState = -1;
                    policy.Observe(registration != IntPtr.Zero, -1, Stopwatch.GetTimestamp());
                    Publish(); message.Result = new IntPtr(1); return;
                }
                try {
                    var header = (Header)Marshal.PtrToStructure(message.LParam, typeof(Header));
                    if (header.Setting == SessionDisplayStatus) {
                        lastState = header.Length == 4 ? Marshal.ReadInt32(message.LParam, Marshal.SizeOf(typeof(Header))) : -1;
                        notifications++;
                        long next = policy.Observe(true, lastState, Stopwatch.GetTimestamp());
                        if (next > 0) wake(next);
                        Publish(); message.Result = new IntPtr(1); return;
                    }
                } catch (Exception) { error = -2; policy.Observe(-1, Stopwatch.GetTimestamp()); Publish(); }
            }
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (registration != IntPtr.Zero) { UnregisterPowerSettingNotification(registration); registration = IntPtr.Zero; }
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // Optional isolated-test status only. The controller must not supply this
    // sink in a production profile. One bounded latest snapshot, no event log,
    // monitor/window identity, content, names, input or process-memory data.
    internal static class DisplayWakeTestStatus
    {
        internal static Action<DisplayWakeObservation> Writer(string path)
        {
            long last = -1;
            bool? registered = null;
            return delegate(DisplayWakeObservation value) {
                long now = Stopwatch.GetTimestamp();
                if (last >= 0 && registered == value.Registered && now >= last && now - last < Stopwatch.Frequency) return;
                last = now;
                registered = value.Registered;
                try {
                    string json = new JavaScriptSerializer().Serialize(new {
                        Registered = value.Registered, Notifications = value.Notifications,
                        Sequence = value.Sequence, State = value.State, Error = value.Error,
                        Source = "SessionDisplayStatus", MonotonicTicks = now, Frequency = Stopwatch.Frequency
                    });
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, json);
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                } catch (Exception) { }
            };
        }
    }
}
