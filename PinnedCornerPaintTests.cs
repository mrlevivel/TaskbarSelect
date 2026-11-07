using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class PinnedCornerPaintTests
    {
        [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        static FocusIndicatorTarget Target(int dpi = 96, int corner = 8)
        {
            return new FocusIndicatorTarget {
                Identity = new FocusedWindowSnapshot { Window = new IntPtr(71), Root = new IntPtr(71), Process = 12,
                    Thread = 13, ProcessStarted = 14, Visible = true, ClassName = "Neutral corner paint fixture" },
                Frame = new Rectangle(-80, -40, 800, 600), Monitors = new[] { new Rectangle(-500, -500, 3000, 3000) },
                Dpi = dpi, Corner = corner
            };
        }
        static int[] Pixels(Bitmap image)
        {
            var result = new int[image.Width * image.Height];
            BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < image.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), result, y * image.Width, image.Width); }
            finally { image.UnlockBits(data); }
            return result;
        }
        static int Alpha(int pixel) { return (int)((uint)pixel >> 24); }
        internal static void Run(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 120, 144, 192, 288, 768 }) foreach (bool dark in new[] { false, true })
                foreach (bool square in new[] { false, true }) {
                    int corner = square ? 0 : (8 * dpi + 48) / 96;
                    var target = Target(dpi, corner);
                    var focus = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, "Forest Green", dark);
                    var inactive = FocusIndicatorVisual.CreatePinCorner(target, false, dark, "Forest Green");
                    var active = FocusIndicatorVisual.CreatePinCorner(target, true, dark, "Forest Green", focus);
                    check(inactive != null && active != null && inactive.PinCorner && active.PinRail &&
                        inactive.PinRailInset == 0 && active.PinRailInset == 0 && inactive.Lighting == FocusLightingMode.None &&
                        inactive.Bounds.Width <= inactive.PinCornerLength + inactive.GlowWidth * 2 &&
                        inactive.Bounds.Height <= inactive.PinCornerLength + inactive.GlowWidth * 2,
                        "The lit pin cue stays on the original perimeter in a small static corner crop at " + dpi + "/" + dark + "/" + square);
                    using (Bitmap a = FocusIndicatorPaint.Render(inactive, .1, .2, 1))
                    using (Bitmap b = FocusIndicatorPaint.Render(active, 1, .8, 0))
                    using (Region shape = FocusIndicatorPaint.Shape(active))
                    using (Graphics graphics = Graphics.FromImage(b)) {
                        IntPtr native = shape.GetHrgn(graphics);
                        try {
                            int[] x = Pixels(a), y = Pixels(b); bool equal = true, premultiplied = true, covers = true, smooth = false, mirror = true;
                            int peak = 0;
                            for (int row = 0; row < a.Height; row++) for (int column = 0; column < a.Width; column++) {
                                int pixel = x[row * a.Width + column], alpha = Alpha(pixel);
                                equal &= pixel == y[row * a.Width + column];
                                premultiplied &= ((pixel >> 16) & 255) <= alpha && ((pixel >> 8) & 255) <= alpha && (pixel & 255) <= alpha && (alpha != 0 || pixel == 0);
                                covers &= alpha == 0 || PtInRegion(native, column, row);
                                smooth |= alpha > 0 && alpha < 255; peak = Math.Max(peak, alpha);
                                mirror &= alpha == Alpha(x[column * a.Width + row]);
                            }
                            check(equal && premultiplied && covers && smooth && mirror && peak == 255,
                                "Rounded/square cue pixels are static, focus-independent, symmetric, premultiplied and wholly retained by the actual HRGN at " + dpi + "/" + dark + "/" + square);
                            Point client = new Point(target.Frame.Left + 100 - active.Bounds.Left, target.Frame.Top + 100 - active.Bounds.Top);
                            check(!PtInRegion(native, client.X, client.Y) && !shape.IsVisible(target.Frame.Width - 1, target.Frame.Height - 1),
                                "No client rectangle, other corner or full-window region is introduced at " + dpi + "/" + dark + "/" + square);
                        } finally { DeleteObject(native); }
                    }
                }
            TaperAndAccent(check); ClippingAndInvalid(check); ZOrderPolicy(check); Reservation(check); BoundReservation(check);
        }
        static void Reservation(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 144 }) foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge })
                foreach (int finish in new[] { 0, 1 }) foreach (int lighting in new[] { 0, 1, 2, 3 }) {
                    var target = Target(dpi, (8 * dpi + 48) / 96);
                    target.Frame = new Rectangle(-80, -40, 180, 130);
                    var ordinary = FocusIndicatorVisual.Create(target, mode, "Forest Green", true, false, lighting, finish);
                    var reserved = FocusIndicatorVisual.Create(target, mode, "Forest Green", true, false, lighting, finish, true);
                    var unlit = FocusIndicatorVisual.Create(target, mode, "Forest Green", true, false, 0, finish);
                    check(reserved.ReservedPinCorner && reserved.Copy().ReservedPinCorner && !ordinary.SameAppearance(reserved),
                        "Reservation is a copied appearance property for cache invalidation at " + dpi + "/" + mode + "/" + finish + "/" + lighting);
                    using (Bitmap normal = FocusIndicatorPaint.Render(ordinary, 1, .17))
                    using (Bitmap quiet = FocusIndicatorPaint.Render(reserved, 1, .17))
                    using (Bitmap plain = FocusIndicatorPaint.Render(unlit, 1, .17)) {
                        int[] a = Pixels(normal), b = Pixels(quiet), c = Pixels(plain);
                        bool sameOutside = true, baseInside = true, premultiplied = true, changed = false;
                        int insideSamples = 0, outsideSamples = 0;
                        for (int y = 0; y < quiet.Height; y++) for (int x = 0; x < quiet.Width; x++) {
                            int index = y * quiet.Width + x; double weight = FocusIndicatorPaint.ReservedCornerWeight(reserved, x, y);
                            if (weight == 0) { sameOutside &= a[index] == b[index]; outsideSamples++; }
                            if (weight == 1) {
                                int px = x + reserved.Bounds.X - unlit.Bounds.X, py = y + reserved.Bounds.Y - unlit.Bounds.Y;
                                int expected = px >= 0 && py >= 0 && px < plain.Width && py < plain.Height ? c[py * plain.Width + px] : 0;
                                baseInside &= b[index] == expected; insideSamples++;
                            }
                            changed |= a[index] != b[index]; int alpha = Alpha(b[index]);
                            premultiplied &= ((b[index] >> 16) & 255) <= alpha && ((b[index] >> 8) & 255) <= alpha && (b[index] & 255) <= alpha;
                        }
                        check(sameOutside && baseInside && premultiplied && insideSamples > 0 && outsideSamples > 0 && (lighting == 0 || changed),
                            "Quiet36DIP becomes the exact unlit edge; outside62DIP stays byte-identical with premultiplied blend at " + dpi + "/" + mode + "/" + finish + "/" + lighting);
                    }
                    using (Bitmap first = FocusIndicatorPaint.Render(reserved, .45, .1, 1))
                    using (Bitmap second = FocusIndicatorPaint.Render(reserved, .45, .6, 1)) {
                        int[] a = Pixels(first), b = Pixels(second); bool quietStatic = true, elsewhereMoves = false;
                        for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++) {
                            int index = y * first.Width + x; double weight = FocusIndicatorPaint.ReservedCornerWeight(reserved, x, y);
                            if (weight == 1) quietStatic &= a[index] == b[index];
                            if (weight == 0) elsewhereMoves |= a[index] != b[index];
                        }
                        check(quietStatic && (lighting != 3 || elsewhereMoves), "Reveal coverage stays intact while only unreserved energy responds to phase at " + dpi + "/" + mode + "/" + finish + "/" + lighting);
                    }
                }
            var cue = FocusIndicatorVisual.CreatePinCorner(Target(), false, true, "Forest Green");
            check(cue.Thickness == 3 && cue.PinCornerLength == 40 && cue.GlowWidth == 3,
                "Reserved pin geometry uses a3DIP near-white core,40DIP arms and tight3DIP halo at96DPI");
            using (Bitmap image = FocusIndicatorPaint.Render(cue)) {
                int left = cue.Target.Frame.Left - cue.Bounds.Left, top = cue.Target.Frame.Top - cue.Bounds.Top;
                Color rim = image.GetPixel(left + cue.Target.Corner + 1, top + 3);
                check(rim.A > 0 && rim.A < 180 && rim.R < 90 && image.GetPixel(left + cue.Target.Corner + 1, top + 2).R > 230,
                    "A low-alpha half-DIP inner contrast rim separates the white core without an opaque backing");
            }
        }
        sealed class ReservationSurface : IFocusIndicatorSurface
        {
            internal Action DuringPresent;
            public IntPtr Window { get { return new IntPtr(999); } }
            public void Present(FocusIndicatorVisual visual) { if (DuringPresent != null) DuringPresent(); }
            public void Dispose() { }
        }
        sealed class ReservationEvents : IFocusIndicatorEvents { public void Dispose() { } }
        static void BoundReservation(Action<bool, string> check)
        {
            var target = Target(); var captured = target.Identity.Copy(); bool pinned = true;
            var surface = new ReservationSurface();
            var api = new FocusIndicatorApi { Foreground = delegate { return target.Identity.Window; }, Read = delegate { return target; },
                DesktopAllowed = delegate { return true; }, Dark = delegate { return true; }, GlowAllowed = delegate { return true; },
                Now = delegate { return 1000; }, PointerButtonDown = delegate { return false; }, Watch = delegate { return new ReservationEvents(); } };
            Func<FocusedWindowSnapshot, bool> knownPin = delegate(FocusedWindowSnapshot current) { return pinned && WindowStatePolicy.SameIdentity(captured, current); };
            using (var indicator = new FocusIndicator(api, delegate { return surface; })) {
                // The exact process image is required for pin identity, even in
                // this fully injected geometry-only focus fixture.
                target.Identity.ProcessImage = captured.ProcessImage = @"C:\Neutral\Demo.exe";
                indicator.Update(2, "Forest Green", false, false, 3, 0, knownPin);
                check(indicator.CurrentVisual != null && indicator.CurrentVisual.ReservedPinCorner, "The focus reservation predicate receives the exact retained pinned identity");
                target = target.Copy(); target.Identity.Window = target.Identity.Root = new IntPtr(72);
                indicator.Update(2, "Forest Green", false, false, 3, 0, knownPin);
                check(indicator.CurrentVisual != null && !indicator.CurrentVisual.ReservedPinCorner, "A newly focused different window cannot inherit an asynchronously computed pin reservation");
                captured = target.Identity.Copy();
                surface.DuringPresent = delegate { pinned = false; };
                indicator.Update(2, "Forest Green", false, false, 3, 0, knownPin);
                check(indicator.CurrentVisual == null, "A pin revoked during presentation cannot leave a stale reserved focus visual published");
            }
        }
        static void ZOrderPolicy(Action<bool, string> check)
        {
            var target = new IntPtr(10); var own = new IntPtr(50); var first = new IntPtr(20); var second = new IntPtr(21); var foreign = new IntPtr(30);
            IntPtr after; bool keep;
            var previous = new System.Collections.Generic.Dictionary<IntPtr, IntPtr>();
            Func<IntPtr, IntPtr> read = delegate(IntPtr window) { IntPtr value; return previous.TryGetValue(window, out value) ? value : IntPtr.Zero; };
            Func<IntPtr, IntPtr, bool> known = delegate(IntPtr window, IntPtr bound) { return bound == target && (window == first || window == second); };
            previous[target] = first; previous[first] = foreign;
            check(FocusIndicatorSurface.TryPinCornerStack(target, own, read, known, out after, out keep) && after == foreign && !keep,
                "A new corner is inserted above only the contiguous authenticated focus surface and below the first foreign window");
            previous[first] = own; previous[own] = foreign;
            check(FocusIndicatorSurface.TryPinCornerStack(target, own, read, known, out after, out keep) && keep,
                "A corner already above its focus chain retains its existing z-order");
            previous[target] = own; previous[own] = first; previous[first] = foreign;
            check(FocusIndicatorSurface.TryPinCornerStack(target, own, read, known, out after, out keep) && after == foreign && !keep,
                "A focus surface above the existing corner is recognized without jumping the next unrelated window");
            previous[target] = first; previous[first] = own; previous[own] = second; previous[second] = foreign;
            check(FocusIndicatorSurface.TryPinCornerStack(target, own, read, known, out after, out keep) && after == foreign && !keep,
                "A corner interleaved between two known focus surfaces moves only above that contiguous chain");
            int reads = 0; previous[target] = foreign; previous[foreign] = first;
            check(FocusIndicatorSurface.TryPinCornerStack(target, own, delegate(IntPtr window) { reads++; return read(window); }, known, out after, out keep) &&
                after == foreign && reads == 1, "The scan stops at the first unrelated window instead of searching higher in the desktop stack");
            previous[target] = first;
            check(FocusIndicatorSurface.TryPinCornerStack(target, own, read, delegate { return false; }, out after, out keep) && after == first && !keep,
                "A marker without independent surface authentication cannot be skipped");
            check(FocusIndicatorSurface.TryPinCornerStack(target, own, read, delegate { throw new InvalidOperationException("Inert identity unavailable."); }, out after, out keep) && after == first,
                "An unreadable surface is treated as the first unrelated boundary, never a reason to jump to topmost");
            previous[target] = first; previous[first] = second; previous[second] = first;
            check(!FocusIndicatorSurface.TryPinCornerStack(target, own, read, known, out after, out keep), "A cyclic stack observation fails without a positioning fallback");
            check(!FocusIndicatorSurface.TryPinCornerStack(target, own, delegate { throw new InvalidOperationException("Inert stack read unavailable."); }, known, out after, out keep),
                "A failed stack read cannot request an unverified topmost fallback");
            previous.Clear(); previous[target] = new IntPtr(100);
            for (int i = 100; i < 112; i++) previous[new IntPtr(i)] = new IntPtr(i + 1);
            reads = 0;
            check(!FocusIndicatorSurface.TryPinCornerStack(target, own, delegate(IntPtr window) { reads++; return read(window); }, delegate { return true; }, out after, out keep) && reads <= 9,
                "The focus-chain walk is bounded even when every observed surface claims authentication");
        }
        sealed class InvisibleTarget : Form
        {
            internal InvisibleTarget(Rectangle bounds)
            {
                ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
                Bounds = bounds; Opacity = 0; TopMost = true;
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams {
                get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x20 | 0x80 | 0x8; return value; }
            }
        }
        // Deliberate opt-in only. Every HWND here is owned by this fixture.
        // Target/cover forms have zero opacity; renderer surfaces also clear
        // all pixels through the pre-existing invisibleFixture implementation.
        // No input, activation, foreign-window mutation or personal data read.
        internal static void RunNativeStack(Action<bool, string> check)
        {
            IntPtr foreground = GetForegroundWindow();
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            var bounds = new Rectangle(work.Left + 30, work.Top + 30, 180, 120);
            using (var target = new InvisibleTarget(bounds)) using (var cover = new InvisibleTarget(bounds)) {
                target.Show(); cover.Show();
                Application.DoEvents();
                var observation = FocusIndicatorNative.Read(target.Handle);
                var coverObservation = FocusIndicatorNative.Read(cover.Handle);
                check(observation != null && observation.Identity != null && observation.Frame.Width > 0 && observation.Frame.Height > 0,
                    "Invisible native fixture retains actual DWM target bounds and process identity");
                check(coverObservation != null && target.Opacity == 0 && cover.Opacity == 0 &&
                    (FocusIndicatorSurface.ExtendedStyle(target.Handle) & 0x08000000) != 0 && (FocusIndicatorSurface.ExtendedStyle(cover.Handle) & 0x08000000) != 0,
                    "Both owned fixture forms are fully transparent native NoActivate windows");
                long clock = 1000;
                using (var focus = new FocusIndicatorSurface(invisibleFixture: true, animationsAllowed: delegate { return true; },
                    now: delegate { return clock; }, current: delegate { return true; }))
                using (var corner = new FocusIndicatorSurface(invisibleFixture: true, animationsAllowed: delegate { return false; }, current: delegate { return true; },
                    lowerFocusOverlay: delegate(IntPtr window, IntPtr bound) { return window == focus.Window && MarksOwnFocus(window, bound); })) {
                    var focusVisual = FocusIndicatorVisual.Create(observation, FocusIndicatorMode.InsetEdge, "Forest Green", true);
                    focus.Present(focusVisual);
                    check(FocusIndicatorSurface.MarksFocusSurface(focus.Window, target.Handle) && !FocusIndicatorSurface.MarksFocusSurface(focus.Window, cover.Handle),
                        "Only the fixture's ordinary focus surface is marked with its exact target binding");
                    var cornerVisual = FocusIndicatorVisual.CreatePinCorner(observation, true, true, "Forest Green", focusVisual);
                    corner.Present(cornerVisual);
                    check(GetWindow(target.Handle, 3) == focus.Window && GetWindow(focus.Window, 3) == corner.Window && GetWindow(corner.Window, 3) == cover.Handle,
                        "Actual native order is unrelated own cover, lit corner, authenticated focus surface, own target");
                    for (int i = 0; i < 3; i++) { focus.Present(focusVisual); corner.Present(cornerVisual); }
                    check(GetWindow(target.Handle, 3) == focus.Window && GetWindow(focus.Window, 3) == corner.Window && GetWindow(corner.Window, 3) == cover.Handle,
                        "Repeated focused/corner presentation preserves priority without jumping the unrelated cover");
                    check(!FocusIndicatorSurface.MarksFocusSurface(corner.Window, target.Handle), "A pin cue never masquerades as an ordinary focus surface");
                    foreach (int lighting in new[] { 0, 1, 2, 3 }) foreach (int finish in new[] { 0, 1 }) {
                        clock += 80;
                        var ordinary = FocusIndicatorVisual.Create(observation, FocusIndicatorMode.InsetEdge, "Forest Green", true, false, lighting, finish);
                        var reserved = FocusIndicatorVisual.Create(observation, FocusIndicatorMode.InsetEdge, "Forest Green", true, false, lighting, finish, true);
                        focus.Present(ordinary); corner.Present(cornerVisual);
                        double progress = focus.RevealProgress, phase = focus.EnergyPhase;
                        focus.Present(reserved); corner.Present(cornerVisual);
                        check(focus.RevealProgress == progress && focus.EnergyPhase == phase,
                            "Reservation toggling preserves the native surface's existing reveal and flow clocks " + lighting + "/" + finish);
                        focus.Present(ordinary); corner.Present(cornerVisual);
                        check(GetWindow(target.Handle, 3) == focus.Window && GetWindow(focus.Window, 3) == corner.Window && GetWindow(corner.Window, 3) == cover.Handle,
                            "All native lighting/finish/cache routes retain cover above corner above focus above target " + lighting + "/" + finish);
                        bool zero = true;
                        foreach (int pixel in Pixels(focus.Pixels)) zero &= pixel == 0;
                        foreach (int pixel in Pixels(corner.Pixels)) zero &= pixel == 0;
                        check(zero, "Reserved premium and ordinary native presentation remain completely invisible in the fixture " + lighting + "/" + finish);
                    }
                    bool transparent = true;
                    foreach (int pixel in Pixels(focus.Pixels)) transparent &= pixel == 0;
                    foreach (int pixel in Pixels(corner.Pixels)) transparent &= pixel == 0;
                    check(transparent, "Both native renderer surfaces uploaded only zero-alpha fixture pixels");
                    var afterTarget = FocusIndicatorNative.Read(target.Handle); var afterCover = FocusIndicatorNative.Read(cover.Handle);
                    check(afterTarget != null && afterCover != null && afterTarget.Frame == observation.Frame && afterCover.Frame == coverObservation.Frame &&
                        WindowStatePolicy.SameIdentity(observation.Identity, afterTarget.Identity) && WindowStatePolicy.SameIdentity(coverObservation.Identity, afterCover.Identity),
                        "Corner priority leaves both owned target and unrelated-cover DWM frames and process identities unchanged");
                    IntPtr oldFocus = focus.Window;
                    focus.Dispose();
                    check(!FocusIndicatorSurface.MarksFocusSurface(oldFocus, target.Handle), "Disposal clears the ordinary focus marker and destroys its owned HWND");
                }
                target.Close(); cover.Close();
            }
            check(GetForegroundWindow() == foreground, "Invisible native corner-stack validation leaves foreground unchanged");
        }
        static bool MarksOwnFocus(IntPtr window, IntPtr target)
        { return FocusIndicatorSurface.MarksFocusSurface(window, target); }
        static void TaperAndAccent(Action<bool, string> check)
        {
            var target = Target();
            var visual = FocusIndicatorVisual.CreatePinCorner(target, false, true, "Forest Green");
            using (Bitmap image = FocusIndicatorPaint.Render(visual)) {
                int top = target.Frame.Top - visual.Bounds.Top, left = target.Frame.Left - visual.Bounds.Left;
                bool monotonic = true; int previous = 256;
                for (int x = target.Corner + 8; x < visual.PinCornerLength; x++) {
                    int alpha = image.GetPixel(left + x, top + 1).A;
                    monotonic &= alpha <= previous; previous = alpha;
                }
                check(monotonic && image.GetPixel(left + visual.PinCornerLength - 4, top + 1).A >
                    image.GetPixel(left + visual.PinCornerLength - 1, top + 1).A && image.GetPixel(left + visual.PinCornerLength, top + 1).A == 0,
                    "The top and mirrored side tails decay continuously to zero instead of ending in a square block");
                check(image.GetPixel(left + target.Corner + 1, top).R >= 240 && image.GetPixel(left + target.Corner + 1, top).A >= 240,
                    "The compact native-perimeter core remains crisp and near-white");
            }
            var coral = FocusIndicatorVisual.CreatePinCorner(target, false, true, "Coral");
            using (Bitmap a = FocusIndicatorPaint.Render(visual)) using (Bitmap b = FocusIndicatorPaint.Render(coral)) {
                int[] x = Pixels(a), y = Pixels(b); bool sameAlpha = true, changed = false;
                for (int i = 0; i < x.Length; i++) { sameAlpha &= Alpha(x[i]) == Alpha(y[i]); changed |= x[i] != y[i]; }
                check(sameAlpha && changed && visual.Outer == coral.Outer, "The chosen accent colors the tight halo without changing core geometry or adding motion");
            }
        }
        static void ClippingAndInvalid(Action<bool, string> check)
        {
            var target = Target();
            target.Monitors = new[] { new Rectangle(-100, -100, 26, 100), new Rectangle(-68, -100, 100, 100) };
            var visual = FocusIndicatorVisual.CreatePinCorner(target, false, true, "Forest Green");
            using (Bitmap image = FocusIndicatorPaint.Render(visual)) using (Region shape = FocusIndicatorPaint.Shape(visual)) {
                bool clipped = true;
                for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
                    var physical = new Point(x + visual.Bounds.X, y + visual.Bounds.Y);
                    bool onMonitor = false; foreach (Rectangle monitor in target.Monitors) onMonitor |= monitor.Contains(physical);
                    if (!onMonitor) clipped &= image.GetPixel(x, y).A == 0 && !shape.IsVisible(x, y);
                }
                check(clipped, "Negative coordinates and a monitor gap clip light without introducing another perimeter or opaque backing");
            }
            target.Monitors = new[] { new Rectangle(100, 100, 100, 100) };
            check(FocusIndicatorVisual.CreatePinCorner(target, false, true, "Forest Green") == null, "An entirely off-monitor upper-left cue allocates no surface");
            target = Target(); target.ShellSurface = true;
            check(FocusIndicatorVisual.CreatePinCorner(target, false, true, "Forest Green") == null, "Shell surfaces remain ineligible for the lit corner");
            target = Target(); target.Frame = new Rectangle(0, 0, 10, 10);
            check(FocusIndicatorVisual.CreatePinCorner(target, false, true, "Forest Green") == null, "Very small targets are refused instead of illuminating a second corner");
            target = Target(); target.Corner = 1000;
            check(FocusIndicatorVisual.CreatePinCorner(target, false, true, "Forest Green") == null, "An unsupported giant corner cannot expand the local cue into a broad decoration");
            target = Target(); target.Frame = new Rectangle(Int32.MinValue, 0, 800, 600); target.Monitors = new[] { target.Frame };
            check(FocusIndicatorVisual.CreatePinCorner(target, false, true, "Forest Green") == null, "Glow padding refuses coordinate underflow");
            foreach (int change in new[] { 0, 1, 2 }) {
                visual = FocusIndicatorVisual.CreatePinCorner(Target(), false, true, "Forest Green");
                if (change == 0) visual.PinRailInset = 1;
                if (change == 1) visual.GlowWidth = 32;
                if (change == 2) visual.Bounds = new Rectangle(visual.Bounds.X, visual.Bounds.Y, 800, 600);
                bool refused = false; try { using (Bitmap image = FocusIndicatorPaint.Render(visual)) { } } catch (ArgumentException) { refused = true; }
                check(refused, "Corrupt inset, halo or crop cannot reach publication " + change);
            }
        }
        // Rendered-pixel evidence only: no desktop capture or native window.
        internal static void Export(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (bool focused in new[] { false, true }) {
                var target = Target(); target.Frame = new Rectangle(20, 20, 740, 460); target.Monitors = new[] { new Rectangle(0, 0, 780, 500) };
                var focus = focused ? FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, "Forest Green", true) : null;
                var cue = FocusIndicatorVisual.CreatePinCorner(target, focused, true, "Forest Green", focus);
                using (var image = new Bitmap(780, 500, PixelFormat.Format32bppPArgb)) using (Graphics graphics = Graphics.FromImage(image)) {
                    graphics.Clear(Color.FromArgb(37, 40, 42));
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath path = WindowPath(target.Frame, target.Corner))
                    using (var fill = new SolidBrush(Color.FromArgb(29, 33, 36)))
                    using (var edge = new Pen(Color.FromArgb(79, 85, 86), 1)) { graphics.FillPath(fill, path); graphics.DrawPath(edge, path); }
                    using (var font = new Font("Segoe UI", 12)) using (var brush = new SolidBrush(Color.FromArgb(219, 225, 225))) {
                        graphics.DrawString("Project notes", font, brush, 42, 35);
                        graphics.DrawString("Neutral renderer preview", font, brush, 52, 112);
                    }
                    if (focus != null) using (Bitmap pixels = FocusIndicatorPaint.Render(focus)) graphics.DrawImageUnscaled(pixels, focus.Bounds.Location);
                    using (Bitmap pixels = FocusIndicatorPaint.Render(cue)) {
                        graphics.DrawImageUnscaled(pixels, cue.Bounds.Location);
                        pixels.Save(Path.Combine(directory, focused ? "corner-focused-alpha.png" : "corner-unfocused-alpha.png"), ImageFormat.Png);
                    }
                    image.Save(Path.Combine(directory, focused ? "corner-focused.png" : "corner-unfocused.png"), ImageFormat.Png);
                    using (var detail = new Bitmap(240, 240, PixelFormat.Format32bppPArgb)) using (Graphics zoom = Graphics.FromImage(detail)) {
                        zoom.InterpolationMode = InterpolationMode.NearestNeighbor; zoom.PixelOffsetMode = PixelOffsetMode.Half;
                        zoom.DrawImage(image, new Rectangle(0, 0, 240, 240), new Rectangle(12, 12, 60, 60), GraphicsUnit.Pixel);
                        detail.Save(Path.Combine(directory, focused ? "corner-focused-detail4x.png" : "corner-unfocused-detail4x.png"), ImageFormat.Png);
                    }
                }
            }
        }
        internal static void ExportReserved(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (int lighting in new[] { 2, 3 }) foreach (int finish in new[] { 0, 1 })
                foreach (double phase in lighting == 3 ? new[] { .85, .35 } : new[] { 0.0 }) {
                    var target = Target(); target.Frame = new Rectangle(20, 20, 740, 460); target.Monitors = new[] { new Rectangle(0, 0, 780, 500) };
                    var focus = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, "Forest Green", true, false, lighting, finish, true);
                    var cue = FocusIndicatorVisual.CreatePinCorner(target, true, true, "Forest Green", focus);
                    string name = "reserved-" + (lighting == 2 ? "illuminated" : phase > .5 ? "energy-near" : "energy-away") + (finish == 0 ? "-muted" : "-accent");
                    using (var image = new Bitmap(780, 500, PixelFormat.Format32bppPArgb)) using (Graphics graphics = Graphics.FromImage(image)) {
                        graphics.Clear(Color.FromArgb(37, 40, 42)); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        using (GraphicsPath path = WindowPath(target.Frame, target.Corner))
                        using (var fill = new SolidBrush(Color.FromArgb(29, 33, 36)))
                        using (var edge = new Pen(Color.FromArgb(79, 85, 86), 1)) { graphics.FillPath(fill, path); graphics.DrawPath(edge, path); }
                        using (var font = new Font("Segoe UI", 12)) using (var brush = new SolidBrush(Color.FromArgb(219, 225, 225))) {
                            graphics.DrawString("Project notes", font, brush, 42, 35);
                            graphics.DrawString("Neutral renderer preview", font, brush, 52, 112);
                        }
                        // Both layers are the actual product rendering path,
                        // including the ReservedPinCorner cache and pixel mask.
                        using (Bitmap pixels = FocusIndicatorPaint.Render(focus, 1, phase, 0)) graphics.DrawImageUnscaled(pixels, focus.Bounds.Location);
                        using (Bitmap pixels = FocusIndicatorPaint.Render(cue)) graphics.DrawImageUnscaled(pixels, cue.Bounds.Location);
                        image.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
                        using (var detail = new Bitmap(240, 240, PixelFormat.Format32bppPArgb)) using (Graphics zoom = Graphics.FromImage(detail)) {
                            zoom.InterpolationMode = InterpolationMode.NearestNeighbor; zoom.PixelOffsetMode = PixelOffsetMode.Half;
                            zoom.DrawImage(image, new Rectangle(0, 0, 240, 240), new Rectangle(12, 12, 60, 60), GraphicsUnit.Pixel);
                            detail.Save(Path.Combine(directory, name + "-detail4x.png"), ImageFormat.Png);
                        }
                    }
                }
        }
        static GraphicsPath WindowPath(Rectangle frame, int radius)
        {
            var path = new GraphicsPath(); int size = radius * 2;
            path.AddArc(frame.Left, frame.Top, size, size, 180, 90); path.AddArc(frame.Right - size, frame.Top, size, size, 270, 90);
            path.AddArc(frame.Right - size, frame.Bottom - size, size, size, 0, 90); path.AddArc(frame.Left, frame.Bottom - size, size, size, 90, 90);
            path.CloseFigure(); return path;
        }
    }
}
