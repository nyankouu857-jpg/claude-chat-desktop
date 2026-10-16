using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace ClaudeChat.App;

// 会話の書き出しを PDF にする。見えない所に WebView を置いて読み込み、印刷の代わりに PDF に書く
sealed partial class WindowsPlatform
{
    public Func<CoreWebView2Environment?>? WebViewEnvironment { get; set; }

    // アーティファクトの中の描画が落ち着くのを待つ
    static readonly TimeSpan RenderSettle = TimeSpan.FromSeconds(1.5);
    static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

    public async Task<string?> HtmlToPdfAsync(string htmlPath, string pdfPath)
    {
        var dispatcher = Application.Current.Dispatcher;
        var operation = await dispatcher.InvokeAsync(async () =>
        {
            if (WebViewEnvironment?.Invoke() is not { } environment) return "まだ起動の途中です";
            var browser = new Microsoft.Web.WebView2.Wpf.WebView2();
            // 画面の外に出す。WebView は窓に載っていないと動かない
            var window = new Window { Width = 900, Height = 1200, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Content = browser };
            window.Show();
            try
            {
                await browser.EnsureCoreWebView2Async(environment);
                var core = browser.CoreWebView2;
                var loaded = new TaskCompletionSource<bool>();
                core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                core.Navigate(new Uri(htmlPath).AbsoluteUri);
                if (!await loaded.Task.WaitAsync(LoadTimeout)) return "書き出した HTML を読み込めませんでした";
                await Task.Delay(RenderSettle);
                var settings = environment.CreatePrintSettings();
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                return await core.PrintToPdfAsync(pdfPath, settings) ? null : "PDF を書き出せませんでした";
            }
            finally
            {
                window.Close();
                browser.Dispose();
            }
        });
        return await operation;
    }
}
