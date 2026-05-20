namespace Aprillz.MewUI.Controls.HighRisk;

/// <summary>
/// Cross-platform terminal host contract used by <see cref="TerminalControl"/>.
/// Implementations own the real process, PTY, SSH, serial, or container transport.
/// </summary>
public interface ITerminalHost : IAsyncDisposable
{
    bool IsConnected { get; }

    event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    event EventHandler<TerminalHostStateChangedEventArgs>? StateChanged;

    Task StartAsync(TerminalHostSize initialSize, CancellationToken cancellationToken = default);

    Task SendAsync(string text, CancellationToken cancellationToken = default);

    Task ResizeAsync(TerminalHostSize size, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional host-side screen source for production terminal buffers.
/// When present, <see cref="TerminalControl"/> renders this buffer instead of
/// its built-in lightweight VT text stream.
/// </summary>
public interface ITerminalScreenSource
{
    int Columns { get; }

    int Rows { get; }

    int ScrollbackLines { get; }

    int ScrollOffset { get; }

    int TotalLines { get; }

    int FirstVisibleLine { get; }

    (int Column, int Row) CursorPosition { get; }

    bool CursorVisible { get; }

    event EventHandler? ScreenChanged;

    TerminalScreenCell GetCell(int column, int absoluteLine);

    void SetScrollOffset(int linesBack);
}

/// <summary>
/// Optional host capability queried by <see cref="TerminalControl"/> for paste handling.
/// </summary>
public interface ITerminalHostCapabilities
{
    bool IsBracketedPasteModeEnabled { get; }
}

public readonly record struct TerminalHostSize(int Columns, int Rows);

public readonly record struct TerminalScreenCell(string Text, TerminalScreenAttributes Attributes)
{
    public static TerminalScreenCell Empty { get; } = new(" ", TerminalScreenAttributes.Default);
}

public readonly record struct TerminalScreenAttributes(uint Foreground, uint Background, TerminalScreenStyles Styles)
{
    public static TerminalScreenAttributes Default { get; } = new(
        0xFFCCCCCC,
        0xFF000000,
        TerminalScreenStyles.None);
}

[Flags]
public enum TerminalScreenStyles
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Underline = 1 << 2,
    Blink = 1 << 3,
    Reverse = 1 << 4,
    Hidden = 1 << 5,
    Strikethrough = 1 << 6
}

public sealed class TerminalOutputEventArgs : EventArgs
{
    public TerminalOutputEventArgs(string text)
    {
        Text = text ?? string.Empty;
    }

    public string Text { get; }
}

public sealed class TerminalHostStateChangedEventArgs : EventArgs
{
    public TerminalHostStateChangedEventArgs(bool isConnected, string statusText)
    {
        IsConnected = isConnected;
        StatusText = statusText ?? string.Empty;
    }

    public bool IsConnected { get; }

    public string StatusText { get; }
}
