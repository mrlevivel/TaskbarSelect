using System;

namespace TaskbarSelect
{
    internal static class SnapIntentPureSuite
    {
        internal static int Run()
        {
            try {
                SnapIntentPolicyTests.Run();
                SnapIntentAdversarialTests.Run();
                SnapIntentRuntimeTests.Run();
                SnapIntentRuntimeAdversarialTests.Run();
                SnapIntentActionBoundaryTests.Run();
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
