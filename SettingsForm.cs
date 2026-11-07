using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class Ui
    {
        internal static readonly bool Dark = ReadDark();
        internal static Color Background { get { return SystemInformation.HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(25, 27, 31) : Color.FromArgb(246, 248, 250); } }
        internal static Color Card { get { return SystemInformation.HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(31, 34, 39) : Color.White; } }
        internal static Color Surface { get { return SystemInformation.HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(35, 39, 45) : Color.FromArgb(248, 250, 252); } }
        internal static Color Hover { get { return SystemInformation.HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(43, 48, 55) : Color.FromArgb(234, 240, 245); } }
        internal static Color Text { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(235, 239, 244) : Color.FromArgb(32, 39, 48); } }
        internal static Color Muted { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(167, 177, 189) : Color.FromArgb(93, 107, 124); } }
        internal static Color Line { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(57, 63, 72) : Color.FromArgb(216, 223, 232); } }
        static string accentPreset = "Original";
        internal static Color Accent { get { return SystemInformation.HighContrast ? SystemColors.Highlight : FocusAppearance.Accent(accentPreset, Dark); } }
        // Only the live app controller applies this. Constructing a hidden form/test controller
        // must not change the palette of other windows or fixtures in the same process.
        internal static bool ApplyAccent(string preset)
        {
            string next = FocusAppearance.NormalizeAccent(preset);
            if (next == accentPreset) return false;
            accentPreset = next; return true;
        }
        static bool ReadDark()
        {
            try { using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key != null && Object.Equals(key.GetValue("AppsUseLightTheme"), 0); }
            catch (System.Security.SecurityException) { return false; }
        }
        internal static Label Label(string text, int height = 26)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, Height = height, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
        }
        internal static Button Button(string text, string icon, EventHandler action, int iconSize = 18)
        {
            var button = new AppButton { Text = text, AutoSize = true, MinimumSize = new Size(36, 34), Height = 34,
                FlatStyle = FlatStyle.Flat, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(4, 0, 0, 0),
                TextImageRelation = TextImageRelation.ImageBeforeText, UseVisualStyleBackColor = false, BackColor = Surface };
            button.FlatAppearance.BorderColor = Line;
            button.FlatAppearance.MouseOverBackColor = Hover;
            button.FlatAppearance.MouseDownBackColor = Hover;
            if (icon != null) {
                SetIcon(button, icon, iconSize);
                button.Disposed += delegate { if (button.Image != null) button.Image.Dispose(); };
            }
            button.Click += action;
            return button;
        }
        internal static void SetIcon(Button button, string name, int size = 18)
        {
            var appButton = button as AppButton;
            bool darkSurface = ToolbarDarkSurface;
            if (Object.Equals(button.Tag, name) && (appButton == null || appButton.IconOnDarkSurface == darkSurface)) return;
            var image = LoadToolbarIcon(name, size, darkSurface);
            if (image == null) return;
            var previous = button.Image;
            button.Image = image; button.Tag = name;
            if (appButton != null) appButton.IconOnDarkSurface = darkSurface;
            if (previous != null) previous.Dispose();
        }
        internal static bool ToolbarDarkSurface { get { return SystemInformation.HighContrast ? SystemColors.Window.GetBrightness() < 0.5f : Dark; } }
        internal static Bitmap LoadToolbarIcon(string name, int size)
        { return LoadToolbarIcon(name, size, ToolbarDarkSurface); }
        static Bitmap LoadToolbarIcon(string name, int size, bool darkSurface)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TaskbarSelect.Toolbar." + name + "-" + (darkSurface ? "dark" : "light") + ".png")) {
                if (stream == null) return null;
                using (var image = Image.FromStream(stream)) return new Bitmap(image, size, size);
            }
        }
        internal static void Theme(Control root)
        {
            Theme(root, root is SettingsCard);
        }
        static void Theme(Control root, bool inCard)
        {
            inCard = inCard || root is SettingsCard;
            root.ForeColor = root is SectionHeader ? Muted : Text;
            if (!(root is Button) && !(root is DisplayMap) && !(root is FieldFrame) && !(root is SectionRule)) root.BackColor = inCard ? Card : Background;
            if (root is TextBox || root is ListView || root is ThemedComboBox || root is NumericUpDown || root is FieldFrame) root.BackColor = Surface;
            if (root is SectionRule) root.BackColor = Line;
            var button = root as AppButton;
            if (button != null) {
                button.BackColor = Surface;
                button.FlatAppearance.BorderColor = Line;
                button.FlatAppearance.MouseOverBackColor = Hover;
                button.FlatAppearance.MouseDownBackColor = Hover;
                string icon = button.Tag as string;
                if (icon != null && button.Image != null) SetIcon(button, icon, button.Image.Width);
            }
            NativeChrome.ApplyTheme(root);
            foreach (Control child in root.Controls) Theme(child, inCard);
            var numeric = root as ThemedNumericUpDown;
            if (numeric != null) numeric.RefreshChrome();
        }
    }

    internal sealed class SectionHeader : Label
    {
        internal SectionHeader(string text)
        {
            Text = text; Dock = DockStyle.Fill; Margin = Padding.Empty; TextAlign = ContentAlignment.MiddleLeft; UseMnemonic = false;
            Font = new Font("Segoe UI", 9, FontStyle.Bold); ForeColor = Ui.Muted; AccessibleName = text;
        }
    }

    internal sealed class SectionRule : Panel
    {
        internal SectionRule() { Dock = DockStyle.Fill; Margin = Padding.Empty; BackColor = Ui.Line; }
    }

    internal sealed class ThemedTabControl : TabControl
    {
        int hovered = -1;
        internal ThemedTabControl()
        {
            Appearance = TabAppearance.Normal; SizeMode = TabSizeMode.Fixed;
            ItemSize = SettingsForm.TabItemSize(DeviceDpi);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ItemSize = SettingsForm.TabItemSize(DeviceDpi);
            Invalidate();
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int next = -1;
            for (int i = 0; i < TabCount; i++) if (GetTabRect(i).Contains(e.Location)) { next = i; break; }
            if (next != hovered) { hovered = next; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = -1; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Ui.Background);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (TabCount == 0) return;
            int separator = GetTabRect(0).Bottom;
            using (var line = new Pen(Ui.Line))
                e.Graphics.DrawLine(line, 0, separator, Width - 1, separator);
            for (int index = 0; index < TabCount; index++) {
                Rectangle bounds = GetTabRect(index);
                bool selected = index == SelectedIndex;
                Rectangle face = Rectangle.Inflate(bounds, -2, 0);
                if (selected || hovered == index) {
                    using (var path = NativeChrome.Rounded(face, Math.Max(1, 3 * DeviceDpi / 96f)))
                    using (var fill = new SolidBrush(selected ? Ui.Card : Ui.Hover)) e.Graphics.FillPath(fill, path);
                }
                if (selected) using (var accent = new SolidBrush(Ui.Accent))
                    e.Graphics.FillRectangle(accent, bounds.Left + 12, bounds.Bottom - Math.Max(2, (int)Math.Ceiling(2 * DeviceDpi / 96f)), bounds.Width - 24, Math.Max(2, (int)Math.Ceiling(2 * DeviceDpi / 96f)));
                Color text = SystemInformation.HighContrast && hovered == index && !selected ? SystemColors.HighlightText : selected ? Ui.Text : Ui.Muted;
                TextRenderer.DrawText(e.Graphics, TabPages[index].Text, Font, bounds, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (selected && Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -5, -5));
            }
        }
    }

    internal sealed class ThemedComboBox : ComboBox
    {
        internal ThemedComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = Math.Max(20, Font.Height + 6);
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = Math.Max(20, Font.Height + (int)Math.Ceiling(6 * DeviceDpi / 96f)); }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            Color background = selected ? Ui.Hover : Ui.Surface;
            Color foreground = !Enabled ? Ui.Muted : SystemInformation.HighContrast && selected ? SystemColors.HighlightText : Ui.Text;
            using (var fill = new SolidBrush(background)) e.Graphics.FillRectangle(fill, e.Bounds);
            string value = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
            Rectangle text = e.Bounds; text.X += (int)Math.Ceiling(8 * DeviceDpi / 96f); text.Width = Math.Max(1, text.Width - (int)Math.Ceiling(12 * DeviceDpi / 96f));
            TextRenderer.DrawText(e.Graphics, value, Font, text, foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0 && ShowFocusCues) e.DrawFocusRectangle();
        }
        protected override void WndProc(ref Message m)
        {
            if ((m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero) {
                // Native owner drawing can omit the selected item for an
                // off-screen WM_PRINT. Render our closed value into the supplied
                // DC as well as its chrome; no screen DC or focus change needed.
                using (var graphics = Graphics.FromHdc(m.WParam)) PaintClosed(graphics);
                m.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref m);
            if (IsHandleCreated && (m.Msg == 0x000F || m.Msg == 0x0085 || m.Msg == 0x0007 || m.Msg == 0x0008)) {
                using (var graphics = Graphics.FromHwnd(Handle)) PaintClosed(graphics);
            }
        }
        internal void PaintClosed(Graphics graphics)
        {
            using (var fill = new SolidBrush(Ui.Surface)) graphics.FillRectangle(fill, ClientRectangle);
            int inset = (int)Math.Ceiling(8 * DeviceDpi / 96f);
            int arrowWidth = (int)Math.Ceiling(22 * DeviceDpi / 96f);
            Rectangle text = new Rectangle(inset, 1, Math.Max(1, Width - inset - arrowWidth - 2), Math.Max(1, Height - 2));
            string value = SelectedIndex >= 0 && SelectedIndex < Items.Count ? GetItemText(Items[SelectedIndex]) : Text;
            TextRenderer.DrawText(graphics, value, Font, text, Enabled ? Ui.Text : Ui.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            PaintChrome(graphics);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(text, -1, -2), Ui.Text, Ui.Surface);
        }
        internal void PaintChrome(Graphics graphics)
        {
            int borderWidth = Math.Max(1, (int)Math.Ceiling(DeviceDpi / 96f));
            int arrowWidth = (int)Math.Ceiling(22 * DeviceDpi / 96f);
            Rectangle arrow = new Rectangle(Math.Max(1, Width - arrowWidth), 1, Math.Min(arrowWidth - 1, Width - 2), Math.Max(1, Height - 2));
            using (var fill = new SolidBrush(Ui.Surface)) graphics.FillRectangle(fill, arrow);
            using (var line = new Pen(Focused ? Ui.Accent : Ui.Line)) {
                for (int inset = 0; inset < borderWidth; inset++)
                    graphics.DrawRectangle(line, inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
            }
            int centerX = arrow.Left + arrow.Width / 2, centerY = arrow.Top + arrow.Height / 2;
            int halfWidth = (int)Math.Round(4 * DeviceDpi / 96f, MidpointRounding.AwayFromZero);
            int rise = (int)Math.Round(2 * DeviceDpi / 96f, MidpointRounding.AwayFromZero);
            using (var chevron = new Pen(Ui.Muted, Math.Max(1, DeviceDpi / 96f))) {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.DrawLines(chevron, new[] {
                    new Point(centerX - halfWidth, centerY - rise),
                    new Point(centerX, centerY + rise),
                    new Point(centerX + halfWidth, centerY - rise)
                });
            }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }

    internal sealed class FieldFrame : Panel
    {
        internal FieldFrame(Control input)
        {
            BackColor = Ui.Surface; Padding = input is ThemedComboBox ? Padding.Empty : new Padding(8, 2, 8, 2);
            Height = Math.Max(30, input.PreferredSize.Height + Padding.Vertical + 2);
            TabStop = false;
            input.Dock = DockStyle.Fill; input.Margin = Padding.Empty;
            Controls.Add(input);
            input.Enter += delegate { Invalidate(); };
            input.Leave += delegate { Invalidate(); };
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            int required = Controls.Count == 0 ? 0 : Controls[0].PreferredSize.Height + Padding.Vertical + 2;
            if (Height < required) Height = required;
            base.OnLayout(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!(Controls.Count > 0 && Controls[0] is ThemedComboBox))
                using (var pen = new Pen(ContainsFocus ? Ui.Accent : Ui.Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class ToggleSwitch : CheckBox
    {
        bool busy;
        // Keep the native checkbox enabled (and focused) while an asynchronous
        // operation owns it. Disabling a focused WinForms control selects the
        // next editor, which can start an unrelated preview or consume typing.
        internal bool Busy
        {
            get { return busy; }
            set {
                if (busy == value) return;
                busy = value; AutoCheck = !value; Invalidate();
                if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            }
        }
        internal ToggleSwitch(string text)
        {
            Text = text; AutoSize = false; Dock = DockStyle.Fill; Height = 40;
            AccessibleName = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        internal void FitText()
        {
            float scale = DeviceDpi / 96f;
            int labelWidth = TextRenderer.MeasureText(Text, Font, new Size(Int32.MaxValue, Height),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            Width = (int)Math.Ceiling(50 * scale) + labelWidth + (int)Math.Ceiling(8 * scale);
        }
        internal Rectangle TextBounds {
            get {
                float scale = DeviceDpi / 96f;
                int left = (int)(38 * scale) + (int)(12 * scale);
                return new Rectangle(left, 0, Math.Max(1, Width - left), Height);
            }
        }
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (Parent != null) FitText();
        }
        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (Parent != null) FitText();
        }
        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            FitText();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool interactive = Enabled && !Busy;
            float scale = DeviceDpi / 96f;
            int w = (int)(38 * scale), h = (int)(20 * scale), y = (Height - h) / 2;
            using (var path = NativeChrome.Rounded(new RectangleF(0, y, w, h), h / 2f)) {
                Color track = interactive && Checked ? Ui.Accent : Ui.Line;
                using (var fill = new SolidBrush(track)) e.Graphics.FillPath(fill, path);
                if (SystemInformation.HighContrast) using (var border = new Pen(SystemColors.WindowText)) e.Graphics.DrawPath(border, path);
            }
            int inset = (int)(3 * scale), size = h - 2 * inset;
            Color thumb = SystemInformation.HighContrast ? (Checked && interactive ? SystemColors.HighlightText : SystemColors.Window) : Checked && interactive ? Ui.Background : Ui.Text;
            using (var fill = new SolidBrush(thumb))
                e.Graphics.FillEllipse(fill, Checked ? w - inset - size : inset, y + inset, size, size);
            TextRenderer.DrawText(e.Graphics, Text, Font, TextBounds, interactive ? Ui.Text : Ui.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle, Ui.Text, BackColor);
        }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnClick(EventArgs e) { if (!Busy) base.OnClick(e); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new BusyToggleAccessibleObject(this); }
        sealed class BusyToggleAccessibleObject : CheckBoxAccessibleObject
        {
            readonly ToggleSwitch owner;
            internal BusyToggleAccessibleObject(ToggleSwitch owner) : base(owner) { this.owner = owner; }
            public override AccessibleStates State { get { return base.State | (owner.Busy ? AccessibleStates.Busy | AccessibleStates.ReadOnly : AccessibleStates.None); } }
            public override string DefaultAction { get { return owner.Busy ? "" : base.DefaultAction; } }
            public override void DoDefaultAction() { if (!owner.Busy && owner.Enabled) base.DoDefaultAction(); }
        }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    internal sealed class DisplayMap : Panel
    {
        // Keep real buttons behind the canvas: monitor selection stays accessible to
        // keyboard users and assistive tools even though the monitor chrome is drawn here.
        sealed class DisplayTile : Button
        {
            internal bool Selected, Main, TaskbarVisible;
            bool hovering;
            internal DisplayTile()
            {
                FlatStyle = FlatStyle.Flat; UseVisualStyleBackColor = false;
                FlatAppearance.BorderSize = 0;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
            protected override void OnMouseEnter(EventArgs e) { hovering = true; base.OnMouseEnter(e); Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { hovering = false; base.OnMouseLeave(e); Invalidate(); }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Ui.Card);
                if (Width < 3 || Height < 3) return;
                float scale = DeviceDpi / 96f;
                bool highContrast = SystemInformation.HighContrast;
                bool highlighted = Selected || hovering;
                e.Graphics.SmoothingMode = highContrast ? SmoothingMode.None : SmoothingMode.AntiAlias;
                var bounds = new RectangleF(1, 1, Width - 3, Height - 3);
                Color fill = highContrast ? highlighted ? SystemColors.Highlight : SystemColors.Window :
                    Selected ? Blend(Ui.Hover, Ui.Accent, 0.08f) : hovering ? Ui.Hover : Ui.Surface;
                Color textColor = highContrast ? highlighted ? SystemColors.HighlightText : SystemColors.WindowText : Enabled ? Ui.Text : Ui.Muted;
                Color lineColor = highContrast ? textColor : Selected ? Ui.Accent : Blend(Ui.Card, Ui.Line, 0.70f);
                using (var path = highContrast ? Square(bounds) : Rounded(bounds, 3 * scale)) {
                    using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                    using (var line = new Pen(lineColor, Selected ? Math.Max(1.5f, 1.5f * scale) : Math.Max(1, scale)))
                        e.Graphics.DrawPath(line, path);
                }
                Rectangle status = StatusBarBounds(Size, DeviceDpi);
                bool details = Height >= Math.Ceiling(88 * scale) && Width >= Math.Ceiling(126 * scale);
                Rectangle number = ClientRectangle;
                if (details) {
                    number.Y += (int)Math.Ceiling(5 * scale);
                    number.Height = Math.Max(1, status.Top - (int)Math.Ceiling(25 * scale) - number.Y);
                    using (var caption = new Font(Font.FontFamily, 8.25f, FontStyle.Regular)) {
                        var label = new Rectangle((int)Math.Ceiling(8 * scale), status.Top - (int)Math.Ceiling(24 * scale),
                            Math.Max(1, Width - (int)Math.Ceiling(16 * scale)), (int)Math.Ceiling(20 * scale));
                        string text = Main ? "Main display" : TaskbarVisible ? "Taskbar visible" : "Taskbar hidden";
                        TextRenderer.DrawText(e.Graphics, text, caption, label, highContrast ? textColor : Ui.Muted,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    }
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, number, textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                if (highContrast && !TaskbarVisible) {
                    using (var line = new Pen(textColor)) e.Graphics.DrawRectangle(line, status.X, status.Y,
                        Math.Max(0, status.Width - 1), Math.Max(0, status.Height - 1));
                } else {
                    Color statusColor = highContrast ? textColor : TaskbarVisible ?
                        Blend(fill, Ui.Accent, Selected ? 0.90f : 0.70f) : Blend(fill, Ui.Line, 0.70f);
                    using (var brush = new SolidBrush(statusColor)) e.Graphics.FillRectangle(brush, status);
                }
                if (Focused && ShowFocusCues) {
                    int inset = Math.Max(3, (int)Math.Ceiling(5 * scale));
                    var focus = Rectangle.Inflate(ClientRectangle, -inset, -inset);
                    if (focus.Width > 0 && focus.Height > 0) ControlPaint.DrawFocusRectangle(e.Graphics, focus, textColor, fill);
                }
            }
        }
        readonly List<Button> tiles = new List<Button>();
        internal ToolTip Tips;
        internal List<Display> Displays = new List<Display>();
        internal Settings Settings = new Settings();
        internal Display Selected;
        internal event EventHandler SelectionChanged;
        internal DisplayMap() { DoubleBuffered = true; Dock = DockStyle.Fill; BackColor = Ui.Card; Padding = new Padding(12); }
        static Color Blend(Color background, Color foreground, float amount)
        {
            return Color.FromArgb((int)Math.Round(background.R + (foreground.R - background.R) * amount),
                (int)Math.Round(background.G + (foreground.G - background.G) * amount),
                (int)Math.Round(background.B + (foreground.B - background.B) * amount));
        }
        static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        static GraphicsPath Square(RectangleF bounds)
        {
            var path = new GraphicsPath(); path.AddRectangle(bounds); return path;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Ui.Background : Parent.BackColor);
            if (Width < 3 || Height < 3) return;
            bool highContrast = SystemInformation.HighContrast;
            e.Graphics.SmoothingMode = highContrast ? SmoothingMode.None : SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = highContrast ? Square(bounds) : Rounded(bounds, 4 * DeviceDpi / 96f)) {
                using (var fill = new SolidBrush(Ui.Card)) e.Graphics.FillPath(fill, path);
                using (var line = new Pen(highContrast ? SystemColors.WindowText : Blend(Ui.Card, Ui.Line, 0.40f))) e.Graphics.DrawPath(line, path);
            }
            base.OnPaint(e);
        }
        internal static RectangleF MapBounds(Display display, List<Display> displays, Size size)
        {
            if (displays.Count == 0) return RectangleF.Empty;
            int left = displays.Min(d => d.X), top = displays.Min(d => d.Y);
            int width = Math.Max(1, displays.Max(d => d.X + d.Width) - left), height = Math.Max(1, displays.Max(d => d.Y + d.Height) - top);
            float scale = Math.Max(0.001f, Math.Min(Math.Max(1, size.Width - 32) / (float)width, Math.Max(1, size.Height - 32) / (float)height));
            float x = (size.Width - width * scale) / 2, y = (size.Height - height * scale) / 2;
            return new RectangleF(x + (display.X - left) * scale, y + (display.Y - top) * scale,
                Math.Max(1, display.Width * scale - 4), Math.Max(1, display.Height * scale - 4));
        }
        internal static float TileFontSize(int height, int dpi)
        {
            float logicalHeight = height * 96f / Math.Max(1, dpi);
            return logicalHeight < 36 ? 9 : logicalHeight < 52 ? 11 : 15;
        }
        internal static Rectangle StatusBarBounds(Size size, int dpi)
        {
            float logicalHeight = size.Height * 96f / Math.Max(1, dpi);
            int inset = (int)Math.Ceiling(7 * dpi / 96f);
            int barHeight = logicalHeight < 32 ? Math.Max(1, (int)Math.Ceiling(dpi / 96f)) :
                logicalHeight < 52 ? Math.Max(2, (int)Math.Ceiling(2 * dpi / 96f)) : 3;
            int bottom = logicalHeight < 32 ? (int)Math.Ceiling(dpi / 96f) :
                logicalHeight < 52 ? (int)Math.Ceiling(4 * dpi / 96f) : 5;
            return new Rectangle(inset, Math.Max(1, size.Height - bottom - barHeight),
                Math.Max(1, size.Width - inset * 2), barHeight);
        }
        internal void Render(List<Display> displays, Settings settings)
        {
            if (Object.ReferenceEquals(Displays, displays) && tiles.Count == displays.Count) {
                Settings = settings; UpdateTiles(); return;
            }
            string device = Selected == null ? "" : Selected.Device;
            Displays = displays; Settings = settings;
            Selected = displays.Find(d => d.Device == device) ?? displays.Find(d => d.Primary) ?? displays.FirstOrDefault();
            SuspendLayout();
            foreach (var tile in tiles) tile.Dispose();
            tiles.Clear(); Controls.Clear();
            foreach (var display in displays) {
                Display item = display;
                var tile = new DisplayTile { Text = display.Device.Replace(@"\\.\DISPLAY", ""),
                    Tag = display, Font = new Font(Font.FontFamily, 15, FontStyle.Bold), BackColor = Ui.Card,
                    ForeColor = Ui.Text, AccessibleName = settings.DisplayName(display) + (display.Primary ? ", main display" : ""), TabStop = true };
                tile.Click += delegate { Selected = item; UpdateTiles(); if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); };
                tile.Disposed += delegate { tile.Font.Dispose(); };
                tiles.Add(tile); Controls.Add(tile);
            }
            ResumeLayout(); UpdateTiles();
        }
        void UpdateTiles()
        {
            foreach (var tile in tiles) {
                var display = (Display)tile.Tag;
                tile.AccessibleName = Settings.DisplayName(display) + (display.Primary ? ", main display" : "");
                string description = display.Primary ? "Main display. Its taskbar always stays visible." :
                    String.IsNullOrEmpty(display.Identity) || Displays.Count(d => d.Identity == display.Identity) != 1 ?
                    "Display identity is uncertain. Its taskbar stays visible." :
                    Settings.HiddenIdentities.Contains(display.Identity) ? "Taskbar hidden. Select this display to change it." :
                    "Taskbar visible. Select this display to change it.";
                tile.AccessibleDescription = description;
                if (Tips != null) Tips.SetToolTip(tile, Settings.DisplayName(display) + ": " + description);
                var painted = (DisplayTile)tile;
                painted.Selected = display == Selected; painted.Main = display.Primary;
                painted.TaskbarVisible = display.Primary || String.IsNullOrEmpty(display.Identity) || Displays.Count(d => d.Identity == display.Identity) != 1 ||
                    !Settings.HiddenIdentities.Contains(display.Identity);
                tile.Bounds = Rectangle.Round(MapBounds(display, Displays, ClientSize));
                float fontSize = TileFontSize(tile.Height, tile.DeviceDpi);
                if (Math.Abs(tile.Font.Size - fontSize) > 0.1f) {
                    var previous = tile.Font;
                    tile.Font = new Font(Font.FontFamily, fontSize, FontStyle.Bold);
                    previous.Dispose();
                }
                tile.Invalidate();
            }
        }
        internal void RefreshAppearance() { UpdateTiles(); Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateTiles(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateTiles(); Invalidate(); }
    }

    internal sealed class SettingsForm : Form
    {
        internal const string WindowSizingHelp = "Keep window size uses the same pixel dimensions, clamped to the destination's usable work area. Relative size and position scale with both monitors' usable work areas. Apps may enforce their own minimum size or DPI behavior.";
        internal const string GhostBarWidthHelp = "Width of the activation area at each screen's bottom-center edge, in physical pixels. A narrower area reduces accidental openings. Range: 80 to 960 px; default: 480 px. Focus activation width or height to preview the effective area on every enabled Ghost Bar; leave both fields to dismiss the preview.";
        internal const string GhostBarHeightHelp = "Height of the activation area inside each enabled Ghost Bar monitor's bottom edge, in physical pixels. Range: 1 to 96 px; default: 6 px. A shallower area reduces accidental openings near playback controls. With stacked monitors, passing into a lower monitor does not open the upper Ghost Bar; hold the pointer inside its activation band. Focus activation width or height to preview the effective area; leave both fields to dismiss the preview.";
        internal const string GhostBarDelayHelp = "How long the pointer must stay in the activation area before the Ghost Bar opens. A longer delay reduces accidental openings. Range: 100 to 1500 ms; default: 100 ms. App-preview and full-size peek timing stay the same.";
        internal const string FocusIndicatorLightingHelp = "None keeps the plain frame. Soft glow adds a gentle halo around the frame. Illuminated softly lights the frame in your accent color. Energy flow adds a slow moving highlight; reduced motion keeps it still.";
        readonly Controller controller;
        readonly DisplayMap map = new DisplayMap();
        readonly Label monitorName = Ui.Label(""), monitorDetail = Ui.Label(""), status = Ui.Label(""), saved = Ui.Label(""), error = Ui.Label("");
        readonly FlowLayoutPanel setupRow;
        readonly RowStyle setupRowStyle;
        readonly TextBox nickname = new TextBox { BorderStyle = BorderStyle.None, MaxLength = 48 };
        readonly ToggleSwitch showTaskbar = new ToggleSwitch("Show taskbar"), moveWindows = new ToggleSwitch("Bring windows to the taskbar I use"), startup = new ToggleSwitch("Start with Windows");
        readonly ToggleSwitch windowShelf = new ToggleSwitch("Ghost Bar");
        readonly ToggleSwitch allowFullscreen = new ToggleSwitch("Allow moving fullscreen apps");
        ToggleSwitch ownedSnapPrototype;
        Label ownedSnapPrototypeStatus;
        sealed class MonitorShortcutEditor
        {
            internal readonly ShortcutRow Row;
            internal readonly Button Button;
            internal readonly Label Status;
            internal MonitorShortcutEditor(string name, Button button, Label status)
            { Row = new ShortcutRow(name, button, status); Button = button; Status = status; }
        }
        readonly Dictionary<int, MonitorShortcutEditor> monitorShortcutEditors = new Dictionary<int, MonitorShortcutEditor>();
        readonly List<Control> movementRows = new List<Control>();
        readonly TableLayoutPanel movementContent;
        readonly Button[] actionShortcuts = new Button[ActionShortcutPolicy.Count];
        readonly Label[] actionShortcutStatus = new Label[ActionShortcutPolicy.Count];
        readonly Button[] navigationShortcuts = new Button[5];
        readonly Label[] navigationShortcutStatus = new Label[5];
        readonly ComboBox windowSizing = new ThemedComboBox();
        readonly AppearancePage appearancePage = new AppearancePage();
        ToggleSwitch focusIndicator { get { return appearancePage.FocusFrame; } }
        AppearanceChoiceGrid focusIndicatorFinish { get { return appearancePage.FinishChoices; } }
        AppearanceChoiceGrid focusIndicatorLighting { get { return appearancePage.LightingChoices; } }
        AppearanceChoiceGrid accentPreset { get { return appearancePage.AccentChoices; } }
        readonly NumericUpDown shelfActivationWidth = new ThemedNumericUpDown(), shelfActivationHeight = new ThemedNumericUpDown(), shelfHoverDelay = new ThemedNumericUpDown();
        readonly ListView exceptions = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly Label emptyApps = Ui.Label("Every app follows your movement settings.\nAdd an exclusion to keep an app on its current display.");
        readonly ToolTip tips = new ToolTip();
        readonly Button undoMove, pauseMove, pauseAll, removeApp, shortcut;
        readonly Label pauseAllHelp, showTaskbarHelp, windowShelfHelp;
        readonly Label shortcutStatus = Ui.Label("");
        readonly Label administratorStatus = Ui.Label("");
        readonly Label administratorHelp = Ui.Label("", 96);
        readonly ToggleSwitch administratorSupport = new ToggleSwitch("Administrator support");
        bool administratorReading;
        const string AdministratorHelp = "Control administrator windows such as Task Manager. Turning on remembers approval for this build.\nTurning off stops support and removes saved approval. Windows may ask for permission.\nApps opened through search keep normal permissions.";
        const string UndoHelp = "Undo the latest window move, layout or minimize performed with the minimize shortcut.";
        Icon windowIcon;
        string windowIconPreset;
        Color windowIconAccent;
        AppUpdateForm updateForm;
        int revision = -1;
        Color renderedAccent = Ui.Accent;
        bool reading, active, activationPreviewUpdatePending;
        internal bool TaskbarIdentityReady { get; private set; }
        IntPtr taskbarIdentityWindow;

        protected override void OnHandleCreated(EventArgs e)
        {
            taskbarIdentityWindow = Handle;
            TaskbarIdentityReady = SettingsTaskbarIdentity.Apply(taskbarIdentityWindow, SettingsTaskbarIdentity.CurrentId);
            base.OnHandleCreated(e);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            // Release the shell property while this HWND is still owned by us;
            // recreation installs the same profile identity on the new handle.
            if (TaskbarIdentityReady) SettingsTaskbarIdentity.Clear(taskbarIdentityWindow);
            TaskbarIdentityReady = false;
            taskbarIdentityWindow = IntPtr.Zero;
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            // Our own move/size loop shares the UI thread with the indicator.
            // Remove its surface before entering that loop rather than waiting
            // for the low-priority timer or queued accessibility events.
            if (message.Msg == 0x0231) ObserveFocusMoveSize(true);
            base.WndProc(ref message);
            if (message.Msg == 0x0232) ObserveFocusMoveSize(false);
        }
        protected override void OnSystemColorsChanged(EventArgs e)
        {
            base.OnSystemColorsChanged(e);
            if (controller == null || Disposing || IsDisposed || undoMove == null) return;
            RefreshTheme(); map.RefreshAppearance(); Invalidate(true);
        }
        void RefreshTheme()
        {
            Ui.Theme(this);
            foreach (Control child in Controls.Cast<Control>().SelectMany(AllControls)) {
                if (child.AccessibleName == "Application subtitle" || child.AccessibleName == "Profile badge") child.ForeColor = Ui.Muted;
            }
            monitorDetail.ForeColor = Ui.Muted; saved.ForeColor = Ui.Muted; emptyApps.ForeColor = Ui.Muted;
            emptyApps.BackColor = Ui.Surface; shortcutStatus.ForeColor = Ui.Muted;
            foreach (Label label in monitorShortcutEditors.Values.Select(editor => editor.Status).Concat(actionShortcutStatus).Concat(navigationShortcutStatus)) label.ForeColor = Ui.Muted;
            appearancePage.SynchronizePreview();
        }
        static IEnumerable<Control> AllControls(Control root)
        {
            yield return root;
            foreach (Control child in root.Controls) foreach (Control descendant in AllControls(child)) yield return descendant;
        }
        void ObserveFocusMoveSize(bool moving)
        {
            try {
                if (controller != null && controller.FocusFrame != null)
                    controller.FocusFrame.ObserveMoveSize(Handle, moving);
            } catch (Exception) {
                // Optional decoration must never interrupt moving Settings.
                if (controller != null && controller.FocusFrame != null) controller.FocusFrame.Hide();
            }
        }

        internal SettingsForm(Controller state)
        {
            controller = state; RefreshAccentIcon(); Text = AppProfile.Title;
            map.Tips = tips;
            Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            ConfigureHelp(tips);
            ClientSize = new Size(920, 860); MinimumSize = new Size(740, 540); StartPosition = FormStartPosition.CenterScreen;
            var root = Table(1); root.Padding = new Padding(20, 14, 20, 12);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            setupRowStyle = new RowStyle(SizeType.Absolute, 0);
            root.RowStyles.Add(setupRowStyle);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var heading = Table(2); heading.ColumnStyles.Clear();
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            heading.RowCount = 1; heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var brand = Table(1); brand.RowCount = 2; brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            var title = Ui.Label("Taskbar Select"); title.Font = new Font(Font.FontFamily, 20, FontStyle.Bold);
            title.AccessibleName = "Application title"; title.Margin = Padding.Empty;
            var subtitle = Ui.Label("Windows, displays & shortcuts"); subtitle.AccessibleName = "Application subtitle";
            subtitle.Margin = Padding.Empty; brand.Controls.Add(title, 0, 0); brand.Controls.Add(subtitle, 0, 1);
            heading.Controls.Add(brand, 0, 0);
            var profileBadge = Ui.Label(AppProfile.IsTest ? "ISOLATED TEST" : "");
            profileBadge.AccessibleName = "Profile badge"; profileBadge.TextAlign = ContentAlignment.MiddleRight;
            profileBadge.Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold); profileBadge.Margin = Padding.Empty;
            profileBadge.Visible = AppProfile.IsTest;
            heading.Controls.Add(profileBadge, 1, 0);
            root.Controls.Add(heading, 0, 0);
            var tabs = new ThemedTabControl { Dock = DockStyle.Fill, AccessibleName = "Sections" };
            var displays = new TabPage("Displays") { UseVisualStyleBackColor = false, AutoScroll = true };
            var apps = new TabPage("Apps") { UseVisualStyleBackColor = false };
            var settings = new TabPage("Settings") { UseVisualStyleBackColor = false };
            var appearance = new TabPage("Appearance") { UseVisualStyleBackColor = false, AutoScroll = true };
            var shortcutsTab = new TabPage("Shortcuts") { UseVisualStyleBackColor = false, AutoScroll = true };
            tabs.TabPages.AddRange(new[] { displays, apps, shortcutsTab, appearance, settings }); root.Controls.Add(tabs, 0, 1);
            appearance.Controls.Add(appearancePage);
            var shortcutsPage = Table(1); shortcutsPage.Padding = new Padding(12, 12, 12, 8);
            shortcutsPage.Dock = DockStyle.Top; shortcutsPage.AutoSize = true; shortcutsPage.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            shortcutsTab.Controls.Add(shortcutsPage);
            var actionRows = new List<Control>();
            var focusRows = new List<Control>(); var layoutRows = new List<Control>();
            string[] actionNames = ActionShortcutPolicy.Labels;
            string[] actionLabels = actionNames.Select(name => name + " shortcut").ToArray();
            string[] actionHelp = {
                "Ask the focused app window to close. Apps may prompt to save; the app is never force-stopped. Pause moves leaves this shortcut available.",
                "Search installed apps on the display you last used, including the main display without a Ghost Bar. Pause moves disables search.",
                "Minimize the focused window without closing it. Undo restores a window minimized with this shortcut. Pause moves leaves this shortcut available. Administrator windows need Administrator support.",
                "Keep the focused window above other windows; press again to unpin it. Taskbar Select releases its own pins when it closes normally. Pause moves leaves this shortcut available. Administrator windows need Administrator support."
            };
            for (int i = 0; i < actionLabels.Length; i++) {
                int slot = i; string label = actionLabels[i];
                actionShortcuts[i] = Ui.Button("", "keyboard", delegate {
                    int value = ActionShortcutPolicy.Choices(controller.Settings)[slot];
                    using (var dialog = new ShortcutDialog(label, (Keys)value, choice => {
                        var next = controller.Settings.Copy();
                        ActionShortcutPolicy.Set(next, slot, (int)choice);
                        controller.Change(next, label, false, -1, slot);
                    }, (Keys)ActionShortcutPolicy.Defaults()[slot])) { if (dialog.ShowDialog(this) == DialogResult.OK) { saved.Text = "Saved"; Populate(); } }
                });
                actionShortcuts[i].AccessibleName = "Change " + label.ToLowerInvariant();
                Describe(actionShortcuts[i], actionHelp[slot]);
                actionShortcutStatus[i] = Ui.Label(""); actionShortcutStatus[i].AccessibleName = label + " status";
                actionRows.Add(new ShortcutRow(actionNames[slot], actionShortcuts[i], actionShortcutStatus[i]));
            }
            string[] navigationLabels = { "Focus left shortcut", "Focus right shortcut", "Focus up shortcut", "Focus down shortcut", "Tiling picker shortcut" };
            int[] navigationDefaults = NavigationShortcutPolicy.Defaults().Concat(new[] { (int)NavigationShortcutPolicy.TileDefault }).ToArray();
            for (int i = 0; i < navigationLabels.Length; i++) {
                int slot = i; string label = navigationLabels[i];
                navigationShortcuts[i] = Ui.Button("", "keyboard", delegate {
                    int value = NavigationShortcutPolicy.Choices(controller.Settings)[slot];
                    using (var dialog = new ShortcutDialog(label, (Keys)value, choice => {
                        var next = controller.Settings.Copy();
                        if (slot < 4) next.FocusWindowShortcuts[slot] = (int)choice; else next.TileWindowShortcut = (int)choice;
                        controller.Change(next, label, false, -1, -1, slot);
                    }, (Keys)navigationDefaults[slot])) { if (dialog.ShowDialog(this) == DialogResult.OK) { saved.Text = "Saved"; Populate(); } }
                });
                navigationShortcuts[i].AccessibleName = "Change " + label.ToLowerInvariant();
                Describe(navigationShortcuts[i], slot < 4 ? "Switch to a nearby window without moving or resizing it. Minimized windows and excluded apps are skipped." :
                    "Choose a half, quarter or centered layout. Use arrows to select, Enter to apply, and Esc to cancel. Press this shortcut again to close the picker.");
                navigationShortcutStatus[i] = Ui.Label(""); navigationShortcutStatus[i].AccessibleName = label + " status";
                var row = new ShortcutRow(slot < 4 ? label.Replace(" shortcut", "") : "Layout picker", navigationShortcuts[i], navigationShortcutStatus[i]);
                if (slot < 4) focusRows.Add(row); else layoutRows.Add(row);
            }


            var displayPage = Table(1); displayPage.Padding = new Padding(12, 8, 12, 8);
            displayPage.Dock = DockStyle.Top; displayPage.AutoSize = true; displayPage.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            displayPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var mapTools = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var identify = Ui.Button("Identify", null, delegate { DisplayIdentifier.Show(controller.Displays); });
            var refreshDisplays = Ui.Button("Refresh", "refresh-cw", delegate { Run(delegate { controller.RefreshDisplayIdentity = true; controller.Refresh(); }); });
            Describe(identify, "Show each screen's Windows display number.");
            Describe(refreshDisplays, "Recheck connected displays and monitor names.");
            mapTools.Controls.Add(identify); mapTools.Controls.Add(refreshDisplays);
            displayPage.Controls.Add(mapTools, 0, 0); displayPage.Controls.Add(map, 0, 1);
            displayPage.Controls.Add(SectionLabel("Selected display"), 0, 2);
            monitorName.Font = new Font(Font, FontStyle.Bold); monitorName.Margin = Padding.Empty; monitorDetail.Margin = Padding.Empty;
            displayPage.Controls.Add(monitorName, 0, 3); displayPage.Controls.Add(monitorDetail, 0, 4);
            Describe(nickname, "A local label for this display. It does not rename the monitor in Windows.");
            nickname.AccessibleName = "Nickname";
            var aliasRow = BoundedFieldRow("Nickname", CreateInfoMarker("Nickname", tips.GetToolTip(nickname)), nickname, 144, 280);
            displayPage.Controls.Add(aliasRow, 0, 5);
            showTaskbarHelp = CreateInfoMarker("Show taskbar", "Show or hide the taskbar on the selected display.");
            displayPage.Controls.Add(ToggleRow(showTaskbar, showTaskbarHelp), 0, 6);
            windowShelfHelp = CreateInfoMarker("Ghost Bar", "Hover briefly at the bottom-center edge to choose an open window. Windows from other screens move here; local choices focus or restore. A single-window icon also minimizes its active local window. App exclusions apply. Window sizing and Undo move affect only cross-display moves.");
            displayPage.Controls.Add(ToggleRow(windowShelf, windowShelfHelp), 0, 7);
            displayPage.Controls.Add(new SectionRule(), 0, 8);
            displayPage.Controls.Add(SectionLabel("All displays"), 0, 9);
            displayPage.Controls.Add(ToggleRow(moveWindows, CreateInfoMarker("Window movement", "Move a window to the monitor whose taskbar button you click. Fullscreen movement requires the opt-in under Shortcuts.")), 0, 10);
            windowSizing.Items.AddRange(new object[] { "Keep window size", "Keep relative size and position" });
            Describe(windowSizing, WindowSizingHelp);
            displayPage.Controls.Add(WindowSizingRow(windowSizing, CreateInfoMarker("Window sizing", WindowSizingHelp)), 0, 11);
            displays.Controls.Add(displayPage);
            Describe(moveWindows, "Move a window to the monitor whose taskbar button you click. Fullscreen movement requires the opt-in under Shortcuts.");

            var appPage = Table(1); appPage.Padding = new Padding(16);
            appPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); appPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); appPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            appPage.Controls.Add(InlineHelpLabel("Excluded from window movement",
                CreateInfoMarker("App exclusions", "Apps on this list stay on their current display and do not appear in the Ghost Bar.")), 0, 0);
            ThemeListHeader(exceptions);
            exceptions.BorderStyle = BorderStyle.None; exceptions.AccessibleName = "Excluded applications";
            Describe(exceptions, "Applications excluded from window movement and the Ghost Bar. Select an application to remove its exclusion.");
            exceptions.Columns.Add("Application", 330); exceptions.Columns.Add("Behavior", 260);
            var appListArea = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            emptyApps.TextAlign = ContentAlignment.MiddleCenter; emptyApps.AutoEllipsis = false;
            emptyApps.AccessibleName = "No app exclusions";
            appListArea.Controls.Add(exceptions); appListArea.Controls.Add(emptyApps);
            appPage.Controls.Add(appListArea, 0, 1);
            var appTools = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            var addApp = Ui.Button("Add app", "plus", delegate {
                using (var picker = new AppPicker(controller.Settings.ExcludedApps)) {
                    if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedApp != null) {
                        var app = picker.SelectedApp;
                        Change(s => { if (!AppCatalog.Matches(s.ExcludedApps, app.Key)) s.ExcludedApps.Add(new AppRule { Key = app.Key, Name = app.Name }); }, "App exclusion");
                    }
                }
            });
            Describe(addApp, "Choose a running app whose windows should stay on their current display.");
            appTools.Controls.Add(addApp);
            removeApp = Ui.Button("Remove exclusion", null, delegate {
                if (exceptions.SelectedItems.Count == 0) return;
                string key = (string)exceptions.SelectedItems[0].Tag;
                Change(s => s.ExcludedApps.RemoveAll(rule => rule.Key == key), "App exclusion");
            });
            removeApp.Enabled = false; exceptions.SelectedIndexChanged += delegate { removeApp.Enabled = exceptions.SelectedItems.Count > 0; };
            Describe(removeApp, "Remove the selected exception and follow the window movement setting.");
            appTools.Controls.Add(removeApp); appPage.Controls.Add(appTools, 0, 2);
            var appCard = new SettingsCard { AccessibleName = "App exclusions", Margin = new Padding(12), Padding = new Padding(16) };
            appPage.Padding = Padding.Empty; appCard.Controls.Add(appPage); apps.Controls.Add(appCard);

            settings.AutoScroll = true;
            var settingsPage = Table(1); settingsPage.Padding = new Padding(16);
            settingsPage.Dock = DockStyle.Top; settingsPage.AutoSize = true; settingsPage.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); settingsPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            var startupRow = Table(2); startupRow.ColumnStyles.Clear();
            startupRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); startupRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            startupRow.Controls.Add(ToggleRow(startup, CreateInfoMarker("Start with Windows", "Launch quietly when you sign in and apply your saved taskbar choices.")), 0, 0);
            Describe(startup, "Launch quietly when you sign in and apply your saved taskbar choices.");
            var startupApps = Ui.Button("Startup apps", null, delegate { Run(delegate { System.Diagnostics.Process.Start("ms-settings:startupapps"); }); });
            startupApps.Enabled = !AppProfile.IsTest;
            Describe(startupApps, "Open Windows Startup apps to enable or disable Taskbar Select at sign-in.");
            startupRow.Controls.Add(startupApps, 1, 0);
            startupRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            startupRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            startupRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            startupRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            administratorSupport.AccessibleName = "Administrator support";
            Describe(administratorSupport, "Turn on to control administrator windows and remember approval for this build. Turn off to stop support now and remove saved approval. Windows may ask for permission.");
            var administratorRow = ToggleRow(administratorSupport, CreateInfoMarker("Administrator support", AdministratorHelp));
            startupRow.Controls.Add(administratorRow, 0, 1);
            startupRow.SetColumnSpan(administratorRow, 2);
            administratorSupport.CheckedChanged += delegate {
                if (administratorReading) return;
                var permissions = controller.AdministratorPermissions;
                if (!controller.DesktopRuntime || permissions == null || permissions.Busy) { UpdateAdministratorStatus(); return; }
                bool requested = administratorSupport.Checked;
                administratorSupport.Busy = true;
                Run(delegate { permissions.Request(requested); UpdateAdministratorStatus(); });
            };
            administratorStatus.AccessibleName = "Administrator support status";
            administratorStatus.AutoEllipsis = false;
            startupRow.Controls.Add(administratorStatus, 0, 2);
            startupRow.SetColumnSpan(administratorStatus, 2);
            administratorHelp.AccessibleName = "Administrator support detail";
            administratorHelp.AutoEllipsis = false;
            administratorHelp.ForeColor = Ui.Muted;
            startupRow.Controls.Add(administratorHelp, 0, 3);
            startupRow.SetColumnSpan(administratorHelp, 2);
            settingsPage.Controls.Add(startupRow, 0, 0);
            shortcut = Ui.Button("", "keyboard", delegate {
                using (var dialog = new ShortcutDialog((Keys)controller.Settings.UndoMoveShortcut, choice => {
                    var next = controller.Settings.Copy(); next.UndoMoveShortcut = (int)choice;
                    controller.Change(next, "Undo shortcut", true);
                })) {
                    if (dialog.ShowDialog(this) == DialogResult.OK) { saved.Text = "Saved"; Populate(); }
                }
            });
            shortcut.AccessibleName = "Change Undo shortcut";
            Describe(shortcut, "Change or disable Undo for the last window move, layout or shortcut minimize. Pinning is toggled separately.");
            shortcutStatus.AccessibleName = "Undo shortcut status";
            movementRows.Add(new ShortcutRow("Undo window action", shortcut, shortcutStatus));
            movementRows.Add(ToggleRow(allowFullscreen, CreateInfoMarker("Fullscreen movement",
                "Off by default. Ghost Bar and shortcuts request the full destination monitor through the existing bounded mover. Games can refuse or move themselves back; Undo retains recovery.")));
            movementContent = AddShortcutCard(shortcutsPage, "Window movement", "Move the focused window, including this settings window. Numbers match the Displays map. Undo also restores shortcut minimizes.", movementRows);
            AddShortcutCard(shortcutsPage, "Focus", "Switch to the nearest window in a direction, including across displays. Pause moves suspends focus and layout.", focusRows);
            AddShortcutCard(shortcutsPage, "Layout", "Arrange the focused window on its current display. Undo restores its previous position.", layoutRows);
            ownedSnapPrototype = new ToggleSwitch("Use experimental snapping");
            ownedSnapPrototype.AccessibleName = "Use experimental snapping";
            ownedSnapPrototypeStatus = Ui.Label(controller.OwnedSnapPrototypeStatus);
            ownedSnapPrototypeStatus.AccessibleName = "Experimental snapping status";
            ownedSnapPrototypeStatus.AutoEllipsis = false;
            ownedSnapPrototype.CheckedChanged += delegate {
                if (!reading) { controller.SetOwnedSnapPrototype(ownedSnapPrototype.Checked); UpdateStatus(); }
            };
            AddShortcutCard(shortcutsPage, "Experimental snapping", "Windows+Left/Right fills display halves and cycles monitors. Native Snap groups and suggestions may differ. Off each launch.",
                new List<Control> { ToggleRow(ownedSnapPrototype, CreateInfoMarker("Experimental snapping", "Restored, resizable app windows. Administrator apps also need Administrator support. Each step can be undone. Pause moves suspends this mode. Turning Off leaves window positions unchanged.")), ownedSnapPrototypeStatus });
            AddShortcutCard(shortcutsPage, "Window actions", "Close, minimize or keep a window on top. Search opens apps on the display you last used. Each shortcut can be changed or turned off.", actionRows);
            settingsPage.Controls.Add(SectionLabel("Ghost Bar sensitivity"), 0, 3);
            Describe(shelfActivationWidth, GhostBarWidthHelp); Describe(shelfActivationHeight, GhostBarHeightHelp); Describe(shelfHoverDelay, GhostBarDelayHelp);
            settingsPage.Controls.Add(GhostBarSensitivityRow(shelfActivationWidth, CreateInfoMarker("Activation width", GhostBarWidthHelp), true), 0, 4);
            settingsPage.Controls.Add(GhostBarActivationHeightRow(shelfActivationHeight, CreateInfoMarker("Activation height", GhostBarHeightHelp)), 0, 5);
            settingsPage.Controls.Add(GhostBarSensitivityRow(shelfHoverDelay, CreateInfoMarker("Hover delay", GhostBarDelayHelp), false), 0, 6);
            var shelfDefaults = Ui.Label("Defaults: 480 px wide, 6 px high, 100 ms.\nFocus activation width or height to preview enabled Ghost Bars.", 44);
            shelfDefaults.AutoEllipsis = false;
            shelfDefaults.ForeColor = Ui.Muted; settingsPage.Controls.Add(shelfDefaults, 0, 7);
            focusIndicator.AccessibleName = "Focus frame";
            const string focusHelp = "Show a thin frame around the focused eligible app window. The frame follows it without moving or resizing it. Turning it off keeps your frame style, lighting and color choices.";
            Describe(focusIndicator, focusHelp);
            focusIndicatorFinish.AccessibleName = "Frame style";
            Describe(focusIndicatorFinish, "Muted edge keeps the restrained two-pixel frame. Accent outline uses a crisp one-pixel line in your accent color. Both can be used with any frame lighting.");
            focusIndicatorLighting.AccessibleName = "Frame lighting";
            Describe(focusIndicatorLighting, FocusIndicatorLightingHelp);
            accentPreset.AccessibleName = "Accent color";
            const string accentHelp = "Choose the accent for Taskbar Select controls, Ghost Bar, search, layout picker, focus frame, and live app icons. Forest Green is the default for new profiles. Windows and other apps keep their own colors.";
            Describe(accentPreset, accentHelp);
            var versionRow = Table(2); versionRow.ColumnStyles.Clear();
            versionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            versionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            versionRow.Controls.Add(Ui.Label("Version " + Application.ProductVersion), 0, 0);
            var checkForUpdates = Ui.Button("Check for updates", "refresh-cw", delegate { ShowUpdates(); });
            checkForUpdates.AccessibleName = "Check for updates";
            Describe(checkForUpdates, "Check official GitHub releases for a newer version. Updates are checked only when you ask.");
            versionRow.Controls.Add(checkForUpdates, 1, 0);
            settingsPage.Controls.Add(versionRow, 0, 8);
            var windowsTaskbar = Ui.Button("Open Windows taskbar settings", null, delegate { Run(delegate { System.Diagnostics.Process.Start("ms-settings:taskbar"); }); });
            Describe(windowsTaskbar, "Enable 'Show my taskbar on all displays' so Taskbar Select can manage secondary taskbars.");
            settingsPage.Controls.Add(windowsTaskbar, 0, 9);
            settingsPage.Controls.Add(Ui.Button("Open GitHub repository", null, delegate { Run(delegate { System.Diagnostics.Process.Start(AppUpdatePolicy.Repository); }); }), 0, 10);
            var copyDiagnostics = Ui.Button("Copy diagnostics", null, delegate { Run(delegate { Clipboard.SetText(Diagnostics.Create(controller)); saved.Text = "Diagnostics copied"; }); });
            Describe(copyDiagnostics, "Copy a support summary without app paths, nicknames, or monitor serial numbers.");
            settingsPage.Controls.Add(copyDiagnostics, 0, 11);
            settingsPage.Controls.Add(error, 0, 12); settings.Controls.Add(settingsPage);

            GroupRows(displayPage, new[] { new[] { 0, 1 }, new[] { 2, 7 }, new[] { 8, 11 } },
                new[] { "Display layout", null, null }, Font.Height);
            foreach (Control control in settingsPage.Controls) {
                int row = settingsPage.GetRow(control);
                if (row >= 3) settingsPage.SetRow(control, row - 2);
            }
            settingsPage.RowStyles.RemoveAt(1); settingsPage.RowStyles.RemoveAt(1);
            GroupRows(settingsPage, new[] { new[] { 0, 0 }, new[] { 1, 5 }, new[] { 6, 10 } },
                new[] { "General", null, "About Taskbar Select" }, Font.Height);
            FitAdministratorHelp(startupRow, settingsPage, Font.Height);
            saved.Margin = Padding.Empty; saved.AccessibleName = "Save status";
            saved.Text = "Settings save automatically"; root.Controls.Add(saved, 0, 2);
            setupRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Visible = false };
            setupRow.Controls.Add(new Label { Text = "No secondary taskbars detected", AutoSize = true, Margin = new Padding(0, 10, 8, 0) });
            var taskbarSettings = Ui.Button("Taskbar settings", null, delegate { Run(delegate { System.Diagnostics.Process.Start("ms-settings:taskbar"); }); });
            Describe(taskbarSettings, "Check Windows' 'Show my taskbar on all displays' setting.");
            setupRow.Controls.Add(taskbarSettings);
            root.Controls.Add(setupRow, 0, 3);
            var footer = Table(2); footer.Padding = new Padding(0, 6, 0, 0); footer.ColumnStyles.Clear(); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.AccessibleName = "Taskbar status"; status.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(status, 0, 0);
            var footerTools = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Fill };
            undoMove = Ui.Button("Undo", "undo-2", delegate { Run(delegate { if (!controller.LayoutRecoveryPending) controller.Mover.Undo(); saved.Text = controller.Mover.LastStatus; }); });
            pauseMove = Ui.Button("Pause moves", "pause", delegate { Run(delegate { controller.ToggleMovementPause(); }); });
            pauseAll = Ui.Button("Pause all", null, delegate { Run(delegate { controller.Enabled = !controller.Enabled; controller.Refresh(true); controller.Revision++; }); });
            pauseAllHelp = CreateInfoMarker("Pause all", "Show all taskbars temporarily. Your saved choices are kept.");
            footerTools.Controls.Add(undoMove); footerTools.Controls.Add(pauseMove); footerTools.Controls.Add(pauseAll);
            footerTools.Controls.Add(pauseAllHelp); footer.Controls.Add(footerTools, 1, 0); root.Controls.Add(footer, 0, 4);
            Describe(undoMove, UndoHelp);
            Describe(pauseMove, "Pause window movement while keeping taskbar visibility choices.");
            Controls.Add(root); Ui.Theme(this); map.BackColor = Ui.Surface; windowSizing.BackColor = Ui.Surface;
            subtitle.ForeColor = Ui.Muted; profileBadge.ForeColor = Ui.Muted;
            emptyApps.ForeColor = Ui.Muted; emptyApps.BackColor = Ui.Surface;
            shortcutStatus.ForeColor = Ui.Muted;
            foreach (Label label in actionShortcutStatus.Concat(navigationShortcutStatus)) label.ForeColor = Ui.Muted;
            shelfDefaults.ForeColor = Ui.Muted;
            monitorDetail.ForeColor = Ui.Muted; saved.ForeColor = Ui.Muted;
            showTaskbar.FitText(); windowShelf.FitText(); moveWindows.FitText(); startup.FitText();
            map.SelectionChanged += delegate { ReadSelected(); };
            nickname.Leave += delegate { SaveAlias(); };
            nickname.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { SaveAlias(); e.SuppressKeyPress = true; } };
            showTaskbar.CheckedChanged += delegate {
                if (reading || map.Selected == null) return;
                string id = map.Selected.Identity; bool shown = showTaskbar.Checked;
                Change(s => { s.HiddenIdentities.RemoveAll(x => x == id); if (!shown) s.HiddenIdentities.Add(id); }, "Taskbar visibility");
            };
            windowShelf.CheckedChanged += delegate {
                if (reading || map.Selected == null || !windowShelf.Enabled) return;
                string id = map.Selected.Identity; bool enabled = windowShelf.Checked;
                Change(s => { s.WindowShelfIdentities.RemoveAll(x => x == id); if (enabled) s.WindowShelfIdentities.Add(id); }, "Ghost Bar");
            };
            moveWindows.CheckedChanged += delegate { if (!reading) Change(s => s.MoveWindowsToClickedTaskbar = moveWindows.Checked, "Window movement"); };
            allowFullscreen.CheckedChanged += delegate { if (!reading) Change(s => s.AllowMovingFullscreenApps = allowFullscreen.Checked, "Fullscreen movement"); };
            windowSizing.SelectedIndexChanged += delegate { if (!reading) Change(s => s.ScaleWindowSizeToMonitor = windowSizing.SelectedIndex == 1, "Window sizing"); };
            focusIndicator.CheckedChanged += delegate {
                if (!reading)
                    Change(s => s.FocusIndicatorStyle = focusIndicator.Checked ? (int)FocusIndicatorMode.InsetEdge : (int)FocusIndicatorMode.Off, "Focus frame");
            };
            focusIndicatorFinish.SelectedIndexChanged += delegate {
                if (!reading && focusIndicatorFinish.Enabled && focusIndicatorFinish.SelectedIndex >= 0)
                    Change(s => s.FocusIndicatorFinish = (int)FocusAppearance.NormalizeFinish(focusIndicatorFinish.SelectedIndex), "Frame style");
            };
            focusIndicatorLighting.SelectedIndexChanged += delegate {
                if (!reading && focusIndicatorLighting.Enabled && focusIndicatorLighting.SelectedIndex >= 0)
                    Change(s => {
                        s.FocusIndicatorLighting = focusIndicatorLighting.SelectedIndex;
                        s.FocusIndicatorGlow = focusIndicatorLighting.SelectedIndex == (int)FocusLightingMode.SubtleGlow;
                    }, "Frame lighting");
            };
            accentPreset.SelectedIndexChanged += delegate {
                if (!reading && accentPreset.SelectedItem != null)
                    Change(s => s.AccentPreset = FocusAppearance.AccentStorageName(accentPreset.SelectedItem), "Accent color");
            };
            shelfActivationWidth.ValueChanged += delegate { if (!reading) Change(s => s.WindowShelfActivationWidth = (int)shelfActivationWidth.Value, "Ghost Bar activation width"); QueueActivationPreviewUpdate(); };
            shelfActivationHeight.ValueChanged += delegate { if (!reading) Change(s => s.WindowShelfActivationHeight = (int)shelfActivationHeight.Value, "Ghost Bar activation height"); QueueActivationPreviewUpdate(); };
            shelfHoverDelay.ValueChanged += delegate { if (!reading) Change(s => s.WindowShelfHoverDelayMs = (int)shelfHoverDelay.Value, "Ghost Bar hover delay"); };
            shelfActivationWidth.Enter += delegate { QueueActivationPreviewUpdate(); };
            shelfActivationWidth.Leave += delegate { QueueActivationPreviewUpdate(); };
            shelfActivationHeight.Enter += delegate { QueueActivationPreviewUpdate(); };
            shelfActivationHeight.Leave += delegate { QueueActivationPreviewUpdate(); };
            shelfActivationWidth.VisibleChanged += delegate { UpdateActivationPreview(); };
            shelfActivationHeight.VisibleChanged += delegate { UpdateActivationPreview(); };
            tabs.SelectedIndexChanged += delegate { controller.PreviewShelfActivation(false); QueueActivationPreviewUpdate(); };
            VisibleChanged += delegate {
                UpdateActivationPreview();
                if (!Visible && updateForm != null && !updateForm.IsDisposed) updateForm.Close();
            };
            Deactivate += delegate { active = false; controller.PreviewShelfActivation(false); };
            startup.CheckedChanged += delegate { if (!reading) Run(delegate { controller.ChangeStartup(startup.Checked); saved.Text = "Saved"; }); };
            Activated += delegate { active = true; ReadStartup(); UpdateStatus(); UpdateActivationPreview(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (e.CloseReason == CloseReason.UserClosing) { SaveAlias(); e.Cancel = true; Hide(); }
            };
            Populate();
        }
        void ShowUpdates()
        {
            Run(delegate {
                if (updateForm != null && !updateForm.IsDisposed) {
                    if (updateForm.WindowState == FormWindowState.Minimized) updateForm.WindowState = FormWindowState.Normal;
                    updateForm.Activate(); return;
                }
                var form = new AppUpdateForm(controller);
                updateForm = form;
                form.FormClosed += delegate { if (Object.ReferenceEquals(updateForm, form)) updateForm = null; };
                try { form.Show(this); }
                catch { form.Dispose(); if (Object.ReferenceEquals(updateForm, form)) updateForm = null; throw; }
            });
        }
        void FitAdministratorHelp(TableLayoutPanel layout, TableLayoutPanel page, int baseFontHeight)
        {
            bool sizing = false;
            layout.Layout += delegate {
                if (sizing || layout.ClientSize.Width < 1) return;
                var content = layout.Parent as TableLayoutPanel;
                var card = content == null ? null : content.Parent as SettingsCard;
                if (card == null) return;
                sizing = true;
                try {
                    float scale = administratorHelp.Font.Height / (float)Math.Max(1, baseFontHeight);
                    int width = Math.Max(1, layout.ClientSize.Width - administratorHelp.Margin.Horizontal - administratorHelp.Padding.Horizontal);
                    int textHeight = TextRenderer.MeasureText(administratorHelp.Text, administratorHelp.Font,
                        new Size(width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    float required = Math.Max(96 * scale, textHeight + administratorHelp.Margin.Vertical + administratorHelp.Padding.Vertical + (int)Math.Ceiling(4 * scale));
                    if (layout.RowStyles[3].Height != required) layout.RowStyles[3].Height = required;
                    float nested = layout.RowStyles.Cast<RowStyle>().Sum(row => row.Height) + layout.Margin.Vertical;
                    var hostRow = content.RowStyles[content.GetRow(layout)];
                    if (hostRow.Height != nested) hostRow.Height = nested;
                    float total = content.RowStyles.Cast<RowStyle>().Sum(row => row.Height) + card.Padding.Vertical + card.Margin.Vertical;
                    var pageRow = page.RowStyles[page.GetRow(card)];
                    if (pageRow.Height != total) pageRow.Height = total;
                } finally { sizing = false; }
            };
            administratorHelp.TextChanged += delegate { layout.PerformLayout(); page.PerformLayout(); };
            administratorHelp.FontChanged += delegate { layout.PerformLayout(); page.PerformLayout(); };
        }
        static TableLayoutPanel Table(int columns)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, Margin = Padding.Empty };
            for (int index = 0; index < columns; index++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            return table;
        }
        // Reparent existing rows without replacing their controls or handlers.
        // Every section remains in one native scrolling content host.
        internal static void GroupRows(TableLayoutPanel page, int[][] groups, string[] titles, int baseFontHeight)
        {
            Control[] children = page.Controls.Cast<Control>().ToArray();
            var rows = children.ToDictionary(control => control, control => page.GetRow(control));
            RowStyle[] styles = page.RowStyles.Cast<RowStyle>().Select(style =>
                new RowStyle(style.SizeType, style.Height)).ToArray();
            page.SuspendLayout();
            page.Controls.Clear(); page.RowStyles.Clear(); page.RowCount = groups.Length;
            page.Padding = new Padding(12, 12, 12, 8);
            for (int group = 0; group < groups.Length; group++) {
                int first = groups[group][0], last = groups[group][1];
                var card = new SettingsCard { AccessibleName = titles[group] ??
                    children.OfType<SectionHeader>().Where(control => rows[control] >= first && rows[control] <= last)
                        .Select(control => control.Text).FirstOrDefault() ?? "Display preferences" };
                var content = Table(1); int offset = titles[group] == null ? 0 : 1;
                content.RowCount = last - first + 1 + offset;
                int height = 0;
                if (offset != 0) {
                    content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); height += 30;
                    content.Controls.Add(SectionLabel(titles[group]), 0, 0);
                }
                for (int row = first; row <= last; row++) {
                    float rowHeight = row < styles.Length && styles[row].SizeType == SizeType.Absolute ? styles[row].Height : 44;
                    content.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
                    height += (int)Math.Ceiling(rowHeight);
                }
                foreach (Control control in children.Where(control => rows[control] >= first && rows[control] <= last))
                    content.Controls.Add(control, 0, rows[control] - first + offset);
                card.Controls.Add(content);
                page.RowStyles.Add(new RowStyle(SizeType.Absolute, height + card.Padding.Vertical + card.Margin.Vertical));
                page.Controls.Add(card, 0, group);
                FitWrappedHelpRows(page, content, card, group, baseFontHeight);
            }
            page.ResumeLayout(true);
        }
        static void FitWrappedHelpRows(TableLayoutPanel page, TableLayoutPanel content, SettingsCard card, int group, int baseFontHeight)
        {
            var labels = content.Controls.OfType<Label>().Where(label => !(label is SectionHeader) && !label.AutoEllipsis).ToArray();
            if (labels.Length == 0) return;
            var minima = labels.ToDictionary(label => label, label => content.RowStyles[content.GetRow(label)].Height);
            bool sizing = false;
            content.Layout += delegate {
                if (sizing || content.ClientSize.Width < 1) return;
                sizing = true;
                try {
                    foreach (Label label in labels) {
                        float scale = label.Font.Height / (float)Math.Max(1, baseFontHeight);
                        int width = Math.Max(1, content.ClientSize.Width - label.Margin.Horizontal - label.Padding.Horizontal);
                        int textHeight = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, Int32.MaxValue),
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                        float required = Math.Max(minima[label] * scale, textHeight + label.Margin.Vertical +
                            label.Padding.Vertical + (int)Math.Ceiling(4 * scale));
                        var row = content.RowStyles[content.GetRow(label)];
                        if (row.Height != required) row.Height = required;
                    }
                    float total = content.RowStyles.Cast<RowStyle>().Sum(row => row.Height) + card.Padding.Vertical + card.Margin.Vertical;
                    if (page.RowStyles[group].Height != total) page.RowStyles[group].Height = total;
                } finally { sizing = false; }
            };
            foreach (Label label in labels) {
                label.TextChanged += delegate { content.PerformLayout(); page.PerformLayout(); };
                label.FontChanged += delegate { content.PerformLayout(); page.PerformLayout(); };
            }
        }
        static TableLayoutPanel AddShortcutCard(TableLayoutPanel page, string title, string help, IList<Control> rows)
        {
            int group = page.Controls.Count;
            var card = new SettingsCard { AccessibleName = title };
            var content = Table(1); content.RowCount = rows.Count + 2;
            var heading = SectionLabel(title); var explanation = Ui.Label(help);
            explanation.AutoEllipsis = false; explanation.AccessibleName = title + " help";
            explanation.ForeColor = Ui.Muted;
            content.Controls.Add(heading, 0, 0); content.Controls.Add(explanation, 0, 1);
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            for (int i = 0; i < rows.Count; i++) {
                content.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
                content.Controls.Add(rows[i], 0, i + 2);
            }
            card.Controls.Add(content); page.RowCount = group + 1;
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 100)); page.Controls.Add(card, 0, group);
            bool sizing = false;
            content.Layout += delegate {
                if (sizing || content.ClientSize.Width < 1) return;
                sizing = true;
                try {
                    int width = content.ClientSize.Width;
                    int total = Math.Max(heading.Font.Height + 10, heading.Margin.Vertical + heading.Font.Height);
                    content.RowStyles[0].Height = total;
                    int helpHeight = TextRenderer.MeasureText(explanation.Text, explanation.Font,
                        new Size(Math.Max(1, width - explanation.Margin.Horizontal), Int32.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + explanation.Margin.Vertical + 8;
                    content.RowStyles[1].Height = helpHeight; total += helpHeight;
                    for (int i = 0; i < rows.Count; i++) {
                        int height = rows[i] is ShortcutRow ? rows[i].GetPreferredSize(new Size(width, 0)).Height :
                            Math.Max(rows[i].Font.Height + 20, rows[i].Controls.Cast<Control>().Select(child => child.Height).DefaultIfEmpty(40).Max());
                        content.RowStyles[i + 2].Height = height; total += height;
                    }
                    float heightNeeded = total + card.Padding.Vertical + card.Margin.Vertical;
                    if (page.RowStyles[group].Height != heightNeeded) page.RowStyles[group].Height = heightNeeded;
                } finally { sizing = false; }
            };
            foreach (Control child in rows.SelectMany(row => AllControls(row)).Concat(new[] { explanation }))
                ObserveShortcutLayout(child, content, page);
            return content;
        }
        static void ObserveShortcutLayout(Control child, TableLayoutPanel content, Control page)
        {
            child.TextChanged += delegate { content.PerformLayout(); page.PerformLayout(); };
            child.FontChanged += delegate { content.PerformLayout(); page.PerformLayout(); };
        }
        MonitorShortcutEditor CreateMonitorShortcutEditor(int number)
        {
            string name = "Move to Display " + number;
            Keys suggestion = number <= 9 ? Keys.Control | Keys.Shift | (Keys)((int)Keys.D0 + number) : Keys.None;
            var button = Ui.Button("", "keyboard", delegate {
                using (var dialog = new ShortcutDialog(name + " shortcut", (Keys)MonitorShortcutPolicy.Choice(controller.Settings, number),
                    choice => controller.ChangeMonitorShortcut(number, choice), suggestion)) {
                    if (dialog.ShowDialog(this) == DialogResult.OK) { saved.Text = "Saved"; Populate(); }
                }
            });
            button.AccessibleName = "Change move to Display " + number + " shortcut";
            var status = Ui.Label("");
            status.AccessibleName = name + " shortcut status";
            var editor = new MonitorShortcutEditor(name, button, status);
            if (IsHandleCreated && DeviceDpi != 96) editor.Row.Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f));
            return editor;
        }
        void UpdateMonitorShortcutRows()
        {
            var displays = MonitorShortcutPolicy.ConnectedDisplays(controller.Displays);
            int[] numbers = displays.Select(MonitorShortcutPolicy.DisplayNumber).ToArray();
            if (monitorShortcutEditors.Count != numbers.Length || numbers.Any(number => !monitorShortcutEditors.ContainsKey(number))) {
                var card = movementContent.Parent;
                var page = card.Parent;
                movementContent.SuspendLayout();
                try {
                    movementRows.RemoveRange(0, monitorShortcutEditors.Count);
                    foreach (int absent in monitorShortcutEditors.Keys.Except(numbers).ToArray()) {
                        monitorShortcutEditors[absent].Row.Dispose();
                        monitorShortcutEditors.Remove(absent);
                    }
                    for (int i = 0; i < numbers.Length; i++) {
                        int number = numbers[i];
                        MonitorShortcutEditor editor;
                        if (!monitorShortcutEditors.TryGetValue(number, out editor)) {
                            editor = CreateMonitorShortcutEditor(number);
                            monitorShortcutEditors.Add(number, editor);
                            foreach (Control child in AllControls(editor.Row)) ObserveShortcutLayout(child, movementContent, page);
                        }
                        movementRows.Insert(i, editor.Row);
                    }
                    int rowCount = movementRows.Count + 2;
                    if (movementContent.RowCount < rowCount) movementContent.RowCount = rowCount;
                    while (movementContent.RowStyles.Count < rowCount) movementContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
                    for (int i = 0; i < movementRows.Count; i++) {
                        Control row = movementRows[i];
                        row.TabIndex = i + 2;
                        if (row.Parent == movementContent) movementContent.SetRow(row, i + 2);
                        else movementContent.Controls.Add(row, 0, i + 2);
                    }
                    while (movementContent.RowStyles.Count > rowCount) movementContent.RowStyles.RemoveAt(movementContent.RowStyles.Count - 1);
                    movementContent.RowCount = rowCount;
                    Ui.Theme(card);
                    shortcutStatus.ForeColor = Ui.Muted;
                    foreach (var editor in monitorShortcutEditors.Values) editor.Status.ForeColor = Ui.Muted;
                } finally { movementContent.ResumeLayout(true); }
            }
            for (int i = 0; i < displays.Count; i++) {
                int number = numbers[i];
                var editor = monitorShortcutEditors[number];
                editor.Button.Text = GlobalShortcut.Format((Keys)MonitorShortcutPolicy.Choice(controller.Settings, number));
                string state = controller.MonitorShortcutStatus(number);
                editor.Status.Text = state == "Shortcut active" || state == "Shortcut off" ? state + " | " + displays[i].Label : state;
                Describe(editor.Status, editor.Status.Text);
            }
        }
        internal static FlowLayoutPanel ToggleRow(ToggleSwitch toggle, Control info)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            toggle.Dock = DockStyle.None; toggle.Margin = Padding.Empty; toggle.Height = 40;
            row.Controls.Add(toggle); row.Controls.Add(info); toggle.FitText();
            return row;
        }
        internal static TableLayoutPanel WindowSizingRow(ComboBox selector, Control info)
        {
            selector.AccessibleName = "Window sizing";
            return BoundedFieldRow("Window sizing", info, selector, 144, 320);
        }
        internal static TableLayoutPanel GhostBarSensitivityRow(NumericUpDown input, Control info, bool width)
        {
            input.Minimum = width ? WindowShelfPolicy.MinActivationWidth : WindowShelfPolicy.MinHoverDelayMs;
            input.Maximum = width ? WindowShelfPolicy.MaxActivationWidth : WindowShelfPolicy.MaxHoverDelayMs;
            input.Increment = width ? 40 : 50;
            input.Value = width ? WindowShelfPolicy.DefaultActivationWidth : WindowShelfPolicy.DefaultHoverDelayMs;
            input.BorderStyle = BorderStyle.None; input.DecimalPlaces = 0; input.ThousandsSeparator = false;
            input.AccessibleName = width ? "Ghost Bar activation width" : "Ghost Bar hover delay";
            return BoundedFieldRow(width ? "Activation width (px)" : "Hover delay (ms)", info, input, 200, 140);
        }
        internal static TableLayoutPanel GhostBarActivationHeightRow(NumericUpDown input, Control info)
        {
            input.Minimum = WindowShelfPolicy.MinActivationHeight; input.Maximum = WindowShelfPolicy.MaxActivationHeight;
            input.Increment = 1; input.Value = WindowShelfPolicy.DefaultActivationHeight;
            input.BorderStyle = BorderStyle.None; input.DecimalPlaces = 0; input.ThousandsSeparator = false;
            input.AccessibleName = "Ghost Bar activation height";
            return BoundedFieldRow("Activation height (px)", info, input, 200, 140);
        }
        internal static TableLayoutPanel BoundedFieldRow(string label, Control info, Control input, int labelWidth, int maxWidth)
        {
            var row = Table(2);
            row.ColumnStyles.Clear();
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(InlineHelpLabel(label, info), 0, 0);
            var frame = new FieldFrame(input) { Margin = new Padding(0, 4, 0, 0) };
            row.Controls.Add(frame, 1, 0);
            row.Layout += delegate {
                int available = Math.Max(1, row.GetColumnWidths()[1] - frame.Margin.Horizontal);
                int width = BoundedFieldWidth(available, maxWidth, row.DeviceDpi);
                if (frame.Width != width) frame.Width = width;
            };
            return row;
        }
        internal static int BoundedFieldWidth(int available, int maxWidth, int dpi)
        {
            return Math.Max(1, Math.Min(available, (int)Math.Ceiling(maxWidth * dpi / 96f)));
        }
        internal static void ThemeListHeader(ListView list)
        {
            list.OwnerDraw = true;
            list.HandleCreated += delegate { FitLastListColumn(list); };
            list.ClientSizeChanged += delegate { FitLastListColumn(list); };
            list.ColumnWidthChanged += delegate(object sender, ColumnWidthChangedEventArgs e) {
                if (e.ColumnIndex != list.Columns.Count - 1) FitLastListColumn(list);
            };
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e) {
                using (var fill = new SolidBrush(Ui.Surface)) e.Graphics.FillRectangle(fill, e.Bounds);
                using (var line = new Pen(Ui.Line)) {
                    e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
                    e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
                }
                Rectangle text = Rectangle.Inflate(e.Bounds, -8, 0);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, list.Font, text, Ui.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
            list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
        }
        internal static void FitLastListColumn(ListView list)
        {
            if (list.Columns.Count < 2) return;
            int occupied = 0;
            for (int index = 0; index < list.Columns.Count - 1; index++) occupied += list.Columns[index].Width;
            int width = Math.Max((int)Math.Ceiling(120 * list.DeviceDpi / 96f), list.ClientSize.Width - occupied);
            var last = list.Columns[list.Columns.Count - 1];
            if (last.Width != width) last.Width = width;
        }
        static Label SectionLabel(string text) { return new SectionHeader(text); }
        internal static Size TabItemSize(int dpi)
        {
            return new Size((int)Math.Ceiling(120 * dpi / 96f), (int)Math.Ceiling(40 * dpi / 96f));
        }
        static FlowLayoutPanel InlineHelpLabel(string label, Control info)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            row.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 10, 2, 0) });
            row.Controls.Add(info);
            return row;
        }
        void Describe(Control control, string description)
        {
            tips.SetToolTip(control, description);
            control.AccessibleDescription = description;
        }
        Label CreateInfoMarker(string name, string description)
        {
            return CreateInfoMarker(name, description, tips);
        }
        internal static Label CreateInfoMarker(string name, string description, ToolTip hoverTips)
        {
            var marker = new Label {
                AutoSize = false, Size = new Size(24, 24), Margin = new Padding(2, 8, 0, 0),
                Padding = Padding.Empty, Image = Ui.LoadToolbarIcon("info", 14),
                ImageAlign = ContentAlignment.MiddleCenter, BackColor = Ui.Background,
                TabStop = false, AccessibleRole = AccessibleRole.Graphic,
                AccessibleName = name + " information", AccessibleDescription = description
            };
            marker.Disposed += delegate { if (marker.Image != null) marker.Image.Dispose(); };
            hoverTips.SetToolTip(marker, description);
            return marker;
        }
        internal static void ConfigureHelp(ToolTip help)
        {
            help.OwnerDraw = true;
            help.Popup += delegate(object sender, PopupEventArgs e) {
                Control control = e.AssociatedControl;
                if (control == null) return;
                e.ToolTipSize = MeasureHelp(help.GetToolTip(control), control.Font, control.DeviceDpi, Screen.GetWorkingArea(control).Size);
            };
            help.Draw += delegate(object sender, DrawToolTipEventArgs e) {
                Control control = e.AssociatedControl;
                int dpi = control == null ? 96 : control.DeviceDpi;
                int padding = HelpPadding(dpi);
                using (var fill = new SolidBrush(Ui.Surface)) e.Graphics.FillRectangle(fill, e.Bounds);
                using (var border = new Pen(Ui.Line)) e.Graphics.DrawRectangle(border, e.Bounds.Left, e.Bounds.Top, e.Bounds.Width - 1, e.Bounds.Height - 1);
                Rectangle textBounds = Rectangle.Inflate(e.Bounds, -padding, -padding);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, control == null ? Control.DefaultFont : control.Font, textBounds, Ui.Text,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            };
        }
        static int HelpPadding(int dpi) { return (int)Math.Ceiling(10 * dpi / 96f); }
        internal static Size MeasureHelp(string text, Font font, int dpi, Size workArea)
        {
            int padding = HelpPadding(dpi);
            int maxWidth = Math.Max(1, Math.Min((int)Math.Ceiling(360 * dpi / 96f), workArea.Width - 24 - padding * 2));
            int lineWidth = TextRenderer.MeasureText(text, font, new Size(Int32.MaxValue, Int32.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            int width = Math.Min(maxWidth, lineWidth);
            Size content = TextRenderer.MeasureText(text, font, new Size(width, Int32.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            return new Size(Math.Min(workArea.Width, width + padding * 2),
                Math.Min(workArea.Height, content.Height + padding * 2));
        }
        void Run(Action action)
        {
            try { action(); Populate(); }
            catch (Exception exception) { saved.Text = "Change could not be completed"; MessageBox.Show(this, exception.Message, "Taskbar Select", MessageBoxButtons.OK, MessageBoxIcon.Warning); Populate(); }
        }
        void Change(Action<Settings> action, string label)
        {
            Run(delegate { var next = controller.Settings.Copy(); action(next); controller.Change(next, label); saved.Text = "Saved"; });
        }
        void SaveAlias()
        {
            if (reading || map.Selected == null || !nickname.Enabled) return;
            string id = map.Selected.Identity, value = nickname.Text.Trim(), previous;
            controller.Settings.MonitorAliases.TryGetValue(id, out previous);
            if ((previous ?? "") == value) return;
            Change(s => { if (value.Length == 0) s.MonitorAliases.Remove(id); else s.MonitorAliases[id] = value; }, "Monitor nickname");
        }
        void ReadSelected()
        {
            reading = true;
            var display = map.Selected;
            if (display == null) { monitorName.Text = "No displays available"; nickname.Enabled = false; showTaskbar.Enabled = false; windowShelf.Enabled = false; reading = false; return; }
            monitorName.Text = controller.Settings.DisplayName(display) + (display.Primary ? "  (Main display)" : "");
            monitorDetail.Text = display.Label + "  |  " + display.Width + " x " + display.Height;
            bool known = !String.IsNullOrEmpty(display.Identity) && controller.Displays.Count(d => d.Identity == display.Identity) == 1;
            nickname.Enabled = known;
            string name; nickname.Text = known && controller.Settings.MonitorAliases.TryGetValue(display.Identity, out name) ? name : "";
            showTaskbar.Enabled = known && !display.Primary;
            showTaskbar.Text = display.Primary ? "Show taskbar (main display)" : "Show taskbar";
            showTaskbar.FitText();
            showTaskbar.Checked = display.Primary || !known || !controller.Settings.HiddenIdentities.Contains(display.Identity);
            windowShelf.Checked = known && controller.Settings.WindowShelfIdentities.Contains(display.Identity);
            windowShelf.Enabled = known && !display.Primary && !showTaskbar.Checked;
            string shelfTip = windowShelf.Enabled ? "Hover briefly at the bottom-center edge to choose an open window. Windows from other screens move here; local choices focus or restore. A single-window icon also minimizes its active local window. App exclusions apply. Window sizing and Undo move affect only cross-display moves." :
                "The Ghost Bar is available on secondary displays with a hidden taskbar. Hide this display's taskbar to enable its Ghost Bar.";
            Describe(windowShelf, shelfTip); Describe(windowShelfHelp, shelfTip);
            string taskbarTip = display.Primary ? "The main display's taskbar always stays visible." :
                !known ? "Windows could not identify this display reliably, so its taskbar stays visible." :
                "Show or hide the taskbar on this display. Hiding it gives that screen the space back.";
            Describe(showTaskbar, taskbarTip);
            Describe(showTaskbarHelp, taskbarTip);
            Describe(monitorName, taskbarTip);
            reading = false;
        }
        void Populate()
        {
            reading = true; map.Render(controller.Displays, controller.Settings);
            moveWindows.Checked = controller.Settings.MoveWindowsToClickedTaskbar; moveWindows.Enabled = controller.Mover.Available;
            if (ownedSnapPrototype != null) {
                ownedSnapPrototype.Checked = controller.OwnedSnapPrototypeEnabled;
                ownedSnapPrototypeStatus.Text = controller.OwnedSnapPrototypeStatus;
            }
            allowFullscreen.Checked = controller.Settings.AllowMovingFullscreenApps;
            windowSizing.SelectedIndex = controller.Settings.ScaleWindowSizeToMonitor ? 1 : 0;
            focusIndicator.Checked = FocusAppearance.FrameStyle(controller.Settings.FocusIndicatorStyle) != FocusIndicatorMode.Off;
            focusIndicatorFinish.SelectedIndex = (int)FocusAppearance.NormalizeFinish(controller.Settings.FocusIndicatorFinish);
            focusIndicatorFinish.Enabled = focusIndicator.Checked;
            focusIndicatorLighting.SelectedIndex = (int)FocusAppearance.ResolveLighting(controller.Settings.FocusIndicatorLighting, controller.Settings.FocusIndicatorGlow);
            focusIndicatorLighting.Enabled = focusIndicator.Checked;
            accentPreset.SelectedIndex = Array.IndexOf(FocusAppearance.Presets, FocusAppearance.NormalizeAccent(controller.Settings.AccentPreset));
            appearancePage.SynchronizePreview();
            shelfActivationWidth.Value = controller.Settings.WindowShelfActivationWidth;
            shelfActivationHeight.Value = controller.Settings.WindowShelfActivationHeight;
            shelfHoverDelay.Value = controller.Settings.WindowShelfHoverDelayMs;
            ReadStartup();
            exceptions.Items.Clear();
            foreach (var rule in controller.Settings.ExcludedApps.OrderBy(rule => rule.Name)) {
                var item = new ListViewItem(rule.Name ?? "Application") { Tag = rule.Key }; item.SubItems.Add("Keep current monitor"); exceptions.Items.Add(item);
            }
            emptyApps.Visible = exceptions.Items.Count == 0;
            if (exceptions.Items.Count == 0) emptyApps.BringToFront();
            revision = controller.Revision; reading = false; ReadSelected(); UpdateStatus();
        }
        internal void UpdateStatus()
        {
            if (ownedSnapPrototypeStatus != null) ownedSnapPrototypeStatus.Text = controller.OwnedSnapPrototypeStatus;
            RefreshAccentIcon();
            UpdateAdministratorStatus();
            // Hook registration can finish after the initial form population.
            // Refresh availability without repopulating controls or moving focus.
            moveWindows.Enabled = controller.Mover.Available;
            if (renderedAccent != Ui.Accent) {
                renderedAccent = Ui.Accent; map.RefreshAppearance(); Invalidate(true);
            }
            if (revision != controller.Revision && !nickname.Focused) { Populate(); return; }
            shortcut.Text = GlobalShortcut.Format((Keys)controller.Settings.UndoMoveShortcut);
            UpdateMonitorShortcutRows();
            for (int i = 0; i < actionShortcuts.Length; i++) {
                int value = ActionShortcutPolicy.Choices(controller.Settings)[i];
                actionShortcuts[i].Text = GlobalShortcut.Format((Keys)value);
                actionShortcutStatus[i].Text = controller.ActionShortcutErrors[i] ??
                    (controller.ActionShortcuts[i].Current == Keys.None ? "Shortcut off" : "Shortcut active");
                if (controller.ActionShortcutErrors[i] == null && controller.ActionShortcuts[i].Current != Keys.None &&
                    (!controller.Enabled || (i == 1 && controller.MovementPaused))) actionShortcutStatus[i].Text += " | Paused";
                Describe(actionShortcutStatus[i], actionShortcutStatus[i].Text);
                tips.SetToolTip(actionShortcutStatus[i], controller.ActionShortcutErrors[i] ?? "");
            }
            int[] navigationChoices = NavigationShortcutPolicy.Choices(controller.Settings);
            for (int i = 0; i < navigationShortcuts.Length; i++) {
                navigationShortcuts[i].Text = GlobalShortcut.Format((Keys)navigationChoices[i]);
                navigationShortcutStatus[i].Text = controller.NavigationShortcutErrors[i] ??
                    (controller.NavigationShortcuts[i].Current == Keys.None ? "Shortcut off" : "Shortcut active");
                if (controller.NavigationShortcutErrors[i] == null && controller.NavigationShortcuts[i].Current != Keys.None &&
                    (!controller.Enabled || controller.MovementPaused)) navigationShortcutStatus[i].Text += " | Paused";
                Describe(navigationShortcutStatus[i], navigationShortcutStatus[i].Text);
            }
            shortcutStatus.Text = controller.ShortcutError ?? (controller.UndoShortcut.Current == Keys.None ? "Shortcut off" : "Shortcut active");
            tips.SetToolTip(shortcutStatus, controller.ShortcutError ?? "");
            Describe(undoMove, UndoHelp + (controller.UndoShortcut.Current == Keys.None ? "" : " (" + GlobalShortcut.Format(controller.UndoShortcut.Current) + ")"));
            undoMove.Enabled = !controller.LayoutRecoveryPending && controller.Mover.CanUndo;
            pauseMove.Enabled = (controller.Settings.HasWindowMovement || controller.OwnedSnapPrototypeEnabled) && controller.Enabled;
            windowSizing.Enabled = controller.Enabled && controller.Settings.HasWindowMovement;
            pauseMove.Text = controller.MovementPaused ? "Resume moves" : "Pause moves";
            Ui.SetIcon(pauseMove, controller.MovementPaused ? "play" : "pause");
            Describe(pauseMove, controller.MovementPaused ? "Resume taskbar-click movement, monitor shortcuts and enabled Ghost Bars." :
                "Stop moving app windows while keeping your taskbar visibility choices.");
            pauseAll.Text = controller.Enabled ? "Pause all" : "Resume";
            Describe(pauseAll, controller.Enabled ? "Show all taskbars temporarily. Your saved choices are kept." :
                "Reapply your saved taskbar visibility choices.");
            Describe(pauseAllHelp, tips.GetToolTip(pauseAll));
            status.Text = TaskbarStatus(controller.Enabled, controller.Bars, controller.Displays, controller.Settings);
            if (controller.MovementPaused && controller.Enabled) status.Text += " | Moves paused";
            error.Text = controller.TaskbarRecoveryError ?? controller.FocusIndicatorError ??
                (controller.Administrator != null && !String.IsNullOrEmpty(controller.Administrator.LastError) ? controller.Administrator.LastError : null) ?? controller.Mover.LastStatus;
            bool missing = SecondaryTaskbarsMissing(controller.Displays, controller.Bars);
            setupRow.Visible = missing;
            setupRowStyle.Height = missing ? SetupRowHeight(setupRow, DeviceDpi) : 0;
        }
        void UpdateAdministratorStatus()
        {
            var permissions = controller.AdministratorPermissions;
            if (permissions == null) PresentAdministratorSupport(false, false, "Off", AdministratorHelp);
            else PresentAdministratorSupport(permissions.Checked, permissions.Busy, permissions.Status, permissions.Detail);
        }
        // Render the coordinator's snapshot without inferring permission state
        // from a preference or triggering a new request during synchronization.
        internal void PresentAdministratorSupport(bool selected, bool busy, string supportStatus, string detail)
        {
            administratorReading = true;
            try {
                administratorSupport.Checked = selected;
                administratorSupport.Busy = busy;
                administratorSupport.Enabled = controller.DesktopRuntime && controller.AdministratorPermissions != null;
            } finally { administratorReading = false; }
            administratorStatus.Text = "Administrator support: " + (String.IsNullOrEmpty(supportStatus) ? "Off" : supportStatus);
            administratorHelp.Text = detail ?? "";
            Describe(administratorStatus, administratorStatus.Text);
            Describe(administratorHelp, administratorHelp.Text);
        }
        void UpdateActivationPreview()
        {
            controller.PreviewShelfActivation(ActivationPreviewRequested(active, Visible, ContainsFocus,
                shelfActivationWidth.Visible, shelfActivationWidth.ContainsFocus, shelfActivationHeight.Visible, shelfActivationHeight.ContainsFocus));
            if (controller.ActivationPreviewError != null) saved.Text = controller.ActivationPreviewError;
        }
        internal static bool ActivationPreviewRequested(bool active, bool visible, bool formContainsFocus,
            bool widthVisible, bool widthContainsFocus, bool heightVisible, bool heightContainsFocus)
        {
            return active && visible && formContainsFocus &&
                ((widthVisible && widthContainsFocus) || (heightVisible && heightContainsFocus));
        }
        void QueueActivationPreviewUpdate()
        {
            if (IsDisposed || Disposing || activationPreviewUpdatePending) return;
            if (!IsHandleCreated) { UpdateActivationPreview(); return; }
            // Enter/Leave can precede the child editor/spinner focus transition.
            // Read both ContainsFocus values once the transition has completed.
            activationPreviewUpdatePending = true;
            try {
                BeginInvoke((MethodInvoker)delegate {
                    activationPreviewUpdatePending = false;
                    if (!IsDisposed && !Disposing) UpdateActivationPreview();
                });
            } catch (InvalidOperationException) {
                activationPreviewUpdatePending = false; controller.PreviewShelfActivation(false);
            }
        }

        internal static int SetupRowHeight(Control row, int dpi)
        {
            return Math.Max(row.GetPreferredSize(Size.Empty).Height, (int)Math.Ceiling(40 * dpi / 96f));
        }

        internal static string TaskbarStatus(bool enabled, List<Bar> bars, List<Display> displays, Settings settings)
        {
            if (!enabled) return "Paused";
            int hidden = bars.Count(bar => !bar.Primary && !bar.Visible && bar.Managed);
            bool applying = bars.Any(bar => Policy.Hide(bar, displays, settings) && (bar.Visible || !bar.Managed));
            if (applying) return hidden == 0 ? "Applying taskbar choices" : hidden + " hidden | Applying";
            return hidden + (hidden == 1 ? " taskbar hidden" : " taskbars hidden");
        }

        internal static bool SecondaryTaskbarsMissing(List<Display> displays, List<Bar> bars)
        {
            return displays.Count > 1 && !bars.Any(bar => !bar.Primary);
        }

        void ReadStartup()
        {
            bool previous = reading; reading = true;
            if (AppProfile.IsTest) { startup.Checked = false; startup.Enabled = false; startup.Text = "Startup disabled in local test"; reading = previous; return; }
            try {
                var state = Startup.ReadState();
                startup.Checked = state.Enabled; startup.Enabled = state.CanChange;
                startup.Text = state.WindowsBlocked ? "Start with Windows (blocked by Windows)" : "Start with Windows";
            } catch (System.Security.SecurityException) { startup.Enabled = false; startup.Text = "Startup status unavailable"; }
            catch (UnauthorizedAccessException) { startup.Enabled = false; startup.Text = "Startup status unavailable"; }
            finally { reading = previous; }
        }
        void RefreshAccentIcon()
        {
            if (controller == null || controller.Settings == null || Disposing || IsDisposed) return;
            string preset = FocusAppearance.NormalizeAccent(controller.Settings.AccentPreset);
            Color accent = Branding.AccentIconColor(preset);
            if (windowIcon != null && windowIconPreset == preset && windowIconAccent == accent) return;
            Icon next = Branding.LoadAccentIcon(32, preset);
            try { Icon = next; }
            catch { next.Dispose(); throw; }
            Icon previous = windowIcon;
            windowIcon = next; windowIconPreset = preset; windowIconAccent = accent;
            if (previous != null) previous.Dispose();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                if (updateForm != null) { updateForm.Dispose(); updateForm = null; }
                controller.PreviewShelfActivation(false); tips.Dispose();
                if (windowIcon != null) { Icon = null; windowIcon.Dispose(); windowIcon = null; }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class AppPicker : Form
    {
        readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly Label state = Ui.Label("Loading running apps...");
        readonly Button add, refresh;
        readonly List<AppRule> excluded;
        readonly ImageList images = new ImageList { ImageSize = new Size(24, 24), ColorDepth = ColorDepth.Depth32Bit };
        internal RunningApp SelectedApp;
        internal AppPicker(List<AppRule> rules)
        {
            excluded = rules; Text = "Add app exception"; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(550, 420); MinimumSize = new Size(460, 330); StartPosition = FormStartPosition.CenterParent;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.Controls.Add(state, 0, 0); list.Columns.Add("Running application", 320); list.Columns.Add("Type", 150); list.SmallImageList = images; root.Controls.Add(list, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            add = Ui.Button("Exclude app", "plus", delegate { SelectApp(); }); add.Enabled = false;
            actions.Controls.Add(add); actions.Controls.Add(Ui.Button("Cancel", null, delegate { DialogResult = DialogResult.Cancel; Close(); }));
            refresh = Ui.Button("Refresh", "refresh-cw", async delegate { await LoadApps(); }); actions.Controls.Add(refresh);
            root.Controls.Add(actions, 0, 2); Controls.Add(root); Ui.Theme(this);
            list.SelectedIndexChanged += delegate { add.Enabled = list.SelectedItems.Count > 0; };
            list.DoubleClick += delegate { SelectApp(); }; AcceptButton = add;
            Shown += async delegate { await LoadApps(); };
        }
        void SelectApp()
        {
            if (list.SelectedItems.Count == 0) return;
            SelectedApp = (RunningApp)list.SelectedItems[0].Tag; DialogResult = DialogResult.OK; Close();
        }
        async Task LoadApps()
        {
            refresh.Enabled = false; add.Enabled = false; state.Text = "Loading running apps...";
            try {
                var apps = await Task.Run(() => AppCatalog.Read());
                if (IsDisposed) return;
                list.Items.Clear(); images.Images.Clear();
                foreach (var app in apps.Where(app => !AppCatalog.Matches(excluded, app.Key))) {
                    int index = images.Images.Count;
                    try { using (var icon = Icon.ExtractAssociatedIcon(app.Path)) images.Images.Add(icon == null ? SystemIcons.Application : icon); }
                    catch (Exception) { images.Images.Add(SystemIcons.Application); }
                    var item = new ListViewItem(app.Name, index) { Tag = app, ToolTipText = app.Path };
                    item.SubItems.Add(app.Key.StartsWith("app:") ? "Windows app" : "Desktop app"); list.Items.Add(item);
                }
                list.ShowItemToolTips = true; state.Text = list.Items.Count == 0 ? "No other running apps" : "Running apps";
            } catch (Exception error) { if (!IsDisposed) state.Text = "Could not load apps: " + error.Message; }
            finally { if (!IsDisposed) refresh.Enabled = true; }
        }
        protected override void Dispose(bool disposing) { if (disposing) images.Dispose(); base.Dispose(disposing); }
    }
}
