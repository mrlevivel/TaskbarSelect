using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Injected surfaces and a suspended manager keep these checks free of native windows and polling.
    internal static class ActivationPreviewTests
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        sealed class Surface : IShelfActivationPreviewSurface
        {
            internal ShelfActivationPreviewLayout Last;
            internal int Presents, Disposals;
            internal bool FailPresent, FailDispose;
            public void Present(ShelfActivationPreviewLayout layout)
            {
                Presents++;
                if (FailPresent) throw new InvalidOperationException("Injected preview presentation failure");
                Last = layout;
            }
            public void Dispose()
            {
                Disposals++;
                if (FailDispose) throw new InvalidOperationException("Injected preview disposal failure");
            }
        }

        static Display Monitor(string identity, string device, int x, int y, int width, int height)
        { return new Display { Identity = identity, Device = device, X = x, Y = y, Width = width, Height = height, WorkHeight = height }; }

        static bool Fails(Action action)
        {
            try { action(); return false; }
            catch (InvalidOperationException) { return true; }
        }

        static T Field<T>(WindowShelfManager manager, string name)
        { return (T)typeof(WindowShelfManager).GetField(name, PrivateInstance).GetValue(manager); }

        static void SetField(WindowShelfManager manager, string name, object value)
        { typeof(WindowShelfManager).GetField(name, PrivateInstance).SetValue(manager, value); }

        static void Call(WindowShelfManager manager, string name, params object[] arguments)
        { typeof(WindowShelfManager).GetMethod(name, PrivateInstance).Invoke(manager, arguments); }

        static void Layout(Action<bool, string> assert)
        {
            var displays = new[] {
                Monitor("left", "left-device", -3840, -1200, 3840, 2160),
                Monitor("right", "right-device", 2560, 367, 1920, 1080),
                Monitor("narrow", "narrow-device", -37, 10, 37, 3),
                Monitor("small", "small-device", 100, -600, 160, 160)
            };
            foreach (Display display in displays)
                foreach (int width in new[] { Int32.MinValue, 79, 80, 480, 960, 961, Int32.MaxValue })
                    foreach (int dpi in new[] { 1, 96, 120, 144, 192, 288, 384, 1000 }) {
                        var layout = ShelfActivationPreviewLayout.Create(display, width, dpi);
                        assert(layout.Area == WindowShelfPolicy.Trigger(display, width),
                            "Preview exactly matches the physical activation area across size, coordinates, width clamps and DPI");
                        assert(WindowShelfPolicy.Bounds(display).Contains(layout.Area) && WindowShelfPolicy.Bounds(display).Contains(layout.Label) &&
                            layout.Bounds.Contains(layout.Area) && layout.Bounds.Contains(layout.Label) && layout.Label.Width > 0 && layout.Label.Height > 0,
                            "Preview label and activation area remain contained on narrow and high-DPI displays");
                        assert(layout.Dpi == Math.Max(96, Math.Min(384, dpi)), "Preview labels use a bounded DPI while activation pixels stay physical");
                    }
            var example = ShelfActivationPreviewLayout.Create(displays[1], 480, 288);
            assert(example.Area == new Rectangle(3280, 1441, 480, 6), "Known offset monitor has the exact bottom-centered six-pixel activation strip");
            var tiny = ShelfActivationPreviewLayout.Create(displays[2], 960, 96);
            assert(tiny.Area == new Rectangle(-37, 10, 37, 3), "Activation strip clamps to a display narrower and shorter than the configured area");

            var monitor = Monitor("work", "work-device", -2000, -900, 1920, 1080);
            var work = new Rectangle(-1920, -850, 1680, 880);
            foreach (int dpi in new[] { 96, 192, 384 }) {
                var layout = ShelfActivationPreviewLayout.Create(monitor, 960, dpi, work);
                assert(layout.Area == WindowShelfPolicy.Trigger(monitor, 960) && work.Contains(layout.Label),
                    "Actual work area constrains only the caption, keeping the activation strip on the full monitor edge");
            }
            var narrowWork = new Rectangle(-1800, -700, 1, 1);
            assert(ShelfActivationPreviewLayout.Create(monitor, 480, 384, narrowWork).Label == narrowWork,
                "A minimal valid work area still receives a bounded caption");
            foreach (Rectangle invalid in new[] { Rectangle.Empty, new Rectangle(-2100, -900, 1920, 1080), new Rectangle(-2000, -900, 1921, 1080) })
                assert(ShelfActivationPreviewLayout.Create(monitor, 480, 192, invalid).Label ==
                    ShelfActivationPreviewLayout.Create(monitor, 480, 192).Label,
                    "Missing or out-of-monitor work areas safely fall back to monitor bounds");
            foreach (Display invalid in new[] { null, Monitor("zero", "zero", 0, 0, 0, 100), Monitor("negative", "negative", 0, 0, 100, -1) }) {
                bool rejected = false;
                try { ShelfActivationPreviewLayout.Create(invalid, 480, 96); }
                catch (ArgumentException) { rejected = true; }
                assert(rejected, "Layout rejects absent or invalid monitor geometry");
            }
        }

        static void Lifecycle(Action<bool, string> assert)
        {
            bool rejected = false;
            try { new ShelfActivationPreview(null); }
            catch (ArgumentNullException) { rejected = true; }
            assert(rejected, "A preview requires an explicit usable surface factory");
            var a = Monitor("a", "device-a", -1920, 0, 1920, 1080);
            var b = Monitor("b", "device-b", 2560, 300, 1920, 1080);
            var made = new List<Surface>(); int dpiReads = 0;
            using (var preview = new ShelfActivationPreview(delegate { var surface = new Surface(); made.Add(surface); return surface; })) {
                preview.Update(new[] { a, b }, 480, delegate(Display display) { dpiReads++; return display == a ? 96 : 288; });
                assert(preview.Count == 2 && made.Count == 2 && dpiReads == 2 && made.All(s => s.Presents == 1),
                    "Every eligible configured display receives its own preview surface");
                a.X = -2400;
                preview.Update(new[] { b, a }, 960, delegate { return 192; }, delegate(Display display) {
                    return new Rectangle(display.X + 40, display.Y + 30, display.Width - 80, display.Height - 100);
                });
                assert(made.Count == 2 && made.All(s => s.Presents == 2 && s.Disposals == 0) &&
                    made[0].Last.Area == WindowShelfPolicy.Trigger(a, 960) && made[1].Last.Area == WindowShelfPolicy.Trigger(b, 960),
                    "Width and topology changes update retained surfaces without leaving old geometry");
                assert(made[0].Last.Label.Left >= a.X + 40 && made[1].Last.Label.Bottom <= b.Y + b.Height - 70,
                    "The service supplies each display's actual usable work area to its caption layout");
                var replacement = Monitor("a", "new-device-a", 4000, -1200, 1920, 1080);
                preview.Update(new[] { replacement }, 240, null);
                assert(preview.Count == 1 && made.Count == 3 && made[0].Disposals == 1 && made[1].Disposals == 1 &&
                    made[2].Last.Area == WindowShelfPolicy.Trigger(replacement, 240) && made[2].Last.Dpi == 96,
                    "Changed device identity replaces the old surface and removes disconnected displays");
                preview.Hide(); preview.Hide();
                assert(preview.Count == 0 && made.All(s => s.Disposals == 1), "Repeated hiding disposes each surface exactly once");
                preview.Update(new[] { b }, 80, null);
                assert(preview.Count == 1 && made.Count == 4 && made[3].Presents == 1, "A hidden preview can begin a fresh independent session");
                preview.Dispose(); preview.Dispose();
                int creates = made.Count, reads = dpiReads;
                preview.Update(new[] { a, b }, 480, delegate { dpiReads++; throw new InvalidOperationException("Late DPI read"); });
                preview.Hide();
                assert(preview.Count == 0 && made.Count == creates && dpiReads == reads && made.All(s => s.Disposals == 1),
                    "Disposal rejects all later creation, presentation and provider reads");
            }
        }

        static void HeightLayoutAndLifecycle(Action<bool, string> assert)
        {
            var displays = new[] {
                Monitor("upper", "upper-device", -1920, -1080, 1920, 1080),
                Monitor("small-upper", "small-upper-device", -80, -160, 160, 160)
            };
            foreach (Display display in displays)
                foreach (int width in new[] { 80, 480, 960 })
                    foreach (int height in new[] { Int32.MinValue, 0, 1, 2, 3, 4, 5, 6, 24, 48, 96, 97, Int32.MaxValue })
                        foreach (int dpi in new[] { 96, 144, 192, 384 }) {
                            var work = new Rectangle(display.X, display.Y, display.Width, Math.Max(1, display.Height - 40));
                            var layout = ShelfActivationPreviewLayout.Create(display, width, height, dpi, work);
                            int physicalHeight = Math.Min(display.Height, Math.Max(1, Math.Min(96, height)));
                            assert(layout.Area == WindowShelfPolicy.Trigger(display, width, height) &&
                                layout.Area.Height == physicalHeight && layout.Area.Bottom == display.Y + display.Height &&
                                layout.Area.Width == WindowShelfPolicy.Trigger(display, width).Width,
                                "Custom preview height shares exact bounded physical pixels with the real trigger at every DPI");
                            assert(WindowShelfPolicy.Bounds(display).Contains(layout.Bounds) && work.Contains(layout.Label) &&
                                !layout.Area.Contains(layout.Area.Left, display.Y + display.Height),
                                "Custom preview stays on the upper/minimum-size display and keeps its caption inside available work area");
                        }
            var made = new List<Surface>(); int dpiReads = 0;
            using (var preview = new ShelfActivationPreview(delegate { var surface = new Surface(); made.Add(surface); return surface; })) {
                var upper = displays[0];
                preview.Update(new[] { upper }, 480, 6, delegate { dpiReads++; return 192; });
                assert(preview.Count == 1 && made.Count == 1 && made[0].Last.Area == new Rectangle(-1200, -6, 480, 6),
                    "The preview service passes a six-pixel custom height through to presentation");
                preview.Update(new[] { upper }, 480, 96, delegate { dpiReads++; return 384; });
                assert(preview.Count == 1 && made.Count == 1 && made[0].Presents == 2 && made[0].Disposals == 0 &&
                    made[0].Last.Area == new Rectangle(-1200, -96, 480, 96),
                    "A height-only update replaces retained preview geometry without creating or leaking another surface");
                preview.Update(new[] { upper }, 480, 48, delegate { dpiReads++; return 96; },
                    delegate(Display display) { return new Rectangle(display.X, display.Y, display.Width, display.Height - 48); });
                assert(made[0].Presents == 3 && made[0].Last.Area == WindowShelfPolicy.Trigger(upper, 480, 48) &&
                    made[0].Last.Label.Bottom <= upper.Y + upper.Height - 48,
                    "Custom-height preview also honors an explicit current work-area provider");
                preview.Update(new[] { upper }, 480, null);
                assert(made[0].Presents == 4 && made[0].Last.Area.Height == 6,
                    "The compatibility update overload uses the current default height");
                preview.Hide();
                assert(preview.Count == 0 && made[0].Disposals == 1, "Hiding a custom-height preview disposes its surface exactly once");
                preview.Dispose();
                preview.Update(new[] { upper }, 480, 96, delegate { dpiReads++; return 384; });
                assert(made.Count == 1 && dpiReads == 3 && made[0].Disposals == 1,
                    "Disposal prevents late height updates from presenting, recreating or reading display providers");
            }
        }

        static void Eligibility(Action<bool, string> assert)
        {
            int creates = 0, reads = 0;
            using (var preview = new ShelfActivationPreview(delegate { creates++; return new Surface(); })) {
                var valid = Monitor("valid", "valid-device", 0, 0, 160, 160);
                var primary = Monitor("primary", "primary-device", 0, 0, 1920, 1080); primary.Primary = true;
                var duplicateA = Monitor("ambiguous", "duplicate-a", 0, 0, 1920, 1080);
                var duplicateB = Monitor("ambiguous", "duplicate-b", 1920, 0, 1920, 1080);
                preview.Update(new[] { null, primary, duplicateA, duplicateB,
                    Monitor("short", "short-device", 0, 0, 160, 159), Monitor("narrow", "narrow-device", 0, 0, 159, 160),
                    Monitor("", "unnamed", 0, 0, 1920, 1080), Monitor("identity", " ", 0, 0, 1920, 1080), valid }, 480,
                    delegate { reads++; return 96; });
                assert(preview.Count == 1 && creates == 1 && reads == 1,
                    "Absent, primary, ambiguous, undersized and unidentified displays never create or present a preview");
                preview.Update(null, 480, delegate { reads++; return 96; });
                assert(preview.Count == 0 && creates == 1 && reads == 1, "An empty target set removes all previews without provider reads");
            }
        }

        static void FailureCleanup(Action<bool, string> assert)
        {
            var a = Monitor("a", "device-a", 0, 0, 1920, 1080);
            var b = Monitor("b", "device-b", 1920, 0, 1920, 1080);
            var c = Monitor("c", "device-c", 3840, 0, 1920, 1080);
            foreach (string failure in new[] { "present", "factory", "null", "dpi", "work" }) {
                var made = new List<Surface>(); bool failFactory = false;
                using (var preview = new ShelfActivationPreview(delegate {
                    if (failFactory && failure == "factory") throw new InvalidOperationException("Injected factory failure");
                    if (failFactory && failure == "null") return null;
                    var surface = new Surface(); made.Add(surface); return surface;
                })) {
                    preview.Update(new[] { a, b }, 480, null);
                    made[0].FailPresent = failure == "present"; failFactory = true;
                    bool failed = Fails(delegate {
                        preview.Update(new[] { a, b, c }, 960, delegate {
                            if (failure == "dpi") throw new InvalidOperationException("Injected DPI failure");
                            return 192;
                        }, delegate(Display display) {
                            if (failure == "work") throw new InvalidOperationException("Injected work area failure");
                            return WindowShelfPolicy.Bounds(display);
                        });
                    });
                    assert(failed && preview.Count == 0 && made.All(s => s.Disposals == 1),
                        "Presentation, creation and geometry-provider failures clean up every owned surface: " + failure);
                }
            }
            var cleanup = new List<Surface>();
            var interrupted = new ShelfActivationPreview(delegate { var surface = new Surface(); cleanup.Add(surface); return surface; });
            try {
                interrupted.Update(new[] { a, b, c }, 480, null); cleanup[0].FailDispose = true;
                assert(Fails(interrupted.Dispose) && interrupted.Count == 0 && cleanup.All(s => s.Disposals == 1),
                    "A failing surface disposal still releases every other surface before reporting failure");
                int count = cleanup.Count;
                interrupted.Update(new[] { a }, 480, delegate { throw new InvalidOperationException("Read after failed disposal"); });
                interrupted.Hide(); interrupted.Dispose();
                assert(cleanup.Count == count && cleanup.All(s => s.Disposals == 1),
                    "A failed disposal remains terminal and cannot recreate or repeatedly dispose surfaces");
            } finally { interrupted.Dispose(); }
        }

        static void Suppression(Action<bool, string> assert)
        {
            var display = Monitor("target", "target-device", 100, 100, 1920, 1080);
            var settings = Settings.CreateFresh(); int targetsRead = 0, rulesRead = 0, moved = 0;
            using (var manager = new WindowShelfManager(delegate { targetsRead++; return new List<Display>(); },
                delegate { rulesRead++; return new List<AppRule>(); }, delegate { moved++; return false; }, null, delegate { return settings; })) {
                var dwell = Field<ShelfActivationDwell>(manager, "activation");
                var timer = Field<Timer>(manager, "timer");
                int generation = Field<int>(manager, "generation");
                var original = new ShelfRequest(generation);
                SetField(manager, "pending", original); SetField(manager, "target", display);
                SetField(manager, "latched", true); SetField(manager, "outside", 12L);
                dwell.Update(1000, true, true);
                assert(dwell.Update(1400, true, true), "Suppression fixture begins with an established activation dwell");
                manager.SetActivationPreview(true);
                assert(original.Cancelled && Field<ShelfRequest>(manager, "pending") == null && Field<Display>(manager, "target") == null &&
                    !Field<bool>(manager, "latched") && Field<long>(manager, "outside") == -1 && !dwell.Update(2000, true, true),
                    "Beginning an activation preview cancels pending catalog work and clears target, latch and old dwell");
                int suspendedGeneration = Field<int>(manager, "generation");
                manager.SetActivationPreview(true); manager.Refresh();
                assert(Field<int>(manager, "generation") == suspendedGeneration && !timer.Enabled && targetsRead == 0 && rulesRead == 0,
                    "Repeated preview entry and periodic refresh preserve suppression without starting native polling");
                var window = new ShelfWindow { Handle = new IntPtr(1), Process = 1, ProcessStarted = 1, AppKey = "exe:neutral" };
                var group = new List<ShelfWindow> { window };
                var late = new ShelfRequest(suspendedGeneration); late.Complete(new ShelfRequest.Result());
                SetField(manager, "pending", late); SetField(manager, "target", display);
                SetField(manager, "groups", new List<List<ShelfWindow>> { group });
                Call(manager, "Tick"); Call(manager, "StartCatalog"); Call(manager, "CompleteRequest");
                Call(manager, "RequestPageIcons"); Call(manager, "OpenChooser", group);
                Call(manager, "Select", window, ShelfSelectionOrigin.Chooser);
                assert(Field<ShelfRequest>(manager, "pending") == late && !late.Cancelled && Field<int>(manager, "generation") == suspendedGeneration &&
                    Field<ShelfPopup>(manager, "shelf") == null && Field<ShelfPreviewChooser>(manager, "chooser") == null &&
                    !timer.Enabled && targetsRead == 0 && rulesRead == 0 && moved == 0,
                    "Late catalog, chooser, page and selection callbacks cannot reopen, poll or move while previewing");
                manager.SetActivationPreview(false);
                assert(late.Cancelled && Field<ShelfRequest>(manager, "pending") == null && Field<Display>(manager, "target") == null &&
                    !Field<bool>(manager, "activationPreview") && !dwell.Update(3000, true, true) && !timer.Enabled && targetsRead == 1 && rulesRead == 1,
                    "Ending preview clears late work and requires a fresh dwell without starting the polling timer in this pure check");
                assert(!dwell.Update(3099, true, true) && dwell.Update(3100, true, true) &&
                    !WindowShelfPolicy.AcceptResult(generation, Field<int>(manager, "generation"), false, true),
                    "New activation uses its full delay and cannot accept catalog results from before preview");
                assert(settings.WindowShelfActivationWidth == 480 && settings.WindowShelfActivationHeight == 6 && settings.WindowShelfHoverDelayMs == 100 && moved == 0,
                    "Preview suspension preserves sensitivity and never delegates movement");
                manager.Dispose(); int disposedGeneration = Field<int>(manager, "generation");
                manager.SetActivationPreview(true); manager.SetActivationPreview(false); manager.Refresh();
                assert(Field<int>(manager, "generation") == disposedGeneration && targetsRead == 1 && rulesRead == 1 && moved == 0,
                    "Disposed managers ignore later focus and refresh callbacks");
            }
        }

        internal static void Run(Action<bool, string> assert)
        {
            if (assert == null) throw new ArgumentNullException("assert");
            Layout(assert); HeightLayoutAndLifecycle(assert); Lifecycle(assert); Eligibility(assert); FailureCleanup(assert); Suppression(assert);
        }
    }
}
