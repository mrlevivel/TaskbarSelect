using System;
using System.Collections.Generic;

namespace TaskbarSelect
{
    internal static class ReservedPinFocusTests
    {
        sealed class Fixture
        {
            internal readonly WindowStateActionsTests.Fixture Windows = new WindowStateActionsTests.Fixture();
            internal readonly OwnedWindowPins Pins;
            internal readonly ReservedPinFocus Focus;
            internal bool MarkerReadFails, ClearDuringOwned;
            internal Fixture()
            {
                Pins = new OwnedWindowPins(new OwnedWindowPinApi {
                    Read = Windows.Api.Read, Exists = Windows.Api.Exists, SetTopmost = Windows.Api.CleanupTopmost,
                    SetMarker = Windows.Api.SetMarker, RemoveMarker = Windows.Api.RemoveMarker, Now = Windows.Api.Now,
                    Pause = Windows.Api.Pause, Quiescent = Windows.Api.Quiescent,
                    ReadMarker = delegate(IntPtr window, string marker) {
                        if (MarkerReadFails) throw new InvalidOperationException("Inert marker unavailable.");
                        return Windows.Api.ReadMarker(window, marker);
                    }
                });
                Focus = new ReservedPinFocus(Windows.Api.Read, delegate(FocusedWindowSnapshot target) {
                    if (ClearDuringOwned) Focus.Clear();
                    return Pins.MatchesPinnedObservation(target);
                }, Windows.Api.Exists);
            }
            internal FocusedWindowSnapshot Pin()
            { var original = Windows.Window.Copy(); Pins.Remember(original); Windows.Style(original.Window, true); return original; }
        }
        static WindowMoveWorker.Result Completed()
        { return new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Success, MutationAttempted = true, CallerExited = true }; }
        internal static void Run(Action<bool, string> check)
        { Confirmation(check); RetainedMarker(check); Reapproval(check); ExactTarget(check); Capacity(check); Observations(check); }
        static void Confirmation(Action<bool, string> check)
        {
            foreach (var result in new[] { null, new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Failed, CallerExited = true },
                new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Success, CallerExited = false } }) {
                var f = new Fixture(); var pin = f.Pin();
                check(!f.Focus.RecordResult(pin, true, result) && f.Focus.Count == 0 && !f.Focus.Matches(f.Windows.Window),
                    "Missing, failed or unconfirmed pin completion cannot become HIGH focus reservation metadata");
                check(f.Pins.Count == 1 && f.Windows.Markers.Count == 1,
                    "Refused visual confirmation leaves the existing pin cleanup ledger and marker intact");
            }
            var normal = new Fixture(); var original = normal.Pin();
            check(normal.Focus.RecordResult(original, true, Completed()) && normal.Focus.Count == 1 && normal.Focus.Matches(normal.Windows.Window),
                "Only a confirmed completed pin with matching live identity and owned marker enables reserved HIGH focus lighting");
            original.Process++;
            check(normal.Focus.Matches(normal.Windows.Window), "Caller changes cannot rewrite retained HIGH visual pin identity");
            normal.Windows.Style(normal.Windows.Foreground, false);
            check(!normal.Focus.Matches(normal.Windows.Window), "Actual non-topmost state immediately disables HIGH focus reservation");
            check(normal.Focus.RecordResult(normal.Windows.Window, false, Completed()) && normal.Focus.Count == 0,
                "Confirmed unpin removes only the visual record");
            check(normal.Pins.Count == 1 && normal.Windows.CleanupCalls == 0 && normal.Windows.WorkerCalls == 0,
                "Visual confirmation bookkeeping never performs unpin, worker or ownership cleanup actions");
            var unowned = new Fixture(); unowned.Windows.Style(unowned.Windows.Foreground, true);
            check(!unowned.Focus.RecordResult(unowned.Windows.Window, true, Completed()) && !unowned.Focus.Matches(unowned.Windows.Window),
                "A pre-existing topmost window cannot be adopted as a confirmed helper pin");
            var mismatch = new Fixture(); original = mismatch.Pin(); mismatch.Windows.Window.ProcessStarted++;
            check(!mismatch.Focus.RecordResult(original, true, Completed()) && mismatch.Focus.Count == 0,
                "An identity change after worker completion cannot publish reserved lighting");
            var clear = new Fixture(); original = clear.Pin(); clear.ClearDuringOwned = true;
            check(!clear.Focus.RecordResult(original, true, Completed()) && clear.Focus.Count == 0,
                "Cleanup racing visual confirmation prevents stale metadata from being republished");
        }
        static void RetainedMarker(Action<bool, string> check)
        {
            var f = new Fixture(); var pin = f.Pin();
            check(f.Pins.MatchesPinnedObservation(f.Windows.Window), "The read-only retained-pin accessor recognizes exact lifetime, topmost state and private marker");
            check(f.Focus.RecordResult(pin, true, Completed()), "The owned neutral fixture creates confirmed visual metadata");
            f.MarkerReadFails = true;
            check(!f.Pins.MatchesPinnedObservation(f.Windows.Window) && !f.Focus.Matches(f.Windows.Window) && f.Focus.Count == 1,
                "Unavailable marker reads fail closed without discarding visual or cleanup records");
            check(f.Pins.Count == 1 && f.Windows.Markers.Count == 1 && f.Windows.CleanupCalls == 0,
                "A failed visual marker read never forgets ownership, removes a marker or mutates a window");
            f.MarkerReadFails = false;
            check(f.Focus.Matches(f.Windows.Window), "A temporarily unavailable read can recover against the same retained marker");
            f.Windows.ReturnNullRead = true;
            check(!f.Focus.Matches(f.Windows.Window) && f.Focus.Count == 1, "Missing native metadata cannot become a positive reservation");
            f.Windows.ReturnNullRead = false; f.Windows.Markers.Clear();
            check(!f.Pins.MatchesPinnedObservation(f.Windows.Window) && !f.Focus.Matches(f.Windows.Window),
                "Identical process/thread/class HWND reuse without the lifetime marker cannot inherit quieted HIGH focus lighting");
            check(f.Pins.Count == 1, "Losing visual marker evidence does not falsely claim successful pin cleanup");
            f.Focus.Clear();
            check(f.Focus.Count == 0 && f.Pins.Count == 1, "Helper visual shutdown clears metadata independently of cleanup authority");
        }
        static void Reapproval(Action<bool, string> check)
        {
            var f = new Fixture(); var original = f.Pin(); f.Focus.RecordResult(original, true, Completed());
            f.Focus.Clear();
            check(f.Focus.Count == 0 && f.Pins.Count == 1 && f.Windows.Markers.Count == 1,
                "Authenticated release clears only visual confirmation before owned cleanup begins");
            check(f.Pins.ReleaseAll(f.Windows.Deadline), "The unchanged owned ledger completes its existing cleanup path");
            var fresh = f.Pin();
            check(!f.Focus.Matches(f.Windows.Window), "A newly installed provisional marker cannot revive confirmation cleared by release");
            check(f.Focus.RecordResult(fresh, true, Completed()) && f.Focus.Matches(f.Windows.Window),
                "Only a fresh confirmed result restores helper reservation after release and reapproval");
            f.Windows.Style(f.Windows.Foreground, false);
            f.Pins.Remember(f.Windows.Window.Copy());
            f.Focus.ForgetObservation(f.Windows.Foreground);
            f.Windows.Style(f.Windows.Foreground, true);
            check(!f.Focus.Matches(f.Windows.Window) && f.Pins.Count == 1 && f.Windows.Markers.Count == 1,
                "A re-pin's provisional owned marker cannot reuse an earlier visual success record");
            check(f.Focus.RecordResult(f.Windows.Window, true, Completed()) && f.Focus.Matches(f.Windows.Window),
                "The completed replacement pin may establish its own fresh visual confirmation");
        }
        static void ExactTarget(Action<bool, string> check)
        {
            var f = new Fixture(); var pin = f.Pin(); f.Focus.RecordResult(pin, true, Completed());
            var unrelated = f.Windows.Window.Copy(); unrelated.Window = unrelated.Root = new IntPtr(43); unrelated.ExtendedStyle |= 8;
            f.Windows.Windows.Add(unrelated.Window, unrelated);
            check(f.Focus.Matches(f.Windows.Window) && !f.Focus.Matches(unrelated),
                "A newly focused unrelated HIGH window cannot inherit a sticky reservation Boolean");
            foreach (var change in new Action<FocusedWindowSnapshot>[] {
                delegate(FocusedWindowSnapshot s) { s.Process++; }, delegate(FocusedWindowSnapshot s) { s.Thread++; },
                delegate(FocusedWindowSnapshot s) { s.ProcessStarted++; }, delegate(FocusedWindowSnapshot s) { s.ProcessImage += ".copy"; },
                delegate(FocusedWindowSnapshot s) { s.ClassName += "other"; }, delegate(FocusedWindowSnapshot s) { s.Root = new IntPtr(90); }
            }) {
                var target = f.Windows.Window.Copy(); change(target);
                check(!f.Focus.Matches(target), "Every retained target identity component is rechecked for reserved lighting");
            }
            f.ClearDuringOwned = true;
            check(!f.Focus.Matches(f.Windows.Window) && f.Focus.Count == 0,
                "Visual metadata cleared during a live predicate cannot return a stale positive result");
            check(AdministratorSupport.ValidFocus(new long[] { 2, 0, 0, 1, 3, 1, 1 }) &&
                !AdministratorSupport.ValidFocus(new long[] { 2, 0, 0, 1, 3, 1 }) &&
                !AdministratorSupport.ValidFocus(new long[] { 2, 0, 0, 1, 3, 1, 1, 1 }),
                "Helper appearance has exactly seven numeric values and no target or global reservation field");
        }
        static void Capacity(Action<bool, string> check)
        {
            var windows = new Dictionary<IntPtr, FocusedWindowSnapshot>();
            var seed = new WindowStateActionsTests.Fixture().Window.Copy();
            var visual = new ReservedPinFocus(delegate(IntPtr handle) { FocusedWindowSnapshot value; return windows.TryGetValue(handle, out value) ? value : null; },
                delegate { return true; }, delegate(IntPtr handle) { return windows.ContainsKey(handle); });
            for (int i = 0; i < OwnedWindowPins.Limit + 1; i++) {
                var target = seed.Copy(); target.Window = target.Root = new IntPtr(100 + i); target.ExtendedStyle |= 8; windows.Add(target.Window, target);
                bool recorded = visual.RecordResult(target, true, Completed());
                check(recorded == (i < OwnedWindowPins.Limit), "Confirmed HIGH visual metadata is bounded by the existing owned pin capacity");
            }
            check(visual.Count == OwnedWindowPins.Limit, "A full visual ledger never grows beyond the helper's existing pin bound");
            windows.Remove(new IntPtr(100));
            check(visual.RecordResult(windows[new IntPtr(100 + OwnedWindowPins.Limit)], true, Completed()) && visual.Count == OwnedWindowPins.Limit,
                "A positively closed target can reclaim visual capacity without changing any cleanup ownership");
        }
        static void Observations(Action<bool, string> check)
        {
            var f = new Fixture(); var original = f.Pin();
            check(f.Focus.Observations.Length == 0, "Provisional HIGH ownership cannot publish an indicator observation");
            f.Focus.RecordResult(original, true, Completed());
            var first = f.Focus.Observations;
            long epoch = first[0].Epoch;
            check(first.Length == 1 && epoch > 0 && first[0].HelperOwned && WindowStatePolicy.SameIdentity(first[0].Identity, f.Windows.Window),
                "Only confirmed HIGH pin completion produces a bounded positive visual epoch");
            first[0].Epoch = -1; first[0].Identity.Process++;
            check(f.Focus.Observations[0].Epoch == epoch && f.Focus.Observations[0].Identity.Process == f.Windows.Window.Process,
                "Helper corner candidates deep-copy both lifetime identity and observation epoch");
            f.MarkerReadFails = true;
            check(f.Focus.Observations.Length == 1 && !f.Focus.Matches(f.Windows.Window),
                "A presentation candidate alone grants no cue when the live owned marker is unreadable");
            f.MarkerReadFails = false; f.Focus.Clear();
            check(f.Focus.Observations.Length == 0 && f.Pins.Count == 1 && f.Windows.Markers.Count == 1,
                "Visual shutdown discards HIGH cue candidates without changing cleanup authority");
            f.Focus.RecordResult(original, true, Completed());
            check(f.Focus.Observations[0].Epoch > epoch, "Reconfirmation after clear cannot reuse a retired helper cue epoch");
            f.Focus.ForgetObservation(original.Window);
            check(f.Focus.Observations.Length == 0, "Forgetting an old pin removes its helper cue candidate immediately");
        }
    }
}
