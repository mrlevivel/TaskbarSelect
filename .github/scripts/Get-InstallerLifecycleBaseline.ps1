$ErrorActionPreference = 'Stop'
# Download only. This script never runs the app, Setup, or a diagnostic donor.
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows' -or
    -not [Environment]::Is64BitProcess) { throw 'Only genuine disposable 64-bit GitHub-hosted Windows is admitted.' }
. (Join-Path $PSScriptRoot 'Installer-Guards.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $repo 'artifacts\TaskbarSelect.exe')).ProductVersion
$baseline = Get-InstallerLifecycleBaseline -Version $version
$candidate = ''; if ($env:GITHUB_REF -notlike 'refs/tags/*') { $candidate = $version }
Assert-InstallerSmokeMode -Version $version -CandidateVersion $candidate -Ref $env:GITHUB_REF -EventName $env:GITHUB_EVENT_NAME
$temp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')
if (-not [IO.Path]::IsPathRooted($temp) -or -not (Test-Path -LiteralPath $temp -PathType Container)) { throw 'Existing absolute hosted RUNNER_TEMP required.' }
for ($current = $temp; $current; $current = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))) {
    if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Baseline ancestors cannot be reparse points.' }
    if ($current.Length -eq 3) { break }
}
$destination = Join-Path $temp $baseline.Directory
if (Test-Path -LiteralPath $destination) { throw 'A fresh pinned baseline directory is required.' }
gh release download $baseline.ReleaseTag --repo $env:GITHUB_REPOSITORY --pattern $baseline.Filename --pattern 'SHA256SUMS.txt' --dir $destination
if ($LASTEXITCODE -ne 0) { throw 'Released lifecycle baseline download failed.' }
$setupHash = (Get-FileHash -LiteralPath (Join-Path $destination $baseline.Filename) -Algorithm SHA256).Hash
$checksums = Join-Path $destination 'SHA256SUMS.txt'
Assert-InstallerLifecycleBaseline -Version $version -PreviousVersion $baseline.Version -SetupHash $setupHash -ChecksumsHash ((Get-FileHash -LiteralPath $checksums -Algorithm SHA256).Hash)
Assert-InstallerChecksum -Lines (Get-Content -LiteralPath $checksums) -Filename $baseline.Filename -Hash $setupHash
Write-Output ('Pinned released ' + $baseline.Version + ' upgrade baseline verified for ' + $version + '. No installer was launched.')
