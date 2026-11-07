# Taskbar Select 1.0.0-beta.18

Public-beta candidate for Windows 11 x64, prepared privately for review. Downloads remain unsigned.

## Changes

- Fix app search opening Telegram and similar desktop apps on the previously used monitor instead of the display where search was opened.
- Bring an already running matching app to the search display, or place its newly launched window there.
- Retain exact executable and window-lifetime checks when a direct installed shortcut has an app identity but its window does not. Conflicting identities, failed identity queries, profile arguments, broker launches and ambiguous windows do not gain broader placement authority.
- Carry forward beta 17's taskbar moves, Ghost Bar, connected-display shortcuts, relative sizing, Undo, search actions, focus frames, pinning and optional administrator support. Saved preferences and the updater address are unchanged.

## Upgrading

Download beta 18 Setup manually while the repository remains private. Beta 17 already uses this repository's updater address. Earlier private beta 16 installations need one manual upgrade to migrate from the original repository.

Before running Setup, turn Administrator support fully Off and approve Windows' removal prompt if requested. Setup preserves preferences and the Startup choice. Enable Administrator support again with a fresh Windows approval if wanted for this build.

After public release, Settings > Check for updates uses this repository's public GitHub releases. Portable and isolated copies update manually.

## Limits and validation

The search fix passed a regression that fails with the previous coordinator, focused identity/placement checks, a neutral Windows launch/reuse test and private Telegram checks in an isolated build. The distributed beta 18 package must pass its own build, applicable suite, package guards, genuine normal/administrator boundaries, exact beta 17 installer upgrade and controlled updater qualification. See the release page for executed results and hashes; isolated-build counts do not qualify changed package bytes.

The control images were captured from beta 17 using generic display data. Its controls are unchanged in beta 18; the older version label remains visible in the Settings image. The overview uses an earlier isolated demo build.

Native Windows snapping can retain the former hidden-taskbar strip. Optional experimental snapping starts Off after each app launch and provides separate half-window placement and monitor cycling.

Broader hardware, physical mixed scaling, prolonged wake/lock, different-account Windows approval and protected-app compatibility remain limited. Keep Windows security protections enabled if an unsigned download is blocked.
