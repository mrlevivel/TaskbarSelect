# Beta Tester Guide

> **Beta 20 for Windows 11 x64.** Use the [official release page](https://github.com/mrlevivel/TaskbarSelect/releases/tag/v1.0.0-beta.20) for downloads and exact-build validation.

Beta 20 adds an optional **Start minimized to tray** switch for manual launches. Download the installer or portable package and matching checksums from the release page. The executable and installer are unsigned. Taskbar Select relies on unsupported Windows taskbar behavior. Save work first and stop if taskbars or windows behave unexpectedly.

See the [testing scope](../TESTING.md), [release notes](../RELEASE_NOTES.md) and [beta 20 release page](https://github.com/mrlevivel/TaskbarSelect/releases/tag/v1.0.0-beta.20) for candidate evidence. Real mixed-DPI and stacked/disconnected hardware, different-account UAC, prolonged lock/wake, and other PCs remain important gaps.

## Download and Install

1. Download the beta 20 installer and checksum file from the official release page.
2. Compare the SHA-256 of **TaskbarSelect-1.0.0-beta.20-Setup.exe** with its matching line in **SHA256SUMS.txt** before running it.
3. Use Windows 11 x64, whose included .NET Framework normally needs no extra runtime installation. Run Setup for your Windows user without administrator access, then open **Taskbar Select** from Start. SmartScreen may warn because this build is unsigned; a checksum verifies integrity rather than publisher identity. If it is blocked, stop and check with the person providing the build rather than changing security settings.

Before running Setup or uninstall, turn **Administrator support** Off and approve Windows if requested; wait for confirmed Off. Cancelled or failed removal remains incomplete. Before a fresh-install trial or uninstall, use **Restore and exit**, confirm recovery succeeded and the app stopped, then privately back up `%LOCALAPPDATA%\TaskbarSelect\settings.json` and any **Taskbar Select.lnk** in `shell:startup`. Note the shortcut target and Windows Startup-app state. Uninstall removes saved choices and the owned Startup entry after successful recovery and confirmed approval absence. Stop and keep the app files if recovery fails. The [user guide](USER_GUIDE.md#installing-the-beta) explains backup and returning to those choices.

Upgrades are designed to preserve settings. For a portable trial, verify and extract **TaskbarSelect-1.0.0-beta.20-portable.zip** completely before running the EXE. It shares settings and Startup with the installed app; opening another copy while one is active opens that existing copy. A new folder does not create clean defaults. Use an explicit test profile, a separate Windows user or a test PC for a clean first run without resetting your normal profile. Do not delete recovery files or change Startup simply to test installation.

For an isolated portable trial, follow the [separate-profile command](USER_GUIDE.md#isolated-portable-trial). The extracted package needs no maintainer launcher. Restore and stop that explicit profile before returning to the installed app.

## Short Check

Taskbar-click movement and Ghost Bar checks are optional; skip steps 2-4 and 7 to leave those features off. Monitor shortcuts have separate defaults and can be turned off under **Shortcuts > Window movement**.

1. **First run and visibility:** Note the version shown in Settings and whether you used the installer or portable ZIP. With fresh settings and no existing Startup entry, all taskbars should remain visible, with taskbar-click movement, Ghost Bar and Startup off. Monitor shortcuts start enabled as Ctrl+Shift+1–4; fullscreen movement starts off. Existing settings should be preserved; do not delete them to simulate a first run. Enable Windows' **Show my taskbar on all displays**, use **Identify**, and try hiding then showing one secondary taskbar. The main taskbar must stay visible. Check a maximized window's usable space; restoring and maximizing it again may be necessary.
2. **Optional movement:** Leave movement off unless you want to test it. Use a disposable, non-elevated window with no unsaved work, enable **Bring windows to the taskbar I use**, and click its button on another monitor's taskbar. It should move there. Try **Undo move**. **Keep relative size and position** is the default when no sizing choice is saved; existing saved choices are preserved. Optionally compare **Keep window size**. Apps can impose their own sizing limits.
3. **Same-monitor minimize:** With movement enabled and the test window active, click its button on that same monitor. Normal Windows minimize behavior should remain intact; click again to restore it.
4. **Grouped thumbnails:** Open two harmless windows of one app. On another monitor, open that app's taskbar previews by hovering or clicking its icon, then choose one thumbnail. Only the selected window should move. Note skips or the wrong window moving; do not repeatedly retry on important work.
5. **Pause and recovery:** **Pause moves** should stop movement while retaining taskbar visibility choices. **Pause all** should attempt to restore managed taskbars and work areas while keeping saved choices. Check the result and any errors before resuming. Return movement and visibility choices to your preferred state when finished.
6. **Optional startup:** Inspect **Start with Windows** and, if relevant, Windows **Startup apps**. Report a Windows-disabled entry. To try **Start minimized to tray**, turn it On, choose **Restore and exit** from the tray menu, and launch Taskbar Select again. Settings should stay closed on that launch and open when you launch again while the app is running. Return the switch to your preferred setting. Report natural sign-in behavior when you next encounter it.
7. **Optional Ghost Bar:** Hide one secondary taskbar and enable **Ghost Bar** there. Fresh-profile sensitivity defaults are 480 physical pixels wide, 6 high and 100 ms; saved values stay intact, and an older settings file with no height retains its 24-pixel fallback. Focus **Settings > Activation width** or **Activation height** and check that the strip updates and disappears when you leave both fields. On a stacked upper display, try a taller strip and pause inside it before crossing into the lower screen. Ghost Bar activation is suspended during the preview. In a grouped chooser, **Focus here** should focus or restore a local window, while **Bring to this screen** should move only the selected remote window. Local actions should preserve the previous cross-display Undo.
8. **Monitor shortcuts:** Ctrl+Shift+1–4 targets the Windows Display numbers shown in the app, including movement of its Settings window. New-profile Undo is Ctrl+Shift+Backspace; saved bindings remain unchanged. Check the registration status before using a chord. Fullscreen movement is off by default; Rocket League's exclusive Fullscreen limitation is documented, so use Borderless for that game.

9. **Optional search and close:** Use a disposable app with no unsaved work. Open search from Ghost Bar or Ctrl+Shift+Space; check typing focus, the destination after a desktop click, repeated-shortcut dismissal and outside-click dismissal. Request a verified app result, and note manual-placement messages for ambiguous launchers. Ctrl+Shift+W should request normal closing, allowing save prompts rather than force-killing an app.
10. **Optional focus and layout:** Check the binding status under Shortcuts, then try directional focus and the layout picker on disposable windows. Escape/repeated shortcut should cancel; an applied layout should respect usable space and minimum size and be recoverable with Undo when safe.
11. **Optional appearance:** In Appearance, try Focus frame, both frame styles, lighting and several colors. A fresh profile starts with Forest Green, Muted edge and no lighting; saved choices remain unchanged. Tiles should remain readable, and exactly one color should be selected across all groups. The cue should hide while dragging/resizing and return smoothly. Return these choices to your preferred state.
12. **Optional administrator support:** only if you want to test administrator windows, enable support in Settings and approve Windows' prompt yourself. Use your usual account and an eligible disposable window; do not alter security settings. On remembers approval for this exact build through app or PC restart; Off stops the helper and removes saved approval, which may require another Windows prompt. A cancellation leaves an incomplete status rather than confirmed Off. The native launcher cache is separate from preferences at `%LOCALAPPDATA%\Temp\TaskbarSelect.Launchers`; the approved managed helper remains in protected Windows Common Application Data. Record whether enable, movement and Undo actually succeeded, then disable support if it is not wanted.

## Optional Beta 20 Check

**Monitor names.** Note the display labels and any saved nicknames. In Windows **Settings > System > Display**, disconnect one secondary display if Windows offers that option. Verify the remaining displays retain their labels and nicknames. Restore the original display mode and confirm the returning display's label and nickname.

## Optional Beta.18 Checks

**App search placement.** Focus a window on the destination display, press Ctrl+Shift+Space, search for a direct desktop app such as Telegram and select it. Check both an already running window on another display and a fully closed app started by search. Each should arrive on the search display. If several windows match or placement is unavailable, retain the message instead of repeatedly launching the app.

**Connected-display shortcuts.** Save a movement shortcut for a secondary display and check that its row disappears on disconnect. Reconnect it and verify the binding when the same Windows display number returns.

**Relative size and Undo.** Move a narrow floating or snapped window through a smaller display and back without manually changing its layout. On return, check the starting size and position. Then move a maximized window and Undo the move. Confirm the previous monitor and maximized state, then restore the window to check its saved floating size and position.

## Other Optional Checks

**Experimental snapping:** it must be Off after each launch. Only if wanted, turn **Shortcuts > Experimental snapping > Use experimental snapping** On and try Windows+Left/Right on a disposable, resizable app window. Compare a display whose taskbar is hidden with one whose taskbar is visible, then try repeated presses across monitors and Undo. Pause moves should suspend this mode. Turning it Off leaves positions in place and restores normal Windows handling; restarting the app must leave it Off. Native Snap Assist, groups, suggestions and quarter layouts may differ. Keep administrator support Off unless you independently want the separate approved-helper check.

**Chrome new window:** normal click and Enter still open or bring the verified app window. For a supported standard Google Chrome desktop-app shortcut, right-click the result or use Apps/Shift+F10 and choose **Open new window** only if you want another Chrome window. Existing Chrome windows must not move as a fallback. Record whether the newly opened window was placed on the search display or required manual placement. Profile-specific, web-app, packaged and other-app entries are outside this capability. Query-field right-click should retain its text-edit menu; result keyboard options should not show a second Edit menu.

Report the exact version and observed result. Stop if an action's outcome is uncertain.

## Guided Update Check

Open **Settings > Check for updates**. It should remain responsive while checking and show plain-text release notes or a clear unavailable/up-to-date result. A private repository is unavailable to the anonymous updater even when a signed-in browser can view it, and draft releases are not offered as updates. During a private trial, use the reviewed Setup provided by the maintainer. Do not provide credentials or a GitHub token; the app has no token-based fallback. Closing the update window should cancel pending work without changing preferences or permission settings.

Only when a maintainer supplies a reviewed newer public release and explicitly arranges an upgrade trial: check **Download update**, cancellation and retry, then **Install update** with its separate confirmation. Use the installed production app opened normally; portable, isolated and elevated copies must not start installation. If Administrator support is On or approval is saved, turn its existing Settings switch Off, approve Windows if asked and wait for confirmed Off. Cancelled or failed removal must block installation. After the upgrade, compare saved appearance, monitor, shortcut and Startup choices. A changed build needs fresh administrator approval. Do not uninstall or replace your regular app simply to complete this guide.

Packages remain unsigned and Windows may warn. See the [testing scope](../TESTING.md) for exact-candidate results.

## If Something Goes Wrong

Use **Pause all** first. If the app cannot be used or was forcibly stopped, run this from the folder containing the executable:

```powershell
.\TaskbarSelect.exe --restore
```

This stops the running instance and attempts restoration without uninstalling. Check errors and the exit status; it is not a guaranteed repair. Recovery avoids overwriting later geometry changes made by another app, and exact saved work areas can be unavailable if the recovery journal is malformed or unreadable. Do not delete or edit recovery files, force a crash, or uninstall just to test recovery. Stop and report an unresolved problem.

## Report Safely

Use the repository's bug report form. Include the app version, Windows build, installer or portable use, display count/resolution/scaling, expected versus actual behavior, and short reproduction steps. For movement, note the sizing mode, direct click versus thumbnail, and pause state; for startup or recovery, report what you actually observed. Unsupported configurations are useful context, not proof of compatibility.

**Copy diagnostics** in Settings is optional. It omits raw monitor identities, nicknames, app names, and exclusion paths, but display models and layout can still identify your setup. Review and redact all text and screenshots. Do not post raw settings, full `--inspect` output, window titles, private paths, serial numbers, or unique device IDs. Follow [SECURITY.md](../SECURITY.md) for security-sensitive reports.

The app has no telemetry, analytics, background update checks or automatic installation. Deliberately opening **Check for updates** contacts GitHub anonymously over HTTPS; **Download update** contacts GitHub's release-asset service too. No settings, diagnostics, monitor information or window titles are uploaded, and no account tokens are used. GitHub sees ordinary connection metadata such as your IP address. Reports are shared only when you choose to submit them; opening a GitHub link uses your browser. Settings and the recovery journal remain local under `%LOCALAPPDATA%\TaskbarSelect`.
