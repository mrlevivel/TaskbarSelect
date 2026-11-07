using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ShellProofOutcome = TaskbarSelect.TaskbarTargets.ShellProofOutcome;
using ShellProofPhase = TaskbarSelect.TaskbarTargets.ShellProofPhase;

namespace TaskbarSelect
{
    // Supplemental, window-specific shell identity. A missing/unsupported resolver
    // keeps Task Manager on the existing native-selection path. No foreign window
    // property, animation option, permission or administrator operation is changed.
    internal static class TaskManagerShellIdentity
    {
        internal const string ReaderMode = "--read-task-manager-identity";
        internal const string ManagementConsoleReaderMode = "--read-mmc-identity";
        internal const int ReaderMilliseconds = 200;
        static readonly object readerGate = new object();
        static bool readerBusy;
        static Process uncollectedReader;
        static Proof hoverProof;
        internal sealed class Proof
        {
            internal readonly IntPtr Window, Taskbar;
            internal readonly uint Process, ShellProcess;
            internal readonly long ProcessStarted, ShellStarted, CapturedTick;
            internal readonly string AppId;
            internal readonly bool ShellOnly;
            internal Proof(IntPtr window, uint process, long processStarted, IntPtr taskbar,
                uint shellProcess, long shellStarted, string appId, long capturedTick)
                : this(window, process, processStarted, taskbar, shellProcess, shellStarted, appId, capturedTick, false) { }
            internal Proof(IntPtr window, uint process, long processStarted, IntPtr taskbar,
                uint shellProcess, long shellStarted, string appId, long capturedTick, bool shellOnly)
            {
                Window = window; Process = process; ProcessStarted = processStarted; Taskbar = taskbar;
                ShellProcess = shellProcess; ShellStarted = shellStarted; AppId = appId; CapturedTick = capturedTick;
                ShellOnly = shellOnly;
            }
        }

        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int size);
        [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint process, out uint session);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int size);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);

        static string TaskManagerPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe"); } }
        static bool SameSession(uint process)
        {
            uint target, self;
            return ProcessIdToSessionId(process, out target) && ProcessIdToSessionId(NativeProcessIdentity.Current.Process, out self) && target == self;
        }
        internal static bool TargetCurrent(IntPtr window, uint process, long started)
        { return OfficialTargetCurrent(window, process, started, TaskManagerPath); }
        internal static bool ManagementConsoleCurrent(IntPtr window, uint process, long started)
        { return OfficialTargetCurrent(window, process, started, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mmc.exe")); }
        static bool CurrentTarget(IntPtr window, uint process, long started, bool managementConsole)
        { return managementConsole ? ManagementConsoleCurrent(window, process, started) : TargetCurrent(window, process, started); }
        static bool OfficialTargetCurrent(IntPtr window, uint process, long started, string expectedPath)
        {
            uint actual;
            return window != IntPtr.Zero && process != 0 && started > 0 && Native.IsWindowVisible(window) &&
                GetAncestor(window, 2) == window && GetWindow(window, 4) == IntPtr.Zero &&
                Native.GetWindowThreadProcessId(window, out actual) != 0 && actual == process && SameSession(process) &&
                NativeProcessIdentity.Started(process) == started &&
                String.Equals(NativeProcessIdentity.Path(process), expectedPath, StringComparison.OrdinalIgnoreCase) &&
                Native.GetWindowThreadProcessId(window, out actual) != 0 && actual == process && NativeProcessIdentity.Started(process) == started;
        }
        internal static bool ReadTaskbar(IntPtr taskbar, out uint process, out long started)
        {
            process = 0; started = 0;
            if (taskbar == IntPtr.Zero || !Native.IsWindowVisible(taskbar) || GetAncestor(taskbar, 2) != taskbar) return false;
            var name = new StringBuilder(128);
            GetClassName(taskbar, name, name.Capacity);
            if (name.ToString() != "Shell_TrayWnd" && name.ToString() != "Shell_SecondaryTrayWnd") return false;
            uint shell, actual;
            if (Native.GetWindowThreadProcessId(GetShellWindow(), out shell) == 0 || shell == 0 ||
                Native.GetWindowThreadProcessId(taskbar, out actual) == 0 || actual != shell || !SameSession(shell)) return false;
            long created = NativeProcessIdentity.Started(shell);
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (created <= 0 || !String.Equals(NativeProcessIdentity.Path(shell), expected, StringComparison.OrdinalIgnoreCase) ||
                Native.GetWindowThreadProcessId(taskbar, out actual) == 0 || actual != shell || NativeProcessIdentity.Started(shell) != created) return false;
            process = shell; started = created; return true;
        }
        internal static bool ProofMatchesTarget(Proof proof, IntPtr window, uint process, long started, IntPtr taskbar, string appId)
        {
            return proof != null && proof.Window != IntPtr.Zero && proof.Taskbar != IntPtr.Zero &&
                proof.Process != 0 && proof.ProcessStarted > 0 && proof.ShellProcess != 0 && proof.ShellStarted > 0 && proof.CapturedTick > 0 &&
                (proof.ShellOnly ? ManagementConsoleIdentityAllowed(proof.AppId) : TaskbarNativeSelection.IsGeneratedIdentity(proof.AppId)) &&
                proof.Window == window && proof.Process == process && proof.ProcessStarted == started && proof.Taskbar == taskbar &&
                String.Equals(proof.AppId, appId, StringComparison.Ordinal);
        }
        internal static bool ProofsMatch(Proof first, Proof second)
        {
            return first != null && second != null && second.CapturedTick > 0 &&
                ProofMatchesTarget(first, second.Window, second.Process, second.ProcessStarted, second.Taskbar, second.AppId) &&
                first.ShellProcess == second.ShellProcess && first.ShellStarted == second.ShellStarted && first.ShellOnly == second.ShellOnly;
        }
        // Only bounded Win32 metadata checks here. Called on the input/UI thread;
        // COM and subprocess queries belong exclusively to the background reader.
        internal static bool ProofIsCurrent(Proof proof)
        {
            if (!ProofFreshAt(proof, Stopwatch.GetTimestamp(), Stopwatch.Frequency)) return false;
            uint shell; long shellStarted;
            return ProofMatchesTarget(proof, proof.Window, proof.Process, proof.ProcessStarted, proof.Taskbar, proof.AppId) &&
                CurrentTarget(proof.Window, proof.Process, proof.ProcessStarted, proof.ShellOnly) &&
                ReadTaskbar(proof.Taskbar, out shell, out shellStarted) && shell == proof.ShellProcess && shellStarted == proof.ShellStarted;
        }
        internal static bool ProofFreshAt(Proof proof, long now, long frequency)
        {
            return proof != null && frequency > 0 && proof.CapturedTick > 0 && proof.CapturedTick <= now &&
                (now - proof.CapturedTick) / (double)frequency <= 2;
        }
        internal static bool HoverProofFreshAt(bool isHover, Proof proof, long now, long frequency)
        {
            return isHover && ProofFreshAt(proof, now, frequency) &&
                (now - proof.CapturedTick) / (double)frequency <= 0.5;
        }
        internal static bool TryProve(TaskbarMover.Click button, IntPtr window, long started, IntPtr taskbar, out Proof proof)
        {
            proof = null;
            TaskbarTargets.NoteShellProofBegin(TaskbarTargets.RemainingProbeMilliseconds);
            if (TaskbarTargets.RemainingProbeMilliseconds < ReaderMilliseconds + 75) return ProofFailure(ShellProofOutcome.BudgetSkipped);
            uint process, shell; long shellStarted;
            if (button == null || !TaskbarNativeSelection.IsGeneratedIdentity(button.AppId)) return ProofFailure(ShellProofOutcome.NotApplicable);
            if (button.Grouped) return ProofFailure(ShellProofOutcome.Grouped);
            if (Native.GetWindowThreadProcessId(window, out process) == 0 || !TargetCurrent(window, process, started))
                return ProofFailure(ShellProofOutcome.TargetMismatch);
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Shell);
            if (!ReadTaskbar(taskbar, out shell, out shellStarted)) return ProofFailure(ShellProofOutcome.ShellMismatch);
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Cache);
            Proof cached;
            lock (readerGate) cached = hoverProof;
            if (HoverProofFreshAt(TaskbarTargets.IsHoverProbe, cached, Stopwatch.GetTimestamp(), Stopwatch.Frequency) &&
                ProofMatchesTarget(cached, window, process, started, taskbar, button.AppId) &&
                cached.ShellProcess == shell && cached.ShellStarted == shellStarted && ProofIsCurrent(cached)) {
                proof = cached;
                TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Complete);
                TaskbarTargets.NoteShellProofOutcome(ShellProofOutcome.CacheHit); return true;
            }
            string identity;
            ShellProofOutcome failure;
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Reader);
            if (!TryRead(window, process, started, out identity, out failure)) return ProofFailure(failure);
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Identity);
            if (!String.Equals(identity, button.AppId, StringComparison.Ordinal)) return ProofFailure(ShellProofOutcome.IdentityMismatch);
            var captured = new Proof(window, process, started, taskbar, shell, shellStarted, identity, Stopwatch.GetTimestamp());
            if (!ProofIsCurrent(captured)) return ProofFailure(ShellProofOutcome.RevalidationFailed);
            if (TaskbarTargets.IsHoverProbe) lock (readerGate) hoverProof = captured;
            proof = captured;
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Complete);
            TaskbarTargets.NoteShellProofOutcome(ShellProofOutcome.Proved); return true;
        }
        static bool ProofFailure(ShellProofOutcome outcome)
        { TaskbarTargets.NoteShellProofOutcome(outcome); return false; }
        internal static bool TryProveManagementConsole(TaskbarMover.Click button, TaskbarTargets.PreferredWindow preferred,
            IntPtr taskbar, out Proof proof)
        {
            proof = null;
            // Only the immutable preferred official MMC window is queried. Never
            // launch a resolver for each foreign window encountered by EnumWindows.
            if (preferred == null || !ManagementConsoleCurrent(preferred.Window, preferred.Process, preferred.ProcessStarted)) return false;
            TaskbarTargets.NoteShellProofBegin(TaskbarTargets.RemainingProbeMilliseconds);
            if (button == null || !ManagementConsoleIdentityAllowed(button.AppId)) return ProofFailure(ShellProofOutcome.NotApplicable);
            if (button.Grouped) return ProofFailure(ShellProofOutcome.Grouped);
            if (TaskbarTargets.RemainingProbeMilliseconds < ReaderMilliseconds + 75) return ProofFailure(ShellProofOutcome.BudgetSkipped);
            uint shell; long shellStarted;
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Shell);
            if (!ReadTaskbar(taskbar, out shell, out shellStarted)) return ProofFailure(ShellProofOutcome.ShellMismatch);
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Cache);
            Proof cached;
            lock (readerGate) cached = hoverProof;
            if (HoverProofFreshAt(TaskbarTargets.IsHoverProbe, cached, Stopwatch.GetTimestamp(), Stopwatch.Frequency) &&
                cached.ShellOnly && ProofMatchesTarget(cached, preferred.Window, preferred.Process, preferred.ProcessStarted, taskbar, button.AppId) &&
                cached.ShellProcess == shell && cached.ShellStarted == shellStarted && ProofIsCurrent(cached)) {
                proof = cached; TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Complete);
                TaskbarTargets.NoteShellProofOutcome(ShellProofOutcome.CacheHit); return true;
            }
            string digest; ShellProofOutcome failure;
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Reader);
            if (!TryRead(preferred.Window, preferred.Process, preferred.ProcessStarted, out digest, out failure, button.AppId)) return ProofFailure(failure);
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Identity);
            if (!ManagementConsoleDigestMatches(button.AppId, digest)) return ProofFailure(ShellProofOutcome.IdentityMismatch);
            var captured = new Proof(preferred.Window, preferred.Process, preferred.ProcessStarted, taskbar,
                shell, shellStarted, button.AppId, Stopwatch.GetTimestamp(), true);
            if (!ProofIsCurrent(captured)) return ProofFailure(ShellProofOutcome.RevalidationFailed);
            if (TaskbarTargets.IsHoverProbe) lock (readerGate) hoverProof = captured;
            proof = captured;
            TaskbarTargets.NoteShellProofPhase(ShellProofPhase.Complete);
            TaskbarTargets.NoteShellProofOutcome(ShellProofOutcome.Proved); return true;
        }
        internal static bool ManagementConsoleIdentityAllowed(string identity)
        {
            if (String.IsNullOrEmpty(identity) || identity.Length > 128) return false;
            for (int index = 0; index < identity.Length; index++) {
                char item = identity[index];
                if (Char.IsControl(item) || Char.IsWhiteSpace(item) || Char.IsLowSurrogate(item)) return false;
                if (Char.IsHighSurrogate(item)) {
                    if (index + 1 >= identity.Length || !Char.IsLowSurrogate(identity[index + 1])) return false;
                    index++;
                }
            }
            return true;
        }
        internal static string HashIdentity(string identity)
        {
            if (!ManagementConsoleIdentityAllowed(identity)) return null;
            using (var hash = SHA256.Create()) {
                var text = new StringBuilder(64);
                foreach (byte item in hash.ComputeHash(Encoding.UTF8.GetBytes(identity))) text.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
        internal static bool ManagementConsoleDigestMatches(string expected, string digest)
        { return digest != null && String.Equals(HashIdentity(expected), digest, StringComparison.Ordinal); }

        internal static bool ParseRequest(string[] args, out IntPtr window, out uint process, out long started)
        { return ParseRequestForMode(args, ReaderMode, out window, out process, out started); }
        internal static bool ParseManagementConsoleRequest(string[] args, out IntPtr window, out uint process, out long started)
        { return ParseRequestForMode(args, ManagementConsoleReaderMode, out window, out process, out started); }
        static bool ParseRequestForMode(string[] args, string mode, out IntPtr window, out uint process, out long started)
        {
            window = IntPtr.Zero; process = 0; started = 0; long rawWindow, rawStarted; uint rawProcess;
            if (args == null || args.Length != 4 || args[0] != mode ||
                !Int64.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out rawWindow) || rawWindow <= 0 ||
                !UInt32.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out rawProcess) || rawProcess == 0 ||
                !Int64.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out rawStarted) || rawStarted <= 0 ||
                args[1] != rawWindow.ToString(CultureInfo.InvariantCulture) || args[2] != rawProcess.ToString(CultureInfo.InvariantCulture) ||
                args[3] != rawStarted.ToString(CultureInfo.InvariantCulture)) return false;
            window = new IntPtr(rawWindow); process = rawProcess; started = rawStarted; return true;
        }
        internal static bool ParseReceipt(string stdout, string stderr, int exitCode, IntPtr window, uint process, long started, out string identity)
        {
            identity = null;
            if (exitCode != 0 || !String.IsNullOrEmpty(stderr) || stdout == null || stdout.Length > 256 ||
                window == IntPtr.Zero || process == 0 || started <= 0) return false;
            string value = stdout.EndsWith("\r\n", StringComparison.Ordinal) ? stdout.Substring(0, stdout.Length - 2) :
                stdout.EndsWith("\n", StringComparison.Ordinal) ? stdout.Substring(0, stdout.Length - 1) : stdout;
            string[] fields = value.Split('|');
            if (fields.Length != 5 || fields[0] != "TSM1" || fields[1] != window.ToInt64().ToString(CultureInfo.InvariantCulture) ||
                fields[2] != process.ToString(CultureInfo.InvariantCulture) || fields[3] != started.ToString(CultureInfo.InvariantCulture) ||
                !TaskbarNativeSelection.IsGeneratedIdentity(fields[4])) return false;
            identity = fields[4]; return true;
        }
        internal static bool ParseManagementConsoleReceipt(string stdout, string stderr, int exitCode, IntPtr window,
            uint process, long started, string expectedIdentity, out string identityDigest)
        {
            identityDigest = null;
            if (exitCode != 0 || !String.IsNullOrEmpty(stderr) || stdout == null || stdout.Length > 256 ||
                window == IntPtr.Zero || process == 0 || started <= 0 || !ManagementConsoleIdentityAllowed(expectedIdentity)) return false;
            string value = stdout.EndsWith("\r\n", StringComparison.Ordinal) ? stdout.Substring(0, stdout.Length - 2) :
                stdout.EndsWith("\n", StringComparison.Ordinal) ? stdout.Substring(0, stdout.Length - 1) : stdout;
            string[] fields = value.Split('|');
            if (fields.Length != 6 || fields[0] != "TSC1" || fields[1] != window.ToInt64().ToString(CultureInfo.InvariantCulture) ||
                fields[2] != process.ToString(CultureInfo.InvariantCulture) || fields[3] != started.ToString(CultureInfo.InvariantCulture) ||
                fields[5] != "1" || !ManagementConsoleDigestMatches(expectedIdentity, fields[4])) return false;
            identityDigest = fields[4]; return true;
        }
        static bool TryRead(IntPtr window, uint process, long started, out string identity, out ShellProofOutcome failure, string managementIdentity = null)
        {
            identity = null; failure = ShellProofOutcome.ReadError;
            var runtime = NativeProcessIdentity.Current;
            if (!runtime.IntegrityAvailable || !runtime.ElevationAvailable || runtime.Elevated || runtime.IntegrityLevel != NativeProcessIdentity.Medium) return false;
            int budget = Math.Min(ReaderMilliseconds, TaskbarTargets.RemainingProbeMilliseconds - 75);
            if (budget <= 0) { failure = ShellProofOutcome.BudgetSkipped; return false; }
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * budget / 1000;
            lock (readerGate) {
                if (readerBusy) { failure = ShellProofOutcome.ReaderBusy; return false; }
                if (uncollectedReader != null) {
                    if (!uncollectedReader.HasExited) { failure = ShellProofOutcome.ReaderBusy; return false; }
                    uncollectedReader.Dispose(); uncollectedReader = null;
                }
                readerBusy = true;
            }
            Process child = null;
            try {
                string image = Assembly.GetExecutingAssembly().Location;
                var info = new ProcessStartInfo(image) {
                    Arguments = (managementIdentity == null ? ReaderMode : ManagementConsoleReaderMode) + " " + window.ToInt64().ToString(CultureInfo.InvariantCulture) + " " +
                        process.ToString(CultureInfo.InvariantCulture) + " " + started.ToString(CultureInfo.InvariantCulture),
                    WorkingDirectory = Path.GetDirectoryName(image), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                child = Process.Start(info);
                if (child == null) return false;
                var output = ReadLimited(child.StandardOutput); var errors = ReadLimited(child.StandardError);
                int remaining = (int)Math.Max(0, (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency);
                if (remaining <= 0 || !child.WaitForExit(remaining)) {
                    failure = ShellProofOutcome.ReaderTimeout;
                    try { child.Kill(); child.WaitForExit(50); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                    return false;
                }
                if (!output.Wait(25) || !errors.Wait(25)) return false;
                bool parsed = managementIdentity == null ? ParseReceipt(output.Result, errors.Result, child.ExitCode, window, process, started, out identity) :
                    ParseManagementConsoleReceipt(output.Result, errors.Result, child.ExitCode, window, process, started, managementIdentity, out identity);
                if (!parsed) return false;
                if (!CurrentTarget(window, process, started, managementIdentity != null)) { failure = ShellProofOutcome.TargetMismatch; return false; }
                return true;
            }
            catch (Exception error) {
                if (error is OutOfMemoryException || error is StackOverflowException) throw;
                return false;
            }
            finally {
                lock (readerGate) {
                    if (child != null) {
                        bool exited;
                        try { exited = child.HasExited; } catch (InvalidOperationException) { exited = true; }
                        if (exited) child.Dispose();
                        else uncollectedReader = child; // No new child until its actual exit is confirmed.
                    }
                    readerBusy = false;
                }
            }
        }
        static async Task<string> ReadLimited(StreamReader reader)
        {
            var result = new StringBuilder(128); var buffer = new char[128];
            for (;;) {
                int count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count == 0) return result.ToString();
                if (result.Length + count > 256) throw new InvalidDataException("Oversized shell identity receipt.");
                result.Append(buffer, 0, count);
            }
        }
        internal sealed class WindowStamp
        {
            internal readonly IntPtr Window;
            internal readonly uint Process;
            internal readonly long Started;
            internal WindowStamp(IntPtr window, uint process, long started) { Window = window; Process = process; Started = started; }
        }
        const int MaximumBatchWindows = 64;
        delegate bool EnumWindow(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
        internal static bool SameWindowSet(WindowStamp[] first, WindowStamp[] second)
        {
            if (first == null || second == null || first.Length == 0 || first.Length > MaximumBatchWindows || first.Length != second.Length) return false;
            var seen = new HashSet<IntPtr>();
            foreach (var item in first) {
                if (item == null || item.Window == IntPtr.Zero || item.Process == 0 || item.Started <= 0 || !seen.Add(item.Window)) return false;
                int count = 0;
                foreach (var current in second)
                    if (current != null && current.Window == item.Window && current.Process == item.Process && current.Started == item.Started) count++;
                if (count != 1) return false;
            }
            return true;
        }
        internal static bool UniqueShellIdentity(string[] identities, string expected, int targetIndex)
        {
            if (identities == null || identities.Length == 0 || identities.Length > MaximumBatchWindows ||
                targetIndex < 0 || targetIndex >= identities.Length || !ManagementConsoleIdentityAllowed(expected)) return false;
            int count = 0;
            for (int index = 0; index < identities.Length; index++) {
                if (!ManagementConsoleIdentityAllowed(identities[index])) return false;
                if (String.Equals(identities[index], expected, StringComparison.Ordinal)) count++;
            }
            return count == 1 && String.Equals(identities[targetIndex], expected, StringComparison.Ordinal);
        }
        static WindowStamp[] SnapshotWindows()
        {
            bool failed = false;
            var result = new List<WindowStamp>();
            bool completed = EnumWindows(delegate(IntPtr window, IntPtr ignored) {
                if (!WindowShelfCatalog.IsTaskbarCandidate(window)) return true;
                uint process;
                if (result.Count >= MaximumBatchWindows || Native.GetWindowThreadProcessId(window, out process) == 0 || process == 0 || !SameSession(process)) {
                    failed = true; return false;
                }
                long started = NativeProcessIdentity.Started(process); uint current;
                if (started <= 0 || Native.GetWindowThreadProcessId(window, out current) == 0 || current != process ||
                    NativeProcessIdentity.Started(process) != started) { failed = true; return false; }
                result.Add(new WindowStamp(window, process, started)); return true;
            }, IntPtr.Zero);
            return completed && !failed ? result.ToArray() : null;
        }
        static bool StampCurrent(WindowStamp stamp)
        {
            uint process;
            return stamp != null && WindowShelfCatalog.IsTaskbarCandidate(stamp.Window) &&
                Native.GetWindowThreadProcessId(stamp.Window, out process) != 0 && process == stamp.Process &&
                SameSession(process) && NativeProcessIdentity.Started(process) == stamp.Started;
        }
        static string ReadShellIdentity(IApplicationResolver2 resolver, IntPtr window)
        {
            IntPtr text = IntPtr.Zero;
            try {
                int prevented, explicitId, embedded;
                if (resolver.GetAppIDForWindow(window, out text, out prevented, out explicitId, out embedded) != 0 || text == IntPtr.Zero) return null;
                var identity = new StringBuilder(128);
                for (int index = 0; index <= 128; index++) {
                    char item = (char)Marshal.ReadInt16(text, index * 2);
                    if (item == 0) return ManagementConsoleIdentityAllowed(identity.ToString()) ? identity.ToString() : null;
                    identity.Append(item);
                }
                return null;
            } finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
        }
        static string ResolveUniqueManagementConsole(IApplicationResolver2 resolver, IntPtr window, uint process, long started)
        {
            if (!ManagementConsoleCurrent(window, process, started)) return null;
            WindowStamp[] before = SnapshotWindows();
            if (before == null || before.Length == 0) return null;
            var identities = new string[before.Length]; int targetIndex = -1;
            for (int index = 0; index < before.Length; index++) {
                var item = before[index];
                if (!StampCurrent(item)) return null;
                identities[index] = ReadShellIdentity(resolver, item.Window);
                if (!ManagementConsoleIdentityAllowed(identities[index]) || !StampCurrent(item)) return null;
                if (item.Window == window && item.Process == process && item.Started == started) targetIndex = index;
            }
            if (targetIndex < 0 || !UniqueShellIdentity(identities, identities[targetIndex], targetIndex) ||
                !SameWindowSet(before, SnapshotWindows()) || !ManagementConsoleCurrent(window, process, started)) return null;
            // The child never emits arbitrary console paths or application IDs.
            return HashIdentity(identities[targetIndex]);
        }
        static bool DigestAllowed(string digest)
        {
            if (digest == null || digest.Length != 64) return false;
            foreach (char item in digest) if (!((item >= '0' && item <= '9') || (item >= 'a' && item <= 'f'))) return false;
            return true;
        }

        internal static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 2;
            if (args == null || args.Length == 0 || (args[0] != ReaderMode && args[0] != ManagementConsoleReaderMode)) return false;
            // A parent crash must not orphan an undocumented COM query forever.
            // This process is read-only; self-expiry has no restoration obligation.
            using (var expiry = new Timer(delegate { Environment.Exit(3); }, null, 500, Timeout.Infinite)) {
                IntPtr window; uint process; long started;
                bool managementConsole = args[0] == ManagementConsoleReaderMode;
                var runtime = NativeProcessIdentity.Current;
                if (IntPtr.Size != 8 || !runtime.IntegrityAvailable || !runtime.ElevationAvailable || runtime.Elevated ||
                    runtime.IntegrityLevel != NativeProcessIdentity.Medium ||
                    !ParseRequestForMode(args, managementConsole ? ManagementConsoleReaderMode : ReaderMode, out window, out process, out started) ||
                    !CurrentTarget(window, process, started, managementConsole)) return true;
                string identity = ResolveWindow(window, process, started, managementConsole);
                if (managementConsole ? !DigestAllowed(identity) : !TaskbarNativeSelection.IsGeneratedIdentity(identity)) return true;
                if (!CurrentTarget(window, process, started, managementConsole)) return true;
                Console.WriteLine((managementConsole ? "TSC1|" : "TSM1|") + window.ToInt64().ToString(CultureInfo.InvariantCulture) + "|" + process.ToString(CultureInfo.InvariantCulture) +
                    "|" + started.ToString(CultureInfo.InvariantCulture) + "|" + identity + (managementConsole ? "|1" : ""));
                exitCode = 0; return true;
            }
        }

        // The undocumented v2 interface is deliberately confined to this ordinary
        // expendable process. ABI reference (declarations only): System Informer,
        // phlib/include/appresolverp.h and phlib/appresolver.c. No third-party DLL.
        [ComImport, Guid("DE25675A-72DE-44B4-9373-05170450C140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationResolver2
        {
            [PreserveSig] int GetAppIDForShortcut(IntPtr item, out IntPtr identity);
            [PreserveSig] int GetAppIDForShortcutObject(IntPtr link, IntPtr item, out IntPtr identity);
            [PreserveSig] int GetAppIDForWindow(IntPtr window, out IntPtr identity, out int prevented, out int explicitId, out int embedded);
        }
        [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IClassFactory
        {
            [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result);
            [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetClassObject(ref Guid clsid, ref Guid iid, out IntPtr result);
        static string ResolveWindow(IntPtr window, uint process, long started, bool managementConsole = false)
        {
            IntPtr module = IntPtr.Zero, factoryPointer = IntPtr.Zero, resolverPointer = IntPtr.Zero;
            object factory = null, resolver = null;
            try {
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "appresolver.dll");
                module = LoadLibraryEx(expected, IntPtr.Zero, 0x800); // Dependencies restricted to System32.
                if (module == IntPtr.Zero) return null;
                var loaded = new StringBuilder(32768);
                if (GetModuleFileName(module, loaded, loaded.Capacity) == 0 || !String.Equals(loaded.ToString(), expected, StringComparison.OrdinalIgnoreCase)) return null;
                IntPtr entry = GetProcAddress(module, "DllGetClassObject");
                if (entry == IntPtr.Zero) return null;
                var getFactory = (GetClassObject)Marshal.GetDelegateForFunctionPointer(entry, typeof(GetClassObject));
                var clsid = new Guid("660B90C8-73A9-4B58-8CAE-355B7F55341B");
                var factoryIid = new Guid("00000001-0000-0000-C000-000000000046");
                var resolverIid = new Guid("DE25675A-72DE-44B4-9373-05170450C140");
                if (getFactory(ref clsid, ref factoryIid, out factoryPointer) < 0 || factoryPointer == IntPtr.Zero) return null;
                factory = Marshal.GetObjectForIUnknown(factoryPointer);
                if (((IClassFactory)factory).CreateInstance(IntPtr.Zero, ref resolverIid, out resolverPointer) < 0 || resolverPointer == IntPtr.Zero) return null;
                resolver = Marshal.GetObjectForIUnknown(resolverPointer);
                if (!CurrentTarget(window, process, started, managementConsole)) return null;
                if (managementConsole) return ResolveUniqueManagementConsole((IApplicationResolver2)resolver, window, process, started);
                IntPtr text = IntPtr.Zero;
                try {
                    int prevented, explicitId, embedded;
                    int result = ((IApplicationResolver2)resolver).GetAppIDForWindow(window, out text, out prevented, out explicitId, out embedded);
                    if (result != 0 || text == IntPtr.Zero || explicitId != 0 || !TargetCurrent(window, process, started)) return null;
                    // Read only the fixed canonical ID length plus its terminator.
                    var identity = new StringBuilder(64);
                    for (int index = 0; index <= 62; index++) {
                        char item = (char)Marshal.ReadInt16(text, index * 2);
                        if (item == 0) return TaskbarNativeSelection.IsGeneratedIdentity(identity.ToString()) ? identity.ToString() : null;
                        identity.Append(item);
                    }
                    return null;
                } finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
            }
            catch (Exception error) {
                if (error is OutOfMemoryException || error is StackOverflowException) throw;
                return null;
            }
            finally {
                if (resolver != null && Marshal.IsComObject(resolver)) Marshal.FinalReleaseComObject(resolver);
                if (resolverPointer != IntPtr.Zero) Marshal.Release(resolverPointer);
                if (factory != null && Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
                if (factoryPointer != IntPtr.Zero) Marshal.Release(factoryPointer);
                // Keep the trusted module loaded until this bounded process exits:
                // COM may have apartment cleanup queued after RCW release.
            }
        }
    }
}
