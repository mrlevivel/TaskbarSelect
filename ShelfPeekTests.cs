using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class ShelfPeekTests
    {
        sealed class FakeApi : IShelfThumbnailApi
        {
            internal int Registered, Released, Updated, RegisterResult, QueryResult, UpdateResult;
            internal Size SourceSize = new Size(1920, 1080);
            internal Rectangle Destination;
            internal Action OnUpdate;
            public int Register(IntPtr destination, IntPtr source, out IntPtr thumbnail)
            { Registered++; thumbnail = RegisterResult == 0 ? new IntPtr(77) : IntPtr.Zero; return RegisterResult; }
            public int Query(IntPtr thumbnail, out Size size) { size = SourceSize; return QueryResult; }
            public int Update(IntPtr thumbnail, Rectangle bounds, byte opacity)
            { Updated++; Destination = bounds; if (OnUpdate != null) OnUpdate(); return UpdateResult; }
            public void Unregister(IntPtr thumbnail) { Released++; }
        }

        sealed class FakeSurface : IShelfPeekSurface
        {
            internal int Created, Resized, Converted, Shown, Disposed, UpdatesAtConvert;
            internal bool CreateResult = true, ResizeResult = true, LayerResult = true, ShowResult = true;
            internal FakeApi Api;
            internal Rectangle Bounds;
            public IntPtr Handle { get { return Disposed == 0 && Created > 0 ? new IntPtr(88) : IntPtr.Zero; } }
            public bool Visible { get; private set; }
            public bool Create(Rectangle bounds, IntPtr chooser)
            { Created++; Bounds = bounds; return CreateResult; }
            public bool Resize(Rectangle bounds)
            { Resized++; Bounds = bounds; return ResizeResult; }
            public bool EnableClickThrough()
            { Converted++; UpdatesAtConvert = Api.Updated; return LayerResult; }
            public bool ShowBehind(IntPtr chooser)
            { Shown++; Visible = Converted > 0 && LayerResult && ShowResult; return Visible; }
            public void Dispose() { Visible = false; Disposed++; }
        }

        static ShelfWindow Window()
        { return new ShelfWindow { Handle = new IntPtr(11), Process = 12, ProcessStarted = 1234, AppKey = "neutral:peek" }; }

        static ShelfPeek Peek(FakeApi api, FakeSurface surface, Func<ShelfWindow, bool> validate,
            Func<IntPtr, bool> visible, Func<Rectangle, bool> allowed)
        { surface.Api = api; return new ShelfPeek(validate, api, visible, allowed, delegate { return surface; }); }

        internal static void Run(Action<bool, string> assert)
        {
            var area = new Rectangle(-1920, 0, 1920, 1040);
            var bounds = new Rectangle(-1600, 100, 800, 600);
            Func<Rectangle, bool> allowed = delegate(Rectangle rectangle) { return area.Contains(rectangle); };
            var source = Window();
            var api = new FakeApi(); var surface = new FakeSurface(); bool valid = true, visible = true;
            using (var peek = Peek(api, surface, delegate { return valid; }, delegate { return visible; }, allowed)) {
                assert(!peek.Show(source, Rectangle.Empty, new IntPtr(9)) &&
                    !peek.Show(source, new Rectangle(-2000, 0, 800, 600), new IntPtr(9)) &&
                    !peek.Show(source, bounds, IntPtr.Zero) && surface.Created == 0,
                    "Empty, off-workarea and missing-chooser requests never create an overlay");
                visible = false;
                assert(!peek.Show(source, bounds, new IntPtr(9)) && api.Registered == 0,
                    "Hidden sources are refused before DWM registration");
                visible = true; valid = false;
                assert(!peek.Show(source, bounds, new IntPtr(9)) && api.Registered == 0,
                    "Invalid source identity is refused before DWM registration");
                valid = true;
                assert(peek.Show(source, bounds, new IntPtr(9)) && peek.IsVisible &&
                    peek.SourceHandle == source.Handle && peek.WindowHandle == new IntPtr(88),
                    "A valid source owns one nonactivating destination and one registration");
                var fitted = ShelfPreviewLayout.AspectFit(api.SourceSize, new Rectangle(Point.Empty, bounds.Size));
                assert(surface.Bounds == new Rectangle(bounds.X + fitted.X, bounds.Y + fitted.Y, fitted.Width, fitted.Height) &&
                    bounds.Contains(surface.Bounds) && api.Destination == new Rectangle(Point.Empty, fitted.Size),
                    "The entire overlay shrinks to the aspect-fit image, without blank margins or crop");
                assert(api.Registered == 1 && api.Released == 0 && api.Updated == 2,
                    "Registration and final fit both succeed before the overlay appears");
                assert(surface.Converted == 1 && surface.UpdatesAtConvert == 2 && surface.Shown == 1,
                    "Input passthrough is enabled after the first DWM presentation and before display");
                valid = false; peek.Check();
                assert(!peek.IsVisible && peek.SourceHandle == IntPtr.Zero && peek.WindowHandle == IntPtr.Zero &&
                    api.Released == 1 && surface.Disposed == 1,
                    "Invalidation releases the DWM handle and destroys the destination");
            }

            api = new FakeApi(); surface = new FakeSurface(); visible = true;
            using (var peek = Peek(api, surface, delegate { return true; }, delegate { return visible; }, allowed)) {
                assert(peek.Show(source, bounds, new IntPtr(9)), "A visible source can be shown");
                visible = false; peek.Check();
                assert(!peek.IsVisible && api.Released == 1 && surface.Disposed == 1,
                    "A source that becomes hidden is removed on the live validation tick");
                peek.Hide(); peek.Hide();
                assert(api.Released == 1 && surface.Disposed == 1, "Repeated hide is idempotent");
            }

            api = new FakeApi(); surface = new FakeSurface();
            using (var peek = Peek(api, surface, delegate { return true; }, delegate { return true; }, allowed)) {
                assert(peek.Show(source, bounds, new IntPtr(9)), "A bounded peek can be shown");
                area = Rectangle.Empty; peek.Check();
                assert(!peek.IsVisible && api.Released == 1 && surface.Disposed == 1,
                    "A changed work area removes the visible peek and its registration");
                area = new Rectangle(-1920, 0, 1920, 1040);
            }

            api = new FakeApi(); surface = new FakeSurface(); bool iconic = false;
            surface.Api = api;
            using (var peek = new ShelfPeek(delegate { return true; }, api, delegate { return true; },
                delegate { return iconic; }, allowed, delegate { return surface; })) {
                assert(peek.Show(source, bounds, new IntPtr(9)), "A non-iconic source can be shown");
                iconic = true; peek.Check();
                assert(!peek.IsVisible && api.Released == 1 && surface.Disposed == 1,
                    "A newly minimized source drops the full-size peek");
            }

            api = new FakeApi(); surface = new FakeSurface();
            using (var peek = Peek(api, surface, delegate { return true; }, delegate { return true; }, allowed)) {
                assert(peek.Show(source, bounds, new IntPtr(9)), "A stable source size can be shown");
                api.SourceSize = new Size(1600, 900); peek.Check();
                assert(!peek.IsVisible && api.Released == 1 && surface.Disposed == 1,
                    "A resized DWM source closes instead of stretching the cached aspect fit");
            }

            for (int failure = 0; failure < 8; failure++) {
                api = new FakeApi(); surface = new FakeSurface();
                if (failure == 0) surface.CreateResult = false;
                if (failure == 1) api.RegisterResult = -1;
                if (failure == 2) api.QueryResult = -1;
                if (failure == 3) api.SourceSize = Size.Empty;
                if (failure == 4) api.UpdateResult = -1;
                if (failure == 5) surface.ResizeResult = false;
                if (failure == 6) surface.LayerResult = false;
                if (failure == 7) surface.ShowResult = false;
                using (var peek = Peek(api, surface, delegate { return true; }, delegate { return true; }, allowed))
                    assert(!peek.Show(source, bounds, new IntPtr(9)) && !peek.IsVisible &&
                        api.Released == (failure >= 2 ? 1 : 0) && surface.Disposed == 1 &&
                        (failure != 6 || surface.Shown == 0),
                        "Failed surface or DWM steps leave no blank, blocking overlay or registration");
            }

            api = new FakeApi(); surface = new FakeSurface();
            using (var peek = Peek(api, surface, delegate { return true; }, delegate { return true; }, allowed)) {
                assert(peek.Show(source, bounds, new IntPtr(9)), "A valid peek can be disposed");
                api.UpdateResult = -1; peek.Check();
                assert(!peek.IsVisible && api.Released == 1 && surface.Disposed == 1,
                    "DWM update failure while visible closes and unregisters the peek");
            }

            api = new FakeApi(); surface = new FakeSurface(); valid = true;
            api.OnUpdate = delegate { valid = false; };
            using (var peek = Peek(api, surface, delegate { return valid; }, delegate { return true; }, allowed))
                assert(!peek.Show(source, bounds, new IntPtr(9)) && api.Released == 1 && surface.Disposed == 1 && surface.Shown == 0,
                    "Identity loss during DWM update never publishes the overlay");

            api = new FakeApi(); surface = new FakeSurface();
            using (var peek = Peek(api, surface, delegate { return true; }, delegate { return true; },
                delegate(Rectangle rectangle) { throw new InvalidOperationException(); }))
                assert(!peek.Show(source, bounds, new IntPtr(9)) && surface.Created == 0,
                    "A failed geometry preflight does not create a destination");

            api = new FakeApi(); surface = new FakeSurface();
            var disposedPeek = Peek(api, surface, delegate { return true; }, delegate { return true; }, allowed);
            assert(disposedPeek.Show(source, bounds, new IntPtr(9)), "A valid peek can start before disposal");
            disposedPeek.Dispose(); disposedPeek.Dispose();
            disposedPeek.Hide();
            assert(!disposedPeek.Show(source, bounds, new IntPtr(9)) && api.Released == 1 && surface.Disposed == 1,
                "Disposal and later hide are idempotent and prohibit reuse");
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);

        sealed class NeutralWindow : Form
        {
            internal NeutralWindow(Rectangle bounds, bool topmost)
            {
                Bounds = bounds; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
                TopMost = topmost; StartPosition = FormStartPosition.Manual; BackColor = Color.Teal;
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x80 | 0x08000000; return value; } }
        }

        // Opt-in only: creates and touches owned neutral windows, never an existing user window.
        internal static void RunNative(Action<bool, string> assert)
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            var sourceBounds = new Rectangle(work.X + 24, work.Y + 24, 320, 180);
            var chooserBounds = new Rectangle(work.Right - 120, work.Bottom - 80, 96, 48);
            var peekBounds = new Rectangle(work.X + (work.Width - 480) / 2, work.Y + (work.Height - 300) / 2, 480, 300);
            using (var source = new NeutralWindow(sourceBounds, false))
            using (var chooser = new NeutralWindow(chooserBounds, true)) {
                source.Show(); chooser.Show();
                var identity = Window(); identity.Handle = source.Handle;
                using (var peek = new ShelfPeek(delegate(ShelfWindow window) { return window.Handle == source.Handle; })) {
                    var original = source.Bounds;
                    bool shown = peek.Show(identity, peekBounds, chooser.Handle);
                    assert(shown && peek.IsVisible, "Neutral native DWM peek appears on an owned live destination");
                    if (shown) {
                        int style = GetWindowLong(peek.WindowHandle, -16), extended = GetWindowLong(peek.WindowHandle, -20);
                        assert((style & ShelfPeekSurface.WindowStyle) == ShelfPeekSurface.WindowStyle &&
                            (extended & ShelfPeekSurface.PresentedExtendedStyle) == ShelfPeekSurface.PresentedExtendedStyle &&
                            !IsWindowEnabled(peek.WindowHandle),
                            "Peek is disabled, no-activate and layered-transparent after DWM setup");
                        assert(source.Bounds == original && source.Visible, "Peeking leaves the neutral source visible and unmoved");
                        peek.Hide();
                        assert(!peek.IsVisible && peek.WindowHandle == IntPtr.Zero, "Native hide removes the destination");
                    }
                }
            }
        }
    }
}
