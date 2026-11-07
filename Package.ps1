param([switch]$SkipInstaller, [string]$ReleaseTag)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root '.github\scripts\Package-Guards.ps1')
$artifacts = Join-Path $root 'artifacts'
$dist = Join-Path $root 'dist'
& (Join-Path $root 'Build.ps1') -OutputDirectory $artifacts
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
$executable = Join-Path $artifacts 'TaskbarSelect.exe'
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
$version = $metadata.ProductVersion
$installerLines = Get-Content -LiteralPath (Join-Path $root 'installer.iss')
$installerVersion = Get-PackageInstallerVersion -Lines $installerLines
$installerFileVersion = Get-PackageInstallerVersion -Lines $installerLines -Field AppFileVersion
$notesHeading = Get-Content -LiteralPath (Join-Path $root 'RELEASE_NOTES.md') -TotalCount 1
Assert-PackageMetadata -Version $version -InstallerVersion $installerVersion -NotesHeading $notesHeading -ReleaseTag $ReleaseTag -Title $metadata.FileDescription -ProductName $metadata.ProductName -FileVersion $metadata.FileVersion -InstallerFileVersion $installerFileVersion
function Invoke-PackageBoundedVerifier {
    param([ValidateSet('--verify-production', '--verify-installer-lifecycle', '--self-test-layout-property-native', '--self-test-snap-intent')][string]$Mode, [string]$Stem)
    $output = Join-Path $artifacts ($Stem + '.txt')
    $errors = Join-Path $artifacts ($Stem + '-errors.txt')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $child = Start-Process -FilePath $executable -ArgumentList $Mode -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors
    $exitedInBudget = $false; $drained = $false; $exitCode = $null; $exitCodeKnown = $false
    try {
        # Windows PowerShell can lose ExitCode for a fast redirected child unless
        # its original native handle is retained before waiting. Never reopen by PID.
        $ownedHandle = $child.Handle
        if ($null -eq $ownedHandle -or $ownedHandle -eq [IntPtr]::Zero) { throw 'Read-only package verifier handle was not retained.' }
        # Cold CLR/runner startup is separate from the strict stdout/exit contract.
        # There is no retry, visible UI, input, installer or permission action.
        # The property boundary regression touches only its own message-only HWND.
        $exitedInBudget = $child.WaitForExit(30000)
        if (-not $exitedInBudget) {
            # Only this invocation's exact owned read-only child may be stopped.
            $child.Kill()
            $drained = $child.WaitForExit(5000)
            throw "Read-only package verifier timed out: $Mode; stopped=$drained."
        }
        $drained = $true; $exitCode = $child.ExitCode
        if ($null -eq $exitCode) { throw 'Read-only package verifier exit code is unknown.' }
        $exitCodeKnown = $true
        return [pscustomobject]@{ ExitCode = $exitCode; Output = (Get-Content -LiteralPath $output -Raw); ErrorOutput = (Get-Content -LiteralPath $errors -Raw) }
    } finally {
        try { [ordered]@{
            mode = $Mode; budgetMilliseconds = 30000; elapsedMilliseconds = $watch.ElapsedMilliseconds
            exitedInBudget = $exitedInBudget; drained = $drained; exitCode = $exitCode; exitCodeKnown = $exitCodeKnown
            stdoutBytes = $(if (Test-Path -LiteralPath $output) { (Get-Item -LiteralPath $output).Length } else { 0 })
            stderrBytes = $(if (Test-Path -LiteralPath $errors) { (Get-Item -LiteralPath $errors).Length } else { 0 })
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts ($Stem + '-diagnostic.json')) -Encoding UTF8
        } finally { $child.Dispose() }
    }
}
$production = Invoke-PackageBoundedVerifier -Mode '--verify-production' -Stem 'package-production'
Assert-PackageProductionInvariant -ExitCode $production.ExitCode -Output $production.Output -ErrorOutput $production.ErrorOutput
$lifecycle = Invoke-PackageBoundedVerifier -Mode '--verify-installer-lifecycle' -Stem 'package-installer-lifecycle'
Assert-PackageInstallerLifecycle -ExitCode $lifecycle.ExitCode -Output $lifecycle.Output -ErrorOutput $lifecycle.ErrorOutput
$properties = Invoke-PackageBoundedVerifier -Mode '--self-test-layout-property-native' -Stem 'package-layout-properties'
Assert-PackageLayoutProperties -ExitCode $properties.ExitCode -Output $properties.Output -ErrorOutput $properties.ErrorOutput
$snapIntent = Invoke-PackageBoundedVerifier -Mode '--self-test-snap-intent' -Stem 'package-snap-intent'
Assert-PackageSnapIntent -ExitCode $snapIntent.ExitCode -Output $snapIntent.Output -ErrorOutput $snapIntent.ErrorOutput
& (Join-Path $root '.github\scripts\Test-PackageGuards.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "Package guard tests failed: $LASTEXITCODE"
}
& (Join-Path $root '.github\scripts\Test-InstallerGuards.ps1')
if ($LASTEXITCODE -ne 0) { throw "Installer guard tests failed: $LASTEXITCODE" }
$testOutput = Join-Path $artifacts 'package-self-test.txt'
$testErrors = Join-Path $artifacts 'package-self-test-errors.txt'
$testProfile = Join-Path $artifacts 'package-test-profile'
$test = Start-Process -FilePath $executable -ArgumentList ('--self-test --test-profile "' + $testProfile + '"') -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $testOutput -RedirectStandardError $testErrors
Get-Content -LiteralPath $testOutput
Get-Content -LiteralPath $testErrors
if ($test.ExitCode -ne 0) { throw "Self-test failed: $($test.ExitCode)" }
$null = New-Item -ItemType Directory -Force -Path $dist
$expectedPrefix = "TaskbarSelect-$version-"
Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Name.StartsWith($expectedPrefix) -or $_.Name -eq 'SHA256SUMS.txt' } |
    ForEach-Object { [IO.File]::Delete($_.FullName) }
$portable = Join-Path $dist ($expectedPrefix + 'portable.zip')
$packageFiles = @($portable)
Compress-Archive -Force -Path (Join-Path $artifacts 'TaskbarSelect.exe'), (Join-Path $root 'docs\package\README.md'), (Join-Path $root 'RELEASE_NOTES.md'), (Join-Path $root 'TESTING.md'), (Join-Path $root 'SECURITY.md'), (Join-Path $root 'CHANGELOG.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'assets\ARTWORK.md') -DestinationPath $portable
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($portable)
try {
    $expectedEntries = @('TaskbarSelect.exe', 'README.md', 'RELEASE_NOTES.md', 'TESTING.md', 'SECURITY.md', 'CHANGELOG.md', 'LICENSE', 'ARTWORK.md')
    if ($archive.Entries.Count -ne $expectedEntries.Count) { throw 'Portable package contains an unexpected number of entries.' }
    foreach ($name in $expectedEntries) {
        if (-not $archive.GetEntry($name)) { throw "Portable package is missing $name." }
    }
    if ($archive.Entries.Name -contains 'lucide-LICENSE.txt') { throw 'Portable package contains the historical Lucide notice.' }
    $verifiedFiles = @{
        'TaskbarSelect.exe' = 'artifacts\TaskbarSelect.exe'; 'README.md' = 'docs\package\README.md'
        'RELEASE_NOTES.md' = 'RELEASE_NOTES.md'; 'TESTING.md' = 'TESTING.md'; 'SECURITY.md' = 'SECURITY.md'
        'CHANGELOG.md' = 'CHANGELOG.md'; 'LICENSE' = 'LICENSE'; 'ARTWORK.md' = 'assets\ARTWORK.md'
    }
    foreach ($entry in $verifiedFiles.GetEnumerator()) {
        Assert-PackageEntryBytes -Entry $archive.GetEntry($entry.Key) -SourcePath (Join-Path $root $entry.Value)
    }
} finally { $archive.Dispose() }
if (-not $SkipInstaller) {
    $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $candidate = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if ($candidate) { $iscc = Get-Item -LiteralPath $candidate }
    }
    if (-not $iscc) { throw 'Inno Setup 6 was not found. Use -SkipInstaller for a portable-only package.' }
    $isccPath = $iscc.Source
    if (-not $isccPath) { $isccPath = $iscc.FullName }
    & $isccPath (Join-Path $root 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed: $LASTEXITCODE" }
    $installer = Join-Path $dist ($expectedPrefix + 'Setup.exe')
    if (-not (Test-Path -LiteralPath $installer)) { throw 'The expected installer was not produced.' }
    $packageFiles += $installer
}
$checksum = Join-Path $dist 'SHA256SUMS.txt'
Get-Item -LiteralPath $packageFiles | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
} | Set-Content -LiteralPath $checksum -Encoding ascii
Get-Item -LiteralPath ($packageFiles + $checksum) | Select-Object Name, Length
