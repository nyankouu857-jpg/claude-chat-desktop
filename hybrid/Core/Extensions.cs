using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ClaudeChat.Core.ToolJson;

namespace ClaudeChat.Core;

public sealed record SkillInfo(string Name, string Description, string Directory, bool UserOnly);

public sealed record CommandInfo(string Name, string Description, string Body, string Kind);

// Claude Code と同じ形の Skills、自作コマンド、出力スタイルを代表フォルダから読む
public sealed partial class Extensions(AppPaths paths, SettingsStore settings)
{
    // Claude Code の組み込みスタイルにならう
    static readonly Dictionary<string, (string Label, string Text)> BuiltinStyles = new()
    {
        ["default"] = ("標準", ""),
        ["concise"] = ("簡潔", "Keep responses short and to the point. Lead with the answer, skip preamble and recaps, and use the fewest words that stay clear."),
        ["formal"] = ("フォーマル", "Write in a clear, polished, professional register. Use complete sentences and precise wording, avoid slang, jokes, and emoji, and organize longer answers with clear structure."),
        ["explanatory"] = ("説明多め", "Alongside doing what the user asks, explain your reasoning and the key ideas behind it. Add short \"Insight\" notes about why a choice was made or what a concept means, so the user learns from the work."),
        ["learning"] = ("学習", "Act as a patient teacher. Explain concepts step by step, check the user's understanding, and when the task involves code or exercises, leave small, well-scoped parts for the user to try themselves before showing the full answer."),
    };

    [GeneratedRegex(@"^---\s*\n(?<front>.*?)\n---\s*\n?", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex SafeName();

    public static bool IsSafeName(string name) => SafeName().IsMatch(name);

    static (Dictionary<string, string> Fields, string Body) Parse(string text)
    {
        text = text.Replace("\r\n", "\n");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var match = FrontMatter().Match(text);
        if (!match.Success) return (fields, text.Trim());
        foreach (var line in match.Groups["front"].Value.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"');
        }
        return (fields, text[match.Length..].Trim());
    }

    public bool IsSkillEnabled(string name) =>
        settings.Current?.Features.DisabledSkills.Contains(name, StringComparer.OrdinalIgnoreCase) != true;

    // 止めた Skill は Claude にも / 補完にも出さない。設定画面だけが全部を見る
    public List<SkillInfo> ListSkills(bool includeDisabled = false)
    {
        if (!Directory.Exists(paths.SkillsDir)) return [];
        var skills = new List<SkillInfo>();
        foreach (var dir in Directory.EnumerateDirectories(paths.SkillsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file)) continue;
            var (fields, body) = Parse(File.ReadAllText(file));
            var name = fields.GetValueOrDefault("name") is { Length: > 0 } n ? n : Path.GetFileName(dir);
            if (!IsSafeName(name)) continue;
            var description = fields.GetValueOrDefault("description") is { Length: > 0 } d ? d : body.Split('\n').FirstOrDefault() ?? "";
            if (!includeDisabled && !IsSkillEnabled(name)) continue;
            skills.Add(new SkillInfo(name, description, dir, fields.GetValueOrDefault("disable-model-invocation") == "true"));
        }
        return skills;
    }

    public (SkillInfo Info, string Body)? GetSkill(string name)
    {
        var skill = ListSkills().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null) return null;
        return (skill, Parse(File.ReadAllText(Path.Combine(skill.Directory, "SKILL.md"))).Body);
    }

    // 自作コマンドと Skills を、画面の / 補完に出す形でまとめる。名前が重なれば Skills を優先する
    public List<CommandInfo> ListCommands()
    {
        var result = new List<CommandInfo>();
        foreach (var skill in ListSkills())
        {
            result.Add(new CommandInfo(skill.Name, skill.Description, GetSkill(skill.Name)?.Body ?? "", "skill"));
        }
        if (Directory.Exists(paths.CommandsDir))
        {
            foreach (var file in Directory.EnumerateFiles(paths.CommandsDir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!IsSafeName(name) || result.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                var (fields, body) = Parse(File.ReadAllText(file));
                var description = fields.GetValueOrDefault("description") is { Length: > 0 } d ? d : body.Split('\n').FirstOrDefault() ?? "";
                result.Add(new CommandInfo(name, description, body, "command"));
            }
        }
        return result;
    }

    // claude.ai の「Skill をアップロード」。SKILL.md が直下か、1 つ下のフォルダにある ZIP を受け付ける
    public string ImportSkill(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var manifest = zip.Entries
            .Where(e => e.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/') <= 1)
            .OrderBy(e => e.FullName.Length)
            .FirstOrDefault() ?? throw new InvalidOperationException("ZIP の中に SKILL.md がありません");
        var prefix = manifest.FullName[..^manifest.Name.Length];
        string text;
        using (var reader = new StreamReader(manifest.Open())) text = reader.ReadToEnd();
        var (fields, _) = Parse(text);
        var name = fields.GetValueOrDefault("name") is { Length: > 0 } n ? n : prefix.TrimEnd('/');
        if (!IsSafeName(name)) throw new InvalidOperationException("SKILL.md の name は英数字とハイフンにしてください");
        var root = Path.GetFullPath(Path.Combine(paths.SkillsDir, name));
        if (Directory.Exists(root)) throw new InvalidOperationException($"{name} という Skill はもうあります");
        try
        {
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName[prefix.Length..]));
                // ../ を含む名前で skills の外へ書かせない
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ZIP の中に不正なパスがあります");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
        }
        catch
        {
            // 途中までの展開を残すと、壊れた Skill が一覧に出る
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
        }
        return name;
    }

    public JsonArray ListOutputStyles()
    {
        var list = new JsonArray(BuiltinStyles.Select(s => (JsonNode)new JsonObject { ["id"] = s.Key, ["label"] = s.Value.Label, ["builtin"] = true }).ToArray());
        if (Directory.Exists(paths.OutputStylesDir))
        {
            foreach (var file in Directory.EnumerateFiles(paths.OutputStylesDir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!IsSafeName(id) || BuiltinStyles.ContainsKey(id)) continue;
                var (fields, _) = Parse(File.ReadAllText(file));
                list.Add(new JsonObject { ["id"] = id, ["label"] = fields.GetValueOrDefault("name") is { Length: > 0 } n ? n : id, ["builtin"] = false });
            }
        }
        return list;
    }

    public JsonObject GetOutputStyle(string id)
    {
        var file = CustomStylePath(id);
        if (!File.Exists(file)) throw new InvalidOperationException("スタイルが見つかりません");
        var (fields, body) = Parse(File.ReadAllText(file));
        return new JsonObject { ["id"] = id, ["name"] = fields.GetValueOrDefault("name") is { Length: > 0 } n ? n : id, ["text"] = body };
    }

    public string SaveOutputStyle(string? id, string name, string text)
    {
        if (name.Trim().Length == 0 || text.Trim().Length == 0) throw new InvalidOperationException("名前と指示を入れてください");
        id ??= "style-" + Guid.NewGuid().ToString("N")[..8];
        var file = CustomStylePath(id);
        Directory.CreateDirectory(paths.OutputStylesDir);
        // 名前に改行や引用符があってもフロントマターが壊れないように
        File.WriteAllText(file, $"---\nname: {name.Trim().Replace('\n', ' ').Replace("\"", "'")}\n---\n\n{text.Trim()}\n");
        return id;
    }

    public string CustomStylePath(string id)
    {
        if (!IsSafeName(id) || BuiltinStyles.ContainsKey(id)) throw new InvalidOperationException("組み込みのスタイルは変えられません");
        return Path.Combine(paths.OutputStylesDir, $"{id}.md");
    }

    public string OutputStyleText(string style)
    {
        if (BuiltinStyles.TryGetValue(style, out var builtin)) return builtin.Text;
        if (!IsSafeName(style)) return "";
        var file = Path.Combine(paths.OutputStylesDir, $"{style}.md");
        return File.Exists(file) ? Parse(File.ReadAllText(file)).Body : "";
    }
}

// Skills を Claude が自分で読み込むためのツール
public sealed class SkillToolProvider(Extensions extensions) : IToolProvider
{
    public const string Source = "skills";
    const string Prefix = "skills__";
    // SKILL.md から参照される資料。大きすぎるものは載せない
    const long MaxFileBytes = 512 * 1024;

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources)
    {
        if (enabledSources is not null && !enabledSources.Contains(Source)) return [];
        var skills = extensions.ListSkills();
        if (skills.Count == 0) return [];
        var tools = new List<ToolSpec>();
        if (skills.Any(s => !s.UserOnly))
        {
            tools.Add(new(Prefix + "use", Source, "use", "スキルを読み込む",
                "Load a skill's full instructions (SKILL.md) by name. The available skills are listed in the system prompt. Load a skill before doing a task it covers, then follow it.",
                Schema(("name", "string", "Skill name", true)), AutoApprove: true, ReadOnly: true));
        }
        // ユーザーが /名前 で呼んだ専用のスキルも、資料は読めるようにする
        tools.Add(new(Prefix + "read_file", Source, "read_file", "スキルの資料を読む",
                "Read a supporting file that a skill's instructions refer to, by a path relative to the skill folder.",
                Schema(("name", "string", "Skill name", true), ("path", "string", "Path inside the skill folder", true)), AutoApprove: true, ReadOnly: true));
        return tools;
    }

    public static List<string> FilesOf(SkillInfo skill) => Directory.EnumerateFiles(skill.Directory, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(skill.Directory, f).Replace('\\', '/'))
        .Where(f => f != "SKILL.md")
        .Take(100)
        .ToList();

    public Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        var name = Required(input, "name");
        var skill = extensions.GetSkill(name) ?? throw new ToolException($"{name} というスキルはありません");
        if (apiName == Prefix + "use")
        {
            if (skill.Info.UserOnly) throw new ToolException($"{name} はユーザーが /{name} で呼ぶ専用のスキルです");
            var files = FilesOf(skill.Info);
            var listing = files.Count > 0 ? $"\n\n## Files in this skill\n{string.Join("\n", files)}" : "";
            return Task.FromResult(Text($"# Skill: {skill.Info.Name}\n\n{skill.Body}{listing}"));
        }
        var root = Path.GetFullPath(skill.Info.Directory);
        var full = Path.GetFullPath(Path.Combine(root, Required(input, "path")));
        // スキルのフォルダの外を読ませない
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ToolException("スキルのフォルダの外は読めません");
        if (!File.Exists(full)) throw new ToolException("そのファイルはありません");
        if (new FileInfo(full).Length > MaxFileBytes) throw new ToolException("ファイルが大きすぎます");
        return Task.FromResult(Text(File.ReadAllText(full)));
    }
}
