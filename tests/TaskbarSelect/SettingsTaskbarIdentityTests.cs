using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Hidden owned HWNDs only. No Explorer UIA, desktop hooks, input, real
    // focus requests, helper launches, profile writes or Startup operations.
    internal static class SettingsTaskbarIdentityTests
    {
        const string TestPrefix = "TaskbarSelect.Test.";

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { internal Guid Format; internal uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropertyVariant
        {
            [FieldOffset(0)] internal ushort Type;
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
        [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropertyVariant value);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();

        // Read the independent native property rather than inferring pin policy
        // from the identity string or the production writer's return value.
        static bool PinningIs(IntPtr window, bool prevented)
        {
            uint process;
            if (window == IntPtr.Zero || Native.GetWindowThreadProcessId(window, out process) == 0 || process != GetCurrentProcessId()) return false;
            IPropertyStore store = null;
            try {
                var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                if (SHGetPropertyStoreForWindow(window, ref iid, out store) < 0 || store == null) return false;
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 9 };
                var value = new PropertyVariant();
                try {
                    if (store.GetValue(ref key, out value) < 0) return false;
                    return prevented ? value.Type == 11 && value.Boolean == -1 : value.Type == 0;
                } finally { PropVariantClear(ref value); }
            }
            catch (COMException) { return false; }
            catch (InvalidCastException) { return false; }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        static string Digest(string directory)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(directory.TrimEnd('\\', '/').ToUpperInvariant()))).Replace("-", "");
        }

        static bool RejectedProfile(ProfileSelection profile)
        {
            try { SettingsTaskbarIdentity.ForProfile(profile); return false; }
            catch (ArgumentException) { return true; }
        }

        static TaskbarMover.Click Button(string id, string label)
        { return new TaskbarMover.Click { AppId = id, AppName = label, Grouped = false }; }

        internal static void Run(Action<bool, string> check)
        {
            Profiles(check);
            OwnedProperties(check);
            ActualSettingsHandles(check);
        }

        static void Profiles(Action<bool, string> check)
        {
            var production = new ProfileSelection(AppProfile.ProductionDirectory, false);
            string normal = SettingsTaskbarIdentity.ForProfile(production);
            check(normal == SettingsTaskbarIdentity.ProductionId && normal == "TaskbarSelect.App",
                "Production settings use a stable explicit taskbar identity independent of install or build path");
            string directory = @"X:\neutral settings identity\profile-A";
            string isolated = SettingsTaskbarIdentity.ForProfile(new ProfileSelection(directory, true));
            check(isolated == TestPrefix + Digest(directory) && isolated.Length == TestPrefix.Length + 64 && isolated.Length <= 128,
                "Isolated taskbar identity is a bounded SHA256 scope rather than an executable name or generated Windows ID");
            check(isolated != normal && !isolated.Contains("neutral") && !isolated.Contains("profile-A") && !isolated.Contains(":"),
                "A test window has a different taskbar group and does not expose its raw profile path");
            foreach (string equivalent in new[] { directory.ToLowerInvariant(), directory.ToUpperInvariant(), directory + "\\", directory + "/" })
                check(SettingsTaskbarIdentity.ForProfile(new ProfileSelection(equivalent, true)) == isolated,
                    "Canonical test scope remains stable across Windows casing and terminal separators");
            foreach (string distinct in new[] { directory + "-other", @"X:\other parent\profile-A", @"Y:\neutral settings identity\profile-A", @"X:\neutral settings identity\profile-" + "\u96ea" })
                check(SettingsTaskbarIdentity.ForProfile(new ProfileSelection(distinct, true)) != isolated,
                    "Different profile locations have separate taskbar identities");
            check(SettingsTaskbarIdentity.IsReserved(normal) && SettingsTaskbarIdentity.IsReserved(normal.ToUpperInvariant()) &&
                SettingsTaskbarIdentity.IsReserved(isolated) && SettingsTaskbarIdentity.IsReserved(isolated.ToLowerInvariant()),
                "Own reserved identities recognize exact production and hexadecimal test scopes case insensitively");
            foreach (string invalid in new[] { null, "", "TaskbarSelect", "Taskbar Select", "TaskbarSelect.App.Other", "TaskbarSelect.App ",
                TestPrefix, TestPrefix + new string('A', 63), TestPrefix + new string('A', 65), TestPrefix + new string('G', 64),
                "Other." + TestPrefix + new string('A', 64), "Microsoft.AutoGenerated.OwnSettingsFixture" })
                check(!SettingsTaskbarIdentity.IsReserved(invalid), "Only the exact application identity namespace can trigger reserved-ID behavior");
            check(RejectedProfile(null), "Missing profile cannot produce a taskbar identity");
            foreach (string invalid in new[] { null, "", " \t" })
                check(RejectedProfile(new ProfileSelection(invalid, true)), "An absent isolated profile scope fails closed");
            check(SettingsTaskbarIdentity.CurrentId == SettingsTaskbarIdentity.ForProfile(new ProfileSelection(AppProfile.Directory, AppProfile.IsTest)),
                "The live settings identity uses the already selected profile without changing its selection");
        }

        static void OwnedProperties(Action<bool, string> check)
        {
            string id = SettingsTaskbarIdentity.CurrentId;
            string different = SettingsTaskbarIdentity.ForProfile(new ProfileSelection(@"X:\neutral settings identity\negative", true));
            if (String.Equals(id, different, StringComparison.OrdinalIgnoreCase))
                different = SettingsTaskbarIdentity.ProductionId;
            using (var neutral = new Form { Text = AppProfile.Title, ShowInTaskbar = false }) {
                IntPtr window = neutral.Handle;
                check(window != IntPtr.Zero && !neutral.Visible && WindowAppIdentity.Read(window) == "" && PinningIs(window, false),
                    "Neutral matching fixture starts hidden without an explicit taskbar identity");
                check(!TaskbarMover.MatchesApp(Button(id, AppProfile.Title), window) &&
                    !TaskbarMover.MatchesApp(Button(id, "TaskbarSelect"), window),
                    "A reserved settings button cannot match an identity-less same-process window by caption or process-name fallback");
                check(!SettingsTaskbarIdentity.Apply(window, "Microsoft.AutoGenerated.OwnSettingsFixture") &&
                    !SettingsTaskbarIdentity.Apply(window, "TaskbarSelect.App.Other") && WindowAppIdentity.Read(window) == "",
                    "Owned property writer rejects non-reserved or malformed identities without changing the HWND");
                check(SettingsTaskbarIdentity.Apply(window, id) && WindowAppIdentity.Read(window) == id && PinningIs(window, AppProfile.IsTest),
                    "Owned HWND property writer installs the exact explicit settings identity");
                check(TaskbarMover.MatchesApp(Button(id, "Unrelated button caption"), window) &&
                    TaskbarMover.MatchesApp(Button(id.ToUpperInvariant(), ""), window),
                    "Exact explicit taskbar identity matches without any title inference and with case-insensitive IDs");
                check(!TaskbarMover.MatchesApp(Button(different, AppProfile.Title), window) &&
                    !TaskbarMover.MatchesApp(Button("TaskbarSelect", AppProfile.Title), window),
                    "An incompatible explicit scope overrides convenient matching captions and process names");
                check(SettingsTaskbarIdentity.Apply(window, different) && WindowAppIdentity.Read(window) == different &&
                    !TaskbarMover.MatchesApp(Button(id, AppProfile.Title), window),
                    "A different test or production scope cannot resolve to this explicit HWND");
                check(SettingsTaskbarIdentity.Clear(window) && WindowAppIdentity.Read(window) == "" && PinningIs(window, false) &&
                    !TaskbarMover.MatchesApp(Button(id, "TaskbarSelect"), window),
                    "Clearing owned identity restores VT_EMPTY and cannot reopen process fallback for reserved buttons");
                check(SettingsTaskbarIdentity.Clear(window) && PinningIs(window, false), "Repeated owned HWND identity cleanup is idempotent");
                string isolated = SettingsTaskbarIdentity.ForProfile(new ProfileSelection(@"X:\neutral settings identity\pin-scope", true));
                foreach (string next in new[] { isolated, SettingsTaskbarIdentity.ProductionId, isolated, SettingsTaskbarIdentity.ProductionId })
                    check(SettingsTaskbarIdentity.Apply(window, next) && WindowAppIdentity.Read(window) == next &&
                        PinningIs(window, next != SettingsTaskbarIdentity.ProductionId),
                        "Test identity sets VT_BOOL PreventPinning and switching to production removes the test-only pin restriction");
                check(SettingsTaskbarIdentity.Apply(window, isolated) && SettingsTaskbarIdentity.Clear(window) &&
                    WindowAppIdentity.Read(window) == "" && PinningIs(window, false),
                    "Clearing a test identity removes both the explicit group and its PreventPinning property");
                check(!neutral.Visible, "Property checks never show or activate their owned form");
            }
            check(!SettingsTaskbarIdentity.Apply(IntPtr.Zero, id) && !SettingsTaskbarIdentity.Clear(IntPtr.Zero) &&
                !SettingsTaskbarIdentity.Apply(new IntPtr(-1), id) && !SettingsTaskbarIdentity.Clear(new IntPtr(-1)),
                "Invalid HWNDs fail before property changes");
            IntPtr shell = Native.GetShellWindow();
            uint shellProcess;
            if (shell != IntPtr.Zero && Native.GetWindowThreadProcessId(shell, out shellProcess) != 0 && shellProcess != GetCurrentProcessId())
                check(!SettingsTaskbarIdentity.Apply(shell, id) && !SettingsTaskbarIdentity.Clear(shell),
                    "Foreign shell ownership is rejected before the property writer can assign identity or pin policy");
            else Console.WriteLine("SKIP: foreign shell owner unavailable for own-process refusal check");
        }

        static void ActualSettingsHandles(Action<bool, string> check)
        {
            byte[] before = File.Exists(Program.ConfigPath) ? File.ReadAllBytes(Program.ConfigPath) : null;
            int helpers = WindowMoveWorker.ActiveHelpers;
            var controller = new Controller(false) { Settings = new Settings(), Enabled = false,
                Displays = new List<Display>(), Bars = new List<Bar>() };
            int destroyed = 0, cleared = 0;
            try {
                using (var form = new SettingsForm(controller)) {
                    IntPtr trackedWindow = IntPtr.Zero;
                    form.HandleCreated += delegate { trackedWindow = form.Handle; };
                    form.HandleDestroyed += delegate {
                        destroyed++;
                        if (!Native.IsWindow(trackedWindow) || WindowAppIdentity.Read(trackedWindow) == "" && PinningIs(trackedWindow, false)) cleared++;
                    };
                    IntPtr window = form.Handle;
                    check(form.TaskbarIdentityReady && WindowAppIdentity.Read(window) == SettingsTaskbarIdentity.CurrentId &&
                        PinningIs(window, AppProfile.IsTest) && !form.Visible,
                        "Actual hidden SettingsForm installs its explicit scoped identity on first handle creation");
                    var clicked = Button(SettingsTaskbarIdentity.CurrentId, AppProfile.Title);
                    check(TaskbarMover.MatchesApp(clicked, window),
                        "The real settings HWND resolves its own taskbar button, including the isolated-test title");
                    form.Text = "A completely different settings caption";
                    check(TaskbarMover.MatchesApp(clicked, window), "Changing the settings caption does not change taskbar ownership");
                    using (var helper = new Form { Text = form.Text, ShowInTaskbar = false }) {
                        check(WindowAppIdentity.Read(helper.Handle) == "" && PinningIs(helper.Handle, false) &&
                            !TaskbarMover.MatchesApp(clicked, helper.Handle) && !helper.Visible,
                            "Other same-process helper forms retain no settings identity even with an identical caption");
                    }
                    for (int repeat = 0; repeat < 3; repeat++) {
                        int previous = destroyed;
                        typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                        window = form.Handle;
                        check(destroyed == previous + 1 && cleared == destroyed && form.TaskbarIdentityReady &&
                            WindowAppIdentity.Read(window) == SettingsTaskbarIdentity.CurrentId && PinningIs(window, AppProfile.IsTest) &&
                            TaskbarMover.MatchesApp(clicked, window) && !form.Visible,
                            "Settings handle recreation clears the old property and reapplies exact identity without showing the window");
                    }
                    foreach (bool showInTaskbar in new[] { false, true }) {
                        form.ShowInTaskbar = showInTaskbar;
                        window = form.Handle;
                        check(form.TaskbarIdentityReady && WindowAppIdentity.Read(window) == SettingsTaskbarIdentity.CurrentId &&
                            PinningIs(window, AppProfile.IsTest) && TaskbarMover.MatchesApp(clicked, window) && !form.Visible,
                            "Taskbar visibility handle changes retain explicit settings identity without activating the window");
                    }
                }
                check(destroyed > 0 && cleared == destroyed, "Every settings handle is cleared before its destruction event");
                check(!controller.DesktopRuntime && controller.Administrator == null && WindowMoveWorker.ActiveHelpers == helpers,
                    "Settings identity regressions use no desktop runtime, administrator connection or movement helper");
            } finally {
                // Controller.Dispose restores global taskbars; only release these
                // fixture-owned hook-free components instead.
                controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                foreach (GlobalShortcut binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
            }
            byte[] after = File.Exists(Program.ConfigPath) ? File.ReadAllBytes(Program.ConfigPath) : null;
            check(before == null ? after == null : after != null && before.SequenceEqual(after),
                "Settings taskbar identity checks preserve existing profile settings bytes");
        }
    }
}
