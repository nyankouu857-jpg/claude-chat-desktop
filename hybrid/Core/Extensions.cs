using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ClaudeChat.Core.ToolJson;

namespace ClaudeChat.Core;

public sealed record SkillInfo(string Name, string Description, string Directory, bool UserOnly);

public sealed record CommandInfo(string Name, string Description, string Body, string Kind);

// Claude Code と同じ形の Skills、自作コマンド、出力スタイルを代表フォルダから読む
public sealed partial class Extensions(AppPaths paths)
{
    // Claude Code の組み込みスタイルにならう
    static readonly Dictionary<string, (string Label, string Text)> BuiltinStyles = new()
    {
        ["default"] = ("標準", ""),
        ["concise"] = ("簡潔", "Keep responses short and to the point. Lead with the answer, skip preamble and recaps, and use the fewest words that stay clear."),
        ["explanatory"] = ("説明多め", "Alongside doing what the user asks, explain your reasoning and the key ideas behind it. Add short \"Insight\" notes about why a choice was made or what a concept means, so the user learns from the work."),
        ["learning"] = ("学習", "Act as a patient teacher. Explain concepts step by step, check the user's understanding, and when the task involves code or exercises, leave small, well-scoped parts for the user to try themselves before showing the full answer."),
        ["proactive"] = ("先回り", "Be proactive. When you finish what was asked, point out closely related issues, risks, or next steps the user is likely to want, and offer to handle them."),
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

    public List<SkillInfo> ListSkills()
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
        if (!extensions.ListSkills().Any(s => !s.UserOnly)) return [];
        return
        [
            new(Prefix + "use", Source, "use", "スキルを読み込む",
                "Load a skill's full instructions (SKILL.md) by name. The available skills are listed in the system prompt. Load a skill before doing a task it covers, then follow it.",
                Schema(("name", "string", "Skill name", true)), AutoApprove: true, ReadOnly: true),
            new(Prefix + "read_file", Source, "read_file", "スキルの資料を読む",
                "Read a supporting file that a skill's instructions refer to, by a path relative to the skill folder.",
                Schema(("name", "string", "Skill name", true), ("path", "string", "Path inside the skill folder", true)), AutoApprove: true, ReadOnly: true),
        ];
    }

    public Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        var name = Required(input, "name");
        var skill = extensions.GetSkill(name) ?? throw new ToolException($"{name} というスキルはありません");
        if (skill.Info.UserOnly) throw new ToolException($"{name} はユーザーが /{name} で呼ぶ専用のスキルです");
        if (apiName == Prefix + "use")
        {
            var files = Directory.EnumerateFiles(skill.Info.Directory, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(skill.Info.Directory, f).Replace('\\', '/'))
                .Where(f => f != "SKILL.md")
                .Take(100)
                .ToList();
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
