using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class WindowTileMinimum
    {
        internal bool Available;
        internal Size Minimum;
        internal int Error;
    }

    internal static class WindowTilingNative
    {
        [StructLayout(LayoutKind.Sequential)] struct MinimumInfo
        { internal Point Reserved, MaximumSize, MaximumPosition, MinimumTrack, MaximumTrack; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr parameter, IntPtr data, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetMenu(IntPtr window);
        [DllImport("user32.dll")] static extern bool AdjustWindowRectExForDpi(ref Native.Rect bounds, uint style, bool menu, uint extendedStyle, uint dpi);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        internal static bool Resizable(IntPtr window) { return (GetWindowLong(window, -16) & 0x00040000) != 0; }
        internal static Size NormalFrameSize(IntPtr window)
        {
            var bounds = new Native.Rect { Right = 100, Bottom = 100 };
            uint dpi = GetDpiForWindow(window);
            return AdjustWindowRectExForDpi(ref bounds, unchecked((uint)(GetWindowLong(window, -16) & ~0x01000000)), GetMenu(window) != IntPtr.Zero,
                unchecked((uint)GetWindowLong(window, -20)), dpi == 0 ? 96 : dpi)
                ? new Size(Math.Max(0, bounds.Right - bounds.Left - 100), Math.Max(0, bounds.Bottom - bounds.Top - 100)) : Size.Empty;
        }
        internal static WindowTileMinimum ReadMinimum(IntPtr window)
        {
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MinimumInfo)));
            try {
                Marshal.StructureToPtr(new MinimumInfo(), memory, false);
                IntPtr result;
                if (SendMessageTimeout(window, 0x0024, IntPtr.Zero, memory, 0x0002 | 0x0020, 80, out result) == IntPtr.Zero)
                    return new WindowTileMinimum { Error = Marshal.GetLastWin32Error() };
                var value = (MinimumInfo)Marshal.PtrToStructure(memory, typeof(MinimumInfo));
                return new WindowTileMinimum { Available = value.MinimumTrack.X >= 0 && value.MinimumTrack.Y >= 0,
                    Minimum = new Size(Math.Max(1, value.MinimumTrack.X), Math.Max(1, value.MinimumTrack.Y)) };
            } finally { Marshal.FreeHGlobal(memory); }
        }
        internal static Rectangle VisibleBounds(IntPtr window, Native.Rect outer)
        {
            Native.Rect visible;
            return Native.DwmGetWindowAttribute(window, 9, out visible, Marshal.SizeOf(typeof(Native.Rect))) == 0 && TaskbarMover.ValidVisibleFrame(outer, visible)
                ? Rectangle.FromLTRB(visible.Left, visible.Top, visible.Right, visible.Bottom)
                : Rectangle.FromLTRB(outer.Left, outer.Top, outer.Right, outer.Bottom);
        }
        internal static Rectangle FitMinimum(Rectangle work, Rectangle wanted, Size minimum, WindowTile tile)
        {
            if (minimum.Width > work.Width || minimum.Height > work.Height) return Rectangle.Empty;
            int width = Math.Max(wanted.Width, minimum.Width), height = Math.Max(wanted.Height, minimum.Height);
            int column = (int)tile % 3, row = (int)tile / 3;
            int x = column == 0 ? wanted.Left : column == 2 ? wanted.Right - width : wanted.Left + (wanted.Width - width) / 2;
            int y = row == 0 ? wanted.Top : row == 2 ? wanted.Bottom - height : wanted.Top + (wanted.Height - height) / 2;
            return new Rectangle(Math.Max(work.Left, Math.Min(x, work.Right - width)), Math.Max(work.Top, Math.Min(y, work.Bottom - height)), width, height);
        }
    }

    internal sealed partial class TaskbarMover
    {
        internal Func<IntPtr, WindowTileMinimum> TileMinimum;
        WindowTileMinimum BoundedTileMinimum(IntPtr window)
        {
            if (TileMinimum != null) return TileMinimum(window);
            EnsureMovementCurrent();
            var result = WindowMoveWorker.Minimum(window, movementBefore.Process, movementBefore.ProcessStarted, movement.Deadline, MovementCurrent);
            if (!AcceptNativeResult(result, false)) return new WindowTileMinimum { Error = result.NativeError };
            return new WindowTileMinimum { Available = true, Minimum = new Size(result.MinimumWidth, result.MinimumHeight) };
        }

        internal bool FocusForNavigation(ShelfWindow selected, IntPtr expectedForeground)
        {
            ShelfWindow current;
            if (selected == null || foregroundWindow() != expectedForeground || WindowTilingNative.IsIconic(selected.Handle) ||
                !WindowShelfCatalog.TryReadWindow(selected.Handle, AllowMovingFullscreenApps, out current) ||
                !WindowShelfPolicy.SameWindow(selected, current) || IsExcluded(selected.Handle)) {
                LastStatus = "Focus target changed or is unavailable"; return false;
            }
            if (!BeginMovement(true, false)) { LastStatus = "A window operation is already in progress"; return false; }
            try {
                var before = MoveApi.Capture(selected.Handle);
                if (before == null || before.Process != selected.Process || before.ProcessStarted != selected.ProcessStarted ||
                    foregroundWindow() != expectedForeground || WindowTilingNative.IsIconic(selected.Handle) ||
                    !WindowShelfCatalog.TryReadWindow(selected.Handle, AllowMovingFullscreenApps, out current) || !WindowShelfPolicy.SameWindow(selected, current)) {
                    LastStatus = "Focus target changed or is unavailable"; return false;
                }
                TrackMovementBefore(before);
                bool focused = FocusWindow(selected.Handle);
                LastStatus = focused ? "Focused the window" : "Windows refused to focus this window";
                return focused;
            } catch (MovementInterruptedException) {
                if (!disposed) LastStatus = movement.Expired ? "Focus timed out; no further request was queued" : "Focus cancelled";
                return false;
            } finally { EndMovement(); }
        }

        internal bool TileWindow(ShelfWindow selected, Native.MonitorInfo expectedMonitor, Native.Rect expectedBounds, WindowTile tile)
        {
            if (!BeginMovement(true, false)) { LastStatus = "A window operation is already in progress"; return false; }
            fullscreenObservation.Stop(); observedFullscreen = null;
            try { return TileWindowCore(selected, expectedMonitor, expectedBounds, tile); }
            catch (MovementInterruptedException) {
                if (!disposed) LastStatus = movement.Expired ? "Tiling timed out; Undo remains when needed" : "Tiling cancelled; Undo remains when needed";
                return false;
            } finally { EndMovement(); }
        }
        bool TileWindowCore(ShelfWindow selected, Native.MonitorInfo expectedMonitor, Native.Rect expectedBounds, WindowTile tile)
        {
            if (!Enum.IsDefined(typeof(WindowTile), tile) || selected == null) { LastStatus = "Choose a supported layout"; return false; }
            IntPtr window = selected.Handle;
            ShelfWindow current;
            bool fullscreen; Native.Rect fullscreenBounds;
            if (!WindowShelfCatalog.TryReadFullscreenMovement(window, out fullscreen, out fullscreenBounds)) {
                LastStatus = "Window bounds are unavailable; restore the app first"; return false;
            }
            if (fullscreen && !AllowMovingFullscreenApps) { LastStatus = "Enable Allow moving fullscreen apps first"; return false; }
            if (WindowTilingNative.IsIconic(window) || !WindowShelfCatalog.TryReadWindow(window, AllowMovingFullscreenApps, out current) ||
                !WindowShelfPolicy.SameWindow(selected, current) || IsExcluded(window)) {
                LastStatus = "Tiling target changed, is excluded, or is unavailable"; return false;
            }
            Native.MonitorInfo source; Native.Rect outer;
            if (!Native.TryMonitor(expectedMonitor.Device, out source) || !ReturnPlacement.SameMonitor(expectedMonitor, source) ||
                !Native.GetWindowRect(window, out outer) || !AreaPolicy.Equal(expectedBounds, outer)) {
                LastStatus = "The window or display changed; reopen the layout picker"; return false;
            }
            var before = MoveApi.Capture(window);
            if (before == null || before.Process != selected.Process || before.ProcessStarted != selected.ProcessStarted ||
                !ReturnPlacement.SameMonitor(before.Monitor, source)) { LastStatus = "The previous position could not be saved"; return false; }
            movementBefore = before; movementSource = movementDestination = source; movementTopology = true;
            EnsureMovementCurrent();
            var work = Rectangle.FromLTRB(source.Work.Left, source.Work.Top, source.Work.Right, source.Work.Bottom);
            var visible = WindowTilingNative.VisibleBounds(window, outer);
            var wanted = WindowTilingPolicy.Target(work, visible, tile);
            if (wanted.IsEmpty) { LastStatus = "No usable space for that layout"; return false; }
            if (!WindowTilingNative.Resizable(window) && !fullscreen && tile != WindowTile.Center) {
                LastStatus = "This app has a fixed window size; choose Center"; return false;
            }
            if (!WindowTilingNative.Resizable(window) && !fullscreen && (visible.Width > work.Width || visible.Height > work.Height)) {
                LastStatus = "This app's fixed size does not fit the monitor; no layout applied"; return false;
            }
            var minimum = BoundedTileMinimum(window);
            EnsureMovementCurrent();
            if (minimum == null || !minimum.Available) {
                LastStatus = "The app did not provide its minimum size" + (minimum != null && minimum.Error != 0 ? " (Windows error " + minimum.Error + ")" : "") + "; no layout applied";
                return false;
            }
            // Minimum tracking size is outer-frame pixels. Normal DWM margins
            // are removed to obtain the corresponding visible minimum.
            int left = outer.Left - visible.Left, top = outer.Top - visible.Top;
            int right = outer.Right - visible.Right, bottom = outer.Bottom - visible.Bottom;
            if (IsZoomed(window) || fullscreen) { left = top = right = bottom = 0; }
            var visibleMinimum = new Size(Math.Max(1, minimum.Minimum.Width + left - right), Math.Max(1, minimum.Minimum.Height + top - bottom));
            if (IsZoomed(window) && !fullscreen) {
                var frame = WindowTilingNative.NormalFrameSize(window);
                visibleMinimum = new Size(Math.Max(1, minimum.Minimum.Width - frame.Width), Math.Max(1, minimum.Minimum.Height - frame.Height));
            }
            wanted = WindowTilingNative.FitMinimum(work, wanted, visibleMinimum, tile);
            if (wanted.IsEmpty) { LastStatus = "The app's minimum size does not fit this monitor; no layout applied"; return false; }
            bool remembered = false;
            if (fullscreen) before.FullscreenBounds = fullscreenBounds;
            if (IsZoomed(window)) {
                var restore = before.Placement; restore.ShowCommand = 4; restore.Flags = 0;
                if (!PlaceWindow(window, ref restore)) { LastStatus = "The app refused to leave maximized mode"; return false; }
                RememberMove(before, ref remembered);
                while (IsZoomed(window)) {
                    EnsureMovementCurrent(); Application.DoEvents();
                    EnsureMovementCurrent(); System.Threading.Thread.Sleep(5);
                }
                // Read normal frame margins only after the maximized window has
                // restored. The saved maximized recovery snapshot stays intact.
                if (!Native.GetWindowRect(window, out outer)) { LastStatus = "The app's restored bounds are unavailable; try Undo"; return false; }
                visible = WindowTilingNative.VisibleBounds(window, outer);
                left = outer.Left - visible.Left; top = outer.Top - visible.Top;
                right = outer.Right - visible.Right; bottom = outer.Bottom - visible.Bottom;
                visibleMinimum = new Size(Math.Max(1, minimum.Minimum.Width + left - right), Math.Max(1, minimum.Minimum.Height + top - bottom));
                wanted = WindowTilingNative.FitMinimum(work, wanted, visibleMinimum, tile);
                if (wanted.IsEmpty) { LastStatus = "The restored minimum size does not fit; try Undo"; return false; }
            }
            var requested = new Rectangle(wanted.Left + left, wanted.Top + top, wanted.Width + right - left, wanted.Height + bottom - top);
            // Repeating the already-applied layout is a no-op, preserving the
            // previous useful Undo entry instead of adding duplicate snapshots.
            if (!remembered && requested.Left == outer.Left && requested.Top == outer.Top && requested.Right == outer.Right && requested.Bottom == outer.Bottom) {
                LastStatus = "The window already uses that layout"; return true;
            }
            bool positioned = PositionWindow(window, IntPtr.Zero, requested.Left, requested.Top, requested.Width, requested.Height, SwpNoActivate | SwpNoOwnerZOrder | SwpNoZOrder);
            if (positioned) RememberMove(before, ref remembered);
            if (!positioned) { LastStatus = "The app refused the layout; Undo remains when safe"; return false; }
            // Observe the result; successful native submission is not proof the
            // app accepted the requested size or remained on this display.
            Native.Rect actual;
            bool applied = Native.GetWindowRect(window, out actual) && actual.Left == requested.Left && actual.Top == requested.Top &&
                actual.Right == requested.Right && actual.Bottom == requested.Bottom;
            if (!applied) { LastStatus = "The app adjusted or refused the layout; Undo is available"; return false; }
            bool focused = FocusWindow(window);
            LastStatus = "Layout applied" + (focused ? "; Undo available" : "; Windows refused focus, Undo available");
            return true;
        }
    }
}
