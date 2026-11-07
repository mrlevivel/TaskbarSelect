param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
# Reflection operates on the owned built/installed image. Every native boundary
# below is replaced per call; neither real HWNDs nor profile paths are read.
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Executable))
$static = [Reflection.BindingFlags]'Static,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,NonPublic'
$native = $assembly.GetType('TaskbarSelect.Native', $true)
$barType = $assembly.GetType('TaskbarSelect.Bar', $true)
$apiType = $native.GetNestedType('TaskbarRestoreApi', [Reflection.BindingFlags]::NonPublic)
$restoreAll = @($native.GetMethods($static) | Where-Object { $_.Name -ceq 'RestoreAll' -and $_.GetParameters().Length -eq 2 })
if ($null -eq $apiType -or $restoreAll.Count -ne 1) { throw 'The installed native recovery boundary is unavailable.' }
$api = [Activator]::CreateInstance($apiType, $true)
$bars = [Array]::CreateInstance($barType, 2)
for ($index = 0; $index -lt 2; $index++) {
    $bar = [Activator]::CreateInstance($barType)
    $bar.Handle = 101 + $index; $bar.Device = 'recovery-fixture-' + $index
    $bars.SetValue($bar.PSObject.BaseObject, $index)
}
$state = @{ Shows = [Collections.Generic.List[long]]::new(); Releases = [Collections.Generic.List[long]]::new(); Cleaned = $false }
$marked = [Func[IntPtr, bool]]{ param($window) return $true }
$show = [Func[IntPtr, bool]]({ param($window) $state.Shows.Add($window.ToInt64()); return $window.ToInt64() -ne 101 }.GetNewClosure())
$release = [Func[IntPtr, bool]]({ param($window) $state.Releases.Add($window.ToInt64()); return $true }.GetNewClosure())
$apiType.GetField('Marked', $instance).SetValue($api, $marked.PSObject.BaseObject)
$apiType.GetField('Show', $instance).SetValue($api, $show.PSObject.BaseObject)
$apiType.GetField('Release', $instance).SetValue($api, $release.PSObject.BaseObject)
$arguments = [object[]]::new(2)
$arguments.SetValue($bars.PSObject.BaseObject, 0); $arguments.SetValue($api.PSObject.BaseObject, 1)
$failure = $null
try { $null = $restoreAll[0].Invoke($null, $arguments) }
catch { $failure = $_.Exception.InnerException }
if ($failure -isnot [AggregateException] -or $failure.InnerExceptions.Count -ne 1) { throw 'A refused native restore did not reach aggregate recovery failure.' }
if ($state.Shows.Count -ne 2 -or $state.Shows[0] -ne 101 -or $state.Shows[1] -ne 102) { throw 'A refused native restore skipped a remaining managed taskbar.' }
if ($state.Releases.Count -ne 1 -or $state.Releases[0] -ne 102) { throw 'A refused native restore released its recovery ownership.' }
$state.Shows.Clear(); $state.Releases.Clear()
$restoreAreas = [Action]{ }
$restoreTaskbars = [Action]({
    try { $null = $restoreAll[0].Invoke($null, $arguments) }
    catch { throw $_.Exception.InnerException }
}.GetNewClosure())
$cleanup = [Action]({ $state.Cleaned = $true }.GetNewClosure())
$recovery = $assembly.GetType('TaskbarSelect.Recovery', $true)
$run = $recovery.GetMethod('Run', $static)
$runArguments = [object[]]::new(3)
$runArguments.SetValue($restoreAreas.PSObject.BaseObject, 0)
$runArguments.SetValue($restoreTaskbars.PSObject.BaseObject, 1)
$runArguments.SetValue($cleanup.PSObject.BaseObject, 2)
$result = $run.Invoke($null, $runArguments)
$success = $result.GetType().GetField('Success', $instance).GetValue($result)
$messages = $result.GetType().GetField('Messages', $instance).GetValue($result)
if ($success -ne $false -or $messages.Count -eq 0) { throw 'Native failure did not produce a failed recovery result and status.' }
if ($state.Cleaned) { throw 'Failed native recovery allowed uninstall cleanup.' }
if ($state.Shows.Count -ne 2 -or $state.Releases.Count -ne 1 -or $state.Releases[0] -ne 102) { throw 'Recovery changed refused-restore request or ownership behavior.' }
Write-Output 'PASS: 6 reflected native-recovery guard checks. All native boundaries injected; no GUI, taskbars, settings or Startup changes.'
