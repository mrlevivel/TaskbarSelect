# Documentation images

These neutral beta 18 UI previews are rendered from the qualified portable executable's real Windows Forms controls, using an owned hidden fixture and generic display data. They illustrate the controls, rather than a running desktop session or runtime acceptance test. The renderer is maintained in [Render-Docs.cs](../tools/Render-Docs.cs).

| Image | Native content retained | Size |
| --- | --- | --- |
| [displays-beta18.png](displays-beta18.png) | Displays tab; four synthetic monitors; the selected upper display has its taskbar hidden and Ghost Bar enabled | 858 × 715 |
| [appearance-beta18.png](appearance-beta18.png) | Appearance tab through the complete frame-style section; Forest Green, Muted edge and lighting None | 858 × 622 |
| [accent-colors-beta18.png](accent-colors-beta18.png) | Complete native accent-choice control with 17 colors | 801 × 373 |
| [settings-beta18.png](settings-beta18.png) | Complete Ghost Bar sensitivity and About cards, including the actual beta 18 version and Check for updates | 825 × 517 |

The source executable has SHA-256 `9a39e12c5bcd3d2eaf9b2ad84006a4d393e2a057f9b58b1f84aad6972f4cdfae`. Its portable archive has SHA-256 `0437a623c1d9d7fb7b160f8677258d719a6b77d9228cf0c43c99ed8df59f3a46`. The renderer checks the executable hash and informational version, and is compiled with the matching `src/TaskbarSelect/AssemblyInfo.cs` so the native About label is accurate. The app and release files are not rebuilt or modified for these images.

The fixture uses `AppPolishTests.Fixture` with `DesktopRuntime=false` and a never-created isolated profile. It does not invoke application startup, read user preferences, call `Startup.ReadState`, capture desktop pixels, show windows, send input, register shortcuts, contact the updater or change permissions. It releases only its hook-free owned components, without `Controller.Dispose`, `Controller.Refresh` or global taskbar restoration. A successful render verifies that the form remains hidden, the isolated profile directory remains absent and the synthetic settings remain unchanged.

The renderer prints the owned tab control directly, then takes rectangular crops of those native pixels. The Displays crop ends after its complete cards. The Appearance crop ends after the complete frame-style section. The Settings crop omits the whole General card, including its honest test-only Startup label. The application header, isolated-test badge, save-status area and footer are outside these previews; their labels are never replaced or painted over. Native combo controls are printed directly with the app's own closed-field painter because parent `DrawToBitmap` can omit their selected value. No desktop pixels are copied. The palette is the native accent control printed on its own.

The disabled movement controls in the Displays fixture do not establish desktop-hook availability. No shortcut preview is included because this fixture does not register shortcuts. Still images do not demonstrate focus-frame animation. The PNGs contain structural, color and resolution data, without text or EXIF metadata.

To reproduce, extract the matching portable archive outside the repository, compile the renderer with .NET Framework 4's x64 `csc.exe` and the matching assembly metadata, then pass the qualified executable and an owned output directory:

```powershell
$docsRender = Join-Path $env:TEMP 'TaskbarSelect-docs-beta18'
New-Item -ItemType Directory -Path $docsRender -Force | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "/out:$docsRender\Render-Docs.exe" docs\tools\Render-Docs.cs src\TaskbarSelect\AssemblyInfo.cs
& "$docsRender\Render-Docs.exe" 'C:\path\to\qualified-beta18\TaskbarSelect.exe' "$docsRender\images"
```

Generated images are reviewed outside Git before the approved PNGs are copied here. SHA-256 values for this set:

```text
e04ccb1538ffd7a106213346efb084212a0153ed57b024ea556f529d68691fd1  displays-beta18.png
97df6371c6bf3be1827a9514dea65f8390a97ea90290b7bbb04f06a3bf3a2037  appearance-beta18.png
cf22b9de7cf9c910da1aac8d5882e903e921773f0b47d792a81ee476fd870436  accent-colors-beta18.png
abd97af486439fbcc7711fd7ae4d05432fdbeabe6d8539e68c34d83d28fe3882  settings-beta18.png
```
