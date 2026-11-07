using System;
using System.Web.Script.Serialization;

namespace TaskbarSelect
{
    internal static class AdministratorPreferenceIdentityTests
    {
        internal static void Run(Action<bool, string> check)
        {
            foreach (bool previous in new[] { false, true }) {
                var settings = new Settings { PersistentAdministratorSupport = previous, WindowShelfActivationHeight = 37 };
                var original = settings; var exclusions = settings.ExcludedApps; var monitors = settings.MonitorAliases;
                var serializer = new JavaScriptSerializer();
                string before = serializer.Serialize(settings); int calls = 0;
                bool changed = Controller.SaveAdministratorPreferenceValue(settings, previous, delegate { calls++; });
                check(!changed && calls == 0 && serializer.Serialize(settings) == before, "An unchanged permission preference performs no save or object replacement");
                bool failed = false;
                try { Controller.SaveAdministratorPreferenceValue(settings, !previous, delegate(Settings proposed) {
                    calls++;
                    check(!Object.ReferenceEquals(proposed, settings) && proposed.PersistentAdministratorSupport == !previous &&
                        settings.PersistentAdministratorSupport == previous, "The permission save receives a separate proposed snapshot before live intent changes");
                    throw new InvalidOperationException("Owned preference write failure");
                }); } catch (InvalidOperationException) { failed = true; }
                check(failed && serializer.Serialize(settings) == before && Object.ReferenceEquals(exclusions, settings.ExcludedApps),
                    "A failed preference write leaves live intent and movement exclusion identity unchanged");
                Settings written = null;
                changed = Controller.SaveAdministratorPreferenceValue(settings, !previous, delegate(Settings proposed) { calls++; written = proposed; });
                check(changed && written != null && written.PersistentAdministratorSupport == !previous && settings.PersistentAdministratorSupport == !previous,
                    "Successful save publishes only the confirmed permission intent");
                check(Object.ReferenceEquals(original, settings) && Object.ReferenceEquals(exclusions, settings.ExcludedApps) &&
                    Object.ReferenceEquals(monitors, settings.MonitorAliases) && settings.WindowShelfActivationHeight == 37,
                    "Saving administrator intent retains the live settings and unrelated collection identities");
                written.PersistentAdministratorSupport = previous;
                check(serializer.Serialize(written) == before, "The proposed permission save preserves every unrelated preference value");
            }
        }
    }
}
