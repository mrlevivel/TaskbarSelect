param()
$ErrorActionPreference = 'Stop'
# Never spoof hosted fields or run this account/installer harness locally.
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows' -or
    -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -cne 'Desktop' -or $env:GITHUB_SHA -cnotmatch '^[a-f0-9]{40}$') {
    throw 'Installer CI requires genuine disposable 64-bit GitHub-hosted Windows PowerShell.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$app = Join-Path $repo 'artifacts\TaskbarSelect.exe'
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion
. (Join-Path $PSScriptRoot 'Installer-Guards.ps1')
$baselineSpec = Get-InstallerLifecycleBaseline -Version $version
$candidate = ''; if ($env:GITHUB_REF -notlike 'refs/tags/*') { $candidate = $version }
Assert-InstallerSmokeMode -Version $version -CandidateVersion $candidate -Ref $env:GITHUB_REF -EventName $env:GITHUB_EVENT_NAME
$temp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')
if (-not [IO.Path]::IsPathRooted($temp) -or -not (Test-Path -LiteralPath $temp -PathType Container)) { throw 'Existing absolute hosted RUNNER_TEMP required.' }
foreach ($path in @($repo, $temp)) {
    for ($current = $path; $current; $current = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Lifecycle fixture ancestors cannot be reparse points.' }
        if ($current.Length -eq 3) { break }
    }
}
if (@(Get-Process -Name TaskbarSelect,AdministratorPayload,AdministratorBootstrap -ErrorAction SilentlyContinue).Count) { throw 'No existing app or helper is admitted.' }
$evidence = Join-Path $repo 'artifacts\installer-ci'
$runtime = Join-Path $temp ('TaskbarSelect-installer-ci-' + [Guid]::NewGuid().ToString('N'))
if ((Test-Path -LiteralPath $evidence) -or (Test-Path -LiteralPath $runtime)) { throw 'A fresh disposable lifecycle workspace is required.' }
[void][IO.Directory]::CreateDirectory($evidence)
$fixture = Join-Path $evidence 'InstallerCiFixture.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @('InstallerCiFixture.cs', 'PersistentMaintenanceAdapterProbe.cs', 'AdministratorCiLaunchProbe.cs', 'AdministratorCiBoundaryLaunch.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$predicates = Join-Path $evidence 'InstallerCiPredicateTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /main:InstallerCiPredicateTests "/out:$predicates" @sources (Join-Path $PSScriptRoot 'InstallerCiPredicateTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Managed fixed-input lifecycle predicate compilation failed.' }
& $predicates
if ($LASTEXITCODE -ne 0) { throw 'Managed fixed-input lifecycle predicates failed; no account or installer was launched.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /main:InstallerCiFixture "/out:$fixture" @sources
if ($LASTEXITCODE -ne 0) { throw 'Hosted lifecycle fixture warning-as-error compilation failed.' }
$env:TASKBARSELECT_CI_ISCC = (Get-Command iscc.exe -ErrorAction Stop).Source
$hash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash.ToLowerInvariant()
$setup = Join-Path $repo ('dist\TaskbarSelect-' + $version + '-Setup.exe')
$checksums = Join-Path $repo 'dist\SHA256SUMS.txt'
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumsHash = (Get-FileHash -LiteralPath $checksums -Algorithm SHA256).Hash.ToLowerInvariant()
$baselineDirectory = Join-Path $temp $baselineSpec.Directory
$baseline = Join-Path $baselineDirectory $baselineSpec.Filename
$baselineChecksums = Join-Path $baselineDirectory 'SHA256SUMS.txt'
$baselineHash = (Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash.ToLowerInvariant()
$baselineChecksumsHash = (Get-FileHash -LiteralPath $baselineChecksums -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-InstallerLifecycleBaseline -Version $version -PreviousVersion $baselineSpec.Version -SetupHash $baselineHash -ChecksumsHash $baselineChecksumsHash
$process = Start-Process -FilePath $fixture -ArgumentList @('--run-ci', ('"' + $app + '"'), ('"' + $runtime + '"')) -WorkingDirectory $evidence -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $evidence 'stdout.log') -RedirectStandardError (Join-Path $evidence 'stderr.log')
try {
    $null = $process.Handle
    if (-not $process.WaitForExit(240000)) { throw 'Hosted lifecycle fixture exceeded four minutes; no cleanup or pass claim.' }
    $process.Refresh()
    foreach ($name in @('launcher.json', 'result.json', 'maintenance-adapter.json', 'installer-child.stdout.log', 'installer-child.stderr.log', 'installer-high-phase.json', 'installer-medium-phase.json')) {
        $input = Join-Path $runtime $name
        if (Test-Path -LiteralPath $input -PathType Leaf) { if ((Get-Item -LiteralPath $input).Length -gt 262144) { throw 'Lifecycle evidence exceeds its bound.' }; Copy-Item -LiteralPath $input -Destination (Join-Path $evidence $name) }
    }
    $nestedNames = @('installer-smoke/result.json', 'installer-policy/result.json', 'installer-policy/policy.log', 'installer-policy/metadata-compilers.json')
    foreach ($fixtureName in @('legacy', 'production', 'persistent-test', 'malformed', 'unicode', 'oversize')) {
        foreach ($channel in @('stdout', 'stderr')) { $nestedNames += 'installer-policy/metadata-' + $fixtureName + '.' + $channel + '.log' }
    }
    foreach ($fixtureName in @('metadata-legacy', 'metadata-production', 'metadata-persistent-test', 'metadata-malformed', 'metadata-unicode', 'metadata-oversize', 'pascal-policy', 'pascal-production-code')) {
        foreach ($suffix in @('json', 'stdout.log', 'stderr.log')) { $nestedNames += 'installer-policy/' + $fixtureName + '.' + $suffix }
    }
    foreach ($suffix in @('json', 'stdout.log', 'stderr.log')) { $nestedNames += 'installer-smoke/refusal-fixture.' + $suffix }
    $nestedNames = @($nestedNames | Select-Object -Unique)
    foreach ($name in $nestedNames) {
        $input = Join-Path $runtime ('installer-inputs/artifacts/' + $name)
        if (Test-Path -LiteralPath $input -PathType Leaf) {
            if ((Get-Item -LiteralPath $input).Length -gt 262144) { throw 'Nested lifecycle evidence exceeds its bound.' }
            $output = Join-Path $evidence $name
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
            Copy-Item -LiteralPath $input -Destination $output
        }
    }
    $launcher = Get-Content -LiteralPath (Join-Path $runtime 'launcher.json') -Raw | ConvertFrom-Json
    $result = Get-Content -LiteralPath (Join-Path $runtime 'result.json') -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $result.status -cne 'passed') { throw 'Hosted lifecycle child failed; retained adapter/child/compiler receipts identify the original stage. No successful installer or cleanup claim.' }
    $adapter = Get-Content -LiteralPath (Join-Path $runtime 'maintenance-adapter.json') -Raw | ConvertFrom-Json
    $smokePath = Join-Path $runtime 'installer-inputs\artifacts\installer-smoke\result.json'
    if ((Get-Item -LiteralPath $smokePath).Length -gt 65536) { throw 'Installer receipt exceeds its bound.' }
    $smoke = Get-Content -LiteralPath $smokePath -Raw | ConvertFrom-Json
    $probe = $launcher.Probe
    if ($process.ExitCode -ne 0 -or $result.status -cne 'passed' -or -not $result.adapterIoPassed -or -not $result.genuineMediumInstallerPassed -or
        $probe.Status -cne 'passed' -or -not $probe.HighHostedChainGrantAuthenticated -or -not $probe.SourceMediumDuplicateVerified -or
        -not $probe.Drained -or $probe.RemainingJobProcesses -ne 0 -or $probe.HighExit -ne 0 -or $probe.SponsorExit -ne 0 -or
        -not $probe.ProtectedSourceRemoved -or -not $launcher.AccountRemoved -or $launcher.CleanupUncertain -or $probe.UnknownHighLaunch -or $probe.UnknownSponsorLaunch) { throw 'Actual MED/HIGH lifecycle and complete owned drain are required.' }
    if ($adapter.status -cne 'passed' -or $adapter.hostIntegrity -ne 12288 -or $adapter.hostElevated -ne 1 -or $adapter.linkedIntegrity -ne 8192 -or
        $adapter.linkedElevated -ne 0 -or -not $adapter.linkedSameUser -or -not $adapter.ownedFixedFilesRemoved -or @($adapter.cases).Count -ne 8 -or
        -not $adapter.adapterIoOnly -or $adapter.productionAuthenticationTested -or $adapter.taskSchedulerTested -or $adapter.uacTested) { throw 'Bounded actual protected-adapter I/O proof is required.' }
    $expectedAdapter = @('protected-pair-create', 'scope-read-lease-refusal', 'native-read-lease-refusal', 'ledger-read-lease-refusal', 'native-delete-ledger-retained', 'ledger-only-retry-delete', 'interrupted-ledger-first-create-retry', 'same-scope-new-build-and-empty')
    for ($index = 0; $index -lt $expectedAdapter.Count; $index++) { if ($adapter.cases[$index] -cne $expectedAdapter[$index]) { throw 'A required ordered maintenance case is absent.' } }
    if ($smoke.status -cne 'passed' -or $smoke.checks -lt 120 -or -not $smoke.installerRefusalFixturePassed -or $smoke.rememberedApprovalLifecycleExecuted -or
        $smoke.interactiveUacTested -or $result.interactiveUacTested -or $result.rebootTested -or $result.rememberedApprovalLifecycleExecuted) { throw 'Production installer receipt or honest coverage labels differ.' }
    foreach ($receipt in @($result, $adapter, $smoke)) { if ($receipt.sourceSha -cne $env:GITHUB_SHA) { throw 'Source-bound lifecycle evidence differs.' } }
    if ($result.appSha256 -cne $hash -or $adapter.appSha256 -cne $hash -or $smoke.exeSha256.ToLowerInvariant() -cne $hash -or $probe.BoundaryAppSha256 -cne $hash -or
        (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'Artifact-bound lifecycle evidence differs.' }
    if ($result.version -cne $version -or $smoke.version -cne $version -or $smoke.setupSha256.ToLowerInvariant() -cne $setupHash -or $smoke.previousSetupSha256.ToLowerInvariant() -cne $baselineHash -or $smoke.upgradeFrom -cne $baselineSpec.Version -or
        (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() -cne $setupHash -or
        (Get-FileHash -LiteralPath $checksums -Algorithm SHA256).Hash.ToLowerInvariant() -cne $checksumsHash -or
        (Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash.ToLowerInvariant() -cne $baselineHash -or
        (Get-FileHash -LiteralPath $baselineChecksums -Algorithm SHA256).Hash.ToLowerInvariant() -cne $baselineChecksumsHash) { throw 'Final Setup/checksum or pinned predecessor changed during lifecycle validation.' }
    Write-Output ('PASS: Genuine MED production installer lifecycle: ' + $smoke.checks + ' checks; genuine HIGH/MED production maintenance adapter: ' + $adapter.checks + ' checks. Interactive UAC/reboot and remembered production approval remain separate gates.')
} finally { $process.Dispose() }
