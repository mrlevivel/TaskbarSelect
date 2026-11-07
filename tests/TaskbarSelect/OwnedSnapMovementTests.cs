using System;
using System.Drawing;

namespace TaskbarSelect
{
    // Pure admission/math checks. These never construct a mover, query process
    // metadata, create a window, or install input hooks.
    internal static class OwnedSnapMovementTests
    {
        static int checks;
        static void Assert(bool value, string message)
        { if (!value) throw new Exception(message); checks++; }

        static Native.Rect Rect(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }

        static NativeProcessIdentity.Info Identity(uint process, uint integrity)
        {
            return new NativeProcessIdentity.Info { Process = process, Started = 42, IntegrityAvailable = true,
                ElevationAvailable = true, IntegrityLevel = integrity };
        }

        internal static int Run()
        {
            checks = 0;
            try {
                Guards(); OrdinaryAdmission(); AdministratorAdmission(); Geometry();
                Console.WriteLine("PASS: owned snap movement admission and geometry (" + checks + " checks).");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        static void Guards()
        {
            Assert(!OwnedSnapMovementGeometry.GuardCurrent(null), "An absent runtime authorization refuses");
            Assert(!OwnedSnapMovementGeometry.GuardCurrent(delegate { return false; }), "A revoked runtime authorization refuses");
            Assert(!OwnedSnapMovementGeometry.GuardCurrent(delegate { throw new InvalidOperationException(); }), "An unreadable runtime authorization refuses");
            bool current = true;
            Func<bool> guard = delegate { return current; };
            Assert(OwnedSnapMovementGeometry.GuardCurrent(guard), "A current runtime authorization is accepted");
            current = false;
            Assert(!OwnedSnapMovementGeometry.GuardCurrent(guard), "The guard observes revocation rather than caching its original value");
        }

        static void OrdinaryAdmission()
        {
            var caller = Identity(1, NativeProcessIdentity.Medium);
            var target = Identity(2, NativeProcessIdentity.Medium);
            Assert(OwnedSnapMovementGeometry.Ordinary(caller, target), "Readable non-elevated medium windows are admitted");
            target.IntegrityLevel = NativeProcessIdentity.Low;
            Assert(OwnedSnapMovementGeometry.Ordinary(caller, target), "Lower-integrity ordinary windows need no administrator route");
            caller.IntegrityLevel = NativeProcessIdentity.Low; target.IntegrityLevel = NativeProcessIdentity.Medium;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "An ordinary target above the caller cannot route through administrator support");
            caller.IntegrityLevel = NativeProcessIdentity.Medium; target.IntegrityLevel = NativeProcessIdentity.MediumPlus;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "Above-medium target is outside prototype scope");
            target.IntegrityLevel = NativeProcessIdentity.Medium; target.Elevated = true;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "Elevated target refuses even with a medium integrity label");
            target.Elevated = false; caller.Elevated = true;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "An elevated caller is outside ordinary-only prototype scope");
            caller.Elevated = false; target.ElevationAvailable = false;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "Unknown target elevation fails closed");
            target.ElevationAvailable = true; target.IntegrityAvailable = false;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "Unknown target integrity fails closed");
            target.IntegrityAvailable = true; caller.ElevationAvailable = false;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "Unknown caller elevation fails closed");
            caller.ElevationAvailable = true; caller.IntegrityAvailable = false;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "Unknown caller integrity fails closed");
            caller.IntegrityAvailable = true; target.Started = 0;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "Unproven process lifetime refuses");
            target.Started = 42; target.Process = 0;
            Assert(!OwnedSnapMovementGeometry.Ordinary(caller, target), "Missing target process refuses");
            Assert(!OwnedSnapMovementGeometry.Ordinary(null, target), "Missing permission metadata refuses");
        }

        static void AdministratorAdmission()
        {
            var caller = Identity(1, NativeProcessIdentity.Medium);
            var target = Identity(2, NativeProcessIdentity.High); target.Elevated = true;
            Assert(OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0),
                "An exact readable HIGH elevated target uses its current approved helper session");
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Ordinary, 7, 7, false, 0),
                "An active administrator session never admits HIGH to the ordinary input route");
            foreach (uint integrity in new[] { NativeProcessIdentity.Low, NativeProcessIdentity.Medium, NativeProcessIdentity.MediumPlus, NativeProcessIdentity.System }) {
                target.IntegrityLevel = integrity;
                Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0),
                    "Administrator snap scope rejects every non-HIGH integrity level");
            }
            target.IntegrityLevel = NativeProcessIdentity.High; target.Elevated = false;
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0), "HIGH without proven elevation refuses");
            target.Elevated = true;
            foreach (var session in new[] { Tuple.Create(0, 0), Tuple.Create(0, 7), Tuple.Create(7, 0), Tuple.Create(7, 8), Tuple.Create(-1, -1) })
                Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, session.Item1, session.Item2, false, 0),
                    "Missing, replaced, or malformed administrator session refuses");
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, true, 0), "Uncertain administrator operation refuses HIGH");
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 1), "Uncertain local helper refuses HIGH");
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, (OwnedSnapRoute)99, 7, 7, false, 0), "Unknown permission route refuses");
            target.IntegrityAvailable = false;
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0), "Unknown HIGH integrity refuses");
            target.IntegrityAvailable = true; target.ElevationAvailable = false;
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0), "Unknown HIGH elevation refuses");
            target.ElevationAvailable = true; target.Started = 0;
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0), "Unknown HIGH lifetime refuses");
            target.Started = 42; target.Process = 0;
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0), "Missing HIGH process refuses");
            target.Process = 2; caller.Elevated = true;
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0), "Elevated controller cannot broaden the approved medium-controller scope");
            caller.Elevated = false; caller.IntegrityLevel = NativeProcessIdentity.Low;
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0), "Low controller cannot borrow the medium controller's helper scope");
            caller.IntegrityLevel = NativeProcessIdentity.Medium; caller.ElevationAvailable = false;
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, 7, false, 0), "Unreadable controller metadata refuses");
            caller.ElevationAvailable = true;
            Assert(!OwnedSnapMovementGeometry.Authorized(caller, null, OwnedSnapRoute.Administrator, 7, 7, false, 0), "Missing HIGH target metadata refuses");
            int generation = 7;
            Func<bool> scoped = delegate { return OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 7, generation, false, 0); };
            Assert(OwnedSnapMovementGeometry.GuardCurrent(scoped), "The same helper session remains admitted during its own bounded operation");
            generation = 0;
            Assert(!OwnedSnapMovementGeometry.GuardCurrent(scoped), "Helper loss revokes an already-created movement guard");
            generation = 8;
            Assert(!OwnedSnapMovementGeometry.GuardCurrent(scoped), "A replacement helper cannot revive an old movement guard");
            Assert(OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Administrator, 8, generation, false, 0),
                "A new explicit operation may use a newly approved helper session without reusing the old scope");
            target.IntegrityLevel = NativeProcessIdentity.Medium; target.Elevated = false;
            Assert(OwnedSnapMovementGeometry.Authorized(caller, target, OwnedSnapRoute.Ordinary, 0, 0, true, 1) == OwnedSnapMovementGeometry.Ordinary(caller, target),
                "Ordinary permission classification stays identical to the existing ordinary-only contract");
        }

        static void Geometry()
        {
            var outer = Rect(-1008, -500, -192, 108);
            var visible = Rect(-1000, -500, -200, 100);
            var wanted = new Rectangle(0, -200, 600, 900);
            Rectangle requested;
            Assert(OwnedSnapMovementGeometry.TryRequest(outer, visible, wanted, new Size(300, 200), out requested) &&
                requested == new Rectangle(-8, -200, 616, 908), "Measured invisible borders are preserved around the exact desired visible half");
            Assert(OwnedSnapMovementGeometry.TryRequest(outer, visible, wanted, new Size(616, 908), out requested),
                "An app minimum equal to requested outer size fits exactly");
            Assert(!OwnedSnapMovementGeometry.TryRequest(outer, visible, wanted, new Size(617, 908), out requested) && requested.IsEmpty,
                "A minimum one pixel too wide refuses instead of widening the visible half");
            Assert(!OwnedSnapMovementGeometry.TryRequest(outer, visible, wanted, new Size(616, 909), out requested),
                "A minimum one pixel too tall refuses");
            Assert(!OwnedSnapMovementGeometry.TryRequest(outer, visible, wanted, new Size(0, 1), out requested),
                "Invalid app minimum refuses");
            Assert(!OwnedSnapMovementGeometry.TryRequest(outer, Rect(-1009, -500, -200, 100), wanted, new Size(1, 1), out requested),
                "A DWM frame outside the measured outer frame refuses");
            Assert(!OwnedSnapMovementGeometry.TryRequest(outer, visible, new Rectangle(Int32.MinValue, 0, 20, 20), new Size(1, 1), out requested),
                "Adding a left invisible border below Int32 minimum refuses");
            Assert(!OwnedSnapMovementGeometry.TryRequest(outer, visible, new Rectangle(Int32.MaxValue - 20, 0, 20, 20), new Size(1, 1), out requested),
                "Adding a right invisible border beyond Int32 maximum refuses");
            Assert(!OwnedSnapMovementGeometry.TryRequest(outer, visible, new Rectangle(Int32.MaxValue, 0, 1, 20), new Size(1, 1), out requested),
                "An already-overflowing desired rectangle refuses");
            Assert(!OwnedSnapMovementGeometry.TryRequest(Rect(Int32.MinValue, 0, Int32.MaxValue, 10), Rect(0, 0, 1, 10),
                new Rectangle(0, 0, 1, 10), new Size(1, 1), out requested), "An unrepresentable outer width refuses rather than wrapping");
            var area = Rect(-801, -100, 0, 500);
            Assert(OwnedSnapMovementGeometry.HalfOf(area, new Rectangle(-801, -100, 400, 600)), "Negative-coordinate odd left half is supported");
            Assert(OwnedSnapMovementGeometry.HalfOf(area, new Rectangle(-401, -100, 401, 600)), "Odd right half owns the remaining pixel");
            Assert(!OwnedSnapMovementGeometry.HalfOf(area, new Rectangle(-401, -100, 400, 600)), "An inexact odd right half refuses");
            Assert(!OwnedSnapMovementGeometry.HalfOf(area, new Rectangle(-801, -99, 400, 599)), "A shortened or offset requested half refuses");
            Assert(!OwnedSnapMovementGeometry.HalfOf(Rect(0, 0, 1, 500), new Rectangle(0, 0, 1, 500)), "One-pixel display cannot form two halves");
            Assert(!OwnedSnapMovementGeometry.HalfOf(Rect(Int32.MinValue, 0, Int32.MaxValue, 10), new Rectangle(0, 0, 1, 10)),
                "Unrepresentable monitor width refuses");
        }
    }
}
