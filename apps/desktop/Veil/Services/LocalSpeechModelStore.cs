using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;

namespace Veil.Services;

internal enum LocalSpeechModelDownloadStage
{
    Downloading,
    Verifying,
    Extracting,
    Completed
}

internal sealed record LocalSpeechModelDownloadProgress(
    string ModelId,
    LocalSpeechModelDownloadStage Stage,
    long DownloadedBytes,
    long TotalBytes);

internal sealed class LocalSpeechModelStore
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly string _modelsDirectoryPath;

    internal LocalSpeechModelStore()
    {
        _modelsDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Veil",
            "models",
            "speech");
    }

    internal string ModelsDirectoryPath => _modelsDirectoryPath;

    internal bool IsInstalled(LocalSpeechModelDefinition model)
    {
        string finalPath = GetFinalPath(model);
        return model.IsArchive ? Directory.Exists(finalPath) : File.Exists(finalPath);
    }

    internal async Task DownloadModelAsync(
        LocalSpeechModelDefinition model,
        IProgress<LocalSpeechModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_modelsDirectoryPath);

        string partialPath = GetPartialPath(model);
        string finalPath = GetFinalPath(model);

        DeletePartialArtifacts(model);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            using HttpResponseMessage response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? 0;
            progress?.Report(new LocalSpeechModelDownloadProgress(
                model.Id,
                LocalSpeechModelDownloadStage.Downloading,
                0,
                totalBytes));

            await using (Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072];
                long downloadedBytes = 0;
                long lastReportedBytes = 0;
                int bytesRead;
                var reportStopwatch = Stopwatch.StartNew();

                while ((bytesRead = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;

                    bool shouldReport = reportStopwatch.ElapsedMilliseconds >= 200 ||
                        downloadedBytes == totalBytes ||
                        downloadedBytes - lastReportedBytes >= 4 * 1024 * 1024;

                    if (shouldReport)
                    {
                        progress?.Report(new LocalSpeechModelDownloadProgress(
                            model.Id,
                            LocalSpeechModelDownloadStage.Downloading,
                            downloadedBytes,
                            totalBytes));
                        lastReportedBytes = downloadedBytes;
                        reportStopwatch.Restart();
                    }
                }
            }

            progress?.Report(new LocalSpeechModelDownloadProgress(
                model.Id,
                LocalSpeechModelDownloadStage.Verifying,
                0,
                0));

            await VerifySha256Async(partialPath, model.Sha256, cancellationToken);

            if (model.IsArchive)
            {
                progress?.Report(new LocalSpeechModelDownloadProgress(
                    model.Id,
                    LocalSpeechModelDownloadStage.Extracting,
                    0,
                    0));

                await ExtractArchiveAsync(model, partialPath, finalPath, cancellationToken);
                File.Delete(partialPath);
            }
            else
            {
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(partialPath, finalPath);
            }

            progress?.Report(new LocalSpeechModelDownloadProgress(
                model.Id,
                LocalSpeechModelDownloadStage.Completed,
                1,
                1));
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("header", StringComparison.OrdinalIgnoreCase))
        {
            DeletePartialArtifacts(model);
            throw new InvalidOperationException(
                "The model server returned malformed HTTP headers. Try again in a moment or switch to another model source.",
                ex);
        }
        catch
        {
            DeletePartialArtifacts(model);
            throw;
        }
    }

    internal void DeleteModel(LocalSpeechModelDefinition model)
    {
        string finalPath = GetFinalPath(model);

        if (model.IsArchive)
        {
            if (Directory.Exists(finalPath))
            {
                Directory.Delete(finalPath, true);
            }
        }
        else if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }

        DeletePartialArtifacts(model);
    }

    private async Task VerifySha256Async(string filePath, string expectedSha256, CancellationToken cancellationToken)
    {
        string actual = await Task.Run(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }, cancellationToken);

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The downloaded model failed checksum verification.");
        }
    }

    private async Task ExtractArchiveAsync(
        LocalSpeechModelDefinition model,
        string archivePath,
        string finalPath,
        CancellationToken cancellationToken)
    {
        string tempDirectoryPath = GetExtractingPath(model);

        if (Directory.Exists(tempDirectoryPath))
        {
            Directory.Delete(tempDirectoryPath, true);
        }

        Directory.CreateDirectory(tempDirectoryPath);

        try
        {
            await ExtractWithNativeTarAsync(archivePath, tempDirectoryPath, cancellationToken);

            if (Directory.Exists(finalPath))
            {
                Directory.Delete(finalPath, true);
            }

            string extractedRootPath = ResolveExtractedRootPath(tempDirectoryPath);
            Directory.Move(extractedRootPath, finalPath);

            if (!string.Equals(extractedRootPath, tempDirectoryPath, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, true);
            }
        }
        catch
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, true);
            }

            throw;
        }
    }

    private static async Task ExtractWithNativeTarAsync(
        string archivePath,
        string destinationDirectoryPath,
        CancellationToken cancellationToken)
    {
        string tarPath = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tarPath))
        {
            throw new InvalidOperationException("Windows tar.exe was not found on this machine.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = tarPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-xzf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(destinationDirectoryPath);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
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

        string standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        string standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            string errorMessage = string.IsNullOrWhiteSpace(standardError)
                ? standardOutput
                : standardError;
            throw new InvalidOperationException(
                $"Archive extraction failed with tar.exe: {errorMessage.Trim()}");
        }
    }

    private string ResolveExtractedRootPath(string tempDirectoryPath)
    {
        string[] directories = Directory.GetDirectories(tempDirectoryPath, "*", SearchOption.TopDirectoryOnly);
        string[] files = Directory.GetFiles(tempDirectoryPath, "*", SearchOption.TopDirectoryOnly);

        if (directories.Length == 1 && files.Length == 0)
        {
            return directories[0];
        }

        return tempDirectoryPath;
    }

    private void DeletePartialArtifacts(LocalSpeechModelDefinition model)
    {
        string partialPath = GetPartialPath(model);
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        string extractingPath = GetExtractingPath(model);
        if (Directory.Exists(extractingPath))
        {
            Directory.Delete(extractingPath, true);
        }
    }

    private string GetFinalPath(LocalSpeechModelDefinition model)
    {
        return Path.Combine(_modelsDirectoryPath, model.StorageName);
    }

    private string GetPartialPath(LocalSpeechModelDefinition model)
    {
        return Path.Combine(_modelsDirectoryPath, model.StorageName + ".partial");
    }

    private string GetExtractingPath(LocalSpeechModelDefinition model)
    {
        return Path.Combine(_modelsDirectoryPath, model.StorageName + ".extracting");
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromHours(2);
        return client;
    }
}
