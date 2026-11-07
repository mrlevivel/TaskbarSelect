$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Installer-Guards.ps1')
$checks = 0
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}
function Reject([scriptblock]$action, [string]$message) {
    $rejected = $false
    try { & $action } catch { $rejected = $true }
    Check $rejected $message
}
foreach ($mode in @(
    @{ CandidateVersion = ''; Ref = 'refs/tags/v1.0.0-beta.8'; EventName = 'push' },
    @{ CandidateVersion = '1.0.0-beta.8'; Ref = 'refs/heads/codex/beta8-integration'; EventName = 'push' },
    @{ CandidateVersion = '1.0.0-beta.8'; Ref = 'refs/heads/codex/beta8-integration'; EventName = 'workflow_dispatch' },
    @{ CandidateVersion = '1.0.0-beta.8'; Ref = 'refs/pull/42/merge'; EventName = 'pull_request' }
)) {
    Assert-InstallerSmokeMode -Version '1.0.0-beta.8' @mode
    Check $true 'An exact version and supported hosted workflow ref is accepted'
}
Reject { Assert-InstallerSmokeMode -Version '1.0.0-beta.8' -Ref 'refs/heads/main' -EventName 'push' } 'An untagged run cannot silently bypass tag mode'
Reject { Assert-InstallerSmokeMode -Version '1.0.0-beta.8' -Ref 'refs/tags/v1.0.0-beta.7' -EventName 'push' } 'An incorrect release tag is rejected'
Reject { Assert-InstallerSmokeMode -Version '1.0.0-beta.8' -CandidateVersion '1.0.0-beta.7' -Ref 'refs/heads/main' -EventName 'push' } 'An incorrect candidate version is rejected'
Reject { Assert-InstallerSmokeMode -Version '1.0.0-beta.8' -CandidateVersion '1.0.0-beta.8' -Ref 'refs/tags/v1.0.0-beta.8' -EventName 'push' } 'Candidate mode cannot bypass release-tag mode'
Reject { Assert-InstallerSmokeMode -Version '1.0.0-beta.8' -CandidateVersion '1.0.0-beta.8' -Ref 'refs/heads/main' -EventName 'pull_request_target' } 'An unrelated workflow event is rejected'
Reject { Assert-InstallerSmokeMode -Version '1.0.0-beta.8' -CandidateVersion '1.0.0-beta.8' -Ref 'refs/pull/42/head' -EventName 'pull_request' } 'An unsupported pull-request ref is rejected'
Reject { Assert-InstallerSmokeMode -Version '1.0.0' -CandidateVersion '1.0.0' -Ref 'refs/heads/main' -EventName 'push' } 'Non-beta metadata is rejected'
Check ((Get-InstallerPreviousVersion -Filename 'TaskbarSelect-1.0.0-beta.7-Setup.exe' -Version '1.0.0-beta.8') -ceq '1.0.0-beta.7') 'The beta.7 upgrade baseline is accepted'
foreach ($filename in @('TaskbarSelect-1.0.0-beta.8-Setup.exe', 'TaskbarSelect-1.0.0-beta.9-Setup.exe', 'TaskbarSelect-2.0.0-beta.7-Setup.exe', 'TaskbarSelect-1.0.0-beta.7-fullscreen-test-Setup.exe', 'TaskbarSelect-1.0.0-beta.7-Setup.exe.extra')) {
    Reject { Get-InstallerPreviousVersion -Filename $filename -Version '1.0.0-beta.8' } 'A same/newer/different/test or malformed baseline is rejected'
}
$hash = 'a' * 64
Assert-InstallerChecksum -Lines @("$hash  TaskbarSelect-1.0.0-beta.7-Setup.exe") -Filename 'TaskbarSelect-1.0.0-beta.7-Setup.exe' -Hash $hash.ToUpperInvariant()
Check $true 'An exact checksum entry accepts case-insensitive hexadecimal'
foreach ($lines in @(
    @(),
    @("$hash  another.exe"),
    @("$hash  TaskbarSelect-1.0.0-beta.7-Setup.exe", "$hash  TaskbarSelect-1.0.0-beta.7-Setup.exe"),
    @((('b' * 64) + '  TaskbarSelect-1.0.0-beta.7-Setup.exe')),
    @("$hash  TaskbarSelect-1.0.0-beta.7-Setup.exe.extra")
)) {
    Reject { Assert-InstallerChecksum -Lines $lines -Filename 'TaskbarSelect-1.0.0-beta.7-Setup.exe' -Hash $hash } 'Missing, duplicate, unrelated or mismatched checksum entries are rejected'
}
Reject { Assert-InstallerChecksum -Lines @("$hash  TaskbarSelect-1.0.0-beta.7-Setup.exe") -Filename 'TaskbarSelect-1.0.0-beta.7-Setup.exe' -Hash 'bad' } 'An invalid expected hash is rejected'
Assert-InstallerSmokeMode -Version '1.0.0-beta.9' -Ref 'refs/tags/v1.0.0-beta.9' -EventName 'push'
Check $true 'The beta.9 release tag is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.9' -CandidateVersion '1.0.0-beta.9' -Ref 'refs/heads/codex/beta9-release' -EventName 'push'
Check $true 'The beta.9 release branch is accepted in explicit candidate mode'
Check ((Get-InstallerPreviousVersion -Filename 'TaskbarSelect-1.0.0-beta.8-Setup.exe' -Version '1.0.0-beta.9') -ceq '1.0.0-beta.8') 'The released beta.8 upgrade baseline is accepted'
foreach ($filename in @('TaskbarSelect-1.0.0-beta.9-Setup.exe', 'TaskbarSelect-1.0.0-beta.10-Setup.exe', 'TaskbarSelect-1.0.0-beta.8-fullscreen-test-Setup.exe')) {
    Reject { Get-InstallerPreviousVersion -Filename $filename -Version '1.0.0-beta.9' } 'Beta.9 rejects same/newer/test-only predecessors'
}
Check (Test-InstallerLegacyMonitorProfile -PreviousVersion '1.0.0-beta.7') 'A beta.7 fixture models missing monitor/fullscreen/height settings'
Check (-not (Test-InstallerLegacyMonitorProfile -PreviousVersion '1.0.0-beta.8')) 'A beta.8 upgrade fixture preserves existing monitor/fullscreen/height settings'
Check (-not (Test-InstallerLegacyMonitorProfile -PreviousVersion '')) 'A same-version reinstall preserves all existing settings'
Reject { Test-InstallerLegacyMonitorProfile -PreviousVersion '2.0.0-beta.7' } 'An unknown predecessor schema is not silently treated as beta.7'
Assert-InstallerSmokeMode -Version '1.0.0-beta.10' -Ref 'refs/tags/v1.0.0-beta.10' -EventName 'push'
Check $true 'The beta.10 release tag is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.10' -CandidateVersion '1.0.0-beta.10' -Ref 'refs/heads/codex/beta10-release' -EventName 'push'
Check $true 'The beta.10 release branch is accepted in explicit candidate mode'
Check ((Get-InstallerPreviousVersion -Filename 'TaskbarSelect-1.0.0-beta.9-Setup.exe' -Version '1.0.0-beta.10') -ceq '1.0.0-beta.9') 'The released beta.9 upgrade baseline is accepted'
foreach ($filename in @('TaskbarSelect-1.0.0-beta.10-Setup.exe', 'TaskbarSelect-1.0.0-beta.11-Setup.exe', 'TaskbarSelect-1.0.0-beta.9-fullscreen-test-Setup.exe')) {
    Reject { Get-InstallerPreviousVersion -Filename $filename -Version '1.0.0-beta.10' } 'Beta.10 rejects same/newer/test-only predecessors'
}
Check (Test-InstallerLegacyAppearanceProfile -PreviousVersion '1.0.0-beta.7') 'A beta.7 profile predates appearance and action controls'
Check (Test-InstallerLegacyAppearanceProfile -PreviousVersion '1.0.0-beta.8') 'A beta.8 profile predates appearance and action controls independently of monitor fields'
Check (-not (Test-InstallerLegacyAppearanceProfile -PreviousVersion '1.0.0-beta.9')) 'A beta.9 predecessor retains its existing appearance and action controls'
Check (-not (Test-InstallerLegacyAppearanceProfile -PreviousVersion '')) 'A same-version fixture does not remove current appearance choices'
foreach ($previous in @('2.0.0-beta.8', '1.0.0-beta.8-fullscreen-test', '1.0.0-beta.x')) {
    Reject { Test-InstallerLegacyAppearanceProfile -PreviousVersion $previous } 'Malformed or unknown appearance schemas fail closed'
}
foreach ($previous in @('1.0.0-beta.7', '1.0.0-beta.8', '1.0.0-beta.9', '')) {
    # Invoke the same in-memory transformation as Test-Installer.ps1 rather
    # than duplicating its policy. No profile or executable is accessed.
    $profile = [pscustomobject][ordered]@{
        CloseWindowShortcut = 0; SearchAppsShortcut = 0; FocusWindowShortcuts = @(0, 0, 0, 0); TileWindowShortcut = 0
        FocusIndicatorStyle = 2; FocusIndicatorGlow = $true; FocusIndicatorLighting = 3; FocusIndicatorFinish = 1; AccentPreset = 'Coral'
        WindowShelfActivationHeight = 42; MonitorMoveShortcuts = @(0, 0, 0, 0); AllowMovingFullscreenApps = $true
        UndoMoveShortcut = 393224; ScaleWindowSizeToMonitor = $false; WindowShelfActivationWidth = 612; UnrelatedSetting = 'retained'
    }
    $before = $profile | ConvertTo-Json -Depth 10 -Compress
    $converted = ConvertTo-InstallerPreviousProfile -Profile $profile -PreviousVersion $previous
    $appearanceAbsent = $previous -cin @('1.0.0-beta.7', '1.0.0-beta.8')
    $monitorAbsent = $previous -ceq '1.0.0-beta.7'
    foreach ($field in @('CloseWindowShortcut', 'SearchAppsShortcut', 'FocusWindowShortcuts', 'TileWindowShortcut',
        'FocusIndicatorStyle', 'FocusIndicatorGlow', 'FocusIndicatorLighting', 'FocusIndicatorFinish', 'AccentPreset')) {
        Check (($converted.PSObject.Properties.Name -contains $field) -ne $appearanceAbsent) "Only older schemas omit appearance/action property $field [$previous]"
    }
    foreach ($field in @('WindowShelfActivationHeight', 'MonitorMoveShortcuts', 'AllowMovingFullscreenApps')) {
        Check (($converted.PSObject.Properties.Name -contains $field) -ne $monitorAbsent) "Monitor/height/fullscreen fields follow their separate beta.8 introduction [$previous]"
    }
    Check ($converted.UndoMoveShortcut -eq 393224 -and -not $converted.ScaleWindowSizeToMonitor -and
        $converted.WindowShelfActivationWidth -eq 612 -and $converted.UnrelatedSetting -ceq 'retained') 'Schema projection retains unrelated preferences and existing Undo/sizing choices'
    if (-not $appearanceAbsent) {
        Check (($converted | ConvertTo-Json -Depth 10 -Compress) -ceq $before) 'Beta.9 and same-version fixture projection preserves every key, value and JSON representation'
    }
}
Reject { ConvertTo-InstallerPreviousProfile -Profile $null -PreviousVersion '1.0.0-beta.9' } 'A missing in-memory profile is rejected'
Assert-InstallerSmokeMode -Version '1.0.0-beta.12' -Ref 'refs/tags/v1.0.0-beta.12' -EventName 'push'
Check $true 'The beta.12 release tag is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.12' -CandidateVersion '1.0.0-beta.12' -Ref 'refs/heads/codex/persistent-admin-support' -EventName 'workflow_dispatch'
Check $true 'The explicit production beta.12 branch candidate is accepted'
Check ((Get-InstallerPreviousVersion -Filename 'TaskbarSelect-1.0.0-beta.11-Setup.exe' -Version '1.0.0-beta.12') -ceq '1.0.0-beta.11') 'Beta.12 accepts the actual beta.11 predecessor'
Assert-InstallerSmokeMode -Version '1.0.0-beta.13' -Ref 'refs/tags/v1.0.0-beta.13' -EventName 'push'
Check $true 'The beta.13 release tag is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.13' -CandidateVersion '1.0.0-beta.13' -Ref 'refs/heads/codex/beta13-release' -EventName 'workflow_dispatch'
Check $true 'The explicit production beta.13 branch candidate is accepted'
Check ((Get-InstallerPreviousVersion -Filename 'TaskbarSelect-1.0.0-beta.12-Setup.exe' -Version '1.0.0-beta.13') -ceq '1.0.0-beta.12') 'Beta.13 accepts the actual beta.12 predecessor'
Assert-InstallerSmokeMode -Version '1.0.0-beta.14' -Ref 'refs/tags/v1.0.0-beta.14' -EventName 'push'
Check $true 'The beta.14 release tag is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.14' -CandidateVersion '1.0.0-beta.14' -Ref 'refs/heads/codex/in-app-updates' -EventName 'workflow_dispatch'
Check $true 'The explicit production beta.14 branch candidate is accepted'
Check ((Get-InstallerPreviousVersion -Filename 'TaskbarSelect-1.0.0-beta.13-Setup.exe' -Version '1.0.0-beta.14') -ceq '1.0.0-beta.13') 'Beta.14 accepts the actual beta.13 predecessor'
Assert-InstallerSmokeMode -Version '1.0.0-beta.15' -Ref 'refs/tags/v1.0.0-beta.15' -EventName 'push'
Check $true 'The beta.15 release tag is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.15' -CandidateVersion '1.0.0-beta.15' -Ref 'refs/heads/codex/beta15-release-20261104' -EventName 'workflow_dispatch'
Check $true 'The explicit production beta.15 branch candidate is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.16' -Ref 'refs/tags/v1.0.0-beta.16' -EventName 'push'
Check $true 'The beta.16 release tag is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.16' -CandidateVersion '1.0.0-beta.16' -Ref 'refs/heads/codex/beta16-release' -EventName 'workflow_dispatch'
Check $true 'The explicit production beta.16 branch candidate is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.17' -Ref 'refs/tags/v1.0.0-beta.17' -EventName 'push'
Check $true 'The beta.17 release tag is accepted'
Assert-InstallerSmokeMode -Version '1.0.0-beta.17' -CandidateVersion '1.0.0-beta.17' -Ref 'refs/heads/main' -EventName 'workflow_dispatch'
Check $true 'The explicit production beta.17 branch candidate is accepted'
foreach ($pair in @(
    @{ Current = '1.0.0-beta.12'; Previous = '1.0.0-beta.11'; Directory = 'TaskbarSelect-beta11-baseline'; Setup = '105954c65f0fea42dec5f3be19df43226bc874a41025b3e31eeffb5eefd85c01'; Checksums = '3056e5e803505bcb8c6ac116706d1d16d406d98ddc863ba5782c43721d552b0e' },
    @{ Current = '1.0.0-beta.13'; Previous = '1.0.0-beta.12'; Directory = 'TaskbarSelect-beta12-baseline'; Setup = '7edfb663b07bdf8773302aad28650ffa472ea5538c5937664aaceb6951c4ea68'; Checksums = '5f948f76cf4baf3aa2b649494dd4103deb58cd1d3439d751c857e3f1e2a489ae' },
    @{ Current = '1.0.0-beta.14'; Previous = '1.0.0-beta.13'; Directory = 'TaskbarSelect-beta13-baseline'; Setup = '9f13d6982f98d584415517da17ff85d45b6710dde39f194b7c393ef57e301140'; Checksums = 'f70f1da9aa3ba958936ed21b5cee7cd072def3a9bd1e9acea11c9fc11f3f5b06' },
    @{ Current = '1.0.0-beta.15'; Previous = '1.0.0-beta.14'; Directory = 'TaskbarSelect-beta14-baseline'; Setup = '5e7d2213cd477bf41ab9d17bf95c53eed4910503a4d4a0fcb6825471b9171870'; Checksums = '46d881b5c61a74446d1fcb596190e9ab401bc2a625f785494297dd343fc51d9c' },
    @{ Current = '1.0.0-beta.16'; Previous = '1.0.0-beta.15'; Directory = 'TaskbarSelect-beta15-baseline'; Setup = '2f254e952f0c9d21b3822cfd779300252fdbedd675fe2d13a42cc57448e2a2fe'; Checksums = '6aee4790d74345aa1f8584cfae2437f88062f9f641a4930ceea7ab1d7a162890' },
    @{ Current = '1.0.0-beta.17'; Previous = '1.0.0-beta.16'; Directory = 'TaskbarSelect-beta16-baseline'; Setup = 'f8bc6de104f18853a91746b156307f797367d3a55110379a9768a30ae5ae413e'; Checksums = '82c80f4601e70dd495ffdea1efa0e35d9a9de46a6f77bf2ac0d31987023ef7d7' }
)) {
    $baseline = Get-InstallerLifecycleBaseline -Version $pair.Current
    Check ($baseline.Version -ceq $pair.Previous -and $baseline.Directory -ceq $pair.Directory -and $baseline.Filename -ceq ('TaskbarSelect-' + $pair.Previous + '-Setup.exe')) 'Production baseline selection binds the exact released predecessor and fixed filenames'
    $releaseTag = if ($pair.Current -ceq '1.0.0-beta.17') { 'ci-upgrade-baseline-beta16' } else { 'v' + $pair.Previous }
    Check ($baseline.ReleaseTag -ceq $releaseTag) 'Each baseline uses only its explicit same-repository release tag'
    Assert-InstallerLifecycleBaseline -Version $pair.Current -PreviousVersion $pair.Previous -SetupHash $pair.Setup.ToUpperInvariant() -ChecksumsHash $pair.Checksums.ToUpperInvariant()
    Check $true 'Exact pinned published predecessor assets are accepted case-insensitively as hashes'
    Reject { Assert-InstallerLifecycleBaseline -Version $pair.Current -PreviousVersion $pair.Current -SetupHash $pair.Setup -ChecksumsHash $pair.Checksums } 'A same-version package cannot substitute for the released predecessor'
    Reject { Assert-InstallerLifecycleBaseline -Version $pair.Current -PreviousVersion $pair.Previous -SetupHash ('0' * 64) -ChecksumsHash $pair.Checksums } 'A different Setup hash is rejected'
    Reject { Assert-InstallerLifecycleBaseline -Version $pair.Current -PreviousVersion $pair.Previous -SetupHash $pair.Setup -ChecksumsHash ('0' * 64) } 'A different checksum asset is rejected'
}
Reject { Assert-InstallerLifecycleBaseline -Version '1.0.0-beta.13' -PreviousVersion '1.0.0-beta.12' -SetupHash '180421485127d0faf393889e35a80ef53cd40a1cc0dae64316e89b2b4e975590' -ChecksumsHash 'cef286c7ad36c5ded6afc82adeadf7a99ec01c3ea5594e9fda19dc9661b61b08' } 'The fixed UNVALIDATED beta.12 diagnostic donor cannot substitute for the published release'
foreach ($unsupported in @('', '1.0.0-beta.11', '1.0.0-beta.18', '1.0.0-beta.013', '1.0.0-beta.015', '1.0.0-beta.016', '1.0.0-beta.017', '1.0.0-beta.13-test', '1.0.0-beta.14-test', '1.0.0-beta.15-test', '1.0.0-beta.16-test', '1.0.0-beta.17-test', '1.0.0-beta.15-persistent-test', '1.0.0-BETA.13', '1.0.0-BETA.15', '1.0.0-BETA.16', '1.0.0-BETA.17')) {
    Reject { Get-InstallerLifecycleBaseline -Version $unsupported } 'Unknown, noncanonical and test artifact versions cannot select a production baseline'
}
Reject { Assert-InstallerSmokeMode -Version '1.0.0-beta.13' -Ref 'refs/tags/v1.0.0-beta.12' -EventName 'push' } 'The old tag cannot admit beta.13'
foreach ($version in @('1.0.0-beta.12-persistent-test', '1.0.0-beta.12-fullscreen-test', '1.0.0-beta.13-test')) {
    Reject { Assert-InstallerSmokeMode -Version $version -CandidateVersion $version -Ref 'refs/heads/codex/persistent-admin-support' -EventName 'workflow_dispatch' } 'Test-only builds cannot enter production installer smoke'
}
foreach ($previous in @('1.0.0-beta.7', '1.0.0-beta.11', '1.0.0-beta.12', '1.0.0-beta.13', '1.0.0-beta.14', '1.0.0-beta.15', '1.0.0-beta.16', '')) {
    $beforePersistent = [pscustomobject][ordered]@{ PersistentAdministratorSupport = $true; UnrelatedSetting = 'retained' }
    $convertedPersistent = ConvertTo-InstallerPreviousProfile -Profile $beforePersistent -PreviousVersion $previous
    $expectedPresent = $previous -cin @('1.0.0-beta.12', '1.0.0-beta.13', '1.0.0-beta.14', '1.0.0-beta.15', '1.0.0-beta.16', '')
    Check (($convertedPersistent.PSObject.Properties.Name -contains 'PersistentAdministratorSupport') -eq $expectedPresent) 'The predecessor fixture models remembered support only in its actual schema'
    Check ($convertedPersistent.UnrelatedSetting -ceq 'retained') 'Persistent schema projection preserves unrelated preferences'
}
Reject { Test-InstallerLegacyPersistentProfile -PreviousVersion '1.0.0-beta.12-persistent-test' } 'Unknown persistent schema markers fail closed'
$installerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$standaloneInstaller = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-Installer.ps1'))
Check ($standaloneInstaller -cmatch "if \(\`$version -cin @\('1\.0\.0-beta\.12', '1\.0\.0-beta\.13', '1\.0\.0-beta\.14', '1\.0\.0-beta\.15', '1\.0\.0-beta\.16', '1\.0\.0-beta\.17'\)\) \{\s*\`$baselineSpec = Get-InstallerLifecycleBaseline -Version \`$version") 'The standalone production installer gate also requires the pinned released predecessor'
$installerSource = [IO.File]::ReadAllText((Join-Path $installerRoot 'installer.iss'))
$installerPolicy = [IO.File]::ReadAllText((Join-Path $installerRoot 'InstallerLifecycle.iss'))
Assert-InstallerLifecycleSource -Installer $installerSource -Policy $installerPolicy
Check $true 'Production installer wires strict predecessor selection and fail-closed cleanup gates'
Reject { Assert-InstallerLifecycleSource -Installer $installerSource.Replace('if PrepareInstalledApp() then', "if StopInstalledApp('--restore', True) then") -Policy $installerPolicy } 'A legacy restore shortcut cannot bypass persistent cleanup'
Reject { Assert-InstallerLifecycleSource -Installer $installerSource -Policy $installerPolicy.Replace('if Revision > CandidateRevision then', 'if False then') } 'A newer-version overwrite policy is rejected'
Reject { Assert-InstallerLifecycleSource -Installer $installerSource -Policy $installerPolicy.Replace('(IntToStr(Revision) = Suffix)', 'True') } 'A noncanonical or test-version dispatch policy is rejected'
$script:exitObservationCalls = 0
Check (Test-InstallerProcessHasExited { $script:exitObservationCalls++; $true }) 'A positively exited same-object observation is not a running process, even without an exit code'
Check ($script:exitObservationCalls -eq 1) 'Process liveness is observed once without polling or reopening a PID'
Check (-not (Test-InstallerProcessHasExited { $false })) 'A live process still blocks the installer fixture'
Check (-not (Test-InstallerProcessHasExited { $null })) 'Missing exit information remains blocking'
Check (-not (Test-InstallerProcessHasExited { throw 'Owned unreadable-process fixture' })) 'An unreadable process remains blocking'
Check (-not (Test-InstallerProcessHasExited { 'True' })) 'Text cannot impersonate native Boolean exit evidence'
Check (-not (Test-InstallerProcessHasExited { 1 })) 'A numeric value cannot impersonate native Boolean exit evidence'
Check (-not (Test-InstallerProcessHasExited { $true; $false })) 'Multiple conflicting observations cannot prove termination'
Check (-not (Test-InstallerProcessHasExited $null)) 'An absent observer cannot prove termination'
Write-Output "PASS: $checks installer input guard checks. No executable, installer, profile, registry or GUI was accessed."
