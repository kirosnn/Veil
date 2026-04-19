using System.Text;

namespace Veil.Services.Terminal;

internal readonly record struct TerminalDecodedChunk(string Text, string? Title, bool ClearRequested);

internal sealed class TerminalStreamDecoder
{
    private enum ParseState
    {
        Text,
        Escape,
        Csi,
        Osc,
        OscEscape
    }

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _escape = new();
    private readonly StringBuilder _osc = new();
    private ParseState _state;

    internal TerminalDecodedChunk Decode(byte[] data)
    {
        if (data.Length == 0)
        {
            return default;
        }

        int charCount = _decoder.GetCharCount(data, 0, data.Length);
        if (charCount == 0)
        {
            return default;
        }

        char[] chars = new char[charCount];
        _decoder.GetChars(data, 0, data.Length, chars, 0);

        _text.Clear();
        string? title = null;
        bool clearRequested = false;

        foreach (char ch in chars)
        {
            switch (_state)
            {
                case ParseState.Text:
                    if (ch == '\u001b')
                    {
                        _state = ParseState.Escape;
                        _escape.Clear();
                    }
                    else if (ch == '\a' || ch == '\0')
                    {
                    }
                    else
                    {
                        _text.Append(ch);
                    }
                    break;

                case ParseState.Escape:
                    if (ch == '[')
                    {
                        _state = ParseState.Csi;
                        _escape.Clear();
                    }
                    else if (ch == ']')
                    {
                        _state = ParseState.Osc;
                        _osc.Clear();
                    }
                    else
                    {
                        _state = ParseState.Text;
                    }
                    break;

                case ParseState.Csi:
                    _escape.Append(ch);
                    if (ch is >= '@' and <= '~')
                    {
                        if (ch == 'J')
                        {
                            string body = _escape.ToString();
                            if (body.Contains("2", StringComparison.Ordinal) || body.Contains("3", StringComparison.Ordinal))
                            {
                                clearRequested = true;
                            }
                        }

                        _escape.Clear();
                        _state = ParseState.Text;
                    }
                    break;

                case ParseState.Osc:
                    if (ch == '\a')
                    {
                        title = ParseTitle(_osc.ToString()) ?? title;
                        _osc.Clear();
                        _state = ParseState.Text;
                    }
                    else if (ch == '\u001b')
                    {
                        _state = ParseState.OscEscape;
                    }
                    else
                    {
                        _osc.Append(ch);
                    }
                    break;

                case ParseState.OscEscape:
                    if (ch == '\\')
                    {
                        title = ParseTitle(_osc.ToString()) ?? title;
                        _osc.Clear();
                        _state = ParseState.Text;
                    }
                    else
                    {
                        _osc.Append('\u001b');
                        _osc.Append(ch);
                        _state = ParseState.Osc;
                    }
                    break;
            }
        }

        return new TerminalDecodedChunk(_text.ToString(), title, clearRequested);
    }

    private static string? ParseTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        int separator = raw.IndexOf(';');
        if (separator <= 0 || separator >= raw.Length - 1)
        {
            return null;
        }

        string prefix = raw[..separator];
        if (!string.Equals(prefix, "0", StringComparison.Ordinal) &&
            !string.Equals(prefix, "2", StringComparison.Ordinal))
        {
            return null;
        }

        string title = raw[(separator + 1)..].Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }
}
