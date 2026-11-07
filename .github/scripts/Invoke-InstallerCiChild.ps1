param([Parameter(Mandatory = $true)][string]$Runtime)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows' -or -not [Environment]::Is64BitProcess) { throw 'Disposable genuine hosted MED child only.' }
$Runtime = [IO.Path]::GetFullPath($Runtime).TrimEnd('\')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($repo -cne (Join-Path $Runtime 'installer-inputs') -or [IO.Path]::GetFileName($Runtime) -cnotmatch '^TaskbarSelectCiRuntime\.[a-f0-9]{32}$') { throw 'Fixed owned hosted lifecycle workspace required.' }
$env:RUNNER_TEMP = $Runtime
$env:TEMP = $Runtime
$env:TMP = $Runtime
$env:PATH = (Join-Path $repo 'compiler') + ';' + $env:PATH
. (Join-Path $PSScriptRoot 'Installer-Guards.ps1')
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $repo 'artifacts\TaskbarSelect.exe')).ProductVersion
$baselineSpec = Get-InstallerLifecycleBaseline -Version $version
$baseline = Join-Path $Runtime $baselineSpec.Directory
[void][IO.Directory]::CreateDirectory($baseline)
foreach ($name in @($baselineSpec.Filename, 'SHA256SUMS.txt')) { Copy-Item -LiteralPath (Join-Path $repo ('baseline\' + $name)) -Destination (Join-Path $baseline $name) }
$arguments = @{ PreviousInstallerPath = (Join-Path $baseline $baselineSpec.Filename) }
if ($env:GITHUB_REF -notlike 'refs/tags/*') { $arguments.CandidateVersion = $version }
& (Join-Path $PSScriptRoot 'Test-Installer.ps1') @arguments
