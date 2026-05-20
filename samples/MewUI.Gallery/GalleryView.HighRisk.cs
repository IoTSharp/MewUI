using System.Text;

using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Controls.HighRisk;

namespace Aprillz.MewUI.Gallery;

partial class GalleryView
{
    private int highRiskVncFrame;
    private readonly ObservableValue<string> highRiskVncStatus = new("Pointer: - | Key: -");

    private FrameworkElement HighRiskPage()
    {
        TerminalControl terminal = null!;
        VncRemoteDesktopControl vnc = null!;
        AiConversationPanel ai = null!;

        return CardGrid(
            Card(
                "Terminal",
                new StackPanel()
                    .Vertical()
                    .Spacing(8)
                    .Children(
                        new TerminalControl()
                            .Ref(out terminal)
                            .Height(260)
                            .Apply(x => _ = x.AttachHostAsync(new GalleryTerminalHost())),

                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Button()
                                    .Content("Run sample")
                                    .OnClick(() => terminal.Write("dotnet --info\r\n\u001b[32mHost bridge is ready.\u001b[0m\r\n$ ")),

                                new Button()
                                    .Content("Clear")
                                    .OnClick(terminal.Clear),

                                new TextBlock()
                                    .BindText(highRiskVncStatus)
                                    .CenterVertical()
                            )
                    ),
                minWidth: 430),

            Card(
                "VNC Remote Desktop",
                new StackPanel()
                    .Vertical()
                    .Spacing(8)
                    .Children(
                        new VncRemoteDesktopControl()
                            .Ref(out vnc)
                            .Height(260)
                            .Apply(x =>
                            {
                                x.PointerStateChanged += (_, e) => highRiskVncStatus.Value = $"Pointer: mask {e.ButtonMask} @ {e.X},{e.Y} | Key: -";
                                x.KeyStateChanged += (_, e) => highRiskVncStatus.Value = $"Pointer: - | Key: 0x{e.KeySym:X} {(e.IsDown ? "down" : "up")}";
                                WriteGalleryVncFrame(x, 0);
                            }),

                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Button()
                                    .Content("Fit")
                                    .OnClick(() => vnc.ScaleMode = VncScaleMode.Fit),

                                new Button()
                                    .Content("1:1")
                                    .OnClick(() => vnc.ScaleMode = VncScaleMode.None),

                                new Button()
                                    .Content("Refresh")
                                    .OnClick(() => WriteGalleryVncFrame(vnc, ++highRiskVncFrame))
                            ),

                        new TextBlock()
                            .BindText(highRiskVncStatus)
                            .FontFamily("Consolas")
                            .FontSize(11)
                    ),
                minWidth: 430),

            Card(
                "AI Rich Interaction",
                new StackPanel()
                    .Vertical()
                    .Spacing(8)
                    .Children(
                        new AiConversationPanel()
                            .Ref(out ai)
                            .Height(420)
                            .Apply(x => x.SendRequested += (_, attachments) =>
                            {
                                x.AddMessage(
                                    "assistant",
                                    "Assistant",
                                    "Received. Rendering a structured response with code and a canvas slot.",
                                    attachments: attachments,
                                    blocks:
                                    [
                                        new AiContentBlock { Kind = AiContentBlockKind.Paragraph, Text = "Received. Rendering a structured response with code and a canvas slot." },
                                        new AiContentBlock { Kind = AiContentBlockKind.Code, Language = "json", Text = "{ \"status\": \"ok\", \"attachments\": " + attachments.Count + " }" },
                                        new AiContentBlock { Kind = AiContentBlockKind.Canvas }
                                    ]);
                                return Task.FromResult(true);
                            }),

                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Button()
                                    .Content("Add attachment")
                                    .OnClick(() => ai.AddDraftAttachment(new AiAttachment($"trace-{DateTime.Now:HHmmss}.log", "log", null, 4096))),

                                new Button()
                                    .Content("Assistant sample")
                                    .OnClick(() => ai.AddMessage(
                                        "assistant",
                                        "Assistant",
                                        "```text\nstream chunk -> rich block -> canvas\n```",
                                        blocks:
                                        [
                                            new AiContentBlock { Kind = AiContentBlockKind.Quote, Text = "Streaming output can land as typed blocks." },
                                            new AiContentBlock { Kind = AiContentBlockKind.Code, Language = "text", Text = "stream chunk -> rich block -> canvas" },
                                            new AiContentBlock { Kind = AiContentBlockKind.Canvas }
                                        ]))
                            )
                    ),
                minWidth: 520)
        );
    }

    private static void WriteGalleryVncFrame(VncRemoteDesktopControl control, int phase)
    {
        const int width = 360;
        const int height = 220;
        byte[] bgra = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                bool grid = x % 40 == 0 || y % 40 == 0;
                byte r = (byte)Math.Clamp(36 + x * 120 / width + phase * 7, 0, 255);
                byte g = (byte)Math.Clamp(42 + y * 130 / height, 0, 255);
                byte b = (byte)Math.Clamp(70 + ((x + y + phase * 13) % 110), 0, 255);

                if (grid)
                {
                    r = 92;
                    g = 112;
                    b = 136;
                }

                bgra[index] = b;
                bgra[index + 1] = g;
                bgra[index + 2] = r;
                bgra[index + 3] = 255;
            }
        }

        DrawRect(bgra, width, 34 + phase * 17 % 210, 42, 96, 54, 232, 236, 240);
        DrawRect(bgra, width, 186, 112 + phase * 11 % 54, 118, 42, 72, 210, 160);
        control.WriteFrame(width, height, bgra, width * 4);
        control.StatusText = $"{width}x{height} frame {phase}";
    }

    private static void DrawRect(byte[] bgra, int stridePixels, int left, int top, int width, int height, byte r, byte g, byte b)
    {
        int maxY = Math.Min(top + height, bgra.Length / 4 / stridePixels);
        for (int y = Math.Max(0, top); y < maxY; y++)
        {
            int maxX = Math.Min(left + width, stridePixels);
            for (int x = Math.Max(0, left); x < maxX; x++)
            {
                int index = (y * stridePixels + x) * 4;
                bgra[index] = b;
                bgra[index + 1] = g;
                bgra[index + 2] = r;
                bgra[index + 3] = 255;
            }
        }
    }

    private sealed class GalleryTerminalHost : ITerminalHost
    {
        private readonly StringBuilder _line = new();

        public bool IsConnected { get; private set; }

        public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

        public event EventHandler<TerminalHostStateChangedEventArgs>? StateChanged;

        public Task StartAsync(TerminalHostSize initialSize, CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            StateChanged?.Invoke(this, new TerminalHostStateChangedEventArgs(true, $"{initialSize.Columns}x{initialSize.Rows}"));
            OutputReceived?.Invoke(this, new TerminalOutputEventArgs(
                "\u001b[36mMewUI terminal host attached\u001b[0m\r\n" +
                "Type help, clear, or any text.\r\n$ "));
            return Task.CompletedTask;
        }

        public Task SendAsync(string text, CancellationToken cancellationToken = default)
        {
            foreach (char ch in text)
            {
                if (ch is '\r' or '\n')
                {
                    OutputReceived?.Invoke(this, new TerminalOutputEventArgs("\r\n"));
                    ExecuteLine(_line.ToString());
                    _line.Clear();
                    continue;
                }

                if (ch == '\u007f')
                {
                    if (_line.Length > 0)
                    {
                        _line.Length--;
                        OutputReceived?.Invoke(this, new TerminalOutputEventArgs("\b \b"));
                    }

                    continue;
                }

                _line.Append(ch);
                OutputReceived?.Invoke(this, new TerminalOutputEventArgs(ch.ToString()));
            }

            return Task.CompletedTask;
        }

        public Task ResizeAsync(TerminalHostSize size, CancellationToken cancellationToken = default)
        {
            StateChanged?.Invoke(this, new TerminalHostStateChangedEventArgs(IsConnected, $"{size.Columns}x{size.Rows}"));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            StateChanged?.Invoke(this, new TerminalHostStateChangedEventArgs(false, "Detached"));
            return ValueTask.CompletedTask;
        }

        private void ExecuteLine(string command)
        {
            string trimmed = command.Trim();
            if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                OutputReceived?.Invoke(this, new TerminalOutputEventArgs("\u001b[2J\u001b[H$ "));
                return;
            }

            if (trimmed.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                OutputReceived?.Invoke(this, new TerminalOutputEventArgs("Commands: help, clear, echo text\r\n$ "));
                return;
            }

            OutputReceived?.Invoke(this, new TerminalOutputEventArgs(
                string.IsNullOrEmpty(trimmed)
                    ? "$ "
                    : $"\u001b[32mecho\u001b[0m {trimmed}\r\n$ "));
        }
    }
}
