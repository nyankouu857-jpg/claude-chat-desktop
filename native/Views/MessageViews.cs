using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeChat.Native.Core;

namespace ClaudeChat.Native.Views;

public sealed class AssistantView
{
    public StackPanel Root { get; } = new() { Margin = new Thickness(0, 0, 0, 24) };

    readonly List<ViewBlock> blocks;
    readonly List<FrameworkElement> nodes = [];
    readonly HashSet<int> dirty = [];
    readonly DispatcherTimer flushTimer;
    readonly Ellipse? cursor;

    public event Action? Rendered;

    public AssistantView(List<ViewBlock> blocks, bool streaming)
    {
        this.blocks = blocks;
        // 1トークンごとに FlowDocument を組み直すと重いので 50ms ごとにまとめる
        flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => Flush(), Dispatcher.CurrentDispatcher);
        flushTimer.Stop();
        if (streaming)
        {
            cursor = new Ellipse { Width = 9, Height = 9, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 6, 0, 0) };
            cursor.SetResourceReference(Shape.FillProperty, "AccentBrush");
            cursor.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            Root.Children.Add(cursor);
        }
        for (var i = 0; i < blocks.Count; i++) RenderBlock(i);
    }

    public void Update(int index)
    {
        dirty.Add(index);
        if (!flushTimer.IsEnabled) flushTimer.Start();
    }

    void Flush()
    {
        flushTimer.Stop();
        foreach (var index in dirty) RenderBlock(index);
        dirty.Clear();
        Rendered?.Invoke();
    }

    void RenderBlock(int index)
    {
        if (index >= blocks.Count) return;
        var block = blocks[index];
        while (nodes.Count <= index) nodes.Add(null!);
        var node = nodes[index];
        if (node is null)
        {
            node = MessageViews.CreateNode(block);
            nodes[index] = node;
            Root.Children.Insert(cursor is null ? Root.Children.Count : Root.Children.IndexOf(cursor), node);
        }
        MessageViews.Fill(node, block);
    }
}

public static class MessageViews
{
    public static FrameworkElement CreateNode(ViewBlock block) => block switch
    {
        TextView => new RichTextBox { Style = (Style)Application.Current.FindResource("DocumentView") },
        ThinkingView => CreateExpander("思考プロセス"),
        SearchResultView => CreateExpander(""),
        ImageView => new Image { MaxWidth = 320, MaxHeight = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) },
        NoticeView => CreateNotice(),
        _ => CreateMutedText(),
    };

    public static void Fill(FrameworkElement node, ViewBlock block)
    {
        switch (block)
        {
            case TextView text:
                ((RichTextBox)node).Document = MarkdownRenderer.Render(text.Text);
                break;
            case ThinkingView thinking:
                ((TextBlock)((Expander)node).Content).Text = thinking.Text;
                break;
            case SearchView search:
                ((TextBlock)node).Text = search.Query.Length > 0 ? $"Web を検索: {search.Query}" : "Web を検索しています";
                break;
            case SearchResultView result:
            {
                var expander = (Expander)node;
                expander.Header = result.Error is null ? $"{result.Hits.Count} 件の検索結果" : $"検索に失敗しました: {result.Error}";
                var list = (TextBlock)expander.Content;
                list.Inlines.Clear();
                foreach (var hit in result.Hits)
                {
                    var link = MarkdownRenderer.CreateHyperlink(hit.Url);
                    link.Inlines.Add(new Run(hit.Title.Length > 0 ? hit.Title : hit.Url));
                    list.Inlines.Add(link);
                    list.Inlines.Add(new LineBreak());
                }
                break;
            }
            case ImageView image:
                ((Image)node).Source = LoadBitmap(image.Data);
                break;
            case NoticeView notice:
                ((TextBlock)((Border)node).Child).Text = notice.Text;
                break;
        }
    }

    static Expander CreateExpander(string header)
    {
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(4, 4, 4, 8) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var expander = new Expander { Header = header, Content = body, Margin = new Thickness(0, 6, 0, 6), FontSize = 13 };
        expander.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
        return expander;
    }

    static TextBlock CreateMutedText()
    {
        var text = new TextBlock { FontSize = 13, Margin = new Thickness(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return text;
    }

    static Border CreateNotice()
    {
        var border = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 6, 0, 6), Child = CreateMutedText() };
        border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        return border;
    }

    public static Border CreateError(string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 6), Child = text };
        border.SetResourceReference(Border.BorderBrushProperty, "DangerBrush");
        return border;
    }

    public static FrameworkElement CreateUserMessage(List<ViewBlock> blocks)
    {
        var content = new StackPanel();
        foreach (var block in blocks)
        {
            if (block is ImageView image)
            {
                content.Children.Add(new Image { Source = LoadBitmap(image.Data), MaxWidth = 240, MaxHeight = 240, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left });
            }
            else if (block is TextView text)
            {
                content.Children.Add(new TextBox { Text = text.Text, Style = (Style)Application.Current.FindResource("SelectableText") });
            }
        }
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 10, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 600,
            Margin = new Thickness(80, 0, 0, 24),
            Child = content,
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "UserBubbleBrush");
        return bubble;
    }

    public static BitmapImage LoadBitmap(byte[] data)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(data);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
