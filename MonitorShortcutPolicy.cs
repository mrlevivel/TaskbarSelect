using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
namespace TaskbarSelect
{
    internal static class MonitorShortcutPolicy
    {
        const string DevicePrefix = @"\\.\DISPLAY";
        const int LegacySlots = 4;

        internal static int[] Defaults()
        { return Enumerable.Range(0, LegacySlots).Select(i => (int)(Keys.Control | Keys.Shift | (Keys)((int)Keys.D1 + i))).ToArray(); }

        static string NumberKey(int number)
        { return number.ToString(CultureInfo.InvariantCulture); }

        static int ParseNumber(string key)
        {
            int number;
            return Int32.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
                number > 0 && key == NumberKey(number) ? number : 0;
        }

        internal static int DisplayNumber(Display display)
        {
            if (display == null || display.Width <= 0 || display.Height <= 0 || display.Device == null ||
                !display.Device.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase)) return 0;
            return ParseNumber(display.Device.Substring(DevicePrefix.Length));
        }

        internal static List<Display> ConnectedDisplays(IEnumerable<Display> displays)
        {
            return (displays ?? Enumerable.Empty<Display>()).GroupBy(DisplayNumber)
                .Where(group => group.Key > 0 && group.Count() == 1).OrderBy(group => group.Key)
                .Select(group => group.First()).ToList();
        }

        internal static int Choice(Settings settings, int number)
        {
            if (number < 1) return 0;
            if (number <= LegacySlots) return settings.MonitorMoveShortcuts[number - 1];
            int value;
            return settings.AdditionalMonitorMoveShortcuts != null &&
                settings.AdditionalMonitorMoveShortcuts.TryGetValue(NumberKey(number), out value) ? value : 0;
        }

        internal static void SetChoice(Settings settings, int number, int choice)
        {
            if (number < 1) throw new ArgumentOutOfRangeException("number");
            if (number <= LegacySlots) { settings.MonitorMoveShortcuts[number - 1] = choice; return; }
            if (settings.AdditionalMonitorMoveShortcuts == null) settings.AdditionalMonitorMoveShortcuts = new Dictionary<string, int>();
            settings.AdditionalMonitorMoveShortcuts[NumberKey(number)] = choice;
        }

        internal static void Normalize(Settings settings)
        {
            // Undo existed before monitor shortcuts. Keep that user's binding and
            // turn off any newly introduced default that would claim its chord.
            // Also retain usable explicit slots in a partial or oversized array.
            int[] choices = Defaults();
            if (settings.MonitorMoveShortcuts != null)
                Array.Copy(settings.MonitorMoveShortcuts, choices, Math.Min(choices.Length, settings.MonitorMoveShortcuts.Length));
            var assigned = new HashSet<int>();
            if (settings.UndoMoveShortcut != 0) assigned.Add(settings.UndoMoveShortcut);
            for (int i = 0; i < choices.Length; i++)
                if (!GlobalShortcut.IsValid((Keys)choices[i]) || (choices[i] != 0 && !assigned.Add(choices[i]))) choices[i] = 0;
            settings.MonitorMoveShortcuts = choices;
            // These actions were added later. Never replace an existing Undo or
            // monitor binding, including an explicit Off choice, with a default.
            settings.CloseWindowShortcut = NormalizeNew(settings.CloseWindowShortcut, assigned);
            settings.SearchAppsShortcut = NormalizeNew(settings.SearchAppsShortcut, assigned);
            // Directional focus and tiling are newer than every binding above.
            // Existing choices win, even when they use one of these defaults.
            int[] focus = NavigationShortcutPolicy.Defaults();
            if (settings.FocusWindowShortcuts != null)
                Array.Copy(settings.FocusWindowShortcuts, focus, Math.Min(focus.Length, settings.FocusWindowShortcuts.Length));
            for (int i = 0; i < focus.Length; i++) focus[i] = NormalizeNew(focus[i], assigned);
            settings.FocusWindowShortcuts = focus;
            settings.TileWindowShortcut = NormalizeNew(settings.TileWindowShortcut, assigned);
            // Minimize and always-on-top are newer than every binding above.
            // Existing explicit navigation/layout chords keep priority too.
            settings.MinimizeWindowShortcut = NormalizeNew(settings.MinimizeWindowShortcut, assigned);
            settings.PinWindowShortcut = NormalizeNew(settings.PinWindowShortcut, assigned);
            // Saved extra displays have lower migration priority than released bindings.
            // Keep valid entries independently of the currently connected topology.
            var additional = settings.AdditionalMonitorMoveShortcuts ?? new Dictionary<string, int>();
            settings.AdditionalMonitorMoveShortcuts = additional.Select(pair => new { Number = ParseNumber(pair.Key), pair.Value })
                .Where(entry => entry.Number > LegacySlots).OrderBy(entry => entry.Number)
                .ToDictionary(entry => NumberKey(entry.Number), entry => NormalizeNew(entry.Value, assigned));
        }
        static int NormalizeNew(int choice, HashSet<int> assigned)
        {
            if (!GlobalShortcut.IsValid((Keys)choice) || (choice != 0 && !assigned.Add(choice))) return 0;
            return choice;
        }
        internal static void Validate(Settings settings)
        {
            if (settings.MonitorMoveShortcuts == null || settings.MonitorMoveShortcuts.Length != LegacySlots) throw new InvalidOperationException("Four legacy monitor shortcut slots are required.");
            var additional = settings.AdditionalMonitorMoveShortcuts;
            if (additional != null && additional.Keys.Any(key => ParseNumber(key) <= LegacySlots))
                throw new InvalidOperationException("Additional monitor shortcuts require a valid Windows display number above four.");
            var chords = settings.MonitorMoveShortcuts.Concat(new[] {
                settings.UndoMoveShortcut, settings.CloseWindowShortcut, settings.SearchAppsShortcut
            }).Concat(NavigationShortcutPolicy.Choices(settings)).Concat(new[] {
                settings.MinimizeWindowShortcut, settings.PinWindowShortcut
            }).Concat(additional == null ? Enumerable.Empty<int>() : additional.Values).Select(k => (Keys)k).ToArray();
            if (chords.Any(k => !GlobalShortcut.IsValid(k))) throw new InvalidOperationException("Choose at least two modifiers and a supported key.");
            if (chords.Where(k => k != Keys.None).Distinct().Count() != chords.Count(k => k != Keys.None))
                throw new InvalidOperationException("That shortcut is already assigned to another Taskbar Select action. Choose another chord or disable the previous action first.");
        }
        internal static Display Target(IEnumerable<Display> displays, int number)
        {
            if (number < 1 || displays == null) return null;
            var found = displays.Where(display => DisplayNumber(display) == number).Take(2).ToArray();
            return found.Length == 1 ? found[0] : null;
        }
        internal static int[] RegistrationChoices(int[] requested, int[] previous, int[] current, int retry)
        {
            return requested.Select((key, i) => i == retry || key != previous[i] ? key : current[i]).ToArray();
        }
        internal static string RegistrationError(int configured, int current, string error)
        { return configured == current ? null : error; }
        internal static void Apply(GlobalShortcut[] bindings, int[] choices, Action commit)
        { Apply(bindings, choices, commit, 0); }
        static void Apply(GlobalShortcut[] bindings, int[] choices, Action commit, int index)
        {
            if (index == bindings.Length) { commit(); return; }
            bindings[index].Apply((Keys)choices[index], delegate { Apply(bindings, choices, commit, index + 1); });
        }
    }

    internal static class ActionShortcutPolicy
    {
        internal const int Count = 4;
        internal static string[] Labels { get { return new[] { "Close focused window", "Search apps", "Minimize focused window", "Keep focused window on top" }; } }
        internal static int[] Defaults()
        { return new[] { (int)(Keys.Control | Keys.Shift | Keys.W), (int)(Keys.Control | Keys.Shift | Keys.Space),
            (int)(Keys.Control | Keys.Shift | Keys.M), (int)(Keys.Control | Keys.Shift | Keys.P) }; }
        internal static int[] Choices(Settings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            return new[] { settings.CloseWindowShortcut, settings.SearchAppsShortcut, settings.MinimizeWindowShortcut, settings.PinWindowShortcut };
        }
        internal static void Set(Settings settings, int slot, int value)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            switch (slot) {
                case 0: settings.CloseWindowShortcut = value; break;
                case 1: settings.SearchAppsShortcut = value; break;
                case 2: settings.MinimizeWindowShortcut = value; break;
                case 3: settings.PinWindowShortcut = value; break;
                default: throw new ArgumentOutOfRangeException("slot");
            }
        }
    }
}
