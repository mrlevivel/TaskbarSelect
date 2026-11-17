# Taskbar Select 1.0.0-beta.21

**Windows 11 x64 · Unsigned beta**

## What changed

- The update window explains why Administrator support must be off before installation and includes an Administrator support switch so you can turn it off there.
- If you cancel the Windows approval prompt, the verified installer stays downloaded and the update window offers a retry.

## Install or upgrade

Use **TaskbarSelect-1.0.0-beta.21-Setup.exe** for installation or **TaskbarSelect-1.0.0-beta.21-portable.zip** for a portable copy. Verify the chosen file against **SHA256SUMS.txt**. Windows may warn about unsigned downloads. Keep Windows security protections enabled.

Installed copies of beta 17 and later can use **Settings > Check for updates**. Before upgrading, turn **Administrator support** fully **Off** and approve Windows if it asks to remove saved approval. Beta 21 also offers this action in the update window. Setup preserves saved preferences and the Startup choice. Enable Administrator support again with fresh Windows approval if you want it for this build.

Portable and isolated copies update manually. Earlier private beta 16 installations need one manual upgrade to move to the public updater address.

## Known limitations

- Native Windows snapping can leave space where a hidden taskbar used to be. Optional **Experimental snapping** provides separate Windows+Left/Right placement and starts Off each launch. Snap Assist, groups, and quarter layouts can behave differently.
- Mirrored or remote displays can have generic names when Windows cannot identify a unique active monitor. Controls that rely on the monitor identity may be unavailable in that case.
- Physical validation covers a four-monitor Windows 11 desktop. Other display hardware, mixed scaling, prolonged lock and wake, and different-account Windows approval have limited coverage.

See the bundled **TESTING.md** for the verification scope. The [official releases](https://github.com/mrlevivel/TaskbarSelect/releases) list the exact package hashes and release evidence.
