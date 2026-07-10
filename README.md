# Magic Input

Opinionated Windows companion app for Apple Magic Trackpad and Magic Keyboard support.

Magic Input is a clean-room Windows companion app for Apple Magic Trackpad and
Magic Keyboard hardware. It does not patch, crack, or depend on Magic Utilities.

This is a personal, opinionated project. The defaults match the way I want my
Apple keyboard and trackpad to behave on Windows, especially around modifier
keys, function-row actions, screenshots, and three-finger drag. Treat it as a
starting point, not a universal Magic Utilities replacement.

The trackpad driver dependency is the Microsoft-signed open-source driver from
`vitoplantamura/MagicTrackpad2ForWindows`. Magic Input provides the local app
surface around that driver: setup scripts, status checks, battery/status views,
trackpad settings, keyboard media-row mapping, modifier mapping, screenshot
shortcuts, three-finger drag, and one-way clipboard handoff from macOS.

## Project Status

This repository is best understood as a working personal utility, not a polished
commercial driver suite. It can install a third-party driver package, write
Windows registry settings, register a login startup entry, and install a
low-level keyboard hook while the app is running. Read the scripts before
running them on a machine you care about.

Expected target hardware:

- Apple Magic Trackpad 2 over Bluetooth.
- Apple Magic Keyboard over Bluetooth.
- Windows 11 on AMD64 or ARM64.

## Driver Dependency

- Upstream: `https://github.com/vitoplantamura/MagicTrackpad2ForWindows`
- Release: `v2.0`
- Asset: `MT2FW11-20260223-MSSigned.zip`
- SHA256: `2870C0C7982CE6AAFC3FF763FEC2999423DC4BDBD1A2C0E31CA216F26A75714F`
- Download URL: `https://github.com/vitoplantamura/MagicTrackpad2ForWindows/releases/download/v2.0/MT2FW11-20260223-MSSigned.zip`

The installer downloads the pinned zip when it is missing, verifies the SHA256,
extracts it, verifies Microsoft signatures on the selected architecture package,
checks the Magic Trackpad 2 Bluetooth hardware ID in the INF, and installs it
with `pnputil`.

Downloaded/extracted driver artifacts live under `downloads\` and `packages\`.
Those folders are intentionally ignored by git.

## Prerequisites

1. Windows 11.
2. PowerShell.
3. .NET 10 SDK, used to publish the WinForms app from source.
4. An administrator PowerShell session for driver installation.

If the driver is already installed and you only want to install or update the
app, use `-SkipDriver`; that does not require elevation.

## Install Everything

Open PowerShell as administrator in this repository folder, then run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\scripts\install-magic-input.ps1
```

That script does all of this:

1. Stops any running `MagicInput.exe`.
2. Publishes the app to `%LOCALAPPDATA%\Programs\MagicInput\app`.
3. Downloads and verifies the pinned Magic Trackpad driver package if needed.
4. Installs the driver with `pnputil`.
5. Creates a `Magic Input` shortcut in the current user's Start Menu.
6. Registers Magic Input to launch at login for the current Windows user.
7. Launches Magic Input in tray mode.

Useful install options:

```powershell
# Install or update only the app; skip the driver.
.\scripts\install-magic-input.ps1 -SkipDriver

# Install without enabling launch at login.
.\scripts\install-magic-input.ps1 -NoStartup

# Install without launching the app afterward.
.\scripts\install-magic-input.ps1 -NoLaunch

# Install the ARM64 driver package instead of AMD64.
.\scripts\install-magic-input.ps1 -Architecture ARM64
```

## Manual Driver Install

If you want to install only the trackpad driver:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\scripts\install-trackpad-driver.ps1
```

The manual driver installer writes logs under `logs\` and device snapshots under
`state\`. Both folders are ignored by git.

## Verify The Driver

```powershell
.\scripts\verify-trackpad-driver.ps1
```

After a successful install, Windows should show an Apple multi-touch/precision
trackpad device instead of only a generic HID mouse for the Magic Trackpad.

## Build And Run During Development

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\src\MagicInput\MagicInput.csproj -c Release
.\src\MagicInput\bin\Release\net10.0-windows\MagicInput.exe
```

Launch a specific page for QA:

```powershell
.\src\MagicInput\bin\Release\net10.0-windows\MagicInput.exe --page Trackpad
.\src\MagicInput\bin\Release\net10.0-windows\MagicInput.exe --page Keyboard
```

## Automatic Startup

Magic Input supports startup through the current user's Run key:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
Value name: MagicInput
Value data: "<path-to>\MagicInput.exe" --tray
```

The main installer enables this by default. Inside the app, the same behavior is
controlled by the `Launch at login` checkbox. If that checkbox is off and the
Run key is absent, Magic Input will not start automatically after a restart.

## Current App Features

- Tray app with Overview, Trackpad, Keyboard, and Maintenance pages.
- Trackpad driver status and Precision Touchpad detection.
- Trackpad haptic, click, and palm-rejection settings using the driver's
  documented registry parameters.
- Elevated apply flow for driver settings, so the app does not need to run as
  administrator all the time.
- Bluetooth battery refresh through the driver's control-device IOCTL.
- Magic Keyboard detection and battery refresh.
- Apple function row mapping:
  - `F1` -> dictation (`Win+H`)
  - `F3` -> Task View (`Win+Tab`)
  - `F4` -> Start
  - `F5` -> previous focused window (`Alt+Tab`)
  - `F6` -> Show Desktop (`Win+D`)
  - `F7` through `F12` -> media previous, play/pause, next, mute, volume down,
    and volume up
- Modifier mapping:
  - physical Command -> Windows Control
  - physical Control -> Windows key
  - `Command+Space` remains input-language switching
  - `Command+Delete` sends forward Delete while the plain Apple Delete key
    remains Backspace
  - `Command+I` opens Properties for the selected file (`Alt+Enter`)
- macOS-style screenshot shortcuts:
  - `Shift+Command+3` -> full-screen screenshot
  - `Shift+Command+4` -> area snip
  - `Shift+Command+5` -> Snipping Tool
- Three-finger drag through Precision Touchpad raw input.
- Configurable bottom-left trackpad tap, defaulting to Clipboard History
  (`Win+V`). This is additive user-mode handling and does not suppress
  Windows' normal tap-to-click event.
- Text-only Mac-to-PC clipboard handoff. Magic Input watches
  `%APPDATA%\MagicInput\clipboard-inbox` in the logged-in desktop session and
  imports atomically written `.clip` files into the real Windows clipboard.

## Mac-to-PC Clipboard Handoff

Magic Input does not expose a new network listener. The Mac remains a pure SSH
client: it sends the current macOS clipboard to the PC over SSH, and the helper
script writes a `.clip` inbox file for the already-running Magic Input tray app.

After installing or updating Magic Input, run this from the Mac:

```bash
pbpaste | ssh <windows-user>@<pc-lan-ip> 'powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\<windows-user>\AppData\Local\Programs\MagicInput\receive-mac-clipboard.ps1"'
```

Then paste normally on Windows with `Ctrl+V`, or open `Win+V` to see the item in
clipboard history. The handoff is text-only and capped at 4 MB to match
Windows clipboard-history behavior.

The full no-password BetterTouchTool setup is documented in
`docs/MAC_TO_PC_CLIPBOARD_README.md`.

## Privacy And Local Data

Magic Input stores local settings under `%APPDATA%\MagicInput`. Diagnostic logs
can also be written there while keyboard and trackpad features are being tested.
The repository ignores generated logs, screenshots, downloaded driver packages,
and local state snapshots.

Do not publish your own `logs\`, `state\`, `downloads\`, or `packages\` folders
unless you have reviewed them. Device inventory snapshots can contain hardware
instance IDs from your PC.

## Rollback

Dry-run trackpad driver removal:

```powershell
.\scripts\uninstall-trackpad-driver.ps1
```

Actually remove matching driver packages:

```powershell
.\scripts\uninstall-trackpad-driver.ps1 -Execute
```

To disable app startup without uninstalling:

```powershell
Remove-ItemProperty `
  -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
  -Name 'MagicInput' `
  -ErrorAction SilentlyContinue
```

To remove the published app files:

```powershell
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Programs\MagicInput"
```

## Repository Hygiene

This repository keeps source, scripts, docs, and notices in git. It ignores:

- Build output: `bin\`, `obj\`
- Downloaded driver zips and extracted packages: `downloads\`, `packages\`
- Local diagnostics: `logs\`, `state\`
- User-local IDE files such as `*.user`

Third-party license details are in `THIRD_PARTY_NOTICES.md`.

## License

Magic Input is released under the MIT License. See `LICENSE`.
