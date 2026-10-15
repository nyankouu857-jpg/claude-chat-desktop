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
    int nextId;
    Task? receiveLoop;

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
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
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
