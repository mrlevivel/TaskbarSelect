using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class AdministratorUiTests
    {
        static IEnumerable<Control> Children(Control root)
        {
            foreach (Control child in root.Controls) {
                yield return child;
                foreach (Control item in Children(child)) yield return item;
            }
        }

        internal static void Run(Action<bool, string> check)
        {
            byte[] before = File.Exists(Program.ConfigPath) ? File.ReadAllBytes(Program.ConfigPath) : null;
            BusySwitchFocus(check);
            foreach (Size size in new[] { new Size(920, 860), new Size(820, 780), new Size(740, 738) }) {
                var controller = new Controller(false) { Settings = new Settings(), Enabled = false,
                    Displays = new List<Display>(), Bars = new List<Bar>() };
                try {
                    using (var form = new SettingsForm(controller)) {
                        form.ClientSize = size;
                        form.CreateControl();
                        var tabs = Children(form).OfType<TabControl>().Single();
                        tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Settings");
                        IntPtr handle = form.Handle;
                        foreach (Control item in Children(form)) handle = item.Handle;
                        check(handle != IntPtr.Zero && !form.Visible, "Administrator UI fixture realizes only owned hidden controls");
                        Layout(form);
                        var controls = Children(form).ToArray();
                        var toggle = controls.OfType<ToggleSwitch>().Single(item => item.AccessibleName == "Administrator support");
                        var status = controls.OfType<Label>().Single(item => item.AccessibleName == "Administrator support status");
                        var detail = controls.OfType<Label>().Single(item => item.AccessibleName == "Administrator support detail");
                        check(controls.Count(item => item.AccessibleName == "Administrator support") == 1 && toggle.Text == "Administrator support",
                            "Administrator support has one labeled switch using the app's existing toggle control");
                        check(!controls.OfType<Button>().Any(item => item.AccessibleName == "Administrator support" ||
                            item.AccessibleName == "Remember administrator support" || item.AccessibleName == "Remove saved administrator approval"),
                            "Separate current-session, Remember and Remove approval buttons are absent");
                        check(status.Text == "Administrator support: Off" && !toggle.Checked && !toggle.Enabled,
                            "Hook-free settings default to Off and cannot request permission");
                        check(controller.AdministratorPermissions == null && controller.Administrator == null && !controller.DesktopRuntime,
                            "Hidden UI creates no permission coordinator, broker or desktop runtime");
                        check(toggle.AccessibleDescription.Contains("remember approval") && toggle.AccessibleDescription.Contains("stop support now") &&
                            toggle.AccessibleDescription.Contains("remove saved approval"),
                            "Switch description explains both persistent On and complete Off actions");
                        string preferences = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(controller.Settings);
                        toggle.Checked = true;
                        check(!toggle.Checked && !controller.Settings.PersistentAdministratorSupport && controller.AdministratorPermissions == null,
                            "Programmatic interaction in a hook-free fixture resets to the real snapshot without a permission request");

                        foreach (var snapshot in new[] {
                            new { Selected = false, Busy = false, Status = "Off", Detail = "Administrator support is off." },
                            new { Selected = true, Busy = false, Status = "On", Detail = "Approval is remembered for this build." },
                            new { Selected = true, Busy = false, Status = "On \u2014 this session only", Detail = "This older session has no saved approval. Turn off, then on to remember support." },
                            new { Selected = true, Busy = true, Status = "Waiting for Windows approval", Detail = "Approve or cancel the Windows prompt yourself." },
                            new { Selected = true, Busy = true, Status = "Turning off\u2026", Detail = "Stopping support and removing saved approval." },
                            new { Selected = true, Busy = false, Status = "Off not confirmed", Detail = "Could not confirm helper shutdown or saved-approval removal. Try switching off again." },
                            new { Selected = true, Busy = false, Status = "Recovery pending", Detail = "Recovery information has been retained." },
                            new { Selected = false, Busy = false, Status = "Unavailable", Detail = "Windows did not approve this request." }
                        }) {
                            form.PresentAdministratorSupport(snapshot.Selected, snapshot.Busy, snapshot.Status, snapshot.Detail);
                            check(toggle.Checked == snapshot.Selected && status.Text == "Administrator support: " + snapshot.Status && detail.Text == snapshot.Detail,
                                "Settings render the coordinator snapshot exactly without inferring success from a preference or helper state");
                            check(!toggle.Enabled && toggle.Busy == snapshot.Busy && detail.AccessibleDescription == snapshot.Detail,
                                "Hidden snapshot rendering exposes pending work without enabling permissions and retains full accessible detail");
                            check(controller.AdministratorPermissions == null && controller.Administrator == null &&
                                new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(controller.Settings) == preferences,
                                "Snapshot synchronization cannot authorize support, create a coordinator or change preferences");
                        }

                        const string failedOff = "Could not turn off administrator support.\nSaved approval or helper recovery is still present.\nTry switching off again when Windows is ready.\nApps opened through search keep normal permissions.";
                        for (int repeat = 0; repeat < 3; repeat++) {
                            form.PresentAdministratorSupport(true, false, "Off not confirmed", failedOff);
                            check(toggle.Checked && status.Text == "Administrator support: Off not confirmed" && detail.Text == failedOff,
                                "A failed-Off snapshot remains checked and keeps its explicit error and retry instruction across refreshes");
                        }
                        Layout(form);
                        check(toggle.Parent.Bottom <= status.Top && status.Bottom <= detail.Top && status.Height >= status.Font.Height,
                            "Administrator toggle, status and explanation occupy separate rows");
                        check(!detail.AutoEllipsis && TextRenderer.MeasureText(detail.Text, detail.Font,
                            new Size(Math.Max(1, detail.ClientSize.Width), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height <= detail.ClientSize.Height,
                            "Four-line administrator error and retry text fits at " + size.Width + " pixels");
                        check(toggle.Width >= TextRenderer.MeasureText(toggle.Text, toggle.Font).Width + 40,
                            "Administrator switch label remains readable at " + size.Width + " pixels");
                        string longDetail = AdministratorPermissionSwitchTests.LongFailureDetail();
                        check(longDetail.Length > 500 && longDetail.Contains(AdministratorPermissionSwitch.Explanation),
                            "Long error fixture uses the coordinator's complete diagnostic and explanation");
                        form.PresentAdministratorSupport(true, false, "Off not confirmed", longDetail);
                        Layout(form);
                        check(detail.Text == longDetail && !detail.AutoEllipsis &&
                            TextRenderer.MeasureText(detail.Text, detail.Font, new Size(Math.Max(1, detail.ClientSize.Width), Int32.MaxValue),
                                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height <= detail.ClientSize.Height,
                            "The administrator detail row grows to show a real long coordinator error at " + size.Width + " pixels");
                        check(detail.Bottom <= detail.Parent.ClientSize.Height && detail.Parent.Bottom <= detail.Parent.Parent.ClientSize.Height,
                            "Growing administrator detail also grows its General card without clipping child rows");
                        form.PresentAdministratorSupport(false, true, "Waiting for Windows approval", "Pending approval.");
                        check(!toggle.Checked && !toggle.Enabled && status.Text == "Administrator support: Waiting for Windows approval",
                            "Busy rendering respects the selected snapshot and cannot reenter a permission request");
                        form.UpdateStatus();
                        check(!toggle.Checked && !toggle.Enabled && status.Text == "Administrator support: Off",
                            "A normal status refresh restores the actual hook-free Off snapshot");
                        check(!form.Visible && WindowMoveWorker.Administrator == null &&
                            new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(controller.Settings) == preferences,
                            "Administrator switch UI regressions leave the desktop and permission preferences untouched");
                        check(Diagnostics.Create(controller).Contains("Administrator support: Off"), "Diagnostics preserve the broker's actual Off state");
                    }
                } finally {
                    // Controller.Dispose restores global taskbars; release only
                    // these fixture-owned hook-free components instead.
                    controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                    foreach (GlobalShortcut binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
                }
            }
            byte[] after = File.Exists(Program.ConfigPath) ? File.ReadAllBytes(Program.ConfigPath) : null;
            check(before == null ? after == null : after != null && before.SequenceEqual(after),
                "Administrator switch UI checks preserve the profile settings file");
        }

        static void Click(ToggleSwitch toggle)
        {
            typeof(ToggleSwitch).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(toggle, new object[] { EventArgs.Empty });
        }

        static void BusySwitchFocus(Action<bool, string> check)
        {
            // ContainerControl.ActiveControl records logical ownership without
            // showing a Form, requesting Win32 focus or invoking permissions.
            using (var host = new ContainerControl())
            using (var toggle = new ToggleSwitch("Neutral pending-operation fixture"))
            using (var width = new NumericUpDown())
            using (var other = new Button()) {
                host.Controls.Add(toggle); host.Controls.Add(width); host.Controls.Add(other);
                int changes = 0, clicks = 0;
                toggle.CheckedChanged += delegate { changes++; };
                toggle.Click += delegate { clicks++; };
                host.ActiveControl = toggle;
                toggle.Busy = true;
                check(Object.ReferenceEquals(host.ActiveControl, toggle) && toggle.Enabled && !toggle.AutoCheck,
                    "A busy switch retains logical focus and stays selectable without automatically focusing sensitivity fields");
                Click(toggle); toggle.AccessibilityObject.DoDefaultAction();
                check(!toggle.Checked && changes == 0 && clicks == 0,
                    "Shared checkbox click dispatch and accessibility default action cannot toggle or dispatch a pending operation");
                AccessibleStates state = toggle.AccessibilityObject.State;
                check((state & AccessibleStates.Busy) != 0 && (state & AccessibleStates.ReadOnly) != 0 &&
                    String.IsNullOrEmpty(toggle.AccessibilityObject.DefaultAction),
                    "Assistive technology receives busy and read-only state with no available toggle action");
                toggle.Busy = false;
                AccessibleStates ready = toggle.AccessibilityObject.State;
                check(Object.ReferenceEquals(host.ActiveControl, toggle) && toggle.Enabled && toggle.AutoCheck &&
                    (ready & (AccessibleStates.Busy | AccessibleStates.ReadOnly)) == 0,
                    "Completing pending work restores interaction and ordinary accessibility state without any focus restoration request");
                Click(toggle);
                check(toggle.Checked && changes == 1 && clicks == 1,
                    "An ordinary nonbusy switch retains the existing single-click toggle behavior");
                host.ActiveControl = width;
                toggle.Busy = true; toggle.Busy = false;
                check(Object.ReferenceEquals(host.ActiveControl, width),
                    "A user's deliberate sensitivity-field selection survives later administrator status updates");
                host.ActiveControl = other;
                toggle.Busy = true; toggle.Busy = false;
                check(Object.ReferenceEquals(host.ActiveControl, other),
                    "An unrelated selected control is never replaced by implicit administrator focus restoration");
            }
        }

        static void Layout(SettingsForm form)
        {
            form.PerformLayout();
            foreach (Control item in Children(form)) item.PerformLayout();
        }
    }
}
