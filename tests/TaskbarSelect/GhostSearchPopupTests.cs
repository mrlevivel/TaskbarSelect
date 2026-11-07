using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class GhostSearchPopupTests
    {
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendListMessage(IntPtr window, uint message, IntPtr parameter, IntPtr position);

        static void ReleaseRightButton(ListBox results, Point point)
        {
            const uint RightButtonUp = 0x0205;
            int position = (point.Y << 16) | (point.X & 0xFFFF);
            SendListMessage(results.Handle, RightButtonUp, IntPtr.Zero, new IntPtr(position));
        }

        static Display Target(Rectangle bounds)
        { return new Display { Device = "test-display", Identity = "ghost-search-neutral", Name = "Neutral monitor", X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height }; }
        static GhostApp App(string id, string name)
        { return new GhostApp { Id = id, Name = name, AppKey = "exe:neutral-" + id, LaunchPath = "C:\\Neutral\\" + id + ".lnk" }; }

        internal static void Run(Action<bool, string> assert)
        {
            RunResultActions(assert);
            FocusRetryChecks(assert);
            FocusLifecycleChecks(assert);
            var workAreas = new[] {
                new Rectangle(0, 0, 1920, 1040), new Rectangle(-1600, -1080, 1600, 1032),
                new Rectangle(60, -1080, 1860, 1080), new Rectangle(-1080, 48, 1080, 1872),
                new Rectangle(20, 30, 160, 160), new Rectangle(-1, -1, 1, 1)
            };
            foreach (Rectangle work in workAreas) foreach (int dpi in new[] { 96, 120, 144, 192, 240 })
            foreach (int count in new[] { -1, 0, 1, 6, 8, 100 }) {
                Rectangle bounds = GhostSearchLayout.Fit(work, dpi, work.Bottom + 80, count);
                assert(bounds.Width > 0 && bounds.Height > 0 && work.Contains(bounds), "Search panel fits captured negative/stacked/sidebar/minimum work area at " + dpi + " DPI");
                int capacity = GhostSearchLayout.ResultCapacity(bounds, dpi);
                assert(capacity >= 0 && capacity <= GhostSearchLayout.MaximumResults, "Search result capacity is bounded by monitor space");
                foreach (double progress in new[] { 0.0, .1, .5, 1.0 }) {
                    Rectangle visual = GhostSearchLayout.Reveal(bounds, dpi, progress);
                    assert(bounds.Contains(visual) && visual.Bottom == bounds.Bottom, "Search reveal stays inside captured work area and grows from bottom");
                }
                assert(GhostSearchLayout.Reveal(bounds, dpi, 1) == bounds, "Search reveal finishes at complete layout");
                assert(GhostSearchLayout.Reveal(bounds, dpi, -1) == GhostSearchLayout.Reveal(bounds, dpi, 0), "Search reveal clamps negative progress");
                assert(GhostSearchLayout.Reveal(bounds, dpi, 3) == bounds, "Search reveal clamps completion progress");
                Rectangle compact = GhostSearchLayout.Fit(work, dpi, work.Bottom + 80, 0);
                assert(GhostSearchLayout.Interpolate(compact, bounds, 0) == compact && GhostSearchLayout.Interpolate(compact, bounds, 1) == bounds,
                    "Result geometry transition preserves complete start and end rectangles");
                foreach (double progress in new[] { .1, .5, .9 })
                    assert(work.Contains(GhostSearchLayout.Interpolate(compact, bounds, progress)), "Interpolated result expansion remains within display work area");
                var icon = GhostSearchLayout.ClampOrigin(new Rectangle(work.Right - 20, work.Bottom - 20, 40, 40), work);
                assert(work.Contains(icon), "Reveal origin at taskbar edge is clamped inside captured work area");
                foreach (double progress in new[] { 0.0, .1, .5, 1.0 })
                    assert(work.Contains(GhostSearchLayout.Reveal(bounds, icon, dpi, progress)), "Icon-to-panel animation stays on captured display");
                assert(GhostSearchLayout.Reveal(bounds, icon, dpi, 0) == icon && GhostSearchLayout.Reveal(bounds, icon, dpi, 1) == bounds,
                    "Search opening begins at clicked icon and ends at full search panel");
                var childLayout = GhostSearchControlLayout.Create(bounds.Size, dpi, Math.Min(count, capacity));
                Rectangle client = new Rectangle(Point.Empty, bounds.Size);
                assert(client.Contains(childLayout.Input) && client.Contains(childLayout.Close) && client.Contains(childLayout.Results) && client.Contains(childLayout.Status),
                    "Search input, close, results and status bounds fit small/high-DPI panel");
            }
            var motion = new ShelfMotion(); motion.Reset(false); motion.Begin(true, 100, true);
            assert(motion.Value(100) == 0 && motion.Active, "Search opening begins transparent before first visible frame");
            double half = motion.Value(100 + ShelfMotion.RevealMilliseconds / 2);
            assert(half > .5 && half < 1, "Search reveal uses short ease-out motion");
            assert(motion.Value(100 + ShelfMotion.RevealMilliseconds) == 1 && !motion.Active, "Search opening finishes within its fixed short duration");
            motion.Begin(false, 300, true);
            assert(motion.Dismissing && motion.Value(300 + ShelfMotion.DismissMilliseconds) == 0 && !motion.Active, "Search dismissal completes in its short duration");
            motion.Reset(false); motion.Begin(true, 500, false);
            assert(motion.Value(500) == 1 && !motion.Active, "Search motion disabled preference reveals immediately");
            assert(!ShelfMotion.Allowed(false, true, false) && !ShelfMotion.Allowed(true, true, true) && !ShelfMotion.Allowed(true, false, false),
                "Search animation respects reduced motion, high contrast, and opaque fallback");

            var displayBounds = new Rectangle(-1200, -900, 1200, 900);
            var exactWork = new Rectangle(-1160, -870, 1160, 840);
            int selectedCount = 0, closedCount = 0; GhostApp chosen = null;
            using (var popup = new GhostSearchPopup(Target(displayBounds), exactWork, 144, exactWork.Bottom - 4,
                delegate(GhostApp app) { selectedCount++; chosen = app; }, delegate { closedCount++; })) {
                var originalInput = popup.Controls.OfType<TextBox>().Single();
                var style = typeof(Control).GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic);
                assert((bool)style.Invoke(popup, new object[] { ControlStyles.ResizeRedraw }), "Animated search resizes repaint the complete client surface instead of retaining old rounded borders");
                popup.SetRevealOrigin(new Rectangle(exactWork.Right, exactWork.Bottom, 40, 40));
                assert(!popup.Visible && !popup.ContainsFocus, "Constructing search does not show a window or steal focus");
                assert(popup.Query == "" && popup.StatusText == "Loading installed apps\u2026", "Search placeholder/loading state never becomes editable query text");
                assert(popup.AccessibleName == "Search installed apps" && originalInput.AccessibleName == "Search apps", "Search panel and editable input have meaningful accessibility labels");
                popup.SetCatalog(new List<GhostApp> { App("a", "Neutral App Alpha"), App("b", "Neutral App Beta"), App("c", "Neutral App Gamma") });
                assert(popup.ResultCount == 0 && popup.StatusText.Contains("this display"), "Loaded empty search stays compact with clear destination behavior");
                int emptyHeight = popup.ExpandedBounds.Height;
                popup.Query = "neutral";
                assert(popup.ResultCount == 3 && popup.SelectedResult == 0 && popup.ExpandedBounds.Height > emptyHeight,
                    "Typed matching query expands stable result list and selects first app");
                assert(exactWork.Contains(popup.ExpandedBounds), "Popup constructor preserves actual top and side work-area offsets");
                assert(popup.HandleSearchKey(Keys.Down) && popup.SelectedResult == 1, "Down key selects following search result");
                popup.HandleSearchKey(Keys.Enter);
                assert(selectedCount == 1 && chosen.Name == "Neutral App Beta", "Enter passes only an installed catalog selection to the placement callback");
                popup.SetCatalog(new List<GhostApp> { App("b", "Neutral App Beta"), App("c", "Neutral App Gamma"), App("a", "Neutral App Alpha") });
                assert(popup.Query == "neutral" && ((GhostApp)popup.Controls.OfType<ListBox>().Single().SelectedItem).Id == "b",
                    "Catalog refresh retains typed query and selected app identity");
                assert(Object.ReferenceEquals(originalInput, popup.Controls.OfType<TextBox>().Single()), "Catalog refresh never recreates editable control or its caret");
                popup.HandleSearchKey(Keys.Up); popup.HandleSearchKey(Keys.Up); popup.HandleSearchKey(Keys.Up);
                assert(popup.SelectedResult == 0, "Up navigation clamps at first result");
                for (int index = 0; index < 10; index++) popup.HandleSearchKey(Keys.Down);
                assert(popup.SelectedResult == popup.ResultCount - 1, "Down navigation clamps at final result");
                assert(!popup.HandleSearchKey(Keys.Control | Keys.Down) && !popup.HandleSearchKey(Keys.Shift | Keys.Enter), "Search only handles explicit plain navigation keys");
                popup.SetStatus("Opening on this display\u2026", true); popup.HandleSearchKey(Keys.Enter);
                assert(popup.Busy && selectedCount == 1, "Busy placement suppresses repeated Enter launches");
                popup.SetStatus("App could not be opened", false);
                assert(popup.StatusText == "App could not be opened", "Launch failure remains visible without replacing typed search");
                popup.Query = "nothing matches";
                assert(popup.ResultCount == 0 && popup.SelectedResult == -1 && popup.StatusText == "No matching installed apps",
                    "No-match query clears stale selection and prior error");
                popup.HandleSearchKey(Keys.Enter);
                assert(selectedCount == 1, "Enter on no match cannot execute arbitrary typed text");
                popup.HandleSearchKey(Keys.Escape); popup.HandleSearchKey(Keys.Escape);
                assert(closedCount == 1, "Repeated Escape notifies close exactly once");
                int completed = 0; popup.BeginDismiss(delegate { completed++; }); popup.BeginDismiss(delegate { completed++; });
                assert(popup.IsDismissing && completed == 1 && closedCount == 1, "Nonshown search closes once without an animation timer or duplicate callback");
                popup.SetStatus("late", true); popup.SetCatalog(new List<GhostApp> { App("z", "Late") });
                assert(popup.ResultCount == 0 && !popup.Busy, "Late catalog and status callbacks cannot revive dismissed search");
            }
            using (var popup = new GhostSearchPopup(Target(displayBounds), exactWork, 96, exactWork.Bottom, null, null)) {
                popup.SetCatalog(Enumerable.Range(0, 20).Select(index => App(index.ToString(), "Neutral many " + index)).ToList());
                popup.Query = "many";
                assert(popup.ResultCount == GhostSearchLayout.MaximumResults, "Search panel shows a small bounded result list");
                popup.Query = "  ";
                assert(popup.ResultCount == 0 && popup.Query == "  ", "Whitespace query stays empty without rewriting editable text");
                popup.HandleSearchKey(Keys.Escape);
                assert(popup.IsDismissing, "Search with no owner close callback still dismisses itself");
            }
            using (var popup = new GhostSearchPopup(Target(new Rectangle(0, 0, 180, 170)), new Rectangle(10, 15, 160, 145), 192, 160, null, null)) {
                popup.SetCatalog(new List<GhostApp> { App("a", "Small fixture") }); popup.Query = "small";
                assert(popup.ResultCount == 0 && popup.ExpandedBounds.Width <= 160 && popup.ExpandedBounds.Height <= 145,
                    "Minimum monitor layout never presents clipped actionable rows");
                assert(popup.StatusText == "Not enough space to show app results", "Minimum layout distinguishes unavailable row space from no matching installed app");
                var input = popup.Controls.OfType<TextBox>().Single();
                var caption = popup.Controls.OfType<Label>().Single();
                var close = popup.Controls.OfType<Button>().Single();
                assert(popup.ClientRectangle.Contains(input.Bounds) && popup.ClientRectangle.Contains(caption.Bounds) && popup.ClientRectangle.Contains(close.Bounds),
                    "Minimum 160x145/192dpi surface keeps all actionable controls and status visibly inside client bounds");
                assert(!input.Bounds.IntersectsWith(close.Bounds) && !input.Bounds.IntersectsWith(caption.Bounds) && !close.Bounds.IntersectsWith(caption.Bounds),
                    "Minimum surface keeps search input, close action and status separate");
                assert(!input.AutoSize && input.Font.Height <= input.Height && caption.Font.Height <= caption.Height && close.Font.Height <= close.Height,
                    "Compact fonts and fixed single-line input fit assigned control heights");
                popup.Query = "small fi";
                assert(popup.ClientRectangle.Contains(caption.Bounds) && popup.Query == "small fi", "Compact filtering preserves visible status and typed query");
            }
            foreach (int width in new[] { 80, 160, 240, 400 }) foreach (int height in new[] { 64, 92, 145, 240 })
            foreach (int dpi in new[] { 96, 144, 192, 288 })
                using (var popup = new GhostSearchPopup(Target(new Rectangle(0, 0, width, height)), new Rectangle(0, 0, width, height), dpi, height, null, null)) {
                    popup.SetCatalog(new List<GhostApp> { App("a", "Small fixture") }); popup.Query = "small";
                    assert(popup.Controls.Cast<Control>().All(child => popup.ClientRectangle.Contains(child.Bounds)),
                        "Native child bounds remain contained at minimum size " + width + "x" + height + "/" + dpi);
                    var input = popup.Controls.OfType<TextBox>().Single(); var close = popup.Controls.OfType<Button>().Single();
                    var caption = popup.Controls.OfType<Label>().Single();
                    assert(!input.Bounds.IntersectsWith(close.Bounds) && !input.Bounds.IntersectsWith(caption.Bounds) && !close.Bounds.IntersectsWith(caption.Bounds),
                        "Compact actions do not overlap at minimum size " + width + "x" + height + "/" + dpi);
                }
            using (var popup = new GhostSearchPopup(Target(new Rectangle(0, 0, 800, 600)), new Rectangle(0, 0, 800, 600), 96, 580, null, null)) {
                // Create an owned hidden HWND so invalidation follows the real
                // WinForms path, without showing or activating any desktop UI.
                popup.TopMost = false;
                IntPtr handle = popup.Handle;
                int fullInvalidations = 0;
                popup.Invalidated += delegate(object sender, InvalidateEventArgs e) {
                    if (e.InvalidRect == popup.ClientRectangle) fullInvalidations++;
                };
                foreach (int width in new[] { 228, 282, 331, 376 }) {
                    int before = fullInvalidations; popup.Width = width;
                    assert(fullInvalidations > before, "Each animated width fully invalidates old border pixels at " + width);
                }
                foreach (int height in new[] { 130, 206, 320, 92 }) {
                    int before = fullInvalidations; popup.Height = height;
                    assert(fullInvalidations > before, "Result expansion/collapse fully invalidates previous rounded geometry at " + height);
                }
                assert(handle != IntPtr.Zero && !popup.Visible && !Native.IsWindowVisible(handle) && TaskbarMover.ForegroundWindow() != handle,
                    "Resize redraw regression uses a hidden owned surface (managedVisible=" + popup.Visible + ", nativeVisible=" + Native.IsWindowVisible(handle) + ", foregroundOwned=" + (TaskbarMover.ForegroundWindow() == handle) + ")");
            }
        }

        static ContextMenuStrip PreparedMenu(GhostSearchPopup popup)
        {
            return (ContextMenuStrip)typeof(GhostSearchPopup).GetField("resultMenu", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(popup);
        }
        sealed class ContextInputProbe : GhostSearchInput
        {
            internal int NativeContextRequests;
            internal IntPtr NativePosition;
            protected override void DefWndProc(ref Message message)
            {
                if (message.Msg == 0x007B) {
                    NativeContextRequests++; NativePosition = message.LParam;
                    message.Result = new IntPtr(71); return;
                }
                base.DefWndProc(ref message);
            }
        }
        static Message InputContextMessage(GhostSearchInput input, IntPtr position)
        {
            object[] values = { Message.Create(IntPtr.Zero, 0x007B, IntPtr.Zero, position) };
            typeof(GhostSearchInput).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, values);
            return (Message)values[0];
        }
        static void KeyboardContextRequests(Action<bool, string> assert)
        {
            var display = Target(new Rectangle(0, 0, 1000, 800));
            int selected = 0;
            using (var popup = new GhostSearchPopup(display, 96, 780, delegate { selected++; }, null)) {
                popup.TopMost = false;
                popup.SetCatalog(new List<GhostApp> { App("keyboard", "Keyboard fixture") }); popup.Query = "keyboard";
                var input = popup.Controls.OfType<GhostSearchInput>().Single();
                popup.HandleSearchKey(Keys.Apps); var menu = PreparedMenu(popup);
                Message followup = InputContextMessage(input, new IntPtr(-1));
                assert(menu != null && Object.ReferenceEquals(menu, PreparedMenu(popup)) && followup.Result == IntPtr.Zero && !menu.Visible,
                    "Actual Edit WndProc consumes keyboard WM_CONTEXTMENU after Apps without replacing its result menu");
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input,
                    new object[] { new KeyEventArgs(Keys.Apps) });
                popup.HandleSearchKey(Keys.Shift | Keys.F10); InputContextMessage(input, new IntPtr(-1));
                assert(Object.ReferenceEquals(menu, PreparedMenu(popup)) && selected == 0,
                    "Command, KeyDown and keyboard-context routes reuse one exact result binding without action");
                popup.HandleSearchKey(Keys.Apps); popup.Query = "fixture"; InputContextMessage(input, new IntPtr(-1));
                assert(menu.IsDisposed && PreparedMenu(popup) == null,
                    "A native keyboard follow-up cannot reopen options retired by query refresh");
                InputContextMessage(input, new IntPtr(-1));
                assert(PreparedMenu(popup) != null && selected == 0,
                    "Consuming the managed gesture's follow-up does not suppress a later standalone keyboard context request");
                popup.HandleSearchKey(Keys.Shift | Keys.F10); var next = PreparedMenu(popup);
                assert(next != null && !Object.ReferenceEquals(next, menu),
                    "A new deliberate keyboard request can open current options after refresh");
                popup.SetStatus("Opening", true); InputContextMessage(input, new IntPtr(-1));
                assert(next.IsDisposed && PreparedMenu(popup) == null && selected == 0,
                    "Busy retirement cannot be undone by the native keyboard follow-up");
                popup.SetStatus(null, false);
                popup.HandleSearchKey(Keys.Apps); var beforeCatalog = PreparedMenu(popup);
                popup.SetCatalog(new List<GhostApp> { App("keyboard", "Keyboard fixture") }); InputContextMessage(input, new IntPtr(-1));
                assert(beforeCatalog.IsDisposed && PreparedMenu(popup) == null,
                    "Catalog refresh retires the old keyboard binding without reopening it from a native follow-up");
                using (var pointer = new ContextInputProbe { Visible = false }) {
                    pointer.KeyboardContextRequested = delegate { popup.HandleSearchKey(Keys.Apps); return true; };
                    pointer.PointerContextRequested = delegate {
                        typeof(GhostSearchPopup).GetMethod("CloseResultMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(popup, null);
                    };
                    InputContextMessage(pointer, new IntPtr(-1)); var beforePointer = PreparedMenu(popup);
                    Message native = InputContextMessage(pointer, new IntPtr(-2));
                    assert(beforePointer != null && beforePointer.IsDisposed && PreparedMenu(popup) == null &&
                        pointer.NativeContextRequests == 1 && pointer.NativePosition == new IntPtr(-2) && native.Result == new IntPtr(71),
                        "Pointer Edit context requests, including negative screen coordinates, retire result options and reach native processing");
                    InputContextMessage(pointer, IntPtr.Zero);
                    assert(pointer.NativeContextRequests == 2 && pointer.NativePosition == IntPtr.Zero,
                        "An ordinary query right-click is not swallowed by earlier keyboard-context ownership");
                    InputContextMessage(pointer, new IntPtr(-1)); var direct = PreparedMenu(popup);
                    InputContextMessage(pointer, new IntPtr(-1));
                    assert(direct != null && Object.ReferenceEquals(direct, PreparedMenu(popup)) && pointer.NativeContextRequests == 2,
                        "Direct keyboard WM_CONTEXTMENU opens current result options once and never enters native Edit menu processing");
                    assert(!pointer.Visible && !popup.Visible && selected == 0,
                        "Keyboard and pointer message fixtures remain hidden and execute no app action");
                }
            }
            using (var popup = new GhostSearchPopup(display, 96, 780, delegate { selected++; }, null)) {
                popup.TopMost = false;
                popup.SetCatalog(new List<GhostApp> { App("keyboard", "Keyboard fixture") }); popup.Query = "keyboard";
                var input = popup.Controls.OfType<GhostSearchInput>().Single();
                InputContextMessage(input, new IntPtr(-1)); var direct = PreparedMenu(popup);
                InputContextMessage(input, new IntPtr(-1));
                assert(direct != null && Object.ReferenceEquals(direct, PreparedMenu(popup)) && !direct.Visible && selected == 0,
                    "The actual Edit message path can prepare options directly once without a preceding managed key event");
            }
            foreach (string state in new[] { "empty", "busy", "dismiss", "dispose" }) {
                using (var popup = new GhostSearchPopup(display, 96, 780, delegate { selected++; }, null)) {
                    popup.TopMost = false;
                    popup.SetCatalog(new List<GhostApp> { App("keyboard", "Keyboard fixture") }); popup.Query = "keyboard";
                    var input = popup.Controls.OfType<GhostSearchInput>().Single();
                    if (state == "empty") popup.Query = "no matching result";
                    if (state == "busy") popup.SetStatus("Opening", true);
                    if (state == "dismiss") popup.BeginDismiss(null);
                    if (state == "dispose") popup.Dispose();
                    Message ignored = InputContextMessage(input, new IntPtr(-1));
                    assert(ignored.Result == IntPtr.Zero && PreparedMenu(popup) == null && selected == 0,
                        "Actual Edit keyboard context processing cannot revive an unavailable result menu: " + state);
                }
            }
        }
        internal static void RunResultActions(Action<bool, string> assert)
        {
            KeyboardContextRequests(assert);
            var display = Target(new Rectangle(0, 0, 1000, 800));
            var chrome = GhostAppCatalog.FromShortcut("Google Chrome", @"C:\Neutral\Chrome.lnk", @"C:\Neutral\chrome.exe",
                "--source-shortcut-location=start-menu", @"C:\Neutral", "Chrome", delegate { return true; });
            int bring = 0, fresh = 0; string chosen = "";
            Func<GhostSearchPopup> fixture = delegate {
                var popup = new GhostSearchPopup(display, 96, 780,
                    delegate(GhostApp app) { bring++; chosen = app.Id; }, null,
                    delegate(GhostApp app) { fresh++; chosen = app.Id; });
                popup.TopMost = false;
                popup.SetCatalog(new List<GhostApp> { App("other", "Fixture other"), chrome }); popup.Query = "";
                return popup;
            };
            using (var popup = fixture()) {
                popup.Query = "Chrome";
                var menu = popup.PrepareResultMenu(0);
                assert(menu != null && menu.Items.Count == 2 && menu.Items[0].Text == "Open or bring here" && menu.Items[1].Text == "Open new window",
                    "Chrome result offers distinct existing-window and new-window actions");
                assert(bring == 0 && fresh == 0 && !menu.Visible && !popup.Visible,
                    "Preparing result options never launches or shows desktop UI");
                popup.SetStatus(null, false);
                assert(Object.ReferenceEquals(menu, PreparedMenu(popup)), "Neutral status polling preserves open result options");
                var twice = menu.Items[1]; twice.PerformClick(); twice.PerformClick();
                assert(fresh == 1 && bring == 0 && chosen == chrome.Id, "New-window option invokes only its explicit action and commits once");
                menu = popup.PrepareResultMenu(0); menu.Items[0].PerformClick();
                assert(bring == 1 && fresh == 1, "Default menu action retains open-or-bring behavior");
                popup.HandleSearchKey(Keys.Enter);
                assert(bring == 2 && fresh == 1, "Plain Enter continues to open or bring rather than create another window");
            }
            using (var popup = fixture()) {
                popup.Query = "Fixture";
                var menu = popup.PrepareResultMenu(0);
                assert(menu.Items.Count == 1 && menu.Items[0].Text == "Open or bring here",
                    "Apps without an explicit supported new-window operation do not promise one");
                assert(popup.PrepareResultMenu(-1) == null && popup.PrepareResultMenu(99) == null,
                    "Options require an actual catalog result, never blank space or arbitrary query text");
            }
            using (var popup = fixture()) {
                popup.Query = "o";
                var results = popup.Controls.OfType<ListBox>().Single();
                int index = results.Items.Cast<GhostApp>().ToList().FindIndex(app => app.Id == chrome.Id);
                Rectangle row = results.GetItemRectangle(index); int beforeBring = bring, beforeFresh = fresh;
                ReleaseRightButton(results, new Point(-4, row.Top + 4));
                assert(PreparedMenu(popup) == null, "Native right-button release outside a row never prepares options");
                ReleaseRightButton(results, new Point(row.Left + 4, row.Top + 4));
                assert(results.SelectedIndex == index && PreparedMenu(popup) != null && !PreparedMenu(popup).Visible &&
                    bring == beforeBring && fresh == beforeFresh,
                    "Native ListBox right-button release binds the clicked row without showing hidden UI or executing either action");
                var old = PreparedMenu(popup).Items[1];
                popup.HandleSearchKey(Keys.Up); old.PerformClick();
                assert(fresh == beforeFresh, "Changing selected row rejects the previous menu command");
                popup.HandleSearchKey(Keys.Apps);
                assert(PreparedMenu(popup) != null, "Menu key prepares options for the selected result");
                popup.HandleSearchKey(Keys.Shift | Keys.F10);
                assert(PreparedMenu(popup) != null, "Shift+F10 prepares result options without launching");
            }
            foreach (string invalidation in new[] { "query", "catalog", "busy", "escape", "dismiss", "dispose", "metadata" }) {
                using (var popup = fixture()) {
                    popup.Query = "Chrome";
                    var menu = popup.PrepareResultMenu(0); var stale = menu.Items[1]; int beforeFresh = fresh;
                    if (invalidation == "query") popup.Query = "Fixture";
                    if (invalidation == "catalog") popup.SetCatalog(new List<GhostApp> { chrome });
                    if (invalidation == "busy") popup.SetStatus("Opening", true);
                    if (invalidation == "escape") popup.HandleSearchKey(Keys.Escape);
                    if (invalidation == "dismiss") popup.BeginDismiss(null);
                    if (invalidation == "dispose") popup.Dispose();
                    if (invalidation == "metadata") chrome.Arguments = "--profile-directory=Other";
                    stale.PerformClick();
                    assert(fresh == beforeFresh, "Stale result command is rejected after " + invalidation);
                    chrome.Arguments = "--source-shortcut-location=start-menu";
                }
            }
            using (var popup = fixture()) {
                popup.Query = "Chrome";
                var menu = popup.PrepareResultMenu(0); var item = menu.Items[1]; int before = fresh;
                typeof(ToolStripDropDown).GetMethod("OnClosed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(menu,
                    new object[] { new ToolStripDropDownClosedEventArgs(ToolStripDropDownCloseReason.ItemClicked) });
                item.PerformClick();
                assert(fresh == before + 1, "Menu Closed(ItemClicked) before Click still commits the captured action once");
                assert(!popup.OwnsWindow(new IntPtr(12345)), "Unrelated windows are never treated as search-owned");
                assert(!popup.ContainsScreenPoint(new Point(-500, -500)), "True outside pointer positions remain outside search");
            }
            using (var popup = new GhostSearchPopup(display, 96, 780, null, null)) {
                popup.TopMost = false; popup.SetCatalog(new List<GhostApp> { chrome }); popup.Query = "Chrome";
                assert(popup.PrepareResultMenu(0).Items.Count == 1,
                    "Missing new-window callback cannot expose a command backed by a real launcher");
            }
        }

        static void FocusRetryChecks(Action<bool, string> assert)
        {
            long now = 0;
            IntPtr owner = new IntPtr(11), previous = new IntPtr(22), outside = new IntPtr(33), foreground = previous;
            var retry = new GhostSearchFocusRetry(delegate { return now; });
            int claims = 0, focuses = 0;
            bool childFocused = false;
            Func<IntPtr> read = delegate { return foreground; };
            Action<IntPtr> claim = delegate(IntPtr target) { claims++; foreground = target; };
            Action focus = delegate { focuses++; childFocused = true; };
            Func<bool> focused = delegate { return childFocused; };
            long token = retry.Begin(owner, previous);
            assert(retry.TryFocus(token, read, claim, focus, focused) && claims == 1 && focuses == 1 && !retry.Pending(token),
                "Search focus verifies both exact popup foreground and editable child focus");
            assert(!retry.TryFocus(token, read, claim, focus, focused) && claims == 1,
                "Completed focus request cannot reactivate through a late timer");

            foreground = previous; claims = focuses = 0; childFocused = false;
            token = retry.Begin(owner, previous);
            Action<IntPtr> rejectedClaim = delegate { claims++; }; // Even an API reporting acceptance cannot replace observation.
            assert(!retry.TryFocus(token, read, rejectedClaim, focus, focused) && claims == 1 && focuses == 0 && retry.Pending(token),
                "Foreground claim without actual activation never focuses a background child or reports success");
            assert(!retry.TryFocus(token, read, rejectedClaim, focus, focused) && claims == 1,
                "Focus timer cannot retry faster than the bounded interval");
            now += GhostSearchFocusRetry.RetryMilliseconds;
            assert(retry.TryFocus(token, read, claim, focus, focused) && claims == 2 && focuses == 1,
                "A short delayed activation retry recovers the initial rejected request");

            foreground = owner; claims = focuses = 0; childFocused = false;
            token = retry.Begin(owner, previous);
            Action noChildFocus = delegate { focuses++; };
            assert(!retry.TryFocus(token, read, claim, noChildFocus, focused) && claims == 0 && focuses == 1 && retry.Pending(token),
                "Popup foreground alone does not count as successful typing focus");
            now += GhostSearchFocusRetry.RetryMilliseconds;
            assert(retry.TryFocus(token, read, claim, focus, focused) && claims == 0 && focuses == 2,
                "Already active search repairs its editable focus without another foreground claim");

            foreground = previous; claims = focuses = 0;
            token = retry.Begin(owner, previous);
            foreground = outside;
            assert(!retry.TryFocus(token, read, claim, focus, focused) && !retry.Pending(token) && claims == 0 && focuses == 0,
                "A different foreground window cancels focus without taking it back");
            foreground = previous;
            assert(!retry.TryFocus(token, read, claim, focus, focused) && claims == 0,
                "Returning to the original window cannot revive a cancelled focus request");

            token = retry.Begin(owner, previous); retry.Cancel();
            assert(!retry.TryFocus(token, read, claim, focus, focused) && claims == 0,
                "Outside click dismissal cancels retry even when it clicked the original foreground app");
            long oldToken = retry.Begin(owner, previous), newToken = retry.Begin(owner, previous);
            assert(!retry.TryFocus(oldToken, read, claim, focus, focused) && retry.Pending(newToken),
                "Stale focus timer cannot cancel or claim a newer shortcut invocation");
            assert(retry.TryFocus(newToken, read, claim, focus, focused), "Repeated explicit shortcut can start a fresh verified focus request");

            foreground = previous; focuses = 0;
            token = retry.Begin(owner, previous);
            Action<IntPtr> cancelledClaim = delegate(IntPtr target) { foreground = target; retry.Cancel(); };
            assert(!retry.TryFocus(token, read, cancelledClaim, focus, focused) && focuses == 0 && !retry.Pending(token),
                "Reentrant dismissal during native activation prevents the subsequent input focus");
            foreground = previous; token = retry.Begin(owner, previous); long replacementToken = 0;
            Action<IntPtr> newerClaim = delegate { replacementToken = retry.Begin(owner, previous); };
            assert(!retry.TryFocus(token, read, newerClaim, focus, focused) && focuses == 0 && retry.Pending(replacementToken),
                "Reentrant repeated shortcut during activation leaves its newer focus generation intact");
            foreground = owner; token = retry.Begin(owner, previous);
            Action cancelledFocus = delegate { focuses++; retry.Cancel(); };
            assert(!retry.TryFocus(token, read, claim, cancelledFocus, focused) && !retry.Pending(token),
                "Reentrant cancellation while focusing never reports a completed request");

            foreground = owner; token = retry.Begin(owner, previous);
            Action leavesPopup = delegate { foreground = outside; };
            assert(!retry.TryFocus(token, read, claim, leavesPopup, focused), "Focus completion rechecks foreground after the child focus call");
            now += GhostSearchFocusRetry.RetryMilliseconds;
            assert(!retry.TryFocus(token, read, claim, focus, focused) && !retry.Pending(token),
                "External activation during child focus cancels the following timer");

            foreground = previous; now = 0; token = retry.Begin(owner, previous);
            int attempts = 0;
            for (int index = 0; index < GhostSearchFocusRetry.MaximumAttempts + 2; index++) {
                if (retry.TryAttempt(token, previous)) attempts++;
                now += GhostSearchFocusRetry.RetryMilliseconds;
            }
            assert(attempts == GhostSearchFocusRetry.MaximumAttempts && !retry.Pending(token),
                "Rejected Windows activation stops after six attempts instead of stealing focus later");
            now = 0; token = retry.Begin(owner, previous); now = GhostSearchFocusRetry.TimeoutMilliseconds;
            assert(!retry.TryAttempt(token, previous) && !retry.Pending(token) && retry.Attempts == 0,
                "Delayed UI timer after the 300ms deadline never attempts activation");
            now = Int64.MaxValue - 10; token = retry.Begin(owner, previous);
            assert(retry.TryAttempt(token, previous), "Bounded focus timing handles a clock close to its numeric limit");
            now = Int64.MaxValue;
            assert(!retry.TryAttempt(token, previous) && !retry.Pending(token), "Saturated focus deadline still stops the request");
            now = 0; token = retry.Begin(IntPtr.Zero, previous);
            assert(!retry.Pending(token) && !retry.TryAttempt(token, previous), "No popup HWND means no focus request can start");
        }

        static void ArmHiddenFocus(GhostSearchPopup popup)
        {
            // Arm only the policy; never show/activate an actual desktop window.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var retry = (GhostSearchFocusRetry)typeof(GhostSearchPopup).GetField("focusRetry", flags).GetValue(popup);
            long token = retry.Begin(popup.Handle, new IntPtr(22));
            typeof(GhostSearchPopup).GetField("focusToken", flags).SetValue(popup, token);
        }
        static GhostSearchPopup FocusFixture()
        {
            var popup = new GhostSearchPopup(Target(new Rectangle(0, 0, 800, 600)), 96, 580, null, null);
            popup.TopMost = false; return popup;
        }
        static void FocusLifecycleChecks(Action<bool, string> assert)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using (var popup = FocusFixture()) {
                int claims = 0, focuses = 0;
                popup.FocusApi.Claim = delegate { claims++; return true; };
                popup.FocusApi.Focus = delegate { focuses++; return true; };
                assert(!popup.RequestInputFocus() && !popup.InputFocusPending && claims == 0 && focuses == 0,
                    "Hidden search refuses focus without touching the user's foreground app");
                ArmHiddenFocus(popup); popup.BeginDismiss(null);
                assert(!popup.InputFocusPending && !popup.RequestInputFocus() && claims == 0 && focuses == 0,
                    "Dismissal cancels pending focus before its animation and rejects repeated focus requests");
                assert(!popup.Visible && !Native.IsWindowVisible(popup.Handle), "Focus dismissal fixture remains a hidden owned HWND");
            }
            using (var popup = FocusFixture()) {
                ArmHiddenFocus(popup);
                typeof(GhostSearchPopup).GetMethod("SetVisibleCore", flags).Invoke(popup, new object[] { false });
                assert(!popup.InputFocusPending, "Hide invalidates pending focus generation");
                ArmHiddenFocus(popup);
                typeof(GhostSearchPopup).GetMethod("OnDeactivate", flags).Invoke(popup, new object[] { EventArgs.Empty });
                assert(!popup.InputFocusPending, "Deactivation cancels focus even if launch placement is allowed to remain open");
                typeof(GhostSearchPopup).GetField("shown", flags).SetValue(popup, true);
                popup.SetStatus("Opening neutral fixture", true); ArmHiddenFocus(popup);
                typeof(GhostSearchPopup).GetMethod("OnDeactivate", flags).Invoke(popup, new object[] { EventArgs.Empty });
                assert(!popup.InputFocusPending && popup.Busy && !popup.IsDismissing,
                    "Expected app activation cancels focus without cancelling an in-flight placement");
                ArmHiddenFocus(popup);
                typeof(GhostSearchPopup).GetMethod("OnMouseDown", flags).Invoke(popup, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 1, 1, 0) });
                assert(!popup.InputFocusPending, "Manual interaction with popup background cancels stale focus retries");
                var input = popup.Controls.OfType<TextBox>().Single(); ArmHiddenFocus(popup);
                typeof(Control).GetMethod("OnMouseDown", flags).Invoke(input, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 1, 1, 0) });
                assert(!popup.InputFocusPending, "Manual input click preserves the user's chosen caret instead of retrying focus");
                ArmHiddenFocus(popup);
                typeof(Control).GetMethod("OnKeyDown", flags).Invoke(input, new object[] { new KeyEventArgs(Keys.A) });
                assert(!popup.InputFocusPending, "Typing stops focus retries so later timer ticks cannot disturb the user's selection");
                ArmHiddenFocus(popup); popup.HandleSearchKey(Keys.Escape);
                assert(!popup.InputFocusPending, "Escape cancels pending focus synchronously");
            }
            using (var popup = FocusFixture()) {
                ArmHiddenFocus(popup);
                typeof(Control).GetMethod("DestroyHandle", flags).Invoke(popup, null);
                assert(!popup.IsHandleCreated && !popup.InputFocusPending,
                    "Native HWND destruction invalidates the old focus request before any handle recreation");
            }
            var disposed = FocusFixture(); ArmHiddenFocus(disposed); disposed.Dispose(); disposed.CancelInputFocus();
            assert(!disposed.InputFocusPending && !disposed.RequestInputFocus(), "Dispose invalidates focus and later cancellation is safe");
        }
    }
}
