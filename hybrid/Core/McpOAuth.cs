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

    static readonly SemaphoreSlim fileGate = new(1, 1);
    // 同じポートで 2 つの待ち受けはできないので、ログインは 1 つずつ
    static readonly SemaphoreSlim loginGate = new(1, 1);

    string TokensFile => Path.Combine(paths.ConfigDir, "mcp-tokens.json");
    ISecretProtector Protector => protector;

    public Action<string>? OpenBrowser { get; set; }

    // URL を変えたら別のサーバーとして、ログインし直してもらう
    static string KeyOf(string server, string url) => $"{server}|{url}";

    async Task<Dictionary<string, string>> LoadAsync()
    {
        try
        {
            return await Json.ReadAsync<Dictionary<string, string>>(TokensFile) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public ITokenCache CacheFor(string server, string url) => new Cache(this, KeyOf(server, url));

    public async Task<bool> HasTokensAsync(string server, string url) => (await LoadAsync()).ContainsKey(KeyOf(server, url));

    public async Task ForgetAsync(string server)
    {
        await fileGate.WaitAsync();
        try
        {
            var all = await LoadAsync();
            foreach (var key in all.Keys.Where(k => k.StartsWith(server + "|")).ToList()) all.Remove(key);
            await Json.WriteAtomicAsync(TokensFile, all);
        }
        finally
        {
            fileGate.Release();
        }
    }

    sealed class Cache(McpOAuth owner, string key) : ITokenCache
    {
        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            await fileGate.WaitAsync(cancellationToken);
            try
            {
                var all = await owner.LoadAsync();
                all[key] = owner.Protector.Protect(JsonSerializer.Serialize(tokens));
                await Json.WriteAtomicAsync(owner.TokensFile, all);
            }
            finally
            {
                fileGate.Release();
            }
        }

        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            var all = await owner.LoadAsync();
            if (!all.TryGetValue(key, out var stored)) return null;
            try
            {
                return JsonSerializer.Deserialize<TokenContainer>(owner.Protector.Unprotect(stored));
            }
            catch (Exception)
            {
                // 別の PC やユーザーで暗号化したものは読めない。ログインし直してもらう
                return null;
            }
        }
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
            OpenBrowser?.Invoke(authorizationUri.ToString());
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
