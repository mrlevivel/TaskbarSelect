using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class FocusFrameFinishNativeTests
    {
        [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr window, IntPtr region);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        sealed class OwnedTarget : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }
        static int[] Pixels(Bitmap image)
        {
            var values = new int[image.Width * image.Height];
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < image.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), values, y * image.Width, image.Width); }
            finally { image.UnlockBits(data); }
            return values;
        }
        static bool Same(Bitmap first, Bitmap second)
        {
            if (first.Size != second.Size) return false;
            int[] a = Pixels(first), b = Pixels(second);
            for (int index = 0; index < a.Length; index++) if (a[index] != b[index]) return false;
            return true;
        }
        static bool VisibleMovement(int[] prior, Bitmap image)
        {
            int[] current = Pixels(image); int coreLight = 0, haloAlpha = 0; bool opaque = true;
            for (int index = 0; index < current.Length; index++) {
                int a = (int)((uint)prior[index] >> 24), b = (int)((uint)current[index] >> 24);
                if (a == 255) {
                    opaque &= b == 255;
                    int first = (((prior[index] >> 16) & 255) * 3 + ((prior[index] >> 8) & 255) * 6 + (prior[index] & 255)) / 10;
                    int second = (((current[index] >> 16) & 255) * 3 + ((current[index] >> 8) & 255) * 6 + (current[index] & 255)) / 10;
                    coreLight = Math.Max(coreLight, Math.Abs(first - second));
                } else haloAlpha = Math.Max(haloAlpha, Math.Abs(a - b));
            }
            return opaque && coreLight >= 20 && haloAlpha >= 30;
        }
        static string StagedMismatch(FocusIndicatorSurface.LayeredUpload staging, Bitmap image)
        {
            int[] pixels = Pixels(image);
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                if (staging.Pixel(x, y) != pixels[y * image.Width + x])
                    return "first pixel " + x + "," + y + " expected0x" + pixels[y * image.Width + x].ToString("X8") +
                        " actual0x" + staging.Pixel(x, y).ToString("X8");
            return null;
        }
        static bool HollowNative(FocusIndicatorSurface surface)
        {
            Bitmap image = surface.Pixels;
            if (image == null || image.GetPixel(image.Width / 2, image.Height / 2).ToArgb() != 0) return false;
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            if (region == IntPtr.Zero) throw new InvalidOperationException("Owned accent fixture region unavailable.");
            try {
                if (GetWindowRgn(surface.Window, region) == 0 || PtInRegion(region, image.Width / 2, image.Height / 2)) return false;
                int[] pixels = Pixels(image);
                for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                    if (((uint)pixels[y * image.Width + x] >> 24) > 0 && !PtInRegion(region, x, y)) return false;
                return true;
            } finally { DeleteObject(region); }
        }
        static bool Released(Bitmap image)
        { try { image.GetPixel(0, 0); return false; } catch (ArgumentException) { return true; } }
        static bool Clean(FocusIndicatorSurface surface, IntPtr window)
        {
            return !Native.IsWindow(window) && surface.Window == IntPtr.Zero && surface.Pixels == null && surface.Region == null &&
                surface.LightingCache == null && !surface.HasStaging && !surface.Uploaded && !surface.AnimationRunning && !surface.EnergyRunning;
        }
        static FocusIndicatorVisual Visual(FocusIndicatorTarget target, int lighting, int finish)
        { return FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, "Forest Green", true, false, lighting, finish); }
        internal static void Run(Action<bool, string> check)
        {
            Staging(check);
            IntPtr foreground = FocusedWindowNative.Foreground(); Point pointer = Cursor.Position;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            var bounds = new Rectangle(work.Left + Math.Min(80, Math.Max(0, work.Width - 360)),
                work.Top + Math.Min(80, Math.Max(0, work.Height - 260)), 320, 220);
            // This fixture owns only a transparent, nonactivating target. No
            // screen pixels or unrelated windows are captured or manipulated.
            using (var target = new OwnedTarget { Text = "Neutral native accent-outline fixture", Opacity = 0,
                ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = bounds }) {
                target.Show(); var captured = FocusIndicatorNative.Read(target.Handle);
                if (captured == null) throw new InvalidOperationException("Owned native accent-outline target unavailable.");
                int style = FocusIndicatorSurface.ExtendedStyle(target.Handle);
                Publication(captured, check); Replacement(captured, check);
                bool geometryUnchanged = FocusIndicatorNative.MatchesFrame(captured);
                bool identityUnchanged = FocusedWindowPolicy.SameWindow(captured.Identity, FocusedWindowNative.Read(target.Handle));
                bool styleUnchanged = style == FocusIndicatorSurface.ExtendedStyle(target.Handle) && (style & 0x08000000) != 0;
                bool focusUnchanged = FocusedWindowNative.Foreground() == foreground, pointerUnchanged = Cursor.Position == pointer;
                check(geometryUnchanged && identityUnchanged && styleUnchanged && focusUnchanged && pointerUnchanged,
                    "Native accent fixtures preserve target identity, geometry, noactivate style, actual focus and pointer" +
                    "; identity=" + identityUnchanged + "; geometry=" + geometryUnchanged + "; style=" + styleUnchanged +
                    "; focus=" + focusUnchanged + "; pointer=" + pointerUnchanged);
            }
            check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                "Accent native fixture cleanup leaves desktop focus and pointer unchanged");
        }
        static void Staging(Action<bool, string> check)
        {
            using (var staging = new FocusIndicatorSurface.LayeredUpload()) {
                Size previousSize = Size.Empty;
                foreach (int dpi in new[] { 96, 144 }) {
                    var target = new FocusIndicatorTarget { Frame = new Rectangle(30, 30, 140, 100), Dpi = dpi,
                        Corner = dpi == 96 ? 0 : 12, Monitors = new[] { new Rectangle(0, 0, 600, 400) } };
                    for (int lighting = 0; lighting <= 3; lighting++) {
                        foreach (int finish in new[] { 0, 1, 0 }) {
                            var visual = Visual(target, lighting, finish);
                            using (Bitmap image = FocusIndicatorPaint.Render(visual, 1, .37)) {
                                int before = staging.Allocations, expected = before + (previousSize == image.Size ? 0 : 1);
                                bool staged = staging.Stage(image, visual);
                                string context = dpi + "/" + lighting + "/" + finish + "; bounds=" + visual.Bounds + "; previous size=" + previousSize +
                                    "; image size=" + image.Size + "; ready=" + staging.Ready + "; allocations=" + staging.Allocations;
                                check(staged, "Native DIB staging succeeds through finish replacement at " + context);
                                string mismatch = StagedMismatch(staging, image);
                                check(mismatch == null, "Native DIB readback exactly matches all pixels without stale inner-edge pixels at " + context +
                                    (mismatch == null ? "" : "; " + mismatch));
                                check(staging.Allocations == expected, "Native DIB reuses an equal-sized buffer and allocates once for changed halo dimensions at " + context +
                                    "; expected allocations=" + expected);
                                previousSize = image.Size;
                            }
                        }
                    }
                }
            }
        }
        static void Publication(FocusIndicatorTarget target, Action<bool, string> check)
        {
            for (int lighting = 0; lighting <= 3; lighting++) {
                long clock = 0; int uploads = 0; bool firstHidden = false, hollow = true; IntPtr window = IntPtr.Zero;
                using (var surface = new FocusIndicatorSurface(false, delegate(IntPtr value, Bitmap image, Point location) {
                    window = value; uploads++;
                    if (uploads == 1) firstHidden = !Native.IsWindowVisible(value);
                    hollow &= image.GetPixel(image.Width / 2, image.Height / 2).ToArgb() == 0;
                    return ShelfPaint.PresentLayered(value, image, location);
                }, delegate { return true; }, delegate { return clock; }, delegate { return true; })) {
                    var visual = Visual(target, lighting, 1); surface.Present(visual);
                    int required = 0x08000000 | 0x20 | 0x80 | 0x80000;
                    check(firstHidden && uploads == 1 && hollow && Native.IsWindowVisible(window) && surface.Uploaded &&
                        (FocusIndicatorSurface.ExtendedStyle(window) & required) == required && surface.RevealProgress == 0,
                        "One-DIP native accent publication uploads before show and preserves layered click-through noactivate styles at lighting " + lighting);
                    clock = FocusIndicatorMotion.TotalMilliseconds; surface.AdvanceAnimation();
                    using (Bitmap expected = FocusIndicatorPaint.Render(visual, 1, surface.EnergyPhase))
                        check(surface.RevealProgress == 1 && hollow && HollowNative(surface) && Same(surface.Pixels, expected) &&
                            visual.Thickness == Math.Max(1, (target.Dpi + 48) / 96),
                            "Settled accent surface retains every rounded stroke pixel, a hollow HWND and the exact one-DIP rendering at lighting " + lighting);
                    Bitmap retained = surface.Pixels; surface.Dispose(); surface.AdvanceAnimation();
                    check(Clean(surface, window) && Released(retained), "Native accent disposal destroys its HWND and releases alpha pixels/region/timer at lighting " + lighting);
                }
            }
        }
        static void Replacement(FocusIndicatorTarget target, Action<bool, string> check)
        {
            long clock = 0; bool allowed = true;
            using (var surface = new FocusIndicatorSurface(false, animationsAllowed: delegate { return allowed; },
                now: delegate { return clock; }, current: delegate { return true; })) {
                var muted = Visual(target, 3, 0); surface.Present(muted); IntPtr window = surface.Window;
                clock = FocusIndicatorMotion.TotalMilliseconds; surface.AdvanceAnimation(); clock = 1500; surface.AdvanceAnimation();
                check(surface.HasStaging && surface.RevealProgress == 1 && surface.EnergyPhase == .25 && surface.EnergyRunning,
                    "Production native premium path owns its DIB staging before switching to the thinner accent finish");
                foreach (int finish in new[] { 1, 0, 1 }) {
                    Bitmap previous = surface.Pixels; var priorCache = surface.LightingCache;
                    var visual = Visual(target, 3, finish); surface.Present(visual);
                    using (Bitmap expected = FocusIndicatorPaint.Render(visual, 1, .25))
                        check(surface.Window == window && surface.HasStaging && surface.EnergyPhase == .25 && surface.EnergyRunning &&
                            surface.RevealProgress == 1 && Released(previous) && !Object.ReferenceEquals(priorCache, surface.LightingCache) &&
                            HollowNative(surface) && Same(surface.Pixels, expected),
                            "Production finish replacement updates native shape/cache without replaying reveal, retaining stale pixels or resetting flow at finish " + finish);
                }
                Bitmap retained = surface.Pixels; int[] beforeMovement = Pixels(retained); clock = 3000; surface.AdvanceAnimation();
                check(Object.ReferenceEquals(retained, surface.Pixels) && surface.EnergyPhase == .5 && surface.EnergyRunning && HollowNative(surface) &&
                    VisibleMovement(beforeMovement, surface.Pixels),
                    "Native accent energy advances a visible light and local bloom while keeping the core opaque and bitmap/geometry retained");
                allowed = false; clock = 4500; surface.AdvanceAnimation();
                var reduced = Visual(target, 3, 1);
                using (Bitmap expected = FocusIndicatorPaint.Render(reduced, 1, 0))
                    check(surface.RevealProgress == 1 && surface.EnergyPhase == 0 && !surface.AnimationRunning && !surface.EnergyRunning &&
                        HollowNative(surface) && Same(surface.Pixels, expected),
                        "Reduced motion commits the complete accent frame at phase zero and stops native animation");
                retained = surface.Pixels; surface.Dispose(); surface.AdvanceAnimation();
                check(Clean(surface, window) && Released(retained), "Production accent staging/cache/shape/HWND are freed together and late ticks remain inert");
            }
        }
    }
}
