using System;
using System.Linq;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class NavigationShortcutPolicy
    {
        internal const Keys TileDefault = Keys.Control | Keys.Shift | Keys.Alt | Keys.Space;

        // Slot order is shared with the controller: left, right, up, down, tile.
        internal static int[] Defaults()
        {
            return new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down }
                .Select(key => (int)(Keys.Control | Keys.Shift | Keys.Alt | key)).ToArray();
        }

        internal static int[] Choices(Settings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (settings.FocusWindowShortcuts == null || settings.FocusWindowShortcuts.Length != 4)
                throw new InvalidOperationException("Four directional focus shortcut slots are required.");
            return settings.FocusWindowShortcuts.Concat(new[] { settings.TileWindowShortcut }).ToArray();
        }
    }
}
