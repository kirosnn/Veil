using System.Diagnostics;
using System.Runtime;
using Veil.Diagnostics;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class GamePerformanceService : IDisposable
{
    private static readonly TimeSpan CompanionRefreshMinimumInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BackgroundMaintenanceMinimumInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BackgroundPressureReliefMinimumInterval = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan TempCleanupMinimumInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan TempFileMinimumAge = TimeSpan.FromHours(18);
    private static readonly TimeSpan PressureReliefMinimumInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PressureTempCleanupMinimumInterval = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan PressureTempFileMinimumAge = TimeSpan.FromHours(3);
    private static readonly TimeSpan WorkingSetTrimMinimumInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PressureCandidateMinimumAge = TimeSpan.FromMinutes(3);
    private const double CpuPressureThresholdPercent = 78;
    private const double BackgroundCpuPressureThresholdPercent = 90;
    private const int MemoryPressureThresholdPercent = 84;
    private const int BackgroundMemoryPressureThresholdPercent = 88;
    private const int SevereMemoryPressureThresholdPercent = 92;
    private static readonly string[] VoiceAndStreamingProcesses =
    [
        "discord",
        "discordcanary",
        "teamspeak",
        "teamspeakclient",
        "ts3client_win64",
        "ts3client_win32",
        "obs64",
        "streamlabsobs",
        "twitchstudio"
    ];
    private static readonly string[] BrowserProcesses =
    [
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "opera"
    ];
    private static readonly string[] StreamingWindowMarkers =
    [
        "youtube",
        "twitch"
    ];
    private static readonly string[] ProtectedBackgroundProcesses =
    [
        "applicationframehost",
        "audiodg",
        "browserhost",
        "code",
        "conhost",
        "csrss",
        "ctfmon",
        "devenv",
        "dllhost",
        "dwm",
        "explorer",
        "fontdrvhost",
        "idle",
        "lockapp",
        "lsass",
        "msedgewebview2",
        "pwsh",
        "registry",
        "searchhost",
        "securityhealthservice",
        "securityhealthsystray",
        "services",
        "shellexperiencehost",
        "sihost",
        "smss",
        "spoolsv",
        "startmenuexperiencehost",
        "steam",
        "steamwebhelper",
        "svchost",
        "system",
        "taskmgr",
        "terminal",
        "textinputhost",
        "widgets",
        "widgetservice",
        "windowsterminal",
        "wininit",
        "winlogon",
        "wt"
    ];
    private static readonly string[] PressureClosableProcessNames =
    [
        "adobearm",
        "adobegcclient",
        "adobenotifier",
        "adobeupdateservice",
        "crashpad_handler",
        "crashreporter",
        "jusched",
        "jusched32",
        "onedrivesetup",
        "reporter",
        "setup",
        "software_reporter_tool",
        "squirrel",
        "teamsmachineinstaller",
        "update",
        "updater"
    ];

    private bool _veilGameOptimizationsApplied;
    private ProcessPriorityClass _veilOriginalPriorityClass = ProcessPriorityClass.Normal;
    private bool _veilOriginalPriorityBoostEnabled = true;
    private int? _boostedGameProcessId;
    private ProcessPriorityClass _boostedGameOriginalPriorityClass = ProcessPriorityClass.Normal;
    private readonly Dictionary<int, ProcessPriorityClass> _boostedCompanionProcesses = [];
    private DateTime _lastCompanionRefreshUtc = DateTime.MinValue;
    private Task? _tempCleanupTask;
    private DateTime _lastTempCleanupUtc = DateTime.MinValue;
    private DateTime _lastPressureTempCleanupUtc = DateTime.MinValue;
    private DateTime _lastPressureReliefUtc = DateTime.MinValue;
    private DateTime _lastWorkingSetTrimUtc = DateTime.MinValue;
    private DateTime _lastBackgroundMaintenanceUtc = DateTime.MinValue;
    private readonly int _currentSessionId = GetCurrentSessionId();
    private readonly PerformanceCounter? _cpuCounter = CreateCpuCounter();
    private readonly object _syncRoot = new();

    internal void ApplyGameOptimizations(int? activeGameProcessId)
    {
        lock (_syncRoot)
        {
            ApplyVeilOptimizations();
            RefreshCompanionProcessPriorities();
            RelieveSystemPressure(activeGameProcessId);

            if (_boostedGameProcessId == activeGameProcessId)
            {
                return;
            }

            RestoreBoostedGamePriority();

            if (!activeGameProcessId.HasValue)
            {
                return;
            }

            try
            {
                using Process gameProcess = Process.GetProcessById(activeGameProcessId.Value);
                _boostedGameOriginalPriorityClass = gameProcess.PriorityClass;

                if (gameProcess.PriorityClass < ProcessPriorityClass.AboveNormal)
                {
                    gameProcess.PriorityClass = ProcessPriorityClass.AboveNormal;
                }

                _boostedGameProcessId = gameProcess.Id;
            }
            catch
            {
                _boostedGameProcessId = null;
            }
        }
    }

    internal void ApplyBackgroundOptimizations()
    {
        lock (_syncRoot)
        {
            RestoreBoostedGamePriority();
            RestoreCompanionProcessPriorities();
            _lastCompanionRefreshUtc = DateTime.MinValue;

            ApplyVeilOptimizations();
            PerformBackgroundMaintenance();
        }
    }

    internal void RestoreNormalOptimizations()
    {
        lock (_syncRoot)
        {
            RestoreBoostedGamePriority();
            RestoreCompanionProcessPriorities();
            _lastCompanionRefreshUtc = DateTime.MinValue;
            _lastPressureReliefUtc = DateTime.MinValue;
            _lastWorkingSetTrimUtc = DateTime.MinValue;
            _lastBackgroundMaintenanceUtc = DateTime.MinValue;

            if (!_veilGameOptimizationsApplied)
            {
                return;
            }

            try
            {
                using Process currentProcess = Process.GetCurrentProcess();
                currentProcess.PriorityBoostEnabled = _veilOriginalPriorityBoostEnabled;
                currentProcess.PriorityClass = _veilOriginalPriorityClass;
            }
            catch
            {
            }

            _veilGameOptimizationsApplied = false;
            ScheduleTempCleanup();
        }
    }

    public void Dispose()
    {
        _cpuCounter?.Dispose();
    }

    private void ApplyVeilOptimizations()
    {
        if (_veilGameOptimizationsApplied)
        {
            return;
        }

        try
        {
            using Process currentProcess = Process.GetCurrentProcess();
            _veilOriginalPriorityClass = currentProcess.PriorityClass;
            _veilOriginalPriorityBoostEnabled = currentProcess.PriorityBoostEnabled;
            currentProcess.PriorityBoostEnabled = false;
            currentProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
            SetProcessWorkingSetSize(currentProcess.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
        }

        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: false, compacting: true);
        }
        catch
        {
        }

        _veilGameOptimizationsApplied = true;
    }

    private void RefreshCompanionProcessPriorities()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastCompanionRefreshUtc < CompanionRefreshMinimumInterval)
        {
            return;
        }

        _lastCompanionRefreshUtc = nowUtc;
        BoostStreamingAndVoiceProcesses();
    }

    private void PerformBackgroundMaintenance()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastBackgroundMaintenanceUtc < BackgroundMaintenanceMinimumInterval)
        {
            return;
        }

        _lastBackgroundMaintenanceUtc = nowUtc;
        ScheduleTempCleanup();

        if (!TryGetPressureSnapshot(out SystemPressureSnapshot snapshot))
        {
            return;
        }

        if (snapshot.CpuPercent < BackgroundCpuPressureThresholdPercent &&
            snapshot.MemoryUsedPercent < BackgroundMemoryPressureThresholdPercent)
        {
            return;
        }

        int trimmedProcesses = 0;
        if (nowUtc - _lastWorkingSetTrimUtc >= WorkingSetTrimMinimumInterval)
        {
            trimmedProcesses = TrimBackgroundWorkingSets(null, snapshot.MemoryUsedPercent >= SevereMemoryPressureThresholdPercent ? 8 : 4);
            _lastWorkingSetTrimUtc = nowUtc;
        }

        bool scheduledTempCleanup = SchedulePressureTempCleanup();
        int closedProcesses = 0;

        if (snapshot.MemoryUsedPercent >= SevereMemoryPressureThresholdPercent)
        {
            TryCompactMemory();

            if (nowUtc - _lastPressureReliefUtc >= BackgroundPressureReliefMinimumInterval)
            {
                closedProcesses = ClosePressureCandidateProcesses(null, 2);
                _lastPressureReliefUtc = nowUtc;
            }
        }

        if (trimmedProcesses > 0 || closedProcesses > 0 || scheduledTempCleanup)
        {
            AppLogger.Info($"Background care engaged. CPU {snapshot.CpuPercent:F0}% RAM {snapshot.MemoryUsedPercent}% Trimmed {trimmedProcesses} background working sets and closed {closedProcesses} helper processes.");
        }
    }

    private void RelieveSystemPressure(int? activeGameProcessId)
    {
        if (!TryGetPressureSnapshot(out SystemPressureSnapshot snapshot))
        {
            return;
        }

        if (!snapshot.IsUnderPressure)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        int trimmedProcesses = 0;

        if (nowUtc - _lastWorkingSetTrimUtc >= WorkingSetTrimMinimumInterval)
        {
            trimmedProcesses = TrimBackgroundWorkingSets(activeGameProcessId, snapshot.MemoryUsedPercent >= SevereMemoryPressureThresholdPercent ? 12 : 6);
            _lastWorkingSetTrimUtc = nowUtc;
        }

        bool scheduledTempCleanup = SchedulePressureTempCleanup();

        if (snapshot.MemoryUsedPercent >= SevereMemoryPressureThresholdPercent)
        {
            TryCompactMemory();
        }

        if (nowUtc - _lastPressureReliefUtc < PressureReliefMinimumInterval)
        {
            if (trimmedProcesses > 0 || scheduledTempCleanup)
            {
                AppLogger.Info($"Pressure relief engaged. CPU {snapshot.CpuPercent:F0}% RAM {snapshot.MemoryUsedPercent}% Trimmed {trimmedProcesses} background working sets.");
            }

            return;
        }

        int closedProcesses = ClosePressureCandidateProcesses(activeGameProcessId);
        if (trimmedProcesses > 0 || closedProcesses > 0 || scheduledTempCleanup)
        {
            AppLogger.Info($"Pressure relief engaged. CPU {snapshot.CpuPercent:F0}% RAM {snapshot.MemoryUsedPercent}% Trimmed {trimmedProcesses} background working sets and closed {closedProcesses} helper processes.");
        }

        _lastPressureReliefUtc = nowUtc;
    }

    private void BoostStreamingAndVoiceProcesses()
    {
        HashSet<int> seenProcessIds = [];

        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string processName = GameProcessMonitor.NormalizeProcessName(process.ProcessName);
                    if (!ShouldBoostCompanionProcess(process, processName))
                    {
                        continue;
                    }

                    seenProcessIds.Add(process.Id);
                    if (_boostedCompanionProcesses.ContainsKey(process.Id))
                    {
                        continue;
                    }

                    _boostedCompanionProcesses[process.Id] = process.PriorityClass;
                    if (process.PriorityClass < ProcessPriorityClass.AboveNormal)
                    {
                        process.PriorityClass = ProcessPriorityClass.AboveNormal;
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }

        int[] staleProcessIds = _boostedCompanionProcesses.Keys
            .Where(processId => !seenProcessIds.Contains(processId))
            .ToArray();

        foreach (int processId in staleProcessIds)
        {
            _boostedCompanionProcesses.Remove(processId);
        }
    }

    private static bool ShouldBoostCompanionProcess(Process process, string processName)
    {
        if (VoiceAndStreamingProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!BrowserProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        string title = process.MainWindowTitle ?? string.Empty;
        return StreamingWindowMarkers.Any(marker => title.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private void RestoreCompanionProcessPriorities()
    {
        foreach ((int processId, ProcessPriorityClass originalPriority) in _boostedCompanionProcesses.ToArray())
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                process.PriorityClass = originalPriority;
            }
            catch
            {
            }
        }

        _boostedCompanionProcesses.Clear();
    }

    private void RestoreBoostedGamePriority()
    {
        if (!_boostedGameProcessId.HasValue)
        {
            return;
        }

        try
        {
            using Process gameProcess = Process.GetProcessById(_boostedGameProcessId.Value);
            gameProcess.PriorityClass = _boostedGameOriginalPriorityClass;
        }
        catch
        {
        }
        finally
        {
            _boostedGameProcessId = null;
        }
    }

    private void ScheduleTempCleanup()
    {
        if (_tempCleanupTask is { IsCompleted: false })
        {
            return;
        }

        if (DateTime.UtcNow - _lastTempCleanupUtc < TempCleanupMinimumInterval)
        {
            return;
        }

        _lastTempCleanupUtc = DateTime.UtcNow;
        _tempCleanupTask = Task.Run(() => CleanupTempFiles(TempFileMinimumAge, maxDeletedItems: 400, includeChildDirectoryFiles: false));
    }

    private bool SchedulePressureTempCleanup()
    {
        if (_tempCleanupTask is { IsCompleted: false })
        {
            return false;
        }

        if (DateTime.UtcNow - _lastPressureTempCleanupUtc < PressureTempCleanupMinimumInterval)
        {
            return false;
        }

        _lastPressureTempCleanupUtc = DateTime.UtcNow;
        _tempCleanupTask = Task.Run(() => CleanupTempFiles(PressureTempFileMinimumAge, maxDeletedItems: 900, includeChildDirectoryFiles: true));
        return true;
    }

    private int TrimBackgroundWorkingSets(int? activeGameProcessId, int maxProcesses)
    {
        int trimmedProcesses = 0;

        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (!ShouldTrimBackgroundProcess(process, activeGameProcessId))
                    {
                        continue;
                    }

                    if (SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1)))
                    {
                        trimmedProcesses++;
                    }

                    if (trimmedProcesses >= maxProcesses)
                    {
                        break;
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }

        return trimmedProcesses;
    }

    private bool ShouldTrimBackgroundProcess(Process process, int? activeGameProcessId)
    {
        if (process.Id == Environment.ProcessId ||
            process.Id == activeGameProcessId ||
            process.SessionId != _currentSessionId ||
            _boostedCompanionProcesses.ContainsKey(process.Id))
        {
            return false;
        }

        string processName = GameProcessMonitor.NormalizeProcessName(process.ProcessName);
        if (string.IsNullOrWhiteSpace(processName) ||
            VoiceAndStreamingProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase) ||
            BrowserProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase) ||
            ProtectedBackgroundProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(process.MainWindowTitle) || process.WorkingSet64 < 80L * 1024 * 1024)
        {
            return false;
        }

        try
        {
            return process.PriorityClass < ProcessPriorityClass.High;
        }
        catch
        {
            return false;
        }
    }

    private int ClosePressureCandidateProcesses(int? activeGameProcessId, int maxProcesses = 3)
    {
        int closedProcesses = 0;

        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (!ShouldClosePressureCandidate(process, activeGameProcessId))
                    {
                        continue;
                    }

                    if (!TryClosePressureCandidate(process))
                    {
                        continue;
                    }

                    closedProcesses++;
                    if (closedProcesses >= maxProcesses)
                    {
                        break;
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }

        return closedProcesses;
    }

    private bool ShouldClosePressureCandidate(Process process, int? activeGameProcessId)
    {
        if (process.Id == Environment.ProcessId ||
            process.Id == activeGameProcessId ||
            process.SessionId != _currentSessionId ||
            _boostedCompanionProcesses.ContainsKey(process.Id))
        {
            return false;
        }

        string processName = GameProcessMonitor.NormalizeProcessName(process.ProcessName);
        if (string.IsNullOrWhiteSpace(processName) ||
            VoiceAndStreamingProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase) ||
            BrowserProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase) ||
            ProtectedBackgroundProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!MatchesClosablePressureProcess(processName))
        {
            return false;
        }

        try
        {
            if (DateTime.UtcNow - process.StartTime.ToUniversalTime() < PressureCandidateMinimumAge)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            if (process.TotalProcessorTime > TimeSpan.FromSeconds(20))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return process.WorkingSet64 >= 20L * 1024 * 1024;
    }

    private static bool MatchesClosablePressureProcess(string processName)
    {
        if (PressureClosableProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return processName.Contains("update", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("updater", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("patcher", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("report", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("bootstrap", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("setup", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryClosePressureCandidate(Process process)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(process.MainWindowTitle) && process.CloseMainWindow())
            {
                return process.WaitForExit(1500);
            }

            process.Kill(entireProcessTree: true);
            return process.WaitForExit(2000);
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetPressureSnapshot(out SystemPressureSnapshot snapshot)
    {
        double cpuPercent = 0;

        if (_cpuCounter != null)
        {
            try
            {
                cpuPercent = Math.Clamp(_cpuCounter.NextValue(), 0, 100);
            }
            catch
            {
                cpuPercent = 0;
            }
        }

        var memoryStatus = MemoryStatusEx.Create();
        if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.ullTotalPhys == 0)
        {
            snapshot = default;
            return cpuPercent > 0;
        }

        double usedRatio = (memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys) / (double)memoryStatus.ullTotalPhys;
        snapshot = new SystemPressureSnapshot(cpuPercent, (int)Math.Round(Math.Clamp(usedRatio * 100.0, 0, 100)));
        return true;
    }

    private void TryCompactMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: false, compacting: true);
        }
        catch
        {
        }
    }

    private static PerformanceCounter? CreateCpuCounter()
    {
        PerformanceCounter? cpuCounter = null;

        try
        {
            cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _ = cpuCounter.NextValue();
        }
        catch
        {
            cpuCounter?.Dispose();
            return null;
        }

        return cpuCounter;
    }

    private static int GetCurrentSessionId()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        return currentProcess.SessionId;
    }

    private static void CleanupTempFiles(TimeSpan minimumFileAge, int maxDeletedItems, bool includeChildDirectoryFiles)
    {
        string[] tempDirectories =
        [
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")
        ];

        HashSet<string> uniqueDirectories = tempDirectories
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.TrimEnd(Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        DateTime cutoff = DateTime.UtcNow - minimumFileAge;
        int deletedItems = 0;

        foreach (string directory in uniqueDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (deletedItems >= maxDeletedItems)
                    {
                        return;
                    }

                    TryDeleteStaleFile(filePath, cutoff, ref deletedItems);
                }

                foreach (string childDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (deletedItems >= maxDeletedItems)
                    {
                        return;
                    }

                    if (includeChildDirectoryFiles)
                    {
                        TryDeleteChildDirectoryFiles(childDirectory, cutoff, ref deletedItems, maxDeletedItems);
                    }

                    TryDeleteEmptyOrStaleDirectory(childDirectory, cutoff, ref deletedItems);
                }
            }
            catch
            {
            }
        }
    }

    private static void TryDeleteChildDirectoryFiles(string directoryPath, DateTime cutoff, ref int deletedItems, int maxDeletedItems)
    {
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (deletedItems >= maxDeletedItems)
                {
                    return;
                }

                TryDeleteStaleFile(filePath, cutoff, ref deletedItems);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteStaleFile(string filePath, DateTime cutoff, ref int deletedItems)
    {
        try
        {
            FileInfo file = new(filePath);
            if (!file.Exists || file.LastWriteTimeUtc > cutoff)
            {
                return;
            }

            file.Delete();
            deletedItems++;
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyOrStaleDirectory(string directoryPath, DateTime cutoff, ref int deletedItems)
    {
        try
        {
            DirectoryInfo directory = new(directoryPath);
            if (!directory.Exists)
            {
                return;
            }

            bool hasRecentContent = directory.EnumerateFileSystemInfos()
                .Any(item => item.LastWriteTimeUtc > cutoff);

            if (hasRecentContent)
            {
                return;
            }

            directory.Delete(recursive: true);
            deletedItems++;
        }
        catch
        {
        }
    }

    private readonly record struct SystemPressureSnapshot(double CpuPercent, int MemoryUsedPercent)
    {
        public bool IsUnderPressure =>
            CpuPercent >= CpuPressureThresholdPercent ||
            MemoryUsedPercent >= MemoryPressureThresholdPercent;
    }
}
