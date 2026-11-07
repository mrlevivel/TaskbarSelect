param([string]$CacheDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetDirectoryName($PSScriptRoot)
foreach ($module in @('Microsoft.PowerShell.Utility')) {
    Import-Module -Name (Join-Path $PSHOME ("Modules\$module\$module.psd1")) -Global -Force -ErrorAction Stop
}
$version = '0.15.2'
$archiveName = "zig-x86_64-windows-$version"
$url = "https://ziglang.org/download/$version/$archiveName.zip"
$expectedHash = '3a0ed1e8799a2f8ce2a6e6290a9ff22e6906f8227865911fb7ddedc3cc14cb0c'
$expectedSize = 92614574L
$ProgressPreference = 'SilentlyContinue'
$provisionClock = [Diagnostics.Stopwatch]::StartNew()
function Write-ProvisionStage([string]$Stage) {
    Write-Host ('Native compiler: ' + $Stage + ' (' + [Math]::Round($provisionClock.Elapsed.TotalSeconds, 1) + 's).')
}
if (-not $CacheDirectory) {
    $existing = Join-Path ([IO.Path]::GetDirectoryName($repoRoot)) 'validation\tools\zig'
    if ($env:TASKBARSELECT_NATIVE_TOOLCHAIN_CACHE) { $CacheDirectory = $env:TASKBARSELECT_NATIVE_TOOLCHAIN_CACHE }
    elseif (Test-Path -LiteralPath (Join-Path $existing 'zig-0.15.2.zip') -PathType Leaf) { $CacheDirectory = $existing }
    elseif ($env:RUNNER_TEMP) { $CacheDirectory = Join-Path $env:RUNNER_TEMP 'TaskbarSelect-native-toolchain' }
    elseif ($env:LOCALAPPDATA) { $CacheDirectory = Join-Path $env:LOCALAPPDATA 'TaskbarSelectBuildTools\native-toolchain' }
    else { throw 'A local compiler cache directory is required.' }
}
if (-not [IO.Path]::IsPathRooted($CacheDirectory)) { throw 'The native compiler cache requires an absolute path.' }
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
$sourceDirectory = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\')
if ($CacheDirectory.TrimEnd('\').Equals($sourceDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $CacheDirectory.StartsWith($sourceDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The native compiler cache must be outside the source checkout.'
}
$null = New-Item -ItemType Directory -Path $CacheDirectory -Force
$archivePath = Join-Path $CacheDirectory "zig-$version.zip"
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    $temporary = Join-Path $CacheDirectory ('zig-download-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Write-ProvisionStage 'downloading pinned publisher archive'
        $curl = Join-Path ([Environment]::SystemDirectory) 'curl.exe'
        if (Test-Path -LiteralPath $curl -PathType Leaf) {
            & $curl --fail --location --silent --show-error --connect-timeout 20 --max-time 120 --retry 1 --retry-delay 2 --retry-max-time 150 --output $temporary $url
            if ($LASTEXITCODE -ne 0) { throw ('Pinned native compiler download failed with curl exit code ' + $LASTEXITCODE + '.') }
        } else {
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $temporary -TimeoutSec 180
        }
        Write-ProvisionStage 'verifying downloaded byte size and SHA-256'
        if ((Get-Item -LiteralPath $temporary).Length -ne $expectedSize -or (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ine $expectedHash) {
            throw 'The pinned native compiler download failed byte-size/SHA-256 verification.'
        }
        Move-Item -LiteralPath $temporary -Destination $archivePath
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
Write-ProvisionStage 'verifying cached archive byte size and SHA-256'
if ((Get-Item -LiteralPath $archivePath).Length -ne $expectedSize -or (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ine $expectedHash) {
    throw 'The cached native compiler archive differs from the pinned publisher release.'
}
$compilerDirectory = Join-Path $CacheDirectory $archiveName
$compiler = Join-Path $compilerDirectory 'zig.exe'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    if (Test-Path -LiteralPath $compilerDirectory) { throw 'The native compiler cache is incomplete; preserve it and choose a fresh cache directory.' }
    Write-ProvisionStage 'extracting verified archive with the .NET ZIP reader'
    [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $CacheDirectory)
}
Write-ProvisionStage 'verifying extracted compiler against the pinned archive entry'
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $entry = $archive.GetEntry("$archiveName/zig.exe")
    if ($null -eq $entry -or $entry.Length -ne (Get-Item -LiteralPath $compiler).Length) { throw 'The extracted compiler differs from its verified archive entry.' }
    $stream = $entry.Open()
    try { $compilerHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose() }
    if ((Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash -cne $compilerHash) { throw 'The extracted native compiler byte hash differs from its verified publisher archive.' }
} finally { $sha.Dispose(); $archive.Dispose() }
Write-ProvisionStage 'ready'
# Return one path only. Do not install or modify PATH; the caller invokes this
# pinned portable tool explicitly and keeps caches out of distribution assets.
Write-Output $compiler
