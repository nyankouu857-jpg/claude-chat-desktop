using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClaudeChat.App;

// Win + Shift + S と同じ「切り取り領域」で範囲を選ばせ、クリップボードに入った画像を受け取る。
// 範囲選びの画面は Windows に任せるので、複数のモニターや拡大率の違いもそのまま扱える
sealed partial class WindowsPlatform
{
    // 切り取った画像が入るのを確かめる間隔
    static readonly TimeSpan ClipboardPoll = TimeSpan.FromMilliseconds(300);

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    // 別の窓で開いたチャット。会話の ID と窓を作る処理は MainWindow が渡す
    public Func<string, string?>? ChatWindowOpener { get; set; }

    public string? OpenChatWindow(string conversationId) =>
        Application.Current.Dispatcher.Invoke(() => ChatWindowOpener?.Invoke(conversationId) ?? "別の窓を開けませんでした");

    public async Task<string?> CaptureScreenAsync(CancellationToken cancellationToken)
    {
        var before = GetClipboardSequenceNumber();
        // 自分の窓が写り込まないよう、選んでいる間はすべて下げておく。戻すときは元の大きさに戻す
        var lowered = Application.Current.Dispatcher.Invoke(() =>
        {
            var shown = Application.Current.Windows.OfType<Window>().Where(w => w is { IsVisible: true, WindowState: not WindowState.Minimized }).Select(w => (Window: w, State: w.WindowState, Active: w.IsActive)).ToList();
            foreach (var (w, _, _) in shown) w.WindowState = WindowState.Minimized;
            return shown;
        });
        try
        {
            // 最小化のアニメーションが終わる前に始めると、窓が写り込む
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            var clipperSeen = false;
            while (true)
            {
                await Task.Delay(ClipboardPoll, cancellationToken);
                var now = GetClipboardSequenceNumber();
                if (now == before)
                {
                    // Esc で選ぶのをやめると切り取りの画面が閉じる。その後に別の画像をコピーしても拾わない
                    var clipperRunning = Process.GetProcessesByName(ClipperProcess).Length > 0;
                    if (clipperRunning) clipperSeen = true;
                    else if (clipperSeen) return null;
                    continue;
                }
                var (busy, png) = Application.Current.Dispatcher.Invoke(ReadClipboardPng);
                // ほかのアプリが開いている間は読めないので、同じ変化をもう一度読みに行く
                if (busy) continue;
                before = now;
                // 撮る前にほかのアプリが文字をコピーしても、画像が来るまで待ち続ける
                if (png is not null) return png;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"画面の切り取りを起動できませんでした: {e.Message}");
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var (w, state, _) in lowered) w.WindowState = state;
                lowered.FirstOrDefault(l => l.Active).Window?.Activate();
            });
        }
    }

    // 「切り取り領域」の画面を出しているプロセス
    const string ClipperProcess = "ScreenClippingHost";

    static (bool Busy, string? Png) ReadClipboardPng()
    {
        try
        {
            if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image) return (false, null);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return (false, Convert.ToBase64String(stream.ToArray()));
        }
        catch (COMException)
        {
            return (true, null);
        }
    }
}
