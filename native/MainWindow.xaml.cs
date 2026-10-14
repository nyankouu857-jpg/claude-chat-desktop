using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Anthropic;
using ClaudeChat.Native.Core;
using ClaudeChat.Native.Views;
using Microsoft.Win32;

namespace ClaudeChat.Native;

public sealed record SidebarItem(string Id, string Title, bool IsStreaming)
{
    public Visibility StreamingVisibility => IsStreaming ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record PendingImage(string MediaType, byte[] Data)
{
    public BitmapImage Preview => MessageViews.LoadBitmap(Data);
}

sealed class LiveReply
{
    public List<ViewBlock> Blocks { get; } = [];
    public Dictionary<int, int> IndexMap { get; } = [];
}

public partial class MainWindow : Window
{
    // API 側の1枚あたり上限
    const long MaxImageBytes = 5 * 1024 * 1024;

    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    readonly Store store = new();
    // 別の会話を見ている間も途中経過を溜めておき、戻ったときに続きから描く
    readonly Dictionary<string, LiveReply> liveReplies = [];
    readonly Dictionary<string, CancellationTokenSource> runningChats = [];
    readonly Dictionary<string, string> lastErrors = [];
    readonly ObservableCollection<PendingImage> pendingImages = [];
    List<ConversationSummary> summaries = [];
    string? currentId;
    AssistantView? liveView;
    AnthropicClient? client;
    string? clientApiKey;
    // サイドバーを作り直したときの SelectionChanged を無視するため
    bool rebuildingSidebar;
    bool settingsLoaded;

    public MainWindow()
    {
        InitializeComponent();
        AttachmentTray.ItemsSource = pendingImages;
        ModelSelect.ItemsSource = ModelCatalog.Models.Select(m => new { m.Id, m.Label }).ToList();
        EffortSelect.ItemsSource = ModelCatalog.Efforts.Select(e => new { Id = e, Label = $"思考: {e}" }).ToList();
        EffortSelect.DisplayMemberPath = "Label";
        EffortSelect.SelectedValuePath = "Id";
        Loaded += async (_, _) =>
        {
            StartNewChat();
            await RefreshSidebarAsync();
            await LoadSettingsAsync();
        };
    }

    AnthropicClient GetClient(string apiKey)
    {
        if (client is null || clientApiKey != apiKey)
        {
            client = new AnthropicClient { ApiKey = apiKey };
            clientApiKey = apiKey;
        }
        return client;
    }

    async Task LoadSettingsAsync()
    {
        var settings = await store.GetSettingsAsync();
        ModelSelect.SelectedValue = settings.Model;
        EffortSelect.SelectedValue = settings.Effort;
        WebSearchToggle.IsChecked = settings.WebSearch;
        settingsLoaded = true;
        if (await store.GetApiKeyAsync() is null) await OpenSettingsAsync();
    }

    async Task OpenSettingsAsync()
    {
        var settings = await store.GetSettingsAsync();
        var dialog = new SettingsWindow(await store.GetApiKeyAsync() is not null, settings.SystemPrompt) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await store.SaveSettingsAsync(s => s.SystemPrompt = dialog.SystemPrompt);
        if (dialog.ApiKey.Length > 0) await store.SetApiKeyAsync(dialog.ApiKey);
    }

    static string FormatTokens(long n) => n >= 1000 ? $"{n / 1000.0:0.0}k" : n.ToString();

    bool IsPinnedToBottom => MessagesScroll.VerticalOffset >= MessagesScroll.ScrollableHeight - 80;

    void ScrollToBottomAfterLayout() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MessagesScroll.ScrollToEnd());

    UIElement CreateEmptyState()
    {
        var hour = DateTime.Now.Hour;
        var greeting = hour < 5 ? "夜更かしですね" : hour < 11 ? "おはようございます" : hour < 18 ? "こんにちは" : "こんばんは";
        var panel = new StackPanel { Margin = new Thickness(0, 140, 0, 0) };
        panel.Children.Add(new TextBlock { Text = greeting, FontSize = 32, FontFamily = new FontFamily("Yu Mincho, Georgia"), HorizontalAlignment = HorizontalAlignment.Center });
        var sub = new TextBlock { Text = "何について話しましょうか?", Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(sub);
        return panel;
    }

    void RenderConversation(Conversation? conversation)
    {
        liveView = null;
        MessagesPanel.Children.Clear();
        ChatTitle.Text = conversation?.Title ?? Conversation.DefaultTitle;
        UsageLabel.Text = conversation is null
            ? ""
            : $"入力 {FormatTokens(conversation.Usage.Input)} / 出力 {FormatTokens(conversation.Usage.Output)} / キャッシュ {FormatTokens(conversation.Usage.CacheRead)} tokens";

        if (conversation is null || conversation.Messages.Count == 0)
        {
            MessagesPanel.Children.Add(CreateEmptyState());
            return;
        }

        var assistantBlocks = new List<ViewBlock>();
        void FlushAssistant()
        {
            if (assistantBlocks.Count > 0) MessagesPanel.Children.Add(new AssistantView(assistantBlocks, false).Root);
            assistantBlocks = [];
        }

        // pause_turn で assistant が連続することがあるので1つの吹き出しにまとめる
        foreach (var message in conversation.Messages)
        {
            if ((string?)message["role"] == "assistant")
            {
                foreach (var block in ViewBlocks.FromContent(message["content"])) ViewBlocks.Push(assistantBlocks, block);
            }
            else
            {
                FlushAssistant();
                MessagesPanel.Children.Add(MessageViews.CreateUserMessage(ViewBlocks.FromContent(message["content"])));
            }
        }
        FlushAssistant();

        if (liveReplies.TryGetValue(conversation.Id, out var live))
        {
            liveView = new AssistantView(live.Blocks, true);
            liveView.Rendered += () =>
            {
                if (IsPinnedToBottom) ScrollToBottomAfterLayout();
            };
            MessagesPanel.Children.Add(liveView.Root);
        }
        if (lastErrors.TryGetValue(conversation.Id, out var error)) MessagesPanel.Children.Add(MessageViews.CreateError(error));
        ScrollToBottomAfterLayout();
    }

    void RenderSidebar()
    {
        rebuildingSidebar = true;
        var items = summaries.Select(s => new SidebarItem(s.Id, s.Title, liveReplies.ContainsKey(s.Id))).ToList();
        ConversationList.ItemsSource = items;
        ConversationList.SelectedItem = items.FirstOrDefault(i => i.Id == currentId);
        rebuildingSidebar = false;
    }

    async Task RefreshSidebarAsync()
    {
        summaries = await store.ListConversationsAsync();
        RenderSidebar();
    }

    void UpdateComposerState()
    {
        var streaming = currentId is not null && liveReplies.ContainsKey(currentId);
        SendButton.Visibility = streaming ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task OpenConversationAsync(string id)
    {
        currentId = id;
        RenderConversation(await store.GetConversationAsync(id));
        RenderSidebar();
        UpdateComposerState();
        PromptBox.Focus();
    }

    void StartNewChat()
    {
        currentId = null;
        RenderConversation(null);
        RenderSidebar();
        UpdateComposerState();
        PromptBox.Focus();
    }

    async Task SendAsync()
    {
        var text = PromptBox.Text.Trim();
        if (text.Length == 0 && pendingImages.Count == 0) return;
        if (currentId is not null && liveReplies.ContainsKey(currentId)) return;

        var apiKey = await store.GetApiKeyAsync();
        if (apiKey is null)
        {
            await OpenSettingsAsync();
            return;
        }

        JsonNode content = pendingImages.Count == 0
            ? JsonValue.Create(text)
            : new JsonArray(pendingImages
                .Select(image => (JsonNode)new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data) },
                })
                .Concat(text.Length > 0 ? [new JsonObject { ["type"] = "text", ["text"] = text }] : [])
                .ToArray());

        var conversation = (currentId is null ? null : await store.GetConversationAsync(currentId)) ?? store.CreateConversation();
        conversation.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        await store.SaveConversationAsync(conversation);

        lastErrors.Remove(conversation.Id);
        liveReplies[conversation.Id] = new LiveReply();
        currentId = conversation.Id;
        PromptBox.Clear();
        pendingImages.Clear();
        RenderConversation(conversation);
        UpdateComposerState();
        await RefreshSidebarAsync();
        await RunChatAsync(conversation, apiKey);
    }

    async Task RunChatAsync(Conversation conversation, string apiKey)
    {
        var id = conversation.Id;
        using var cancellation = new CancellationTokenSource();
        runningChats[id] = cancellation;
        var service = new ChatService(GetClient(apiKey));
        try
        {
            // UI スレッドのまま await するので、会話の書き換えも描画も同じスレッドで済みロックが要らない
            var result = await service.StreamReplyAsync(await store.GetSettingsAsync(), conversation, e => ApplyStreamEvent(id, e), cancellation.Token);
            liveReplies.Remove(id);
            // 応答中に削除された会話を保存し直して復活させない
            if (await store.GetConversationAsync(id) is null) return;
            if (result.Outcome == ChatOutcome.Failed) lastErrors[id] = result.Error ?? "不明なエラー";
            await store.SaveConversationAsync(conversation);
            if (currentId == id) RenderConversation(conversation);
            UpdateComposerState();
            await RefreshSidebarAsync();

            if (result.Outcome == ChatOutcome.Done && conversation.Title == Conversation.DefaultTitle)
            {
                await UpdateTitleAsync(service, conversation);
            }
        }
        finally
        {
            runningChats.Remove(id);
            liveReplies.Remove(id);
        }
    }

    async Task UpdateTitleAsync(ChatService service, Conversation conversation)
    {
        string? title;
        try
        {
            title = await service.GenerateTitleAsync(conversation);
        }
        catch (Exception)
        {
            // タイトルは後回しでいいので失敗しても会話は止めない
            return;
        }
        if (title is null) return;
        conversation.Title = title;
        await store.SaveConversationAsync(conversation);
        if (currentId == conversation.Id) ChatTitle.Text = title;
        await RefreshSidebarAsync();
    }

    void ApplyStreamEvent(string conversationId, StreamEvent streamEvent)
    {
        if (!liveReplies.TryGetValue(conversationId, out var live)) return;
        int? viewIndex = null;
        switch (streamEvent)
        {
            case BlockStarted started:
                viewIndex = ViewBlocks.Push(live.Blocks, started.Block);
                live.IndexMap[started.Index] = viewIndex.Value;
                break;
            case DeltaReceived delta when live.IndexMap.TryGetValue(delta.Index, out var i):
                if (live.Blocks[i] is TextView text) text.Text += delta.Text;
                else if (live.Blocks[i] is ThinkingView thinking) thinking.Text += delta.Text;
                viewIndex = i;
                break;
            case SearchQueryResolved resolved when live.IndexMap.TryGetValue(resolved.Index, out var i):
                if (live.Blocks[i] is SearchView search) search.Query = resolved.Query;
                viewIndex = i;
                break;
        }
        if (viewIndex is int index && conversationId == currentId) liveView?.Update(index);
    }

    void AddImageFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!ImageTypes.TryGetValue(Path.GetExtension(path), out var mediaType)) continue;
            if (new FileInfo(path).Length > MaxImageBytes)
            {
                MessageBox.Show(this, $"{Path.GetFileName(path)} は 5MB を超えているので添付できません", "ClaudeChat");
                continue;
            }
            pendingImages.Add(new PendingImage(mediaType, File.ReadAllBytes(path)));
        }
    }

    bool TryPasteImage()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var before = pendingImages.Count;
            AddImageFiles(Clipboard.GetFileDropList().Cast<string>());
            return pendingImages.Count > before;
        }
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image) return false;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        if (stream.Length > MaxImageBytes)
        {
            MessageBox.Show(this, "貼り付けた画像が 5MB を超えているので添付できません", "ClaudeChat");
            return true;
        }
        pendingImages.Add(new PendingImage("image/png", stream.ToArray()));
        return true;
    }

    // Event Handlers

    async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (currentId is not null && runningChats.TryGetValue(currentId, out var cancellation)) cancellation.Cancel();
    }

    void NewChat_Click(object sender, RoutedEventArgs e) => StartNewChat();

    async void Settings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();

    async void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rebuildingSidebar || ConversationList.SelectedItem is not SidebarItem item || item.Id == currentId) return;
        await OpenConversationAsync(item.Id);
    }

    async void DeleteConversation_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: string id }) return;
        var title = summaries.FirstOrDefault(s => s.Id == id)?.Title ?? "";
        if (MessageBox.Show(this, $"{title} を削除しますか?", "ClaudeChat", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (runningChats.TryGetValue(id, out var cancellation)) cancellation.Cancel();
        await store.DeleteConversationAsync(id);
        if (id == currentId) StartNewChat();
        await RefreshSidebarAsync();
    }

    void PromptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // IME 確定の Enter は Key.ImeProcessed で届くので、素の Enter だけ拾えば変換中に誤送信しない
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendAsync();
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && TryPasteImage())
        {
            e.Handled = true;
        }
    }

    void PromptBox_TextChanged(object sender, TextChangedEventArgs e) =>
        PromptPlaceholder.Visibility = PromptBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "画像|*.png;*.jpg;*.jpeg;*.gif;*.webp", Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddImageFiles(dialog.FileNames);
    }

    void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PendingImage image }) pendingImages.Remove(image);
    }

    void Composer_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Composer_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddImageFiles(paths);
        e.Handled = true;
    }

    async void ModelSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settingsLoaded && ModelSelect.SelectedValue is string model) await store.SaveSettingsAsync(s => s.Model = model);
    }

    async void EffortSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settingsLoaded && EffortSelect.SelectedValue is string effort) await store.SaveSettingsAsync(s => s.Effort = effort);
    }

    async void WebSearchToggle_Click(object sender, RoutedEventArgs e) =>
        await store.SaveSettingsAsync(s => s.WebSearch = WebSearchToggle.IsChecked == true);
}
