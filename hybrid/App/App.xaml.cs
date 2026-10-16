using System.Diagnostics;
using System.Reflection;
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
    public const string UpdatedFlag = "--updated";
    // 更新で起動し直したときは、前のプロセスが片付けを終えるまで待つ
    static readonly TimeSpan UpdateHandoverWait = TimeSpan.FromSeconds(30);

    static bool TryAcquire(Mutex mutex, TimeSpan wait)
    {
        try
        {
            return mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // 前のプロセスが手放さずに終わった。持ち主はもういない
            return true;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.Resolve();
        // 同じフォルダのデータを2つのプロセスが同時に書くと履歴が壊れる
        var mutexName = "ClaudeChat-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToLowerInvariant())))[..16];
        singleInstance = new Mutex(false, mutexName);
        if (!TryAcquire(singleInstance, e.Args.Contains(UpdatedFlag) ? UpdateHandoverWait : TimeSpan.Zero))
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
        WindowsPlatform.DeleteReplacedExe();
        CreateShortcutsOnFirstRun(paths);
        new MainWindow(paths).Show();
    }

    // 初めて起動したときだけ作る。消したショートカットを起動のたびに作り直さないよう、作ったことを覚えておく
    static void CreateShortcutsOnFirstRun(AppPaths paths)
    {
        var marker = Path.Combine(paths.ConfigDir, "shortcuts-created");
        if (File.Exists(marker)) return;
        try
        {
            if (new WindowsPlatform().CreateShortcuts() is null) File.WriteAllText(marker, DateTime.Now.ToString("O"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            // ショートカットが無くてもアプリは使える。設定から作り直せる
        }
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

sealed partial class WindowsPlatform : IPlatform
{
    public string Version => typeof(WindowsPlatform).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // CI が SourceRevisionId で埋めた「版+コミット」の後ろ半分
    public string? Commit =>
        typeof(WindowsPlatform).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+') is [_, var commit, ..] && commit.Length > 0 ? commit : null;

    static string ReplacedExe(string exe) => exe + ".old";

    // 動いている exe は消せないが名前は変えられる。名前を変えて新しい exe を置き、起動し直してから閉じる
    public string? InstallUpdate(string newExePath)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return "今の exe の場所が分かりません";
        var old = ReplacedExe(exe);
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            File.Move(newExePath, exe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe);
            return $"exe を入れ替えられませんでした。書き込めないフォルダに置いているかもしれません: {e.Message}";
        }
        Process.Start(new ProcessStartInfo(exe, App.UpdatedFlag) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        Application.Current.Dispatcher.BeginInvoke(() => Application.Current.MainWindow?.Close());
        return null;
    }

    // 前の版の exe は、そのプロセスが終わってからでないと消せない
    public static void DeleteReplacedExe()
    {
        if (Environment.ProcessPath is not { } exe || !File.Exists(ReplacedExe(exe))) return;
        try
        {
            File.Delete(ReplacedExe(exe));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 次の起動で消す
        }
    }

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
