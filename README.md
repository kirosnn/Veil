# Veil

Veil is a Windows desktop overlay built with WinUI 3 on .NET 10.

The project focuses on a lightweight top bar and companion panels designed for everyday desktop use without getting in the way during fullscreen gaming. The app combines launcher-style search, system actions, media controls, local dictation, AI integration, Discord notifications, weather visuals, performance monitoring, and game-aware optimizations.

## What Veil Does

- Draws a compact top bar on the primary monitor, all monitors, or a custom monitor selection
- Opens a Finder-style launcher with apps, Windows settings links, and AI entry points
- Shows media controls with artwork, source switching, and optional volume controls
- Displays system stats and animated runners
- Surfaces Discord notifications in a dedicated panel
- Supports local speech dictation with downloadable speech models
- Detects game processes and can reduce background activity while gaming
- Exposes a settings window for visuals, monitor targeting, shortcuts, AI providers, speech models, and performance behavior

## Tech Stack

- .NET 10
- WinUI 3
- Windows App SDK 1.8
- Win32 interop for hotkeys, monitor handling, tray behavior, shell actions, and window management
- TypeScript for the embedded Finder AI web UI
- Rust for the local speech transcription CLI

## Repository Layout

```text
apps/
  desktop/
    Veil/                    Main WinUI desktop application
    VeilSpeechCli/           Local speech transcription helper written in Rust
tests/
  Veil.Tests/                MSTest project for service-level coverage
tools/
  VeilPerfGame/              Synthetic fullscreen workload used by perf benchmarks
scripts/
  dev.ps1                    Local hot-reload style development runner
  benchmark-perf.ps1         Performance benchmark and regression assertions
  build-setup.ps1            Local installer builder
artifacts/                   Generated publish, benchmark, and setup outputs
UnicodeAnimations/           Shared spinner/animation assets and support code
.github/workflows/           CI workflow definitions
Veil.sln                     Solution entry point
dev.cmd                      Convenience launcher for scripts/dev.ps1
```

## Application Structure

Inside `apps/desktop/Veil`:

- `Assets/`: fonts, icons, web assets, logos, and runner frames
- `Configuration/`: persisted settings and normalization logic
- `Diagnostics/`: application and performance logging
- `Interop/`: native Windows bindings and monitor/window helpers
- `Services/`: app discovery, AI providers, media, dictation, game detection, startup, tray, and shell integrations
- `Windows/`: top bar, finder, dictation overlay, panels, settings, and Alt+Tab related windows

## Runtime Requirements

- Windows 11
- x64 environment
- .NET 10 SDK
- Rust toolchain with `cargo` available on `PATH`
- TypeScript compiler `tsc` available on `PATH`

Why `cargo` and `tsc` are required:

- `dotnet build` for the desktop app runs `tsc` to rebuild `Assets/Web/FinderAi`
- `dotnet build` also runs `cargo build --release` for `apps/desktop/VeilSpeechCli`

## Getting Started

Restore the solution:

```powershell
dotnet restore Veil.sln
```

Build everything in debug:

```powershell
dotnet build Veil.sln -c Debug -p:Platform=x64
```

Run the desktop app:

```powershell
dotnet run --project apps/desktop/Veil/Veil.csproj -c Debug -p:Platform=x64
```

Run tests:

```powershell
dotnet test tests/Veil.Tests/Veil.Tests.csproj -c Debug -p:Platform=x64
```

## Development Workflow

For local iteration, use:

```powershell
.\dev.cmd
```

This launches `scripts/dev.ps1`, which:

- watches `apps/desktop/Veil`
- rebuilds and restarts Veil when relevant files change
- ignores generated `bin`, `obj`, and Finder AI `dist` outputs
- exposes dev shortcuts for restart, process listing, cleanup, and help

Watched file types include:

- `.cs`
- `.xaml`
- `.csproj`
- `.manifest`
- `.ts`
- `.html`
- `.css`
- `.json`

## AI and Dictation

Veil includes an AI section in settings and supports multiple provider modes:

- OpenAI OAuth
- OpenAI API
- Anthropic
- Mistral
- Ollama
- Ollama Cloud

Local provider configuration is stored under:

- `%LOCALAPPDATA%\Veil\settings.json`

Encrypted AI secrets are stored under:

- `%LOCALAPPDATA%\Veil\secrets\ai`

For OpenAI OAuth, Veil can import an auth payload from common local paths such as:

- `%USERPROFILE%\.codex\auth.json`
- `%USERPROFILE%\.chatgpt-local\auth.json`

Local speech models are downloaded into:

- `%LOCALAPPDATA%\Veil\models\speech`

## Hotkeys and Interaction Notes

- Finder uses `Ctrl+Space`
- Dictation uses a learned low-level keyboard trigger with a fallback to right control if no Fn-like key is detected during the learning window

Some behavior depends on Windows shell state, active media sessions, monitor configuration, and fullscreen app detection.

## Performance Benchmark

The repository includes a benchmark runner that compares desktop and fullscreen game-like scenarios with and without Veil.

Run it with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\benchmark-perf.ps1
```

Example with shorter duration and stricter thresholds:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\benchmark-perf.ps1 `
  -SkipBuild `
  -DurationSeconds 15 `
  -WarmupSeconds 4 `
  -MaxGameFpsDropPercent 3 `
  -MaxGameP95FrameIncreaseMs 2
```

Artifacts are written under `artifacts/perf/<timestamp>/` and include:

- `summary.md`
- `summary.json`
- `assertions.json`
- per-scenario `samples.json`
- per-scenario `workload-report.json`
- per-scenario `veil.log`

The benchmark exits with a non-zero code when configured non-regression thresholds are exceeded unless assertions are explicitly skipped.

## Packaging

Build the local installer with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-setup.ps1
```

By default this:

- builds the desktop app for `win-x64`
- stages the output into `artifacts/setup/publish`
- creates a payload archive
- generates a small setup project
- publishes a single-file installer at `artifacts/setup/VeilSetup.exe`

The installer currently performs per-user install/uninstall behavior and registers an uninstall entry in the current user registry hive.

## CI

GitHub Actions currently runs on Windows and:

- checks out the repository
- installs .NET 10
- restores `Veil.sln`
- publishes the desktop app in `Release` for `win-x64`

Workflow file:

- `.github/workflows/build.yml`

At the moment, the workflow does not run the MSTest suite.

## Current State

- The solution contains the main WinUI app, a Rust speech CLI, MSTest coverage, and a benchmark workload tool
- Local settings, AI secrets, and downloaded speech models live under `%LOCALAPPDATA%\Veil`
- The README assumes a Windows-only development environment and does not attempt to support cross-platform build or runtime scenarios

## Known Caveats

- Successful desktop builds depend on both `cargo` and `tsc` being available
- Some runtime features depend on local Windows capabilities or external services
