using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Automation;

namespace TaskbarSelect
{
    // Checking starts only when the user opens this window from Settings.
    // Release notes stay plain text; no remote markup or links are executed.
    internal sealed class AppUpdateForm : Form
    {
        internal const string OfficialReleases = AppUpdatePolicy.Repository + "/releases";
        internal const string UnsignedNotice = "Downloads are unsigned. Windows may show a security warning.";
        const string SettingsNotice = "Setup keeps your settings.";
        readonly Controller controller;
        readonly AppUpdateService service;
        readonly Label current = Ui.Label(""), state = Ui.Label(""), releaseHeading = Ui.Label("Release notes"), guidance = Ui.Label("");
        readonly TextBox notes = new TextBox();
        readonly ProgressBar progress = new ProgressBar();
        readonly Label progressText = Ui.Label("");
        readonly Label administratorStatus = Ui.Label("");
        readonly Button check, primary, close, releases;
        readonly ToggleSwitch administratorSupport = new ToggleSwitch("Administrator support");
        readonly TableLayoutPanel administratorPanel;
        readonly Icon windowIcon;
        CancellationTokenSource operation;
        AppUpdateRelease release;
        VerifiedAppUpdate verified;
        bool busy, downloading, closing, started;
        bool requestingAdministratorOff, observedAdministratorOff;
        bool readingAdministratorSwitch;
        string administratorBlock;
        int generation;
        readonly string eligibilityError;

        internal AppUpdateForm(Controller stateController)
            : this(stateController, AppUpdateService.CreateDefault()) { }

        internal AppUpdateForm(Controller stateController, AppUpdateService updateService)
        {
            if (stateController == null) throw new ArgumentNullException("stateController");
            if (updateService == null) throw new ArgumentNullException("updateService");
            controller = stateController; service = updateService;
            eligibilityError = AppUpdateInstaller.EligibilityError(controller.DesktopRuntime);
            Text = "Taskbar Select updates";
            Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 560); MinimumSize = new Size(520, 440);
            ShowInTaskbar = false; MinimizeBox = false;
            windowIcon = Branding.LoadIcon(32); Icon = windowIcon;

            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Padding = new Padding(20, 16, 20, 16), Margin = Padding.Empty };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty,
                AccessibleName = "Update details" };
            var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 5,
                Padding = Padding.Empty, Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            // Keep useful notes space at every window size. Extra text scrolls
            // with the details instead of squeezing this card down to a line.
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(root); shell.Controls.Add(body, 0, 0);
            var heading = Ui.Label("Updates"); heading.Font = new Font(Font.FontFamily, 18, FontStyle.Bold);
            heading.AccessibleName = "Updates"; heading.Margin = Padding.Empty;
            root.Controls.Add(heading, 0, 0);
            current.Text = "Current version " + Application.ProductVersion;
            current.AccessibleName = "Installed version"; current.Margin = Padding.Empty;
            root.Controls.Add(current, 0, 1);
            state.AccessibleName = "Update status"; state.AutoEllipsis = false;
            state.LiveSetting = AutomationLiveSetting.Polite;
            state.TextChanged += delegate { state.AccessibleName = state.Text; };
            state.AutoSize = true; state.Dock = DockStyle.Top;
            state.Margin = new Padding(0, 8, 0, 12);
            state.Text = "Ready to check for updates.";
            root.Controls.Add(state, 0, 2);

            var card = new SettingsCard { Dock = DockStyle.Fill, Margin = Padding.Empty,
                Padding = new Padding(12), AccessibleName = "Release information" };
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            releaseHeading.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
            releaseHeading.AccessibleName = "Available release"; releaseHeading.Margin = Padding.Empty;
            notes.Multiline = true; notes.ReadOnly = true; notes.BorderStyle = BorderStyle.None;
            notes.ScrollBars = ScrollBars.Vertical; notes.Dock = DockStyle.Fill;
            notes.WordWrap = true; notes.ShortcutsEnabled = true; notes.MaxLength = 131072;
            notes.AccessibleName = "Release notes";
            notes.AccessibleDescription = "Plain-text changes in the available version.";
            notes.Text = "New versions and their changes will appear here.";
            content.Controls.Add(releaseHeading, 0, 0); content.Controls.Add(notes, 0, 1);
            card.Controls.Add(content); root.Controls.Add(card, 0, 3);

            guidance.AccessibleName = "Update guidance"; guidance.AutoEllipsis = false;
            guidance.AutoSize = true; guidance.Dock = DockStyle.Top;
            guidance.Margin = new Padding(0, 12, 0, 4);
            guidance.Text = eligibilityError ?? UnsignedNotice;
            root.Controls.Add(guidance, 0, 4);
            var transfer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2,
                Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 8), Visible = false };
            transfer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            transfer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            transfer.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            progressText.AccessibleName = "Download progress"; progressText.Margin = Padding.Empty;
            progress.Dock = DockStyle.Fill; progress.Margin = Padding.Empty;
            progress.Minimum = 0; progress.Maximum = 100; progress.Visible = false;
            progress.AccessibleName = "Installer download progress";
            transfer.Controls.Add(progressText, 0, 0); transfer.Controls.Add(progress, 0, 1);
            progressText.TextChanged += delegate { transfer.Visible = progressText.Text.Length > 0; };

            var footer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 12, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var actionsRow = new RowStyle(SizeType.Absolute, 40); footer.RowStyles.Add(actionsRow);
            administratorPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2,
                Margin = Padding.Empty, Visible = false, AccessibleName = "Administrator support for updates" };
            administratorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            administratorStatus.AccessibleName = "Administrator support update status";
            administratorStatus.LiveSetting = AutomationLiveSetting.Polite;
            administratorStatus.AutoEllipsis = false; administratorStatus.AutoSize = true; administratorStatus.Dock = DockStyle.Top;
            administratorStatus.Margin = new Padding(0, 0, 0, 8);
            administratorSupport.AccessibleDescription = "Turn off to stop support and remove saved approval. Windows may ask for permission. Use Settings to turn support on.";
            administratorSupport.Margin = Padding.Empty;
            administratorSupport.CheckedChanged += delegate {
                if (readingAdministratorSwitch) return;
                if (administratorSupport.Checked) RefreshAdministratorStatus();
                else TurnOffAdministrator();
            };
            administratorPanel.Controls.Add(administratorSupport, 0, 0);
            administratorPanel.Controls.Add(administratorStatus, 0, 1);
            footer.Controls.Add(administratorPanel, 0, 0);
            footer.Controls.Add(transfer, 0, 1);
            releases = Ui.Button("View releases on GitHub", null, delegate { OpenReleases(); });
            releases.AccessibleName = "View official GitHub releases"; releases.Margin = new Padding(0, 0, 0, 8);
            footer.Controls.Add(releases, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true,
                FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty, Padding = Padding.Empty };
            close = Ui.Button("Close", null, delegate { if (downloading) CancelDownload(); else Close(); });
            close.AccessibleName = "Close updates";
            primary = Ui.Button("Download update", null, async delegate { if (verified != null) await InstallUpdate(); else await DownloadUpdate(); });
            primary.AccessibleName = "Download update"; primary.Enabled = false;
            check = Ui.Button("Check again", "refresh-cw", async delegate { await CheckForUpdates(); });
            check.AccessibleName = "Check for updates";
            actions.Controls.Add(close); actions.Controls.Add(primary); actions.Controls.Add(check);
            bool fittingActions = false;
            actions.Layout += delegate {
                if (fittingActions || actions.ClientSize.Width < 1) return;
                fittingActions = true;
                try {
                    int height = 0;
                    foreach (Control button in actions.Controls) height = Math.Max(height, button.Bottom + button.Margin.Bottom);
                    if (height > 0 && actionsRow.Height != height) actionsRow.Height = height;
                } finally { fittingActions = false; }
            };
            footer.Controls.Add(actions, 0, 3); shell.Controls.Add(footer, 0, 1);
            Controls.Add(shell); Ui.Theme(this); ApplyTextColors();
            root.Layout += delegate {
                int width = Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal);
                state.MaximumSize = new Size(width, 0); guidance.MaximumSize = new Size(width, 0);
            };
            administratorPanel.Layout += delegate {
                administratorStatus.MaximumSize = new Size(Math.Max(1, administratorPanel.ClientSize.Width), 0);
            };
            CancelButton = close;
            Shown += async delegate {
                if (started) return;
                FitToWorkingArea();
                started = true; await CheckForUpdates();
            };
            Activated += delegate { if (!closing && !IsDisposed) { Ui.Theme(this); ApplyTextColors(); } };
        }

        void ApplyTextColors()
        {
            current.ForeColor = Ui.Muted; guidance.ForeColor = Ui.Muted; progressText.ForeColor = Ui.Muted;
            state.ForeColor = Ui.Text;
        }

        void FitToWorkingArea()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            if (area.Width < 1 || area.Height < 1) return;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }

        protected override void OnSystemColorsChanged(EventArgs e)
        {
            base.OnSystemColorsChanged(e);
            if (close == null || closing || IsDisposed) return;
            Ui.Theme(this); ApplyTextColors();
        }

        async Task CheckForUpdates()
        {
            if (busy || closing || requestingAdministratorOff) return;
            ReleaseDownload(); release = null;
            CancellationTokenSource source = BeginOperation(false);
            state.Text = "Checking for updates...";
            notes.Text = "Checking GitHub releases..."; releaseHeading.Text = "Release notes";
            progressText.Text = ""; progress.Visible = false;
            guidance.Text = eligibilityError ?? UnsignedNotice;
            try {
                AppUpdateCheckResult result = await Task.Run(() => service.Check(Application.ProductVersion, source.Token));
                if (!CanPresent(source)) return;
                source.Token.ThrowIfCancellationRequested();
                if (result.Status == AppUpdateCheckStatus.Available && result.Release != null) {
                    release = result.Release;
                    state.Text = "Version " + release.Version + " is available.";
                    releaseHeading.Text = "What is new in " + release.Version;
                    notes.Text = String.IsNullOrWhiteSpace(release.Notes) ? "No release notes were provided." : release.Notes;
                    guidance.Text = eligibilityError ?? UnsignedNotice + "\r\n" + SettingsNotice;
                } else {
                    state.Text = String.IsNullOrWhiteSpace(result.Message) ?
                        (result.Status == AppUpdateCheckStatus.UpToDate ? "You are up to date." : "Updates are unavailable right now. Try again later or visit the official releases page.") : result.Message;
                    notes.Text = result.Status == AppUpdateCheckStatus.UpToDate ? "You already have the newest available version." :
                        "You can still use Taskbar Select normally. Check your connection and try again, or view the official releases page.";
                }
            } catch (OperationCanceledException) {
                if (CanPresent(source)) { state.Text = "Update check cancelled."; notes.Text = "Check again when you are ready."; }
            } catch (Exception) {
                if (CanPresent(source)) {
                    state.Text = "Could not check for updates.";
                    notes.Text = "Try again later or view the official releases page.";
                }
            } finally { EndOperation(source); }
        }

        async Task DownloadUpdate()
        {
            if (busy || closing || requestingAdministratorOff || release == null || eligibilityError != null) return;
            ReleaseDownload();
            CancellationTokenSource source = BeginOperation(true);
            int downloadGeneration = generation;
            state.Text = "Downloading version " + release.Version + "...";
            progress.Style = ProgressBarStyle.Continuous; progress.Value = 0; progress.Visible = true;
            progressText.Text = "Starting download...";
            VerifiedAppUpdate downloaded = null;
            try {
                AppUpdateRelease selected = release;
                long lastProgress = 0;
                downloaded = await Task.Run(() => service.Download(selected, item => {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    long previous = Interlocked.Read(ref lastProgress);
                    bool finished = item.TotalBytes > 0 && item.BytesReceived >= item.TotalBytes;
                    if (!finished && previous != 0 && now - previous < System.Diagnostics.Stopwatch.Frequency / 10) return;
                    Interlocked.Exchange(ref lastProgress, now);
                    PresentProgress(source, downloadGeneration, item);
                }, source.Token));
                if (!CanPresent(source)) return;
                source.Token.ThrowIfCancellationRequested();
                verified = downloaded; downloaded = null;
                state.Text = "Version " + verified.Version + " is downloaded and verified.";
                progress.Value = 100;
                progressText.Text = "Download verified against the release checksum.";
                guidance.Text = UnsignedNotice + "\r\n" + SettingsNotice;
            } catch (OperationCanceledException) {
                if (CanPresent(source)) { state.Text = "Download cancelled."; progressText.Text = "You can download the update again."; progress.Visible = false; }
            } catch (AppUpdateException error) {
                if (CanPresent(source)) {
                    state.Text = error.Message;
                    progressText.Text = "No installer was started.";
                    progress.Visible = false;
                }
            } catch (Exception) {
                if (CanPresent(source)) {
                    state.Text = "Could not download and verify the update.";
                    progressText.Text = "No installer was started. Try again or visit GitHub releases.";
                    progress.Visible = false;
                }
            } finally {
                if (downloaded != null) downloaded.Dispose();
                EndOperation(source);
            }
        }

        void PresentProgress(CancellationTokenSource source, int downloadGeneration, AppUpdateProgress item)
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try {
                BeginInvoke((MethodInvoker)delegate {
                    if (!CanPresent(source) || source.IsCancellationRequested || generation != downloadGeneration || !downloading) return;
                    progress.Value = ProgressPercentage(item.BytesReceived, item.TotalBytes);
                    progressText.Text = FormatTransfer(item.BytesReceived, item.TotalBytes);
                });
            } catch (InvalidOperationException) { }
        }

        internal static int ProgressPercentage(long received, long total)
        {
            if (received <= 0 || total <= 0) return 0;
            if (received >= total) return 100;
            return (int)Math.Max(0, Math.Min(100, received / (double)total * 100));
        }

        internal static string FormatTransfer(long received, long total)
        {
            double megabyte = 1024d * 1024d;
            return (Math.Max(0, received) / megabyte).ToString("0.0") + " of " +
                (Math.Max(0, total) / megabyte).ToString("0.0") + " MB downloaded";
        }

        async Task InstallUpdate()
        {
            if (busy || closing || verified == null) return;
            string unavailable = AppUpdateInstaller.InstallError(controller);
            if (unavailable != null) { ShowInstallBlock(unavailable); return; }
            VerifiedAppUpdate pending = verified; verified = null;
            CancellationTokenSource source = BeginOperation(false);
            state.Text = "Checking the installer before opening Setup...";
            try {
                await Task.Run(() => pending.VerifyForLaunch());
                if (!CanPresent(source)) return;
                unavailable = AppUpdateInstaller.InstallError(controller);
                if (!String.IsNullOrEmpty(unavailable)) {
                    verified = pending; pending = null; ShowInstallBlock(unavailable); return;
                }
                DialogResult choice = MessageBox.Show(this,
                    "Install Taskbar Select " + pending.Version + " now?\r\n\r\n" +
                    "Setup will close Taskbar Select while it updates. Your settings will be kept.\r\n\r\n" +
                    "The installer is unsigned. Windows may show a security warning.",
                    "Install update", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (!CanPresent(source)) return;
                if (choice != DialogResult.Yes) {
                    verified = pending; pending = null;
                    state.Text = "The update is ready when you are."; return;
                }
                AppUpdateInstaller.Launch(pending, controller);
                // Launch owns the descriptor and its file lease after success.
                pending = null;
                state.Text = "Setup is opening. Follow its instructions to finish updating.";
                Close();
            } catch (AppUpdateException error) {
                if (CanPresent(source)) {
                    if (AppUpdateInstaller.IsAdministratorError(error.Message)) {
                        verified = pending; pending = null; ShowInstallBlock(error.Message);
                    } else {
                        state.Text = error.Message;
                        progressText.Text = "If Setup did not open, download the update again.";
                        progress.Visible = false;
                    }
                }
            } catch (Exception) {
                if (CanPresent(source)) {
                    state.Text = "Could not start the update.";
                    progressText.Text = "Taskbar Select is still running. Download the update again or visit GitHub releases.";
                    progress.Visible = false;
                }
            } finally {
                if (pending != null) pending.Dispose();
                EndOperation(source);
            }
        }

        CancellationTokenSource BeginOperation(bool isDownload)
        {
            busy = true; downloading = isDownload; generation++;
            var source = new CancellationTokenSource(); operation = source;
            UpdateActions(); return source;
        }

        bool CanPresent(CancellationTokenSource source)
        { return !closing && !IsDisposed && !Disposing && Object.ReferenceEquals(operation, source); }

        void EndOperation(CancellationTokenSource source)
        {
            bool currentOperation = Object.ReferenceEquals(operation, source);
            if (currentOperation) { operation = null; busy = false; downloading = false; }
            source.Dispose();
            if (!closing && !IsDisposed && !Disposing && currentOperation) UpdateActions();
        }

        void UpdateActions()
        {
            primary.Text = verified == null ? "Download update" : "Install update";
            primary.AccessibleName = primary.Text;
            close.Text = downloading ? "Cancel download" : "Close";
            close.AccessibleName = close.Text;
            close.Enabled = true; releases.Enabled = !busy;
            RefreshAdministratorStatus();
        }

        internal static string AdministratorUpdateMessage(string block, string status, bool pending)
        {
            if (pending) return "Waiting for the Administrator support change to finish. Check for a Windows approval prompt.";
            if (block == null) return "Administrator support is off. You can turn it on in Settings.";
            if (status == "Off not confirmed")
                return "Administrator support must be off to install. Turning it off was not confirmed. Try again.";
            return block + " Windows may ask you to approve removing this build's saved permission.";
        }

        internal void RefreshAdministratorStatus()
        {
            if (closing || IsDisposed || Disposing) return;
            var permissions = controller.AdministratorPermissions;
            bool relevant = eligibilityError == null && release != null;
            bool pending = relevant && (requestingAdministratorOff || (permissions != null && permissions.Busy));
            string status = permissions == null ? null : permissions.Status;
            bool off = relevant && !pending && permissions != null && !permissions.Checked && status == "Off";
            if (!relevant) administratorBlock = null;
            else if (!off) administratorBlock = AppUpdateInstaller.ApprovalError;
            // Only probe the saved registration when the coordinator first
            // reports Off. The installer rechecks independently at launch.
            else if (!observedAdministratorOff) administratorBlock = AppUpdateInstaller.AdministratorError(controller);
            observedAdministratorOff = off;

            bool blocked = relevant && (pending || administratorBlock != null);
            string message = relevant ? AdministratorUpdateMessage(administratorBlock, status, pending) : "";
            bool changed = administratorStatus.Text != message;
            if (changed) {
                administratorStatus.Text = message;
                administratorStatus.AccessibleName = message;
            }
            administratorPanel.Visible = relevant;
            check.Enabled = !busy && !pending;
            primary.Enabled = !busy && !pending && relevant && (verified == null || !blocked);
            AcceptButton = primary.Enabled ? primary : verified == null && check.Enabled ? check : null;
            Control next = AdministratorFocusTarget(ActiveControl, administratorSupport, !blocked && primary.Enabled, primary);
            if (next != null && next.CanSelect) ActiveControl = next;
            readingAdministratorSwitch = true;
            try {
                administratorSupport.Busy = pending;
                administratorSupport.Checked = blocked;
                administratorSupport.Enabled = blocked && !busy && permissions != null;
            } finally { readingAdministratorSwitch = false; }
            if (changed && administratorPanel.Visible && administratorStatus.IsHandleCreated)
                administratorStatus.AccessibilityObject.RaiseLiveRegionChanged();
        }

        internal static Control AdministratorFocusTarget(Control selected, ToggleSwitch support, bool canProceed, Button updateAction)
        { return selected == support && canProceed ? updateAction : null; }

        void TurnOffAdministrator()
        {
            var permissions = controller.AdministratorPermissions;
            if (closing || busy || requestingAdministratorOff || eligibilityError != null || release == null ||
                administratorBlock == null || permissions == null || permissions.Busy || !administratorSupport.Enabled) {
                RefreshAdministratorStatus(); return;
            }
            requestingAdministratorOff = true;
            try {
                RefreshAdministratorStatus();
                permissions.Request(false);
            }
            finally {
                requestingAdministratorOff = false;
                observedAdministratorOff = false;
                RefreshAdministratorStatus();
            }
        }

        void ShowInstallBlock(string reason)
        {
            if (AppUpdateInstaller.IsAdministratorError(reason)) {
                administratorBlock = reason;
                if (verified != null) state.Text = "Version " + verified.Version + " is downloaded and verified.";
                RefreshAdministratorStatus();
            } else state.Text = reason;
        }

        void CancelDownload()
        {
            if (!downloading || operation == null) return;
            state.Text = "Cancelling download..."; close.Enabled = false;
            operation.Cancel();
        }

        void OpenReleases()
        {
            try { System.Diagnostics.Process.Start(OfficialReleases); }
            catch (Exception) { state.Text = "Could not open your browser. Visit the Taskbar Select releases page on GitHub."; }
        }

        void ReleaseDownload()
        {
            if (verified == null) return;
            verified.Dispose(); verified = null;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            closing = true; generation++;
            if (operation != null) operation.Cancel();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                closing = true;
                if (operation != null) operation.Cancel();
                ReleaseDownload(); windowIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
