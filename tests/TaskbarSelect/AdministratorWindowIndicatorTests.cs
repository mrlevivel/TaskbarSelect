using System;
using System.Collections.Generic;
using System.Drawing;

namespace TaskbarSelect
{
    internal static class AdministratorWindowIndicatorTests
    {
        sealed class Surface : IFocusIndicatorSurface
        {
            internal IntPtr Handle;
            internal FocusIndicatorVisual Last;
            internal Action DuringPresent;
            internal bool Fail, Disposed;
            public IntPtr Window { get { return Disposed ? IntPtr.Zero : Handle; } }
            public void Present(FocusIndicatorVisual visual)
            {
                Last = visual.Copy();
                if (DuringPresent != null) DuringPresent();
                if (Fail) throw new InvalidOperationException("Inert helper publication failure.");
            }
            public void Dispose() { Disposed = true; }
        }
        sealed class Events : IFocusIndicatorEvents
        {
            public void Dispose() { }
        }
        sealed class Fixture : IDisposable
        {
            internal readonly WindowStateActionsTests.Fixture Windows = new WindowStateActionsTests.Fixture();
            internal readonly List<string> Publications = new List<string>();
            internal readonly List<Surface> Corners = new List<Surface>();
            internal readonly List<Surface> Frames = new List<Surface>();
            internal readonly OwnedWindowPins Owned;
            internal readonly ReservedPinFocus Confirmed;
            internal readonly FocusIndicator Focus;
            internal readonly PinnedWindowIndicator Pins;
            internal readonly AdministratorWindowIndicators Indicators;
            internal bool MarkerUnavailable, FailCorner, TargetAllowed = true;
            internal long Clock = 1000;
            internal Action DuringCorner;
            internal Fixture()
            {
                Owned = new OwnedWindowPins(new OwnedWindowPinApi {
                    Read = Windows.Api.Read, Exists = Windows.Api.Exists, SetTopmost = Windows.Api.CleanupTopmost,
                    SetMarker = Windows.Api.SetMarker, RemoveMarker = Windows.Api.RemoveMarker, Now = Windows.Api.Now,
                    Pause = Windows.Api.Pause, Quiescent = Windows.Api.Quiescent,
                    ReadMarker = delegate(IntPtr window, string marker) {
                        return MarkerUnavailable ? IntPtr.Zero : Windows.Api.ReadMarker(window, marker);
                    }
                });
                Confirmed = new ReservedPinFocus(Windows.Api.Read, Owned.MatchesPinnedObservation, Windows.Api.Exists);
                var api = new FocusIndicatorApi {
                    Foreground = delegate { return Windows.Foreground; }, DesktopAllowed = delegate { return true; },
                    Dark = delegate { return true; }, GlowAllowed = delegate { return true; },
                    Now = delegate { return Clock; }, PointerButtonDown = delegate { return false; },
                    Read = delegate(IntPtr window) {
                        var identity = Windows.Api.Read(window);
                        return identity == null ? null : new FocusIndicatorTarget { Identity = identity.Copy(),
                            Frame = new Rectangle(100, 100, 800, 600), Dpi = 96, Corner = 8,
                            Monitors = new[] { new Rectangle(0, 0, 1920, 1080) } };
                    },
                    Watch = delegate { return new Events(); }
                };
                Focus = new FocusIndicator(api, delegate {
                    var surface = new Surface { Handle = new IntPtr(900 + Frames.Count),
                        DuringPresent = delegate { Publications.Add("focus"); } };
                    Frames.Add(surface); return surface;
                }, delegate { return !TargetAllowed; });
                Pins = new PinnedWindowIndicator(api, delegate {
                    var surface = new Surface { Handle = new IntPtr(700 + Corners.Count), Fail = FailCorner,
                        DuringPresent = delegate { Publications.Add("pin"); if (DuringCorner != null) DuringCorner(); } };
                    Corners.Add(surface); return surface;
                }, delegate(IntPtr window) { return !TargetAllowed || !Confirmed.Matches(Windows.Api.Read(window)); });
                Indicators = new AdministratorWindowIndicators(Focus, Pins, Confirmed);
            }
            internal void Pin()
            {
                var expected = Windows.Window.Copy(); Owned.Remember(expected); Windows.Style(expected.Window, true);
                Confirmed.RecordResult(expected, true, new WindowMoveWorker.Result {
                    Outcome = WindowMoveWorker.Outcome.Success, CallerExited = true, MutationAttempted = true });
            }
            internal void Update(bool focusPaused = false, bool pinsPaused = false, int style = 2)
            { Indicators.Update(new[] { style, 0, focusPaused ? 1 : 0, 0, 3, 0, pinsPaused ? 1 : 0 }); }
            public void Dispose() { Indicators.Dispose(); }
        }
        internal static void Run(Action<bool, string> check)
        { Publication(check); Failure(check); Pauses(check); Lifetimes(check); Races(check); }
        static void Publication(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                f.Windows.Style(f.Windows.Foreground, true); f.Update();
                check(f.Corners.Count == 0 && f.Focus.CurrentVisual != null && !f.Focus.CurrentVisual.ReservedPinCorner,
                    "Unowned HIGH topmost windows retain ordinary focus lighting without a claimed pin cue");
            }
            using (var f = new Fixture()) {
                f.Pin(); f.Update();
                check(f.Publications.Count == 2 && f.Publications[0] == "pin" && f.Publications[1] == "focus" &&
                    f.Corners.Count == 1 && f.Corners[0].Last.PinCorner && f.Focus.CurrentVisual.ReservedPinCorner,
                    "The helper publishes its confirmed HIGH corner before reserving matching focus-frame lighting");
                var other = f.Windows.Window.Copy(); other.Window = other.Root = new IntPtr(43);
                f.Windows.Windows.Add(other.Window, other); f.Windows.Foreground = other.Window; f.Update();
                check(f.Corners.Count == 1 && !f.Corners[0].Last.PinRailFocused && f.Focus.CurrentVisual != null &&
                    f.Focus.CurrentVisual.Target.Identity.Window == other.Window && !f.Focus.CurrentVisual.ReservedPinCorner,
                    "An unfocused HIGH pin retains its helper corner while another HIGH window gets unreserved focus lighting");
                check(f.Windows.WorkerCalls == 0 && f.Windows.CleanupCalls == 0 && f.Owned.Count == 1,
                    "The helper's presentation coordinator never issues a pin worker or consumes cleanup ownership");
            }
        }
        static void Failure(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                f.Pin(); f.FailCorner = true; f.Update();
                check(f.Corners.Count == 1 && f.Corners[0].Disposed && f.Pins.Handles.Length == 0 &&
                    f.Focus.CurrentVisual != null && !f.Focus.CurrentVisual.ReservedPinCorner,
                    "A failed helper corner publication leaves the focused HIGH corner illuminated by the ordinary frame");
                f.FailCorner = false; f.Update();
                check(f.Corners.Count == 2 && !f.Corners[1].Disposed && f.Focus.CurrentVisual.ReservedPinCorner,
                    "A failed helper surface can recover against the same confirmed pin without a fresh window mutation");
            }
            using (var f = new Fixture()) {
                f.Pin(); f.MarkerUnavailable = true; f.Update();
                check(f.Pins.Handles.Length == 0 && f.Focus.CurrentVisual != null && !f.Focus.CurrentVisual.ReservedPinCorner &&
                    f.Owned.Count == 1 && f.Confirmed.Count == 1,
                    "Missing HIGH lifetime-marker evidence hides the cue and preserves full focus lighting and cleanup metadata");
                f.MarkerUnavailable = false; f.Update(); f.Clock += 121; f.Update();
                check(f.Pins.Handles.Length == 1 && f.Focus.CurrentVisual.ReservedPinCorner,
                    "Temporary marker-read failure can settle and recover the existing helper cue");
            }
        }
        static void Pauses(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                f.Pin(); f.Update(); f.Update(focusPaused: true);
                check(!f.Focus.Visible && f.Pins.Handles.Length == 1 && !f.Corners[0].Disposed,
                    "Pause moves hides helper focus while the HIGH pin corner remains visible");
                f.Update(focusPaused: true, pinsPaused: true);
                check(!f.Focus.Visible && f.Pins.Handles.Length == 0 && f.Confirmed.Count == 1 && f.Owned.Count == 1,
                    "Pause all hides both helper surfaces without erasing HIGH pin cleanup records");
                f.Update(style: 0); f.Clock += 121; f.Update(style: 0);
                check(!f.Focus.Visible && f.Pins.Handles.Length == 1,
                    "Turning the focus frame off leaves the confirmed HIGH pin corner independent");
                f.Indicators.Hide();
                check(!f.Focus.Visible && f.Pins.Handles.Length == 0, "Helper liveness shutdown hides both owned visual surfaces");
            }
        }
        static void Lifetimes(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                f.Pin(); f.Update(); f.Windows.Window.ProcessStarted++; f.Update();
                check(f.Pins.Handles.Length == 0 && f.Focus.CurrentVisual != null && !f.Focus.CurrentVisual.ReservedPinCorner,
                    "A changed HIGH process lifetime cannot inherit a retained helper corner or its focus reservation");
            }
            using (var f = new Fixture()) {
                f.Pin(); f.Update(); f.Confirmed.Clear(); f.Update();
                check(f.Pins.Handles.Length == 0 && !f.Focus.CurrentVisual.ReservedPinCorner && f.Owned.Count == 1,
                    "Permission-release visual clear removes helper cues before existing owned-pin cleanup runs");
            }
            using (var f = new Fixture()) {
                f.Pin(); f.Update(); f.Windows.Style(f.Windows.Foreground, false);
                f.Confirmed.RecordResult(f.Windows.Window, false, new WindowMoveWorker.Result {
                    Outcome = WindowMoveWorker.Outcome.Success, CallerExited = true });
                f.Update();
                check(f.Pins.Handles.Length == 0 && !f.Focus.CurrentVisual.ReservedPinCorner,
                    "Confirmed HIGH unpin removes the helper corner and restores the complete focus lighting");
            }
            using (var f = new Fixture()) {
                f.Pin(); f.Update(); f.TargetAllowed = false; f.Update();
                check(!f.Focus.Visible && f.Pins.Handles.Length == 0,
                    "A target that loses authenticated-helper eligibility immediately loses both helper surfaces");
            }
        }
        static void Races(Action<bool, string> check)
        {
            using (var f = new Fixture()) {
                f.Pin(); f.DuringCorner = f.Confirmed.Clear; f.Update();
                check(f.Pins.Handles.Length == 0 && f.Focus.CurrentVisual != null && !f.Focus.CurrentVisual.ReservedPinCorner,
                    "Visual ownership cleared during HIGH corner publication cannot leave a stale focus reservation");
            }
            using (var f = new Fixture()) {
                f.Pin(); f.Update(); f.Indicators.Update(new[] { 2, 0, 0, 0, 3, 0 });
                check(!f.Focus.Visible && f.Pins.Handles.Length == 0,
                    "An old or malformed helper appearance packet cannot keep either owned surface visible");
                f.Update(); f.Indicators.Dispose();
                check(!f.Focus.Visible && f.Pins.Handles.Length == 0 && f.Confirmed.Count == 1 && f.Owned.Count == 1,
                    "Helper renderer disposal destroys its surfaces without claiming successful pin cleanup");
                int frames = f.Frames.Count, corners = f.Corners.Count; f.Update();
                check(f.Frames.Count == frames && f.Corners.Count == corners, "A disposed helper renderer cannot republish late visual work");
            }
        }
    }
}
