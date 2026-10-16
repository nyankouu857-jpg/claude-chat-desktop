using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;

namespace ClaudeChat.App;

// Windows の題名の帯と枠を消し、画面の端までアプリの色にする。─ ☐ ✕ と、つかんで動かす所は画面が描く。
// 端を引っ張っての大きさ変更、影、角の丸み、スナップは Windows のものをそのまま使う
static class Frameless
{
    sealed class State
    {
        public bool Fullscreen;
        public bool WasMaximized;
        public CoreWebView2? View;
    }

    static readonly Dictionary<Window, State> states = [];

    const int WmGetMinMaxInfo = 0x0024;
    const int WmNcLButtonDown = 0x00A1;
    const int HtCaption = 2;
    const int MonitorDefaultToNearest = 2;
    // DWMWA_WINDOW_CORNER_PREFERENCE と DWMWCP_ROUND。Windows 11 で角を丸くする
    const int CornerPreference = 33;
    const int RoundCorners = 2;

    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        states[window] = new State();
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            // 下に 1 だけ残すと、Windows が枠の影を描き続ける
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var round = RoundCorners;
            DwmSetWindowAttribute(handle, CornerPreference, ref round, sizeof(int));
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WmGetMinMaxInfo) FitToMonitor(window, hwnd, lParam);
                return IntPtr.Zero;
            });
        };
        window.StateChanged += (_, _) => Report(window);
        window.Closed += (_, _) => states.Remove(window);
    }

    // 画面が描いた ─ ☐ ✕ や、つかんだ所の知らせを受け取る WebView
    public static void Attach(Window window, CoreWebView2 view)
    {
        if (!states.TryGetValue(window, out var state)) return;
        state.View = view;
        view.NavigationCompleted += (_, _) => Report(window);
    }

    // 枠の無い窓を最大化すると、枠の分だけ画面の外へはみ出してタスクバーにも重なる。作業領域に収める
    static void FitToMonitor(Window window, IntPtr hwnd, IntPtr lParam)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info)) return;
        var area = states.TryGetValue(window, out var state) && state.Fullscreen ? info.Monitor : info.Work;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new Point { X = area.Left - info.Monitor.Left, Y = area.Top - info.Monitor.Top };
        limits.MaxSize = new Point { X = area.Right - area.Left, Y = area.Bottom - area.Top };
        Marshal.StructureToPtr(limits, lParam, true);
    }

    public static void Command(Window window, string action)
    {
        if (!states.TryGetValue(window, out var state)) return;
        switch (action)
        {
            case "minimize":
                window.WindowState = WindowState.Minimized;
                break;
            case "maximize":
                if (state.Fullscreen) ToggleFullscreen(window, state);
                else window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case "fullscreen":
                ToggleFullscreen(window, state);
                break;
            case "close":
                window.Close();
                break;
            case "drag":
                // 画面の CSS で動かせなかったときの予備。題名の帯をつかんだのと同じにする
                if (state.Fullscreen) return;
                ReleaseCapture();
                SendMessage(new WindowInteropHelper(window).Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
                break;
        }
    }

    // タスクバーも隠して画面いっぱいに出す。最大化し直すと、作業領域ではなく画面全体に合わせる
    static void ToggleFullscreen(Window window, State state)
    {
        if (!state.Fullscreen)
        {
            state.WasMaximized = window.WindowState == WindowState.Maximized;
            state.Fullscreen = true;
            window.WindowState = WindowState.Normal;
            window.WindowState = WindowState.Maximized;
        }
        else
        {
            state.Fullscreen = false;
            window.WindowState = WindowState.Normal;
            if (state.WasMaximized) window.WindowState = WindowState.Maximized;
        }
        Report(window);
    }

    static void Report(Window window)
    {
        if (!states.TryGetValue(window, out var state) || state.View is not { } view) return;
        var message = new JsonObject
        {
            ["event"] = "window.state",
            ["data"] = new JsonObject { ["maximized"] = window.WindowState == WindowState.Maximized, ["fullscreen"] = state.Fullscreen },
        };
        try
        {
            view.PostWebMessageAsString(message.ToJsonString());
        }
        catch (Exception)
        {
            // 閉じかけの窓には届かなくてよい
        }
    }

    // 画面から窓の操作だけを受け取る。ほかの知らせは AppHost に渡す
    public static bool TryHandle(Window window, string message)
    {
        if (!message.StartsWith("{\"window\":")) return false;
        try
        {
            if (JsonNode.Parse(message)?["window"]?.GetValue<string>() is { } action) Command(window, action);
        }
        catch (Exception)
        {
            // 形の崩れた知らせは無視する
        }
        return true;
    }
}
