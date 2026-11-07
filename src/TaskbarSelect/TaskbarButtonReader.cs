using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace TaskbarSelect
{
    internal sealed class TaskbarButtonSnapshot
    {
        internal string AutomationId, Name;
        internal System.Windows.Rect Bounds;
    }

    internal static class TaskbarButtonReader
    {
        internal static bool Read(TaskbarMover.Click candidate, IntPtr taskbar)
        {
            try {
                AutomationElement root = AutomationElement.FromHandle(taskbar);
                var request = new CacheRequest { TreeScope = TreeScope.Element,
                    TreeFilter = Automation.RawViewCondition, AutomationElementMode = AutomationElementMode.None };
                request.Add(AutomationElement.AutomationIdProperty);
                request.Add(AutomationElement.NameProperty);
                request.Add(AutomationElement.BoundingRectangleProperty);
                AutomationElementCollection elements;
                // This cache belongs to this fresh probe only. All three properties
                // cross the process boundary together rather than once per element.
                using (request.Activate()) elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                return SelectAppButton(Snapshots(elements), candidate.Position, candidate);
            } catch (ElementNotAvailableException error) { TaskbarTargets.NoteReaderError(error.HResult); }
            catch (InvalidOperationException error) { TaskbarTargets.NoteReaderError(error.HResult); }
            catch (COMException error) { TaskbarTargets.NoteReaderError(error.HResult); }
            return false;
        }

        static IEnumerable<TaskbarButtonSnapshot> Snapshots(AutomationElementCollection elements)
        {
            for (int index = 0; index < elements.Count; index++) {
                TaskbarTargets.NoteUiaElements(elements.Count, index + 1);
                var cached = elements[index].Cached;
                yield return new TaskbarButtonSnapshot { AutomationId = cached.AutomationId,
                    Name = cached.Name, Bounds = cached.BoundingRectangle };
            }
        }

        internal static bool SelectAppButton(IEnumerable<TaskbarButtonSnapshot> snapshots,
            TaskbarMover.Point point, TaskbarMover.Click candidate)
        {
            if (snapshots == null || candidate == null) return false;
            TaskbarButtonSnapshot selected = null;
            try {
                foreach (var item in snapshots) {
                    if (item == null) return false;
                    string id = item.AutomationId ?? "";
                    if (!id.StartsWith("Appid: ", StringComparison.OrdinalIgnoreCase) || id.Length == 7) continue;
                    if (item.Bounds.IsEmpty) continue;
                    if (!ValidBounds(item.Bounds)) return false;
                    if (!item.Bounds.Contains(new System.Windows.Point(point.X, point.Y))) continue;
                    if (selected != null && (!String.Equals(selected.AutomationId, id, StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(selected.Name, item.Name, StringComparison.Ordinal) || selected.Bounds != item.Bounds)) return false;
                    if (selected == null) selected = item;
                }
            } catch (ElementNotAvailableException error) { TaskbarTargets.NoteReaderError(error.HResult); return false; }
            catch (InvalidOperationException error) { TaskbarTargets.NoteReaderError(error.HResult); return false; }
            catch (COMException error) { TaskbarTargets.NoteReaderError(error.HResult); return false; }
            if (selected == null) return false;
            candidate.AppId = selected.AutomationId.Substring(7);
            candidate.AppName = TaskbarMover.ButtonAppName(selected.Name);
            candidate.Grouped = TaskbarMover.IsGroupedButtonName(selected.Name);
            candidate.ButtonBounds = new Native.Rect { Left = (int)selected.Bounds.Left, Top = (int)selected.Bounds.Top,
                Right = (int)selected.Bounds.Right, Bottom = (int)selected.Bounds.Bottom };
            return true;
        }

        static bool ValidBounds(System.Windows.Rect bounds)
        {
            foreach (double value in new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom })
                if (Double.IsNaN(value) || Double.IsInfinity(value) || value < Int32.MinValue || value > Int32.MaxValue) return false;
            return bounds.Width > 0 && bounds.Height > 0;
        }
    }
}
