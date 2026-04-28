[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$targetFramework = "net10.0-windows10.0.22621.0"
$veilProject = Join-Path $projectRoot "apps\desktop\Veil\Veil.csproj"
$terminalProject = Join-Path $projectRoot "apps\desktop\VeilTerminal\VeilTerminal.csproj"

$veilBuildDir = Join-Path $projectRoot "apps\desktop\Veil\bin\$Configuration\$targetFramework\$RuntimeIdentifier"
$terminalBuildDir = Join-Path $projectRoot "apps\desktop\VeilTerminal\bin\$Configuration\$targetFramework\$RuntimeIdentifier"

$artifactsRoot = Join-Path $projectRoot "artifacts"
$veilDestination = Join-Path $artifactsRoot "build\Veil"
$terminalDestination = Join-Path $artifactsRoot "build\VeilTerminal"
$installerOutput = Join-Path $artifactsRoot "inno"

$dotnetCliHome = Join-Path $projectRoot ".dotnet-cli"
$nugetPackages = Join-Path $dotnetCliHome ".nuget\packages"
$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:NUGET_PACKAGES = $nugetPackages

New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
New-Item -ItemType Directory -Path $nugetPackages -Force | Out-Null

$iscc = $null
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$pathCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($pathCommand) {
    $isccCandidates = $isccCandidates + @($pathCommand.Source)
}

foreach ($candidate in $isccCandidates) {
    if ($candidate -and (Test-Path $candidate)) {
        $iscc = $candidate
        break
    }
}

if (-not $iscc) {
    Write-Host "Inno Setup not found. Installing with winget..."
    winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements

    $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) {
        $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    }
    if (-not (Test-Path $iscc)) {
        throw "Inno Setup installation failed. ISCC.exe was not found."
    }
}

Write-Host "Inno Setup: $iscc"

Write-Host ""
Write-Host "=== Generating terminal icons ==="
python (Join-Path $projectRoot "scripts\make-terminal-icons.py")

Write-Host ""
Write-Host "=== Cleaning installer staging ==="
foreach ($directory in @($veilDestination, $terminalDestination, $installerOutput)) {
    if (Test-Path $directory) {
        Remove-Item $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory | Out-Null
}

Write-Host ""
Write-Host "=== Building Veil ==="
dotnet build $veilProject -c $Configuration -r $RuntimeIdentifier --self-contained false
if (-not (Test-Path $veilBuildDir)) {
    throw "Veil build output was not created at $veilBuildDir."
}
Get-ChildItem $veilBuildDir -Force |
    Where-Object { $_.Name -ne "publish" } |
    Copy-Item -Destination $veilDestination -Recurse -Force

Write-Host ""
Write-Host "=== Building VeilTerminal ==="
dotnet build $terminalProject -c $Configuration -r $RuntimeIdentifier --self-contained false
if (-not (Test-Path $terminalBuildDir)) {
    throw "VeilTerminal build output was not created at $terminalBuildDir."
}
Get-ChildItem $terminalBuildDir -Force |
    Where-Object { $_.Name -ne "publish" } |
    Copy-Item -Destination $terminalDestination -Recurse -Force

Write-Host ""
Write-Host "=== Compiling Inno Setup installer ==="
$installerScript = Join-Path $projectRoot "installer\veil.iss"
& $iscc $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe exited with code $LASTEXITCODE."
}

$setupExe = Join-Path $installerOutput "VeilSetup.exe"
if (-not (Test-Path $setupExe)) {
    throw "Expected installer was not created at $setupExe."
}

Write-Host ""
Write-Host "Installer ready: $setupExe"
