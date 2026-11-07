using System;
using System.Reflection;
using System.Runtime.Serialization;

namespace TaskbarSelect
{
    internal static class OwnedSnapPrototypeTests
    {
        static int checks;
        static void Check(bool value, string message)
        { if (!value) throw new Exception(message); checks++; }
        internal static int Run()
        {
            try {
                if (OwnedSnapPolicyTests.Run() != 0 || OwnedSnapMovementTests.Run() != 0 || OwnedSnapRoutingTests.Run() != 0) return 1;
                for (int mask = 0; mask < 128; mask++) {
                    bool desktop = (mask & 1) != 0, wanted = (mask & 2) != 0, active = (mask & 4) != 0;
                    bool paused = (mask & 8) != 0, recovering = (mask & 16) != 0, exiting = (mask & 32) != 0, discovering = (mask & 64) != 0;
                    bool allowed = OwnedSnapRuntimePolicy.Allowed(desktop, wanted, active, paused, recovering, exiting, discovering);
                    Check(allowed == (mask == 7), "Only an active, opted-in desktop outside pause, recovery, exit and discovery can own input");
                }
                ProfileAdmission();
                Check(OwnedSnapKeyboard.Eligible(0x25, 1, true, false, true, true), "Physical extended left is eligible");
                Check(OwnedSnapKeyboard.Eligible(0x27, 0x11, true, false, true, true), "Ordinary injected input is supported only through an eligible opt-in route");
                foreach (uint flags in new uint[] { 2, 0x12, 0x80, 0x20, 0x40, 0x100 })
                    Check(!OwnedSnapKeyboard.Eligible(0x25, flags, true, false, true, true), "Lower-integrity, up, Alt or malformed input cannot claim a chord");
                Check(!OwnedSnapKeyboard.Eligible(0x26, 0, true, false, true, true), "Windows+Up remains native");
                Check(!OwnedSnapKeyboard.Eligible(0x28, 0, true, false, true, true), "Windows+Down remains native");
                Check(!OwnedSnapKeyboard.Eligible(0x25, 0, false, false, true, true), "Bare arrow remains native");
                Check(!OwnedSnapKeyboard.Eligible(0x25, 0, true, true, true, true), "Ctrl/Alt/Shift variants remain native");
                Check(!OwnedSnapKeyboard.Eligible(0x25, 0, true, false, false, true), "Changed foreground cannot own input");
                Check(!OwnedSnapKeyboard.Eligible(0x25, 0, true, false, true, false), "Stale target cannot own input");
                var ownership = new OwnedSnapKeyOwnership();
                Check(!ownership.ConsumeExisting(0x25, false) && !ownership.ConsumeExisting(0x25, true), "Failed/unaccepted dispatch has no key ownership");
                ownership.Accept(0x25);
                Check(ownership.ConsumeExisting(0x25, false), "Held arrow repeat consumes without another action");
                Check(!ownership.ConsumeExisting(0x27, true), "Different key-up passes through");
                Check(ownership.ConsumeExisting(0x25, true) && !ownership.ConsumeExisting(0x25, true), "Accepted arrow up is consumed exactly once");
                ownership.Accept(0x25); ownership.Accept(0x27); ownership.Clear();
                Check(!ownership.ConsumeExisting(0x25, false) && !ownership.ConsumeExisting(0x27, true), "Confirmed unhook clears ownership");
                var release = new OwnedSnapReleaseGate();
                Check(!release.Ready, "New registration starts disarmed"); release.Observe(false);
                Check(!release.Ready, "Held chord at enable cannot become an owned repeat"); release.Observe(true);
                Check(release.Ready, "Observed full release permits a new chord"); release.Observe(false);
                Check(release.Ready, "Holding Windows between deliberate arrow presses remains supported"); release.Reset();
                Check(!release.Ready, "Pause/re-enable requires a fresh release");
                Console.WriteLine("PASS: owned snap runtime/input checks " + checks + "; no desktop input or hook installed.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        static void ProfileAdmission()
        {
            var profile = typeof(AppProfile).GetField("selection", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = profile.GetValue(null);
            try {
                foreach (bool isolated in new[] { false, true }) {
                    profile.SetValue(null, new ProfileSelection(@"C:\OwnedSnapMetadataOnly", isolated));
                    // Only state predicates and the Off path are called. No
                    // controller constructor, hook, window, timer or I/O runs.
                    var controller = (Controller)FormatterServices.GetUninitializedObject(typeof(Controller));
                    Set(controller, "DesktopRuntime", true); Set(controller, "enabled", true);
                    var allowed = typeof(Controller).GetProperty("OwnedSnapAllowed", BindingFlags.Instance | BindingFlags.NonPublic);
                    Check(!controller.OwnedSnapPrototypeEnabled && !(bool)allowed.GetValue(controller, null),
                        "Every fresh controller starts with snapping Off, profile=" + isolated);
                    Set(controller, "ownedSnapWanted", true);
                    Check(controller.OwnedSnapPrototypeEnabled && (bool)allowed.GetValue(controller, null) &&
                        controller.OwnedSnapDiagnostic().Contains("On"),
                        "Production and isolated profiles share explicit session opt-in admission, profile=" + isolated);
                    controller.MovementPaused = true;
                    Check(!(bool)allowed.GetValue(controller, null), "Pause still revokes opted-in runtime admission, profile=" + isolated);
                    controller.MovementPaused = false;
                    controller.SetOwnedSnapPrototype(false);
                    Check(!controller.OwnedSnapPrototypeEnabled && !(bool)allowed.GetValue(controller, null) &&
                        (long)typeof(Controller).GetField("ownedSnapEpoch", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller) > 0,
                        "Explicit Off retires the session epoch in either profile without a hook, profile=" + isolated);
                    var next = (Controller)FormatterServices.GetUninitializedObject(typeof(Controller));
                    Check(!next.OwnedSnapPrototypeEnabled,
                        "A prior controller's opt-in cannot enable a later controller session, profile=" + isolated);
                }
            } finally { profile.SetValue(null, previous); }
        }
        static void Set(Controller controller, string name, object value)
        { typeof(Controller).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, value); }
    }
}
