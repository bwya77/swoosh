using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Swoosh.Updates;

/// <summary>
/// Checks GitHub Releases for a newer build than the one currently running.
/// Uses the public REST API (no auth, 60 req/hour/IP is ample for a startup check).
/// All failures are swallowed so a missing network never disrupts the app.
/// </summary>
public sealed class UpdateChecker
{
    private const string Owner = "bwya77";
    private const string Repo = "swoosh";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public sealed record UpdateInfo(Version Latest, string Tag, string HtmlUrl);

    /// <summary>The running build's version, normalized to Major.Minor.Build.</summary>
    public Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return Normalize(v);
        }
    }

    /// <summary>
    /// Returns details of a newer release if one exists, otherwise null
    /// (already up to date, or the check could not be completed).
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Swoosh-UpdateChecker");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(htmlUrl))
                htmlUrl = $"https://github.com/{Owner}/{Repo}/releases/latest";

            var latest = ParseVersion(tag);
            if (latest == null) return null;

            return latest > CurrentVersion ? new UpdateInfo(latest, tag, htmlUrl) : null;
        }
        catch
        {
            return null; // network / parse / cancellation: treat as "no update info"
        }
    }

    /// <summary>Parse a release tag like "v0.1.5" into a Major.Minor.Build version.</summary>
    internal static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.Trim();

        int start = 0;
        while (start < s.Length && !char.IsDigit(s[start])) start++; // skip leading "v"
        s = s[start..];

        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end];
        if (s.Length == 0) return null;
        if (!s.Contains('.')) s += ".0"; // Version requires at least Major.Minor

        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
