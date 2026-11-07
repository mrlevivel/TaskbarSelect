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
$manifest = Join-Path $PSScriptRoot 'app.manifest'
if (-not (Test-Path -LiteralPath $icon)) { throw 'Build the application icon with Build-Icon.cjs first.' }
if (-not (Test-Path -LiteralPath $manifest)) { throw 'The application manifest is missing.' }
$toolbarResources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'assets\toolbar') -Filter '*.png' | ForEach-Object { "/resource:$($_.FullName),TaskbarSelect.Toolbar.$($_.Name)" })
$newSources = @('PersistentAdministrator.cs', 'PersistentAdministratorTests.cs', 'PersistentAdministratorHostTests.cs', 'ActivationPreview.cs', 'ActivationPreviewTests.cs', 'AppCatalog.cs', 'WindowHistory.cs', 'WindowMoveWorker.cs', 'WindowMoveCoordinator.cs', 'WindowMoveCoordinatorTests.cs', 'WindowMoveTimeoutTests.cs', 'SettingsHistory.cs', 'SettingsForm.cs', 'GlobalShortcut.cs', 'ShortcutDialog.cs', 'TaskbarTargets.cs', 'TaskbarTargetRecoveryTests.cs', 'TaskbarInput.cs', 'NativePreviewSelection.cs', 'Recovery.cs', 'WindowShelf.cs', 'WindowShelfTests.cs', 'WindowMoveRecoveryTests.cs', 'SnapWorkAreas.cs', 'SnapWorkAreaTests.cs', 'ShelfPreviews.cs', 'ShelfPreviewTests.cs', 'ShelfPeek.cs', 'ShelfPeekTests.cs', 'ShelfPeekInteraction.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('AppProfile.cs', 'ProfileFixTests.cs', 'MonitorShortcutPolicy.cs', 'FullscreenMovePolicy.cs', 'Beta7FullscreenTests.cs', 'FeatureFixTests.cs', 'MovementFixTests.cs', 'GhostReturnTests.cs', 'WindowAppIdentity.cs', 'SettingsTaskbarIdentityTests.cs', 'SteamIdentityTests.cs', 'CaptionlessMaximizedTests.cs', 'MinimizedFullscreenTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('GhostAppCatalog.cs', 'GhostAppCatalogTests.cs', 'GhostLaunchCoordinator.cs', 'GhostLaunchCoordinatorTests.cs', 'GhostSearchPopup.cs', 'GhostSearchPopupTests.cs', 'GhostSearchSession.cs', 'GhostSearchIntegrationTests.cs', 'GhostSearchCancellationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('FocusedWindowActions.cs', 'FocusedWindowActionsTests.cs', 'KeyboardShortcutPolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('WindowStateActions.cs', 'WindowStateActionsTests.cs', 'WindowStateHelperTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('MinimizeUndoTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('AdministratorPinRecovery.cs', 'AdministratorPinRecoveryTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('PinnedWindowIndicator.cs', 'PinnedWindowIndicatorTests.cs', 'PinnedRailPaint.cs', 'PinnedRailPaintTests.cs', 'PinIndicatorObservationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('PinnedCornerPaint.cs', 'PinnedCornerPaintTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('ReservedPinFocus.cs', 'ReservedPinFocusTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('TaskbarHideRequests.cs', 'TaskbarHideRequestsTests.cs', 'HoverQueryPolicy.cs', 'HoverQueryPolicyTests.cs', 'HoverQueryIntegrationTests.cs', 'WakeSafetyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('WorkAreaGuardTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('WorkAreaReclaimTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('SnapLayoutProperties.cs', 'SnapLayoutPropertyTests.cs', 'SnapLayoutPropertyNativeTests.cs', 'SnapLayoutReceipts.cs', 'SnapLayoutNative.cs', 'SnapLayoutExitPolicy.cs', 'SnapLayoutReceiptTests.cs', 'SnapLayoutAdversarialTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('SnapLayoutRestorePolicy.cs', 'SnapLayoutTransitionTests.cs', 'SnapLayoutTransitionAdversarialTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('SnapLayoutRetirementTests.cs', 'SnapLayoutRetirementAdversarialTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('SnapCorrectionPolicy.cs', 'SnapCorrectionPolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('SnapIntentPolicy.cs', 'SnapKeyboardObserver.cs', 'SnapWorkAreasIntent.cs', 'SnapIntentPolicyTests.cs', 'SnapIntentAdversarialTests.cs', 'SnapIntentRuntimeTests.cs', 'SnapIntentRuntimeAdversarialTests.cs', 'SnapIntentActionBoundaryTests.cs', 'SnapIntentPureSuite.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('DisplayWakePolicy.cs', 'DisplayWakeObserver.cs', 'DisplayWakePolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('WakeRecoveryDiagnostics.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('SearchPointerInputTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('DirectionalFocus.cs', 'DirectionalFocusTests.cs', 'NavigationShortcutPolicy.cs', 'NavigationShortcutPolicyTests.cs', 'TilingPicker.cs', 'TilingPickerTests.cs', 'WindowTiling.cs', 'WindowTilingTests.cs', 'MinimumWorkerTests.cs', 'KeyboardWindowController.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('FocusAppearance.cs', 'FocusAppearanceSettingsTests.cs', 'FocusIndicator.cs', 'FocusIndicatorTests.cs', 'FocusIndicatorAlphaTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('FocusIndicatorPaint.cs', 'FocusIndicatorPaintTests.cs', 'FocusIndicatorMotion.cs', 'FocusIndicatorMotionTests.cs', 'FocusIndicatorMotionIntegrationTests.cs', 'FocusFrameFinishTests.cs', 'FocusFrameFinishNativeTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('FocusIndicatorGlowTests.cs', 'FocusIndicatorGlowIntegrationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('FocusIndicatorRevealPaintTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('NativeProcessIdentity.cs', 'NativeProcessIdentityTests.cs', 'AdministratorSupport.cs', 'AdministratorSupportTests.cs', 'AdministratorPermissionSwitch.cs', 'AdministratorPermissionSwitchTests.cs', 'AdministratorUiTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('AdministratorLaunch.cs', 'AdministratorLaunchTests.cs', 'AdministratorPayloadEntry.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('TaskbarButtonReader.cs', 'TaskbarButtonSelectionTests.cs', 'TaskbarNativeSelection.cs', 'TaskbarNativeSelectionTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('FocusLightingPolicyTests.cs', 'FocusIndicatorLightingTests.cs', 'FocusIndicatorLightingIntegrationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('AppChrome.cs', 'AppLayout.cs', 'AppearanceChoices.cs', 'AppPolishTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('ScheduledBootstrapImage.cs', 'ScheduledBootstrapImageTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('NativeStopDiagnostics.cs', 'NativeStopDiagnosticsTests.cs', 'MovementValidityDiagnosticTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('ApplicationLifecycle.cs', 'ApplicationLifecycleTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('AdministratorCancellationTests.cs', 'AdministratorReconnectRecoveryTests.cs', 'AdministratorPreferenceIdentityTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('NativeTaskbarHandoff.cs', 'NativeTaskbarHandoffTests.cs', 'TaskbarMoveDiagnostics.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('TaskManagerShellIdentity.cs', 'TaskManagerShellIdentityTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('TaskbarRoutingDiagnosticTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('DeferredTaskbarClick.cs', 'DeferredTaskbarClickTests.cs', 'ManagementConsoleShellIdentityTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('OrdinaryDeferredClickIntegrationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('DesktopHookLifetime.cs', 'DesktopHookLifetimeTests.cs', 'HookDeliveryWatch.cs', 'HookDeliveryWatchTests.cs', 'TaskbarHookService.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('MouseHookPacketPolicy.cs', 'MouseHookPacketPolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('AppUpdates.cs', 'AppUpdateTests.cs', 'AppUpdateInstaller.cs', 'AppUpdateInstallerTests.cs', 'AppUpdateForm.cs', 'AppUpdateUiTests.cs', 'AccentIconTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('OwnedSnapPolicy.cs', 'OwnedSnapPolicyTests.cs', 'OwnedSnapMovement.cs', 'OwnedSnapMovementTests.cs', 'OwnedSnapKeyboard.cs', 'OwnedSnapController.cs', 'OwnedSnapPrototypeTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('OwnedSnapRoutingTests.cs', 'AdministratorWindowIndicators.cs', 'AdministratorWindowIndicatorTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$newSources += @('AdministratorSnapProtocol.cs', 'AdministratorSnapProtocolTests.cs', 'AdministratorSnapInput.cs', 'AdministratorSnapInputTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$common = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warnaserror+',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', '/r:System.Management.dll',
    ("/r:$(Join-Path $wpf 'UIAutomationClient.dll')"), ("/r:$(Join-Path $wpf 'UIAutomationTypes.dll')"), ("/r:$(Join-Path $wpf 'WindowsBase.dll')"),
    ("/win32icon:$icon"), ("/win32manifest:$manifest"), ("/resource:$icon,TaskbarSelect.Icon"))
$sources = @('AssemblyInfo.cs', 'Native.cs', 'MonitorNames.cs', 'TaskbarMover.cs', 'Program.cs', 'DisplayIdentifier.cs', 'WorkAreas.cs', 'Startup.cs', 'Tests.cs') |
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
$scheduledBootstrap = & (Join-Path $PSScriptRoot 'Build-AdministratorBootstrap.ps1') -PayloadPath $payload -OutputDirectory $scheduledOutput -NativeCompilerCache $NativeCompilerCache -ScheduledOnly
if ($null -eq $scheduledBootstrap -or -not $scheduledBootstrap.ScheduledOnly -or -not $scheduledBootstrap.BootstrapPath) { throw 'The dedicated scheduled administrator entry did not produce its verified role.' }
$bootstrap = & (Join-Path $PSScriptRoot 'Build-AdministratorBootstrap.ps1') -PayloadPath $payload -OutputDirectory $nativeOutput -NativeCompilerCache $NativeCompilerCache -ScheduledBootstrapPath $scheduledBootstrap.BootstrapPath
if ($null -eq $bootstrap -or -not $bootstrap.BootstrapPath -or -not $bootstrap.PayloadHashPath) { throw 'Native administrator bootstrap did not produce verified embedded resources.' }
& $compiler @common /main:TaskbarSelect.Program "/out:$output" ("/resource:$($bootstrap.BootstrapPath),TaskbarSelect.AdministratorBootstrap") ("/resource:$($bootstrap.PayloadHashPath),TaskbarSelect.AdministratorPayloadHash") @toolbarResources @sources @newSources
if ($LASTEXITCODE -ne 0) { throw "Normal application compiler failed: $LASTEXITCODE" }
Get-Item -LiteralPath $output | Select-Object FullName, Length
