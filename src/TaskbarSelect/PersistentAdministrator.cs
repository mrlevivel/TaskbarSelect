using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace TaskbarSelect
{
    // The scheduler grants only one immutable native entry point. Preferences,
    // an installed user-writable EXE, and arbitrary commands never become task actions.
    internal static class PersistentAdministrator
    {
        internal const string CacheName = "TaskbarSelect.Persistent", NativeName = "AdministratorBootstrap.exe", LedgerName = "registration.bin";
        internal const string TaskFolder = "\\TaskbarSelect", TaskPrefix = "Persistent-";
        internal const string ScopeDomain = "TaskbarSelect.PersistentAdministrator.v1\n";
        internal const int NormalTaskPriority = 4;
        const string Admins = "S-1-5-32-544", SystemSid = "S-1-5-18";
        const int MaximumNative = 16 * 1024 * 1024, MaximumController = 64 * 1024 * 1024;
        static readonly byte[] magic = Encoding.ASCII.GetBytes("TSAPR01\0");

        internal sealed class Ledger
        {
            internal string Scope, ControllerHash, NativeHash, Sid;
        }
        internal sealed class TaskDefinition
        {
            internal string Name, Path, Arguments, WorkingDirectory, Sid, Author, Description;
            internal int Actions, ActionType, Triggers, LogonType, RunLevel, MultipleInstances;
            internal int? Priority;
            internal bool Enabled, AllowDemandStart, Hidden, BatteryBlocked, BatteryStops, NetworkRequired, IdleRequired, Wake, StartWhenAvailable;
            internal string ExecutionLimit;
            internal RawSecurityDescriptor Security;
        }
        internal interface ITaskScheduler : IDisposable
        {
            TaskDefinition Read(string name);
            void Register(TaskDefinition definition);
            TaskInstance Run(string name, string[] arguments);
            string[] Instances(string name);
            void Delete(string name);
        }
        internal sealed class TaskInstance : IDisposable
        {
            internal string Id;
            internal object Native;
            public void Dispose() { Release(Native); Native = null; }
        }
        internal static bool AncestorRightsDenied(uint granted)
        { return (granted & 0xD0040u) == 0; } // DELETE, WRITE_DAC, WRITE_OWNER, directory DELETE_CHILD.
        internal static bool NamespaceRightsDenied(uint granted, bool directory)
        { return (granted & (directory ? 0xD000Cu : 0xD0002u)) == 0; }
        internal static bool RootNamespaceTarget(string target)
        { return target != null && (target.Length == 0 || target == "\\"); }

        internal static string ComputeScope(string sid, string profile, bool isTest)
        {
            string canonical = AdministratorLaunch.CanonicalDosPath(profile);
            if (!CanonicalSid(sid) || canonical == null || canonical.Length == 3) throw new ArgumentException("The persistent administrator profile identity is invalid.");
            return Digest(Encoding.UTF8.GetBytes(ScopeDomain + sid + "\n" + (isTest ? "Tests" : "Production") + "\n" + canonical.ToUpperInvariant() + "\n"));
        }
        internal static string ScopeForCurrentProfile()
        { using (var identity = WindowsIdentity.GetCurrent()) return ComputeScope(identity.User.Value, AppProfile.Directory, AppProfile.IsTest); }
        internal static string TaskName(string scope)
        { if (!AdministratorLaunch.ValidHash(scope)) throw new ArgumentException("The persistent administrator scope is invalid."); return TaskPrefix + scope; }
        internal static byte[] EncodeLedger(Ledger ledger)
        {
            if (ledger == null || !AdministratorLaunch.ValidHash(ledger.Scope) || !AdministratorLaunch.ValidHash(ledger.ControllerHash) ||
                !AdministratorLaunch.ValidHash(ledger.NativeHash) || !CanonicalSid(ledger.Sid)) throw new ArgumentException("The persistent administrator ledger identity is invalid.");
            var sid = new SecurityIdentifier(ledger.Sid); byte[] sidBytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(sidBytes, 0);
            using (var memory = new MemoryStream()) using (var writer = new BinaryWriter(memory)) {
                writer.Write(magic); writer.Write(Unhex(ledger.Scope)); writer.Write(Unhex(ledger.ControllerHash)); writer.Write(Unhex(ledger.NativeHash));
                writer.Write((uint)sidBytes.Length); writer.Write(sidBytes); return memory.ToArray();
            }
        }
        internal static Ledger DecodeLedger(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 120 || bytes.Length > 176) throw new IOException("The persistent administrator ledger size is invalid.");
            for (int index = 0; index < magic.Length; index++) if (bytes[index] != magic[index]) throw new IOException("The persistent administrator ledger format is invalid.");
            uint length = BitConverter.ToUInt32(bytes, 104);
            if (length < 12 || length > 68 || bytes.Length != 108 + length || bytes[108] != 1 || bytes[109] < 1 || bytes[109] > 15 || length != 8 + bytes[109] * 4)
                throw new IOException("The persistent administrator ledger SID is invalid.");
            var sid = new SecurityIdentifier(bytes, 108); byte[] canonical = new byte[sid.BinaryLength]; sid.GetBinaryForm(canonical, 0);
            for (int index = 0; index < canonical.Length; index++) if (canonical[index] != bytes[108 + index]) throw new IOException("The persistent administrator ledger SID is not canonical.");
            var ledger = new Ledger { Scope = Hex(bytes, 8, 32), ControllerHash = Hex(bytes, 40, 32), NativeHash = Hex(bytes, 72, 32), Sid = sid.Value };
            if (!AdministratorLaunch.ValidHash(ledger.Scope) || !AdministratorLaunch.ValidHash(ledger.ControllerHash) || !AdministratorLaunch.ValidHash(ledger.NativeHash))
                throw new IOException("The persistent administrator ledger hashes are invalid.");
            return ledger;
        }
        internal static bool CanonicalSid(string sid)
        { try { return sid != null && new SecurityIdentifier(sid).Value == sid && new SecurityIdentifier(sid).BinaryLength >= 12 && new SecurityIdentifier(sid).BinaryLength <= 68; } catch (ArgumentException) { return false; } }
        internal static string TaskPrincipalSid(string value, Func<string, string> resolve)
        {
            if (CanonicalSid(value)) return value;
            if (String.IsNullOrEmpty(value) || resolve == null) return null;
            try { string sid = resolve(value); return CanonicalSid(sid) ? sid : null; }
            catch (IdentityNotMappedException) { return null; }
            catch (ArgumentException) { return null; }
        }
        static string TaskPrincipalSid(string value)
        { return TaskPrincipalSid(value, delegate(string account) { return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value; }); }
        internal static TaskDefinition ExpectedTask(string scope, string sid, string physicalNative)
        {
            if (!CanonicalSid(sid) || !PhysicalAction(physicalNative) || !physicalNative.EndsWith("\\" + NativeName, StringComparison.Ordinal)) throw new ArgumentException("The persistent task action is invalid.");
            return new TaskDefinition { Name = TaskName(scope), Path = physicalNative, Arguments = "--scheduled-administrator-bootstrap " + scope + " $(Arg0) $(Arg1) $(Arg2)",
                WorkingDirectory = physicalNative.Substring(0, physicalNative.LastIndexOf('\\')), Sid = sid, Author = "Taskbar Select", Description = "Taskbar Select administrator support on demand",
                Actions = 1, ActionType = 0, Triggers = 0, LogonType = 3, RunLevel = 1, MultipleInstances = 2, Priority = NormalTaskPriority,
                Enabled = true, AllowDemandStart = true, Hidden = true, ExecutionLimit = "PT0S", Security = new RawSecurityDescriptor(TaskSecurity(sid)) };
        }
        internal static bool SameTask(TaskDefinition actual, TaskDefinition expected)
        {
            return actual != null && expected != null && actual.Name == expected.Name && actual.Path == expected.Path && actual.Arguments == expected.Arguments &&
                actual.WorkingDirectory == expected.WorkingDirectory && actual.Sid == expected.Sid && actual.Author == expected.Author && actual.Description == expected.Description &&
                actual.Actions == 1 && actual.ActionType == 0 && actual.Triggers == 0 && actual.LogonType == 3 && actual.RunLevel == 1 && actual.MultipleInstances == 2 &&
                actual.Priority == NormalTaskPriority && expected.Priority == NormalTaskPriority &&
                actual.Enabled && actual.AllowDemandStart && actual.Hidden && !actual.BatteryBlocked && !actual.BatteryStops && !actual.NetworkRequired && !actual.IdleRequired &&
                !actual.Wake && !actual.StartWhenAvailable && actual.ExecutionLimit == "PT0S" && ProtectedTaskSecurity(actual.Security, expected.Sid);
        }
        internal static bool ProtectedTaskSecurity(RawSecurityDescriptor security, string sid)
        {
            if (security == null || !CanonicalSid(sid) || security.Owner == null || (security.Owner.Value != Admins && security.Owner.Value != SystemSid) ||
                security.Group == null || security.Group.Value != Admins || (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                security.DiscretionaryAcl == null || security.DiscretionaryAcl.Count != 3) return false;
            bool admin = false, system = false, user = false;
            foreach (GenericAce value in security.DiscretionaryAcl) {
                var ace = value as CommonAce;
                if (ace == null || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed || ace.AceFlags != AceFlags.None) return false;
                uint mask = unchecked((uint)ace.AccessMask); string who = ace.SecurityIdentifier.Value;
                if (who == Admins && !admin && (mask == 0x1F01FF || mask == 0x10000000)) admin = true;
                else if (who == SystemSid && !system && (mask == 0x1F01FF || mask == 0x10000000)) system = true;
                else if (who == sid && !user && (mask == 0x1200A9 || mask == 0xA0000000)) user = true;
                else return false;
            }
            return admin && system && user;
        }
        static string TaskSecurity(string sid) { return "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;" + sid + ")"; }
        static string DirectorySecurity { get { return "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)"; } }
        static string FileSecurity { get { return "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)"; } }
        internal static bool PhysicalAction(string path)
        {
            const string prefix = @"\\?\GLOBALROOT\GLOBAL??\";
            if (path == null || !path.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string guidPath = @"\\?\" + path.Substring(prefix.Length);
            return StableGuidAction(guidPath) == path;
        }
        internal static string StableGuidAction(string path)
        {
            const string prefix = @"\\?\Volume{";
            if (path == null || !path.StartsWith(prefix, StringComparison.Ordinal) || path.Length <= prefix.Length + 38 ||
                path[prefix.Length + 36] != '}' || path[prefix.Length + 37] != '\\') return null;
            Guid volume;
            if (!Guid.TryParseExact(path.Substring(prefix.Length, 36), "D", out volume) || volume == Guid.Empty) return null;
            string tail = path.Substring(prefix.Length + 38);
            if (AdministratorLaunch.CanonicalDosPath("C:\\" + tail) == null || tail.Length == 0) return null;
            return @"\\?\GLOBALROOT\GLOBAL??\Volume{" + volume.ToString("D") + "}\\" + tail;
        }
        internal static bool VolumeMappingMatches(string action, string target, string kernel)
        {
            if (!PhysicalAction(action) || !PhysicalVolume(target) || !KernelPath(kernel)) return false;
            const string prefix = @"\\?\GLOBALROOT\GLOBAL??\";
            string tail = action.Substring(prefix.Length); int separator = tail.IndexOf('\\');
            return String.Equals(target + tail.Substring(separator), kernel, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool RunArguments(string nonce, uint pid, long started)
        { return AdministratorLaunch.ValidToken(nonce) && pid != 0 && started > 0; }

        internal sealed class Image : IDisposable
        {
            internal bool Available { get; private set; }
            internal string Path { get; private set; }
            internal string PayloadHash { get; private set; }
            internal string Reason { get; private set; }
            internal string InstanceGuid { get { return instance == null ? null : instance.Id; } }
            internal bool TaskStartAttempted { get { return attempted; } }
            readonly string scope, sid;
            readonly List<IDisposable> leases;
            readonly ITaskScheduler scheduler;
            readonly TaskDefinition expected;
            readonly FileStream native;
            readonly Action loaderCheck;
            readonly Func<uint, long, bool> callerCheck;
            TaskInstance instance;
            bool attempted, uncertain, drained, disposed;
            internal Image(string reason) { Reason = reason; leases = new List<IDisposable>(); }
            internal Image(string value, string user, FileStream file, string physical, string payload, List<IDisposable> held, ITaskScheduler tasks, TaskDefinition definition)
            { scope = value; sid = user; native = file; Path = physical; PayloadHash = payload; leases = held; scheduler = tasks; expected = definition; Available = true;
                loaderCheck = AdministratorLaunch.CheckMachineLoaderPolicy; callerCheck = CurrentCaller; }
            internal Image(string value, string user, ITaskScheduler tasks, TaskDefinition definition, Action loader, Func<uint, long, bool> caller)
            { scope = value; sid = user; scheduler = tasks; expected = definition; leases = new List<IDisposable>(); loaderCheck = loader; callerCheck = caller; Available = true; }
            internal void Run(string nonce, uint pid, long started)
            {
                if (disposed || !Available || attempted || !RunArguments(nonce, pid, started)) throw new InvalidOperationException("The persistent task launch is unavailable.");
                loaderCheck();
                if (!callerCheck(pid, started) ||
                    !SameTask(scheduler.Read(TaskName(scope)), expected) || scheduler.Instances(TaskName(scope)).Length != 0) throw new UnauthorizedAccessException("The persistent task launch identity changed.");
                attempted = true; uncertain = true;
                instance = scheduler.Run(TaskName(scope), new[] { nonce, pid.ToString(CultureInfo.InvariantCulture), started.ToString(CultureInfo.InvariantCulture) });
                Guid value;
                if (instance == null || !Guid.TryParse(instance.Id, out value) || value == Guid.Empty) throw new IOException("The scheduled task instance could not be retained.");
                instance.Id = value.ToString("D"); uncertain = false;
            }
            internal bool WaitForTaskDrain(int milliseconds)
            {
                if (disposed) return false;
                if (!attempted) return true;
                if (uncertain || instance == null) return false;
                var clock = Stopwatch.StartNew();
                do {
                    try {
                        if (!SameTask(scheduler.Read(TaskName(scope)), expected)) return false;
                        string[] running = scheduler.Instances(TaskName(scope));
                        if (running == null) return false;
                        bool exactPresent = false;
                        foreach (string value in running) { Guid parsed; if (!Guid.TryParse(value, out parsed) || parsed == Guid.Empty) return false; if (parsed.ToString("D") == instance.Id) exactPresent = true; }
                        if (!exactPresent && running.Length == 0) {
                            // A fresh second query fences a transient empty snapshot.
                            if (!SameTask(scheduler.Read(TaskName(scope)), expected) || scheduler.Instances(TaskName(scope)).Length != 0) return false;
                            drained = true; return true;
                        }
                    } catch (Exception) { return false; }
                    if (clock.ElapsedMilliseconds >= Math.Max(0, milliseconds)) return false;
                    Thread.Sleep(10);
                } while (true);
            }
            internal bool VerifyBootstrapProcess(uint pid, long started)
            {
                if (disposed || !Available || native == null || pid == 0 || started <= 0) return false;
                try {
                    using (var process = OpenBoundProcess(pid, started)) {
                        var info = NativeProcessIdentity.Read(pid);
                        if (info.Started != started || !AdministratorLaunch.HighToken(info) || ProcessSid(process) != sid || ProcessSession(pid) != ProcessSession(NativeProcessIdentity.Current.Process)) return false;
                        using (var actual = OpenKernelImage(process)) return SameFile(actual.SafeFileHandle, native.SafeFileHandle) && Digest(actual) == Digest(native);
                    }
                } catch (Exception) { return false; }
            }
            public void Dispose()
            {
                if (disposed) return;
                if (attempted && !drained) throw new InvalidOperationException("The scheduled administrator instance has not been confirmed stopped.");
                disposed = true; Available = false;
                if (instance != null) instance.Dispose();
                if (scheduler != null) scheduler.Dispose();
                for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose();
            }
        }
        internal static Image Inspect(string scope)
        {
            var held = new List<IDisposable>(); ITaskScheduler scheduler = null;
            try {
                AdministratorLaunch.CheckMachineLoaderPolicy();
                var current = NativeProcessIdentity.Current;
                if (current.IntegrityLevel != NativeProcessIdentity.Medium || current.Elevated || !current.IntegrityAvailable || !current.ElevationAvailable) throw new UnauthorizedAccessException();
                using (var caller = OpenBoundProcess(current.Process, current.Started)) {
                    string sid = ProcessSid(caller); string folder;
                    using (var medium = MediumAccessToken(caller)) folder = PinCache(scope, false, held, medium);
                    Ledger ledger; FileStream native = ReadRegistration(folder, scope, sid, held, out ledger);
                    using (var image = OpenKernelImage(caller)) if (Digest(image) != ledger.ControllerHash) throw new UnauthorizedAccessException("The persistent approval belongs to another application build.");
                    string action;
                    using (var medium = MediumAccessToken(caller)) action = StableAction(native.SafeFileHandle, held, medium);
                    TaskDefinition expected = ExpectedTask(scope, sid, action);
                    scheduler = new WindowsScheduler();
                    if (!SameTask(scheduler.Read(TaskName(scope)), expected)) throw new UnauthorizedAccessException("The persistent task definition changed.");
                    return new Image(scope, sid, native, action, CurrentPayloadHash(), held, scheduler, expected);
                }
            } catch (Exception) {
                if (scheduler != null) scheduler.Dispose();
                for (int index = held.Count - 1; index >= 0; index--) held[index].Dispose();
                return new Image("Persistent administrator approval is unavailable or does not match this application.");
            }
        }
        internal static TaskInstance Run(string scope, string nonce, uint pid, long started)
        { throw new InvalidOperationException("Retain Inspect(scope).Run and its exact instance until confirmed task drain."); }

        // This read-only gate never turns a corrupt, partial or inaccessible
        // registration into "no approval". Empty protected scope directories are
        // deliberately retained after successful revocation.
        internal static bool RequiresApprovalRemoval(string scope)
        {
            TaskName(scope); var held = new List<IDisposable>();
            try {
                var current = NativeProcessIdentity.Current;
                using (var caller = ApprovedController(current.Process, current.Started))
                using (var medium = MediumAccessToken(caller)) {
                    bool absent = false; string folder = null;
                    try { folder = PinCache(scope, false, held, medium); }
                    catch (FileOperationException error) { if (!MissingFile(error)) throw; absent = true; }
                    bool empty = absent || DirectoryNames(folder).Length == 0;
                    using (var scheduler = new WindowsScheduler()) {
                        // A task without its ledger is unsafe, not permission to
                        // uninstall. A second absence observation fences races.
                        if (scheduler.Read(TaskName(scope)) != null || !empty) return true;
                        return scheduler.Read(TaskName(scope)) != null;
                    }
                }
            } finally { DisposeAll(held); }
        }
        internal static bool Install(string scope, uint parentPid, long parentStarted, string approvedBootstrapHash, Action authorization)
        {
            RequireHigh(); TaskName(scope); if (authorization == null) throw new UnauthorizedAccessException(); authorization(); AdministratorLaunch.CheckMachineLoaderPolicy();
            using (var parent = ApprovedController(parentPid, parentStarted))
            using (var controller = OpenKernelImage(parent))
            using (var nativeParent = OpenNativeParent())
            using (var source = OpenKernelImage(nativeParent)) {
                byte[] scheduled = ApprovedScheduledImage(source, approvedBootstrapHash);
                if (controller.Length <= 2 || controller.Length > MaximumController) throw new UnauthorizedAccessException("The approving controller image is invalid.");
                string sid = ProcessSid(parent);
                var ledger = new Ledger { Scope = scope, Sid = sid, NativeHash = Digest(scheduled), ControllerHash = Digest(controller) };
                Action authorized = delegate { authorization(); CheckApprovingController(parent, parentPid, parentStarted); };
                authorized();
                using (var medium = MediumAccessToken(parent))
                using (var cache = MaintenanceCache.Open(scope, sid, true, medium))
                using (var scheduler = new WindowsScheduler()) {
                    // A fresh manual approval may retire this user's previous
                    // inactive build. Its bytes are never executed or adopted by
                    // the new controller. All old identity checks still apply.
                    RevokeRegistration(scope, sid, cache, scheduler, authorized);
                    authorized();
                    cache.Create(ledger, scheduled, authorized);
                    var expected = ExpectedTask(scope, sid, cache.Action);
                    if (scheduler.Read(TaskName(scope)) != null) throw new UnauthorizedAccessException("A persistent task appeared during registration.");
                    authorized(); scheduler.Register(expected);
                    authorized();
                    if (!SameTask(scheduler.Read(TaskName(scope)), expected)) throw new IOException("Persistent administrator registration could not be confirmed.");
                    return true;
                }
            }
        }
        internal static bool Remove(string scope, uint parentPid, long parentStarted, string approvedBootstrapHash, Action authorization)
        {
            RequireHigh(); TaskName(scope); if (authorization == null) throw new UnauthorizedAccessException(); authorization(); AdministratorLaunch.CheckMachineLoaderPolicy();
            using (var parent = ApprovedController(parentPid, parentStarted))
            using (var nativeParent = OpenNativeParent())
            using (var source = OpenKernelImage(nativeParent)) {
                // A scheduled-only broker cannot be repurposed for maintenance.
                // The bound native parent must be the freshly approved manual
                // image whose digest arrived over the authenticated protocol.
                ApprovedScheduledImage(source, approvedBootstrapHash);
                using (var medium = MediumAccessToken(parent))
                using (var cache = MaintenanceCache.Open(scope, ProcessSid(parent), false, medium))
                using (var scheduler = new WindowsScheduler())
                    return RevokeRegistration(scope, ProcessSid(parent), cache, scheduler,
                        delegate { authorization(); CheckApprovingController(parent, parentPid, parentStarted); });
            }
        }
        static byte[] ApprovedScheduledImage(FileStream source, string approvedBootstrapHash)
        {
            if (!AdministratorLaunch.ValidHash(approvedBootstrapHash) || source.Length <= 2 || source.Length > MaximumNative || Digest(source) != approvedBootstrapHash)
                throw new UnauthorizedAccessException("The approved native source identity changed.");
            return ScheduledBootstrapImage.Extract(ReadBounded(source, MaximumNative), approvedBootstrapHash);
        }
        static SafeFileHandle ApprovedController(uint pid, long started)
        {
            var parent = OpenBoundProcess(pid, started);
            try {
                var info = NativeProcessIdentity.Read(pid);
                if (!info.IntegrityAvailable || !info.ElevationAvailable || info.IntegrityLevel != NativeProcessIdentity.Medium || info.Elevated ||
                    ProcessSid(parent) != CurrentSid() || ProcessSession(pid) != ProcessSession(NativeProcessIdentity.Current.Process)) throw new UnauthorizedAccessException("The approving controller identity changed.");
                return parent;
            } catch { parent.Dispose(); throw; }
        }
        static void CheckApprovingController(SafeFileHandle parent, uint pid, long started)
        { if (!Alive(parent) || NativeProcessIdentity.Started(pid) != started) throw new UnauthorizedAccessException("The approving controller exited."); }

        internal enum RegistrationState { Empty, LedgerOnly, Complete }
        internal interface IRegistrationMaintenance
        {
            RegistrationState State { get; }
            Ledger Registration { get; }
            string Action { get; }
            void DeleteNative();
            void DeleteLedger();
            void ConfirmEmpty();
        }
        // The real adapter enters only after its protected scope is exclusively
        // leased and every existing leaf is identity-checked and exclusively
        // opened with DELETE. This policy is also exercised by pure fixtures.
        internal static bool RevokeRegistration(string scope, string sid, IRegistrationMaintenance cache, ITaskScheduler scheduler, Action authorized)
        {
            TaskName(scope);
            if (!CanonicalSid(sid) || cache == null || scheduler == null || authorized == null) throw new UnauthorizedAccessException();
            var ledger = cache.Registration;
            if (cache.State == RegistrationState.Empty && (ledger != null || cache.Action != null)) throw new UnauthorizedAccessException();
            if (cache.State == RegistrationState.LedgerOnly && cache.Action != null) throw new UnauthorizedAccessException();
            if (cache.State != RegistrationState.Empty && (ledger == null || ledger.Scope != scope || ledger.Sid != sid ||
                !AdministratorLaunch.ValidHash(ledger.ControllerHash) || !AdministratorLaunch.ValidHash(ledger.NativeHash))) throw new UnauthorizedAccessException("The retained approval identity changed.");
            if (cache.State != RegistrationState.Empty && cache.State != RegistrationState.LedgerOnly && cache.State != RegistrationState.Complete) throw new UnauthorizedAccessException();
            TaskDefinition expected = cache.State == RegistrationState.Complete ? ExpectedTask(scope, sid, cache.Action) : null;
            string name = TaskName(scope); authorized();
            TaskDefinition actual = scheduler.Read(name);
            if (actual != null) {
                if (expected == null || !SameTask(actual, expected)) throw new UnauthorizedAccessException("The persistent task definition changed; revocation was not attempted.");
                for (int pass = 0; pass < 2; pass++) {
                    if (!SameTask(scheduler.Read(name), expected)) throw new UnauthorizedAccessException("The persistent task identity changed before revocation.");
                    string[] instances = scheduler.Instances(name);
                    if (instances == null || instances.Length != 0) throw new InvalidOperationException("The persistent task must be confirmed inactive before revocation.");
                }
                authorized(); scheduler.Delete(name);
            }
            // If Delete throws after committing, retain every file. A later
            // explicit retry can observe the absent task and finish safely.
            ConfirmTaskAbsent(scheduler, name); authorized();
            if (cache.State == RegistrationState.Complete) { cache.DeleteNative(); authorized(); }
            if (cache.State == RegistrationState.LedgerOnly) { cache.DeleteLedger(); authorized(); }
            cache.ConfirmEmpty(); ConfirmTaskAbsent(scheduler, name); authorized();
            return true;
        }
        static void ConfirmTaskAbsent(ITaskScheduler scheduler, string name)
        { if (scheduler.Read(name) != null || scheduler.Read(name) != null) throw new IOException("The persistent task could not be confirmed revoked."); }
        static bool MissingFile(FileOperationException error)
        { return error.Status == unchecked((int)0xC0000034) || error.Status == unchecked((int)0xC000003A); }
        static void DisposeAll(List<IDisposable> held)
        { for (int index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }

        sealed class MaintenanceCache : IRegistrationMaintenance, IDisposable
        {
            readonly List<IDisposable> held;
            readonly string folder, scope, sid;
            readonly SafeFileHandle directory, medium;
            FileStream native, record;
            public RegistrationState State { get; private set; }
            public Ledger Registration { get; private set; }
            public string Action { get; private set; }
            MaintenanceCache(string value, string user, bool create, SafeFileHandle caller, List<IDisposable> leases)
            {
                scope = value; sid = user; medium = caller; held = leases;
                try { folder = PinCache(scope, create, held, medium); }
                catch (FileOperationException error) { if (create || !MissingFile(error)) throw; State = RegistrationState.Empty; return; }
                int last = held.Count - 1; var original = (SafeFileHandle)held[last];
                AdministratorLaunch.FileInformation before;
                if (!GetFileInformationByHandle(original, out before)) throw new Win32Exception(Marshal.GetLastWin32Error());
                original.Dispose(); held.RemoveAt(last);
                // No other legitimate reader, bootstrap or maintenance request
                // can enter this scope while its directory lease is exclusive.
                directory = OpenNt(((SafeFileHandle)held[held.Count - 1]).DangerousGetHandle(), scope, true, false, null, true);
                held.Add(directory); AdministratorLaunch.FileInformation after;
                if (!GetFileInformationByHandle(directory, out after) || !SameInformation(before, after, true) || KernelName(directory) != folder || !Security(directory, true) || !AncestorSafe(directory, medium))
                    throw new UnauthorizedAccessException("The persistent maintenance directory identity changed.");
                string[] names = DirectoryNames(directory);
                if (names.Length == 0) { State = RegistrationState.Empty; return; }
                bool hasNative = false, hasLedger = false;
                foreach (string name in names) { if (name == NativeName && !hasNative) hasNative = true; else if (name == LedgerName && !hasLedger) hasLedger = true; else throw new UnauthorizedAccessException(); }
                if (!hasLedger) throw new UnauthorizedAccessException("An unowned native cache residue was retained for review.");
                AdministratorLaunch.FileInformation oldRecord, oldNative = new AdministratorLaunch.FileInformation();
                byte[] ledgerBytes;
                using (var readRecord = OpenRead(LedgerName)) {
                    if (!GetFileInformationByHandle(readRecord.SafeFileHandle, out oldRecord)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    ledgerBytes = ReadBounded(readRecord, 176); Registration = DecodeLedger(ledgerBytes);
                    if (Registration.Scope != scope || Registration.Sid != sid) throw new UnauthorizedAccessException("The persistent approval belongs to another scope or user.");
                    if (hasNative) using (var readNative = OpenRead(NativeName)) {
                        if (!GetFileInformationByHandle(readNative.SafeFileHandle, out oldNative) || readNative.Length <= 2 || readNative.Length > MaximumNative || Digest(readNative) != Registration.NativeHash)
                            throw new UnauthorizedAccessException("The retained native approval identity changed.");
                        Action = StableAction(readNative.SafeFileHandle, held, medium);
                    }
                }
                // Release only our own read leases, then revalidate the exact
                // physical files after exclusive DELETE acquisition. Existing
                // runtime leases deny these opens even if GetInstances says 0.
                record = OpenMutable(LedgerName); held.Add(record);
                if (!SameIdentity(record.SafeFileHandle, oldRecord) || !Equal(ReadBounded(record, 176), ledgerBytes)) throw new UnauthorizedAccessException();
                if (hasNative) {
                    native = OpenMutable(NativeName); held.Add(native);
                    if (!SameIdentity(native.SafeFileHandle, oldNative) || native.Length <= 2 || native.Length > MaximumNative || Digest(native) != Registration.NativeHash) throw new UnauthorizedAccessException();
                }
                State = hasNative ? RegistrationState.Complete : RegistrationState.LedgerOnly;
            }
            internal static MaintenanceCache Open(string scope, string sid, bool create, SafeFileHandle medium)
            {
                var held = new List<IDisposable>();
                try { return new MaintenanceCache(scope, sid, create, medium, held); }
                catch { DisposeAll(held); throw; }
            }
            FileStream OpenRead(string name)
            {
                var handle = OpenNt(directory.DangerousGetHandle(), name, false, false, null);
                try { CheckLeaf(handle, name); return new FileStream(handle, FileAccess.Read); }
                catch { handle.Dispose(); throw; }
            }
            FileStream OpenMutable(string name)
            {
                var handle = OpenNt(directory.DangerousGetHandle(), name, false, false, null, true);
                try { CheckLeaf(handle, name); return new FileStream(handle, FileAccess.Read); }
                catch { handle.Dispose(); throw; }
            }
            void CheckLeaf(SafeFileHandle file, string name)
            { if (KernelName(file) != folder + "\\" + name || !Security(file, false)) throw new UnauthorizedAccessException("A persistent maintenance leaf is not protected."); }
            public void DeleteNative()
            {
                if (State != RegistrationState.Complete || native == null) throw new InvalidOperationException();
                DeleteRetained(native.SafeFileHandle); native.Dispose(); native = null; State = RegistrationState.LedgerOnly; Action = null;
                string[] names = DirectoryNames(directory);
                if (names.Length != 1 || names[0] != LedgerName) throw new IOException("Persistent native cleanup could not be confirmed; the ledger was retained.");
            }
            public void DeleteLedger()
            {
                if (State != RegistrationState.LedgerOnly || record == null) throw new InvalidOperationException();
                string[] names = DirectoryNames(directory);
                if (names.Length != 1 || names[0] != LedgerName) throw new UnauthorizedAccessException();
                DeleteRetained(record.SafeFileHandle); record.Dispose(); record = null; State = RegistrationState.Empty; Registration = null; Action = null;
                ConfirmEmpty();
            }
            public void ConfirmEmpty()
            { if (State != RegistrationState.Empty || (directory != null && DirectoryNames(directory).Length != 0)) throw new IOException("Persistent approval cleanup could not be confirmed."); }
            internal void Create(Ledger ledger, byte[] scheduled, Action authorization)
            {
                if (authorization == null) throw new UnauthorizedAccessException();
                authorization(); ConfirmEmpty();
                if (directory == null || ledger.Scope != scope || ledger.Sid != sid || Digest(scheduled) != ledger.NativeHash) throw new UnauthorizedAccessException();
                // Write the ownership ledger first. A failed native creation
                // retains that record; unexpected or corrupt bytes fail closed.
                authorization(); CreateOrVerify(directory, LedgerName, EncodeLedger(ledger));
                authorization(); CreateOrVerify(directory, NativeName, scheduled);
                authorization();
                using (var approved = OpenRead(NativeName)) using (var registration = OpenRead(LedgerName)) {
                    var confirmed = DecodeLedger(ReadBounded(registration, 176));
                    if (confirmed.Scope != ledger.Scope || confirmed.Sid != ledger.Sid || confirmed.ControllerHash != ledger.ControllerHash || confirmed.NativeHash != ledger.NativeHash || Digest(approved) != ledger.NativeHash)
                        throw new UnauthorizedAccessException("The new persistent approval could not be confirmed.");
                    Action = StableAction(approved.SafeFileHandle, held, medium);
                }
                Registration = ledger; State = RegistrationState.Complete;
            }
            public void Dispose() { DisposeAll(held); }
        }
        static bool SameIdentity(SafeFileHandle file, AdministratorLaunch.FileInformation expected)
        { AdministratorLaunch.FileInformation actual; return GetFileInformationByHandle(file, out actual) && SameInformation(expected, actual, false); }
        static bool SameInformation(AdministratorLaunch.FileInformation a, AdministratorLaunch.FileInformation b, bool directory)
        { return AdministratorLaunch.SafeInformation(a, directory) && AdministratorLaunch.SafeInformation(b, directory) && a.Volume == b.Volume && a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow; }
        static void DeleteRetained(SafeFileHandle file)
        {
            byte remove = 1; IoStatus status;
            int result = NtSetInformationFile(file, out status, ref remove, 1, 13); // FileDispositionInformation; no name/path deletion.
            if (result < 0) throw new FileOperationException(result);
        }
        static void RequireHigh()
        {
            if (!AdministratorPayloadEntry.IsPayloadProcess || !AdministratorLaunch.HighToken(NativeProcessIdentity.Current) || !AdministratorLaunch.IsProtectedPayloadCurrent)
                throw new UnauthorizedAccessException("Only the authenticated protected administrator helper can change persistent approval.");
        }

        sealed class WindowsScheduler : ITaskScheduler
        {
            object service;
            internal WindowsScheduler() {
                const string id = "{0F87369F-A4E5-4CFC-BD3E-73E6154572DD}";
                using (var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                using (var overrideKey = user.OpenSubKey(@"Software\Classes\CLSID\" + id, false))
                    if (overrideKey != null) throw new UnauthorizedAccessException("Per-user Task Scheduler COM overrides are not permitted.");
                service = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(id), true)); Call(service, "Connect");
            }
            object Folder(bool create)
            {
                try { object existing = Call(service, "GetFolder", TaskFolder); return ProtectedFolder(existing); }
                catch (Exception error) {
                    if (!Missing(error) || !create) { if (Missing(error)) return null; throw; }
                    object root = Call(service, "GetFolder", "\\");
                    try { return ProtectedFolder(Call(root, "CreateFolder", "TaskbarSelect", "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)")); }
                    finally { Release(root); }
                }
            }
            static object ProtectedFolder(object folder)
            { try { if (!AdministratorLaunch.ProtectedSecurity(new RawSecurityDescriptor((string)Call(folder, "GetSecurityDescriptor", 7)), false)) throw new UnauthorizedAccessException("The persistent task folder is not protected."); return folder; } catch { Release(folder); throw; } }
            object Task(string name)
            {
                object folder = Folder(false); if (folder == null) return null;
                try { return Call(folder, "GetTask", name); }
                catch (Exception error) { if (Missing(error)) return null; throw; }
                finally { Release(folder); }
            }
            public TaskDefinition Read(string name)
            {
                object task = Task(name); if (task == null) return null;
                object definition = null, principal = null, actions = null, action = null, triggers = null, settings = null, registration = null;
                try {
                    definition = Get(task, "Definition"); principal = Get(definition, "Principal"); actions = Get(definition, "Actions"); triggers = Get(definition, "Triggers");
                    settings = Get(definition, "Settings"); registration = Get(definition, "RegistrationInfo");
                    int count = Number(Get(actions, "Count")); if (count == 1) action = Item(actions, 1);
                    return new TaskDefinition { Name = (string)Get(task, "Name"), Path = action == null ? null : (string)Get(action, "Path"), Arguments = action == null ? null : (string)Get(action, "Arguments"),
                        WorkingDirectory = action == null ? null : (string)Get(action, "WorkingDirectory"), Sid = TaskPrincipalSid((string)Get(principal, "UserId")), Author = (string)Get(registration, "Author"), Description = (string)Get(registration, "Description"),
                        Actions = count, ActionType = action == null ? -1 : Number(Get(action, "Type")), Triggers = Number(Get(triggers, "Count")), LogonType = Number(Get(principal, "LogonType")), RunLevel = Number(Get(principal, "RunLevel")),
                        MultipleInstances = Number(Get(settings, "MultipleInstances")), Priority = SavedPriority(Get(settings, "Priority")), Enabled = Truth(Get(settings, "Enabled")), AllowDemandStart = Truth(Get(settings, "AllowDemandStart")), Hidden = Truth(Get(settings, "Hidden")),
                        BatteryBlocked = Truth(Get(settings, "DisallowStartIfOnBatteries")), BatteryStops = Truth(Get(settings, "StopIfGoingOnBatteries")), NetworkRequired = Truth(Get(settings, "RunOnlyIfNetworkAvailable")),
                        IdleRequired = Truth(Get(settings, "RunOnlyIfIdle")), Wake = Truth(Get(settings, "WakeToRun")), StartWhenAvailable = Truth(Get(settings, "StartWhenAvailable")), ExecutionLimit = (string)Get(settings, "ExecutionTimeLimit"),
                        Security = new RawSecurityDescriptor((string)Call(task, "GetSecurityDescriptor", 7)) };
                } finally { Release(action); Release(actions); Release(triggers); Release(principal); Release(settings); Release(registration); Release(definition); Release(task); }
            }
            public void Register(TaskDefinition expected)
            {
                object folder = Folder(true), definition = null, principal = null, registration = null, settings = null, actions = null, action = null, registered = null;
                try {
                    definition = Call(service, "NewTask", 0); principal = Get(definition, "Principal"); registration = Get(definition, "RegistrationInfo"); settings = Get(definition, "Settings"); actions = Get(definition, "Actions"); action = Call(actions, "Create", 0);
                    Set(principal, "UserId", expected.Sid); Set(principal, "LogonType", 3); Set(principal, "RunLevel", 1);
                    Set(registration, "Author", expected.Author); Set(registration, "Description", expected.Description);
                    Set(action, "Path", expected.Path); Set(action, "Arguments", expected.Arguments); Set(action, "WorkingDirectory", expected.WorkingDirectory);
                    // Highest describes permissions, not CPU scheduling. Use
                    // Normal explicitly instead of Scheduler's background7.
                    Set(settings, "Priority", NormalTaskPriority);
                    Set(settings, "Enabled", true); Set(settings, "AllowDemandStart", true); Set(settings, "Hidden", true); Set(settings, "MultipleInstances", 2); Set(settings, "ExecutionTimeLimit", "PT0S");
                    Set(settings, "DisallowStartIfOnBatteries", false); Set(settings, "StopIfGoingOnBatteries", false); Set(settings, "RunOnlyIfNetworkAvailable", false); Set(settings, "RunOnlyIfIdle", false); Set(settings, "WakeToRun", false); Set(settings, "StartWhenAvailable", false);
                    registered = Call(folder, "RegisterTaskDefinition", expected.Name, definition, 2 | 16, expected.Sid, null, 3, TaskSecurity(expected.Sid));
                } finally { Release(registered); Release(action); Release(actions); Release(settings); Release(registration); Release(principal); Release(definition); Release(folder); }
            }
            public TaskInstance Run(string name, string[] arguments)
            {
                object task = Task(name), instance = null;
                if (task == null) throw new IOException("The persistent task is unavailable.");
                try { instance = Call(task, "Run", (object)arguments); string id = (string)Get(instance, "InstanceGuid"); var result = new TaskInstance { Id = id, Native = instance }; instance = null; return result; }
                finally { Release(instance); Release(task); }
            }
            public string[] Instances(string name)
            {
                object task = Task(name), instances = null;
                if (task == null) throw new IOException("The persistent task is unavailable.");
                try {
                    instances = Call(task, "GetInstances", 0); int count = Number(Get(instances, "Count"));
                    if (count < 0 || count > 32) throw new IOException("The persistent task instance count is invalid.");
                    string[] ids = new string[count];
                    for (int index = 0; index < count; index++) { object instance = Item(instances, index + 1); try { ids[index] = (string)Get(instance, "InstanceGuid"); } finally { Release(instance); } }
                    return ids;
                } finally { Release(instances); Release(task); }
            }
            public void Delete(string name) { object folder = Folder(false); if (folder == null) return; try { Call(folder, "DeleteTask", name, 0); } finally { Release(folder); } }
            public void Dispose() { Release(service); service = null; }
        }
        static object Call(object value, string name, params object[] arguments) { return value.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, value, arguments, CultureInfo.InvariantCulture); }
        static object Get(object value, string name) { return value.GetType().InvokeMember(name, BindingFlags.GetProperty, null, value, null, CultureInfo.InvariantCulture); }
        static void Set(object value, string name, object argument) { value.GetType().InvokeMember(name, BindingFlags.SetProperty, null, value, new[] { argument }, CultureInfo.InvariantCulture); }
        static object Item(object value, int index) { return value.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, value, new object[] { index }, CultureInfo.InvariantCulture); }
        static int Number(object value) { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        static int? SavedPriority(object value) { return value == null ? (int?)null : Number(value); }
        static bool Truth(object value) { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        static bool Missing(Exception value) { for (Exception item = value; item != null; item = item.InnerException) if (item.HResult == unchecked((int)0x80070002) || item.HResult == unchecked((int)0x80070003)) return true; return false; }
        static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

        [StructLayout(LayoutKind.Sequential)] struct Unicode { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)] struct Attributes { internal int Length; internal IntPtr Root, Name; internal uint Flags; internal IntPtr Security, Quality; }
        [StructLayout(LayoutKind.Sequential)] struct IoStatus { internal IntPtr Status, Information; }
        [StructLayout(LayoutKind.Sequential)] struct BasicProcess { internal IntPtr Reserved1, Peb, Reserved2, Reserved3, Pid, Parent; }
        [StructLayout(LayoutKind.Sequential)] struct GenericMapping { internal uint Read, Write, Execute, All; }
        [DllImport("ntdll.dll")] static extern int NtCreateFile(out SafeFileHandle file, uint access, ref Attributes attributes, out IoStatus status, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(SafeFileHandle process, int kind, out BasicProcess information, int bytes, out int returned);
        [DllImport("ntdll.dll")] static extern int NtOpenDirectoryObject(out SafeFileHandle directory, uint access, ref Attributes attributes);
        [DllImport("ntdll.dll")] static extern int NtOpenSymbolicLinkObject(out SafeFileHandle link, uint access, ref Attributes attributes);
        [DllImport("ntdll.dll")] static extern int NtQuerySymbolicLinkObject(SafeFileHandle link, ref Unicode target, out uint required);
        [DllImport("ntdll.dll")] static extern int NtQueryObject(SafeFileHandle handle, int kind, IntPtr information, uint length, out uint required);
        [DllImport("kernel32.dll", SetLastError = true)] static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeFileHandle process, out NativeProcessIdentity.FileTime created, out NativeProcessIdentity.FileTime exited, out NativeProcessIdentity.FileTime kernel, out NativeProcessIdentity.FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint count, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle file, out AdministratorLaunch.FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(uint process, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(SafeFileHandle process, uint access, out SafeFileHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(SafeFileHandle existing, uint access, IntPtr security, int level, int kind, out SafeFileHandle duplicate);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AccessCheck(IntPtr descriptor, SafeFileHandle token, uint desired, ref GenericMapping mapping, IntPtr privileges, ref uint privilegeBytes, out uint granted, out bool access);
        [DllImport("ntdll.dll")] static extern int NtQueryDirectoryFile(SafeFileHandle directory, IntPtr evt, IntPtr apc, IntPtr context, out IoStatus status, IntPtr buffer, uint length, int kind, bool single, IntPtr pattern, bool restart);
        [DllImport("ntdll.dll")] static extern int NtSetInformationFile(SafeFileHandle file, out IoStatus status, ref byte information, uint bytes, int kind);
        [DllImport("advapi32.dll")] static extern uint GetSecurityInfo(SafeFileHandle handle, int kind, uint requested, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        static SafeFileHandle OpenBoundProcess(uint pid, long started)
        {
            if (pid == 0 || started <= 0) throw new UnauthorizedAccessException();
            var handle = OpenProcess(0x101000, false, pid);
            try { NativeProcessIdentity.FileTime a, b, c, d; if (handle.IsInvalid || !GetProcessTimes(handle, out a, out b, out c, out d) || NativeProcessIdentity.CreationTicks(a) != started || !Alive(handle)) throw new UnauthorizedAccessException(); return handle; }
            catch { handle.Dispose(); throw; }
        }
        static SafeFileHandle OpenNativeParent()
        {
            var self = NativeProcessIdentity.Current;
            using (var process = OpenBoundProcess(self.Process, self.Started)) {
                BasicProcess information; int returned;
                if (NtQueryInformationProcess(process, 0, out information, Marshal.SizeOf(typeof(BasicProcess)), out returned) < 0 || returned != Marshal.SizeOf(typeof(BasicProcess))) throw new IOException("The native administrator parent is unavailable.");
                long raw = information.Parent.ToInt64(); if (raw <= 0 || raw > UInt32.MaxValue) throw new UnauthorizedAccessException();
                uint pid = (uint)raw; var info = NativeProcessIdentity.Read(pid);
                if (!AdministratorLaunch.HighToken(info) || ProcessSession(pid) != ProcessSession(self.Process)) throw new UnauthorizedAccessException();
                var parent = OpenBoundProcess(pid, info.Started);
                try { if (ProcessSid(parent) != CurrentSid()) throw new UnauthorizedAccessException(); using (var image = OpenKernelImage(parent)) if (!KernelName(image.SafeFileHandle).EndsWith("\\" + NativeName, StringComparison.Ordinal)) throw new UnauthorizedAccessException(); return parent; }
                catch { parent.Dispose(); throw; }
            }
        }
        static string ProcessSid(SafeFileHandle process)
        { SafeFileHandle token; if (!OpenProcessToken(process, 8, out token)) throw new Win32Exception(Marshal.GetLastWin32Error()); using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User.Value; }
        static string CurrentSid() { using (var identity = WindowsIdentity.GetCurrent()) return identity.User.Value; }
        static uint ProcessSession(uint pid) { uint session; if (!ProcessIdToSessionId(pid, out session)) throw new Win32Exception(Marshal.GetLastWin32Error()); return session; }
        static bool Alive(SafeFileHandle process) { uint result = WaitForSingleObject(process, 0); if (result == 258) return true; if (result == 0) return false; throw new Win32Exception(Marshal.GetLastWin32Error()); }
        static bool CurrentCaller(uint pid, long started) { var value = NativeProcessIdentity.Current; return value.Process == pid && value.Started == started && value.IntegrityAvailable && value.ElevationAvailable && value.IntegrityLevel == NativeProcessIdentity.Medium && !value.Elevated; }
        static SafeFileHandle MediumAccessToken(SafeFileHandle process)
        { SafeFileHandle original; if (!OpenProcessToken(process, 10, out original)) throw new Win32Exception(Marshal.GetLastWin32Error()); using (original) { SafeFileHandle duplicate; if (!DuplicateTokenEx(original, 8, IntPtr.Zero, 2, 2, out duplicate)) throw new Win32Exception(Marshal.GetLastWin32Error()); return duplicate; } }
        static FileStream OpenKernelImage(SafeFileHandle process)
        {
            var name = new StringBuilder(1024); int length = name.Capacity;
            if (!QueryFullProcessImageName(process, 1, name, ref length) || length <= 0 || length >= name.Capacity || name.Length != length || !KernelPath(name.ToString())) throw new IOException("The process physical image identity is unavailable.");
            SafeFileHandle handle = OpenNt(IntPtr.Zero, name.ToString(), false, false, null);
            try { if (KernelName(handle) != name.ToString()) throw new UnauthorizedAccessException(); return new FileStream(handle, FileAccess.Read); } catch { handle.Dispose(); throw; }
        }
        static string KernelName(SafeFileHandle file)
        { var name = new StringBuilder(1024); uint length = GetFinalPathNameByHandle(file, name, (uint)name.Capacity, 2); if (length == 0 || length >= name.Capacity || !KernelPath(name.ToString())) throw new IOException("A persistent image physical identity is invalid."); return name.ToString(); }
        // Device numbers are not persistent across restart. The retained file
        // supplies its stable volume GUID, while direct global object handles
        // prove that this name currently resolves to its exact physical volume.
        static string StableAction(SafeFileHandle file, List<IDisposable> held, SafeFileHandle medium)
        {
            var guid = new StringBuilder(1024); uint length = GetFinalPathNameByHandle(file, guid, (uint)guid.Capacity, 1);
            if (length == 0 || length >= guid.Capacity || guid.Length != length) throw new IOException("The persistent volume identity is unavailable.");
            string action = StableGuidAction(guid.ToString());
            if (action == null) throw new IOException("The persistent volume identity is invalid.");
            const string prefix = @"\\?\GLOBALROOT\GLOBAL??\";
            string globalTail = action.Substring(prefix.Length); int separator = globalTail.IndexOf('\\');
            string volume = globalTail.Substring(0, separator), suffix = globalTail.Substring(separator);
            var root = OpenNamespaceObject(IntPtr.Zero, "\\", true); held.Add(root);
            var global = OpenNamespaceObject(root.DangerousGetHandle(), "GLOBAL??", true); held.Add(global);
            var rootLink = OpenNamespaceObject(global.DangerousGetHandle(), "GLOBALROOT", false); held.Add(rootLink);
            var volumeLink = OpenNamespaceObject(global.DangerousGetHandle(), volume, false); held.Add(volumeLink);
            if (!NamespaceIdentity(root, "\\", true, medium) || !NamespaceIdentity(global, @"\GLOBAL??", true, medium) ||
                !NamespaceIdentity(rootLink, @"\GLOBAL??\GLOBALROOT", false, medium) || !RootNamespaceTarget(NamespaceTarget(rootLink, true)) ||
                !NamespaceIdentity(volumeLink, @"\GLOBAL??\" + volume, false, medium)) throw new UnauthorizedAccessException("The persistent global volume mapping is not protected.");
            string target = NamespaceTarget(volumeLink);
            if (!VolumeMappingMatches(action, target, KernelName(file)))
                throw new UnauthorizedAccessException("The persistent volume mapping does not match its retained image.");
            using (var actual = OpenNt(IntPtr.Zero, target + suffix, false, false, null))
                if (!SameFile(file, actual) || !String.Equals(NamespaceTarget(volumeLink), target, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The persistent volume mapping changed.");
            return action;
        }
        static SafeFileHandle OpenNamespaceObject(IntPtr parent, string name, bool directory)
        {
            if ((parent == IntPtr.Zero && name != "\\") || (parent != IntPtr.Zero && (String.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0)))
                throw new UnauthorizedAccessException();
            IntPtr text = Marshal.StringToHGlobalUni(name), unicode = IntPtr.Zero;
            try {
                var value = new Unicode { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = text };
                unicode = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Unicode))); Marshal.StructureToPtr(value, unicode, false);
                var attributes = new Attributes { Length = Marshal.SizeOf(typeof(Attributes)), Root = parent, Name = unicode, Flags = 0x40 };
                SafeFileHandle handle; int status = directory ? NtOpenDirectoryObject(out handle, 0x20003, ref attributes) : NtOpenSymbolicLinkObject(out handle, 0x20001, ref attributes);
                if (status < 0 || handle == null || handle.IsInvalid) { if (handle != null) handle.Dispose(); throw new UnauthorizedAccessException("The persistent global namespace could not be retained."); }
                return handle;
            } finally { if (unicode != IntPtr.Zero) Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
        }
        static string NamespaceName(SafeFileHandle handle)
        {
            IntPtr buffer = Marshal.AllocHGlobal(8192);
            try {
                uint required;
                if (NtQueryObject(handle, 1, buffer, 8192, out required) < 0 || required > 8192) throw new UnauthorizedAccessException();
                var value = (Unicode)Marshal.PtrToStructure(buffer, typeof(Unicode)); long offset = value.Buffer.ToInt64() - buffer.ToInt64();
                if (value.Length == 0 || (value.Length & 1) != 0 || offset < Marshal.SizeOf(typeof(Unicode)) || offset + value.Length > 8192) throw new UnauthorizedAccessException();
                return Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        static string NamespaceTarget(SafeFileHandle handle, bool allowRootEmpty = false)
        {
            IntPtr buffer = Marshal.AllocHGlobal(8192);
            try {
                var value = new Unicode { Buffer = buffer, MaximumLength = 8190 }; uint required;
                if (NtQuerySymbolicLinkObject(handle, ref value, out required) < 0 || value.Buffer != buffer || (value.Length == 0 && !allowRootEmpty) ||
                    (value.Length & 1) != 0 || value.Length > 8190 || required > 8192) throw new UnauthorizedAccessException();
                return Marshal.PtrToStringUni(buffer, value.Length / 2);
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        static bool NamespaceIdentity(SafeFileHandle handle, string name, bool directory, SafeFileHandle medium)
        {
            if (!String.Equals(NamespaceName(handle), name, StringComparison.OrdinalIgnoreCase)) return false;
            IntPtr a, b, c, d, descriptor;
            if (GetSecurityInfo(handle, 6, 7, out a, out b, out c, out d, out descriptor) != 0 || descriptor == IntPtr.Zero) return false;
            IntPtr privileges = Marshal.AllocHGlobal(4096);
            try {
                uint length = GetSecurityDescriptorLength(descriptor); if (length < 20 || length > 65536) return false;
                byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length); var raw = new RawSecurityDescriptor(bytes, 0);
                if (raw.Owner == null || (raw.Owner.Value != Admins && raw.Owner.Value != SystemSid) || raw.Group == null || raw.DiscretionaryAcl == null ||
                    (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0) return false;
                // Removal compares immutable metadata but never runs this image.
                // Every Install/Inspect execution boundary has the actual MED
                // caller's retained impersonation token for effective denial.
                if (medium == null) return true;
                var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200A0, All = 0x1F01FF };
                uint size = 4096, granted; bool access;
                return AccessCheck(descriptor, medium, 0x02000000, ref mapping, privileges, ref size, out granted, out access) && access && size <= 4096 && NamespaceRightsDenied(granted, directory);
            } finally { Marshal.FreeHGlobal(privileges); LocalFree(descriptor); }
        }
        static bool PhysicalVolume(string path)
        {
            const string prefix = @"\Device\HarddiskVolume";
            if (path == null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length <= prefix.Length) return false;
            for (int index = prefix.Length; index < path.Length; index++) if (path[index] < '0' || path[index] > '9') return false;
            return true;
        }
        static bool KernelPath(string path)
        {
            const string prefix = "\\Device\\HarddiskVolume";
            if (String.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length >= 1024) return false;
            int end = path.IndexOf('\\', prefix.Length); if (end <= prefix.Length) return false;
            for (int index = prefix.Length; index < end; index++) if (path[index] < '0' || path[index] > '9') return false;
            string tail = path.Substring(end + 1); if (tail.Length == 0) return true;
            return AdministratorLaunch.CanonicalDosPath("C:\\" + tail) != null;
        }
        sealed class FileOperationException : IOException { internal readonly int Status; internal FileOperationException(int status) : base("A persistent administrator file operation failed. [nt=0x" + unchecked((uint)status).ToString("X8", CultureInfo.InvariantCulture) + "]") { Status = status; } }
        static SafeFileHandle OpenNt(IntPtr parent, string name, bool directory, bool create, string security, bool maintenance = false)
        {
            if (parent == IntPtr.Zero ? !KernelPath(name) : String.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0 || name == "." || name == "..") throw new IOException("A persistent file component is invalid.");
            IntPtr text = Marshal.StringToHGlobalUni(name), unicode = IntPtr.Zero, descriptor = IntPtr.Zero;
            try {
                var value = new Unicode { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = text };
                unicode = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Unicode))); Marshal.StructureToPtr(value, unicode, false);
                if (security != null) { var raw = new RawSecurityDescriptor(security); byte[] bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0); descriptor = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, descriptor, bytes.Length); }
                var attributes = new Attributes { Length = Marshal.SizeOf(typeof(Attributes)), Root = parent, Name = unicode, Flags = 0x1040, Security = descriptor };
                SafeFileHandle file; IoStatus status;
                uint access = create && !directory ? 0x12019Fu : 0x120089u;
                if (maintenance && !directory) access |= 0x10000u; // DELETE only for exact retained leaves.
                int result = NtCreateFile(out file, access, ref attributes, out status, IntPtr.Zero, 0x80, maintenance ? 0u : 1u, create ? 2u : 1u, (directory ? 1u : 0x40u) | 0x20 | 0x200000, IntPtr.Zero, 0);
                if (result < 0) { if (file != null) file.Dispose(); throw new FileOperationException(result); }
                try { AdministratorLaunch.FileInformation info; if (!GetFileInformationByHandle(file, out info) || !AdministratorLaunch.SafeInformation(info, directory)) throw new UnauthorizedAccessException(); return file; }
                catch { file.Dispose(); throw; }
            } finally { if (descriptor != IntPtr.Zero) Marshal.FreeHGlobal(descriptor); if (unicode != IntPtr.Zero) Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
        }
        static bool Security(SafeFileHandle file, bool directory)
        {
            IntPtr a, b, c, d, descriptor; uint error = GetSecurityInfo(file, 1, 7, out a, out b, out c, out d, out descriptor);
            if (error != 0 || descriptor == IntPtr.Zero) return false;
            try { uint length = GetSecurityDescriptorLength(descriptor); if (length < 20 || length > 65536) return false; byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length); return AdministratorLaunch.ProtectedSecurity(new RawSecurityDescriptor(bytes, 0), directory); }
            finally { LocalFree(descriptor); }
        }
        static bool AncestorSafe(SafeFileHandle file, SafeFileHandle medium)
        {
            if (medium == null) return false;
            IntPtr a, b, c, d, descriptor; if (GetSecurityInfo(file, 1, 7, out a, out b, out c, out d, out descriptor) != 0 || descriptor == IntPtr.Zero) return false;
            IntPtr privileges = Marshal.AllocHGlobal(4096);
            try { var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200A0, All = 0x1F01FF }; uint bytes = 4096, granted; bool access;
                return AccessCheck(descriptor, medium, 0x02000000, ref mapping, privileges, ref bytes, out granted, out access) && access && bytes <= 4096 && AncestorRightsDenied(granted);
            } finally { Marshal.FreeHGlobal(privileges); LocalFree(descriptor); }
        }
        static string PinCache(string scope, bool create, List<IDisposable> held, SafeFileHandle medium)
        {
            TaskName(scope);
            string common = AdministratorLaunch.CanonicalDosPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            if (common == null) throw new IOException("The persistent administrator root is invalid.");
            SafeFileHandle commonProbe = CreateFile(common, 0x120089, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            string physical;
            using (commonProbe) { AdministratorLaunch.FileInformation info; if (commonProbe.IsInvalid || !GetFileInformationByHandle(commonProbe, out info) || !AdministratorLaunch.SafeInformation(info, true)) throw new UnauthorizedAccessException(); physical = KernelName(commonProbe); }
            int firstSlash = physical.IndexOf('\\', "\\Device\\HarddiskVolume".Length);
            string root = physical.Substring(0, firstSlash + 1); SafeFileHandle parent = OpenNt(IntPtr.Zero, root, true, false, null); held.Add(parent);
            if (medium != null && !AncestorSafe(parent, medium)) throw new UnauthorizedAccessException("A persistent executable ancestor permits standard-permission mutation.");
            string[] components = physical.Substring(firstSlash + 1).Split('\\'); string current = root.TrimEnd('\\');
            foreach (string component in components) { current += "\\" + component; var next = OpenNt(parent.DangerousGetHandle(), component, true, false, null); held.Add(next); if (KernelName(next) != current || (medium != null && !AncestorSafe(next, medium))) throw new UnauthorizedAccessException("A persistent executable ancestor permits standard-permission mutation."); parent = next; }
            foreach (string component in new[] { CacheName, scope }) {
                current += "\\" + component; SafeFileHandle next;
                try { next = OpenNt(parent.DangerousGetHandle(), component, true, false, null); }
                catch (FileOperationException error) { if (!create || (error.Status != unchecked((int)0xC0000034) && error.Status != unchecked((int)0xC000003A))) throw; next = OpenNt(parent.DangerousGetHandle(), component, true, true, DirectorySecurity); }
                held.Add(next); if (KernelName(next) != current || !Security(next, true)) throw new UnauthorizedAccessException("The persistent administrator directory is not protected."); parent = next;
            }
            return current;
        }
        static FileStream ReadRegistration(string folder, string scope, string sid, List<IDisposable> held, out Ledger ledger)
        {
            // Querying a Win32 physical spelling does not execute it; pin every
            // returned entry by a direct NT handle before trusting its contents.
            string[] names = DirectoryNames(folder);
            if (names.Length != 2) throw new UnauthorizedAccessException("The persistent administrator cache contains unexpected entries.");
            bool sawNative = false, sawLedger = false;
            foreach (string leaf in names) { if (leaf == NativeName && !sawNative) sawNative = true; else if (leaf == LedgerName && !sawLedger) sawLedger = true; else throw new UnauthorizedAccessException(); }
            var nativeHandle = OpenNt(IntPtr.Zero, folder + "\\" + NativeName, false, false, null);
            if (!Security(nativeHandle, false)) { nativeHandle.Dispose(); throw new UnauthorizedAccessException(); }
            var native = new FileStream(nativeHandle, FileAccess.Read); held.Add(native);
            var ledgerHandle = OpenNt(IntPtr.Zero, folder + "\\" + LedgerName, false, false, null);
            if (!Security(ledgerHandle, false)) { ledgerHandle.Dispose(); throw new UnauthorizedAccessException(); }
            var record = new FileStream(ledgerHandle, FileAccess.Read); held.Add(record);
            ledger = DecodeLedger(ReadBounded(record, 176));
            if (ledger.Scope != scope || ledger.Sid != sid || native.Length <= 2 || native.Length > MaximumNative || Digest(native) != ledger.NativeHash || KernelName(native.SafeFileHandle) != folder + "\\" + NativeName ||
                KernelName(record.SafeFileHandle) != folder + "\\" + LedgerName) throw new UnauthorizedAccessException("The persistent administrator approval identity changed.");
            return native;
        }
        static string[] DirectoryNames(string folder)
        { using (var directory = OpenNt(IntPtr.Zero, folder, true, false, null)) return DirectoryNames(directory); }
        static string[] DirectoryNames(SafeFileHandle directory)
        {
            var names = new List<string>(); IntPtr buffer = Marshal.AllocHGlobal(4096);
            try {
                    for (int round = 0; round < 8; round++) {
                        IoStatus status; int result = NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out status, buffer, 4096, 12, false, IntPtr.Zero, round == 0);
                        if (result == unchecked((int)0x80000006)) return names.ToArray();
                        long used = status.Information.ToInt64(); if (result < 0 || used < 12 || used > 4096) throw new IOException("The persistent directory could not be completely inspected.");
                        int offset = 0;
                        do {
                            if (offset + 12 > used) throw new IOException();
                            uint next = unchecked((uint)Marshal.ReadInt32(buffer, offset)), length = unchecked((uint)Marshal.ReadInt32(buffer, offset + 8));
                            if (length == 0 || (length & 1) != 0 || offset + 12L + length > used) throw new IOException();
                            string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 12), (int)length / 2);
                            if (name != "." && name != "..") { if (name.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0 || names.Count >= 2) throw new UnauthorizedAccessException(); names.Add(name); }
                            if (next == 0) break; if (next < 12 || (next & 3) != 0 || offset + (long)next >= used) throw new IOException(); offset += (int)next;
                        } while (true);
                    }
                    throw new IOException("The persistent directory enumeration exceeded its bound.");
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        static void CreateOrVerify(SafeFileHandle directory, string name, byte[] bytes)
        {
            SafeFileHandle file;
            try { file = OpenNt(directory.DangerousGetHandle(), name, false, true, FileSecurity); }
            catch (FileOperationException error) {
                if (error.Status != unchecked((int)0xC0000035)) throw;
                using (var existing = OpenNt(directory.DangerousGetHandle(), name, false, false, null)) {
                    if (!Security(existing, false)) throw new UnauthorizedAccessException();
                    using (var stream = new FileStream(existing, FileAccess.Read)) if (!Equal(ReadBounded(stream, bytes.Length), bytes)) throw new UnauthorizedAccessException("The existing persistent approval differs from this application.");
                }
                return;
            }
            using (file) {
                if (!Security(file, false)) throw new UnauthorizedAccessException();
                using (var stream = new FileStream(file, FileAccess.Write)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            }
        }
        static string CurrentPayloadHash()
        { using (var stream = typeof(PersistentAdministrator).Assembly.GetManifestResourceStream("TaskbarSelect.AdministratorPayloadHash")) { if (stream == null || stream.Length != 64) throw new IOException("The approved payload identity is unavailable."); byte[] value = ReadBounded(stream, 64); string hash = Encoding.ASCII.GetString(value); if (!AdministratorLaunch.ValidHash(hash)) throw new IOException(); return hash; } }
        static byte[] ReadBounded(Stream stream, int maximum)
        { if (!stream.CanRead || !stream.CanSeek || stream.Length < 0 || stream.Length > maximum) throw new IOException("A persistent administrator record exceeds its bound."); stream.Position = 0; byte[] bytes = new byte[(int)stream.Length]; int offset = 0; while (offset < bytes.Length) { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count <= 0) throw new EndOfStreamException(); offset += count; } return bytes; }
        static bool SameFile(SafeFileHandle a, SafeFileHandle b) { AdministratorLaunch.FileInformation x, y; return GetFileInformationByHandle(a, out x) && GetFileInformationByHandle(b, out y) && AdministratorLaunch.SafeInformation(x, false) && AdministratorLaunch.SafeInformation(y, false) && x.Volume == y.Volume && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow; }
        static bool Equal(byte[] a, byte[] b) { if (a == null || b == null || a.Length != b.Length) return false; for (int index = 0; index < a.Length; index++) if (a[index] != b[index]) return false; return true; }
        static byte[] Unhex(string text) { byte[] value = new byte[32]; for (int index = 0; index < value.Length; index++) value[index] = Byte.Parse(text.Substring(index * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture); return value; }
        static string Hex(byte[] value, int offset, int count) { var text = new StringBuilder(count * 2); for (int index = 0; index < count; index++) text.Append(value[offset + index].ToString("x2", CultureInfo.InvariantCulture)); return text.ToString(); }
        static string Digest(byte[] bytes) { using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes), 0, 32); }
        static string Digest(Stream stream) { stream.Position = 0; using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(stream), 0, 32); }
    }
}
