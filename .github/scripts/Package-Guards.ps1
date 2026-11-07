function Get-PackageInstallerVersion {
    param([string[]]$Lines, [ValidateSet('AppVersion', 'AppFileVersion')][string]$Field = 'AppVersion')
    $definitions = @($Lines | Select-String -Pattern ('^#define ' + $Field + ' "([^"]+)"$'))
    if ($definitions.Count -ne 1) { throw 'The installer must define exactly one package version.' }
    return $definitions[0].Matches[0].Groups[1].Value
}

function Assert-PackageMetadata {
    param(
        [string]$Version,
        [string]$InstallerVersion,
        [string]$NotesHeading,
        [string]$ReleaseTag,
        [string]$Title,
        [string]$ProductName,
        [string]$FileVersion,
        [string]$InstallerFileVersion
    )
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid package version: $Version" }
    # A package is a production build even without a release tag. Test/local
    # markers in any branding/version metadata are therefore always rejected.
    foreach ($value in @($Version, $Title, $ProductName)) {
        if ($value -match '(?i)(local|test)') { throw 'Local and test builds cannot be packaged.' }
    }
    if ($Title -cne 'Taskbar Select' -or $ProductName -cne 'Taskbar Select') {
        throw 'The executable does not have production Taskbar Select branding.'
    }
    if ($InstallerVersion -cne $Version) { throw 'The installer and executable versions do not match.' }
    $baseVersion = ($Version -split '-', 2)[0]
    $revision = '0'
    if ($Version -match '-(?:beta|rc)\.([0-9]+)$') { $revision = $Matches[1] }
    if ($FileVersion -cne "$baseVersion.$revision" -or $InstallerFileVersion -cne $FileVersion) {
        throw 'The executable and installer numeric file versions do not match the package version.'
    }
    if ($ReleaseTag -and $ReleaseTag -cne "v$Version") { throw "Release tag $ReleaseTag does not match v$Version." }
    if ($NotesHeading -notmatch ('^# Taskbar Select ' + [regex]::Escape($Version) + '(?:\s|$)')) {
        throw 'Release notes do not describe the package version.'
    }
}

function Assert-PackageProductionInvariant {
    param([Nullable[int]]$ExitCode, [string]$Output, [string]$ErrorOutput)
    if ($null -eq $ExitCode -or $ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($Output) -or $Output.Trim() -cne 'TASKBARSELECT_PRODUCTION_PROFILE_OK' -or -not [string]::IsNullOrWhiteSpace($ErrorOutput)) {
        throw 'The executable did not verify its production profile and lifecycle.'
    }
}

function Assert-PackageInstallerLifecycle {
    param([Nullable[int]]$ExitCode, [string]$Output, [string]$ErrorOutput)
    if ($null -eq $ExitCode -or $ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($Output) -or
        $Output.Trim() -cne 'TASKBARSELECT_INSTALLER_LIFECYCLE_V1' -or
        -not [string]::IsNullOrWhiteSpace($ErrorOutput)) {
        throw 'The executable does not verify the safe installer lifecycle contract.'
    }
}

function Assert-PackageLayoutProperties {
    param([Nullable[int]]$ExitCode, [string]$Output, [string]$ErrorOutput)
    $expected = 'PASS: own message-only HWND property absence, set, exact readback, remove and absence; no geometry or desktop mutation.'
    if ($null -eq $ExitCode -or $ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($Output) -or
        $Output.Trim() -cne $expected -or -not [string]::IsNullOrWhiteSpace($ErrorOutput)) {
        throw 'The executable did not pass the own-window layout property boundary regression.'
    }
}

function Assert-PackageSnapIntent {
    param([Nullable[int]]$ExitCode, [string]$Output, [string]$ErrorOutput)
    # Require every current suite and its exact successful assertion count. An
    # exit-zero shortcut, one subgroup, extra/noisy PASS, or missing assertion
    # cannot qualify this package. Count changes require a reviewed contract.
    $expected = @(
        'PASS: 158 first-slice pure WinArrow intent checks; no native provider, application startup or desktop.',
        'PASS: 39 independent pure intent assertions; no native, observer or application startup.',
        'PASS: 27 actual-ledger intent runtime protocol checks; injected API only, no observer/application/native startup.',
        'Independent runtime intent assertions: 41; failing cases: 0',
        'PASS: 10 managed action-admission/empty-handoff checks; no hook, timer start or application constructor.'
    ) -join "`n"
    $actual = if ($null -eq $Output) { '' } else { $Output.Replace("`r`n", "`n") }
    if ($null -eq $ExitCode -or $ExitCode -ne 0 -or -not [string]::IsNullOrEmpty($ErrorOutput) -or
        ($actual -cne $expected -and $actual -cne ($expected + "`n"))) {
        throw 'The executable did not pass all five pure snap-intent suites.'
    }
}

function Assert-PackageEntryBytes {
    param($Entry, [string]$SourcePath)
    if ($null -eq $Entry) { throw 'Portable package entry is missing.' }
    # Compare the original file and decompressed entry as bytes. Text decoding
    # defaults differ between Windows PowerShell 5.1 and StreamReader; a text
    # comparison also loses distinctions such as a BOM or line endings.
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $source = [IO.File]::OpenRead($SourcePath)
        try { $expected = [BitConverter]::ToString($sha.ComputeHash($source)) }
        finally { $source.Dispose() }
        $content = $Entry.Open()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($content)) }
        finally { $content.Dispose() }
        if ($actual -cne $expected) { throw "Portable package contains different $($Entry.FullName) content." }
    } finally { $sha.Dispose() }
}
