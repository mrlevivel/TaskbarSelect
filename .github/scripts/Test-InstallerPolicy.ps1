param()
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') {
    throw 'Executable installer policy checks require a disposable GitHub-hosted Windows runner.'
}
$policyTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
if (-not [IO.Path]::IsPathRooted($policyTemp) -or -not (Test-Path -LiteralPath $policyTemp -PathType Container)) { throw 'Existing absolute RUNNER_TEMP is required.' }
$policyOutput = Join-Path $policyTemp ('TaskbarSelect-installer-policy-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $policyOutput) { throw 'The installer policy output directory must be fresh.' }
$null = [IO.Directory]::CreateDirectory($policyOutput)
$policyMetadata = Join-Path $policyOutput 'metadata'
$null = [IO.Directory]::CreateDirectory($policyMetadata)
$policyRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $PSScriptRoot 'Installer-Guards.ps1')
$policyProductionVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $policyRepo 'artifacts\TaskbarSelect.exe')).ProductVersion
$null = Get-InstallerLifecycleBaseline -Version $policyProductionVersion
$policyEvidence = Join-Path $policyRepo 'artifacts\installer-policy'
if (Test-Path -LiteralPath $policyEvidence) { throw 'Preserve prior policy evidence; a fresh checkout is required.' }
$null = [IO.Directory]::CreateDirectory($policyEvidence)
. (Join-Path $PSScriptRoot 'Invoke-OwnedCompiler.ps1')
$policyCompilerReceipts = @()
$policyFixtures = [ordered]@{
    legacy = '1.0.0-beta.11'; production = '1.0.0-beta.12'; 'persistent-test' = '1.0.0-beta.12-persistent-test'
    malformed = '1.0.0-beta.012'; unicode = ('1.0.0-beta.12-' + [char]0x96EA); oversize = ('1.0.0-beta.12-' + ('a' * 150))
}
foreach ($policyFixture in $policyFixtures.GetEnumerator()) {
    $policySource = Join-Path $policyMetadata ($policyFixture.Key + '.cs')
    $policyText = 'using System.Reflection; [assembly: AssemblyFileVersion("1.0.0.12")] [assembly: AssemblyInformationalVersion("' + $policyFixture.Value + '")] public sealed class OwnedMetadataFixture {}'
    [IO.File]::WriteAllText($policySource, $policyText, [Text.UTF8Encoding]::new($true))
    $policyCompilerReceipts += Invoke-OwnedCompiler -Compiler (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') -CompilerArguments @('/nologo', '/target:library', '/platform:x64', '/warnaserror+', ('/out:"' + (Join-Path $policyMetadata ($policyFixture.Key + '.dll')) + '"'), ('"' + $policySource + '"')) -Label ('metadata-' + $policyFixture.Key) -Source $policySource -EvidenceDirectory $policyEvidence
    $policyCompilerReceipts | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $policyEvidence 'metadata-compilers.json') -Encoding UTF8
}
[IO.File]::WriteAllText((Join-Path $policyMetadata 'non-pe.txt'), 'This is an owned non-executable fixture.')
$policyCompiler = Get-Command iscc.exe -ErrorAction Stop
$null = Invoke-OwnedCompiler -Compiler $policyCompiler.Source -CompilerArguments @(('/O"' + $policyOutput + '"'), ('"' + (Join-Path $PSScriptRoot 'InstallerLifecyclePolicy.iss') + '"')) -Label 'pascal-policy' -Source (Join-Path $PSScriptRoot 'InstallerLifecyclePolicy.iss') -EvidenceDirectory $policyEvidence
$policyInstallerSource = [IO.File]::ReadAllText((Join-Path $policyRepo 'installer.iss'))
$policyCode = $policyInstallerSource.Substring($policyInstallerSource.IndexOf('[Code]', [StringComparison]::Ordinal))
$policyCode = $policyCode.Replace('#include "InstallerLifecycle.iss"', ('#include "' + (Join-Path $policyRepo 'InstallerLifecycle.iss') + '"'))
$policyCompileHeader = @'
#define AppVersion "__POLICY_PRODUCTION_VERSION__"
#define AppExeName "TaskbarSelect.exe"
[Setup]
AppName=Taskbar Select compile-only lifecycle fixture
AppVersion=0
DefaultDirName={tmp}\TaskbarSelectCompileOnlyFixture
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
OutputBaseFilename=InstallerLifecycleCompileOnlyFixture
'@
$policyCompileHeader = $policyCompileHeader.Replace('__POLICY_PRODUCTION_VERSION__', $policyProductionVersion)
$policyCompileSource = Join-Path $policyOutput 'InstallerLifecycleCompileOnlyFixture.iss'
[IO.File]::WriteAllText($policyCompileSource, $policyCompileHeader + "`r`n" + $policyCode + "`r`nfunction InitializeSetup(): Boolean;`r`nbegin`r`n Result := False;`r`nend;`r`n", [Text.UTF8Encoding]::new($false))
$null = Invoke-OwnedCompiler -Compiler $policyCompiler.Source -CompilerArguments @(('/O"' + $policyOutput + '"'), ('"' + $policyCompileSource + '"')) -Label 'pascal-production-code' -Source $policyCompileSource -EvidenceDirectory $policyEvidence
$policyExecutable = Join-Path $policyOutput 'InstallerLifecyclePolicyFixture.exe'
$policyImage = [IO.File]::ReadAllBytes($policyExecutable)
if ($policyImage.Length -lt 64) { throw 'The policy runtime PE header is missing.' }
$policyPeOffset = [BitConverter]::ToInt32($policyImage, 60)
if ($policyPeOffset -lt 64 -or $policyPeOffset -gt $policyImage.Length - 6 -or
    [BitConverter]::ToUInt32($policyImage, $policyPeOffset) -ne 0x4550 -or
    [BitConverter]::ToUInt16($policyImage, $policyPeOffset + 4) -ne 0x14c) {
    throw 'The Inno runtime architecture changed; review the 32-bit Windows metadata bindings before execution.'
}
$policyLog = Join-Path $policyOutput 'policy.log'
$policyReceipt = [ordered]@{
    status = 'failed'; sourceSha = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID
    policySha256 = (Get-FileHash -LiteralPath (Join-Path $policyRepo 'InstallerLifecycle.iss') -Algorithm SHA256).Hash.ToLowerInvariant()
    fixtureSourceSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'InstallerLifecyclePolicy.iss') -Algorithm SHA256).Hash.ToLowerInvariant()
    fixtureExeSha256 = (Get-FileHash -LiteralPath $policyExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    productionCodeCompilePassed = $true; productionCodeFixtureExecuted = $false
    policyCases = 0; uninstallPolicyCases = 0; boundsCases = 0; metadataCases = 0; installerRuntimeMachine = 'I386'; fixtureExecuted = $false; fixtureDrained = $false
    applicationInstallationExecuted = $false; productionPackageProduced = $false; rememberedApprovalLifecycleExecuted = $false; interactiveUacTested = $false
}
$policyProcess = $null
try {
    $policyProcess = Start-Process -FilePath $policyExecutable -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/MetadataDir="' + $policyMetadata + '"'), ('/LOG="' + $policyLog + '"')) -WindowStyle Hidden -PassThru
    $null = $policyProcess.Handle
    $policyReceipt.fixtureExecuted = $true
    if (-not $policyProcess.WaitForExit(30000)) { throw 'Installer policy fixture did not exit; it was not force-terminated.' }
    $policyProcess.Refresh()
    $policyReceipt.fixtureDrained = $true
    if ($null -eq $policyProcess.ExitCode -or $policyProcess.ExitCode -ne 0) { throw "Installer policy fixture rejected a case or has an unknown exit: $($policyProcess.ExitCode)." }
    $policyResult = @(Get-Content -LiteralPath $policyLog | Where-Object { $_ -cmatch 'PASS: 33 actual Pascal installer lifecycle policy cases\.' })
    if ($policyResult.Count -ne 1) { throw 'The exact Pascal policy success record is missing.' }
    $policyUninstallResult = @(Get-Content -LiteralPath $policyLog | Where-Object { $_ -cmatch 'PASS: 17 actual Pascal uninstaller lifecycle policy cases\.' })
    if ($policyUninstallResult.Count -ne 1) { throw 'The exact uninstaller policy success record is missing.' }
    $policyBoundsResult = @(Get-Content -LiteralPath $policyLog | Where-Object { $_ -cmatch 'PASS: 7 actual returned-pointer bounds cases\.' })
    if ($policyBoundsResult.Count -ne 1) { throw 'The returned-pointer bounds success record is missing.' }
    $policyMetadataResult = @(Get-Content -LiteralPath $policyLog | Where-Object { $_ -cmatch 'PASS: 9 actual ProductVersion metadata cases\.' })
    if ($policyMetadataResult.Count -ne 1) { throw 'The actual ProductVersion metadata success record is missing.' }
    $policyReceipt.status = 'passed'; $policyReceipt.policyCases = 33; $policyReceipt.uninstallPolicyCases = 17; $policyReceipt.boundsCases = 7; $policyReceipt.metadataCases = 9
    Write-Output 'PASS: 33 actual Pascal installer lifecycle policy cases. No app, profile, task or installation was accessed.'
    Write-Output 'PASS: 17 actual Pascal uninstaller lifecycle policy cases. Downgraded and mismatched executables cannot certify cleanup.'
    Write-Output 'PASS: 7 actual returned-pointer bounds cases. Out-of-range and wraparound pointers are refused.'
    Write-Output 'PASS: 9 actual ProductVersion metadata cases. No numeric-version fallback or predecessor execution.'
} finally {
    if ($policyProcess) { $policyProcess.Dispose() }
    if ((Test-Path -LiteralPath $policyLog) -and (Get-Item -LiteralPath $policyLog).Length -le 131072) { Copy-Item -LiteralPath $policyLog -Destination (Join-Path $policyEvidence 'policy.log') }
    $policyReceipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $policyEvidence 'result.json') -Encoding UTF8
}
