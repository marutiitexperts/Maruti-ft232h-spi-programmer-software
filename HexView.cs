using System.Text;

namespace FTFlash;

/// <summary>
/// Read-only hex viewer: offset | 16 hex bytes | ASCII. Only visible rows are painted,
/// so even a 64 MB buffer scrolls instantly.
/// </summary>
public sealed class HexView : Control
{
    private const int BytesPerRow = 16;
    private const int HexCol = 10;                       // "00000000" + 2 spaces
    private const int AsciiCol = HexCol + 49 + 1;        // 16 * "XX " + 1 mid gap + 1 space
    private const int PadX = 6;

    private static readonly Color Back = Color.FromArgb(30, 30, 32);
    private static readonly Color Fore = Color.FromArgb(212, 212, 212);
    private static readonly Color AddrFore = Color.FromArgb(86, 156, 214);
    private static readonly Color AsciiFore = Color.FromArgb(206, 145, 120);
    private static readonly Color HeaderBack = Color.FromArgb(45, 45, 48);
    private static readonly Color HeaderFore = Color.FromArgb(150, 150, 150);
    private static readonly Color CursorBack = Color.FromArgb(38, 79, 120);
    private static readonly Color FoundBack = Color.FromArgb(125, 95, 0);

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                                          TextFormatFlags.Left | TextFormatFlags.Top;

    private readonly VScrollBar _sb = new() { Dock = DockStyle.Right };
    private byte[]? _data;
    private int _topRow;
    private int _cursor = -1;
    private int _hlStart = -1, _hlLen;
    private byte[]? _lastPattern;
    private int _charW = 9, _rowH = 16, _headerH = 20;
    private string _headerText = "";

    /// <summary>Raised when the selected byte changes (argument = byte offset).</summary>
    public event Action<int>? CursorChanged;

    public HexView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Back;
        Font = new Font("Consolas", 10f);

        Controls.Add(_sb);
        _sb.ValueChanged += (_, _) =>
        {
            if (_sb.Value != _topRow) { _topRow = _sb.Value; Invalidate(); }
        };

        MeasureFont();
        UpdateScroll();
    }

    // ---- public API ------------------------------------------------------------------
    public void SetData(byte[]? data)
    {
        _data = data;
        _topRow = 0;
        _cursor = -1;
        _hlStart = -1;
        _hlLen = 0;
        _lastPattern = null;
        UpdateScroll();
        Invalidate();
    }

    /// <summary>Selects the byte at offset and scrolls it into view (centred when it was off-screen).</summary>
    public void GoTo(int offset)
    {
        if (_data == null || _data.Length == 0) return;
        offset = Math.Clamp(offset, 0, _data.Length - 1);
        SetCursor(offset);
        int row = offset / BytesPerRow;
        if (row < _topRow || row >= _topRow + VisibleRows)
            SetTop(row - VisibleRows / 2);
    }

    public void Highlight(int start, int length)
    {
        _hlStart = start;
        _hlLen = length;
        GoTo(start);
    }

    /// <summary>Finds the next occurrence (wraps around). Returns the offset or -1.</summary>
    public int Find(byte[] pattern)
    {
        if (_data == null || pattern.Length == 0) return -1;

        bool same = _lastPattern != null && _lastPattern.AsSpan().SequenceEqual(pattern);
        int start = same && _hlLen > 0 ? _hlStart + 1 : Math.Max(0, _cursor);
        start = Math.Min(start, _data.Length);
        _lastPattern = pattern;

        var all = new ReadOnlySpan<byte>(_data);
        int idx = all.Slice(start).IndexOf(pattern);
        if (idx >= 0) idx += start;
        else idx = all.IndexOf(pattern);      // wrap to the beginning

        if (idx >= 0) Highlight(idx, pattern.Length);
        return idx;
    }

    public string Describe(int off)
    {
        if (_data == null || off < 0 || off >= _data.Length) return "";
        byte b = _data[off];
        char c = b >= 0x20 && b < 0x7F ? (char)b : '.';
        return $"Offset 0x{off:X8} ({off:N0})    Value 0x{b:X2}  {b}  '{c}'  {Convert.ToString(b, 2).PadLeft(8, '0')}b";
    }

    // ---- geometry --------------------------------------------------------------------
    private int TotalRows => _data == null ? 0 : (_data.Length + BytesPerRow - 1) / BytesPerRow;
    private int VisibleRows => Math.Max(1, (ClientSize.Height - _headerH) / _rowH);

    private void MeasureFont()
    {
        var sz = TextRenderer.MeasureText("0000000000", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        _charW = Math.Max(1, sz.Width / 10);
        _rowH = Font.Height + 1;
        _headerH = _rowH + 4;

        var h = new StringBuilder("Offset".PadRight(HexCol));
        for (int i = 0; i < BytesPerRow; i++)
        {
            h.Append(i.ToString("X2")).Append(' ');
            if (i == 7) h.Append(' ');
        }
        h.Append(' ').Append("ASCII");
        _headerText = h.ToString();
    }

    private void UpdateScroll()
    {
        int total = TotalRows, vis = VisibleRows;
        if (total <= vis)
        {
            _topRow = 0;
            _sb.Enabled = false;
            _sb.Minimum = 0;
            _sb.Maximum = 0;
            _sb.LargeChange = 1;
            _sb.Value = 0;
        }
        else
        {
            int maxTop = total - vis;
            if (_topRow > maxTop) _topRow = maxTop;
            _sb.Enabled = true;
            _sb.Minimum = 0;
            _sb.SmallChange = 1;
            _sb.LargeChange = vis;
            _sb.Maximum = total - 1;
            _sb.Value = _topRow;
        }
    }

    private void SetTop(int row)
    {
        int maxTop = Math.Max(0, TotalRows - VisibleRows);
        row = Math.Clamp(row, 0, maxTop);
        if (row == _topRow) return;
        _topRow = row;
        if (_sb.Enabled) _sb.Value = row;
        Invalidate();
    }

    private void SetCursor(int off)
    {
        _cursor = off;
        Invalidate();
        CursorChanged?.Invoke(off);
    }

    private void MoveCursor(int off)
    {
        SetCursor(off);
        int row = off / BytesPerRow;
        if (row < _topRow) SetTop(row);
        else if (row >= _topRow + VisibleRows) SetTop(row - VisibleRows + 1);
    }

    private static int HexX(int i) => HexCol + i * 3 + (i >= 8 ? 1 : 0);

    // ---- painting --------------------------------------------------------------------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Back);
        int w = Math.Max(0, ClientSize.Width - _sb.Width);

        if (_data == null || _data.Length == 0)
        {
            TextRenderer.DrawText(g, "No data. Use Open File or Read IC.", Font,
                new Point(PadX, _headerH + 6), HeaderFore, Flags);
        }
        else
        {
            using var hlBrush = new SolidBrush(FoundBack);
            using var curBrush = new SolidBrush(CursorBack);
            var hex = new StringBuilder(64);
            var asc = new StringBuilder(16);

            int rows = VisibleRows + 1;
            for (int r = 0; r < rows; r++)
            {
                int off = (_topRow + r) * BytesPerRow;
                if (off >= _data.Length) break;
                int y = _headerH + r * _rowH;
                int count = Math.Min(BytesPerRow, _data.Length - off);

                hex.Clear();
                asc.Clear();
                for (int i = 0; i < count; i++)
                {
                    byte b = _data[off + i];
                    hex.Append(b.ToString("X2")).Append(' ');
                    if (i == 7) hex.Append(' ');
                    asc.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');

                    int abs = off + i;
                    bool found = _hlLen > 0 && abs >= _hlStart && abs < _hlStart + _hlLen;
                    if (found || abs == _cursor)
                    {
                        var br = abs == _cursor ? curBrush : hlBrush;
                        g.FillRectangle(br, PadX + HexX(i) * _charW, y, 2 * _charW, _rowH);
                        g.FillRectangle(br, PadX + (AsciiCol + i) * _charW, y, _charW, _rowH);
                    }
                }

                TextRenderer.DrawText(g, off.ToString("X8"), Font, new Point(PadX, y), AddrFore, Flags);
                TextRenderer.DrawText(g, hex.ToString(), Font, new Point(PadX + HexCol * _charW, y), Fore, Flags);
                TextRenderer.DrawText(g, asc.ToString(), Font, new Point(PadX + AsciiCol * _charW, y), AsciiFore, Flags);
            }
        }

        using (var hb = new SolidBrush(HeaderBack))
            g.FillRectangle(hb, 0, 0, w, _headerH);
        TextRenderer.DrawText(g, _headerText, Font, new Point(PadX, 2), HeaderFore, Flags);
    }

    // ---- input -----------------------------------------------------------------------
    private int HitTest(int x, int y)
    {
        if (_data == null || y < _headerH || x < PadX) return -1;
        int row = _topRow + (y - _headerH) / _rowH;
        int col = (x - PadX) / _charW;
        int i;
        int rel = col - HexCol;
        if (rel >= 0 && rel < 49)
            i = rel < 24 ? rel / 3 : (rel == 24 ? 7 : (rel - 1) / 3);
        else if (col >= AsciiCol && col < AsciiCol + BytesPerRow)
            i = col - AsciiCol;
        else
            return -1;
        i = Math.Clamp(i, 0, BytesPerRow - 1);
        int idx = row * BytesPerRow + i;
        return idx < _data.Length ? idx : -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int idx = HitTest(e.X, e.Y);
        if (idx >= 0) SetCursor(idx);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        // so the mouse wheel works without clicking first (but never steal focus from a text box being typed in)
        if (!ContainsFocus && FindForm()?.ActiveControl is not TextBoxBase) Focus();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetTop(_topRow - Math.Sign(e.Delta) * 3);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
            case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End:
                return true;
        }
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_data == null || _data.Length == 0) return;

        int c = _cursor < 0 ? 0 : _cursor;
        int vis = VisibleRows;
        int n;
        switch (e.KeyCode)
        {
            case Keys.Left: n = c - 1; break;
            case Keys.Right: n = c + 1; break;
            case Keys.Up: n = c - BytesPerRow; break;
            case Keys.Down: n = c + BytesPerRow; break;
            case Keys.PageUp: n = c - BytesPerRow * vis; break;
            case Keys.PageDown: n = c + BytesPerRow * vis; break;
            case Keys.Home: n = e.Control ? 0 : c - c % BytesPerRow; break;
            case Keys.End: n = e.Control ? _data.Length - 1 : Math.Min(_data.Length - 1, c - c % BytesPerRow + BytesPerRow - 1); break;
            default: return;
        }
        e.Handled = true;
        MoveCursor(Math.Clamp(n, 0, _data.Length - 1));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        MeasureFont();
        UpdateScroll();
        Invalidate();
    }
}
