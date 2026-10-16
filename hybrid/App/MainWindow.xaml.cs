using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
    readonly WindowsPlatform platform;
    readonly Dictionary<string, string> resources;
    AppHost? host;
    CoreWebView2Environment? environment;
    // 画面を出している WebView。別の窓で開いたチャットにも同じ知らせを流す
    readonly List<CoreWebView2> views = [];
    // トレイの「終了」や更新など、本当に終わらせるとき。× はトレイにしまうだけ
    bool exiting;
    // 片付けを待つ上限。止まったものを待ち続けて、閉じたはずのアプリを残さない
    static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal MainWindow(AppPaths paths, WindowsPlatform platform, bool background)
    {
        this.paths = paths;
        this.platform = platform;
        // MSBuild が付ける埋め込み名は区切りが \ になることがあるので / にそろえて引く
        resources = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("web/"))
            .ToDictionary(n => n["web/".Length..].Replace('\\', '/'), n => n);
        InitializeComponent();
        var dark = PrefersDarkTheme();
        Background = new SolidColorBrush(dark ? Color.FromRgb(0x26, 0x26, 0x24) : Color.FromRgb(0xFA, 0xF9, 0xF5));
        Browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x26, 0x26, 0x24) : System.Drawing.Color.FromArgb(0xFA, 0xF9, 0xF5);
        SourceInitialized += (_, _) => UseDarkTitleBar(dark);
        if (background)
        {
            // 予定を動かすために画面も読み込むが、窓は見せない
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
        Loaded += async (_, _) =>
        {
            platform.ShowTrayIcon(this, BringToFront, Exit);
            await InitializeAsync();
            if (!background) return;
            Hide();
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
        };
        Closing += async (_, e) =>
        {
            if (!exiting && host?.CloseToTray == true)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            platform.RemoveTrayIcon();
            if (host is null) return;
            // MCP サーバーの子プロセスを残さないよう、片付けてから終わる
            e.Cancel = true;
            var closing = host;
            host = null;
            // Closing の中では隠すことも閉じ直すこともできない。片付けがすぐ終わっても、いったんここを抜けてから続ける
            await Dispatcher.InvokeAsync(Hide);
            try
            {
                await closing.DisposeAsync().AsTask().WaitAsync(CleanupTimeout);
            }
            catch (Exception)
            {
                // 片付けに失敗しても、終わるのは止めない
            }
            // × はアプリの終了。裏で動き続けるスレッドが残っていても、プロセスごと終わらせる
            Environment.Exit(0);
        };
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void Exit()
    {
        exiting = true;
        Close();
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
            environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(paths.CacheDir, "webview2"));
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
        Configure(core);
        views.Add(core);

        host = new AppHost(paths, new DpapiProtector(), platform, json =>
        {
            // UI スレッドのキューは先入れ先出しなので、通知の順番が保たれる。返事も全部の窓に流し、ID の合う窓だけが受け取る
            Dispatcher.InvokeAsync(() =>
            {
                foreach (var view in views.ToList())
                {
                    try
                    {
                        view.PostWebMessageAsString(json);
                    }
                    catch (Exception)
                    {
                        // 中の WebView が落ちた窓には送らない。ほかの窓への知らせは止めない
                        views.Remove(view);
                    }
                }
            });
        });
        platform.ChatWindowOpener = OpenChatWindow;
        await host.StartAsync();
        core.Navigate($"{AppOrigin}/index.html");
    }

    // メインの窓と別の窓の WebView に、同じ制限と通り道を付ける
    void Configure(CoreWebView2 core)
    {
        core.Settings.IsStatusBarEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        // 配る版では開発者ツールから画面の中身や接続コードを触らせない
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.AddWebResourceRequestedFilter($"{AppOrigin}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += async (_, e) =>
        {
            var path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'));
            if (!AppHost.IsArtifactPath(path) || host is not { } current)
            {
                e.Response = Serve(core, e.Request.Uri);
                return;
            }
            // 会話の読み込みを待つ間、応答を保留する
            var deferral = e.GetDeferral();
            try
            {
                var html = await current.RenderArtifactAsync(path);
                e.Response = html is null
                    ? core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                    : core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", $"Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: {AppHost.ArtifactCsp}");
            }
            finally
            {
                deferral.Complete();
            }
        };

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
        core.WebMessageReceived += (_, e) =>
        {
            // アプリの画面以外から中身を操作させない
            if (!IsAppPage(e.Source)) return;
            var message = e.TryGetWebMessageAsString();
            // 重い処理で画面が固まらないよう、受け取ったら UI スレッドから離す
            if (host is { } current) _ = Task.Run(() => current.HandleAsync(message));
        };
    }

    // 1 つの会話だけを出す窓。サイドバーは出さず、知らせはメインの窓と同じものを受け取る
    string? OpenChatWindow(string conversationId)
    {
        if (environment is null || host is null) return "まだ起動の途中です";
        if (!Guid.TryParse(conversationId, out _)) return "チャットの ID が不正です";
        var browser = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = Browser.DefaultBackgroundColor };
        var chatWindow = new Window { Title = "ClaudeChat", Width = 900, Height = 820, Content = browser, Background = Background, Icon = Icon };
        CoreWebView2? core = null;
        var closed = false;
        chatWindow.Loaded += async (_, _) =>
        {
            await browser.EnsureCoreWebView2Async(environment);
            // 用意ができる前に閉じられた窓は、捨てた WebView に触らない
            if (closed) return;
            core = browser.CoreWebView2;
            Configure(core);
            views.Add(core);
            core.Navigate($"{AppOrigin}/index.html?chat={conversationId}");
        };
        chatWindow.Closed += (_, _) =>
        {
            closed = true;
            if (core is not null) views.Remove(core);
            browser.Dispose();
        };
        chatWindow.Show();
        return null;
    }

    // アーティファクトのページが上の窓に読み込まれると、画面と同じ扱いで中身を操作できてしまう。画面そのものだけを通す
    static bool IsAppPage(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && parsed.Host == new Uri(AppOrigin).Host
        && parsed.AbsolutePath is "/" or "/index.html";

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
