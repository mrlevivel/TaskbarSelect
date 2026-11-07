using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class FocusAppearanceSettingsTests
    {
        internal static void Run(Action<bool, string> check)
        {
            JsonComparisonChecks(check);
            DefaultsAndValidation(check);
            FreshProfileAndExistingPreferences(check);
            Palettes(check);
            AccentFamilies(check);
            AccentApplication(check);
            TealLegacyProfile(check);
            PersistenceAndHistory(check);
            FinishPersistenceAndHistory(check);
            HiddenSettingsUi(check);
            HiddenSettingsUi(check, new Size(1022, 718));
            ChoiceSelectionAndAutosave(check);
            ControllerFinishPolicy(check);
            SettingsMoveLoop(check);
        }

        static void DefaultsAndValidation(Action<bool, string> check)
        {
            var defaults = new Settings();
            check(defaults.FocusIndicatorStyle == 0 && defaults.FocusIndicatorFinish == 0 && !defaults.FocusIndicatorGlow && defaults.FocusIndicatorLighting == -1 &&
                FocusAppearance.ResolveLighting(defaults.FocusIndicatorLighting, defaults.FocusIndicatorGlow) == FocusLightingMode.None && defaults.AccentPreset == "Original",
                "Focus indicator and frame lighting stay opt-in and Original keeps the released app palette by default");
            var legacy = new JavaScriptSerializer().Deserialize<Settings>("{\"WindowShelfHoverDelayMs\":700,\"UndoMoveShortcut\":0}");
            check(legacy.FocusIndicatorStyle == 0 && legacy.FocusIndicatorFinish == 0 && !legacy.FocusIndicatorGlow && legacy.FocusIndicatorLighting == -1 && legacy.AccentPreset == "Original" && legacy.WindowShelfHoverDelayMs == 700 && legacy.UndoMoveShortcut == 0,
                "Legacy JSON adds opt-in appearance defaults without changing released preferences");
            foreach (string value in new[] { "Original", "original", " Original ", "Teal", "teal", " Teal " })
                check(new Settings { AccentPreset = value }.AccentPreset == "Original" && FocusAppearance.NormalizeAccent(value) == "Original" &&
                    FocusAppearance.AccentStorageName(value) == "Original" && FocusAppearance.AccentLabel(value) == "Teal" && FocusAppearance.AccentFamily(value) == "Greens",
                    "Both old Original and displayed Teal names resolve to the same stable stored default: " + value);
            foreach (int value in new[] { Int32.MinValue, -100, -1, 0, 1, 2, 3, 100, Int32.MaxValue }) {
                var settings = new Settings { FocusIndicatorFinish = value };
                int expected = value == 1 ? 1 : 0;
                check(settings.FocusIndicatorFinish == expected && (int)FocusAppearance.NormalizeFinish(value) == expected &&
                    (int)FocusAppearance.NormalizeFinish(settings.FocusIndicatorFinish) == expected &&
                    FocusAppearance.FinishLabel(value) == (expected == 1 ? "Accent outline" : "Muted edge"),
                    "Frame finish accepts only Muted edge or Accent outline and safely normalizes every other value: " + value);
                var persisted = new JavaScriptSerializer().Deserialize<Settings>("{\"FocusIndicatorFinish\":" + value + "}");
                check(persisted.FocusIndicatorFinish == expected && persisted.FocusIndicatorStyle == 0 &&
                    persisted.FocusIndicatorLighting == -1 && !persisted.FocusIndicatorGlow,
                    "A persisted finish cannot auto-enable the focus frame or change legacy lighting: " + value);
            }
            foreach (int value in new[] { Int32.MinValue, -100, -1, 0, 1, 2, 3, 100, Int32.MaxValue }) {
                var settings = new Settings { FocusIndicatorStyle = value };
                int expected = value == 1 || value == 2 ? value : 0;
                check(settings.FocusIndicatorStyle == expected && (int)FocusAppearance.NormalizeStyle(value) == expected,
                    "Only Off, Muted frame and Inset edge are accepted: " + value);
                check((int)FocusAppearance.NormalizeStyle(settings.FocusIndicatorStyle) == settings.FocusIndicatorStyle,
                    "Style normalization is idempotent: " + value);
                check(FocusAppearance.FrameStyle(value) == (expected == 0 ? FocusIndicatorMode.Off : FocusIndicatorMode.InsetEdge) &&
                    FocusAppearance.FrameLabel(value) == (expected == 0 ? "Off" : "Focus frame") && settings.FocusIndicatorStyle == expected,
                    "The single displayed frame maps both legacy enabled styles to Inset edge without rewriting the stored value: " + value);
            }
            foreach (string value in new[] { null, "", " ", "Unknown", "#ff0000", "Original\u0000", "ForestGreen" })
                check(new Settings { AccentPreset = value }.AccentPreset == "Original",
                    "Invalid accent names safely use Original: " + (value ?? "null"));
            foreach (string preset in FocusAppearance.Presets) {
                string input = "  " + preset.ToLowerInvariant() + "  ";
                check(new Settings { AccentPreset = input }.AccentPreset == preset && FocusAppearance.NormalizeAccent(preset) == preset,
                    "Accent names canonicalize case and surrounding whitespace: " + preset);
            }
            string[] names = FocusAppearance.Presets; names[0] = "changed";
            string[] accentLabels = FocusAppearance.AccentLabels; accentLabels[0] = "changed";
            string[] labels = FocusAppearance.Labels; labels[0] = "changed";
            string[] lightingLabels = FocusAppearance.LightingLabels; lightingLabels[0] = "changed";
            string[] finishLabels = FocusAppearance.FinishLabels; finishLabels[0] = "changed";
            check(FocusAppearance.Presets[0] == "Original" && FocusAppearance.AccentLabels[0] == "Teal" &&
                FocusAppearance.AccentLabels.SequenceEqual(FocusAppearance.Presets.Select(FocusAppearance.AccentLabel)) &&
                FocusAppearance.Labels.SequenceEqual(new[] { "Off", "Muted frame", "Inset edge" }) &&
                FocusAppearance.LightingLabels.SequenceEqual(new[] { "None", "Soft glow", "Illuminated", "Energy flow" }) &&
                FocusAppearance.FinishLabels.SequenceEqual(new[] { "Muted edge", "Accent outline" }),
                "Public-facing choices cannot mutate the helper's canonical palette or labels");
            Settings malformed = new JavaScriptSerializer().Deserialize<Settings>("{\"FocusIndicatorStyle\":999,\"AccentPreset\":null}");
            check(malformed.FocusIndicatorStyle == 0 && !malformed.FocusIndicatorGlow && malformed.FocusIndicatorLighting == -1 && malformed.AccentPreset == "Original",
                "Malformed persisted appearance values pass through the same safe setters");
            foreach (bool glow in new[] { false, true }) {
                var explicitGlow = new JavaScriptSerializer().Deserialize<Settings>("{\"FocusIndicatorGlow\":" + (glow ? "true" : "false") + "}");
                check(explicitGlow.FocusIndicatorGlow == glow && explicitGlow.FocusIndicatorLighting == -1 && explicitGlow.FocusIndicatorStyle == 0 && explicitGlow.AccentPreset == "Original" &&
                    FocusAppearance.ResolveLighting(explicitGlow.FocusIndicatorLighting, glow) == (glow ? FocusLightingMode.SubtleGlow : FocusLightingMode.None),
                    "Legacy glow JSON maps to the same visual without enabling the focus indicator or upgrading the effect: " + glow);
                foreach (int value in new[] { Int32.MinValue, -100, -1, 0, 1, 2, 3, 4, 100, Int32.MaxValue }) {
                    var lighting = new JavaScriptSerializer().Deserialize<Settings>("{\"FocusIndicatorLighting\":" + value + ",\"FocusIndicatorGlow\":" + (glow ? "true" : "false") + "}");
                    int expected = value >= 0 && value <= 3 ? value : -1;
                    FocusLightingMode resolved = expected >= 0 ? (FocusLightingMode)expected : glow ? FocusLightingMode.SubtleGlow : FocusLightingMode.None;
                    check(lighting.FocusIndicatorLighting == expected && FocusAppearance.NormalizeLighting(value) == expected &&
                        FocusAppearance.NormalizeLighting(lighting.FocusIndicatorLighting) == expected,
                        "Persisted lighting accepts four choices or the migration sentinel and normalizes invalid values safely: " + value + "/" + glow);
                    check(FocusAppearance.ResolveLighting(lighting.FocusIndicatorLighting, lighting.FocusIndicatorGlow) == resolved &&
                        FocusAppearance.ResolveLighting(value, glow) == resolved,
                        "Explicit lighting overrides the legacy glow flag; invalid or missing values preserve its original effect: " + value + "/" + glow);
                }
            }
        }

        // File loading, rather than a missing JSON member, identifies a new
        // installation. Existing profiles must not silently gain a focus frame.
        internal static void FreshProfileAndExistingPreferences(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.FreshDefaults." + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(directory);
            try {
                Settings fresh = Program.Load(path);
                check(fresh.WindowShelfActivationHeight == 6 && fresh.FocusIndicatorStyle == 2 && fresh.FocusIndicatorFinish == 0 &&
                    fresh.FocusIndicatorLighting == 0 && !fresh.FocusIndicatorGlow && fresh.AccentPreset == "Forest Green" &&
                    !fresh.MoveWindowsToClickedTaskbar && fresh.HiddenIdentities.Count == 0 && fresh.WindowShelfIdentities.Count == 0 &&
                    !fresh.PersistentAdministratorSupport && !File.Exists(path),
                    "A genuinely new profile starts at six physical pixels with an unlit Muted edge and Forest Green, without opting into movement, hidden taskbars or administrator support");
                Program.Save(fresh, path); Settings reopened = Program.Load(path);
                check(SameJson(Serialize(fresh), Serialize(reopened)), "Fresh defaults survive their first save and ordinary profile reload");
                File.WriteAllText(path, "{\"HiddenIdentities\":[],\"WindowShelfHoverDelayMs\":700,\"UndoMoveShortcut\":0}");
                byte[] legacyBytes = File.ReadAllBytes(path); Settings legacy = Program.Load(path);
                check(legacy.WindowShelfActivationHeight == 24 && legacy.FocusIndicatorStyle == 0 && legacy.FocusIndicatorFinish == 0 &&
                    legacy.FocusIndicatorLighting == -1 && !legacy.FocusIndicatorGlow && legacy.AccentPreset == "Original" &&
                    legacy.WindowShelfHoverDelayMs == 700 && legacy.UndoMoveShortcut == 0 && legacyBytes.SequenceEqual(File.ReadAllBytes(path)),
                    "Loading an existing profile with omitted fields retains the released appearance and 24-pixel fallback without rewriting its file");
                foreach (int height in new[] { 1, 2, 3, 4, 5, 6, 24, 96 }) {
                    var previous = new Settings { WindowShelfActivationHeight = height, FocusIndicatorStyle = 0,
                        FocusIndicatorFinish = 1, FocusIndicatorGlow = true, FocusIndicatorLighting = -1, AccentPreset = "Teal" };
                    previous.HiddenIdentities.Add("neutral-hidden-display"); previous.WindowShelfIdentities.Add("neutral-ghost-display");
                    previous.MonitorAliases.Add("neutral-hidden-display", "Neutral display");
                    Program.Save(previous, path); byte[] savedBytes = File.ReadAllBytes(path); Settings saved = Program.Load(path);
                    check(SameJson(Serialize(previous), Serialize(saved)) && saved.WindowShelfActivationHeight == height &&
                        saved.FocusIndicatorStyle == 0 && saved.AccentPreset == "Original" && saved.FocusIndicatorLighting == -1 &&
                        saved.FocusIndicatorGlow && savedBytes.SequenceEqual(File.ReadAllBytes(path)),
                        "Explicit saved appearance, monitor choices and every supported small height survive load unchanged: " + height);
                    Settings copy = saved.Copy(); var history = new SettingsHistory(); history.Push(saved, null, "Neutral defaults check");
                    saved.WindowShelfActivationHeight = height == 1 ? 6 : 1; saved.FocusIndicatorStyle = 2; saved.AccentPreset = "Blaze Orange";
                    check(copy.WindowShelfActivationHeight == height && copy.FocusIndicatorStyle == 0 && copy.AccentPreset == "Original" &&
                        history.Peek.Settings.WindowShelfActivationHeight == height && history.Peek.Settings.FocusIndicatorStyle == 0 &&
                        history.Peek.Settings.AccentPreset == "Original", "New defaults cannot overwrite a saved copy or undo snapshot: " + height);
                }
            } finally {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                Directory.Delete(directory);
            }
        }

        static int Chroma(Color color) { return Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)); }
        static void Palettes(Action<bool, string> check)
        {
            check(FocusAppearance.Accent("Original", false) == Color.FromArgb(0, 115, 89) &&
                FocusAppearance.Accent("Original", true) == Color.FromArgb(92, 221, 183),
                "Original exactly preserves the current light and dark app accent values");
            string[] legacyNames = { "Original", "Coral", "Mint", "Lavender", "Sky", "Peach", "Blaze Orange", "Forest Green" };
            string[] expected = legacyNames.Concat(new[] { "Rose", "Amber", "Olive", "Slate", "Sand", "Ruby", "Cobalt", "Violet", "Lime" }).ToArray();
            check(FocusAppearance.Presets.SequenceEqual(expected), "Muted and vivid accent names append to the unchanged eight canonical names and indices");
            // Independent snapshots of the existing eight palettes, including
            // the actual blended frame/inset results before family expansion.
            // Columns: light/dark accent, light/dark frame, light/dark inset.
            int[,] legacyRgb = {
                { 0x007359, 0x5CDDB7, 0xA2AEAE, 0x72878A, 0x94A3A0, 0x7D9797 },
                { 0xC36355, 0xF19080, 0xB7ADAC, 0x8D7E7E, 0xB1A19E, 0xA38A88 },
                { 0x3B977A, 0xA1E5BC, 0xA5B2AF, 0x7B8E89, 0x99A8A1, 0x8AA097 },
                { 0x8B6FB2, 0xC2A8E8, 0xAEA9BD, 0x847E96, 0xA49CB5, 0x968BA8 },
                { 0x3A8DBA, 0x8BCDF0, 0xA4B1BC, 0x778995, 0x97A7B3, 0x849AA7 },
                { 0xC47D50, 0xF0B58A, 0xB8AFAA, 0x8D857F, 0xB2A59A, 0xA39589 },
                { 0xBD681F, 0xF58B38, 0xB8AEA7, 0x8C7F74, 0xB2A397, 0xA18C79 },
                { 0x3F8656, 0x78BD86, 0xA8B2AD, 0x7B8983, 0x9CA89F, 0x8A9A8E }
            };
            for (int row = 0; row < legacyNames.Length; row++)
                foreach (bool dark in new[] { false, true }) {
                    int column = dark ? 1 : 0;
                    check(FocusAppearance.Accent(legacyNames[row], dark).ToArgb() == Opaque(legacyRgb[row, column]) &&
                        FocusAppearance.Frame(legacyNames[row], dark).ToArgb() == Opaque(legacyRgb[row, 2 + column]) &&
                        FocusAppearance.Inset(legacyNames[row], dark).ToArgb() == Opaque(legacyRgb[row, 4 + column]),
                        "The old accent, frame and inset ARGB values remain exactly frozen: " + legacyNames[row] + "/" + dark);
                }
            foreach (bool dark in new[] { false, true }) {
                check(expected.Select(name => FocusAppearance.Accent(name, dark).ToArgb()).Distinct().Count() == expected.Length &&
                    expected.Select(name => FocusAppearance.Frame(name, dark).ToArgb()).Distinct().Count() == expected.Length,
                    "Every preset has its own app accent and restrained frame tint in " + (dark ? "dark" : "light") + " appearance");
                foreach (string preset in expected) {
                    Color accent = FocusAppearance.Accent(preset, dark), frame = FocusAppearance.Frame(preset, dark), inset = FocusAppearance.Inset(preset, dark);
                    check(accent.A == 255 && frame.A == 255 && inset.A == 255 && FocusAppearance.Neutral(dark).A == 255,
                        "Appearance colors are opaque with no glow or transparency dependency: " + preset + "/" + dark);
                    check(Chroma(frame) < Chroma(accent) && Chroma(inset) < Chroma(accent) && frame != inset && frame != FocusAppearance.Neutral(dark),
                        "Muted frame and inset tints stay quieter than app controls and differ from a neutral edge: " + preset + "/" + dark);
                    check(FocusAppearance.Accent("invalid", dark) == FocusAppearance.Accent("Original", dark) &&
                        FocusAppearance.Frame(null, dark) == FocusAppearance.Frame("Original", dark) &&
                        FocusAppearance.Inset("", dark) == FocusAppearance.Inset("Original", dark),
                        "Every palette accessor shares the safe Original fallback: " + dark);
                }
            }
        }
        static int Opaque(int rgb) { return unchecked((int)(0xFF000000u | (uint)rgb)); }
        static void AccentFamilies(Action<bool, string> check)
        {
            string[] labels = { "Default", "Warm", "Greens", "Cool", "Neutrals" };
            string[][] expected = {
                new[] { "Forest Green" }, new[] { "Ruby", "Rose", "Coral", "Peach", "Blaze Orange", "Amber" },
                new[] { "Original", "Mint", "Lime", "Olive" }, new[] { "Sky", "Cobalt", "Lavender", "Violet" }, new[] { "Sand", "Slate" }
            };
            check(FocusAppearance.AccentFamilyLabels.SequenceEqual(labels), "Five accent families retain their descriptive reading order");
            var grouped = new List<string>();
            for (int index = 0; index < labels.Length; index++) {
                string[] names = FocusAppearance.AccentFamilyPresets(labels[index]);
                check(names.SequenceEqual(expected[index]), "Each family has its intended named color order: " + labels[index]);
                grouped.AddRange(names);
                foreach (string name in names)
                    check(FocusAppearance.AccentFamily(name) == labels[index] &&
                        FocusAppearance.AccentFamily("  " + name.ToLowerInvariant() + "  ") == labels[index],
                        "Saved preset names and normalized input map to the same color family: " + name);
                names[0] = "changed";
                check(FocusAppearance.AccentFamilyPresets(labels[index]).SequenceEqual(expected[index]),
                    "Returned family choices cannot mutate the canonical metadata: " + labels[index]);
            }
            check(grouped.Count == FocusAppearance.Presets.Length && grouped.Distinct(StringComparer.Ordinal).Count() == grouped.Count &&
                grouped.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(FocusAppearance.Presets.OrderBy(name => name, StringComparer.Ordinal)),
                "Every named accent appears exactly once across all color families");
            string[] returnedLabels = FocusAppearance.AccentFamilyLabels; returnedLabels[0] = "changed";
            check(FocusAppearance.AccentFamilyLabels.SequenceEqual(labels) && FocusAppearance.AccentFamily(null) == "Greens" &&
                FocusAppearance.AccentFamily("unknown") == "Greens",
                "Family labels are defensive copies and invalid saved color names share the Original fallback");
            foreach (string unknown in new[] { null, "", "unknown" })
                check(FocusAppearance.AccentFamilyPresets(unknown).Length == 0 &&
                    !Object.ReferenceEquals(FocusAppearance.AccentFamilyPresets(unknown), FocusAppearance.AccentFamilyPresets(unknown)),
                    "Unknown color families return independent empty metadata arrays: " + (unknown ?? "null"));
        }
        static double Luminance(Color color)
        {
            Func<int, double> linear = delegate(int value) { double channel = value / 255.0; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); };
            return .2126 * linear(color.R) + .7152 * linear(color.G) + .0722 * linear(color.B);
        }
        static double Contrast(Color first, Color second)
        {
            double a = Luminance(first), b = Luminance(second);
            return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        }
        static void AccentApplication(Action<bool, string> check)
        {
            FieldInfo selected = typeof(Ui).GetField("accentPreset", BindingFlags.Static | BindingFlags.NonPublic);
            string originalPreset = (string)selected.GetValue(null); Color originalAccent = Ui.Accent;
            Color originalText = Ui.Text, originalMuted = Ui.Muted;
            try {
                foreach (string preset in FocusAppearance.Presets) {
                    Ui.ApplyAccent(preset);
                    check(Ui.Accent == (SystemInformation.HighContrast ? SystemColors.Highlight : FocusAppearance.Accent(preset, Ui.Dark)) &&
                        Ui.Text == originalText && Ui.Muted == originalMuted && !Ui.ApplyAccent(preset),
                        "Each accent updates the owned process palette without recoloring readable labels or overriding Windows high contrast: " + preset);
                    foreach (bool dark in new[] { false, true }) {
                        Color accent = FocusAppearance.Accent(preset, dark);
                        Color[] surfaces = dark ? new[] { Color.FromArgb(25, 27, 31), Color.FromArgb(31, 34, 39), Color.FromArgb(35, 39, 45) } :
                            new[] { Color.FromArgb(246, 248, 250), Color.White, Color.FromArgb(248, 250, 252) };
                        check(surfaces.All(surface => Contrast(accent, surface) >= 3),
                            "Selected accent outlines have at least three-to-one contrast on app, card and field surfaces: " + preset + "/" + dark);
                    }
                }
            } finally { Ui.ApplyAccent(originalPreset); }
            check((string)selected.GetValue(null) == originalPreset && Ui.Accent == originalAccent && Ui.Text == originalText && Ui.Muted == originalMuted,
                "Owned palette application checks restore the exact previous preset and readable global colors");
        }
        static void TealLegacyProfile(Action<bool, string> check)
        {
            bool originalTest = AppProfile.IsTest; string originalDirectory = AppProfile.Directory;
            Color originalAccent = Ui.Accent;
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.TealLegacy." + Guid.NewGuid().ToString("N"));
            Controller controller = null;
            try {
                AppProfile.Initialize(new[] { "--test-profile", directory });
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Program.ConfigPath, System.Text.Encoding.UTF8.GetBytes(
                    "{\"HiddenIdentities\":[],\"AccentPreset\":\"Original\",\"FocusIndicatorStyle\":1,\"FocusIndicatorGlow\":true}"));
                byte[] before = File.ReadAllBytes(Program.ConfigPath);
                controller = new Controller(false) { Settings = Program.Load(), Enabled = false, RefreshDisplayIdentity = false };
                using (var form = new SettingsForm(controller)) {
                    Realize(form); Layout(form);
                    TabControl tabs = Descendants(form).OfType<TabControl>().Single();
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Appearance"); Layout(form);
                    AppearancePage appearance = Descendants(form).OfType<AppearancePage>().Single();
                    AppearanceChoiceCard teal = appearance.AccentChoices.Cards[0];
                    Label caption = Descendants(appearance).OfType<Label>().Single(label => label.AccessibleName == "Selected appearance");
                    check(appearance.AccentChoices.Items[0] == "Original" && appearance.AccentChoices.SelectedItem == "Original" &&
                        teal.PresetName == "Original" && teal.Text == "Teal" && teal.AccessibleName == "Teal" && teal.Checked &&
                        appearance.CombinedPreview.AccentPreset == "Original" &&
                        appearance.CombinedPreview.AccessibleDescription.IndexOf("Teal", StringComparison.Ordinal) >= 0 &&
                        appearance.CombinedPreview.AccessibleDescription.IndexOf("Original", StringComparison.Ordinal) < 0 &&
                        caption.Text.IndexOf("Teal", StringComparison.Ordinal) >= 0 && caption.Text.IndexOf("Original", StringComparison.Ordinal) < 0,
                        "A legacy Original profile displays the same default color as Teal in its card, selected preview and accessibility text while retaining its stored identifier");
                    check(controller.Settings.AccentPreset == "Original" && controller.Settings.FocusIndicatorStyle == 1 &&
                        controller.Settings.FocusIndicatorFinish == 0 && controller.Settings.FocusIndicatorLighting == -1 && controller.Settings.FocusIndicatorGlow &&
                        controller.Revision == 0 && controller.History.Peek == null && before.SequenceEqual(File.ReadAllBytes(Program.ConfigPath)) &&
                        !form.Visible && !controller.DesktopRuntime && Ui.Accent == originalAccent,
                        "Opening the renamed default never rewrites legacy profile bytes, style/lighting/finish, history or the live palette");
                }
            } finally {
                if (controller != null) {
                    controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                    foreach (GlobalShortcut binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
                }
                string path = Path.Combine(directory, "settings.json");
                AppProfile.Initialize(originalTest ? new[] { "--test-profile", originalDirectory } : new string[0]);
                if (File.Exists(path)) File.Delete(path);
                if (Directory.Exists(directory)) Directory.Delete(directory);
            }
            check(AppProfile.IsTest == originalTest && AppProfile.Directory == originalDirectory && Ui.Accent == originalAccent,
                "Renamed-default profile checks release owned components and restore the original profile selection");
        }

        static void PersistenceAndHistory(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.FocusAppearance." + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(directory);
            try {
                foreach (string preset in FocusAppearance.Presets)
                foreach (int mode in new[] { 0, 1, 2 })
                foreach (int finish in new[] { 0, 1 })
                foreach (int lighting in new[] { -1, 0, 1, 2, 3 })
                foreach (bool glow in new[] { false, true }) {
                    var settings = new Settings {
                        AccentPreset = preset, FocusIndicatorStyle = mode, FocusIndicatorFinish = finish, FocusIndicatorGlow = glow, FocusIndicatorLighting = lighting, WindowShelfActivationWidth = 640,
                        WindowShelfActivationHeight = 42, WindowShelfHoverDelayMs = 700, AllowMovingFullscreenApps = true,
                        MoveWindowsToClickedTaskbar = true, ScaleWindowSizeToMonitor = false,
                        UndoMoveShortcut = 0, MonitorMoveShortcuts = new int[4], FocusWindowShortcuts = new int[4], TileWindowShortcut = 0,
                        CloseWindowShortcut = (int)(Keys.Control | Keys.Alt | Keys.F19), SearchAppsShortcut = (int)(Keys.Control | Keys.Alt | Keys.F20)
                    };
                    settings.HiddenIdentities.Add("owned-display"); settings.WindowShelfIdentities.Add("owned-display");
                    settings.MonitorAliases.Add("owned-display", "Owned neutral display");
                    settings.ExcludedApps.Add(new AppRule { Key = "exe:owned-fixture", Name = "Owned fixture" });
                    Settings copy = settings.Copy();
                    check(copy.AccentPreset == preset && copy.FocusIndicatorStyle == mode && copy.FocusIndicatorFinish == finish && copy.FocusIndicatorGlow == glow && copy.FocusIndicatorLighting == lighting &&
                        FocusAppearance.ResolveLighting(copy.FocusIndicatorLighting, copy.FocusIndicatorGlow) == FocusAppearance.ResolveLighting(lighting, glow) &&
                        copy.CloseWindowShortcut == settings.CloseWindowShortcut && copy.SearchAppsShortcut == settings.SearchAppsShortcut,
                        "Settings.Copy retains explicit and legacy lighting choices: " + preset + "/" + mode + "/lighting " + lighting + "/glow " + glow);
                    Program.Save(settings, path); Settings loaded = Program.Load(path);
                    check(loaded.AccentPreset == preset && loaded.FocusIndicatorStyle == mode && loaded.FocusIndicatorFinish == finish && loaded.FocusIndicatorGlow == glow && loaded.FocusIndicatorLighting == lighting &&
                        FocusAppearance.ResolveLighting(loaded.FocusIndicatorLighting, loaded.FocusIndicatorGlow) == FocusAppearance.ResolveLighting(lighting, glow) &&
                        loaded.WindowShelfActivationWidth == 640 && loaded.WindowShelfActivationHeight == 42 && loaded.WindowShelfHoverDelayMs == 700 &&
                        loaded.MoveWindowsToClickedTaskbar && loaded.AllowMovingFullscreenApps && !loaded.ScaleWindowSizeToMonitor && loaded.UndoMoveShortcut == 0 &&
                        loaded.CloseWindowShortcut == settings.CloseWindowShortcut && loaded.SearchAppsShortcut == settings.SearchAppsShortcut &&
                        loaded.MonitorMoveShortcuts.All(value => value == 0) && loaded.FocusWindowShortcuts.All(value => value == 0) && loaded.TileWindowShortcut == 0 &&
                        loaded.HiddenIdentities.SequenceEqual(settings.HiddenIdentities) && loaded.WindowShelfIdentities.SequenceEqual(settings.WindowShelfIdentities) &&
                        loaded.MonitorAliases["owned-display"] == "Owned neutral display" && loaded.ExcludedApps.Single().Key == "exe:owned-fixture",
                        "Owned save/reload preserves lighting migration state and every supplied released preference: " + preset + "/" + mode + "/lighting " + lighting + "/glow " + glow);
                    var history = new SettingsHistory(); history.Push(settings, null, "Appearance");
                    settings.AccentPreset = preset == "Original" ? "Coral" : "Original"; settings.FocusIndicatorStyle = (mode + 1) % 3;
                    settings.FocusIndicatorFinish = 1 - finish;
                    settings.FocusIndicatorGlow = !glow;
                    settings.FocusIndicatorLighting = (lighting + 1) % 4;
                    settings.HiddenIdentities.Clear(); settings.MonitorAliases.Clear();
                    check(history.Peek.Settings.AccentPreset == preset && history.Peek.Settings.FocusIndicatorStyle == mode && history.Peek.Settings.FocusIndicatorGlow == glow &&
                        history.Peek.Settings.FocusIndicatorFinish == finish && history.Peek.Settings.FocusIndicatorLighting == lighting &&
                        history.Peek.Settings.HiddenIdentities.Count == 1 && history.Peek.Settings.MonitorAliases.Count == 1,
                        "Settings history retains independent lighting and display recovery choices: " + preset + "/" + mode + "/lighting " + lighting + "/glow " + glow);
                }
            } finally {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                Directory.Delete(directory);
            }
        }

        static void FinishPersistenceAndHistory(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.FrameFinish." + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(directory);
            try {
                foreach (int finish in new[] { 0, 1 })
                foreach (int style in new[] { 0, 1, 2 })
                foreach (int lighting in new[] { -1, 0, 1, 2, 3 })
                foreach (bool glow in new[] { false, true }) {
                    var settings = new Settings {
                        FocusIndicatorFinish = finish, FocusIndicatorStyle = style, FocusIndicatorLighting = lighting,
                        FocusIndicatorGlow = glow, AccentPreset = "Peach", WindowShelfActivationWidth = 620,
                        WindowShelfActivationHeight = 36, WindowShelfHoverDelayMs = 650,
                        UndoMoveShortcut = 0, MonitorMoveShortcuts = new int[4], FocusWindowShortcuts = new int[4],
                        TileWindowShortcut = 0, CloseWindowShortcut = 0, SearchAppsShortcut = 0, MinimizeWindowShortcut = 0, PinWindowShortcut = 0
                    };
                    Settings copy = settings.Copy();
                    string expectedCopy = Serialize(settings), actualCopy = Serialize(copy);
                    bool copyMatches = SameJson(expectedCopy, actualCopy) && copy.FocusIndicatorFinish == finish;
                    check(copyMatches,
                        "Settings.Copy preserves the complete frame finish and legacy style/lighting state: " + finish + "/" + style + "/" + lighting + "/" + glow +
                        (copyMatches ? "" : OwnedSettingsDifference(expectedCopy, actualCopy, finish, copy.FocusIndicatorFinish)));
                    Program.Save(settings, path);
                    Settings loaded = Program.Load(path);
                    string expectedLoaded = Serialize(settings), actualLoaded = Serialize(loaded);
                    bool loadedMatches = SameJson(expectedLoaded, actualLoaded) && loaded.FocusIndicatorFinish == finish;
                    check(loadedMatches,
                        "Owned save/reload preserves both finishes with Off and every legacy style/lighting combination: " + finish + "/" + style + "/" + lighting + "/" + glow +
                        (loadedMatches ? "" : OwnedSettingsDifference(expectedLoaded, actualLoaded, finish, loaded.FocusIndicatorFinish) +
                            "; owned persisted JSON=" + File.ReadAllText(path)));
                    var history = new SettingsHistory(); history.Push(settings, null, "Frame style");
                    string expectedHistory = Serialize(copy);
                    settings.FocusIndicatorFinish = 1 - finish;
                    settings.FocusIndicatorStyle = style == 0 ? 2 : 0;
                    settings.FocusIndicatorLighting = lighting == 3 ? 0 : 3;
                    string actualHistory = Serialize(history.Peek.Settings);
                    bool historyMatches = SameJson(expectedHistory, actualHistory) && history.Peek.Settings.FocusIndicatorFinish == finish;
                    check(historyMatches,
                        "Undo history retains an independent previous frame finish without changing its enabled or lighting state: " + finish + "/" + style + "/" + lighting + "/" + glow +
                        (historyMatches ? "" : OwnedSettingsDifference(expectedHistory, actualHistory, finish, history.Peek.Settings.FocusIndicatorFinish)));
                }
            } finally {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                Directory.Delete(directory);
            }
        }

        static string OwnedSettingsDifference(string expected, string actual, int expectedFinish, int actualFinish)
        {
            var serializer = new JavaScriptSerializer();
            var expectedValues = serializer.Deserialize<Dictionary<string, object>>(expected);
            var actualValues = serializer.Deserialize<Dictionary<string, object>>(actual);
            string[] differences = expectedValues.Keys.Union(actualValues.Keys).Where(key =>
                !expectedValues.ContainsKey(key) || !actualValues.ContainsKey(key) ||
                !SameJsonValue(expectedValues[key], actualValues[key])).ToArray();
            return "; differing owned fields=" + (differences.Length == 0 ? "none (serialized property ordering differs)" : String.Join(", ", differences)) +
                "; expected owned settings=" + expected + "; actual owned settings=" + actual +
                "; expected finish=" + expectedFinish + "; actual finish=" + actualFinish;
        }

        static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls) {
                yield return child;
                foreach (Control descendant in Descendants(child)) yield return descendant;
            }
        }
        static void Realize(Control root)
        {
            if (root.Handle == IntPtr.Zero) throw new InvalidOperationException("Owned hidden appearance handle was not created.");
            foreach (Control child in root.Controls) Realize(child);
        }
        static void Layout(Control root)
        {
            root.PerformLayout(); foreach (Control child in root.Controls) Layout(child); root.PerformLayout();
        }
        static Rectangle BoundsIn(Control control, Control ancestor)
        {
            Rectangle bounds = control.Bounds;
            for (Control parent = control.Parent; parent != ancestor; parent = parent.Parent) {
                if (parent == null) throw new InvalidOperationException("Owned appearance control is outside its expected page.");
                bounds.Offset(parent.Location);
            }
            return bounds;
        }
        internal static Size ConstrainedMinimum(Rectangle workingArea, Rectangle requested)
        {
            // .NET Framework Form.MinimumSize uses this working-area rule:
            // a contained rectangle is unchanged; otherwise each dimension is
            // capped to the working area minus its one-pixel inset on each side.
            if (workingArea.Contains(requested)) return requested.Size;
            return new Size(Math.Min(workingArea.Width - 2, requested.Width),
                Math.Min(workingArea.Height - 2, requested.Height));
        }
        static void MinimumConstraintChecks(Action<bool, string> check)
        {
            check(ScaleNativeMinimum(new Size(740, 738), new Size(16, 39), 2) == new Size(1464, 1437),
                "Native minimum scaling doubles the client dimensions while retaining unscaled window chrome");
            check(ScaleNativeMinimum(new Size(740, 738), new Size(16, 39), 1) == new Size(740, 738),
                "Native minimum scaling at the current DPI preserves its dimensions");
            check(ConstrainedMinimum(new Rectangle(0, 0, 1024, 720), new Rectangle(0, 0, 1480, 1476)) == new Size(1022, 718),
                "The hosted 1024x720 working area constrains the designed 192-scale minimum to 1022x718");
            check(ConstrainedMinimum(new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 1480, 1476)) == new Size(1480, 1438),
                "A height-only working-area limit preserves the complete designed minimum width");
            check(ConstrainedMinimum(new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 740, 738)) == new Size(740, 738),
                "A fully contained native minimum remains unchanged");
            check(ConstrainedMinimum(new Rectangle(0, 0, 1024, 720), new Rectangle(0, 0, 1024, 720)) == new Size(1024, 720),
                "An exactly contained minimum does not receive an unnecessary working-area inset");
            check(ConstrainedMinimum(new Rectangle(-1024, -720, 1024, 720), new Rectangle(-1024, -720, 1480, 1476)) == new Size(1022, 718),
                "Native minimum constraints preserve the same dimensions for a negative-coordinate working area");
        }
        static Size ScaleNativeMinimum(Size minimum, Size chrome, float scale)
        {
            // Control.ScaleControl scales the nonnegative client minimum with
            // Math.Round, then adds its unchanged native adornment dimensions.
            return new Size((int)Math.Round(Math.Max(0, minimum.Width - chrome.Width) * scale) + chrome.Width,
                (int)Math.Round(Math.Max(0, minimum.Height - chrome.Height) * scale) + chrome.Height);
        }
        internal static void MinimumFixtureBounds(SettingsForm form, TabControl tabs, int dpi, Action<bool, string> check,
            Size previousMinimum, Size chrome, float scale)
        {
            Size scaled = ScaleNativeMinimum(previousMinimum, chrome, scale);
            var requested = new Rectangle(form.Location, scaled);
            Rectangle workingArea = Screen.GetWorkingArea(requested);
            Size expected = ConstrainedMinimum(workingArea, requested);
            // Keep the original designed tab-width allowance on an unconstrained
            // host; reduce it only by the proven native working-area width cap.
            int minimumTabWidth = (int)Math.Floor((740 - 64) * dpi / 96f) - (scaled.Width - expected.Width);
            check(form.MinimumSize == expected && form.Size == form.MinimumSize &&
                tabs.ClientSize.Width >= minimumTabWidth,
                "The owned minimum fixture uses the designed size constrained by the native working area at scale " + dpi +
                ": requested=" + requested + ", work=" + workingArea + ", expected=" + expected +
                ", previousMinimum=" + previousMinimum + ", nativeChrome=" + chrome + ", factor=" + scale +
                ", form=" + form.Size + ", minimum=" + form.MinimumSize + ", tabs=" + tabs.ClientSize + "/minimum " + minimumTabWidth + ", deviceDpi=" + form.DeviceDpi);
        }
        static void HiddenSettingsUi(Action<bool, string> check, Size? ownedMinimum = null)
        {
            MinimumConstraintChecks(check);
            Color originalAccent = Ui.Accent;
            byte[] originalBytes = File.Exists(Program.ConfigPath) ? File.ReadAllBytes(Program.ConfigPath) : null;
            var controller = new Controller(false) { Settings = new Settings { AccentPreset = "Forest Green", FocusIndicatorStyle = 2 }, Enabled = false };
            try {
                foreach (int dpi in ownedMinimum.HasValue ? new[] { 192 } : new[] { 96, 192 })
                using (var form = new SettingsForm(controller))
                using (var font = new Font("Segoe UI", 10 * dpi / 96f)) {
                    TabControl tabs = Descendants(form).OfType<TabControl>().Single();
                    TabPage page = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Appearance");
                    // A never-shown native tab can retain its placeholder page
                    // bounds if selected before its HWND exists. Realize the
                    // complete minimum form first, then select Appearance just
                    // as the running app does after its initial Displays page.
                    form.Size = form.MinimumSize; Realize(form); Layout(form);
                    Size previousMinimum = form.MinimumSize, nativeChrome = form.Size - form.ClientSize;
                    float scale = dpi / (float)form.DeviceDpi; form.AutoScaleMode = AutoScaleMode.None;
                    if (scale != 1) form.Scale(new SizeF(scale, scale));
                    form.Font = font; form.Size = form.MinimumSize; Layout(form);
                    MinimumFixtureBounds(form, tabs, dpi, check, previousMinimum, nativeChrome, scale);
                    if (ownedMinimum.HasValue) {
                        // Repeat the hosted small-desktop minimum on larger
                        // machines without changing any monitor or desktop DPI.
                        form.MinimumSize = ownedMinimum.Value; form.Size = form.MinimumSize; Layout(form);
                        check(form.MinimumSize == ownedMinimum.Value && form.Size == ownedMinimum.Value,
                            "The owned Appearance regression retains the exact hosted 1022x718 minimum without expanding its viewport");
                    }
                    Size previousPage = page.ClientSize;
                    tabs.SelectedTab = page; Layout(form);
                    AppearancePage appearance = Descendants(page).OfType<AppearancePage>().Single();
                    check(page.ClientSize.Width >= tabs.DisplayRectangle.Width - SystemInformation.VerticalScrollBarWidth &&
                        appearance.ClientSize.Width >= page.ClientSize.Width,
                        "The owned Appearance fixture uses the native minimum-width page rather than hidden placeholder bounds at scale " + dpi +
                        ": form=" + form.Size + ", minimum=" + form.MinimumSize + ", tabs=" + tabs.ClientSize +
                        ", tabDisplay=" + tabs.DisplayRectangle + ", previousPage=" + previousPage + ", page=" + page.ClientSize + ", content=" + appearance.ClientSize);
                    ToggleSwitch mode = appearance.FocusFrame;
                    AppearanceChoiceGrid finish = appearance.FinishChoices, lighting = appearance.LightingChoices, accent = appearance.AccentChoices;
                    check(!Descendants(page).OfType<ComboBox>().Any() && mode.Checked && mode.Text == "Focus frame" &&
                        finish.Items.SequenceEqual(FocusAppearance.FinishLabels) && finish.SelectedIndex == 0 &&
                        lighting.Items.SequenceEqual(FocusAppearance.LightingLabels) && lighting.SelectedIndex == 0 &&
                        accent.Items.SequenceEqual(FocusAppearance.Presets) && accent.SelectedItem == "Forest Green",
                        "Dedicated Appearance shows every visual option and the existing values without closed dropdowns at scale " + dpi);
                    check(!String.IsNullOrWhiteSpace(mode.AccessibleDescription) && !String.IsNullOrWhiteSpace(finish.AccessibleDescription) && !String.IsNullOrWhiteSpace(accent.AccessibleDescription) &&
                        mode.AutoCheck && finish.Cards.Length == 2 && lighting.Cards.Length == 4 && accent.Cards.Length == FocusAppearance.Presets.Length &&
                        Descendants(appearance).OfType<RadioButton>().Count() == 6 + FocusAppearance.Presets.Length,
                        "Appearance has one Focus frame toggle, two frame styles, four lighting and every organized accent card with readable group help at scale " + dpi);
                    check(lighting.Enabled && lighting.AccessibleDescription == SettingsForm.FocusIndicatorLightingHelp &&
                        !String.IsNullOrWhiteSpace(appearance.CombinedPreview.AccessibleDescription) &&
                        finish.Enabled && appearance.CombinedPreview.Finish == FocusFrameFinish.MutedEdge &&
                        appearance.CombinedPreview.FrameStyle == FocusIndicatorMode.InsetEdge && appearance.CombinedPreview.Lighting == FocusLightingMode.None &&
                        appearance.CombinedPreview.AccentPreset == "Forest Green" && !appearance.CombinedPreview.AnimationRunning,
                        "Lighting keeps the plain default and accessible effect/reduced-motion help beside its combined preview at scale " + dpi);
                    Control content = page.Controls[0];
                    check(BoundsIn(mode, content).Bottom <= BoundsIn(finish, content).Top && BoundsIn(finish, content).Bottom <= BoundsIn(lighting, content).Top &&
                        BoundsIn(lighting, content).Bottom <= BoundsIn(accent, content).Top,
                        "The frame styles fit below the switch, followed by lighting and accent at minimum width and scale " + dpi);
                    ChoiceLayout(appearance, page, dpi, check);
                    Size previousSize = tabs.Size, previousMaximum = tabs.MaximumSize;
                    DockStyle previousDock = tabs.Dock;
                    try {
                        // Dock changes synchronously relayout the table parent.
                        // Keep the measured native width from before that change.
                        tabs.Dock = DockStyle.None; tabs.MaximumSize = new Size(previousSize.Width, (int)Math.Ceiling(380 * dpi / 96f));
                        tabs.Size = tabs.MaximumSize; Layout(form);
                        check(tabs.Width == previousSize.Width && page.ClientSize.Width >= tabs.DisplayRectangle.Width - SystemInformation.VerticalScrollBarWidth &&
                            content.ClientSize.Width >= page.ClientSize.Width,
                            "The short Appearance viewport retains the supported native width at scale " + dpi);
                        check(page.AutoScroll && page.VerticalScroll.Visible && content.Height > page.ClientSize.Height,
                            "Appearance scrolls every visible option in a 380 DIP viewport instead of squeezing its cards at scale " + dpi);
                        ChoiceLayout(appearance, page, dpi, check);
                    } finally {
                        tabs.MaximumSize = previousMaximum; tabs.Size = previousSize; tabs.Dock = previousDock;
                        page.AutoScrollPosition = Point.Empty; Layout(form);
                    }
                    Size tooltipWork = new Size(360 * dpi / 96, 300 * dpi / 96);
                    Size lightingTip = SettingsForm.MeasureHelp(SettingsForm.FocusIndicatorLightingHelp, form.Font, dpi, tooltipWork);
                    check(lightingTip.Width > 0 && lightingTip.Height > 0 && lightingTip.Width <= tooltipWork.Width && lightingTip.Height <= tooltipWork.Height,
                        "Lighting helper text fits the bounded tooltip at scale " + dpi);

                    // Populate is the same guarded path used after settings
                    // changes. Do not invoke save handlers against a real profile
                    // from this hidden fixture.
                    controller.Settings.FocusIndicatorGlow = true; controller.Settings.FocusIndicatorStyle = 0; controller.Revision++; form.UpdateStatus();
                    check(!mode.Checked && finish.SelectedIndex == 0 && !finish.Enabled && lighting.SelectedIndex == 1 && !lighting.Enabled && controller.Settings.FocusIndicatorGlow && controller.Settings.FocusIndicatorLighting == -1 && controller.History.Peek == null,
                        "Turning the focus style Off disables lighting while preserving legacy glow migration state at scale " + dpi);
                    controller.Settings.FocusIndicatorStyle = 2; controller.Revision++; form.UpdateStatus();
                    check(mode.Checked && lighting.SelectedIndex == 1 && lighting.Enabled && controller.Settings.FocusIndicatorGlow && controller.Settings.FocusIndicatorLighting == -1 && controller.History.Peek == null,
                        "Restoring Focus frame re-enables legacy Soft glow without a save/history action or automatic effect upgrade at scale " + dpi);
                    controller.Settings.FocusIndicatorGlow = false; controller.Settings.FocusIndicatorStyle = 0; controller.Revision++; form.UpdateStatus();
                    check(lighting.SelectedIndex == 0 && !lighting.Enabled && !controller.Settings.FocusIndicatorGlow && controller.History.Peek == null,
                        "The disabled lighting control retains the legacy plain-frame choice at scale " + dpi);
                    foreach (int choice in new[] { 0, 1, 2, 3 }) {
                        controller.Settings.FocusIndicatorLighting = choice; controller.Settings.FocusIndicatorGlow = true; controller.Revision++; form.UpdateStatus();
                        check(lighting.SelectedIndex == choice && !lighting.Enabled && controller.Settings.FocusIndicatorLighting == choice && controller.Settings.FocusIndicatorGlow && controller.History.Peek == null,
                            "Off retains every explicit lighting choice and gives it priority over the legacy flag: " + choice + "/" + dpi);
                        controller.Settings.FocusIndicatorStyle = 2; controller.Revision++; form.UpdateStatus();
                        check(lighting.SelectedIndex == choice && lighting.Enabled && controller.Settings.FocusIndicatorLighting == choice && controller.History.Peek == null,
                            "An eligible style re-enables the same explicit lighting choice without a save/history action: " + choice + "/" + dpi);
                        controller.Settings.FocusIndicatorStyle = 0; controller.Revision++; form.UpdateStatus();
                    }
                    controller.Settings.FocusIndicatorLighting = 3;
                    controller.Settings.FocusIndicatorGlow = true;
                    foreach (int choice in new[] { 0, 1 }) {
                        controller.Settings.FocusIndicatorFinish = choice;
                        controller.Settings.FocusIndicatorStyle = 0; controller.Revision++; form.UpdateStatus();
                        check(finish.SelectedIndex == choice && !finish.Enabled && !lighting.Enabled && !mode.Checked &&
                            appearance.CombinedPreview.Finish == (FocusFrameFinish)choice && controller.Settings.FocusIndicatorFinish == choice &&
                            controller.Settings.FocusIndicatorLighting == 3 && controller.Settings.FocusIndicatorGlow && controller.History.Peek == null,
                            "Off retains each disabled frame finish and its existing lighting without a save/history action: " + choice + "/" + dpi);
                        controller.Settings.FocusIndicatorStyle = 1; controller.Revision++; form.UpdateStatus();
                        check(finish.SelectedIndex == choice && finish.Enabled && lighting.Enabled && mode.Checked &&
                            appearance.CombinedPreview.Finish == (FocusFrameFinish)choice && controller.Settings.FocusIndicatorStyle == 1 &&
                            controller.Settings.FocusIndicatorLighting == 3 && controller.History.Peek == null,
                            "Restoring a legacy enabled frame resumes the retained finish without rewriting the old style or lighting: " + choice + "/" + dpi);
                    }
                    foreach (string preset in FocusAppearance.Presets) {
                        controller.Settings.AccentPreset = preset; controller.Settings.FocusIndicatorStyle = 1; controller.Revision++;
                        form.UpdateStatus();
                        check(accent.SelectedItem == preset && mode.Checked && lighting.SelectedIndex == 3 && lighting.Enabled && controller.History.Peek == null &&
                            controller.Settings.FocusIndicatorStyle == 1 && appearance.CombinedPreview.FrameStyle == FocusIndicatorMode.InsetEdge && appearance.CombinedPreview.Lighting == FocusLightingMode.EnergyFlow &&
                            appearance.CombinedPreview.AccentPreset == preset && appearance.CombinedPreview.Finish == FocusFrameFinish.AccentOutline &&
                            finish.SelectedIndex == 1 && !appearance.CombinedPreview.AnimationRunning,
                            "Populate displays the legacy Muted frame as Focus frame without rewriting it or firing a save/history action: " + preset + "/" + dpi);
                    }
                    check(Ui.Accent == originalAccent && !form.Visible && !controller.DesktopRuntime,
                        "Hidden settings fixtures never apply a global accent or show/activate real windows at scale " + dpi);
                    byte[] currentBytes = File.Exists(Program.ConfigPath) ? File.ReadAllBytes(Program.ConfigPath) : null;
                    check(originalBytes == null ? currentBytes == null : currentBytes != null && originalBytes.SequenceEqual(currentBytes),
                        "Constructing, laying out and repopulating appearance cards never writes the active settings file at scale " + dpi);
                    controller.Settings.AccentPreset = "Forest Green"; controller.Settings.FocusIndicatorStyle = 2;
                    controller.Settings.FocusIndicatorFinish = 0;
                    controller.Settings.FocusIndicatorLighting = -1; controller.Settings.FocusIndicatorGlow = false;
                }
            } finally {
                // Controller.Dispose performs global taskbar recovery. A hidden UI fixture
                // owns no taskbar/work-area state and releases only its own components.
                controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                foreach (GlobalShortcut binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
            }
            check(Ui.Accent == originalAccent, "Disposing hidden appearance settings leaves the live UI palette unchanged");
        }

        static void ChoiceLayout(AppearancePage appearance, TabPage page, int dpi, Action<bool, string> check)
        {
            Control content = page.Controls[0];
            ToggleSwitch frame = appearance.FocusFrame;
            Size frameText = TextRenderer.MeasureText(frame.Text, frame.Font, new Size(Int32.MaxValue, Int32.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            check(frame.AutoCheck && frame.TabStop && frame.AccessibilityObject.Role == AccessibleRole.CheckButton &&
                frame.AccessibleName == "Focus frame" && !String.IsNullOrWhiteSpace(frame.AccessibleDescription) &&
                frame.Parent.ClientRectangle.Contains(frame.Bounds) && content.ClientRectangle.Contains(BoundsIn(frame, content)) &&
                frame.ClientRectangle.Contains(frame.TextBounds) && frame.TextBounds.Width >= frameText.Width && frame.TextBounds.Height >= frameText.Height,
                "The native Focus frame checkbox has a complete visible label, explanation and hit target at scale " + dpi);
            MethodInfo frameCommand = typeof(ToggleSwitch).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo checkboxCommand = typeof(CheckBox).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo frameDialog = typeof(ToggleSwitch).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo checkboxDialog = typeof(CheckBox).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
            check(frameCommand.DeclaringType == checkboxCommand.DeclaringType && frameDialog.DeclaringType == checkboxDialog.DeclaringType,
                "Focus frame inherits native checkbox command and dialog keyboard routes at scale " + dpi);
            page.ScrollControlIntoView(frame); Layout(page);
            check(page.ClientRectangle.Contains(BoundsIn(frame, page)),
                "The entire Focus frame toggle can be scrolled into the native viewport at scale " + dpi);
            foreach (AppearanceChoiceGrid grid in new[] { appearance.FinishChoices, appearance.LightingChoices, appearance.AccentChoices }) {
                AppearanceChoiceCard[] cards = grid.Cards;
                check(cards.Length == grid.Items.Length && cards.Select(card => card.Kind == AppearanceChoiceKind.Accent ? card.PresetName : card.Text).SequenceEqual(grid.Items) &&
                    cards.Select(card => card.AccessibleName).Distinct().Count() == cards.Length &&
                    cards.All(card => card.AutoCheck && card.AccessibilityObject.Role == AccessibleRole.RadioButton &&
                        !String.IsNullOrWhiteSpace(card.AccessibleName) && !String.IsNullOrWhiteSpace(card.AccessibleDescription) &&
                        !String.IsNullOrWhiteSpace(card.Description)),
                    "Every " + grid.AccessibleName + " option is a named native radio choice with its explanation always available at scale " + dpi);
                check(cards.Count(card => card.Checked) == 1 && cards[grid.SelectedIndex].Checked && cards.Any(card => card.TabStop),
                    "The current " + grid.AccessibleName + " choice remains checked and reachable through the native radio keyboard group at scale " + dpi);
                MethodInfo choiceCommand = typeof(AppearanceChoiceCard).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo nativeCommand = typeof(RadioButton).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo choiceDialog = typeof(AppearanceChoiceCard).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo nativeDialog = typeof(RadioButton).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
                check(choiceCommand.DeclaringType == nativeCommand.DeclaringType && choiceDialog.DeclaringType == nativeDialog.DeclaringType,
                    "The " + grid.AccessibleName + " cards inherit native radio command and dialog keyboard routes at scale " + dpi);
                foreach (AppearanceChoiceCard card in cards) {
                    Rectangle title = card.TitleBounds, description = card.DescriptionBounds, preview = card.PreviewBounds, radio = card.RadioBounds;
                    Size titleText;
                    using (var titleFont = new Font(card.Font, FontStyle.Bold))
                        titleText = TextRenderer.MeasureText(card.Text, titleFont, new Size(Math.Max(1, title.Width), Int32.MaxValue),
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    Size descriptionText = TextRenderer.MeasureText(card.Description, card.Font, new Size(Math.Max(1, description.Width), Int32.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    check(card.Width > 0 && card.Height > 0 && grid.ClientRectangle.Contains(card.Bounds) &&
                        content.ClientRectangle.Contains(BoundsIn(card, content)) &&
                        card.ClientRectangle.Contains(title) && card.ClientRectangle.Contains(preview) && card.ClientRectangle.Contains(radio) &&
                        (card.Kind == AppearanceChoiceKind.Accent ? description.IsEmpty : card.ClientRectangle.Contains(description) && description.Height >= descriptionText.Height) &&
                        title.Width >= titleText.Width && title.Height >= titleText.Height &&
                        !title.IntersectsWith(description) && !title.IntersectsWith(preview) && !description.IntersectsWith(preview) &&
                        !radio.IntersectsWith(title) && !radio.IntersectsWith(preview) && !radio.IntersectsWith(description),
                        "The complete " + card.Text + " label, explanation and visual fit without overlap at scale " + dpi +
                        ": card=" + card.Bounds + ", grid=" + grid.ClientRectangle + ", content=" + content.ClientRectangle +
                        ", inContent=" + BoundsIn(card, content) + ", title=" + title + "/" + titleText +
                        ", description=" + description + "/" + descriptionText + ", preview=" + preview + ", radio=" + radio);
                    if (card.Kind == AppearanceChoiceKind.Accent) {
                        double scale = dpi / 96.0;
                        check(card.Width <= Math.Ceiling(180 * scale) && card.Height <= Math.Ceiling(64 * scale) &&
                            preview.Width == preview.Height && preview.Width <= Math.Ceiling(32 * scale) &&
                            preview.Right <= title.Left && title.Right <= radio.Left &&
                            card.MaximumAccentWidth <= Math.Ceiling(180 * scale) && card.MinimumAccentHeight <= Math.Ceiling(64 * scale),
                            "Each accent stays a compact horizontal named chip with a small square swatch and separate radio at scale " + dpi + ": " + card.Text);
                    }
                    check(cards.Where(other => other != card).All(other => !other.Bounds.IntersectsWith(card.Bounds)),
                        "The " + card.Text + " native hit target does not overlap any neighboring choice at scale " + dpi);
                    page.ScrollControlIntoView(card); Layout(page);
                    check(page.ClientRectangle.Contains(BoundsIn(card, page)),
                        "The entire " + card.Text + " option and its explanation can be scrolled into view at scale " + dpi);
                }
            }
            FamilyChoiceLayout(appearance.AccentChoices, page, content, dpi, check);
            Control combined = appearance.CombinedPreview;
            check(combined.Width > 0 && combined.Height > 0 && !combined.TabStop && !String.IsNullOrWhiteSpace(combined.AccessibleName) &&
                content.ClientRectangle.Contains(BoundsIn(combined, content)),
                "The combined appearance preview is contained and does not take keyboard focus at scale " + dpi);
            page.ScrollControlIntoView(combined); Layout(page);
            check(page.ClientRectangle.Contains(BoundsIn(combined, page)),
                "The whole combined appearance preview is reachable in the native viewport at scale " + dpi);
            page.AutoScrollPosition = Point.Empty;
        }
        static void FamilyChoiceLayout(AppearanceChoiceGrid accents, TabPage page, Control content, int dpi, Action<bool, string> check)
        {
            string[] labels = FocusAppearance.AccentFamilyLabels;
            string[] displayNames = labels.SelectMany(FocusAppearance.AccentFamilyPresets).ToArray();
            Label[] headings = accents.FamilyHeadings;
            AppearanceChoiceCard[] displayOrder = accents.DisplayOrder;
            check(accents.Items.SequenceEqual(FocusAppearance.Presets) && accents.Cards.Select(card => card.PresetName).SequenceEqual(FocusAppearance.Presets) &&
                accents.Cards.Select(card => card.Text).SequenceEqual(FocusAppearance.AccentLabels) &&
                displayOrder.Select(card => card.PresetName).SequenceEqual(displayNames) && headings.Select(heading => heading.Text).SequenceEqual(labels),
                "Family layout preserves canonical saved-color indices while exposing the intended organized visual order at scale " + dpi);
            check(accents.Cards.All(card => Object.ReferenceEquals(card.Parent, accents)) &&
                accents.Cards.All(card => card.AccessibleName == FocusAppearance.AccentLabel(card.PresetName) &&
                    card.AccessibleDescription.IndexOf("Color family: " + FocusAppearance.AccentFamily(card.PresetName), StringComparison.Ordinal) >= 0) &&
                displayOrder.Select(card => card.TabIndex).SequenceEqual(Enumerable.Range(0, displayOrder.Length)) &&
                accents.Cards.OrderBy(card => card.Top).ThenBy(card => card.Left).Select(card => card.PresetName).SequenceEqual(displayNames) &&
                accents.Controls.Cast<Control>().Select(control => control.Text).SequenceEqual(labels.SelectMany(label => new[] { label }.Concat(FocusAppearance.AccentFamilyPresets(label).Select(FocusAppearance.AccentLabel)))),
                "All color cards remain one native radio group with keyboard and geometric reading order following their families at scale " + dpi);
            foreach (Label heading in headings) {
                Size text = TextRenderer.MeasureText(heading.Text, heading.Font, new Size(Int32.MaxValue, Int32.MaxValue),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                AppearanceChoiceCard[] members = accents.Cards.Where(card => FocusAppearance.AccentFamily(card.PresetName) == heading.Text).ToArray();
                check(Object.ReferenceEquals(heading.Parent, accents) && !heading.TabStop && heading.AccessibleName == heading.Text &&
                    heading.AccessibilityObject.Role == AccessibleRole.StaticText && heading.ClientSize.Width >= text.Width && heading.ClientSize.Height >= text.Height &&
                    accents.ClientRectangle.Contains(heading.Bounds) && content.ClientRectangle.Contains(BoundsIn(heading, content)) &&
                    members.Length > 0 && heading.Bottom <= members.Min(card => card.Top) &&
                    accents.Cards.All(card => !card.Bounds.IntersectsWith(heading.Bounds)) &&
                    headings.Where(other => other != heading).All(other => !other.Bounds.IntersectsWith(heading.Bounds)),
                    "Each complete, non-focusable family heading fits above its named cards without overlapping choices at scale " + dpi + ": " + heading.Text);
                page.ScrollControlIntoView(heading); Layout(page);
                check(page.ClientRectangle.Contains(BoundsIn(heading, page)),
                    "Each family heading can be reached in the native Appearance viewport at scale " + dpi + ": " + heading.Text);
            }
            Label[] returnedHeadings = accents.FamilyHeadings; returnedHeadings[0] = null;
            AppearanceChoiceCard[] returnedOrder = accents.DisplayOrder; returnedOrder[0] = null;
            check(accents.FamilyHeadings[0] != null && accents.DisplayOrder[0] != null &&
                accents.Cards.Count(card => card.Checked) == 1,
                "Family metadata arrays are defensive copies and layout keeps exactly one selected accent across all families at scale " + dpi);
        }

        static string Serialize(Settings settings) { return new JavaScriptSerializer().Serialize(settings); }
        internal static bool SameJson(string expected, string actual)
        {
            if (expected == actual) return true;
            var serializer = new JavaScriptSerializer();
            return SameJsonValue(serializer.DeserializeObject(expected), serializer.DeserializeObject(actual));
        }
        static bool SameJsonValue(object expected, object actual)
        {
            if (Object.ReferenceEquals(expected, actual)) return true;
            if (expected == null || actual == null) return false;
            var expectedObject = expected as IDictionary<string, object>;
            var actualObject = actual as IDictionary<string, object>;
            if (expectedObject != null || actualObject != null)
                return expectedObject != null && actualObject != null && expectedObject.Count == actualObject.Count &&
                    expectedObject.All(item => actualObject.ContainsKey(item.Key) && SameJsonValue(item.Value, actualObject[item.Key]));
            var expectedArray = expected as System.Collections.IList;
            var actualArray = actual as System.Collections.IList;
            if (expectedArray != null || actualArray != null) {
                if (expectedArray == null || actualArray == null || expectedArray.Count != actualArray.Count) return false;
                for (int index = 0; index < expectedArray.Count; index++)
                    if (!SameJsonValue(expectedArray[index], actualArray[index])) return false;
                return true;
            }
            return expected.GetType() == actual.GetType() && expected.Equals(actual);
        }
        static void JsonComparisonChecks(Action<bool, string> check)
        {
            const string baseline = "{\"Finish\":1,\"Enabled\":true,\"Alias\":\"a\",\"Bindings\":[1,2],\"Nested\":{\"Color\":\"Peach\",\"Value\":null}}";
            const string reordered = "{\"Nested\":{\"Value\":null,\"Color\":\"Peach\"},\"Bindings\":[1,2],\"Alias\":\"a\",\"Enabled\":true,\"Finish\":1}";
            check(SameJson(baseline, reordered) && SameJson(reordered, baseline),
                "Decoded settings comparison accepts reordered root and nested object keys while retaining all values");
            check(SameJson("[{\"a\":1,\"b\":2},{\"c\":true}]", "[{\"b\":2,\"a\":1},{\"c\":true}]") &&
                SameJson("{\"Alias\":\"a\"}", "{\"Alias\":\"\\u0061\"}"),
                "Decoded settings comparison accepts equivalent nested objects inside ordered arrays and equivalent escaped scalar text");
            foreach (string changed in new[] {
                "{\"Enabled\":true,\"Alias\":\"a\",\"Bindings\":[1,2],\"Nested\":{\"Color\":\"Peach\",\"Value\":null}}",
                baseline.Replace("\"Finish\":1", "\"Finish\":1,\"Extra\":0"),
                baseline.Replace("\"Finish\":1", "\"Finish\":2"),
                baseline.Replace("\"Finish\":1", "\"Finish\":\"1\""),
                baseline.Replace("\"Finish\":1", "\"Finish\":1.0"),
                baseline.Replace("\"Enabled\":true", "\"Enabled\":1"),
                baseline.Replace("[1,2]", "[2,1]"),
                baseline.Replace("[1,2]", "[1,2,3]"),
                baseline.Replace("\"Value\":null", "\"Value\":0"),
                baseline.Replace("\"Color\":\"Peach\",\"Value\":null", "\"Value\":null"),
                baseline.Replace("\"Value\":null", "\"Value\":null,\"Extra\":false"),
                baseline.Replace("\"Color\":\"Peach\"", "\"Color\":\"Mint\""),
                baseline.Replace("\"Finish\":1", "\"finish\":1") })
                check(!SameJson(baseline, changed) && !SameJson(changed, baseline),
                    "Decoded settings comparison rejects changed key sets, ordered arrays, scalar types/values and nested content: " + changed);
            check(!SameJson("[1,2]", "{\"0\":1,\"1\":2}") && !SameJson("null", "false"),
                "Decoded settings comparison distinguishes container types and null from scalar values");
        }
        static string ReleasedPreferences(Settings settings)
        {
            Settings copy = settings.Copy();
            copy.FocusIndicatorStyle = 0; copy.FocusIndicatorFinish = 0; copy.FocusIndicatorGlow = false; copy.FocusIndicatorLighting = -1; copy.AccentPreset = "Original";
            return Serialize(copy);
        }
        static void ClickChoice(AppearanceChoiceCard card)
        {
            // Exercise the owned native control's click/AutoCheck path. This
            // invokes no global keyboard/mouse input and never shows the form.
            typeof(RadioButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(card, new object[] { EventArgs.Empty });
        }
        static void ClickFrame(ToggleSwitch frame)
        {
            typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(frame, new object[] { EventArgs.Empty });
        }
        static string ExceptFinish(Settings settings)
        {
            Settings copy = settings.Copy(); copy.FocusIndicatorFinish = 0;
            return Serialize(copy);
        }
        static void ChoiceSelectionAndAutosave(Action<bool, string> check)
        {
            bool originalTest = AppProfile.IsTest;
            string originalDirectory = AppProfile.Directory;
            Color originalAccent = Ui.Accent;
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.AppearanceCallbacks." + Guid.NewGuid().ToString("N"));
            Controller controller = null;
            try {
                AppProfile.Initialize(new[] { "--test-profile", directory });
                var settings = new Settings {
                    FocusIndicatorStyle = 1, FocusIndicatorGlow = true, FocusIndicatorLighting = -1, AccentPreset = "Forest Green",
                    WindowShelfActivationWidth = 620, WindowShelfActivationHeight = 36, WindowShelfHoverDelayMs = 650,
                    MoveWindowsToClickedTaskbar = false, ScaleWindowSizeToMonitor = false, AllowMovingFullscreenApps = true,
                    UndoMoveShortcut = 0, MonitorMoveShortcuts = new int[4], FocusWindowShortcuts = new int[4],
                    TileWindowShortcut = 0, CloseWindowShortcut = 0, SearchAppsShortcut = 0, MinimizeWindowShortcut = 0, PinWindowShortcut = 0
                };
                settings.HiddenIdentities.Add("owned-callback-display");
                settings.MonitorAliases.Add("owned-callback-display", "Owned callback display");
                settings.ExcludedApps.Add(new AppRule { Key = "exe:owned-callback", Name = "Owned callback app" });
                controller = new Controller(false) { Settings = settings, Enabled = false, RefreshDisplayIdentity = false };
                // Refresh is part of the real save callback. Keep its discovery
                // fenced to this empty, owned fixture so it cannot inspect or
                // restore Explorer taskbars or change any live work area.
                typeof(Controller).GetField("nextDiscovery", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, DateTime.MaxValue);
                Program.Save(settings);
                // Simulate a released profile that has no frame-finish property.
                // Opening and laying out the settings window must not migrate it
                // through an autosave or rewrite its legacy style/lighting values.
                var legacyJson = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Serialize(settings));
                legacyJson.Remove("FocusIndicatorFinish");
                File.WriteAllBytes(Program.ConfigPath, System.Text.Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(legacyJson)));
                byte[] originalProfileBytes = File.ReadAllBytes(Program.ConfigPath);
                string released = ReleasedPreferences(settings);
                using (var form = new SettingsForm(controller)) {
                    AppearancePage appearance = Descendants(form).OfType<AppearancePage>().Single();
                    TabControl tabs = Descendants(form).OfType<TabControl>().Single();
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Appearance");
                    Realize(form); Layout(form);
                    check(SameJson(Serialize(Program.Load()), Serialize(settings)) && controller.Settings.FocusIndicatorStyle == 1 &&
                        appearance.FocusFrame.Checked && appearance.CombinedPreview.FrameStyle == FocusIndicatorMode.InsetEdge &&
                        appearance.FinishChoices.SelectedIndex == 0 && appearance.CombinedPreview.Finish == FocusFrameFinish.MutedEdge &&
                        Program.Load().FocusIndicatorFinish == 0 && originalProfileBytes.SequenceEqual(File.ReadAllBytes(Program.ConfigPath)) &&
                        controller.Revision == 0 && controller.History.Peek == null,
                        "Opening a profile without frame finish shows its existing Muted edge without enabling, migrating, rewriting any byte or creating history");
                    int styleEvents = 0, finishEvents = 0, lightingEvents = 0, accentEvents = 0;
                    appearance.FocusFrame.CheckedChanged += delegate { styleEvents++; };
                    appearance.FinishChoices.SelectedIndexChanged += delegate { finishEvents++; };
                    appearance.LightingChoices.SelectedIndexChanged += delegate { lightingEvents++; };
                    appearance.AccentChoices.SelectedIndexChanged += delegate { accentEvents++; };
                    foreach (int choice in new[] { 1, 0, 1 }) {
                        Settings previous = controller.Settings.Copy(); int revision = controller.Revision, previousEvents = finishEvents;
                        ClickChoice(appearance.FinishChoices.Cards[choice]);
                        check(controller.Settings.FocusIndicatorFinish == choice && Program.Load().FocusIndicatorFinish == choice &&
                            appearance.FinishChoices.SelectedIndex == choice && appearance.FinishChoices.Cards.Count(card => card.Checked) == 1 &&
                            appearance.FinishChoices.Cards[choice].Checked && appearance.CombinedPreview.Finish == (FocusFrameFinish)choice &&
                            finishEvents == previousEvents + 1 && controller.Revision == revision + 1 &&
                            SameJson(Serialize(controller.History.Peek.Settings), Serialize(previous)),
                            "Each native frame-style choice invokes exactly one real autosave and retains its complete previous Undo snapshot: " + choice);
                        check(SameJson(ExceptFinish(controller.Settings), ExceptFinish(previous)) && SameJson(ReleasedPreferences(controller.Settings), released) &&
                            styleEvents == 0 && lightingEvents == 0 && accentEvents == 0 && controller.Settings.FocusIndicatorStyle == 1 &&
                            controller.Settings.FocusIndicatorGlow && controller.Settings.FocusIndicatorLighting == -1,
                            "Frame-style changes only finish, preserving the stored legacy enabled style, lighting, accent and all released preferences: " + choice);
                    }
                    foreach (int choice in new[] { 0, 2, 0, 2 }) {
                        Settings previous = controller.Settings.Copy(); int revision = controller.Revision, previousEvents = styleEvents;
                        ClickFrame(appearance.FocusFrame);
                        check(controller.Settings.FocusIndicatorStyle == choice && Program.Load().FocusIndicatorStyle == choice &&
                            appearance.FocusFrame.Checked == (choice != 0) && styleEvents == previousEvents + 1 && controller.Revision == revision + 1 &&
                            appearance.CombinedPreview.FrameStyle == (choice == 0 ? FocusIndicatorMode.Off : FocusIndicatorMode.InsetEdge),
                            "A native Focus frame click invokes one actual autosave and writes only canonical Off/Inset edge: " + choice);
                        check(SameJson(Serialize(controller.History.Peek.Settings), Serialize(previous)) && controller.Settings.FocusIndicatorLighting == -1 &&
                            controller.Settings.FocusIndicatorGlow && controller.Settings.FocusIndicatorFinish == 1 && Program.Load().FocusIndicatorFinish == 1 &&
                            appearance.FinishChoices.SelectedIndex == 1 && appearance.FinishChoices.Enabled == (choice != 0) &&
                            appearance.CombinedPreview.Finish == FocusFrameFinish.AccentOutline &&
                            appearance.LightingChoices.SelectedIndex == 1 && appearance.LightingChoices.Enabled == (choice != 0) &&
                            SameJson(ReleasedPreferences(controller.Settings), released),
                            "Frame selection preserves legacy glow, the recoverable previous state and every unrelated preference: " + choice);
                    }
                    foreach (int choice in new[] { 0, 1, 2, 3 }) {
                        Settings previous = controller.Settings.Copy(); int revision = controller.Revision, previousEvents = lightingEvents;
                        ClickChoice(appearance.LightingChoices.Cards[choice]);
                        check(controller.Settings.FocusIndicatorLighting == choice && Program.Load().FocusIndicatorLighting == choice &&
                            appearance.LightingChoices.SelectedIndex == choice && appearance.LightingChoices.Cards.Count(card => card.Checked) == 1 &&
                            appearance.LightingChoices.Cards[choice].Checked && lightingEvents == previousEvents + 1 && controller.Revision == revision + 1 &&
                            SameJson(Serialize(controller.History.Peek.Settings), Serialize(previous)),
                            "Each native lighting choice persists once through the real settings callback and retains Undo state: " + choice);
                        check(SameJson(ReleasedPreferences(controller.Settings), released) && controller.Settings.FocusIndicatorStyle == 2 &&
                            controller.Settings.FocusIndicatorFinish == 1 && appearance.CombinedPreview.Finish == FocusFrameFinish.AccentOutline &&
                            controller.Settings.FocusIndicatorGlow == (choice == 1),
                            "Explicit lighting updates its legacy compatibility flag while preserving unrelated settings: " + choice);
                    }
                    int styleEventsBeforeColors = styleEvents, finishEventsBeforeColors = finishEvents, lightingEventsBeforeColors = lightingEvents;
                    foreach (AppearanceChoiceCard colorChoice in appearance.AccentChoices.DisplayOrder) {
                        int choice = Array.IndexOf(appearance.AccentChoices.Items, colorChoice.PresetName);
                        Settings previous = controller.Settings.Copy(); int revision = controller.Revision, previousEvents = accentEvents;
                        bool changed = previous.AccentPreset != colorChoice.PresetName;
                        var previousHistory = controller.History.Peek;
                        ClickChoice(colorChoice);
                        string preset = FocusAppearance.Presets[choice];
                        check(controller.Settings.AccentPreset == preset && Program.Load().AccentPreset == preset &&
                            appearance.AccentChoices.SelectedItem == preset && appearance.AccentChoices.Cards.Count(card => card.Checked) == 1 &&
                            appearance.AccentChoices.Cards[choice].Checked && appearance.AccentChoices.Cards.All(option => Object.ReferenceEquals(option.Parent, appearance.AccentChoices)) &&
                            accentEvents == previousEvents + (changed ? 1 : 0) && controller.Revision == revision + (changed ? 1 : 0) &&
                            (changed ? SameJson(Serialize(controller.History.Peek.Settings), Serialize(previous)) : Object.ReferenceEquals(previousHistory, controller.History.Peek)),
                            "Each named native accent swatch saves once when changed and retains Undo without resaving the selected color: " + preset);
                        check(SameJson(ReleasedPreferences(controller.Settings), released) && controller.Settings.FocusIndicatorStyle == 2 &&
                            controller.Settings.FocusIndicatorFinish == 1 && appearance.CombinedPreview.Finish == FocusFrameFinish.AccentOutline &&
                            controller.Settings.FocusIndicatorLighting == 3 && !controller.Settings.FocusIndicatorGlow && Ui.Accent == originalAccent &&
                            styleEvents == styleEventsBeforeColors && finishEvents == finishEventsBeforeColors && lightingEvents == lightingEventsBeforeColors,
                            "Selecting an accent preserves frame/lighting and does not apply a live global palette from a hidden fixture: " + preset);
                    }
                    int previousRevision = controller.Revision;
                    string previousSettings = Serialize(controller.Settings);
                    string lastColor = appearance.AccentChoices.SelectedItem;
                    appearance.FocusFrame.Checked = true; ClickChoice(appearance.FinishChoices.Cards[1]); ClickChoice(appearance.LightingChoices.Cards[3]);
                    ClickChoice(appearance.AccentChoices.Cards[appearance.AccentChoices.SelectedIndex]);
                    check(controller.Revision == previousRevision && SameJson(Serialize(controller.Settings), previousSettings),
                        "Assigning the current toggle state and clicking already-selected appearance cards creates no redundant save/history actions");
                    check(!form.Visible && !controller.DesktopRuntime && controller.Bars.Count == 0 && controller.Displays.Count == 0 &&
                        controller.Areas.SnapTargets.Count == 0 && controller.UndoShortcut.Current == Keys.None &&
                        controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts).All(binding => binding.Current == Keys.None) &&
                        !File.Exists(WorkAreas.JournalPath) && !appearance.CombinedPreview.AnimationRunning &&
                        appearance.CombinedPreview.FrameStyle == FocusIndicatorMode.InsetEdge && appearance.CombinedPreview.Lighting == FocusLightingMode.EnergyFlow &&
                        appearance.CombinedPreview.AccentPreset == lastColor && appearance.CombinedPreview.Finish == FocusFrameFinish.AccentOutline,
                        "Real appearance callbacks stay in the owned test profile without desktop hooks, shortcuts, taskbar discovery or work-area changes");
                }
                var grid = new AppearancePage();
                AppearanceChoiceCard[] ownedCards = grid.FinishChoices.Cards.Concat(grid.LightingChoices.Cards).Concat(grid.AccentChoices.Cards).ToArray();
                Label[] ownedFamilyHeadings = grid.AccentChoices.FamilyHeadings;
                using (grid) {
                    AppearanceChoiceGrid choices = grid.LightingChoices;
                    int notifications = 0; choices.SelectedIndexChanged += delegate { notifications++; };
                    choices.SelectedIndex = 2; int count = notifications; choices.SelectedIndex = 2;
                    check(notifications == count && choices.Cards.Count(card => card.Checked) == 1 && choices.Cards[2].Checked,
                        "Repeated selected-index assignment emits no event and keeps one checked native option");
                    choices.SelectedIndex = -1;
                    check(choices.SelectedIndex == -1 && choices.SelectedItem == null && choices.Cards.All(card => !card.Checked) && notifications == count + 1,
                        "The guarded populate API can clear a selection with one notification and no checked option");
                    string[] items = choices.Items; items[0] = "changed";
                    AppearanceChoiceCard[] cards = choices.Cards; cards[0] = null;
                    check(choices.Items[0] == "None" && choices.Cards[0] != null && choices.Cards.Length == 4,
                        "Returning appearance item and card arrays cannot mutate the grid's canonical choices");
                }
                check(grid.IsDisposed && grid.FocusFrame.IsDisposed && grid.CombinedPreview.IsDisposed && ownedCards.All(card => card.IsDisposed) &&
                    ownedFamilyHeadings.All(heading => heading.IsDisposed) && !grid.CombinedPreview.AnimationRunning,
                    "Disposing the owned appearance page releases every native choice and its static preview without an animation timer");
            } finally {
                if (controller != null) {
                    controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                    foreach (GlobalShortcut binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
                }
                AppProfile.Initialize(originalTest ? new[] { "--test-profile", originalDirectory } : new string[0]);
                string path = Path.Combine(directory, "settings.json");
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                if (Directory.Exists(directory)) Directory.Delete(directory);
            }
            check(AppProfile.IsTest == originalTest && AppProfile.Directory == originalDirectory && Ui.Accent == originalAccent,
                "Owned callback checks restore the original profile selection and leave the live accent unchanged");
        }

        sealed class MoveLoopEvents : IFocusIndicatorEvents
        {
            internal int Disposals;
            public void Dispose() { Disposals++; }
        }
        sealed class MoveLoopSurface : IFocusIndicatorSurface
        {
            internal int Presents, Disposals;
            internal FocusIndicatorVisual Last;
            public IntPtr Window { get { return new IntPtr(900); } }
            public void Present(FocusIndicatorVisual visual) { Presents++; Last = visual; }
            public void Dispose() { Disposals++; }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        static void ControllerFinishPolicy(Action<bool, string> check)
        {
            var settings = new Settings { FocusIndicatorStyle = 2, AccentPreset = "Peach", FocusIndicatorLighting = 0 };
            var controller = new Controller(false) { Settings = settings, Enabled = true };
            var target = new FocusIndicatorTarget {
                Identity = new FocusedWindowSnapshot { Window = new IntPtr(77), Root = new IntPtr(77), Process = 7, Thread = 8,
                    ProcessStarted = 99, Visible = true, ClassName = "Owned injected finish target", Device = "Owned display" },
                Frame = new Rectangle(100, 100, 600, 400), Monitors = new[] { new Rectangle(0, 0, 1280, 1024) }, Dpi = 96
            };
            Color originalAccent = Ui.Accent;
            FocusIndicator indicator = null;
            var surfaces = new List<MoveLoopSurface>();
            try {
                var api = new FocusIndicatorApi {
                    Foreground = delegate { return target.Identity.Window; }, Read = delegate { return target; },
                    DesktopAllowed = delegate { return true; }, Dark = delegate { return true; }, GlowAllowed = delegate { return true; },
                    Now = delegate { return 1000; }, PointerButtonDown = delegate { return false; },
                    Watch = delegate(FocusedWindowSnapshot window, Action<FocusIndicatorChange> callback) { return new MoveLoopEvents(); }
                };
                indicator = new FocusIndicator(api, delegate { var surface = new MoveLoopSurface(); surfaces.Add(surface); return surface; });
                typeof(Controller).GetField("FocusFrame", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, indicator);
                foreach (int finish in new[] { 0, 1 })
                foreach (int style in new[] { 0, 1, 2 })
                foreach (int lighting in new[] { 0, 1, 2, 3 }) {
                    settings.FocusIndicatorFinish = finish; settings.FocusIndicatorStyle = style; settings.FocusIndicatorLighting = lighting;
                    string previous = Serialize(settings);
                    int previousPresents = surfaces.Sum(surface => surface.Presents);
                    controller.RefreshFocusIndicator();
                    if (style == 0) {
                        check(!indicator.Visible && surfaces.Sum(surface => surface.Presents) == previousPresents,
                            "Controller Off retains the chosen finish without publishing any surface: " + finish + "/" + lighting);
                    } else {
                        FocusIndicatorVisual visual = surfaces.Last().Last;
                        check(indicator.Visible && visual.Finish == (FocusFrameFinish)finish && visual.Mode == FocusIndicatorMode.InsetEdge &&
                            visual.Lighting == (FocusLightingMode)lighting && visual.AccentColor == FocusAppearance.Accent("Peach", true),
                            "Controller forwards each finish and lighting while canonicalizing both retained legacy enabled styles: " + finish + "/" + style + "/" + lighting);
                    }
                    check(SameJson(Serialize(settings), previous) && controller.History.Peek == null && controller.FocusIndicatorError == null,
                        "Refreshing a retained finish never rewrites settings, adds Undo history or reports an injected rendering error: " + finish + "/" + style + "/" + lighting);
                }
                settings.FocusIndicatorStyle = 2;
                settings.FocusIndicatorFinish = 1;
                settings.FocusIndicatorLighting = 1;
                string retained = Serialize(settings);
                controller.MovementPaused = true;
                int beforePause = surfaces.Sum(surface => surface.Presents);
                controller.RefreshFocusIndicator();
                check(!indicator.Visible && surfaces.Sum(surface => surface.Presents) == beforePause && SameJson(Serialize(settings), retained),
                    "Pausing movement removes the injected focus frame without publishing or forgetting Accent outline and Soft glow");
                controller.MovementPaused = false;
                controller.RefreshFocusIndicator();
                check(indicator.Visible && surfaces.Last().Last.Finish == FocusFrameFinish.AccentOutline &&
                    surfaces.Last().Last.Lighting == FocusLightingMode.SubtleGlow && SameJson(Serialize(settings), retained),
                    "Resuming movement forwards the retained Accent outline and Soft glow through the controller");
                controller.Enabled = false;
                int beforeDisable = surfaces.Sum(surface => surface.Presents);
                controller.RefreshFocusIndicator();
                check(!indicator.Visible && surfaces.Sum(surface => surface.Presents) == beforeDisable && SameJson(Serialize(settings), retained),
                    "Pausing all removes the injected frame without publishing or changing its saved finish");
                controller.Enabled = true;
                controller.RefreshFocusIndicator();
                check(indicator.Visible && surfaces.Last().Last.Finish == FocusFrameFinish.AccentOutline && SameJson(Serialize(settings), retained) &&
                    !controller.DesktopRuntime && Ui.Accent == originalAccent && controller.History.Peek == null,
                    "Re-enabling forwards the retained finish using only fake observation/surfaces and leaves the desktop palette/history untouched");
            } finally {
                if (indicator != null) indicator.Dispose();
                controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                foreach (GlobalShortcut binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
            }
        }

        static void SettingsMoveLoop(Action<bool, string> check)
        {
            var controller = new Controller(false) { Settings = new Settings { FocusIndicatorStyle = 1 }, Enabled = false };
            FocusIndicator indicator = null;
            Color originalAccent = Ui.Accent;
            try {
                using (var form = new SettingsForm(controller)) {
                    // Real messages go only to this owned hidden HWND. All foreground,
                    // geometry, time, subscriptions and decoration surfaces are fakes.
                    IntPtr owned = form.Handle;
                    // CenterScreen may place this hidden fixture beyond a fixed
                    // virtual rectangle on a wide desktop. Keep its fake monitor
                    // around the actual starting bounds and all three drag steps.
                    Rectangle fixtureMonitor = Rectangle.Inflate(form.Bounds, 1000, 1000);
                    var target = new FocusIndicatorTarget {
                        Identity = new FocusedWindowSnapshot { Window = owned, Root = owned, Process = 7, Thread = 8,
                            ProcessStarted = 99, Visible = true, ClassName = "Owned hidden Settings", Device = "Owned display" },
                        Frame = form.Bounds, Monitors = new[] { fixtureMonitor }, Dpi = 96
                    };
                    long now = 1000;
                    Action<FocusIndicatorChange> eventCallback = null;
                    var events = new MoveLoopEvents();
                    var surfaces = new List<MoveLoopSurface>();
                    var api = new FocusIndicatorApi {
                        Foreground = delegate { return owned; }, Read = delegate { return target; },
                        DesktopAllowed = delegate { return true; }, Dark = delegate { return true; },
                        Now = delegate { return now; }, PointerButtonDown = delegate { return false; },
                        Watch = delegate(FocusedWindowSnapshot window, Action<FocusIndicatorChange> callback) { eventCallback = callback; return events; }
                    };
                    indicator = new FocusIndicator(api, delegate { var surface = new MoveLoopSurface(); surfaces.Add(surface); return surface; });
                    typeof(Controller).GetField("FocusFrame", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, indicator);
                    indicator.Update(1, "Original", false);
                    check(indicator.Visible && surfaces.Count == 1 && surfaces[0].Last.Target.Identity.Window == owned,
                        "Settings move-loop integration begins with a fake cue for the real owned hidden HWND");
                    bool resizeBeginCalled = false, hiddenBeforeBaseLoop = false, resizeEndCalled = false, hiddenDuringBaseExit = false;
                    form.ResizeBegin += delegate { resizeBeginCalled = true; hiddenBeforeBaseLoop = !indicator.Visible; };
                    form.ResizeEnd += delegate {
                        resizeEndCalled = true; indicator.Update(1, "Original", false); hiddenDuringBaseExit = !indicator.Visible;
                    };
                    SendMessage(owned, 0x0231, IntPtr.Zero, IntPtr.Zero);
                    check(resizeBeginCalled && hiddenBeforeBaseLoop && !indicator.Visible && surfaces[0].Disposals == 1,
                        "Real Settings WM_ENTERSIZEMOVE hides synchronously before its base ResizeBegin/modal-loop event");
                    Rectangle initial = form.Bounds;
                    for (int step = 1; step <= 3; step++) {
                        form.Bounds = new Rectangle(initial.X + step * 45, initial.Y + step * 30, initial.Width, initial.Height);
                        target.Frame = form.Bounds; now += 500;
                        // A delayed native END plus arbitrarily late timer ticks cannot
                        // override Settings' own still-open modal move/size notification.
                        eventCallback(new FocusIndicatorChange { Window = owned, Kind = FocusIndicatorChangeKind.MoveEnd });
                        indicator.Update(1, "Original", false); now += 500; indicator.Update(1, "Original", false);
                        check(!indicator.Visible && surfaces.Count == 1,
                            "Settings direct move-loop state prevents stale END/timer recreation at owned drag step " + step);
                    }
                    SendMessage(owned, 0x0232, IntPtr.Zero, IntPtr.Zero);
                    check(resizeEndCalled && hiddenDuringBaseExit && !indicator.Visible,
                        "Settings base ResizeEnd still sees suppression until direct WM_EXITSIZEMOVE forwarding completes");
                    indicator.Update(1, "Original", false); now += 119; indicator.Update(1, "Original", false);
                    check(!indicator.Visible && surfaces.Count == 1, "Settings drag exit waits for verified stable geometry before restoring its cue");
                    now++; indicator.Update(1, "Original", false);
                    check(indicator.Visible && surfaces.Count == 2 && surfaces[1].Last.Bounds == form.Bounds &&
                        surfaces[1].Last.Target.Frame == target.Frame && form.Bounds != initial,
                        "Settings direct drag exit restores only at the owned HWND's settled final position (form=" + form.Bounds +
                        "; target=" + target.Frame + "; visible=" + indicator.Visible + "; surfaces=" + surfaces.Count +
                        "; last=" + (surfaces.Count > 1 ? surfaces[1].Last.Bounds.ToString() : "none") + "; error=" + indicator.Error + ")");
                    check(!form.Visible && !controller.DesktopRuntime && Ui.Accent == originalAccent && controller.History.Peek == null,
                        "Settings move-loop integration shows no windows, installs no desktop hooks and changes no saved preferences");
                }
            } finally {
                if (indicator != null) indicator.Dispose();
                controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                foreach (GlobalShortcut binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
            }
        }
    }
}
