using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClaudeChat.App;

// どのアプリを使っていても、決めたキーで入力の小窓を出す
sealed partial class WindowsPlatform
{
    const int WmHotkey = 0x0312;
    const int HotkeyId = 1;
    const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;
    // 押しっぱなしで小窓が開いたり閉じたりを繰り返さない
    const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    HwndSource? hotkeyTarget;
    public Action? QuickEntryOpener { get; set; }

    public string? SetQuickEntryHotkey(string hotkey) => Application.Current.Dispatcher.Invoke(() =>
    {
        if (hotkeyTarget is null)
        {
            // キーの知らせだけを受ける、見えない窓
            hotkeyTarget = new HwndSource(new HwndSourceParameters("ClaudeChatHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            hotkeyTarget.AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr _, ref bool handled) =>
            {
                if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
                {
                    QuickEntryOpener?.Invoke();
                    handled = true;
                }
                return IntPtr.Zero;
            });
        }
        UnregisterHotKey(hotkeyTarget.Handle, HotkeyId);
        if (string.IsNullOrWhiteSpace(hotkey)) return null;
        if (ParseHotkey(hotkey) is not var (modifiers, key)) return $"キーの書き方が分かりません: {hotkey}";
        return RegisterHotKey(hotkeyTarget.Handle, HotkeyId, modifiers | ModNoRepeat, key)
            ? null
            : $"{hotkey} はほかのアプリが使っています。別のキーを選んでください";
    });

    // 「Ctrl+Alt+Space」の形。最後の 1 つがキーで、その前は Ctrl / Alt / Shift / Win
    static (uint Modifiers, uint Key)? ParseHotkey(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                "win" => ModWin,
                _ => 0u,
            };
        }
        if (modifiers == 0 || !Enum.TryParse<Key>(parts[^1], true, out var key)) return null;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey == 0 ? null : (modifiers, virtualKey);
    }
}
