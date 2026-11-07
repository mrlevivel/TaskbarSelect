using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TaskbarSelect
{
    // Numeric, bounded, advisory evidence. No field is an instruction or proof
    // of quiescence, and parsing never releases a lease or changes approval.
    internal static class NativeStopDiagnostics
    {
        internal const int RecordLength = 160;
        const long Epoch = 504911232000000000L;
        internal sealed class Record
        {
            internal readonly uint Reason, DrainPhase, Win32Error, ActiveProcesses, PayloadWait, PayloadExit;
            internal readonly uint IoWait, IoCancelError, NativeResult, Flags, PayloadExitBeforeDrain, IoCancelWait, ErrorPhase, StopCode;
            internal readonly ulong DrainMilliseconds, RuntimeMilliseconds;
            internal Record(byte[] bytes)
            {
                Reason = U32(bytes, 12); DrainPhase = U32(bytes, 16); Win32Error = U32(bytes, 20);
                ActiveProcesses = U32(bytes, 24); PayloadWait = U32(bytes, 28); PayloadExit = U32(bytes, 32);
                IoWait = U32(bytes, 36); IoCancelError = U32(bytes, 40); NativeResult = U32(bytes, 44);
                DrainMilliseconds = U64(bytes, 48); RuntimeMilliseconds = U64(bytes, 56);
                Flags = U32(bytes, 136); PayloadExitBeforeDrain = U32(bytes, 140);
                IoCancelWait = U32(bytes, 144); ErrorPhase = U32(bytes, 148); StopCode = U32(bytes, 152);
            }
            internal string Summary()
            {
                return String.Format(CultureInfo.InvariantCulture,
                    "nativeStop={0} drainPhase={1} win32={2} errorPhase={3} active={4} payloadWait={5} payloadExit={6} payloadBeforeDrain={7} drainMs={8} runtimeMs={9} ioWait={10} ioCancelError={11} ioCancelWait={12} nativeResult={13} stopCode={14} flags={15}",
                    Reason, DrainPhase, Win32Error, ErrorPhase, ActiveProcesses, PayloadWait, PayloadExit, PayloadExitBeforeDrain,
                    DrainMilliseconds, RuntimeMilliseconds, IoWait, IoCancelError, IoCancelWait, NativeResult, StopCode, Flags);
            }
        }
        internal static Record Parse(byte[] bytes, string expectedPayloadHash, string expectedNonce,
            uint nativePid, long nativeStarted, uint parentPid, long parentStarted)
        {
            if (bytes == null || bytes.Length != RecordLength || !HexText(expectedPayloadHash, 64, false) ||
                !HexText(expectedNonce, 32, true) || nativePid == 0 || parentPid == 0 || nativePid == parentPid ||
                nativeStarted <= Epoch || nativeStarted > DateTime.MaxValue.Ticks || parentStarted <= Epoch || parentStarted > nativeStarted)
                throw Invalid();
            byte[] magic = Encoding.ASCII.GetBytes("TSAND01\0");
            for (int index = 0; index < magic.Length; index++) if (bytes[index] != magic[index]) throw Invalid();
            if (U32(bytes, 8) != RecordLength || U32(bytes, 64) != nativePid || U32(bytes, 68) != parentPid ||
                U64(bytes, 72) != (ulong)nativeStarted || U64(bytes, 80) != (ulong)parentStarted || U32(bytes, 156) != 0 ||
                !HexMatches(bytes, 88, expectedNonce) || !HexMatches(bytes, 104, expectedPayloadHash)) throw Invalid();
            var record = new Record(bytes);
            if (record.Reason < 1 || record.Reason > 7 || record.DrainPhase < 1 || record.DrainPhase > 8 || record.ErrorPhase > 8 ||
                record.NativeResult != 90 || record.Flags > 3 || record.DrainMilliseconds > record.RuntimeMilliseconds ||
                !ProcessWait(record.PayloadWait) || !IoWait(record.IoWait) || !ProcessWait(record.IoCancelWait) ||
                (record.StopCode != 0 && (record.StopCode < 100 || record.StopCode > 108)) ||
                (record.ErrorPhase != 0 && record.Win32Error == 0) ||
                (record.DrainPhase == 6 && (record.Flags & 2) == 0) ||
                (record.DrainPhase != 6 && record.DrainPhase != 8 && (record.Flags & 2) != 0) ||
                ((record.Reason == 7) != (record.DrainPhase == 8)) ||
                (record.Reason == 7 ? record.IoWait == 0 || record.IoCancelWait == 0 : record.IoWait != 0 || record.IoCancelWait != 0 || record.IoCancelError != 0))
                throw Invalid();
            return record;
        }
        static IOException Invalid() { return new IOException("The native stop diagnostic is unavailable or invalid."); }
        static uint U32(byte[] bytes, int at)
        { return (uint)bytes[at] | (uint)bytes[at + 1] << 8 | (uint)bytes[at + 2] << 16 | (uint)bytes[at + 3] << 24; }
        static ulong U64(byte[] bytes, int at)
        { return (ulong)U32(bytes, at) | (ulong)U32(bytes, at + 4) << 32; }
        static bool ProcessWait(uint value) { return value == 0 || value == 258 || value == UInt32.MaxValue; }
        static bool IoWait(uint value)
        { return value <= 2 || (value >= 128 && value <= 130) || value == 258 || value == UInt32.MaxValue; }
        static bool HexText(string value, int length, bool nonzero)
        {
            if (value == null || value.Length != length) return false;
            bool any = false;
            foreach (char item in value) { if (!(item >= '0' && item <= '9') && !(item >= 'a' && item <= 'f')) return false; if (item != '0') any = true; }
            return !nonzero || any;
        }
        static bool HexMatches(byte[] bytes, int offset, string value)
        {
            const string alphabet = "0123456789abcdef";
            for (int index = 0; index < value.Length / 2; index++)
                if (value[index * 2] != alphabet[bytes[offset + index] >> 4] || value[index * 2 + 1] != alphabet[bytes[offset + index] & 15]) return false;
            return true;
        }
    }
}
