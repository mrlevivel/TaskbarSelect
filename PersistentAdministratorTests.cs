using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TaskbarSelect
{
    // Only managed policy and an in-memory scheduler. No tasks, files, tokens,
    // application entry, UAC, windows, permissions or Startup are changed.
    internal static class PersistentAdministratorTests
    {
        const string Sid = "S-1-5-21-11-22-33-1001";
        const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string NativeHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        const string Nonce = "74a5a692966c4c07a97e374ed87a066e";
        const string Instance = "74a5a692-966c-4c07-a97e-374ed87a066e";
        const string Volume = "ff581a80-4a32-4ec1-9f00-31c0b177725f";
        internal static void Run(Action<bool, string> check)
        { Scopes(check); Ledgers(check); Tasks(check); StableVolume(check); Ancestors(check); LeaseLifecycle(check); RegistrationLifecycle(check); }
        static void Scopes(Action<bool, string> check)
        {
            string first = PersistentAdministrator.ComputeScope(Sid, @"C:\Owned\Alpha", true);
            check(AdministratorLaunch.ValidHash(first) && first == PersistentAdministrator.ComputeScope(Sid, @"c:\owned\alpha", true), "Persistent scope is a canonical lowercase SHA256 and stable across Windows path casing");
            check(first != PersistentAdministrator.ComputeScope(Sid, @"C:\Owned\Alpha", false) && first != PersistentAdministrator.ComputeScope(Sid, @"C:\Owned\Beta", true) &&
                first != PersistentAdministrator.ComputeScope("S-1-5-21-11-22-33-1002", @"C:\Owned\Alpha", true), "User, profile and Production/Tests domains never share the same persistent approval scope");
            check(PersistentAdministrator.TaskName(first) == "Persistent-" + first && !PersistentAdministrator.TaskName(first).Contains("Alpha"), "Task names expose only the fixed namespace and opaque scope");
            foreach (string bad in new[] { null, "", "relative", @"C:relative", @"C:\", @"\\server\share\profile", @"\\?\C:\profile", @"C:\owned\..\profile", @"C:\owned\.\profile", @"C:\owned\profile:stream", @"C:/owned/profile" })
                check(Throws<ArgumentException>(delegate { PersistentAdministrator.ComputeScope(Sid, bad, true); }), "Invalid aliases, roots, UNC/device and relative profile identities cannot become persistent scopes");
            foreach (string bad in new[] { null, "", "not-a-sid", " " + Sid, Sid + " ", Sid + "\n" })
                check(Throws<ArgumentException>(delegate { PersistentAdministrator.ComputeScope(bad, @"C:\Owned\Alpha", true); }), "Malformed or noncanonical user identities cannot acquire a persistent scope");
            foreach (string bad in new[] { null, "", Hash.ToUpperInvariant(), Hash + "0", "..\\" + Hash })
                check(Throws<ArgumentException>(delegate { PersistentAdministrator.TaskName(bad); }), "Task lookup accepts only one canonical fixed-length scope, never paths or names");
        }
        static void Ledgers(Action<bool, string> check)
        {
            var input = new PersistentAdministrator.Ledger { Scope = Hash, ControllerHash = NativeHash, NativeHash = Hash, Sid = Sid };
            byte[] bytes = PersistentAdministrator.EncodeLedger(input); var output = PersistentAdministrator.DecodeLedger(bytes);
            check(output.Scope == input.Scope && output.ControllerHash == input.ControllerHash && output.NativeHash == input.NativeHash && output.Sid == Sid,
                "Binary approval ledger round-trips exact scope, approved controller, native image and canonical user SID");
            check(Encoding.ASCII.GetString(bytes, 0, 8) == "TSAPR01\0" && bytes.Length == 136 && BitConverter.ToUInt32(bytes, 104) == 28,
                "Native ledger uses exact versioned magic, fixed offsets and bounded canonical SID bytes");
            check(bytes[8] == 0x01 && bytes[40] == 0xFE && bytes[72] == 0x01 && bytes[108] == 1,
                "The scope/controller/native/SID offsets agree with the native parser without paths or commands");
            foreach (int length in new[] { 0, 107, 119, 177 }) check(Throws<IOException>(delegate { PersistentAdministrator.DecodeLedger(new byte[length]); }), "Truncated or overbound ledger lengths fail closed");
            check(Throws<IOException>(delegate { PersistentAdministrator.DecodeLedger(null); }), "An absent ledger cannot authorize a scheduled action");
            var extra = new byte[bytes.Length + 1]; Array.Copy(bytes, extra, bytes.Length);
            check(Throws<IOException>(delegate { PersistentAdministrator.DecodeLedger(extra); }), "Trailing bytes cannot smuggle another registration field");
            foreach (int offset in new[] { 0, 7, 104, 105, 108, 109 }) {
                byte[] changed = (byte[])bytes.Clone(); changed[offset] = 0xFF;
                check(Throws<Exception>(delegate { PersistentAdministrator.DecodeLedger(changed); }), "Changed magic, SID size/revision/count must reject before task admission");
            }
            input.ControllerHash = "bad";
            check(Throws<ArgumentException>(delegate { PersistentAdministrator.EncodeLedger(input); }), "Registration cannot serialize an unbound controller image identity");
            input.ControllerHash = NativeHash; input.Sid = "bad";
            check(Throws<ArgumentException>(delegate { PersistentAdministrator.EncodeLedger(input); }), "Registration cannot serialize malformed user identity");
        }
        static PersistentAdministrator.TaskDefinition Spec()
        { return PersistentAdministrator.ExpectedTask(Hash, Sid, @"\\?\GLOBALROOT\GLOBAL??\Volume{" + Volume + @"}\ProgramData\TaskbarSelect.Persistent\" + Hash + "\\AdministratorBootstrap.exe"); }
        static PersistentAdministrator.TaskDefinition Clone(PersistentAdministrator.TaskDefinition value)
        { return (PersistentAdministrator.TaskDefinition)typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(value, null); }
        static void Tasks(Action<bool, string> check)
        {
            var expected = Spec();
            check(PersistentAdministrator.SameTask(Clone(expected), expected) && expected.LogonType == 3 && expected.RunLevel == 1 && expected.Triggers == 0,
                "Only a protected interactive-token Highest on-demand task matches; no startup trigger is created");
            check(expected.Priority == 4, "The interactive scheduled helper explicitly uses Normal priority rather than the Scheduler background default");
            foreach (int priority in new[] { Int32.MinValue, -1, 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, Int32.MaxValue }) {
                var altered = Clone(expected); altered.Priority = priority;
                check(!PersistentAdministrator.SameTask(altered, expected), "Any different saved task priority is refused, never silently repaired: " + priority);
            }
            var missingPriority = Clone(expected); missingPriority.Priority = null;
            check(!PersistentAdministrator.SameTask(missingPriority, expected), "Missing scheduling metadata cannot default to an approved Normal task");
            var forgedExpected = Clone(expected); forgedExpected.Priority = 7;
            check(!PersistentAdministrator.SameTask(expected, forgedExpected) && !PersistentAdministrator.SameTask(forgedExpected, forgedExpected),
                "An altered expected definition cannot admit the old background priority or override the fixed policy");
            check(expected.Arguments == "--scheduled-administrator-bootstrap " + Hash + " $(Arg0) $(Arg1) $(Arg2)" && expected.Path.StartsWith(@"\\?\GLOBALROOT\GLOBAL??\Volume{", StringComparison.Ordinal),
                "The action is one persistent global-volume protected native image with exactly three fixed runtime parameter slots");
            foreach (Action<PersistentAdministrator.TaskDefinition> mutate in new Action<PersistentAdministrator.TaskDefinition>[] {
                delegate(PersistentAdministrator.TaskDefinition x) { x.Name += "-other"; }, delegate(PersistentAdministrator.TaskDefinition x) { x.Path = @"C:\Users\Owned\TaskbarSelect.exe"; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.Arguments += " --uninstall"; }, delegate(PersistentAdministrator.TaskDefinition x) { x.WorkingDirectory = @"C:\Users\Owned"; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.Sid = "S-1-5-21-11-22-33-1002"; }, delegate(PersistentAdministrator.TaskDefinition x) { x.Actions = 2; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.ActionType = 5; }, delegate(PersistentAdministrator.TaskDefinition x) { x.Triggers = 1; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.LogonType = 1; }, delegate(PersistentAdministrator.TaskDefinition x) { x.RunLevel = 0; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.MultipleInstances = 0; }, delegate(PersistentAdministrator.TaskDefinition x) { x.Enabled = false; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.AllowDemandStart = false; }, delegate(PersistentAdministrator.TaskDefinition x) { x.Hidden = false; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.BatteryBlocked = true; }, delegate(PersistentAdministrator.TaskDefinition x) { x.BatteryStops = true; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.NetworkRequired = true; }, delegate(PersistentAdministrator.TaskDefinition x) { x.IdleRequired = true; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.Wake = true; }, delegate(PersistentAdministrator.TaskDefinition x) { x.StartWhenAvailable = true; },
                delegate(PersistentAdministrator.TaskDefinition x) { x.ExecutionLimit = "PT30S"; }, delegate(PersistentAdministrator.TaskDefinition x) { x.Security = null; } }) {
                var changed = Clone(expected); mutate(changed); check(!PersistentAdministrator.SameTask(changed, expected), "An altered action/principal/trigger/instance/lifetime/security contract is never repaired or executed");
            }
            // GetSddlForm canonicalizes GRGX to GXGR; mutate the explicit input
            // so every negative fixture actually changes the intended boundary.
            string exact = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;" + Sid + ")";
            int fixture = 0;
            foreach (string changed in new[] { exact.Replace("O:BA", "O:" + Sid), exact.Replace("D:P", "D:"), exact + "(A;;FA;;;WD)",
                exact.Replace("GRGX;;;" + Sid, "FA;;;" + Sid), exact.Replace("GRGX;;;" + Sid, "GR;;;" + Sid),
                exact.Replace("(A;;GRGX;;;" + Sid + ")", ""), exact + "(A;;FA;;;BA)", exact.Replace("(A;;", "(A;CI;") })
                check(changed != exact && !PersistentAdministrator.ProtectedTaskSecurity(new RawSecurityDescriptor(changed), Sid), "User ownership/write grants, missing execute, duplicate trustees and inheritance cannot pass task protection (fixture " + fixture++ + ")");
            int resolved = 0;
            Func<string, string> account = delegate(string value) { resolved++; return value == "LabAdmin" ? Sid : "S-1-5-21-11-22-33-1002"; };
            check(PersistentAdministrator.TaskPrincipalSid(Sid, account) == Sid && resolved == 0,
                "A saved canonical SID remains exact without an account lookup");
            var named = Clone(expected); named.Sid = PersistentAdministrator.TaskPrincipalSid("LabAdmin", account);
            check(resolved == 1 && PersistentAdministrator.SameTask(named, expected),
                "Task Scheduler's saved account name is accepted only through its exact canonical SID identity");
            named.Sid = PersistentAdministrator.TaskPrincipalSid("OtherAdmin", account);
            check(!PersistentAdministrator.SameTask(named, expected), "Another valid account SID cannot match the approved principal");
            foreach (string invalid in new[] { null, "", "not-a-sid", Sid + " " })
                check(PersistentAdministrator.TaskPrincipalSid("LabAdmin", delegate(string value) { return invalid; }) == null,
                    "Unresolved or noncanonical account lookup results cannot acquire the approved task identity");
            check(PersistentAdministrator.TaskPrincipalSid(null, account) == null && PersistentAdministrator.TaskPrincipalSid("", account) == null && resolved == 2 &&
                PersistentAdministrator.TaskPrincipalSid("MissingAccount", delegate(string value) { throw new IdentityNotMappedException(); }) == null,
                "Absent and unmapped principal names fail closed without a fallback identity");
            foreach (string bad in new[] { @"C:\ProgramData\AdministratorBootstrap.exe", @"\\server\share\AdministratorBootstrap.exe", @"\\?\C:\AdministratorBootstrap.exe",
                @"\\?\GLOBALROOT\Device\Mup\server\AdministratorBootstrap.exe", @"\\?\GLOBALROOT\Device\HarddiskVolume3\..\AdministratorBootstrap.exe" })
                check(!PersistentAdministrator.PhysicalAction(bad), "Scheduled execution cannot fall back to DOS aliases, network paths or traversal");
        }
        static void StableVolume(Action<bool, string> check)
        {
            string tail = @"ProgramData\TaskbarSelect.Persistent\" + Hash + "\\AdministratorBootstrap.exe";
            string input = @"\\?\Volume{" + Volume + "}\\" + tail;
            string action = PersistentAdministrator.StableGuidAction(input);
            check(action == @"\\?\GLOBALROOT\GLOBAL??\Volume{" + Volume + "}\\" + tail && PersistentAdministrator.PhysicalAction(action),
                "Only the retained file's stable volume GUID becomes the task action through the literal global namespace");
            check(PersistentAdministrator.StableGuidAction(input.Replace(Volume, Volume.ToUpperInvariant())) == action,
                "A handle-reported GUID casing variation normalizes without changing the selected volume or suffix");
            check(PersistentAdministrator.VolumeMappingMatches(action, @"\Device\HarddiskVolume3", @"\Device\HarddiskVolume3\" + tail) &&
                PersistentAdministrator.VolumeMappingMatches(action, @"\Device\HarddiskVolume7", @"\Device\HarddiskVolume7\" + tail),
                "A reboot may change the current device number while the same stored GUID action still proves the exact current image mapping");
            foreach (string invalid in new[] { null, "", @"C:\" + tail, @"\\?\C:\" + tail, @"\\?\GLOBALROOT\Device\HarddiskVolume3\" + tail,
                @"\\server\share\" + tail, @"\\?\Volume{not-a-guid}\" + tail, @"\\?\Volume{00000000-0000-0000-0000-000000000000}\" + tail,
                @"\\?\Volume{" + Volume + "}", @"\\?\Volume{" + Volume + "}\\", input + "\\", input.Replace("ProgramData", ".."),
                input.Replace("ProgramData", "."), input.Replace("ProgramData", "ProgramData:stream"), input.Replace('\\', '/') })
                check(PersistentAdministrator.StableGuidAction(invalid) == null, "DOS/UNC/device aliases, malformed GUIDs, roots and traversal never become persistent actions");
            foreach (string invalid in new[] { @"\\?\Volume{" + Volume + "}\\" + tail,
                @"\\?\GLOBALROOT\Device\HarddiskVolume3\" + tail, action.Replace("GLOBAL??", "??"), action.Replace("GLOBAL??", "Sessions\\2\\DosDevices"),
                action.Replace(Volume, Volume.ToUpperInvariant()), action.Replace("ProgramData", "..") })
                check(!PersistentAdministrator.PhysicalAction(invalid), "Saved task actions must use the one canonical global GUID spelling without local or session aliases");
            foreach (string target in new[] { null, "", @"C:", @"\Device\Mup", @"\??\C:", @"\Device\HarddiskVolume3\", @"\Device\HarddiskVolume3\other", @"\Device\HarddiskVolumeX" })
                check(!PersistentAdministrator.VolumeMappingMatches(action, target, @"\Device\HarddiskVolume3\" + tail),
                    "A volume link must target a single raw physical volume without another alias or suffix");
            check(!PersistentAdministrator.VolumeMappingMatches(action, @"\Device\HarddiskVolume4", @"\Device\HarddiskVolume3\" + tail) &&
                !PersistentAdministrator.VolumeMappingMatches(action, @"\Device\HarddiskVolume3", @"\Device\HarddiskVolume3\" + tail + ".other"),
                "A wrong current volume or a changed file suffix fails identity before scheduler execution");
            check(PersistentAdministrator.NamespaceRightsDenied(0x20003, true) && PersistentAdministrator.NamespaceRightsDenied(0x20001, false),
                "Existing global directory query/traverse and volume-link read rights are compatible with mutation denial");
            check(PersistentAdministrator.RootNamespaceTarget("") && PersistentAdministrator.RootNamespaceTarget("\\"),
                "The exact protected GLOBALROOT link may use Windows' empty or explicit root representation");
            foreach (string target in new[] { null, @"\??", @"\GLOBAL??", @"\Device", " ", "\\\\", "\\\0" })
                check(!PersistentAdministrator.RootNamespaceTarget(target), "GLOBALROOT never accepts another namespace or ambiguous root target");
            foreach (uint right in new uint[] { 0x10000, 0x40000, 0x80000, 4, 8 })
                check(!PersistentAdministrator.NamespaceRightsDenied(0x20003 | right, true), "Any effective MED delete, owner, DACL or directory-create grant blocks the persistent namespace");
            foreach (uint right in new uint[] { 0x10000, 0x40000, 0x80000, 2 })
                check(!PersistentAdministrator.NamespaceRightsDenied(0x20001 | right, false), "Any effective MED delete, owner, DACL or volume-link mutation grant blocks execution");
        }
        static void Ancestors(Action<bool, string> check)
        {
            check(PersistentAdministrator.AncestorRightsDenied(0) && PersistentAdministrator.AncestorRightsDenied(0x1200A9) && PersistentAdministrator.AncestorRightsDenied(0x116),
                "Read/execute and the existing CommonData create/write-attribute allowances do not conceal destructive ancestor grants");
            foreach (uint right in new uint[] { 0x10000, 0x40000, 0x80000, 0x40, 0xD0040, 0x1F01FF })
                check(!PersistentAdministrator.AncestorRightsDenied(right) && !PersistentAdministrator.AncestorRightsDenied(0x1200A9 | right),
                    "Any actual MED DELETE/WRITE_DAC/WRITE_OWNER/DELETE_CHILD grant independently blocks persistent executable registration");
        }
        sealed class Scheduler : PersistentAdministrator.ITaskScheduler
        {
            internal PersistentAdministrator.TaskDefinition Definition = Spec();
            internal string[] Active = new string[0], Arguments;
            internal int Starts, Reads, Polls, Disposals, Deletes;
            internal bool ThrowRun, BadId, ThrowRead, AllowDelete, ThrowDeleteBefore, ThrowDeleteAfter, KeepAfterDelete, ThrowPoll, NullPoll;
            internal Action<Scheduler> OnPoll, OnRead;
            public PersistentAdministrator.TaskDefinition Read(string name) { Reads++; if (OnRead != null) OnRead(this); if (ThrowRead) throw new IOException(); return Definition; }
            public void Register(PersistentAdministrator.TaskDefinition definition) { throw new InvalidOperationException("No pure fixture can register a real task."); }
            public PersistentAdministrator.TaskInstance Run(string name, string[] arguments) { Starts++; Arguments = (string[])arguments.Clone(); if (ThrowRun) throw new IOException(); Active = new[] { Instance }; return new PersistentAdministrator.TaskInstance { Id = BadId ? "bad" : Instance }; }
            public string[] Instances(string name) { Polls++; if (ThrowPoll) throw new IOException(); if (OnPoll != null) OnPoll(this); return NullPoll ? null : (string[])Active.Clone(); }
            public void Delete(string name) { if (!AllowDelete) throw new InvalidOperationException("No pure fixture can remove a real task."); Deletes++; if (ThrowDeleteBefore) throw new IOException(); if (!KeepAfterDelete) Definition = null; if (ThrowDeleteAfter) throw new IOException(); }
            public void Dispose() { Disposals++; }
        }
        static PersistentAdministrator.Image Policy(Scheduler scheduler, Action loader, Func<uint, long, bool> caller)
        { return new PersistentAdministrator.Image(Hash, Sid, scheduler, Spec(), loader, caller); }
        static void LeaseLifecycle(Action<bool, string> check)
        {
            int loaderChecks = 0; var scheduler = new Scheduler();
            var image = Policy(scheduler, delegate { loaderChecks++; }, delegate(uint pid, long started) { return pid == 11 && started == 22; });
            foreach (string invalid in new[] { null, "", Nonce.ToUpperInvariant(), Nonce + " --show", new string('0', 32) })
                check(Throws<InvalidOperationException>(delegate { image.Run(invalid, 11, 22); }) && scheduler.Starts == 0, "Malformed task parameters cannot reach Task.Run");
            check(Throws<InvalidOperationException>(delegate { image.Run(Nonce, 0, 22); }) && Throws<InvalidOperationException>(delegate { image.Run(Nonce, 11, 0); }) && scheduler.Starts == 0,
                "Zero PID/creation identities cannot start a task");
            check(Throws<UnauthorizedAccessException>(delegate { image.Run(Nonce, 12, 22); }) && scheduler.Starts == 0, "A different or stale caller cannot use the approved task");
            scheduler.Definition = null;
            check(Throws<UnauthorizedAccessException>(delegate { image.Run(Nonce, 11, 22); }) && scheduler.Starts == 0, "A missing or changed definition prevents launch rather than being repaired");
            scheduler.Definition = Spec(); image.Run(Nonce, 11, 22);
            check(scheduler.Starts == 1 && scheduler.Arguments.Length == 3 && scheduler.Arguments[0] == Nonce && scheduler.Arguments[1] == "11" && scheduler.Arguments[2] == "22" && image.InstanceGuid == Instance && loaderChecks == 3,
                "A valid launch retains one exact instance and only nonce/PID/creation parameters after current loader-policy and caller checks");
            check(Throws<InvalidOperationException>(delegate { image.Run(Nonce, 11, 22); }) && scheduler.Starts == 1, "A retained launch cannot silently start another instance");
            check(!image.WaitForTaskDrain(0) && Throws<InvalidOperationException>(delegate { image.Dispose(); }) && scheduler.Disposals == 0,
                "A live exact scheduled instance retains ownership and prevents disposal without stopping it");
            scheduler.Active = new[] { "7a31d1c1-ccad-4cde-866b-2ed36132bc95" };
            check(!image.WaitForTaskDrain(0), "Another owned instance prevents drain even after the launched instance disappears");
            scheduler.Active = new string[0]; scheduler.ThrowRead = true;
            check(!image.WaitForTaskDrain(0), "An unavailable task query never proves shutdown");
            scheduler.ThrowRead = false; scheduler.Definition = null;
            check(!image.WaitForTaskDrain(0), "An unapproved missing task definition never turns into a successful drain");
            scheduler.Definition = Spec(); scheduler.OnPoll = delegate(Scheduler value) { if (value.Polls % 2 == 0) value.Active = new[] { Instance }; };
            scheduler.Polls = 0;
            check(!image.WaitForTaskDrain(0), "A fresh instance appearing after an empty snapshot prevents early success");
            scheduler.OnPoll = null; scheduler.Active = new string[0];
            check(image.WaitForTaskDrain(0), "Only unchanged task metadata plus a fresh confirmed absence of every instance proves drain");
            image.Dispose(); image.Dispose(); check(scheduler.Disposals == 1 && !image.Available, "Confirmed shutdown closes task ownership exactly once");
            foreach (bool badId in new[] { false, true }) {
                var unknown = new Scheduler { ThrowRun = !badId, BadId = badId }; var uncertain = Policy(unknown, delegate { }, delegate(uint pid, long started) { return true; });
                check(Throws<IOException>(delegate { uncertain.Run(Nonce, 11, 22); }) && !uncertain.WaitForTaskDrain(0) &&
                    Throws<InvalidOperationException>(delegate { uncertain.Dispose(); }) && unknown.Starts == 1 && unknown.Disposals == 0,
                    "A transport failure or missing exact instance identity remains uncertain even when a later snapshot might be empty");
            }
            var guarded = new Scheduler(); var refused = Policy(guarded, delegate { throw new IOException(); }, delegate(uint pid, long started) { return true; });
            check(Throws<IOException>(delegate { refused.Run(Nonce, 11, 22); }) && guarded.Starts == 0, "A current machine-loader refusal prevents task execution without changing machine policy");
            refused.Dispose();
            foreach (int? priority in new int?[] { null, 7, 5, 0 }) {
                var changed = new Scheduler(); changed.Definition.Priority = priority;
                var normalOnly = Policy(changed, delegate { }, delegate(uint pid, long started) { return true; });
                check(Throws<UnauthorizedAccessException>(delegate { normalOnly.Run(Nonce, 11, 22); }) && changed.Starts == 0 && !normalOnly.TaskStartAttempted && normalOnly.WaitForTaskDrain(0),
                    "A missing, background or noncanonical priority fails before Task.Run or launch uncertainty");
                normalOnly.Dispose();
            }
        }
        sealed class Maintenance : PersistentAdministrator.IRegistrationMaintenance
        {
            public PersistentAdministrator.RegistrationState State { get; internal set; }
            public PersistentAdministrator.Ledger Registration { get; internal set; }
            public string Action { get; internal set; }
            internal int NativeDeletes, LedgerDeletes, EmptyChecks;
            internal bool FailNative, FailNativeAfter, FailLedger, FailEmpty;
            internal Maintenance(PersistentAdministrator.RegistrationState state)
            {
                State = state;
                if (state != PersistentAdministrator.RegistrationState.Empty) Registration = new PersistentAdministrator.Ledger { Scope = Hash, Sid = Sid, ControllerHash = Hash, NativeHash = NativeHash };
                if (state == PersistentAdministrator.RegistrationState.Complete) Action = Spec().Path;
            }
            public void DeleteNative()
            {
                NativeDeletes++; if (FailNative) throw new IOException();
                State = PersistentAdministrator.RegistrationState.LedgerOnly; Action = null;
                if (FailNativeAfter) throw new IOException();
            }
            public void DeleteLedger()
            { LedgerDeletes++; if (FailLedger) throw new IOException(); State = PersistentAdministrator.RegistrationState.Empty; Registration = null; }
            public void ConfirmEmpty()
            { EmptyChecks++; if (FailEmpty || State != PersistentAdministrator.RegistrationState.Empty) throw new IOException(); }
        }
        static bool Revoke(Maintenance cache, Scheduler scheduler, Action authorized)
        { return PersistentAdministrator.RevokeRegistration(Hash, Sid, cache, scheduler, authorized); }
        static Scheduler RemovalScheduler()
        { return new Scheduler { AllowDelete = true }; }
        static void RegistrationLifecycle(Action<bool, string> check)
        {
            var complete = new Maintenance(PersistentAdministrator.RegistrationState.Complete); var scheduler = RemovalScheduler();
            int authorized = 0;
            check(Revoke(complete, scheduler, delegate { authorized++; }) && scheduler.Deletes == 1 && scheduler.Definition == null &&
                complete.NativeDeletes == 1 && complete.LedgerDeletes == 1 && complete.State == PersistentAdministrator.RegistrationState.Empty && authorized == 6,
                "Explicit approved revocation confirms inactive exact task, revokes it, removes native before ledger, then proves absence");
            check(Revoke(complete, scheduler, delegate { }) && scheduler.Deletes == 1 && complete.NativeDeletes == 1 && complete.LedgerDeletes == 1,
                "A verified empty protected scope with no task is an idempotent completed removal");
            foreach (PersistentAdministrator.RegistrationState state in new[] { PersistentAdministrator.RegistrationState.Empty, PersistentAdministrator.RegistrationState.LedgerOnly }) {
                var cache = new Maintenance(state); var absent = RemovalScheduler(); absent.Definition = null;
                check(Revoke(cache, absent, delegate { }) && absent.Deletes == 0 && cache.NativeDeletes == 0 && cache.State == PersistentAdministrator.RegistrationState.Empty,
                    "Absent-task cleanup accepts only an empty owned scope or its retained valid ledger-only recovery record");
                cache = new Maintenance(state); var present = RemovalScheduler();
                check(Throws<UnauthorizedAccessException>(delegate { Revoke(cache, present, delegate { }); }) && present.Deletes == 0 && cache.LedgerDeletes == 0,
                    "A task without a fully verified native-and-ledger pair is retained rather than blindly deleted");
            }
            foreach (bool throwAfter in new[] { false, true }) {
                var cache = new Maintenance(PersistentAdministrator.RegistrationState.Complete);
                var uncertain = RemovalScheduler(); uncertain.ThrowDeleteBefore = !throwAfter; uncertain.ThrowDeleteAfter = throwAfter;
                check(Throws<IOException>(delegate { Revoke(cache, uncertain, delegate { }); }) && uncertain.Deletes == 1 && cache.NativeDeletes == 0 && cache.LedgerDeletes == 0,
                    "A task deletion exception retains both approval files whether or not the service committed deletion");
                uncertain.ThrowDeleteBefore = uncertain.ThrowDeleteAfter = false;
                check(Revoke(cache, uncertain, delegate { }) && cache.State == PersistentAdministrator.RegistrationState.Empty,
                    "Explicit retry can finish after a known retained task deletion failure without claiming earlier success");
            }
            foreach (Action<Scheduler> mutation in new Action<Scheduler>[] {
                delegate(Scheduler x) { x.ThrowRead = true; }, delegate(Scheduler x) { x.ThrowPoll = true; }, delegate(Scheduler x) { x.NullPoll = true; },
                delegate(Scheduler x) { x.Active = new[] { Instance }; }, delegate(Scheduler x) { x.Definition.Priority = 7; },
                delegate(Scheduler x) { x.Definition.Security = null; }, delegate(Scheduler x) { x.Definition.Arguments += " --other"; },
                delegate(Scheduler x) { x.OnPoll = delegate(Scheduler value) { if (value.Polls == 2) value.Active = new[] { Instance }; }; },
                delegate(Scheduler x) { x.OnRead = delegate(Scheduler value) { if (value.Reads == 3) value.Definition = null; }; } }) {
                var cache = new Maintenance(PersistentAdministrator.RegistrationState.Complete); var refused = RemovalScheduler(); mutation(refused);
                check(Throws<Exception>(delegate { Revoke(cache, refused, delegate { }); }) && refused.Deletes == 0 && cache.NativeDeletes == 0 && cache.LedgerDeletes == 0,
                    "Unknown queries, active or newly active instances, altered task metadata and concurrent disappearance refuse before any mutation");
            }
            var notGone = new Maintenance(PersistentAdministrator.RegistrationState.Complete); var remaining = RemovalScheduler(); remaining.KeepAfterDelete = true;
            check(Throws<IOException>(delegate { Revoke(notGone, remaining, delegate { }); }) && remaining.Deletes == 1 && notGone.NativeDeletes == 0,
                "A task still present after Delete prevents native or ledger deletion");
            foreach (bool afterNative in new[] { false, true }) {
                var cache = new Maintenance(PersistentAdministrator.RegistrationState.Complete) { FailNative = !afterNative, FailNativeAfter = afterNative };
                var failed = RemovalScheduler();
                check(Throws<IOException>(delegate { Revoke(cache, failed, delegate { }); }) && failed.Definition == null && cache.LedgerDeletes == 0 && cache.Registration != null,
                    "A native disposition or native absence-confirmation failure retains the ledger and reports failure after task revocation");
                cache.FailNative = cache.FailNativeAfter = false;
                check(Revoke(cache, failed, delegate { }) && cache.Registration == null,
                    "A fresh explicit retry completes either intact or ledger-only residue after failed native cleanup");
            }
            var ledgerOnly = new Maintenance(PersistentAdministrator.RegistrationState.Complete) { FailLedger = true }; var revoked = RemovalScheduler();
            check(Throws<IOException>(delegate { Revoke(ledgerOnly, revoked, delegate { }); }) && ledgerOnly.NativeDeletes == 1 && ledgerOnly.Registration != null &&
                ledgerOnly.State == PersistentAdministrator.RegistrationState.LedgerOnly && revoked.Definition == null,
                "Failed ledger deletion retains the exact ownership record without resurrecting the old scheduled authority");
            ledgerOnly.FailLedger = false;
            check(Revoke(ledgerOnly, revoked, delegate { }) && ledgerOnly.NativeDeletes == 1 && ledgerOnly.LedgerDeletes == 2 && ledgerOnly.Registration == null,
                "Ledger-only retry never needs an absent native image and removes only its authenticated retained record");
            var finalUnknown = new Maintenance(PersistentAdministrator.RegistrationState.Complete) { FailEmpty = true }; var absentTask = RemovalScheduler();
            check(Throws<IOException>(delegate { Revoke(finalUnknown, absentTask, delegate { }); }),
                "An unconfirmed final empty directory never returns cleanup success even after successful deletions");
            foreach (Action<Maintenance> mutation in new Action<Maintenance>[] {
                delegate(Maintenance x) { x.Registration.Scope = NativeHash; }, delegate(Maintenance x) { x.Registration.Sid = "S-1-5-21-11-22-33-1002"; },
                delegate(Maintenance x) { x.Registration.ControllerHash = "bad"; }, delegate(Maintenance x) { x.Registration.NativeHash = null; },
                delegate(Maintenance x) { x.Registration = null; }, delegate(Maintenance x) { x.Action = @"C:\Untrusted\AdministratorBootstrap.exe"; },
                delegate(Maintenance x) { x.State = (PersistentAdministrator.RegistrationState)77; } }) {
                var cache = new Maintenance(PersistentAdministrator.RegistrationState.Complete); var foreign = RemovalScheduler(); mutation(cache);
                check(Throws<Exception>(delegate { Revoke(cache, foreign, delegate { }); }) && foreign.Reads == 0 && foreign.Deletes == 0 && cache.NativeDeletes == 0,
                    "Corrupt ownership, foreign user/scope, nonphysical action and unknown states refuse before scheduler mutation or file cleanup");
            }
            for (int expires = 1; expires <= 6; expires++) {
                int stop = expires, calls = 0;
                var cache = new Maintenance(PersistentAdministrator.RegistrationState.Complete); var task = RemovalScheduler();
                check(Throws<UnauthorizedAccessException>(delegate { Revoke(cache, task, delegate { if (++calls == stop) throw new UnauthorizedAccessException(); }); }),
                    "Approval expiry or parent exit at destructive/final checkpoints always reports failure, checkpoint " + expires);
                if (expires <= 2) check(task.Deletes == 0 && cache.NativeDeletes == 0 && cache.LedgerDeletes == 0, "Expired authorization before task deletion preserves all authority and ownership records");
                else if (expires == 3) check(task.Definition == null && cache.NativeDeletes == 0 && cache.Registration != null, "Expired authorization after task revocation retains both files");
                else if (expires == 4) check(task.Definition == null && cache.State == PersistentAdministrator.RegistrationState.LedgerOnly && cache.Registration != null,
                    "Expired authorization after native deletion retains the valid recovery ledger");
            }
            var appearing = new Maintenance(PersistentAdministrator.RegistrationState.Complete); var freshTask = RemovalScheduler(); freshTask.Definition = null;
            freshTask.OnRead = delegate(Scheduler value) { if (value.Reads == 3) value.Definition = Spec(); };
            check(Throws<IOException>(delegate { Revoke(appearing, freshTask, delegate { }); }) && freshTask.Deletes == 0 && appearing.NativeDeletes == 0,
                "A task appearing during repeated absence observations prevents file removal");
            foreach (PersistentAdministrator.RegistrationState state in new[] { PersistentAdministrator.RegistrationState.Empty, PersistentAdministrator.RegistrationState.LedgerOnly }) {
                var inconsistent = new Maintenance(state); inconsistent.Action = Spec().Path; var task = RemovalScheduler(); task.Definition = null;
                check(Throws<UnauthorizedAccessException>(delegate { Revoke(inconsistent, task, delegate { }); }) && task.Reads == 0 && inconsistent.LedgerDeletes == 0,
                    "Empty and ledger-only states cannot retain a stale executable action");
            }
            var oldBuild = new Maintenance(PersistentAdministrator.RegistrationState.Complete); oldBuild.Registration.ControllerHash = NativeHash;
            var oldTask = RemovalScheduler();
            check(Revoke(oldBuild, oldTask, delegate { }) && oldBuild.Registration == null,
                "Fresh manually approved maintenance may retire a different controller hash owned by the exact user/scope without executing old code");
        }
        static bool Throws<T>(Action action) where T : Exception
        { try { action(); return false; } catch (T) { return true; } }
    }
}
