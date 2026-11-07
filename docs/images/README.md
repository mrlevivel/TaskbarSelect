# Documentation images

The retained beta 17 PNGs use the real candidate's Windows Forms controls with generic display data. These controls are unchanged in beta 18; the Settings image still shows the older beta 17 version label. The renderer is maintained in [Render-Docs.cs](../tools/Render-Docs.cs).

| Image | Contents |
| --- | --- |
| displays-beta17.png | Four generic displays, Forest Green and the actual Displays controls |
| shortcuts-beta17.png | One movement row per synthetic display, default bindings and shared Undo |
| appearance-beta17.png | Muted edge, lighting None and Forest Green |
| accent-colors-beta17.png | Seventeen compact accent choices |
| settings-beta17.png | Beta 17 version, Ghost Bar sensitivity and Check for updates |

The source executable for these images has SHA-256 `50a25760a69a39706b780c89eb66c31bad067854617fe061f384445e628d4dec`. It is a local build of the beta 17 candidate. Images illustrate the controls and do not establish runtime acceptance of a later package.

The form remains hidden with desktop runtime disabled. The renderer does not invoke application startup, read user preferences, capture desktop pixels, show windows, send input, register shortcuts, contact the updater or change permissions. Its unused isolated profile directory is checked to remain absent. The actual isolated-test label, paused/missing-taskbar state and unregistered-shortcut status are preserved.

The renderer prints owned native controls directly. It omits the native titlebar because DrawToBitmap does not reproduce DWM chrome, and crops the Shortcuts image after the complete Window movement card. No personal desktop or account data is used. The images contain structural/color/resolution PNG metadata and no text/EXIF metadata. Still images do not demonstrate animation.
