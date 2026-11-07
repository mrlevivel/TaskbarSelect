using System;

namespace TaskbarSelect
{
    internal static class SnapWorkAreaTests
    {
        static Native.Rect Rect(int l, int t, int r, int b)
        { return new Native.Rect { Left = l, Top = t, Right = r, Bottom = b }; }
        internal static void Run(Action<bool, string> assert)
        {
            var area = new AreaRecord { Monitor = Rect(3816, 1447, 5736, 2527),
                Original = Rect(3816, 1447, 5736, 2479), Applied = Rect(3816, 1447, 5736, 2527) };
            Native.Rect result;
            foreach (int seam in new[] { 4200, 4776, 5326 }) {
                foreach (var frame in new[] { Rect(3816, 1447, seam, 2479), Rect(seam, 1447, 5736, 2479),
                    Rect(seam, 1963, 5736, 2479) }) {
                    assert(SnapAreaPolicy.Expand(frame, area, out result), "Snap edge matches released reservation");
                    assert(result.Bottom == 2527 && result.Top == frame.Top && result.Left == frame.Left && result.Right == frame.Right,
                        "Snap fills released edge without shifting split or top");
                    Native.Rect again;
                    assert(!SnapAreaPolicy.Expand(result, area, out again), "Snap repair is idempotent");
                }
            }
            foreach (var frame in new[] { Rect(3816, 1447, 4776, 1963), Rect(4000, 1500, 4700, 2400),
                Rect(3816, 1447, 5736, 2527), Rect(3000, 1447, 5736, 2479), Rect(3816, 1447, 3816, 2479) })
                assert(!SnapAreaPolicy.Expand(frame, area, out result), "Unrelated, full, or invalid frame stays untouched");
            foreach (int offset in new[] { -2, -1, 0, 1, 2 })
                assert(SnapAreaPolicy.Expand(Rect(3816, 1447, 4776, 2479 + offset), area, out result) && result.Bottom == 2527,
                    "Snap boundary tolerates two pixels of native rounding");
            assert(!SnapAreaPolicy.Expand(Rect(3816, 1447, 4776, 2476), area, out result), "No guessed taskbar boundary");
            var monitor = Rect(-1920, -1080, 0, 0);
            foreach (var original in new[] { Rect(-1880, -1080, 0, 0), Rect(-1920, -1040, 0, 0),
                Rect(-1920, -1080, -40, 0), Rect(-1920, -1080, 0, -40) }) {
                var edge = new AreaRecord { Monitor = monitor, Original = original, Applied = monitor };
                assert(SnapAreaPolicy.Expand(original, edge, out result) && AreaPolicy.Equal(result, monitor),
                    "All taskbar edges support negative monitor coordinates");
            }
            var preserved = new AreaRecord { Monitor = area.Monitor, Original = Rect(3840, 1447, 5736, 2479),
                Applied = Rect(3840, 1447, 5736, 2527) };
            assert(SnapAreaPolicy.Expand(preserved.Original, preserved, out result) && result.Left == 3840,
                "Another appbar reservation is preserved");
            var invalid = new AreaRecord { Monitor = monitor, Original = monitor, Applied = Rect(-1920, -1080, 0, 40) };
            assert(!SnapAreaPolicy.Expand(monitor, invalid, out result), "Invalid recovery record cannot expand windows");
            assert(!SnapAreaPolicy.Expand(monitor, null, out result), "Missing record cannot expand windows");
            var telegramOuter = Rect(5312, 1447, 5736, 2479);
            var telegramFrame = Rect(5319, 1447, 5729, 2472);
            assert(SnapAreaPolicy.ExpandFrame(telegramOuter, telegramFrame, area, out result) &&
                result.Bottom == 2534 && result.Left == telegramOuter.Left && result.Right == telegramOuter.Right,
                "Legacy raw-border contact fills the reclaimed edge including invisible bottom border");
            var chromeOuter = Rect(3809, 1447, 5333, 2486);
            var chromeFrame = Rect(3816, 1447, 5326, 2479);
            assert(SnapAreaPolicy.ExpandFrame(chromeOuter, chromeFrame, area, out result) &&
                result.Bottom == 2534 && result.Right == chromeOuter.Right, "Native DWM contact preserves invisible border and snap seam");
            assert(!SnapAreaPolicy.ExpandFrame(chromeFrame, chromeOuter, area, out result), "Invalid DWM bounds cannot authorize correction");
        }
    }
}
