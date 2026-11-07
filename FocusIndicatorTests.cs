using System;
using System.Collections.Generic;
using System.Drawing;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class FocusIndicatorTests
    {
        sealed class Surface : IFocusIndicatorSurface
        {
            internal int Presents, Disposals;
            internal bool Fail, MovingFailure;
            internal Action DuringPresent;
            internal FocusIndicatorVisual Last;
            public IntPtr Window { get { return new IntPtr(700); } }
            public void Present(FocusIndicatorVisual visual)
            { Presents++; Last = visual; if (DuringPresent != null) DuringPresent();
                if (MovingFailure) throw new FocusIndicatorGeometryChangedException();
                if (Fail) throw new InvalidOperationException("Fixture failure"); }
            public void Dispose() { Disposals++; }
        }
        sealed class Fixture
        {
            internal IntPtr Foreground = new IntPtr(42);
            internal FocusIndicatorTarget Target = TargetWindow();
            internal bool Desktop = true, Dark = true, Ignore;
            internal bool PointerDown, WatchFails;
            internal long Clock = 1000;
            internal int Reads, Creations;
            internal Action<int> DuringRead;
            internal readonly List<Surface> Surfaces = new List<Surface>();
            internal Surface Next = new Surface();
            internal readonly List<Events> Observers = new List<Events>();
            internal Action DuringWatch;
            internal Action DuringCreate;
            internal FocusIndicator Controller;
            internal Fixture()
            {
                var api = new FocusIndicatorApi {
                    Foreground = delegate { return Foreground; },
                    Read = delegate(IntPtr window) { Reads++; if (DuringRead != null) DuringRead(Reads); return Target; },
                    DesktopAllowed = delegate { return Desktop; }, Dark = delegate { return Dark; },
                    GlowAllowed = delegate { return true; },
                    Now = delegate { return Clock; }, PointerButtonDown = delegate { return PointerDown; },
                    Watch = delegate(FocusedWindowSnapshot target, Action<FocusIndicatorChange> changed) {
                        if (WatchFails) throw new Win32Exception(8, "Fixture hook failure");
                        var observer = new Events(target.Window, changed); Observers.Add(observer);
                        if (DuringWatch != null) DuringWatch(); return observer;
                    }
                };
                Controller = new FocusIndicator(api, delegate { Creations++; var value = Next; Surfaces.Add(value); Next = new Surface();
                    if (DuringCreate != null) DuringCreate(); return value; },
                    delegate { return Ignore; });
            }
            internal void Update(int style = 1) { Controller.Update(style, "Original", false); }
        }
        sealed class Events : IFocusIndicatorEvents
        {
            internal readonly IntPtr Target;
            internal readonly Action<FocusIndicatorChange> Callback;
            internal int Disposals;
            internal Events(IntPtr target, Action<FocusIndicatorChange> callback) { Target = target; Callback = callback; }
            internal void Emit(FocusIndicatorChangeKind kind, IntPtr window = default(IntPtr), int objectId = 0, int child = 0)
            { Callback(new FocusIndicatorChange { Kind = kind, Window = window == IntPtr.Zero ? Target : window, Object = objectId, Child = child }); }
            public void Dispose() { Disposals++; }
        }
        static FocusIndicatorTarget TargetWindow()
        {
            return new FocusIndicatorTarget { Identity = new FocusedWindowSnapshot { Window = new IntPtr(42), Root = new IntPtr(42),
                    Process = 7, Thread = 8, ProcessStarted = 99, Visible = true, ClassName = "Neutral app", Device = "DISPLAY2" },
                Frame = new Rectangle(100, 100, 800, 600), Monitors = new[] { new Rectangle(0, 0, 1920, 1080) }, Dpi = 96 };
        }
        internal static void Run(Action<bool, string> check)
        {
            NeutralReadinessPolicyChecks(check);
            RunTargets(check); NativeSurface(check); NativeEvents(check); NativeAlphaCompositor(check);
        }
        // This entry uses only injected identities/events/surfaces and in-memory
        // geometry. It never creates a native window, hook or input operation.
        internal static void RunTargets(Action<bool, string> check)
        {
            Lifecycle(check); Guards(check); Races(check); MovementEvents(check); Geometry(check); ShellSurfaceChecks(check);
        }
        static void ShellSurfaceChecks(Action<bool, string> check)
        {
            const string windows = @"C:\Windows";
            const string start = @"C:\Windows\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe";
            const string search = @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe";
            check(FocusIndicatorNative.IsShellSurface(start, windows) && FocusIndicatorNative.IsShellSurface(search, windows) &&
                FocusIndicatorNative.IsShellSurface(start.ToLowerInvariant(), windows.ToUpperInvariant()),
                "The focus-only classifier recognizes exact Windows Start and Search host paths independent of Windows path casing");
            foreach (string unrelated in new[] { null, "", "StartMenuExperienceHost.exe",
                @"C:\Users\Neutral\StartMenuExperienceHost.exe", start + ".old", search.Replace("Client.CBS", "Client.CBS-copy"),
                start.Replace(@"C:\Windows\", @"C:\WindowsBackup\") })
                check(!FocusIndicatorNative.IsShellSurface(unrelated, windows),
                    "A missing, copied, decorated or differently located executable cannot become a Windows shell-surface match");
            check(!FocusIndicatorNative.IsShellSurface(start, @"D:\Windows"),
                "Shell-host classification is tied to the configured Windows directory rather than a drive-independent basename");

            var first = new Fixture();
            first.Target = new FocusIndicatorTarget { Identity = TargetWindow().Identity, ShellSurface = true };
            var copy = first.Target.Copy(); copy.ShellSurface = false;
            check(first.Target.ShellSurface && !copy.ShellSurface &&
                FocusedWindowPolicy.Eligible(first.Target.Identity, delegate(IntPtr ignored) { return false; }),
                "Shell-surface observation is copied independently without modifying shared movement eligibility");
            first.Update();
            check(!first.Controller.Visible && first.Creations == 0 && first.Observers.Count == 0 && first.Reads == 1,
                "A shell-surface sentinel is rejected before frame geometry, event registration or overlay creation");
            first.Controller.Dispose();

            var drawableShell = TargetWindow(); drawableShell.ShellSurface = true;
            bool allVisualsRefused = true;
            foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge })
                for (int lighting = 0; lighting < 4; lighting++)
                    for (int finish = 0; finish < 2; finish++)
                        allVisualsRefused &= FocusIndicatorVisual.Create(drawableShell, mode, "Original", true, false, lighting, finish) == null;
            check(allVisualsRefused,
                "A shell surface with otherwise drawable bounds cannot construct any frame style, lighting or finish");

            var oldFrame = new Fixture(); oldFrame.Update(); var oldSurface = oldFrame.Surfaces[0];
            oldFrame.Target.ShellSurface = true; oldFrame.Update();
            check(!oldFrame.Controller.Visible && oldFrame.Creations == 1 && oldSurface.Disposals == 1 && oldFrame.Observers[0].Disposals == 1,
                "Switching from an app to Start or Search immediately removes its previous frame and scoped observer");
            oldFrame.Controller.Dispose();

            var race = new Fixture();
            race.DuringRead = delegate(int read) { if (read == 2) race.Target.ShellSurface = true; };
            race.Update();
            check(!race.Controller.Visible && race.Creations == 0 && race.Observers[0].Disposals == 1,
                "A shell-surface transition between independent reads cannot publish a stale application frame");
            race.Controller.Dispose();

            foreach (var ordinary in new[] {
                new { Class = "Windows.UI.Core.CoreWindow", Image = @"C:\Program Files\WindowsApps\Calculator\CalculatorApp.exe" },
                new { Class = "CabinetWClass", Image = @"C:\Windows\explorer.exe" },
                new { Class = "TaskManagerWindow", Image = @"C:\Windows\System32\Taskmgr.exe" },
                new { Class = "Chrome_WidgetWin_1", Image = @"C:\Program Files\Telegram Desktop\Telegram.exe" }
            }) {
                var app = new Fixture(); app.Target.Identity.ClassName = ordinary.Class;
                app.Target.ShellSurface = FocusIndicatorNative.IsShellSurface(ordinary.Image, windows);
                app.Update();
                check(app.Controller.Visible && app.Creations == 1,
                    "Ordinary applications retain their focus frame without generic class or host-name exclusion: " + ordinary.Class);
                app.Controller.Dispose();
            }
        }
        static void Lifecycle(Action<bool, string> check)
        {
            var f = new Fixture(); f.Update(0);
            check(!f.Controller.Visible && f.Reads == 0 && f.Creations == 0, "Focus indicator OFF does not read target or create surfaces");
            f.Controller.Update(1, "Original", true);
            check(f.Reads == 0 && f.Creations == 0, "Paused indicator does not read target or create surfaces");
            f.Update(); var first = f.Surfaces[0];
            check(f.Controller.Visible && f.Controller.Handle == first.Window && first.Presents == 1 && f.Reads == 2,
                "Verified foreground creates exactly one indicator with two independent identity reads");
            f.Target.Frame = new Rectangle(400, -700, 500, 400); f.Target.Monitors = new[] { new Rectangle(0, -1080, 1920, 1080) };
            f.Controller.Update(2, "Lavender", false);
            check(!f.Controller.Visible && first.Disposals == 1, "A between-tick geometry change removes the old outline before reuse");
            f.Clock += 120; f.Controller.Update(2, "Lavender", false);
            var moved = f.Surfaces[1];
            check(f.Creations == 2 && moved.Presents == 1 && moved.Last.Mode == FocusIndicatorMode.InsetEdge && moved.Last.Bounds.Y == -700,
                "Settled target recreates its frame at the latest position without activation or movement APIs");
            f.Controller.Hide(); f.Controller.Hide();
            check(first.Disposals == 1 && moved.Disposals == 1 && !f.Controller.Visible && f.Controller.Handle == IntPtr.Zero && f.Observers[0].Disposals == 1,
                "Hide disposes surfaces and event subscriptions once and releases their handles");
            f.Update(); var second = f.Surfaces[2]; f.Controller.Dispose(); f.Controller.Dispose(); f.Update();
            check(second.Disposals == 1 && f.Creations == 3 && !f.Controller.Visible && f.Observers[1].Disposals == 1, "Disposed service never recreates a surface or event hook");
            var failed = new Fixture(); failed.Next.Fail = true; failed.Update();
            check(!failed.Controller.Visible && failed.Surfaces[0].Disposals == 1 && failed.Controller.Error != null && !failed.Controller.Error.Contains("Fixture failure"),
                "Surface failure removes the outline, releases resources and reports safe diagnostics");
            failed.Update(); check(failed.Controller.Visible && failed.Controller.Error == null, "A later successful indicator update clears previous failure diagnostics");
            failed.Controller.Dispose();
            var pausedFailure = new Fixture(); pausedFailure.Next.Fail = true; pausedFailure.Update();
            pausedFailure.Controller.Update(1, "Original", true);
            check(pausedFailure.Controller.Error == null && !pausedFailure.Controller.Visible, "Pausing clears a previous indicator failure diagnostic");
            pausedFailure.Controller.Dispose();
            var offFailure = new Fixture(); offFailure.Next.Fail = true; offFailure.Update(); offFailure.Update(0);
            check(offFailure.Controller.Error == null && !offFailure.Controller.Visible, "Turning indicator OFF clears a previous failure diagnostic");
            offFailure.Controller.Dispose();
        }
        static void Guards(Action<bool, string> check)
        {
            var changes = new Action<Fixture>[] {
                delegate(Fixture f) { f.Desktop = false; }, delegate(Fixture f) { f.Ignore = true; },
                delegate(Fixture f) { f.Foreground = IntPtr.Zero; }, delegate(Fixture f) { f.Foreground = new IntPtr(-1); },
                delegate(Fixture f) { f.Foreground = new IntPtr(0xFFFF); }, delegate(Fixture f) { f.Target = null; },
                delegate(Fixture f) { f.Target.Minimized = true; }, delegate(Fixture f) { f.Target.Cloaked = true; },
                delegate(Fixture f) { f.Target.Fullscreen = true; }, delegate(Fixture f) { f.Target.Menu = true; },
                delegate(Fixture f) { f.Target.Identity.Visible = false; }, delegate(Fixture f) { f.Target.Identity.Shell = true; },
                delegate(Fixture f) { f.Target.Identity.ExtendedStyle = 0x80; }, delegate(Fixture f) { f.Target.Identity.ExtendedStyle = 0x08000000; },
                delegate(Fixture f) { f.Target.Identity.Root = new IntPtr(43); }, delegate(Fixture f) { f.Target.Identity.Window = new IntPtr(43); },
                delegate(Fixture f) { f.Target.Identity.Process = 0; }, delegate(Fixture f) { f.Target.Identity.Thread = 0; },
                delegate(Fixture f) { f.Target.Identity.ProcessStarted = 0; }, delegate(Fixture f) { f.Target.Identity.Error = 5; },
                delegate(Fixture f) { f.Target.Identity.ClassName = ""; }, delegate(Fixture f) { f.Target.Frame = Rectangle.Empty; },
                delegate(Fixture f) { f.Target.Monitors = null; }, delegate(Fixture f) { f.Target.Monitors = new Rectangle[0]; },
                delegate(Fixture f) { f.Target.Monitors = new[] { new Rectangle(5000, 5000, 100, 100) }; }
            };
            for (int i = 0; i < changes.Length; i++) {
                var f = new Fixture(); f.Update(); var surface = f.Surfaces[0]; changes[i](f); f.Update();
                check(!f.Controller.Visible && surface.Disposals == 1 && f.Creations == 1,
                    "Indicator invalid/paused/secure/fullscreen/transient target hides previous surface " + i);
                f.Controller.Dispose();
            }
            foreach (string name in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame", "#32768", "tooltips_class32" }) {
                var f = new Fixture(); f.Target.Identity.ClassName = name; f.Update();
                check(f.Creations == 0, "Indicator excludes shell/menu/tooltip class " + name); f.Controller.Dispose();
            }
            foreach (int invalid in new[] { -1, 3, Int32.MaxValue, Int32.MinValue }) {
                var f = new Fixture(); f.Update(invalid); check(f.Reads == 0 && f.Creations == 0, "Unknown indicator mode normalizes to OFF " + invalid);
                f.Controller.Dispose();
            }
            var own = new Fixture(); own.Update(); own.Foreground = own.Controller.Handle; own.Update();
            check(!own.Controller.Visible && own.Creations == 1, "The indicator can never outline itself"); own.Controller.Dispose();
        }
        static void Races(Action<bool, string> check)
        {
            var changes = new Action<Fixture>[] {
                delegate(Fixture f) { f.Target.Identity.Process++; }, delegate(Fixture f) { f.Target.Identity.Thread++; },
                delegate(Fixture f) { f.Target.Identity.ProcessStarted++; }, delegate(Fixture f) { f.Target.Identity.ClassName = "Reused handle"; },
                delegate(Fixture f) { f.Target.Identity.Root = new IntPtr(43); }, delegate(Fixture f) { f.Target.Minimized = true; },
                delegate(Fixture f) { f.Target.Cloaked = true; }, delegate(Fixture f) { f.Target.Menu = true; },
                delegate(Fixture f) { f.Target.Fullscreen = true; }, delegate(Fixture f) { f.Foreground = new IntPtr(43); },
                delegate(Fixture f) { f.Desktop = false; }, delegate(Fixture f) { f.Target = null; }
            };
            for (int i = 0; i < changes.Length; i++) {
                var f = new Fixture(); int index = i;
                f.DuringRead = delegate(int read) { if (read == 2) changes[index](f); }; f.Update();
                check(f.Creations == 0 && !f.Controller.Visible, "Copied identity and foreground refuse a read race before show " + i);
                f.Controller.Dispose();
            }
            var changed = new Fixture(); changed.Next.DuringPresent = delegate { changed.Foreground = new IntPtr(99); }; changed.Update();
            check(!changed.Controller.Visible && changed.Surfaces[0].Disposals == 1, "Foreground change during surface creation immediately removes stale outline");
            changed.Controller.Dispose();
            var secure = new Fixture(); secure.Next.DuringPresent = delegate { secure.Desktop = false; }; secure.Update();
            check(!secure.Controller.Visible && secure.Surfaces[0].Disposals == 1, "Desktop switch during surface creation immediately removes outline");
            secure.Controller.Dispose();
            var api = new FocusIndicatorApi { Read = delegate { throw new InvalidOperationException("Fixture inaccessible"); },
                DesktopAllowed = delegate { return true; }, Foreground = delegate { return new IntPtr(42); } };
            using (var controller = new FocusIndicator(api, delegate { throw new Exception("Surface should not exist"); })) {
                controller.Update(1, "Original", false); check(!controller.Visible, "Native observation exceptions hide safely without interrupting the application timer");
            }
        }
        static void MovementEvents(Action<bool, string> check)
        {
            var location = new Fixture(); location.Update(); var old = location.Surfaces[0]; int reads = location.Reads;
            location.Target.Frame.Offset(40, 20); location.Observers[0].Emit(FocusIndicatorChangeKind.Location);
            check(!location.Controller.Visible && old.Disposals == 1 && location.Reads == reads,
                "Between-tick location event immediately removes stale outline without native target reads");
            location.Update(); location.Clock += 119; location.Update();
            check(!location.Controller.Visible && location.Creations == 1, "Moving frame stays hidden until geometry is quiet for 120ms");
            location.Clock++; location.Update();
            check(location.Controller.Visible && location.Surfaces[1].Last.Target.Frame == location.Target.Frame && location.Observers.Count == 1,
                "Settled geometry returns the outline at fresh bounds while retaining one target subscription");
            location.Controller.Dispose();

            var drag = new Fixture(); drag.Update(); drag.Controller.ObserveMoveSize(drag.Foreground, true);
            check(!drag.Controller.Visible && drag.Surfaces[0].Disposals == 1, "Direct own move-size start hides between polling ticks");
            drag.Observers[0].Emit(FocusIndicatorChangeKind.MoveEnd); drag.Clock += 500; drag.Update(); drag.Clock += 500; drag.Update();
            check(!drag.Controller.Visible, "Delayed asynchronous END cannot override a live direct own move-size loop");
            drag.Target.Frame.Offset(100, 0); drag.Controller.ObserveMoveSize(drag.Foreground, false); drag.Update(); drag.Clock += 120; drag.Update();
            check(drag.Controller.Visible && drag.Surfaces[1].Last.Target.Frame == drag.Target.Frame, "Direct move-size exit restores only the final settled position");
            drag.Observers[0].Emit(FocusIndicatorChangeKind.MoveStart); drag.Update(); drag.Clock += 120; drag.Update();
            check(drag.Controller.Visible, "Delayed stale START with released buttons and nonmoving native state cannot leave the cue permanently hidden");
            drag.Controller.Dispose();

            var missingStart = new Fixture(); missingStart.Update(); missingStart.Target.Moving = true; missingStart.Update();
            missingStart.Clock += 500; missingStart.Update();
            check(!missingStart.Controller.Visible && missingStart.Surfaces[0].Disposals == 1,
                "Native GUI_INMOVESIZE/MoveSize state suppresses dragging even if START notification is lost");
            missingStart.Target.Moving = false; missingStart.Update(); missingStart.Clock += 120; missingStart.Update();
            check(missingStart.Controller.Visible, "Native nonmoving state with stable geometry recovers when no END notification arrives");
            missingStart.Controller.Dispose();

            var held = new Fixture(); held.Update(); held.PointerDown = true;
            held.Observers[0].Emit(FocusIndicatorChangeKind.MoveStart); held.Update(); held.Clock += 500; held.Update();
            check(!held.Controller.Visible, "Held pointer button keeps a native START suppressed if END is missing");
            held.Observers[0].Emit(FocusIndicatorChangeKind.MoveEnd); held.Target.Moving = true; held.Update(); held.Clock += 500; held.Update();
            check(!held.Controller.Visible, "Early native END cannot show a frame while GUI move-size state is still active");
            held.PointerDown = false; held.Target.Moving = false; held.Update(); held.Clock += 120; held.Update();
            check(held.Controller.Visible, "Released pointer, inactive native move state and settled bounds recover after mixed drag events");
            held.Controller.Dispose();

            var ignored = new Fixture(); ignored.Update(); var original = ignored.Surfaces[0];
            ignored.Observers[0].Emit(FocusIndicatorChangeKind.Location, new IntPtr(88));
            ignored.Observers[0].Emit(FocusIndicatorChangeKind.Location, objectId: -4, child: 1);
            ignored.Observers[0].Emit(FocusIndicatorChangeKind.Location, objectId: 1);
            ignored.Controller.ObserveMoveSize(new IntPtr(88), true);
            check(ignored.Controller.Visible && original.Disposals == 0, "Unrelated HWND/object/child/direct move notifications cannot remove an active cue");
            ignored.Observers[0].Emit(FocusIndicatorChangeKind.Location, objectId: -4);
            check(!ignored.Controller.Visible, "Target client-object self-location notification removes a possibly stale frame");
            ignored.Controller.Dispose();

            var switched = new Fixture(); switched.Update(); Events obsolete = switched.Observers[0];
            switched.Controller.ObserveMoveSize(switched.Foreground, true);
            switched.Foreground = new IntPtr(99); switched.Target.Identity.Window = switched.Target.Identity.Root = switched.Foreground; switched.Update();
            check(switched.Controller.Visible && obsolete.Disposals == 1 && switched.Observers.Count == 2,
                "Foreground change while dragging clears the old movement state and replaces its scoped watcher");
            obsolete.Emit(FocusIndicatorChangeKind.MoveStart); obsolete.Emit(FocusIndicatorChangeKind.Hidden); obsolete.Emit(FocusIndicatorChangeKind.Destroyed);
            check(switched.Controller.Visible && switched.Observers[1].Disposals == 0, "Queued old watcher callbacks cannot hide or dispose a new target's surface");
            switched.Controller.Hide(); var latest = switched.Observers[1]; latest.Emit(FocusIndicatorChangeKind.MoveStart);
            check(!switched.Controller.Visible && latest.Disposals == 1, "Queued callbacks after Hide do not recreate disposed watcher or surface");
            switched.Controller.Dispose(); latest.Emit(FocusIndicatorChangeKind.Location);
            check(latest.Disposals == 1, "Service disposal unhooks once and rejects late callbacks");

            foreach (FocusIndicatorChangeKind kind in new[] { FocusIndicatorChangeKind.Hidden, FocusIndicatorChangeKind.Destroyed }) {
                var construction = new Fixture(); construction.DuringWatch = delegate { construction.Observers[0].Emit(kind); }; construction.Update();
                check(!construction.Controller.Visible && construction.Creations == 0 && construction.Observers[0].Disposals == 1 && construction.Controller.Error == null,
                    "Invalidated subscription construction disposes its returned local watcher and never shows " + kind);
                construction.DuringWatch = null; construction.Update();
                check(construction.Controller.Visible && construction.Observers.Count == 2, "A fresh target observation recovers after rejected subscription construction " + kind);
                construction.Controller.Dispose();
                var readRace = new Fixture(); readRace.Update(); readRace.DuringRead = delegate { readRace.Observers[0].Emit(kind); }; readRace.Update();
                check(!readRace.Controller.Visible && readRace.Creations == 1, "Hide/destroy during target capture invalidates the in-progress update " + kind);
                readRace.Controller.Dispose();
                var createRace = new Fixture(); createRace.DuringCreate = delegate { createRace.Observers[0].Emit(kind); }; createRace.Update();
                check(!createRace.Controller.Visible && createRace.Surfaces[0].Presents == 0 && createRace.Surfaces[0].Disposals == 1,
                    "Hide/destroy during surface creation disposes the unpublished surface before Present " + kind);
                createRace.Controller.Dispose();
            }
            var failed = new Fixture { WatchFails = true }; failed.Update();
            check(!failed.Controller.Visible && failed.Creations == 0 && failed.Controller.Error.Contains("Windows error 8"),
                "Failed geometry hook does not silently fall back to a detached polling-only outline");
            failed.WatchFails = false; failed.Update();
            check(failed.Controller.Visible && failed.Controller.Error == null, "Successful hook registration clears a previous observation failure");
            failed.Controller.Dispose();

            var presentRace = new Fixture(); presentRace.Next.DuringPresent = delegate { presentRace.Observers[0].Emit(FocusIndicatorChangeKind.Location); }; presentRace.Update();
            check(!presentRace.Controller.Visible && presentRace.Surfaces[0].Disposals == 1, "Geometry notification during Present removes the just-created stale frame");
            presentRace.Controller.Dispose();
            var finalBounds = new Fixture(); finalBounds.Next.MovingFailure = true; finalBounds.Update();
            check(!finalBounds.Controller.Visible && finalBounds.Controller.Error == null && finalBounds.Observers[0].Disposals == 0,
                "Expected final geometry change hides safely without unavailable diagnostics or dropping target observation");
            finalBounds.Update(); finalBounds.Clock += 120; finalBounds.Update();
            check(finalBounds.Controller.Visible && finalBounds.Controller.Error == null, "Fresh settled frame recovers after final native geometry refusal");
            finalBounds.Controller.Dispose();
            var betweenReads = new Fixture(); betweenReads.DuringRead = delegate(int read) { if (read == 2) betweenReads.Target.Frame.Offset(1, 0); }; betweenReads.Update();
            check(!betweenReads.Controller.Visible && betweenReads.Creations == 0, "Geometry change between identity reads cannot publish an outdated outline");
            betweenReads.Controller.Dispose();
            check(FocusIndicatorNative.Moving(2, IntPtr.Zero) && FocusIndicatorNative.Moving(0, new IntPtr(42)) &&
                !FocusIndicatorNative.Moving(4, IntPtr.Zero), "Native move-size policy covers GUI flag and MoveSize handle independently of menu flags");
        }
        static void Geometry(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 144, 192, 288, 384 }) {
                var target = TargetWindow(); target.Dpi = dpi;
                var muted = FocusIndicatorVisual.Create(target, FocusIndicatorMode.MutedFrame, "Original", true);
                var inset = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, "Original", true);
                check(muted.Thickness == (2 * dpi + 48) / 96 && inset.Thickness == muted.Thickness && inset.OuterThickness < inset.Thickness,
                    "Both frame treatments retain two logical pixels at mixed DPI " + dpi);
                using (Region ring = muted.Ring(0, muted.Thickness)) {
                    check(ring.IsVisible(0, 300) && ring.IsVisible(400, 0) && !ring.IsVisible(400, 300) && !ring.IsVisible(40, 40),
                        "Indicator region remains hollow at DPI " + dpi);
                }
                check(muted.Outer == muted.Inner && inset.Outer != inset.Inner, "Muted is uniform and inset has distinct neutral/accent rings " + dpi);
                using (var bitmap = new Bitmap(target.Frame.Width, target.Frame.Height))
                using (Graphics graphics = Graphics.FromImage(bitmap)) {
                    graphics.Clear(Color.Magenta); inset.Paint(graphics);
                    check(bitmap.GetPixel(100, 0).ToArgb() == inset.Outer.ToArgb() &&
                        bitmap.GetPixel(100, inset.Thickness - 1).ToArgb() == inset.Inner.ToArgb() &&
                        bitmap.GetPixel(100, inset.Thickness).ToArgb() == Color.Magenta.ToArgb() &&
                        bitmap.GetPixel(100, 100).ToArgb() == Color.Magenta.ToArgb(),
                        "Inset rendering retains exact neutral/accent pixels without growing into the client area " + dpi);
                    graphics.Clear(Color.Magenta); muted.Paint(graphics);
                    check(bitmap.GetPixel(100, 0).ToArgb() == muted.Outer.ToArgb() &&
                        bitmap.GetPixel(100, muted.Thickness - 1).ToArgb() == muted.Outer.ToArgb() &&
                        bitmap.GetPixel(100, muted.Thickness).ToArgb() == Color.Magenta.ToArgb(),
                        "Muted rendering uses one solid color and exactly two logical pixels " + dpi);
                }
            }
            var spanning = TargetWindow(); spanning.Frame = new Rectangle(-300, -800, 1000, 1100);
            spanning.Monitors = new[] { new Rectangle(-1920, -1080, 1920, 1080), new Rectangle(0, 0, 1920, 1080) };
            var visible = FocusIndicatorVisual.Create(spanning, FocusIndicatorMode.MutedFrame, "Peach", false);
            using (Region ring = visible.Ring(0, visible.Thickness)) {
                check(visible.Bounds == spanning.Frame && ring.IsVisible(0, 100) && !ring.IsVisible(999, 100) && ring.IsVisible(999, 1000) &&
                    !ring.IsVisible(301, 801), "Negative/stacked monitor union clips disconnected portions without creating edges at monitor seams");
            }
            var offscreen = TargetWindow(); offscreen.Frame = new Rectangle(-50, 100, 800, 600);
            var clipped = FocusIndicatorVisual.Create(offscreen, FocusIndicatorMode.InsetEdge, "Sky", true);
            using (Region ring = clipped.Ring(0, clipped.Thickness)) {
                check(clipped.Bounds == new Rectangle(0, 100, 750, 600) && !ring.IsVisible(0, 200) && ring.IsVisible(749, 200),
                    "Partly offscreen window preserves real edges instead of outlining the clip rectangle");
            }
            var rounded = TargetWindow(); rounded.Corner = 8;
            var roundedVisual = FocusIndicatorVisual.Create(rounded, FocusIndicatorMode.MutedFrame, "Original", true);
            using (Region ring = roundedVisual.Ring(0, roundedVisual.Thickness))
                check(!ring.IsVisible(0, 0) && ring.IsVisible(20, 0) && !ring.IsVisible(20, 20), "Rounded window keeps corners and interior hollow");
            var same = FocusIndicatorVisual.Create(rounded.Copy(), FocusIndicatorMode.MutedFrame, "Original", true);
            check(roundedVisual.SameAppearance(same), "Unchanged target and palette reuse the existing native region");
            var changes = new Action<FocusIndicatorTarget>[] {
                delegate(FocusIndicatorTarget t) { t.Identity.ProcessStarted++; }, delegate(FocusIndicatorTarget t) { t.Frame.Offset(1, 0); },
                delegate(FocusIndicatorTarget t) { t.Dpi = 144; }, delegate(FocusIndicatorTarget t) { t.Corner = 4; },
                delegate(FocusIndicatorTarget t) { t.Monitors[0] = new Rectangle(0, 0, 1800, 1000); }
            };
            foreach (var change in changes) {
                var changed = rounded.Copy(); change(changed);
                check(!roundedVisual.SameAppearance(FocusIndicatorVisual.Create(changed, FocusIndicatorMode.MutedFrame, "Original", true)),
                    "Target identity/geometry/DPI/rounding/monitor changes invalidate cached frame");
            }
            check(!roundedVisual.SameAppearance(FocusIndicatorVisual.Create(rounded, FocusIndicatorMode.InsetEdge, "Original", true)) &&
                !roundedVisual.SameAppearance(FocusIndicatorVisual.Create(rounded, FocusIndicatorMode.MutedFrame, "Coral", true)) &&
                !roundedVisual.SameAppearance(FocusIndicatorVisual.Create(rounded, FocusIndicatorMode.MutedFrame, "Original", false)),
                "Style, accent and theme changes invalidate cached frame even on the same HWND");
            var maximized = TargetWindow(); maximized.Frame = new Rectangle(0, 0, 1920, 1040); maximized.Corner = 0;
            var maximizedVisual = FocusIndicatorVisual.Create(maximized, FocusIndicatorMode.MutedFrame, "Original", true);
            using (Region ring = maximizedVisual.Ring(0, maximizedVisual.Thickness))
                check(maximizedVisual.Bounds == maximized.Frame && ring.IsVisible(0, 0) && ring.IsVisible(1919, 1039) && !ring.IsVisible(800, 800),
                    "Maximized work-area frame keeps a square thin outline entirely above the taskbar");
            var huge = TargetWindow(); huge.Frame = new Rectangle(0, 0, 16000, 16000); huge.Monitors = new[] { huge.Frame };
            check(FocusIndicatorVisual.Create(huge, FocusIndicatorMode.MutedFrame, "Original", true) == null,
                "Giant spanning window cannot allocate an unbounded layered surface");
            huge.Frame = new Rectangle(Int32.MaxValue - 2, 0, 20, 20);
            check(!FocusIndicatorVisual.Valid(huge.Frame) && FocusIndicatorVisual.Create(huge, FocusIndicatorMode.MutedFrame, "Original", true) == null,
                "Overflowing virtual coordinates are rejected before drawing");
        }
        static void PumpUntil(Func<bool> complete, int maximumMilliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (!complete() && elapsed.ElapsedMilliseconds < maximumMilliseconds) {
                Application.DoEvents(); System.Threading.Thread.Sleep(1);
            }
        }
        static void NativeEvents(Action<bool, string> check)
        {
            var identity = TargetWindow().Identity;
            for (int failure = 1; failure <= 3; failure++) {
                int additions = 0, removals = 0, failAt = failure; bool refused = false;
                try {
                    new FocusIndicatorNativeEvents(identity, delegate { }, delegate(uint first, uint last) {
                        additions++; return additions == failAt ? IntPtr.Zero : new IntPtr(additions);
                    }, delegate { removals++; });
                } catch (Win32Exception) { refused = true; }
                check(refused && additions == failAt && removals == failAt - 1,
                    "Partial native event registration failure unhooks every earlier successful hook " + failure);
            }
            int hooks = 0, unhooks = 0;
            var fakeNative = new FocusIndicatorNativeEvents(identity, delegate { }, delegate { return new IntPtr(++hooks); }, delegate { unhooks++; });
            fakeNative.Dispose(); fakeNative.Dispose();
            check(hooks == 3 && unhooks == 3, "Native event observer holds all three delegates/hooks and unhooks each exactly once");

            IntPtr foreground = FocusedWindowNative.Foreground(); Point pointer = Cursor.Position;
            using (var target = new OwnedWindow { Text = "Neutral geometry event fixture", Opacity = 0, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(70, 70, 430, 290) }) {
                target.Show();
                long clock = 1000; int callbacks = 0;
                var api = new FocusIndicatorApi {
                    Foreground = delegate { return target.Handle; }, Now = delegate { return clock; }, PointerButtonDown = delegate { return false; },
                    Read = delegate(IntPtr window) {
                        // Only the injected eligibility snapshot is adjusted.
                        // The owned fixture's real noactivate style is retained.
                        var value = FocusIndicatorNative.Read(window);
                        if (value != null) value.Identity.ExtendedStyle &= ~0x08000000;
                        return value;
                    },
                    Watch = delegate(FocusedWindowSnapshot value, Action<FocusIndicatorChange> changed) {
                        return new FocusIndicatorNativeEvents(value, delegate(FocusIndicatorChange change) { callbacks++; changed(change); });
                    }
                };
                int beforeStyle = FocusIndicatorSurface.ExtendedStyle(target.Handle);
                FocusIndicatorTarget before = FocusIndicatorNative.Read(target.Handle);
                using (var service = new FocusIndicator(api, delegate { return new FocusIndicatorSurface(true); })) {
                    service.Update(1, "Original", false);
                    check(service.Visible, "Owned native geometry observer starts with a safe invisible noactivate surface");
                    IntPtr oldOverlay = service.Handle;
                    target.Bounds = new Rectangle(120, 90, 470, 310);
                    PumpUntil(delegate { return !service.Visible; }, 500);
                    check(!service.Visible && callbacks > 0 && !Native.IsWindow(oldOverlay),
                        "Real native position/size event removes stale fixture outline before the next Update call");
                    service.Update(1, "Original", false); clock += 119; service.Update(1, "Original", false);
                    check(!service.Visible, "Native event target stays suppressed while final geometry settles");
                    clock++; service.Update(1, "Original", false);
                    FocusIndicatorTarget current = FocusIndicatorNative.Read(target.Handle);
                    Native.Rect overlayBounds; Native.GetWindowRect(service.Handle, out overlayBounds);
                    var expected = FocusIndicatorVisual.Create(current, FocusIndicatorMode.MutedFrame, "Original", api.Dark());
                    check(service.Visible && overlayBounds.Left == expected.Bounds.Left && overlayBounds.Top == expected.Bounds.Top &&
                        overlayBounds.Right == expected.Bounds.Right && overlayBounds.Bottom == expected.Bounds.Bottom,
                        "Settled real target uses the latest physical DWM frame instead of old drag bounds");
                    using (var stale = new FocusIndicatorSurface(true)) {
                        bool refused = false;
                        try { stale.Present(FocusIndicatorVisual.Create(before, FocusIndicatorMode.MutedFrame, "Original", true)); }
                        catch (FocusIndicatorGeometryChangedException) { refused = true; }
                        check(refused && stale.Window == IntPtr.Zero, "Final native presentation refuses stale DWM bounds before showing any surface");
                    }
                    var wrongDpi = current.Copy(); wrongDpi.Dpi = current.Dpi == 96 ? 144 : 96;
                    check(!FocusIndicatorNative.MatchesFrame(wrongDpi), "Final native geometry fence rejects a stale DPI snapshot");
                    target.Bounds = new Rectangle(160, 90, 470, 310);
                    service.Dispose(); int disposedCallbacks = callbacks;
                    PumpUntil(delegate { return false; }, 35);
                    check(callbacks == disposedCallbacks && !service.Visible, "Disposal rejects queued real location events and releases native subscriptions");
                }
                check(beforeStyle == FocusIndicatorSurface.ExtendedStyle(target.Handle) && !FocusIndicatorNative.IsIconic(target.Handle) &&
                    FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                    "Native geometry fixtures preserve foreign foreground/pointer and never alter owned target styles or state");
            }
            check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer, "Geometry event fixture cleanup preserves the actual desktop");
        }
        sealed class OwnedWindow : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }
        [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr window, IntPtr region);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        static Color OwnedPixel(IntPtr fixture, Point point)
        {
            // One verified fixture pixel only; never capture an occluding app or
            // any personal desktop content when the owned target is hidden.
            if (WindowFromPoint(point) != fixture) throw new InvalidOperationException("Neutral compositor fixture is occluded.");
            IntPtr screen = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
            try {
                if (screen == IntPtr.Zero) throw new InvalidOperationException("Neutral compositor screen DC unavailable.");
                memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, 1, 1);
                if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("Neutral compositor pixel buffer unavailable.");
                old = SelectObject(memory, bitmap);
                if (old == IntPtr.Zero || old == new IntPtr(-1)) throw new InvalidOperationException("Neutral compositor pixel selection failed.");
                if (WindowFromPoint(point) != fixture || !BitBlt(memory, 0, 0, 1, 1, screen, point.X, point.Y, 0x40CC0020))
                    throw new InvalidOperationException("Neutral compositor pixel capture unavailable.");
                uint rgb = GetPixel(memory, 0, 0);
                if (rgb == UInt32.MaxValue) throw new InvalidOperationException("Neutral compositor pixel read unavailable.");
                return Color.FromArgb((int)(rgb & 255), (int)((rgb >> 8) & 255), (int)((rgb >> 16) & 255));
            } finally {
                if (old != IntPtr.Zero && old != new IntPtr(-1) && memory != IntPtr.Zero) SelectObject(memory, old);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }
        // BEGIN managed neutral-readiness policy (no native/UI dependency).
        sealed class NeutralReadinessApi
        {
            internal Func<long> Now;
            internal Func<bool> Owned;
            internal Func<int> Flush;
            internal Func<Color> Read;
            internal Action Pump;
        }
        sealed class NeutralReadiness
        {
            internal Color First, Last;
            internal int Samples;
            internal long Milliseconds;
        }
        static Color WaitForNeutral(NeutralReadinessApi api, Color expected, int milliseconds, NeutralReadiness result)
        {
            if (api == null || result == null || milliseconds <= 0) throw new ArgumentException("Invalid neutral readiness fixture.");
            long start = api.Now(), previous = start;
            if (start < 0) throw new InvalidOperationException("Neutral readiness clock is invalid.");
            while (true) {
                if (!api.Owned()) throw new InvalidOperationException("Neutral compositor fixture is occluded.");
                if (api.Flush() != 0) throw new InvalidOperationException("Neutral readiness flush unavailable.");
                long now = api.Now();
                if (now < previous) throw new InvalidOperationException("Neutral readiness clock moved backwards.");
                previous = now; result.Milliseconds = now - start;
                if (result.Milliseconds > milliseconds) throw new TimeoutException("Neutral compositor readiness timed out.");
                if (!api.Owned()) throw new InvalidOperationException("Neutral compositor fixture is occluded.");
                Color pixel = api.Read();
                if (result.Samples++ == 0) result.First = pixel;
                result.Last = pixel;
                now = api.Now();
                if (now < previous) throw new InvalidOperationException("Neutral readiness clock moved backwards.");
                previous = now; result.Milliseconds = now - start;
                if (result.Milliseconds > milliseconds) throw new TimeoutException("Neutral compositor readiness timed out.");
                if (!api.Owned()) throw new InvalidOperationException("Neutral compositor fixture is occluded.");
                if (pixel.ToArgb() == expected.ToArgb()) return pixel;
                if (result.Milliseconds >= milliseconds) throw new TimeoutException("Neutral compositor readiness timed out.");
                api.Pump();
            }
        }
        sealed class NeutralReadinessFixture
        {
            internal long Clock;
            internal bool Owned = true;
            internal int FlushResult, Reads, Flushes, Pumps;
            internal Color Pixel = Color.FromArgb(37, 61, 87);
            internal Action OnFlush, OnRead, OnPump;
            internal NeutralReadinessApi Api()
            {
                return new NeutralReadinessApi {
                    Now = delegate { return Clock; }, Owned = delegate { return Owned; },
                    Flush = delegate { Flushes++; if (OnFlush != null) OnFlush(); return FlushResult; },
                    Read = delegate { Reads++; if (OnRead != null) OnRead(); return Pixel; },
                    Pump = delegate { Pumps++; Clock += 16; if (OnPump != null) OnPump(); }
                };
            }
        }
        static bool NeutralRefused<T>(Action action) where T : Exception
        { try { action(); return false; } catch (T) { return true; } }
        internal static void NeutralReadinessPolicyChecks(Action<bool, string> check)
        {
            Color expected = Color.FromArgb(37, 61, 87);
            var f = new NeutralReadinessFixture(); var result = new NeutralReadiness();
            check(WaitForNeutral(f.Api(), expected, 32, result).ToArgb() == expected.ToArgb() &&
                f.Reads == 1 && f.Flushes == 1 && f.Pumps == 0, "An already painted owned baseline needs no artificial settling delay");
            f = new NeutralReadinessFixture { Pixel = Color.Black }; var stale = f;
            f.OnPump = delegate { stale.Pixel = expected; }; result = new NeutralReadiness();
            check(WaitForNeutral(f.Api(), expected, 32, result).ToArgb() == expected.ToArgb() &&
                result.First.ToArgb() == Color.Black.ToArgb() && result.Last.ToArgb() == expected.ToArgb() &&
                f.Reads == 2 && f.Pumps == 1, "A stale plain-window pixel cannot pass before a fresh exact owned render");
            f = new NeutralReadinessFixture { Pixel = Color.Black }; var timeout = f;
            check(NeutralRefused<TimeoutException>(delegate { WaitForNeutral(timeout.Api(), expected, 32, new NeutralReadiness()); }) &&
                f.Clock == 32, "A never-neutral fixture stops at its bounded deadline without a tolerance or skip");
            f = new NeutralReadinessFixture(); var expired = f; f.OnRead = delegate { expired.Clock = 33; };
            check(NeutralRefused<TimeoutException>(delegate { WaitForNeutral(expired.Api(), expected, 32, new NeutralReadiness()); }),
                "Exact color received after the deadline cannot authorize early or late success");
            f = new NeutralReadinessFixture { Owned = false }; var hidden = f;
            check(NeutralRefused<InvalidOperationException>(delegate { WaitForNeutral(hidden.Api(), expected, 32, new NeutralReadiness()); }) &&
                f.Reads == 0 && f.Flushes == 0, "An occluded fixture is refused before flushing or reading any pixel");
            f = new NeutralReadinessFixture(); var lostFlush = f; f.OnFlush = delegate { lostFlush.Owned = false; };
            check(NeutralRefused<InvalidOperationException>(delegate { WaitForNeutral(lostFlush.Api(), expected, 32, new NeutralReadiness()); }) &&
                f.Reads == 0, "Ownership loss during flushing prevents pixel capture");
            f = new NeutralReadinessFixture(); var lostRead = f; f.OnRead = delegate { lostRead.Owned = false; };
            check(NeutralRefused<InvalidOperationException>(delegate { WaitForNeutral(lostRead.Api(), expected, 32, new NeutralReadiness()); }),
                "Ownership loss during capture rejects even an exact neutral value");
            f = new NeutralReadinessFixture { FlushResult = 1 }; var failedFlush = f;
            check(NeutralRefused<InvalidOperationException>(delegate { WaitForNeutral(failedFlush.Api(), expected, 32, new NeutralReadiness()); }) &&
                f.Reads == 0, "A failed flush cannot be hidden by an already neutral sample");
            f = new NeutralReadinessFixture(); var readFailure = f;
            f.OnRead = delegate { throw new InvalidOperationException("Owned synthetic capture failure"); };
            check(NeutralRefused<InvalidOperationException>(delegate { WaitForNeutral(readFailure.Api(), expected, 32, new NeutralReadiness()); }),
                "Pixel capture errors remain hard failures");
            f = new NeutralReadinessFixture(); var backwards = f; f.OnFlush = delegate { backwards.Clock = -1; };
            check(NeutralRefused<InvalidOperationException>(delegate { WaitForNeutral(backwards.Api(), expected, 32, new NeutralReadiness()); }) &&
                f.Reads == 0, "Backward time cannot extend readiness or permit a pixel read");
            f = new NeutralReadinessFixture { Clock = -1 }; var invalid = f;
            check(NeutralRefused<InvalidOperationException>(delegate { WaitForNeutral(invalid.Api(), expected, 32, new NeutralReadiness()); }),
                "An invalid initial monotonic clock fails closed");
            f = new NeutralReadinessFixture(); var overFlush = f; f.OnFlush = delegate { overFlush.Clock = 33; };
            check(NeutralRefused<TimeoutException>(delegate { WaitForNeutral(overFlush.Api(), expected, 32, new NeutralReadiness()); }) &&
                f.Reads == 0, "A flush that consumes the deadline cannot admit a late pixel");
            f = new NeutralReadinessFixture { Pixel = Color.Black }; var lostPump = f; f.OnPump = delegate { lostPump.Owned = false; };
            check(NeutralRefused<InvalidOperationException>(delegate { WaitForNeutral(lostPump.Api(), expected, 32, new NeutralReadiness()); }) &&
                f.Reads == 1, "Occlusion while settling stops before reading another frame");
            f = new NeutralReadinessFixture(); var badBudget = f;
            check(NeutralRefused<ArgumentException>(delegate { WaitForNeutral(badBudget.Api(), expected, 0, new NeutralReadiness()); }),
                "A missing readiness deadline is rejected");
        }
        // END managed neutral-readiness policy.
        static Color ReadyOwnedBaseline(IntPtr fixture, Point center, Color expected)
        {
            // Retain the exact old immediate sample for diagnosis, then prepare
            // only the plain owned window. No surface exists at this point.
            Color original = OwnedPixel(fixture, center);
            var clock = Stopwatch.StartNew(); var result = new NeutralReadiness();
            try {
                return WaitForNeutral(new NeutralReadinessApi {
                    Now = delegate { return clock.ElapsedMilliseconds; },
                    Owned = delegate { return WindowFromPoint(center) == fixture; },
                    Flush = DwmFlush, Read = delegate { return OwnedPixel(fixture, center); },
                    Pump = delegate { Application.DoEvents(); System.Threading.Thread.Sleep(2); }
                }, expected, 500, result);
            } finally {
                Console.WriteLine("Owned neutral baseline originalRgb=" + original.R + "," + original.G + "," + original.B +
                    " expectedRgb=" + expected.R + "," + expected.G + "," + expected.B +
                    " readyRgb=" + (result.Samples == 0 ? "unread" : result.Last.R + "," + result.Last.G + "," + result.Last.B) +
                    " samples=" + result.Samples + " elapsedMs=" + result.Milliseconds);
            }
        }
        static void NativeAlphaCompositor(Action<bool, string> check)
        {
            IntPtr foreground = FocusedWindowNative.Foreground(); Point pointer = Cursor.Position;
            Color neutral = Color.FromArgb(37, 61, 87);
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            var bounds = new Rectangle(work.Left + Math.Min(80, Math.Max(0, work.Width - 360)),
                work.Top + Math.Min(80, Math.Max(0, work.Height - 260)), 320, 220);
            // Native WinForms/compositor caches are initialized once per thread.
            // Measure repeated lifecycle resources after a fully disposed owned
            // warm-up, while still testing the actual cold first upload below.
            using (var warm = new OwnedWindow { Text = "Neutral alpha compositor warm-up", ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None, BackColor = neutral, TopMost = true,
                    StartPosition = FormStartPosition.Manual, Bounds = bounds }) {
                warm.Show(); warm.Update(); Application.DoEvents();
                if (DwmFlush() != 0) throw new InvalidOperationException("Neutral warm-up flush unavailable.");
                var warmFrame = FocusIndicatorNative.Read(warm.Handle);
                if (warmFrame == null) throw new InvalidOperationException("Neutral warm-up frame unavailable.");
                Point warmCenter = new Point(warm.Left + warm.Width / 2, warm.Top + warm.Height / 2);
                Color warmBaseline = ReadyOwnedBaseline(warm.Handle, warmCenter, neutral);
                check(warmBaseline.ToArgb() == neutral.ToArgb(), "Cold compositor warm-up begins with exact owned neutral pixels");
                IntPtr warmOverlay;
                using (var surface = new FocusIndicatorSurface(animationsAllowed: delegate { return false; })) {
                    var first = FocusIndicatorVisual.Create(warmFrame, FocusIndicatorMode.MutedFrame, "Original", true);
                    surface.Present(first); warmOverlay = surface.Window;
                    if (DwmFlush() != 0) throw new InvalidOperationException("Neutral cold-upload flush unavailable.");
                    check(Native.IsWindowVisible(warmOverlay) && surface.Uploaded && surface.Pixels != null &&
                        surface.Pixels.GetPixel(first.Bounds.Width / 2, first.Bounds.Height / 2).A == 0 &&
                        OwnedPixel(warm.Handle, warmCenter).ToArgb() == warmBaseline.ToArgb() &&
                        OwnedPixel(warm.Handle, new Point(first.Bounds.Left + 50, first.Bounds.Top + 1)).ToArgb() == first.Outer.ToArgb(),
                        "The real cold first alpha upload preserves transparent client pixels and displays its restrained opaque frame before any paint pass");
                }
                if (DwmFlush() != 0) throw new InvalidOperationException("Neutral cold-upload teardown flush unavailable.");
                check(!Native.IsWindow(warmOverlay) && OwnedPixel(warm.Handle, warmCenter).ToArgb() == warmBaseline.ToArgb(),
                    "Cold compositor warm-up disposes its actual overlay and preserves neutral client content");
            }
            Application.DoEvents();
            if (DwmFlush() != 0) throw new InvalidOperationException("Neutral warm-up cleanup flush unavailable.");
            check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                "Owned compositor warm-up preserves the original foreground/pointer before measuring resources");
            uint gdi = GetGuiResources(GetCurrentProcess(), 0), user = GetGuiResources(GetCurrentProcess(), 1);
            using (var target = new OwnedWindow { Text = "Neutral alpha compositor fixture", ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None, BackColor = neutral, TopMost = true,
                    StartPosition = FormStartPosition.Manual, Bounds = bounds }) {
                target.Show(); target.Update(); Application.DoEvents();
                if (DwmFlush() != 0) throw new InvalidOperationException("Neutral compositor flush unavailable.");
                var frame = FocusIndicatorNative.Read(target.Handle);
                if (frame == null) throw new InvalidOperationException("Neutral compositor frame unavailable.");
                Point center = new Point(target.Left + target.Width / 2, target.Top + target.Height / 2);
                Color baseline = ReadyOwnedBaseline(target.Handle, center, neutral);
                check(baseline.ToArgb() == neutral.ToArgb(), "Normal-opacity compositor fixture has an exact visible neutral baseline before the indicator");
                if (baseline.ToArgb() != neutral.ToArgb()) throw new InvalidOperationException("Neutral compositor fixture baseline mismatched.");
                int style = FocusIndicatorSurface.ExtendedStyle(target.Handle);
                Native.Rect before; Native.GetWindowRect(target.Handle, out before);
                for (int cycle = 0; cycle < 6; cycle++) {
                    IntPtr overlay;
                    using (var surface = new FocusIndicatorSurface(animationsAllowed: delegate { return false; })) {
                        var first = FocusIndicatorVisual.Create(frame, FocusIndicatorMode.MutedFrame,
                            FocusAppearance.Presets[cycle % FocusAppearance.Presets.Length], true);
                        surface.Present(first); overlay = surface.Window;
                        // Flush and sample immediately after Present, before any
                        // WM_PAINT message pump could repair a blank backing.
                        if (DwmFlush() != 0) throw new InvalidOperationException("Neutral first-show flush unavailable.");
                        check(Native.IsWindowVisible(overlay) && surface.Uploaded && surface.Pixels != null &&
                            surface.Pixels.GetPixel(first.Bounds.Width / 2, first.Bounds.Height / 2).A == 0 &&
                            OwnedPixel(target.Handle, center).ToArgb() == baseline.ToArgb(),
                            "First production-alpha presentation preserves the visible fixture interior without a paint pass " + cycle);
                        Point edge = new Point(first.Bounds.Left + 50, first.Bounds.Top + 1);
                        check(OwnedPixel(target.Handle, edge).ToArgb() == first.Outer.ToArgb(),
                            "Compositor displays the actual opaque restrained frame while its interior stays transparent " + cycle);
                        var recolored = FocusIndicatorVisual.Create(frame, FocusIndicatorMode.InsetEdge,
                            FocusAppearance.Presets[(cycle + 1) % FocusAppearance.Presets.Length], false);
                        surface.Present(recolored);
                        if (DwmFlush() != 0) throw new InvalidOperationException("Neutral recolor flush unavailable.");
                        check(OwnedPixel(target.Handle, center).ToArgb() == baseline.ToArgb() &&
                            surface.Pixels.GetPixel(recolored.Bounds.Width / 2, recolored.Bounds.Height / 2).A == 0,
                            "Visible production style/palette replacement never fills the fixture client rectangle " + cycle);
                        surface.Dispose(); surface.Dispose();
                        if (DwmFlush() != 0) throw new InvalidOperationException("Neutral teardown flush unavailable.");
                        check(!Native.IsWindow(overlay) && OwnedPixel(target.Handle, center).ToArgb() == baseline.ToArgb(),
                            "Hide/destroy and repeated disposal retain neutral client content and leave no indicator HWND " + cycle);
                    }
                }
                Native.Rect after; Native.GetWindowRect(target.Handle, out after);
                check(style == FocusIndicatorSurface.ExtendedStyle(target.Handle) && AreaPolicy.Equal(before, after) &&
                    (style & 0x08000000) != 0 && FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                    "Normal-opacity tests preserve native noactivate style, fixture bounds and actual foreground/pointer");
            }
            uint immediateGdi = GetGuiResources(GetCurrentProcess(), 0), immediateUser = GetGuiResources(GetCurrentProcess(), 1);
            bool foregroundUnchanged = FocusedWindowNative.Foreground() == foreground;
            bool pointerUnchanged = Cursor.Position == pointer;
            // Match the warm-up measurement boundary after the owned Form closes.
            Application.DoEvents();
            if (DwmFlush() != 0) throw new InvalidOperationException("Neutral teardown cleanup flush unavailable.");
            uint finalGdi = GetGuiResources(GetCurrentProcess(), 0), finalUser = GetGuiResources(GetCurrentProcess(), 1);
            Console.WriteLine("Owned compositor cleanup GDI=" + gdi + "/" + immediateGdi + "/" + finalGdi +
                ", USER=" + user + "/" + immediateUser + "/" + finalUser + " (before/immediate/drained).");
            check(finalGdi <= gdi + 4, "Repeated real alpha presentation and teardown balance GDI resources");
            check(finalUser <= user + 2, "Repeated real alpha presentation and teardown balance USER resources");
            check(foregroundUnchanged && FocusedWindowNative.Foreground() == foreground, "Alpha compositor teardown preserves the actual foreground window");
            check(pointerUnchanged && Cursor.Position == pointer, "Alpha compositor teardown preserves the actual pointer position");
        }
        static void NativeSurface(Action<bool, string> check)
        {
            IntPtr foreground = FocusedWindowNative.Foreground(); Point pointer = Cursor.Position;
            using (var target = new OwnedWindow { Text = "Neutral focus indicator fixture", Opacity = 0, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(50, 50, 420, 280) }) {
                target.Show();
                Native.Rect before; Native.GetWindowRect(target.Handle, out before);
                int targetStyle = FocusIndicatorSurface.ExtendedStyle(target.Handle);
                var snapshot = FocusedWindowNative.Read(target.Handle);
                var native = FocusIndicatorNative.Read(target.Handle);
                check(native != null && native.Identity.Window == target.Handle && native.Frame.Width > 0 && native.Dpi >= 96 && !native.Fullscreen,
                    "Native indicator reads visible DWM frame, identity, DPI and menu state of owned fixture");
                if (native == null) throw new InvalidOperationException("Owned indicator native frame unavailable");
                IntPtr overlay;
                using (var surface = new FocusIndicatorSurface(true)) {
                    var visual = FocusIndicatorVisual.Create(native, FocusIndicatorMode.InsetEdge, "Original", true);
                    if (visual == null) throw new InvalidOperationException("Owned indicator layout unavailable");
                    surface.Present(visual); overlay = surface.Window;
                    int exstyle = FocusIndicatorSurface.ExtendedStyle(overlay);
                    check(Native.IsWindowVisible(overlay) && (exstyle & (0x08000000 | 0x20 | 0x80 | 0x80000)) == (0x08000000 | 0x20 | 0x80 | 0x80000) && (exstyle & 8) == 0,
                        "Owned native surface is initialized layered, click-through, noactivate, toolwindow and ordinary z-order");
                    check(GetWindow(target.Handle, 3) == overlay, "Owned outline sits immediately above its target without using unconditional topmost");
                    IntPtr region = CreateRectRgn(0, 0, 0, 0);
                    try {
                        check(GetWindowRgn(overlay, region) != 0 && !PtInRegion(region, visual.Bounds.Width / 2, visual.Bounds.Height / 2),
                            "Native surface has a hollow window region rather than covering the app client area");
                    } finally { DeleteObject(region); }
                    for (int i = 0; i < 40; i++) surface.Present(FocusIndicatorVisual.Create(native,
                        i % 2 == 0 ? FocusIndicatorMode.MutedFrame : FocusIndicatorMode.InsetEdge, i % 2 == 0 ? "Original" : "Coral", i % 3 == 0));
                    var steady = FocusIndicatorVisual.Create(native, FocusIndicatorMode.MutedFrame, "Mint", true);
                    surface.Present(steady); Region retained = surface.Region;
                    uint gdi = GetGuiResources(GetCurrentProcess(), 0), handles = GetGuiResources(GetCurrentProcess(), 1);
                    for (int i = 0; i < 100; i++) surface.Present(FocusIndicatorVisual.Create(native, FocusIndicatorMode.MutedFrame, "Mint", true));
                    check(Object.ReferenceEquals(retained, surface.Region), "Idle native timer updates retain the exact owned hollow region");
                    for (int i = 0; i < 100; i++) surface.Present(FocusIndicatorVisual.Create(native,
                        i % 2 == 0 ? FocusIndicatorMode.MutedFrame : FocusIndicatorMode.InsetEdge,
                        FocusAppearance.Presets[i % FocusAppearance.Presets.Length], i % 3 == 0));
                    check(GetGuiResources(GetCurrentProcess(), 0) <= gdi + 4 && GetGuiResources(GetCurrentProcess(), 1) <= handles + 2,
                        "Repeated owned palette/style updates keep GDI and USER handle counts bounded");
                    Native.Rect after; Native.GetWindowRect(target.Handle, out after);
                    check(AreaPolicy.Equal(before, after) && FocusedWindowPolicy.SameWindow(snapshot, FocusedWindowNative.Read(target.Handle)) &&
                        targetStyle == FocusIndicatorSurface.ExtendedStyle(target.Handle) && !FocusIndicatorNative.IsIconic(target.Handle),
                        "Repeated native palette/style updates do not mutate target bounds, identity, styles or state");
                    check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                        "Native indicator fixture never activates a target or moves the user's pointer");
                }
                check(!Native.IsWindow(overlay), "Native indicator disposal destroys its owned HWND");
                using (var guarded = new FocusIndicator(new FocusIndicatorApi { Foreground = delegate { return target.Handle; },
                    Read = FocusIndicatorNative.Read, DesktopAllowed = delegate { return true; } }, delegate { throw new Exception("Noactivate fixture must be ignored"); })) {
                    guarded.Update(1, "Original", false); check(!guarded.Visible, "Production eligibility excludes actual noactivate native fixture");
                }
            }
            check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer, "Owned indicator test cleanup preserves desktop foreground and pointer");
        }
    }
}
