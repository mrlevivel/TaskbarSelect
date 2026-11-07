using System;

namespace TaskbarSelect
{
    internal static class OwnedSnapRoutingTests
    {
        static int checks;
        static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        static OwnedSnapKeyboard.Target Target(int handle, long time)
        { return new OwnedSnapKeyboard.Target { Window = new IntPtr(handle), Published = time, State = new object() }; }
        internal static int Run()
        {
            try {
                var cache = new OwnedSnapTargetCache(100);
                var first = Target(1, 100); var other = Target(2, 110);
                Check(cache.Find(first.Window, 100) == null, "An unknown foreground stays native");
                cache.Add(first); cache.Add(other);
                Check(Object.ReferenceEquals(cache.Find(first.Window, 120), first), "Immediate focus return routes to that window, not last publication");
                Check(Object.ReferenceEquals(cache.Find(other.Window, 120), other), "Each known foreground has an independent route");
                Check(cache.Find(first.Window, 99) == null, "Clock rollback cannot renew a hint");
                Check(cache.Find(first.Window, 200) == first && cache.Find(first.Window, 201) == null, "Hint lease is finite and exact");
                Check(cache.Find(IntPtr.Zero, 120) == null && cache.Find(new IntPtr(3), 120) == null, "No default or unrelated target");
                cache.Add(null); cache.Add(new OwnedSnapKeyboard.Target { Window = IntPtr.Zero, Published = 120, State = new object() });
                cache.Add(new OwnedSnapKeyboard.Target { Window = new IntPtr(3), Published = 120 });
                Check(cache.Count == 2, "Incomplete metadata never creates a routing hint");
                cache.Remove(first.Window);
                Check(cache.Find(first.Window, 130) == null, "Observed unsupported foreground loses its hint");
                var replacement = Target(2, 140); cache.Add(replacement);
                Check(cache.Count == 1 && cache.Find(other.Window, 140) == replacement, "A new publication replaces the same handle's old snapshot");
                cache.Clear();
                Check(cache.Count == 0 && cache.Find(other.Window, 140) == null, "Settings invalidation drops every routing hint");
                for (int i = 1; i <= OwnedSnapTargetCache.Capacity + 1; i++) cache.Add(Target(i, 200 + i));
                Check(cache.Count == OwnedSnapTargetCache.Capacity, "Retention is bounded");
                Check(cache.Find(new IntPtr(1), 400) == null && cache.Find(new IntPtr(OwnedSnapTargetCache.Capacity + 1), 400) != null,
                    "Capacity pressure evicts oldest metadata and retains latest within lease");

                Check(OwnedSnapRuntimePolicy.CurrentNotice(5, 5, true), "Newly routed notice uses the current action epoch");
                Check(!OwnedSnapRuntimePolicy.CurrentNotice(4, 5, true), "Queued input from before pause/settings change cannot execute after resume");
                Check(!OwnedSnapRuntimePolicy.CurrentNotice(5, 5, false), "Epoch matching cannot revive disabled mode");
                var identity = new ShelfWindow { Handle = new IntPtr(12), Process = 8, ProcessStarted = 99, AppKey = "test" };
                var same = new ShelfWindow { Handle = identity.Handle, Process = 8, ProcessStarted = 99, AppKey = "test" };
                Check(WindowShelfPolicy.SameWindow(identity, same), "Fresh metadata can verify a routed identity");
                same.ProcessStarted++;
                Check(!WindowShelfPolicy.SameWindow(identity, same), "Recycled handle/process lifetime fails actual delivery identity gate");
                same.ProcessStarted = 99; same.Process++;
                Check(!WindowShelfPolicy.SameWindow(identity, same), "Cached handle cannot authorize a different process");
                Check(OwnedSnapRuntimePolicy.OrdinaryHint(OwnedSnapRoute.Ordinary) &&
                    !OwnedSnapRuntimePolicy.OrdinaryHint(OwnedSnapRoute.Administrator) && !OwnedSnapRuntimePolicy.OrdinaryHint((OwnedSnapRoute)99),
                    "HIGH and unknown permission routes cannot publish to the ordinary hook");
                Check(OwnedSnapRuntimePolicy.SameRoute(OwnedSnapRoute.Ordinary, 0, OwnedSnapRoute.Ordinary, 0), "Ordinary route has no administrator session");
                Check(!OwnedSnapRuntimePolicy.SameRoute(OwnedSnapRoute.Ordinary, 7, OwnedSnapRoute.Ordinary, 0), "Ordinary route cannot retain an administrator scope");
                Check(!OwnedSnapRuntimePolicy.SameRoute(OwnedSnapRoute.Ordinary, 0, OwnedSnapRoute.Administrator, 7), "Delivery cannot switch from ordinary to administrator");
                Check(!OwnedSnapRuntimePolicy.SameRoute(OwnedSnapRoute.Administrator, 7, OwnedSnapRoute.Ordinary, 0), "Delivery cannot switch from administrator to ordinary");
                Check(OwnedSnapRuntimePolicy.SameRoute(OwnedSnapRoute.Administrator, 7, OwnedSnapRoute.Administrator, 7) &&
                    !OwnedSnapRuntimePolicy.SameRoute(OwnedSnapRoute.Administrator, 7, OwnedSnapRoute.Administrator, 8) &&
                    !OwnedSnapRuntimePolicy.SameRoute(OwnedSnapRoute.Administrator, 0, OwnedSnapRoute.Administrator, 0),
                    "HIGH delivery and cycling require the same live positive session generation");
                Check(OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 7, 7, 1000, 2000, 1000, 2, 1, true), "A fresh HIGH notice is admitted through the exact one-second boundary");
                Check(!OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 7, 7, 1000, 2001, 1000, 2, 1, true), "HIGH notice older than one second cannot execute");
                Check(!OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 7, 7, 1000, 999, 1000, 2, 1, true), "Future or rollback-clock HIGH notice cannot execute");
                Check(!OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 7, 7, 0, 1000, 1000, 2, 1, true), "Missing notice creation proof refuses");
                Check(!OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 7, 7, 1000, 1100, 0, 2, 1, true), "Invalid notice clock frequency refuses");
                Check(!OwnedSnapRuntimePolicy.AdministratorNotice(4, 5, 7, 7, 1000, 1100, 1000, 2, 1, true), "Before-pause or settings-epoch HIGH notice cannot execute");
                Check(!OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 7, 8, 1000, 1100, 1000, 2, 1, true), "Reconnected helper cannot execute its predecessor's notice");
                Check(!OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 0, 0, 1000, 1100, 1000, 2, 1, true), "Inactive helper has no notice authority");
                Check(!OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 7, 7, 1000, 1100, 1000, 2, 1, false), "Matching metadata cannot bypass disabled experimental mode");
                Check(!OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 7, 7, 1000, 1100, 1000, 1, 1, true) &&
                    !OwnedSnapRuntimePolicy.AdministratorNotice(5, 5, 7, 7, 1000, 1100, 1000, 0, 0, true),
                    "Duplicate and zero notice IDs cannot execute");
                Check(OwnedSnapRuntimePolicy.NoticeTarget(identity.Handle, identity.Process, identity.ProcessStarted, identity), "Fresh HIGH capture can prove the notice's exact target");
                foreach (var wrong in new[] { new ShelfWindow { Handle = new IntPtr(99), Process = identity.Process, ProcessStarted = identity.ProcessStarted },
                    new ShelfWindow { Handle = identity.Handle, Process = identity.Process + 1, ProcessStarted = identity.ProcessStarted },
                    new ShelfWindow { Handle = identity.Handle, Process = identity.Process, ProcessStarted = identity.ProcessStarted + 1 } })
                    Check(!OwnedSnapRuntimePolicy.NoticeTarget(identity.Handle, identity.Process, identity.ProcessStarted, wrong), "Changed foreground handle, process or creation lifetime refuses HIGH notice");
                Check(!OwnedSnapRuntimePolicy.NoticeTarget(IntPtr.Zero, identity.Process, identity.ProcessStarted, identity) &&
                    !OwnedSnapRuntimePolicy.NoticeTarget(identity.Handle, 0, identity.ProcessStarted, identity) &&
                    !OwnedSnapRuntimePolicy.NoticeTarget(identity.Handle, identity.Process, 0, identity) &&
                    !OwnedSnapRuntimePolicy.NoticeTarget(identity.Handle, identity.Process, identity.ProcessStarted, null),
                    "Incomplete HIGH notice or capture cannot authorize a movement");
                Console.WriteLine("PASS: owned snap focus routing checks " + checks + "; no hooks or desktop input."); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
