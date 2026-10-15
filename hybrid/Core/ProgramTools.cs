using System.Diagnostics;
using System.Text.Json.Nodes;
using static ClaudeChat.Core.ToolJson;

namespace ClaudeChat.Core;

// exe やファイルを Windows の既定の方法で開く組み込みツール
public sealed class ProgramToolProvider(SettingsStore settings) : IToolProvider
{
    public const string Source = "programs";
    const string Prefix = "programs__";
    const int MaxFindResults = 30;
    static readonly string[] LaunchableExtensions = [".exe", ".lnk", ".bat", ".cmd", ".msc", ".appref-ms", ".url"];

    bool Enabled => settings.Current?.Programs.Enabled == true;

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources)
    {
        if (!Enabled || (enabledSources is not null && !enabledSources.Contains(Source))) return [];
        return
        [
            new(Prefix + "find_program", Source, "find_program", "アプリを探す",
                "Find installed apps by name in the Start menu and on PATH. Returns paths that launch_program can open.",
                Schema(("name", "string", "Part of the app name, for example chrome, excel, メモ帳", true)),
                AutoApprove: true),
            new(Prefix + "launch_program", Source, "launch_program", "アプリやファイルを開く",
                "Open an exe, a shortcut, a document, a folder, or a URL the way double-clicking it in Windows would, without waiting for it to close. Use the terminal tool instead when you need the output.",
                Schema(("path", "string", "Path of the exe, shortcut, file, or folder, or a URL", true), ("arguments", "string", "Command-line arguments", false), ("working_directory", "string", "Directory to start in", false)),
                AllowAlways: false),
            new(Prefix + "list_windows", Source, "list_windows", "開いているウィンドウを見る",
                "List programs that currently have a window open, with their process ids.",
                Schema(("name", "string", "Only show processes whose name or window title contains this", false)),
                AutoApprove: true),
            new(Prefix + "close_window", Source, "close_window", "ウィンドウを閉じる",
                "Ask a program to close its main window, as clicking its close button would. Unsaved work prompts stay up for the user.",
                Schema(("process_id", "integer", "The process id from list_windows", true)),
                AllowAlways: false),
        ];
    }

    public bool Owns(string apiName) => apiName.StartsWith(Prefix);

    public Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new ToolException("アプリの起動は設定で無効になっています");
        return Task.FromResult(apiName[Prefix.Length..] switch
        {
            "find_program" => FindProgram(Required(input, "name")),
            "launch_program" => Launch(input),
            "list_windows" => ListWindows((string?)input["name"]),
            "close_window" => CloseWindow(input),
            _ => throw new ToolException($"ツール {apiName} はありません"),
        });
    }

    static IEnumerable<string> StartMenuFolders()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && Directory.Exists(path)) yield return path;
        }
    }

    static ToolCallResult FindProgram(string name)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var shortcuts = StartMenuFolders()
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", options))
            .Where(p => LaunchableExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => Path.GetFileNameWithoutExtension(p).Contains(name, StringComparison.OrdinalIgnoreCase));
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => dir.Trim('"'))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", new EnumerationOptions { IgnoreInaccessible = true }))
            .Where(p => !OperatingSystem.IsWindows() || Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(p => Path.GetFileNameWithoutExtension(p).Contains(name, StringComparison.OrdinalIgnoreCase));
        var hits = shortcuts.Concat(onPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFindResults).ToList();
        return Text(hits.Count == 0 ? $"{name} に一致するアプリは見つかりませんでした" : string.Join("\n", hits));
    }

    static ToolCallResult Launch(JsonObject input)
    {
        var path = Required(input, "path");
        var isUrl = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        if (!isUrl && Path.IsPathRooted(path) && !File.Exists(path) && !Directory.Exists(path)) throw new ToolException($"{path} は見つかりません");
        var info = new ProcessStartInfo(path)
        {
            // 既定のアプリで開く。exe 以外のファイルやフォルダも同じ口で開ける
            UseShellExecute = true,
            Arguments = (string?)input["arguments"] ?? "",
        };
        if ((string?)input["working_directory"] is { Length: > 0 } dir) info.WorkingDirectory = dir;
        try
        {
            using var process = Process.Start(info);
            var detail = process is null ? "既に起動しているアプリに渡しました" : $"プロセス id {process.Id}";
            return Text($"{path} を開きました ({detail})");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ToolException($"{path} を開けませんでした: {e.Message}");
        }
    }

    static ToolCallResult ListWindows(string? filter)
    {
        var rows = Process.GetProcesses()
            .Select(p =>
            {
                try
                {
                    return (p.Id, p.ProcessName, Title: p.MainWindowTitle);
                }
                catch (InvalidOperationException)
                {
                    return (p.Id, ProcessName: "", Title: "");
                }
                finally
                {
                    p.Dispose();
                }
            })
            .Where(r => r.Title.Length > 0)
            .Where(r => filter is null || r.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(r => $"{r.Id}\t{r.ProcessName}\t{r.Title}")
            .ToList();
        return Text(rows.Count == 0 ? "該当するウィンドウはありません" : "process_id\tname\ttitle\n" + string.Join("\n", rows));
    }

    static ToolCallResult CloseWindow(JsonObject input)
    {
        var id = (int?)input["process_id"] ?? throw new ToolException("process_id を指定してください");
        // 自分自身を閉じさせない
        if (id == Environment.ProcessId) throw new ToolException("このアプリ自身は閉じられません");
        try
        {
            using var process = Process.GetProcessById(id);
            var name = process.ProcessName;
            return process.CloseMainWindow()
                ? Text($"{name} (id {id}) に閉じるよう伝えました。保存の確認が出た場合はユーザーの操作を待ちます")
                : throw new ToolException($"{name} (id {id}) にはウィンドウがないため閉じられません");
        }
        catch (ArgumentException)
        {
            throw new ToolException($"id {id} のプロセスはありません");
        }
    }
}
