using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

// claude.ai のプロジェクトナレッジ。文字にしたものをプロジェクトごとのフォルダに置き、そのプロジェクトのチャットに毎回渡す
public static class ProjectKnowledge
{
    // 毎回の入力に載る。超えた分は名前だけ知らせ、会話の枠を食い潰さないようにする
    const int MaxPromptChars = 300_000;

    public static string DirFor(AppPaths paths, string folderId) => Path.Combine(paths.ProjectKnowledgeDir, folderId);

    public static JsonArray List(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        return new JsonArray(Directory.EnumerateFiles(dir)
            .OrderBy(File.GetCreationTimeUtc)
            .Select(f => (JsonNode)new JsonObject
            {
                ["name"] = Path.GetFileName(f),
                ["size"] = new FileInfo(f).Length,
                ["updatedAt"] = new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds(),
            })
            .ToArray());
    }

    // 画面から来た名前をフォルダの中のファイル名に限る
    public static string PathOf(string dir, string name)
    {
        var fileName = Path.GetFileName(name.Trim());
        foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
        if (fileName.Trim('.').Length == 0) throw new InvalidOperationException("ファイル名が不正です");
        return Path.Combine(dir, fileName);
    }

    public static async Task<JsonArray> AddAsync(string dir, string name, string text)
    {
        if (text.Trim().Length == 0) throw new InvalidOperationException("中身が空です");
        Directory.CreateDirectory(dir);
        // 元が Word などでも中身は文字なので、開いたときに読めるよう .txt を付ける
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension == ".pdf" || DocumentText.Extensions.Contains(extension)) name += ".txt";
        await File.WriteAllTextAsync(PathOf(dir, name), text);
        return List(dir);
    }

    public static string? PromptBlock(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var builder = new StringBuilder();
        var skipped = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(File.GetCreationTimeUtc))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (builder.Length + text.Length > MaxPromptChars)
            {
                skipped.Add(name);
                continue;
            }
            builder.Append($"<document name=\"{name.Replace("\"", "'")}\">\n{text.Trim()}\n</document>\n");
        }
        if (builder.Length == 0 && skipped.Count == 0) return null;
        var note = skipped.Count > 0 ? $"Too large to include here: {string.Join(", ", skipped)}. Tell the user if they ask about these.\n" : "";
        return $"<project_knowledge>\nDocuments the user added to this project. Use them when relevant.\n{builder}{note}</project_knowledge>";
    }
}
