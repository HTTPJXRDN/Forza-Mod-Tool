using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FH6LocalCryptoTool;

internal sealed record ReleaseUpdateAsset(string Name, Uri Url, long Size, string Sha256);
internal sealed record ReleaseUpdateResult(bool UpdateAvailable, string Version, ReleaseUpdateAsset? Asset = null);

internal static class ReleaseUpdates
{
    internal const string LatestReleaseUrl = "https://github.com/HTTPJXRDN/Forza-Mod-Tool/releases/latest";
    internal const string ApiUrl = "https://api.github.com/repos/HTTPJXRDN/Forza-Mod-Tool/releases/latest";
    static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 1_048_576,
    };

    internal static Version InstalledVersion => Normalize(typeof(ReleaseUpdates).Assembly.GetName().Version ?? new Version(0, 0));
    internal static string InstalledVersionText => Display(InstalledVersion);

    internal static Task<ReleaseUpdateResult> CheckAsync() => CheckAsync(Client, InstalledVersion);

    internal static async Task<ReleaseUpdateResult> CheckAsync(HttpClient client, Version installed, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.UserAgent.ParseAdd("ForzaModTool/" + Display(Normalize(installed)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ReadRelease(document.RootElement, installed);
    }

    internal static ReleaseUpdateResult ReadRelease(JsonElement release, Version installed)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !TryVersion(tag.GetString(), out var latest))
            throw new InvalidDataException("GitHub did not return a supported published release version.");
        var version = Display(latest!);
        ReleaseUpdateAsset? package = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    name.GetString() != "FH6LocalModTool_v" + version + ".zip") continue;
                if (package != null) throw new InvalidDataException("The release contains duplicate app packages.");
                if (!asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out long bytes) || bytes is < 1 or > 536_870_912 ||
                    !asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String ||
                    !Regex.IsMatch(digest.GetString() ?? "", @"^sha256:[0-9a-fA-F]{64}$") ||
                    !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                    !uri.AbsolutePath.StartsWith("/HTTPJXRDN/Forza-Mod-Tool/releases/download/", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(uri.AbsolutePath) != name.GetString()) continue;
                package = new(name.GetString()!, uri, bytes, digest.GetString()![7..].ToUpperInvariant());
            }
        }
        return new(latest! > Normalize(installed), version, package);
    }

    internal static bool TryVersion(string? tag, out Version? version)
    {
        version = null;
        if (tag is null || tag.Length > 160) return false;
        var match = Regex.Match(tag.Trim(), @"^(?:Forza[-_ ]?Mod[-_ ]?Tool[-_ ]*)?v?(?<version>\d+\.\d+(?:\.\d+){0,2})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    static string Display(Version version) => version.ToString(version.Revision > 0 ? 4 : 3);
}
