using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class FocusIndicatorTarget
    {
        internal FocusedWindowSnapshot Identity;
        internal Rectangle Frame;
        internal Rectangle[] Monitors;
        internal int Dpi = 96, Corner;
        internal bool Minimized, Cloaked, Fullscreen, Menu, Moving, ShellSurface;
        internal FocusIndicatorTarget Copy()
        {
            var copy = (FocusIndicatorTarget)MemberwiseClone();
            copy.Identity = Identity == null ? null : Identity.Copy();
            copy.Monitors = Monitors == null ? null : (Rectangle[])Monitors.Clone();
            return copy;
        }
    }

    internal sealed class FocusIndicatorVisual
    {
        internal FocusIndicatorTarget Target;
        internal Rectangle Bounds;
        internal int Thickness, OuterThickness, GlowWidth;
        internal FocusIndicatorMode Mode;
        internal FocusLightingMode Lighting;
        internal FocusFrameFinish Finish;
        internal bool Dark;
        internal bool PinRail, PinRailFocused, PinCorner, ReservedPinCorner;
        internal int PinRailInset, PinCornerLength;
        internal Color AccentColor;
        internal Color Outer, Inner, GlowColor;
        internal static FocusIndicatorVisual Create(FocusIndicatorTarget target, FocusIndicatorMode mode, string accent, bool dark, bool glow = false)
        { return Create(target, mode, accent, dark, glow, -1); }
        internal static FocusIndicatorVisual Create(FocusIndicatorTarget target, FocusIndicatorMode mode, string accent, bool dark, bool glow, int lighting)
        { return Create(target, mode, accent, dark, glow, lighting, 0); }
        internal static FocusIndicatorVisual Create(FocusIndicatorTarget target, FocusIndicatorMode mode, string accent, bool dark, bool glow, int lighting, int finish, bool reservePinCorner = false)
        {
            var visual = CreateCore(target, mode, accent, dark, FocusAppearance.ResolveLighting(lighting, glow), FocusAppearance.NormalizeFinish(finish), true);
            if (visual != null) visual.ReservedPinCorner = reservePinCorner;
            return visual;
        }
        internal static FocusIndicatorVisual CreatePinRail(FocusIndicatorTarget target, bool focused, bool dark, FocusIndicatorVisual focusVisual = null)
        {
            var visual = CreateCore(target, FocusIndicatorMode.InsetEdge, "Forest Green", dark,
                FocusLightingMode.None, FocusFrameFinish.MutedEdge, false);
            if (visual == null) return null;
            int inset = focused && focusVisual != null && !focusVisual.PinRail &&
                focusVisual.Target != null && FocusedWindowPolicy.SameWindow(target.Identity, focusVisual.Target.Identity) &&
                target.Frame == focusVisual.Target.Frame && target.Dpi == focusVisual.Target.Dpi && target.Corner == focusVisual.Target.Corner
                ? focusVisual.Thickness : 0;
            if (inset < 0 || inset > 32 || (long)(inset + visual.Thickness) * 2 >= Math.Min(target.Frame.Width, target.Frame.Height)) return null;
            int corner = Math.Max(0, Math.Min(target.Corner, Math.Min(target.Frame.Width, target.Frame.Height) / 2));
            int depth = Math.Max(corner, inset + visual.Thickness);
            var painting = new Rectangle(target.Frame.X, target.Frame.Y, target.Frame.Width, depth);
            Rectangle bounds = Rectangle.Empty;
            foreach (Rectangle monitor in target.Monitors) {
                Rectangle clipped = Rectangle.Intersect(painting, monitor);
                if (clipped.Width > 0 && clipped.Height > 0) bounds = bounds.IsEmpty ? clipped : Rectangle.Union(bounds, clipped);
            }
            if (!Valid(bounds) || bounds.Width > 16384 || bounds.Height > 16384 || (long)bounds.Width * bounds.Height > 16777216) return null;
            visual.Bounds = bounds; visual.PinRail = true; visual.PinRailFocused = focused; visual.PinRailInset = inset;
            visual.Outer = dark ? Color.FromArgb(188, 193, 187) : Color.FromArgb(94, 104, 98);
            visual.Inner = dark ? Color.FromArgb(141, 149, 143) : Color.FromArgb(139, 147, 140);
            visual.AccentColor = Color.Empty;
            return visual;
        }
        internal static FocusIndicatorVisual CreatePinCorner(FocusIndicatorTarget target, bool focused, bool dark, string accent, FocusIndicatorVisual focusVisual = null)
        {
            var visual = CreateCore(target, FocusIndicatorMode.InsetEdge, accent, dark,
                FocusLightingMode.None, FocusFrameFinish.MutedEdge, false);
            if (visual == null || target.Identity == null || target.Monitors.Length > 128) return null;
            int dpi = Math.Max(96, Math.Min(768, target.Dpi));
            visual.Thickness = Math.Max(3, (3 * dpi + 48) / 96);
            int margin = Math.Max(2, (3 * dpi + 48) / 96);
            int length = Math.Min((40 * dpi + 48) / 96, Math.Min(target.Frame.Width, target.Frame.Height) / 2);
            int corner = Math.Max(0, Math.Min(target.Corner, Math.Min(target.Frame.Width, target.Frame.Height) / 2));
            // Native Windows corners fit comfortably within this local cue.
            // Refuse an exceptionally tight or overly rounded target rather
            // than illuminate a second corner or allocate a broad client area.
            if (length <= corner + visual.Thickness || length <= visual.Thickness + margin ||
                Math.Min(target.Frame.Width, target.Frame.Height) <= (visual.Thickness + margin + 1) * 2) return null;
            Rectangle painting = Expand(new Rectangle(target.Frame.X, target.Frame.Y, length, length), margin);
            Rectangle envelope = Expand(target.Frame, margin);
            if (!Valid(painting) || !Valid(envelope)) return null;
            Rectangle bounds = Rectangle.Empty;
            foreach (Rectangle monitor in target.Monitors) {
                Rectangle clipped = Rectangle.Intersect(painting, monitor);
                if (clipped.Width > 0 && clipped.Height > 0) bounds = bounds.IsEmpty ? clipped : Rectangle.Union(bounds, clipped);
            }
            if (!Valid(bounds) || bounds.Width > 16384 || bounds.Height > 16384 || (long)bounds.Width * bounds.Height > 16777216) return null;
            visual.Bounds = bounds; visual.PinRail = visual.PinCorner = true; visual.PinRailFocused = focused;
            visual.PinRailInset = 0; visual.PinCornerLength = length; visual.GlowWidth = margin;
            visual.AccentColor = FocusAppearance.Accent(accent, dark);
            visual.Outer = visual.Inner = Color.FromArgb(246, 251, 247);
            visual.GlowColor = Color.FromArgb(dark ? 94 : 112, visual.AccentColor);
            return visual;
        }
        internal FocusIndicatorVisual Copy()
        { var copy = (FocusIndicatorVisual)MemberwiseClone(); copy.Target = Target == null ? null : Target.Copy(); return copy; }
        static FocusIndicatorVisual CreateCore(FocusIndicatorTarget target, FocusIndicatorMode mode, string accent, bool dark, FocusLightingMode lighting, FocusFrameFinish finish, bool padding)
        {
            if (target == null || target.ShellSurface || !Valid(target.Frame) || target.Monitors == null || target.Monitors.Length == 0 ||
                (mode != FocusIndicatorMode.MutedFrame && mode != FocusIndicatorMode.InsetEdge)) return null;
            int dpi = Math.Max(96, Math.Min(768, target.Dpi));
            bool accentOutline = finish == FocusFrameFinish.AccentOutline;
            bool premium = lighting == FocusLightingMode.Illuminated || lighting == FocusLightingMode.EnergyFlow;
            if (!padding && !premium) lighting = FocusLightingMode.None;
            bool glow = lighting != FocusLightingMode.None;
            // Soft glow is deliberately visible beyond the two-pixel frame,
            // while keeping its optional upload margin bounded at extreme DPI.
            int glowWidth = glow && padding ? (premium ? (accentOutline ? Math.Min(48, (9 * dpi + 48) / 96) : (6 * dpi + 48) / 96) : Math.Min(32, (7 * dpi + 48) / 96)) : 0;
            Rectangle painting = Expand(target.Frame, glowWidth);
            // An optional halo must not suppress a usable normal frame when
            // its extra pixels exceed safe geometry/allocation bounds.
            if (!Valid(painting)) return glow && padding ? CreateCore(target, mode, accent, dark, lighting, finish, false) : null;
            Rectangle bounds = Rectangle.Empty;
            foreach (Rectangle monitor in target.Monitors) {
                if (!Valid(monitor)) return null;
                Rectangle clipped = Rectangle.Intersect(painting, monitor);
                if (clipped.Width > 0 && clipped.Height > 0) bounds = bounds.IsEmpty ? clipped : Rectangle.Union(bounds, clipped);
            }
            // The cached bitmap and temporary DIB each need four bytes per
            // pixel. Bound allocation even for giant multi-monitor windows.
            if (!Valid(bounds) || bounds.Width > 16384 || bounds.Height > 16384 || (long)bounds.Width * bounds.Height > 16777216)
                return glow && padding ? CreateCore(target, mode, accent, dark, lighting, finish, false) : null;
            int outer = Math.Max(1, (dpi + 48) / 96), thickness = accentOutline ? outer : Math.Max(2, (2 * dpi + 48) / 96);
            if (target.Frame.Width <= thickness * 2 || target.Frame.Height <= thickness * 2) return null;
            return new FocusIndicatorVisual { Target = target.Copy(), Bounds = bounds, Thickness = thickness,
                OuterThickness = outer, Mode = mode, GlowWidth = glowWidth, Lighting = lighting, Finish = finish, Dark = dark,
                AccentColor = FocusAppearance.Accent(accent, dark),
                GlowColor = glow && glowWidth > 0 ? Color.FromArgb(premium ? (accentOutline ? (dark ? 132 : 108) : (dark ? 96 : 68)) : (dark ? 90 : 72), FocusAppearance.Accent(accent, dark)) : Color.Empty,
                Outer = accentOutline ? FocusAppearance.Accent(accent, dark) : mode == FocusIndicatorMode.InsetEdge ? FocusAppearance.Neutral(dark) : FocusAppearance.Frame(accent, dark),
                Inner = accentOutline ? FocusAppearance.Accent(accent, dark) : mode == FocusIndicatorMode.InsetEdge ? FocusAppearance.Inset(accent, dark) : FocusAppearance.Frame(accent, dark) };
        }
        internal static bool Valid(Rectangle rectangle)
        {
            return rectangle.Width > 0 && rectangle.Height > 0 && (long)rectangle.X + rectangle.Width <= Int32.MaxValue &&
                (long)rectangle.Y + rectangle.Height <= Int32.MaxValue;
        }
        internal static Rectangle Expand(Rectangle frame, int margin)
        {
            if (!Valid(frame) || margin < 0 || margin > 48) return Rectangle.Empty;
            long left = (long)frame.Left - margin, top = (long)frame.Top - margin;
            long right = (long)frame.X + frame.Width + margin, bottom = (long)frame.Y + frame.Height + margin;
            if (left < Int32.MinValue || top < Int32.MinValue || right > Int32.MaxValue || bottom > Int32.MaxValue ||
                right - left > Int32.MaxValue || bottom - top > Int32.MaxValue) return Rectangle.Empty;
            return new Rectangle((int)left, (int)top, (int)(right - left), (int)(bottom - top));
        }
        internal bool SameAppearance(FocusIndicatorVisual other)
        {
            if (other == null || !FocusedWindowPolicy.SameWindow(Target.Identity, other.Target.Identity) ||
                Bounds != other.Bounds || Target.Frame != other.Target.Frame || Target.Dpi != other.Target.Dpi ||
                Target.Corner != other.Target.Corner || Thickness != other.Thickness || OuterThickness != other.OuterThickness ||
                PinRail != other.PinRail || PinRailFocused != other.PinRailFocused || PinRailInset != other.PinRailInset ||
                PinCorner != other.PinCorner || PinCornerLength != other.PinCornerLength || ReservedPinCorner != other.ReservedPinCorner ||
                Mode != other.Mode || Lighting != other.Lighting || Finish != other.Finish || Dark != other.Dark || AccentColor != other.AccentColor ||
                Outer != other.Outer || Inner != other.Inner || GlowWidth != other.GlowWidth || GlowColor != other.GlowColor ||
                Target.Monitors.Length != other.Target.Monitors.Length) return false;
            for (int i = 0; i < Target.Monitors.Length; i++) if (Target.Monitors[i] != other.Target.Monitors[i]) return false;
            return true;
        }
        internal void Paint(Graphics graphics)
        {
            // Solid, region-clipped pixels keep rounded edges restrained and
            // preserve an exactly transparent interior in the alpha image.
            graphics.SmoothingMode = SmoothingMode.None;
            using (var ring = Ring(0, Thickness))
            using (var brush = new SolidBrush(Outer)) graphics.FillRegion(brush, ring);
            if (Mode == FocusIndicatorMode.InsetEdge)
                using (var ring = Ring(OuterThickness, Thickness - OuterThickness))
                using (var brush = new SolidBrush(Inner)) graphics.FillRegion(brush, ring);
        }
        // Geometry is relative to the overlay, but rings retain the original
        // window edges. Clipping at a monitor seam must not invent an edge.
        internal Region Ring(int inset, int width)
        {
            Rectangle frame = Target.Frame; frame.Offset(-Bounds.X, -Bounds.Y);
            using (var outside = Rounded(frame, Math.Max(0, Target.Corner))) {
                var result = new Region(outside);
                if (inset > 0) {
                    result.Dispose();
                    using (var path = Rounded(Inset(frame, inset), Math.Max(0, Target.Corner - inset))) result = new Region(path);
                }
                using (var inside = Rounded(Inset(frame, inset + width), Math.Max(0, Target.Corner - inset - width))) result.Exclude(inside);
                using (var monitors = new Region()) {
                    monitors.MakeEmpty();
                    foreach (Rectangle monitor in Target.Monitors) {
                        Rectangle clip = Rectangle.Intersect(monitor, Bounds); clip.Offset(-Bounds.X, -Bounds.Y);
                        if (clip.Width > 0 && clip.Height > 0) monitors.Union(clip);
                    }
                    result.Intersect(monitors);
                }
                return result;
            }
        }
        static Rectangle Inset(Rectangle value, int pixels)
        { return new Rectangle(value.X + pixels, value.Y + pixels, Math.Max(0, value.Width - pixels * 2), Math.Max(0, value.Height - pixels * 2)); }
        static GraphicsPath Rounded(Rectangle value, int radius)
        {
            var path = new GraphicsPath();
            if (value.Width <= 0 || value.Height <= 0) return path;
            radius = Math.Min(radius, Math.Min(value.Width, value.Height) / 2);
            if (radius <= 0) { path.AddRectangle(value); return path; }
            int diameter = radius * 2;
            path.AddArc(value.Left, value.Top, diameter, diameter, 180, 90);
            path.AddArc(value.Right - diameter, value.Top, diameter, diameter, 270, 90);
            path.AddArc(value.Right - diameter, value.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(value.Left, value.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
    }

    internal interface IFocusIndicatorSurface : IDisposable
    {
        IntPtr Window { get; }
        void Present(FocusIndicatorVisual visual);
    }
    internal enum FocusIndicatorChangeKind { Location, MoveStart, MoveEnd, Hidden, Destroyed }
    internal sealed class FocusIndicatorChange
    {
        internal IntPtr Window;
        internal FocusIndicatorChangeKind Kind;
        internal int Object, Child;
    }
    internal interface IFocusIndicatorEvents : IDisposable { }
    internal sealed class FocusIndicatorApi
    {
        internal Func<IntPtr> Foreground = FocusedWindowNative.Foreground;
        internal Func<IntPtr, FocusIndicatorTarget> Read = FocusIndicatorNative.Read;
        internal Func<bool> DesktopAllowed = FocusIndicatorNative.DesktopAllowed;
        internal Func<bool> Dark = FocusIndicatorNative.Dark;
        internal Func<bool> GlowAllowed = FocusIndicatorNative.GlowAllowed;
        internal Func<long> Now = delegate { return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency); };
        internal Func<bool> PointerButtonDown = FocusIndicatorNative.PointerButtonDown;
        internal Func<FocusedWindowSnapshot, Action<FocusIndicatorChange>, IFocusIndicatorEvents> Watch =
            delegate(FocusedWindowSnapshot target, Action<FocusIndicatorChange> changed) { return new FocusIndicatorNativeEvents(target, changed); };
    }

    // Read-only observation of the target. Every mutation belongs to our own
    // click-through surface; no target style, activation or Undo state changes.
    internal sealed class FocusIndicator : IDisposable
    {
        readonly FocusIndicatorApi api;
        readonly Func<IFocusIndicatorSurface> create;
        readonly Func<IntPtr, bool> ignore;
        IFocusIndicatorSurface surface;
        IFocusIndicatorEvents events;
        FocusedWindowSnapshot observed;
        FocusIndicatorTarget lastGeometry, settling;
        FocusIndicatorVisual currentVisual;
        int generation, eventRevision;
        long quietSince;
        bool geometryPending, movingSignal, ownMoveSize;
        bool disposed;
        internal FocusIndicator(Func<IntPtr, bool> ignoreWindow = null)
            : this(new FocusIndicatorApi(), delegate { return new FocusIndicatorSurface(); }, ignoreWindow)
        { create = delegate { return new FocusIndicatorSurface(stopped: SurfaceStopped); }; }
        internal FocusIndicator(FocusIndicatorApi api, Func<IFocusIndicatorSurface> create, Func<IntPtr, bool> ignoreWindow = null)
        {
            if (api == null || api.Foreground == null || api.Read == null || api.DesktopAllowed == null || api.Dark == null || api.GlowAllowed == null ||
                api.Now == null || api.PointerButtonDown == null || api.Watch == null) throw new ArgumentNullException("api");
            if (create == null) throw new ArgumentNullException("create");
            this.api = api; this.create = create; ignore = ignoreWindow ?? delegate { return false; };
        }
        internal IntPtr Handle { get { return surface == null ? IntPtr.Zero : surface.Window; } }
        internal bool Visible { get { return surface != null; } }
        internal FocusIndicatorVisual CurrentVisual { get { return currentVisual == null ? null : currentVisual.Copy(); } }
        internal string Error { get; private set; }
        void SurfaceStopped(IFocusIndicatorSurface source, Exception failure)
        {
            // Late callbacks from a replaced surface cannot affect its successor.
            if (disposed || !Object.ReferenceEquals(surface, source)) return;
            if (failure is FocusIndicatorGeometryChangedException) { GeometryChanged(); return; }
            Hide();
            if (failure != null) Error = "Focus indicator animation unavailable; window controls remain active.";
        }
        internal void Update(int style, string accent, bool paused, bool glow = false, int lighting = -1, int finish = 0,
            Func<FocusedWindowSnapshot, bool> reservePinCorner = null)
        {
            if (disposed) return;
            Error = null;
            FocusIndicatorMode mode = FocusAppearance.NormalizeStyle(style);
            if (paused || mode == FocusIndicatorMode.Off) { Hide(); return; }
            try {
                if (!api.DesktopAllowed()) { Hide(); return; }
                IntPtr foreground = api.Foreground();
                if (FocusedWindowPolicy.SpecialHandle(foreground) || foreground == Handle || ignore(foreground)) { Hide(); return; }
                int readGeneration = generation;
                FocusIndicatorTarget first = Copy(api.Read(foreground));
                if (!Eligible(first, foreground)) { Hide(); return; }
                if (generation != readGeneration) { HideSurface(); return; }
                if (!ObserveTarget(first.Identity)) { HideSurface(); return; }
                int revision = eventRevision, observingGeneration = generation;
                FocusIndicatorTarget current = Copy(api.Read(foreground));
                if (!Eligible(current, foreground) || !FocusedWindowPolicy.SameWindow(first.Identity, current.Identity) ||
                    api.Foreground() != foreground || !api.DesktopAllowed()) { Hide(); return; }
                if (eventRevision != revision || generation != observingGeneration) { HideSurface(); return; }
                if (!SameGeometry(first, current) || (!geometryPending && lastGeometry != null && !SameGeometry(lastGeometry, current)))
                    GeometryChanged();
                if (current.Moving || ownMoveSize) {
                    geometryPending = true; settling = null; quietSince = api.Now(); HideSurface(); return;
                }
                // An early/stale asynchronous END cannot override a live drag.
                // Conversely a lost END can recover once native move state,
                // pointer buttons and the physical frame have all settled.
                if (movingSignal && api.PointerButtonDown()) { HideSurface(); return; }
                if (geometryPending) {
                    long now = api.Now();
                    if (settling == null || !SameGeometry(settling, current)) {
                        settling = current.Copy(); quietSince = now; HideSurface(); return;
                    }
                    if (now < quietSince || now - quietSince < 120) { HideSurface(); return; }
                    geometryPending = movingSignal = false; settling = null;
                }
                FocusLightingMode resolved = FocusAppearance.ResolveLighting(lighting, glow);
                if (resolved != FocusLightingMode.None && !api.GlowAllowed()) resolved = FocusLightingMode.None;
                bool reserved = reservePinCorner != null && reservePinCorner(current.Identity.Copy());
                var visual = FocusIndicatorVisual.Create(current, mode, FocusAppearance.NormalizeAccent(accent), api.Dark(), false, (int)resolved, finish, reserved);
                if (visual == null) { Hide(); return; }
                if (surface == null) {
                    var added = create();
                    if (generation != observingGeneration || eventRevision != revision) {
                        if (added != null) added.Dispose(); HideSurface(); return;
                    }
                    surface = added;
                }
                if (surface == null) throw new InvalidOperationException("Focus indicator surface unavailable.");
                if (reservePinCorner != null && reservePinCorner(current.Identity.Copy()) != reserved) { HideSurface(); return; }
                surface.Present(visual);
                // Showing our nonactivating window can race another app taking
                // focus. A new foreground never inherits the previous outline.
                if (api.Foreground() != foreground || !api.DesktopAllowed()) Hide();
                else if (eventRevision != revision || generation != observingGeneration) HideSurface();
                else if (reservePinCorner != null && reservePinCorner(current.Identity.Copy()) != reserved) HideSurface();
                else { lastGeometry = current.Copy(); currentVisual = visual.Copy(); }
            } catch (FocusIndicatorGeometryChangedException) {
                GeometryChanged();
            } catch (Exception failure) {
                Hide();
                var native = failure as Win32Exception;
                Error = native == null ? "Focus indicator could not be updated safely (" + failure.GetType().Name + ")." :
                    "Focus indicator could not be updated safely (Windows error " + native.NativeErrorCode + ").";
            }
        }
        bool Eligible(FocusIndicatorTarget value, IntPtr foreground)
        {
            return value != null && value.Identity != null && value.Identity.Window == foreground &&
                FocusedWindowPolicy.Eligible(value.Identity, ignore) && (value.Identity.ExtendedStyle & 0x80) == 0 &&
                !value.Minimized && !value.Cloaked && !value.Fullscreen && !value.Menu && !value.ShellSurface &&
                !String.Equals(value.Identity.ClassName, "#32768", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(value.Identity.ClassName, "tooltips_class32", StringComparison.OrdinalIgnoreCase);
        }
        static FocusIndicatorTarget Copy(FocusIndicatorTarget value) { return value == null ? null : value.Copy(); }
        static bool SameGeometry(FocusIndicatorTarget a, FocusIndicatorTarget b)
        {
            if (a == null || b == null || !FocusedWindowPolicy.SameWindow(a.Identity, b.Identity) || a.Frame != b.Frame ||
                a.Dpi != b.Dpi || a.Corner != b.Corner || a.Monitors == null || b.Monitors == null || a.Monitors.Length != b.Monitors.Length) return false;
            for (int i = 0; i < a.Monitors.Length; i++) if (a.Monitors[i] != b.Monitors[i]) return false;
            return true;
        }
        bool ObserveTarget(FocusedWindowSnapshot target)
        {
            if (events != null && FocusedWindowPolicy.SameWindow(observed, target)) return true;
            StopObserving(); HideSurface(); observed = target.Copy(); int token = generation;
            var added = api.Watch(observed.Copy(), delegate(FocusIndicatorChange change) { Changed(token, change); });
            if (added == null) throw new InvalidOperationException("Focus indicator geometry events unavailable.");
            if (disposed || token != generation || !FocusedWindowPolicy.SameWindow(observed, target)) {
                added.Dispose(); return false;
            }
            events = added; return true;
        }
        void Changed(int token, FocusIndicatorChange change)
        {
            if (disposed || token != generation || observed == null || change == null || change.Window != observed.Window ||
                change.Child != 0 || (change.Object != 0 && !(change.Kind == FocusIndicatorChangeKind.Location && change.Object == -4))) return;
            try {
                if (change.Kind == FocusIndicatorChangeKind.Hidden || change.Kind == FocusIndicatorChangeKind.Destroyed) { Hide(); return; }
                if (change.Kind == FocusIndicatorChangeKind.MoveStart) movingSignal = true;
                if (change.Kind == FocusIndicatorChangeKind.MoveEnd) movingSignal = false;
                GeometryChanged();
            } catch { Hide(); Error = "Focus indicator geometry observation failed safely."; }
        }
        internal void ObserveMoveSize(IntPtr window, bool moving)
        {
            if (disposed || observed == null || observed.Window != window) return;
            ownMoveSize = moving;
            GeometryChanged();
        }
        void GeometryChanged()
        {
            eventRevision++; geometryPending = true; settling = null; quietSince = api.Now(); HideSurface();
        }
        void StopObserving()
        {
            generation++; eventRevision++;
            var previous = events; events = null; observed = null;
            lastGeometry = settling = null; geometryPending = movingSignal = ownMoveSize = false;
            if (previous != null) try { previous.Dispose(); } catch { }
        }
        void HideSurface()
        {
            currentVisual = null;
            var previous = surface; surface = null;
            if (previous != null) try { previous.Dispose(); } catch { }
        }
        internal void Hide()
        {
            Error = null;
            StopObserving(); HideSurface();
        }
        public void Dispose() { if (disposed) return; disposed = true; Hide(); }
    }

    internal static class FocusIndicatorNative
    {
        [StructLayout(LayoutKind.Sequential)] struct GuiInfo
        {
            internal int Size; internal uint Flags;
            internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            internal Native.Rect CaretRect;
        }
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int size, out int needed);
        static long themeChecked;
        static bool cachedDark;
        internal static bool GlowAllowed()
        {
            // Decorative transparency yields to accessibility settings. This
            // never changes Windows preferences or the persisted glow choice.
            try { return !SystemInformation.HighContrast; } catch { return false; }
        }
        internal static bool AnimationsAllowed()
        {
            int enabled;
            return !SystemInformation.HighContrast && SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0;
        }
        internal static int CornerRadius(int dpi, int preference, bool maximized, bool arranged)
        {
            if (maximized || arranged || preference == 1) return 0;
            // DWM exposes a preference, not an exact radius for custom frames.
            return (Math.Max(96, Math.Min(768, dpi)) * (preference == 3 ? 4 : 8) + 48) / 96;
        }
        internal static bool Dark()
        {
            long now = Environment.TickCount & Int32.MaxValue;
            if (themeChecked != 0 && now >= themeChecked && now - themeChecked < 1000) return cachedDark;
            themeChecked = now;
            try { using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                cachedDark = key != null && Object.Equals(key.GetValue("AppsUseLightTheme"), 0); }
            catch { cachedDark = false; }
            return cachedDark;
        }
        internal static bool DesktopAllowed()
        {
            IntPtr input = OpenInputDesktop(0, false, 1);
            if (input == IntPtr.Zero) return false;
            try {
                string current = DesktopName(GetThreadDesktop(GetCurrentThreadId())), active = DesktopName(input);
                return !String.IsNullOrEmpty(current) && String.Equals(current, active, StringComparison.Ordinal);
            } finally { CloseDesktop(input); }
        }
        internal static bool PointerButtonDown()
        { return (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0 || (GetAsyncKeyState(4) & 0x8000) != 0; }
        internal static bool Moving(uint flags, IntPtr moveSize)
        { return (flags & 2) != 0 || moveSize != IntPtr.Zero; }
        internal static bool MatchesFrame(FocusIndicatorTarget target)
        {
            if (target == null || target.Identity == null || target.ShellSurface) return false;
            Native.Rect frame;
            int cloaked;
            uint dpi = GetDpiForWindow(target.Identity.Window);
            if (Native.DwmGetWindowAttribute(target.Identity.Window, 9, out frame, Marshal.SizeOf(typeof(Native.Rect))) != 0 ||
                frame.Left != target.Frame.Left || frame.Top != target.Frame.Top || frame.Right != target.Frame.Right || frame.Bottom != target.Frame.Bottom ||
                dpi == 0 || Math.Max(96, Math.Min(768, dpi)) != target.Dpi ||
                DwmGetWindowAttribute(target.Identity.Window, 14, out cloaked, sizeof(int)) != 0 || cloaked != 0 ||
                WindowShelfCatalog.IsFullscreenForMovement(target.Identity.Window)) return false;
            var gui = new GuiInfo { Size = Marshal.SizeOf(typeof(GuiInfo)) };
            return GetGUIThreadInfo(target.Identity.Thread, ref gui) && !Moving(gui.Flags, gui.MoveSize) && (gui.Flags & (4 | 8 | 16)) == 0;
        }
        static string DesktopName(IntPtr handle)
        {
            var value = new StringBuilder(256); int needed;
            return handle != IntPtr.Zero && GetUserObjectInformation(handle, 2, value, value.Capacity * 2, out needed) ? value.ToString() : null;
        }
        internal static bool IsShellSurface(string processImage, string windowsRoot)
        {
            if (String.IsNullOrEmpty(processImage) || String.IsNullOrEmpty(windowsRoot) ||
                !System.IO.Path.IsPathRooted(windowsRoot)) return false;
            // Start and Search use ordinary XAML/CoreWindow classes, shared by
            // real apps. Exclude their exact Windows host images, not that class
            // or explorer.exe (which also owns normal File Explorer windows).
            return String.Equals(processImage, System.IO.Path.Combine(windowsRoot,
                @"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe"), StringComparison.OrdinalIgnoreCase) ||
                String.Equals(processImage, System.IO.Path.Combine(windowsRoot,
                @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe"), StringComparison.OrdinalIgnoreCase);
        }
        internal static FocusIndicatorTarget Read(IntPtr window)
        {
            var identity = FocusedWindowNative.Read(window, true);
            if (identity == null || identity.Error != 0 || String.IsNullOrEmpty(identity.ProcessImage) || IsIconic(window)) return null;
            if (IsShellSurface(identity.ProcessImage, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
                return new FocusIndicatorTarget { Identity = identity, ShellSurface = true };
            int cloaked; Native.Rect bounds;
            if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) != 0 || cloaked != 0 ||
                Native.DwmGetWindowAttribute(window, 9, out bounds, Marshal.SizeOf(typeof(Native.Rect))) != 0) return null;
            long width = (long)bounds.Right - bounds.Left, height = (long)bounds.Bottom - bounds.Top;
            if (width <= 0 || width > Int32.MaxValue || height <= 0 || height > Int32.MaxValue) return null;
            var gui = new GuiInfo { Size = Marshal.SizeOf(typeof(GuiInfo)) };
            if (!GetGUIThreadInfo(identity.Thread, ref gui)) return null;
            uint dpi = GetDpiForWindow(window); if (dpi == 0) return null;
            var monitors = new List<Rectangle>();
            bool complete = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                long w = (long)rect.Right - rect.Left, h = (long)rect.Bottom - rect.Top;
                if (w <= 0 || w > Int32.MaxValue || h <= 0 || h > Int32.MaxValue) return false;
                monitors.Add(new Rectangle(rect.Left, rect.Top, (int)w, (int)h)); return monitors.Count < 128;
            }, IntPtr.Zero);
            if (!complete || monitors.Count == 0 || IsIconic(window)) return null;
            int cornerPreference; int corner = 0;
            if (DwmGetWindowAttribute(window, 33, out cornerPreference, sizeof(int)) == 0)
                corner = CornerRadius((int)dpi, cornerPreference, IsZoomed(window), TaskbarMover.Arranged(window));
            return new FocusIndicatorTarget { Identity = identity, Frame = new Rectangle(bounds.Left, bounds.Top, (int)width, (int)height),
                Monitors = monitors.ToArray(), Dpi = (int)Math.Min(768, dpi), Corner = corner,
                Menu = (gui.Flags & (0x04 | 0x08 | 0x10)) != 0, Moving = Moving(gui.Flags, gui.MoveSize),
                Fullscreen = WindowShelfCatalog.IsFullscreenForMovement(window) };
        }
    }

    internal sealed class FocusIndicatorGeometryChangedException : Exception { }

    // OUTOFCONTEXT callbacks execute on this hook's installing UI thread. Each
    // subscription is scoped to the captured process/thread, then filtered to
    // the exact target HWND. Callbacks only remove our own stale surface.
    internal sealed class FocusIndicatorNativeEvents : IFocusIndicatorEvents
    {
        delegate void WinEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int child, uint thread, uint time);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        readonly WinEvent callback;
        readonly List<IntPtr> hooks = new List<IntPtr>();
        readonly FocusedWindowSnapshot target;
        readonly Action<FocusIndicatorChange> changed;
        readonly Func<uint, uint, IntPtr> register;
        readonly Action<IntPtr> unregister;
        bool disposed;
        internal FocusIndicatorNativeEvents(FocusedWindowSnapshot target, Action<FocusIndicatorChange> changed)
            : this(target, changed, null, null) { }
        internal FocusIndicatorNativeEvents(FocusedWindowSnapshot target, Action<FocusIndicatorChange> changed,
            Func<uint, uint, IntPtr> register, Action<IntPtr> unregister)
        {
            if (target == null || target.Process == 0 || target.Thread == 0 || changed == null) throw new ArgumentException("Focus indicator event target unavailable.");
            this.target = target.Copy(); this.changed = changed; callback = Receive;
            this.register = register ?? delegate(uint first, uint last) { return SetWinEventHook(first, last, IntPtr.Zero, callback, this.target.Process, this.target.Thread, 0); };
            this.unregister = unregister ?? delegate(IntPtr hook) { UnhookWinEvent(hook); };
            try { Add(10, 11); Add(0x800B, 0x800B); Add(0x8001, 0x8003); }
            catch { Dispose(); throw; }
        }
        void Add(uint first, uint last)
        {
            IntPtr hook = register(first, last);
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Focus indicator geometry events unavailable.");
            hooks.Add(hook);
        }
        void Receive(IntPtr hook, uint kind, IntPtr window, int objectId, int child, uint thread, uint time)
        {
            if (disposed || window != target.Window || thread != target.Thread) return;
            FocusIndicatorChangeKind change;
            if (kind == 10) change = FocusIndicatorChangeKind.MoveStart;
            else if (kind == 11) change = FocusIndicatorChangeKind.MoveEnd;
            else if (kind == 0x800B) change = FocusIndicatorChangeKind.Location;
            else if (kind == 0x8001) change = FocusIndicatorChangeKind.Destroyed;
            else if (kind == 0x8003) change = FocusIndicatorChangeKind.Hidden;
            else return;
            // Never let managed exceptions cross the native callback boundary.
            try { changed(new FocusIndicatorChange { Window = window, Kind = change, Object = objectId, Child = child }); }
            catch { }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (IntPtr hook in hooks) unregister(hook);
            hooks.Clear(); GC.KeepAlive(callback);
        }
    }

    internal sealed class FocusIndicatorSurface : NativeWindow, IFocusIndicatorSurface
    {
        const int NoActivate = 0x08000000, Transparent = 0x20, ToolWindow = 0x80, Layered = 0x80000, Topmost = 8;
        readonly bool invisibleFixture;
        readonly Func<IntPtr, Bitmap, Point, bool> upload;
        readonly Func<bool> animationsAllowed;
        readonly FocusIndicatorMotion motion;
        readonly FlowClock flow = new FlowClock();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Func<long> now;
        readonly Func<FocusIndicatorVisual, bool> current;
        readonly Action<IFocusIndicatorSurface, Exception> stopped;
        readonly Func<IntPtr, IntPtr, bool> lowerFocusOverlay;
        readonly Timer animationTimer = new Timer { Interval = 15 };
        readonly List<IDisposable> retired = new List<IDisposable>();
        FocusIndicatorVisual visual;
        FocusIndicatorLightingRenderer lightingRenderer;
        LayeredUpload staging;
        Region ownedRegion;
        Bitmap pixels;
        bool disposed, uploaded;
        int publicationRevision, paintingDepth;
        long renderedAt;
        bool sampledRender;
        IntPtr markedFocusTarget;
        double renderedProgress = -1, renderedPhase = -1, renderedStrength = -1, lastFrameMilliseconds;
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool ValidateRect(IntPtr window, IntPtr rectangle);
        [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        const string FocusTargetProperty = "TaskbarSelect.FocusSurface.Target.836e3832-690e-491b-86a4-46cc1a346648";
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr window, string name);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr window, int index);
        internal FocusIndicatorSurface(bool invisibleFixture = false, Func<IntPtr, Bitmap, Point, bool> upload = null,
            Func<bool> animationsAllowed = null, Func<long> now = null, Func<FocusIndicatorVisual, bool> current = null,
            Action<IFocusIndicatorSurface, Exception> stopped = null, Func<IntPtr, IntPtr, bool> lowerFocusOverlay = null)
        {
            this.invisibleFixture = invisibleFixture;
            this.upload = upload;
            this.animationsAllowed = animationsAllowed ?? (invisibleFixture ? (Func<bool>)delegate { return false; } : FocusIndicatorNative.AnimationsAllowed);
            motion = new FocusIndicatorMotion(Allowed);
            this.now = now ?? delegate { return clock.ElapsedMilliseconds; };
            this.current = current ?? delegate(FocusIndicatorVisual value) {
                return FocusIndicatorNative.DesktopAllowed() && FocusedWindowNative.Foreground() == value.Target.Identity.Window;
            };
            this.stopped = stopped;
            this.lowerFocusOverlay = lowerFocusOverlay ?? delegate { return false; };
            animationTimer.Tick += Animate;
        }
        public IntPtr Window { get { return disposed ? IntPtr.Zero : Handle; } }
        internal Region Region { get { return ownedRegion; } }
        internal Bitmap Pixels { get { return pixels; } }
        internal bool Uploaded { get { return uploaded; } }
        internal bool AnimationRunning { get { return !disposed && animationTimer.Enabled; } }
        internal double RevealProgress { get { return renderedProgress; } }
        internal double RevealStrength { get { return renderedStrength; } }
        internal double EnergyPhase { get { return renderedPhase; } }
        internal bool EnergyRunning { get { return !disposed && flow.Active; } }
        internal int AnimationInterval { get { return disposed ? 0 : animationTimer.Interval; } }
        internal FocusIndicatorLightingRenderer LightingCache { get { return lightingRenderer; } }
        internal bool HasStaging { get { return staging != null; } }
        internal static int ExtendedStyle(IntPtr window)
        { return IntPtr.Size == 8 ? unchecked((int)GetWindowLongPtr64(window, -20).ToInt64()) : GetWindowLong32(window, -20); }
        internal static Bitmap Render(FocusIndicatorVisual value)
        { return FocusIndicatorPaint.Render(value, 1); }
        public void Present(FocusIndicatorVisual next)
        {
            if (disposed || next == null || next.Target == null || next.Target.Identity == null) throw new InvalidOperationException("Focus indicator target unavailable.");
            var identity = Verify(next);
            int revision = ++publicationRevision;
            bool changed = !next.SameAppearance(visual);
            bool firstShow = visual == null;
            long sampled = now();
            double progress = firstShow ? 0 : motion.Value(sampled), phase = firstShow ? 0 : Phase(next, sampled);
            double strength = firstShow ? 0 : motion.Strength;
            if (ShouldRender(next, changed, progress, phase, strength, sampled)) {
                FocusIndicatorLightingRenderer nextRenderer = null;
                Bitmap nextPixels = null; Region nextRegion = null;
                bool ownedPixels = false, ownedRenderer = false;
                paintingDepth++;
                try {
                    if (Premium(next)) {
                        nextRenderer = changed ? new FocusIndicatorLightingRenderer(next) : lightingRenderer;
                        ownedRenderer = changed;
                        if (nextRenderer == null) throw new InvalidOperationException("Focus lighting cache unavailable.");
                    }
                    // Building the immutable perimeter cache can take longer
                    // than one reveal frame on a large window. Begin the shown
                    // timeline after that hidden work so the complete reveal
                    // reveal remains available instead of being spent caching.
                    if (firstShow) {
                        sampled = now(); motion.Show(sampled); flow.Show(sampled);
                        progress = motion.Value(sampled); phase = Phase(next, sampled);
                        strength = motion.Strength;
                    }
                    var cost = Stopwatch.StartNew();
                    nextPixels = nextRenderer == null ? FocusIndicatorPaint.Render(next, progress, phase, strength) : nextRenderer.Render(progress, phase, strength);
                    ownedPixels = nextRenderer == null;
                    ClearFixture(nextPixels);
                    if (Handle == IntPtr.Zero) CreateHandle(new CreateParams { Caption = "Taskbar Select focus indicator",
                        Style = unchecked((int)0x88000000), ExStyle = NoActivate | Transparent | ToolWindow | Layered | (next.PinCorner ? Topmost : 0),
                        X = next.Bounds.X, Y = next.Bounds.Y, Width = next.Bounds.Width, Height = next.Bounds.Height });
                    if (Handle == IntPtr.Zero) throw new InvalidOperationException("Focus indicator surface unavailable.");
                    if (changed) {
                        nextRegion = FocusIndicatorPaint.Shape(next);
                        using (Graphics graphics = Graphics.FromImage(nextPixels)) {
                            IntPtr nativeRegion = nextRegion.GetHrgn(graphics);
                            if (SetWindowRgn(Handle, nativeRegion, false) == 0) {
                                int error = Marshal.GetLastWin32Error(); DeleteObject(nativeRegion);
                                throw new Win32Exception(error, "Focus indicator shape unavailable.");
                            }
                            // Windows owns the HRGN; keep our managed copy and
                            // every AA fringe until this surface is destroyed.
                        }
                    }
                    // Upload a complete premultiplied image BEFORE any show.
                    // No redirected Form paint or color-key backing exists.
                    IntPtr publishingWindow = Handle;
                    bool presented = Upload(publishingWindow, nextPixels, next);
                    if (disposed || Handle != publishingWindow)
                        throw new InvalidOperationException("Focus indicator presentation was cancelled.");
                    if (revision != publicationRevision) return;
                    if (!presented) throw new InvalidOperationException("Focus indicator alpha presentation unavailable.");
                    Bitmap oldPixels = pixels; Region oldRegion = null;
                    var oldRenderer = lightingRenderer;
                    pixels = nextPixels; ownedPixels = false;
                    lightingRenderer = nextRenderer; ownedRenderer = false;
                    if (nextRegion != null) { oldRegion = ownedRegion; ownedRegion = nextRegion; nextRegion = null; }
                    visual = next; uploaded = true; renderedProgress = progress; renderedPhase = phase; renderedStrength = strength;
                    RecordRender(sampled);
                    lastFrameMilliseconds = cost.Elapsed.TotalMilliseconds;
                    if (!Object.ReferenceEquals(oldRenderer, lightingRenderer)) {
                        if (oldRenderer != null) Retire(oldRenderer); else if (oldPixels != null) Retire(oldPixels);
                    } else if (oldRenderer == null && oldPixels != null && !Object.ReferenceEquals(oldPixels, pixels)) Retire(oldPixels);
                    if (oldRegion != null) Retire(oldRegion);
                    if (!Premium(next) && staging != null) { Retire(staging); staging = null; }
                } catch (Exception) {
                    // A callback may commit a replacement while this obsolete
                    // upload subsequently reports failure. Only the current
                    // publication can propagate an error to the manager.
                    if (!disposed && revision != publicationRevision) return;
                    throw;
                } finally {
                    if (ownedRenderer && nextRenderer != null) Retire(nextRenderer);
                    if (ownedPixels && nextPixels != null) Retire(nextPixels);
                    if (nextRegion != null) Retire(nextRegion);
                    FinishPainting();
                }
            }
            if (disposed || revision != publicationRevision) return;
            Verify(next);
            BindFocusMark(next.PinRail ? IntPtr.Zero : identity.Window);
            Position(identity.Window, next.Bounds);
            Schedule(next);
        }
        static FocusedWindowSnapshot Verify(FocusIndicatorVisual value)
        {
            var identity = FocusedWindowNative.Read(value.Target.Identity.Window);
            if (!FocusedWindowPolicy.SameWindow(value.Target.Identity, identity) || !identity.Visible || FocusIndicatorNative.IsIconic(identity.Window))
                throw new FocusIndicatorGeometryChangedException();
            if (!FocusIndicatorNative.MatchesFrame(value.Target)) throw new FocusIndicatorGeometryChangedException();
            return identity;
        }
        void Animate(object sender, EventArgs args) { AdvanceAnimation(); }
        internal void AdvanceAnimation()
        {
            if (disposed || visual == null || (!motion.Active && !flow.Active)) { if (!disposed) animationTimer.Stop(); return; }
            var frame = visual; int revision = publicationRevision;
            try {
                if (!current(frame)) { if (OwnsPublication(frame, revision)) StopAnimation(null); return; }
                if (!OwnsPublication(frame, revision)) return;
                var identity = Verify(frame);
                long sampled = now();
                double progress = motion.Value(sampled), phase = Phase(frame, sampled);
                double strength = motion.Strength;
                if (ShouldRender(frame, false, progress, phase, strength, sampled)) {
                    Bitmap nextPixels = null; bool ownsPixels = lightingRenderer == null;
                    paintingDepth++;
                    try {
                        var cost = Stopwatch.StartNew();
                        nextPixels = ownsPixels ? FocusIndicatorPaint.Render(frame, progress, phase, strength) : lightingRenderer.Render(progress, phase, strength);
                        ClearFixture(nextPixels);
                        Verify(frame);
                        if (!current(frame)) { if (OwnsPublication(frame, revision)) StopAnimation(null); return; }
                        if (!OwnsPublication(frame, revision)) return;
                        IntPtr publishingWindow = Handle;
                        bool presented = Upload(publishingWindow, nextPixels, frame);
                        if (disposed || Handle != publishingWindow || revision != publicationRevision || !Object.ReferenceEquals(visual, frame)) return;
                        if (!presented) throw new InvalidOperationException("Focus indicator alpha presentation unavailable.");
                        Bitmap previous = pixels; pixels = nextPixels; ownsPixels = false;
                        renderedProgress = progress; renderedPhase = phase; renderedStrength = strength;
                        RecordRender(sampled);
                        lastFrameMilliseconds = cost.Elapsed.TotalMilliseconds;
                        if (previous != null && !Object.ReferenceEquals(previous, pixels)) Retire(previous);
                    } finally {
                        if (ownsPixels && nextPixels != null) Retire(nextPixels);
                        FinishPainting();
                    }
                }
                if (disposed || revision != publicationRevision || !Object.ReferenceEquals(visual, frame)) return;
                Verify(frame);
                if (!current(frame)) { if (OwnsPublication(frame, revision)) StopAnimation(null); return; }
                if (!OwnsPublication(frame, revision)) return;
                Position(identity.Window, frame.Bounds);
                Schedule(frame);
            } catch (Exception failure) { if (OwnsPublication(frame, revision)) StopAnimation(failure); }
        }
        bool OwnsPublication(FocusIndicatorVisual frame, int revision)
        { return !disposed && revision == publicationRevision && Object.ReferenceEquals(visual, frame); }
        static bool Premium(FocusIndicatorVisual value)
        { return value != null && (value.Lighting == FocusLightingMode.Illuminated || value.Lighting == FocusLightingMode.EnergyFlow); }
        bool Allowed()
        { try { return animationsAllowed(); } catch (Exception) { return false; } }
        double Phase(FocusIndicatorVisual value, long sampled)
        {
            bool energy = value.Lighting == FocusLightingMode.EnergyFlow;
            if (energy && !Allowed()) { flow.Reset(sampled); return 0; }
            return flow.Value(sampled, energy);
        }
        void ClearFixture(Bitmap image)
        {
            if (invisibleFixture) using (Graphics graphics = Graphics.FromImage(image)) {
                graphics.CompositingMode = CompositingMode.SourceCopy; graphics.Clear(Color.Transparent);
            }
        }
        bool Upload(IntPtr window, Bitmap image, FocusIndicatorVisual value)
        {
            if (upload != null) return upload(window, image, value.Bounds.Location);
            if (!Premium(value)) return ShelfPaint.PresentLayered(window, image, value.Bounds.Location);
            if (staging == null) staging = new LayeredUpload();
            return staging.Present(window, image, value);
        }
        void Schedule(FocusIndicatorVisual value)
        {
            if (disposed) return;
            bool flowing = flow.Active && value.Lighting == FocusLightingMode.EnergyFlow;
            // A short reveal has its own cadence; the conservative continuous
            // Energy budget resumes after its taper has settled.
            animationTimer.Interval = motion.Active ? 16 : flowing ? FlowInterval(value.Bounds.Size, lastFrameMilliseconds) : 16;
            animationTimer.Enabled = motion.Active || flowing;
        }
        bool ShouldRender(FocusIndicatorVisual value, bool changed, double progress, double phase, double strength, long sampled)
        {
            // The normal 80ms verifier also calls Present. It must not add flow
            // uploads between timer frames or bypass a slower measured budget.
            // Appearance changes, completed reveal and reduced motion commit
            // immediately; only an unchanged phase-only update is rate limited.
            if (changed || progress != renderedProgress || strength != renderedStrength) return true;
            if (phase == renderedPhase) return false;
            if (!flow.Active || value.Lighting != FocusLightingMode.EnergyFlow) return true;
            return !sampledRender || FrameDue(sampled, renderedAt, FlowInterval(value.Bounds.Size, lastFrameMilliseconds));
        }
        void RecordRender(long sampled)
        { if (!sampledRender || sampled > renderedAt) renderedAt = sampled; sampledRender = true; }
        internal static bool FrameDue(long sampled, long previous, int interval)
        { return sampled >= previous && unchecked((ulong)sampled - (ulong)previous) >= (ulong)Math.Max(1, interval); }
        internal static int FlowInterval(Size size, double frameMilliseconds)
        {
            long area = (long)size.Width * size.Height;
            int baseline = area > 4000000 ? 67 : area > 1800000 ? 50 : 33;
            if (Double.IsNaN(frameMilliseconds) || Double.IsInfinity(frameMilliseconds) || frameMilliseconds < 0) return baseline;
            return Math.Max(baseline, (int)Math.Min(250, Math.Ceiling(frameMilliseconds * 2)));
        }
        void Retire(IDisposable resource)
        {
            if (resource == null) return;
            if (paintingDepth != 0) retired.Add(resource); else resource.Dispose();
        }
        void FinishPainting()
        {
            paintingDepth--;
            if (paintingDepth != 0) return;
            foreach (var resource in retired) resource.Dispose();
            retired.Clear();
            if (disposed) ReleasePixels();
        }
        void ReleasePixels()
        {
            if (lightingRenderer != null) { lightingRenderer.Dispose(); lightingRenderer = null; pixels = null; }
            else if (pixels != null) { pixels.Dispose(); pixels = null; }
            if (staging != null) { staging.Dispose(); staging = null; }
            if (ownedRegion != null) { ownedRegion.Dispose(); ownedRegion = null; }
        }
        void StopAnimation(Exception failure)
        {
            if (disposed) return;
            animationTimer.Stop(); motion.Hide(); flow.Hide();
            try { if (stopped != null) stopped(this, failure); }
            catch (Exception) { }
            finally { Dispose(); }
        }
        internal static bool MarksFocusSurface(IntPtr window, IntPtr target)
        { return window != IntPtr.Zero && target != IntPtr.Zero && GetProp(window, FocusTargetProperty) == target; }
        void BindFocusMark(IntPtr target)
        {
            if (target == IntPtr.Zero) { ClearFocusMark(); return; }
            if (markedFocusTarget == target && MarksFocusSurface(Handle, target)) return;
            if (!SetProp(Handle, FocusTargetProperty, target))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Focus surface binding unavailable.");
            markedFocusTarget = target;
        }
        void ClearFocusMark()
        {
            if (markedFocusTarget == IntPtr.Zero) return;
            if (Handle != IntPtr.Zero) RemoveProp(Handle, FocusTargetProperty);
            markedFocusTarget = IntPtr.Zero;
        }
        // Walk only a short contiguous chain immediately above this target.
        // A property alone is not authentication: the caller must also bind
        // each skipped HWND to its exact local surface or verified helper.
        internal static bool TryPinCornerStack(IntPtr target, IntPtr own, Func<IntPtr, IntPtr> previous,
            Func<IntPtr, IntPtr, bool> authenticatedFocus, out IntPtr after, out bool keepOrder)
        {
            after = IntPtr.Zero; keepOrder = false;
            if (target == IntPtr.Zero || own == IntPtr.Zero || target == own || previous == null || authenticatedFocus == null) return false;
            bool sawOwn = false, focusAboveOwn = false;
            var seen = new HashSet<IntPtr>();
            try {
                IntPtr cursor = previous(target);
                for (int i = 0; i < 8; i++) {
                    if (cursor == IntPtr.Zero) { after = cursor; keepOrder = sawOwn && !focusAboveOwn; return true; }
                    if (cursor == target || !seen.Add(cursor)) return false;
                    if (cursor == own) { sawOwn = true; cursor = previous(cursor); continue; }
                    bool known;
                    try { known = authenticatedFocus(cursor, target); } catch { known = false; }
                    if (!known) { after = cursor; keepOrder = sawOwn && !focusAboveOwn; return true; }
                    if (sawOwn) focusAboveOwn = true;
                    cursor = previous(cursor);
                }
            } catch { }
            return false;
        }
        void Position(IntPtr target, Rectangle bounds)
        {
            if (!uploaded || pixels == null) throw new InvalidOperationException("Focus indicator pixels unavailable.");
            bool targetTopmost = (ExtendedStyle(target) & Topmost) != 0;
            bool ownTopmost = (ExtendedStyle(Handle) & Topmost) != 0;
            bool corner = visual != null && visual.PinCorner;
            if (corner && targetTopmost != ownTopmost) ShowWindow(Handle, 0);
            if (targetTopmost != ownTopmost && !SetWindowPos(Handle, new IntPtr(targetTopmost ? -1 : -2), 0, 0, 0, 0, 0x10 | 1 | 2 | 0x200))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Focus indicator z-order unavailable.");
            // UpdateLayeredWindow already committed position, size and pixels
            // together. This call only changes z-order and visible state.
            IntPtr previous = GetWindow(target, 3); uint flags = 0x10 | 0x40 | 0x200 | 1 | 2;
            if (corner) {
                bool keep;
                if (!TryPinCornerStack(target, Handle, delegate(IntPtr window) { return GetWindow(window, 3); },
                    lowerFocusOverlay, out previous, out keep)) throw new FocusIndicatorGeometryChangedException();
                if (keep) flags |= 4;
            } else {
                if (previous == Handle) flags |= 4;
                else if (previous == IntPtr.Zero || (!targetTopmost && (ExtendedStyle(previous) & Topmost) != 0)) previous = IntPtr.Zero;
            }
            if (!SetWindowPos(Handle, previous, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Focus indicator positioning unavailable.");
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; }
            if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }
            if (message.Msg == 0x14) { message.Result = new IntPtr(1); return; } // no background brush
            if (message.Msg == 0x0F) { ValidateRect(Handle, IntPtr.Zero); message.Result = IntPtr.Zero; return; }
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            ClearFocusMark();
            animationTimer.Stop(); animationTimer.Tick -= Animate; animationTimer.Dispose();
            motion.Hide(); flow.Hide(); clock.Stop(); publicationRevision++;
            // Keep the hollow shape and alpha image intact until the HWND is
            // hidden/destroyed. Removing a visible Region caused the old flash.
            try { if (Handle != IntPtr.Zero) { ShowWindow(Handle, 0); DestroyHandle(); } }
            finally {
                uploaded = false; visual = null;
                if (paintingDepth == 0) ReleasePixels();
            }
        }

        // One monotonic, bounded phase for the lifetime of a shown cue. Palette
        // and style changes reuse it; hiding and reduced motion reset it.
        internal sealed class FlowClock
        {
            internal const int LapMilliseconds = 6000;
            long latest;
            ulong ticks;
            internal bool Visible { get; private set; }
            internal bool Active { get; private set; }
            internal double Phase { get { return ticks / (double)LapMilliseconds; } }
            internal void Show(long now)
            {
                if (Visible) return;
                Visible = true; latest = now; ticks = 0; Active = false;
            }
            internal double Value(long now, bool running)
            {
                if (!Visible) return 0;
                if (now > latest) {
                    ulong elapsed = unchecked((ulong)now - (ulong)latest);
                    if (Active && running) ticks = (ticks + elapsed % LapMilliseconds) % LapMilliseconds;
                    latest = now;
                }
                Active = running;
                return Phase;
            }
            internal void Reset(long now)
            { if (now > latest) latest = now; ticks = 0; Active = false; }
            internal void Hide()
            { Visible = Active = false; latest = 0; ticks = 0; }
        }

        // A premium frame changes only a narrow perimeter. Keep its native DIB
        // and copy that band on each tick instead of allocating/copying a 4K
        // transparent client body every time. Nothing is drawn to the target.
        internal sealed class LayeredUpload : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
            {
                internal uint Size; internal int Width, Height; internal ushort Planes, Bits;
                internal uint Compression, ImageSize; internal int X, Y; internal uint Colors, Important;
            }
            [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { internal byte Operation, Flags, Alpha, Format; }
            [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
            [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
            [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
            [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point destination,
                ref Size size, IntPtr source, ref Point origin, uint key, ref Blend blend, uint flags);
            IntPtr dc, dib, bits, previous;
            Size size;
            byte[] row;
            FocusIndicatorVisual geometry;
            bool disposed;
            internal int Allocations { get; private set; }
            internal bool Ready { get { return !disposed && dc != IntPtr.Zero && dib != IntPtr.Zero && bits != IntPtr.Zero; } }
            internal bool Present(IntPtr window, Bitmap image, FocusIndicatorVisual value)
            {
                if (!Stage(image, value)) return false;
                var origin = Point.Empty; var location = value.Bounds.Location; var extent = size;
                var blend = new Blend { Alpha = 255, Format = 1 };
                return UpdateLayeredWindow(window, IntPtr.Zero, ref location, ref extent, dc, ref origin, 0, ref blend, 2);
            }
            internal bool Stage(Bitmap image, FocusIndicatorVisual value)
            {
                if (disposed) throw new ObjectDisposedException("FocusIndicatorLayeredUpload");
                if (image == null || value == null || image.Size != value.Bounds.Size) return false;
                if (!Ensure(image.Size)) return false;
                if (!SameGeometry(geometry, value)) Clear();
                var data = image.LockBits(new Rectangle(Point.Empty, image.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try {
                    foreach (Rectangle band in Bands(value)) {
                        int count = band.Width * 4;
                        for (int y = band.Top; y < band.Bottom; y++) {
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride + band.Left * 4), row, 0, count);
                            Marshal.Copy(row, 0, IntPtr.Add(bits, y * size.Width * 4 + band.Left * 4), count);
                        }
                    }
                } finally { image.UnlockBits(data); }
                geometry = value;
                return true;
            }
            internal int Pixel(int x, int y)
            {
                if (!Ready || x < 0 || y < 0 || x >= size.Width || y >= size.Height) throw new ArgumentOutOfRangeException("pixel");
                return Marshal.ReadInt32(bits, (y * size.Width + x) * 4);
            }
            bool Ensure(Size next)
            {
                if (Ready && next == size) return true;
                Release(); size = next;
                if (next.Width <= 0 || next.Height <= 0 || next.Width > 16384 || next.Height > 16384 || (long)next.Width * next.Height > 16777216) return false;
                var info = new BitmapInfo { Size = 40, Width = next.Width, Height = -next.Height, Planes = 1, Bits = 32 };
                dc = CreateCompatibleDC(IntPtr.Zero);
                if (dc != IntPtr.Zero) dib = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
                if (dc == IntPtr.Zero || dib == IntPtr.Zero || bits == IntPtr.Zero) { Release(); return false; }
                previous = SelectObject(dc, dib);
                if (!ShelfPaint.ValidSelection(previous)) { previous = IntPtr.Zero; Release(); return false; }
                row = new byte[next.Width * 4]; Allocations++; Clear(); return true;
            }
            void Clear()
            {
                Array.Clear(row, 0, row.Length);
                for (int y = 0; y < size.Height; y++) Marshal.Copy(row, 0, IntPtr.Add(bits, y * row.Length), row.Length);
            }
            static bool SameGeometry(FocusIndicatorVisual a, FocusIndicatorVisual b)
            {
                return a != null && b != null && a.Bounds == b.Bounds && a.Target.Frame == b.Target.Frame &&
                    a.Target.Corner == b.Target.Corner && a.Thickness == b.Thickness && a.GlowWidth == b.GlowWidth;
            }
            internal static Rectangle[] Bands(FocusIndicatorVisual value)
            {
                var frame = value.Target.Frame;
                long left = (long)frame.Left - value.GlowWidth - 2, top = (long)frame.Top - value.GlowWidth - 2;
                long right = (long)frame.X + frame.Width + value.GlowWidth + 2, bottom = (long)frame.Y + frame.Height + value.GlowWidth + 2;
                long band = (long)Math.Max(value.Target.Corner, value.Thickness + 2) + value.GlowWidth + 2;
                return new[] { Clip(value.Bounds, left, top, right, Math.Min(bottom, top + band)),
                    Clip(value.Bounds, left, Math.Max(top, bottom - band), right, bottom),
                    Clip(value.Bounds, left, top, Math.Min(right, left + band), bottom),
                    Clip(value.Bounds, Math.Max(left, right - band), top, right, bottom) };
            }
            static Rectangle Clip(Rectangle bounds, long left, long top, long right, long bottom)
            {
                left = Math.Max(bounds.X, left); top = Math.Max(bounds.Y, top);
                right = Math.Min((long)bounds.X + bounds.Width, right); bottom = Math.Min((long)bounds.Y + bounds.Height, bottom);
                if (right <= left || bottom <= top) return Rectangle.Empty;
                return new Rectangle((int)(left - bounds.X), (int)(top - bounds.Y), (int)(right - left), (int)(bottom - top));
            }
            void Release()
            {
                if (previous != IntPtr.Zero && dc != IntPtr.Zero) SelectObject(dc, previous);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (dc != IntPtr.Zero) DeleteDC(dc);
                dc = dib = bits = previous = IntPtr.Zero; row = null; geometry = null;
            }
            public void Dispose()
            { if (disposed) return; disposed = true; Release(); }
        }
    }
}
