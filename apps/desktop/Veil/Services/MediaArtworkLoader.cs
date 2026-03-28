using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Veil.Services;

internal static class MediaArtworkLoader
{
    private const uint PreferredArtworkSize = 600;
    private static readonly global::Windows.UI.Color DefaultArtworkColor =
        global::Windows.UI.Color.FromArgb(255, 24, 24, 30);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> RemoteArtworkTasks = new(StringComparer.Ordinal);

    internal static async Task<ImageSource?> LoadAsync(
        IRandomAccessStreamReference? thumbnailRef,
        string? sourceAppId,
        string? trackTitle,
        string? trackArtist,
        string? albumTitle,
        string? subtitle)
    {
        return (await LoadVisualAsync(
            thumbnailRef,
            sourceAppId,
            trackTitle,
            trackArtist,
            albumTitle,
            subtitle)).Source;
    }

    internal static async Task<MediaArtworkVisual> LoadVisualAsync(
        IRandomAccessStreamReference? thumbnailRef,
        string? sourceAppId,
        string? trackTitle,
        string? trackArtist,
        string? albumTitle,
        string? subtitle)
    {
        var localArtwork = await TryLoadAsync(thumbnailRef);
        if (localArtwork.MaxDimension >= PreferredArtworkSize)
        {
            return new MediaArtworkVisual(localArtwork.Source, localArtwork.AverageColor);
        }

        if (IsLikelyYouTubeSource(sourceAppId, subtitle))
        {
            return new MediaArtworkVisual(localArtwork.Source, localArtwork.AverageColor);
        }

        byte[]? remoteBytes = await TryGetRemoteArtworkBytesAsync(sourceAppId, trackTitle, trackArtist, albumTitle);
        if (remoteBytes is null)
        {
            return new MediaArtworkVisual(localArtwork.Source, localArtwork.AverageColor);
        }

        var remoteArtwork = await TryLoadAsync(remoteBytes);
        if (remoteArtwork.Source is not null && remoteArtwork.MaxDimension > localArtwork.MaxDimension)
        {
            return new MediaArtworkVisual(remoteArtwork.Source, remoteArtwork.AverageColor);
        }

        return new MediaArtworkVisual(localArtwork.Source, localArtwork.AverageColor);
    }

    private static async Task<ArtworkResult> TryLoadAsync(IRandomAccessStreamReference? thumbnailRef)
    {
        if (thumbnailRef is null)
        {
            return EmptyResult();
        }

        try
        {
            using var stream = await thumbnailRef.OpenReadAsync();
            return await TryLoadAsync(stream);
        }
        catch
        {
            return EmptyResult();
        }
    }

    private static async Task<ArtworkResult> TryLoadAsync(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return EmptyResult();
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        return await TryLoadAsync(stream);
    }

    private static async Task<ArtworkResult> TryLoadAsync(IRandomAccessStream stream)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            byte[] pixels = pixelData.DetachPixelData();
            uint width = decoder.OrientedPixelWidth;
            uint height = decoder.OrientedPixelHeight;
            if (pixels.Length == 0 || width == 0 || height == 0)
            {
                return EmptyResult();
            }

            var bitmap = new WriteableBitmap((int)width, (int)height);
            pixels.CopyTo(bitmap.PixelBuffer);
            return new ArtworkResult(bitmap, Math.Max(width, height), ComputeAverageColor(pixels));
        }
        catch
        {
            return EmptyResult();
        }
    }

    private static Task<byte[]?> TryGetRemoteArtworkBytesAsync(
        string? sourceAppId,
        string? trackTitle,
        string? trackArtist,
        string? albumTitle)
    {
        string? cacheKey = BuildCacheKey(trackTitle, trackArtist, albumTitle);
        if (cacheKey is null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return RemoteArtworkTasks.GetOrAdd(
            cacheKey,
            _ => FetchRemoteArtworkBytesAsync(sourceAppId, trackTitle!, trackArtist!, albumTitle));
    }

    private static async Task<byte[]?> FetchRemoteArtworkBytesAsync(
        string? sourceAppId,
        string trackTitle,
        string trackArtist,
        string? albumTitle)
    {
        string? remoteUrl = await FindArtworkUrlAsync(sourceAppId, trackTitle, trackArtist, albumTitle);
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        try
        {
            return await HttpClient.GetByteArrayAsync(remoteUrl);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> FindArtworkUrlAsync(
        string? sourceAppId,
        string trackTitle,
        string trackArtist,
        string? albumTitle)
    {
        bool deezerFirst = sourceAppId?.Contains("deezer", StringComparison.OrdinalIgnoreCase) == true;
        if (deezerFirst)
        {
            return await TryFindOnDeezerAsync(trackTitle, trackArtist, albumTitle)
                ?? await TryFindOnItunesAsync(trackTitle, trackArtist, albumTitle);
        }

        return await TryFindOnItunesAsync(trackTitle, trackArtist, albumTitle)
            ?? await TryFindOnDeezerAsync(trackTitle, trackArtist, albumTitle);
    }

    private static async Task<string?> TryFindOnItunesAsync(
        string trackTitle,
        string trackArtist,
        string? albumTitle)
    {
        string term = Uri.EscapeDataString($"{trackArtist} {trackTitle} {albumTitle}".Trim());
        string url = $"https://itunes.apple.com/search?media=music&entity=song&limit=10&term={term}";

        try
        {
            using var stream = await HttpClient.GetStreamAsync(url);
            using var document = await JsonDocument.ParseAsync(stream);
            if (!document.RootElement.TryGetProperty("results", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement result in results.EnumerateArray())
            {
                string? candidateTitle = GetString(result, "trackName");
                string? candidateArtist = GetString(result, "artistName");
                string? candidateAlbum = GetString(result, "collectionName");
                if (!IsMatch(trackTitle, trackArtist, albumTitle, candidateTitle, candidateArtist, candidateAlbum))
                {
                    continue;
                }

                string? artworkUrl = GetString(result, "artworkUrl100");
                if (!string.IsNullOrWhiteSpace(artworkUrl))
                {
                    return UpgradeItunesArtworkUrl(artworkUrl);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static async Task<string?> TryFindOnDeezerAsync(
        string trackTitle,
        string trackArtist,
        string? albumTitle)
    {
        string query = Uri.EscapeDataString($"artist:\"{trackArtist}\" track:\"{trackTitle}\"");
        string url = $"https://api.deezer.com/search?q={query}&limit=10";

        try
        {
            using var stream = await HttpClient.GetStreamAsync(url);
            using var document = await JsonDocument.ParseAsync(stream);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement result in data.EnumerateArray())
            {
                string? candidateTitle = GetString(result, "title");
                string? candidateArtist = result.TryGetProperty("artist", out JsonElement artist)
                    ? GetString(artist, "name")
                    : null;
                string? candidateAlbum = result.TryGetProperty("album", out JsonElement album)
                    ? GetString(album, "title")
                    : null;
                if (!IsMatch(trackTitle, trackArtist, albumTitle, candidateTitle, candidateArtist, candidateAlbum))
                {
                    continue;
                }

                if (result.TryGetProperty("album", out album))
                {
                    string? artworkUrl =
                        GetString(album, "cover_xl") ??
                        GetString(album, "cover_big") ??
                        GetString(album, "cover_medium");
                    if (!string.IsNullOrWhiteSpace(artworkUrl))
                    {
                        return artworkUrl;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsMatch(
        string trackTitle,
        string trackArtist,
        string? albumTitle,
        string? candidateTitle,
        string? candidateArtist,
        string? candidateAlbum)
    {
        string normalizedTrackTitle = Normalize(trackTitle);
        string normalizedTrackArtist = Normalize(trackArtist);
        string normalizedCandidateTitle = Normalize(candidateTitle);
        string normalizedCandidateArtist = Normalize(candidateArtist);
        if (string.IsNullOrEmpty(normalizedTrackTitle) ||
            string.IsNullOrEmpty(normalizedTrackArtist) ||
            string.IsNullOrEmpty(normalizedCandidateTitle) ||
            string.IsNullOrEmpty(normalizedCandidateArtist))
        {
            return false;
        }

        bool titleMatches = normalizedCandidateTitle.Contains(normalizedTrackTitle, StringComparison.Ordinal) ||
            normalizedTrackTitle.Contains(normalizedCandidateTitle, StringComparison.Ordinal);
        bool artistMatches = normalizedCandidateArtist.Contains(normalizedTrackArtist, StringComparison.Ordinal) ||
            normalizedTrackArtist.Contains(normalizedCandidateArtist, StringComparison.Ordinal);
        if (!titleMatches || !artistMatches)
        {
            return false;
        }

        string normalizedAlbumTitle = Normalize(albumTitle);
        string normalizedCandidateAlbum = Normalize(candidateAlbum);
        if (string.IsNullOrEmpty(normalizedAlbumTitle) || string.IsNullOrEmpty(normalizedCandidateAlbum))
        {
            return true;
        }

        return normalizedCandidateAlbum.Contains(normalizedAlbumTitle, StringComparison.Ordinal) ||
            normalizedAlbumTitle.Contains(normalizedCandidateAlbum, StringComparison.Ordinal);
    }

    private static string? BuildCacheKey(string? trackTitle, string? trackArtist, string? albumTitle)
    {
        string normalizedTrackTitle = Normalize(trackTitle);
        string normalizedTrackArtist = Normalize(trackArtist);
        if (string.IsNullOrEmpty(normalizedTrackTitle) || string.IsNullOrEmpty(normalizedTrackArtist))
        {
            return null;
        }

        return $"{normalizedTrackArtist}|{normalizedTrackTitle}|{Normalize(albumTitle)}";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static string UpgradeItunesArtworkUrl(string url)
    {
        int slashIndex = url.LastIndexOf('/');
        if (slashIndex < 0)
        {
            return url;
        }

        int extensionIndex = url.IndexOf('.', slashIndex);
        if (extensionIndex < 0)
        {
            return url;
        }

        return $"{url[..(slashIndex + 1)]}1200x1200bb{url[extensionIndex..]}";
    }

    private static bool IsLikelyYouTubeSource(string? sourceAppId, string? subtitle)
    {
        if (!string.IsNullOrWhiteSpace(sourceAppId) &&
            sourceAppId.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(subtitle) &&
            subtitle.Contains("youtube", StringComparison.OrdinalIgnoreCase);
    }

    private static global::Windows.UI.Color ComputeAverageColor(byte[] pixels)
    {
        long blue = 0;
        long green = 0;
        long red = 0;
        long alpha = 0;
        int stride = Math.Max(4, pixels.Length / 4096);
        stride -= stride % 4;
        if (stride == 0)
        {
            stride = 4;
        }

        for (int index = 0; index <= pixels.Length - 4; index += stride)
        {
            byte pixelAlpha = pixels[index + 3];
            if (pixelAlpha == 0)
            {
                continue;
            }

            blue += pixels[index] * pixelAlpha;
            green += pixels[index + 1] * pixelAlpha;
            red += pixels[index + 2] * pixelAlpha;
            alpha += pixelAlpha;
        }

        if (alpha == 0)
        {
            return DefaultArtworkColor;
        }

        byte avgRed = (byte)Math.Clamp(red / alpha, 0, 255);
        byte avgGreen = (byte)Math.Clamp(green / alpha, 0, 255);
        byte avgBlue = (byte)Math.Clamp(blue / alpha, 0, 255);
        return global::Windows.UI.Color.FromArgb(255, avgRed, avgGreen, avgBlue);
    }

    private static ArtworkResult EmptyResult()
    {
        return new ArtworkResult(null, 0, DefaultArtworkColor);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(4);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Veil/1.0");
        return client;
    }

    internal readonly record struct MediaArtworkVisual(ImageSource? Source, global::Windows.UI.Color AverageColor);

    private readonly record struct ArtworkResult(ImageSource? Source, uint MaxDimension, global::Windows.UI.Color AverageColor);
}
