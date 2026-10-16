using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClaudeChat.App;

// 通知領域のアイコンの吹き出しで知らせる。Windows 10 以降はトースト通知として出て、通知センターにも残る
sealed partial class WindowsPlatform
{
    // 吹き出しが出てから消えるまで、アイコンを残しておく長さ
    static readonly TimeSpan IconLifetime = TimeSpan.FromSeconds(20);
    DispatcherTimer? removeTimer;
    NotifyIconData? shown;

    public string? Notify(string title, string message) => Application.Current.Dispatcher.Invoke(() =>
    {
        if (Application.Current.MainWindow is not { } window) return "通知を出す窓がありません";
        Remove();
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = new WindowInteropHelper(window).Handle,
            uID = 1,
            uFlags = NifIcon | NifTip | NifInfo,
            hIcon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath ?? "", 0),
            szTip = "ClaudeChat",
            // Windows が切り捨てる長さ
            szInfo = Shorten(message, 255),
            szInfoTitle = Shorten(title, 63),
            dwInfoFlags = NiifInfo,
        };
        if (!Shell_NotifyIcon(NimAdd, ref data)) return "通知を出せませんでした";
        shown = data;
        removeTimer = new DispatcherTimer { Interval = IconLifetime };
        removeTimer.Tick += (_, _) => Remove();
        removeTimer.Start();
        return null;
    });

    void Remove()
    {
        removeTimer?.Stop();
        removeTimer = null;
        if (shown is not { } data) return;
        Shell_NotifyIcon(NimDelete, ref data);
        shown = null;
    }

    static string Shorten(string text, int max) => text.Length > max ? text[..(max - 1)] + "…" : text;

    const int NimAdd = 0;
    const int NimDelete = 2;
    const int NifIcon = 0x2;
    const int NifTip = 0x4;
    const int NifInfo = 0x10;
    const int NiifInfo = 0x1;

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
}
