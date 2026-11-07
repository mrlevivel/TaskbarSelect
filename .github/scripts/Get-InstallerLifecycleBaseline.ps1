$ErrorActionPreference = 'Stop'
# Download only. This script never runs the app, Setup, or a diagnostic donor.
function Get-BaselineArtifactPolicy([string]$CandidateVersion) {
    switch -CaseSensitive ($CandidateVersion) {
        '1.0.0-beta.17' {
            return [pscustomobject]@{
                Manifest = 'beta16-baseline-artifact.json'; Workflow = '.github/workflows/seed-upgrade-baseline.yml'
                Branch = 'codex/beta16-baseline-seed'; Name = 'Taskbar-Select-beta16-upgrade-baseline'
                Files = @(
                    [pscustomobject]@{ Name = 'TaskbarSelect-1.0.0-beta.16-Setup.exe'; Length = 3362501; Hash = 'f8bc6de104f18853a91746b156307f797367d3a55110379a9768a30ae5ae413e' },
                    [pscustomobject]@{ Name = 'SHA256SUMS.txt'; Length = 213; Hash = '82c80f4601e70dd495ffdea1efa0e35d9a9de46a6f77bf2ac0d31987023ef7d7' }
                )
            }
        }
        '1.0.0-beta.18' {
            return [pscustomobject]@{
                Manifest = 'beta17-baseline-artifact.json'; Workflow = '.github/workflows/build.yml'
                Branch = 'main'; Name = 'Taskbar-Select-beta-package'
                Files = @(
                    [pscustomobject]@{ Name = 'TaskbarSelect-1.0.0-beta.17-Setup.exe'; Length = 3329290; Hash = 'e1d35af6c0d9ca9bc16f537b82d12058bbf8cb9ae1180a83f8b1b4a640350a3d' },
                    [pscustomobject]@{ Name = 'TaskbarSelect-1.0.0-beta.17-portable.zip'; Length = 3326800; Hash = '150d3a7f72f9c6b0b16caf6a7d00b7f7dbb87dedbb284d5cc4b36a134a6769d1' },
                    [pscustomobject]@{ Name = 'SHA256SUMS.txt'; Length = 213; Hash = 'a9bc425be4ac93d3d5a4f6194a29dd2d64be4fb514898c8914e64a35fa4f50a4' }
                )
            }
        }
        default { throw 'An explicitly reviewed production baseline artifact policy is required.' }
    }
}
function Assert-BaselineArtifactAdmission([string]$CandidateVersion, $Manifest, $Repository, $Run, $Artifact, $RunArtifacts) {
    $policy = Get-BaselineArtifactPolicy $CandidateVersion
    $keys = @('schema', 'repositoryId', 'runId', 'artifactId', 'headSha', 'workflowPath', 'artifactName', 'artifactDigest')
    if ($null -eq $Manifest -or @($Manifest.PSObject.Properties).Count -ne $keys.Count -or
        @(Compare-Object ($keys | Sort-Object) (@($Manifest.PSObject.Properties.Name) | Sort-Object) -CaseSensitive).Count -ne 0 -or
        $Manifest.schema -ne 1 -or $Manifest.repositoryId -ne 1449520856 -or
        $Manifest.runId -isnot [string] -or $Manifest.runId -cnotmatch '^[1-9][0-9]{0,19}$' -or
        $Manifest.artifactId -isnot [string] -or $Manifest.artifactId -cnotmatch '^[1-9][0-9]{0,19}$' -or
        $Manifest.headSha -isnot [string] -or $Manifest.headSha -cnotmatch '^[a-f0-9]{40}$' -or
        $Manifest.headSha -ceq ('0' * 40) -or
        $Manifest.workflowPath -cne $policy.Workflow -or
        $Manifest.artifactName -cne $policy.Name -or
        $Manifest.artifactDigest -isnot [string] -or $Manifest.artifactDigest -cnotmatch '^sha256:[a-f0-9]{64}$') {
        throw 'An exact reviewed baseline artifact manifest is required.'
    }
    if ($Repository.id -ne 1449520856 -or $Repository.full_name -cne 'mrlevivel/TaskbarSelect' -or
        $Run.id -ne [long]$Manifest.runId -or $Run.repository.id -ne 1449520856 -or $Run.head_repository.id -ne 1449520856 -or
        $Run.status -cne 'completed' -or $Run.conclusion -cne 'success' -or $Run.event -cne 'push' -or
        $Run.head_branch -cne $policy.Branch -or $Run.head_sha -cne $Manifest.headSha -or
        $Run.path -cne $Manifest.workflowPath) {
        throw 'The fixed same-repository baseline workflow must have completed successfully.'
    }
    if ($Artifact.id -ne [long]$Manifest.artifactId -or $Artifact.name -cne $Manifest.artifactName -or $Artifact.expired -isnot [bool] -or $Artifact.expired -ne $false -or
        $Artifact.digest -cne $Manifest.artifactDigest -or $Artifact.workflow_run.id -ne $Run.id -or
        $Artifact.workflow_run.head_sha -cne $Manifest.headSha -or $Artifact.size_in_bytes -le 0 -or $Artifact.size_in_bytes -gt 8388608 -or
        $RunArtifacts.total_count -lt 1 -or $RunArtifacts.total_count -gt 100 -or @($RunArtifacts.artifacts).Count -ne $RunArtifacts.total_count -or
        @($RunArtifacts.artifacts | Where-Object name -CEQ $Manifest.artifactName).Count -ne 1 -or
        @($RunArtifacts.artifacts | Where-Object id -EQ $Artifact.id).Count -ne 1) {
        throw 'The bounded baseline archive must match its exact reviewed artifact identity and digest.'
    }
}
function Assert-BaselineArchiveContents($Archive, $Files) {
    if ($Archive.Entries.Count -ne $Files.Count) { throw 'The baseline archive must contain exactly the reviewed root files.' }
    foreach ($file in $Files) {
        $entry = @($Archive.Entries | Where-Object FullName -CEQ $file.Name)
        if ($entry.Count -ne 1 -or $entry[0].Length -ne $file.Length) {
            throw 'The exact baseline archive entry is missing, duplicated or incorrectly sized.'
        }
        $stream = $entry[0].Open()
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose() }
        } finally { $stream.Dispose() }
        if ($hash -cne $file.Hash) { throw 'A baseline archive entry differs from its reviewed file hash.' }
    }
}
function Read-BaselineApi([string]$Endpoint) {
    $text = gh api $Endpoint
    if ($LASTEXITCODE -ne 0 -or ($text -join "`n").Length -gt 2097152) { throw 'Baseline metadata read failed or exceeded its bound.' }
    return ($text | ConvertFrom-Json)
}
function Save-BaselineArtifact([string]$ArtifactId, [string]$Destination) {
    if ($ArtifactId -cnotmatch '^[1-9][0-9]{0,19}$' -or [String]::IsNullOrEmpty($env:GH_TOKEN) -or $env:GH_TOKEN.Length -gt 4096) {
        throw 'Fixed baseline artifact download inputs are required.'
    }
    $address = [Uri]('https://api.github.com/repos/mrlevivel/TaskbarSelect/actions/artifacts/' + $ArtifactId + '/zip')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        foreach ($hop in 0, 1) {
            if ($clock.ElapsedMilliseconds -ge 60000) { throw 'Baseline artifact download deadline exceeded.' }
            $request = [Net.HttpWebRequest]::Create($address)
            $request.AllowAutoRedirect = $false
            $request.Method = 'GET'
            $request.UserAgent = 'TaskbarSelect-baseline-validation/1.0'
            $request.Timeout = 60000
            $request.ReadWriteTimeout = 30000
            if ($hop -eq 0) {
                $request.Headers['Authorization'] = 'Bearer ' + $env:GH_TOKEN
                $request.Headers['X-GitHub-Api-Version'] = '2022-11-28'
                $request.Accept = 'application/vnd.github+json'
            }
            $response = $request.GetResponse()
            try {
                if ([int]$response.StatusCode -eq 302 -and $hop -eq 0) {
                    $next = $null
                    if (-not [Uri]::TryCreate($response.Headers['Location'], [UriKind]::Absolute, [ref]$next) -or
                        $next.Scheme -cne 'https' -or $next.Port -ne 443 -or $next.UserInfo -or $next.Fragment -or
                        $next.Host -cnotmatch '^[a-z0-9]+\.blob\.core\.windows\.net$') {
                        throw 'Unexpected baseline artifact download redirect.'
                    }
                    # The signed storage request receives no GitHub bearer token.
                    $address = $next
                    continue
                }
                if ([int]$response.StatusCode -ne 200 -or $response.ContentLength -gt 8388608) { throw 'Unexpected baseline artifact response.' }
                $input = $response.GetResponseStream()
                try {
                    $file = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
                    try {
                        $buffer = New-Object byte[] 65536
                        $total = 0L
                        while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            if ($clock.ElapsedMilliseconds -ge 60000) { throw 'Baseline artifact download deadline exceeded.' }
                            if ($read -gt 8388608 - $total) { throw 'Baseline artifact exceeds its download bound.' }
                            $file.Write($buffer, 0, $read)
                            $total += $read
                        }
                        if ($total -eq 0) { throw 'Baseline artifact is empty.' }
                        $file.Flush($true)
                    } finally { $file.Dispose() }
                } finally { $input.Dispose() }
                return
            } finally { $response.Dispose() }
        }
        throw 'Baseline artifact redirect budget exceeded.'
    } catch { throw 'The fixed baseline artifact download failed.' }
}
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
if ($version -cin @('1.0.0-beta.17', '1.0.0-beta.18')) {
    if ($env:GITHUB_REPOSITORY -cne 'mrlevivel/TaskbarSelect') { throw 'The baseline artifact belongs to the exact public-staging repository.' }
    $policy = Get-BaselineArtifactPolicy $version
    $manifestPath = Join-Path $PSScriptRoot $policy.Manifest
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 4096) { throw 'Bounded baseline manifest required.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.runId -isnot [string] -or $manifest.runId -cnotmatch '^[1-9][0-9]{0,19}$' -or
        $manifest.artifactId -isnot [string] -or $manifest.artifactId -cnotmatch '^[1-9][0-9]{0,19}$') {
        throw 'Exact numeric baseline identities are required before metadata access.'
    }
    $repository = Read-BaselineApi 'repos/mrlevivel/TaskbarSelect'
    $run = Read-BaselineApi ('repos/mrlevivel/TaskbarSelect/actions/runs/' + $manifest.runId)
    $artifact = Read-BaselineApi ('repos/mrlevivel/TaskbarSelect/actions/artifacts/' + $manifest.artifactId)
    $runArtifacts = Read-BaselineApi ('repos/mrlevivel/TaskbarSelect/actions/runs/' + $manifest.runId + '/artifacts?per_page=100')
    Assert-BaselineArtifactAdmission $version $manifest $repository $run $artifact $runArtifacts
    $archivePath = Join-Path $temp ($baseline.Directory + '-' + [Guid]::NewGuid().ToString('N') + '.zip')
    Save-BaselineArtifact $manifest.artifactId $archivePath
    if ('sha256:' + (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.artifactDigest) {
        throw 'Downloaded baseline archive bytes differ from the reviewed digest.'
    }
    [void][IO.Directory]::CreateDirectory($destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        # Check every entry, including the unused portable app, before extracting.
        Assert-BaselineArchiveContents $archive $policy.Files
        foreach ($name in @($baseline.Filename, 'SHA256SUMS.txt')) {
            $entry = @($archive.Entries | Where-Object FullName -CEQ $name)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], (Join-Path $destination $name), $false)
        }
    } finally { $archive.Dispose() }
} else {
    gh release download $baseline.ReleaseTag --repo $env:GITHUB_REPOSITORY --pattern $baseline.Filename --pattern 'SHA256SUMS.txt' --dir $destination
    if ($LASTEXITCODE -ne 0) { throw 'Released lifecycle baseline download failed.' }
}
$setupHash = (Get-FileHash -LiteralPath (Join-Path $destination $baseline.Filename) -Algorithm SHA256).Hash
$checksums = Join-Path $destination 'SHA256SUMS.txt'
Assert-InstallerLifecycleBaseline -Version $version -PreviousVersion $baseline.Version -SetupHash $setupHash -ChecksumsHash ((Get-FileHash -LiteralPath $checksums -Algorithm SHA256).Hash)
Assert-InstallerChecksum -Lines (Get-Content -LiteralPath $checksums) -Filename $baseline.Filename -Hash $setupHash
Write-Output ('Pinned released ' + $baseline.Version + ' upgrade baseline verified for ' + $version + '. No installer was launched.')
