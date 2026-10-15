using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeChat.Core;

public sealed record HookOutcome(bool Blocked, string? Message);

// Claude Code のフックと同じ考え方。決まったタイミングで settings.json に書いたコマンドを動かし、
// 入力は JSON で標準入力に渡す。終了コード 2 は「止める」で、標準エラーが理由になる
public sealed class Hooks(SettingsStore settings, AppPaths paths)
{
    public const string PreToolUse = "PreToolUse";
    public const string PostToolUse = "PostToolUse";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string Stop = "Stop";
    public const string Notification = "Notification";

    // 1 回のフックの出力の上限
    const int MaxOutputChars = 10_000;

    async Task<List<HookEntry>> EntriesAsync(string name, string? toolName)
    {
        var current = await settings.GetAsync();
        if (!current.Hooks.TryGetValue(name, out var entries)) return [];
        return entries.Where(e => !string.IsNullOrWhiteSpace(e.Command) && Matches(e.Matcher, toolName)).ToList();
    }

    static bool Matches(string? matcher, string? toolName)
    {
        // Claude Code の設定例は「すべて」を * と書く
        if (string.IsNullOrWhiteSpace(matcher) || matcher.Trim() == "*" || toolName is null) return true;
        try
        {
            return Regex.IsMatch(toolName, $"^(?:{matcher})$", RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    async Task<HookOutcome> RunAllAsync(string name, string? toolName, JsonObject payload)
    {
        var notes = new List<string>();
        foreach (var entry in await EntriesAsync(name, toolName))
        {
            payload["hook_event_name"] = name;
            var (exitCode, stdout, stderr) = await RunAsync(entry, payload.ToJsonString());
            var blockedMessage = stderr.Trim().Length > 0 ? stderr.Trim() : $"{name} フックが止めました";
            if (exitCode == 2) return new HookOutcome(true, blockedMessage);
            if (exitCode != 0) continue;
            // Claude Code と同じ JSON の返し方も受け付ける
            if (TryParse(stdout) is { } json)
            {
                var specific = json["hookSpecificOutput"] as JsonObject;
                if ((string?)json["decision"] == "block") return new HookOutcome(true, (string?)json["reason"] ?? blockedMessage);
                if ((string?)specific?["permissionDecision"] == "deny") return new HookOutcome(true, (string?)specific["permissionDecisionReason"] ?? blockedMessage);
                if ((string?)specific?["additionalContext"] is { Length: > 0 } context) notes.Add(context);
                if ((string?)json["reason"] is { Length: > 0 } reason && name == PostToolUse) notes.Add(reason);
            }
            else if (stdout.Trim().Length > 0)
            {
                notes.Add(stdout.Trim());
            }
        }
        return new HookOutcome(false, notes.Count > 0 ? string.Join("\n", notes) : null);
    }

    static JsonObject? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(HookEntry entry, string input)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = paths.Root,
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
            info.FileName = "powershell.exe";
            var script = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; [Console]::InputEncoding = [Text.Encoding]::UTF8\n" + entry.Command;
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        }
        else
        {
            info.FileName = "/bin/bash";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(entry.Command);
        }
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("フックを起動できませんでした");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(entry.TimeoutSeconds, 1, 600)));
            try
            {
                // 標準入力を読まないフックでも、書き込みで固まらないよう時間を区切る
                try
                {
                    await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // 読まずに終わったフック。終了コードはそのまま使う
                }
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (1, "", "フックがタイムアウトしました");
            }
            return (process.ExitCode, Limit(await stdout), Limit(await stderr));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 壊れたフックでチャットを止めない
            return (1, "", e.Message);
        }
    }

    static string Limit(string s) => s.Length > MaxOutputChars ? s[..MaxOutputChars] : s;

    JsonObject Base(Conversation conversation) => new()
    {
        ["session_id"] = conversation.Id,
        ["chat_title"] = conversation.Title,
        ["folder_id"] = conversation.FolderId,
        ["cwd"] = conversation.WorktreePath ?? settings.Current?.Files.Folders.FirstOrDefault()?.Path ?? paths.Root,
        ["permission_mode"] = conversation.PermissionMode,
    };

    public Task<HookOutcome> RunPreToolUseAsync(Conversation conversation, string toolName, JsonObject input)
    {
        var payload = Base(conversation);
        payload["tool_name"] = toolName;
        payload["tool_input"] = input.DeepClone();
        return RunAllAsync(PreToolUse, toolName, payload);
    }

    public Task<HookOutcome> RunPostToolUseAsync(Conversation conversation, string toolName, JsonObject input, JsonObject result)
    {
        var payload = Base(conversation);
        payload["tool_name"] = toolName;
        payload["tool_input"] = input.DeepClone();
        payload["tool_response"] = result.DeepClone();
        return RunAllAsync(PostToolUse, toolName, payload);
    }

    public Task<HookOutcome> RunUserPromptSubmitAsync(Conversation conversation, string prompt)
    {
        var payload = Base(conversation);
        payload["prompt"] = prompt;
        return RunAllAsync(UserPromptSubmit, null, payload);
    }

    public Task<HookOutcome> RunStopAsync(Conversation conversation, ChatRunResult result, bool continuing)
    {
        var payload = Base(conversation);
        // Claude Code と同じく、フックに続けさせた後の Stop かどうかを渡す
        payload["stop_hook_active"] = continuing;
        payload["status"] = result.Status.ToString().ToLowerInvariant();
        payload["last_message"] = ChatHub.FinalText(conversation);
        return RunAllAsync(Stop, null, payload);
    }

    public Task<HookOutcome> RunNotificationAsync(Conversation conversation, string message)
    {
        var payload = Base(conversation);
        payload["message"] = message;
        return RunAllAsync(Notification, null, payload);
    }
}
