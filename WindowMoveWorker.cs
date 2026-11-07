using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // A helper bounds the caller's wait, not a foreign window's transaction.
    // Preserve recovery for an attempted operation whose outcome is uncertain.
    internal static class WindowMoveWorker
    {
        internal enum Outcome { Success, Rejected, Failed, TimedOut, Cancelled }
        internal sealed class Result
        {
            internal Outcome Outcome;
            internal bool MutationAttempted, CallerExited;
            internal int MinimumWidth, MinimumHeight, NativeError;
            // Diagnostic observations only. They never authorize a native call.
            internal bool FocusGrantAttempted, FocusGrantSucceeded, WorkerExitKnown;
            internal int FocusGrantError, WorkerReply = -1, WorkerExitCode;
            internal uint WorkerProcess;
            internal long WorkerStarted;
        }
        internal sealed class FocusTrace
        {
            internal long Generation;
            internal IntPtr Window;
            internal uint Process;
            internal long Started;
            internal Result Result;
        }
        sealed class Lease
        {
            internal readonly object Gate = new object();
            internal Process Child;
            internal bool Started, Ready, LaunchComplete, CollectorComplete, LaunchFailed, Retired, Released, Exited, Authorized, Quarantined, ExitCodeKnown;
            internal int Output = -1, ExitCode;
            internal bool ReadOnly;
            internal int MinimumWidth, MinimumHeight, NativeError;
            internal bool FocusGrantAttempted, FocusGrantSucceeded;
            internal int FocusGrantError;
            internal uint WorkerProcess;
            internal long WorkerStarted;
            internal StreamWriter Input;
            internal StreamReader Reader;
            internal System.Threading.Timer Cleanup;
        }
        sealed class Authorization
        {
            internal volatile bool Complete;
            internal bool Accepted;
        }
        // Only these two fixed state changes use this boundary. Tests inject
        // USER observations without creating or changing desktop windows.
        internal sealed class StateApi
        {
            internal Func<IntPtr> Foreground = FocusedWindowNative.Foreground;
            internal Func<IntPtr, FocusedWindowSnapshot> Read = delegate(IntPtr w) { return FocusedWindowNative.Read(w, true); };
            internal Func<IntPtr, bool> Minimize = WindowStateNative.Minimize;
            internal Func<IntPtr, bool, bool> SetTopmost = WindowStateNative.SetTopmost;
            internal Func<IntPtr, bool> IsMinimized = WindowStateNative.IsMinimized;
            internal Func<IntPtr, bool> IsTopmost = WindowStateNative.IsTopmost;
            internal Func<long> Now = Stopwatch.GetTimestamp;
            internal Action Pause = delegate { Thread.Sleep(2); };
        }
        const string Mode = "--window-move-worker";
        const int Limit = 2;
        internal static AdministratorSupport Administrator;
        static readonly object budgetGate = new object();
        static readonly object focusTraceGate = new object();
        static long focusTraceGeneration;
        static FocusTrace lastFocusTrace;
        internal static FocusTrace LastFocusTrace { get { lock (focusTraceGate) return lastFocusTrace; } }
        internal static long ClearLastFocusTrace()
        { lock (focusTraceGate) { lastFocusTrace = null; return ++focusTraceGeneration; } }
        static int activeHelpers;
        static int uncertainHelpers;
        internal static int ActiveHelpers { get { return Interlocked.CompareExchange(ref activeHelpers, 0, 0); } }
        internal static int UncertainHelpers { get { return Interlocked.CompareExchange(ref uncertainHelpers, 0, 0); } }
        static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr window, ref WindowHistory.Placement placement);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr parameter, IntPtr detail);
        [DllImport("user32.dll", SetLastError = true)] static extern bool AllowSetForegroundWindow(uint process);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint exitCode);

        internal static bool TryRun(string[] args)
        {
            if (args == null || args.Length == 0 || args[0] != Mode) return false;
            int output = 0;
            bool attempted = false;
            int requestedOperation;
            bool minimumQuery = args.Length > 1 && Int32.TryParse(args[1], NumberStyles.Integer, Numbers, out requestedOperation) && requestedOperation == 3;
            WindowTileMinimum minimum = null;
            System.Threading.Timer deadlineWatch = null;
            long deadline;
            if (args.Length >= 6 && Int64.TryParse(args[5], NumberStyles.Integer, Numbers, out deadline) && deadline > Stopwatch.GetTimestamp()) {
                int due = (int)Math.Min(Int32.MaxValue, Math.Ceiling((deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency));
                deadlineWatch = new System.Threading.Timer(delegate(object state) {
                    if (Stopwatch.GetTimestamp() >= (long)state) TerminateProcess(GetCurrentProcess(), 3);
                }, deadline, Math.Max(1, due), 25);
            }
            // This helper dispatches before the normal app's DPI setup. USER's
            // tracking sizes must use the same physical coordinate space.
            if (minimumQuery) try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { }
            try { output = Execute(args, ref attempted, out minimum); }
            catch (Exception) { output = minimumQuery || attempted ? 2 : 0; }
            finally { if (deadlineWatch != null) deadlineWatch.Dispose(); }
            // 0 proves rejection before the call, 1 success, 2 attempted failure.
            Environment.ExitCode = 0;
            try {
                if (minimumQuery) Console.Out.WriteLine(output.ToString(Numbers) + " " +
                    (minimum == null ? 0 : minimum.Minimum.Width).ToString(Numbers) + " " +
                    (minimum == null ? 0 : minimum.Minimum.Height).ToString(Numbers) + " " +
                    (minimum == null ? 0 : minimum.Error).ToString(Numbers));
                else Console.Out.WriteLine(output.ToString(Numbers));
                Console.Out.Flush();
            }
            catch (Exception) { }
            return true;
        }
        static int Execute(string[] args, ref bool attempted, out WindowTileMinimum minimum)
        {
            minimum = null;
            int operation;
            long handle, started, deadline;
            uint process;
            if (args.Length < 6 || !Int32.TryParse(args[1], NumberStyles.Integer, Numbers, out operation) ||
                !Int64.TryParse(args[2], NumberStyles.Integer, Numbers, out handle) || handle == 0 ||
                !UInt32.TryParse(args[3], NumberStyles.Integer, Numbers, out process) || process == 0 ||
                !Int64.TryParse(args[4], NumberStyles.Integer, Numbers, out started) || started <= 0 ||
                !Int64.TryParse(args[5], NumberStyles.Integer, Numbers, out deadline) || deadline <= Stopwatch.GetTimestamp()) return 0;
            var values = new int[args.Length - 6];
            for (int index = 0; index < values.Length; index++)
                if (!Int32.TryParse(args[6 + index], NumberStyles.Integer, Numbers, out values[index])) return 0;
            if (!AdministratorSupport.ValidValues(operation, handle, process, started, values)) return 0;
            // Launch happens off the UI thread. It must not authorize an action
            // after cancellation merely because Process.Start returned late.
            if (!Authorized(deadline) || deadline <= Stopwatch.GetTimestamp()) return 0;
            var window = new IntPtr(handle);
            uint actual;
            if (!IsWindow(window) || GetWindowThreadProcessId(window, out actual) == 0 || actual != process) return 0;
            if (NativeProcessIdentity.Started(process) != started) return 0;
            if (deadline <= Stopwatch.GetTimestamp() || !IsWindow(window) ||
                GetWindowThreadProcessId(window, out actual) == 0 || actual != process) return 0;
            if (operation == 3) {
                minimum = WindowTilingNative.ReadMinimum(window);
                return minimum != null && minimum.Available && minimum.Minimum.Width > 0 && minimum.Minimum.Height > 0 &&
                    deadline > Stopwatch.GetTimestamp() ? 1 : 2;
            }
            if (operation == 5 || operation == 6)
                return StateOperation(operation, window, process, started, deadline, values, ref attempted, new StateApi());
            if (operation == 4) {
                var focused = FocusedWindowNative.Read(window);
                if (!FocusedWindowPolicy.Eligible(focused, delegate { return false; }) ||
                    focused.Process != process || NativeProcessIdentity.Started(process) != started ||
                    FocusedWindowNative.Foreground() != window || deadline <= Stopwatch.GetTimestamp()) return 0;
            }
            attempted = true;
            bool succeeded;
            if (operation == 0)
                succeeded = SetWindowPos(window, IntPtr.Zero, values[0], values[1], values[2], values[3], (uint)values[4]);
            else if (operation == 1) {
                var placement = new WindowHistory.Placement { Length = Marshal.SizeOf(typeof(WindowHistory.Placement)),
                    Flags = values[0], ShowCommand = values[1],
                    MinPosition = new WindowHistory.Point { X = values[2], Y = values[3] },
                    MaxPosition = new WindowHistory.Point { X = values[4], Y = values[5] },
                    NormalPosition = new Native.Rect { Left = values[6], Top = values[7], Right = values[8], Bottom = values[9] } };
                succeeded = SetWindowPlacement(window, ref placement);
            } else if (operation == 2) succeeded = SetForegroundWindow(window);
            else succeeded = PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return succeeded ? 1 : 2;
        }
        internal static bool StateIdentity(FocusedWindowSnapshot original, FocusedWindowSnapshot current)
        { return WindowStatePolicy.SameIdentity(original, current); }
        internal static int StateOperation(int operation, IntPtr window, uint process, long started, long deadline,
            int[] values, ref bool attempted, StateApi api)
        {
            if ((operation != 5 && operation != 6) || api == null ||
                !AdministratorSupport.ValidValues(operation, window.ToInt64(), process, started, values) || api.Now() >= deadline) return 0;
            var original = api.Read(window);
            if (!WindowStatePolicy.Eligible(original, delegate { return false; }) || original.Window != window ||
                original.Process != process || WindowStatePolicy.WorkerStarted(original) != started || api.Foreground() != window) return 0;
            if (operation == 6 && WindowStatePolicy.Topmost(original) == (values[0] == 1)) return 0;
            var current = api.Read(window);
            if (!WindowStatePolicy.Eligible(current, delegate { return false; }) || !StateIdentity(original, current) ||
                api.Now() >= deadline || api.Foreground() != window) return 0;
            if (operation == 5 && api.IsMinimized(window)) return 0;
            if (operation == 6) {
                // The fixed desired value never adopts a window that became
                // topmost independently while this request was being admitted.
                if (api.IsTopmost(window) == (values[0] == 1)) return 0;
                current = api.Read(window);
                if (!WindowStatePolicy.SameIdentity(original, current) || WindowStatePolicy.Topmost(current) == (values[0] == 1) ||
                    api.Now() >= deadline || api.Foreground() != window) return 0;
            }
            attempted = true;
            if (!(operation == 5 ? api.Minimize(window) : api.SetTopmost(window, values[0] == 1))) return 2;
            // Minimize deliberately changes foreground. Completion verifies
            // the captured identity and native state, never a new foreground.
            for (int pass = 0; pass < 1000 && api.Now() < deadline; pass++) {
                bool reached = operation == 5 ? api.IsMinimized(window) : api.IsTopmost(window) == (values[0] == 1);
                current = api.Read(window);
                if (!StateIdentity(original, current)) return 2;
                if (reached && api.Now() < deadline) return 1;
                api.Pause();
            }
            return 2;
        }
        static bool Authorized(long deadline)
        {
            var input = new Authorization();
            var reader = new Thread(delegate() {
                try {
                    using (var stream = Console.OpenStandardInput()) {
                        int token = stream.ReadByte(), end = stream.ReadByte();
                        if (end == 13) end = stream.ReadByte();
                        input.Accepted = token == 49 && end == 10;
                    }
                }
                catch (Exception) { }
                finally { input.Complete = true; }
            });
            reader.IsBackground = true; reader.Name = "Window move authorization"; reader.Start();
            while (!input.Complete && Stopwatch.GetTimestamp() < deadline) Thread.Sleep(5);
            // A failed launch can leave stdin open without giving the parent a
            // process handle. This child still expires without ever invoking USER.
            return input.Complete && input.Accepted && Stopwatch.GetTimestamp() < deadline;
        }

        internal static Result Position(IntPtr window, uint process, long started, int x, int y, int width, int height,
            uint flags, long deadline, Func<bool> stillValid)
        {
            if (flags > Int32.MaxValue) return Rejected();
            return Run(0, window, process, started, deadline, stillValid, new[] { x, y, width, height, (int)flags });
        }
        internal static Result Placement(IntPtr window, uint process, long started, WindowHistory.Placement placement,
            long deadline, Func<bool> stillValid)
        {
            return Run(1, window, process, started, deadline, stillValid, new[] { placement.Flags, placement.ShowCommand,
                placement.MinPosition.X, placement.MinPosition.Y, placement.MaxPosition.X, placement.MaxPosition.Y,
                placement.NormalPosition.Left, placement.NormalPosition.Top, placement.NormalPosition.Right, placement.NormalPosition.Bottom });
        }
        internal static Result Foreground(IntPtr window, uint process, long started, long deadline, Func<bool> stillValid)
        {
            long generation = ClearLastFocusTrace();
            Result result = Run(2, window, process, started, deadline, stillValid, new int[0]);
            lock (focusTraceGate) if (generation == focusTraceGeneration)
                lastFocusTrace = new FocusTrace { Generation = generation, Window = window, Process = process, Started = started, Result = result };
            return result;
        }
        internal static Result Minimum(IntPtr window, uint process, long started, long deadline, Func<bool> stillValid)
        { return Run(3, window, process, started, deadline, stillValid, new int[0]); }
        internal static Result Close(IntPtr window, uint process, long started, long deadline, Func<bool> stillValid)
        { return Run(4, window, process, started, deadline, stillValid, new int[0]); }
        internal static Result Minimize(IntPtr window, uint process, long started, long deadline, Func<bool> stillValid)
        { return Run(5, window, process, started, deadline, stillValid, new int[0]); }
        internal static Result SetTopmost(IntPtr window, uint process, long started, long deadline, Func<bool> stillValid, bool desired)
        { return Run(6, window, process, started, deadline, stillValid, new[] { desired ? 1 : 0 }); }
        static Result Rejected() { return new Result { Outcome = Outcome.Rejected, CallerExited = true }; }
        static bool Valid(Func<bool> stillValid)
        {
            try { return stillValid == null || stillValid(); }
            catch (Exception) { return false; }
        }
        static bool Reserve()
        {
            lock (budgetGate) {
                if (ActiveHelpers >= Limit || UncertainHelpers != 0) return false;
                Interlocked.Increment(ref activeHelpers); return true;
            }
        }
        static string Arguments(int operation, IntPtr window, uint process, long started, long deadline, int[] values)
        {
            var args = new string[6 + values.Length];
            args[0] = Mode; args[1] = operation.ToString(Numbers); args[2] = window.ToInt64().ToString(Numbers);
            args[3] = process.ToString(Numbers); args[4] = started.ToString(Numbers); args[5] = deadline.ToString(Numbers);
            for (int index = 0; index < values.Length; index++) args[6 + index] = values[index].ToString(Numbers);
            return String.Join(" ", args);
        }
        static Result Run(int operation, IntPtr window, uint process, long started, long deadline, Func<bool> stillValid, int[] values)
        {
            if (deadline <= Stopwatch.GetTimestamp()) return new Result { Outcome = Outcome.TimedOut, CallerExited = true };
            if (!Valid(stillValid)) return new Result { Outcome = Outcome.Cancelled, CallerExited = true };
            if (!AdministratorSupport.ValidValues(operation, window.ToInt64(), process, started, values)) return Rejected();
            AdministratorSupport administrator = Administrator;
            if (operation != 3 && administrator != null && administrator.HasUncertainOperation) return Rejected();
            if (NativeProcessIdentity.RequiresAdministrator(process)) {
                if (administrator == null || UncertainHelpers != 0) return Rejected();
                return administrator.Execute(operation, window, process, started, deadline, stillValid, values);
            }
            return RunLocal(operation, window, process, started, deadline, stillValid, values);
        }
        internal static Result RunLocal(int operation, IntPtr window, uint process, long started, long deadline, Func<bool> stillValid, int[] values)
        {
            if (deadline <= Stopwatch.GetTimestamp()) return new Result { Outcome = Outcome.TimedOut, CallerExited = true };
            if (!Valid(stillValid)) return new Result { Outcome = Outcome.Cancelled, CallerExited = true };
            if (!AdministratorSupport.ValidValues(operation, window.ToInt64(), process, started, values) || !Reserve()) return Rejected();
            var lease = new Lease { ReadOnly = operation == 3 };
            string arguments = Arguments(operation, window, process, started, deadline, values);
            try {
                var launcher = new Thread(delegate() { Launch(lease, arguments); });
                launcher.IsBackground = true; launcher.Name = "Window move helper launcher";
                launcher.SetApartmentState(ApartmentState.MTA); launcher.Start();
            }
            catch (Exception) { lock (lease.Gate) { lease.LaunchComplete = true; lease.CollectorComplete = true; lease.LaunchFailed = true; } }
            while (true) {
                if (Stopwatch.GetTimestamp() >= deadline) return Finish(lease, Outcome.TimedOut);
                if (!Valid(stillValid)) return Finish(lease, Outcome.Cancelled);
                bool ready, failed, complete, authorized;
                int output;
                lock (lease.Gate) {
                    ready = lease.Ready; failed = lease.LaunchFailed; complete = lease.LaunchComplete;
                    authorized = lease.Authorized; output = lease.Output;
                }
                if (complete && failed) return Finish(lease, Outcome.Rejected);
                if (Exited(lease) && output >= 0)
                    return Finish(lease, Completion(lease, output));
                if (ready && !authorized) {
                    if (Stopwatch.GetTimestamp() >= deadline) return Finish(lease, Outcome.TimedOut);
                    if (!Valid(stillValid)) return Finish(lease, Outcome.Cancelled);
                    bool admitted = false;
                    try {
                        lock (budgetGate) {
                            if (UncertainHelpers == 0) lock (lease.Gate) {
                                if (operation == 2) {
                                    lease.WorkerProcess = (uint)lease.Child.Id;
                                    lease.WorkerStarted = NativeProcessIdentity.Started(lease.WorkerProcess);
                                    lease.FocusGrantAttempted = true;
                                    lease.FocusGrantSucceeded = AllowSetForegroundWindow(lease.WorkerProcess);
                                    lease.FocusGrantError = lease.FocusGrantSucceeded ? 0 : Marshal.GetLastWin32Error();
                                }
                                lease.Authorized = true; admitted = true;
                                lease.Input.WriteLine("1"); lease.Input.Flush();
                            }
                        }
                    }
                    catch (Exception) { return Finish(lease, Outcome.Failed); }
                    if (!admitted) return Finish(lease, Outcome.Rejected);
                }
                // Collected stdout may arrive after HasExited, so keep its wait inside
                // the same budget rather than using unbounded WaitForExit/ReadToEnd.
                Application.DoEvents();
                if (Stopwatch.GetTimestamp() >= deadline) return Finish(lease, Outcome.TimedOut);
                if (!Valid(stillValid)) return Finish(lease, Outcome.Cancelled);
                Thread.Sleep(5);
            }
        }
        static void Launch(Lease lease, string arguments)
        {
            try {
                ProcessStartInfo start = AdministratorPayloadEntry.IsPayloadProcess ? AdministratorLaunch.CreateWorkerStartInfo(arguments) : new ProcessStartInfo {
                    FileName = typeof(WindowMoveWorker).Assembly.Location, Arguments = arguments, UseShellExecute = false,
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true };
                var child = new Process { StartInfo = start };
                lock (lease.Gate) lease.Child = child;
                if (!child.Start()) throw new InvalidOperationException("Could not start the window move helper");
                lock (lease.Gate) lease.Started = true;
                var input = child.StandardInput;
                lock (lease.Gate) lease.Input = input;
                var reader = child.StandardOutput;
                lock (lease.Gate) { lease.Reader = reader; lease.Ready = true; lease.LaunchComplete = true; }
                // Never use ReadLine, whose buffering is unbounded. Mutation
                // operations retain their original three-byte token protocol;
                // a minimum-size query has a separate capped numeric payload.
                var stream = reader.BaseStream;
                if (lease.ReadOnly) {
                    var payload = new byte[48]; int length = 0;
                    while (length < payload.Length) {
                        int next = stream.ReadByte();
                        if (next < 0) break;
                        payload[length++] = (byte)next;
                        if (next == 10) break;
                    }
                    int output, width, height, error;
                    bool parsed = ParseMinimumOutput(payload, length, out output, out width, out height, out error);
                    lock (lease.Gate) {
                        lease.Output = parsed ? output : 3;
                        lease.MinimumWidth = parsed ? width : 0; lease.MinimumHeight = parsed ? height : 0;
                        lease.NativeError = parsed ? error : 0;
                    }
                } else {
                    int token = stream.ReadByte(), end = stream.ReadByte();
                    if (end == 13) end = stream.ReadByte();
                    lock (lease.Gate) lease.Output = end == 10 && token >= 48 && token <= 50 ? token - 48 : 3;
                }
            }
            catch (Exception) {
                lock (lease.Gate) {
                    if (lease.Started) lease.Output = 3;
                    else lease.LaunchFailed = true;
                }
            }
            finally {
                bool retired;
                lock (lease.Gate) { lease.LaunchComplete = true; lease.CollectorComplete = true; retired = lease.Retired; }
                if (retired) { Kill(lease); StartCleanup(lease); }
            }
        }
        internal static bool ParseMinimumOutput(byte[] payload, int length, out int output, out int width, out int height, out int error)
        {
            output = width = height = error = 0;
            if (payload == null || length < 1 || length > 48 || length > payload.Length || payload[length - 1] != 10) return false;
            int contentLength = length - 1;
            if (contentLength > 0 && payload[contentLength - 1] == 13) contentLength--;
            for (int i = 0; i < contentLength; i++)
                if (payload[i] != 32 && payload[i] != 43 && payload[i] != 45 && (payload[i] < 48 || payload[i] > 57)) return false;
            string[] values = Encoding.ASCII.GetString(payload, 0, contentLength).Split(' ');
            if (values.Length != 4 || !Int32.TryParse(values[0], NumberStyles.AllowLeadingSign, Numbers, out output) || output < 0 || output > 2 ||
                !Int32.TryParse(values[1], NumberStyles.AllowLeadingSign, Numbers, out width) ||
                !Int32.TryParse(values[2], NumberStyles.AllowLeadingSign, Numbers, out height) ||
                !Int32.TryParse(values[3], NumberStyles.AllowLeadingSign, Numbers, out error) || width < 0 || height < 0 || error < 0) return false;
            return output != 1 || (width > 0 && height > 0 && error == 0);
        }
        static Outcome Completion(Lease lease, int output)
        {
            lock (lease.Gate) {
                // Main returns zero for the recognized helper mode. Native results
                // use the bounded token; an abnormal process exit is indeterminate.
                if (!lease.ExitCodeKnown || lease.ExitCode != 0) return Outcome.Failed;
                return output == 1 ? Outcome.Success : output == 0 ? Outcome.Rejected : Outcome.Failed;
            }
        }
        static bool Exited(Lease lease)
        {
            lock (lease.Gate) {
                if (lease.Released || lease.Exited) return true;
                if (!lease.LaunchComplete) return false;
                if (!lease.Started) { lease.Exited = true; lease.ExitCodeKnown = true; return true; }
                try {
                    if (lease.Child.HasExited) {
                        lease.Exited = true;
                        try { lease.ExitCode = lease.Child.ExitCode; lease.ExitCodeKnown = true; }
                        catch (InvalidOperationException) { }
                        catch (Win32Exception) { }
                        return true;
                    }
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                return false;
            }
        }
        static void Kill(Lease lease)
        {
            lock (lease.Gate) {
                if (lease.Released || !lease.Started || lease.Exited) return;
                try { if (!lease.Child.HasExited) lease.Child.Kill(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }
        static Result Finish(Lease lease, Outcome outcome)
        {
            lock (budgetGate) lock (lease.Gate) {
                lease.Retired = true;
                if (!lease.Released && !lease.Exited && lease.Authorized && !lease.Quarantined) {
                    lease.Quarantined = true; Interlocked.Increment(ref uncertainHelpers);
                }
            }
            if (!Exited(lease)) Kill(lease);
            var cleanup = Stopwatch.StartNew();
            while (!Exited(lease) && cleanup.ElapsedMilliseconds < 100) Thread.Sleep(5);
            bool exited = Exited(lease), attempted;
            lock (lease.Gate) attempted = !lease.ReadOnly && lease.Authorized && !(lease.Output == 0 && exited && lease.ExitCodeKnown && lease.ExitCode == 0);
            Release(lease);
            lock (lease.Gate) { if (!lease.Released) StartCleanup(lease); }
            lock (lease.Gate) return new Result { Outcome = outcome, MutationAttempted = attempted, CallerExited = exited,
                MinimumWidth = lease.MinimumWidth, MinimumHeight = lease.MinimumHeight, NativeError = lease.NativeError,
                FocusGrantAttempted = lease.FocusGrantAttempted, FocusGrantSucceeded = lease.FocusGrantSucceeded,
                FocusGrantError = lease.FocusGrantError, WorkerProcess = lease.WorkerProcess, WorkerStarted = lease.WorkerStarted,
                WorkerReply = lease.Output, WorkerExitKnown = lease.ExitCodeKnown, WorkerExitCode = lease.ExitCode };
        }
        static void StartCleanup(Lease lease)
        {
            lock (lease.Gate) {
                if (lease.Released || lease.Cleanup != null) return;
                lease.Cleanup = new System.Threading.Timer(delegate(object state) {
                    var retired = (Lease)state;
                    if (Exited(retired)) Release(retired);
                    else Kill(retired);
                }, lease, 25, 25);
            }
        }
        static void Release(Lease lease)
        {
            Process child;
            System.Threading.Timer cleanup;
            StreamWriter input;
            StreamReader reader;
            bool quarantined;
            lock (lease.Gate) {
                if (lease.Released || !lease.Exited || !lease.CollectorComplete) return;
                lease.Released = true; child = lease.Child; lease.Child = null;
                cleanup = lease.Cleanup; lease.Cleanup = null;
                input = lease.Input; lease.Input = null; reader = lease.Reader; lease.Reader = null;
                quarantined = lease.Quarantined; lease.Quarantined = false;
            }
            try {
                Close(cleanup);
                // Framework Process.Close does not close redirected streams.
                Close(input); Close(reader);
                if (child != null) {
                    if (input == null) try { Close(child.StandardInput); } catch (InvalidOperationException) { }
                    if (reader == null) try { Close(child.StandardOutput); } catch (InvalidOperationException) { }
                    Close(child);
                }
            }
            finally {
                lock (budgetGate) {
                    if (quarantined) Interlocked.Decrement(ref uncertainHelpers);
                    Interlocked.Decrement(ref activeHelpers);
                }
            }
        }
        static void Close(IDisposable value)
        {
            if (value == null) return;
            try { value.Dispose(); }
            catch (Exception) { }
        }
    }
}
