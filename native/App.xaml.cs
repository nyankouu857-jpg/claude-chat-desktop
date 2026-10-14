using System.Windows;
using Microsoft.Win32;

namespace ClaudeChat.Native;

public partial class App : Application
{
    static bool PrefersDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // async void のハンドラで落ちてもウィンドウごと消えないように
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ClaudeChat", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // ThemeMode.System と自前の判定がずれると白地に白文字になるので、判定を1か所に寄せる
        var dark = PrefersDarkTheme();
        ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        var palette = dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(palette, UriKind.Relative) });
        new MainWindow().Show();
    }
}
