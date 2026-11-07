param([string]$CandidateVersion)
$ErrorActionPreference = 'Stop'
# Refuse personal and self-hosted environments before compiling, spawning,
# staging a bootstrap or creating any fixture output. Do not fake these fields.
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') {
    throw 'Administrator boundary validation requires a disposable GitHub-hosted Windows runner.'
}
Import-Module -Name (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Global -Force
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')
if (-not [Environment]::Is64BitProcess -or -not [IO.Path]::IsPathRooted($runnerTemp) -or -not (Test-Path -LiteralPath $runnerTemp -PathType Container)) {
    throw 'Administrator boundary validation requires a 64-bit runner and an existing absolute RUNNER_TEMP.'
}
function NoReparseAncestors([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture paths must not traverse a reparse point.' }
        }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if (-not $parent -or $parent -eq $current) { break }; $current = $parent
    }
}
NoReparseAncestors $runnerTemp
$executable = Join-Path $repoRoot 'artifacts\TaskbarSelect.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Build and package must precede administrator boundary validation.' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
. (Join-Path $PSScriptRoot 'Installer-Guards.ps1')
Assert-InstallerSmokeMode -Version $version -CandidateVersion $CandidateVersion -Ref $env:GITHUB_REF -EventName $env:GITHUB_EVENT_NAME
if (@(Get-Process -Name TaskbarSelect,AdministratorPayload,AdministratorBootstrap -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'No existing Taskbar Select manager, HIGH payload, or bootstrap may be present.'
}
$cacheRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'TaskbarSelect.Admin'
$normalProfile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TaskbarSelect'
$startupLink = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)) 'Taskbar Select.lnk'
foreach ($path in @($cacheRoot, $normalProfile, $startupLink)) {
    if (Test-Path -LiteralPath $path) { throw 'A pre-existing production profile, Startup link, or protected administrator cache blocks the disposable fixture.' }
}
$evidence = Join-Path $repoRoot 'artifacts\administrator-boundary'
if (Test-Path -LiteralPath $evidence) { throw 'Preserve previous administrator boundary evidence; a clean runner checkout is required.' }
$workspace = Join-Path $runnerTemp ('TaskbarSelect-administrator-boundary-' + [Guid]::NewGuid().ToString('N'))
if (-not $workspace.StartsWith($runnerTemp + '\', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $workspace)) {
    throw 'Fixture workspace must be a fresh child of RUNNER_TEMP.'
}
$null = New-Item -ItemType Directory -Path $evidence
$fixture = Join-Path $evidence 'AdministratorBoundaryFixture.exe'
$source = Join-Path $PSScriptRoot 'AdministratorBoundaryFixture.cs'
$launcherSource = Join-Path $PSScriptRoot 'AdministratorCiLaunchProbe.cs'
$boundarySource = Join-Path $PSScriptRoot 'AdministratorCiBoundaryLaunch.cs'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /main:AdministratorBoundaryFixture "/out:$fixture" $source $launcherSource $boundarySource
if ($LASTEXITCODE -ne 0) { throw "Administrator boundary fixture compiler failed: $LASTEXITCODE" }
$beforeHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
& $fixture --check-protocol $executable
if ($LASTEXITCODE -ne 0) { throw 'The fixture Focus packet does not match the actual candidate protocol; no hosted account or native fixture was launched.' }
if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -cne $beforeHash) { throw 'The candidate changed during managed protocol preflight; no hosted fixture was launched.' }
$stdout = Join-Path $evidence 'stdout.log'; $stderr = Join-Path $evidence 'stderr.log'
$started = [DateTime]::UtcNow
# The genuine MED parent creates the fresh runtime workspace/profile. The
# compiled fixture and collector logs remain in the separate evidence folder.
$process = Start-Process -FilePath $fixture -ArgumentList @('--run-ci', ('"' + $executable + '"'), ('"' + $workspace + '"')) -WorkingDirectory $evidence -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    $null = $process.Handle
    # The fixture contains its own native descendants in an unnamed outer job.
    # It must query zero active processes before closing that handle for PASS.
    # This collector never force-kills an unconfirmed HIGH process or erases
    # failed recovery state; the disposable workflow is the final time bound.
    if (-not $process.WaitForExit(240000)) {
        @{ status='failed'; reason='Owned boundary fixture exceeded 240 seconds; HIGH job drain is unconfirmed.'; fixturePid=$process.Id; sourceSha=$env:GITHUB_SHA; appSha256=$beforeHash.ToLowerInvariant() } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
        throw 'Administrator boundary fixture timed out; no successful cleanup or release readiness is claimed.'
    }
    $process.Refresh(); $fixtureExit = $process.ExitCode
    if ($null -eq $fixtureExit) { throw 'Owned boundary fixture exit could not be observed.' }
    $launcherResult = Join-Path $workspace 'launcher.json'
    if (-not (Test-Path -LiteralPath $launcherResult -PathType Leaf) -or (Get-Item -LiteralPath $launcherResult).Length -gt 262144) { throw 'No bounded authoritative CI sponsor-chain result was retained.' }
    Copy-Item -LiteralPath $launcherResult -Destination (Join-Path $evidence 'launcher.json')
    $launcher = Get-Content -LiteralPath $launcherResult -Raw | ConvertFrom-Json
    foreach ($name in @('stdout.log','stderr.log')) { $caseLog = Join-Path $workspace $name; if (Test-Path -LiteralPath $caseLog -PathType Leaf) { if ((Get-Item -LiteralPath $caseLog).Length -gt 262144) { throw 'Case log exceeds its bound.' }; Copy-Item -LiteralPath $caseLog -Destination (Join-Path $evidence ('fixture-' + $name)) } }
    $linkedObservation = Join-Path $workspace 'linked-high-observation.json'
    if (Test-Path -LiteralPath $linkedObservation -PathType Leaf) { if ((Get-Item -LiteralPath $linkedObservation).Length -gt 4096) { throw 'Linked-token observation exceeds its bound.' }; Copy-Item -LiteralPath $linkedObservation -Destination (Join-Path $evidence 'linked-high-observation.json') }
    $recoveryFiles = @(Get-ChildItem -LiteralPath $workspace -Filter 'owned-recovery-*.json' -File)
    if ($recoveryFiles.Count -gt 10 -or ($recoveryFiles.Count -gt 0 -and -not $launcher.Probe.Drained)) { throw 'Recovery evidence requires a bounded, drained owned workspace.' }
    foreach ($recoveryFile in $recoveryFiles) { if ($recoveryFile.Length -gt 65536) { throw 'Owned recovery evidence exceeds its bound.' }; Copy-Item -LiteralPath $recoveryFile.FullName -Destination (Join-Path $evidence $recoveryFile.Name) }
    $mediumLogs = @(Get-ChildItem -LiteralPath $workspace -Filter 'owned-medium-*.stdout.log' -File) + @(Get-ChildItem -LiteralPath $workspace -Filter 'owned-medium-*.stderr.log' -File)
    if ($mediumLogs.Count -gt 20 -or ($mediumLogs.Count -gt 0 -and -not $launcher.Probe.Drained)) { throw 'MED child logs require a bounded, drained owned workspace.' }
    foreach ($mediumLog in $mediumLogs) { if ($mediumLog.Name -cnotmatch '^owned-medium-[0-9a-f]{32}\.(stdout|stderr)\.log$' -or $mediumLog.Length -gt 262144) { throw 'MED child log name or size exceeds its bound.' }; Copy-Item -LiteralPath $mediumLog.FullName -Destination (Join-Path $evidence $mediumLog.Name) }
    $fixtureResult = Join-Path $workspace 'result.json'
    if (-not (Test-Path -LiteralPath $fixtureResult -PathType Leaf)) { throw 'Administrator boundary fixture produced no authoritative result.' }
    if ((Get-Item -LiteralPath $fixtureResult).Length -gt 262144) { throw 'Fixture result exceeds its bound.' }
    Copy-Item -LiteralPath $fixtureResult -Destination (Join-Path $evidence 'result.json')
    $result = Get-Content -LiteralPath $fixtureResult -Raw | ConvertFrom-Json
    if ($fixtureExit -eq 3 -or $result.status -ceq 'blocked') { throw ('BLOCKED: genuine HIGH/MED administrator validation unavailable: ' + $result.reason) }
    if ($fixtureExit -ne 0 -or $result.status -cne 'passed' -or $result.checks -le 0 -or @($result.cases).Count -ne 10 -or @($result.cases | Where-Object { $_.status -cne 'passed' }).Count -ne 0) {
        throw 'Actual administrator boundary validation failed; release publication remains gated.'
    }
    $expectedCases = @('move-placement-undo','absent-boot-grant','absent-resume','absent-authorization','bad-sequence','late-authorization','parent-death','unresponsive-owned-target','active-worker-parent-death','approval-host-route')
    for ($caseIndex = 0; $caseIndex -lt $expectedCases.Count; $caseIndex++) { if ($result.cases[$caseIndex].name -cne $expectedCases[$caseIndex]) { throw 'Administrator boundary evidence must contain the exact ten unique scenarios in their fixed order.' } }
    if ($launcher.Probe.Status -cne 'passed' -or -not $launcher.Probe.Drained -or -not $launcher.Probe.ProtectedSourceRemoved -or
        $launcher.Probe.RemainingJobProcesses -ne 0 -or $launcher.Probe.HighExit -ne 0 -or $launcher.Probe.SponsorExit -ne 0 -or
        -not $launcher.AccountRemoved -or $launcher.CleanupUncertain -or -not $launcher.Probe.HighHostedChainGrantAuthenticated -or
        -not $launcher.Probe.SourceMediumDuplicateVerified -or $launcher.Probe.BoundaryAppSha256 -cne $beforeHash.ToLowerInvariant()) {
        throw 'Genuine CI sponsor identity, descendant drain, protected source and exact account cleanup must all pass.'
    }
    $approvalRoute = @($result.cases | Where-Object { $_.name -ceq 'approval-host-route' })
    if ($approvalRoute.Count -ne 1 -or -not $approvalRoute[0].realApprovalHostRoute -or -not $approvalRoute[0].sourceStageApi -or
        $approvalRoute[0].interactivePromptTested -or $approvalRoute[0].mediumHostExit -ne 0 -or $approvalRoute[0].nativeExit -ne 0 -or
        -not $approvalRoute[0].protectedSessionRemoved) {
        throw 'The actual MED approval-host/private-map/runas route and its descendant cleanup require separate matching evidence.'
    }
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -cne $beforeHash -or $result.appSha256 -cne $beforeHash.ToLowerInvariant() -or $result.sourceSha -cne $env:GITHUB_SHA -or $result.version -cne $version) {
        throw 'Administrator boundary evidence does not match the unchanged packaged source/artifact.'
    }
    foreach ($path in @($cacheRoot, $normalProfile, $startupLink)) { if (Test-Path -LiteralPath $path) { throw 'Disposable administrator fixture did not preserve the absent production profile/Startup/cache baseline.' } }
    if (@(Get-Process -Name TaskbarSelect,AdministratorPayload,AdministratorBootstrap -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'An owned administrator descendant remains after the fixture.'
    }
    Write-Host ('PASS: Actual administrator boundary: ' + $result.checks + ' assertions across ten genuine HIGH/MED scenarios, including the real native approval-host/runas route (interactive UAC untested).')
} finally { $process.Dispose() }
