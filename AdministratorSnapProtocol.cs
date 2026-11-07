using System;
using System.Diagnostics;

namespace TaskbarSelect
{
    // Numeric format admission only. No hook, input, process or window access.
    // An admitted identity is still a hint; the runtime must verify authority.
    internal static class AdministratorSnapProtocol
    {
        internal const int ConfigurationLength = 8, ReplyLength = 10;

        static bool Boolean(long value) { return value == 0 || value == 1; }
        static bool Window(long value) { return value > 0 && value != 0xFFFF; }
        static bool Identity(long window, long process, long started)
        {
            return Window(window) && process > 0 && process <= UInt32.MaxValue &&
                started > 0 && started <= DateTime.MaxValue.Ticks;
        }
        static bool FutureSecond(long expires, long now)
        {
            if (expires <= now) return false;
            // Near Int64.MaxValue every representable later tick is within
            // the bound. Never overflow the comparison's upper endpoint.
            return now > Int64.MaxValue - Stopwatch.Frequency || expires <= now + Stopwatch.Frequency;
        }

        // [enabled, epoch, hwnd, pid, utcStarted, leaseExpires,
        //  rejectedHwnd, ackNoticeId]
        internal static bool ValidConfiguration(long[] values, long now)
        {
            if (values == null || values.Length != ConfigurationLength || !Boolean(values[0]) || values[7] < 0) return false;
            if (values[0] == 0) return values[1] >= 0 && values[2] == 0 && values[3] == 0 &&
                values[4] == 0 && values[5] == 0 && values[6] == 0;
            if (values[1] <= 0 || !FutureSecond(values[5], now) ||
                (values[6] != 0 && !Window(values[6]))) return false;
            return (values[2] == 0 && values[3] == 0 && values[4] == 0) || Identity(values[2], values[3], values[4]);
        }

        // [activeWorkers, uncertainWorkers, hookArmed, noticeId, epoch,
        //  hwnd, pid, utcStarted, leftBoolean, createdTicks]
        internal static bool ValidReply(long[] values)
        {
            if (values == null || values.Length != ReplyLength || values[0] < 0 || values[1] < 0 ||
                !Boolean(values[2]) || values[3] < 0 || !Boolean(values[8])) return false;
            if (values[3] == 0) {
                for (int i = 4; i < ReplyLength; i++) if (values[i] != 0) return false;
                return true;
            }
            return values[4] > 0 && Identity(values[5], values[6], values[7]) && values[9] > 0;
        }

        // Separate from packet format: the receiver supplies its current
        // monotonic clock and must not renew a notice after a clock rollback.
        internal static bool Fresh(long created, long now)
        {
            return created > 0 && now >= created && now - created <= Stopwatch.Frequency;
        }
    }
}
