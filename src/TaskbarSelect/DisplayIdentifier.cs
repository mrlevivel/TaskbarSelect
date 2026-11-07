using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class IdentifierWindow : Form
    {
        internal IdentifierWindow(Display display)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 88, 166);
            ClientSize = new Size(240, 150);
            Location = new Point(display.X + Math.Max(0, (display.Width - Width) / 2),
                display.Y + Math.Max(0, (display.Height - Height) / 2));
            Controls.Add(new Label {
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 44, FontStyle.Bold),
                Text = display.Device.Replace(@"\\.\DISPLAY", "")
            });
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get {
                const int WsExToolWindow = 0x80;
                const int WsExNoActivate = 0x08000000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }
    }

    internal static class DisplayIdentifier
    {
        static readonly List<IdentifierWindow> windows = new List<IdentifierWindow>();
        static Timer timer;

        internal static void Show(IEnumerable<Display> displays)
        {
            Close();
            foreach (Display display in displays) {
                var window = new IdentifierWindow(display);
                windows.Add(window);
                window.Show();
            }
            timer = new Timer { Interval = 2200 };
            timer.Tick += delegate { Close(); };
            timer.Start();
        }

        internal static void Close()
        {
            if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
            foreach (IdentifierWindow window in windows) { window.Close(); window.Dispose(); }
            windows.Clear();
        }
    }
}
