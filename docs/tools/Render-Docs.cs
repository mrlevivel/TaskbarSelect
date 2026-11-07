using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Render the real SettingsForm with an owned synthetic model. This helper never
// shows a window, takes desktop pixels, sends input, registers hotkeys, reads
// user preferences, or calls Controller.Dispose/Refresh or taskbar restoration.
// Arguments: <candidate TaskbarSelect.exe> <owned output directory>.
internal static class RenderDocs
{
    const BindingFlags StaticHidden = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags InstanceHidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected candidate assembly and owned output directory.");
        string assemblyPath = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Assembly source = Assembly.LoadFrom(assemblyPath);
        string sourceVersion = source.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .Cast<AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        if (Application.ProductVersion != sourceVersion)
            throw new InvalidOperationException("Compile the renderer with the candidate AssemblyInfo.cs so the About version is accurate.");

        // The isolated metadata fences production paths and bypasses even the
        // read-only Startup.ReadState call in form construction. The unused
        // directory is never created or read. Keep the actual isolated-test
        // badge visible, so the fixture is not mistaken for an installed app.
        Type profile = source.GetType("TaskbarSelect.AppProfile", true);
        Type selection = source.GetType("TaskbarSelect.ProfileSelection", true);
        object isolated = selection.GetConstructors(InstanceHidden).Single().Invoke(
            new object[] { Path.Combine(output, "unused-render-profile"), true });
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

            using (var form = (Form)source.GetType("TaskbarSelect.SettingsForm", true)
                .GetConstructors(InstanceHidden).Single().Invoke(new[] { controller })) {
                form.AutoScaleMode = AutoScaleMode.None;
                form.Size = new Size(920, 1020);
                Realize(form); Layout(form);
                TabControl tabs = Descendants(form).OfType<TabControl>().Single();
                foreach (string name in new[] { "Displays", "Appearance", "Shortcuts", "Settings" }) {
                    form.Size = new Size(920, name == "Settings" || name == "Shortcuts" ? 1100 : 1020);
                    TabPage page = tabs.TabPages.Cast<TabPage>().Single(value => value.Text == name);
                    tabs.SelectedTab = page; page.AutoScrollPosition = Point.Empty;
                    Realize(form); Layout(form);
                    if (name == "Shortcuts") {
                        RenderClient(form, page, Path.Combine(output, "settings-display-shortcuts.png"));
                        Control pin = Descendants(page).Single(control => control.AccessibleName == "Keep focused window on top shortcut status");
                        page.ScrollControlIntoView(pin); Layout(form);
                    }
                    RenderClient(form, page, Path.Combine(output, "settings-" + name.ToLowerInvariant() + ".png"));
                    if (name == "Appearance") {
                        Control colors = Descendants(page).Single(control => control.GetType().Name == "AppearanceChoiceGrid" && control.AccessibleName == "Accent color");
                        using (var palette = new Bitmap(colors.Width, colors.Height)) {
                            colors.DrawToBitmap(palette, new Rectangle(Point.Empty, colors.Size));
                            palette.Save(Path.Combine(output, "settings-colors.png"), ImageFormat.Png);
                        }
                    }
                }
                if (form.Visible) throw new InvalidOperationException("Owned documentation form was unexpectedly shown.");
                if (Directory.Exists(Path.Combine(output, "unused-render-profile")))
                    throw new InvalidOperationException("Documentation rendering unexpectedly created its unused profile.");
            }
        } finally {
            // Release only hook-free fixture components. Controller.Dispose
            // would invoke global taskbar restoration and is deliberately avoided.
            tests.GetMethod("Release", StaticHidden).Invoke(null, new[] { controller });
        }
        Console.WriteLine("Rendered owned client-area documentation images and palette; no desktop capture or input.");
        return 0;
    }

    static void RenderClient(Form form, TabPage page, string filename)
    {
        Point client = form.PointToScreen(Point.Empty);
        client.Offset(-form.Left, -form.Top);
        using (var full = new Bitmap(form.Width, form.Height)) {
            form.DrawToBitmap(full, new Rectangle(Point.Empty, form.Size));
            // Native combo printing can be omitted by a parent WM_PRINT walk.
            // Print each owned combo directly, never copying any screen pixels.
            using (Graphics graphics = Graphics.FromImage(full))
                foreach (ComboBox choice in Descendants(page).OfType<ComboBox>()) {
                    Point location = client;
                    for (Control parent = choice; parent != form; parent = parent.Parent)
                        location.Offset(parent.Left, parent.Top);
                    using (var printed = new Bitmap(choice.Width, choice.Height)) {
                        using (Graphics field = Graphics.FromImage(printed))
                            choice.GetType().GetMethod("PaintClosed", InstanceHidden).Invoke(choice, new object[] { field });
                        graphics.DrawImageUnscaled(printed, location);
                    }
                }
            // DrawToBitmap does not reproduce modern DWM nonclient chrome.
            // The actual application header is retained; omit that false titlebar.
            Size region = form.ClientSize;
            if (Path.GetFileName(filename) == "settings-display-shortcuts.png") {
                Control movement = Descendants(page).Single(control =>
                    control.GetType().Name == "SettingsCard" && control.AccessibleName == "Window movement");
                Point bottom = movement.PointToScreen(new Point(0, movement.Height));
                region.Height = Math.Min(region.Height, bottom.Y - form.PointToScreen(Point.Empty).Y + 12);
            }
            if (page.Text == "Appearance") {
                // End after the complete frame-style section instead of showing
                // half of the next scrolling card. Keep native pixels unchanged.
                Control styles = Descendants(page).Single(control => control.GetType().Name == "AppearanceChoiceGrid" && control.AccessibleName == "Frame style");
                Point bottom = styles.PointToScreen(new Point(0, styles.Height));
                region.Height = Math.Min(region.Height, bottom.Y - form.PointToScreen(Point.Empty).Y + 18);
            }
            using (Bitmap image = full.Clone(new Rectangle(client, region), PixelFormat.Format32bppArgb))
                image.Save(filename, ImageFormat.Png);
        }
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
