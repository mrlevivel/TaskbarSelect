# Taskbar Select 1.0.0-beta.17

Public-beta candidate for Windows 11 x64, prepared privately for review. Downloads remain unsigned.

## Changes

- Start the public source repository from the reviewed application snapshot, with original artwork, build scripts and regression tests.
- Bind update checks, release-page links and verified download redirects to the new TaskbarSelect repository and its exact GitHub asset namespace.
- Retain connected-display shortcut rows and saved bindings across disconnect/reconnect.
- Retain relative layout across smaller displays for floating and snapped windows, and Undo of maximized moves.
- Refresh user documentation and synthetic control images.

## Upgrading

Users of the earlier private beta 16 must download and run beta 17 Setup manually once. Beta 16 is tied to its original repository and cannot adopt this address through a settings change.

Before running Setup, turn Administrator support fully Off and approve Windows' removal prompt if requested. Setup preserves preferences and the Startup choice. Enable Administrator support again with a fresh Windows approval if wanted for this build.

After migration, Settings > Check for updates uses this repository's public GitHub releases. While staging remains private, anonymous checks are unavailable. Portable and isolated copies update manually.

## Limits and validation

Native Windows snapping can retain the former hidden-taskbar strip. Optional experimental snapping starts Off after each app launch and provides separate half-window placement and monitor cycling.

The public-beta candidate requires a warning-as-error build, full applicable suite, package guards, genuine normal/administrator boundaries, a disposable upgrade from the exact beta 16 installer, and updater verification against this repository's coordinates. See the release page for executed results and exact hashes. Earlier private build counts do not qualify changed bytes.

Broader hardware, physical mixed scaling, prolonged wake/lock, different-account Windows approval and protected-app compatibility remain limited. Keep Windows security protections enabled if an unsigned download is blocked.
