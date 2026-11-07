using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class FeatureFixTests
    {
        internal static void Run(Action<bool, string> check)
        {
            LegacyMigration(check);
            Normalization(check);
            DialogTitles(check);
            Layout(check);
            DynamicShortcutRows(check);
        }

        static void LegacyMigration(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.FeatureFixTests." + Guid.NewGuid().ToString("N"));
            var paths = new List<string>();
            Directory.CreateDirectory(directory);
            try {
                for (int slot = 0; slot < 4; slot++) {
                    int undo = MonitorShortcutPolicy.Defaults()[slot];
                    string path = Path.Combine(directory, "legacy-undo-" + slot + ".json"); paths.Add(path);
                    File.WriteAllText(path, "{\"HiddenIdentities\":[],\"UndoMoveShortcut\":" + undo +
                        ",\"WindowShelfHoverDelayMs\":645,\"WindowShelfActivationWidth\":160,\"WindowShelfActivationHeight\":36}");
                    Settings settings = Program.Load(path);
                    var expected = MonitorShortcutPolicy.Defaults(); expected[slot] = 0;
                    check(settings.UndoMoveShortcut == undo && settings.MonitorMoveShortcuts.SequenceEqual(expected),
                        "Legacy Undo chord is retained and only its colliding monitor default is off: slot " + slot);
                    check(settings.WindowShelfActivationWidth == 160 && settings.WindowShelfActivationHeight == 36 && settings.WindowShelfHoverDelayMs == 645,
                        "Legacy shortcut migration preserves explicit Ghost Bar width, height and delay: slot " + slot);
                    var changed = settings.Copy(); changed.WindowShelfActivationWidth = 161; changed.WindowShelfActivationHeight = 48; changed.WindowShelfHoverDelayMs = 750;
                    changed.AllowMovingFullscreenApps = true;
                    // Exercise the production validator and saver using only an owned file.
                    // Calling Controller.Change here would also refresh Explorer taskbars.
                    MonitorShortcutPolicy.Validate(changed); Program.Save(changed, path);
                    Settings reloaded = Program.Load(path);
                    check(reloaded.UndoMoveShortcut == undo && reloaded.MonitorMoveShortcuts.SequenceEqual(expected) &&
                        reloaded.WindowShelfActivationWidth == 161 && reloaded.WindowShelfActivationHeight == 48 && reloaded.WindowShelfHoverDelayMs == 750 && reloaded.AllowMovingFullscreenApps,
                        "An unrelated Ghost Bar/fullscreen settings save succeeds and survives reload after migration: slot " + slot);
                    var display = new Display { Device = @"\\.\DISPLAY2", Identity = "migration-fixture", X = -1920, Y = -1080, Width = 1920, Height = 1080 };
                    foreach (int dpi in new[] { 96, 120, 144, 192 }) {
                        var preview = ShelfActivationPreviewLayout.Create(display, reloaded.WindowShelfActivationWidth, reloaded.WindowShelfActivationHeight, dpi);
                        check(preview.Area == new Rectangle(-1041, -48, 161, 48),
                            "Migrated sensitivity still previews physical-pixel activation bounds at " + dpi + " DPI: slot " + slot);
                    }
                    var dwell = new ShelfActivationDwell(); dwell.Configure(reloaded.WindowShelfActivationWidth, reloaded.WindowShelfActivationHeight, reloaded.WindowShelfHoverDelayMs);
                    check(!dwell.Update(1000, true, true) && !dwell.Update(1749, true, true) && dwell.Update(1750, true, true),
                        "Migrated hover timing still opens at the selected delay: slot " + slot);
                }
            } finally {
                foreach (string path in paths) { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
                Directory.Delete(directory);
            }
        }

        static void Normalization(Action<bool, string> check)
        {
            var defaults = MonitorShortcutPolicy.Defaults();
            var missing = new Settings { UndoMoveShortcut = defaults[1], MonitorMoveShortcuts = null };
            MonitorShortcutPolicy.Normalize(missing);
            check(missing.UndoMoveShortcut == defaults[1] && missing.MonitorMoveShortcuts.SequenceEqual(new[] { defaults[0], 0, defaults[2], defaults[3] }),
                "Missing monitor choices preserve Undo and disable a colliding default");
            var partial = new Settings { MonitorMoveShortcuts = new[] { 0 } };
            MonitorShortcutPolicy.Normalize(partial);
            check(partial.MonitorMoveShortcuts.SequenceEqual(new[] { 0, defaults[1], defaults[2], defaults[3] }),
                "A partial monitor array preserves explicit off choices and fills only missing slots");
            int custom = (int)(Keys.Control | Keys.Alt | Keys.F20);
            var supplied = new[] { custom, (int)GlobalShortcut.Default, custom, -1, defaults[0] };
            var corrupt = new Settings { MonitorMoveShortcuts = supplied };
            MonitorShortcutPolicy.Normalize(corrupt);
            check(corrupt.UndoMoveShortcut == (int)GlobalShortcut.Default && corrupt.MonitorMoveShortcuts.SequenceEqual(new[] { custom, 0, 0, 0 }),
                "An oversized array retains usable leading choices and disables invalid/duplicate/Undo conflicts");
            check(supplied.SequenceEqual(new[] { custom, (int)GlobalShortcut.Default, custom, -1, defaults[0] }),
                "Normalization owns its canonical array without changing the caller's supplied array");
            var once = corrupt.MonitorMoveShortcuts.ToArray(); MonitorShortcutPolicy.Normalize(corrupt);
            check(corrupt.MonitorMoveShortcuts.SequenceEqual(once), "Shortcut normalization is idempotent");
            int invalidUndo = 12345; var invalid = new Settings { UndoMoveShortcut = invalidUndo };
            MonitorShortcutPolicy.Normalize(invalid);
            check(invalid.UndoMoveShortcut == invalidUndo, "Normalization never rewrites an existing Undo binding, including an invalid stored value");
            var conflict = new Settings(); conflict.MonitorMoveShortcuts[0] = conflict.UndoMoveShortcut;
            bool rejected = false; try { MonitorShortcutPolicy.Validate(conflict); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Interactive conflicting assignments remain rejected instead of silently changing another action");
        }

        static void DialogTitles(Action<bool, string> check)
        {
            using (var undo = new ShortcutDialog(GlobalShortcut.Default, delegate { }))
                check(undo.Text == "Undo shortcut" && undo.AccessibleName == undo.Text && !undo.Visible,
                    "The existing Undo editor retains its action title without showing a window");
            for (int number = 1; number <= 4; number++) {
                string title = "Move to Display " + number + " shortcut";
                using (var monitor = new ShortcutDialog(title, (Keys)MonitorShortcutPolicy.Defaults()[number - 1], delegate { }))
                    check(monitor.Text == title && monitor.AccessibleName == title && !monitor.Visible,
                        "Each monitor editor exposes its own action title: " + number);
            }
        }

        static IEnumerable<Control> Controls(Control parent)
        { foreach (Control child in parent.Controls) { yield return child; foreach (Control descendant in Controls(child)) yield return descendant; } }
        static void PerformLayout(Control parent)
        { parent.PerformLayout(); foreach (Control child in parent.Controls) PerformLayout(child); }
        static void Realize(Control parent)
        {
            // Create handles inside an invisible top-level form. CreateControl
            // alone skips hidden TabPages and leaves their placeholder 200x100 size.
            if (parent.Handle == IntPtr.Zero) throw new InvalidOperationException("The owned layout control has no handle.");
            foreach (Control child in parent.Controls) Realize(child);
        }
        static Rectangle BoundsIn(Control child, Control ancestor)
        {
            var bounds = child.Bounds;
            for (Control parent = child.Parent; parent != ancestor; parent = parent.Parent) {
                if (parent == null) throw new InvalidOperationException("The control is outside the expected layout.");
                bounds.Offset(parent.Location);
            }
            return bounds;
        }
        static void Layout(Action<bool, string> check)
        {
            const string registrationError = "Ctrl + Alt + Shift + F24 could not be registered (Windows error 1409).";
            foreach (int dpi in new[] { 96, 120, 144, 192 }) {
                Size tab = SettingsForm.TabItemSize(dpi);
                using (var font = new Font("Segoe UI", 10 * dpi / 96f)) {
                    foreach (string header in new[] { "Displays", "Apps", "Shortcuts", "Appearance", "Settings" }) {
                        Size text = TextRenderer.MeasureText(header, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        check(text.Width <= tab.Width && text.Height <= tab.Height,
                            "Each consolidated section header fits its native DPI-scaled tab at " + dpi + " DPI: " + header);
                    }
                }
                check(tab.Width * 5 <= (int)Math.Floor((740 - 64) * dpi / 96f),
                    "Five tab headers fit the minimum-width content area at " + dpi + " DPI");
            }
            var controller = new Controller(false) { Settings = new Settings { WindowShelfActivationWidth = 160, WindowShelfActivationHeight = 48, WindowShelfHoverDelayMs = 645 }, Enabled = false,
                Displays = ShortcutDisplays(1, 2, 3, 4) };
            controller.MonitorShortcutErrors[1] = registrationError;
            try {
                foreach (int layoutDpi in new[] { 96, 192 })
                using (var form = new SettingsForm(controller)) {
                    var tabs = Controls(form).OfType<TabControl>().Single();
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Shortcuts");
                    form.Size = form.MinimumSize; Realize(form); PerformLayout(form);
                    // Scale only this hidden, owned native layout. Control.Scale
                    // does not change DeviceDpi or the user's display setup.
                    float scale = (float)layoutDpi / form.DeviceDpi;
                    form.AutoScaleMode = AutoScaleMode.None;
                    if (scale != 1) form.Scale(new SizeF(scale, scale));
                    form.Font = new Font("Segoe UI", 10 * layoutDpi / 96f);
                    form.Size = form.MinimumSize; PerformLayout(form);
                    if (layoutDpi == 96) {
                        Control footer = Controls(form).Single(control => control.AccessibleName == "Taskbar status").Parent;
                        var footerButtons = Controls(footer).OfType<Button>().ToArray();
                        Rectangle viewport = BoundsIn(tabs.SelectedTab, form);
                        check(form.Height <= 600 && form.ClientRectangle.Contains(viewport) && viewport.Height > 0 &&
                            tabs.SelectedTab.AutoScroll && viewport.Bottom <= BoundsIn(footer, form).Top &&
                            footerButtons.Length == 3 && footerButtons.All(button =>
                                button.Height >= button.MinimumSize.Height && form.ClientRectangle.Contains(BoundsIn(button, form))),
                            "The compact 96 DPI minimum fits on a 600 px display with a scrollable tab and all three footer buttons visible");
                    }
                    Control movement = tabs.SelectedTab.Controls[0];
                    var buttons = Controls(movement).OfType<Button>().Where(button => (button.AccessibleName ?? "").StartsWith("Change move to Display ")).ToArray();
                    check(buttons.Length == 4 && buttons.All(button => button.Width > 0 && button.Height >= button.MinimumSize.Height && movement.ClientRectangle.Contains(BoundsIn(button, movement))),
                        "All four monitor shortcut editors remain inside the real minimum-size page: page=" + movement.ClientSize +
                        "; buttons=" + String.Join("; ", buttons.Select(button => BoundsIn(button, movement).ToString() + "/minimum=" + button.MinimumSize)));
                    var actions = Controls(movement).OfType<Button>().Where(button => button.AccessibleName == "Change close focused window shortcut" ||
                        button.AccessibleName == "Change search apps shortcut").ToArray();
                    check(actions.Length == 2 && actions.All(button => button.Width > 0 && button.Height >= button.MinimumSize.Height && movement.ClientRectangle.Contains(BoundsIn(button, movement))),
                        "Both new keyboard action editors fit the actual minimum-size content at layout scale " + layoutDpi);
                    var allEditors = buttons.Concat(actions).ToArray();
                    var status = Controls(movement).OfType<Label>().Where(label =>
                        (label.AccessibleName ?? "").StartsWith("Move to Display ") && (label.AccessibleName ?? "").EndsWith("shortcut status") ||
                        label.AccessibleName == "Close focused window shortcut status" || label.AccessibleName == "Search apps shortcut status").ToArray();
                    check(status.Length == 6 && status.All(label => label.Height >= TextRenderer.MeasureText(label.Text, label.Font,
                        new Size(Math.Max(1, label.Width), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height && movement.ClientRectangle.Contains(BoundsIn(label, movement))),
                        "All six registration/display status rows remain legible inside the real minimum-size content at layout scale " + layoutDpi +
                        "; form=" + form.Size + ", client=" + form.ClientSize + ", minimum=" + form.MinimumSize +
                        ", deviceDpi=" + form.DeviceDpi + ", hostMaxTrack=" + SystemInformation.MaxWindowTrackSize +
                        ", page=" + movement.ClientRectangle + ", pageBounds=" + movement.Bounds +
                        "; labels=" + String.Join("; ", status.Select(label => "text=" + label.Text +
                            ", bounds=" + BoundsIn(label, movement) + ", local=" + label.Bounds +
                            ", textSize=" + TextRenderer.MeasureText(label.Text, label.Font) +
                            ", font=" + label.Font + ", contained=" + movement.ClientRectangle.Contains(BoundsIn(label, movement)))));
                    check(status.Any(label => label.Text == registrationError), "The minimum-size fixture includes the actual registration-error UI path");
                    var actionStatus = status.Where(label => label.AccessibleName == "Close focused window shortcut status" ||
                        label.AccessibleName == "Search apps shortcut status").ToArray();
                    check(actionStatus.Length == 2, "Both keyboard actions have distinct accessible status rows");
                    foreach (int action in new[] { 0, 1 }) controller.ActionShortcutErrors[action] = registrationError;
                    form.UpdateStatus();
                    check(actionStatus.All(label => label.Text == registrationError && label.Height >= TextRenderer.MeasureText(label.Text, label.Font,
                        new Size(label.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height &&
                        movement.ClientRectangle.Contains(BoundsIn(label, movement))),
                        "Both new action registration-error messages fit their full status rows at layout scale " + layoutDpi);
                    var movementTab = tabs.SelectedTab;
                    var editorBounds = allEditors.Select(button => BoundsIn(button, movement)).ToArray();
                    var statusBounds = status.Select(label => BoundsIn(label, movement)).ToArray();
                    var previousDock = tabs.Dock; var previousMaximum = tabs.MaximumSize; var previousSize = tabs.Size;
                    try {
                        // Exercise a real native viewport shorter than the reported
                        // hosted minimum page, without changing display topology,
                        // showing the form or relaxing the production minimum.
                        int viewportHeight = (int)Math.Ceiling(380 * layoutDpi / 96f);
                        tabs.Dock = DockStyle.None; tabs.MaximumSize = new Size(previousSize.Width, viewportHeight);
                        tabs.Size = new Size(previousSize.Width, viewportHeight);
                        PerformLayout(form);
                        check(movementTab.ClientSize.Height < (int)Math.Ceiling(511 * layoutDpi / 96f) && movementTab.AutoScroll &&
                            movementTab.VerticalScroll.Visible && movement.Height > movementTab.ClientSize.Height,
                            "The native shorter Shortcuts viewport provides scrolling instead of clipping content: viewport=" +
                            movementTab.ClientSize + ", content=" + movement.Size + ", vertical=" + movementTab.VerticalScroll.Visible);
                        check(allEditors.Select(button => BoundsIn(button, movement).Height).SequenceEqual(editorBounds.Select(bounds => bounds.Height)) &&
                            status.Select(label => BoundsIn(label, movement).Height).SequenceEqual(statusBounds.Select(bounds => bounds.Height)),
                            "Shortening the viewport preserves every editor and status row's full height");
                        foreach (Control control in allEditors.Cast<Control>().Concat(status)) {
                            movementTab.ScrollControlIntoView(control); PerformLayout(movementTab);
                            check(movementTab.ClientRectangle.Contains(BoundsIn(control, movementTab)) &&
                                movement.ClientRectangle.Contains(BoundsIn(control, movement)),
                                "Each complete monitor/action editor and status can be scrolled into the native viewport at layout scale " + layoutDpi + ": control=" +
                                (control.AccessibleName ?? control.Text) + ", bounds=" + BoundsIn(control, movementTab) +
                                ", viewport=" + movementTab.ClientRectangle + ", scroll=" + movementTab.AutoScrollPosition);
                        }
                        check(status.All(label => label.Height >= TextRenderer.MeasureText(label.Text, label.Font,
                            new Size(label.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height),
                            "The shorter viewport preserves full registration and display status text without truncating its rows");
                        foreach (string optionName in new[] { "Allow moving fullscreen apps", "Use experimental snapping" }) {
                            var option = Controls(movement).OfType<ToggleSwitch>().Single(control => control.AccessibleName == optionName);
                            movementTab.ScrollControlIntoView(option); PerformLayout(movementTab);
                            check(movementTab.ClientRectangle.Contains(BoundsIn(option, movementTab)),
                                "The named shortcut option remains fully reachable after scrolling through all six shortcut statuses: " + optionName);
                        }
                    } finally {
                        tabs.MaximumSize = previousMaximum; tabs.Size = previousSize; tabs.Dock = previousDock;
                        movementTab.AutoScrollPosition = Point.Empty; PerformLayout(form);
                    }
                    check(!form.Visible, "Minimum-size layout checks never show or activate the settings window");
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Settings");
                    PerformLayout(form);
                    var settings = tabs.SelectedTab;
                    var sensitivity = Controls(settings).OfType<NumericUpDown>().ToArray();
                    check(sensitivity.Length == 3 && sensitivity.All(field => field.Width > 0 && field.Height > 0 &&
                        field.Parent.ClientRectangle.Contains(field.Bounds)),
                        "All three native Ghost Bar sensitivity controls fit their frames at minimum form size");
                    check(sensitivity.Single(field => field.AccessibleName == "Ghost Bar activation height").Value == 48 && settings.AutoScroll,
                        "The minimum-size Settings page retains the custom height and scrolls its additional content");
                    var defaults = Controls(settings).OfType<Label>().Single(label => label.Text.StartsWith("Defaults: 480 px wide"));
                    Size defaultsText = TextRenderer.MeasureText(defaults.Text, defaults.Font,
                        new Size(Math.Max(1, defaults.Width), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    check(!defaults.AutoEllipsis && defaults.Height >= defaultsText.Height && defaults.Width > 0,
                        "The default dimensions and focus-preview instructions fit the minimum-width help label: layout=" + layoutDpi +
                        ", font=" + defaults.Font + ", client=" + defaults.ClientSize + ", required=" + defaultsText +
                        ", form=" + form.Size + ", minimum=" + form.MinimumSize + ", maxTrack=" + SystemInformation.MaxWindowTrackSize);
                    int defaultHeight = defaults.Height;
                    Font previousFont = defaults.Font;
                    using (var enlargedFont = new Font(previousFont.FontFamily, previousFont.SizeInPoints * 1.75f)) {
                        defaults.Font = enlargedFont; PerformLayout(form);
                        Size enlargedText = TextRenderer.MeasureText(defaults.Text, defaults.Font,
                            new Size(Math.Max(1, defaults.Width), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                        check(defaults.Height > defaultHeight && defaults.Height >= enlargedText.Height &&
                            defaults.Parent.ClientRectangle.Contains(defaults.Bounds),
                            "Grouped help grows with real font metrics and its card retains the complete native label at layout scale " + layoutDpi);
                        defaults.Font = previousFont; PerformLayout(form);
                    }
                    check(defaults.Height == defaultHeight && !defaults.AutoEllipsis && !form.Visible,
                        "Grouped help shrinks after font recovery without activating the settings window at layout scale " + layoutDpi);
                    var startupApps = Controls(form).OfType<Button>().Single(button => button.Text == "Startup apps");
                    check(startupApps.Enabled == !AppProfile.IsTest, "Startup apps UI is enabled only for the normal profile mode");
                }
            } finally {
                controller.Mover.Dispose(); controller.UndoShortcut.Dispose(); foreach (var shortcut in controller.MonitorShortcuts) shortcut.Dispose();
                foreach (var shortcut in controller.ActionShortcuts) shortcut.Dispose();
                foreach (var shortcut in controller.NavigationShortcuts) shortcut.Dispose();
            }
        }

        internal static List<Display> ShortcutDisplays(params int[] numbers)
        {
            return numbers.Select(number => new Display { Device = @"\\.\DISPLAY" + number,
                Identity = "owned-shortcuts-" + number, Name = "Owned display " + number,
                X = number * 800, Width = 800, Height = 600, WorkHeight = 560 }).ToList();
        }

        static Button[] MonitorEditors(TabPage page)
        {
            return Controls(page).OfType<Button>().Where(button => (button.AccessibleName ?? "").StartsWith("Change move to Display ", StringComparison.Ordinal))
                .OrderBy(button => BoundsIn(button, page).Top).ToArray();
        }

        static void DynamicShortcutRows(Action<bool, string> check)
        {
            var settings = new Settings();
            Keys fifthChord = Keys.Control | Keys.Shift | Keys.F17;
            MonitorShortcutPolicy.SetChoice(settings, 5, (int)fifthChord);
            var controller = new Controller(false) { Settings = settings, Displays = ShortcutDisplays(5, 2), Enabled = false };
            try {
                using (var form = new SettingsForm(controller)) {
                    var tabs = Controls(form).OfType<TabControl>().Single();
                    var page = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Shortcuts");
                    tabs.SelectedTab = page; form.Size = form.MinimumSize; Realize(form); PerformLayout(form);
                    Action<int[]> expect = numbers => {
                        PerformLayout(form);
                        var editors = MonitorEditors(page);
                        check(editors.Select(button => button.AccessibleName).SequenceEqual(numbers.Select(number => "Change move to Display " + number + " shortcut")) &&
                            editors.Select(button => button.Parent.TabIndex).SequenceEqual(Enumerable.Range(2, numbers.Length)),
                            "Monitor shortcut rows match connected Windows numbers in visual and tab order: " + String.Join(",", numbers));
                    };
                    expect(new[] { 2, 5 });
                    var original = MonitorEditors(page);
                    check(original[1].Text == GlobalShortcut.Format(fifthChord), "Sparse display 5 shows its stored custom binding");
                    controller.Revision++; form.UpdateStatus();
                    check(MonitorEditors(page).SequenceEqual(original), "An unrelated refresh preserves the existing monitor editor controls");

                    controller.Displays = ShortcutDisplays(6, 5, 2, 4, 1, 3); controller.Revision++; form.UpdateStatus();
                    expect(new[] { 1, 2, 3, 4, 5, 6 });
                    var six = MonitorEditors(page);
                    check(six[1] == original[0] && six[4] == original[1] && six[5].Text == "Off", "Connecting four more monitors preserves existing controls and starts a new extra binding Off");
                    int viewportWidth = tabs.Width;
                    tabs.Dock = DockStyle.None;
                    tabs.MaximumSize = new Size(viewportWidth, (int)Math.Ceiling(380 * form.DeviceDpi / 96f));
                    tabs.Size = tabs.MaximumSize;
                    PerformLayout(form);
                    check(page.VerticalScroll.Visible, "Six dynamic monitor rows keep the Shortcuts page scrollable");
                    var undo = Controls(page).OfType<Button>().Single(button => button.AccessibleName == "Change Undo shortcut");
                    var fullscreen = Controls(page).OfType<ToggleSwitch>().Single(toggle => toggle.AccessibleName == "Allow moving fullscreen apps");
                    foreach (Control editor in new Control[] { six[5], undo, fullscreen }) {
                        Point previousScroll = page.AutoScrollPosition;
                        Rectangle before = BoundsIn(editor, page);
                        page.ScrollControlIntoView(editor); PerformLayout(page);
                        Rectangle after = BoundsIn(editor, page);
                        check(page.ClientRectangle.Contains(after), "Six-monitor layout keeps the complete control reachable: " + editor.AccessibleName +
                            ", before=" + before + ", after=" + after + ", viewport=" + page.ClientRectangle +
                            ", scroll=" + previousScroll + " -> " + page.AutoScrollPosition +
                            ", parentBounds=" + BoundsIn(editor.Parent, page));
                    }

                    controller.Displays = ShortcutDisplays(6, 4, 2); controller.Revision++; form.UpdateStatus();
                    expect(new[] { 2, 4, 6 });
                    check(original[1].IsDisposed && MonitorEditors(page)[0] == original[0] && MonitorShortcutPolicy.Choice(settings, 5) == (int)fifthChord,
                        "Unplugging display 5 removes only its row and retains the saved shortcut");
                    controller.Displays = ShortcutDisplays(6, 2, 5); controller.Revision++; form.UpdateStatus();
                    expect(new[] { 2, 5, 6 });
                    check(MonitorEditors(page)[1].Text == GlobalShortcut.Format(fifthChord) && !form.Visible && controller.History.Peek == null,
                        "Reconnecting display 5 restores its saved choice without applying settings or activating a window");
                }
            } finally {
                controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                foreach (var binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
            }
        }
    }
}
