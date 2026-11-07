using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarSelect
{
    // The UI publishes immutable snapshots. STA workers never read Controller
    // state or controls, and cancelled discovery cannot reopen a closed panel.
    internal sealed class GhostSearchSession : IDisposable
    {
        readonly object gate = new object();
        List<Display> targets = new List<Display>();
        List<AppRule> rules = new List<AppRule>();
        List<GhostApp> ready;
        string version = "";
        bool active = true, failed, completed;
        internal bool Active { get { lock (gate) return active; } }
        internal string Version { get { lock (gate) return version; } }
        internal List<Display> Targets { get { lock (gate) return targets.Select(Copy).ToList(); } }
        internal List<AppRule> Rules { get { lock (gate) return rules.Select(r => new AppRule { Key = r.Key }).ToList(); } }
        static Display Copy(Display d)
        { return new Display { Device = d.Device, Identity = d.Identity, X = d.X, Y = d.Y, Width = d.Width, Height = d.Height, Primary = d.Primary }; }
        internal void Update(IEnumerable<Display> displays, IEnumerable<AppRule> exclusions, string context)
        {
            var nextTargets = (displays ?? new Display[0]).Where(d => d != null).Select(Copy).ToList();
            var nextRules = (exclusions ?? new AppRule[0]).Where(r => r != null).Select(r => new AppRule { Key = r.Key }).ToList();
            lock (gate) { if (!active) return; targets = nextTargets; rules = nextRules; version = context ?? ""; }
        }
        internal void Read(Func<Func<bool>, List<GhostApp>> reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            var worker = new Thread(delegate() {
                List<GhostApp> result = null; bool error = false;
                try { result = reader(delegate { return !Active; }); } catch (Exception) { error = true; }
                lock (gate) { if (active) { ready = result ?? new List<GhostApp>(); failed = error; completed = true; } }
            });
            worker.IsBackground = true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
        internal bool Take(out List<GhostApp> apps, out bool error)
        {
            lock (gate) {
                apps = null; error = false;
                if (!active || !completed) return false;
                apps = ready; error = failed; ready = null; completed = false; return true;
            }
        }
        public void Dispose() { lock (gate) { active = false; ready = null; completed = false; } }
    }

    internal static class GhostBarSearchLayout
    {
        internal static int Slot(int width, int dpi)
        { return Math.Max(1, Math.Min(WindowShelfPolicy.Scale(44, dpi), (width - WindowShelfPolicy.Scale(16, dpi)) / 4)); }
        internal static int Capacity(int width, int dpi)
        { int slot = Slot(width, dpi); return Math.Max(1, Math.Min(10, (width - WindowShelfPolicy.Scale(16, dpi) - slot * 3) / slot)); }
        internal static int Width(int width, int dpi, int count, bool paged)
        { return Math.Min(width, WindowShelfPolicy.Scale(16, dpi) + (Math.Max(0, count) + 1 + (paged ? 2 : 0)) * Slot(width, dpi)); }
        internal static Bitmap Icon(int dpi)
        {
            int pixels = WindowShelfPolicy.Scale(20, dpi); var bitmap = new Bitmap(pixels, pixels);
            using (var graphics = Graphics.FromImage(bitmap))
                GhostSearchPopup.DrawSearchIcon(graphics, new Rectangle(2, 2, pixels - 4, pixels - 4),
                    System.Windows.Forms.SystemInformation.HighContrast ? SystemColors.WindowText : Ui.Text, dpi);
            return bitmap;
        }
    }

    internal static class GhostSearchPlacement
    {
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        internal static bool Matches(ShelfWindow expected, ShelfWindow current, Display destination,
            string actualDevice, Native.Rect bounds, bool minimized, bool fullscreen)
        {
            return WindowShelfPolicy.SameWindow(expected, current) && destination != null &&
                !String.IsNullOrWhiteSpace(destination.Device) && String.Equals(destination.Device, actualDevice, StringComparison.OrdinalIgnoreCase) &&
                !minimized && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top && (!fullscreen ||
                FullscreenMovePolicy.Matches(bounds, new Native.Rect { Left = destination.X, Top = destination.Y,
                    Right = destination.X + destination.Width, Bottom = destination.Y + destination.Height }));
        }
        internal static bool Observe(ShelfWindow expected, Display destination, bool fullscreen)
        {
            ShelfWindow current; Native.Rect bounds;
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            return WindowShelfCatalog.TryReadWindow(expected.Handle, true, out current) &&
                Native.GetMonitorInfo(Native.MonitorFromWindow(expected.Handle, 2), ref monitor) &&
                Native.GetWindowRect(expected.Handle, out bounds) && Matches(expected, current, destination, monitor.Device, bounds, IsIconic(expected.Handle), fullscreen);
        }
    }
}
