using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal sealed partial class TaskbarMover
    {
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        DesktopHookLifetime mouseHookLifetime;
        HookDeliveryWatch hookDelivery;
        Timer hookServiceTimer;
        uint hookOwnerThread, hookGeneration, hookRegisteredTick;
        long nativeMouseCallbacks, lastNativeMouseCallback, longestMouseDispatch, hookRegisteredAt;
        int mouseCallbackDepth, mouseCallbackWrongThread, mouseInstallError, mouseRetireError;
        bool hookServiceArmed, hookServiceChanging, sampledPointer;
        Point sampledPointerPosition;
        readonly List<MouseHook> registeredMouseCallbacks = new List<MouseHook>();
        // If final removal is unconfirmed, native code can still own its callback.
        // Keep that delegate (and its disposed/pass-through owner) rooted until
        // process exit instead of risking a callback into collected managed code.
        static readonly List<MouseHook> uncertainMouseCallbacks = new List<MouseHook>();
        internal event EventHandler HookAvailabilityChanged;

        // A low-level mouse hook is synchronous on its installing thread. Do not
        // install it while constructing the app, loading settings or discovering
        // monitors, before Application.Run has started that thread's message pump.
        void InitializeMouseHookService()
        {
            hookOwnerThread = GetCurrentThreadId();
            hookDelivery = new HookDeliveryWatch(Stopwatch.Frequency);
            mouseHookLifetime = new DesktopHookLifetime(InstallMouseHook, RetireMouseHook);
            hookServiceTimer = new Timer { Interval = 250 };
            hookServiceTimer.Tick += delegate { ServiceMouseHook(); };
        }
        internal void ArmDesktopHooks()
        {
            if (disposed || injectedInputOnly || hookServiceArmed || GetCurrentThreadId() != hookOwnerThread) return;
            hookServiceArmed = true;
            hookServiceTimer.Start();
        }
        bool InstallMouseHook()
        {
            if (disposed || mouseHook != IntPtr.Zero) return false;
            uint generation = ++hookGeneration;
            hookRegisteredTick = unchecked((uint)Environment.TickCount);
            hookRegisteredAt = Stopwatch.GetTimestamp();
            mouseCallback = delegate(int code, IntPtr message, IntPtr data) { return NativeMouseEvent(generation, code, message, data); };
            mouseHook = SetWindowsHookEx(WhMouseLowLevel, mouseCallback, GetModuleHandle(null), 0);
            mouseInstallError = mouseHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (mouseHook != IntPtr.Zero) registeredMouseCallbacks.Add(mouseCallback);
            return mouseHook != IntPtr.Zero;
        }
        bool RetireMouseHook()
        {
            if (mouseHook == IntPtr.Zero) return true;
            bool removed = UnhookWindowsHookEx(mouseHook);
            mouseRetireError = removed ? 0 : Marshal.GetLastWin32Error();
            // Windows may silently remove a timed-out low-level hook. Only these
            // explicit absent-hook errors establish that replacement cannot
            // duplicate our old registration; other errors retain ownership.
            if (!removed && mouseRetireError != 1404 && mouseRetireError != 1431) return false;
            mouseHook = IntPtr.Zero;
            ++hookGeneration;
            return true;
        }
        IntPtr NativeMouseEvent(uint generation, int code, IntPtr message, IntPtr data)
        {
            long began = Stopwatch.GetTimestamp();
            mouseCallbackDepth++;
            try {
                if (code < 0 || disposed || generation != hookGeneration || hookServiceChanging)
                    return CallNextHookEx(IntPtr.Zero, code, message, data);
                nativeMouseCallbacks++;
                lastNativeMouseCallback = began;
                if (GetCurrentThreadId() != hookOwnerThread) {
                    mouseCallbackWrongThread = Math.Min(9999, mouseCallbackWrongThread + 1);
                    return CallNextHookEx(IntPtr.Zero, code, message, data);
                }
                MouseData input = (MouseData)Marshal.PtrToStructure(data, typeof(MouseData));
                // Do not let a packet queued before this registration create a
                // gesture or complete one owned by a different hook generation.
                if (!MouseHookPacketPolicy.Current(generation, hookGeneration, input.Time, hookRegisteredTick,
                    unchecked((uint)Environment.TickCount), began - hookRegisteredAt <= Stopwatch.Frequency * 2) ||
                    (mouseHookLifetime != null && mouseHookLifetime.RetirementUncertain))
                    return CallNextHookEx(IntPtr.Zero, code, message, data);
                return MouseEvent(code, message, data);
            }
            finally {
                mouseCallbackDepth--;
                longestMouseDispatch = Math.Max(longestMouseDispatch, Stopwatch.GetTimestamp() - began);
            }
        }
        bool MouseHookOwnedWorkIdle()
        {
            return !disposed && mouseCallbackDepth == 0 && !hookServiceChanging && !movement.Busy && !pendingUndo &&
                click == null && directPress == null && directRelease == null && directCheck == null && deferredClick == null;
        }
        bool MouseHookSafeBoundary()
        {
            // Read the potentially slower desktop guard before the final button
            // states: a silent hook cannot report a press during that probe.
            if (!MouseHookOwnedWorkIdle() || !FocusIndicatorNative.DesktopAllowed()) return false;
            foreach (int key in new[] { 1, 2, 4, 5, 6 }) if ((GetAsyncKeyState(key) & 0x8000) != 0) return false;
            return MouseHookOwnedWorkIdle();
        }
        void ServiceMouseHook()
        {
            if (disposed || !hookServiceArmed || !Application.MessageLoop || GetCurrentThreadId() != hookOwnerThread) return;
            long now = Stopwatch.GetTimestamp();
            bool before = Available;
            if (!mouseHookLifetime.RegistrationClaimed) {
                // Bounded startup retry, with no synthetic probe and no desktop
                // changes. Pause/disabled movement still needs pointer observers.
                if (MouseHookSafeBoundary()) {
                    hookServiceChanging = true;
                    try {
                        if (mouseHookLifetime.Start(true, true)) ResetMouseHookObservation(Stopwatch.GetTimestamp());
                    } finally { hookServiceChanging = false; }
                }
            } else {
                Point point;
                if (FocusIndicatorNative.DesktopAllowed() && GetCursorPos(out point)) {
                    bool changed = sampledPointer && (point.X != sampledPointerPosition.X || point.Y != sampledPointerPosition.Y);
                    sampledPointer = true; sampledPointerPosition = point;
                    long observedCallbacks = nativeMouseCallbacks;
                    hookDelivery.Observe(now, observedCallbacks, changed);
                    // Absence of callbacks alone says nothing while idle. Require
                    // sustained pointer movement without any real callback, then
                    // recheck input/gesture ownership immediately before retiring.
                    if (hookDelivery.ShouldRecover(now) && MouseHookSafeBoundary() &&
                        observedCallbacks == nativeMouseCallbacks && hookDelivery.AttemptedRecovery(Stopwatch.GetTimestamp())) {
                        hookServiceChanging = true;
                        try {
                            if (mouseHookLifetime.TryRearm(true, true, true)) ResetMouseHookObservation(Stopwatch.GetTimestamp());
                        } finally { hookServiceChanging = false; }
                    }
                } else {
                    sampledPointer = false;
                    hookDelivery.Observe(now, nativeMouseCallbacks, false);
                }
            }
            if (before != Available) {
                var changed = HookAvailabilityChanged;
                if (changed != null) changed(this, EventArgs.Empty);
            }
        }
        void ResetMouseHookObservation(long now)
        {
            sampledPointer = false;
            lastHoverTarget = null; lastHoverAt = DateTime.MinValue;
            hookDelivery.ResetAfterInstall(now, nativeMouseCallbacks);
        }
        void DisposeMouseHookService()
        {
            hookServiceArmed = false;
            if (hookServiceTimer != null) { hookServiceTimer.Stop(); hookServiceTimer.Dispose(); }
            if (mouseHookLifetime != null) mouseHookLifetime.Dispose();
            if (mouseHookLifetime != null && mouseHookLifetime.RetirementUncertain)
                lock (uncertainMouseCallbacks) uncertainMouseCallbacks.AddRange(registeredMouseCallbacks);
        }
        internal string TaskbarHookLifetimeDiagnostic
        {
            get {
                if (injectedInputOnly) return "noDesktopHooks=1";
                long age = lastNativeMouseCallback == 0 ? -1 : Math.Min(99999, (Stopwatch.GetTimestamp() - lastNativeMouseCallback) * 1000 / Stopwatch.Frequency);
                return "armed=" + (hookServiceArmed ? "1" : "0") + " callbacks=" + nativeMouseCallbacks.ToString(CultureInfo.InvariantCulture) +
                    " callbackAge=" + age.ToString(CultureInfo.InvariantCulture) + " maxDispatchMs=" +
                    (longestMouseDispatch * 1000 / Stopwatch.Frequency).ToString(CultureInfo.InvariantCulture) +
                    " wrongThread=" + mouseCallbackWrongThread.ToString(CultureInfo.InvariantCulture) +
                    " installError=" + mouseInstallError.ToString(CultureInfo.InvariantCulture) +
                    " retireError=" + mouseRetireError.ToString(CultureInfo.InvariantCulture) + " " +
                    (mouseHookLifetime == null ? "uninitialized=1" : mouseHookLifetime.Format()) + " " +
                    (hookDelivery == null ? "watchUninitialized=1" : hookDelivery.Format(Stopwatch.GetTimestamp()));
            }
        }
    }
}
