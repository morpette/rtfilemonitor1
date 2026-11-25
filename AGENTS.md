# GUI terminology

Use these names when discussing changes to the main window.

- **Banner** is the header that displays “ChatGPT File Monitor” and the version number.
- **Dashboard** is the area below the Banner with the folder paths and the controls for selecting and configuring folders.
- **Main** (also called the **viewer**) includes the tab selector and the contents of the selected tab. Its tabs are `.codex folder`, `JSONL sessions`, and `SQLite activity`.

# Building RtFileMonitor

The WPF project targets .NET 10 for Windows (`net10.0-windows`). The .NET 10 SDK is already in the workspace at `.build\dotnet\dotnet.exe`. Use that executable for builds if the `dotnet` on PATH reports no installed SDKs.

When asked to build a new version, publish a self-contained Windows x64 release to a new folder named `dist/RtFileMonitor-win-x64-v<N>`. Use the next version number after the highest existing release folder or ZIP in `dist`, and set `PackageVersionLabel` to `v<N>`. Create a ZIP only when explicitly requested.

To explicitly create a release ZIP using the workspace SDK and cached NuGet packages, run from the repository root in PowerShell:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.build\cli-home'
$env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME '.nuget\packages'
.\publish.ps1 -Dotnet (Join-Path $PWD '.build\dotnet\dotnet.exe')
```
