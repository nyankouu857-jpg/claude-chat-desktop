using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ClaudeChat.App;

// 通知領域のアイコン。× でしまったあとの出し入れと、Windows の通知 (吹き出し。10 以降はトースト) を受け持つ
sealed partial class WindowsPlatform
{
    // アイコンからの知らせを窓に届けるメッセージ番号。WM_APP より後ならアプリが自由に使える
    const int TrayCallback = 0x8000 + 1;
    const int MenuOpen = 1;
    const int MenuExit = 2;

    NotifyIconData? icon;
    Action? onOpen;
    Action? onExit;
    // 吹き出しを押したときの動き。次の吹き出しで置き換わる
    Action? onBalloonClick;
    Window? window;

    public void ShowTrayIcon(Window owner, Action open, Action exit)
    {
        window = owner;
        onOpen = open;
        onExit = exit;
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        HwndSource.FromHwnd(handle)?.AddHook(OnMessage);
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = handle,
            uID = 1,
            uFlags = NifIcon | NifTip | NifMessage,
            uCallbackMessage = TrayCallback,
            hIcon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath ?? "", 0),
            szTip = "ClaudeChat",
            szInfo = "",
            szInfoTitle = "",
        };
        if (Shell_NotifyIcon(NimAdd, ref data)) icon = data;
    }

    public void RemoveTrayIcon()
    {
        if (icon is not { } data) return;
        Shell_NotifyIcon(NimDelete, ref data);
        icon = null;
    }

    public string? Notify(string title, string message, Action? onClick = null) => Application.Current.Dispatcher.Invoke(() =>
    {
        if (icon is not { } data) return "通知領域のアイコンがありません";
        data.uFlags = NifInfo;
        // Windows が切り捨てる長さ
        data.szInfo = Shorten(message, 255);
        data.szInfoTitle = Shorten(title, 63);
        data.dwInfoFlags = NiifInfo;
        onBalloonClick = onClick;
        return Shell_NotifyIcon(NimModify, ref data) ? null : "通知を出せませんでした";
    });

    // 別の窓で開いたチャットを見ているときも、見ていることにする
    public bool IsActive => Application.Current.Dispatcher.Invoke(() =>
        Application.Current.Windows.OfType<Window>().Any(w => w is { IsVisible: true, IsActive: true, WindowState: not WindowState.Minimized }));

    public void ShowWindow() => Application.Current.Dispatcher.Invoke(() => onOpen?.Invoke());

    IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != TrayCallback) return IntPtr.Zero;
        switch ((int)lParam)
        {
            case WmLButtonUp:
                onOpen?.Invoke();
                break;
            case NinBalloonUserClick:
                onOpen?.Invoke();
                onBalloonClick?.Invoke();
                break;
            case WmRButtonUp:
                ShowMenu(hwnd);
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, MenuOpen, "開く");
        AppendMenu(menu, 0, MenuExit, "終了");
        GetCursorPos(out var point);
        // 前に出しておかないと、メニューの外を押しても閉じない
        SetForegroundWindow(hwnd);
        var chosen = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (chosen == MenuOpen) onOpen?.Invoke();
        else if (chosen == MenuExit) onExit?.Invoke();
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClaudeChat";

    // ログオンしたら窓を出さずに起動し、予定を動かせるようにしておく
    public string? SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!enabled)
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
                return null;
            }
            if (Environment.ProcessPath is not { } exe) return "今の exe の場所が分かりません";
            key.SetValue(RunValue, $"\"{exe}\" {App.BackgroundFlag}");
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return $"自動起動を設定できませんでした: {e.Message}";
        }
    }

    static string Shorten(string text, int max) => text.Length > max ? text[..(max - 1)] + "…" : text;

    const int NimAdd = 0;
    const int NimModify = 1;
    const int NimDelete = 2;
    const int NifMessage = 0x1;
    const int NifIcon = 0x2;
    const int NifTip = 0x4;
    const int NifInfo = 0x10;
    const int NiifInfo = 0x1;
    const int WmLButtonUp = 0x0202;
    const int WmRButtonUp = 0x0205;
    const int NinBalloonUserClick = 0x0405;
    const uint TpmRightButton = 0x0002;
    const uint TpmReturnCmd = 0x0100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ExtractIcon(IntPtr instance, string path, int index);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, int id, string text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
}
