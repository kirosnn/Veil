[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [int]$DurationSeconds = 45,
    [int]$WarmupSeconds = 8,
    [double]$SampleIntervalSeconds = 1,
    [int]$DesktopCpuLoad = 18000,
    [int]$GameCpuLoad = 28000,
    [double]$MaxGameFpsDropPercent = 5,
    [double]$MaxGameP95FrameIncreaseMs = 3,
    [double]$MaxGameVeilCpuAvg = 8,
    [double]$MaxGameVeilWorkingSetAvgMb = 250,
    [switch]$SkipAssertions,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repoRoot "artifacts\perf"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $artifactsRoot $timestamp
$veilProject = Join-Path $repoRoot "apps\desktop\Veil\Veil.csproj"
$perfGameProject = Join-Path $repoRoot "tools\VeilPerfGame\VeilPerfGame.csproj"
$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet"
$veilExe = Join-Path $repoRoot "apps\desktop\Veil\bin\x64\$Configuration\net10.0-windows10.0.22621.0\Veil.exe"
$perfGameExe = Join-Path $repoRoot "tools\VeilPerfGame\bin\x64\$Configuration\net10.0-windows\VeilPerfGame.exe"
$settingsPath = Join-Path $env:LOCALAPPDATA "Veil\settings.json"
$settingsBackupPath = Join-Path $runRoot "settings.backup.json"

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

function Build-Project {
    param(
        [string]$ProjectPath
    )

    & dotnet build $ProjectPath -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $ProjectPath"
    }
}

function Backup-Settings {
    if (Test-Path $settingsPath) {
        Copy-Item $settingsPath $settingsBackupPath -Force
    }
}

function Restore-Settings {
    if (Test-Path $settingsBackupPath) {
        Copy-Item $settingsBackupPath $settingsPath -Force
        return
    }

    if (Test-Path $settingsPath) {
        Remove-Item $settingsPath -Force
    }
}

function Update-VeilSettings {
    param(
        [string[]]$GameProcessNames
    )

    $directory = Split-Path -Parent $settingsPath
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $payload = @{}
    if (Test-Path $settingsPath) {
        $existing = Get-Content $settingsPath -Raw | ConvertFrom-Json
        if ($null -ne $existing) {
            foreach ($property in $existing.PSObject.Properties) {
                $payload[$property.Name] = $property.Value
            }
        }
    }

    $payload["GameDetectionMode"] = "Hybrid"
    $payload["GameProcessNames"] = $GameProcessNames
    $payload["BackgroundOptimizationEnabled"] = $true
    $payload["SystemPowerBoostEnabled"] = $true

    $json = $payload | ConvertTo-Json -Depth 12
    Set-Content -Path $settingsPath -Value $json -Encoding UTF8
}

function Stop-ProcessTree {
    param(
        [System.Diagnostics.Process]$Process
    )

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            if ($Process.MainWindowHandle -ne 0) {
                $null = $Process.CloseMainWindow()
                $Process.WaitForExit(2000) | Out-Null
            }

            if (-not $Process.HasExited) {
                taskkill /PID $Process.Id /T /F | Out-Null
                $Process.WaitForExit(5000) | Out-Null
            }
        }
    }
    catch {
    }
}

function Get-ProcessIdsByName {
    param(
        [string]$ProcessName
    )

    return @(
        Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Id }
    )
}

function Stop-NewProcessesByName {
    param(
        [string]$ProcessName,
        [int[]]$BaselineIds
    )

    $baselineLookup = @{}
    foreach ($id in @($BaselineIds)) {
        $baselineLookup[$id] = $true
    }

    $currentProcesses = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    foreach ($process in $currentProcesses) {
        if (-not $baselineLookup.ContainsKey($process.Id)) {
            Stop-ProcessTree -Process $process
        }
    }
}

function Get-ScenarioSamples {
    param(
        [hashtable]$TrackedProcesses,
        [int]$DurationSeconds,
        [double]$SampleIntervalSeconds
    )

    $processorCount = [Environment]::ProcessorCount
    $samples = @()
    $state = @{}

    foreach ($entry in $TrackedProcesses.GetEnumerator()) {
        if ($null -ne $entry.Value -and -not $entry.Value.HasExited) {
            $state[$entry.Key] = @{
                LastCpu = $entry.Value.TotalProcessorTime
                LastTimestamp = [DateTime]::UtcNow
            }
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds ([int]([Math]::Max(100, $SampleIntervalSeconds * 1000)))
        $timestamp = Get-Date

        foreach ($entry in $TrackedProcesses.GetEnumerator()) {
            $process = $entry.Value
            if ($null -eq $process) {
                continue
            }

            try {
                if ($process.HasExited) {
                    continue
                }

                $process.Refresh()
                $currentCpu = $process.TotalProcessorTime
                $currentTimestamp = [DateTime]::UtcNow
                $previous = $state[$entry.Key]
                $elapsedWall = ($currentTimestamp - $previous.LastTimestamp).TotalSeconds
                $cpuPercent = 0

                if ($elapsedWall -gt 0) {
                    $cpuPercent = (($currentCpu - $previous.LastCpu).TotalSeconds / ($elapsedWall * $processorCount)) * 100
                }

                $state[$entry.Key] = @{
                    LastCpu = $currentCpu
                    LastTimestamp = $currentTimestamp
                }

                $samples += [pscustomobject]@{
                    Timestamp = $timestamp.ToString("o")
                    Name = $entry.Key
                    CpuPercent = [Math]::Round($cpuPercent, 3)
                    WorkingSetMb = [Math]::Round($process.WorkingSet64 / 1MB, 3)
                    PrivateMb = [Math]::Round($process.PrivateMemorySize64 / 1MB, 3)
                    Handles = $process.HandleCount
                    Threads = $process.Threads.Count
                }
            }
            catch {
            }
        }
    }

    return $samples
}

function Convert-SamplesToSummary {
    param(
        [object[]]$Samples
    )

    $grouped = $Samples | Group-Object Name
    $summary = @{}
    foreach ($group in $grouped) {
        $cpuValues = @($group.Group | ForEach-Object { $_.CpuPercent })
        $workingSetValues = @($group.Group | ForEach-Object { $_.WorkingSetMb })
        $privateValues = @($group.Group | ForEach-Object { $_.PrivateMb })
        $handleValues = @($group.Group | ForEach-Object { $_.Handles })
        $threadValues = @($group.Group | ForEach-Object { $_.Threads })

        $summary[$group.Name] = [ordered]@{
            SampleCount = $group.Count
            CpuAvg = [Math]::Round((($cpuValues | Measure-Object -Average).Average), 3)
            CpuMax = [Math]::Round((($cpuValues | Measure-Object -Maximum).Maximum), 3)
            WorkingSetAvgMb = [Math]::Round((($workingSetValues | Measure-Object -Average).Average), 3)
            WorkingSetMaxMb = [Math]::Round((($workingSetValues | Measure-Object -Maximum).Maximum), 3)
            PrivateAvgMb = [Math]::Round((($privateValues | Measure-Object -Average).Average), 3)
            PrivateMaxMb = [Math]::Round((($privateValues | Measure-Object -Maximum).Maximum), 3)
            HandlesAvg = [Math]::Round((($handleValues | Measure-Object -Average).Average), 3)
            ThreadsAvg = [Math]::Round((($threadValues | Measure-Object -Average).Average), 3)
        }
    }

    return $summary
}

function Wait-ForLogPattern {
    param(
        [string]$LogPath,
        [string]$Pattern,
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $LogPath) {
            $content = Get-Content $LogPath -Raw
            if ($content -match $Pattern) {
                return $true
            }
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

function Invoke-Scenario {
    param(
        [string]$Name,
        [bool]$LaunchVeil,
        [string]$WorkloadMode,
        [int]$CpuLoad,
        [string[]]$GameProcessNames
    )

    $scenarioRoot = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Path $scenarioRoot -Force | Out-Null

    $veilLogPath = Join-Path (Split-Path -Parent $veilExe) "veil.log"
    $gameReportPath = Join-Path $scenarioRoot "workload-report.json"
    $samplesPath = Join-Path $scenarioRoot "samples.json"
    $summaryPath = Join-Path $scenarioRoot "summary.json"

    Update-VeilSettings -GameProcessNames $GameProcessNames

    if (Test-Path $veilLogPath) {
        Remove-Item $veilLogPath -Force
    }

    $baselineVeilIds = Get-ProcessIdsByName -ProcessName "Veil"
    $baselineWorkloadIds = Get-ProcessIdsByName -ProcessName "VeilPerfGame"
    $veilProcess = $null
    $workloadProcess = $null

    try {
        if ($LaunchVeil) {
            $veilProcess = Start-Process -FilePath $veilExe -WorkingDirectory (Split-Path -Parent $veilExe) -PassThru
            Start-Sleep -Seconds 6
        }

        $workloadArgs = @(
            "--mode", $WorkloadMode,
            "--duration", ($DurationSeconds + $WarmupSeconds + 2),
            "--cpu-load", $CpuLoad,
            "--report-path", $gameReportPath
        )
        $workloadProcess = Start-Process -FilePath $perfGameExe -ArgumentList $workloadArgs -WorkingDirectory (Split-Path -Parent $perfGameExe) -PassThru

        if ($LaunchVeil -and $GameProcessNames.Count -gt 0) {
            $null = Wait-ForLogPattern -LogPath $veilLogPath -Pattern "entering minimal mode" -TimeoutSeconds 12
        }

        Start-Sleep -Seconds $WarmupSeconds

        $trackedProcesses = @{}
        $trackedProcesses["Workload"] = $workloadProcess
        if ($LaunchVeil) {
            $trackedProcesses["Veil"] = $veilProcess
        }

        $samples = Get-ScenarioSamples -TrackedProcesses $trackedProcesses -DurationSeconds $DurationSeconds -SampleIntervalSeconds $SampleIntervalSeconds
        $summary = Convert-SamplesToSummary -Samples $samples

        try {
            $workloadProcess.WaitForExit(7000) | Out-Null
        }
        catch {
        }

        $veilLogTail = @()
        if (Test-Path $veilLogPath) {
            Copy-Item $veilLogPath (Join-Path $scenarioRoot "veil.log") -Force
            $veilLogTail = Get-Content $veilLogPath | Select-Object -Last 80
        }

        $workloadReport = $null
        if (Test-Path $gameReportPath) {
            $workloadReport = Get-Content $gameReportPath -Raw | ConvertFrom-Json
        }

        $scenarioResult = [ordered]@{
            Name = $Name
            LaunchVeil = $LaunchVeil
            WorkloadMode = $WorkloadMode
            CpuLoad = $CpuLoad
            GameProcessNames = $GameProcessNames
            WorkloadReport = $workloadReport
            ProcessSummary = $summary
            VeilMinimalModeDetected = $veilLogTail -match "entering minimal mode"
            VeilPerfSnapshots = @($veilLogTail | Where-Object { $_ -match "Perf snapshot" })
        }

        $samples | ConvertTo-Json -Depth 5 | Set-Content -Path $samplesPath -Encoding UTF8
        $scenarioResult | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8
        return $scenarioResult
    }
    finally {
        if ($null -ne $workloadProcess) {
            try {
                $workloadProcess.WaitForExit(5000) | Out-Null
            }
            catch {
            }
        }

        Stop-ProcessTree -Process $workloadProcess
        Stop-ProcessTree -Process $veilProcess
        Stop-NewProcessesByName -ProcessName "VeilPerfGame" -BaselineIds $baselineWorkloadIds
        Stop-NewProcessesByName -ProcessName "Veil" -BaselineIds $baselineVeilIds
        Start-Sleep -Seconds 2
    }
}

function New-MarkdownReport {
    param(
        [object[]]$ScenarioResults,
        [object[]]$Assertions
    )

    $lines = @(
        "# Veil performance benchmark",
        "",
        "Generated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")",
        "",
        "| Scenario | Workload FPS avg | Workload p95 frame ms | Workload CPU avg | Workload WS avg MB | Veil CPU avg | Veil WS avg MB | Minimal mode |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |"
    )

    foreach ($scenario in $ScenarioResults) {
        $workload = $scenario.ProcessSummary.Workload
        $veil = $scenario.ProcessSummary.Veil
        $fps = if ($null -ne $scenario.WorkloadReport) { "{0:N1}" -f $scenario.WorkloadReport.averageFps } else { "-" }
        $p95 = if ($null -ne $scenario.WorkloadReport) { "{0:N2}" -f $scenario.WorkloadReport.p95FrameMs } else { "-" }
        $veilCpu = if ($null -ne $veil) { "{0:N2}" -f $veil.CpuAvg } else { "-" }
        $veilWs = if ($null -ne $veil) { "{0:N2}" -f $veil.WorkingSetAvgMb } else { "-" }
        $minimalMode = if ($scenario.VeilMinimalModeDetected) { "Yes" } else { "No" }

        $lines += "| $($scenario.Name) | $fps | $p95 | {0:N2} | {1:N2} | $veilCpu | $veilWs | $minimalMode |" -f $workload.CpuAvg, $workload.WorkingSetAvgMb
    }

    if ($null -ne $Assertions -and $Assertions.Count -gt 0) {
        $lines += ""
        $lines += "## Assertions"
        $lines += ""
        $lines += "| Assertion | Status | Value | Threshold |"
        $lines += "| --- | --- | ---: | ---: |"

        foreach ($assertion in $Assertions) {
            $status = if ($assertion.Passed) { "PASS" } else { "FAIL" }
            $value = if ($null -ne $assertion.Value) { "{0:N2}" -f $assertion.Value } else { "-" }
            $threshold = if ($null -ne $assertion.Threshold) { "{0:N2}" -f $assertion.Threshold } else { "-" }
            $lines += "| $($assertion.Name) | $status | $value | $threshold |"
        }
    }

    return ($lines -join [Environment]::NewLine)
}

function Get-ScenarioByName {
    param(
        [object[]]$ScenarioResults,
        [string]$Name
    )

    return @($ScenarioResults | Where-Object { $_.Name -eq $Name } | Select-Object -First 1)[0]
}

function New-AssertionResult {
    param(
        [string]$Name,
        [double]$Value,
        [double]$Threshold,
        [bool]$Passed
    )

    return [pscustomobject]@{
        Name = $Name
        Value = [Math]::Round($Value, 3)
        Threshold = [Math]::Round($Threshold, 3)
        Passed = $Passed
    }
}

function Test-BenchmarkAssertions {
    param(
        [object[]]$ScenarioResults
    )

    $gameWithoutVeil = Get-ScenarioByName -ScenarioResults $ScenarioResults -Name "game-without-veil"
    $gameWithVeil = Get-ScenarioByName -ScenarioResults $ScenarioResults -Name "game-with-veil"

    if ($null -eq $gameWithoutVeil -or $null -eq $gameWithVeil) {
        throw "Game scenarios are required to evaluate benchmark assertions."
    }

    if ($null -eq $gameWithoutVeil.WorkloadReport -or $null -eq $gameWithVeil.WorkloadReport) {
        throw "Workload reports are required to evaluate benchmark assertions."
    }

    $assertions = @()

    $baselineFps = [double]$gameWithoutVeil.WorkloadReport.averageFps
    $withVeilFps = [double]$gameWithVeil.WorkloadReport.averageFps
    $fpsDropPercent = if ($baselineFps -le 0.001) {
        0
    }
    else {
        (($baselineFps - $withVeilFps) / $baselineFps) * 100
    }

    $baselineP95 = [double]$gameWithoutVeil.WorkloadReport.p95FrameMs
    $withVeilP95 = [double]$gameWithVeil.WorkloadReport.p95FrameMs
    $p95IncreaseMs = $withVeilP95 - $baselineP95

    $veilProcessSummary = $gameWithVeil.ProcessSummary.Veil
    $veilCpuAvg = if ($null -ne $veilProcessSummary) { [double]$veilProcessSummary.CpuAvg } else { 0 }
    $veilWorkingSetAvgMb = if ($null -ne $veilProcessSummary) { [double]$veilProcessSummary.WorkingSetAvgMb } else { 0 }
    $minimalModeDetected = [bool]$gameWithVeil.VeilMinimalModeDetected

    $assertions += New-AssertionResult -Name "Game FPS drop %" -Value $fpsDropPercent -Threshold $MaxGameFpsDropPercent -Passed ($fpsDropPercent -le $MaxGameFpsDropPercent)
    $assertions += New-AssertionResult -Name "Game p95 frame increase ms" -Value $p95IncreaseMs -Threshold $MaxGameP95FrameIncreaseMs -Passed ($p95IncreaseMs -le $MaxGameP95FrameIncreaseMs)
    $assertions += New-AssertionResult -Name "Game Veil CPU avg %" -Value $veilCpuAvg -Threshold $MaxGameVeilCpuAvg -Passed ($veilCpuAvg -le $MaxGameVeilCpuAvg)
    $assertions += New-AssertionResult -Name "Game Veil WS avg MB" -Value $veilWorkingSetAvgMb -Threshold $MaxGameVeilWorkingSetAvgMb -Passed ($veilWorkingSetAvgMb -le $MaxGameVeilWorkingSetAvgMb)
    $assertions += [pscustomobject]@{
        Name = "Game minimal mode detected"
        Value = if ($minimalModeDetected) { 1 } else { 0 }
        Threshold = 1
        Passed = $minimalModeDetected
    }

    return $assertions
}

Backup-Settings

try {
    if (-not $SkipBuild) {
        Build-Project -ProjectPath $veilProject
        Build-Project -ProjectPath $perfGameProject
    }

    if (-not (Test-Path $veilExe)) {
        throw "Veil executable not found at $veilExe"
    }

    if (-not (Test-Path $perfGameExe)) {
        throw "Workload executable not found at $perfGameExe"
    }

    $results = @()
    $results += Invoke-Scenario -Name "desktop-without-veil" -LaunchVeil:$false -WorkloadMode "desktop" -CpuLoad $DesktopCpuLoad -GameProcessNames @()
    $results += Invoke-Scenario -Name "desktop-with-veil" -LaunchVeil:$true -WorkloadMode "desktop" -CpuLoad $DesktopCpuLoad -GameProcessNames @()
    $results += Invoke-Scenario -Name "game-without-veil" -LaunchVeil:$false -WorkloadMode "game" -CpuLoad $GameCpuLoad -GameProcessNames @("VeilPerfGame")
    $results += Invoke-Scenario -Name "game-with-veil" -LaunchVeil:$true -WorkloadMode "game" -CpuLoad $GameCpuLoad -GameProcessNames @("VeilPerfGame")

    $assertions = @()
    if (-not $SkipAssertions) {
        $assertions = Test-BenchmarkAssertions -ScenarioResults $results
    }

    $markdown = New-MarkdownReport -ScenarioResults $results -Assertions $assertions
    $markdownPath = Join-Path $runRoot "summary.md"
    $jsonPath = Join-Path $runRoot "summary.json"
    $assertionsPath = Join-Path $runRoot "assertions.json"

    Set-Content -Path $markdownPath -Value $markdown -Encoding UTF8
    $results | ConvertTo-Json -Depth 10 | Set-Content -Path $jsonPath -Encoding UTF8
    $assertions | ConvertTo-Json -Depth 6 | Set-Content -Path $assertionsPath -Encoding UTF8

    Write-Host ""
    Write-Host $markdown
    Write-Host ""
    Write-Host "Artifacts: $runRoot"

    if (-not $SkipAssertions) {
        $failedAssertions = @($assertions | Where-Object { -not $_.Passed })
        if ($failedAssertions.Count -gt 0) {
            $failedNames = ($failedAssertions | ForEach-Object { $_.Name }) -join ", "
            throw "Benchmark assertions failed: $failedNames"
        }
    }
}
finally {
    Restore-Settings
}
