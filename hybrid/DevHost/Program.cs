using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ClaudeChat.Core;

// Windows 以外でも画面と中身を通しで動かすための開発用ホスト。配布物には含めない
var options = ParseArgs(args);
var webRoot = Path.GetFullPath(options.GetValueOrDefault("web", Path.Combine("..", "web", "dist")));
var port = int.Parse(options.GetValueOrDefault("port", "5180"));
var paths = AppPaths.Resolve(Path.GetFullPath(options.GetValueOrDefault("data", Path.Combine("..", ".devdata"))));

var sockets = new List<(WebSocket Socket, Channel<string> Outbox)>();

// 通知の順番が入れ替わるとストリーミング表示が崩れるので、ソケットごとに1本の送信列で順に送る
void Broadcast(string message)
{
    lock (sockets)
    {
        foreach (var (_, outbox) in sockets) outbox.Writer.TryWrite(message);
    }
}

async Task PumpAsync(WebSocket socket, Channel<string> outbox)
{
    await foreach (var message in outbox.Reader.ReadAllAsync())
    {
        if (socket.State != WebSocketState.Open) break;
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}

await using var host = new AppHost(paths, new PlainSecretProtector(), new ConsolePlatform(Path.Combine(paths.DataDir, "trash")), Broadcast);
await host.StartAsync();

var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{port}/");
listener.Start();
Console.WriteLine($"http://localhost:{port}/ で待ち受けています。データ: {paths.Root}");

while (true)
{
    var context = await listener.GetContextAsync();
    _ = Task.Run(() => HandleAsync(context));
}

async Task HandleAsync(HttpListenerContext context)
{
    if (context.Request.IsWebSocketRequest && context.Request.Url?.AbsolutePath == "/ws")
    {
        // ほかのサイトのページから ws://localhost に繋がれると、ツールやコネクタを勝手に動かされる
        var origin = context.Request.Headers["Origin"];
        if (origin != $"http://localhost:{port}" && origin != $"http://127.0.0.1:{port}")
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }
        var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        var entry = (socket, Channel.CreateUnbounded<string>());
        lock (sockets) sockets.Add(entry);
        _ = PumpAsync(socket, entry.Item2);
        try
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                _ = host.HandleAsync(text);
            }
        }
        catch (WebSocketException)
        {
            // ブラウザを閉じただけ
        }
        finally
        {
            lock (sockets) sockets.Remove(entry);
            entry.Item2.Writer.TryComplete();
        }
        return;
    }

    var relative = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath.TrimStart('/') ?? "");
    if (AppHost.IsArtifactPath(relative))
    {
        var html = await host.RenderArtifactAsync(relative);
        context.Response.StatusCode = html is null ? 404 : 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers["Content-Security-Policy"] = AppHost.ArtifactCsp;
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html ?? ""));
        context.Response.Close();
        return;
    }
    var file = Path.GetFullPath(Path.Combine(webRoot, relative.Length == 0 ? "index.html" : relative));
    // 区切りまで見ないと、隣の dist-old のようなフォルダも読めてしまう
    if (!file.StartsWith(webRoot + Path.DirectorySeparatorChar) || !File.Exists(file)) file = Path.Combine(webRoot, "index.html");
    context.Response.ContentType = ContentTypes.Of(file);
    // ほかのサイトに枠で埋め込まれて、押し間違いを誘われないように
    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
    var bytes = await File.ReadAllBytesAsync(file);
    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>();
    for (var i = 0; i + 1 < args.Length; i += 2) result[args[i].TrimStart('-')] = args[i + 1];
    return result;
}

sealed class ConsolePlatform(string trashDir) : IPlatform
{
    public string Version => "dev";
    public void OpenPath(string path) => Console.WriteLine($"開く: {path}");

    // ブラウザからはフォルダ選択ダイアログを出せない
    public string? PickFolder() => null;

    // 画面の無いところで動くので、本物のマウスは無い
    public bool CanRealClick => false;
    public string? RealClick(string titleMarker, double fx, double fy) => "この環境では本物のマウスでのクリックは使えません";
    public string? CreateShortcuts() => "この環境ではショートカットを作れません";

    // ごみ箱の代わり
    public void MoveToTrash(string path)
    {
        Directory.CreateDirectory(trashDir);
        var destination = Path.Combine(trashDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(path)}");
        if (Directory.Exists(path)) Directory.Move(path, destination);
        else File.Move(path, destination);
    }
}
