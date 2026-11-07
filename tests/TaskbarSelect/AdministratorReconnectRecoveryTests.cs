using System;
using System.Diagnostics;
using System.Reflection;

namespace TaskbarSelect
{
    // In-memory broker policy only. Synthetic sessions do not launch a task,
    // authenticate a helper, touch USER, or stand in for actual drain proof.
    internal static class AdministratorReconnectRecoveryTests
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static object Get(object value, string name)
        { return value.GetType().GetField(name, PrivateInstance).GetValue(value); }
        static void Set(object value, string name, object data)
        { value.GetType().GetField(name, PrivateInstance).SetValue(value, data); }
        static object New(string name)
        { return Activator.CreateInstance(typeof(AdministratorSupport).GetNestedType(name, BindingFlags.NonPublic), true); }
        static object Call(AdministratorSupport broker, string name, params object[] args)
        { return typeof(AdministratorSupport).GetMethod(name, PrivateInstance).Invoke(broker, args); }
        static WindowMoveWorker.Result Success()
        { return new WindowMoveWorker.Result { Outcome = WindowMoveWorker.Outcome.Success, MutationAttempted = true, CallerExited = true }; }

        sealed class Fixture : IDisposable
        {
            internal readonly AdministratorSupport Broker = new AdministratorSupport();
            internal object Session, Lease;
            internal int Generation = 72;
            internal Fixture() { Connected(Generation); }
            internal void Connected(int token)
            {
                Generation = token; Session = New("LaunchSession"); Lease = New("Lease");
                Set(Session, "ImageVerified", true); Set(Session, "StartComplete", true);
                Set(Lease, "Mutation", true); Set(Lease, "Authorized", true);
                Set(Broker, "generation", token); Set(Broker, "failureGeneration", token - 1);
                Set(Broker, "active", true); Set(Broker, "enabling", false);
                Set(Broker, "launchSession", Session); Set(Broker, "operation", Lease);
                Set(Broker, "pendingOperations", 1); Set(Broker, "pendingShutdowns", 0);
                Set(Broker, "uncertain", false); Set(Broker, "shutdownConfirmed", false);
                Set(Broker, "retirementConfirmed", false); Set(Broker, "reconnectUsed", true);
                Set(Broker, "reconnectRearmGeneration", token);
                Set(Broker, "reconnectRearmSession", Session); Set(Broker, "reconnectRearmLease", null);
                Set(Broker, "lastIncident", "Owned MainValidity seq=71 op=1 validity=8");
                Set(Broker, "lastIncidentGeneration", token - 1);
            }
            internal void Stage(WindowMoveWorker.Result result, int activeWorkers = 0, int uncertainWorkers = 0, int? token = null)
            { Call(Broker, "StageReconnectBudget", Lease, result, activeWorkers, uncertainWorkers, token ?? Generation); }
            internal void Complete()
            {
                Set(Lease, "Terminal", true); Set(Lease, "Complete", true);
                Set(Broker, "pendingOperations", 0); Set(Broker, "operation", null);
            }
            internal void Refresh() { Call(Broker, "RefreshReconnectBudget"); }
            internal bool Used { get { return (bool)Get(Broker, "reconnectUsed"); } }
            internal object Candidate()
            {
                var candidate = New("ReconnectCandidate");
                Set(Session, "Retired", 1); Set(Session, "RetirementComplete", true);
                Set(Session, "RetirementDrained", true); Set(Session, "RetirementExit", (int?)0);
                Set(Session, "RetirementGeneration", Generation);
                Set(candidate, "Generation", Generation); Set(candidate, "Lease", Lease); Set(candidate, "Session", Session);
                Set(candidate, "Began", Stopwatch.GetTimestamp() - Stopwatch.Frequency);
                Set(Broker, "active", false); Set(Broker, "failureGeneration", Generation);
                Set(Broker, "shutdownConfirmed", true); Set(Broker, "retirementConfirmed", true);
                Set(Broker, "reconnectCandidate", candidate);
                return candidate;
            }
            public void Dispose()
            {
                // No synthetic launch is ever handed to ordinary retirement.
                Set(Broker, "launchSession", null); Set(Broker, "operation", null);
                Set(Broker, "reconnectCandidate", null); Set(Broker, "reconnectRearmSession", null);
                Set(Broker, "reconnectRearmLease", null); Set(Broker, "reconnectRearmGeneration", -1);
                Set(Broker, "active", false); Set(Broker, "enabling", false);
                Set(Broker, "uncertain", false); Set(Broker, "pendingOperations", 0);
                Set(Broker, "pendingShutdowns", 0); Set(Broker, "shutdownConfirmed", true);
                Set(Broker, "disposed", false); Broker.Dispose();
            }
        }

        internal static void Run(Action<bool, string> check)
        { ReceiptAdmission(check); CompletionFences(check); RearmGuards(check); RepeatedCancellation(check); ResetInvalidates(check); }

        static void ReceiptAdmission(Action<bool, string> check)
        {
            foreach (string rejected in new[] { "Rejected", "Failed", "TimedOut", "Cancelled", "noMutation", "callerLive", "activeWorker", "uncertainWorker",
                "queryLease", "unauthorized", "staleToken", "staleGeneration", "otherSession", "unverifiedSession", "retiredSession", "otherOperation", "unspent" })
                using (var fixture = new Fixture()) {
                    var result = Success(); int activeWorkers = 0, uncertainWorkers = 0, token = fixture.Generation;
                    if (rejected == "Rejected") result.Outcome = WindowMoveWorker.Outcome.Rejected;
                    else if (rejected == "Failed") result.Outcome = WindowMoveWorker.Outcome.Failed;
                    else if (rejected == "TimedOut") result.Outcome = WindowMoveWorker.Outcome.TimedOut;
                    else if (rejected == "Cancelled") result.Outcome = WindowMoveWorker.Outcome.Cancelled;
                    else if (rejected == "noMutation") result.MutationAttempted = false;
                    else if (rejected == "callerLive") result.CallerExited = false;
                    else if (rejected == "activeWorker") activeWorkers = 1;
                    else if (rejected == "uncertainWorker") uncertainWorkers = 1;
                    else if (rejected == "queryLease") Set(fixture.Lease, "Mutation", false);
                    else if (rejected == "unauthorized") Set(fixture.Lease, "Authorized", false);
                    else if (rejected == "staleToken") token--;
                    else if (rejected == "staleGeneration") Set(fixture.Broker, "reconnectRearmGeneration", fixture.Generation - 1);
                    else if (rejected == "otherSession") Set(fixture.Broker, "reconnectRearmSession", New("LaunchSession"));
                    else if (rejected == "unverifiedSession") Set(fixture.Session, "ImageVerified", false);
                    else if (rejected == "retiredSession") Set(fixture.Session, "Retired", 1);
                    else if (rejected == "otherOperation") Set(fixture.Broker, "operation", New("Lease"));
                    else Set(fixture.Broker, "reconnectUsed", false);
                    fixture.Stage(result, activeWorkers, uncertainWorkers, token);
                    check(Get(fixture.Broker, "reconnectRearmLease") == null,
                        "Reconnect allowance cannot stage from an ineligible receipt: " + rejected);
                }
            using (var fixture = new Fixture()) {
                fixture.Stage(Success());
                check(Get(fixture.Broker, "reconnectRearmLease") == fixture.Lease && fixture.Used,
                    "Only the current authorized successful mutation stages its exact lease without replenishing before completion");
            }
        }

        static void CompletionFences(Action<bool, string> check)
        {
            foreach (bool callbackFirst in new[] { true, false }) using (var fixture = new Fixture()) {
                fixture.Stage(Success());
                if (callbackFirst) Set(fixture.Lease, "Complete", true);
                else Set(fixture.Lease, "Terminal", true);
                fixture.Refresh();
                check(fixture.Used, "A single completion publication cannot replenish reconnect: callbackFirst=" + callbackFirst);
                Set(fixture.Lease, "Complete", true); Set(fixture.Lease, "Terminal", true);
                fixture.Refresh();
                check(fixture.Used, "Terminal and callback flags cannot bypass the pending callback count or operation slot");
                Set(fixture.Broker, "pendingOperations", 0); fixture.Refresh();
                check(fixture.Used, "Actual callback completion cannot replace clearing the matching operation slot");
                Set(fixture.Broker, "operation", null); fixture.Refresh();
                check(!fixture.Used && (int)Get(fixture.Broker, "reconnectRearmGeneration") == -1 &&
                    Get(fixture.Broker, "reconnectRearmSession") == null && Get(fixture.Broker, "reconnectRearmLease") == null &&
                    !(bool)Get(fixture.Broker, "shutdownConfirmed") && fixture.Broker.LastIncident.Contains("MainValidity"),
                    "A drained successful operation in the new active session rearms once without requiring inactive-session shutdown confirmation or losing prior evidence");
                Set(fixture.Broker, "reconnectUsed", true); fixture.Refresh();
                check(fixture.Used, "Repeated GUI polls cannot reuse an already consumed success stamp");
            }
        }

        static void RearmGuards(Action<bool, string> check)
        {
            foreach (string blocked in new[] { "inactive", "enabling", "disposed", "setup", "permissionChange", "uncertain", "pendingShutdown", "pendingCallback",
                "operationSlot", "staleGeneration", "otherSession", "unverifiedSession", "retiredSession", "worker" })
                using (var fixture = new Fixture()) {
                    fixture.Stage(Success()); fixture.Complete();
                    if (blocked == "inactive") Set(fixture.Broker, "active", false);
                    else if (blocked == "enabling") Set(fixture.Broker, "enabling", true);
                    else if (blocked == "disposed") Set(fixture.Broker, "disposed", true);
                    else if (blocked == "setup") Set(fixture.Broker, "setupPending", true);
                    else if (blocked == "permissionChange") Set(fixture.Broker, "permissionChangePending", true);
                    else if (blocked == "uncertain") Set(fixture.Broker, "uncertain", true);
                    else if (blocked == "pendingShutdown") Set(fixture.Broker, "pendingShutdowns", 1);
                    else if (blocked == "pendingCallback") Set(fixture.Broker, "pendingOperations", 1);
                    else if (blocked == "operationSlot") Set(fixture.Broker, "operation", fixture.Lease);
                    else if (blocked == "staleGeneration") Set(fixture.Broker, "generation", fixture.Generation + 1);
                    else if (blocked == "otherSession") Set(fixture.Broker, "launchSession", New("LaunchSession"));
                    else if (blocked == "unverifiedSession") Set(fixture.Session, "ImageVerified", false);
                    else if (blocked == "retiredSession") Set(fixture.Session, "Retired", 1);
                    int oldWorkers = WindowMoveWorker.UncertainHelpers;
                    try {
                        if (blocked == "worker") typeof(WindowMoveWorker).GetField("uncertainHelpers", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 1);
                        fixture.Refresh();
                        check(fixture.Used, "A staged success cannot bypass the reconnect completion guard: " + blocked);
                    } finally {
                        if (blocked == "worker") typeof(WindowMoveWorker).GetField("uncertainHelpers", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, oldWorkers);
                        Set(fixture.Broker, "setupPending", false); Set(fixture.Broker, "permissionChangePending", false);
                    }
                }
        }

        static void RepeatedCancellation(Action<bool, string> check)
        {
            using (var fixture = new Fixture()) {
                fixture.Complete(); Set(fixture.Broker, "reconnectUsed", false); fixture.Candidate();
                check((bool)Call(fixture.Broker, "TryTakeReconnect", true, true) && fixture.Used,
                    "First independently drained foreground cancellation consumes one reconnect reservation");
                fixture.Connected(73); fixture.Stage(Success()); fixture.Complete(); fixture.Refresh();
                check(!fixture.Used, "An eligible validated mutation receipt replenishes the new session's single allowance");
                fixture.Candidate();
                check((bool)Call(fixture.Broker, "TryTakeReconnect", true, true) && fixture.Used,
                    "A second sequential foreground cancellation can reserve after intervening successful work");
                fixture.Connected(74); fixture.Complete(); fixture.Refresh(); fixture.Candidate();
                check(!(bool)Call(fixture.Broker, "TryTakeReconnect", true, true) && fixture.Used,
                    "A connected generation without a qualifying mutation receipt cannot create an automatic reconnect loop");
            }
        }

        static void ResetInvalidates(Action<bool, string> check)
        {
            foreach (bool disable in new[] { false, true }) using (var fixture = new Fixture()) {
                fixture.Stage(Success()); fixture.Complete();
                // Clear the synthetic active session before invoking ordinary
                // Disable; no fake session may enter the retirement worker.
                Set(fixture.Broker, "launchSession", null); Set(fixture.Broker, "active", false);
                if (disable) fixture.Broker.Disable(); else Call(fixture.Broker, "ResetReconnect");
                check((int)Get(fixture.Broker, "reconnectRearmGeneration") == -1 &&
                    Get(fixture.Broker, "reconnectRearmSession") == null && Get(fixture.Broker, "reconnectRearmLease") == null,
                    "Explicit permission lifecycle invalidates every prior success stamp: disable=" + disable);
            }
        }
    }
}
