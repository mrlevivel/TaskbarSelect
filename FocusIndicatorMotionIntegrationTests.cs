using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class FocusIndicatorMotionIntegrationTests
    {
        sealed class OwnedTarget : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }

        internal static void Run(Action<bool, string> check)
        {
            CornerPolicy(check);
            IntPtr foreground = FocusedWindowNative.Foreground(); Point pointer = Cursor.Position;
            using (var target = new OwnedTarget { Text = "Neutral focus motion fixture", Opacity = 0, ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(60, 60, 320, 220) }) {
                target.Show();
                var identity = FocusedWindowNative.Read(target.Handle);
                int style = FocusIndicatorSurface.ExtendedStyle(target.Handle);
                Rectangle bounds = target.Bounds;
                ProgressAndCompletion(target, check);
                RecolorAndReducedMotion(target, check);
                StopsAndFailures(target, check);
                Native.Rect after; Native.GetWindowRect(target.Handle, out after);
                check(FocusedWindowPolicy.SameWindow(identity, FocusedWindowNative.Read(target.Handle)) &&
                    FocusIndicatorSurface.ExtendedStyle(target.Handle) == style && (style & 0x08000000) != 0 &&
                    after.Left == bounds.Left && after.Top == bounds.Top && after.Right == bounds.Right && after.Bottom == bounds.Bottom &&
                    !FocusIndicatorNative.IsIconic(target.Handle) && FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                    "Motion integration preserves the owned target identity/style/bounds and actual foreground/pointer");
            }
            check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                "Motion integration fixture cleanup preserves desktop focus and pointer");
        }

        static FocusIndicatorVisual Visual(OwnedTarget target, FocusIndicatorMode mode = FocusIndicatorMode.MutedFrame,
            string accent = "Original", bool dark = true)
        {
            var frame = FocusIndicatorNative.Read(target.Handle);
            if (frame == null) throw new InvalidOperationException("Owned motion target unavailable.");
            var visual = FocusIndicatorVisual.Create(frame, mode, accent, dark);
            if (visual == null) throw new InvalidOperationException("Owned motion geometry unavailable.");
            return visual;
        }

        static int PaintedPixels(Bitmap image)
        {
            int painted = 0;
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try {
                var row = new byte[image.Width * 4];
                for (int y = 0; y < image.Height; y++) {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < image.Width; x++) if (row[x * 4 + 3] != 0) painted++;
                }
            } finally { image.UnlockBits(data); }
            return painted;
        }
        static bool CenterTransparent(Bitmap image)
        { return image != null && image.GetPixel(image.Width / 2, image.Height / 2).ToArgb() == 0; }
        static bool SamePixels(Bitmap first, Bitmap second)
        {
            if (first == null || second == null || first.Size != second.Size) return false;
            Rectangle bounds = new Rectangle(Point.Empty, first.Size);
            var left = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var right = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try {
                var leftRow = new int[first.Width]; var rightRow = new int[first.Width];
                for (int y = 0; y < first.Height; y++) {
                    Marshal.Copy(IntPtr.Add(left.Scan0, y * left.Stride), leftRow, 0, leftRow.Length);
                    Marshal.Copy(IntPtr.Add(right.Scan0, y * right.Stride), rightRow, 0, rightRow.Length);
                    for (int x = 0; x < leftRow.Length; x++) if (leftRow[x] != rightRow[x]) return false;
                }
                return true;
            } finally { second.UnlockBits(right); first.UnlockBits(left); }
        }
        static bool Clean(FocusIndicatorSurface surface, IntPtr window)
        {
            return !Native.IsWindow(window) && surface.Window == IntPtr.Zero && !surface.AnimationRunning &&
                surface.Pixels == null && surface.Region == null && !surface.Uploaded;
        }

        static void ProgressAndCompletion(OwnedTarget target, Action<bool, string> check)
        {
            long clock = 0; int uploads = 0; bool firstHidden = false, centers = true; IntPtr window = IntPtr.Zero;
            var visual = Visual(target);
            int full;
            using (Bitmap image = FocusIndicatorSurface.Render(visual)) full = PaintedPixels(image);
            using (var surface = new FocusIndicatorSurface(false, delegate(IntPtr value, Bitmap image, Point location) {
                window = value; uploads++;
                if (uploads == 1) firstHidden = !Native.IsWindowVisible(value) && PaintedPixels(image) == 0;
                centers &= CenterTransparent(image);
                return ShelfPaint.PresentLayered(value, image, location);
            }, delegate { return true; }, delegate { return clock; }, delegate { return true; })) {
                surface.Present(visual);
                Region region = surface.Region;
                check(firstHidden && centers && uploads == 1 && surface.RevealProgress == 0 && surface.RevealStrength == 1 &&
                    surface.AnimationRunning && surface.AnimationInterval == 16 && Native.IsWindowVisible(window),
                    "Animated first upload commits an entirely transparent production image while its owned HWND is hidden");
                clock = 30; surface.AdvanceAnimation();
                int partial = PaintedPixels(surface.Pixels);
                check(uploads == 2 && surface.RevealProgress > 0 && surface.RevealProgress < 1 && partial > 0 && partial < full && centers &&
                    Object.ReferenceEquals(region, surface.Region),
                    "A live tick reveals only part of the border, retains its native shape and keeps the client center transparent");

                clock = FocusIndicatorMotion.RevealMilliseconds; surface.Present(visual);
                check(uploads == 3 && surface.RevealProgress == 1 && surface.RevealStrength == 1 && surface.AnimationRunning &&
                    surface.AnimationInterval == 16 && PaintedPixels(surface.Pixels) == full && centers &&
                    Object.ReferenceEquals(region, surface.Region),
                    "Same-appearance Present at 420ms completes the hollow border while preserving its highlight settling timer");
                using (Bitmap bright = surface.Pixels.Clone(new Rectangle(Point.Empty, surface.Pixels.Size), PixelFormat.Format32bppPArgb)) {
                    clock = 510; surface.AdvanceAnimation();
                    using (Bitmap expected = FocusIndicatorPaint.Render(visual, 1, 0, .5))
                        check(uploads == 4 && surface.RevealProgress == 1 && surface.RevealStrength == .5 && surface.AnimationRunning &&
                            surface.AnimationInterval == 16 && !SamePixels(bright, surface.Pixels) && SamePixels(expected, surface.Pixels) && centers &&
                            Object.ReferenceEquals(region, surface.Region),
                            "Settling strength changes upload the correct fading pixels even though complete reveal progress stays one");
                }
                // The normal 80ms verifier can win the final-tick race. Present
                // must publish the settled image even though appearance and
                // progress are unchanged and only highlight strength reached zero.
                clock = FocusIndicatorMotion.TotalMilliseconds; surface.Present(visual);
                using (Bitmap expected = FocusIndicatorSurface.Render(visual))
                    check(uploads == 5 && surface.RevealProgress == 1 && surface.RevealStrength == 0 && !surface.AnimationRunning &&
                        PaintedPixels(surface.Pixels) == full && SamePixels(expected, surface.Pixels) && centers && Object.ReferenceEquals(region, surface.Region),
                        "Same-appearance Present at 600ms commits exactly the static border and stops its reveal timer");
                Bitmap retained = surface.Pixels;
                clock = 1000;
                for (int i = 0; i < 10; i++) { surface.Present(visual); surface.AdvanceAnimation(); }
                check(uploads == 5 && surface.RevealStrength == 0 && Object.ReferenceEquals(retained, surface.Pixels) && Object.ReferenceEquals(region, surface.Region) && !surface.AnimationRunning,
                    "Completed idle verifier/timer callbacks reuse the committed bitmap and region without another upload");
                surface.Dispose(); surface.AdvanceAnimation();
                check(Clean(surface, window) && uploads == 5, "Disposal cancels motion and late ticks cannot recreate or upload a border");
            }
        }

        static void RecolorAndReducedMotion(OwnedTarget target, Action<bool, string> check)
        {
            long clock = 0; bool allowed = true; int uploads = 0;
            var first = Visual(target);
            using (var surface = new FocusIndicatorSurface(false, delegate(IntPtr window, Bitmap image, Point location) {
                uploads++; return ShelfPaint.PresentLayered(window, image, location);
            }, delegate { return allowed; }, delegate { return clock; }, delegate { return true; })) {
                surface.Present(first); clock = 45; surface.AdvanceAnimation();
                double progress = surface.RevealProgress, strength = surface.RevealStrength;
                var changed = Visual(target, FocusIndicatorMode.InsetEdge, "Coral", false);
                surface.Present(changed);
                check(uploads == 3 && surface.RevealProgress == progress && surface.RevealStrength == strength && surface.AnimationRunning && CenterTransparent(surface.Pixels),
                    "Style/theme/accent replacement preserves the current reveal instead of restarting it");
                Region region = surface.Region;
                clock = 90; surface.AdvanceAnimation();
                check(surface.RevealProgress > progress && surface.RevealProgress < 1 && Object.ReferenceEquals(region, surface.Region),
                    "Recolored motion continues on its original timeline while retaining the shape");
                allowed = false; clock = 100; surface.AdvanceAnimation();
                int full;
                using (Bitmap image = FocusIndicatorSurface.Render(changed)) full = PaintedPixels(image);
                check(surface.RevealProgress == 1 && surface.RevealStrength == 0 && !surface.AnimationRunning && PaintedPixels(surface.Pixels) == full && CenterTransparent(surface.Pixels),
                    "Disabling animation preference during a reveal commits the complete static border immediately");
                int completedUploads = uploads; Bitmap retained = surface.Pixels;
                allowed = true; clock = 1000; surface.Present(changed); surface.AdvanceAnimation();
                check(uploads == completedUploads && surface.RevealStrength == 0 && Object.ReferenceEquals(retained, surface.Pixels) && !surface.AnimationRunning,
                    "Re-enabling animations leaves the already shown completed border static");
            }
            using (var reduced = new FocusIndicatorSurface(false, animationsAllowed: delegate { return false; },
                    now: delegate { return clock; }, current: delegate { return true; })) {
                reduced.Present(first);
                check(reduced.RevealProgress == 1 && reduced.RevealStrength == 0 && !reduced.AnimationRunning && PaintedPixels(reduced.Pixels) > 0 && CenterTransparent(reduced.Pixels),
                    "Reduced-animation policy shows a complete hollow border without starting a live timer");
            }
            clock = 0; allowed = true; uploads = 0;
            using (var surface = new FocusIndicatorSurface(false, delegate(IntPtr window, Bitmap image, Point location) {
                uploads++; return ShelfPaint.PresentLayered(window, image, location);
            }, delegate { return allowed; }, delegate { return clock; }, delegate { return true; })) {
                surface.Present(first); clock = 510; surface.AdvanceAnimation();
                check(surface.RevealProgress == 1 && surface.RevealStrength == .5 && surface.AnimationRunning,
                    "The owned reduced-motion fixture first reaches the complete border with a live settling highlight");
                int settlingUploads = uploads; allowed = false; clock = 512; surface.AdvanceAnimation();
                using (Bitmap expected = FocusIndicatorSurface.Render(first))
                    check(uploads == settlingUploads + 1 && surface.RevealProgress == 1 && surface.RevealStrength == 0 &&
                        !surface.AnimationRunning && SamePixels(expected, surface.Pixels) && CenterTransparent(surface.Pixels),
                        "Reduced motion during settling immediately uploads the static border although progress has not changed");
            }
        }

        static void StopsAndFailures(OwnedTarget target, Action<bool, string> check)
        {
            foreach (long stoppedAt in new[] { 30L, 510L })
            foreach (string reason in new[] { "not current", "current changed before upload", "current changed after upload", "geometry changed", "upload failure" }) {
                long clock = 0; int uploads = 0, currentReads = 0, stops = 0; bool allowedCurrent = true;
                IntPtr window = IntPtr.Zero; IFocusIndicatorSurface stoppedSource = null; Exception stoppedFailure = null;
                Rectangle startingBounds = target.Bounds;
                try {
                    using (var surface = new FocusIndicatorSurface(false, delegate(IntPtr value, Bitmap image, Point location) {
                        window = value; uploads++;
                        if (reason == "upload failure" && uploads == 2) return false;
                        return ShelfPaint.PresentLayered(value, image, location);
                    }, delegate { return true; }, delegate { return clock; }, delegate {
                        currentReads++;
                        if (reason == "current changed before upload") return currentReads < 2;
                        if (reason == "current changed after upload") return currentReads < 3;
                        return allowedCurrent;
                    }, delegate(IFocusIndicatorSurface source, Exception failure) {
                        stops++; stoppedSource = source; stoppedFailure = failure;
                        if (reason == "upload failure") throw new InvalidOperationException("Owned callback failure fixture");
                    })) {
                        surface.Present(Visual(target));
                        clock = stoppedAt;
                        if (reason == "not current") allowedCurrent = false;
                        if (reason == "geometry changed") target.Bounds = new Rectangle(startingBounds.X + 15, startingBounds.Y + 10,
                            startingBounds.Width, startingBounds.Height);
                        surface.AdvanceAnimation();
                        int expectedUploads = reason == "upload failure" || reason == "current changed after upload" ? 2 : 1;
                        bool failureCorrect = reason == "geometry changed" ? stoppedFailure is FocusIndicatorGeometryChangedException :
                            reason == "upload failure" ? stoppedFailure is InvalidOperationException : stoppedFailure == null;
                        check(stops == 1 && Object.ReferenceEquals(stoppedSource, surface) && failureCorrect && uploads == expectedUploads && Clean(surface, window),
                            "Animation safely stops, reports and disposes its owned surface on " + reason + " during " + (stoppedAt < 420 ? "reveal" : "settling"));
                        clock = FocusIndicatorMotion.TotalMilliseconds; surface.AdvanceAnimation(); surface.Dispose();
                        check(stops == 1 && uploads == expectedUploads && Clean(surface, window),
                            "Stopped animation ignores late ticks/repeated cleanup on " + reason);
                    }
                } finally { target.Bounds = startingBounds; }
            }

            long reentrantClock = 0; int reentrantUploads = 0, reentrantStops = 0; IntPtr reentrantWindow = IntPtr.Zero;
            FocusIndicatorSurface reentrant = null;
            reentrant = new FocusIndicatorSurface(false, delegate(IntPtr window, Bitmap image, Point location) {
                reentrantWindow = window; reentrantUploads++;
                if (reentrantUploads == 2) { reentrant.Dispose(); return true; }
                return ShelfPaint.PresentLayered(window, image, location);
            }, delegate { return true; }, delegate { return reentrantClock; }, delegate { return true; }, delegate { reentrantStops++; });
            using (reentrant) {
                reentrant.Present(Visual(target)); reentrantClock = 30; reentrant.AdvanceAnimation();
                check(reentrantUploads == 2 && reentrantStops == 0 && Clean(reentrant, reentrantWindow),
                    "Reentrant disposal during a timer upload cannot publish freed pixels or run a stopped callback for intentional cleanup");
                reentrantClock = FocusIndicatorMotion.TotalMilliseconds; reentrant.AdvanceAnimation();
                check(reentrantUploads == 2 && Clean(reentrant, reentrantWindow), "A reentrantly disposed timer cannot upload its final frame afterward");
            }
        }

        static void CornerPolicy(Action<bool, string> check)
        {
            int[] dpi = { 96, 120, 144, 168, 192, 240, 288, 768 };
            int[] regular = { 8, 10, 12, 14, 16, 20, 24, 64 }, small = { 4, 5, 6, 7, 8, 10, 12, 32 };
            for (int i = 0; i < dpi.Length; i++) {
                check(FocusIndicatorNative.CornerRadius(dpi[i], 0, false, false) == regular[i] &&
                    FocusIndicatorNative.CornerRadius(dpi[i], 2, false, false) == regular[i] &&
                    FocusIndicatorNative.CornerRadius(dpi[i], 3, false, false) == small[i],
                    "Default/round/small-round corner preferences scale in physical pixels at DPI " + dpi[i]);
                foreach (int preference in new[] { 0, 1, 2, 3 })
                    check(FocusIndicatorNative.CornerRadius(dpi[i], preference, true, false) == 0 &&
                        FocusIndicatorNative.CornerRadius(dpi[i], preference, false, true) == 0,
                        "Maximized or arranged windows always keep square corners at DPI " + dpi[i] + "/preference " + preference);
                check(FocusIndicatorNative.CornerRadius(dpi[i], 1, false, false) == 0,
                    "The explicit do-not-round preference remains square at DPI " + dpi[i]);
            }
            check(FocusIndicatorNative.CornerRadius(0, 2, false, false) == 8 &&
                FocusIndicatorNative.CornerRadius(Int32.MaxValue, 2, false, false) == 64,
                "Invalid DPI inputs are bounded before deriving a physical corner radius");
        }
    }
}
