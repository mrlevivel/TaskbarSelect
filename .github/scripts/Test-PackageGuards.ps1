$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package-Guards.ps1')
$checks = 0
function Check($condition, [string]$name) {
    if (-not $condition) { throw "FAIL: $name" }
    $script:checks++
}
function Metadata([hashtable]$Overrides = @{}) {
    $fixture = @{
        Version = '1.0.0-beta.8'; InstallerVersion = '1.0.0-beta.8'
        NotesHeading = '# Taskbar Select 1.0.0-beta.8'; ReleaseTag = ''
        Title = 'Taskbar Select'; ProductName = 'Taskbar Select'
        FileVersion = '1.0.0.8'; InstallerFileVersion = '1.0.0.8'
    }
    foreach ($key in $Overrides.Keys) { $fixture[$key] = $Overrides[$key] }
    Assert-PackageMetadata @fixture
}
function Reject([scriptblock]$action, [string]$message, [string]$name) {
    $failure = $null
    try { & $action } catch { $failure = $_.Exception.Message }
    Check ($null -ne $failure -and $failure.Contains($message)) $name
}

Metadata
Check $true 'Production metadata is accepted without a release tag'
Metadata @{ ReleaseTag = 'v1.0.0-beta.8' }
Check $true 'Matching production beta release tag is accepted'
Metadata @{ Version = '1.0.0-beta.9'; InstallerVersion = '1.0.0-beta.9'; NotesHeading = '# Taskbar Select 1.0.0-beta.9'; ReleaseTag = 'v1.0.0-beta.9'; FileVersion = '1.0.0.9'; InstallerFileVersion = '1.0.0.9' }
Check $true 'Aligned beta.9 executable, installer, release notes and tag metadata is accepted'
Metadata @{ Version = '1.0.0-beta.13'; InstallerVersion = '1.0.0-beta.13'; NotesHeading = '# Taskbar Select 1.0.0-beta.13'; ReleaseTag = 'v1.0.0-beta.13'; FileVersion = '1.0.0.13'; InstallerFileVersion = '1.0.0.13' }
Check $true 'Aligned beta.13 executable, installer, release notes and tag metadata is accepted'
Metadata @{ Version = '1.0.0-beta.14'; InstallerVersion = '1.0.0-beta.14'; NotesHeading = '# Taskbar Select 1.0.0-beta.14'; ReleaseTag = 'v1.0.0-beta.14'; FileVersion = '1.0.0.14'; InstallerFileVersion = '1.0.0.14' }
Check $true 'Aligned beta.14 executable, installer, release notes and tag metadata is accepted'
Metadata @{ Version = '1.0.0-beta.15'; InstallerVersion = '1.0.0-beta.15'; NotesHeading = '# Taskbar Select 1.0.0-beta.15'; ReleaseTag = 'v1.0.0-beta.15'; FileVersion = '1.0.0.15'; InstallerFileVersion = '1.0.0.15' }
Check $true 'Aligned beta.15 executable, installer, release notes and tag metadata is accepted'
Metadata @{ Version = '1.0.0-beta.16'; InstallerVersion = '1.0.0-beta.16'; NotesHeading = '# Taskbar Select 1.0.0-beta.16'; ReleaseTag = 'v1.0.0-beta.16'; FileVersion = '1.0.0.16'; InstallerFileVersion = '1.0.0.16' }
Check $true 'Aligned beta.16 executable, installer, release notes and tag metadata is accepted'
Metadata @{ Version = '1.0.0-beta.17'; InstallerVersion = '1.0.0-beta.17'; NotesHeading = '# Taskbar Select 1.0.0-beta.17'; ReleaseTag = 'v1.0.0-beta.17'; FileVersion = '1.0.0.17'; InstallerFileVersion = '1.0.0.17' }
Check $true 'Aligned beta.17 executable, installer, release notes and tag metadata is accepted'
Metadata @{ Version = '1.0.0-beta.18'; InstallerVersion = '1.0.0-beta.18'; NotesHeading = '# Taskbar Select 1.0.0-beta.18'; ReleaseTag = 'v1.0.0-beta.18'; FileVersion = '1.0.0.18'; InstallerFileVersion = '1.0.0.18' }
Check $true 'Aligned beta.18 executable, installer, release notes and tag metadata is accepted'
foreach ($version in @('1.0.0', '1.0.0-rc.1')) {
    $fileVersion = if ($version -eq '1.0.0') { '1.0.0.0' } else { '1.0.0.1' }
    Metadata @{ Version = $version; InstallerVersion = $version; NotesHeading = "# Taskbar Select $version"; ReleaseTag = "v$version"; FileVersion = $fileVersion; InstallerFileVersion = $fileVersion }
    Check $true "Production version is accepted: $version"
}
foreach ($version in @('', 'beta.8', '1.0', '1.0.0 beta.8', '1.0.0+test')) {
    Reject { Metadata @{ Version = $version } } 'Invalid package version' "Malformed version is rejected: $version"
}
foreach ($version in @('1.0.0-local', '1.0.0-beta.8-local', '1.0.0-test', '1.0.0-test1', '1.0.0-beta.7-fullscreen-test.20261008', '1.0.0-LOCAL', '1.0.0-TEST')) {
    foreach ($tag in @('', "v$version")) {
        Reject { Metadata @{ Version = $version; InstallerVersion = $version; NotesHeading = "# Taskbar Select $version"; ReleaseTag = $tag } } 'Local and test builds cannot be packaged' "Test/local version is rejected regardless of tag: $version [$tag]"
    }
}
foreach ($field in @('Title', 'ProductName')) {
    foreach ($value in @('Taskbar Select - LOCAL', 'Taskbar Select - BETA7 LOCAL TEST', 'Taskbar Select - fullscreen-test')) {
        foreach ($tag in @('', 'v1.0.0-beta.8')) {
            $override = @{ ReleaseTag = $tag }; $override[$field] = $value
            Reject { Metadata $override } 'Local and test builds cannot be packaged' "Test/local $field is rejected regardless of tag: $value [$tag]"
        }
    }
    $override = @{}; $override[$field] = 'Other Product'
    Reject { Metadata $override } 'production Taskbar Select branding' "Foreign $field is rejected"
}
Reject { Metadata @{ InstallerVersion = '1.0.0-beta.7' } } 'installer and executable versions' 'Installer version mismatch is rejected'
Reject { Metadata @{ FileVersion = '1.0.0.0' } } 'numeric file versions' 'Stale executable numeric file version is rejected'
Reject { Metadata @{ InstallerFileVersion = '1.0.0.0' } } 'numeric file versions' 'Stale installer numeric file version is rejected'
Reject { Metadata @{ ReleaseTag = 'v1.0.0-beta.7' } } 'Release tag' 'Release tag mismatch is rejected'
Reject { Metadata @{ ReleaseTag = 'V1.0.0-beta.8' } } 'Release tag' 'Release tag comparison is case sensitive'
foreach ($heading in @('', '# Taskbar Select 1.0.0-beta.7', '# Taskbar Select 1.0.0-beta.80', '# Another App 1.0.0-beta.8')) {
    Reject { Metadata @{ NotesHeading = $heading } } 'Release notes' "Incorrect notes heading is rejected: $heading"
}
$installerVersion = Get-PackageInstallerVersion -Lines @('#define AppName "Taskbar Select"', '#define AppVersion "1.0.0-beta.8"')
Check ($installerVersion -ceq '1.0.0-beta.8') 'Exactly one installer definition is read'
Reject { Get-PackageInstallerVersion -Lines @('#define AppName "Taskbar Select"') } 'exactly one' 'Missing installer version is rejected'
Reject { Get-PackageInstallerVersion -Lines @('#define AppVersion "1.0.0-beta.8"', '#define AppVersion "1.0.0-beta.8"') } 'exactly one' 'Duplicate installer versions are rejected'
$installerFileVersion = Get-PackageInstallerVersion -Lines @('#define AppVersion "1.0.0-beta.8"', '#define AppFileVersion "1.0.0.8"') -Field AppFileVersion
Check ($installerFileVersion -ceq '1.0.0.8') 'Exactly one installer numeric file version is read'
Reject { Get-PackageInstallerVersion -Lines @('#define AppVersion "1.0.0-beta.8"') -Field AppFileVersion } 'exactly one' 'Missing installer numeric file version is rejected'

Assert-PackageProductionInvariant -ExitCode 0 -Output "TASKBARSELECT_PRODUCTION_PROFILE_OK`r`n" -ErrorOutput ''
Check $true 'Successful production invariant is accepted'
Reject { Assert-PackageProductionInvariant -ExitCode $null -Output 'TASKBARSELECT_PRODUCTION_PROFILE_OK' -ErrorOutput '' } 'production profile and lifecycle' 'Unknown invariant exit cannot be coerced to success'
Reject { Assert-PackageProductionInvariant -ExitCode 1 -Output 'TASKBARSELECT_PRODUCTION_PROFILE_OK' -ErrorOutput '' } 'production profile and lifecycle' 'Nonzero invariant exit is rejected'
foreach ($output in @($null, '', 'TASKBARSELECT_TEST_PROFILE_OK', "TASKBARSELECT_PRODUCTION_PROFILE_OK`nUnexpected output")) {
    Reject { Assert-PackageProductionInvariant -ExitCode 0 -Output $output -ErrorOutput '' } 'production profile and lifecycle' "Missing or incorrect invariant is rejected: $output"
}
Reject { Assert-PackageProductionInvariant -ExitCode 0 -Output 'TASKBARSELECT_PRODUCTION_PROFILE_OK' -ErrorOutput 'Profile error' } 'production profile and lifecycle' 'Invariant error output is rejected'

Assert-PackageInstallerLifecycle -ExitCode 0 -Output "TASKBARSELECT_INSTALLER_LIFECYCLE_V1`r`n" -ErrorOutput ''
Check $true 'The exact read-only lifecycle capability is accepted'
Reject { Assert-PackageInstallerLifecycle -ExitCode $null -Output 'TASKBARSELECT_INSTALLER_LIFECYCLE_V1' -ErrorOutput '' } 'safe installer lifecycle' 'Unknown lifecycle exit cannot be coerced to success'
Reject { Assert-PackageInstallerLifecycle -ExitCode 1 -Output 'TASKBARSELECT_INSTALLER_LIFECYCLE_V1' -ErrorOutput '' } 'safe installer lifecycle' 'A failed lifecycle capability is rejected'
foreach ($output in @($null, '', 'TASKBARSELECT_PRODUCTION_PROFILE_OK', 'TASKBARSELECT_INSTALLER_LIFECYCLE_V0', "TASKBARSELECT_INSTALLER_LIFECYCLE_V1`nUnexpected output")) {
    Reject { Assert-PackageInstallerLifecycle -ExitCode 0 -Output $output -ErrorOutput '' } 'safe installer lifecycle' 'Missing, obsolete, unrelated or noisy lifecycle capability is rejected'
}
Reject { Assert-PackageInstallerLifecycle -ExitCode 0 -Output 'TASKBARSELECT_INSTALLER_LIFECYCLE_V1' -ErrorOutput 'Failure' } 'safe installer lifecycle' 'Lifecycle capability error output is rejected'

Add-Type -AssemblyName System.IO.Compression
$propertyPass = 'PASS: own message-only HWND property absence, set, exact readback, remove and absence; no geometry or desktop mutation.'
Assert-PackageLayoutProperties -ExitCode 0 -Output ($propertyPass + "`r`n") -ErrorOutput ''
Check $true 'The exact own-window property boundary result is accepted'
Reject { Assert-PackageLayoutProperties -ExitCode $null -Output $propertyPass -ErrorOutput '' } 'layout property boundary' 'An unknown property test exit cannot be coerced to success'
Reject { Assert-PackageLayoutProperties -ExitCode 1 -Output $propertyPass -ErrorOutput '' } 'layout property boundary' 'A failed property test cannot produce a qualified package'
foreach ($output in @($null, '', 'TASKBARSELECT_PRODUCTION_PROFILE_OK', ($propertyPass + "`nUnexpected output"))) {
    Reject { Assert-PackageLayoutProperties -ExitCode 0 -Output $output -ErrorOutput '' } 'layout property boundary' 'Missing, unrelated or noisy property boundary output is rejected'
}
Reject { Assert-PackageLayoutProperties -ExitCode 0 -Output $propertyPass -ErrorOutput 'Failure' } 'layout property boundary' 'Property boundary error output is rejected'

$snapIntentLines = @(
    'PASS: 158 first-slice pure WinArrow intent checks; no native provider, application startup or desktop.',
    'PASS: 39 independent pure intent assertions; no native, observer or application startup.',
    'PASS: 27 actual-ledger intent runtime protocol checks; injected API only, no observer/application/native startup.',
    'Independent runtime intent assertions: 41; failing cases: 0',
    'PASS: 10 managed action-admission/empty-handoff checks; no hook, timer start or application constructor.'
)
$snapIntentPass = $snapIntentLines -join "`n"
foreach ($valid in @($snapIntentPass, ($snapIntentPass + "`n"), (($snapIntentLines -join "`r`n") + "`r`n"))) {
    Assert-PackageSnapIntent -ExitCode 0 -Output $valid -ErrorOutput ''
    Check $true 'Every expected snap-intent suite is accepted with exact LF/CRLF output'
}
foreach ($exit in @($null, 1, -1)) {
    Reject { Assert-PackageSnapIntent -ExitCode $exit -Output $snapIntentPass -ErrorOutput '' } 'all five pure snap-intent suites' 'Unknown/nonzero snap-intent exit cannot qualify a package'
}
foreach ($bad in @($null, '', 'PASS', 'PASS: 275 checks', ($snapIntentLines[0]),
    ($snapIntentPass + "`nUnexpected output"), ($snapIntentPass + "`n" + $snapIntentLines[0]),
    ($snapIntentPass + "`n`n"), ("`n" + $snapIntentPass),
    $snapIntentPass.Replace('158 ', '0 '), $snapIntentPass.Replace('39 ', '39x '),
    $snapIntentPass.Replace('failing cases: 0', 'failing cases: 1'))) {
    Reject { Assert-PackageSnapIntent -ExitCode 0 -Output $bad -ErrorOutput '' } 'all five pure snap-intent suites' 'Missing, malformed, duplicate, noisy or failed snap-intent output is rejected'
}
for ($index = 0; $index -lt $snapIntentLines.Count; $index++) {
    $partial = @(for ($line = 0; $line -lt $snapIntentLines.Count; $line++) { if ($line -ne $index) { $snapIntentLines[$line] } }) -join "`n"
    Reject { Assert-PackageSnapIntent -ExitCode 0 -Output $partial -ErrorOutput '' } 'all five pure snap-intent suites' 'Every individual missing snap-intent subgroup is rejected'
}
foreach ($stderr in @('Failure', ' ', "`r`n")) {
    Reject { Assert-PackageSnapIntent -ExitCode 0 -Output $snapIntentPass -ErrorOutput $stderr } 'all five pure snap-intent suites' 'Any snap-intent stderr fails the package'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$directory = Join-Path ([IO.Path]::GetTempPath()) ('TaskbarSelect-package-byte-guards-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $directory) { throw 'The unique owned byte-check fixture already exists.' }
$null = [IO.Directory]::CreateDirectory($directory)
$archivePath = Join-Path $directory 'owned-documents.zip'
$unicode = 'Ctrl + Shift + 1' + [char]0x2013 + '4; preview ' + [char]0x00D7 + ' height'
$utf8 = [Text.UTF8Encoding]::new($false); $utf8Bom = [Text.UTF8Encoding]::new($true)
$utf16 = [Text.UnicodeEncoding]::new($false, $true)
$fixtures = @(
    @{ Name = 'ascii.txt'; Bytes = [Text.Encoding]::ASCII.GetBytes("ASCII`nexact bytes`n") },
    @{ Name = 'unicode-utf8.txt'; Bytes = $utf8.GetBytes($unicode + "`n") },
    @{ Name = 'unicode-utf8-bom-crlf.txt'; Bytes = [byte[]]($utf8Bom.GetPreamble() + $utf8Bom.GetBytes($unicode + "`r`n")) },
    @{ Name = 'unicode-utf16-bom-crlf.txt'; Bytes = [byte[]]($utf16.GetPreamble() + $utf16.GetBytes($unicode + "`r`n")) }
)
$archive = $null
try {
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($fixture in $fixtures) {
        $path = Join-Path $directory $fixture.Name
        [IO.File]::WriteAllBytes($path, $fixture.Bytes)
        $entry = $archive.CreateEntry($fixture.Name)
        $stream = $entry.Open()
        try { $stream.Write($fixture.Bytes, 0, $fixture.Bytes.Length) }
        finally { $stream.Dispose() }
    }
    $archive.Dispose(); $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    foreach ($fixture in $fixtures) {
        $path = Join-Path $directory $fixture.Name; $entry = $archive.GetEntry($fixture.Name)
        Assert-PackageEntryBytes -Entry $entry -SourcePath $path
        Check $true "Exact archive bytes are accepted: $($fixture.Name)"
        $changed = [byte[]]$fixture.Bytes.Clone(); $changed[$changed.Length - 1] = $changed[$changed.Length - 1] -bxor 1
        [IO.File]::WriteAllBytes($path, $changed)
        Reject { Assert-PackageEntryBytes -Entry $entry -SourcePath $path } 'different' "A changed byte is rejected: $($fixture.Name)"
        [IO.File]::WriteAllBytes($path, $fixture.Bytes)
    }
    $bomPath = Join-Path $directory 'unicode-utf8-bom-crlf.txt'
    $bomEntry = $archive.GetEntry('unicode-utf8-bom-crlf.txt')
    [IO.File]::WriteAllBytes($bomPath, $utf8.GetBytes($unicode + "`r`n"))
    Reject { Assert-PackageEntryBytes -Entry $bomEntry -SourcePath $bomPath } 'different' 'Removing only the UTF-8 BOM is rejected even though the decoded text matches'
    [IO.File]::WriteAllBytes($bomPath, [byte[]]($utf8Bom.GetPreamble() + $utf8Bom.GetBytes($unicode + "`n")))
    Reject { Assert-PackageEntryBytes -Entry $bomEntry -SourcePath $bomPath } 'different' 'Changing only CRLF to LF is rejected instead of normalizing document text'
    Reject { Assert-PackageEntryBytes -Entry $archive.GetEntry('missing.txt') -SourcePath $bomPath } 'missing' 'A missing archive entry is rejected'
} finally {
    if ($archive) { $archive.Dispose() }
    foreach ($fixture in $fixtures) { [IO.File]::Delete((Join-Path $directory $fixture.Name)) }
    [IO.File]::Delete($archivePath)
    [IO.Directory]::Delete($directory)
}
Write-Output "PASS: $checks package guard checks. No executable or installer was launched."
& (Join-Path $PSScriptRoot 'Test-BaselineArtifactGuards.ps1')
