using Aprillz.MewUI.Rendering;

using System.Text;

using Aprillz.MewUI.Input;

namespace Aprillz.MewUI.Controls.HighRisk;

public enum VncScaleMode
{
    None,
    Fit,
    Stretch
}

public sealed class VncPointerEventArgs : EventArgs
{
    public VncPointerEventArgs(byte buttonMask, ushort x, ushort y)
    {
        ButtonMask = buttonMask;
        X = x;
        Y = y;
    }

    public byte ButtonMask { get; }

    public ushort X { get; }

    public ushort Y { get; }
}

public sealed class VncKeyEventArgs : EventArgs
{
    public VncKeyEventArgs(bool isDown, uint keySym)
    {
        IsDown = isDown;
        KeySym = keySym;
    }

    public bool IsDown { get; }

    public uint KeySym { get; }
}

public enum VncConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

/// <summary>
/// Pixel-based VNC framebuffer surface with scale, pointer, and key mapping.
/// </summary>
public sealed class VncRemoteDesktopControl : Control, ITextInputClient
{
    private readonly Dictionary<IGraphicsFactory, IImage> _imageCache = new(ReferenceEqualityComparer<IGraphicsFactory>.Instance);
    private WriteableBitmap? _framebuffer;
    private byte _buttonMask;
    private ModifierKeys _sentModifiers = ModifierKeys.None;
    private int _suppressedTextInputCount;
    private Rect _lastDestinationRect = Rect.Empty;
    private VncConnectionState _connectionState = VncConnectionState.Disconnected;
    private string _statusText = "VNC framebuffer";

    public VncRemoteDesktopControl()
    {
        Background = Color.FromRgb(18, 20, 24);
        BorderBrush = Color.FromArgb(255, 54, 60, 68);
        BorderThickness = 1;
        Cursor = CursorType.Arrow;
        FocusableOnPointerInput = true;
    }

    public override bool Focusable => true;

    public bool FocusableOnPointerInput { get; set; }

    public VncScaleMode ScaleMode
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }
    } = VncScaleMode.Fit;

    public int FramebufferWidth => _framebuffer?.PixelWidth ?? 0;

    public int FramebufferHeight => _framebuffer?.PixelHeight ?? 0;

    public VncConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            if (_connectionState == value)
            {
                return;
            }

            _connectionState = value;
            InvalidateVisual();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            value ??= string.Empty;
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            InvalidateVisual();
        }
    }

    public event EventHandler<VncPointerEventArgs>? PointerStateChanged;

    public event EventHandler<VncKeyEventArgs>? KeyStateChanged;

    public void SetFramebufferSize(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (_framebuffer != null && _framebuffer.PixelWidth == width && _framebuffer.PixelHeight == height)
        {
            return;
        }

        _framebuffer?.Dispose();
        _framebuffer = new WriteableBitmap(width, height, clear: true, hasAlpha: false);
        _framebuffer.Changed += OnFramebufferChanged;
        ConnectionState = VncConnectionState.Connected;
        StatusText = $"{width}x{height}";
        ClearImageCache();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void WriteFrame(int width, int height, ReadOnlySpan<byte> bgra, int strideBytes)
    {
        SetFramebufferSize(width, height);
        _framebuffer!.WritePixels(0, 0, width, height, bgra, strideBytes);
    }

    public void WriteRect(int x, int y, int width, int height, ReadOnlySpan<byte> bgra, int strideBytes)
    {
        if (_framebuffer == null)
        {
            throw new InvalidOperationException("Framebuffer size must be set before writing a rectangle.");
        }

        _framebuffer.WritePixels(x, y, width, height, bgra, strideBytes);
    }

    public bool TryMapToRemote(Point pointInControl, out ushort x, out ushort y)
    {
        x = 0;
        y = 0;
        if (_framebuffer == null)
        {
            return false;
        }

        var destination = CalculateDestinationRect(GetContentRect(), new Size(_framebuffer.PixelWidth, _framebuffer.PixelHeight), ScaleMode);
        _lastDestinationRect = destination;
        if (destination.IsEmpty || !destination.Contains(pointInControl))
        {
            return false;
        }

        double normalizedX = (pointInControl.X - destination.X) / Math.Max(1, destination.Width);
        double normalizedY = (pointInControl.Y - destination.Y) / Math.Max(1, destination.Height);
        int remoteX = Math.Clamp((int)Math.Floor(normalizedX * _framebuffer.PixelWidth), 0, _framebuffer.PixelWidth - 1);
        int remoteY = Math.Clamp((int)Math.Floor(normalizedY * _framebuffer.PixelHeight), 0, _framebuffer.PixelHeight - 1);
        x = (ushort)remoteX;
        y = (ushort)remoteY;
        return true;
    }

    protected override Size MeasureContent(Size availableSize)
    {
        if (_framebuffer == null)
        {
            return new Size(320, 200);
        }

        var content = new Size(_framebuffer.PixelWidth, _framebuffer.PixelHeight).Inflate(Padding);
        var border = GetBorderVisualInset();
        if (border > 0)
        {
            content = content.Inflate(new Thickness(border));
        }

        return content;
    }

    protected override void OnRender(IGraphicsContext context)
    {
        DrawBackgroundAndBorder(context, GetSnappedBorderBounds(Bounds), Background, BorderBrush, BorderThickness, CornerRadius);
        if (_framebuffer == null)
        {
            DrawEmptyState(context);
            return;
        }

        var image = GetImage(context);
        var content = GetContentRect();
        var destination = CalculateDestinationRect(content, new Size(_framebuffer.PixelWidth, _framebuffer.PixelHeight), ScaleMode);
        _lastDestinationRect = destination;
        if (destination.IsEmpty)
        {
            return;
        }

        context.Save();
        context.SetClip(content);
        var quality = context.ImageScaleQuality;
        context.ImageScaleQuality = ScaleMode == VncScaleMode.None ? ImageScaleQuality.Fast : ImageScaleQuality.HighQuality;
        try
        {
            context.DrawImage(image, destination);
        }
        finally
        {
            context.ImageScaleQuality = quality;
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

        if (FocusableOnPointerInput)
        {
            Focus();
        }

        _buttonMask = ApplyButtonMask(_buttonMask, e.Button, pressed: true);
        SendPointer(e.Position, _buttonMask);
        if (FindVisualRoot() is Window window)
        {
            window.CaptureMouse(this);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SendPointer(e.Position, _buttonMask);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _buttonMask = ApplyButtonMask(_buttonMask, e.Button, pressed: false);
        SendPointer(e.Position, _buttonMask);
        if (_buttonMask == 0 && FindVisualRoot() is Window window)
        {
            window.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!TryMapToRemote(e.Position, out var x, out var y))
        {
            return;
        }

        byte wheelMask = e.Delta > 0 ? (byte)8 : (byte)16;
        PointerStateChanged?.Invoke(this, new VncPointerEventArgs((byte)(_buttonMask | wheelMask), x, y));
        PointerStateChanged?.Invoke(this, new VncPointerEventArgs(_buttonMask, x, y));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        SendModifierChanges(e.Modifiers);

        if (TryMapKeySym(e.Key, out uint keySym))
        {
            if (IsTextProducingKey(e.Key))
            {
                _suppressedTextInputCount++;
            }

            KeyStateChanged?.Invoke(this, new VncKeyEventArgs(true, keySym));
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (TryMapKeySym(e.Key, out uint keySym))
        {
            KeyStateChanged?.Invoke(this, new VncKeyEventArgs(false, keySym));
            e.Handled = true;
        }

        SendModifierChanges(e.Modifiers);
    }

    protected override void OnLostFocus()
    {
        base.OnLostFocus();
        ReleaseSentModifiers();
        _buttonMask = 0;
    }

    void ITextInputClient.HandleTextInput(TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (_suppressedTextInputCount > 0)
        {
            _suppressedTextInputCount--;
            e.Handled = true;
            return;
        }

        foreach (Rune rune in e.Text.EnumerateRunes())
        {
            uint keySym = RuneToKeySym(rune);
            KeyStateChanged?.Invoke(this, new VncKeyEventArgs(true, keySym));
            KeyStateChanged?.Invoke(this, new VncKeyEventArgs(false, keySym));
        }

        e.Handled = true;
    }

    protected override void OnDispose()
    {
        base.OnDispose();
        if (_framebuffer != null)
        {
            _framebuffer.Changed -= OnFramebufferChanged;
            _framebuffer.Dispose();
            _framebuffer = null;
        }

        ClearImageCache();
    }

    private Rect GetContentRect()
    {
        var border = GetBorderVisualInset();
        var rect = border > 0 ? Bounds.Deflate(new Thickness(border)) : Bounds;
        return rect.Deflate(Padding);
    }

    private IImage GetImage(IGraphicsContext context)
    {
        if (_framebuffer == null)
        {
            throw new InvalidOperationException("Framebuffer has not been created.");
        }

        var factory = Application.IsRunning ? Application.Current.GraphicsFactory : GetGraphicsFactory();
        if (_imageCache.TryGetValue(factory, out var cached))
        {
            return cached;
        }

        var image = ((IImageSource)_framebuffer).CreateImage(factory);
        _imageCache[factory] = image;
        return image;
    }

    private void SendPointer(Point position, byte mask)
    {
        if (TryMapToRemote(position, out var x, out var y))
        {
            PointerStateChanged?.Invoke(this, new VncPointerEventArgs(mask, x, y));
        }
    }

    private void OnFramebufferChanged()
    {
        ClearImageCache();
        InvalidateVisual();
    }

    private void DrawEmptyState(IGraphicsContext context)
    {
        using var measure = BeginTextMeasurement();
        var content = GetContentRect();
        var text = string.IsNullOrWhiteSpace(_statusText)
            ? _connectionState.ToString()
            : _statusText;
        context.DrawText(text, content, measure.Font, Color.FromArgb(180, 210, 216, 225), TextAlignment.Center, TextAlignment.Center, TextWrapping.NoWrap);
    }

    private void ClearImageCache()
    {
        foreach (var image in _imageCache.Values)
        {
            image.Dispose();
        }

        _imageCache.Clear();
    }

    private static Rect CalculateDestinationRect(Rect bounds, Size frameSize, VncScaleMode scaleMode)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || frameSize.Width <= 0 || frameSize.Height <= 0)
        {
            return Rect.Empty;
        }

        return scaleMode switch
        {
            VncScaleMode.Stretch => bounds,
            VncScaleMode.None => new Rect(
                bounds.X + Math.Max(0, (bounds.Width - frameSize.Width) * 0.5),
                bounds.Y + Math.Max(0, (bounds.Height - frameSize.Height) * 0.5),
                frameSize.Width,
                frameSize.Height),
            _ => CalculateFitRect(bounds, frameSize)
        };
    }

    private static Rect CalculateFitRect(Rect bounds, Size frameSize)
    {
        double scale = Math.Min(bounds.Width / frameSize.Width, bounds.Height / frameSize.Height);
        double width = frameSize.Width * scale;
        double height = frameSize.Height * scale;
        double left = bounds.X + (bounds.Width - width) * 0.5;
        double top = bounds.Y + (bounds.Height - height) * 0.5;
        return new Rect(left, top, width, height);
    }

    private static byte ApplyButtonMask(byte currentMask, MouseButton button, bool pressed)
    {
        byte flag = button switch
        {
            MouseButton.Left => 1,
            MouseButton.Middle => 2,
            MouseButton.Right => 4,
            _ => 0
        };

        if (flag == 0)
        {
            return currentMask;
        }

        return pressed ? (byte)(currentMask | flag) : (byte)(currentMask & ~flag);
    }

    private static bool TryMapKeySym(Key key, out uint keySym)
    {
        switch (key)
        {
            case >= Key.A and <= Key.Z:
                keySym = (uint)('a' + (key - Key.A));
                return true;
            case >= Key.D0 and <= Key.D9:
                keySym = (uint)('0' + (key - Key.D0));
                return true;
            case >= Key.NumPad0 and <= Key.NumPad9:
                keySym = (uint)('0' + (key - Key.NumPad0));
                return true;
            case >= Key.F1 and <= Key.F12:
                keySym = 0xFFBEu + (uint)(key - Key.F1);
                return true;
            case Key.Enter:
                keySym = 0xFF0Du;
                return true;
            case Key.Backspace:
                keySym = 0xFF08u;
                return true;
            case Key.Tab:
                keySym = 0xFF09u;
                return true;
            case Key.Escape:
                keySym = 0xFF1Bu;
                return true;
            case Key.Insert:
                keySym = 0xFF63u;
                return true;
            case Key.Delete:
                keySym = 0xFFFFu;
                return true;
            case Key.Home:
                keySym = 0xFF50u;
                return true;
            case Key.End:
                keySym = 0xFF57u;
                return true;
            case Key.PageUp:
                keySym = 0xFF55u;
                return true;
            case Key.PageDown:
                keySym = 0xFF56u;
                return true;
            case Key.Left:
                keySym = 0xFF51u;
                return true;
            case Key.Up:
                keySym = 0xFF52u;
                return true;
            case Key.Right:
                keySym = 0xFF53u;
                return true;
            case Key.Down:
                keySym = 0xFF54u;
                return true;
            case Key.Space:
                keySym = 0x20u;
                return true;
            case Key.Add:
                keySym = (uint)'+';
                return true;
            case Key.Subtract:
                keySym = (uint)'-';
                return true;
            case Key.Multiply:
                keySym = (uint)'*';
                return true;
            case Key.Divide:
                keySym = (uint)'/';
                return true;
            case Key.Decimal:
                keySym = (uint)'.';
                return true;
            default:
                keySym = 0;
                return false;
        }
    }

    private void SendModifierChanges(ModifierKeys modifiers)
    {
        ModifierKeys normalized = modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Meta);
        SendModifierChange(ModifierKeys.Control, 0xFFE3u, normalized);
        SendModifierChange(ModifierKeys.Shift, 0xFFE1u, normalized);
        SendModifierChange(ModifierKeys.Alt, 0xFFE9u, normalized);
        SendModifierChange(ModifierKeys.Meta, 0xFFEBu, normalized);
        _sentModifiers = normalized;
    }

    private void SendModifierChange(ModifierKeys modifier, uint keySym, ModifierKeys activeModifiers)
    {
        bool wasDown = (_sentModifiers & modifier) != 0;
        bool isDown = (activeModifiers & modifier) != 0;
        if (wasDown == isDown)
        {
            return;
        }

        KeyStateChanged?.Invoke(this, new VncKeyEventArgs(isDown, keySym));
    }

    private void ReleaseSentModifiers()
    {
        SendModifierChanges(ModifierKeys.None);
    }

    private static uint RuneToKeySym(Rune rune)
    {
        int value = rune.Value;
        if (value <= 0xFF)
        {
            return (uint)value;
        }

        return 0x01000000u | (uint)value;
    }

    private static bool IsTextProducingKey(Key key)
        => key is >= Key.A and <= Key.Z
            || key is >= Key.D0 and <= Key.D9
            || key is >= Key.NumPad0 and <= Key.NumPad9
            || key is Key.Space or Key.Add or Key.Subtract or Key.Multiply or Key.Divide or Key.Decimal;
}
