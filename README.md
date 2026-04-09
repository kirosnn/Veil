# Veil

Veil is a Windows desktop overlay built with WinUI 3 and .NET 10.

It provides a compact top bar with quick system controls, launcher-style search, media controls, weather, notifications, performance insights, and game-aware behavior focused on staying lightweight while gaming.

## Highlights

- Multi-monitor top bar
- Finder-style app and system action launcher
- Media controls with album art and source-aware behavior
- Weather panel and popup
- Discord notification surface
- System stats and animated runner panel
- Game detection with adaptive optimization modes

## Stack

- .NET 10
- WinUI 3
- Windows App SDK 1.8
- Native Windows interop for shell, tray, hotkeys, window management, and DWM integration

## Repository Layout

```text
apps/
  desktop/
    Veil/              Main WinUI desktop application
.github/
  workflows/           CI workflows
scripts/               Local development and packaging scripts
artifacts/             Generated outputs
temp/                  Local verification and scratch files
Veil.sln               Solution entry point
```

## Application Layout

Inside `apps/desktop/Veil`:

- `Assets/`: fonts, icons, logos, runner sprites
- `Configuration/`: persisted settings models
- `Diagnostics/`: logging
- `Interop/`: Win32 and native interop helpers
- `Services/`: application services and integrations
- `Windows/`: WinUI windows and visual helpers

## Requirements

- Windows 11
- .NET 10 SDK
- x64 environment

## Development

Restore and build:

```powershell
dotnet restore Veil.sln
dotnet build Veil.sln -c Debug -p:Platform=x64
```

The Finder AI webview frontend is compiled from embedded TypeScript during `dotnet build`.

Run the desktop app:

```powershell
dotnet run --project apps/desktop/Veil/Veil.csproj -c Debug -p:Platform=x64
```

Live development helper:

```powershell
.\dev.cmd
```

`dev.cmd` also watches Finder AI web sources under `apps/desktop/Veil/Assets/Web/FinderAi` and rebuilds the app when `.ts`, `.html`, `.css`, or the local `tsconfig.json` changes.

## Packaging

The repository includes a local setup builder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-setup.ps1
```

This produces installer artifacts under `artifacts/setup`.

## CI

GitHub Actions runs on Windows and:

- restores the solution
- publishes the desktop app in Release

Workflow file:

- `.github/workflows/build.yml`

## Current Technical Notes

- The project builds successfully in `Release|x64`.
- There are still a few nullable warnings in Discord-related code paths.
- The repository currently contains a single desktop app; the new layout leaves room for future `apps/`, shared libraries, or packaging tools without flattening the root.
