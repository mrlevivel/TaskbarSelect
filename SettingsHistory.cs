using System.Collections.Generic;

namespace TaskbarSelect
{
    internal sealed class SettingsHistory
    {
        internal sealed class Entry
        {
            internal Settings Settings;
            internal bool? Startup;
            internal string Label;
        }
        readonly List<Entry> entries = new List<Entry>();
        internal Entry Peek { get { return entries.Count == 0 ? null : entries[entries.Count - 1]; } }
        internal void Push(Settings settings, bool? startup, string label)
        {
            if (entries.Count == 20) entries.RemoveAt(0);
            entries.Add(new Entry { Settings = settings.Copy(), Startup = startup, Label = label });
        }
        internal void Pop() { if (entries.Count > 0) entries.RemoveAt(entries.Count - 1); }
    }
}
