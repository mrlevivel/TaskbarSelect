using System;
using System.Diagnostics;

namespace TaskbarSelect
{
    // Inert numeric fixtures only; no helper, hook, input or desktop access.
    internal static class AdministratorSnapProtocolTests
    {
        static int checks;
        static void Check(bool value, string message)
        { if (!value) throw new Exception(message); checks++; }
        static long[] Changed(long[] source, int field, long value)
        { var copy = (long[])source.Clone(); copy[field] = value; return copy; }

        internal static int Run()
        {
            checks = 0;
            try {
                Configuration(); Replies(); Freshness();
                Check(8 + AdministratorSnapProtocol.ConfigurationLength * 8 <= 128 &&
                    8 + AdministratorSnapProtocol.ReplyLength * 8 <= 128,
                    "Both fixed payloads fit the existing128-byte packet body cap");
                Console.WriteLine("PASS: administrator snap numeric protocol checks " + checks + "; no helper, hook or input.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        static void Configuration()
        {
            const long now = 1000;
            long second = Stopwatch.Frequency;
            long[] enabled = { 1, 7, 12, 3, 4, now + second, 0, 0 };
            long[] disabled = new long[AdministratorSnapProtocol.ConfigurationLength];
            Check(AdministratorSnapProtocol.ValidConfiguration(enabled, now), "A complete enabled identity admits the exact one-second lease boundary");
            Check(AdministratorSnapProtocol.ValidConfiguration(disabled, now), "An all-zero disabled packet is canonical");
            Check(AdministratorSnapProtocol.ValidConfiguration(new long[] { 0, Int64.MaxValue, 0, 0, 0, 0, 0, Int64.MaxValue }, now),
                "Disabled mode retains a nonnegative epoch and acknowledgement without a target or lease");
            Check(AdministratorSnapProtocol.ValidConfiguration(new long[] { 1, 7, 0, 0, 0, now + 1, 12, 8 }, now),
                "Enabled mode can retract a target while retaining a rejection hint and acknowledgement");
            Check(!AdministratorSnapProtocol.ValidConfiguration(null, now), "Null configuration refuses");
            foreach (int length in new[] { 0, 7, 9, 16 })
                Check(!AdministratorSnapProtocol.ValidConfiguration(new long[length], now), "Configuration arity is exact at " + length);
            foreach (long value in new[] { -1L, 2L, Int64.MaxValue })
                Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 0, value), now), "Enabled is a strict Boolean at " + value);
            foreach (long value in new[] { -1L, 0L })
                Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 1, value), now), "Enabled epochs must be positive at " + value);
            Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(disabled, 1, -1), now), "Disabled epoch cannot be negative");
            for (int field = 2; field <= 6; field++)
                Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(disabled, field, 1), now), "Disabled mode forbids leftover target/lease/rejection data at " + field);
            foreach (long value in new[] { -1L, 0L, 0xFFFFL })
                Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 2, value), now), "Incomplete or special target HWND refuses at " + value);
            foreach (long value in new[] { -1L, 0L, (long)UInt32.MaxValue + 1 })
                Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 3, value), now), "Target PID is a positive UInt32 at " + value);
            foreach (long value in new[] { -1L, 0L, DateTime.MaxValue.Ticks + 1, Int64.MaxValue })
                Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 4, value), now), "Target creation time is positive UTC ticks at " + value);
            Check(AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 2, Int64.MaxValue), now), "Positive nonspecial HWND format does not invent a native validity proof");
            Check(AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 3, UInt32.MaxValue), now), "Largest UInt32 PID is in format");
            Check(AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 4, DateTime.MaxValue.Ticks), now), "Largest representable UTC creation time is in format");
            Check(!AdministratorSnapProtocol.ValidConfiguration(new long[] { 1, 7, 0, 3, 0, now + 1, 0, 0 }, now) &&
                !AdministratorSnapProtocol.ValidConfiguration(new long[] { 1, 7, 0, 0, 4, now + 1, 0, 0 }, now), "A zero target requires the entire identity triple to be zero");
            foreach (long value in new[] { Int64.MinValue, 0L, now - 1, now, now + second + 1, Int64.MaxValue })
                Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 5, value), now), "Expired or overlong lease refuses at " + value);
            Check(AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 5, now + 1), now), "The next monotonic tick is a live lease");
            foreach (long value in new[] { -1L, 0xFFFFL })
                Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 6, value), now), "Rejected HWND must be zero or positive nonspecial at " + value);
            Check(AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 6, Int64.MaxValue), now), "Rejected HWND format accepts a positive nonspecial value");
            Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 7, -1), now) &&
                !AdministratorSnapProtocol.ValidConfiguration(Changed(disabled, 7, -1), now), "Acknowledgement cannot be negative in either mode");
            Check(AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 7, Int64.MaxValue), now), "Largest acknowledgement stays numeric");
            Check(AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 5, Int64.MaxValue), Int64.MaxValue - 1),
                "Near the clock maximum a representable future lease admits without endpoint overflow");
            Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 5, Int64.MaxValue), Int64.MaxValue), "No future lease exists at the maximum clock tick");
            Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 5, Int64.MinValue), Int64.MaxValue - 1), "Wrapped future lease cannot become current");
            Check(AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 5, Int64.MinValue + second), Int64.MinValue),
                "The signed clock minimum still admits its exact one-second numeric lease without subtraction overflow");
            Check(!AdministratorSnapProtocol.ValidConfiguration(Changed(enabled, 5, Int64.MaxValue), Int64.MinValue),
                "An extreme signed span cannot wrap into the lease bound");
        }

        static void Replies()
        {
            long[] reply = { 0, 0, 1, 9, 7, 12, 3, 4, 1, 5 };
            long[] empty = new long[AdministratorSnapProtocol.ReplyLength];
            Check(AdministratorSnapProtocol.ValidReply(reply), "A complete notice is valid numeric format");
            Check(AdministratorSnapProtocol.ValidReply(empty) && AdministratorSnapProtocol.ValidReply(Changed(empty, 2, 1)),
                "No-notice replies may report either armed state");
            Check(AdministratorSnapProtocol.ValidReply(Changed(reply, 2, 0)), "An already queued notice can be reported after disarming");
            Check(AdministratorSnapProtocol.ValidReply(Changed(reply, 8, 0)), "Right and left use strict numeric Booleans");
            Check(!AdministratorSnapProtocol.ValidReply(null), "Null reply refuses");
            foreach (int length in new[] { 0, 9, 11, 16 })
                Check(!AdministratorSnapProtocol.ValidReply(new long[length]), "Reply arity is exact at " + length);
            foreach (int field in new[] { 0, 1 }) {
                Check(!AdministratorSnapProtocol.ValidReply(Changed(reply, field, -1)), "Worker counts cannot be negative at " + field);
                Check(AdministratorSnapProtocol.ValidReply(Changed(reply, field, Int64.MaxValue)), "Worker count format is nonnegative at " + field);
            }
            foreach (int field in new[] { 2, 8 }) foreach (long value in new[] { -1L, 2L, Int64.MaxValue })
                Check(!AdministratorSnapProtocol.ValidReply(Changed(reply, field, value)), "Reply Boolean field refuses out-of-range value at " + field + "/" + value);
            Check(!AdministratorSnapProtocol.ValidReply(Changed(reply, 3, -1)), "Notice identifier cannot be negative");
            Check(AdministratorSnapProtocol.ValidReply(Changed(reply, 3, Int64.MaxValue)), "Notice identifiers retain their full positive numeric range");
            for (int field = 4; field <= 9; field++)
                Check(!AdministratorSnapProtocol.ValidReply(Changed(empty, field, 1)), "No-notice packet cannot smuggle notice fields at " + field);
            foreach (long value in new[] { -1L, 0L })
                Check(!AdministratorSnapProtocol.ValidReply(Changed(reply, 4, value)), "Notice epoch is positive at " + value);
            foreach (long value in new[] { -1L, 0L, 0xFFFFL })
                Check(!AdministratorSnapProtocol.ValidReply(Changed(reply, 5, value)), "Notice HWND must be positive nonspecial at " + value);
            foreach (long value in new[] { -1L, 0L, (long)UInt32.MaxValue + 1 })
                Check(!AdministratorSnapProtocol.ValidReply(Changed(reply, 6, value)), "Notice PID is a positive UInt32 at " + value);
            foreach (long value in new[] { -1L, 0L, DateTime.MaxValue.Ticks + 1, Int64.MaxValue })
                Check(!AdministratorSnapProtocol.ValidReply(Changed(reply, 7, value)), "Notice creation identity must use representable positive UTC ticks at " + value);
            foreach (long value in new[] { Int64.MinValue, -1L, 0L })
                Check(!AdministratorSnapProtocol.ValidReply(Changed(reply, 9, value)), "Notice monotonic creation tick must be positive at " + value);
            Check(AdministratorSnapProtocol.ValidReply(new long[] { Int64.MaxValue, Int64.MaxValue, 1, Int64.MaxValue,
                Int64.MaxValue, Int64.MaxValue, UInt32.MaxValue, DateTime.MaxValue.Ticks, 0, Int64.MaxValue }),
                "Maximum in-format identity/notice values stay separate from runtime authorization and freshness");
            Check(AdministratorSnapProtocol.ValidReply(reply) && !AdministratorSnapProtocol.Fresh(reply[9], reply[9] + Stopwatch.Frequency + 1),
                "A stale notice can be valid format; freshness is a separate receiver decision");
        }

        static void Freshness()
        {
            const long created = 1000;
            Check(AdministratorSnapProtocol.Fresh(created, created), "A notice is fresh at creation");
            Check(AdministratorSnapProtocol.Fresh(created, created + Stopwatch.Frequency), "The exact one-second age boundary is inclusive");
            Check(!AdministratorSnapProtocol.Fresh(created, created + Stopwatch.Frequency + 1), "One tick beyond the age bound is stale");
            Check(!AdministratorSnapProtocol.Fresh(created, created - 1), "Clock rollback never renews a notice");
            foreach (long invalid in new[] { Int64.MinValue, -1L, 0L })
                Check(!AdministratorSnapProtocol.Fresh(invalid, created), "A nonpositive creation tick never becomes fresh at " + invalid);
            Check(!AdministratorSnapProtocol.Fresh(1, Int64.MaxValue), "An extreme positive elapsed time does not overflow into freshness");
            Check(!AdministratorSnapProtocol.Fresh(Int64.MaxValue, Int64.MinValue), "A wrapped clock is a rollback");
            Check(AdministratorSnapProtocol.Fresh(Int64.MaxValue, Int64.MaxValue), "The largest positive equal clock is fresh without addition");
            Check(AdministratorSnapProtocol.Fresh(Int64.MaxValue - Stopwatch.Frequency, Int64.MaxValue), "Freshness at the maximum clock retains its exact age boundary");
        }
    }
}
