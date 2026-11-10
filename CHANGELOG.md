# Changelog

## 1.0.0-beta.19

- Fix duplicate monitor names after disabling a display in Windows Settings by matching each screen to its active monitor interface.
- Keep saved nicknames, taskbar visibility, and Ghost Bar choices associated with verified monitor identities. When a match is ambiguous, show a generic monitor label and defer those preferences until the match can be verified.

## 1.0.0-beta.18

- Fix app search leaving Telegram and similar desktop apps on their previous monitor instead of the display where search was opened.
- Recognize verified direct shortcuts when a window has no explicit app identity, for both existing windows and newly launched apps.
- Preserve exact executable, window lifetime, exclusions and cancellation checks; profile arguments, conflicting identities and ambiguous windows remain restricted.
- Carry forward beta 17's features, updater address and saved-settings format.
- Clarify public downloads and anonymous update checks in the user, tester and bundled guides, including how private trials and draft releases differ.
- Group application source, tests, native helper, build tools and installer definitions into folders while retaining the root build commands.

## 1.0.0-beta.17

- Prepare the reviewed Taskbar Select source for its public beta repository.
- Move the updater and repository links to the new official home while retaining exact repository and download checks.
- Retain per-connected-display shortcut rows, saved bindings through disconnect/reconnect, relative sizing through smaller displays and maximized-move Undo.
- Retain taskbar-click moves, Ghost Bar, app search, Chrome's new-window action, directional focus, layout picker, minimize, shared Undo, always on top and its corner cue.
- Retain optional remembered administrator support, explicit maintenance cleanup, per-user installation, local settings and user-requested updates.
- Update documentation, licensing/provenance links and synthetic control images.

The public repository begins with a reviewed source snapshot. Its release page records exact candidate verification. This changelog describes available behavior and does not claim that every supported Windows or display configuration has been tested.
