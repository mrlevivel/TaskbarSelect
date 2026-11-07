using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class NavigationShortcutPolicyTests
    {
        internal static void Run(Action<bool, string> check)
        {
            DefaultsAndKeys(check);
            PreserveExistingChoices(check);
            Persistence(check);
            Validation(check);
            RegistrationTransactions(check);
            SettingsLayout(check);
        }

        static int[] All(Settings settings)
        {
            return new[] { settings.UndoMoveShortcut }.Concat(settings.MonitorMoveShortcuts)
                .Concat(new[] { settings.CloseWindowShortcut, settings.SearchAppsShortcut })
                .Concat(NavigationShortcutPolicy.Choices(settings)).ToArray();
        }

        static void Set(Settings settings, int slot, int value)
        {
            if (slot == 0) settings.UndoMoveShortcut = value;
            else if (slot < 5) settings.MonitorMoveShortcuts[slot - 1] = value;
            else if (slot == 5) settings.CloseWindowShortcut = value;
            else if (slot == 6) settings.SearchAppsShortcut = value;
            else if (slot < 11) settings.FocusWindowShortcuts[slot - 7] = value;
            else settings.TileWindowShortcut = value;
        }

        static bool Accepts(Settings settings)
        {
            try { MonitorShortcutPolicy.Validate(settings); return true; }
            catch (InvalidOperationException) { return false; }
        }

        static void DefaultsAndKeys(Action<bool, string> check)
        {
            int[] defaults = NavigationShortcutPolicy.Defaults();
            var settings = new Settings();
            Keys modifiers = Keys.Control | Keys.Shift | Keys.Alt;
            check(defaults.SequenceEqual(new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down }.Select(key => (int)(modifiers | key))),
                "Directional focus defaults use the proposed Ctrl + Shift + Alt arrows in left/right/up/down order");
            check(settings.FocusWindowShortcuts.SequenceEqual(defaults) && settings.TileWindowShortcut == (int)(modifiers | Keys.Space),
                "Fresh settings contain all five navigation defaults");
            check(Accepts(settings) && All(settings).Where(chord => chord != 0).Distinct().Count() == 12,
                "The twelve defaults remain valid and distinct, including every released shortcut");
            check(!Object.ReferenceEquals(defaults, NavigationShortcutPolicy.Defaults()) && !Object.ReferenceEquals(settings.FocusWindowShortcuts, NavigationShortcutPolicy.Choices(settings)),
                "Default and runtime choice arrays are independent copies");
            foreach (Keys key in new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down }) {
                check(GlobalShortcut.AvailableKeys().Count(candidate => candidate == key) == 1 && GlobalShortcut.KeyName(key).EndsWith(" Arrow"),
                    "Each arrow is supported once and has a readable shortcut label: " + key);
                foreach (Keys chordModifiers in new[] { Keys.Control | Keys.Shift, Keys.Control | Keys.Alt, Keys.Alt | Keys.Shift, modifiers })
                    check(GlobalShortcut.IsValid(chordModifiers | key), "Arrows accept the existing modifier policy: " + chordModifiers + "/" + key);
                foreach (Keys chordModifiers in new[] { Keys.None, Keys.Control, Keys.Alt, Keys.Shift })
                    check(!GlobalShortcut.IsValid(chordModifiers | key), "Plain or single-modifier arrows cannot claim ordinary navigation: " + chordModifiers + "/" + key);
                long message = ((long)key << 16) | GlobalShortcut.Modifiers(modifiers | key);
                check(GlobalShortcut.MatchesMessage(71, modifiers | key, new IntPtr(71), new IntPtr(message)) &&
                    !GlobalShortcut.MatchesMessage(71, modifiers | key, new IntPtr(70), new IntPtr(message)),
                    "Only the active arrow registration identifier dispatches navigation: " + key);
            }
            check(!GlobalShortcut.IsValid(modifiers | Keys.F12) && !GlobalShortcut.IsValid(modifiers | Keys.LWin),
                "Arrow support does not change the reserved F12 and Windows-key restrictions");
        }

        static void PreserveExistingChoices(Action<bool, string> check)
        {
            int[] introduced = NavigationShortcutPolicy.Choices(new Settings());
            for (int existing = 0; existing < 7; existing++)
            for (int added = 0; added < introduced.Length; added++) {
                var settings = new Settings(); Set(settings, existing, introduced[added]);
                int[] before = All(settings).Take(7).ToArray();
                MonitorShortcutPolicy.Normalize(settings);
                check(All(settings).Take(7).SequenceEqual(before) && NavigationShortcutPolicy.Choices(settings)[added] == 0 && Accepts(settings),
                    "A new focus/tiling default never steals an existing Undo, monitor, Close or Search binding: " + existing + "/" + added);
                int[] normalized = All(settings); MonitorShortcutPolicy.Normalize(settings);
                check(All(settings).SequenceEqual(normalized), "Navigation collision migration is idempotent: " + existing + "/" + added);
            }
            var off = new Settings { FocusWindowShortcuts = new int[4], TileWindowShortcut = 0 };
            MonitorShortcutPolicy.Normalize(off);
            check(NavigationShortcutPolicy.Choices(off).All(value => value == 0) && Accepts(off), "Every explicit navigation Off choice remains Off");
            int custom = (int)(Keys.Control | Keys.Alt | Keys.F20);
            int[] supplied = { 0, custom };
            var partial = new Settings { FocusWindowShortcuts = supplied };
            MonitorShortcutPolicy.Normalize(partial);
            check(partial.FocusWindowShortcuts.SequenceEqual(new[] { 0, custom, introduced[2], introduced[3] }) && supplied.SequenceEqual(new[] { 0, custom }),
                "Partial focus arrays retain supplied choices and fill missing defaults without mutating the caller");
            var corrupt = new Settings { FocusWindowShortcuts = new[] { -1, custom, custom, (int)Keys.Down, introduced[0] }, TileWindowShortcut = custom };
            MonitorShortcutPolicy.Normalize(corrupt);
            check(corrupt.FocusWindowShortcuts.SequenceEqual(new[] { 0, custom, 0, 0 }) && corrupt.TileWindowShortcut == 0 && Accepts(corrupt),
                "Malformed, oversized and duplicate new slots normalize to editable settings, with focus preceding tile");
            var missing = new Settings { FocusWindowShortcuts = null };
            MonitorShortcutPolicy.Normalize(missing);
            check(missing.FocusWindowShortcuts.SequenceEqual(NavigationShortcutPolicy.Defaults()), "A missing focus array receives defaults");
        }

        static void Persistence(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.NavigationPersistence." + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(directory);
            try {
                var serializer = new JavaScriptSerializer();
                Settings legacy = serializer.Deserialize<Settings>("{\"UndoMoveShortcut\":" + (int)(Keys.Control | Keys.Alt | Keys.Back) +
                    ",\"SearchAppsShortcut\":" + (int)(Keys.Control | Keys.Alt | Keys.Shift | Keys.Left) + "}");
                MonitorShortcutPolicy.Normalize(legacy);
                check(legacy.UndoMoveShortcut == (int)(Keys.Control | Keys.Alt | Keys.Back) && legacy.SearchAppsShortcut == (int)(Keys.Control | Keys.Alt | Keys.Shift | Keys.Left) &&
                    legacy.FocusWindowShortcuts[0] == 0 && legacy.FocusWindowShortcuts.Skip(1).SequenceEqual(NavigationShortcutPolicy.Defaults().Skip(1)),
                    "Old JSON preserves released explicit bindings and disables only the colliding new default");
                var custom = new Settings { FocusWindowShortcuts = new[] { 0, (int)(Keys.Control | Keys.Alt | Keys.F18), 0, (int)(Keys.Control | Keys.Alt | Keys.F19) }, TileWindowShortcut = 0 };
                int[] expected = All(custom); Settings copy = custom.Copy();
                check(All(copy).SequenceEqual(expected) && !Object.ReferenceEquals(copy.FocusWindowShortcuts, custom.FocusWindowShortcuts),
                    "Copy preserves new shortcut values with an independent focus array");
                copy.WindowShelfActivationHeight = 54; copy.WindowShelfHoverDelayMs = 750;
                MonitorShortcutPolicy.Validate(copy); Program.Save(copy, path); Settings loaded = Program.Load(path);
                check(All(loaded).SequenceEqual(expected) && loaded.WindowShelfActivationHeight == 54 && loaded.WindowShelfHoverDelayMs == 750,
                    "An unrelated settings save reloads all twelve choices, including explicit navigation Off values");
                check(All(custom).SequenceEqual(expected) && custom.WindowShelfActivationHeight != 54,
                    "Saving a changed copy does not mutate the source profile");
                File.WriteAllText(path, "{\"FocusWindowShortcuts\":[0],\"TileWindowShortcut\":0}");
                loaded = Program.Load(path);
                check(loaded.FocusWindowShortcuts[0] == 0 && loaded.FocusWindowShortcuts.Skip(1).SequenceEqual(NavigationShortcutPolicy.Defaults().Skip(1)) && loaded.TileWindowShortcut == 0,
                    "Load distinguishes explicit Off choices from absent partial-array slots");
            } finally {
                foreach (string file in new[] { path, path + ".tmp" }) if (File.Exists(file)) File.Delete(file);
                Directory.Delete(directory);
            }
        }

        static void Validation(Action<bool, string> check)
        {
            int[] defaults = All(new Settings());
            for (int first = 0; first < defaults.Length; first++)
            for (int second = first + 1; second < defaults.Length; second++) {
                var duplicate = new Settings(); Set(duplicate, second, defaults[first]); int[] before = All(duplicate);
                check(!Accepts(duplicate) && All(duplicate).SequenceEqual(before),
                    "All twelve action pairs reject interactive duplicates without rewriting preferences: " + first + "/" + second);
            }
            for (int slot = 7; slot < 12; slot++) {
                foreach (int value in new[] { -1, (int)Keys.Left, (int)(Keys.Control | Keys.Space), (int)(Keys.Control | Keys.Shift | Keys.F12) }) {
                    var invalid = new Settings(); Set(invalid, slot, value);
                    check(!Accepts(invalid), "Unsupported navigation choices fail before native registration: " + slot + "/" + value);
                }
                var off = new Settings(); Set(off, slot, 0);
                check(Accepts(off), "Each navigation action can independently be disabled: " + slot);
            }
            foreach (int size in new[] { 0, 3, 5 })
                check(!Accepts(new Settings { FocusWindowShortcuts = new int[size] }), "Interactive save rejects malformed focus arrays: " + size);
            check(!Accepts(new Settings { FocusWindowShortcuts = null }), "Interactive save rejects a missing focus array before acquiring any hotkey");
        }

        static int[] Current(GlobalShortcut[] bindings)
        { return bindings.Select(binding => (int)binding.Current).ToArray(); }

        static int[] UnusedChords()
        {
            var found = new List<int>();
            using (var probe = new GlobalShortcut()) {
                for (Keys key = Keys.F13; key <= Keys.F24; key++) {
                    Keys chord = Keys.Control | Keys.Shift | Keys.Alt | key;
                    try { probe.Apply(chord, delegate { }); found.Add((int)chord); }
                    catch (InvalidOperationException) { }
                    finally { probe.Apply(Keys.None, delegate { }); }
                }
            }
            return found.ToArray();
        }

        static void RegistrationTransactions(Action<bool, string> check)
        {
            int[] pool = UnusedChords();
            check(pool.Length >= 10, "At least ten unused unusual native fixture chords are available for five-binding rollback");
            if (pool.Length < 10) return;
            var bindings = Enumerable.Range(0, 5).Select(i => new GlobalShortcut()).ToArray();
            try {
                int[] previous = pool.Take(5).ToArray(); int commits = 0;
                MonitorShortcutPolicy.Apply(bindings, previous, delegate { commits++; });
                using (var blocker = new GlobalShortcut()) {
                    blocker.Apply((Keys)pool[9], delegate { });
                    int[] proposed = pool.Skip(5).Take(5).ToArray(); bool rejected = false;
                    try { MonitorShortcutPolicy.Apply(bindings, proposed, delegate { commits++; }); }
                    catch (InvalidOperationException error) { rejected = error.Message.Contains("already registered"); }
                    check(rejected && commits == 1 && Current(bindings).SequenceEqual(previous),
                        "A late tiling conflict rolls back four tentative focus registrations and never reaches persistence");
                    int[] failedStartup = previous.ToArray(); failedStartup[4] = pool[9];
                    bindings[4].Apply(Keys.None, delegate { }); string startupError = null;
                    try { bindings[4].Apply((Keys)failedStartup[4], delegate { }); }
                    catch (InvalidOperationException failure) { startupError = failure.Message; }
                    int[] runtime = Current(bindings);
                    int[] fallback = MonitorShortcutPolicy.RegistrationChoices(failedStartup, failedStartup, runtime, -1);
                    MonitorShortcutPolicy.Apply(bindings, fallback, delegate { commits++; });
                    check(commits == 2 && Current(bindings).SequenceEqual(runtime) &&
                        MonitorShortcutPolicy.RegistrationError(failedStartup[4], runtime[4], startupError) == startupError,
                        "An unchanged unavailable tiling chord does not block unrelated settings or clear its registration error");
                    int[] retry = MonitorShortcutPolicy.RegistrationChoices(failedStartup, failedStartup, runtime, 4);
                    bool retryRejected = false;
                    try { MonitorShortcutPolicy.Apply(bindings, retry, delegate { commits++; }); }
                    catch (InvalidOperationException) { retryRejected = true; }
                    check(retryRejected && commits == 2 && Current(bindings).SequenceEqual(runtime),
                        "An explicit still-blocked tiling retry leaves every previous focus registration intact");
                    blocker.Apply(Keys.None, delegate { });
                    MonitorShortcutPolicy.Apply(bindings, retry, delegate { commits++; });
                    check(commits == 3 && Current(bindings).SequenceEqual(failedStartup) &&
                        MonitorShortcutPolicy.RegistrationError(failedStartup[4], (int)bindings[4].Current, startupError) == null,
                        "Releasing a blocker allows explicit tiling retry and clears its runtime error");
                    int[] changed = pool.Skip(5).Take(4).Concat(new[] { previous[4] }).ToArray();
                    var persistenceFailure = new IOException("owned navigation persistence failure"); bool propagated = false;
                    try { MonitorShortcutPolicy.Apply(bindings, changed, delegate { throw persistenceFailure; }); }
                    catch (IOException failure) { propagated = Object.ReferenceEquals(failure, persistenceFailure); }
                    check(propagated && Current(bindings).SequenceEqual(failedStartup),
                        "A save failure after acquiring new focus and tiling chords preserves all five previous registrations");
                    using (var probe = new GlobalShortcut()) {
                        foreach (int chord in changed) {
                            bool released = false;
                            try { probe.Apply((Keys)chord, delegate { }); released = true; }
                            catch (InvalidOperationException) { }
                            check(released, "Rollback releases tentative navigation chord for reuse: " + chord);
                            probe.Apply(Keys.None, delegate { });
                        }
                    }
                }
            } finally { foreach (GlobalShortcut binding in bindings) binding.Dispose(); }
        }

        static IEnumerable<Control> Controls(Control root)
        {
            foreach (Control child in root.Controls) {
                yield return child;
                foreach (Control descendant in Controls(child)) yield return descendant;
            }
        }
        static void Realize(Control root)
        {
            if (root.Handle == IntPtr.Zero) throw new InvalidOperationException("The owned hidden layout handle could not be created.");
            foreach (Control child in root.Controls) Realize(child);
        }
        static void Layout(Control root)
        {
            root.PerformLayout();
            foreach (Control child in root.Controls) Layout(child);
        }
        static Rectangle BoundsIn(Control control, Control ancestor)
        {
            Rectangle bounds = control.Bounds;
            for (Control parent = control.Parent; parent != ancestor; parent = parent.Parent) {
                if (parent == null) throw new InvalidOperationException("The owned control is outside its expected layout.");
                bounds.Offset(parent.Location);
            }
            return bounds;
        }

        static void SettingsLayout(Action<bool, string> check)
        {
            const string conflict = "Ctrl + Alt + Shift + Left Arrow is already registered by another app.";
            var controller = new Controller(false) { Settings = new Settings(), Enabled = false };
            try {
                int[] fixtureChords = UnusedChords();
                if (fixtureChords.Length > 0) {
                    controller.Settings.FocusWindowShortcuts[0] = fixtureChords[0];
                    controller.NavigationShortcuts[0].Apply((Keys)fixtureChords[0], delegate { });
                }
                foreach (int dpi in new[] { 96, 192 })
                using (var form = new SettingsForm(controller)) {
                    TabControl tabs = Controls(form).OfType<TabControl>().Single();
                    TabPage page = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Shortcuts");
                    tabs.SelectedTab = page; form.Size = form.MinimumSize; Realize(form); Layout(form);
                    float scale = dpi / (float)form.DeviceDpi;
                    form.AutoScaleMode = AutoScaleMode.None;
                    if (scale != 1) form.Scale(new SizeF(scale, scale));
                    form.Font = new Font("Segoe UI", 10 * dpi / 96f); form.Size = form.MinimumSize; Layout(form);
                    Control content = page.Controls[0];
                    string[] names = { "Focus left shortcut", "Focus right shortcut", "Focus up shortcut", "Focus down shortcut", "Tiling picker shortcut" };
                    var editors = Controls(content).OfType<Button>().Where(button => names.Any(name => button.AccessibleName == "Change " + name.ToLowerInvariant())).ToArray();
                    var statuses = Controls(content).OfType<Label>().Where(label => names.Any(name => label.AccessibleName == name + " status")).ToArray();
                    check(editors.Length == 5 && statuses.Length == 5 && editors.All(button => button.Width > 0 && button.Height >= button.MinimumSize.Height && content.ClientRectangle.Contains(BoundsIn(button, content))),
                        "All five new shortcut editors fit their real hidden minimum-size page at layout scale " + dpi);
                    check(tabs.TabPages.Count == 5 && tabs.GetTabRect(4).Right <= tabs.ClientSize.Width,
                        "The consolidated Shortcuts header remains reachable at minimum width and layout scale " + dpi);
                    if (fixtureChords.Length > 0) {
                        Label focusStatus = statuses.Single(label => label.AccessibleName == "Focus left shortcut status");
                        check(focusStatus.Text == "Shortcut active | Paused", "An active navigation binding reports Pause all in its accessible status at layout scale " + dpi);
                        controller.Enabled = true; controller.MovementPaused = false; form.UpdateStatus();
                        check(focusStatus.Text == "Shortcut active", "An enabled focus binding reports active at layout scale " + dpi);
                        controller.MovementPaused = true; form.UpdateStatus();
                        check(focusStatus.Text == "Shortcut active | Paused", "Pause moves is visible for directional focus at layout scale " + dpi);
                        controller.Enabled = false; controller.MovementPaused = false;
                    }
                    controller.NavigationShortcutErrors[0] = conflict; form.UpdateStatus(); Layout(form);
                    check(statuses.Any(label => label.Text == conflict) && statuses.All(label => label.Height >= TextRenderer.MeasureText(label.Text, label.Font,
                        new Size(label.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height),
                        "Navigation registration errors and status text fit their actual rows at layout scale " + dpi);
                    // Undocking restores a control's remembered pre-dock size.
                    // Preserve the native viewport width before shortening only
                    // its height, as a real user resize would do.
                    int viewportWidth = tabs.Width;
                    tabs.Dock = DockStyle.None; tabs.MaximumSize = new Size(viewportWidth, (int)Math.Ceiling(300 * dpi / 96f));
                    tabs.Size = tabs.MaximumSize; Layout(form);
                    check(page.AutoScroll && page.VerticalScroll.Visible && content.Height > page.ClientSize.Height,
                        "A shorter native navigation viewport scrolls instead of clipping its five editors at layout scale " + dpi);
                    foreach (Control control in editors.Cast<Control>().Concat(statuses)) {
                        page.ScrollControlIntoView(control); Layout(page);
                        check(page.ClientRectangle.Contains(BoundsIn(control, page)),
                            "Every complete navigation editor and status row can be scrolled into view at layout scale " + dpi + ": " + control.AccessibleName +
                            ", bounds=" + BoundsIn(control, page) + ", viewport=" + page.ClientRectangle + ", scroll=" + page.AutoScrollPosition);
                    }
                    check(!form.Visible, "Navigation layout tests never show or activate the settings window");
                    controller.NavigationShortcutErrors[0] = null;
                }
            } finally {
                controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                foreach (GlobalShortcut binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
            }
        }
    }
}
