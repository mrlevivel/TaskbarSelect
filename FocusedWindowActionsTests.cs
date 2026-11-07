using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class FocusedWindowActionsTests
    {
        sealed class Fixture
        {
            internal IntPtr Foreground = new IntPtr(42);
            internal FocusedWindowSnapshot Window = Snapshot();
            internal int Reads, Posts, PostError;
            internal bool PostAccepted = true, ReturnNullPost;
            internal Action<int> DuringRead = null;
            internal readonly FocusedWindowApi Api;
            internal Fixture()
            {
                Api = new FocusedWindowApi {
                    Foreground = delegate { return Foreground; },
                    Read = delegate(IntPtr handle) { Reads++; if (DuringRead != null) DuringRead(Reads); return Window; },
                    PostClose = delegate(IntPtr handle) {
                        if (handle != new IntPtr(42)) throw new Exception("Close target changed.");
                        Posts++; return ReturnNullPost ? null : new FocusedClosePostResult { Accepted = PostAccepted, Error = PostError };
                    }
                };
            }
        }
        static FocusedWindowSnapshot Snapshot()
        {
            return new FocusedWindowSnapshot { Window = new IntPtr(42), Root = new IntPtr(42), Process = 7,
                Thread = 8, ProcessStarted = 99, Visible = true, ClassName = "Neutral app", Device = "DISPLAY2" };
        }
        static Display Monitor(string device, string identity, bool primary = false)
        { return new Display { Device = device, Identity = identity, Primary = primary, Width = 1920, Height = 1080 }; }
        internal static void Run(Action<bool, string> check)
        {
            CloseRequests(check);
            ChangedWindows(check);
            MonitorSelection(check);
            PointerSelection(check);
            OwnedNativeBoundary(check);
        }
        static void CloseRequests(Action<bool, string> check)
        {
            var normal = new Fixture(); var result = new FocusedWindowActions(normal.Api).RequestClose(normal.Foreground);
            check(result.Requested && result.Error == 0 && normal.Posts == 1 && normal.Reads == 2 &&
                result.Status.StartsWith("Close requested.", StringComparison.Ordinal) && result.Status.IndexOf("remain open", StringComparison.Ordinal) >= 0,
                "Close enqueues one normal request and reports requested, never observed closed");
            var refused = new Fixture { PostAccepted = false, PostError = 5 };
            result = new FocusedWindowActions(refused.Api).RequestClose(refused.Foreground);
            check(!result.Requested && result.Error == 5 && refused.Posts == 1 && result.Status.Contains("Windows error 5"),
                "UIPI/elevated-app refusal preserves the actual Windows error without claiming a close");
            var unknown = new Fixture { PostAccepted = false };
            result = new FocusedWindowActions(unknown.Api).RequestClose(unknown.Foreground);
            check(!result.Requested && result.Error == 0 && result.Status.Contains("refused"), "Rejected enqueue with no reported error remains refused");
            var noResult = new Fixture { ReturnNullPost = true };
            check(!new FocusedWindowActions(noResult.Api).RequestClose(noResult.Foreground).Requested,
                "A missing enqueue result cannot be reported as a requested close");
            foreach (string name in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame" }) {
                var shell = new Fixture(); shell.Window.ClassName = name;
                check(!new FocusedWindowActions(shell.Api).RequestClose(shell.Foreground).Requested && shell.Posts == 0,
                    "Close guards shell window class " + name);
            }
            var changes = new Action<FocusedWindowSnapshot>[] {
                delegate(FocusedWindowSnapshot w) { w.Shell = true; },
                delegate(FocusedWindowSnapshot w) { w.ExtendedStyle = 0x08000000; },
                delegate(FocusedWindowSnapshot w) { w.Root = new IntPtr(43); },
                delegate(FocusedWindowSnapshot w) { w.Visible = false; },
                delegate(FocusedWindowSnapshot w) { w.Process = 0; },
                delegate(FocusedWindowSnapshot w) { w.Thread = 0; },
                delegate(FocusedWindowSnapshot w) { w.ProcessStarted = 0; },
                delegate(FocusedWindowSnapshot w) { w.ClassName = ""; },
                delegate(FocusedWindowSnapshot w) { w.Window = new IntPtr(43); }
            };
            for (int index = 0; index < changes.Length; index++) {
                var fixture = new Fixture(); changes[index](fixture.Window);
                check(!new FocusedWindowActions(fixture.Api).RequestClose(fixture.Foreground).Requested && fixture.Posts == 0,
                    "Close rejects an ineligible or incorrectly captured window " + index);
            }
            var inaccessible = new Fixture(); inaccessible.Window.Error = 5; inaccessible.Window.ProcessStarted = 0;
            result = new FocusedWindowActions(inaccessible.Api).RequestClose(inaccessible.Foreground);
            check(!result.Requested && result.Error == 5 && inaccessible.Posts == 0 && result.Status.Contains("could not be verified"),
                "An inaccessible process identity fails closed with the native verification error");
            var search = new Fixture();
            check(!new FocusedWindowActions(search.Api, delegate { return true; }).RequestClose(search.Foreground).Requested && search.Posts == 0,
                "Explicit activated search overlays never receive an app-close request");
            var settings = new Fixture(); settings.Window.ClassName = "WindowsForms10.Window.8.app.0.neutral";
            check(new FocusedWindowActions(settings.Api).RequestClose(settings.Foreground).Requested,
                "A regular TaskbarSelect-style settings form remains eligible for its normal close/hide behavior");
            foreach (IntPtr handle in new[] { IntPtr.Zero, new IntPtr(-1), new IntPtr(0xFFFF) }) {
                var invalid = new Fixture { Foreground = handle };
                check(!new FocusedWindowActions(invalid.Api).RequestClose(handle).Requested && invalid.Posts == 0 && invalid.Reads == 0,
                    "Close never posts to a null, broadcast or special handle " + handle);
            }
        }
        static void ChangedWindows(Action<bool, string> check)
        {
            var staleFocus = new Fixture { Foreground = new IntPtr(43) };
            check(!new FocusedWindowActions(staleFocus.Api).RequestClose(new IntPtr(42)).Requested && staleFocus.Reads == 0 && staleFocus.Posts == 0,
                "Close never substitutes a different current foreground for the captured app");
            var changes = new Action<Fixture>[] {
                delegate(Fixture f) { f.Window = null; },
                delegate(Fixture f) { f.Window.Process++; },
                delegate(Fixture f) { f.Window.Thread++; },
                delegate(Fixture f) { f.Window.ProcessStarted++; },
                delegate(Fixture f) { f.Window.ClassName += " changed"; },
                delegate(Fixture f) { f.Window.Root = new IntPtr(43); },
                delegate(Fixture f) { f.Window.Window = new IntPtr(43); },
                delegate(Fixture f) { f.Window.ExtendedStyle = 0x08000000; },
                delegate(Fixture f) { f.Window.Visible = false; },
                delegate(Fixture f) { f.Foreground = new IntPtr(43); }
            };
            for (int index = 0; index < changes.Length; index++) {
                var fixture = new Fixture(); var change = changes[index];
                fixture.DuringRead = delegate(int count) { if (count == 2) change(fixture); };
                check(!new FocusedWindowActions(fixture.Api).RequestClose(new IntPtr(42)).Requested && fixture.Posts == 0,
                    "Close rechecks captured HWND/process/thread/birth/class/eligibility/foreground before enqueue " + index);
            }
            var denied = new Fixture(); denied.DuringRead = delegate(int count) { if (count == 2) denied.Window.Error = 5; };
            var result = new FocusedWindowActions(denied.Api).RequestClose(denied.Foreground);
            check(!result.Requested && result.Error == 5 && denied.Posts == 0,
                "Late native identity verification failure prevents any close request");
        }
        static void MonitorSelection(Action<bool, string> check)
        {
            var primary = Monitor("DISPLAY3", "physical-main", true);
            var secondary = Monitor("DISPLAY2", "physical-second"); secondary.X = -1920;
            var top = Monitor("DISPLAY4", "physical-top"); top.Y = -1080;
            var displays = new[] { top, primary, secondary };
            var fixture = new Fixture(); var tracker = new LastFocusedMonitor(fixture.Api);
            check(tracker.Resolve(IntPtr.Zero, displays) == primary, "Without an app history search falls back to the actual primary display, not display 1/list order");
            foreach (var display in displays) {
                fixture.Window.Device = display.Device;
                check(tracker.Resolve(fixture.Foreground, displays) == display && tracker.LastIdentity == display.Identity,
                    "Focused-app targeting supports primary, stacked and negative-coordinate monitors without Ghost settings " + display.Device);
            }
            tracker.Observe(fixture.Foreground, displays);
            fixture.Window.ClassName = "Progman"; fixture.Window.Device = primary.Device;
            check(tracker.Resolve(fixture.Foreground, displays) == secondary,
                "Desktop focus retains the last app monitor instead of following the desktop/taskbar monitor");
            foreach (string shell in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "WorkerW", "MultitaskingViewFrame" }) {
                fixture.Window.ClassName = shell; tracker.Observe(fixture.Foreground, displays);
                check(tracker.Resolve(fixture.Foreground, displays) == secondary, "Monitor history ignores shell focus " + shell);
            }
            fixture.Window.ClassName = "Neutral app"; fixture.Window.ExtendedStyle = 0x08000000;
            check(tracker.Resolve(fixture.Foreground, displays) == secondary, "Noactivate overlays cannot retarget app search history");
            fixture.Window.ExtendedStyle = 0; fixture.Foreground = new IntPtr(43);
            check(tracker.Resolve(new IntPtr(42), displays) == secondary, "A stale captured foreground cannot update remembered monitor");
            fixture.Foreground = new IntPtr(42); fixture.Window.Root = new IntPtr(43);
            check(tracker.Resolve(fixture.Foreground, displays) == secondary, "Child/incorrect top-level focus cannot retarget app search history");
            fixture.Window.Root = fixture.Window.Window; fixture.Window.Device = secondary.Device;
            bool ignore = false; var popupTracker = new LastFocusedMonitor(fixture.Api, delegate { return ignore; });
            popupTracker.Observe(fixture.Foreground, displays); ignore = true; fixture.Window.Device = top.Device;
            check(popupTracker.Resolve(fixture.Foreground, displays) == secondary,
                "Activated GhostSearch popup focus retains the monitor captured before search opened");
            check(tracker.Resolve(IntPtr.Zero, new[] { primary, top }) == primary,
                "A disconnected remembered physical monitor falls back to a connected primary");
            var replacement = Monitor(secondary.Device, "new-physical-display");
            check(tracker.Resolve(IntPtr.Zero, new[] { replacement, primary }) == primary,
                "A reused device slot cannot inherit another physical monitor's remembered focus");
            var relabeled = Monitor("DISPLAY9", secondary.Identity); relabeled.X = 3840;
            check(tracker.Resolve(IntPtr.Zero, new[] { relabeled, primary }) == relabeled && tracker.LastDevice == relabeled.Device,
                "The same unique physical display remains the remembered target after device-slot renumbering");
            var duplicate = Monitor("DISPLAY10", secondary.Identity);
            check(tracker.Resolve(IntPtr.Zero, new[] { relabeled, duplicate, primary }) == primary,
                "Ambiguous physical identities never select an arbitrary remembered monitor");
            check(tracker.Resolve(IntPtr.Zero, new[] { relabeled, duplicate }) == null,
                "No usable focused/history/primary monitor returns unavailable, not an arbitrary display");
            check(new LastFocusedMonitor(fixture.Api).Resolve(IntPtr.Zero, new[] { primary, Monitor("DISPLAY8", "other-main", true) }) == null,
                "Multiple claimed primary displays are refused as ambiguous");
            var invalidPrimary = Monitor(primary.Device, primary.Identity, true); invalidPrimary.Width = 0;
            check(new LastFocusedMonitor(fixture.Api).Resolve(IntPtr.Zero, new[] { invalidPrimary }) == null,
                "Invalid zero-sized monitor snapshots are never returned as search destinations");
            ignore = false; fixture.Window.Device = secondary.Device; popupTracker.Observe(fixture.Foreground, displays);
            var noIdentity = Monitor("DISPLAY6", ""); fixture.Window.Device = noIdentity.Device;
            check(popupTracker.Resolve(fixture.Foreground, new[] { primary, noIdentity, secondary }) == noIdentity && popupTracker.LastIdentity == secondary.Identity,
                "Current monitor without physical identity is usable but cannot erase safe remembered identity");
            var duplicateDevice = Monitor(secondary.Device, "another-physical"); fixture.Window.Device = secondary.Device;
            check(new LastFocusedMonitor(fixture.Api).Resolve(fixture.Foreground, new[] { secondary, duplicateDevice, primary }) == primary,
                "Ambiguous current device mappings fall back honestly instead of selecting the first monitor");
            fixture.Window.Device = secondary.Device.ToLowerInvariant();
            check(new LastFocusedMonitor(fixture.Api).Resolve(fixture.Foreground, displays) == secondary,
                "Native display-device comparisons are case insensitive");
        }
        static void PointerSelection(Action<bool, string> check)
        {
            var primary = Monitor("DISPLAY3", "physical-main", true);
            var left = Monitor("DISPLAY2", "physical-left"); left.X = -1920;
            var upper = Monitor("DISPLAY4", "physical-upper"); upper.Y = -1080;
            var displays = new[] { upper, primary, left };
            var fixture = new Fixture(); fixture.Window.Device = primary.Device;
            var tracker = new LastFocusedMonitor(fixture.Api);
            check(tracker.Resolve(fixture.Foreground, displays) == primary, "Interaction history starts on the current focused app monitor");
            tracker.ObservePointerDown(new Point(-960, 540), displays);
            check(tracker.LastIdentity == left.Identity && tracker.LastDevice == left.Device,
                "A real mouse-down on blank desktop records its physical monitor even while foreground stays elsewhere");
            for (int poll = 0; poll < 5; poll++) {
                tracker.Observe(fixture.Foreground, displays);
                check(tracker.Resolve(fixture.Foreground, displays) == left,
                    "Unchanged foreground polling and search resolution preserve the latest desktop click: poll " + poll);
            }
            check(fixture.Posts == 0, "Pointer/focus tracking does not send window messages or close an app");
            tracker.ObservePointerDown(new Point(400, -500), displays);
            check(tracker.Resolve(fixture.Foreground, displays) == upper, "Global negative-Y coordinates select a stacked upper monitor");
            tracker.ObservePointerDown(new Point(400, 0), displays);
            check(tracker.Resolve(fixture.Foreground, displays) == primary, "The shared monitor boundary belongs to the lower monitor, using native exclusive bottom edges");
            tracker.ObservePointerDown(new Point(-1, 400), displays);
            check(tracker.Resolve(fixture.Foreground, displays) == left, "The pixel immediately left of the primary monitor selects the negative-X monitor");
            tracker.ObservePointerDown(new Point(-4000, -4000), displays);
            check(tracker.Resolve(fixture.Foreground, displays) == left, "A click outside all connected monitor bounds does not invent a target");

            // No pointer/hover or typing state is sampled by the poll API.
            for (int poll = 0; poll < 3; poll++) tracker.Observe(fixture.Foreground, displays);
            check(tracker.Resolve(fixture.Foreground, displays) == left, "Routine typing/hover time with unchanged app focus cannot retarget the last clicked monitor");
            tracker.ObserveForeground(fixture.Foreground, displays);
            check(tracker.Resolve(fixture.Foreground, displays) == primary, "A genuine native foreground event can reselect the app monitor even for the same HWND");
            tracker.ObservePointerDown(new Point(-960, 540), displays);
            fixture.Foreground = new IntPtr(43); fixture.Window.Window = fixture.Foreground; fixture.Window.Root = fixture.Foreground;
            fixture.Window.Process = 17; fixture.Window.Thread = 18; fixture.Window.ProcessStarted = 199; fixture.Window.Device = upper.Device;
            check(tracker.Resolve(fixture.Foreground, displays) == upper, "A different foreground app overrides an earlier blank-desktop click before the timer runs");
            tracker.ObservePointerDown(new Point(-960, 540), displays);
            fixture.Window.Device = primary.Device;
            tracker.Observe(fixture.Foreground, displays);
            check(tracker.Resolve(fixture.Foreground, displays) == primary,
                "A monitor hotkey moving the same foreground HWND to another native device updates interaction history on the next poll");
            tracker.ObservePointerDown(new Point(-960, 540), displays);
            fixture.Window.ProcessStarted++;
            tracker.Observe(fixture.Foreground, displays);
            check(tracker.Resolve(fixture.Foreground, displays) == primary, "A reused HWND with changed process identity cannot keep a stale pointer baseline");

            var firstClick = new LastFocusedMonitor(fixture.Api);
            firstClick.ObservePointerDown(new Point(500, -500), displays);
            check(firstClick.Resolve(fixture.Foreground, displays) == upper, "A desktop click captures its foreground baseline even before the first timer poll");
            var disconnected = new LastFocusedMonitor(fixture.Api);
            disconnected.ObservePointerDown(new Point(-960, 540), displays);
            check(disconnected.Resolve(fixture.Foreground, new[] { primary, upper }) == primary,
                "Disconnecting the clicked physical monitor falls back to the connected primary without following a reused slot");
            var replacement = Monitor(left.Device, "replacement-left"); replacement.X = left.X;
            check(disconnected.Resolve(fixture.Foreground, new[] { primary, upper, replacement }) == primary,
                "Replacing the clicked monitor at the same coordinates/device cannot inherit its physical selection");
            var renumbered = Monitor("DISPLAY9", left.Identity); renumbered.X = left.X;
            check(disconnected.Resolve(fixture.Foreground, new[] { primary, upper, renumbered }) == renumbered && disconnected.LastDevice == renumbered.Device,
                "The clicked unique physical monitor is retained when Windows renumbers its device slot");
            var duplicate = Monitor("DISPLAY10", left.Identity); duplicate.X = -3840;
            check(disconnected.Resolve(fixture.Foreground, new[] { primary, renumbered, duplicate }) == primary,
                "Duplicate clicked physical identities fall back instead of choosing an arbitrary display");

            var invalidClicks = new LastFocusedMonitor(fixture.Api);
            invalidClicks.Observe(fixture.Foreground, displays);
            var overlapping = Monitor("DISPLAY5", "overlap"); overlapping.X = left.X;
            invalidClicks.ObservePointerDown(new Point(-960, 540), new[] { primary, left, overlapping });
            check(invalidClicks.Resolve(fixture.Foreground, displays) == primary, "A mouse-down in overlapping monitor bounds is rejected as ambiguous");
            invalidClicks.ObservePointerDown(new Point(-960, 540), new[] { primary, left, duplicate });
            check(invalidClicks.Resolve(fixture.Foreground, displays) == primary, "A clicked monitor with duplicate physical identity cannot retarget history");
            var duplicateDevice = Monitor(left.Device, "duplicate-device"); duplicateDevice.X = -3840;
            invalidClicks.ObservePointerDown(new Point(-960, 540), new[] { primary, left, duplicateDevice });
            check(invalidClicks.Resolve(fixture.Foreground, displays) == primary, "A clicked monitor with ambiguous native device mapping cannot retarget history");
            var unknown = Monitor(left.Device, ""); unknown.X = left.X;
            invalidClicks.ObservePointerDown(new Point(-960, 540), new[] { primary, unknown });
            check(invalidClicks.Resolve(fixture.Foreground, displays) == primary, "Blank physical identity cannot be stored by a desktop click");

            IntPtr searchWindow = new IntPtr(84);
            var search = new LastFocusedMonitor(fixture.Api, delegate(IntPtr window) { return window == searchWindow; });
            search.Observe(fixture.Foreground, displays);
            var app = fixture.Window.Copy(); IntPtr appWindow = fixture.Foreground;
            fixture.Foreground = searchWindow; fixture.Window.Window = searchWindow; fixture.Window.Root = searchWindow; fixture.Window.Device = upper.Device;
            search.ObserveForeground(searchWindow, displays);
            search.Observe(searchWindow, displays);
            check(search.Resolve(searchWindow, displays) == primary, "Search popup activation and polling do not erase the previous app interaction");
            search.ObservePointerDown(new Point(-960, 540), displays);
            check(search.Resolve(searchWindow, displays) == left, "An outside blank-desktop click is recorded while the search popup still has foreground");
            fixture.Foreground = appWindow; fixture.Window = app.Copy();
            search.Observe(fixture.Foreground, displays);
            check(search.Resolve(fixture.Foreground, displays) == left, "A timer poll before Search's restoration event preserves the outside desktop click");
            search.ObserveForeground(fixture.Foreground, displays);
            check(search.Resolve(fixture.Foreground, displays) == left, "Restoring the same prior app after Search dismissal does not erase the outside desktop click");
            search.ObserveForeground(fixture.Foreground, displays);
            check(search.Resolve(fixture.Foreground, displays) == primary, "A later genuine reactivation of the same app still updates interaction history");

            fixture.Foreground = searchWindow; fixture.Window.Window = searchWindow; fixture.Window.Root = searchWindow; fixture.Window.Device = upper.Device;
            search.ObservePointerDown(new Point(-960, 540), displays);
            fixture.Foreground = new IntPtr(93); fixture.Window = app.Copy(); fixture.Window.Window = fixture.Foreground; fixture.Window.Root = fixture.Foreground;
            fixture.Window.Process = 29; fixture.Window.ProcessStarted = 399; fixture.Window.Device = upper.Device;
            search.ObserveForeground(fixture.Foreground, displays);
            check(search.Resolve(fixture.Foreground, displays) == upper, "A genuinely different app after Search dismisses overrides its ignored-focus restoration guard");
            fixture.Window.ClassName = "Progman"; fixture.Window.Device = primary.Device;
            search.ObserveForeground(fixture.Foreground, displays);
            search.Observe(fixture.Foreground, displays);
            check(search.Resolve(fixture.Foreground, displays) == upper, "Shell focus cannot erase the last physical interaction");
            check(fixture.Posts == 0, "All pointer interaction regressions use pure snapshots without native UI input");
        }
        sealed class OwnedWindow : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }
        static void OwnedNativeBoundary(Action<bool, string> check)
        {
            IntPtr foreground = FocusedWindowNative.Foreground(); Point pointer = Cursor.Position;
            using (var window = new OwnedWindow { Text = "Neutral keyboard close boundary fixture", Opacity = 0,
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(20, 20, 160, 100) }) {
                int closes = 0; window.FormClosing += delegate(object sender, FormClosingEventArgs e) { closes++; e.Cancel = true; };
                window.Show();
                var native = FocusedWindowNative.Read(window.Handle);
                check(native != null && native.Window == window.Handle && native.Root == window.Handle && native.Visible &&
                    native.Process != 0 && native.Thread != 0 && native.ProcessStarted > 0 && native.Error == 0 && !String.IsNullOrEmpty(native.ClassName),
                    "Native focused-window capture reads exact owned HWND/process/thread/birth/class identity");
                int posts = 0;
                var api = new FocusedWindowApi { Foreground = delegate { return window.Handle; },
                    Read = FocusedWindowNative.Read, PostClose = delegate { posts++; throw new Exception("Noactivate policy must stop close enqueue."); } };
                check(!new FocusedWindowActions(api).RequestClose(window.Handle).Requested && posts == 0 && closes == 0,
                    "Actual noactivate owned HWND is blocked by the production close policy");
                // This directly exercises only the native enqueue boundary on
                // our fixture. It does not bypass policy for a personal app.
                var posted = FocusedWindowNative.PostClose(window.Handle);
                check(posted.Accepted && posted.Error == 0 && closes == 0,
                    "Native WM_CLOSE accepted enqueue does not synchronously close or dispatch the owned window");
                Application.DoEvents();
                check(closes == 1 && !window.IsDisposed && Native.IsWindow(window.Handle),
                    "Owned app can refuse WM_CLOSE after accepted enqueue, proving requested does not mean closed");
            }
            check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                "Keyboard-action native fixtures preserve actual foreground and pointer");
            check(FocusedWindowNative.Read(IntPtr.Zero) == null && FocusedWindowNative.Read(new IntPtr(-1)) == null &&
                FocusedWindowNative.Read(new IntPtr(0xFFFF)) == null, "Native capture safely rejects null and special handles");
        }
    }
}
