using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Owned hidden settings windows only. These checks never click a real app,
    // register a shortcut, show a window, or change the desktop's DPI or theme.
    internal static class AppPolishTests
    {
        internal static void Run(Action<bool, string> check)
        {
            SettingsAndReachability(check);
            SettingsAndReachability(check, new Size(1022, 718));
            NativeTabSelection(check);
            OwnedControlSemantics(check);
            ThemeRefresh(check);
            EmptyAppsState(check);
            ConsolidatedShortcutErrors(check);
            StableFrameSamples(check);
            CompressedFrameSamples(check);
            CompactAccentChips(check);
        }

        static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls) {
                yield return child;
                foreach (Control descendant in Descendants(child)) yield return descendant;
            }
        }
        static void ThemeRefresh(Action<bool, string> check)
        {
            // Simulate cached controls from a previous palette. This exercises
            // refresh and resource lifetime without changing any Windows theme.
            Controller controller = Fixture();
            byte[] bytesBefore = SettingsBytes();
            string before = Serialize(controller.Settings);
            Color accentBefore = Ui.Accent;
            try {
                using (var form = new SettingsForm(controller)) {
                    var button = Descendants(form).OfType<AppButton>().First(control => control.Image != null && control.Tag is string);
                    var frame = Descendants(form).OfType<FieldFrame>().First();
                    var rule = Descendants(form).OfType<SectionRule>().First();
                    string iconName = (string)button.Tag;
                    Size iconSize = button.Image.Size;
                    object iconTag = button.Tag;
                    bool previousDark = !Ui.ToolbarDarkSurface;
                    Bitmap staleIcon;
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TaskbarSelect.Toolbar." + iconName + "-" + (previousDark ? "dark" : "light") + ".png")) {
                        if (stream == null) throw new InvalidOperationException("Owned theme fixture icon is missing.");
                        using (var image = Image.FromStream(stream)) staleIcon = new Bitmap(image, iconSize);
                    }
                    Image previousIcon = button.Image;
                    button.Image = staleIcon; previousIcon.Dispose();
                    button.IconOnDarkSurface = previousDark;
                    button.BackColor = Color.Magenta;
                    button.ForeColor = Color.Yellow;
                    button.FlatAppearance.BorderColor = Color.Lime;
                    button.FlatAppearance.MouseOverBackColor = Color.Red;
                    button.FlatAppearance.MouseDownBackColor = Color.Blue;
                    frame.BackColor = Color.Magenta;
                    rule.BackColor = Color.Magenta;
                    Control[] controls = Descendants(form).Concat(new Control[] { form }).ToArray();
                    bool[] handles = controls.Select(control => control.IsHandleCreated).ToArray();

                    Ui.Theme(form);
                    check(button.BackColor == Ui.Surface && button.ForeColor == Ui.Text && button.FlatAppearance.BorderColor == Ui.Line &&
                        button.FlatAppearance.MouseOverBackColor == Ui.Hover && button.FlatAppearance.MouseDownBackColor == Ui.Hover &&
                        frame.BackColor == Ui.Surface && rule.BackColor == Ui.Line,
                        "Theme refresh replaces stale button, field and rule colors with the current palette");
                    check(button.IconOnDarkSurface == Ui.ToolbarDarkSurface && !Object.ReferenceEquals(staleIcon, button.Image) &&
                        button.Image.Size == iconSize && Object.Equals(button.Tag, iconTag),
                        "Theme refresh replaces a stale icon palette while preserving its size and action tag");
                    bool disposed = false;
                    try { staleIcon.GetPixel(0, 0); } catch (ArgumentException) { disposed = true; }
                    check(disposed, "Theme refresh disposes the replaced owned toolbar image");
                    using (Bitmap expected = Ui.LoadToolbarIcon(iconName, iconSize.Width)) {
                        bool matches = expected != null;
                        for (int y = 0; matches && y < expected.Height; y++)
                            for (int x = 0; matches && x < expected.Width; x++)
                                matches = ((Bitmap)button.Image).GetPixel(x, y).ToArgb() == expected.GetPixel(x, y).ToArgb();
                        check(matches, "The refreshed toolbar pixels match the current palette resource");
                    }
                    Image refreshed = button.Image;
                    Ui.Theme(form); Ui.Theme(form);
                    check(Object.ReferenceEquals(refreshed, button.Image) && controls.Select(control => control.IsHandleCreated).SequenceEqual(handles),
                        "Repeated theme refresh retains the current toolbar image and does not create hidden HWNDs");
                    check(FocusAppearanceSettingsTests.SameJson(Serialize(controller.Settings), before) && controller.Revision == 0 && controller.History.Peek == null &&
                        SameBytes(bytesBefore, SettingsBytes()) && Ui.Accent == accentBefore && !form.Visible,
                        "Simulated palette refresh preserves saved settings, history, accent and hidden ownership");
                }
            } finally { Release(controller); }
        }
        static void Realize(Control root)
        {
            if (root.Handle == IntPtr.Zero) throw new InvalidOperationException("Owned polish handle was not created.");
            foreach (Control child in root.Controls) Realize(child);
        }
        static void Layout(Control root)
        {
            root.PerformLayout(); foreach (Control child in root.Controls) Layout(child);
        }
        static Rectangle BoundsIn(Control control, Control ancestor)
        {
            Rectangle bounds = control.Bounds;
            for (Control parent = control.Parent; parent != ancestor; parent = parent.Parent) {
                if (parent == null) throw new InvalidOperationException("Owned polish control is outside its page.");
                bounds.Offset(parent.Location);
            }
            return bounds;
        }
        static string Serialize(Settings settings) { return new JavaScriptSerializer().Serialize(settings); }
        static byte[] SettingsBytes() { return File.Exists(Program.ConfigPath) ? File.ReadAllBytes(Program.ConfigPath) : null; }
        static bool SameBytes(byte[] previous, byte[] current)
        {
            return previous == null ? current == null : current != null && previous.SequenceEqual(current);
        }
        static int[] RenderedArea(Control control, Rectangle area)
        {
            using (var image = new Bitmap(control.Width, control.Height)) {
                control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size));
                var pixels = new int[area.Width * area.Height]; int next = 0;
                for (int y = area.Top; y < area.Bottom; y++)
                    for (int x = area.Left; x < area.Right; x++) pixels[next++] = image.GetPixel(x, y).ToArgb();
                return pixels;
            }
        }
        static void StableFrameSamples(Action<bool, string> check)
        {
            byte[] bytesBefore = SettingsBytes(); Color accentBefore = Ui.Accent;
            foreach (int dpi in new[] { 96, 192 })
            using (var host = new Form { Size = new Size(900, 500) })
            using (var font = new Font("Segoe UI", 10 * dpi / 96f)) {
                var page = new AppearancePage(); host.Controls.Add(page);
                Realize(host); Layout(host);
                float scale = dpi / (float)host.DeviceDpi; host.AutoScaleMode = AutoScaleMode.None;
                if (scale != 1) host.Scale(new SizeF(scale, scale));
                host.Font = font;
                page.FocusFrame.Checked = true; page.AccentChoices.SelectedIndex = 0; page.LightingChoices.SelectedIndex = 0;
                Layout(host);
                AppearanceChoiceCard[] samples = page.FinishChoices.Cards;
                int[][] unlitSamples = samples.Select(card => RenderedArea(card, card.PreviewBounds)).ToArray();
                foreach (int finish in new[] { 0, 1 }) {
                    page.FinishChoices.SelectedIndex = finish; page.LightingChoices.SelectedIndex = 0; Layout(host);
                    int[] unlitCombined = RenderedArea(page.CombinedPreview, page.CombinedPreview.ClientRectangle);
                    foreach (int light in new[] { 0, 1, 2, 3 }) {
                        page.LightingChoices.SelectedIndex = light; Layout(host);
                        for (int sample = 0; sample < samples.Length; sample++)
                            check(RenderedArea(samples[sample], samples[sample].PreviewBounds).SequenceEqual(unlitSamples[sample]),
                                "Frame style samples stay pixel-identical and unlit across finish and lighting selections at layout scale " + dpi + ": " + samples[sample].Text + "/" + FocusAppearance.LightingLabels[light]);
                        check(page.CombinedPreview.Finish == (FocusFrameFinish)finish && page.CombinedPreview.Lighting == (FocusLightingMode)light &&
                            !page.CombinedPreview.AnimationRunning,
                            "The combined preview keeps the complete chosen finish and lighting without animating the samples at layout scale " + dpi + ": " + finish + "/" + light);
                        if (!SystemInformation.HighContrast && light != 0)
                            check(!RenderedArea(page.CombinedPreview, page.CombinedPreview.ClientRectangle).SequenceEqual(unlitCombined),
                                "The combined preview still shows the chosen lighting rather than the unlit frame sample at layout scale " + dpi + ": " + finish + "/" + light);
                    }
                }
                page.AccentChoices.SelectedIndex = Array.IndexOf(page.AccentChoices.Items, "Coral"); Layout(host);
                check(!RenderedArea(samples[1], samples[1].PreviewBounds).SequenceEqual(unlitSamples[1]) &&
                    page.CombinedPreview.AccentPreset == "Coral",
                    "Stable frame samples still update to the chosen accent and the combined preview follows it at layout scale " + dpi);
                int selectedFinish = page.FinishChoices.SelectedIndex, selectedLight = page.LightingChoices.SelectedIndex;
                page.FocusFrame.Checked = false;
                check(!page.FinishChoices.Enabled && !page.LightingChoices.Enabled && page.FinishChoices.SelectedIndex == selectedFinish &&
                    page.LightingChoices.SelectedIndex == selectedLight && page.CombinedPreview.FrameStyle == FocusIndicatorMode.Off,
                    "Turning the frame off retains both selected values while disabling their controls at layout scale " + dpi);
                page.FocusFrame.Checked = true;
                check(page.FinishChoices.Enabled && page.LightingChoices.Enabled && page.CombinedPreview.Finish == (FocusFrameFinish)selectedFinish &&
                    page.CombinedPreview.Lighting == (FocusLightingMode)selectedLight && !host.Visible,
                    "Turning the frame back on restores the complete preview without showing an owned window at layout scale " + dpi);
            }
            check(Ui.Accent == accentBefore && SameBytes(bytesBefore, SettingsBytes()),
                "Frame sample rendering and selection never write a profile or change the live accent");
        }
        static void CompressedFrameSamples(Action<bool, string> check)
        {
            byte[] bytesBefore = SettingsBytes(); Color accentBefore = Ui.Accent;
            FieldInfo imageField = typeof(AppearancePreviewScene).GetField("image", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo frameField = typeof(AppearancePreviewScene).GetField("frame", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo boundsField = typeof(AppearancePreviewScene).GetField("imageBounds", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (int dpi in new[] { 96, 192, 288, 384 }) foreach (int finish in new[] { 0, 1 }) foreach (int lighting in new[] { 0, 1, 2, 3 }) {
                var band = new Rectangle(0, 0, 170 * dpi / 96, 24 * dpi / 96);
                using (var scene = new AppearancePreviewScene())
                using (var canvas = new Bitmap(band.Width, band.Height))
                using (Graphics graphics = Graphics.FromImage(canvas)) {
                    scene.Draw(graphics, band, FocusIndicatorMode.InsetEdge, (FocusLightingMode)lighting, "Forest Green", dpi, false, finish);
                    var image = (Bitmap)imageField.GetValue(scene);
                    var frame = (Rectangle)frameField.GetValue(scene);
                    var bounds = (Rectangle)boundsField.GetValue(scene);
                    string choice = dpi + "/finish " + finish + "/lighting " + lighting;
                    check(image != null && image.Width > 0 && image.Height > 0 && bounds.Size == image.Size,
                        "A compressed 24 DIP sample retains its enabled frame image at every supported sample scale: " + choice);
                    check(band.Contains(frame) && band.Contains(bounds) && frame.Height > 2 * (finish == 1 ? dpi / 96 : 2 * dpi / 96),
                        "The complete compressed frame and clipped render bounds fit inside the decorative sample band: " + choice +
                        "; frame=" + frame + ", image=" + bounds + ", band=" + band);
                    int x = frame.Left + frame.Width / 2 - bounds.Left, y = frame.Top - bounds.Top;
                    check(x >= 0 && y >= 0 && x < image.Width && y < image.Height && image.GetPixel(x, y).A > 0,
                        "The compressed sample has a visible core stroke rather than only an empty cached image: " + choice);
                }
            }
            check(Ui.Accent == accentBefore && SameBytes(bytesBefore, SettingsBytes()),
                "Compressed high-DPI sample rendering leaves the live palette and profile bytes unchanged");
        }
        static void CompactAccentChips(Action<bool, string> check)
        {
            byte[] bytesBefore = SettingsBytes(); Color accentBefore = Ui.Accent;
            foreach (int dpi in new[] { 96, 144, 192 }) foreach (int fontPoints in new[] { 10, 14 }) {
                    using (var form = new Form { ClientSize = new Size(1000, 700) })
                    using (var font = new Font("Segoe UI", fontPoints * dpi / 96f)) {
                        string[] names = FocusAppearance.Presets;
                        var colors = new AppearanceChoiceGrid(names, names.Select(name => name + " accent").ToArray(), AppearanceChoiceKind.Accent, 6);
                        form.Controls.Add(colors);
                        Realize(form); Layout(form);
                        float scale = dpi / (float)form.DeviceDpi; form.AutoScaleMode = AutoScaleMode.None;
                        if (scale != 1) form.Scale(new SizeF(scale, scale));
                        form.Font = font;
                        colors.SelectedIndex = Array.IndexOf(names, "Forest Green");
                        double logicalScale = dpi / 96.0;
                        int largestFamily = FocusAppearance.AccentFamilyLabels.Max(family => FocusAppearance.AccentFamilyPresets(family).Length);
                        int previousRowCount = 0;
                        foreach (int width in new[] { 650, 800, 960 }) {
                            int physicalWidth = (int)Math.Ceiling(width * logicalScale);
                            colors.Size = colors.GetPreferredSize(new Size(physicalWidth, 0));
                            Layout(form);
                            AppearanceChoiceCard[] cards = colors.Cards;
                            int rowCount = cards.GroupBy(card => card.Top).Max(row => row.Count());
                            check(cards.Length == FocusAppearance.Presets.Length && colors.ClientSize.Width > 0 &&
                                colors.ClientRectangle.Contains(new Rectangle(Point.Empty, colors.GetPreferredSize(colors.ClientSize))) &&
                                rowCount <= 6 && rowCount >= previousRowCount,
                                "Natural color chips reflow within the supported page width without stretching or exceeding six choices per row: " + dpi + "/font " + fontPoints + "/width " + width);
                            if (fontPoints == 10)
                                check(rowCount >= Math.Min(largestFamily, width == 650 ? 5 : 6),
                                    "Compact color choices fill the available family row at supported narrow/default/wide grid widths: " + dpi + "/width " + width + "/choices per row " + rowCount);
                            previousRowCount = rowCount;
                            foreach (AppearanceChoiceCard card in cards) {
                                Rectangle title = card.TitleBounds, swatch = card.PreviewBounds, radio = card.RadioBounds;
                                Size text;
                                using (var titleFont = new Font(card.Font, FontStyle.Bold))
                                    text = TextRenderer.MeasureText(card.Text, titleFont, new Size(Math.Max(1, title.Width), Int32.MaxValue),
                                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                                int rowTitleHeight = cards.Where(other => other.Top == card.Top).Max(other => other.TitleBounds.Height);
                                int heightLimit = Math.Max((int)Math.Ceiling(64 * logicalScale), rowTitleHeight + (int)Math.Ceiling(24 * logicalScale));
                                check(card.Width <= Math.Ceiling(180 * logicalScale) && card.Width <= card.MaximumAccentWidth && card.Height <= heightLimit &&
                                    swatch.Width == swatch.Height && swatch.Width <= Math.Ceiling(32 * logicalScale) &&
                                    card.ClientRectangle.Contains(swatch) && card.ClientRectangle.Contains(title) && card.ClientRectangle.Contains(radio) &&
                                    swatch.Right <= title.Left && title.Right <= radio.Left &&
                                    !swatch.IntersectsWith(title) && !swatch.IntersectsWith(radio) && !title.IntersectsWith(radio) &&
                                    title.Width >= text.Width && title.Height >= text.Height && card.DescriptionBounds.IsEmpty &&
                                    colors.ClientRectangle.Contains(card.Bounds) && cards.Where(other => other != card).All(other => !other.Bounds.IntersectsWith(card.Bounds)),
                                    "Each compact color chip keeps its full name, square swatch and radio separate at supported widths and larger fonts: " + card.Text + "/" + dpi +
                                    "/font " + fontPoints + "/width " + width + "; bounds=" + card.Bounds + "; text=" + title + "/" + text + "; swatch=" + swatch + "; radio=" + radio);
                            }
                            check(colors.Cards.Count(card => card.Checked) == 1 && colors.SelectedItem == "Forest Green" &&
                                colors.DisplayOrder.Select(card => card.PresetName).SequenceEqual(FocusAppearance.AccentFamilyLabels.SelectMany(FocusAppearance.AccentFamilyPresets)),
                                "Resizing and enlarging fonts preserve one selected color and organized native reading order: " + dpi + "/font " + fontPoints + "/width " + width);
                        }
                        check(!form.Visible && SameBytes(bytesBefore, SettingsBytes()) && Ui.Accent == accentBefore,
                            "Standalone compact color layout checks keep owned windows hidden and preserve profile bytes and the live palette: " + dpi + "/font " + fontPoints);
                    }
            }
        }
        static Controller Fixture()
        {
            var settings = new Settings {
                WindowShelfActivationWidth = 680, WindowShelfActivationHeight = 34, WindowShelfHoverDelayMs = 450,
                AllowMovingFullscreenApps = true, ScaleWindowSizeToMonitor = false, MoveWindowsToClickedTaskbar = false,
                FocusIndicatorStyle = 2, FocusIndicatorLighting = 3, AccentPreset = "Forest Green",
                UndoMoveShortcut = (int)(Keys.Control | Keys.Alt | Keys.Back), MonitorMoveShortcuts = new int[4], FocusWindowShortcuts = new int[4],
                CloseWindowShortcut = 0, SearchAppsShortcut = 0, MinimizeWindowShortcut = 0, PinWindowShortcut = 0, TileWindowShortcut = 0
            };
            settings.HiddenIdentities.Add("owned-polish-north");
            settings.WindowShelfIdentities.Add("owned-polish-north");
            settings.MonitorAliases.Add("owned-polish-north", "Owned upper display");
            settings.ExcludedApps.Add(new AppRule { Key = "exe:owned-polish-app", Name = "Owned polish app" });
            return new Controller(false) {
                Settings = settings, Enabled = false,
                Displays = new List<Display> {
                    new Display { Device = @"\\.\DISPLAY1", Identity = "owned-polish-north", Name = "Owned upper display", X = 0, Y = -1080, Width = 1920, Height = 1080, WorkHeight = 1040 },
                    new Display { Device = @"\\.\DISPLAY2", Identity = "owned-polish-main", Name = "Owned main display", Primary = true, X = 0, Y = 0, Width = 2560, Height = 1440, WorkHeight = 1400 },
                    new Display { Device = @"\\.\DISPLAY3", Identity = "owned-polish-left", Name = "Owned left display", X = -1280, Y = 0, Width = 1280, Height = 720, WorkHeight = 680 },
                    new Display { Device = @"\\.\DISPLAY4", Identity = "owned-polish-right", Name = "Owned right display", X = 2560, Y = 200, Width = 1920, Height = 1080, WorkHeight = 1040 }
                }
            };
        }
        static void Release(Controller controller)
        {
            // Controller.Dispose restores global taskbars. These fixtures own no
            // desktop state and release only their own hook-free components.
            controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
            foreach (GlobalShortcut shortcut in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) shortcut.Dispose();
        }
        static Control[] ReachableActions(TabPage page)
        {
            return Descendants(page).Where(control =>
                control is TextBox || control is ComboBox || control is NumericUpDown || control is ToggleSwitch || control is RadioButton ||
                control is Button && !(control.Tag is Display)).ToArray();
        }
        static void SettingsAndReachability(Action<bool, string> check, Size? ownedMinimum = null)
        {
            Color accentBefore = Ui.Accent;
            byte[] bytesBefore = SettingsBytes();
            foreach (int dpi in ownedMinimum.HasValue ? new[] { 192 } : new[] { 96, 192 }) {
                Controller controller = Fixture();
                string before = Serialize(controller.Settings);
                try {
                    using (var form = new SettingsForm(controller))
                    using (var font = new Font("Segoe UI", 10 * dpi / 96f)) {
                        var tabs = Descendants(form).OfType<TabControl>().Single();
                        check(form.Text == AppProfile.Title && tabs.TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(
                            new[] { "Displays", "Apps", "Shortcuts", "Appearance", "Settings" }),
                            "Polished app keeps its correct profile title and five clearly named sections at layout scale " + dpi);
                        var identity = Descendants(form).Where(control => control.AccessibleName == "Application title" ||
                            control.AccessibleName == "Application subtitle" || control.AccessibleName == "Profile badge").ToArray();
                        check(identity.Length == 3 && identity.Where(control => control.AccessibleName != "Profile badge").All(control => !String.IsNullOrWhiteSpace(control.Text)) &&
                            identity.Single(control => control.AccessibleName == "Application title").Text == "Taskbar Select" &&
                            identity.Single(control => control.AccessibleName == "Profile badge").Text == (AppProfile.IsTest ? "ISOLATED TEST" : ""),
                            "The compact app header names the product clearly and keeps isolated testing explicit at layout scale " + dpi);
                        form.Size = form.MinimumSize; Realize(form); Layout(form);
                        Size previousMinimum = form.MinimumSize, nativeChrome = form.Size - form.ClientSize;
                        float scale = dpi / (float)form.DeviceDpi; form.AutoScaleMode = AutoScaleMode.None;
                        if (scale != 1) form.Scale(new SizeF(scale, scale));
                        form.Font = font; form.Size = form.MinimumSize; Layout(form);
                        FocusAppearanceSettingsTests.MinimumFixtureBounds(form, tabs, dpi, check, previousMinimum, nativeChrome, scale);
                        if (ownedMinimum.HasValue) {
                            // The hosted runner's constrained minimum is a real
                            // small-viewport regression, not a reason to resize
                            // the desktop or omit native reachability checks.
                            form.MinimumSize = ownedMinimum.Value; form.Size = form.MinimumSize; Layout(form);
                            check(form.MinimumSize == ownedMinimum.Value && form.Size == ownedMinimum.Value,
                                "The owned app regression retains the exact hosted 1022x718 minimum without expanding its viewport");
                        }
                        foreach (Control label in identity.Where(control => control.AccessibleName != "Profile badge")) {
                            Size text = TextRenderer.MeasureText(label.Text, label.Font, new Size(Int32.MaxValue, Int32.MaxValue),
                                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                            check(label.Parent.ClientRectangle.Contains(label.Bounds) && form.ClientRectangle.Contains(BoundsIn(label, form)) &&
                                label.ClientSize.Width >= text.Width && label.ClientSize.Height >= text.Height,
                                "The complete app header label fits its heading row and minimum form bounds at layout scale " + dpi + ": " + label.AccessibleName);
                        }
                        check(tabs.TabStop && tabs.Appearance == TabAppearance.Normal && tabs.GetTabRect(4).Right <= tabs.ClientSize.Width,
                            "All five native, focusable section headers fit the minimum app width at layout scale " + dpi);
                        var settingsPage = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Settings");
                        var numbers = Descendants(settingsPage).OfType<NumericUpDown>().ToArray();
                        check(numbers.Length == 3 && numbers.All(input => input.TabStop && input.DecimalPlaces == 0 && !input.ThousandsSeparator &&
                            !String.IsNullOrWhiteSpace(input.AccessibleName) && !String.IsNullOrWhiteSpace(input.AccessibleDescription)),
                            "All Ghost Bar fields remain native keyboard-editable numeric controls with accessible names and help at layout scale " + dpi);
                        check(numbers.Single(input => input.AccessibleName == "Ghost Bar activation width").Value == 680 &&
                            numbers.Single(input => input.AccessibleName == "Ghost Bar activation height").Value == 34 &&
                            numbers.Single(input => input.AccessibleName == "Ghost Bar hover delay").Value == 450,
                            "The polished Ghost Bar section shows existing custom width, height and delay without substituting defaults at layout scale " + dpi);
                        var selectors = Descendants(form).OfType<ComboBox>().ToArray();
                        check(selectors.Length == 1 && selectors.All(input => input.TabStop && input.DropDownStyle == ComboBoxStyle.DropDownList &&
                            !String.IsNullOrWhiteSpace(input.AccessibleName) && !String.IsNullOrWhiteSpace(input.AccessibleDescription)),
                            "Window sizing keeps its native selector while appearance no longer depends on closed dropdowns at layout scale " + dpi);
                        AppearancePage appearance = Descendants(form).OfType<AppearancePage>().Single();
                        check(selectors.Single(input => input.AccessibleName == "Window sizing").SelectedIndex == 0 &&
                            appearance.FocusFrame.Checked && appearance.FinishChoices.SelectedIndex == 0 && appearance.LightingChoices.SelectedIndex == 3 &&
                            appearance.AccentChoices.SelectedItem == "Forest Green",
                            "Visual appearance choices preserve the existing sizing, enabled muted Focus frame, Energy flow and accent values at layout scale " + dpi);
                        check(appearance.Parent is TabPage && appearance.Parent.Text == "Appearance" &&
                            !Descendants(settingsPage).OfType<AppearanceChoiceGrid>().Any() &&
                            typeof(CheckBox).IsAssignableFrom(appearance.FocusFrame.GetType()) && appearance.FocusFrame.Text == "Focus frame" &&
                            appearance.FinishChoices.Cards.Length == 2 && appearance.LightingChoices.Cards.Length == 4 && appearance.AccentChoices.Cards.Length == FocusAppearance.Presets.Length &&
                            appearance.AccentChoices.FamilyHeadings.Select(heading => heading.Text).SequenceEqual(FocusAppearance.AccentFamilyLabels) &&
                            appearance.AccentChoices.DisplayOrder.Select(card => card.PresetName).SequenceEqual(FocusAppearance.AccentFamilyLabels.SelectMany(FocusAppearance.AccentFamilyPresets)) &&
                            Descendants(appearance).OfType<RadioButton>().Count() == 6 + FocusAppearance.Presets.Length,
                            "One native Focus frame toggle and every frame style, lighting and accent option are exposed in the dedicated Appearance section at layout scale " + dpi);
                        check(appearance.FinishChoices.Parent == appearance.FocusFrame.Parent && appearance.FinishChoices.Parent is SettingsCard &&
                            appearance.FinishChoices.Top >= appearance.FocusFrame.Bottom && appearance.FinishChoices.AccessibleName == "Frame style" &&
                            appearance.FinishChoices.AccessibleRole == AccessibleRole.Grouping && appearance.FinishChoices.TabIndex > appearance.FocusFrame.TabIndex &&
                            Descendants(appearance).OfType<SettingsCard>().Count() == 4 &&
                            appearance.FinishChoices.Cards.All(card => card.Kind == AppearanceChoiceKind.Finish && card.AccessibleRole == AccessibleRole.RadioButton &&
                                !String.IsNullOrWhiteSpace(card.AccessibleName) && !String.IsNullOrWhiteSpace(card.AccessibleDescription)),
                            "Frame styles form their own accessible keyboard group beneath the switch within the existing Focus frame card at layout scale " + dpi);
                        foreach (TabPage page in tabs.TabPages) {
                            tabs.SelectedTab = page; Layout(form);
                            Control[] actions = ReachableActions(page);
                            check(actions.Length > 0 && actions.All(control => control.Width > 0 && control.Height > 0 &&
                                page.Controls[0].ClientRectangle.Contains(BoundsIn(control, page.Controls[0]))),
                                "Every released section lays out its actions inside the content instead of overlapping or clipping them at layout scale " + dpi + ": " + page.Text);
                            foreach (Control action in actions) {
                                if (page.AutoScroll) page.ScrollControlIntoView(action);
                                Layout(form);
                                check(page.ClientRectangle.Contains(BoundsIn(action, page)),
                                    "Each setting or action is fully reachable in the minimum native viewport at layout scale " + dpi + ": " + page.Text + "/" + (action.AccessibleName ?? action.Text));
                            }
                            if (page.AutoScroll) page.AutoScrollPosition = Point.Empty;
                        }
                        var appList = Descendants(form).OfType<ListView>().Single();
                        check(appList.Items.Count == 1 && appList.Items[0].Text == "Owned polish app" &&
                            (string)appList.Items[0].Tag == "exe:owned-polish-app" && appList.FullRowSelect && !appList.MultiSelect &&
                            appList.AccessibleName == "Excluded applications" && !String.IsNullOrWhiteSpace(appList.AccessibleDescription),
                            "The Apps section preserves the stored exclusion and native selection semantics at layout scale " + dpi);
                        var editors = Descendants(form).OfType<Button>().Where(button => (button.AccessibleName ?? "").StartsWith("Change ")).ToArray();
                        TabPage shortcutPage = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Shortcuts");
                        var shortcutEditors = Descendants(shortcutPage).OfType<Button>().Where(button => (button.AccessibleName ?? "").StartsWith("Change ")).ToArray();
                        var shortcutStatuses = Descendants(shortcutPage).OfType<Label>().Where(label => (label.AccessibleName ?? "").EndsWith("shortcut status")).ToArray();
                        check(editors.Length == 14 && shortcutEditors.Length == 14 && editors.All(button => button.TabStop) &&
                            !Descendants(settingsPage).OfType<Button>().Any(button => (button.AccessibleName ?? "").StartsWith("Change ")),
                            "All fourteen Undo, monitor, close, search, minimize, pin, directional-focus and tiling editors are discoverable in one Shortcuts section at layout scale " + dpi);
                        check(shortcutStatuses.Length == 14 && shortcutStatuses.Select(label => label.AccessibleName).Distinct().Count() == 14,
                            "Every consolidated shortcut has a distinct accessible status at layout scale " + dpi);
                        foreach (string action in new[] { "Minimize focused window", "Keep focused window on top" }) {
                            Button editor = shortcutEditors.Single(button => button.AccessibleName == "Change " + action.ToLowerInvariant() + " shortcut");
                            check(editor.Text == "Off" && !String.IsNullOrWhiteSpace(editor.AccessibleDescription) &&
                                shortcutStatuses.Any(label => label.AccessibleName == action + " shortcut status"),
                                "The new window action preserves Off and exposes its own editor, help and status at layout scale " + dpi + ": " + action);
                        }
                        foreach (Button editor in shortcutEditors) {
                            Label state = editor.Parent.Controls.OfType<Label>().Single(label => (label.AccessibleName ?? "").EndsWith("shortcut status"));
                            check(editor.Height >= editor.MinimumSize.Height && editor.Bottom <= state.Top && editor.Parent.ClientRectangle.Contains(editor.Bounds),
                                "Each compact shortcut binding clears its status text while retaining its native minimum height at layout scale " + dpi + ": " + editor.AccessibleName);
                        }
                        Button undoEditor = shortcutEditors.Single(button => button.AccessibleName == "Change Undo shortcut");
                        check(undoEditor.Text == GlobalShortcut.Format((Keys)controller.Settings.UndoMoveShortcut) &&
                            undoEditor.AccessibleDescription.IndexOf("minimize", StringComparison.OrdinalIgnoreCase) >= 0,
                            "Consolidating shortcuts displays the stored Undo choice instead of substituting a new default at layout scale " + dpi);
                        var footer = Descendants(form).OfType<Button>().Where(button => button.Text == "Undo" || button.Text == "Pause moves" || button.Text == "Resume moves" || button.Text == "Pause all" || button.Text == "Resume").ToArray();
                        check(footer.Length == 3 && footer.All(button => button.TabStop && form.ClientRectangle.Contains(BoundsIn(button, form))),
                            "Recovery and both pause actions remain fully reachable outside the scrolling pages at layout scale " + dpi);
                        check(footer.Single(button => button.Text == "Undo").AccessibleDescription.IndexOf("minimize", StringComparison.OrdinalIgnoreCase) >= 0,
                            "The shared Undo action explains shortcut minimize recovery at layout scale " + dpi);
                        check(Descendants(form).OfType<SettingsCard>().All(card => !card.TabStop && card.AccessibleRole == AccessibleRole.Grouping && !String.IsNullOrWhiteSpace(card.AccessibleName)) &&
                            Descendants(form).Count(control => control.AccessibleName == "Taskbar status" || control.AccessibleName == "Save status") == 2,
                            "Visual cards stay out of the keyboard sequence while save and taskbar status retain accessible names at layout scale " + dpi);
                        ShortViewport(form, tabs, dpi, check);
                        MapSelection(form, tabs, controller, dpi, check);
                        typeof(SettingsForm).GetMethod("OnSystemColorsChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(form, new object[] { EventArgs.Empty });
                        controller.Revision++; form.UpdateStatus(); Layout(form);
                        check(FocusAppearanceSettingsTests.SameJson(Serialize(controller.Settings), before) && controller.Revision == 1 && controller.History.Peek == null,
                            "Constructing, resizing, scrolling, switching sections, selecting displays and refreshing the app never saves or rewrites existing settings at layout scale " + dpi);
                        check(!form.Visible && !controller.DesktopRuntime && Ui.Accent == accentBefore && SameBytes(bytesBefore, SettingsBytes()),
                            "Hidden app polish checks leave the live profile file, palette and desktop untouched at layout scale " + dpi);
                    }
                } finally { Release(controller); }
            }
        }
        static void ShortViewport(SettingsForm form, TabControl tabs, int dpi, Action<bool, string> check)
        {
            Size previousSize = tabs.Size, previousMaximum = tabs.MaximumSize;
            DockStyle previousDock = tabs.Dock;
            try {
                int width = tabs.Width, height = (int)Math.Ceiling(380 * dpi / 96f);
                tabs.Dock = DockStyle.None; tabs.MaximumSize = new Size(width, height); tabs.Size = tabs.MaximumSize; Layout(form);
                foreach (TabPage page in tabs.TabPages.Cast<TabPage>().Where(page => page.Text != "Apps")) {
                    tabs.SelectedTab = page; Layout(form);
                    check(page.AutoScroll, "The shorter app viewport scrolls each settings page at layout scale " + dpi + ": " + page.Text);
                    foreach (Control action in ReachableActions(page)) {
                        page.ScrollControlIntoView(action); Layout(page);
                        check(page.ClientRectangle.Contains(BoundsIn(action, page)),
                            "The shorter app viewport can reveal every complete action at layout scale " + dpi + ": " + page.Text + "/" + (action.AccessibleName ?? action.Text));
                    }
                    if (page.Text == "Shortcuts")
                        foreach (Label status in Descendants(page).OfType<Label>().Where(label => (label.AccessibleName ?? "").EndsWith("shortcut status"))) {
                            page.ScrollControlIntoView(status); Layout(page);
                            Size text = TextRenderer.MeasureText(status.Text, status.Font, new Size(Math.Max(1, status.Width), Int32.MaxValue),
                                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            check(status.Height >= text.Height && page.ClientRectangle.Contains(BoundsIn(status, page)),
                                "The shorter consolidated Shortcuts viewport reveals complete status text at layout scale " + dpi + ": " + status.AccessibleName);
                        }
                    page.AutoScrollPosition = Point.Empty;
                }
            } finally {
                tabs.MaximumSize = previousMaximum; tabs.Size = previousSize; tabs.Dock = previousDock; Layout(form);
            }
        }
        static void MapSelection(SettingsForm form, TabControl tabs, Controller controller, int dpi, Action<bool, string> check)
        {
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Displays"); Layout(form);
            DisplayMap map = Descendants(form).OfType<DisplayMap>().Single();
            Button[] tiles = map.Controls.OfType<Button>().ToArray();
            check(tiles.Length == 4 && tiles.Select(tile => ((Display)tile.Tag).Device).SequenceEqual(controller.Displays.Select(display => display.Device)) &&
                tiles.All(tile => tile.TabStop && !String.IsNullOrWhiteSpace(tile.AccessibleName) && !String.IsNullOrWhiteSpace(tile.AccessibleDescription)),
                "The polished map keeps all Windows monitor identities and native accessible selection buttons at layout scale " + dpi);
            Button upper = tiles.Single(tile => ((Display)tile.Tag).Device == @"\\.\DISPLAY1");
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(upper, new object[] { EventArgs.Empty });
            var nickname = Descendants(form).OfType<TextBox>().Single(input => input.AccessibleName == "Nickname");
            var taskbar = Descendants(form).OfType<ToggleSwitch>().Single(input => input.AccessibleName == "Show taskbar");
            var ghost = Descendants(form).OfType<ToggleSwitch>().Single(input => input.AccessibleName == "Ghost Bar");
            check(map.Selected == controller.Displays[0] && nickname.Text == "Owned upper display" && !taskbar.Checked && taskbar.Enabled && ghost.Checked && ghost.Enabled,
                "Selecting an upper-monitor tile shows its retained nickname, hidden taskbar and enabled Ghost Bar without editing them at layout scale " + dpi);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        static void NativeTabSelection(Action<bool, string> check)
        {
            Controller controller = Fixture();
            string before = Serialize(controller.Settings);
            try {
                using (var form = new SettingsForm(controller)) {
                    TabControl tabs = Descendants(form).OfType<TabControl>().Single();
                    Realize(form); Layout(form);
                    var className = new StringBuilder(256); Native.GetClassName(tabs.Handle, className, className.Capacity);
                    check(className.ToString().IndexOf("SysTabControl32", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        SendMessage(tabs.Handle, 0x1304, IntPtr.Zero, IntPtr.Zero).ToInt32() == 5,
                        "Polished navigation still owns a real five-page Windows tab control");
                    // Ctrl+Tab depends on an active child/focus route. Invoking its
                    // protected handler on a never-shown form cannot represent
                    // that route. Verify native selection/notification agreement
                    // here; the live isolated app checks the real keyboard route.
                    MethodInfo inheritedCommand = typeof(ThemedTabControl).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
                    MethodInfo nativeCommand = typeof(TabControl).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
                    MethodInfo inheritedDialog = typeof(ThemedTabControl).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
                    MethodInfo nativeDialog = typeof(TabControl).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
                    check(inheritedCommand.DeclaringType == nativeCommand.DeclaringType && inheritedDialog.DeclaringType == nativeDialog.DeclaringType,
                        "Polished tab painting leaves the Windows command and dialog keyboard routes inherited");
                    int changes = 0; tabs.SelectedIndexChanged += delegate { changes++; };
                    foreach (int index in new[] { 4, 0, 3, 2, 1 }) {
                        int previous = changes; tabs.SelectedIndex = index;
                        check(tabs.SelectedTab == tabs.TabPages[index] &&
                            SendMessage(tabs.Handle, 0x130B, IntPtr.Zero, IntPtr.Zero).ToInt32() == index && changes == previous + 1,
                            "Each native section selection agrees with its page and raises one notification: " + tabs.TabPages[index].Text);
                    }
                    check(!form.Visible && FocusAppearanceSettingsTests.SameJson(Serialize(controller.Settings), before) && controller.Revision == 0 && controller.History.Peek == null,
                        "Native section selection does not activate a real window or change settings");
                }
            } finally { Release(controller); }
        }
        static void OwnedControlSemantics(Action<bool, string> check)
        {
            byte[] bytesBefore = SettingsBytes();
            using (Label subtitle = Ui.Label("Windows, displays & shortcuts"))
            using (var section = new SectionHeader("Focus & tile"))
                check(!subtitle.UseMnemonic && !section.UseMnemonic,
                    "Product labels show literal ampersands instead of hiding them as keyboard mnemonics");
            using (var host = new Form { Size = new Size(460, 180), Font = new Font("Segoe UI", 10) }) {
                var number = new ThemedNumericUpDown { Minimum = 8, Maximum = 96, Increment = 2, Value = 24,
                    Location = new Point(12, 12), Size = new Size(160, 32), AccessibleName = "Owned numeric setting" };
                var selector = new ThemedComboBox { Location = new Point(12, 52), Size = new Size(220, 30), AccessibleName = "Owned choice" };
                selector.Items.AddRange(new object[] { "One", "Two", "Three" }); selector.SelectedIndex = 1;
                var button = Ui.Button("Owned action", null, delegate { }); button.Location = new Point(12, 94);
                host.Controls.Add(number); host.Controls.Add(selector); host.Controls.Add(button);
                var handlesBefore = new[] { (Control)host }.Concat(Descendants(host)).ToDictionary(control => control, control => control.IsHandleCreated);
                Ui.Theme(host); Ui.Theme(host);
                check(handlesBefore.All(pair => pair.Key.IsHandleCreated == pair.Value),
                    "Applying app chrome repeatedly creates no additional HWNDs for hidden controls");
                Realize(host); Layout(host);
                check(number.Controls.OfType<TextBox>().Count() == 1 && number.Controls.Count == 2 &&
                    number.Controls.Cast<Control>().All(child => child.BackColor == Ui.Surface && child.ForeColor == Ui.Text),
                    "The themed number retains its native editable child and arrow child with matching field colors");
                number.UpButton(); check(number.Value == 26, "Themed numeric Up keeps the existing increment");
                number.DownButton(); check(number.Value == 24, "Themed numeric Down keeps the existing increment");
                TextBox edit = number.Controls.OfType<TextBox>().Single(); edit.Text = "42";
                typeof(NumericUpDown).GetMethod("ValidateEditText", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(number, null);
                check(number.Value == 42, "Themed numeric editing still parses typed numbers through native validation");
                number.Value = 96; number.UpButton(); check(number.Value == 96, "Themed numeric Up respects the saved field maximum");
                number.Value = 8; number.DownButton(); check(number.Value == 8, "Themed numeric Down respects the saved field minimum");
                selector.SelectedIndex = 2;
                check(selector.SelectedItem.ToString() == "Three" && selector.DropDownStyle == ComboBoxStyle.DropDownList && selector.TabStop &&
                    button is AppButton && button.TabStop && button is IButtonControl,
                    "Themed choices and buttons keep native selection, keyboard and dialog semantics");
                selector.Items.Clear(); selector.Items.AddRange(new object[] { "Inset edge", "Energy flow", "Forest Green" });
                Font previousFont = selector.Font;
                foreach (int layoutDpi in new[] { 96, 192 })
                using (var fieldFont = new Font("Segoe UI", 10 * layoutDpi / 96f)) {
                    selector.Font = fieldFont; selector.Size = new Size(220 * layoutDpi / 96, 32 * layoutDpi / 96);
                    foreach (int index in new[] { 0, 1, 2 }) {
                    selector.SelectedIndex = index;
                    string selectedText = selector.Text;
                    using (var pixels = new Bitmap(selector.Width, selector.Height)) {
                        selector.DrawToBitmap(pixels, new Rectangle(Point.Empty, selector.Size));
                        int textPixels = 0, arrowWidth = (int)Math.Ceiling(22 * selector.DeviceDpi / 96f);
                        for (int y = 3; y < pixels.Height - 3; y++)
                        for (int x = 3; x < pixels.Width - arrowWidth - 3; x++)
                            if (pixels.GetPixel(x, y).ToArgb() == Ui.Text.ToArgb()) textPixels++;
                        check(textPixels >= 10 && pixels.GetPixel(selector.Width - 12, 4).ToArgb() == Ui.Surface.ToArgb() &&
                            pixels.GetPixel(0, 0).ToArgb() == Ui.Line.ToArgb(),
                            "Native closed-choice printing shows readable selected text and themed arrow/border without a blank field at font scale " + layoutDpi + ": " + selectedText);
                    }
                    check(selector.SelectedIndex == index && selector.Text == selectedText,
                        "Painting the selected choice leaves its native text and value unchanged at font scale " + layoutDpi + ": " + selectedText);
                    }
                    selector.Font = previousFont;
                }
                if (SystemInformation.HighContrast)
                    check(Ui.Background == SystemColors.Window && Ui.Surface == SystemColors.Window && Ui.Text == SystemColors.WindowText,
                        "An already-enabled Windows high-contrast theme keeps system surfaces and readable text");
                check(!host.Visible && SameBytes(bytesBefore, SettingsBytes()), "Owned themed-control checks never show a window or write profile settings");
            }
        }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr WindowLong(IntPtr window, int index);
        static void EmptyAppsState(Action<bool, string> check)
        {
            byte[] bytesBefore = SettingsBytes();
            foreach (bool empty in new[] { false, true }) {
                Controller controller = Fixture();
                if (empty) controller.Settings.ExcludedApps.Clear();
                string before = Serialize(controller.Settings);
                try {
                    using (var form = new SettingsForm(controller)) {
                        TabControl tabs = Descendants(form).OfType<TabControl>().Single();
                        TabPage apps = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Apps");
                        // Keep Apps unselected and the entire form hidden. Its
                        // empty-state z-order must already be correct when opened.
                        Realize(form); Layout(form);
                        Control message = Descendants(apps).Single(control => control.AccessibleName == "No app exclusions");
                        ListView list = Descendants(apps).OfType<ListView>().Single();
                        bool locallyVisible = (WindowLong(message.Handle, -16).ToInt64() & 0x10000000L) != 0;
                        check(list.Items.Count == (empty ? 0 : 1) && locallyVisible == empty &&
                            (!empty || message.Parent.Controls.GetChildIndex(message) < message.Parent.Controls.GetChildIndex(list)),
                            "The Apps empty-state is shown in front only when there are no exclusions, even before its hidden page opens: " + empty);
                        check(message.Text.IndexOf("Add an exclusion", StringComparison.Ordinal) >= 0 && !message.TabStop && !message.AutoSize,
                            "The Apps empty-state explains how to add an exception without taking keyboard focus");
                        check(!form.Visible && FocusAppearanceSettingsTests.SameJson(Serialize(controller.Settings), before) && controller.Revision == 0 && controller.History.Peek == null && SameBytes(bytesBefore, SettingsBytes()),
                            "Rendering either Apps empty-state does not show a window or rewrite the exclusion profile");
                    }
                } finally { Release(controller); }
            }
        }
        static void ConsolidatedShortcutErrors(Action<bool, string> check)
        {
            const string error = "Ctrl + Alt + Shift + Left Arrow could not be registered because another app already uses it (Windows error 1409).";
            byte[] bytesBefore = SettingsBytes();
            foreach (int dpi in new[] { 96, 192 }) {
                Controller controller = Fixture();
                string before = Serialize(controller.Settings);
                controller.ShortcutError = error;
                for (int i = 0; i < controller.MonitorShortcutErrors.Length; i++) controller.MonitorShortcutErrors[i] = error;
                for (int i = 0; i < controller.ActionShortcutErrors.Length; i++) controller.ActionShortcutErrors[i] = error;
                for (int i = 0; i < controller.NavigationShortcutErrors.Length; i++) controller.NavigationShortcutErrors[i] = error;
                try {
                    using (var form = new SettingsForm(controller))
                    using (var font = new Font("Segoe UI", 10 * dpi / 96f)) {
                        TabControl tabs = Descendants(form).OfType<TabControl>().Single();
                        TabPage page = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Shortcuts");
                        tabs.SelectedTab = page; form.Size = form.MinimumSize; Realize(form); Layout(form);
                        Size previousMinimum = form.MinimumSize, nativeChrome = form.Size - form.ClientSize;
                        float scale = dpi / (float)form.DeviceDpi; form.AutoScaleMode = AutoScaleMode.None;
                        if (scale != 1) form.Scale(new SizeF(scale, scale));
                        form.Font = font; form.Size = form.MinimumSize; Layout(form);
                        FocusAppearanceSettingsTests.MinimumFixtureBounds(form, tabs, dpi, check, previousMinimum, nativeChrome, scale);
                        Control content = page.Controls[0];
                        Label[] statuses = Descendants(page).OfType<Label>().Where(label => (label.AccessibleName ?? "").EndsWith("shortcut status")).ToArray();
                        Button[] editors = Descendants(page).OfType<Button>().Where(button => (button.AccessibleName ?? "").StartsWith("Change ")).ToArray();
                        check(statuses.Length == 14 && editors.Length == 14 && statuses.All(status => status.Text == error),
                            "One Shortcuts page exposes all fourteen actions and their real registration errors at layout scale " + dpi);
                        int viewportWidth = tabs.Width;
                        tabs.Dock = DockStyle.None; tabs.MaximumSize = new Size(viewportWidth, (int)Math.Ceiling(380 * dpi / 96f));
                        tabs.Size = tabs.MaximumSize; Layout(form);
                        check(page.AutoScroll && page.VerticalScroll.Visible && content.Height > page.ClientSize.Height,
                            "Consolidated long shortcut errors scroll instead of squeezing or dropping rows at layout scale " + dpi);
                        foreach (Label status in statuses) {
                            Size fullText = TextRenderer.MeasureText(status.Text, status.Font, new Size(Math.Max(1, status.Width), Int32.MaxValue),
                                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            check(!status.AutoEllipsis && status.Height >= fullText.Height && content.ClientRectangle.Contains(BoundsIn(status, content)),
                                "Each compact shortcut row grows for its complete registration error at layout scale " + dpi + ": " + status.AccessibleName);
                            page.ScrollControlIntoView(status); Layout(page);
                            check(page.ClientRectangle.Contains(BoundsIn(status, page)),
                                "Each full shortcut error can be scrolled into the shorter viewport at layout scale " + dpi + ": " + status.AccessibleName);
                        }
                        foreach (Button editor in editors) {
                            page.ScrollControlIntoView(editor); Layout(page);
                            check(page.ClientRectangle.Contains(BoundsIn(editor, page)),
                                "All fourteen shortcut editors remain reachable beside long errors at layout scale " + dpi + ": " + editor.AccessibleName);
                        }
                        // Change all messages back to their normal state. Rows
                        // should fit that text without retaining huge error gaps.
                        controller.ShortcutError = null;
                        for (int i = 0; i < controller.MonitorShortcutErrors.Length; i++) controller.MonitorShortcutErrors[i] = null;
                        for (int i = 0; i < controller.ActionShortcutErrors.Length; i++) controller.ActionShortcutErrors[i] = null;
                        for (int i = 0; i < controller.NavigationShortcutErrors.Length; i++) controller.NavigationShortcutErrors[i] = null;
                        int errorHeight = content.Height; form.UpdateStatus(); Layout(form);
                        check(statuses.All(status => status.Text.StartsWith("Shortcut off", StringComparison.Ordinal)) && content.Height < errorHeight,
                            "Clearing all errors restores compact normal shortcut rows at layout scale " + dpi);
                        check(FocusAppearanceSettingsTests.SameJson(Serialize(controller.Settings), before) && controller.Revision == 0 && controller.History.Peek == null &&
                            SameBytes(bytesBefore, SettingsBytes()) && !form.Visible,
                            "Consolidated error/status updates preserve Undo and every stored shortcut without saves or activation at layout scale " + dpi);
                    }
                } finally { Release(controller); }
            }
        }
    }
}
