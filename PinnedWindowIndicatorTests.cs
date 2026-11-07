using System;
using System.Collections.Generic;
using System.Drawing;

namespace TaskbarSelect
{
    internal static class PinnedWindowIndicatorTests
    {
        sealed class Surface : IFocusIndicatorSurface
        {
            internal readonly IntPtr Handle;
            internal int Presents, Disposals;
            internal FocusIndicatorVisual Last;
            internal Action<FocusIndicatorVisual> DuringPresent;
            internal bool Fail;
            internal Surface(int handle) { Handle = new IntPtr(handle); }
            public IntPtr Window { get { return Disposals == 0 ? Handle : IntPtr.Zero; } }
            public void Present(FocusIndicatorVisual visual)
            { Presents++; Last = visual; if (DuringPresent != null) DuringPresent(visual); if (Fail) throw new InvalidOperationException("Owned inert fixture failure."); }
            public void Dispose() { Disposals++; }
        }
        sealed class Events : IFocusIndicatorEvents
        {
            internal readonly IntPtr Window;
            internal readonly Action<FocusIndicatorChange> Changed;
            internal int Disposals;
            internal Events(IntPtr window, Action<FocusIndicatorChange> changed) { Window = window; Changed = changed; }
            internal void Emit(FocusIndicatorChangeKind kind, int objectId = 0, int child = 0)
            { Changed(new FocusIndicatorChange { Window = Window, Kind = kind, Object = objectId, Child = child }); }
            public void Dispose() { Disposals++; }
        }
        sealed class Fixture
        {
            internal readonly Dictionary<IntPtr, FocusIndicatorTarget> Targets = new Dictionary<IntPtr, FocusIndicatorTarget>();
            internal readonly List<Surface> Surfaces = new List<Surface>();
            internal readonly List<Events> Observers = new List<Events>();
            internal readonly Dictionary<IntPtr, int> Reads = new Dictionary<IntPtr, int>();
            internal IntPtr Foreground = new IntPtr(42);
            internal bool Desktop = true, PointerDown, Ignore, FailFirst, DesktopReadFails;
            internal long Clock = 1000;
            internal Action<IntPtr, int> DuringRead;
            internal Action<Events> DuringWatch;
            internal Action DuringCreate;
            internal Action<FocusIndicatorVisual> DuringPresent;
            internal readonly PinnedWindowIndicator Indicator;
            internal Fixture()
            {
                Targets.Add(new IntPtr(42), Target(42)); Targets.Add(new IntPtr(43), Target(43));
                var api = new FocusIndicatorApi {
                    Foreground = delegate { return Foreground; }, DesktopAllowed = delegate { if (DesktopReadFails) throw new InvalidOperationException("Inert desktop read failed."); return Desktop; },
                    Dark = delegate { return true; }, Now = delegate { return Clock; }, PointerButtonDown = delegate { return PointerDown; },
                    Read = delegate(IntPtr window) {
                        int count; Reads.TryGetValue(window, out count); Reads[window] = ++count;
                        if (DuringRead != null) DuringRead(window, count);
                        FocusIndicatorTarget value; return Targets.TryGetValue(window, out value) ? value : null;
                    },
                    Watch = delegate(FocusedWindowSnapshot identity, Action<FocusIndicatorChange> changed) {
                        var observer = new Events(identity.Window, changed); Observers.Add(observer);
                        if (DuringWatch != null) DuringWatch(observer); return observer;
                    }
                };
                Indicator = new PinnedWindowIndicator(api, delegate {
                    var surface = new Surface(700 + Surfaces.Count); surface.Fail = FailFirst && Surfaces.Count == 0;
                    surface.DuringPresent = delegate(FocusIndicatorVisual visual) { if (DuringPresent != null) DuringPresent(visual); };
                    Surfaces.Add(surface); if (DuringCreate != null) DuringCreate(); return surface;
                }, delegate { return Ignore; });
            }
            internal PinnedWindowObservation Pin(int window = 42, long epoch = 1)
            { return new PinnedWindowObservation { Identity = Targets[new IntPtr(window)].Identity.Copy(), Epoch = epoch }; }
            internal void Update(params PinnedWindowObservation[] pins) { Indicator.Update(pins, false); }
        }
        static FocusIndicatorTarget Target(int window)
        {
            return new FocusIndicatorTarget { Identity = new FocusedWindowSnapshot { Window = new IntPtr(window), Root = new IntPtr(window),
                Process = 7, Thread = 8, ProcessStarted = 99, Visible = true, ExtendedStyle = 8,
                ClassName = "Neutral application", ProcessImage = @"C:\Neutral\Demo.exe", Device = "DISPLAY2" },
                Frame = new Rectangle(100 + window, 100, 800, 600), Monitors = new[] { new Rectangle(0, 0, 1920, 1080) }, Dpi = 96, Corner = 8 };
        }
        internal static void Run(Action<bool, string> check)
        { Lifecycle(check); AccentAndCornerCoordination(check); ReservedFocusObservation(check); Eligibility(check); IdentityAndEpoch(check); Moving(check); Races(check); }
        static void Lifecycle(Action<bool, string> check)
        {
            var f = new Fixture(); var a = f.Pin(); var b = f.Pin(43, 2); f.Foreground = new IntPtr(99); f.Update(a, b);
            check(f.Indicator.Handles.Length == 2 && f.Indicator.TrackedCount == 2 && f.Observers.Count == 2,
                "Two confirmed session pins each receive a separately observed rail while unfocused");
            check(f.Surfaces[0].Last.PinRail && f.Surfaces[0].Last.PinCorner && !f.Surfaces[0].Last.PinRailFocused && !f.Surfaces[1].Last.PinRailFocused,
                "An unfocused pinned window retains its single lit corner without inheriting foreground focus");
            var handles = f.Indicator.Handles; handles[0] = IntPtr.Zero;
            check(f.Indicator.Handles[0] != IntPtr.Zero, "Returned rail handles are a defensive snapshot");
            f.Update(a, b);
            check(f.Surfaces.Count == 2 && f.Observers.Count == 2 && f.Surfaces[0].Presents == 2,
                "A stable pin reuses its surface, renderer appearance key and scoped observer");
            f.Foreground = a.Identity.Window;
            var focus = FocusIndicatorVisual.Create(f.Targets[a.Identity.Window], FocusIndicatorMode.InsetEdge, "Forest Green", true);
            f.Indicator.Update(new[] { a, b }, false, focus);
            check(f.Surfaces[0].Last.PinRailFocused && f.Surfaces[0].Last.PinCorner && f.Surfaces[0].Last.PinRailInset == 0 && f.Surfaces[1].Last.PinRailInset == 0,
                "The focused lit corner shares the native perimeter instead of becoming an inward parallel bracket");
            f.Foreground = b.Identity.Window;
            focus = FocusIndicatorVisual.Create(f.Targets[b.Identity.Window], FocusIndicatorMode.InsetEdge, "Forest Green", true, false, 0, 1);
            f.Indicator.Update(new[] { a, b }, false, focus);
            check(!f.Surfaces[0].Last.PinRailFocused && f.Surfaces[0].Last.PinRailInset == 0 &&
                f.Surfaces[1].Last.PinRailFocused && f.Surfaces[1].Last.PinCorner && f.Surfaces[1].Last.PinRailInset == 0,
                "Changing focus preserves both corner cues on their original perimeter across focus finishes");
            f.Indicator.Update(new[] { a, b }, true);
            check(f.Indicator.Handles.Length == 0 && f.Surfaces[0].Disposals == 1 && f.Observers[0].Disposals == 0,
                "Pause all removes every rail while retaining scoped lifetime observation");
            f.Update(a, b); f.Clock += 121; f.Update(a, b);
            check(f.Indicator.Handles.Length == 2, "Unpausing can redisplay the same still-confirmed pin epochs after geometry settles");
            f.Desktop = false; f.Update(a, b);
            check(f.Indicator.Handles.Length == 0 && f.Indicator.TrackedCount == 2 && f.Observers[0].Disposals == 0,
                "An inaccessible desktop removes visuals while preserving lifetime guards");
            f.Desktop = true; f.Update(a, b); f.Clock += 121; f.Update(a, b); f.Update(b);
            check(f.Indicator.Handles.Length == 1 && f.Indicator.TrackedCount == 1, "Removing one source pin immediately removes only its indicator");
            f.Indicator.Dispose(); f.Update(a, b);
            check(f.Indicator.Handles.Length == 0 && f.Indicator.TrackedCount == 0, "Disposed pin observation cannot recreate any rail");
        }
        static void AccentAndCornerCoordination(Action<bool, string> check)
        {
            var f = new Fixture(); var pin = f.Pin(); f.Foreground = new IntPtr(99); f.Update(pin);
            var normal = f.Surfaces[0].Last;
            check(normal.PinCorner && normal.PinRailInset == 0 && normal.AccentColor == FocusAppearance.Accent("Forest Green", true),
                "The isolated corner defaults to Forest Green without changing pin ownership");
            f.Indicator.Update(new[] { pin }, false, null, "Coral"); var coral = f.Surfaces[0].Last;
            check(coral.PinCorner && coral.AccentColor == FocusAppearance.Accent("Coral", true) && coral.AccentColor != normal.AccentColor &&
                f.Surfaces.Count == 1 && f.Observers.Count == 1,
                "A selected accent reaches the existing single-corner surface without replacing lifetime observation");
            f.Indicator.Update(new[] { pin }, false, null, "Coral");
            check(f.Surfaces[0].Last.SameAppearance(coral) && f.Surfaces.Count == 1,
                "Repeated static corner updates retain an identical renderer appearance key");
            f.Foreground = pin.Identity.Window;
            var focus = FocusIndicatorVisual.Create(f.Targets[pin.Identity.Window], FocusIndicatorMode.InsetEdge, "Coral", true, false, 3, 0);
            f.Indicator.Update(new[] { pin }, false, focus, "Coral"); var focused = f.Surfaces[0].Last;
            check(focused.PinRailFocused && focused.PinCorner && focused.PinRailInset == 0 && focused.Target.Frame == coral.Target.Frame &&
                focused.Bounds == coral.Bounds && focused.Thickness == coral.Thickness && focused.Outer == coral.Outer &&
                focused.Inner == coral.Inner && focused.GlowColor == coral.GlowColor && focused.GlowWidth == coral.GlowWidth,
                "Focus lighting does not move, resize, recolor or brighten the pinned corner cue");
            f.Foreground = new IntPtr(99); f.Indicator.Update(new[] { pin }, false, focus, "Coral"); var unfocused = f.Surfaces[0].Last;
            check(!unfocused.PinRailFocused && unfocused.Bounds == focused.Bounds && unfocused.Outer == focused.Outer &&
                unfocused.GlowColor == focused.GlowColor && f.Indicator.Handles.Length == 1,
                "Losing focus leaves the same lit corner visible at the same brightness");
            check(normal.Bounds.Width < normal.Target.Frame.Width && normal.Bounds.Height < normal.Target.Frame.Height,
                "The pin surface is a bounded upper-left corner crop rather than a full-width top rail");
            f.Indicator.Dispose();
        }
        static void ReservedFocusObservation(Action<bool, string> check)
        {
            var f = new Fixture(); var pin = f.Pin(); var target = f.Targets[pin.Identity.Window].Identity.Copy();
            check(!f.Indicator.ReservesFocusCorner(target), "A confirmed pin without a visible corner does not quiet normal focus lighting");
            bool reentered = false, reservedBeforeFirstUpload = false;
            f.DuringPresent = delegate {
                reentered = true; reservedBeforeFirstUpload = f.Indicator.ReservesFocusCorner(target);
            };
            f.Update(pin);
            f.DuringPresent = null;
            check(reentered && !reservedBeforeFirstUpload,
                "Reentrant first presentation cannot reserve focus lighting before the corner publication is confirmed");
            check(f.Indicator.ReservesFocusCorner(target), "An active exact-epoch corner reserves focus lighting only for its retained target");
            check(!f.Indicator.ReservesFocusCorner(f.Targets[new IntPtr(43)].Identity),
                "An unrelated topmost foreground cannot inherit another pin's reserved corner");
            var changed = target.Copy(); changed.ProcessStarted++;
            check(!f.Indicator.ReservesFocusCorner(changed), "Reserved lighting rejects a changed target process lifetime");
            f.Observers[0].Emit(FocusIndicatorChangeKind.MoveStart);
            check(!f.Indicator.ReservesFocusCorner(target), "A hidden moving corner cannot continue suppressing focus lighting");
            f.Observers[0].Emit(FocusIndicatorChangeKind.MoveEnd); f.Update(pin); f.Clock += 121; f.Update(pin);
            check(f.Indicator.ReservesFocusCorner(target), "The settled visible corner can resume exact-target lighting reservation");
            f.Observers[0].Emit(FocusIndicatorChangeKind.Destroyed); f.Update(pin);
            check(!f.Indicator.ReservesFocusCorner(target), "A destroyed epoch cannot reserve lighting even when identical HWND metadata is reused");
            f.Update(f.Pin(42, 2));
            check(f.Indicator.ReservesFocusCorner(target), "A fresh confirmed epoch restores reservation after a replacement corner is visible");
            f.Indicator.Update(new[] { f.Pin(42, 2) }, true);
            check(!f.Indicator.ReservesFocusCorner(target), "Paused corner drawing never leaves a reserved focus-lighting gap");
            f.Indicator.Dispose();
            check(!f.Indicator.ReservesFocusCorner(target), "Disposed corner observation cannot reserve lighting");
        }
        static void Eligibility(Action<bool, string> check)
        {
            var mutations = new Action<FocusIndicatorTarget>[] {
                delegate(FocusIndicatorTarget t) { t.Identity.ExtendedStyle = 0; },
                delegate(FocusIndicatorTarget t) { t.Identity.Visible = false; },
                delegate(FocusIndicatorTarget t) { t.Minimized = true; }, delegate(FocusIndicatorTarget t) { t.Cloaked = true; },
                delegate(FocusIndicatorTarget t) { t.Fullscreen = true; }, delegate(FocusIndicatorTarget t) { t.Menu = true; },
                delegate(FocusIndicatorTarget t) { t.ShellSurface = true; }, delegate(FocusIndicatorTarget t) { t.Identity.Shell = true; },
                delegate(FocusIndicatorTarget t) { t.Identity.ExtendedStyle |= 0x80; },
                delegate(FocusIndicatorTarget t) { t.Identity.ClassName = "#32768"; },
                delegate(FocusIndicatorTarget t) { t.Identity.ClassName = "tooltips_class32"; },
                delegate(FocusIndicatorTarget t) { t.Identity.Error = 5; }
            };
            foreach (var mutate in mutations) {
                var f = new Fixture(); var pin = f.Pin(); mutate(f.Targets[pin.Identity.Window]); f.Update(pin);
                check(f.Indicator.Handles.Length == 0 && f.Surfaces.Count == 0,
                    "A non-topmost, inaccessible, hidden, cloaked, fullscreen, shell or transient target never receives a rail");
                f.Indicator.Dispose();
            }
            var ignored = new Fixture(); ignored.Ignore = true; ignored.Update(ignored.Pin());
            check(ignored.Surfaces.Count == 0, "Ignored app-owned transient windows cannot receive pin indicators"); ignored.Indicator.Dispose();
            var invalid = new Fixture(); var noEpoch = invalid.Pin(); noEpoch.Epoch = 0; invalid.Update(noEpoch, null);
            check(invalid.Surfaces.Count == 0 && invalid.Indicator.TrackedCount == 0, "Missing observations and nonpositive epochs are not visual pin claims"); invalid.Indicator.Dispose();
            var over = new Fixture(); var pins = new PinnedWindowObservation[OwnedWindowPins.Limit * 2 + 1];
            for (int i = 0; i < pins.Length; i++) pins[i] = over.Pin(); over.Update(pins);
            check(over.Surfaces.Count == 0 && over.Indicator.Error != null, "The observation input is bounded before native target reads"); over.Indicator.Dispose();
            var unrelated = new Fixture(); unrelated.Update(unrelated.Pin());
            check(unrelated.Reads.Count == 1 && !unrelated.Reads.ContainsKey(new IntPtr(43)),
                "Only confirmed pin HWNDs are read; other topmost windows are never enumerated"); unrelated.Indicator.Dispose();
        }
        static void IdentityAndEpoch(Action<bool, string> check)
        {
            var stale = new Fixture(); var pin = stale.Pin(); stale.Targets[pin.Identity.Window].Identity.ProcessStarted++;
            stale.Update(pin); check(stale.Surfaces.Count == 0, "A reused HWND with a different process lifetime cannot inherit the old pin rail"); stale.Indicator.Dispose();
            var race = new Fixture(); pin = race.Pin(); race.DuringRead = delegate(IntPtr window, int count) { if (count == 2) race.Targets[window].Identity.Thread++; };
            race.Update(pin); check(race.Surfaces.Count == 0 && race.Indicator.Handles.Length == 0,
                "Changed retained identity between independent reads blocks presentation"); race.Indicator.Dispose();
            var destroyed = new Fixture(); pin = destroyed.Pin(); destroyed.Update(pin); var oldEvents = destroyed.Observers[0];
            oldEvents.Emit(FocusIndicatorChangeKind.Destroyed);
            check(destroyed.Indicator.Handles.Length == 0 && oldEvents.Disposals == 1, "Destroyed immediately retires the matching rail and scoped observer");
            destroyed.Update(pin);
            check(destroyed.Surfaces.Count == 1 && destroyed.Indicator.Handles.Length == 0,
                "Even identical process/thread/class/start HWND reuse cannot revive a destroyed pin epoch");
            var newPin = destroyed.Pin(42, 2); destroyed.Update(newPin);
            check(destroyed.Indicator.Handles.Length == 1 && destroyed.Surfaces.Count == 2, "A separately confirmed newer pin epoch may create a fresh rail");
            oldEvents.Emit(FocusIndicatorChangeKind.Destroyed); oldEvents.Emit(FocusIndicatorChangeKind.Location);
            check(destroyed.Indicator.Handles.Length == 1 && destroyed.Surfaces[1].Disposals == 0,
                "Late callbacks from the destroyed epoch cannot hide its confirmed successor"); destroyed.Indicator.Dispose();
            foreach (bool secureDesktop in new[] { false, true }) {
                var paused = new Fixture(); var original = paused.Pin(); paused.Update(original);
                if (secureDesktop) paused.Desktop = false;
                paused.Indicator.Update(new[] { original }, !secureDesktop);
                check(paused.Indicator.Handles.Length == 0 && paused.Observers[0].Disposals == 0,
                    "Paused or inaccessible drawing preserves the existing scoped destruction watch");
                paused.Observers[0].Emit(FocusIndicatorChangeKind.Destroyed);
                paused.Desktop = true; paused.Update(original); paused.Clock += 150; paused.Update(original);
                check(paused.Indicator.Handles.Length == 0 && paused.Surfaces.Count == 1,
                    "Same-process identical HWND reuse during paused drawing cannot revive a destroyed epoch");
                paused.Update(paused.Pin(42, 2));
                check(paused.Indicator.Handles.Length == 1, "Only a fresh confirmed epoch resumes a reused HWND after paused destruction");
                paused.Indicator.Dispose();
            }
            var duplicate = new Fixture(); pin = duplicate.Pin(); duplicate.Update(pin, pin.Copy());
            check(duplicate.Surfaces.Count == 0, "Duplicate HWND source observations are ambiguous and produce no overlay"); duplicate.Indicator.Dispose();
            var defensive = new Fixture(); pin = defensive.Pin(); defensive.Update(pin); pin.Identity.Process++;
            defensive.Observers[0].Emit(FocusIndicatorChangeKind.Location, -4); defensive.Clock += 150; defensive.Update(defensive.Pin());
            defensive.Clock += 150; defensive.Update(defensive.Pin());
            check(defensive.Indicator.Handles.Length == 1, "Caller mutation cannot change the controller's retained pin identity copy"); defensive.Indicator.Dispose();
        }
        static void Moving(Action<bool, string> check)
        {
            var f = new Fixture(); var a = f.Pin(); var b = f.Pin(43, 2); f.Update(a, b);
            var first = f.Surfaces[0]; var second = f.Surfaces[1]; var observer = f.Observers[0];
            observer.Emit(FocusIndicatorChangeKind.MoveStart);
            check(first.Disposals == 1 && second.Disposals == 0 && f.Indicator.Handles.Length == 1,
                "Dragging one pinned window hides its own rail without disturbing a second pin");
            f.PointerDown = true; f.Update(a, b);
            check(f.Surfaces.Count == 2, "A live pointer-down move signal cannot recreate a detached rail");
            f.Targets[a.Identity.Window].Frame.Offset(80, 30); f.PointerDown = false; observer.Emit(FocusIndicatorChangeKind.MoveEnd);
            f.Update(a, b); check(f.Surfaces.Count == 2, "Move completion waits for a newly observed stable rectangle");
            f.Clock += 119; f.Update(a, b); check(f.Surfaces.Count == 2, "A rail remains hidden before the full geometry settling interval");
            f.Clock++; f.Update(a, b);
            check(f.Surfaces.Count == 3 && f.Indicator.Handles.Length == 2 && f.Surfaces[2].Last.Target.Frame == f.Targets[a.Identity.Window].Frame,
                "The settled rail reappears at the verified new frame with no stale geometry");
            observer.Emit(FocusIndicatorChangeKind.Hidden);
            check(f.Indicator.Handles.Length == 1, "A Hidden event removes the pin cue immediately");
            f.Indicator.ObserveMoveSize(b.Identity.Window, true); f.Update(a, b);
            check(f.Indicator.Handles.Length == 0, "The app's own move-size notification suppresses its matching pin rail");
            f.Indicator.ObserveMoveSize(b.Identity.Window, false); f.Update(a, b); f.Clock += 121; f.Update(a, b);
            check(f.Indicator.Handles.Length == 2, "Both independently settled pin rails recover after movement ends");
            int surfaces = f.Surfaces.Count; observer.Emit(FocusIndicatorChangeKind.Destroyed, 0, 1);
            check(f.Indicator.Handles.Length == 2 && f.Surfaces.Count == surfaces, "Child-object events cannot retire a top-level pin epoch"); f.Indicator.Dispose();
        }
        static void Races(Action<bool, string> check)
        {
            var watch = new Fixture(); watch.DuringWatch = delegate(Events observer) { observer.Emit(FocusIndicatorChangeKind.Destroyed); };
            watch.Update(watch.Pin());
            check(watch.Surfaces.Count == 0 && watch.Observers[0].Disposals == 1, "Destruction during watch registration cannot publish or leak an observer"); watch.Indicator.Dispose();
            var create = new Fixture(); create.DuringCreate = delegate { create.Indicator.Update(new[] { create.Pin() }, true); };
            create.Update(create.Pin());
            check(create.Indicator.Handles.Length == 0 && create.Surfaces[0].Disposals == 1, "Pause during surface creation disposes the unpublished surface"); create.Indicator.Dispose();
            var show = new Fixture(); show.DuringPresent = delegate { show.Observers[0].Emit(FocusIndicatorChangeKind.Destroyed); };
            show.Update(show.Pin());
            check(show.Indicator.Handles.Length == 0 && show.Surfaces[0].Disposals == 1, "A target destroyed during presentation leaves no visible old-epoch rail"); show.Indicator.Dispose();
            var changed = new Fixture(); changed.DuringPresent = delegate(FocusIndicatorVisual visual) { changed.Targets[visual.Target.Identity.Window].Frame.Offset(3, 0); };
            changed.Update(changed.Pin());
            check(changed.Indicator.Handles.Length == 0, "Geometry changing during presentation is hidden before the next controller update"); changed.Indicator.Dispose();
            var isolated = new Fixture(); isolated.FailFirst = true; isolated.Update(isolated.Pin(), isolated.Pin(43, 2));
            check(isolated.Indicator.Handles.Length == 1 && isolated.Surfaces[0].Disposals == 1 && isolated.Surfaces[1].Disposals == 0 && isolated.Indicator.Error != null,
                "A failing pin surface is isolated while another pinned window remains correctly marked"); isolated.Indicator.Dispose();
            var read = new Fixture(); read.DuringRead = delegate(IntPtr window, int count) { if (window == new IntPtr(42)) throw new InvalidOperationException("Inert read unavailable."); };
            read.Update(read.Pin(), read.Pin(43, 2));
            check(read.Indicator.Handles.Length == 1 && read.Indicator.Error != null, "One unreadable pinned target does not disable another valid pin"); read.Indicator.Dispose();
            var global = new Fixture(); var old = global.Pin(); global.Update(old); global.DesktopReadFails = true; global.Update(old);
            check(global.Indicator.Handles.Length == 0 && global.Observers[0].Disposals == 1,
                "An unknown global observation failure retires epochs before their lifetime watches detach");
            global.DesktopReadFails = false; global.Update(old);
            check(global.Indicator.Handles.Length == 0, "An old epoch cannot recreate a rail across an unobserved global failure gap");
            global.Update(global.Pin(42, 2));
            check(global.Indicator.Handles.Length == 1, "Fresh confirmed pin authority can resume visual observation after a global failure"); global.Indicator.Dispose();
        }
    }
}
