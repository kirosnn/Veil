using System.Diagnostics;
using System.Text;
using Veil.Configuration;

namespace Veil.Services;

internal sealed class LocalSpeechTranscriptionService
{
    private const string HelperExecutableName = "handy.exe";
    private static readonly HashSet<string> SupportedModelIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "parakeet-tdt-0.6b-v2",
        "parakeet-tdt-0.6b-v3",
        "moonshine-base",
        "moonshine-tiny-streaming-en",
        "moonshine-small-streaming-en",
        "moonshine-medium-streaming-en",
        "gigaam-v3-e2e-ctc",
        "canary-180m-flash",
        "canary-1b-v2",
        "sense-voice-int8"
    };

    private readonly LocalSpeechModelStore _modelStore = new();
    private readonly SemaphoreSlim _helperBinaryGate = new(1, 1);
    private string? _cachedRepoRoot;
    private string? _cachedHelperPath;

    internal bool CanTranscribe(AppSettings settings)
    {
        return TryGetInstalledSelectedModel(settings, out _);
    }

    internal bool TryGetInstalledSelectedModel(AppSettings settings, out LocalSpeechModelDefinition model)
    {
        model = LocalSpeechModelCatalog.GetById(settings.LocalSpeechModelId) ?? LocalSpeechModelCatalog.GetDefault();
        return SupportedModelIds.Contains(model.Id) && _modelStore.IsInstalled(model);
    }

    internal async Task<string> TranscribeAsync(AppSettings settings, string audioPath, CancellationToken cancellationToken)
    {
        if (!TryGetInstalledSelectedModel(settings, out LocalSpeechModelDefinition model))
        {
            return string.Empty;
        }

        string helperPath = await EnsureHelperBinaryAsync(cancellationToken);
        string modelPath = Path.Combine(_modelStore.ModelsDirectoryPath, model.StorageName);

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(model.Id);
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add(audioPath);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
            }
        });

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(standardOutputTask, standardErrorTask);
        await process.WaitForExitAsync(cancellationToken);

        string standardOutput = standardOutputTask.Result;
        string standardError = standardErrorTask.Result;

        if (process.ExitCode != 0)
        {
            string error = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "Local transcription failed."
                : error.Trim());
        }

        return standardOutput.Trim();
    }

    private async Task<string> EnsureHelperBinaryAsync(CancellationToken cancellationToken)
    {
        await _helperBinaryGate.WaitAsync(cancellationToken);
        try
        {
            string? deployedHelperPath = GetDeployedHelperPath();
            if (deployedHelperPath is not null)
            {
                _cachedHelperPath = deployedHelperPath;
                return deployedHelperPath;
            }

            string? repoRoot = _cachedRepoRoot ??= FindRepoRoot();
            if (repoRoot is null)
            {
                throw new InvalidOperationException("Veil speech helper executable was not found in the installed app.");
            }

            string manifestPath = Path.Combine(repoRoot, "apps", "desktop", "Handy", "Cargo.toml");
            string helperPath = _cachedHelperPath ?? Path.Combine(
                repoRoot,
                "apps",
                "desktop",
                "Handy",
                "target",
                "release",
                HelperExecutableName);

            if (File.Exists(helperPath))
            {
                _cachedHelperPath = helperPath;
                return helperPath;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "cargo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add("--manifest-path");
            startInfo.ArgumentList.Add(manifestPath);
            startInfo.ArgumentList.Add("--release");

            using var process = new Process
            {
                StartInfo = startInfo
            };

            process.Start();

            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(standardOutputTask, standardErrorTask);
            await process.WaitForExitAsync(cancellationToken);

            string standardOutput = standardOutputTask.Result;
            string standardError = standardErrorTask.Result;

            if (process.ExitCode != 0 || !File.Exists(helperPath))
            {
                string error = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "Failed to build the local speech helper."
                    : error.Trim());
            }

            _cachedHelperPath = helperPath;
            return helperPath;
        }
        finally
        {
            _helperBinaryGate.Release();
        }
    }

    private static string? FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Veil.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? GetDeployedHelperPath()
    {
        string baseDirectoryHelperPath = Path.Combine(AppContext.BaseDirectory, HelperExecutableName);
        if (File.Exists(baseDirectoryHelperPath))
        {
            return baseDirectoryHelperPath;
        }

        string toolsDirectoryHelperPath = Path.Combine(AppContext.BaseDirectory, "Tools", HelperExecutableName);
        if (File.Exists(toolsDirectoryHelperPath))
        {
            return toolsDirectoryHelperPath;
        }

        return null;
    }
}
