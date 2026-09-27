using System.Globalization;
using System.Text;
using AiUsageMonitor.Claude.Cli;

namespace AiUsageMonitor.Claude.Mac;

/// <summary>Observed screen-reader VT dialect only. Unknown controls poison the session.</summary>
internal sealed class VtScreen(int width = 400, int height = 120)
{
    private readonly string?[,] _cells = new string?[height, width];
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly StringBuilder _sequence = new();
    private int _row, _column, _savedRow, _savedColumn, _state, _pendingBytes;
    private int _top, _bottom = height - 1;
    private bool _wrap;
    private char? _surrogate;
    internal bool Valid { get; private set; } = width is > 0 and <= 400 && height is > 0 and <= 120;
    internal bool Complete => Valid && _state == 0 && _pendingBytes == 0 && _surrogate is null;
    internal long Revision { get; private set; }

    internal void Feed(ReadOnlySpan<byte> bytes)
    {
        if (!Valid) return;
        Revision++;
        Span<byte> input = stackalloc byte[1];
        Span<char> output = stackalloc char[2];
        foreach (byte value in bytes)
        {
            if (value < 0x80) { if (_pendingBytes != 0) { Valid = false; return; } }
            else if ((value & 0xC0) == 0x80) _pendingBytes--;
            else _pendingBytes = value < 0xE0 ? 1 : value < 0xF0 ? 2 : 3;
            try
            {
                input[0] = value;
                int length = _decoder.GetChars(input, output, false);
                foreach (char character in output[..length]) Consume(character);
            }
            catch (DecoderFallbackException) { Valid = false; }
            if (!Valid) return;
        }
    }

    private void Consume(char value)
    {
        if (_state == 0)
        {
            if (value == '\x1b') { _state = 1; return; }
            if (value == '\r') { _column = 0; _wrap = false; return; }
            if (value == '\n') { LineFeed(); return; }
            if (value is '\a' or '\x0f') return;
            if (char.IsControl(value)) { Valid = false; return; }
            if (char.IsHighSurrogate(value)) { _surrogate = value; return; }
            int scalar = value;
            if (char.IsLowSurrogate(value))
            {
                if (_surrogate is null) { Valid = false; return; }
                scalar = char.ConvertToUtf32(_surrogate.Value, value); _surrogate = null;
            }
            Put(new Rune(scalar)); return;
        }
        if (_state == 1)
        {
            switch (value)
            {
                case '[': _sequence.Clear(); _state = 2; return;
                case ']': _sequence.Clear(); _state = 3; return;
                case '(': _state = 5; return;
                case '7': _savedRow = _row; _savedColumn = _column; break;
                case '8': _row = _savedRow; _column = _savedColumn; _wrap = false; break;
                default: Valid = false; break;
            }
            _state = 0; return;
        }
        if (_state == 2)
        {
            if (value is >= '@' and <= '~') { Csi(value, _sequence.ToString()); _state = 0; return; }
            if (value is < ' ' or > '?' || _sequence.Length >= 64) { Valid = false; return; }
            _sequence.Append(value); return;
        }
        if (_state == 5) { if (value != 'B') Valid = false; _state = 0; return; }
        if (_state == 4)
        {
            if (value != '\\') Valid = false;
            FinishOsc(); return;
        }
        if (value == '\a') { FinishOsc(); return; }
        if (value == '\x1b') { _state = 4; return; }
        if (_sequence.Length >= 1024 || char.IsControl(value)) { Valid = false; return; }
        _sequence.Append(value);
    }

    private void FinishOsc()
    {
        string text = _sequence.ToString();
        if (!text.StartsWith("0;", StringComparison.Ordinal) && !text.StartsWith("133;", StringComparison.Ordinal)) Valid = false;
        _state = 0;
    }

    private void Csi(char final, string parameters)
    {
        if ((final is 'h' or 'l') && parameters is "?25" or "?1004" or "?2004" or "?2031") return;
        if ((final == 'c' && parameters is "" or "0") ||
            (final == 'q' && parameters == ">") || (final == 'u' && parameters == "?") ||
            (final == 'm' && parameters.StartsWith('>') && parameters[1..].All(c => char.IsAsciiDigit(c) || c == ';'))) return;
        string[] parts = parameters.Split(';');
        int[] values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length != 0 && (!int.TryParse(parts[i], CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)) { Valid = false; return; }
        int n = values[0] == 0 ? 1 : values[0];
        _wrap = false;
        switch (final)
        {
            case 'A' when values.Length == 1: _row = Math.Max(0, _row - Math.Min(n, height)); break;
            case 'B' when values.Length == 1: _row = Math.Min(height - 1, _row + Math.Min(n, height)); break;
            case 'G' when values.Length == 1: _column = Math.Min(width, n) - 1; break;
            case 'K' when values.Length == 1 && values[0] <= 2:
                int from = values[0] == 0 ? _column : 0;
                int to = values[0] == 1 ? _column + 1 : width;
                for (int c = from; c < to; c++) ClearCell(_row, c);
                break;
            case 'r' when values.Length <= 2:
                int bottom = values.Length == 1 || values[1] == 0 ? height : values[1];
                if (n >= bottom || bottom > height) { Valid = false; break; }
                _top = n - 1; _bottom = bottom - 1; _row = _column = 0; break;
            default: Valid = false; break;
        }
    }

    private void ClearCell(int row, int column)
    {
        if (_cells[row, column] == "" && column > 0) _cells[row, column - 1] = null;
        if (column + 1 < width && _cells[row, column + 1] == "") _cells[row, column + 1] = null;
        _cells[row, column] = null;
    }

    private void Put(Rune value)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(value);
        if (category is UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned) { Valid = false; return; }
        int columns = UnicodeCellWidth.Get(value.Value);
        if (columns < 0) { Valid = false; return; }
        if (columns == 0)
        {
            int previous = _wrap ? _column : _column - 1;
            if (previous >= 0 && _cells[_row, previous] == "") previous--;
            if (previous < 0 || _cells[_row, previous] is null) { Valid = false; return; }
            _cells[_row, previous] += value.ToString(); return;
        }
        if (_wrap || _column + columns > width) { _column = 0; LineFeed(); }
        ClearCell(_row, _column); _cells[_row, _column] = value.ToString();
        if (columns == 2) { ClearCell(_row, _column + 1); _cells[_row, _column + 1] = ""; }
        _column += columns;
        if (_column == width) { _column--; _wrap = true; }
    }

    private void LineFeed()
    {
        _wrap = false;
        if (_row == _bottom)
        {
            for (int r = _top; r < _bottom; r++)
                for (int c = 0; c < width; c++) _cells[r, c] = _cells[r + 1, c];
            for (int c = 0; c < width; c++) _cells[_bottom, c] = null;
        }
        else _row = Math.Min(height - 1, _row + 1);
    }

    internal ScreenSnapshot Snapshot()
    {
        if (!Complete) throw new InvalidOperationException("VT_SCREEN_UNCERTAIN");
        string[] lines = new string[height];
        for (int r = 0; r < height; r++)
        {
            var line = new StringBuilder();
            for (int c = 0; c < width; c++) line.Append(_cells[r, c] ?? " ");
            lines[r] = line.ToString().TrimEnd();
        }
        return new(lines, width, height);
    }
}
