using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Aerisflow.Installer;

public sealed record ReleaseInfo(string Version, string Tag, string Notes, string Url);

public static class ReleaseFeed
{
    private const string Owner = "RealBibus";
    private const string Repo = "Aerisflow";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aerisflow-Setup");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<ReleaseInfo?> ForVersionAsync(string version, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(version)) return await LatestAsync(token);
        string clean = version.Trim().TrimStart('v', 'V');
        var tagged = await GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/v{clean}", token);
        if (tagged != null) return tagged;
        tagged = await GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/{clean}", token);
        if (tagged != null) return tagged;
        return await LatestAsync(token);
    }

    public static async Task<ReleaseInfo?> LatestAsync(CancellationToken token = default)
    => await GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", token);

    private static async Task<ReleaseInfo?> GetAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(url, token);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;

            string tag = Read(root, "tag_name");
            string body = Read(root, "body");
            string link = Read(root, "html_url");
            string name = Read(root, "name");

            string version = tag.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(version)) version = name.TrimStart('v', 'V');

            return new ReleaseInfo(version, tag, Plain(body), link);
        }
        catch
        {
            return null;
        }
    }

    private static string Read(JsonElement element, string name)
    => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
    ? value.GetString() ?? ""
    : "";

    public static string Plain(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        string text = markdown.Replace("\r", "");
        text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"^[ \t]*#{1,6}[ \t]*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[ \t]*[-*+][ \t]+", "• ", RegexOptions.Multiline);
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}