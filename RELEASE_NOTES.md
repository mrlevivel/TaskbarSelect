# Taskbar Select 1.0.0-beta.18

**Windows 11 x64 · Unsigned beta**

## What changed

- **App search uses the display where you searched.** Telegram and similar desktop apps can now be brought to that display when already running, or placed there after a fresh launch.
- Retains taskbar-click moves, Ghost Bar, connected-display shortcuts, relative sizing, minimize and Undo, always on top, focus frames and optional administrator support.
- The source repository is organized into application, test, native, build and installer folders. Existing build commands and application behavior are retained.

## Install or upgrade

Use **TaskbarSelect-1.0.0-beta.18-Setup.exe** for installation or **TaskbarSelect-1.0.0-beta.18-portable.zip** for a portable copy. Verify the chosen file against **SHA256SUMS.txt**. Downloads are unsigned; Windows may warn or block them. Keep Windows security protections enabled.

Before upgrading, turn **Settings > Administrator support** fully **Off**, approving Windows' removal prompt if requested. Setup preserves saved preferences and the Startup choice. Enable support again with a fresh Windows approval if wanted for this build.

Private beta 16 users need one manual upgrade to this release. Installed copies of beta 17 and later can use **Settings > Check for updates**. Portable and isolated copies update manually.

## Known limitations

- Native Windows snapping can leave the former hidden-taskbar strip. Optional **Experimental snapping** provides separate Windows+Left/Right placement and starts Off each launch. Snap Assist, groups and quarter layouts can behave differently.
- Most physical experience is on one four-monitor PC. Other hardware, mixed scaling, stacked or disconnected displays, prolonged lock/wake, different-account Windows approval and protected apps have limited coverage.

## Overview

Open **TaskbarSelect-overview.mp4** in the release assets for a 2:17 narrated walkthrough of beta 18 using neutral demo windows.

## Validation status

The hosted build, installer and administrator checks passed, as did the controlled private updater trial. **A separate local interactive run failed a native focus/input-stability check after detecting changed desktop input and a denied focus grant.** That failure remains recorded; hosted success does not establish that the local case is resolved.

Signed-out downloads passed. The shipped beta 17 updater found beta 18 on the live GitHub feed and retrieved the checksum file and Setup with matching SHA-256 hashes. The separate controlled installation trial used synthetic preferences and a source-built predecessor with version metadata.

<details>
<summary>Validation details and source provenance</summary>

The frozen downloads were built from [8ef9fbd](https://github.com/mrlevivel/TaskbarSelect/tree/8ef9fbdffdccd45195bde091cc7e0d90d0b31cc6). Later main-branch changes polish online documentation and images; the installer, portable archive and their bundled documents remain the exact qualified files from that source.

- [Build 42423141972](https://github.com/mrlevivel/TaskbarSelect/actions/runs/42423141972): 38,567 application checks, 156 genuine installer checks, 25 maintenance checks and 1,216 administrator-boundary assertions, plus package and baseline guards.
- [Private updater trial 42447023903](https://github.com/mrlevivel/TaskbarSelect/actions/runs/42447023903): 124 runtime and four outer checks; one approved ordinary Run action, exact installation, synthetic settings and Startup retention, normal reopens and complete disposable-account cleanup.
- The local interactive attempt failed after 37,990 checks and is retained as a failed attempt.
- Source organization preserved all 215 moved application/test/native/configuration/manifest files byte-for-byte and the same 210 explicit compiler inputs in the same order.
- The exact downloads and hosted output passed focused privacy review. These checks do not constitute an independent security certification or broad hardware validation.

See [TESTING.md](https://github.com/mrlevivel/TaskbarSelect/blob/8ef9fbdffdccd45195bde091cc7e0d90d0b31cc6/TESTING.md) for the testing scope. The attached **SHA256SUMS.txt** is the checksum reference for these downloads.

</details>
