using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal sealed partial class Controller
    {
        TilingPicker tilingPicker;
        sealed class TilingInvocation
        {
            internal ShelfWindow Window;
            internal Native.Rect Bounds;
            internal Native.MonitorInfo Monitor;
            internal Display Display;
            internal int Dpi;
        }
        TilingInvocation tilingInvocation;
        internal TilingPicker CurrentTilingPicker { get { return tilingPicker; } }
        bool IsTransientWindow(IntPtr window)
        { return IsSearchWindow(window) || (PinRails != null && PinRails.Handles.Contains(window)) ||
            (tilingPicker != null && !tilingPicker.IsDisposed && tilingPicker.IsHandleCreated && tilingPicker.Handle == window); }

        internal void FocusDirection(IntPtr foreground, WindowDirection direction)
        {
            if (disposed || !EffectiveEnabled || MovementPaused) { Mover.LastStatus = "Focus navigation is paused"; Revision++; return; }
            if (tilingPicker != null) { CancelTilingPicker(); Mover.LastStatus = "Layout picker closed"; Revision++; return; }
            ConfigureMover(Mover, Settings, EffectiveEnabled, MovementPaused);
            Mover.CancelPending();
            FocusNavigation.Focus(foreground, direction, Settings.ExcludedApps, Settings.AllowMovingFullscreenApps);
            Revision++;
        }

        internal void OpenTilingPicker(IntPtr foreground)
        {
            if (disposed || !EffectiveEnabled || MovementPaused) { Mover.LastStatus = "Tiling is paused"; Revision++; return; }
            if (tilingPicker != null) { CancelTilingPicker(); Mover.LastStatus = "Layout picker closed"; Revision++; return; }
            if (IsSearchWindow(foreground)) { Mover.LastStatus = "Close search before arranging a window"; Revision++; return; }
            var invocation = CaptureTilingInvocation(foreground);
            if (invocation == null) { Mover.LastStatus = "Tiling unavailable for this window or display"; Revision++; return; }
            Mover.CancelPending();
            var picker = new TilingPicker(delegate(WindowTile tile) { ApplyTiling(invocation, tile); });
            tilingInvocation = invocation; tilingPicker = picker;
            picker.FormClosed += delegate {
                if (Object.ReferenceEquals(tilingPicker, picker)) { tilingPicker = null; tilingInvocation = null; }
                Revision++;
            };
            var work = Rectangle.FromLTRB(invocation.Monitor.Work.Left, invocation.Monitor.Work.Top, invocation.Monitor.Work.Right, invocation.Monitor.Work.Bottom);
            try { picker.Open(work, invocation.Dpi, invocation.Window.AppName); }
            catch { CancelTilingPicker(); throw; }
            Mover.LastStatus = tilingPicker != null ? "Choose a layout with arrows, then Enter; Escape cancels" : "Windows could not focus the layout picker";
            Revision++;
        }

        TilingInvocation CaptureTilingInvocation(IntPtr foreground)
        {
            ShelfWindow window; Native.Rect bounds;
            if (foreground == IntPtr.Zero || TaskbarMover.ForegroundWindow() != foreground || WindowTilingNative.IsIconic(foreground) ||
                !WindowShelfCatalog.TryReadWindow(foreground, Settings.AllowMovingFullscreenApps, out window) ||
                AppCatalog.Matches(Settings.ExcludedApps, window.AppKey) || !Native.GetWindowRect(foreground, out bounds)) return null;
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(foreground, 2), ref monitor)) return null;
            var connected = Native.Displays();
            var matches = connected.Where(d => d.Device == monitor.Device && !String.IsNullOrWhiteSpace(d.Identity)).ToArray();
            if (matches.Length != 1 || connected.Count(d => d.Identity == matches[0].Identity) != 1 ||
                monitor.Work.Right <= monitor.Work.Left || monitor.Work.Bottom <= monitor.Work.Top || TaskbarMover.ForegroundWindow() != foreground) return null;
            return new TilingInvocation { Window = window, Bounds = bounds, Monitor = monitor, Display = KeyboardSearchDestination.Copy(matches[0]),
                Dpi = WindowShelfManager.ReadDpi(matches[0]) };
        }

        bool TilingDisplayCurrent(TilingInvocation invocation)
        {
            if (invocation == null) return false;
            Native.MonitorInfo monitor;
            if (!Native.TryMonitor(invocation.Monitor.Device, out monitor) || !TaskbarMover.ReturnPlacement.SameMonitor(invocation.Monitor, monitor)) return false;
            var connected = Native.Displays();
            var physical = connected.Where(d => d.Identity == invocation.Display.Identity).ToArray();
            return physical.Length == 1 && KeyboardSearchDestination.Same(invocation.Display, physical[0]) &&
                WindowShelfManager.ReadDpi(physical[0]) == invocation.Dpi;
        }

        void ApplyTiling(TilingInvocation invocation, WindowTile tile)
        {
            if (disposed || !EffectiveEnabled || MovementPaused) { Mover.LastStatus = "Tiling is paused; no layout applied"; Revision++; return; }
            if (!TilingDisplayCurrent(invocation)) { Mover.LastStatus = "The display changed; reopen the layout picker"; Revision++; return; }
            ConfigureMover(Mover, Settings, EffectiveEnabled, MovementPaused);
            try { Mover.TileWindow(invocation.Window, invocation.Monitor, invocation.Bounds, tile); }
            catch (Exception error) { Mover.LastStatus = "Tiling stopped safely: " + error.Message + "; Undo remains when safe"; }
            Revision++;
        }
        void ValidateTilingPicker()
        {
            if (tilingPicker == null) return;
            if (disposed || !EffectiveEnabled || MovementPaused || !TilingDisplayCurrent(tilingInvocation)) {
                CancelTilingPicker(); Mover.LastStatus = "Layout picker closed because window actions or the display changed"; Revision++;
            }
        }
        internal void CancelTilingPicker()
        {
            var picker = tilingPicker; tilingPicker = null; tilingInvocation = null;
            if (picker != null) { if (!picker.IsDisposed) picker.Cancel(); picker.Dispose(); }
        }
    }
}
