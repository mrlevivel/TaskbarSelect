function Test-InstallerProcessHasExited {
    param([scriptblock]$ReadExited)
    if ($null -eq $ReadExited) { return $false }
    try {
        $observed = & $ReadExited
        return ($observed -is [bool] -and $observed -eq $true)
    } catch { return $false }
}

function Get-InstallerLifecycleBaseline {
    param([string]$Version)
    # Explicit production pairs. Never substitute an UNVALIDATED diagnostic
    # donor, a test build, or an arbitrary earlier beta for the released asset.
    switch -CaseSensitive ($Version) {
        '1.0.0-beta.12' {
            return [pscustomobject]@{ Version = '1.0.0-beta.11'; ReleaseTag = 'v1.0.0-beta.11'; Directory = 'TaskbarSelect-beta11-baseline';
                Filename = 'TaskbarSelect-1.0.0-beta.11-Setup.exe';
                SetupSha256 = '105954c65f0fea42dec5f3be19df43226bc874a41025b3e31eeffb5eefd85c01';
                ChecksumsSha256 = '3056e5e803505bcb8c6ac116706d1d16d406d98ddc863ba5782c43721d552b0e' }
        }
        '1.0.0-beta.13' {
            return [pscustomobject]@{ Version = '1.0.0-beta.12'; ReleaseTag = 'v1.0.0-beta.12'; Directory = 'TaskbarSelect-beta12-baseline';
                Filename = 'TaskbarSelect-1.0.0-beta.12-Setup.exe';
                SetupSha256 = '7edfb663b07bdf8773302aad28650ffa472ea5538c5937664aaceb6951c4ea68';
                ChecksumsSha256 = '5f948f76cf4baf3aa2b649494dd4103deb58cd1d3439d751c857e3f1e2a489ae' }
        }
        '1.0.0-beta.14' {
            return [pscustomobject]@{ Version = '1.0.0-beta.13'; ReleaseTag = 'v1.0.0-beta.13'; Directory = 'TaskbarSelect-beta13-baseline';
                Filename = 'TaskbarSelect-1.0.0-beta.13-Setup.exe';
                SetupSha256 = '9f13d6982f98d584415517da17ff85d45b6710dde39f194b7c393ef57e301140';
                ChecksumsSha256 = 'f70f1da9aa3ba958936ed21b5cee7cd072def3a9bd1e9acea11c9fc11f3f5b06' }
        }
        '1.0.0-beta.15' {
            return [pscustomobject]@{ Version = '1.0.0-beta.14'; ReleaseTag = 'v1.0.0-beta.14'; Directory = 'TaskbarSelect-beta14-baseline';
                Filename = 'TaskbarSelect-1.0.0-beta.14-Setup.exe';
                SetupSha256 = '5e7d2213cd477bf41ab9d17bf95c53eed4910503a4d4a0fcb6825471b9171870';
                ChecksumsSha256 = '46d881b5c61a74446d1fcb596190e9ab401bc2a625f785494297dd343fc51d9c' }
        }
        '1.0.0-beta.16' {
            return [pscustomobject]@{ Version = '1.0.0-beta.15'; ReleaseTag = 'v1.0.0-beta.15'; Directory = 'TaskbarSelect-beta15-baseline';
                Filename = 'TaskbarSelect-1.0.0-beta.15-Setup.exe';
                SetupSha256 = '2f254e952f0c9d21b3822cfd779300252fdbedd675fe2d13a42cc57448e2a2fe';
                ChecksumsSha256 = '6aee4790d74345aa1f8584cfae2437f88062f9f641a4930ceea7ab1d7a162890' }
        }
        '1.0.0-beta.17' {
            return [pscustomobject]@{ Version = '1.0.0-beta.16'; ReleaseTag = 'ci-upgrade-baseline-beta16'; Directory = 'TaskbarSelect-beta16-baseline';
                Filename = 'TaskbarSelect-1.0.0-beta.16-Setup.exe';
                SetupSha256 = 'f8bc6de104f18853a91746b156307f797367d3a55110379a9768a30ae5ae413e';
                ChecksumsSha256 = '82c80f4601e70dd495ffdea1efa0e35d9a9de46a6f77bf2ac0d31987023ef7d7' }
        }
        '1.0.0-beta.18' {
            return [pscustomobject]@{ Version = '1.0.0-beta.17'; ReleaseTag = 'v1.0.0-beta.17'; Directory = 'TaskbarSelect-beta17-baseline';
                Filename = 'TaskbarSelect-1.0.0-beta.17-Setup.exe';
                SetupSha256 = 'e1d35af6c0d9ca9bc16f537b82d12058bbf8cb9ae1180a83f8b1b4a640350a3d';
                ChecksumsSha256 = 'a9bc425be4ac93d3d5a4f6194a29dd2d64be4fb514898c8914e64a35fa4f50a4' }
        }
        '1.0.0-beta.19' {
            return [pscustomobject]@{ Version = '1.0.0-beta.18'; ReleaseTag = 'v1.0.0-beta.18'; Directory = 'TaskbarSelect-beta18-baseline';
                Filename = 'TaskbarSelect-1.0.0-beta.18-Setup.exe';
                PublicReleaseBase = 'https://github.com/mrlevivel/TaskbarSelect/releases/download/v1.0.0-beta.18/';
                SetupSha256 = '0b066ee454e1e5b3b2ba251372c24a773ba288f4e635044b07bf7aeb3531e5f5';
                ChecksumsSha256 = '018804cf469ebc657881b1d7185e1ab1adb0d1a8c81ce72398ceca96149261f8' }
        }
        '1.0.0-beta.20' {
            return [pscustomobject]@{ Version = '1.0.0-beta.19'; ReleaseTag = 'v1.0.0-beta.19'; Directory = 'TaskbarSelect-beta19-baseline';
                Filename = 'TaskbarSelect-1.0.0-beta.19-Setup.exe';
                PublicReleaseBase = 'https://github.com/mrlevivel/TaskbarSelect/releases/download/v1.0.0-beta.19/';
                SetupSha256 = '0e51d38ed30592c03bc51836145467d5de057c00f402acf785ff946da0b59859';
                ChecksumsSha256 = '4d7b0c091256f307f3981a798ed776de281817797b35be36e9ad3ad4108b5e01' }
        }
        default { throw 'The lifecycle gate requires an explicitly supported production beta.12 through beta.20 artifact.' }
    }
}

function Assert-InstallerLifecycleBaseline {
    param([string]$Version, [string]$PreviousVersion, [string]$SetupHash, [string]$ChecksumsHash)
    $baseline = Get-InstallerLifecycleBaseline -Version $Version
    if ($PreviousVersion -cne $baseline.Version -or $SetupHash.ToLowerInvariant() -cne $baseline.SetupSha256 -or
        $ChecksumsHash.ToLowerInvariant() -cne $baseline.ChecksumsSha256) {
        throw 'Published predecessor version or asset identity differs from the exact lifecycle baseline.'
    }
}

function Assert-InstallerSmokeMode {
    param([string]$Version, [string]$CandidateVersion, [string]$Ref, [string]$EventName)
    if ($Version -notmatch '^\d+\.\d+\.\d+-beta\.\d+$') { throw 'Installer smoke requires a beta package version.' }
    if ($CandidateVersion) {
        if ($CandidateVersion -cne $Version) { throw 'Candidate version does not match the packaged executable.' }
        $branchRun = $EventName -cin @('push', 'workflow_dispatch') -and $Ref -cmatch '^refs/heads/[^\s]+$'
        $pullRun = $EventName -ceq 'pull_request' -and $Ref -cmatch '^refs/pull/[1-9][0-9]*/merge$'
        if (-not ($branchRun -or $pullRun)) { throw 'Candidate installer smoke requires a branch push, branch dispatch or pull-request merge ref.' }
    } elseif ($Ref -cne "refs/tags/v$Version") {
        throw 'Smoke version does not match the beta release tag.'
    }
}

function Get-InstallerPreviousVersion {
    param([string]$Filename, [string]$Version)
    if ($Filename -cmatch '^TaskbarSelect-(\d+\.\d+\.\d+-beta\.[0-9]+)-Setup\.exe$') { $previous = $Matches[1] }
    else { throw 'Previous installer filename must contain its beta version.' }
    $currentParts = $Version -split '-beta\.'
    $previousParts = $previous -split '-beta\.'
    if ($currentParts.Count -ne 2 -or $currentParts[0] -cne $previousParts[0] -or [long]$previousParts[1] -ge [long]$currentParts[1]) {
        throw 'Previous installer must be an earlier beta of the same base version.'
    }
    return $previous
}

function Assert-InstallerChecksum {
    param([string[]]$Lines, [string]$Filename, [string]$Hash)
    if ($Hash -notmatch '^[a-fA-F0-9]{64}$') { throw 'Installer checksum hash is invalid.' }
    $entries = @($Lines | Where-Object { $_ -cmatch ('^[a-fA-F0-9]{64}  ' + [regex]::Escape($Filename) + '$') })
    if ($entries.Count -ne 1 -or $entries[0].Substring(0, 64) -ine $Hash) { throw 'Installer hash does not match exactly one distribution checksum.' }
}

function Test-InstallerLegacyMonitorProfile {
    param([string]$PreviousVersion)
    if (-not $PreviousVersion) { return $false }
    if ($PreviousVersion -cnotmatch '^1\.0\.0-beta\.([0-9]+)$') { throw 'The upgrade fixture requires a known 1.0.0 beta settings schema.' }
    return [long]$Matches[1] -lt 8
}

function Test-InstallerLegacyAppearanceProfile {
    param([string]$PreviousVersion)
    if (-not $PreviousVersion) { return $false }
    if ($PreviousVersion -cnotmatch '^1\.0\.0-beta\.([0-9]+)$') { throw 'The upgrade fixture requires a known 1.0.0 beta settings schema.' }
    return [long]$Matches[1] -lt 9
}

function ConvertTo-InstallerPreviousProfile {
    param($Profile, [string]$PreviousVersion)
    if ($null -eq $Profile) { throw 'An in-memory predecessor settings profile is required.' }
    if (Test-InstallerLegacyAppearanceProfile -PreviousVersion $PreviousVersion) {
        foreach ($property in @('CloseWindowShortcut', 'SearchAppsShortcut', 'FocusWindowShortcuts', 'TileWindowShortcut',
            'FocusIndicatorStyle', 'FocusIndicatorGlow', 'FocusIndicatorLighting', 'FocusIndicatorFinish', 'AccentPreset')) {
            $Profile.PSObject.Properties.Remove($property)
        }
    }
    if (Test-InstallerLegacyMonitorProfile -PreviousVersion $PreviousVersion) {
        foreach ($property in @('WindowShelfActivationHeight', 'MonitorMoveShortcuts', 'AllowMovingFullscreenApps')) {
            $Profile.PSObject.Properties.Remove($property)
        }
    }
    if (Test-InstallerLegacyPersistentProfile -PreviousVersion $PreviousVersion) {
        $Profile.PSObject.Properties.Remove('PersistentAdministratorSupport')
    }
    return $Profile
}

function Test-InstallerLegacyPersistentProfile {
    param([string]$PreviousVersion)
    if (-not $PreviousVersion) { return $false }
    if ($PreviousVersion -cnotmatch '^1\.0\.0-beta\.([0-9]+)$') { throw 'The upgrade fixture requires a known 1.0.0 beta settings schema.' }
    return [long]$Matches[1] -lt 12
}

function Assert-InstallerLifecycleSource {
    param([string]$Installer, [string]$Policy)
    if ($Installer -cnotmatch '(?m)^#include "InstallerLifecycle\.iss"\r?$' -or
        $Installer -cnotmatch 'if PrepareInstalledApp\(\) then' -or
        $Installer -cmatch "StopInstalledApp\('--restore'" -or
        $Installer -cnotmatch "GetInstalledUpgradeArguments\(Version, '\{#AppVersion\}', Arguments\)" -or
        $Installer -cnotmatch 'Result := StopInstalledApp\(\);' -or
        $Installer -cnotmatch "GetInstalledUninstallArguments\(Version, '\{#AppVersion\}', CheckedArguments\)" -or
        $Installer -cnotmatch "ExtractTemporaryFile\('TaskbarSelect.Lifecycle.exe'\)" -or
        $Installer -cnotmatch "Exec\(TrustedCandidate, '--prepare-upgrade'" -or
        $Installer -cnotmatch 'DestName: "TaskbarSelect.Lifecycle.exe"; Flags: dontcopy' -or
        $Installer -cnotmatch 'and \(ResultCode = 0\)') { throw 'Installer lifecycle wiring must fail closed before file replacement or uninstall.' }
    if ($Policy -cnotmatch "if Revision <= 11 then\s+Arguments := '--restore'\s+else\s+Arguments := '--prepare-upgrade';" -or
        $Policy -cnotmatch 'if Revision > CandidateRevision then' -or
        $Policy -cnotmatch '\(Revision < 12\) or \(CandidateRevision < 12\) or \(Version <> CandidateVersion\)' -or
        $Policy -cnotmatch '\(IntToStr\(Revision\) = Suffix\)') { throw 'Installer lifecycle predecessor policy is missing its strict production-version dispatch.' }
}
