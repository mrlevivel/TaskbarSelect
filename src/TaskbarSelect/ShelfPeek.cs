using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal interface IShelfPeekSurface : IDisposable
    {
        IntPtr Handle { get; }
        bool Visible { get; }
        bool Create(Rectangle bounds, IntPtr anchorWindow);
        bool Resize(Rectangle bounds);
        bool EnableClickThrough();
        bool ShowBehind(IntPtr anchorWindow);
    }

    // Begin non-layered for DWM setup, then enable layered input passthrough before display.
    // The source HWND is never moved or shown.
    internal sealed class ShelfPeekSurface : NativeWindow, IShelfPeekSurface
    {
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);

        Rectangle bounds;
        bool clickThroughReady;
        internal static int WindowStyle { get { return unchecked((int)0x80000000) | 0x08000000; } } // WS_POPUP | WS_DISABLED
        internal static int ExtendedStyle { get { return 0x8 | 0x80 | 0x20 | 0x08000000; } } // TOPMOST | TOOLWINDOW | TRANSPARENT | NOACTIVATE
        internal static int PresentedExtendedStyle { get { return ExtendedStyle | 0x80000; } } // plus WS_EX_LAYERED
        public bool Visible { get { return Handle != IntPtr.Zero && IsWindowVisible(Handle); } }

        public bool Create(Rectangle requested, IntPtr anchorWindow)
        {
            if (Handle != IntPtr.Zero || !IsWindow(anchorWindow)) return false;
            bounds = requested;
            var parameters = new CreateParams { Caption = "Taskbar Select hover preview", Style = WindowStyle, ExStyle = ExtendedStyle,
                X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
            CreateHandle(parameters);
            return Handle != IntPtr.Zero && ShelfPreviewGlass.Configure(Handle, true);
        }

        public bool Resize(Rectangle requested)
        {
            bounds = requested;
            return Handle != IntPtr.Zero && SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                0x4 | 0x10 | 0x200); // NOZORDER | NOACTIVATE | NOOWNERZORDER
        }

        public bool EnableClickThrough()
        {
            if (Handle == IntPtr.Zero || Visible || clickThroughReady) return false;
            int style = GetWindowLong(Handle, -20);
            if ((style & PresentedExtendedStyle) != ExtendedStyle ||
                SetWindowLong(Handle, -20, style | 0x80000) == 0 ||
                (GetWindowLong(Handle, -20) & PresentedExtendedStyle) != PresentedExtendedStyle) return false;
            clickThroughReady = SetLayeredWindowAttributes(Handle, 0, 255, 2); // LWA_ALPHA
            return clickThroughReady;
        }

        public bool ShowBehind(IntPtr anchorWindow)
        {
            if (!clickThroughReady || Handle == IntPtr.Zero || !IsWindow(anchorWindow) ||
                (GetWindowLong(Handle, -20) & PresentedExtendedStyle) != PresentedExtendedStyle) return false;
            if (!SetWindowPos(Handle, anchorWindow, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10 | 0x200)) return false;
            ShowWindow(Handle, 8); // SW_SHOWNA, on the overlay only
            return Visible && IsWindow(anchorWindow) && SetWindowPos(Handle, anchorWindow, bounds.X, bounds.Y,
                bounds.Width, bounds.Height, 0x10 | 0x200);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            if (message.Msg == 0x0021) { message.Result = new IntPtr(4); return; } // MA_NOACTIVATEANDEAT
            base.WndProc(ref message);
        }

        public void Dispose() { clickThroughReady = false; if (Handle != IntPtr.Zero) DestroyHandle(); }
    }

    internal sealed class ShelfPeek : IDisposable
    {
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        readonly Func<ShelfWindow, bool> validate;
        readonly IShelfThumbnailApi api;
        readonly Func<IntPtr, bool> sourceVisible;
        readonly Func<IntPtr, bool> sourceIconic;
        readonly Func<Rectangle, bool> boundsAllowed;
        readonly Func<IShelfPeekSurface> createSurface;
        readonly bool automaticRefresh;
        readonly Timer refresh = new Timer { Interval = 250 };
        IShelfPeekSurface surface;
        IntPtr thumbnail;
        Size sourceSize;
        ShelfWindow source;
        Rectangle requestedBounds, surfaceBounds;
        bool disposed;

        internal ShelfPeek(Func<ShelfWindow, bool> validate, IShelfThumbnailApi api = null)
            : this(validate, api ?? new ShelfRecoveryThumbnailApi(), Native.IsWindowVisible,
                IsIconic, InWorkArea, delegate { return new ShelfPeekSurface(); }, true) { }

        // Pure tests inject visibility, work-area and destination behavior; production always uses native checks.
        internal ShelfPeek(Func<ShelfWindow, bool> validate, IShelfThumbnailApi api, Func<IntPtr, bool> sourceVisible,
            Func<Rectangle, bool> boundsAllowed, Func<IShelfPeekSurface> createSurface)
            : this(validate, api, sourceVisible, delegate { return false; }, boundsAllowed, createSurface, false) { }

        internal ShelfPeek(Func<ShelfWindow, bool> validate, IShelfThumbnailApi api, Func<IntPtr, bool> sourceVisible,
            Func<IntPtr, bool> sourceIconic, Func<Rectangle, bool> boundsAllowed, Func<IShelfPeekSurface> createSurface)
            : this(validate, api, sourceVisible, sourceIconic, boundsAllowed, createSurface, false) { }

        ShelfPeek(Func<ShelfWindow, bool> validate, IShelfThumbnailApi api, Func<IntPtr, bool> sourceVisible,
            Func<IntPtr, bool> sourceIconic, Func<Rectangle, bool> boundsAllowed,
            Func<IShelfPeekSurface> createSurface, bool automaticRefresh)
        {
            if (validate == null || api == null || sourceVisible == null || sourceIconic == null || boundsAllowed == null || createSurface == null)
                throw new ArgumentNullException();
            this.validate = validate; this.api = api; this.sourceVisible = sourceVisible; this.sourceIconic = sourceIconic;
            this.boundsAllowed = boundsAllowed; this.createSurface = createSurface; this.automaticRefresh = automaticRefresh;
            refresh.Tick += delegate { Check(); };
        }

        static bool InWorkArea(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;
            bool found = false, failed = false;
            bool enumerated = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate(IntPtr monitor, IntPtr dc, ref Native.Rect unused, IntPtr data) {
                    var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                    if (!Native.GetMonitorInfo(monitor, ref info)) failed = true;
                    else if (Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom).Contains(bounds)) found = true;
                    return true;
                }, IntPtr.Zero);
            return enumerated && !failed && found;
        }

        bool Valid(ShelfWindow candidate)
        {
            try { return !disposed && WindowShelfPolicy.SameWindow(candidate, candidate) &&
                sourceVisible(candidate.Handle) && !sourceIconic(candidate.Handle) && validate(candidate) &&
                sourceVisible(candidate.Handle) && !sourceIconic(candidate.Handle); }
            catch (Exception) { return false; }
        }

        internal bool IsVisible { get { return source != null && surface != null && surface.Visible; } }
        internal IntPtr SourceHandle { get { return IsVisible ? source.Handle : IntPtr.Zero; } }
        internal IntPtr WindowHandle { get { return IsVisible ? surface.Handle : IntPtr.Zero; } }

        internal bool Show(ShelfWindow candidate, Rectangle bounds, IntPtr anchorWindow)
        {
            Hide();
            try {
                if (disposed || anchorWindow == IntPtr.Zero || bounds.Width <= 0 || bounds.Height <= 0 ||
                    !boundsAllowed(bounds) || !Valid(candidate)) return false;
                surface = createSurface();
                if (surface == null || !surface.Create(bounds, anchorWindow)) return Fail();
                if (api.Register(surface.Handle, candidate.Handle, out thumbnail) != 0 || thumbnail == IntPtr.Zero ||
                    !Valid(candidate) || api.Query(thumbnail, out sourceSize) != 0 || !Valid(candidate)) return Fail();
                Rectangle fitted = ShelfPreviewLayout.AspectFit(sourceSize, new Rectangle(Point.Empty, bounds.Size));
                Rectangle screenFit = new Rectangle(bounds.X + fitted.X, bounds.Y + fitted.Y, fitted.Width, fitted.Height);
                if (fitted.IsEmpty || !bounds.Contains(screenFit) || api.Update(thumbnail, fitted, 255) != 0 ||
                    !Valid(candidate) || !surface.Resize(screenFit) ||
                    api.Update(thumbnail, new Rectangle(Point.Empty, screenFit.Size), 255) != 0 ||
                    !Valid(candidate) || !surface.EnableClickThrough() || !Valid(candidate) ||
                    !surface.ShowBehind(anchorWindow) || !Valid(candidate)) return Fail();
                source = candidate; requestedBounds = bounds; surfaceBounds = screenFit;
                if (automaticRefresh) refresh.Start();
                return true;
            } catch (Exception) { return Fail(); }
        }

        bool Fail() { Hide(); return false; }

        internal void Check()
        {
            if (!IsVisible) { Hide(); return; }
            try {
                Size current;
                if (!boundsAllowed(requestedBounds) || !Valid(source) || thumbnail == IntPtr.Zero ||
                    api.Query(thumbnail, out current) != 0 || current != sourceSize || !Valid(source) ||
                    api.Update(thumbnail, new Rectangle(Point.Empty, surfaceBounds.Size), 255) != 0 || !Valid(source)) Hide();
            } catch (Exception) { Hide(); }
        }

        internal void Hide()
        {
            if (!disposed) refresh.Stop();
            source = null; requestedBounds = Rectangle.Empty; surfaceBounds = Rectangle.Empty; sourceSize = Size.Empty;
            IntPtr ownedThumbnail = thumbnail; thumbnail = IntPtr.Zero;
            var ownedSurface = surface; surface = null;
            try { if (ownedThumbnail != IntPtr.Zero) api.Unregister(ownedThumbnail); }
            catch (Exception) { }
            finally { if (ownedSurface != null) try { ownedSurface.Dispose(); } catch (Exception) { } }
        }

        public void Dispose()
        {
            if (disposed) return;
            Hide(); disposed = true; refresh.Dispose();
        }
    }
}
