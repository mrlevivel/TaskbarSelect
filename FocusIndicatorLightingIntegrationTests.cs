using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class FocusIndicatorLightingIntegrationTests
    {
        sealed class OwnedTarget : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }
        internal static void Run(Action<bool, string> check)
        {
            Timeline(check); Staging(check);
            IntPtr foreground = FocusedWindowNative.Foreground(); Point pointer = Cursor.Position;
            using (var target = new OwnedTarget { Text = "Neutral premium focus fixture", Opacity = 0, ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(70, 70, 320, 220) }) {
                target.Show(); var captured = FocusIndicatorNative.Read(target.Handle);
                if (captured == null) throw new InvalidOperationException("Owned premium focus target unavailable.");
                ConstructionLatency(target, check); RevealCadence(target, check); Publication(target, check); Failures(target, check); Reentrancy(target, check);
                StaleUploadFailures(target, check);
                check(FocusedWindowPolicy.SameWindow(captured.Identity, FocusedWindowNative.Read(target.Handle)) &&
                    FocusIndicatorNative.MatchesFrame(captured) && FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                    "Premium cue fixtures preserve target identity/geometry, actual focus and pointer");
            }
            check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                "Premium cue fixture cleanup preserves the user's desktop focus and pointer");
        }
        static void Timeline(Action<bool, string> check)
        {
            var flow = new FocusIndicatorSurface.FlowClock();
            check(flow.Value(200, true) == 0 && !flow.Active && !flow.Visible, "A hidden energy clock ignores late samples");
            flow.Show(0); flow.Value(0, true);
            check(flow.Value(1500, true) == .25 && flow.Active, "Energy advances clockwise through a calm six-second lap");
            check(flow.Value(1200, true) == .25 && flow.Value(1500, true) == .25, "Backward/repeated clock samples cannot rewind or advance energy");
            flow.Show(1500);
            check(flow.Value(4500, true) == .75, "Repaint or palette replacement does not restart the shown energy timeline");
            check(flow.Value(6000, true) == 0 && flow.Value(13500, true) == .25, "Energy wraps smoothly after one or many complete laps");
            double retained = flow.Value(14000, false);
            check(retained == .25 && !flow.Active && flow.Value(18000, false) == retained && flow.Value(20000, true) == retained,
                "Static lighting pauses the retained energy phase without counting paused wall time");
            check(flow.Value(21500, true) == .5, "Re-entering energy continues from its retained phase");
            flow.Reset(22000);
            check(flow.Phase == 0 && !flow.Active && flow.Value(23000, true) == 0,
                "Reduced motion resets to a complete static phase-zero frame and resumes without a jump");
            flow.Hide(); flow.Value(30000, true);
            check(!flow.Visible && !flow.Active && flow.Phase == 0, "Hide cancels and resets the energy phase; late ticks cannot revive it");
            flow.Show(Int64.MinValue + 20); flow.Value(Int64.MinValue + 20, true);
            ulong huge = unchecked((ulong)Int64.MaxValue - (ulong)(Int64.MinValue + 20));
            check(flow.Value(Int64.MaxValue, true) == (huge % 6000) / 6000.0,
                "Large signed clock ranges are reduced as integer elapsed time before floating-point phase conversion");
            flow.Hide(); flow.Show(Int64.MaxValue - 2000); flow.Value(Int64.MaxValue - 2000, true);
            check(flow.Value(Int64.MaxValue - 500, true) == .25, "Near-maximum clocks retain exact short energy intervals");
            check(FocusIndicatorSurface.FlowInterval(new Size(640, 480), 1) == 33 &&
                FocusIndicatorSurface.FlowInterval(new Size(1920, 1080), 1) == 50 &&
                FocusIndicatorSurface.FlowInterval(new Size(3840, 2160), 1) == 67 &&
                FocusIndicatorSurface.FlowInterval(new Size(640, 480), 80) == 160 &&
                FocusIndicatorSurface.FlowInterval(new Size(640, 480), 1000) == 250,
                "Continuous flow has conservative size-based frame budgets and backs off when rendering is expensive");
            check(FocusIndicatorSurface.FlowInterval(new Size(640, 480), Double.NaN) == 33 &&
                FocusIndicatorSurface.FlowInterval(new Size(640, 480), Double.PositiveInfinity) == 33 &&
                FocusIndicatorSurface.FlowInterval(new Size(640, 480), -1) == 33,
                "Invalid optional timing telemetry cannot overflow or disable the flow timer");
            check(!FocusIndicatorSurface.FrameDue(80, 0, 250) && !FocusIndicatorSurface.FrameDue(249, 0, 250) &&
                FocusIndicatorSurface.FrameDue(250, 0, 250) && !FocusIndicatorSurface.FrameDue(50, 100, 33) &&
                FocusIndicatorSurface.FrameDue(Int64.MaxValue, Int64.MinValue, 250),
                "Verifier and timer share a bounded phase-only upload deadline; backward/large clocks remain safe");
        }
        static FocusIndicatorTarget Synthetic()
        {
            return new FocusIndicatorTarget { Identity = new FocusedWindowSnapshot { Window = new IntPtr(42), Root = new IntPtr(42), Process = 7,
                    Thread = 8, ProcessStarted = 99, Visible = true, ClassName = "Neutral lighting staging fixture" },
                Frame = new Rectangle(30, 30, 300, 180), Monitors = new[] { new Rectangle(0, 0, 600, 400) }, Dpi = 96, Corner = 8 };
        }
        static bool Matches(FocusIndicatorSurface.LayeredUpload staging, Bitmap image)
        {
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try {
                var row = new int[image.Width];
                for (int y = 0; y < image.Height; y++) {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x++) if (row[x] != staging.Pixel(x, y)) return false;
                }
                return true;
            } finally { image.UnlockBits(data); }
        }
        static void Staging(Action<bool, string> check)
        {
            using (var staging = new FocusIndicatorSurface.LayeredUpload()) {
                foreach (int dpi in new[] { 96, 120, 192 }) {
                    var target = Synthetic(); target.Dpi = dpi; target.Corner = (8 * dpi + 48) / 96;
                    if (dpi == 120) target.Monitors = new[] { new Rectangle(0, 0, 170, 400), new Rectangle(190, 0, 410, 400) };
                    var visual = FocusIndicatorVisual.Create(target, FocusIndicatorMode.MutedFrame, "Coral", true, false, 3);
                    using (var renderer = new FocusIndicatorLightingRenderer(visual)) {
                        int before = staging.Allocations;
                        foreach (double progress in new[] { 0.0, .4, 1.0 }) foreach (double phase in new[] { 0.0, .25, .75 }) {
                            Bitmap image = renderer.Render(progress, phase);
                            check(staging.Stage(image, visual) && Matches(staging, image),
                                "Perimeter-only native staging exactly matches every premultiplied pixel at DPI " + dpi + ", reveal " + progress + ", phase " + phase);
                        }
                        check(staging.Ready && staging.Allocations == before + 1,
                            "Premium motion reuses one native DIB across all phases/reveal frames at DPI " + dpi);
                        var recolored = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, "Mint", false, false, 2);
                        using (var replacement = new FocusIndicatorLightingRenderer(recolored)) {
                            Bitmap image = replacement.Render(1, 0);
                            check(staging.Stage(image, recolored) && staging.Allocations == before + 1 && Matches(staging, image),
                                "Palette/style replacement with unchanged geometry reuses staging without retaining old color pixels at DPI " + dpi);
                        }
                    }
                }
                staging.Dispose();
                check(!staging.Ready, "Persistent DIB/DC resources are released on disposal");
                bool refused = false;
                try { staging.Stage(null, null); } catch (ObjectDisposedException) { refused = true; }
                check(refused, "Disposed native staging cannot allocate again on a stale call");
            }
        }
        static FocusIndicatorVisual Visual(OwnedTarget target, int lighting = 3, string accent = "Coral", FocusIndicatorMode mode = FocusIndicatorMode.MutedFrame)
        {
            var native = FocusIndicatorNative.Read(target.Handle);
            if (native == null) throw new InvalidOperationException("Owned premium target unavailable.");
            var visual = FocusIndicatorVisual.Create(native, mode, accent, true, false, lighting);
            if (visual == null) throw new InvalidOperationException("Owned premium visual unavailable.");
            return visual;
        }
        static bool Released(Bitmap image)
        { try { image.GetPixel(0, 0); return false; } catch (ArgumentException) { return true; } }
        static bool Transparent(Bitmap image)
        {
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try {
                var row = new int[image.Width];
                for (int y = 0; y < image.Height; y++) {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x++) if ((row[x] & unchecked((int)0xff000000)) != 0) return false;
                }
                return true;
            } finally { image.UnlockBits(data); }
        }
        static bool Clean(FocusIndicatorSurface surface, IntPtr window)
        {
            return !Native.IsWindow(window) && surface.Window == IntPtr.Zero && surface.Pixels == null && surface.Region == null &&
                surface.LightingCache == null && !surface.HasStaging && !surface.Uploaded && !surface.EnergyRunning && !surface.AnimationRunning;
        }
        static void ConstructionLatency(OwnedTarget target, Action<bool, string> check)
        {
            int clockReads = 0; long clock = 170; IntPtr window = IntPtr.Zero;
            using (var surface = new FocusIndicatorSurface(true, delegate(IntPtr value, Bitmap image, Point location) {
                window = value; return ShelfPaint.PresentLayered(value, image, location);
            }, delegate { return true; }, delegate { return clockReads++ == 0 ? 0 : clock; }, delegate { return true; })) {
                surface.Present(Visual(target));
                check(clockReads >= 2 && surface.RevealProgress == 0 && surface.RevealStrength == 1 && surface.EnergyPhase == 0 &&
                    surface.AnimationRunning && surface.AnimationInterval == 16,
                    "Initial premium cache construction completes before the visible reveal/energy clock starts");
                clock = 200; surface.AdvanceAnimation();
                check(surface.RevealProgress > 0 && surface.RevealProgress < 1 && surface.EnergyPhase == .005,
                    "A simulated 170ms cache build preserves early reveal frames instead of skipping to completion");
                clock = 590; surface.AdvanceAnimation();
                check(surface.RevealProgress == 1 && surface.RevealStrength == 1 && surface.EnergyPhase == .07 && surface.EnergyRunning && surface.AnimationInterval == 16,
                    "The full 420ms reveal and its settling tail follow the post-construction timestamp");
                clock = 680; surface.AdvanceAnimation();
                check(surface.RevealProgress == 1 && surface.RevealStrength == .5 && surface.EnergyPhase == .085 && surface.EnergyRunning && surface.AnimationInterval == 16,
                    "Cache construction does not consume the halfway settling frame of the designed reveal");
                clock = 770; surface.AdvanceAnimation();
                check(surface.RevealProgress == 1 && surface.RevealStrength == 0 && surface.EnergyPhase == .1 && surface.EnergyRunning && surface.AnimationInterval >= 33,
                    "After the post-construction 600ms timeline the timer returns to the conservative energy cadence");
                surface.Dispose();
                check(Clean(surface, window), "Delayed-construction fixture releases every owned surface resource");
            }
        }
        static void RevealCadence(OwnedTarget target, Action<bool, string> check)
        {
            long clock = 0; int uploads = 0; IntPtr window = IntPtr.Zero;
            FieldInfo cost = typeof(FocusIndicatorSurface).GetField("lastFrameMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var surface = new FocusIndicatorSurface(true, delegate(IntPtr value, Bitmap image, Point location) {
                window = value; uploads++;
                check(Transparent(image),
                    "The cadence fixture keeps its owned native surface transparent on upload " + uploads);
                return ShelfPaint.PresentLayered(value, image, location);
            }, delegate { return true; }, delegate { return clock; }, delegate { return true; })) {
                var energy = Visual(target); surface.Present(energy);
                // Inject expensive optional telemetry without allocating a huge
                // desktop fixture or blocking this test on real rendering cost.
                // A same-time Present changes no pixels and only re-schedules.
                cost.SetValue(surface, 1000.0); surface.Present(energy);
                check(uploads == 1 && surface.AnimationRunning && surface.EnergyRunning && surface.AnimationInterval == 16 &&
                    surface.RevealProgress == 0 && surface.RevealStrength == 1,
                    "Reveal cadence stays 16ms even when idle energy telemetry would request a 250ms interval");
                clock = 16; surface.AdvanceAnimation();
                check(uploads == 2 && surface.RevealProgress > 0 && surface.RevealProgress < 1 && surface.RevealStrength == 1 && surface.AnimationInterval == 16,
                    "The first dedicated reveal tick bypasses the slower phase-only upload budget");
                clock = 420; surface.AdvanceAnimation(); cost.SetValue(surface, 1000.0); surface.Present(energy);
                check(uploads == 3 && surface.RevealProgress == 1 && surface.RevealStrength == 1 && surface.AnimationInterval == 16,
                    "Complete perimeter progress keeps dedicated reveal cadence throughout its settling tail");
                clock = 436; surface.AdvanceAnimation();
                double firstStrength = surface.RevealStrength;
                check(uploads == 4 && surface.RevealProgress == 1 && firstStrength > 0 && firstStrength < 1 && surface.AnimationInterval == 16 &&
                    !FocusIndicatorSurface.FrameDue(436, 420, 250),
                    "A strength-only settling tick publishes after 16ms rather than waiting for a phase-only budget");
                cost.SetValue(surface, 1000.0); clock = 452; surface.Present(energy);
                check(uploads == 5 && surface.RevealProgress == 1 && surface.RevealStrength < firstStrength && surface.AnimationInterval == 16,
                    "The normal verifier also commits changed settling strength without restarting or throttling the reveal");
                clock = 600; surface.AdvanceAnimation(); cost.SetValue(surface, 1000.0); surface.Present(energy);
                check(uploads == 6 && surface.RevealProgress == 1 && surface.RevealStrength == 0 && surface.EnergyRunning &&
                    surface.AnimationRunning && surface.AnimationInterval == 250,
                    "The completed reveal returns to the exact measured idle energy budget without replaying its highlight");
                clock = 616; surface.AdvanceAnimation(); clock = 800; surface.Present(energy);
                check(uploads == 6 && surface.AnimationInterval == 250,
                    "Phase-only updates remain rate limited after the reveal and settling work end");
                clock = 850; surface.AdvanceAnimation();
                check(uploads == 7 && surface.RevealProgress == 1 && surface.RevealStrength == 0 && surface.EnergyRunning,
                    "The next phase-only update publishes at the retained 250ms deadline");
                surface.Dispose(); surface.AdvanceAnimation();
                check(Clean(surface, window) && uploads == 7,
                    "Dedicated-cadence cleanup releases all owned resources and rejects queued reveal ticks");
            }
        }
        static void Publication(OwnedTarget target, Action<bool, string> check)
        {
            long clock = 0; bool allowed = true; int uploads = 0; bool hollow = true, firstHidden = false; IntPtr window = IntPtr.Zero;
            using (var surface = new FocusIndicatorSurface(true, delegate(IntPtr value, Bitmap image, Point location) {
                window = value; uploads++;
                if (uploads == 1) firstHidden = !Native.IsWindowVisible(value);
                hollow &= image.GetPixel(image.Width / 2, image.Height / 2).ToArgb() == 0;
                return ShelfPaint.PresentLayered(value, image, location);
            }, delegate { return allowed; }, delegate { return clock; }, delegate { return true; })) {
                var energy = Visual(target); surface.Present(energy);
                Bitmap bitmap = surface.Pixels; Region region = surface.Region; var cache = surface.LightingCache;
                check(firstHidden && hollow && surface.EnergyPhase == 0 && surface.EnergyRunning && surface.AnimationRunning && cache != null,
                    "Energy starts on its hidden owned transparent surface before show and keeps the client hollow");
                clock = FocusIndicatorMotion.TotalMilliseconds; surface.AdvanceAnimation(); clock = 1500; surface.AdvanceAnimation();
                check(surface.RevealProgress == 1 && surface.RevealStrength == 0 && surface.EnergyPhase == .25 && surface.EnergyRunning && surface.AnimationRunning &&
                    Object.ReferenceEquals(bitmap, surface.Pixels) && Object.ReferenceEquals(cache, surface.LightingCache) && Object.ReferenceEquals(region, surface.Region),
                    "Energy continues after the quick reveal and reuses the same bitmap, geometry cache and native shape");
                surface.Present(Visual(target, 3, "Mint", FocusIndicatorMode.InsetEdge));
                check(surface.EnergyPhase == .25 && surface.RevealProgress == 1 && surface.RevealStrength == 0 && surface.EnergyRunning && Released(bitmap) &&
                    !Object.ReferenceEquals(cache, surface.LightingCache),
                    "Changing style/palette replaces and frees the old paint cache while retaining reveal and energy phase");
                clock = 3000; surface.AdvanceAnimation();
                double retained = surface.EnergyPhase;
                var illuminated = Visual(target, 2, "Mint", FocusIndicatorMode.InsetEdge); surface.Present(illuminated);
                bitmap = surface.Pixels; cache = surface.LightingCache; int settledUploads = uploads;
                clock = 10000; surface.Present(illuminated); surface.AdvanceAnimation();
                check(retained == .5 && !surface.EnergyRunning && !surface.AnimationRunning && uploads == settledUploads &&
                    Object.ReferenceEquals(bitmap, surface.Pixels) && Object.ReferenceEquals(cache, surface.LightingCache),
                    "Settled illuminated lighting has no live timer, repeated upload or cache allocation");
                energy = Visual(target, 3, "Mint", FocusIndicatorMode.InsetEdge); surface.Present(energy);
                check(surface.EnergyPhase == retained && surface.EnergyRunning && surface.RevealProgress == 1,
                    "Returning to energy retains its shown phase without counting time spent in static lighting");
                allowed = false; clock = 11000; surface.AdvanceAnimation();
                check(surface.EnergyPhase == 0 && surface.RevealProgress == 1 && surface.RevealStrength == 0 && !surface.EnergyRunning && !surface.AnimationRunning && hollow,
                    "Reduced motion commits phase-zero illuminated energy with no continuing timer or body fill");
                allowed = true; clock = 12000; surface.Present(energy); clock = 13500; surface.AdvanceAnimation();
                check(surface.EnergyPhase == .25 && surface.RevealProgress == 1 && surface.EnergyRunning && hollow,
                    "Re-enabling client animations resumes a calm flow without replaying the reveal");
                bitmap = surface.Pixels; surface.Dispose(); surface.AdvanceAnimation();
                check(Clean(surface, window) && Released(bitmap), "Energy teardown destroys its HWND before freeing cached image/shape; late ticks cannot revive it");
            }
            using (var surface = new FocusIndicatorSurface(true, animationsAllowed: delegate { return false; },
                now: delegate { return clock; }, current: delegate { return true; })) {
                surface.Present(Visual(target)); window = surface.Window;
                check(surface.HasStaging && surface.LightingCache != null && surface.RevealProgress == 1 && surface.RevealStrength == 0 && surface.EnergyPhase == 0 && !surface.AnimationRunning,
                    "The production premium upload path owns persistent native staging and stays static when motion is disabled");
                surface.Dispose();
                check(Clean(surface, window), "Production staging, renderer, shape and owned HWND are all released together");
            }
        }
        static void Failures(OwnedTarget target, Action<bool, string> check)
        {
            foreach (string reason in new[] { "focus changed", "geometry changed", "upload failure" }) {
                long clock = 0; bool current = true; int uploads = 0, stopped = 0; IntPtr window = IntPtr.Zero;
                Exception failure = null; Rectangle original = target.Bounds;
                try {
                    using (var surface = new FocusIndicatorSurface(true, delegate(IntPtr value, Bitmap image, Point location) {
                        window = value; uploads++;
                        if (reason == "upload failure" && uploads == 3) return false;
                        return ShelfPaint.PresentLayered(value, image, location);
                    }, delegate { return true; }, delegate { return clock; }, delegate { return current; }, delegate(IFocusIndicatorSurface source, Exception error) {
                        stopped++; failure = error;
                    })) {
                        surface.Present(Visual(target)); clock = FocusIndicatorMotion.TotalMilliseconds; surface.AdvanceAnimation();
                        clock = 1200;
                        if (reason == "focus changed") current = false;
                        if (reason == "geometry changed") target.Bounds = new Rectangle(original.X + 10, original.Y + 10, original.Width, original.Height);
                        surface.AdvanceAnimation();
                        bool correct = reason == "focus changed" ? failure == null : reason == "geometry changed" ? failure is FocusIndicatorGeometryChangedException : failure is InvalidOperationException;
                        check(stopped == 1 && correct && Clean(surface, window),
                            "Post-reveal continuous flow re-verifies target/focus and safely tears down on " + reason);
                        int retainedUploads = uploads; surface.AdvanceAnimation(); surface.Dispose();
                        check(stopped == 1 && uploads == retainedUploads && Clean(surface, window), "Stopped energy rejects late ticks on " + reason);
                    }
                } finally { target.Bounds = original; }
            }
        }
        static void Reentrancy(OwnedTarget target, Action<bool, string> check)
        {
            foreach (bool replace in new[] { false, true }) {
                long clock = 0; int uploads = 0; bool inFlightAlive = false; IntPtr window = IntPtr.Zero;
                FocusIndicatorSurface surface = null; FocusIndicatorLightingRenderer replacement = null; Bitmap original = null;
                surface = new FocusIndicatorSurface(true, delegate(IntPtr value, Bitmap image, Point location) {
                    window = value; uploads++;
                    if (uploads == 2) {
                        original = image;
                        if (replace) { surface.Present(Visual(target, 3, "Sky", FocusIndicatorMode.InsetEdge)); replacement = surface.LightingCache; }
                        else surface.Dispose();
                        inFlightAlive = !Released(image);
                        return true;
                    }
                    return ShelfPaint.PresentLayered(value, image, location);
                }, delegate { return true; }, delegate { return clock; }, delegate { return true; });
                using (surface) {
                    surface.Present(Visual(target)); clock = 1000; surface.AdvanceAnimation();
                    if (replace) {
                        check(inFlightAlive && uploads == 3 && Object.ReferenceEquals(replacement, surface.LightingCache) && Released(original) &&
                            surface.RevealProgress == 1 && surface.EnergyRunning,
                            "A reentrant palette replacement keeps the in-flight bitmap alive, commits only its successor and retires the stale cache afterward");
                    } else check(inFlightAlive && uploads == 2 && Clean(surface, window) && Released(original),
                        "Reentrant energy disposal cancels immediately but defers freeing the bitmap until its upload returns");
                    surface.Dispose(); surface.AdvanceAnimation();
                    check(Clean(surface, window), "Reentrant energy cleanup remains idempotent and stale ticks stay inert");
                }
            }
        }
        static void StaleUploadFailures(OwnedTarget target, Action<bool, string> check)
        {
            foreach (bool timer in new[] { false, true }) foreach (bool throws in new[] { false, true }) {
                long clock = 0; int uploads = 0, stops = 0; IntPtr window = IntPtr.Zero;
                FocusIndicatorSurface surface = null; FocusIndicatorLightingRenderer successor = null;
                Bitmap obsolete = null; bool inFlightAlive = false;
                surface = new FocusIndicatorSurface(true, delegate(IntPtr value, Bitmap image, Point location) {
                    window = value; uploads++;
                    if (uploads == 2) {
                        obsolete = image;
                        surface.Present(Visual(target, 3, "Sky", FocusIndicatorMode.InsetEdge));
                        successor = surface.LightingCache;
                        inFlightAlive = !Released(image);
                        if (throws) throw new InvalidOperationException("Obsolete owned upload failure fixture");
                        return false;
                    }
                    return ShelfPaint.PresentLayered(value, image, location);
                }, delegate { return true; }, delegate { return clock; }, delegate { return true; }, delegate { stops++; });
                using (surface) {
                    surface.Present(Visual(target)); clock = 1000;
                    Exception escaped = null;
                    try {
                        if (timer) surface.AdvanceAnimation();
                        else surface.Present(Visual(target, 3, "Mint"));
                    } catch (Exception error) { escaped = error; }
                    string operation = timer ? "timer" : "Present", failure = throws ? "exception" : "false result";
                    check(escaped == null && stops == 0 && uploads == 3 && inFlightAlive && Released(obsolete) &&
                        Native.IsWindow(window) && surface.Uploaded && surface.EnergyRunning &&
                        Object.ReferenceEquals(successor, surface.LightingCache),
                        "An obsolete " + operation + " upload's " + failure + " cannot report failure or dispose its committed successor");
                    clock = 2000; surface.AdvanceAnimation();
                    check(uploads == 4 && stops == 0 && surface.RevealProgress == 1 && surface.EnergyPhase == 2000 / 6000.0 &&
                        Object.ReferenceEquals(successor, surface.LightingCache),
                        "The successor continues its retained timeline after an obsolete " + operation + " upload's " + failure);
                    surface.Dispose(); surface.AdvanceAnimation();
                    check(Clean(surface, window), "Stale-failure replacement releases all owned resources on final cleanup");
                }
            }
        }
    }
}
