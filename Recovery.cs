using System;
using System.Collections.Generic;
using System.IO;

namespace TaskbarSelect
{
    internal sealed class CorruptRecoveryJournalException : IOException
    {
        internal readonly string Journal;
        internal CorruptRecoveryJournalException(string journal, Exception error)
            : base("Invalid work-area recovery file: " + journal, error) { Journal = journal; }

        internal string Preserve()
        {
            string backup = Journal + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
            File.Move(Journal, backup);
            return backup;
        }
    }

    internal sealed class RecoveryResult
    {
        internal bool Success = true;
        internal int PendingWorkAreas;
        internal string PreservedJournal;
        internal readonly List<string> Messages = new List<string>();
    }

    internal static class Recovery
    {
        internal static RecoveryResult ForStartup()
        {
            return ForStartup(delegate { return new WorkAreas().RestoreAvailable(); }, Native.RestoreAll);
        }
        internal static RecoveryResult ForStartup(Func<int> restoreAvailable, Action restoreTaskbars)
        {
            int pending = 0;
            var result = Run(delegate { pending = restoreAvailable(); }, restoreTaskbars);
            result.PendingWorkAreas = pending;
            return result;
        }

        internal static RecoveryResult Persisted(Action cleanup = null)
        {
            return Run(delegate { new WorkAreas().RestoreAll(); }, Native.RestoreAll, cleanup);
        }

        internal static RecoveryResult Run(Action restoreAreas, Action restoreTaskbars, Action cleanup = null)
        {
            var result = new RecoveryResult();
            try { restoreAreas(); }
            catch (CorruptRecoveryJournalException error) {
                try {
                    result.PreservedJournal = error.Preserve();
                    result.Messages.Add("Preserved the damaged recovery file at " + result.PreservedJournal +
                        ". Exact saved work areas were unavailable; no replacement geometry was guessed.");
                } catch (Exception preserveError) {
                    result.Success = false;
                    result.Messages.Add("Could not preserve the damaged recovery file: " + preserveError.Message);
                }
            }
            catch (Exception error) {
                result.Success = false;
                result.Messages.Add("Could not restore saved work areas: " + error.Message);
            }
            finally {
                // The visible-taskbar recovery must not depend on reading or saving JSON.
                try { restoreTaskbars(); }
                catch (AggregateException error) {
                    result.Success = false;
                    foreach (var failure in error.Flatten().InnerExceptions)
                        result.Messages.Add("Could not restore native taskbars: " + failure.Message);
                }
                catch (Exception error) {
                    result.Success = false;
                    result.Messages.Add("Could not restore native taskbars: " + error.Message);
                }
            }
            if (result.Success && cleanup != null) {
                try { cleanup(); }
                catch (Exception error) {
                    result.Success = false;
                    result.Messages.Add("Taskbars were restored, but cleanup failed: " + error.Message);
                }
            }
            return result;
        }

        internal static int Report(RecoveryResult result)
        {
            foreach (string message in result.Messages) Console.Error.WriteLine(message);
            return result.Success ? 0 : 1;
        }
    }
}
