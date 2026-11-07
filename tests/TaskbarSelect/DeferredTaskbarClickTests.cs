using System;

namespace TaskbarSelect
{
    // Pure ownership model for swallowed input. Replay is represented by an
    // enum only: no real taskbar, input injection, native move or profile access.
    internal static class DeferredTaskbarClickTests
    {
        internal static void Run(Action<bool, string> check)
        {
            Admission(check);
            IdentityAndProtection(check);
            OrdinaryInputProvenance(check);
            ExactTargetProof(check);
            ColdResolution(check);
            RejectedResolution(check);
            MutationAndCancellation(check);
            DirectReplayRecovery(check);
        }

        static bool CanBegin(bool physical = true, bool modified = false, bool enabled = true,
            bool busy = false, bool eligible = true, bool elevated = true, bool administrator = true,
            bool crossMonitor = true, bool cachedUsable = false, bool uncertain = false)
        {
            return DeferredTaskbarClick.CanBegin(physical, modified, enabled, busy, eligible, elevated,
                administrator, crossMonitor, cachedUsable, uncertain);
        }
        static DeferredTaskbarClick Click() { return new DeferredTaskbarClick(1000, 1000); }
        static DeferredTaskbarClick Released(bool physical = true, bool modified = false, bool inside = true, bool dragged = false)
        {
            var click = Click(); click.Release(physical, modified, inside, dragged, 1100); return click;
        }

        static void Admission(Action<bool, string> check)
        {
            check(CanBegin(), "An eligible physical cross-monitor click can wait for cold target verification before Explorer minimizes it");
            check(!CanBegin(physical: false) && !CanBegin(modified: true),
                "An injected elevated click or modified mouse-down cannot acquire deferred input ownership");
            check(!CanBegin(enabled: false) && !CanBegin(busy: true) && !CanBegin(eligible: false),
                "Disabled, occupied or untrusted-window paths never swallow a new taskbar mouse-down");
            check(!CanBegin(administrator: false) && !CanBegin(crossMonitor: false),
                "Unavailable elevated-app support and same-monitor clicks preserve their existing native behavior");
            foreach(bool physical in new[]{true,false}) {
                check(CanBegin(physical: physical,elevated: false,administrator: false),
                    "A cold ordinary foreground click can await fresh proof with administrator support Off; physical="+physical);
                check(CanBegin(physical: physical,elevated: false,administrator: true),
                    "Administrator support being On does not change ordinary click admission; physical="+physical);
                check(!CanBegin(physical: physical,elevated: false,administrator: false,crossMonitor: false) &&
                    !CanBegin(physical: physical,elevated: false,administrator: false,uncertain: true),
                    "Same-monitor and uncertain-worker contexts never begin ordinary deferral; physical="+physical);
                check(TaskbarMover.DirectMoveAllowed(true,false,new IntPtr(1),new IntPtr(2),false) &&
                    !CanBegin(physical: physical,elevated: false,administrator: false,cachedUsable: true),
                    "A verified warm ordinary target keeps the existing direct route without a second input owner; physical="+physical);
            }
            check(!CanBegin(cachedUsable: true), "A verified warm-cache direct route does not acquire a second deferred-click owner");
        }

        static NativeProcessIdentity.Info Identity(uint process, long started, uint integrity)
        {
            return new NativeProcessIdentity.Info { Process=process, Started=started, IntegrityAvailable=true,
                IntegrityLevel=integrity, ElevationAvailable=true, Elevated=integrity>=NativeProcessIdentity.High };
        }
        static void IdentityAndProtection(Action<bool,string> check)
        {
            var caller=Identity(10,100,NativeProcessIdentity.Medium);
            var expected=Identity(20,200,NativeProcessIdentity.Medium);
            check(DeferredTaskbarClick.IdentityCurrent(caller,expected,expected.Copy(),false),
                "Fresh ordinary identity is bound to the original PID, start time and known integrity");
            foreach(int change in new[]{0,1,2,3,4,5}) {
                var actual=expected.Copy();
                if(change==0)actual.Process++;
                if(change==1)actual.Started++;
                if(change==2)actual.IntegrityAvailable=false;
                if(change==3)actual.IntegrityLevel=NativeProcessIdentity.High;
                if(change==4)actual.Started=0;
                if(change==5)actual.Process=0;
                check(!DeferredTaskbarClick.IdentityCurrent(caller,expected,actual,false),
                    "Changed or unknown ordinary process metadata cannot dispatch a deferred move: "+change);
            }
            var unknownCaller=caller.Copy();unknownCaller.IntegrityAvailable=false;
            var unknownExpected=expected.Copy();unknownExpected.IntegrityAvailable=false;
            check(!DeferredTaskbarClick.IdentityCurrent(unknownCaller,expected,expected,false) &&
                !DeferredTaskbarClick.IdentityCurrent(caller,unknownExpected,expected,false) &&
                !DeferredTaskbarClick.IdentityCurrent(null,expected,expected,false),
                "RequiresAdministrator=false never turns missing token evidence into an ordinary-app authorization");
            var elevated=Identity(20,200,NativeProcessIdentity.High);
            check(DeferredTaskbarClick.IdentityCurrent(caller,elevated,elevated.Copy(),true) &&
                !DeferredTaskbarClick.IdentityCurrent(caller,elevated,elevated.Copy(),false) &&
                !DeferredTaskbarClick.IdentityCurrent(caller,elevated,expected,false),
                "Original elevated classification cannot be downgraded during fresh verification");
            check(DeferredTaskbarClick.ProtectedInput(true,false,false) && DeferredTaskbarClick.ProtectedInput(false,true,false) &&
                DeferredTaskbarClick.ProtectedInput(false,false,true) && !DeferredTaskbarClick.ProtectedInput(false,false,false),
                "Original elevation or native provenance keeps physical-input protection even if a fresh target flag is ordinary");
        }

        static void OrdinaryInputProvenance(Action<bool,string> check)
        {
            foreach(bool physical in new[]{true,false}) {
                var click=new DeferredTaskbarClick(1000,1000,physical);
                click.Release(physical,false,true,false,1100);
                check(click.Evaluate(false,false,true,1150)==DeferredTaskbarAction.None &&
                    click.Evaluate(true,true,true,1200)==DeferredTaskbarAction.Move &&
                    click.AfterMove(true,true,false)==DeferredTaskbarAction.Drop &&
                    click.AfterMove(true,true,false)==DeferredTaskbarAction.None,
                    "An ordinary cold click waits for release/proof and dispatches once without native replay; physical="+physical);
                var mixed=new DeferredTaskbarClick(1000,1000,physical);
                mixed.Release(!physical,false,true,false,1100);
                check(mixed.Evaluate(true,true,true,1200)==DeferredTaskbarAction.ReplayClick &&
                    mixed.Evaluate(true,true,true,1250)==DeferredTaskbarAction.None,
                    "Mixed physical/injected down-up provenance cannot move and returns the owned click once; physicalDown="+physical);
                check(DeferredTaskbarClick.GestureCurrent(false,physical,physical,1000,1050,1100,false) &&
                    !DeferredTaskbarClick.GestureCurrent(false,physical,!physical,1000,1050,1100,false) &&
                    !DeferredTaskbarClick.GestureCurrent(false,physical,physical,1000,999,1100,false) &&
                    !DeferredTaskbarClick.GestureCurrent(false,physical,physical,1000,1050,2000,false),
                    "Ordinary input keeps provenance and original gesture chronology; physical="+physical);
                var timeout=new DeferredTaskbarClick(1000,1000,physical);timeout.Release(physical,false,true,false,1100);
                check(timeout.Evaluate(false,false,true,1650)==DeferredTaskbarAction.ReplayClick &&
                    timeout.Evaluate(true,true,true,1700)==DeferredTaskbarAction.None,
                    "An ordinary reader timeout replays once and late proof cannot revive movement; physical="+physical);
            }
            check(!DeferredTaskbarClick.GestureCurrent(true,false,false,1000,1050,1100,false) &&
                DeferredTaskbarClick.GestureCurrent(true,true,true,1000,1050,1100,false),
                "Elevated or native-selection requests still require physical down and up");
            check(DeferredTaskbarClick.GestureCurrent(false,false,false,UInt32.MaxValue-30,10,20,false),
                "Injected ordinary chronology remains valid across the native tick counter wrap");
        }

        static TaskbarTargets.Target Target()
        {
            return new TaskbarTargets.Target { Window=new IntPtr(10),Process=20,ProcessStarted=200,
                Button=new TaskbarMover.Click(),Windows=new[]{new TaskbarTargets.WindowIdentity {
                    Window=new IntPtr(10),Process=20,ProcessStarted=200 }} };
        }
        static void ExactTargetProof(Action<bool,string> check)
        {
            check(DeferredTaskbarClick.MatchesTarget(Target(),new IntPtr(10),20,200),
                "Deferred proof identifies exactly the captured foreground window and process lifetime");
            for(int change=0;change<10;change++) {
                var target=Target();
                if(change==0)target.Window=new IntPtr(11);
                if(change==1)target.Process++;
                if(change==2)target.ProcessStarted++;
                if(change==3)target.Button.Grouped=true;
                if(change==4)target.Button.PreviewOnly=true;
                if(change==5)target.Button.SelectionStarted=true;
                if(change==6)target.Windows=new[]{target.Windows[0],target.Windows[0]};
                if(change==7)target.Windows[0].Window=new IntPtr(11);
                if(change==8)target.Windows[0].Process++;
                if(change==9)target.Windows[0].ProcessStarted++;
                check(!DeferredTaskbarClick.MatchesTarget(target,new IntPtr(10),20,200),
                    "Grouped, ambiguous, preview or changed foreground proof cannot dispatch a deferred move: "+change);
            }
            check(!DeferredTaskbarClick.MatchesTarget(null,new IntPtr(10),20,200),
                "A missing fresh target is not interpreted as a verified ordinary app");
        }

        static void ColdResolution(Action<bool, string> check)
        {
            var click = Click();
            check(click.Evaluate(false, false, true, 1050) == DeferredTaskbarAction.None && !click.Released,
                "A cold-cache mouse-down waits without replaying, moving or pretending the button was released");
            click.Release(true, false, true, false, 1100);
            check(click.Released && click.Evaluate(false, false, true, 1150) == DeferredTaskbarAction.None,
                "A valid release retains input ownership while fresh proof is still pending");
            check(click.Evaluate(true, true, true, 1200) == DeferredTaskbarAction.Move && click.Moving,
                "A completed valid release and fresh target proof admit exactly one move without an Explorer minimize");
            check(click.Evaluate(true, true, true, 1210) != DeferredTaskbarAction.Move &&
                click.Abort(true) == DeferredTaskbarAction.Drop,
                "Reentrant processing cannot repeat a move or replay the swallowed click while native mutation is in flight");
        }

        static void RejectedResolution(Action<bool, string> check)
        {
            var missing = Released();
            check(missing.Evaluate(true, false, true, 1200) == DeferredTaskbarAction.ReplayClick && missing.Settled &&
                missing.Evaluate(true, true, true, 1250) == DeferredTaskbarAction.None,
                "A reader miss, missing proof or changed target returns the native click once and cannot be revived by late proof");
            var held = Click();
            check(held.Evaluate(false, false, true, 1649) == DeferredTaskbarAction.None &&
                held.Evaluate(false, false, true, 1650) == DeferredTaskbarAction.ReplayDown,
                "The original 650ms verification budget preserves a held down on expiry without restarting its deadline");
            var released = Released();
            check(released.Evaluate(false, false, true, 1650) == DeferredTaskbarAction.ReplayClick,
                "A timed-out released gesture restores one complete click instead of losing the user's input");
            check(Released().Evaluate(true, true, true, 999) == DeferredTaskbarAction.ReplayClick,
                "A backward monotonic clock cannot admit movement from deferred proof");
            check(Released(physical: false).Evaluate(true, true, true, 1200) == DeferredTaskbarAction.ReplayClick &&
                Released(modified: true).Evaluate(true, true, true, 1200) == DeferredTaskbarAction.ReplayClick &&
                Released(inside: false).Evaluate(true, true, true, 1200) == DeferredTaskbarAction.ReplayClick &&
                Released(dragged: true).Evaluate(true, true, true, 1200) == DeferredTaskbarAction.ReplayClick,
                "Injected releases, modifiers, button exits and icon dragging return native input without admitting a move");
            check(Released().Evaluate(true, true, false, 1200) == DeferredTaskbarAction.ReplayClick,
                "Changed foreground, exclusion or movement context prevents deferred admission before any mutation");
        }

        static void MutationAndCancellation(Action<bool, string> check)
        {
            var held = Click(); var released = Released();
            check(held.Abort(true) == DeferredTaskbarAction.ReplayDown && held.Abort(true) == DeferredTaskbarAction.None &&
                released.Abort(true) == DeferredTaskbarAction.ReplayClick && released.Abort(true) == DeferredTaskbarAction.None,
                "Pause, stop, disable or disposal returns owned input once when the original shell geometry is still valid");
            var invalidGeometry = Released();
            check(invalidGeometry.Abort(false) == DeferredTaskbarAction.Drop &&
                invalidGeometry.Evaluate(true, true, true, 1200) == DeferredTaskbarAction.None,
                "A changed source, destination or taskbar geometry cannot replay a click onto an unrelated location");
            foreach (int outcome in new[] { 0, 1, 2 }) {
                var click = Released(); click.Evaluate(true, true, true, 1200);
                check(click.AfterMove(outcome == 0, outcome == 1, outcome == 2) == DeferredTaskbarAction.Drop && click.Settled,
                    "Successful, possibly mutated or interrupted movement never replays a native click after the operation starts");
            }
            var failedBeforeMutation = Released(); failedBeforeMutation.Evaluate(true, true, true, 1200);
            check(failedBeforeMutation.AfterMove(false, false, false) == DeferredTaskbarAction.ReplayClick &&
                failedBeforeMutation.AfterMove(false, false, false) == DeferredTaskbarAction.None,
                "A failed move with confirmed absence of mutation may restore its native click exactly once");
        }

        static void DirectReplayRecovery(Action<bool,string> check)
        {
            check(TaskbarMover.DirectReplayAllowed(false,false,false,false,true),
                "A failed fresh proof can replay despite a stale mutation diagnostic from an older move");
            check(TaskbarMover.DirectReplayAllowed(false,false,false,true,false),
                "A verified direct failure before any mutation may restore its normal native click");
            check(!TaskbarMover.DirectReplayAllowed(false,false,false,true,true),
                "An ordinary direct move that may have partly changed geometry never replays Explorer minimize");
            check(!TaskbarMover.DirectReplayAllowed(true,false,false,true,true) &&
                !TaskbarMover.DirectReplayAllowed(false,true,false,true,false) &&
                !TaskbarMover.DirectReplayAllowed(false,false,true,true,false),
                "Successful, interrupted or disposed direct operations never replay their native click");
        }
    }
}
