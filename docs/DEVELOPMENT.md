# Development and testing

The solution contains the WPF app, core library and xUnit tests. Use a Windows x64 development environment with the .NET SDK corresponding to the target in `Directory.Build.props`.

```powershell
dotnet restore MeetingDesktop.sln
dotnet build MeetingDesktop.sln -c Release --no-restore
dotnet test MeetingDesktop.sln -c Release --no-build
dotnet publish src/MeetingDesktop.App/MeetingDesktop.App.csproj -c Release -r win-x64 --self-contained true -o ./publish
```

The test suite uses temporary files, fake desktop operations, and several owned Windows windows. It is not a complete end-to-end desktop switch test. Any test involving real shell changes, public-desktop files, Explorer restart or wallpapers must be run separately in a disposable Windows environment with a verified recovery plan. The repository does not include the local machine's diagnostic scripts or screenshots.

The GitHub Actions workflow is a prepared starting point; its first hosted run is still required to confirm Windows runner behavior. Release packages must include the project license and required third-party/runtime notices. For the self-contained Windows x64 package, preserve the project's `LICENSE.txt` and `THIRD-PARTY-NOTICES.md`, the liquid-glass license, and the license and notices from the exact .NET and Windows Desktop runtime packages used by the build. Inspect their versions in `MeetingDesktop.runtimeconfig.json`.

This repository targets .NET 10 for its support lifecycle. Changes to the runtime require their own Windows compatibility validation; passing simulated tests does not establish real Explorer behavior.
