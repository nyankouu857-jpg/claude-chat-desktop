using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

public sealed record UpdateInfo(bool Available, string? Current, string Latest, string? PublishedAt, IReadOnlyList<string> Changes)
{
    public JsonObject ToWire() => new()
    {
        ["available"] = Available,
        ["current"] = Current,
        ["latest"] = Latest,
        ["publishedAt"] = PublishedAt,
        ["changes"] = new JsonArray(Changes.Select(c => (JsonNode)c).ToArray()),
    };
}

// main に入るたびに CI が作り直す latest リリースと、この exe に埋め込んだコミットを比べる
public sealed class Updater(IPlatform platform, HttpClient http)
{
    const string Repo = "nyankouu857-jpg/claude-chat-desktop";
    const string AssetName = "ClaudeChat.zip";
    const string ExeEntry = "ClaudeChat/ClaudeChat.exe";
    // 変更点は多すぎても読まれないので、新しい方からこれだけ
    const int MaxChanges = 15;

    async Task<JsonObject> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // GitHub の API は User-Agent が無いと断る
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClaudeChat", platform.Version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub から更新を確かめられませんでした ({(int)response.StatusCode})");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject ?? throw new InvalidOperationException("GitHub の応答を読めませんでした");
    }

    static bool SameCommit(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    public async Task<UpdateInfo> CheckAsync(CancellationToken cancellationToken)
    {
        var release = await GetJsonAsync($"https://api.github.com/repos/{Repo}/releases/tags/latest", cancellationToken);
        var latest = (string?)release["target_commitish"] ?? throw new InvalidOperationException("最新版のコミットが分かりませんでした");
        var current = platform.Commit;
        // 手元でビルドした版はコミットが分からないので、更新を勧めない
        var available = current is not null && !SameCommit(current, latest);
        var changes = new List<string>();
        if (available)
        {
            try
            {
                var compare = await GetJsonAsync($"https://api.github.com/repos/{Repo}/compare/{current}...{latest}", cancellationToken);
                changes.AddRange((compare["commits"] as JsonArray ?? [])
                    .Select(c => ((string?)c?["commit"]?["message"] ?? "").Split('\n')[0].Trim())
                    .Where(m => m.Length > 0)
                    .Reverse()
                    .Take(MaxChanges));
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
            {
                // 変更点が出せなくても更新はできる
            }
        }
        return new UpdateInfo(available, current, latest, (string?)release["published_at"], changes);
    }

    // 新しい exe を今の exe の隣に取り出してから入れ替える。途中で失敗しても今の exe は壊さない
    public async Task<string?> InstallAsync(CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("今の exe の場所が分かりません");
        var release = await GetJsonAsync($"https://api.github.com/repos/{Repo}/releases/tags/latest", cancellationToken);
        var url = (release["assets"] as JsonArray ?? []).Select(a => a as JsonObject).FirstOrDefault(a => (string?)a?["name"] == AssetName)?["browser_download_url"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"最新版に {AssetName} がありません");
        var zip = Path.Combine(Path.GetTempPath(), $"ClaudeChat-update-{Guid.NewGuid():N}.zip");
        var fresh = exe + ".new";
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClaudeChat", platform.Version));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"最新版をダウンロードできませんでした ({(int)response.StatusCode})");
                await using var file = File.Create(zip);
                await response.Content.CopyToAsync(file, cancellationToken);
            }
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.GetEntry(ExeEntry) ?? throw new InvalidOperationException($"ダウンロードした zip に {ExeEntry} がありません");
                entry.ExtractToFile(fresh, overwrite: true);
            }
            return platform.InstallUpdate(fresh);
        }
        finally
        {
            File.Delete(zip);
            // 入れ替えに失敗したときの残り
            if (File.Exists(fresh)) File.Delete(fresh);
        }
    }
}
