param([string]$CandidateVersion, [string]$PreviousInstallerPath)
$ErrorActionPreference = 'Stop'
# This lifecycle check is intentionally unavailable on local or self-hosted desktops.
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') {
    throw 'Installer smoke requires a disposable GitHub-hosted Windows runner.'
}
. (Join-Path $PSScriptRoot 'Installer-Guards.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
if (-not [Environment]::Is64BitProcess -or -not (Test-Path -LiteralPath $runnerTemp -PathType Container)) { throw 'A 64-bit runner and existing RUNNER_TEMP are required.' }
$installDir = Join-Path $runnerTemp ('TaskbarSelect-installer-smoke-' + [Guid]::NewGuid().ToString('N'))
$localData = [Environment]::GetFolderPath('LocalApplicationData')
$startupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup, [Environment+SpecialFolderOption]::DoNotVerify)
if (-not [IO.Path]::IsPathRooted($localData) -or -not [IO.Path]::IsPathRooted($startupDir)) { throw 'Absolute current-user profile paths are required.' }
$profileDir = Join-Path $localData 'TaskbarSelect'
$settingsFile = Join-Path $profileDir 'settings.json'
$startupLink = Join-Path $startupDir 'Taskbar Select.lnk'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A446A470-529B-42E0-AD2D-D24B31864DFC}_is1'
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$checks = 0; $gui = $null; $evidence = $null
function Check($condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++; Write-Host "PASS: $message"
}
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function SamePath([string]$a, [string]$b) { [String]::Equals([IO.Path]::GetFullPath($a).TrimEnd('\'), [IO.Path]::GetFullPath($b).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) }
function RegKey([Microsoft.Win32.RegistryHive]$hive, [Microsoft.Win32.RegistryView]$view, [string]$path) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
    try { return $base.OpenSubKey($path) } finally { $base.Dispose() }
}
function NoOtherApp([int]$allowed = -1) {
    $observed = @(Get-Process -Name TaskbarSelect -ErrorAction SilentlyContinue)
    try {
        $others = @($observed | Where-Object {
            if ($_.Id -eq $allowed) { return $false }
            $candidate = $_
            # An exited process can remain enumerable while administrative
            # handles drain. Read this same object; never reopen by PID, wait
            # away an unrelated live process or coerce unknown exit evidence.
            -not (Test-InstallerProcessHasExited { $candidate.Refresh(); $candidate.HasExited })
        })
        if ($others.Count -ne 0) {
            foreach ($other in $others) {
                $exited = 'unavailable'; $exitCode = 'unavailable'
                try { $other.Refresh(); $exited = $other.HasExited; if ($exited) { $exitCode = $other.ExitCode } } catch { }
                Write-Host ("Unexpected TaskbarSelect process: pid={0}; hasExited={1}; exitCode={2}" -f $other.Id, $exited, $exitCode)
            }
        }
        Check ($others.Count -eq 0) 'No pre-existing or unrelated TaskbarSelect process'
    } finally { foreach ($other in $observed) { $other.Dispose() } }
}
function RunBounded([string]$path, [string[]]$arguments, [int]$milliseconds, [string]$label) {
    $process = Start-Process -FilePath $path -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        $null = $process.Handle
        Check ($process.WaitForExit($milliseconds)) "$label exits within its deadline (no forced termination)"
        $process.Refresh(); Check ($null -ne $process.ExitCode -and $process.ExitCode -eq 0) "$label exits successfully"
    } finally { $process.Dispose() }
}
function StartOwnedGui([string]$path, [string[]]$arguments = @()) {
    $process = Start-Process -FilePath $path -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $null = $process.Handle
    $started = $process.StartTime.ToUniversalTime().Ticks
    $watch = [Diagnostics.Stopwatch]::StartNew(); $ready = $false
    while ($watch.ElapsedMilliseconds -lt 20000) {
        $process.Refresh(); if ($process.HasExited) { throw 'Installed GUI exited before readiness.' }
        if ($process.Responding -and $process.WaitForInputIdle(0)) { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    Check $ready 'Installed GUI becomes responsive and input-idle within 20 seconds'
    Check (SamePath $process.MainModule.FileName $path) 'GUI process belongs to the installed EXE'
    Check ($process.StartTime.ToUniversalTime().Ticks -eq $started) 'GUI process identity remains unchanged'
    NoOtherApp $process.Id
    return $process
}
# All profile/registration checks precede every mutation, including evidence creation.
NoOtherApp
foreach ($path in @($profileDir, (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Taskbar Select'), $startupLink,
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Taskbar Select.lnk'), (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Taskbar Select.lnk'), $installDir)) {
    Check (-not (Test-Path -LiteralPath $path)) "No pre-existing app path: $path"
}
foreach ($hive in @('CurrentUser', 'LocalMachine')) { foreach ($view in @('Registry64', 'Registry32')) {
    $key = RegKey $hive $view $uninstallKey
    try { Check ($null -eq $key) "No pre-existing AppId in $hive/$view" } finally { if ($key) { $key.Dispose() } }
    $key = RegKey $hive $view $runKey
    try { Check ($null -eq $key -or $key.GetValueNames() -notcontains 'TaskbarSelect') "No legacy Run entry in $hive/$view" } finally { if ($key) { $key.Dispose() } }
} }
foreach ($approval in @('StartupFolder', 'Run', 'Run32')) {
    $key = RegKey CurrentUser Registry64 ('Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\' + $approval)
    try { Check ($null -eq $key -or ($key.GetValueNames() -notcontains 'TaskbarSelect' -and $key.GetValueNames() -notcontains 'Taskbar Select.lnk')) "No pre-existing startup approval in $approval" }
    finally { if ($key) { $key.Dispose() } }
}
$artifactExe = Join-Path $repoRoot 'artifacts\TaskbarSelect.exe'
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($artifactExe).ProductVersion
Assert-InstallerSmokeMode -Version $version -CandidateVersion $CandidateVersion -Ref $env:GITHUB_REF -EventName $env:GITHUB_EVENT_NAME
Check $true 'Smoke version matches the explicit candidate version or beta release tag'
$setup = Join-Path $repoRoot "dist\TaskbarSelect-$version-Setup.exe"
$setupHash = Hash $setup; $exeHash = Hash $artifactExe
Assert-InstallerChecksum -Lines (Get-Content -LiteralPath (Join-Path $repoRoot 'dist\SHA256SUMS.txt')) -Filename ([IO.Path]::GetFileName($setup)) -Hash $setupHash
Check $true 'Setup hash matches distribution checksums'
$previousVersion = $null; $previousHash = $null
$baselineSpec = $null
if ($version -cin @('1.0.0-beta.12', '1.0.0-beta.13', '1.0.0-beta.14', '1.0.0-beta.15', '1.0.0-beta.16', '1.0.0-beta.17', '1.0.0-beta.18')) {
    $baselineSpec = Get-InstallerLifecycleBaseline -Version $version
    if (-not $PreviousInstallerPath) { throw 'Production lifecycle validation requires its exact released predecessor.' }
}
if ($PreviousInstallerPath) {
    if (-not [IO.Path]::IsPathRooted($PreviousInstallerPath)) { throw 'Previous installer requires an absolute runner-temporary path.' }
    $PreviousInstallerPath = [IO.Path]::GetFullPath($PreviousInstallerPath)
    if (-not $PreviousInstallerPath.StartsWith($runnerTemp.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Previous installer must be inside RUNNER_TEMP.' }
    $previousVersion = Get-InstallerPreviousVersion -Filename ([IO.Path]::GetFileName($PreviousInstallerPath)) -Version $version
    $previousHash = Hash $PreviousInstallerPath
    $previousChecksums = Join-Path ([IO.Path]::GetDirectoryName($PreviousInstallerPath)) 'SHA256SUMS.txt'
    if ($baselineSpec) {
        Check ($previousVersion -ceq $baselineSpec.Version) 'Production lifecycle uses its exact released upgrade predecessor'
        Check ($previousHash.ToLowerInvariant() -ceq $baselineSpec.SetupSha256) 'Released predecessor Setup matches the pinned published asset'
        Check ((Hash $previousChecksums).ToLowerInvariant() -ceq $baselineSpec.ChecksumsSha256) 'Released predecessor checksum asset matches the pinned published asset'
    }
    Assert-InstallerChecksum -Lines (Get-Content -LiteralPath $previousChecksums) -Filename ([IO.Path]::GetFileName($PreviousInstallerPath)) -Hash $previousHash
    Check $true 'Earlier beta installer matches its adjacent release checksum'
}
$legacyMonitorProfile = Test-InstallerLegacyMonitorProfile -PreviousVersion $previousVersion
$legacyAppearanceProfile = Test-InstallerLegacyAppearanceProfile -PreviousVersion $previousVersion
$legacyPersistentProfile = Test-InstallerLegacyPersistentProfile -PreviousVersion $previousVersion
& (Join-Path $PSScriptRoot 'Test-InstallerGuards.ps1')
& (Join-Path $PSScriptRoot 'Test-InstallerPolicy.ps1')
$evidence = Join-Path $repoRoot 'artifacts\installer-smoke'
$null = New-Item -ItemType Directory -Path $evidence
$smokeIdentityAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($artifactExe))
$smokeIdentityType = $smokeIdentityAssembly.GetType('TaskbarSelect.NativeProcessIdentity', $true)
$smokeIdentityFlags = [Reflection.BindingFlags]'Static,NonPublic'
$smokeIdentity = $smokeIdentityType.GetMethod('Read', $smokeIdentityFlags, $null, [Type[]]@([uint32]), $null).Invoke($null, [object[]]@([uint32]$PID))
$smokeIdentityFields = [Reflection.BindingFlags]'Instance,NonPublic'
$smokeIntegrityAvailable = [bool]$smokeIdentity.GetType().GetField('IntegrityAvailable', $smokeIdentityFields).GetValue($smokeIdentity)
$smokeIntegrity = [uint32]$smokeIdentity.GetType().GetField('IntegrityLevel', $smokeIdentityFields).GetValue($smokeIdentity)
$smokeElevationAvailable = [bool]$smokeIdentity.GetType().GetField('ElevationAvailable', $smokeIdentityFields).GetValue($smokeIdentity)
$smokeElevated = [bool]$smokeIdentity.GetType().GetField('Elevated', $smokeIdentityFields).GetValue($smokeIdentity)
if (-not $smokeIntegrityAvailable -or $smokeIntegrity -ne 8192 -or -not $smokeElevationAvailable -or $smokeElevated) {
    @{ status = 'blocked'; sourceSha = $env:GITHUB_SHA; version = $version; reason = 'Disposable installer lifecycle requires a genuine MED non-elevated launcher; production checks are not weakened for a HIGH runner.'; launcherIntegrity = $smokeIntegrity; launcherElevated = $smokeElevated; installerExecuted = $false; rememberedApprovalLifecycleExecuted = $false } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding UTF8
    throw 'BLOCKED: A genuine MED non-elevated disposable installer launcher is unavailable.'
}
$result = [ordered]@{ status = 'running'; version = $version; sourceSha = $env:GITHUB_SHA; setupSha256 = $setupHash; exeSha256 = $exeHash; installDirectory = $installDir; candidateMode = [bool]$CandidateVersion; upgradeFrom = $version; previousSetupSha256 = $previousHash; checks = 0; rememberedApprovalLifecycleExecuted = $false; interactiveUacTested = $false; installerRefusalFixturePassed = $false }
try {
    RunBounded $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', '/CURRENTUSER', ('/DIR="' + $installDir + '"'), ('/LOG="' + (Join-Path $evidence 'setup.log') + '"')) 60000 'Setup'
    $sources = [ordered]@{ 'TaskbarSelect.exe' = 'artifacts\TaskbarSelect.exe'; 'README.md' = 'docs\package\README.md'; 'RELEASE_NOTES.md' = 'RELEASE_NOTES.md'; 'TESTING.md' = 'TESTING.md'; 'SECURITY.md' = 'SECURITY.md'; 'CHANGELOG.md' = 'CHANGELOG.md'; 'LICENSE' = 'LICENSE'; 'ARTWORK.md' = 'assets\ARTWORK.md' }
    foreach ($entry in $sources.GetEnumerator()) { Check ((Hash (Join-Path $installDir $entry.Key)) -eq (Hash (Join-Path $repoRoot $entry.Value))) "Installed payload matches its source: $($entry.Key)" }
    $installedExe = Join-Path $installDir 'TaskbarSelect.exe'; $uninstaller = Join-Path $installDir 'unins000.exe'
    Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).ProductVersion -ceq $version -and (Hash $installedExe) -eq $exeHash) 'Installed EXE version and hash match packaged artifact'
    . (Join-Path $PSScriptRoot 'Package-Guards.ps1')
    $productionOutput = Join-Path $evidence 'installed-production.txt'
    $productionErrors = Join-Path $evidence 'installed-production-errors.txt'
    $production = Start-Process -FilePath $installedExe -ArgumentList '--verify-production' -WindowStyle Hidden -PassThru -RedirectStandardOutput $productionOutput -RedirectStandardError $productionErrors
    try {
        $null = $production.Handle
        Check ($production.WaitForExit(10000)) 'Installed production verification exits within ten seconds'
        $production.Refresh()
        if ($null -eq $production.ExitCode) { throw 'Installed production verification exit is unknown.' }
        Assert-PackageProductionInvariant -ExitCode $production.ExitCode -Output (Get-Content -LiteralPath $productionOutput -Raw) -ErrorOutput (Get-Content -LiteralPath $productionErrors -Raw)
        Check $true 'Installed EXE verifies the normal production profile and lifecycle'
    } finally { $production.Dispose() }
    $lifecycleOutput = Join-Path $evidence 'installed-lifecycle.txt'
    $lifecycleErrors = Join-Path $evidence 'installed-lifecycle-errors.txt'
    $lifecycle = Start-Process -FilePath $installedExe -ArgumentList '--verify-installer-lifecycle' -WindowStyle Hidden -PassThru -RedirectStandardOutput $lifecycleOutput -RedirectStandardError $lifecycleErrors
    try {
        $null = $lifecycle.Handle
        Check ($lifecycle.WaitForExit(10000)) 'Installed lifecycle capability exits within ten seconds'
        $lifecycle.Refresh()
        if ($null -eq $lifecycle.ExitCode) { throw 'Installed lifecycle verification exit is unknown.' }
        Assert-PackageInstallerLifecycle -ExitCode $lifecycle.ExitCode -Output (Get-Content -LiteralPath $lifecycleOutput -Raw) -ErrorOutput (Get-Content -LiteralPath $lifecycleErrors -Raw)
        Check $true 'Installed EXE verifies the safe upgrade and uninstall contract'
    } finally { $lifecycle.Dispose() }
    $key = RegKey CurrentUser Registry64 $uninstallKey
    try {
        Check ($null -ne $key) 'Installer creates the expected per-user uninstall registration'
        Check (SamePath $key.GetValue('InstallLocation') $installDir) 'Uninstall registration belongs to the unique test directory'
        Check ($key.GetValue('UninstallString').Trim('"') -ieq $uninstaller -and $key.GetValue('DisplayVersion') -ceq $version) 'Uninstall command and version match this installation'
    } finally { if ($key) { $key.Dispose() } }
    $uninstallerHash = Hash $uninstaller
    NoOtherApp
    # Showing this owned GUI is permitted only by the disposable-host guards above.
    $gui = Start-Process -FilePath $installedExe -WindowStyle Normal -PassThru
    $null = $gui.Handle
    $started = $gui.StartTime.ToUniversalTime().Ticks; $watch = [Diagnostics.Stopwatch]::StartNew(); $ready = $false
    while ($watch.ElapsedMilliseconds -lt 20000) {
        $gui.Refresh(); if ($gui.HasExited) { throw 'Installed GUI exited before readiness.' }
        if ($gui.MainWindowHandle -ne [IntPtr]::Zero -and $gui.Responding -and $gui.WaitForInputIdle(0)) { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    Check $ready 'Installed GUI creates a responding main window within 20 seconds'
    Check (SamePath $gui.MainModule.FileName $installedExe) 'GUI process belongs to the installed EXE'
    Check ($gui.StartTime.ToUniversalTime().Ticks -eq $started -and (Hash $installedExe) -eq $exeHash) 'GUI identity and installed image remain unchanged'
    NoOtherApp $gui.Id
    $stopClock = [Diagnostics.Stopwatch]::StartNew()
    RunBounded $installedExe @('--restore') 10000 'Installed restore'
    Check ($gui.WaitForExit([Math]::Max(0, 10000 - [int]$stopClock.ElapsedMilliseconds))) 'Installed GUI stops through restore within the same 10-second deadline'
    $gui.Dispose(); $gui = $null
    NoOtherApp
    Check (-not (Test-Path -LiteralPath $settingsFile) -and -not (Test-Path -LiteralPath $startupLink)) 'First launch and restore leave settings and startup opt-in absent'
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($installedExe))
    $settingsType = $assembly.GetType('TaskbarSelect.Settings', $true); $programType = $assembly.GetType('TaskbarSelect.Program', $true)
    $flags = [Reflection.BindingFlags]'Static,NonPublic'
    $load = $programType.GetMethod('Load', $flags, $null, [Type[]]@([string]), $null)
    # PS 5.1 array expressions can retain PSObject wrappers rejected by reflection.
    $loadArguments = [object[]]::new(1)
    $loadArguments.SetValue([string]$settingsFile, 0)
    Check ($loadArguments[0].GetType() -eq [string]) 'Settings load receives a native string path'
    $model = $load.Invoke($null, $loadArguments)
    Check ($model.WindowShelfActivationWidth -eq 480 -and $model.WindowShelfHoverDelayMs -eq 100) 'Installed model uses 480px and 100ms defaults'
    Check ($model.WindowShelfActivationHeight -eq 6 -and $model.UndoMoveShortcut -eq 196616) 'Missing-file fresh model uses height6 and Ctrl+Shift+Backspace Undo'
    Check ($model.AccentPreset -ceq 'Forest Green' -and $model.FocusIndicatorStyle -eq 2 -and $model.FocusIndicatorFinish -eq 0 -and
        -not $model.FocusIndicatorGlow -and $model.FocusIndicatorLighting -eq 0) 'Fresh model uses Forest Green, Muted edge and no frame lighting'
    Check ($model.MinimizeWindowShortcut -eq 196685 -and $model.PinWindowShortcut -eq 196688) 'Fresh model uses Ctrl+Shift+M minimize and Ctrl+Shift+P pin'
    # Constructor defaults are the legacy JSON fallback, not missing-file defaults.
    $legacyModel = [Activator]::CreateInstance($settingsType)
    Check ($legacyModel.WindowShelfActivationHeight -eq 24 -and $legacyModel.AccentPreset -ceq 'Original' -and
        $legacyModel.FocusIndicatorStyle -eq 0 -and $legacyModel.FocusIndicatorLighting -eq -1) 'Legacy constructor retains height24 and opt-in appearance fallback'
    $copy = $settingsType.GetMethod('Copy', [Reflection.BindingFlags]'Instance,NonPublic')
    Check ($copy.Invoke($legacyModel.PSObject.BaseObject, $null).WindowShelfActivationHeight -eq 24) 'Serialized explicit height24 remains unchanged'
    $legacyModel.WindowShelfActivationHeight = 1
    $legacyModel.MinimizeWindowShortcut = 393293; $legacyModel.PinWindowShortcut = 0
    $customModel = $copy.Invoke($legacyModel.PSObject.BaseObject, $null)
    Check ($customModel.WindowShelfActivationHeight -eq 1 -and $customModel.MinimizeWindowShortcut -eq 393293 -and
        $customModel.PinWindowShortcut -eq 0) 'Settings serialization retains height1, custom minimize and pin Off'
    $legacyModel.WindowShelfActivationHeight = 0
    Check ($legacyModel.WindowShelfActivationHeight -eq 1) 'Activation height clamps below-range input to the one-pixel minimum'
    $legacyModel.MinimizeWindowShortcut = 0; $legacyModel.PinWindowShortcut = 393296
    $customModel = $copy.Invoke($legacyModel.PSObject.BaseObject, $null)
    Check ($customModel.MinimizeWindowShortcut -eq 0 -and $customModel.PinWindowShortcut -eq 393296) 'Settings serialization retains minimize Off and custom pin'
    Check $model.ScaleWindowSizeToMonitor 'Fresh model defaults to relative size and position'
    Check ($model.HiddenIdentities.Count -eq 0 -and $model.WindowShelfIdentities.Count -eq 0 -and -not $model.MoveWindowsToClickedTaskbar) 'Fresh model leaves taskbar hiding, Ghost Bar and movement opt-in off'
    Check (-not $model.AllowMovingFullscreenApps -and $model.MonitorMoveShortcuts.Count -eq 4) 'Fresh model leaves fullscreen movement off and contains four monitor shortcut slots'
    if ($PreviousInstallerPath) {
        # A released beta.12+ installer correctly refuses a downgrade over the
        # candidate. Complete the pristine fresh-install lifecycle first, then
        # establish the real predecessor on a clean, still-owned fixture.
        Check ((Hash $installedExe) -eq $exeHash -and (Hash $uninstaller) -eq $uninstallerHash) 'Only the exact pristine candidate enters fresh-install cleanup'
        $pristineNames = @($sources.Keys) + @('unins000.exe', 'unins000.dat')
        $pristineClock = [Diagnostics.Stopwatch]::StartNew()
        RunBounded $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $evidence 'fresh-uninstall.log') + '"')) 30000 'Pristine candidate uninstaller'
        while ($pristineClock.ElapsedMilliseconds -lt 30000 -and @($pristineNames | Where-Object { Test-Path -LiteralPath (Join-Path $installDir $_) }).Count -ne 0) { Start-Sleep -Milliseconds 100 }
        foreach ($name in $pristineNames) { Check (-not (Test-Path -LiteralPath (Join-Path $installDir $name))) "Pristine uninstall removes owned installed file: $name" }
        Check (-not (Test-Path -LiteralPath $settingsFile) -and -not (Test-Path -LiteralPath $startupLink)) 'Pristine uninstall leaves settings and Startup absent before predecessor setup'
        $key = RegKey CurrentUser Registry64 $uninstallKey
        try { Check ($null -eq $key) 'Pristine uninstall removes its registration before predecessor setup' } finally { if ($key) { $key.Dispose() } }
        NoOtherApp
        RunBounded $PreviousInstallerPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', '/CURRENTUSER', ('/DIR="' + $installDir + '"'), ('/LOG="' + (Join-Path $evidence 'baseline-setup.log') + '"')) 60000 'Earlier beta fixture setup'
        Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).ProductVersion -ceq $previousVersion) 'Earlier released beta is actually installed on the clean upgrade fixture'
        Check (-not (Test-Path -LiteralPath $settingsFile) -and -not (Test-Path -LiteralPath $startupLink)) 'Establishing the predecessor leaves preferences and Startup opt-in absent'
        $key = RegKey CurrentUser Registry64 $uninstallKey
        try {
            Check ($null -ne $key -and (SamePath $key.GetValue('InstallLocation') $installDir) -and $key.GetValue('DisplayVersion') -ceq $previousVersion) 'Earlier beta registration belongs to the unique smoke directory'
        } finally { if ($key) { $key.Dispose() } }
        $result.upgradeFrom = $previousVersion
    }
    # Seed observable custom choices without hiding taskbars or enabling movement.
    # Seed the actual predecessor schema: beta.9 and later have appearance and
    # action/navigation choices; older beta fixtures omit only absent fields.
    $model.WindowShelfActivationWidth = 612; $model.WindowShelfHoverDelayMs = 450
    $model.WindowShelfActivationHeight = 42; $model.ScaleWindowSizeToMonitor = $false
    $model.UndoMoveShortcut = 393224 # Existing Ctrl+Alt+Backspace must survive upgrade.
    $model.MonitorMoveShortcuts = [int[]]@(0, 0, 0, 0)
    $model.AllowMovingFullscreenApps = $true
    $model.FocusIndicatorStyle = 2; $model.FocusIndicatorFinish = 1
    $model.FocusIndicatorGlow = $true; $model.FocusIndicatorLighting = 3; $model.AccentPreset = 'Coral'
    $model.CloseWindowShortcut = 0; $model.SearchAppsShortcut = 0
    $model.FocusWindowShortcuts = [int[]]@(0, 0, 0, 0); $model.TileWindowShortcut = 0
    $model.MonitorAliases.Add('installer-smoke-neutral-display', 'Smoke owned display')
    $save = $programType.GetMethod('Save', $flags, $null, [Type[]]@($settingsType, [string]), $null)
    $saveArguments = [object[]]::new(2)
    $saveArguments.SetValue($model.PSObject.BaseObject, 0)
    $saveArguments.SetValue([string]$settingsFile, 1)
    Check ($saveArguments[0].GetType() -eq $settingsType -and $saveArguments[1].GetType() -eq [string]) 'Settings save receives the native model and string path'
    $null = $save.Invoke($null, $saveArguments)
    if ($PreviousInstallerPath -and ($legacyMonitorProfile -or $legacyAppearanceProfile -or $legacyPersistentProfile)) {
        $legacy = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        $legacy = ConvertTo-InstallerPreviousProfile -Profile $legacy -PreviousVersion $previousVersion
        $legacy | ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath $settingsFile -Encoding UTF8
    }
    $settingsHash = Hash $settingsFile
    $null = [IO.Directory]::CreateDirectory($startupDir)
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($startupLink); $link.TargetPath = $installedExe; $link.Arguments = '--startup'; $link.WorkingDirectory = $installDir; $link.Save()
        Check (SamePath $link.TargetPath $installedExe) 'Seeded startup shortcut targets only the test installation'
        Check ($link.Arguments -ceq '--startup' -and (Test-Path -LiteralPath $startupLink)) 'Seeded startup opt-in uses the owned command'
    } finally { if ($link) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }; $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    $startupHash = Hash $startupLink
    NoOtherApp
    if ($PreviousInstallerPath) {
        Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).ProductVersion -ceq $previousVersion) 'Earlier released beta is actually installed as the upgrade predecessor'
        Check ((Hash $settingsFile) -eq $settingsHash -and (Hash $startupLink) -eq $startupHash) 'Smoke-owned predecessor custom settings and Startup remain exact before upgrade'
        $key = RegKey CurrentUser Registry64 $uninstallKey
        try {
            Check ($null -ne $key -and (SamePath $key.GetValue('InstallLocation') $installDir) -and $key.GetValue('DisplayVersion') -ceq $previousVersion) 'Earlier beta registration still belongs to the unique smoke directory'
        } finally { if ($key) { $key.Dispose() } }
    }
    $preUpgradeExeHash = Hash $installedExe
    $gui = StartOwnedGui $installedExe @('--startup')
    Check ((Hash $settingsFile) -eq $settingsHash -and (Hash $startupLink) -eq $startupHash -and (Hash $installedExe) -eq $preUpgradeExeHash) 'Running predecessor Startup command preserves seeded choices and owned executable'
    $upgradeClock = [Diagnostics.Stopwatch]::StartNew()
    RunBounded $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', '/CURRENTUSER', ('/DIR="' + $installDir + '"'), ('/LOG="' + (Join-Path $evidence 'upgrade.log') + '"')) 60000 'Running upgrade'
    Check ($gui.WaitForExit([Math]::Max(0, 60000 - [int]$upgradeClock.ElapsedMilliseconds))) 'Upgrade stops the exact running predecessor within the upgrade deadline'
    $gui.Dispose(); $gui = $null
    NoOtherApp
    foreach ($entry in $sources.GetEnumerator()) { Check ((Hash (Join-Path $installDir $entry.Key)) -eq (Hash (Join-Path $repoRoot $entry.Value))) "Upgraded payload matches its candidate source: $($entry.Key)" }
    Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).ProductVersion -ceq $version -and (Hash $installedExe) -eq $exeHash) 'Running upgrade installs the exact beta candidate'
    Check ((Hash $settingsFile) -eq $settingsHash -and (Hash $startupLink) -eq $startupHash) 'Running upgrade preserves custom settings and Startup byte-for-byte'
    $key = RegKey CurrentUser Registry64 $uninstallKey
    try {
        Check ($null -ne $key -and (SamePath $key.GetValue('InstallLocation') $installDir) -and $key.GetValue('DisplayVersion') -ceq $version) 'Upgrade registration reports the candidate in the same owned directory'
    } finally { if ($key) { $key.Dispose() } }
    # The predecessor controls PrepareToInstall recovery. Verify the new recovery
    # implementation after replacement too; this does not fault-inject the old
    # predecessor restore result or prove its inherited failure reporting is corrected.
    RunBounded $installedExe @('--restore') 10000 'Post-upgrade candidate recovery'
    Check ((Hash $settingsFile) -eq $settingsHash -and (Hash $startupLink) -eq $startupHash) 'Post-upgrade recovery retains settings and Startup'
    $upgradedModel = $load.Invoke($null, $loadArguments)
    Check ($upgradedModel.WindowShelfActivationWidth -eq 612 -and $upgradedModel.WindowShelfHoverDelayMs -eq 450 -and -not $upgradedModel.ScaleWindowSizeToMonitor -and $upgradedModel.UndoMoveShortcut -eq 393224) 'Upgraded model retains custom sensitivity, explicit keep-window-size and existing Undo binding'
    Check ($upgradedModel.MonitorAliases['installer-smoke-neutral-display'] -ceq 'Smoke owned display') 'Upgraded model retains the custom display alias'
    $expectedHeight = 42; if ($legacyMonitorProfile) { $expectedHeight = 24 }
    Check ($upgradedModel.WindowShelfActivationHeight -eq $expectedHeight) 'Upgrade preserves saved height or applies height24 to the legacy missing property'
    if ($legacyMonitorProfile) {
        Check (($upgradedModel.MonitorMoveShortcuts -join ',') -ceq '196657,196658,196659,196660' -and -not $upgradedModel.AllowMovingFullscreenApps) 'Legacy upgrade introduces monitor defaults and keeps fullscreen opt-in off'
    } else {
        Check (($upgradedModel.MonitorMoveShortcuts -join ',') -ceq '0,0,0,0' -and $upgradedModel.AllowMovingFullscreenApps) 'Upgrade retains saved Off monitor shortcuts and fullscreen opt-in'
    }
    if ($legacyAppearanceProfile) {
        Check ($upgradedModel.FocusIndicatorStyle -eq 0 -and $upgradedModel.FocusIndicatorFinish -eq 0 -and
            -not $upgradedModel.FocusIndicatorGlow -and $upgradedModel.FocusIndicatorLighting -eq -1 -and $upgradedModel.AccentPreset -ceq 'Original') 'Legacy profile adds opt-in appearance defaults while retaining the Teal storage identity'
        Check ($upgradedModel.CloseWindowShortcut -eq 196695 -and $upgradedModel.SearchAppsShortcut -eq 196640 -and
            $upgradedModel.FocusWindowShortcuts.Count -eq 4 -and $upgradedModel.TileWindowShortcut -ne 0) 'Legacy upgrade adds configurable close, search, directional focus and layout shortcuts'
    } else {
        Check ($upgradedModel.FocusIndicatorStyle -eq 2 -and $upgradedModel.FocusIndicatorFinish -eq 1 -and
            $upgradedModel.FocusIndicatorGlow -and $upgradedModel.FocusIndicatorLighting -eq 3 -and $upgradedModel.AccentPreset -ceq 'Coral') 'Upgrade preserves the predecessor focus frame, accent outline, Energy flow and accent color'
        Check ($upgradedModel.CloseWindowShortcut -eq 0 -and $upgradedModel.SearchAppsShortcut -eq 0 -and
            ($upgradedModel.FocusWindowShortcuts -join ',') -ceq '0,0,0,0' -and $upgradedModel.TileWindowShortcut -eq 0) 'Upgrade preserves saved Off app-action, directional-focus and layout bindings'
    }
    Check ((Hash $settingsFile) -eq $settingsHash) 'Loading migrated preferences does not silently rewrite the saved profile'
    Check (-not $upgradedModel.PersistentAdministratorSupport) "Upgrade preserves the fixture's Off remembered administrator support preference"
    $gui = StartOwnedGui $installedExe @('--startup')
    Check ((Hash $settingsFile) -eq $settingsHash -and (Hash $startupLink) -eq $startupHash -and (Hash $installedExe) -eq $exeHash) 'Upgraded Startup command runs the exact candidate without rewriting preferences'
    $stopClock = [Diagnostics.Stopwatch]::StartNew()
    RunBounded $installedExe @('--restore') 10000 'Upgraded installed restore'
    Check ($gui.WaitForExit([Math]::Max(0, 10000 - [int]$stopClock.ElapsedMilliseconds))) 'Upgraded Startup instance stops through recovery within ten seconds'
    $gui.Dispose(); $gui = $null
    NoOtherApp
    $uninstallerHash = Hash $uninstaller
    Check ((Hash $settingsFile) -eq $settingsHash -and (Hash $startupLink) -eq $startupHash -and (Hash $installedExe) -eq $exeHash) 'Only smoke-owned seeded state and validated binaries enter uninstall'
    & (Join-Path $PSScriptRoot 'Test-RecoveryGuards.ps1') -Executable $installedExe
    Check $true 'Installed native-recovery fixture rejects false restore results, retains ownership and blocks cleanup'
    Check ((Hash $settingsFile) -eq $settingsHash -and (Hash $startupLink) -eq $startupHash -and (Hash $installedExe) -eq $exeHash -and (Hash $uninstaller) -eq $uninstallerHash) 'Failed recovery fixture preserves smoke-owned settings, Startup and installed binaries'
    RunBounded $installedExe @('--prepare-upgrade') 15000 'Upgrade preparation with no remembered approval'
    Check ((Hash $settingsFile) -eq $settingsHash -and (Hash $startupLink) -eq $startupHash -and (Hash $installedExe) -eq $exeHash) 'Successful upgrade preparation preserves preferences, Startup and installed bytes'
    # This fixture rejects a fixed lifecycle command. It proves installer
    # refusal ordering, not actual UAC cancellation or protected task cleanup.
    & (Join-Path $PSScriptRoot 'Test-InstallerRefusal.ps1') -InstallDirectory $installDir -SetupPath $setup -EvidenceDirectory $evidence -Version $version -SettingsFile $settingsFile -StartupLink $startupLink
    Check $true 'Setup and uninstaller retain every owned file, settings, Startup and registration after lifecycle refusal'
    $result.installerRefusalFixturePassed = $true
    $installedNames = @($sources.Keys) + @('unins000.exe', 'unins000.dat'); $uninstallClock = [Diagnostics.Stopwatch]::StartNew()
    RunBounded $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $evidence 'uninstall.log') + '"')) 30000 'Uninstaller'
    # Allow native self-removal to settle within the same uninstall deadline.
    while ($uninstallClock.ElapsedMilliseconds -lt 30000 -and @($installedNames | Where-Object { Test-Path -LiteralPath (Join-Path $installDir $_) }).Count -ne 0) { Start-Sleep -Milliseconds 100 }
    Check (-not (Test-Path -LiteralPath $settingsFile) -and -not (Test-Path -LiteralPath $startupLink)) 'Uninstall removes smoke-owned settings and startup shortcut'
    foreach ($name in $installedNames) { Check (-not (Test-Path -LiteralPath (Join-Path $installDir $name))) "Uninstall removes owned installed file: $name" }
    $key = RegKey CurrentUser Registry64 $uninstallKey
    try { Check ($null -eq $key) 'Uninstall removes its registration' } finally { if ($key) { $key.Dispose() } }
    $key = RegKey CurrentUser Registry64 $runKey
    try { Check ($null -eq $key -or $key.GetValueNames() -notcontains 'TaskbarSelect') 'Uninstall leaves no legacy startup entry' } finally { if ($key) { $key.Dispose() } }
    NoOtherApp
    $result.status = 'passed'
} catch { $result.status = 'failed'; $result.failure = $_.Exception.ToString(); throw }
finally {
    $result.checks = $checks; $result.utc = [DateTime]::UtcNow.ToString('o')
    try {
        if ($gui) { $gui.Dispose() }
        $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding UTF8
    } catch {
        Write-Warning ("Installer smoke evidence/handle cleanup failed: " + $_.Exception.Message)
        if ($result.status -eq 'passed') { throw }
    }
    Write-Host ("Installer smoke " + $result.status + ": " + $checks + " checks; version " + $version + "; EXE SHA256 " + $exeHash)
}
