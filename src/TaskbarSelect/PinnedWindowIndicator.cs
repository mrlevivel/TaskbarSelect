using System;
using System.Collections.Generic;
using System.Drawing;

namespace TaskbarSelect
{
    // The epoch belongs only to visual observation. It grants no pin or worker
    // authority, and changes only after a separately confirmed user pin action.
    internal sealed class PinnedWindowObservation
    {
        internal FocusedWindowSnapshot Identity;
        internal long Epoch;
        internal bool HelperOwned;
        internal PinnedWindowObservation Copy()
        { return new PinnedWindowObservation { Identity = Identity == null ? null : Identity.Copy(), Epoch = Epoch, HelperOwned = HelperOwned }; }
    }

    // One read-only, nonactivating surface per confirmed session pin. The
    // target's topmost state, placement and contents are never mutated here.
    internal sealed class PinnedWindowIndicator : IDisposable
    {
        sealed class Entry
        {
            internal PinnedWindowObservation Pin;
            internal IFocusIndicatorSurface Surface;
            internal IFocusIndicatorEvents Events;
            internal FocusIndicatorVisual Visual;
            internal FocusIndicatorTarget LastGeometry, Settling;
            internal int Revision;
            internal long QuietSince;
            internal bool Pending, MovingSignal, OwnMoveSize, Retired, Published;
        }
        const int RetiredLimit = 256;
        readonly FocusIndicatorApi api;
        readonly Func<IFocusIndicatorSurface> create;
        readonly Func<IntPtr, bool> ignored;
        readonly Dictionary<IntPtr, Entry> entries = new Dictionary<IntPtr, Entry>();
        readonly Dictionary<IntPtr, long> retiredEpochs = new Dictionary<IntPtr, long>();
        bool disposed, retirementOverflow;

        internal PinnedWindowIndicator(Func<IntPtr, bool> ignoreWindow = null, Func<IntPtr, IntPtr, bool> lowerFocusOverlay = null)
            : this(new FocusIndicatorApi(), delegate { return new FocusIndicatorSurface(); }, ignoreWindow)
        {
            create = delegate { return new FocusIndicatorSurface(animationsAllowed: delegate { return false; },
                current: PublicationCurrent, stopped: SurfaceStopped, lowerFocusOverlay: lowerFocusOverlay); };
        }
        internal PinnedWindowIndicator(FocusIndicatorApi api, Func<IFocusIndicatorSurface> create, Func<IntPtr, bool> ignoreWindow = null)
        {
            if (api == null || api.Read == null || api.Foreground == null || api.DesktopAllowed == null || api.Dark == null ||
                api.Now == null || api.PointerButtonDown == null || api.Watch == null) throw new ArgumentNullException("api");
            if (create == null) throw new ArgumentNullException("create");
            this.api = api; this.create = create; ignored = ignoreWindow ?? delegate { return false; };
        }
        internal string Error { get; private set; }
        internal int TrackedCount { get { return entries.Count; } }
        internal bool ReservesFocusCorner(FocusedWindowSnapshot target)
        {
            try {
                Entry entry;
                if (disposed || retirementOverflow || !WindowStatePolicy.Verified(target) || !WindowStatePolicy.Topmost(target) ||
                    !entries.TryGetValue(target.Window, out entry) || !Current(entry) || entry.Pending || entry.OwnMoveSize ||
                    !entry.Published || entry.Surface == null || entry.Surface.Window == IntPtr.Zero || entry.Visual == null || !entry.Visual.PinCorner ||
                    !WindowStatePolicy.SameIdentity(entry.Pin.Identity, target) || !api.DesktopAllowed()) return false;
                var current = Read(target.Window);
                return Eligible(entry, current) && !current.Moving && SameGeometry(entry.Visual.Target, current) &&
                    WindowStatePolicy.SameIdentity(target, current.Identity);
            } catch (Exception) { return false; }
        }
        internal IntPtr[] Handles
        {
            get {
                var result = new List<IntPtr>();
                foreach (var entry in entries.Values) if (entry.Surface != null && entry.Surface.Window != IntPtr.Zero) result.Add(entry.Surface.Window);
                return result.ToArray();
            }
        }
        internal void Update(PinnedWindowObservation[] pins, bool paused, FocusIndicatorVisual focusVisual = null, string accent = "Forest Green")
        {
            if (disposed) return;
            Error = null;
            try {
                if (paused || !api.DesktopAllowed()) { SuspendSurfaces(); return; }
                if (retirementOverflow) { ClearEntries(); Error = "Pinned-window indicator observation must be restarted."; return; }
                if (pins != null && pins.Length > OwnedWindowPins.Limit * 2) { RetireEntries(); Error = "Pinned-window indicator limit exceeded."; return; }
                var wanted = new Dictionary<IntPtr, PinnedWindowObservation>();
                var ambiguous = new HashSet<IntPtr>();
                foreach (var pin in pins ?? new PinnedWindowObservation[0]) {
                    if (pin == null || pin.Epoch <= 0 || !WindowStatePolicy.Verified(pin.Identity)) continue;
                    IntPtr window = pin.Identity.Window;
                    if (wanted.ContainsKey(window)) { ambiguous.Add(window); continue; }
                    wanted.Add(window, pin.Copy());
                }
                foreach (IntPtr window in ambiguous) wanted.Remove(window);
                foreach (var entry in new List<Entry>(entries.Values)) {
                    PinnedWindowObservation desired;
                    if (!wanted.TryGetValue(entry.Pin.Identity.Window, out desired)) { Retire(entry, true); continue; }
                    if (desired.Epoch != entry.Pin.Epoch || !WindowStatePolicy.SameIdentity(desired.Identity, entry.Pin.Identity)) Retire(entry, true);
                }
                foreach (var pin in wanted.Values) {
                    if (retirementOverflow) { ClearEntries(); Error = "Pinned-window indicator observation must be restarted."; return; }
                    if (!api.DesktopAllowed()) { SuspendSurfaces(); return; }
                    long retired;
                    if (retiredEpochs.TryGetValue(pin.Identity.Window, out retired)) {
                        if (pin.Epoch <= retired) continue;
                        retiredEpochs.Remove(pin.Identity.Window);
                    }
                    Entry entry;
                    if (!entries.TryGetValue(pin.Identity.Window, out entry)) {
                        entry = new Entry { Pin = pin.Copy() };
                        entries.Add(pin.Identity.Window, entry);
                    }
                    try { UpdateEntry(entry, focusVisual, accent); }
                    catch (FocusIndicatorGeometryChangedException) { if (Current(entry)) GeometryChanged(entry); }
                    catch (Exception) {
                        if (Current(entry)) HideSurface(entry);
                        Error = "A pinned-window indicator could not be updated safely.";
                    }
                }
                if (!api.DesktopAllowed()) SuspendSurfaces();
            } catch (Exception) {
                RetireEntries(); Error = "Pinned-window indicators are temporarily unavailable.";
            }
        }
        void UpdateEntry(Entry entry, FocusIndicatorVisual focusVisual, string accent)
        {
            if (!Current(entry)) return;
            var first = Read(entry.Pin.Identity.Window);
            if (!IdentityCurrent(entry, first)) { HideSurface(entry); return; }
            if (!Eligible(entry, first)) { BeginSettling(entry); return; }
            if (!Observe(entry, first.Identity)) return;
            int revision = entry.Revision;
            var current = Read(entry.Pin.Identity.Window);
            if (!IdentityCurrent(entry, current) || !Eligible(entry, current) || !api.DesktopAllowed()) { BeginSettling(entry); return; }
            if (!Current(entry) || entry.Revision != revision) { HideSurface(entry); return; }
            if (!SameGeometry(first, current) || (!entry.Pending && entry.LastGeometry != null && !SameGeometry(entry.LastGeometry, current))) GeometryChanged(entry);
            if (current.Moving || entry.OwnMoveSize) { BeginSettling(entry); return; }
            if (entry.MovingSignal && api.PointerButtonDown()) { HideSurface(entry); return; }
            if (entry.Pending) {
                long now = api.Now();
                if (entry.Settling == null || !SameGeometry(entry.Settling, current)) {
                    entry.Settling = current.Copy(); entry.QuietSince = now; HideSurface(entry); return;
                }
                if (now < entry.QuietSince || now - entry.QuietSince < 120) { HideSurface(entry); return; }
                entry.Pending = entry.MovingSignal = false; entry.Settling = null;
            }
            revision = entry.Revision;
            var visual = FocusIndicatorVisual.CreatePinCorner(current, api.Foreground() == current.Identity.Window, api.Dark(), accent, focusVisual);
            if (visual == null) { HideSurface(entry); return; }
            if (entry.Surface == null) {
                var added = create();
                if (!Current(entry) || entry.Revision != revision) { if (added != null) added.Dispose(); HideSurface(entry); return; }
                entry.Surface = added;
            }
            if (entry.Surface == null) throw new InvalidOperationException("Pinned-window surface unavailable.");
            entry.Visual = visual;
            entry.Surface.Present(visual);
            var after = Read(entry.Pin.Identity.Window);
            if (!Current(entry) || entry.Revision != revision) { HideSurface(entry); return; }
            if (!IdentityCurrent(entry, after) || !Eligible(entry, after) || !SameGeometry(current, after) ||
                !api.DesktopAllowed() || (api.Foreground() == current.Identity.Window) != visual.PinRailFocused) { GeometryChanged(entry); return; }
            if (!Object.ReferenceEquals(entry.Visual, visual)) return;
            if (entry.Surface == null || entry.Surface.Window == IntPtr.Zero) { HideSurface(entry); return; }
            entry.LastGeometry = current.Copy(); entry.Published = true;
        }
        FocusIndicatorTarget Read(IntPtr window)
        { var value = api.Read(window); return value == null ? null : value.Copy(); }
        bool IdentityCurrent(Entry entry, FocusIndicatorTarget target)
        { return target != null && WindowStatePolicy.SameIdentity(entry.Pin.Identity, target.Identity) && WindowStatePolicy.Topmost(target.Identity); }
        bool Eligible(Entry entry, FocusIndicatorTarget target)
        {
            return IdentityCurrent(entry, target) && WindowStatePolicy.Eligible(target.Identity, ignored) && !IsOwnSurface(target.Identity.Window) &&
                !target.Minimized && !target.Cloaked && !target.Fullscreen && !target.Menu && !target.ShellSurface &&
                !String.Equals(target.Identity.ClassName, "#32768", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(target.Identity.ClassName, "tooltips_class32", StringComparison.OrdinalIgnoreCase);
        }
        bool IsOwnSurface(IntPtr window)
        {
            foreach (var entry in entries.Values) if (entry.Surface != null && entry.Surface.Window == window) return true;
            return false;
        }
        bool Current(Entry entry)
        {
            Entry current;
            return !disposed && !entry.Retired && entries.TryGetValue(entry.Pin.Identity.Window, out current) && Object.ReferenceEquals(current, entry);
        }
        bool Observe(Entry entry, FocusedWindowSnapshot identity)
        {
            if (entry.Events != null) return true;
            var added = api.Watch(identity.Copy(), delegate(FocusIndicatorChange change) { Changed(entry, change); });
            if (added == null) throw new InvalidOperationException("Pinned-window events unavailable.");
            if (!Current(entry)) { added.Dispose(); return false; }
            entry.Events = added; return true;
        }
        void Changed(Entry entry, FocusIndicatorChange change)
        {
            if (!Current(entry) || change == null || change.Window != entry.Pin.Identity.Window || change.Child != 0 ||
                (change.Object != 0 && !(change.Kind == FocusIndicatorChangeKind.Location && change.Object == -4))) return;
            try {
                if (change.Kind == FocusIndicatorChangeKind.Destroyed) { Retire(entry, true); return; }
                if (change.Kind == FocusIndicatorChangeKind.MoveStart) entry.MovingSignal = true;
                if (change.Kind == FocusIndicatorChangeKind.MoveEnd) entry.MovingSignal = false;
                GeometryChanged(entry);
            } catch { if (Current(entry)) HideSurface(entry); Error = "Pinned-window geometry observation failed safely."; }
        }
        internal void ObserveMoveSize(IntPtr window, bool moving)
        {
            Entry entry;
            if (disposed || !entries.TryGetValue(window, out entry)) return;
            entry.OwnMoveSize = moving; GeometryChanged(entry);
        }
        void GeometryChanged(Entry entry)
        { entry.Revision++; BeginSettling(entry); }
        void BeginSettling(Entry entry)
        { entry.Pending = true; entry.Settling = null; entry.QuietSince = api.Now(); HideSurface(entry); }
        bool PublicationCurrent(FocusIndicatorVisual visual)
        {
            try {
                Entry entry;
                if (visual == null || visual.Target == null || visual.Target.Identity == null || !api.DesktopAllowed() ||
                    !entries.TryGetValue(visual.Target.Identity.Window, out entry) || !Current(entry) || !Object.ReferenceEquals(entry.Visual, visual)) return false;
                var current = Read(entry.Pin.Identity.Window);
                return Eligible(entry, current) && !current.Moving && !entry.OwnMoveSize && SameGeometry(visual.Target, current) &&
                    (api.Foreground() == current.Identity.Window) == visual.PinRailFocused;
            } catch { return false; }
        }
        void SurfaceStopped(IFocusIndicatorSurface surface, Exception failure)
        {
            foreach (var entry in new List<Entry>(entries.Values)) if (Object.ReferenceEquals(entry.Surface, surface)) {
                if (failure is FocusIndicatorGeometryChangedException) GeometryChanged(entry); else HideSurface(entry);
                if (failure != null && !(failure is FocusIndicatorGeometryChangedException)) Error = "A pinned-window indicator stopped safely.";
                break;
            }
        }
        static bool SameGeometry(FocusIndicatorTarget a, FocusIndicatorTarget b)
        {
            if (a == null || b == null || !WindowStatePolicy.SameIdentity(a.Identity, b.Identity) || a.Frame != b.Frame || a.Dpi != b.Dpi ||
                a.Corner != b.Corner || a.Monitors == null || b.Monitors == null || a.Monitors.Length != b.Monitors.Length) return false;
            for (int i = 0; i < a.Monitors.Length; i++) if (a.Monitors[i] != b.Monitors[i]) return false;
            return true;
        }
        void HideSurface(Entry entry)
        {
            var previous = entry.Surface; entry.Surface = null; entry.Visual = null; entry.Published = false;
            if (previous != null) try { previous.Dispose(); } catch { }
        }
        void Retire(Entry entry, bool remember)
        {
            if (!Current(entry)) return;
            if (remember) {
                long prior;
                if (retiredEpochs.TryGetValue(entry.Pin.Identity.Window, out prior)) retiredEpochs[entry.Pin.Identity.Window] = Math.Max(prior, entry.Pin.Epoch);
                else if (retiredEpochs.Count < RetiredLimit) retiredEpochs.Add(entry.Pin.Identity.Window, entry.Pin.Epoch);
                else retirementOverflow = true;
            }
            entry.Retired = true; entries.Remove(entry.Pin.Identity.Window); entry.Revision++;
            var previous = entry.Events; entry.Events = null;
            if (previous != null) try { previous.Dispose(); } catch { }
            HideSurface(entry);
            if (retirementOverflow) ClearEntries();
        }
        void ClearEntries()
        {
            foreach (var entry in new List<Entry>(entries.Values)) {
                entry.Retired = true; entry.Revision++;
                var previous = entry.Events; entry.Events = null;
                if (previous != null) try { previous.Dispose(); } catch { }
                HideSurface(entry);
            }
            entries.Clear();
        }
        void RetireEntries()
        { foreach (var entry in new List<Entry>(entries.Values)) Retire(entry, true); }
        void SuspendSurfaces()
        {
            // Keep already-scoped lifetime watches while drawing is paused.
            // Otherwise a destroyed HWND could be reused with identical process
            // metadata during the gap and inherit an unchanged visual epoch.
            foreach (var entry in new List<Entry>(entries.Values)) {
                if (entry.Events == null) { Retire(entry, true); continue; }
                entry.Revision++; BeginSettling(entry);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            ClearEntries(); retiredEpochs.Clear(); disposed = true;
        }
    }
}
