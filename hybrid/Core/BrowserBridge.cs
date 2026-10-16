using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

// いつもの Chrome に入れた拡張機能と話す窓口。Chrome は普段のプロフィールで外からの操作を受け付けないので、
// 拡張機能が chrome.debugger で DevTools プロトコルを中継する
public sealed class BrowserBridge(SettingsStore settings) : IAsyncDisposable
{
    // 拡張機能が探しに来る番号。変えるなら ChromeExtension の background.js も
    public const int Port = 47823;
    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    // 拡張機能の service worker は 30 秒何も届かないと止められる
    static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    const int MaxHeaderBytes = 8192;

    sealed class Connection(WebSocket socket, string version)
    {
        public WebSocket Socket { get; } = socket;
        public string Version { get; } = version;
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> Pending { get; } = new();
    }

    readonly Lock sync = new();
    TcpListener? listener;
    CancellationTokenSource? listening;
    Connection? current;
    int nextId;

    public event Action<string, JsonObject>? DialogOpened;
    public event Action<string>? Detached;

    public bool Connected => current?.Socket.State == WebSocketState.Open;
    public string? Version => current?.Version;
    // ほかのプログラムが同じ番号を使っていると待ち受けられない。アプリの起動までは止めない
    public string? ListenError { get; private set; }

    bool Wanted => settings.Current?.Browser is { Enabled: true, Mode: "extension" };

    // 設定で拡張機能を選んだときだけ待ち受ける。使わない人の PC でポートを開けない
    public async Task SyncAsync()
    {
        if (Wanted && settings.Current?.Browser.ExtensionToken is not { Length: > 0 })
        {
            await settings.UpdateAsync(s => s.Browser.ExtensionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
        }
        lock (sync)
        {
            if (Wanted && listener is null)
            {
                var server = new TcpListener(IPAddress.Loopback, Port);
                try
                {
                    server.Start();
                    ListenError = null;
                    listener = server;
                    listening = new CancellationTokenSource();
                    _ = AcceptLoopAsync(server, listening.Token);
                }
                catch (SocketException e)
                {
                    ListenError = $"{Port} 番で待ち受けられませんでした: {e.Message}";
                }
            }
            else if (!Wanted && listener is not null)
            {
                listening?.Cancel();
                listener.Stop();
                listener = null;
            }
        }
        if (!Wanted) await DropAsync(current);
    }

    async Task AcceptLoopAsync(TcpListener server, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = ServeAsync(client, cancellationToken);
        }
    }

    async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        WebSocket? socket = null;
        try
        {
            var headers = await ReadHeadersAsync(stream, cancellationToken);
            // ふつうの Web ページも localhost へ WebSocket をつなげる。Origin はページから偽れないので拡張機能だけ通す
            if (headers is null || !headers.TryGetValue("origin", out var origin) || !origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
                || !headers.TryGetValue("sec-websocket-key", out var key))
            {
                await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), cancellationToken);
                return;
            }
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), cancellationToken);
            socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            if (await HandshakeAsync(socket, cancellationToken) is not { } version) return;
            var connection = new Connection(socket, version);
            Connection? previous;
            lock (sync)
            {
                previous = current;
                current = connection;
            }
            await DropAsync(previous);
            using var pinging = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = PingLoopAsync(connection, pinging.Token);
            await ReceiveLoopAsync(connection, cancellationToken);
            pinging.Cancel();
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException)
        {
            // 拡張機能やアプリが閉じられた
        }
        finally
        {
            socket?.Dispose();
            client.Dispose();
        }
    }

    static async Task<Dictionary<string, string>?> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < MaxHeaderBytes)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0) return null;
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    // 接続コードを知っていることをお互いに示す。ほかのプログラムがこの番号で待ち受けて、いつもの Chrome を乗っ取れないように
    async Task<string?> HandshakeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var token = settings.Current?.Browser.ExtensionToken;
        if (token is not { Length: > 0 }) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var hello = await ReadMessageAsync(socket, timeout.Token);
        if ((string?)hello?["type"] != "hello" || (string?)hello["nonce"] is not { Length: > 0 } nonce) return null;
        await WriteAsync(socket, new JsonObject { ["type"] = "welcome", ["proof"] = Proof(token, "app:" + nonce) }, cancellationToken);
        var ready = await ReadMessageAsync(socket, timeout.Token);
        if ((string?)ready?["type"] != "ready" || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes((string?)ready["proof"] ?? ""), Encoding.ASCII.GetBytes(Proof(token, "ext:" + nonce)))) return null;
        return (string?)hello["version"] ?? "";
    }

    static string Proof(string token, string text) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    async Task PingLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
            {
                await Task.Delay(PingInterval, cancellationToken);
                await SendAsync(connection, new JsonObject { ["type"] = "ping" }, cancellationToken);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException)
        {
            // 切れた
        }
    }

    async Task ReceiveLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (connection.Socket.State == WebSocketState.Open)
            {
                if (await ReadMessageAsync(connection.Socket, cancellationToken) is not { } message) break;
                if ((string?)message["event"] is { } name)
                {
                    var tabId = (string?)message["tabId"] ?? "";
                    if (name == "dialog") DialogOpened?.Invoke(tabId, message["params"] as JsonObject ?? []);
                    else if (name == "detached") Detached?.Invoke(tabId);
                    continue;
                }
                if ((int?)message["id"] is not { } id || !connection.Pending.TryRemove(id, out var completion)) continue;
                if ((string?)message["error"] is { } error) completion.TrySetException(new ToolException($"ブラウザの操作に失敗しました: {error}"));
                else completion.TrySetResult(message["result"]?.DeepClone());
            }
        }
        finally
        {
            foreach (var completion in connection.Pending.Values) completion.TrySetException(new ToolException("いつもの Chrome との接続が切れました。Chrome が閉じられたか、拡張機能が止まった可能性があります"));
            connection.Pending.Clear();
            lock (sync)
            {
                if (current == connection) current = null;
            }
        }
    }

    static async Task<JsonObject?> ReadMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonNode.Parse(message.ToArray()) as JsonObject;
        }
    }

    static Task WriteAsync(WebSocket socket, JsonObject message, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cancellationToken);

    static async Task SendAsync(Connection connection, JsonObject message, CancellationToken cancellationToken)
    {
        await connection.SendGate.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(connection.Socket, message, cancellationToken);
        }
        finally
        {
            connection.SendGate.Release();
        }
    }

    public async Task<JsonNode?> CallAsync(string command, JsonObject arguments, CancellationToken cancellationToken)
    {
        var connection = current is { Socket.State: WebSocketState.Open } open ? open : throw NotConnected();
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Pending[id] = completion;
        arguments["id"] = id;
        arguments["cmd"] = command;
        try
        {
            await SendAsync(connection, arguments, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
            return await completion.Task;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolException($"ブラウザが {CommandTimeout.TotalSeconds:0} 秒応答しませんでした");
        }
        catch (WebSocketException)
        {
            throw NotConnected();
        }
        finally
        {
            connection.Pending.TryRemove(id, out _);
        }
    }

    // 接続コードを作り直したら、古いコードでつながっている拡張機能を切る
    public Task DisconnectAsync() => DropAsync(current);

    public static ToolException NotConnected() =>
        new("いつもの Chrome とつながっていません。Chrome が開いていて、ClaudeChat 連携の拡張機能に接続コードを設定してあるか確かめてください (設定の「PC 操作」)");

    static async Task DropAsync(Connection? connection)
    {
        if (connection is null) return;
        try
        {
            await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            // 先に切れていた
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            listening?.Cancel();
            listener?.Stop();
            listener = null;
        }
        await DropAsync(current);
    }
}

// 拡張機能を通して、いつもの Chrome の 1 つのタブを動かす
public sealed class ExtensionPage : IPageChannel
{
    readonly BrowserBridge bridge;
    readonly string tabId;
    bool detached;
    string? dialogNote;

    ExtensionPage(BrowserBridge bridge, string tabId)
    {
        this.bridge = bridge;
        this.tabId = tabId;
        bridge.DialogOpened += OnDialog;
        bridge.Detached += OnDetached;
    }

    public static async Task<ExtensionPage> AttachAsync(BrowserBridge bridge, string tabId, CancellationToken cancellationToken)
    {
        await bridge.CallAsync("attach", new JsonObject { ["tabId"] = tabId }, cancellationToken);
        return new ExtensionPage(bridge, tabId);
    }

    public bool IsOpen => !detached && bridge.Connected;

    public string? TakeDialogNote() => Interlocked.Exchange(ref dialogNote, null);

    public async Task<JsonObject> SendAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var result = await bridge.CallAsync("send", new JsonObject { ["tabId"] = tabId, ["method"] = method, ["params"] = parameters ?? new JsonObject() }, cancellationToken);
        return result as JsonObject ?? [];
    }

    void OnDialog(string tab, JsonObject parameters)
    {
        // 受信ループの中で応答を待つと止まるので、待たずに投げる
        if (tab == tabId) _ = Task.Run(async () => dialogNote = await CdpSession.DismissDialogAsync(this, parameters));
    }

    void OnDetached(string tab)
    {
        if (tab == tabId) detached = true;
    }

    public async ValueTask DisposeAsync()
    {
        bridge.DialogOpened -= OnDialog;
        bridge.Detached -= OnDetached;
        if (detached || !bridge.Connected) return;
        try
        {
            // デバッグ中の帯を消して、ユーザーがふつうに使える状態に戻す
            await bridge.CallAsync("detach", new JsonObject { ["tabId"] = tabId }, CancellationToken.None);
        }
        catch (ToolException)
        {
            // 先に切れていた
        }
    }
}
