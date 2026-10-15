using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeChat.Core;

// Chrome DevTools Protocol の最小限のクライアント。1 つのタブにつなぐ
public sealed class CdpSession : IAsyncDisposable
{
    readonly ClientWebSocket socket = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    readonly SemaphoreSlim sendGate = new(1, 1);
    readonly CancellationTokenSource closing = new();
    // alert や confirm が出たままだと CDP の応答が返らない
    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    int nextId;
    Task? receiveLoop;
    string? dialogNote;

    // 自動で閉じたダイアログの説明。1 回読むと消える
    public string? TakeDialogNote() => Interlocked.Exchange(ref dialogNote, null);

    public bool IsOpen => socket.State == WebSocketState.Open;

    public static async Task<CdpSession> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        var session = new CdpSession();
        // 画面全体のスクリーンショットなど大きな応答があるため
        session.socket.Options.SetBuffer(1024 * 1024, 64 * 1024);
        await session.socket.ConnectAsync(url, cancellationToken);
        session.receiveLoop = Task.Run(session.ReceiveAsync);
        return session;
    }

    public async Task<JsonObject> SendAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        try
        {
            return await completion.Task;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolException($"ブラウザが {CommandTimeout.TotalSeconds:0} 秒応答しませんでした");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    // 確認ダイアログは承認せずにキャンセルする。削除などをユーザーの知らないところで確定させない
    async Task HandleDialogAsync(JsonObject parameters)
    {
        var type = (string?)parameters["type"];
        var accept = type is "alert" or "beforeunload";
        dialogNote = $"{type} ダイアログ「{(string?)parameters["message"]}」を{(accept ? "閉じました" : "キャンセルしました")}";
        try
        {
            await SendAsync("Page.handleJavaScriptDialog", new JsonObject { ["accept"] = accept }, CancellationToken.None);
        }
        catch (ToolException)
        {
            // 先に閉じられていた
        }
    }

    async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, closing.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var json = JsonNode.Parse(message.ToArray()) as JsonObject;
                message.SetLength(0);
                if ((string?)json?["method"] == "Page.javascriptDialogOpening")
                {
                    // 受信ループの中で応答を待つと止まるので、待たずに投げる
                    _ = HandleDialogAsync(json!["params"] as JsonObject ?? []);
                    continue;
                }
                if ((int?)json?["id"] is not { } id || !pending.TryRemove(id, out var completion)) continue;
                if (json["error"] is JsonObject error) completion.TrySetException(new ToolException($"ブラウザの操作に失敗しました: {(string?)error["message"]}"));
                else completion.TrySetResult(json["result"] as JsonObject ?? []);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // タブやブラウザが閉じられた
        }
        foreach (var completion in pending.Values) completion.TrySetException(new ToolException("ブラウザとの接続が切れました。タブかブラウザが閉じられた可能性があります"));
        pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                // 受信ループが閉じる応答を受け取って抜ける
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // 相手が先に閉じていた
            }
        }
        // 応答が来ないときの待ち時間
        closing.CancelAfter(TimeSpan.FromSeconds(2));
        if (receiveLoop is not null) await receiveLoop;
        socket.Dispose();
        closing.Dispose();
    }
}
