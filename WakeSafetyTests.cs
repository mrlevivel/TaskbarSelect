using System;

namespace TaskbarSelect
{
    // Purposefully excludes real taskbars, UIA providers, desktop hooks, visible
    // windows, input and display-power changes. Native policy fixtures may read
    // only the test process's own identity through their existing guards.
    internal static class WakeSafetyTests
    {
        internal static int Run()
        {
            int checks = 0;
            Action<bool, string> check = delegate(bool value, string name) {
                if (!value) throw new Exception(name);
                checks++;
            };
            try {
                TaskbarHideRequestsTests.Run(check);
                HoverQueryPolicyTests.Run(check);
                HoverQueryIntegrationTests.Run(check);
                TaskbarTargetRecoveryTests.Run(check);
                MovementFixTests.RunManaged(check);
                DeferredTaskbarClickTests.Run(check);
                TaskbarNativeSelectionTests.Run(check);
                Console.WriteLine("PASS: " + checks + " isolated wake-safety and click-policy checks; no desktop/provider/input tests.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
