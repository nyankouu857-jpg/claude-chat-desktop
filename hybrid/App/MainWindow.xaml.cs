using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClaudeChat.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace ClaudeChat.App;

public partial class MainWindow : Window
{
    // 実在しないドメインにして、外部のサイトと混ざらないようにする
    const string AppOrigin = "https://app.claudechat";

    readonly AppPaths paths;
    readonly Dictionary<string, string> resources;
    AppHost? host;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow(AppPaths paths)
    {
        this.paths = paths;
        // MSBuild が付ける埋め込み名は区切りが \ になることがあるので / にそろえて引く
        resources = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("web/"))
            .ToDictionary(n => n["web/".Length..].Replace('\\', '/'), n => n);
        InitializeComponent();
        var dark = PrefersDarkTheme();
        Background = new SolidColorBrush(dark ? Color.FromRgb(0x26, 0x26, 0x24) : Color.FromRgb(0xFA, 0xF9, 0xF5));
        Browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x26, 0x26, 0x24) : System.Drawing.Color.FromArgb(0xFA, 0xF9, 0xF5);
        SourceInitialized += (_, _) => UseDarkTitleBar(dark);
        Loaded += async (_, _) => await InitializeAsync();
        Closing += async (_, e) =>
        {
            if (host is null) return;
            // MCP サーバーの子プロセスを残さないよう、片付け終わってから閉じ直す
            e.Cancel = true;
            var closing = host;
            host = null;
            await closing.DisposeAsync();
            Close();
        };
    }

    static bool PrefersDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    void UseDarkTitleBar(bool dark)
    {
        var value = dark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref value, sizeof(int));
    }

    async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(paths.CacheDir, "webview2"));
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (MessageBox.Show("画面の表示に必要な Microsoft Edge WebView2 ランタイムが見つかりません。ダウンロードページを開きますか?", "ClaudeChat", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                WindowsPlatform.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
            }
            Close();
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.AddWebResourceRequestedFilter($"{AppOrigin}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = Serve(core, e.Request.Uri);

        // 回答中のリンクはアプリ内で開かず、既定のブラウザに渡す
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Uri.StartsWith("http://") || e.Uri.StartsWith("https://")) WindowsPlatform.OpenUrl(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            // 前方一致だと app.claudechat.evil.example も通ってしまう
            if (IsAppPage(e.Uri)) return;
            e.Cancel = true;
            if (e.Uri.StartsWith("http://") || e.Uri.StartsWith("https://")) WindowsPlatform.OpenUrl(e.Uri);
        };

        host = new AppHost(paths, new DpapiProtector(), new WindowsPlatform(), json =>
        {
            // UI スレッドのキューは先入れ先出しなので、通知の順番が保たれる
            Dispatcher.InvokeAsync(() => core.PostWebMessageAsString(json));
        });
        core.WebMessageReceived += (_, e) =>
        {
            // アプリの画面以外から中身を操作させない
            if (!IsAppPage(e.Source)) return;
            var message = e.TryGetWebMessageAsString();
            // 重い処理で画面が固まらないよう、受け取ったら UI スレッドから離す
            if (host is { } current) _ = Task.Run(() => current.HandleAsync(message));
        };
        await host.StartAsync();
        core.Navigate($"{AppOrigin}/index.html");
    }

    static bool IsAppPage(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && parsed.Host == new Uri(AppOrigin).Host;

    CoreWebView2WebResourceResponse Serve(CoreWebView2 core, string uri)
    {
        var path = Uri.UnescapeDataString(new Uri(uri).AbsolutePath.TrimStart('/'));
        if (path.Length == 0) path = "index.html";
        if (!resources.TryGetValue(path, out var name))
        {
            return core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        return core.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {ContentTypes.Of(path)}");
    }
}
