using System.Globalization;
using System.Text;

using Aprillz.MewUI.Input;
using Aprillz.MewUI.Rendering;

namespace Aprillz.MewUI.Controls.HighRisk;

/// <summary>
/// Terminal input payload emitted by <see cref="TerminalControl"/>.
/// </summary>
public sealed class TerminalInputEventArgs : EventArgs
{
    public TerminalInputEventArgs(string text)
    {
        Text = text ?? string.Empty;
    }

    public string Text { get; }
}

/// <summary>
/// Lightweight VT-style terminal surface with its own screen buffer, selection, keyboard input, and IME bridge.
/// </summary>
public sealed class TerminalControl : Control, ITextInputClient, ITextCompositionClient
{
    private const int MinColumns = 8;
    private const int MinRows = 4;
    private const int DefaultColumns = 80;
    private const int DefaultRows = 24;
    private const int DefaultScrollbackLimit = 2000;
    private const char Escape = '\u001b';
    private static readonly Color DefaultBackground = Color.FromRgb(12, 16, 20);
    private static readonly Color DefaultForeground = Color.FromRgb(218, 224, 232);
    private static readonly Color CursorColor = Color.FromRgb(245, 245, 245);
    private static readonly Color SelectionColor = Color.FromArgb(96, 80, 150, 255);
    private static readonly Color CompositionColor = Color.FromArgb(180, 255, 216, 102);

    private readonly List<Cell[]> _scrollback = new();
    private Cell[] _screen = Array.Empty<Cell>();
    private readonly StringBuilder _ansi = new();
    private readonly StringBuilder _clipboard = new();
    private readonly StringBuilder _composition = new();

    private ITerminalHost? _host;
    private ITerminalScreenSource? _screenSource;
    private bool _isHostInputAttached;
    private int _columns = DefaultColumns;
    private int _rows = DefaultRows;
    private int _cursorColumn;
    private int _cursorRow;
    private int _scrollbackOffset;
    private int _scrollbackLimit = DefaultScrollbackLimit;
    private double _cellWidth = 8;
    private double _lineHeight = 16;
    private bool _isSelecting;
    private TerminalPosition? _selectionAnchor;
    private TerminalPosition? _selectionActive;
    private int _savedCursorColumn;
    private int _savedCursorRow;
    private bool _cursorVisible = true;
    private bool _isComposing;

    public TerminalControl()
    {
        FontFamily = "Consolas";
        FontSize = 13;
        Background = DefaultBackground;
        Foreground = DefaultForeground;
        BorderBrush = Color.FromArgb(255, 40, 48, 58);
        BorderThickness = 1;
        Padding = new Thickness(10, 8);
        Cursor = CursorType.IBeam;
        Resize(DefaultColumns, DefaultRows, clear: true);
        ContextMenu = CreateContextMenu();
    }

    public override bool Focusable => true;

    public int Columns => _screenSource?.Columns ?? _columns;

    public int Rows => _screenSource?.Rows ?? _rows;

    public int ScrollbackLineCount => _screenSource?.ScrollbackLines ?? _scrollback.Count;

    public ITerminalHost? Host => _host;

    public ITerminalScreenSource? ScreenSource => _screenSource;

    public int ScrollbackLimit
    {
        get => _scrollbackLimit;
        set
        {
            _scrollbackLimit = Math.Clamp(value, 0, 100_000);
            TrimScrollback();
            InvalidateVisual();
        }
    }

    public event EventHandler<TerminalInputEventArgs>? InputRequested;

    public event Action<int, int>? TerminalResized;

    public event EventHandler<TerminalHostStateChangedEventArgs>? HostStateChanged;

    public void Write(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        ProcessInput(text);
        InvalidateVisual();
    }

    public async Task AttachHostAsync(ITerminalHost host, bool clear = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await DetachHostAsync().ConfigureAwait(false);
        int initialColumns = Columns;
        int initialRows = Rows;
        if (clear)
        {
            Clear();
        }

        _host = host;
        host.OutputReceived += OnHostOutputReceived;
        host.StateChanged += OnHostStateChanged;
        if (host is ITerminalScreenSource screenSource)
        {
            _screenSource = screenSource;
            screenSource.ScreenChanged += OnHostScreenChanged;
        }

        if (!_isHostInputAttached)
        {
            InputRequested += OnTerminalInputRequested;
            TerminalResized += OnTerminalResizedForHost;
            _isHostInputAttached = true;
        }

        await host.StartAsync(new TerminalHostSize(initialColumns, initialRows), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DetachHostAsync()
    {
        var host = _host;
        if (host == null)
        {
            return;
        }

        host.OutputReceived -= OnHostOutputReceived;
        host.StateChanged -= OnHostStateChanged;
        if (_screenSource != null)
        {
            _screenSource.ScreenChanged -= OnHostScreenChanged;
            _screenSource = null;
        }

        _host = null;
        await host.DisposeAsync().ConfigureAwait(false);
    }

    public void Clear()
    {
        _screenSource?.SetScrollOffset(0);
        _scrollback.Clear();
        _scrollbackOffset = 0;
        ClearScreen();
        _cursorColumn = 0;
        _cursorRow = 0;
        ClearSelection();
        InvalidateVisual();
    }

    public string GetSelectedText()
    {
        if (_screenSource != null)
        {
            return GetSelectedTextFromScreenSource(_screenSource);
        }

        if (!TryGetSelectionRange(out var start, out var end))
        {
            return string.Empty;
        }

        _clipboard.Clear();
        int screenStart = _scrollback.Count;
        for (int line = start.Line; line <= end.Line; line++)
        {
            var cells = GetAbsoluteLine(line);
            int firstColumn = line == start.Line ? start.Column : 0;
            int lastColumn = line == end.Line ? end.Column : _columns - 1;
            firstColumn = Math.Clamp(firstColumn, 0, _columns - 1);
            lastColumn = Math.Clamp(lastColumn, 0, _columns - 1);
            if (lastColumn >= firstColumn)
            {
                AppendTrimmed(_clipboard, cells, firstColumn, lastColumn);
            }

            if (line < end.Line)
            {
                _clipboard.AppendLine();
            }
        }

        return _clipboard.ToString();

        Cell[] GetAbsoluteLine(int line)
        {
            if (line < screenStart)
            {
                return _scrollback[line];
            }

            return GetScreenRow(line - screenStart);
        }
    }

    public bool CopySelectionToClipboard()
    {
        var selected = GetSelectedText();
        if (selected.Length == 0 || !Application.IsRunning)
        {
            return false;
        }

        return Application.Current.PlatformHost.Clipboard.TrySetText(selected);
    }

    public bool PasteFromClipboard()
    {
        if (!Application.IsRunning || !Application.Current.PlatformHost.Clipboard.TryGetText(out var text) || string.IsNullOrEmpty(text))
        {
            return false;
        }

        SendInput(PreparePasteText(text));
        return true;
    }

    public void SelectAll()
    {
        int totalLines = _screenSource?.TotalLines ?? (_scrollback.Count + _rows);
        int columns = _screenSource?.Columns ?? _columns;
        if (totalLines == 0 || columns == 0)
        {
            return;
        }

        _selectionAnchor = new TerminalPosition(0, 0);
        _selectionActive = new TerminalPosition(totalLines - 1, columns - 1);
        InvalidateVisual();
    }

    bool ITextCompositionClient.IsComposing => _isComposing;

    int ITextCompositionClient.CompositionStartIndex => 0;

    Rect ITextCompositionClient.GetCharRectInWindow(int charIndex)
    {
        var bounds = GetTerminalBounds();
        var (column, row) = GetCursorPosition();
        var local = new Rect(
            bounds.X + column * _cellWidth,
            bounds.Y + Math.Clamp(row, 0, Math.Max(0, Rows - 1)) * _lineHeight,
            Math.Max(1, _cellWidth),
            Math.Max(1, _lineHeight));
        try
        {
            return TranslateRect(new Rect(local.X - Bounds.X, local.Y - Bounds.Y, local.Width, local.Height), FindVisualRoot() ?? this);
        }
        catch (InvalidOperationException)
        {
            return local;
        }
    }

    void ITextInputClient.HandleTextInput(TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        SendInput(e.Text);
        _composition.Clear();
        _isComposing = false;
        e.Handled = true;
    }

    void ITextCompositionClient.HandleTextCompositionStart(TextCompositionEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        _isComposing = true;
        _composition.Clear();
        e.Handled = true;
        InvalidateVisual();
    }

    void ITextCompositionClient.HandleTextCompositionUpdate(TextCompositionEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        _isComposing = true;
        _composition.Clear();
        _composition.Append(e.Text);
        e.Handled = true;
        InvalidateVisual();
    }

    void ITextCompositionClient.HandleTextCompositionEnd(TextCompositionEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (!string.IsNullOrEmpty(e.Text))
        {
            SendInput(e.Text);
        }

        _composition.Clear();
        _isComposing = false;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override Size MeasureContent(Size availableSize)
    {
        EnsureCellMetrics();
        var content = new Size(Columns * _cellWidth, Rows * _lineHeight).Inflate(Padding);
        var border = GetBorderVisualInset();
        if (border > 0)
        {
            content = content.Inflate(new Thickness(border));
        }

        return content;
    }

    protected override void ArrangeContent(Rect bounds)
    {
        base.ArrangeContent(bounds);
        EnsureCellMetrics();
        var terminal = GetTerminalBounds();
        int newColumns = Math.Max(MinColumns, (int)Math.Floor(terminal.Width / Math.Max(1, _cellWidth)));
        int newRows = Math.Max(MinRows, (int)Math.Floor(terminal.Height / Math.Max(1, _lineHeight)));
        if (newColumns != Columns || newRows != Rows)
        {
            if (_screenSource == null)
            {
                Resize(newColumns, newRows, clear: false);
            }
            else
            {
                _columns = newColumns;
                _rows = newRows;
            }

            TerminalResized?.Invoke(newColumns, newRows);
        }
    }

    protected override void OnRender(IGraphicsContext context)
    {
        DrawBackgroundAndBorder(context, GetSnappedBorderBounds(Bounds), Background, BorderBrush, BorderThickness, CornerRadius);
        EnsureCellMetrics();

        var terminalBounds = GetTerminalBounds();
        context.Save();
        context.SetClip(terminalBounds);
        try
        {
            DrawTerminal(context, terminalBounds);
        }
        finally
        {
            context.Restore();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled)
        {
            return;
        }

        if (e.Button == MouseButton.Left)
        {
            Focus();
            var pos = PointToTerminalPosition(e.Position);
            _selectionAnchor = pos;
            _selectionActive = pos;
            _isSelecting = true;
            if (FindVisualRoot() is Window window)
            {
                window.CaptureMouse(this);
            }

            e.Handled = true;
            InvalidateVisual();
        }
        else if (e.Button == MouseButton.Right && ContextMenu != null)
        {
            ContextMenu.ShowAt(this, e.Position);
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isSelecting && IsMouseCaptured && e.LeftButton)
        {
            _selectionActive = PointToTerminalPosition(e.Position);
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButton.Left && _isSelecting)
        {
            _selectionActive = PointToTerminalPosition(e.Position);
            _isSelecting = false;
            if (FindVisualRoot() is Window window)
            {
                window.ReleaseMouseCapture();
            }

            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int delta = e.Delta > 0 ? -3 : 3;
        if (_screenSource != null)
        {
            _screenSource.SetScrollOffset(_screenSource.ScrollOffset + delta);
        }
        else
        {
            _scrollbackOffset = Math.Clamp(_scrollbackOffset + delta, 0, _scrollback.Count);
        }

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if (e.PrimaryKey)
        {
            if (e.Key == Key.C && GetSelectedText().Length > 0)
            {
                CopySelectionToClipboard();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.V)
            {
                PasteFromClipboard();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.A)
            {
                SelectAll();
                e.Handled = true;
                return;
            }
        }

        string? sequence = KeyToSequence(e);
        if (sequence != null)
        {
            SendInput(sequence);
            e.Handled = true;
        }
    }

    protected override void OnDispose()
    {
        base.OnDispose();
        if (_isHostInputAttached)
        {
            InputRequested -= OnTerminalInputRequested;
            TerminalResized -= OnTerminalResizedForHost;
            _isHostInputAttached = false;
        }

        var host = _host;
        if (host != null)
        {
            host.OutputReceived -= OnHostOutputReceived;
            host.StateChanged -= OnHostStateChanged;
            if (_screenSource != null)
            {
                _screenSource.ScreenChanged -= OnHostScreenChanged;
                _screenSource = null;
            }

            _host = null;
            _ = host.DisposeAsync();
        }
    }

    private void DrawTerminal(IGraphicsContext context, Rect bounds)
    {
        if (_screenSource != null)
        {
            DrawScreenSource(context, bounds, _screenSource);
            return;
        }

        var font = GetFont(GetGraphicsFactory());
        var visibleStart = Math.Max(0, _scrollback.Count - _scrollbackOffset);
        int historicalRows = Math.Min(_rows, Math.Max(0, _scrollback.Count - visibleStart));
        int screenRows = _rows - historicalRows;
        int firstScreenRow = Math.Max(0, _rows - screenRows);

        for (int row = 0; row < _rows; row++)
        {
            Cell[] cells;
            if (row < historicalRows)
            {
                cells = _scrollback[visibleStart + row];
            }
            else
            {
                cells = GetScreenRow(row - historicalRows + firstScreenRow);
            }

            double y = bounds.Y + row * _lineHeight;
            DrawRow(context, font, cells, row, bounds.X, y);
        }

        DrawComposition(context, font, bounds);

        if (_cursorVisible && _scrollbackOffset == 0 && IsFocused)
        {
            var cursorRect = new Rect(bounds.X + _cursorColumn * _cellWidth, bounds.Y + _cursorRow * _lineHeight, Math.Max(2, _cellWidth), Math.Max(2, _lineHeight));
            context.FillRectangle(new Rect(cursorRect.X, cursorRect.Bottom - 2, cursorRect.Width, 2), CursorColor);
        }
    }

    private void DrawScreenSource(IGraphicsContext context, Rect bounds, ITerminalScreenSource source)
    {
        var font = GetFont(GetGraphicsFactory());
        int rows = Math.Min(Rows, source.Rows);
        int columns = Math.Min(Columns, source.Columns);
        int firstLine = source.FirstVisibleLine;

        for (int row = 0; row < rows; row++)
        {
            int absoluteLine = firstLine + row;
            double y = bounds.Y + row * _lineHeight;
            DrawScreenSourceRow(context, font, source, absoluteLine, row, columns, bounds.X, y);
        }

        DrawComposition(context, font, bounds);

        if (source.CursorVisible && source.ScrollOffset == 0 && IsFocused)
        {
            var (cursorColumn, cursorRow) = source.CursorPosition;
            if (cursorRow >= 0 && cursorRow < rows && cursorColumn >= 0 && cursorColumn < columns)
            {
                var cursorRect = new Rect(bounds.X + cursorColumn * _cellWidth, bounds.Y + cursorRow * _lineHeight, Math.Max(2, _cellWidth), Math.Max(2, _lineHeight));
                context.FillRectangle(new Rect(cursorRect.X, cursorRect.Bottom - 2, cursorRect.Width, 2), CursorColor);
            }
        }
    }

    private void DrawScreenSourceRow(
        IGraphicsContext context,
        IFont font,
        ITerminalScreenSource source,
        int absoluteLine,
        int visibleRow,
        int columns,
        double x,
        double y)
    {
        for (int col = 0; col < columns; col++)
        {
            var cell = source.GetCell(col, absoluteLine);
            if (IsContinuationCell(cell))
            {
                continue;
            }

            var rect = new Rect(x + col * _cellWidth, y, _cellWidth, _lineHeight);
            var background = ToColor(cell.Attributes.Background);
            if (background.A > 0 && background != Background)
            {
                double width = IsWideCell(cell) && col + 1 < columns ? _cellWidth * 2 : _cellWidth;
                context.FillRectangle(new Rect(rect.X, rect.Y, width, rect.Height), background);
            }

            if (IsCellSelected(visibleRow, col))
            {
                context.FillRectangle(rect, SelectionColor);
            }
        }

        int start = 0;
        while (start < columns)
        {
            while (start < columns && IsBlankOrContinuationCell(source.GetCell(start, absoluteLine)))
            {
                start++;
            }

            if (start >= columns)
            {
                break;
            }

            var first = source.GetCell(start, absoluteLine);
            var color = ToColor(first.Attributes.Foreground);
            int end = start + (IsWideCell(first) ? 2 : 1);
            while (end < columns)
            {
                var next = source.GetCell(end, absoluteLine);
                if (IsBlankOrContinuationCell(next) || ToColor(next.Attributes.Foreground) != color)
                {
                    break;
                }

                end += IsWideCell(next) ? 2 : 1;
            }

            var text = new StringBuilder(end - start);
            for (int i = start; i < end && i < columns; i++)
            {
                var cell = source.GetCell(i, absoluteLine);
                if (!IsContinuationCell(cell))
                {
                    text.Append(cell.Text);
                }
            }

            context.DrawText(text.ToString(), new Rect(x + start * _cellWidth, y, (end - start) * _cellWidth, _lineHeight), font, color, TextAlignment.Left, TextAlignment.Top, TextWrapping.NoWrap);
            start = end;
        }
    }

    private void DrawRow(IGraphicsContext context, IFont font, Cell[] cells, int visibleRow, double x, double y)
    {
        for (int col = 0; col < _columns; col++)
        {
            var cell = cells[col];
            var rect = new Rect(x + col * _cellWidth, y, _cellWidth, _lineHeight);
            if (cell.Background.A > 0 && cell.Background != Background)
            {
                context.FillRectangle(rect, cell.Background);
            }

            if (IsCellSelected(visibleRow, col))
            {
                context.FillRectangle(rect, SelectionColor);
            }
        }

        int start = 0;
        while (start < _columns)
        {
            while (start < _columns && cells[start].Rune == ' ')
            {
                start++;
            }

            if (start >= _columns)
            {
                break;
            }

            var color = cells[start].Foreground;
            int end = start + 1;
            while (end < _columns && cells[end].Rune != ' ' && cells[end].Foreground == color)
            {
                end++;
            }

            var text = new StringBuilder(end - start);
            for (int i = start; i < end; i++)
            {
                char ch = cells[i].Rune == '\0' ? ' ' : cells[i].Rune;
                text.Append(ch);
            }

            context.DrawText(text.ToString(), new Rect(x + start * _cellWidth, y, (end - start) * _cellWidth, _lineHeight), font, color, TextAlignment.Left, TextAlignment.Top, TextWrapping.NoWrap);
            start = end;
        }
    }

    private void DrawComposition(IGraphicsContext context, IFont font, Rect bounds)
    {
        if (!_isComposing || _composition.Length == 0 || GetCurrentScrollOffset() != 0)
        {
            return;
        }

        var (column, row) = GetCursorPosition();
        double x = bounds.X + column * _cellWidth;
        double y = bounds.Y + row * _lineHeight;
        double width = Math.Max(_cellWidth, _composition.Length * _cellWidth);
        var rect = new Rect(x, y, width, _lineHeight);
        context.FillRectangle(rect, Color.FromArgb(40, 255, 216, 102));
        context.DrawText(_composition.ToString(), rect, font, CompositionColor, TextAlignment.Left, TextAlignment.Top, TextWrapping.NoWrap);
        context.DrawLine(new Point(rect.X, rect.Bottom - 1), new Point(rect.Right, rect.Bottom - 1), CompositionColor, 1, true);
    }

    private void ProcessInput(string text)
    {
        foreach (var ch in text)
        {
            if (_ansi.Length > 0 || ch == Escape)
            {
                ProcessAnsiChar(ch);
                continue;
            }

            WriteChar(ch);
        }
    }

    private void ProcessAnsiChar(char ch)
    {
        _ansi.Append(ch);
        if (_ansi.Length == 1)
        {
            return;
        }

        if (_ansi.Length == 2 && ch != '[')
        {
            _ansi.Clear();
            return;
        }

        if (ch is >= '@' and <= '~')
        {
            ExecuteCsi(_ansi.ToString());
            _ansi.Clear();
        }
        else if (_ansi.Length > 64)
        {
            _ansi.Clear();
        }
    }

    private void ExecuteCsi(string sequence)
    {
        if (sequence.Length < 3 || sequence[0] != Escape || sequence[1] != '[')
        {
            return;
        }

        char command = sequence[^1];
        string payload = sequence.Substring(2, sequence.Length - 3);
        int[] args = ParseCsiArgs(payload);
        switch (command)
        {
            case 'A':
                _cursorRow = Math.Max(0, _cursorRow - Arg(args, 0, 1));
                break;
            case 'B':
                _cursorRow = Math.Min(_rows - 1, _cursorRow + Arg(args, 0, 1));
                break;
            case 'C':
                _cursorColumn = Math.Min(_columns - 1, _cursorColumn + Arg(args, 0, 1));
                break;
            case 'D':
                _cursorColumn = Math.Max(0, _cursorColumn - Arg(args, 0, 1));
                break;
            case 'H':
            case 'f':
                _cursorRow = Math.Clamp(Arg(args, 0, 1) - 1, 0, _rows - 1);
                _cursorColumn = Math.Clamp(Arg(args, 1, 1) - 1, 0, _columns - 1);
                break;
            case 'J':
                if (Arg(args, 0, 0) is 2 or 3)
                {
                    ClearScreen();
                }
                break;
            case 'K':
                ClearLine(_cursorRow, _cursorColumn, _columns - 1);
                break;
            case 's':
                _savedCursorColumn = _cursorColumn;
                _savedCursorRow = _cursorRow;
                break;
            case 'u':
                _cursorColumn = Math.Clamp(_savedCursorColumn, 0, _columns - 1);
                _cursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
                break;
            case 'h':
                if (payload == "?25")
                {
                    _cursorVisible = true;
                }
                break;
            case 'l':
                if (payload == "?25")
                {
                    _cursorVisible = false;
                }
                break;
        }
    }

    private void WriteChar(char ch)
    {
        switch (ch)
        {
            case '\r':
                _cursorColumn = 0;
                break;
            case '\n':
                NewLine();
                break;
            case '\b':
                _cursorColumn = Math.Max(0, _cursorColumn - 1);
                break;
            case '\t':
                int nextTab = Math.Min(_columns - 1, ((_cursorColumn / 4) + 1) * 4);
                while (_cursorColumn < nextTab)
                {
                    PutChar(' ');
                }
                break;
            default:
                if (!char.IsControl(ch))
                {
                    PutChar(ch);
                }
                break;
        }
    }

    private void PutChar(char ch)
    {
        SetCell(_cursorRow, _cursorColumn, new Cell(ch, Foreground, Background));
        _cursorColumn++;
        if (_cursorColumn >= _columns)
        {
            _cursorColumn = 0;
            NewLine();
        }
    }

    private void NewLine()
    {
        _cursorRow++;
        if (_cursorRow < _rows)
        {
            return;
        }

        AddScrollbackLine(GetScreenRow(0));
        for (int row = 1; row < _rows; row++)
        {
            Array.Copy(_screen, row * _columns, _screen, (row - 1) * _columns, _columns);
        }

        ClearLine(_rows - 1, 0, _columns - 1);
        _cursorRow = _rows - 1;
    }

    private void Resize(int columns, int rows, bool clear)
    {
        columns = Math.Max(MinColumns, columns);
        rows = Math.Max(MinRows, rows);
        var oldScreen = _screen;
        int oldColumns = _columns;
        int oldRows = _rows;

        _columns = columns;
        _rows = rows;
        _screen = new Cell[_columns * _rows];
        for (int i = 0; i < _screen.Length; i++)
        {
            _screen[i] = Cell.CreateBlank(Foreground, Background);
        }

        if (!clear && oldScreen.Length > 0)
        {
            int copyRows = Math.Min(oldRows, rows);
            int copyColumns = Math.Min(oldColumns, columns);
            for (int row = 0; row < copyRows; row++)
            {
                Array.Copy(oldScreen, row * oldColumns, _screen, row * columns, copyColumns);
            }
        }

        _cursorColumn = Math.Clamp(_cursorColumn, 0, _columns - 1);
        _cursorRow = Math.Clamp(_cursorRow, 0, _rows - 1);
        _scrollbackOffset = Math.Clamp(_scrollbackOffset, 0, _scrollback.Count);
    }

    private void ClearScreen()
    {
        for (int i = 0; i < _screen.Length; i++)
        {
            _screen[i] = Cell.CreateBlank(Foreground, Background);
        }
    }

    private void ClearLine(int row, int firstColumn, int lastColumn)
    {
        var line = GetScreenRow(row);
        for (int col = Math.Max(0, firstColumn); col <= Math.Min(_columns - 1, lastColumn); col++)
        {
            SetCell(row, col, Cell.CreateBlank(Foreground, Background));
        }
    }

    private void AddScrollbackLine(Cell[] line)
    {
        var copy = new Cell[_columns];
        Array.Copy(line, copy, _columns);
        _scrollback.Add(copy);
        TrimScrollback();
    }

    private void TrimScrollback()
    {
        if (_scrollback.Count <= _scrollbackLimit)
        {
            return;
        }

        int remove = _scrollback.Count - _scrollbackLimit;
        _scrollback.RemoveRange(0, remove);
        _scrollbackOffset = Math.Clamp(_scrollbackOffset, 0, _scrollback.Count);
    }

    private Cell[] GetScreenRow(int row)
    {
        row = Math.Clamp(row, 0, _rows - 1);
        var result = new Cell[_columns];
        Array.Copy(_screen, row * _columns, result, 0, _columns);
        return result;
    }

    private void SetCell(int row, int column, Cell cell)
    {
        row = Math.Clamp(row, 0, _rows - 1);
        column = Math.Clamp(column, 0, _columns - 1);
        _screen[row * _columns + column] = cell;
    }

    private void EnsureCellMetrics()
    {
        using var measure = BeginTextMeasurement();
        var size = measure.Context.MeasureText("W", measure.Font);
        _cellWidth = Math.Max(6, Math.Ceiling(size.Width));
        _lineHeight = Math.Max(12, Math.Ceiling(measure.Context.MeasureText("Mg", measure.Font).Height + 2));
    }

    private Rect GetTerminalBounds()
    {
        var border = GetBorderVisualInset();
        var bounds = border > 0 ? Bounds.Deflate(new Thickness(border)) : Bounds;
        return bounds.Deflate(Padding);
    }

    private TerminalPosition PointToTerminalPosition(Point point)
    {
        var bounds = GetTerminalBounds();
        int column = (int)Math.Floor((point.X - bounds.X) / Math.Max(1, _cellWidth));
        int row = (int)Math.Floor((point.Y - bounds.Y) / Math.Max(1, _lineHeight));
        column = Math.Clamp(column, 0, Math.Max(0, Columns - 1));
        row = Math.Clamp(row, 0, Math.Max(0, Rows - 1));
        int absoluteLine = (_screenSource?.FirstVisibleLine ?? Math.Max(0, _scrollback.Count - _scrollbackOffset)) + row;
        return new TerminalPosition(absoluteLine, column);
    }

    private bool TryGetSelectionRange(out TerminalPosition start, out TerminalPosition end)
    {
        start = default;
        end = default;
        if (_selectionAnchor is not TerminalPosition anchor || _selectionActive is not TerminalPosition active)
        {
            return false;
        }

        start = anchor.CompareTo(active) <= 0 ? anchor : active;
        end = anchor.CompareTo(active) <= 0 ? active : anchor;
        return start != end;
    }

    private bool IsCellSelected(int visibleRow, int column)
    {
        if (!TryGetSelectionRange(out var start, out var end))
        {
            return false;
        }

        int absoluteLine = (_screenSource?.FirstVisibleLine ?? Math.Max(0, _scrollback.Count - _scrollbackOffset)) + visibleRow;
        var pos = new TerminalPosition(absoluteLine, column);
        return pos.CompareTo(start) >= 0 && pos.CompareTo(end) <= 0;
    }

    private void ClearSelection()
    {
        _selectionAnchor = null;
        _selectionActive = null;
    }

    private void SendInput(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        InputRequested?.Invoke(this, new TerminalInputEventArgs(text));
    }

    private void OnHostOutputReceived(object? sender, TerminalOutputEventArgs e)
    {
        if (_screenSource != null)
        {
            return;
        }

        DispatchToUi(() => Write(e.Text));
    }

    private void OnHostStateChanged(object? sender, TerminalHostStateChangedEventArgs e)
    {
        DispatchToUi(() => HostStateChanged?.Invoke(this, e));
    }

    private void OnHostScreenChanged(object? sender, EventArgs e)
    {
        DispatchToUi(InvalidateVisual);
    }

    private void OnTerminalInputRequested(object? sender, TerminalInputEventArgs e)
    {
        var host = _host;
        if (host == null || !host.IsConnected)
        {
            return;
        }

        _ = host.SendAsync(e.Text);
    }

    private void OnTerminalResizedForHost(int columns, int rows)
    {
        var host = _host;
        if (host == null || !host.IsConnected)
        {
            return;
        }

        _ = host.ResizeAsync(new TerminalHostSize(columns, rows));
    }

    private static void DispatchToUi(Action action)
    {
        if (Application.IsRunning && Application.Current.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(action);
            return;
        }

        action();
    }

    private string? KeyToSequence(KeyEventArgs e)
    {
        return e.Key switch
        {
            Key.Enter => "\r",
            Key.Tab => "\t",
            Key.Backspace => "\u007f",
            Key.Escape => "\u001b",
            Key.Left => "\u001b[D",
            Key.Right => "\u001b[C",
            Key.Up => "\u001b[A",
            Key.Down => "\u001b[B",
            Key.Home => "\u001b[H",
            Key.End => "\u001b[F",
            Key.PageUp => "\u001b[5~",
            Key.PageDown => "\u001b[6~",
            Key.Insert => "\u001b[2~",
            Key.Delete => "\u001b[3~",
            _ => null
        };
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        menu.AddItem("Copy", () => CopySelectionToClipboard(), isEnabled: true, shortcut: new KeyGesture(Key.C, ModifierKeys.Primary));
        menu.AddItem("Paste", () => PasteFromClipboard(), isEnabled: true, shortcut: new KeyGesture(Key.V, ModifierKeys.Primary));
        menu.AddSeparator();
        menu.AddItem("Select All", SelectAll, isEnabled: true, shortcut: new KeyGesture(Key.A, ModifierKeys.Primary));
        menu.AddItem("Clear", Clear);
        return menu;
    }

    private string GetSelectedTextFromScreenSource(ITerminalScreenSource source)
    {
        if (!TryGetSelectionRange(out var start, out var end))
        {
            return string.Empty;
        }

        _clipboard.Clear();
        int lastLine = Math.Max(0, source.TotalLines - 1);
        int columns = source.Columns;
        for (int line = Math.Clamp(start.Line, 0, lastLine); line <= Math.Clamp(end.Line, 0, lastLine); line++)
        {
            int firstColumn = line == start.Line ? start.Column : 0;
            int lastColumn = line == end.Line ? end.Column : columns - 1;
            firstColumn = Math.Clamp(firstColumn, 0, Math.Max(0, columns - 1));
            lastColumn = Math.Clamp(lastColumn, 0, Math.Max(0, columns - 1));
            if (lastColumn >= firstColumn)
            {
                AppendTrimmed(_clipboard, source, line, firstColumn, lastColumn);
            }

            if (line < end.Line)
            {
                _clipboard.AppendLine();
            }
        }

        return _clipboard.ToString();
    }

    private static void AppendTrimmed(StringBuilder builder, Cell[] cells, int firstColumn, int lastColumn)
    {
        while (lastColumn >= firstColumn && cells[lastColumn].Rune == ' ')
        {
            lastColumn--;
        }

        for (int i = firstColumn; i <= lastColumn; i++)
        {
            builder.Append(cells[i].Rune == '\0' ? ' ' : cells[i].Rune);
        }
    }

    private static void AppendTrimmed(StringBuilder builder, ITerminalScreenSource source, int line, int firstColumn, int lastColumn)
    {
        while (lastColumn >= firstColumn && IsBlankOrContinuationCell(source.GetCell(lastColumn, line)))
        {
            lastColumn--;
        }

        for (int i = firstColumn; i <= lastColumn; i++)
        {
            var cell = source.GetCell(i, line);
            if (!IsContinuationCell(cell))
            {
                builder.Append(string.IsNullOrEmpty(cell.Text) ? " " : cell.Text);
            }
        }
    }

    private string PreparePasteText(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (_host is ITerminalHostCapabilities { IsBracketedPasteModeEnabled: true })
        {
            return "\u001b[200~" + normalized + "\u001b[201~";
        }

        return normalized.Replace('\n', '\r');
    }

    private (int Column, int Row) GetCursorPosition()
    {
        if (_screenSource != null)
        {
            return _screenSource.CursorPosition;
        }

        return (_cursorColumn, _cursorRow);
    }

    private int GetCurrentScrollOffset()
        => _screenSource?.ScrollOffset ?? _scrollbackOffset;

    private static bool IsBlankOrContinuationCell(TerminalScreenCell cell)
        => IsContinuationCell(cell) || string.IsNullOrEmpty(cell.Text) || cell.Text == " ";

    private static bool IsContinuationCell(TerminalScreenCell cell)
        => cell.Text == "\0";

    private static bool IsWideCell(TerminalScreenCell cell)
        => !IsContinuationCell(cell) && cell.Text.Length > 0 && char.ConvertToUtf32(cell.Text, 0) > 0x7F;

    private static Color ToColor(uint argb)
        => Color.FromArgb(argb);

    private static int[] ParseCsiArgs(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return Array.Empty<int>();
        }

        payload = payload.TrimStart('?');
        string[] parts = payload.Split(';', StringSplitOptions.None);
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            result[i] = int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }

        return result;
    }

    private static int Arg(int[] args, int index, int defaultValue)
    {
        if (index >= args.Length || args[index] == 0)
        {
            return defaultValue;
        }

        return args[index];
    }

    private readonly record struct Cell(char Rune, Color Foreground, Color Background)
    {
        public static Cell CreateBlank(Color foreground, Color background) => new(' ', foreground, background);
    }

    private readonly record struct TerminalPosition(int Line, int Column) : IComparable<TerminalPosition>
    {
        public int CompareTo(TerminalPosition other)
        {
            int line = Line.CompareTo(other.Line);
            return line != 0 ? line : Column.CompareTo(other.Column);
        }
    }
}
