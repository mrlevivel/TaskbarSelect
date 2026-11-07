using System;
using System.Collections.Generic;

namespace TaskbarSelect
{
    // Every observation is injected. No helper, windows, USER mutation,
    // permissions, persisted state, hooks or desktop input are involved.
    internal static class AdministratorPinRecoveryTests
    {
        sealed class Fixture
        {
            internal readonly Dictionary<IntPtr, FocusedWindowSnapshot> Windows = new Dictionary<IntPtr, FocusedWindowSnapshot>();
            internal bool Unreadable, ThrowRead, ThrowExists;
            internal readonly AdministratorPinRecovery Ledger;
            internal Fixture()
            {
                Ledger = new AdministratorPinRecovery(new AdministratorPinRecoveryApi {
                    Exists = delegate(IntPtr window) { if (ThrowExists) throw new InvalidOperationException(); return Windows.ContainsKey(window); },
                    Read = delegate(IntPtr window) {
                        if (ThrowRead) throw new InvalidOperationException();
                        FocusedWindowSnapshot value;
                        return !Unreadable && Windows.TryGetValue(window, out value) ? value.Copy() : null;
                    }
                });
            }
            internal FocusedWindowSnapshot Add(int handle)
            {
                var window = new FocusedWindowSnapshot { Window = new IntPtr(handle), Root = new IntPtr(handle), Process = 100,
                    Thread = 200, ProcessStarted = 300, Visible = true, ClassName = "DemoWindow", ProcessImage = @"C:\Demo\Demo.exe" };
                Windows[window.Window] = window; return window;
            }
        }
        internal static void Run(Action<bool, string> check)
        {
            Admission(check); ReleaseEvidence(check); Capacity(check);
        }
        static void Admission(Action<bool, string> check)
        {
            var f = new Fixture();
            check(!f.Ledger.TryConfirmReleased(), "An empty ledger without a recorded authorization cannot clear unknown HIGH pin recovery");
            check(!f.Ledger.Remember(null) && f.Ledger.Count == 0, "A missing authorization snapshot is rejected without claiming ownership");
            var window = f.Add(42);
            foreach (Action<FocusedWindowSnapshot> invalidate in new Action<FocusedWindowSnapshot>[] {
                delegate(FocusedWindowSnapshot w) { w.Root = IntPtr.Zero; },
                delegate(FocusedWindowSnapshot w) { w.ProcessStarted = 0; },
                delegate(FocusedWindowSnapshot w) { w.ProcessImage = ""; },
                delegate(FocusedWindowSnapshot w) { w.ExtendedStyle = 8; },
                delegate(FocusedWindowSnapshot w) { w.Visible = false; }
            }) {
                var invalid = window.Copy(); invalidate(invalid);
                check(!f.Ledger.Remember(invalid) && f.Ledger.Count == 0, "Only a verified non-topmost eligible authorization can enter the HIGH observation ledger");
            }
            var stale = window.Copy(); stale.Thread++;
            check(!f.Ledger.Remember(stale) && f.Ledger.Count == 0, "A changed current lifetime cannot be replaced by a stale authorization snapshot");
            check(f.Ledger.Remember(window) && f.Ledger.Count == 1, "The exact verified authorization is retained before the HIGH pin can be sent");
            window.ExtendedStyle = 8;
            check(!f.Ledger.TryConfirmReleased() && f.Ledger.Count == 1, "A live still-topmost target retains HIGH pin recovery after helper drain");
            f.Ledger.Reset();
            check(f.Ledger.Count == 0 && !f.Ledger.TryConfirmReleased() && window.ExtendedStyle == 8,
                "An authenticated zero-owned reset forgets observations without changing the actual target or authorizing later empty recovery");
        }
        static void ReleaseEvidence(Action<bool, string> check)
        {
            foreach (int mode in new[] { 0, 1, 2, 3, 4, 5, 6, 7 }) {
                var f = new Fixture(); var window = f.Add(42); check(f.Ledger.Remember(window), "Owned recovery fixture records its exact pin authorization " + mode);
                window.ExtendedStyle = 8;
                switch (mode) {
                    case 0: f.Windows.Remove(window.Window); break;
                    case 1: window.ProcessStarted++; break;
                    case 2: window.Thread++; break;
                    case 3: window.ClassName = "Replacement"; break;
                    case 4: window.ProcessImage = @"C:\Demo\Replacement.exe"; break;
                    case 5: window.ExtendedStyle = 0; break;
                    case 6: window.Process++; break;
                    case 7: window.Visible = false; window.ExtendedStyle = 0; break;
                }
                check(f.Ledger.TryConfirmReleased() && f.Ledger.Count == 0, "Positive closed, replaced or non-topmost evidence permits recovery without a PC restart " + mode);
                check(!f.Ledger.TryConfirmReleased(), "A successful observation proof is consumed and cannot clear a future unaccounted latch " + mode);
            }
            foreach (int mode in new[] { 0, 1, 2, 3, 4, 5 }) {
                var f = new Fixture(); var window = f.Add(42); check(f.Ledger.Remember(window), "Uncertain recovery fixture records an authorization " + mode);
                window.ExtendedStyle = 8;
                switch (mode) {
                    case 0: f.Unreadable = true; break;
                    case 1: f.ThrowRead = true; break;
                    case 2: f.ThrowExists = true; break;
                    case 3: window.ProcessImage = ""; break;
                    case 4: window.Error = 5; break;
                    case 5: window.Root = IntPtr.Zero; break;
                }
                check(!f.Ledger.TryConfirmReleased() && f.Ledger.Count == 1,
                    "Missing identity, failed observations and unreadable style cannot be mistaken for restored HIGH pins " + mode);
            }
        }
        static void Capacity(Action<bool, string> check)
        {
            var f = new Fixture();
            for (int handle = 1; handle <= OwnedWindowPins.Limit; handle++) {
                var window = f.Add(handle);
                check(f.Ledger.Remember(window), "The bounded HIGH recovery ledger admits an accounted authorization " + handle);
                window.ExtendedStyle = 8;
            }
            var extra = f.Add(100);
            check(!f.Ledger.Remember(extra) && f.Ledger.Count == OwnedWindowPins.Limit, "A full ledger rejects new pin authority instead of dropping a live target");
            f.Windows.Remove(new IntPtr(1));
            check(f.Ledger.Remember(extra) && f.Ledger.Count == OwnedWindowPins.Limit, "A positively closed target reclaims HIGH observation capacity before admission");
            extra.ExtendedStyle = 8; f.Windows.Clear();
            check(f.Ledger.TryConfirmReleased(), "Closing every retained target proves all accounted HIGH pins gone");
            f = new Fixture();
            bool admitted = true;
            for (int handle = 1; handle <= 3 * OwnedWindowPins.Limit; handle++) {
                var window = f.Add(handle); admitted &= f.Ledger.Remember(window); window.ExtendedStyle = 8; f.Windows.Remove(window.Window);
            }
            check(admitted && f.Ledger.Count <= 1 && f.Ledger.TryConfirmReleased(), "Repeated pin-and-close cycles prune positive evidence instead of exhausting HIGH recovery capacity");
        }
    }
}
