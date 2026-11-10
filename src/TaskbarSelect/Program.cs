using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarSelect
{
    public sealed class Settings
    {
        public List<string> HiddenIdentities { get; set; }
        public List<string> WindowShelfIdentities { get; set; }
        public bool MoveWindowsToClickedTaskbar { get; set; }
        public bool ScaleWindowSizeToMonitor { get; set; }
        public bool AllowMovingFullscreenApps { get; set; }
        public bool PersistentAdministratorSupport { get; set; }
        public int[] MonitorMoveShortcuts { get; set; }
        public Dictionary<string, int> AdditionalMonitorMoveShortcuts { get; set; }
        public Dictionary<string, string> MonitorAliases { get; set; }
        public List<AppRule> ExcludedApps { get; set; }
        public int UndoMoveShortcut { get; set; }
        public int CloseWindowShortcut { get; set; }
        public int SearchAppsShortcut { get; set; }
        public int MinimizeWindowShortcut { get; set; }
        public int PinWindowShortcut { get; set; }
        public int[] FocusWindowShortcuts { get; set; }
        public int TileWindowShortcut { get; set; }
        int focusIndicatorStyle;
        string accentPreset = "Original";
        public int FocusIndicatorStyle
        {
            get { return focusIndicatorStyle; }
            set { focusIndicatorStyle = (int)FocusAppearance.NormalizeStyle(value); }
        }
        public bool FocusIndicatorGlow { get; set; }
        int focusIndicatorFinish;
        public int FocusIndicatorFinish
        {
            get { return focusIndicatorFinish; }
            set { focusIndicatorFinish = (int)FocusAppearance.NormalizeFinish(value); }
        }
        int focusIndicatorLighting = -1;
        public int FocusIndicatorLighting
        {
            get { return focusIndicatorLighting; }
            set { focusIndicatorLighting = FocusAppearance.NormalizeLighting(value); }
        }
        public string AccentPreset
        {
            get { return accentPreset; }
            set { accentPreset = FocusAppearance.NormalizeAccent(value); }
        }
        int shelfActivationWidth = WindowShelfPolicy.DefaultActivationWidth;
        // Deserialization starts with this constructor. Keep the released
        // fallback for profiles written before activation height was stored.
        // Fresh profiles use CreateFresh instead of inheriting legacy choices.
        int shelfActivationHeight = 24;
        int shelfHoverDelayMs = WindowShelfPolicy.DefaultHoverDelayMs;
        public int WindowShelfActivationWidth
        {
            get { return shelfActivationWidth; }
            set { shelfActivationWidth = WindowShelfPolicy.ClampActivationWidth(value); }
        }
        public int WindowShelfHoverDelayMs
        {
            get { return shelfHoverDelayMs; }
            set { shelfHoverDelayMs = WindowShelfPolicy.ClampHoverDelayMs(value); }
        }
        public int WindowShelfActivationHeight
        {
            get { return shelfActivationHeight; }
            set { shelfActivationHeight = WindowShelfPolicy.ClampActivationHeight(value); }
        }
        public Settings()
        {
            ScaleWindowSizeToMonitor = true;
            HiddenIdentities = new List<string>();
            WindowShelfIdentities = new List<string>();
            MonitorAliases = new Dictionary<string, string>();
            ExcludedApps = new List<AppRule>();
            UndoMoveShortcut = (int)GlobalShortcut.Default;
            MonitorMoveShortcuts = MonitorShortcutPolicy.Defaults();
            AdditionalMonitorMoveShortcuts = new Dictionary<string, int>();
            CloseWindowShortcut = (int)(Keys.Control | Keys.Shift | Keys.W);
            SearchAppsShortcut = (int)(Keys.Control | Keys.Shift | Keys.Space);
            MinimizeWindowShortcut = (int)(Keys.Control | Keys.Shift | Keys.M);
            PinWindowShortcut = (int)(Keys.Control | Keys.Shift | Keys.P);
            FocusWindowShortcuts = NavigationShortcutPolicy.Defaults();
            TileWindowShortcut = (int)NavigationShortcutPolicy.TileDefault;
        }
        internal static Settings CreateFresh()
        {
            return new Settings {
                WindowShelfActivationHeight = WindowShelfPolicy.DefaultActivationHeight,
                FocusIndicatorStyle = (int)FocusIndicatorMode.InsetEdge,
                FocusIndicatorFinish = (int)FocusFrameFinish.MutedEdge,
                FocusIndicatorGlow = false,
                FocusIndicatorLighting = (int)FocusLightingMode.None,
                AccentPreset = "Forest Green"
            };
        }
        internal Settings Copy()
        {
            return new JavaScriptSerializer().Deserialize<Settings>(new JavaScriptSerializer().Serialize(this));
        }
        internal string DisplayName(Display display)
        {
            string name;
            return !String.IsNullOrEmpty(display.Identity) && MonitorAliases.TryGetValue(display.Identity, out name) &&
                !String.IsNullOrWhiteSpace(name) ? name : display.Name;
        }
        internal bool HasWindowMovement
        {
            get { return MoveWindowsToClickedTaskbar || WindowShelfIdentities.Count > 0 || MonitorMoveShortcuts.Any(k => k != 0) ||
                (AdditionalMonitorMoveShortcuts != null && AdditionalMonitorMoveShortcuts.Values.Any(k => k != 0)) ||
                SearchAppsShortcut != 0 || TileWindowShortcut != 0 || (FocusWindowShortcuts != null && FocusWindowShortcuts.Any(k => k != 0)); }
        }
    }

    internal static class Policy
    {
        internal static bool Hide(Bar bar, List<Display> displays, Settings settings)
        {
            Display display = displays.Find(d => d.Device == bar.Device);
            return !bar.Primary && display != null && !display.Primary &&
                !String.IsNullOrEmpty(display.Identity) &&
                displays.Count(d => d.Identity == display.Identity) == 1 &&
                settings.HiddenIdentities.Contains(display.Identity);
        }
        internal static bool Shelf(Display display, List<Display> displays, Settings settings, bool enabled, bool paused)
        {
            return enabled && !paused && display != null && !display.Primary &&
                !String.IsNullOrEmpty(display.Identity) && displays.Count(d => d.Identity == display.Identity) == 1 &&
                settings.HiddenIdentities.Contains(display.Identity) && settings.WindowShelfIdentities.Contains(display.Identity);
        }
    }

    internal sealed partial class Controller : IDisposable
    {
        internal Settings Settings;
        bool enabled;
        internal bool Enabled
        {
            get { return enabled; }
            set { if (enabled && !value) { StopOwnedSnapInput(); CancelSnapInput(); Mover.CancelPending(); CancelTilingPicker(); HideFocusIndicator(); HidePinnedIndicators(); } enabled = value; }
        }
        internal volatile bool RefreshDisplayIdentity = true;
        internal List<Display> Displays = new List<Display>();
        internal List<Bar> Bars = new List<Bar>();
        internal readonly WorkAreas Areas = new WorkAreas();
        internal readonly TaskbarMover Mover;
        internal readonly bool DesktopRuntime;
        internal readonly FocusIndicator FocusFrame;
        internal readonly PinnedWindowIndicator PinRails;
        internal readonly AdministratorSupport Administrator;
        internal readonly AdministratorPermissionSwitch AdministratorPermissions;
        readonly DisplayWakeObserver displayWakeObserver;
        readonly Action<Dictionary<string, long>, Dictionary<string, long>> wakeDiagnostics;
        internal string FocusIndicatorError;
        internal Controller() : this(true) { }
        internal Controller(bool installDesktopHooks)
        {
            DesktopRuntime = installDesktopHooks;
            Mover = installDesktopHooks ? new TaskbarMover() : new TaskbarMover(new TaskbarTargets(), TaskbarInput.Replay, TaskbarMover.ForegroundWindow, false);
            Mover.ControllerActionGate = delegate { return !disposed && !exitPreparing && !LayoutRecoveryPending; };
            Mover.ControllerActionStarting = delegate { CancelSnapInput(); if (!ownedSnapDispatch) InvalidateOwnedSnapCycle(); };
            Mover.ManualMoveStarted = delegate(IntPtr window) {
                if (ownedSnapCycleWindow != null && ownedSnapCycleWindow.Handle == window) InvalidateOwnedSnapCycle();
            };
            if (DesktopRuntime) {
                FocusFrame = new FocusIndicator(IsTransientWindow);
                Administrator = new AdministratorSupport();
                AdministratorPermissions = new AdministratorPermissionSwitch(new PermissionBackend(this));
                PinRails = new PinnedWindowIndicator(IsTransientWindow, IsPinnedFocusOverlay);
                WindowMoveWorker.Administrator = Administrator;
            }
            var focusedApi = new FocusedWindowApi();
            if (DesktopRuntime) focusedApi.PostClose = PostFocusedCloseWithAdministrator;
            FocusedActions = new FocusedWindowActions(focusedApi, IsTransientWindow);
            WindowStateActions = new FocusedWindowStateActions(delegate(IntPtr window) {
                return disposed || !EffectiveEnabled || IsTransientWindow(window) || Mover.IsExcluded(window);
            });
            FocusedMonitor = new LastFocusedMonitor(IsTransientWindow);
            FocusNavigation = new DirectionalFocus(Mover, IsTransientWindow);
            Mover.ForegroundChanged += delegate(IntPtr window) {
                try {
                    if (disposed) return;
                    HideFocusIndicator();
                    ObserveOwnedSnapForeground(window);
                    if (snapAreas != null) snapAreas.ObserveInputForeground(window);
                    FocusedMonitor.ObserveForeground(window, Displays);
                } catch (Exception) { }
            };
            Mover.PointerDown += delegate(Point point) {
                if (disposed) return;
                try { FocusedMonitor.ObservePointerDown(point, Displays); } catch (Exception) { }
                try { if (shelf != null) shelf.ObservePointerDown(point); } catch (Exception) { }
                try { if (tilingPicker != null) tilingPicker.ObservePointerDown(point); } catch (Exception) { }
            };
            if (DesktopRuntime) displayWakeObserver = new DisplayWakeObserver(delegate(long sequence) { BeginWakeEpoch(sequence); },
                AppProfile.IsTest ? DisplayWakeTestStatus.Writer(Path.Combine(AppProfile.Directory, "display-wake-status.json")) : null);
            if (DesktopRuntime && AppProfile.IsTest)
                wakeDiagnostics = WakeRecoveryDiagnostics.Writer(Path.Combine(AppProfile.Directory, "wake-recovery-status.json"));
        }
        sealed class PermissionBackend : AdministratorPermissionSwitch.IBackend
        {
            readonly Controller controller;
            AdministratorSupport Support { get { return controller.Administrator; } }
            internal PermissionBackend(Controller value) { controller = value; }
            public bool Active { get { return Support.Active; } }
            public bool Enabling { get { return Support.Enabling; } }
            public bool Uncertain { get { return Support.HasUncertainOperation; } }
            public bool SavedApproval { get { return Support.SavedApprovalAvailable; } }
            public bool Remembered { get { return controller.Settings != null && controller.Settings.PersistentAdministratorSupport; } }
            public string Error { get { return Support.LastError; } }
            public void Remember(bool value) { controller.SaveAdministratorPreference(value); }
            public void BlockOperations(bool value) { Support.PermissionChangePending = value; }
            public bool RequiresRemoval() { return PersistentAdministrator.RequiresApprovalRemoval(PersistentAdministrator.ScopeForCurrentProfile()); }
            public bool Stop() { return Support.StopForExit(5000); }
            public void EnableManual() { Support.Enable(); }
            public void EnableRemembered() { Support.EnablePersistent(); }
            public bool Install() { return Support.InstallPersistent(PersistentAdministrator.ScopeForCurrentProfile(), AppProfile.IsTest); }
            public bool Remove() { return Support.RemovePersistent(PersistentAdministrator.ScopeForCurrentProfile(), AppProfile.IsTest); }
        }
        internal void SaveAdministratorPreference(bool value)
        {
            if (Settings != null && SaveAdministratorPreferenceValue(Settings, value, Program.Save)) Revision++;
        }
        internal static bool SaveAdministratorPreferenceValue(Settings current, bool value, Action<Settings> save)
        {
            if (current == null || save == null) throw new ArgumentNullException();
            if (current.PersistentAdministratorSupport == value) return false;
            Settings next = current.Copy(); next.PersistentAdministratorSupport = value;
            // Saving a permission flag must not replace the exclusion-list
            // identity underneath an unrelated window operation.
            save(next); current.PersistentAdministratorSupport = value;
            return true;
        }
        internal static void PreserveAdministratorPreference(Settings next, Settings current)
        {
            if (next == null || current == null) throw new ArgumentNullException();
            next.PersistentAdministratorSupport = current.PersistentAdministratorSupport;
        }
        internal readonly GlobalShortcut UndoShortcut = new GlobalShortcut();
        internal string ShortcutError;
        internal readonly GlobalShortcut[] MonitorShortcuts = Enumerable.Range(0, 4).Select(i => new GlobalShortcut()).ToArray();
        internal readonly string[] MonitorShortcutErrors = new string[4];
        readonly Dictionary<int, GlobalShortcut> additionalMonitorBindings = new Dictionary<int, GlobalShortcut>();
        readonly Dictionary<int, string> additionalMonitorErrors = new Dictionary<int, string>();
        readonly HashSet<int> connectedMonitorBindings = new HashSet<int>();
        bool monitorShortcutsInitialized, applyingShortcutSettings;
        internal event Action<int> MonitorShortcutPressed;
        internal readonly GlobalShortcut[] ActionShortcuts = Enumerable.Range(0, ActionShortcutPolicy.Count).Select(i => new GlobalShortcut()).ToArray();
        internal readonly string[] ActionShortcutErrors = new string[ActionShortcutPolicy.Count];
        internal readonly GlobalShortcut[] NavigationShortcuts = Enumerable.Range(0, 5).Select(i => new GlobalShortcut()).ToArray();
        internal readonly string[] NavigationShortcutErrors = new string[5];
        internal readonly DirectionalFocus FocusNavigation;
        internal readonly FocusedWindowActions FocusedActions;
        internal readonly FocusedWindowStateActions WindowStateActions;
        internal readonly LastFocusedMonitor FocusedMonitor;
        FocusedClosePostResult PostFocusedCloseWithAdministrator(IntPtr window)
        {
            if (!EffectiveEnabled) return new FocusedClosePostResult { Accepted = false, Error = 0 };
            uint process;
            Native.GetWindowThreadProcessId(window, out process);
            if (!NativeProcessIdentity.RequiresAdministrator(process))
                return EffectiveEnabled ? FocusedWindowNative.PostClose(window) : new FocusedClosePostResult { Accepted = false, Error = 0 };
            bool supportAvailable = Administrator != null && Administrator.Active;
            long started = NativeProcessIdentity.Started(process);
            var result = WindowMoveWorker.Close(window, process, started,
                Stopwatch.GetTimestamp() + Stopwatch.Frequency, delegate {
                    uint current;
                    return !disposed && EffectiveEnabled && FocusedWindowNative.Foreground() == window &&
                        Native.IsWindow(window) && Native.GetWindowThreadProcessId(window, out current) != 0 && current == process &&
                        NativeProcessIdentity.Started(process) == started;
                });
            return new FocusedClosePostResult { Accepted = result.Outcome == WindowMoveWorker.Outcome.Success,
                Error = result.Outcome == WindowMoveWorker.Outcome.Success ? 0 : result.NativeError != 0 ? result.NativeError : supportAvailable ? 0 : 5 };
        }
        internal bool MovementPaused;
        internal readonly SettingsHistory History = new SettingsHistory();
        internal int Revision;
        DateTime nextDiscovery = DateTime.MinValue;
        readonly TaskbarHideRequests hideRequests = new TaskbarHideRequests();
        DateTime retryDisplayIdentityUntil = DateTime.MinValue;
        DateTime minimumDisplayDiscoveryUntil = DateTime.MinValue;
        DateTime nextDisplayIdentityRetry = DateTime.MinValue;
        bool disposed;
        WindowShelfManager shelf;
        ShelfActivationPreview activationPreview;
        bool previewActivation;
        internal string ActivationPreviewError;
        internal string TaskbarRecoveryError;
        SnapWorkAreas snapAreas;
        void CancelSnapInput()
        { try { if (snapAreas != null) snapAreas.CancelInputIntent(); } catch (Exception) { } }
        SnapWorkAreas CreateSnapAreas()
        {
            var created = new SnapWorkAreas();
            if (DesktopRuntime) created.EnableKeyboardIntent(delegate {
                return EffectiveEnabled && !layoutExitQuiesced && !MovementPaused && !Mover.MovementBusy;
            });
            return created;
        }
        internal void PollSnapIntent()
        { if (DesktopRuntime && snapAreas != null) try { snapAreas.PollKeyboardIntent(); } catch (Exception) { CancelSnapInput(); } }
        SnapLayoutRestorePolicy layoutRestore;
        internal bool LayoutRecoveryPending { get { return layoutRestore != null && layoutRestore.Pending; } }
        internal bool EffectiveEnabled { get { return Enabled && !disposed && !exitPreparing && !LayoutRecoveryPending; } }
        bool exitPreparing, exitPrepared, layoutExitQuiesced;
        SnapLayoutExitPolicy exitLayout;
        long verifiedWakeSequence;
        internal void PreviewShelfActivation(bool visible)
        {
            if (disposed) return;
            previewActivation = visible;
            ActivationPreviewError = null;
            if (shelf != null) shelf.SetActivationPreview(visible);
            UpdateActivationPreview();
        }
        void UpdateActivationPreview()
        {
            if (!previewActivation || disposed) {
                if (activationPreview != null) try { activationPreview.Hide(); } catch (Exception) { ActivationPreviewError = "Activation preview unavailable"; }
                return;
            }
            if (activationPreview == null) activationPreview = new ShelfActivationPreview();
            try {
                var targets = ShelfTargets().Where(display => {
                    Native.MonitorInfo info;
                    return Native.TryMonitor(display.Device, out info) && (info.Flags & 1) == 0 &&
                        Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom) == WindowShelfPolicy.Bounds(display);
                });
                activationPreview.Update(targets, Settings.WindowShelfActivationWidth, Settings.WindowShelfActivationHeight, WindowShelfManager.ReadDpi, delegate(Display display) {
                    Native.MonitorInfo info;
                    return Native.TryMonitor(display.Device, out info) ?
                        Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom) : Rectangle.Empty;
                });
            } catch (Exception) {
                previewActivation = false; ActivationPreviewError = "Activation preview unavailable";
                try { activationPreview.Hide(); } catch (Exception) { }
                if (shelf != null) shelf.SetActivationPreview(false);
            }
        }
        internal List<Display> ShelfTargets()
        {
            if (disposed || Settings == null || LayoutRecoveryPending) return new List<Display>();
            return Displays.Where(display => Policy.Shelf(display, Displays, Settings, Enabled, MovementPaused) &&
                Bars.Any(bar => bar.Device == display.Device && !bar.Primary && Native.IsWindow(new IntPtr(bar.Handle)) &&
                    !Native.IsWindowVisible(new IntPtr(bar.Handle)) && Native.GetProp(new IntPtr(bar.Handle), Native.Marker) == new IntPtr(1))).ToList();
        }
        bool MoveFromShelf(ShelfWindow window, Display target)
        {
            if (window == null || target == null || !ShelfTargets().Any(d => d.Device == target.Device && d.Identity == target.Identity)) return false;
            ConfigureMover(Mover, Settings, EffectiveEnabled, MovementPaused);
            bool moved = Mover.MoveFromShelf(window, target);
            Revision++;
            return moved;
        }
        bool IsSearchWindow(IntPtr window)
        { return shelf != null && shelf.IsSearchWindow(window); }
        internal List<Display> SearchTargets()
        {
            if (disposed || Settings == null || !EffectiveEnabled || MovementPaused) return new List<Display>();
            return Displays.Where(d => d != null && d.Width >= 160 && d.Height >= 160 &&
                !String.IsNullOrWhiteSpace(d.Device) && !String.IsNullOrWhiteSpace(d.Identity) &&
                Displays.Count(other => other != null && String.Equals(other.Device, d.Device, StringComparison.OrdinalIgnoreCase)) == 1 &&
                Displays.Count(other => other != null && String.Equals(other.Identity, d.Identity, StringComparison.OrdinalIgnoreCase)) == 1).ToList();
        }
        bool MoveFromSearch(ShelfWindow window, Display target)
        {
            if (window == null || target == null || !SearchTargets().Any(d => d.Device == target.Device && d.Identity == target.Identity)) return false;
            ConfigureMover(Mover, Settings, EffectiveEnabled, MovementPaused);
            bool moved = Mover.MoveFromShelf(window, target);
            Revision++;
            return moved;
        }
        internal void ObserveFocus(IntPtr foreground)
        { if (!disposed) FocusedMonitor.Observe(foreground, Displays); }
        internal void CloseFocusedWindow(IntPtr foreground)
        {
            if (disposed || !EffectiveEnabled) { Mover.LastStatus = "Window actions are paused"; Revision++; return; }
            if (tilingPicker != null && IsTransientWindow(foreground)) { CancelTilingPicker(); Mover.LastStatus = "Layout picker closed"; Revision++; return; }
            if (IsSearchWindow(foreground) && shelf.DismissSearch()) {
                Mover.LastStatus = "Search closed"; Revision++; return;
            }
            Mover.CancelPending();
            Mover.LastStatus = FocusedActions.RequestClose(foreground).Status;
            Revision++;
        }
        internal void MinimizeFocusedWindow(IntPtr foreground)
        {
            if (disposed || !EffectiveEnabled) { Mover.LastStatus = "Window actions are paused"; Revision++; return; }
            if (IsTransientWindow(foreground)) { Mover.LastStatus = "Select an app window before minimizing"; Revision++; return; }
            ConfigureMover(Mover, Settings, EffectiveEnabled, MovementPaused);
            Mover.CancelPending();
            if (!Mover.TryBeginMinimizeAction()) { Revision++; return; }
            bool remembered = false;
            try {
                var result = WindowStateActions.Minimize(foreground);
                remembered = result.Success && result.UndoEntry != null && Mover.RememberMinimized(result.UndoEntry);
                Mover.LastStatus = result.Success && !remembered ? "Window minimized; its state changed before Undo could be saved" : result.Status;
            } finally {
                // Publish the history entry and status before a fast Undo can
                // run. An unconfirmed minimize consumes its queued request.
                Mover.CompleteMinimizeAction(remembered);
                Revision++;
            }
        }
        internal void TogglePinnedWindow(IntPtr foreground)
        {
            if (disposed || !EffectiveEnabled) { Mover.LastStatus = "Window actions are paused"; Revision++; return; }
            if (IsTransientWindow(foreground)) { Mover.LastStatus = "Select an app window before pinning"; Revision++; return; }
            ConfigureMover(Mover, Settings, EffectiveEnabled, MovementPaused);
            Mover.CancelPending();
            Mover.LastStatus = WindowStateActions.TogglePin(foreground).Status;
            Revision++;
        }
        internal bool TryReleaseWindowPins(long deadline)
        { return WindowStateActions == null || WindowStateActions.TryReleasePins(deadline); }
        internal void OpenAppSearch(IntPtr foreground)
        {
            if (disposed || !EffectiveEnabled || MovementPaused) { Mover.LastStatus = "Search is paused"; Revision++; return; }
            CancelTilingPicker();
            if (shelf != null && shelf.DismissSearch()) {
                Mover.LastStatus = "Search closed"; Revision++; return;
            }
            DiscoverConnectedDisplays();
            Display destination = FocusedMonitor.Resolve(foreground, SearchTargets());
            EnsureShelf();
            bool opened = destination != null && shelf != null && shelf.OpenSearchOnDisplay(destination);
            Mover.LastStatus = opened ? "Search on " + destination.Label : "Search unavailable: no eligible connected display";
            Revision++;
        }
        void EnsureShelf()
        {
            if (shelf == null && (Settings.WindowShelfIdentities.Count > 0 || Settings.SearchAppsShortcut != 0))
                shelf = new WindowShelfManager(ShelfTargets, delegate { return Settings.ExcludedApps; }, MoveFromShelf,
                    Mover.ShelfMinimizes, delegate { return Settings; }, Mover.CancelPending, SearchTargets, MoveFromSearch);
        }
        internal void BeginDisplayDiscovery(TimeSpan duration)
        {
            StopOwnedSnapInput();
            RefreshDisplayIdentity = true;
            nextDisplayIdentityRetry = DateTime.MinValue;
            DateTime now = DateTime.UtcNow;
            retryDisplayIdentityUntil = now.Add(duration);
            // Display notifications can precede the settled Windows topology.
            minimumDisplayDiscoveryUntil = now.AddSeconds(Math.Min(10, duration.TotalSeconds));
        }
        internal bool BeginWakeEpoch(long sequence)
        {
            if (disposed || !Areas.BeginWakeEpoch(sequence)) return false;
            verifiedWakeSequence = sequence;
            if (snapAreas != null) snapAreas.BeginWakeEpoch(sequence);
            return true;
        }
        internal static bool AllConfiguredDisplaysAvailable(Settings settings, IEnumerable<Display> displays)
        {
            return settings.HiddenIdentities.All(identity => displays.Any(display => display.Identity == identity));
        }
        internal void Change(Settings settings, string label, bool retryShortcut = false, int retryMonitor = -1, int retryAction = -1, int retryNavigation = -1)
        {
            Settings previous = Settings;
            ApplySettings(settings, retryShortcut, retryMonitor, retryAction, retryNavigation);
            History.Push(previous, null, label);
            Revision++;
        }
        internal void InitializeShortcut()
        {
            try { UndoShortcut.Apply((Keys)Settings.UndoMoveShortcut, delegate { }); ShortcutError = null; }
            catch (InvalidOperationException error) { ShortcutError = error.Message; }
            int[] actions = ActionShortcutPolicy.Choices(Settings);
            for (int i = 0; i < actions.Length; i++) {
                try { ActionShortcuts[i].Apply((Keys)actions[i], delegate { }); ActionShortcutErrors[i] = null; }
                catch (InvalidOperationException error) { ActionShortcutErrors[i] = error.Message; }
            }
            int[] navigation = NavigationShortcutPolicy.Choices(Settings);
            for (int i = 0; i < navigation.Length; i++) {
                try { NavigationShortcuts[i].Apply((Keys)navigation[i], delegate { }); NavigationShortcutErrors[i] = null; }
                catch (InvalidOperationException error) { NavigationShortcutErrors[i] = error.Message; }
            }
            if (!monitorShortcutsInitialized) {
                for (int i = 0; i < MonitorShortcuts.Length; i++) ObserveMonitorShortcut(MonitorShortcuts[i], i + 1);
                monitorShortcutsInitialized = true;
            }
            SyncMonitorShortcuts();
        }
        void ObserveMonitorShortcut(GlobalShortcut binding, int number)
        {
            binding.Pressed += delegate {
                var pressed = MonitorShortcutPressed;
                if (pressed != null) pressed(number);
            };
        }
        GlobalShortcut MonitorBinding(int number)
        {
            GlobalShortcut binding;
            return number <= MonitorShortcuts.Length ? MonitorShortcuts[number - 1] :
                additionalMonitorBindings.TryGetValue(number, out binding) ? binding : null;
        }
        string MonitorError(int number)
        {
            string error;
            return number <= MonitorShortcutErrors.Length ? MonitorShortcutErrors[number - 1] :
                additionalMonitorErrors.TryGetValue(number, out error) ? error : null;
        }
        void SetMonitorError(int number, string error)
        {
            if (number <= MonitorShortcutErrors.Length) MonitorShortcutErrors[number - 1] = error;
            else if (error == null) additionalMonitorErrors.Remove(number);
            else additionalMonitorErrors[number] = error;
        }
        internal string MonitorShortcutStatus(int number)
        {
            if (number < 1) return "Display unavailable";
            var binding = MonitorBinding(number);
            return MonitorError(number) ?? (binding == null || binding.Current == Keys.None ? "Shortcut off" : "Shortcut active");
        }
        internal void ChangeMonitorShortcut(int number, Keys choice)
        {
            if (MonitorShortcutPolicy.Target(Displays, number) == null)
                throw new InvalidOperationException("The display is no longer available.");
            var next = Settings.Copy();
            MonitorShortcutPolicy.SetChoice(next, number, (int)choice);
            Change(next, "Monitor shortcut", retryMonitor: number);
        }
        internal void SyncMonitorShortcuts()
        {
            if (!monitorShortcutsInitialized || applyingShortcutSettings || disposed) return;
            var connected = new HashSet<int>(MonitorShortcutPolicy.ConnectedDisplays(Displays).Select(MonitorShortcutPolicy.DisplayNumber));
            foreach (int number in connectedMonitorBindings.Except(connected).ToArray()) {
                var binding = MonitorBinding(number);
                if (number <= MonitorShortcuts.Length) binding.Apply(Keys.None, delegate { });
                else { binding.Dispose(); additionalMonitorBindings.Remove(number); }
                SetMonitorError(number, null);
                connectedMonitorBindings.Remove(number);
            }
            foreach (int number in connected.Except(connectedMonitorBindings).OrderBy(value => value)) {
                var binding = MonitorBinding(number);
                if (binding == null) {
                    binding = new GlobalShortcut();
                    ObserveMonitorShortcut(binding, number);
                    additionalMonitorBindings.Add(number, binding);
                }
                try { binding.Apply((Keys)MonitorShortcutPolicy.Choice(Settings, number), delegate { }); }
                catch (InvalidOperationException error) { SetMonitorError(number, error.Message); }
                connectedMonitorBindings.Add(number);
            }
        }
        void DiscoverConnectedDisplays()
        {
            Displays = Native.Displays();
            SyncMonitorShortcuts();
        }
        internal void MoveFocusedWindow(int number, IntPtr foreground)
        {
            if (!EffectiveEnabled || MovementPaused) { Mover.LastStatus = "Window movement is paused"; Revision++; return; }
            if (tilingPicker != null) { CancelTilingPicker(); Mover.LastStatus = "Layout picker closed; select a window to move"; Revision++; return; }
            DiscoverConnectedDisplays();
            ConfigureMover(Mover, Settings, EffectiveEnabled, MovementPaused);
            Mover.MoveFocusedWindow(foreground, MonitorShortcutPolicy.Target(Displays, number), number);
            Revision++;
        }
        void ApplySettings(Settings settings, bool retryShortcut = false, int retryMonitor = -1, int retryAction = -1, int retryNavigation = -1)
        {
            InvalidateOwnedSnapCycle(true);
            // Ordinary edits and settings Undo cannot grant or restore a stale
            // permission intent. Only the explicit permission coordinator saves it.
            PreserveAdministratorPreference(settings, Settings);
            CancelTilingPicker();
            Mover.CancelPending();
            Settings previous = Settings;
            MonitorShortcutPolicy.Validate(settings);
            SyncMonitorShortcuts();
            Action persist = delegate {
                Program.Save(settings);
                Settings = settings;
                try { Refresh(); }
                catch { Settings = previous; Program.Save(previous); Refresh(); throw; }
            };
            int[] numbers = connectedMonitorBindings.OrderBy(number => number).ToArray();
            GlobalShortcut[] monitorBindings = numbers.Select(MonitorBinding).ToArray();
            int[] choices = MonitorShortcutPolicy.RegistrationChoices(numbers.Select(number => MonitorShortcutPolicy.Choice(settings, number)).ToArray(),
                numbers.Select(number => MonitorShortcutPolicy.Choice(previous, number)).ToArray(),
                monitorBindings.Select(binding => (int)binding.Current).ToArray(), Array.IndexOf(numbers, retryMonitor));
            int[] previousActions = ActionShortcutPolicy.Choices(previous);
            int[] actionChoices = MonitorShortcutPolicy.RegistrationChoices(ActionShortcutPolicy.Choices(settings),
                previousActions, ActionShortcuts.Select(binding => (int)binding.Current).ToArray(), retryAction);
            int[] navigationChoices = MonitorShortcutPolicy.RegistrationChoices(NavigationShortcutPolicy.Choices(settings), NavigationShortcutPolicy.Choices(previous),
                NavigationShortcuts.Select(binding => (int)binding.Current).ToArray(), retryNavigation);
            Action navigationBindings = delegate { MonitorShortcutPolicy.Apply(NavigationShortcuts, navigationChoices, persist); };
            Action actionBindings = delegate { MonitorShortcutPolicy.Apply(ActionShortcuts, actionChoices, navigationBindings); };
            Action bindings = delegate { MonitorShortcutPolicy.Apply(monitorBindings, choices, actionBindings); };
            applyingShortcutSettings = true;
            try {
                if (retryShortcut || settings.UndoMoveShortcut != previous.UndoMoveShortcut ||
                    ((Keys)settings.UndoMoveShortcut != UndoShortcut.Current && ShortcutError == null))
                    UndoShortcut.Apply((Keys)settings.UndoMoveShortcut, bindings);
                else bindings();
            } finally {
                applyingShortcutSettings = false;
                SyncMonitorShortcuts();
            }
            ShortcutError = MonitorShortcutPolicy.RegistrationError(settings.UndoMoveShortcut, (int)UndoShortcut.Current, ShortcutError);
            foreach (int number in numbers.Where(connectedMonitorBindings.Contains))
                SetMonitorError(number, MonitorShortcutPolicy.RegistrationError(MonitorShortcutPolicy.Choice(settings, number), (int)MonitorBinding(number).Current, MonitorError(number)));
            int[] actions = ActionShortcutPolicy.Choices(settings);
            for (int i = 0; i < actions.Length; i++) ActionShortcutErrors[i] = MonitorShortcutPolicy.RegistrationError(actions[i], (int)ActionShortcuts[i].Current, ActionShortcutErrors[i]);
            int[] navigation = NavigationShortcutPolicy.Choices(settings);
            for (int i = 0; i < navigation.Length; i++) NavigationShortcutErrors[i] = MonitorShortcutPolicy.RegistrationError(navigation[i], (int)NavigationShortcuts[i].Current, NavigationShortcutErrors[i]);
        }
        internal void ChangeStartup(bool enabled)
        {
            bool previous = Startup.Enabled;
            if (enabled == previous) return;
            Startup.SetEnabled(enabled);
            History.Push(Settings, previous, "Startup");
            Revision++;
        }
        internal void UndoSettings()
        {
            var entry = History.Peek;
            if (entry == null) return;
            if (entry.Startup.HasValue) Startup.SetEnabled(entry.Startup.Value);
            else ApplySettings(entry.Settings.Copy());
            History.Pop(); Revision++;
        }
        internal void ToggleMovementPause()
        {
            StopOwnedSnapInput();
            CancelTilingPicker();
            MovementPaused = !MovementPaused;
            if (MovementPaused) HideFocusIndicator();
            Mover.CancelPending(); Refresh(); Revision++;
        }
        void HideFocusIndicator()
        {
            try { if (FocusFrame != null) FocusFrame.Hide(); }
            catch (Exception) { FocusIndicatorError = "Focus indicator unavailable"; }
            if (Administrator != null) Administrator.HideFocus();
        }
        void HidePinnedIndicators()
        {
            try { if (PinRails != null) PinRails.Update(new PinnedWindowObservation[0], true); } catch (Exception) { }
            if (Administrator != null) Administrator.HidePins();
        }
        bool IsPinnedFocusOverlay(IntPtr candidate, IntPtr target)
        {
            try {
                if (FocusFrame != null && candidate == FocusFrame.Handle)
                    return FocusIndicatorSurface.MarksFocusSurface(candidate, target);
                return Administrator != null && Administrator.OwnsFocusSurface(candidate, target);
            } catch (Exception) { return false; }
        }
        internal void RefreshFocusIndicator()
        {
            if (FocusFrame == null || disposed || Settings == null) return;
            FocusIndicatorVisual pinFocus = null;
            try {
                if (AdministratorPermissions != null) AdministratorPermissions.Poll();
                bool paused = !EffectiveEnabled || MovementPaused || previewActivation;
                bool administratorTarget = false;
                if (Administrator != null) {
                    Administrator.TryReconnectRemembered(DesktopRuntime && EffectiveEnabled && Settings.PersistentAdministratorSupport &&
                        (AdministratorPermissions == null || AdministratorPermissions.ReconnectAllowed),
                        !MovementPaused && !previewActivation && !Mover.MovementBusy);
                    Administrator.Poll();
                    Administrator.UpdateFocus((int)FocusAppearance.FrameStyle(Settings.FocusIndicatorStyle), Settings.AccentPreset, paused,
                        Settings.FocusIndicatorGlow, Settings.FocusIndicatorLighting, Settings.FocusIndicatorFinish, !EffectiveEnabled || previewActivation);
                    uint process;
                    Native.GetWindowThreadProcessId(FocusedWindowNative.Foreground(), out process);
                    administratorTarget = Administrator.Active && NativeProcessIdentity.RequiresAdministrator(process);
                }
                FocusFrame.Update((int)FocusAppearance.FrameStyle(Settings.FocusIndicatorStyle), Settings.AccentPreset, paused || administratorTarget,
                    Settings.FocusIndicatorGlow, Settings.FocusIndicatorLighting, Settings.FocusIndicatorFinish,
                    PinRails == null ? null : new Func<FocusedWindowSnapshot, bool>(PinRails.ReservesFocusCorner));
                pinFocus = FocusFrame.CurrentVisual;
                // The authenticated helper owns both HIGH focus and pin cues.
                FocusIndicatorError = FocusFrame.Error;
            } catch (Exception) {
                HideFocusIndicator();
                pinFocus = null;
                FocusIndicatorError = "Focus indicator unavailable; window controls remain active";
            }
            try {
                if (PinRails != null) PinRails.Update(WindowStateActions.IndicatedPins.Where(pin => pin != null && !pin.HelperOwned).ToArray(),
                    !EffectiveEnabled || previewActivation, pinFocus, Settings.AccentPreset);
            } catch (Exception) { HidePinnedIndicators(); }
        }
        void RecoverSelectedLayouts(IEnumerable<SnapCorrectionArea> selected)
        {
            var scope = selected.ToArray();
            var result = Recovery.Run(delegate { Areas.RestoreSelected(scope); }, delegate {
                var displays = Native.Displays();
                var selectedBars = Native.Bars().Where(bar => !bar.Primary && scope.Any(area => {
                    var record = area.ToRecord();
                    var matches = displays.Where(d => String.Equals(d.Identity, record.Identity, StringComparison.OrdinalIgnoreCase)).ToList();
                    Native.MonitorInfo info;
                    return matches.Count == 1 && !matches[0].Primary && String.Equals(matches[0].Device, bar.Device, StringComparison.OrdinalIgnoreCase) &&
                        Native.TryMonitor(bar.Device, out info) && (info.Flags & 1) == 0 && AreaPolicy.Equal(record.Monitor, info.Monitor) &&
                        Native.IsSecondaryTaskbar(bar);
                })).ToArray();
                Native.RestoreAll(selectedBars, new Native.TaskbarRestoreApi());
            });
            Recovery.Report(result);
            if (!result.Success) throw new InvalidOperationException("Taskbar restoration was not confirmed.");
        }
        bool CompleteLayoutRestore(bool explicitAttempt)
        {
            if (layoutRestore == null) return true;
            if (layoutRestore.TryComplete(explicitAttempt)) {
                layoutRestore = null; TaskbarRecoveryError = null;
                // A verified display wake may have arrived while intake was
                // fenced. Deliver its existing sequence after resumption; this
                // does not invent an epoch or reset a same-sequence budget.
                if (snapAreas != null && verifiedWakeSequence > 0) snapAreas.BeginWakeEpoch(verifiedWakeSequence);
                return true;
            }
            Mover.Enabled = false; Mover.CancelPending();
            TaskbarRecoveryError = layoutRestore.Error ?? "Window recovery is paused; retry Pause all or Restore and exit.";
            HideFocusIndicator();
            if (shelf != null) shelf.Refresh();
            return false;
        }
        internal void Refresh(bool explicitLayoutRetry = false)
        {
            if (exitPreparing) { Enabled = false; Mover.Enabled = false; return; }
            if (DesktopRuntime && Settings != null && Ui.ApplyAccent(Settings.AccentPreset)) {
                foreach (Form owned in Application.OpenForms.Cast<Form>().ToArray())
                    if (!owned.IsDisposed) owned.Invalidate(true);
            }
            ValidateTilingPicker();
            ConfigureMover(Mover, Settings, EffectiveEnabled, MovementPaused);
            DateTime now = DateTime.UtcNow;
            bool refresh = RefreshDisplayIdentity || (now < retryDisplayIdentityUntil && now >= nextDisplayIdentityRetry);
            RefreshDisplayIdentity = false;
            if (refresh) {
                DiscoverConnectedDisplays(); Revision++;
                nextDisplayIdentityRetry = now.AddSeconds(5);
                if (now >= minimumDisplayDiscoveryUntil && AllConfiguredDisplaysAvailable(Settings, Displays))
                    retryDisplayIdentityUntil = DateTime.MinValue;
            }
            if (refresh || now >= nextDiscovery || Bars.Any(b => !Native.IsWindow(new IntPtr(b.Handle)))) {
                Bars = Native.Bars();
                nextDiscovery = now.AddSeconds(15);
            }
            // Fence before any native bar restoration or WorkAreas.Update can
            // retire the original area. A settings rollback or immediate Resume
            // cannot cancel a queued show/inverse request or re-hide its area.
            if (!CompleteLayoutRestore(explicitLayoutRetry)) return;
            Func<SnapCorrectionArea, bool> restoring = delegate(SnapCorrectionArea area) {
                return !Enabled || !Settings.HiddenIdentities.Contains(area.ToRecord().Identity);
            };
            var restoreAreas = Areas.RestorationAreas(restoring).Concat(snapAreas == null ?
                new SnapCorrectionArea[0] : snapAreas.RestorationAreas(restoring)).Distinct().ToArray();
            if (restoreAreas.Length != 0) {
                if (snapAreas == null) {
                    snapAreas = CreateSnapAreas();
                    if (verifiedWakeSequence > 0) snapAreas.BeginWakeEpoch(verifiedWakeSequence);
                }
                layoutRestore = new SnapLayoutRestorePolicy(restoreAreas,
                    delegate { snapAreas.QuiesceForRestore(restoreAreas); Mover.Enabled = false; Mover.CancelPending(); },
                    delegate { RecoverSelectedLayouts(restoreAreas); }, snapAreas.RestoreAreaState,
                    snapAreas.TryFinishRestore, snapAreas.ResumeAfterRestore);
                if (!CompleteLayoutRestore(true)) return;
            }
            // Rediscovery and display/power notifications must not replenish an
            // unresolved async-hide budget. Only positively destroyed/replaced
            // native lifetimes can be forgotten without hidden acknowledgement.
            IntPtr shellWindow = Native.GetShellWindow();
            uint shellProcess;
            Native.GetWindowThreadProcessId(shellWindow, out shellProcess);
            long shellStarted = NativeProcessIdentity.Started(shellProcess);
            foreach (var remembered in hideRequests.RememberedIdentities) {
                uint rememberedProcess;
                uint rememberedThread = Native.GetWindowThreadProcessId(remembered.Window, out rememberedProcess);
                bool replaced = rememberedThread != 0 && (rememberedThread != remembered.Thread || rememberedProcess != remembered.Process ||
                    (rememberedProcess == shellProcess && shellStarted > 0 && remembered.ProcessStarted != shellStarted));
                if (!Native.IsWindow(remembered.Window) || replaced) hideRequests.RetireDestroyed(remembered);
            }
            var failedRestores = new List<string>();
            bool hideBlocked = false;
            foreach (Bar bar in Bars)
            {
                IntPtr window = new IntPtr(bar.Handle);
                uint process;
                uint thread = Native.GetWindowThreadProcessId(window, out process);
                bar.Visible = Native.IsWindowVisible(window);
                bar.Managed = Native.GetProp(window, Native.Marker) == new IntPtr(1);
                bool shouldHide = Enabled && Policy.Hide(bar, Displays, Settings);
                bool secondary = !bar.Primary && Native.IsSecondaryTaskbar(bar);
                // A false visibility read is an acknowledgement only while the
                // same current-shell window identity still exists.
                uint observedProcess;
                bool identityCurrent = shellWindow != IntPtr.Zero && shellStarted > 0 && process == shellProcess && thread != 0 &&
                    Native.GetShellWindow() == shellWindow && Native.IsWindow(window) &&
                    Native.GetWindowThreadProcessId(window, out observedProcess) == thread && observedProcess == process;
                if (!bar.Primary && identityCurrent && secondary) {
                    var identity = new TaskbarHideRequests.Identity(window, process, shellStarted, thread);
                    var request = hideRequests.Request(identity, bar.Visible, shouldHide, delegate {
                        uint currentProcess;
                        return Native.GetShellWindow() == shellWindow && Native.IsWindow(window) &&
                            Native.GetWindowThreadProcessId(window, out currentProcess) == identity.Thread && currentProcess == identity.Process &&
                            NativeProcessIdentity.Started(currentProcess) == identity.ProcessStarted &&
                            Native.IsSecondaryTaskbar(bar) && Native.Hide(bar);
                    });
                    if (shouldHide && bar.Visible && request.CircuitOpen) hideBlocked = true;
                }
                else if (shouldHide && bar.Visible) hideBlocked = true;
                // Recovery never shares the hide circuit breaker or its budget.
                if (!shouldHide && !Native.Restore(bar)) failedRestores.Add(bar.Device);
            }
            TaskbarRecoveryError = failedRestores.Count != 0 ?
                "Taskbar restore was refused on " + String.Join(", ", failedRestores) + ". Recovery remains available; retry Pause all or restore." :
                hideBlocked ? "Windows has not confirmed the taskbar state. Further hide requests are paused." : null;
            Areas.Update(Bars, Displays, Settings, Enabled);
            if (!layoutExitQuiesced && snapAreas == null && Areas.SnapTargets.Count > 0) {
                snapAreas = CreateSnapAreas();
                if (verifiedWakeSequence > 0) snapAreas.BeginWakeEpoch(verifiedWakeSequence);
            }
            if (snapAreas != null) snapAreas.Update(Areas.SnapTargets);
            EnsureShelf();
            if (shelf != null) { shelf.SetActivationPreview(previewActivation); shelf.Refresh(); }
            UpdateActivationPreview();
            RefreshFocusIndicator();
            if (wakeDiagnostics != null) try {
                wakeDiagnostics(Areas.DiagnosticSnapshot(), snapAreas == null ? null : snapAreas.DiagnosticSnapshot());
            } catch (Exception) { }
        }
        internal static void ConfigureMover(TaskbarMover mover, Settings settings, bool enabled, bool paused)
        {
            mover.ScaleWindowSizeToMonitor = settings.ScaleWindowSizeToMonitor;
            mover.AllowMovingFullscreenApps = settings.AllowMovingFullscreenApps;
            mover.ExcludedApps = settings.ExcludedApps;
            mover.Enabled = enabled && !paused && settings.MoveWindowsToClickedTaskbar && mover.Available;
        }
        internal void QuiesceLayoutForExit()
        { layoutExitQuiesced = true; if (snapAreas != null) snapAreas.QuiesceForExit(); }
        internal bool TryPrepareExit(bool explicitAttempt)
        {
            StopOwnedSnapInput();
            exitPreparing = true;
            if (exitLayout == null) exitLayout = new SnapLayoutExitPolicy(
                delegate { Enabled = false; Mover.Enabled = false; Mover.CancelPending(); QuiesceLayoutForExit(); },
                delegate { var result = Recovery.Run(Areas.RestoreAll, Native.RestoreAll); Recovery.Report(result); return result; },
                delegate(bool allowInverse) { return snapAreas == null || snapAreas.TryFinishExit(allowInverse); });
            exitPrepared = exitLayout.TryPrepare(explicitAttempt);
            return exitPrepared;
        }
        public void Dispose()
        {
            if (disposed) return;
            if (DesktopRuntime && !exitPrepared && !TryPrepareExit(true))
                throw new InvalidOperationException("Window recovery is not confirmed. Taskbar Select remains paused.");
            disposed = true; StopOwnedSnapInput(); if (ownedSnapKeyboard != null) ownedSnapKeyboard.Dispose();
            Mover.CancelPending(); Enabled = false; Mover.Enabled = false;
            if (displayWakeObserver != null) try { displayWakeObserver.Dispose(); } catch (Exception) { }
            if (AdministratorPermissions != null) AdministratorPermissions.Dispose();
            if (Administrator != null) {
                if (Object.ReferenceEquals(WindowMoveWorker.Administrator, Administrator)) WindowMoveWorker.Administrator = null;
                Administrator.Dispose();
            }
            if (FocusFrame != null) try { FocusFrame.Dispose(); } catch (Exception) { }
            if (PinRails != null) try { PinRails.Dispose(); } catch (Exception) { }
            CancelTilingPicker();
            if (activationPreview != null) try { activationPreview.Dispose(); } catch (Exception) { }
            if (snapAreas != null) snapAreas.Dispose();
            try {
                try { if (shelf != null) shelf.Dispose(); }
                finally {
                    UndoShortcut.Dispose();
                    foreach (var binding in MonitorShortcuts.Concat(additionalMonitorBindings.Values)) binding.Dispose();
                    foreach (var binding in ActionShortcuts) binding.Dispose();
                    foreach (var binding in NavigationShortcuts) binding.Dispose();
                }
            }
            finally {
                try { if (!exitPrepared) Recovery.Report(Recovery.Run(Areas.RestoreAll, Native.RestoreAll)); }
                finally { Mover.Dispose(); }
            }
        }
    }

    internal sealed class AppContext : ApplicationContext
    {
        readonly Controller controller;
        readonly NotifyIcon tray;
        Icon trayIcon;
        Color trayIconColor;
        readonly System.Windows.Forms.Timer timer;
        readonly System.Windows.Forms.Timer focusTimer;
        readonly EventWaitHandle stop, show;
        SettingsForm form;
        bool exiting;
        System.Windows.Forms.Timer exitReadback;
        long exitReadbackDeadline;
        bool exitMenuEnabled, exitFormEnabled;
        internal AppContext(EventWaitHandle stopEvent, EventWaitHandle showEvent, bool startQuietly)
        {
            stop = stopEvent; show = showEvent;
            controller = new Controller { Settings = Program.Load(), Enabled = true };
            if (controller.Settings.PersistentAdministratorSupport)
                controller.Administrator.EnablePersistent();
            controller.BeginDisplayDiscovery(startQuietly ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(30));
            controller.InitializeShortcut();
            controller.MonitorShortcutPressed += delegate(int number) {
                if (exiting) return;
                IntPtr foreground = TaskbarMover.ForegroundWindow(); // Before UI/status changes.
                controller.ObserveFocus(foreground);
                try { controller.MoveFocusedWindow(number, foreground); }
                catch (Exception error) { controller.Mover.LastStatus = "Move stopped safely: " + error.Message; }
                UpdateStatus(); tray.ShowBalloonTip(3500, AppProfile.Title, controller.Mover.LastStatus, ToolTipIcon.Info);
            };
            controller.UndoShortcut.Pressed += delegate {
                if (exiting || controller.LayoutRecoveryPending) return;
                controller.CancelTilingPicker();
                try { controller.Mover.Undo(); }
                catch (Exception error) { controller.Mover.LastStatus = "Undo failed: " + error.Message; }
                UpdateStatus();
            };
            controller.ActionShortcuts[0].Pressed += delegate {
                if (exiting) return;
                IntPtr foreground = TaskbarMover.ForegroundWindow();
                controller.ObserveFocus(foreground);
                try { controller.CloseFocusedWindow(foreground); }
                catch (Exception error) { controller.Mover.LastStatus = "Close request failed: " + error.Message; }
                UpdateStatus();
            };
            controller.ActionShortcuts[1].Pressed += delegate {
                if (exiting) return;
                IntPtr foreground = TaskbarMover.ForegroundWindow();
                try { controller.OpenAppSearch(foreground); }
                catch (Exception error) { controller.Mover.LastStatus = "Search unavailable: " + error.Message; }
                UpdateStatus();
            };
            controller.ActionShortcuts[2].Pressed += delegate {
                if (exiting) return;
                IntPtr foreground = TaskbarMover.ForegroundWindow();
                controller.ObserveFocus(foreground);
                try { controller.MinimizeFocusedWindow(foreground); }
                catch (Exception error) { controller.Mover.LastStatus = "Minimize request failed: " + error.Message; }
                UpdateStatus();
            };
            controller.ActionShortcuts[3].Pressed += delegate {
                if (exiting) return;
                IntPtr foreground = TaskbarMover.ForegroundWindow();
                controller.ObserveFocus(foreground);
                try { controller.TogglePinnedWindow(foreground); }
                catch (Exception error) { controller.Mover.LastStatus = "Pin request failed: " + error.Message; }
                UpdateStatus();
                tray.ShowBalloonTip(1800, AppProfile.Title, controller.Mover.LastStatus, ToolTipIcon.Info);
            };
            for (int i = 0; i < controller.NavigationShortcuts.Length; i++) {
                int action = i;
                controller.NavigationShortcuts[i].Pressed += delegate {
                    if (exiting) return;
                    IntPtr foreground = TaskbarMover.ForegroundWindow();
                    try {
                        if (action == 4) controller.OpenTilingPicker(foreground);
                        else controller.FocusDirection(foreground, (WindowDirection)action);
                    } catch (Exception error) { controller.Mover.LastStatus = "Window action stopped safely: " + error.Message; }
                    UpdateStatus();
                };
            }
            var startupRecovery = Recovery.ForStartup(controller.Areas.RestoreAvailable, Native.RestoreAll);
            if (!startupRecovery.Success) throw new InvalidOperationException(String.Join(Environment.NewLine, startupRecovery.Messages));
            controller.Refresh();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged += PowerModeChanged;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Choose monitors...", null, delegate { Open(); });
            var undoMove = menu.Items.Add("Undo last window action", null, delegate { if (!controller.LayoutRecoveryPending) controller.Mover.Undo(); UpdateStatus(); });
            var pauseMove = menu.Items.Add("Pause window movement", null, delegate { controller.ToggleMovementPause(); UpdateStatus(); });
            menu.Opening += delegate {
                undoMove.Enabled = !controller.LayoutRecoveryPending && controller.Mover.CanUndo;
                ((ToolStripMenuItem)undoMove).ShortcutKeyDisplayString = controller.UndoShortcut.Current == Keys.None ? "" : GlobalShortcut.Format(controller.UndoShortcut.Current);
                pauseMove.Enabled = controller.Settings.HasWindowMovement || controller.OwnedSnapPrototypeEnabled;
                pauseMove.Text = controller.MovementPaused ? "Resume window movement" : "Pause window movement";
            };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Pause and restore taskbars", null, delegate {
                controller.Enabled = false; controller.Refresh(true); UpdateStatus();
            });
            menu.Items.Add("Resume", null, delegate {
                controller.Enabled = true; controller.Refresh(true); UpdateStatus();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Restore and exit", null, delegate {
                if (!AppProfile.IsTest && Startup.Enabled && MessageBox.Show("Start with Windows is still enabled, so Taskbar Select will run again at your next sign-in. Restore and exit now?",
                    "Taskbar Select", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
                ExitThread();
            });
            trayIconColor = Branding.AccentIconColor(controller.Settings.AccentPreset);
            trayIcon = Branding.LoadAccentIcon(SystemInformation.SmallIconSize.Width, controller.Settings.AccentPreset);
            tray = new NotifyIcon { Icon = trayIcon, Text = AppProfile.Title, ContextMenuStrip = menu, Visible = true };
            controller.Mover.FullscreenChecked += delegate { UpdateStatus(); tray.ShowBalloonTip(4000, AppProfile.Title, controller.Mover.LastStatus, ToolTipIcon.Info); };
            tray.DoubleClick += delegate { Open(); };
            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate {
                try {
                    if (stop.WaitOne(0)) { ExitThread(); return; }
                    controller.ObserveFocus(TaskbarMover.ForegroundWindow());
                    if (show.WaitOne(0)) Open();
                    controller.Refresh(false); UpdateStatus();
                } catch (Exception error) {
                    controller.Enabled = false; controller.Mover.Enabled = false; controller.Mover.CancelPending(); timer.Stop();
                    MessageBox.Show(error.Message, "Taskbar Select paused", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ExitThread();
                }
            };
            timer.Start();
            focusTimer = new System.Windows.Forms.Timer { Interval = 80 };
            focusTimer.Tick += delegate { controller.PollSnapIntent(); controller.PollOwnedSnapPrototype(); controller.RefreshFocusIndicator(); };
            focusTimer.Start();
            if (!startQuietly) Open();
            controller.Mover.HookAvailabilityChanged += delegate {
                if (exiting) return;
                Controller.ConfigureMover(controller.Mover, controller.Settings, controller.EffectiveEnabled, controller.MovementPaused);
                UpdateStatus();
            };
            controller.Mover.ArmDesktopHooks();
        }
        void UpdateStatus()
        {
            Color nextIconColor = Branding.AccentIconColor(controller.Settings.AccentPreset);
            if (nextIconColor != trayIconColor) {
                Icon next = Branding.LoadAccentIcon(SystemInformation.SmallIconSize.Width, controller.Settings.AccentPreset);
                try { tray.Icon = next; }
                catch { next.Dispose(); throw; }
                Icon previous = trayIcon; trayIcon = next; trayIconColor = nextIconColor;
                if (previous != null) previous.Dispose();
            }
            string text = AppProfile.Title + (controller.LayoutRecoveryPending ? " - recovery pending" : controller.Enabled ? " - active" : " - paused");
            if (tray.Text != text) tray.Text = text;
            if (form != null && !form.IsDisposed) form.UpdateStatus();
        }
        void DisplaySettingsChanged(object sender, EventArgs args) { controller.BeginDisplayDiscovery(TimeSpan.FromSeconds(30)); }
        void PowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args) { controller.BeginDisplayDiscovery(TimeSpan.FromSeconds(30)); }
        void Open()
        {
            if (exiting) return;
            if (form == null || form.IsDisposed) form = new SettingsForm(controller);
            form.Show();
            // A hidden launcher can suppress the first native ShowWindow call.
            Native.ShowWindowAsync(form.Handle, 8);
            form.Activate();
        }
        protected override void ExitThreadCore()
        {
            if (exiting) return;
            exiting = true;
            timer.Stop(); if (focusTimer != null) focusTimer.Stop();
            bool menuEnabled = tray.ContextMenuStrip.Enabled;
            tray.ContextMenuStrip.Enabled = false;
            bool formEnabled = form != null && !form.IsDisposed && form.Enabled;
            if (formEnabled) form.Enabled = false;
            controller.Enabled = false; controller.Mover.Enabled = false; controller.Mover.CancelPending();
            // Stop forward correction before any helper/pin recovery can pump
            // native callbacks or refresh the restored work areas.
            try { controller.QuiesceLayoutForExit(); } catch (Exception) { }
            if (controller.AdministratorPermissions != null) controller.AdministratorPermissions.CancelForExit();
            if (!controller.TryReleaseWindowPins(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2L)) {
                if (formEnabled && form != null && !form.IsDisposed) form.Enabled = true;
                tray.ContextMenuStrip.Enabled = menuEnabled;
                timer.Start(); if (focusTimer != null) focusTimer.Start();
                exiting = false;
                controller.Mover.LastStatus = "Some pinned windows could not be released. Taskbar Select remains paused; retry Restore and exit.";
                UpdateStatus();
                MessageBox.Show(controller.Mover.LastStatus, "Taskbar Select recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (controller.Administrator != null && !controller.Administrator.StopForExit(4000)) {
                // Keep desktop ownership and recovery while privileged work
                // cannot be proved stopped. Restore/uninstall must not race it.
                string recoveryError = "";
                try { controller.Refresh(); UpdateStatus(); }
                catch (Exception) { recoveryError = " Recovery status could not be refreshed."; }
                finally {
                    if (formEnabled && form != null && !form.IsDisposed) form.Enabled = true;
                    tray.ContextMenuStrip.Enabled = menuEnabled;
                    timer.Start(); if (focusTimer != null) focusTimer.Start();
                    exiting = false;
                }
                MessageBox.Show("Administrator support has not confirmed that its helper stopped. Taskbar Select remains paused and recovery was retained." + recoveryError + " Wait and retry Restore and exit.",
                    "Taskbar Select recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            exitMenuEnabled = menuEnabled; exitFormEnabled = formEnabled;
            if (!controller.TryPrepareExit(true)) {
                // Yield the UI thread while already-issued async writes settle.
                // This timer only reads: it cannot issue another inverse or SPI.
                exitReadbackDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
                exitReadback = new System.Windows.Forms.Timer { Interval = 60 };
                exitReadback.Tick += delegate {
                    if (controller.TryPrepareExit(false)) { StopExitReadback(); CompleteExit(); }
                    else if (Stopwatch.GetTimestamp() >= exitReadbackDeadline) {
                        StopExitReadback(); RetainPendingExit();
                    }
                };
                exitReadback.Start(); return;
            }
            CompleteExit();
        }
        void StopExitReadback()
        { if (exitReadback != null) { exitReadback.Stop(); exitReadback.Dispose(); exitReadback = null; } }
        void RetainPendingExit()
        {
            // Keep the message loop and desktop mutex alive so Setup/restore
            // cannot treat an empty work-area journal as full layout recovery.
            if (exitFormEnabled && form != null && !form.IsDisposed) form.Enabled = true;
            tray.ContextMenuStrip.Enabled = exitMenuEnabled;
            exiting = false;
            timer.Start(); if (focusTimer != null) focusTimer.Start();
            controller.Mover.LastStatus = "Window recovery is not confirmed. Taskbar Select remains paused; wait and retry Restore and exit.";
            UpdateStatus();
            MessageBox.Show(controller.Mover.LastStatus, "Taskbar Select recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        void CompleteExit()
        {
            if (focusTimer != null) { focusTimer.Stop(); focusTimer.Dispose(); }
            timer.Stop(); timer.Dispose(); controller.Dispose();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerModeChanged;
            tray.Visible = false; tray.Dispose(); trayIcon.Dispose();
            if (form != null) form.Dispose();
            base.ExitThreadCore();
        }
    }


    internal static class Diagnostics
    {
        internal static string Create(Controller controller)
        {
            var text = new StringBuilder();
            var version = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));
            text.AppendLine("Taskbar Select " + (version == null ? Assembly.GetExecutingAssembly().GetName().Version.ToString() : version.InformationalVersion));
            text.AppendLine(Environment.OSVersion.VersionString);
            text.AppendLine("State: " + (controller.Enabled ? "active" : "paused"));
            text.AppendLine("Administrator support: " + (controller.Administrator == null ? "Off" : controller.Administrator.Status));
            if (controller.Administrator != null && !String.IsNullOrEmpty(controller.Administrator.LastIncident))
                text.AppendLine("Administrator last incident: " + controller.Administrator.LastIncident);
            if (controller.TaskbarRecoveryError != null) text.AppendLine(controller.TaskbarRecoveryError);
            text.AppendLine("Startup: " + (Startup.Enabled ? "enabled" : "disabled"));
            text.AppendLine("Move from clicked taskbar: " + (controller.Settings.MoveWindowsToClickedTaskbar ? "enabled" : "disabled"));
            text.AppendLine("Window sizing: " + (controller.Settings.ScaleWindowSizeToMonitor ? "relative size and position" : "keep window size"));
            text.AppendLine("Ghost Bar: " + controller.Settings.WindowShelfIdentities.Count + " displays selected");
            text.AppendLine("Ghost Bar activation: " + controller.Settings.WindowShelfActivationWidth + " x " +
                controller.Settings.WindowShelfActivationHeight + " physical pixels; hover " + controller.Settings.WindowShelfHoverDelayMs + " ms");
            text.AppendLine("Movement paused: " + controller.MovementPaused + "; app exclusions: " + controller.Settings.ExcludedApps.Count);
            text.AppendLine("Movement diagnostic: " + controller.Mover.MovementDiagnostic);
            text.AppendLine("Taskbar hook diagnostic: " + controller.Mover.TaskbarHookDiagnostic);
            text.AppendLine("Taskbar hook lifetime: " + controller.Mover.TaskbarHookLifetimeDiagnostic);
            text.AppendLine("Taskbar gesture diagnostic: " + controller.Mover.TaskbarGestureDiagnostic);
            text.AppendLine("Focus frame: " + FocusAppearance.FrameLabel(controller.Settings.FocusIndicatorStyle) + "; style: " + FocusAppearance.FinishLabel(controller.Settings.FocusIndicatorFinish) + "; accent: " + FocusAppearance.AccentLabel(controller.Settings.AccentPreset) + "; lighting: " +
                FocusAppearance.ResolveLighting(controller.Settings.FocusIndicatorLighting, controller.Settings.FocusIndicatorGlow));
            if (controller.FocusIndicatorError != null) text.AppendLine(controller.FocusIndicatorError);
            text.AppendLine(controller.OwnedSnapDiagnostic());
            if (controller.Administrator != null) text.AppendLine(controller.Administrator.SnapInputDiagnostic());
            text.AppendLine("Undo shortcut: " + GlobalShortcut.Format(controller.UndoShortcut.Current) +
                (controller.ShortcutError == null ? "" : " | " + controller.ShortcutError));
            for (int i = 0; i < controller.ActionShortcuts.Length; i++)
                text.AppendLine(ActionShortcutPolicy.Labels[i] + " shortcut: " +
                    GlobalShortcut.Format(controller.ActionShortcuts[i].Current) +
                    (controller.ActionShortcutErrors[i] == null ? "" : " | " + controller.ActionShortcutErrors[i]));
            for (int i = 0; i < controller.NavigationShortcuts.Length; i++)
                text.AppendLine((i == 4 ? "Tiling picker shortcut: " : "Focus " + ((WindowDirection)i).ToString().ToLowerInvariant() + " shortcut: ") +
                    GlobalShortcut.Format(controller.NavigationShortcuts[i].Current) +
                    (controller.NavigationShortcutErrors[i] == null ? "" : " | " + controller.NavigationShortcutErrors[i]));
            text.AppendLine("Displays: " + controller.Displays.Count + "; taskbars: " + controller.Bars.Count);
            foreach (Display display in controller.Displays) {
                bool hidden = controller.Settings.HiddenIdentities.Contains(display.Identity);
                text.AppendLine(display.Device + " | " + display.Name + " | " + display.Width + "x" + display.Height +
                    " @ " + display.X + "," + display.Y + " | " + (display.Primary ? "main" : (hidden ? "selected hidden" : "selected visible")) +
                    " | identity " + (String.IsNullOrEmpty(display.Identity) ? "missing" : "available"));
            }
            return text.ToString();
        }
    }

    internal static class Program
    {
        internal static string ConfigPath { get { return Path.Combine(AppProfile.Directory, "settings.json"); } }
        internal static Settings Load()
        { return Load(ConfigPath); }
        internal static Settings Load(string path)
        {
            if (!File.Exists(path)) return Settings.CreateFresh();
            var settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path));
            if (settings == null || settings.HiddenIdentities == null) throw new InvalidDataException("Invalid Taskbar Select settings. Restore taskbars and check " + path);
            if (settings.MonitorAliases == null) settings.MonitorAliases = new Dictionary<string, string>();
            if (settings.WindowShelfIdentities == null) settings.WindowShelfIdentities = new List<string>();
            settings.WindowShelfIdentities = settings.WindowShelfIdentities.Where(id => !String.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (settings.ExcludedApps == null) settings.ExcludedApps = new List<AppRule>();
            settings.ExcludedApps.RemoveAll(rule => rule == null || String.IsNullOrWhiteSpace(rule.Key));
            MonitorShortcutPolicy.Normalize(settings);
            return settings;
        }
        internal static void Save(Settings settings)
        { Save(settings, ConfigPath); }
        internal static void Save(Settings settings, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(settings));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        sealed class LifecycleEnvironment : ApplicationLifecycle.IEnvironment
        {
            readonly EventWaitHandle stop;
            readonly string mutexName;
            internal LifecycleEnvironment(EventWaitHandle stopEvent, string desktopMutex) { stop = stopEvent; mutexName = desktopMutex; }
            public bool Production { get { return AppProfile.ProductionSafe(); } }
            public bool OrdinaryUser { get { return OrdinaryLifecycleUser(); } }
            public bool RequiresApprovalRemoval() { return PersistentAdministrator.RequiresApprovalRemoval(PersistentAdministrator.ScopeForCurrentProfile()); }
            public IDisposable StopAndAcquire()
            {
                stop.Set();
                var mutex = new Mutex(false, mutexName);
                try {
                    bool acquired;
                    try { acquired = mutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new InvalidOperationException("The utility has not confirmed that it stopped. Wait and retry; recovery was retained.");
                    return new DesktopLease(mutex);
                } catch { mutex.Dispose(); throw; }
            }
            public RecoveryResult Restore() { return Recovery.Persisted(); }
            public void RemoveUserState()
            {
                Startup.SetEnabled(false);
                if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
                string configDirectory = Path.GetDirectoryName(ConfigPath);
                if (Directory.Exists(configDirectory) && Directory.GetFileSystemEntries(configDirectory).Length == 0) Directory.Delete(configDirectory);
            }
        }
        sealed class DesktopLease : IDisposable
        {
            Mutex mutex;
            internal DesktopLease(Mutex value) { mutex = value; }
            public void Dispose() { if (mutex != null) { try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); mutex = null; } } }
        }
        static bool OrdinaryLifecycleUser()
        {
            var identity = NativeProcessIdentity.Current;
            return identity.IntegrityAvailable && identity.ElevationAvailable && !identity.Elevated && identity.IntegrityLevel == NativeProcessIdentity.Medium;
        }
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--self-test-layout-retirement") return SnapLayoutRetirementTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-snap-intent") return SnapIntentPureSuite.Run();
            if (args.Length == 1 && args[0] == "--self-test-layout-retirement-adversarial") return SnapLayoutRetirementAdversarialTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-layout-transitions") return SnapLayoutTransitionTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-layout-transition-adversarial") return SnapLayoutTransitionAdversarialTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-layout-property-native") return SnapLayoutPropertyNativeTests.Run();
            if (args.Length == 1 && args[0] == "--self-test-layout-properties") return SnapLayoutPropertyTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-layout-receipts") return SnapLayoutReceiptTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-layout-adversarial") return SnapLayoutAdversarialTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-wake-policies") return Tests.RunWakePolicies();
            if (args.Length == 1 && args[0] == "--self-test-workarea-guard") return WorkAreaGuardTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-workarea-reclaim") return WorkAreaReclaimTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-snap-correction-policy") return SnapCorrectionPolicyTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-display-wake-policy") return DisplayWakePolicyTests.RunPureTest();
            if (args.Length == 1 && args[0] == "--self-test-wake-recovery-diagnostics") return WakeRecoveryDiagnostics.RunPureTest();
            int identityExit;
            if (TaskManagerShellIdentity.TryRun(args, out identityExit)) return identityExit;
            // Privileged modes belong only to the separately staged payload.
            if (args.Length > 0 && args[0] == "--administrator-helper") return 2;
            if (args.Length == 1 && args[0] == "--self-test-hook-lifetime") return Tests.RunHookLifetime();
            if (WindowMoveWorker.TryRun(args)) return 0;
            try { AppProfile.Initialize(args); }
            catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
            if (args.Length == 1 && args[0] == "--self-test-updates") return Tests.RunUpdates();
            LifecycleAction lifecycle = LifecycleAction.Upgrade;
            bool lifecycleCommand = ApplicationLifecycle.HasCommand(args);
            if (lifecycleCommand) {
                try {
                    lifecycle = ApplicationLifecycle.Parse(args);
                    if (!AppProfile.ProductionSafe() || !OrdinaryLifecycleUser()) throw new InvalidOperationException("Run the production installer normally, without Run as administrator.");
                } catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
            }
            if (args.Contains("--verify-installer-lifecycle")) {
                if (args.Length != 1 || !AppProfile.ProductionSafe()) return 2;
                Console.WriteLine(ApplicationLifecycle.Capability); return 0;
            }
            if (args.Contains("--verify-production")) {
                if (!AppProfile.ProductionSafe()) { Console.Error.WriteLine("A test profile cannot be packaged as production."); return 1; }
                Console.WriteLine("TASKBARSELECT_PRODUCTION_PROFILE_OK"); return 0;
            }
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            if (args.Contains("--self-test-wake-safety")) return WakeSafetyTests.Run();
            if (args.Contains("--self-test-pin-corner-native")) return Tests.RunPinCornerNative();
            if (args.Contains("--self-test-pin-rail") || args.Contains("--self-test-pin-corner")) return Tests.RunPinRails();
            if (args.Contains("--self-test-beta7-fullscreen")) return Beta7FullscreenTests.RunFocused();
            if (args.Contains("--self-test")) return Tests.Run();
            if (args.Contains("--self-test-owned-snap")) return OwnedSnapPrototypeTests.Run();
            if (args.Contains("--self-test-search-placement")) return Tests.RunSearchPlacement();
            if (args.Contains("--self-test-administrator-snap"))
                return AdministratorSnapProtocolTests.Run() != 0 || AdministratorSnapInputTests.Run() != 0 ? 1 : 0;
            if (args.Contains("--self-test-navigation")) return Tests.RunNavigation();
            if (args.Contains("--self-test-window-state")) return Tests.RunWindowStates();
            if (args.Contains("--self-test-appearance")) return Tests.RunAppearance();
            if (args.Contains("--self-test-focus-reveal")) return Tests.RunFocusReveal();
            if (args.Contains("--self-test-focus-targets")) return Tests.RunFocusTargets();
            if (args.Contains("--self-test-administrator")) return Tests.RunAdministrator();
            if (args.Contains("--self-test-administrator-ui")) return Tests.RunAdministratorUi();
            if (args.Contains("--self-test-lifecycle")) return Tests.RunLifecycle();
            if (args.Contains("--self-test-persistent-host")) return PersistentAdministratorHostTests.Run(args);
            if (args.Contains("--self-test-taskbar-targets")) return Tests.RunTaskbarTargets();
            if (args.Contains("--self-test-deferred-controller")) return Tests.RunDeferredController();
            if (args.Contains("--self-test-settings-taskbar")) return Tests.RunSettingsTaskbar();
            if (args.Contains("--self-test-app-polish")) return Tests.RunAppPolish();
            if (args.Contains("--self-test-monitor-shortcuts")) return Tests.RunMonitorShortcuts();
            if (args.Contains("--self-test-relative-move")) return Tests.RunRelativeMoveGeometry();
            if (args.Contains("--self-test-relative-move-native")) return Tests.RunRelativeMoveGeometry(true);
            if (args.Contains("--self-test-startup")) return Tests.RunStartupIntegration();
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            string name = AppProfile.EventName(sid);
            string mutexName = AppProfile.DesktopName(sid) + ".mutex";
            using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".stop"))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show"))
            {
                if (args.Contains("--inspect")) {
                    var displays = Native.Displays();
                    Console.WriteLine(new JavaScriptSerializer().Serialize(new { Displays = displays, Taskbars = Native.Bars(), ConfigPath = ConfigPath,
                        MonitorDpi = displays.Select(d => new { Device = d.Device, Dpi = WindowShelfManager.ReadDpi(d) }).ToArray() }));
                    return 0;
                }
                if (args.Contains("--restore")) {
                    stop.Set();
                    using (var recoveryMutex = new Mutex(false, mutexName)) {
                        bool acquired;
                        try { acquired = recoveryMutex.WaitOne(5000); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) { Console.WriteLine("The utility has not stopped yet. Retry restore."); return 1; }
                        try { return Recovery.Report(Recovery.Persisted()); }
                        finally { recoveryMutex.ReleaseMutex(); }
                    }
                }
                if (lifecycleCommand) return Recovery.Report(ApplicationLifecycle.Run(lifecycle, new LifecycleEnvironment(stop, mutexName)));
                bool created;
                using (var mutex = new Mutex(true, mutexName, out created))
                {
                    if (!created) {
                        if (!args.Contains("--startup")) show.Set();
                        // A test cannot signal a production instance. The shared desktop
                        // lock prevents concurrent taskbar management across profiles.
                        return 0;
                    }
                    try {
                        var recovery = Recovery.ForStartup();
                        if (recovery.Messages.Count != 0)
                            MessageBox.Show(String.Join(Environment.NewLine, recovery.Messages), "Taskbar Select recovery",
                                MessageBoxButtons.OK, recovery.Success ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
                        if (!recovery.Success) return 1;
                        if (AppProfile.AllowsStartup) Startup.MigrateLegacy();
                        stop.Reset(); show.Reset();
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new AppContext(stop, show, args.Contains("--startup")));
                        return Recovery.Report(Recovery.Persisted());
                    } catch (Exception error) {
                        MessageBox.Show(error.Message, "Taskbar Select", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    } finally {
                        try { Recovery.Report(Recovery.Persisted()); }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
            }
        }
    }
}
