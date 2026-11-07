using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal static class OwnedSnapRuntimePolicy
    {
        internal static bool Allowed(bool desktop, bool wanted, bool active, bool paused, bool recovering, bool exiting, bool discovering)
        { return desktop && wanted && active && !paused && !recovering && !exiting && !discovering; }
        internal static bool CurrentNotice(long sent, long current, bool allowed) { return allowed && sent == current; }
        internal static bool OrdinaryHint(OwnedSnapRoute route) { return route == OwnedSnapRoute.Ordinary; }
        internal static bool SameRoute(OwnedSnapRoute expected, int expectedGeneration, OwnedSnapRoute current, int currentGeneration)
        { return expected == current && (expected == OwnedSnapRoute.Ordinary ? expectedGeneration == 0 && currentGeneration == 0 :
            expected == OwnedSnapRoute.Administrator && expectedGeneration > 0 && expectedGeneration == currentGeneration); }
        internal static bool AdministratorNotice(long sentEpoch, long currentEpoch, int sentGeneration, int currentGeneration,
            long created, long now, long frequency, long id, long lastId, bool allowed)
        {
            return CurrentNotice(sentEpoch, currentEpoch, allowed) && sentGeneration > 0 && sentGeneration == currentGeneration &&
                frequency > 0 && created > 0 && now >= created && now - created <= frequency && id > 0 && id > lastId;
        }
        internal static bool NoticeTarget(IntPtr window, uint process, long started, ShelfWindow current)
        { return current != null && window != IntPtr.Zero && process != 0 && started > 0 && current.Handle == window &&
            current.Process == process && current.ProcessStarted == started; }
    }
    internal sealed partial class Controller
    {
        sealed class OwnedSnapInvocation
        {
            internal ShelfWindow Window;
            internal Native.Rect Outer;
            internal Rectangle Visible;
            internal Native.MonitorInfo Source;
            internal List<OwnedSnapDisplay> Displays;
            internal Dictionary<string, Native.MonitorInfo> Monitors;
            internal string Fingerprint;
            internal long Epoch;
            internal OwnedSnapRoute Route;
            internal int SessionGeneration;
        }
        [DllImport("user32.dll", EntryPoint = "IsZoomed")] static extern bool OwnedSnapZoomed(IntPtr window);
        OwnedSnapKeyboard ownedSnapKeyboard;
        OwnedSnapCycle ownedSnapCycle;
        ShelfWindow ownedSnapCycleWindow;
        string ownedSnapCycleTopology;
        OwnedSnapRoute ownedSnapCycleRoute;
        int ownedSnapCycleSessionGeneration;
        bool ownedSnapWanted, ownedSnapDispatch;
        long ownedSnapEpoch;
        int ownedSnapAdministratorGeneration;
        long ownedSnapAdministratorNoticeId;
        internal bool OwnedSnapPrototypeEnabled { get { return ownedSnapWanted; } }
        internal string OwnedSnapPrototypeStatus = "Off — uses normal Windows snapping";
        bool OwnedSnapAllowed
        {
            get { return OwnedSnapRuntimePolicy.Allowed(DesktopRuntime, ownedSnapWanted, !disposed && EffectiveEnabled,
                MovementPaused, LayoutRecoveryPending, exitPreparing, RefreshDisplayIdentity); }
        }
        internal void SetOwnedSnapPrototype(bool enabled)
        {
            if (!DesktopRuntime) return;
            StopOwnedSnapInput(); ownedSnapWanted = enabled;
            if (enabled) {
                if (ownedSnapKeyboard == null) ownedSnapKeyboard = new OwnedSnapKeyboard(ApplyOwnedSnapNotice);
                ownedSnapKeyboard.AdvanceEpoch(ownedSnapEpoch);
                if (!ownedSnapKeyboard.Enable()) { ownedSnapWanted = false; OwnedSnapPrototypeStatus = ownedSnapKeyboard.Error; }
                else OwnedSnapPrototypeStatus = "On — Windows+Left/Right; Undo restores the last step";
            } else OwnedSnapPrototypeStatus = "Off — positions stay as they are; normal Windows snapping resumes";
            Revision++;
        }
        void InvalidateOwnedSnapCycle(bool clearTargets = false)
        {
            ownedSnapEpoch++; ownedSnapCycle = null; ownedSnapCycleWindow = null; ownedSnapCycleTopology = null;
            if (ownedSnapKeyboard != null) {
                ownedSnapKeyboard.AdvanceEpoch(ownedSnapEpoch);
                if (clearTargets) ownedSnapKeyboard.ClearTargets();
            }
            if (Administrator != null) Administrator.UpdateSnapInput(false, ownedSnapEpoch, null, IntPtr.Zero);
        }
        void StopOwnedSnapInput()
        {
            InvalidateOwnedSnapCycle(true);
            if (ownedSnapKeyboard != null) ownedSnapKeyboard.Disable();
        }
        int OwnedSnapAdministratorGeneration { get { return Administrator == null ? 0 : Administrator.SnapInputGeneration; } }
        void ObserveOwnedSnapAdministratorSession()
        {
            int current = OwnedSnapAdministratorGeneration;
            if (current == ownedSnapAdministratorGeneration) return;
            ownedSnapAdministratorGeneration = current; ownedSnapAdministratorNoticeId = 0;
            InvalidateOwnedSnapCycle(true);
        }
        void ObserveOwnedSnapForeground(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            // Focus changes retire the cycle, not a freshly queued request for
            // that new foreground. Delivery rechecks its exact live identity.
            if (ownedSnapCycleWindow != null && window != ownedSnapCycleWindow.Handle) {
                ownedSnapCycle = null; ownedSnapCycleWindow = null; ownedSnapCycleTopology = null;
            }
            // WinEvent delivery is outside the keyboard callback and avoids
            // waiting for the periodic foreground metadata refresh.
            PollOwnedSnapPrototype();
        }
        static Rectangle OwnedRectangle(Native.Rect box)
        { return Rectangle.FromLTRB(box.Left, box.Top, box.Right, box.Bottom); }
        static string OwnedTopologyKey(IEnumerable<OwnedSnapDisplay> displays)
        {
            return String.Join("|", displays.OrderBy(d => d.Device, StringComparer.Ordinal).Select(d =>
                d.Device + ":" + d.Identity + ":" + d.Bounds.ToString() + ":" + d.Work.ToString() + ":" + d.Hidden));
        }
        bool ReadOwnedSnapDisplays(out List<OwnedSnapDisplay> displays, out Dictionary<string, Native.MonitorInfo> monitors)
        {
            displays = new List<OwnedSnapDisplay>(); monitors = new Dictionary<string, Native.MonitorInfo>(StringComparer.OrdinalIgnoreCase);
            var bars = Native.Bars();
            foreach (var display in Displays.ToArray()) {
                Native.MonitorInfo info;
                if (String.IsNullOrWhiteSpace(display.Identity) || monitors.ContainsKey(display.Device) || !Native.TryMonitor(display.Device, out info) ||
                    !AreaPolicy.Equal(info.Monitor, new Native.Rect { Left = display.X, Top = display.Y, Right = checked(display.X + display.Width), Bottom = checked(display.Y + display.Height) })) return false;
                var associated = bars.Where(b => String.Equals(b.Device, display.Device, StringComparison.OrdinalIgnoreCase)).ToArray();
                bool hidden = associated.Length == 1 && associated[0].Managed && !associated[0].Visible && Policy.Hide(associated[0], Displays, Settings) &&
                    Native.GetProp(new IntPtr(associated[0].Handle), Native.Marker) == new IntPtr(1);
                // Do not cover a visible native taskbar while its reservation is
                // changing. The ordinary refresh/recovery path owns that state.
                if (!hidden && associated.Any(b => b.Visible) && AreaPolicy.Equal(info.Work, info.Monitor)) return false;
                displays.Add(new OwnedSnapDisplay { Device = display.Device, Identity = display.Identity,
                    Bounds = OwnedRectangle(info.Monitor), Work = OwnedRectangle(info.Work), Hidden = hidden });
                monitors.Add(display.Device, info);
            }
            return displays.Count > 0 && displays.Select(d => d.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count() == displays.Count;
        }
        bool OwnedSnapTopologyCurrent(OwnedSnapInvocation invocation)
        {
            if (!OwnedSnapAllowed || invocation.Epoch != ownedSnapEpoch || !OwnedSnapPermissionCurrent(invocation)) return false;
            List<OwnedSnapDisplay> displays; Dictionary<string, Native.MonitorInfo> monitors;
            return ReadOwnedSnapDisplays(out displays, out monitors) && invocation.Fingerprint == OwnedTopologyKey(displays);
        }
        bool OwnedSnapPermissionCurrent(OwnedSnapInvocation invocation)
        {
            if (invocation == null || invocation.Window == null) return false;
            var identity = NativeProcessIdentity.Read(invocation.Window.Process);
            return identity.Process == invocation.Window.Process && identity.Started == invocation.Window.ProcessStarted &&
                OwnedSnapMovementGeometry.Authorized(NativeProcessIdentity.Current, identity, invocation.Route, invocation.SessionGeneration,
                    OwnedSnapAdministratorGeneration, Administrator != null && Administrator.HasUncertainOperation, WindowMoveWorker.UncertainHelpers);
        }
        OwnedSnapInvocation CaptureOwnedSnapTarget(OwnedSnapRoute route)
        {
            if (!OwnedSnapAllowed || tilingPicker != null || Mover.MovementBusy) return null;
            IntPtr foreground = TaskbarMover.ForegroundWindow();
            int sessionGeneration = route == OwnedSnapRoute.Administrator ? OwnedSnapAdministratorGeneration : 0;
            ShelfWindow window; Native.Rect outer, frame;
            if (foreground == IntPtr.Zero || IsTransientWindow(foreground) || WindowTilingNative.IsIconic(foreground) || OwnedSnapZoomed(foreground) ||
                !WindowTilingNative.Resizable(foreground) || !WindowShelfCatalog.TryReadWindow(foreground, false, out window) ||
                AppCatalog.Matches(Settings.ExcludedApps, window.AppKey) ||
                !OwnedSnapMovementGeometry.Authorized(NativeProcessIdentity.Current, NativeProcessIdentity.Read(window.Process), route, sessionGeneration,
                    OwnedSnapAdministratorGeneration, Administrator != null && Administrator.HasUncertainOperation, WindowMoveWorker.UncertainHelpers) ||
                !Native.GetWindowRect(foreground, out outer) || Native.DwmGetWindowAttribute(foreground, 9, out frame, Marshal.SizeOf(typeof(Native.Rect))) != 0 ||
                !TaskbarMover.ValidVisibleFrame(outer, frame)) return null;
            bool fullscreen; Native.Rect fullscreenBounds;
            if (!WindowShelfCatalog.TryReadFullscreenMovement(foreground, out fullscreen, out fullscreenBounds) || fullscreen) return null;
            var source = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            List<OwnedSnapDisplay> displays; Dictionary<string, Native.MonitorInfo> monitors;
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(foreground, 2), ref source) || !ReadOwnedSnapDisplays(out displays, out monitors) ||
                !monitors.ContainsKey(source.Device) || !TaskbarMover.ReturnPlacement.SameMonitor(source, monitors[source.Device]) ||
                TaskbarMover.ForegroundWindow() != foreground) return null;
            var invocation = new OwnedSnapInvocation { Window = window, Outer = outer, Visible = OwnedRectangle(frame), Source = source,
                Displays = displays, Monitors = monitors, Fingerprint = OwnedTopologyKey(displays), Epoch = ownedSnapEpoch,
                Route = route, SessionGeneration = sessionGeneration };
            return OwnedSnapPermissionCurrent(invocation) ? invocation : null;
        }
        internal void PollOwnedSnapPrototype()
        {
            if (!OwnedSnapPrototypeEnabled || ownedSnapDispatch) return;
            ObserveOwnedSnapAdministratorSession();
            if (!OwnedSnapAllowed) {
                if (Administrator != null) Administrator.UpdateSnapInput(false, ownedSnapEpoch, null, IntPtr.Zero);
                if (ownedSnapKeyboard != null && ownedSnapKeyboard.Enabled) StopOwnedSnapInput();
                OwnedSnapPrototypeStatus = "Paused — normal Windows snapping is active"; return;
            }
            try {
                if (ownedSnapKeyboard == null) return;
                if (!ownedSnapKeyboard.Enabled && !ownedSnapKeyboard.Enable()) {
                    if (Administrator != null) Administrator.UpdateSnapInput(false, ownedSnapEpoch, null, IntPtr.Zero);
                    OwnedSnapPrototypeStatus = ownedSnapKeyboard.Error; return;
                }
                // The ordinary hook must never receive a HIGH routing hint,
                // including while the independent approved helper is active.
                var invocation = CaptureOwnedSnapTarget(OwnedSnapRoute.Ordinary);
                bool ordinary = invocation != null && OwnedSnapRuntimePolicy.OrdinaryHint(invocation.Route);
                ownedSnapKeyboard.Publish(!ordinary ? null : new OwnedSnapKeyboard.Target {
                    Window = invocation.Window.Handle, Published = Stopwatch.GetTimestamp(), State = invocation },
                    !ordinary ? TaskbarMover.ForegroundWindow() : IntPtr.Zero);
                var high = ordinary ? null : CaptureOwnedSnapTarget(OwnedSnapRoute.Administrator);
                if (Administrator != null) {
                    Administrator.UpdateSnapInput(OwnedSnapAllowed, ownedSnapEpoch, high == null ? null : high.Window,
                        high == null ? TaskbarMover.ForegroundWindow() : IntPtr.Zero);
                    ApplyAdministratorOwnedSnapNotice(Administrator.TakeSnapInput());
                }
                if (invocation == null && high == null) OwnedSnapPrototypeStatus = "On — restore a supported resizable window; unavailable windows use Windows snapping";
                else if (OwnedSnapPrototypeStatus.StartsWith("Paused", StringComparison.Ordinal) ||
                    OwnedSnapPrototypeStatus.StartsWith("On — restore", StringComparison.Ordinal))
                    OwnedSnapPrototypeStatus = "On — Windows+Left/Right; Undo restores the last step";
            } catch (Exception) {
                StopOwnedSnapInput(); ownedSnapWanted = false;
                OwnedSnapPrototypeStatus = "Experimental snapping stopped; normal Windows snapping is available";
            }
        }
        void ApplyOwnedSnapNotice(OwnedSnapKeyboard.Notice notice)
        {
            if (notice == null || notice.Target == null) return;
            var accepted = notice.Target.State as OwnedSnapInvocation;
            if (accepted == null || !OwnedSnapRuntimePolicy.OrdinaryHint(accepted.Route)) return;
            ApplyOwnedSnapInvocation(accepted, notice.Left, notice.Epoch);
        }
        void ApplyAdministratorOwnedSnapNotice(AdministratorSupport.SnapInputNotice notice)
        {
            if (notice == null || !OwnedSnapRuntimePolicy.AdministratorNotice(notice.Epoch, ownedSnapEpoch,
                notice.SessionGeneration, OwnedSnapAdministratorGeneration, notice.Created, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                notice.Id, ownedSnapAdministratorNoticeId, OwnedSnapAllowed)) return;
            ownedSnapAdministratorNoticeId = notice.Id;
            var current = CaptureOwnedSnapTarget(OwnedSnapRoute.Administrator);
            if (current == null || current.Epoch != notice.Epoch || current.SessionGeneration != notice.SessionGeneration ||
                !OwnedSnapRuntimePolicy.NoticeTarget(notice.Window, notice.Process, notice.Started, current.Window)) return;
            ApplyOwnedSnapInvocation(current, notice.Left, notice.Epoch);
        }
        void ApplyOwnedSnapInvocation(OwnedSnapInvocation accepted, bool left, long epoch)
        {
            if (accepted == null || !OwnedSnapRuntimePolicy.CurrentNotice(epoch, ownedSnapEpoch, OwnedSnapAllowed)) return;
            try {
                var current = CaptureOwnedSnapTarget(accepted.Route);
                // Cached metadata routes the chord only. Use a fresh capture
                // of the still-foreground exact process/start identity for the
                // plan, bounds, topology, permission and native commit proofs.
                if (current == null || current.Epoch != epoch || !OwnedSnapRuntimePolicy.CurrentNotice(epoch, ownedSnapEpoch, OwnedSnapAllowed) ||
                    !WindowShelfPolicy.SameWindow(accepted.Window, current.Window) ||
                    !OwnedSnapRuntimePolicy.SameRoute(accepted.Route, accepted.SessionGeneration, current.Route, current.SessionGeneration) ||
                    (accepted.Route == OwnedSnapRoute.Administrator && accepted.Fingerprint != current.Fingerprint)) {
                    InvalidateOwnedSnapCycle(); OwnedSnapPrototypeStatus = "The window changed or is unsupported; no experimental snap applied"; return;
                }
                var display = current.Displays.Single(d => String.Equals(d.Device, current.Source.Device, StringComparison.OrdinalIgnoreCase));
                OwnedSnapCycle previous = ownedSnapCycleWindow != null && WindowShelfPolicy.SameWindow(ownedSnapCycleWindow, current.Window) &&
                    ownedSnapCycleTopology == current.Fingerprint && OwnedSnapRuntimePolicy.SameRoute(ownedSnapCycleRoute,
                        ownedSnapCycleSessionGeneration, current.Route, current.SessionGeneration) ? ownedSnapCycle : null;
                OwnedSnapPlan plan; string reason;
                if (!OwnedSnapPolicy.TryPlan(current.Displays, display.Identity, current.Visible, left, previous, out plan, out reason)) {
                    InvalidateOwnedSnapCycle(); OwnedSnapPrototypeStatus = reason; return;
                }
                ownedSnapDispatch = true; ownedSnapKeyboard.Publish(null);
                bool applied = Mover.SnapPrototype(current.Window, current.Outer, current.Source, current.Monitors[plan.Display.Device], plan.VisibleTarget,
                    current.Route, current.SessionGeneration, delegate { return OwnedSnapTopologyCurrent(current); });
                if (applied && OwnedSnapTopologyCurrent(current)) {
                    ownedSnapCycle = new OwnedSnapCycle { DisplayIdentity = plan.Display.Identity, RightHalf = plan.RightHalf, ExpectedVisible = plan.VisibleTarget };
                    ownedSnapCycleWindow = current.Window; ownedSnapCycleTopology = current.Fingerprint;
                    ownedSnapCycleRoute = current.Route; ownedSnapCycleSessionGeneration = current.SessionGeneration;
                } else InvalidateOwnedSnapCycle();
                OwnedSnapPrototypeStatus = Mover.LastStatus;
            } catch (Exception) { InvalidateOwnedSnapCycle(); OwnedSnapPrototypeStatus = "Experimental snap stopped; Undo remains available"; }
            finally { ownedSnapDispatch = false; Revision++; }
        }
        internal string OwnedSnapDiagnostic()
        { return "Experimental snapping: " + (OwnedSnapPrototypeEnabled ? "On" : "Off") + "; " + OwnedSnapPrototypeStatus; }
    }
}
