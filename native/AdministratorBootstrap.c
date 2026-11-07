/* No CLR or CRT is loaded before the protected payload and environment exist.
   The only statically imported DLL is kernel32. UAC is requested by the medium
   approval host; this process never adjusts privileges, installs, or changes Startup.
   Protocol and cache names are fixed, and no caller-supplied file bytes or paths
   cross this boundary. HIGH exit 0 means an empty descendant job; MED exit 0
   means its exact retained HIGH image reported that drain or a pre-child refusal. */
#define _WIN32_WINNT 0x0602
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winternl.h>
#include <aclapi.h>
#include <sddl.h>
#include <bcrypt.h>
#include <shellapi.h>
#include "AdministratorPayload.generated.h"

#define CAP 4096
#define MAX_ANCESTORS 40
#define PACKET_MAX 128
#define HANDSHAKE_MS 5000
#define DRAIN_MS 2500
#define DOTNET_EPOCH 504911232000000000ULL
#define DOTNET_MAX 3155378975999999999ULL
#define DIAGNOSTIC_BYTES 160
#define DIAGNOSTIC_IO_MS 100

/* Fixed numeric failure evidence only. This is never a drain acknowledgement,
   grant, persisted approval, or an instruction to remove a retained lease. */
enum { STOP_SETUP=1, STOP_PIPE_IDENTITY=2, STOP_PIPE_PEEK=3,
       STOP_PIPE_INPUT=4, STOP_PAYLOAD_EXIT=5, STOP_PAYLOAD_WAIT=6, STOP_IO_CANCEL=7 };
enum { DRAIN_INITIAL=1, DRAIN_TERMINATE_JOB=2, DRAIN_TERMINATE_PAYLOAD=3,
       DRAIN_QUERY_JOB=4, DRAIN_WAIT=5, DRAIN_CONFIRMED=6,
       DRAIN_TIMEOUT=7, DRAIN_IO_CANCEL=8 };
typedef struct NativeStopRecord {
    DWORD reason,phase,error,active,payloadWait,payloadExit,ioWait,ioCancelError,result,flags;
    DWORD payloadBeforeDrain,ioCancelWait,errorPhase,stopCode;
    ULONGLONG drainElapsed,runElapsed;
} NativeStopRecord;

enum { E_ARGS=100, E_LIBRARY=101, E_IDENTITY=102, E_PIPE=103,
       E_GRANT=104, E_CACHE=105, E_PAYLOAD=106, E_JOB=107,
       E_RESUME=108, E_PROTOCOL=109, E_SELFTEST=110, E_UAC_CANCELLED=111,
       E_APPROVAL_REFUSED=112, E_NOT_DRAINED=90 };

/* Explicit declarations prevent an accidentally linked CRT implementation. */
void *memcpy(void *d, const void *s, SIZE_T n) { BYTE *a=d; const BYTE *b=s; while(n--) *a++=*b++; return d; }
void *memset(void *d, int v, SIZE_T n) { BYTE *a=d; while(n--) *a++=(BYTE)v; return d; }
int memcmp(const void *a, const void *b, SIZE_T n) { const BYTE *x=a,*y=b; while(n--) { if(*x!=*y) return *x<*y?-1:1; ++x; ++y; } return 0; }

typedef BOOL (WINAPI *OpenTokenFn)(HANDLE,DWORD,PHANDLE);
typedef BOOL (WINAPI *TokenInfoFn)(HANDLE,TOKEN_INFORMATION_CLASS,LPVOID,DWORD,PDWORD);
typedef BOOL (WINAPI *EqualSidFn)(PSID,PSID);
typedef BOOL (WINAPI *ValidSidFn)(PSID);
typedef DWORD (WINAPI *SidLengthFn)(PSID);
typedef BOOL (WINAPI *WellKnownFn)(WELL_KNOWN_SID_TYPE,PSID,PSID,DWORD*);
typedef BOOL (WINAPI *DuplicateFn)(HANDLE,SECURITY_IMPERSONATION_LEVEL,PHANDLE);
typedef BOOL (WINAPI *AccessFn)(PSECURITY_DESCRIPTOR,HANDLE,DWORD,PGENERIC_MAPPING,PPRIVILEGE_SET,LPDWORD,LPDWORD,LPBOOL);
typedef DWORD (WINAPI *SecurityFn)(HANDLE,SE_OBJECT_TYPE,SECURITY_INFORMATION,PSID*,PSID*,PACL*,PACL*,PSECURITY_DESCRIPTOR*);
typedef BOOL (WINAPI *ControlFn)(PSECURITY_DESCRIPTOR,PSECURITY_DESCRIPTOR_CONTROL,LPDWORD);
typedef BOOL (WINAPI *DescriptorOwnerFn)(PSECURITY_DESCRIPTOR,PSID*,LPBOOL);
typedef BOOL (WINAPI *DescriptorDaclFn)(PSECURITY_DESCRIPTOR,LPBOOL,PACL*,LPBOOL);
typedef BOOL (WINAPI *AclInfoFn)(PACL,LPVOID,DWORD,ACL_INFORMATION_CLASS);
typedef BOOL (WINAPI *AceFn)(PACL,DWORD,LPVOID*);
typedef BOOL (WINAPI *SddlFn)(LPCWSTR,DWORD,PSECURITY_DESCRIPTOR*,PULONG);
typedef HRESULT (WINAPI *FolderFn)(HWND,int,HANDLE,DWORD,LPWSTR);
typedef NTSTATUS (WINAPI *OpenAlgorithmFn)(BCRYPT_ALG_HANDLE*,LPCWSTR,LPCWSTR,ULONG);
typedef NTSTATUS (WINAPI *GetPropertyFn)(BCRYPT_HANDLE,LPCWSTR,PUCHAR,ULONG,ULONG*,ULONG);
typedef NTSTATUS (WINAPI *CreateHashFn)(BCRYPT_ALG_HANDLE,BCRYPT_HASH_HANDLE*,PUCHAR,ULONG,PUCHAR,ULONG,ULONG);
typedef NTSTATUS (WINAPI *HashDataFn)(BCRYPT_HASH_HANDLE,PUCHAR,ULONG,ULONG);
typedef NTSTATUS (WINAPI *FinishHashFn)(BCRYPT_HASH_HANDLE,PUCHAR,ULONG,ULONG);
typedef NTSTATUS (WINAPI *DestroyHashFn)(BCRYPT_HASH_HANDLE);
typedef NTSTATUS (WINAPI *CloseAlgorithmFn)(BCRYPT_ALG_HANDLE,ULONG);
typedef NTSTATUS (NTAPI *CreateRelativeFn)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,PIO_STATUS_BLOCK,PLARGE_INTEGER,ULONG,ULONG,ULONG,ULONG,PVOID,ULONG);
typedef NTSTATUS (NTAPI *CreateObjectDirectoryFn)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES);
typedef NTSTATUS (NTAPI *OpenObjectFn)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES);
typedef NTSTATUS (NTAPI *CreateLinkFn)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,PUNICODE_STRING);
typedef NTSTATUS (NTAPI *QueryLinkFn)(HANDLE,PUNICODE_STRING,PULONG);
typedef NTSTATUS (NTAPI *SetObjectSecurityFn)(HANDLE,SECURITY_INFORMATION,PSECURITY_DESCRIPTOR);
typedef NTSTATUS (NTAPI *SetProcessFn)(HANDLE,ULONG,PVOID,ULONG);
typedef NTSTATUS (NTAPI *QueryObjectFn)(HANDLE,ULONG,PVOID,ULONG,PULONG);
typedef BOOL (WINAPI *SidStringFn)(PSID,LPWSTR*);
typedef BOOL (WINAPI *ShellExecuteFn)(SHELLEXECUTEINFOW*);
typedef NTSTATUS (NTAPI *NtOpenTokenFn)(HANDLE,ACCESS_MASK,PHANDLE);
typedef NTSTATUS (NTAPI *NtTokenInfoFn)(HANDLE,TOKEN_INFORMATION_CLASS,PVOID,ULONG,PULONG);
typedef NTSTATUS (NTAPI *NtDuplicateTokenFn)(HANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,BOOLEAN,TOKEN_TYPE,PHANDLE);
typedef NTSTATUS (NTAPI *NtAccessFn)(PSECURITY_DESCRIPTOR,HANDLE,ACCESS_MASK,PGENERIC_MAPPING,PPRIVILEGE_SET,PULONG,PULONG,NTSTATUS*);
typedef NTSTATUS (NTAPI *NtSecurityFn)(HANDLE,SECURITY_INFORMATION,PSECURITY_DESCRIPTOR,ULONG,PULONG);
typedef BOOLEAN (NTAPI *RtlSidFn)(PSID);
typedef BOOLEAN (NTAPI *RtlEqualSidFn)(PSID,PSID);
typedef ULONG (NTAPI *RtlSidLengthFn)(PSID);
typedef NTSTATUS (NTAPI *RtlControlFn)(PSECURITY_DESCRIPTOR,PSECURITY_DESCRIPTOR_CONTROL,PULONG);
typedef NTSTATUS (NTAPI *RtlOwnerFn)(PSECURITY_DESCRIPTOR,PSID*,PBOOLEAN);
typedef NTSTATUS (NTAPI *RtlDaclFn)(PSECURITY_DESCRIPTOR,PBOOLEAN,PACL*,PBOOLEAN);

static OpenTokenFn pOpenToken;
static TokenInfoFn pTokenInfo;
static EqualSidFn pEqualSid;
static ValidSidFn pValidSid;
static SidLengthFn pSidLength;
static WellKnownFn pWellKnown;
static DuplicateFn pDuplicate;
static AccessFn pAccess;
static SecurityFn pSecurity;
static ControlFn pControl;
static DescriptorOwnerFn pDescriptorOwner;
static DescriptorDaclFn pDescriptorDacl;
static AclInfoFn pAclInfo;
static AceFn pAce;
static SddlFn pSddl;
static FolderFn pFolder;
static OpenAlgorithmFn pOpenAlgorithm;
static GetPropertyFn pGetProperty;
static CreateHashFn pCreateHash;
static HashDataFn pHashData;
static FinishHashFn pFinishHash;
static DestroyHashFn pDestroyHash;
static CloseAlgorithmFn pCloseAlgorithm;
static CreateRelativeFn pCreateRelative;
static CreateObjectDirectoryFn pCreateObjectDirectory;
static OpenObjectFn pOpenObjectDirectory,pOpenLink;
static CreateLinkFn pCreateLink;
static QueryLinkFn pQueryLink;
static SetObjectSecurityFn pSetObjectSecurity;
static SetProcessFn pSetProcess;
static QueryObjectFn pQueryObject;
static SidStringFn pSidString;
static ShellExecuteFn pShellExecute;
static NtOpenTokenFn pNtOpenToken; static NtTokenInfoFn pNtTokenInfo; static NtDuplicateTokenFn pNtDuplicateToken;
static NtAccessFn pNtAccess; static NtSecurityFn pNtSecurity;
static RtlSidFn pRtlValidSid; static RtlEqualSidFn pRtlEqualSid; static RtlSidLengthFn pRtlSidLength;
static RtlControlFn pRtlControl; static RtlOwnerFn pRtlOwner; static RtlDaclFn pRtlDacl;

static HANDLE parent, parentToken, parentAccess, ownToken, pipeHandle, job, payload, payloadThread;
static HANDLE ancestors[MAX_ANCESTORS], payloadGuard, configGuard;
static DWORD ancestorCount, parentPid, selfPid;
static DWORD selfIntegrity;
static ULONGLONG parentStarted, selfStarted;
static BOOL sessionOwned, childCreated;
static BOOL requestMode,approvalTest,scheduledMode;
static DWORD childExit=STILL_ACTIVE;
static WCHAR token[33], windowsPath[CAP], systemPath[CAP], commonPath[CAP];
static WCHAR canonical[CAP], cacheRoot[CAP], cacheHash[CAP], cacheSession[CAP];
static WCHAR payloadPath[CAP], configPath[CAP], tempPath[CAP], pipePath[128];
static WCHAR payloadLaunchPath[CAP];
static WCHAR workPath[CAP], pathScratch[CAP], launchArguments[512], environment[CAP*4];
static BYTE ioBytes[PACKET_MAX+4], readBytes[65536];
static union { ULONGLONG alignment; BYTE bytes[8192]; } infoBuffer, privilegeBuffer;
static BYTE selfSid[SECURITY_MAX_SID_SIZE], parentSid[SECURITY_MAX_SID_SIZE];
static BYTE adminSid[SECURITY_MAX_SID_SIZE], systemSid[SECURITY_MAX_SID_SIZE], usersSid[SECURITY_MAX_SID_SIZE];
static PSECURITY_DESCRIPTOR protectedSd;
static WIN32_FIND_DATAW findData;
static HANDLE sourceGuard,sourceAncestors[MAX_ANCESTORS],privateDirectory,namespaceLinks[8],globalNamespace,highNamespace;
static DWORD sourceAncestorCount,namespaceLinkCount;
static WCHAR sourceCanonical[CAP],sourceDos[CAP],privateObjectPath[512],highObjectPath[512],namespaceScratch[CAP];
static WCHAR registrationScope[65],registrationRoot[CAP],registrationFolder[CAP],controllerKernelPath[CAP];
static HANDLE registrationGuard,controllerGuard,controllerAncestors[MAX_ANCESTORS];
static DWORD controllerAncestorCount;
static BYTE registrationBytes[108+SECURITY_MAX_SID_SIZE];
static NativeStopRecord stopRecord;
static ULONGLONG runBegan,drainBegan;
static BYTE diagnosticBytes[DIAGNOSTIC_BYTES];
static OVERLAPPED diagnosticIo;
static IO_STATUS_BLOCK diagnosticOpen;
static HANDLE diagnosticFile,diagnosticEvent;
static void writeFailureDiagnostic(DWORD result);
static BOOL jobCount(DWORD *remaining);

static SIZE_T wlen(const WCHAR *s) { SIZE_T n=0; if(s) while(s[n]) ++n; return n; }
static BOOL same(const WCHAR *a,const WCHAR *b) { while(*a&&*a==*b) { ++a; ++b; } return *a==*b; }
static WCHAR lower(WCHAR c) { return c>=L'A'&&c<=L'Z' ? c+32 : c; }
static BOOL sameInsensitive(const WCHAR *a,const WCHAR *b) { while(*a&&lower(*a)==lower(*b)) { ++a; ++b; } return lower(*a)==lower(*b); }
static BOOL copy(WCHAR *d,SIZE_T cap,const WCHAR *s) { SIZE_T n=wlen(s); if(n>=cap) return FALSE; memcpy(d,s,(n+1)*sizeof(WCHAR)); return TRUE; }
static BOOL append(WCHAR *d,SIZE_T cap,const WCHAR *s) { SIZE_T n=wlen(d), m=wlen(s); if(n+m>=cap) return FALSE; memcpy(d+n,s,(m+1)*sizeof(WCHAR)); return TRUE; }
static BOOL childPath(WCHAR *d,const WCHAR *base,const WCHAR *name) { return copy(d,CAP,base)&&append(d,CAP,L"\\")&&append(d,CAP,name); }
static BOOL hex(WCHAR c) { return (c>=L'0'&&c<=L'9')||(c>=L'a'&&c<=L'f'); }
static BOOL hexText(const WCHAR *s,SIZE_T n,BOOL nonzero) { SIZE_T i; BOOL any=FALSE; if(wlen(s)!=n) return FALSE; for(i=0;i<n;++i) { if(!hex(s[i])) return FALSE; if(s[i]!=L'0') any=TRUE; } return !nonzero||any; }
static BOOL decodedHex(const WCHAR *text,SIZE_T length,BYTE *bytes) {
    SIZE_T i; if(length%2||!hexText(text,length,FALSE)) return FALSE;
    for(i=0;i<length/2;++i) { WCHAR a=text[i*2],b=text[i*2+1]; bytes[i]=(BYTE)(((a<=L'9'?a-L'0':a-L'a'+10)<<4)|(b<=L'9'?b-L'0':b-L'a'+10)); }
    return TRUE;
}
static BOOL number(const WCHAR *s,ULONGLONG maximum,ULONGLONG *out) { ULONGLONG n=0; if(!*s||(*s==L'0'&&s[1])) return FALSE; while(*s) { unsigned digit=(unsigned)(*s++-L'0'); if(digit>9||digit>maximum||n>(maximum-digit)/10) return FALSE; n=n*10+digit; } *out=n; return n!=0; }
static BOOL digits(WCHAR *d,SIZE_T cap,ULONGLONG n) { WCHAR b[24]; SIZE_T i=0,j=0; do { b[i++]=(WCHAR)(L'0'+n%10); n/=10; } while(n&&i<24); if(i>=cap) return FALSE; while(i) d[j++]=b[--i]; d[j]=0; return TRUE; }
static BOOL nextArgument(const WCHAR **p,WCHAR *out,SIZE_T cap) { SIZE_T n=0; while(**p==L' '||**p==L'\t') ++*p; if(!**p) return FALSE; while(**p&&**p!=L' '&&**p!=L'\t') { WCHAR c=*(*p)++; if(c==L'"'||c<32||n+1>=cap) return FALSE; out[n++]=c; } out[n]=0; return n!=0; }
static const WCHAR *afterExecutable(const WCHAR *p) { if(*p==L'"') { ++p; while(*p&&*p!=L'"') ++p; if(!*p) return NULL; ++p; if(*p&&*p!=L' '&&*p!=L'\t') return NULL; } else { while(*p&&*p!=L' '&&*p!=L'\t') ++p; } return p; }
static BOOL parse(const WCHAR *line,BOOL *test) {
    WCHAR mode[64], nonce[64], pidText[32], ticksText[32], extra[2]; ULONGLONG pid,ticks;
    const WCHAR *p=afterExecutable(line); *test=FALSE; requestMode=approvalTest=scheduledMode=FALSE; registrationScope[0]=0;
    if(!p||!nextArgument(&p,mode,64)) return FALSE;
    if(same(mode,L"--bootstrap-self-test")) { while(*p==L' '||*p==L'\t') ++p; *test=TRUE; return !*p; }
    requestMode=same(mode,L"--request-administrator"); approvalTest=same(mode,L"--approval-self-test");
    scheduledMode=same(mode,L"--scheduled-administrator-bootstrap");
#ifdef TASKBARSELECT_SCHEDULED_ONLY
    if(!scheduledMode) return FALSE;
#endif
    if(scheduledMode&&(!nextArgument(&p,registrationScope,65)||!hexText(registrationScope,64,FALSE))) return FALSE;
    if((!requestMode&&!approvalTest&&!scheduledMode&&!same(mode,L"--administrator-bootstrap"))||!nextArgument(&p,nonce,64)||!hexText(nonce,32,TRUE)||
       !nextArgument(&p,pidText,32)||!number(pidText,0xffffffffULL,&pid)||
       !nextArgument(&p,ticksText,32)||!number(ticksText,DOTNET_MAX,&ticks)||ticks<=DOTNET_EPOCH) return FALSE;
    while(*p==L' '||*p==L'\t') ++p; if(*p||nextArgument(&p,extra,2)) return FALSE;
    if(!copy(token,33,nonce)) return FALSE; parentPid=(DWORD)pid; parentStarted=ticks; return TRUE;
}
static BOOL alive(void) { return parent&&WaitForSingleObject(parent,0)==WAIT_TIMEOUT; }
static BOOL started(HANDLE process,ULONGLONG *ticks) { FILETIME c,e,k,u; ULARGE_INTEGER n; if(!GetProcessTimes(process,&c,&e,&k,&u)) return FALSE; n.LowPart=c.dwLowDateTime; n.HighPart=c.dwHighDateTime; if(n.QuadPart>DOTNET_MAX-DOTNET_EPOCH) return FALSE; *ticks=n.QuadPart+DOTNET_EPOCH; return *ticks>DOTNET_EPOCH; }
static void closeHandle(HANDLE *h) { if(*h&&*h!=INVALID_HANDLE_VALUE) CloseHandle(*h); *h=NULL; }
static BOOL inside(const void *p,SIZE_T n,const BYTE *base,DWORD length) { ULONG_PTR a=(ULONG_PTR)p,b=(ULONG_PTR)base; return a>=b&&n<=length&&a-b<=length-n; }
static BOOL boundedSid(PSID sid,const BYTE *base,DWORD length) {
    const BYTE *s=(const BYTE*)sid; DWORD n;
    if(!inside(s,8,base,length)||s[0]!=SID_REVISION||s[1]>SID_MAX_SUB_AUTHORITIES) return FALSE;
    n=8+4*s[1]; return inside(s,n,base,length)&&pValidSid(sid)&&pSidLength(sid)==n;
}

static HMODULE systemDll(const WCHAR *name) {
    /* Actual roles establish the sealed process map through ntdll alone before
       any DLL load; this spelling is then a verified physical System32 path. */
    if(!childPath(workPath,systemPath,name)) return NULL;
    return LoadLibraryExW(workPath,NULL,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
}
#define LOAD_FN(module,variable,type,name) do { variable=(type)(void*)GetProcAddress(module,name); if(!variable) return FALSE; } while(0)
static BOOL earlyOpenToken(HANDLE process,DWORD access,PHANDLE out) { return pNtOpenToken(process,access,out)>=0; }
static BOOL earlyTokenInfo(HANDLE t,TOKEN_INFORMATION_CLASS kind,LPVOID b,DWORD size,PDWORD n) { return pNtTokenInfo(t,kind,b,size,n)>=0; }
static BOOL earlyValidSid(PSID sid) { return pRtlValidSid(sid)!=0; }
static BOOL earlyEqualSid(PSID a,PSID b) { return pRtlEqualSid(a,b)!=0; }
static BOOL earlyDuplicate(HANDLE t,SECURITY_IMPERSONATION_LEVEL level,PHANDLE out) {
    OBJECT_ATTRIBUTES a; SECURITY_QUALITY_OF_SERVICE qos; memset(&a,0,sizeof(a)); memset(&qos,0,sizeof(qos));
    a.Length=sizeof(a); a.SecurityQualityOfService=&qos; qos.Length=sizeof(qos); qos.ImpersonationLevel=level;
    return pNtDuplicateToken(t,TOKEN_QUERY,&a,FALSE,TokenImpersonation,out)>=0;
}
static BOOL earlyAccess(PSECURITY_DESCRIPTOR sd,HANDLE t,DWORD desired,PGENERIC_MAPPING mapping,PPRIVILEGE_SET p,LPDWORD n,LPDWORD granted,LPBOOL allowed) {
    NTSTATUS decision=(NTSTATUS)0xc0000022; NTSTATUS status=pNtAccess(sd,t,desired,mapping,p,n,granted,&decision); *allowed=decision>=0; return status>=0;
}
static DWORD earlySecurity(HANDLE h,SE_OBJECT_TYPE kind,SECURITY_INFORMATION requested,PSID *owner,PSID *group,PACL *dacl,PACL *sacl,PSECURITY_DESCRIPTOR *out) {
    ULONG n=0; NTSTATUS status; PVOID sd; (void)kind; (void)owner; (void)group; (void)dacl; (void)sacl;
    status=pNtSecurity(h,requested,NULL,0,&n); if((ULONG)status!=0xc0000023||!n||n>65536) return ERROR_ACCESS_DENIED;
    sd=LocalAlloc(LMEM_FIXED,n); if(!sd) return ERROR_NOT_ENOUGH_MEMORY;
    status=pNtSecurity(h,requested,sd,n,&n); if(status<0) { LocalFree(sd); return ERROR_ACCESS_DENIED; } *out=sd; return ERROR_SUCCESS;
}
static BOOL earlyControl(PSECURITY_DESCRIPTOR sd,PSECURITY_DESCRIPTOR_CONTROL c,LPDWORD rev) { return pRtlControl(sd,c,rev)>=0; }
static BOOL earlyOwner(PSECURITY_DESCRIPTOR sd,PSID *sid,LPBOOL defaulted) { BOOLEAN d=FALSE; NTSTATUS s=pRtlOwner(sd,sid,&d); *defaulted=d; return s>=0; }
static BOOL earlyDacl(PSECURITY_DESCRIPTOR sd,LPBOOL present,PACL *acl,LPBOOL defaulted) { BOOLEAN p=FALSE,d=FALSE; NTSTATUS s=pRtlDacl(sd,&p,acl,&d); *present=p; *defaulted=d; return s>=0; }
static BOOL initialNativeFunctions(void) {
    HMODULE nt=GetModuleHandleW(L"ntdll.dll"); if(!nt) return FALSE;
    LOAD_FN(nt,pNtOpenToken,NtOpenTokenFn,"NtOpenProcessToken"); LOAD_FN(nt,pNtTokenInfo,NtTokenInfoFn,"NtQueryInformationToken");
    LOAD_FN(nt,pNtDuplicateToken,NtDuplicateTokenFn,"NtDuplicateToken"); LOAD_FN(nt,pNtAccess,NtAccessFn,"NtAccessCheck");
    LOAD_FN(nt,pNtSecurity,NtSecurityFn,"NtQuerySecurityObject"); LOAD_FN(nt,pRtlValidSid,RtlSidFn,"RtlValidSid");
    LOAD_FN(nt,pRtlEqualSid,RtlEqualSidFn,"RtlEqualSid"); LOAD_FN(nt,pRtlSidLength,RtlSidLengthFn,"RtlLengthSid");
    LOAD_FN(nt,pRtlControl,RtlControlFn,"RtlGetControlSecurityDescriptor"); LOAD_FN(nt,pRtlOwner,RtlOwnerFn,"RtlGetOwnerSecurityDescriptor");
    LOAD_FN(nt,pRtlDacl,RtlDaclFn,"RtlGetDaclSecurityDescriptor"); LOAD_FN(nt,pCreateRelative,CreateRelativeFn,"NtCreateFile");
    LOAD_FN(nt,pCreateObjectDirectory,CreateObjectDirectoryFn,"NtCreateDirectoryObject"); LOAD_FN(nt,pOpenObjectDirectory,OpenObjectFn,"NtOpenDirectoryObject");
    LOAD_FN(nt,pOpenLink,OpenObjectFn,"NtOpenSymbolicLinkObject"); LOAD_FN(nt,pCreateLink,CreateLinkFn,"NtCreateSymbolicLinkObject");
    LOAD_FN(nt,pQueryLink,QueryLinkFn,"NtQuerySymbolicLinkObject"); LOAD_FN(nt,pSetObjectSecurity,SetObjectSecurityFn,"NtSetSecurityObject");
    LOAD_FN(nt,pSetProcess,SetProcessFn,"NtSetInformationProcess"); LOAD_FN(nt,pQueryObject,QueryObjectFn,"NtQueryObject");
    pOpenToken=earlyOpenToken; pTokenInfo=earlyTokenInfo; pValidSid=earlyValidSid; pEqualSid=earlyEqualSid; pSidLength=pRtlSidLength;
    pDuplicate=earlyDuplicate; pAccess=earlyAccess; pSecurity=earlySecurity; pControl=earlyControl; pDescriptorOwner=earlyOwner; pDescriptorDacl=earlyDacl;
    memset(adminSid,0,sizeof(adminSid)); adminSid[0]=1; adminSid[1]=2; adminSid[7]=5; *(DWORD*)(adminSid+8)=32; *(DWORD*)(adminSid+12)=544;
    memset(systemSid,0,sizeof(systemSid)); systemSid[0]=1; systemSid[1]=1; systemSid[7]=5; *(DWORD*)(systemSid+8)=18;
    memset(usersSid,0,sizeof(usersSid)); usersSid[0]=1; usersSid[1]=2; usersSid[7]=5; *(DWORD*)(usersSid+8)=32; *(DWORD*)(usersSid+12)=545;
    return TRUE;
}
static BOOL libraries(void) {
    HMODULE advapi,shell,bcrypt,ntdll; DWORD n;
    if(!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)) return FALSE;
    n=GetSystemDirectoryW(systemPath,CAP); if(!n||n>=CAP) return FALSE;
    n=GetWindowsDirectoryW(windowsPath,CAP); if(!n||n>=CAP||windowsPath[1]!=L':'||windowsPath[2]!=L'\\') return FALSE;
    advapi=systemDll(L"advapi32.dll"); shell=systemDll(L"shell32.dll"); bcrypt=systemDll(L"bcrypt.dll");
    ntdll=GetModuleHandleW(L"ntdll.dll"); if(!advapi||!shell||!bcrypt||!ntdll) return FALSE;
    LOAD_FN(ntdll,pCreateRelative,CreateRelativeFn,"NtCreateFile");
    LOAD_FN(ntdll,pCreateObjectDirectory,CreateObjectDirectoryFn,"NtCreateDirectoryObject");
    LOAD_FN(ntdll,pOpenObjectDirectory,OpenObjectFn,"NtOpenDirectoryObject");
    LOAD_FN(ntdll,pOpenLink,OpenObjectFn,"NtOpenSymbolicLinkObject");
    LOAD_FN(ntdll,pCreateLink,CreateLinkFn,"NtCreateSymbolicLinkObject");
    LOAD_FN(ntdll,pQueryLink,QueryLinkFn,"NtQuerySymbolicLinkObject");
    LOAD_FN(ntdll,pSetObjectSecurity,SetObjectSecurityFn,"NtSetSecurityObject");
    LOAD_FN(ntdll,pSetProcess,SetProcessFn,"NtSetInformationProcess");
    LOAD_FN(ntdll,pQueryObject,QueryObjectFn,"NtQueryObject");
    LOAD_FN(advapi,pOpenToken,OpenTokenFn,"OpenProcessToken"); LOAD_FN(advapi,pTokenInfo,TokenInfoFn,"GetTokenInformation");
    LOAD_FN(advapi,pEqualSid,EqualSidFn,"EqualSid"); LOAD_FN(advapi,pValidSid,ValidSidFn,"IsValidSid");
    LOAD_FN(advapi,pSidLength,SidLengthFn,"GetLengthSid"); LOAD_FN(advapi,pWellKnown,WellKnownFn,"CreateWellKnownSid");
    LOAD_FN(advapi,pDuplicate,DuplicateFn,"DuplicateToken"); LOAD_FN(advapi,pAccess,AccessFn,"AccessCheck");
    LOAD_FN(advapi,pSecurity,SecurityFn,"GetSecurityInfo"); LOAD_FN(advapi,pControl,ControlFn,"GetSecurityDescriptorControl");
    LOAD_FN(advapi,pDescriptorOwner,DescriptorOwnerFn,"GetSecurityDescriptorOwner");
    LOAD_FN(advapi,pDescriptorDacl,DescriptorDaclFn,"GetSecurityDescriptorDacl");
    LOAD_FN(advapi,pAclInfo,AclInfoFn,"GetAclInformation"); LOAD_FN(advapi,pAce,AceFn,"GetAce");
    LOAD_FN(advapi,pSddl,SddlFn,"ConvertStringSecurityDescriptorToSecurityDescriptorW");
    LOAD_FN(advapi,pSidString,SidStringFn,"ConvertSidToStringSidW");
    LOAD_FN(shell,pShellExecute,ShellExecuteFn,"ShellExecuteExW");
    LOAD_FN(shell,pFolder,FolderFn,"SHGetFolderPathW"); LOAD_FN(bcrypt,pOpenAlgorithm,OpenAlgorithmFn,"BCryptOpenAlgorithmProvider");
    LOAD_FN(bcrypt,pGetProperty,GetPropertyFn,"BCryptGetProperty"); LOAD_FN(bcrypt,pCreateHash,CreateHashFn,"BCryptCreateHash");
    LOAD_FN(bcrypt,pHashData,HashDataFn,"BCryptHashData"); LOAD_FN(bcrypt,pFinishHash,FinishHashFn,"BCryptFinishHash");
    LOAD_FN(bcrypt,pDestroyHash,DestroyHashFn,"BCryptDestroyHash"); LOAD_FN(bcrypt,pCloseAlgorithm,CloseAlgorithmFn,"BCryptCloseAlgorithmProvider");
    n=sizeof(adminSid); if(!pWellKnown(WinBuiltinAdministratorsSid,NULL,adminSid,&n)) return FALSE;
    n=sizeof(systemSid); if(!pWellKnown(WinLocalSystemSid,NULL,systemSid,&n)) return FALSE;
    n=sizeof(usersSid); if(!pWellKnown(WinBuiltinUsersSid,NULL,usersSid,&n)) return FALSE;
    return pSddl(L"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)",SDDL_REVISION_1,&protectedSd,NULL);
}
static BOOL tokenMetadata(HANDLE process,BYTE *sid,DWORD *integrity,BOOL *elevated,HANDLE *retained) {
    HANDLE t=NULL; DWORD n=0,len; TOKEN_USER *user; TOKEN_MANDATORY_LABEL *label; TOKEN_ELEVATION elevation;
    BOOL ok=FALSE;
    if(!pOpenToken(process,TOKEN_QUERY|(retained?TOKEN_DUPLICATE:0),&t)) return FALSE;
    if(!pTokenInfo(t,TokenUser,infoBuffer.bytes,sizeof(infoBuffer.bytes),&n)||n>sizeof(infoBuffer.bytes)||n<sizeof(TOKEN_USER)) goto done;
    user=(TOKEN_USER*)infoBuffer.bytes;
    if(!boundedSid(user->User.Sid,infoBuffer.bytes,n)) goto done;
    len=pSidLength(user->User.Sid); if(len>SECURITY_MAX_SID_SIZE||!inside(user->User.Sid,len,infoBuffer.bytes,n)) goto done;
    memcpy(sid,user->User.Sid,len);
    if(!pTokenInfo(t,TokenIntegrityLevel,infoBuffer.bytes,sizeof(infoBuffer.bytes),&n)||n>sizeof(infoBuffer.bytes)||n<sizeof(TOKEN_MANDATORY_LABEL)) goto done;
    label=(TOKEN_MANDATORY_LABEL*)infoBuffer.bytes;
    if(!boundedSid(label->Label.Sid,infoBuffer.bytes,n)) goto done;
    len=pSidLength(label->Label.Sid); if(((BYTE*)label->Label.Sid)[1]!=1||len!=12||
        memcmp((BYTE*)label->Label.Sid+2,"\0\0\0\0\0\020",6)) goto done;
    *integrity=*(DWORD*)((BYTE*)label->Label.Sid+8);
    if(!pTokenInfo(t,TokenElevation,&elevation,sizeof(elevation),&n)||n!=sizeof(elevation)) goto done;
    *elevated=elevation.TokenIsElevated!=0; ok=TRUE;
done:
    if(ok&&retained) { *retained=t; t=NULL; } closeHandle(&t); return ok;
}
static BOOL actualProcessSessionMatches(DWORD first,DWORD second) { return first==second; }
static BOOL linkedHighSessionMatches(DWORD rawSession,DWORD actualSession) {
    /* Credential-launched MED processes can have a genuine linked HIGH context
       with raw TS SessionId zero. DOS identity remains its AuthenticationId;
       actual MED/HIGH process session equality is checked independently. */
    return rawSession==0||rawSession==actualSession;
}
#ifdef TASKBARSELECT_SCHEDULED_ONLY
static BOOL scheduledRoleAdmission(BOOL pure,DWORD integrity,BOOL elevated) {
    return pure||(integrity==SECURITY_MANDATORY_HIGH_RID&&elevated);
}
static BOOL scheduledImageRole(void) {
    static const char marker[]="TaskbarSelect.ScheduledOnly.v1";
    HMODULE image=GetModuleHandleW(NULL); HRSRC resource; HGLOBAL loaded; const BYTE *bytes;
    if(!image||(resource=FindResourceExW(image,MAKEINTRESOURCEW(10),MAKEINTRESOURCEW(211),1033))==NULL||
       SizeofResource(image,resource)!=sizeof(marker)-1||(loaded=LoadResource(image,resource))==NULL||
       (bytes=(const BYTE*)LockResource(loaded))==NULL) return FALSE;
    return memcmp(bytes,marker,sizeof(marker)-1)==0;
}
#endif
static BOOL authenticate(BOOL selfTest) {
    DWORD ownIntegrity,otherIntegrity,ownSession,otherSession; BOOL ownElevated,otherElevated; ULONGLONG current;
    selfPid=GetCurrentProcessId();
    if(!tokenMetadata(GetCurrentProcess(),selfSid,&ownIntegrity,&ownElevated,&ownToken)) return FALSE;
    selfIntegrity=ownIntegrity;
#ifdef TASKBARSELECT_SCHEDULED_ONLY
    if(!scheduledImageRole()||!scheduledRoleAdmission(selfTest,ownIntegrity,ownElevated)||
       (!selfTest&&(!scheduledMode||requestMode||approvalTest))) return FALSE;
#endif
    if(selfTest) return TRUE; /* Fixed pure mode has no filesystem or child path. */
    if(((requestMode||approvalTest)?(ownElevated||ownIntegrity<SECURITY_MANDATORY_MEDIUM_RID||ownIntegrity>=SECURITY_MANDATORY_HIGH_RID):
       (!ownElevated||ownIntegrity!=SECURITY_MANDATORY_HIGH_RID))||parentPid==selfPid||
       !ProcessIdToSessionId(selfPid,&ownSession)) return FALSE;
    parent=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,parentPid);
    if(!parent||!started(parent,&current)||current!=parentStarted||!alive()||
       !started(GetCurrentProcess(),&selfStarted)||!ProcessIdToSessionId(parentPid,&otherSession)||!actualProcessSessionMatches(ownSession,otherSession)||
       !tokenMetadata(parent,parentSid,&otherIntegrity,&otherElevated,&parentToken)||otherElevated||
       otherIntegrity<SECURITY_MANDATORY_MEDIUM_RID||otherIntegrity>=SECURITY_MANDATORY_HIGH_RID||!pEqualSid(selfSid,parentSid)||
       !pDuplicate(parentToken,SecurityImpersonation,&parentAccess)) return FALSE;
    return TRUE;
}
static BOOL trustedPipe(void) { ULONG pid=0; return pipeHandle&&alive()&&GetNamedPipeServerProcessId(pipeHandle,&pid)&&pid==parentPid; }
static BOOL connectPipe(void) {
    ULONGLONG end=GetTickCount64()+HANDSHAKE_MS;
    if(!copy(pipePath,128,requestMode?L"\\\\.\\pipe\\TaskbarSelect.AdminApproval.":L"\\\\.\\pipe\\TaskbarSelect.AdminBootstrap.")||!append(pipePath,128,token)) return FALSE;
    while(alive()&&GetTickCount64()<end) {
        DWORD error;
        pipeHandle=CreateFileW(pipePath,GENERIC_READ|GENERIC_WRITE,0,NULL,OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED|SECURITY_SQOS_PRESENT|SECURITY_ANONYMOUS,NULL);
        if(pipeHandle!=INVALID_HANDLE_VALUE) return trustedPipe(); error=GetLastError(); pipeHandle=NULL;
        if(error!=ERROR_PIPE_BUSY&&error!=ERROR_FILE_NOT_FOUND) return FALSE;
        Sleep(20);
    } return FALSE;
}
static BOOL exactIo(BOOL write,BYTE *buffer,DWORD size,ULONGLONG end,BOOL watchChild) {
    DWORD offset=0;
    while(offset<size&&trustedPipe()) {
        OVERLAPPED ov; DWORD n=0,error,wait; HANDLE handles[3]; DWORD count=2; BOOL ok;
        ULONGLONG now=GetTickCount64(); if(now>=end) return FALSE;
        memset(&ov,0,sizeof(ov)); ov.hEvent=CreateEventW(NULL,TRUE,FALSE,NULL); if(!ov.hEvent) return FALSE;
        ok=write?WriteFile(pipeHandle,buffer+offset,size-offset,&n,&ov):ReadFile(pipeHandle,buffer+offset,size-offset,&n,&ov);
        error=ok?0:GetLastError();
        if(!ok&&error==ERROR_IO_PENDING) {
            handles[0]=ov.hEvent; handles[1]=parent; if(payload&&watchChild) handles[count++]=payload;
            now=GetTickCount64(); wait=WaitForMultipleObjects(count,handles,FALSE,now>=end?0:(DWORD)(end-now));
            if(wait!=WAIT_OBJECT_0) {
                DWORD cancelError=0,cancelWait;
                if(!CancelIoEx(pipeHandle,&ov)) cancelError=GetLastError();
                /* A stack OVERLAPPED may not be abandoned. Hard exit retains
                   no false drain claim; closing the job still kills children. */
                cancelWait=WaitForSingleObject(ov.hEvent,100);
                if(cancelWait!=WAIT_OBJECT_0) {
                    stopRecord.reason=STOP_IO_CANCEL; stopRecord.phase=DRAIN_IO_CANCEL;
                    stopRecord.error=cancelWait==WAIT_FAILED?GetLastError():ERROR_TIMEOUT;
                    stopRecord.ioWait=wait; stopRecord.ioCancelError=cancelError;
                    stopRecord.ioCancelWait=cancelWait; stopRecord.errorPhase=DRAIN_IO_CANCEL;
                    writeFailureDiagnostic(E_NOT_DRAINED); ExitProcess(E_NOT_DRAINED);
                }
                CloseHandle(ov.hEvent); return FALSE;
            }
            ok=GetOverlappedResult(pipeHandle,&ov,&n,FALSE);
        }
        CloseHandle(ov.hEvent); if(!ok||!n||n>size-offset) return FALSE; offset+=n;
    } return offset==size&&trustedPipe();
}
static void put32(BYTE *p,DWORD n) { unsigned i; for(i=0;i<4;++i) p[i]=(BYTE)(n>>(i*8)); }
static DWORD get32(const BYTE *p) { return (DWORD)p[0]|((DWORD)p[1]<<8)|((DWORD)p[2]<<16)|((DWORD)p[3]<<24); }
static void put64(BYTE *p,ULONGLONG n) { unsigned i; for(i=0;i<8;++i) p[i]=(BYTE)(n>>(i*8)); }
static ULONGLONG get64(const BYTE *p) { ULONGLONG n=0; unsigned i; for(i=0;i<8;++i) n|=(ULONGLONG)p[i]<<(i*8); return n; }
static BOOL packetShape(DWORD length,DWORD kind,DWORD sequence,DWORD expected,DWORD values) { return length==8+8*values&&length<=PACKET_MAX&&kind==expected&&sequence==1; }
static BOOL sendPacket(DWORD kind,const ULONGLONG *values,DWORD count) {
    DWORD i,length=8+8*count; if(length>PACKET_MAX) return FALSE;
    put32(ioBytes,length); put32(ioBytes+4,kind); put32(ioBytes+8,1);
    for(i=0;i<count;++i) put64(ioBytes+12+i*8,values[i]);
    return exactIo(TRUE,ioBytes,length+4,GetTickCount64()+HANDSHAKE_MS,kind!=24);
}
static BOOL receiveGrant(DWORD expected) {
    DWORD length; ULONGLONG end=GetTickCount64()+HANDSHAKE_MS;
    if(!exactIo(FALSE,ioBytes,4,end,TRUE)) return FALSE; length=get32(ioBytes);
    if(length!=16||!exactIo(FALSE,ioBytes+4,length,end,TRUE)) return FALSE;
    return packetShape(length,get32(ioBytes+4),get32(ioBytes+8),expected,1)&&get64(ioBytes+12)==1&&trustedPipe();
}
static BOOL accessDenied(PSECURITY_DESCRIPTOR sd,DWORD requested) {
    GENERIC_MAPPING mapping={FILE_GENERIC_READ,FILE_GENERIC_WRITE,FILE_GENERIC_EXECUTE,FILE_ALL_ACCESS};
    DWORD granted=0,n=sizeof(privilegeBuffer.bytes); BOOL allowed=FALSE;
    return pAccess(sd,parentAccess,requested,&mapping,(PPRIVILEGE_SET)privilegeBuffer.bytes,&n,&granted,&allowed)&&!allowed;
}
static BOOL securityPolicy(PSECURITY_DESCRIPTOR sd,BOOL exact,BOOL directory,BOOL verifyAccess) {
    PSID owner=NULL; PACL acl=NULL; SECURITY_DESCRIPTOR_CONTROL control; DWORD revision; BOOL present,defaulted;
    ACL_SIZE_INFORMATION ai; DWORD i,seen=0; BOOL ok=FALSE;
    static const DWORD denied[]={FILE_WRITE_DATA,FILE_APPEND_DATA,FILE_WRITE_EA,FILE_WRITE_ATTRIBUTES,DELETE,WRITE_DAC,WRITE_OWNER};
    if(!pDescriptorOwner(sd,&owner,&defaulted)||!pDescriptorDacl(sd,&present,&acl,&defaulted)||!present||
       !owner||!pValidSid(owner)||!acl||!pControl(sd,&control,&revision)) return FALSE;
    if(exact) {
        if(!(control&SE_DACL_PROTECTED)||(!pEqualSid(owner,adminSid)&&!pEqualSid(owner,systemSid))||
           !pAclInfo(acl,&ai,sizeof(ai),AclSizeInformation)||ai.AceCount!=3) goto done;
        for(i=0;i<ai.AceCount;++i) {
            ACCESS_ALLOWED_ACE *ace=NULL; DWORD mask,which=0;
            if(!pAce(acl,i,(LPVOID*)&ace)||!ace||ace->Header.AceType!=ACCESS_ALLOWED_ACE_TYPE||
               (ace->Header.AceFlags&~(OBJECT_INHERIT_ACE|CONTAINER_INHERIT_ACE))||
               ace->Header.AceSize<sizeof(ACCESS_ALLOWED_ACE)||!boundedSid(&ace->SidStart,(BYTE*)ace,ace->Header.AceSize)||
               pSidLength(&ace->SidStart)>ace->Header.AceSize-FIELD_OFFSET(ACCESS_ALLOWED_ACE,SidStart)) goto done;
            if(directory&&ace->Header.AceFlags!=(OBJECT_INHERIT_ACE|CONTAINER_INHERIT_ACE)) goto done;
            mask=ace->Mask;
            if(pEqualSid(&ace->SidStart,adminSid)) { which=1; if(mask!=FILE_ALL_ACCESS&&mask!=GENERIC_ALL) goto done; }
            else if(pEqualSid(&ace->SidStart,systemSid)) { which=2; if(mask!=FILE_ALL_ACCESS&&mask!=GENERIC_ALL) goto done; }
            else if(pEqualSid(&ace->SidStart,usersSid)) { which=4; if(mask!=(FILE_GENERIC_READ|FILE_GENERIC_EXECUTE)&&mask!=(GENERIC_READ|GENERIC_EXECUTE)) goto done; }
            if(!which||(seen&which)) goto done; seen|=which;
        }
        if(seen!=7) goto done;
        if(verifyAccess) {
            for(i=0;i<sizeof(denied)/sizeof(denied[0]);++i) if(!accessDenied(sd,denied[i])) goto done;
            if(directory&&!accessDenied(sd,FILE_DELETE_CHILD)) goto done;
        }
    } else {
        /* System ancestors may permit creating other directories. They must
           not let the medium parent reroute/delete our protected namespace. */
        if(!accessDenied(sd,DELETE)||!accessDenied(sd,WRITE_DAC)||!accessDenied(sd,WRITE_OWNER)||
           !accessDenied(sd,FILE_DELETE_CHILD)) goto done;
    }
    ok=TRUE;
done: return ok;
}
static BOOL protectedSecurity(HANDLE h,BOOL exact,BOOL directory) {
    PSECURITY_DESCRIPTOR sd=NULL; BOOL ok;
    /* AccessCheck requires both owner and primary group in this descriptor. */
    if(pSecurity(h,SE_FILE_OBJECT,OWNER_SECURITY_INFORMATION|GROUP_SECURITY_INFORMATION|DACL_SECURITY_INFORMATION,NULL,NULL,NULL,NULL,&sd)!=ERROR_SUCCESS) return FALSE;
    ok=securityPolicy(sd,exact,directory,TRUE); if(sd) LocalFree(sd); return ok;
}
static BOOL fileInfo(HANDLE h,BOOL directory,BY_HANDLE_FILE_INFORMATION *info) {
    FILE_ATTRIBUTE_TAG_INFO tag;
    return GetFileInformationByHandleEx(h,FileAttributeTagInfo,&tag,sizeof(tag))&&
        !(tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)&&((tag.FileAttributes&FILE_ATTRIBUTE_DIRECTORY)!=0)==directory&&
        GetFileInformationByHandle(h,info)&&info->nNumberOfLinks==1&&(info->nFileIndexHigh||info->nFileIndexLow);
}
static BOOL finalPath(HANDLE h,WCHAR *out) { DWORD n=GetFinalPathNameByHandleW(h,out,CAP,FILE_NAME_NORMALIZED|VOLUME_NAME_GUID); return n&&n<CAP; }
static BOOL rememberDirectory(HANDLE h,const WCHAR *expected,BOOL exact) {
    BY_HANDLE_FILE_INFORMATION info;
    if(ancestorCount>=MAX_ANCESTORS) { CloseHandle(h); return FALSE; }
    if(!fileInfo(h,TRUE,&info)||!protectedSecurity(h,exact,TRUE)||!finalPath(h,pathScratch)) { CloseHandle(h); return FALSE; }
    if(expected&&!sameInsensitive(expected,pathScratch)) { CloseHandle(h); return FALSE; }
    ancestors[ancestorCount++]=h; return TRUE;
}
static BOOL simpleComponent(const WCHAR *p,SIZE_T n) {
    SIZE_T i; if(!n||(n==1&&p[0]==L'.')||(n==2&&p[0]==L'.'&&p[1]==L'.')||p[n-1]==L'.'||p[n-1]==L' ') return FALSE;
    for(i=0;i<n;++i) if(p[i]<32||p[i]==L':'||p[i]==L'/'||p[i]==L'\\'||p[i]==L'"'||p[i]==L'*'||p[i]==L'?'||p[i]==L'<'||p[i]==L'>'||p[i]==L'|') return FALSE;
    return TRUE;
}
static HANDLE relativeDirectory(HANDLE container,const WCHAR *name,BOOL create) {
    UNICODE_STRING text; OBJECT_ATTRIBUTES attributes; IO_STATUS_BLOCK result; HANDLE h=NULL; NTSTATUS status;
    SIZE_T length=wlen(name);
    if(!container||!simpleComponent(name,length)||length>32766) return NULL;
    text.Length=(USHORT)(length*sizeof(WCHAR)); text.MaximumLength=text.Length; text.Buffer=(PWSTR)name;
    memset(&attributes,0,sizeof(attributes)); attributes.Length=sizeof(attributes); attributes.RootDirectory=container;
    attributes.ObjectName=&text; attributes.Attributes=0x40|0x1000; /* OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE */
    attributes.SecurityDescriptor=create?protectedSd:NULL; memset(&result,0,sizeof(result));
    status=pCreateRelative(&h,FILE_READ_ATTRIBUTES|READ_CONTROL|SYNCHRONIZE,&attributes,&result,NULL,FILE_ATTRIBUTE_NORMAL,
        FILE_SHARE_READ|FILE_SHARE_WRITE,create?2:1, /* FILE_CREATE / FILE_OPEN */
        0x20|(create?0x1:0x00200000), /* SYNCHRONOUS_IO_NONALERT; DIRECTORY_FILE or OPEN_REPARSE_POINT */
        NULL,0);
    if(status<0) { closeHandle(&h); return NULL; }
    return h;
}
static HANDLE relativeReadFile(HANDLE container,const WCHAR *name) {
    UNICODE_STRING text; OBJECT_ATTRIBUTES attributes; IO_STATUS_BLOCK result; HANDLE h=NULL; SIZE_T length=wlen(name);
    if(!container||!simpleComponent(name,length)||length>32766) return NULL;
    text.Length=(USHORT)(length*sizeof(WCHAR)); text.MaximumLength=text.Length; text.Buffer=(PWSTR)name;
    memset(&attributes,0,sizeof(attributes)); attributes.Length=sizeof(attributes); attributes.RootDirectory=container;
    attributes.ObjectName=&text; attributes.Attributes=0x40|0x1000; memset(&result,0,sizeof(result));
    if(pCreateRelative(&h,GENERIC_READ|READ_CONTROL|SYNCHRONIZE,&attributes,&result,NULL,FILE_ATTRIBUTE_NORMAL,
        FILE_SHARE_READ,1,0x20|0x00200000|0x40,NULL,0)<0) closeHandle(&h);
    return h;
}
static BOOL prepareCommon(void) {
    SIZE_T n,i,start; WCHAR root[4]; WCHAR fs[32]; DWORD flags; HANDLE h;
    if(pFolder(NULL,0x0023,NULL,0,commonPath)<0) return FALSE; /* CSIDL_COMMON_APPDATA */
    n=wlen(commonPath); if(n<=3||n>=CAP||commonPath[1]!=L':'||commonPath[2]!=L'\\') return FALSE;
    root[0]=commonPath[0]; root[1]=L':'; root[2]=L'\\'; root[3]=0;
    if(GetDriveTypeW(root)!=DRIVE_FIXED) return FALSE;
    h=CreateFileW(root,FILE_READ_ATTRIBUTES|READ_CONTROL,FILE_SHARE_READ|FILE_SHARE_WRITE,NULL,OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    if(h==INVALID_HANDLE_VALUE||!rememberDirectory(h,NULL,FALSE)||!finalPath(ancestors[ancestorCount-1],canonical)) return FALSE;
    if(GetDriveTypeW(canonical)!=DRIVE_FIXED||!GetVolumeInformationW(canonical,NULL,0,NULL,NULL,&flags,fs,32)||!sameInsensitive(fs,L"NTFS")) return FALSE;
    while(wlen(canonical)>0&&canonical[wlen(canonical)-1]==L'\\') canonical[wlen(canonical)-1]=0;
    /* Every component is opened from the previous retained filesystem handle,
       so DOS-device aliases or a later name reparse cannot reroute acquisition. */
    i=3; while(i<n) {
        start=i; while(i<n&&commonPath[i]!=L'\\') ++i;
        if(!simpleComponent(commonPath+start,i-start)) return FALSE;
        if(i-start+1>=CAP) return FALSE; memcpy(workPath,commonPath+start,(i-start)*sizeof(WCHAR)); workPath[i-start]=0;
        h=relativeDirectory(ancestors[ancestorCount-1],workPath,FALSE); if(!h) return FALSE;
        if(!append(canonical,CAP,L"\\")||!append(canonical,CAP,workPath)) { CloseHandle(h); return FALSE; }
        if(!rememberDirectory(h,canonical,FALSE)) return FALSE;
        if(i<n) ++i;
    }
    if(!finalPath(ancestors[ancestorCount-1],canonical)||canonical[0]!=L'\\'||canonical[1]!=L'\\') return FALSE;
    return TRUE;
}
static BOOL createProtectedDirectory(const WCHAR *path,BOOL fresh) {
    const WCHAR *name=path+wlen(path); HANDLE h;
    while(name>path&&name[-1]!=L'\\') --name;
    if(!ancestorCount||!simpleComponent(name,wlen(name))) return FALSE;
    h=relativeDirectory(ancestors[ancestorCount-1],name,TRUE);
    if(!h&&!fresh) h=relativeDirectory(ancestors[ancestorCount-1],name,FALSE);
    return h&&rememberDirectory(h,path,TRUE);
}
static BOOL childrenHaveShape(const WCHAR *path,SIZE_T childLength) {
    HANDLE f; DWORD error; BOOL ok=TRUE;
    if(!childPath(workPath,path,L"*")) return FALSE;
    f=FindFirstFileW(workPath,&findData); if(f==INVALID_HANDLE_VALUE) return GetLastError()==ERROR_FILE_NOT_FOUND;
    do {
        if(same(findData.cFileName,L".")||same(findData.cFileName,L"..")) continue;
        if(!(findData.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)||(findData.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)||
           !hexText(findData.cFileName,childLength,childLength==32)) { ok=FALSE; break; }
    } while(FindNextFileW(f,&findData));
    error=GetLastError(); FindClose(f); return ok&&error==ERROR_NO_MORE_FILES;
}
static BOOL ancestorsStable(void) {
    DWORD i; BY_HANDLE_FILE_INFORMATION info;
    for(i=0;i<ancestorCount;++i) if(!fileInfo(ancestors[i],TRUE,&info)) return FALSE;
    return TRUE;
}
static BOOL prepareCache(void) {
    if(!hexText(AdministratorPayloadHash,64,FALSE)||!prepareCommon()||
       !childPath(cacheRoot,canonical,L"TaskbarSelect.Admin")||!createProtectedDirectory(cacheRoot,FALSE)||!ancestorsStable()||
       !childrenHaveShape(cacheRoot,64)||!childPath(cacheHash,cacheRoot,AdministratorPayloadHash)||
       !createProtectedDirectory(cacheHash,FALSE)||!childrenHaveShape(cacheHash,32)||
       !childPath(cacheSession,cacheHash,token)||!createProtectedDirectory(cacheSession,TRUE)) return FALSE;
    sessionOwned=TRUE;
    return childPath(payloadPath,cacheSession,L"AdministratorPayload.exe")&&
        childPath(configPath,cacheSession,L"AdministratorPayload.exe.config")&&childPath(tempPath,cacheSession,L"tmp")&&
        createProtectedDirectory(tempPath,TRUE);
}
static BOOL formatStopDiagnostic(BYTE *out,const NativeStopRecord *record,const WCHAR *nonce,const WCHAR *hash,
    DWORD nativePid,ULONGLONG nativeStarted,DWORD controllerPid,ULONGLONG controllerStarted) {
    static const BYTE magic[8]={'T','S','A','N','D','0','1',0};
    memset(out,0,DIAGNOSTIC_BYTES); memcpy(out,magic,sizeof(magic));
    if(!decodedHex(nonce,32,out+88)||!hexText(nonce,32,TRUE)||!decodedHex(hash,64,out+104)) return FALSE;
    put32(out+8,DIAGNOSTIC_BYTES); put32(out+12,record->reason); put32(out+16,record->phase);
    put32(out+20,record->error); put32(out+24,record->active); put32(out+28,record->payloadWait);
    put32(out+32,record->payloadExit); put32(out+36,record->ioWait); put32(out+40,record->ioCancelError);
    put32(out+44,record->result); put64(out+48,record->drainElapsed); put64(out+56,record->runElapsed);
    put32(out+64,nativePid); put32(out+68,controllerPid); put64(out+72,nativeStarted); put64(out+80,controllerStarted);
    put32(out+136,record->flags); put32(out+140,record->payloadBeforeDrain); put32(out+144,record->ioCancelWait);
    put32(out+148,record->errorPhase); put32(out+152,record->stopCode); return TRUE;
}
static void writeFailureDiagnostic(DWORD result) {
    UNICODE_STRING name; OBJECT_ATTRIBUTES attributes; HANDLE folder; NTSTATUS opened;
    DWORD written=0,wait,error; BOOL completed; ULONGLONG now=GetTickCount64();
    BY_HANDLE_FILE_INFORMATION information; WCHAR actual[CAP];
    /* This retained handle is the fresh protected temp directory prepared by
       this HIGH image. No DOS lookup, caller-selected name or replacement is
       admitted. Only the abandoned native launch/serve path writes evidence;
       Exit90 still means descendants may remain until job close/process exit. */
    if(!sessionOwned||!ancestorCount||diagnosticFile||diagnosticEvent) return;
    folder=ancestors[ancestorCount-1];
    if(!folder||!fileInfo(folder,TRUE,&information)||!protectedSecurity(folder,TRUE,TRUE)||
       !finalPath(folder,actual)||!sameInsensitive(actual,tempPath)) return;
    stopRecord.result=result; stopRecord.flags|=childCreated?1:0;
    stopRecord.runElapsed=runBegan&&now>=runBegan?now-runBegan:0;
    stopRecord.drainElapsed=drainBegan&&now>=drainBegan?now-drainBegan:0;
    stopRecord.payloadWait=payload?WaitForSingleObject(payload,0):0;
    if(payload) { DWORD exit; if(GetExitCodeProcess(payload,&exit)) stopRecord.payloadExit=exit; }
    if(job) { DWORD count; if(jobCount(&count)) stopRecord.active=count; }
    if(!formatStopDiagnostic(diagnosticBytes,&stopRecord,token,AdministratorPayloadHash,selfPid,selfStarted,parentPid,parentStarted)) return;
    name.Length=(USHORT)(sizeof(L"NativeStop.bin")-sizeof(WCHAR)); name.MaximumLength=name.Length; name.Buffer=L"NativeStop.bin";
    memset(&attributes,0,sizeof(attributes)); attributes.Length=sizeof(attributes); attributes.RootDirectory=folder;
    attributes.ObjectName=&name; attributes.Attributes=0x40|0x1000; attributes.SecurityDescriptor=protectedSd;
    memset(&diagnosticOpen,0,sizeof(diagnosticOpen));
    opened=pCreateRelative(&diagnosticFile,GENERIC_READ|GENERIC_WRITE|READ_CONTROL|SYNCHRONIZE,&attributes,&diagnosticOpen,NULL,
        FILE_ATTRIBUTE_NORMAL,FILE_SHARE_READ,2,0x00200000|0x40,NULL,0);
    if(opened!=0) {
        /* A filter may defer open completion. Keep its IOSB/returned handle
           alive through required process exit; never wait or admit pending. */
        if(opened<0) closeHandle(&diagnosticFile); return;
    }
    if(!fileInfo(diagnosticFile,FALSE,&information)||!protectedSecurity(diagnosticFile,TRUE,FALSE)||
       !childPath(actual,tempPath,L"NativeStop.bin")||!finalPath(diagnosticFile,pathScratch)||!sameInsensitive(actual,pathScratch)) {
        closeHandle(&diagnosticFile); return;
    }
    diagnosticEvent=CreateEventW(NULL,TRUE,FALSE,NULL); if(!diagnosticEvent) { closeHandle(&diagnosticFile); return; }
    memset(&diagnosticIo,0,sizeof(diagnosticIo)); diagnosticIo.hEvent=diagnosticEvent;
    completed=WriteFile(diagnosticFile,diagnosticBytes,DIAGNOSTIC_BYTES,&written,&diagnosticIo); error=completed?0:GetLastError();
    if(!completed&&error==ERROR_IO_PENDING) {
        wait=WaitForSingleObject(diagnosticEvent,DIAGNOSTIC_IO_MS);
        if(wait!=WAIT_OBJECT_0) {
            CancelIoEx(diagnosticFile,&diagnosticIo);
            if(WaitForSingleObject(diagnosticEvent,DIAGNOSTIC_IO_MS)!=WAIT_OBJECT_0) {
                /* Every referenced byte/OVERLAPPED/handle has process lifetime.
                   Exit is already required; do not wait forever or reuse them. */
                return;
            }
        }
        completed=GetOverlappedResult(diagnosticFile,&diagnosticIo,&written,FALSE);
    }
    (void)completed; (void)written; closeHandle(&diagnosticFile); closeHandle(&diagnosticEvent);
}
static BOOL digestMatches(const BYTE *bytes,DWORD size) {
    BCRYPT_ALG_HANDLE algorithm=NULL; BCRYPT_HASH_HANDLE hashHandle=NULL; BYTE digest[32]; BYTE *object=NULL;
    DWORD length=0,received=0,i; BOOL ok=FALSE; static const WCHAR alphabet[]=L"0123456789abcdef";
    if(pOpenAlgorithm(&algorithm,BCRYPT_SHA256_ALGORITHM,NULL,0)<0||
       pGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,(PUCHAR)&length,sizeof(length),&received,0)<0||received!=sizeof(length)||!length||length>1048576) goto done;
    object=HeapAlloc(GetProcessHeap(),0,length); if(!object) goto done;
    if(pCreateHash(algorithm,&hashHandle,object,length,NULL,0,0)<0||pHashData(hashHandle,(PUCHAR)bytes,size,0)<0||
       pFinishHash(hashHandle,digest,sizeof(digest),0)<0) goto done;
    for(i=0;i<32;++i) if(AdministratorPayloadHash[i*2]!=alphabet[digest[i]>>4]||AdministratorPayloadHash[i*2+1]!=alphabet[digest[i]&15]) goto done;
    ok=TRUE;
done: if(hashHandle) pDestroyHash(hashHandle); if(object) HeapFree(GetProcessHeap(),0,object); if(algorithm) pCloseAlgorithm(algorithm,0); return ok;
}
static BOOL fileDigestMatches(HANDLE file,const BYTE *expected,DWORD maximum) {
    BCRYPT_ALG_HANDLE algorithm=NULL; BCRYPT_HASH_HANDLE hashHandle=NULL; BYTE digest[32]; BYTE *object=NULL;
    DWORD length=0,received=0,n,total=0; LARGE_INTEGER zero,size; BOOL ok=FALSE;
    zero.QuadPart=0;
    if(!file||!GetFileSizeEx(file,&size)||size.QuadPart<512||size.QuadPart>maximum||!SetFilePointerEx(file,zero,NULL,FILE_BEGIN)||
       pOpenAlgorithm(&algorithm,BCRYPT_SHA256_ALGORITHM,NULL,0)<0||
       pGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,(PUCHAR)&length,sizeof(length),&received,0)<0||received!=sizeof(length)||!length||length>1048576) goto done;
    object=HeapAlloc(GetProcessHeap(),0,length); if(!object) goto done;
    if(pCreateHash(algorithm,&hashHandle,object,length,NULL,0,0)<0) goto done;
    for(;;) {
        if(!ReadFile(file,readBytes,sizeof(readBytes),&n,NULL)||n>maximum-total) goto done;
        if(!n) break;
        if(pHashData(hashHandle,readBytes,n,0)<0) goto done; total+=n;
    }
    if(total!=(DWORD)size.QuadPart||pFinishHash(hashHandle,digest,sizeof(digest),0)<0||memcmp(digest,expected,sizeof(digest))) goto done;
    ok=TRUE;
done: if(hashHandle) pDestroyHash(hashHandle); if(object) HeapFree(GetProcessHeap(),0,object); if(algorithm) pCloseAlgorithm(algorithm,0); return ok;
}
static BOOL fixedConfig(void) {
    static const char expected[]="<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.8\"/></startup></configuration>\n";
    return AdministratorConfigLength==sizeof(expected)-1&&!memcmp(AdministratorConfigBytes,expected,sizeof(expected)-1);
}
static BOOL sameFile(const BY_HANDLE_FILE_INFORMATION *a,const BY_HANDLE_FILE_INFORMATION *b) {
    return a->dwVolumeSerialNumber==b->dwVolumeSerialNumber&&a->nFileIndexHigh==b->nFileIndexHigh&&a->nFileIndexLow==b->nFileIndexLow&&
        a->nFileSizeHigh==b->nFileSizeHigh&&a->nFileSizeLow==b->nFileSizeLow;
}
static BOOL bytesMatch(HANDLE h,const BYTE *bytes,DWORD length) {
    DWORD offset=0,n,want; LARGE_INTEGER zero; zero.QuadPart=0;
    if(!SetFilePointerEx(h,zero,NULL,FILE_BEGIN)) return FALSE;
    while(offset<length) { want=length-offset; if(want>sizeof(readBytes)) want=sizeof(readBytes);
        if(!ReadFile(h,readBytes,want,&n,NULL)||n!=want||memcmp(readBytes,bytes+offset,n)) return FALSE; offset+=n; }
    return ReadFile(h,readBytes,1,&n,NULL)&&!n;
}
static BOOL createVerifiedFile(const WCHAR *path,const BYTE *bytes,DWORD length,HANDLE *guard) {
    SECURITY_ATTRIBUTES sa={sizeof(sa),protectedSd,FALSE}; HANDLE writeHandle; BY_HANDLE_FILE_INFORMATION before,after; DWORD n,offset=0;
    writeHandle=CreateFileW(path,GENERIC_READ|GENERIC_WRITE|READ_CONTROL,0,&sa,CREATE_NEW,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    if(writeHandle==INVALID_HANDLE_VALUE) return FALSE;
    while(offset<length) { DWORD want=length-offset; if(want>65536) want=65536;
        if(!WriteFile(writeHandle,bytes+offset,want,&n,NULL)||!n||n>want) { CloseHandle(writeHandle); return FALSE; } offset+=n; }
    if(!FlushFileBuffers(writeHandle)||!fileInfo(writeHandle,FALSE,&before)||before.nFileSizeHigh||before.nFileSizeLow!=length||
       !protectedSecurity(writeHandle,TRUE,FALSE)||!bytesMatch(writeHandle,bytes,length)) { CloseHandle(writeHandle); return FALSE; }
    CloseHandle(writeHandle);
    /* The writable-handle transition is inside a protected, pinned directory.
       Retain the read guard for the entire job lifetime, excluding write/delete. */
    *guard=CreateFileW(path,GENERIC_READ|READ_CONTROL,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    if(*guard==INVALID_HANDLE_VALUE) { *guard=NULL; return FALSE; }
    if(!fileInfo(*guard,FALSE,&after)||!sameFile(&before,&after)||!protectedSecurity(*guard,TRUE,FALSE)||
       !finalPath(*guard,pathScratch)||!sameInsensitive(path,pathScratch)||!bytesMatch(*guard,bytes,length)) { closeHandle(guard); return FALSE; }
    return TRUE;
}
static BOOL permittedEnvironmentName(const WCHAR *s) { return same(s,L"SystemRoot")||same(s,L"WINDIR")||same(s,L"SystemDrive")||same(s,L"PATH")||same(s,L"TEMP")||same(s,L"TMP"); }
static BOOL envEntry(SIZE_T *offset,const WCHAR *name,const WCHAR *value) {
    SIZE_T n=wlen(name),m=wlen(value),end=*offset+n+m+2;
    if(!permittedEnvironmentName(name)||end+1>=sizeof(environment)/sizeof(WCHAR)||!n||!m) return FALSE;
    memcpy(environment+*offset,name,n*sizeof(WCHAR)); environment[*offset+n]=L'=';
    memcpy(environment+*offset+n+1,value,(m+1)*sizeof(WCHAR)); *offset=end; return TRUE;
}
static BOOL cleanEnvironment(void) {
    SIZE_T offset=0; WCHAR drive[3]={windowsPath[0],L':',0};
    memset(environment,0,sizeof(environment));
    return envEntry(&offset,L"PATH",systemPath)&&envEntry(&offset,L"SystemDrive",drive)&&
        envEntry(&offset,L"SystemRoot",windowsPath)&&envEntry(&offset,L"TEMP",tempPath)&&envEntry(&offset,L"TMP",tempPath)&&
        envEntry(&offset,L"WINDIR",windowsPath);
}
/* Namespace objects below are temporary and owned by this process. Neither
   global nor logon DOS maps, tokens, privileges or Windows policy are changed. */
static UNICODE_STRING objectName(const WCHAR *s) { UNICODE_STRING n; n.Length=(USHORT)(wlen(s)*2); n.MaximumLength=n.Length; n.Buffer=(PWSTR)s; return n; }
static OBJECT_ATTRIBUTES objectAttributes(UNICODE_STRING *n,HANDLE root,PVOID sd) {
    OBJECT_ATTRIBUTES a; memset(&a,0,sizeof(a)); a.Length=sizeof(a); a.ObjectName=n; a.RootDirectory=root; a.Attributes=0x40; a.SecurityDescriptor=sd; return a;
}
static BOOL rawVolume(const WCHAR *s) { static const WCHAR p[]=L"\\Device\\HarddiskVolume"; SIZE_T i=0; while(p[i]) { if(s[i]!=p[i]) return FALSE; ++i; } if(s[i]<L'0'||s[i]>L'9') return FALSE; while(s[i]>=L'0'&&s[i]<=L'9') ++i; return !s[i]; }
static HANDLE rawFile(const WCHAR *s,BOOL directory) {
    UNICODE_STRING n=objectName(s); OBJECT_ATTRIBUTES a=objectAttributes(&n,NULL,NULL); IO_STATUS_BLOCK io; HANDLE h=NULL;
    a.Attributes|=0x1000; memset(&io,0,sizeof(io));
    if(pCreateRelative(&h,GENERIC_READ|READ_CONTROL|SYNCHRONIZE,&a,&io,NULL,FILE_ATTRIBUTE_NORMAL,
        directory?FILE_SHARE_READ|FILE_SHARE_WRITE:FILE_SHARE_READ,1,0x20|0x00200000|(directory?1:0x40),NULL,0)<0) closeHandle(&h);
    return h;
}
static BOOL pinSource(void) {
    DWORD count=CAP; SIZE_T rootEnd,i,start,n; HANDLE h; BY_HANDLE_FILE_INFORMATION file;
    static const WCHAR prefix[]=L"\\Device\\HarddiskVolume";
    if(!alive()||!QueryFullProcessImageNameW(GetCurrentProcess(),1,workPath,&count)||!count||count>=CAP||
       count<=wlen(prefix)||memcmp(workPath,prefix,(wlen(prefix))*2)) return FALSE;
    rootEnd=wlen(prefix); while(workPath[rootEnd]>=L'0'&&workPath[rootEnd]<=L'9') ++rootEnd;
    if(rootEnd==wlen(prefix)||workPath[rootEnd]!=L'\\'||!copy(namespaceScratch,CAP,workPath)) return FALSE;
    sourceGuard=rawFile(namespaceScratch,FALSE);
    if(!sourceGuard||!fileInfo(sourceGuard,FALSE,&file)||!finalPath(sourceGuard,sourceCanonical)) return FALSE;
    count=GetFinalPathNameByHandleW(sourceGuard,sourceDos,CAP,VOLUME_NAME_DOS);
    if(!count||count>=CAP||sourceDos[0]!=L'\\'||sourceDos[1]!=L'\\'||sourceDos[2]!=L'?'||sourceDos[3]!=L'\\'||
       sourceDos[4]<L'A'||sourceDos[4]>L'Z'||sourceDos[5]!=L':'||sourceDos[6]!=L'\\') return FALSE;
    n=wlen(namespaceScratch); start=n; while(start&&namespaceScratch[start-1]!=L'\\') --start;
    if(!sameInsensitive(namespaceScratch+start,L"AdministratorBootstrap.exe")) return FALSE;
    { WCHAR saved=namespaceScratch[rootEnd+1]; namespaceScratch[rootEnd+1]=0; h=rawFile(namespaceScratch,TRUE); namespaceScratch[rootEnd+1]=saved; }
    if(!h||!fileInfo(h,TRUE,&file)) { closeHandle(&h); return FALSE; } sourceAncestors[sourceAncestorCount++]=h;
    i=rootEnd+1; while(i<start-1) {
        SIZE_T at=i; while(i<start-1&&namespaceScratch[i]!=L'\\') ++i;
        if(!simpleComponent(namespaceScratch+at,i-at)||i-at>=CAP||sourceAncestorCount>=MAX_ANCESTORS) return FALSE;
        memcpy(workPath,namespaceScratch+at,(i-at)*2); workPath[i-at]=0;
        h=relativeDirectory(sourceAncestors[sourceAncestorCount-1],workPath,FALSE);
        if(!h||!fileInfo(h,TRUE,&file)) { closeHandle(&h); return FALSE; } sourceAncestors[sourceAncestorCount++]=h; ++i;
    }
    return alive();
}
/* Registration grants only this protected native image and these exact MED
   controller bytes. No path, payload, command or ledger data comes from IPC. */
static BOOL registrationShape(const BYTE *bytes,DWORD length,const WCHAR *scope,PSID sid) {
    static const BYTE magic[8]={'T','S','A','P','R','0','1',0}; BYTE decoded[32]; DWORD sidLength;
    if(!bytes||length<116||length>sizeof(registrationBytes)||memcmp(bytes,magic,sizeof(magic))||
       !decodedHex(scope,64,decoded)||memcmp(bytes+8,decoded,sizeof(decoded))) return FALSE;
    sidLength=get32(bytes+104);
    return sidLength>=8&&sidLength<=SECURITY_MAX_SID_SIZE&&length==108+sidLength&&
        boundedSid((PSID)(bytes+108),bytes,length)&&pSidLength((PSID)(bytes+108))==sidLength&&
        sid&&pValidSid(sid)&&pEqualSid((PSID)(bytes+108),sid);
}
static BOOL registeredChildren(void) {
    HANDLE f; DWORD error,seen=0; BOOL ok=TRUE;
    if(!childPath(workPath,registrationFolder,L"*")) return FALSE;
    f=FindFirstFileW(workPath,&findData); if(f==INVALID_HANDLE_VALUE) return FALSE;
    do {
        DWORD which;
        if(same(findData.cFileName,L".")||same(findData.cFileName,L"..")) continue;
        if(findData.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)) { ok=FALSE; break; }
        which=same(findData.cFileName,L"AdministratorBootstrap.exe")?1:same(findData.cFileName,L"registration.bin")?2:0;
        if(!which||(seen&which)) { ok=FALSE; break; } seen|=which;
    } while(FindNextFileW(f,&findData));
    error=GetLastError(); FindClose(f); return ok&&seen==3&&error==ERROR_NO_MORE_FILES;
}
static BOOL pinControllerImage(void) {
    static const WCHAR prefix[]=L"\\Device\\HarddiskVolume";
    DWORD count=CAP; SIZE_T rootEnd,i,start,n; HANDLE h,relative=NULL; BY_HANDLE_FILE_INFORMATION first,second; ULONGLONG ticks;
    if(!alive()||!started(parent,&ticks)||ticks!=parentStarted||
       !QueryFullProcessImageNameW(parent,1,controllerKernelPath,&count)||!count||count>=CAP||
       count<=wlen(prefix)||memcmp(controllerKernelPath,prefix,wlen(prefix)*sizeof(WCHAR))) return FALSE;
    rootEnd=wlen(prefix); while(controllerKernelPath[rootEnd]>=L'0'&&controllerKernelPath[rootEnd]<=L'9') ++rootEnd;
    if(rootEnd==wlen(prefix)||controllerKernelPath[rootEnd]!=L'\\') return FALSE;
    controllerGuard=rawFile(controllerKernelPath,FALSE);
    if(!controllerGuard||!fileInfo(controllerGuard,FALSE,&first)) return FALSE;
    n=wlen(controllerKernelPath); start=n; while(start&&controllerKernelPath[start-1]!=L'\\') --start;
    if(!sameInsensitive(controllerKernelPath+start,L"TaskbarSelect.exe")) return FALSE;
    { WCHAR saved=controllerKernelPath[rootEnd+1]; controllerKernelPath[rootEnd+1]=0;
      h=rawFile(controllerKernelPath,TRUE); controllerKernelPath[rootEnd+1]=saved; }
    if(!h||!fileInfo(h,TRUE,&second)) { closeHandle(&h); return FALSE; }
    controllerAncestors[controllerAncestorCount++]=h;
    i=rootEnd+1;
    while(i<start-1) {
        SIZE_T at=i; while(i<start-1&&controllerKernelPath[i]!=L'\\') ++i;
        if(!simpleComponent(controllerKernelPath+at,i-at)||i-at>=CAP||controllerAncestorCount>=MAX_ANCESTORS) return FALSE;
        memcpy(workPath,controllerKernelPath+at,(i-at)*sizeof(WCHAR)); workPath[i-at]=0;
        h=relativeDirectory(controllerAncestors[controllerAncestorCount-1],workPath,FALSE);
        if(!h||!fileInfo(h,TRUE,&second)) { closeHandle(&h); return FALSE; }
        controllerAncestors[controllerAncestorCount++]=h; ++i;
    }
    relative=relativeReadFile(controllerAncestors[controllerAncestorCount-1],controllerKernelPath+start);
    if(!relative||!fileInfo(relative,FALSE,&second)||!sameFile(&first,&second)) { closeHandle(&relative); return FALSE; }
    closeHandle(&relative); count=CAP;
    return QueryFullProcessImageNameW(parent,1,workPath,&count)&&count&&count<CAP&&sameInsensitive(workPath,controllerKernelPath)&&
        started(parent,&ticks)&&ticks==parentStarted&&alive();
}
static BOOL scheduledAdmission(void) {
    HANDLE h; BY_HANDLE_FILE_INFORMATION info; DWORD n,extra; BYTE tail; ULONGLONG ticks;
    if(!scheduledMode||requestMode||approvalTest||selfIntegrity!=SECURITY_MANDATORY_HIGH_RID||
       !hexText(registrationScope,64,FALSE)||!alive()||!prepareCommon()||
       !childPath(registrationRoot,canonical,L"TaskbarSelect.Persistent")) return FALSE;
    h=relativeDirectory(ancestors[ancestorCount-1],L"TaskbarSelect.Persistent",FALSE);
    if(!h||!rememberDirectory(h,registrationRoot,TRUE)||!childPath(registrationFolder,registrationRoot,registrationScope)) return FALSE;
    h=relativeDirectory(ancestors[ancestorCount-1],registrationScope,FALSE);
    if(!h||!rememberDirectory(h,registrationFolder,TRUE)||!childPath(workPath,registrationFolder,L"AdministratorBootstrap.exe")||
       !sameInsensitive(workPath,sourceCanonical)||!protectedSecurity(sourceGuard,TRUE,FALSE)) return FALSE;
    registrationGuard=relativeReadFile(ancestors[ancestorCount-1],L"registration.bin");
    if(!registrationGuard||!fileInfo(registrationGuard,FALSE,&info)||info.nFileSizeHigh||info.nFileSizeLow>sizeof(registrationBytes)||
       !protectedSecurity(registrationGuard,TRUE,FALSE)||
       !ReadFile(registrationGuard,registrationBytes,sizeof(registrationBytes),&n,NULL)||n!=info.nFileSizeLow||
       !ReadFile(registrationGuard,&tail,1,&extra,NULL)||extra||!registrationShape(registrationBytes,n,registrationScope,parentSid)||
       !registeredChildren()||!fileDigestMatches(sourceGuard,registrationBytes+72,16777216)||
       !pinControllerImage()||!fileDigestMatches(controllerGuard,registrationBytes+40,67108864)) return FALSE;
    return ancestorsStable()&&fileInfo(sourceGuard,FALSE,&info)&&fileInfo(controllerGuard,FALSE,&info)&&
        started(parent,&ticks)&&ticks==parentStarted&&alive();
}
static void closeRegistration(void) {
    DWORD i; closeHandle(&registrationGuard); closeHandle(&controllerGuard);
    for(i=0;i<controllerAncestorCount;++i) closeHandle(&controllerAncestors[i]); controllerAncestorCount=0;
}
static BOOL objectDenied(HANDLE h,DWORD mask) {
    PSECURITY_DESCRIPTOR sd=NULL; BOOL denied;
    if(pSecurity(h,SE_KERNEL_OBJECT,OWNER_SECURITY_INFORMATION|GROUP_SECURITY_INFORMATION|DACL_SECURITY_INFORMATION,NULL,NULL,NULL,NULL,&sd)!=ERROR_SUCCESS) return FALSE;
    denied=accessDenied(sd,mask); if(sd) LocalFree(sd); return denied;
}
static BOOL objectProtected(HANDLE h,BOOL directory) {
    return objectDenied(h,DELETE)&&objectDenied(h,WRITE_DAC)&&objectDenied(h,WRITE_OWNER)&&
        objectDenied(h,2)&&(directory?objectDenied(h,4)&&objectDenied(h,8):TRUE);
}
static BOOL openVolumeLink(HANDLE root,const WCHAR *name,HANDLE *out,WCHAR *target) {
    UNICODE_STRING n=objectName(name),value; OBJECT_ATTRIBUTES a=objectAttributes(&n,root,NULL); ULONG received;
    if(pOpenLink(out,READ_CONTROL|1,&a)<0) return FALSE;
    value.Buffer=target; value.Length=0; value.MaximumLength=(CAP-1)*2;
    if(pQueryLink(*out,&value,&received)<0||value.Length%2||value.Length>=(CAP-1)*2) return FALSE;
    target[value.Length/2]=0; return rawVolume(target)&&objectProtected(*out,FALSE);
}
static BOOL authLuid(HANDLE t,LUID *luid) { TOKEN_STATISTICS st; DWORD n=0; if(!pTokenInfo(t,TokenStatistics,&st,sizeof(st),&n)||n!=sizeof(st)) return FALSE; *luid=st.AuthenticationId; return TRUE; }
static BOOL hexEight(WCHAR *s,DWORD value) { static const WCHAR a[]=L"0123456789abcdef"; DWORD i; for(i=0;i<8;++i) s[i]=a[(value>>(28-4*i))&15]; s[8]=0; return TRUE; }
static BOOL highMapIdentity(void) {
    HANDLE t=ownToken,linked=NULL; TOKEN_LINKED_TOKEN link; DWORD n,session,ownSession; TOKEN_ELEVATION elevation; LUID luid; WCHAR part[16]; BOOL ok=FALSE;
    if(requestMode||approvalTest) {
        if(!pTokenInfo(ownToken,TokenLinkedToken,&link,sizeof(link),&n)||n!=sizeof(link)) return FALSE; linked=link.LinkedToken; t=linked;
        if(!pTokenInfo(t,TokenUser,infoBuffer.bytes,sizeof(infoBuffer.bytes),&n)||n>sizeof(infoBuffer.bytes)||n<sizeof(TOKEN_USER)||
           !boundedSid(((TOKEN_USER*)infoBuffer.bytes)->User.Sid,infoBuffer.bytes,n)||!pEqualSid(selfSid,((TOKEN_USER*)infoBuffer.bytes)->User.Sid)) goto done;
        if(!pTokenInfo(t,TokenIntegrityLevel,infoBuffer.bytes,sizeof(infoBuffer.bytes),&n)||n>sizeof(infoBuffer.bytes)||n<sizeof(TOKEN_MANDATORY_LABEL)||
           !boundedSid(((TOKEN_MANDATORY_LABEL*)infoBuffer.bytes)->Label.Sid,infoBuffer.bytes,n)||
           ((BYTE*)((TOKEN_MANDATORY_LABEL*)infoBuffer.bytes)->Label.Sid)[1]!=1||
           *(DWORD*)((BYTE*)((TOKEN_MANDATORY_LABEL*)infoBuffer.bytes)->Label.Sid+8)!=SECURITY_MANDATORY_HIGH_RID) goto done;
        if(!pTokenInfo(t,TokenElevation,&elevation,sizeof(elevation),&n)||n!=sizeof(elevation)||!elevation.TokenIsElevated) goto done;
        if(!pTokenInfo(t,TokenSessionId,&session,sizeof(session),&n)||n!=sizeof(session)||!ProcessIdToSessionId(selfPid,&ownSession)||
            !linkedHighSessionMatches(session,ownSession)) goto done;
    }
    if(!authLuid(t,&luid)||!copy(highObjectPath,512,L"\\Sessions\\0\\DosDevices\\")) goto done;
    hexEight(part,(DWORD)luid.HighPart); if(!append(highObjectPath,512,part)||!append(highObjectPath,512,L"-") ) goto done;
    hexEight(part,luid.LowPart); if(!append(highObjectPath,512,part)) goto done; ok=TRUE;
done: closeHandle(&linked); return ok;
}
static BOOL highDirectoryDenied(void) {
    UNICODE_STRING n=objectName(highObjectPath); OBJECT_ATTRIBUTES a=objectAttributes(&n,NULL,NULL); HANDLE h=NULL;
    static const DWORD denied[]={4,8,DELETE,WRITE_DAC,WRITE_OWNER}; DWORD i;
    if(requestMode||approvalTest) {
        /* Use actual MED opens: its linked HIGH directory normally cannot be
           queried by MED, but all mutation opens must be explicitly denied. */
        for(i=0;i<sizeof(denied)/sizeof(denied[0]);++i) { NTSTATUS s=pOpenObjectDirectory(&h,denied[i],&a); closeHandle(&h); if((ULONG)s!=0xc0000022) return FALSE; }
        return TRUE;
    }
    if(pOpenObjectDirectory(&highNamespace,READ_CONTROL|3,&a)<0) return FALSE;
    return objectDenied(highNamespace,4)&&objectDenied(highNamespace,8)&&objectDenied(highNamespace,DELETE)&&
        objectDenied(highNamespace,WRITE_DAC)&&objectDenied(highNamespace,WRITE_OWNER);
}
static BOOL namespaceAdmission(WCHAR drive,WCHAR *physicalTarget) {
    HANDLE globalLink=NULL,highLink=NULL,effective=NULL; WCHAR d[3]={drive,L':',0}; static WCHAR globalTarget[CAP],highTarget[CAP];
    BOOL ok=FALSE; SIZE_T len;
    if(namespaceLinkCount+2>sizeof(namespaceLinks)/sizeof(namespaceLinks[0])||!openVolumeLink(globalNamespace,d,&globalLink,globalTarget)) goto done;
    if(!copy(namespaceScratch,CAP,highObjectPath)||!append(namespaceScratch,CAP,L"\\")||!append(namespaceScratch,CAP,d)||
       !openVolumeLink(NULL,namespaceScratch,&highLink,highTarget)||!same(globalTarget,highTarget)) goto done;
    if(!(requestMode||approvalTest)) {
        if(!copy(namespaceScratch,CAP,L"\\??\\")||!append(namespaceScratch,CAP,d)||!openVolumeLink(NULL,namespaceScratch,&effective,highTarget)||!same(globalTarget,highTarget)) goto done;
    }
    if(!copy(physicalTarget,CAP,globalTarget)) goto done;
    if(drive==systemPath[0]) {
        typedef DWORD (WINAPI *MappedNameFn)(HANDLE,LPVOID,LPWSTR,DWORD);
        HMODULE kernel=GetModuleHandleW(L"kernel32.dll"); MappedNameFn mapped; static WCHAR expected[CAP]; DWORD count;
        mapped=kernel?(MappedNameFn)(void*)GetProcAddress(kernel,"K32GetMappedFileNameW"):NULL;
        /* Metadata of the already loaded KnownDLL identifies the OS volume;
           no DOS/BootDevice alias chain selects this initial module. */
        if(!mapped||!GetWindowsDirectoryW(windowsPath,CAP)||windowsPath[0]!=drive||windowsPath[1]!=L':'||windowsPath[2]!=L'\\'||
           !copy(expected,CAP,globalTarget)||!append(expected,CAP,windowsPath+2)||!append(expected,CAP,L"\\System32\\kernel32.dll")) goto done;
        count=mapped(GetCurrentProcess(),kernel,namespaceScratch,CAP);
        if(!count||count>=CAP||!sameInsensitive(expected,namespaceScratch)) goto done;
    }
    /* The raw GLOBAL target must be the actual held source/system volume. */
    if(drive==sourceDos[4]) { DWORD count=CAP; if(!QueryFullProcessImageNameW(GetCurrentProcess(),1,namespaceScratch,&count)) goto done;
        len=wlen(globalTarget); if(memcmp(namespaceScratch,globalTarget,len*2)||namespaceScratch[len]!=L'\\') goto done; }
    namespaceLinks[namespaceLinkCount++]=globalLink; globalLink=NULL; namespaceLinks[namespaceLinkCount++]=highLink; highLink=NULL; ok=TRUE;
done: closeHandle(&globalLink); closeHandle(&highLink); closeHandle(&effective); return ok;
}
static BOOL addMapAce(BYTE *acl,DWORD *used,BYTE type,DWORD mask,PSID sid) {
    DWORD n=pSidLength(sid),size=8+n; ACCESS_ALLOWED_ACE *ace; if(size>512-*used) return FALSE;
    ace=(ACCESS_ALLOWED_ACE*)(acl+*used); ace->Header.AceType=type; ace->Header.AceFlags=0; ace->Header.AceSize=(WORD)size; ace->Mask=mask;
    memcpy(&ace->SidStart,sid,n); *used+=size; ((ACL*)acl)->AceCount++; ((ACL*)acl)->AclSize=(WORD)*used; return TRUE;
}
static BOOL mapDescriptor(SECURITY_DESCRIPTOR *sd,BYTE *acl,BOOL sealed,BOOL directory) {
    static BYTE ownerRights[]={1,1,0,0,0,0,0,3,4,0,0,0}; DWORD used=sizeof(ACL),all=directory?0xF000F:0xF0001;
    memset(sd,0,sizeof(*sd)); memset(acl,0,512); sd->Revision=SECURITY_DESCRIPTOR_REVISION; sd->Control=SE_DACL_PRESENT|SE_DACL_PROTECTED;
    sd->Owner=selfSid; sd->Group=usersSid; sd->Dacl=(PACL)acl; ((ACL*)acl)->AclRevision=ACL_REVISION; ((ACL*)acl)->AclSize=sizeof(ACL);
    if(sealed&&(!addMapAce(acl,&used,ACCESS_DENIED_ACE_TYPE,directory?0xD000C:0xD0002,ownerRights)||
       !addMapAce(acl,&used,ACCESS_DENIED_ACE_TYPE,directory?0xD000C:0xD0002,selfSid))) return FALSE;
    return addMapAce(acl,&used,ACCESS_ALLOWED_ACE_TYPE,sealed?(directory?0x20003:0x20001):all,selfSid)&&
        addMapAce(acl,&used,ACCESS_ALLOWED_ACE_TYPE,all,systemSid)&&addMapAce(acl,&used,ACCESS_ALLOWED_ACE_TYPE,all,adminSid);
}
static BOOL effectivePrivate(WCHAR drive) {
    HANDLE link=NULL; UNICODE_STRING n,*actual; OBJECT_ATTRIBUTES a; ULONG length; WCHAR d[3]={drive,L':',0}; BOOL ok=FALSE;
    if(!copy(namespaceScratch,CAP,L"\\??\\")||!append(namespaceScratch,CAP,d)) return FALSE;
    n=objectName(namespaceScratch); a=objectAttributes(&n,NULL,NULL); if(pOpenLink(&link,READ_CONTROL|1,&a)<0) return FALSE;
    if(pQueryObject(link,1,infoBuffer.bytes,sizeof(infoBuffer.bytes),&length)<0) goto done;
    actual=(UNICODE_STRING*)infoBuffer.bytes;
    if(actual->Length%2||!inside(actual->Buffer,actual->Length+2,infoBuffer.bytes,sizeof(infoBuffer.bytes))) goto done;
    actual->Buffer[actual->Length/2]=0;
    if(copy(namespaceScratch,CAP,privateObjectPath)&&append(namespaceScratch,CAP,L"\\")&&append(namespaceScratch,CAP,d)) ok=same(actual->Buffer,namespaceScratch);
done: closeHandle(&link); return ok;
}
static BOOL prepareNamespace(void) {
    static BYTE initialAcl[512],dirAcl[512],linkAcl[512]; SECURITY_DESCRIPTOR initial,dirSd,linkSd;
    HANDLE writable=NULL; UNICODE_STRING n,target; OBJECT_ATTRIBUTES a; WCHAR part[32],drive[3]; static WCHAR globalTarget[CAP]; DWORD session,i,count=1; BOOL ok=FALSE;
    if(!pinSource()||!highMapIdentity()||!highDirectoryDenied()) return FALSE;
    n=objectName(L"\\GLOBAL??"); a=objectAttributes(&n,NULL,NULL);
    if(pOpenObjectDirectory(&globalNamespace,READ_CONTROL|3,&a)<0||!objectDenied(globalNamespace,4)||!objectDenied(globalNamespace,8)||
       !objectDenied(globalNamespace,DELETE)||!objectDenied(globalNamespace,WRITE_DAC)||!objectDenied(globalNamespace,WRITE_OWNER)) return FALSE;
    if(!GetSystemDirectoryW(systemPath,CAP)||systemPath[1]!=L':'||systemPath[2]!=L'\\'||systemPath[0]<L'A'||systemPath[0]>L'Z') return FALSE;
    if(sourceDos[4]!=systemPath[0]) count=2;
    if(!mapDescriptor(&initial,initialAcl,FALSE,TRUE)||!mapDescriptor(&dirSd,dirAcl,TRUE,TRUE)||!mapDescriptor(&linkSd,linkAcl,TRUE,FALSE)||
       !ProcessIdToSessionId(selfPid,&session)||!copy(privateObjectPath,512,L"\\Sessions\\")||!digits(part,32,session)||!append(privateObjectPath,512,part)||
       !append(privateObjectPath,512,(requestMode||approvalTest)?L"\\BaseNamedObjects\\TaskbarSelect.AdminApproval.Medium.":L"\\BaseNamedObjects\\TaskbarSelect.AdminApproval.High.")||!append(privateObjectPath,512,token)) goto done;
    n=objectName(privateObjectPath); a=objectAttributes(&n,NULL,&initial);
    if(pCreateObjectDirectory(&writable,0xF000F,&a)<0) goto done;
    for(i=0;i<count;++i) {
        HANDLE mutableLink=NULL,readLink=NULL; drive[0]=i?sourceDos[4]:systemPath[0]; drive[1]=L':'; drive[2]=0;
        if(!namespaceAdmission(drive[0],globalTarget)) goto done;
        n=objectName(drive); target=objectName(globalTarget); a=objectAttributes(&n,writable,&initial);
        if(pCreateLink(&mutableLink,0xF0001,&a,&target)<0) goto done;
        a=objectAttributes(&n,writable,NULL);
        if(pOpenLink(&readLink,READ_CONTROL|1,&a)<0||pSetObjectSecurity(mutableLink,DACL_SECURITY_INFORMATION|PROTECTED_DACL_SECURITY_INFORMATION,&linkSd)<0) {
            closeHandle(&mutableLink); closeHandle(&readLink); goto done; }
        closeHandle(&mutableLink); /* Directory retains name, keep a read-only handle. */
        if(namespaceLinkCount>=sizeof(namespaceLinks)/sizeof(namespaceLinks[0])) { closeHandle(&readLink); goto done; }
        namespaceLinks[namespaceLinkCount++]=readLink;
    }
    n=objectName(privateObjectPath); a=objectAttributes(&n,NULL,NULL);
    if(pOpenObjectDirectory(&privateDirectory,READ_CONTROL|3,&a)<0||pSetObjectSecurity(writable,DACL_SECURITY_INFORMATION|PROTECTED_DACL_SECURITY_INFORMATION,&dirSd)<0) goto done;
    closeHandle(&writable);
    if(!objectDenied(privateDirectory,4)||!objectDenied(privateDirectory,8)||!objectDenied(privateDirectory,DELETE)||
       !objectDenied(privateDirectory,WRITE_DAC)||!objectDenied(privateDirectory,WRITE_OWNER)||
       pSetProcess(GetCurrentProcess(),23,&privateDirectory,sizeof(privateDirectory))<0||!effectivePrivate(systemPath[0])||!effectivePrivate(sourceDos[4])) goto done;
    { HANDLE file=CreateFileW(sourceDos,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL); BY_HANDLE_FILE_INFORMATION x,y;
      if(file==INVALID_HANDLE_VALUE) goto done;
      ok=fileInfo(file,FALSE,&x)&&fileInfo(sourceGuard,FALSE,&y)&&sameFile(&x,&y); CloseHandle(file); }
done: closeHandle(&writable); return ok&&alive();
}
static BOOL createChild(ULONGLONG *childStarted) {
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits; STARTUPINFOW si; PROCESS_INFORMATION pi; WCHAR n[32]; DWORD count; HANDLE check=NULL; BY_HANDLE_FILE_INFORMATION x,y;
    memset(&limits,0,sizeof(limits)); limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    job=CreateJobObjectW(NULL,NULL); if(!job||!SetInformationJobObject(job,JobObjectExtendedLimitInformation,&limits,sizeof(limits))) return FALSE;
    count=GetFinalPathNameByHandleW(payloadGuard,payloadLaunchPath,CAP,VOLUME_NAME_DOS);
    if(!count||count>=CAP||payloadLaunchPath[0]!=L'\\'||payloadLaunchPath[1]!=L'\\'||payloadLaunchPath[2]!=L'?'||payloadLaunchPath[3]!=L'\\'||
       payloadLaunchPath[4]!=systemPath[0]||payloadLaunchPath[5]!=L':'||!effectivePrivate(systemPath[0])) return FALSE;
    check=CreateFileW(payloadLaunchPath,GENERIC_READ|READ_CONTROL,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    if(check==INVALID_HANDLE_VALUE) return FALSE;
    if(!fileInfo(check,FALSE,&x)||!fileInfo(payloadGuard,FALSE,&y)||!sameFile(&x,&y)||!finalPath(check,pathScratch)||!sameInsensitive(pathScratch,payloadPath)) { CloseHandle(check); return FALSE; }
    CloseHandle(check);
    /* Framework 4 cannot read Assembly.Location when its executable spelling
       contains the extended prefix. The immutable private drive and held file
       above prove this ordinary DOS spelling before managed code is admitted. */
    if(!copy(launchArguments,512,L"\"")||!append(launchArguments,512,payloadLaunchPath+4)||!append(launchArguments,512,L"\" --administrator-helper ")||
       !append(launchArguments,512,token)||!append(launchArguments,512,L" ")||!digits(n,32,parentPid)||!append(launchArguments,512,n)||
       !append(launchArguments,512,L" ")||!digits(n,32,parentStarted)||!append(launchArguments,512,n)||!cleanEnvironment()) return FALSE;
    memset(&si,0,sizeof(si)); si.cb=sizeof(si); memset(&pi,0,sizeof(pi));
    if(!CreateProcessW(payloadLaunchPath+4,launchArguments,NULL,NULL,FALSE,CREATE_SUSPENDED|CREATE_UNICODE_ENVIRONMENT|CREATE_NO_WINDOW,
        environment,systemPath,&si,&pi)) return FALSE;
    payload=pi.hProcess; payloadThread=pi.hThread; childCreated=TRUE;
    /* CreateProcess does not inherit the parent's private device map. Bind
       this still-suspended child before Main can grant its CLR resume. */
    if(!AssignProcessToJobObject(job,payload)||pSetProcess(payload,23,&privateDirectory,sizeof(privateDirectory))<0||!started(payload,childStarted)) {
        TerminateProcess(payload,E_JOB); WaitForSingleObject(payload,DRAIN_MS); return FALSE;
    } return TRUE;
}
static BOOL jobCount(DWORD *remaining) { JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info; if(!job) { *remaining=0; return !childCreated; }
    if(!QueryInformationJobObject(job,JobObjectBasicAccountingInformation,&info,sizeof(info),NULL)) return FALSE; *remaining=info.ActiveProcesses; return TRUE; }
static BOOL drain(DWORD *remaining) {
    ULONGLONG end; drainBegan=GetTickCount64(); end=drainBegan+DRAIN_MS;
    stopRecord.phase=DRAIN_INITIAL;
    if(payload) { DWORD exit; if(GetExitCodeProcess(payload,&exit)) childExit=exit; }
    stopRecord.payloadBeforeDrain=childExit;
    stopRecord.phase=DRAIN_TERMINATE_JOB;
    if(job&&!TerminateJobObject(job,E_PROTOCOL)) { stopRecord.error=GetLastError(); stopRecord.errorPhase=DRAIN_TERMINATE_JOB; return FALSE; }
    /* If assignment itself failed, the new suspended child belongs to no job. */
    stopRecord.phase=DRAIN_TERMINATE_PAYLOAD;
    if(payload&&WaitForSingleObject(payload,0)!=WAIT_OBJECT_0&&!TerminateProcess(payload,E_PROTOCOL)) {
        stopRecord.error=GetLastError(); stopRecord.errorPhase=DRAIN_TERMINATE_PAYLOAD;
    }
    do { stopRecord.phase=DRAIN_QUERY_JOB;
        if(!jobCount(remaining)) { stopRecord.error=GetLastError(); stopRecord.errorPhase=DRAIN_QUERY_JOB; return FALSE; }
        stopRecord.active=*remaining;
        if(!*remaining&&(!payload||WaitForSingleObject(payload,0)==WAIT_OBJECT_0)) {
            if(payload) GetExitCodeProcess(payload,&childExit);
            stopRecord.phase=DRAIN_CONFIRMED; stopRecord.flags|=2; return TRUE; }
        stopRecord.phase=DRAIN_WAIT;
        Sleep(25);
    } while(GetTickCount64()<end);
    stopRecord.phase=DRAIN_TIMEOUT;
    if(!stopRecord.error) { stopRecord.error=ERROR_TIMEOUT; stopRecord.errorPhase=DRAIN_TIMEOUT; }
    return FALSE;
}
static BOOL emptyDirectory(const WCHAR *path) {
    HANDLE f; DWORD error; BOOL empty=TRUE; if(!childPath(workPath,path,L"*")) return FALSE;
    f=FindFirstFileW(workPath,&findData); if(f==INVALID_HANDLE_VALUE) return GetLastError()==ERROR_FILE_NOT_FOUND;
    do { if(!same(findData.cFileName,L".")&&!same(findData.cFileName,L"..")) { empty=FALSE; break; } } while(FindNextFileW(f,&findData));
    error=GetLastError(); FindClose(f); return empty&&error==ERROR_NO_MORE_FILES;
}
static void cleanupCache(void) {
    /* No recursive delete. An unexpected runtime temp entry leaves a protected
       session behind for explicit verified cleanup rather than widening scope. */
    DWORD i; closeHandle(&payloadGuard); closeHandle(&configGuard);
    for(i=ancestorCount;i>0;--i) closeHandle(&ancestors[i-1]); ancestorCount=0;
    if(!sessionOwned) return;
    if(payloadPath[0]) DeleteFileW(payloadPath); if(configPath[0]) DeleteFileW(configPath);
    if(tempPath[0]&&emptyDirectory(tempPath)) RemoveDirectoryW(tempPath);
    if(emptyDirectory(cacheSession)) RemoveDirectoryW(cacheSession);
}
static DWORD run(void) {
    ULONGLONG values[4],childStarted=0; DWORD code=E_PIPE,remaining=0xffffffff; BOOL drained=FALSE;
    memset(&stopRecord,0,sizeof(stopRecord)); stopRecord.reason=STOP_SETUP; stopRecord.active=0xffffffff;
    stopRecord.payloadExit=stopRecord.payloadBeforeDrain=STILL_ACTIVE; runBegan=GetTickCount64();
    if(!connectPipe()) goto done;
    values[0]=parentPid; values[1]=parentStarted; values[2]=selfPid; values[3]=selfStarted;
    if(!sendPacket(20,values,4)||!receiveGrant(21)) { code=E_GRANT; goto done; }
    if(!prepareCache()) { code=E_CACHE; goto done; }
    if(AdministratorPayloadLength<512||AdministratorPayloadLength>67108864||!fixedConfig()||!digestMatches(AdministratorPayloadBytes,AdministratorPayloadLength)||
       !createVerifiedFile(payloadPath,AdministratorPayloadBytes,AdministratorPayloadLength,&payloadGuard)||
       !createVerifiedFile(configPath,AdministratorConfigBytes,AdministratorConfigLength,&configGuard)) { code=E_PAYLOAD; goto done; }
    if(!trustedPipe()||!createChild(&childStarted)) { code=E_JOB; goto done; }
    values[0]=GetProcessId(payload); values[1]=childStarted;
    if(!sendPacket(22,values,2)||!receiveGrant(23)||!trustedPipe()||ResumeThread(payloadThread)==(DWORD)-1) { code=E_RESUME; goto done; }
    closeHandle(&payloadThread); code=0;
    for(;;) {
        DWORD available=0,wait;
        if(!trustedPipe()) { stopRecord.reason=STOP_PIPE_IDENTITY; break; }
        if(!PeekNamedPipe(pipeHandle,NULL,0,NULL,&available,NULL)) {
            stopRecord.reason=STOP_PIPE_PEEK; stopRecord.error=GetLastError(); break;
        }
        if(available) { stopRecord.reason=STOP_PIPE_INPUT; break; }
        wait=WaitForSingleObject(payload,50);
        if(wait==WAIT_OBJECT_0) { stopRecord.reason=STOP_PAYLOAD_EXIT; break; }
        if(wait==WAIT_FAILED) { stopRecord.reason=STOP_PAYLOAD_WAIT; stopRecord.error=GetLastError(); break; }
    }
done:
    stopRecord.stopCode=code;
    drained=drain(&remaining);
    if(drained) {
        if(childCreated&&trustedPipe()) { values[0]=(ULONGLONG)(LONGLONG)(LONG)childExit; values[1]=remaining; sendPacket(24,values,2); }
        cleanupCache();
        /* Documented 100..108 refusals prove no payload or worker was created.
           Once any child existed, only zero proves descendant quiescence;
           an unconfirmed drain exits 90, never a pre-child refusal code. */
        if(childCreated) code=0;
    } else { code=E_NOT_DRAINED; writeFailureDiagnostic(code); }
    closeHandle(&payloadThread); closeHandle(&payload); closeHandle(&job); closeHandle(&pipeHandle);
    closeHandle(&parentAccess); closeHandle(&parentToken); closeHandle(&parent); return code;
}
static BOOL approvedImage(HANDLE process,ULONGLONG *created) {
    DWORD count=CAP,pid; HANDLE file=NULL; BY_HANDLE_FILE_INFORMATION x,y; BOOL ok=FALSE;
    pid=GetProcessId(process);
    if(!pid||!started(process,created)||*created<selfStarted||
       !QueryFullProcessImageNameW(process,1,workPath,&count)||!count||count>=CAP) return FALSE;
    if(!copy(namespaceScratch,CAP,workPath)) return FALSE;
    file=rawFile(namespaceScratch,FALSE);
    if(file&&fileInfo(file,FALSE,&x)&&fileInfo(sourceGuard,FALSE,&y)&&sameFile(&x,&y)&&finalPath(file,pathScratch)&&sameInsensitive(sourceCanonical,pathScratch)) ok=TRUE;
    closeHandle(&file); return ok;
}
static BOOL admittedHigh(HANDLE process) {
    DWORD session,ownSession,integrity,pid=GetProcessId(process); BOOL elevated; BYTE sid[SECURITY_MAX_SID_SIZE];
    return pid&&ProcessIdToSessionId(pid,&session)&&ProcessIdToSessionId(selfPid,&ownSession)&&actualProcessSessionMatches(session,ownSession)&&
        tokenMetadata(process,sid,&integrity,&elevated,NULL)&&pEqualSid(sid,selfSid)&&elevated&&integrity==SECURITY_MANDATORY_HIGH_RID;
}
static DWORD requestAdministrator(void) {
    SHELLEXECUTEINFOW si; ULONGLONG values[4],created=0; DWORD error,exitCode=STILL_ACTIVE,code=E_APPROVAL_REFUSED;
    BOOL verified=FALSE,admitted=FALSE; HANDLE high=NULL;
    if(!approvalTest) {
        if(!connectPipe()) goto done;
        values[0]=parentPid; values[1]=parentStarted; values[2]=selfPid; values[3]=selfStarted;
        if(!sendPacket(30,values,4)||!receiveGrant(31)) goto done;
    }
    if(!alive()||!effectivePrivate(systemPath[0])||!effectivePrivate(sourceDos[4])) goto done;
    memset(&si,0,sizeof(si)); si.cbSize=sizeof(si); si.fMask=SEE_MASK_NOCLOSEPROCESS|SEE_MASK_NOASYNC|SEE_MASK_FLAG_NO_UI;
    si.lpFile=sourceDos; si.lpDirectory=systemPath; si.nShow=SW_HIDE; si.lpVerb=approvalTest?NULL:L"runas";
    if(approvalTest) si.lpParameters=L"--bootstrap-self-test";
    else {
        WCHAR n[32];
        if(!copy(launchArguments,512,L"--administrator-bootstrap ")||!append(launchArguments,512,token)||!append(launchArguments,512,L" ")||
           !digits(n,32,parentPid)||!append(launchArguments,512,n)||!append(launchArguments,512,L" ")||!digits(n,32,parentStarted)||!append(launchArguments,512,n)) goto done;
        si.lpParameters=launchArguments;
    }
    { BOOL launched=pShellExecute(&si); error=GetLastError(); high=si.hProcess;
      /* Even a failed Shell call with a handle may have created a process.
         Only a cancelled request with no handle proves pre-HIGH refusal. */
      if(!high) { code=!approvalTest&&!launched&&error==ERROR_CANCELLED?E_UAC_CANCELLED:E_NOT_DRAINED; goto done; }
      code=E_NOT_DRAINED;
      verified=launched&&approvedImage(high,&created); admitted=verified&&!approvalTest&&admittedHigh(high);
    }
    if(admitted&&trustedPipe()) {
        values[0]=GetProcessId(high); values[1]=created;
        if(!sendPacket(32,values,2)||!receiveGrant(33)) closeHandle(&pipeHandle);
    } else if(!approvalTest) closeHandle(&pipeHandle);
    /* Never abandon a possibly authorized HIGH process or release the source
       leases because Main exits, Disable closes its pipe, or UAC returned late.
       HIGH independently observes the original Main/bootstrap-pipe lifetime. */
    while(WaitForSingleObject(high,100)!=WAIT_OBJECT_0) {
        if(!approvalTest) { DWORD available=0; if(!trustedPipe()||!PeekNamedPipe(pipeHandle,NULL,0,NULL,&available,NULL)||available) closeHandle(&pipeHandle); }
    }
    if(GetExitCodeProcess(high,&exitCode)&&verified&&(approvalTest?exitCode==0:(exitCode==0||(exitCode>=E_ARGS&&exitCode<=E_RESUME)))) code=0;
done: closeHandle(&high); closeHandle(&pipeHandle); return code;
}
static void closeNamespace(void) {
    DWORD i; closeHandle(&sourceGuard);
    for(i=sourceAncestorCount;i>0;--i) closeHandle(&sourceAncestors[i-1]); sourceAncestorCount=0;
    for(i=0;i<namespaceLinkCount;++i) closeHandle(&namespaceLinks[i]); namespaceLinkCount=0;
    closeHandle(&globalNamespace); closeHandle(&highNamespace); closeHandle(&privateDirectory);
}
static BOOL securityFixture(const WCHAR *sddl,BOOL expected) {
    PSECURITY_DESCRIPTOR sd=NULL; BOOL answer;
    if(!pSddl(sddl,SDDL_REVISION_1,&sd,NULL)) return FALSE;
    answer=securityPolicy(sd,TRUE,TRUE,FALSE); LocalFree(sd); return answer==expected;
}
static BOOL registrationFixtures(void) {
    static const BYTE magic[8]={'T','S','A','P','R','0','1',0};
    static const WCHAR scope[]=L"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    BYTE bytes[sizeof(registrationBytes)]; DWORD sidLength=pSidLength(systemSid),length=108+sidLength; BOOL ok;
    memset(bytes,0,sizeof(bytes)); memcpy(bytes,magic,sizeof(magic));
    if(!decodedHex(scope,64,bytes+8)) return FALSE;
    put32(bytes+104,sidLength); memcpy(bytes+108,systemSid,sidLength);
    ok=registrationShape(bytes,length,scope,systemSid)&&!registrationShape(bytes,length-1,scope,systemSid)&&
        !registrationShape(bytes,length+1,scope,systemSid)&&!registrationShape(bytes,length,scope,usersSid)&&
        !registrationShape(bytes,length,L"1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",systemSid);
    bytes[0]='X'; ok=ok&&!registrationShape(bytes,length,scope,systemSid); bytes[0]=magic[0];
    bytes[108]=0; ok=ok&&!registrationShape(bytes,length,scope,systemSid); bytes[108]=SID_REVISION;
    put32(bytes+104,SECURITY_MAX_SID_SIZE+1); ok=ok&&!registrationShape(bytes,length,scope,systemSid);
    put32(bytes+104,sidLength+1); ok=ok&&!registrationShape(bytes,length,scope,systemSid);
    return ok;
}
static BOOL diagnosticFixtures(void) {
    NativeStopRecord record; BYTE bytes[DIAGNOSTIC_BYTES]; DWORD i;
    static const WCHAR nonce[]=L"0123456789abcdef0123456789abcdef";
    static const WCHAR hash[]=L"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    memset(&record,0,sizeof(record)); record.reason=STOP_IO_CANCEL; record.phase=DRAIN_IO_CANCEL;
    record.error=ERROR_TIMEOUT; record.active=0xffffffff; record.payloadWait=WAIT_TIMEOUT;
    record.payloadExit=STILL_ACTIVE; record.ioWait=WAIT_FAILED; record.ioCancelError=ERROR_NOT_FOUND;
    record.result=E_NOT_DRAINED; record.drainElapsed=1234; record.runElapsed=9876543210ULL;
    record.flags=1; record.payloadBeforeDrain=STILL_ACTIVE; record.ioCancelWait=WAIT_TIMEOUT;
    record.errorPhase=DRAIN_IO_CANCEL; record.stopCode=E_RESUME;
    if(!formatStopDiagnostic(bytes,&record,nonce,hash,17,638959000000000000ULL,23,638958999000000000ULL)||
       memcmp(bytes,"TSAND01",7)||bytes[7]||get32(bytes+8)!=DIAGNOSTIC_BYTES||get32(bytes+12)!=STOP_IO_CANCEL||
       get32(bytes+16)!=DRAIN_IO_CANCEL||get32(bytes+20)!=ERROR_TIMEOUT||get32(bytes+24)!=0xffffffff||
       get32(bytes+28)!=WAIT_TIMEOUT||get32(bytes+32)!=STILL_ACTIVE||get32(bytes+36)!=WAIT_FAILED||
       get32(bytes+40)!=ERROR_NOT_FOUND||get32(bytes+44)!=E_NOT_DRAINED||get64(bytes+48)!=1234||
       get64(bytes+56)!=9876543210ULL||get32(bytes+64)!=17||get32(bytes+68)!=23||
       get64(bytes+72)!=638959000000000000ULL||get64(bytes+80)!=638958999000000000ULL||
       get32(bytes+136)!=1||get32(bytes+140)!=STILL_ACTIVE||get32(bytes+144)!=WAIT_TIMEOUT||
       get32(bytes+148)!=DRAIN_IO_CANCEL||get32(bytes+152)!=E_RESUME||get32(bytes+156)) return FALSE;
    for(i=0;i<16;++i) if(bytes[88+i]!=(BYTE)((i%8)*0x22+1)) return FALSE;
    for(i=0;i<32;++i) if(bytes[104+i]!=(BYTE)((i%8)*0x22+1)) return FALSE;
    return !formatStopDiagnostic(bytes,&record,L"00000000000000000000000000000000",hash,17,1,23,1)&&
        !formatStopDiagnostic(bytes,&record,L"0123456789ABCDEF0123456789abcdef",hash,17,1,23,1)&&
        !formatStopDiagnostic(bytes,&record,nonce,L"bad",17,1,23,1);
}
static BOOL pureSelfTest(void) {
    BOOL test; ULONGLONG n; BYTE b[8]; SIZE_T offset=0; DWORD pid=parentPid; ULONGLONG ticks=parentStarted;
    BOOL ok=hexText(L"0123456789abcdef0123456789abcdef",32,TRUE)&&!hexText(L"00000000000000000000000000000000",32,TRUE)&&
        !hexText(L"0123456789abcdeg0123456789abcdef",32,TRUE)&&number(L"4294967295",0xffffffffULL,&n)&&n==0xffffffffULL&&
        !number(L"4294967296",0xffffffffULL,&n)&&!number(L"-1",DOTNET_MAX,&n)&&!number(L"01",DOTNET_MAX,&n)&&!number(L"0",DOTNET_MAX,&n)&&
        packetShape(16,21,1,21,1)&&!packetShape(24,21,1,21,1)&&!packetShape(16,23,1,21,1)&&!packetShape(16,21,2,21,1)&&
        simpleComponent(L"ProgramData",11)&&!simpleComponent(L"..",2)&&!simpleComponent(L"other:ads",9)&&!simpleComponent(L"link\\x",6)&&
        permittedEnvironmentName(L"PATH")&&!permittedEnvironmentName(L"COR_ENABLE_PROFILING")&&!permittedEnvironmentName(L"COMPLUS_ApplicationMigrationRuntimeActivationConfigPath")&&
        !permittedEnvironmentName(L"APPDOMAIN_MANAGER_ASM")&&!permittedEnvironmentName(L"DOTNET_STARTUP_HOOKS");
    put64(b,0x8877665544332211ULL); ok=ok&&get64(b)==0x8877665544332211ULL; put32(b,0x87654321); ok=ok&&get32(b)==0x87654321;
    ok=ok&&linkedHighSessionMatches(0,0)&&linkedHighSessionMatches(0,2)&&linkedHighSessionMatches(2,2)&&
        !linkedHighSessionMatches(1,2)&&!linkedHighSessionMatches(3,2)&&!linkedHighSessionMatches(0xffffffffUL,2)&&
        actualProcessSessionMatches(2,2)&&!actualProcessSessionMatches(2,0)&&!actualProcessSessionMatches(0,2);
    ok=ok&&parse(L"\"C:\\a b\\bootstrap.exe\" --bootstrap-self-test",&test)&&test&&
        !parse(L"bootstrap.exe --bootstrap-self-test extra",&test)&&!parse(L"bootstrap.exe --administrator-bootstrap abc 1 638959000000000000",&test);
#ifndef TASKBARSELECT_SCHEDULED_ONLY
    ok=ok&&
        parse(L"bootstrap.exe --administrator-bootstrap 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&!test&&parentPid==123&&parentStarted==638959000000000000ULL&&
        !parse(L"bootstrap.exe --administrator-bootstrap 0123456789abcdef0123456789abcdef 123 638959000000000000 extra",&test)&&
        !parse(L"bootstrap.exe --administrator-bootstrap 0123456789abcdef0123456789abcdef 0 638959000000000000",&test)&&
        !parse(L"bootstrap.exe --administrator-bootstrap 0123456789abcdef0123456789abcdef 123 3155378976000000000",&test);
    ok=ok&&parse(L"bootstrap.exe --request-administrator 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&requestMode&&!approvalTest&&!test&&
        !parse(L"bootstrap.exe --request-administrator 0123456789abcdef0123456789abcdef 123 638959000000000000 extra",&test)&&
        parse(L"bootstrap.exe --approval-self-test 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&approvalTest&&!requestMode&&!test&&
        !parse(L"bootstrap.exe --approval-self-test",&test)&&
        !parse(L"bootstrap.exe --approval-self-test 0123456789abcdef0123456789abcdef 123 638959000000000000 extra",&test);
#else
    ok=ok&&!parse(L"bootstrap.exe --administrator-bootstrap 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&
        !parse(L"bootstrap.exe --request-administrator 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&
        !parse(L"bootstrap.exe --approval-self-test 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&
        !parse(L"bootstrap.exe",&test)&&!parse(L"bootstrap.exe --unknown",&test)&&
        scheduledImageRole()&&scheduledRoleAdmission(TRUE,SECURITY_MANDATORY_MEDIUM_RID,FALSE)&&
        scheduledRoleAdmission(FALSE,SECURITY_MANDATORY_HIGH_RID,TRUE)&&
        !scheduledRoleAdmission(FALSE,SECURITY_MANDATORY_MEDIUM_RID,FALSE)&&
        !scheduledRoleAdmission(FALSE,SECURITY_MANDATORY_MEDIUM_RID,TRUE)&&
        !scheduledRoleAdmission(FALSE,SECURITY_MANDATORY_HIGH_RID,FALSE)&&
        !scheduledRoleAdmission(FALSE,SECURITY_MANDATORY_SYSTEM_RID,TRUE);
#endif
    ok=ok&&parse(L"bootstrap.exe --scheduled-administrator-bootstrap 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&
        scheduledMode&&!requestMode&&!approvalTest&&!test&&parentPid==123&&parentStarted==638959000000000000ULL&&
        same(registrationScope,L"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")&&
        !parse(L"bootstrap.exe --scheduled-administrator-bootstrap 0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&
        !parse(L"bootstrap.exe --scheduled-administrator-bootstrap 0123456789abcdef0123456789abcdef 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&
        !parse(L"bootstrap.exe --scheduled-administrator-bootstrap 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef 00000000000000000000000000000000 123 638959000000000000",&test)&&
        !parse(L"bootstrap.exe --scheduled-administrator-bootstrap 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef 0123456789abcdef0123456789abcdef 0123 638959000000000000",&test)&&
        !parse(L"bootstrap.exe --scheduled-administrator-bootstrap 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef $(Arg0) $(Arg1) $(Arg2)",&test)&&
        !parse(L"bootstrap.exe --scheduled-administrator-bootstrap 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef 0123456789abcdef0123456789abcdef 123 638959000000000000 extra",&test);
#ifndef TASKBARSELECT_SCHEDULED_ONLY
    ok=ok&&
        parse(L"bootstrap.exe --administrator-bootstrap 0123456789abcdef0123456789abcdef 123 638959000000000000",&test)&&!scheduledMode&&!requestMode&&!approvalTest&&!registrationScope[0]&&
        registrationFixtures();
#else
    ok=ok&&registrationFixtures();
#endif
    ok=ok&&diagnosticFixtures()&&envEntry(&offset,L"PATH",L"C:\\Windows\\System32")&&!envEntry(&offset,L"COR_PROFILER",L"evil")&&fixedConfig()&&
        hexText(AdministratorPayloadHash,64,FALSE)&&AdministratorPayloadLength>=512&&AdministratorPayloadLength<=67108864&&
        digestMatches(AdministratorPayloadBytes,AdministratorPayloadLength);
    /* Structural ACL fixtures never create files or change object security.
       A medium caller also verifies actual denied mutation rights. HIGH CI
       still runs every structural fixture; HIGH has intentional BA access. */
    if(selfIntegrity>=SECURITY_MANDATORY_MEDIUM_RID&&selfIntegrity<SECURITY_MANDATORY_HIGH_RID) {
        if(!pOpenToken(GetCurrentProcess(),TOKEN_QUERY|TOKEN_DUPLICATE,&parentToken)||
           !pDuplicate(parentToken,SecurityImpersonation,&parentAccess)||!securityPolicy(protectedSd,TRUE,TRUE,TRUE)) ok=FALSE;
    }
    ok=ok&&securityPolicy(protectedSd,TRUE,TRUE,FALSE)&&
        securityFixture(L"O:BAG:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)",FALSE)&&
        securityFixture(L"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GA;;;BU)",FALSE)&&
        securityFixture(L"O:BUG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)",FALSE)&&
        securityFixture(L"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)(A;OICI;GR;;;WD)",FALSE)&&
        securityFixture(L"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;GRGX;;;BU)",FALSE)&&
        securityFixture(L"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;GRGX;;;BU)",FALSE);
    closeHandle(&parentAccess); closeHandle(&parentToken);
    parentPid=pid; parentStarted=ticks; requestMode=approvalTest=scheduledMode=FALSE; registrationScope[0]=0; return ok;
}
void WINAPI BootstrapEntry(void) {
    BOOL selfTest=FALSE; DWORD code;
    if(!parse(GetCommandLineW(),&selfTest)) ExitProcess(E_ARGS);
    if(!initialNativeFunctions()) ExitProcess(E_LIBRARY);
    if(!authenticate(selfTest)) ExitProcess(E_IDENTITY);
    if(!selfTest&&!prepareNamespace()) code=(requestMode||approvalTest)?E_APPROVAL_REFUSED:E_CACHE;
    else if(!libraries()) code=E_LIBRARY;
    else if(selfTest) code=pureSelfTest()?0:E_SELFTEST;
    else if(requestMode||approvalTest) {
#ifdef TASKBARSELECT_SCHEDULED_ONLY
        /* Keep the shared manual implementation linked for warning parity,
           but no scheduled-only runtime path can request elevation. */
        (void)requestAdministrator; code=E_ARGS;
#else
        code=requestAdministrator();
#endif
    }
    else if(scheduledMode&&!scheduledAdmission()) code=E_IDENTITY;
    else code=run();
    closeRegistration(); closeNamespace(); closeHandle(&parentAccess); closeHandle(&parentToken); closeHandle(&parent); closeHandle(&ownToken);
    if(protectedSd) LocalFree(protectedSd); ExitProcess(code);
}
