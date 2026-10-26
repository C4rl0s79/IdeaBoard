using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace IdeaBoard;

public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string Url, string ZipUrl, string ShaUrl);

/// <summary>
/// Self-update from GitHub Releases. A release carries IdeaBoard-X.Y.Z-win-x64.zip and a .sha256 file.
/// Applying swaps the files next to the exe (never appsettings.json or data\) and then exits with an
/// error code; the service recovery actions set by install-service.ps1 start the new version.
/// </summary>
public sealed class Updater(string baseDir, string dataDir, string repo, ILogger<Updater> log)
{
    public static readonly Version Current = ParseVersion(
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    static readonly HttpClient Http = CreateHttp();
    static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    readonly SemaphoreSlim _applying = new(1, 1);
    ReleaseInfo? _latest;
    DateTime _checkedAt = DateTime.MinValue;

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"IdeaBoard/{Current}");
        return http;
    }

    static Version? ParseVersion(string? s) =>
        Version.TryParse((s ?? "").Split('+')[0].TrimStart('v'), out var v) ? v : null;

    public bool IsService => WindowsServiceHelpers.IsWindowsService();

    public async Task<ReleaseInfo?> LatestAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _checkedAt < CacheFor) return _latest;

        using var res = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            (_latest, _checkedAt) = (null, DateTime.UtcNow);
            return null;
        }
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        string? zip = null, sha = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) zip = url;
            else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }

        _latest = ParseVersion(tag) is { } version && zip is not null && sha is not null
            ? new ReleaseInfo(version, tag, root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.GetProperty("html_url").GetString() ?? "", zip, sha)
            : null;
        _checkedAt = DateTime.UtcNow;
        return _latest;
    }

    /// <summary>Why an update can't be installed from the app, or null when it can.</summary>
    public string? BlockedReason()
    {
        if (!IsService)
            return "Aktualizacja z aplikacji działa, gdy IdeaBoard pracuje jako usługa. Użyj update.ps1.";
        try
        {
            var probe = Path.Combine(baseDir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Usługa nie ma prawa zapisu w katalogu aplikacji. Raz uruchom update.ps1 (albo install-service.ps1) jako administrator.";
        }
    }

    public async Task<ReleaseInfo> ApplyAsync()
    {
        if (!await _applying.WaitAsync(0)) throw new BoardException("Aktualizacja już trwa.", 409);
        try
        {
            var release = await LatestAsync(force: true) ?? throw new BoardException("Nie znaleziono wydania na GitHubie.", 404);
            if (release.Version <= Current) throw new BoardException("Masz już najnowszą wersję.", 409);
            if (BlockedReason() is { } reason) throw new BoardException(reason, 409);

            var work = Path.Combine(dataDir, "update");
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);

            var zipPath = Path.Combine(work, "release.zip");
            await using (var file = File.Create(zipPath))
            await using (var stream = await Http.GetStreamAsync(release.ZipUrl))
                await stream.CopyToAsync(file);

            var expected = (await Http.GetStringAsync(release.ShaUrl)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            string actual;
            await using (var file = File.OpenRead(zipPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new BoardException("Suma kontrolna pobranej paczki się nie zgadza — aktualizacja przerwana.", 502);

            var staging = Path.Combine(work, "staging");
            ZipFile.ExtractToDirectory(zipPath, staging);
            var exeName = Path.GetFileName(Environment.ProcessPath) ?? "IdeaBoard.exe";
            if (!File.Exists(Path.Combine(staging, exeName)))
                throw new BoardException($"Paczka nie zawiera {exeName} — aktualizacja przerwana.", 502);

            // Everything else first; the running exe last, renamed aside so it can be restored.
            foreach (var src in Directory.GetFiles(staging))
            {
                var name = Path.GetFileName(src);
                if (name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) || name.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(src, Path.Combine(baseDir, name), overwrite: true);
            }
            var exe = Path.Combine(baseDir, exeName);
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Copy(Path.Combine(staging, exeName), exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }

            log.LogWarning("Updated files to {Version}; exiting so the service restarts", release.Version);
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500); // let the HTTP response go out
                Environment.Exit(3);
            });
            return release;
        }
        finally
        {
            _applying.Release();
        }
    }

    /// <summary>Runs once the new version is up: the old exe and the download are no longer needed.</summary>
    public void CleanupAfterStart()
    {
        try
        {
            foreach (var old in Directory.GetFiles(baseDir, "*.old")) File.Delete(old);
            var work = Path.Combine(dataDir, "update");
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
        catch (Exception e)
        {
            log.LogInformation("Update cleanup skipped: {Message}", e.Message);
        }
    }
}
