$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding = [System.Text.Encoding]::UTF8

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
$global:devMutex = $null

# -- UI helpers --

$script:BoxTL = [char]0x2554   # top-left
$script:BoxTR = [char]0x2557   # top-right
$script:BoxBL = [char]0x255A   # bottom-left
$script:BoxBR = [char]0x255D   # bottom-right
$script:BoxH  = [char]0x2550   # horizontal
$script:BoxV  = [char]0x2551   # vertical
$script:Dash  = [char]0x2500   # light horizontal

function Write-Timestamp {
    Write-Host "  " -NoNewline
    Write-Host (Get-Date -Format "HH:mm:ss") -ForegroundColor DarkGray -NoNewline
    Write-Host " " -NoNewline
}

function Write-Info {
    param([string]$Message)
    Write-Timestamp
    Write-Host "[INFO]" -ForegroundColor Cyan -NoNewline
    Write-Host "  $Message"
}

function Write-Ok {
    param([string]$Message)
    Write-Timestamp
    Write-Host " [OK] " -ForegroundColor Green -NoNewline
    Write-Host " $Message"
}

function Write-Warn {
    param([string]$Message)
    Write-Timestamp
    Write-Host "[WARN]" -ForegroundColor Yellow -NoNewline
    Write-Host "  $Message" -ForegroundColor Yellow
}

function Write-Err {
    param([string]$Message)
    Write-Timestamp
    Write-Host " [ERR]" -ForegroundColor Red -NoNewline
    Write-Host "  $Message" -ForegroundColor Red
}

function Write-Banner {
    Write-Host ""
    Write-Host "  __     __   _ _ " -ForegroundColor White
    Write-Host "  \ \   / /__(_) |" -ForegroundColor White
    Write-Host "   \ \ / / _ \ | |" -ForegroundColor White
    Write-Host "    \ V /  __/ | |" -ForegroundColor White
    Write-Host "     \_/ \___|_|_|" -ForegroundColor White
    Write-Host ""
}

function Write-Separator {
    $line = "$script:Dash" * 40
    Write-Host "  $line" -ForegroundColor DarkGray
}

function Write-Help {
    Write-Host "  Keys: " -NoNewline
    Write-Host "[R]" -ForegroundColor Cyan -NoNewline
    Write-Host " restart  " -NoNewline
    Write-Host "[P]" -ForegroundColor Cyan -NoNewline
    Write-Host " list processes  " -NoNewline
    Write-Host "[K]" -ForegroundColor Cyan -NoNewline
    Write-Host " kill workspace processes  " -NoNewline
    Write-Host "[H]" -ForegroundColor Cyan -NoNewline
    Write-Host " help  " -NoNewline
    Write-Host "[Ctrl+C]" -ForegroundColor Cyan -NoNewline
    Write-Host " stop"
}

# -- Project helpers --

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

function Get-LogPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    return Join-Path (Split-Path -Parent $ExecutablePath) "veil.log"
}

# -- Process management --

function Get-ExcludedProcessIds {
    $ids = New-Object System.Collections.Generic.HashSet[int]
    [void]$ids.Add($PID)

    try {
        $current = Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction Stop
        $parentId = [int]$current.ParentProcessId

        while ($parentId -gt 0 -and -not $ids.Contains($parentId)) {
            [void]$ids.Add($parentId)
            $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $parentId" -ErrorAction Stop
            $parentId = [int]$parent.ParentProcessId
        }
    }
    catch {
    }

    return $ids
}

function Get-WorkspaceProcessTargets {
    $excludedIds = Get-ExcludedProcessIds
    $targets = @()
    $knownNames = @(
        "Veil.exe",
        "dotnet.exe",
        "pwsh.exe",
        "powershell.exe",
        "cmd.exe",
        "node.exe",
        "bun.exe",
        "codex.exe",
        "claude.exe"
    )

    try {
        $processes = Get-CimInstance Win32_Process -ErrorAction Stop
    }
    catch {
        return @()
    }

    foreach ($process in $processes) {
        $processId = [int]$process.ProcessId
        if ($excludedIds.Contains($processId)) {
            continue
        }

        $name = [string]$process.Name
        $commandLine = if ($null -eq $process.CommandLine) { "" } else { [string]$process.CommandLine }
        $hasProjectPath = $commandLine.IndexOf($projectRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        $isVeilBinary = $name.Equals("Veil.exe", [System.StringComparison]::OrdinalIgnoreCase)
        $isDevScript = $commandLine.IndexOf("scripts\dev.ps1", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        $isKnownTool = $knownNames -contains $name

        if ($isVeilBinary -or $isDevScript -or ($hasProjectPath -and $isKnownTool)) {
            $targets += $process
        }
    }

    return $targets | Sort-Object ProcessId -Unique
}

function Get-ProcessKind {
    param($Process)

    $name = [string]$Process.Name
    $commandLine = if ($null -eq $Process.CommandLine) { "" } else { [string]$Process.CommandLine }

    if ($name.Equals("Veil.exe", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "app"
    }

    if ($commandLine.IndexOf("scripts\dev.ps1", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return "dev"
    }

    if ($commandLine.IndexOf($projectRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return "workspace"
    }

    return "other"
}

function Show-WorkspaceProcesses {
    $targets = Get-WorkspaceProcessTargets
    Write-Separator
    if ($targets.Count -eq 0) {
        Write-Info "No extra Veil/workspace processes detected"
        Write-Separator
        return
    }

    Write-Info "Detected Veil/workspace processes:"
    foreach ($target in $targets) {
        $commandLine = if ($null -eq $target.CommandLine) { "" } else { [string]$target.CommandLine }
        $summary = if ($commandLine.Length -gt 110) { $commandLine.Substring(0, 110) + "..." } else { $commandLine }
        Write-Host "       " -NoNewline
        Write-Host ("[{0}]" -f (Get-ProcessKind -Process $target)) -ForegroundColor DarkCyan -NoNewline
        Write-Host " pid=" -NoNewline
        Write-Host $target.ProcessId -ForegroundColor White -NoNewline
        Write-Host "  name=" -NoNewline
        Write-Host $target.Name -ForegroundColor White -NoNewline
        if (-not [string]::IsNullOrWhiteSpace($summary)) {
            Write-Host "  cmd=" -NoNewline
            Write-Host $summary -ForegroundColor DarkGray
        }
        else {
            Write-Host ""
        }
    }
    Write-Separator
}

function Stop-WorkspaceProcesses {
    $targets = Get-WorkspaceProcessTargets
    if ($targets.Count -eq 0) {
        Write-Info "No Veil/workspace processes to stop"
        return
    }

    foreach ($target in $targets) {
        Write-Info "Stopping $($target.Name) (pid $($target.ProcessId))"
        try {
            & taskkill /F /T /PID $target.ProcessId 2>$null | Out-Null
        }
        catch {
        }
    }

    Start-Sleep -Milliseconds 400
}

function Enter-SingleInstanceMode {
    $mutexName = "Local\VeilDev_{0}" -f (($projectRoot.ToLowerInvariant()) -replace "[^a-z0-9]+", "_")
    $createdNew = $false
    $global:devMutex = New-Object System.Threading.Mutex($true, $mutexName, [ref]$createdNew)

    if ($createdNew) {
        return
    }

    $acquired = $global:devMutex.WaitOne(0)
    if ($acquired) {
        return
    }

    Stop-WorkspaceProcesses

    $acquired = $global:devMutex.WaitOne(2000)
    if (-not $acquired) {
        throw "Another Veil dev instance is still active."
    }
}

function Stop-Veil {
    $global:veilProcess = $null

    # Use taskkill which reliably terminates the process tree on Windows
    $procs = Get-Process -Name "Veil" -ErrorAction SilentlyContinue
    if ($null -eq $procs -or $procs.Count -eq 0) {
        return
    }

    foreach ($p in $procs) {
        Write-Info "Stopping Veil (pid $($p.Id))"
    }

    & taskkill /F /IM Veil.exe 2>$null | Out-Null

    # Wait until the exe file is actually released
    $exePath = $null
    try {
        $meta = Get-ProjectMetadata
        $exePath = Get-ExecutablePath -ProjectMetadata $meta
    }
    catch {
    }

    if ($null -ne $exePath -and (Test-Path $exePath)) {
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                [System.IO.File]::Open($exePath, 'Open', 'ReadWrite', 'None').Dispose()
                break
            }
            catch {
                Start-Sleep -Milliseconds 100
            }
        }
    }
}

function Invoke-VeilBuild {
    $env:DOTNET_CLI_HOME = $dotnetCliHome
    $projectMetadata = Get-ProjectMetadata

    Write-Info "Building..."
    Write-Separator

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $output = & dotnet build $projectPath -c $configuration -p:Platform=$platform --nologo 2>&1
    $exitCode = $LASTEXITCODE
    $stopwatch.Stop()
    $elapsed = $stopwatch.Elapsed.TotalSeconds.ToString("F1")

    $errorCount = 0
    $warningCount = 0
    $errorLines = @()

    foreach ($line in $output) {
        $text = "$line"
        if ([string]::IsNullOrWhiteSpace($text)) { continue }

        if ($text -match "^\s*(Restauration|Tous les projets|Identification|Restore completed|All projects)") {
            continue
        }

        # Skip MSB3026 retry spam (keep only the final MSB3027/MSB3021 errors)
        if ($text -match "MSB3026") {
            continue
        }

        if ($text -match ": error ") {
            $errorCount++

            if ($text -match "([^\\]+\.\w+)\((\d+),(\d+)\): error (\w+): (.+?)\s*\[") {
                $file = $Matches[1]
                $lineNum = $Matches[2]
                $code = $Matches[4]
                $msg = $Matches[5]
                $errorLines += @{ File = $file; Line = $lineNum; Code = $code; Message = $msg }
            }
            elseif ($text -match "error (\w+): (.+)") {
                $errorLines += @{ File = ""; Line = ""; Code = $Matches[1]; Message = $Matches[2] }
            }
        }
        elseif ($text -match ": warning ") {
            $warningCount++

            if ($text -match "([^\\]+\.\w+)\((\d+),(\d+)\): warning (\w+): (.+?)\s*\[") {
                $wFile = $Matches[1]
                $wLine = $Matches[2]
                $wCode = $Matches[4]
                $wMsg = $Matches[5]
                Write-Host "       " -NoNewline
                Write-Host "$wFile" -ForegroundColor White -NoNewline
                Write-Host ":" -ForegroundColor DarkGray -NoNewline
                Write-Host "$wLine" -ForegroundColor White -NoNewline
                Write-Host "  $wCode" -ForegroundColor Yellow -NoNewline
                Write-Host "  $wMsg" -ForegroundColor DarkYellow
            }
        }
        elseif ($text -match "^\s*\d+ Avertissement|^\s*\d+ Erreur|^\s*\d+ Warning|^\s*\d+ Error|Temps |Time Elapsed|Build succeeded|ECHEC|FAILED") {
            continue
        }
        elseif ($text -match "^\s*(.+?) -> (.+)$") {
            continue
        }
    }

    Write-Separator

    if ($exitCode -ne 0) {
        if ($errorLines.Count -gt 0) {
            Write-Host ""
            foreach ($err in $errorLines) {
                Write-Host "       " -NoNewline
                if ($err.File) {
                    Write-Host "$($err.File)" -ForegroundColor White -NoNewline
                    Write-Host ":" -ForegroundColor DarkGray -NoNewline
                    Write-Host "$($err.Line)" -ForegroundColor White -NoNewline
                    Write-Host "  " -NoNewline
                }
                Write-Host "$($err.Code)" -ForegroundColor Red -NoNewline
                Write-Host "  $($err.Message)" -ForegroundColor DarkRed
            }
            Write-Host ""
        }

        $ePlural = if ($errorCount -ne 1) { "s" } else { "" }
        $summary = "$errorCount error$ePlural"
        if ($warningCount -gt 0) {
            $wPlural = if ($warningCount -ne 1) { "s" } else { "" }
            $summary += ", $warningCount warning$wPlural"
        }
        Write-Err "Build failed  ($summary, ${elapsed}s)"
        throw "dotnet build failed with exit code $exitCode"
    }

    $summary = "0 errors"
    if ($warningCount -gt 0) {
        $wPlural = if ($warningCount -ne 1) { "s" } else { "" }
        $summary += ", $warningCount warning$wPlural"
    }
    Write-Ok "Build succeeded  ($summary, ${elapsed}s)"

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
    Start-Sleep -Milliseconds 1200

    if ($global:veilProcess.HasExited) {
        Write-Err "Veil exited immediately after launch"
        $logPath = Get-LogPath -ExecutablePath $ExecutablePath
        if (Test-Path $logPath) {
            Write-Separator
            Write-Info "Recent veil.log entries:"
            Get-Content $logPath -Tail 20 | ForEach-Object {
                Write-Host "       $_" -ForegroundColor DarkRed
            }
            Write-Separator
        }
        throw "Veil exited immediately after launch"
    }

    Write-Ok "Running Veil  (pid $($global:veilProcess.Id))"
    Write-Info "Executable  $ExecutablePath"
}

function Restart-Veil {
    Stop-Veil
    Start-Sleep -Milliseconds 200

    try {
        $executablePath = Invoke-VeilBuild
    }
    catch {
        Write-Warn "Build failed, nothing to run"
        return
    }

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

function Handle-DevKey {
    param([System.ConsoleKeyInfo]$KeyInfo)

    switch ($KeyInfo.Key) {
        ([ConsoleKey]::R) {
            Write-Host ""
            Write-Info "Manual restart requested"
            Restart-Veil
            Write-Host ""
            return
        }
        ([ConsoleKey]::P) {
            Write-Host ""
            Show-WorkspaceProcesses
            Write-Host ""
            return
        }
        ([ConsoleKey]::K) {
            Write-Host ""
            Write-Info "Manual process cleanup requested"
            Stop-WorkspaceProcesses
            Write-Host ""
            return
        }
        ([ConsoleKey]::H) {
            Write-Host ""
            Write-Help
            Write-Host ""
            return
        }
    }
}

# -- Main --

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
    Enter-SingleInstanceMode
    Stop-WorkspaceProcesses
    Write-Banner
    Write-Info "Project root  $projectRoot"
    Write-Info "Watching      apps/desktop/Veil"
    Show-WorkspaceProcesses
    Write-Help
    Write-Host ""
    Restart-Veil
    Write-Host ""

    while ($true) {
        while ([Console]::KeyAvailable) {
            $keyInfo = [Console]::ReadKey($true)
            Handle-DevKey -KeyInfo $keyInfo
        }

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

        Write-Host ""
        Write-Info "Change detected  $relativePath"
        $global:pendingRestart = $false
        $global:lastQueuedPath = $null
        Restart-Veil
        Write-Host ""
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
    if ($null -ne $global:devMutex) {
        try {
            $global:devMutex.ReleaseMutex()
        }
        catch {
        }
        $global:devMutex.Dispose()
        $global:devMutex = $null
    }
    Write-Host ""
    Write-Info "Stopped"
}
