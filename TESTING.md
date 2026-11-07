# Testing Taskbar Select

## Beta 17 release qualification

The Build workflow compiles the application and native broker with warnings as errors, executes the applicable integrated suite, verifies production-profile and lifecycle contracts, and compares the exact eight portable entries to their build inputs.

The installer gate uses a disposable Windows account at genuine Medium integrity. It installs the hash-pinned beta 16 baseline, checks a running upgrade with synthetic saved preferences and Startup, verifies the exact target executable and registration, exercises recovery/refusal cases and cleans up its owned installation. The separate administrator boundary exercises the actual protected launch and fixed authorized operations. Interactive UAC, remembered-approval install/uninstall and reboot are separate physical checks.

The first public candidate uses a pinned Actions artifact containing the exact previous beta 16 installer and checksum asset. The build verifies the seed run, source commit, artifact identity, archive digest and both file hashes with a read-only token. The fixture is retained for 90 days; rebuilding beta 17 after expiry requires reseeding and reviewing its manifest. This CI fixture is separate from the app's update feed. No cross-repository credential is needed.

## Updater coverage

Focused tests cover numeric version ordering, exact official feed and asset URLs, repository-specific GitHub CDN redirects, rejection of the legacy repository and asset namespace, hostile URLs, bounded responses, digest/checksum disagreement, staging identity, tampering, cancellation and install eligibility.

A complete controlled updater trial must exercise the Settings button, checking, download progress, No then Yes confirmation, ordinary Setup and normal reopens with preserved synthetic preferences. A source-built test predecessor or controlled transport must be identified in its result. Signed-out public feed, CDN and installation acceptance require the approved public launch and cannot be established while the repository is private.

## Running tests

On Windows 11 x64:

```powershell
.\Build.ps1
.\Package.ps1 -SkipInstaller
```

The package command runs the ordinary suite and packaging guards. Individual pure suites include `--self-test-updates`, `--self-test-monitor-shortcuts`, `--self-test-owned-snap` and `--self-test-relative-move`. Check each command's source before running a native or lifecycle integration fixture.

Ordinary self-tests do not change Explorer taskbars. Dedicated startup, lifecycle, recovery, desktop-input and administrator tests can change state and belong in an owned disposable environment or an explicitly arranged local trial.

## Voluntary physical checks

Use the [beta tester guide](docs/BETA_TEST_GUIDE.md) for taskbar visibility, ordinary and grouped taskbar moves, Ghost Bar, search, shortcuts, minimize and Undo, pin cues, appearance and optional administrator support.

Most physical experience is on one four-monitor Windows 11 PC. Different Windows builds, physical scaling combinations, stacked/disconnected displays, prolonged lock/wake, other protected apps and different-account UAC still need broader testing. Synthetic geometry passes describe their fixtures rather than every hardware arrangement.

The [release page](https://github.com/mrlevivel/TaskbarSelect/releases/tag/v1.0.0-beta.17) is the authority for the distributed candidate's exact hashes, executed checks, skips and limits.
