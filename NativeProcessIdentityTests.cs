using System;
using System.Diagnostics;
using System.IO;

namespace TaskbarSelect
{
    // Metadata reads of the owned test process plus pure value policies. These
    // tests create no app window/process, request no UAC and change no token,
    // privilege, security setting, Startup entry or user profile.
    internal static class NativeProcessIdentityTests
    {
        internal static void Run(Action<bool, string> check)
        {
            CreationRepresentation(check);
            IntegrityPolicy(check);
            OwnedProcess(check);
        }
        static void CreationRepresentation(Action<bool, string> check)
        {
            foreach (DateTime timestamp in new[] {
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 12, 12, 34, 56, DateTimeKind.Utc).AddTicks(12345),
                DateTime.MaxValue.ToUniversalTime()
            }) {
                ulong raw = unchecked((ulong)timestamp.ToFileTimeUtc());
                var native = new NativeProcessIdentity.FileTime { Low = (uint)raw, High = (uint)(raw >> 32) };
                check(NativeProcessIdentity.CreationTicks(native) == timestamp.Ticks,
                    "Native creation metadata preserves UTC DateTime ticks rather than changing the existing recovery identity epoch");
            }
            check(NativeProcessIdentity.CreationTicks(new NativeProcessIdentity.FileTime()) == 0,
                "An unavailable zero creation time cannot become a valid process identity");
            check(NativeProcessIdentity.CreationTicks(new NativeProcessIdentity.FileTime { Low = UInt32.MaxValue, High = UInt32.MaxValue }) == 0 &&
                NativeProcessIdentity.CreationTicks(new NativeProcessIdentity.FileTime { Low = UInt32.MaxValue, High = 0x7fffffff }) == 0,
                "Unsigned-overflow and out-of-DateTime-range native creation values are safely rejected");
        }
        static NativeProcessIdentity.Info Level(uint value, bool available)
        { return new NativeProcessIdentity.Info { Process = 1, Started = 1, IntegrityLevel = value, IntegrityAvailable = available }; }
        static void IntegrityPolicy(Action<bool, string> check)
        {
            uint[] levels = { NativeProcessIdentity.Untrusted, NativeProcessIdentity.Low, NativeProcessIdentity.Medium,
                NativeProcessIdentity.MediumPlus, NativeProcessIdentity.High, NativeProcessIdentity.System, UInt32.MaxValue };
            foreach (uint current in levels) foreach (uint target in levels) {
                check(NativeProcessIdentity.RequiresAdministrator(current, target) == (target > current) &&
                    NativeProcessIdentity.RequiresAdministrator(Level(current, true), Level(target, true)) == (target > current),
                    "The broker is needed only for a known strictly higher target integrity: current=" + current + ", target=" + target);
                check(!NativeProcessIdentity.RequiresAdministrator(Level(current, false), Level(target, true)) &&
                    !NativeProcessIdentity.RequiresAdministrator(Level(current, true), Level(target, false)),
                    "Unavailable token integrity cannot silently authorize a higher-integrity route: current=" + current + ", target=" + target);
            }
            check(!NativeProcessIdentity.RequiresAdministrator(null, Level(NativeProcessIdentity.High, true)) &&
                !NativeProcessIdentity.RequiresAdministrator(Level(NativeProcessIdentity.Medium, true), null),
                "Missing process metadata cannot authorize a broker request");
            var medium = Level(NativeProcessIdentity.Medium, true); medium.Elevated = true; medium.ElevationAvailable = true;
            var high = Level(NativeProcessIdentity.High, true); high.Elevated = false; high.ElevationAvailable = true;
            check(NativeProcessIdentity.RequiresAdministrator(medium, high) && !NativeProcessIdentity.RequiresAdministrator(high, medium),
                "Integrity comparison determines routing independently of an administrator-group or elevation flag");
        }
        static void OwnedProcess(Action<bool, string> check)
        {
            using (var process = Process.GetCurrentProcess()) {
                uint pid = (uint)process.Id;
                long expected = process.StartTime.ToUniversalTime().Ticks;
                NativeProcessIdentity.Info current = NativeProcessIdentity.Read(pid);
                check(current.Process == pid && current.Started == expected && NativeProcessIdentity.Started(pid) == expected,
                    "Limited native process metadata agrees exactly with the owned process's prior StartTime identity");
                check(current.IntegrityAvailable && current.ElevationAvailable && current.Error == 0,
                    "The owned process token exposes integrity and elevation using query-only access");
                string path = NativeProcessIdentity.Path(pid), expectedPath = process.MainModule.FileName;
                check(!String.IsNullOrWhiteSpace(path) && String.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase),
                    "The limited native image-name query returns the owned executable path without reading process modules");
                NativeProcessIdentity.Info own = NativeProcessIdentity.Current;
                check(own.Process == pid && own.Started == expected && own.IntegrityAvailable && own.ElevationAvailable &&
                    own.IntegrityLevel == current.IntegrityLevel && own.Elevated == current.Elevated && !NativeProcessIdentity.RequiresAdministrator(pid),
                    "Own integrity classification is stable and never routes the owned process through elevation");
                own.IntegrityLevel = UInt32.MaxValue; own.IntegrityAvailable = false; own.Started = 0;
                NativeProcessIdentity.Info retained = NativeProcessIdentity.Current;
                check(retained.Started == expected && retained.IntegrityAvailable && retained.IntegrityLevel == current.IntegrityLevel &&
                    !Object.ReferenceEquals(own, retained),
                    "Reading own metadata returns independent snapshots so callers cannot alter the cached integrity policy");
                for (int sample = 0; sample < 25; sample++) {
                    var repeated = NativeProcessIdentity.Read(pid);
                    check(repeated.Started == expected && repeated.IntegrityLevel == current.IntegrityLevel && repeated.IntegrityAvailable &&
                        repeated.Elevated == current.Elevated && repeated.ElevationAvailable && repeated.Error == 0,
                        "Repeated query-only owned identity reads retain exact metadata and safely release their handles: " + sample);
                }
            }
            var absent = NativeProcessIdentity.Read(0);
            check(absent.Started == 0 && !absent.IntegrityAvailable && !absent.ElevationAvailable && absent.Error != 0 &&
                NativeProcessIdentity.Started(0) == 0 && NativeProcessIdentity.Path(0) == null && !NativeProcessIdentity.RequiresAdministrator(0),
                "A missing PID is rejected before metadata access or elevation routing");
            var invalid = NativeProcessIdentity.Read(UInt32.MaxValue);
            check(invalid.Started == 0 && !invalid.IntegrityAvailable && !invalid.ElevationAvailable && invalid.Error != 0 &&
                NativeProcessIdentity.Started(UInt32.MaxValue) == 0 && NativeProcessIdentity.Path(UInt32.MaxValue) == null &&
                !NativeProcessIdentity.RequiresAdministrator(UInt32.MaxValue),
                "An invalid process identifier returns unavailable metadata without throwing or authorizing a broker");
        }
    }
}
