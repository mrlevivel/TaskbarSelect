using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarSelect
{
    // Fixed disposable-hosted test host. The real GUI image is the MED parent
    // admitted by the native bootstrap; loading the GUI assembly into a fixture
    // process would not establish that identity. This entry never constructs a
    // Controller, discovers taskbars, installs shortcuts, or changes Startup.
    internal static class PersistentAdministratorHostTests
    {
        const int MaximumFrame = 8192;
        const string Prefix = "TaskbarSelect.PersistentFixture.";
        static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;
        static readonly object exchange = new object();
        static readonly object identityGate = new object();
        static string nonce, fixturePath, fixtureHash, runtime, ownSid, currentScope;
        static uint highPid, ownPid, ownSession;
        static long highStarted, ownStarted;
        static int currentSequence, admissionPhase;
        static NamedPipeServerStream control;
        static FileStream fixtureLease;
        static AdministratorSupport.ProcessLifetime highLifetime;
        static AdministratorSupport heartbeat;

        internal sealed class Message
        {
            internal int Kind, Sequence;
            internal long[] Values;
            internal string Path, Token;
        }
        sealed class Completion
        {
            internal int Done, Count;
            internal Exception Error;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(uint pid, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetSystemWindowsDirectoryW(StringBuilder path, uint count);

        static void Require(bool value)
        { if (!value) throw new InvalidOperationException("The fixed hosted persistent fixture proof failed."); }
        static bool Hex(string text, int count)
        { return text != null && text.Length == count && text.Any(c => c != '0') && text.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
        static string Hash(Stream stream)
        {
            stream.Position = 0;
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        static string Sid(uint pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid), token = IntPtr.Zero;
            Require(process != IntPtr.Zero);
            try {
                Require(OpenProcessToken(process, 8, out token));
                using (var identity = new WindowsIdentity(token)) { Require(identity.User != null); return identity.User.Value; }
            } finally { if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(process); }
        }
        static bool TrustedHigh()
        {
            lock (identityGate) try {
                if (highLifetime == null || highLifetime.HasExited || fixtureLease == null) return false;
                var peer = NativeProcessIdentity.Read(highPid); uint session;
                return peer != null && peer.Process == highPid && peer.Started == highStarted && peer.IntegrityAvailable &&
                    peer.ElevationAvailable && peer.IntegrityLevel == NativeProcessIdentity.High && peer.Elevated &&
                    ProcessIdToSessionId(highPid, out session) && session == ownSession && Sid(highPid) == ownSid &&
                    String.Equals(NativeProcessIdentity.Path(highPid), fixturePath, StringComparison.OrdinalIgnoreCase) && Hash(fixtureLease) == fixtureHash;
            } catch { return false; }
        }
        static string WindowsDirectory()
        {
            var buffer = new StringBuilder(512); uint count = GetSystemWindowsDirectoryW(buffer, (uint)buffer.Capacity);
            Require(count > 0 && count < buffer.Capacity); return System.IO.Path.GetFullPath(buffer.ToString()).TrimEnd('\\');
        }
        static void SafeAncestors(string path)
        {
            for (string current = System.IO.Path.GetFullPath(path); current != null; current = System.IO.Path.GetDirectoryName(current.TrimEnd('\\'))) {
                if (File.Exists(current)) Require(false);
                if (Directory.Exists(current)) Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0);
                if (current.Length == 3) break;
            }
        }
        static void Admit(string[] args)
        {
            admissionPhase = 1;
            Require(args.Length == 8 && args[0] == "--self-test-persistent-host" && args[6] == "--test-profile" && AppProfile.IsTest);
            admissionPhase = 2;
            Require(Environment.Is64BitProcess && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" && Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows");
            admissionPhase = 3;
            nonce = args[1]; fixtureHash = args[4]; Require(Hex(nonce, 32) && Hex(fixtureHash, 64));
            Require(UInt32.TryParse(args[2], NumberStyles.None, Numbers, out highPid) && highPid > 0 &&
                Int64.TryParse(args[3], NumberStyles.None, Numbers, out highStarted) && highStarted > 0);
            admissionPhase = 4;
            runtime = AdministratorLaunch.CanonicalDosPath(args[5]); Require(runtime != null);
            string leaf = System.IO.Path.GetFileName(runtime), systemTemp = System.IO.Path.Combine(WindowsDirectory(), "Temp");
            const string runtimePrefix = "TaskbarSelectCiRuntime.";
            Require(leaf.StartsWith(runtimePrefix, StringComparison.Ordinal) && Hex(leaf.Substring(runtimePrefix.Length), 32) &&
                String.Equals(System.IO.Path.GetDirectoryName(runtime), systemTemp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(runtime));
            admissionPhase = 5;
            Require(String.Equals(AppProfile.Directory, System.IO.Path.Combine(runtime, "profile"), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(System.IO.Path.GetFullPath(args[7]), AppProfile.Directory, StringComparison.OrdinalIgnoreCase));
            SafeAncestors(runtime); SafeAncestors(AppProfile.Directory);
            admissionPhase = 6;
            ownPid = (uint)Process.GetCurrentProcess().Id;
            var own = NativeProcessIdentity.Read(ownPid);
            Require(own != null && own.IntegrityAvailable && own.ElevationAvailable && own.IntegrityLevel == NativeProcessIdentity.Medium && !own.Elevated &&
                own.Started > 0 && ProcessIdToSessionId(ownPid, out ownSession));
            ownStarted = own.Started; ownSid = Sid(ownPid);
            admissionPhase = 7;
            fixturePath = System.IO.Path.Combine(systemTemp, leaf.Replace(runtimePrefix, "TaskbarSelectCiSource."), "AdministratorCiLaunchProbe.exe");
            SafeAncestors(System.IO.Path.GetDirectoryName(fixturePath));
            fixtureLease = new FileStream(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Require(Hash(fixtureLease) == fixtureHash);
            admissionPhase = 8;
            highLifetime = AdministratorSupport.ProcessLifetime.Open(highPid, highStarted);
            Require(TrustedHigh());
        }
        static void Await(Completion item, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref item.Done) == 0 && clock.ElapsedMilliseconds < milliseconds && TrustedHigh()) {
                if (heartbeat != null) heartbeat.Poll();
                Application.DoEvents(); Thread.Sleep(5);
            }
            if (Volatile.Read(ref item.Done) == 0) { control.Dispose(); throw new TimeoutException("The owned hosted pipe deadline expired."); }
            if (item.Error != null) throw new IOException("The owned hosted pipe failed.", item.Error);
        }
        static void Connect()
        {
            var completed = new Completion(); var selected = control;
            selected.BeginWaitForConnection(delegate(IAsyncResult result) {
                try { selected.EndWaitForConnection(result); } catch (Exception error) { completed.Error = error; }
                finally { Volatile.Write(ref completed.Done, 1); }
            }, null);
            // The completion callback owns EndWait. No AsyncWaitHandle is ever
            // materialized or closed while Framework completion can signal it.
            Await(completed, 15000); uint client;
            Require(GetNamedPipeClientProcessId(control.SafePipeHandle.DangerousGetHandle(), out client) && client == highPid && TrustedHigh());
        }
        static byte[] Exact(int length, int milliseconds)
        {
            byte[] bytes = new byte[length]; int offset = 0; var clock = Stopwatch.StartNew();
            while (offset != bytes.Length) {
                var completed = new Completion();
                var selected = control;
                selected.BeginRead(bytes, offset, bytes.Length - offset, delegate(IAsyncResult result) {
                    try { completed.Count = selected.EndRead(result); } catch (Exception error) { completed.Error = error; }
                    finally { Volatile.Write(ref completed.Done, 1); }
                }, null);
                Await(completed, Math.Max(1, milliseconds - (int)clock.ElapsedMilliseconds));
                Require(completed.Count > 0 && clock.ElapsedMilliseconds <= milliseconds); offset += completed.Count;
            }
            return bytes;
        }
        static Message Receive(int milliseconds)
        {
            var clock = Stopwatch.StartNew(); int size = BitConverter.ToInt32(Exact(4, milliseconds), 0);
            Require(size > 0 && size <= MaximumFrame);
            var value = new JavaScriptSerializer().DeserializeObject(new UTF8Encoding(false, true).GetString(Exact(size, Math.Max(1, milliseconds - (int)clock.ElapsedMilliseconds)))) as Dictionary<string, object>;
            Require(value != null && value.Count == 5 && new[] { "kind", "sequence", "values", "path", "token" }.All(value.ContainsKey));
            var values = value["values"] as object[];
            Require(value["kind"] is int && value["sequence"] is int && values != null && values.Length <= 12 &&
                values.All(v => v is int || v is long) && value["path"] is string && value["token"] is string);
            return new Message { Kind = (int)value["kind"], Sequence = (int)value["sequence"], Values = values.Select(Convert.ToInt64).ToArray(), Path = (string)value["path"], Token = (string)value["token"] };
        }
        static void Send(int kind, int sequence, long[] values, string path = "", string token = "")
        {
            byte[] body = new UTF8Encoding(false, true).GetBytes(new JavaScriptSerializer().Serialize(new { kind = kind, sequence = sequence, values = values, path = path, token = token }));
            Require(body.Length > 0 && body.Length <= MaximumFrame && TrustedHigh());
            byte[] packet = new byte[4 + body.Length]; Array.Copy(BitConverter.GetBytes(body.Length), packet, 4); Array.Copy(body, 0, packet, 4, body.Length);
            var completed = new Completion(); var selected = control;
            selected.BeginWrite(packet, 0, packet.Length, delegate(IAsyncResult result) {
                try { selected.EndWrite(result); } catch (Exception error) { completed.Error = error; }
                finally { Volatile.Write(ref completed.Done, 1); }
            }, null);
            Await(completed, 10000);
        }
        static void SponsorBootstrap(AdministratorLaunch.BootstrapImage image, string token, uint parent, long started)
        {
            lock (exchange) {
                Require(image != null && Hex(token, 32) && parent == ownPid && started == ownStarted && TrustedHigh());
                Send(20, currentSequence, new long[] { parent, started }, image.Path, token);
                var reply = Receive(15000);
                Require(reply.Kind == 21 && reply.Sequence == currentSequence && reply.Values.Length == 2 && reply.Path == "" && reply.Token == "" &&
                    reply.Values[0] > 0 && reply.Values[0] <= UInt32.MaxValue && reply.Values[1] > ownStarted && TrustedHigh());
            }
        }
        static void ObserveScheduledBootstrap(uint pid, long started)
        {
            lock (exchange) {
                Require(pid > 0 && started > ownStarted && TrustedHigh());
                Send(22, currentSequence, new long[] { pid, started });
                var reply = Receive(15000);
                Require(reply.Kind == 23 && reply.Sequence == currentSequence && reply.Values.Length == 1 && reply.Values[0] == 1 &&
                    reply.Path == "" && reply.Token == "" && TrustedHigh());
            }
        }
        static void WaitForActive(AdministratorSupport support)
        {
            var clock = Stopwatch.StartNew();
            while (!support.Active && support.Enabling && clock.ElapsedMilliseconds < 20000 && TrustedHigh()) { support.Poll(); Application.DoEvents(); Thread.Sleep(10); }
            Require(support.Active && !support.Enabling && !support.HasUncertainOperation && TrustedHigh());
        }
        static object Com(object value, string member, BindingFlags flags, params object[] args)
        { return value.GetType().InvokeMember(member, flags, null, value, args, Numbers); }
        static uint SearchLaunch()
        {
            Require(TrustedHigh());
            string root = System.IO.Path.Combine(AppProfile.Directory, "owned-search"); Require(!Directory.Exists(root) && !File.Exists(root));
            Directory.CreateDirectory(root); SafeAncestors(root);
            string shortcut = System.IO.Path.Combine(root, "Persistent neutral probe.lnk"); object shell = null, link = null;
            string arguments = "--persistent-search-probe " + nonce + " " + highPid.ToString(Numbers) + " " + highStarted.ToString(Numbers);
            try {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                link = Com(shell, "CreateShortcut", BindingFlags.InvokeMethod, shortcut);
                Com(link, "TargetPath", BindingFlags.SetProperty, fixturePath); Com(link, "Arguments", BindingFlags.SetProperty, arguments);
                Com(link, "WorkingDirectory", BindingFlags.SetProperty, System.IO.Path.GetDirectoryName(fixturePath)); Com(link, "Save", BindingFlags.InvokeMethod);
            } finally { if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
            var apps = GhostAppCatalog.ReadRoots(new[] { root }, delegate { return false; });
            Require(apps.Count == 1 && apps[0].CatalogShortcut && String.Equals(apps[0].TargetPath, fixturePath, StringComparison.OrdinalIgnoreCase) && apps[0].Arguments == arguments);
            var result = GhostAppCatalog.Launch(apps[0]); Require(result.Started); return result.ProcessId;
        }
        static bool WriteDenied(string file)
        {
            try { using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.Read)) return false; }
            catch (UnauthorizedAccessException error) { return error.HResult == unchecked((int)0x80070005); }
        }
        static long[] InspectNegatives(string scope, AdministratorSupport support)
        {
            Require(!support.Active && !support.Enabling);
            string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), PersistentAdministrator.CacheName, scope);
            bool nativeDenied = WriteDenied(System.IO.Path.Combine(root, PersistentAdministrator.NativeName));
            bool ledgerDenied = WriteDenied(System.IO.Path.Combine(root, PersistentAdministrator.LedgerName));
            string otherScope = (scope[0] == '0' ? "1" : "0") + scope.Substring(1);
            bool absentRejected; using (var absent = PersistentAdministrator.Inspect(otherScope)) absentRejected = !absent.Available;
            bool ownAvailable; using (var own = PersistentAdministrator.Inspect(scope)) ownAvailable = own.Available;
            bool argumentsRejected = !PersistentAdministrator.RunArguments(new string('0', 32), ownPid, ownStarted) &&
                !PersistentAdministrator.RunArguments(nonce, 0, ownStarted) && !PersistentAdministrator.RunArguments(nonce, ownPid, 0);
            Require(nativeDenied && ledgerDenied && absentRejected && ownAvailable && argumentsRejected);
            return new long[] { nativeDenied ? 1 : 0, ledgerDenied ? 1 : 0, absentRejected ? 1 : 0, ownAvailable ? 1 : 0, argumentsRejected ? 1 : 0 };
        }
        static string DiagnosticName(string value)
        { return value != null && value.Length > 0 && value.Length <= 96 && value.All(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_') ? value : "unknown"; }
        static void ScopeDiagnostic()
        {
            bool queryCurrent = false, sameUser = false, pureScope = false; int queryHr = 0, pureHr = 0;
            try {
                using (var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query)) {
                    queryCurrent = identity.User != null; sameUser = queryCurrent && identity.User.Value == ownSid;
                }
            } catch (Exception error) { queryHr = error.HResult; }
            try { pureScope = Hex(PersistentAdministrator.ComputeScope(ownSid, AppProfile.Directory, AppProfile.IsTest), 64); }
            catch (Exception error) { pureHr = error.HResult; }
            // Diagnostic results never replace the failed required production scope call.
            Console.Error.WriteLine("Persistent scope diagnostic queryCurrent=" + queryCurrent + " sameUser=" + sameUser + " queryHr=0x" + queryHr.ToString("X8", Numbers) +
                " pureScope=" + pureScope + " pureHr=0x" + pureHr.ToString("X8", Numbers));
        }
        static string BoundedDiagnosticText(string value)
        { if (value == null) return ""; value = value.Replace('\r', ' ').Replace('\n', ' '); return value.Length <= 512 ? value : value.Substring(0, 512); }
        static void SupportDiagnostic(AdministratorSupport support)
        {
            if (support == null) return;
            Console.Error.WriteLine("Persistent support diagnostic status=" + BoundedDiagnosticText(support.Status) + " active=" + support.Active + " enabling=" + support.Enabling +
                " uncertain=" + support.HasUncertainOperation + " lastError=" + BoundedDiagnosticText(support.LastError));
            if (!AppProfile.IsTest || !Hex(currentScope, 64) || !TrustedHigh()) return;
            object service = null, folder = null, task = null, instances = null;
            try {
                using (var approved = PersistentAdministrator.Inspect(currentScope)) {
                    Console.Error.WriteLine("Persistent task diagnostic protectedRegistration=" + approved.Available);
                    if (!approved.Available) return;
                    // Fixed current-profile task only, after its retained protected
                    // image/ledger/definition proof. No Run, Stop or mutation call.
                    service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)); Com(service, "Connect", BindingFlags.InvokeMethod);
                    folder = Com(service, "GetFolder", BindingFlags.InvokeMethod, PersistentAdministrator.TaskFolder);
                    task = Com(folder, "GetTask", BindingFlags.InvokeMethod, PersistentAdministrator.TaskName(currentScope));
                    long result = Convert.ToInt64(Com(task, "LastTaskResult", BindingFlags.GetProperty), Numbers);
                    Require(result >= Int32.MinValue && result <= UInt32.MaxValue);
                    int state = Convert.ToInt32(Com(task, "State", BindingFlags.GetProperty), Numbers);
                    DateTime ran = (DateTime)Com(task, "LastRunTime", BindingFlags.GetProperty);
                    instances = Com(task, "GetInstances", BindingFlags.InvokeMethod, 0);
                    int count = Convert.ToInt32(Com(instances, "Count", BindingFlags.GetProperty), Numbers); Require(count >= 0 && count <= 100);
                    Console.Error.WriteLine("Persistent task diagnostic result=0x" + unchecked((uint)result).ToString("X8", Numbers) + " state=" + state.ToString(Numbers) +
                        " instances=" + count.ToString(Numbers) + " lastRunUtcTicks=" + ran.ToUniversalTime().Ticks.ToString(Numbers));
                }
            } catch (Exception error) { Console.Error.WriteLine("Persistent task diagnostic readHr=0x" + error.HResult.ToString("X8", Numbers) + " type=" + DiagnosticName(error.GetType().Name)); }
            finally {
                foreach (object item in new[] { instances, task, folder, service }) try { if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); } catch { }
            }
        }
        internal static int Run(string[] args)
        {
            AdministratorSupport support = null; int result = 2;
            try {
                Admit(args); admissionPhase = 9; string scope = PersistentAdministrator.ScopeForCurrentProfile(); currentScope = scope; admissionPhase = 90; Require(Hex(scope, 64));
                admissionPhase = 91;
                var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(ownSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-5-18"), PipeAccessRights.FullControl, AccessControlType.Allow));
                admissionPhase = 10;
                control = new NamedPipeServerStream(Prefix + nonce, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, security);
                admissionPhase = 11; Connect(); support = new AdministratorSupport(); heartbeat = support;
                admissionPhase = 12;
                Send(0, 0, new long[] { ownPid, ownStarted, ownSession, NativeProcessIdentity.Medium }, "", scope);
                for (int expected = 1; expected <= 32; expected++) {
                    Message request; lock (exchange) request = Receive(60000);
                    Require(request.Sequence == expected && request.Kind >= 1 && request.Kind <= 9 && request.Values.Length == 0 && request.Path == "" && request.Token == "" && TrustedHigh());
                    currentSequence = expected; admissionPhase = 20 + request.Kind; bool okay = true; uint child = 0; long[] details = new long[0];
                    if (request.Kind == 1) { support.EnableForHostedSponsor(SponsorBootstrap, TrustedHigh); WaitForActive(support); okay = support.InstallPersistent(scope, true); }
                    else if (request.Kind == 2) { support.EnablePersistentForHosted(ObserveScheduledBootstrap, TrustedHigh); WaitForActive(support); }
                    else if (request.Kind == 3) okay = support.StopForExit(5000);
                    else if (request.Kind == 4) okay = !support.HasUncertainOperation; // Read-only Status; reply carries the actual current fields.
                    else if (request.Kind == 5) {
                        Require(support.StopForExit(5000));
                        support.EnableForHostedSponsor(SponsorBootstrap, TrustedHigh); WaitForActive(support);
                        okay = support.RemovePersistent(scope, true);
                    }
                    else if (request.Kind == 6) { Require(support.Active); child = SearchLaunch(); }
                    else if (request.Kind == 7) okay = support.StopForExit(5000);
                    else if (request.Kind == 8) details = InspectNegatives(scope, support);
                    else if (request.Kind == 9) { using (var inspected = PersistentAdministrator.Inspect(scope)) details = new long[] { inspected.Available ? 1 : 0 }; }
                    Require(okay && !support.HasUncertainOperation);
                    lock (exchange) Send(100, expected, new long[] { request.Kind, okay ? 1 : 0, support.Active ? 1 : 0, support.Enabling ? 1 : 0, support.HasUncertainOperation ? 1 : 0, child }.Concat(details).ToArray());
                    if (request.Kind == 7) { result = 0; break; }
                }
            } catch (Exception error) {
                var native = error as System.ComponentModel.Win32Exception;
                Console.Error.WriteLine("Persistent hosted MED refusal phase=" + admissionPhase.ToString(Numbers) + " hr=0x" + error.HResult.ToString("X8", Numbers) +
                    " type=" + DiagnosticName(error.GetType().Name) + " method=" + DiagnosticName(error.TargetSite == null ? null : error.TargetSite.Name) +
                    " native=" + (native == null ? "unknown" : native.NativeErrorCode.ToString(Numbers)) +
                    " hostedFields=" + (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" && Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows" ? "true" : "false"));
                if (admissionPhase == 9) ScopeDiagnostic();
                SupportDiagnostic(support);
            } finally {
                heartbeat = null;
                if (support != null) { try { if (!support.StopForExit(5000)) result = 113; } catch { result = 113; } support.Dispose(); }
                if (control != null) control.Dispose(); control = null;
                if (highLifetime != null) highLifetime.Dispose(); highLifetime = null;
                if (fixtureLease != null) fixtureLease.Dispose(); fixtureLease = null;
            }
            return result;
        }
    }
}
