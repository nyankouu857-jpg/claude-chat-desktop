using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeChat.App;

// ブラウザの中への操作で反応しないページのために、画面上の本物のマウスで押す
sealed partial class WindowsPlatform
{
    // Chrome が窓の題名を新しいタブの題名に変えるまでの待ち
    static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(2);
    static readonly string[] BrowserProcesses = ["chrome", "msedge"];

    public bool CanRealClick => true;

    public string? RealClick(string titleMarker, double fx, double fy)
    {
        if (fx is < 0 or > 1 || fy is < 0 or > 1) return "押す位置がページの表示領域の外です。スクロールしてからやり直してください";
        // 拡大率の違うモニターでも、窓の位置とカーソルの位置を同じ物差しで測る
        var previous = SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
        try
        {
            var window = FindBrowserWindow(titleMarker);
            if (window == IntPtr.Zero) return "操作中のタブのブラウザの窓が見つかりませんでした";
            if (IsIconic(window)) ShowWindow(window, SwRestore);
            // 前面に出せないまま押すと、上に重なったほかの窓を押してしまう
            if (!BringToFront(window)) return "ブラウザの窓を前面に出せませんでした";
            var view = FindPageView(window);
            if (view == IntPtr.Zero || !GetWindowRect(view, out var rect)) return "ページの表示領域が見つかりませんでした";
            var point = new Point { X = rect.Left + (int)Math.Round((rect.Right - rect.Left) * fx), Y = rect.Top + (int)Math.Round((rect.Bottom - rect.Top) * fy) };
            var hit = WindowFromPoint(point);
            if (hit != view && !IsChild(view, hit)) return "押す位置にほかの窓 (通知やダイアログなど) が重なっています";

            GetCursorPos(out var original);
            SetCursorPos(point.X, point.Y);
            mouse_event(MouseLeftDown, 0, 0, 0, IntPtr.Zero);
            mouse_event(MouseLeftUp, 0, 0, 0, IntPtr.Zero);
            // ブラウザが押されたことを受け取ってから、カーソルを元の場所に戻す
            Thread.Sleep(50);
            SetCursorPos(original.X, original.Y);
            return null;
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    static IntPtr FindBrowserWindow(string titleMarker)
    {
        var deadline = DateTime.UtcNow + FindTimeout;
        while (true)
        {
            var found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || !TitleOf(hwnd).Contains(titleMarker, StringComparison.Ordinal) || !IsBrowserProcess(hwnd)) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero || DateTime.UtcNow > deadline) return found;
            Thread.Sleep(100);
        }
    }

    static bool IsBrowserProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return BrowserProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static string TitleOf(IntPtr hwnd)
    {
        var builder = new StringBuilder(512);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    // ページを描く子の窓。いちばん大きいものが今のタブの表示領域
    static IntPtr FindPageView(IntPtr window)
    {
        var best = IntPtr.Zero;
        var bestArea = 0L;
        EnumChildWindows(window, (hwnd, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == "Chrome_RenderWidgetHostHWND" && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var rect))
            {
                var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea)
                {
                    best = hwnd;
                    bestArea = area;
                }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    static bool BringToFront(IntPtr window)
    {
        if (GetForegroundWindow() == window) return true;
        SetForegroundWindow(window);
        if (GetForegroundWindow() == window) return true;
        // Windows は他のアプリを勝手に前面に出させないので、Alt キーを一度押して許してもらう
        keybd_event(VkMenu, 0, 0, IntPtr.Zero);
        keybd_event(VkMenu, 0, KeyUp, IntPtr.Zero);
        SetForegroundWindow(window);
        Thread.Sleep(100);
        return GetForegroundWindow() == window;
    }

    const int SwRestore = 9;
    const uint MouseLeftDown = 0x0002;
    const uint MouseLeftUp = 0x0004;
    const byte VkMenu = 0x12;
    const uint KeyUp = 0x0002;
    static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extraInfo);
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extraInfo);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
