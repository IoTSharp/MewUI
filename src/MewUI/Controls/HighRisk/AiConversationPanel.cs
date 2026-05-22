using System.Collections.ObjectModel;
using System.Diagnostics;

using Aprillz.MewUI.Platform;
using Aprillz.MewUI.Rendering;

namespace Aprillz.MewUI.Controls.HighRisk;

public sealed record AiAttachment(string Name, string? Kind = null, string? Path = null, long? SizeBytes = null, string? Id = null);

public enum AiContentBlockKind
{
    Paragraph,
    Code,
    Quote,
    Canvas
}

public sealed class AiContentBlock
{
    public AiContentBlockKind Kind { get; set; }

    public string Text { get; set; } = string.Empty;

    public Element? Canvas { get; set; }

    public string? Language { get; set; }
}

public sealed class AiConversationMessage
{
    public string Role { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public bool IsAssistant => string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase);
    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);
    public ObservableCollection<AiContentBlock> Blocks { get; } = new();
    public ObservableCollection<AiAttachment> Attachments { get; } = new();
}

public sealed class AiConversationPanel : UserControl
{
    private readonly ObservableCollection<AiConversationMessage> _messages = new();
    private readonly ObservableCollection<AiAttachment> _draftAttachments = new();
    private readonly ObservableValue<string> _input = new(string.Empty);
    private readonly ObservableValue<string> _status = new("Ready");
    private readonly ObservableValue<string> _subtitle = new("sonnet.vip");
    private readonly ObservableValue<bool> _composerEnabledValue = new(true);
    private readonly ObservableValue<bool> _approvalVisible = new(false);
    private readonly ObservableValue<string> _approvalTitleText = new(string.Empty);
    private readonly ObservableValue<string> _approvalStateText = new(string.Empty);
    private readonly ObservableValue<string> _approvalDetailsText = new(string.Empty);
    private readonly ObservableValue<string> _approvalHintText = new(string.Empty);
    private string? _approvalTitle;
    private string? _approvalState;
    private string? _approvalDetails;
    private string? _approvalHint;
    private bool _composerEnabled = true;
    private bool _echoUserMessage = true;
    private ItemsControl _messagesList = null!;
    private MultiLineTextBox _inputBox = null!;

    public AiConversationPanel()
    {
        Build();
    }

    public ObservableCollection<AiConversationMessage> Messages => _messages;

    public ObservableCollection<AiAttachment> DraftAttachments => _draftAttachments;

    public event Func<string, IReadOnlyList<AiAttachment>, Task<bool>>? SendRequested;

    public event Action? AttachRequested;

    public event Action? ConfigureRequested;

    public event Action<AiAttachment>? DraftAttachmentRemoveRequested;

    public event Action? ApprovalApproveRequested;

    public event Action? ApprovalApproveAllRequested;

    public void AppendAssistant(string content)
    {
        AddMessage("assistant", "Assistant", content);
        ScrollToBottom();
    }

    public void AppendUser(string content)
    {
        AddMessage("user", "You", content);
        ScrollToBottom();
    }

    public void ClearMessages()
    {
        _messages.Clear();
        RefreshMessages();
    }

    public void SetMessages(IEnumerable<AiConversationMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        _messages.Clear();
        foreach (var message in messages)
        {
            _messages.Add(message);
        }

        RefreshMessages();
    }

    public void SetDraftAttachments(IEnumerable<AiAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        _draftAttachments.Clear();
        foreach (var attachment in attachments)
        {
            _draftAttachments.Add(attachment);
        }

        _status.Value = _draftAttachments.Count == 0 ? "就绪" : $"{_draftAttachments.Count} 个附件";
        Build();
    }

    public void ClearDraftAttachments()
    {
        _draftAttachments.Clear();
        _status.Value = "就绪";
        Build();
    }

    public void SetStatus(string status)
    {
        _status.Value = status;
    }

    public void SetSubtitle(string subtitle)
    {
        _subtitle.Value = subtitle;
    }

    public void SetComposerEnabled(bool enabled)
    {
        _composerEnabled = enabled;
        _composerEnabledValue.Value = enabled;
        if (_inputBox != null)
        {
            _inputBox.IsEnabled = enabled;
        }
    }

    public void SetEchoUserMessage(bool echoUserMessage)
    {
        _echoUserMessage = echoUserMessage;
    }

    public void SetApprovalPrompt(string? title, string? state, string? details, string? hint)
    {
        _approvalTitle = string.IsNullOrWhiteSpace(title) ? null : title;
        _approvalState = state;
        _approvalDetails = details;
        _approvalHint = hint;
        SyncApprovalPrompt();
    }

    public void ClearApprovalPrompt()
    {
        SetApprovalPrompt(null, null, null, null);
    }

    public void AddDraftAttachment(AiAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        _draftAttachments.Add(attachment);
        _status.Value = $"{_draftAttachments.Count} 个附件";
        Build();
    }

    public AiConversationMessage AddMessage(string role, string author, string content, IEnumerable<AiAttachment>? attachments = null, IEnumerable<AiContentBlock>? blocks = null)
    {
        var message = new AiConversationMessage
        {
            Role = role,
            Author = author,
            Content = content,
            Timestamp = DateTimeOffset.Now
        };

        foreach (var block in blocks ?? ParseBlocks(content))
        {
            message.Blocks.Add(block);
        }

        if (message.Blocks.Count == 0 && !string.IsNullOrWhiteSpace(content))
        {
            message.Blocks.Add(new AiContentBlock { Kind = AiContentBlockKind.Paragraph, Text = content });
        }

        if (attachments != null)
        {
            foreach (var attachment in attachments)
            {
                message.Attachments.Add(attachment);
            }
        }

        _messages.Add(message);
        ScrollToBottom();
        return message;
    }

    protected override Element? OnBuild()
    {
        SyncApprovalPrompt();
        var listView = ItemsView.Create(_messages, textSelector: m => m.Content, keySelector: m => m);
        _messagesList = new ItemsControl()
            .Ref(out var list)
            .HorizontalAlignment(HorizontalAlignment.Stretch)
            .VariableHeightPresenter()
            .ItemsSource(listView)
            .ItemPadding(Thickness.Zero)
            .ItemTemplate(new DelegateTemplate<AiConversationMessage>(
                build: ctx => BuildMessageTemplate(ctx),
                bind: (view, msg, index, ctx) => BindMessageTemplate(view, msg, index, ctx)))
            .Apply(_ => _messagesList = list);

        return new DockPanel()
            .Padding(12)
            .LastChildFill()
            .Children(
                new Border()
                    .DockTop()
                    .Padding(10, 8)
                    .BorderThickness(1)
                    .CornerRadius(0)
                    .Child(
                        new DockPanel()
                            .Children(
                                new TextBlock()
                                    .Text("曾华AI")
                                    .FontSize(16)
                                    .SemiBold()
                                    .DockLeft(),

                                new StackPanel()
                                    .Horizontal()
                                    .Spacing(8)
                                    .DockRight()
                                    .Children(
                                        new TextBlock()
                                            .BindText(_subtitle)
                                            .CenterVertical(),

                                        new Button()
                                            .Height(26)
                                            .MinWidth(54)
                                            .Padding(8, 3)
                                            .Content("设置")
                                            .OnClick(() => ConfigureRequested?.Invoke()))
                            )),

                BuildApprovalPrompt().DockTop(),
                BuildComposer().DockBottom(),

                new Border()
                    .BorderThickness(1)
                    .CornerRadius(0)
                    .Child(
                        new ScrollViewer()
                            .VerticalScroll(ScrollMode.Auto)
                            .HorizontalScroll(ScrollMode.Disabled)
                            .Content(_messagesList)
                    )
            );
    }

    private FrameworkElement BuildComposer()
    {
        return new Border()
            .Padding(10)
            .BorderThickness(1)
            .CornerRadius(8)
            .Child(
                new DockPanel()
                    .LastChildFill()
                    .Spacing(8)
                    .Children(
                        new StackPanel()
                            .DockTop()
                            .Vertical()
                            .Spacing(6)
                            .Children(
                                BuildAttachmentChips(),

                                new MultiLineTextBox()
                                    .Ref(out _inputBox)
                                    .Height(76)
                                    .Wrap()
                                    .BindIsEnabled(_composerEnabledValue)
                                    .Placeholder("输入消息，Enter 发送，Shift+Enter 换行")
                                    .BindText(_input)
                                    .OnKeyDown(OnComposerKeyDown)
                                    .ToolTip("Enter 发送，Shift+Enter 换行")
                            ),

                        new StackPanel()
                            .DockBottom()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Button()
                                    .Content("附件")
                                    .BindIsEnabled(_composerEnabledValue)
                                    .OnClick(() => AttachRequested?.Invoke()),

                                new Button()
                                    .Content("发送")
                                    .BindIsEnabled(_composerEnabledValue)
                                    .OnClick(SendDraft),

                                new TextBlock()
                                    .BindText(_status)
                                    .CenterVertical()
                            )
                    ));
    }

    private FrameworkElement BuildApprovalPrompt()
    {
        var approvalButtons = new StackPanel()
            .Horizontal()
            .Spacing(8)
            .DockRight()
            .Children(
                new Button()
                    .Content("本会话全部确定")
                    .OnClick(() => ApprovalApproveAllRequested?.Invoke()),

                new Button()
                    .Content("确定")
                    .OnClick(() => ApprovalApproveRequested?.Invoke()));

        var approvalActions = new DockPanel()
            .Children(
                new TextBlock()
                    .BindText(_approvalHintText)
                    .DockLeft()
                    .WithTheme((t, text) => text.Foreground(t.Palette.PlaceholderText)),

                approvalButtons);

        return new Border()
            .Padding(10, 8)
            .BorderThickness(1)
            .CornerRadius(0)
            .BindIsVisible(_approvalVisible)
            .WithTheme((t, border) =>
            {
                border.Background(t.Palette.ControlBackground);
                border.BorderBrush(t.Palette.Accent.Lerp(t.Palette.ControlBorder, 0.50));
            })
            .Child(
                new StackPanel()
                    .Vertical()
                    .Spacing(6)
                    .Children(
                        new DockPanel()
                            .Children(
                                new TextBlock()
                                    .BindText(_approvalTitleText)
                                    .SemiBold()
                                    .DockLeft(),

                                new TextBlock()
                                    .BindText(_approvalStateText)
                                    .DockRight()),

                        new TextBlock()
                            .BindText(_approvalDetailsText)
                            .TextWrapping(TextWrapping.Wrap),

                        approvalActions));
    }

    private FrameworkElement BuildAttachmentChips()
    {
        return new WrapPanel()
            .Orientation(Orientation.Horizontal)
            .Spacing(6)
            .ItemWidth(double.NaN)
            .ItemHeight(double.NaN)
            .Children(BuildAttachmentChipElements().ToArray());
    }

    private IEnumerable<Element> BuildAttachmentChipElements()
    {
        if (_draftAttachments.Count == 0)
        {
            yield return new TextBlock()
                .Text("无附件")
                .Foreground(Color.FromRgb(122, 132, 143));
            yield break;
        }

        for (int i = 0; i < _draftAttachments.Count; i++)
        {
            var attachment = _draftAttachments[i];
            var chip = new Border()
                .Padding(8, 4)
                .BorderThickness(1)
                .CornerRadius(6)
                .Child(
                    new DockPanel()
                        .Spacing(6)
                        .Children(
                            new TextBlock()
                                .Text(attachment.Name)
                                .DockLeft(),

                            new Button()
                                .Content("移除")
                                .DockRight()
                                .OnClick(() => RemoveDraftAttachment(attachment))
                        ));

            chip.ContextMenu = new ContextMenu()
                .Item("打开", () => OpenAttachment(attachment))
                .Item("移除", () => RemoveDraftAttachment(attachment));

            yield return chip;
        }
    }

    private FrameworkElement BuildMessageTemplate(TemplateContext ctx)
    {
        var panel = new Border()
            .Padding(12, 8)
            .Margin(0, 0, 0, 8)
            .BorderThickness(1)
            .CornerRadius(8)
            .Child(
                new StackPanel()
                    .Vertical()
                    .Spacing(6)
                    .Children(
                        new DockPanel()
                            .Children(
                                new TextBlock()
                                    .Register(ctx, "Author")
                                    .FontSize(11)
                                    .SemiBold()
                                    .DockLeft(),

                                new TextBlock()
                                    .Register(ctx, "Time")
                                    .FontSize(10)
                                    .DockRight()
                            ),

                        new StackPanel()
                            .Register(ctx, "Blocks")
                            .Vertical()
                            .Spacing(6),

                        new WrapPanel()
                            .Register(ctx, "Attachments")
                            .Orientation(Orientation.Horizontal)
                            .Spacing(6)
                            .ItemWidth(double.NaN)
                            .ItemHeight(double.NaN)
                    ));

        return panel;
    }

    private void BindMessageTemplate(FrameworkElement view, AiConversationMessage message, int index, TemplateContext ctx)
    {
        var card = (Border)view;
        var author = ctx.Get<TextBlock>("Author");
        var time = ctx.Get<TextBlock>("Time");
        var blocks = ctx.Get<StackPanel>("Blocks");
        var attachments = ctx.Get<WrapPanel>("Attachments");

        author.Text = string.IsNullOrWhiteSpace(message.Author) ? message.Role : message.Author;
        time.Text = message.Timestamp.ToLocalTime().ToString("HH:mm");
        blocks.Clear();
        foreach (var block in message.Blocks.Count == 0 ? ParseBlocks(message.Content) : message.Blocks)
        {
            blocks.Add(CreateContentBlock(block));
        }

        card.HorizontalAlignment = message.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        card.WithTheme((t, border) =>
        {
            if (message.IsUser)
            {
                border.Background(t.Palette.Accent.Lerp(t.Palette.WindowBackground, 0.90));
                border.BorderBrush(t.Palette.Accent.Lerp(t.Palette.WindowText, 0.20));
            }
            else
            {
                border.Background(t.Palette.ControlBackground);
                border.BorderBrush(t.Palette.ControlBorder);
            }
        });

        author.WithTheme((t, text) => text.Foreground(t.Palette.WindowText));
        time.WithTheme((t, text) => text.Foreground(t.Palette.DisabledText));

        attachments.Children(message.Attachments.Select(CreateAttachmentChip).ToArray());
    }

    private static Element CreateContentBlock(AiContentBlock block)
    {
        return block.Kind switch
        {
            AiContentBlockKind.Code => new Border()
                .Padding(8)
                .BorderThickness(1)
                .CornerRadius(6)
                .WithTheme((t, border) =>
                {
                    border.Background(t.Palette.WindowBackground.Lerp(t.Palette.ControlBackground, 0.35));
                    border.BorderBrush(t.Palette.ControlBorder);
                })
                .Child(
                    new TextBlock()
                        .Text(block.Text)
                        .FontFamily("Consolas")
                        .FontSize(12)
                        .TextWrapping(TextWrapping.Wrap)),

            AiContentBlockKind.Quote => new Border()
                .Padding(8, 4)
                .BorderThickness(1)
                .CornerRadius(4)
                .WithTheme((t, border) =>
                {
                    border.Background(t.Palette.ControlBackground);
                    border.BorderBrush(t.Palette.Accent.Lerp(t.Palette.ControlBorder, 0.55));
                })
                .Child(
                    new TextBlock()
                        .Text(block.Text)
                        .TextWrapping(TextWrapping.Wrap)),

            AiContentBlockKind.Canvas => block.Canvas ?? new RichCanvasPreview().Height(92),

            _ => new TextBlock()
                .Text(block.Text)
                .TextWrapping(TextWrapping.Wrap)
        };
    }

    private static Element CreateAttachmentChip(AiAttachment attachment)
    {
        return new Border()
            .Padding(6, 3)
            .BorderThickness(1)
            .CornerRadius(5)
            .Child(
                new TextBlock()
                    .Text(attachment.Name)
                    .FontSize(10));
    }

    private async void SendDraft()
    {
        string text = _input.Value.Trim();
        if (string.IsNullOrEmpty(text) && _draftAttachments.Count == 0)
        {
            _status.Value = "没有可发送内容";
            return;
        }

        AiAttachment[] attachments = _draftAttachments.ToArray();
        bool accepted = await NotifySendRequestedAsync(text, attachments);
        if (!accepted)
        {
            return;
        }

        if (_echoUserMessage)
        {
            AddMessage("user", "我", text, attachments);
        }

        _input.Value = string.Empty;
        _draftAttachments.Clear();
        _status.Value = $"已发送 {DateTime.Now:HH:mm:ss}";
        Build();
        ScrollToBottom();
    }

    private void OnComposerKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.ShiftKey)
        {
            e.Handled = true;
            SendDraft();
        }
    }

    private async Task<bool> NotifySendRequestedAsync(string text, IReadOnlyList<AiAttachment> attachments)
    {
        Func<string, IReadOnlyList<AiAttachment>, Task<bool>>? handler = SendRequested;
        if (handler == null)
        {
            return true;
        }

        foreach (Func<string, IReadOnlyList<AiAttachment>, Task<bool>> subscriber in handler.GetInvocationList())
        {
            if (!await subscriber(text, attachments))
            {
                return false;
            }
        }

        return true;
    }

    protected override void OnVisualRootChanged(Element? oldRoot, Element? newRoot)
    {
        base.OnVisualRootChanged(oldRoot, newRoot);
        if (newRoot != null && Content is null)
        {
            Build();
        }
    }

    private void RemoveDraftAttachment(AiAttachment attachment)
    {
        if (!_draftAttachments.Remove(attachment))
        {
            return;
        }

        if (DraftAttachmentRemoveRequested != null)
        {
            DraftAttachmentRemoveRequested.Invoke(attachment);
            return;
        }

        _status.Value = _draftAttachments.Count == 0 ? "就绪" : $"{_draftAttachments.Count} 个附件";
        Build();
    }

    private void OpenAttachment(AiAttachment attachment)
    {
        _status.Value = $"打开 {attachment.Name}";
    }

    private void ScrollToBottom()
    {
        if (_messagesList is null)
        {
            return;
        }

        _messagesList.ScrollIntoView(Math.Max(0, _messages.Count - 1));
    }

    private void RefreshMessages()
    {
        _messagesList?.ItemsSource?.Invalidate();
        ScrollToBottom();
    }

    private void SyncApprovalPrompt()
    {
        bool hasApproval = !string.IsNullOrWhiteSpace(_approvalTitle);
        _approvalVisible.Value = hasApproval;
        _approvalTitleText.Value = _approvalTitle ?? string.Empty;
        _approvalStateText.Value = _approvalState ?? string.Empty;
        _approvalDetailsText.Value = _approvalDetails ?? string.Empty;
        _approvalHintText.Value = _approvalHint ?? string.Empty;
    }

    private static IEnumerable<AiContentBlock> ParseBlocks(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var code = new List<string>();
        bool inCode = false;
        string? language = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    yield return new AiContentBlock { Kind = AiContentBlockKind.Code, Text = string.Join('\n', code), Language = language };
                    code.Clear();
                    inCode = false;
                    language = null;
                }
                else
                {
                    foreach (var block in FlushParagraph(paragraph))
                    {
                        yield return block;
                    }

                    inCode = true;
                    language = line.Length > 3 ? line[3..].Trim() : null;
                }

                continue;
            }

            if (inCode)
            {
                code.Add(line);
                continue;
            }

            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                foreach (var block in FlushParagraph(paragraph))
                {
                    yield return block;
                }

                yield return new AiContentBlock { Kind = AiContentBlockKind.Quote, Text = line[2..] };
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                foreach (var block in FlushParagraph(paragraph))
                {
                    yield return block;
                }

                continue;
            }

            paragraph.Add(line);
        }

        if (inCode)
        {
            yield return new AiContentBlock { Kind = AiContentBlockKind.Code, Text = string.Join('\n', code), Language = language };
        }

        foreach (var block in FlushParagraph(paragraph))
        {
            yield return block;
        }
    }

    private static IEnumerable<AiContentBlock> FlushParagraph(List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            yield break;
        }

        yield return new AiContentBlock { Kind = AiContentBlockKind.Paragraph, Text = string.Join('\n', paragraph) };
        paragraph.Clear();
    }

    private sealed class RichCanvasPreview : Control
    {
        public RichCanvasPreview()
        {
            Background = Color.FromRgb(24, 28, 34);
            BorderBrush = Color.FromArgb(255, 62, 70, 82);
            BorderThickness = 1;
            CornerRadius = 6;
        }

        protected override Size MeasureContent(Size availableSize) => new(260, 92);

        protected override void OnRender(IGraphicsContext context)
        {
            DrawBackgroundAndBorder(context, GetSnappedBorderBounds(Bounds), Background, BorderBrush, BorderThickness, CornerRadius);

            var inner = Bounds.Deflate(new Thickness(14));
            var axis = Color.FromArgb(120, 160, 170, 184);
            var accent = Theme.Palette.Accent;
            context.DrawLine(new Point(inner.X, inner.Bottom - 18), new Point(inner.Right, inner.Bottom - 18), axis, 1, true);
            context.DrawLine(new Point(inner.X + 20, inner.Y), new Point(inner.X + 20, inner.Bottom), axis, 1, true);

            double step = Math.Max(1, inner.Width / 5);
            var last = new Point(inner.X + 20, inner.Bottom - 28);
            for (int i = 1; i <= 5; i++)
            {
                var next = new Point(inner.X + 20 + step * i, inner.Bottom - 24 - Math.Sin(i * 0.9) * 24 - i * 3);
                context.DrawLine(last, next, accent, 2, true);
                context.FillEllipse(new Rect(next.X - 3, next.Y - 3, 6, 6), accent);
                last = next;
            }
        }
    }
}
