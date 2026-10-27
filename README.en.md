# Meeting Desktop

A Windows desktop switcher that uses a separate folder for meetings, recording or presentations, and attempts to restore the original icon layout afterwards.

**Development preview:** automated tests cover simulated switching and recovery; repeated real switches, icon position restoration, and selected recovery scenarios have also been checked in a single-monitor Windows 11 virtual machine. Live wallpapers, multiple monitors, mixed DPI, interruption during a switch, and default UAC prompts still need broader testing. Recovery can be incomplete. Back up important files and layouts and use a recoverable test environment first.

[中文](README.md) · [Development](docs/DEVELOPMENT.md) · [Third-party notices](THIRD-PARTY-NOTICES.md)

The app supports a switch page, configurable global hotkey and desktop middle-button gesture, tray access, icon-layout snapshots, Chinese and English, light and dark themes, and optional glass and transition effects. An administrator authorization may be required to move and restore public desktop shortcuts. The responsive mode attempts to keep Explorer running; compatibility mode can restart Explorer and close its windows.

Download the self-contained Windows x64 preview from [GitHub Releases](https://github.com/Dream-yd/MeetingDesktop/releases). Extract the entire folder and run `MeetingDesktop.exe`.

To build on Windows with the .NET 10 SDK:

```powershell
dotnet restore MeetingDesktop.sln
dotnet build MeetingDesktop.sln -c Release --no-restore
dotnet test MeetingDesktop.sln -c Release --no-build
```

The default data directory is `%LOCALAPPDATA%\MeetingDesktop`. Preserve the recovery record and snapshots if a switch or restore fails. The project's license is [MIT](LICENSE); see [third-party notices](THIRD-PARTY-NOTICES.md).
