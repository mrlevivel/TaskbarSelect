using System;

namespace TaskbarSelect
{
    internal static class MouseHookPacketPolicyTests
    {
        internal static void Run(Action<bool, string> check)
        {
            check(MouseHookPacketPolicy.Current(3, 3, 1000, 1000, 1000, true), "Current generation accepts the exact registration tick");
            check(!MouseHookPacketPolicy.Current(2, 3, 1100, 1000, 1100, true), "Retired generation cannot create a new gesture");
            check(!MouseHookPacketPolicy.Current(3, 3, 999, 1000, 1100, true), "Queued pre-registration input passes through without movement");
            check(MouseHookPacketPolicy.Current(3, 3, 1100, 1000, 1200, true), "Fresh current-generation input is admitted after registration");
            check(!MouseHookPacketPolicy.Current(3, 3, 1100, 1000, 3101, false), "Stale native input cannot create a delayed gesture");
            check(!MouseHookPacketPolicy.Current(3, 3, 1201, 1000, 1200, true), "Future-dated input cannot create a gesture");
            check(MouseHookPacketPolicy.Current(3, 3, 20, UInt32.MaxValue - 30, 25, true), "A fresh packet across native tick wrap remains valid");
            check(!MouseHookPacketPolicy.Current(3, 3, UInt32.MaxValue - 31, UInt32.MaxValue - 30, 25, true), "A queued pre-install packet remains stale across tick wrap");
            check(MouseHookPacketPolicy.Current(3, 3, 0x80000020, 1, 0x80000030, false), "More than 24.9 days of uptime cannot permanently reject new input");
            check(MouseHookPacketPolicy.Current(3, 3, 15, 20, 25, false), "Expired registration fence cannot recur after a full native tick wrap");
        }
    }
}
