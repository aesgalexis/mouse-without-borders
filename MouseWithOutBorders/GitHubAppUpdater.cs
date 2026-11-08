using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MouseWithOutBorders;

internal sealed record AppRelease(Version Version, string Tag, string InstallerName,
    Uri InstallerUrl, Uri ChecksumUrl, long InstallerSize, long InstallerAssetId = 0, long ChecksumAssetId = 0);

internal interface IAppUpdater
{
    Version CurrentVersion { get; }
    Task<AppRelease?> CheckAsync(CancellationToken cancellationToken);
    Task InstallAsync(AppRelease release, IProgress<int> progress, CancellationToken cancellationToken);
}

internal sealed class GitHubAppUpdater : IAppUpdater
{
    internal const string Repository = "aesgalexis/mouse-without-borders";
    private const long MaximumInstallerSize = 512L * 1024 * 1024;
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly HttpClient _client;
    private readonly Action<ProcessStartInfo> _startInstaller;
    private readonly string _downloadRoot;

    public Version CurrentVersion { get; }

    internal GitHubAppUpdater(HttpClient? client = null, Version? currentVersion = null,
        Action<ProcessStartInfo>? startInstaller = null, string? downloadRoot = null)
    {
        _client = client ?? SharedClient;
        Version version = currentVersion ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        CurrentVersion = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        _startInstaller = startInstaller ?? (info =>
        {
            using Process process = Process.Start(info)
                ?? throw new IOException("Windows could not start the update installer.");
        });
        _downloadRoot = downloadRoot ?? Path.GetTempPath();
    }

    public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        ReleaseMetadata? newest = null;
        Version newestVersion = CurrentVersion;
        // Include previews as well as the release marked latest on GitHub.
        for (int page = 1; ; page++)
        {
            using HttpResponseMessage response = await GetAsync(new Uri(
                $"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}"), cancellationToken);
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            ReleaseMetadata[] releases = await JsonSerializer.DeserializeAsync<ReleaseMetadata[]>(stream,
                cancellationToken: cancellationToken) ?? throw new InvalidDataException("GitHub returned an invalid release list.");
            foreach (ReleaseMetadata release in releases)
            {
                if (release.Draft || !Regex.IsMatch(release.Tag, @"^v?\d+\.\d+\.\d+$") ||
                    !Version.TryParse(release.Tag.TrimStart('v'), out Version? version) || version <= newestVersion)
                    continue;
                newest = release;
                newestVersion = version;
            }
            if (releases.Length < 100) break;
        }
        if (newest is null) return null;

        string installerName = $"MouseWithOutBorders-{newestVersion}-Setup.exe";
        AssetMetadata installer = RequireAsset(newest, installerName);
        AssetMetadata checksum = RequireAsset(newest, "SHA256SUMS.txt");
        if (installer.Size <= 0 || installer.Size > MaximumInstallerSize)
            throw new InvalidDataException("The latest release has an invalid installer size.");
        return new AppRelease(newestVersion, newest.Tag, installerName,
            AssetUrl(newest.Tag, installer), AssetUrl(newest.Tag, checksum), installer.Size, installer.Id, checksum.Id);
    }

    public async Task InstallAsync(AppRelease release, IProgress<int> progress, CancellationToken cancellationToken)
    {
        using HttpResponseMessage manifestResponse = await GetAssetAsync(release.ChecksumUrl, release.ChecksumAssetId, cancellationToken);
        if (manifestResponse.Content.Headers.ContentLength > 1024 * 1024)
            throw new InvalidDataException("The release checksum manifest is too large.");
        string manifest = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
        Match[] entries = Regex.Matches(manifest.TrimStart('\uFEFF'),
            @"^([a-fA-F0-9]{64})[ \t]+\*?" + Regex.Escape(release.InstallerName) + @"\r?$",
            RegexOptions.Multiline).Cast<Match>().ToArray();
        if (entries.Length != 1)
            throw new InvalidDataException("The installer is missing or duplicated in SHA256SUMS.txt. Update cancelled.");
        byte[] expectedHash = Convert.FromHexString(entries[0].Groups[1].Value);

        string folder = Path.Combine(_downloadRoot, "MouseWithOutBorders-Update-" + Guid.NewGuid().ToString("N"));
        string installerPath = Path.Combine(folder, release.InstallerName);
        Directory.CreateDirectory(folder);
        bool started = false;
        try
        {
            using HttpResponseMessage response = await GetAssetAsync(release.InstallerUrl, release.InstallerAssetId, cancellationToken);
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value != release.InstallerSize)
                throw new InvalidDataException("The installer download size does not match the release.");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream output = new(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long downloaded = 0;
                int lastPercent = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    downloaded += count;
                    if (downloaded > release.InstallerSize || downloaded > MaximumInstallerSize)
                        throw new InvalidDataException("The installer download is larger than expected.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    int percent = (int)(downloaded * 100 / release.InstallerSize);
                    if (percent != lastPercent) { progress.Report(percent); lastPercent = percent; }
                }
                if (downloaded != release.InstallerSize)
                    throw new InvalidDataException("The installer download was incomplete. Try again.");
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash))
                throw new InvalidDataException("SHA-256 verification failed. Update cancelled.");
            cancellationToken.ThrowIfCancellationRequested();
            _startInstaller(CreateInstallerStartInfo(installerPath));
            started = true;
        }
        finally
        {
            // A running installer must retain its files. Failed/cancelled downloads are discarded.
            if (!started)
            {
                try { File.Delete(installerPath); Directory.Delete(folder); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string installerPath) => new()
    {
        FileName = installerPath,
        Arguments = "/SILENT /SP- /NORESTART /MWBUPDATE=1",
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(installerPath)!
    };

    private async Task<HttpResponseMessage> GetAssetAsync(Uri url, long assetId, CancellationToken cancellationToken)
    {
        try { return await GetAsync(url, cancellationToken); }
        catch (HttpRequestException ex) when (assetId > 0 && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
        {
            // GitHub serves the same uploaded asset through its API when the public download route fails.
            Uri apiUrl = new($"https://api.github.com/repos/{Repository}/releases/assets/{assetId}?download=1");
            return await GetAsync(apiUrl, cancellationToken, binary: true);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken, bool binary = false)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"MouseWithOutBorders/{CurrentVersion}");
        if (uri.Host == "api.github.com")
        {
            request.Headers.Accept.ParseAdd(binary ? "application/octet-stream" : "application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        HttpStatusCode status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException(status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? "GitHub is limiting update requests. Try again later."
            : $"GitHub could not provide the update (HTTP {(int)status}). Try again later.", null, status);
    }

    private static AssetMetadata RequireAsset(ReleaseMetadata release, string name) =>
        release.Assets.FirstOrDefault(asset => asset.Name == name && asset.State == "uploaded")
        ?? throw new InvalidDataException($"The latest release ({release.Tag}) is not ready: {name} is missing. Try again later.");

    private static Uri AssetUrl(string tag, AssetMetadata asset)
    {
        Uri expected = new($"https://github.com/{Repository}/releases/download/{tag}/{asset.Name}");
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? actual) || actual != expected)
            throw new InvalidDataException("The release contains an unexpected download URL. Update cancelled.");
        return actual;
    }

    private sealed class ReleaseMetadata
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public AssetMetadata[] Assets { get; set; } = [];
    }

    private sealed class AssetMetadata
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    }
}
