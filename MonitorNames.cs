using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace TaskbarSelect
{
    internal static class MonitorNames
    {
        static readonly Dictionary<string, string> manufacturers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "ACR", "Acer" },
            { "AUS", "ASUS" },
            { "DEL", "Dell" },
            { "GSM", "LG" },
            { "HWP", "HP" },
            { "LEN", "Lenovo" },
            { "MSI", "MSI" },
            { "SAM", "Samsung" }
        };

        internal static string NormalizeWindowsIdentity(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            string normalized = value.Trim().Replace('#', '\\');
            if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) normalized = normalized.Substring(4);
            int classGuid = normalized.IndexOf(@"\{", StringComparison.Ordinal);
            if (classGuid >= 0) normalized = normalized.Substring(0, classGuid);
            if (normalized.EndsWith("_0", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - 2);
            return normalized.ToUpperInvariant();
        }

        internal static string Decode(object value)
        {
            ushort[] characters = value as ushort[];
            if (characters == null) return "";
            return new string(characters.TakeWhile(character => character != 0).Select(character => (char)character).ToArray()).Trim();
        }

        internal static string Format(string manufacturer, string product, string friendly)
        {
            string maker;
            if (!manufacturers.TryGetValue(manufacturer ?? "", out maker)) maker = manufacturer;
            if (!String.IsNullOrWhiteSpace(friendly))
                return String.IsNullOrWhiteSpace(maker) || friendly.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ?
                    friendly : maker + " " + friendly;
            string fallback = ((maker ?? "") + " " + (product ?? "")).Trim();
            return String.IsNullOrWhiteSpace(fallback) ? "Monitor" : fallback;
        }

        internal static Dictionary<string, string> Read()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, ManufacturerName, ProductCodeID, UserFriendlyName FROM WmiMonitorID"))
                using (ManagementObjectCollection monitors = searcher.Get()) {
                    foreach (ManagementObject monitor in monitors) {
                        string identity = NormalizeWindowsIdentity(monitor["InstanceName"] as string);
                        if (String.IsNullOrEmpty(identity)) continue;
                        result[identity] = Format(Decode(monitor["ManufacturerName"]), Decode(monitor["ProductCodeID"]), Decode(monitor["UserFriendlyName"]));
                    }
                }
            } catch (Exception) {
                // Friendly monitor metadata is optional; display control must keep working without WMI.
            }
            return result;
        }
    }
}
