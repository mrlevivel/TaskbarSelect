using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // The worker pumps STA messages while minimize is in flight. Keep one Undo
    // request behind publication of that minimize's confirmed history entry.
    internal sealed class MinimizeActionGate
    {
        internal bool Pending { get; private set; }
        internal bool UndoQueued { get; private set; }
        internal bool TryBegin(bool blocked)
        {
            if (blocked || Pending) return false;
            Pending = true; UndoQueued = false; return true;
        }
        internal bool QueueUndo()
        { if (!Pending) return false; UndoQueued = true; return true; }
        internal bool Complete(bool recorded)
        {
            bool restore = Pending && UndoQueued && recorded;
            Pending = UndoQueued = false;
            return restore;
        }
    }

    internal sealed partial class TaskbarMover : IDisposable, AdministratorSupport.IValidityDiagnostic
    {
        const int WhMouseLowLevel = 14, MouseMove = 0x0200, LeftButtonDown = 0x0201, LeftButtonUp = 0x0202;
        const uint EventSystemForeground = 3, EventSystemMoveSizeStart = 10, GaRoot = 2, MonitorDefaultNearest = 2;
        const uint SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200, SwpNoZOrder = 0x4;
        static readonly int[] ModifierKeys = { 0x10, 0x11, 0x12, 0x5B, 0x5C };
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MouseData { internal Point Position; internal uint MouseDataValue, Flags, Time; internal IntPtr ExtraInfo; }
        delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
        delegate void WinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
        internal sealed class Click
        {
            internal Point Position;
            internal IntPtr TargetMonitor, PriorWindow, PriorMonitor;
            internal uint PriorProcess;
            internal uint NativeDownTime;
            internal bool NativeGesture, PriorWasMinimized;
            internal IntPtr Taskbar;
            internal DateTime Time, SelectionTime;
            internal DateTime ReleasedAt;
            internal bool Inspected, Grouped, SelectionStarted, PreviewTransition, PreviewOnly;
            internal string AppId, AppName;
            internal Native.Rect ButtonBounds;
            internal TaskbarTargets.Probe Verification;
            internal TaskbarTargets.Target Origin;
            internal IntPtr SelectedWindow;
            internal uint SelectedProcess;
            internal NativePreviewSelection NativeSelection;
            internal NativeTaskbarHandoff NativeHandoff;
        }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, MouseHook callback, IntPtr module, uint thread);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowArranged(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder appId);

        internal delegate bool MovePosition(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        internal delegate bool MovePlacement(IntPtr window, ref WindowHistory.Placement placement);
        // Per-mover operations let owned-window tests fail a native substep without
        // changing global interop or the actual placement and Undo implementation.
        internal sealed class WindowMoveApi
        {
            internal Func<IntPtr, WindowHistory.Entry> Capture = WindowHistory.Capture;
            internal MovePosition Position = SetWindowPos;
            internal MovePlacement Placement = WindowHistory.SetWindowPlacement;
            internal Func<IntPtr, bool> Focus;
            internal Func<int> Modifiers = ModifierMask;
        }
        internal readonly WindowMoveApi MoveApi = new WindowMoveApi();

        MouseHook mouseCallback;
        readonly WinEvent foregroundCallback;
        readonly WinEvent shelfRestoreCallback;
        readonly Timer timer;
        readonly Timer hoverTimer;
        IntPtr mouseHook, foregroundHook, shelfRestoreHook, activatedWindow;
        uint activatedNativeTime;
        DateTime activatedAt;
        Click click;
        readonly TaskbarTargets targets;
        readonly Func<Point, Point, bool, bool> replayInput;
        readonly Func<IntPtr> foregroundWindow;
        internal sealed class ReplayObservation
        {
            internal Func<Point?> Cursor;
            internal Func<Point, IntPtr> RootAtPoint;
            internal Func<bool> LeftButtonDown;
        }
        readonly ReplayObservation replayObservation;
        TaskbarTargets.Target directPress, directRelease;
        TaskbarTargets.Probe directCheck;
        Point directOrigin, directEnd, lastHoverPoint;
        System.Drawing.Size directDragThreshold;
        TaskbarTargets.Target lastHoverTarget;
        DateTime directReleasedAt, lastHoverAt;
        bool directCancelled;
        IntPtr directMonitor;
        IntPtr directSourceMonitor;
        bool directPhysicalDown, directPhysicalUp;
        uint directNativeDownTime, directNativeUpTime;
        sealed class DeferredRequest
        {
            internal readonly IntPtr Window, Taskbar, SourceMonitor, TargetMonitor;
            internal readonly uint Process, ShellProcess, DownTime;
            internal readonly long Started, ShellStarted;
            internal readonly Native.Rect TaskbarBounds;
            internal readonly Native.MonitorInfo Source, Target;
            internal readonly Point Origin;
            internal readonly System.Drawing.Size DragThreshold;
            internal readonly DateTime DownUtc;
            internal readonly List<AppRule> Rules;
            internal readonly List<string> RuleKeys = new List<string>();
            internal readonly bool FullscreenAllowed, RelativeSize;
            internal readonly bool PhysicalDown, RequiresAdministrator, NativeSelectionRequired;
            internal readonly NativeProcessIdentity.Info Identity;
            internal readonly DeferredTaskbarClick State;
            internal Point End;
            internal uint UpTime;
            internal bool PhysicalUp;
            internal TaskbarTargets.Probe Verification;
            internal DeferredRequest(IntPtr window, uint process, long started, IntPtr taskbar, uint shellProcess,
                long shellStarted, Native.Rect taskbarBounds, IntPtr sourceMonitor, Native.MonitorInfo source,
                IntPtr targetMonitor, Native.MonitorInfo target, Point origin, System.Drawing.Size dragThreshold, uint downTime,
                List<AppRule> rules, bool fullscreenAllowed, bool relativeSize, long began, DateTime downUtc,
                bool physicalDown, bool requiresAdministrator, bool nativeSelectionRequired, NativeProcessIdentity.Info identity)
            {
                Window = window; Process = process; Started = started; Taskbar = taskbar; ShellProcess = shellProcess;
                ShellStarted = shellStarted; TaskbarBounds = taskbarBounds; SourceMonitor = sourceMonitor; Source = source;
                TargetMonitor = targetMonitor; Target = target; Origin = End = origin; DragThreshold = dragThreshold;
                DownTime = downTime; DownUtc = downUtc; Rules = rules;
                foreach (var rule in rules) RuleKeys.Add(rule == null ? null : rule.Key);
                FullscreenAllowed = fullscreenAllowed; RelativeSize = relativeSize;
                PhysicalDown = physicalDown; RequiresAdministrator = requiresAdministrator; NativeSelectionRequired = nativeSelectionRequired;
                Identity = identity.Copy();
                State = new DeferredTaskbarClick(began, Stopwatch.Frequency, physicalDown);
            }
        }
        DeferredRequest deferredClick;
        // The controller can fence all entry and commit paths during layout
        // recovery, including Undo queued before that fence began. Standalone
        // injected mover fixtures retain their existing default behavior.
        internal Func<bool> ControllerActionGate;
        internal Action ControllerActionStarting;
        void NotifyControllerActionStarting()
        { try { if (ControllerActionStarting != null) ControllerActionStarting(); } catch (Exception) { } }
        bool ActionAllowed
        { get { try { return ControllerActionGate == null || ControllerActionGate(); } catch (Exception) { return false; } } }
        bool enabled;
        internal bool Enabled
        {
            get { return enabled && ActionAllowed; }
            set { bool changed = enabled != value; enabled = value; if (!value && timer != null) { if (changed && movement.Busy && !shelfMovement) CancelPending(); else StopProcessing(); } }
        }
        internal List<AppRule> ExcludedApps = new List<AppRule>();
        bool allowMovingFullscreenApps;
        internal bool AllowMovingFullscreenApps
        {
            get { return allowMovingFullscreenApps; }
            set {
                if (allowMovingFullscreenApps != value && movement.Busy) CancelPending();
                allowMovingFullscreenApps = value;
            }
        }
        readonly Timer fullscreenObservation = new Timer { Interval = 100 };
        ShelfWindow observedFullscreen;
        Native.MonitorInfo observedTarget;
        long observeAfter;
        internal event EventHandler FullscreenChecked;
        internal static IntPtr ForegroundWindow() { return GetForegroundWindow(); }
        bool scaleWindowSizeToMonitor;
        internal bool ScaleWindowSizeToMonitor
        {
            get { return scaleWindowSizeToMonitor; }
            set { if (scaleWindowSizeToMonitor != value) { lastMove = null; if (movement.Busy) CancelPending(); } scaleWindowSizeToMonitor = value; }
        }
        ReturnPlacement lastMove;
        readonly WindowHistory history = new WindowHistory();
        readonly MinimizeActionGate minimizeAction = new MinimizeActionGate();
        internal readonly ShelfMinimizeRegistry ShelfMinimizes = new ShelfMinimizeRegistry();
        internal bool CanUndo { get { return ActionAllowed && (minimizeAction.Pending || history.Available); } }
        internal bool TryBeginMinimizeAction()
        {
            if (!ActionAllowed) { LastStatus = "Window actions are paused for recovery"; return false; }
            if (!minimizeAction.TryBegin(disposed || movement.Busy)) { LastStatus = "Another window action is still pending"; return false; }
            try {
                NotifyControllerActionStarting();
                StopProcessing(); fullscreenObservation.Stop(); observedFullscreen = null;
                return true;
            } catch { minimizeAction.Complete(false); throw; }
        }
        internal void CompleteMinimizeAction(bool recorded)
        {
            bool restore = minimizeAction.Complete(recorded);
            if (restore && !disposed) Undo();
        }
        internal bool RememberMinimized(WindowHistory.Entry entry)
        {
            if (disposed || movement.Busy || !history.RememberMinimized(entry)) return false;
            // A minimize separates two move gestures; older chronological Undo
            // entries remain, while immediate taskbar-return memory expires.
            lastMove = null;
            return true;
        }
        readonly WindowMoveCoordinator movement = new WindowMoveCoordinator();
        bool disposed, shelfMovement, movementInterrupted, pendingUndo, undoMovement;
        bool movementRemembered, movementMayHaveChanged, movementKnownFailure, preserveMoveStatus;
        WindowHistory.Entry movementBefore;
        Func<bool> ownedMovementGuard;
        IntPtr movementForeground;
        bool movementForegroundEstablished;
        int movementModifiers;
        List<AppRule> movementRules;
        readonly List<string> movementRuleKeys = new List<string>();
        Native.MonitorInfo movementSource, movementDestination;
        bool movementTopology;
        sealed class MovementInterruptedException : Exception { }
        internal void CancelPending() { movement.Cancel(); StopProcessing(); fullscreenObservation.Stop(); observedFullscreen = null; }
        internal bool MovementBusy { get { return movement.Busy || minimizeAction.Pending; } }
        internal bool Undo()
        {
            if (disposed || !ActionAllowed) { pendingUndo = false; LastStatus = "Undo is paused for window recovery"; return false; }
            if (minimizeAction.QueueUndo()) { LastStatus = "Undo queued until the minimize finishes"; return false; }
            if (movement.Busy) { pendingUndo = true; CancelPending(); return false; }
            StopProcessing();
            fullscreenObservation.Stop(); observedFullscreen = null;
            lastMove = null;
            if (!BeginMovement(false, true)) return false;
            try {
                bool result = history.Undo(BoundedPosition, BoundedPlacement, TrackMovementBefore);
                LastStatus = result ? (history.LastUndoMinimized ? "Minimized window restored" : history.LastUndoFullscreen ? "Fullscreen restore observed; the app can move itself later" : "Window returned to its previous position") :
                    history.LastUndoMinimizeUnavailable ? "That minimized window was already restored or changed; older Undo is available on the next request" :
                    "The previous window action is no longer available or the app refused the restore; Undo remains when safe";
                return result;
            } catch (MovementInterruptedException) {
                LastStatus = "Undo stopped; the saved position is available to retry";
                return false;
            } finally { EndMovement(); }
        }
        bool BeginMovement(bool shelf, bool undo)
        {
            if (disposed || !ActionAllowed || minimizeAction.Pending || !movement.TryEnter()) return false;
            NotifyControllerActionStarting();
            shelfMovement = shelf; undoMovement = undo;
            movementInterrupted = movementRemembered = movementMayHaveChanged = movementKnownFailure = false;
            preserveMoveStatus = false;
            movementBefore = null;
            movementTopology = false;
            movementForeground = foregroundWindow();
            movementForegroundEstablished = movementForeground != IntPtr.Zero;
            movementModifiers = MoveApi.Modifiers();
            movementRules = ExcludedApps;
            movementRuleKeys.Clear();
            foreach (var rule in ExcludedApps) movementRuleKeys.Add(rule == null ? null : rule.Key);
            return true;
        }
        internal enum MovementValidityReason
        { None, MovementLifetime, Disposed, ExclusionIdentity, NewModifier, ExclusionRules, MonitorTopology, WindowIdentity, Foreground, OwnedSnapGuard }
        int lastValidityReason;
        int AdministratorSupport.IValidityDiagnostic.LastValidityReason { get { return lastValidityReason; } }
        bool InvalidMovement(MovementValidityReason reason)
        { lastValidityReason = (int)reason; return false; }
        bool MovementCurrent()
        {
            lastValidityReason = 0;
            if (!movement.Current) return InvalidMovement(MovementValidityReason.MovementLifetime);
            if (disposed || !ActionAllowed) return InvalidMovement(MovementValidityReason.Disposed);
            if (ownedMovementGuard != null && !OwnedSnapMovementGeometry.GuardCurrent(ownedMovementGuard))
                return InvalidMovement(MovementValidityReason.OwnedSnapGuard);
            if (!Object.ReferenceEquals(movementRules, ExcludedApps)) return InvalidMovement(MovementValidityReason.ExclusionIdentity);
            int modifiers = MoveApi.Modifiers();
            if ((modifiers & ~movementModifiers) != 0) return InvalidMovement(MovementValidityReason.NewModifier);
            movementModifiers &= modifiers;
            if (movementRuleKeys.Count != ExcludedApps.Count) return InvalidMovement(MovementValidityReason.ExclusionRules);
            for (int i = 0; i < movementRuleKeys.Count; i++)
                if (movementRuleKeys[i] != (ExcludedApps[i] == null ? null : ExcludedApps[i].Key)) return InvalidMovement(MovementValidityReason.ExclusionRules);
            Native.MonitorInfo sourceNow, destinationNow;
            if (movementTopology && (!Native.TryMonitor(movementSource.Device, out sourceNow) ||
                !ReturnPlacement.SameMonitor(movementSource, sourceNow) ||
                !Native.TryMonitor(movementDestination.Device, out destinationNow) ||
                !ReturnPlacement.SameMonitor(movementDestination, destinationNow))) return InvalidMovement(MovementValidityReason.MonitorTopology);
            if (movementBefore == null) return true;
            uint pid;
            if (!Native.IsWindow(movementBefore.Window) || Native.GetWindowThreadProcessId(movementBefore.Window, out pid) == 0 || pid != movementBefore.Process) return InvalidMovement(MovementValidityReason.WindowIdentity);
            IntPtr current = foregroundWindow();
            // Minimizing a foreground window can briefly leave no foreground HWND.
            // Capture its first stable replacement once, then fence later changes.
            if (!movementForegroundEstablished && current != IntPtr.Zero) {
                movementForeground = current; movementForegroundEstablished = true;
            }
            if (current == IntPtr.Zero || current == movementForeground || current == movementBefore.Window) return true;
            return InvalidMovement(MovementValidityReason.Foreground);
        }
        void TrackMovementBefore(WindowHistory.Entry entry)
        {
            movementBefore = entry; movementSource = entry.Monitor;
            movementDestination = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            movementTopology = true;
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(entry.Window, MonitorDefaultNearest), ref movementDestination)) movement.Cancel();
        }
        void EnsureMovementCurrent()
        {
            if (!MovementCurrent()) {
                movementInterrupted = true;
                if (!disposed) LastStatus = movement.Expired ? "Window move timed out; no further move was queued" : "Window move cancelled";
                throw new MovementInterruptedException();
            }
        }
        void EndMovement()
        {
            // A killed caller can have changed geometry before its native call returned.
            // Keep the pre-move state once, even when the final result is indeterminate.
            if (!undoMovement && movementMayHaveChanged && !movementRemembered && movementBefore != null &&
                (!movementKnownFailure || !UnchangedAfterFailure())) {
                history.Remember(movementBefore); lastMove = null;
            }
            movement.Exit();
            if (pendingUndo && !disposed) { pendingUndo = false; Undo(); movementInterrupted = true; }
        }
        bool UnchangedAfterFailure()
        {
            try {
                if (movementBefore.FullscreenBounds.HasValue) {
                    Native.Rect actual;
                    if (!Native.GetWindowRect(movementBefore.Window, out actual) || !AreaPolicy.Equal(actual, movementBefore.FullscreenBounds.Value)) return false;
                }
                var current = WindowHistory.Capture(movementBefore.Window);
                return current != null && current.Process == movementBefore.Process && current.ProcessStarted == movementBefore.ProcessStarted &&
                    current.IsNormal == movementBefore.IsNormal && current.Placement.ShowCommand == movementBefore.Placement.ShowCommand &&
                    current.Placement.Flags == movementBefore.Placement.Flags &&
                    AreaPolicy.Equal(current.Placement.NormalPosition, movementBefore.Placement.NormalPosition) &&
                    ReturnPlacement.SameMonitor(current.Monitor, movementBefore.Monitor) &&
                    current.ArrangedBounds.HasValue == movementBefore.ArrangedBounds.HasValue &&
                    (!current.ArrangedBounds.HasValue || AreaPolicy.Equal(current.ArrangedBounds.Value, movementBefore.ArrangedBounds.Value));
            } catch (Exception) { return false; }
        }
        bool AcceptNativeResult(WindowMoveWorker.Result result, bool geometry = true)
        {
            if (geometry) movementMayHaveChanged |= result.MutationAttempted;
            movementKnownFailure = result.Outcome == WindowMoveWorker.Outcome.Failed && result.CallerExited;
            if (result.Outcome == WindowMoveWorker.Outcome.TimedOut || result.Outcome == WindowMoveWorker.Outcome.Cancelled || !result.CallerExited) {
                movementInterrupted = true;
                LastStatus = result.Outcome == WindowMoveWorker.Outcome.TimedOut ?
                    "Window move timed out; no further move was queued" : "Window move cancelled";
                throw new MovementInterruptedException();
            }
            EnsureMovementCurrent();
            return result.Outcome == WindowMoveWorker.Outcome.Success;
        }
        bool BoundedPosition(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags)
        {
            EnsureMovementCurrent();
            return AcceptNativeResult(WindowMoveWorker.Position(window, movementBefore.Process, movementBefore.ProcessStarted,
                x, y, width, height, flags, movement.Deadline, MovementCurrent));
        }
        bool BoundedPlacement(IntPtr window, ref WindowHistory.Placement placement)
        {
            EnsureMovementCurrent();
            return AcceptNativeResult(WindowMoveWorker.Placement(window, movementBefore.Process, movementBefore.ProcessStarted,
                placement, movement.Deadline, MovementCurrent));
        }
        bool PositionWindow(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags)
        {
            EnsureMovementCurrent();
            bool result = MoveApi.Position(window, after, x, y, width, height, flags);
            movementMayHaveChanged |= result;
            EnsureMovementCurrent();
            return result;
        }
        bool PlaceWindow(IntPtr window, ref WindowHistory.Placement placement)
        {
            EnsureMovementCurrent();
            bool result = MoveApi.Placement(window, ref placement);
            movementMayHaveChanged |= result;
            EnsureMovementCurrent();
            return result;
        }
        void SetMoveStatus(bool moved, string success, string failure)
        {
            if (!movementInterrupted && !disposed && observedFullscreen == null && !preserveMoveStatus) LastStatus = moved ? success : failure;
            preserveMoveStatus = false;
        }
        bool FocusFromShelf(IntPtr window)
        {
            if (!BeginMovement(true, false)) return false;
            try {
                movementBefore = MoveApi.Capture(window);
                if (movementBefore == null) return false;
                TrackMovementBefore(movementBefore);
                return FocusWindow(window);
            } catch (MovementInterruptedException) { return false; }
            finally { EndMovement(); }
        }
        bool FocusWindow(IntPtr window)
        {
            EnsureMovementCurrent();
            if (MoveApi.Focus != null) { bool focused = MoveApi.Focus(window); EnsureMovementCurrent(); return focused; }
            bool accepted = AcceptNativeResult(WindowMoveWorker.Foreground(window, movementBefore.Process,
                movementBefore.ProcessStarted, movement.Deadline, MovementCurrent), false);
            if (!accepted) return false;
            // Cross-queue activation is asynchronous even after SetForegroundWindow
            // returns. Observe completion inside the existing operation budget.
            while (GetForegroundWindow() != window) {
                EnsureMovementCurrent(); Application.DoEvents();
                EnsureMovementCurrent(); System.Threading.Thread.Sleep(5);
            }
            EnsureMovementCurrent();
            return true;
        }
        internal string LastStatus = "Ready";
        readonly bool injectedInputOnly;
        internal bool Available { get { return injectedInputOnly || (mouseHook != IntPtr.Zero && foregroundHook != IntPtr.Zero &&
            (mouseHookLifetime == null || !mouseHookLifetime.RetirementUncertain)); } }

        internal TaskbarMover() : this(new TaskbarTargets()) { }
        internal TaskbarMover(TaskbarTargets targets) : this(targets, TaskbarInput.Replay) { }
        internal TaskbarMover(TaskbarTargets targets, Func<Point, Point, bool, bool> replayInput)
            : this(targets, replayInput, GetForegroundWindow) { }
        internal TaskbarMover(TaskbarTargets targets, Func<Point, Point, bool, bool> replayInput, Func<IntPtr> foregroundWindow)
            : this(targets, replayInput, foregroundWindow, true) { }
        // Isolated fixtures supply every input dependency and never register desktop hooks.
        // Normal constructors retain the native hook-availability requirement.
        internal TaskbarMover(TaskbarTargets targets, Func<Point, Point, bool, bool> replayInput, Func<IntPtr> foregroundWindow, bool installDesktopHooks)
            : this(targets, replayInput, foregroundWindow, installDesktopHooks, null) { }
        internal TaskbarMover(TaskbarTargets targets, Func<Point, Point, bool, bool> replayInput,
            Func<IntPtr> foregroundWindow, bool installDesktopHooks, ReplayObservation replayObservation)
        {
            if (targets == null || replayInput == null || foregroundWindow == null) throw new ArgumentNullException();
            if (replayObservation != null && (installDesktopHooks || replayObservation.Cursor == null ||
                replayObservation.RootAtPoint == null || replayObservation.LeftButtonDown == null)) throw new ArgumentException("Replay observations are complete no-hook fixture dependencies only.");
            injectedInputOnly = !installDesktopHooks;
            MoveApi.Position = BoundedPosition; MoveApi.Placement = BoundedPlacement;
            this.targets = targets;
            this.replayInput = replayInput;
            this.foregroundWindow = foregroundWindow;
            this.replayObservation = replayObservation ?? new ReplayObservation {
                Cursor = delegate { Point point; return GetCursorPos(out point) ? (Point?)point : null; },
                RootAtPoint = delegate(Point point) { return GetAncestor(WindowFromPoint(point), GaRoot); },
                LeftButtonDown = delegate { return (GetAsyncKeyState(1) & 0x8000) != 0; }
            };
            fullscreenObservation.Tick += delegate { ObserveFullscreen(); };
            foregroundCallback = ForegroundEvent;
            shelfRestoreCallback = delegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time) {
                if (objectId == 0 && childId == 0) ShelfMinimizes.ObserveRestore(window, ExcludedApps);
            };
            if (installDesktopHooks) {
                foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemMoveSizeStart, IntPtr.Zero, foregroundCallback, 0, 0, 0);
                shelfRestoreHook = SetWinEventHook(0x0017, 0x0017, IntPtr.Zero, shelfRestoreCallback, 0, 0, 0);
            }
            timer = new Timer { Interval = 25 };
            timer.Tick += delegate {
                try { ProcessClick(); }
                catch (Exception error) {
                    LastStatus = "Window move stopped safely (" + error.GetType().Name + ")";
                    StopProcessing();
                }
            };
            hoverTimer = new Timer { Interval = 150 };
            hoverTimer.Tick += delegate {
                Point point;
                if (!Enabled || !GetCursorPos(out point)) { targets.LeaveHover(); hoverTimer.Stop(); return; }
                TrackHover(point);
            };
            if (installDesktopHooks) InitializeMouseHookService();
        }

        void StopProcessing()
        {
            AbortDeferredClick();
            gestureDiagnostics.Stopped();
            click = null;
            directRelease = null;
            directCheck = null;
            directCancelled = true;
            if (!disposed) timer.Stop();
        }

        static bool IsTaskbar(IntPtr window)
        {
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            return name.ToString() == "Shell_TrayWnd" || name.ToString() == "Shell_SecondaryTrayWnd";
        }

        internal event Action<System.Drawing.Point> PointerDown;
        void ObserveHookDown(MouseData input, bool left)
        {
            if (left) diagnosticGestureBegan = false;
            IntPtr root = GetAncestor(WindowFromPoint(input.Position), GaRoot);
            TaskbarHookRoot kind = TaskbarHookRoot.Missing;
            if (root != IntPtr.Zero) {
                var name = new StringBuilder(128); GetClassName(root, name, name.Capacity);
                if (name.ToString() == "Shell_TrayWnd") kind = TaskbarHookRoot.Primary;
                else if (name.ToString() == "Shell_SecondaryTrayWnd") kind = TaskbarHookRoot.Secondary;
                else {
                    uint process, shell;
                    bool shellOwned = GetWindowThreadProcessId(root, out process) != 0 &&
                        GetWindowThreadProcessId(Native.GetShellWindow(), out shell) != 0 && process != 0 && process == shell;
                    kind = shellOwned ? (name.ToString() == "Progman" || name.ToString() == "WorkerW" ?
                        TaskbarHookRoot.ShellDesktop : TaskbarHookRoot.ShellOther) : TaskbarHookRoot.App;
                }
            }
            hookDiagnostics.Record(TaskbarNativeSelection.IsPhysicalPointer(input.Flags), left, kind, Stopwatch.GetTimestamp());
            if (left && (kind == TaskbarHookRoot.Primary || kind == TaskbarHookRoot.Secondary)) {
                gestureDiagnostics.Begin(kind == TaskbarHookRoot.Primary, false, TaskbarNativeSelection.IsPhysicalPointer(input.Flags),
                    false, false, false, false, false, 0, null);
                diagnosticGestureBegan = true;
                if (movement.Busy || !Enabled) {
                    gestureDiagnostics.Direct(movement.Busy ? TaskbarDirectDecision.Busy : TaskbarDirectDecision.Disabled);
                    gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                }
            }
        }
        void BeginTaskbarGesture(IntPtr taskbar, IntPtr monitor, TaskbarTargets.Target target, bool physical)
        {
            var name = new StringBuilder(128); GetClassName(taskbar, name, name.Capacity);
            bool found = target != null;
            bool currentProof = found && TaskManagerShellIdentity.ProofFreshAt(target.ShellProof, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            int age = found ? (int)Math.Max(0, Math.Min(9999, (DateTime.UtcNow - target.Captured).TotalMilliseconds)) : 0;
            if (!diagnosticGestureBegan) gestureDiagnostics.Begin(name.ToString() == "Shell_TrayWnd", false, physical,
                false, false, false, false, false, 0, null);
            gestureDiagnostics.Cache(found, found && target.NeedsNativeSelection, found && target.ShellProof != null,
                currentProof, age, found ? target.Diagnostics : null);
        }
        void ObserveGestureProbe(TaskbarTargets.Probe probe)
        {
            if (probe == null || !probe.Complete) return;
            var target = probe.Result;
            gestureDiagnostics.Probe(probe.Diagnostics, target != null, target == null || target.Windows == null ? 0 : target.Windows.Length,
                target != null && target.NeedsNativeSelection, target != null && target.ShellProof != null,
                target != null && TaskManagerShellIdentity.ProofFreshAt(target.ShellProof, Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        }
        IntPtr MouseEvent(int code, IntPtr message, IntPtr data)
        {
            if (disposed || code < 0) return CallNextHookEx(mouseHook, code, message, data);
            MouseData input = (MouseData)Marshal.PtrToStructure(data, typeof(MouseData));
            if (input.ExtraInfo == TaskbarInput.ReplayTag) return CallNextHookEx(mouseHook, code, message, data);
            int pointerMessage = message.ToInt32();
            if (deferredClick != null) {
                if (pointerMessage == LeftButtonDown || pointerMessage == 0x204 || pointerMessage == 0x207 || pointerMessage == 0x20B)
                    AbortDeferredClick();
                else if (pointerMessage == LeftButtonUp && !deferredClick.State.Released) {
                    ReleaseDeferredClick(input);
                    return new IntPtr(1);
                } else if (pointerMessage == MouseMove && !deferredClick.State.Released &&
                    (ModifiedClick() || TaskbarInput.IsDirectDrag(deferredClick.Origin, input.Position, deferredClick.DragThreshold))) {
                    var pending = deferredClick; pending.End = input.Position;
                    FinishDeferredClick(pending, pending.State.Abort(DeferredGeometryCurrent(pending)), false);
                    return new IntPtr(1);
                }
            }
            if (pointerMessage == LeftButtonDown || pointerMessage == 0x204 || pointerMessage == 0x207 || pointerMessage == 0x20B) {
                ObserveHookDown(input, pointerMessage == LeftButtonDown);
                var observer = PointerDown;
                if (observer != null) try { observer(new System.Drawing.Point(input.Position.X, input.Position.Y)); } catch (Exception) { }
            }
            if (movement.Busy) {
                int button = message.ToInt32();
                if (button == LeftButtonDown || button == 0x204 || button == 0x207 || button == 0x20B) CancelPending();
                return CallNextHookEx(mouseHook, code, message, data);
            }
            if (message.ToInt32() == 0x204 || message.ToInt32() == 0x207 || message.ToInt32() == 0x20B) StopProcessing();
            if (message.ToInt32() == MouseMove && directPress != null &&
                TaskbarInput.IsDirectDrag(directOrigin, input.Position, directDragThreshold)) {
                HandOffDrag(input.Position, false);
                return new IntPtr(1);
            }
            if (code >= 0 && message.ToInt32() == LeftButtonUp && directPress != null) {
                var pressed = directPress;
                directEnd = input.Position;
                if (TaskbarInput.IsDirectDrag(directOrigin, input.Position, directDragThreshold)) HandOffDrag(input.Position, true);
                else CompleteDirectPress(Contains(pressed.Button.ButtonBounds, input.Position),
                    TaskbarNativeSelection.IsPhysicalPointer(input.Flags), input.Time);
                return new IntPtr(1);
            }
            if (Enabled && code >= 0 && message.ToInt32() == MouseMove) {
                MouseData moved = (MouseData)Marshal.PtrToStructure(data, typeof(MouseData));
                TrackHover(moved.Position);
                if (click != null && !click.SelectionStarted &&
                    TaskbarInput.IsDrag(click.Position, moved.Position, SystemInformation.DragSize) &&
                    (GetAsyncKeyState(1) & 0x8000) != 0) StopProcessing();
            }
            if (Enabled && code >= 0 && message.ToInt32() == LeftButtonUp && click != null) {
                if (click.PreviewOnly && (ModifiedClick() || TaskbarInput.IsDrag(click.Position, input.Position, SystemInformation.DragSize))) {
                    StopProcessing();
                    return CallNextHookEx(mouseHook, code, message, data);
                }
                click.ReleasedAt = DateTime.UtcNow;
                if (click.NativeSelection != null) click.NativeSelection.Release(input.Time);
            }
            if (Enabled && code >= 0 && message.ToInt32() == LeftButtonDown) {
                if (ModifiedClick()) { StopProcessing(); return CallNextHookEx(mouseHook, code, message, data); }
                MouseData mouse = (MouseData)Marshal.PtrToStructure(data, typeof(MouseData));
                IntPtr leaf = WindowFromPoint(mouse.Position), root = GetAncestor(leaf, GaRoot);
                IntPtr preview = PreviewAt(leaf, root, mouse.Position);
                if (preview != IntPtr.Zero) {
                    StopProcessing();
                    if (lastHoverTarget == null || DateTime.UtcNow - lastHoverAt > TimeSpan.FromSeconds(8) ||
                        MonitorFromPoint(lastHoverPoint, MonitorDefaultNearest) != MonitorFromPoint(mouse.Position, MonitorDefaultNearest))
                        return CallNextHookEx(mouseHook, code, message, data);
                    click = NewClick(mouse.Position);
                    click.NativeGesture = TaskbarNativeSelection.IsPhysicalPointer(mouse.Flags); click.NativeDownTime = mouse.Time;
                    click.Origin = lastHoverTarget;
                    click.NativeSelection = new NativePreviewSelection(preview, mouse.Time);
                    click.Verification = targets.VerifyPreview(lastHoverTarget, preview, mouse.Position);
                    click.SelectionStarted = true;
                    click.SelectionTime = click.Time;
                    click.PreviewTransition = true;
                    click.PreviewOnly = true;
                    activatedWindow = IntPtr.Zero;
                    activatedAt = DateTime.MinValue;
                    LastStatus = "Taskbar preview selection detected";
                    timer.Start();
                } else if (IsTaskbar(root)) {
                    Native.Rect taskbarBounds;
                    bool insideTaskbar = Native.GetWindowRect(root, out taskbarBounds) && Contains(taskbarBounds, mouse.Position);
                    if (!insideTaskbar && click != null) {
                        StopProcessing();
                    } else if (insideTaskbar) {
                        var target = targets.Find(root, mouse.Position);
                        IntPtr monitor = MonitorFromPoint(mouse.Position, MonitorDefaultNearest);
                        BeginTaskbarGesture(root, monitor, target, TaskbarNativeSelection.IsPhysicalPointer(mouse.Flags));
                        if (TryStartDirectMove(target, monitor, mouse.Position,
                            TaskbarNativeSelection.IsPhysicalPointer(mouse.Flags), mouse.Time)) {
                            return new IntPtr(1);
                        }
                        if (TryStartDeferredClick(target, root, monitor, mouse.Position,
                            TaskbarNativeSelection.IsPhysicalPointer(mouse.Flags), mouse.Time)) return new IntPtr(1);
                        StopProcessing();
                        click = NewClick(mouse.Position);
                        gestureDiagnostics.DownContext(target != null && click.PriorWindow == target.Window && !click.PriorWasMinimized,
                            target != null && click.PriorWindow == target.Window && click.PriorMonitor == monitor);
                        click.NativeGesture = TaskbarNativeSelection.IsPhysicalPointer(mouse.Flags); click.NativeDownTime = mouse.Time;
                        click.Taskbar = root;
                        click.Origin = target;
                        click.Verification = targets.Verify(root, mouse.Position);
                        activatedWindow = IntPtr.Zero; activatedAt = DateTime.MinValue;
                        LastStatus = "Taskbar click detected";
                        timer.Start();
                    }
                } else if (click != null) {
                    StopProcessing();
                }
            }
            return CallNextHookEx(mouseHook, code, message, data);
        }

        void RememberHover(IntPtr taskbar, Point point)
        {
            var target = targets.Find(taskbar, point);
            if (target == null) return;
            lastHoverPoint = point; lastHoverAt = DateTime.UtcNow;
            lastHoverTarget = target;
        }

        void TrackHover(Point point)
        {
            IntPtr leaf = WindowFromPoint(point), root = GetAncestor(leaf, GaRoot);
            IntPtr preview = PreviewAt(leaf, root, point);
            if (preview != IntPtr.Zero && lastHoverTarget != null && DateTime.UtcNow - lastHoverAt < TimeSpan.FromSeconds(8)) {
                lastHoverAt = DateTime.UtcNow;
                targets.RequestPreview(lastHoverTarget, preview, point);
                hoverTimer.Start();
            } else if (IsTaskbar(root)) {
                targets.Request(root, point); RememberHover(root, point); hoverTimer.Start();
            } else { targets.LeaveHover(); hoverTimer.Stop(); }
        }

        static IntPtr PreviewAt(IntPtr leaf, IntPtr root, Point point)
        {
            if (IsTaskbarPreviewWindow(root)) return root;
            Native.Rect bounds;
            if (!IsTaskbar(root) || (Native.GetWindowRect(root, out bounds) && Contains(bounds, point))) return IntPtr.Zero;
            // Windows can parent a thumbnail popup to the taskbar. GA_ROOT then
            // returns the bar, so inspect the native child ancestry as well.
            for (int depth = 0; leaf != IntPtr.Zero && leaf != root && depth < 8; depth++, leaf = GetAncestor(leaf, 1))
                if (IsTaskbarPreviewWindow(leaf)) return leaf;
            return IntPtr.Zero;
        }

        internal bool HandOffDrag(Point point, bool released)
        {
            if (directPress == null) return false;
            Point origin = directOrigin;
            directPress = null;
            StopProcessing();
            bool replayed = replayInput(origin, point, released);
            LastStatus = replayed ? "Used normal Windows icon dragging" : "Windows could not resume this drag";
            return replayed;
        }

        internal static bool Contains(Native.Rect bounds, Point point)
        {
            return point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
        }

        Click NewClick(Point position)
        {
            IntPtr priorWindow = GetAncestor(foregroundWindow(), GaRoot);
            uint priorProcess;
            GetWindowThreadProcessId(priorWindow, out priorProcess);
            return new Click {
                Position = position,
                TargetMonitor = MonitorFromPoint(position, MonitorDefaultNearest),
                PriorWindow = priorWindow, PriorProcess = priorProcess, PriorWasMinimized = IsIconic(priorWindow),
                PriorMonitor = priorWindow == IntPtr.Zero ? IntPtr.Zero : Native.MonitorFromWindow(priorWindow, MonitorDefaultNearest),
                Time = DateTime.UtcNow
            };
        }

        static bool IsTaskbarPreviewWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            string className = name.ToString();
            if (!className.Equals("XamlExplorerHostIslandWindow", StringComparison.Ordinal) &&
                !className.Equals("TaskListThumbnailWnd", StringComparison.Ordinal)) return false;
            uint processId, shellProcessId;
            GetWindowThreadProcessId(window, out processId);
            GetWindowThreadProcessId(Native.GetShellWindow(), out shellProcessId);
            return processId != 0 && processId == shellProcessId;
        }

        internal event Action<IntPtr> ForegroundChanged;
        internal Action<IntPtr> ManualMoveStarted;
        void ForegroundEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            if (eventType == EventSystemMoveSizeStart) {
                ReturnPlacement.InvalidateManualMove(ref lastMove, window);
                try { if (ManualMoveStarted != null) ManualMoveStarted(window); } catch (Exception) { }
                if (movement.Busy && movementBefore != null && movementBefore.Window == window) CancelPending();
                return;
            }
            if (eventType != EventSystemForeground) return;
            IntPtr root = GetAncestor(window, GaRoot);
            var observer = ForegroundChanged;
            if (root != IntPtr.Zero && observer != null) observer(root);
            if (!Enabled || root == IntPtr.Zero) return;
            if (click != null && click.NativeSelection != null) click.NativeSelection.Observe(root, time);
            if (IsTaskbar(root)) return;
            if (!TaskbarNativeSelection.ObserveForegroundForClick(click, time, unchecked((uint)Environment.TickCount))) return;
            activatedWindow = root; activatedAt = DateTime.UtcNow; activatedNativeTime = time;
        }

        internal static bool ReadAppButton(Click candidate, IntPtr taskbar)
        {
            TaskbarTargets.NoteStage(TaskbarTargets.DiagnosticStage.ReadAppButton);
            return candidate != null && IsTaskbar(taskbar) && TaskbarButtonReader.Read(candidate, taskbar);
        }

        internal static bool IsGroupedButtonName(string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            return name.IndexOf(" running windows", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (name.IndexOf(" running window and ", StringComparison.OrdinalIgnoreCase) >= 0 &&
                 name.IndexOf(" group", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static string ButtonAppName(string name)
        {
            if (String.IsNullOrEmpty(name)) return "";
            int running = name.IndexOf(" running window", StringComparison.OrdinalIgnoreCase);
            if (running < 0) return name;
            int separator = name.LastIndexOf(" - ", running, StringComparison.OrdinalIgnoreCase);
            return separator > 0 ? name.Substring(0, separator) : name;
        }

        static string NormalizeName(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var result = new StringBuilder(value.Length);
            foreach (char character in value)
                if (Char.IsLetterOrDigit(character)) result.Append(Char.ToLowerInvariant(character));
            return result.ToString();
        }

        static string ProcessAppId(uint processId)
        {
            const uint ProcessQueryLimitedInformation = 0x1000;
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) return "";
            try {
                uint length = 0;
                GetApplicationUserModelId(process, ref length, null);
                if (length == 0) return "";
                var appId = new StringBuilder((int)length);
                return GetApplicationUserModelId(process, ref length, appId) == 0 ? appId.ToString() : "";
            } finally { CloseHandle(process); }
        }

        internal static bool MatchesApp(Click candidate, IntPtr window)
        {
            if (candidate == null || String.IsNullOrEmpty(candidate.AppId) || window == IntPtr.Zero) return false;
            uint processId; GetWindowThreadProcessId(window, out processId);
            if (processId == 0) return false;
            TaskbarTargets.NoteStage(TaskbarTargets.DiagnosticStage.WindowAppIdentity);
            string windowAppId = WindowAppIdentity.Read(window);
            TaskbarTargets.NoteStage(TaskbarTargets.DiagnosticStage.ResolveWindows);
            // Explicit HWND identity controls its taskbar grouping, even when
            // the process name belongs to a shared browser/helper host.
            if (!String.IsNullOrEmpty(windowAppId))
                return candidate.AppId.Equals(windowAppId, StringComparison.OrdinalIgnoreCase);
            // Our settings icon has an exact HWND identity. A same-process
            // helper without it must not make this single-window group ambiguous.
            if (SettingsTaskbarIdentity.IsReserved(candidate.AppId)) return false;
            try {
                using (Process process = Process.GetProcessById((int)processId)) {
                    if (candidate.AppId.IndexOf(process.ProcessName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    string processAppId = ProcessAppId(processId);
                    if (!String.IsNullOrEmpty(processAppId) && candidate.AppId.Equals(processAppId, StringComparison.OrdinalIgnoreCase)) return true;
                    if (NormalizeName(candidate.AppName) == NormalizeName(process.ProcessName)) return true;
                    string executable = Path.GetFileNameWithoutExtension(NativeProcessIdentity.Path(processId));
                    return !String.IsNullOrEmpty(executable) && candidate.AppId.IndexOf(executable, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            } catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (NotSupportedException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        internal static Native.Rect MapToWorkArea(Native.Rect current, Native.Rect source, Native.Rect target)
        {
            return MapWorkArea(current, source, target, false);
        }

        internal static Native.Rect MapRelativeToWorkArea(Native.Rect current, Native.Rect source, Native.Rect target)
        {
            return MapWorkArea(current, source, target, true);
        }

        static Native.Rect MapWorkArea(Native.Rect current, Native.Rect source, Native.Rect target, bool relativeSize)
        {
            long sourceWidth = Math.Max(1L, (long)source.Right - source.Left);
            long sourceHeight = Math.Max(1L, (long)source.Bottom - source.Top);
            long targetWidth = Math.Max(1L, (long)target.Right - target.Left);
            long targetHeight = Math.Max(1L, (long)target.Bottom - target.Top);
            long originalWidth = Math.Max(1L, (long)current.Right - current.Left);
            long originalHeight = Math.Max(1L, (long)current.Bottom - current.Top);
            int width = relativeSize ? ScaledSize(originalWidth, sourceWidth, targetWidth) : (int)Math.Min(Int32.MaxValue, Math.Min(originalWidth, targetWidth));
            int height = relativeSize ? ScaledSize(originalHeight, sourceHeight, targetHeight) : (int)Math.Min(Int32.MaxValue, Math.Min(originalHeight, targetHeight));
            long availableX = Math.Max(0L, targetWidth - width);
            long availableY = Math.Max(0L, targetHeight - height);
            // Position is a fraction of the ORIGINAL free travel, before any size clamp.
            // Exact end points remain flush; a full-span source has no position to retain.
            double rx = RelativePosition((long)current.Left - source.Left, sourceWidth - originalWidth);
            double ry = RelativePosition((long)current.Top - source.Top, sourceHeight - originalHeight);
            long x = target.Left + (long)Math.Round(rx * availableX);
            long y = target.Top + (long)Math.Round(ry * availableY);
            return new Native.Rect { Left = (int)x, Top = (int)y, Right = (int)(x + width), Bottom = (int)(y + height) };
        }

        static double RelativePosition(long offset, long travel)
        {
            return travel <= 0 ? 0 : Math.Max(0, Math.Min(1, (double)offset / travel));
        }

        static int ScaledSize(long original, long source, long target)
        {
            double scaled = Math.Round((double)original * target / source);
            return (int)Math.Max(1, Math.Min(Math.Min(target, Int32.MaxValue), scaled));
        }

        Native.Rect MapWindow(Native.Rect current, Native.Rect source, Native.Rect target)
        {
            return ScaleWindowSizeToMonitor ? MapRelativeToWorkArea(current, source, target) : MapToWorkArea(current, source, target);
        }

        internal static bool ValidVisibleFrame(Native.Rect outer, Native.Rect visible)
        {
            return visible.Right > visible.Left && visible.Bottom > visible.Top &&
                visible.Left >= outer.Left && visible.Top >= outer.Top &&
                visible.Right <= outer.Right && visible.Bottom <= outer.Bottom;
        }

        static bool OverlapsWorkArea(Native.Rect frame, Native.Rect work)
        {
            return work.Right > work.Left && work.Bottom > work.Top && frame.Left < work.Right &&
                frame.Right > work.Left && frame.Top < work.Bottom && frame.Bottom > work.Top;
        }

        internal static bool TryFitMovedWindow(Native.Rect outer, Native.Rect visible, Native.Rect work, out Native.Rect fitted)
        {
            fitted = outer;
            if (!ValidVisibleFrame(outer, outer) || !ValidVisibleFrame(work, work)) return false;
            if (!ValidVisibleFrame(outer, visible)) visible = outer;
            // The app has already chosen its size. Fit each axis where possible,
            // and keep the top or left edge accessible if the window is too large.
            long width = (long)visible.Right - visible.Left, height = (long)visible.Bottom - visible.Top;
            long x = Math.Max(work.Left, Math.Min((long)visible.Left, (long)work.Right - width));
            long y = Math.Max(work.Top, Math.Min((long)visible.Top, (long)work.Bottom - height));
            long dx = x - visible.Left, dy = y - visible.Top;
            if ((dx == 0 && dy == 0) || (long)outer.Right - outer.Left > Int32.MaxValue ||
                (long)outer.Bottom - outer.Top > Int32.MaxValue || outer.Left + dx < Int32.MinValue ||
                outer.Top + dy < Int32.MinValue || outer.Right + dx > Int32.MaxValue || outer.Bottom + dy > Int32.MaxValue) return false;
            fitted = new Native.Rect { Left = (int)(outer.Left + dx), Top = (int)(outer.Top + dy),
                Right = (int)(outer.Right + dx), Bottom = (int)(outer.Bottom + dy) };
            return true;
        }

        internal static Native.Rect MapFramedWindow(Native.Rect outer, Native.Rect visible,
            Native.Rect source, Native.Rect target, bool relativeSize, bool arranged = false)
        {
            if (!ValidVisibleFrame(outer, visible)) visible = outer;
            var mapped = MapWorkArea(visible, source, target, relativeSize);
            if (arranged) AlignArrangedEdges(ref mapped, outer, source, target);
            // SetWindowPos still needs outer bounds. Invisible resize borders may extend
            // beyond the work area, while the visible frame fits it without artificial gaps.
            Native.Rect framed;
            return ReturnPlacement.TryAddFrameMargins(mapped, outer, visible, out framed) ? framed :
                MapWorkArea(outer, source, target, relativeSize);
        }

        static void AlignArrangedEdges(ref Native.Rect mapped, Native.Rect outer, Native.Rect source, Native.Rect target)
        {
            // A snapped window can align its outer edge instead of its visible
            // edge. Normalize only exact work-area contacts.
            int dx = outer.Left == source.Left ? target.Left - mapped.Left :
                outer.Right == source.Right ? target.Right - mapped.Right : 0;
            int dy = outer.Top == source.Top ? target.Top - mapped.Top :
                outer.Bottom == source.Bottom ? target.Bottom - mapped.Bottom : 0;
            Offset(ref mapped, dx, dy);
        }

        internal static bool Arranged(IntPtr window)
        {
            try { return IsWindowArranged(window); }
            catch (EntryPointNotFoundException) { return false; }
        }

        // One verified sequence for the current window. Actual bounds guard continuity,
        // while the relative layout stays anchored to the original work area.
        internal sealed class ReturnPlacement
        {
            internal WindowHistory.Entry Before, After;
            internal Native.Rect Original, ReturnBounds, Moved;
            internal bool RelativeSize;
            internal bool HasRelativeAnchor { get; private set; }
            Native.Rect originalVisible;

            internal static bool SameMonitor(Native.MonitorInfo a, Native.MonitorInfo b)
            {
                return a.Device == b.Device && a.Flags == b.Flags &&
                    AreaPolicy.Equal(a.Monitor, b.Monitor) && AreaPolicy.Equal(a.Work, b.Work);
            }

            static bool SameWindow(WindowHistory.Entry a, WindowHistory.Entry b)
            {
                return a != null && b != null && a.Window == b.Window && a.Process != 0 &&
                    a.Process == b.Process && a.ProcessStarted != 0 && a.ProcessStarted == b.ProcessStarted;
            }

            static bool RelativeCandidate(WindowHistory.Entry entry)
            {
                return entry.IsNormal && !entry.FullscreenBounds.HasValue;
            }

            static bool MatchesRelativeSource(ReturnPlacement saved, WindowHistory.Entry current, Native.Rect outer)
            {
                // DWM frame margins can settle separately after a move. The
                // outer bounds and manual-move events guard continuity.
                return saved != null && saved.HasRelativeAnchor && SameWindow(saved.After, current) && RelativeCandidate(current) &&
                    saved.After.ArrangedBounds.HasValue == current.ArrangedBounds.HasValue &&
                    SameMonitor(saved.After.Monitor, current.Monitor) && AreaPolicy.Equal(outer, saved.Moved) &&
                    (!current.VisibleBounds.HasValue || AreaPolicy.Equal(current.VisibleBounds.Value, outer));
            }

            internal static bool TryMapRelative(ReturnPlacement saved, WindowHistory.Entry current,
                Native.Rect outer, Native.Rect visible, Native.MonitorInfo target, Native.MonitorInfo originNow,
                out Native.Rect mapped)
            {
                mapped = new Native.Rect();
                if (!MatchesRelativeSource(saved, current, outer) || !SameMonitor(saved.Before.Monitor, originNow) ||
                    target.Work.Right <= target.Work.Left || target.Work.Bottom <= target.Work.Top) return false;
                if (SameMonitor(target, saved.Before.Monitor)) {
                    mapped = saved.ReturnBounds;
                    return true;
                }
                if (!ValidVisibleFrame(outer, visible)) visible = outer;
                var originWork = saved.Before.Monitor.Work;
                var relativeVisible = MapRelativeToWorkArea(saved.originalVisible, originWork, target.Work);
                if (saved.Before.ArrangedBounds.HasValue) AlignArrangedEdges(ref relativeVisible, saved.Original, originWork, target.Work);
                return TryAddFrameMargins(relativeVisible, outer, visible, out mapped);
            }

            internal static bool TryAddFrameMargins(Native.Rect mapped, Native.Rect outer, Native.Rect visible, out Native.Rect framed)
            {
                long left = mapped.Left - (visible.Left - (long)outer.Left);
                long top = mapped.Top - (visible.Top - (long)outer.Top);
                long right = mapped.Right + (outer.Right - (long)visible.Right);
                long bottom = mapped.Bottom + (outer.Bottom - (long)visible.Bottom);
                if (left < Int32.MinValue || top < Int32.MinValue || right > Int32.MaxValue || bottom > Int32.MaxValue ||
                    right - left > Int32.MaxValue || bottom - top > Int32.MaxValue) {
                    framed = new Native.Rect();
                    return false;
                }
                framed = new Native.Rect { Left = (int)left, Top = (int)top, Right = (int)right, Bottom = (int)bottom };
                return true;
            }

            internal static ReturnPlacement CreateRelative(ReturnPlacement continued, WindowHistory.Entry before,
                WindowHistory.Entry after, Native.Rect original, Native.Rect originalVisible, Native.Rect actual,
                Native.Rect actualVisible, Native.MonitorInfo target)
            {
                if (!SameWindow(before, after) || !RelativeCandidate(before) || !RelativeCandidate(after) || !SameMonitor(after.Monitor, target) ||
                    !ValidVisibleFrame(actual, actualVisible) ||
                    (after.VisibleBounds.HasValue && !AreaPolicy.Equal(after.VisibleBounds.Value, actual))) return null;
                if (!OverlapsWorkArea(actualVisible, target.Work)) return null;
                if (continued != null) {
                    if (!MatchesRelativeSource(continued, before, original)) return null;
                    before = continued.Before;
                    original = continued.Original;
                    originalVisible = continued.originalVisible;
                } else if (!ValidVisibleFrame(original, originalVisible) || !OverlapsWorkArea(originalVisible, before.Monitor.Work) ||
                    (before.VisibleBounds.HasValue && !AreaPolicy.Equal(before.VisibleBounds.Value, original))) return null;
                var work = before.Monitor.Work;
                var returnBounds = before.ArrangedBounds.HasValue && ValidVisibleFrame(work, originalVisible) ?
                    MapFramedWindow(original, originalVisible, work, work, false, true) : original;
                return new ReturnPlacement { Before = before, After = after, Original = original, ReturnBounds = returnBounds,
                    Moved = actual, RelativeSize = true, HasRelativeAnchor = true,
                    originalVisible = originalVisible };
            }

            internal static ReturnPlacement Create(WindowHistory.Entry before, WindowHistory.Entry after,
                Native.Rect original, Native.Rect visible, Native.Rect requested, Native.Rect actual,
                Native.MonitorInfo target, bool relativeSize, bool arranged = false)
            {
                if (!SameWindow(before, after) || !before.IsNormal || !after.IsNormal ||
                    !SameMonitor(after.Monitor, target) || !AreaPolicy.Equal(requested, actual) ||
                    !ValidVisibleFrame(original, visible) || !ValidVisibleFrame(before.Monitor.Work, visible)) return null;
                // Retain the measured source, but do not reintroduce a known invisible-
                // border inset at an arranged outer-edge contact. No source size changes.
                var returnBounds = arranged ? MapFramedWindow(original, visible, before.Monitor.Work, before.Monitor.Work, false, true) : original;
                return new ReturnPlacement { Before = before, After = after, Original = original,
                    ReturnBounds = returnBounds, Moved = actual, RelativeSize = relativeSize };
            }

            internal static bool Take(ref ReturnPlacement saved, WindowHistory.Entry current,
                Native.Rect outer, Native.MonitorInfo target, bool relativeSize, out Native.Rect original)
            {
                var record = saved; saved = null; original = new Native.Rect();
                if (record == null || record.HasRelativeAnchor || !SameWindow(record.After, current) || !current.IsNormal ||
                    relativeSize != record.RelativeSize || !AreaPolicy.Equal(outer, record.Moved) ||
                    !SameMonitor(current.Monitor, record.After.Monitor) || !SameMonitor(target, record.Before.Monitor)) return false;
                original = record.ReturnBounds;
                return true;
            }

            internal static void InvalidateManualMove(ref ReturnPlacement saved, IntPtr window)
            {
                if (saved != null && saved.After.Window == window) saved = null;
            }
        }

        internal bool IsExcluded(IntPtr window)
        {
            if (ExcludedApps.Count == 0) return false;
            var app = AppCatalog.ForWindow(window);
            return app == null || AppCatalog.Matches(ExcludedApps, app.Key);
        }

        bool CanDirectMove(TaskbarTargets.Target target, IntPtr monitor, bool physicalNativeInput = false)
        {
            lastDirectCheck = TaskbarDirectDecision.Accepted;
            if (target == null) return RejectDirectCheck(TaskbarDirectDecision.NoTarget);
            if (target.Button == null) return RejectDirectCheck(TaskbarDirectDecision.MissingButton);
            if (!TaskbarNativeSelection.DirectTargetShape(target)) return RejectDirectCheck(target.NeedsNativeSelection && target.ShellProof == null ?
                TaskbarDirectDecision.MissingShellProof : TaskbarDirectDecision.Shape);
            if (target.NeedsNativeSelection && (!physicalNativeInput || !TaskManagerShellIdentity.ProofIsCurrent(target.ShellProof)))
                return RejectDirectCheck(!physicalNativeInput ? TaskbarDirectDecision.Input : TaskbarDirectDecision.StaleShellProof);
            Native.Rect currentTaskbarBounds;
            if (target.NeedsNativeSelection && (!Native.GetWindowRect(target.Taskbar, out currentTaskbarBounds) ||
                !AreaPolicy.Equal(currentTaskbarBounds, target.TaskbarBounds))) return RejectDirectCheck(TaskbarDirectDecision.TaskbarChanged);
            uint pid; GetWindowThreadProcessId(target.Window, out pid);
            if (pid == 0 || pid != target.Process) return RejectDirectCheck(TaskbarDirectDecision.ProcessChanged);
            if (!IsWindowVisible(target.Window)) return RejectDirectCheck(TaskbarDirectDecision.Hidden);
            if (AppCatalog.Matches(ExcludedApps, target.AppKey)) return RejectDirectCheck(TaskbarDirectDecision.Excluded);
            IntPtr source = Native.MonitorFromWindow(target.Window, MonitorDefaultNearest);
            bool fullscreen = WindowShelfCatalog.IsFullscreenForMovement(target.Window);
            bool allowed = DirectMoveAllowed(target.Window != IntPtr.Zero, target.Button.Grouped, source, monitor, fullscreen, AllowMovingFullscreenApps);
            if (!allowed) lastDirectCheck = target.Button.Grouped ? TaskbarDirectDecision.Grouped :
                source == IntPtr.Zero || monitor == IntPtr.Zero ? TaskbarDirectDecision.MonitorUnknown :
                source == monitor ? TaskbarDirectDecision.SameMonitor : fullscreen && !AllowMovingFullscreenApps ?
                TaskbarDirectDecision.Fullscreen : TaskbarDirectDecision.Shape;
            return allowed;
        }
        bool RejectDirectCheck(TaskbarDirectDecision reason) { lastDirectCheck = reason; return false; }

        bool TryStartDeferredClick(TaskbarTargets.Target cached, IntPtr taskbar, IntPtr monitor, Point point, bool physical, uint downTime)
        {
            long began = Stopwatch.GetTimestamp();
            DateTime downUtc = DateTime.UtcNow;
            if (ModifiedClick() || disposed || movement.Busy || !Enabled || deferredClick != null) return false;
            if (!NativeTaskbarHandoff.WithinDeadline(downTime, unchecked((uint)Environment.TickCount))) return false;
            var support = WindowMoveWorker.Administrator;
            if (support != null && support.HasUncertainOperation) return false;
            IntPtr foreground = GetAncestor(foregroundWindow(), GaRoot);
            if (foreground == IntPtr.Zero || !IsCurrentForeground(foreground) || !WindowShelfCatalog.IsTaskbarCandidate(foreground)) return false;
            if (cached != null && (cached.Button == null || cached.Button.Grouped ||
                (cached.Window != IntPtr.Zero && cached.Window != foreground))) return false;
            if (cached != null && cached.NeedsNativeSelection && !physical) return false;
            if (lastDirectCheck == TaskbarDirectDecision.Excluded || lastDirectCheck == TaskbarDirectDecision.Fullscreen ||
                lastDirectCheck == TaskbarDirectDecision.Grouped || lastDirectCheck == TaskbarDirectDecision.SameMonitor) return false;
            uint process;
            if (GetWindowThreadProcessId(foreground, out process) == 0 || process == 0) return false;
            var identity = NativeProcessIdentity.Read(process);
            var caller = NativeProcessIdentity.Current;
            IntPtr sourceMonitor = Native.MonitorFromWindow(foreground, MonitorDefaultNearest);
            bool elevated = NativeProcessIdentity.RequiresAdministrator(caller, identity);
            // An unknown token is not evidence of an ordinary app. Bind the
            // original PID/start/integrity independently of later button flags.
            if (!DeferredTaskbarClick.IdentityCurrent(caller, identity, identity, elevated)) return false;
            if (!DeferredTaskbarClick.CanBegin(physical, ModifiedClick(), Enabled, movement.Busy, true, elevated,
                support != null && support.Active && !support.HasUncertainOperation,
                sourceMonitor != IntPtr.Zero && monitor != IntPtr.Zero && sourceMonitor != monitor, false,
                support != null && support.HasUncertainOperation)) return false;
            if (identity.Started <= 0 || (WindowShelfCatalog.IsFullscreenForMovement(foreground) && !AllowMovingFullscreenApps)) return false;
            uint shell; long shellStarted; Native.Rect taskbarBounds;
            var source = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            var target = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            if (!TaskManagerShellIdentity.ReadTaskbar(taskbar, out shell, out shellStarted) ||
                !Native.GetWindowRect(taskbar, out taskbarBounds) || !Contains(taskbarBounds, point) ||
                MonitorFromPoint(point, MonitorDefaultNearest) != monitor ||
                !Native.GetMonitorInfo(sourceMonitor, ref source) || !Native.GetMonitorInfo(monitor, ref target) ||
                NativeProcessIdentity.Started(process) != identity.Started || !IsCurrentForeground(foreground) || ModifiedClick()) return false;
            uint dpi;
            try { dpi = GetDpiForWindow(taskbar); } catch (EntryPointNotFoundException) { dpi = 96; }
            StopProcessing();
            deferredClick = new DeferredRequest(foreground, process, identity.Started, taskbar, shell, shellStarted,
                taskbarBounds, sourceMonitor, source, monitor, target, point,
                TaskbarInput.DirectDragThreshold(SystemInformation.DragSize, dpi), downTime, ExcludedApps,
                AllowMovingFullscreenApps, ScaleWindowSizeToMonitor, began, downUtc,
                physical, elevated, cached != null && cached.NeedsNativeSelection, identity);
            gestureDiagnostics.DownContext(true, false);
            gestureDiagnostics.Waiting(TaskbarGestureRoute.DeferredPending);
            LastStatus = "Verifying the taskbar icon before moving";
            timer.Start();
            return true;
        }
        bool DeferredGeometryCurrent(DeferredRequest request)
        {
            uint shell; long started; Native.Rect bounds;
            var source = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            var target = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            return TaskManagerShellIdentity.ReadTaskbar(request.Taskbar, out shell, out started) &&
                shell == request.ShellProcess && started == request.ShellStarted &&
                Native.GetWindowRect(request.Taskbar, out bounds) && AreaPolicy.Equal(bounds, request.TaskbarBounds) &&
                Native.GetMonitorInfo(request.SourceMonitor, ref source) && ReturnPlacement.SameMonitor(request.Source, source) &&
                Native.GetMonitorInfo(request.TargetMonitor, ref target) && ReturnPlacement.SameMonitor(request.Target, target) &&
                MonitorFromPoint(request.Origin, MonitorDefaultNearest) == request.TargetMonitor;
        }
        bool DeferredContextCurrent(DeferredRequest request)
        {
            var support = WindowMoveWorker.Administrator;
            uint process;
            if (disposed || !Enabled || ModifiedClick() || (support != null && support.HasUncertainOperation) ||
                (request.RequiresAdministrator && (!request.PhysicalDown || support == null || !support.Active)) ||
                request.FullscreenAllowed != AllowMovingFullscreenApps || request.RelativeSize != ScaleWindowSizeToMonitor ||
                !Object.ReferenceEquals(request.Rules, ExcludedApps) || request.RuleKeys.Count != ExcludedApps.Count ||
                !IsCurrentForeground(request.Window) || !WindowShelfCatalog.IsTaskbarCandidate(request.Window) ||
                GetWindowThreadProcessId(request.Window, out process) == 0 || process != request.Process ||
                !DeferredTaskbarClick.IdentityCurrent(NativeProcessIdentity.Current, request.Identity,
                    NativeProcessIdentity.Read(process), request.RequiresAdministrator) ||
                Native.MonitorFromWindow(request.Window, MonitorDefaultNearest) != request.SourceMonitor || !DeferredGeometryCurrent(request)) return false;
            for (int i = 0; i < request.RuleKeys.Count; i++)
                if (request.RuleKeys[i] != (ExcludedApps[i] == null ? null : ExcludedApps[i].Key)) return false;
            return true;
        }
        void ReleaseDeferredClick(MouseData input)
        {
            var request = deferredClick;
            if (request == null) return;
            request.End = input.Position; request.UpTime = input.Time;
            request.PhysicalUp = TaskbarNativeSelection.IsPhysicalPointer(input.Flags);
            bool dragged = TaskbarInput.IsDirectDrag(request.Origin, request.End, request.DragThreshold);
            request.State.Release(request.PhysicalUp, ModifiedClick(), Contains(request.TaskbarBounds, request.End), dragged, Stopwatch.GetTimestamp());
            if (request.PhysicalUp != request.PhysicalDown) {
                FinishDeferredClick(request, request.State.Abort(DeferredGeometryCurrent(request)), false); return;
            }
            if (dragged || ModifiedClick() || !Contains(request.TaskbarBounds, request.End)) {
                FinishDeferredClick(request, request.State.Abort(DeferredGeometryCurrent(request)), false); return;
            }
            request.Verification = targets.Verify(request.Taskbar, request.Origin,
                new TaskbarTargets.PreferredWindow(request.Window, request.Process, request.Started));
            timer.Start();
        }
        void AbortDeferredClick()
        {
            var request = deferredClick;
            if (request != null) FinishDeferredClick(request, request.State.Abort(DeferredGeometryCurrent(request)), false);
        }
        void FinishDeferredClick(DeferredRequest request, DeferredTaskbarAction action, bool allowLegacy)
        {
            if (action == DeferredTaskbarAction.None) return;
            if (Object.ReferenceEquals(deferredClick, request)) deferredClick = null;
            bool replayed = false;
            bool released = action == DeferredTaskbarAction.ReplayClick;
            bool resumeLegacy = allowLegacy && released && request.State.Released && request.PhysicalUp == request.PhysicalDown && !disposed && Enabled && !ModifiedClick() &&
                TaskbarNativeSelection.IsFreshGesture(request.DownTime, unchecked((uint)Environment.TickCount)) && DeferredContextCurrent(request);
            if (resumeLegacy) { activatedWindow = IntPtr.Zero; activatedAt = DateTime.MinValue; }
            if (action == DeferredTaskbarAction.ReplayDown || released) {
                Point? observedCursor = replayObservation.Cursor();
                if (DeferredGeometryCurrent(request) && observedCursor.HasValue) {
                    Point cursor = observedCursor.Value;
                    if (!released && !replayObservation.LeftButtonDown()) released = true;
                    bool safeReleased = !released || (replayObservation.RootAtPoint(cursor) == request.Taskbar &&
                        !TaskbarInput.IsDrag(request.End, cursor, SystemInformation.DragSize));
                    if (safeReleased) replayed = replayInput(request.Origin, released ? request.End : cursor, released);
                }
            }
            if (resumeLegacy && replayed && released && !disposed && Enabled && !ModifiedClick() &&
                TaskbarNativeSelection.IsFreshGesture(request.DownTime, unchecked((uint)Environment.TickCount)) && DeferredGeometryCurrent(request)) {
                click = new Click { Position = request.Origin, Taskbar = request.Taskbar, TargetMonitor = request.TargetMonitor,
                    PriorWindow = request.Window, PriorProcess = request.Process, PriorMonitor = request.SourceMonitor,
                    Time = request.DownUtc, ReleasedAt = DateTime.UtcNow, NativeGesture = request.PhysicalDown && request.PhysicalUp, NativeDownTime = request.DownTime,
                    Verification = targets.Verify(request.Taskbar, request.Origin,
                        new TaskbarTargets.PreferredWindow(request.Window, request.Process, request.Started)) };
                gestureDiagnostics.Waiting(TaskbarGestureRoute.NativePending); timer.Start();
                return;
            }
            gestureDiagnostics.Complete(action == DeferredTaskbarAction.ReplayClick || action == DeferredTaskbarAction.ReplayDown ?
                TaskbarGestureRoute.DeferredReplay : TaskbarGestureRoute.Deferred, false, replayed);
            if (!disposed && click == null && directRelease == null) timer.Stop();
        }
        void ProcessDeferredClick()
        {
            var request = deferredClick;
            if (request == null) return;
            if (!DeferredGeometryCurrent(request)) { FinishDeferredClick(request, request.State.Abort(false), false); return; }
            bool context = DeferredContextCurrent(request);
            bool complete = request.Verification != null && request.Verification.Complete;
            var target = complete ? request.Verification.Result : null;
            if (complete) ObserveGestureProbe(request.Verification);
            bool verified = false;
            if (request.State.Released && complete && context) {
                verified = DeferredTaskbarClick.MatchesTarget(target, request.Window, request.Process, request.Started) && target.Taskbar == request.Taskbar &&
                    target.Button != null && Contains(target.Button.ButtonBounds, request.Origin) && Contains(target.Button.ButtonBounds, request.End) &&
                    AreaPolicy.Equal(target.TaskbarBounds, request.TaskbarBounds) &&
                    (!request.NativeSelectionRequired || target.NeedsNativeSelection) &&
                    DeferredTaskbarClick.GestureCurrent(DeferredTaskbarClick.ProtectedInput(request.RequiresAdministrator,
                        request.NativeSelectionRequired, target.NeedsNativeSelection), request.PhysicalDown, request.PhysicalUp,
                        request.DownTime, request.UpTime, unchecked((uint)Environment.TickCount), ModifiedClick()) &&
                    CanDirectMove(target, request.TargetMonitor, request.PhysicalDown);
                gestureDiagnostics.Verified(verified);
            }
            DeferredTaskbarAction action = request.State.Evaluate(complete, verified, context, Stopwatch.GetTimestamp());
            if (action != DeferredTaskbarAction.Move) { FinishDeferredClick(request, action, context); return; }
            if (!DeferredContextCurrent(request)) { FinishDeferredClick(request, request.State.Abort(true), false); return; }
            bool moved = MoveToMonitor(request.Window, request.TargetMonitor);
            action = request.State.AfterMove(moved, movementMayHaveChanged, movementInterrupted);
            if (moved) gestureDiagnostics.Complete(TaskbarGestureRoute.Deferred, true, false);
            FinishDeferredClick(request, action, false);
            SetMoveStatus(moved, "Moved window after verifying its taskbar icon", "The verified window could not be moved");
        }

        static bool ModifiedClick()
        {
            return ModifierMask() != 0;
        }
        static int ModifierMask()
        {
            int result = 0;
            for (int index = 0; index < ModifierKeys.Length; index++) if ((GetAsyncKeyState(ModifierKeys[index]) & 0x8000) != 0) result |= 1 << index;
            return result;
        }

        internal bool TryStartDirectMove(TaskbarTargets.Target target, IntPtr monitor)
        {
            return TryStartDirectMove(target, monitor, target == null || target.Button == null ? new Point() : target.Button.Position);
        }

        internal bool TryStartDirectMove(TaskbarTargets.Target target, IntPtr monitor, Point pressPoint)
        { return TryStartDirectMove(target, monitor, pressPoint, false, unchecked((uint)Environment.TickCount)); }

        internal bool TryStartDirectMove(TaskbarTargets.Target target, IntPtr monitor, Point pressPoint, bool physicalNativeInput)
        { return TryStartDirectMove(target, monitor, pressPoint, physicalNativeInput, unchecked((uint)Environment.TickCount)); }

        internal bool TryStartDirectMove(TaskbarTargets.Target target, IntPtr monitor, Point pressPoint, bool physicalNativeInput, uint nativeDownTime)
        {
            // Inactive apps need Explorer's original click to receive foreground permission.
            bool native = target != null && target.NeedsNativeSelection;
            if (disposed || movement.Busy || !Enabled) {
                gestureDiagnostics.Direct(disposed ? TaskbarDirectDecision.Disposed : movement.Busy ? TaskbarDirectDecision.Busy : TaskbarDirectDecision.Disabled); return false;
            }
            if (!TaskbarNativeSelection.DirectGestureAllowed(native, physicalNativeInput, physicalNativeInput,
                nativeDownTime, nativeDownTime, unchecked((uint)Environment.TickCount), ModifiedClick())) {
                gestureDiagnostics.Direct(TaskbarDirectDecision.Input); return false;
            }
            if (!CanDirectMove(target, monitor, physicalNativeInput)) { gestureDiagnostics.Direct(lastDirectCheck); return false; }
            bool directForeground = IsCurrentForeground(target.Window);
            gestureDiagnostics.DownContext(directForeground, false);
            if (!directForeground) { gestureDiagnostics.Direct(TaskbarDirectDecision.Foreground); return false; }
            Native.Rect taskbarBounds;
            if (native && (!Contains(target.Button.ButtonBounds, pressPoint) ||
                MonitorFromPoint(pressPoint, MonitorDefaultNearest) != monitor ||
                !Native.GetWindowRect(target.Taskbar, out taskbarBounds) || !AreaPolicy.Equal(taskbarBounds, target.TaskbarBounds))) {
                gestureDiagnostics.Direct(TaskbarDirectDecision.PointChanged); return false;
            }
            gestureDiagnostics.Direct(TaskbarDirectDecision.Accepted);
            StopProcessing();
            directPress = target; directMonitor = monitor;
            directOrigin = pressPoint; directEnd = directOrigin; directCancelled = false;
            directSourceMonitor = Native.MonitorFromWindow(target.Window, MonitorDefaultNearest);
            directPhysicalDown = physicalNativeInput; directPhysicalUp = false;
            directNativeDownTime = nativeDownTime; directNativeUpTime = 0;
            uint dpi;
            try { dpi = GetDpiForWindow(target.Taskbar); }
            catch (EntryPointNotFoundException) { dpi = 96; }
            directDragThreshold = TaskbarInput.DirectDragThreshold(SystemInformation.DragSize, dpi);
            LastStatus = "Ready to move window";
            return true;
        }

        internal void CompleteDirectPress(bool inside)
        { CompleteDirectPress(inside, false, unchecked((uint)Environment.TickCount)); }

        internal void CompleteDirectPress(bool inside, bool physicalNativeInput, uint nativeUpTime)
        {
            var pressed = directPress; directPress = null;
            if (pressed != null && Enabled && inside && !directCancelled && !ModifiedClick()) {
                if (directPhysicalDown != physicalNativeInput ||
                    !TaskbarNativeSelection.DirectGestureAllowed(pressed.NeedsNativeSelection, directPhysicalDown,
                    physicalNativeInput, directNativeDownTime, nativeUpTime, unchecked((uint)Environment.TickCount), false)) {
                    gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                    StopProcessing(); LastStatus = "Taskbar input changed; direct move cancelled"; return;
                }
                if (!IsCurrentForeground(pressed.Window)) {
                    gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                    StopProcessing(); LastStatus = "Foreground changed; direct move cancelled"; return;
                }
                directRelease = pressed;
                directPhysicalUp = physicalNativeInput; directNativeUpTime = nativeUpTime;
                directReleasedAt = DateTime.UtcNow;
                directCheck = targets.Verify(pressed.Taskbar, directOrigin);
                timer.Start();
            } else if (pressed != null) gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
        }

        internal static bool DirectMoveAllowed(bool resolved, bool grouped, IntPtr source, IntPtr target, bool fullscreen, bool allowFullscreen = false)
        {
            return resolved && !grouped && (!fullscreen || allowFullscreen) && source != IntPtr.Zero && target != IntPtr.Zero && source != target;
        }

        internal static bool DirectReplayAllowed(bool success, bool interrupted, bool disposed, bool verified, bool mayHaveChanged)
        {
            // A failed proof did not attempt this gesture's move, even if an
            // earlier gesture left the diagnostic mutation flag set. After a
            // verified move starts, ordinary and elevated targets share the
            // same no-replay rule for a partial or uncertain mutation.
            return !success && !interrupted && !disposed && (!verified || !mayHaveChanged);
        }

        bool IsCurrentForeground(IntPtr window)
        {
            return window != IntPtr.Zero && !IsIconic(window) && GetAncestor(foregroundWindow(), GaRoot) == window;
        }

        internal bool MoveFromShelf(ShelfWindow selected, Display display)
        {
            if (disposed || !ActionAllowed || movement.Busy || selected == null || display == null) return false;
            movementInterrupted = false;
            IntPtr foreground = selected.SelectionTimestamp != 0 ? selected.SelectionForeground : foregroundWindow();
            long captured = selected.SelectionTimestamp != 0 ? selected.SelectionTimestamp : Stopwatch.GetTimestamp();
            IntPtr destination = IntPtr.Zero;
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(monitor, ref info) && info.Device == display.Device) { destination = monitor; return false; }
                return true;
            }, IntPtr.Zero);
            if (destination == IntPtr.Zero) return false;
            CancelPending();
            ShelfWindow current;
            if (!WindowShelfCatalog.TryReadSelection(selected, display, ExcludedApps, ShelfMinimizes, out current, AllowMovingFullscreenApps)) {
                LastStatus = "Window is no longer available for the Ghost Bar";
                return false;
            }
            if (Native.MonitorFromWindow(current.Handle, MonitorDefaultNearest) == destination) {
                if (!ActionAllowed) return false;
                if (!WindowShelfPolicy.FreshSelection(captured, Stopwatch.GetTimestamp())) {
                    LastStatus = "The Ghost Bar selection expired"; return false;
                }
                ShelfWindow pendingMinimize;
                // A second click can arrive before ShowWindowAsync has minimized the app.
                // Queue restore behind that accepted request, never a second minimize.
                bool restoring = IsIconic(current.Handle) || ShelfMinimizes.TryRead(current, ExcludedApps, out pendingMinimize);
                var action = WindowShelfPolicy.LocalAction(restoring, foreground == current.Handle, selected.SelectionOrigin);
                if (action == ShelfLocalAction.Focus) {
                    bool focused = GetForegroundWindow() == current.Handle || FocusFromShelf(current.Handle);
                    SetMoveStatus(focused, "Focused window from the Ghost Bar", "The selected window could not be focused");
                    return focused;
                }
                if (action == ShelfLocalAction.Minimize) {
                    bool restoreMaximized = IsZoomed(current.Handle);
                    if (!ActionAllowed) return false;
                    bool minimized = Native.ShowWindowAsync(current.Handle, 6);
                    if (minimized) ShelfMinimizes.RecordSuccessfulMinimize(current, restoreMaximized);
                    LastStatus = minimized ? "Minimized window from the Ghost Bar" : "The selected window could not be minimized";
                    return minimized;
                }
                // SW_RESTORE honors the native restore-to-maximized state without rewriting
                // restore bounds, applying cross-monitor sizing, or touching move history.
                int restoreMode = ShelfMinimizes.RestoreMaximized(current, ExcludedApps) ? 3 : 9;
                if (!ActionAllowed) return false;
                bool restored = Native.ShowWindowAsync(current.Handle, restoreMode);
                if (restored) { ShelfMinimizes.RequestedRestore(current); FocusFromShelf(current.Handle); }
                SetMoveStatus(restored, "Restored window from the Ghost Bar", "The selected window could not be restored");
                return restored;
            }
            bool moved = MoveToMonitor(current.Handle, destination, current);
            SetMoveStatus(moved, "Moved window from the Ghost Bar", "The selected window could not be moved");
            return moved;
        }

        internal bool MoveFocusedWindow(IntPtr foreground, Display display, int number)
        {
            if (!ActionAllowed) { LastStatus = "Window movement is paused for recovery"; return false; }
            if (disposed || movement.Busy) { LastStatus = "A window move is already pending"; return false; }
            if (foreground == IntPtr.Zero || foregroundWindow() != foreground || GetAncestor(foreground, GaRoot) != foreground) {
                LastStatus = "The focused window changed; no window moved"; return false;
            }
            if (display == null) { LastStatus = "Display " + number + " is unavailable; no window moved"; return false; }
            if (!AllowMovingFullscreenApps && WindowShelfCatalog.IsFullscreenForMovement(foreground)) {
                LastStatus = "Enable Allow moving fullscreen apps first"; return false;
            }
            ShelfWindow expected, current;
            if (!WindowShelfCatalog.TryReadWindow(foreground, AllowMovingFullscreenApps, out expected) ||
                !WindowShelfCatalog.TryReadSelection(expected, display, ExcludedApps, ShelfMinimizes, out current, AllowMovingFullscreenApps) || foregroundWindow() != foreground) {
                LastStatus = "Focused window is unavailable or excluded; no window moved"; return false;
            }
            IntPtr destination = IntPtr.Zero;
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(monitor, ref info) && info.Device == display.Device && info.Monitor.Left == display.X && info.Monitor.Top == display.Y &&
                    info.Monitor.Right - info.Monitor.Left == display.Width && info.Monitor.Bottom - info.Monitor.Top == display.Height) { destination = monitor; return false; }
                return true;
            }, IntPtr.Zero);
            if (destination == IntPtr.Zero) { LastStatus = "Display " + number + " changed or is unavailable; no window moved"; return false; }
            if (Native.MonitorFromWindow(foreground, MonitorDefaultNearest) == destination) { LastStatus = "Window is already on Display " + number; return true; }
            CancelPending();
            bool moved = MoveToMonitor(foreground, destination, current);
            if (LastStatus == "App excluded from window movement") return false;
            SetMoveStatus(moved, "Moved focused window to Display " + number, "Focused window could not be moved");
            return moved;
        }
        void StartFullscreenObservation(ShelfWindow window, Native.MonitorInfo target)
        {
            observedFullscreen = window; observedTarget = target;
            observeAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            LastStatus = "Fullscreen move requested; checking full monitor bounds (Undo available)";
            fullscreenObservation.Start();
        }
        void ObserveFullscreen()
        {
            if (observedFullscreen == null || disposed) { fullscreenObservation.Stop(); return; }
            if (Stopwatch.GetTimestamp() < observeAfter) return;
            ShelfWindow current; Native.Rect actual; Native.MonitorInfo target;
            bool arrived = WindowShelfCatalog.TryReadWindow(observedFullscreen.Handle, true, out current) && WindowShelfPolicy.SameWindow(observedFullscreen, current) &&
                Native.TryMonitor(observedTarget.Device, out target) && AreaPolicy.Equal(target.Monitor, observedTarget.Monitor) &&
                Native.GetWindowRect(current.Handle, out actual) && FullscreenMovePolicy.Matches(actual, observedTarget.Monitor);
            LastStatus = arrived ? "Fullscreen window observed on " + observedTarget.Device.Replace(@"\\.\DISPLAY", "Display ") + "; the app can move itself later" :
                "Fullscreen app refused, moved back, or did not complete in time; try Undo";
            observedFullscreen = null; fullscreenObservation.Stop();
            if (FullscreenChecked != null) FullscreenChecked(this, EventArgs.Empty);
        }

        internal bool MoveToMonitor(IntPtr window, IntPtr targetMonitor)
        { return MoveToMonitor(window, targetMonitor, null); }
        bool AuthorizedHiddenMinimize(ShelfWindow expected)
        {
            ShelfWindow current;
            return expected != null && ShelfMinimizes.TryRead(expected, ExcludedApps, out current) &&
                !IsWindowVisible(current.Handle) && IsIconic(current.Handle);
        }
        bool MoveToMonitor(IntPtr window, IntPtr targetMonitor, ShelfWindow shelfSelection)
        {
            if (!BeginMovement(shelfSelection != null, false)) return false;
            fullscreenObservation.Stop(); observedFullscreen = null;
            try { return MoveToMonitorCore(window, targetMonitor, shelfSelection); }
            catch (MovementInterruptedException) {
                movementInterrupted = true;
                lastMove = null;
                if (!disposed) LastStatus = movement.Expired ? "Window move timed out; no further move was queued" : "Window move cancelled";
                return false;
            } finally { EndMovement(); }
        }
        bool MoveToMonitorCore(IntPtr window, IntPtr targetMonitor, ShelfWindow shelfSelection)
        {
            var previousMove = lastMove;
            bool hidden = !IsWindowVisible(window);
            if (!IsWindow(window) || (hidden && (shelfSelection == null || shelfSelection.Handle != window || !AuthorizedHiddenMinimize(shelfSelection)))) return false;
            if (IsExcluded(window)) { LastStatus = "App excluded from window movement"; return false; }
            // Taskbar, Ghost Bar and shortcut moves share the catalog's classifier,
            // including captionless maximized windows. Recovery Undo remains usable.
            bool fullscreen; Native.Rect fullscreenBounds;
            if (!WindowShelfCatalog.TryReadFullscreenMovement(window, out fullscreen, out fullscreenBounds)) {
                preserveMoveStatus = true;
                LastStatus = "Restore this window first, then retry the move; its fullscreen restore bounds are unavailable";
                return false;
            }
            if (fullscreen && !AllowMovingFullscreenApps) { LastStatus = "Enable Allow moving fullscreen apps first"; return false; }
            IntPtr sourceMonitor = Native.MonitorFromWindow(window, MonitorDefaultNearest);
            if (sourceMonitor == IntPtr.Zero || sourceMonitor == targetMonitor) return true;
            var source = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            var target = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            Native.Rect current;
            if (!Native.GetMonitorInfo(sourceMonitor, ref source) || !Native.GetMonitorInfo(targetMonitor, ref target) || !Native.GetWindowRect(window, out current)) return false;
            movementSource = source; movementDestination = target; movementTopology = true;
            var before = MoveApi.Capture(window);
            // Every native mutation needs a usable recovery snapshot, including
            // visible windows. A rejected move must leave the earlier move intact.
            if (before == null) return false;
            if (shelfSelection != null && (before.Process != shelfSelection.Process || before.ProcessStarted != shelfSelection.ProcessStarted)) return false;
            movementBefore = before; EnsureMovementCurrent();
            if (fullscreen) {
                ShelfWindow identity;
                if (!WindowShelfCatalog.TryReadWindow(window, true, out identity) || identity.Process != before.Process || identity.ProcessStarted != before.ProcessStarted ||
                    (shelfSelection != null && !WindowShelfPolicy.SameWindow(shelfSelection, identity))) return false;
                before.FullscreenBounds = fullscreenBounds;
                bool recorded = false;
                if (IsIconic(window)) {
                    // Restore only after the shared gate and recovery capture.
                    // WINDOWPLACEMENT retains source restore geometry; subsequent
                    // positioning requests the full destination bounds, never a
                    // work-area resize of a minimized fullscreen app.
                    var restore = before.Placement;
                    restore.ShowCommand = restore.ShowCommand == 3 ? 3 : 4;
                    if (!PlaceWindow(window, ref restore)) return false;
                    RememberMove(before, ref recorded);
                    while (IsIconic(window)) {
                        EnsureMovementCurrent(); Application.DoEvents();
                        EnsureMovementCurrent(); System.Threading.Thread.Sleep(5);
                    }
                }
                // Use the unchanged worker/coordinator and the same identity/deadline
                // authorization as every native move; only the requested bounds differ.
                var bounds = target.Monitor;
                bool fullMoved = PositionWindow(window, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                    SwpNoActivate | SwpNoOwnerZOrder | SwpNoZOrder);
                if (fullMoved) {
                    RememberMove(before, ref recorded);
                    FocusWindow(window);
                    StartFullscreenObservation(identity, target);
                }
                return fullMoved;
            }
            if (shelfSelection != null && ShelfMinimizes.RestoreMaximized(shelfSelection, ExcludedApps))
                before.Placement.ShowCommand = 3;
            bool moved;
            bool remembered = false;
            bool wasMinimized = IsIconic(window);
            if (wasMinimized || IsZoomed(window)) {
                var placement = before.Placement;
                // WINDOWPLACEMENT uses workspace coordinates for ordinary app windows.
                // Preserve restore bounds while relocating a maximized window intact.
                var normal = placement.NormalPosition;
                Offset(ref normal, source.Work.Left - source.Monitor.Left, source.Work.Top - source.Monitor.Top);
                normal = MapWindow(normal, source.Work, target.Work);
                var normalScreen = normal;
                Offset(ref normal, target.Monitor.Left - target.Work.Left, target.Monitor.Top - target.Work.Top);
                placement.NormalPosition = normal;
                placement.MaxPosition = new WindowHistory.Point { X = -1, Y = -1 };
                if (!wasMinimized) {
                    if (!PositionWindow(window, IntPtr.Zero, target.Work.Left, target.Work.Top,
                        target.Work.Right - target.Work.Left, target.Work.Bottom - target.Work.Top,
                        SwpNoActivate | SwpNoOwnerZOrder | SwpNoZOrder)) return false;
                    RememberMove(before, ref remembered);
                }
                if (hidden && !AuthorizedHiddenMinimize(shelfSelection)) return false;
                moved = PlaceWindow(window, ref placement);
                if (moved) RememberMove(before, ref remembered);
                // Some apps reapply their own floating bounds during the normal restore.
                // Complete this move once after restoration; never unmaximize a restore.
                Native.Rect restoredBounds;
                if (moved && wasMinimized && placement.ShowCommand == 1 && !IsIconic(window) && !IsZoomed(window) &&
                    Native.GetWindowRect(window, out restoredBounds) && !AreaPolicy.Equal(restoredBounds, normalScreen))
                    moved = PositionWindow(window, IntPtr.Zero, normalScreen.Left, normalScreen.Top,
                        normalScreen.Right - normalScreen.Left, normalScreen.Bottom - normalScreen.Top,
                        SwpNoActivate | SwpNoOwnerZOrder | SwpNoZOrder);
            } else {
                bool arranged = before.ArrangedBounds.HasValue;
                bool relative = ScaleWindowSizeToMonitor;
                // Keep the source geometry and identity from the same capture.
                if (relative && before.VisibleBounds.HasValue) current = before.VisibleBounds.Value;
                Native.Rect visible;
                // This process is PerMonitorV2: GetWindowRect and DWM bounds are physical
                // screen pixels. Never use minimized/maximized DWM bounds for restore geometry.
                bool verifiedFrame = Native.DwmGetWindowAttribute(window, 9, out visible, Marshal.SizeOf(typeof(Native.Rect))) == 0 &&
                    ValidVisibleFrame(current, visible);
                if (!verifiedFrame)
                    visible = current;
                ReturnPlacement continued = null;
                Native.Rect mapped;
                Native.MonitorInfo originNow;
                if (relative && previousMove != null && previousMove.HasRelativeAnchor &&
                    Native.TryMonitor(previousMove.Before.Monitor.Device, out originNow) &&
                    ReturnPlacement.TryMapRelative(previousMove, before, current, visible, target, originNow, out mapped)) {
                    continued = previousMove;
                } else if (!ReturnPlacement.Take(ref previousMove, before, current, target, ScaleWindowSizeToMonitor, out mapped))
                    mapped = MapFramedWindow(current, visible, source.Work, target.Work, ScaleWindowSizeToMonitor, arranged);
                moved = PositionWindow(window, IntPtr.Zero, mapped.Left, mapped.Top, mapped.Right - mapped.Left, mapped.Bottom - mapped.Top,
                    SwpNoActivate | SwpNoOwnerZOrder | SwpNoZOrder);
                if (moved) {
                    RememberMove(before, ref remembered);
                    if (relative) CompleteRelativeMove(window, continued, before, current, visible, mapped, source, target);
                    else {
                        Native.Rect actual;
                        var after = MoveApi.Capture(window);
                        EnsureMovementCurrent();
                        if (foregroundHook != IntPtr.Zero && verifiedFrame && ReturnPlacement.SameMonitor(before.Monitor, source) &&
                            Native.GetWindowRect(window, out actual))
                            lastMove = ReturnPlacement.Create(before, after, current, visible, mapped, actual, target, ScaleWindowSizeToMonitor, arranged);
                    }
                }
            }
            if (moved) FocusWindow(window);
            return moved;
        }

        void CompleteRelativeMove(IntPtr window, ReturnPlacement continued, WindowHistory.Entry before, Native.Rect original,
            Native.Rect originalVisible, Native.Rect requested, Native.MonitorInfo source, Native.MonitorInfo target)
        {
            WindowHistory.Entry after;
            Native.Rect actual, visible;
            if (!ObserveRelativeMove(window, before, out after, out actual, out visible)) return;
            bool onTarget = ReturnPlacement.SameMonitor(after.Monitor, target);
            bool atRequestedOrigin = actual.Left == requested.Left && actual.Top == requested.Top &&
                OverlapsWorkArea(visible, target.Work);
            Native.Rect fitted;
            if ((onTarget || atRequestedOrigin) && TryFitMovedWindow(actual, visible, target.Work, out fitted)) {
                // Keep the size the app actually accepted. This uses the same
                // bounded worker, deadline, and Undo snapshot as the first move.
                Native.Rect latest;
                if (Native.GetWindowRect(window, out latest) && AreaPolicy.Equal(latest, actual))
                    PositionWindow(window, IntPtr.Zero, fitted.Left, fitted.Top, fitted.Right - fitted.Left, fitted.Bottom - fitted.Top,
                        SwpNoActivate | SwpNoOwnerZOrder | SwpNoZOrder);
                if (!ObserveRelativeMove(window, before, out after, out actual, out visible)) return;
            }
            if (ReturnPlacement.SameMonitor(before.Monitor, source))
                lastMove = ReturnPlacement.CreateRelative(continued, before, after, original, originalVisible, actual, visible, target);
            if (!ValidVisibleFrame(target.Work, visible)) {
                preserveMoveStatus = true;
                LastStatus = "The app did not fit within this display; Undo is available";
            }
        }

        bool ObserveRelativeMove(IntPtr window, WindowHistory.Entry before, out WindowHistory.Entry after,
            out Native.Rect actual, out Native.Rect visible)
        {
            after = MoveApi.Capture(window);
            EnsureMovementCurrent();
            actual = visible = new Native.Rect();
            if (after == null || after.Window != before.Window || after.Process != before.Process ||
                after.ProcessStarted != before.ProcessStarted || !after.IsNormal || after.FullscreenBounds.HasValue) return false;
            if (after.VisibleBounds.HasValue) actual = after.VisibleBounds.Value;
            else if (!Native.GetWindowRect(window, out actual)) return false;
            if (!ValidVisibleFrame(actual, actual)) return false;
            if (Native.DwmGetWindowAttribute(window, 9, out visible, Marshal.SizeOf(typeof(Native.Rect))) != 0 || !ValidVisibleFrame(actual, visible))
                visible = actual;
            return true;
        }

        void RememberMove(WindowHistory.Entry before, ref bool remembered)
        {
            if (remembered) return;
            // Preserve recovery as soon as the first native change succeeds. A
            // later placement/restore correction may fail or throw after that.
            history.Remember(before);
            lastMove = null;
            remembered = true; movementRemembered = true;
        }

        static void Offset(ref Native.Rect rect, int x, int y)
        {
            rect.Left += x; rect.Right += x; rect.Top += y; rect.Bottom += y;
        }

        internal static bool ClickHasSettled(DateTime releasedAt, DateTime now)
        {
            return releasedAt != DateTime.MinValue && now >= releasedAt;
        }

        internal bool FreshForegroundClickMatches(Click candidate, TaskbarTargets.Target resolved, DateTime now)
        {
            if (candidate == null || candidate.Origin != null || candidate.PreviewOnly || candidate.SelectionStarted ||
                candidate.Grouped || resolved == null || resolved.NeedsNativeSelection || resolved.Button == null || resolved.Button.Grouped ||
                candidate.Verification == null || !candidate.Verification.Complete || candidate.Verification.Result != resolved ||
                !String.Equals(candidate.AppId, resolved.Button.AppId, StringComparison.OrdinalIgnoreCase) ||
                resolved.Windows == null || resolved.Windows.Length != 1 ||
                resolved.Windows[0].Window != resolved.Window || resolved.Windows[0].Process != resolved.Process ||
                resolved.Window == IntPtr.Zero || resolved.Window != candidate.PriorWindow ||
                candidate.PriorProcess == 0 || resolved.Process != candidate.PriorProcess ||
                resolved.Taskbar != candidate.Taskbar || !Contains(resolved.Button.ButtonBounds, candidate.Position) ||
                candidate.PriorMonitor == IntPtr.Zero || candidate.PriorMonitor == candidate.TargetMonitor ||
                candidate.ReleasedAt == DateTime.MinValue || now < candidate.ReleasedAt.AddMilliseconds(250) ||
                now > candidate.ReleasedAt.AddSeconds(1) ||
                resolved.Captured < candidate.Time || resolved.Captured > now || now - resolved.Captured > TimeSpan.FromSeconds(1) ||
                !IsWindowVisible(resolved.Window) || IsIconic(resolved.Window) ||
                Native.MonitorFromWindow(resolved.Window, MonitorDefaultNearest) != candidate.PriorMonitor ||
                GetAncestor(foregroundWindow(), GaRoot) != resolved.Window || !MatchesApp(candidate, resolved.Window)) return false;
            Native.Rect bounds;
            return Native.GetWindowRect(resolved.Taskbar, out bounds) && AreaPolicy.Equal(bounds, resolved.TaskbarBounds) &&
                CanDirectMove(resolved, candidate.TargetMonitor);
        }

        internal static bool NativeMinimizeMatches(Click candidate, TaskbarTargets.Target resolved)
        {
            if (candidate == null || candidate.PreviewOnly || candidate.SelectionStarted || candidate.Grouped ||
                resolved == null || resolved.Button == null || resolved.Button.Grouped ||
                candidate.Verification == null || !candidate.Verification.Complete || candidate.Verification.Result != resolved ||
                (candidate.Origin != null && !TaskbarTargets.SameButton(candidate.Origin, resolved)) ||
                resolved.Window == IntPtr.Zero || resolved.Window != candidate.PriorWindow ||
                candidate.PriorProcess == 0 || resolved.Process != candidate.PriorProcess ||
                candidate.PriorMonitor == IntPtr.Zero ||
                Native.MonitorFromWindow(resolved.Window, MonitorDefaultNearest) != candidate.PriorMonitor ||
                !IsWindowVisible(resolved.Window) || !IsIconic(resolved.Window) ||
                !(resolved.NeedsNativeSelection ? TaskbarNativeSelection.HasMinimizeProof(candidate, resolved, resolved.Window, true,
                    unchecked((uint)Environment.TickCount), DateTime.UtcNow) && NativeSystemSelectionMatches(candidate, resolved, resolved.Window) : MatchesApp(candidate, resolved.Window))) return false;
            uint process;
            return GetWindowThreadProcessId(resolved.Window, out process) != 0 && process == candidate.PriorProcess;
        }

        static bool NativeSystemSelectionMatches(Click candidate, TaskbarTargets.Target target, IntPtr window)
        {
            if (!TaskbarNativeSelection.HasGestureShape(candidate, target, window,
                unchecked((uint)Environment.TickCount), DateTime.UtcNow)) return false;
            uint process; long started; Native.Rect bounds;
            return GetWindowThreadProcessId(window, out process) != 0 && process == target.Process && IsWindowVisible(window) &&
                (target.ShellOnly ? TaskbarNativeSelection.HasBoundShellSelectionProof(candidate, target, window,
                    unchecked((uint)Environment.TickCount), DateTime.UtcNow) :
                    TaskbarNativeSelection.TryIdentifyTaskManager(candidate, window, out started) && started == target.ProcessStarted) &&
                Native.GetWindowRect(target.Taskbar, out bounds) && AreaPolicy.Equal(bounds, target.TaskbarBounds);
        }

        bool NativeSystemActivationMatches(Click candidate, TaskbarTargets.Target target, IntPtr window)
        {
            return TaskbarNativeSelection.HasActivationProof(candidate, target, window, IsCurrentForeground(window), IsIconic(window),
                activatedNativeTime, unchecked((uint)Environment.TickCount), DateTime.UtcNow) &&
                NativeSystemSelectionMatches(candidate, target, window);
        }

        internal void ProcessClick()
        {
            if (disposed || movement.Busy) return;
            movementInterrupted = false;
            if (!Enabled) { StopProcessing(); return; }
            if (deferredClick != null) { ProcessDeferredClick(); return; }
            if (directRelease != null) {
                if (directRelease.NeedsNativeSelection && ModifiedClick()) {
                    gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                    StopProcessing(); LastStatus = "Modified taskbar click; direct move cancelled"; return;
                }
                if (!IsCurrentForeground(directRelease.Window)) {
                    gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                    StopProcessing(); LastStatus = "Foreground changed; direct move cancelled"; return;
                }
                if (!directCheck.Complete && DateTime.UtcNow - directReleasedAt < TimeSpan.FromMilliseconds(750)) return;
                bool verificationTimedOut = !directCheck.Complete;
                var expected = directRelease;
                var target = verificationTimedOut ? null : directCheck.Result;
                IntPtr monitor = directMonitor;
                ObserveGestureProbe(directCheck);
                StopProcessing();
                bool verified = TaskbarTargets.SameButton(expected, target) && CanDirectMove(target, monitor, directPhysicalDown);
                if (expected.NeedsNativeSelection) {
                    verified = verified && TaskbarNativeSelection.DirectGestureAllowed(true, directPhysicalDown, directPhysicalUp,
                        directNativeDownTime, directNativeUpTime, unchecked((uint)Environment.TickCount), ModifiedClick()) &&
                        TaskManagerShellIdentity.ProofsMatch(expected.ShellProof, target.ShellProof) &&
                        TaskManagerShellIdentity.ProofIsCurrent(expected.ShellProof) &&
                        Native.MonitorFromWindow(expected.Window, MonitorDefaultNearest) == directSourceMonitor &&
                        MonitorFromPoint(directOrigin, MonitorDefaultNearest) == monitor;
                }
                gestureDiagnostics.Verified(verified);
                if (!IsCurrentForeground(expected.Window)) {
                    gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                    LastStatus = "Foreground changed; direct move cancelled"; return;
                }
                bool directMoved = verified && MoveToMonitor(target.Window, monitor);
                bool inputReplayed = false;
                if (DirectReplayAllowed(directMoved, movementInterrupted, disposed, verified, movementMayHaveChanged)) {
                    Point? observedCursor = replayObservation.Cursor();
                    if (observedCursor.HasValue && !ModifiedClick() && !replayObservation.LeftButtonDown() &&
                        !TaskbarInput.IsDrag(directEnd, observedCursor.Value, SystemInformation.DragSize) &&
                        replayObservation.RootAtPoint(observedCursor.Value) == expected.Taskbar)
                        inputReplayed = replayInput(directOrigin, directEnd, true);
                }
                gestureDiagnostics.Complete(TaskbarGestureRoute.Direct, directMoved, inputReplayed);
                SetMoveStatus(directMoved, "Moved window directly", verificationTimedOut ?
                    "Taskbar verification timed out; move skipped" : "Icon changed; kept normal Windows behavior");
                return;
            }
            Click candidate = click;
            if (candidate == null) { timer.Stop(); return; }
            if (DateTime.UtcNow - candidate.Time > TimeSpan.FromSeconds(8)) {
                if (!candidate.PreviewOnly) gestureDiagnostics.Complete(TaskbarGestureRoute.TimedOut, false, false);
                LastStatus = "Timed out waiting for the app"; StopProcessing(); return;
            }
            // Explorer applies its taskbar action on release. Moving on mouse-down can
            // otherwise be followed by Explorer minimizing the window we just moved.
            if (!ClickHasSettled(candidate.ReleasedAt, DateTime.UtcNow)) return;
            if (!candidate.Inspected) {
                if (candidate.Verification == null || !candidate.Verification.Complete) {
                    if (DateTime.UtcNow - candidate.Time > TimeSpan.FromSeconds(1)) {
                        if (!candidate.PreviewOnly) gestureDiagnostics.Complete(TaskbarGestureRoute.TimedOut, false, false);
                        StopProcessing(); LastStatus = "Used normal Windows taskbar behavior";
                    }
                    return;
                }
                var resolved = candidate.Verification.Result;
                if (!candidate.PreviewOnly) ObserveGestureProbe(candidate.Verification);
                if (resolved == null || (candidate.Origin != null && !TaskbarTargets.SameButton(candidate.Origin, resolved))) {
                    string reason = resolved == null ? TaskbarTargets.FailureDescription(candidate.Verification) :
                        TaskbarTargets.SameButtonDifference(candidate.Origin, resolved);
                    if (!candidate.PreviewOnly) gestureDiagnostics.Complete(resolved == null ? TaskbarGestureRoute.ProbeMiss : TaskbarGestureRoute.ChangedIcon, false, false);
                    StopProcessing(); LastStatus = "Ignored a changed taskbar icon" + (AppProfile.IsTest ? " (" + reason + ")" : ""); return;
                }
                candidate.Inspected = true;
                candidate.AppId = resolved.Button.AppId; candidate.AppName = resolved.Button.AppName;
                candidate.Grouped = resolved.Button.Grouped;
                candidate.SelectedWindow = candidate.Verification.SelectedWindow;
                candidate.SelectedProcess = candidate.Verification.Selection == null ? 0 : candidate.Verification.Selection.Process;
                LastStatus = "Waiting for " + candidate.AppId;
            }
            if (candidate.PreviewOnly) {
                if (candidate.SelectedWindow == IntPtr.Zero && candidate.NativeSelection != null) {
                    IntPtr selected = candidate.NativeSelection.WindowAt(unchecked((uint)Environment.TickCount));
                    if (selected != IntPtr.Zero) {
                        bool verified = GetAncestor(GetForegroundWindow(), GaRoot) == selected &&
                            TaskbarTargets.NativePreviewMatches(candidate.Verification, selected) && MatchesApp(candidate, selected);
                        bool nativeMove = verified && MoveToMonitor(selected, candidate.TargetMonitor);
                        StopProcessing();
                        SetMoveStatus(nativeMove, "Moved the window selected by Windows", "Selection changed; kept normal Windows behavior");
                        return;
                    }
                }
                if (activatedWindow != IntPtr.Zero && activatedAt >= candidate.Time) {
                    if (IsTaskbarPreviewWindow(activatedWindow)) {
                        if (DateTime.UtcNow - candidate.ReleasedAt < TimeSpan.FromSeconds(1)) {
                            LastStatus = "Waiting for the previewed app to activate";
                            return;
                        }
                    } else if (PreviewMatches(candidate, activatedWindow)) {
                        if (!IsCurrentForeground(activatedWindow)) {
                            StopProcessing(); LastStatus = "Selection changed; kept normal Windows behavior"; return;
                        }
                        bool previewMove = MoveToMonitor(activatedWindow, candidate.TargetMonitor);
                        StopProcessing();
                        SetMoveStatus(previewMove, "Moved the selected preview window", "The selected preview window could not be moved");
                        return;
                    }
                }
                if (PreviewMatches(candidate, candidate.PriorWindow) && DateTime.UtcNow - candidate.ReleasedAt > TimeSpan.FromMilliseconds(250) &&
                    GetAncestor(GetForegroundWindow(), GaRoot) == candidate.PriorWindow) {
                    bool currentMove = MoveToMonitor(candidate.PriorWindow, candidate.TargetMonitor);
                    StopProcessing();
                    SetMoveStatus(currentMove, "Moved the selected preview window", "The selected preview window could not be moved");
                    return;
                }
                if (DateTime.UtcNow - candidate.ReleasedAt > TimeSpan.FromSeconds(1)) {
                    StopProcessing(); LastStatus = "Preview could not be verified; kept normal Windows behavior";
                }
                return;
            }
            var fallback = candidate.Verification == null ? null : candidate.Verification.Result;
            gestureDiagnostics.Waiting(TaskbarGestureRoute.NativePending);
            bool nativeSystemSelection = fallback != null && fallback.NeedsNativeSelection;
            if (nativeSystemSelection) {
                if (ModifiedClick()) {
                    gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                    StopProcessing(); LastStatus = "Modified taskbar click; kept normal Windows behavior"; return;
                }
                if (!TaskbarNativeSelection.CanAwaitSelection(candidate, fallback,
                    unchecked((uint)Environment.TickCount), DateTime.UtcNow) ||
                    !NativeSystemSelectionMatches(candidate, fallback, fallback.Window) ||
                    candidate.TargetMonitor == IntPtr.Zero ||
                    MonitorFromPoint(candidate.Position, MonitorDefaultNearest) != candidate.TargetMonitor) {
                    gestureDiagnostics.Complete(TaskbarGestureRoute.ProbeMiss, false, false);
                    StopProcessing(); LastStatus = "Native taskbar selection could not be verified; kept normal Windows behavior"; return;
                }
            }
            // Clicking the foreground app normally minimizes it without producing a useful
            // foreground event. Preserve that behavior on its current monitor; restore and
            // move only the exact verified window that was foreground at mouse-down.
            if (NativeMinimizeMatches(candidate, fallback)) {
                if (IsExcluded(candidate.PriorWindow)) {
                    gestureDiagnostics.Complete(TaskbarGestureRoute.Excluded, false, false);
                    StopProcessing(); LastStatus = "App excluded from window movement"; return;
                }
                if (candidate.PriorMonitor == candidate.TargetMonitor) {
                    gestureDiagnostics.Complete(TaskbarGestureRoute.NormalMinimize, false, false);
                    StopProcessing();
                    LastStatus = "Used normal Windows minimize behavior";
                    return;
                }
                if (nativeSystemSelection) {
                    // IsIconic can become true before Explorer has installed the
                    // replacement foreground window. Starting the elevated request
                    // in that interval would correctly fail MovementCurrent when
                    // Explorer's own handoff arrives during authorization.
                    if (candidate.NativeHandoff == null) candidate.NativeHandoff = new NativeTaskbarHandoff(candidate.NativeDownTime);
                    IntPtr foreground = GetAncestor(foregroundWindow(), GaRoot);
                    if (!candidate.NativeHandoff.Observe(true, fallback.Window, foreground, IsTaskbar(foreground),
                        false, unchecked((uint)Environment.TickCount))) {
                        LastStatus = "Waiting for Windows taskbar focus to settle"; return;
                    }
                    if (ModifiedClick()) {
                        gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                        StopProcessing(); LastStatus = "Modified taskbar click; kept normal Windows behavior"; return;
                    }
                    bool stillSelected = NativeMinimizeMatches(candidate, fallback) &&
                        MonitorFromPoint(candidate.Position, MonitorDefaultNearest) == candidate.TargetMonitor;
                    foreground = GetAncestor(foregroundWindow(), GaRoot);
                    if (ModifiedClick()) {
                        gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                        StopProcessing(); LastStatus = "Modified taskbar click; kept normal Windows behavior"; return;
                    }
                    if (!candidate.NativeHandoff.TryConsume(stillSelected, fallback.Window, foreground, IsTaskbar(foreground),
                        false, unchecked((uint)Environment.TickCount))) {
                        LastStatus = "Waiting for Windows taskbar focus to settle"; return;
                    }
                }
                bool restoredMove = MoveToMonitor(candidate.PriorWindow, candidate.TargetMonitor);
                gestureDiagnostics.Complete(TaskbarGestureRoute.NativeMinimize, restoredMove, false);
                StopProcessing();
                SetMoveStatus(restoredMove, "Moved the restored window", "The restored window could not be moved");
                return;
            }
            if (candidate.NativeHandoff != null)
                candidate.NativeHandoff.Observe(false, fallback == null ? IntPtr.Zero : fallback.Window,
                    IntPtr.Zero, false, false, unchecked((uint)Environment.TickCount));
            if (candidate.SelectionStarted && candidate.PriorWindow != IntPtr.Zero &&
                DateTime.UtcNow - candidate.SelectionTime > TimeSpan.FromMilliseconds(250) &&
                GetAncestor(GetForegroundWindow(), GaRoot) == candidate.PriorWindow && MatchesApp(candidate, candidate.PriorWindow)) {
                bool selectedMove = MoveToMonitor(candidate.PriorWindow, candidate.TargetMonitor);
                StopProcessing();
                SetMoveStatus(selectedMove, "Moved the selected preview window", "The selected preview window could not be moved");
                return;
            }
            if (FreshForegroundClickMatches(candidate, fallback, DateTime.UtcNow)) {
                bool freshMoved = MoveToMonitor(fallback.Window, candidate.TargetMonitor);
                gestureDiagnostics.Complete(TaskbarGestureRoute.FreshForeground, freshMoved, false);
                StopProcessing();
                SetMoveStatus(freshMoved, "Moved the verified foreground window", "The verified foreground window could not be moved");
                return;
            }
            if (activatedWindow == IntPtr.Zero || activatedAt < candidate.Time) return;
            if (nativeSystemSelection && !TaskbarNativeSelection.IsFreshEvent(candidate.NativeDownTime,
                activatedNativeTime, unchecked((uint)Environment.TickCount))) {
                LastStatus = "Waiting for Windows taskbar selection"; return;
            }
            if (!(fallback != null && fallback.NeedsNativeSelection ? NativeSystemActivationMatches(candidate, fallback, activatedWindow) : MatchesApp(candidate, activatedWindow))) {
                if (nativeSystemSelection) {
                    // The replacement foreground event can arrive before the
                    // target's iconic transition. Keep observing, without moving,
                    // until a native outcome is proved or the original gesture expires.
                    LastStatus = "Waiting for Windows taskbar selection"; return;
                }
                if (candidate.PreviewTransition && IsTaskbarPreviewWindow(activatedWindow) &&
                    DateTime.UtcNow - candidate.SelectionTime < TimeSpan.FromSeconds(1)) {
                    LastStatus = "Waiting for the previewed app to activate";
                    return;
                }
                if (candidate.Grouped && !candidate.SelectionStarted) {
                    LastStatus = "Choose a window from the taskbar preview";
                    return;
                }
                LastStatus = "Ignored an unrelated window activation" + (AppProfile.IsTest ? " (" + TaskbarTargets.FailureDescription(candidate.Verification) + ")" : "");
                gestureDiagnostics.Complete(TaskbarGestureRoute.UnrelatedActivation, false, false);
                StopProcessing();
                return;
            }
            if (!IsCurrentForeground(activatedWindow)) {
                gestureDiagnostics.Complete(TaskbarGestureRoute.Cancelled, false, false);
                StopProcessing(); LastStatus = "Activation changed; kept normal Windows behavior"; return;
            }
            bool moved = MoveToMonitor(activatedWindow, candidate.TargetMonitor);
            gestureDiagnostics.Complete(TaskbarGestureRoute.NativeActivation, moved, false);
            StopProcessing();
            SetMoveStatus(moved, "Moved the activated window", "The activated window could not be moved");
        }

        public void Dispose()
        {
            if (disposed) return;
            CancelPending(); disposed = true; movement.Dispose();
            fullscreenObservation.Dispose();
            ShelfMinimizes.Dispose();
            targets.Dispose();
            hoverTimer.Stop(); hoverTimer.Dispose();
            timer.Stop(); timer.Dispose();
            DisposeMouseHookService();
            if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
            if (shelfRestoreHook != IntPtr.Zero) UnhookWinEvent(shelfRestoreHook);
        }

        internal static bool PreviewMatches(Click candidate, IntPtr window)
        {
            uint process;
            return candidate.SelectedWindow != IntPtr.Zero && candidate.SelectedWindow == window && IsWindow(window) &&
                IsWindowVisible(window) && Native.GetWindowThreadProcessId(window, out process) != 0 &&
                process == candidate.SelectedProcess && MatchesApp(candidate, window);
        }
    }
}
