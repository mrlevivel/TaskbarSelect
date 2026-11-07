using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal enum WindowTile { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

    // Work areas and visible frames are already physical pixels. Tiling does not
    // reinterpret them as DIPs or rely on the primary monitor's origin or DPI.
    internal static class WindowTilingPolicy
    {
        internal static Rectangle Target(Rectangle work, Rectangle currentVisible, WindowTile tile)
        {
            if (work.Width < 1 || work.Height < 1) throw new ArgumentException("A valid work area is required.", "work");
            if ((int)tile < 0 || (int)tile > 8) throw new ArgumentOutOfRangeException("tile");
            if (tile == WindowTile.Center) {
                int width = Math.Max(1, Math.Min(work.Width, currentVisible.Width));
                int height = Math.Max(1, Math.Min(work.Height, currentVisible.Height));
                return new Rectangle(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
            }
            int column = (int)tile % 3, row = (int)tile / 3;
            int leftWidth = Math.Max(1, work.Width / 2), topHeight = Math.Max(1, work.Height / 2);
            int rightOffset = work.Width == 1 ? 0 : leftWidth, bottomOffset = work.Height == 1 ? 0 : topHeight;
            int x = column == 2 ? work.Left + rightOffset : work.Left;
            int y = row == 2 ? work.Top + bottomOffset : work.Top;
            int widthResult = column == 1 ? work.Width : column == 0 ? leftWidth : work.Width - rightOffset;
            int heightResult = row == 1 ? work.Height : row == 0 ? topHeight : work.Height - bottomOffset;
            return new Rectangle(x, y, widthResult, heightResult);
        }
    }

    internal sealed class TilingPickerLayout
    {
        internal Rectangle Header, Footer, Grid;
        internal readonly Rectangle[] Cells = new Rectangle[9];
        internal double Scale;
        internal bool ShowCaptions;

        internal static Rectangle Fit(Rectangle work, int dpi)
        {
            if (work.Width < 1 || work.Height < 1) throw new ArgumentException("A valid work area is required.", "work");
            int margin = Math.Min(WindowShelfPolicy.Scale(12, Math.Max(96, dpi)), Math.Max(0, (Math.Min(work.Width, work.Height) - 1) / 2));
            int width = Math.Max(1, Math.Min(WindowShelfPolicy.Scale(388, Math.Max(96, dpi)), work.Width - margin * 2));
            int height = Math.Max(1, Math.Min(WindowShelfPolicy.Scale(352, Math.Max(96, dpi)), work.Height - margin * 2));
            return new Rectangle(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
        }

        internal static TilingPickerLayout Create(Size size, int dpi)
        {
            var layout = new TilingPickerLayout();
            int width = Math.Max(1, size.Width), height = Math.Max(1, size.Height);
            layout.Scale = Math.Min(Math.Max(96, dpi) / 96.0, Math.Min(width / 240.0, height / 240.0));
            int margin = Math.Max(0, Math.Min((int)Math.Round(12 * layout.Scale), (Math.Min(width, height) - 3) / 4));
            int headerHeight = height >= 96 ? Math.Max(14, (int)Math.Round(44 * layout.Scale)) : 0;
            int footerHeight = height >= 120 ? Math.Max(14, (int)Math.Round(26 * layout.Scale)) : 0;
            layout.Header = new Rectangle(margin, margin, Math.Max(1, width - margin * 2), headerHeight);
            layout.Footer = new Rectangle(margin, Math.Max(margin, height - margin - footerHeight), Math.Max(1, width - margin * 2), footerHeight);
            int top = margin + headerHeight, bottom = Math.Max(top + 1, height - margin - footerHeight);
            layout.Grid = new Rectangle(margin, Math.Min(height - 1, top), Math.Max(1, width - margin * 2), Math.Max(1, Math.Min(height, bottom) - top));
            int gap = Math.Min(Math.Max(0, (int)Math.Round(8 * layout.Scale)), Math.Max(0, (Math.Min(layout.Grid.Width, layout.Grid.Height) - 3) / 4));
            int availableWidth = Math.Max(1, layout.Grid.Width - gap * 2), availableHeight = Math.Max(1, layout.Grid.Height - gap * 2);
            for (int index = 0; index < 9; index++) {
                int column = index % 3, row = index / 3;
                int x = layout.Grid.Left + availableWidth * column / 3 + gap * column;
                int y = layout.Grid.Top + availableHeight * row / 3 + gap * row;
                int cellWidth = Math.Max(1, availableWidth * (column + 1) / 3 - availableWidth * column / 3);
                int cellHeight = Math.Max(1, availableHeight * (row + 1) / 3 - availableHeight * row / 3);
                x = Math.Min(width - 1, x); y = Math.Min(height - 1, y);
                layout.Cells[index] = new Rectangle(x, y, Math.Min(cellWidth, width - x), Math.Min(cellHeight, height - y));
            }
            layout.ShowCaptions = layout.Cells[0].Width >= 62 && layout.Cells[0].Height >= 54;
            return layout;
        }

        internal static WindowTile Navigate(WindowTile selected, Keys key)
        {
            int index = Math.Max(0, Math.Min(8, (int)selected)), column = index % 3, row = index / 3;
            if (key == Keys.Left) column = Math.Max(0, column - 1);
            else if (key == Keys.Right) column = Math.Min(2, column + 1);
            else if (key == Keys.Up) row = Math.Max(0, row - 1);
            else if (key == Keys.Down) row = Math.Min(2, row + 1);
            return (WindowTile)(row * 3 + column);
        }
    }

    internal sealed class TilingPicker : Form
    {
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
        readonly Action<WindowTile> apply;
        readonly TileButton[] buttons = new TileButton[9];
        readonly Timer timer = new Timer { Interval = 15 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly ShelfMotion motion = new ShelfMotion();
        readonly GhostSearchFocusRetry focusRetry;
        internal GhostSearchFocusApi FocusApi = new GhostSearchFocusApi();
        long focusToken;
        bool finished, opened, activated, timerDisposed, animate;
        int dpi = 96;
        string targetTitle = "Active window";
        WindowTile selected = WindowTile.Center;
        TilingPickerLayout layout;
        Font titleFont, hintFont;

        internal TilingPicker(Action<WindowTile> apply)
        {
            this.apply = apply;
            focusRetry = new GhostSearchFocusRetry(delegate { return clock.ElapsedMilliseconds; });
            Text = "Taskbar Select tiling"; AccessibleName = "Arrange the active window";
            AccessibleDescription = "Arrow keys choose a layout. Enter applies it. Escape cancels without changing the window.";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None; KeyPreview = true;
            DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true);
            for (int index = 0; index < buttons.Length; index++) {
                var button = new TileButton(this, (WindowTile)index) { TabStop = false, Cursor = Cursors.Hand };
                button.Click += delegate(object sender, EventArgs e) { Apply(((TileButton)sender).Tile); };
                buttons[index] = button; Controls.Add(button);
            }
            timer.Tick += delegate { Tick(); };
            Configure(new Rectangle(0, 0, 800, 600), 96, null);
        }

        internal WindowTile SelectedTile { get { return selected; } }
        internal bool IsFinished { get { return finished; } }
        internal bool FocusPending { get { return focusRetry.Pending(focusToken); } }
        internal TilingPickerLayout PickerLayout { get { return layout; } }
        internal string TargetTitle { get { return targetTitle; } }

        protected override CreateParams CreateParams
        { get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; } }

        // Configure is also used by hidden owned regression fixtures: it never shows
        // the form, changes foreground, or touches the target window.
        internal void Configure(Rectangle work, int capturedDpi, string title)
        {
            if (finished || IsDisposed || opened) return;
            dpi = Math.Max(96, capturedDpi);
            targetTitle = SafeTitle(title);
            Bounds = TilingPickerLayout.Fit(work, dpi);
            LayoutChildren(); ApplyTheme();
        }

        static string SafeTitle(string title)
        {
            if (String.IsNullOrWhiteSpace(title)) return "Active window";
            var clean = new System.Text.StringBuilder();
            foreach (char value in title) if (!Char.IsControl(value)) { clean.Append(value); if (clean.Length == 96) break; }
            string valueText = clean.ToString().Trim();
            return valueText.Length == 0 ? "Active window" : valueText;
        }

        internal void Open(Rectangle work, int capturedDpi, string title)
        {
            if (finished || IsDisposed || opened) return;
            Configure(work, capturedDpi, title);
            IntPtr previous = FocusApi.Foreground();
            int enabled;
            animate = ShelfMotion.Allowed(SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0, true, SystemInformation.HighContrast);
            motion.Reset(false); motion.Begin(true, clock.ElapsedMilliseconds, animate);
            Opacity = animate ? .05 : 1;
            opened = true;
            Show();
            if (finished || IsDisposed) return;
            focusToken = focusRetry.Begin(Handle, previous);
            Tick();
            if (!finished && !IsDisposed) timer.Start();
        }

        void Tick()
        {
            if (finished || IsDisposed || !Visible || !IsHandleCreated) { StopTimers(); return; }
            if (motion.Active) Opacity = Math.Max(.05, motion.Value(clock.ElapsedMilliseconds));
            else if (Opacity != 1) Opacity = 1;
            try {
                if (FocusPending) {
                    bool focused = focusRetry.TryFocus(focusToken, FocusApi.Foreground,
                        delegate(IntPtr handle) { FocusApi.Claim(handle); }, delegate { FocusApi.Focus(this); },
                        delegate { return FocusApi.Focused(this) || ContainsFocus; });
                    if (!focused && !FocusPending) { Cancel(); return; }
                } else if (activated && FocusApi.Foreground() != Handle) { Cancel(); return; }
            } catch (Win32Exception) { Cancel(); }
            catch (InvalidOperationException) { Cancel(); }
        }

        internal void ObservePointerDown(Point screenPoint)
        { if (opened && !finished && Visible && !ContainsScreenPoint(screenPoint)) Cancel(); }

        internal bool ContainsScreenPoint(Point screenPoint)
        {
            return Bounds.Contains(screenPoint) && (Region == null ||
                Region.IsVisible(screenPoint.X - Bounds.Left, screenPoint.Y - Bounds.Top));
        }

        internal bool HandlePickerKey(Keys key)
        {
            if (finished || IsDisposed) return false;
            if (key == Keys.Escape) { Cancel(); return true; }
            if (key == Keys.Enter) { Apply(selected); return true; }
            if (key != Keys.Left && key != Keys.Right && key != Keys.Up && key != Keys.Down) return false;
            SelectTile(TilingPickerLayout.Navigate(selected, key)); return true;
        }

        void SelectTile(WindowTile tile)
        {
            if (finished || IsDisposed) return;
            selected = tile;
            foreach (TileButton button in buttons) button.Invalidate();
            AccessibleDescription = TileButton.Caption(tile) + ". Arrow keys choose a layout. Enter applies. Escape cancels.";
        }

        void Apply(WindowTile tile)
        {
            if (finished || IsDisposed) return;
            selected = tile; finished = true; StopTimers();
            // Close before requesting placement: the captured app, never this popup,
            // is the target and foreground eligibility is checked again by its owner.
            Close();
            if (apply != null) apply(tile);
        }

        internal void Cancel()
        {
            if (finished || IsDisposed) return;
            finished = true; StopTimers(); Close();
        }

        void StopTimers()
        { focusRetry.Cancel(); if (!timerDisposed) timer.Stop(); }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        { return HandlePickerKey(keyData) || base.ProcessCmdKey(ref message, keyData); }

        protected override void OnActivated(EventArgs e)
        { activated = true; base.OnActivated(e); }

        protected override void OnDeactivate(EventArgs e)
        { base.OnDeactivate(e); if (opened && activated && !finished) Cancel(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        { finished = true; StopTimers(); base.OnFormClosing(e); }

        protected override void OnHandleDestroyed(EventArgs e)
        { StopTimers(); base.OnHandleDestroyed(e); }

        protected override void OnResize(EventArgs e)
        { base.OnResize(e); if (buttons != null && buttons[0] != null) LayoutChildren(); }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x001A || message.Msg == 0x031A || message.Msg == 0x031E) {
                ApplyTheme();
                if (SystemInformation.HighContrast) { animate = false; motion.Reset(true); Opacity = 1; }
            }
        }

        void LayoutChildren()
        {
            layout = TilingPickerLayout.Create(ClientSize, dpi);
            if (titleFont != null) titleFont.Dispose();
            if (hintFont != null) hintFont.Dispose();
            titleFont = new Font("Segoe UI", Math.Max(8, (float)(14 * layout.Scale)), FontStyle.Bold, GraphicsUnit.Pixel);
            hintFont = new Font("Segoe UI", Math.Max(7, (float)(11 * layout.Scale)), FontStyle.Regular, GraphicsUnit.Pixel);
            for (int index = 0; index < buttons.Length; index++) buttons[index].Bounds = layout.Cells[index];
            UpdateShape(); Invalidate(true);
        }

        void ApplyTheme()
        {
            bool contrast = SystemInformation.HighContrast;
            BackColor = ShelfPaint.Surface(contrast); ForeColor = ShelfPaint.Ink(contrast);
            foreach (TileButton button in buttons) { button.BackColor = BackColor; button.ForeColor = ForeColor; }
            UpdateShape(); Invalidate(true);
        }

        void UpdateShape()
        {
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            Region previous = Region;
            if (SystemInformation.HighContrast) Region = null;
            else using (var path = ShelfPaint.Round(new RectangleF(0, 0, Width, Height), Math.Max(2, (float)(12 * layout.Scale)))) Region = new Region(path);
            if (previous != null) previous.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (layout == null) return;
            bool contrast = SystemInformation.HighContrast;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(ShelfPaint.Border(contrast)))
            using (var path = ShelfPaint.Round(new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), contrast ? 1 : Math.Max(2, (float)(12 * layout.Scale))))
                e.Graphics.DrawPath(pen, path);
            if (layout.Header.Height > 0) {
                int titleHeight = Math.Min(layout.Header.Height, Math.Max(10, (int)Math.Round(21 * layout.Scale)));
                ShelfPaint.Text(e.Graphics, "Arrange window", titleFont, new Rectangle(layout.Header.Left, layout.Header.Top, layout.Header.Width, titleHeight), ForeColor, false);
                Rectangle subtitle = new Rectangle(layout.Header.Left, layout.Header.Top + titleHeight, layout.Header.Width, Math.Max(0, layout.Header.Height - titleHeight));
                ShelfPaint.Text(e.Graphics, targetTitle, hintFont, subtitle, contrast ? ForeColor : Ui.Muted, false);
            }
            if (layout.Footer.Height > 0) ShelfPaint.Text(e.Graphics, "Arrows choose  \u00b7  Enter applies  \u00b7  Esc cancels", hintFont, layout.Footer, contrast ? ForeColor : Ui.Muted, true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                finished = true; StopTimers(); timer.Dispose(); timerDisposed = true;
                if (titleFont != null) titleFont.Dispose(); if (hintFont != null) hintFont.Dispose();
                Region previous = Region; Region = null; if (previous != null) previous.Dispose();
            }
            base.Dispose(disposing);
        }

        sealed class TileButton : Button
        {
            readonly TilingPicker owner;
            internal readonly WindowTile Tile;
            bool hover;
            internal TileButton(TilingPicker owner, WindowTile tile)
            {
                this.owner = owner; Tile = tile; AccessibleName = Caption(tile);
                AccessibleDescription = "Apply " + Caption(tile).ToLowerInvariant() + " layout to the captured window.";
                AccessibleRole = AccessibleRole.PushButton;
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
            internal static string Caption(WindowTile tile)
            {
                string[] values = { "Top left", "Top half", "Top right", "Left half", "Center", "Right half", "Bottom left", "Bottom half", "Bottom right" };
                return values[(int)tile];
            }
            protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; base.OnMouseLeave(e); Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                bool contrast = SystemInformation.HighContrast, selected = owner.selected == Tile;
                Color ink = selected && contrast ? SystemColors.HighlightText : ShelfPaint.Ink(contrast);
                Color fill = selected ? contrast ? SystemColors.Highlight : Ui.Dark ? Color.FromArgb(48, 74, 67) : Color.FromArgb(216, 241, 231) : hover ? contrast ? SystemColors.Control : Ui.Background : BackColor;
                e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (Width < 3 || Height < 3) return;
                using (var path = ShelfPaint.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), contrast ? 1 : Math.Max(2, (float)(7 * owner.layout.Scale))))
                using (var brush = new SolidBrush(fill))
                using (var pen = new Pen(selected ? contrast ? SystemColors.Highlight : Ui.Accent : ShelfPaint.Border(contrast), selected ? 2 : 1)) {
                    e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path);
                }
                int padding = Math.Max(2, Math.Min(Width / 7, Height / 7));
                int captionHeight = owner.layout.ShowCaptions ? Math.Min(Height / 3, Math.Max(12, (int)Math.Round(20 * owner.layout.Scale))) : 0;
                var screen = new Rectangle(padding, padding, Math.Max(1, Width - padding * 2), Math.Max(1, Height - padding * 2 - captionHeight));
                var target = WindowTilingPolicy.Target(screen, new Rectangle(0, 0, Math.Max(1, screen.Width * 2 / 3), Math.Max(1, screen.Height * 2 / 3)), Tile);
                using (var pen = new Pen(ink)) e.Graphics.DrawRectangle(pen, screen.Left, screen.Top, Math.Max(0, screen.Width - 1), Math.Max(0, screen.Height - 1));
                target.Inflate(-Math.Min(2, target.Width / 3), -Math.Min(2, target.Height / 3));
                using (var brush = new SolidBrush(selected && !contrast ? Ui.Accent : ink)) e.Graphics.FillRectangle(brush, target);
                if (captionHeight > 0) ShelfPaint.Text(e.Graphics, Caption(Tile), owner.hintFont,
                    new Rectangle(0, Height - captionHeight - 2, Width, captionHeight), ink, true);
            }
        }
    }
}
