using System;
using System.Drawing;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class FocusIndicatorGlowIntegrationTests
    {
        sealed class Surface : IFocusIndicatorSurface
        {
            internal FocusIndicatorVisual Last;
            internal int Disposals;
            public IntPtr Window { get { return new IntPtr(700); } }
            public void Present(FocusIndicatorVisual visual) { Last = visual; }
            public void Dispose() { Disposals++; }
        }
        sealed class Events : IFocusIndicatorEvents { public void Dispose() { } }
        sealed class OwnedTarget : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }
        static FocusIndicatorTarget Target()
        {
            return new FocusIndicatorTarget {
                Identity = new FocusedWindowSnapshot { Window = new IntPtr(42), Root = new IntPtr(42), Process = 7,
                    Thread = 8, ProcessStarted = 99, Visible = true, ClassName = "Neutral glow fixture" },
                Frame = new Rectangle(100, 100, 320, 220), Monitors = new[] { new Rectangle(0, 0, 1920, 1080) }, Dpi = 96, Corner = 8
            };
        }
        internal static void Run(Action<bool, string> check)
        {
            BoundsAndFallback(check); RuntimePolicy(check); NativePublication(check);
        }
        static void BoundsAndFallback(Action<bool, string> check)
        {
            var target = Target();
            foreach (int dpi in new[] { 96, 120, 144, 168, 192, 288, 384, 768 }) {
                target.Dpi = dpi;
                var glow = FocusIndicatorVisual.Create(target, FocusIndicatorMode.MutedFrame, "Coral", true, true);
                var normal = FocusIndicatorVisual.Create(target, FocusIndicatorMode.MutedFrame, "Coral", true);
                int expected = Math.Min(32, (7 * dpi + 48) / 96);
                check(glow.GlowWidth == expected && glow.Bounds == FocusIndicatorVisual.Expand(target.Frame, expected) &&
                    glow.GlowColor.A == 90 && normal.GlowWidth == 0 && normal.Bounds == target.Frame &&
                    normal.Thickness == glow.Thickness && !normal.SameAppearance(glow),
                    "Soft glow expands only its own surface, scales to seven logical pixels with a 32-pixel cap and invalidates the appearance cache at DPI " + dpi);
            }
            var light = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.InsetEdge, "Mint", false, true);
            check(light.GlowColor.A == 72, "Light appearance retains a clearly visible but lower-alpha soft halo");
            var large = Target(); large.Frame = new Rectangle(0, 0, 4096, 4096);
            large.Monitors = new[] { new Rectangle(-100, -100, 6000, 6000) };
            var fallback = FocusIndicatorVisual.Create(large, FocusIndicatorMode.MutedFrame, "Coral", true, true);
            check(fallback != null && fallback.GlowWidth == 0 && fallback.Bounds == large.Frame,
                "Glow exceeding the pixel allocation cap falls back to a usable normal frame");
            var extreme = Target(); extreme.Frame = new Rectangle(Int32.MinValue + 2, Int32.MinValue + 2, 100, 100);
            extreme.Monitors = new[] { extreme.Frame };
            var bounded = FocusIndicatorVisual.Create(extreme, FocusIndicatorMode.MutedFrame, "Original", true, true);
            check(bounded != null && bounded.GlowWidth == 0 && bounded.Bounds == extreme.Frame &&
                FocusIndicatorVisual.Expand(extreme.Frame, 4).IsEmpty && FocusIndicatorVisual.Expand(Target().Frame, 49).IsEmpty,
                "Expansion overflow/oversized padding is rejected before allocation and keeps a safe normal-frame fallback");
            var clipped = Target(); clipped.Frame = new Rectangle(-2, -2, 320, 220);
            var edge = FocusIndicatorVisual.Create(clipped, FocusIndicatorMode.MutedFrame, "Sky", true, true);
            check(edge.Bounds == new Rectangle(0, 0, 325, 225) && edge.Target.Frame == clipped.Frame,
                "Glow clips at the real monitor edge while retaining original window geometry");
        }
        static void RuntimePolicy(Action<bool, string> check)
        {
            var target = Target(); bool allowed = true; int reads = 0;
            var surface = new Surface();
            var api = new FocusIndicatorApi {
                Foreground = delegate { return target.Identity.Window; }, Read = delegate { return target; },
                DesktopAllowed = delegate { return true; }, Dark = delegate { return true; },
                GlowAllowed = delegate { reads++; return allowed; }, PointerButtonDown = delegate { return false; },
                Watch = delegate { return new Events(); }
            };
            using (var indicator = new FocusIndicator(api, delegate { return surface; })) {
                indicator.Update(1, "Coral", false);
                check(surface.Last.GlowWidth == 0 && reads == 0, "Default-disabled glow does not read optional accessibility policy");
                indicator.Update(1, "Coral", false, true);
                check(surface.Last.GlowWidth == 7 && reads == 1 && indicator.Visible,
                    "The production manager carries the optional glow into its verified visual");
                allowed = false; indicator.Update(1, "Coral", false, true);
                check(surface.Last.GlowWidth == 0 && indicator.Visible && surface.Disposals == 0,
                    "High-contrast policy removes only the decorative halo while retaining the focus frame");
                allowed = true; indicator.Update(2, "Sky", false, true);
                check(surface.Last.GlowWidth == 7 && surface.Last.Mode == FocusIndicatorMode.InsetEdge,
                    "An unchanged saved glow choice resumes with the current style/color after policy allows it");
                indicator.Update(0, "Sky", false, true);
                check(!indicator.Visible && surface.Disposals == 1, "Focus style Off removes both frame and halo immediately");
            }
        }
        static void NativePublication(Action<bool, string> check)
        {
            IntPtr foreground = FocusedWindowNative.Foreground(); Point pointer = Cursor.Position;
            using (var target = new OwnedTarget { Text = "Neutral native glow fixture", Opacity = 0, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(70, 70, 320, 220) }) {
                target.Show(); var native = FocusIndicatorNative.Read(target.Handle);
                if (native == null) throw new InvalidOperationException("Owned glow fixture unavailable.");
                var glow = FocusIndicatorVisual.Create(native, FocusIndicatorMode.MutedFrame, "Coral", true, true);
                var normal = FocusIndicatorVisual.Create(native, FocusIndicatorMode.MutedFrame, "Coral", true);
                long clock = 0; int uploads = 0; IntPtr window = IntPtr.Zero; bool hollow = true, firstHidden = false;
                using (var surface = new FocusIndicatorSurface(false, delegate(IntPtr value, Bitmap bitmap, Point position) {
                    window = value; uploads++;
                    if (uploads == 1) firstHidden = !Native.IsWindowVisible(value);
                    hollow &= bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).ToArgb() == 0;
                    return ShelfPaint.PresentLayered(value, bitmap, position);
                }, delegate { return true; }, delegate { return clock; }, delegate { return true; })) {
                    surface.Present(glow); Region retained = surface.Region;
                    clock = 60; surface.AdvanceAnimation();
                    check(firstHidden && hollow && uploads == 2 && surface.AnimationRunning && surface.RevealProgress > 0 &&
                        Object.ReferenceEquals(retained, surface.Region) && surface.Pixels.Size == glow.Bounds.Size,
                        "Native glow reveal uploads before show, keeps its hollow client and retains its expanded shape");
                    clock = FocusIndicatorMotion.TotalMilliseconds; surface.Present(glow);
                    check(surface.RevealProgress == 1 && !surface.AnimationRunning && hollow,
                        "Native glow reveal commits its completed frame even when the verifier wins the timer race");
                    surface.Present(normal);
                    check(surface.Pixels.Size == normal.Bounds.Size && surface.RevealProgress == 1 && !surface.AnimationRunning && hollow,
                        "Switching glow off shrinks only the owned surface and does not restart motion or fill the client");
                    surface.Present(glow);
                    check(surface.Pixels.Size == glow.Bounds.Size && hollow && surface.RevealProgress == 1,
                        "Repeated glow toggles rebuild the owned shape without a visibility transition");
                    surface.Dispose(); surface.AdvanceAnimation();
                    check(!Native.IsWindow(window) && surface.Pixels == null && surface.Region == null && !surface.AnimationRunning,
                        "Expanded glow image and shape are freed after HWND destruction; late ticks cannot revive them");
                }
                check(FocusedWindowPolicy.SameWindow(native.Identity, FocusedWindowNative.Read(target.Handle)) &&
                    FocusIndicatorNative.MatchesFrame(native) && FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                    "Native glow changes preserve target geometry/identity, actual focus and mouse position");
            }
            check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                "Glow fixture cleanup preserves the user's desktop focus and pointer");
        }
    }
}
