using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    internal static class DisplayConfigMonitors
    {
        const uint OnlyActivePaths = 2;
        const int InsufficientBuffer = 122;
        static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        internal struct Target
        {
            internal readonly string Source, Identity, Name;

            internal Target(string source, string identity, string name)
            {
                Source = source;
                Identity = identity;
                Name = name;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct AdapterId { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        struct PathSource
        {
            public AdapterId Adapter;
            public uint Id, ModeIndex, StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PathTarget
        {
            public AdapterId Adapter;
            public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
            public uint RefreshNumerator, RefreshDenominator, ScanLineOrdering;
            public int Available;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PathInfo
        {
            public PathSource Source;
            public PathTarget Target;
            public uint Flags;
        }

        // DISPLAYCONFIG_MODE_INFO has a 16-byte header and a 48-byte union.
        // Windows requires a mode buffer even though we only use the path records.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        struct ModeInfo { public uint Type; }

        [StructLayout(LayoutKind.Sequential)]
        struct DeviceInfoHeader
        {
            public uint Type, Size;
            public AdapterId Adapter;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SourceName
        {
            public DeviceInfoHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TargetName
        {
            public DeviceInfoHeader Header;
            public uint Flags, OutputTechnology;
            public ushort EdidManufacturerId, EdidProductCodeId;
            public uint ConnectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
            ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);

        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int DisplayConfigGetDeviceInfo(ref SourceName request);

        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int DisplayConfigGetDeviceInfo(ref TargetName request);

        internal static Dictionary<string, Target> Read()
        {
            return ResolveTargets(ReadActiveTargets());
        }

        static IEnumerable<Target> ReadActiveTargets()
        {
            for (int attempt = 0; attempt < 3; attempt++) {
                uint pathCount, modeCount;
                if (GetDisplayConfigBufferSizes(OnlyActivePaths, out pathCount, out modeCount) != 0 || pathCount == 0)
                    break;
                var paths = new PathInfo[pathCount];
                int result = QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount,
                    new ModeInfo[modeCount], IntPtr.Zero);
                if (result == InsufficientBuffer) continue;
                if (result != 0) break;

                var targets = new List<Target>();
                for (int i = 0; i < pathCount; i++) {
                    PathInfo path = paths[i];
                    var source = new SourceName { Header = new DeviceInfoHeader { Type = 1,
                        Size = (uint)Marshal.SizeOf(typeof(SourceName)), Adapter = path.Source.Adapter, Id = path.Source.Id } };
                    if (DisplayConfigGetDeviceInfo(ref source) != 0 || String.IsNullOrWhiteSpace(source.GdiName))
                        return new Target[0];

                    var target = new TargetName { Header = new DeviceInfoHeader { Type = 2,
                        Size = (uint)Marshal.SizeOf(typeof(TargetName)), Adapter = path.Target.Adapter, Id = path.Target.Id } };
                    bool known = path.Target.Available != 0 && DisplayConfigGetDeviceInfo(ref target) == 0;
                    targets.Add(new Target(source.GdiName, known ? target.DevicePath : "", known ? target.FriendlyName : ""));
                }
                return targets;
            }
            return new Target[0];
        }

        internal static Dictionary<string, Target> ResolveTargets(IEnumerable<Target> targets)
        {
            var paths = targets.ToArray();
            var byIdentity = paths.ToLookup(path => path.Identity, PathComparer);
            // A cloned source or a physical identity claimed by multiple sources cannot own saved preferences.
            return paths.GroupBy(path => path.Source, PathComparer)
                .Where(source => source.Count() == 1)
                .Select(source => source.Single())
                .Where(path => !String.IsNullOrWhiteSpace(path.Identity) &&
                    byIdentity[path.Identity].All(other => PathComparer.Equals(other.Source, path.Source)))
                .ToDictionary(path => path.Source, PathComparer);
        }

        internal static string PreserveIdentitySpelling(string verified, string legacy)
        {
            return !String.IsNullOrWhiteSpace(verified) && PathComparer.Equals(verified, legacy) ? legacy : verified;
        }
    }
}
