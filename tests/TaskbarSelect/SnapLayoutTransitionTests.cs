using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace TaskbarSelect
{
    // Injected work-area and managed action gates only. No controller Refresh,
    // HWND provider, native API, profile, filesystem, input or desktop is used.
    internal static class SnapLayoutTransitionTests
    {
        sealed class Platform : IWorkAreaPlatform
        {
            internal readonly Dictionary<string, Native.MonitorInfo> Monitors = new Dictionary<string, Native.MonitorInfo>();
            internal readonly List<Display> Connected = new List<Display>();
            internal readonly List<Native.Rect> Sets = new List<Native.Rect>();
            public bool OwnedHidden(Bar bar) { throw new Exception("A scoped restoration must never hide or reclaim another bar."); }
            public Native.MonitorInfo? Monitor(Bar bar) { return Monitor(bar.Device); }
            public Native.MonitorInfo? Monitor(string device)
            { Native.MonitorInfo info; return Monitors.TryGetValue(device, out info) ? info : (Native.MonitorInfo?)null; }
            public Native.Rect? Bounds(Bar bar) { throw new Exception("No expansion is permitted in restoration."); }
            public List<Display> Displays() { return Connected.ToList(); }
            public void Set(Native.Rect rect)
            {
                Sets.Add(rect);
                foreach (string device in Monitors.Keys.ToArray()) {
                    var info = Monitors[device];
                    if (AreaPolicy.Contains(info.Monitor, rect)) { info.Work = rect; Monitors[device] = info; return; }
                }
                throw new Exception("Unexpected foreign-monitor mutation.");
            }
        }
        static Native.Rect R(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        static AreaRecord Record(string identity, int left)
        { return new AreaRecord { Identity = identity, Monitor = R(left, 0, left + 1000, 1000), Original = R(left, 0, left + 1000, 950), Applied = R(left, 0, left + 1000, 1000) }; }
        static SnapCorrectionArea Area(AreaRecord record)
        { SnapCorrectionArea area; if (!SnapCorrectionArea.TryCapture(record, out area)) throw new Exception("Invalid fixture area."); return area; }
        static Platform Backend(params AreaRecord[] records)
        {
            var result = new Platform();
            foreach (var record in records) {
                string device = "device-" + record.Identity;
                result.Connected.Add(new Display { Identity = record.Identity, Device = device, Primary = false });
                result.Monitors.Add(device, new Native.MonitorInfo { Device = device, Monitor = record.Monitor, Work = record.Applied });
            }
            return result;
        }
        static int checks;
        static void Check(bool okay, string message) { if (!okay) throw new Exception(message); checks++; }
        internal static void Run()
        {
            checks = 0;
            {
                var first = Record("selected", 0); var other = Record("retained", 1000);
                var backend = Backend(first, other); var records = new List<AreaRecord> { first, other }; int saves = 0;
                var areas = new WorkAreas(records, backend, delegate { saves++; });
                var selected = areas.RestorationAreas(a => a.ToRecord().Identity == "selected").ToArray();
                Check(selected.Length == 1 && selected[0].Equals(Area(first)), "Selection snapshots only the affected physical area.");
                areas.RestoreSelected(selected);
                Check(backend.Sets.Count == 1 && AreaPolicy.Equal(backend.Sets[0], first.Original), "Selected restoration issues only the exact saved Original work area.");
                Check(records.Count == 1 && Object.ReferenceEquals(records[0], other) && saves == 1, "Unrelated recovery ownership is retained untouched.");
                Check(AreaPolicy.Equal(backend.Monitors["device-retained"].Work, other.Applied), "Other hidden monitor keeps its expanded work area.");
                Check(AreaPolicy.Equal(selected[0].ToRecord().Original, first.Original), "Scope retains Original after journal retirement.");
                areas.RestoreSelected(selected);
                Check(backend.Sets.Count == 1 && saves == 1, "A retired selected claim cannot recreate or repeat a recovery write.");
            }
            {
                var record = Record("foreign", 0); var backend = Backend(record);
                var foreign = backend.Monitors["device-foreign"]; foreign.Work = R(0, 0, 1000, 900); backend.Monitors["device-foreign"] = foreign;
                var records = new List<AreaRecord> { record }; var areas = new WorkAreas(records, backend, delegate { });
                areas.RestoreSelected(new[] { Area(record) });
                Check(backend.Sets.Count == 0 && records.Count == 0 && AreaPolicy.Equal(backend.Monitors["device-foreign"].Work, foreign.Work),
                    "A positively foreign appbar reservation is yielded to without overwriting it.");
            }
            {
                var record = Record("missing", 0); var backend = Backend(record); backend.Connected.Clear();
                var records = new List<AreaRecord> { record }; var areas = new WorkAreas(records, backend, delegate { });
                areas.RestoreSelected(new[] { Area(record) });
                Check(backend.Sets.Count == 0 && records.Count == 1, "Missing display retains recovery and does not guess a target.");
            }
            {
                int recovery = 0, finished = 0, resumed = 0; bool foreignReady = false;
                var area = Area(Record("foreign-gate", 0));
                var policy = new SnapLayoutRestorePolicy(new[] { area }, delegate { }, delegate { recovery++; },
                    delegate { return foreignReady ? SnapLayoutAreaState.Changed : SnapLayoutAreaState.Unknown; },
                    delegate { finished++; return true; }, delegate { resumed++; return true; });
                Check(!policy.TryComplete(true) && recovery == 1 && finished == 0 && resumed == 0, "Foreign geometry alone cannot bypass an unknown taskbar readback.");
                foreignReady = true;
                Check(policy.TryComplete(false) && recovery == 1 && finished == 1 && resumed == 1,
                    "A positively yielded foreign area after completed bar proof can finish without another recovery write.");
            }
            {
                var refresh = typeof(Controller).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(refresh != null && refresh.GetParameters().Length == 1 && Object.Equals(refresh.GetParameters()[0].DefaultValue, false),
                    "Ordinary refresh and unrelated settings changes do not implicitly authorize recovery retry.");
                // Bypass constructors deliberately: Controller's normal field
                // initializers create message-only shortcut HWNDs even with
                // desktop hooks disabled. These gates need managed state only.
                var controller = (Controller)FormatterServices.GetUninitializedObject(typeof(Controller));
                var mover = (TaskbarMover)FormatterServices.GetUninitializedObject(typeof(TaskbarMover));
                typeof(Controller).GetField("Mover", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, mover);
                controller.Enabled = true; controller.Settings = new Settings();
                var policy = new SnapLayoutRestorePolicy(new[] { Area(Record("actions", 0)) }, delegate { }, delegate { },
                    delegate { return SnapLayoutAreaState.Unknown; }, delegate { return true; }, delegate { return true; });
                var field = typeof(Controller).GetField("layoutRestore", BindingFlags.Instance | BindingFlags.NonPublic);
                field.SetValue(controller, policy);
                mover.ControllerActionGate = delegate { return !controller.LayoutRecoveryPending; };
                Check(controller.LayoutRecoveryPending && !controller.EffectiveEnabled, "The production action gate rejects a retained transition even when Enabled intent remains true.");
                var target = new IntPtr(1234);
                controller.MoveFocusedWindow(1, target);
                Check(controller.Mover.LastStatus == "Window movement is paused", "Direct monitor shortcut stops before any target/native observation while recovery is pending.");
                controller.MinimizeFocusedWindow(target);
                Check(controller.Mover.LastStatus == "Window actions are paused", "Minimize cannot bypass the recovery action gate.");
                controller.TogglePinnedWindow(target);
                Check(controller.Mover.LastStatus == "Window actions are paused", "Pin cannot bypass the recovery action gate.");
                controller.CloseFocusedWindow(target);
                Check(controller.Mover.LastStatus == "Window actions are paused", "Close cannot bypass the recovery action gate.");
                controller.OpenAppSearch(target);
                Check(controller.Mover.LastStatus == "Search is paused" && controller.SearchTargets().Count == 0,
                    "App search cannot launch or move windows during retained cleanup.");
                controller.FocusDirection(target, WindowDirection.Left);
                Check(controller.Mover.LastStatus == "Focus navigation is paused", "Directional focus respects the same gate.");
                controller.OpenTilingPicker(target);
                Check(controller.Mover.LastStatus == "Tiling is paused", "Tiling respects the same gate.");
                Check(controller.ShelfTargets().Count == 0, "Ghost Bar cannot offer taskbar moves during pending cleanup.");
                Check(!mover.CanUndo && !mover.Undo(), "Mover's internal Undo path rejects retained recovery before reading history or queuing Undo.");
                Check(!mover.TryBeginMinimizeAction(), "Mover's internal minimize gate cannot bypass controller recovery.");
                Check(!mover.MoveFocusedWindow(target, null, 1) && !mover.MoveFromShelf(null, null),
                    "Mover entrypoints cannot bypass the central gate even when called directly.");
                // Simulate another callback reconfiguring the raw enable flag.
                typeof(TaskbarMover).GetField("enabled", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mover, true);
                Check(!mover.Enabled, "The mover's effective enable flag cannot be reactivated while the recovery gate remains closed.");
                // No constructors, handles or disposable native resources were
                // created, and no native action was reached.
            }
            Console.WriteLine(checks + " layout transition boundary checks passed.");
        }
        internal static int RunPureTest()
        { try { Run(); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
    }
}
