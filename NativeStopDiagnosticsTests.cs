using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace TaskbarSelect
{
    // Inert numeric byte fixtures only; never writes a protected cache, creates
    // a task, starts a native helper, or changes approval/recovery state.
    internal static class NativeStopDiagnosticsTests
    {
        const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string Nonce = "0123456789abcdef0123456789abcdef";
        const long NativeStarted = 638959000000000000L, ParentStarted = 638958999000000000L;
        static void W32(byte[] bytes, int at, uint value)
        { for (int index = 0; index < 4; index++) bytes[at + index] = (byte)(value >> (index * 8)); }
        static void W64(byte[] bytes, int at, ulong value)
        { for (int index = 0; index < 8; index++) bytes[at + index] = (byte)(value >> (index * 8)); }
        static byte[] Fixture()
        {
            var bytes = new byte[NativeStopDiagnostics.RecordLength];
            System.Text.Encoding.ASCII.GetBytes("TSAND01\0").CopyTo(bytes, 0);
            W32(bytes, 8, (uint)bytes.Length); W32(bytes, 12, 5); W32(bytes, 16, 7); W32(bytes, 20, 1460);
            W32(bytes, 24, 1); W32(bytes, 28, 0); W32(bytes, 32, 200); W32(bytes, 44, 90);
            W64(bytes, 48, 2508); W64(bytes, 56, 43200123); W32(bytes, 64, 17); W32(bytes, 68, 23);
            W64(bytes, 72, (ulong)NativeStarted); W64(bytes, 80, (ulong)ParentStarted);
            for (int index = 0; index < 16; index++) bytes[88 + index] = (byte)((index % 8) * 0x22 + 1);
            for (int index = 0; index < 32; index++) bytes[104 + index] = (byte)((index % 8) * 0x22 + 1);
            W32(bytes, 136, 1); W32(bytes, 140, 200); W32(bytes, 148, 7); return bytes;
        }
        static NativeStopDiagnostics.Record Parse(byte[] bytes)
        { return NativeStopDiagnostics.Parse(bytes, Hash, Nonce, 17, NativeStarted, 23, ParentStarted); }
        static bool Reject(byte[] bytes)
        { try { Parse(bytes); return false; } catch (IOException) { return true; } }
        static bool BadIdentity(string hash, string nonce, uint nativePid, long nativeStarted, uint parentPid, long parentStarted)
        { try { NativeStopDiagnostics.Parse(Fixture(), hash, nonce, nativePid, nativeStarted, parentPid, parentStarted); return false; } catch (IOException) { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            byte[] bytes = Fixture(); NativeStopDiagnostics.Record record = Parse(bytes);
            check(record.Reason == 5 && record.DrainPhase == 7 && record.Win32Error == 1460 && record.ActiveProcesses == 1 &&
                record.PayloadWait == 0 && record.PayloadExit == 200 && record.PayloadExitBeforeDrain == 200 && record.NativeResult == 90 &&
                record.DrainMilliseconds == 2508 && record.RuntimeMilliseconds == 43200123 && record.Flags == 1,
                "An overnight payload-exit/job-drain timeout retains exact numeric observations and never becomes a success receipt");
            bytes[12] = 1;
            check(record.Reason == 5, "A parsed stop record owns numeric values and is unaffected by later caller byte mutations");
            bytes = Fixture(); W32(bytes, 12, 7); W32(bytes, 16, 8); W32(bytes, 24, UInt32.MaxValue);
            W32(bytes, 28, 258); W32(bytes, 32, 259); W32(bytes, 36, 2); W32(bytes, 40, 1168);
            W32(bytes, 140, 259); W32(bytes, 144, 258); W32(bytes, 148, 8); W32(bytes, 152, 108);
            record = Parse(bytes);
            check(record.Reason == 7 && record.DrainPhase == 8 && record.IoWait == 2 && record.IoCancelWait == 258 &&
                record.IoCancelError == 1168 && record.StopCode == 108 && record.ActiveProcesses == UInt32.MaxValue,
                "The exactIo hard-exit branch preserves child-wait/cancel timeout and unknown job count without a false drain claim");
            W32(bytes, 136, 3);
            check(Parse(bytes).NativeResult == 90 && Parse(bytes).Flags == 3,
                "An acknowledgement-write cancellation remains Exit90 even when the earlier native drain observed zero descendants");
            bytes = Fixture(); W32(bytes, 12, 1); W32(bytes, 16, 2); W32(bytes, 20, 5); W32(bytes, 28, 258);
            W32(bytes, 32, 259); W32(bytes, 140, 259); W32(bytes, 148, 2); W32(bytes, 152, 107);
            check(Parse(bytes).Win32Error == 5 && Parse(bytes).ErrorPhase == 2 && Parse(bytes).StopCode == 107,
                "Job termination refusal retains its own Win32 error and setup failure separately from the required Exit90");
            check(Reject(null) && Reject(new byte[159]) && Reject(new byte[161]) && Reject(new byte[4096]),
                "Missing, incomplete, overlong and oversized records are discarded before any field is read");
            foreach (int offset in new[] { 0, 6, 7, 8, 64, 68, 72, 80, 88, 103, 104, 135, 156, 159 }) {
                bytes = Fixture(); bytes[offset] ^= 1;
                check(Reject(bytes), "Header, version, size, PID/ticks, nonce/hash and reserved-byte mismatches cannot impersonate another retained session");
            }
            foreach (Action<byte[]> mutate in new Action<byte[]>[] {
                b => W32(b, 12, 0), b => W32(b, 12, 8), b => W32(b, 16, 0), b => W32(b, 16, 9),
                b => W32(b, 148, 9), b => W32(b, 44, 0), b => W32(b, 44, 109), b => W32(b, 136, 4),
                b => W64(b, 48, 43200124), b => W32(b, 28, 1), b => W32(b, 36, 3), b => W32(b, 144, 1),
                b => W32(b, 152, 90), b => W32(b, 152, 109), b => W32(b, 20, 0),
                b => W32(b, 16, 6), b => W32(b, 136, 3), b => W32(b, 12, 7), b => W32(b, 16, 8),
                b => W32(b, 40, 1168), b => W32(b, 36, 258), b => W32(b, 144, 258) }) {
                bytes = Fixture(); mutate(bytes); check(Reject(bytes), "Out-of-domain or contradictory numeric stop/cancel fields are rejected as unavailable evidence");
            }
            foreach (string hash in new[] { null, "bad", Hash.ToUpperInvariant(), new string('0', 64), new string('g', 64) })
                check(BadIdentity(hash, Nonce, 17, NativeStarted, 23, ParentStarted), "The reader accepts only the exact independently expected canonical payload digest");
            foreach (string nonce in new[] { null, "bad", Nonce.ToUpperInvariant(), new string('0', 32), new string('g', 32), Nonce.Substring(0, 31) + "0" })
                check(BadIdentity(Hash, nonce, 17, NativeStarted, 23, ParentStarted), "A trace cannot cross a nonce boundary or establish its own expected session identity");
            check(BadIdentity(Hash, Nonce, 0, NativeStarted, 23, ParentStarted) && BadIdentity(Hash, Nonce, 23, NativeStarted, 23, ParentStarted) &&
                BadIdentity(Hash, Nonce, 17, 504911232000000000L, 23, ParentStarted) && BadIdentity(Hash, Nonce, 17, Int64.MaxValue, 23, ParentStarted) &&
                BadIdentity(Hash, Nonce, 17, NativeStarted, 0, ParentStarted) && BadIdentity(Hash, Nonce, 17, NativeStarted, 23, NativeStarted + 1) &&
                BadIdentity(Hash, Nonce, 18, NativeStarted, 23, ParentStarted) && BadIdentity(Hash, Nonce, 17, NativeStarted + 1, 23, ParentStarted) &&
                BadIdentity(Hash, Nonce, 17, NativeStarted, 24, ParentStarted) && BadIdentity(Hash, Nonce, 17, NativeStarted, 23, ParentStarted - 1),
                "Malformed, reused or differently created native/controller process identities do not admit a record");
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try { Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA"); record = Parse(Fixture());
                string summary = record.Summary();
                check(summary.Contains("nativeStop=5") && summary.Contains("drainMs=2508") && summary.Contains("runtimeMs=43200123") &&
                    !summary.Contains(Hash) && !summary.Contains(Nonce) && !summary.Contains("\\"),
                    "Diagnostic summaries are invariant numeric metadata and never expose a nonce, hash, user path or command");
            } finally { Thread.CurrentThread.CurrentCulture = original; }
        }
    }
}
