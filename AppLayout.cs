using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Keep real native buttons and labels; only their arrangement is custom.
    // The status has the full row width and grows for registration failures.
    internal sealed class ShortcutRow : Panel
    {
        readonly Label action;
        readonly Button binding;
        readonly Label status;
        int gap = 12, bottom = 8, keyMinimum = 220, inset = 4;

        internal ShortcutRow(string name, Button editor, Label state)
        {
            Dock = DockStyle.Fill; Margin = Padding.Empty; TabStop = false;
            action = Ui.Label(name); action.AutoEllipsis = false; action.Dock = DockStyle.None;
            binding = editor; binding.AutoSize = false; binding.Dock = DockStyle.None;
            binding.Margin = Padding.Empty;
            status = state; status.AutoEllipsis = false; status.Dock = DockStyle.None; status.Margin = Padding.Empty;
            Controls.Add(action); Controls.Add(binding); Controls.Add(status);
        }

        int KeyWidth(int width)
        {
            Size text = TextRenderer.MeasureText(binding.Text, binding.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int icon = binding.Image == null ? 0 : binding.Image.Width + gap;
            return Math.Min(Math.Max(1, width / 2), Math.Max(keyMinimum, text.Width + icon + gap * 2));
        }

        static int TextHeight(Label label, int width)
        {
            return TextRenderer.MeasureText(String.IsNullOrEmpty(label.Text) ? " " : label.Text,
                label.Font, new Size(Math.Max(1, width), Int32.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        }

        int HeaderHeight(int width)
        {
            return Math.Max(binding.Font.Height + binding.Padding.Vertical + inset * 2,
                Math.Max(binding.MinimumSize.Height + inset * 2, TextHeight(action, width - KeyWidth(width) - gap) + inset * 2));
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = proposedSize.Width > 0 ? proposedSize.Width : Math.Max(1, Width);
            return new Size(width, HeaderHeight(width) + TextHeight(status, width) + bottom);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int width = ClientSize.Width, keys = KeyWidth(width), header = HeaderHeight(width);
            action.SetBounds(0, 0, Math.Max(1, width - keys - gap), header);
            binding.SetBounds(Math.Max(0, width - keys), inset, keys, Math.Max(1, header - inset * 2));
            status.SetBounds(0, header, width, TextHeight(status, width));
        }

        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            gap = Math.Max(1, (int)Math.Round(gap * factor.Width));
            bottom = Math.Max(1, (int)Math.Round(bottom * factor.Height));
            keyMinimum = Math.Max(1, (int)Math.Round(keyMinimum * factor.Width));
            inset = Math.Max(1, (int)Math.Round(inset * factor.Height));
            base.ScaleControl(factor, specified);
        }
    }

    // A visual group only. Native child controls still own focus, keyboard input,
    // accessibility and their existing settings handlers.
    internal sealed class SettingsCard : Panel
    {
        internal SettingsCard()
        {
            Dock = DockStyle.Fill;
            Margin = new Padding(0, 0, 0, 12);
            Padding = new Padding(16, 12, 16, 12);
            TabStop = false;
            AccessibleRole = AccessibleRole.Grouping;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Ui.Background : Parent.BackColor);
            Rectangle bounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            if (bounds.Width < 2 || bounds.Height < 2) return;
            if (SystemInformation.HighContrast) {
                using (var fill = new SolidBrush(Ui.Card)) e.Graphics.FillRectangle(fill, bounds);
                using (var line = new Pen(Ui.Line)) e.Graphics.DrawRectangle(line, bounds);
                return;
            }
            float radius = Math.Min(4 * DeviceDpi / 96f, Math.Min(bounds.Width, bounds.Height) / 2f);
            using (var path = Rounded(bounds, radius)) {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(Ui.Card)) e.Graphics.FillPath(fill, path);
                using (var line = new Pen(Ui.Line)) e.Graphics.DrawPath(line, path);
            }
        }

        static GraphicsPath Rounded(Rectangle bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Max(1, radius * 2);
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
