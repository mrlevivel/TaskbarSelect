param()
$ErrorActionPreference='Stop'
# Preparation/compilation only. No fixture, app, Setup, account or prompt is run.
function Require($value,[string]$message){if(-not $value){throw $message}}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function ExactInnoVersion($value,[string]$expected){return ($value -is [string] -and [String]::Equals($value.TrimEnd(' '),$expected,[StringComparison]::Ordinal))}
function Api([string]$path){$value=& gh api $path;if($LASTEXITCODE -ne 0){throw 'Private donor metadata read failed.'};$value|ConvertFrom-Json}
function PrivateDownloadFailure([string]$phase,[bool]$exitAvailable,[int]$code,[long]$bytes,[string]$diagnostic){
    $phase=if([String]::Equals($phase,'JobLog',[StringComparison]::Ordinal)){'JobLog'}elseif([String]::Equals($phase,'ArtifactZip',[StringComparison]::Ordinal)){'ArtifactZip'}else{'Unknown'}
    if($null -eq $diagnostic){$diagnostic=''}
    $http=0;$match=[regex]::Match($diagnostic,'(?i)\bHTTP[ /:]*([1-5][0-9]{2})\b')
    if($match.Success){$http=[int]$match.Groups[1].Value}
    $bucket='Other'
    if($http -in @(401,403) -or $diagnostic -match '(?i)GH_TOKEN|GITHUB_TOKEN|auth(?:entication|orization)?|gh auth login'){$bucket='Auth'}
    elseif($http -eq 404 -or $diagnostic -match '(?i)not found'){$bucket='NotFound'}
    elseif($diagnostic -match '(?i)timeout|timed out|TLS|connection|resolve host|DNS'){$bucket='Network'}
    elseif([String]::IsNullOrEmpty($diagnostic)){$bucket='None'}
    # Never return raw diagnostic text, URLs, paths, tokens or caller labels.
    return ('PrivateDownloadFailed Phase={0};ExitAvailable={1};Exit={2};Bytes={3};HTTP={4};Category={5}' -f $phase,$exitAvailable,$code,$bytes,$http,$bucket)
}
function DownloadPrivateBytes([string]$endpoint,[string]$path,[long]$maximum){
    Require ($endpoint -cmatch '^repos/mrlevivel/TaskbarSelect/actions/(artifacts/[1-9][0-9]{0,19}/zip|jobs/[1-9][0-9]{0,19}/logs)$') 'Fixed exact private download endpoint required.'
    if(-not $script:PrivateUpdaterHttpTransportReady){
        Require (-not ('TaskbarSelectPrivateHttpTransportV1' -as [type])) 'Unexpected existing private transport type.'
        $transportSource=@'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

public static class TaskbarSelectPrivateHttpTransportV1
{
    const long Limit = 134217728;
    const int BudgetMilliseconds = 60000;
    public static bool FixedEndpoint(string endpoint)
    {
        return endpoint != null && Regex.IsMatch(endpoint,
            @"\Arepos/mrlevivel/TaskbarSelect/actions/(artifacts/[1-9][0-9]{0,19}/zip|jobs/[1-9][0-9]{0,19}/logs)\z");
    }
    public static bool FixedApi(Uri uri)
    {
        return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Port == 443 && String.Equals(uri.IdnHost, "api.github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Query.Length == 0 &&
            uri.AbsolutePath.Length > 1 && uri.AbsolutePath[0] == '/' && FixedEndpoint(uri.AbsolutePath.Substring(1));
    }
    public static bool HttpsTarget(Uri uri)
    {
        return uri != null && uri.IsAbsoluteUri && uri.OriginalString.Length <= 16384 &&
            uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 &&
            uri.Fragment.Length == 0 && uri.HostNameType == UriHostNameType.Dns &&
            !uri.IsLoopback && uri.IdnHost.IndexOf('.') > 0 &&
            Regex.IsMatch(uri.IdnHost, @"\A[A-Za-z0-9.-]+\z");
    }
    public static bool RedirectAllowed(Uri from, Uri target, int followed, bool authenticatedApi)
    {
        return followed == 0 && authenticatedApi && FixedApi(from) && HttpsTarget(target);
    }
    public static bool Authorize(Uri uri, int hop) { return hop == 0 && FixedApi(uri); }
    public static bool Fits(long current, int next, long maximum)
    {
        return maximum > 0 && maximum <= Limit && current >= 0 && current <= maximum &&
            next >= 0 && (long)next <= maximum - current;
    }
    public static bool HeaderLengthFits(long? length, long maximum)
    {
        return Fits(0, 0, maximum) && (!length.HasValue || (length.Value >= 0 && length.Value <= maximum));
    }
    static bool ValidToken(string token)
    {
        if (String.IsNullOrEmpty(token) || token.Length > 4096) return false;
        foreach (char c in token) if (c < 0x21 || c > 0x7e) return false;
        return true;
    }
    public static HttpRequestMessage Request(Uri uri, int hop, string token)
    {
        if ((hop != 0 && hop != 1) || (hop == 0 ? !FixedApi(uri) : !HttpsTarget(uri)))
            throw new InvalidOperationException("PrivateHttpPolicyRefused");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("TaskbarSelect-private-updater-prepare/1.0");
        if (Authorize(uri, hop)) {
            if (!ValidToken(token)) { request.Dispose(); throw new InvalidOperationException("PrivateHttpTokenUnavailable"); }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        return request;
    }
    sealed class Refusal : Exception
    {
        internal Refusal(string phase, int status, string category, long bytes)
            : base("PrivateHttpDownloadFailed Phase=" + phase + ";HTTP=" + status +
                ";Category=" + category + ";Bytes=" + bytes) { }
    }
    static Refusal Fail(string phase, int status, string category, long bytes)
    {
        return new Refusal(phase, status, category, bytes);
    }
    static void Check(CancellationToken token, Stopwatch watch)
    {
        token.ThrowIfCancellationRequested();
        if (watch.ElapsedMilliseconds >= BudgetMilliseconds) throw new OperationCanceledException();
    }
    static string HttpCategory(int status)
    {
        if (status == 401 || status == 403) return "Auth";
        if (status == 404 || status == 410) return "NotFound";
        if (status == 429) return "RateLimit";
        return "HttpStatus";
    }
    public static void Download(string endpoint, string path, long maximum, string token)
    {
        string phase = endpoint != null && endpoint.EndsWith("/logs", StringComparison.Ordinal) ? "JobLog" : "ArtifactZip";
        int status = 0; long copied = 0;
        if (!FixedEndpoint(endpoint) || !Fits(0, 0, maximum) || !Path.IsPathRooted(path))
            throw Fail(phase, 0, "Policy", 0);
        if (!ValidToken(token)) throw Fail(phase, 0, "TokenUnavailable", 0);
        var watch = Stopwatch.StartNew();
        HttpResponseMessage response = null; Stream input = null;
        try {
            using (var handler = new HttpClientHandler()) {
                handler.AllowAutoRedirect = false;
                handler.UseCookies = false;
                handler.UseDefaultCredentials = false;
                // Decode HTTP content coding; bounds/digest apply to the
                // actual entity bytes, including the original artifact ZIP.
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                // Default OS proxy and certificate validation are retained.
                using (var client = new HttpClient(handler))
                using (var cancel = new CancellationTokenSource()) {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    cancel.CancelAfter(BudgetMilliseconds);
                    using (cancel.Token.Register(delegate {
                        // CLR delegate, not a PowerShell threadpool scriptblock.
                        try { if (input != null) input.Dispose(); } catch { }
                        try { if (response != null) response.Dispose(); } catch { }
                        try { client.CancelPendingRequests(); } catch { }
                    })) {
                        Uri current = new Uri("https://api.github.com/" + endpoint);
                        for (int hop = 0; ; hop++) {
                            Check(cancel.Token, watch);
                            using (var request = Request(current, hop, token)) {
                                response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).GetAwaiter().GetResult();
                                Check(cancel.Token, watch);
                                status = (int)response.StatusCode;
                                if (status == 302) {
                                    Uri target = response.Headers.Location;
                                    bool authenticated = request.Headers.Authorization != null;
                                    if (!RedirectAllowed(current, target, hop, authenticated))
                                        throw Fail(phase, status, "RedirectRefused", copied);
                                    response.Dispose(); response = null;
                                    current = target; continue;
                                }
                                if (status != 200) throw Fail(phase, status, HttpCategory(status), copied);
                                if (!HeaderLengthFits(response.Content.Headers.ContentLength, maximum))
                                    throw Fail(phase, status, "Oversize", copied);
                                Check(cancel.Token, watch);
                                input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                                Check(cancel.Token, watch);
                                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) {
                                    byte[] buffer = new byte[65536];
                                    for (;;) {
                                        Check(cancel.Token, watch);
                                        int read = input.ReadAsync(buffer, 0, buffer.Length, cancel.Token).GetAwaiter().GetResult();
                                        Check(cancel.Token, watch);
                                        if (read == 0) break;
                                        if (!Fits(copied, read, maximum)) throw Fail(phase, status, "Oversize", copied);
                                        file.Write(buffer, 0, read); copied += read;
                                    }
                                    if (copied <= 0) throw Fail(phase, status, "Empty", copied);
                                    Check(cancel.Token, watch); file.Flush(true); Check(cancel.Token, watch);
                                }
                                return;
                            }
                        }
                    }
                }
            }
        } catch (Refusal) { throw; }
          catch (OperationCanceledException) { throw Fail(phase, status, "Timeout", copied); }
          catch (HttpRequestException) { throw Fail(phase, status, watch.ElapsedMilliseconds >= BudgetMilliseconds ? "Timeout" : "Network", copied); }
          catch (IOException) { throw Fail(phase, status, watch.ElapsedMilliseconds >= BudgetMilliseconds ? "Timeout" : "ReadIO", copied); }
          catch (ObjectDisposedException) { throw Fail(phase, status, watch.ElapsedMilliseconds >= BudgetMilliseconds ? "Timeout" : "Disposed", copied); }
          catch (Exception) { throw Fail(phase, status, "Other", copied); }
        finally {
            try { if (input != null) input.Dispose(); } catch { }
            try { if (response != null) response.Dispose(); } catch { }
            watch.Stop();
        }
    }
}
'@
        Add-Type -TypeDefinition $transportSource -ReferencedAssemblies System.Net.Http.dll -ErrorAction Stop
        $script:PrivateUpdaterHttpTransportReady=$true
    }
    # Only the existing step-scoped GH_TOKEN is used; no CLI credential fallback.
    [TaskbarSelectPrivateHttpTransportV1]::Download($endpoint,$path,$maximum,$env:GH_TOKEN)
}
function FreshDirectory([string]$path){Require (-not(Test-Path -LiteralPath $path)) 'Fresh preparation directory required.';[void][IO.Directory]::CreateDirectory($path)}
Require ($env:GITHUB_ACTIONS -ceq 'true' -and $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and $env:RUNNER_OS -ceq 'Windows' -and
    $env:GITHUB_EVENT_NAME -ceq 'workflow_dispatch' -and $env:GITHUB_REF -ceq 'refs/heads/codex/beta18-private-updater' -and
    $env:GITHUB_REPOSITORY -ceq 'mrlevivel/TaskbarSelect' -and $env:GITHUB_SHA -cmatch '^[a-f0-9]{40}$' -and
    $PSVersionTable.PSEdition -ceq 'Desktop' -and [Environment]::Is64BitProcess) 'Genuine dedicated hosted Windows dispatch required.'
Require ($env:GITHUB_EVENT_PATH -and (Test-Path -LiteralPath $env:GITHUB_EVENT_PATH -PathType Leaf) -and
    (Get-Item -LiteralPath $env:GITHUB_EVENT_PATH).Length -le 2097152) 'Bounded actual event required.'
$event=Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw|ConvertFrom-Json
Require ($event.repository.id -eq 1449520856 -and $event.repository.private -eq $true -and
    $event.repository.full_name -ceq 'mrlevivel/TaskbarSelect') 'Still-private exact repository required.'
$rawSpec=$event.inputs.updater_spec
Require ($rawSpec -is [string] -and $rawSpec.Length -gt 0 -and $rawSpec.Length -le 4096) 'Bounded JSON donor spec required.'
$inputSpec=$rawSpec|ConvertFrom-Json
$keys=@('donor_run_id','donor_artifact_id','donor_head_sha','donor_compiled_sha','donor_artifact_digest','target_app_sha256','target_setup_sha256','target_sums_sha256')
Require (@($inputSpec.PSObject.Properties).Count -eq $keys.Count -and @(Compare-Object ($keys|Sort-Object) (@($inputSpec.PSObject.Properties.Name)|Sort-Object) -CaseSensitive).Count -eq 0) 'Exact donor spec keyset required.'
foreach($field in @('donor_run_id','donor_artifact_id')){Require ($inputSpec.$field -is [string] -and $inputSpec.$field -cmatch '^[1-9][0-9]{0,19}$') 'Numeric donor identity required.'}
foreach($field in @('donor_head_sha','donor_compiled_sha')){Require ($inputSpec.$field -is [string] -and $inputSpec.$field -cmatch '^[a-f0-9]{40}$') 'Full donor commit required.'}
foreach($field in @('target_app_sha256','target_setup_sha256','target_sums_sha256')){Require ($inputSpec.$field -is [string] -and $inputSpec.$field -cmatch '^[a-f0-9]{64}$') 'Exact lowercase candidate hash required.'}
Require ($inputSpec.donor_artifact_digest -is [string] -and $inputSpec.donor_artifact_digest -cmatch '^sha256:[a-f0-9]{64}$') 'Exact donor archive digest required.'
foreach($flag in @('private_updater','execute_trial','approve_run')){Require ($event.inputs.$flag -ceq 'true' -or $event.inputs.$flag -ceq 'false' -or $event.inputs.$flag -is [bool]) 'Strict dispatch switch required.'}
Require ($event.inputs.private_updater -ceq 'true' -or $event.inputs.private_updater -eq $true) 'Dedicated mode must be explicit.'
$allowRun=$event.inputs.approve_run -ceq 'true' -or $event.inputs.approve_run -eq $true
$executeTrial=$event.inputs.execute_trial -ceq 'true' -or $event.inputs.execute_trial -eq $true
Require (-not $allowRun -or $executeTrial) 'Run approval without an explicit trial is invalid.'
if($allowRun){Require ($env:GITHUB_RUN_ATTEMPT -ceq '1') 'Security consent can only apply to a fresh first-attempt dispatch.'}
$repository='mrlevivel/TaskbarSelect'
$repoMeta=Api ('repos/'+$repository)
Require ($repoMeta.id -eq 1449520856 -and $repoMeta.private -eq $true -and $repoMeta.full_name -ceq $repository) 'Repository identity changed.'
$donorRun=Api ('repos/'+$repository+'/actions/runs/'+$inputSpec.donor_run_id)
Require ($donorRun.id -eq [long]$inputSpec.donor_run_id -and $donorRun.status -ceq 'completed' -and $donorRun.conclusion -ceq 'success' -and
    $donorRun.head_sha -ceq $inputSpec.donor_head_sha -and $donorRun.head_repository.id -eq 1449520856 -and
    $donorRun.repository.id -eq 1449520856 -and $donorRun.path -ceq '.github/workflows/build.yml' -and
    $donorRun.event -cin @('pull_request','workflow_dispatch','push')) 'Successful same-repository nonfork normal Build donor required.'
$jobs=Api ('repos/'+$repository+'/actions/runs/'+$inputSpec.donor_run_id+'/jobs?per_page=100')
$windowsJobs=@($jobs.jobs|Where-Object {$_.name -ceq 'windows' -and $_.conclusion -ceq 'success'})
Require ($jobs.total_count -le 100 -and $windowsJobs.Count -eq 1 -and $windowsJobs[0].id -gt 0 -and
    $windowsJobs[0].run_id -eq $donorRun.id) 'Single normal windows release-gate job must have succeeded.'
$artifact=Api ('repos/'+$repository+'/actions/artifacts/'+$inputSpec.donor_artifact_id)
Require ($artifact.id -eq [long]$inputSpec.donor_artifact_id -and $artifact.name -ceq 'Taskbar-Select-beta-package' -and -not $artifact.expired -and
    $artifact.digest -ceq $inputSpec.donor_artifact_digest -and $artifact.workflow_run.id -eq $donorRun.id -and
    $artifact.workflow_run.head_sha -ceq $inputSpec.donor_head_sha -and $artifact.size_in_bytes -gt 0 -and
    $artifact.size_in_bytes -le 134217728) 'Exact bounded validated donor artifact required.'
$runArtifacts=Api ('repos/'+$repository+'/actions/runs/'+$inputSpec.donor_run_id+'/artifacts?per_page=100')
Require ($runArtifacts.total_count -le 100 -and @($runArtifacts.artifacts|Where-Object name -CEQ 'Taskbar-Select-beta-package').Count -eq 1) 'Donor artifact name must be unique.'
$compiled=Api ('repos/'+$repository+'/git/commits/'+$inputSpec.donor_compiled_sha)
Require ($compiled.sha -ceq $inputSpec.donor_compiled_sha) 'Compiled donor commit missing.'
if($donorRun.event -ceq 'pull_request'){
    Require (@($compiled.parents|Where-Object sha -CEQ $inputSpec.donor_head_sha).Count -eq 1) 'PR merge donor must contain the exact reviewed head.'
}else{Require ($compiled.sha -ceq $inputSpec.donor_head_sha) 'Branch donor compiled commit differs from run head.'}
$fixtureCommit=Api ('repos/'+$repository+'/git/commits/'+$env:GITHUB_SHA)
$before=Api ('repos/'+$repository+'/git/trees/'+$compiled.tree.sha+'?recursive=1')
$after=Api ('repos/'+$repository+'/git/trees/'+$fixtureCommit.tree.sha+'?recursive=1')
Require (-not $before.truncated -and -not $after.truncated -and $before.sha -ceq $compiled.tree.sha -and $after.sha -ceq $fixtureCommit.tree.sha -and
    @($before.tree).Count -gt 0 -and @($before.tree).Count -le 4000 -and @($after.tree).Count -le 4000) 'Complete bounded tree comparison required.'
$old=@{};$new=@{}
foreach($entry in $before.tree|Where-Object type -CNE 'tree'){$old[$entry.path]=$entry.mode+':'+$entry.type+':'+$entry.sha}
foreach($entry in $after.tree|Where-Object type -CNE 'tree'){$new[$entry.path]=$entry.mode+':'+$entry.type+':'+$entry.sha}
$changed=@(@($old.Keys)+@($new.Keys)|Sort-Object -Unique|Where-Object {$old[$_] -cne $new[$_]})
Require (@($changed|Where-Object {$_ -cne '.github/workflows/build.yml' -and $_ -cnotmatch '^\.github/scripts/private-updater-[A-Za-z0-9_.-]+$'}).Count -eq 0) 'Diagnostic branch must preserve every donor shipping/build input.'
$adapter=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'private-updater-build-adapter.json') -Raw|ConvertFrom-Json
Require ($adapter.schema -eq 1 -and $adapter.donorBuildBlob -cmatch '^[a-f0-9]{40}$' -and $adapter.adapterBuildBlob -cmatch '^[a-f0-9]{40}$' -and
    $old['.github/workflows/build.yml'] -ceq ('100644:blob:'+$adapter.donorBuildBlob) -and
    $new['.github/workflows/build.yml'] -ceq ('100644:blob:'+$adapter.adapterBuildBlob)) 'Normal Build workflow must differ only by the exact reviewed adapter blob.'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$donor=Join-Path $root 'donor-source'
$actualHead=& git -C $donor rev-parse HEAD
Require ($LASTEXITCODE -eq 0 -and $actualHead -ceq $inputSpec.donor_compiled_sha) 'Separate donor source checkout must be the compiled commit.'
$dirty=& git -C $donor status --porcelain
Require ($LASTEXITCODE -eq 0 -and -not $dirty) 'Donor checkout must be unchanged before fixture compilation.'
$dist=Join-Path $root 'dist';$artifacts=Join-Path $root 'artifacts';$inputs=Join-Path $artifacts 'private-updater-input'
FreshDirectory $dist;FreshDirectory $artifacts;FreshDirectory $inputs
$checkoutLog=Join-Path $artifacts 'donor-windows-checkout.private.log'
DownloadPrivateBytes ('repos/'+$repository+'/actions/jobs/'+$windowsJobs[0].id+'/logs') $checkoutLog 33554432
$logText=[Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($checkoutLog))
$timestamp='[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z'
$checkoutPattern='(?m)^'+$timestamp+' \[command\][^\r\n]{0,500}\bgit(?:\.exe)?"? log -1 --format=%H\r?\n'+$timestamp+' ([a-f0-9]{40})\r?$'
$checkoutMatches=[regex]::Matches($logText,$checkoutPattern)
Require ($checkoutMatches.Count -eq 1 -and $checkoutMatches[0].Groups[1].Value -ceq $inputSpec.donor_compiled_sha) 'Exact single successful-job checkout must prove the compiled donor SHA.'
$checkoutLogHash=Hash $checkoutLog;$logText=$null
# Private raw logs are not exported by this workflow; preserve only digest and
# the independently parsed exact compiled commit in bounded provenance.
$artifactZip=Join-Path $artifacts 'validated-donor-artifact.zip'
DownloadPrivateBytes ('repos/'+$repository+'/actions/artifacts/'+$inputSpec.donor_artifact_id+'/zip') $artifactZip 134217728
Require ((Get-Item -LiteralPath $artifactZip).Length -gt 0 -and (Get-Item -LiteralPath $artifactZip).Length -le 134217728 -and
    ('sha256:'+(Hash $artifactZip)) -ceq $inputSpec.donor_artifact_digest) 'Downloaded donor archive bytes differ from reviewed digest/bound.'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageArchive=[IO.Compression.ZipFile]::OpenRead($artifactZip)
try{
    $expectedPackage=@('TaskbarSelect-1.0.0-beta.18-Setup.exe','TaskbarSelect-1.0.0-beta.18-portable.zip','SHA256SUMS.txt')
    Require ($packageArchive.Entries.Count -eq 3) 'Exact three-file donor archive required.'
    foreach($name in $expectedPackage){
        $entry=@($packageArchive.Entries|Where-Object FullName -CEQ $name)
        Require ($entry.Count -eq 1 -and $entry[0].Length -gt 0 -and $entry[0].Length -le 67108864) 'Missing/duplicate/oversize donor archive entry.'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0],(Join-Path $dist $name),$false)
    }
}finally{$packageArchive.Dispose()}
$files=@(Get-ChildItem -LiteralPath $dist -Force)
$names=@('TaskbarSelect-1.0.0-beta.18-Setup.exe','TaskbarSelect-1.0.0-beta.18-portable.zip','SHA256SUMS.txt')
Require ($files.Count -eq 3 -and @($files|Where-Object {$_.PSIsContainer -or $_.Name -cnotin $names -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)}).Count -eq 0) 'Exact three-file validated package required.'
$setup=Join-Path $dist $names[0];$portable=Join-Path $dist $names[1];$sums=Join-Path $dist $names[2]
Require ((Hash $setup) -ceq $inputSpec.target_setup_sha256 -and (Hash $sums) -ceq $inputSpec.target_sums_sha256) 'Candidate Setup/checksum hashes differ.'
$checksumLines=@(Get-Content -LiteralPath $sums)
foreach($package in @($setup,$portable)){
    $leaf=[IO.Path]::GetFileName($package);$rows=@($checksumLines|Where-Object {$_ -cmatch ('^[a-f0-9]{64}  '+[regex]::Escape($leaf)+'$')})
    Require ($rows.Count -eq 1 -and $rows[0].Substring(0,64) -ceq (Hash $package)) 'Each candidate file must have exactly one matching checksum.'
}
$archive=[IO.Compression.ZipFile]::OpenRead($portable)
try{
    $allowed=@('TaskbarSelect.exe','README.md','RELEASE_NOTES.md','TESTING.md','SECURITY.md','CHANGELOG.md','LICENSE','ARTWORK.md')
    Require ($archive.Entries.Count -eq 8 -and @($archive.Entries|Where-Object {$_.FullName -cnotin $allowed}).Count -eq 0) 'Portable package allowlist differs.'
    foreach($name in $allowed){Require (@($archive.Entries|Where-Object FullName -CEQ $name).Count -eq 1) 'Portable entry absent or duplicated.'}
    $appEntry=$archive.GetEntry('TaskbarSelect.exe');Require ($appEntry.Length -gt 0 -and $appEntry.Length -le 33554432) 'Bounded candidate executable required.'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($appEntry,(Join-Path $artifacts 'TaskbarSelect.exe'),$false)
}finally{$archive.Dispose()}
$app=Join-Path $artifacts 'TaskbarSelect.exe'
Require ((Hash $app) -ceq $inputSpec.target_app_sha256 -and [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion -ceq '1.0.0-beta.18' -and
    (ExactInnoVersion ([Diagnostics.FileVersionInfo]::GetVersionInfo($setup).ProductVersion) '1.0.0-beta.18')) 'Exact beta18 candidate version and app identity required.'
Copy-Item -LiteralPath $app,$setup,$sums -Destination $inputs
# The normal donor downloader admits the exact same-repository beta17 Actions
# artifact with a read-only token. The tree comparison above keeps that script,
# its guard policy and its reviewed artifact manifest equal to the donor.
# The running predecessor below is rebuilt from candidate beta18 source with
# beta17 version metadata and the controlled transport seam. It is never the
# released beta17 updater.
foreach($relative in @('.github/scripts/Get-InstallerLifecycleBaseline.ps1', '.github/scripts/Installer-Guards.ps1', '.github/scripts/beta17-baseline-artifact.json')){
    $localInput=Join-Path $root $relative;$donorInput=Join-Path $donor $relative
    Require ((Test-Path -LiteralPath $localInput -PathType Leaf) -and (Test-Path -LiteralPath $donorInput -PathType Leaf) -and
        (Hash $localInput) -ceq (Hash $donorInput)) 'Normal baseline downloader inputs must match the compiled donor.'
}
& (Join-Path $PSScriptRoot 'Get-InstallerLifecycleBaseline.ps1')
Require ($LASTEXITCODE -eq 0) 'Normal read-only baseline artifact download failed.'
$baseline=Join-Path ([IO.Path]::GetFullPath($env:RUNNER_TEMP)) 'TaskbarSelect-beta17-baseline'
Require ((Hash (Join-Path $baseline 'TaskbarSelect-1.0.0-beta.17-Setup.exe')) -ceq 'e1d35af6c0d9ca9bc16f537b82d12058bbf8cb9ae1180a83f8b1b4a640350a3d' -and
    (Hash (Join-Path $baseline 'SHA256SUMS.txt')) -ceq 'a9bc425be4ac93d3d5a4f6194a29dd2d64be4fb514898c8914e64a35fa4f50a4') 'Exact published beta17 bootstrap assets required.'
Copy-Item -LiteralPath (Join-Path $baseline 'TaskbarSelect-1.0.0-beta.17-Setup.exe') -Destination $inputs
$replace=@{'@TARGET_APP_SHA256@'=$inputSpec.target_app_sha256;'@TARGET_SETUP_SHA256@'=$inputSpec.target_setup_sha256;'@TARGET_SUMS_SHA256@'=$inputSpec.target_sums_sha256}
foreach($template in Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter 'private-updater-*.in'){
    $text=[Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($template.FullName))
    foreach($key in $replace.Keys){$text=$text.Replace($key,$replace[$key])}
    Require ($text -cnotmatch '@TARGET_[A-Z0-9_]+@') 'Unbound executable template value.'
    [IO.File]::WriteAllText((Join-Path $inputs $template.Name.Substring(0,$template.Name.Length-3)),$text,[Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'private-updater-assembly.cs') -Destination $inputs
$resourceDirectory=Join-Path $inputs 'resources';FreshDirectory $resourceDirectory
$assembly=[Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($app));$resources=@();$index=0
foreach($name in $assembly.GetManifestResourceNames()){
    Require ($name -cmatch '^TaskbarSelect\.[A-Za-z0-9_.-]+$') 'Unexpected candidate resource name.'
    $resource=Join-Path $resourceDirectory ('resource-'+$index+'.bin');$stream=$assembly.GetManifestResourceStream($name)
    try{Require ($stream.Length -gt 0 -and $stream.Length -le 33554432) 'Bounded candidate resource required.';$file=[IO.File]::Create($resource);try{$stream.CopyTo($file)}finally{$file.Dispose()}}finally{$stream.Dispose()}
    $resources+=('/resource:'+ $resource+','+$name);$index++
}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe';$wpf=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$sources=@(Get-ChildItem -LiteralPath $donor -Filter '*.cs' -File|Where-Object Name -CNE 'AssemblyInfo.cs'|ForEach-Object {$_.FullName})
Require ($sources.Count -gt 100 -and $sources.Count -lt 400) 'Bounded complete candidate managed source set required.'
$sourceHashes=@($sources|ForEach-Object {[ordered]@{file=[IO.Path]::GetFileName($_);sha256=Hash $_}})
$sourceForm=Join-Path $donor 'AppUpdateForm.cs'
$formText=[Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($sourceForm))
$productionFactory=': this(stateController, AppUpdateService.CreateDefault()) { }'
$fixtureFactory=': this(stateController, PrivateUpdateFixture.CreateUpdateService()) { }'
Require ([regex]::Matches($formText,[regex]::Escape($productionFactory)).Count -eq 1) 'Exact updater form factory seam required.'
$adaptedForm=Join-Path $artifacts 'AppUpdateForm.fixture.cs'
[IO.File]::WriteAllText($adaptedForm,$formText.Replace($productionFactory,$fixtureFactory),[Text.UTF8Encoding]::new($false))
$fixtureSources=@($sources|Where-Object {$_ -cne $sourceForm})+@($adaptedForm)
$compilerArguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/warnaserror+','/main:TaskbarSelect.PrivateUpdateFixture',
    '/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Management.dll',
    ('/r:'+(Join-Path $wpf 'UIAutomationClient.dll')),('/r:'+(Join-Path $wpf 'UIAutomationTypes.dll')),('/r:'+(Join-Path $wpf 'WindowsBase.dll')),
    ('/win32icon:'+(Join-Path $donor 'assets\TaskbarSelect.ico')),('/win32manifest:'+(Join-Path $donor 'app.manifest')),('/out:'+(Join-Path $inputs 'PrivateUpdateFixture.exe')))
& $compiler @compilerArguments @resources @fixtureSources (Join-Path $inputs 'private-updater-host.cs') (Join-Path $inputs 'private-updater-assembly.cs')
Require ($LASTEXITCODE -eq 0 -and [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $inputs 'PrivateUpdateFixture.exe')).ProductVersion -ceq '1.0.0-beta.17') 'Labelled source-built predecessor compilation failed.'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Management.dll /r:System.Web.Extensions.dll ('/out:'+(Join-Path $inputs 'SetupWizardSupervisor.exe')) (Join-Path $inputs 'private-updater-wizard.cs')
Require ($LASTEXITCODE -eq 0) 'Owned ordinary wizard supervisor compilation failed.'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /main:PrivateUpdaterCiFixture ('/out:'+(Join-Path $inputs 'PrivateUpdaterCiFixture.exe')) (Join-Path $inputs 'private-updater-wrapper.cs') (Join-Path $donor '.github\scripts\AdministratorCiLaunchProbe.cs') (Join-Path $donor '.github\scripts\AdministratorCiBoundaryLaunch.cs')
Require ($LASTEXITCODE -eq 0) 'Authenticated disposable MED wrapper compilation failed.'
foreach($entry in $sourceHashes){Require ((Hash (Join-Path $donor $entry.file)) -ceq $entry.sha256) 'Candidate source changed during compilation.'}
$generatedHashes=@(Get-ChildItem -LiteralPath $inputs -File|Where-Object {$_.Name -like 'private-updater-*' -or $_.Name -cin @('PrivateUpdateFixture.exe','PrivateUpdaterCiFixture.exe','SetupWizardSupervisor.exe')}|ForEach-Object {[ordered]@{file=$_.Name;sha256=Hash $_.FullName}})
$provenance=[ordered]@{schema='taskbar-select-private-beta18-updater-input-v1';preparedOnly=$true;fixtureExecuted=$false;donorRun=$donorRun.id;donorAttempt=$donorRun.run_attempt;
    donorWindowsJob=$windowsJobs[0].id;donorCheckoutLogSha256=$checkoutLogHash;singleCheckoutCompiledCommitVerified=$true;
    donorHead=$inputSpec.donor_head_sha;compiledCommit=$compiled.sha;compiledTree=$compiled.tree.sha;fixtureSource=$env:GITHUB_SHA;artifactId=$artifact.id;archiveDigest=$artifact.digest;
    appSha256=$inputSpec.target_app_sha256;setupSha256=$inputSpec.target_setup_sha256;sumsSha256=$inputSpec.target_sums_sha256;targetVersion='1.0.0-beta.18';
    predecessorVersion='1.0.0-beta.17';predecessorSource=$inputSpec.donor_compiled_sha;predecessorTree=$compiled.tree.sha;candidateSourceVersion='1.0.0-beta.18';sourceBuiltPredecessor=$true;exactReleasedBeta17=$false;
    transportAndVersionAdapter=$true;exactReleasedBeta17Updater=$false;adaptedFormSha256=(Hash $adaptedForm);realStagingRequired=$true;
    internetZoneMustRemain=$true;securityInputDefaultOff=$true;allowUnsignedRun=$allowRun;publicGitHubTested=$false;sourceHashes=$sourceHashes;generatedHashes=$generatedHashes;diagnosticOnlyChanges=$changed}
$provenance|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $artifacts 'private-updater-provenance.json') -Encoding UTF8
Write-Output 'Prepared exact validated beta18 bytes and labelled candidate-source beta17-version predecessor; no app/Setup/account/security action executed.'
