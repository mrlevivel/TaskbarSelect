# Security

## Current candidate

Taskbar Select beta 19 is an unsigned Windows 11 x64 beta. The [official releases](https://github.com/mrlevivel/TaskbarSelect/releases) list the exact packages and validation scope.

Taskbar Select normally runs with standard permissions. Optional Administrator support uses a separately approved helper and remembers approval for the exact user, profile and build. Search launches retain normal permissions when the app is launched normally. An already elevated caller's token is inherited, so open the app normally for ordinary use and updates.

## Updates and privacy

Check for updates contacts the fixed official GitHub API only when requested. Downloads are a separate action. The app has no background polling, telemetry, account-token prompt or automatic installation. Requests carry normal connection metadata such as IP address and a generic updater user-agent, without uploading settings, diagnostics, monitor information or window titles.

Release text is treated as plain text. Version, asset names, response sizes, installer metadata, digests, checksums, staging identity and repository-specific redirects are verified. These integrity checks do not identify an unsigned publisher or protect against compromise of the official release account. Keep Windows security protections enabled.

The updater admits installation only from the verified installed copy running normally. Portable, isolated and elevated copies update manually. Administrator support and its saved approval must be fully Off first. Incomplete removal or recovery blocks replacement. Setup preserves preferences and Startup on upgrade.

## Administrator boundary

The native approval launcher stages and pins fixed executable payloads in protected app-owned locations. Controller and helper identity, user/session, token, path, file identity, hash and loader inputs are checked before authorization. The protocol accepts fixed window operations and never accepts arbitrary executables, DLLs, commands or remote-supplied launch arguments.

Remembered support uses a protected scheduled broker with no sign-in trigger. The standard interface requests that approved broker on demand. The broker, ledger, task definition, caller and protected namespace are rechecked before a session. A changed build requires explicit fresh approval. Turning support Off stops it and removes saved approval; Windows may ask for permission.

Upgrade and uninstall commands do not request elevation. They check strict saved-approval absence before stopping the interface, restore owned state and check again before replacing files or removing user settings. Uncertain cleanup retains recovery and blocks maintenance.

Automated boundary checks and app movement trials describe their exact fixtures. They do not constitute a comprehensive independent security audit. Taskbar management also depends on undocumented Explorer behavior that Windows updates can change.

## Reporting

Use **Security > Report a vulnerability** to share vulnerability details and exploit steps privately. If the form is unavailable, open an issue asking for a private contact channel and share the details after the maintainer confirms that channel.

Include the app version, Windows build, impact and minimal reproduction in a private report. Remove credentials, unnecessary app/window titles, monitor serials and private paths from attachments. An ordinary compatibility regression can use the normal bug form.
