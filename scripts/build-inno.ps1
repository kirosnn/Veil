[CmdletBinding()]
param(
    [string]$Configuration     = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot  = Resolve-Path (Join-Path $PSScriptRoot "..")
$tf           = "net10.0-windows10.0.22621.0"
$veilProj     = Join-Path $projectRoot "apps\desktop\Veil\Veil.csproj"
$terminalProj = Join-Path $projectRoot "apps\desktop\VeilTerminal\VeilTerminal.csproj"

$veilBuildDir     = Join-Path $projectRoot "apps\desktop\Veil\bin\$Configuration\$tf\$RuntimeIdentifier"
$terminalBuildDir = Join-Path $projectRoot "apps\desktop\VeilTerminal\bin\$Configuration\$tf\$RuntimeIdentifier"

$artifactsRoot = Join-Path $projectRoot "artifacts"
$veilDest      = Join-Path $artifactsRoot "build\Veil"
$terminalDest  = Join-Path $artifactsRoot "build\VeilTerminal"
$innoOut       = Join-Path $artifactsRoot "inno"

$dotnetCliHome = Join-Path $projectRoot ".dotnet-cli"
$nugetPackages = Join-Path $dotnetCliHome ".nuget\packages"
$env:DOTNET_CLI_HOME                   = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT       = "1"
$env:NUGET_PACKAGES                    = $nugetPackages
New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
New-Item -ItemType Directory -Path $nugetPackages  -Force | Out-Null

# --- 0. Locate Inno Setup ---
$iscc = $null
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$pathCmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($pathCmd) {
    $isccCandidates = $isccCandidates + @($pathCmd.Source)
}
foreach ($c in $isccCandidates) {
    if ($c -and (Test-Path $c)) {
        $iscc = $c
        break
    }
}
if (-not $iscc) {
    Write-Host "Inno Setup not found -- installing via winget..."
    winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) {
        $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    }
    if (-not (Test-Path $iscc)) {
        throw "Inno Setup install failed -- ISCC.exe not found"
    }
}
Write-Host "Inno Setup: $iscc"

# --- 1. Generate terminal icons ---
Write-Host ""
Write-Host "=== Generating terminal icons ==="
python (Join-Path $projectRoot "scripts\make-terminal-icons.py")

# --- 2. Clean staging ---
Write-Host ""
Write-Host "=== Cleaning staging ==="
foreach ($d in @($veilDest, $terminalDest, $innoOut)) {
    if (Test-Path $d) {
        Remove-Item $d -Recurse -Force
    }
    New-Item -ItemType Directory -Path $d | Out-Null
}

# --- 3. Build Veil ---
Write-Host ""
Write-Host "=== Building Veil ==="
dotnet build $veilProj -c $Configuration -r $RuntimeIdentifier --self-contained false
if (-not (Test-Path $veilBuildDir)) {
    throw "Veil build output missing at $veilBuildDir"
}
Get-ChildItem $veilBuildDir -Force | Where-Object { $_.Name -ne "publish" } |
    Copy-Item -Destination $veilDest -Recurse -Force

# --- 4. Build VeilTerminal ---
Write-Host ""
Write-Host "=== Building VeilTerminal ==="
dotnet build $terminalProj -c $Configuration -r $RuntimeIdentifier --self-contained false
if (-not (Test-Path $terminalBuildDir)) {
    throw "VeilTerminal build output missing at $terminalBuildDir"
}
Get-ChildItem $terminalBuildDir -Force | Where-Object { $_.Name -ne "publish" } |
    Copy-Item -Destination $terminalDest -Recurse -Force

# --- 5. Compile Inno Setup ---
Write-Host ""
Write-Host "=== Compiling installer ==="
$issFile = Join-Path $projectRoot "installer\veil.iss"
& $iscc $issFile
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe exited with code $LASTEXITCODE"
}

$setupExe = Join-Path $innoOut "VeilSetup.exe"
if (-not (Test-Path $setupExe)) {
    throw "Expected $setupExe was not created"
}
Write-Host ""
Write-Host "Installer ready: $setupExe"
