param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'), [string]$NativeCompilerCache)
$ErrorActionPreference = 'Stop'
# A Windows PowerShell child can inherit PowerShell 7's module search path.
# Select this runtime's compatible modules in this process only; Package.ps1
# also uses their cmdlets after Build returns.
foreach ($module in @('Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Archive')) {
    Import-Module -Name (Join-Path $PSHOME ("Modules\$module\$module.psd1")) -Global -Force -ErrorAction Stop
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$output = Join-Path $OutputDirectory 'TaskbarSelect.exe'
$icon = Join-Path $PSScriptRoot 'assets\TaskbarSelect.ico'
$manifest = Join-Path $PSScriptRoot 'src\TaskbarSelect\app.manifest'
if (-not (Test-Path -LiteralPath $icon)) { throw 'Build the application icon with build/Build-Icon.cjs first.' }
if (-not (Test-Path -LiteralPath $manifest)) { throw 'The application manifest is missing.' }
$toolbarResources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'assets\toolbar') -Filter '*.png' | ForEach-Object { "/resource:$($_.FullName),TaskbarSelect.Toolbar.$($_.Name)" })
$newSources = @('src\TaskbarSelect\PersistentAdministrator.cs', 'tests\TaskbarSelect\PersistentAdministratorTests.cs', 'tests\TaskbarSelect\PersistentAdministratorHostTests.cs', 'src\TaskbarSelect\ActivationPreview.cs', 'tests\TaskbarSelect\ActivationPreviewTests.cs', 'src\TaskbarSelect\AppCatalog.cs', 'src\TaskbarSelect\WindowHistory.cs', 'src\TaskbarSelect\WindowMoveWorker.cs', 'src\TaskbarSelect\WindowMoveCoordinator.cs', 'tests\TaskbarSelect\WindowMoveCoordinatorTests.cs', 'tests\TaskbarSelect\WindowMoveTimeoutTests.cs', 'src\TaskbarSelect\SettingsHistory.cs', 'src\TaskbarSelect\SettingsForm.cs', 'src\TaskbarSelect\GlobalShortcut.cs', 'src\TaskbarSelect\ShortcutDialog.cs', 'src\TaskbarSelect\TaskbarTargets.cs', 'tests\TaskbarSelect\TaskbarTargetRecoveryTests.cs', 'src\TaskbarSelect\TaskbarInput.cs', 'src\TaskbarSelect\NativePreviewSelection.cs', 'src\TaskbarSelect\Recovery.cs', 'src\TaskbarSelect\WindowShelf.cs', 'tests\TaskbarSelect\WindowShelfTests.cs', 'tests\TaskbarSelect\WindowMoveRecoveryTests.cs', 'src\TaskbarSelect\SnapWorkAreas.cs', 'tests\TaskbarSelect\SnapWorkAreaTests.cs', 'src\TaskbarSelect\ShelfPreviews.cs', 'tests\TaskbarSelect\ShelfPreviewTests.cs', 'src\TaskbarSelect\ShelfPeek.cs', 'tests\TaskbarSelect\ShelfPeekTests.cs', 'src\TaskbarSelect\ShelfPeekInteraction.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\AppProfile.cs', 'tests\TaskbarSelect\ProfileFixTests.cs', 'src\TaskbarSelect\MonitorShortcutPolicy.cs', 'src\TaskbarSelect\FullscreenMovePolicy.cs', 'tests\TaskbarSelect\Beta7FullscreenTests.cs', 'tests\TaskbarSelect\FeatureFixTests.cs', 'tests\TaskbarSelect\MovementFixTests.cs', 'tests\TaskbarSelect\GhostReturnTests.cs', 'src\TaskbarSelect\WindowAppIdentity.cs', 'tests\TaskbarSelect\SettingsTaskbarIdentityTests.cs', 'tests\TaskbarSelect\SteamIdentityTests.cs', 'tests\TaskbarSelect\CaptionlessMaximizedTests.cs', 'tests\TaskbarSelect\MinimizedFullscreenTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\GhostAppCatalog.cs', 'tests\TaskbarSelect\GhostAppCatalogTests.cs', 'src\TaskbarSelect\GhostLaunchCoordinator.cs', 'tests\TaskbarSelect\GhostLaunchCoordinatorTests.cs', 'src\TaskbarSelect\GhostSearchPopup.cs', 'tests\TaskbarSelect\GhostSearchPopupTests.cs', 'src\TaskbarSelect\GhostSearchSession.cs', 'tests\TaskbarSelect\GhostSearchIntegrationTests.cs', 'tests\TaskbarSelect\GhostSearchCancellationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\FocusedWindowActions.cs', 'tests\TaskbarSelect\FocusedWindowActionsTests.cs', 'tests\TaskbarSelect\KeyboardShortcutPolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\WindowStateActions.cs', 'tests\TaskbarSelect\WindowStateActionsTests.cs', 'tests\TaskbarSelect\WindowStateHelperTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\MinimizeUndoTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\AdministratorPinRecovery.cs', 'tests\TaskbarSelect\AdministratorPinRecoveryTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\PinnedWindowIndicator.cs', 'tests\TaskbarSelect\PinnedWindowIndicatorTests.cs', 'src\TaskbarSelect\PinnedRailPaint.cs', 'tests\TaskbarSelect\PinnedRailPaintTests.cs', 'tests\TaskbarSelect\PinIndicatorObservationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\PinnedCornerPaint.cs', 'tests\TaskbarSelect\PinnedCornerPaintTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\ReservedPinFocus.cs', 'tests\TaskbarSelect\ReservedPinFocusTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\TaskbarHideRequests.cs', 'tests\TaskbarSelect\TaskbarHideRequestsTests.cs', 'src\TaskbarSelect\HoverQueryPolicy.cs', 'tests\TaskbarSelect\HoverQueryPolicyTests.cs', 'tests\TaskbarSelect\HoverQueryIntegrationTests.cs', 'tests\TaskbarSelect\WakeSafetyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\WorkAreaGuardTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\WorkAreaReclaimTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\SnapLayoutProperties.cs', 'tests\TaskbarSelect\SnapLayoutPropertyTests.cs', 'tests\TaskbarSelect\SnapLayoutPropertyNativeTests.cs', 'src\TaskbarSelect\SnapLayoutReceipts.cs', 'src\TaskbarSelect\SnapLayoutNative.cs', 'src\TaskbarSelect\SnapLayoutExitPolicy.cs', 'tests\TaskbarSelect\SnapLayoutReceiptTests.cs', 'tests\TaskbarSelect\SnapLayoutAdversarialTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\SnapLayoutRestorePolicy.cs', 'tests\TaskbarSelect\SnapLayoutTransitionTests.cs', 'tests\TaskbarSelect\SnapLayoutTransitionAdversarialTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\SnapLayoutRetirementTests.cs', 'tests\TaskbarSelect\SnapLayoutRetirementAdversarialTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\SnapCorrectionPolicy.cs', 'tests\TaskbarSelect\SnapCorrectionPolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\SnapIntentPolicy.cs', 'src\TaskbarSelect\SnapKeyboardObserver.cs', 'src\TaskbarSelect\SnapWorkAreasIntent.cs', 'tests\TaskbarSelect\SnapIntentPolicyTests.cs', 'tests\TaskbarSelect\SnapIntentAdversarialTests.cs', 'tests\TaskbarSelect\SnapIntentRuntimeTests.cs', 'tests\TaskbarSelect\SnapIntentRuntimeAdversarialTests.cs', 'tests\TaskbarSelect\SnapIntentActionBoundaryTests.cs', 'tests\TaskbarSelect\SnapIntentPureSuite.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\DisplayWakePolicy.cs', 'src\TaskbarSelect\DisplayWakeObserver.cs', 'tests\TaskbarSelect\DisplayWakePolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\WakeRecoveryDiagnostics.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\SearchPointerInputTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\DirectionalFocus.cs', 'tests\TaskbarSelect\DirectionalFocusTests.cs', 'src\TaskbarSelect\NavigationShortcutPolicy.cs', 'tests\TaskbarSelect\NavigationShortcutPolicyTests.cs', 'src\TaskbarSelect\TilingPicker.cs', 'tests\TaskbarSelect\TilingPickerTests.cs', 'src\TaskbarSelect\WindowTiling.cs', 'tests\TaskbarSelect\WindowTilingTests.cs', 'tests\TaskbarSelect\MinimumWorkerTests.cs', 'src\TaskbarSelect\KeyboardWindowController.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\FocusAppearance.cs', 'tests\TaskbarSelect\FocusAppearanceSettingsTests.cs', 'src\TaskbarSelect\FocusIndicator.cs', 'tests\TaskbarSelect\FocusIndicatorTests.cs', 'tests\TaskbarSelect\FocusIndicatorAlphaTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\FocusIndicatorPaint.cs', 'tests\TaskbarSelect\FocusIndicatorPaintTests.cs', 'src\TaskbarSelect\FocusIndicatorMotion.cs', 'tests\TaskbarSelect\FocusIndicatorMotionTests.cs', 'tests\TaskbarSelect\FocusIndicatorMotionIntegrationTests.cs', 'tests\TaskbarSelect\FocusFrameFinishTests.cs', 'tests\TaskbarSelect\FocusFrameFinishNativeTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\FocusIndicatorGlowTests.cs', 'tests\TaskbarSelect\FocusIndicatorGlowIntegrationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\FocusIndicatorRevealPaintTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\NativeProcessIdentity.cs', 'tests\TaskbarSelect\NativeProcessIdentityTests.cs', 'src\TaskbarSelect\AdministratorSupport.cs', 'tests\TaskbarSelect\AdministratorSupportTests.cs', 'src\TaskbarSelect\AdministratorPermissionSwitch.cs', 'tests\TaskbarSelect\AdministratorPermissionSwitchTests.cs', 'tests\TaskbarSelect\AdministratorUiTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\AdministratorLaunch.cs', 'tests\TaskbarSelect\AdministratorLaunchTests.cs', 'src\TaskbarSelect\AdministratorPayloadEntry.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\TaskbarButtonReader.cs', 'tests\TaskbarSelect\TaskbarButtonSelectionTests.cs', 'src\TaskbarSelect\TaskbarNativeSelection.cs', 'tests\TaskbarSelect\TaskbarNativeSelectionTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\FocusLightingPolicyTests.cs', 'tests\TaskbarSelect\FocusIndicatorLightingTests.cs', 'tests\TaskbarSelect\FocusIndicatorLightingIntegrationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\AppChrome.cs', 'src\TaskbarSelect\AppLayout.cs', 'src\TaskbarSelect\AppearanceChoices.cs', 'tests\TaskbarSelect\AppPolishTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\ScheduledBootstrapImage.cs', 'tests\TaskbarSelect\ScheduledBootstrapImageTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\NativeStopDiagnostics.cs', 'tests\TaskbarSelect\NativeStopDiagnosticsTests.cs', 'tests\TaskbarSelect\MovementValidityDiagnosticTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\ApplicationLifecycle.cs', 'tests\TaskbarSelect\ApplicationLifecycleTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\AdministratorCancellationTests.cs', 'tests\TaskbarSelect\AdministratorReconnectRecoveryTests.cs', 'tests\TaskbarSelect\AdministratorPreferenceIdentityTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\NativeTaskbarHandoff.cs', 'tests\TaskbarSelect\NativeTaskbarHandoffTests.cs', 'src\TaskbarSelect\TaskbarMoveDiagnostics.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\TaskManagerShellIdentity.cs', 'tests\TaskbarSelect\TaskManagerShellIdentityTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\TaskbarRoutingDiagnosticTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\DeferredTaskbarClick.cs', 'tests\TaskbarSelect\DeferredTaskbarClickTests.cs', 'tests\TaskbarSelect\ManagementConsoleShellIdentityTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\OrdinaryDeferredClickIntegrationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\DesktopHookLifetime.cs', 'tests\TaskbarSelect\DesktopHookLifetimeTests.cs', 'src\TaskbarSelect\HookDeliveryWatch.cs', 'tests\TaskbarSelect\HookDeliveryWatchTests.cs', 'src\TaskbarSelect\TaskbarHookService.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\MouseHookPacketPolicy.cs', 'tests\TaskbarSelect\MouseHookPacketPolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\AppUpdates.cs', 'tests\TaskbarSelect\AppUpdateTests.cs', 'src\TaskbarSelect\AppUpdateInstaller.cs', 'tests\TaskbarSelect\AppUpdateInstallerTests.cs', 'src\TaskbarSelect\AppUpdateForm.cs', 'tests\TaskbarSelect\AppUpdateUiTests.cs', 'tests\TaskbarSelect\AccentIconTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\OwnedSnapPolicy.cs', 'tests\TaskbarSelect\OwnedSnapPolicyTests.cs', 'src\TaskbarSelect\OwnedSnapMovement.cs', 'tests\TaskbarSelect\OwnedSnapMovementTests.cs', 'src\TaskbarSelect\OwnedSnapKeyboard.cs', 'src\TaskbarSelect\OwnedSnapController.cs', 'tests\TaskbarSelect\OwnedSnapPrototypeTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('tests\TaskbarSelect\OwnedSnapRoutingTests.cs', 'src\TaskbarSelect\AdministratorWindowIndicators.cs', 'tests\TaskbarSelect\AdministratorWindowIndicatorTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('src\TaskbarSelect\AdministratorSnapProtocol.cs', 'tests\TaskbarSelect\AdministratorSnapProtocolTests.cs', 'src\TaskbarSelect\AdministratorSnapInput.cs', 'tests\TaskbarSelect\AdministratorSnapInputTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$common = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warnaserror+',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Management.dll',
    ("/r:$(Join-Path $wpf 'UIAutomationClient.dll')"), ("/r:$(Join-Path $wpf 'UIAutomationTypes.dll')"), ("/r:$(Join-Path $wpf 'WindowsBase.dll')"),
    ("/win32icon:$icon"), ("/win32manifest:$manifest"), ("/resource:$icon,TaskbarSelect.Icon"))
$sources = @('src\TaskbarSelect\AssemblyInfo.cs', 'src\TaskbarSelect\Native.cs', 'src\TaskbarSelect\MonitorNames.cs', 'src\TaskbarSelect\TaskbarMover.cs', 'src\TaskbarSelect\Program.cs', 'src\TaskbarSelect\DisplayIdentifier.cs', 'src\TaskbarSelect\WorkAreas.cs', 'src\TaskbarSelect\Startup.cs', 'tests\TaskbarSelect\Tests.cs') |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
foreach ($source in ($sources + $newSources)) { if (-not (Test-Path -LiteralPath $source)) { throw "A required shared source file is missing: $source" } }
$nativeOutput = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'administrator-native'
$null = New-Item -ItemType Directory -Path $nativeOutput -Force
$payload = Join-Path $nativeOutput 'AdministratorPayload.exe'
# Program's shared model/controller references remain available in this
# assembly, but its normal UI Main is not the selected payload entry point.
& $compiler @common /main:TaskbarSelect.AdministratorPayloadEntry "/out:$payload" @toolbarResources @sources @newSources
if ($LASTEXITCODE -ne 0) { throw "Administrator managed payload compiler failed: $LASTEXITCODE" }
$scheduledOutput = Join-Path $nativeOutput 'scheduled'
$scheduledBootstrap = & (Join-Path $PSScriptRoot 'build\Build-AdministratorBootstrap.ps1') -PayloadPath $payload -OutputDirectory $scheduledOutput -NativeCompilerCache $NativeCompilerCache -ScheduledOnly
if ($null -eq $scheduledBootstrap -or -not $scheduledBootstrap.ScheduledOnly -or -not $scheduledBootstrap.BootstrapPath) { throw 'The dedicated scheduled administrator entry did not produce its verified role.' }
$bootstrap = & (Join-Path $PSScriptRoot 'build\Build-AdministratorBootstrap.ps1') -PayloadPath $payload -OutputDirectory $nativeOutput -NativeCompilerCache $NativeCompilerCache -ScheduledBootstrapPath $scheduledBootstrap.BootstrapPath
if ($null -eq $bootstrap -or -not $bootstrap.BootstrapPath -or -not $bootstrap.PayloadHashPath) { throw 'Native administrator bootstrap did not produce verified embedded resources.' }
& $compiler @common /main:TaskbarSelect.Program "/out:$output" ("/resource:$($bootstrap.BootstrapPath),TaskbarSelect.AdministratorBootstrap") ("/resource:$($bootstrap.PayloadHashPath),TaskbarSelect.AdministratorPayloadHash") @toolbarResources @sources @newSources
if ($LASTEXITCODE -ne 0) { throw "Normal application compiler failed: $LASTEXITCODE" }
Get-Item -LiteralPath $output | Select-Object FullName, Length
