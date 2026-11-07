using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class ShelfGroupDwell
    {
        object candidate, suppressed;
        long entered;
        internal void Reset() { candidate = null; suppressed = null; entered = 0; }
        internal void Suppress(object group) { candidate = null; suppressed = group; }
        internal bool Update(object group, long now, bool quiet)
        {
            if (group == null) { Reset(); return false; }
            if (!Object.ReferenceEquals(suppressed, group)) suppressed = null;
            if (!quiet) { Suppress(group); return false; }
            if (Object.ReferenceEquals(suppressed, group)) return false;
            if (!Object.ReferenceEquals(candidate, group) || now < entered) { candidate = group; entered = now; return false; }
            return now - entered >= 350;
        }
    }

    // Preview selection always focuses/restores a local window; only a remote
    // window is moved. Resolve each entry independently, including mixed groups.
    internal static class ShelfPreviewAction
    {
        internal static string Label(ShelfWindow window, Display target, Func<ShelfWindow, string> sourceDevice)
        {
            if (window == null || window.Handle == IntPtr.Zero || window.Process == 0 || target == null ||
                String.IsNullOrWhiteSpace(target.Device) || sourceDevice == null) return String.Empty;
            try {
                string device = sourceDevice(window);
                if (String.IsNullOrWhiteSpace(device)) return String.Empty;
                return String.Equals(device, target.Device, StringComparison.OrdinalIgnoreCase) ? "Focus here" : "Bring to this screen";
            } catch (Exception) { return String.Empty; }
        }
        internal static string ReadSourceDevice(ShelfWindow window)
        {
            uint before, after;
            if (window == null || window.Handle == IntPtr.Zero || window.Process == 0 ||
                Native.GetWindowThreadProcessId(window.Handle, out before) == 0 || before != window.Process) return String.Empty;
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            // MonitorFromWindow uses the pre-minimize rectangle for iconic windows.
            // Do not exclude hidden tracked minimizes merely to obtain a label.
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(window.Handle, 2), ref monitor) ||
                Native.GetWindowThreadProcessId(window.Handle, out after) == 0 || after != before) return String.Empty;
            return monitor.Device;
        }
        internal static string DefaultAction(string label)
        { return String.IsNullOrEmpty(label) ? "Select" : label; }
        internal static string Description(string label)
        {
            return label == "Focus here" ? "Focus this window here; restore it in place if minimized." :
                label == "Bring to this screen" ? "Bring this window to this screen and focus it." : "Select this window.";
        }
        internal static void CaptionRows(Rectangle bounds, out Rectangle title, out Rectangle action)
        {
            int split = bounds.Height / 2;
            title = new Rectangle(bounds.X, bounds.Y, bounds.Width, split);
            action = new Rectangle(bounds.X, bounds.Y + split, bounds.Width, bounds.Height - split);
        }
    }

    internal sealed class ShelfPreviewLayout
    {
        internal Rectangle Bounds, Previous, Next, PageLabel;
        internal int Capacity, Pages, Padding, TitleHeight, TileWidth, TileHeight;
        internal Rectangle Tile(int slot)
        { return new Rectangle(Padding + slot * (TileWidth + Padding), Padding, TileWidth, TileHeight); }
        internal Rectangle Image(int slot)
        {
            var tile = Tile(slot);
            return new Rectangle(tile.X + Padding, tile.Y + Padding, tile.Width - Padding * 2, tile.Height - TitleHeight - Padding * 2);
        }
        internal Rectangle Title(int slot)
        {
            var tile = Tile(slot);
            return new Rectangle(tile.X + Padding, tile.Bottom - TitleHeight, tile.Width - Padding * 2, TitleHeight);
        }
        internal Rectangle Title(int slot, Rectangle preview)
        {
            var title = Title(slot);
            title.Y = preview.Bottom + Padding / 2;
            return title;
        }
        internal static ShelfPreviewLayout Create(Display display, int dpi, int bottom, int count)
        {
            var layout = new ShelfPreviewLayout();
            layout.Padding = WindowShelfPolicy.Scale(8, dpi);
            layout.TitleHeight = WindowShelfPolicy.Scale(30, dpi);
            int desiredWidth = WindowShelfPolicy.Scale(232, dpi);
            layout.Capacity = Math.Max(1, Math.Min(Math.Min(4, Math.Max(1, count)),
                (display.Width - layout.Padding) / (desiredWidth + layout.Padding)));
            layout.Pages = Math.Max(1, (count + layout.Capacity - 1) / layout.Capacity);
            layout.TileWidth = Math.Min(desiredWidth, (display.Width - layout.Padding) / layout.Capacity - layout.Padding);
            int footer = layout.Pages > 1 ? WindowShelfPolicy.Scale(32, dpi) : 0;
            int available = Math.Max(1, Math.Min(display.Height, bottom - display.Y));
            layout.TileHeight = Math.Min(WindowShelfPolicy.Scale(140, dpi) + layout.TitleHeight + layout.Padding * 2,
                available - layout.Padding * 2 - footer);
            // Supported shelf displays leave room for at least a small preview and title.
            layout.TileHeight = Math.Max(layout.TitleHeight + layout.Padding * 2 + 1, layout.TileHeight);
            int width = layout.Capacity * (layout.TileWidth + layout.Padding) + layout.Padding;
            int height = layout.TileHeight + layout.Padding * 2 + footer;
            layout.Bounds = WindowShelfPolicy.Fit(display, width, height, bottom);
            if (footer > 0) {
                int y = layout.Padding * 2 + layout.TileHeight;
                layout.Previous = new Rectangle(layout.Padding, y, footer, footer);
                layout.Next = new Rectangle(width - layout.Padding - footer, y, footer, footer);
                layout.PageLabel = Rectangle.FromLTRB(layout.Previous.Right, y, layout.Next.Left, y + footer);
            }
            return layout;
        }
        internal static Rectangle AspectFit(Size source, Rectangle bounds)
        {
            if (source.Width <= 0 || source.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return Rectangle.Empty;
            double scale = Math.Min(bounds.Width / (double)source.Width, bounds.Height / (double)source.Height);
            int width = Math.Max(1, Math.Min(bounds.Width, (int)Math.Round(source.Width * scale)));
            int height = Math.Max(1, Math.Min(bounds.Height, (int)Math.Round(source.Height * scale)));
            return new Rectangle(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
        }
    }

    internal static class ShelfPreviewGlass
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Margins
        { internal int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")] static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static Margins Frame(bool ghost)
        { return ghost ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new Margins(); }
        internal static bool Configure(IntPtr window, bool requested)
        {
            try {
                bool composition;
                bool ghost = requested && DwmIsCompositionEnabled(out composition) == 0 && composition;
                var margins = Frame(ghost);
                int result = DwmExtendFrameIntoClientArea(window, ref margins);
                // Optional Windows 11 attributes: no enclosing rounded frame or system backdrop.
                int none = 1, noBorder = unchecked((int)0xfffffffe);
                DwmSetWindowAttribute(window, 33, ref none, sizeof(int));
                DwmSetWindowAttribute(window, 34, ref noBorder, sizeof(int));
                DwmSetWindowAttribute(window, 38, ref none, sizeof(int));
                return ghost && result == 0;
            } catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    internal sealed class ShelfPreviewMotion
    {
        internal const int RevealMilliseconds = 160, DismissMilliseconds = 110;
        double from = 1, target = 1;
        long started;
        int duration;
        internal bool Active { get; private set; }
        internal bool Dismissing { get { return target == 0; } }
        internal void Reset(double value) { from = target = value; Active = false; }
        internal double Value(long now)
        {
            if (!Active) return target;
            double progress = Math.Max(0, Math.Min(1, (now - started) / (double)duration));
            if (progress == 1) Active = false;
            return from + (target - from) * (1 - Math.Pow(1 - progress, 3));
        }
        internal void Begin(bool reveal, long now, bool enabled)
        {
            double next = reveal ? 1 : 0;
            if (target == next) return;
            from = Value(now); target = next; started = now;
            duration = (int)Math.Ceiling((reveal ? RevealMilliseconds : DismissMilliseconds) * Math.Abs(target - from));
            Active = enabled && duration > 0;
        }
        internal static Rectangle Transform(Rectangle bounds, Rectangle final, Rectangle icon, Point host, double value)
        {
            if (icon.Width <= 0 || icon.Height <= 0) value = 1;
            double scale = 1;
            if (value < 1) scale = Math.Min(1, Math.Min(icon.Width / (double)final.Width, icon.Height / (double)final.Height));
            scale += (1 - scale) * value;
            double cx = final.X + final.Width / 2.0, cy = final.Y + final.Height / 2.0;
            double x = cx + (icon.X + icon.Width / 2.0 - cx) * (1 - value);
            double y = cy + (icon.Y + icon.Height / 2.0 - cy) * (1 - value);
            return new Rectangle((int)Math.Round(x + (bounds.X - final.Width / 2.0) * scale) - host.X,
                (int)Math.Round(y + (bounds.Y - final.Height / 2.0) * scale) - host.Y,
                Math.Max(1, (int)Math.Round(bounds.Width * scale)), Math.Max(1, (int)Math.Round(bounds.Height * scale)));
        }
    }

    internal interface IShelfThumbnailApi
    {
        int Register(IntPtr destination, IntPtr source, out IntPtr thumbnail);
        int Query(IntPtr thumbnail, out Size size);
        int Update(IntPtr thumbnail, Rectangle bounds, byte opacity);
        void Unregister(IntPtr thumbnail);
    }

    internal sealed class ShelfDwmApi : IShelfThumbnailApi
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Properties
        {
            internal uint Flags;
            internal Rect Destination, Source;
            internal byte Opacity;
            [MarshalAs(UnmanagedType.Bool)] internal bool Visible;
            [MarshalAs(UnmanagedType.Bool)] internal bool SourceClientAreaOnly;
        }
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out Size size);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref Properties properties);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
        public int Register(IntPtr destination, IntPtr source, out IntPtr thumbnail)
        {
            thumbnail = IntPtr.Zero;
            try { return DwmRegisterThumbnail(destination, source, out thumbnail); }
            catch (DllNotFoundException) { return -1; }
            catch (EntryPointNotFoundException) { return -1; }
        }
        public int Query(IntPtr thumbnail, out Size size) { return DwmQueryThumbnailSourceSize(thumbnail, out size); }
        internal static Properties ForBounds(Rectangle bounds)
        { return ForBounds(bounds, 255); }
        internal static Properties ForBounds(Rectangle bounds, byte opacity)
        {
            return new Properties { Flags = 0x1 | 0x4 | 0x8 | 0x10,
                Destination = new Rect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom },
                Opacity = opacity, Visible = opacity != 0, SourceClientAreaOnly = false };
        }
        public int Update(IntPtr thumbnail, Rectangle bounds, byte opacity)
        { var properties = ForBounds(bounds, opacity); return DwmUpdateThumbnailProperties(thumbnail, ref properties); }
        public void Unregister(IntPtr thumbnail) { DwmUnregisterThumbnail(thumbnail); }
    }

    // One page owns a bounded set of registrations. Never restore a source to obtain pixels.
    internal sealed class ShelfThumbnail : IDisposable
    {
        readonly IShelfThumbnailApi api;
        readonly Func<ShelfWindow, bool> validate;
        readonly ShelfWindow source;
        readonly IntPtr destination;
        IntPtr thumbnail;
        bool disposed;
        internal bool Available { get; private set; }
        internal bool Invalid { get; private set; }
        internal Rectangle Destination { get; private set; }
        internal ShelfThumbnail(IShelfThumbnailApi api, IntPtr destination, ShelfWindow source, Func<ShelfWindow, bool> validate)
        { this.api = api; this.destination = destination; this.source = source; this.validate = validate; }
        bool Valid()
        {
            if (disposed || Invalid) return false;
            if (validate(source)) return true;
            Invalid = true; return false;
        }
        internal void Refresh(Rectangle bounds)
        { Refresh(bounds, delegate(Rectangle fit) { return fit; }, 255); }
        internal void Refresh(Rectangle bounds, Func<Rectangle, Rectangle> transform, byte opacity)
        {
            bool keep = false;
            try {
                if (!Valid() || destination == IntPtr.Zero || bounds.Width < 1 || bounds.Height < 1) return;
                if (thumbnail == IntPtr.Zero && (api.Register(destination, source.Handle, out thumbnail) != 0 || thumbnail == IntPtr.Zero)) return;
                Size size;
                if (!Valid() || api.Query(thumbnail, out size) != 0) return;
                var fit = ShelfPreviewLayout.AspectFit(size, bounds);
                if (fit.IsEmpty || !Valid() || api.Update(thumbnail, transform(fit), opacity) != 0 || !Valid()) return;
                Destination = fit; Available = true; keep = true;
            } finally { if (!keep) Release(); }
        }
        internal bool Present(Rectangle bounds, byte opacity)
        {
            if (!Available) return true;
            bool keep = false;
            try { keep = Valid() && api.Update(thumbnail, bounds, opacity) == 0 && Valid(); return keep; }
            finally { if (!keep) Release(); }
        }
        void Release()
        {
            IntPtr owned = thumbnail; thumbnail = IntPtr.Zero; Available = false; Destination = Rectangle.Empty;
            if (owned != IntPtr.Zero) api.Unregister(owned);
        }
        public void Dispose() { if (disposed) return; disposed = true; Release(); }
    }

    // DWM paints into this top-level client area; no child HWND obscures a thumbnail.
    internal sealed class ShelfPreviewChooser : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
        readonly List<ShelfWindow> windows;
        readonly List<ShelfThumbnail> thumbnails = new List<ShelfThumbnail>();
        readonly Func<ShelfWindow, bool> validate;
        readonly Action<ShelfWindow> select;
        readonly Func<ShelfWindow, string> sourceDevice;
        readonly IShelfThumbnailApi api;
        readonly Bitmap icon;
        readonly Bitmap previousIcon, nextIcon;
        readonly Font titleFont = new Font("Segoe UI", 9);
        readonly Font captionFont = new Font("Segoe UI", 8);
        readonly ToolTip tips = new ToolTip { ShowAlways = true, InitialDelay = 500, ReshowDelay = 100 };
        readonly Timer refresh = new Timer { Interval = 500 };
        readonly Timer animation = new Timer { Interval = 15 };
        readonly Stopwatch animationClock = Stopwatch.StartNew();
        readonly ShelfPreviewMotion motion = new ShelfPreviewMotion();
        readonly ShelfPreviewLayout layout;
        readonly Display peekTarget;
        readonly ShelfPeek peek;
        readonly ShelfPeekDwell peekDwell = new ShelfPeekDwell();
        readonly Timer peekTimer = new Timer { Interval = 40 };
        IntPtr peekForeground;
        readonly int dpi;
        int page, accessibilityGeneration, hovered = -1, pressed = -1;
        Point origin;
        Size dragThreshold;
        bool dragged, releasing, ghost, paintFailed, clientAnimation, motionFailed;
        Rectangle revealOrigin;
        double visibility = 1;
        Action dismissCompleted;
        internal bool IsDismissing { get { return dismissCompleted != null || motion.Dismissing; } }
        internal bool AnimationRunning { get { return animation.Enabled; } }
        bool AnimationEnabled { get { return ShelfMotion.Allowed(clientAnimation, ghost, SystemInformation.HighContrast) && !motionFailed && !revealOrigin.IsEmpty; } }
        internal bool GhostEnabled { get { return ghost; } }
        internal ShelfPreviewLayout LayoutInfo { get { return layout; } }
        internal int Page { get { return page; } }
        internal int PreviewCount { get { return thumbnails.Count; } }
        internal bool PeekOnSourceMonitor { get; set; }
        internal IntPtr PeekAnchor { get; set; }
        internal bool PeekVisible { get { return peek.IsVisible; } }
        internal ShelfPreviewChooser(List<ShelfWindow> windows, Display target, int dpi, int bottom, Bitmap icon,
            Func<ShelfWindow, bool> validate, Action<ShelfWindow> select)
            : this(windows, target, dpi, bottom, icon, validate, select, new ShelfDwmApi()) { }
        internal ShelfPreviewChooser(List<ShelfWindow> windows, Display target, int dpi, int bottom, Bitmap icon,
            Func<ShelfWindow, bool> validate, Action<ShelfWindow> select, IShelfThumbnailApi api)
            : this(windows, target, dpi, bottom, icon, validate, select, api, ShelfPreviewAction.ReadSourceDevice) { }
        internal ShelfPreviewChooser(List<ShelfWindow> windows, Display target, int dpi, int bottom, Bitmap icon,
            Func<ShelfWindow, bool> validate, Action<ShelfWindow> select, IShelfThumbnailApi api, Func<ShelfWindow, string> sourceDevice)
        {
            this.windows = new List<ShelfWindow>(windows); this.dpi = dpi; this.validate = validate; this.select = select; this.api = api;
            this.sourceDevice = sourceDevice;
            peekTarget = target; PeekOnSourceMonitor = true; peek = new ShelfPeek(validate);
            this.icon = icon == null ? null : (Bitmap)icon.Clone();
            previousIcon = Ui.LoadToolbarIcon("play", WindowShelfPolicy.Scale(18, dpi));
            nextIcon = Ui.LoadToolbarIcon("play", WindowShelfPolicy.Scale(18, dpi));
            if (previousIcon != null) previousIcon.RotateFlip(RotateFlipType.RotateNoneFlipX);
            layout = ShelfPreviewLayout.Create(target, dpi, bottom, windows.Count);
            Text = windows.Count == 0 ? "Windows" : windows[0].AppName + " windows";
            AccessibleName = Text; AccessibleRole = AccessibleRole.List;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            Font = titleFont; Bounds = layout.Bounds; BackColor = Color.Black;
            // The premultiplied DIB must reach the DWM redirection surface unchanged.
            // WinForms background painting/double buffering can discard its alpha channel.
            DoubleBuffered = false;
            SetStyle(ControlStyles.UserMouse | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.Selectable, false);
            refresh.Tick += delegate { RefreshPreviews(); };
            animation.Tick += delegate { AdvanceAnimation(); };
            peekTimer.Tick += delegate { UpdatePeek(); };
        }
        internal void SetRevealOrigin(Rectangle screenIconBounds)
        {
            if (Visible || IsDisposed) return;
            revealOrigin = screenIconBounds.Width > 0 && screenIconBounds.Height > 0 ? screenIconBounds : Rectangle.Empty;
            Bounds = revealOrigin.IsEmpty ? layout.Bounds : Rectangle.Union(layout.Bounds, revealOrigin);
        }
        internal void BeginDismiss(Action completed)
        {
            StopPeek();
            if (IsDisposed || !Visible) { if (completed != null) completed(); return; }
            if (IsDismissing || pressed != -1) return;
            dismissCompleted = completed;
            motion.Begin(false, animationClock.ElapsedMilliseconds, AnimationEnabled);
            AdvanceAnimation();
        }
        internal void CancelDismiss()
        {
            if (IsDisposed || !Visible || !IsDismissing) return;
            dismissCompleted = null;
            motion.Begin(true, animationClock.ElapsedMilliseconds, AnimationEnabled);
            AdvanceAnimation();
        }
        void StopAnimation()
        { animation.Stop(); dismissCompleted = null; motion.Reset(1); visibility = 1; }
        void FinishDismiss()
        {
            var completed = dismissCompleted;
            StopAnimation(); Hide();
            if (completed != null) completed();
        }
        void AdvanceAnimation()
        {
            if (IsDisposed || !Visible || !IsHandleCreated) { StopAnimation(); return; }
            visibility = motion.Value(animationClock.ElapsedMilliseconds);
            if (!motion.Active && motion.Dismissing) { FinishDismiss(); return; }
            ApplyMotion();
            if (IsDisposed || !Visible) return;
            if (motion.Active) animation.Start(); else animation.Stop();
        }
        Rectangle VisualBounds(Rectangle bounds)
        { return ShelfPreviewMotion.Transform(bounds, layout.Bounds, revealOrigin, Location, visibility); }
        byte PreviewOpacity { get { return (byte)Math.Round(255 * visibility); } }
        void ApplyMotion()
        {
            try {
                for (int slot = 0; slot < thumbnails.Count; slot++) {
                    var thumbnail = thumbnails[slot];
                    if (!thumbnail.Present(VisualBounds(thumbnail.Destination), PreviewOpacity) && !thumbnail.Invalid) motionFailed = true;
                }
            } catch (Exception) { motionFailed = true; ReleasePage(); }
            if (motionFailed) {
                bool dismiss = motion.Dismissing;
                animation.Stop(); motion.Reset(dismiss ? 0 : 1); visibility = dismiss ? 0 : 1;
                if (dismiss) { FinishDismiss(); return; }
                try {
                    for (int slot = 0; slot < thumbnails.Count; slot++)
                        thumbnails[slot].Present(VisualBounds(thumbnails[slot].Destination), 255);
                } catch (Exception) { ReleasePage(); }
            }
            Invalidate();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { var value = base.CreateParams; value.ExStyle |= 0x8 | 0x80 | 0x08000000; value.ClassStyle &= ~0x20000; return value; } }
        void ConfigurePresentation()
        {
            if (!IsHandleCreated || IsDisposed) return;
            int enabled;
            clientAnimation = SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0;
            bool requested = ShelfAppearance.GhostAllowed(SystemInformation.HighContrast,
                ShelfAppearance.TransparencyEnabled(), false, paintFailed);
            ghost = ShelfPreviewGlass.Configure(Handle, requested);
            if (!AnimationEnabled) {
                if (IsDismissing) { FinishDismiss(); return; }
                StopAnimation(); ApplyMotion();
            }
            UpdateFallbackRegion(); Invalidate();
        }
        internal Region FallbackRegion()
        {
            var region = new Region(); region.MakeEmpty();
            int count = Math.Min(layout.Capacity, windows.Count - page * layout.Capacity);
            for (int slot = 0; slot < count; slot++) {
                var preview = PreviewBounds(slot); var edge = preview; edge.Inflate(1, 1);
                region.Union(VisualBounds(edge)); region.Union(VisualBounds(layout.Title(slot, preview)));
            }
            if (layout.Pages > 1) {
                region.Union(VisualBounds(layout.Previous)); region.Union(VisualBounds(layout.Next)); region.Union(VisualBounds(layout.PageLabel));
            }
            return region;
        }
        void UpdateFallbackRegion()
        {
            // Without glass, clip away the gaps instead of reverting to a shared opaque panel.
            var previous = Region;
            if (ghost && previous == null) return;
            Region = ghost ? null : FallbackRegion();
            if (previous != null) previous.Dispose();
        }
        void RaiseWithoutActivation()
        {
            // Framework Form.SetVisibleCore explicitly focuses managed TopMost forms,
            // even with ShowWithoutActivation. Maintain the native z-order instead.
            if (IsHandleCreated && !IsDisposed) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        }
        protected override void SetVisibleCore(bool value)
        { base.SetVisibleCore(value); if (value) RaiseWithoutActivation(); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
            if (m.Msg == 0x0084) {
                long position = m.LParam.ToInt64();
                var point = new Point(unchecked((short)position), unchecked((short)(position >> 16)));
                // The union host is only a drawing surface between the icon and chooser.
                if (!layout.Bounds.Contains(point)) { m.Result = new IntPtr(-1); return; }
            }
            if (m.Msg == 0x0202) {
                // WinForms releases capture before raising MouseUp. Keep this press until
                // its release is evaluated, while unrelated capture loss still cancels.
                releasing = true;
                try { base.WndProc(ref m); }
                finally { releasing = false; pressed = -1; }
                return;
            }
            base.WndProc(ref m);
            if ((m.Msg == 0x031E || m.Msg == 0x031A || m.Msg == 0x001A) && !IsDisposed) {
                ConfigurePresentation();
                if (m.Msg == 0x031E && Visible) Rebuild();
            }
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) {
                ConfigurePresentation();
                motion.Reset(AnimationEnabled ? 0 : 1); visibility = AnimationEnabled ? 0 : 1;
                motion.Begin(true, animationClock.ElapsedMilliseconds, AnimationEnabled);
                Rebuild(); refresh.Start(); AdvanceAnimation();
            }
            else { StopPeek(); StopAnimation(); refresh.Stop(); ReleasePage(); }
        }
        protected override void OnHandleDestroyed(EventArgs e)
        { StopPeek(); StopAnimation(); refresh.Stop(); ReleasePage(); base.OnHandleDestroyed(e); }
        protected override void OnHandleCreated(EventArgs e)
        { base.OnHandleCreated(e); paintFailed = false; motionFailed = false; ConfigurePresentation(); RaiseWithoutActivation(); if (Visible) { Rebuild(); refresh.Start(); } }
        void ReleasePage()
        {
            StopPeek();
            accessibilityGeneration++;
            foreach (var thumbnail in thumbnails) thumbnail.Dispose();
            thumbnails.Clear();
        }
        internal void Rebuild()
        {
            ReleasePage(); pressed = -1; hovered = -1; Capture = false; tips.SetToolTip(this, "");
            if (!IsHandleCreated || !Visible || IsDisposed) return;
            int start = page * layout.Capacity, count = Math.Min(layout.Capacity, windows.Count - start);
            for (int slot = 0; slot < count; slot++) thumbnails.Add(new ShelfThumbnail(api, Handle, windows[start + slot], validate));
            RefreshPreviews();
        }
        internal void ChangePage(int direction)
        {
            int next = Math.Max(0, Math.Min(layout.Pages - 1, page + direction));
            if (next == page) return;
            page = next; Rebuild(); AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        }
        internal void RefreshPreviews()
        {
            try {
                if (pressed != -1) return;
                for (int slot = 0; slot < thumbnails.Count; slot++) thumbnails[slot].Refresh(layout.Image(slot), VisualBounds, PreviewOpacity);
                UpdateFallbackRegion(); Invalidate();
            } catch (Exception) {
                refresh.Stop(); ReleasePage(); motionFailed = true;
                if (IsDismissing) { FinishDismiss(); return; }
                StopAnimation(); UpdateFallbackRegion(); Invalidate();
            }
        }
        int Hit(Point point)
        {
            if (layout.Pages > 1) {
                if (page > 0 && VisualBounds(layout.Previous).Contains(point)) return -2;
                if (page + 1 < layout.Pages && VisualBounds(layout.Next).Contains(point)) return -3;
            }
            int count = Math.Min(layout.Capacity, windows.Count - page * layout.Capacity);
            for (int slot = count - 1; slot >= 0; slot--) if (VisualBounds(layout.Tile(slot)).Contains(point)) return slot;
            return -1;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            StopPeek();
            pressed = e.Button == MouseButtons.Left ? Hit(e.Location) : -1;
            origin = e.Location; dragged = false;
            dragThreshold = TaskbarInput.DirectDragThreshold(SystemInformation.DragSize, (uint)dpi);
            if (pressed != -1) {
                // Hold the exact displayed geometry from press through release; neither
                // animation nor source-size refresh may move a different window under it.
                dismissCompleted = null; animation.Stop(); motion.Reset(visibility); Capture = true;
            } else CancelDismiss();
            base.OnMouseDown(e);
        }
        protected override void OnMouseEnter(EventArgs e)
        { CancelDismiss(); base.OnMouseEnter(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            CancelDismiss();
            if (pressed != -1 && WindowShelfPolicy.Dragged(origin, e.Location, dragThreshold)) dragged = true;
            int next = Hit(e.Location);
            if (hovered != next) {
                StopPeek();
                hovered = next;
                tips.SetToolTip(this, next == -2 ? "Previous windows" : next == -3 ? "Next windows" : "");
                Invalidate();
                if (next >= 0 && pressed == -1) {
                    peekForeground = GetForegroundWindow();
                    peekDwell.Update(next, animationClock.ElapsedMilliseconds, true);
                    peekTimer.Start();
                }
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { StopPeek(); hovered = -1; tips.SetToolTip(this, ""); Invalidate(); base.OnMouseLeave(e); }
        void StopPeek()
        { peekTimer.Stop(); peekDwell.Reset(); peek.Hide(); }
        void UpdatePeek()
        { UpdatePeekInput(Cursor.Position, MouseButtons, GetForegroundWindow(), animationClock.ElapsedMilliseconds); }
        internal void UpdatePeekInput(Point pointer, MouseButtons buttons, IntPtr foreground, long now)
        {
            if (IsDisposed || !Visible || IsDismissing || pressed != -1 || Capture || buttons != MouseButtons.None ||
                foreground != peekForeground || hovered < 0 || Hit(PointToClient(pointer)) != hovered) {
                StopPeek(); hovered = -1; tips.SetToolTip(this, ""); Invalidate(); return;
            }
            if (!peekDwell.Update(hovered, now, !motion.Active)) return;
            var window = windows[page * layout.Capacity + hovered];
            Rectangle bounds;
            if (hovered < thumbnails.Count && thumbnails[hovered].Available && !thumbnails[hovered].Invalid && validate(window) &&
                ShelfPeekPlacement.TryRead(window, peekTarget, PeekOnSourceMonitor, out bounds) &&
                peek.Show(window, bounds, PeekAnchor != IntPtr.Zero ? PeekAnchor : Handle)) return;
            StopPeek();
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        { if (!Capture && !releasing) { pressed = -1; ResumeReveal(); } base.OnMouseCaptureChanged(e); }
        void ResumeReveal()
        {
            if (IsDisposed || !Visible || visibility == 1 || IsDismissing) return;
            motion.Begin(true, animationClock.ElapsedMilliseconds, AnimationEnabled); AdvanceAnimation();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int expected = pressed;
            bool activate = e.Button == MouseButtons.Left && expected != -1 && expected == Hit(e.Location) && !dragged &&
                !WindowShelfPolicy.Dragged(origin, e.Location, dragThreshold);
            pressed = -1; Capture = false;
            base.OnMouseUp(e);
            if (activate) ActivateItem(expected);
            ResumeReveal();
        }
        void ActivateItem(int item)
        {
            if (IsDisposed || !Visible) return;
            StopPeek();
            if (item == -2) ChangePage(-1);
            else if (item == -3) ChangePage(1);
            else if (item >= 0 && item < thumbnails.Count && !thumbnails[item].Invalid) {
                var window = windows[page * layout.Capacity + item];
                if (validate(window)) select(window); else RefreshPreviews();
            }
        }
        static Color Ink(bool contrast) { return contrast ? SystemColors.WindowText : Color.FromArgb(235, 237, 240); }
        internal Rectangle PreviewBounds(int slot)
        { return slot < thumbnails.Count && thumbnails[slot].Available ? thumbnails[slot].Destination : layout.Image(slot); }
        internal string ActionLabel(int slot)
        {
            int index = page * layout.Capacity + slot;
            if (IsDisposed || slot < 0 || slot >= layout.Capacity || index < 0 || index >= windows.Count ||
                (slot < thumbnails.Count && thumbnails[slot].Invalid)) return String.Empty;
            return ShelfPreviewAction.Label(windows[index], peekTarget, sourceDevice);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var bitmap = Render(ghost, SystemInformation.HighContrast)) {
                // Keep the DWM thumbnail destination nonlayered. This is SRCCOPY of a
                // top-down 32-bit premultiplied DIB, NOT UpdateLayeredWindow.
                if (ShelfPaint.Present(e.Graphics, bitmap, Point.Empty)) return;
            }
            if (!paintFailed) { paintFailed = true; ConfigurePresentation(); }
            if (IsDisposed || !Visible) return;
            using (var bitmap = Render(false, SystemInformation.HighContrast)) e.Graphics.DrawImageUnscaled(bitmap, Point.Empty);
        }
        // Layout-local pixels are independent of WinForms desktop-size constraints
        // and the host's animated viewport. Render uses this same raster below.
        internal Bitmap RenderLayout(bool transparent, bool contrast)
        {
            transparent = transparent && !contrast;
            var bitmap = ShelfPaint.Canvas(layout.Bounds.Size, dpi);
            using (var graphics = Graphics.FromImage(bitmap)) PaintPreviews(graphics, transparent, contrast);
            return bitmap;
        }
        internal Bitmap Render(bool transparent, bool contrast)
        {
            transparent = transparent && !contrast;
            var bitmap = RenderLayout(transparent, contrast);
            if (visibility == 1 && ClientSize == bitmap.Size && Location == layout.Bounds.Location) return bitmap;
            using (bitmap) {
                var frame = ShelfPaint.Canvas(ClientSize, dpi);
                using (var graphics = Graphics.FromImage(frame))
                using (var attributes = new ImageAttributes()) {
                    var matrix = new ColorMatrix(); matrix.Matrix33 = transparent ? (float)visibility : 1;
                    attributes.SetColorMatrix(matrix);
                    graphics.DrawImage(bitmap, VisualBounds(new Rectangle(Point.Empty, layout.Bounds.Size)),
                        0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
                }
                return frame;
            }
        }
        void PaintPreviews(Graphics graphics, bool transparent, bool contrast)
        {
            Color surface = contrast ? SystemColors.Window : Color.FromArgb(32, 34, 38);
            graphics.Clear(Color.FromArgb(0, 0, 0, 0));
            int count = Math.Min(layout.Capacity, windows.Count - page * layout.Capacity);
            for (int slot = 0; slot < count; slot++) {
                var image = PreviewBounds(slot); var title = layout.Title(slot, image);
                bool hot = hovered == slot, invalid = slot < thumbnails.Count && thumbnails[slot].Invalid;
                bool available = slot < thumbnails.Count && thumbnails[slot].Available;
                Color ink = contrast && hot ? SystemColors.HighlightText : Ink(contrast);
                if (transparent) PaintShadow(graphics, image);
                if (!transparent || !available) using (var brush = new SolidBrush(surface)) graphics.FillRectangle(brush, image);
                // DWM composes the preview above our pixels; put the one-device-pixel
                // hover outline immediately OUTSIDE the aspect-fitted destination.
                if (hot || !transparent) using (var pen = new Pen(hot ? (contrast ? SystemColors.Highlight : Color.FromArgb(154, 220, 196)) : Ink(contrast)))
                    graphics.DrawRectangle(pen, image.X - 1, image.Y - 1, image.Width + 1, image.Height + 1);
                if (!available) {
                    int size = Math.Min(WindowShelfPolicy.Scale(32, dpi), Math.Min(image.Width, image.Height));
                    if (icon != null && size > 0) graphics.DrawImage(icon, image.X + (image.Width - size) / 2, image.Y + (image.Height - size) / 2, size, size);
                    else ShelfPaint.Text(graphics, windows[page * layout.Capacity + slot].AppName ?? "Application", Font, image, Ink(contrast), true);
                }
                if (hot || !available || !transparent) {
                    if (!transparent) using (var brush = new SolidBrush(contrast && hot ? SystemColors.Highlight : surface)) graphics.FillRectangle(brush, title);
                    string label = invalid ? String.Empty : ActionLabel(slot);
                    string name = invalid ? "Window unavailable" : windows[page * layout.Capacity + slot].ToString();
                    if (String.IsNullOrEmpty(label)) PaintLabel(graphics, name, title, ink, transparent);
                    else {
                        Rectangle nameRow, actionRow;
                        ShelfPreviewAction.CaptionRows(title, out nameRow, out actionRow);
                        PaintLabelFont(graphics, name, captionFont, nameRow, ink, transparent);
                        PaintLabelFont(graphics, label, captionFont, actionRow, ink, transparent);
                    }
                }
            }
            if (layout.Pages > 1) {
                PaintArrow(graphics, layout.Previous, false, page > 0, hovered == -2, transparent, contrast);
                PaintArrow(graphics, layout.Next, true, page + 1 < layout.Pages, hovered == -3, transparent, contrast);
                if (!transparent) using (var brush = new SolidBrush(surface)) graphics.FillRectangle(brush, layout.PageLabel);
                PaintLabel(graphics, (page + 1) + " / " + layout.Pages, layout.PageLabel, Ink(contrast), transparent);
            }
        }
        void PaintShadow(Graphics graphics, Rectangle preview)
        {
            int spread = Math.Max(1, WindowShelfPolicy.Scale(3, dpi));
            for (int distance = spread; distance >= 1; distance--) {
                var ring = preview; ring.Inflate(distance, distance); ring.Offset(0, 1);
                using (var pen = new Pen(Color.FromArgb(4 + (spread - distance + 1) * 12 / spread, 0, 0, 0)))
                    graphics.DrawRectangle(pen, ring.X, ring.Y, ring.Width - 1, ring.Height - 1);
            }
        }
        void PaintLabel(Graphics graphics, string text, Rectangle bounds, Color ink, bool transparent)
        { PaintLabelFont(graphics, text, Font, bounds, ink, transparent); }
        void PaintLabelFont(Graphics graphics, string text, Font font, Rectangle bounds, Color ink, bool transparent)
        {
            if (transparent) {
                int edge = Math.Max(1, WindowShelfPolicy.Scale(1, dpi));
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++) {
                    if (x == 0 && y == 0) continue;
                    var outline = bounds; outline.Offset(x * edge, y * edge);
                    ShelfPaint.Text(graphics, text, font, outline, Color.FromArgb(200, 0, 0, 0), true);
                }
            }
            ShelfPaint.Text(graphics, text, font, bounds, ink, true);
        }
        void PaintArrow(Graphics graphics, Rectangle bounds, bool right, bool enabled, bool hot, bool transparent, bool contrast)
        {
            if (!transparent) using (var brush = new SolidBrush(contrast ? (hot ? SystemColors.Highlight : SystemColors.Window) : Color.FromArgb(32, 34, 38))) graphics.FillRectangle(brush, bounds);
            Color ink = hot ? (contrast ? SystemColors.HighlightText : Color.FromArgb(154, 220, 196)) : Ink(contrast);
            var arrow = right ? nextIcon : previousIcon;
            if (arrow == null) PaintLabel(graphics, right ? ">" : "<", bounds, enabled ? ink : SystemColors.GrayText, transparent);
            else {
                var destination = new Rectangle(bounds.X + (bounds.Width - arrow.Width) / 2, bounds.Y + (bounds.Height - arrow.Height) / 2, arrow.Width, arrow.Height);
                Color color = enabled ? ink : contrast ? SystemColors.GrayText : Color.FromArgb(107, 112, 118);
                // Preserve the toolbar artwork's alpha, with readable ink in either Windows theme.
                using (var attributes = new ImageAttributes()) {
                    attributes.SetColorMatrix(new ColorMatrix(new[] {
                        new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 }, new[] { color.R / 255f, color.G / 255f, color.B / 255f, 0, 1 } }));
                    graphics.DrawImage(arrow, destination, 0, 0, arrow.Width, arrow.Height, GraphicsUnit.Pixel, attributes);
                }
            }
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new PreviewAccessible(this); }
        sealed class PreviewAccessible : ControlAccessibleObject
        {
            readonly ShelfPreviewChooser owner;
            readonly List<AccessibleObject> children = new List<AccessibleObject>();
            int generation = -1;
            internal PreviewAccessible(ShelfPreviewChooser owner) : base(owner) { this.owner = owner; }
            void EnsureChildren()
            {
                if (generation == owner.accessibilityGeneration) return;
                generation = owner.accessibilityGeneration; children.Clear();
                if (owner.IsDisposed || !owner.Visible) return;
                int count = Math.Min(owner.layout.Capacity, owner.windows.Count - owner.page * owner.layout.Capacity);
                for (int index = 0; index < count; index++) children.Add(new ItemAccessible(owner, this, index, owner.page, generation));
                if (owner.layout.Pages > 1) {
                    children.Add(new ItemAccessible(owner, this, -2, owner.page, generation));
                    children.Add(new ItemAccessible(owner, this, -3, owner.page, generation));
                }
            }
            public override int GetChildCount() { EnsureChildren(); return children.Count; }
            public override AccessibleObject GetChild(int index)
            {
                EnsureChildren(); return index < 0 || index >= children.Count ? null : children[index];
            }
        }
        sealed class ItemAccessible : AccessibleObject
        {
            readonly ShelfPreviewChooser owner;
            readonly AccessibleObject parent;
            readonly int item, page, generation;
            internal ItemAccessible(ShelfPreviewChooser owner, AccessibleObject parent, int item, int page, int generation)
            { this.owner = owner; this.parent = parent; this.item = item; this.page = page; this.generation = generation; }
            bool Current { get { return !owner.IsDisposed && owner.Visible && page == owner.page && generation == owner.accessibilityGeneration; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override string Name { get { return item == -2 ? "Previous windows" : item == -3 ? "Next windows" : owner.windows[page * owner.layout.Capacity + item].ToString(); } set { } }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            string ActionLabel { get { return Current && State != AccessibleStates.Unavailable ? owner.ActionLabel(item) : String.Empty; } }
            public override string DefaultAction { get { return item < 0 ? "Change page" : ShelfPreviewAction.DefaultAction(ActionLabel); } }
            public override string Description { get { return item < 0 ? base.Description : ShelfPreviewAction.Description(ActionLabel); } }
            public override Rectangle Bounds { get { return Current ? owner.RectangleToScreen(owner.VisualBounds(item == -2 ? owner.layout.Previous : item == -3 ? owner.layout.Next : owner.layout.Tile(item))) : Rectangle.Empty; } }
            public override AccessibleStates State { get { return !Current || (item == -2 && page == 0) || (item == -3 && page + 1 == owner.layout.Pages) ||
                (item >= 0 && (item >= owner.thumbnails.Count || owner.thumbnails[item].Invalid)) ? AccessibleStates.Unavailable : AccessibleStates.None; } }
            public override void DoDefaultAction() { if (Current && State != AccessibleStates.Unavailable) owner.ActivateItem(item); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { StopAnimation(); refresh.Stop(); ReleasePage(); }
            base.Dispose(disposing);
            if (disposing) {
                peekTimer.Dispose(); peek.Dispose(); animation.Dispose(); refresh.Dispose(); tips.Dispose(); titleFont.Dispose(); captionFont.Dispose(); if (icon != null) icon.Dispose();
                if (previousIcon != null) previousIcon.Dispose(); if (nextIcon != null) nextIcon.Dispose();
            }
        }
    }
}
