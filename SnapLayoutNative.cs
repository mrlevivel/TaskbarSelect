using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSelect
{
    // This adapter is used only by the in-process, normal-exit receipt ledger.
    // No durable claim is made for forced termination or another process.
    internal sealed class SnapLayoutNative : ISnapLayoutApi
    {
        [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern IntPtr GetProperty(IntPtr window, string name);
        [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetProperty(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr RemoveProperty(IntPtr window, string name);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowArranged(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, uint attr, out int value, int size);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        readonly string property = "TaskbarSelect.Layout." + Guid.NewGuid().ToString("N");
        internal string NonceProperty { get { return property; } }
        readonly SnapLayoutProperties properties = new SnapLayoutProperties(GetProperty, SetProperty, RemoveProperty);
        readonly Dictionary<SnapCorrectionTarget, SnapLayoutCandidate> candidates = new Dictionary<SnapCorrectionTarget, SnapLayoutCandidate>();
        readonly Dictionary<SnapCorrectionTarget, long> nonces = new Dictionary<SnapCorrectionTarget, long>();
        HashSet<SnapCorrectionArea> restoreScope;
        internal void BeginRestore(IEnumerable<SnapCorrectionArea> areas)
        { restoreScope = new HashSet<SnapCorrectionArea>(areas); }
        internal void EndRestore() { restoreScope = null; }
        internal bool Track(SnapLayoutCandidate c) { return Track(c, false); }
        internal bool Track(SnapLayoutCandidate c, bool replaceResolved)
        {
            if (c == null || !c.Valid) return false;
            SnapLayoutCandidate old;
            if (candidates.TryGetValue(c.Target, out old) && !replaceResolved) return Same(old, c);
            if (!candidates.ContainsKey(c.Target) && candidates.Count >= SnapLayoutReceiptLedger.MaximumReceipts) return false;
            candidates[c.Target] = new SnapLayoutCandidate { Target = c.Target, Area = c.Area, Before = c.Before,
                BeforeVisible = c.BeforeVisible, Desired = c.Desired, DesiredVisible = c.DesiredVisible, Maximized = c.Maximized };
            if (replaceResolved) nonces.Remove(c.Target);
            return true;
        }
        static bool Same(SnapLayoutCandidate a, SnapLayoutCandidate b)
        { return a.Target.Equals(b.Target) && a.Area.Equals(b.Area) && a.Maximized == b.Maximized && AreaPolicy.Equal(a.Before, b.Before) && AreaPolicy.Equal(a.BeforeVisible, b.BeforeVisible) && AreaPolicy.Equal(a.Desired, b.Desired) && AreaPolicy.Equal(a.DesiredVisible, b.DesiredVisible); }
        static bool Capture(IntPtr window, out SnapCorrectionTarget target)
        {
            target = new SnapCorrectionTarget(); uint process, again;
            if (!Native.IsWindow(window)) return false;
            uint thread = Native.GetWindowThreadProcessId(window, out process);
            if (thread == 0 || process == 0) return false;
            long started = NativeProcessIdentity.Started(process);
            if (started <= 0 || Native.GetWindowThreadProcessId(window, out again) != thread || again != process || NativeProcessIdentity.Started(process) != started) return false;
            target = new SnapCorrectionTarget { Window = window, Process = process, Thread = thread, ProcessStarted = started }; return true;
        }
        public SnapLayoutObservation Read(SnapCorrectionTarget expected)
        {
            var unknown = new SnapLayoutObservation { Kind = SnapLayoutReadKind.Unknown };
            if (!Native.IsWindow(expected.Window)) return new SnapLayoutObservation { Kind = SnapLayoutReadKind.Gone };
            SnapCorrectionTarget target, after;
            if (!Capture(expected.Window, out target)) return unknown;
            if (!target.Equals(expected)) return new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = target };
            Native.Rect outer, visible;
            long nonce = properties.Read(expected.Window, property);
            bool hidden = !Native.IsWindowVisible(expected.Window), iconic = IsIconic(expected.Window), ineligible = false;
            // Positive ineligibility needs no fallible frame/cloak query. It
            // grants only a skip after a confirmed forward, never an inverse.
            if (!hidden && !iconic) {
                var ancestor = GetAncestor(expected.Window, 2);
                if (ancestor == IntPtr.Zero) return unknown;
                ineligible = ancestor != expected.Window || GetWindow(expected.Window, 4) != IntPtr.Zero;
                if (!ineligible) {
                    int cloaked;
                    if (DwmGetWindowAttribute(expected.Window, 14, out cloaked, sizeof(int)) != 0) return unknown;
                    ineligible = cloaked != 0;
                }
            }
            if (hidden || iconic || ineligible) {
                if (!Capture(expected.Window, out after) || !after.Equals(target)) return unknown;
                return new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = target, Nonce = nonce,
                    Hidden = hidden, Iconic = iconic, Ineligible = ineligible };
            }
            if (!Native.GetWindowRect(expected.Window, out outer) ||
                Native.DwmGetWindowAttribute(expected.Window, 9, out visible, Marshal.SizeOf(typeof(Native.Rect))) != 0) return unknown;
            bool max = IsZoomed(expected.Window), arranged;
            try { arranged = IsWindowArranged(expected.Window); } catch (EntryPointNotFoundException) { return unknown; }
            if (!Capture(expected.Window, out after) || !after.Equals(target)) return unknown;
            return new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = target, Nonce = nonce,
                Outer = outer, Visible = visible, Maximized = max, Arranged = arranged, Iconic = iconic };
        }
        public bool SetNonce(SnapCorrectionTarget target, long nonce)
        {
            SnapCorrectionTarget current;
            if (nonce <= 0 || !candidates.ContainsKey(target) || !Capture(target.Window, out current) || !current.Equals(target)) return false;
            // A NULL GetProp is absence, never write authority. A failed SetProp
            // (including UIPI denial) or missing/mismatched readback vetoes forward.
            if (!properties.Mark(target.Window, property, nonce)) return false;
            nonces[target] = nonce;
            return Capture(target.Window, out current) && current.Equals(target) && properties.Read(target.Window, property) == nonce;
        }
        public bool RemoveNonce(SnapCorrectionTarget target, long nonce)
        {
            SnapCorrectionTarget current;
            if (!Capture(target.Window, out current) || !current.Equals(target)) return false;
            return properties.Release(target.Window, property, nonce);
        }
        static bool FreshDisplay(AreaRecord record, out Native.MonitorInfo match)
        {
            Native.MonitorInfo found = new Native.MonitorInfo(); int count = 0; bool readable = true;
            bool enumerated = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Native.Rect bounds, IntPtr data) {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (!Native.GetMonitorInfo(monitor, ref info)) { readable = false; return true; }
                var device = new Native.DisplayDevice { Size = Marshal.SizeOf(typeof(Native.DisplayDevice)) };
                if (!Native.EnumDisplayDevices(info.Device, 0, ref device, 1)) { readable = false; return true; }
                if (String.Equals(device.Identity, record.Identity, StringComparison.OrdinalIgnoreCase)) { count++; found = info; }
                return true;
            }, IntPtr.Zero);
            match = found; return enumerated && readable && count == 1;
        }
        static SnapLayoutAreaState TargetMonitor(SnapCorrectionTarget target, Native.MonitorInfo expected)
        {
            SnapCorrectionTarget current;
            if (!Capture(target.Window, out current) || !current.Equals(target)) return SnapLayoutAreaState.Unknown;
            var actual = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(target.Window, 0), ref actual) ||
                !Capture(target.Window, out current) || !current.Equals(target)) return SnapLayoutAreaState.Unknown;
            return String.Equals(expected.Device, actual.Device, StringComparison.OrdinalIgnoreCase) && AreaPolicy.Equal(expected.Monitor, actual.Monitor) ?
                SnapLayoutAreaState.OriginalReady : SnapLayoutAreaState.Changed;
        }
        // Read-only proof for one foreground intent target, never a work-area
        // write or a scan of tracked application windows.
        internal bool IntentAreaReady(SnapCorrectionTarget target, SnapCorrectionArea area)
        {
            if (area == null || restoreScope != null) return false;
            Native.MonitorInfo info; SnapCorrectionTarget current, shellIdentity, shellAfter;
            var record = area.ToRecord();
            if (!Capture(target.Window, out current) || !current.Equals(target) || !FreshDisplay(record, out info) ||
                (info.Flags & 1) != 0 || !AreaPolicy.Equal(record.Monitor, info.Monitor) || !AreaPolicy.Equal(record.Applied, info.Work) ||
                TargetMonitor(target, info) != SnapLayoutAreaState.OriginalReady) return false;
            IntPtr shell = Native.GetShellWindow();
            if (!Capture(shell, out shellIdentity)) return false;
            var bars = Native.Bars();
            var matching = bars.Where(b => !b.Primary && String.Equals(b.Device, info.Device, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bars.Count(b => b.Primary) != 1 || matching.Length != 1) return false;
            var bar = matching[0]; IntPtr window = new IntPtr(bar.Handle); uint process;
            if (!Native.IsWindow(window) || Native.IsWindowVisible(window) || !Native.IsSecondaryTaskbar(bar) ||
                Native.GetWindowThreadProcessId(window, out process) == 0 || process != shellIdentity.Process ||
                properties.Read(window, Native.Marker) != 1) return false;
            Native.MonitorInfo fresh;
            return Native.GetShellWindow() == shell && Capture(shell, out shellAfter) && shellAfter.Equals(shellIdentity) &&
                FreshDisplay(record, out fresh) && (fresh.Flags & 1) == 0 && String.Equals(info.Device, fresh.Device, StringComparison.OrdinalIgnoreCase) &&
                AreaPolicy.Equal(record.Monitor, fresh.Monitor) && AreaPolicy.Equal(record.Applied, fresh.Work) &&
                TargetMonitor(target, fresh) == SnapLayoutAreaState.OriginalReady;
        }
        internal SnapLayoutAreaState RestoreAreaState(SnapCorrectionArea area)
        { Native.MonitorInfo ignored; return AreaStateCore(area, out ignored); }
        SnapLayoutAreaState AreaStateCore(SnapCorrectionArea area, out Native.MonitorInfo result)
        {
            result = new Native.MonitorInfo(); Native.MonitorInfo info;
            if (area == null || (restoreScope != null && !restoreScope.Contains(area))) return SnapLayoutAreaState.Unknown;
            var record = area.ToRecord();
            if (!FreshDisplay(record, out info)) return SnapLayoutAreaState.Unknown;
            if ((info.Flags & 1) != 0 || !AreaPolicy.Equal(record.Monitor, info.Monitor))
                return restoreScope == null ? SnapLayoutAreaState.Changed : SnapLayoutAreaState.Unknown;
            bool original = AreaPolicy.Equal(record.Original, info.Work);
            // A positively different, valid appbar reservation is yielded to,
            // never overwritten. It still needs the affected bar's completed
            // Show/readback before a transition may resume or release ownership.
            bool yielded = restoreScope != null && !original && !AreaPolicy.Equal(record.Applied, info.Work) &&
                AreaPolicy.Contains(record.Monitor, info.Work);
            if (!original && !yielded) return SnapLayoutAreaState.Unknown;
            // No vacuous success while Explorer or its taskbars are unavailable.
            var shell = Native.GetShellWindow(); SnapCorrectionTarget shellIdentity, shellAfter;
            if (!Capture(shell, out shellIdentity)) return SnapLayoutAreaState.Unknown;
            var bars = Native.Bars();
            if (bars.Count == 0 || bars.Count(b => b.Primary) != 1 || bars.Count(b => !b.Primary && String.Equals(b.Device, info.Device, StringComparison.OrdinalIgnoreCase)) != 1) return SnapLayoutAreaState.Unknown;
            foreach (var bar in bars) {
                // A per-display restoration must not depend on unrelated bars
                // that deliberately remain hidden on other monitors.
                if (restoreScope != null && !String.Equals(bar.Device, info.Device, StringComparison.OrdinalIgnoreCase)) continue;
                var window = new IntPtr(bar.Handle); uint process;
                if (!Native.IsWindow(window) || !Native.IsWindowVisible(window) ||
                    Native.GetWindowThreadProcessId(window, out process) == 0 || process != shellIdentity.Process ||
                    properties.Read(window, Native.Marker) != 0) return SnapLayoutAreaState.Unknown;
                var current = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (!Native.GetMonitorInfo(Native.MonitorFromWindow(window, 0), ref current) || !String.Equals(current.Device, bar.Device, StringComparison.OrdinalIgnoreCase) ||
                    (!bar.Primary && !Native.IsSecondaryTaskbar(bar))) return SnapLayoutAreaState.Unknown;
            }
            Native.MonitorInfo fresh;
            if (Native.GetShellWindow() != shell || !Capture(shell, out shellAfter) || !shellAfter.Equals(shellIdentity) ||
                !FreshDisplay(record, out fresh) || (fresh.Flags & 1) != 0 || !String.Equals(info.Device, fresh.Device, StringComparison.OrdinalIgnoreCase) ||
                !AreaPolicy.Equal(record.Monitor, fresh.Monitor) || !AreaPolicy.Equal(info.Work, fresh.Work)) return SnapLayoutAreaState.Unknown;
            result = fresh; return yielded ? SnapLayoutAreaState.Changed : SnapLayoutAreaState.OriginalReady;
        }
        public SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area)
        {
            Native.MonitorInfo info;
            var state = AreaStateCore(area, out info);
            return state == SnapLayoutAreaState.OriginalReady ? TargetMonitor(target, info) : state;
        }
        public bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> beforeNativeSet)
        {
            SnapLayoutCandidate c; long nonce;
            if (!candidates.TryGetValue(target, out c) || !nonces.TryGetValue(target, out nonce) || !AreaPolicy.Equal(c.Before, outer) ||
                AreaState(target, c.Area) != SnapLayoutAreaState.OriginalReady) return false;
            var current = Read(target);
            if (current.Kind != SnapLayoutReadKind.Current || !current.Target.Equals(target) || current.Nonce != nonce || current.Ineligible || current.Hidden || current.Iconic ||
                current.Maximized != c.Maximized || current.Arranged == c.Maximized ||
                !AreaPolicy.Equal(current.Outer, c.Desired) || !AreaPolicy.Equal(current.Visible, c.DesiredVisible)) return false;
            Native.MonitorInfo commit;
            if (!FreshDisplay(c.Area.ToRecord(), out commit) || (commit.Flags & 1) != 0 ||
                !AreaPolicy.Equal(c.Area.ToRecord().Monitor, commit.Monitor) ||
                TargetMonitor(target, commit) != SnapLayoutAreaState.OriginalReady ||
                !AreaPolicy.Equal(c.Area.ToRecord().Original, commit.Work)) return false;
            if (beforeNativeSet == null || !beforeNativeSet()) return false;
            return SetWindowPos(target.Window, IntPtr.Zero, outer.Left, outer.Top, outer.Right - outer.Left,
                outer.Bottom - outer.Top, 0x0014 | 0x0200 | 0x4000);
        }
    }
}
