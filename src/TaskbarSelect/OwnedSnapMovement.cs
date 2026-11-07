using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal enum OwnedSnapRoute { Ordinary, Administrator }

    internal static class OwnedSnapMovementGeometry
    {
        internal static bool GuardCurrent(Func<bool> guard)
        { try { return guard != null && guard(); } catch (Exception) { return false; } }

        internal static bool Ordinary(NativeProcessIdentity.Info current, NativeProcessIdentity.Info target)
        {
            return current != null && target != null && current.Process != 0 && target.Process != 0 &&
                current.Started > 0 && target.Started > 0 && current.IntegrityAvailable && target.IntegrityAvailable &&
                current.ElevationAvailable && target.ElevationAvailable && !current.Elevated && !target.Elevated &&
                current.IntegrityLevel <= NativeProcessIdentity.Medium && target.IntegrityLevel <= NativeProcessIdentity.Medium &&
                target.IntegrityLevel <= current.IntegrityLevel;
        }

        internal static bool Authorized(NativeProcessIdentity.Info current, NativeProcessIdentity.Info target,
            OwnedSnapRoute route, int expectedGeneration, int currentGeneration, bool uncertain, int uncertainHelpers)
        {
            if (route == OwnedSnapRoute.Ordinary) return Ordinary(current, target);
            return route == OwnedSnapRoute.Administrator && expectedGeneration > 0 && expectedGeneration == currentGeneration &&
                !uncertain && uncertainHelpers == 0 && current != null && target != null &&
                current.Process != 0 && target.Process != 0 && current.Process != target.Process &&
                current.Started > 0 && target.Started > 0 && current.IntegrityAvailable && target.IntegrityAvailable &&
                current.ElevationAvailable && target.ElevationAvailable && !current.Elevated &&
                current.IntegrityLevel == NativeProcessIdentity.Medium && target.Elevated && target.IntegrityLevel == NativeProcessIdentity.High;
        }

        static bool ValidRectangle(Rectangle value)
        {
            return value.Width > 0 && value.Height > 0 && (long)value.X + value.Width <= Int32.MaxValue &&
                (long)value.Y + value.Height <= Int32.MaxValue;
        }

        internal static bool HalfOf(Native.Rect area, Rectangle wanted)
        {
            long width = (long)area.Right - area.Left, height = (long)area.Bottom - area.Top;
            if (!ValidRectangle(wanted) || width < 2 || width > Int32.MaxValue || height <= 0 || height > Int32.MaxValue ||
                wanted.Top != area.Top || wanted.Height != height) return false;
            long leftWidth = width / 2;
            return (wanted.Left == area.Left && wanted.Width == leftWidth) ||
                (wanted.Left == (long)area.Left + leftWidth && wanted.Width == width - leftWidth);
        }

        internal static bool TryRequest(Native.Rect outer, Native.Rect visible, Rectangle wanted, Size minimum, out Rectangle requested)
        {
            requested = Rectangle.Empty;
            if (!TaskbarMover.ValidVisibleFrame(outer, visible) || !ValidRectangle(wanted) || minimum.Width < 1 || minimum.Height < 1) return false;
            long left = (long)wanted.Left + outer.Left - visible.Left;
            long top = (long)wanted.Top + outer.Top - visible.Top;
            long right = (long)wanted.Left + wanted.Width + outer.Right - visible.Right;
            long bottom = (long)wanted.Top + wanted.Height + outer.Bottom - visible.Bottom;
            long width = right - left, height = bottom - top;
            if (left < Int32.MinValue || left > Int32.MaxValue || top < Int32.MinValue || top > Int32.MaxValue ||
                right < Int32.MinValue || right > Int32.MaxValue || bottom < Int32.MinValue || bottom > Int32.MaxValue ||
                width < minimum.Width || height < minimum.Height || width < 1 || height < 1 ||
                width > Int32.MaxValue || height > Int32.MaxValue) return false;
            requested = new Rectangle(checked((int)left), checked((int)top), checked((int)width), checked((int)height));
            return true;
        }
    }

    internal sealed partial class TaskbarMover
    {
        internal bool SnapPrototype(ShelfWindow selected, Native.Rect expectedBounds,
            Native.MonitorInfo expectedSource, Native.MonitorInfo expectedDestination,
            Rectangle wantedVisible, OwnedSnapRoute route, int sessionGeneration, Func<bool> stillCurrent)
        {
            if (selected == null || !OwnedSnapMovementGeometry.GuardCurrent(stillCurrent)) {
                LastStatus = "Experimental snap is paused or its window changed"; return false;
            }
            if (!BeginMovement(true, false)) { LastStatus = "A window operation is already in progress"; return false; }
            ownedMovementGuard = delegate {
                return OwnedSnapMovementGeometry.GuardCurrent(stillCurrent) && foregroundWindow() == selected.Handle &&
                    !IsIconic(selected.Handle) && !IsZoomed(selected.Handle) &&
                    SnapPrototypePermissionCurrent(selected, route, sessionGeneration);
            };
            fullscreenObservation.Stop(); observedFullscreen = null;
            bool applied = false;
            try { applied = SnapPrototypeCore(selected, expectedBounds, expectedSource, expectedDestination, wantedVisible, route, sessionGeneration); }
            catch (MovementInterruptedException) {
                if (!disposed) LastStatus = movement.Expired ? "Experimental snap timed out; Undo remains when needed" :
                    "Experimental snap cancelled; Undo remains when needed";
                return false;
            } finally {
                // Recovery, including a queued Undo, uses the normal movement
                // lifetime after this operation has relinquished its scope.
                ownedMovementGuard = null;
                EndMovement();
            }
            return applied && !movementInterrupted;
        }

        bool SnapPrototypePermissionCurrent(ShelfWindow selected, OwnedSnapRoute route, int sessionGeneration)
        {
            if (selected == null) return false;
            var identity = NativeProcessIdentity.Read(selected.Process);
            var support = WindowMoveWorker.Administrator;
            return identity.Process == selected.Process && identity.Started == selected.ProcessStarted &&
                OwnedSnapMovementGeometry.Authorized(NativeProcessIdentity.Current, identity, route, sessionGeneration,
                    support == null ? 0 : support.SnapInputGeneration, support != null && support.HasUncertainOperation,
                    WindowMoveWorker.UncertainHelpers);
        }

        bool SnapPrototypeWindowCurrent(ShelfWindow selected, OwnedSnapRoute route, int sessionGeneration)
        {
            ShelfWindow current;
            if (selected == null || foregroundWindow() != selected.Handle || IsIconic(selected.Handle) || IsZoomed(selected.Handle) ||
                !WindowTilingNative.Resizable(selected.Handle) || IsExcluded(selected.Handle) ||
                !WindowShelfCatalog.TryReadWindow(selected.Handle, false, out current) || !WindowShelfPolicy.SameWindow(selected, current) ||
                WindowShelfCatalog.IsFullscreenForMovement(selected.Handle)) return false;
            return SnapPrototypePermissionCurrent(selected, route, sessionGeneration);
        }

        static bool SnapPrototypeFrame(IntPtr window, Native.Rect outer, out Native.Rect visible)
        {
            return Native.DwmGetWindowAttribute(window, 9, out visible, Marshal.SizeOf(typeof(Native.Rect))) == 0 &&
                ValidVisibleFrame(outer, visible);
        }

        bool SnapPrototypeCore(ShelfWindow selected, Native.Rect expectedBounds,
            Native.MonitorInfo expectedSource, Native.MonitorInfo expectedDestination, Rectangle wantedVisible,
            OwnedSnapRoute route, int sessionGeneration)
        {
            if (!SnapPrototypeWindowCurrent(selected, route, sessionGeneration)) {
                LastStatus = "The experimental snap target or its approved permission route is unavailable";
                return false;
            }
            Native.MonitorInfo source, destination;
            Native.Rect outer, visible;
            if (!Native.TryMonitor(expectedSource.Device, out source) || !ReturnPlacement.SameMonitor(expectedSource, source) ||
                !Native.TryMonitor(expectedDestination.Device, out destination) || !ReturnPlacement.SameMonitor(expectedDestination, destination) ||
                !Native.GetWindowRect(selected.Handle, out outer) || !AreaPolicy.Equal(outer, expectedBounds) ||
                !SnapPrototypeFrame(selected.Handle, outer, out visible)) {
                LastStatus = "The window, visible frame or display changed; no experimental snap applied"; return false;
            }
            if (!OwnedSnapMovementGeometry.HalfOf(destination.Work, wantedVisible) &&
                !OwnedSnapMovementGeometry.HalfOf(destination.Monitor, wantedVisible)) {
                LastStatus = "The requested display half is unavailable"; return false;
            }
            var before = MoveApi.Capture(selected.Handle);
            if (before == null || !before.IsNormal || before.Window != selected.Handle || before.Process != selected.Process ||
                before.ProcessStarted != selected.ProcessStarted || !ReturnPlacement.SameMonitor(before.Monitor, source)) {
                LastStatus = "The window's previous placement could not be saved"; return false;
            }
            movementBefore = before; movementSource = source; movementDestination = destination; movementTopology = true;
            EnsureMovementCurrent();
            var minimum = BoundedTileMinimum(selected.Handle);
            EnsureMovementCurrent();
            if (minimum == null || !minimum.Available) {
                LastStatus = "The app's minimum size is unavailable; no experimental snap applied"; return false;
            }
            Rectangle requested;
            if (!OwnedSnapMovementGeometry.TryRequest(outer, visible, wantedVisible, minimum.Minimum, out requested)) {
                LastStatus = "The app's minimum size or window frame does not fit the exact display half; no experimental snap applied"; return false;
            }
            // WM_GETMINMAXINFO can pump target messages. Re-prove the capture
            // immediately before the sole geometry mutation.
            Native.Rect currentOuter, currentVisible;
            if (!SnapPrototypeWindowCurrent(selected, route, sessionGeneration) || !Native.GetWindowRect(selected.Handle, out currentOuter) ||
                !AreaPolicy.Equal(currentOuter, expectedBounds) || !SnapPrototypeFrame(selected.Handle, currentOuter, out currentVisible) ||
                !AreaPolicy.Equal(currentVisible, visible)) {
                LastStatus = "The app changed before experimental snap; no layout applied"; return false;
            }
            EnsureMovementCurrent();
            bool unchanged = requested.Left == outer.Left && requested.Top == outer.Top && requested.Right == outer.Right && requested.Bottom == outer.Bottom;
            bool remembered = false;
            if (!unchanged) {
                bool positioned = PositionWindow(selected.Handle, IntPtr.Zero, requested.Left, requested.Top, requested.Width, requested.Height,
                    SwpNoActivate | SwpNoOwnerZOrder | SwpNoZOrder);
                if (positioned) RememberMove(before, ref remembered);
                if (!positioned) { LastStatus = "The app refused experimental snap; Undo remains when safe"; return false; }
            }
            // One submitted position, bounded observation, and no follow-up
            // correction if an app changes its frame (for example across DPI).
            long now = Stopwatch.GetTimestamp(), observationTicks = Math.Max(1, Stopwatch.Frequency / 5);
            long observationEnd = Math.Min(movement.Deadline, now > Int64.MaxValue - observationTicks ? Int64.MaxValue : now + observationTicks);
            do {
                EnsureMovementCurrent();
                if (!SnapPrototypeWindowCurrent(selected, route, sessionGeneration)) { LastStatus = "The window changed after experimental snap; Undo remains when safe"; return false; }
                var actualMonitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                Native.Rect actual, frame;
                bool applied = Native.GetWindowRect(selected.Handle, out actual) && actual.Left == requested.Left && actual.Top == requested.Top &&
                    actual.Right == requested.Right && actual.Bottom == requested.Bottom && SnapPrototypeFrame(selected.Handle, actual, out frame) &&
                    frame.Left == wantedVisible.Left && frame.Top == wantedVisible.Top && frame.Right == wantedVisible.Right && frame.Bottom == wantedVisible.Bottom &&
                    Native.GetMonitorInfo(Native.MonitorFromWindow(selected.Handle, MonitorDefaultNearest), ref actualMonitor) &&
                    ReturnPlacement.SameMonitor(destination, actualMonitor);
                EnsureMovementCurrent();
                if (applied) { LastStatus = unchanged ? "The window already uses that experimental half" : "Experimental half applied; Undo available"; return true; }
                if (Stopwatch.GetTimestamp() >= observationEnd) break;
                Application.DoEvents(); EnsureMovementCurrent(); Thread.Sleep(5);
            } while (Stopwatch.GetTimestamp() < observationEnd);
            LastStatus = "The app adjusted or refused the exact display half; Undo remains when safe";
            return false;
        }
    }
}
