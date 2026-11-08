using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MouseWithOutBorders;

internal static class Program
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("A test installer; never executed.");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Installer));
    private const string InstallerName = "MouseWithOutBorders-0.1.4-Setup.exe";
    private const string AssetBase = "https://github.com/aesgalexis/mouse-without-borders/releases/download/";
    private static readonly AppRelease Update = new(new Version(0, 1, 4), "v0.1.4", InstallerName,
        new Uri(AssetBase + "v0.1.4/" + InstallerName), new Uri(AssetBase + "v0.1.4/SHA256SUMS.txt"), Installer.Length);

    private static async Task Main(string[] args)
    {
        if (args.Contains("--live"))
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
            string root = CreateRoot();
            try
            {
                GitHubAppUpdater live = new(currentVersion: new Version(0, 0, 0), downloadRoot: root,
                    startInstaller: info => Console.WriteLine($"Verified real GitHub installer: {Path.GetFileName(info.FileName)}. Launch suppressed."));
                AppRelease? latest = await live.CheckAsync(timeout.Token);
                Console.WriteLine(latest is null ? "No published releases."
                    : $"Available: {latest.Version}; installer: {latest.InstallerName}. No files installed.");
                if (latest is not null && args.Contains("--download"))
                    await live.InstallAsync(latest, new ImmediateProgress(_ => { }), timeout.Token);
            }
            finally { Directory.Delete(root, recursive: true); }
            return;
        }
        await TestReleaseSelection();
        await TestPagination();
        await TestInvalidReleases();
        await TestSuccessfulInstall();
        await TestGatewayTimeoutFallback();
        await TestFailedDownloads();
        await TestCancellation();
        Console.WriteLine("All updater smoke tests passed. No installers executed.");
    }

    private static object Release(string version, bool draft = false, bool prerelease = true,
        bool missingHash = false, string? installerUrl = null, bool missingInstaller = false)
    {
        string tag = "v" + version;
        string name = $"MouseWithOutBorders-{version}-Setup.exe";
        List<object> assets = [];
        if (!missingInstaller) assets.Add(new { id = 100, name, state = "uploaded", size = Installer.Length,
            browser_download_url = installerUrl ?? AssetBase + tag + "/" + name });
        if (!missingHash) assets.Add(new { id = 101, name = "SHA256SUMS.txt", state = "uploaded", size = 150,
            browser_download_url = AssetBase + tag + "/SHA256SUMS.txt" });
        return new { tag_name = tag, draft, prerelease, assets };
    }

    private static async Task TestReleaseSelection()
    {
        using HttpClient client = Client(_ => Json(new[] { Release("0.1.4"), Release("9.0.0", draft: true),
            Release("0.1.12"), Release("0.1.5", prerelease: false), Release("0.1.99-beta") }));
        AppRelease? selected = await new GitHubAppUpdater(client, new Version(0, 1, 3, 0)).CheckAsync(default);
        Assert(selected?.Version == new Version(0, 1, 12), "Choose the highest numeric release, including previews, without drafts.");
        using HttpClient older = Client(_ => Json(new[] { Release("0.1.3"), Release("0.1.2") }));
        Assert(await new GitHubAppUpdater(older, new Version(0, 1, 3)).CheckAsync(default) is null,
            "Equal/older releases must not cause a reinstall or downgrade.");
        using HttpClient empty = Client(_ => Json(Array.Empty<object>()));
        Assert(await new GitHubAppUpdater(empty).CheckAsync(default) is null, "No published releases is a valid result.");
        Console.WriteLine("PASS: numeric versions, previews, drafts, unknown tags, equal/older versions and empty releases.");
    }

    private static async Task TestPagination()
    {
        int requests = 0;
        using HttpClient client = Client(request =>
        {
            Assert(request.Headers.UserAgent.Count != 0 && request.Headers.Accept.Any(x => x.MediaType == "application/vnd.github+json"),
                "GitHub API headers must be supplied.");
            requests++;
            return request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
                ? Json(new[] { Release("0.1.4") }) : Json(Enumerable.Repeat(Release("0.1.2"), 100));
        });
        AppRelease? release = await new GitHubAppUpdater(client, new Version(0, 1, 3)).CheckAsync(default);
        Assert(release?.Version == Update.Version && requests == 2, "An update on a later page must be found.");
        Console.WriteLine("PASS: paginated release lookup and required GitHub headers.");
    }

    private static async Task TestInvalidReleases()
    {
        foreach (object release in new[] { Release("0.1.4", missingHash: true),
            Release("0.1.4", missingInstaller: true), Release("0.1.4", installerUrl: "https://example.com/installer.exe") })
        {
            using HttpClient client = Client(_ => Json(new[] { Release("0.1.3"), release }));
            await ExpectFailure<InvalidDataException>(() => new GitHubAppUpdater(client, new Version(0, 1, 2)).CheckAsync(default));
        }
        foreach (HttpStatusCode code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound })
        {
            using HttpClient client = Client(_ => new HttpResponseMessage(code));
            await ExpectFailure<HttpRequestException>(() => new GitHubAppUpdater(client).CheckAsync(default));
        }
        Console.WriteLine("PASS: incomplete latest releases, foreign download URLs, HTTP errors and rate limits are rejected.");
    }

    private static async Task TestSuccessfulInstall()
    {
        string root = CreateRoot();
        try
        {
            ProcessStartInfo? launched = null;
            List<int> progress = [];
            using HttpClient client = Downloads($"\uFEFF{Hash.ToLowerInvariant()} *{InstallerName}\r\n{new string('a', 64)} *Portable.zip\r\n");
            GitHubAppUpdater updater = new(client, new Version(0, 1, 3), info =>
            {
                launched = info;
                Assert(File.ReadAllBytes(info.FileName).SequenceEqual(Installer), "Only the verified installer may be launched.");
                using FileStream exclusive = File.Open(info.FileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }, root);
            await updater.InstallAsync(Update, new ImmediateProgress(progress.Add), default);
            Assert(launched is not null && launched.UseShellExecute && launched.Arguments.Contains("/NORESTART") &&
                launched.Arguments.Contains("/MWBUPDATE=1") && launched.Arguments.Contains("/SILENT"),
                "Launch the local installer with visible progress, app restart and no Windows restart.");
            Assert(progress.Last() == 100 && File.Exists(launched!.FileName), "Report download progress and retain the running installer's files.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS: BOM/CRLF SHA-256 manifest, verified file, progress and installer launch/restart flags.");
    }

    private static async Task TestFailedDownloads()
    {
        string root = CreateRoot();
        try
        {
            foreach ((string manifest, byte[] data) in new[] {
                ($"{new string('0', 64)} *{InstallerName}\n", Installer),
                ($"{Hash} *Other.exe\n", Installer),
                ($"{Hash} *{InstallerName}\n{Hash} *{InstallerName}\n", Installer),
                ($"{Hash} *{InstallerName}\n", Installer[..^1]),
                ($"{Hash} *{InstallerName}\n", Installer.Concat(new byte[] { 0 }).ToArray()) })
            {
                using HttpClient client = Downloads(manifest, data);
                GitHubAppUpdater updater = new(client, startInstaller: _ => throw new Exception("Unverified installer was launched!"), downloadRoot: root);
                await ExpectFailure<InvalidDataException>(() => updater.InstallAsync(Update, new ImmediateProgress(_ => { }), default));
                Assert(!Directory.EnumerateFileSystemEntries(root).Any(), "Failed downloads must be removed.");
            }
            using HttpClient launchClient = Downloads($"{Hash} {InstallerName}\n");
            await ExpectFailure<System.ComponentModel.Win32Exception>(() =>
                new GitHubAppUpdater(launchClient, startInstaller: _ => throw new System.ComponentModel.Win32Exception(5), downloadRoot: root)
                    .InstallAsync(Update, new ImmediateProgress(_ => { }), default));
            Assert(!Directory.EnumerateFileSystemEntries(root).Any(), "A rejected Windows launch must discard its download.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS: wrong/missing/duplicate hashes, truncated/oversized downloads and launch failures never install; files cleaned up.");
    }

    private static async Task TestGatewayTimeoutFallback()
    {
        string root = CreateRoot();
        try
        {
            int failedDirectRequests = 0;
            int launched = 0;
            using HttpClient client = Client(request =>
            {
                Uri uri = request.RequestUri!;
                if (uri.Host == "github.com")
                {
                    failedDirectRequests++;
                    return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
                }
                if (uri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
                    return Json(new[] { Release("0.1.4") });
                Assert(uri.Host == "api.github.com" && uri.AbsolutePath.StartsWith(
                    $"/repos/{GitHubAppUpdater.Repository}/releases/assets/", StringComparison.Ordinal) &&
                    request.Headers.Accept.Any(value => value.MediaType == "application/octet-stream"),
                    "The fallback must request bytes from the asset's repository API.");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = uri.Segments[^1] switch
                    {
                        "100" => new ByteArrayContent(Installer),
                        "101" => new StringContent($"{Hash} *{InstallerName}\n"),
                        _ => throw new Exception("Wrong asset ID in fallback request.")
                    }
                };
            });
            GitHubAppUpdater updater = new(client, new Version(0, 1, 3), info =>
            {
                Assert(File.ReadAllBytes(info.FileName).SequenceEqual(Installer), "API bytes must pass installer verification.");
                launched++;
            }, root);
            AppRelease release = await updater.CheckAsync(default) ?? throw new Exception("Update missing.");
            await updater.InstallAsync(release, new ImmediateProgress(_ => { }), default);
            Assert(failedDirectRequests == 2 && launched == 1, "A 504 on either release asset must fall back to the API.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS: both release assets recover from 504 through the GitHub API with SHA-256 verification.");
    }

    private static async Task TestCancellation()
    {
        string root = CreateRoot();
        try
        {
            using CancellationTokenSource cancellation = new();
            using HttpClient client = Downloads($"{Hash} *{InstallerName}\n");
            GitHubAppUpdater updater = new(client, startInstaller: _ => throw new Exception("Cancelled installer was launched!"), downloadRoot: root);
            await ExpectFailure<OperationCanceledException>(() => updater.InstallAsync(Update,
                new ImmediateProgress(_ => cancellation.Cancel()), cancellation.Token));
            Assert(!Directory.EnumerateFileSystemEntries(root).Any(), "Cancelled downloads must be removed.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS: cancellation during download prevents launch and removes temporary files.");
    }

    private static string CreateRoot() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "MwbUpdaterTests-" + Guid.NewGuid().ToString("N"))).FullName;
    private static HttpClient Downloads(string manifest, byte[]? data = null) => Client(request =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? new StringContent(manifest) : new ByteArrayContent(data ?? Installer) });
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) => new(new FakeHandler(handler));
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ExpectFailure<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }
    private sealed class ImmediateProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
