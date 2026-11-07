using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskbarSelect
{
    // Pure in-memory backend only. No real broker, HWND, task, protected cache,
    // profile settings, Startup state, permission prompt or process is used.
    internal static class AdministratorPermissionSwitchTests
    {
        sealed class Backend : AdministratorPermissionSwitch.IBackend
        {
            internal bool IsActive, IsEnabling, IsUncertain, HasSavedApproval, IsRemembered, RemovalRequired, Blocked;
            internal bool InstallSucceeds = true, RemoveSucceeds = true, PartialInstall, RememberedStarts = true;
            internal string Failure = "";
            internal Action<string> Calling;
            internal readonly List<string> Calls = new List<string>();
            internal readonly Queue<bool> StopResults = new Queue<bool>();
            internal readonly Dictionary<string, int> Throws = new Dictionary<string, int>();
            public bool Active { get { return IsActive; } }
            public bool Enabling { get { return IsEnabling; } }
            public bool Uncertain { get { return IsUncertain; } }
            public bool SavedApproval { get { return HasSavedApproval; } }
            public bool Remembered { get { return IsRemembered; } }
            public string Error { get { return Failure; } }
            void Call(string name)
            {
                Calls.Add(name);
                var callback = Calling;
                if (callback != null) callback(name);
                int remaining;
                if (Throws.TryGetValue(name, out remaining) && remaining > 0) {
                    Throws[name] = remaining - 1;
                    throw new InvalidOperationException("injected " + name + " failure");
                }
            }
            public void Remember(bool value) { Call("Remember:" + value); IsRemembered = value; }
            public void BlockOperations(bool value) { Call("Block:" + value); Blocked = value; }
            public bool RequiresRemoval() { Call("Probe"); return RemovalRequired; }
            public bool Stop()
            {
                Call("Stop");
                bool succeeded = StopResults.Count == 0 || StopResults.Dequeue();
                if (succeeded) { IsActive = IsEnabling = IsUncertain = false; }
                return succeeded;
            }
            public void EnableManual() { Call("Manual"); IsEnabling = true; }
            public void EnableRemembered() { Call("Remembered"); IsEnabling = RememberedStarts; }
            public bool Install()
            {
                Call("Install");
                if (InstallSucceeds) { HasSavedApproval = true; RemovalRequired = true; }
                else if (PartialInstall) RemovalRequired = true;
                return InstallSucceeds;
            }
            public bool Remove()
            {
                Call("Remove");
                if (RemoveSucceeds) { HasSavedApproval = false; RemovalRequired = false; }
                return RemoveSucceeds;
            }
            internal void ApproveManual() { IsEnabling = false; IsActive = true; Failure = ""; }
            internal void ApproveRemembered() { ApproveManual(); HasSavedApproval = true; }
            internal void CancelApproval() { IsEnabling = IsActive = false; Failure = "Windows approval cancelled"; }
            internal int Count(string name) { return Calls.Count(value => value == name); }
            internal string[] Flow { get { return Calls.Where(value => !value.StartsWith("Block:", StringComparison.Ordinal)).ToArray(); } }
        }

        static Backend Approved()
        { return new Backend { IsActive = true, IsRemembered = true, HasSavedApproval = true, RemovalRequired = true }; }

        internal static string LongFailureDetail()
        {
            var backend = Approved();
            backend.Failure = String.Concat(Enumerable.Repeat("Owned diagnostic has not confirmed helper shutdown; ", 11));
            backend.StopResults.Enqueue(false);
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(false);
                return change.Detail;
            }
        }

        static void Sequence(Action<bool, string> check, Backend backend, string description, params string[] expected)
        { check(backend.Flow.SequenceEqual(expected), description + "; observed=" + String.Join(",", backend.Flow)); }

        internal static void Run(Action<bool, string> check)
        {
            ConstructorAndStartup(check);
            EnableAndReconnect(check);
            CompleteOff(check);
            CancelAndRetry(check);
            FailureBoundaries(check);
            ReentrantRequests(check);
            ExitFences(check);
            SettingsIntent(check);
            DisconnectedPresentation(check);
        }

        static void DisconnectedPresentation(Action<bool, string> check)
        {
            var backend = new Backend();
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(true); backend.ApproveManual(); change.Poll();
                check(change.Status == "On" && change.Detail.Contains("Approval is remembered"), "Verified successful On initially presents remembered support");
                backend.IsActive = false; backend.Failure = "Owned foreground cancellation before authorization";
                for (int tick = 0; tick < 3; tick++) {
                    change.Poll();
                    check(change.Status == "Disconnected" && change.Checked && change.Detail.Contains(backend.Failure) &&
                        change.Detail.Contains("Saved approval is retained") && !change.Detail.Contains("Approval is remembered for this build. Start"),
                        "A later disconnect replaces historical success text with the current failure without blaming saved approval");
                }
            }
        }
        static void ConstructorAndStartup(Action<bool, string> check)
        {
            bool rejected = false;
            try { new AdministratorPermissionSwitch(null); } catch (ArgumentNullException) { rejected = true; }
            check(rejected, "Permission switch requires a backend without creating any real permission capability");
            foreach (bool remembered in new[] { false, true }) foreach (bool cache in new[] { false, true }) {
                var backend = new Backend { IsRemembered = remembered, RemovalRequired = cache };
                using (var change = new AdministratorPermissionSwitch(backend)) {
                    check(backend.Calls.Count == 0, "Constructing the switch performs no permission or protected-state operation");
                    for (int repeat = 0; repeat < 4; repeat++) change.Poll();
                    Sequence(check, backend, "Startup polling only observes approval and never requests fresh Windows permission", "Probe");
                    check(!change.Busy && change.Checked == (remembered || cache) && backend.Count("Manual") == 0 &&
                        backend.Count("Install") == 0 && backend.Count("Remembered") == 0,
                        "Startup cannot silently fall back to manual approval even when remembered support is missing");
                }
            }
            var failed = new Backend(); failed.Throws["Probe"] = 1;
            using (var change = new AdministratorPermissionSwitch(failed)) {
                change.Poll(); change.Poll();
                check(change.Checked && !change.Busy && change.Status == "Needs attention" && change.Detail.Contains("could not be checked"),
                    "Unknown approval state is retained as checked and cannot masquerade as clean Off");
                Sequence(check, failed, "A failed startup observation cannot create a helper or prompt", "Probe");
            }
        }

        static void EnableAndReconnect(Action<bool, string> check)
        {
            var backend = new Backend();
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(true);
                Sequence(check, backend, "Explicit clean On observes existing approval and stops the previous session before requesting permission", "Probe", "Stop", "Manual");
                check(change.Busy && change.Checked && backend.Blocked && change.Status == "Waiting for Windows approval" && !backend.IsRemembered,
                    "Pending On is checked and blocks elevated operations without prematurely saving remembered intent");
                change.Poll(); change.Request(true); change.Request(false);
                check(backend.Count("Manual") == 1 && backend.Count("Install") == 0 && backend.Count("Remember:True") == 0,
                    "Pending permission cannot be installed twice or replaced by another switch request");
                backend.ApproveManual(); change.Poll();
                Sequence(check, backend, "Approved On installs protected approval before saving the remembered preference", "Probe", "Stop", "Manual", "Install", "Remember:True");
                check(!change.Busy && change.Checked && change.Status == "On" && backend.IsRemembered && backend.HasSavedApproval &&
                    !backend.Blocked && change.ReconnectAllowed,
                    "Only authenticated installed and saved support becomes remembered On and unblocks operations");
                int completed = backend.Calls.Count;
                for (int repeat = 0; repeat < 3; repeat++) change.Poll();
                check(backend.Calls.Count == completed, "Completed On does not repeat install or save during later polling");
                change.Request(true);
                check(backend.Count("Manual") == 1 && backend.Count("Install") == 1 && backend.Count("Remember:True") == 1,
                    "An explicit repeated On against verified remembered support is idempotent and creates no UAC prompt");
            }
            backend = new Backend { IsRemembered = true, RemovalRequired = true };
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(true);
                Sequence(check, backend, "Explicit On first uses existing remembered approval", "Probe", "Remembered");
                backend.ApproveRemembered(); change.Poll();
                check(change.Status == "On" && !change.Busy && !backend.Blocked && backend.Count("Manual") == 0 &&
                    backend.Count("Stop") == 0 && backend.Count("Install") == 0 && backend.Count("Remember:True") == 0,
                    "A valid remembered reconnect does not request UAC, reinstall or rewrite settings");
            }
            backend = new Backend { IsRemembered = true, RemovalRequired = true, RememberedStarts = false };
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(true);
                Sequence(check, backend, "Only an explicit On request may fall back from failed remembered reconnect to fresh approval", "Probe", "Remembered", "Stop", "Manual");
                check(change.Busy && backend.Count("Install") == 0, "Fallback still waits for Windows approval before install");
            }
        }

        static void CompleteOff(Action<bool, string> check)
        {
            var backend = new Backend();
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(false);
                Sequence(check, backend, "Clean Off saves intent and confirms stopped helper plus absent approval without requesting UAC", "Remember:False", "Stop", "Probe");
                check(!change.Checked && !change.Busy && change.Status == "Off" && !change.ReconnectAllowed && backend.Blocked &&
                    change.Detail.Contains("saved approval is removed"), "Confirmed clean Off cannot reconnect or retain elevated operation capability");
            }
            backend = Approved();
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(false);
                Sequence(check, backend, "Approved Off records Off before stopping, inspecting and requesting removal permission", "Remember:False", "Stop", "Probe", "Manual");
                check(change.Busy && change.Checked && !backend.IsRemembered && backend.Blocked && backend.Count("Remove") == 0,
                    "Off waits for cleanup approval while retaining a checked switch for the outstanding saved record");
                backend.ApproveManual(); change.Poll();
                Sequence(check, backend, "Approved Off removes approval, stops the maintenance session and verifies absence before reporting Off",
                    "Remember:False", "Stop", "Probe", "Manual", "Remove", "Stop", "Probe");
                check(!change.Checked && !change.Busy && change.Status == "Off" && !backend.IsActive && !backend.HasSavedApproval &&
                    !backend.RemovalRequired && !backend.IsRemembered && !change.ReconnectAllowed && backend.Blocked,
                    "One successful Off clears saved approval and the current helper instead of leaving a session behind");
                int completed = backend.Calls.Count; change.Poll(); change.Poll();
                check(backend.Calls.Count == completed, "Finished Off cannot repeat maintenance or request another prompt on status ticks");
            }
            backend = new Backend { IsActive = true };
            using (var change = new AdministratorPermissionSwitch(backend)) {
                check(change.Checked && change.Status == "On \u2014 this session only", "Legacy session-only support remains accurately labeled");
                change.Request(false);
                check(!change.Checked && backend.Count("Manual") == 0 && backend.Count("Remove") == 0 && !backend.IsActive,
                    "An active legacy session with positively absent approval turns fully off without a needless UAC prompt");
            }
        }

        static void CancelAndRetry(Action<bool, string> check)
        {
            var backend = new Backend();
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(true); backend.CancelApproval(); change.Poll();
                check(!change.Checked && !change.Busy && change.Status == "Off" && !backend.IsRemembered &&
                    backend.Count("Install") == 0 && backend.Count("Remember:True") == 0 && change.Detail.Contains("cancelled"),
                    "Cancelled clean On confirms absence and returns unchecked without fake saved approval");
            }
            backend = Approved();
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(false); backend.CancelApproval(); change.Poll();
                check(change.Checked && !change.Busy && change.Status == "Off not confirmed" && !backend.IsRemembered &&
                    backend.RemovalRequired && backend.Count("Remove") == 0 && change.Detail.Contains("Try switching off again"),
                    "Cancelled Off retains a checked retry state and the record while keeping automatic reconnect intent disabled");
                change.Request(false); backend.ApproveManual(); change.Poll();
                check(!change.Checked && change.Status == "Off" && backend.Count("Manual") == 2 && backend.Count("Remove") == 1,
                    "Toggling Off again retries cancelled cleanup and can finish without a separate Remove control");
            }
        }

        static void FailureBoundaries(Action<bool, string> check)
        {
            var backend = new Backend(); backend.Throws["Block:True"] = 1;
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(true);
                check(!change.Busy && change.Checked && change.Status == "Needs attention" && backend.Count("Manual") == 0 && backend.Count("Install") == 0,
                    "An operation-blocking exception cannot strand Busy or enter a permission change");
                change.Request(false);
                check(!change.Busy && !change.Checked && change.Status == "Off",
                    "A failed operation-blocking attempt remains retryable rather than permanently wedging the switch");
            }
            backend = Approved(); backend.Throws["Remember:False"] = 1;
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(false);
                check(change.Checked && !change.Busy && change.Status == "Off not confirmed" && backend.IsRemembered &&
                    backend.Count("Stop") == 0 && backend.Count("Manual") == 0 && backend.Blocked,
                    "Off preference-save failure cannot stop or remove support under an uncommitted request");
            }
            backend = Approved(); backend.StopResults.Enqueue(false);
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(false);
                check(change.Checked && change.Status == "Off not confirmed" && !backend.IsRemembered && backend.IsActive &&
                    backend.Count("Probe") == 0 && backend.Count("Manual") == 0,
                    "Unconfirmed initial shutdown retains checked recovery and cannot start a competing cleanup helper");
            }
            backend = Approved(); backend.Throws["Probe"] = 1;
            using (var change = new AdministratorPermissionSwitch(backend)) {
                change.Request(false);
                check(change.Checked && change.Status == "Off not confirmed" && backend.Count("Manual") == 0 && backend.Count("Remove") == 0,
                    "Unreadable saved-approval state refuses removal admission and cannot report Off");
            }
            foreach (string failure in new[] { "RemoveFalse", "RemoveThrows", "FinalStop", "FinalProbe", "StillRegistered", "StillUncertain" }) {
                backend = Approved();
                if (failure == "RemoveFalse") backend.RemoveSucceeds = false;
                if (failure == "RemoveThrows") backend.Throws["Remove"] = 1;
                if (failure == "FinalStop") { backend.StopResults.Enqueue(true); backend.StopResults.Enqueue(false); }
                if (failure == "FinalProbe" || failure == "StillRegistered" || failure == "StillUncertain") {
                    Backend captured = backend;
                    backend.Calling = delegate(string call) {
                        if (call == "Probe" && captured.Count("Probe") == 2) {
                            if (failure == "FinalProbe") throw new InvalidOperationException("injected final absence query failure");
                            if (failure == "StillRegistered") captured.RemovalRequired = true;
                            if (failure == "StillUncertain") captured.IsUncertain = true;
                        }
                    };
                }
                using (var change = new AdministratorPermissionSwitch(backend)) {
                    change.Request(false); backend.ApproveManual(); change.Poll();
                    check(change.Checked && !change.Busy && change.Status == "Off not confirmed" && backend.Blocked &&
                        !change.ReconnectAllowed && !backend.IsRemembered && change.Detail.Contains("switch"),
                        "Off cannot claim success after " + failure + "; recovery and explicit retry remain available");
                }
            }
            foreach (string failure in new[] { "InstallFalse", "InstallThrows", "RememberThrows", "InitialStop" }) {
                backend = new Backend();
                if (failure == "InstallFalse") { backend.InstallSucceeds = false; backend.PartialInstall = true; }
                if (failure == "InstallThrows") backend.Throws["Install"] = 1;
                if (failure == "RememberThrows") backend.Throws["Remember:True"] = 1;
                if (failure == "InitialStop") backend.StopResults.Enqueue(false);
                using (var change = new AdministratorPermissionSwitch(backend)) {
                    change.Request(true);
                    if (failure != "InitialStop") { backend.ApproveManual(); change.Poll(); }
                    check(change.Checked && !change.Busy && change.Status == "Needs attention" && backend.Blocked && !backend.IsRemembered,
                        "On cannot claim remembered success after " + failure + "; potentially partial approval stays checked");
                    check(backend.Count("Block:False") == 0, "Failed On never re-enables elevated operations before admission is complete");
                    if (failure == "InitialStop") check(backend.Count("Manual") == 0 && backend.Count("Install") == 0,
                        "An unconfirmed previous helper blocks a new manual permission request");
                }
            }
        }

        static void ReentrantRequests(Action<bool, string> check)
        {
            foreach (bool enabling in new[] { false, true }) {
                var backend = enabling ? new Backend() : Approved();
                using (var change = new AdministratorPermissionSwitch(backend)) {
                    backend.Calling = delegate(string call) {
                        if (call == "Stop" || call == "Install" || call == "Remove" || call.StartsWith("Remember:", StringComparison.Ordinal)) {
                            change.Request(!enabling); change.Request(enabling); change.Poll();
                        }
                    };
                    change.Request(enabling);
                    change.Request(enabling); change.Request(!enabling); change.Poll();
                    backend.ApproveManual(); change.Poll();
                    check(backend.Count("Manual") == 1 && backend.Count(enabling ? "Install" : "Remove") == 1 &&
                        backend.Count(enabling ? "Remember:True" : "Remember:False") == 1 && !change.Busy,
                        "DoEvents-style reentrant requests and polls cannot consume the same On or Off transition twice");
                    check(change.Checked == enabling && change.Status == (enabling ? "On" : "Off"),
                        "Ignoring reentrant toggles preserves the original requested transition");
                }
            }
        }

        static void ExitFences(Action<bool, string> check)
        {
            foreach (bool dispose in new[] { false, true }) foreach (bool enabling in new[] { false, true }) {
                var backend = enabling ? new Backend() : Approved();
                var change = new AdministratorPermissionSwitch(backend);
                bool cancelled = false;
                backend.Calling = delegate(string call) {
                    if (call != "Stop" || cancelled) return;
                    cancelled = true;
                    if (dispose) change.Dispose(); else change.CancelForExit();
                    change.Poll();
                };
                change.Request(enabling);
                check(cancelled && !change.Busy && !change.ReconnectAllowed && backend.Blocked && backend.Count("Manual") == 0 &&
                    backend.Count("Install") == 0 && backend.Count("Remove") == 0 && backend.Count("Remember:True") == 0,
                    "Exit cancellation during Stop invalidates the old transition before any approval, install or enabled preference save");
                check(backend.Count("Remember:False") == (enabling ? 0 : 1),
                    "Exit fencing preserves only the explicit Off intent saved before shutdown began");
                int terminal = backend.Calls.Count;
                int mutating = backend.Calls.Count(call => call != "Probe");
                change.Poll();
                if (dispose) { change.Request(true); change.Request(false); }
                check(dispose ? backend.Calls.Count == terminal : backend.Calls.Count(call => call != "Probe") == mutating,
                    "Stale polling cannot revive a cancelled generation; disposal also refuses every later request");
                if (!dispose) change.Dispose();
            }
        }

        static void SettingsIntent(Action<bool, string> check)
        {
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            foreach (bool remembered in new[] { false, true }) {
                var current = new Settings { PersistentAdministratorSupport = remembered, AccentPreset = "Teal" };
                var staleEdit = new Settings { PersistentAdministratorSupport = !remembered, AccentPreset = "Coral", WindowShelfActivationWidth = 720 };
                staleEdit.MonitorAliases["neutral-monitor"] = "Neutral alias";
                var expected = staleEdit.Copy(); expected.PersistentAdministratorSupport = remembered;
                string currentBefore = serializer.Serialize(current), expectedEdit = serializer.Serialize(expected);
                Controller.PreserveAdministratorPreference(staleEdit, current);
                check(serializer.Serialize(staleEdit) == expectedEdit && serializer.Serialize(current) == currentBefore,
                    "Ordinary settings edits preserve current permission intent while retaining every unrelated edited field");
                var history = new SettingsHistory();
                history.Push(new Settings { PersistentAdministratorSupport = !remembered, WindowShelfHoverDelayMs = 375 }, null, "Neutral settings Undo");
                Controller.PreserveAdministratorPreference(history.Peek.Settings, current);
                check(history.Peek.Settings.PersistentAdministratorSupport == remembered && history.Peek.Settings.WindowShelfHoverDelayMs == 375,
                    "Settings Undo cannot restore stale permission intent or discard the unrelated historical preference");
            }
            foreach (bool missingNext in new[] { false, true }) {
                bool rejected = false;
                try { Controller.PreserveAdministratorPreference(missingNext ? null : new Settings(), missingNext ? new Settings() : null); }
                catch (ArgumentNullException) { rejected = true; }
                check(rejected, "Permission-intent preservation rejects missing settings without writing any state");
            }
        }
    }
}
