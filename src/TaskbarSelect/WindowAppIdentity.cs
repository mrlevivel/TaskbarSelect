using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TaskbarSelect
{
    // A taskbar group can be identified on the HWND rather than its host process
    // (for example a browser/helper process hosting another application's UI).
    internal static class WindowAppIdentity
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { internal Guid Format; internal uint Id; }
        // Taskbar Select is built for x64; PROPVARIANT's union occupies 16 bytes.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropertyVariant
        {
            [FieldOffset(0)] internal ushort Type;
            [FieldOffset(8)] internal IntPtr Pointer;
            [FieldOffset(8)] internal short Boolean;
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropertyVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyVariant value);
            [PreserveSig] int Commit();
        }
        [DllImport("shell32.dll")]
        static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropertyVariant value);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();

        // Only our own HWNDs may be assigned or cleared. This is used by the
        // Settings form lifecycle, never to relabel a selected foreign app.
        internal static bool WriteOwned(IntPtr window, string identity)
        {
            uint process;
            if (window == IntPtr.Zero || Native.GetWindowThreadProcessId(window, out process) == 0 || process != GetCurrentProcessId()) return false;
            IPropertyStore store = null;
            try {
                var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                if (SHGetPropertyStoreForWindow(window, ref iid, out store) < 0 || store == null) return false;
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                var emptyIdentity = new PropertyVariant();
                if (store.SetValue(ref key, ref emptyIdentity) < 0) return false;
                // A test window must not create a pin/relaunch that drops
                // --test-profile. Windows requires this before assigning its ID.
                var pinKey = new PropertyKey { Format = key.Format, Id = 9 };
                var pinValue = new PropertyVariant();
                if (identity != null && !String.Equals(identity, SettingsTaskbarIdentity.ProductionId, StringComparison.OrdinalIgnoreCase)) {
                    pinValue.Type = 11; pinValue.Boolean = -1;
                }
                bool pinSet = store.SetValue(ref pinKey, ref pinValue) >= 0;
                if (identity == null || !pinSet) return pinSet;
                var value = new PropertyVariant();
                try {
                    value.Pointer = Marshal.StringToCoTaskMemUni(identity);
                    value.Type = 31;
                    // Window property stores apply SetValue immediately.
                    bool identitySet = store.SetValue(ref key, ref value) >= 0;
                    if (!identitySet) {
                        var empty = new PropertyVariant();
                        store.SetValue(ref pinKey, ref empty);
                    }
                    return pinSet && identitySet;
                } finally { PropVariantClear(ref value); }
            }
            catch (COMException) { return false; }
            catch (InvalidCastException) { return false; }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        internal static string Read(IntPtr window)
        {
            string value;
            return TryRead(window, out value) ? value : "";
        }

        // An empty identity is useful evidence only when its property could be
        // read. Keep the older Read contract while distinguishing access/query
        // failures for native-selection recovery.
        internal static bool TryRead(IntPtr window, out string identity)
        {
            identity = "";
            if (window == IntPtr.Zero) return false;
            IPropertyStore store = null;
            try {
                var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                if (SHGetPropertyStoreForWindow(window, ref iid, out store) < 0 || store == null) return false;
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                var value = new PropertyVariant();
                try {
                    int result = store.GetValue(ref key, out value);
                    string decoded = result >= 0 && value.Type == 31 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
                    return TryDecode(result, value.Type, value.Pointer != IntPtr.Zero, decoded, out identity);
                } finally { PropVariantClear(ref value); }
            }
            catch (COMException) { return false; }
            catch (InvalidCastException) { return false; }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        internal static bool TryDecode(int result, ushort type, bool hasPointer, string decoded, out string identity)
        {
            identity = "";
            if (result < 0) return false;
            if (type == 0) return true; // A successful VT_EMPTY property is absent.
            if (type != 31 || !hasPointer || decoded == null) return false;
            identity = decoded;
            return true;
        }
    }

    internal static class SettingsTaskbarIdentity
    {
        // Matches the existing production installer shortcuts. Test windows
        // have their own stable profile group, independent of the build path.
        internal const string ProductionId = "TaskbarSelect.App";
        const string TestPrefix = "TaskbarSelect.Test.";
        internal static string CurrentId
        { get { return ForProfile(new ProfileSelection(AppProfile.Directory, AppProfile.IsTest)); } }

        internal static string ForProfile(ProfileSelection profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (!profile.IsTest) return ProductionId;
            if (String.IsNullOrWhiteSpace(profile.Directory)) throw new ArgumentException("A test taskbar identity requires a profile directory.");
            using (var hash = SHA256.Create())
                return TestPrefix + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(profile.Directory.TrimEnd('\\', '/').ToUpperInvariant()))).Replace("-", "");
        }

        internal static bool IsReserved(string identity)
        {
            if (String.Equals(identity, ProductionId, StringComparison.OrdinalIgnoreCase)) return true;
            if (identity == null || identity.Length != TestPrefix.Length + 64 || !identity.StartsWith(TestPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            for (int index = TestPrefix.Length; index < identity.Length; index++) {
                char c = identity[index];
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) return false;
            }
            return true;
        }

        internal static bool Apply(IntPtr window, string identity)
        { return IsReserved(identity) && WindowAppIdentity.WriteOwned(window, identity); }
        internal static bool Clear(IntPtr window) { return WindowAppIdentity.WriteOwned(window, null); }
    }
}
