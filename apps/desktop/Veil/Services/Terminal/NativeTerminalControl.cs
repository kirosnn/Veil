using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Veil.Services.Terminal;

internal sealed class NativeTerminalControl : Grid
{
    private readonly ScrollViewer _scrollViewer;
    private readonly TextBlock _outputBlock;
    private readonly Border _statusHost;
    private readonly TextBlock _statusText;
    private readonly TextBox _inputSink;
    private readonly TerminalStreamDecoder _decoder = new();
    private readonly Queue<string> _lines = new();
    private readonly StringBuilder _currentLine = new();
    private readonly StringBuilder _pendingText = new();
    private readonly object _pendingGate = new();
    private readonly int _scrollback;
    private bool _pendingFlush;
    private bool _pendingClear;
    private string? _pendingTitle;
    private bool _suppressTextChanged;
    private bool _pendingCarriageReturn;
    private int _lastCols;
    private int _lastRows;

    public event Action<byte[]>? InputSubmitted;
    public event Action<int, int>? ResizeRequested;
    public event Action<string>? TitleChanged;

    internal NativeTerminalControl(string fontFamily, double fontSize, int scrollback)
    {
        _scrollback = Math.Max(100, scrollback);
        Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 13, 17, 23));

        string resolvedFontFamily = ResolveFontFamily(fontFamily);

        _outputBlock = new TextBlock
        {
            FontFamily = new FontFamily(resolvedFontFamily),
            FontSize = Math.Max(8, fontSize),
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, 230, 237, 243)),
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = false,
            Margin = new Thickness(14, 12, 14, 12)
        };

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            ZoomMode = ZoomMode.Disabled,
            Content = _outputBlock
        };

        _statusText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(220, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap
        };

        _statusHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14, 12, 14, 0),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(40, 255, 255, 255)),
            Child = _statusText,
            Visibility = Visibility.Collapsed
        };

        _inputSink = new TextBox
        {
            Width = 1,
            Height = 1,
            Opacity = 0.01,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            IsSpellCheckEnabled = false
        };

        _inputSink.TextChanged += OnInputTextChanged;
        _inputSink.PreviewKeyDown += OnInputPreviewKeyDown;

        Children.Add(_scrollViewer);
        Children.Add(_statusHost);
        Children.Add(_inputSink);

        PointerPressed += OnPointerPressed;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    internal int CurrentCols => _lastCols > 0 ? _lastCols : 120;
    internal int CurrentRows => _lastRows > 0 ? _lastRows : 30;

    internal void FocusTerminal()
    {
        _inputSink.Focus(FocusState.Programmatic);
    }

    internal void AppendOutput(byte[] data)
    {
        TerminalDecodedChunk decoded = _decoder.Decode(data);
        if (string.IsNullOrEmpty(decoded.Text) && decoded.Title is null && !decoded.ClearRequested)
        {
            return;
        }

        lock (_pendingGate)
        {
            if (decoded.ClearRequested)
            {
                _pendingClear = true;
            }

            if (!string.IsNullOrEmpty(decoded.Text))
            {
                _pendingText.Append(decoded.Text);
            }

            if (!string.IsNullOrWhiteSpace(decoded.Title))
            {
                _pendingTitle = decoded.Title;
            }

            if (_pendingFlush)
            {
                return;
            }

            _pendingFlush = true;
        }

        DispatcherQueue.TryEnqueue(FlushPendingOutput);
    }

    internal void ShowStatus(string text, bool isError)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _statusText.Text = text;
            _statusHost.Background = new SolidColorBrush(
                isError
                    ? global::Windows.UI.Color.FromArgb(70, 170, 40, 40)
                    : global::Windows.UI.Color.FromArgb(40, 255, 255, 255));
            _statusHost.Visibility = Visibility.Visible;
        });
    }

    internal void HideStatus()
    {
        DispatcherQueue.TryEnqueue(() => _statusHost.Visibility = Visibility.Collapsed);
    }

    internal void Clear()
    {
        lock (_pendingGate)
        {
            _pendingClear = true;
            if (_pendingFlush)
            {
                return;
            }

            _pendingFlush = true;
        }

        DispatcherQueue.TryEnqueue(FlushPendingOutput);
    }

    private void FlushPendingOutput()
    {
        string text;
        string? title;
        bool clearRequested;

        lock (_pendingGate)
        {
            text = _pendingText.ToString();
            _pendingText.Clear();
            title = _pendingTitle;
            _pendingTitle = null;
            clearRequested = _pendingClear;
            _pendingClear = false;
            _pendingFlush = false;
        }

        if (clearRequested)
        {
            _lines.Clear();
            _currentLine.Clear();
            _pendingCarriageReturn = false;
        }

        if (!string.IsNullOrEmpty(text))
        {
            AppendText(text);
            _outputBlock.Text = BuildVisibleText();
            _scrollViewer.ChangeView(null, _scrollViewer.ScrollableHeight, null, true);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            TitleChanged?.Invoke(title);
        }
    }

    private void AppendText(string text)
    {
        foreach (char ch in text)
        {
            if (_pendingCarriageReturn)
            {
                if (ch == '\n')
                {
                    PushCurrentLine();
                    _pendingCarriageReturn = false;
                    continue;
                }

                _currentLine.Clear();
                _pendingCarriageReturn = false;
            }

            switch (ch)
            {
                case '\r':
                    _pendingCarriageReturn = true;
                    break;
                case '\n':
                    PushCurrentLine();
                    break;
                case '\b':
                    if (_currentLine.Length > 0)
                    {
                        _currentLine.Length--;
                    }
                    break;
                default:
                    _currentLine.Append(ch);
                    break;
            }
        }
    }

    private void PushCurrentLine()
    {
        _lines.Enqueue(_currentLine.ToString());
        _currentLine.Clear();

        while (_lines.Count > _scrollback)
        {
            _lines.Dequeue();
        }
    }

    private string BuildVisibleText()
    {
        var builder = new StringBuilder();
        foreach (string line in _lines)
        {
            builder.AppendLine(line);
        }

        builder.Append(_currentLine);
        return builder.ToString();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PublishViewportSize();
        FocusTerminal();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PublishViewportSize();
    }

    private void PublishViewportSize()
    {
        double width = Math.Max(0, ActualWidth - 28);
        double height = Math.Max(0, ActualHeight - 24);
        double cellWidth = Math.Max(6, _outputBlock.FontSize * 0.62);
        double cellHeight = Math.Max(12, _outputBlock.FontSize * 1.5);

        int cols = Math.Max(20, (int)Math.Floor(width / cellWidth));
        int rows = Math.Max(5, (int)Math.Floor(height / cellHeight));

        if (cols == _lastCols && rows == _lastRows)
        {
            return;
        }

        _lastCols = cols;
        _lastRows = rows;
        ResizeRequested?.Invoke(cols, rows);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        FocusTerminal();
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged)
        {
            return;
        }

        string text = _inputSink.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        SubmitText(text);
        ResetInputSink();
    }

    private void OnInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        byte[]? payload = TranslateKey(e.Key);
        if (payload is null)
        {
            return;
        }

        e.Handled = true;
        SubmitBytes(payload);
        ResetInputSink();
    }

    private byte[]? TranslateKey(VirtualKey key)
    {
        bool ctrl = IsKeyDown(VirtualKey.Control);
        bool alt = IsKeyDown(VirtualKey.Menu);

        if (ctrl)
        {
            return key switch
            {
                VirtualKey.C => EncodeControl(0x03),
                VirtualKey.D => EncodeControl(0x04),
                VirtualKey.L => EncodeControl(0x0C),
                VirtualKey.Z => EncodeControl(0x1A),
                _ => null
            };
        }

        byte[]? sequence = key switch
        {
            VirtualKey.Enter => EncodeControl(0x0D),
            VirtualKey.Tab => EncodeControl(0x09),
            VirtualKey.Back => EncodeControl(0x08),
            VirtualKey.Escape => EncodeControl(0x1B),
            VirtualKey.Up => Encoding.UTF8.GetBytes("\u001b[A"),
            VirtualKey.Down => Encoding.UTF8.GetBytes("\u001b[B"),
            VirtualKey.Right => Encoding.UTF8.GetBytes("\u001b[C"),
            VirtualKey.Left => Encoding.UTF8.GetBytes("\u001b[D"),
            VirtualKey.Home => Encoding.UTF8.GetBytes("\u001b[H"),
            VirtualKey.End => Encoding.UTF8.GetBytes("\u001b[F"),
            VirtualKey.Delete => Encoding.UTF8.GetBytes("\u001b[3~"),
            VirtualKey.Insert => Encoding.UTF8.GetBytes("\u001b[2~"),
            VirtualKey.PageUp => Encoding.UTF8.GetBytes("\u001b[5~"),
            VirtualKey.PageDown => Encoding.UTF8.GetBytes("\u001b[6~"),
            _ => null
        };

        if (sequence is null || !alt)
        {
            return sequence;
        }

        byte[] prefixed = new byte[sequence.Length + 1];
        prefixed[0] = 0x1B;
        Buffer.BlockCopy(sequence, 0, prefixed, 1, sequence.Length);
        return prefixed;
    }

    private static byte[] EncodeControl(byte value) => [value];

    private void SubmitText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        SubmitBytes(Encoding.UTF8.GetBytes(text));
    }

    private void SubmitBytes(byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        InputSubmitted?.Invoke(data);
    }

    private void ResetInputSink()
    {
        _suppressTextChanged = true;
        _inputSink.Text = string.Empty;
        _inputSink.SelectionStart = 0;
        _suppressTextChanged = false;
    }

    private static string ResolveFontFamily(string fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
        {
            return "Cascadia Mono";
        }

        string primary = fontFamily.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? fontFamily;
        return string.IsNullOrWhiteSpace(primary) ? "Cascadia Mono" : primary;
    }

    private static bool IsKeyDown(VirtualKey key) => (GetKeyState((int)key) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
