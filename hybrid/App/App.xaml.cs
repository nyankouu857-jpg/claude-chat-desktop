using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using ClaudeChat.Core;

namespace ClaudeChat.App;

public partial class App : Application
{
    Mutex? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.Resolve();
        // 同じフォルダのデータを2つのプロセスが同時に書くと履歴が壊れる
        var mutexName = "ClaudeChat-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToLowerInvariant())))[..16];
        singleInstance = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ClaudeChat はすでに起動しています。", "ClaudeChat");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ClaudeChat", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        new MainWindow(paths).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}

sealed class DpapiProtector : ISecretProtector
{
    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedValue) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
}

sealed class WindowsPlatform : IPlatform
{
    public string Version => typeof(WindowsPlatform).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public void OpenPath(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // .json に関連付けが無い PC ではメモ帳で開く
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // 画面からの呼び出しはスレッドプール上なので UI スレッドに戻す
    public string? PickFolder() => Application.Current.Dispatcher.Invoke(() =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Claude に操作を許可するフォルダ" };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    });

    public void MoveToTrash(string path)
    {
        var operation = new ShFileOperation
        {
            Func = FoDelete,
            // 二重の NUL 終端
            From = path + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        var code = SHFileOperation(ref operation);
        if (code != 0 || operation.AnyOperationsAborted) throw new IOException($"ごみ箱へ移せませんでした (コード {code})");
    }

    const uint FoDelete = 3;
    const ushort FofSilent = 0x0004;
    const ushort FofNoConfirmation = 0x0010;
    const ushort FofAllowUndo = 0x0040;
    const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOperation
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref ShFileOperation operation);
}
