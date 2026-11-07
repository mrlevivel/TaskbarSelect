# Contributing

Taskbar Select is preparing its first public beta. Small, focused fixes and clear compatibility reports are welcome. Discuss substantial features or changes to window-management behavior in an issue before implementing them.

## Report a Bug

Use the [bug report form](https://github.com/mrlevivel/TaskbarSelect/issues/new/choose). Include the version, Windows build, display arrangement and scaling, reproduction steps, and expected versus actual behavior. The [beta tester guide](docs/BETA_TEST_GUIDE.md) has focused checks.

Review diagnostics and screenshots before posting. Do not include credentials, private paths, raw settings, monitor serial numbers, or unreviewed `--inspect` output. Security-sensitive reports follow [SECURITY.md](SECURITY.md), not a public issue.

## Build and Check

Use 64-bit Windows with the .NET Framework C# compiler included in Windows:

```powershell
.\Build.ps1
.\Package.ps1 -SkipInstaller
```

`Package.ps1` rebuilds with warnings treated as errors, runs the ordinary `--self-test` suite, verifies the portable archive, and writes output to ignored `artifacts/` and `dist/` directories. To build the installer too, run `.\Package.ps1` with Inno Setup 6 installed; CI currently uses 6.7.3.

Ordinary self-tests do not manipulate Explorer taskbars. The separate `--self-test-startup` integration command changes current-user startup state during testing; it is not part of the routine commands above. Do not run lifecycle, recovery fault, sign-in, or diagnostic experiments on someone else's session without approval. See [TESTING.md](TESTING.md) for coverage and live checks.

Existing artwork outputs are checked in, so Node.js is not required for the normal app build. To regenerate artwork, follow [assets/ARTWORK.md](assets/ARTWORK.md).

## Changes and Documentation

Keep changes scoped and include regression tests for behavioral fixes. Do not commit local settings, diagnostics, generated packages, or private screenshots. Source files are explicitly listed in `Build.ps1`; moving files requires updating their references.

The repository's `README.md` is the short GitHub overview. `docs/USER_GUIDE.md` covers detailed controls and recovery. `docs/package/README.md` is the offline entry point installed as `README.md` and included at the portable ZIP's root. Keep shared instructions consistent; package-relative links must point to files actually bundled.

Retain license notices and relevant artwork provenance. Historical test evidence is linked from `TESTING.md`; don't present an older build's results as validation of new behavior.

Release publication, tags, repository visibility, and changes to licensing or funding need maintainer approval. Packaging and a passing self-test do not authorize a public release.

For a release, run the normal PR Build once after the source is final, then promote that run's validated `Taskbar-Select-beta-package` artifact. Record the actual CI checkout commit and require its tree to match the version tag before uploading the exact Setup, portable ZIP and checksums. Do not promote an `UNVALIDATED` diagnostic artifact or rebuild between validation and publication. Review repository visibility and obtain explicit maintainer approval before publication.

Pushing a version tag does not start another package build. The Release workflow is an optional explicit rebuild, dispatched at an existing version tag; it retains the full package, installer and administrator-boundary gates. Merging a later change into `main` still runs the normal Build checks.
