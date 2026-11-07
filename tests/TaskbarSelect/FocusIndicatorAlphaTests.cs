using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class FocusIndicatorAlphaTests
    {
        internal static void Run(Action<bool, string> check)
        {
            TransparentPixels(check);
            NativePublication(check);
        }

        static void TransparentPixels(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 144, 192 })
            foreach (FocusIndicatorMode mode in new[] { FocusIndicatorMode.MutedFrame, FocusIndicatorMode.InsetEdge })
            foreach (string accent in FocusAppearance.Presets)
            foreach (bool dark in new[] { false, true }) {
                var target = new FocusIndicatorTarget {
                    Identity = new FocusedWindowSnapshot { Window = new IntPtr(42), Root = new IntPtr(42), Process = 1, Thread = 1 },
                    Frame = new Rectangle(-160, -120, 160, 120), Monitors = new[] { new Rectangle(-200, -200, 400, 400) }, Dpi = dpi
                };
                var visual = FocusIndicatorVisual.Create(target, mode, accent, dark);
                using (Bitmap bitmap = FocusIndicatorSurface.Render(visual)) {
                    bool transparent = true, premultiplied = true, painted = false;
                    BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    try {
                        var row = new byte[bitmap.Width * 4];
                        for (int y = 0; y < bitmap.Height; y++) {
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                            for (int x = 0; x < bitmap.Width; x++) {
                                int offset = x * 4, alpha = row[offset + 3];
                                if (alpha > 0) painted = true;
                                premultiplied &= row[offset] <= alpha && row[offset + 1] <= alpha && row[offset + 2] <= alpha;
                                if (x >= visual.Thickness && x < bitmap.Width - visual.Thickness &&
                                    y >= visual.Thickness && y < bitmap.Height - visual.Thickness)
                                    transparent &= row[offset] == 0 && row[offset + 1] == 0 && row[offset + 2] == 0 && alpha == 0;
                            }
                        }
                    } finally { bitmap.UnlockBits(data); }
                    check(transparent && premultiplied && painted && bitmap.PixelFormat == PixelFormat.Format32bppPArgb &&
                        bitmap.GetPixel(bitmap.Width / 2, 0).A == 255,
                        "Production alpha image paints its border while every interior byte stays zero: " + dpi + "/" + mode + "/" + accent + "/" + dark);
                }
            }
        }

        sealed class OwnedTarget : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
        }
        sealed class Events : IFocusIndicatorEvents
        { public void Dispose() { } }
        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        static FocusIndicatorApi Api(OwnedTarget target)
        {
            return new FocusIndicatorApi {
                Foreground = delegate { return target.Handle; }, DesktopAllowed = delegate { return true; },
                PointerButtonDown = delegate { return false; }, Dark = delegate { return true; },
                Read = delegate(IntPtr window) {
                    var value = FocusIndicatorNative.Read(window);
                    // Eligibility is injected; the actual fixture remains noactivate.
                    if (value != null) value.Identity.ExtendedStyle &= ~0x08000000;
                    return value;
                },
                Watch = delegate { return new Events(); }
            };
        }

        static void NativePublication(Action<bool, string> check)
        {
            IntPtr foreground = FocusedWindowNative.Foreground(); Point pointer = Cursor.Position;
            using (var target = new OwnedTarget { Text = "Neutral alpha lifecycle fixture", Opacity = 0, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(60, 60, 360, 240) }) {
                target.Show();
                var native = FocusIndicatorNative.Read(target.Handle);
                if (native == null) throw new InvalidOperationException("Owned alpha fixture unavailable.");
                var visual = FocusIndicatorVisual.Create(native, FocusIndicatorMode.InsetEdge, "Sky", true);
                int calls = 0; bool firstHidden = false, centerTransparent = true, realBorder = true; IntPtr owned = IntPtr.Zero;
                using (var surface = new FocusIndicatorSurface(false, delegate(IntPtr window, Bitmap bitmap, Point location) {
                    calls++; owned = window;
                    if (calls == 1) firstHidden = !Native.IsWindowVisible(window);
                    centerTransparent &= bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).ToArgb() == 0;
                    realBorder &= bitmap.GetPixel(bitmap.Width / 2, 0).A == 255;
                    return ShelfPaint.PresentLayered(window, bitmap, location);
                }, animationsAllowed: delegate { return false; })) {
                    surface.Present(visual);
                    check(firstHidden && centerTransparent && realBorder && calls == 1 && surface.Uploaded && Native.IsWindowVisible(owned),
                        "First production-opacity upload is transparent inside and completed while the HWND was still hidden");
                    Bitmap retained = surface.Pixels;
                    for (int i = 0; i < 20; i++) surface.Present(visual);
                    check(calls == 1 && Object.ReferenceEquals(retained, surface.Pixels), "Idle updates reuse the committed alpha image without repainting");
                    surface.Present(FocusIndicatorVisual.Create(native, FocusIndicatorMode.MutedFrame, "Coral", false));
                    check(calls == 2 && centerTransparent && realBorder && !Object.ReferenceEquals(retained, surface.Pixels),
                        "Visible palette/style changes upload another hollow production-opacity image");
                    surface.Dispose(); surface.Dispose();
                    check(!Native.IsWindow(owned) && surface.Window == IntPtr.Zero && surface.Pixels == null && surface.Region == null && !surface.Uploaded,
                        "Repeated disposal destroys the native surface before releasing retained image and shape");
                    bool refused = false;
                    try { surface.Present(visual); } catch (InvalidOperationException) { refused = true; }
                    check(refused && surface.Window == IntPtr.Zero, "A disposed renderer cannot recreate or show a window");
                }

                // First-upload and recolor failures exercise the production
                // manager, which must remove the whole surface and permit retry.
                foreach (int failureAt in new[] { 1, 2 }) {
                    int uploads = 0; IntPtr failedWindow = IntPtr.Zero; FocusIndicatorSurface created = null;
                    using (var indicator = new FocusIndicator(Api(target), delegate {
                        created = new FocusIndicatorSurface(false, delegate(IntPtr window, Bitmap bitmap, Point location) {
                            failedWindow = window; uploads++;
                            return uploads != failureAt && ShelfPaint.PresentLayered(window, bitmap, location);
                        }, animationsAllowed: delegate { return false; });
                        return created;
                    })) {
                        indicator.Update(1, "Original", false);
                        if (failureAt == 2) {
                            check(indicator.Visible && indicator.Error == null, "Recolor failure fixture first shows a valid alpha border");
                            indicator.Update(2, "Coral", false);
                        }
                        check(!indicator.Visible && indicator.Error != null && !Native.IsWindow(failedWindow) && created.Pixels == null && created.Region == null,
                            "Upload failure destroys the owned HWND and releases buffers at operation " + failureAt);
                        indicator.Update(1, "Mint", false);
                        check(indicator.Visible && indicator.Error == null, "A later successful upload recovers after failure at operation " + failureAt);
                    }
                    check(!Native.IsWindow(failedWindow), "Upload-failure recovery cleans up its replacement HWND on exit");
                }

                // Native publication may be cancelled during an injected upload.
                // It must not commit disposed pixels or reach SHOWWINDOW afterward.
                FocusIndicatorSurface cancelled = null; IntPtr cancelledWindow = IntPtr.Zero;
                cancelled = new FocusIndicatorSurface(false, delegate(IntPtr window, Bitmap bitmap, Point location) {
                    cancelledWindow = window; cancelled.Dispose(); return true;
                });
                using (cancelled) {
                    bool refused = false;
                    try { cancelled.Present(visual); } catch (InvalidOperationException) { refused = true; }
                    check(refused && !Native.IsWindow(cancelledWindow) && cancelled.Window == IntPtr.Zero && cancelled.Pixels == null && !cancelled.Uploaded,
                        "Reentrant cancellation during upload cannot expose a window or retain a disposed alpha image");
                }

                uint gdi = GetGuiResources(GetCurrentProcess(), 0), user = GetGuiResources(GetCurrentProcess(), 1);
                for (int i = 0; i < 12; i++) {
                    using (var failure = new FocusIndicatorSurface(false, delegate { return false; })) {
                        try { failure.Present(visual); } catch (InvalidOperationException) { }
                    }
                }
                check(GetGuiResources(GetCurrentProcess(), 0) <= gdi + 4 && GetGuiResources(GetCurrentProcess(), 1) <= user + 2,
                    "Repeated first-upload failures release native regions, images and HWND resources");
            }
            check(FocusedWindowNative.Foreground() == foreground && Cursor.Position == pointer,
                "Alpha lifecycle tests preserve the user's foreground and pointer");
        }
    }
}
