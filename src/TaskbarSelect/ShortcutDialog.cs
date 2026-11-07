using System;
using System.Drawing;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed class ShortcutDialog : Form
    {
        readonly ToggleSwitch enabled = new ToggleSwitch("Enabled");
        readonly CheckBox control = new CheckBox { Text = "Ctrl", AutoSize = true };
        readonly CheckBox alt = new CheckBox { Text = "Alt", AutoSize = true };
        readonly CheckBox shift = new CheckBox { Text = "Shift", AutoSize = true };
        readonly ComboBox key = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly Label preview = Ui.Label(""), error = Ui.Label("");
        readonly Button save;
        readonly Keys[] choices = GlobalShortcut.AvailableKeys();
        internal ShortcutDialog(Keys current, Action<Keys> apply)
            : this("Undo shortcut", current, apply) { }
        internal ShortcutDialog(string title, Keys current, Action<Keys> apply, Keys defaultChoice = Keys.None)
        {
            if (String.IsNullOrWhiteSpace(title)) throw new ArgumentException("A shortcut action title is required.", "title");
            Text = title; AccessibleName = title; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(430, 290); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18) };
            foreach (int height in new[] { 38, 32, 36, 38 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.Controls.Add(enabled, 0, 0); root.Controls.Add(preview, 0, 1);
            var modifiers = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            modifiers.Controls.Add(control); modifiers.Controls.Add(alt); modifiers.Controls.Add(shift); root.Controls.Add(modifiers, 0, 2);
            var keyRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60)); keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            keyRow.Controls.Add(Ui.Label("Key"), 0, 0); keyRow.Controls.Add(key, 1, 0); root.Controls.Add(keyRow, 0, 3);
            key.AccessibleName = "Shortcut key";
            foreach (Keys choice in choices) key.Items.Add(GlobalShortcut.KeyName(choice));
            error.AutoEllipsis = false; root.Controls.Add(error, 0, 4);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            save = Ui.Button("Save", null, delegate {
                try { apply(SelectedShortcut); DialogResult = DialogResult.OK; Close(); }
                catch (Exception exception) { error.Text = exception.Message; }
            });
            var cancel = Ui.Button("Cancel", null, delegate { DialogResult = DialogResult.Cancel; Close(); });
            actions.Controls.Add(save); actions.Controls.Add(cancel); root.Controls.Add(actions, 0, 5);
            AcceptButton = save; CancelButton = cancel; Controls.Add(root); Ui.Theme(this);
            enabled.Checked = current != Keys.None;
            Keys fallback = GlobalShortcut.IsValid(defaultChoice) && defaultChoice != Keys.None ? defaultChoice : GlobalShortcut.Default;
            Keys value = GlobalShortcut.IsValid(current) && current != Keys.None ? current : fallback;
            control.Checked = (value & Keys.Control) != 0; alt.Checked = (value & Keys.Alt) != 0; shift.Checked = (value & Keys.Shift) != 0;
            key.SelectedIndex = Array.IndexOf(choices, value & Keys.KeyCode);
            enabled.CheckedChanged += delegate { UpdateChoice(); }; control.CheckedChanged += delegate { UpdateChoice(); };
            alt.CheckedChanged += delegate { UpdateChoice(); }; shift.CheckedChanged += delegate { UpdateChoice(); };
            key.SelectedIndexChanged += delegate { UpdateChoice(); }; UpdateChoice();
        }
        Keys SelectedShortcut
        {
            get {
                if (!enabled.Checked) return Keys.None;
                return (key.SelectedIndex < 0 ? Keys.None : choices[key.SelectedIndex]) |
                    (control.Checked ? Keys.Control : Keys.None) | (alt.Checked ? Keys.Alt : Keys.None) | (shift.Checked ? Keys.Shift : Keys.None);
            }
        }
        void UpdateChoice()
        {
            control.Enabled = alt.Enabled = shift.Enabled = key.Enabled = enabled.Checked;
            save.Enabled = GlobalShortcut.IsValid(SelectedShortcut);
            preview.Text = GlobalShortcut.Format(SelectedShortcut);
            error.Text = save.Enabled ? "" : "Choose at least two modifiers.";
        }
    }
}
