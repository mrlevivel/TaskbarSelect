$ErrorActionPreference = 'Stop'
# Load only the pure predicates. Never execute the hosted downloader entry point.
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Get-InstallerLifecycleBaseline.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Baseline downloader must parse before predicate checks.' }
foreach ($name in @('Get-BaselineArtifactPolicy', 'Assert-BaselineArtifactAdmission', 'Assert-BaselineArchiveContents')) {
    $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true))
    if ($definitions.Count -ne 1) { throw 'One exact baseline predicate definition is required.' }
    Invoke-Expression $definitions[0].Extent.Text
}
$checks = 0
function Check($condition, [string]$name) {
    if (-not $condition) { throw "FAIL: $name" }
    $script:checks++
}
function Reject([scriptblock]$action, [string]$name) {
    $failed = $false
    try { & $action } catch { $failed = $true }
    Check $failed $name
}
function New-Fixture([string]$version) {
    $policy = Get-BaselineArtifactPolicy $version
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot $policy.Manifest) -Raw | ConvertFrom-Json
    $artifact = [pscustomobject]@{
        id = [long]$manifest.artifactId; name = $manifest.artifactName; expired = $false
        digest = $manifest.artifactDigest; size_in_bytes = 5767958
        workflow_run = [pscustomobject]@{ id = [long]$manifest.runId; head_sha = $manifest.headSha }
    }
    return [pscustomobject]@{
        Manifest = $manifest; Repository = [pscustomobject]@{ id = 1449520856; full_name = 'mrlevivel/TaskbarSelect' }
        Run = [pscustomobject]@{
            id = [long]$manifest.runId; repository = [pscustomobject]@{ id = 1449520856 }; head_repository = [pscustomobject]@{ id = 1449520856 }
            status = 'completed'; conclusion = 'success'; event = 'push'; head_branch = $policy.Branch; head_sha = $manifest.headSha; path = $policy.Workflow
        }
        Artifact = $artifact; RunArtifacts = [pscustomobject]@{ total_count = 1; artifacts = @($artifact) }
    }
}
function Admit([string]$version, $fixture) {
    Assert-BaselineArtifactAdmission $version $fixture.Manifest $fixture.Repository $fixture.Run $fixture.Artifact $fixture.RunArtifacts
}
foreach ($version in @('1.0.0-beta.17', '1.0.0-beta.18')) {
    $fixture = New-Fixture $version
    Admit $version $fixture
    Check $true 'The exact reviewed same-repository successful predecessor workflow is admitted'
    foreach ($mutation in @(
        { param($f) $f.Manifest.schema = 2 },
        { param($f) $f.Manifest.repositoryId = 1378387389 },
        { param($f) $f.Manifest | Add-Member extra 'unexpected' },
        { param($f) $f.Manifest.runId = '0' },
        { param($f) $f.Manifest.runId = '../outside' },
        { param($f) $f.Manifest.artifactId = 123 },
        { param($f) $f.Manifest.artifactId = '0' },
        { param($f) $f.Manifest.headSha = '0' * 40 },
        { param($f) $f.Manifest.headSha = 'A' * 40 },
        { param($f) $f.Manifest.workflowPath = '.github/workflows/unreviewed.yml' },
        { param($f) $f.Manifest.artifactName = 'unreviewed-package' },
        { param($f) $f.Manifest.artifactDigest = 'sha256:invalid' },
        { param($f) $f.Repository.id = 1378387389 },
        { param($f) $f.Repository.full_name = 'someone/TaskbarSelect' },
        { param($f) $f.Run.id++ },
        { param($f) $f.Run.repository.id = 123 },
        { param($f) $f.Run.head_repository.id = 123 },
        { param($f) $f.Run.status = 'in_progress' },
        { param($f) $f.Run.conclusion = 'failure' },
        { param($f) $f.Run.event = 'pull_request' },
        { param($f) $f.Run.head_branch = 'unreviewed-branch' },
        { param($f) $f.Run.head_sha = 'a' * 40 },
        { param($f) $f.Run.path = '.github/workflows/unreviewed.yml' },
        { param($f) $f.Artifact.id++ },
        { param($f) $f.Artifact.name = 'unreviewed-package' },
        { param($f) $f.Artifact.expired = $true },
        { param($f) $f.Artifact.expired = $null },
        { param($f) $f.Artifact.digest = 'sha256:' + ('0' * 64) },
        { param($f) $f.Artifact.workflow_run.id++ },
        { param($f) $f.Artifact.workflow_run.head_sha = 'a' * 40 },
        { param($f) $f.Artifact.size_in_bytes = 0 },
        { param($f) $f.Artifact.size_in_bytes = 8388609 },
        { param($f) $f.RunArtifacts.total_count = 0 },
        { param($f) $f.RunArtifacts.total_count = 101 },
        { param($f) $f.RunArtifacts.artifacts = @() },
        { param($f) $f.RunArtifacts.total_count = 2; $f.RunArtifacts.artifacts = @($f.Artifact, $f.Artifact) }
    )) {
        $fixture = New-Fixture $version
        & $mutation $fixture
        Reject { Admit $version $fixture } 'Altered, unqualified, cross-repository or unbounded baseline metadata is rejected'
    }
}
foreach ($unsupported in @('', '1.0.0-beta.16', '1.0.0-beta.19', '1.0.0-beta.018', '1.0.0-beta.18-test', '1.0.0-BETA.18')) {
    Reject { Get-BaselineArtifactPolicy $unsupported } 'Unknown or noncanonical versions cannot select an artifact profile'
}
$policy17 = Get-BaselineArtifactPolicy '1.0.0-beta.17'
$policy18 = Get-BaselineArtifactPolicy '1.0.0-beta.18'
Check ($policy17.Manifest -ceq 'beta16-baseline-artifact.json' -and $policy17.Files.Count -eq 2) 'The previous seed profile and exact two-file archive remain supported'
Check ($policy18.Manifest -ceq 'beta17-baseline-artifact.json' -and $policy18.Files.Count -eq 3) 'Beta.18 uses the exact three-file qualified beta.17 Build artifact'
$manifest18 = (New-Fixture '1.0.0-beta.18').Manifest
Check ($manifest18.runId -ceq '42392562457' -and $manifest18.artifactId -ceq '13582239015' -and
    $manifest18.headSha -ceq 'a2128e78b567d2a09e4c549d010ecec59f28035a' -and
    $manifest18.artifactDigest -ceq 'sha256:f77f5a161176205ce9a7f9e2b70f9da13c268d2248539f26410a8c14da9954ef') 'Beta.18 pins the reviewed successful Build run, source, artifact and ZIP digest'
Reject { Admit '1.0.0-beta.18' (New-Fixture '1.0.0-beta.17') } 'The old seed cannot replace the new Build predecessor'
Reject { Admit '1.0.0-beta.17' (New-Fixture '1.0.0-beta.18') } 'The new Build artifact cannot replace the old seed profile'

Add-Type -AssemblyName System.IO.Compression
function New-MemoryArchive($entries) {
    $memory = New-Object IO.MemoryStream
    $writer = New-Object IO.Compression.ZipArchive($memory, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($item in $entries) {
            $entry = $writer.CreateEntry($item.Name)
            $output = $entry.Open()
            try { $bytes = [Text.Encoding]::UTF8.GetBytes($item.Text); $output.Write($bytes, 0, $bytes.Length) }
            finally { $output.Dispose() }
        }
    } finally { $writer.Dispose() }
    $memory.Position = 0
    return [pscustomobject]@{ Memory = $memory; Archive = (New-Object IO.Compression.ZipArchive($memory, [IO.Compression.ZipArchiveMode]::Read, $true)) }
}
function Check-Archive($entries, $specification) {
    $owned = New-MemoryArchive $entries
    try { Assert-BaselineArchiveContents $owned.Archive $specification }
    finally { $owned.Archive.Dispose(); $owned.Memory.Dispose() }
}
$entries = @(
    [pscustomobject]@{ Name = 'fixture-Setup.exe'; Text = 'neutral setup bytes' },
    [pscustomobject]@{ Name = 'fixture-portable.zip'; Text = 'neutral portable bytes' },
    [pscustomobject]@{ Name = 'SHA256SUMS.txt'; Text = 'neutral checksum bytes' }
)
$files = @($entries | ForEach-Object {
    $bytes = [Text.Encoding]::UTF8.GetBytes($_.Text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    [pscustomobject]@{ Name = $_.Name; Length = $bytes.Length; Hash = $hash }
})
Check-Archive $entries $files
Check $true 'Every exact root entry in a neutral in-memory archive is hash-verified'
Reject { Check-Archive @($entries[0], $entries[2]) $files } 'An omitted portable entry is rejected'
Reject { Check-Archive @($entries + [pscustomobject]@{ Name = 'extra.txt'; Text = 'extra' }) $files } 'Extra archive entries are rejected'
Reject { Check-Archive @($entries[0], $entries[0], $entries[2]) $files } 'Duplicate archive entries are rejected'
foreach ($invalidName in @('../fixture-portable.zip', 'folder/fixture-portable.zip', 'FIXTURE-portable.zip', '/fixture-portable.zip')) {
    Reject { Check-Archive @($entries[0], [pscustomobject]@{ Name = $invalidName; Text = $entries[1].Text }, $entries[2]) $files } 'Non-root, traversing or case-altered archive paths are rejected'
}
Reject { Check-Archive @($entries[0], [pscustomobject]@{ Name = $entries[1].Name; Text = 'short' }, $entries[2]) $files } 'Incorrect archive entry length is rejected'
Reject { Check-Archive @($entries[0], [pscustomobject]@{ Name = $entries[1].Name; Text = $entries[1].Text.Replace('neutral', 'altered') }, $entries[2]) $files } 'An altered unused portable entry of the same length is rejected'
Write-Output "PASS: $checks baseline artifact admission/archive checks. Only source, JSON and neutral in-memory bytes were read; no downloader or executable was launched."
