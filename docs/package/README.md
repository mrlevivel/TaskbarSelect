# Taskbar Select

> **Beta.17 private release candidate for Windows 11 x64.** Verify the supplied archive against its matching checksum before use.

Choose which secondary monitors show the native Windows taskbar, and optionally bring windows to the monitor whose taskbar you use.

This is **1.0.0-beta.17**, with connected-display shortcuts, relative sizing for floating and snapped windows, and maximized-move Undo. Taskbar Select relies on unsupported Explorer behavior that Windows updates can change. Read the bundled `RELEASE_NOTES.md` and `TESTING.md` for the changes and verification scope.

**Native snapping limitation:** with experimental snapping Off, Windows+Left/Right and already-maximized windows can retain the former hidden-taskbar strip. Automatic correction of those native layouts remains disabled. The optional session mode below provides separate app-owned halves; native Snap Assist, groups, suggestions and quarter layouts can differ. Taskbar-click moves, explicit layouts and shortcut-minimize Undo retain their own rules.

## Start Here

1. Use 64-bit Windows 11. Its included .NET Framework normally needs no separate runtime installation. Enable **Show my taskbar on all displays** under Windows Settings > Personalization > Taskbar > Taskbar behaviors.
2. Extract the complete portable archive into its own folder and compare its checksum. Ordinary launch of `TaskbarSelect.exe` shows **Taskbar Select** and uses your existing production profile. For an isolated trial, use the command below from the extracted folder; only that explicit mode shows **isolated test**. GitHub's **Source code (zip)** is not an installer.
3. On **Displays**, use **Identify**, select a secondary monitor, and set **Show taskbar**. The main taskbar is protected. Changes save immediately; all taskbars are visible by default with fresh settings.
4. Closing the window leaves the app running in the notification area. Its tray menu can reopen, pause, or exit it. **Start with Windows** is optional.

### Isolated Portable Trial

First choose **Restore and exit** in any running Taskbar Select copy and confirm recovery succeeded. Open PowerShell in the extracted portable folder and run:

```powershell
$testProfile = Join-Path $env:LOCALAPPDATA 'TaskbarSelect-beta17-trial'
Start-Process -FilePath (Join-Path $PWD 'TaskbarSelect.exe') -ArgumentList ('--test-profile "{0}"' -f $testProfile)
```

This explicit profile keeps trial preferences separate and disables Startup changes. To recover and stop this trial, use its tray menu **Restore and exit**, or run:

```powershell
.\TaskbarSelect.exe --test-profile "$testProfile" --restore
```

The shared desktop lock still permits only one taskbar controller. Stop the trial successfully before returning to your installed copy. The portable archive does not contain the maintainer's integration-folder launchers.

Builds are unsigned and SmartScreen may warn. Checksums establish integrity, not publisher identity. Confirm the source before running; if security software blocks it, stop and check with the person providing the build. The beta.17 candidate files are **TaskbarSelect-1.0.0-beta.17-Setup.exe**, **TaskbarSelect-1.0.0-beta.17-portable.zip** and **SHA256SUMS.txt**.

Installed and portable copies share your saved choices and Startup entry. Before switching copies, choose **Restore and exit** on the running app and confirm it stopped; closing its settings window is insufficient. Upgrades preserve choices. For a fresh-install trial or uninstall, first copy `%LOCALAPPDATA%\TaskbarSelect\settings.json` and any **Taskbar Select.lnk** from `shell:startup` into a private backup, noting the shortcut target and Windows Startup-app state. Do not delete recovery files. A separate Windows user or test PC gives clean defaults without changing your usual profile.

## Guided Updates

For migration from any earlier private release, download and run beta 17 Setup manually once. It switches future update checks to this repository.

Open **Settings > Check for updates** to check for a newer official beta or stable release and read its notes. Choose **Download update** for a cancellable download, then **Install update** and explicitly confirm to open Setup. The release checksum and expected installer are verified before launch. Setup preserves preferences and Startup choices. No update check runs at startup or in the background, and nothing installs automatically.

In-app installation requires the installed production copy launched normally, without **Run as administrator**. Portable and isolated copies can check but must update manually through the fixed official releases link. If administrator support is enabled or saved, turn its existing Settings switch Off, approve Windows if requested and wait for confirmed Off before installing. A cancellation, incomplete shutdown or retained approval blocks the upgrade. Turn support On and approve Windows again in a changed build if wanted.

The GitHub repository is private. The check uses anonymous GitHub access without account tokens, so **updates unavailable is expected** even if your signed-in browser can view releases. For a candidate upgrade, run the reviewed Setup supplied by the maintainer. Do not supply credentials or change security settings.

See the bundled `TESTING.md` for the current upgrade verification scope.

## Optional Window Movement

Enable **Bring windows to the taskbar I use** under **All displays** to try movement. Click an app's button on another monitor, or choose an individual grouped thumbnail. Same-monitor clicks retain normal Windows behavior.

**Keep relative size and position** scales the window to the destination's usable desktop and remembers the original layout of a floating or snapped window through continuous moves. A smaller display can require a temporary fit, and apps can impose their own sizing limits. **Keep window size** retains pixel dimensions where they fit. Existing saved choices are preserved. **Apps > Add app** excludes an app; **Remove exclusion** lets it follow the movement setting again.

**Pause moves** leaves taskbar choices active. **Undo** reverses a recent move, layout or minimize performed with Taskbar Select's minimize shortcut. Undoing a maximized move restores the previous display and the window's saved floating size and position. The configurable Undo shortcut defaults to **Ctrl + Shift + Backspace** for new profiles. Existing saved choices, including Ctrl + Alt + Backspace or Off, are preserved.

**Shortcuts > Window movement** lists connected displays and assigns Ctrl+Shift+1–4 to Displays 1–4 by default. Assign a shortcut for any additional display. Saved shortcuts follow Windows display numbers and are kept when a display disconnects. Existing Undo bindings take priority over colliding defaults, and each row reports registration conflicts. **Allow moving fullscreen apps** is off by default and applies to every movement path. Fullscreen requests use full monitor bounds with a bounded observation and safe Undo. Rocket League works in Borderless and refuses monitor moves in exclusive Fullscreen with both Taskbar Select and Windows' monitor shortcut.

An explicitly selected test profile keeps settings and recovery separate, disables Startup changes and production uninstall, and uses separate stop/show events. It still shares the desktop-management lock with regular copies; only one copy may manage taskbars at a time.

## Experimental Snapping

Under **Shortcuts > Experimental snapping**, turn **Use experimental snapping** On only when wanted. It starts Off after every app launch and is not a saved preference. Windows+Left/Right places eligible resizable app windows in display halves and cycles through connected monitors. Each step uses full bounds where Taskbar Select hides the taskbar and the work area where it is visible. Native Snap Assist, groups, suggestions and quarter layouts may differ.

Pause moves, Pause all, exclusions and fresh target/display checks still apply. Minimized, maximized, fullscreen, shell and unverified targets do not receive app-owned snapping; administrator targets also require approved Administrator support. Each applied step can be undone through the shared history. Turning this mode Off leaves window positions unchanged and restores normal Windows key handling.

## Ghost Bar Preview

Select a secondary display with a hidden taskbar, then enable **Ghost Bar**. Hover briefly at that screen's bottom-center edge to show eligible open windows, including local windows. Hover over or click an app with several windows to open thumbnail previews and titles, with an icon/title fallback when a preview is unavailable. A remote window moves here; a local chooser selection focuses or restores without minimizing. A single-window icon also minimizes its already-active local window. Local actions preserve the previous move's undo entry, and restores preserve normal/maximized state. Leave the Ghost Bar or press Escape to dismiss it.

Grouped previews label local choices **Focus here** and remote choices **Bring to this screen**, including their accessibility actions. A minimized local choice restores in place. An unverified source does not show a guessed movement action, and selection rechecks the window before acting.

**Settings > Ghost Bar sensitivity** sets **Activation width** (80-960 physical pixels), **Activation height** (1-96 physical pixels, in one-pixel steps) and **Hover delay** (100-1,500 ms) for every Ghost Bar. Changes save immediately; new-profile defaults are 480 pixels wide, 6 pixels high and 100 ms, with saved values retained. Older settings files without a height retain the released 24-pixel fallback. A narrower or shallower strip or longer delay can reduce accidental openings. App-icon and full-size preview delays are unchanged.

Focus **Activation width** or **Activation height** to highlight each eligible Ghost Bar display's actual bottom-center activation strip and its effective dimensions. On stacked screens, try a taller strip and pause inside the upper monitor before crossing into the lower screen. The preview updates with either size, stays inside the display and disappears when you leave both size fields. It does not take focus or intercept clicks, and Ghost Bar activation is suspended while it is visible.

Ghost Bars are off by default and independent of taskbar-click movement. Exclusions, sizing and Undo apply to their cross-display moves. Local Ghost Bar focus/restore/minimize actions retain their existing behavior; only the minimize shortcut adds a minimize Undo entry. Pause moves suppresses Ghost Bars; Pause all also restores taskbars. The Ghost Bar does not reserve screen space and is suppressed over fullscreen destinations. Titles and icons are used locally and not saved in diagnostics.

## Search, Focus, Layouts and Appearance

Click Ghost Bar's search icon, or use configurable Ctrl+Shift+Space to search installed apps on the last focused display. Desktop clicks select that monitor too. Choose a result to open or bring its verified window there; press Escape, click outside or press the shortcut again to dismiss and cancel placement. Search does not execute typed commands or search documents/the web. Ctrl+Shift+W requests an app's normal close behavior, including save prompts.

Right-click a result or select it and press Apps/Shift+F10 for its options. Ordinary click, Enter and **Open or bring here** keep their existing behavior. **Open new window** is available only for supported standard Google Chrome desktop-app shortcuts with canonical Chrome identity, without profile/web-app arguments beyond the recognized launch-attribution flag. It requests a separate Chrome window and places only a uniquely verified newly observed window on the search display; it never falls back to moving an old Chrome window. If verification fails, place the opened window manually. Packaged entries, profile-specific Chrome, Chrome Remote Desktop and other apps are outside this capability. Typed search text is never added to the launch command. Query-field right-click keeps the normal text-edit menu.

Under **Shortcuts > Window actions**, Ctrl+Shift+M minimizes the focused eligible window and Ctrl+Shift+P toggles always on top. Undo restores shortcut-minimized windows through the same history as moves and layouts; earlier actions remain available below them. If a minimized window was already restored, closed or changed, one Undo press clears that obsolete entry without touching the previous window. Pinning stays a toggle and does not enter Undo. A lit top-left corner marks windows pinned by Taskbar Select and remains distinct from optional focus-frame lighting; unpinning clears it. Each binding can be changed or turned off. Both actions respect exclusions; Pause moves leaves them available and Pause all suspends them. Administrator windows need administrator support. Normal exit releases pins added by Taskbar Select, and disabling administrator support must confirm its helper's pin cleanup first. Pins are not saved across app or PC restarts. Forced termination cannot guarantee cleanup; a remaining topmost window can be unpinned after reopening the app or closed normally.

Under Shortcuts, directional focus defaults to Ctrl+Shift+Alt+arrow and the layout picker to Ctrl+Shift+Alt+Space. Pick a half/quarter/center layout with arrows and Enter, or click a tile. Fullscreen, exclusions, minimum sizes, pause and Undo rules apply; Windows can refuse an operation. Every binding is configurable and reports conflicts.

Appearance has a focus frame, Muted edge or Accent outline, four lighting choices and 17 compact accent tiles grouped by color family. New profiles start with Forest Green, the frame on, Muted edge and no lighting. Existing saved choices are retained. The frame hides during movement; reduced animation and high contrast are honored. Accents affect Taskbar Select's controls, helper panels and live window/notification-area icons, without changing Windows or other apps. Windows may cache an existing pinned shortcut's icon; executable and shortcut artwork remain unchanged.

**Settings > Administrator support** enables a separate helper and remembers approval for this exact build. Approve Windows yourself. The same approved build can reconnect after restart; a changed build needs fresh approval. Off stops helpers and removes saved approval, which may also require a Windows prompt. Cancellation or failure leaves a checked switch and an incomplete status. Normal desktop launch keeps the interface and search launches at normal permissions; Startup remains a separate choice.

## Recovery and Uninstall

Use **Pause all** to restore managed taskbars. If the app cannot be used, run this from its folder:

```powershell
.\TaskbarSelect.exe --restore
```

This targets the ordinary production profile and attempts restoration without uninstalling. For an isolated trial, include the same explicit `--test-profile` directory used to launch it, as shown above. Check errors and exit status; recovery is not guaranteed. Do not delete or edit recovery files. Automatic resizing of already-snapped or maximized windows is disabled. Such windows may retain the former taskbar strip; normal Windows restore/maximize or snapping remains under your control.

Uninstall through Windows **Settings > Apps > Installed apps > Taskbar Select** attempts recovery before removing settings and the app's owned Startup shortcut. If recovery fails, stop and keep the app files. To return to backed-up choices, stop the app, restore `settings.json` to its original folder and restore the backed-up Startup shortcut only when its original target still exists. Keep Windows' Startup-app state as recorded. Deleting a portable folder does not remove the shared settings or an existing installed copy; disable Startup only if its shortcut points to that portable folder.

## Help, Privacy, and License

Live testing is limited to one four-monitor Windows 11 PC. Consult the release page for exact-build elevated minimize/pin/Undo and reconnect acceptance; earlier elevated-movement results do not qualify new actions. Windows 10, alternative taskbars, physical mixed-DPI setups and broader protected-app compatibility remain unvalidated. See the bundled `TESTING.md` for the exact scope.

No telemetry, advertising, background update polling or automatic installation. Opening **Check for updates** deliberately contacts GitHub's official release API over unauthenticated HTTPS; choosing **Download update** also contacts its release-asset service. No settings, diagnostics, monitor information or window titles are uploaded, and no GitHub token is used. GitHub receives ordinary connection metadata such as your IP address. Settings and recovery data stay local. Review **Copy diagnostics** and screenshots before sharing; display models and layout can identify your setup. Do not post raw settings or unreviewed `--inspect` output. Opening a GitHub link uses your browser.

- [User guide](https://github.com/mrlevivel/TaskbarSelect/blob/main/docs/USER_GUIDE.md)
- [Beta tester guide](https://github.com/mrlevivel/TaskbarSelect/blob/main/docs/BETA_TEST_GUIDE.md)
- [Bug reports](https://github.com/mrlevivel/TaskbarSelect/issues/new/choose); security-sensitive reports: the bundled `SECURITY.md`
- This package includes the MIT `LICENSE` and artwork provenance in `ARTWORK.md`.

## Remembered Support and Maintenance

Use the **Settings > Administrator support** switch. On enables support and remembers approval for this build; Off stops the helper and removes saved approval. Windows permission may be needed for either action; approve it yourself using the usual account. A cancelled or failed change stays checked with an incomplete status instead of claiming Off. The same approved build can reconnect after app or PC restart without another prompt. The interface and apps opened through search keep normal permissions. Start with Windows remains your existing choice; remembered support adds no scheduled logon trigger or service.

For an upgrade or uninstall, run Setup normally. These operations will not request administrator permission themselves. If saved approval or incomplete maintenance is detected, the operation stops before closing the app or replacing its files. Open the installed app's Settings, turn **Administrator support** off, approve Windows there if requested, and wait for confirmed Off before retrying Setup. If cancellation or failure leaves the switch checked, follow its incomplete-status detail rather than assuming approval was removed. If recovery or helper shutdown cannot be confirmed, finish recovery before retrying. Do not remove protected files, settings or recovery records manually.

The new lifecycle preserves settings and Startup on upgrade, including remembered-support intent. A changed app build needs explicit approval by turning **Administrator support** on in Settings; upgrade never transfers the previous build's approval silently. Successful uninstall requires recovered taskbars and confirmed absence of the saved task and its fixed protected native/ledger files before settings and Startup are deleted. Empty protected parent directories and dormant manual launcher staging may remain.

See the bundled `TESTING.md` for beta.17 qualification and remaining checks. Existing private beta 16 installations need one manual beta 17 installation to migrate to the new updater address.
