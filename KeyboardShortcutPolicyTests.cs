using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class KeyboardShortcutPolicyTests
    {
        static readonly int CloseDefault = (int)(Keys.Control | Keys.Shift | Keys.W);
        static readonly int SearchDefault = (int)(Keys.Control | Keys.Shift | Keys.Space);

        internal static void Run(Action<bool, string> check)
        {
            DefaultsAndSpace(check);
            ExistingBindingPriority(check);
            NewBindingNormalization(check);
            Validation(check);
            AdditionalMonitorBindings(check);
            Persistence(check);
            RunWindowStateBindings(check);
            EmptyTransaction(check);
            RegistrationFallback(check);
            NativeTransactions(check);
        }

        // Pure policy and temporary-file checks: this entry point never creates
        // a GlobalShortcut, registers a hotkey, or reads the user's profile.
        internal static void RunWindowStateBindings(Action<bool, string> check)
        {
            int minimize = (int)(Keys.Control | Keys.Shift | Keys.M);
            int pin = (int)(Keys.Control | Keys.Shift | Keys.P);
            var defaults = new Settings();
            check(defaults.MinimizeWindowShortcut == minimize && defaults.PinWindowShortcut == pin,
                "Minimize and always-on-top default to Ctrl + Shift + M and Ctrl + Shift + P");
            check(ActionShortcutPolicy.Count == 4 && ActionShortcutPolicy.Choices(defaults).SequenceEqual(new[] { CloseDefault, SearchDefault, minimize, pin }) &&
                ActionShortcutPolicy.Defaults().SequenceEqual(ActionShortcutPolicy.Choices(defaults)),
                "Action slots preserve Close zero and Search one, with Minimize two and always-on-top three");
            check(ActionShortcutPolicy.Labels.Length == 4 && ActionShortcutPolicy.Labels.Distinct().Count() == 4 &&
                !Object.ReferenceEquals(ActionShortcutPolicy.Defaults(), ActionShortcutPolicy.Defaults()) &&
                !Object.ReferenceEquals(ActionShortcutPolicy.Choices(defaults), ActionShortcutPolicy.Choices(defaults)),
                "Action labels cover all four slots and callers receive independent choice arrays");
            for (int slot = 0; slot < ActionShortcutPolicy.Count; slot++) {
                var settings = new Settings(); int[] before = ActionShortcutPolicy.Choices(settings);
                ActionShortcutPolicy.Set(settings, slot, 0); before[slot] = 0;
                check(ActionShortcutPolicy.Choices(settings).SequenceEqual(before), "The shared action editor changes only its requested slot: " + slot);
            }
            foreach (int slot in new[] { -1, ActionShortcutPolicy.Count }) {
                bool rejected = false;
                try { ActionShortcutPolicy.Set(new Settings(), slot, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
                check(rejected, "An invalid action editor slot is rejected: " + slot);
            }

            for (int earlier = 0; earlier < 12; earlier++)
            for (int added = 0; added < 2; added++) {
                var settings = new Settings(); SetWindowStateChord(settings, earlier, added == 0 ? minimize : pin);
                int[] previous = WindowStateChords(settings).Take(12).ToArray();
                check(Rejects(settings), "A duplicate new action is rejected before registration: " + earlier + "/" + added);
                MonitorShortcutPolicy.Normalize(settings);
                check(WindowStateChords(settings).Take(12).SequenceEqual(previous) &&
                    (added == 0 ? settings.MinimizeWindowShortcut : settings.PinWindowShortcut) == 0 &&
                    (added == 0 ? settings.PinWindowShortcut : settings.MinimizeWindowShortcut) == (added == 0 ? pin : minimize) && Accepts(settings),
                    "Every existing binding, including focus and layout, keeps its chord ahead of a new default: " + earlier + "/" + added);
                int[] normalized = WindowStateChords(settings); MonitorShortcutPolicy.Normalize(settings);
                check(WindowStateChords(settings).SequenceEqual(normalized), "New action collision normalization is idempotent: " + earlier + "/" + added);
            }
            var shared = new Settings { MinimizeWindowShortcut = minimize, PinWindowShortcut = minimize };
            MonitorShortcutPolicy.Normalize(shared);
            check(shared.MinimizeWindowShortcut == minimize && shared.PinWindowShortcut == 0 && Accepts(shared),
                "A collision between both new actions keeps Minimize and disables always-on-top deterministically");
            for (int slot = 12; slot < 14; slot++) {
                foreach (int chord in new[] { -1, (int)Keys.M, (int)(Keys.Control | Keys.P), (int)(Keys.Control | Keys.Shift),
                    (int)(Keys.Control | Keys.Shift | Keys.F12), (int)(Keys.Control | Keys.Shift | Keys.LWin), minimize | 0x08000000 }) {
                    var settings = new Settings(); SetWindowStateChord(settings, slot, chord); int[] previous = WindowStateChords(settings).Take(12).ToArray();
                    check(Rejects(settings), "Invalid new state-action chords are rejected before normalization: " + slot + "/" + chord);
                    MonitorShortcutPolicy.Normalize(settings);
                    check(WindowStateChords(settings)[slot] == 0 && WindowStateChords(settings).Take(12).SequenceEqual(previous) && Accepts(settings),
                        "Malformed state-action choices are disabled without changing existing shortcuts: " + slot + "/" + chord);
                }
            }
            int[] fresh = WindowStateChords(new Settings());
            check(fresh.Length == 14 && fresh.Distinct().Count() == 14 && Accepts(new Settings()), "All fourteen default shortcuts are valid and distinct");
            for (int first = 0; first < fresh.Length; first++)
            for (int second = first + 1; second < fresh.Length; second++) {
                var duplicate = new Settings(); SetWindowStateChord(duplicate, second, fresh[first]); int[] before = WindowStateChords(duplicate);
                check(Rejects(duplicate) && WindowStateChords(duplicate).SequenceEqual(before),
                    "Interactive validation rejects duplicates across all fourteen actions without rewriting them: " + first + "/" + second);
            }
            foreach (int slot in new[] { 12, 13 }) {
                var off = new Settings(); SetWindowStateChord(off, slot, 0); MonitorShortcutPolicy.Normalize(off);
                check(WindowStateChords(off)[slot] == 0 && Accepts(off), "Each new action supports an explicit Off value: " + slot);
            }

            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.WindowStateShortcutPolicyTests." + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(directory);
            try {
                var serializer = new JavaScriptSerializer();
                var absent = serializer.Deserialize<Settings>("{}");
                check(absent.MinimizeWindowShortcut == minimize && absent.PinWindowShortcut == pin,
                    "Missing new JSON fields receive state-action defaults");
                var explicitOff = serializer.Deserialize<Settings>("{\"MinimizeWindowShortcut\":0,\"PinWindowShortcut\":0}");
                MonitorShortcutPolicy.Normalize(explicitOff);
                check(explicitOff.MinimizeWindowShortcut == 0 && explicitOff.PinWindowShortcut == 0,
                    "Explicit JSON Off choices override both new defaults");
                for (int earlier = 0; earlier < 12; earlier++)
                for (int added = 0; added < 2; added++) {
                    var source = new Settings(); SetWindowStateChord(source, earlier, added == 0 ? minimize : pin);
                    var legacy = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(source));
                    legacy.Remove("MinimizeWindowShortcut"); legacy.Remove("PinWindowShortcut");
                    File.WriteAllText(path, serializer.Serialize(legacy)); Settings loaded = Program.Load(path);
                    check(WindowStateChords(loaded).Take(12).SequenceEqual(WindowStateChords(source).Take(12)) &&
                        WindowStateChords(loaded)[12 + added] == 0 && Accepts(loaded),
                        "Loading a released profile preserves every existing binding before adding defaults: " + earlier + "/" + added);
                }
                int customMinimize = (int)(Keys.Control | Keys.Alt | Keys.F20), customPin = (int)(Keys.Alt | Keys.Shift | Keys.F21);
                foreach (int[] choices in new[] { new[] { 0, 0 }, new[] { customMinimize, customPin }, new[] { 0, customPin }, new[] { customMinimize, 0 } }) {
                    var source = new Settings { MinimizeWindowShortcut = choices[0], PinWindowShortcut = choices[1], AccentPreset = "Ruby",
                        WindowShelfActivationHeight = 1, WindowShelfHoverDelayMs = 575, PersistentAdministratorSupport = true,
                        FocusIndicatorStyle = (int)FocusIndicatorMode.InsetEdge, FocusIndicatorLighting = (int)FocusLightingMode.None };
                    source.HiddenIdentities.Add("neutral-display"); source.WindowShelfIdentities.Add("neutral-display");
                    source.MonitorAliases.Add("neutral-display", "Demo display");
                    int[] expected = WindowStateChords(source); Settings changed = source.Copy();
                    var history = new SettingsHistory(); history.Push(source, null, "Window shortcut");
                    source.MinimizeWindowShortcut = source.PinWindowShortcut = 0;
                    check(WindowStateChords(changed).SequenceEqual(expected) && WindowStateChords(history.Peek.Settings).SequenceEqual(expected),
                        "Copy and settings history independently retain custom state actions and Off: " + choices[0] + "/" + choices[1]);
                    Program.Save(changed, path); Settings loaded = Program.Load(path);
                    check(WindowStateChords(loaded).SequenceEqual(expected) && loaded.AccentPreset == "Ruby" && loaded.WindowShelfActivationHeight == 1 &&
                        loaded.WindowShelfHoverDelayMs == 575 && loaded.PersistentAdministratorSupport && loaded.FocusIndicatorStyle == (int)FocusIndicatorMode.InsetEdge &&
                        loaded.FocusIndicatorLighting == (int)FocusLightingMode.None && loaded.HiddenIdentities.SequenceEqual(new[] { "neutral-display" }) &&
                        loaded.WindowShelfIdentities.SequenceEqual(new[] { "neutral-display" }) && loaded.MonitorAliases["neutral-display"] == "Demo display",
                        "Saving new shortcuts preserves appearance, monitor, activation and approval preferences: " + choices[0] + "/" + choices[1]);
                }
            } finally {
                foreach (string file in new[] { path, path + ".tmp" }) if (File.Exists(file)) File.Delete(file);
                Directory.Delete(directory);
            }
            var requested = ActionShortcutPolicy.Choices(new Settings()); var active = requested.ToArray(); active[2] = active[3] = 0;
            check(MonitorShortcutPolicy.RegistrationChoices(requested, requested, active, -1).SequenceEqual(active),
                "Unavailable Minimize and always-on-top remain Off during an unrelated settings save");
            for (int slot = 2; slot < 4; slot++) {
                int[] expected = active.ToArray(); expected[slot] = requested[slot];
                check(MonitorShortcutPolicy.RegistrationChoices(requested, requested, active, slot).SequenceEqual(expected),
                    "An explicit retry affects only its new action: " + slot);
            }
        }

        static int[] WindowStateChords(Settings settings)
        {
            return Chords(settings).Concat(NavigationShortcutPolicy.Choices(settings))
                .Concat(new[] { settings.MinimizeWindowShortcut, settings.PinWindowShortcut }).ToArray();
        }
        static void SetWindowStateChord(Settings settings, int slot, int chord)
        {
            if (slot < 5) SetExisting(settings, slot, chord);
            else if (slot == 5) settings.CloseWindowShortcut = chord;
            else if (slot == 6) settings.SearchAppsShortcut = chord;
            else if (slot < 11) settings.FocusWindowShortcuts[slot - 7] = chord;
            else if (slot == 11) settings.TileWindowShortcut = chord;
            else if (slot == 12) settings.MinimizeWindowShortcut = chord;
            else if (slot == 13) settings.PinWindowShortcut = chord;
            else throw new ArgumentOutOfRangeException("slot");
        }

        static int[] Chords(Settings settings)
        {
            return new[] { settings.UndoMoveShortcut }.Concat(settings.MonitorMoveShortcuts)
                .Concat(new[] { settings.CloseWindowShortcut, settings.SearchAppsShortcut }).ToArray();
        }

        static Settings WithChords(int[] choices)
        {
            return new Settings {
                UndoMoveShortcut = choices[0], MonitorMoveShortcuts = choices.Skip(1).Take(4).ToArray(),
                CloseWindowShortcut = choices[5], SearchAppsShortcut = choices[6]
            };
        }

        static void SetExisting(Settings settings, int action, int chord)
        {
            if (action == 0) settings.UndoMoveShortcut = chord;
            else settings.MonitorMoveShortcuts[action - 1] = chord;
        }

        static bool Rejects(Settings settings)
        {
            try { MonitorShortcutPolicy.Validate(settings); return false; }
            catch (InvalidOperationException) { return true; }
        }

        static bool Accepts(Settings settings)
        {
            return !Rejects(settings);
        }

        static void DefaultsAndSpace(Action<bool, string> check)
        {
            var settings = new Settings();
            check(settings.CloseWindowShortcut == CloseDefault, "Close defaults to Ctrl + Shift + W");
            check(settings.SearchAppsShortcut == SearchDefault, "Search defaults to Ctrl + Shift + Space");
            check(settings.UndoMoveShortcut == (int)GlobalShortcut.Default && settings.MonitorMoveShortcuts.SequenceEqual(MonitorShortcutPolicy.Defaults()),
                "Adding keyboard actions preserves released Undo and monitor defaults");
            check(Accepts(settings), "All seven fresh default actions are valid and distinct");
            var keys = GlobalShortcut.AvailableKeys();
            check(keys.Count(k => k == Keys.Space) == 1, "Space is available exactly once in the supported key list");
            check(GlobalShortcut.KeyName(Keys.Space) == "Space", "Space has its readable dialog label");
            check(GlobalShortcut.Format((Keys)SearchDefault) == "Ctrl + Shift + Space", "The search chord has its readable shortcut label");
            check(GlobalShortcut.Format(Keys.None) == "Off", "Disabled actions retain the Off label");
            foreach (Keys modifiers in new[] { Keys.Control | Keys.Shift, Keys.Control | Keys.Alt, Keys.Alt | Keys.Shift, Keys.Control | Keys.Alt | Keys.Shift })
                check(GlobalShortcut.IsValid(modifiers | Keys.Space), "Space supports the existing two- or three-modifier policy: " + modifiers);
            foreach (Keys modifiers in new[] { Keys.None, Keys.Control, Keys.Alt, Keys.Shift })
                check(!GlobalShortcut.IsValid(modifiers | Keys.Space), "Space still requires two modifiers: " + modifiers);
            check(!GlobalShortcut.IsValid(Keys.Control | Keys.Shift | Keys.LWin) &&
                !GlobalShortcut.IsValid((Keys)SearchDefault | (Keys)0x08000000), "Space does not expand support to Windows keys or unknown modifier flags");
            check(!GlobalShortcut.IsValid(Keys.Control | Keys.Shift | Keys.F12), "Reserved F12 remains unavailable");
            const int id = 53;
            uint modifiersExpected = 6;
            var data = new IntPtr(((int)Keys.Space << 16) | (int)modifiersExpected);
            check(GlobalShortcut.Modifiers((Keys)SearchDefault) == modifiersExpected, "Search maps to the existing native Control and Shift modifier bits");
            check(GlobalShortcut.MatchesMessage(id, (Keys)SearchDefault, new IntPtr(id), data), "A Space hotkey message matches the exact active search chord");
            check(!GlobalShortcut.MatchesMessage(id, (Keys)SearchDefault, new IntPtr(id + 1), data), "A stale hotkey identifier cannot dispatch search");
            check(!GlobalShortcut.MatchesMessage(id, (Keys)SearchDefault, new IntPtr(id), new IntPtr(((int)Keys.Space << 16) | 3)),
                "A Space hotkey message with different modifiers cannot dispatch search");
            check(!GlobalShortcut.MatchesMessage(id, (Keys)SearchDefault, new IntPtr(id), new IntPtr(((int)Keys.W << 16) | 6)),
                "A close hotkey message cannot dispatch the search action");
            check(!GlobalShortcut.MatchesMessage(0, Keys.None, IntPtr.Zero, IntPtr.Zero), "An Off binding does not dispatch a message");
        }

        static void ExistingBindingPriority(Action<bool, string> check)
        {
            for (int action = 0; action < 5; action++) {
                for (int added = 0; added < 2; added++) {
                    var settings = new Settings();
                    SetExisting(settings, action, added == 0 ? CloseDefault : SearchDefault);
                    int[] previous = Chords(settings).Take(5).ToArray();
                    MonitorShortcutPolicy.Normalize(settings);
                    string scenario = "existing action " + action + ", new action " + added;
                    check(Chords(settings).Take(5).SequenceEqual(previous), "New defaults do not steal an existing binding: " + scenario);
                    check((added == 0 ? settings.CloseWindowShortcut : settings.SearchAppsShortcut) == 0,
                        "Only the new colliding default is disabled: " + scenario);
                    check((added == 0 ? settings.SearchAppsShortcut : settings.CloseWindowShortcut) == (added == 0 ? SearchDefault : CloseDefault) && Accepts(settings),
                        "An unrelated new action remains available and settings remain editable: " + scenario);
                    var normalized = Chords(settings); MonitorShortcutPolicy.Normalize(settings);
                    check(Chords(settings).SequenceEqual(normalized), "Collision migration is idempotent: " + scenario);
                }
            }
            int[] defaults = MonitorShortcutPolicy.Defaults();
            for (int slot = 0; slot < defaults.Length; slot++) {
                var settings = new Settings { UndoMoveShortcut = defaults[slot], MonitorMoveShortcuts = null };
                MonitorShortcutPolicy.Normalize(settings);
                int[] expected = defaults.ToArray(); expected[slot] = 0;
                check(settings.UndoMoveShortcut == defaults[slot] && settings.MonitorMoveShortcuts.SequenceEqual(expected) &&
                    settings.CloseWindowShortcut == CloseDefault && settings.SearchAppsShortcut == SearchDefault && Accepts(settings),
                    "The existing Undo-before-monitor migration still applies with both added actions: slot " + slot);
            }
            int supplied = (int)(Keys.Control | Keys.Alt | Keys.F20);
            int[] original = { SearchDefault, CloseDefault, supplied, supplied, defaults[0] };
            var oversized = new Settings { MonitorMoveShortcuts = original };
            MonitorShortcutPolicy.Normalize(oversized);
            check(oversized.MonitorMoveShortcuts.SequenceEqual(new[] { SearchDefault, CloseDefault, supplied, 0 }) &&
                oversized.CloseWindowShortcut == 0 && oversized.SearchAppsShortcut == 0,
                "An oversized monitor array retains leading legacy choices ahead of both new defaults");
            check(original.SequenceEqual(new[] { SearchDefault, CloseDefault, supplied, supplied, defaults[0] }),
                "Normalization does not mutate the caller's monitor array");
            var partial = new Settings { MonitorMoveShortcuts = new[] { 0 } };
            MonitorShortcutPolicy.Normalize(partial);
            check(partial.MonitorMoveShortcuts.SequenceEqual(new[] { 0, defaults[1], defaults[2], defaults[3] }) &&
                partial.CloseWindowShortcut == CloseDefault && partial.SearchAppsShortcut == SearchDefault,
                "Explicit Off in a partial monitor array survives addition of keyboard defaults");
            var invalidUndo = new Settings { UndoMoveShortcut = 12345 };
            MonitorShortcutPolicy.Normalize(invalidUndo);
            check(invalidUndo.UndoMoveShortcut == 12345 && Rejects(invalidUndo),
                "Legacy invalid Undo stays unchanged and remains explicitly invalid rather than silently reassigned");
        }

        static void NewBindingNormalization(Action<bool, string> check)
        {
            int customClose = (int)(Keys.Control | Keys.Alt | Keys.F20), customSearch = (int)(Keys.Control | Keys.Alt | Keys.Space);
            var custom = new Settings { CloseWindowShortcut = customClose, SearchAppsShortcut = customSearch };
            MonitorShortcutPolicy.Normalize(custom);
            check(custom.CloseWindowShortcut == customClose && custom.SearchAppsShortcut == customSearch && Accepts(custom),
                "Valid explicit new chords are preserved");
            var off = new Settings { CloseWindowShortcut = 0, SearchAppsShortcut = 0 };
            MonitorShortcutPolicy.Normalize(off);
            check(off.CloseWindowShortcut == 0 && off.SearchAppsShortcut == 0 && Accepts(off), "Both explicit Off choices remain disabled");
            var shared = new Settings { CloseWindowShortcut = customClose, SearchAppsShortcut = customClose };
            MonitorShortcutPolicy.Normalize(shared);
            check(shared.CloseWindowShortcut == customClose && shared.SearchAppsShortcut == 0 && Accepts(shared),
                "A collision between new actions keeps Close and disables Search deterministically");
            var sharedLegacy = new Settings { UndoMoveShortcut = customClose, CloseWindowShortcut = customClose, SearchAppsShortcut = customClose };
            MonitorShortcutPolicy.Normalize(sharedLegacy);
            check(sharedLegacy.UndoMoveShortcut == customClose && sharedLegacy.CloseWindowShortcut == 0 && sharedLegacy.SearchAppsShortcut == 0 && Accepts(sharedLegacy),
                "A legacy binding shared by both new actions disables both new actions");
            int[] invalid = { -1, (int)Keys.W, (int)(Keys.Control | Keys.Space), (int)(Keys.Control | Keys.Shift),
                (int)(Keys.Control | Keys.Shift | Keys.F12), (int)(Keys.Control | Keys.Shift | Keys.LWin), SearchDefault | 0x08000000 };
            for (int added = 0; added < 2; added++) {
                foreach (int chord in invalid) {
                    var settings = new Settings();
                    if (added == 0) settings.CloseWindowShortcut = chord; else settings.SearchAppsShortcut = chord;
                    int[] previous = Chords(settings).Take(5).ToArray();
                    check(Rejects(settings), "Invalid explicit new binding is rejected before normalization: action " + added + ", chord " + chord);
                    MonitorShortcutPolicy.Normalize(settings);
                    check((added == 0 ? settings.CloseWindowShortcut : settings.SearchAppsShortcut) == 0 &&
                        Chords(settings).Take(5).SequenceEqual(previous) && Accepts(settings),
                        "Invalid new binding is disabled without affecting released actions: action " + added + ", chord " + chord);
                }
            }
        }

        static void Validation(Action<bool, string> check)
        {
            int[] valid = Chords(new Settings());
            for (int first = 0; first < valid.Length; first++) {
                for (int second = first + 1; second < valid.Length; second++) {
                    int[] duplicated = valid.ToArray(); duplicated[second] = duplicated[first];
                    Settings settings = WithChords(duplicated);
                    check(Rejects(settings), "Duplicate chords are rejected across every pair of actions: " + first + "/" + second);
                    check(Chords(settings).SequenceEqual(duplicated), "Interactive validation does not change the duplicated choices: " + first + "/" + second);
                }
            }
            for (int action = 0; action < valid.Length; action++) {
                foreach (int invalid in new[] { -1, (int)Keys.Space, (int)(Keys.Shift | Keys.W), (int)(Keys.Control | Keys.Shift | Keys.F12) }) {
                    int[] choices = valid.ToArray(); choices[action] = invalid;
                    check(Rejects(WithChords(choices)), "Unsupported chords are rejected for all seven actions: " + action + ", chord " + invalid);
                }
                int[] disabled = valid.ToArray(); disabled[action] = 0;
                check(Accepts(WithChords(disabled)), "Each action can independently be switched Off: " + action);
            }
            check(Accepts(WithChords(new int[7])), "Any number of Off choices can coexist");
            foreach (int size in new[] { 0, 3, 5 })
                check(Rejects(new Settings { MonitorMoveShortcuts = new int[size] }), "A malformed monitor array is rejected before applying actions: length " + size);
            check(Rejects(new Settings { MonitorMoveShortcuts = null }), "A missing monitor array is rejected before applying actions");
        }

        internal static void AdditionalMonitorBindings(Action<bool, string> check)
        {
            var displays = new[] {
                new Display { Device = @"\\.\DISPLAY12", Width = 1920, Height = 1080 },
                new Display { Device = @"\\.\DISPLAY6", Width = 1920, Height = 1080 },
                new Display { Device = @"\\.\display2", Width = 1920, Height = 1080, Primary = true },
                new Display { Device = @"\\.\DISPLAY5", Width = 1920, Height = 1080 }
            };
            check(MonitorShortcutPolicy.ConnectedDisplays(displays).Select(MonitorShortcutPolicy.DisplayNumber)
                .SequenceEqual(new[] { 2, 5, 6, 12 }), "Connected shortcuts use Windows display numbers in numeric order, including gaps and numbers above nine");
            check(MonitorShortcutPolicy.Target(displays, 5) == displays[3] && MonitorShortcutPolicy.Target(displays, 12) == displays[0] &&
                MonitorShortcutPolicy.Target(displays, 1) == null, "Numbered moves resolve an exact connected target above four without redirecting a missing target");
            var ambiguous = displays.Concat(new[] { displays[3], new Display { Device = @"\\.\DISPLAY7", Width = 0, Height = 1080 },
                new Display { Device = @"\\.\DISPLAY08", Width = 1920, Height = 1080 } }).ToArray();
            check(MonitorShortcutPolicy.Target(ambiguous, 5) == null && MonitorShortcutPolicy.ConnectedDisplays(ambiguous)
                .Select(MonitorShortcutPolicy.DisplayNumber).SequenceEqual(new[] { 2, 6, 12 }), "Ambiguous and malformed display targets cannot claim a shortcut row or movement");

            int chord = (int)(Keys.Control | Keys.Shift | Keys.D5);
            var settings = new Settings { MonitorMoveShortcuts = new int[4], SearchAppsShortcut = 0, TileWindowShortcut = 0, FocusWindowShortcuts = new int[4] };
            check(MonitorShortcutPolicy.Choice(settings, 5) == 0 && !settings.HasWindowMovement, "Extra displays start with an unassigned shortcut");
            MonitorShortcutPolicy.SetChoice(settings, 5, chord);
            MonitorShortcutPolicy.SetChoice(settings, 12, 0);
            check(settings.HasWindowMovement && Accepts(settings), "An extra shortcut enables the movement control and validates alongside released bindings");
            check(MonitorShortcutPolicy.Target(displays.Where(display => display != displays[3]), 5) == null &&
                MonitorShortcutPolicy.Choice(settings, 5) == chord && MonitorShortcutPolicy.Target(displays, 5) == displays[3],
                "Removing and reconnecting a display changes target availability while preserving its chord");
            var copy = settings.Copy();
            MonitorShortcutPolicy.SetChoice(copy, 5, 0);
            check(MonitorShortcutPolicy.Choice(settings, 5) == chord && copy.AdditionalMonitorMoveShortcuts.ContainsKey("12") &&
                MonitorShortcutPolicy.Choice(copy, 12) == 0, "Settings snapshots deep-copy extra display shortcuts, including explicit Off");

            var conflicting = settings.Copy();
            MonitorShortcutPolicy.SetChoice(conflicting, 6, chord);
            check(Rejects(conflicting), "An extra display binding reserves its chord even when its target is disconnected");
            conflicting.SearchAppsShortcut = chord;
            conflicting.AdditionalMonitorMoveShortcuts["08"] = chord;
            conflicting.AdditionalMonitorMoveShortcuts["2"] = chord;
            conflicting.AdditionalMonitorMoveShortcuts["7"] = (int)Keys.F12;
            MonitorShortcutPolicy.Normalize(conflicting);
            check(conflicting.SearchAppsShortcut == chord && MonitorShortcutPolicy.Choice(conflicting, 5) == 0 &&
                MonitorShortcutPolicy.Choice(conflicting, 6) == 0 && MonitorShortcutPolicy.Choice(conflicting, 7) == 0 &&
                !conflicting.AdditionalMonitorMoveShortcuts.ContainsKey("08") && !conflicting.AdditionalMonitorMoveShortcuts.ContainsKey("2") &&
                Accepts(conflicting), "Loading malformed extra settings preserves established actions and sanitizes invalid numbers and conflicts");
        }

        static void Persistence(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.KeyboardShortcutPolicyTests." + Guid.NewGuid().ToString("N"));
            var paths = new List<string>();
            Directory.CreateDirectory(directory);
            try {
                var serializer = new JavaScriptSerializer();
                var empty = serializer.Deserialize<Settings>("{}");
                check(empty.CloseWindowShortcut == CloseDefault && empty.SearchAppsShortcut == SearchDefault,
                    "Missing JSON fields receive constructor defaults");
                var explicitOff = serializer.Deserialize<Settings>("{\"CloseWindowShortcut\":0,\"SearchAppsShortcut\":0}");
                MonitorShortcutPolicy.Normalize(explicitOff);
                check(explicitOff.CloseWindowShortcut == 0 && explicitOff.SearchAppsShortcut == 0,
                    "Explicit JSON Off values override constructor defaults");
                string legacy = Path.Combine(directory, "legacy.json"); paths.Add(legacy);
                int legacyUndo = (int)(Keys.Control | Keys.Alt | Keys.Back);
                int[] legacyMonitors = { 0, (int)(Keys.Control | Keys.Alt | Keys.F18), 0, (int)(Keys.Control | Keys.Alt | Keys.F19) };
                File.WriteAllText(legacy, "{\"HiddenIdentities\":[],\"UndoMoveShortcut\":" + legacyUndo +
                    ",\"MonitorMoveShortcuts\":" + serializer.Serialize(legacyMonitors) +
                    ",\"WindowShelfActivationWidth\":160,\"WindowShelfActivationHeight\":48,\"WindowShelfHoverDelayMs\":645}");
                Settings settings = Program.Load(legacy);
                check(settings.UndoMoveShortcut == legacyUndo && settings.MonitorMoveShortcuts.SequenceEqual(legacyMonitors) &&
                    settings.CloseWindowShortcut == CloseDefault && settings.SearchAppsShortcut == SearchDefault &&
                    settings.AdditionalMonitorMoveShortcuts.Count == 0,
                    "An old settings file keeps every released explicit binding and receives new defaults");
                check(settings.WindowShelfActivationWidth == 160 && settings.WindowShelfActivationHeight == 48 && settings.WindowShelfHoverDelayMs == 645,
                    "Adding keyboard defaults preserves Ghost Bar choices from old JSON");
                CheckRoundTrip(settings, legacy, check, "legacy defaults");
                string extendedPath = Path.Combine(directory, "extra-displays.json"); paths.Add(extendedPath);
                int extraChord = (int)(Keys.Control | Keys.Shift | Keys.D6);
                MonitorShortcutPolicy.SetChoice(settings, 6, extraChord);
                MonitorShortcutPolicy.SetChoice(settings, 12, 0);
                Program.Save(settings, extendedPath);
                var extended = Program.Load(extendedPath);
                check(MonitorShortcutPolicy.Choice(extended, 6) == extraChord && extended.AdditionalMonitorMoveShortcuts.ContainsKey("12") &&
                    MonitorShortcutPolicy.Choice(extended, 12) == 0 && extended.MonitorMoveShortcuts.SequenceEqual(legacyMonitors) && Accepts(extended),
                    "Disk persistence round-trips extra monitor choices independently of connected hardware and legacy shortcuts");
                for (int action = 0; action < 5; action++) {
                    for (int added = 0; added < 2; added++) {
                        var source = new Settings(); SetExisting(source, action, added == 0 ? CloseDefault : SearchDefault);
                        string path = Path.Combine(directory, "legacy-conflict-" + action + "-" + added + ".json"); paths.Add(path);
                        // Leave both new fields absent, as in an actual older-version settings file.
                        var legacyValues = new Dictionary<string, object> {
                            { "HiddenIdentities", new string[0] }, { "UndoMoveShortcut", source.UndoMoveShortcut },
                            { "MonitorMoveShortcuts", source.MonitorMoveShortcuts }
                        };
                        File.WriteAllText(path, serializer.Serialize(legacyValues));
                        Settings migrated = Program.Load(path);
                        check(Chords(migrated).Take(5).SequenceEqual(Chords(source).Take(5)) &&
                            (added == 0 ? migrated.CloseWindowShortcut : migrated.SearchAppsShortcut) == 0 && Accepts(migrated),
                            "Loading legacy JSON keeps an existing binding ahead of an added default: " + action + "/" + added);
                        CheckRoundTrip(migrated, path, check, "legacy conflict " + action + "/" + added);
                    }
                }
                int customClose = (int)(Keys.Control | Keys.Alt | Keys.F20), customSearch = (int)(Keys.Control | Keys.Alt | Keys.Space);
                foreach (int[] choices in new[] { new[] { 0, 0 }, new[] { customClose, customSearch }, new[] { 0, customSearch }, new[] { customClose, 0 } }) {
                    string path = Path.Combine(directory, "explicit-" + choices[0] + "-" + choices[1] + ".json"); paths.Add(path);
                    Settings explicitSettings = new Settings { CloseWindowShortcut = choices[0], SearchAppsShortcut = choices[1] };
                    CheckRoundTrip(explicitSettings, path, check, "explicit choices " + choices[0] + "/" + choices[1]);
                }
                string invalidPath = Path.Combine(directory, "invalid-new.json"); paths.Add(invalidPath);
                var invalidSource = new Settings { CloseWindowShortcut = -1, SearchAppsShortcut = (int)(Keys.Control | Keys.Space) };
                Program.Save(invalidSource, invalidPath);
                Settings invalidLoaded = Program.Load(invalidPath);
                check(invalidLoaded.CloseWindowShortcut == 0 && invalidLoaded.SearchAppsShortcut == 0 &&
                    Chords(invalidLoaded).Take(5).SequenceEqual(Chords(invalidSource).Take(5)) && Accepts(invalidLoaded),
                    "Loading corrupt added bindings disables only the added actions");
                CheckRoundTrip(invalidLoaded, invalidPath, check, "normalized corrupt choices");
            } finally {
                foreach (string path in paths) {
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                }
                Directory.Delete(directory);
            }
        }

        static void CheckRoundTrip(Settings settings, string path, Action<bool, string> check, string scenario)
        {
            int[] expected = Chords(settings);
            Settings changed = settings.Copy();
            check(Chords(changed).SequenceEqual(expected) && !Object.ReferenceEquals(changed.MonitorMoveShortcuts, settings.MonitorMoveShortcuts),
                "Settings.Copy preserves all seven chords with an independent monitor array: " + scenario);
            changed.WindowShelfActivationWidth = 172; changed.WindowShelfActivationHeight = 60; changed.WindowShelfHoverDelayMs = 750;
            changed.AllowMovingFullscreenApps = true;
            MonitorShortcutPolicy.Validate(changed); Program.Save(changed, path);
            Settings loaded = Program.Load(path);
            check(Chords(loaded).SequenceEqual(expected) && loaded.WindowShelfActivationWidth == 172 && loaded.WindowShelfActivationHeight == 60 &&
                loaded.WindowShelfHoverDelayMs == 750 && loaded.AllowMovingFullscreenApps,
                "An unrelated settings edit saves and reloads all keyboard choices: " + scenario);
            check(!File.Exists(path + ".tmp"), "Successful persistence leaves no temporary settings file: " + scenario);
            int[] loadedChords = Chords(loaded); MonitorShortcutPolicy.Normalize(loaded);
            check(Chords(loaded).SequenceEqual(loadedChords), "Saved shortcut normalization remains idempotent: " + scenario);
            check(Chords(settings).SequenceEqual(expected), "Copying and saving does not mutate original keyboard choices: " + scenario);
        }

        static void EmptyTransaction(Action<bool, string> check)
        {
            int commits = 0;
            MonitorShortcutPolicy.Apply(new GlobalShortcut[0], new int[0], delegate { commits++; });
            check(commits == 1, "A generic empty shortcut transaction commits once without registering a native hotkey");
            var failure = new InvalidOperationException("owned persistence fixture");
            bool propagated = false;
            try { MonitorShortcutPolicy.Apply(new GlobalShortcut[0], new int[0], delegate { throw failure; }); }
            catch (InvalidOperationException error) { propagated = Object.ReferenceEquals(error, failure); }
            check(propagated, "The generic transaction propagates the exact persistence failure");
        }

        static void RegistrationFallback(Action<bool, string> check)
        {
            int[] configured = Chords(new Settings()), active = configured.ToArray(); active[5] = 0;
            int[] original = configured.ToArray(), originalActive = active.ToArray();
            int[] unchanged = MonitorShortcutPolicy.RegistrationChoices(configured, configured, active, -1);
            check(unchanged.SequenceEqual(active), "An unchanged unavailable action uses its current Off registration without implicitly retrying");
            int[] retried = MonitorShortcutPolicy.RegistrationChoices(configured, configured, active, 5);
            check(retried.SequenceEqual(configured), "An explicit action retry requests the configured chord despite Current being Off");
            int[] changed = configured.ToArray(); changed[5] = (int)(Keys.Control | Keys.Alt | Keys.F22);
            check(MonitorShortcutPolicy.RegistrationChoices(changed, configured, active, -1).SequenceEqual(changed),
                "Changing an unavailable action attempts the new explicit chord");
            check(configured.SequenceEqual(original) && active.SequenceEqual(originalActive), "Choosing runtime bindings does not mutate configured or active arrays");
            const string error = "owned registration failure";
            check(MonitorShortcutPolicy.RegistrationError(configured[5], active[5], error) == error,
                "An unchanged unavailable action retains its registration error during unrelated saves");
            check(MonitorShortcutPolicy.RegistrationError(configured[5], configured[5], error) == null,
                "A successful retry clears the corresponding registration error");
            check(MonitorShortcutPolicy.RegistrationError(0, 0, error) == null,
                "Explicitly disabling an unavailable action clears the obsolete error");
            check(MonitorShortcutPolicy.RegistrationError(configured[5], 0, null) == null,
                "A missing error is not invented by shared error reconciliation");
            for (int action = 0; action < configured.Length; action++) {
                int[] current = configured.ToArray(); current[action] = 0;
                check(MonitorShortcutPolicy.RegistrationChoices(configured, configured, current, -1).SequenceEqual(current) &&
                    MonitorShortcutPolicy.RegistrationChoices(configured, configured, current, action).SequenceEqual(configured),
                    "Unchanged fallback and explicit retry have the same meaning for all seven actions: " + action);
            }
        }

        static int[] Current(GlobalShortcut[] bindings)
        { return bindings.Select(binding => (int)binding.Current).ToArray(); }

        static int[] UnusedNativeChords()
        {
            var available = new List<int>();
            using (var probe = new GlobalShortcut()) {
                // These message-only fixtures never claim normal shortcuts or
                // generate keyboard input. Keep externally owned chords alone.
                for (Keys key = Keys.F13; key <= Keys.F24; key++) {
                    Keys choice = Keys.Control | Keys.Alt | Keys.Shift | key;
                    try { probe.Apply(choice, delegate { }); available.Add((int)choice); }
                    catch (InvalidOperationException) { }
                    finally { probe.Apply(Keys.None, delegate { }); }
                }
            }
            return available.ToArray();
        }

        static void CheckReleased(IEnumerable<int> choices, Action<bool, string> check, string scenario)
        {
            using (var probe = new GlobalShortcut()) {
                foreach (int choice in choices.Distinct()) {
                    bool registered = false;
                    try { probe.Apply((Keys)choice, delegate { }); registered = true; }
                    catch (InvalidOperationException) { }
                    check(registered && (int)probe.Current == choice, "Rollback releases every tentative native registration: " + scenario + ", chord " + choice);
                    probe.Apply(Keys.None, delegate { });
                }
            }
        }

        static void NativeTransactions(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.KeyboardNativeTransactionTests." + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json"), blockedSave = Path.Combine(directory, "save-refused");
            var bindings = new List<GlobalShortcut>();
            Directory.CreateDirectory(directory);
            try {
                int[] pool = UnusedNativeChords();
                check(pool.Length == 12, "Twelve unused Ctrl + Alt + Shift F13-F24 fixture chords are available for the seven-binding native regression");
                if (pool.Length != 12) return;
                for (int i = 0; i < 7; i++) bindings.Add(new GlobalShortcut());
                GlobalShortcut[] all = bindings.ToArray(); int[] initial = pool.Take(7).ToArray();
                int commits = 0;
                MonitorShortcutPolicy.Apply(all, initial, delegate { commits++; });
                check(commits == 1 && Current(all).SequenceEqual(initial), "The generic seven-binding transaction establishes every owned native chord atomically");
                using (var blocker = new GlobalShortcut()) {
                    blocker.Apply((Keys)pool[7], delegate { });
                    int[] changed = initial.ToArray(); changed[0] = pool[8]; changed[1] = pool[9]; changed[5] = pool[7];
                    Settings saved = WithChords(initial); Program.Save(saved, path); string before = File.ReadAllText(path);
                    bool rejected = false;
                    try { MonitorShortcutPolicy.Apply(all, changed, delegate { commits++; Program.Save(WithChords(changed), path); }); }
                    catch (InvalidOperationException error) { rejected = error.Message.Contains("already registered"); }
                    check(rejected && commits == 1 && Current(all).SequenceEqual(initial),
                        "A late Close registration conflict preserves all seven previous registrations and never reaches persistence");
                    check(File.ReadAllText(path) == before, "A late action conflict does not change the owned settings file");
                    CheckReleased(new[] { pool[8], pool[9] }, check, "late action conflict");

                    // Model a failed startup action registration: the saved
                    // preference remains configured, while its runtime is Off.
                    all[5].Apply(Keys.None, delegate { });
                    string actionError = null;
                    try { all[5].Apply((Keys)pool[7], delegate { }); }
                    catch (InvalidOperationException error) { actionError = error.Message; }
                    int[] configured = initial.ToArray(); configured[5] = pool[7];
                    Settings configuredSettings = WithChords(configured); Program.Save(configuredSettings, path);
                    int[] unavailable = Current(all);
                    check(actionError != null && unavailable[5] == 0 && unavailable.Where((choice, i) => i != 5).SequenceEqual(initial.Where((choice, i) => i != 5)),
                        "A failed startup Close binding leaves the existing six runtime states unaffected");
                    Settings unrelated = configuredSettings.Copy(); unrelated.WindowShelfHoverDelayMs = 750;
                    int[] choices = MonitorShortcutPolicy.RegistrationChoices(Chords(unrelated), Chords(configuredSettings), Current(all), -1);
                    int unrelatedCommits = 0;
                    MonitorShortcutPolicy.Apply(all, choices, delegate { unrelatedCommits++; Program.Save(unrelated, path); });
                    actionError = MonitorShortcutPolicy.RegistrationError(unrelated.CloseWindowShortcut, (int)all[5].Current, actionError);
                    Settings reloaded = Program.Load(path);
                    check(unrelatedCommits == 1 && Current(all).SequenceEqual(unavailable),
                        "An unrelated preference edit succeeds without retrying the blocked action or changing the other six bindings");
                    check(reloaded.WindowShelfHoverDelayMs == 750 && Chords(reloaded).SequenceEqual(configured) && actionError != null,
                        "The unrelated save retains the configured failed action and its runtime error");

                    int[] explicitRetry = MonitorShortcutPolicy.RegistrationChoices(configured, configured, Current(all), 5);
                    bool retryRejected = false; int retryCommits = 0;
                    try { MonitorShortcutPolicy.Apply(all, explicitRetry, delegate { retryCommits++; Program.Save(unrelated, path); }); }
                    catch (InvalidOperationException) { retryRejected = true; }
                    check(retryRejected && retryCommits == 0 && Current(all).SequenceEqual(unavailable) &&
                        MonitorShortcutPolicy.RegistrationError(configured[5], (int)all[5].Current, actionError) == actionError,
                        "Explicit retry of a still-blocked action preserves the error, preferences and all seven runtime states");
                    blocker.Apply(Keys.None, delegate { });
                    MonitorShortcutPolicy.Apply(all, explicitRetry, delegate { retryCommits++; Program.Save(unrelated, path); });
                    actionError = MonitorShortcutPolicy.RegistrationError(configured[5], (int)all[5].Current, actionError);
                    check(retryCommits == 1 && Current(all).SequenceEqual(configured) && actionError == null &&
                        Chords(Program.Load(path)).SequenceEqual(configured),
                        "Explicit retry after releasing the native blocker registers only the unavailable action and clears its error");

                    int[] rollback = configured.ToArray(); rollback[0] = pool[8]; rollback[1] = pool[9]; rollback[5] = pool[10]; rollback[6] = pool[11];
                    Settings candidate = WithChords(rollback); candidate.WindowShelfHoverDelayMs = 800;
                    Directory.CreateDirectory(blockedSave);
                    before = File.ReadAllText(path); int persistenceCalls = 0; bool saveRejected = false;
                    try {
                        MonitorShortcutPolicy.Apply(all, rollback, delegate { persistenceCalls++; Program.Save(candidate, blockedSave); });
                    } catch (IOException) { saveRejected = true; }
                    check(saveRejected && persistenceCalls == 1 && Current(all).SequenceEqual(configured) && File.ReadAllText(path) == before,
                        "A real owned-file save failure after acquiring new Undo, monitor, Close and Search chords restores all seven previous registrations");
                    CheckReleased(rollback.Where((choice, i) => choice != configured[i]), check, "persistence failure");

                    Settings currentSettings = unrelated;
                    var refreshFailure = new InvalidOperationException("owned Refresh failure"); bool refreshedRejected = false;
                    int refreshCalls = 0;
                    try {
                        MonitorShortcutPolicy.Apply(all, rollback, delegate {
                            Program.Save(candidate, path); currentSettings = candidate;
                            try { refreshCalls++; throw refreshFailure; }
                            catch { currentSettings = unrelated; Program.Save(unrelated, path); refreshCalls++; throw; }
                        });
                    } catch (InvalidOperationException error) { refreshedRejected = Object.ReferenceEquals(error, refreshFailure); }
                    check(refreshedRejected && refreshCalls == 2 && Object.ReferenceEquals(currentSettings, unrelated) &&
                        Current(all).SequenceEqual(configured) && File.ReadAllText(path) == before,
                        "A Refresh failure after persistence restores the owned settings snapshot and all seven prior native registrations");
                    CheckReleased(rollback.Where((choice, i) => choice != configured[i]), check, "Refresh failure");
                }
                int[] last = Current(all);
                foreach (GlobalShortcut binding in all) binding.Dispose();
                check(Current(all).All(choice => choice == 0), "Disposing the seven owned shortcut windows clears their runtime states");
                CheckReleased(last, check, "fixture disposal");
            } finally {
                foreach (GlobalShortcut binding in bindings) binding.Dispose();
                foreach (string file in new[] { path, path + ".tmp", blockedSave + ".tmp" }) if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(blockedSave)) Directory.Delete(blockedSave);
                Directory.Delete(directory);
            }
        }
    }
}
