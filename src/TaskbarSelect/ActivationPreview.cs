using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class ShelfActivationPreviewLayout
    {
        internal Rectangle Area, Label, Bounds;
        internal int Dpi;
        internal static ShelfActivationPreviewLayout Create(Display display, int width, int dpi)
        { return Create(display, width, WindowShelfPolicy.DefaultActivationHeight, dpi); }
        internal static ShelfActivationPreviewLayout Create(Display display, int width, int dpi, Rectangle work)
        { return Create(display, width, WindowShelfPolicy.DefaultActivationHeight, dpi, work); }
        internal static ShelfActivationPreviewLayout Create(Display display, int width, int height, int dpi)
        { return Create(display, width, height, dpi, display == null ? Rectangle.Empty : WindowShelfPolicy.Bounds(display)); }
        internal static ShelfActivationPreviewLayout Create(Display display, int width, int height, int dpi, Rectangle work)
        {
            if (display == null || display.Width <= 0 || display.Height <= 0) throw new ArgumentException("Invalid preview display");
            dpi = Math.Max(96, Math.Min(384, dpi));
            Rectangle monitor = WindowShelfPolicy.Bounds(display), area = WindowShelfPolicy.Trigger(display, width, height);
            if (work.Width <= 0 || work.Height <= 0 || !monitor.Contains(work)) work = monitor;
            int labelWidth = Math.Min(work.Width, WindowShelfPolicy.Scale(240, dpi));
            int labelHeight = Math.Min(work.Height, WindowShelfPolicy.Scale(32, dpi));
            int x = Math.Max(work.Left, Math.Min(work.Right - labelWidth, area.Left + (area.Width - labelWidth) / 2));
            int y = Math.Max(work.Top, Math.Min(work.Bottom - labelHeight, area.Top - WindowShelfPolicy.Scale(8, dpi) - labelHeight));
            Rectangle label = new Rectangle(x, y, labelWidth, labelHeight);
            return new ShelfActivationPreviewLayout { Area = area, Label = label, Bounds = Rectangle.Union(area, label), Dpi = dpi };
        }
    }

    internal interface IShelfActivationPreviewSurface : IDisposable
    {
        void Present(ShelfActivationPreviewLayout layout);
    }

    // The caller supplies eligible Ghost Bar targets. This service never changes settings,
    // taskbars, work areas or activation state. Injected surfaces keep lifecycle tests pure.
    internal sealed class ShelfActivationPreview : IDisposable
    {
        readonly Func<IShelfActivationPreviewSurface> create;
        readonly Dictionary<string, IShelfActivationPreviewSurface> surfaces = new Dictionary<string, IShelfActivationPreviewSurface>();
        bool disposed;
        internal ShelfActivationPreview() : this(delegate { return new ShelfActivationPreviewSurface(); }) { }
        internal ShelfActivationPreview(Func<IShelfActivationPreviewSurface> create)
        { if (create == null) throw new ArgumentNullException("create"); this.create = create; }
        internal int Count { get { return surfaces.Count; } }
        internal void Update(IEnumerable<Display> targets, int width, Func<Display, int> readDpi, Func<Display, Rectangle> readWorkArea = null)
        { Update(targets, width, WindowShelfPolicy.DefaultActivationHeight, readDpi, readWorkArea); }
        internal void Update(IEnumerable<Display> targets, int width, int height, Func<Display, int> readDpi, Func<Display, Rectangle> readWorkArea = null)
        {
            if (disposed) return;
            var next = (targets ?? Enumerable.Empty<Display>()).Where(d => d != null && !d.Primary &&
                d.Width >= 160 && d.Height >= 160 && !String.IsNullOrWhiteSpace(d.Device) && !String.IsNullOrWhiteSpace(d.Identity)).ToList();
            next = next.Where(d => next.Count(other => other.Identity == d.Identity) == 1).ToList();
            var retained = new HashSet<string>();
            try {
                foreach (Display display in next) {
                    string key = display.Device + "|" + display.Identity;
                    retained.Add(key);
                    IShelfActivationPreviewSurface surface;
                    if (!surfaces.TryGetValue(key, out surface)) {
                        surface = create();
                        if (surface == null) throw new InvalidOperationException("Preview surface unavailable");
                        surfaces.Add(key, surface);
                    }
                    surface.Present(ShelfActivationPreviewLayout.Create(display, width, height, readDpi == null ? 96 : readDpi(display),
                        readWorkArea == null ? WindowShelfPolicy.Bounds(display) : readWorkArea(display)));
                }
                foreach (string key in surfaces.Keys.Where(key => !retained.Contains(key)).ToArray()) {
                    var surface = surfaces[key]; surfaces.Remove(key); surface.Dispose();
                }
            } catch { Hide(); throw; }
        }
        internal void Hide()
        {
            var closing = surfaces.Values.ToArray(); surfaces.Clear();
            Exception failure = null;
            foreach (var surface in closing) {
                try { surface.Dispose(); }
                catch (Exception error) { if (failure == null) failure = error; }
            }
            if (failure != null) throw failure;
        }
        public void Dispose() { if (disposed) return; disposed = true; Hide(); }
    }

    internal sealed class ShelfActivationPreviewSurface : Form, IShelfActivationPreviewSurface
    {
        const int NoActivate = 0x08000000, Transparent = 0x20, ToolWindow = 0x80, Layered = 0x80000;
        const int HitTest = 0x84, MouseActivate = 0x21;
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        ShelfActivationPreviewLayout layout;
        internal ShelfActivationPreviewSurface()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; BackColor = Color.Magenta; TransparencyKey = Color.Magenta;
            Text = "Ghost Bar activation area preview"; AccessibleName = Text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { var parameters = base.CreateParams; parameters.ExStyle |= NoActivate | Transparent | ToolWindow | Layered; return parameters; } }
        public void Present(ShelfActivationPreviewLayout next)
        {
            if (IsDisposed) return;
            layout = next;
            AccessibleDescription = "Preview of the Ghost Bar activation area: " + next.Area.Width + " physical pixels wide and " + next.Area.Height + " pixels high. Clicks pass through.";
            Bounds = next.Bounds;
            if (!Visible) Show();
            if (!SetWindowPos(Handle, new IntPtr(-1), next.Bounds.X, next.Bounds.Y, next.Bounds.Width, next.Bounds.Height, 0x10 | 0x40))
                throw new InvalidOperationException("Could not position activation preview");
            Invalidate();
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HitTest) { message.Result = new IntPtr(-1); return; }
            if (message.Msg == MouseActivate) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (layout == null) return;
            Rectangle area = layout.Area, label = layout.Label;
            area.Offset(-layout.Bounds.X, -layout.Bounds.Y); label.Offset(-layout.Bounds.X, -layout.Bounds.Y);
            using (var fill = new SolidBrush(Color.FromArgb(65, 220, 255))) e.Graphics.FillRectangle(fill, area);
            using (var border = new Pen(Color.White)) e.Graphics.DrawRectangle(border, area.X, area.Y, area.Width - 1, area.Height - 1);
            using (var fill = new SolidBrush(Color.FromArgb(24, 35, 44))) e.Graphics.FillRectangle(fill, label);
            using (var border = new Pen(Color.FromArgb(65, 220, 255))) e.Graphics.DrawRectangle(border, label.X, label.Y, label.Width - 1, label.Height - 1);
            using (var font = new Font("Segoe UI", WindowShelfPolicy.Scale(13, layout.Dpi), FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(e.Graphics, "Ghost Bar · " + layout.Area.Width + " × " + layout.Area.Height + " px", font, label, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}
