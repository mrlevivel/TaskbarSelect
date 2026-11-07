using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class TilingPickerTests
    {
        internal static void Run(Action<bool, string> assert)
        {
            Geometry(assert);
            Layout(assert);
            KeyboardLifecycle(assert);
        }

        static void Geometry(Action<bool, string> assert)
        {
            var odd = new Rectangle(-1601, -1081, 1601, 1001);
            var current = new Rectangle(-1500, -900, 801, 601);
            assert(WindowTilingPolicy.Target(odd, current, WindowTile.Left) == new Rectangle(-1601, -1081, 800, 1001), "Left half preserves negative origin and physical pixels");
            assert(WindowTilingPolicy.Target(odd, current, WindowTile.Right) == new Rectangle(-801, -1081, 801, 1001), "Right half receives the remaining odd pixel without a gap");
            assert(WindowTilingPolicy.Target(odd, current, WindowTile.Top) == new Rectangle(-1601, -1081, 1601, 500), "Top layout uses full width and top half of a stacked work area");
            assert(WindowTilingPolicy.Target(odd, current, WindowTile.Bottom) == new Rectangle(-1601, -581, 1601, 501), "Bottom half receives the remaining odd pixel");
            assert(WindowTilingPolicy.Target(odd, current, WindowTile.BottomRight) == new Rectangle(-801, -581, 801, 501), "Bottom right quarter combines independent physical splits");
            assert(WindowTilingPolicy.Target(odd, current, WindowTile.Center) == new Rectangle(-1201, -881, 801, 601), "Center retains the captured visible size and centers on its work area");
            assert(WindowTilingPolicy.Target(odd, new Rectangle(0, 0, 3000, 2000), WindowTile.Center) == odd, "Center clamps oversized windows to the captured work area");
            assert(WindowTilingPolicy.Target(odd, Rectangle.Empty, WindowTile.Center).Size == new Size(1, 1), "Empty captured size cannot create a zero-size center target");
            foreach (int width in new[] { 1, 2, 3, 80, 801, 1920, 2561 })
            foreach (int height in new[] { 1, 2, 3, 61, 1001, 1440 }) {
                var work = new Rectangle(-4000, -2500, width, height);
                Rectangle left = WindowTilingPolicy.Target(work, current, WindowTile.Left);
                Rectangle right = WindowTilingPolicy.Target(work, current, WindowTile.Right);
                Rectangle top = WindowTilingPolicy.Target(work, current, WindowTile.Top);
                Rectangle bottom = WindowTilingPolicy.Target(work, current, WindowTile.Bottom);
                if (width > 1) assert(left.Right == right.Left && left.Width + right.Width == width, "Horizontal halves partition every odd/even work width exactly");
                if (height > 1) assert(top.Bottom == bottom.Top && top.Height + bottom.Height == height, "Vertical halves partition every odd/even work height exactly");
                foreach (WindowTile tile in Enum.GetValues(typeof(WindowTile))) {
                    Rectangle target = WindowTilingPolicy.Target(work, current, tile);
                    assert(target.Width > 0 && target.Height > 0 && work.Contains(target), "Every layout stays positive and inside tiny, negative-origin or stacked work area: " + tile);
                }
            }
            bool invalidWork = false, invalidTile = false;
            try { WindowTilingPolicy.Target(Rectangle.Empty, current, WindowTile.Left); } catch (ArgumentException) { invalidWork = true; }
            try { WindowTilingPolicy.Target(odd, current, (WindowTile)9); } catch (ArgumentOutOfRangeException) { invalidTile = true; }
            assert(invalidWork && invalidTile, "Invalid work areas or enum values cannot produce an actionable placement");
        }

        static void Layout(Action<bool, string> assert)
        {
            foreach (Rectangle work in new[] {
                new Rectangle(0, 48, 1920, 1032), new Rectangle(-1600, -1200, 1600, 1140),
                new Rectangle(-1100, 30, 1080, 1870), new Rectangle(20, 30, 160, 145),
                new Rectangle(-10, -20, 64, 64), new Rectangle(-1, -1, 1, 1)
            }) foreach (int dpi in new[] { 96, 120, 144, 192, 240, 288 }) {
                Rectangle fitted = TilingPickerLayout.Fit(work, dpi);
                assert(work.Contains(fitted) && fitted.Width > 0 && fitted.Height > 0, "Picker fits captured work offsets, stacked layout and DPI " + dpi);
                var layout = TilingPickerLayout.Create(fitted.Size, dpi);
                var client = new Rectangle(Point.Empty, fitted.Size);
                assert(layout.Cells.All(cell => cell.Width > 0 && cell.Height > 0 && client.Contains(cell)), "All nine picker hit targets fit the captured compact/high-DPI panel");
                if (fitted.Width >= 64 && fitted.Height >= 64) {
                    assert(client.Contains(layout.Header) && client.Contains(layout.Footer) && client.Contains(layout.Grid), "Header, footer and grid remain inside compact client bounds");
                    for (int index = 0; index < 9; index++) for (int other = index + 1; other < 9; other++)
                        assert(!layout.Cells[index].IntersectsWith(layout.Cells[other]), "Tiling hit targets do not overlap in compact/high-DPI layout");
                    assert(layout.Header.IsEmpty || !layout.Header.IntersectsWith(layout.Grid), "Picker title stays separate from actionable grid");
                    assert(layout.Footer.IsEmpty || !layout.Footer.IntersectsWith(layout.Grid), "Picker instructions stay separate from actionable grid");
                }
                using (var picker = new TilingPicker(null)) {
                    picker.TopMost = false; picker.Configure(work, dpi, "Owned fixture");
                    assert(work.Contains(picker.Bounds) && picker.Controls.Cast<Control>().All(control => picker.ClientRectangle.Contains(control.Bounds)), "Real controls fit the exact captured work area without automatic DPI growth");
                    assert(!picker.Visible && !picker.ContainsFocus && !picker.FocusPending, "Configuration never opens a window or starts foreground requests");
                }
            }
            using (var picker = new TilingPicker(null)) {
                picker.TopMost = false; picker.Configure(new Rectangle(-800, -600, 800, 600), 144, "Calculator\r\n\towned");
                assert(picker.TargetTitle == "Calculatorowned", "Captured title removes control characters before painting");
                picker.Configure(new Rectangle(-800, -600, 800, 600), 144, new string('x', 500));
                assert(picker.TargetTitle.Length == 96, "Captured title is bounded and never used as a command");
                IntPtr handle = picker.Handle;
                using (var bitmap = new Bitmap(picker.Width, picker.Height)) picker.DrawToBitmap(bitmap, new Rectangle(Point.Empty, picker.Size));
                assert(handle != IntPtr.Zero && !picker.Visible && !Native.IsWindowVisible(handle) && TaskbarMover.ForegroundWindow() != handle,
                    "Picker drawing uses a hidden owned HWND and never activates a personal window");
            }
            using (var picker = new TilingPicker(null)) {
                picker.TopMost = false; picker.Configure(new Rectangle(-1200, -900, 800, 600), 144, "Owned corner fixture");
                Point center = new Point(picker.Left + picker.Width / 2, picker.Top + picker.Height / 2);
                Point outside = new Point(picker.Left - 1, picker.Top + picker.Height / 2);
                Point corner = new Point(picker.Left + 1, picker.Top + 1);
                assert(picker.ContainsScreenPoint(center) && !picker.ContainsScreenPoint(outside), "Picker hit testing translates physical negative-origin coordinates correctly");
                Region previous = picker.Region;
                using (var path = ShelfPaint.Round(new RectangleF(0, 0, picker.Width, picker.Height), 18)) picker.Region = new Region(path);
                if (previous != null) previous.Dispose();
                assert(picker.Bounds.Contains(corner) && !picker.ContainsScreenPoint(corner), "Transparent rounded corner counts as an outside click even when native foreground does not change");
                assert(picker.ContainsScreenPoint(center), "Rounded region still accepts the actual picker surface");
                previous = picker.Region; picker.Region = null; previous.Dispose();
                assert(picker.ContainsScreenPoint(corner), "Opaque high-contrast rectangular region keeps its corner inside the picker");
                assert(!picker.Visible && !picker.ContainsFocus, "Rounded-region regression does not open or focus any desktop window");
            }
        }

        static void KeyboardLifecycle(Action<bool, string> assert)
        {
            foreach (WindowTile tile in Enum.GetValues(typeof(WindowTile))) {
                assert((int)TilingPickerLayout.Navigate(tile, Keys.Left) % 3 <= (int)tile % 3, "Left selection moves spatially or clamps at left edge");
                assert((int)TilingPickerLayout.Navigate(tile, Keys.Right) % 3 >= (int)tile % 3, "Right selection moves spatially or clamps at right edge");
                assert((int)TilingPickerLayout.Navigate(tile, Keys.Up) / 3 <= (int)tile / 3, "Up selection moves spatially or clamps at top edge");
                assert((int)TilingPickerLayout.Navigate(tile, Keys.Down) / 3 >= (int)tile / 3, "Down selection moves spatially or clamps at bottom edge");
            }
            int applied = 0, closed = 0; WindowTile chosen = WindowTile.Center;
            using (var picker = new TilingPicker(delegate(WindowTile tile) { applied++; chosen = tile; })) {
                picker.FormClosed += delegate { closed++; };
                assert(picker.SelectedTile == WindowTile.Center && picker.AccessibleName == "Arrange the active window", "Picker starts at center with keyboard and accessibility instructions");
                assert(picker.HandlePickerKey(Keys.Left) && picker.SelectedTile == WindowTile.Left, "Left chooses the left half without applying placement");
                picker.HandlePickerKey(Keys.Up); picker.HandlePickerKey(Keys.Up); picker.HandlePickerKey(Keys.Left);
                assert(picker.SelectedTile == WindowTile.TopLeft && applied == 0, "Spatial arrow selection clamps at top left and causes no movement");
                picker.HandlePickerKey(Keys.Right); picker.HandlePickerKey(Keys.Right); picker.HandlePickerKey(Keys.Down); picker.HandlePickerKey(Keys.Down);
                assert(picker.SelectedTile == WindowTile.BottomRight && applied == 0, "Spatial arrows can select the bottom right quarter without moving any window");
                assert(!picker.HandlePickerKey(Keys.Control | Keys.Left) && !picker.HandlePickerKey(Keys.Shift | Keys.Enter) && !picker.HandlePickerKey(Keys.Space), "Only explicit plain picker keys are consumed");
                assert(picker.HandlePickerKey(Keys.Enter), "Enter accepts the current layout");
                picker.HandlePickerKey(Keys.Enter); picker.Cancel();
                assert(applied == 1 && chosen == WindowTile.BottomRight && picker.IsFinished, "Repeated Enter and cancellation cannot apply twice");
                assert(closed <= 1, "Closing a hidden owned picker never emits duplicate FormClosed events");
            }
            foreach (string cancelPath in new[] { "escape", "cancel", "dispose", "deactivate" }) {
                int callbacks = 0;
                using (var picker = new TilingPicker(delegate { callbacks++; })) {
                    picker.HandlePickerKey(Keys.Right);
                    if (cancelPath == "escape") picker.HandlePickerKey(Keys.Escape);
                    else if (cancelPath == "cancel") picker.Cancel();
                    else if (cancelPath == "dispose") picker.Dispose();
                    else {
                        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        typeof(TilingPicker).GetField("opened", flags).SetValue(picker, true);
                        typeof(TilingPicker).GetField("activated", flags).SetValue(picker, true);
                        typeof(TilingPicker).GetMethod("OnDeactivate", flags).Invoke(picker, new object[] { EventArgs.Empty });
                    }
                    picker.HandlePickerKey(Keys.Enter);
                    assert(callbacks == 0 && picker.IsFinished && !picker.FocusPending, "Cancel path " + cancelPath + " clears selection authority and never applies placement");
                }
            }
            int clickCallbacks = 0;
            using (var picker = new TilingPicker(delegate { clickCallbacks++; })) {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var click = typeof(Button).GetMethod("OnClick", flags);
                Control clickedButton = picker.Controls[5];
                click.Invoke(clickedButton, new object[] { EventArgs.Empty });
                // Native click messages queued before closing cannot apply again.
                click.Invoke(clickedButton, new object[] { EventArgs.Empty });
                assert(clickCallbacks == 1 && picker.SelectedTile == WindowTile.Right, "A mouse-selected layout applies once even with a repeated click");
            }
        }
    }
}
