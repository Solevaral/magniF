# magniF

<img src="assets/icon.png" width="96" height="96" alt="magniF icon">

**English** · [Русский](README.ru.md)

A hold-to-zoom magnifier for Windows 10 (2004+) and 11. Hold a key — a rounded lens appears right under the
cursor, the cursor itself hides so it doesn't get in the way, and the lens follows the mouse.
Release the key — the lens is gone.

## Download

Grab the latest build from [Releases](https://github.com/Solevaral/magniF/releases):

- `magniF-<version>-win-x64-standalone.exe` — runs as is, no runtime needed.
- `magniF-<version>-win-x64-net9.exe` — small, requires the .NET 9 Desktop Runtime.

No installer: put the file anywhere and run it. It lives in the notification area; the settings
window opens on first launch, by clicking the tray icon, or by launching the exe again.

## How it works

- **Hold the bind** (Ctrl by default) — the lens appears after a short delay.
- **The key still works as usual.** The lens shows up only if nothing else was pressed during the
  delay, so Ctrl+C, Ctrl+click and friends never trigger it.
- **Clicks pass through** the lens to the point under the center mark.
- **Mouse wheel** while the lens is open changes the zoom; the new value is remembered.
- **Release** — the lens disappears and the cursor comes back.

The bind can be any key, a combination (Ctrl+Shift, Alt+Z…) or a mouse button (middle, side
buttons 4/5).

## Settings

| Section | What can be tuned |
|---|---|
| Activation | bind, activation delay, wheel zoom and its step |
| Lens | zoom, smooth scaling, width, height, corner radius, center mark (none / dot / cross) and its color |
| Style | border color and thickness, shadow size and opacity |
| General | language (system / English / Russian), start with Windows |

Every change applies immediately and is shown in the live preview. Settings live in
`%AppData%\magniF\settings.json`.

## Under the hood

The lens is drawn entirely on the GPU, at the refresh rate of your monitor:

- the desktop image comes from the **Desktop Duplication API**;
- the output is a click-through window covering the monitor, presented through a
  **DirectComposition** swap chain with per-pixel alpha;
- a single **Direct3D 11** pixel shader draws the magnified image (bicubic or crisp pixels), the
  rounded shape, border, shadow and center mark, all antialiased.

The window itself never moves — only the picture inside it does, so the lens position and the
magnified area always come from the same frame: no trailing, no smearing. The window is excluded
from screen capture (`WDA_EXCLUDEFROMCAPTURE`), so the lens never zooms into itself.

Keys are watched by low-level keyboard and mouse hooks on a separate thread. The cursor is hidden
with `MagShowSystemCursor` from the Magnification API and restored on release, on exit and on an
unhandled error.

## Known limitations

- **Exclusive-fullscreen games** bypass DWM — the lens can't draw over them. Borderless and
  windowed modes work.
- While a window running **as administrator** is focused, Windows does not deliver its input to
  hooks of a regular process, so the bind doesn't trigger there. Run magniF as administrator if
  you need it in such windows.
- The UAC prompt and Ctrl+Alt+Del live on the secure desktop and can't be magnified.

## All in One module

magniF can run as a module of [All in One](https://github.com/Solevaral/All-in-one), a launcher that installs, updates and starts several tools from one place. The launcher starts it with `--hosted --pipe <name>`. In that mode magniF keeps its tray icon but has no autostart switch of its own: autostart is set in All in One. All in One shows its status, opens its windows and stops it over a named pipe (the cursor and input hooks are restored before exit). Started normally, magniF works as before.

## Build

```bash
dotnet build magniF.sln -c Release
```

Framework-dependent single file:

```bash
dotnet publish src/magniF/magniF.csproj -c Release -o publish
```

Standalone single file:

```bash
dotnet publish src/magniF/magniF.csproj -c Release -r win-x64 --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish-selfcontained
```

Requires the .NET 9 SDK. Target platform is x64.

## Code layout

```
src/magniF/
  App.xaml.cs                 single instance, wiring, tray menu
  Interop/Magnification.cs    cursor hiding via magnification.dll
  Interop/NativeMethods.cs    windows, GDI, hooks, input, tray icon
  Core/InputHook.cs           low-level hooks, hold detection, bind recording
  Core/KeyBinding.cs          key codes and names
  Core/Settings.cs            settings with change notification, JSON
  Core/Strings.cs             English / Russian interface strings
  Core/AutoStart.cs
  UI/Lens.cs                  the lens: frame loop, desktop duplication, Direct3D output
  UI/LensShader.hlsl          magnification, shape, border, shadow and mark in one shader
  UI/SettingsWindow.xaml      settings and live preview
  UI/Controls/Controls.cs     slider row, color picker, converter
  UI/Theme/Dark.xaml          dark theme
  UI/TrayIcon.cs              icon via Shell_NotifyIcon
```

## License

[MIT](LICENSE)
