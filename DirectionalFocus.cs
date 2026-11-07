using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal enum WindowDirection { Left, Right, Up, Down }

    internal sealed class DirectionalFocusWindow
    {
        internal ShelfWindow Identity;
        internal Native.Rect Bounds;
        internal int ZOrder;
        internal bool Visible = true, Enabled = true;
        internal bool Minimized = false, Cloaked = false, ToolWindow = false, Shell = false, Fullscreen = false;
        internal DirectionalFocusWindow Copy()
        {
            var result = (DirectionalFocusWindow)MemberwiseClone();
            if (Identity != null) result.Identity = new ShelfWindow { Handle = Identity.Handle, Process = Identity.Process,
                ProcessStarted = Identity.ProcessStarted, AppKey = Identity.AppKey, AppName = Identity.AppName, Title = Identity.Title };
            return result;
        }
    }

    // Tests supply every boundary. Focus does not restore, resize, minimize or
    // remember a window; activation uses the mover's bounded native path.
    internal sealed class DirectionalFocusApi
    {
        internal Func<IntPtr> Foreground = TaskbarMover.ForegroundWindow;
        internal Func<IntPtr, bool, DirectionalFocusWindow> Read = DirectionalFocusNative.Read;
        internal Func<bool, List<DirectionalFocusWindow>> Windows = DirectionalFocusNative.Windows;
        internal Func<ShelfWindow, IntPtr, bool> Activate;
    }

    internal sealed class DirectionalFocus
    {
        readonly DirectionalFocusApi api;
        readonly Func<IntPtr, bool> ignoreWindow;
        readonly Action<string> status;
        internal DirectionalFocus(TaskbarMover mover, Func<IntPtr, bool> ignoreWindow = null)
            : this(ApiFor(mover), delegate(string value) { mover.LastStatus = value; }, ignoreWindow) { }
        static DirectionalFocusApi ApiFor(TaskbarMover mover)
        {
            if (mover == null) throw new ArgumentNullException("mover");
            return new DirectionalFocusApi { Activate = mover.FocusForNavigation };
        }
        internal DirectionalFocus(DirectionalFocusApi api, Action<string> status = null, Func<IntPtr, bool> ignoreWindow = null)
        {
            if (api == null || api.Foreground == null || api.Read == null || api.Windows == null || api.Activate == null)
                throw new ArgumentNullException("api");
            this.api = api; this.status = status ?? delegate { }; this.ignoreWindow = ignoreWindow ?? delegate { return false; };
        }
        internal bool Focus(IntPtr foreground, WindowDirection direction, List<AppRule> exclusions, bool allowFullscreen)
        {
            if (!Enum.IsDefined(typeof(WindowDirection), direction)) return Refuse("Focus direction is unavailable.");
            if (FocusedWindowPolicy.SpecialHandle(foreground) || api.Foreground() != foreground)
                return Refuse("Focused window changed or is unavailable.");
            var source = Copy(api.Read(foreground, allowFullscreen));
            if (!Eligible(source, exclusions, allowFullscreen) || source.Identity.Handle != foreground)
                return Refuse("This window cannot be used for focus navigation.");
            var read = api.Windows(allowFullscreen);
            if (read == null) return Refuse("Window list changed or timed out; focus was kept.");
            var candidates = read.Select(Copy).Where(w => Eligible(w, exclusions, allowFullscreen)).ToList();
            var selected = DirectionalFocusPolicy.Select(source, candidates, direction);
            if (selected == null) return Refuse("No eligible window to the " + Name(direction) + ".");
            // Native enumeration can race HWND reuse, minimizing, exclusion
            // changes and a user's new foreground. Never substitute a target.
            var sourceNow = Copy(api.Read(foreground, allowFullscreen));
            var selectedNow = Copy(api.Read(selected.Identity.Handle, allowFullscreen));
            if (!Eligible(sourceNow, exclusions, allowFullscreen) || !Eligible(selectedNow, exclusions, allowFullscreen) ||
                !Same(source, sourceNow) || !Same(selected, selectedNow) || api.Foreground() != foreground)
                return Refuse("Window changed; focus was not switched.");
            if (!api.Activate(selectedNow.Identity, foreground)) return Refuse("Windows refused focus or the window changed; focus was not switched.");
            status("Focused " + (String.IsNullOrWhiteSpace(selectedNow.Identity.AppName) ? "window" : selectedNow.Identity.AppName) +
                " to the " + Name(direction) + ".");
            return true;
        }
        bool Eligible(DirectionalFocusWindow window, List<AppRule> exclusions, bool allowFullscreen)
        {
            return window != null && WindowShelfPolicy.SameWindow(window.Identity, window.Identity) &&
                !FocusedWindowPolicy.SpecialHandle(window.Identity.Handle) && DirectionalFocusPolicy.Valid(window.Bounds) &&
                window.Visible && window.Enabled && !window.Minimized && !window.Cloaked && !window.ToolWindow && !window.Shell &&
                (!window.Fullscreen || allowFullscreen) && !ignoreWindow(window.Identity.Handle) &&
                !AppCatalog.Matches((exclusions ?? new List<AppRule>()).Where(r => r != null), window.Identity.AppKey);
        }
        static DirectionalFocusWindow Copy(DirectionalFocusWindow value) { return value == null ? null : value.Copy(); }
        static bool Same(DirectionalFocusWindow a, DirectionalFocusWindow b)
        { return a != null && b != null && WindowShelfPolicy.SameWindow(a.Identity, b.Identity) && AreaPolicy.Equal(a.Bounds, b.Bounds); }
        static string Name(WindowDirection direction) { return direction.ToString().ToLowerInvariant(); }
        bool Refuse(string value) { status(value); return false; }
    }

    internal static class DirectionalFocusPolicy
    {
        internal static bool Valid(Native.Rect bounds) { return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top; }
        internal static DirectionalFocusWindow Select(DirectionalFocusWindow source, IEnumerable<DirectionalFocusWindow> windows, WindowDirection direction)
        {
            if (source == null || source.Identity == null || !Valid(source.Bounds) || windows == null || !Enum.IsDefined(typeof(WindowDirection), direction)) return null;
            DirectionalFocusWindow best = null; bool bestAligned = false; decimal bestDistance = 0;
            foreach (var candidate in windows) {
                if (candidate == null || candidate.Identity == null || candidate.Identity.Handle == source.Identity.Handle || !Valid(candidate.Bounds)) continue;
                // Twice the center avoids rounding odd-sized windows. Decimal
                // squaring is safe even across the full signed coordinate range.
                long dx = (long)candidate.Bounds.Left + candidate.Bounds.Right - source.Bounds.Left - source.Bounds.Right;
                long dy = (long)candidate.Bounds.Top + candidate.Bounds.Bottom - source.Bounds.Top - source.Bounds.Bottom;
                if ((direction == WindowDirection.Left && dx >= 0) || (direction == WindowDirection.Right && dx <= 0) ||
                    (direction == WindowDirection.Up && dy >= 0) || (direction == WindowDirection.Down && dy <= 0)) continue;
                bool horizontal = direction == WindowDirection.Left || direction == WindowDirection.Right;
                bool aligned = horizontal ? candidate.Bounds.Top < source.Bounds.Bottom && candidate.Bounds.Bottom > source.Bounds.Top :
                    candidate.Bounds.Left < source.Bounds.Right && candidate.Bounds.Right > source.Bounds.Left;
                decimal distance = (decimal)dx * dx + (decimal)dy * dy;
                if (best == null || (aligned && !bestAligned) || (aligned == bestAligned && (distance < bestDistance ||
                    (distance == bestDistance && (candidate.ZOrder < best.ZOrder ||
                    (candidate.ZOrder == best.ZOrder && candidate.Identity.Handle.ToInt64() < best.Identity.Handle.ToInt64())))))) {
                    best = candidate; bestAligned = aligned; bestDistance = distance;
                }
            }
            return best;
        }
    }

    internal static class DirectionalFocusNative
    {
        delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        internal static DirectionalFocusWindow Read(IntPtr window, bool allowFullscreen)
        {
            ShelfWindow identity; Native.Rect bounds;
            if (FocusedWindowPolicy.SpecialHandle(window) || IsIconic(window) ||
                !WindowShelfCatalog.TryReadWindow(window, allowFullscreen, out identity) ||
                !Native.GetWindowRect(window, out bounds) || !DirectionalFocusPolicy.Valid(bounds) || IsIconic(window)) return null;
            // The shared catalog already rejects shell/owned/tool/noactivate/
            // cloaked/inaccessible windows and applies the fullscreen classifier.
            return new DirectionalFocusWindow { Identity = identity, Bounds = bounds,
                Fullscreen = WindowShelfCatalog.IsFullscreenForMovement(window) };
        }
        internal static List<DirectionalFocusWindow> Windows(bool allowFullscreen)
        {
            var result = new List<DirectionalFocusWindow>(); var elapsed = Stopwatch.StartNew(); int order = 0; bool stopped = false;
            bool complete = EnumWindows(delegate(IntPtr window, IntPtr parameter) {
                if (elapsed.ElapsedMilliseconds > 500) { stopped = true; return false; }
                var candidate = Read(window, allowFullscreen);
                if (candidate != null) { candidate.ZOrder = order; result.Add(candidate); }
                order++; return true;
            }, IntPtr.Zero);
            return !complete || stopped || elapsed.ElapsedMilliseconds > 500 ? null : result;
        }
    }
}
