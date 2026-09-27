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

    /// <summary>最初に画面を無効にした理由の種類。制御の種別と数値parameterだけで、画面の文字を含めない。</summary>
    internal string? RejectedCategory { get; private set; }

    private void Reject(string category)
    {
        Valid = false;
        RejectedCategory ??= category;
    }

    private static string Hex(string prefix, char value) => $"{prefix}:0x{(int)value:X2}";

    // CSI parameterは数字と区切り記号だけを残し、長さも抑える（診断に画面内容を持ち込まない）。
    private static string Sanitize(string parameters)
    {
        string kept = new(parameters.Where(c => char.IsAsciiDigit(c) || c is ';' or '?' or '>' or '<' or '=' or ' ').ToArray());
        return kept.Length > 16 ? kept[..16] + "~" : kept;
    }

    private static string OscKind(string text)
    {
        string number = new(text.TakeWhile(char.IsAsciiDigit).Take(4).ToArray());
        return number.Length == 0 ? "?" : number;
    }

    internal void Feed(ReadOnlySpan<byte> bytes)
    {
        if (!Valid) return;
        Revision++;
        Span<byte> input = stackalloc byte[1];
        Span<char> output = stackalloc char[2];
        foreach (byte value in bytes)
        {
            if (value < 0x80) { if (_pendingBytes != 0) { Reject("utf8-truncated"); return; } }
            else if ((value & 0xC0) == 0x80) _pendingBytes--;
            else _pendingBytes = value < 0xE0 ? 1 : value < 0xF0 ? 2 : 3;
            try
            {
                input[0] = value;
                int length = _decoder.GetChars(input, output, false);
                foreach (char character in output[..length]) Consume(character);
            }
            catch (DecoderFallbackException) { Reject("utf8-invalid"); }
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
            if (char.IsControl(value)) { Reject(Hex("c0", value)); return; }
            if (char.IsHighSurrogate(value)) { _surrogate = value; return; }
            int scalar = value;
            if (char.IsLowSurrogate(value))
            {
                if (_surrogate is null) { Reject("unicode-surrogate"); return; }
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
                default: Reject(char.IsControl(value) ? Hex("esc", value) : "esc:" + value); break;
            }
            _state = 0; return;
        }
        if (_state == 2)
        {
            if (value is >= '@' and <= '~') { Csi(value, _sequence.ToString()); _state = 0; return; }
            if (value is < ' ' or > '?' || _sequence.Length >= 64) { Reject("csi-malformed"); return; }
            _sequence.Append(value); return;
        }
        if (_state == 5) { if (value != 'B') Reject("charset"); _state = 0; return; }
        if (_state == 4)
        {
            if (value != '\\') Reject("osc-terminator");
            FinishOsc(); return;
        }
        if (value == '\a') { FinishOsc(); return; }
        if (value == '\x1b') { _state = 4; return; }
        if (_sequence.Length >= 1024 || char.IsControl(value)) { Reject("osc-malformed"); return; }
        _sequence.Append(value);
    }

    private void FinishOsc()
    {
        string text = _sequence.ToString();
        if (!text.StartsWith("0;", StringComparison.Ordinal) && !text.StartsWith("133;", StringComparison.Ordinal)) Reject("osc:" + OscKind(text));
        _state = 0;
    }

    // P0-3の取得PoC（probes/macos-claude-screen）が描画に影響しないと分類した入力・cursor・同期のDECモード。
    // 代替画面（1049/1047/47）と自動折り返し（7）は描画を変えるため含めない。
    private static readonly HashSet<int> NonRenderingDecModes = [1, 12, 25, 1000, 1002, 1003, 1004, 1006, 2004, 2026, 2031];

    private static bool IsNumericList(string value) => value.All(c => char.IsAsciiDigit(c) || c == ';');

    private static bool IsNonRenderingDecMode(string parameters) =>
        parameters.Length > 1 && parameters[0] == '?' &&
        parameters[1..].Split(';').All(mode => int.TryParse(mode, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && NonRenderingDecModes.Contains(value));

    // 応答しない問い合わせと、描画に影響しないkeyboard・cursor形状の設定（PoCと同じ集合）。引数は数字だけを許す。
    // DA1（CSI c）、DA2（CSI > c）、XTVERSION（CSI > q）、modifyOtherKeys（CSI > m）、
    // kitty keyboard（CSI ? u / > u / < u / = u）、DECSCUSR（CSI Ps SP q）。
    private static bool IsIgnoredQueryOrMode(char final, string parameters)
    {
        if (final == 'q' && parameters.EndsWith(' ') && IsNumericList(parameters[..^1])) return true;
        if (parameters.Length == 0 || char.IsAsciiDigit(parameters[0])) return final == 'c' && IsNumericList(parameters);
        if (!IsNumericList(parameters[1..])) return false;
        return parameters[0] switch
        {
            '?' => final == 'u',
            '>' => final is 'q' or 'm' or 'u' or 'c',
            '<' or '=' => final == 'u',
            _ => false,
        };
    }

    private void Csi(char final, string parameters)
    {
        if ((final is 'h' or 'l') && IsNonRenderingDecMode(parameters)) return;
        if (IsIgnoredQueryOrMode(final, parameters)) return;
        string[] parts = parameters.Split(';');
        int[] values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length != 0 && (!int.TryParse(parts[i], CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)) { Reject("csi:" + Sanitize(parameters) + final); return; }
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
                if (n >= bottom || bottom > height) { Reject("csi:r-range"); break; }
                _top = n - 1; _bottom = bottom - 1; _row = _column = 0; break;
            default: Reject("csi:" + Sanitize(parameters) + final); break;
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
        if (category is UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned) { Reject(category == UnicodeCategory.Format ? "unicode-format" : "unicode-unassigned"); return; }
        int columns = UnicodeCellWidth.Get(value.Value);
        if (columns < 0) { Reject("unicode-width"); return; }
        if (columns == 0)
        {
            int previous = _wrap ? _column : _column - 1;
            if (previous >= 0 && _cells[_row, previous] == "") previous--;
            if (previous < 0 || _cells[_row, previous] is null) { Reject("unicode-combining"); return; }
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
