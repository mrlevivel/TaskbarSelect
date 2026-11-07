$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows' -or -not [Environment]::Is64BitProcess) { throw 'Owned compiler fixtures require genuine disposable hosted Windows.' }
function Test-OwnedCompilerResult {
    param([bool]$Completed, $ExitCode)
    return $Completed -and $null -ne $ExitCode -and $ExitCode -is [int] -and $ExitCode -eq 0
}
function Invoke-OwnedCompiler {
    param([string]$Compiler, [string[]]$CompilerArguments, [string]$Label, [string]$Source, [string]$EvidenceDirectory)
    $compilerLabels = @('metadata-legacy', 'metadata-production', 'metadata-persistent-test', 'metadata-malformed', 'metadata-unicode', 'metadata-oversize', 'pascal-policy', 'pascal-production-code', 'refusal-fixture')
    if ($Label -cnotin $compilerLabels) { throw 'The compiler evidence label is not a fixed owned fixture.' }
    $Compiler = [IO.Path]::GetFullPath($Compiler)
    $expectedCsc = [IO.Path]::GetFullPath((Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'))
    $expectedCompiler = $expectedCsc
    if ($Label -clike 'pascal-*') { $expectedCompiler = [IO.Path]::GetFullPath((Get-Command iscc.exe -ErrorAction Stop).Source) }
    if (-not [String]::Equals($Compiler, $expectedCompiler, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $Source -PathType Leaf) -or -not (Test-Path -LiteralPath $EvidenceDirectory -PathType Container)) { throw 'The fixed compiler/source/evidence identity differs.' }
    $Source = [IO.Path]::GetFullPath($Source); $EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
    $output = Join-Path $EvidenceDirectory ($Label + '.stdout.log'); $errors = Join-Path $EvidenceDirectory ($Label + '.stderr.log'); $receiptPath = Join-Path $EvidenceDirectory ($Label + '.json')
    foreach ($path in @($output, $errors, $receiptPath)) { if (Test-Path -LiteralPath $path) { throw 'Compiler evidence must be fresh.' } }
    $receipt = [ordered]@{ fixture=$Label; completed=$false; exitCode=$null; exitHex=$null; sourceSha256=(Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash.ToLowerInvariant(); compilerSha256=(Get-FileHash -LiteralPath $Compiler -Algorithm SHA256).Hash.ToLowerInvariant(); stdoutBytes=0; stderrBytes=0 }
    $ownedCompiler = $null
    try {
        $ownedCompiler = Start-Process -FilePath $Compiler -ArgumentList $CompilerArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors
        $null = $ownedCompiler.Handle
        $receipt.completed = $ownedCompiler.WaitForExit(30000)
        $ownedCompiler.Refresh()
        if ($receipt.completed) { $receipt.exitCode = $ownedCompiler.ExitCode }
        if ($null -ne $receipt.exitCode) { $receipt.exitHex = '0x' + ([BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$receipt.exitCode),0)).ToString('X8') }
        $receipt.stdoutBytes = (Get-Item -LiteralPath $output).Length; $receipt.stderrBytes = (Get-Item -LiteralPath $errors).Length
        if (-not (Test-OwnedCompilerResult $receipt.completed $receipt.exitCode)) { throw ('Owned compiler failed: '+$Label+'; nativeExit='+$receipt.exitCode+'; completed='+$receipt.completed) }
        if ($receipt.stdoutBytes -gt 65536 -or $receipt.stderrBytes -gt 65536) { throw 'Owned compiler output exceeds its bound.' }
    } catch {
        $failure = $_.Exception; while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        $receipt.errorType = $failure.GetType().Name; $receipt.errorHResult = '0x' + $failure.HResult.ToString('X8')
        if ($failure -is [ComponentModel.Win32Exception]) { $receipt.win32 = $failure.NativeErrorCode }
        throw
    } finally {
        $receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $receiptPath -Encoding UTF8
        if ($null -ne $ownedCompiler) { $ownedCompiler.Dispose() }
    }
    return [pscustomobject]$receipt
}
