# Veil — Claude Code Instructions

## Project

Windows desktop app (C# · WPF · .NET 10 · win-x64).  
Project root: `C:\Users\Nassim\Projects\Veil`

## Scripts — use these, never reinvent them

| Situation | Script to run |
|-----------|---------------|
| User wants to run / test / develop the app | `powershell -ExecutionPolicy Bypass -File scripts\dev.ps1` |
| User wants to build, package, or produce an installer / .exe | `powershell -ExecutionPolicy Bypass -File scripts\build-setup.ps1` |
| User wants to release, publish, or ship a version | `powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Version <x.y.z>` |

### What each script does

**`scripts\dev.ps1`** — Debug build + launches `Veil.exe`, watches `apps/desktop/Veil` for changes and restarts automatically. Interactive keys: R restart · P list processes · K kill workspace · Ctrl+C stop.

**`scripts\build-setup.ps1`** — Release build of Veil + VeilTerminal, runs Inno Setup, outputs `artifacts\inno\VeilSetup.exe`. After running it, always verify the file exists and report its path and size.

**`scripts\release.ps1 -Version x.y.z`** — Creates annotated git tag `vX.Y.Z`, pushes to origin, triggers GitHub Actions (`release.yml`) which builds and publishes `VeilSetup.exe` as a release asset.

## Rules — apply automatically, no user command needed

1. **Before any release**: run `git status --porcelain`. If the output is non-empty, stop and tell the user which files need to be committed and pushed first.

2. **After every build**: verify `artifacts\inno\VeilSetup.exe` exists. If yes, report path + size. If no, surface the relevant build error lines.

3. **Never reimplement** what these scripts already do (dotnet build, Inno Setup invocation, git tag/push). Always delegate to the script.

4. **Version format**: strip or add the `v` prefix as needed (`1.0.0` → `v1.0.0`). Never guess a version — ask the user if not provided.

5. **GitHub Actions**: after a release tag is pushed, inform the user they can monitor the build at `https://github.com/kirosnn/Veil/actions`.
