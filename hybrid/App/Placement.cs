using System.IO;
using System.Text.Json;
using System.Windows;

namespace ClaudeChat.App;

// 窓の位置と大きさを次に開いたときに戻す。メインの窓と、別の窓で開いたチャットごとに覚える
static class Placement
{
    sealed class Bounds
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
        public DateTime SavedAt { get; set; }
    }

    // 別の窓のチャットは開くたびに増えるので、最近のものだけ残す
    const int KeepChatWindows = 30;
    // 画面の端にわずかに掛かっているだけの窓は、つかめないので見えないのと同じ
    const double MinVisible = 80;

    static string? file;
    static Dictionary<string, Bounds> saved = [];

    public static void Load(string dataDir)
    {
        file = Path.Combine(dataDir, "window.json");
        try
        {
            saved = JsonSerializer.Deserialize<Dictionary<string, Bounds>>(File.ReadAllText(file)) ?? [];
        }
        catch (Exception)
        {
            // 初回や壊れたときは既定の位置で開く
            saved = [];
        }
    }

    // 戻せたら true。最大化していたかは maximized で返し、出し方は呼ぶ側に任せる
    public static bool Restore(Window window, string key, out bool maximized)
    {
        maximized = false;
        if (!saved.TryGetValue(key, out var bounds) || !OnScreen(bounds)) return false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = Math.Max(bounds.Width, window.MinWidth);
        window.Height = Math.Max(bounds.Height, window.MinHeight);
        maximized = bounds.Maximized;
        return true;
    }

    public static void Save(Window window, string key)
    {
        if (file is null || window.WindowState == WindowState.Minimized && window.RestoreBounds.IsEmpty) return;
        // 最大化や全画面のときは、戻したときの大きさを覚える
        var normal = window.WindowState == WindowState.Normal && !Frameless.IsFullscreen(window)
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (normal.IsEmpty || normal.Width < 1) return;
        saved[key] = new Bounds
        {
            Left = normal.Left,
            Top = normal.Top,
            Width = normal.Width,
            Height = normal.Height,
            Maximized = Frameless.IsMaximized(window),
            SavedAt = DateTime.UtcNow,
        };
        foreach (var old in saved.Where(s => s.Key.StartsWith("chat:")).OrderByDescending(s => s.Value.SavedAt).Skip(KeepChatWindows).Select(s => s.Key).ToList())
        {
            saved.Remove(old);
        }
        try
        {
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, file, true);
        }
        catch (Exception)
        {
            // 書けなくても、次は既定の位置で開くだけ
        }
    }

    // モニターを外したあとなどに、画面の外へ出してしまわない
    static bool OnScreen(Bounds bounds)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(screen, new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        return bounds.Width > 0 && bounds.Height > 0 && !visible.IsEmpty && visible.Width >= MinVisible && visible.Height >= MinVisible;
    }
}
