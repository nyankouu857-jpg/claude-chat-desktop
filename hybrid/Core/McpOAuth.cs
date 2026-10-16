using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using ModelContextProtocol.Authentication;

namespace ClaudeChat.Core;

// リモートの MCP サーバーへの OAuth ログイン。トークンは暗号化して残し、次からは聞かずにつなぐ
public sealed class McpOAuth(AppPaths paths, ISecretProtector protector)
{
    // 動的クライアント登録で戻り先を固定で届けるので、毎回同じポートで待つ
    const int CallbackPort = 33418;
    public static readonly Uri RedirectUri = new($"http://localhost:{CallbackPort}/callback");
    // ブラウザでログインを済ませるのを待つ上限
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    readonly SemaphoreSlim fileGate = new(1, 1);
    // 同じポートで 2 つの待ち受けはできないので、ログインは 1 つずつ
    static readonly SemaphoreSlim loginGate = new(1, 1);
    // SDK は問い合わせのたびにトークンを聞いてくる。毎回ファイルを開いて復号すると、書き込みとぶつかって失敗する
    readonly Dictionary<string, TokenContainer> memory = [];
    Dictionary<string, string>? stored;

    string TokensFile => Path.Combine(paths.ConfigDir, "mcp-tokens.json");

    public Action<string>? OpenBrowser { get; set; }

    // URL を変えたら別のサーバーとして、ログインし直してもらう
    static string KeyOf(string server, string url) => $"{server}|{url}";

    // fileGate の中で呼ぶ
    async Task<Dictionary<string, string>> StoredAsync()
    {
        if (stored is not null) return stored;
        try
        {
            stored = await Json.ReadAsync<Dictionary<string, string>>(TokensFile) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            stored = [];
        }
        return stored;
    }

    async Task<TokenContainer?> GetAsync(string key)
    {
        await fileGate.WaitAsync();
        try
        {
            if (memory.TryGetValue(key, out var tokens)) return tokens;
            if (!(await StoredAsync()).TryGetValue(key, out var protectedTokens)) return null;
            try
            {
                tokens = JsonSerializer.Deserialize<TokenContainer>(protector.Unprotect(protectedTokens));
            }
            catch (Exception)
            {
                // 別の PC やユーザーで暗号化したものは読めない。ログインし直してもらう
                return null;
            }
            if (tokens is not null) memory[key] = tokens;
            return tokens;
        }
        finally
        {
            fileGate.Release();
        }
    }

    async Task SetAsync(string key, TokenContainer tokens, CancellationToken cancellationToken)
    {
        await fileGate.WaitAsync(cancellationToken);
        try
        {
            var all = await StoredAsync();
            all[key] = protector.Protect(JsonSerializer.Serialize(tokens));
            memory[key] = tokens;
            await Json.WriteAtomicAsync(TokensFile, all);
        }
        finally
        {
            fileGate.Release();
        }
    }

    public ITokenCache CacheFor(string server, string url) => new Cache(this, KeyOf(server, url));

    public async Task<bool> HasTokensAsync(string server, string url) => await GetAsync(KeyOf(server, url)) is not null;

    public async Task ForgetAsync(string server)
    {
        await fileGate.WaitAsync();
        try
        {
            var all = await StoredAsync();
            foreach (var key in all.Keys.Concat(memory.Keys).Where(k => k.StartsWith(server + "|")).Distinct().ToList())
            {
                all.Remove(key);
                memory.Remove(key);
            }
            await Json.WriteAtomicAsync(TokensFile, all);
        }
        finally
        {
            fileGate.Release();
        }
    }

    sealed class Cache(McpOAuth owner, string key) : ITokenCache
    {
        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken) => await owner.SetAsync(key, tokens, cancellationToken);

        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) => await owner.GetAsync(key);
    }

    // ブラウザでログインしてもらい、戻ってきた code を受け取る
    public async Task<AuthorizationResult?> AuthorizeAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        // 開く先はサーバーが返したもの。https 以外を渡すと、Windows がファイルやプログラムとして開いてしまう
        if (authorizationUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException($"ログイン画面の URL が https ではありません: {authorizationUri}");
        if (!await loginGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("ほかのコネクタのログインが終わってからやり直してください");
        try
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{CallbackPort}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                throw new InvalidOperationException($"ログインの戻り先 (ポート {CallbackPort}) をほかのアプリが使っています");
            }
            // ToString は %20 などを戻してしまい、ブラウザに渡す引数が崩れる
            OpenBrowser?.Invoke(authorizationUri.AbsoluteUri);
            var expectedState = HttpUtility.ParseQueryString(authorizationUri.Query)["state"];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LoginTimeout);
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                // ブラウザが favicon なども取りに来るので、戻り先以外は相手にしない
                if (context.Request.Url?.AbsolutePath != RedirectUri.AbsolutePath)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }
                var query = HttpUtility.ParseQueryString(context.Request.Url.Query);
                // ほかのページから戻り先を叩かれても、始めたログインの返事でなければ相手にせず待ち続ける
                if (expectedState is not null && query["state"] != expectedState)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }
                var failed = query["error"];
                var page = failed is null
                    ? "<h2>ClaudeChat にログインしました</h2><p>この画面を閉じて、アプリに戻ってください。</p>"
                    : $"<h2>ログインできませんでした</h2><p>{WebUtility.HtmlEncode(query["error_description"] ?? failed)}</p>";
                var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>ClaudeChat</title><body style=\"font-family:sans-serif;padding:40px\">{page}</body>");
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(body, timeout.Token);
                context.Response.Close();
                if (failed is not null || query["code"] is not { } code) return null;
                return new AuthorizationResult { Code = code, State = query["state"], Iss = query["iss"] };
            }
        }
        finally
        {
            loginGate.Release();
        }
    }
}
