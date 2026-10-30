using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DosboxEnhanced;

// Pure release precedence plus a bounded, cancellable, metadata-only request.
internal sealed record ReleaseVersion(Version Number, string Preview) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion? Parse(string text)
    {
        if (text.Length > 100) return null;
        var match = Regex.Match(text, @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:-([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*))?$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int major) ||
            !int.TryParse(match.Groups[2].Value, out int minor) ||
            !int.TryParse(match.Groups[3].Success ? match.Groups[3].Value : "0", out int patch)) return null;
        return new(new Version(major, minor, patch), match.Groups[4].Value);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int order = Number.CompareTo(other.Number);
        if (order != 0) return order;
        if (Preview.Length == 0 || other.Preview.Length == 0) return (Preview.Length == 0).CompareTo(other.Preview.Length == 0);
        var left = Preview.Split('.'); var right = other.Preview.Split('.');
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            bool a = left[i].All(char.IsAsciiDigit), b = right[i].All(char.IsAsciiDigit);
            if (a != b) return a ? -1 : 1;
            string x = a ? left[i].TrimStart('0') : left[i], y = b ? right[i].TrimStart('0') : right[i];
            order = a && x.Length != y.Length ? x.Length.CompareTo(y.Length) : StringComparer.Ordinal.Compare(x, y);
            if (order != 0) return order;
        }
        return left.Length.CompareTo(right.Length);
    }
}
internal static class ReleaseUpdates
{
    public const string Page = "https://github.com/RazvanManolache/dosbox-enhanced/releases";
    internal const string Feed = "https://api.github.com/repos/RazvanManolache/dosbox-enhanced/releases?per_page=10";
    public static string Current => (typeof(ReleaseUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
    public static string? Newer(string body, string current)
    {
        if (body.Length > 256 * 1024 || ReleaseVersion.Parse(current) is not { } best) return null;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 100) return null;
            string? latest = null;
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                    !entry.TryGetProperty("published_at", out var date) || date.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
                string candidate = tag.GetString()!;
                if (ReleaseVersion.Parse(candidate) is { } version && version.CompareTo(best) > 0) { best = version; latest = candidate; }
            }
            return latest;
        }
        catch (JsonException) { return null; }
    }
    public static async Task<string?> Check(CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DOSBox-Enhanced-update-notice");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var response = await http.GetAsync(Feed, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 256 * 1024) return null;
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var bytes = new System.IO.MemoryStream();
        byte[] buffer = new byte[4096]; int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > 256 * 1024) return null;
            bytes.Write(buffer, 0, read);
        }
        return Newer(System.Text.Encoding.UTF8.GetString(bytes.ToArray()), Current);
    }
}
