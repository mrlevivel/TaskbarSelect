param(
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SettingsFile,
    [Parameter(Mandatory = $true)][string]$StartupLink
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') {
    throw 'Installer refusal checks require a disposable GitHub-hosted Windows runner.'
}
if ($Version -cmatch '^1\.0\.0-beta\.([1-9][0-9]*)$') { $refusalRevision = [long]$Matches[1] }
else { throw 'Refusal checks require a canonical production beta version.' }
if ($refusalRevision -lt 12) { throw 'Refusal checks require the production persistent-support lifecycle contract.' }
$refusalTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if (-not $InstallDirectory.StartsWith($refusalTemp + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($InstallDirectory) -cnotmatch '^TaskbarSelect-installer-smoke-[a-f0-9]{32}$') {
    throw 'Refusal checks require the unique existing smoke installation inside RUNNER_TEMP.'
}
foreach ($refusalPath in @($InstallDirectory, $SetupPath, $EvidenceDirectory, $SettingsFile, $StartupLink)) {
    if (-not [IO.Path]::IsPathRooted($refusalPath) -or -not (Test-Path -LiteralPath $refusalPath)) { throw 'Every refusal fixture path must already exist and be absolute.' }
    for ($refusalCurrent = [IO.Path]::GetFullPath($refusalPath); $refusalCurrent; $refusalCurrent = [IO.Path]::GetDirectoryName($refusalCurrent.TrimEnd('\'))) {
        if ((Get-Item -LiteralPath $refusalCurrent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusal fixture paths cannot traverse a reparse point.' }
        if ($refusalCurrent.Length -eq 3) { break }
    }
}
$refusalApp = Join-Path $InstallDirectory 'TaskbarSelect.exe'
$refusalUninstaller = Join-Path $InstallDirectory 'unins000.exe'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($refusalApp).ProductVersion -cne $Version -or
    @(Get-Process -Name TaskbarSelect -ErrorAction SilentlyContinue).Count) { throw 'Refusal fixture requires the stopped exact candidate.' }
$refusalKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A446A470-529B-42E0-AD2D-D24B31864DFC}_is1'
function ReadRefusalRegistration {
    $refusalRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    $refusalRegistration = $null
    try {
        $refusalRegistration = $refusalRegistry.OpenSubKey($refusalKey)
        if (-not $refusalRegistration -or [IO.Path]::GetFullPath($refusalRegistration.GetValue('InstallLocation')).TrimEnd('\') -ine $InstallDirectory) { throw 'Refusal fixture registration differs from the owned installation.' }
        $refusalValues = [ordered]@{}
        foreach ($refusalName in @($refusalRegistration.GetValueNames() | Sort-Object)) {
            $refusalValues[$refusalName] = [ordered]@{ kind = $refusalRegistration.GetValueKind($refusalName).ToString(); value = $refusalRegistration.GetValue($refusalName) }
        }
        return ($refusalValues | ConvertTo-Json -Depth 8 -Compress)
    } finally { if ($refusalRegistration) { $refusalRegistration.Dispose() }; $refusalRegistry.Dispose() }
}
function RefusalHash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$refusalOriginalBytes = [IO.File]::ReadAllBytes($refusalApp)
$refusalOriginalHash = RefusalHash $refusalApp
$refusalSettingsHash = RefusalHash $SettingsFile
$refusalStartupHash = RefusalHash $StartupLink
$refusalRegistrationBefore = ReadRefusalRegistration
$refusalSnapshot = @{}
foreach ($refusalFile in @(Get-ChildItem -LiteralPath $InstallDirectory -File -Force)) { $refusalSnapshot[$refusalFile.Name] = RefusalHash $refusalFile.FullName }
$refusalSource = Join-Path $EvidenceDirectory 'RejectLifecycleFixture.cs'
$refusalExecutable = Join-Path $EvidenceDirectory 'RejectLifecycleFixture.exe'
$refusalCalls = Join-Path $EvidenceDirectory 'rejected-lifecycle-commands.txt'
foreach ($refusalNew in @($refusalSource, $refusalExecutable, $refusalCalls)) { if (Test-Path -LiteralPath $refusalNew) { throw 'Preserve prior refusal evidence; a fresh fixture is required.' } }
$refusalEscapedCalls = $refusalCalls.Replace('\', '\\').Replace('"', '\"')
$refusalSourceText = @"
using System;
using System.IO;
using System.Reflection;
[assembly: AssemblyTitle("Taskbar Select owned refusal fixture")]
[assembly: AssemblyProduct("Taskbar Select owned refusal fixture")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("$Version")]
internal static class RejectLifecycleFixture {
    private static int Main(string[] args) {
        File.AppendAllText("$refusalEscapedCalls", String.Join(" ", args) + "\n");
        return args.Length == 1 && (args[0] == "--prepare-upgrade" || args[0] == "--uninstall") ? 51 : 52;
    }
}
"@
[IO.File]::WriteAllText($refusalSource, $refusalSourceText, [Text.UTF8Encoding]::new($false))
. (Join-Path $PSScriptRoot 'Invoke-OwnedCompiler.ps1')
$null = Invoke-OwnedCompiler -Compiler (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') -CompilerArguments @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warnaserror+', ('/out:"' + $refusalExecutable + '"'), ('"' + $refusalSource + '"')) -Label 'refusal-fixture' -Source $refusalSource -EvidenceDirectory $EvidenceDirectory
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($refusalExecutable).ProductVersion -cne $Version) { throw 'The fixed refusal fixture could not be compiled with the exact candidate version.' }
$refusalChildrenDrained = $true
$refusalChecks = 0
function CheckRefusal([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:refusalChecks++
}
function RunRefusedInstaller([string]$Executable, [string]$Label) {
    $script:refusalChildrenDrained = $false
    $refusalArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $EvidenceDirectory ($Label + '-refused.log')) + '"'))
    if ($Label -ceq 'upgrade') { $refusalArguments += @('/SP-', '/CURRENTUSER', ('/DIR="' + $InstallDirectory + '"')) }
    $refusalChild = Start-Process -FilePath $Executable -ArgumentList $refusalArguments -WindowStyle Hidden -PassThru
    try {
        $null = $refusalChild.Handle
        CheckRefusal ($refusalChild.WaitForExit(60000)) "$Label refuses within 60 seconds; no forced termination"
        $refusalChild.Refresh(); $script:refusalChildrenDrained = $true
        CheckRefusal ($null -ne $refusalChild.ExitCode -and $refusalChild.ExitCode -ne 0) "$Label returns a confirmed nonzero after app lifecycle refusal"
    } finally { $refusalChild.Dispose() }
}
function CheckRetainedFixture {
    CheckRefusal ((RefusalHash $SettingsFile) -ceq $refusalSettingsHash) 'Lifecycle refusal preserves settings bytes'
    CheckRefusal ((RefusalHash $StartupLink) -ceq $refusalStartupHash) 'Lifecycle refusal preserves Startup bytes'
    CheckRefusal ((ReadRefusalRegistration) -ceq $refusalRegistrationBefore) 'Lifecycle refusal preserves every owned uninstall registration value'
    foreach ($refusalEntry in $refusalSnapshot.GetEnumerator()) {
        $refusalExpected = $refusalEntry.Value
        if ($refusalEntry.Key -ceq 'TaskbarSelect.exe') { $refusalExpected = RefusalHash $refusalExecutable }
        CheckRefusal ((RefusalHash (Join-Path $InstallDirectory $refusalEntry.Key)) -ceq $refusalExpected) ('Lifecycle refusal preserves installed file: ' + $refusalEntry.Key)
    }
}
$refusalResult = [ordered]@{ status = 'running'; checks = 0; version = $Version; actualRememberedApprovalTested = $false; interactiveUacTested = $false; ownedChildrenDrained = $false; candidateRestored = $false }
try {
    [IO.File]::WriteAllBytes($refusalApp, [IO.File]::ReadAllBytes($refusalExecutable))
    RunRefusedInstaller $SetupPath 'upgrade'
    CheckRefusal (([IO.File]::ReadAllText($refusalCalls)).Trim() -ceq '--prepare-upgrade') 'Upgrade invokes only the safe command and never falls back to restore'
    CheckRetainedFixture
    RunRefusedInstaller $refusalUninstaller 'uninstall'
    CheckRefusal (([IO.File]::ReadAllText($refusalCalls)).Replace("`r", '') -ceq "--prepare-upgrade`n--uninstall`n") 'Uninstall invokes only its fixed cleanup command'
    CheckRetainedFixture
    $refusalResult.status = 'passed'
} catch { $refusalResult.status = 'failed'; $refusalResult.failure = $_.Exception.Message; throw }
finally {
    $refusalResult.ownedChildrenDrained = $refusalChildrenDrained
    if ($refusalChildrenDrained) {
        [IO.File]::WriteAllBytes($refusalApp, $refusalOriginalBytes)
        $refusalResult.candidateRestored = (RefusalHash $refusalApp) -ceq $refusalOriginalHash
        if (-not $refusalResult.candidateRestored) { $refusalResult.status = 'failed' }
    }
    $refusalResult.checks = $refusalChecks
    $refusalResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'refusal-result.json') -Encoding UTF8
    if (-not $refusalResult.candidateRestored) { throw 'Refusal fixture could not confirm stopped children and candidate-byte restoration; preserve this disposable fixture.' }
}
Write-Output "PASS: $refusalChecks installer refusal checks. Actual remembered approval and interactive UAC are separate coverage."
