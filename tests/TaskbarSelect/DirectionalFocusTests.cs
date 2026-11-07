using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskbarSelect
{
    internal static class DirectionalFocusTests
    {
        sealed class Fixture
        {
            internal IntPtr Foreground = new IntPtr(10);
            internal readonly DirectionalFocusWindow Source = Window(10, 0, 0, 600, 600);
            internal readonly DirectionalFocusWindow Target = Window(20, 700, 0, 600, 600);
            internal readonly List<DirectionalFocusWindow> Windows = new List<DirectionalFocusWindow>();
            internal readonly DirectionalFocusApi Api;
            internal int Reads, Enumerations, Activations;
            internal bool Accept = true, ReturnNullList;
            internal string Status;
            internal Action<int> OnRead;
            internal Action OnEnumerate;
            internal Fixture()
            {
                Windows.Add(Source); Windows.Add(Target);
                Api = new DirectionalFocusApi {
                    Foreground = delegate { return Foreground; },
                    Read = delegate(IntPtr handle, bool allow) {
                        Reads++; if (OnRead != null) OnRead(Reads);
                        return Windows.FirstOrDefault(w => w != null && w.Identity != null && w.Identity.Handle == handle);
                    },
                    Windows = delegate(bool allow) { Enumerations++; if (OnEnumerate != null) OnEnumerate(); return ReturnNullList ? null : Windows; },
                    Activate = delegate(ShelfWindow selected, IntPtr expected) {
                        if (selected.Handle != Target.Identity.Handle || expected != new IntPtr(10)) throw new Exception("Focus target changed.");
                        Activations++; return Accept;
                    }
                };
            }
            internal bool Run(List<AppRule> exclusions = null, bool allowFullscreen = false, Func<IntPtr, bool> ignore = null)
            { return new DirectionalFocus(Api, delegate(string value) { Status = value; }, ignore).Focus(new IntPtr(10), WindowDirection.Right, exclusions, allowFullscreen); }
        }
        static DirectionalFocusWindow Window(int handle, int x, int y, int width = 100, int height = 100, int order = 0)
        {
            return new DirectionalFocusWindow { Identity = new ShelfWindow { Handle = new IntPtr(handle), Process = (uint)(handle + 100),
                ProcessStarted = handle + 1000, AppKey = "exe:neutral" + handle, AppName = "Neutral " + handle, Title = "Neutral fixture" },
                Bounds = new Native.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height }, ZOrder = order };
        }
        internal static void Run(Action<bool, string> check)
        { Geometry(check); Safety(check); Races(check); }
        static void Geometry(Action<bool, string> check)
        {
            var source = Window(10, 0, 0);
            var left = Window(11, -200, 0); var right = Window(12, 200, 0); var top = Window(13, 0, -200); var bottom = Window(14, 0, 200);
            var windows = new[] { bottom, right, source, top, left };
            check(DirectionalFocusPolicy.Select(source, windows, WindowDirection.Left) == left, "Focus left follows physical negative coordinates");
            check(DirectionalFocusPolicy.Select(source, windows, WindowDirection.Right) == right, "Focus right chooses the actual neighboring window");
            check(DirectionalFocusPolicy.Select(source, windows, WindowDirection.Up) == top, "Focus up crosses a stacked-monitor seam");
            check(DirectionalFocusPolicy.Select(source, windows, WindowDirection.Down) == bottom, "Focus down crosses a stacked-monitor seam");
            foreach (WindowDirection direction in Enum.GetValues(typeof(WindowDirection)))
                check(DirectionalFocusPolicy.Select(source, new[] { source }, direction) == null, "Focus never wraps when no window exists " + direction);
            var diagonal = Window(21, 110, 110); var aligned = Window(22, 2000, 0);
            check(DirectionalFocusPolicy.Select(source, new[] { diagonal, aligned }, WindowDirection.Right) == aligned,
                "Focus prioritizes a window overlapping the perpendicular axis over a nearer diagonal");
            var near = Window(23, 200, 0); var far = Window(24, 400, 0);
            check(DirectionalFocusPolicy.Select(source, new[] { far, near }, WindowDirection.Right) == near, "Focus chooses the nearest aligned center");
            var nearDiagonal = Window(25, 200, 300); var farDiagonal = Window(26, 300, 600);
            check(DirectionalFocusPolicy.Select(source, new[] { farDiagonal, nearDiagonal }, WindowDirection.Right) == nearDiagonal,
                "Without aligned candidates focus falls back to nearest directional center");
            var overlap = Window(27, 20, 0); var sameCenter = Window(28, 0, 0);
            check(DirectionalFocusPolicy.Select(source, new[] { sameCenter, overlap }, WindowDirection.Right) == overlap,
                "Overlapping windows are directional by center; identical centers do not invent a direction");
            var front = Window(30, 200, 0, 100, 100, 1); var back = Window(31, 200, 0, 100, 100, 9);
            check(DirectionalFocusPolicy.Select(source, new[] { back, front }, WindowDirection.Right) == front &&
                DirectionalFocusPolicy.Select(source, new[] { front, back }, WindowDirection.Right) == front,
                "Equidistant windows prefer the frontmost Z-order regardless of list ordering");
            var lowerHandle = Window(32, 200, 0, 100, 100, 1); var higherHandle = Window(33, 200, 0, 100, 100, 1);
            check(DirectionalFocusPolicy.Select(source, new[] { higherHandle, lowerHandle }, WindowDirection.Right) == lowerHandle,
                "Equal geometry and Z-order use a stable HWND tie-break");
            var empty = Window(40, 0, 0, 0, 0); var inverted = Window(41, 0, 0, -100, 100);
            check(DirectionalFocusPolicy.Select(source, new[] { empty, null, inverted }, WindowDirection.Right) == null,
                "Focus geometry ignores missing, empty and inverted bounds");
            check(DirectionalFocusPolicy.Select(empty, windows, WindowDirection.Right) == null &&
                DirectionalFocusPolicy.Select(null, windows, WindowDirection.Right) == null &&
                DirectionalFocusPolicy.Select(source, null, WindowDirection.Right) == null &&
                DirectionalFocusPolicy.Select(source, windows, (WindowDirection)99) == null,
                "Invalid source, catalog or direction cannot select a target");
            var extremeSource = Window(50, Int32.MinValue, Int32.MinValue);
            var extremeTarget = Window(51, Int32.MaxValue - 100, Int32.MaxValue - 100);
            check(DirectionalFocusPolicy.Select(extremeSource, new[] { extremeTarget }, WindowDirection.Right) == extremeTarget,
                "Focus distance handles the full signed desktop coordinate range without overflow");
            var oddSource = Window(60, -3, -3, 101, 101); var oddRight = Window(61, -2, -3, 101, 101);
            check(DirectionalFocusPolicy.Select(oddSource, new[] { oddRight }, WindowDirection.Right) == oddRight,
                "Odd-sized and negative-coordinate windows retain exact center ordering");
            foreach (int offset in new[] { -10000, -1920, -1, 0, 1, 1080, 10000 }) {
                var translated = Window(70, offset, -offset);
                var neighbors = new[] { Window(71, offset - 200, -offset), Window(72, offset + 200, -offset),
                    Window(73, offset, -offset - 200), Window(74, offset, -offset + 200) };
                foreach (WindowDirection direction in Enum.GetValues(typeof(WindowDirection))) {
                    int expected = direction == WindowDirection.Left ? 71 : direction == WindowDirection.Right ? 72 : direction == WindowDirection.Up ? 73 : 74;
                    var selected = DirectionalFocusPolicy.Select(translated, neighbors.Reverse(), direction);
                    check(selected != null && selected.Identity.Handle == new IntPtr(expected),
                        "Directional selection remains invariant under desktop translation " + offset + " " + direction);
                }
            }
        }
        static void Safety(Action<bool, string> check)
        {
            var normal = new Fixture();
            check(normal.Run() && normal.Activations == 1 && normal.Reads == 3 && normal.Status == "Focused Neutral 20 to the right.",
                "Focus revalidates source and selected identity then invokes exactly one activation");
            var refused = new Fixture { Accept = false };
            check(!refused.Run() && refused.Activations == 1 && refused.Status.Contains("Windows refused"),
                "Native focus refusal cannot be reported as successful activation");
            var unavailable = new Fixture { ReturnNullList = true };
            check(!unavailable.Run() && unavailable.Activations == 0 && unavailable.Status.Contains("timed out"),
                "A timed-out or incomplete catalog never selects from a truncated list");
            var empty = new Fixture(); empty.Windows.Remove(empty.Target);
            check(!empty.Run() && empty.Activations == 0 && empty.Status == "No eligible window to the right.",
                "No directional target leaves focus unchanged and explains the direction");
            var excluded = new Fixture();
            check(!excluded.Run(new List<AppRule> { new AppRule { Key = excluded.Target.Identity.AppKey.ToUpperInvariant() } }) && excluded.Activations == 0,
                "Focus respects case-insensitive app exclusions");
            var nullRule = new Fixture();
            check(nullRule.Run(new List<AppRule> { null }) && nullRule.Activations == 1, "A null exclusion entry does not bypass identity safety or break focus");
            var ignored = new Fixture();
            check(!ignored.Run(ignore: delegate(IntPtr handle) { return handle == ignored.Target.Identity.Handle; }) && ignored.Activations == 0,
                "Search and tiling-picker windows supplied to the ignore guard cannot become focus targets");
            var ignoredSource = new Fixture();
            check(!ignoredSource.Run(ignore: delegate(IntPtr handle) { return handle == ignoredSource.Source.Identity.Handle; }) && ignoredSource.Enumerations == 0,
                "An ignored foreground overlay cannot act as the navigation source");
            var fullscreen = new Fixture(); fullscreen.Target.Fullscreen = true;
            check(!fullscreen.Run() && fullscreen.Activations == 0 && fullscreen.Run(allowFullscreen: true),
                "Directional focus uses the current shared fullscreen opt-in for target eligibility");
            var fullSource = new Fixture(); fullSource.Source.Fullscreen = true;
            check(!fullSource.Run() && fullSource.Enumerations == 0 && fullSource.Run(allowFullscreen: true),
                "Fullscreen source is guarded consistently before native enumeration");
            var ineligible = new Action<DirectionalFocusWindow>[] {
                delegate(DirectionalFocusWindow w) { w.Visible = false; }, delegate(DirectionalFocusWindow w) { w.Enabled = false; },
                delegate(DirectionalFocusWindow w) { w.Minimized = true; }, delegate(DirectionalFocusWindow w) { w.Cloaked = true; },
                delegate(DirectionalFocusWindow w) { w.ToolWindow = true; }, delegate(DirectionalFocusWindow w) { w.Shell = true; },
                delegate(DirectionalFocusWindow w) { w.Identity.Process = 0; }, delegate(DirectionalFocusWindow w) { w.Identity.ProcessStarted = 0; },
                delegate(DirectionalFocusWindow w) { w.Identity.AppKey = ""; }, delegate(DirectionalFocusWindow w) { w.Identity = null; },
                delegate(DirectionalFocusWindow w) { w.Bounds.Right = w.Bounds.Left; },
                delegate(DirectionalFocusWindow w) { w.Identity.Handle = IntPtr.Zero; },
                delegate(DirectionalFocusWindow w) { w.Identity.Handle = new IntPtr(-1); },
                delegate(DirectionalFocusWindow w) { w.Identity.Handle = new IntPtr(0xFFFF); }
            };
            for (int index = 0; index < ineligible.Length; index++) {
                var target = new Fixture(); ineligible[index](target.Target);
                check(!target.Run() && target.Activations == 0, "Focus excludes invalid, hidden, minimized, shell or helper targets " + index);
                var source = new Fixture(); ineligible[index](source.Source);
                check(!source.Run() && source.Activations == 0 && source.Enumerations == 0, "Focus refuses an ineligible foreground source " + index);
            }
            var captured = new Fixture { Foreground = new IntPtr(11) };
            check(!captured.Run() && captured.Reads == 0 && captured.Activations == 0, "Captured foreground mismatch never reads or activates a substitute window");
            var invalidDirection = new Fixture();
            check(!new DirectionalFocus(invalidDirection.Api).Focus(new IntPtr(10), (WindowDirection)99, null, false) && invalidDirection.Reads == 0,
                "An invalid direction causes no native actions");
        }
        static void Races(Action<bool, string> check)
        {
            var changes = new Action<Fixture>[] {
                delegate(Fixture f) { f.Target.Identity.Process++; }, delegate(Fixture f) { f.Target.Identity.ProcessStarted++; },
                delegate(Fixture f) { f.Target.Identity.AppKey += " replaced"; }, delegate(Fixture f) { f.Target.Minimized = true; },
                delegate(Fixture f) { f.Target.Bounds.Top++; }, delegate(Fixture f) { f.Target.Cloaked = true; },
                delegate(Fixture f) { f.Windows.Remove(f.Target); }, delegate(Fixture f) { f.Source.Identity.ProcessStarted++; },
                delegate(Fixture f) { f.Source.Bounds.Left++; }, delegate(Fixture f) { f.Source.Visible = false; },
                delegate(Fixture f) { f.Foreground = new IntPtr(99); }
            };
            for (int index = 0; index < changes.Length; index++) {
                var fixture = new Fixture(); var change = changes[index];
                fixture.OnRead = delegate(int count) { if (count == 2) change(fixture); };
                check(!fixture.Run() && fixture.Activations == 0 && fixture.Status.Contains("Window changed"),
                    "Late HWND identity, position, visibility or foreground change prevents activation " + index);
            }
            var excludedLate = new Fixture(); var rules = new List<AppRule>();
            excludedLate.OnRead = delegate(int count) { if (count == 2) rules.Add(new AppRule { Key = excludedLate.Target.Identity.AppKey }); };
            check(!excludedLate.Run(rules) && excludedLate.Activations == 0, "An exclusion added during enumeration wins before focus activation");
            var fullscreenLate = new Fixture(); fullscreenLate.OnRead = delegate(int count) { if (count == 2) fullscreenLate.Target.Fullscreen = true; };
            check(!fullscreenLate.Run() && fullscreenLate.Activations == 0, "A target becoming fullscreen while the option is off cannot be activated");
            var sourceMutates = new Fixture(); sourceMutates.OnEnumerate = delegate { sourceMutates.Source.Identity.ProcessStarted++; };
            check(!sourceMutates.Run() && sourceMutates.Activations == 0, "Mutable native snapshots are copied so source process reuse cannot rewrite the captured identity");
            var targetMutates = new Fixture(); targetMutates.OnRead = delegate(int count) { if (count == 3) targetMutates.Target.Identity.ProcessStarted++; };
            check(!targetMutates.Run() && targetMutates.Activations == 0, "Copied selected identity rejects reuse during the final target read");
        }
    }
}
