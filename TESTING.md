# Testing Taskbar Select

## Beta 19 release qualification

The Build workflow compiles the application and native broker with warnings as errors, executes the applicable integrated suite, verifies production-profile and lifecycle contracts, and compares the exact eight portable entries to their build inputs.

The installer gate uses a disposable Windows account at genuine Medium integrity. It installs the hash-pinned public beta 18 baseline, checks a running upgrade with synthetic saved preferences and Startup, verifies the exact target executable and registration, exercises recovery and refusal cases, and cleans up its owned installation. The separate administrator boundary exercises the actual protected launch and fixed authorized operations. Interactive UAC, remembered-approval install and uninstall, and reboot are separate physical checks.

The baseline uses the exact Setup and checksum files from the [published beta 18 release](https://github.com/mrlevivel/TaskbarSelect/releases/tag/v1.0.0-beta.18), verified against pinned SHA-256 hashes. This installer fixture is separate from the app's update feed.

Monitor identity tests cover mismatched legacy records, disabled and reconnected monitors, duplicate and incomplete paths, saved nicknames and taskbar preferences, and connected-display shortcuts. The lookup fix was also checked on a four-monitor Windows 11 desktop. During a display disconnect, each remaining screen retained its original name and settings key. All four matched after reconnecting.

## Updater coverage

Focused tests cover numeric version ordering, exact official feed and asset URLs, repository-specific GitHub CDN redirects, rejection of the legacy repository and asset namespace, hostile URLs, bounded responses, digest/checksum disagreement, staging identity, tampering, cancellation and install eligibility.

A complete controlled updater trial must exercise the Settings button, checking, download progress, No then Yes confirmation, ordinary Setup and normal reopens with preserved synthetic preferences. Record any source-built test predecessor and controlled transport separately from signed-out checks of the published GitHub feed and downloads.

## Running tests

On Windows 11 x64:

```powershell
.\Build.ps1
.\Package.ps1 -SkipInstaller
```

The package command runs the ordinary suite and packaging guards. Individual pure suites include `--self-test-search-placement`, `--self-test-updates`, `--self-test-monitor-shortcuts`, `--self-test-owned-snap` and `--self-test-relative-move`. Check each command's source before running a native or lifecycle integration fixture.

Ordinary self-tests do not change Explorer taskbars. Dedicated startup, lifecycle, recovery, desktop-input and administrator tests can change state and belong in an owned disposable environment or an explicitly arranged local trial.

## Voluntary physical checks

Use the [beta tester guide](docs/BETA_TEST_GUIDE.md) for taskbar visibility, ordinary and grouped taskbar moves, Ghost Bar, search, shortcuts, minimize and Undo, pin cues, appearance and optional administrator support.

Most physical experience is on one four-monitor Windows 11 PC. Different Windows builds, physical scaling combinations, stacked/disconnected displays, prolonged lock/wake, other protected apps and different-account UAC still need broader testing. Synthetic geometry passes describe their fixtures rather than every hardware arrangement.

See the [official releases](https://github.com/mrlevivel/TaskbarSelect/releases) for the distributed packages' exact hashes, executed checks, skips and limits.
