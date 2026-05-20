using System.Collections.ObjectModel;
using System.Diagnostics;

using Aprillz.MewUI.Platform;
using Aprillz.MewUI.Rendering;

namespace Aprillz.MewUI.Controls.HighRisk;

public sealed record AiAttachment(string Name, string? Kind = null, string? Path = null, long? SizeBytes = null);

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
    private readonly ObservableValue<string> _subtitle = new("AI conversation");
    private ItemsControl _messagesList = null!;
    private TextBox _inputBox = null!;

    public AiConversationPanel()
    {
        AttachInitialMessages();
        Build();
    }

    public ObservableCollection<AiConversationMessage> Messages => _messages;

    public ObservableCollection<AiAttachment> DraftAttachments => _draftAttachments;

    public event Action<string, IReadOnlyList<AiAttachment>>? SendRequested;

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

    public void AddDraftAttachment(AiAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        _draftAttachments.Add(attachment);
        _status.Value = $"{_draftAttachments.Count} attachment(s)";
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
                    .CornerRadius(8)
                    .Child(
                        new DockPanel()
                            .Children(
                                new TextBlock()
                                    .Text("AI 富交互")
                                    .FontSize(16)
                                    .SemiBold()
                                    .DockLeft(),

                                new TextBlock()
                                    .BindText(_subtitle)
                                    .DockRight()
                            )),

                BuildComposer().DockBottom(),

                new Border()
                    .BorderThickness(1)
                    .CornerRadius(8)
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

                                new TextBox()
                                    .Ref(out _inputBox)
                                    .Placeholder("Write a reply or paste content")
                                    .BindText(_input)
                                    .OnKeyDown(OnComposerKeyDown)
                                    .ToolTip("Enter to send, Shift+Enter for newline")
                            ),

                        new StackPanel()
                            .DockBottom()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Button()
                                    .Content("Attach")
                                    .OnClick(() => AddDraftAttachment(new AiAttachment("attachment.txt", "file", "attachment.txt", 2048))),

                                new Button()
                                    .Content("Send")
                                    .OnClick(SendDraft),

                                new TextBlock()
                                    .BindText(_status)
                                    .CenterVertical()
                            )
                    ));
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
                .Text("No attachments")
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
                                .Content("Remove")
                                .DockRight()
                                .OnClick(() => RemoveDraftAttachment(attachment))
                        ));

            chip.ContextMenu = new ContextMenu()
                .Item("Open", () => OpenAttachment(attachment))
                .Item("Remove", () => RemoveDraftAttachment(attachment));

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

    private void SendDraft()
    {
        string text = _input.Value.Trim();
        if (string.IsNullOrEmpty(text) && _draftAttachments.Count == 0)
        {
            _status.Value = "Nothing to send";
            return;
        }

        AddMessage("user", "You", text, _draftAttachments.ToArray());
        SendRequested?.Invoke(text, _draftAttachments.ToArray());
        _input.Value = string.Empty;
        _draftAttachments.Clear();
        _status.Value = $"Sent at {DateTime.Now:HH:mm:ss}";
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

    private void RemoveDraftAttachment(AiAttachment attachment)
    {
        _draftAttachments.Remove(attachment);
        _status.Value = _draftAttachments.Count == 0 ? "Ready" : $"{_draftAttachments.Count} attachment(s)";
        Build();
    }

    private void OpenAttachment(AiAttachment attachment)
    {
        _status.Value = $"Open {attachment.Name}";
    }

    private void ScrollToBottom()
    {
        if (_messagesList is null)
        {
            return;
        }

        _messagesList.ScrollIntoView(Math.Max(0, _messages.Count - 1));
    }

    private void AttachInitialMessages()
    {
        AddMessage(
            "assistant",
            "Assistant",
            "Rich content is available.\n\n> Attach files, render code, and reserve canvas space.\n\n```csharp\npanel.AddMessage(\"assistant\", \"AI\", \"done\");\n```",
            blocks:
            [
                new AiContentBlock { Kind = AiContentBlockKind.Paragraph, Text = "Rich content is available." },
                new AiContentBlock { Kind = AiContentBlockKind.Quote, Text = "Attach files, render code, and reserve canvas space." },
                new AiContentBlock { Kind = AiContentBlockKind.Code, Language = "csharp", Text = "panel.AddMessage(\"assistant\", \"AI\", \"done\");" },
                new AiContentBlock { Kind = AiContentBlockKind.Canvas, Canvas = new RichCanvasPreview().Height(92) }
            ]);

        AddMessage("user", "You", "Great, let's wire it into the gallery.");
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
