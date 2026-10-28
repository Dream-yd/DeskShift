# DeskShift · 幕间

Switch desktop spaces. Keep your icon layouts. Use separate desktop folders for work, study, recording or presentations, and restore saved icon positions when you return.

*一键切换桌面，保留每个空间的布局。适用于工作、学习、录屏和演示。*

[简体中文](README.md) · **English**

[Download V1.0 · Windows x64](https://github.com/Dream-yd/DeskShift/releases/tag/v1.0.0) · [Release notes](https://github.com/Dream-yd/DeskShift/releases/tag/v1.0.0) · [Development](docs/DEVELOPMENT.md) · [Third-party notices](THIRD-PARTY-NOTICES.md)

**Windows desktop app · Chinese & English · Light & dark themes · Self-contained .NET 10 release**

![DeskShift desktop-switching page in English and light mode, showing example Work and Focus folders](docs/screenshots/switch-light-en.png)

*V1.0 interface with demo folders. Personal desktop previews are not loaded in these screenshots.*

## Features

- Switch between your original desktop and an alternate folder; restore icon positions using saved identities and coordinates.
- Use the switch page, a configurable global hotkey, a desktop middle-button double-click, or the system tray.
- Save, browse and search icon layouts. Retain automatic snapshots as needed; snapshots required by an active recovery session are protected.
- Switch between Chinese and English, light and dark themes, or follow the system theme. Optional liquid-glass skin, transition effects and switch notifications.
- Use Recovery & repair and Diagnostics when a normal switch cannot complete. Uncertain states retain recovery records for review.

## Screenshots

These views use the actual V1.0 UI controls with isolated demonstration data. Folder paths and layout names are examples. Screenshots are for interface illustration, not evidence of performance or compatibility.

<details open>
<summary><strong>Layout history · dark mode / 深色布局管理</strong></summary>

Browse saved layouts, search by name, date or resolution, and inspect icon positions and monitor arrangement before restoring.

![Dark-mode layout manager with three demonstration layouts and an icon-position diagram](docs/screenshots/layouts-dark-zh.png)

</details>

<details>
<summary><strong>Desktop switching · Chinese, dark / 深色中文切换页</strong></summary>

Switch out and return on the same page. Recovery & repair is available separately for exceptional states.

![Chinese desktop-switching page in dark mode](docs/screenshots/switch-dark-zh.png)

</details>

<details>
<summary><strong>Shortcuts and settings / 快捷操作与设置</strong></summary>

Configure the desktop middle-button gesture, global hotkey, tray behavior, animations and notifications. Startup and advance authorization are optional.

![Settings page showing the middle-button gesture, Ctrl+Alt+M, transitions and notifications](docs/screenshots/settings-dark-zh.png)

</details>

## Getting started

1. Download the **Windows x64 ZIP** from [Releases](https://github.com/Dream-yd/DeskShift/releases/tag/v1.0.0).
2. Extract the entire folder and run `MeetingDesktop.exe`. The self-contained package requires no separate .NET installation. The package is unsigned.
3. Back up important desktop files, save a layout and note your original desktop folder.
4. Choose a separate alternate folder in Settings. Do not choose the original desktop or one of its subfolders.
5. Authorize in advance if needed, then use **Switch desktops** to switch out or return. Check the icon layout after returning.

Personal desktop files stay in their respective folders. The app changes the Windows desktop-folder location; public-desktop `.lnk` shortcuts may require administrator authorization to park and restore them. Responsive mode attempts to keep Explorer running. Compatibility mode may restart Explorer and close File Explorer windows.

If a switch fails, open **Recovery & repair** or **Diagnostics**. Keep recovery records and active snapshots; do not delete them to dismiss a warning.

## Upgrading and data

Exit the old version from its tray menu before starting the new one. The executable remains `MeetingDesktop.exe` and the default data directory remains `%LOCALAPPDATA%\MeetingDesktop`, preserving compatibility with existing settings, recovery records, helpers and startup entries. If startup is enabled, migrate the old entry through Settings.

Settings provides a recovery-backup export. Automatic snapshot retention can be set to 7, 15 or 30 days, or kept without automatic expiry; snapshots needed by an active recovery session are protected.

## Build from source

To build on Windows with the .NET 10 SDK:

```powershell
dotnet restore MeetingDesktop.sln
dotnet build MeetingDesktop.sln -c Release --no-restore
dotnet test MeetingDesktop.sln -c Release --no-build
```

Tests include Windows-native tests on separate test windows. Automated tests do not perform a complete switch of the runner's real desktop. Release packages use a multi-file deployment: keep the EXE and accompanying files together.

## Release and validation scope

**V1.0**, formerly Meeting Desktop: 879 local automated tests passed and one symlink-permission test was skipped; Windows CI passed. A single-monitor Windows 11 VM verified repeated real round trips, restored icon coordinates, icon fades with static wallpaper, and returning after restarting the app from a stable alternate-desktop state.

Live wallpapers, multiple monitors, mixed DPI, interruption during a switch and default UAC prompts still need broader real-device testing. Recovery can be incomplete when icons, permissions or display configuration change. Back up important files and layouts before first use.

## License

The project is licensed under [MIT](LICENSE). The liquid-glass implementation draws on optical ideas from a third-party project; attribution and licenses are listed in [Third-party notices](THIRD-PARTY-NOTICES.md).
