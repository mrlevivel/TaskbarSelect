using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // All dimensions are monitor-local physical pixels, including work-area offsets.
    internal static class GhostSearchLayout
    {
        internal const int MaximumResults = 6;
        internal static Rectangle Fit(Rectangle work, int dpi, int bottom, int resultCount)
        {
            int margin = Math.Min(WindowShelfPolicy.Scale(8, dpi), Math.Max(0, (Math.Min(work.Width, work.Height) - 1) / 2));
            int width = Math.Max(1, Math.Min(WindowShelfPolicy.Scale(376, dpi), work.Width - margin * 2));
            int height = Math.Max(1, Math.Min(WindowShelfPolicy.Scale(92 + Math.Min(MaximumResults, Math.Max(0, resultCount)) * 38, dpi), work.Height - margin * 2));
            int x = work.Left + (work.Width - width) / 2;
            int y = Math.Max(work.Top + margin, Math.Min(bottom - height, work.Bottom - margin - height));
            return new Rectangle(x, y, width, height);
        }
        internal static Rectangle Reveal(Rectangle expanded, int dpi, double progress)
        {
            progress = Math.Max(0, Math.Min(1, progress));
            int initialWidth = Math.Min(expanded.Width, WindowShelfPolicy.Scale(72, dpi));
            int initialHeight = Math.Min(expanded.Height, WindowShelfPolicy.Scale(60, dpi));
            int width = Math.Max(1, (int)Math.Round(initialWidth + (expanded.Width - initialWidth) * progress));
            int height = Math.Max(1, (int)Math.Round(initialHeight + (expanded.Height - initialHeight) * progress));
            return new Rectangle(expanded.Left + (expanded.Width - width) / 2, expanded.Bottom - height, width, height);
        }
        internal static Rectangle Reveal(Rectangle expanded, Rectangle origin, int dpi, double progress)
        { return origin.IsEmpty ? Reveal(expanded, dpi, progress) : Interpolate(origin, expanded, progress); }
        internal static Rectangle ClampOrigin(Rectangle requested, Rectangle work)
        {
            if (requested.Width < 1 || requested.Height < 1) return Rectangle.Empty;
            int width = Math.Min(work.Width, requested.Width), height = Math.Min(work.Height, requested.Height);
            return new Rectangle(Math.Max(work.Left, Math.Min(requested.Left, work.Right - width)),
                Math.Max(work.Top, Math.Min(requested.Top, work.Bottom - height)), width, height);
        }
        internal static int ResultCapacity(Rectangle expanded, int dpi)
        { return Math.Min(MaximumResults, Math.Max(0, (expanded.Height - WindowShelfPolicy.Scale(92, dpi)) / WindowShelfPolicy.Scale(38, dpi))); }
        internal static Rectangle Interpolate(Rectangle from, Rectangle to, double progress)
        {
            progress = Math.Max(0, Math.Min(1, progress));
            return new Rectangle((int)Math.Round(from.X + (to.X - from.X) * progress), (int)Math.Round(from.Y + (to.Y - from.Y) * progress),
                Math.Max(1, (int)Math.Round(from.Width + (to.Width - from.Width) * progress)), Math.Max(1, (int)Math.Round(from.Height + (to.Height - from.Height) * progress)));
        }
        internal static Rectangle MonitorBounds(Display target)
        {
            if (target == null) throw new ArgumentNullException("target");
            return new Rectangle(target.X, target.Y, target.Width, target.Height);
        }
    }

    internal sealed class GhostSearchControlLayout
    {
        internal double Scale;
        internal bool ShowSearchIcon;
        internal Rectangle Input, Close, Results, Status;
        static int Scaled(int pixels, double scale) { return Math.Max(1, (int)Math.Round(pixels * scale)); }
        static Rectangle Clip(Rectangle bounds, Size size)
        {
            int x = Math.Max(0, Math.Min(bounds.X, size.Width - 1)), y = Math.Max(0, Math.Min(bounds.Y, size.Height - 1));
            return new Rectangle(x, y, Math.Max(1, Math.Min(bounds.Width, size.Width - x)), Math.Max(1, Math.Min(bounds.Height, size.Height - y)));
        }
        internal static GhostSearchControlLayout Create(Size size, int dpi, int resultCount)
        {
            int count = Math.Max(0, resultCount);
            double scale = Math.Min(Math.Max(96, dpi) / 96.0, size.Height / (double)(92 + count * 38));
            var layout = new GhostSearchControlLayout { Scale = scale };
            int closeWidth = Math.Min(Scaled(27, scale), Math.Max(1, size.Width / 4));
            int closeLeft = Math.Max(0, size.Width - Scaled(10, scale) - closeWidth);
            int inputRight = Math.Max(1, closeLeft - Scaled(9, scale));
            int inputLeft = Math.Min(Scaled(42, scale), Math.Max(1, inputRight - Scaled(40, scale)));
            layout.ShowSearchIcon = inputLeft >= Scaled(40, scale);
            layout.Input = Clip(new Rectangle(inputLeft, Scaled(22, scale), Math.Max(1, inputRight - inputLeft), Scaled(23, scale)), size);
            layout.Close = Clip(new Rectangle(closeLeft, Scaled(13, scale), closeWidth, Scaled(34, scale)), size);
            layout.Results = Clip(new Rectangle(Scaled(10, scale), Scaled(59, scale), Math.Max(1, size.Width - Scaled(20, scale)), count * Scaled(38, scale)), size);
            layout.Status = Clip(new Rectangle(Scaled(15, scale), Scaled(59, scale) + count * Scaled(38, scale), Math.Max(1, size.Width - Scaled(30, scale)), Scaled(25, scale)), size);
            return layout;
        }
    }

    // A focus request belongs to one explicit search invocation. A late timer
    // cannot revive it after dismissal, external focus or a newer invocation.
    internal sealed class GhostSearchFocusRetry
    {
        internal const int MaximumAttempts = 6, TimeoutMilliseconds = 300, RetryMilliseconds = 35;
        readonly Func<long> clock;
        long generation, deadline, next;
        int attempts;
        bool pending;
        IntPtr owner, previous;
        internal GhostSearchFocusRetry(Func<long> clock)
        { if (clock == null) throw new ArgumentNullException("clock"); this.clock = clock; }
        internal long Begin(IntPtr owner, IntPtr previous)
        {
            this.owner = owner; this.previous = previous; attempts = 0; next = clock();
            deadline = next > Int64.MaxValue - TimeoutMilliseconds ? Int64.MaxValue : next + TimeoutMilliseconds;
            pending = owner != IntPtr.Zero; return ++generation;
        }
        internal bool Pending(long token) { return pending && token == generation; }
        internal bool TryAttempt(long token, IntPtr foreground)
        {
            if (!Pending(token)) return false;
            long now = clock();
            if (now >= deadline || attempts >= MaximumAttempts || (foreground != owner && foreground != previous)) {
                Cancel(); return false;
            }
            if (now < next) return false;
            attempts++; next = now > Int64.MaxValue - RetryMilliseconds ? Int64.MaxValue : now + RetryMilliseconds;
            return true;
        }
        internal bool TryFocus(long token, Func<IntPtr> foreground, Action<IntPtr> claim, Action focus, Func<bool> focused)
        {
            if (!Pending(token)) return false;
            IntPtr target = owner, current = foreground();
            if (!TryAttempt(token, current) || !Pending(token)) return false;
            if (current != target) claim(target);
            if (!Pending(token) || foreground() != target) return false;
            focus();
            if (!Pending(token) || foreground() != target || !focused()) return false;
            Complete(token); return true;
        }
        internal void Complete(long token) { if (Pending(token)) Cancel(); }
        internal void Cancel() { pending = false; ++generation; }
        internal int Attempts { get { return attempts; } }
    }

    internal sealed class GhostSearchFocusApi
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        internal Func<IntPtr> Foreground = GetForegroundWindow;
        internal Func<IntPtr, bool> Claim = SetForegroundWindow;
        internal Func<Control, bool> Focus = delegate(Control control) { return control.Focus(); };
        internal Func<Control, bool> Focused = delegate(Control control) { return control.Focused; };
    }

    // The native Edit control can request its context menu on Apps-key up even
    // after WinForms handled key-down. Keep that follow-up from opening a
    // second menu. Pointer context requests still use the normal Edit menu.
    internal class GhostSearchInput : TextBox
    {
        internal Func<bool> KeyboardContextRequested;
        internal Action PointerContextRequested;
        bool pendingKeyboardContext;
        internal void OwnKeyboardContext() { pendingKeyboardContext = true; }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x007B) { // WM_CONTEXTMENU; (-1,-1) means keyboard.
                if (message.LParam == new IntPtr(-1)) {
                    bool handled = pendingKeyboardContext; pendingKeyboardContext = false;
                    if (handled || (KeyboardContextRequested != null && KeyboardContextRequested())) {
                        message.Result = IntPtr.Zero; return;
                    }
                } else {
                    pendingKeyboardContext = false;
                    if (PointerContextRequested != null) PointerContextRequested();
                }
            }
            base.WndProc(ref message);
        }
    }

    internal sealed class GhostSearchPopup : Form
    {
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, string text);

        readonly Rectangle workArea;
        readonly int dpi, bottom;
        readonly Action<GhostApp> select;
        readonly Action<GhostApp> openNewWindow;
        readonly Action closeRequested;
        readonly GhostSearchInput input;
        readonly SearchResults results;
        readonly Label status;
        readonly Button closeButton;
        readonly Font searchFont = new Font("Segoe UI", 10);
        readonly Font captionFont = new Font("Segoe UI", 8);
        Font compactSearchFont, compactCaptionFont;
        readonly Timer animation = new Timer { Interval = 15 };
        readonly Timer focusTimer = new Timer { Interval = 20 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly GhostSearchFocusRetry focusRetry;
        long focusToken;
        IntPtr openingForeground;
        internal GhostSearchFocusApi FocusApi = new GhostSearchFocusApi();
        readonly ShelfMotion motion = new ShelfMotion();
        readonly ShelfMotion resizeMotion = new ShelfMotion();
        List<GhostApp> catalog = new List<GhostApp>();
        Rectangle expanded, resizeFrom, dismissFrom, revealOrigin;
        bool catalogReady, busy, closedNotified, dismissing, animationAllowed, materialFailed, noResultSpace;
        bool layingOut, shown, resizing, focusTimerDisposed;
        double dismissVisibilityStart = 1;
        double controlScale = 1;
        bool showSearchIcon = true;
        string statusOverride;
        Action dismissCompleted;
        ContextMenuStrip resultMenu;
        GhostApp resultMenuSelection, resultMenuSnapshot;
        long resultMenuVersion;
        bool showingResultMenu, executingResultAction;

        internal GhostSearchPopup(Display target, int dpi, int bottom, Action<GhostApp> select, Action close, Action<GhostApp> openNewWindow = null)
            : this(target, GhostSearchLayout.MonitorBounds(target), dpi, bottom, select, close, openNewWindow) { }

        internal GhostSearchPopup(Display target, Rectangle workArea, int dpi, int bottom, Action<GhostApp> select, Action close, Action<GhostApp> openNewWindow = null)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (workArea.Width < 1 || workArea.Height < 1) throw new ArgumentException("A valid captured display work area is required.", "workArea");
            this.workArea = workArea; this.dpi = Math.Max(96, dpi); this.bottom = bottom;
            focusRetry = new GhostSearchFocusRetry(delegate { return clock.ElapsedMilliseconds; });
            this.select = select; this.openNewWindow = openNewWindow; closeRequested = close;
            Text = "Ghost Bar app search"; AccessibleName = "Search installed apps";
            AccessibleDescription = "Choose an app to open or bring to this display. Right-click a result for options. Escape closes search.";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); Font = searchFont; KeyPreview = true;
            BackColor = ShelfPaint.Surface(SystemInformation.HighContrast); ForeColor = ShelfPaint.Ink(SystemInformation.HighContrast);
            input = new GhostSearchInput { BorderStyle = BorderStyle.None, AutoSize = false, Font = searchFont, TabIndex = 0, MaxLength = 120,
                AccessibleName = "Search apps", AccessibleDescription = "Type an installed app name. Up and Down select a result. Enter opens it on this display." };
            input.KeyboardContextRequested = delegate { ShowKeyboardResultMenu(); return true; };
            input.PointerContextRequested = CloseResultMenu;
            input.TextChanged += delegate { if (!busy) statusOverride = null; RefreshResults(); };
            input.HandleCreated += delegate { SendMessage(input.Handle, 0x1501, new IntPtr(1), "Search apps"); }; // EM_SETCUEBANNER never changes the query.
            input.KeyDown += delegate(object sender, KeyEventArgs e) {
                CancelInputFocus();
                if (HandleSearchKey(e.KeyData)) { e.SuppressKeyPress = true; e.Handled = true; }
            };
            input.MouseDown += delegate { CancelInputFocus(); };
            results = new SearchResults(this.dpi) { Font = searchFont, TabIndex = 1, TabStop = true,
                AccessibleName = "Matching installed apps", AccessibleDescription = "Press Enter or click an app to open or bring it here. Right-click or press Shift+F10 for options." };
            results.MouseClick += delegate(object sender, MouseEventArgs e) {
                if (e.Button != MouseButtons.Left || busy || dismissing) return;
                int index = results.IndexFromPoint(e.Location);
                if (index != ListBox.NoMatches) {
                    results.SelectedIndex = index; SelectCurrent();
                }
            };
            results.RightButtonReleased = delegate(Point location) {
                int index = results.IndexFromPoint(location);
                if (index != ListBox.NoMatches && results.GetItemRectangle(index).Contains(location))
                    ShowResultMenu(index, location);
            };
            results.KeyDown += delegate(object sender, KeyEventArgs e) {
                CancelInputFocus();
                if (HandleSearchKey(e.KeyData)) { e.SuppressKeyPress = true; e.Handled = true; }
            };
            results.MouseDown += delegate { CancelInputFocus(); };
            status = new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Font = captionFont,
                AccessibleName = "App search status", AccessibleRole = AccessibleRole.StaticText };
            closeButton = new Button { Text = "\u00d7", FlatStyle = FlatStyle.Flat, Font = searchFont,
                TabIndex = 2, AccessibleName = "Close app search", AccessibleDescription = "Close search without opening an app.", Cursor = Cursors.Hand };
            closeButton.FlatAppearance.BorderSize = 0; closeButton.Click += delegate { RequestClose(); };
            closeButton.MouseDown += delegate { CancelInputFocus(); };
            status.MouseDown += delegate { CancelInputFocus(); };
            Controls.Add(input); Controls.Add(results); Controls.Add(status); Controls.Add(closeButton);
            animation.Tick += delegate { ApplyAnimation(); };
            focusTimer.Tick += delegate { TryInputFocus(focusToken); };
            ApplyTheme(); RefreshResults();
        }

        internal bool IsDismissing { get { return dismissing; } }
        internal bool Busy { get { return busy; } }
        internal bool OwnsWindow(IntPtr window)
        {
            return window != IntPtr.Zero && !IsDisposed &&
                ((IsHandleCreated && window == Handle) || (resultMenu != null && !resultMenu.IsDisposed &&
                resultMenu.Visible && resultMenu.IsHandleCreated && window == resultMenu.Handle));
        }
        internal bool ContainsScreenPoint(Point point)
        {
            if (IsDisposed) return false;
            if (resultMenu != null && !resultMenu.IsDisposed && resultMenu.Visible && resultMenu.Bounds.Contains(point)) return true;
            return Bounds.Contains(point) && (Region == null || Region.IsVisible(point.X - Left, point.Y - Top));
        }
        internal string Query { get { return input.Text; } set { input.Text = value ?? ""; } }
        internal int ResultCount { get { return results.Items.Count; } }
        internal int SelectedResult { get { return results.SelectedIndex; } }
        internal string StatusText { get { return status.Text; } }
        internal Rectangle ExpandedBounds { get { return expanded; } }
        internal bool AnimationEnabled { get { return animationAllowed && !materialFailed; } }
        internal bool AnimationRunning { get { return animation.Enabled; } }
        internal bool InputFocusPending { get { return focusRetry.Pending(focusToken); } }
        internal bool InputFocusVerified
        { get { return IsHandleCreated && !IsDisposed && FocusApi.Foreground() == Handle && FocusApi.Focused(input); } }
        internal bool RequestInputFocus()
        {
            if (IsDisposed || Disposing || dismissing || closedNotified || !Visible) return false;
            return BeginInputFocus(FocusApi.Foreground());
        }
        bool BeginInputFocus(IntPtr previous)
        {
            CancelInputFocus();
            if (IsDisposed || Disposing || dismissing || closedNotified || !Visible || !IsHandleCreated) return false;
            focusToken = focusRetry.Begin(Handle, previous);
            return TryInputFocus(focusToken);
        }
        bool TryInputFocus(long token)
        {
            if (!focusRetry.Pending(token)) return false;
            if (IsDisposed || Disposing || dismissing || closedNotified || !Visible || !IsHandleCreated) { CancelInputFocus(); return false; }
            try {
                // The shortcut/button is recent user input. Ask Windows to
                // activate normally; a rejected request is never bypassed.
                if (focusRetry.TryFocus(token, FocusApi.Foreground,
                    delegate(IntPtr window) { FocusApi.Claim(window); },
                    delegate { ActiveControl = input; FocusApi.Focus(input); },
                    delegate { return FocusApi.Focused(input); })) {
                    focusTimer.Stop(); return true;
                }
            } catch (Win32Exception) { if (token == focusToken) CancelInputFocus(); return false; }
            catch (InvalidOperationException) { if (token == focusToken) CancelInputFocus(); return false; }
            if (token != focusToken) return false;
            if (focusRetry.Pending(token)) focusTimer.Start(); else focusTimer.Stop();
            return false;
        }
        internal void CancelInputFocus()
        { if (focusRetry != null) focusRetry.Cancel(); if (!focusTimerDisposed) focusTimer.Stop(); }
        internal void SetRevealOrigin(Rectangle iconBounds)
        {
            if (!Visible && !dismissing && !IsDisposed) revealOrigin = GhostSearchLayout.ClampOrigin(iconBounds, workArea);
        }

        protected override CreateParams CreateParams
        { get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; } } // TOOLWINDOW, deliberately activatable on click.

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ReadAnimationPreference(); }
        protected override void OnHandleDestroyed(EventArgs e) { CancelInputFocus(); base.OnHandleDestroyed(e); }
        protected override void OnMouseDown(MouseEventArgs e) { CancelInputFocus(); base.OnMouseDown(e); }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x001A || message.Msg == 0x031A || message.Msg == 0x031E) {
                ReadAnimationPreference(); ApplyTheme();
                if (!AnimationEnabled && animation.Enabled) ApplyAnimation();
            }
        }
        void ReadAnimationPreference()
        {
            int enabled;
            animationAllowed = ShelfMotion.Allowed(SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0,
                ShelfAppearance.TransparencyEnabled(), SystemInformation.HighContrast);
        }
        void ApplyTheme()
        {
            bool contrast = SystemInformation.HighContrast;
            BackColor = ShelfPaint.Surface(contrast); ForeColor = ShelfPaint.Ink(contrast);
            input.BackColor = BackColor; input.ForeColor = ForeColor;
            results.BackColor = BackColor; results.ForeColor = ForeColor;
            status.BackColor = BackColor; status.ForeColor = contrast ? SystemColors.WindowText : Ui.Muted;
            closeButton.BackColor = BackColor; closeButton.ForeColor = ForeColor;
            closeButton.FlatAppearance.MouseOverBackColor = contrast ? SystemColors.Highlight : Ui.Background;
            closeButton.FlatAppearance.MouseDownBackColor = contrast ? SystemColors.Highlight : Ui.Line;
            UpdateShape(); Invalidate(true);
        }

        internal void SetCatalog(List<GhostApp> apps)
        {
            if (IsDisposed || dismissing) return;
            CloseResultMenu();
            catalog = apps == null ? new List<GhostApp>() : new List<GhostApp>(apps);
            catalogReady = true;
            if (!busy) statusOverride = null;
            RefreshResults();
        }
        internal void SetStatus(string message, bool isBusy)
        {
            if (IsDisposed || dismissing) return;
            if (isBusy) CloseResultMenu();
            busy = isBusy; statusOverride = String.IsNullOrWhiteSpace(message) ? null : message;
            results.Enabled = !busy; RefreshStatus();
        }
        void RefreshResults()
        {
            CloseResultMenu();
            string selected = results.SelectedItem is GhostApp ? ((GhostApp)results.SelectedItem).Id : null;
            Rectangle previousBounds = expanded;
            var matches = String.IsNullOrWhiteSpace(input.Text) ? new List<GhostApp>() : GhostAppSearch.Find(catalog, input.Text, GhostSearchLayout.MaximumResults);
            expanded = GhostSearchLayout.Fit(workArea, dpi, bottom, matches.Count);
            int capacity = GhostSearchLayout.ResultCapacity(expanded, dpi);
            noResultSpace = matches.Count > 0 && capacity == 0;
            matches = matches.Take(capacity).ToList();
            results.BeginUpdate();
            try {
                results.Items.Clear(); foreach (var app in matches) results.Items.Add(app);
                int index = selected == null ? -1 : matches.FindIndex(app => String.Equals(app.Id, selected, StringComparison.OrdinalIgnoreCase));
                results.SelectedIndex = matches.Count == 0 ? -1 : Math.Max(0, index);
            } finally { results.EndUpdate(); }
            results.Visible = matches.Count > 0;
            LayoutChildren(); RefreshStatus();
            if (Visible && AnimationEnabled && !motion.Active && previousBounds != expanded) {
                resizeFrom = Bounds; resizing = true;
                resizeMotion.Reset(false); resizeMotion.Begin(true, clock.ElapsedMilliseconds, true); animation.Start();
                ApplyAnimation();
            } else if (!animation.Enabled) Bounds = expanded;
            else ApplyAnimation();
            Invalidate();
        }
        void RefreshStatus()
        {
            status.Text = statusOverride ?? (!catalogReady ? "Loading installed apps\u2026" : String.IsNullOrWhiteSpace(input.Text) ?
                "Search installed apps to open on this display" : noResultSpace ? "Not enough space to show app results" :
                results.Items.Count == 0 ? "No matching installed apps" : "Enter to open here  \u00b7  Right-click for options");
            status.AccessibleDescription = status.Text;
        }
        int Px(int pixels) { return WindowShelfPolicy.Scale(pixels, dpi); }
        int Ux(int pixels) { return Math.Max(1, (int)Math.Round(pixels * controlScale)); }
        void LayoutChildren()
        {
            if (layingOut) return;
            layingOut = true;
            try {
                var layout = GhostSearchControlLayout.Create(expanded.Size, dpi, results.Items.Count);
                controlScale = layout.Scale; showSearchIcon = layout.ShowSearchIcon;
                FitFonts(controlScale);
                input.Bounds = layout.Input; closeButton.Bounds = layout.Close;
                results.Bounds = layout.Results; status.Bounds = layout.Status;
            } finally { layingOut = false; }
        }
        void FitFonts(double scale)
        {
            bool compact = scale < dpi / 96.0 - .001;
            float searchPixels = (float)Math.Max(6, 14 * scale), captionPixels = (float)Math.Max(6, 11 * scale);
            if (!compact && compactSearchFont == null && compactCaptionFont == null) return;
            if (compact && compactSearchFont != null && compactCaptionFont != null &&
                Math.Abs(compactSearchFont.Size - searchPixels) < .01 && Math.Abs(compactCaptionFont.Size - captionPixels) < .01) return;
            Font previousSearch = compactSearchFont, previousCaption = compactCaptionFont;
            compactSearchFont = compact ? new Font("Segoe UI", searchPixels, FontStyle.Regular, GraphicsUnit.Pixel) : null;
            compactCaptionFont = compact ? new Font("Segoe UI", captionPixels, FontStyle.Regular, GraphicsUnit.Pixel) : null;
            input.Font = closeButton.Font = results.Font = compactSearchFont ?? searchFont;
            status.Font = compactCaptionFont ?? captionFont;
            if (previousSearch != null) previousSearch.Dispose();
            if (previousCaption != null) previousCaption.Dispose();
        }

        internal bool HandleSearchKey(Keys key)
        {
            if (key == Keys.Escape) { RequestClose(); return true; }
            if (dismissing) return key == Keys.Enter || key == Keys.Up || key == Keys.Down;
            if (key == Keys.Apps || key == (Keys.Shift | Keys.F10)) {
                input.OwnKeyboardContext();
                ShowKeyboardResultMenu();
                return true;
            }
            if (key == Keys.Enter) { if (closeButton.Focused) RequestClose(); else SelectCurrent(); return true; }
            if (key != Keys.Up && key != Keys.Down) return false;
            if (results.Items.Count > 0 && !busy) {
                int next = results.SelectedIndex + (key == Keys.Down ? 1 : -1);
                results.SelectedIndex = Math.Max(0, Math.Min(results.Items.Count - 1, next));
            }
            return true;
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        { CancelInputFocus(); return HandleSearchKey(keyData) || base.ProcessCmdKey(ref message, keyData); }
        void SelectCurrent()
        {
            if (busy || dismissing || closedNotified || !(results.SelectedItem is GhostApp)) return;
            CloseResultMenu();
            var selected = (GhostApp)results.SelectedItem;
            if (select != null) select(selected);
        }
        static bool SameResult(GhostApp a, GhostApp b)
        {
            return a != null && b != null && a.CatalogShortcut == b.CatalogShortcut &&
                String.Equals(a.Id, b.Id, StringComparison.Ordinal) && String.Equals(a.AppKey, b.AppKey, StringComparison.Ordinal) &&
                String.Equals(a.LaunchPath, b.LaunchPath, StringComparison.Ordinal) && String.Equals(a.PackageId, b.PackageId, StringComparison.Ordinal) &&
                String.Equals(a.TargetPath, b.TargetPath, StringComparison.Ordinal) && String.Equals(a.Arguments, b.Arguments, StringComparison.Ordinal) &&
                String.Equals(a.WorkingDirectory, b.WorkingDirectory, StringComparison.Ordinal) && String.Equals(a.ShortcutAppId, b.ShortcutAppId, StringComparison.Ordinal);
        }
        // Preparing a menu never opens an app. Each action belongs to this exact
        // catalog item and is invalidated by refresh, query change or dismissal.
        internal ContextMenuStrip PrepareResultMenu(int index)
        {
            CloseResultMenu(); CancelInputFocus();
            if (IsDisposed || busy || dismissing || closedNotified || index < 0 || index >= results.Items.Count) return null;
            var selected = results.Items[index] as GhostApp;
            if (selected == null) return null;
            results.SelectedIndex = index;
            var captured = selected.Copy(); long version = ++resultMenuVersion;
            var menu = new ContextMenuStrip { ShowImageMargin = false, Font = searchFont,
                AccessibleName = "App search options" };
            if (!SystemInformation.HighContrast) {
                menu.BackColor = BackColor; menu.ForeColor = ForeColor;
                menu.Renderer = new ToolStripProfessionalRenderer(new SearchMenuColors(BackColor));
            }
            resultMenu = menu; resultMenuSelection = selected; resultMenuSnapshot = captured;
            Action<bool> invoke = delegate(bool newWindow) {
                if (IsDisposed || busy || dismissing || closedNotified || resultMenu != menu || version != resultMenuVersion ||
                    !Object.ReferenceEquals(results.SelectedItem, selected) || !SameResult(selected, captured) ||
                    (newWindow && (openNewWindow == null || !GhostAppCatalog.CanOpenNewWindow(captured)))) return;
                executingResultAction = true;
                try {
                    CloseResultMenu();
                    if (newWindow) openNewWindow(captured); else if (select != null) select(captured);
                } finally { executingResultAction = false; }
            };
            var bring = menu.Items.Add("Open or bring here"); bring.AccessibleName = bring.Text;
            bring.Click += delegate { invoke(false); };
            if (openNewWindow != null && GhostAppCatalog.CanOpenNewWindow(captured)) {
                var fresh = menu.Items.Add("Open new window"); fresh.AccessibleName = fresh.Text;
                fresh.ToolTipText = "Open a separate window on this display";
                fresh.Click += delegate { invoke(true); };
            }
            menu.Closed += delegate(object sender, ToolStripDropDownClosedEventArgs e) {
                // WinForms may close the menu before raising Item.Click.
                if (resultMenu != menu || e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) return;
                CloseResultMenu();
                if (shown && !busy && !dismissing && !executingResultAction && IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)delegate {
                        // A catalog refresh must not suppress an external-focus
                        // dismissal. A replacement live menu remains protected
                        // by its exact current ownership, never a process match.
                        if (!IsDisposed && !dismissing && !busy && !showingResultMenu &&
                            !executingResultAction && !OwnsWindow(FocusApi.Foreground())) RequestClose();
                    });
            };
            return menu;
        }
        void ShowKeyboardResultMenu()
        {
            CancelInputFocus();
            if (IsDisposed || busy || dismissing || closedNotified) return;
            int index = results.SelectedIndex;
            if (index < 0 || index >= results.Items.Count) return;
            var selected = results.Items[index] as GhostApp;
            // Command preprocessing, KeyDown and WM_CONTEXTMENU may describe
            // the same gesture. Reuse its exact binding instead of reopening.
            if (resultMenu != null && !resultMenu.IsDisposed && Object.ReferenceEquals(resultMenuSelection, selected) &&
                SameResult(selected, resultMenuSnapshot)) return;
            Rectangle row = results.GetItemRectangle(index);
            ShowResultMenu(index, new Point(row.Left + Ux(12), row.Bottom));
        }
        void ShowResultMenu(int index, Point location)
        {
            var menu = PrepareResultMenu(index);
            if (menu == null || !Visible) return;
            showingResultMenu = true;
            try { menu.Show(results, location); }
            finally { showingResultMenu = false; }
        }
        void CloseResultMenu()
        {
            ++resultMenuVersion;
            var menu = resultMenu; resultMenu = null;
            resultMenuSelection = resultMenuSnapshot = null;
            if (menu == null) return;
            menu.Close(); menu.Dispose();
        }
        sealed class SearchMenuColors : ProfessionalColorTable
        {
            readonly Color surface;
            internal SearchMenuColors(Color surface) { this.surface = surface; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return surface; } }
            public override Color MenuBorder { get { return Ui.Line; } }
            public override Color MenuItemBorder { get { return Ui.Accent; } }
            public override Color MenuItemSelected { get { return Ui.Dark ? Color.FromArgb(51, 73, 68) : Color.FromArgb(225, 243, 237); } }
        }
        void RequestClose()
        {
            if (closedNotified || IsDisposed) return;
            CancelInputFocus();
            CloseResultMenu();
            closedNotified = true;
            if (closeRequested != null) closeRequested(); else BeginDismiss(null);
        }
        protected override void OnDeactivate(EventArgs e)
        {
            CancelInputFocus();
            base.OnDeactivate(e);
            // A launched app taking foreground is expected; don't cancel its pending placement.
            if (shown && !busy && !dismissing && !showingResultMenu && !executingResultAction &&
                (resultMenu == null || !resultMenu.Visible)) RequestClose();
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!dismissing && !closedNotified && e.CloseReason == CloseReason.UserClosing) {
                e.Cancel = true; RequestClose();
            }
            base.OnFormClosing(e);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e); shown = true;
            if (!dismissing) BeginInputFocus(openingForeground);
        }
        protected override void SetVisibleCore(bool value)
        {
            if (value && !Visible && !dismissing) {
                openingForeground = FocusApi.Foreground();
                if (!IsHandleCreated) CreateHandle();
                resizing = false;
                motion.Reset(false); motion.Begin(true, clock.ElapsedMilliseconds, AnimationEnabled);
                ApplyAnimation();
            }
            base.SetVisibleCore(value);
            if (!value) CancelInputFocus();
            if (value && !dismissing && motion.Active) animation.Start();
        }
        internal void BeginDismiss(Action completed)
        {
            if (dismissing) return;
            CancelInputFocus();
            CloseResultMenu();
            dismissing = true; closedNotified = true; dismissCompleted = completed;
            if (IsDisposed || !Visible) { FinishDismiss(); return; }
            resizing = false; dismissFrom = Bounds; dismissVisibilityStart = Math.Max(.001, motion.Value(clock.ElapsedMilliseconds));
            input.ReadOnly = true; results.Enabled = false; closeButton.Enabled = false;
            motion.Begin(false, clock.ElapsedMilliseconds, AnimationEnabled);
            if (motion.Active) animation.Start();
            ApplyAnimation();
        }
        void FinishDismiss()
        {
            animation.Stop(); var completed = dismissCompleted; dismissCompleted = null;
            if (!IsDisposed) Hide();
            if (completed != null) completed();
        }
        void ApplyAnimation()
        {
            if (IsDisposed || Disposing) { animation.Stop(); return; }
            double progress = AnimationEnabled ? motion.Value(clock.ElapsedMilliseconds) : dismissing ? 0 : 1;
            if (dismissing && (progress <= 0 || !motion.Active)) { FinishDismiss(); return; }
            try {
                Rectangle requested = dismissing ? dismissFrom : expanded;
                if (resizing && !dismissing && AnimationEnabled) {
                    requested = GhostSearchLayout.Interpolate(resizeFrom, expanded, resizeMotion.Value(clock.ElapsedMilliseconds));
                    resizing = resizeMotion.Active;
                }
                Bounds = motion.Active || dismissing ? GhostSearchLayout.Reveal(requested, revealOrigin, dpi, dismissing ? progress / dismissVisibilityStart : progress) : requested;
                Opacity = AnimationEnabled ? Math.Max(0.04, progress) : 1;
            } catch (Win32Exception) { UseOpaqueFallback(); }
            catch (ExternalException) { UseOpaqueFallback(); }
            if ((!motion.Active && !resizing) || !AnimationEnabled) animation.Stop();
        }
        void UseOpaqueFallback()
        {
            materialFailed = true; resizing = false; animation.Stop();
            if (dismissing) { FinishDismiss(); return; }
            try { Opacity = 1; } catch (Win32Exception) { } catch (ExternalException) { }
            Bounds = expanded;
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e); UpdateShape();
            // Windows otherwise keeps old client pixels while an expanding popup
            // exposes only narrow strips, preserving the previous rounded border.
            Invalidate(true);
        }
        void UpdateShape()
        {
            if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
            Region previous = Region;
            if (SystemInformation.HighContrast) Region = null;
            else using (var path = ShelfPaint.Round(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), Px(10))) Region = new Region(path);
            if (previous != null) previous.Dispose();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 2 || Height < 2) return;
            var graphics = e.Graphics; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool contrast = SystemInformation.HighContrast;
            using (var line = new Pen(ShelfPaint.Border(contrast))) {
                if (contrast) graphics.DrawRectangle(line, 0, 0, Width - 1, Height - 1);
                else using (var outline = ShelfPaint.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Px(10))) graphics.DrawPath(line, outline);
            }
            using (var line = new Pen(contrast ? SystemColors.WindowText : Ui.Line)) graphics.DrawLine(line, Ux(14), Ux(56), Math.Max(Ux(14), expanded.Width - Ux(14)), Ux(56));
            if (showSearchIcon) DrawSearchIcon(graphics, new Rectangle(Ux(17), Ux(25), Ux(16), Ux(16)), contrast ? SystemColors.WindowText : Ui.Accent, Math.Max(1, (int)Math.Round(controlScale * 96)));
        }
        internal static void DrawSearchIcon(Graphics graphics, Rectangle bounds, Color color, int dpi)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float stroke = Math.Max(1.4f, dpi / 96f * 1.6f);
            using (var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                float diameter = Math.Min(bounds.Width, bounds.Height) * .66f;
                graphics.DrawEllipse(pen, bounds.X, bounds.Y, diameter, diameter);
                graphics.DrawLine(pen, bounds.X + diameter * .84f, bounds.Y + diameter * .84f, bounds.Right - stroke, bounds.Bottom - stroke);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { CancelInputFocus(); CloseResultMenu(); animation.Stop(); dismissCompleted = null; }
            base.Dispose(disposing);
            if (disposing) {
                focusTimerDisposed = true; focusTimer.Dispose(); animation.Dispose(); searchFont.Dispose(); captionFont.Dispose();
                if (compactSearchFont != null) compactSearchFont.Dispose(); if (compactCaptionFont != null) compactCaptionFont.Dispose();
            }
        }

        sealed class SearchResults : ListBox
        {
            readonly int dpi;
            internal Action<Point> RightButtonReleased;
            internal SearchResults(int dpi)
            {
                this.dpi = dpi; DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None;
                IntegralHeight = false; ItemHeight = WindowShelfPolicy.Scale(38, dpi);
            }
            protected override void WndProc(ref Message message)
            {
                const int RightButtonUp = 0x0205;
                bool rightButtonUp = message.Msg == RightButtonUp;
                IntPtr window = message.HWnd;
                IntPtr position = message.LParam;
                base.WndProc(ref message);
                // ListBox raises MouseClick for the left button only. Let the
                // framework release mouse capture before opening the menu.
                if (!rightButtonUp || IsDisposed || Disposing || !IsHandleCreated || Handle != window || RightButtonReleased == null) return;
                int packed = unchecked((int)position.ToInt64());
                RightButtonReleased(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
            }
            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count) return;
                bool contrast = SystemInformation.HighContrast, selected = (e.State & DrawItemState.Selected) != 0;
                Color fill = contrast ? selected ? SystemColors.Highlight : SystemColors.Window : selected ? Ui.Dark ? Color.FromArgb(51, 73, 68) : Color.FromArgb(225, 243, 237) : BackColor;
                Color ink = contrast ? selected ? SystemColors.HighlightText : SystemColors.WindowText : ForeColor;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var background = new SolidBrush(BackColor)) e.Graphics.FillRectangle(background, e.Bounds);
                var tile = new Rectangle(e.Bounds.X, e.Bounds.Y + 1, Math.Max(1, e.Bounds.Width - 1), Math.Max(1, e.Bounds.Height - 2));
                using (var brush = new SolidBrush(fill)) {
                    if (contrast) e.Graphics.FillRectangle(brush, tile);
                    else using (var path = ShelfPaint.Round(tile, WindowShelfPolicy.Scale(5, dpi))) e.Graphics.FillPath(brush, path);
                }
                var app = Items[e.Index] as GhostApp;
                int inset = WindowShelfPolicy.Scale(12, dpi);
                ShelfPaint.Text(e.Graphics, app == null ? "" : app.Name, Font,
                    new Rectangle(e.Bounds.Left + inset, e.Bounds.Top, Math.Max(1, e.Bounds.Width - inset * 2 - WindowShelfPolicy.Scale(24, dpi)), e.Bounds.Height), ink, false);
                if (selected) ShelfPaint.Text(e.Graphics, "\u2197", Font,
                    new Rectangle(e.Bounds.Right - WindowShelfPolicy.Scale(30, dpi), e.Bounds.Top, WindowShelfPolicy.Scale(20, dpi), e.Bounds.Height), contrast ? ink : Ui.Accent, true);
                if ((e.State & DrawItemState.Focus) != 0 && contrast) e.DrawFocusRectangle();
            }
        }
    }
}
