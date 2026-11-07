using System;
using System.Collections.Generic;
namespace TaskbarSelect
{
    internal static class SnapLayoutReceiptTests
    {
        sealed class Api : ISnapLayoutApi
        {
            internal SnapLayoutObservation Window;
            internal SnapLayoutCandidate Candidate;
            internal SnapLayoutAreaState Area = SnapLayoutAreaState.OriginalReady;
            internal bool NonceAccepted = true, Apply = true, BoundsAccepted = true;
            internal int Marked, Removed, Sets;
            public SnapLayoutObservation Read(SnapCorrectionTarget target) { return Window.Copy(); }
            public bool SetNonce(SnapCorrectionTarget target, long nonce) { Marked++; if (NonceAccepted) Window.Nonce = nonce; return NonceAccepted; }
            public bool RemoveNonce(SnapCorrectionTarget target, long nonce) { Removed++; if (Window.Nonce != nonce) return false; Window.Nonce = 0; return true; }
            public SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area) { return Area; }
            public bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> beforeNativeSet)
            { if (!beforeNativeSet()) return false; Sets++; if (Apply) { Window.Outer = outer; Window.Visible = Candidate.BeforeVisible; } return BoundsAccepted; }
        }
        static Native.Rect R(int l, int t, int r, int b) { return new Native.Rect { Left = l, Top = t, Right = r, Bottom = b }; }
        static SnapLayoutCandidate Candidate(bool max)
        {
            SnapCorrectionArea area;
            SnapCorrectionArea.TryCapture(new AreaRecord { Identity = "neutral", Monitor = R(0, 0, 1920, 1080), Original = R(0, 0, 1920, 1032), Applied = R(0, 0, 1920, 1080) }, out area);
            return new SnapLayoutCandidate { Target = new SnapCorrectionTarget { Window = new IntPtr(1), Process = 2, Thread = 3, ProcessStarted = 4 }, Area = area,
                Maximized = max, Before = R(-8, -8, max ? 1928 : 968, 1040), BeforeVisible = R(0, 0, max ? 1920 : 960, 1032),
                Desired = R(-8, -8, max ? 1928 : 968, 1088), DesiredVisible = R(0, 0, max ? 1920 : 960, 1080) };
        }
        static Api Backend(SnapLayoutCandidate c)
        { return new Api { Candidate = c, Window = new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = c.Target, Outer = c.Before, Visible = c.BeforeVisible, Maximized = c.Maximized, Arranged = !c.Maximized } }; }
        static SnapLayoutReceiptLedger.Ticket Forward(SnapLayoutReceiptLedger ledger, Api api, bool apply)
        {
            SnapLayoutReceiptLedger.Ticket ticket;
            Check(ledger.TryPrepareForward(api.Candidate, out ticket), "Verified candidate reserves before any forward mutation");
            Check(api.Marked == 1 && ledger.MarkInvoked(ticket), "Forward needs its own readable lifetime marker");
            if (apply) { api.Window.Outer = api.Candidate.Desired; api.Window.Visible = api.Candidate.DesiredVisible; }
            ledger.RecordReturned(ticket, true); return ticket;
        }
        static int checks;
        static void Check(bool okay, string message) { if (!okay) throw new Exception(message); checks++; }
        internal static void Run()
        {
            checks = 0;
            foreach (bool max in new[] { false, true }) {
                var c = Candidate(max); var api = Backend(c); var ledger = new SnapLayoutReceiptLedger(api);
                Check(c.Valid, "Exact original contact validates for max and arranged modes");
                Forward(ledger, api, true);
                Check(!ledger.TryFinishExit(false) && api.Sets == 0, "A settle-only check cannot issue the initial inverse");
                Check(ledger.TryFinishExit(true) && api.Sets == 1 && api.Removed == 1 && api.Window.Nonce == 0 &&
                    AreaPolicy.Equal(api.Window.Outer, c.Before) && api.Window.Maximized == max && api.Window.Arranged != max,
                    "One inverse confirms exact original geometry while preserving max/arranged mode");
                Check(ledger.TryFinishExit(true) && api.Sets == 1, "Repeated completed exit has no second native geometry request");
            }
            {
                var c = Candidate(true); var api = Backend(c); api.NonceAccepted = false; var ledger = new SnapLayoutReceiptLedger(api); SnapLayoutReceiptLedger.Ticket ticket;
                Check(!ledger.TryPrepareForward(c, out ticket) && api.Sets == 0, "Denied nonce reservation cannot authorize forward geometry");
                Check(ledger.TryFinishExit(true) && api.Removed == 0, "A definitely unissued unmarked reservation can retire safely");
            }
            {
                var c = Candidate(false); var api = Backend(c); var ledger = new SnapLayoutReceiptLedger(api);
                Forward(ledger, api, true); api.Area = SnapLayoutAreaState.Unknown;
                for (int i = 0; i < 1000; i++) if (ledger.TryFinishExit(true)) throw new Exception("Unknown area falsely confirmed cleanup");
                Check(api.Sets == 0 && ledger.PendingCount == 1, "Missing/ambiguous area proof retains receipt without polling mutations");
                api.Area = SnapLayoutAreaState.OriginalReady;
                Check(ledger.TryFinishExit(true) && api.Sets == 1, "Fresh returned Original/bar proof allows one later explicit cleanup");
            }
            {
                var c = Candidate(true); var api = Backend(c); var ledger = new SnapLayoutReceiptLedger(api);
                Forward(ledger, api, false); api.Window.Kind = SnapLayoutReadKind.Gone;
                Check(ledger.TryFinishExit(true) && api.Sets == 0, "Definite destroyed target retires old lifetime without inverse even if async forward was unobserved");
            }
            {
                var c = Candidate(true); var api = Backend(c); var ledger = new SnapLayoutReceiptLedger(api);
                Forward(ledger, api, true); var changed = api.Window.Target; changed.ProcessStarted++; api.Window.Target = changed;
                Check(ledger.TryFinishExit(true) && api.Sets == 0 && api.Removed == 0, "Reused HWND lifetime never receives old inverse or old marker removal");
            }
            {
                var c = Candidate(true); var api = Backend(c); var ledger = new SnapLayoutReceiptLedger(api);
                Forward(ledger, api, true); api.Apply = false; api.BoundsAccepted = false;
                for (int i = 0; i < 1000; i++) if (ledger.TryFinishExit(true)) throw new Exception("Refused inverse falsely confirmed cleanup");
                Check(api.Sets == 1 && ledger.PendingCount == 1, "Refused inverse spends one request and keeps exit pending across explicit retries");
            }
            foreach (bool pending in new[] { false, true }) {
                var c = Candidate(true); var api = Backend(c); var ledger = new SnapLayoutReceiptLedger(api);
                Forward(ledger, api, !pending); api.Window.Ineligible = true;
                bool clean = ledger.TryFinishExit(true);
                Check(clean != pending && api.Sets == 0, "Positive ineligibility skips confirmed shape but cannot retire ambiguous latest forward");
            }
            {
                var order = new List<string>(); bool areaFails = true, finishClean = false; int inverse = 0;
                var policy = new SnapLayoutExitPolicy(delegate { order.Add("quiesce"); }, delegate {
                    return Recovery.Run(delegate { order.Add("areas"); if (areaFails) throw new Exception("injected"); }, delegate { order.Add("bars"); });
                }, delegate(bool allow) { order.Add(allow ? "inverse" : "readback"); if (allow) inverse++; return finishClean; });
                Check(!policy.TryPrepare(true) && String.Join(",", order) == "quiesce,areas,bars" && inverse == 0,
                    "Real exit ordering restores visible bars in finally while area failure vetoes inverse/cleanup");
                order.Clear();
                for (int i = 0; i < 1000; i++) if (policy.TryPrepare(false)) throw new Exception("Recovery failure released ownership");
                Check(inverse == 0 && order.Count == 1000, "Settling checks repeat no area/bar writes after failed recovery");
                areaFails = false; order.Clear();
                Check(!policy.TryPrepare(true) && String.Join(",", order) == "quiesce,areas,bars,inverse" && inverse == 1,
                    "Explicit retry grants one cleanup phase only after successful area/bar recovery");
                order.Clear(); finishClean = true;
                Check(policy.TryPrepare(false) && String.Join(",", order) == "quiesce,readback" && inverse == 1,
                    "Successful readback authorizes completion without another recovery/inverse mutation");
            }
            {
                int bars = 0, finish = 0;
                var policy = new SnapLayoutExitPolicy(delegate { throw new Exception("quiesce"); },
                    delegate { return Recovery.Run(delegate { throw new Exception("area"); }, delegate { bars++; }); },
                    delegate(bool allow) { finish++; return true; });
                Check(!policy.TryPrepare(true) && bars == 1 && finish == 0 && policy.Error != null,
                    "Quiesce/area exceptions retain ownership and still execute visible-taskbar finally");
                Check(!policy.TryPrepare(false) && bars == 1 && finish == 0,
                    "Failed readback quiesce never unwinds or repeats native recovery");
                policy = new SnapLayoutExitPolicy(delegate { }, delegate { throw new Exception("recovery"); }, delegate(bool allow) { finish++; return true; });
                Check(!policy.TryPrepare(true) && finish == 0 && policy.Error != null, "Recovery delegate exception fails closed without authorizing cleanup");
                policy = new SnapLayoutExitPolicy(delegate { }, delegate { return Recovery.Run(delegate { }, delegate { }); }, delegate(bool allow) { throw new Exception("finish"); });
                Check(!policy.TryPrepare(true) && policy.Error != null && !policy.TryPrepare(false), "Finish and settling exceptions retain ownership without unwinding the message loop");
            }
            {
                SnapLayoutExitPolicy policy = null; bool nested = true; int finish = 0;
                policy = new SnapLayoutExitPolicy(delegate { nested = policy.TryPrepare(true); }, delegate { return Recovery.Run(delegate { }, delegate { }); }, delegate(bool allow) { finish++; return false; });
                Check(!policy.TryPrepare(true) && !nested && finish == 1, "Reentrant PrepareExit cannot duplicate recovery/finish or authorize mutex release");
            }
        }
        internal static int RunPureTest()
        { try { Run(); Console.WriteLine("PASS: " + checks + " receipt/basic exit checks; injected only."); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
    }
}
