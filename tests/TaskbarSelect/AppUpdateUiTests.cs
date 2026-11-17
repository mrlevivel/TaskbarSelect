using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // These exercise presentation logic and logical focus on unshown controls.
    // They don't activate windows, access transport, or request permissions.
    internal static class AppUpdateUiTests
    {
        internal static void Run(Action<bool, string> check)
        {
            if (check == null) throw new ArgumentNullException("check");
            Percentages(check);
            TransferText(check);
            AdministratorMessages(check);
            AdministratorFocus(check);
        }

        static void AdministratorFocus(Action<bool, string> check)
        {
            using (var host = new ContainerControl())
            using (var support = new ToggleSwitch("Administrator support"))
            using (var update = new Button())
            using (var other = new Button()) {
                host.Controls.AddRange(new Control[] { support, update, other });
                host.ActiveControl = support;
                check(AppUpdateForm.AdministratorFocusTarget(host.ActiveControl, support, true, update) == update,
                    "Confirmed removal sends focus from the switch to the update action");
                check(AppUpdateForm.AdministratorFocusTarget(host.ActiveControl, support, false, update) == null &&
                    host.ActiveControl == support,
                    "Pending or cancelled removal keeps keyboard focus on the switch");
                host.ActiveControl = other;
                check(AppUpdateForm.AdministratorFocusTarget(host.ActiveControl, support, false, update) == null &&
                    AppUpdateForm.AdministratorFocusTarget(host.ActiveControl, support, true, update) == null &&
                    host.ActiveControl == other,
                    "A user's deliberate focus change survives either permission outcome");
            }
        }

        static void AdministratorMessages(Action<bool, string> check)
        {
            string requirement = AppUpdateInstaller.ApprovalError;
            check(AppUpdateForm.AdministratorUpdateMessage(requirement, "On", false).StartsWith(requirement) &&
                AppUpdateForm.AdministratorUpdateMessage(requirement, "On", false).Contains("Windows may ask"),
                "The update panel explains the Off requirement and possible Windows prompt");
            check(AppUpdateForm.AdministratorUpdateMessage(requirement, "Turning off", true).Contains("Waiting") &&
                AppUpdateForm.AdministratorUpdateMessage(requirement, "Off not confirmed", false).Contains("Try again"),
                "The update panel distinguishes pending and incomplete permission removal");
            check(AppUpdateForm.AdministratorUpdateMessage(null, "Off", false).Contains("Administrator support is off.") &&
                AppUpdateForm.AdministratorUpdateMessage(AppUpdateInstaller.ApprovalRemovalError, "Off", false)
                    .StartsWith(AppUpdateInstaller.ApprovalRemovalError),
                "The update panel says Off only after approval removal is verified");
            check(AppUpdateInstaller.IsAdministratorError(requirement) &&
                AppUpdateInstaller.IsAdministratorError(AppUpdateInstaller.ApprovalCheckError) &&
                AppUpdateInstaller.IsAdministratorError(AppUpdateInstaller.ApprovalRemovalError) &&
                !AppUpdateInstaller.IsAdministratorError(AppUpdateInstaller.PortableError) &&
                !AppUpdateInstaller.IsAdministratorError(null),
                "Only administrator errors present the removal action and retain the verified update on a blocked launch");
        }

        static void Percentages(Action<bool, string> check)
        {
            check(AppUpdateForm.ProgressPercentage(0, 1024) == 0 &&
                AppUpdateForm.ProgressPercentage(-1, 1024) == 0 &&
                AppUpdateForm.ProgressPercentage(Int64.MinValue, 1024) == 0,
                "Update progress treats missing or negative received bytes as zero");
            check(AppUpdateForm.ProgressPercentage(1, 0) == 0 &&
                AppUpdateForm.ProgressPercentage(Int64.MaxValue, -1) == 0 &&
                AppUpdateForm.ProgressPercentage(Int64.MaxValue, Int64.MinValue) == 0,
                "Update progress cannot divide by unknown, zero or negative transfer lengths");
            check(AppUpdateForm.ProgressPercentage(1, 3) == 33 &&
                AppUpdateForm.ProgressPercentage(2, 3) == 66 &&
                AppUpdateForm.ProgressPercentage(99, 100) == 99,
                "Incomplete update transfers display a bounded whole percentage without rounding up ordinary byte counts");
            check(AppUpdateForm.ProgressPercentage(1, 1) == 100 &&
                AppUpdateForm.ProgressPercentage(1025, 1024) == 100 &&
                AppUpdateForm.ProgressPercentage(Int64.MaxValue, 1) == 100,
                "Complete or overlong update progress clamps to the progress bar maximum");
            long evenMaximum = Int64.MaxValue - 1;
            check(AppUpdateForm.ProgressPercentage(evenMaximum / 2, evenMaximum) == 50 &&
                AppUpdateForm.ProgressPercentage(evenMaximum / 4, evenMaximum) == 25 &&
                AppUpdateForm.ProgressPercentage(evenMaximum, evenMaximum) == 100,
                "Update progress handles Int64-sized transfers without multiplying bytes into an overflow");

            const long size = 64L * 1024 * 1024;
            long[] received = { 0, 1, size / 4, size / 2, size * 3 / 4, size - 1, size, size + 1 };
            int[] expected = { 0, 0, 25, 50, 75, 99, 100, 100 };
            bool monotonic = true; int previous = -1;
            for (int index = 0; index < received.Length; index++) {
                int value = AppUpdateForm.ProgressPercentage(received[index], size);
                if (value != expected[index] || value < previous || value < 0 || value > 100) monotonic = false;
                previous = value;
            }
            check(monotonic, "A maximum supported installer advances monotonically through zero, partial and complete progress");
        }

        static void TransferText(Action<bool, string> check)
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                const long mb = 1024L * 1024;
                check(AppUpdateForm.FormatTransfer(0, 0) == "0.0 of 0.0 MB downloaded",
                    "An update transfer with no received bytes has readable zero-sized progress text");
                check(AppUpdateForm.FormatTransfer(mb, 4 * mb) == "1.0 of 4.0 MB downloaded" &&
                    AppUpdateForm.FormatTransfer(mb + mb / 2, 3 * mb) == "1.5 of 3.0 MB downloaded",
                    "Update transfer text reports whole and fractional binary-megabyte byte counts consistently");
                check(AppUpdateForm.FormatTransfer(-1, 2 * mb) == "0.0 of 2.0 MB downloaded" &&
                    AppUpdateForm.FormatTransfer(mb, Int64.MinValue) == "1.0 of 0.0 MB downloaded" &&
                    AppUpdateForm.FormatTransfer(Int64.MinValue, -1) == "0.0 of 0.0 MB downloaded",
                    "Invalid byte counters never present negative megabyte values to users");
                check(AppUpdateForm.FormatTransfer(Int64.MaxValue, Int64.MaxValue) ==
                    "8796093022208.0 of 8796093022208.0 MB downloaded",
                    "Update transfer formatting accepts Int64 byte limits without wrapping or scientific notation");
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                check(AppUpdateForm.FormatTransfer(mb + mb / 2, 3 * mb) == "1,5 of 3,0 MB downloaded",
                    "Update transfer numbers respect the user's decimal separator");
            } finally { Thread.CurrentThread.CurrentCulture = previous; }
        }
    }
}
