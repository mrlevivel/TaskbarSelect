using System;

namespace TaskbarSelect
{
    internal static class PinIndicatorObservationTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var f = new WindowStateActionsTests.Fixture();
            var actions = new FocusedWindowStateActions(f.Api);
            check(actions.IndicatedPins.Length == 0, "A window is not labelled pinned merely because it is eligible");
            var result = actions.TogglePin(f.Foreground);
            var first = actions.IndicatedPins;
            check(result.Success && result.Pinned == true && first.Length == 1 && first[0].Epoch > 0 && !first[0].HelperOwned,
                "A confirmed local pin publishes one positive visual observation epoch");
            long epoch = first[0].Epoch;
            first[0].Identity.Process = 123;
            first[0].Epoch = -1;
            first[0].HelperOwned = true;
            check(actions.IndicatedPins[0].Identity.Process == f.Window.Process && actions.IndicatedPins[0].Epoch == epoch && !actions.IndicatedPins[0].HelperOwned,
                "Visual observers cannot rewrite retained identity or epoch");
            check(actions.TogglePin(f.Foreground).Success && actions.IndicatedPins.Length == 0,
                "A confirmed unpin removes its visual indication");
            f.Local = false;
            check(actions.TogglePin(f.Foreground).Success && actions.IndicatedPins.Length == 1 && actions.IndicatedPins[0].Epoch > epoch && actions.IndicatedPins[0].HelperOwned,
                "Confirmed helper-routed pins get a fresh observation without borrowing local cleanup authority");
            check(actions.PinnedCount == 0, "A helper-routed visual observation is not a local pin ownership entry");
            check(actions.TogglePin(f.Foreground).Success && actions.IndicatedPins.Length == 0,
                "Confirmed helper-routed unpin clears the visual record");
            f = new WindowStateActionsTests.Fixture();
            f.WorkerSucceeds = false; f.WorkerMutates = false;
            actions = new FocusedWindowStateActions(f.Api);
            check(!actions.TogglePin(f.Foreground).Success && actions.IndicatedPins.Length == 0,
                "Rejected pin does not publish a success cue");
            f = new WindowStateActionsTests.Fixture(); f.WorkerExited = false;
            actions = new FocusedWindowStateActions(f.Api);
            check(!actions.TogglePin(f.Foreground).Success && actions.IndicatedPins.Length == 0 && actions.PinnedCount == 1,
                "An uncertain mutation retains cleanup authority without claiming a confirmed visual pin");
            f = new WindowStateActionsTests.Fixture(); f.Style(f.Foreground, true);
            actions = new FocusedWindowStateActions(f.Api);
            check(actions.IndicatedPins.Length == 0, "A pre-existing topmost window is not claimed as this app's pin");
        }
    }
}
