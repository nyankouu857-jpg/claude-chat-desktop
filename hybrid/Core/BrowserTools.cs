using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using static ClaudeChat.Core.ToolJson;

namespace ClaudeChat.Core;

// Chrome を DevTools プロトコルで動かす組み込みツール。専用プロファイルの Chrome (無ければ Edge) か、拡張機能を通していつもの Chrome を使う。
// hidden は Claude が自分で調べるための内蔵ブラウザ。窓を出さず、ユーザーのログインも持たない
public sealed class BrowserToolProvider(SettingsStore settings, AppPaths paths, BrowserBridge bridge, IPlatform platform, bool hidden = false) : IToolProvider, IAsyncDisposable
{
    public const string Source = "browser";
    public const string HiddenSource = "browse";
    string ToolSource => hidden ? HiddenSource : Source;
    string Prefix => ToolSource + "__";
    // ページ本文を API に載せる量の上限
    const int MaxPageTextChars = 20_000;
    const int MaxElements = 300;
    static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);

    // ログイン状態を残すため代表フォルダの中に置く
    string ProfileDir => Path.Combine(paths.DataDir, hidden ? "claude-browser-profile" : "browser-profile");

    readonly SemaphoreSlim gate = new(1, 1);
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
    Process? browserProcess;
    IPageChannel? session;
    string? currentTargetId;
    // 設定で接続先を変えたら、前の接続先のタブを使い続けない
    string? sessionMode;
    // いつもの Chrome で、確認なしに読んでよいサイト。ユーザーが open を許可したものだけ。
    // 開いたページが勝手にメールなどへ移っても、ログイン中の中身を確認なしで Claude に渡さない
    readonly HashSet<string> approvedHosts = new(StringComparer.OrdinalIgnoreCase);
    int port;

    const string SnapshotScript = """
        (() => {
          const max = __MAX__;
          document.querySelectorAll('[data-claude-ref]').forEach(e => e.removeAttribute('data-claude-ref'));
          const selector = 'a[href],button,input,textarea,select,summary,[role=button],[role=link],[role=tab],[role=menuitem],[role=checkbox],[role=radio],[role=option],[role=combobox],[role=textbox],[contenteditable=""],[contenteditable=true],[onclick]';
          const clean = s => String(s ?? '').replace(/\s+/g, ' ').trim();
          const lines = [];
          let n = 0;
          for (const el of document.querySelectorAll(selector)) {
            if (n >= max) break;
            const rect = el.getBoundingClientRect();
            const style = getComputedStyle(el);
            if (rect.width === 0 || rect.height === 0 || style.visibility === 'hidden' || style.display === 'none') continue;
            n++;
            el.setAttribute('data-claude-ref', String(n));
            const tag = el.tagName.toLowerCase();
            let kind = tag === 'a' ? 'link' : tag === 'input' ? `input[${el.type}]` : tag;
            const role = el.getAttribute('role');
            if (role) kind += `[role=${role}]`;
            const label = clean(el.getAttribute('aria-label') || (tag === 'input' || tag === 'select' ? '' : el.innerText) || el.title || el.alt || el.name).slice(0, 80);
            let line = `[${n}] ${kind} "${label}"`;
            if (tag === 'input' || tag === 'textarea') {
              if (el.placeholder) line += ` placeholder="${clean(el.placeholder).slice(0, 60)}"`;
              if (el.type !== 'password' && el.value && el.type !== 'checkbox' && el.type !== 'radio') line += ` value="${clean(el.value).slice(0, 80)}"`;
              if (el.checked) line += ' checked';
            }
            if (tag === 'select') line += ` selected="${clean(el.options[el.selectedIndex]?.text)}"`;
            if (tag === 'a') line += ` -> ${el.href.slice(0, 150)}`;
            if (el.disabled) line += ' (disabled)';
            if (rect.bottom < 0 || rect.top > innerHeight) line += ' (offscreen)';
            lines.push(line);
          }
          const text = (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n');
          const scrollMax = Math.max(0, document.documentElement.scrollHeight - innerHeight);
          return JSON.stringify({ url: location.href, title: document.title, text, elements: lines, truncated: n >= max, scroll: `${Math.round(scrollY)} / ${Math.round(scrollMax)}` });
        })()
        """;

    sealed record BrowserTool(string Name, string Title, string Description, JsonObject Schema, bool AutoApprove, bool AllowAlways, Func<JsonObject, CancellationToken, Task<ToolCallResult>> Run);

    List<BrowserTool>? browserTools;

    List<BrowserTool> Tools() => browserTools ??=
    [
        new("open", "ページを開く",
            "Open a URL in the browser that this app controls. Starts the browser if needed. Returns a snapshot of the page.",
            Schema(("url", "string", "The URL to open", true), ("new_tab", "boolean", "Open in a new tab instead of the current one", false)),
            false, true, OpenAsync),
        new("snapshot", "ページを読む",
            "Read the current page: its URL, title, visible text (cut at 20,000 characters; scroll and snapshot again for the rest), and a numbered list of clickable and editable elements. Use the numbers with click and type. Numbers change after each snapshot.",
            Schema(), true, true, (_, ct) => SnapshotAsync(ct)),
        new("screenshot", "スクリーンショット",
            "Take a screenshot of the visible part of the current page, for layouts, images, or charts the text snapshot cannot show.",
            Schema(), true, true, (_, ct) => ScreenshotAsync(ct)),
        new("click", "クリック",
            "Click an element by its number from the latest snapshot. If the click does not reach the element, the tool retries with the real mouse on the screen (Windows), then with a direct DOM click. If the click opens a new tab, the tool switches to it. The result only tells you the current URL, so take a snapshot to see what changed. Set real to click with the real mouse from the start, when a page ignored a normal click that did reach the element.",
            Schema(("ref", "integer", "Element number from the snapshot", true), ("real", "boolean", "Use the real mouse on the screen (moves the user's pointer briefly)", false)),
            false, true, ClickAsync),
        new("type", "文字を入力",
            "Replace the contents of an input, text area, or editable element with text, by its number from the latest snapshot. For a select element, choose the option whose text or value matches. Set submit to press Enter afterwards.",
            Schema(("ref", "integer", "Element number from the snapshot", true), ("text", "string", "Text to enter", true), ("submit", "boolean", "Press Enter after typing", false)),
            false, true, TypeAsync),
        new("press_key", "キーを押す",
            "Press a key on the focused element: Enter, Tab, Escape, Backspace, Delete, Space, ArrowUp, ArrowDown, ArrowLeft, ArrowRight, PageUp, PageDown, Home, End.",
            Schema(("key", "string", "Key name", true)),
            false, true, PressKeyAsync),
        new("scroll", "スクロール",
            "Scroll the page up or down by about one screen.",
            Schema(("direction", "string", "up or down", true)),
            true, true, ScrollAsync),
        new("back", "戻る",
            "Go back to the previous page in the current tab.",
            Schema(), true, true, (_, ct) => BackAsync(ct)),
        new("tabs", "タブ一覧",
            "List the open tabs, or switch to one by its id.",
            Schema(("switch_to", "string", "Tab id to switch to", false)),
            true, true, TabsAsync),
        new("evaluate", "JavaScript を実行",
            "Run a JavaScript expression in the current page and return its value as JSON. Use only when the other tools cannot do the job.",
            Schema(("expression", "string", "JavaScript expression", true)),
            false, false, EvaluateAsync),
    ];

    // 内蔵ブラウザはユーザーの PC 操作の設定と関係なく、ツールのメニューだけでオンオフする
    bool Enabled => hidden || settings.Current?.Browser.Enabled == true;
    string Mode => !hidden && settings.Current?.Browser.Mode == "extension" ? "extension" : "dedicated";
    bool CanRealClick => !hidden && platform.CanRealClick;

    // どちらのブラウザかで、ログインの扱いと、ユーザーに見えるかが違う
    string OpenNote => hidden
        ? " This is your own private browser built into the app, running in the background. The user cannot see it: not its window, tabs, or pages. It has none of the user's accounts or sign-ins. Use it when you want to look at something yourself, such as reading a page in full or checking how a site behaves, and tell the user what you found. To work in the browser the user sees, use the browser__ tools instead."
        : Mode == "extension"
        ? " This is the user's own everyday Chrome, signed in to the user's accounts, so treat actions on sites as acting in the user's name. The user can see it. You only see and control tabs that this app opened, grouped as \"ClaudeChat\"."
        : " It is a dedicated Chrome or Edge profile on the user's screen, separate from the user's everyday browser, so the user may need to sign in once.";

    public IReadOnlyList<ToolSpec> GetTools(ISet<string>? enabledSources)
    {
        if (!Enabled || (enabledSources is not null && !enabledSources.Contains(ToolSource))) return [];
        // いつもの Chrome ではあなたの名前で操作するので、「常に許可」は出さない。専用の窓で許可した分も持ち越さない
        var allowAlways = Mode != "extension";
        return Tools()
            // 内蔵ブラウザは画面に出ないので、本物のマウスでは押せない
            .Select(t => hidden && t.Name == "click" ? t with { Description = HiddenClickDescription, Schema = Schema(("ref", "integer", "Element number from the snapshot", true)) } : t)
            // ログインの無い窓で公開ページを読むだけなら Web ページの取得と変わらない
            .Select(t => new ToolSpec(Prefix + t.Name, ToolSource, t.Name, t.Title, t.Name == "open" ? t.Description + OpenNote : t.Description, t.Schema, t.AutoApprove || (hidden && t.Name == "open"), t.AllowAlways && allowAlways))
            .ToList();
    }

    const string HiddenClickDescription = "Click an element by its number from the latest snapshot. If the click does not reach the element, the tool retries with a direct DOM click. If the click opens a new tab, the tool switches to it. The result only tells you the current URL, so take a snapshot to see what changed.";

    public async Task<ToolCallResult> CallAsync(string apiName, JsonObject input, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new ToolException("ブラウザ操作は設定で無効になっています");
        var tool = Tools().FirstOrDefault(t => Prefix + t.Name == apiName) ?? throw new ToolException($"ツール {apiName} はありません");
        // 2 つの会話から同時に同じタブを動かさない
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (sessionMode != Mode)
            {
                await ResetSessionAsync();
                currentTargetId = null;
                approvedHosts.Clear();
                sessionMode = Mode;
            }
            var result = await tool.Run(input, cancellationToken);
            if (session?.TakeDialogNote() is { } note) result.Content.Add(new JsonObject { ["type"] = "text", ["text"] = $"(ページの{note})" });
            return result;
        }
        catch (HttpRequestException e)
        {
            throw new ToolException($"ブラウザに接続できませんでした: {e.Message}");
        }
        // HttpClient のタイムアウトもキャンセル扱いで飛んでくる
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolException("ブラウザの応答がありませんでした。少し待ってからやり直してください");
        }
        finally
        {
            gate.Release();
        }
    }

    // Browser

    string FindExecutable()
    {
        var configured = settings.Current?.Browser.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : throw new ToolException($"設定のブラウザ {configured} が見つかりません");
        }
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
            {
                candidates.Add(Path.Combine(Environment.GetFolderPath(root), "Google", "Chrome", "Application", "chrome.exe"));
            }
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                candidates.Add(Path.Combine(Environment.GetFolderPath(root), "Microsoft", "Edge", "Application", "msedge.exe"));
            }
        }
        else
        {
            foreach (var name in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge" })
            {
                candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(dir => Path.Combine(dir, name)));
            }
        }
        return candidates.FirstOrDefault(File.Exists) ?? throw new ToolException("Chrome も Edge も見つかりません。設定の「PC 操作」でブラウザの場所を指定してください");
    }

    async Task<int?> ReadActivePortAsync(CancellationToken cancellationToken)
    {
        var file = Path.Combine(ProfileDir, "DevToolsActivePort");
        if (!File.Exists(file)) return null;
        try
        {
            var lines = await File.ReadAllLinesAsync(file, cancellationToken);
            if (lines.Length == 0 || !int.TryParse(lines[0], out var found)) return null;
            await http.GetStringAsync($"http://127.0.0.1:{found}/json/version", cancellationToken);
            return found;
        }
        // ユーザーの停止まで「ブラウザが無い」と扱うと、動いているブラウザの接続情報を消してしまう
        catch (Exception e) when (e is IOException or HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    async Task EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (port != 0 && await ReadActivePortAsync(cancellationToken) == port) return;
        await ResetSessionAsync();
        // 前回起動したブラウザが残っていればそれを使う
        if (await ReadActivePortAsync(cancellationToken) is { } existing)
        {
            port = existing;
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(ProfileDir);
        File.Delete(Path.Combine(ProfileDir, "DevToolsActivePort"));
        // ブラウザのログをアプリの出力に混ぜない
        var info = new ProcessStartInfo(FindExecutable()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--remote-debugging-port=0", $"--user-data-dir={ProfileDir}", "--no-first-run", "--no-default-browser-check", "about:blank" })
        {
            info.ArgumentList.Add(arg);
        }
        if (hidden)
        {
            // 横幅はふつうのノート PC の画面に合わせ、スマホ版のページにならないようにする
            foreach (var arg in new[] { "--headless=new", "--window-size=1280,900", "--mute-audio" }) info.ArgumentList.Add(arg);
        }
        browserProcess = Process.Start(info) ?? throw new ToolException("ブラウザを起動できませんでした");
        browserProcess.OutputDataReceived += (_, _) => { };
        browserProcess.ErrorDataReceived += (_, _) => { };
        browserProcess.BeginOutputReadLine();
        browserProcess.BeginErrorReadLine();
        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ReadActivePortAsync(cancellationToken) is { } launched)
            {
                port = launched;
                return;
            }
            await Task.Delay(200, cancellationToken);
        }
        throw new ToolException("ブラウザは起動しましたが、操作用の接続ができませんでした。いったんブラウザを閉じてからやり直してください");
    }

    async Task<JsonArray> ListTargetsAsync(CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync<JsonArray>($"http://127.0.0.1:{port}/json/list", cancellationToken) ?? [];

    async Task ResetSessionAsync()
    {
        if (session is not null) await session.DisposeAsync();
        session = null;
    }

    async Task<IPageChannel> PageAsync(CancellationToken cancellationToken, bool newTab = false)
    {
        if (Mode == "extension") return await ExtensionPageAsync(cancellationToken, newTab);
        await EnsureBrowserAsync(cancellationToken);
        if (session is { IsOpen: true } && !newTab) return session;
        await ResetSessionAsync();

        JsonObject? target;
        if (newTab)
        {
            target = await NewTargetAsync(cancellationToken);
        }
        else
        {
            var pages = (await ListTargetsAsync(cancellationToken)).OfType<JsonObject>().Where(t => (string?)t["type"] == "page").ToList();
            target = pages.FirstOrDefault(t => (string?)t["id"] == currentTargetId) ?? pages.FirstOrDefault() ?? await NewTargetAsync(cancellationToken);
        }
        var url = (string?)target?["webSocketDebuggerUrl"] ?? throw new ToolException("ブラウザのタブに接続できませんでした");
        currentTargetId = (string?)target!["id"];
        var page = await CdpSession.ConnectAsync(new Uri(url), cancellationToken);
        session = page;
        // ダイアログの通知を受け取る
        await page.SendAsync("Page.enable", null, cancellationToken);
        await page.SendAsync("Page.bringToFront", null, cancellationToken);
        return page;
    }

    async Task<JsonObject?> NewTargetAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?about:blank");
        using var response = await http.SendAsync(request, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
    }

    // 拡張機能が見せるのは、このアプリが開いたタブだけ
    async Task<IPageChannel> ExtensionPageAsync(CancellationToken cancellationToken, bool newTab)
    {
        if (!bridge.Connected) throw bridge.ListenError is { } error ? new ToolException(error) : BrowserBridge.NotConnected();
        if (session is { IsOpen: true } && !newTab) return session;
        await ResetSessionAsync();
        var tabs = await ListTabsAsync(cancellationToken);
        var tabId = newTab ? null : tabs.FirstOrDefault(t => t.Id == currentTargetId).Id ?? tabs.FirstOrDefault().Id;
        tabId ??= (string?)(await bridge.CallAsync("newTab", [], cancellationToken))?["id"] ?? throw new ToolException("タブを開けませんでした");
        currentTargetId = tabId;
        var page = await ExtensionPage.AttachAsync(bridge, tabId, cancellationToken);
        session = page;
        await page.SendAsync("Page.enable", null, cancellationToken);
        return page;
    }

    async Task<List<(string Id, string Title, string Url)>> ListTabsAsync(CancellationToken cancellationToken)
    {
        if (Mode == "extension")
        {
            var tabs = await bridge.CallAsync("listTabs", [], cancellationToken) as JsonArray ?? [];
            return tabs.OfType<JsonObject>().Select(t => ((string?)t["id"] ?? "", (string?)t["title"] ?? "", (string?)t["url"] ?? "")).ToList();
        }
        await EnsureBrowserAsync(cancellationToken);
        return (await ListTargetsAsync(cancellationToken)).OfType<JsonObject>()
            .Where(t => (string?)t["type"] == "page")
            .Select(t => ((string?)t["id"] ?? "", (string?)t["title"] ?? "", (string?)t["url"] ?? ""))
            .ToList();
    }

    // Page helpers

    static async Task<JsonNode?> EvalAsync(IPageChannel page, string expression, CancellationToken cancellationToken)
    {
        var result = await page.SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true,
        }, cancellationToken);
        if (result["exceptionDetails"] is JsonObject error)
        {
            var message = (string?)error["exception"]?["description"] ?? (string?)error["text"] ?? "不明なエラー";
            throw new ToolException($"ページ内の処理でエラーになりました: {message}");
        }
        return result["result"]?["value"]?.DeepClone();
    }

    static async Task WaitForLoadAsync(IPageChannel page, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LoadTimeout;
        // 遷移が始まる前の古いページを読んでしまわないように
        await Task.Delay(300, cancellationToken);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((string?)await EvalAsync(page, "document.readyState", cancellationToken) == "complete") return;
            }
            catch (ToolException)
            {
                // 遷移の途中で実行先が消えた
            }
            await Task.Delay(250, cancellationToken);
        }
    }

    static string HostKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return "";
        var host = parsed.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    async Task GuardHostAsync(IPageChannel page, CancellationToken cancellationToken)
    {
        if (Mode != "extension") return;
        var url = (string?)await EvalAsync(page, "location.href", cancellationToken) ?? "";
        if (url == "about:blank" || approvedHosts.Contains(HostKey(url))) return;
        throw new ToolException($"このタブは {HostKey(url)} に移っています。いつもの Chrome では、確認なしに読めるのは open で開いたサイトだけです。中身が必要なら、open で {url} を開き直してください (ユーザーに確認が出ます)");
    }

    async Task<string> DescribePageAsync(IPageChannel page, CancellationToken cancellationToken)
    {
        await GuardHostAsync(page, cancellationToken);
        var raw = (string?)await EvalAsync(page, SnapshotScript.Replace("__MAX__", MaxElements.ToString()), cancellationToken) ?? "{}";
        var snapshot = JsonNode.Parse(raw)!.AsObject();
        var text = (string?)snapshot["text"] ?? "";
        if (text.Length > MaxPageTextChars) text = text[..MaxPageTextChars] + $"\n... 本文が長いので {MaxPageTextChars:N0} 文字で切りました";
        var elements = (snapshot["elements"] as JsonArray ?? []).Select(e => (string?)e).ToList();
        var more = (bool?)snapshot["truncated"] == true ? $"\n... 要素が多いので {MaxElements} 個までにしました" : "";
        return $"URL: {snapshot["url"]}\nタイトル: {snapshot["title"]}\nスクロール位置: {snapshot["scroll"]}\n\n## 本文\n{text}\n\n## 操作できる要素\n{string.Join("\n", elements)}{more}";
    }

    // 座標を返すと同時に、その要素に click が届いたかを見る仕掛けを付ける
    static async Task<(double X, double Y)> LocateAsync(IPageChannel page, int reference, CancellationToken cancellationToken)
    {
        var box = await EvalAsync(page, $$"""
            (() => {
              const el = document.querySelector('[data-claude-ref="{{reference}}"]');
              if (!el) return null;
              // scroll-behavior: smooth のページでも、座標を取る前に動かし終える
              el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
              window.__claudeClicked = false;
              el.addEventListener('click', () => { window.__claudeClicked = true; }, { once: true, capture: true });
              const r = el.getBoundingClientRect();
              return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
            })()
            """, cancellationToken) as JsonObject;
        if (box is null) throw new ToolException($"要素 {reference} が見つかりません。ページが変わった可能性があるので snapshot を取り直してください");
        return ((double)box["x"]!, (double)box["y"]!);
    }

    // 重なった広告や同意の帯、ブラウザの拡大率のずれで、座標のクリックが要素に届かないことがある。
    // 届いていれば null、届いていなければ重なっていた要素の説明 (分からなければ空)
    static async Task<string?> MissedClickAsync(IPageChannel page, int reference, CancellationToken cancellationToken)
    {
        try
        {
            return (string?)await EvalAsync(page, $$"""
                (() => {
                  if (window.__claudeClicked !== false) return null;
                  const el = document.querySelector('[data-claude-ref="{{reference}}"]');
                  if (!el) return null;
                  const r = el.getBoundingClientRect();
                  const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
                  return top && top !== el && !el.contains(top) ? `${top.tagName.toLowerCase()}${top.id ? '#' + top.id : ''}` : '';
                })()
                """, cancellationToken);
        }
        catch (ToolException)
        {
            // クリックでページが移り始め、元のページで調べられなかった。届いている
            return null;
        }
    }

    // 窓を見分けるため、タブの題名を一時的に目印に変えてから、Windows に本物のマウスで押してもらう
    async Task<string?> RealClickAsync(IPageChannel page, int reference, CancellationToken cancellationToken)
    {
        var marker = "claudechat-" + Guid.NewGuid().ToString("N")[..12];
        var box = await EvalAsync(page, $$"""
            (() => {
              const el = document.querySelector('[data-claude-ref="{{reference}}"]');
              if (!el) return null;
              el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
              window.__claudeClicked = false;
              el.addEventListener('click', () => { window.__claudeClicked = true; }, { once: true, capture: true });
              window.__claudeTitle = document.title;
              document.title = '{{marker}}';
              const r = el.getBoundingClientRect();
              return { fx: (r.left + r.width / 2) / innerWidth, fy: (r.top + r.height / 2) / innerHeight };
            })()
            """, cancellationToken) as JsonObject;
        if (box is null) throw new ToolException($"要素 {reference} が見つかりません。ページが変わった可能性があるので snapshot を取り直してください");
        try
        {
            return await Task.Run(() => platform.RealClick(marker, (double)box["fx"]!, (double)box["fy"]!), cancellationToken);
        }
        finally
        {
            try
            {
                await EvalAsync(page, $"if (document.title === '{marker}') document.title = window.__claudeTitle", CancellationToken.None);
            }
            catch (ToolException)
            {
                // 押したことでページが移った。題名は新しいページのものになっている
            }
        }
    }

    static Task ScriptClickAsync(IPageChannel page, int reference, CancellationToken cancellationToken) =>
        EvalAsync(page, $"document.querySelector('[data-claude-ref=\"{reference}\"]')?.click()", cancellationToken);

    static async Task KeyAsync(IPageChannel page, string key, int code, string? text, CancellationToken cancellationToken)
    {
        // DOM の key は空白 1 文字。"Space" のままだとボタンやチェックボックスが反応しない
        var domKey = key == "Space" ? " " : key;
        var down = new JsonObject { ["type"] = text is null ? "rawKeyDown" : "keyDown", ["key"] = domKey, ["code"] = key, ["windowsVirtualKeyCode"] = code };
        if (text is not null) down["text"] = text;
        await page.SendAsync("Input.dispatchKeyEvent", down, cancellationToken);
        await page.SendAsync("Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = domKey, ["code"] = key, ["windowsVirtualKeyCode"] = code }, cancellationToken);
    }

    static readonly Dictionary<string, (int Code, string? Text)> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = (13, "\r"),
        ["Tab"] = (9, null),
        ["Escape"] = (27, null),
        ["Backspace"] = (8, null),
        ["Delete"] = (46, null),
        ["Space"] = (32, " "),
        ["ArrowUp"] = (38, null),
        ["ArrowDown"] = (40, null),
        ["ArrowLeft"] = (37, null),
        ["ArrowRight"] = (39, null),
        ["PageUp"] = (33, null),
        ["PageDown"] = (34, null),
        ["Home"] = (36, null),
        ["End"] = (35, null),
    };

    static int Reference(JsonObject input) => (int?)input["ref"] ?? throw new ToolException("ref を指定してください");

    // Tools

    async Task<ToolCallResult> OpenAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var url = Required(input, "url");
        if (!url.Contains("://") && !url.StartsWith("about:"))
        {
            // 手元の開発サーバーは https で開けない
            var local = url.StartsWith("localhost") || url.StartsWith("127.") || url.StartsWith("[::1]");
            url = (local ? "http://" : "https://") + url;
        }
        // file:// を許すと、フォルダ操作の許可範囲を通らずに PC のファイルを読めてしまう
        var isWeb = Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https";
        if (!isWeb && url != "about:blank") throw new ToolException("開けるのは http と https のページだけです。PC のファイルはフォルダ操作のツールで読んでください");
        var page = await PageAsync(cancellationToken, newTab: (bool?)input["new_tab"] == true);
        approvedHosts.Add(HostKey(url));
        var result = await page.SendAsync("Page.navigate", new JsonObject { ["url"] = url }, cancellationToken);
        if ((string?)result["errorText"] is { Length: > 0 } error) throw new ToolException($"{url} を開けませんでした: {error}");
        await WaitForLoadAsync(page, cancellationToken);
        return Text(await DescribePageAsync(page, cancellationToken));
    }

    async Task<ToolCallResult> SnapshotAsync(CancellationToken cancellationToken) =>
        Text(await DescribePageAsync(await PageAsync(cancellationToken), cancellationToken));

    async Task<ToolCallResult> ScreenshotAsync(CancellationToken cancellationToken)
    {
        var page = await PageAsync(cancellationToken);
        await GuardHostAsync(page, cancellationToken);
        // PNG だと API の画像上限を超えやすい
        var shot = await page.SendAsync("Page.captureScreenshot", new JsonObject { ["format"] = "jpeg", ["quality"] = 70 }, cancellationToken);
        return new ToolCallResult(new JsonArray(new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = (string?)shot["data"] },
        }), false);
    }

    async Task<ToolCallResult> ClickAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var page = await PageAsync(cancellationToken);
        var reference = Reference(input);
        var tabsBefore = (await ListTabsAsync(cancellationToken)).Select(t => t.Id).ToHashSet();
        var lines = new List<string> { $"要素 {reference} をクリックしました" };
        if ((bool?)input["real"] == true)
        {
            if (!CanRealClick) throw new ToolException("この環境では本物のマウスでのクリックは使えません");
            if (await RealClickAsync(page, reference, cancellationToken) is { } failed) throw new ToolException($"本物のマウスでクリックできませんでした: {failed}");
            lines.Add("画面上で本物のマウスを使って押しました");
        }
        else
        {
            var (x, y) = await LocateAsync(page, reference, cancellationToken);
            foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
            {
                await page.SendAsync("Input.dispatchMouseEvent", new JsonObject { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 }, cancellationToken);
            }
            if (await MissedClickAsync(page, reference, cancellationToken) is { } cover)
            {
                lines.Add(cover.Length > 0 ? $"画面上では別の要素 ({cover}) が重なっていて、クリックが要素に届きませんでした" : "座標でのクリックが要素に届きませんでした");
                var realFailed = CanRealClick ? await RealClickAsync(page, reference, cancellationToken) : "";
                if (realFailed is null && await MissedClickAsync(page, reference, cancellationToken) is null)
                {
                    lines.Add("画面上で本物のマウスを使って押し直しました");
                }
                else
                {
                    if (realFailed is { Length: > 0 }) lines.Add($"本物のマウスでも押せませんでした ({realFailed})");
                    await ScriptClickAsync(page, reference, cancellationToken);
                    lines.Add("要素に直接クリックを送りました");
                }
            }
        }
        await WaitForLoadAsync(page, cancellationToken);
        // target=_blank のリンクは新しいタブで開く。元のタブを見続けると「何も起きなかった」ように見えてしまう
        var opened = (await ListTabsAsync(cancellationToken)).FirstOrDefault(t => !tabsBefore.Contains(t.Id));
        if (opened.Id is not null)
        {
            await ResetSessionAsync();
            currentTargetId = opened.Id;
            page = await PageAsync(cancellationToken);
            await WaitForLoadAsync(page, cancellationToken);
            lines.Add("新しいタブで開いたので、そのタブに切り替えました");
        }
        var title = await EvalAsync(page, "JSON.stringify([location.href, document.title])", cancellationToken);
        var info = JsonNode.Parse((string?)title ?? "[]") as JsonArray;
        var url = (string?)info?[0] ?? "";
        // 確認していないサイトに移ったら、題名 (受信件数などが入る) も渡さない
        var pageTitle = Mode == "extension" && !approvedHosts.Contains(HostKey(url)) ? "" : $"{info?[1]} ";
        lines.Add($"今のページ: {pageTitle}({url})");
        return Text(string.Join("\n", lines));
    }

    async Task<ToolCallResult> TypeAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var page = await PageAsync(cancellationToken);
        var reference = Reference(input);
        var text = (string?)input["text"] ?? "";
        var kind = (string?)await EvalAsync(page, $$"""
            (() => {
              const el = document.querySelector('[data-claude-ref="{{reference}}"]');
              if (!el) return 'missing';
              el.scrollIntoView({ block: 'center', behavior: 'instant' });
              if (el.tagName === 'SELECT') {
                const wanted = {{JsonSerializer.Serialize(text)}};
                const option = [...el.options].find(o => o.text.trim() === wanted || o.value === wanted);
                if (!option) return 'no-option';
                el.value = option.value;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
                return 'select';
              }
              el.focus();
              // フォーカスできない要素だと、前に選んでいた欄へ入力してしまう
              const active = document.activeElement;
              if (!(active === el || el.contains(active))) return 'not-editable';
              if (el.isContentEditable) document.execCommand('selectAll');
              else if (typeof el.select === 'function') el.select();
              return 'text';
            })()
            """, cancellationToken);
        switch (kind)
        {
            case "missing":
                throw new ToolException($"要素 {reference} が見つかりません。snapshot を取り直してください");
            case "not-editable":
                throw new ToolException($"要素 {reference} は入力欄ではないため入力できません。snapshot で入力欄の番号を確かめてください");
            case "no-option":
                throw new ToolException($"要素 {reference} に「{text}」という選択肢はありません");
            case "text":
                // 選択中の中身を置き換える。日本語も 1 回で入る
                if (text.Length > 0) await page.SendAsync("Input.insertText", new JsonObject { ["text"] = text }, cancellationToken);
                else await KeyAsync(page, "Delete", 46, null, cancellationToken);
                break;
        }
        if ((bool?)input["submit"] == true)
        {
            await KeyAsync(page, "Enter", 13, "\r", cancellationToken);
            await WaitForLoadAsync(page, cancellationToken);
        }
        return Text($"要素 {reference} に入力しました{((bool?)input["submit"] == true ? "。Enter を押しました" : "")}");
    }

    async Task<ToolCallResult> PressKeyAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var key = Required(input, "key");
        if (!Keys.TryGetValue(key, out var spec)) throw new ToolException($"{key} は押せないキーです。{string.Join(", ", Keys.Keys)} から選んでください");
        var page = await PageAsync(cancellationToken);
        await KeyAsync(page, Keys.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)), spec.Code, spec.Text, cancellationToken);
        if (spec.Code == 13) await WaitForLoadAsync(page, cancellationToken);
        return Text($"{key} を押しました");
    }

    async Task<ToolCallResult> ScrollAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var page = await PageAsync(cancellationToken);
        var sign = (string?)input["direction"] == "up" ? "-" : "";
        var position = await EvalAsync(page, $"window.scrollBy(0, {sign}innerHeight * 0.8); `${{Math.round(scrollY)}} / ${{Math.round(Math.max(0, document.documentElement.scrollHeight - innerHeight))}}`", cancellationToken);
        return Text($"スクロールしました。位置: {position}");
    }

    async Task<ToolCallResult> BackAsync(CancellationToken cancellationToken)
    {
        var page = await PageAsync(cancellationToken);
        await EvalAsync(page, "history.back()", cancellationToken);
        await WaitForLoadAsync(page, cancellationToken);
        return Text($"前のページに戻りました: {await EvalAsync(page, "location.href", cancellationToken)}");
    }

    async Task<ToolCallResult> TabsAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var pages = await ListTabsAsync(cancellationToken);
        if ((string?)input["switch_to"] is { Length: > 0 } id)
        {
            if (!pages.Any(t => t.Id == id)) throw new ToolException($"id {id} のタブはありません");
            await ResetSessionAsync();
            currentTargetId = id;
            var page = await PageAsync(cancellationToken);
            return Text($"タブを切り替えました\n\n{await DescribePageAsync(page, cancellationToken)}");
        }
        if (pages.Count == 0) return Text("開いているタブはありません。open で開いてください");
        // 確認していないサイトのタブは、題名にメールの件名などが出ることがあるので伏せる
        string TitleOf((string Id, string Title, string Url) tab) => Mode == "extension" && !approvedHosts.Contains(HostKey(tab.Url)) && tab.Url != "about:blank" ? "(確認していないサイト)" : tab.Title;
        return Text(string.Join("\n", pages.Select(t => $"{(t.Id == currentTargetId ? "* " : "  ")}{t.Id}  {TitleOf(t)}  ({t.Url})")));
    }

    async Task<ToolCallResult> EvaluateAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var page = await PageAsync(cancellationToken);
        var value = await EvalAsync(page, Required(input, "expression"), cancellationToken);
        // 日本語を \uXXXX にしない
        var json = value?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "undefined";
        return Text(json.Length > MaxPageTextChars ? json[..MaxPageTextChars] + "\n... 長いので切りました" : json);
    }

    // ブラウザはユーザーがそのまま使えるように閉じない。内蔵ブラウザは見えないまま残るので閉じる
    public async ValueTask DisposeAsync()
    {
        // 応答の遅い接続の後始末より先に止める。終了は片付けを長く待たない
        if (hidden && browserProcess is { HasExited: false })
        {
            try { browserProcess.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        await ResetSessionAsync();
        browserProcess?.Dispose();
        http.Dispose();
    }
}
