using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
internal static partial class AdministratorCiLaunchProbe {
 public sealed class Facts {
  public string Status {get;set;} public ProbeFacts Probe {get;set;}
  public string StatusScope {get;set;}
  public int Phase {get;set;}
  public int? EnableLUA {get;set;}
  public int? ConsentPromptBehaviorAdmin {get;set;}
  public bool ExistingBuiltinHighPrimary {get;set;}
  public bool AdmissionPassed {get;set;}
  public bool AccountCreateAttempted {get;set;}
  public bool AccountCreated {get;set;}
  public int AccountCreateStatus {get;set;}
  public bool CreatedAccountOwnershipVerified {get;set;}
  public int GroupAddStatus {get;set;}
  public bool InteractiveLogonSucceeded {get;set;}
  public int LogonWindowsError {get;set;}
  public int? LogonTokenType {get;set;}
  public int? LogonElevationType {get;set;}
  public uint? LogonIntegrityRid {get;set;}
  public bool LinkedTokenQuerySucceeded {get;set;}
  public int LinkedTokenWindowsError {get;set;}
  public int LinkedTokenReturnedBytes {get;set;}
  public int? HighTokenType {get;set;}
  public int? MediumTokenType {get;set;}
  public int? HighImpersonationLevel {get;set;}
  public int? MediumImpersonationLevel {get;set;}
  public int HighImpersonationWindowsError {get;set;}
  public int MediumImpersonationWindowsError {get;set;}
  public bool GenuineSameUserSessionPairVerified {get;set;}
  public bool FilteredMediumAdministratorDenyOnly {get;set;}
  public bool DuplicateMediumAttempted {get;set;}
  public int DuplicateMediumRequestedAccess {get;set;}
  public bool DuplicateMediumSucceeded {get;set;}
  public int DuplicateMediumWindowsError {get;set;}
  public bool DuplicatedFilteredPrimaryVerified {get;set;}
  public string HighPrimaryDuplicateStatus {get;set;}
  public bool DuplicateHighAttempted {get;set;}
  public int DuplicateHighRequestedAccess {get;set;}
  public bool DuplicateHighSucceeded {get;set;}
  public int DuplicateHighWindowsError {get;set;}
  public int HighDuplicateQueryWindowsError {get;set;}
  public int HighDuplicateQueryHResult {get;set;}
  public int? DuplicatedHighTokenType {get;set;}
  public bool DuplicatedHighPrimaryVerified {get;set;}
  public int NativeQueryWindowsError {get;set;}
  public int FailureHResult {get;set;}
  public bool CleanupOwnershipVerified {get;set;}
  public bool AccountRemoved {get;set;}
  public int AccountRemoveStatus {get;set;}
  public bool CleanupUncertain {get;set;}
 }
 sealed class Token {internal string Sid;internal uint Integrity,Session;internal long Authentication;internal int Type,ElevationType,ImpersonationWindowsError;internal int? ImpersonationLevel;internal bool Elevated,AdminEnabled,AdminDenyOnly;}
 sealed class NativeFailure : Exception {internal int Code;internal NativeFailure(int code){Code=code;}}
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct User1 {internal string Name,Password;internal uint PasswordAge,Privilege;internal string Home,Comment;internal uint Flags;internal string Script;}
 [StructLayout(LayoutKind.Sequential)] struct User23 {internal IntPtr Name,FullName,Comment;internal uint Flags;internal IntPtr Sid;}
 [StructLayout(LayoutKind.Sequential)] struct Member0 {internal IntPtr Sid;}
 [StructLayout(LayoutKind.Sequential)] struct SidAttributes {internal IntPtr Sid;internal uint Attributes;}
 [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr data,int size,out int returned);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool LogonUserW(string user,string domain,string password,uint kind,uint provider,out IntPtr token);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool DuplicateTokenEx(IntPtr token,uint access,IntPtr security,int impersonation,int kind,out IntPtr duplicate);
 [DllImport("netapi32.dll",CharSet=CharSet.Unicode)] static extern uint NetUserAdd(string server,uint level,ref User1 user,out uint parameterError);
 [DllImport("netapi32.dll",CharSet=CharSet.Unicode)] static extern uint NetUserGetInfo(string server,string user,uint level,out IntPtr data);
 [DllImport("netapi32.dll")] static extern uint NetApiBufferFree(IntPtr buffer);
 [DllImport("netapi32.dll",CharSet=CharSet.Unicode)] static extern uint NetUserDel(string server,string user);
 [DllImport("netapi32.dll",CharSet=CharSet.Unicode)] static extern uint NetLocalGroupAddMembers(string server,string group,uint level,ref Member0 member,uint count);
 static int? Policy(RegistryKey key,string name) {object value=key==null?null:key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);return value is int?(int?)value:null;}
 static IntPtr Query(IntPtr token,int kind,out int length) {
  int size; bool first=GetTokenInformation(token,kind,IntPtr.Zero,0,out size);int error=first?0:Marshal.GetLastWin32Error();
  if(first || error!=122 || size<=0 || size>65536)throw new NativeFailure(error==0?13:error);
  IntPtr data=Marshal.AllocHGlobal(size);
  if(!GetTokenInformation(token,kind,data,size,out length)){error=Marshal.GetLastWin32Error();Marshal.FreeHGlobal(data);throw new NativeFailure(error);}
  if(length<=0 || length>size){Marshal.FreeHGlobal(data);throw new NativeFailure(13);}return data;
 }
 static int Scalar(IntPtr token,int kind){
  IntPtr data=Marshal.AllocHGlobal(4);
  try{int length;if(!GetTokenInformation(token,kind,data,4,out length))throw new NativeFailure(Marshal.GetLastWin32Error());if(length!=4)throw new NativeFailure(13);return Marshal.ReadInt32(data);}finally{Marshal.FreeHGlobal(data);}
 }
 static string Sid(IntPtr token,int kind){int length;IntPtr data=Query(token,kind,out length);try{if(length<IntPtr.Size)throw new NativeFailure(13);return new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value;}finally{Marshal.FreeHGlobal(data);}}
 static Token Read(IntPtr handle) {
  var token=new Token{Sid=Sid(handle,1),Type=Scalar(handle,8),ElevationType=Scalar(handle,18),Elevated=Scalar(handle,20)!=0,Session=unchecked((uint)Scalar(handle,12))};
  if(token.Type==2){try{token.ImpersonationLevel=Scalar(handle,9);}catch(NativeFailure failure){token.ImpersonationWindowsError=failure.Code;}}
  string integrity=Sid(handle,25);token.Integrity=UInt32.Parse(integrity.Substring(integrity.LastIndexOf('-')+1),CultureInfo.InvariantCulture);
  int length;IntPtr data=Query(handle,2,out length);
  try {
   if(length<4)throw new NativeFailure(13);int count=Marshal.ReadInt32(data),stride=Marshal.SizeOf(typeof(SidAttributes)),offset=IntPtr.Size;
   if(count<0 || count>4096 || offset+(long)stride*count>length)throw new NativeFailure(13);
   for(int index=0;index<count;index++){var group=(SidAttributes)Marshal.PtrToStructure(new IntPtr(data.ToInt64()+offset+(long)stride*index),typeof(SidAttributes));if(new SecurityIdentifier(group.Sid).Value=="S-1-5-32-544"){token.AdminEnabled=(group.Attributes&4)!=0&&(group.Attributes&16)==0;token.AdminDenyOnly=(group.Attributes&16)!=0&&(group.Attributes&4)==0;}}
  }finally{Marshal.FreeHGlobal(data);}token.Authentication=Authentication(handle);return token;
 }
 static string AccountSid(string name) {
  IntPtr data;uint status=NetUserGetInfo(null,name,23,out data);if(status!=0)throw new NativeFailure(unchecked((int)status));
  try{return new SecurityIdentifier(((User23)Marshal.PtrToStructure(data,typeof(User23))).Sid).Value;}finally{NetApiBufferFree(data);}
 }
 static bool Medium(Token token){return token.Integrity==8192&&!token.Elevated&&token.ElevationType==3&&token.AdminDenyOnly;}
 static bool High(Token token){return token.Integrity==12288&&token.Elevated&&token.ElevationType==2&&token.AdminEnabled;}
 static bool ActualPair(Token high,Token medium){return high.Type==1&&High(high)&&medium.Type==1&&Medium(medium)&&high.Sid==medium.Sid&&high.Session==medium.Session;}
 // Raw linked-token SessionId is metadata, not the actual sponsor's process session.
 // The authentic process pair and source-primary clone retain separate strict checks.
 static int LinkedRefusal(Token context,Token medium){if(context.Type!=1&&context.Type!=2)return 13;if(!Medium(context))return 14;if(context.Sid!=medium.Sid)return 15;if(context.Authentication!=medium.Authentication)return 17;return 0;}
 static bool CloneMatches(Token clone,Token medium){return medium.Type==1&&Medium(medium)&&clone.Type==1&&Medium(clone)&&clone.Sid==medium.Sid&&clone.Session==medium.Session&&clone.Authentication==medium.Authentication;}
 public static Facts Run() {
  var facts=new Facts{Status="blocked",StatusScope="Filtered MED primary-token duplication and own-account cleanup only; HIGH duplication is reported separately",DuplicateMediumRequestedAccess=11,DuplicateHighRequestedAccess=11,HighPrimaryDuplicateStatus="not-attempted"};IntPtr current=IntPtr.Zero,logon=IntPtr.Zero,linked=IntPtr.Zero,duplicate=IntPtr.Zero,highDuplicate=IntPtr.Zero;
  string name=null,password=null,ownedSid=null;bool created=false;
  try {
   facts.Phase=1;
   using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))using(var policy=machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",false)){facts.EnableLUA=Policy(policy,"EnableLUA");facts.ConsentPromptBehaviorAdmin=Policy(policy,"ConsentPromptBehaviorAdmin");}
   facts.Phase=2;if(!OpenProcessToken(GetCurrentProcess(),8,out current))throw new NativeFailure(Marshal.GetLastWin32Error());Token caller=Read(current);
   facts.ExistingBuiltinHighPrimary=caller.Integrity==12288&&caller.Elevated&&caller.Type==1&&caller.Sid.EndsWith("-500",StringComparison.Ordinal);
   if(facts.EnableLUA!=1 || facts.ConsentPromptBehaviorAdmin!=0 || !facts.ExistingBuiltinHighPrimary)return facts;
   facts.AdmissionPassed=true;facts.Status="unavailable";facts.Phase=3;
   byte[] random=new byte[32];using(var rng=RandomNumberGenerator.Create()){rng.GetBytes(random);}password="Tsb!aA9"+Convert.ToBase64String(random);Array.Clear(random,0,random.Length);
   for(int attempt=0;attempt<3;attempt++) {
    name="TsbLab"+Guid.NewGuid().ToString("N").Substring(0,12);var user=new User1{Name=name,Password=password,Privilege=1,Comment="Disposable token feasibility",Flags=0x201};uint parameter;
    facts.AccountCreateAttempted=true;uint status=NetUserAdd(null,1,ref user,out parameter);facts.AccountCreateStatus=unchecked((int)status);
    if(status==0){created=true;facts.AccountCreated=true;break;}if(status!=2224)return facts;
   }
   if(!created)return facts;
   facts.Phase=4;ownedSid=AccountSid(name);if(ownedSid.EndsWith("-500",StringComparison.Ordinal))throw new NativeFailure(13);facts.CreatedAccountOwnershipVerified=true;
   facts.Phase=5;if(AccountSid(name)!=ownedSid)throw new NativeFailure(13);
   string group=((NTAccount)new SecurityIdentifier("S-1-5-32-544").Translate(typeof(NTAccount))).Value;group=group.Substring(group.LastIndexOf('\\')+1);
   var sid=new SecurityIdentifier(ownedSid);byte[] bytes=new byte[sid.BinaryLength];sid.GetBinaryForm(bytes,0);GCHandle pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
   try{var member=new Member0{Sid=pinned.AddrOfPinnedObject()};facts.GroupAddStatus=unchecked((int)NetLocalGroupAddMembers(null,group,0,ref member,1));}finally{pinned.Free();}
   if(facts.GroupAddStatus!=0)return facts;
   facts.Phase=6;facts.InteractiveLogonSucceeded=LogonUserW(name,Environment.MachineName,password,2,0,out logon);facts.LogonWindowsError=facts.InteractiveLogonSucceeded?0:Marshal.GetLastWin32Error();if(!facts.InteractiveLogonSucceeded)return facts;
   Token original=Read(logon);facts.LogonTokenType=original.Type;facts.LogonElevationType=original.ElevationType;facts.LogonIntegrityRid=original.Integrity;
   facts.Phase=7;IntPtr buffer=Marshal.AllocHGlobal(IntPtr.Size);
   try{Marshal.WriteIntPtr(buffer,IntPtr.Zero);int length;facts.LinkedTokenQuerySucceeded=GetTokenInformation(logon,19,buffer,IntPtr.Size,out length);facts.LinkedTokenWindowsError=facts.LinkedTokenQuerySucceeded?0:Marshal.GetLastWin32Error();facts.LinkedTokenReturnedBytes=length;if(facts.LinkedTokenQuerySucceeded&&length==IntPtr.Size)linked=Marshal.ReadIntPtr(buffer);}finally{Marshal.FreeHGlobal(buffer);}
   if(linked==IntPtr.Zero)return facts;Token partner=Read(linked);facts.Phase=8;
   Token medium=Medium(original)?original:Medium(partner)?partner:null;Token high=High(original)?original:High(partner)?partner:null;
   if(medium==null||high==null||medium.Sid!=ownedSid||high.Sid!=ownedSid||medium.Session!=high.Session||medium.Sid.EndsWith("-500",StringComparison.Ordinal))return facts;
   facts.HighTokenType=high.Type;facts.MediumTokenType=medium.Type;facts.GenuineSameUserSessionPairVerified=true;facts.FilteredMediumAdministratorDenyOnly=medium.AdminDenyOnly;
   facts.HighImpersonationLevel=high.ImpersonationLevel;facts.MediumImpersonationLevel=medium.ImpersonationLevel;
   facts.HighImpersonationWindowsError=high.ImpersonationWindowsError;facts.MediumImpersonationWindowsError=medium.ImpersonationWindowsError;
   facts.Phase=9;facts.DuplicateMediumAttempted=true;IntPtr source=Object.ReferenceEquals(medium,original)?logon:linked;
   facts.DuplicateMediumSucceeded=DuplicateTokenEx(source,11,IntPtr.Zero,2,1,out duplicate);facts.DuplicateMediumWindowsError=facts.DuplicateMediumSucceeded?0:Marshal.GetLastWin32Error();
   if(facts.DuplicateMediumSucceeded) {
    Token copy=Read(duplicate);facts.DuplicatedFilteredPrimaryVerified=copy.Type==1&&Medium(copy)&&copy.Sid==ownedSid&&copy.Session==medium.Session;
    if(facts.DuplicatedFilteredPrimaryVerified){facts.Status="feasible";facts.Phase=0;}
   }
   if(facts.DuplicatedFilteredPrimaryVerified){facts.Probe=new ProbeFacts();Provisioner(facts.Probe,duplicate,ownedSid,name,password);}return facts;
  }catch(NativeFailure failure){facts.NativeQueryWindowsError=failure.Code;facts.Status="unavailable";return facts;}
   catch(Exception failure){facts.FailureHResult=failure.HResult;facts.Status="unavailable";return facts;}
  finally {
   if(highDuplicate!=IntPtr.Zero)CloseHandle(highDuplicate);if(duplicate!=IntPtr.Zero)CloseHandle(duplicate);if(linked!=IntPtr.Zero)CloseHandle(linked);if(logon!=IntPtr.Zero)CloseHandle(logon);if(current!=IntPtr.Zero)CloseHandle(current);password=null;
   if(created && (facts.Probe==null || facts.Probe.Drained)) {
    try{facts.CleanupOwnershipVerified=ownedSid!=null&&AccountSid(name)==ownedSid;if(facts.CleanupOwnershipVerified){facts.AccountRemoveStatus=unchecked((int)NetUserDel(null,name));facts.AccountRemoved=facts.AccountRemoveStatus==0;}else facts.CleanupUncertain=true;}
    catch(NativeFailure failure){facts.AccountRemoveStatus=failure.Code;facts.CleanupUncertain=true;}
    catch(Exception failure){facts.FailureHResult=failure.HResult;facts.CleanupUncertain=true;}
    if(!facts.AccountRemoved){facts.CleanupUncertain=true;facts.Status="cleanup-unconfirmed";}
   }
   else if(created){facts.CleanupUncertain=true;facts.Status="cleanup-unconfirmed";}
  }
 }
}

internal static partial class AdministratorCiLaunchProbe {
 public sealed class ProbeFacts {
  public string Status {get;set;} public int Phase {get;set;} public int Step {get;set;} public int FailureStep {get;set;} public int WindowsError {get;set;} public int HResult {get;set;}
  public uint? SourceTokenGrantedAccess {get;set;} public int? SourceTokenObjectQueryStatus {get;set;} public uint? SourceTokenObjectQueryBytes {get;set;}
  public bool? CallerSeImpersonatePresent {get;set;} public bool? CallerSeImpersonateEnabled {get;set;} public int CallerPrivilegeQueryWindowsError {get;set;} public int CallerPrivilegeQueryHResult {get;set;}
  public string ImageAccessCheckScope {get;set;} public bool? ImageReadExecuteAccessCheckApiSucceeded {get;set;} public bool? ImageReadExecuteAllowed {get;set;} public uint? ImageReadExecuteGrantedAccess {get;set;} public int ImageAccessCheckWindowsError {get;set;} public int ImageAccessCheckHResult {get;set;}
  public bool ProtectedSourceConstructorCompleted {get;set;} public bool JobConstructorCompleted {get;set;} public bool CreateProcessWithTokenAttempted {get;set;} public int CreateProcessWithTokenWindowsError {get;set;}
  public bool CreateProcessWithLogonAttempted {get;set;} public bool CreateProcessWithLogonSucceeded {get;set;} public int CreateProcessWithLogonWindowsError {get;set;} public bool DesktopPermissionRepairAttempted {get;set;}
  public bool SponsorCreated {get;set;} public bool SponsorPinned {get;set;} public bool SponsorAssignedBeforeResume {get;set;}
  public bool RunasAuthorized {get;set;} public bool ShellSucceeded {get;set;} public int ShellWindowsError {get;set;}
  public bool HighCreated {get;set;} public bool HighPinned {get;set;} public bool HighJobAssigned {get;set;}
  public bool HighPrimary {get;set;} public bool SponsorMediumPrimary {get;set;} public int? LinkedMediumContextType {get;set;}
  public bool? HighOriginalHostedFieldsPresent {get;set;} public bool HighHostedChainGrantAuthenticated {get;set;} public int? HighRefusalPhase {get;set;} public bool? HighRefusalManagedException {get;set;} public int? HighRefusalWindowsError {get;set;} public uint? HighRefusalHResultLow20 {get;set;}
  public bool? HighLinkedContextFilteredMedium {get;set;} public bool? HighLinkedContextSameSid {get;set;} public bool? HighLinkedContextSameSession {get;set;} public uint? HighLinkedContextSession {get;set;} public uint? HighSourceMediumSession {get;set;} public uint? HighActualSession {get;set;}
  public bool SameSidSession {get;set;} public bool AuthenticationIdMatches {get;set;}
  public bool SourceMediumDuplicateSucceeded {get;set;} public int SourceMediumDuplicateError {get;set;}
  public bool SourceMediumDuplicateVerified {get;set;} public int? HighExit {get;set;} public int? SponsorExit {get;set;}
  public uint? InitialAcceptanceJobProcesses {get;set;} public uint? RemainingJobProcesses {get;set;} public bool Drained {get;set;} public bool UnknownHighLaunch {get;set;} public bool UnknownSponsorLaunch {get;set;}
  public bool ProtectedSourceCreated {get;set;} public bool ProtectedSourceRemoved {get;set;} public int ProtectedSourceCleanupHResult {get;set;}
  public string BoundaryRuntimePath {get;set;} public string BoundaryAppSha256 {get;set;}
 }
 [StructLayout(LayoutKind.Sequential)] struct StartInfo {internal int Size;internal IntPtr Reserved,Desktop,Title;internal int X,Y,Width,Height,XCount,YCount,Fill,Flags;internal short Show,ReservedBytes;internal IntPtr ReservedData,Input,Output,Error;}
 [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {internal IntPtr Process,Thread;internal uint Pid,Tid;}
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct ShellInfo {internal uint Size,Mask;internal IntPtr Window;internal string Verb,File,Parameters,Directory;internal int Show;internal IntPtr Instance,IdList;internal string Class;internal IntPtr ClassKey;internal uint HotKey;internal IntPtr Icon,Process;}
 sealed class Packet {internal int Kind;internal long[] Values;}
 static bool PossibleChild(ProcessInfo value){return value.Process!=IntPtr.Zero||value.Thread!=IntPtr.Zero||value.Pid!=0||value.Tid!=0;}
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(IntPtr handle,out long created,out long exited,out long kernel,out long user);
 [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
 [DllImport("kernel32.dll",SetLastError=true)] static extern uint GetProcessId(IntPtr handle);
 [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(IntPtr process,uint code);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageNameW(IntPtr process,uint flags,StringBuilder path,ref uint length);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObjectW(IntPtr security,string name);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int kind,IntPtr data,uint size);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool assigned);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,IntPtr data,uint size,IntPtr returned);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint code);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint pid);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessWithTokenW(IntPtr token,uint logon,string file,StringBuilder command,uint flags,IntPtr environment,string directory,ref StartInfo start,out ProcessInfo process);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessWithLogonW(string user,string domain,string password,uint logon,string file,StringBuilder command,uint flags,IntPtr environment,string directory,ref StartInfo start,out ProcessInfo process);
 [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ShellExecuteExW(ref ShellInfo info);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetSystemWindowsDirectoryW(StringBuilder path,uint size);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool DuplicateHandle(IntPtr sourceProcess,IntPtr source,IntPtr targetProcess,out IntPtr target,uint access,bool inherit,uint options);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandleW(IntPtr file,StringBuilder path,uint size,uint flags);
 [DllImport("ntdll.dll")] static extern int NtQueryObject(IntPtr handle,int kind,IntPtr data,uint size,out uint returned);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool LookupPrivilegeValueW(string system,string name,out long luid);
 [StructLayout(LayoutKind.Sequential)] struct GenericMapping {internal uint Read,Write,Execute,All;}
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool AccessCheck(IntPtr descriptor,IntPtr token,uint desired,ref GenericMapping mapping,IntPtr privileges,ref uint size,out uint granted,out bool allowed);
 static string selfPath,selfHash;static bool highRole;static int highPhase;
 static void Require(bool valid){if(!valid)throw new NativeFailure(13);}
 static int Last(){return Marshal.GetLastWin32Error();}
 static long Authentication(IntPtr token){IntPtr data=Marshal.AllocHGlobal(56);try{int size;if(!GetTokenInformation(token,10,data,56,out size))throw new NativeFailure(Last());Require(size==56);return Marshal.ReadInt64(data,8);}finally{Marshal.FreeHGlobal(data);}}
 static long Started(IntPtr process){long c,e,k,u;if(!GetProcessTimes(process,out c,out e,out k,out u))throw new NativeFailure(Last());return DateTime.FromFileTimeUtc(c).Ticks;}
 static uint OwnPid(){return unchecked((uint)Process.GetCurrentProcess().Id);}
 static string Hash(Stream stream){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
 static string Quote(string value){Require(value.IndexOfAny(new[]{'"','\0','\r','\n'})<0);return "\""+value+"\"";}
 static bool FreshNonce(string value){return value!=null&&value.Length==32&&value.Any(c=>c!='0')&&value.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f');}
 static bool HostedFields(){return Environment.GetEnvironmentVariable("GITHUB_ACTIONS")=="true"&&Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT")=="github-hosted"&&Environment.GetEnvironmentVariable("RUNNER_OS")=="Windows";}
 static bool OriginalHostedFieldsPresent(){return Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!=null&&Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT")!=null&&Environment.GetEnvironmentVariable("RUNNER_OS")!=null;}
 static void Hosted(){Require(Environment.Is64BitProcess&&HostedFields());}
 // Fixed HIGH-role refusal only: marker, bounded phase, native error or explicitly
 // partial managed HRESULT. This adds observability, never an admission path.
 static int Refusal(int error,bool managed){return 0x40000000|(managed?0x20000000:0)|((highPhase&255)<<20)|(error&0xfffff);}
 static void RecordHighExit(ProbeFacts facts,int code){facts.HighExit=code;if((code&unchecked((int)0xd0000000))!=0x40000000)return;facts.HighRefusalPhase=(code>>20)&255;bool managed=(code&0x20000000)!=0;facts.HighRefusalManagedException=managed;if(managed)facts.HighRefusalHResultLow20=unchecked((uint)code)&0xfffff;else facts.HighRefusalWindowsError=code&0xfffff;}
 static void Consent(){using(var m=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))using(var p=m.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",false))Require(Policy(p,"EnableLUA")==1&&Policy(p,"ConsentPromptBehaviorAdmin")==0);}
 sealed class Life : IDisposable {
  internal IntPtr Handle;internal uint Pid;internal long Start;
  internal static Life Open(uint pid,long expected,bool jobRights=false){IntPtr handle=OpenProcess(jobRights?0x101101u:0x101000u,false,pid);if(handle==IntPtr.Zero)throw new NativeFailure(Last());try{long start=Started(handle);Require(pid>0&&start>0&&(expected==0||expected==start));return new Life{Handle=handle,Pid=pid,Start=start};}catch{CloseHandle(handle);throw;}}
  internal bool Exited(int wait){uint result=WaitForSingleObject(Handle,(uint)wait);if(result==0)return true;if(result==258)return false;throw new NativeFailure(Last());}
  internal int Exit(){Require(Exited(0));uint code;if(!GetExitCodeProcess(Handle,out code))throw new NativeFailure(Last());return unchecked((int)code);}
  internal void Pin(string expectedPath){var path=new StringBuilder(32768);uint n=32768;if(!QueryFullProcessImageNameW(Handle,0,path,ref n))throw new NativeFailure(Last());Require(n>0&&n<32768&&(expectedPath==null||String.Equals(path.ToString(),expectedPath,StringComparison.OrdinalIgnoreCase)));using(var file=new FileStream(path.ToString(),FileMode.Open,FileAccess.Read,FileShare.Read))Require(Hash(file)==selfHash);}
  internal void PinTransferredImage(IntPtr image){var processPath=new StringBuilder(32768);uint size=32768;if(!QueryFullProcessImageNameW(Handle,1,processPath,ref size))throw new NativeFailure(Last());var filePath=new StringBuilder(32768);uint length=GetFinalPathNameByHandleW(image,filePath,32768,2);Require(size>0&&size<32768&&length>0&&length<32768&&String.Equals(processPath.ToString(),filePath.ToString(),StringComparison.OrdinalIgnoreCase));using(var safe=new Microsoft.Win32.SafeHandles.SafeFileHandle(image,false))using(var file=new FileStream(safe,FileAccess.Read)){file.Position=0;Require(Hash(file)==selfHash);}}
  public void Dispose(){if(Handle!=IntPtr.Zero){CloseHandle(Handle);Handle=IntPtr.Zero;}}
 }
 static Token ProcessToken(Life life,uint access,out IntPtr token){if(!OpenProcessToken(life.Handle,access,out token))throw new NativeFailure(Last());return Read(token);}
 static void ReadLaunchDiagnostics(ProbeFacts facts,IntPtr medium,string imagePath){
  facts.ImageAccessCheckScope="DACL-only staged PE read/execute; not MIC, ancestor traversal, desktop access or process-creation proof";
  uint capacity=56;IntPtr data=Marshal.AllocHGlobal((int)capacity);try{uint size;int status=NtQueryObject(medium,0,data,capacity,out size);if(status!=0&&size>=56&&size<=4096&&size!=capacity){Marshal.FreeHGlobal(data);data=IntPtr.Zero;capacity=size;data=Marshal.AllocHGlobal((int)capacity);status=NtQueryObject(medium,0,data,capacity,out size);}facts.SourceTokenObjectQueryStatus=status;facts.SourceTokenObjectQueryBytes=size;if(status==0&&size>=8&&size<=capacity)facts.SourceTokenGrantedAccess=unchecked((uint)Marshal.ReadInt32(data,4));}finally{if(data!=IntPtr.Zero)Marshal.FreeHGlobal(data);}
  IntPtr caller=IntPtr.Zero;try{if(!OpenProcessToken(GetCurrentProcess(),8,out caller))throw new NativeFailure(Last());long desired;if(!LookupPrivilegeValueW(null,"SeImpersonatePrivilege",out desired))throw new NativeFailure(Last());int length;data=Query(caller,3,out length);try{Require(length>=4);int count=Marshal.ReadInt32(data);Require(count>=0&&count<=4096&&4L+12L*count<=length);facts.CallerSeImpersonatePresent=false;facts.CallerSeImpersonateEnabled=false;for(int i=0;i<count;i++){int offset=4+12*i;if(Marshal.ReadInt64(data,offset)==desired){facts.CallerSeImpersonatePresent=true;facts.CallerSeImpersonateEnabled=(Marshal.ReadInt32(data,offset+8)&2)!=0;}}}finally{Marshal.FreeHGlobal(data);}}catch(NativeFailure failure){facts.CallerPrivilegeQueryWindowsError=failure.Code;}catch(Exception failure){facts.CallerPrivilegeQueryHResult=failure.HResult;}finally{if(caller!=IntPtr.Zero)CloseHandle(caller);}
  IntPtr inspection=IntPtr.Zero,security=IntPtr.Zero,privileges=IntPtr.Zero;try{if(!DuplicateTokenEx(medium,8,IntPtr.Zero,2,2,out inspection))throw new NativeFailure(Last());Token source=Read(medium),copy=Read(inspection);Require(copy.Type==2&&copy.ImpersonationLevel==2&&Medium(copy)&&copy.Sid==source.Sid&&copy.Session==source.Session&&copy.Authentication==source.Authentication);byte[] bytes=File.GetAccessControl(imagePath,AccessControlSections.Access|AccessControlSections.Owner|AccessControlSections.Group).GetSecurityDescriptorBinaryForm();Require(bytes.Length>0&&bytes.Length<=65536);security=Marshal.AllocHGlobal(bytes.Length);Marshal.Copy(bytes,0,security,bytes.Length);privileges=Marshal.AllocHGlobal(4096);uint size=4096,granted;bool allowed;var mapping=new GenericMapping{Read=0x120089,Write=0x120116,Execute=0x1200a0,All=0x1f01ff};bool success=AccessCheck(security,inspection,0x1200a9,ref mapping,privileges,ref size,out granted,out allowed);facts.ImageReadExecuteAccessCheckApiSucceeded=success;facts.ImageAccessCheckWindowsError=success?0:Last();if(success&&size<=4096){facts.ImageReadExecuteAllowed=allowed;facts.ImageReadExecuteGrantedAccess=granted;}}catch(NativeFailure failure){facts.ImageAccessCheckWindowsError=failure.Code;}catch(Exception failure){facts.ImageAccessCheckHResult=failure.HResult;}finally{if(privileges!=IntPtr.Zero)Marshal.FreeHGlobal(privileges);if(security!=IntPtr.Zero)Marshal.FreeHGlobal(security);if(inspection!=IntPtr.Zero)CloseHandle(inspection);}
 }
 // Only the exact provisioner's readonly process and pinned image are duplicated.
 // This grants no token, memory, mutation or child-creation rights to the LAB role.
 static long[] TransferParent(Life recipient){IntPtr target=OpenProcess(0x101040,false,recipient.Pid);if(target==IntPtr.Zero)throw new NativeFailure(Last());IntPtr image=IntPtr.Zero;try{Require(GetProcessId(target)==recipient.Pid&&Started(target)==recipient.Start);image=CreateFileW(selfPath,0x80000000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);if(image==IntPtr.Zero||image==new IntPtr(-1))throw new NativeFailure(Last());IntPtr parent,copy;if(!DuplicateHandle(GetCurrentProcess(),GetCurrentProcess(),target,out parent,0x101000,false,0))throw new NativeFailure(Last());if(!DuplicateHandle(GetCurrentProcess(),image,target,out copy,0,false,2))throw new NativeFailure(Last());Require(parent.ToInt64()>0&&copy.ToInt64()>0&&parent!=copy);return new[]{1L,parent.ToInt64(),copy.ToInt64()};}finally{if(image!=IntPtr.Zero&&image!=new IntPtr(-1))CloseHandle(image);CloseHandle(target);}}
 static Life ReceiveParent(Packet packet,int kind,uint expectedPid,long expectedStart){Expected(packet,kind,3);Require(packet.Values[0]==1&&packet.Values[1]>0&&packet.Values[2]>0&&packet.Values[1]!=packet.Values[2]);IntPtr process=new IntPtr(packet.Values[1]),image=new IntPtr(packet.Values[2]);Life parent=null;try{Require(GetProcessId(process)==expectedPid&&Started(process)==expectedStart);parent=new Life{Handle=process,Pid=expectedPid,Start=expectedStart};parent.PinTransferredImage(image);Require(!parent.Exited(0));return parent;}catch{if(parent!=null)parent.Dispose();else CloseHandle(process);throw;}finally{CloseHandle(image);}}
 sealed class Job : IDisposable {
  IntPtr handle;internal Job(ProbeFacts facts){facts.Step=30;handle=CreateJobObjectW(IntPtr.Zero,null);if(handle==IntPtr.Zero)throw new NativeFailure(Last());IntPtr data=Marshal.AllocHGlobal(144);try{for(int i=0;i<144;i++)Marshal.WriteByte(data,i,0);Marshal.WriteInt32(data,16,0x2000);facts.Step=31;if(!SetInformationJobObject(handle,9,data,144))throw new NativeFailure(Last());}catch{CloseHandle(handle);handle=IntPtr.Zero;throw;}finally{Marshal.FreeHGlobal(data);}}
  internal void Add(IntPtr process){bool assigned;if(!IsProcessInJob(process,handle,out assigned))throw new NativeFailure(Last());if(!assigned&&!AssignProcessToJobObject(handle,process))throw new NativeFailure(Last());if(!IsProcessInJob(process,handle,out assigned))throw new NativeFailure(Last());Require(assigned);}
  internal uint Active(){IntPtr data=Marshal.AllocHGlobal(48);try{if(!QueryInformationJobObject(handle,1,data,48,IntPtr.Zero))throw new NativeFailure(Last());return unchecked((uint)Marshal.ReadInt32(data,40));}finally{Marshal.FreeHGlobal(data);}}
  internal bool Drain(){if(Active()!=0&&!TerminateJobObject(handle,91))return false;var clock=Stopwatch.StartNew();while(Active()!=0&&clock.ElapsedMilliseconds<4000)Thread.Sleep(5);return Active()==0;}
  public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
 }
 sealed class ConnectionWait {Exception error;internal volatile bool Complete;internal ConnectionWait(NamedPipeServerStream pipe){pipe.BeginWaitForConnection(delegate(IAsyncResult result){try{pipe.EndWaitForConnection(result);}catch(Exception failure){error=failure;}finally{try{result.AsyncWaitHandle.Close();}catch(Exception failure){if(error==null)error=failure;}Complete=true;}},null);}internal void End(){Require(Complete);if(error!=null)throw error;}}
 sealed class ReadWait {Exception error;int count;internal volatile bool Complete;internal ReadWait(PipeStream pipe,byte[] bytes,int offset,int length){pipe.BeginRead(bytes,offset,length,delegate(IAsyncResult result){try{count=pipe.EndRead(result);}catch(Exception failure){error=failure;}finally{try{result.AsyncWaitHandle.Close();}catch(Exception failure){if(error==null)error=failure;}Complete=true;}},null);}internal int End(){Require(Complete);if(error!=null)throw error;return count;}}
 static bool Wait(Func<bool> complete,int milliseconds){var clock=Stopwatch.StartNew();while(!complete()){if(clock.ElapsedMilliseconds>=milliseconds)return complete();Thread.Sleep(2);}return true;}
 static NamedPipeServerStream Server(string name,string sid){var acl=new PipeSecurity();acl.SetAccessRuleProtection(true,false);foreach(string trustee in new[]{sid,"S-1-5-32-544","S-1-5-18"})acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(trustee),PipeAccessRights.FullControl,AccessControlType.Allow));return new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,512,512,acl);}
 static void Connect(NamedPipeServerStream pipe,uint expected){var wait=new ConnectionWait(pipe);if(!Wait(delegate{return wait.Complete;},10000)){pipe.Dispose();throw new NativeFailure(1460);}wait.End();uint pid;Require(GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out pid)&&pid==expected);}
 static void Write(PipeStream pipe,int kind,params long[] values){Require(values.Length<=15);using(var m=new MemoryStream())using(var w=new BinaryWriter(m)){w.Write(8+values.Length*8);w.Write(kind);w.Write(1);foreach(long v in values)w.Write(v);byte[] bytes=m.ToArray();pipe.Write(bytes,0,bytes.Length);pipe.Flush();}}
 static byte[] Exact(PipeStream pipe,int size,int milliseconds){Require(milliseconds>0&&milliseconds<=180000);byte[] bytes=new byte[size];int at=0;var clock=Stopwatch.StartNew();while(at<size){var read=new ReadWait(pipe,bytes,at,size-at);if(!Wait(delegate{return read.Complete;},Math.Max(1,milliseconds-(int)clock.ElapsedMilliseconds))){pipe.Dispose();throw new NativeFailure(1460);}int n=read.End();Require(n>0);at+=n;}return bytes;}
 static Packet Receive(PipeStream pipe,int milliseconds=10000){int size=BitConverter.ToInt32(Exact(pipe,4,milliseconds),0);Require(size>=8&&size<=128&&(size-8)%8==0);byte[] data=Exact(pipe,size,milliseconds);Require(BitConverter.ToInt32(data,4)==1);var values=new long[(size-8)/8];for(int i=0;i<values.Length;i++)values[i]=BitConverter.ToInt64(data,8+i*8);return new Packet{Kind=BitConverter.ToInt32(data,0),Values=values};}
 static void Expected(Packet packet,int kind,int count){Require(packet.Kind==kind&&packet.Values.Length==count);}
 static IntPtr EnvironmentBlock(){var values=new System.Collections.Generic.SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(string key in new[]{"SystemRoot","WINDIR","SystemDrive","PATH","GITHUB_ACTIONS","RUNNER_ENVIRONMENT","RUNNER_OS","GITHUB_SHA","GITHUB_REF","GITHUB_EVENT_NAME","GITHUB_RUN_ID"}){string value=Environment.GetEnvironmentVariable(key);if(value!=null)values.Add(key,value);}string text=String.Join("\0",values.Select(p=>p.Key+"="+p.Value))+"\0\0";return Marshal.StringToHGlobalUni(text);}
 sealed class ProtectedSource {
  internal string DirectoryPath,ImagePath;string configPath;IntPtr directory;FileStream image,config;BoundaryAsset boundaryAsset;
  internal ProtectedSource(string sid,ProbeFacts facts){
   var windows=new StringBuilder(32768);uint count=GetSystemWindowsDirectoryW(windows,32768);Require(count>0&&count<32768);
   string parent=Path.Combine(windows.ToString(),"Temp");Require(Directory.Exists(parent));for(string current=parent;current!=null;current=Path.GetDirectoryName(current.TrimEnd('\\')))Require((File.GetAttributes(current)&FileAttributes.ReparsePoint)==0);
   DirectoryPath=Path.Combine(parent,"TaskbarSelectCiSource."+Guid.NewGuid().ToString("N"));ImagePath=Path.Combine(DirectoryPath,"AdministratorCiLaunchProbe.exe");configPath=ImagePath+".config";Require(!Directory.Exists(DirectoryPath)&&!File.Exists(DirectoryPath));
   var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
   foreach(string trustee in new[]{"S-1-5-18","S-1-5-32-544",sid})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(trustee),trustee==sid?FileSystemRights.ReadAndExecute:FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   Directory.CreateDirectory(DirectoryPath,acl);facts.ProtectedSourceCreated=true;
   try {
    var actual=Directory.GetAccessControl(DirectoryPath);Require(actual.AreAccessRulesProtected&&((SecurityIdentifier)actual.GetOwner(typeof(SecurityIdentifier))).Value=="S-1-5-32-544");var rules=actual.GetAccessRules(true,true,typeof(SecurityIdentifier));Require(rules.Count==3);
    foreach(FileSystemAccessRule rule in rules){string who=((SecurityIdentifier)rule.IdentityReference).Value;Require((who==sid||who=="S-1-5-18"||who=="S-1-5-32-544")&&!rule.IsInherited&&rule.AccessControlType==AccessControlType.Allow&&rule.InheritanceFlags==(InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit)&&rule.PropagationFlags==PropagationFlags.None);var expected=new FileSystemAccessRule(new SecurityIdentifier(who),who==sid?FileSystemRights.ReadAndExecute:FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow);Require(rule.FileSystemRights==expected.FileSystemRights);}
    directory=CreateFileW(DirectoryPath,0x80000000,1,IntPtr.Zero,3,0x02200000,IntPtr.Zero);if(directory==new IntPtr(-1)||directory==IntPtr.Zero){directory=IntPtr.Zero;throw new NativeFailure(Last());}
    using(var input=new FileStream(selfPath,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=new FileStream(ImagePath,FileMode.CreateNew,FileAccess.Write,FileShare.Read))input.CopyTo(output);
    byte[] bytes=new UTF8Encoding(false).GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.8\"/></startup></configuration>\n");using(var output=new FileStream(configPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read))output.Write(bytes,0,bytes.Length);
    image=new FileStream(ImagePath,FileMode.Open,FileAccess.Read,FileShare.Read);config=new FileStream(configPath,FileMode.Open,FileAccess.Read,FileShare.Read);Require(Hash(image)==selfHash);
    if(boundaryMode)boundaryAsset=new BoundaryAsset(sid,DirectoryPath,facts);
   }catch{Close(false);throw;}
  }
  internal bool Close(bool remove){if(boundaryAsset!=null)boundaryAsset.Dispose();if(config!=null){config.Dispose();config=null;}if(image!=null){image.Dispose();image=null;}if(directory!=IntPtr.Zero){CloseHandle(directory);directory=IntPtr.Zero;}if(!remove)return false;if(boundaryAsset!=null&&boundaryRemoveExtension!=null)boundaryRemoveExtension(DirectoryPath);if(boundaryAsset!=null)File.Delete(boundaryAsset.AppPath);File.Delete(configPath);File.Delete(ImagePath);Directory.Delete(DirectoryPath,false);return !Directory.Exists(DirectoryPath);}
 }
 static void Provisioner(ProbeFacts facts,IntPtr medium,string ownedSid,string accountName,string accountPassword){
  facts.Status="unavailable";Life sponsor=null,high=null;ProcessInfo created=new ProcessInfo();bool mayHigh=false,sponsorRan=false,rawExited=true,uncertainStartup=false;Job job=null;ProtectedSource stage=null;
  try {
   facts.Phase=1;facts.Step=10;stage=new ProtectedSource(ownedSid,facts);facts.ProtectedSourceConstructorCompleted=true;facts.Step=30;job=new Job(facts);facts.JobConstructorCompleted=true;facts.Step=40;ReadLaunchDiagnostics(facts,medium,stage.ImagePath);facts.Step=50;string nonce=Guid.NewGuid().ToString("N");uint root=OwnPid();long rootStart=Started(GetCurrentProcess());
   var sponsorArguments=new System.Collections.Generic.List<string>{stage.ImagePath,boundaryMode?"--sponsor-boundary":"--sponsor",nonce,root.ToString(CultureInfo.InvariantCulture),rootStart.ToString(CultureInfo.InvariantCulture),selfHash};if(boundaryMode)sponsorArguments.AddRange(new[]{boundaryApp,boundaryRuntime,boundarySourceSha});string command=String.Join(" ",sponsorArguments.Select(Quote));
   var startup=new StartInfo{Size=Marshal.SizeOf(typeof(StartInfo))};facts.Step=51;IntPtr environment=EnvironmentBlock();
   try{
    facts.Step=52;facts.CreateProcessWithTokenAttempted=true;
    if(!CreateProcessWithTokenW(medium,1,stage.ImagePath,new StringBuilder(command),0x08000404,environment,stage.DirectoryPath,ref startup,out created)){
     facts.CreateProcessWithTokenWindowsError=Last();
     uncertainStartup=PossibleChild(created);
     // This is a separate measured credential launch, never a permission repair.
     // Do not launch again if the first API returned any possible child identity.
     if(facts.CreateProcessWithTokenWindowsError!=5||uncertainStartup)throw new NativeFailure(facts.CreateProcessWithTokenWindowsError);
     facts.Step=53;Require(accountName!=null&&accountPassword!=null&&AccountSid(accountName)==ownedSid&&command.Length<=1024);
     startup=new StartInfo{Size=Marshal.SizeOf(typeof(StartInfo))};facts.Step=54;facts.CreateProcessWithLogonAttempted=true;
     facts.CreateProcessWithLogonSucceeded=CreateProcessWithLogonW(accountName,Environment.MachineName,accountPassword,1,stage.ImagePath,new StringBuilder(command),0x08000404,environment,stage.DirectoryPath,ref startup,out created);
     if(!facts.CreateProcessWithLogonSucceeded){facts.CreateProcessWithLogonWindowsError=Last();uncertainStartup=PossibleChild(created);throw new NativeFailure(facts.CreateProcessWithLogonWindowsError);}
    }
   }finally{Marshal.FreeHGlobal(environment);}
   facts.SponsorCreated=true;facts.Step=60;sponsor=Life.Open(created.Pid,Started(created.Process));facts.Step=61;sponsor.Pin(stage.ImagePath);facts.SponsorPinned=true;
   IntPtr token=IntPtr.Zero;try{Token actual=ProcessToken(sponsor,8,out token);Require(actual.Type==1&&Medium(actual)&&actual.Sid==ownedSid);}finally{if(token!=IntPtr.Zero)CloseHandle(token);}
   job.Add(created.Process);facts.SponsorAssignedBeforeResume=true;if(ResumeThread(created.Thread)==UInt32.MaxValue)throw new NativeFailure(Last());sponsorRan=true;CloseHandle(created.Thread);created.Thread=IntPtr.Zero;CloseHandle(created.Process);created.Process=IntPtr.Zero;
   using(var control=new NamedPipeClientStream(".","TaskbarSelect.CiSponsor."+nonce,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Anonymous)){
    facts.Phase=2;control.Connect(10000);uint peer;Require(GetNamedPipeServerProcessId(control.SafePipeHandle.DangerousGetHandle(),out peer)&&peer==sponsor.Pid);Packet hello=Receive(control);Expected(hello,70,2);Require(hello.Values[0]==sponsor.Pid&&hello.Values[1]==sponsor.Start);
    facts.Phase=3;long[] sponsorParent=TransferParent(sponsor);mayHigh=true;facts.RunasAuthorized=true;Write(control,71,sponsorParent);Packet launched=Receive(control);Expected(launched,72,4);facts.ShellSucceeded=launched.Values[2]==1;facts.ShellWindowsError=unchecked((int)launched.Values[3]);
    Require(launched.Values[0]>0&&launched.Values[0]<=UInt32.MaxValue&&launched.Values[1]>0);facts.HighCreated=true;
    high=Life.Open((uint)launched.Values[0],launched.Values[1],true);high.Pin(stage.ImagePath);facts.HighPinned=true;job.Add(high.Handle);facts.HighJobAssigned=true;
    Require(facts.ShellSucceeded);token=IntPtr.Zero;try{Token actual=ProcessToken(high,8,out token);Require(actual.Type==1&&High(actual)&&actual.Sid==ownedSid);}finally{if(token!=IntPtr.Zero)CloseHandle(token);}
    facts.Phase=4;Write(control,73,TransferParent(high));Packet proof=Receive(control,boundaryMode?180000:10000);Expected(proof,76,15);
    facts.HighPrimary=proof.Values[0]==1;facts.SponsorMediumPrimary=proof.Values[1]==1;facts.LinkedMediumContextType=(int)proof.Values[2];facts.SameSidSession=proof.Values[3]==1;facts.AuthenticationIdMatches=proof.Values[4]==1;facts.SourceMediumDuplicateSucceeded=proof.Values[5]==1;facts.SourceMediumDuplicateError=(int)proof.Values[6];facts.SourceMediumDuplicateVerified=proof.Values[7]==1;
    Require((proof.Values[8]==0||proof.Values[8]==1)&&proof.Values[9]==1);facts.HighOriginalHostedFieldsPresent=proof.Values[8]==1;facts.HighHostedChainGrantAuthenticated=true;
    Require((proof.Values[10]==0||proof.Values[10]==1)&&(proof.Values[11]==0||proof.Values[11]==1));for(int i=12;i<15;i++)Require(proof.Values[i]>=0&&proof.Values[i]<=UInt32.MaxValue);
    facts.HighLinkedContextFilteredMedium=proof.Values[10]==1;facts.HighLinkedContextSameSid=proof.Values[11]==1;facts.HighLinkedContextSession=(uint)proof.Values[12];facts.HighSourceMediumSession=(uint)proof.Values[13];facts.HighActualSession=(uint)proof.Values[14];facts.HighLinkedContextSameSession=facts.HighLinkedContextSession==facts.HighSourceMediumSession;
    Require(high.Exited(5000));RecordHighExit(facts,high.Exit());Write(control,77,1);Require(sponsor.Exited(5000));facts.SponsorExit=sponsor.Exit();
    Require(facts.HighPrimary&&facts.SponsorMediumPrimary&&(facts.LinkedMediumContextType==1||facts.LinkedMediumContextType==2)&&facts.HighLinkedContextFilteredMedium==true&&facts.HighLinkedContextSameSid==true&&facts.HighSourceMediumSession==facts.HighActualSession&&facts.SameSidSession&&facts.AuthenticationIdMatches&&facts.SourceMediumDuplicateSucceeded&&facts.SourceMediumDuplicateVerified&&facts.HighExit==0&&facts.SponsorExit==0);
    facts.Step=62;facts.InitialAcceptanceJobProcesses=job.Active();facts.Drained=Wait(delegate{return job.Active()==0;},1000);facts.RemainingJobProcesses=job.Active();Require(facts.Drained&&facts.RemainingJobProcesses==0);mayHigh=false;facts.Status="passed";facts.Phase=0;
   }
  }catch(NativeFailure failure){facts.FailureStep=facts.Step;facts.WindowsError=failure.Code;}catch(Win32Exception failure){facts.FailureStep=facts.Step;facts.WindowsError=failure.NativeErrorCode;}catch(Exception failure){facts.FailureStep=facts.Step;facts.HResult=failure.HResult;}
  finally {
   if(created.Thread!=IntPtr.Zero)CloseHandle(created.Thread);
   if(created.Process!=IntPtr.Zero){try{rawExited=false;if(!sponsorRan&&!TerminateProcess(created.Process,91))facts.WindowsError=Last();rawExited=WaitForSingleObject(created.Process,4000)==0;if(rawExited)uncertainStartup=false;}finally{CloseHandle(created.Process);}}
   if(job!=null){try{bool jobZero=job.Drain();facts.RemainingJobProcesses=job.Active();bool sponsorExited=sponsor==null||sponsor.Exited(1000),highExited=high==null||high.Exited(1000);if(sponsor!=null&&sponsorExited){facts.SponsorExit=sponsor.Exit();if(boundaryMode&&facts.SponsorExit==113)uncertainStartup=true;}if(high!=null&&highExited)RecordHighExit(facts,high.Exit());if(high!=null&&facts.HighPinned&&facts.HighJobAssigned&&highExited&&jobZero)mayHigh=false;if(!facts.Drained)facts.Drained=jobZero&&sponsorExited&&highExited&&rawExited&&!mayHigh&&!uncertainStartup;}catch(NativeFailure failure){facts.WindowsError=failure.Code;facts.Drained=false;}catch(Exception failure){facts.HResult=failure.HResult;facts.Drained=false;}finally{job.Dispose();}}
   facts.UnknownHighLaunch=mayHigh;facts.UnknownSponsorLaunch=uncertainStartup;if(high!=null)high.Dispose();if(sponsor!=null)sponsor.Dispose();
   if(!facts.SponsorCreated&&created.Process==IntPtr.Zero&&!mayHigh&&!uncertainStartup)facts.Drained=true;
   if(stage!=null){try{facts.ProtectedSourceRemoved=stage.Close(facts.Drained);}catch(Exception failure){facts.ProtectedSourceCleanupHResult=failure.HResult;}}
  }
 }
 static int Sponsor(string[] args){
  string nonce=args[1];uint root=UInt32.Parse(args[2],CultureInfo.InvariantCulture);long start=Int64.Parse(args[3],CultureInfo.InvariantCulture);Require(FreshNonce(nonce)&&args[4]==selfHash);
  IntPtr current=IntPtr.Zero;try{if(!OpenProcessToken(GetCurrentProcess(),8,out current))throw new NativeFailure(Last());Token own=Read(current);Require(own.Type==1&&Medium(own)&&!own.Sid.EndsWith("-500",StringComparison.Ordinal));
   using(var control=Server("TaskbarSelect.CiSponsor."+nonce,own.Sid)){
    Connect(control,root);Write(control,70,OwnPid(),Started(GetCurrentProcess()));using(var provisioner=ReceiveParent(Receive(control),71,root,start)){
    Consent();if(boundaryMode)PrepareBoundaryRuntime(own.Sid);
    using(var proof=Server("TaskbarSelect.CiHighProof."+nonce,own.Sid)){
     var highArguments=new System.Collections.Generic.List<string>{boundaryMode?"--high-boundary":"--high-proof",nonce,OwnPid().ToString(CultureInfo.InvariantCulture),Started(GetCurrentProcess()).ToString(CultureInfo.InvariantCulture),root.ToString(CultureInfo.InvariantCulture),start.ToString(CultureInfo.InvariantCulture),selfHash};if(boundaryMode)highArguments.AddRange(new[]{boundaryApp,boundaryRuntime,boundarySourceSha});string parameters=String.Join(" ",highArguments.Select(Quote));
     var launch=new ShellInfo{Size=(uint)Marshal.SizeOf(typeof(ShellInfo)),Mask=0x540,Verb="runas",File=selfPath,Parameters=parameters,Directory=Path.GetDirectoryName(selfPath),Show=0};
     bool success=ShellExecuteExW(ref launch);int error=success?0:Last();
     if(launch.Process==IntPtr.Zero){Write(control,72,0,0,success?1:0,error);return 2;}
     try{uint pid=GetProcessId(launch.Process);long highStart=Started(launch.Process);Write(control,72,pid,highStart,success?1:0,error);Packet grant=Receive(control);Expected(grant,73,3);Require(grant.Values[0]==1&&grant.Values[1]>0&&grant.Values[2]>0&&grant.Values[1]!=grant.Values[2]);Connect(proof,pid);Write(proof,74,grant.Values);Packet accepted=boundaryMode?ReceiveBoundaryProof(proof,own,pid,highStart):Receive(proof);Expected(accepted,75,15);Require(WaitForSingleObject(launch.Process,5000)==0);uint exit;if(!GetExitCodeProcess(launch.Process,out exit))throw new NativeFailure(Last());Write(control,76,accepted.Values);Packet acknowledged=Receive(control);Expected(acknowledged,77,1);Require(acknowledged.Values[0]==1&&exit==0);return 0;}finally{CloseHandle(launch.Process);}
    }
    }
   }
  }finally{if(current!=IntPtr.Zero)CloseHandle(current);}
 }
 static int HighProof(string[] args){
  highPhase=2;string nonce=args[1];uint sponsorPid=UInt32.Parse(args[2],CultureInfo.InvariantCulture);long sponsorStart=Int64.Parse(args[3],CultureInfo.InvariantCulture);uint root=UInt32.Parse(args[4],CultureInfo.InvariantCulture);long rootStart=Int64.Parse(args[5],CultureInfo.InvariantCulture);Require(FreshNonce(nonce)&&args[6]==selfHash);highPhase=3;Consent();bool originalFields=OriginalHostedFieldsPresent();
  IntPtr current=IntPtr.Zero,source=IntPtr.Zero,linked=IntPtr.Zero,copy=IntPtr.Zero;
  try{highPhase=4;if(!OpenProcessToken(GetCurrentProcess(),8,out current))throw new NativeFailure(Last());Token high=Read(current);highPhase=5;Require(high.Type==1&&High(high)&&!high.Sid.EndsWith("-500",StringComparison.Ordinal));
   highPhase=6;
   using(var sponsor=Life.Open(sponsorPid,sponsorStart))using(var pipe=new NamedPipeClientStream(".","TaskbarSelect.CiHighProof."+nonce,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Anonymous)){
    sponsor.Pin(selfPath);highPhase=7;pipe.Connect(10000);uint server;Require(GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out server)&&server==sponsorPid);highPhase=8;
    using(var provisioner=ReceiveParent(Receive(pipe),74,root,rootStart)){
    highPhase=9;IntPtr parentToken=IntPtr.Zero;try{Token parent=ProcessToken(provisioner,8,out parentToken);Require(parent.Type==1&&parent.Integrity==12288&&parent.Elevated&&parent.Sid.EndsWith("-500",StringComparison.Ordinal));}finally{if(parentToken!=IntPtr.Zero)CloseHandle(parentToken);}
    highPhase=10;Token medium=ProcessToken(sponsor,11,out source);highPhase=11;Require(ActualPair(high,medium));
    highPhase=12;IntPtr data=Marshal.AllocHGlobal(IntPtr.Size);try{int size;if(!GetTokenInformation(current,19,data,IntPtr.Size,out size))throw new NativeFailure(Last());Require(size==IntPtr.Size);linked=Marshal.ReadIntPtr(data);}finally{Marshal.FreeHGlobal(data);}Require(linked!=IntPtr.Zero);Token context=Read(linked);int refusal=LinkedRefusal(context,medium);
    if(refusal!=0){highPhase=refusal;Write(pipe,75,1,1,context.Type,1,context.Authentication==medium.Authentication?1:0,0,13,0,originalFields?1:0,1,Medium(context)?1:0,context.Sid==medium.Sid?1:0,context.Session,medium.Session,high.Session);return Refusal(13,false);}
    highPhase=18;bool duplicate=DuplicateTokenEx(source,11,IntPtr.Zero,2,1,out copy);int error=duplicate?0:Last();bool verified=false;if(duplicate){highPhase=19;Token actual=Read(copy);verified=CloneMatches(actual,medium);}
    int boundaryResult=0;if(boundaryMode&&duplicate&&verified){highPhase=21;boundaryMedium=medium;Require(boundaryRunner!=null);ObserveReverseLinkedHigh(source,medium,high);boundaryResult=boundaryRunner(pipe,boundaryApp,boundaryRuntime,sponsorPid,sponsorStart,boundarySourceSha);}
    highPhase=20;Require(!sponsor.Exited(0)&&!provisioner.Exited(0));Write(pipe,75,1,1,context.Type,1,1,duplicate?1:0,error,verified?1:0,originalFields?1:0,1,1,1,context.Session,medium.Session,high.Session);if(!duplicate){highPhase=18;return Refusal(error,false);}if(!verified){highPhase=19;return Refusal(13,false);}if(boundaryResult!=0){highPhase=22;return Refusal(boundaryResult,false);}return 0;
    }
   }
  }finally{if(copy!=IntPtr.Zero)CloseHandle(copy);if(linked!=IntPtr.Zero)CloseHandle(linked);if(source!=IntPtr.Zero)CloseHandle(source);if(current!=IntPtr.Zero)CloseHandle(current);}
 }
 [STAThread] static int Main(string[] args){
  highRole=args.Length==7&&args[0]=="--high-proof";highPhase=1;
  try{if(highRole)Require(Environment.Is64BitProcess);else Hosted();selfPath=Assembly.GetExecutingAssembly().Location;using(var image=new FileStream(selfPath,FileMode.Open,FileAccess.Read,FileShare.Read)){
   selfHash=Hash(image);
   if(args.Length==5&&args[0]=="--sponsor")return Sponsor(args);
   if(args.Length==7&&args[0]=="--high-proof")return HighProof(args);
   Require(args.Length==1&&args[0]=="--probe");Facts facts=Run();
   Console.WriteLine(new JavaScriptSerializer().Serialize(new{schema="taskbar-select-ci-launch-probe-v1",diagnosticOnly=true,boundaryValidationExecuted=false,tenCaseFixtureExecuted=false,sourceSha=Environment.GetEnvironmentVariable("GITHUB_SHA"),facts=facts,policyChanged=false,existingAccountChanged=false,notes=new[]{"Genuine Windows MED sponsor and consent0 runas HIGH launch only; no app or fixture windows/processes.","Actual HIGH and sponsor MED primary process SID/session must match; source-MED primary clone preserves SID/session/AuthenticationId.","Raw linked SessionId is reported metadata; readonly linked SID, filtered role and AuthenticationId must match the authentic sponsor.","Unknown HIGH launch or drain retains the account; no successful cleanup is claimed.","No raw SID, account name/password, authentication LUID or environment dump is exported."}}));
   return facts.Probe!=null&&facts.Probe.Status=="passed"&&facts.Probe.Drained&&facts.Probe.ProtectedSourceRemoved&&facts.AccountRemoved&&!facts.CleanupUncertain?0:3;
  }}catch(NativeFailure failure){Console.Error.WriteLine("Probe native failure="+failure.Code.ToString(CultureInfo.InvariantCulture));return highRole?Refusal(failure.Code,false):failure.Code==0?2:failure.Code;}catch(Exception failure){Console.Error.WriteLine("Probe failed hr=0x"+failure.HResult.ToString("X8",CultureInfo.InvariantCulture));return highRole?Refusal(failure.HResult,true):2;}
 }
}
