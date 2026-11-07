param(
    [Parameter(Mandatory=$true)][string]$PayloadPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$NativeCompilerCache,
    [switch]$ScheduledOnly,
    [string]$ScheduledBootstrapPath
)
$ErrorActionPreference = 'Stop'
Import-Module -Name (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Global -Force -ErrorAction Stop
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($ScheduledOnly -and $ScheduledBootstrapPath) { throw 'The scheduled-only entry cannot embed another native entry.' }
if (-not $ScheduledOnly) {
    $expectedScheduled = Join-Path $OutputDirectory 'scheduled\AdministratorBootstrap.exe'
    if (-not $ScheduledBootstrapPath -or [IO.Path]::GetFullPath($ScheduledBootstrapPath) -ine $expectedScheduled) {
        throw 'The manual approval bootstrap requires the scheduled entry built in its dedicated output directory.'
    }
    $scheduledBytes = [IO.File]::ReadAllBytes($ScheduledBootstrapPath)
    if ($scheduledBytes.Length -lt 512 -or $scheduledBytes.Length -gt 16777216) { throw 'The scheduled entry resource must be bounded to 16 MiB.' }
    $scheduledHash = (Get-FileHash -LiteralPath $ScheduledBootstrapPath -Algorithm SHA256).Hash
}
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$payload = [IO.File]::ReadAllBytes($PayloadPath)
if ($payload.Length -eq 0 -or $payload.Length -gt 16777216) { throw 'The managed administrator payload must be nonempty and bounded to 16 MiB.' }
$configurationPath = Join-Path $PSScriptRoot 'AdministratorPayload.config'
$configuration = [IO.File]::ReadAllBytes($configurationPath)
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$null = $utf8.GetString($configuration)
if ($configuration.Length -eq 0 -or $configuration.Length -gt 16384 -or
    ($configuration.Length -ge 3 -and $configuration[0] -eq 0xEF -and $configuration[1] -eq 0xBB -and $configuration[2] -eq 0xBF) -or
    $configuration -contains 13 -or @($configuration | Where-Object { $_ -eq 10 }).Count -ne 1 -or $configuration[$configuration.Length - 1] -ne 10) {
    throw 'AdministratorPayload.config must be bounded exact UTF8 without a BOM/CR and with one terminal LF.'
}
$hash = (Get-FileHash -LiteralPath $PayloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashFile = Join-Path $OutputDirectory 'AdministratorPayload.sha256'
[IO.File]::WriteAllText($hashFile, $hash, (New-Object Text.UTF8Encoding($false)))
$header = Join-Path $OutputDirectory 'AdministratorPayload.generated.h'
$writer = New-Object IO.StreamWriter($header, $false, (New-Object Text.UTF8Encoding($false)))
$writer.NewLine = "`n"
function Write-ByteArray([string]$Name, [byte[]]$Bytes) {
    $writer.WriteLine('static const unsigned char ' + $Name + '[] = {')
    for ($offset = 0; $offset -lt $Bytes.Length; $offset += 16) {
        $count = [Math]::Min(16, $Bytes.Length - $offset)
        $line = [BitConverter]::ToString($Bytes, $offset, $count).ToLowerInvariant().Replace('-', ',0x')
        $writer.WriteLine('    0x' + $line + ',')
    }
    $writer.WriteLine('};')
}
try {
    $writer.WriteLine('#ifndef TASKBARSELECT_ADMINISTRATOR_PAYLOAD_GENERATED_H')
    $writer.WriteLine('#define TASKBARSELECT_ADMINISTRATOR_PAYLOAD_GENERATED_H')
    $writer.WriteLine('#include <stddef.h>')
    Write-ByteArray 'AdministratorPayloadBytes' $payload
    $writer.WriteLine('static const unsigned long AdministratorPayloadLength = sizeof(AdministratorPayloadBytes);')
    $writer.WriteLine('static const wchar_t AdministratorPayloadHash[] = L"' + $hash + '";')
    Write-ByteArray 'AdministratorConfigBytes' $configuration
    $writer.WriteLine('static const unsigned long AdministratorConfigLength = sizeof(AdministratorConfigBytes);')
    $writer.WriteLine('#endif')
} finally { $writer.Dispose() }
$manifest = Join-Path $PSScriptRoot 'AdministratorBootstrap.manifest'
[xml]$manifestXml = [IO.File]::ReadAllText($manifest)
$manager = New-Object Xml.XmlNamespaceManager($manifestXml.NameTable)
$manager.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v3')
$execution = $manifestXml.SelectSingleNode('//asm:requestedExecutionLevel', $manager)
if ($null -eq $execution -or $execution.level -cne 'asInvoker' -or $execution.uiAccess -cne 'false') { throw 'The native bootstrap must be asInvoker with uiAccess=false; only the explicitly authorized runas launch may request UAC.' }
$resource = Join-Path $OutputDirectory 'AdministratorBootstrap.generated.rc'
$payloadMetadata = [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($PayloadPath))
$versionParts = @($payloadMetadata.FileMajorPart, $payloadMetadata.FileMinorPart, $payloadMetadata.FileBuildPart, $payloadMetadata.FilePrivatePart)
if (@($versionParts | Where-Object { $_ -lt 0 -or $_ -gt 65535 }).Count -ne 0) { throw 'The managed payload has invalid numeric version metadata.' }
$fileVersion = $versionParts -join '.'
$productVersion = $payloadMetadata.ProductVersion
if ($fileVersion -cne $payloadMetadata.FileVersion -or $productVersion -cnotmatch '^[0-9]+(?:\.[0-9]+){2}(?:[-+][A-Za-z0-9.-]+)?$') {
    throw 'The managed payload must have explicit safe file/product version metadata.'
}
$friendlyTitle = 'Taskbar Select administrator support'
$icon = Join-Path $PSScriptRoot 'assets\TaskbarSelect.ico'
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw 'The native administrator bootstrap icon is missing.' }
$resourceManifest = $manifest.Replace('\', '/')
$resourceIcon = $icon.Replace('\', '/')
$resourceVersion = $versionParts -join ','
$resourceFlags = if ($productVersion.Contains('-')) { '0x2L' } else { '0x0L' }
$resourceText = @"
1 ICON "$resourceIcon"
1 VERSIONINFO
 FILEVERSION $resourceVersion
 PRODUCTVERSION $resourceVersion
 FILEFLAGSMASK 0x3fL
 FILEFLAGS $resourceFlags
 FILEOS 0x40004L
 FILETYPE 0x1L
 FILESUBTYPE 0x0L
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "FileDescription", "$friendlyTitle\0"
   VALUE "FileVersion", "$fileVersion\0"
   VALUE "ProductName", "Taskbar Select\0"
   VALUE "ProductVersion", "$productVersion\0"
   VALUE "InternalName", "TaskbarSelect.AdministratorBootstrap\0"
   VALUE "OriginalFilename", "AdministratorBootstrap.exe\0"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"@
if ($ScheduledOnly) {
    $markerPath = Join-Path $OutputDirectory 'AdministratorBootstrap.ScheduledOnly.marker'
    [IO.File]::WriteAllBytes($markerPath, [Text.Encoding]::ASCII.GetBytes('TaskbarSelect.ScheduledOnly.v1'))
    $resourceRole = '211 10 "' + $markerPath.Replace('\','/') + '"'
} else {
    $resourceRole = '1 24 "' + $resourceManifest + '"' + "`n" + '210 10 "' + $ScheduledBootstrapPath.Replace('\','/') + '"'
}
$resourceText = "LANGUAGE 9, 1`n" + $resourceRole + "`n" + $resourceText
[IO.File]::WriteAllText($resource, $resourceText + "`n", (New-Object Text.UTF8Encoding($false)))
$compiledResource = Join-Path $OutputDirectory 'AdministratorBootstrap.manifest.res'
$bootstrap = Join-Path $OutputDirectory 'AdministratorBootstrap.exe'
$zig = & (Join-Path $PSScriptRoot 'Get-NativeCompiler.ps1') -CacheDirectory $NativeCompilerCache
if (-not $zig -or @($zig).Count -ne 1) { throw 'Pinned native compiler selection returned an invalid path.' }
$windowsHeaders = Join-Path ([IO.Path]::GetDirectoryName($zig)) 'lib\libc\include\any-windows-any'
if (-not (Test-Path -LiteralPath (Join-Path $windowsHeaders 'windows.h'))) { throw 'The pinned portable native compiler is missing its Windows headers.' }
$priorGlobal = $env:ZIG_GLOBAL_CACHE_DIR; $priorLocal = $env:ZIG_LOCAL_CACHE_DIR
try {
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($zig))) 'zig-global-cache'
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $OutputDirectory 'zig-local-cache'
    # This generated resource contains a fixed numeric manifest entry only. It
    # needs no macros or compiler include discovery, so suppress preprocessing
    # and ignore inherited INCLUDE paths.
    $resourceArguments = @('rc', '/nologo', '/:no-preprocess', '/:auto-includes', 'none', '/x', '/fo', $compiledResource, $resource)
    & $zig @resourceArguments
    if ($LASTEXITCODE -ne 0) { throw "Native manifest compilation failed: $LASTEXITCODE" }
    $nativeArguments = @('cc', '-target', 'x86_64-windows-gnu', '-Os', '-nostdlib', '-fno-builtin', '-fno-stack-protector', '-Wall', '-Wextra', '-Werror',
        '-isystem', $windowsHeaders, ('-I' + $OutputDirectory), (Join-Path $PSScriptRoot 'AdministratorBootstrap.c'), $compiledResource,
        '-Wl,--entry,BootstrapEntry', '-Wl,--subsystem,windows', '-Wl,--nxcompat', '-Wl,--dynamicbase', '-lkernel32', '-o', $bootstrap)
    if ($ScheduledOnly) { $nativeArguments += '-DTASKBARSELECT_SCHEDULED_ONLY=1' }
    & $zig @nativeArguments
    if ($LASTEXITCODE -ne 0) { throw "Native bootstrap compilation failed: $LASTEXITCODE" }
} finally { $env:ZIG_GLOBAL_CACHE_DIR = $priorGlobal; $env:ZIG_LOCAL_CACHE_DIR = $priorLocal }

# Inspect the linked PE before the normal app can embed it. No native executable
# is launched by this static import/CLR check.
$bytes = [IO.File]::ReadAllBytes($bootstrap)
if ($bytes.Length -lt 512 -or $bytes.Length -gt 16777216) { throw 'Each native entry must be bounded to 16 MiB.' }
function U16([int]$At) { if ($At -lt 0 -or $At + 2 -gt $bytes.Length) { throw 'Truncated native PE.' }; return [BitConverter]::ToUInt16($bytes, $At) }
function U32([int]$At) { if ($At -lt 0 -or $At + 4 -gt $bytes.Length) { throw 'Truncated native PE.' }; return [BitConverter]::ToUInt32($bytes, $At) }
$pe = [int](U32 0x3C)
if ((U16 0) -ne 0x5A4D -or (U32 $pe) -ne 0x4550 -or (U16 ($pe + 4)) -ne 0x8664 -or ((U16 ($pe + 22)) -band 0x2000) -ne 0) { throw 'Bootstrap must be a native x64 Windows executable PE.' }
$optional = $pe + 24; $sectionCount = U16 ($pe + 6); $optionalSize = U16 ($pe + 20)
if ((U16 $optional) -ne 0x20B -or $optionalSize -lt 240 -or (U32 ($optional + 108)) -ne 16 -or $sectionCount -eq 0 -or $sectionCount -gt 96 -or
    (U32 ($optional + 16)) -eq 0 -or (U16 ($optional + 68)) -ne 2 -or
    ((U16 ($optional + 70)) -band 0x140) -ne 0x140) { throw 'Bootstrap lacks its x64 native GUI entry or ASLR/NX policy.' }
if ((U32 ($optional + 112 + 14 * 8)) -ne 0 -or (U32 ($optional + 116 + 14 * 8)) -ne 0 -or
    (U32 ($optional + 112 + 13 * 8)) -ne 0 -or (U32 ($optional + 116 + 13 * 8)) -ne 0) { throw 'Bootstrap contains a CLR or delayed-import startup dependency.' }
function Offset([uint32]$Rva) {
    if ($Rva -lt (U32 ($optional + 60))) { return [int]$Rva }
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $section = $optional + $optionalSize + 40 * $index
        $start = U32 ($section + 12); $length = [Math]::Max((U32 ($section + 8)), (U32 ($section + 16)))
        if ([long]$Rva -ge $start -and [long]$Rva -lt [long]$start + $length) {
            $mapped = [long](U32 ($section + 20)) + $Rva - $start
            if ($mapped -lt 0 -or $mapped -ge $bytes.Length) { throw 'Native PE RVA maps outside its bytes.' }
            return [int]$mapped
        }
    }
    throw 'Native PE RVA has no section mapping.'
}
$imports = @(); $importRva = U32 ($optional + 120)
if ($importRva -eq 0) { throw 'Bootstrap is missing its fixed Kernel32 import table.' }
$importTable = Offset $importRva; $terminated = $false
for ($index = 0; $index -lt 32; $index++) {
    $descriptor = $importTable + 20 * $index
    if ((U32 $descriptor) -eq 0 -and (U32 ($descriptor + 4)) -eq 0 -and (U32 ($descriptor + 8)) -eq 0 -and
        (U32 ($descriptor + 12)) -eq 0 -and (U32 ($descriptor + 16)) -eq 0) { $terminated = $true; break }
    $nameAt = Offset (U32 ($descriptor + 12)); $name = New-Object Text.StringBuilder
    for ($character = 0; $character -lt 256; $character++) {
        if ($nameAt + $character -ge $bytes.Length) { throw 'Truncated native import name.' }
        if ($bytes[$nameAt + $character] -eq 0) { break }
        $null = $name.Append([char]$bytes[$nameAt + $character])
    }
    if ($character -eq 256 -or $name.ToString() -ine 'KERNEL32.dll') { throw 'Bootstrap statically imports a DLL outside its Kernel32-only startup contract.' }
    $imports += $name.ToString()
}
if (-not $terminated -or $imports.Count -ne 1) { throw 'Bootstrap does not have exactly one terminated Kernel32-only import descriptor.' }
function ResourceEntry([int]$Root, [uint32]$Relative, [uint32]$Id) {
    $directory = $Root + [int]$Relative
    $count = (U16 ($directory + 12)) + (U16 ($directory + 14))
    if ($count -lt 1 -or $count -gt 256) { throw 'The native resource directory is not bounded.' }
    for ($index = 0; $index -lt $count; $index++) {
        $entry = $directory + 16 + $index * 8
        if ((U32 $entry) -eq $Id) { return (U32 ($entry + 4)) }
    }
    return $null
}
function ResourceBytes([uint32]$Type, [uint32]$Name) {
    $rva = U32 ($optional + 128)
    if ($rva -eq 0) { return $null }
    $root = Offset $rva
    $typeEntry = ResourceEntry $root 0 $Type
    if ($null -eq $typeEntry) { return $null }
    if (($typeEntry -band [uint32]2147483648) -eq 0) { throw 'The native resource type is not a directory.' }
    $nameEntry = ResourceEntry $root ($typeEntry -band [uint32]2147483647) $Name
    if ($null -eq $nameEntry) { return $null }
    if (($nameEntry -band [uint32]2147483648) -eq 0) { throw 'The native resource name is not a fixed directory.' }
    $languageAt = $root + [int]($nameEntry -band [uint32]2147483647)
    if ((U16 ($languageAt + 12)) -ne 0 -or (U16 ($languageAt + 14)) -ne 1) { throw 'The native resource must have one numeric language.' }
    $dataEntry = ResourceEntry $root ($nameEntry -band [uint32]2147483647) 1033
    if ($null -eq $dataEntry -or ($dataEntry -band [uint32]2147483648) -ne 0) { throw 'The native resource must have one English data entry.' }
    $dataAt = $root + [int]$dataEntry
    $size = U32 ($dataAt + 4); $first = Offset (U32 $dataAt)
    if ($size -eq 0 -or $size -gt 16777216 -or [long]$first + $size -gt $bytes.Length) { throw 'The native resource bytes are not bounded.' }
    $content = New-Object byte[] ([int]$size)
    [Array]::Copy($bytes, $first, $content, 0, [int]$size)
    return ,$content
}
function BytesHash([byte[]]$Content) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Content)).Replace('-', '') }
    finally { $sha.Dispose() }
}
$resourceRoot = Offset (U32 ($optional + 128))
$hasManifest = $null -ne (ResourceEntry $resourceRoot 0 24)
if ($ScheduledOnly) {
    $marker = ResourceBytes 10 211
    if ($hasManifest -or $null -eq $marker -or $marker.Length -ne 30 -or [Text.Encoding]::ASCII.GetString($marker) -cne 'TaskbarSelect.ScheduledOnly.v1' -or
        $null -ne (ResourceBytes 10 210)) { throw 'The scheduled-only entry must have its exact role marker and no startup manifest or nested broker.' }
} else {
    $embedded = ResourceBytes 10 210
    $embeddedManifest = ResourceBytes 24 1
    if (-not $hasManifest -or $null -eq $embedded -or (BytesHash $embedded) -cne $scheduledHash -or $embedded.Length -ne $scheduledBytes.Length -or
        $null -eq $embeddedManifest -or (BytesHash $embeddedManifest) -cne (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -or
        $null -ne (ResourceBytes 10 211)) { throw 'The manual entry must retain its exact asInvoker manifest and embed only the verified scheduled broker.' }
}
$bootstrapMetadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($bootstrap)
if ($bootstrapMetadata.FileVersion -cne $fileVersion -or $bootstrapMetadata.ProductVersion -cne $productVersion -or
    $bootstrapMetadata.FileDescription -cne $friendlyTitle -or $bootstrapMetadata.ProductName -cne 'Taskbar Select') {
    throw 'Native bootstrap version or friendly administrator-support branding differs from its managed payload contract.'
}
$selfTest = Start-Process -FilePath $bootstrap -ArgumentList '--bootstrap-self-test' -WindowStyle Hidden -PassThru
try {
    $null = $selfTest.Handle
    if (-not $selfTest.WaitForExit(10000)) {
        # Only this positively created pure self-test process is stopped. It
        # cannot create a cache, launch a payload or request elevation.
        $selfTest.Kill()
        throw 'The native bootstrap pure self-test exceeded ten seconds.'
    }
    $selfTest.Refresh()
    if ($null -eq $selfTest.ExitCode -or $selfTest.ExitCode -ne 0) { throw ('The native bootstrap pure self-test failed with exit code ' + $selfTest.ExitCode + '.') }
} finally { $selfTest.Dispose() }
[pscustomobject]@{ BootstrapPath = $bootstrap; BootstrapHash = (Get-FileHash -LiteralPath $bootstrap -Algorithm SHA256).Hash; ScheduledOnly = [bool]$ScheduledOnly; ScheduledResourceId = if ($ScheduledOnly) { 211 } else { 210 }; ResourceLanguage = 1033; EmbeddedScheduledHash = if ($ScheduledOnly) { $null } else { $scheduledHash }; PayloadHashPath = $hashFile; PayloadHash = $hash; StaticImports = $imports; HasClr = $false; HasDelayedImports = $false; FileVersion = $fileVersion; ProductVersion = $productVersion; FileDescription = $friendlyTitle; BootstrapSelfTestExit = 0 }
