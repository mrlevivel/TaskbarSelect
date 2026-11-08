using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

// Render the real SettingsForm with an owned synthetic model. This helper never
// shows a window, takes desktop pixels, sends input, registers hotkeys, reads
// user preferences, or calls Controller.Dispose/Refresh or taskbar restoration.
// Arguments: <qualified beta.18 TaskbarSelect.exe> <owned output directory>.
internal static class RenderDocs
{
    const BindingFlags StaticHidden = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags InstanceHidden = BindingFlags.Instance | BindingFlags.NonPublic;
    const string RequiredVersion = "1.0.0-beta.18";
    const string RequiredSha256 = "9a39e12c5bcd3d2eaf9b2ad84006a4d393e2a057f9b58b1f84aad6972f4cdfae";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected candidate assembly and owned output directory.");
        string assemblyPath = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        string sourceHash;
        using (var hash = SHA256.Create())
        using (var input = File.OpenRead(assemblyPath))
            sourceHash = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        if (sourceHash != RequiredSha256)
            throw new InvalidOperationException("Documentation images require the qualified beta.18 portable executable.");
        Directory.CreateDirectory(output);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Assembly source = Assembly.LoadFrom(assemblyPath);
        string sourceVersion = source.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .Cast<AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        if (sourceVersion != RequiredVersion)
            throw new InvalidOperationException("Documentation image names require beta.18.");
        if (Application.ProductVersion != sourceVersion)
            throw new InvalidOperationException("Compile the renderer with the candidate AssemblyInfo.cs so the About version is accurate.");

        // The isolated metadata fences production paths and bypasses even the
        // read-only Startup.ReadState call in form construction. The unused
        // directory is never created or read. Render only native tab panels:
        // crops omit the fixture header/footer without changing their labels.
        // The accompanying image documentation identifies this as a fixture.
        Type profile = source.GetType("TaskbarSelect.AppProfile", true);
        Type selection = source.GetType("TaskbarSelect.ProfileSelection", true);
        string unusedProfile = Path.Combine(output, "unused-render-profile");
        if (Directory.Exists(unusedProfile))
            throw new InvalidOperationException("The documentation fixture profile must remain unused.");
        object isolated = selection.GetConstructors(InstanceHidden).Single().Invoke(
            new object[] { unusedProfile, true });
        profile.GetField("selection", StaticHidden).SetValue(null, isolated);
        source.GetType("TaskbarSelect.Ui", true).GetMethod("ApplyAccent", StaticHidden)
            .Invoke(null, new object[] { "Forest Green" });

        Type tests = source.GetType("TaskbarSelect.AppPolishTests", true);
        object controller = tests.GetMethod("Fixture", StaticHidden).Invoke(null, null);
        try {
            if ((bool)controller.GetType().GetField("DesktopRuntime", InstanceHidden).GetValue(controller))
                throw new InvalidOperationException("Documentation rendering requires a runtime-disabled owned fixture.");
            object settings = source.GetType("TaskbarSelect.Settings", true).GetMethod("CreateFresh", StaticHidden).Invoke(null, null);
            controller.GetType().GetField("Settings", InstanceHidden).SetValue(controller, settings);
            ((IDictionary)Get(settings, "MonitorAliases")).Clear();
            ((IList)Get(settings, "ExcludedApps")).Clear();
            IList displays = (IList)controller.GetType().GetField("Displays", InstanceHidden).GetValue(controller);
            string[] genericNames = { "Upper display", "Center display", "Left display", "Right display" };
            if (displays.Count != genericNames.Length) throw new InvalidOperationException("Expected four synthetic displays.");
            for (int i = 0; i < displays.Count; i++) Set(displays[i], "Name", genericNames[i]);
            ((IList)Get(settings, "HiddenIdentities")).Add(Get(displays[0], "Identity"));
            ((IList)Get(settings, "WindowShelfIdentities")).Add(Get(displays[0], "Identity"));
            string settingsBefore = (string)tests.GetMethod("Serialize", StaticHidden).Invoke(null, new[] { settings });

            using (var form = (Form)source.GetType("TaskbarSelect.SettingsForm", true)
                .GetConstructors(InstanceHidden).Single().Invoke(new[] { controller })) {
                if (form.Visible) throw new InvalidOperationException("The documentation fixture must stay hidden.");
                form.AutoScaleMode = AutoScaleMode.None;
                form.Size = new Size(920, 1100);
                Realize(form); Layout(form);
                TabControl tabs = Descendants(form).OfType<TabControl>().Single();
                Control map = Descendants(form).Single(control => control.GetType().Name == "DisplayMap");
                map.GetType().GetField("Selected", InstanceHidden).SetValue(map, displays[0]);
                map.GetType().GetMethod("RefreshAppearance", InstanceHidden).Invoke(map, null);
                form.GetType().GetMethod("ReadSelected", InstanceHidden).Invoke(form, null);
                foreach (string name in new[] { "Displays", "Appearance", "Settings" }) {
                    TabPage page = tabs.TabPages.Cast<TabPage>().Single(value => value.Text == name);
                    tabs.SelectedTab = page; page.AutoScrollPosition = Point.Empty;
                    Realize(form); Layout(form);
                    RenderPanel(tabs, page, Path.Combine(output, name.ToLowerInvariant() + "-beta18.png"));
                    if (name == "Appearance") {
                        Control colors = Descendants(page).Single(control => control.GetType().Name == "AppearanceChoiceGrid" && control.AccessibleName == "Accent color");
                        using (var palette = new Bitmap(colors.Width, colors.Height)) {
                            colors.DrawToBitmap(palette, new Rectangle(Point.Empty, colors.Size));
                            palette.Save(Path.Combine(output, "accent-colors-beta18.png"), ImageFormat.Png);
                            Console.WriteLine("accent-colors-beta18.png: owned accent control, " + palette.Width + "x" + palette.Height);
                        }
                    }
                }
                if (form.Visible) throw new InvalidOperationException("Owned documentation form was unexpectedly shown.");
                if (Directory.Exists(unusedProfile))
                    throw new InvalidOperationException("Documentation rendering unexpectedly created its unused profile.");
                string settingsAfter = (string)tests.GetMethod("Serialize", StaticHidden).Invoke(null, new[] { settings });
                bool settingsUnchanged = (bool)source.GetType("TaskbarSelect.FocusAppearanceSettingsTests", true)
                    .GetMethod("SameJson", StaticHidden).Invoke(null, new object[] { settingsBefore, settingsAfter });
                if (!settingsUnchanged)
                    throw new InvalidOperationException("Documentation rendering unexpectedly changed synthetic settings.");
            }
        } finally {
            // Release only hook-free fixture components. Controller.Dispose
            // would invoke global taskbar restoration and is deliberately avoided.
            tests.GetMethod("Release", StaticHidden).Invoke(null, new[] { controller });
        }
        Console.WriteLine("Source: " + sourceVersion + "; SHA-256 " + sourceHash);
        Console.WriteLine("Rendered four owned control previews; hidden fixture, desktop runtime disabled, synthetic settings unchanged, unused isolated profile absent.");
        return 0;
    }

    static void RenderPanel(TabControl tabs, TabPage page, string filename)
    {
        using (var full = new Bitmap(tabs.Width, tabs.Height)) {
            tabs.DrawToBitmap(full, new Rectangle(Point.Empty, tabs.Size));
            // Native combo printing can be omitted by a parent WM_PRINT walk.
            // Print each owned combo directly, never copying any screen pixels.
            using (Graphics graphics = Graphics.FromImage(full))
                foreach (ComboBox choice in Descendants(page).OfType<ComboBox>()) {
                    Point location = BoundsIn(choice, tabs).Location;
                    using (var printed = new Bitmap(choice.Width, choice.Height)) {
                        using (Graphics field = Graphics.FromImage(printed))
                            choice.GetType().GetMethod("PaintClosed", InstanceHidden).Invoke(choice, new object[] { field });
                        graphics.DrawImageUnscaled(printed, location);
                    }
                }
            Rectangle region = new Rectangle(Point.Empty, tabs.Size);
            if (page.Text == "Displays")
                region.Height = Descendants(page).Where(control => control.GetType().Name == "SettingsCard")
                    .Max(control => BoundsIn(control, tabs).Bottom) + 12;
            if (page.Text == "Appearance") {
                // End after the complete frame-style section instead of showing
                // half of the next scrolling card. Keep native pixels unchanged.
                Control styles = Descendants(page).Single(control => control.GetType().Name == "AppearanceChoiceGrid" && control.AccessibleName == "Frame style");
                region.Height = BoundsIn(styles, tabs).Bottom + 18;
            }
            if (page.Text == "Settings") {
                // The General card contains the honest test-only Startup label.
                // Omit that complete card rather than relabeling a disabled test
                // control as production. Retain the sensitivity and About cards.
                var cards = Descendants(page).Where(control => control.GetType().Name == "SettingsCard" &&
                    (control.AccessibleName == "Ghost Bar sensitivity" || control.AccessibleName == "About Taskbar Select")).ToArray();
                if (cards.Length != 2) throw new InvalidOperationException("Expected sensitivity and About cards.");
                region = cards.Select(control => BoundsIn(control, tabs)).Aggregate(Rectangle.Union);
                region.Inflate(8, 8);
            }
            region = Rectangle.Intersect(region, new Rectangle(Point.Empty, full.Size));
            using (Bitmap image = full.Clone(region, PixelFormat.Format32bppArgb))
                image.Save(filename, ImageFormat.Png);
            Console.WriteLine(Path.GetFileName(filename) + ": native tab-panel crop " + region);
        }
    }
    static Rectangle BoundsIn(Control control, Control ancestor)
    {
        Rectangle bounds = control.Bounds;
        for (Control parent = control.Parent; parent != ancestor; parent = parent.Parent) {
            if (parent == null) throw new InvalidOperationException("Owned render control is outside its panel.");
            bounds.Offset(parent.Location);
        }
        return bounds;
    }
    static object Get(object target, string property)
    { return target.GetType().GetProperty(property).GetValue(target, null); }
    static void Set(object target, string property, object value)
    { target.GetType().GetProperty(property).SetValue(target, value, null); }
    static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control value in Descendants(child)) yield return value;
    }
    static void Realize(Control root)
    {
        if (root.Handle == IntPtr.Zero) throw new InvalidOperationException("Owned render handle was not created.");
        foreach (Control child in root.Controls) Realize(child);
    }
    static void Layout(Control root)
    { root.PerformLayout(); foreach (Control child in root.Controls) Layout(child); root.PerformLayout(); }
}
