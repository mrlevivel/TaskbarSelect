# Taskbar Select User Guide

> **Beta 18 for Windows 11 x64.** See the [testing scope](../TESTING.md) and [release page](https://github.com/mrlevivel/TaskbarSelect/releases/tag/v1.0.0-beta.18) for executed checks and known limitations.

This guide covers connected-display shortcuts, relative sizing for snapped and floating windows, and maximized-move Undo alongside existing features. Normal launch uses the production profile. The **isolated test** label appears only with an explicit trial profile.

<a id="installing-the-private-beta"></a>

## Installing the Beta

Download beta 18 from the [official release page](https://github.com/mrlevivel/TaskbarSelect/releases/tag/v1.0.0-beta.18). Choose Setup or the portable package; GitHub's source-code archives contain the project rather than a ready-to-run app.

1. Download **TaskbarSelect-1.0.0-beta.18-Setup.exe** and the matching **SHA256SUMS.txt** from that release page.
2. Compare the installer's SHA-256 with the matching line in `SHA256SUMS.txt`. In PowerShell opened in the download folder, run `Get-FileHash .\TaskbarSelect-1.0.0-beta.18-Setup.exe -Algorithm SHA256`.
3. Run Setup and keep the default per-user location unless you have a reason to choose another. No administrator access is needed. Open **Taskbar Select** from the Start menu after installation.

Use 64-bit Windows 11. Windows includes the .NET Framework runtime used by Taskbar Select, so a separate runtime installation is normally unnecessary. The app and installer are unsigned; SmartScreen may warn. A matching checksum confirms the download's integrity, not the publisher's identity. If Windows or security software blocks the build, stop and check with the person providing it; do not change security settings to complete this guide.

For a portable trial, verify **TaskbarSelect-1.0.0-beta.18-portable.zip** against the supplied checksum, extract the whole archive into a folder you can keep, and open `TaskbarSelect.exe`. It uses the same Windows-user settings and Startup entry as an installed copy. Before switching copies, use the running app's tray menu **Restore and exit**, then confirm it stopped and taskbars were restored. Closing the settings window leaves it running.

<a id="isolated-beta13-portable-trial"></a>

### Isolated Portable Trial

After extracting the portable package, use **Restore and exit** in any running Taskbar Select copy. Launch an explicit separate profile from PowerShell in the extracted folder.

```powershell
$testProfile = Join-Path $env:LOCALAPPDATA 'TaskbarSelect-beta18-trial'
Start-Process -FilePath (Join-Path $PWD 'TaskbarSelect.exe') -ArgumentList ('--test-profile "{0}"' -f $testProfile)
```

This mode disables Startup changes and production uninstall, reuses only its trial preferences, and shares a lock preventing two taskbar controllers. To recover and stop it, use its own tray **Restore and exit**, or run `.\TaskbarSelect.exe --test-profile "$testProfile" --restore`. The ordinary `--restore` command targets the normal profile; always repeat the explicit trial directory when recovering a trial. The portable package is self-contained and needs no integration-folder launcher.

### Preserve Choices Before a Fresh-Install Trial

Upgrading or extracting into a new folder preserves existing choices; it does not create a clean profile. An explicit test profile, separate Windows user or test PC gives clean defaults without changing your usual profile. With no previous settings or Startup entry, all taskbars remain visible, taskbar-click movement/Ghost Bar/startup are off, and sensitivity defaults to 480 pixels wide, 6 pixels high and 100 ms. The focus frame starts on with Muted edge and no frame lighting, using Forest Green as the app accent. Existing saved appearance choices are retained. Default shortcuts are enabled, including Ctrl+Shift+M to minimize, Ctrl+Shift+P to toggle always on top and Ctrl+Shift+Backspace to Undo; fullscreen movement remains off.

Before uninstalling or making a same-profile fresh-install trial:

If administrator support is enabled or saved, first turn **Settings > Administrator support** Off, approve Windows if requested, and wait for confirmed Off. Do not continue while removal is cancelled, failed or unconfirmed. See [administrator support](#optional-administrator-support) for approval and maintenance behavior.

1. Choose **Restore and exit** and confirm the app stopped and managed taskbars are visible. If recovery reports an error, stop and keep the app files.
2. Press **Win + R**, open `%LOCALAPPDATA%\TaskbarSelect`, and copy `settings.json`, if present, into a private backup folder. If the folder or file does not exist, there are no saved settings to back up. Do not delete or edit recovery files.
3. Press **Win + R**, open `shell:startup`, and copy **Taskbar Select.lnk**, if present, into that backup. Note its **Properties > Target** and the app's enabled/disabled state in Windows **Startup apps**. Leave other shortcuts and startup preferences unchanged.

Uninstalling through **Settings > Apps > Installed apps > Taskbar Select** removes saved settings and the app's owned Startup shortcut only after recovery succeeds. To return to backed-up choices after the trial, stop the app again, restore `settings.json` to its original folder, and restore the backed-up shortcut only when its original target still exists. Keep Windows' Startup-app state as recorded. These backups contain local preferences and paths; do not upload them with feedback.

## Taskbars and Displays

Enable **Show my taskbar on all displays** in Windows Settings > Personalization > Taskbar > Taskbar behaviors. Taskbar Select cannot create secondary taskbars when Windows has disabled them.

Use **Identify** to show the Windows display number on each screen. Select a monitor in the map, then set **Show taskbar**. The main display's taskbar is protected and cannot be hidden. Changes save immediately; a first launch with fresh settings leaves all taskbars visible until you make a selection. Existing choices are preserved.

Monitor labels use the manufacturer and model reported through EDID, falling back to manufacturer/product identifiers when a friendly name is unavailable. **Nickname** adds a local label; press Enter or leave the field to save it. It does not rename the monitor in Windows.

Choices follow physical monitor identity rather than just a display number. Unknown, replaced, or ambiguous monitors default to visible. A cable or GPU-port change can require selecting a monitor again.

Hidden taskbars release their reserved space. An already-maximized window may need restoring and maximizing again to use it. Explorer can briefly recreate taskbars after display or shell changes; the app periodically rediscovers and manages them.

Hover over an information marker or control for help. Closing the settings window leaves the app running in the notification area; its tray menu can reopen it, pause management, restore taskbars, or exit.

## Window Movement

**Bring windows to the taskbar I use**, under **All displays**, is an optional experiment, off by default. Click an app's taskbar button on another monitor to bring its window there. For grouped windows, hover or click to open the thumbnails, then choose one; only that selected window should move. Same-monitor clicks retain normal Windows behavior, including native grouped-window previews.

Start, Search, Widgets, Task View and tray controls are excluded. Fullscreen windows require the separate **Allow moving fullscreen apps** opt-in. Deliberate icon drags are handed back to Windows for reordering. If the app cannot verify the selected window, it leaves the interaction to Windows instead of guessing. Clicks are not recorded and Windows' global drag settings are not changed.

### Window Sizing

The sizing choice applies to future monitor-shortcut, taskbar and Ghost Bar moves across all displays:

- **Keep relative size and position** (default): scale the window's dimensions and placement in proportion to each monitor's usable desktop. A lower, centered window should retain roughly that arrangement on a smaller display.
- **Keep window size**: retain pixel dimensions where they fit and constrain oversized windows to the destination's usable desktop.

The default applies when no sizing choice is saved. Existing saved choices are preserved.

With relative sizing, a continuous series of moves remembers the original layout of a floating or snapped window. If a smaller display requires a temporary fit, returning to the original display restores the remembered size and position when the app accepts them. Apps can impose minimum sizes or adjust to display scaling. Maximized windows stay maximized, and Undo restores the previous display and the window's saved floating size and position. Changing the setting does not rearrange windows already on screen.

### App Exclusions

On **Apps**, choose **Add app** and select a running application to leave on its current monitor. All of that app's windows retain native Windows behavior. **Remove exclusion** makes the selected app follow the global movement setting again; it does not change the app's own settings.

Desktop apps match their executable path; packaged apps match their Windows app identity. Excluded apps also stay out of the Ghost Bar.

### Monitor Shortcuts and Fullscreen Movement (Beta.8)

**Shortcuts > Window movement** shows one row per connected Windows display. Displays 1–4 default to Ctrl+Shift+1 through Ctrl+Shift+4. Choose a shortcut for any additional display. Bindings follow Windows display numbers and stay saved while a display is disconnected. They activate when the corresponding display number returns. Shortcuts work independently of taskbar-click movement. Pause moves and Pause all suspend them. The Taskbar Select Settings window can move and be undone like other eligible windows.

Your existing Undo binding is preserved. If it uses a new default monitor chord, that monitor binding starts off. Each binding reports registration conflicts; failed edits keep the previous registrations. Unrelated settings changes remain available after legacy migration.

**Allow moving fullscreen apps** starts off and applies to taskbar clicks, Ghost Bar selection and monitor shortcuts. Opt-in movement requests the full destination monitor bounds and reports a bounded observation afterward. Apps can refuse or move back. Rocket League works in Borderless; its Fullscreen mode fails with both Taskbar Select and Windows' monitor shortcut. Undo remains available when safe even after the option is turned off.

### Directional Focus and Window Layouts

**Shortcuts > Focus** configures four directional focus shortcuts, and **Shortcuts > Layout** configures the layout picker. Their defaults are **Ctrl+Shift+Alt+Left/Right/Up/Down** and **Ctrl+Shift+Alt+Space**. Existing monitor, Undo, close and search choices take priority; conflicting new defaults start off. Each binding can be changed or turned off, and registration failures retain the previous usable binding.

Directional focus selects an eligible window in the chosen physical direction, prioritizing windows aligned with the current window. It works with side-by-side and stacked positions, does not wrap at an edge, and does not resize windows or replace Undo history. Minimized, excluded, hidden and helper windows are skipped. Windows can refuse focus.

The layout picker opens on the focused window's monitor. Use plain arrow keys to choose a half, quarter or centered layout, then Enter to apply; clicking a tile also applies it. Center keeps the current size where it fits. Escape, an outside click, or repeating the picker shortcut closes it without applying. This arranges one window when requested; it does not automatically rearrange other windows.

Layouts respect the monitor's usable area and the app's minimum size. A large minimum size can expand a tile; layouts that cannot fit are refused. Fixed-size apps can be centered but are not resized. Undo uses your existing shortcut and restores the previous position and normal/maximized state when recovery remains safe. A target that closes, moves, changes identity or changes display before selection is refused. Pause, exclusions and the fullscreen option apply to these actions; elevated apps and Windows focus restrictions may prevent an operation.

### Experimental snapping (session opt-in)

Turn **Shortcuts > Experimental snapping > Use experimental snapping** On to let Taskbar Select handle Windows+Left/Right for eligible app windows. It starts Off every time the app launches, including after an ordinary exit or restart. Enable it again only when wanted. Turning it Off leaves the current window positions in place and returns those keys to Windows.

The first press places the window in that side's half of its display. Repeating toward the outer edge cycles to the opposite half of the next display in that direction, wrapping around connected displays. Each step uses full display bounds where Taskbar Select hides the taskbar and the work area where the taskbar is visible. This is a separate app-owned layout mode: native Snap Assist, Snap groups, suggestions, quarter layouts and continuation behavior can differ. It does not automatically correct existing native snapped or maximized windows.

The mode handles eligible resizable windows that are neither minimized nor maximized. Exclusions, fullscreen refusal, verified foreground/window/display identity, Pause moves and Pause all still apply. Administrator windows also need approved **Administrator support**; uncertain or unsupported targets do not grant app-owned movement. Each applied step enters the shared Undo history. Turning the mode Off does not undo earlier steps; use **Undo** when safe.

### Window actions and Ghost Bar search

The default shortcuts are Ctrl+Shift+1–4 for monitor movement, Ctrl+Shift+Backspace for Undo, **Ctrl+Shift+W** for a normal close request, **Ctrl+Shift+M** to minimize, **Ctrl+Shift+P** to toggle always on top, and **Ctrl+Shift+Space** for animated app search. Change or turn off the window actions in **Shortcuts > Window actions**. Saved bindings are retained; an existing saved binding wins over a colliding new default. Existing app shortcuts using these chords are overridden while the global binding is active; a chord registered by another application is reported as unavailable without discarding your previous binding or blocking unrelated settings changes.

Keyboard search opens on the most recently selected monitor: clicking its desktop counts even when Windows leaves the previous app focused. Focusing or moving an app to another monitor also updates the selection. If that display is no longer available, search falls back to the main monitor. It works on the main display and displays without Ghost Bar. The destination remains fixed while you type or an app opens. Search requests and verifies typing focus. Its shortcut toggles the panel: press once to open, again to close, then again after dismissal to open a fresh search. Press Enter or click a result to open or bring its verified window there. Escape, the close button or an outside click also dismisses search and cancels pending placement; the outside click still reaches its target. Pause moves disables search/placement; Pause all disables every action shortcut. Closing uses the app's normal behavior, so a save prompt, tray hiding, or an app refusing to close remains possible. It does not force termination, alter Undo, or close the desktop/taskbar.

Ghost Bar includes a **Search apps** magnifying-glass button. Click it to expand the search bar, type an installed app name, then click a result or use Up/Down and Enter. The app opens or its existing verified window is brought to this Ghost Bar's display, using your sizing, exclusions, fullscreen choice and Undo behavior. Escape or the close button dismisses the panel. It stays open while you type even after the pointer leaves the activation strip.

Minimize and always on top operate on the focused eligible app window and respect exclusions. Minimize uses the window's normal minimize behavior. A confirmed shortcut minimize enters the same Undo history as window moves and layouts. **Ctrl+Shift+Backspace**, the **Undo** button or the tray's **Undo last window action** restores its previous normal, maximized or snapped state when safe. Earlier actions remain below it, and Undo reverses interleaved actions in order. If that window was already restored, closed or changed, one Undo press clears the obsolete minimize entry without touching an earlier window; press again for the preceding action. Unconfirmed restores retain safe retry state. This covers Taskbar Select's minimize shortcut, not unrelated Windows or taskbar minimization.

Always on top keeps a window above other ordinary windows without moving or resizing it; a lit top-left corner marks windows pinned by Taskbar Select. That corner remains distinct from optional focus-frame lighting. Press the shortcut again to unpin and clear the indicator. Pinning does not add an Undo entry. A status message confirms pinning or unpinning. Minimize and pin remain available during **Pause moves** and stop during **Pause all**; Undo retains the existing recovery behavior during pauses. Administrator windows require administrator support. Desktop, taskbars, Start and Search surfaces are excluded.

Pins last for the current Taskbar Select session. Normal exit releases pins Taskbar Select added, while leaving unrelated topmost windows alone. Turning administrator support Off also releases pins owned by its helper and waits for confirmation. A forced termination or Windows crash cannot guarantee cleanup; if a window remains on top, toggle it off after reopening Taskbar Select or close that window. Pin choices are not restored after restarting the app or PC.

Search reads app shortcuts from the user/shared Start Menu and packaged Windows apps; it does not search documents, the web, or execute typed commands. Discovery runs in the background. Some launcher shortcuts cannot provide an exact window identity; their app may open with a message asking you to place it manually. If several matching windows are already open, choose the window from the Ghost Bar's normal previews. Pausing, changing relevant movement settings, or disconnecting/changing the destination cancels pending placement. A launch already handed to Windows can still open the app; cancelled search will not move it afterward. Search text and results are not saved.

Right-click a result, or select it and press Apps/Shift+F10, for **Open or bring here** and any supported extra action. Ordinary click, Enter and **Open or bring here** keep their existing behavior. **Open new window** is available only for supported standard Google Chrome desktop-app shortcuts with canonical Chrome identity and no profile, web-app or other launch arguments beyond the recognized attribution flag. Packaged entries, other browsers/apps, profile-specific Chrome shortcuts and Chrome Remote Desktop are outside this capability.

**Open new window** requests a separate Chrome window at normal permissions. Only a uniquely verified window newly observed after that request can be placed on the captured search display. It does not move a preexisting Chrome window as a fallback. If Chrome opens but its new window cannot be verified, place it manually. Typed search text is never added to the launch command. Right-clicking inside the search field retains the normal text-edit menu.

Select a secondary display whose taskbar is hidden, then enable **Ghost Bar**. Briefly hover at the bottom-center edge of that screen to see eligible open windows, including those already on this monitor. Hover over or click an app with several windows to open thumbnail previews with titles, even when some windows are local. Choose a remote window to bring it here; choose a local window to focus or restore it without minimizing it. A single-window icon also minimizes its already-active local window. Local actions do not replace the last move's undo entry, and restores preserve normal/maximized state. Move the pointer away or press Escape to dismiss the Ghost Bar. If Windows cannot supply a preview, the title and icon remain available; previewing never restores a minimized window.

In **Settings > Ghost Bar sensitivity**, set **Activation width** (80-960 physical pixels), **Activation height** (1-96 physical pixels) and **Hover delay** (100-1,500 ms). These settings save immediately and apply to every Ghost Bar. New profiles default to 480 pixels wide, 6 pixels high and 100 ms; a narrower or shallower strip or longer delay can reduce accidental openings near playback controls. Height changes in one-pixel steps. Existing saved values are retained, with the released 24-pixel fallback when an older settings file has no saved height. The strip stays centered and wholly inside each display. On stacked screens, try a taller activation area and pause inside the upper monitor's bottom-center strip before the pointer crosses into the lower screen. Changing sensitivity starts a fresh hover; app-icon and full-size preview delays keep their existing timing.

Focus **Activation width** or **Activation height** to preview the actual activation strip on eligible Ghost Bar displays. Adjusting either value updates the highlighted area and its effective width × height label. The area fits within the display; its dimensions stay in physical pixels at different DPI scales. Leaving both size fields hides the preview. The highlight keeps focus on the setting and lets clicks pass through; Ghost Bar activation is suspended until the preview ends.

Grouped-preview captions show **Focus here** for a window already on the Ghost Bar's display and **Bring to this screen** for one on another display. **Focus here** also restores a minimized local window in place. Assistive technology exposes the same action while retaining the full window title. If the source cannot be verified, the preview does not promise a move; selection rechecks the window before acting.

If an app hides its minimized window, the Ghost Bar remembers a window minimized through its own icon so you can select it again to restore it. These restore entries last only for the current Taskbar Select session and do not include unrelated hidden windows. After restarting Taskbar Select, reopen an already-hidden app from its own tray icon once to make it available again.

Hover briefly over a window thumbnail for a temporary full-size live preview on the window's current monitor. Move away to dismiss it; only clicking focuses the real window or moves it to the Ghost Bar's monitor. Large previews fit within the usable screen area. Minimized or tray-hidden windows are not restored by hover and retain their existing thumbnail or fallback until selected.

Each display is opt-in, and old configurations leave Ghost Bars off. The Ghost Bar is independent of taskbar-click movement, uses the same sizing/exclusions/undo, and reserves no work area. It is unavailable on the main display, when the native taskbar is visible, while movement is paused, and over fullscreen destinations. The [beta tester guide](BETA_TEST_GUIDE.md) covers optional live checks.

Grouped previews appear from their app icon. They remain open while you cross into them, then retreat after you leave that icon and its previews, including when you move onto a neighboring icon. Returning during the exit keeps them open. The effect respects Windows' client-area animation preference.

The Ghost Bar uses a short fade and slight upward reveal, with a quicker fade when the pointer leaves. Icon hit targets stay fixed during motion. Windows' reduced-animation preference, high contrast and opaque fallback disable this animation; selecting a window, pressing Escape or pausing dismisses the Ghost Bar immediately.

Grouped previews float without an enclosing panel. Hover over a preview for its title, action label and a thin outline. Captions stay visible for unavailable previews and when Windows transparency is disabled or high contrast is enabled; full names remain available to assistive technology.

### Pause and Undo

- **Pause moves** suspends movement while retaining taskbar visibility choices.
- **Pause all** also attempts to restore managed taskbars and work areas, keeping saved choices for resuming.
- **Undo** reverses the latest move, layout or minimize performed with Taskbar Select's minimize shortcut, including the previous position and maximized state when safe. Its shortcut defaults to **Ctrl + Shift + Backspace** for new profiles and settings without a saved Undo choice. Existing saved shortcuts, including Ctrl + Alt + Backspace and Off, are preserved; change or disable it in **Shortcuts > Window movement**. If another app already owns your chosen shortcut, its row reports the registration conflict and keeps the previous usable binding.

Up to ten moves are held in memory for the current session. A closed window, disconnected display, or changed work area can make undo unavailable.

Normal exit, Pause all and showing an individual taskbar restore managed taskbars and their reserved work areas with bounded recovery checks. Showing one taskbar leaves other hidden displays unchanged. If recovery cannot be confirmed, movement stays paused and the status explains how to retry; reconnect a missing display before retrying recovery.

**Native snapping limitation:** with experimental snapping Off, Windows+Left/Right can leave the former hidden-taskbar strip, and an already-maximized window can retain that reservation. Automatic correction of those native layouts remains disabled. The session opt-in above supplies separate half-window placement without promising full native Snap compatibility. Explicit taskbar-click moves, layouts and shortcut-minimize Undo retain their own rules. Earlier automatic-correction experiments remain historical validation.

## Focus Appearance

The app has a compact header, quieter section tabs, squared settings cards, and coordinated controls. Five sections keep every existing setting available: Displays, Apps, Shortcuts, Appearance and Settings. Shortcuts groups window movement and Undo, directional focus, the layout picker, and app actions in one place. Binding buttons align beside their actions; status messages wrap underneath, growing for conflicts and shrinking when cleared. Pages scroll at smaller window sizes; Undo and the pause controls stay in the footer. Changes save immediately. An **ISOLATED TEST** badge and the window title identify a separate test profile; normal launch keeps the production title and Startup behavior.

In **Appearance**, use **Focus frame** and choose a **Frame style**. **Muted edge** keeps the neutral outer line and tinted inner line, two pixels thick at 100% display scaling. **Accent outline** uses one crisp pixel in the selected accent color at 100% scaling, with no gray band. Both scale with the display and follow the window's own corners. Frame style samples stay unlit so you can compare their edges; selecting lighting does not change those samples. The combined preview shows your frame style, lighting and color together. New profiles start with the frame on, Muted edge selected and lighting set to None. Existing saved Off remains off; old files without appearance preferences retain their released behavior. Opening the app does not rewrite that preference. Use Tab to reach the switch or a choice group, Space to toggle the frame, and arrow keys or Space to choose the frame style, lighting and colors; changes save immediately.

**Accent color** starts with Forest Green in **Default** for new profiles. Compact tiles show a color square beside each name and wrap to the available space. Other swatches are grouped into **Warm** (Ruby, Rose, Coral, Peach, Blaze Orange, Amber), **Greens** (Teal, Mint, Lime, Olive), **Cool** (Sky, Cobalt, Lavender, Violet), and **Neutrals** (Sand, Slate). All existing shades and saved choices are retained, including the former Teal default. Your choice applies to Taskbar Select controls, Ghost Bar, search, the layout picker, focus frame, and live window and notification-area icons, and saves immediately. Windows may retain a cached icon for an existing pinned shortcut; the installed executable and shortcut artwork are unchanged. The groups share one selection; arrow keys follow their displayed order, and smaller windows scroll to every swatch.

**Frame lighting** offers **None**, **Soft glow**, **Illuminated**, and **Energy flow**. None keeps a clean outline. Soft glow adds a gentle halo while retaining that outline. Illuminated provides a brighter edge with a stronger, steady glow. Energy flow adds a traveling highlight and a colored tail; with Accent outline, the glow brightens locally as the highlight passes. The window interior stays transparent. Existing Subtle glow preferences use Soft glow; new profiles have no lighting by default. Light is clipped at display edges. Windows' reduced-animation preference makes Energy flow static; high contrast suppresses decorative lighting without clearing the saved choice. Turning **Focus frame** off removes the entire cue.

The frame-style and lighting cards keep their descriptions and selected values visible when Focus frame is off. Turn the switch on to enable them again; your frame style, lighting and accent preferences are retained. Soft glow can add its halo to either frame style. With Accent outline, illuminated and flowing lighting retain the crisp accent-colored edge. Previews illustrate a synthetic window inside Taskbar Select and never capture the desktop or open an extra focus overlay. The previews are static, including the Energy flow example; the real Energy flow frame animates when Windows permits animation.

The cue follows the focused eligible window and lets clicks reach the app. It hides while dragging or resizing, then fills from two sides over roughly 420 ms and settles into the chosen frame over another 180 ms. Rounded corners use smooth alpha edges; snapped and maximized windows use square corners. Windows' reduced-animation preference and high contrast show the complete frame immediately. It also hides during Pause all, Pause moves, fullscreen, minimization, native menus, Windows Start and Windows Search. Turning it off removes the cue immediately.

## Optional Administrator Support

Use the **Settings → Administrator support** switch for eligible administrator windows such as Task Manager. On asks Windows for approval and remembers this exact build. Once approved, the same build can reconnect after the app or PC restarts without another prompt. Start with Windows is a separate optional setting: remembering support does not turn it on. A changed build needs fresh approval.

Off stops the session helpers and removes saved approval. Windows may ask for permission to remove the protected registration. Wait for confirmed Off. Cancelling or a failure leaves the switch checked with an incomplete status; follow its detail and retry instead of deleting protected files. Normal window controls remain available when administrator support is unavailable. Permission changes keep focus on the switch rather than selecting Ghost Bar sensitivity.

The interface, settings, Ghost Bar and apps opened through search keep normal permissions when launched normally from the desktop. Administrator support uses a separate protected helper, fixed per-user launcher storage and an on-demand scheduled task pinned to the approved build and profile. The task has no logon trigger; there is no new service. Launch Taskbar Select normally rather than from an elevated caller. Movement, Undo, exclusions and fullscreen rules remain in force; same-monitor taskbar clicks still minimize normally. Windows UAC and protected system interfaces are outside this feature. See the [current testing scope](../TESTING.md) for the exact candidate and protected-window coverage.
## Startup, Updates, and Uninstall

**Start with Windows** creates the app's own current-user Startup-folder shortcut and starts quietly with saved choices. It is optional. If Windows has disabled the entry, use **Startup apps** to re-enable it. Taskbar Select reports Windows' decision rather than overriding startup approval records.

The per-user installer creates a searchable Start menu shortcut and runs at normal permissions. For an upgrade from beta.13 or older, download and run the supplied Setup manually; those versions have no updater. The deliberate guided flow was introduced in beta.14. Both paths preserve settings and the owned Startup shortcut. Before upgrade or uninstall, turn Administrator support Off in the app and wait for confirmation. Setup and uninstall stop safely if saved approval, incomplete helper shutdown or recovery remains; they do not request UAC on your behalf. Reopen the app, complete the requested cleanup, and retry. Builds are unsigned; checksums establish integrity, not publisher identity.

### Guided Updates

1. Open **Settings > Check for updates**. This checks for a newer official beta or stable release only when you ask. Read the available version and its release notes.
2. Choose **Download update**. Progress is shown, and **Cancel download** stops it without installing. The download is checked against the release's checksum and expected installer before installation is offered.
3. If Administrator support is enabled or saved, use its existing Settings switch to turn it Off. Approve Windows' removal prompt yourself if requested and wait for confirmed Off, then return to the update window. A cancelled prompt, incomplete state or retained saved approval does not count as Off.
4. Choose **Install update** and confirm. Setup opens to perform the upgrade; saved preferences and Startup choices are preserved. Once the new version opens, turn Administrator support On and approve Windows again if you want it. Approval belongs to the exact build and is not transferred silently.

In-app installation is available only from the verified installed production app opened normally from Windows, without **Run as administrator**. A portable or isolated copy can check releases but cannot replace the installed app; use **View releases on GitHub** and update manually. Downloading and opening Setup are separate explicit actions. The app and installer remain unsigned, so Windows may warn or block them. Stop and verify the source if blocked; do not disable security settings.

The updater reads published releases through anonymous GitHub access. A private repository is unavailable to it even if your signed-in browser can view that repository; draft releases are not offered as updates. While a build is being reviewed privately, use the reviewed Setup provided by the maintainer. The updater does not use your browser session, ask for a password or accept a GitHub token. See the release page for the exact public-feed and updater validation scope.

See the [beta.18 testing scope](../TESTING.md) for current package and guided-upgrade qualification.

### Uninstall

Uninstall first attempts recovery, then removes settings and the app's owned Startup shortcut. It is blocked if recovery cannot finish safely. Follow the [install and backup instructions](#installing-the-beta) to preserve settings, the shortcut and Windows' Startup-app state for a later reinstall. Stop and keep the app files if recovery fails.

Successful uninstall requires confirmed absence of saved approval and recovery before deleting settings and Startup. Dormant manual-launcher cache bytes and empty protected cache parent directories may remain; no reusable saved grant or running helper is retained after successful cleanup. Unexpected files are preserved rather than removed recursively. Do not remove protected files or recovery records manually.

## Recovery

Use **Pause all** first. After forced termination, reopen the app or run this from the folder containing the executable:

```powershell
.\TaskbarSelect.exe --restore
```

This stops the running instance and attempts to restore managed taskbars and work areas without uninstalling. Check errors and the exit status; restoration is not guaranteed. Stop and report an unresolved problem rather than repeatedly changing recovery files.

Work-area changes are journaled before being applied. Recovery restores only work-area rectangles still owned by Taskbar Select so it does not overwrite a later change made by another app. The main display's taskbar and work area are never changed. Automatic geometry correction of other apps' snapped or maximized windows is disabled; explicit window moves, layouts and their Undo use their separate operation rules.

A malformed journal is preserved as an `.invalid-*.bak` file. Native taskbar restoration still runs, but exact saved work areas may be unavailable; replacement geometry is not guessed. Read/write errors are reported, and unsafe uninstall cleanup is blocked. Do not delete or edit recovery files to test this behavior.

## Privacy and Diagnostics

The app has no telemetry, analytics, advertising, background update polling or automatic installation. **Check for updates** deliberately makes unauthenticated HTTPS requests to GitHub's official release API. Choosing **Download update** also requests its checksum and installer from GitHub and the allowed release-asset service. No requests run at startup or periodically. No GitHub credentials or tokens are requested, stored or sent, and local settings, diagnostics, monitor information and window titles are not uploaded. Like other HTTPS connections, GitHub receives connection metadata such as your IP address and a generic updater user-agent. **Open GitHub repository** and **View releases on GitHub** open your browser, whose own sign-in and privacy settings are separate.

Settings and the recovery journal live under `%LOCALAPPDATA%\TaskbarSelect`. They can contain monitor identities, nicknames, and executable paths for excluded apps. The app picker scans running window identities only when opened or refreshed. Enabled Ghost Bars inspect window geometry for fullscreen suppression and read eligible window titles/app icons when deliberately revealed. Grouped previews use native Desktop Window Manager thumbnails while the chooser is open. Titles and icons stay in memory for the Ghost Bar session; the app does not save screenshots or create window-title logs.

**Copy diagnostics** in Settings includes app/Windows version, startup and movement state, display models, sizes, positions, selection state, and taskbar counts. It omits raw monitor identities, nicknames, app names, and exclusion paths. Display models and layout can still identify your setup: review and redact all output and screenshots before sharing.

Command-line options:

| Option | Behavior |
| --- | --- |
| `--restore` | Stop the instance and attempt recovery; check errors and exit status. |
| `--startup` | Start quietly in the notification area. |
| `--self-test` | Run automated checks without manipulating Explorer taskbars. |
| `--self-test-updates` | Run focused updater tests with neutral fixtures, without contacting GitHub or starting Setup. |
| `--inspect` | Print raw monitor/taskbar identities and a local configuration path. Do not post unreviewed output. |
| `--test-profile <absolute-directory>` | Explicitly isolate settings/recovery for testing. The directory must not overlap the production profile. Startup and production uninstall are unavailable in this mode; stop/show commands are separate, while the desktop taskbar-management lock is shared. |
| `--verify-production` | Read-only package invariant check. It does not execute installer or Startup lifecycle tests. |

Use the [bug report form](https://github.com/mrlevivel/TaskbarSelect/issues/new/choose) for compatibility problems and [SECURITY.md](../SECURITY.md) for security-sensitive reports. The [beta tester guide](BETA_TEST_GUIDE.md) suggests short, voluntary checks without requiring a restart or uninstall.
