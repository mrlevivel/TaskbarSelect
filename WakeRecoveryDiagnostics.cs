using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TaskbarSelect
{
    // Private latest-snapshot diagnostics only, with numeric policy/geometry data.
    // Never names, HWND/PID identities, titles, classes, content or input data.
    internal static class WakeRecoveryDiagnostics
    {
        internal const int MaximumBytes = 16384;
        internal static bool Sample(bool isTest, long now, long frequency, ref long last,
            Dictionary<string, long> workAreas, Dictionary<string, long> snap, Action<string> write)
        {
            if (!isTest || now < 0 || frequency <= 0 || write == null) return false;
            if (last >= 0 && now < last) { last = now; return false; }
            if (last >= 0 && now - last < frequency) return false;
            last = now;
            try {
                string json = new JavaScriptSerializer().Serialize(new {
                    MonotonicTicks = now, Frequency = frequency, WorkAreas = workAreas, Snap = snap
                });
                if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) return false;
                write(json); return true;
            } catch (Exception) { return false; }
        }
        internal static Action<Dictionary<string, long>, Dictionary<string, long>> Writer(string path)
        {
            long last = -1;
            return delegate(Dictionary<string, long> workAreas, Dictionary<string, long> snap) {
                Sample(AppProfile.IsTest, Stopwatch.GetTimestamp(), Stopwatch.Frequency, ref last, workAreas, snap, delegate(string json) {
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, json);
                    if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                });
            };
        }
        internal static int RunPureTest()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool value, string name) { if (!value) throw new Exception(name); count++; };
            try {
                long last = -1; int writes = 0; string json = null;
                var numbers = new Dictionary<string, long> { { "Attempts", 3 }, { "Accepted", 2 } };
                check(!Sample(false, 1000, 1000, ref last, numbers, numbers, delegate { writes++; }), "Production refuses the sink before serialization or writing");
                check(writes == 0 && last == -1, "Production refusal does not change writer cadence");
                check(Sample(true, 1000, 1000, ref last, numbers, null, delegate(string value) { writes++; json = value; }), "First private snapshot writes injected numeric data");
                check(json.Contains("\"Attempts\":3") && json.Contains("\"Snap\":null") && Encoding.UTF8.GetByteCount(json) < MaximumBytes,
                    "Snapshot contains bounded numeric totals and explicit unavailable snap data");
                for (int i = 0; i < 1000; i++) Sample(true, 1000 + i, 1000, ref last, numbers, numbers, delegate { writes++; });
                check(writes == 1, "One thousand sub-second requests perform one write");
                check(Sample(true, 2000, 1000, ref last, numbers, numbers, delegate { writes++; }), "One second boundary permits one fresh snapshot");
                check(!Sample(true, 1000, 1000, ref last, numbers, numbers, delegate { writes++; }) && writes == 2, "Clock rollback resets cadence without writing");
                last = -1;
                check(!Sample(true, 1000, 1000, ref last, numbers, numbers, delegate { throw new IOException("Injected write failure"); }), "Diagnostic I/O failure never escapes into app behavior");
                check(!Sample(true, 1001, 1000, ref last, numbers, numbers, delegate { writes++; }) && writes == 2, "Failed write still consumes its cadence slot");
                var large = new Dictionary<string, long> { { new string('x', MaximumBytes), 1 } }; last = -1;
                check(!Sample(true, 1000, 1000, ref last, large, null, delegate { writes++; }) && writes == 2, "Oversize snapshot is rejected before I/O");
                var fixture = new WorkAreaGuardTests.Fixture(); fixture.Update();
                var snapshot = fixture.Areas.DiagnosticSnapshot();
                check(snapshot["Claims"] == 1 && snapshot["NativeAttempts"] == 0 && snapshot["RecoveryExpansionAttempts"] == 0,
                    "Injected work-area snapshot uses only memory and never invokes native platform methods");
                Console.WriteLine("PASS: " + count + " pure private diagnostic checks; no filesystem, UI or native access."); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
