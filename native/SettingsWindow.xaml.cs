using System.Windows;

namespace ClaudeChat.Native;

public partial class SettingsWindow : Window
{
    public string ApiKey => ApiKeyBox.Password.Trim();
    public string SystemPrompt => SystemPromptBox.Text;

    public SettingsWindow(bool hasApiKey, string systemPrompt)
    {
        InitializeComponent();
        ApiKeyState.Text = hasApiKey
            ? "登録済みです。変更するときだけ入力してください"
            : "未登録です。console.anthropic.com で発行したキーを入力してください";
        SystemPromptBox.Text = systemPrompt;
    }

    void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
