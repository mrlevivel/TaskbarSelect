using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Decoration stays on this application's own HWNDs. None of these helpers
    // alters the Windows theme, display configuration, or another app's frame.
    internal static class NativeChrome
    {
        static readonly ConditionalWeakTable<Control, ThemeRegistration> registrations = new ConditionalWeakTable<Control, ThemeRegistration>();

        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0) return path;
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            if (radius < 1) { path.AddRectangle(bounds); return path; }
            float diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }

        internal static void ApplyTheme(Control owner)
        {
            if (!(owner is Form) && !(owner is ScrollableControl) && !(owner is ComboBox) && !(owner is ListView)) return;
            registrations.GetValue(owner, delegate(Control control) { return new ThemeRegistration(control); }).Refresh();
        }
        internal static void Apply(Form owner) { ApplyTheme(owner); }
        internal static void ApplyScrollbars(Control owner) { ApplyTheme(owner); }

        sealed class ThemeRegistration
        {
            readonly Control owner;
            internal ThemeRegistration(Control control)
            {
                owner = control;
                owner.HandleCreated += delegate { Refresh(); };
            }
            internal void Refresh()
            {
                // Hidden layout fixtures must not create a native window here.
                if (owner.IsDisposed || !owner.IsHandleCreated) return;
                try {
                    bool dark = Ui.Dark && !SystemInformation.HighContrast;
                    SetWindowTheme(owner.Handle, dark ? "DarkMode_Explorer" : null, null);
                    if (owner is Form) {
                        int value = dark ? 1 : 0;
                        if (DwmSetWindowAttribute(owner.Handle, 20, ref value, sizeof(int)) != 0)
                            DwmSetWindowAttribute(owner.Handle, 19, ref value, sizeof(int));
                        int caption = SystemInformation.HighContrast ? -1 : ColorRef(Ui.Background);
                        int text = SystemInformation.HighContrast ? -1 : ColorRef(Ui.Text);
                        int border = SystemInformation.HighContrast ? -1 : ColorRef(Ui.Line);
                        // Windows 11 supports these colors; older systems safely reject them.
                        DwmSetWindowAttribute(owner.Handle, 35, ref caption, sizeof(int));
                        DwmSetWindowAttribute(owner.Handle, 36, ref text, sizeof(int));
                        DwmSetWindowAttribute(owner.Handle, 34, ref border, sizeof(int));
                    }
                } catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
        }
        static int ColorRef(Color value) { return value.R | (value.G << 8) | (value.B << 16); }
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }

    // A native Button retains keyboard, mnemonic, dialog, and accessibility
    // behavior. Only its paint changes; its preferred-size contract is unchanged.
    internal sealed class AppButton : Button
    {
        bool hover, pressed;
        internal bool? IconOnDarkSurface;
        internal AppButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) pressed = true; Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) pressed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); pressed = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color surrounding = Parent == null ? Ui.Background : Parent.BackColor;
            e.Graphics.Clear(surrounding);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool hot = Enabled && (hover || pressed);
            Color background = hot ? Ui.Hover : SystemInformation.HighContrast ? Ui.Surface : BackColor;
            Color foreground = !Enabled ? (SystemInformation.HighContrast ? SystemColors.GrayText : Ui.Muted) :
                SystemInformation.HighContrast && hot ? SystemColors.HighlightText : Ui.Text;
            RectangleF face = new RectangleF(0.5f, 0.5f, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            using (var path = NativeChrome.Rounded(face, SystemInformation.HighContrast ? 0 : 3 * DeviceDpi / 96f)) {
                using (var fill = new SolidBrush(background)) e.Graphics.FillPath(fill, path);
                using (var line = new Pen(Focused && ShowFocusCues ? Ui.Accent : Ui.Line)) e.Graphics.DrawPath(line, path);
            }
            Rectangle content = new Rectangle(Padding.Left, Padding.Top, Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical));
            TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            int textWidth = TextRenderer.MeasureText(e.Graphics, Text, Font, new Size(Int32.MaxValue, content.Height), flags).Width;
            int imageWidth = Image == null ? 0 : Image.Width;
            int gap = Image == null || String.IsNullOrEmpty(Text) ? 0 : Math.Max(4, (int)Math.Ceiling(6 * DeviceDpi / 96f));
            int groupWidth = imageWidth + gap + Math.Min(textWidth, Math.Max(1, content.Width - imageWidth - gap));
            int left = content.Left + Math.Max(0, (content.Width - groupWidth) / 2);
            if (Image != null) {
                int top = content.Top + (content.Height - Image.Height) / 2;
                if (Enabled) e.Graphics.DrawImage(Image, left, top, Image.Width, Image.Height);
                else ControlPaint.DrawImageDisabled(e.Graphics, Image, left, top, background);
            }
            Rectangle textBounds = new Rectangle(left + imageWidth + gap, content.Top,
                Math.Max(1, Math.Min(textWidth, content.Right - left - imageWidth - gap)), content.Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, foreground, flags | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), foreground, background);
        }
    }

    // The real NumericUpDown owns editing, range validation, arrow keys, mouse
    // repeat, and accessibility. We theme its existing arrow child, not an input proxy.
    internal sealed class ThemedNumericUpDown : NumericUpDown
    {
        SpinnerChrome spinner;
        internal ThemedNumericUpDown() { BorderStyle = BorderStyle.None; BackColor = Ui.Surface; ForeColor = Ui.Text; }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); RefreshChrome();
        }
        protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); RefreshChrome(); }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); RefreshChrome(); }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); RefreshChrome(); }
        internal void RefreshChrome()
        {
            foreach (Control child in Controls) {
                child.BackColor = Ui.Surface; child.ForeColor = Ui.Text;
                if (!(child is TextBox) && spinner == null) spinner = new SpinnerChrome(this, child);
                child.Invalidate();
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && spinner != null) { spinner.Dispose(); spinner = null; }
            base.Dispose(disposing);
        }

        sealed class SpinnerChrome : NativeWindow, IDisposable
        {
            readonly ThemedNumericUpDown owner;
            readonly Control arrows;
            int hover, pressed;
            internal SpinnerChrome(ThemedNumericUpDown input, Control child)
            {
                owner = input; arrows = child;
                arrows.HandleCreated += HandleCreated; arrows.HandleDestroyed += HandleDestroyed;
                arrows.MouseMove += MouseMove; arrows.MouseLeave += MouseLeave;
                arrows.MouseDown += MouseDown; arrows.MouseUp += MouseUp;
                arrows.EnabledChanged += EnabledChanged;
                if (arrows.IsHandleCreated) AssignHandle(arrows.Handle);
            }
            void HandleCreated(object sender, EventArgs e) { if (Handle == IntPtr.Zero) AssignHandle(arrows.Handle); }
            void HandleDestroyed(object sender, EventArgs e) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
            void MouseMove(object sender, MouseEventArgs e) { int next = e.Y < arrows.Height / 2 ? 1 : 2; if (hover != next) { hover = next; arrows.Invalidate(); } }
            void MouseLeave(object sender, EventArgs e) { hover = 0; pressed = 0; arrows.Invalidate(); }
            void MouseDown(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = e.Y < arrows.Height / 2 ? 1 : 2; arrows.Invalidate(); }
            void MouseUp(object sender, MouseEventArgs e) { pressed = 0; arrows.Invalidate(); }
            void EnabledChanged(object sender, EventArgs e) { pressed = 0; hover = 0; arrows.Invalidate(); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0014) { message.Result = new IntPtr(1); return; }
                if (message.Msg == 0x000F) {
                    PaintStruct paint;
                    IntPtr dc = BeginPaint(message.HWnd, out paint);
                    try { if (dc != IntPtr.Zero) using (var graphics = Graphics.FromHdc(dc)) Paint(graphics); }
                    finally { EndPaint(message.HWnd, ref paint); }
                    message.Result = IntPtr.Zero; return;
                }
                if ((message.Msg == 0x0317 || message.Msg == 0x0318) && message.WParam != IntPtr.Zero) {
                    using (var graphics = Graphics.FromHdc(message.WParam)) Paint(graphics);
                    message.Result = IntPtr.Zero; return;
                }
                base.WndProc(ref message);
            }
            void Paint(Graphics graphics)
            {
                graphics.Clear(Ui.Surface); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = owner.DeviceDpi / 96f;
                int half = Math.Max(1, arrows.Height / 2);
                for (int i = 1; i <= 2; i++) {
                    Rectangle part = new Rectangle(0, i == 1 ? 0 : half, arrows.Width, i == 1 ? half : arrows.Height - half);
                    bool hot = owner.Enabled && (pressed == i || hover == i);
                    if (hot) using (var fill = new SolidBrush(Ui.Hover)) graphics.FillRectangle(fill, part);
                    Color color = !owner.Enabled ? (SystemInformation.HighContrast ? SystemColors.GrayText : Ui.Muted) :
                        SystemInformation.HighContrast && hot ? SystemColors.HighlightText : Ui.Muted;
                    float x = part.Left + part.Width / 2f, y = part.Top + part.Height / 2f;
                    float direction = i == 1 ? -1 : 1;
                    using (var pen = new Pen(color, Math.Max(1, scale))) graphics.DrawLines(pen, new[] {
                        new PointF(x - 3 * scale, y - direction * scale),
                        new PointF(x, y + direction * scale),
                        new PointF(x + 3 * scale, y - direction * scale)
                    });
                }
            }
            public void Dispose()
            {
                arrows.HandleCreated -= HandleCreated; arrows.HandleDestroyed -= HandleDestroyed;
                arrows.MouseMove -= MouseMove; arrows.MouseLeave -= MouseLeave;
                arrows.MouseDown -= MouseDown; arrows.MouseUp -= MouseUp; arrows.EnabledChanged -= EnabledChanged;
                if (Handle != IntPtr.Zero) ReleaseHandle();
            }
            [StructLayout(LayoutKind.Sequential)] struct PaintStruct
            {
                public IntPtr Hdc;
                public int Erase, Left, Top, Right, Bottom, Restore, IncUpdate;
                public int Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7;
            }
            [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);
            [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);
        }
    }
}
