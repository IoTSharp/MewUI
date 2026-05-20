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

public readonly record struct TerminalHostSize(int Columns, int Rows);

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

