using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TaskbarSelect
{
    // Owned bitmaps/icons only: no windows, input, Explorer or profile writes.
    internal static class AccentIconTests
    {
        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
        static readonly Color OriginalInk = Color.FromArgb(147, 239, 191);
        static readonly Color Background = Color.FromArgb(36, 43, 42);
        static readonly Color Outline = Color.FromArgb(242, 245, 243);

        internal static void Run(Action<bool, string> check)
        {
            using (var solid = new Bitmap(3, 1)) {
                solid.SetPixel(0, 0, OriginalInk); solid.SetPixel(1, 0, Background); solid.SetPixel(2, 0, Outline);
                foreach (string preset in FocusAppearance.Presets) {
                    Color ink = FocusAppearance.Accent(preset, true);
                    using (var painted = Branding.AccentBitmap(solid, ink))
                        check(painted.GetPixel(0, 0).ToArgb() == ink.ToArgb() && painted.GetPixel(1, 0).ToArgb() == Background.ToArgb() &&
                            painted.GetPixel(2, 0).ToArgb() == Outline.ToArgb(), "Accent-only recoloring maps each exact palette ink without tinting neutral artwork");
                }
            }
            foreach (int size in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }) {
                using (var icon = Branding.LoadIcon(size))
                using (var original = icon.ToBitmap()) {
                    long originalHash = Hash(original);
                    foreach (string preset in FocusAppearance.Presets) {
                        Color target = FocusAppearance.Accent(preset, true);
                        using (var painted = Branding.AccentBitmap(original, target)) {
                            bool alpha = true, neutral = true, core = true;
                            int cores = 0, changed = 0, transparent = 0;
                            for (int y = 0; y < original.Height; y++) for (int x = 0; x < original.Width; x++) {
                                Color source = original.GetPixel(x, y), next = painted.GetPixel(x, y);
                                alpha &= source.A == next.A;
                                if (source.A == 0) transparent++;
                                if (source.ToArgb() == Background.ToArgb() || source.ToArgb() == Outline.ToArgb()) neutral &= source.ToArgb() == next.ToArgb();
                                if (source.ToArgb() == OriginalInk.ToArgb()) { cores++; core &= next.ToArgb() == target.ToArgb(); }
                                if (source.ToArgb() != next.ToArgb()) changed++;
                            }
                            check(alpha && transparent > 0, "Accent icon preserves all original alpha and transparent corners at " + size + "/" + preset);
                            check(neutral, "Accent icon preserves neutral outline/background at " + size + "/" + preset);
                            check((cores == 0 || core) && changed > 0, "Accent icon recolors the existing check/stripe to the selected palette at " + size + "/" + preset);
                        }
                    }
                    using (var unchanged = Branding.AccentBitmap(original, OriginalInk))
                        check(Hash(unchanged) == originalHash, "Original source ink reconstructs every pixel without changing asset details at " + size);
                    check(Hash(original) == originalHash, "Accent rendering never modifies the shared source at " + size);
                    using (var recolored = Branding.LoadAccentIcon(size, "Blaze Orange"))
                    using (var nativeRoundtrip = recolored.ToBitmap()) {
                        bool alpha = true;
                        for (int y = 0; y < original.Height; y++) for (int x = 0; x < original.Width; x++)
                            alpha &= original.GetPixel(x, y).A == nativeRoundtrip.GetPixel(x, y).A;
                        check(recolored.Width == icon.Width && recolored.Height == icon.Height && alpha,
                            "Owned native icon roundtrip preserves dimensions/alpha at " + size);
                    }
                }
            }
            using (var one = Branding.LoadAccentIcon(32, "Original"))
            using (var two = Branding.LoadAccentIcon(32, "Teal"))
            using (var first = one.ToBitmap())
            using (var second = two.ToBitmap())
                check(Hash(first) == Hash(second), "Stored Original and visible Teal share the exact icon");

            foreach (int invalid in new[] { Int32.MinValue, -1, 0, 257, Int32.MaxValue }) {
                bool refused = false;
                try { using (var invalidIcon = Branding.LoadAccentIcon(invalid, "Coral")) { } }
                catch (ArgumentOutOfRangeException) { refused = true; }
                check(refused, "Accent icon refuses invalid allocation sizes");
            }
            bool nullRefused = false;
            try { using (var invalid = Branding.AccentBitmap(null, Color.Red)) { } }
            catch (ArgumentNullException) { nullRefused = true; }
            check(nullRefused, "Accent bitmap requires an owned source");

            using (var process = Process.GetCurrentProcess()) {
                // Warm up GDI+/Icon wrappers before comparing owned USER/GDI
                // resources; only returned managed icons survive the factory.
                using (var warmup = Branding.LoadAccentIcon(32, "Coral")) { }
                uint user = GetGuiResources(process.Handle, 1), gdi = GetGuiResources(process.Handle, 0);
                for (int index = 0; index < 80; index++)
                    using (var icon = Branding.LoadAccentIcon(32, index % 2 == 0 ? "Coral" : "Blaze Orange"))
                    using (var image = icon.ToBitmap())
                        if (image.Width != 32) throw new InvalidOperationException("Owned icon no longer has its source size.");
                uint finalUser = GetGuiResources(process.Handle, 1), finalGdi = GetGuiResources(process.Handle, 0);
                check(finalUser <= user + 1 && finalGdi <= gdi + 1, "Repeated accent replacement releases temporary native icons and bitmaps");
            }
            SettingsWindow(check);
        }

        static void SettingsWindow(Action<bool, string> check)
        {
            Color originalUiAccent = Ui.Accent;
            using (var controller = new Controller(false) { Settings = new Settings { AccentPreset = "Coral" }, Enabled = false })
            using (var form = new SettingsForm(controller)) {
                check(!form.IsHandleCreated && !controller.DesktopRuntime, "Accent icon Settings fixture stays hidden with no desktop hooks");
                using (var expected = Branding.LoadAccentIcon(32, "Coral"))
                using (var expectedImage = expected.ToBitmap())
                using (var actual = form.Icon.ToBitmap())
                    check(Hash(expectedImage) == Hash(actual), "Settings initially uses its own selected accent even without a live controller palette");
                Icon unchanged = form.Icon;
                form.UpdateStatus();
                check(Object.ReferenceEquals(unchanged, form.Icon), "Ordinary status refresh reuses the current window icon");
                long before;
                using (var image = form.Icon.ToBitmap()) before = Hash(image);
                controller.Settings.AccentPreset = "Blaze Orange"; controller.Revision++;
                form.UpdateStatus();
                using (var expected = Branding.LoadAccentIcon(32, "Blaze Orange"))
                using (var expectedImage = expected.ToBitmap())
                using (var actual = form.Icon.ToBitmap())
                    check(Hash(expectedImage) == Hash(actual) && (Hash(actual) != before) ==
                        (Branding.AccentIconColor("Coral") != Branding.AccentIconColor("Blaze Orange")), "Existing Settings window updates to Blaze Orange without reopening (or keeps Windows high-contrast ink)");
                foreach (string preset in new[] { "Original", "Mint", "Coral", "Cobalt", "Blaze Orange" }) {
                    controller.Settings.AccentPreset = preset; controller.Revision++;
                    form.UpdateStatus();
                    using (var expected = Branding.LoadAccentIcon(32, preset))
                    using (var expectedImage = expected.ToBitmap())
                    using (var actual = form.Icon.ToBitmap())
                        check(Hash(actual) == Hash(expectedImage), "Repeated Settings accent changes replace the actual window icon with the correct palette");
                }
                check(Ui.Accent == originalUiAccent && !form.IsHandleCreated && !controller.DesktopRuntime,
                    "Hidden icon integration does not change global palette or create a desktop window");
            }
        }

        static long Hash(Bitmap image)
        {
            unchecked {
                long hash = 1469598103934665603L;
                for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                    hash = (hash ^ image.GetPixel(x, y).ToArgb()) * 1099511628211L;
                return hash;
            }
        }
    }
}
