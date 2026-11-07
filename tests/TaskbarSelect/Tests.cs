using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSelect
{
    internal static class Tests
    {
        static int count;
        static void PinIndicatorChecks()
        {
            PinnedCornerPaintTests.Run(Assert);
            ReservedPinFocusTests.Run(Assert);
            AdministratorWindowIndicatorTests.Run(Assert);
            PinnedRailPaintTests.Run(Assert);
            PinnedWindowIndicatorTests.Run(Assert);
            PinIndicatorObservationTests.Run(Assert);
        }
        // These injected/pure policies are also part of the package aggregate.
        // Keep this entry point free of the normal desktop application startup.
        internal static int RunWakePolicies()
        {
            try {
                Assert(WorkAreaGuardTests.RunPureTest() == 0, "Work-area ownership and bounded mutation regressions");
                Assert(WorkAreaReclaimTests.RunPureTest() == 0, "Delayed wake reclaim regressions");
                Assert(SnapCorrectionPolicyTests.RunPureTest() == 0, "Bounded window correction regressions");
                Assert(DisplayWakePolicyTests.RunPureTest() == 0, "Session display wake epoch regressions");
                Assert(WakeRecoveryDiagnostics.RunPureTest() == 0, "Private numeric diagnostics regressions");
                Assert(SnapLayoutReceiptTests.RunPureTest() == 0, "Owned window-layout cleanup regressions");
                Assert(SnapLayoutAdversarialTests.RunPureTest() == 0, "Ambiguous and reentrant layout cleanup regressions");
                Assert(SnapLayoutPropertyTests.RunPureTest() == 0, "Window property absence and verified marker ownership regressions");
                Assert(SnapLayoutTransitionTests.RunPureTest() == 0, "Scoped work-area restoration and pending action fences");
                Assert(SnapLayoutTransitionAdversarialTests.RunPureTest() == 0, "Scoped transition lifecycle and return-boundary regressions");
                Assert(SnapLayoutRetirementTests.RunPureTest() == 0, "New-wake changed-candidate receipt retirement pipeline");
                Assert(SnapLayoutRetirementAdversarialTests.RunPureTest() == 0, "Receipt retirement ownership and reentrancy regressions");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunPinRails()
        {
            int start = count;
            try {
                PinIndicatorChecks();
                WindowStateActionsTests.Run(Assert);
                Console.WriteLine("PASS: isolated pin-corner rendering, tracking and window actions (" + (count - start) + " checks).");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunPinCornerNative()
        {
            int start = count;
            try {
                PinnedCornerPaintTests.RunNativeStack(Assert);
                Console.WriteLine("PASS: isolated no-input native pin-corner ordering (" + (count - start) + " checks).");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunSearchPlacement()
        {
            int start = count;
            try {
                GhostAppCatalogTests.Run(Assert);
                GhostLaunchCoordinatorTests.Run(Assert);
                GhostSearchPopupTests.RunResultActions(Assert);
                GhostSearchIntegrationTests.Run(Assert);
                Console.WriteLine("PASS: app search identity and placement (" + (count - start) + " checks); no real app launch or window movement.");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        static void Assert(bool value, string name)
        {
            if (!value) throw new Exception(name);
            count++;
        }
        static void WaitFor(Func<bool> ready)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (!ready()) {
                if (DateTime.UtcNow > deadline) throw new Exception("Timed out waiting for an asynchronous check");
                Thread.Sleep(5); Application.DoEvents();
            }
        }
        static void SyntheticMouse(TaskbarMover mover, int message, TaskbarMover.Point point)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var dataType = typeof(TaskbarMover).GetNestedType("MouseData", System.Reflection.BindingFlags.NonPublic);
            object data = Activator.CreateInstance(dataType);
            dataType.GetField("Position", flags).SetValue(data, point);
            dataType.GetField("Flags", flags).SetValue(data, 1u);
            dataType.GetField("Time", flags).SetValue(data, unchecked((uint)Environment.TickCount));
            IntPtr memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf(dataType));
            try {
                System.Runtime.InteropServices.Marshal.StructureToPtr(data, memory, false);
                object wire = System.Runtime.InteropServices.Marshal.PtrToStructure(memory, dataType);
                uint pointerFlags = (uint)dataType.GetField("Flags", flags).GetValue(wire);
                uint pointerTime = (uint)dataType.GetField("Time", flags).GetValue(wire);
                Assert(pointerFlags == 1u && !TaskbarNativeSelection.IsPhysicalPointer(pointerFlags) &&
                    TaskbarNativeSelection.IsFreshGesture(pointerTime, unchecked((uint)Environment.TickCount)),
                    "Synthetic mouse fixture delivers fresh injected provenance through the actual marshaled hook structure");
                typeof(TaskbarMover).GetMethod("MouseEvent", flags).Invoke(mover, new object[] { 0, new IntPtr(message), memory });
            } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
        }
        // Enabled movement fixtures receive only explicit injected gestures.
        // Assert that isolated construction registered no desktop callbacks.
        static void FenceNativeMoverHooks(TaskbarMover mover)
        {
            if (mover.Enabled) throw new Exception("Movement fixture hooks must be checked before enabling");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            foreach (string name in new[] { "mouseHook", "foregroundHook", "shelfRestoreHook" }) {
                var field = typeof(TaskbarMover).GetField(name, flags);
                Assert((IntPtr)field.GetValue(mover) == IntPtr.Zero, "Movement fixture has no registered " + name + " before enabling");
            }
            Assert(mover.Available, "Isolated injected input remains available without desktop hooks");
        }
        static void FenceOwnedNativeMouseInput(TaskbarMover mover)
        {
            Assert(!mover.Enabled, "Owned native placement fixture remains disabled for desktop input");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var field = typeof(TaskbarMover).GetField("mouseHook", flags);
            var hook = (IntPtr)field.GetValue(mover);
            if (hook != IntPtr.Zero) {
                var native = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                if (!(bool)typeof(TaskbarMover).GetMethod("UnhookWindowsHookEx", native).Invoke(null, new object[] { hook }))
                    throw new Exception("Could not detach the owned native placement fixture's mouse hook");
                field.SetValue(mover, IntPtr.Zero);
            }
            Assert((IntPtr)field.GetValue(mover) == IntPtr.Zero, "Owned native placement fixture receives no global mouse callbacks");
            // Retain native window events: clipped immediate-return coverage uses
            // their move/resize invalidation and verified return-placement memory.
        }
        static byte[] Approval(uint state)
        {
            var value = new byte[12]; Array.Copy(BitConverter.GetBytes(state), value, 4); return value;
        }

        static Native.Rect PlacementRect(int x, int y, int width, int height)
        {
            return new Native.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        }

        static Native.Rect OuterFrame(Native.Rect visible, int left, int top, int right, int bottom)
        {
            return new Native.Rect { Left = visible.Left - left, Top = visible.Top - top,
                Right = visible.Right + right, Bottom = visible.Bottom + bottom };
        }

        // Independently callable without Run(), window creation, hooks, or native calls.
        internal static int RunWindowPlacementCalculations()
        {
            int start = count;
            ReturnPlacementChecks();
            RelativeMoveChainChecks();
            ArrangedRelativeMoveChainChecks();
            var hidden = PlacementRect(-1920, -120, 1920, 1080);
            var taskbar = PlacementRect(0, 0, 1920, 1032);
            foreach (bool relative in new[] { false, true }) {
                foreach (int[] margins in new[] { new[] { 7, 0, 7, 7 }, new[] { 11, 3, 5, 9 }, new[] { 0, 0, 0, 0 } }) {
                    var visible = PlacementRect(hidden.Right - 800, hidden.Bottom - 900, 800, 900);
                    var outer = OuterFrame(visible, margins[0], margins[1], margins[2], margins[3]);
                    var mapped = TaskbarMover.MapFramedWindow(outer, visible, hidden, taskbar, relative);
                    int height = relative ? 860 : 900;
                    var expectedVisible = PlacementRect(taskbar.Right - 800, taskbar.Bottom - height, 800, height);
                    Assert(AreaPolicy.Equal(mapped, OuterFrame(expectedVisible, margins[0], margins[1], margins[2], margins[3])),
                        "Visible right/bottom edges remain flush with asymmetric or absent frame margins, relative=" + relative);
                    Assert(AreaPolicy.Equal(TaskbarMover.MapFramedWindow(mapped, expectedVisible, taskbar, hidden, relative), outer),
                        "Flush frame roundtrip restores this non-clipped window, relative=" + relative);
                    if (!relative) Assert(mapped.Right - mapped.Left == outer.Right - outer.Left &&
                        mapped.Bottom - mapped.Top == outer.Bottom - outer.Top, "Keep-size preserves outer pixel dimensions when the visible frame fits");
                }
            }
            foreach (bool relative in new[] { false, true }) {
                var fullHeight = PlacementRect(-960, -120, 960, 1080);
                var outer = OuterFrame(fullHeight, 7, 0, 7, 7);
                var mapped = TaskbarMover.MapFramedWindow(outer, fullHeight, hidden, taskbar, relative);
                var expected = PlacementRect(960, 0, 960, 1032);
                Assert(AreaPolicy.Equal(mapped, OuterFrame(expected, 7, 0, 7, 7)),
                    "Tall snapped-shaped frame fits visible work height without right/bottom gaps");
                var back = TaskbarMover.MapFramedWindow(mapped, expected, taskbar, hidden, relative);
                Assert(back.Right == outer.Right && back.Top == outer.Top && back.Bottom - back.Top == (relative ? 1087 : 1039),
                    "Clipped keep-size does not silently regrow; proportional mode restores this height");
            }
            var normal = PlacementRect(-1500, 60, 800, 600);
            foreach (bool relative in new[] { false, true }) {
                var fallback = relative ? TaskbarMover.MapRelativeToWorkArea(normal, hidden, taskbar) : TaskbarMover.MapToWorkArea(normal, hidden, taskbar);
                foreach (var invalid in new[] { new Native.Rect(), PlacementRect(-1501, 60, 800, 600),
                    PlacementRect(-1500, 60, 801, 600), PlacementRect(-1500, 59, 800, 600), PlacementRect(-1500, 60, 800, 601) }) {
                    Assert(!TaskbarMover.ValidVisibleFrame(normal, invalid) &&
                        AreaPolicy.Equal(TaskbarMover.MapFramedWindow(normal, invalid, hidden, taskbar, relative), fallback),
                        "Empty or non-contained DWM geometry falls back to outer bounds");
                }
            }
            // Every corner, interior rounding, negative origins, and left/top taskbar offsets.
            var inset = PlacementRect(48, 40, 1872, 1040);
            foreach (var target in new[] { taskbar, inset, PlacementRect(400, -400, 1920, 1080) }) {
                foreach (int xFraction in new[] { 0, 1, 2, 3, 4 }) foreach (int yFraction in new[] { 0, 1, 2, 3, 4 }) {
                    var visible = PlacementRect(hidden.Left + 1120 * xFraction / 4, hidden.Top + 480 * yFraction / 4, 800, 600);
                    var outer = OuterFrame(visible, 7, 0, 7, 7);
                    var mapped = TaskbarMover.MapFramedWindow(outer, visible, hidden, target, false);
                    var mappedVisible = OuterFrame(mapped, -7, 0, -7, -7);
                    Assert(mappedVisible.Left >= target.Left && mappedVisible.Top >= target.Top &&
                        mappedVisible.Right <= target.Right && mappedVisible.Bottom <= target.Bottom,
                        "Only visible bounds are constrained at every sampled position");
                    Assert(AreaPolicy.Equal(TaskbarMover.MapFramedWindow(mapped, mappedVisible, target, hidden, false), outer),
                        "Unclipped keep-size positions roundtrip exactly on the sampled grid");
                    for (int repeat = 0; repeat < 3; repeat++) {
                        var back = TaskbarMover.MapFramedWindow(mapped, mappedVisible, target, hidden, false);
                        var next = TaskbarMover.MapFramedWindow(back, OuterFrame(back, -7, 0, -7, -7), hidden, target, false);
                        Assert(AreaPolicy.Equal(next, mapped) && AreaPolicy.Equal(back, outer),
                            "Repeated roundtrip does not accumulate mapping drift");
                        mapped = next; mappedVisible = OuterFrame(mapped, -7, 0, -7, -7);
                    }
                }
            }
            var full = PlacementRect(0, 0, 800, 600);
            Assert(AreaPolicy.Equal(TaskbarMover.MapToWorkArea(full, full, PlacementRect(100, 100, 1000, 800)), PlacementRect(100, 100, 800, 600)),
                "A full-span source has ambiguous anchoring and deterministically stays top-left");
            Assert(AreaPolicy.Equal(TaskbarMover.MapToWorkArea(PlacementRect(-100, -100, 2400, 1400), hidden, taskbar), taskbar),
                "Oversized windows remain constrained without proportional keep-size growth");
            var tiny = TaskbarMover.MapToWorkArea(new Native.Rect(), new Native.Rect(), taskbar);
            Assert(tiny.Right - tiny.Left == 1 && tiny.Bottom - tiny.Top == 1, "Degenerate keep-size geometry remains valid");
            // Geometry-only regression from the read-only audit; no application handles.
            var monitor4 = PlacementRect(3816, 1447, 1920, 1080);
            var monitor1 = PlacementRect(2560, 367, 1920, 1032);
            var telegramOuter = PlacementRect(5312, 1447, 424, 1032);
            var telegramFrame = PlacementRect(5319, 1447, 410, 1025);
            var telegramMoved = TaskbarMover.MapFramedWindow(telegramOuter, telegramFrame, monitor4, monitor1, false, true);
            Assert(AreaPolicy.Equal(telegramMoved, PlacementRect(4063, 367, 424, 1032)),
                "Audited arranged outer-right contact normalizes visible right edge without resizing or inventing stale bottom anchoring");
            var telegramMovedFrame = OuterFrame(telegramMoved, -7, 0, -7, -7);
            foreach (bool remainsArranged in new[] { false, true }) {
                var returned = TaskbarMover.MapFramedWindow(telegramMoved, telegramMovedFrame, monitor1, monitor4, false, remainsArranged);
                Assert(AreaPolicy.Equal(returned, PlacementRect(5319, 1447, 424, 1032)),
                    "Audited geometry returns to canonical visible-right anchor with or without persistent arranged state");
                var again = TaskbarMover.MapFramedWindow(returned, OuterFrame(returned, -7, 0, -7, -7), monitor4, monitor1, false, remainsArranged);
                Assert(AreaPolicy.Equal(again, telegramMoved), "Arranged edge normalization is one-time, not cumulative drift");
            }
            var floatingMoved = TaskbarMover.MapFramedWindow(telegramOuter, telegramFrame, monitor4, monitor1, false);
            Assert(floatingMoved.Right == monitor1.Right, "A non-arranged window keeps its deliberate 7px visible inset");
            var chromeOuter = PlacementRect(3809, 1447, 1524, 1039);
            var chromeFrame = PlacementRect(3816, 1447, 1510, 1032);
            Assert(AreaPolicy.Equal(TaskbarMover.MapFramedWindow(chromeOuter, chromeFrame, monitor4, monitor1, false, true),
                PlacementRect(2553, 367, 1524, 1039)),
                "Audited Chrome visible height fits exactly; invisible bottom border must not cause a 7px shrink");
            var insetOuter = PlacementRect(1120, 480, 800, 600);
            var insetFrame = OuterFrame(insetOuter, -7, 0, -7, -7);
            foreach (bool relative in new[] { false, true }) {
                var normalized = TaskbarMover.MapFramedWindow(insetOuter, insetFrame, PlacementRect(0, 0, 1920, 1080), taskbar, relative, true);
                Assert(normalized.Right == taskbar.Right + 7 && normalized.Bottom == taskbar.Bottom + 7,
                    "Exact arranged outer-right/bottom contacts normalize both visible edges in either sizing mode");
            }
            var nearlyFull = PlacementRect(hidden.Left + 100, hidden.Top + 29, 800, 1025);
            var rounded = TaskbarMover.MapToWorkArea(nearlyFull, hidden, taskbar);
            var roundedBack = TaskbarMover.MapToWorkArea(rounded, taskbar, hidden);
            Assert(roundedBack.Top == hidden.Top + 31 && roundedBack.Bottom - roundedBack.Top == 1025,
                "Integer position quantization can lose 2px with only 7px destination travel, without resizing");
            Assert(AreaPolicy.Equal(TaskbarMover.MapToWorkArea(roundedBack, hidden, taskbar), rounded),
                "Quantized interior placement stabilizes after one roundtrip instead of accumulating drift");
            Console.WriteLine("PASS: " + (count - start) + " pure window-placement checks (no native calls).");
            return count - start;
        }

        static WindowHistory.Entry PlacementEntry(Native.MonitorInfo monitor)
        {
            return new WindowHistory.Entry { Window = new IntPtr(42), Process = 123, ProcessStarted = 456,
                Monitor = monitor, IsNormal = true };
        }

        static void ReturnPlacementChecks()
        {
            var source = new Native.MonitorInfo { Device = "source", Monitor = PlacementRect(3816, 1447, 1920, 1080),
                Work = PlacementRect(3816, 1447, 1920, 1080) };
            var target = new Native.MonitorInfo { Device = "target", Monitor = PlacementRect(2560, 367, 1920, 1080),
                Work = PlacementRect(2560, 367, 1920, 1032) };
            var originalVisible = PlacementRect(4776, 1447, 960, 1080);
            var original = OuterFrame(originalVisible, 7, 0, 7, 7);
            var clipped = TaskbarMover.MapFramedWindow(original, originalVisible, source.Work, target.Work, false);
            Assert(original.Bottom - original.Top - (clipped.Bottom - clipped.Top) == 48,
                "Immediate-return fixture actually loses 48px to the destination work area");
            var record = TaskbarMover.ReturnPlacement.Create(PlacementEntry(source), PlacementEntry(target),
                original, originalVisible, clipped, clipped, target, false);
            Native.Rect returned;
            Assert(record != null && TaskbarMover.ReturnPlacement.Take(ref record, PlacementEntry(target), clipped, source, false, out returned) &&
                AreaPolicy.Equal(returned, original) && record == null,
                "Immediate reverse restores exact original outer bounds after height clipping, not floating restore bounds");
            foreach (string mismatch in new[] { "move", "resize", "window", "pid", "start", "mode", "state",
                "sourceWork", "targetWork", "sourceMonitor", "targetMonitor", "sourceDevice", "targetDevice", "flags" }) {
                record = TaskbarMover.ReturnPlacement.Create(PlacementEntry(source), PlacementEntry(target),
                    original, originalVisible, clipped, clipped, target, false);
                var current = PlacementEntry(target); var destination = source; var changed = clipped; bool mode = false;
                switch (mismatch) {
                    case "move": changed.Left++; changed.Right++; break;
                    case "resize": changed.Bottom--; break;
                    case "window": current.Window = new IntPtr(43); break;
                    case "pid": current.Process++; break;
                    case "start": current.ProcessStarted++; break;
                    case "mode": mode = true; break;
                    case "state": current.IsNormal = false; break;
                    case "sourceWork": current.Monitor.Work.Bottom--; break;
                    case "targetWork": destination.Work.Top++; break;
                    case "sourceMonitor": current.Monitor.Monitor.Left--; break;
                    case "targetMonitor": destination.Monitor.Bottom++; break;
                    case "sourceDevice": current.Monitor.Device = "replacement"; break;
                    case "targetDevice": destination.Device = "third"; break;
                    case "flags": destination.Flags = 1; break;
                }
                Assert(!TaskbarMover.ReturnPlacement.Take(ref record, current, changed, destination, mode, out returned) && record == null,
                    "Immediate-return mismatch consumes the sole record: " + mismatch);
            }
            var outside = originalVisible; outside.Top--;
            Assert(TaskbarMover.ReturnPlacement.Create(PlacementEntry(source), PlacementEntry(target),
                OuterFrame(outside, 7, 0, 7, 7), outside, clipped, clipped, target, false) == null,
                "A source visible frame outside its work area cannot authorize an unclipped return");
            var unverified = clipped; unverified.Bottom++;
            Assert(TaskbarMover.ReturnPlacement.Create(PlacementEntry(source), PlacementEntry(target),
                original, originalVisible, clipped, unverified, target, false) == null,
                "A post-move rectangle differing from the requested bounds is not remembered");
            foreach (bool beforeNormal in new[] { false, true }) {
                var before = PlacementEntry(source); var after = PlacementEntry(target);
                before.IsNormal = beforeNormal; after.IsNormal = !beforeNormal;
                Assert(TaskbarMover.ReturnPlacement.Create(before, after, original, originalVisible, clipped, clipped, target, false) == null,
                    "Both sides must be normal to record immediate-return geometry");
            }
            var reused = PlacementEntry(target); reused.ProcessStarted++;
            Assert(TaskbarMover.ReturnPlacement.Create(PlacementEntry(source), reused, original, originalVisible, clipped, clipped, target, false) == null,
                "Post-move process identity must still match the pre-move capture");
            var alteredArea = PlacementEntry(target); alteredArea.Monitor.Work.Bottom--;
            Assert(TaskbarMover.ReturnPlacement.Create(PlacementEntry(source), alteredArea, original, originalVisible, clipped, clipped, target, false) == null,
                "Post-move monitor/work area must match the requested destination");
            record = null;
            var at = source; var next = target; var outer = original;
            for (int iteration = 0; iteration < 12; iteration++) {
                var visible = OuterFrame(outer, -7, 0, -7, -7);
                Native.Rect mapped;
                bool remembered = TaskbarMover.ReturnPlacement.Take(ref record, PlacementEntry(at), outer, next, false, out mapped);
                if (!remembered) mapped = TaskbarMover.MapFramedWindow(outer, visible, at.Work, next.Work, false);
                Assert(remembered == (iteration > 0) && AreaPolicy.Equal(mapped, iteration % 2 == 0 ? clipped : original),
                    "Single-record clipped roundtrips alternate exact bounds without drift, iteration " + iteration);
                record = TaskbarMover.ReturnPlacement.Create(PlacementEntry(at), PlacementEntry(next), outer, visible, mapped, mapped, next, false);
                outer = mapped; var swap = at; at = next; next = swap;
            }
            var relative = TaskbarMover.MapFramedWindow(original, originalVisible, source.Work, target.Work, true);
            record = TaskbarMover.ReturnPlacement.Create(PlacementEntry(source), PlacementEntry(target), original, originalVisible, relative, relative, target, true);
            Assert(TaskbarMover.ReturnPlacement.Take(ref record, PlacementEntry(target), relative, source, true, out returned) && AreaPolicy.Equal(returned, original),
                "Immediate-return record also requires and preserves the same relative-sizing mode");
            record = TaskbarMover.ReturnPlacement.Create(PlacementEntry(source), PlacementEntry(target), original, originalVisible, clipped, clipped, target, false);
            TaskbarMover.ReturnPlacement.InvalidateManualMove(ref record, new IntPtr(43));
            Assert(record != null, "Another window's native move/resize does not invalidate the matching-window record");
            TaskbarMover.ReturnPlacement.InvalidateManualMove(ref record, new IntPtr(42));
            Assert(record == null && !TaskbarMover.ReturnPlacement.Take(ref record, PlacementEntry(target), clipped, source, false, out returned),
                "Native move/resize start invalidates memory even if geometry is subsequently restored manually");
            var insetOuter = PlacementRect(5312, 1447, 424, 1032);
            var insetVisible = OuterFrame(insetOuter, -7, 0, -7, -7);
            foreach (bool arranged in new[] { false, true }) {
                var moved = TaskbarMover.MapFramedWindow(insetOuter, insetVisible, source.Work, target.Work, false, arranged);
                record = TaskbarMover.ReturnPlacement.Create(PlacementEntry(source), PlacementEntry(target), insetOuter, insetVisible,
                    moved, moved, target, false, arranged);
                Assert(record != null && AreaPolicy.Equal(record.Original, insetOuter), "Return record retains measured source geometry");
                Assert(TaskbarMover.ReturnPlacement.Take(ref record, PlacementEntry(target), moved, source, false, out returned) &&
                    AreaPolicy.Equal(returned, arranged ? PlacementRect(5319, 1447, 424, 1032) : insetOuter),
                    "Only arranged exact outer contact canonicalizes the return edge without resizing");
            }
        }

        internal static int RunRelativeMoveGeometry(bool native = false)
        {
            int start = count;
            try {
                RelativeMoveChainChecks();
                ArrangedRelativeMoveChainChecks();
                Console.WriteLine("PASS: " + (count - start) + " relative move geometry checks.");
                if (native) RelativeMoveNativeCycle();
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        static void RelativeMoveNativeCycle()
        {
            var displays = Native.Displays().OrderBy(d => d.Width).ToArray();
            if (displays.Length < 2 || (long)displays[0].Width * 4 >= (long)displays[displays.Length - 1].Width * 3) {
                Console.WriteLine("SKIP: native cycle needs a substantially smaller display.");
                return;
            }
            var small = displays[0]; var origin = displays[displays.Length - 1];
            Native.MonitorInfo area;
            Assert(Native.TryMonitor(origin.Device, out area), "The native cycle origin has a work area");
            const int minWidth = 420, width = 460, height = 350;
            var route = displays.Skip(1).Take(displays.Length - 2).Concat(new[] { small, origin }).ToArray();
            foreach (bool arranged in new[] { false, true })
            using (var window = new ShelfMovementFixture { Text = "Owned relative sizing fixture", Opacity = 0,
                MinimumSize = new Size(minWidth, 180), StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(area.Work.Right - width, area.Work.Top + 30, width, height) })
            using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { throw new InvalidOperationException("Replay forbidden"); },
                delegate { return window.Handle; }, false)) {
                FenceNativeMoverHooks(mover);
                mover.Enabled = true; mover.ScaleWindowSizeToMonitor = true;
                mover.MoveApi.Modifiers = delegate { return 0; };
                mover.MoveApi.Focus = delegate(IntPtr hwnd) { return hwnd == window.Handle; };
                // Inject only the Windows snap-state observation. Positioning and
                // the application's minimum width still use real native geometry.
                if (arranged) mover.MoveApi.Capture = delegate(IntPtr hwnd) {
                    var entry = WindowHistory.Capture(hwnd);
                    if (entry != null && entry.IsNormal) entry.ArrangedBounds = entry.VisibleBounds;
                    return entry;
                };
                window.Show(); Application.DoEvents();
                if (arranged) {
                    Native.Rect outer;
                    var frame = ReadPlacementFrame(window.Handle, out outer);
                    window.Left += area.Work.Right - frame.Right;
                    Application.DoEvents();
                }
                var original = WindowHistory.Capture(window.Handle);
                Assert(original != null && original.VisibleBounds.HasValue && original.Monitor.Device == origin.Device,
                    "The narrow fixture begins near its origin's right edge");
                for (int repeat = 0; repeat < 3; repeat++) foreach (var destination in route) {
                    Assert(mover.MoveFocusedWindow(window.Handle, destination, MonitorShortcutPolicy.DisplayNumber(destination)),
                        "Owned window moves through the shortcut path: " + mover.LastStatus);
                    Application.DoEvents();
                    var actual = WindowHistory.Capture(window.Handle);
                    Assert(actual != null && actual.VisibleBounds.HasValue && actual.Monitor.Device == destination.Device,
                        "The narrow fixture reaches " + destination.Device);
                    if (destination == small) {
                        Assert(actual.VisibleBounds.Value.Right - actual.VisibleBounds.Value.Left >= minWidth,
                            "The smaller display forces the fixture to respect its minimum width");
                        Native.Rect outer;
                        Assert(TaskbarMover.ValidVisibleFrame(actual.Monitor.Work, ReadPlacementFrame(window.Handle, out outer)),
                            "The shared mover fits the accepted window inside the smaller display");
                    }
                    if (destination == origin)
                        Assert(AreaPolicy.Equal(actual.VisibleBounds.Value, original.VisibleBounds.Value),
                            "After the small monitor clamps its width, the window returns to its exact starting rectangle");
                }
                Assert(GetForegroundWindow() != window.Handle, "The native cycle never requires activating the fixture");
            }
            Console.WriteLine("PASS: three native relative sizing cycles per placement type through " + displays.Length +
                " displays (snap state injected, native bounds and minimum width observed).");
        }

        static void ArrangedRelativeMoveChainChecks()
        {
            var monitors = new[] {
                new Native.MonitorInfo { Device = "DISPLAY4", Work = PlacementRect(3816, 1447, 1920, 1080) },
                new Native.MonitorInfo { Device = "DISPLAY1", Work = PlacementRect(2560, 367, 1920, 1080) },
                new Native.MonitorInfo { Device = "DISPLAY2", Work = PlacementRect(0, 0, 2560, 1392) },
                new Native.MonitorInfo { Device = "DISPLAY3", Work = PlacementRect(2036, 1447, 1024, 600) }
            };
            var outer = new[] { PlacementRect(5347, 1447, 396, 1087), PlacementRect(4091, 367, 396, 1087),
                PlacementRect(2044, 0, 523, 1399), PlacementRect(2671, 1447, 396, 607) };
            var frame = outer.Select(r => OuterFrame(r, -7, 0, -7, -7)).ToArray();
            var entry = monitors.Select((m, i) => {
                var e = PlacementEntry(m); e.ArrangedBounds = outer[i]; e.VisibleBounds = outer[i]; return e;
            }).ToArray();
            var first = TaskbarMover.ReturnPlacement.CreateRelative(null, entry[0], entry[1],
                outer[0], frame[0], outer[1], frame[1], monitors[1]);
            Native.Rect requested;
            Assert(first != null && TaskbarMover.ReturnPlacement.TryMapRelative(first, entry[1], outer[1], frame[1],
                monitors[2], monitors[0], out requested) && AreaPolicy.Equal(requested, outer[2]),
                "The arranged right edge scales from the original display across the first two hops");
            var second = TaskbarMover.ReturnPlacement.CreateRelative(first, entry[1], entry[2],
                outer[1], frame[1], outer[2], frame[2], monitors[2]);
            Assert(second != null && TaskbarMover.ReturnPlacement.TryMapRelative(second, entry[2], outer[2], frame[2],
                monitors[3], monitors[0], out requested) && AreaPolicy.Equal(requested, PlacementRect(2849, 1447, 218, 607)),
                "The small monitor requests the arranged window's original proportions");
            var accepted = PlacementRect(2849, 1447, 396, 607);
            Native.Rect fitted;
            Assert(TaskbarMover.TryFitMovedWindow(accepted, OuterFrame(accepted, -7, 0, -7, -7), monitors[3].Work, out fitted) &&
                AreaPolicy.Equal(fitted, outer[3]), "The snapped minimum-width window shifts left without resizing");
            var third = TaskbarMover.ReturnPlacement.CreateRelative(second, entry[2], entry[3],
                outer[2], frame[2], fitted, frame[3], monitors[3]);
            Assert(third != null && TaskbarMover.ReturnPlacement.TryMapRelative(third, entry[3], fitted, frame[3],
                monitors[0], monitors[0], out requested) && AreaPolicy.Equal(requested, outer[0]),
                "A snapped window returns to the original 396px width after crossing the small monitor");
            var unsnapped = entry[3].Copy(); unsnapped.ArrangedBounds = null;
            Assert(!TaskbarMover.ReturnPlacement.TryMapRelative(third, unsnapped, fitted, frame[3], monitors[0], monitors[0], out requested),
                "Unsnapping between moves starts a new layout even if the window bounds match");
        }

        static void RelativeMoveChainChecks()
        {
            var origin = new Native.MonitorInfo { Device = @"\\.\DISPLAY4", Monitor = PlacementRect(-1920, 0, 1920, 1080),
                Work = PlacementRect(-1920, 0, 1920, 1080) };
            var second = new Native.MonitorInfo { Device = @"\\.\DISPLAY1", Monitor = PlacementRect(2560, 0, 1920, 1080),
                Work = PlacementRect(2560, 0, 1920, 1032) };
            var main = new Native.MonitorInfo { Device = @"\\.\DISPLAY2", Monitor = PlacementRect(0, 0, 2560, 1440),
                Work = PlacementRect(0, 0, 2560, 1392), Flags = 1 };
            var small = new Native.MonitorInfo { Device = @"\\.\DISPLAY3", Monitor = PlacementRect(0, 1440, 1024, 600),
                Work = PlacementRect(0, 1440, 1024, 600) };
            var originalVisible = PlacementRect(-380, 20, 380, 1020);
            var original = OuterFrame(originalVisible, 7, 0, 7, 7);
            var secondVisible = PlacementRect(4100, 19, 380, 975);
            var secondOuter = OuterFrame(secondVisible, 7, 0, 7, 7);
            var firstLeg = TaskbarMover.ReturnPlacement.CreateRelative(null, PlacementEntry(origin), PlacementEntry(second),
                original, originalVisible, secondOuter, secondVisible, second);
            Assert(firstLeg != null && firstLeg.HasRelativeAnchor, "The first relative move retains the origin window layout");

            Native.Rect requested;
            var mainVisible = PlacementRect(2053, 26, 507, 1315);
            Assert(TaskbarMover.ReturnPlacement.TryMapRelative(firstLeg, PlacementEntry(second), secondOuter, secondVisible,
                main, origin, out requested) && AreaPolicy.Equal(requested, OuterFrame(mainVisible, 7, 0, 7, 7)),
                "4→1→2 preserves proportional width, height, and right-edge placement");
            // An app can acquire different invisible frame margins after moving.
            var mainOuter = OuterFrame(mainVisible, 9, 1, 9, 9);
            var secondLeg = TaskbarMover.ReturnPlacement.CreateRelative(firstLeg, PlacementEntry(second), PlacementEntry(main),
                secondOuter, secondVisible, mainOuter, mainVisible, main);
            Assert(secondLeg != null && secondLeg.HasRelativeAnchor, "A verified change of frame margins keeps the origin layout");

            var smallRequestedVisible = PlacementRect(821, 1451, 203, 567);
            Assert(TaskbarMover.ReturnPlacement.TryMapRelative(secondLeg, PlacementEntry(main), mainOuter, mainVisible,
                small, origin, out requested) && AreaPolicy.Equal(requested, OuterFrame(smallRequestedVisible, 9, 1, 9, 9)),
                "The 1024×600 display requests the original proportions using the current frame margins");
            // The window refuses to shrink below 380px and the move fits it against the right edge.
            var smallActualVisible = PlacementRect(644, 1451, 380, 567);
            var smallActual = OuterFrame(smallActualVisible, 7, 0, 7, 7);
            var clippedVisible = PlacementRect(smallRequestedVisible.Left, smallRequestedVisible.Top, 380, 567);
            Native.Rect fitted;
            Assert(TaskbarMover.TryFitMovedWindow(OuterFrame(clippedVisible, 7, 0, 7, 7), clippedVisible,
                small.Work, out fitted) && AreaPolicy.Equal(fitted, smallActual),
                "The small display shifts the actual minimum-width frame fully inside without requesting another resize");
            Assert(!TaskbarMover.TryFitMovedWindow(smallActual, smallActualVisible, small.Work, out fitted),
                "An already fitting visible frame does not move again for invisible resize borders");
            var smallLeg = TaskbarMover.ReturnPlacement.CreateRelative(secondLeg, PlacementEntry(main), PlacementEntry(small),
                mainOuter, mainVisible, smallActual, smallActualVisible, small);
            Assert(smallLeg != null && smallLeg.HasRelativeAnchor, "A minimum-size adjustment on the small display keeps the original relative layout");
            Assert(TaskbarMover.MapRelativeToWorkArea(smallActualVisible, small.Work, origin.Work).Left < originalVisible.Left,
                "The adjusted small window would expand on return if used as the next reference");
            Assert(TaskbarMover.ReturnPlacement.TryMapRelative(smallLeg, PlacementEntry(small), smallActual, smallActualVisible,
                origin, origin, out requested) && AreaPolicy.Equal(requested, original),
                "4→1→2→3→4 recovers exact initial bounds despite a minimum-size adjustment and a different frame");

            var moved = smallActual; moved.Left--; moved.Right--;
            Assert(!TaskbarMover.ReturnPlacement.TryMapRelative(smallLeg, PlacementEntry(small), moved,
                OuterFrame(moved, -7, 0, -7, -7), origin, origin, out requested), "An external move breaks the relative sequence");
            var resized = smallActual; resized.Left--;
            Assert(!TaskbarMover.ReturnPlacement.TryMapRelative(smallLeg, PlacementEntry(small), resized,
                OuterFrame(resized, -7, 0, -7, -7), origin, origin, out requested), "An external resize breaks the relative sequence");
            var changedFrame = smallActualVisible; changedFrame.Left++;
            Assert(TaskbarMover.ReturnPlacement.TryMapRelative(smallLeg, PlacementEntry(small), smallActual, changedFrame,
                origin, origin, out requested) && AreaPolicy.Equal(requested, original),
                "A DWM frame adjustment keeps the anchor when the outer bounds have not moved");
            var smallFallback = TaskbarMover.ReturnPlacement.CreateRelative(secondLeg, PlacementEntry(main), PlacementEntry(small),
                mainOuter, mainOuter, smallActual, smallActual, small);
            Assert(smallFallback != null && TaskbarMover.ReturnPlacement.TryMapRelative(smallFallback, PlacementEntry(small),
                smallActual, smallActualVisible, origin, origin, out requested) && AreaPolicy.Equal(requested, original),
                "A temporarily unavailable DWM frame on the small monitor does not make the minimum width the new anchor");

            var edgeFallback = TaskbarMover.ReturnPlacement.CreateRelative(null, PlacementEntry(origin), PlacementEntry(second),
                original, original, secondOuter, secondVisible, second);
            Assert(edgeFallback != null && TaskbarMover.ReturnPlacement.TryMapRelative(edgeFallback, PlacementEntry(second),
                secondOuter, secondOuter, origin, origin, out requested) && AreaPolicy.Equal(requested, original),
                "An outer resize border overhanging the source work area preserves the starting rectangle");
            var outsideOrigin = PlacementRect(origin.Work.Right, 20, 380, 1020);
            Assert(TaskbarMover.ReturnPlacement.CreateRelative(null, PlacementEntry(origin), PlacementEntry(second),
                outsideOrigin, outsideOrigin, secondOuter, secondVisible, second) == null,
                "A source rectangle entirely outside its recorded work area cannot anchor a move");

            var changed = PlacementEntry(small); changed.IsNormal = false;
            Assert(!TaskbarMover.ReturnPlacement.TryMapRelative(smallLeg, changed, smallActual, smallActualVisible,
                origin, origin, out requested), "A state change breaks the relative sequence");
            changed = PlacementEntry(small); changed.ArrangedBounds = smallActual;
            Assert(!TaskbarMover.ReturnPlacement.TryMapRelative(smallLeg, changed, smallActual, smallActualVisible,
                origin, origin, out requested), "A native arranged placement cannot inherit a floating relative layout");
            changed = PlacementEntry(small); changed.ProcessStarted++;
            Assert(!TaskbarMover.ReturnPlacement.TryMapRelative(smallLeg, changed, smallActual, smallActualVisible,
                origin, origin, out requested), "A reused window handle cannot inherit another process lifetime's layout");
            changed = PlacementEntry(small); changed.Monitor.Work.Bottom--;
            Assert(!TaskbarMover.ReturnPlacement.TryMapRelative(smallLeg, changed, smallActual, smallActualVisible,
                origin, origin, out requested), "A changed current work area breaks the relative sequence");
            var changedOrigin = origin; changedOrigin.Work.Bottom--;
            Assert(!TaskbarMover.ReturnPlacement.TryMapRelative(smallLeg, PlacementEntry(small), smallActual, smallActualVisible,
                second, changedOrigin, out requested), "A changed origin work area breaks the sequence even when heading elsewhere");
            TaskbarMover.ReturnPlacement.InvalidateManualMove(ref smallLeg, new IntPtr(43));
            Assert(smallLeg != null, "A manual move on another window preserves the active relative sequence");
            TaskbarMover.ReturnPlacement.InvalidateManualMove(ref smallLeg, PlacementEntry(small).Window);
            Assert(smallLeg == null, "A manual move on the same window clears the anchor even if its geometry returns to the recorded bounds");

            var tallerVisible = PlacementRect(184, 1510, 730, 729);
            var tallerOuter = OuterFrame(tallerVisible, 7, 0, 7, 7);
            Assert(TaskbarMover.TryFitMovedWindow(tallerOuter, tallerVisible, small.Work, out fitted) &&
                AreaPolicy.Equal(fitted, OuterFrame(PlacementRect(184, 1440, 730, 729), 7, 0, 7, 7)),
                "A minimum-height window larger than the work area keeps its full height and puts its title bar at the top");
            var negativeWork = PlacementRect(-1600, -600, 1024, 600);
            var offscreenOuter = PlacementRect(-1640, -300, 500, 460);
            Assert(TaskbarMover.TryFitMovedWindow(offscreenOuter, new Native.Rect(), negativeWork, out fitted) &&
                AreaPolicy.Equal(fitted, PlacementRect(-1600, -460, 500, 460)),
                "When a visible frame cannot be measured, fit the outer bounds against the destination work area");
        }

        static Native.Rect ReadPlacementFrame(IntPtr window, out Native.Rect outer)
        {
            Native.Rect visible;
            Assert(Native.GetWindowRect(window, out outer), "Owned native placement fixture exposes outer geometry");
            Assert(Native.DwmGetWindowAttribute(window, 9, out visible, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.Rect))) == 0,
                "Owned native placement fixture exposes DWM geometry");
            Assert(TaskbarMover.ValidVisibleFrame(outer, visible), "Owned native frame is valid and contained");
            return visible;
        }

        // Creates and moves only disposable Forms. Run only during coordinated native testing.
        internal static void WindowPlacementNativeChecks()
        {
            int start = count;
            var monitors = new List<IntPtr>();
            var infos = new List<Native.MonitorInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(handle, ref info)) { monitors.Add(handle); infos.Add(info); }
                return true;
            }, IntPtr.Zero);
            if (monitors.Count < 2) { Console.WriteLine("SKIP: native placement requires two monitors."); return; }
            int first = 0, second = 1;
            // Prefer the reported equal-resolution/different-taskbar-work-area case.
            for (int a = 0; a < infos.Count; a++) for (int b = a + 1; b < infos.Count; b++) {
                if (infos[a].Monitor.Right - infos[a].Monitor.Left == infos[b].Monitor.Right - infos[b].Monitor.Left &&
                    infos[a].Monitor.Bottom - infos[a].Monitor.Top == infos[b].Monitor.Bottom - infos[b].Monitor.Top &&
                    infos[a].Work.Bottom - infos[a].Work.Top != infos[b].Work.Bottom - infos[b].Work.Top) { first = a; second = b; }
            }
            foreach (var style in new[] { FormBorderStyle.Sizable, FormBorderStyle.None })
            using (var window = new Form { Text = "Neutral window placement fixture", ShowInTaskbar = false,
                FormBorderStyle = style, StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(infos[first].Work.Left + 80, infos[first].Work.Top + 80, 640, 480) })
            using (var mover = new TaskbarMover()) {
                FenceOwnedNativeMouseInput(mover);
                window.Show(); Application.DoEvents();
                foreach (bool relative in new[] { false, true }) foreach (bool reverse in new[] { false, true }) {
                    int sourceIndex = reverse ? second : first, targetIndex = reverse ? first : second;
                    var source = infos[sourceIndex].Work; var target = infos[targetIndex].Work;
                    window.Bounds = new Rectangle(source.Left + 80, source.Top + 80, 640, 480); Application.DoEvents();
                    Native.Rect outer;
                    var frame = ReadPlacementFrame(window.Handle, out outer);
                    window.Location = new System.Drawing.Point(outer.Left + source.Right - frame.Right, outer.Top + source.Bottom - frame.Bottom);
                    Application.DoEvents();
                    frame = ReadPlacementFrame(window.Handle, out outer);
                    Assert(frame.Right == source.Right && frame.Bottom == source.Bottom, "Disposable visible frame begins flush on right/bottom");
                    uint dpi = PlacementDpi(window.Handle);
                    var saved = ShelfPlacement(window.Handle);
                    mover.ScaleWindowSizeToMonitor = relative;
                    var expected = TaskbarMover.MapFramedWindow(outer, frame, source, target, relative);
                    Assert(mover.MoveToMonitor(window.Handle, monitors[targetIndex]), "Frame-aware native move succeeds: style=" + style +
                        ", relative=" + relative + ", reverse=" + reverse + ", status=" + mover.LastStatus);
                    Application.DoEvents();
                    Native.Rect movedOuter;
                    var movedFrame = ReadPlacementFrame(window.Handle, out movedOuter);
                    Assert(Native.MonitorFromWindow(window.Handle, 2) == monitors[targetIndex], "Owned fixture reaches target monitor");
                    bool sameDpi = dpi == PlacementDpi(window.Handle);
                    if (sameDpi) {
                        Assert(AreaPolicy.Equal(movedOuter, expected) && movedFrame.Right == target.Right && movedFrame.Bottom == target.Bottom,
                            "Actual same-DPI DWM frame is flush without invisible-border gaps in either sizing mode");
                        if (!relative) {
                            Assert(mover.MoveToMonitor(window.Handle, monitors[sourceIndex]), "Native keep-size return succeeds");
                            Application.DoEvents();
                            Native.Rect returned; Native.GetWindowRect(window.Handle, out returned);
                            Assert(AreaPolicy.Equal(returned, outer), "Native unclipped same-DPI edge roundtrip is exact");
                            Assert(mover.Undo(), "Undo consumes the return move"); Application.DoEvents();
                        }
                    } else Console.WriteLine("SKIP: exact native frame assertions across differing DPI; app-managed DPI resizing is outside this control.");
                    Assert(mover.Undo(), "Frame-aware native move retains Undo"); Application.DoEvents();
                    var undone = ShelfPlacement(window.Handle);
                    Assert(AreaPolicy.Equal(undone.NormalPosition, saved.NormalPosition) && undone.ShowCommand == saved.ShowCommand && !mover.CanUndo,
                        "Native Undo restores exact placement and consumes only owned moves");
                    foreach (bool maximized in new[] { true, false }) {
                        window.WindowState = maximized ? FormWindowState.Maximized : FormWindowState.Minimized;
                        Application.DoEvents();
                        var stateBefore = WindowHistory.Capture(window.Handle);
                        int showBefore = ShelfPlacement(window.Handle).ShowCommand;
                        if (maximized && WindowShelfCatalog.IsFullscreen(window.Handle)) {
                            Native.Rect fullscreenBefore; Native.GetWindowRect(window.Handle, out fullscreenBefore);
                            Assert(style == FormBorderStyle.None && !mover.AllowMovingFullscreenApps,
                                "Captionless maximized fixture follows the fullscreen classifier with the option OFF");
                            Assert(!mover.MoveToMonitor(window.Handle, monitors[targetIndex]) && !mover.CanUndo && mover.LastStatus.Contains("Enable Allow"),
                                "Fullscreen OFF rejects the captionless maximized fixture without recording a move");
                            Native.Rect unchanged; Native.GetWindowRect(window.Handle, out unchanged);
                            Assert(!stateBefore.FullscreenBounds.HasValue && AreaPolicy.Equal(unchanged, fullscreenBefore) &&
                                ShelfPlacement(window.Handle).ShowCommand == showBefore,
                                "Refused captionless fullscreen movement preserves actual bounds and maximized state");
                            window.WindowState = FormWindowState.Normal; Application.DoEvents();
                            continue; // Full-monitor ON/Undo behavior has its own fullscreen fixture.
                        }
                        int intermediateRestores = 0;
                        EventHandler resized = delegate { if (window.WindowState != FormWindowState.Maximized) intermediateRestores++; };
                        if (maximized) window.Resize += resized;
                        try { Assert(mover.MoveToMonitor(window.Handle, monitors[targetIndex]), "Native state move succeeds in both sizing modes: style=" + style + ", relative=" + relative + ", reverse=" + reverse + ", maximized=" + maximized + ", status=" + mover.LastStatus); }
                        finally { if (maximized) window.Resize -= resized; }
                        Application.DoEvents();
                        for (int settle = 0; !maximized && settle < 10 && Native.MonitorFromWindow(window.Handle, 2) != monitors[targetIndex]; settle++) {
                            Thread.Sleep(25); Application.DoEvents();
                        }
                        Assert(Native.MonitorFromWindow(window.Handle, 2) == monitors[targetIndex] &&
                            window.WindowState == (maximized ? FormWindowState.Maximized : FormWindowState.Normal) &&
                            (!maximized || intermediateRestores == 0), "Maximized moves stay maximized; minimized moves restore visibly: style=" + style +
                            ", relative=" + relative + ", reverse=" + reverse + ", maximized=" + maximized + ", actualState=" + window.WindowState +
                            ", actualMonitor=" + Native.MonitorFromWindow(window.Handle, 2) + ", targetMonitor=" + monitors[targetIndex] +
                            ", intermediateRestores=" + intermediateRestores + ", beforeDevice=" + stateBefore.Monitor.Device +
                            ", nativeNormalBefore=" + stateBefore.IsNormal + ", showBefore=" + showBefore +
                            ", normalBefore=" + PlacementGeometry(stateBefore.Placement.NormalPosition) +
                            ", normalAfter=" + PlacementGeometry(ShelfPlacement(window.Handle).NormalPosition) +
                            ", targetWork=" + PlacementGeometry(target));
                        Assert(mover.Undo(), "Native state move can be undone"); Application.DoEvents();
                        Assert(window.WindowState == (maximized ? FormWindowState.Maximized : FormWindowState.Normal),
                            "Undo preserves maximized state or visibly restores minimized state");
                        window.WindowState = FormWindowState.Normal; Application.DoEvents();
                        Assert(AreaPolicy.Equal(ShelfPlacement(window.Handle).NormalPosition, saved.NormalPosition) &&
                            Native.MonitorFromWindow(window.Handle, 2) == monitors[sourceIndex] && !mover.CanUndo,
                            "State Undo preserves original restore bounds and source monitor");
                    }
                }
            }
            ImmediateReturnNativeChecks(monitors[first], infos[first], monitors[second], infos[second]);
            Console.WriteLine("PASS: " + (count - start) + " native window-placement checks, including immediate-return and arranged Undo controls.");
        }

        static void ImmediateReturnNativeChecks(IntPtr sourceHandle, Native.MonitorInfo source, IntPtr targetHandle, Native.MonitorInfo target)
        {
            if (source.Work.Bottom - source.Work.Top < target.Work.Bottom - target.Work.Top) {
                var monitor = source; source = target; target = monitor;
                var handle = sourceHandle; sourceHandle = targetHandle; targetHandle = handle;
            }
            using (var window = new Form { Text = "Neutral immediate-return fixture", ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(source.Work.Left + 80, source.Work.Top + 80, 640, 480) })
            using (var mover = new TaskbarMover()) {
                FenceOwnedNativeMouseInput(mover);
                window.Show(); Application.DoEvents();
                Native.Rect outer;
                var visible = ReadPlacementFrame(window.Handle, out outer);
                var desiredVisible = PlacementRect(source.Work.Right - 640, source.Work.Top, 640, source.Work.Bottom - source.Work.Top);
                var desired = OuterFrame(desiredVisible, visible.Left - outer.Left, visible.Top - outer.Top,
                    outer.Right - visible.Right, outer.Bottom - visible.Bottom);
                window.Bounds = new Rectangle(desired.Left, desired.Top, desired.Right - desired.Left, desired.Bottom - desired.Top);
                Application.DoEvents();
                visible = ReadPlacementFrame(window.Handle, out outer);
                Assert(AreaPolicy.Equal(visible, desiredVisible), "Owned return fixture fills the source visible work height");
                uint dpi = PlacementDpi(window.Handle);
                var recordField = typeof(TaskbarMover).GetField("lastMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Native.Rect clipped = new Native.Rect();
                for (int iteration = 0; iteration < 6; iteration++) {
                    Assert(mover.MoveToMonitor(window.Handle, iteration % 2 == 0 ? targetHandle : sourceHandle), "Owned immediate-return move succeeds");
                    Application.DoEvents();
                    if (dpi != PlacementDpi(window.Handle)) {
                        Console.WriteLine("SKIP: exact clipped native return across differing DPI.");
                        return;
                    }
                    Native.Rect actual; Native.GetWindowRect(window.Handle, out actual);
                    if (iteration == 0) clipped = actual;
                    Assert(AreaPolicy.Equal(actual, iteration % 2 == 0 ? clipped : outer) && recordField.GetValue(mover) != null,
                        "Verified clipped native return alternates exact outer rectangles without drift");
                }
                Assert(mover.MoveToMonitor(window.Handle, targetHandle), "Owned resize-invalidation fixture moves out"); Application.DoEvents();
                window.Height -= 20; Application.DoEvents();
                Native.Rect resized; var resizedVisible = ReadPlacementFrame(window.Handle, out resized);
                var expected = TaskbarMover.MapFramedWindow(resized, resizedVisible, target.Work, source.Work, false, TaskbarMover.Arranged(window.Handle));
                Assert(mover.MoveToMonitor(window.Handle, sourceHandle), "Manual resize uses ordinary mapping on return"); Application.DoEvents();
                Native.Rect afterResize; Native.GetWindowRect(window.Handle, out afterResize);
                Assert(AreaPolicy.Equal(afterResize, expected) && !AreaPolicy.Equal(afterResize, outer), "Manual resize invalidates old unclipped return bounds");
                Assert(recordField.GetValue(mover) != null, "A verified replacement move owns the sole record");
                mover.ScaleWindowSizeToMonitor = true; mover.ScaleWindowSizeToMonitor = false;
                Assert(recordField.GetValue(mover) == null, "Switching sizing mode away and back clears the record immediately");
                Assert(mover.MoveToMonitor(window.Handle, targetHandle) && recordField.GetValue(mover) != null,
                    "Owned Undo-invalidation fixture records a fresh move");
                Assert(mover.Undo() && recordField.GetValue(mover) == null, "Undo clears immediate-return memory"); Application.DoEvents();

                // Exercise arranged Undo geometry with an owned snapshot whose floating
                // restore rectangle intentionally differs. Native drag/snap capture is a
                // separate coordinated integration control, not simulated global input here.
                foreach (var state in new[] { FormWindowState.Normal, FormWindowState.Maximized, FormWindowState.Minimized }) {
                    window.WindowState = FormWindowState.Normal;
                    window.Bounds = new Rectangle(source.Work.Left + 120, source.Work.Top + 120, 640, 480); Application.DoEvents();
                    var entry = WindowHistory.Capture(window.Handle);
                    Native.Rect actual; Native.GetWindowRect(window.Handle, out actual);
                    entry.ArrangedBounds = actual;
                    entry.Placement.NormalPosition = PlacementRect(source.Work.Left + 10, source.Work.Top + 10, 300, 200);
                    var history = new WindowHistory(); history.Remember(entry);
                    window.Location = new System.Drawing.Point(target.Work.Left + 80, target.Work.Top + 80);
                    window.WindowState = state; Application.DoEvents();
                    Assert(history.Undo(), "Arranged-bounds snapshot Undo succeeds from " + state); Application.DoEvents();
                    Native.Rect restored; Native.GetWindowRect(window.Handle, out restored);
                    Assert(AreaPolicy.Equal(restored, actual) && window.WindowState == FormWindowState.Normal && !history.Available,
                        "Arranged Undo restores actual bounds, never unrelated floating restore bounds, from " + state);
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        static extern uint PlacementDpi(IntPtr window);

        static string PlacementGeometry(Native.Rect rect)
        {
            return rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom;
        }

        static void TaskbarUiChecks()
        {
            var displays = new List<Display> {
                new Display { Device = "main", Identity = "main-id", Primary = true },
                new Display { Device = "side", Identity = "side-id" }
            };
            var settings = new Settings { HiddenIdentities = new List<string> { "side-id", "main-id" } };
            var primary = new Bar { Device = "main", Primary = true, Visible = true };
            var secondary = new Bar { Device = "side", Visible = true, Managed = false };
            var bars = new List<Bar> { primary, secondary };
            Assert(SettingsForm.TaskbarStatus(true, bars, displays, settings) == "Applying taskbar choices",
                "A desired hide still visible and unmanaged is pending, not reported hidden");
            Assert(SettingsForm.TaskbarStatus(false, bars, displays, settings) == "Paused",
                "Paused taskbar management never claims a desired hide succeeded");
            secondary.Visible = false;
            Assert(SettingsForm.TaskbarStatus(true, bars, displays, settings) == "Applying taskbar choices",
                "An unmanaged invisible bar is not claimed as hidden by this app");
            secondary.Managed = true;
            Assert(SettingsForm.TaskbarStatus(true, bars, displays, settings) == "1 taskbar hidden",
                "An observed app-managed hidden secondary is reported accurately");
            bars.Add(new Bar { Device = "side", Visible = true });
            Assert(SettingsForm.TaskbarStatus(true, bars, displays, settings) == "1 hidden | Applying",
                "A partial or failed hide retains the applying state");
            bars.RemoveAt(2); secondary.Visible = true; secondary.Managed = false;
            settings.HiddenIdentities.Clear();
            Assert(SettingsForm.TaskbarStatus(true, bars, displays, settings) == "0 taskbars hidden",
                "Visible bars without hide choices report zero hidden");
            Assert(!SettingsForm.SecondaryTaskbarsMissing(displays, bars), "Detected secondary taskbars clear the setup warning");
            bars.RemoveAt(1);
            Assert(SettingsForm.SecondaryTaskbarsMissing(displays, bars), "Multiple displays without a secondary bar show setup action");
            bars.Add(secondary);
            Assert(!SettingsForm.SecondaryTaskbarsMissing(displays, bars), "Secondary-bar recovery clears the setup warning");
            displays.RemoveAt(1); bars.RemoveAt(1);
            Assert(!SettingsForm.SecondaryTaskbarsMissing(displays, bars), "One display does not show a secondary-taskbar warning");

            using (var host = new Panel { Size = new System.Drawing.Size(470, 40), Font = new System.Drawing.Font("Segoe UI", 10) })
            using (var toggle = new ToggleSwitch("Start with Windows"))
            using (var info = new Button { Size = new System.Drawing.Size(24, 24), Margin = new Padding(2, 8, 0, 0) }) {
                var row = SettingsForm.ToggleRow(toggle, info);
                host.Controls.Add(row); host.CreateControl(); host.PerformLayout(); row.PerformLayout();
                int shortWidth = toggle.Width;
                toggle.Text = "Start with Windows (blocked by Windows)"; row.PerformLayout();
                var bounds = toggle.TextBounds;
                var measured = TextRenderer.MeasureText(toggle.Text, toggle.Font,
                    new System.Drawing.Size(bounds.Width, Int32.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
                Assert(toggle.Width > shortWidth && measured.Height <= bounds.Height && measured.Width <= bounds.Width,
                    "Blocked startup label fits on one line in its painted text area");
                Assert(info.Left >= toggle.Right && info.Right <= row.ClientSize.Width,
                    "Growing startup label keeps its inline info marker beside it");
                toggle.Text = "Startup status unavailable"; row.PerformLayout();
                Assert(info.Left >= toggle.Right && toggle.Width < row.ClientSize.Width,
                    "Unavailable startup label is resized without overlapping the marker");
                toggle.Text = "Start with Windows"; row.PerformLayout();
                Assert(toggle.Width == shortWidth, "Startup label shrinks when Windows-blocked state clears");
            }
            using (var host = new Panel { Size = new System.Drawing.Size(660, 82), Font = new System.Drawing.Font("Segoe UI", 10) }) {
                var setup = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false };
                setup.Controls.Add(new Label { Text = "No secondary taskbars detected", AutoSize = true, Margin = new Padding(0, 10, 8, 0) });
                var taskbarSettings = Ui.Button("Taskbar settings", null, delegate { });
                setup.Controls.Add(taskbarSettings); host.Controls.Add(setup);
                host.CreateControl(); setup.PerformLayout();
                Assert(taskbarSettings.Right <= setup.ClientSize.Width && taskbarSettings.Bottom <= setup.ClientSize.Height,
                    "Secondary-taskbar setup action fits the minimum-width feedback row");

                var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 42, ColumnCount = 2 };
                footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var state = Ui.Label(""); footer.Controls.Add(state, 0, 0);
                var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Fill };
                actions.Controls.Add(Ui.Button("Undo", "undo-2", delegate { }));
                actions.Controls.Add(Ui.Button("Pause moves", "pause", delegate { }));
                actions.Controls.Add(Ui.Button("Pause all", null, delegate { }));
                actions.Controls.Add(new Button { Size = new System.Drawing.Size(24, 24), Margin = new Padding(2, 8, 0, 0) });
                footer.Controls.Add(actions, 1, 0); host.Controls.Add(footer);
                foreach (string text in new[] { "Applying taskbar choices | Moves paused", "1 hidden | Applying | Moves paused", "1 taskbar hidden | Moves paused" }) {
                    state.Text = text; footer.PerformLayout(); actions.PerformLayout();
                    int width = TextRenderer.MeasureText(text, host.Font, new System.Drawing.Size(Int32.MaxValue, 42),
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                    Assert(width <= state.ClientSize.Width && actions.Right <= footer.ClientSize.Width,
                        "Taskbar status and all four footer controls fit at minimum form width: " + text);
                }
                int previousDpi = 96;
                foreach (int dpi in new[] { 144, 192 }) {
                    setup.Scale(new System.Drawing.SizeF((float)dpi / previousDpi, (float)dpi / previousDpi));
                    host.Width = (int)Math.Ceiling(660 * dpi / 96f);
                    setup.Height = SettingsForm.SetupRowHeight(setup, dpi);
                    setup.PerformLayout();
                    Assert(taskbarSettings.Bottom <= setup.ClientSize.Height && taskbarSettings.Right <= setup.ClientSize.Width,
                        "Secondary-taskbar action fits its scaled row at " + dpi + " DPI");
                    previousDpi = dpi;
                }
            }
            using (var host = new Panel { Size = new System.Drawing.Size(640, 48), Font = new System.Drawing.Font("Segoe UI", 10) })
            using (var help = new ToolTip()) {
                var selector = new ThemedComboBox();
                selector.Items.AddRange(new object[] { "Keep window size", "Keep relative size and position" });
                selector.SelectedIndex = 1;
                var info = new Button { Size = new System.Drawing.Size(24, 24), Margin = new Padding(2, 8, 0, 0) };
                help.SetToolTip(info, SettingsForm.WindowSizingHelp);
                var row = SettingsForm.WindowSizingRow(selector, info);
                host.Controls.Add(row); host.CreateControl(); host.PerformLayout(); row.PerformLayout();
                Assert(selector.DropDownStyle == ComboBoxStyle.DropDownList && selector.Items.Count == 2 &&
                    selector.AccessibleName == "Window sizing", "Window sizing is a named noneditable two-choice selector");
                var frame = selector.Parent;
                Assert(frame.Left >= 144 && frame.Right <= row.ClientSize.Width && info.Right <= frame.Left,
                    "Window sizing selector and inline help fit at the minimum form width");
                Assert(help.GetToolTip(info).Contains("usable work areas") && help.GetToolTip(info).Contains("minimum size or DPI"),
                    "Window sizing inline help explains work areas and app overrides");
                foreach (int choice in new[] { 0, 1 }) {
                    selector.SelectedIndex = choice;
                    Assert(TextRenderer.MeasureText(selector.Text, selector.Font).Width <= selector.ClientSize.Width - 28 &&
                        info.Right <= frame.Left && frame.Right <= row.ClientSize.Width,
                        "Sizing choice " + choice + " fits without overlap at minimum form width");
                }
                int previousDpi = 96;
                foreach (int dpi in new[] { 144, 192 }) {
                    row.Scale(new System.Drawing.SizeF((float)dpi / previousDpi, (float)dpi / previousDpi));
                    host.Size = new System.Drawing.Size((int)Math.Ceiling(640 * dpi / 96f), (int)Math.Ceiling(48 * dpi / 96f));
                    host.PerformLayout(); row.PerformLayout();
                    foreach (int choice in new[] { 0, 1 }) {
                        selector.SelectedIndex = choice;
                        Assert(info.Right <= frame.Left && frame.Right <= row.ClientSize.Width &&
                            TextRenderer.MeasureText(selector.Text, selector.Font).Width <= selector.ClientSize.Width - 28,
                            "Sizing choice " + choice + " and tooltip marker fit at " + dpi + " DPI");
                        Assert(frame.ClientSize.Height - frame.Padding.Vertical >= selector.PreferredSize.Height,
                            "Sizing selector keeps vertical room at simulated " + dpi + " DPI");
                    }
                    previousDpi = dpi;
                }
            }
        }
        static void WindowShelfSettingsChecks()
        {
            var settings = new Settings { MonitorMoveShortcuts = new int[4], SearchAppsShortcut = 0, FocusWindowShortcuts = new int[4], TileWindowShortcut = 0 }; // Isolate Ghost Bar opt-in from all keyboard actions.
            var display = new Display { Device = "side", Identity = "side-id" };
            var displays = new List<Display> { display };
            Assert(settings.WindowShelfIdentities.Count == 0 && !settings.HasWindowMovement, "Window shelf defaults off");
            settings.SearchAppsShortcut = (int)(Keys.Control | Keys.Shift | Keys.Space);
            Assert(settings.HasWindowMovement && settings.WindowShelfIdentities.Count == 0,
                "Keyboard Search enables shared sizing and Undo controls without Ghost Bar opt-in");
            settings.SearchAppsShortcut = 0;
            Assert(!settings.HasWindowMovement, "Close-only keyboard actions do not enable shared movement controls");
            Assert(!Policy.Shelf(display, displays, settings, true, false), "Unconfigured displays never expose a shelf");
            settings.WindowShelfIdentities.Add(display.Identity);
            Assert(settings.HasWindowMovement, "Explicit shelf opt-in enables shared movement controls independently of taskbar clicks");
            Assert(!Policy.Shelf(display, displays, settings, true, false), "Visible-taskbar displays never expose a shelf");
            settings.HiddenIdentities.Add(display.Identity);
            Assert(Policy.Shelf(display, displays, settings, true, false), "Selected hidden secondary supports a shelf");
            Assert(!Policy.Shelf(display, displays, settings, false, false), "Pause all suppresses shelves");
            Assert(!Policy.Shelf(display, displays, settings, true, true), "Pause movement suppresses shelves");
            display.Primary = true;
            Assert(!Policy.Shelf(display, displays, settings, true, false), "Primary display never exposes a shelf");
            display.Primary = false;
            displays.Add(new Display { Device = "duplicate", Identity = "side-id" });
            Assert(!Policy.Shelf(display, displays, settings, true, false), "Ambiguous monitor identities suppress shelves");
            displays.RemoveAt(1);
            display.Identity = "";
            Assert(!Policy.Shelf(display, displays, settings, true, false), "Unknown monitor identities suppress shelves");
            Assert(!Policy.Shelf(null, displays, settings, true, false), "Removed displays cannot expose shelves");
            var old = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Settings>("{\"HiddenIdentities\":[\"side-id\"]}");
            Assert(old.WindowShelfIdentities.Count == 0, "Older settings do not opt into the new feature");
            var copy = settings.Copy();
            Assert(copy.WindowShelfIdentities.Count == 1 && copy.WindowShelfIdentities[0] == "side-id", "Shelf opt-in survives serialization");
            copy.WindowShelfIdentities.Clear();
            Assert(settings.WindowShelfIdentities.Count == 1, "Shelf selections are independent in settings snapshots");
            GhostBarSensitivityChecks();
        }
        static void GhostBarSensitivityPersistenceChecks()
        {
            var defaults = Settings.CreateFresh();
            Assert(defaults.WindowShelfActivationWidth == 480 && defaults.WindowShelfActivationHeight == 6 && defaults.WindowShelfHoverDelayMs == 100,
                "New Ghost Bar profiles use a 480px by 6px activation area and 100ms hover delay");
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect-sensitivity-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(directory);
            try {
                Assert(Program.Load(path).WindowShelfActivationWidth == 480 && Program.Load(path).WindowShelfActivationHeight == 6 && Program.Load(path).WindowShelfHoverDelayMs == 100,
                    "Missing settings use the current Ghost Bar activation defaults");
                File.WriteAllText(path, "{\"HiddenIdentities\":[\"side-id\"],\"WindowShelfIdentities\":[\"side-id\"],\"MoveWindowsToClickedTaskbar\":true,\"ScaleWindowSizeToMonitor\":true,\"MonitorAliases\":{\"side-id\":\"Side screen\"}}");
                var migrated = Program.Load(path);
                Assert(migrated.WindowShelfActivationWidth == 480 && migrated.WindowShelfActivationHeight == 24 && migrated.WindowShelfHoverDelayMs == 100 &&
                    migrated.HiddenIdentities.Contains("side-id") && migrated.WindowShelfIdentities.Contains("side-id") &&
                    migrated.MoveWindowsToClickedTaskbar && migrated.ScaleWindowSizeToMonitor && migrated.MonitorAliases["side-id"] == "Side screen",
                    "Legacy settings retain taskbar and movement choices while adding Ghost Bar defaults");
                foreach (int savedDelay in new[] { 100, 400, 850 }) {
                    File.WriteAllText(path, "{\"HiddenIdentities\":[\"side-id\"],\"WindowShelfIdentities\":[\"side-id\"],\"UndoMoveShortcut\":0,\"WindowShelfHoverDelayMs\":" + savedDelay + "}");
                    var saved = Program.Load(path);
                    Assert(saved.WindowShelfActivationWidth == 480 && saved.WindowShelfActivationHeight == 24 && saved.WindowShelfHoverDelayMs == savedDelay &&
                        saved.HiddenIdentities.Contains("side-id") && saved.WindowShelfIdentities.Contains("side-id") && saved.UndoMoveShortcut == 0,
                        "Migration preserves an explicit saved hover delay and unrelated preferences: " + savedDelay);
                    var savedCopy = saved.Copy();
                    Program.Save(savedCopy, path);
                    var savedAgain = Program.Load(path);
                    Assert(savedAgain.WindowShelfActivationWidth == 480 && savedAgain.WindowShelfActivationHeight == 24 && savedAgain.WindowShelfHoverDelayMs == savedDelay &&
                        savedAgain.HiddenIdentities.Contains("side-id") && savedAgain.WindowShelfIdentities.Contains("side-id") && savedAgain.UndoMoveShortcut == 0,
                        "Explicit saved hover delay survives copy, save and reload instead of resetting to the new default: " + savedDelay);
                }
                File.WriteAllText(path, "{\"HiddenIdentities\":[],\"WindowShelfActivationWidth\":240}");
                var missingDelay = Program.Load(path);
                Assert(missingDelay.WindowShelfActivationWidth == 240 && missingDelay.WindowShelfActivationHeight == 24 && missingDelay.WindowShelfHoverDelayMs == 100,
                    "An existing file with missing height retains the released 24-pixel fallback without resetting explicit saved width");
                migrated.WindowShelfActivationWidth = 240; migrated.WindowShelfActivationHeight = 72; migrated.WindowShelfHoverDelayMs = 850;
                Program.Save(migrated, path);
                var loaded = Program.Load(path);
                Assert(loaded.WindowShelfActivationWidth == 240 && loaded.WindowShelfActivationHeight == 72 && loaded.WindowShelfHoverDelayMs == 850 &&
                    loaded.HiddenIdentities.Contains("side-id") && loaded.WindowShelfIdentities.Contains("side-id") && loaded.ScaleWindowSizeToMonitor,
                    "Immediate-save persistence retains custom sensitivity and existing preferences");
                var history = new SettingsHistory(); history.Push(loaded, null, "Ghost Bar sensitivity");
                var snapshot = loaded.Copy(); snapshot.WindowShelfActivationWidth = 320; snapshot.WindowShelfActivationHeight = 48; snapshot.WindowShelfHoverDelayMs = 600;
                loaded.WindowShelfActivationWidth = 960; loaded.WindowShelfActivationHeight = 96; loaded.WindowShelfHoverDelayMs = 1500;
                Assert(history.Peek.Settings.WindowShelfActivationWidth == 240 && history.Peek.Settings.WindowShelfActivationHeight == 72 && history.Peek.Settings.WindowShelfHoverDelayMs == 850 &&
                    snapshot.WindowShelfActivationWidth == 320 && snapshot.WindowShelfActivationHeight == 48 && snapshot.WindowShelfHoverDelayMs == 600,
                    "Sensitivity values remain independent in settings copies and history snapshots");
                foreach (int value in new[] { Int32.MinValue, -1, 0, 79, 80, 480, 960, 961, Int32.MaxValue }) {
                    loaded.WindowShelfActivationWidth = value;
                    Program.Save(loaded, path);
                    Assert(Program.Load(path).WindowShelfActivationWidth == Math.Max(80, Math.Min(960, value)),
                        "Activation width remains bounded after save/load: " + value);
                }
                foreach (int value in new[] { Int32.MinValue, -1, 0, 99, 100, 400, 1500, 1501, Int32.MaxValue }) {
                    loaded.WindowShelfHoverDelayMs = value;
                    Program.Save(loaded, path);
                    Assert(Program.Load(path).WindowShelfHoverDelayMs == Math.Max(100, Math.Min(1500, value)),
                        "Hover delay remains bounded after save/load: " + value);
                }
                foreach (int value in new[] { Int32.MinValue, -1, 0, 1, 2, 3, 4, 5, 6, 24, 48, 96, 97, Int32.MaxValue }) {
                    loaded.WindowShelfActivationHeight = value;
                    Program.Save(loaded, path);
                    var retained = Program.Load(path);
                    Assert(retained.WindowShelfActivationHeight == Math.Max(1, Math.Min(96, value)) &&
                        retained.WindowShelfActivationWidth == 960 && retained.WindowShelfHoverDelayMs == 1500 &&
                        retained.HiddenIdentities.Contains("side-id") && retained.WindowShelfIdentities.Contains("side-id") &&
                        retained.MonitorAliases["side-id"] == "Side screen" && retained.ScaleWindowSizeToMonitor,
                        "Activation height remains bounded after save/load without changing existing width, delay or monitor choices: " + value);
                }
                File.WriteAllText(path, "{\"HiddenIdentities\":[],\"WindowShelfActivationWidth\":-2147483648,\"WindowShelfActivationHeight\":-2147483648,\"WindowShelfHoverDelayMs\":2147483647}");
                var bounded = Program.Load(path);
                Assert(bounded.WindowShelfActivationWidth == 80 && bounded.WindowShelfActivationHeight == 1 && bounded.WindowShelfHoverDelayMs == 1500,
                    "Out-of-range persisted values are safely bounded when loaded");
                File.WriteAllText(path, "{\"HiddenIdentities\":[],\"WindowShelfActivationHeight\":2147483647}");
                var tooTall = Program.Load(path);
                Assert(tooTall.WindowShelfActivationHeight == 96 && tooTall.WindowShelfActivationWidth == 480 && tooTall.WindowShelfHoverDelayMs == 100,
                    "An oversized persisted height is bounded without requiring other new sensitivity fields");
            } finally {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                Directory.Delete(directory);
            }
        }
        // Focused safe entry: only settings objects and a unique temporary directory.
        // No controls, HWNDs, message pump, native hooks or application entry point.
        internal static int RunGhostBarSensitivityPersistenceChecks()
        {
            int start = count;
            GhostBarSensitivityPersistenceChecks();
            Console.WriteLine("PASS: " + (count - start) + " pure Ghost Bar sensitivity persistence checks.");
            return count - start;
        }
        static void GhostBarSensitivityChecks()
        {
            GhostBarSensitivityPersistenceChecks();
            foreach (string dimension in new[] { "width", "height", "delay" }) {
                bool width = dimension == "width", height = dimension == "height";
                string name = width ? "Activation width" : height ? "Activation height" : "Hover delay";
                string description = width ? SettingsForm.GhostBarWidthHelp : height ? SettingsForm.GhostBarHeightHelp : SettingsForm.GhostBarDelayHelp;
                using (var host = new Panel { Size = new Size(600, 42), Font = new Font("Segoe UI", 10) })
                using (var input = new NumericUpDown())
                using (var help = new ToolTip())
                using (var info = SettingsForm.CreateInfoMarker(name, description, help)) {
                    var row = height ? SettingsForm.GhostBarActivationHeightRow(input, info) : SettingsForm.GhostBarSensitivityRow(input, info, width);
                    host.Controls.Add(row); host.CreateControl(); host.PerformLayout(); row.PerformLayout();
                    Assert(input.AccessibleName == (width ? "Ghost Bar activation width" : height ? "Ghost Bar activation height" : "Ghost Bar hover delay") &&
                        input.Minimum == (width ? 80 : height ? 1 : 100) && input.Maximum == (width ? 960 : height ? 96 : 1500) && input.Value == (width ? 480 : height ? 6 : 100),
                        "Sensitivity numeric control exposes its label, safe bounds and current default");
                    Assert(help.GetToolTip(info) == description && (!height || input.Increment == 1),
                        "Sensitivity information marker describes the corresponding dimension and height has a usable step");
                    var frame = input.Parent;
                    Assert(info.Right <= frame.Left && frame.Right <= row.ClientSize.Width && frame.Bottom <= row.ClientSize.Height,
                        "Sensitivity input and information marker fit the native control row");
                    foreach (int dpi in new[] { 96, 144, 192, 288 }) {
                        // Control.Scale does not change DeviceDpi. Check the same production
                        // field cap with explicit DPI and scaled text, instead of mixing a
                        // scaled font with the host desktop's unscaled field cap.
                        int cap = SettingsForm.BoundedFieldWidth((int)Math.Ceiling(400 * dpi / 96f), 140, dpi);
                        Assert(cap == (int)Math.Ceiling(140 * dpi / 96f), "Sensitivity field cap scales at " + dpi + " DPI");
                        using (var font = new Font(input.Font.FontFamily, input.Font.Size * dpi / 96f)) {
                            foreach (decimal value in new[] { input.Minimum, input.Maximum }) {
                                Assert(TextRenderer.MeasureText(value.ToString("0"), font).Width + (int)Math.Ceiling(40 * dpi / 96f) <= cap,
                                    "Sensitivity boundary value fits its numeric field including padding and spinner at " + dpi + " DPI");
                            }
                        }
                    }
                }
            }
            for (int flags = 0; flags < 128; flags++) {
                bool active = (flags & 1) != 0, visible = (flags & 2) != 0, formFocus = (flags & 4) != 0;
                bool widthVisible = (flags & 8) != 0, widthFocus = (flags & 16) != 0;
                bool heightVisible = (flags & 32) != 0, heightFocus = (flags & 64) != 0;
                Assert(SettingsForm.ActivationPreviewRequested(active, visible, formFocus, widthVisible, widthFocus, heightVisible, heightFocus) ==
                    (active && visible && formFocus && (widthVisible && widthFocus || heightVisible && heightFocus)),
                    "Either focused visible activation dimension requests preview; hidden, inactive and unrelated fields suppress it");
            }
        }
        internal sealed class ShelfFocusSafety
        {
            internal long Generation, Started;
            internal IntPtr Window;
            internal uint Process;
            internal bool Accepted, OwnMedium, BoundsUnchanged, PlacementUnchanged, StateUnchanged,
                MonitorUnchanged, EmptyUndo, ForegroundUnchanged, PointerUnchanged, InputUnchanged, NoWorkers;
            internal string Status;
        }
        internal static bool MeasuredLocalFocusUnavailable(WindowMoveWorker.FocusTrace trace, ShelfFocusSafety safety)
        {
            if (trace == null || trace.Result == null || safety == null) return false;
            var result = trace.Result;
            return !safety.Accepted && safety.Status == "The selected window could not be focused" &&
                safety.Window != IntPtr.Zero && safety.Process != 0 && safety.Started > 0 && safety.Generation > 0 &&
                trace.Generation == safety.Generation && trace.Window == safety.Window && trace.Process == safety.Process &&
                trace.Started == safety.Started && safety.OwnMedium &&
                result.FocusGrantAttempted && !result.FocusGrantSucceeded && (result.FocusGrantError == 0 || result.FocusGrantError == 5) &&
                result.WorkerProcess != 0 && result.WorkerProcess != safety.Process && result.WorkerStarted > 0 &&
                result.Outcome == WindowMoveWorker.Outcome.Failed && result.MutationAttempted && result.CallerExited &&
                result.WorkerReply == 2 && result.WorkerExitKnown && result.WorkerExitCode == 0 &&
                safety.BoundsUnchanged && safety.PlacementUnchanged && safety.StateUnchanged && safety.MonitorUnchanged &&
                safety.EmptyUndo && safety.ForegroundUnchanged && safety.PointerUnchanged && safety.InputUnchanged && safety.NoWorkers;
        }
        static ShelfFocusSafety ValidShelfFocusSafety()
        {
            return new ShelfFocusSafety { Generation = 2, Window = new IntPtr(123), Process = 456, Started = 789,
                OwnMedium = true, BoundsUnchanged = true, PlacementUnchanged = true, StateUnchanged = true,
                MonitorUnchanged = true, EmptyUndo = true, ForegroundUnchanged = true, PointerUnchanged = true,
                InputUnchanged = true, NoWorkers = true, Status = "The selected window could not be focused" };
        }
        static WindowMoveWorker.FocusTrace ValidShelfFocusTrace()
        {
            return new WindowMoveWorker.FocusTrace { Generation = 2, Window = new IntPtr(123), Process = 456, Started = 789,
                Result = new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Failed, MutationAttempted = true,
                    CallerExited = true, FocusGrantAttempted = true, FocusGrantError = 5, WorkerProcess = 999, WorkerStarted = 987,
                    WorkerReply = 2, WorkerExitKnown = true, WorkerExitCode = 0 } };
        }
        internal static void ShelfFocusClassificationChecks()
        {
            Assert(MeasuredLocalFocusUnavailable(ValidShelfFocusTrace(), ValidShelfFocusSafety()),
                "Measured denied foreground grant with complete unchanged owned MED state permits only focus-success acceptance to be skipped");
            var noError = ValidShelfFocusTrace(); noError.Result.FocusGrantError = 0;
            Assert(MeasuredLocalFocusUnavailable(noError, ValidShelfFocusSafety()), "A measured false grant with no extended error still requires every native and safety proof");
            Assert(!MeasuredLocalFocusUnavailable(null, ValidShelfFocusSafety()) &&
                !MeasuredLocalFocusUnavailable(ValidShelfFocusTrace(), null), "Absent actual focus trace or safety proof cannot become a permission skip");
            foreach (Action<WindowMoveWorker.FocusTrace> change in new Action<WindowMoveWorker.FocusTrace>[] {
                x => x.Generation--, x => x.Window = IntPtr.Zero, x => x.Process++, x => x.Started++,
                x => x.Result = null, x => x.Result.FocusGrantAttempted = false, x => x.Result.FocusGrantSucceeded = true,
                x => x.Result.FocusGrantError = 87, x => x.Result.WorkerProcess = 0, x => x.Result.WorkerProcess = 456,
                x => x.Result.WorkerStarted = 0, x => x.Result.Outcome = WindowMoveWorker.Outcome.Success,
                x => x.Result.Outcome = WindowMoveWorker.Outcome.Rejected, x => x.Result.Outcome = WindowMoveWorker.Outcome.TimedOut,
                x => x.Result.Outcome = WindowMoveWorker.Outcome.Cancelled, x => x.Result.MutationAttempted = false,
                x => x.Result.CallerExited = false, x => x.Result.WorkerReply = 0, x => x.Result.WorkerReply = 1,
                x => x.Result.WorkerReply = 3, x => x.Result.WorkerExitKnown = false, x => x.Result.WorkerExitCode = 3 }) {
                var trace = ValidShelfFocusTrace(); change(trace);
                Assert(!MeasuredLocalFocusUnavailable(trace, ValidShelfFocusSafety()),
                    "Stale identity, unmeasured/granted focus, invalid grant error, pre-call rejection, timeout, cancellation or unknown worker cannot be skipped");
            }
            foreach (Action<ShelfFocusSafety> change in new Action<ShelfFocusSafety>[] {
                x => x.Generation = 0, x => x.Window = IntPtr.Zero, x => x.Process = 0, x => x.Started = 0,
                x => x.Accepted = true, x => x.OwnMedium = false, x => x.BoundsUnchanged = false,
                x => x.PlacementUnchanged = false, x => x.StateUnchanged = false, x => x.MonitorUnchanged = false,
                x => x.EmptyUndo = false, x => x.ForegroundUnchanged = false, x => x.PointerUnchanged = false,
                x => x.InputUnchanged = false, x => x.NoWorkers = false, x => x.Status = "Window move cancelled" }) {
                var safety = ValidShelfFocusSafety(); change(safety);
                Assert(!MeasuredLocalFocusUnavailable(ValidShelfFocusTrace(), safety),
                    "Unavailable or changed owned-state, foreground, pointer, input, history, worker cleanup or exact refusal cannot be skipped");
            }
        }
        static bool CheckNativeShelfFocus(Form window, TaskbarMover mover, ShelfWindow selected, Display target, IntPtr clickForeground, string context)
        {
            var placementBefore = ShelfPlacement(window.Handle);
            FormWindowState stateBefore = window.WindowState;
            IntPtr monitorBefore = Native.MonitorFromWindow(window.Handle, 2);
            Native.Rect boundsBefore, boundsAfter;
            bool undoBefore = mover.CanUndo;
            Assert(Native.GetWindowRect(window.Handle, out boundsBefore) && stateBefore != FormWindowState.Minimized &&
                monitorBefore != IntPtr.Zero && !undoBefore, "Native local focus starts from owned visible state and empty Undo: " + context);
            uint own = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var identityBefore = NativeProcessIdentity.Read(own);
            uint targetBefore; Native.GetWindowThreadProcessId(window.Handle, out targetBefore);
            IntPtr foregroundBefore = GetForegroundWindow(); uint foregroundPidBefore;
            Native.GetWindowThreadProcessId(foregroundBefore, out foregroundPidBefore);
            var foregroundIdentity = NativeProcessIdentity.Read(foregroundPidBefore);
            TaskbarMover.Point pointerBefore; bool pointerRead = ShelfFocusCursor(out pointerBefore);
            var inputBefore = new ShelfFocusInput { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(ShelfFocusInput)) };
            bool inputRead = ShelfFocusLastInput(ref inputBefore);
            int activeBefore = WindowMoveWorker.ActiveHelpers, uncertainBefore = WindowMoveWorker.UncertainHelpers;
            long generation = WindowMoveWorker.ClearLastFocusTrace() + 1;
            mover.LastStatus = "Native local focus check has not reported an outcome";
            bool accepted = ShelfClick(mover, selected, target, clickForeground);
            var trace = WindowMoveWorker.LastFocusTrace;
            var placementAfter = ShelfPlacement(window.Handle);
            bool boundsRead = Native.GetWindowRect(window.Handle, out boundsAfter);
            IntPtr foregroundAfter = GetForegroundWindow(); uint foregroundPidAfter;
            Native.GetWindowThreadProcessId(foregroundAfter, out foregroundPidAfter);
            TaskbarMover.Point pointerAfter; bool pointerAfterRead = ShelfFocusCursor(out pointerAfter);
            var inputAfter = new ShelfFocusInput { Size = inputBefore.Size }; bool inputAfterRead = ShelfFocusLastInput(ref inputAfter);
            var identityAfter = NativeProcessIdentity.Read(own);
            uint targetAfter; Native.GetWindowThreadProcessId(window.Handle, out targetAfter);
            var safety = new ShelfFocusSafety {
                Generation = generation, Window = window.Handle, Process = selected.Process, Started = selected.ProcessStarted,
                Accepted = accepted, Status = mover.LastStatus,
                OwnMedium = selected.Process == own && targetBefore == own && targetAfter == own &&
                    identityBefore.Process == own && identityAfter.Process == own && identityBefore.Started == selected.ProcessStarted &&
                    identityAfter.Started == selected.ProcessStarted && identityBefore.IntegrityAvailable && identityAfter.IntegrityAvailable &&
                    identityBefore.IntegrityLevel == NativeProcessIdentity.Medium && identityAfter.IntegrityLevel == NativeProcessIdentity.Medium &&
                    identityBefore.ElevationAvailable && identityAfter.ElevationAvailable && !identityBefore.Elevated && !identityAfter.Elevated,
                BoundsUnchanged = boundsRead && AreaPolicy.Equal(boundsBefore, boundsAfter),
                PlacementUnchanged = placementAfter.ShowCommand == placementBefore.ShowCommand && placementAfter.Flags == placementBefore.Flags &&
                    AreaPolicy.Equal(placementAfter.NormalPosition, placementBefore.NormalPosition) &&
                    placementAfter.MinPosition.X == placementBefore.MinPosition.X && placementAfter.MinPosition.Y == placementBefore.MinPosition.Y &&
                    placementAfter.MaxPosition.X == placementBefore.MaxPosition.X && placementAfter.MaxPosition.Y == placementBefore.MaxPosition.Y,
                StateUnchanged = window.WindowState == stateBefore,
                MonitorUnchanged = Native.MonitorFromWindow(window.Handle, 2) == monitorBefore,
                EmptyUndo = !undoBefore && !mover.CanUndo,
                ForegroundUnchanged = foregroundBefore == foregroundAfter && foregroundPidBefore == foregroundPidAfter,
                PointerUnchanged = pointerRead && pointerAfterRead && pointerBefore.X == pointerAfter.X && pointerBefore.Y == pointerAfter.Y,
                InputUnchanged = inputRead && inputAfterRead && inputBefore.Time == inputAfter.Time,
                NoWorkers = activeBefore == 0 && uncertainBefore == 0 && WindowMoveWorker.ActiveHelpers == 0 && WindowMoveWorker.UncertainHelpers == 0 };
            string evidence = " [trace=" + (trace == null ? "none" : trace.Generation.ToString()) +
                " expected=" + generation + " ownMED=" + safety.OwnMedium + " foregroundPid=" + foregroundPidBefore +
                " foregroundIntegrity=" + (foregroundIdentity.IntegrityAvailable ? foregroundIdentity.IntegrityLevel.ToString() : "unavailable") +
                " accepted=" + accepted + " status=" + mover.LastStatus;
            if (trace != null && trace.Result != null) {
                var result = trace.Result;
                evidence += " outcome=" + result.Outcome + " grantAttempted=" + result.FocusGrantAttempted +
                    " grant=" + result.FocusGrantSucceeded + " grantError=" + result.FocusGrantError +
                    " workerPid=" + result.WorkerProcess + " workerStarted=" + result.WorkerStarted +
                    " reply=" + result.WorkerReply + " exitKnown=" + result.WorkerExitKnown + " exit=" + result.WorkerExitCode +
                    " callerExited=" + result.CallerExited + " attempted=" + result.MutationAttempted;
            }
            evidence += " bounds=" + safety.BoundsUnchanged + " placement=" + safety.PlacementUnchanged +
                " state=" + safety.StateUnchanged + " monitor=" + safety.MonitorUnchanged + " emptyUndo=" + safety.EmptyUndo +
                " foregroundUnchanged=" + safety.ForegroundUnchanged + " pointerUnchanged=" + safety.PointerUnchanged +
                " inputUnchanged=" + safety.InputUnchanged + " noWorkers=" + safety.NoWorkers + "]";
            Console.WriteLine("Native local Ghost Bar focus observation: " + context + evidence);
            Assert(safety.BoundsUnchanged && safety.PlacementUnchanged && safety.StateUnchanged && safety.MonitorUnchanged && safety.EmptyUndo,
                "Native local focus preserves show state, placement, monitor and empty Undo: " + context + evidence);
            if (accepted) {
                Assert(GetForegroundWindow() == window.Handle, "Accepted native local focus activates exactly the selected owned window: " + context + evidence);
                return true;
            }
            Assert(MeasuredLocalFocusUnavailable(trace, safety),
                "A refused native local focus can skip acceptance only after a measured denied grant and complete unchanged owned MED safety proof: " + context + evidence);
            Console.WriteLine("SKIP: native focus-success acceptance unavailable after measured foreground-grant refusal; unchanged owned state and complete worker cleanup verified (" + context + ").");
            return false;
        }
        sealed class ShelfMovementFixture : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
        }
        static void ShowOwnedShelfMovementFixture(Form window)
        {
            IntPtr foreground = GetForegroundWindow(); Point pointer = Cursor.Position;
            window.Show();
            IntPtr handle = window.Handle; uint process;
            uint own = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            Assert(handle != IntPtr.Zero && Native.IsWindow(handle) &&
                Native.GetWindowThreadProcessId(handle, out process) != 0 && process == own,
                "Native shelf setup targets exactly its live owned HWND/PID before requesting visibility");
            Assert(Native.ShowWindowAsync(handle, 4), "Owned shelf fixture accepts a nonactivating native show request");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // ShowWindowAsync posts to the queue. Await only native visibility,
            // never retry the catalog or relax any production eligibility rule.
            WaitFor(delegate {
                if (window.IsDisposed || window.Handle != handle || !Native.IsWindow(handle) ||
                    Native.GetWindowThreadProcessId(handle, out process) == 0 || process != own)
                    throw new InvalidOperationException("Owned shelf visibility preparation lost its exact HWND/PID.");
                if (clock.ElapsedMilliseconds > 500) throw new TimeoutException("Owned shelf native visibility preparation timed out.");
                bool visible = Native.IsWindowVisible(handle);
                if (clock.ElapsedMilliseconds > 500) throw new TimeoutException("Owned shelf native visibility preparation timed out.");
                return visible;
            });
            Assert(Native.IsWindowVisible(handle) && Native.GetWindowThreadProcessId(handle, out process) != 0 && process == own,
                "Owned shelf fixture is natively visible before its first strict catalog lookup");
            Assert(GetForegroundWindow() == foreground && Cursor.Position == pointer,
                "Nonactivating shelf fixture setup preserves the actual foreground and pointer");
            Console.WriteLine("Owned shelf native visibility ready; nonactivating=true elapsedMs=" + clock.ElapsedMilliseconds);
        }
        static void WindowShelfMoveChecks()
        {
            ShelfFocusClassificationChecks();
            var displays = Native.Displays();
            if (displays.Count < 2) return;
            using (var window = new ShelfMovementFixture { Text = "Window shelf movement test", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(displays[0].X + 80, displays[0].Y + 80, 360, 260) })
            using (var mover = new TaskbarMover()) {
                ShowOwnedShelfMovementFixture(window);
                ShelfWindow selected;
                bool resolved; string lookupTrace;
                WindowShelfCatalog.OwnedWindowTrace = null;
                WindowShelfCatalog.TraceOwnedWindow = window.Handle;
                try {
                    resolved = WindowShelfCatalog.TryReadWindow(window.Handle, out selected);
                    lookupTrace = WindowShelfCatalog.OwnedWindowTrace ?? "unavailable: owned PID proof was lost";
                } finally { WindowShelfCatalog.TraceOwnedWindow = IntPtr.Zero; WindowShelfCatalog.OwnedWindowTrace = null; }
                Console.WriteLine("Owned initial Ghost Bar catalog observation: resolved=" + resolved + " stages=" + lookupTrace);
                Assert(resolved, "Shelf resolves its own disposable test window [owned stages=" + lookupTrace + "]");
                var target = displays[1];
                uint pid = selected.Process;
                selected.Process++;
                Assert(!mover.MoveFromShelf(selected, target) && !mover.CanUndo, "Shelf rejects a stale process identity without recording undo");
                selected.Process = pid;
                selected.ProcessStarted++;
                Assert(!mover.MoveFromShelf(selected, target), "Shelf rejects a replaced process generation");
                selected.ProcessStarted--;
                string key = selected.AppKey;
                selected.AppKey = "exe:another-app";
                Assert(!mover.MoveFromShelf(selected, target), "Shelf rejects changed app identity");
                selected.AppKey = key;
                mover.ExcludedApps.Add(new AppRule { Key = key, Name = "Disposable test" });
                Assert(!mover.MoveFromShelf(selected, target), "Shelf rechecks exclusions at selection time");
                mover.ExcludedApps.Clear();
                CheckNativeShelfFocus(window, mover, selected, displays[0], IntPtr.Zero, "visible local movement fixture");
                Assert(!mover.MoveFromShelf(selected, new Display { Device = "disconnected" }), "Shelf skips disconnected destinations");
                Native.Rect before, after;
                Native.GetWindowRect(window.Handle, out before);
                foreach (bool relative in new[] { false, true }) {
                    mover.ScaleWindowSizeToMonitor = relative;
                    Assert(mover.MoveFromShelf(selected, target) && mover.CanUndo, "Shelf move uses the existing mover and records undo");
                    Assert(mover.Undo(), "Shelf moves use the shared undo history");
                    Native.GetWindowRect(window.Handle, out after);
                    Assert(AreaPolicy.Equal(before, after), "Shelf undo restores exact original bounds in both sizing modes");
                }
                window.WindowState = FormWindowState.Minimized; Application.DoEvents();
                Assert(mover.MoveFromShelf(selected, target) && window.WindowState != FormWindowState.Minimized,
                    "Shelf restores and moves a minimized window");
                Assert(mover.Undo(), "A shelf move of a minimized window can be undone");
                window.WindowState = FormWindowState.Maximized; Application.DoEvents();
                Assert(mover.MoveFromShelf(selected, target) && window.WindowState == FormWindowState.Maximized,
                    "Shelf preserves maximized state");
                Assert(mover.Undo(), "A shelf move of a maximized window can be undone");
                window.Close(); Application.DoEvents();
                Assert(!mover.MoveFromShelf(selected, target), "Closing a listed window invalidates its shelf selection");
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowPlacement(IntPtr window, ref WindowHistory.Placement placement);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct ShelfFocusInput { internal uint Size, Time; }
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetLastInputInfo")]
        static extern bool ShelfFocusLastInput(ref ShelfFocusInput input);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos")]
        static extern bool ShelfFocusCursor(out TaskbarMover.Point point);
        static WindowHistory.Placement ShelfPlacement(IntPtr window)
        {
            var placement = new WindowHistory.Placement { Length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(WindowHistory.Placement)) };
            Assert(GetWindowPlacement(window, ref placement), "Owned shelf fixture exposes its native placement");
            return placement;
        }
        static bool ShelfClick(TaskbarMover mover, ShelfWindow selected, Display target, IntPtr foreground)
        {
            selected.SelectionForeground = foreground; selected.SelectionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return mover.MoveFromShelf(selected, target); }
            finally { selected.SelectionForeground = IntPtr.Zero; selected.SelectionTimestamp = 0; }
        }
        sealed class ShelfHiddenFixture : Form
        {
            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (IsHandleCreated && WindowState == FormWindowState.Minimized) BeginInvoke((MethodInvoker)delegate {
                    if (!IsDisposed && WindowState == FormWindowState.Minimized) Hide();
                });
            }
        }
        static void WindowShelfMinimizeRegistryChecks() { WindowShelfTests.RunMinimizeRegistry(Assert); }
        static void ShelfPeekChecks() { ShelfPeekTests.Run(Assert); ShelfPeekInteractionChecks.Run(Assert); }
        static void ShelfPeekNativeChecks() { ShelfPeekTests.RunNative(Assert); }
        static void WaitForShelfState(Form window, FormWindowState state)
        {
            try { WaitFor(delegate { return Native.IsWindowVisible(window.Handle) && window.WindowState == state; }); }
            catch (Exception error) {
                var placement = ShelfPlacement(window.Handle);
                throw new Exception("Owned hidden fixture restore: expected=" + state + ", actual=" + window.WindowState +
                    ", visible=" + Native.IsWindowVisible(window.Handle) + ", show=" + placement.ShowCommand + ", flags=" + placement.Flags, error);
            }
        }
        // Isolated reflection entry point: creates and manipulates only disposable neutral windows.
        static void WindowShelfHiddenMinimizeChecks()
        {
            var displays = Native.Displays();
            var local = displays[displays.Count - 1]; var remote = displays[0];
            var bounds = new Rectangle(local.X + 80, local.Y + 80, 360, 260);
            using (var window = new ShelfHiddenFixture { Text = "Neutral hidden minimize fixture", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = bounds })
            using (var sibling = new ShelfHiddenFixture { Text = "Neutral untracked hidden fixture", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(local.X + 140, local.Y + 140, 360, 260) })
            using (var mover = new TaskbarMover()) {
                window.Show(); sibling.Show(); Native.ShowWindowAsync(window.Handle, 5); Application.DoEvents();
                ShelfWindow selected, untracked, current;
                Assert(WindowShelfCatalog.TryReadWindow(window.Handle, out selected), "Hidden recovery fixture begins with an eligible visible identity");
                Assert(WindowShelfCatalog.TryReadWindow(sibling.Handle, out untracked), "Untracked sibling begins with an eligible visible identity");
                sibling.WindowState = FormWindowState.Minimized;
                WaitFor(delegate { return !Native.IsWindowVisible(sibling.Handle); });
                foreach (bool relative in new[] { false, true }) foreach (bool maximized in new[] { false, true }) {
                    mover.ScaleWindowSizeToMonitor = relative;
                    window.Show(); window.WindowState = FormWindowState.Normal; window.Bounds = bounds;
                    if (maximized) window.WindowState = FormWindowState.Maximized;
                    Application.DoEvents();
                    var saved = ShelfPlacement(window.Handle);
                    Assert(saved.ShowCommand == (maximized ? 3 : 1), "Owned fixture actually enters its requested native normal/maximized state");
                    IntPtr sourceMonitor = Native.MonitorFromWindow(window.Handle, 2);
                    Assert(ShelfClick(mover, selected, local, window.Handle), "Explicit direct local shelf minimize is accepted");
                    WaitFor(delegate { return !Native.IsWindowVisible(window.Handle) && window.WindowState == FormWindowState.Minimized; });
                    Assert(!WindowShelfCatalog.TryReadWindow(window.Handle, out current) &&
                        !WindowShelfCatalog.TryReadSelection(selected, local, mover.ExcludedApps, out current), "Default catalog and selection still reject hidden iconic windows");
                    var catalog = WindowShelfCatalog.Read(local, mover.ExcludedApps, delegate { return false; }, mover.ShelfMinimizes);
                    Assert(catalog.FindAll(w => w.Handle == window.Handle).Count == 1 && !catalog.Exists(w => w.Handle == sibling.Handle),
                        "Authorized hidden iconic window remains exactly once; untracked hidden sibling stays omitted");
                    Assert(!mover.MoveFromShelf(untracked, local) && !mover.MoveToMonitor(window.Handle, sourceMonitor),
                        "Untracked hidden selection and default hidden MoveToMonitor remain blocked");
                    Assert(WindowShelfCatalog.TryReadSelection(selected, local, mover.ExcludedApps, mover.ShelfMinimizes, out current),
                        "Authorized hidden identity survives a fresh selection read");
                    using (var thumbnail = new ShelfThumbnail(new ShelfRecoveryThumbnailApi(), window.Handle, selected,
                        delegate(ShelfWindow expected) { ShelfWindow fresh; return WindowShelfCatalog.TryReadSelection(expected, local, mover.ExcludedApps, mover.ShelfMinimizes, out fresh); })) {
                        thumbnail.Refresh(new Rectangle(0, 0, 200, 100));
                        Assert(!thumbnail.Available && !thumbnail.Invalid, "Hidden authorized preview uses a selectable fallback without unhiding its source");
                    }
                    using (var manager = new WindowShelfManager(delegate { return new List<Display>(); }, delegate { return mover.ExcludedApps; },
                        delegate { throw new Exception("Paused shelf must never act"); }, mover.ShelfMinimizes)) {
                        manager.Refresh();
                        Assert(!Native.IsWindowVisible(window.Handle) && mover.ShelfMinimizes.Snapshot().Length == 1,
                            "Paused or disabled shelves neither restore nor expose retained windows");
                    }
                    Assert(ShelfClick(mover, selected, local, window.Handle), "Second explicit selection restores a hidden iconic local window");
                    // Reopen immediately, before pumping any outstanding restore message.
                    Assert(WindowShelfCatalog.TryReadSelection(selected, local, mover.ExcludedApps, mover.ShelfMinimizes, out current),
                        "Immediate reopen remains selectable whether restore is pending or already visible");
                    WaitForShelfState(window, maximized ? FormWindowState.Maximized : FormWindowState.Normal);
                    var restored = ShelfPlacement(window.Handle);
                    Assert(restored.ShowCommand == saved.ShowCommand && AreaPolicy.Equal(restored.NormalPosition, saved.NormalPosition) &&
                        Native.MonitorFromWindow(window.Handle, 2) == sourceMonitor && !mover.CanUndo,
                        "Hidden local restore preserves normal/maximized state, exact restore bounds, monitor and Undo");
                    catalog = WindowShelfCatalog.Read(local, mover.ExcludedApps, delegate { return false; }, mover.ShelfMinimizes);
                    Assert(catalog.FindAll(w => w.Handle == window.Handle).Count == 1 && mover.ShelfMinimizes.Snapshot().Length == 0,
                        "Observed visible restoration prunes authority without duplicating the ordinary catalog entry");
                    if (displays.Count > 1) {
                        Assert(ShelfClick(mover, selected, local, window.Handle), "Remote recovery setup minimizes through the local shelf");
                        WaitFor(delegate { return !Native.IsWindowVisible(window.Handle); });
                        Assert(mover.MoveFromShelf(selected, remote) && mover.CanUndo, "Another shelf explicitly restores and moves the authorized hidden window with Undo");
                        WaitForShelfState(window, maximized ? FormWindowState.Maximized : FormWindowState.Normal);
                        Assert(Native.MonitorFromWindow(window.Handle, 2) != sourceMonitor, "Hidden recovery reaches the requested remote monitor");
                        WindowShelfCatalog.TryReadSelection(selected, remote, mover.ExcludedApps, mover.ShelfMinimizes, out current);
                        Assert(ShelfClick(mover, selected, remote, window.Handle), "Local minimize after a remote recovery preserves prior Undo");
                        WaitFor(delegate { return !Native.IsWindowVisible(window.Handle); });
                        Assert(ShelfClick(mover, selected, remote, IntPtr.Zero) && mover.CanUndo, "Hidden local restore keeps the existing move history");
                        WaitForShelfState(window, maximized ? FormWindowState.Maximized : FormWindowState.Normal);
                        Assert(mover.Undo(), "Authorized hidden remote move uses the unchanged shared Undo");
                        Application.DoEvents(); restored = ShelfPlacement(window.Handle);
                        Assert(restored.ShowCommand == saved.ShowCommand && AreaPolicy.Equal(restored.NormalPosition, saved.NormalPosition) &&
                            Native.MonitorFromWindow(window.Handle, 2) == sourceMonitor && !mover.CanUndo,
                            "Remote recovery Undo restores exact source state and placement in both sizing modes");
                        WindowShelfCatalog.TryReadSelection(selected, local, mover.ExcludedApps, mover.ShelfMinimizes, out current);
                    }
                }
                window.Show(); window.WindowState = FormWindowState.Normal; Application.DoEvents();
                foreach (bool maximized in new[] { false, true }) {
                    window.Show();
                    if (maximized) window.WindowState = FormWindowState.Maximized;
                    Application.DoEvents();
                    var state = window.WindowState;
                    Assert(ShelfClick(mover, selected, local, window.Handle), "Native pending-minimize fixture accepts a real ShowWindowAsync request");
                    Assert(Native.IsWindowVisible(window.Handle) && window.WindowState == state,
                        "Native async minimize is still queued before this test thread pumps messages");
                    Assert(WindowShelfCatalog.TryReadSelection(selected, local, mover.ExcludedApps, mover.ShelfMinimizes, out current) &&
                        mover.ShelfMinimizes.Snapshot().Length == 1, "Immediate native reopen does not prune the still-pending minimize request");
                    Assert(ShelfClick(mover, selected, local, window.Handle), "Immediate second native selection queues restore behind the pending minimize");
                    Application.DoEvents();
                    WaitFor(delegate { return Native.IsWindowVisible(window.Handle) && window.WindowState == state; });
                    Assert(!mover.CanUndo, "Native immediate minimize/restore preserves state without adding Undo");
                    Assert(mover.ShelfMinimizes.Snapshot().Length == 0, "Native restore-complete event retires pending authority without waiting out grace");
                    Assert(ShelfClick(mover, selected, local, window.Handle) && mover.LastStatus == "Minimized window from the Ghost Bar",
                        "A third deliberate selection immediately minimizes the settled restored window");
                    WaitFor(delegate { return !Native.IsWindowVisible(window.Handle); });
                    Assert(ShelfClick(mover, selected, local, window.Handle), "Native third-click fixture remains recoverable");
                    WaitForShelfState(window, state);
                    WindowShelfCatalog.TryReadSelection(selected, local, mover.ExcludedApps, mover.ShelfMinimizes, out current);
                }
                window.Show(); window.WindowState = FormWindowState.Normal; Application.DoEvents();
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                using (var grace = new ShelfMinimizeRegistry(delegate { return now; })) {
                    grace.RecordSuccessfulMinimize(selected);
                    Assert(grace.TryRead(selected, mover.ExcludedApps, out current), "Accepted async minimize is retained while still visible/noniconic");
                    now += ShelfMinimizeRegistry.GraceSeconds * System.Diagnostics.Stopwatch.Frequency;
                    Assert(grace.TryRead(selected, mover.ExcludedApps, out current), "Async minimize grace includes its deadline");
                    now++;
                    Assert(!grace.TryRead(selected, mover.ExcludedApps, out current) && grace.Snapshot().Length == 0,
                        "Unsettled visible minimize request expires after bounded grace");
                }
                Assert(ShelfClick(mover, selected, local, window.Handle), "Invalidation checks begin from a successful explicit native minimize");
                WaitFor(delegate { return !Native.IsWindowVisible(window.Handle); });
                foreach (int mismatch in new[] { 0, 1, 2, 3 }) using (var stale = new ShelfMinimizeRegistry()) {
                    var identity = new ShelfWindow { Handle = selected.Handle, Process = selected.Process, ProcessStarted = selected.ProcessStarted, AppKey = selected.AppKey };
                    if (mismatch == 0) identity.Process++;
                    else if (mismatch == 1) identity.ProcessStarted++;
                    else if (mismatch == 2) identity.AppKey = "exe:changed-identity";
                    else identity.Handle = new IntPtr(-1);
                    stale.RecordSuccessfulMinimize(identity);
                    Assert(!stale.TryRead(identity, mover.ExcludedApps, out current) && stale.Snapshot().Length == 0,
                        "Fresh native identity checks prune invalid HWND/PID/start/AppKey authority");
                }
                mover.ExcludedApps.Add(new AppRule { Key = selected.AppKey });
                Assert(!ShelfClick(mover, selected, local, window.Handle) && mover.ShelfMinimizes.Snapshot().Length == 0 && !Native.IsWindowVisible(window.Handle),
                    "A newly excluded app revokes hidden recovery without restoring it");
                mover.ExcludedApps.Clear();
                Assert(!ShelfClick(mover, selected, local, window.Handle), "Removing an exclusion does not authorize an already-hidden app again");
                window.Show(); Native.ShowWindowAsync(window.Handle, 9); Application.DoEvents();
                Assert(ShelfClick(mover, selected, local, window.Handle), "A fresh explicit minimize can establish authority again");
                WaitFor(delegate { return !Native.IsWindowVisible(window.Handle); });
                window.Enabled = false;
                Assert(!ShelfClick(mover, selected, local, window.Handle) && mover.ShelfMinimizes.Snapshot().Length == 0,
                    "Hidden authorization retains disabled-parent eligibility checks");
                window.Enabled = true; window.Show(); Native.ShowWindowAsync(window.Handle, 9); Application.DoEvents();
                mover.ShelfMinimizes.RecordSuccessfulMinimize(selected); window.Hide();
                Assert(!mover.ShelfMinimizes.TryRead(selected, mover.ExcludedApps, out current) && mover.ShelfMinimizes.Snapshot().Length == 0,
                    "A hidden noniconic helper is rejected even during minimize grace");
                window.Show(); Native.ShowWindowAsync(window.Handle, 9); Application.DoEvents();
                Assert(ShelfClick(mover, selected, local, window.Handle), "Closing fixture begins from an authorized local minimize");
                WaitFor(delegate { return !Native.IsWindowVisible(window.Handle); });
                window.Close();
                Assert(!ShelfClick(mover, selected, local, selected.Handle) && mover.ShelfMinimizes.Snapshot().Length == 0,
                    "Closing a retained HWND prunes authority without creating history");
            }
        }
        static void WindowShelfLocalActionChecks()
        {
            var displays = Native.Displays();
            var target = displays[displays.Count - 1]; var other = displays[0];
            var bounds = new Rectangle(target.X + 80, target.Y + 80, 360, 260);
            IntPtr reportedForeground = IntPtr.Zero;
            using (var local = new Form { Text = "Neutral local action fixture", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = bounds })
            using (var peer = new Form { Text = "Neutral other foreground fixture", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(other.X + 160, other.Y + 160, 360, 260) })
            using (var mover = new TaskbarMover(new TaskbarTargets(), delegate { return false; }, delegate { return reportedForeground; })) {
                local.Show(); peer.Show(); Application.DoEvents();
                ShelfWindow selected;
                Assert(WindowShelfCatalog.TryReadWindow(local.Handle, out selected), "Local action fixture exposes its revalidatable identity");
                IntPtr monitor = Native.MonitorFromWindow(local.Handle, 2);
                int focusAccepted = 0, focusRefused = 0;
                Action<IntPtr, string> checkNativeFocus = delegate(IntPtr clickForeground, string context) {
                    if (CheckNativeShelfFocus(local, mover, selected, target, clickForeground, context)) focusAccepted++;
                    else focusRefused++;
                };
                foreach (bool relative in new[] { false, true }) {
                    mover.ScaleWindowSizeToMonitor = relative;
                    foreach (bool maximized in new[] { false, true }) {
                        local.WindowState = FormWindowState.Normal; local.Bounds = bounds;
                        if (maximized) local.WindowState = FormWindowState.Maximized;
                        peer.Activate(); Application.DoEvents();
                        var before = ShelfPlacement(local.Handle);
                        reportedForeground = local.Handle;
                        checkNativeFocus(peer.Handle, "click-time inactive local; relative=" + relative + ", maximized=" + maximized);
                        reportedForeground = peer.Handle;
                        Assert(ShelfClick(mover, selected, target, local.Handle), "Click-time active local selection minimizes despite dismissal changing foreground");
                        WaitFor(delegate { return local.WindowState == FormWindowState.Minimized; });
                        var minimized = ShelfPlacement(local.Handle);
                        Assert(AreaPolicy.Equal(minimized.NormalPosition, before.NormalPosition) && ((minimized.Flags & 2) != 0) == maximized,
                            "Local minimize preserves the prior normal restore bounds and maximized restore flag");
                        Assert(ShelfClick(mover, selected, target, local.Handle), "A currently minimized local selection restores even if its foreground snapshot matched");
                        WaitFor(delegate { return local.WindowState == (maximized ? FormWindowState.Maximized : FormWindowState.Normal); });
                        checkNativeFocus(IntPtr.Zero, "unknown click-time foreground; relative=" + relative + ", maximized=" + maximized);
                        var restored = ShelfPlacement(local.Handle);
                        Assert(AreaPolicy.Equal(restored.NormalPosition, before.NormalPosition) && restored.ShowCommand == before.ShowCommand &&
                            Native.MonitorFromWindow(local.Handle, 2) == monitor && !mover.CanUndo,
                            "Local focus/minimize/restore preserves placement and creates no Undo in either sizing mode");
                        reportedForeground = local.Handle;
                        Assert(mover.MoveFromShelf(selected, target), "Two-argument direct callers capture foreground at entry for active local toggles");
                        WaitFor(delegate { return local.WindowState == FormWindowState.Minimized; });
                        Assert(mover.MoveFromShelf(selected, target) && !mover.CanUndo, "Two-argument direct callers restore minimized locals without recording Undo");
                        WaitFor(delegate { return local.WindowState == (maximized ? FormWindowState.Maximized : FormWindowState.Normal); });
                    }
                }
                Console.WriteLine("Native local Ghost Bar focus outcomes: accepted=" + focusAccepted + ", unavailable=" + focusRefused + ". Accepted cases verified the actual selected foreground HWND; every case verified unchanged state, placement, monitor and empty Undo.");
                local.WindowState = FormWindowState.Normal; Application.DoEvents();
                selected.SelectionForeground = local.Handle;
                selected.SelectionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() - 2 * System.Diagnostics.Stopwatch.Frequency;
                Assert(!mover.MoveFromShelf(selected, target) && local.WindowState == FormWindowState.Normal, "Expired foreground snapshots reject local actions without minimizing");
                selected.SelectionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency;
                Assert(!mover.MoveFromShelf(selected, target) && local.WindowState == FormWindowState.Normal, "Future foreground snapshots reject local actions");
                selected.SelectionTimestamp = 0; selected.SelectionForeground = IntPtr.Zero;
                uint pid = selected.Process;
                selected.Process++;
                Assert(!ShelfClick(mover, selected, target, local.Handle) && local.WindowState == FormWindowState.Normal, "Visible-local minimize rejects stale PID identity");
                selected.Process = pid; selected.ProcessStarted++;
                Assert(!ShelfClick(mover, selected, target, local.Handle), "Visible-local actions reject stale process generations");
                selected.ProcessStarted--;
                string key = selected.AppKey; selected.AppKey = "exe:wrong-local-app";
                Assert(!ShelfClick(mover, selected, target, peer.Handle), "Visible-local focus rejects changed app identity");
                selected.AppKey = key;
                mover.ExcludedApps.Add(new AppRule { Key = key, Name = "Neutral fixture" });
                Assert(!ShelfClick(mover, selected, target, local.Handle) && !ShelfClick(mover, selected, target, peer.Handle),
                    "Fresh exclusions block both visible-local minimize and focus");
                mover.ExcludedApps.Clear(); local.Enabled = false;
                Assert(!ShelfClick(mover, selected, target, local.Handle), "Disabled local windows cannot be minimized through the shelf");
                local.Enabled = true;
                Assert(!ShelfClick(mover, selected, new Display { Device = "disconnected" }, local.Handle), "Local actions cannot target a disconnected display");
                Assert(local.WindowState == FormWindowState.Normal && !mover.CanUndo, "Rejected local actions leave window state and empty Undo history unchanged");
                local.Close(); Application.DoEvents();
                Assert(!ShelfClick(mover, selected, target, selected.Handle) && !mover.CanUndo, "Closed visible-local selections cannot minimize a replacement or create Undo");
            }
        }
        static void WindowHistoryStateChecks()
        {
            var displays = Native.Displays();
            if (displays.Count < 2) return;
            var source = displays[0]; var target = displays[displays.Count - 1];
            var bounds = new Rectangle(source.X + 80, source.Y + 80, 360, 260);
            using (var window = new Form { Text = "Neutral Undo state transition fixture", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = bounds })
            using (var mover = new TaskbarMover()) {
                FenceOwnedNativeMouseInput(mover);
                window.Show(); Application.DoEvents();
                IntPtr sourceMonitor = Native.MonitorFromWindow(window.Handle, 2), targetMonitor = IntPtr.Zero;
                Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Native.Rect rect, IntPtr data) {
                    var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                    if (Native.GetMonitorInfo(monitor, ref info) && info.Device == target.Device) { targetMonitor = monitor; return false; }
                    return true;
                }, IntPtr.Zero);
                foreach (bool relative in new[] { false, true }) {
                    mover.ScaleWindowSizeToMonitor = relative;
                    foreach (bool savedMaximized in new[] { false, true }) foreach (bool currentMaximized in new[] { true, false }) {
                        window.WindowState = FormWindowState.Normal; window.Bounds = bounds; Application.DoEvents();
                        if (savedMaximized) window.WindowState = FormWindowState.Maximized;
                        Application.DoEvents();
                        var saved = ShelfPlacement(window.Handle);
                        Assert(mover.MoveToMonitor(window.Handle, targetMonitor), "Undo state fixture moves using the existing mover without a shelf restore: savedMaximized=" + savedMaximized +
                            ", relative=" + relative + ", status=" + mover.LastStatus);
                        window.WindowState = currentMaximized ? FormWindowState.Maximized : FormWindowState.Normal; Application.DoEvents();
                        Assert(mover.Undo(), "Undo accepts a saved normal/maximized state after the current state changes: savedMaximized=" + savedMaximized +
                            ", currentMaximized=" + currentMaximized + ", relative=" + relative + ", status=" + mover.LastStatus);
                        Application.DoEvents();
                        var restored = ShelfPlacement(window.Handle);
                        Assert(restored.ShowCommand == saved.ShowCommand && Native.MonitorFromWindow(window.Handle, 2) == sourceMonitor &&
                            AreaPolicy.Equal(restored.NormalPosition, saved.NormalPosition),
                            "Undo restores saved state, source monitor and normal bounds: savedMaximized=" + savedMaximized +
                            ", currentMaximized=" + currentMaximized + ", relative=" + relative);
                        Assert(!mover.CanUndo, "State-changing Undo consumes exactly one recorded move");
                    }
                }
            }
        }
        static void WindowShelfRestoreChecks()
        {
            var displays = Native.Displays();
            var target = displays[displays.Count - 1];
            var bounds = new Rectangle(target.X + 80, target.Y + 80, 360, 260);
            IntPtr ownedWindow = IntPtr.Zero;
            using (var window = new Form { Text = "Neutral shelf in-place restore fixture", ShowInTaskbar = true, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = bounds })
            using (var mover = new TaskbarMover(new TaskbarTargets(),
                delegate { throw new InvalidOperationException("Shelf restore fixture must not replay desktop input"); },
                delegate { return ownedWindow; }, false)) {
                FenceNativeMoverHooks(mover);
                mover.MoveApi.Modifiers = delegate { return 0; };
                mover.MoveApi.Focus = delegate(IntPtr handle) {
                    Assert(handle == ownedWindow, "Restore fixture focus request targets only its owned neutral window");
                    return true;
                };
                window.Show(); Application.DoEvents();
                ownedWindow = window.Handle;
                ShelfWindow selected;
                Assert(WindowShelfCatalog.TryReadWindow(window.Handle, out selected), "In-place restore fixture has a shelf identity");
                IntPtr monitor = Native.MonitorFromWindow(window.Handle, 2);
                Assert(ShelfClick(mover, selected, target, IntPtr.Zero) && !mover.CanUndo,
                    "Visible local selection requests focus without creating Undo; status=" + mover.LastStatus);
                foreach (bool relative in new[] { false, true }) {
                    mover.ScaleWindowSizeToMonitor = relative;
                    foreach (bool maximized in new[] { false, true }) {
                        window.WindowState = FormWindowState.Normal; window.Bounds = bounds; Application.DoEvents();
                        var normal = ShelfPlacement(window.Handle);
                        if (maximized) window.WindowState = FormWindowState.Maximized;
                        Application.DoEvents();
                        var before = ShelfPlacement(window.Handle);
                        window.WindowState = FormWindowState.Minimized; Application.DoEvents();
                        var minimized = ShelfPlacement(window.Handle);
                        Assert(minimized.ShowCommand == 2 && ((minimized.Flags & 2) != 0) == maximized,
                            "Native minimized placement retains the fixture's normal versus maximized restore state");
                        Assert(mover.MoveFromShelf(selected, target), "Shelf accepts an in-place restore in both sizing modes and prior states");
                        WaitFor(delegate { return window.WindowState == (maximized ? FormWindowState.Maximized : FormWindowState.Normal); });
                        // Replace the disabled desktop minimize-end hook with an
                        // explicit observation of this fixture's completed restore.
                        mover.ShelfMinimizes.ObserveRestore(window.Handle, mover.ExcludedApps);
                        Assert(mover.ShelfMinimizes.Snapshot().Length == 0, "Completed owned restore clears its explicit minimize authority");
                        var after = ShelfPlacement(window.Handle);
                        Assert(after.ShowCommand == before.ShowCommand && AreaPolicy.Equal(after.NormalPosition, normal.NormalPosition) &&
                            Native.MonitorFromWindow(window.Handle, 2) == monitor,
                            "In-place restore preserves normal restore bounds, previous show state, and monitor");
                        Assert(!mover.CanUndo && !mover.Undo(), "A local restore neither creates nor consumes an Undo entry");
                        Assert(ShelfClick(mover, selected, target, window.Handle), "Repeating an active local selection after restoration minimizes it");
                        WaitFor(delegate { return window.WindowState == FormWindowState.Minimized; });
                        Assert(mover.MoveFromShelf(selected, target) && !mover.CanUndo, "The following local selection restores it without creating Undo");
                        WaitFor(delegate { return window.WindowState == (maximized ? FormWindowState.Maximized : FormWindowState.Normal); });
                        mover.ShelfMinimizes.ObserveRestore(window.Handle, mover.ExcludedApps);
                        Assert(mover.ShelfMinimizes.Snapshot().Length == 0, "Completed owned toggle restore clears its prior normal/maximized authority");
                        window.WindowState = FormWindowState.Normal; Application.DoEvents();
                        Assert(AreaPolicy.Equal(ShelfPlacement(window.Handle).NormalPosition, normal.NormalPosition),
                            "Unmaximizing after local restore returns to the unchanged normal bounds");
                    }
                }
                window.WindowState = FormWindowState.Minimized; Application.DoEvents();
                uint pid = selected.Process;
                selected.Process++;
                Assert(!mover.MoveFromShelf(selected, target) && window.WindowState == FormWindowState.Minimized, "Local restore rejects a stale PID");
                selected.Process = pid;
                selected.ProcessStarted++;
                Assert(!mover.MoveFromShelf(selected, target) && window.WindowState == FormWindowState.Minimized, "Local restore rejects a stale process generation");
                selected.ProcessStarted--;
                string key = selected.AppKey;
                selected.AppKey = "exe:wrong-local-app";
                Assert(!mover.MoveFromShelf(selected, target) && window.WindowState == FormWindowState.Minimized, "Local restore rejects changed app identity");
                selected.AppKey = key;
                mover.ExcludedApps.Add(new AppRule { Key = key, Name = "Neutral fixture" });
                Assert(!mover.MoveFromShelf(selected, target) && window.WindowState == FormWindowState.Minimized, "Local restore rechecks a newly excluded app");
                mover.ExcludedApps.Clear();
                Assert(!mover.MoveFromShelf(selected, new Display { Device = "disconnected" }) && !mover.CanUndo,
                    "A disconnected destination cannot restore the local selection or create Undo");
                window.WindowState = FormWindowState.Normal; Application.DoEvents();
                Assert(ShelfClick(mover, selected, target, IntPtr.Zero), "A local selection restored before its click uses its current visible state");

                if (displays.Count > 1) {
                    var source = displays[0];
                    foreach (bool relative in new[] { false, true }) {
                        mover.ScaleWindowSizeToMonitor = relative;
                        window.Bounds = new Rectangle(source.X + 80, source.Y + 80, 360, 260); Application.DoEvents();
                        var origin = ShelfPlacement(window.Handle);
                        IntPtr sourceMonitor = Native.MonitorFromWindow(window.Handle, 2);
                        Assert(mover.MoveFromShelf(selected, target) && mover.CanUndo,
                            "A cross-monitor move establishes the existing move Undo; relative=" + relative + ", status=" + mover.LastStatus +
                            ", undo=" + mover.CanUndo + ", source=" + source.Device + ", target=" + target.Device +
                            ", sourceMonitor=" + sourceMonitor.ToInt64() + ", currentMonitor=" + Native.MonitorFromWindow(window.Handle, 2).ToInt64() +
                            ", state=" + window.WindowState + ", originalShow=" + origin.ShowCommand + ", originalFlags=" + origin.Flags +
                            ", originalBounds=" + origin.NormalPosition.Left + "," + origin.NormalPosition.Top + "," +
                            origin.NormalPosition.Right + "," + origin.NormalPosition.Bottom);
                        WaitFor(delegate { return Native.MonitorFromWindow(window.Handle, 2) == monitor; });
                        foreach (bool maximized in new[] { false, true }) {
                            if (maximized) window.WindowState = FormWindowState.Maximized;
                            Application.DoEvents();
                            var before = ShelfPlacement(window.Handle);
                            window.WindowState = FormWindowState.Minimized; Application.DoEvents();
                            mover.ExcludedApps.Add(new AppRule { Key = key, Name = "Neutral fixture" });
                            Assert(!mover.MoveFromShelf(selected, target) && mover.CanUndo, "Rejected local restoration retains previous move Undo");
                            mover.ExcludedApps.Clear();
                            Assert(mover.MoveFromShelf(selected, target), "User path restores a window minimized after moving to the shelf display");
                            WaitFor(delegate { return window.WindowState == (maximized ? FormWindowState.Maximized : FormWindowState.Normal); });
                            mover.ShelfMinimizes.ObserveRestore(window.Handle, mover.ExcludedApps);
                            Assert(mover.ShelfMinimizes.Snapshot().Length == 0, "Completed local restore after a move clears only the owned minimize authority");
                            var restored = ShelfPlacement(window.Handle);
                            Assert(restored.ShowCommand == before.ShowCommand && AreaPolicy.Equal(restored.NormalPosition, before.NormalPosition) &&
                                Native.MonitorFromWindow(window.Handle, 2) == monitor && mover.CanUndo,
                                "Repeated local restores preserve placement and the previous cross-monitor Undo");
                            Assert(ShelfClick(mover, selected, target, IntPtr.Zero) && mover.CanUndo,
                                "Local focus also leaves the previous cross-monitor Undo intact");
                            Assert(ShelfClick(mover, selected, target, window.Handle) && mover.CanUndo, "Repeat active-local clicks minimize and leave the previous Undo intact");
                            WaitFor(delegate { return window.WindowState == FormWindowState.Minimized; });
                            Assert(mover.MoveFromShelf(selected, target) && mover.CanUndo, "Local toggle restoration also retains the previous Undo");
                            WaitFor(delegate { return window.WindowState == (maximized ? FormWindowState.Maximized : FormWindowState.Normal); });
                            mover.ShelfMinimizes.ObserveRestore(window.Handle, mover.ExcludedApps);
                            Assert(mover.ShelfMinimizes.Snapshot().Length == 0, "Completed local toggle restore retains Undo while clearing minimize authority");
                        }
                        Assert(mover.Undo(), "Undo after local restores still undoes the original cross-monitor move");
                        WaitFor(delegate { return window.WindowState == FormWindowState.Normal; });
                        var undone = ShelfPlacement(window.Handle);
                        Assert(Native.MonitorFromWindow(window.Handle, 2) == sourceMonitor,
                            "Undo after local restores returns to the original monitor; source=" + source.Device + ", target=" + target.Device +
                            ", origin=" + origin.NormalPosition.Left + "," + origin.NormalPosition.Top + "," + origin.NormalPosition.Right + "," + origin.NormalPosition.Bottom +
                            ", restored=" + undone.NormalPosition.Left + "," + undone.NormalPosition.Top + "," + undone.NormalPosition.Right + "," + undone.NormalPosition.Bottom);
                        Assert(AreaPolicy.Equal(undone.NormalPosition, origin.NormalPosition) && undone.ShowCommand == origin.ShowCommand && !mover.CanUndo,
                            "Undo returns to the original source bounds and consumes exactly the original move entry");
                    }
                }
                window.WindowState = FormWindowState.Minimized; Application.DoEvents();
                window.Close(); Application.DoEvents();
                Assert(!mover.MoveFromShelf(selected, target) && !mover.CanUndo, "A closed local minimized window cannot be restored or add Undo");
            }
        }
        static void AppActionsLayoutChecks()
        {
            foreach (int dpi in new[] { 96, 144, 192 }) {
                float scale = dpi / 96f;
                using (var baseFont = new Font("Segoe UI", 10))
                using (var font = new Font("Segoe UI", 10 * scale))
                using (var host = new Panel { Size = new Size(640, 44), Font = baseFont }) {
                    var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
                        WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
                    var add = Ui.Button("Add app", "plus", delegate { });
                    var remove = Ui.Button("Remove exclusion", null, delegate { });
                    actions.Controls.Add(add); actions.Controls.Add(remove); host.Controls.Add(actions);
                    host.Scale(new SizeF(scale, scale));
                    host.Font = font;
                    host.CreateControl(); host.PerformLayout(); actions.PerformLayout();
                    Assert(remove.Left >= 0 && remove.Right <= add.Left && add.Right <= actions.ClientSize.Width &&
                        remove.Bottom <= actions.ClientSize.Height && add.Bottom <= actions.ClientSize.Height,
                        "App actions fit without overlap at minimum width and " + dpi + " DPI: row=" +
                        actions.ClientSize + ", add=" + add.Bounds + ", remove=" + remove.Bounds);
                    Size text = TextRenderer.MeasureText(remove.Text, remove.Font);
                    Assert(text.Width <= remove.ClientSize.Width - remove.Padding.Horizontal &&
                        text.Height <= remove.ClientSize.Height - remove.Padding.Vertical,
                        "Remove exclusion label fits its button at " + dpi + " DPI");
                }
            }
        }
        static void UiPolishChecks()
        {
            TooltipPathChecks();
            var compactDisplays = new List<Display> {
                new Display { X = -1920, Y = 300, Width = 1920, Height = 1080 },
                new Display { X = 0, Y = 0, Width = 2560, Height = 1440 },
                new Display { X = 0, Y = 1927, Width = 1024, Height = 600 }
            };
            var smallTile = Rectangle.Round(DisplayMap.MapBounds(compactDisplays[2], compactDisplays, new Size(700, 190)));
            using (var font = new Font("Segoe UI", DisplayMap.TileFontSize(smallTile.Height, 96), FontStyle.Bold)) {
                int textHeight = TextRenderer.MeasureText("3", font, Size.Empty, TextFormatFlags.NoPadding).Height;
                Assert(smallTile.Height >= 30 && (smallTile.Height + textHeight) / 2 < DisplayMap.StatusBarBounds(smallTile.Size, 96).Top,
                    "Small display numeral clears its status mark in a compact map");
            }
            foreach (int height in new[] { 22, 34, 52 }) {
                using (var font = new Font("Segoe UI", DisplayMap.TileFontSize(height, 96), FontStyle.Bold)) {
                    int textHeight = TextRenderer.MeasureText("3", font, Size.Empty, TextFormatFlags.NoPadding).Height;
                    Assert((height + textHeight) / 2 < DisplayMap.StatusBarBounds(new Size(80, height), 96).Top,
                        "Tile label and bottom status mark remain separate at " + height + " pixels");
                }
            }
            using (var list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false }) {
                SettingsForm.ThemeListHeader(list);
                list.Columns.Add("Application", 200);
                list.Items.Add(new ListViewItem("Example") { Tag = "example" });
                list.CreateControl();
                list.Items[0].Selected = true;
                Assert(list.OwnerDraw && list.SelectedItems.Count == 1 && (string)list.SelectedItems[0].Tag == "example",
                    "The themed Apps header leaves native list selection and item data intact");
                list.Columns.Add("Behavior", 150);
                list.Width = 720;
                SettingsForm.FitLastListColumn(list);
                Assert(list.Columns[0].Width + list.Columns[1].Width == list.ClientSize.Width,
                    "The final Apps column fills the unused native header area");
                list.Width = 520;
                Assert(list.Columns[0].Width + list.Columns[1].Width == list.ClientSize.Width &&
                    list.SelectedItems.Count == 1,
                    "The Apps header follows window resizing without changing selection");
                list.Columns[0].Width = 240;
                Assert(list.Columns[0].Width + list.Columns[1].Width == list.ClientSize.Width,
                    "Resizing the first Apps column keeps the remaining header filled");
            }
            foreach (int width in new[] { 620, 720, 1060 }) {
                using (var host = new Panel { Size = new Size(width, 100), Font = new Font("Segoe UI", 10) }) {
                    var nickname = new TextBox { BorderStyle = BorderStyle.None };
                    var selector = new ThemedComboBox();
                    selector.Items.AddRange(new object[] { "Keep window size", "Keep relative size and position" });
                    var aliasRow = SettingsForm.BoundedFieldRow("Nickname", new Button { Size = new Size(24, 24) }, nickname, 144, 280);
                    var sizingRow = SettingsForm.WindowSizingRow(selector, new Button { Size = new Size(24, 24) });
                    aliasRow.Dock = DockStyle.Top; aliasRow.Height = 44;
                    sizingRow.Dock = DockStyle.Bottom; sizingRow.Height = 44;
                    host.Controls.Add(aliasRow); host.Controls.Add(sizingRow);
                    host.CreateControl(); host.PerformLayout(); aliasRow.PerformLayout(); sizingRow.PerformLayout();
                    Ui.Theme(host);
                    Assert(aliasRow.GetColumnWidths()[0] == sizingRow.GetColumnWidths()[0],
                        "Nickname and sizing labels align at " + width + " pixels");
                    Assert(nickname.Parent.Left == selector.Parent.Left && nickname.Parent.Width <= 280 && selector.Parent.Width <= 320,
                        "Both fields are left-aligned and bounded at " + width + " pixels");
                    Assert(nickname.Parent.Right <= aliasRow.ClientSize.Width && selector.Parent.Right <= sizingRow.ClientSize.Width,
                        "Both fields remain inside the row at " + width + " pixels");
                    Assert(nickname.TabStop && selector.TabStop && !nickname.Parent.TabStop && !selector.Parent.TabStop,
                        "Field frames do not interrupt keyboard navigation");
                    Assert(selector.Parent.ClientSize.Height - selector.Parent.Padding.Vertical >= selector.PreferredSize.Height,
                        "Sizing selector fits vertically inside its frame at " + width + " pixels");
                    Assert(((FieldFrame)selector.Parent).BackColor == Ui.Surface,
                        "Themed sizing frame retains its field surface");
                    Assert(selector.Parent.Padding == Padding.Empty,
                        "The native sizing selector owns the single themed border");
                }
            }
            using (var header = new SectionHeader("Selected display"))
            using (var rule = new SectionRule()) {
                Ui.Theme(header); Ui.Theme(rule);
                Assert(header.ForeColor == Ui.Muted && rule.BackColor == Ui.Line && rule.Margin == Padding.Empty,
                    "Section heading and divider retain their semantic theme colors");
            }
            foreach (int dpi in new[] { 96, 144, 192 }) {
                int available = (int)Math.Ceiling(520 * dpi / 96f);
                Assert(SettingsForm.TabItemSize(dpi) == new Size((int)Math.Ceiling(120 * dpi / 96f), (int)Math.Ceiling(40 * dpi / 96f)),
                    "Tab headers scale at " + dpi + " DPI");
                Assert(SettingsForm.BoundedFieldWidth(available, 280, dpi) == (int)Math.Ceiling(280 * dpi / 96f) &&
                    SettingsForm.BoundedFieldWidth(available, 320, dpi) == (int)Math.Ceiling(320 * dpi / 96f),
                    "Field caps scale at " + dpi + " DPI");
                Assert(SettingsForm.BoundedFieldWidth((int)Math.Ceiling(220 * dpi / 96f), 320, dpi) == (int)Math.Ceiling(220 * dpi / 96f),
                    "Sizing selector yields to narrow space at " + dpi + " DPI");
                using (var font = new Font("Segoe UI", 10 * dpi / 96f)) {
                    Size area = new Size((int)Math.Ceiling(640 * dpi / 96f), (int)Math.Ceiling(600 * dpi / 96f));
                    Size help = SettingsForm.MeasureHelp(SettingsForm.WindowSizingHelp, font, dpi, area);
                    int line = TextRenderer.MeasureText("Window sizing", font).Height;
                    Assert(help.Width < area.Width && help.Height > line * 2 && help.Height < area.Height,
                        "Long sizing help wraps inside a modest work area at " + dpi + " DPI");
                    Size edge = SettingsForm.MeasureHelp(SettingsForm.WindowSizingHelp, font, dpi,
                        new Size((int)Math.Ceiling(340 * dpi / 96f), (int)Math.Ceiling(600 * dpi / 96f)));
                    Assert(edge.Width <= (int)Math.Ceiling(340 * dpi / 96f) && edge.Height > help.Height,
                        "Help reflows for a narrow display at " + dpi + " DPI");
                }
            }
            using (var tabs = new ThemedTabControl { Size = new Size(500, 220) }) {
                tabs.TabPages.AddRange(new[] {
                    new TabPage("Displays") { UseVisualStyleBackColor = false },
                    new TabPage("Apps") { UseVisualStyleBackColor = false },
                    new TabPage("Settings") { UseVisualStyleBackColor = false }
                });
                Ui.Theme(tabs);
                tabs.SelectedIndex = 1;
                tabs.SelectedIndex = 2;
                Assert(tabs.Appearance == TabAppearance.Normal && tabs.TabStop && tabs.TabPages.Count == 3,
                    "The themed navigation retains a native focusable tab control");
                Assert(tabs.SelectedTab.Text == "Settings" && tabs.SelectedIndex == 2,
                    "Native tab selection still changes the active page");
                tabs.CreateControl();
                using (var pixels = new Bitmap(tabs.Width, tabs.Height)) {
                    tabs.DrawToBitmap(pixels, new Rectangle(Point.Empty, tabs.Size));
                    Assert(pixels.GetPixel(450, 12).ToArgb() == Ui.Background.ToArgb() &&
                        pixels.GetPixel(1, 100).ToArgb() == Ui.Background.ToArgb(),
                        "Tab paint covers the unused header strip and outer page frame");
                }
            }
            using (var selector = new ThemedComboBox { Size = new Size(320, 32) })
            using (var pixels = new Bitmap(320, 32))
            using (var graphics = Graphics.FromImage(pixels)) {
                graphics.Clear(Color.White);
                selector.PaintChrome(graphics);
                Assert(pixels.GetPixel(0, 16).ToArgb() == Ui.Line.ToArgb() &&
                    pixels.GetPixel(310, 8).ToArgb() == Ui.Surface.ToArgb(),
                    "Sizing selector chrome paints one themed border and arrow area");
            }
        }
        static void TooltipPathChecks()
        {
            using (var host = new Panel { Size = new Size(360, 100), Font = new Font("Segoe UI", 10) })
            using (var hover = new ToolTip()) {
                SettingsForm.ConfigureHelp(hover);
                var info = SettingsForm.CreateInfoMarker("Window sizing", SettingsForm.WindowSizingHelp, hover);
                const string nextHelp = "Show or hide the taskbar on the selected display.";
                var next = SettingsForm.CreateInfoMarker("Show taskbar", nextHelp, hover);
                next.Location = new Point(80, 0);
                host.Controls.Add(info);
                host.Controls.Add(next);
                int popups = 0;
                hover.Popup += delegate { popups++; };
                Assert(hover.OwnerDraw && !hover.IsBalloon &&
                    hover.GetToolTip(info) == SettingsForm.WindowSizingHelp && hover.GetToolTip(next) == nextHelp,
                    "Information markers register their distinct descriptions on the themed hover provider");
                Assert(!info.TabStop && !next.TabStop && !((Control)info is IButtonControl) &&
                    info.AccessibleRole == AccessibleRole.Graphic,
                    "Information markers are non-button graphics outside the keyboard tab order");
                Assert(info.AccessibleName == "Window sizing information" &&
                    info.AccessibleDescription == SettingsForm.WindowSizingHelp && next.AccessibleDescription == nextHelp,
                    "Hover-only information remains available to accessibility clients");
                Assert(info.Size == new Size(24, 24) && info.Image != null && info.Image.Size == new Size(14, 14),
                    "Hover-only markers preserve the compact icon and stable hit area");
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var click = typeof(Control).GetMethod("OnClick", flags);
                click.Invoke(info, new object[] { EventArgs.Empty });
                click.Invoke(info, new object[] { EventArgs.Empty });
                click.Invoke(next, new object[] { EventArgs.Empty });
                Assert(popups == 0, "Clicking or repeatedly clicking an information marker does not open help");
                Assert(hover.GetToolTip(info) == SettingsForm.WindowSizingHelp && hover.GetToolTip(next) == nextHelp,
                    "Clicks leave the automatic hover associations unchanged");
                // Exercise the shared rendering callbacks without opening a tracked popup or injecting input.
                foreach (var marker in new[] { info, next }) {
                    var popup = new PopupEventArgs(marker, marker, false, Size.Empty);
                    typeof(ToolTip).GetMethod("OnPopup", flags).Invoke(hover, new object[] { popup });
                    string text = hover.GetToolTip(marker);
                    Assert(popup.ToolTipSize == SettingsForm.MeasureHelp(text, marker.Font, marker.DeviceDpi,
                        Screen.GetWorkingArea(marker).Size), "Hover help retains bounded measurement for " + marker.AccessibleName);
                    using (var pixels = new Bitmap(popup.ToolTipSize.Width, popup.ToolTipSize.Height))
                    using (var graphics = Graphics.FromImage(pixels)) {
                        graphics.Clear(Color.Magenta);
                        var draw = new DrawToolTipEventArgs(graphics, marker, marker, new Rectangle(Point.Empty, pixels.Size),
                            text, Color.White, Color.Black, marker.Font);
                        typeof(ToolTip).GetMethod("OnDraw", flags).Invoke(hover, new object[] { draw });
                        Assert(pixels.GetPixel(0, 0).ToArgb() == Ui.Line.ToArgb() && pixels.GetPixel(3, 3).ToArgb() == Ui.Surface.ToArgb(),
                            "Hover help paints the themed border and surface for " + marker.AccessibleName);
                    }
                }
            }
        }
        static void ReviewPolicyChecks()
        {
            string owned = Startup.CommandFor(@"C:\Test\TaskbarSelect.exe");
            Assert(Startup.ApprovalAllows(null), "An unlisted startup approval is not disabled by Windows");
            Assert(Startup.ApprovalAllows(Approval(2)) && Startup.ApprovalAllows(Approval(6)), "Known Windows enabled startup states are recognized");
            Assert(!Startup.ApprovalAllows(Approval(3)) && !Startup.ApprovalAllows(Approval(7)), "Known Windows disabled startup states are respected");
            Assert(!Startup.ApprovalAllows(Approval(99)) && !Startup.ApprovalAllows(new byte[2]) && !Startup.ApprovalAllows("invalid"),
                "Unknown or malformed startup approvals are not silently overridden");
            var disabled = Startup.Describe(owned, null, Approval(3), null);
            Assert(!disabled.Enabled && disabled.WindowsBlocked && disabled.CanChange, "Existing but Windows-disabled shortcut is shown as off, not enabled");
            Assert(Startup.Describe(owned, null, Approval(2), null).Enabled, "Windows re-enabling the shortcut is reflected without editing settings");
            Assert(!Startup.Describe(null, null, null, null).Enabled, "No startup registration is off");
            Assert(!Startup.Describe("foreign", null, null, null).CanChange, "Foreign startup shortcut cannot be edited");
            Assert(!Startup.CanMigrate(owned, null, Approval(3), null), "Migration does not re-enable a disabled legacy Run entry");
            Assert(!Startup.CanMigrate(owned, owned, Approval(2), Approval(3)), "Migration does not override a disabled Startup-folder preference");
            Assert(Startup.CanMigrate(owned, null, Approval(2), null), "Enabled owned legacy entry can migrate");
            Assert(!Startup.CanMigrate(owned, "foreign", null, null), "Migration does not overwrite another shortcut");
            Assert(!Startup.Describe(null, owned, null, Approval(7)).Enabled, "Disabled legacy entry remains off in the interface");
            Assert(Startup.Describe(null, null, Approval(3), null).WindowsBlocked, "Stale Windows disable state remains visible before re-registration");

            var origin = new TaskbarMover.Point { X = -500, Y = 20 };
            var threshold = new System.Drawing.Size(8, 8);
            Assert(!TaskbarInput.IsDrag(origin, new TaskbarMover.Point { X = -498, Y = 22 }, threshold), "Small pointer jitter stays a click");
            var end = new TaskbarMover.Point { X = -450, Y = 20 };
            Assert(TaskbarInput.IsDrag(origin, end, threshold), "Icon movement crosses the native drag threshold");
            Assert(TaskbarInput.IsDrag(origin, new TaskbarMover.Point { X = -500, Y = 16 }, threshold), "Vertical drags are recognized too");
            var directThreshold = TaskbarInput.DirectDragThreshold(new System.Drawing.Size(4, 4), 96);
            Assert(directThreshold.Width == 8 && directThreshold.Height == 8 &&
                !TaskbarInput.IsDirectDrag(origin, new TaskbarMover.Point { X = -493, Y = 20 }, directThreshold) &&
                TaskbarInput.IsDirectDrag(origin, new TaskbarMover.Point { X = -492, Y = 20 }, directThreshold),
                "Swallowed direct press tolerates seven pixels of jitter but hands off at eight");
            Assert(TaskbarInput.DirectDragThreshold(new System.Drawing.Size(4, 4), 144).Width == 12 &&
                TaskbarInput.DirectDragThreshold(new System.Drawing.Size(4, 4), 192).Height == 16 &&
                TaskbarInput.DirectDragThreshold(new System.Drawing.Size(4, 4), 0).Width == 8,
                "Direct-press jitter tolerance follows taskbar DPI with a 96 DPI fallback");
            directThreshold = TaskbarInput.DirectDragThreshold(new System.Drawing.Size(24, 30), 96);
            Assert(directThreshold.Width == 12 && directThreshold.Height == 15 &&
                TaskbarInput.IsDirectDrag(origin, new TaskbarMover.Point { X = -500, Y = 35 }, directThreshold),
                "A larger native drag threshold is never reduced for direct presses");
            var desktop = new System.Drawing.Rectangle(-1920, -1080, 5760, 2520);
            var events = TaskbarInput.ReplayEvents(origin, end, false, desktop);
            Assert(events.Length == 2 && (events[0].Mouse.Flags & 2) != 0 && (events[1].Mouse.Flags & 6) == 0,
                "Drag handoff restores the swallowed down then moves without a premature up");
            Assert(events[0].Mouse.X != events[1].Mouse.X && events[0].Mouse.Y == events[1].Mouse.Y,
                "Drag replay retains the original press location and current pointer location");
            Assert(events[0].Mouse.ExtraInfo == TaskbarInput.ReplayTag && events[1].Mouse.ExtraInfo == TaskbarInput.ReplayTag,
                "Both replay events bypass interception rather than recursively moving windows");
            Assert((events[0].Mouse.Flags & 0xE001) == 0xE001 && System.Runtime.InteropServices.Marshal.SizeOf(typeof(TaskbarInput.Input)) == 40,
                "Replay uses absolute virtual-desktop coordinates and the native x64 INPUT layout");
            events = TaskbarInput.ReplayEvents(origin, end, true, desktop);
            Assert((events[1].Mouse.Flags & 4) != 0, "A completed native fallback click includes the matching release");
            Assert(TaskbarInput.Normalize(-1920, -1920, 5760) >= 0 && TaskbarInput.Normalize(3839, -1920, 5760) <= 65535,
                "Mouse normalization handles negative monitor coordinates");
        }

        static void TargetCacheChecks(Form window)
        {
            Native.Rect bounds; Native.GetWindowRect(window.Handle, out bounds);
            var point = new TaskbarMover.Point { X = bounds.Left + 20, Y = bounds.Top + 20 };
            var first = new TaskbarTargets.Target { Taskbar = window.Handle, Window = window.Handle, Process = 12, TaskbarBounds = bounds,
                Captured = DateTime.UtcNow, Button = new TaskbarMover.Click { AppId = "App.One", Position = point, ButtonBounds = bounds } };
            TaskbarTargets.Target response = first;
            using (var cache = new TaskbarTargets(delegate { return response; })) {
                var probe = cache.Verify(window.Handle, point); WaitFor(() => probe.Complete);
                Assert(cache.Find(window.Handle, point) == first, "A successful lookup populates the hover cache");
                response = null;
                probe = cache.Verify(window.Handle, point); WaitFor(() => probe.Complete);
                Assert(probe.Result == null && cache.Find(window.Handle, point) == null, "A negative lookup clears the stale icon even when taskbar bounds did not change");
                var second = new TaskbarTargets.Target { Taskbar = first.Taskbar, Window = first.Window, Process = first.Process,
                    TaskbarBounds = bounds, Captured = DateTime.UtcNow,
                    Button = new TaskbarMover.Click { AppId = "App.Two", Position = point, ButtonBounds = bounds } };
                Assert(!TaskbarTargets.SameButton(first, second), "Same rectangle occupied by another app invalidates a direct click");
                second.Button.AppId = first.Button.AppId;
                Assert(TaskbarTargets.SameButton(first, second), "Fresh matching icon and window can be moved");
                second.Button.ButtonBounds.Left++;
                Assert(!TaskbarTargets.SameButton(first, second), "Reordered icon cannot reuse its old click bounds");
                second.Button.ButtonBounds = bounds; second.Window = new IntPtr(99);
                Assert(!TaskbarTargets.SameButton(first, second), "Replaced window cannot reuse cached identity");
                second.Window = first.Window; second.Process++;
                Assert(!TaskbarTargets.SameButton(first, second), "Recycled handle from another process fails verification");
                Assert(!TaskbarTargets.SameButton(first, null), "Unavailable UI Automation never validates an old icon");
                response = second;
                cache.Dispose(); probe = cache.Verify(window.Handle, point);
                Assert(probe.Complete && probe.Result == null && cache.Find(window.Handle, point) == null, "Disposed target resolver cannot publish or queue more work");
            }
        }

        sealed class ActivationTestWindow : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ShowWindow")]
        static extern bool ActivationShowWindow(IntPtr window, int command);

        // Standalone policy checks: only owned nonactivating HWNDs, injected foreground,
        // no installed input hooks, no message pump, and no valid movement destination.
        static void TaskbarActivationChecks()
        {
            int start = count;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            using (var window = new ActivationTestWindow { ShowInTaskbar = false, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(0, 0, 320, 200) })
            using (var unrelated = new Form { ShowInTaskbar = false }) {
                window.Show();
                IntPtr reportedForeground = unrelated.Handle;
                IntPtr source = Native.MonitorFromWindow(window.Handle, 2), destination = new IntPtr(-1);
                uint process; Native.GetWindowThreadProcessId(window.Handle, out process);
                Native.Rect bounds; Native.GetWindowRect(window.Handle, out bounds);
                var target = new TaskbarTargets.Target { Taskbar = window.Handle, TaskbarBounds = bounds,
                    Window = window.Handle, Process = process,
                    Button = new TaskbarMover.Click { AppId = System.Diagnostics.Process.GetCurrentProcess().ProcessName,
                        ButtonBounds = bounds, Position = new TaskbarMover.Point { X = 20, Y = 20 } } };
                int replays = 0;
                using (var resolver = new TaskbarTargets(delegate { return target; }))
                using (var mover = new TaskbarMover(resolver, delegate { replays++; return true; }, delegate { return reportedForeground; }, false)) {
                    FenceNativeMoverHooks(mover);
                    var clickField = typeof(TaskbarMover).GetField("click", flags);
                    var releaseField = typeof(TaskbarMover).GetField("directRelease", flags);
                    var checkField = typeof(TaskbarMover).GetField("directCheck", flags);
                    Action<TaskbarMover.Click, IntPtr> queue = delegate(TaskbarMover.Click pending, IntPtr activated) {
                        mover.CancelPending();
                        clickField.SetValue(mover, pending);
                        typeof(TaskbarMover).GetField("activatedWindow", flags).SetValue(mover, activated);
                        typeof(TaskbarMover).GetField("activatedAt", flags).SetValue(mover, DateTime.UtcNow);
                    };
                    Func<TaskbarMover.Click> nativeClick = delegate {
                        return new TaskbarMover.Click { Taskbar = target.Taskbar, Origin = target,
                            PriorWindow = unrelated.Handle, PriorProcess = process, PriorMonitor = source,
                            TargetMonitor = destination, AppId = target.Button.AppId,
                            Time = DateTime.UtcNow.AddMilliseconds(-100), ReleasedAt = DateTime.UtcNow,
                            Verification = new TaskbarTargets.Probe { Complete = true, Result = target } };
                    };
                    mover.Enabled = true;
                    Assert(Native.IsWindowVisible(window.Handle) && window.WindowState == FormWindowState.Normal,
                        "Activation fixture is a visible normal window without requesting real foreground");
                    Assert(!mover.TryStartDirectMove(target, destination), "Inactive direct taskbar click stays with Explorer");
                    reportedForeground = IntPtr.Zero;
                    Assert(!mover.TryStartDirectMove(target, destination), "Missing foreground cannot authorize direct interception");
                    reportedForeground = window.Handle;
                    Assert(mover.TryStartDirectMove(target, destination), "Injected active single window accepts cross-monitor direct press");
                    mover.CompleteDirectPress(false);
                    target.NeedsNativeSelection = true;
                    Assert(!mover.TryStartDirectMove(target, destination),
                        "An otherwise eligible owned target with weak taskbar provenance never swallows the native click");
                    target.NeedsNativeSelection = false;
                    typeof(TaskbarMover).GetMethod("ForegroundEvent", flags).Invoke(mover,
                        new object[] { IntPtr.Zero, (uint)3, window.Handle, 0, 0, (uint)0, (uint)123456 });
                    Assert((uint)typeof(TaskbarMover).GetField("activatedNativeTime", flags).GetValue(mover) == 123456,
                        "Owned foreground callback records the event's original timestamp without a focus request");
                    Assert(releaseField.GetValue(mover) == null && checkField.GetValue(mover) == null,
                        "Outside release does not queue verification or movement");
                    Assert(!mover.TryStartDirectMove(target, source), "Active same-monitor click retains native minimize");
                    target.Button.Grouped = true;
                    Assert(!mover.TryStartDirectMove(target, destination), "Active grouped button retains native selection");
                    target.Button.Grouped = false;
                    Assert(mover.TryStartDirectMove(target, destination), "Active gesture starts before foreground changes");
                    reportedForeground = unrelated.Handle;
                    mover.CompleteDirectPress(true); mover.ProcessClick();
                    Assert(releaseField.GetValue(mover) == null && checkField.GetValue(mover) == null && replays == 0 &&
                        mover.LastStatus == "Foreground changed; direct move cancelled",
                        "Foreground lost before release cancels without verification, success, or replay");
                    reportedForeground = window.Handle;
                    foreach (bool complete in new[] { false, true }) {
                        Assert(mover.TryStartDirectMove(target, destination), "Active gesture starts before asynchronous verification");
                        mover.CompleteDirectPress(true);
                        Assert(releaseField.GetValue(mover) == target && checkField.GetValue(mover) != null,
                            "Active release still requires fresh taskbar verification");
                        checkField.SetValue(mover, new TaskbarTargets.Probe { Complete = complete, Result = complete ? target : null });
                        reportedForeground = unrelated.Handle;
                        mover.ProcessClick();
                        Assert(releaseField.GetValue(mover) == null && !mover.CanUndo && replays == 0 &&
                            mover.LastStatus == "Foreground changed; direct move cancelled",
                            "Foreground loss cancels pending or completed verification without replay");
                        reportedForeground = window.Handle;
                        mover.ProcessClick();
                        Assert(releaseField.GetValue(mover) == null && !mover.CanUndo,
                            "Foreground returning later cannot resurrect the cancelled gesture");
                    }
                    Assert(mover.TryStartDirectMove(target, destination), "Cancellation control starts an active press");
                    mover.CancelPending(); mover.CompleteDirectPress(true);
                    Assert(releaseField.GetValue(mover) == null, "Explicit cancellation still prevents release processing");

                    var candidate = nativeClick();
                    candidate.ReleasedAt = DateTime.MinValue;
                    queue(candidate, window.Handle); mover.ProcessClick();
                    Assert(clickField.GetValue(mover) == candidate && !candidate.Inspected,
                        "Native activation cannot be processed before the original release");
                    candidate = nativeClick(); reportedForeground = unrelated.Handle;
                    queue(candidate, IntPtr.Zero); mover.ProcessClick();
                    Assert(clickField.GetValue(mover) == candidate && candidate.Inspected && !mover.CanUndo,
                        "Visible inactive verified target waits for Windows activation");
                    candidate = nativeClick();
                    queue(candidate, window.Handle); mover.ProcessClick();
                    Assert(clickField.GetValue(mover) == null && mover.LastStatus == "Activation changed; kept normal Windows behavior",
                        "Formerly activated target cannot move after foreground changes again");
                    candidate = nativeClick(); reportedForeground = window.Handle;
                    queue(candidate, window.Handle); mover.ProcessClick();
                    Assert(clickField.GetValue(mover) == null && mover.LastStatus == "The activated window could not be moved",
                        "Current verified native activation still reaches movement, with invalid destination safely refusing it");
                    candidate = nativeClick();
                    candidate.Verification.Result = new TaskbarTargets.Target { Taskbar = target.Taskbar, Window = target.Window,
                        Process = process, TaskbarBounds = bounds, Button = new TaskbarMover.Click {
                            AppId = "Changed.Icon", ButtonBounds = bounds } };
                    queue(candidate, window.Handle); mover.ProcessClick();
                    Assert(mover.LastStatus == "Ignored a changed taskbar icon" + (AppProfile.IsTest ? " (app-id)" : "") && !mover.CanUndo,
                        "Foreground gating does not bypass original fresh icon verification");
                    candidate = nativeClick(); candidate.Verification.Complete = false;
                    queue(candidate, window.Handle); mover.ProcessClick();
                    Assert(clickField.GetValue(mover) == candidate && !candidate.Inspected,
                        "Incomplete native verification still waits even when foreground matches");
                    target.Button.Grouped = true;
                    candidate = nativeClick();
                    queue(candidate, IntPtr.Zero); mover.ProcessClick();
                    Assert(candidate.Grouped && clickField.GetValue(mover) == candidate,
                        "Grouped original click still waits for a native window selection");
                    candidate = nativeClick();
                    queue(candidate, window.Handle); mover.ProcessClick();
                    Assert(mover.LastStatus == "The activated window could not be moved",
                        "Grouped verified activation still reaches the original movement path");
                    target.Button.Grouped = false;
                    candidate = nativeClick(); candidate.PreviewOnly = true;
                    candidate.Verification.SelectedWindow = window.Handle;
                    candidate.Verification.Selection = new TaskbarTargets.PreviewSelection { Window = window.Handle, Process = process };
                    reportedForeground = unrelated.Handle;
                    queue(candidate, window.Handle); mover.ProcessClick();
                    Assert(mover.LastStatus == "Selection changed; kept normal Windows behavior" && !mover.CanUndo,
                        "Preview foreground event also refuses a formerly activated target");

                    ActivationShowWindow(window.Handle, 7); // SW_SHOWMINNOACTIVE
                    Assert(window.WindowState == FormWindowState.Minimized, "Owned fixture minimizes without requesting focus");
                    Assert(!mover.TryStartDirectMove(target, destination), "Inactive minimized click stays with Explorer");
                    reportedForeground = window.Handle;
                    Assert(!mover.TryStartDirectMove(target, destination), "Minimized target is refused even with a stale foreground signal");
                    candidate = nativeClick(); reportedForeground = unrelated.Handle;
                    queue(candidate, IntPtr.Zero); mover.ProcessClick();
                    Assert(clickField.GetValue(mover) == candidate && window.WindowState == FormWindowState.Minimized && !mover.CanUndo,
                        "Verified minimized inactive app is not restored or moved before activation");
                    candidate = nativeClick(); reportedForeground = window.Handle;
                    queue(candidate, window.Handle); mover.ProcessClick();
                    Assert(mover.LastStatus == "Activation changed; kept normal Windows behavior" && window.WindowState == FormWindowState.Minimized,
                        "A foreground event for a still-minimized inactive app cannot bypass the restore wait");
                    candidate = nativeClick();
                    Assert(!TaskbarMover.NativeMinimizeMatches(candidate, target),
                        "Same-process unrelated prior window does not prove a foreground minimize");
                    candidate.PriorWindow = window.Handle;
                    Assert(TaskbarMover.NativeMinimizeMatches(candidate, target),
                        "Exact prior foreground HWND and PID retain the cross-monitor native minimize workaround");
                    candidate.PriorProcess++;
                    Assert(!TaskbarMover.NativeMinimizeMatches(candidate, target), "Reused prior HWND with changed PID cannot restore");
                    candidate.PriorProcess = process;
                    candidate.PriorMonitor = IntPtr.Zero;
                    Assert(!TaskbarMover.NativeMinimizeMatches(candidate, target), "Unknown prior monitor cannot authorize restore");
                    candidate.PriorMonitor = destination;
                    Assert(!TaskbarMover.NativeMinimizeMatches(candidate, target), "Changed source monitor cannot authorize restore");
                    candidate.PriorMonitor = source;
                    candidate.Grouped = true;
                    Assert(!TaskbarMover.NativeMinimizeMatches(candidate, target), "Grouped selection cannot use single-window restore workaround");
                    candidate.Grouped = false; target.Button.Grouped = true;
                    Assert(!TaskbarMover.NativeMinimizeMatches(candidate, target), "Fresh grouped target cannot use restore workaround");
                    target.Button.Grouped = false; candidate.SelectionStarted = true;
                    Assert(!TaskbarMover.NativeMinimizeMatches(candidate, target), "Preview selection cannot restore the prior app speculatively");
                    candidate.SelectionStarted = false; candidate.Verification.Complete = false;
                    Assert(!TaskbarMover.NativeMinimizeMatches(candidate, target), "Unfinished verification cannot authorize native restore");
                    candidate.Verification.Complete = true; candidate.TargetMonitor = source;
                    queue(candidate, IntPtr.Zero); mover.ProcessClick();
                    Assert(mover.LastStatus == "Used normal Windows minimize behavior" && window.WindowState == FormWindowState.Minimized && !mover.CanUndo,
                        "Proven same-monitor foreground minimize stays minimized without undo history");
                    ActivationShowWindow(window.Handle, 4); // SW_SHOWNOACTIVATE
                    Assert(window.WindowState == FormWindowState.Normal && !TaskbarMover.NativeMinimizeMatches(candidate, target),
                        "Restored normal window no longer takes the native minimize workaround");
                    Assert(replays == 0 && !mover.CanUndo && Native.MonitorFromWindow(window.Handle, 2) == source,
                        "Activation checks never replay input, move a window, or create undo history");
                }
            }
            Console.WriteLine("PASS: " + (count - start) + " taskbar activation policy checks (injected foreground; no native focus requests).");
        }

        static void ColdCacheForegroundChecks(TaskbarTargets resolver, Action<TaskbarTargets.Target> supplyFresh, Action<IntPtr> setForeground,
            TaskbarMover mover, Form window, IntPtr originalMonitor, IntPtr otherMonitor)
        {
            // Inject only the foreground signal; geometry, visibility, PID, movement and undo use the real test window.
            setForeground(window.Handle);
            Native.Rect bounds; Native.GetWindowRect(window.Handle, out bounds);
            uint process; Native.GetWindowThreadProcessId(window.Handle, out process);
            var point = new TaskbarMover.Point { X = bounds.Left + 20, Y = bounds.Top + 20 };
            var target = new TaskbarTargets.Target {
                Taskbar = window.Handle, TaskbarBounds = bounds, Window = window.Handle, Process = process,
                AppKey = AppCatalog.ForWindow(window.Handle).Key, Captured = DateTime.UtcNow,
                Windows = new[] { new TaskbarTargets.WindowIdentity { Window = window.Handle, Process = process } },
                Button = new TaskbarMover.Click { AppId = System.Diagnostics.Process.GetCurrentProcess().ProcessName,
                    Position = point, ButtonBounds = bounds, Grouped = false }
            };
            // The fake resolver supplies the same identity that a fresh UIA click check would return.
            supplyFresh(target);
            var verification = resolver.Verify(target.Taskbar, point);
            WaitFor(() => verification.Complete);
            Assert(verification.Result == target, "Cold-cache click has a fresh resolved single-window target");
            var click = new TaskbarMover.Click {
                Position = point, Taskbar = target.Taskbar, TargetMonitor = otherMonitor,
                PriorWindow = window.Handle, PriorProcess = process, PriorMonitor = originalMonitor,
                Time = DateTime.UtcNow.AddMilliseconds(-350), ReleasedAt = DateTime.UtcNow.AddMilliseconds(-300),
                Verification = verification, AppId = target.Button.AppId
            };
            Assert(mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "Fresh exact single-window identity still foreground on another monitor is eligible");
            target.NeedsNativeSelection = true;
            Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "An otherwise eligible weak target cannot take the unchanged-foreground cold-cache shortcut");
            target.NeedsNativeSelection = false;
            setForeground(IntPtr.Zero);
            Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "Losing foreground invalidates the cold-cache fallback");
            setForeground(window.Handle);
            click.PriorProcess++;
            Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "Reused prior HWND with a different recorded PID cannot move");
            click.PriorProcess = process;
            click.PriorMonitor = IntPtr.Zero;
            Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "Missing or changed source-monitor identity cannot move");
            click.PriorMonitor = originalMonitor;
            using (var unrelated = new Form { ShowInTaskbar = false }) {
                unrelated.CreateControl();
                target.Window = unrelated.Handle;
                Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                    "Another window of the same process cannot substitute for the prior foreground window");
                target.Window = window.Handle;
            }
            target.Process++;
            Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "Changed fresh target PID cannot reuse the prior handle");
            target.Process = process;
            target.Button.Grouped = true;
            Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "Grouped button cannot take the single-window foreground fallback");
            target.Button.Grouped = false;
            click.TargetMonitor = originalMonitor;
            Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "Same-monitor click remains available for native minimization");
            click.TargetMonitor = otherMonitor;
            click.ReleasedAt = DateTime.UtcNow.AddMilliseconds(-1100);
            Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "A late resolver result cannot cause an eight-second delayed move");
            click.ReleasedAt = DateTime.UtcNow.AddMilliseconds(-300);
            mover.ExcludedApps.Add(new AppRule { Key = target.AppKey, Name = "Test" });
            Assert(!mover.FreshForegroundClickMatches(click, target, DateTime.UtcNow),
                "Excluded app cannot use the cold-cache fallback");
            mover.ExcludedApps.Clear();

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(TaskbarMover).GetField("click", flags).SetValue(mover, click);
            typeof(TaskbarMover).GetField("activatedWindow", flags).SetValue(mover, IntPtr.Zero);
            mover.ProcessClick();
            Assert(Native.MonitorFromWindow(window.Handle, 2) == otherMonitor &&
                mover.LastStatus == "Moved the verified foreground window" && mover.Undo(),
                "ProcessClick moves only the freshly verified active single window and can undo it");
            Assert(Native.MonitorFromWindow(window.Handle, 2) == originalMonitor,
                "Cold-cache fallback undo returns the disposable window to its source monitor");
        }

        static void PreviewSafetyChecks(TaskbarMover mover, Form window, IntPtr otherMonitor)
        {
            var candidate = new TaskbarMover.Click { PreviewOnly = true, Inspected = true, SelectionStarted = true,
                Time = DateTime.UtcNow.AddMilliseconds(-500), SelectionTime = DateTime.UtcNow.AddMilliseconds(-500),
                ReleasedAt = DateTime.UtcNow.AddMilliseconds(-300), TargetMonitor = otherMonitor, AppId = "Unrelated.Application",
                SelectedWindow = window.Handle };
            Native.GetWindowThreadProcessId(window.Handle, out candidate.SelectedProcess);
            Assert(!TaskbarMover.PreviewMatches(candidate, window.Handle), "A thumbnail selection cannot move an unrelated activated app");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(TaskbarMover).GetField("click", flags).SetValue(mover, candidate);
            typeof(TaskbarMover).GetField("activatedWindow", flags).SetValue(mover, window.Handle);
            typeof(TaskbarMover).GetField("activatedAt", flags).SetValue(mover, DateTime.UtcNow);
            IntPtr original = Native.MonitorFromWindow(window.Handle, 2);
            mover.ProcessClick();
            Assert(Native.MonitorFromWindow(window.Handle, 2) == original && !mover.CanUndo, "Pending preview does not move a focus-stealing unrelated window");
            mover.CancelPending();
            candidate.AppId = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            candidate.SelectedWindow = IntPtr.Zero;
            Assert(!TaskbarMover.PreviewMatches(candidate, window.Handle), "Missing thumbnail identity fails closed even for the expected app");
            using (var other = new Form { ShowInTaskbar = false, Opacity = 0 }) {
                other.Show();
                candidate.SelectedWindow = other.Handle;
                Assert(!TaskbarMover.PreviewMatches(candidate, window.Handle), "Another window of the same app cannot substitute for the clicked thumbnail");
                other.Close();
            }
            candidate.SelectedWindow = window.Handle;
            Assert(TaskbarMover.PreviewMatches(candidate, window.Handle), "The exact selected window still matches its app");
            typeof(TaskbarMover).GetField("click", flags).SetValue(mover, candidate);
            typeof(TaskbarMover).GetField("activatedWindow", flags).SetValue(mover, window.Handle);
            typeof(TaskbarMover).GetField("activatedAt", flags).SetValue(mover, DateTime.UtcNow);
            mover.ProcessClick();
            Assert(Native.MonitorFromWindow(window.Handle, 2) == otherMonitor && mover.Undo(), "A verified thumbnail selection still moves and supports undo");
            Assert(!TaskbarMover.MatchesApp(new TaskbarMover.Click(), window.Handle), "Unresolved app identity cannot throw or match");
        }

        static void PreviewHoverChecks(Form popup)
        {
            Native.Rect bounds; Native.GetWindowRect(popup.Handle, out bounds);
            var item = bounds; item.Right = item.Left + (item.Right - item.Left) / 2;
            var point = new TaskbarMover.Point { X = item.Left + 5, Y = item.Top + 5 };
            var origin = new TaskbarTargets.Target { Taskbar = popup.Handle,
                Button = new TaskbarMover.Click { AppId = "Chrome", Grouped = true } };
            var captured = new TaskbarTargets.PreviewSelection { Origin = origin, Preview = popup.Handle, Window = popup.Handle,
                PreviewBounds = bounds, ItemBounds = item, Captured = DateTime.UtcNow };
            Native.GetWindowThreadProcessId(popup.Handle, out captured.Process);
            int reads = 0;
            TaskbarTargets.PreviewSelection response = captured;
            using (var targets = new TaskbarTargets(delegate { return null; }, delegate { reads++; return response; })) {
                var hover = targets.VerifyPreview(origin, popup.Handle, point);
                WaitFor(() => hover.Complete);
                Assert(hover.SelectedWindow == popup.Handle && reads == 1, "Preview lookup captures the selected window while the popup is visible");
                response = null;
                var click = targets.VerifyPreview(origin, popup.Handle, point);
                Assert(click.Complete && click.SelectedWindow == popup.Handle && click.Selection.Process == captured.Process && reads == 1,
                    "Click uses the captured hover selection without querying a popup that disappears on release");
                var otherPoint = new TaskbarMover.Point { X = item.Right + 5, Y = point.Y };
                Assert(!TaskbarTargets.PreviewFits(captured, origin, popup.Handle, otherPoint, bounds, DateTime.UtcNow),
                    "A neighboring thumbnail cannot reuse the first thumbnail's hover identity");
                Assert(!TaskbarTargets.PreviewFits(captured, origin, popup.Handle, point, bounds, captured.Captured.AddMilliseconds(401)),
                    "An expired preview selection must be looked up again");
                var changedBounds = bounds; changedBounds.Left++;
                Assert(!TaskbarTargets.PreviewFits(captured, origin, popup.Handle, point, changedBounds, DateTime.UtcNow),
                    "A moved or resized popup invalidates the preview hit");
                var otherApp = new TaskbarTargets.Target { Taskbar = popup.Handle, Button = new TaskbarMover.Click { AppId = "AnotherApp", Grouped = true } };
                Assert(!TaskbarTargets.PreviewFits(captured, otherApp, popup.Handle, point, bounds, DateTime.UtcNow),
                    "Another app cannot reuse Chrome's preview selection");
                var miss = targets.VerifyPreview(origin, popup.Handle, otherPoint);
                WaitFor(() => miss.Complete);
                Assert(miss.SelectedWindow == IntPtr.Zero, "Unavailable neighboring preview does not substitute the previous selection");
                targets.Dispose();
                Assert(targets.VerifyPreview(origin, popup.Handle, point).SelectedWindow == IntPtr.Zero,
                    "Disposed resolver drops cached thumbnail selections");
            }
        }

        static void PreviewTreeChecks()
        {
            var root = new Native.Rect { Left = 100, Top = 500, Right = 600, Bottom = 750 };
            var left = new Native.Rect { Left = 110, Top = 510, Right = 340, Bottom = 740 };
            var right = new Native.Rect { Left = 360, Top = 510, Right = 590, Bottom = 740 };
            var firstWindow = new IntPtr(11); var secondWindow = new IntPtr(22);
            Func<string, IntPtr> resolve = name => name == "First - Google Chrome" ? firstWindow : name == "Second - Google Chrome" ? secondWindow : IntPtr.Zero;
            var elements = new List<TaskbarTargets.PreviewElement> {
                new TaskbarTargets.PreviewElement { Name = "", Process = 77, Bounds = root },
                new TaskbarTargets.PreviewElement { Name = "First - Google Chrome", Process = 77, Bounds = left },
                new TaskbarTargets.PreviewElement { Name = "Second - Google Chrome", Process = 77, Bounds = right }
            };
            var leftPoint = new TaskbarMover.Point { X = 200, Y = 600 };
            var rightPoint = new TaskbarMover.Point { X = 450, Y = 600 };
            var hit = TaskbarTargets.SelectPreviewElement(elements, leftPoint, 77, resolve);
            Assert(hit != null && hit.Window == firstWindow, "Unnamed native preview host does not hide the named left thumbnail in its descendants");
            hit = TaskbarTargets.SelectPreviewElement(elements, rightPoint, 77, resolve);
            Assert(hit != null && hit.Window == secondWindow, "The right thumbnail resolves its own window from the descendant tree");
            Assert(TaskbarTargets.SelectPreviewElement(elements, new TaskbarMover.Point { X = 350, Y = 600 }, 77, resolve) == null,
                "Blank space between thumbnail controls never selects either window");
            Assert(TaskbarTargets.SelectPreviewElement(elements, leftPoint, 99, resolve) == null, "Controls from another process cannot supply a thumbnail identity");
            elements.Add(new TaskbarTargets.PreviewElement { Name = "First - Google Chrome", Process = 77, Bounds = root });
            hit = TaskbarTargets.SelectPreviewElement(elements, leftPoint, 77, resolve);
            Assert(hit != null && AreaPolicy.Equal(hit.ItemBounds, left), "Matching parent labels do not expand the click target to the whole preview");
            Assert(TaskbarTargets.SelectPreviewElement(elements, rightPoint, 77, resolve) == null, "Conflicting overlapping preview names fail closed");
            Assert(TaskbarTargets.SelectPreviewElement(elements, leftPoint, 77, delegate { return IntPtr.Zero; }) == null,
                "Unavailable or ambiguous window names cannot be guessed from geometry");
        }
        static void NativePreviewChecks()
        {
            var popup = new IntPtr(10); var left = new IntPtr(20); var right = new IntPtr(30);
            var sequence = new NativePreviewSelection(popup, 100);
            sequence.Observe(popup, 116); sequence.Release(240); sequence.Observe(left, 256);
            Assert(sequence.WindowAt(270) == left, "Observed Explorer popup-to-window sequence selects the left thumbnail");
            sequence.Observe(left, 280);
            Assert(sequence.WindowAt(290) == left, "Repeated events for the selected window retain its identity");
            sequence.Observe(right, 300);
            Assert(sequence.WindowAt(310) == IntPtr.Zero, "A second window taking focus makes the native selection ambiguous");
            sequence = new NativePreviewSelection(popup, 100);
            sequence.Observe(popup, 116); sequence.Release(240); sequence.Observe(right, 240);
            Assert(sequence.WindowAt(250) == right, "Selection can share the mouse-up timestamp on Windows' coarse clock");
            Assert(sequence.WindowAt(741) == IntPtr.Zero, "A native selection expires instead of moving a later focus change");
            sequence = new NativePreviewSelection(popup, 100);
            sequence.Observe(popup, 90); sequence.Release(240); sequence.Observe(left, 250);
            Assert(sequence.WindowAt(260) == IntPtr.Zero, "Queued popup activation from before mouse-down cannot arm a new click");
            sequence = new NativePreviewSelection(popup, 100);
            sequence.Observe(new IntPtr(99), 110); sequence.Release(240); sequence.Observe(left, 250);
            Assert(sequence.WindowAt(260) == IntPtr.Zero, "A different popup cannot authorize native window selection");
            sequence = new NativePreviewSelection(popup, 100);
            sequence.Observe(popup, 110); sequence.Observe(left, 120);
            Assert(sequence.WindowAt(130) == IntPtr.Zero, "Held thumbnail clicks never move a window");
            sequence.Release(240); sequence.Observe(left, 200);
            Assert(sequence.WindowAt(260) == IntPtr.Zero, "Pre-release activation cannot identify the chosen thumbnail");
            sequence.Observe(left, 741);
            Assert(sequence.WindowAt(742) == IntPtr.Zero, "Late activation after the selection deadline is ignored");
            sequence = new NativePreviewSelection(popup, UInt32.MaxValue - 100);
            sequence.Observe(popup, UInt32.MaxValue - 50); sequence.Release(UInt32.MaxValue - 10); sequence.Observe(left, 5);
            Assert(sequence.WindowAt(20) == left, "Native event timing survives the Windows tick-counter wraparound");
            sequence = new NativePreviewSelection(popup, 100);
            sequence.Release(101); sequence.Observe(popup, 116); sequence.Observe(left, 120);
            Assert(sequence.WindowAt(130) == left, "Fast clicks tolerate Explorer processing mouse-up after its input timestamp");

            var first = new TaskbarTargets.WindowIdentity { Window = left, Process = 1 };
            var second = new TaskbarTargets.WindowIdentity { Window = right, Process = 2 };
            Assert(TaskbarTargets.SameWindows(new[] { first, second }, new[] { second, first }), "Native selection ignores harmless window enumeration order changes");
            Assert(!TaskbarTargets.SameWindows(new[] { first, second }, new[] { second }), "Closing a thumbnail invalidates the native selection fallback");
            Assert(!TaskbarTargets.SameWindows(new[] { first, first }, new[] { first, second }), "Duplicate identities cannot disguise a changed window set");
            Assert(!TaskbarTargets.SameWindows(new[] { first }, new[] { new TaskbarTargets.WindowIdentity { Window = left, Process = 7 } }),
                "A reused window handle from another process invalidates selection");
            Assert(!TaskbarTargets.SameWindows(null, new[] { first }), "No app-window snapshot means no native fallback");
        }

        static RecoveryResult RecoverTestWindow(Form window, Action restoreAreas, Action cleanup = null)
        {
            var sample = new Bar { Handle = window.Handle.ToInt64(), Visible = true };
            Assert(Native.Hide(sample), "Recovery fault test hides only its own disposable window");
            WaitFor(() => !Native.IsWindowVisible(window.Handle));
            bool nativeRestoreCalled = false;
            var result = Recovery.Run(restoreAreas, delegate {
                Native.Restore(sample); nativeRestoreCalled = true;
            }, cleanup == null ? (Action)null : delegate {
                Assert(nativeRestoreCalled, "Uninstall cleanup runs only after the native restore attempt");
                cleanup();
            });
            WaitFor(() => Native.IsWindowVisible(window.Handle));
            Assert(nativeRestoreCalled && Native.GetProp(window.Handle, Native.Marker) == IntPtr.Zero,
                "Recovery restores the hidden test window and clears its ownership marker even after a journal failure");
            return result;
        }

        static void RecoveryFailureChecks(Form window)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect-RecoveryTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string journal = Path.Combine(directory, "workarea-recovery.json");
            string settings = Path.Combine(directory, "settings.json");
            try {
                foreach (string damaged in new[] { "[{\"Identity\":", "null", "[null]", "[{}]", "[1]" }) {
                    File.WriteAllText(journal, damaged);
                    var result = RecoverTestWindow(window, delegate { new WorkAreas(journal).RestoreAll(); });
                    Assert(result.Success && result.PreservedJournal != null && !File.Exists(journal),
                        "Malformed recovery data is isolated without blocking native taskbar recovery");
                    Assert(File.ReadAllText(result.PreservedJournal) == damaged,
                        "The damaged journal is preserved byte-for-byte as a separate file");
                    new WorkAreas(journal).RestoreAll();
                    Assert(result.Messages.Count == 1, "Recovery reports that exact saved work areas were unavailable");
                }
                File.WriteAllText(journal, "[]");
                var intact = RecoverTestWindow(window, delegate { new WorkAreas(journal).RestoreAll(); });
                Assert(intact.Success && intact.PreservedJournal == null && File.ReadAllText(journal) == "[]",
                    "A valid journal is not quarantined or rewritten by empty recovery");

                bool cleaned = false;
                File.WriteAllText(settings, "saved settings");
                File.WriteAllText(journal, "[{broken");
                var uninstall = RecoverTestWindow(window, delegate { new WorkAreas(journal).RestoreAll(); }, delegate {
                    File.Delete(settings); cleaned = true;
                });
                Assert(uninstall.Success && cleaned && !File.Exists(settings) && File.Exists(uninstall.PreservedJournal),
                    "Uninstall cleanup can finish after a corrupt journal is preserved and native recovery runs");

                cleaned = false;
                File.WriteAllText(journal, "[]");
                using (var locked = File.Open(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                    var inaccessible = RecoverTestWindow(window, delegate { new WorkAreas(journal).RestoreAll(); }, delegate { cleaned = true; });
                    Assert(!inaccessible.Success && !cleaned && inaccessible.PreservedJournal == null,
                        "A locked journal still triggers native recovery but blocks destructive uninstall cleanup");
                }
                Assert(File.ReadAllText(journal) == "[]", "Read failures preserve the original journal for retry");

                File.WriteAllText(journal, "[{broken");
                FileStream archiveLock = null;
                try {
                    var blockedArchive = RecoverTestWindow(window, delegate {
                        try { new WorkAreas(journal).RestoreAll(); }
                        catch (CorruptRecoveryJournalException) {
                            archiveLock = File.Open(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            throw;
                        }
                    }, delegate { cleaned = true; });
                    Assert(!blockedArchive.Success && !cleaned && File.Exists(journal),
                        "Failed quarantine preserves the original file, still restores the test window, and prevents cleanup");
                } finally { if (archiveLock != null) archiveLock.Dispose(); }

                string fakeIdentity = "RecoveryTest-" + Guid.NewGuid().ToString("N");
                var fakeMonitor = new Native.Rect { Left = 1000, Top = 0, Right = 2920, Bottom = 1080 };
                var fakeOriginal = fakeMonitor; fakeOriginal.Bottom -= 48;
                var fakePlatform = new WorkAreaGuardTests.FakePlatform {
                    Connected = new List<Display> { new Display { Identity = fakeIdentity, Device = "recovery-fixture" } },
                    Info = new Native.MonitorInfo { Device = "recovery-fixture", Monitor = fakeMonitor, Work = fakeOriginal }
                };
                string validJournal = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new[] {
                    new AreaRecord { Identity = fakeIdentity, Monitor = fakeMonitor, Original = fakeOriginal, Applied = fakeMonitor }
                });
                File.WriteAllText(journal, validJournal);
                var areas = new WorkAreas(journal, fakePlatform);
                using (var locked = File.Open(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                    var saveFailure = RecoverTestWindow(window, areas.RestoreAll, delegate { cleaned = true; });
                    Assert(!saveFailure.Success && !cleaned, "An actual journal replacement failure cannot skip native recovery or allow uninstall cleanup");
                }
                Assert(File.ReadAllText(journal) == validJournal, "Failed journal replacement keeps the previous recovery records intact");
                Assert(fakePlatform.Sets.Count == 0, "An already-restored injected unique monitor retires ownership without a real desktop mutation");
                var nativeFailure = Recovery.Run(delegate { }, delegate { throw new InvalidOperationException("Simulated native recovery failure"); }, delegate { cleaned = true; });
                Assert(!nativeFailure.Success && !cleaned, "Native recovery failure is reported and prevents uninstall cleanup");
                var cleanupFailure = RecoverTestWindow(window, delegate { }, delegate { throw new IOException("Simulated uninstall cleanup failure"); });
                Assert(!cleanupFailure.Success && cleanupFailure.Messages.Count == 1, "Cleanup errors are reported after the test window has been restored");
            } finally {
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }

        static void NativePreviewIntegration(Form window)
        {
            uint process; Native.GetWindowThreadProcessId(window.Handle, out process);
            var identity = new TaskbarTargets.WindowIdentity { Window = window.Handle, Process = process };
            var origin = new TaskbarTargets.Target { Taskbar = window.Handle,
                Button = new TaskbarMover.Click { AppId = "TestApp", Grouped = true }, Windows = new[] { identity } };
            TaskbarTargets.Target response = origin;
            using (var targets = new TaskbarTargets(delegate { return response; }, delegate { return null; })) {
                var probe = targets.VerifyPreview(origin, window.Handle, new TaskbarMover.Point());
                WaitFor(() => probe.Complete);
                Assert(probe.NativeFallback && TaskbarTargets.NativePreviewMatches(probe, window.Handle),
                    "Unavailable preview accessibility permits only a freshly verified native app-window selection");
                Assert(!TaskbarTargets.NativePreviewMatches(probe, new IntPtr(99)), "Unrelated foreground windows cannot pass native preview verification");
                response = null;
                probe = targets.VerifyPreview(origin, window.Handle, new TaskbarMover.Point());
                WaitFor(() => probe.Complete);
                Assert(!probe.NativeFallback, "An unavailable or changed originating icon leaves the preview to Windows");
            }
            using (var targets = new TaskbarTargets(delegate { return origin; }, delegate { throw new InvalidOperationException("Preview disappeared"); })) {
                var probe = targets.VerifyPreview(origin, window.Handle, new TaskbarMover.Point());
                WaitFor(() => probe.Complete);
                Assert(probe.NativeFallback, "A disappearing UI Automation popup still permits fresh native-selection verification");
            }
            using (var sibling = new Form { ShowInTaskbar = false, Opacity = 0 }) {
                sibling.Show();
                origin.Windows = new[] { identity, new TaskbarTargets.WindowIdentity { Window = sibling.Handle, Process = process } };
                var probe = new TaskbarTargets.Probe { Origin = origin, Result = origin, Complete = true, NativeFallback = true };
                Assert(TaskbarTargets.NativePreviewMatches(probe, window.Handle), "An intact app-window set validates its selected member");
                sibling.Close();
                Assert(!TaskbarTargets.NativePreviewMatches(probe, window.Handle), "Closing a sibling after verification cannot move the window Windows focuses next");
            }
        }
        static void WindowSizingDefaultSettingsChecks()
        {
            int start = count;
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            Assert(new Settings().ScaleWindowSizeToMonitor && serializer.Deserialize<Settings>("{}").ScaleWindowSizeToMonitor,
                "Fresh and empty settings default to relative size and position");
            string directory = Path.Combine(Path.GetTempPath(), "TaskbarSelect.SizingDefault." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                string path = Path.Combine(directory, "settings.json");
                var fresh = Program.Load(path);
                Assert(fresh.ScaleWindowSizeToMonitor && !File.Exists(path),
                    "Missing settings use relative sizing without creating a file");
                Program.Save(fresh, path);
                Assert(Program.Load(path).ScaleWindowSizeToMonitor,
                    "The fresh relative sizing default persists through save and reload");
                string missing = "{\"HiddenIdentities\":[\"side-id\"],\"MoveWindowsToClickedTaskbar\":true,\"UndoMoveShortcut\":393224,\"WindowShelfHoverDelayMs\":645}";
                File.WriteAllText(path, missing);
                var legacy = Program.Load(path);
                Assert(legacy.ScaleWindowSizeToMonitor && legacy.MoveWindowsToClickedTaskbar &&
                    legacy.HiddenIdentities.SequenceEqual(new[] { "side-id" }) && legacy.UndoMoveShortcut == 393224 && legacy.WindowShelfHoverDelayMs == 645,
                    "A missing sizing choice receives relative sizing while existing preferences remain intact");
                Assert(File.ReadAllText(path) == missing, "Loading older settings does not rewrite their file");
                foreach (bool choice in new[] { false, true }) {
                    string saved = "{\"ScaleWindowSizeToMonitor\":" + (choice ? "true" : "false") + ",\"WindowShelfHoverDelayMs\":645}";
                    File.WriteAllText(path, saved);
                    var loaded = Program.Load(path);
                    Assert(loaded.ScaleWindowSizeToMonitor == choice && File.ReadAllText(path) == saved,
                        "An explicit saved sizing choice survives loading without rewriting: " + choice);
                    var history = new SettingsHistory(); history.Push(loaded, null, "Window sizing");
                    var copy = loaded.Copy(); copy.ScaleWindowSizeToMonitor = !choice;
                    Assert(history.Peek.Settings.ScaleWindowSizeToMonitor == choice && loaded.ScaleWindowSizeToMonitor == choice,
                        "Changing a sizing copy preserves the original and settings Undo snapshot: " + choice);
                    var unrelated = loaded.Copy(); unrelated.WindowShelfHoverDelayMs = 750;
                    Program.Save(unrelated, path);
                    var reloaded = Program.Load(path);
                    Assert(reloaded.ScaleWindowSizeToMonitor == choice && reloaded.WindowShelfHoverDelayMs == 750,
                        "Unrelated edits, settings copy, save and reload preserve explicit sizing: " + choice);
                    Program.Save(history.Peek.Settings, path);
                    var restored = Program.Load(path);
                    Assert(restored.ScaleWindowSizeToMonitor == choice && restored.WindowShelfHoverDelayMs == 645,
                        "Persisting a settings Undo snapshot restores the saved sizing choice: " + choice);
                }
            } finally {
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
            Console.WriteLine("PASS: " + (count - start) + " window-sizing default and saved-preference checks.");
        }
        static void UndoDefaultSettingsChecks()
        {
            Keys currentDefault = Keys.Control | Keys.Shift | Keys.Back;
            Keys previousDefault = Keys.Control | Keys.Alt | Keys.Back;
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            Assert(GlobalShortcut.Default == currentDefault && (int)currentDefault == 196616,
                "New Undo defaults use Ctrl+Shift+Backspace with the persisted key value");
            Assert(new Settings().UndoMoveShortcut == (int)currentDefault,
                "A new profile receives the new Undo default");
            var missing = serializer.Deserialize<Settings>("{\"WindowShelfHoverDelayMs\":645}");
            Assert(missing.UndoMoveShortcut == (int)currentDefault && missing.WindowShelfHoverDelayMs == 645,
                "Missing Undo in legacy settings receives the new default without changing explicit Ghost Bar timing");
            MonitorShortcutPolicy.Normalize(missing); MonitorShortcutPolicy.Validate(missing);
            Assert(missing.MonitorMoveShortcuts.SequenceEqual(MonitorShortcutPolicy.Defaults()),
                "The new Undo default leaves all four monitor defaults available");
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TaskbarSelect.UndoDefault." + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            try {
                string freshPath = System.IO.Path.Combine(directory, "fresh.json");
                var fresh = Program.Load(freshPath);
                Assert(fresh.UndoMoveShortcut == (int)currentDefault && !System.IO.File.Exists(freshPath),
                    "Loading a missing owned settings file uses the new default without writing settings");
                string missingPath = System.IO.Path.Combine(directory, "missing-undo.json");
                System.IO.File.WriteAllText(missingPath, "{\"WindowShelfHoverDelayMs\":645,\"WindowShelfActivationWidth\":160}");
                var loadedMissing = Program.Load(missingPath);
                Assert(loadedMissing.UndoMoveShortcut == (int)currentDefault && loadedMissing.WindowShelfHoverDelayMs == 645 &&
                    loadedMissing.WindowShelfActivationWidth == 160 && loadedMissing.MonitorMoveShortcuts.SequenceEqual(MonitorShortcutPolicy.Defaults()),
                    "Production settings loading fills only a missing Undo choice and preserves Ghost Bar preferences");
                foreach (Keys savedChoice in new[] { previousDefault, Keys.None, currentDefault, Keys.Control | Keys.Alt | Keys.F20 }) {
                    string path = System.IO.Path.Combine(directory, "saved-" + ((int)savedChoice) + ".json");
                    System.IO.File.WriteAllText(path, "{\"UndoMoveShortcut\":" + (int)savedChoice + ",\"WindowShelfHoverDelayMs\":645}");
                    var stored = Program.Load(path);
                    Assert(stored.UndoMoveShortcut == (int)savedChoice && stored.MonitorMoveShortcuts.SequenceEqual(MonitorShortcutPolicy.Defaults()),
                        "An explicit saved Undo choice survives the changed default: " + GlobalShortcut.Format(savedChoice));
                    var copy = stored.Copy(); copy.WindowShelfHoverDelayMs = 750;
                    MonitorShortcutPolicy.Normalize(copy); MonitorShortcutPolicy.Validate(copy); Program.Save(copy, path);
                    var reloaded = Program.Load(path);
                    Assert(reloaded.UndoMoveShortcut == (int)savedChoice && reloaded.WindowShelfHoverDelayMs == 750 &&
                        reloaded.MonitorMoveShortcuts.SequenceEqual(MonitorShortcutPolicy.Defaults()),
                        "Copy, unrelated changes, persistence and reload retain explicit Undo: " + GlobalShortcut.Format(savedChoice));
                }
            } finally {
                foreach (string file in System.IO.Directory.GetFiles(directory)) System.IO.File.Delete(file);
                System.IO.Directory.Delete(directory);
            }
            var conflict = new Settings(); conflict.MonitorMoveShortcuts[0] = (int)currentDefault;
            var expected = MonitorShortcutPolicy.Defaults(); expected[0] = 0;
            MonitorShortcutPolicy.Normalize(conflict); MonitorShortcutPolicy.Validate(conflict);
            Assert(conflict.UndoMoveShortcut == (int)currentDefault && conflict.MonitorMoveShortcuts.SequenceEqual(expected),
                "A conflicting monitor assignment never rewrites the new Undo default or other monitor bindings");
            var distinctLegacy = new Settings { UndoMoveShortcut = (int)previousDefault };
            distinctLegacy.MonitorMoveShortcuts[0] = (int)currentDefault;
            MonitorShortcutPolicy.Normalize(distinctLegacy); MonitorShortcutPolicy.Validate(distinctLegacy);
            Assert(distinctLegacy.UndoMoveShortcut == (int)previousDefault && distinctLegacy.MonitorMoveShortcuts[0] == (int)currentDefault,
                "A retained old Undo choice allows the new default chord to remain explicitly assigned elsewhere");
        }
        static void ShortcutChecks()
        {
            UndoDefaultSettingsChecks();
            Assert(GlobalShortcut.IsValid(GlobalShortcut.Default) && GlobalShortcut.IsValid(Keys.None), "Default shortcut and disabled state are valid");
            Assert(!GlobalShortcut.IsValid(Keys.Z) && !GlobalShortcut.IsValid(Keys.Control | Keys.Z), "Global shortcut avoids unmodified and single-modifier typing keys");
            Assert(!GlobalShortcut.IsValid(Keys.Control | Keys.Alt | Keys.F12), "Debugger-reserved F12 is rejected");
            Assert(!GlobalShortcut.IsValid(Keys.Control | Keys.Alt | Keys.LWin), "Windows keys cannot be registered");
            Assert(GlobalShortcut.Format(GlobalShortcut.Default) == "Ctrl + Shift + Backspace", "Default shortcut has a readable label");
            Assert(GlobalShortcut.Format(Keys.Control | Keys.Shift | Keys.D1) == "Ctrl + Shift + 1", "Numeric shortcut labels omit enum prefixes");
            var data = new IntPtr(((int)Keys.Back << 16) | (int)GlobalShortcut.Modifiers(GlobalShortcut.Default));
            Assert(GlobalShortcut.MatchesMessage(3, GlobalShortcut.Default, new IntPtr(3), data), "Registered hotkey messages match identity and chord");
            Assert(!GlobalShortcut.MatchesMessage(4, GlobalShortcut.Default, new IntPtr(3), data), "Queued messages from an old registration are ignored");
            Assert(!GlobalShortcut.MatchesMessage(3, Keys.Control | Keys.Alt | Keys.Back, new IntPtr(3), data), "Hotkey messages with different modifiers are ignored");
            Assert(!GlobalShortcut.MatchesMessage(0, Keys.None, IntPtr.Zero, IntPtr.Zero), "Disabled shortcut never dispatches");
            using (var first = new GlobalShortcut())
            using (var second = new GlobalShortcut()) {
                Keys firstKey = Keys.None, secondKey = Keys.None;
                for (Keys key = Keys.F13; key <= Keys.F24 && secondKey == Keys.None; key++) {
                    Keys choice = Keys.Control | Keys.Alt | Keys.Shift | key;
                    try {
                        if (firstKey == Keys.None) { first.Apply(choice, delegate { }); firstKey = choice; }
                        else { second.Apply(choice, delegate { }); secondKey = choice; }
                    } catch (InvalidOperationException) { }
                }
                Assert(firstKey != Keys.None && secondKey != Keys.None, "Two unused test shortcuts register on message-only windows");
                bool committed = false, rejected = false;
                try { first.Apply(secondKey, delegate { committed = true; }); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && !committed && first.Current == firstKey, "A conflicting shortcut preserves the old binding and never commits settings");
                second.Apply(Keys.None, delegate { });
                rejected = false;
                try { first.Apply(secondKey, delegate { throw new System.IO.IOException("Simulated save failure"); }); } catch (System.IO.IOException) { rejected = true; }
                Assert(rejected && first.Current == firstKey, "Save failure keeps the previous shortcut active");
                second.Apply(secondKey, delegate { });
                Assert(second.Current == secondKey, "Save failure releases the tentative new shortcut");
                rejected = false;
                try { first.Apply(Keys.None, delegate { throw new System.IO.IOException("Simulated save failure"); }); } catch (System.IO.IOException) { rejected = true; }
                Assert(rejected && first.Current == firstKey, "Failed disable preserves the current shortcut");
                second.Apply(Keys.None, delegate { });
                first.Apply(secondKey, delegate { committed = true; });
                second.Apply(firstKey, delegate { });
                Assert(committed && first.Current == secondKey && second.Current == firstKey, "A successful rebind releases the old shortcut");
                bool defaultAvailable = true;
                bool defaultCommitted = false;
                rejected = false;
                try { first.Apply(GlobalShortcut.Default, delegate { defaultCommitted = true; throw new System.IO.IOException("Simulated new-default save failure"); }); }
                catch (System.IO.IOException) { rejected = true; }
                catch (InvalidOperationException error) {
                    if (error.Message != "Ctrl + Shift + Backspace is already registered by another app.") throw;
                    defaultAvailable = false;
                    Assert(!defaultCommitted && first.Current == secondKey, "An externally owned Undo default does not commit and preserves the previous registered chord");
                    Console.WriteLine("SKIP: native Ctrl+Shift+Backspace save/rebind checks: another app owns that chord. Unused-chord conflict and save-rollback checks passed.");
                }
                if (defaultAvailable) {
                    Assert(rejected && defaultCommitted && first.Current == secondKey, "A failed save of the new Undo default retains the previous registered chord");
                    second.Apply(Keys.None, delegate { });
                    second.Apply(GlobalShortcut.Default, delegate { });
                    Assert(second.Current == GlobalShortcut.Default, "A failed save releases the tentative new Undo default registration");
                    committed = false; rejected = false;
                    try { first.Apply(GlobalShortcut.Default, delegate { committed = true; }); }
                    catch (InvalidOperationException error) { rejected = error.Message == "Ctrl + Shift + Backspace is already registered by another app."; }
                    Assert(rejected && !committed && first.Current == secondKey,
                        "An unavailable new Undo default reports Windows hotkey conflict without committing or replacing the old chord");
                    second.Apply(Keys.None, delegate { });
                    first.Apply(GlobalShortcut.Default, delegate { committed = true; });
                    second.Apply(secondKey, delegate { });
                    Assert(committed && first.Current == GlobalShortcut.Default && second.Current == secondKey,
                        "A successful new-default rebind commits and releases the previous native chord");
                }
                first.Dispose();
                Keys released = defaultAvailable ? GlobalShortcut.Default : secondKey;
                second.Apply(released, delegate { });
                Assert(second.Current == released, "Disposing a shortcut releases its native registration");
            }
        }
        internal static int RunStartupIntegration()
        {
            if (Startup.Read() != null) { Console.WriteLine("SKIP: an existing startup preference must not be changed by this test."); return 1; }
            try {
                Startup.SetEnabled(true);
                Assert(Startup.Enabled && (string)Startup.Read() == Startup.CommandFor(Application.ExecutablePath), "Startup shortcut points to this executable in quiet mode");
                Startup.SetEnabled(false);
                Assert(Startup.Read() == null, "Disabling removes only this utility's startup entry");
                Console.WriteLine("PASS: startup enable/disable integration. Startup was left disabled.");
                return 0;
            } catch (Exception error) { Console.WriteLine("FAIL: " + error.Message); return 1; }
            finally { if (Startup.IsOwned(Startup.Read())) Startup.SetEnabled(false); }
        }
        internal static int RunFocusTargets()
        {
            try {
                FocusIndicatorTests.RunTargets(Assert);
                Console.WriteLine("PASS: " + count + " managed focus target checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunFocusReveal()
        {
            try {
                FocusIndicatorMotionTests.Run(Assert);
                FocusIndicatorRevealPaintTests.Run(Assert);
                FocusIndicatorPaintTests.Run(Assert);
                FocusFrameFinishTests.Run(Assert);
                FocusIndicatorGlowTests.Run(Assert);
                FocusLightingPolicyTests.Run(Assert);
                FocusIndicatorLightingTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " managed focus reveal checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunAdministrator()
        {
            try {
                AdministratorLaunchTests.Run(Assert);
                NativeProcessIdentityTests.Run(Assert);
                AdministratorSupportTests.Run(Assert);
                AdministratorPermissionSwitchTests.Run(Assert);
                AdministratorCancellationTests.Run(Assert);
                AdministratorReconnectRecoveryTests.Run(Assert);
                AdministratorPreferenceIdentityTests.Run(Assert);
                MovementValidityDiagnosticTests.Run(Assert);
                PersistentAdministratorTests.Run(Assert);
                ApplicationLifecycleTests.Run(Assert);
                ScheduledBootstrapImageTests.Run(Assert);
                NativeStopDiagnosticsTests.Run(Assert);
                AdministratorUiTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " administrator support checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunAdministratorUi()
        {
            try {
                AdministratorPermissionSwitchTests.Run(Assert);
                AdministratorPreferenceIdentityTests.Run(Assert);
                AdministratorUiTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " hidden administrator UI checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunHookLifetime()
        {
            try {
                DesktopHookLifetimeTests.Run(Assert);
                HookDeliveryWatchTests.Run(Assert);
                MouseHookPacketPolicyTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " pure hook lifetime and delivery checks (no desktop hooks or input)"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunLifecycle()
        {
            try {
                ApplicationLifecycleTests.Run(Assert);
                PersistentAdministratorTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " isolated lifecycle checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunTaskbarTargets()
        {
            try {
                TaskbarHideRequestsTests.Run(Assert);
                HoverQueryPolicyTests.Run(Assert);
                HoverQueryIntegrationTests.Run(Assert);
                TaskbarTargetRecoveryTests.Run(Assert);
                TaskbarButtonSelectionTests.Run(Assert);
                TaskbarNativeSelectionTests.Run(Assert);
                NativeTaskbarHandoffTests.Run(Assert);
                TaskManagerShellIdentityTests.Run(Assert);
                TaskbarRoutingDiagnosticTests.Run(Assert);
                DeferredTaskbarClickTests.Run(Assert);
                ManagementConsoleShellIdentityTests.Run(Assert);
                SettingsTaskbarIdentityTests.Run(Assert);
                Assert(TaskbarNativeSelection.IsPhysicalPointer(0) && !TaskbarNativeSelection.IsPhysicalPointer(1) &&
                    !TaskbarNativeSelection.IsPhysicalPointer(2) && !TaskbarNativeSelection.IsPhysicalPointer(3),
                    "Weak Task Manager selection proof requires a non-injected low-level pointer gesture");
                Console.WriteLine("PASS: " + count + " isolated taskbar target checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunDeferredController()
        {
            try {
                DeferredTaskbarClickTests.Run(Assert);
                OrdinaryDeferredClickIntegrationTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " deferred taskbar controller checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunSettingsTaskbar()
        {
            try {
                SettingsTaskbarIdentityTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " hidden settings taskbar identity checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunAppearance()
        {
            try {
                AccentIconTests.Run(Assert);
                FocusAppearanceSettingsTests.Run(Assert);
                FocusIndicatorTests.Run(Assert);
                FocusIndicatorAlphaTests.Run(Assert);
                FocusIndicatorPaintTests.Run(Assert);
                FocusFrameFinishTests.Run(Assert);
                FocusFrameFinishNativeTests.Run(Assert);
                FocusIndicatorMotionTests.Run(Assert);
                FocusIndicatorRevealPaintTests.Run(Assert);
                FocusIndicatorMotionIntegrationTests.Run(Assert);
                FocusIndicatorGlowTests.Run(Assert);
                FocusIndicatorGlowIntegrationTests.Run(Assert);
                FocusLightingPolicyTests.Run(Assert);
                FocusIndicatorLightingTests.Run(Assert);
                FocusIndicatorLightingIntegrationTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " focus appearance checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunUpdates()
        {
            try {
                AppUpdateTests.Run(Assert);
                AppUpdateInstallerTests.Run(Assert);
                AppUpdateUiTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " guided update checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunAppPolish()
        {
            try {
                AccentIconTests.Run(Assert);
                AppPolishTests.Run(Assert);
                UiPolishChecks();
                FeatureFixTests.Run(Assert);
                MonitorShortcutReconnectChecks();
                FocusAppearanceSettingsTests.Run(Assert);
                FocusFrameFinishTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " app polish checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunMonitorShortcuts()
        {
            try {
                KeyboardShortcutPolicyTests.AdditionalMonitorBindings(Assert);
                FeatureFixTests.Run(Assert);
                MonitorShortcutReconnectChecks();
                Console.WriteLine("PASS: " + count + " connected monitor shortcut checks");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        static void MonitorShortcutReconnectChecks()
        {
            const Keys chord = Keys.Control | Keys.Alt | Keys.Shift | Keys.F21;
            var settings = new Settings {
                UndoMoveShortcut = 0, MonitorMoveShortcuts = new int[4], FocusWindowShortcuts = new int[4],
                CloseWindowShortcut = 0, SearchAppsShortcut = 0, MinimizeWindowShortcut = 0,
                PinWindowShortcut = 0, TileWindowShortcut = 0
            };
            var display = new Display { Device = @"\\.\DISPLAY6", Width = 1920, Height = 1080 };
            MonitorShortcutPolicy.SetChoice(settings, 6, (int)chord);
            var controller = new Controller(false) { Settings = settings };
            try {
                controller.InitializeShortcut();
                controller.Displays.Add(display);
                controller.SyncMonitorShortcuts();
                Assert(controller.MonitorShortcutStatus(6) == "Shortcut active", "A connected extra display registers its saved shortcut");
                controller.Displays.Clear();
                controller.SyncMonitorShortcuts();
                Assert(controller.MonitorShortcutStatus(6) == "Shortcut off" && MonitorShortcutPolicy.Choice(settings, 6) == (int)chord,
                    "Unplugging removes the registration and keeps the saved shortcut");
                using (var other = new GlobalShortcut()) {
                    other.Apply(chord, delegate { });
                    controller.Displays.Add(display);
                    controller.SyncMonitorShortcuts();
                    Assert(controller.MonitorShortcutStatus(6).Contains("already registered") && MonitorShortcutPolicy.Choice(settings, 6) == (int)chord,
                        "A reconnect conflict reports the registration error and keeps the binding");
                }
                controller.Displays.Clear(); controller.SyncMonitorShortcuts();
                controller.Displays.Add(display); controller.SyncMonitorShortcuts();
                Assert(controller.MonitorShortcutStatus(6) == "Shortcut active", "Reconnecting the same display number restores the binding");
            } finally {
                controller.Displays.Clear(); controller.SyncMonitorShortcuts();
                controller.Mover.Dispose(); controller.UndoShortcut.Dispose();
                foreach (var binding in controller.MonitorShortcuts.Concat(controller.ActionShortcuts).Concat(controller.NavigationShortcuts)) binding.Dispose();
            }
        }
        internal static int RunNavigation()
        {
            try {
                DirectionalFocusTests.Run(Assert);
                NavigationShortcutPolicyTests.Run(Assert);
                TilingPickerTests.Run(Assert);
                WindowTilingTests.Run(Assert);
                MinimumWorkerTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " focus and tiling checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int RunWindowStates()
        {
            try {
                WindowStateActionsTests.Run(Assert);
                WindowStateHelperTests.Run(Assert);
                AdministratorPinRecoveryTests.Run(Assert);
                MinimizeUndoTests.Run(Assert);
                KeyboardShortcutPolicyTests.RunWindowStateBindings(Assert);
                Console.WriteLine("PASS: " + count + " minimize and always-on-top checks"); return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        internal static int Run()
        {
            try {
                Assert(RunWakePolicies() == 0, "Package aggregate includes wake and work-area policies");
                Assert(OwnedSnapPrototypeTests.Run() == 0, "Package aggregate includes owned snap routing and geometry policies");
                Assert(AdministratorSnapProtocolTests.Run() == 0 && AdministratorSnapInputTests.Run() == 0,
                    "Package aggregate includes narrow administrator snap leases and notice lifecycle");
                PinIndicatorChecks();
                WindowStateActionsTests.Run(Assert);
                WindowStateHelperTests.Run(Assert);
                AdministratorPinRecoveryTests.Run(Assert);
                MinimizeUndoTests.Run(Assert);
                AppUpdateTests.Run(Assert);
                AppUpdateInstallerTests.Run(Assert);
                DesktopHookLifetimeTests.Run(Assert);
                AppUpdateUiTests.Run(Assert);
                HookDeliveryWatchTests.Run(Assert);
                MouseHookPacketPolicyTests.Run(Assert);
                AdministratorLaunchTests.Run(Assert);
                NativeProcessIdentityTests.Run(Assert);
                AdministratorSupportTests.Run(Assert);
                AdministratorPermissionSwitchTests.Run(Assert);
                AdministratorCancellationTests.Run(Assert);
                AdministratorReconnectRecoveryTests.Run(Assert);
                AdministratorPreferenceIdentityTests.Run(Assert);
                MovementValidityDiagnosticTests.Run(Assert);
                PersistentAdministratorTests.Run(Assert);
                ApplicationLifecycleTests.Run(Assert);
                ScheduledBootstrapImageTests.Run(Assert);
                NativeStopDiagnosticsTests.Run(Assert);
                AdministratorUiTests.Run(Assert);
                WindowSizingDefaultSettingsChecks();
                TaskbarHideRequestsTests.Run(Assert);
                HoverQueryPolicyTests.Run(Assert);
                HoverQueryIntegrationTests.Run(Assert);
                TaskbarTargetRecoveryTests.Run(Assert);
                TaskbarButtonSelectionTests.Run(Assert);
                TaskbarNativeSelectionTests.Run(Assert);
                NativeTaskbarHandoffTests.Run(Assert);
                TaskManagerShellIdentityTests.Run(Assert);
                TaskbarRoutingDiagnosticTests.Run(Assert);
                DeferredTaskbarClickTests.Run(Assert);
                ManagementConsoleShellIdentityTests.Run(Assert);
                SettingsTaskbarIdentityTests.Run(Assert);
                Assert(TaskbarNativeSelection.IsPhysicalPointer(0) && !TaskbarNativeSelection.IsPhysicalPointer(1) &&
                    !TaskbarNativeSelection.IsPhysicalPointer(2) && !TaskbarNativeSelection.IsPhysicalPointer(3),
                    "Weak Task Manager selection proof requires a non-injected low-level pointer gesture");
                WindowMoveCoordinatorTests.Run(Assert);
                RunWindowPlacementCalculations();
                TaskbarActivationChecks();
                OrdinaryDeferredClickIntegrationTests.Run(Assert);
                SnapWorkAreaTests.Run(Assert);
                WindowPlacementNativeChecks();
                TaskbarUiChecks();
                WindowShelfSettingsChecks();
                WindowShelfTests.Run(Assert);
                GhostAppCatalogTests.Run(Assert);
                GhostLaunchCoordinatorTests.Run(Assert);
                GhostSearchPopupTests.Run(Assert);
                GhostSearchIntegrationTests.Run(Assert);
                GhostSearchCancellationTests.Run(Assert);
                KeyboardShortcutPolicyTests.Run(Assert);
                FocusedWindowActionsTests.Run(Assert);
                SearchPointerInputTests.Run(Assert);
                DirectionalFocusTests.Run(Assert);
                NavigationShortcutPolicyTests.Run(Assert);
                TilingPickerTests.Run(Assert);
                WindowTilingTests.Run(Assert);
                MinimumWorkerTests.Run(Assert);
                Beta7FullscreenTests.RunFeatureChecks(Assert);
                AccentIconTests.Run(Assert);
                FocusAppearanceSettingsTests.Run(Assert);
                FocusIndicatorTests.Run(Assert);
                FocusIndicatorAlphaTests.Run(Assert);
                FocusIndicatorPaintTests.Run(Assert);
                FocusFrameFinishTests.Run(Assert);
                FocusFrameFinishNativeTests.Run(Assert);
                FocusIndicatorMotionTests.Run(Assert);
                FocusIndicatorRevealPaintTests.Run(Assert);
                FocusIndicatorMotionIntegrationTests.Run(Assert);
                FocusIndicatorGlowTests.Run(Assert);
                FocusIndicatorGlowIntegrationTests.Run(Assert);
                FocusLightingPolicyTests.Run(Assert);
                FocusIndicatorLightingTests.Run(Assert);
                FocusIndicatorLightingIntegrationTests.Run(Assert);
                ProfileFixTests.Run(Assert);
                FeatureFixTests.Run(Assert);
                MonitorShortcutReconnectChecks();
                AppPolishTests.Run(Assert);
                MovementFixTests.RunManaged(Assert);
                MovementFixTests.RunOwned(Assert);
                GhostReturnTests.Run(Assert);
                SteamIdentityTests.Run(Assert);
                CaptionlessMaximizedTests.Run(Assert);
                ActivationPreviewTests.Run(Assert);
                ShelfPreviewTests.Run(Assert);
                ShelfPeekChecks();
                WindowShelfMoveChecks();
                WindowShelfLocalActionChecks();
                WindowHistoryStateChecks();
                WindowShelfRestoreChecks();
                AppActionsLayoutChecks();
                UiPolishChecks();
                ShortcutChecks();
                ReviewPolicyChecks();
                PreviewTreeChecks();
                NativePreviewChecks();
                var clickTime = DateTime.UtcNow;
                Assert(!TaskbarMover.ClickHasSettled(DateTime.MinValue, clickTime), "Held taskbar clicks cannot move windows before Explorer handles release");
                Assert(TaskbarMover.ClickHasSettled(clickTime, clickTime.AddMilliseconds(50)), "Released taskbar clicks do not add an artificial delay");
                Assert(TaskbarMover.ClickHasSettled(clickTime, clickTime.AddMilliseconds(200)), "Completed taskbar clicks can move windows");
                var firstMonitor = new IntPtr(1); var secondMonitor = new IntPtr(2);
                Assert(TaskbarMover.DirectMoveAllowed(true, false, firstMonitor, secondMonitor, false), "A resolved cross-monitor click uses direct movement");
                Assert(TaskbarMover.DirectMoveAllowed(true, false, secondMonitor, firstMonitor, false), "Immediate return clicks can move back without a cooldown");
                Assert(!TaskbarMover.DirectMoveAllowed(true, false, firstMonitor, firstMonitor, false), "Same-monitor clicks remain native so they can minimize");
                Assert(!TaskbarMover.DirectMoveAllowed(true, true, firstMonitor, secondMonitor, false), "Grouped taskbar buttons remain native for preview selection");
                Assert(!TaskbarMover.DirectMoveAllowed(false, false, firstMonitor, secondMonitor, false), "Unresolved clicks cannot be intercepted");
                Assert(!TaskbarMover.DirectMoveAllowed(true, false, firstMonitor, secondMonitor, true), "Fullscreen windows cannot be intercepted");
                var display = new Display { Device = "display3", Identity = "physical-C" };
                var screens = new List<Display> { display };
                var bar = new Bar { Device = "display3" };
                var settings = new Settings { HiddenIdentities = new List<string> { "physical-C" } };
                Assert(Policy.Hide(bar, screens, settings), "Selected secondary is hidden");
                Assert(!Policy.Hide(bar, screens, new Settings()), "First-run settings leave every taskbar visible");
                Assert(Controller.AllConfiguredDisplaysAvailable(settings, screens), "Display discovery recognizes every configured monitor");
                Assert(!Controller.AllConfiguredDisplaysAvailable(settings, new List<Display>()), "Display discovery retries while a configured monitor is missing");
                Assert(!new Settings().MoveWindowsToClickedTaskbar, "Experimental taskbar movement defaults off");
                Assert(new Settings().ScaleWindowSizeToMonitor, "Window sizing defaults to relative size and position");
                var legacy = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Settings>("{\"HiddenIdentities\":[\"display\"],\"MoveWindowsToClickedTaskbar\":true}");
                Assert(legacy.ExcludedApps != null && legacy.MonitorAliases != null && legacy.MoveWindowsToClickedTaskbar,
                    "Existing settings retain movement and receive empty aliases and exclusions");
                Assert(legacy.ScaleWindowSizeToMonitor, "Existing settings without a sizing choice receive relative size and position");
                legacy.ScaleWindowSizeToMonitor = true;
                Assert(legacy.Copy().ScaleWindowSizeToMonitor &&
                    new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Settings>(
                        new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(legacy)).ScaleWindowSizeToMonitor,
                    "Relative sizing survives settings copy and serialization");
                Assert(legacy.UndoMoveShortcut == (int)GlobalShortcut.Default, "Older settings receive the default Undo move shortcut");
                var disabledShortcut = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Settings>("{\"UndoMoveShortcut\":0}");
                Assert(disabledShortcut.Copy().UndoMoveShortcut == 0, "Disabled shortcut persists through serialization and settings snapshots");
                var history = new SettingsHistory();
                history.Push(legacy, null, "Test change");
                legacy.HiddenIdentities.Clear(); legacy.MonitorAliases["display"] = "Side";
                Assert(history.Peek.Settings.HiddenIdentities.Count == 1 && history.Peek.Settings.MonitorAliases.Count == 0,
                    "Settings undo snapshot is independent of future edits");
                var copy = legacy.Copy(); copy.ExcludedApps.Add(new AppRule { Key = "app:Example!App", Name = "Example" });
                Assert(legacy.ExcludedApps.Count == 0, "Editing exclusions does not mutate the previous settings");
                Assert(AppCatalog.Matches(copy.ExcludedApps, "APP:example!app"), "App exclusions match stable identity case-insensitively");
                Assert(!AppCatalog.Matches(copy.ExcludedApps, "app:Another!App"), "An exclusion never matches a different app by name");
                Assert(AppCatalog.IdentityKey("Example!App", @"C:\NewVersion\app.exe") == AppCatalog.IdentityKey("Example!App", @"C:\OldVersion\app.exe"),
                    "Packaged app exclusions survive installation path changes");
                Assert(AppCatalog.IdentityKey("", @"C:\A\app.exe") != AppCatalog.IdentityKey("", @"C:\B\app.exe"),
                    "Desktop applications with the same executable name remain distinct");
                var mapDisplays = new List<Display> {
                    new Display { X = -1920, Y = 300, Width = 1920, Height = 1080 },
                    new Display { X = 0, Y = 0, Width = 2560, Height = 1440 },
                    new Display { X = 0, Y = 1440, Width = 1024, Height = 600 }
                };
                foreach (var screen in mapDisplays) {
                    var tile = DisplayMap.MapBounds(screen, mapDisplays, new System.Drawing.Size(600, 220));
                    Assert(tile.Width > 0 && tile.Height > 0 && tile.Left >= 0 && tile.Top >= 0 && tile.Right <= 600 && tile.Bottom <= 220,
                        "Monitor map handles negative coordinates and fits its viewport");
                }
                Assert(!TaskbarMover.IsGroupedButtonName("Google Chrome - 1 running window pinned"), "Single taskbar window is not grouped");
                Assert(TaskbarMover.IsGroupedButtonName("Google Chrome - 2 running windows pinned"), "Multiple taskbar windows are grouped");
                Assert(TaskbarMover.IsGroupedButtonName("ChatGPT - 1 running window and 1 group"), "Taskbar window plus app group is grouped");
                Assert(TaskbarMover.ButtonAppName("UGREEN NAS - 1 running window pinned") == "UGREEN NAS", "Extracts desktop app name from taskbar status");
                Assert(TaskbarMover.ButtonAppName("ChatGPT - 1 running window and 1 group") == "ChatGPT", "Extracts packaged app name from taskbar status");
                Assert(TaskbarMover.ButtonAppName("Spotify pinned") == "Spotify pinned", "Leaves a taskbar name without running status intact");
                bar.Primary = true;
                Assert(!Policy.Hide(bar, screens, settings), "Primary taskbar is protected");
                bar.Primary = false; display.Primary = true;
                Assert(!Policy.Hide(bar, screens, settings), "Primary monitor is protected");
                display.Primary = false;
                display.Device = "display8"; bar.Device = "display8";
                Assert(Policy.Hide(bar, screens, settings), "Selection survives device-number change");
                display.Identity = "replacement-monitor";
                Assert(!Policy.Hide(bar, screens, settings), "Replacement monitor defaults visible");
                display.Identity = "";
                Assert(!Policy.Hide(bar, screens, settings), "Missing identity defaults visible");
                display.Identity = "physical-C";
                screens.Add(new Display { Device = "other", Identity = "physical-C" });
                Assert(!Policy.Hide(bar, screens, settings), "Ambiguous identity defaults visible");
                screens.Clear();
                Assert(!Policy.Hide(bar, screens, settings), "Disconnected display defaults visible");
                Assert(!Native.Hide(new Bar { Primary = true }), "Native hide refuses primary");
                Assert(!Native.Hide(new Bar { Visible = false, Managed = false }), "Does not claim someone else's hidden window");
                Assert(!Native.IsSecondaryTaskbar(new Bar()), "Invalid handle cannot be managed");
                Assert(MonitorNames.NormalizeWindowsIdentity(@"\\?\DISPLAY#ACR0746#5&sample&UID1#{guid}") == @"DISPLAY\ACR0746\5&SAMPLE&UID1", "Normalizes display identity for EDID matching");
                Assert(MonitorNames.NormalizeWindowsIdentity(@"DISPLAY\SAM707D\sample_0") == @"DISPLAY\SAM707D\SAMPLE", "Normalizes WMI monitor suffix");
                Assert(MonitorNames.Decode(new ushort[] { 88, 66, 50, 55, 51, 0, 70 }) == "XB273", "Decodes WMI monitor text");
                Assert(MonitorNames.Format("ACR", "0746", "XB273 GX") == "Acer XB273 GX", "Formats manufacturer and model name");
                Assert(MonitorNames.Format("ADA", "0004", "") == "ADA 0004", "Falls back to EDID manufacturer and product");
                string startupCommand = Startup.CommandFor(@"C:\Folder With Spaces\TaskbarSelect.exe");
                Assert(startupCommand == "\"C:\\Folder With Spaces\\TaskbarSelect.exe\" --startup", "Startup path is quoted");
                Assert(Startup.IsOwned(startupCommand), "Recognizes owned startup command");
                Assert(Startup.IsOwnedTarget(@"C:\Folder With Spaces\TaskbarSelect.exe", "--startup"), "Recognizes an owned startup shortcut target");
                Assert(!Startup.IsOwnedTarget(@"C:\Folder With Spaces\TaskbarSelect.exe", ""), "Startup shortcut requires quiet mode");
                Assert(!Startup.IsOwned("\"C:\\Other.exe\" --startup"), "Does not own another app's startup entry");
                Assert(!Startup.IsOwned(startupCommand + " --unexpected"), "Does not own an unexpected command");
                Assert(!Startup.IsOwned(null), "Missing startup entry is disabled");
                bool rejected = false;
                try { Startup.CommandFor("TaskbarSelect.exe"); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "Rejects a relative startup path");
                foreach (int size in new[] { 16, 20, 24, 32, 40, 48, 64, 128 }) {
                    using (var icon = Branding.LoadIcon(size))
                    using (var bitmap = icon.ToBitmap()) {
                        Assert(icon.Width == size && bitmap.GetPixel(size / 2, size / 2).A > 0, "Embedded icon renders at " + size);
                        Color edge = bitmap.GetPixel(size * 32 / 64, size * 35 / 64);
                        Assert(edge.A > 200 && edge.G > edge.R + 40 && bitmap.GetPixel(0, 0).A == 0,
                            "Monitor Check taskbar stripe and transparent corners survive at " + size);
                    }
                }
                var assembly = typeof(Tests).Assembly;
                // System.Drawing selects the 128 px frame for a 256 px request; inspect all ICO entries directly.
                using (var stream = assembly.GetManifestResourceStream("TaskbarSelect.Icon"))
                using (var reader = new BinaryReader(stream)) {
                    Assert(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 9,
                        "Embedded ICO directory contains nine frames");
                    int frame = 0;
                    foreach (int size in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }) {
                        stream.Position = 6 + frame++ * 16;
                        int width = reader.ReadByte(), height = reader.ReadByte();
                        Assert((width == 0 ? 256 : width) == size && (height == 0 ? 256 : height) == size,
                            "Embedded ICO directory dimensions at " + size);
                        stream.Position += 10;
                        uint frameOffset = reader.ReadUInt32();
                        int x = size * 32 / 64, y = size * 35 / 64;
                        stream.Position = frameOffset + 40 + ((size - y - 1) * size + x) * 4;
                        reader.ReadByte();
                        int green = reader.ReadByte(), red = reader.ReadByte(), alpha = reader.ReadByte();
                        Assert(alpha > 200 && green > red + 40, "Embedded Monitor Check frame taskbar stripe at " + size);
                    }
                }
                var toolbarNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in new[] { "undo-2", "refresh-cw", "pause", "play", "plus", "keyboard", "info" }) {
                    foreach (string theme in new[] { "dark", "light" }) {
                        string resource = "TaskbarSelect.Toolbar." + name + "-" + theme + ".png";
                        toolbarNames.Add(resource);
                        using (var stream = assembly.GetManifestResourceStream(resource)) {
                            Assert(stream != null, "Toolbar artwork is embedded: " + resource);
                            using (var bitmap = new Bitmap(stream)) {
                                Assert(bitmap.Width == 32 && bitmap.Height == 32, "Toolbar artwork dimensions: " + resource);
                                int visible = 0, clear = 0;
                                for (int y = 0; y < bitmap.Height; y++) {
                                    for (int x = 0; x < bitmap.Width; x++) {
                                        int alpha = bitmap.GetPixel(x, y).A;
                                        if (alpha > 128) visible++;
                                        if (alpha == 0) clear++;
                                    }
                                }
                                Assert(visible > 10 && clear > 10, "Toolbar artwork is nonblank with transparency: " + resource);
                            }
                        }
                    }
                }
                int toolbarCount = 0;
                foreach (string resource in assembly.GetManifestResourceNames()) {
                    if (!resource.StartsWith("TaskbarSelect.Toolbar.", StringComparison.Ordinal)) continue;
                    toolbarCount++;
                    Assert(toolbarNames.Contains(resource), "No retired toolbar artwork is embedded: " + resource);
                }
                Assert(toolbarCount == toolbarNames.Count, "Only the fourteen active theme assets are embedded");
                var monitor = new Native.Rect { Left = 1000, Top = 100, Right = 2920, Bottom = 1180 };
                var work = monitor; work.Left += 60; work.Bottom -= 48;
                var bottom = monitor; bottom.Top = work.Bottom;
                var info = new Native.MonitorInfo { Monitor = monitor, Work = work };
                Native.Rect expanded;
                Assert(AreaPolicy.Expand(info, bottom, out expanded) && expanded.Bottom == monitor.Bottom, "Reclaims bottom taskbar margin");
                Assert(expanded.Left == work.Left, "Preserves unrelated left dock");
                var record = new AreaRecord { Identity = "physical-C", Monitor = monitor, Original = work, Applied = expanded };
                display.Identity = "physical-C"; info.Work = expanded;
                Assert(AreaPolicy.CanRestore(record, display, info), "Restores the exact area owned by this utility");
                info.Work.Right -= 20;
                Assert(!AreaPolicy.CanRestore(record, display, info), "Does not overwrite another app's area change");
                info.Work = expanded; info.Monitor.Bottom += 10;
                Assert(!AreaPolicy.CanRestore(record, display, info), "Does not restore stale monitor geometry");
                info.Monitor = monitor; display.Identity = "replacement";
                Assert(!AreaPolicy.CanRestore(record, display, info), "Does not restore onto a replacement display");
                info.Work = work; info.Flags = 1;
                Assert(!AreaPolicy.Expand(info, bottom, out expanded), "Does not reclaim the primary monitor");
                info.Flags = 0; bottom.Top -= 10;
                Assert(!AreaPolicy.Expand(info, bottom, out expanded), "Rejects a non-matching reserved edge");
                var sourceWork = new Native.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
                var targetWork = new Native.Rect { Left = 1920, Top = 100, Right = 3200, Bottom = 820 };
                var mapped = TaskbarMover.MapToWorkArea(new Native.Rect { Left = 0, Top = 0, Right = 800, Bottom = 600 }, sourceWork, targetWork);
                Assert(mapped.Left == targetWork.Left && mapped.Top == targetWork.Top && mapped.Right - mapped.Left == 800 && mapped.Bottom - mapped.Top == 600,
                    "Window placement preserves top-left position and size");
                mapped = TaskbarMover.MapToWorkArea(new Native.Rect { Left = 1120, Top = 440, Right = 1920, Bottom = 1040 }, sourceWork, targetWork);
                Assert(mapped.Right == targetWork.Right && mapped.Bottom == targetWork.Bottom,
                    "Window placement preserves bottom-right position");
                mapped = TaskbarMover.MapToWorkArea(new Native.Rect { Left = -100, Top = -100, Right = 2500, Bottom = 1300 }, sourceWork, targetWork);
                Assert(mapped.Left == targetWork.Left && mapped.Top == targetWork.Top && mapped.Right == targetWork.Right && mapped.Bottom == targetWork.Bottom,
                    "Oversized windows are constrained to the target work area");
                var largeWork = new Native.Rect { Left = 0, Top = 0, Right = 2560, Bottom = 1392 };
                var smallWork = new Native.Rect { Left = 2560, Top = -100, Right = 4480, Bottom = 932 };
                var centered = new Native.Rect { Left = 800, Top = 348, Right = 1760, Bottom = 1044 };
                var relative = TaskbarMover.MapRelativeToWorkArea(centered, largeWork, smallWork);
                Assert(relative.Left == 3160 && relative.Top == 158 && relative.Right == 3880 && relative.Bottom == 674,
                    "Relative mapping scales and centers a window from 2560x1392 to 1920x1032 usable area");
                Assert(AreaPolicy.Equal(TaskbarMover.MapRelativeToWorkArea(relative, smallWork, largeWork), centered),
                    "Reverse relative mapping rounds this centered window back to its original bounds");
                var offset = TaskbarMover.MapRelativeToWorkArea(new Native.Rect { Left = 400, Top = 100, Right = 1680, Bottom = 796 }, largeWork, smallWork);
                Assert(offset.Left == 2860 && offset.Right - offset.Left == 960,
                    "Relative position uses original source width, not the scaled width");
                var sameWork = new Native.Rect { Left = -2560, Top = -100, Right = 0, Bottom = 1292 };
                var sameWindow = new Native.Rect { Left = -2200, Top = 100, Right = -1300, Bottom = 700 };
                var same = TaskbarMover.MapRelativeToWorkArea(sameWindow, sameWork, largeWork);
                Assert(same.Left == 360 && same.Top == 200 && same.Right == 1260 && same.Bottom == 800,
                    "Same-size usable areas preserve pixel size and relative placement across negative origins");
                relative = TaskbarMover.MapRelativeToWorkArea(new Native.Rect { Left = -200, Top = -100, Right = 3000, Bottom = 1500 }, largeWork, smallWork);
                Assert(AreaPolicy.Equal(relative, smallWork), "Relative mapping clamps an oversized offscreen window to the target work area");
                relative = TaskbarMover.MapRelativeToWorkArea(new Native.Rect { Left = 0, Top = 0, Right = 0, Bottom = 0 },
                    new Native.Rect(), smallWork);
                Assert(relative.Right > relative.Left && relative.Bottom > relative.Top && relative.Left >= smallWork.Left &&
                    relative.Top >= smallWork.Top && relative.Right <= smallWork.Right && relative.Bottom <= smallWork.Bottom,
                    "Zero-size source and window avoid division by zero and remain on the target");
                relative = TaskbarMover.MapRelativeToWorkArea(new Native.Rect { Left = Int32.MinValue, Top = -100, Right = Int32.MaxValue, Bottom = 100 },
                    new Native.Rect { Left = Int32.MinValue, Top = -100, Right = Int32.MaxValue, Bottom = 100 }, smallWork);
                Assert(AreaPolicy.Equal(relative, smallWork), "Extreme source coordinates avoid integer overflow");
                var roundtrip = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<AreaRecord>(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(record));
                Assert(AreaPolicy.Equal(roundtrip.Original, record.Original), "Recovery journal round trip");
                display.Identity = record.Identity; info.Work = record.Applied;
                var invalid = record.Original;
                invalid.Bottom = monitor.Bottom + 100;
                record.Original = invalid;
                Assert(!AreaPolicy.CanRestore(record, display, info), "Rejects an invalid recovery rectangle");
                // Exercise Win32 hide/restore on our own disposable window, never Explorer.
                using (var window = new Form()) {
                    window.Text = "Taskbar Select test"; window.ShowInTaskbar = false; window.Opacity = 0;
                    window.Show(); Application.DoEvents();
                    TargetCacheChecks(window);
                    PreviewHoverChecks(window);
                    NativePreviewIntegration(window);
                    RecoveryFailureChecks(window);
                    var sample = new Bar { Handle = window.Handle.ToInt64(), Visible = true };
                    Assert(!Native.IsSecondaryTaskbar(sample), "Unrelated window cannot be managed");
                    Assert(Native.Hide(sample), "Win32 hide accepted");
                    Application.DoEvents(); Thread.Sleep(50); Application.DoEvents();
                    Assert(!Native.IsWindowVisible(window.Handle), "Test window hidden");
                    sample.Managed = true;
                    Native.Restore(sample); Application.DoEvents(); Thread.Sleep(50); Application.DoEvents();
                    Assert(Native.IsWindowVisible(window.Handle), "Test window restored");
                    Assert(Native.GetProp(window.Handle, Native.Marker) == IntPtr.Zero, "Recovery marker cleared");
                    var monitors = new List<IntPtr>();
                    Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitorHandle, IntPtr dc, ref Native.Rect bounds, IntPtr data) { monitors.Add(monitorHandle); return true; }, IntPtr.Zero);
                    IntPtr originalMonitor = Native.MonitorFromWindow(window.Handle, 2);
                    IntPtr otherMonitor = monitors.Find(handle => handle != originalMonitor);
                    if (otherMonitor != IntPtr.Zero) {
                        TaskbarTargets.Target freshDirect = null;
                        IntPtr reportedForeground = IntPtr.Zero;
                        int dragReplays = 0;
                        bool replayReleased = false;
                        TaskbarMover.Point replayOrigin = new TaskbarMover.Point(), replayEnd = new TaskbarMover.Point();
                        using (var resolver = new TaskbarTargets(delegate { return freshDirect; }))
                        using (var mover = new TaskbarMover(resolver, delegate(TaskbarMover.Point start, TaskbarMover.Point end, bool released) {
                            dragReplays++; replayOrigin = start; replayEnd = end; replayReleased = released; return true;
                        }, delegate { return reportedForeground; }, false)) {
                            FenceNativeMoverHooks(mover);
                            Native.Rect beforeMove, afterUndo;
                            Native.GetWindowRect(window.Handle, out beforeMove);
                            Assert(mover.MoveToMonitor(window.Handle, otherMonitor), "Disposable window moves across monitors");
                            Assert(Native.MonitorFromWindow(window.Handle, 2) == otherMonitor && mover.CanUndo, "A successful move records undo");
                            Assert(mover.Undo(), "Window movement can be undone");
                            Native.GetWindowRect(window.Handle, out afterUndo);
                            Assert(AreaPolicy.Equal(beforeMove, afterUndo), "Undo restores exact normal window bounds");
                            var sizingSettings = new Settings { MoveWindowsToClickedTaskbar = true, ScaleWindowSizeToMonitor = true };
                            Controller.ConfigureMover(mover, sizingSettings, true, false);
                            Assert(mover.ScaleWindowSizeToMonitor && mover.Enabled == mover.Available,
                                "Controller applies the saved relative-sizing choice to the mover");
                            Assert(mover.MoveToMonitor(window.Handle, otherMonitor) &&
                                Native.MonitorFromWindow(window.Handle, 2) == otherMonitor,
                                "Relative-size normal window reaches the destination monitor");
                            Assert(mover.Undo(), "Relative-size normal window move can be undone");
                            Native.GetWindowRect(window.Handle, out afterUndo);
                            Assert(AreaPolicy.Equal(beforeMove, afterUndo), "Relative-size undo restores the exact original bounds");
                            Controller.ConfigureMover(mover, sizingSettings, false, false);
                            Assert(!mover.Enabled && mover.ScaleWindowSizeToMonitor,
                                "Pausing movement retains the selected sizing mode");
                            sizingSettings.ScaleWindowSizeToMonitor = false;
                            Controller.ConfigureMover(mover, sizingSettings, true, false);
                            Assert(!mover.ScaleWindowSizeToMonitor, "Controller reapplies legacy sizing when the choice is off");
                            for (int move = 0; move < 8; move++) {
                                var destination = move % 2 == 0 ? otherMonitor : originalMonitor;
                                Assert(mover.MoveToMonitor(window.Handle, destination) && window.WindowState != FormWindowState.Minimized &&
                                    Native.MonitorFromWindow(window.Handle, 2) == destination, "Repeated direct moves preserve a visible window on the chosen monitor");
                            }
                            for (int move = 0; move < 8; move++) Assert(mover.Undo(), "Repeated movements retain undo history");
                            ColdCacheForegroundChecks(resolver, item => freshDirect = item, value => reportedForeground = value,
                                mover, window, originalMonitor, otherMonitor);
                            var direct = new TaskbarTargets.Target { Window = window.Handle, AppKey = AppCatalog.ForWindow(window.Handle).Key,
                                Button = new TaskbarMover.Click { AppId = System.Diagnostics.Process.GetCurrentProcess().ProcessName } };
                            freshDirect = direct;
                            Native.GetWindowThreadProcessId(window.Handle, out direct.Process);
                            mover.Enabled = true;
                            Assert(!mover.TryStartDirectMove(direct, originalMonitor), "Direct click leaves same-monitor minimization to Windows");
                            direct.Button.Grouped = true;
                            Assert(!mover.TryStartDirectMove(direct, otherMonitor), "Grouped click is not swallowed");
                            direct.Button.Grouped = false;
                            Assert(mover.TryStartDirectMove(direct, otherMonitor), "Cross-monitor mouse-down can be handled directly");
                            mover.CompleteDirectPress(false); mover.ProcessClick();
                            Assert(Native.MonitorFromWindow(window.Handle, 2) == originalMonitor, "Releasing outside the button cancels a direct move");
                            for (int move = 0; move < 8; move++) {
                                var destination = move % 2 == 0 ? otherMonitor : originalMonitor;
                                Assert(mover.TryStartDirectMove(direct, destination), "Rapid opposite-monitor press is accepted");
                                mover.ProcessClick();
                                Assert(Native.MonitorFromWindow(window.Handle, 2) != destination, "Window does not move until release");
                                mover.CompleteDirectPress(true);
                                WaitFor(delegate { mover.ProcessClick(); return Native.MonitorFromWindow(window.Handle, 2) == destination; });
                                Assert(Native.MonitorFromWindow(window.Handle, 2) == destination && window.WindowState != FormWindowState.Minimized,
                                    "Press and release move directly without minimizing");
                            }
                            for (int move = 0; move < 8; move++) Assert(mover.Undo(), "Direct-click moves retain undo history");
                            Native.Rect directBounds; Native.GetWindowRect(window.Handle, out directBounds);
                            var pressPoint = new TaskbarMover.Point { X = directBounds.Left + 40, Y = directBounds.Top + 40 };
                            direct.Taskbar = window.Handle; direct.TaskbarBounds = directBounds;
                            direct.Button.ButtonBounds = directBounds;
                            direct.Button.Position = new TaskbarMover.Point { X = pressPoint.X + 2, Y = pressPoint.Y + 2 };
                            Assert(mover.TryStartDirectMove(direct, otherMonitor, pressPoint),
                                "Direct press uses its real mouse-down point even when cached hover coordinates differ");
                            var dragFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                            var capturedThreshold = (System.Drawing.Size)typeof(TaskbarMover).GetField("directDragThreshold", dragFlags).GetValue(mover);
                            Assert(capturedThreshold.Width >= 8 && capturedThreshold.Height >= 8 &&
                                !TaskbarInput.IsDirectDrag(pressPoint, new TaskbarMover.Point { X = pressPoint.X + capturedThreshold.Width - 1, Y = pressPoint.Y }, capturedThreshold),
                                "Direct drag threshold is captured once at mouse-down");
                            SyntheticMouse(mover, 0x0202, new TaskbarMover.Point { X = pressPoint.X + 2, Y = pressPoint.Y + 1 });
                            WaitFor(delegate { mover.ProcessClick(); return Native.MonitorFromWindow(window.Handle, 2) == otherMonitor; });
                            Assert(mover.LastStatus == "Moved window directly" && mover.Undo(),
                                "Jittered direct release moves the verified window without drag handoff");
                            Assert(mover.TryStartDirectMove(direct, otherMonitor, pressPoint), "Deliberate held drag starts a direct press");
                            SyntheticMouse(mover, 0x0200, new TaskbarMover.Point { X = pressPoint.X + capturedThreshold.Width, Y = pressPoint.Y });
                            Assert(replayOrigin.X == pressPoint.X && replayOrigin.Y == pressPoint.Y && !replayReleased &&
                                mover.LastStatus == "Used normal Windows icon dragging" && Native.MonitorFromWindow(window.Handle, 2) == originalMonitor,
                                "Held drag hands off at the captured threshold and replays the actual press point");
                            Assert(mover.TryStartDirectMove(direct, otherMonitor, pressPoint), "Release-only deliberate drag starts a direct press");
                            SyntheticMouse(mover, 0x0202, new TaskbarMover.Point { X = pressPoint.X, Y = pressPoint.Y + capturedThreshold.Height });
                            Assert(replayOrigin.X == pressPoint.X && replayOrigin.Y == pressPoint.Y && replayReleased &&
                                mover.LastStatus == "Used normal Windows icon dragging" && Native.MonitorFromWindow(window.Handle, 2) == originalMonitor,
                                "Release-only drag hands off without moving and retains the real press point");
                            direct.Button.ButtonBounds = new Native.Rect { Left = pressPoint.X - 1, Top = pressPoint.Y - 1,
                                Right = pressPoint.X + 2, Bottom = pressPoint.Y + 2 };
                            Assert(mover.TryStartDirectMove(direct, otherMonitor, pressPoint), "Narrow button starts a direct press");
                            SyntheticMouse(mover, 0x0202, new TaskbarMover.Point { X = pressPoint.X + 4, Y = pressPoint.Y });
                            mover.ProcessClick();
                            Assert(Native.MonitorFromWindow(window.Handle, 2) == originalMonitor && !mover.CanUndo,
                                "Release outside the same button never moves, even below drag threshold");
                            direct.Button.ButtonBounds = directBounds;
                            var stalled = new TaskbarTargets.Probe();
                            Assert(mover.TryStartDirectMove(direct, otherMonitor, pressPoint), "Timeout diagnostic starts from a direct press");
                            mover.CompleteDirectPress(true);
                            typeof(TaskbarMover).GetField("directCheck", dragFlags).SetValue(mover, stalled);
                            typeof(TaskbarMover).GetField("directReleasedAt", dragFlags).SetValue(mover, DateTime.UtcNow.AddMilliseconds(-760));
                            mover.ProcessClick();
                            Assert(mover.LastStatus == "Taskbar verification timed out; move skipped" &&
                                Native.MonitorFromWindow(window.Handle, 2) == originalMonitor,
                                "Incomplete direct check reports a timeout instead of claiming the icon changed");
                            Assert(mover.TryStartDirectMove(direct, otherMonitor), "Cached icon can start verification");
                            freshDirect = null;
                            mover.CompleteDirectPress(true);
                            WaitFor(delegate { mover.ProcessClick(); return mover.LastStatus.Contains("Icon changed"); });
                            Assert(!mover.CanUndo && Native.MonitorFromWindow(window.Handle, 2) == originalMonitor,
                                "A failed fresh lookup never moves the stale cached window");
                            freshDirect = new TaskbarTargets.Target { Window = direct.Window, Process = direct.Process,
                                Button = new TaskbarMover.Click { AppId = "Another.Icon" } };
                            Assert(mover.TryStartDirectMove(direct, otherMonitor), "A reused icon location begins with its cached candidate");
                            mover.CompleteDirectPress(true);
                            WaitFor(delegate { mover.ProcessClick(); return mover.LastStatus.Contains("Icon changed"); });
                            Assert(!mover.CanUndo && Native.MonitorFromWindow(window.Handle, 2) == originalMonitor,
                                "A fresh different app at the same location cancels the cached move");
                            freshDirect = direct;
                            Assert(mover.TryStartDirectMove(direct, otherMonitor), "A pending move may be cancelled before release");
                            mover.CancelPending(); mover.CompleteDirectPress(true); mover.ProcessClick();
                            Assert(!mover.CanUndo && Native.MonitorFromWindow(window.Handle, 2) == originalMonitor, "Pausing or undoing during a held press prevents a later move");
                            direct.Button.Position = new TaskbarMover.Point { X = 10, Y = 20 };
                            var dragEnd = new TaskbarMover.Point { X = 60, Y = 20 };
                            int priorDragReplays = dragReplays;
                            Assert(mover.TryStartDirectMove(direct, otherMonitor) && mover.HandOffDrag(dragEnd, false), "Dragging a swallowed press hands control back to Windows");
                            Assert(dragReplays == priorDragReplays + 1 && !replayReleased && replayOrigin.X == 10 && replayEnd.X == 60,
                                "Handoff sends one down at the real press point and preserves a held drag");
                            mover.CompleteDirectPress(true); mover.ProcessClick();
                            Assert(!mover.CanUndo && Native.MonitorFromWindow(window.Handle, 2) == originalMonitor, "Release after a handed-off drag cannot also move an app");
                            Assert(!mover.HandOffDrag(dragEnd, false) && dragReplays == priorDragReplays + 1, "Later drag motion cannot replay another down");
                            Assert(mover.TryStartDirectMove(direct, otherMonitor) && mover.HandOffDrag(dragEnd, true) && replayReleased,
                                "Drag first detected at release restores the complete native gesture");
                            PreviewSafetyChecks(mover, window, otherMonitor);
                            foreach (bool relativeSize in new[] { false, true }) {
                                mover.ScaleWindowSizeToMonitor = relativeSize;
                                string mode = relativeSize ? "relative" : "legacy";
                                window.WindowState = FormWindowState.Maximized; Application.DoEvents();
                                int restoredDuringMove = 0;
                                EventHandler sizeChanged = delegate { if (window.WindowState != FormWindowState.Maximized) restoredDuringMove++; };
                                window.Resize += sizeChanged;
                                Assert(mover.MoveToMonitor(window.Handle, otherMonitor), mode + " maximized move succeeds");
                                Application.DoEvents();
                                window.Resize -= sizeChanged;
                                Assert(window.WindowState == FormWindowState.Maximized && Native.MonitorFromWindow(window.Handle, 2) == otherMonitor &&
                                    restoredDuringMove == 0, mode + " maximized window reaches its destination without an intermediate restore/minimize");
                                Assert(mover.Undo(), mode + " maximized move can be undone");
                                Application.DoEvents();
                                Assert(window.WindowState == FormWindowState.Maximized && Native.MonitorFromWindow(window.Handle, 2) == originalMonitor,
                                    mode + " undo preserves maximized state and original monitor");
                                window.WindowState = FormWindowState.Normal; Application.DoEvents();
                                Native.GetWindowRect(window.Handle, out afterUndo);
                                Assert(AreaPolicy.Equal(beforeMove, afterUndo), mode + " undo preserves maximized restore bounds");
                                window.WindowState = FormWindowState.Minimized; Application.DoEvents();
                                Assert(mover.MoveToMonitor(window.Handle, otherMonitor) && mover.Undo(), mode + " minimized move can be undone");
                                Application.DoEvents();
                                Assert(window.WindowState == FormWindowState.Normal && Native.MonitorFromWindow(window.Handle, 2) == originalMonitor,
                                    mode + " undo returns the minimized window visibly to its original monitor");
                            }
                            var app = AppCatalog.ForWindow(window.Handle);
                            Assert(app != null, "Exclusion picker resolves the test application's identity");
                            mover.ExcludedApps.Add(new AppRule { Key = app.Key, Name = "Test" });
                            Assert(!mover.TryStartDirectMove(direct, otherMonitor), "Excluded apps never have their taskbar clicks intercepted");
                            Assert(!mover.MoveToMonitor(window.Handle, otherMonitor) && Native.MonitorFromWindow(window.Handle, 2) == originalMonitor,
                                "Excluded application stays on its current monitor");
                            Assert(!mover.CanUndo, "Blocked moves do not create undo entries");
                        }
                    }
                    var windowHistory = new WindowHistory();
                    windowHistory.Remember(WindowHistory.Capture(window.Handle));
                    Assert(windowHistory.Available, "Window history captures a live window");
                    window.Close();
                    Assert(!windowHistory.Available && !windowHistory.Undo(), "Undo prunes a closed window without acting on a stale handle");
                }
                // Hiding the active fixture can relinquish this process's native focus
                // permission. Run that sequence after the foreground-dependent checks.
                WindowShelfMinimizeRegistryChecks();
                WindowShelfHiddenMinimizeChecks();
                WindowMoveRecoveryTests.Run(Assert);
                WindowMoveTimeoutTests.Run(Assert);
                Console.WriteLine("PASS: " + count + " checks. No Explorer taskbars changed.");
                return 0;
            } catch (Exception error) { Console.WriteLine("FAIL after " + count + " checks: " + error); return 1; }
        }
    }
}
