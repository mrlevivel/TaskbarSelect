using System;
using System.Drawing;

namespace TaskbarSelect
{
    internal static class FocusLightingPolicyTests
    {
        sealed class Surface : IFocusIndicatorSurface
        {
            internal FocusIndicatorVisual Last;
            internal int Disposals;
            public IntPtr Window { get { return new IntPtr(701); } }
            public void Present(FocusIndicatorVisual value) { Last = value; }
            public void Dispose() { Disposals++; }
        }
        sealed class Events : IFocusIndicatorEvents { public void Dispose() { } }
        static FocusIndicatorTarget Target()
        {
            return new FocusIndicatorTarget {
                Identity = new FocusedWindowSnapshot { Window = new IntPtr(42), Root = new IntPtr(42), Process = 7,
                    Thread = 8, ProcessStarted = 99, Visible = true, ClassName = "Neutral lighting policy fixture" },
                Frame = new Rectangle(100, 100, 320, 220), Monitors = new[] { new Rectangle(-100, -100, 3000, 2000) }, Dpi = 96, Corner = 8
            };
        }
        internal static void Run(Action<bool, string> check)
        {
            foreach (int invalid in new[] { Int32.MinValue, -2, -1, 4, Int32.MaxValue }) {
                check(FocusAppearance.NormalizeLighting(invalid) == -1 &&
                    FocusAppearance.ResolveLighting(invalid, true) == FocusLightingMode.SubtleGlow &&
                    FocusAppearance.ResolveLighting(invalid, false) == FocusLightingMode.None,
                    "Absent or unsupported lighting carries forward the saved legacy glow flag");
            }
            for (int lighting = 0; lighting <= 3; lighting++) {
                check(FocusAppearance.NormalizeLighting(lighting) == lighting &&
                    (int)FocusAppearance.ResolveLighting(lighting, true) == lighting &&
                    (int)FocusAppearance.ResolveLighting(lighting, false) == lighting,
                    "An explicit lighting choice takes precedence over the legacy flag");
            }
            foreach (int dpi in new[] { 96, 120, 144, 168, 192, 288, 384, 768 }) {
                var target = Target(); target.Dpi = dpi;
                foreach (int lighting in new[] { 2, 3 }) {
                    var visual = FocusIndicatorVisual.Create(target, FocusIndicatorMode.InsetEdge, "Mint", true, false, lighting);
                    int width = (6 * dpi + 48) / 96;
                    check(visual != null && (int)visual.Lighting == lighting && visual.GlowWidth == width &&
                        visual.Bounds == FocusIndicatorVisual.Expand(target.Frame, width) && visual.GlowColor.A == 96 &&
                        visual.AccentColor == FocusAppearance.Accent("Mint", true) && visual.Dark &&
                        visual.Thickness == Math.Max(2, (2 * dpi + 48) / 96) && visual.Target.Frame == target.Frame,
                        "Premium lighting changes only the owned surface, scales the six-pixel light and keeps the two-pixel frame");
                }
            }
            var normal = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.MutedFrame, "Coral", true, false, 0);
            var illuminated = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.MutedFrame, "Coral", true, false, 2);
            var flow = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.MutedFrame, "Coral", true, false, 3);
            check(!normal.SameAppearance(illuminated) && !illuminated.SameAppearance(flow),
                "Changing a lighting finish invalidates the surface appearance cache");
            var large = Target(); large.Frame = new Rectangle(0, 0, 4096, 4096);
            large.Monitors = new[] { new Rectangle(-100, -100, 6000, 6000) };
            var bounded = FocusIndicatorVisual.Create(large, FocusIndicatorMode.MutedFrame, "Original", true, false, 2);
            check(bounded != null && bounded.Lighting == FocusLightingMode.Illuminated && bounded.GlowWidth == 0 && bounded.Bounds == large.Frame,
                "A premium halo exceeding the allocation cap retains the luminous frame without padding");
            var extreme = Target(); extreme.Frame = new Rectangle(Int32.MinValue + 2, Int32.MinValue + 2, 100, 100);
            extreme.Monitors = new[] { extreme.Frame };
            var noPadding = FocusIndicatorVisual.Create(extreme, FocusIndicatorMode.InsetEdge, "Sky", true, false, 3);
            check(noPadding != null && noPadding.Lighting == FocusLightingMode.EnergyFlow && noPadding.GlowWidth == 0 &&
                noPadding.Bounds == extreme.Frame, "Premium light overflow retains the thin lit frame without unsafe padding");
            foreach (int dpi in new[] { Int32.MinValue, 0, 95, 769, Int32.MaxValue }) {
                var scaled = Target(); scaled.Dpi = dpi;
                var visual = FocusIndicatorVisual.Create(scaled, FocusIndicatorMode.MutedFrame, "Mint", false, false, 2);
                int clamped = Math.Max(96, Math.Min(768, dpi));
                check(visual != null && visual.GlowWidth == (6 * clamped + 48) / 96 && visual.GlowColor.A == 68,
                    "Premium geometry clamps unsupported DPI values while keeping light-theme intensity bounded");
            }
            var light = FocusIndicatorVisual.Create(Target(), FocusIndicatorMode.MutedFrame, "Coral", false, false, 2);
            check(light.GlowColor.A == 68 && !light.Dark, "Light theme uses lower light intensity and preserves theme identity");
            var targetPolicy = Target(); bool allowed = true; int reads = 0;
            var surface = new Surface();
            var api = new FocusIndicatorApi {
                Foreground = delegate { return targetPolicy.Identity.Window; }, Read = delegate { return targetPolicy; },
                DesktopAllowed = delegate { return true; }, Dark = delegate { return true; },
                GlowAllowed = delegate { reads++; return allowed; }, PointerButtonDown = delegate { return false; },
                Watch = delegate { return new Events(); }
            };
            using (var indicator = new FocusIndicator(api, delegate { return surface; })) {
                indicator.Update(1, "Coral", false, true, 3);
                check(indicator.Visible && surface.Last.Lighting == FocusLightingMode.EnergyFlow && reads == 1,
                    "The production manager resolves and presents a saved premium finish");
                allowed = false; indicator.Update(1, "Coral", false, true, 3);
                check(indicator.Visible && surface.Last.Lighting == FocusLightingMode.None && surface.Last.GlowWidth == 0 && surface.Disposals == 0,
                    "High contrast suppresses all decorative lighting while retaining the focus outline");
                allowed = true; indicator.Update(1, "Coral", false, true, 3);
                check(surface.Last.Lighting == FocusLightingMode.EnergyFlow,
                    "The saved finish resumes after the high-contrast gate without a preference mutation");
                indicator.Update(1, "Coral", true, true, 3);
                check(!indicator.Visible && surface.Disposals == 1, "Pausing removes both lighting and frame immediately");
            }
        }
    }
}
