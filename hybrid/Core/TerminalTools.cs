using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ClaudeChat.Core.ToolJson;

namespace ClaudeChat.Core;

// PowerShell (Windows) か bash でコマンドを実行する組み込みツール
public sealed partial class TerminalToolProvider(SettingsStore settings) : IToolProvider, IDisposable
{
    public const string Source = "terminal";
    const string Prefix = "terminal__";
    // API に載せる量の上限。頭と末尾を残す
    const int MaxOutputChars = 30_000;
    const int KeepHeadChars = 10_000;
    // バックグラウンドの出力を溜めすぎない
    const int MaxBackgroundBufferChars = 1_000_000;
    const int MaxTimeoutSeconds = 600;
    static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    sealed class BackgroundCommand(int id, string command, Process process)
    {
        public int Id { get; } = id;
        public string Command { get; } = command;
        public Process Process { get; } = process;
        public DateTime StartedAt { get; } = DateTime.Now;
        public StringBuilder Output { get; } = new();
        public int ReadPosition { get; set; }
    }

    readonly ConcurrentDictionary<int, BackgroundCommand> background = new();
    int nextId;

    [GeneratedRegex(@"\x1B(\[[0-9;?]*[ -/]*[@-~]|\][^\x07]*\x07|[@-Z\\-_])")]
    private static partial Regex AnsiEscape();

    static string ShellName => OperatingSystem.IsWindows() ? (FindOnPath("pwsh.exe") is not null ? "PowerShell 7 (pwsh)" : "Windows PowerShell 5.1") : "bash";

    static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), file))
            .FirstOrDefault(File.Exists);

    TerminalSettings? Active() => settings.Current?.Terminal is { Enabled: true } t ? t : null;

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources)
    {
        var terminal = Active();
        if (terminal is null || (enabledSources is not null && !enabledSources.Contains(Source))) return [];
        var shell = ShellName;
        var timeout = Math.Clamp(terminal.TimeoutSeconds, 1, MaxTimeoutSeconds);
        var cwd = $"Default working directory: {DefaultDirectory()}.";
        return
        [
            new(Prefix + "run_command", Source, "run_command", "コマンドを実行",
                $"Run a command in {shell} on the user's PC and wait for it to finish. {cwd} Each call starts a fresh shell, so cd and variables do not carry over; pass working_directory instead. Standard input is closed, so interactive prompts fail: use non-interactive flags. Times out after {timeout} seconds by default. For servers or anything long-running, use start_background instead.",
                Schema(("command", "string", "The command to run", true), ("working_directory", "string", "Absolute path of the directory to run in", false), ("timeout_seconds", "integer", $"Timeout in seconds, up to {MaxTimeoutSeconds}", false)),
                AllowAlways: false),
            new(Prefix + "start_background", Source, "start_background", "バックグラウンドで実行",
                $"Start a long-running command in {shell} (a dev server, a watcher, a long build) without waiting. Returns an id. {cwd}",
                Schema(("command", "string", "The command to run", true), ("working_directory", "string", "Absolute path of the directory to run in", false)),
                AllowAlways: false),
            new(Prefix + "read_background", Source, "read_background", "バックグラウンドの出力を見る",
                "Read new output from a background command since the last read, and whether it is still running. Without id, list all background commands.",
                Schema(("id", "integer", "The id returned by start_background", false)),
                AutoApprove: true),
            new(Prefix + "stop_background", Source, "stop_background", "バックグラウンドを止める",
                "Stop a background command and everything it started.",
                Schema(("id", "integer", "The id returned by start_background", true)),
                AutoApprove: true),
        ];
    }

    public async Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        var terminal = Active() ?? throw new ToolException("ターミナルは設定で無効になっています");
        return apiName[Prefix.Length..] switch
        {
            "run_command" => await RunAsync(input, terminal, cancellationToken),
            "start_background" => StartBackground(input),
            "read_background" => ReadBackground(input),
            "stop_background" => StopBackground(input),
            _ => throw new ToolException($"ツール {apiName} はありません"),
        };
    }

    string DefaultDirectory()
    {
        // ワークツリーに入っている間はそこで動かす。元のリポジトリを触らないため
        if (ToolContext.Current?.Conversation.WorktreePath is { } worktree && Directory.Exists(worktree)) return worktree;
        // フォルダ操作の基準フォルダがあればそこで動かす
        var files = settings.Current?.Files;
        if (files is { Enabled: true, Folders.Count: > 0 } && Directory.Exists(files.Folders[0].Path)) return files.Folders[0].Path;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    string WorkingDirectory(JsonObject input)
    {
        var dir = (string?)input["working_directory"];
        if (string.IsNullOrWhiteSpace(dir)) return DefaultDirectory();
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(DefaultDirectory(), dir);
        return Directory.Exists(dir) ? dir : throw new ToolException($"{dir} というフォルダはありません");
    }

    static Process CreateProcess(string command, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            info.FileName = FindOnPath("pwsh.exe") ?? "powershell.exe";
            // 出力を UTF-8 にそろえないと日本語が化ける
            var script = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'\n" + command;
            // npm などの .ps1 が既定の実行ポリシーで止まるため、このプロセスだけ緩める
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            {
                info.ArgumentList.Add(arg);
            }
        }
        else
        {
            info.FileName = "/bin/bash";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
        }
        info.Environment["NO_COLOR"] = "1";
        return new Process { StartInfo = info, EnableRaisingEvents = true };
    }

    static void Capture(Process process, StringBuilder output)
    {
        void Append(string? line)
        {
            if (line is null) return;
            lock (output) output.Append(line).Append('\n');
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
    }

    static void Start(Process process)
    {
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ToolException($"シェルを起動できませんでした: {e.Message}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // 入力待ちで止まらないように
        process.StandardInput.Close();
    }

    static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 止める直前に終わっていた
        }
    }

    static string Clean(string text)
    {
        text = AnsiEscape().Replace(text, "").TrimEnd();
        if (text.Length <= MaxOutputChars) return text;
        var omitted = text.Length - MaxOutputChars;
        return $"{text[..KeepHeadChars]}\n\n... {omitted:N0} 文字を省略 ...\n\n{text[^(MaxOutputChars - KeepHeadChars)..]}";
    }

    async Task<ToolCallResult> RunAsync(JsonObject input, TerminalSettings terminal, CancellationToken cancellationToken)
    {
        var command = Required(input, "command");
        var directory = WorkingDirectory(input);
        var timeout = Math.Clamp((int?)input["timeout_seconds"] ?? terminal.TimeoutSeconds, 1, MaxTimeoutSeconds);
        var output = new StringBuilder();
        using var process = CreateProcess(command, directory);
        Capture(process, output);
        // WaitForExitAsync は出力の終わりまで待つので、& で残した子プロセスがいるとシェルが終わっても返らない
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        Start(process);
        if (process.HasExited) exited.TrySetResult();

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(TimeSpan.FromSeconds(timeout));
        var timedOut = false;
        try
        {
            await exited.Task.WaitAsync(timer.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        var leftRunning = false;
        if (!timedOut)
        {
            // 残りの出力が届くのを少しだけ待つ
            var drained = process.WaitForExitAsync(CancellationToken.None);
            leftRunning = await Task.WhenAny(drained, Task.Delay(OutputGrace, CancellationToken.None)) != drained;
        }

        string text;
        lock (output) text = Clean(output.ToString());
        var status = timedOut ? $"{timeout} 秒でタイムアウトしたので止めました" : $"終了コード {process.ExitCode}";
        if (leftRunning) status += "。シェルの終了後も動き続けているプロセスがあります。止める必要があれば、次からは start_background で起動してください";
        var body = $"$ {command}\n({directory} / {status})\n{(text.Length > 0 ? text : "(出力なし)")}";
        return new ToolCallResult(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = body }), timedOut || process.ExitCode != 0);
    }

    ToolCallResult StartBackground(JsonObject input)
    {
        var command = Required(input, "command");
        var directory = WorkingDirectory(input);
        var process = CreateProcess(command, directory);
        var job = new BackgroundCommand(Interlocked.Increment(ref nextId), command, process);
        Capture(process, job.Output);
        process.OutputDataReceived += (_, _) => Trim(job);
        process.ErrorDataReceived += (_, _) => Trim(job);
        Start(process);
        background[job.Id] = job;
        return Text($"id {job.Id} で開始しました ({directory})。出力は read_background で確認できます");
    }

    static void Trim(BackgroundCommand job)
    {
        lock (job.Output)
        {
            var excess = job.Output.Length - MaxBackgroundBufferChars;
            if (excess <= 0) return;
            job.Output.Remove(0, excess);
            job.ReadPosition = Math.Max(0, job.ReadPosition - excess);
        }
    }

    static string Status(BackgroundCommand job) =>
        job.Process.HasExited ? $"終了 (終了コード {job.Process.ExitCode})" : $"実行中 ({(DateTime.Now - job.StartedAt).TotalSeconds:F0} 秒)";

    ToolCallResult ReadBackground(JsonObject input)
    {
        if (input["id"] is null)
        {
            if (background.IsEmpty) return Text("バックグラウンドのコマンドはありません");
            return Text(string.Join("\n", background.Values.OrderBy(j => j.Id).Select(j => $"id {j.Id}: {Status(j)}  $ {j.Command}")));
        }
        var job = Find(input);
        string fresh;
        lock (job.Output)
        {
            fresh = job.Output.ToString(job.ReadPosition, job.Output.Length - job.ReadPosition);
            job.ReadPosition = job.Output.Length;
        }
        fresh = Clean(fresh);
        return Text($"id {job.Id}: {Status(job)}\n{(fresh.Length > 0 ? fresh : "(新しい出力なし)")}");
    }

    ToolCallResult StopBackground(JsonObject input)
    {
        var job = Find(input);
        Kill(job.Process);
        background.TryRemove(job.Id, out _);
        job.Process.Dispose();
        return Text($"id {job.Id} を止めました");
    }

    BackgroundCommand Find(JsonObject input) =>
        (int?)input["id"] is { } id && background.TryGetValue(id, out var job) ? job : throw new ToolException("その id のバックグラウンドコマンドはありません");

    // アプリを閉じたら、起動したサーバーなどを残さない
    public void Dispose()
    {
        foreach (var job in background.Values)
        {
            Kill(job.Process);
            job.Process.Dispose();
        }
        background.Clear();
    }
}
