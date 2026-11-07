using System;

namespace TaskbarSelect
{
    // Production property contract plus receipt ledger, injected delegates only.
    internal static class SnapLayoutPropertyTests
    {
        sealed class Fixture : ISnapLayoutApi
        {
            internal readonly SnapLayoutProperties Properties;
            internal readonly SnapLayoutReceiptLedger Ledger;
            internal readonly SnapLayoutCandidate Candidate;
            internal SnapLayoutObservation Observation;
            internal long Value;
            internal int StaleError, Sets, Removes, ForwardCalls, InverseCalls, WriteMode;
            internal bool SetAccepted = true;
            internal Fixture(int staleError = 0)
            {
                StaleError = staleError;
                Properties = new SnapLayoutProperties(
                    delegate(IntPtr window, string name) { return new IntPtr(Value); },
                    delegate(IntPtr window, string name, IntPtr value) {
                        Sets++; if (!SetAccepted) return false;
                        if (WriteMode == 0) Value = value.ToInt64();
                        else if (WriteMode == 2) Value = value.ToInt64() + 1;
                        return true;
                    },
                    delegate(IntPtr window, string name) { Removes++; long value = Value; Value = 0; return new IntPtr(value); });
                SnapCorrectionArea area;
                if (!SnapCorrectionArea.TryCapture(new AreaRecord { Identity = "injected-property-contract",
                    Monitor = Rect(0, 0, 640, 480), Original = Rect(0, 0, 640, 432), Applied = Rect(0, 0, 640, 480) }, out area))
                    throw new Exception("Invalid property fixture area");
                Candidate = new SnapLayoutCandidate { Target = new SnapCorrectionTarget { Window = new IntPtr(1), Process = 2, Thread = 3, ProcessStarted = 4 },
                    Area = area, Maximized = true, Before = Rect(-8, -8, 648, 440), BeforeVisible = Rect(0, 0, 640, 432),
                    Desired = Rect(-8, -8, 648, 488), DesiredVisible = Rect(0, 0, 640, 480) };
                if (!Candidate.Valid) throw new Exception("Invalid property fixture candidate");
                Observation = new SnapLayoutObservation { Kind = SnapLayoutReadKind.Current, Target = Candidate.Target,
                    Outer = Candidate.Before, Visible = Candidate.BeforeVisible, Maximized = true, Arranged = false };
                Ledger = new SnapLayoutReceiptLedger(this);
            }
            public SnapLayoutObservation Read(SnapCorrectionTarget target)
            { var observation = Observation.Copy(); observation.Nonce = Properties.Read(target.Window, "injected"); return observation; }
            public bool SetNonce(SnapCorrectionTarget target, long nonce)
            { return Properties.Mark(target.Window, "injected", nonce); }
            public bool RemoveNonce(SnapCorrectionTarget target, long nonce)
            { return Properties.Release(target.Window, "injected", nonce); }
            public SnapLayoutAreaState AreaState(SnapCorrectionTarget target, SnapCorrectionArea area)
            { return SnapLayoutAreaState.OriginalReady; }
            public bool SetBounds(SnapCorrectionTarget target, Native.Rect outer, Func<bool> beforeNativeSet)
            {
                if (!beforeNativeSet()) return false;
                InverseCalls++; Observation.Outer = Candidate.Before; Observation.Visible = Candidate.BeforeVisible; return true;
            }
            internal bool Forward(bool observed)
            {
                SnapLayoutReceiptLedger.Ticket ticket;
                if (!Ledger.TryPrepareForward(Candidate, out ticket) || !Ledger.MarkInvoked(ticket)) return false;
                ForwardCalls++;
                if (observed) { Observation.Outer = Candidate.Desired; Observation.Visible = Candidate.DesiredVisible; }
                Ledger.RecordReturned(ticket, true); return true;
            }
        }
        static Native.Rect Rect(int left, int top, int right, int bottom)
        { return new Native.Rect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        static int checks;
        static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        internal static void Run()
        {
            checks = 0;
            foreach (int staleError in new[] { 0, 2, 5, 1400 }) {
                var f = new Fixture(staleError);
                Check(f.Properties.Read(f.Candidate.Target.Window, "injected") == 0 && f.StaleError == staleError,
                    "Documented absent GetProp value is zero independently of an unrelated or stale error value");
                Check(f.Forward(true) && f.Sets == 1 && f.Value == 1 && f.ForwardCalls == 1,
                    "Absent property permits forward only after actual successful write and exact positive nonce readback");
                Check(f.Ledger.TryFinishExit(true) && f.InverseCalls == 1 && f.Removes == 1 && f.Value == 0,
                    "A matching positive nonce authorizes one verified inverse and removal despite stale error state");
            }
            {
                var f = new Fixture(5); f.SetAccepted = false;
                Check(!f.Forward(true) && f.Sets == 1 && f.ForwardCalls == 0 && f.InverseCalls == 0,
                    "Actual SetProp refusal retains the write boundary and denies all corresponding geometry mutation");
            }
            foreach (int mode in new[] { 1, 2 }) {
                var f = new Fixture(); f.WriteMode = mode;
                Check(!f.Forward(true) && f.Sets == 1 && f.ForwardCalls == 0 && f.InverseCalls == 0,
                    "Accepted SetProp without exact positive readback cannot authorize forward mutation");
            }
            {
                var f = new Fixture(); f.Value = 19;
                Check(!f.Forward(true) && f.Sets == 0 && f.Value == 19,
                    "A preexisting positive foreign property is not overwritten or borrowed");
            }
            {
                var f = new Fixture();
                Check(!f.Properties.Mark(f.Candidate.Target.Window, "injected", 0) &&
                    !f.Properties.Mark(f.Candidate.Target.Window, "injected", -1) && f.Sets == 0,
                    "Zero and negative nonce requests cannot create forward authority");
                Check(!f.Properties.Release(f.Candidate.Target.Window, "injected", 0) &&
                    !f.Properties.Release(f.Candidate.Target.Window, "injected", 1) && f.Removes == 0,
                    "Absent property and zero expected nonce cannot authorize native removal");
                f.Value = 2;
                Check(!f.Properties.Release(f.Candidate.Target.Window, "injected", 1) && f.Removes == 0 && f.Value == 2,
                    "A mismatched positive property cannot authorize removal");
            }
            {
                var f = new Fixture(); Check(f.Forward(true), "Positive property fixture forward applies"); f.Value = 0;
                Check(f.Ledger.TryFinishExit(true) && f.InverseCalls == 0 && f.Removes == 0,
                    "Property absence after a confirmed forward can retire ownership but never authorize inverse or removal");
                f = new Fixture(); Check(f.Forward(false), "Pending property fixture forward is accepted"); f.Value = 0;
                Check(!f.Ledger.TryFinishExit(true) && f.InverseCalls == 0 && f.Removes == 0,
                    "Zero property while accepted forward remains unobserved retains ambiguity without authorizing inverse");
            }
            {
                int writes = 0;
                var properties = new SnapLayoutProperties(
                    delegate { throw new InvalidOperationException("Injected query failure"); },
                    delegate { writes++; return true; }, delegate { throw new Exception("Removal must not be called"); });
                bool threw = false;
                try { properties.Mark(new IntPtr(1), "injected", 1); } catch (InvalidOperationException) { threw = true; }
                Check(threw && writes == 0, "An actual query invocation failure is not reclassified as documented absence");
            }
        }
        internal static int RunPureTest()
        {
            try { Run(); Console.WriteLine("PASS: " + checks + " injected property-contract checks; no native or desktop access."); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
