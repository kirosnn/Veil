$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot "apps/desktop/Veil/Veil.csproj"
$watchRoot = Join-Path $projectRoot "apps/desktop/Veil"
$dotnetCliHome = Join-Path $projectRoot ".dotnet"
$configuration = "Debug"
$platform = "x64"
$restartQuietPeriodMs = 700

$global:veilProcess = $null
$global:pendingRestart = $false
$global:lastRelevantChangeAt = [DateTime]::MinValue
$global:lastQueuedPath = $null

function Get-ProjectMetadata {
    $project = [xml](Get-Content $projectPath)
    $propertyGroup = $project.Project.PropertyGroup | Where-Object { $_.TargetFramework } | Select-Object -First 1
    if ($null -eq $propertyGroup) {
        throw "Unable to resolve project metadata from $projectPath"
    }

    return @{
        TargetFramework = [string]$propertyGroup.TargetFramework
        AssemblyName = if ([string]::IsNullOrWhiteSpace([string]$propertyGroup.AssemblyName)) { "Veil" } else { [string]$propertyGroup.AssemblyName }
    }
}

function Get-ExecutablePath {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$ProjectMetadata
    )

    return Join-Path $watchRoot ("bin\{0}\{1}\{2}\{3}.exe" -f $platform, $configuration, $ProjectMetadata.TargetFramework, $ProjectMetadata.AssemblyName)
}

function Stop-Veil {
    if ($null -eq $global:veilProcess) {
        return
    }

    try {
        if (-not $global:veilProcess.HasExited) {
            Stop-Process -Id $global:veilProcess.Id -Force
            $global:veilProcess.WaitForExit()
        }
    }
    catch {
    }

    $global:veilProcess = $null
}

function Invoke-VeilBuild {
    $env:DOTNET_CLI_HOME = $dotnetCliHome
    $projectMetadata = Get-ProjectMetadata

    & dotnet build $projectPath -c $configuration -p:Platform=$platform --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    $executablePath = Get-ExecutablePath -ProjectMetadata $projectMetadata
    if (-not (Test-Path $executablePath)) {
        throw "Built executable not found at $executablePath"
    }

    return $executablePath
}

function Start-Veil {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = Split-Path -Parent $ExecutablePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

    $global:veilProcess = [System.Diagnostics.Process]::Start($startInfo)
    $global:lastRelevantChangeAt = [DateTime]::UtcNow
    Write-Host ("[{0}] running Veil (pid {1})" -f (Get-Date -Format "HH:mm:ss"), $global:veilProcess.Id)
}

function Restart-Veil {
    try {
        $executablePath = Invoke-VeilBuild
    }
    catch {
        Write-Host ("[{0}] build failed, keeping current app alive" -f (Get-Date -Format "HH:mm:ss")) -ForegroundColor Yellow
        Write-Host $_.Exception.Message -ForegroundColor Yellow
        return
    }

    Stop-Veil
    Start-Sleep -Milliseconds 150
    Start-Veil -ExecutablePath $executablePath
}

function Queue-Restart {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FullPath
    )

    if ($FullPath -match "\\(bin|obj)(\\|$)") {
        return
    }

    if ($FullPath -notmatch "\.(cs|xaml|csproj|manifest)$") {
        return
    }

    $global:pendingRestart = $true
    $global:lastQueuedPath = $FullPath
    $global:lastRelevantChangeAt = [DateTime]::UtcNow
}

$watcher = New-Object System.IO.FileSystemWatcher
$watcher.Path = $watchRoot
$watcher.IncludeSubdirectories = $true
$watcher.NotifyFilter = [System.IO.NotifyFilters]'FileName, LastWrite, CreationTime, Size, DirectoryName'
$watcher.EnableRaisingEvents = $true

$subscriptions = @(
    Register-ObjectEvent -InputObject $watcher -EventName Changed
    Register-ObjectEvent -InputObject $watcher -EventName Created
    Register-ObjectEvent -InputObject $watcher -EventName Renamed
    Register-ObjectEvent -InputObject $watcher -EventName Deleted
)

try {
    Restart-Veil
    Write-Host "watching apps/desktop/Veil"
    Write-Host "press Ctrl+C to stop"

    while ($true) {
        $event = Wait-Event -Timeout 0.2
        if ($null -ne $event) {
            try {
                $fullPath = $event.SourceEventArgs.FullPath
                if (-not [string]::IsNullOrWhiteSpace($fullPath)) {
                    Queue-Restart -FullPath $fullPath
                }
            }
            finally {
                Remove-Event -EventIdentifier $event.EventIdentifier -ErrorAction SilentlyContinue
            }
        }

        if (-not $global:pendingRestart) {
            continue
        }

        if (([DateTime]::UtcNow - $global:lastRelevantChangeAt).TotalMilliseconds -lt $restartQuietPeriodMs) {
            continue
        }

        $relativePath = if ($null -ne $global:lastQueuedPath -and $global:lastQueuedPath.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $global:lastQueuedPath.Substring($projectRoot.Length + 1)
        }
        else {
            $global:lastQueuedPath
        }

        Write-Host ("[{0}] change detected: {1}" -f (Get-Date -Format "HH:mm:ss"), $relativePath)
        $global:pendingRestart = $false
        $global:lastQueuedPath = $null
        Restart-Veil
    }
}
finally {
    foreach ($subscription in $subscriptions) {
        try {
            Unregister-Event -SourceIdentifier $subscription.Name
        }
        catch {
        }
    }

    $watcher.Dispose()
    Stop-Veil
}
