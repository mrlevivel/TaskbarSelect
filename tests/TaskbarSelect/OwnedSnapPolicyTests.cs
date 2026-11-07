using System;
using System.Collections.Generic;
using System.Drawing;

namespace TaskbarSelect
{
    internal static class OwnedSnapPolicyTests
    {
        static int checks;
        static void Assert(bool value, string message)
        { if (!value) throw new Exception(message); checks++; }

        static OwnedSnapDisplay Display(string device, string identity, Rectangle bounds, Rectangle work, bool hidden)
        { return new OwnedSnapDisplay { Device = device, Identity = identity, Bounds = bounds, Work = work, Hidden = hidden }; }

        static OwnedSnapCycle Receipt(OwnedSnapPlan plan)
        { return new OwnedSnapCycle { DisplayIdentity = plan.Display.Identity, RightHalf = plan.RightHalf, ExpectedVisible = plan.VisibleTarget }; }

        static OwnedSnapPlan Plan(IList<OwnedSnapDisplay> displays, string identity, Rectangle visible, bool left, OwnedSnapCycle previous)
        {
            OwnedSnapPlan plan; string reason;
            Assert(OwnedSnapPolicy.TryPlan(displays, identity, visible, left, previous, out plan, out reason),
                "Expected an owned snap plan: " + reason);
            Assert(plan != null && reason == null, "A successful plan has geometry and no refusal reason");
            return plan;
        }

        static void Refuses(IList<OwnedSnapDisplay> displays, string identity, Rectangle visible, string message)
        {
            OwnedSnapPlan plan; string reason;
            Assert(!OwnedSnapPolicy.TryPlan(displays, identity, visible, true, null, out plan, out reason) &&
                plan == null && !String.IsNullOrEmpty(reason), message);
        }

        internal static int Run()
        {
            checks = 0;
            try {
                CycleAndDirection(); VisibilityAndOddPixels(); Ordering(); Drift(); InvalidInputs(); CoordinateEdges();
                Console.WriteLine("PASS: owned snap geometry policy (" + checks + " checks).");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        static List<OwnedSnapDisplay> ThreeDisplays()
        {
            return new List<OwnedSnapDisplay> {
                Display("DISPLAY3", "right", new Rectangle(1600, 200, 1200, 1000), new Rectangle(1600, 200, 1200, 960), false),
                Display("DISPLAY1", "left", new Rectangle(-1920, -200, 1920, 1080), new Rectangle(-1920, -200, 1920, 1040), false),
                Display("DISPLAY2", "middle", new Rectangle(0, 0, 1600, 900), new Rectangle(0, 0, 1600, 860), false)
            };
        }

        static void CycleAndDirection()
        {
            var displays = ThreeDisplays();
            var original = new Rectangle(150, 100, 900, 650);
            var plan = Plan(displays, "middle", original, true, null);
            Assert(plan.Display.Identity == "middle" && !plan.RightHalf, "Fresh Left uses the current display left half");
            string[] expected = { "left", "left", "right", "right", "middle", "middle" };
            for (int index = 0; index < expected.Length; index++) {
                plan = Plan(displays, plan.Display.Identity, plan.VisibleTarget, true, Receipt(plan));
                Assert(plan.Display.Identity == expected[index] && plan.RightHalf == (index % 2 == 0),
                    "Repeated Left alternates previous display right, then left and wraps");
            }
            plan = Plan(displays, "middle", original, false, null);
            Assert(plan.Display.Identity == "middle" && plan.RightHalf, "Fresh Right uses the current display right half");
            string[] mirrored = { "right", "right", "left", "left", "middle", "middle" };
            for (int index = 0; index < mirrored.Length; index++) {
                plan = Plan(displays, plan.Display.Identity, plan.VisibleTarget, false, Receipt(plan));
                Assert(plan.Display.Identity == mirrored[index] && plan.RightHalf == (index % 2 != 0),
                    "Repeated Right mirrors Left and wraps");
            }
            var left = Plan(displays, "middle", original, true, null);
            var right = Plan(displays, "middle", left.VisibleTarget, false, Receipt(left));
            Assert(right.Display.Identity == "middle" && right.RightHalf, "Reversing Left to Right stays on this display");
            var back = Plan(displays, "middle", right.VisibleTarget, true, Receipt(right));
            Assert(back.Display.Identity == "middle" && !back.RightHalf, "Reversing Right to Left stays on this display");
            var single = new List<OwnedSnapDisplay> { displays[2] };
            plan = Plan(single, "middle", original, true, null);
            plan = Plan(single, "middle", plan.VisibleTarget, true, Receipt(plan));
            Assert(plan.Display.Identity == "middle" && plan.RightHalf, "Single display Left wraps to its right half");
        }

        static void VisibilityAndOddPixels()
        {
            var visible = Display("visible", "visible", new Rectangle(-801, -601, 801, 601), new Rectangle(-791, -581, 781, 541), false);
            var hidden = Display("hidden", "hidden", new Rectangle(0, -601, 801, 601), new Rectangle(0, -601, 801, 561), true);
            var displays = new List<OwnedSnapDisplay> { visible, hidden };
            var original = new Rectangle(-700, -500, 500, 300);
            var left = Plan(displays, "visible", original, true, null);
            var right = Plan(displays, "visible", original, false, null);
            Assert(left.VisibleTarget == new Rectangle(-791, -581, 390, 541) && right.VisibleTarget == new Rectangle(-401, -581, 391, 541),
                "Visible taskbar uses complete offset work area and assigns odd pixel to right half");
            Assert(left.VisibleTarget.Right == right.VisibleTarget.Left && right.VisibleTarget.Right == visible.Work.Right,
                "Odd-width halves meet without a gap or overlap");
            left = Plan(displays, "hidden", original, true, null);
            right = Plan(displays, "hidden", original, false, null);
            Assert(left.VisibleTarget == new Rectangle(0, -601, 400, 601) && right.VisibleTarget == new Rectangle(400, -601, 401, 601),
                "Hidden taskbar uses monitor bounds rather than reserved work area");
            Assert(displays[0] == visible && visible.Work == new Rectangle(-791, -581, 781, 541), "Planning does not reorder or mutate supplied display records");
        }

        static void Ordering()
        {
            var z = Display("Z", "z", new Rectangle(-100, -200, 100, 100), new Rectangle(-100, -200, 100, 100), false);
            var a = Display("A", "a", z.Bounds, z.Work, false);
            var lower = Display("B", "lower", new Rectangle(-100, 200, 100, 100), new Rectangle(-100, 200, 100, 100), false);
            var farther = Display("C", "farther", new Rectangle(-200, 500, 100, 100), new Rectangle(-200, 500, 100, 100), false);
            var displays = new List<OwnedSnapDisplay> { lower, z, farther, a };
            var plan = Plan(displays, "lower", new Rectangle(-90, 210, 80, 80), true, null);
            plan = Plan(displays, plan.Display.Identity, plan.VisibleTarget, true, Receipt(plan));
            Assert(plan.Display.Identity == "z", "Top coordinate orders displays sharing a left edge");
            plan = Plan(displays, plan.Display.Identity, plan.VisibleTarget, true, Receipt(plan));
            plan = Plan(displays, plan.Display.Identity, plan.VisibleTarget, true, Receipt(plan));
            Assert(plan.Display.Identity == "a", "Device ordinal breaks identical left/top coordinates deterministically");
            plan = Plan(displays, plan.Display.Identity, plan.VisibleTarget, true, Receipt(plan));
            plan = Plan(displays, plan.Display.Identity, plan.VisibleTarget, true, Receipt(plan));
            Assert(plan.Display.Identity == "farther", "Signed left coordinate takes priority over top coordinate");
        }

        static void Drift()
        {
            var displays = ThreeDisplays();
            var first = Plan(displays, "middle", new Rectangle(100, 100, 600, 500), true, null);
            var moved = first.VisibleTarget; moved.Offset(1, 0);
            var plan = Plan(displays, "middle", moved, true, Receipt(first));
            Assert(plan.Display.Identity == "middle" && !plan.RightHalf, "One-pixel manual movement invalidates continuation");
            var resized = first.VisibleTarget; resized.Width--;
            plan = Plan(displays, "middle", resized, true, Receipt(first));
            Assert(plan.Display.Identity == "middle" && !plan.RightHalf, "Manual resizing invalidates continuation");
            plan = Plan(displays, "left", first.VisibleTarget, true, Receipt(first));
            Assert(plan.Display.Identity == "left" && !plan.RightHalf, "Changed current display starts a fresh cycle");
            var receipt = Receipt(first); receipt.DisplayIdentity = "MIDDLE";
            plan = Plan(displays, "middle", first.VisibleTarget, true, receipt);
            Assert(plan.Display.Identity == "left" && plan.RightHalf, "Identity matching follows Windows case-insensitive semantics");
        }

        static void InvalidInputs()
        {
            var visible = new Rectangle(0, 0, 10, 10);
            Refuses(null, "display", visible, "Missing display list refuses");
            Refuses(new List<OwnedSnapDisplay>(), "display", visible, "Empty display list refuses");
            var displays = ThreeDisplays();
            Refuses(displays, "missing", visible, "Disconnected current identity refuses");
            Refuses(displays, " ", visible, "Blank current identity refuses");
            Refuses(displays, "middle", Rectangle.Empty, "Unavailable visible bounds refuse");
            displays.Add(null); Refuses(displays, "middle", visible, "Null connected display refuses");
            displays = ThreeDisplays(); displays[0].Identity = "MIDDLE";
            Refuses(displays, "middle", visible, "Duplicate physical identity refuses even with case changes");
            displays = ThreeDisplays(); displays[0].Device = "display2";
            Refuses(displays, "middle", visible, "Duplicate device refuses even with case changes");
            displays = ThreeDisplays(); displays[0].Device = " ";
            Refuses(displays, "middle", visible, "Blank device refuses");
            displays = ThreeDisplays(); displays[0].Identity = null;
            Refuses(displays, "middle", visible, "Blank display identity refuses");
            displays = ThreeDisplays(); displays[0].Bounds = new Rectangle(1600, 200, 0, 1000);
            Refuses(displays, "middle", visible, "Zero-width monitor bounds refuse");
            displays = ThreeDisplays(); displays[0].Work = new Rectangle(1599, 200, 1200, 960);
            Refuses(displays, "middle", visible, "Work area outside monitor bounds refuses");
            displays = ThreeDisplays(); displays[0].Hidden = true; displays[0].Work = Rectangle.Empty;
            Refuses(displays, "middle", visible, "Hidden display still requires a valid observed work area");
            var narrow = Display("narrow", "narrow", new Rectangle(0, 0, 1, 100), new Rectangle(0, 0, 1, 100), false);
            Refuses(new List<OwnedSnapDisplay> { narrow }, "narrow", visible, "One-pixel width cannot create two positive halves");
            narrow.Bounds = new Rectangle(0, 0, 2, 100); narrow.Work = narrow.Bounds;
            var smallest = Plan(new List<OwnedSnapDisplay> { narrow }, "narrow", visible, true, null);
            Assert(smallest.VisibleTarget == new Rectangle(0, 0, 1, 100), "Two-pixel width creates one-pixel halves");
        }

        static void CoordinateEdges()
        {
            var high = Display("high", "high", new Rectangle(Int32.MaxValue - 9, Int32.MaxValue - 5, 9, 5),
                new Rectangle(Int32.MaxValue - 9, Int32.MaxValue - 5, 9, 5), false);
            var plan = Plan(new List<OwnedSnapDisplay> { high }, "high", new Rectangle(0, 0, 2, 2), false, null);
            Assert(plan.VisibleTarget.Right == Int32.MaxValue && plan.VisibleTarget.Bottom == Int32.MaxValue && plan.VisibleTarget.Width == 5,
                "Representable upper coordinate edges split without overflow");
            var low = Display("low", "low", new Rectangle(Int32.MinValue, Int32.MinValue, 3, 2),
                new Rectangle(Int32.MinValue, Int32.MinValue, 3, 2), true);
            plan = Plan(new List<OwnedSnapDisplay> { low }, "low", new Rectangle(0, 0, 2, 2), true, null);
            Assert(plan.VisibleTarget.X == Int32.MinValue && plan.VisibleTarget.Width == 1, "Minimum signed coordinates remain valid");
            high.Bounds = new Rectangle(Int32.MaxValue - 8, 0, 9, 5); high.Work = high.Bounds;
            Refuses(new List<OwnedSnapDisplay> { high }, "high", new Rectangle(0, 0, 2, 2), "Overflowing monitor right endpoint refuses");
            high.Bounds = new Rectangle(0, Int32.MaxValue - 4, 9, 5); high.Work = high.Bounds;
            Refuses(new List<OwnedSnapDisplay> { high }, "high", new Rectangle(0, 0, 2, 2), "Overflowing monitor bottom endpoint refuses");
            Refuses(new List<OwnedSnapDisplay> { low }, "low", new Rectangle(Int32.MaxValue, 0, 1, 1), "Overflowing current window rectangle refuses");
        }
    }
}
