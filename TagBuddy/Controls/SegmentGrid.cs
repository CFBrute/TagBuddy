using TagBuddy.Services;

namespace TagBuddy.Controls;

/// <summary>The segment editor's view of a <see cref="TagEdit"/>, laid out like the read view's memory: one row per page
/// or block ("005  48 65 6C 6C  Hell"), MIFARE Classic blocks under their sector, the rows that are never written greyed
/// with what they hold. Tapping a byte, in the hex or the text column, puts the edit's cursor there; the page then
/// sends the keyboard's typing to the edit.
///
/// <para>Every character is drawn in its own fixed-width cell, so what is drawn and what a tap hits are computed the same
/// way whatever font the phone has. Typed bytes are drawn in the accent colour, bytes not known (set up by hand) as ··,
/// and the cursor as a highlight on its nibble or character, echoed faintly in the other column.</para></summary>
public class SegmentGrid : GraphicsView, IDrawable
{
    private const float FontSize = 13f, CharWidth = 8.2f, LineHeight = 22f, Pad = 6f, Baseline = 15.5f;
    private static readonly Microsoft.Maui.Graphics.Font MonoFont = new("monospace");

    // MIFARE Classic structure colours, matching the read view (ClassicDumpView): block 0's UID / check byte / maker
    // bytes, and a trailer's key A / rules / key B. A field byte that was typed stays in the accent colour instead.
    private static readonly Color CUid = Color.FromArgb("#22C55E"), CBcc = Color.FromArgb("#EF4444"), CMaker = Color.FromArgb("#F59E0B");
    private static readonly Color CKeyA = Color.FromArgb("#3B82F6"), CRules = Color.FromArgb("#A855F7"), CKeyB = Color.FromArgb("#F59E0B");

    /// <summary>"005" and two spaces before the hex.</summary>
    private const int NumberColumns = 5;

    /// <summary>One row showing part of one segment, under a label of its own: a key's six bytes of a sector trailer
    /// ("Key A  FF FF FF FF  FF FF  ······"), edited like any row of the full grid.</summary>
    public sealed record RowWindow(int Segment, int Start, int Count, string Label);

    private RowWindow? _window;

    /// <summary>Shows only <see cref="RowWindow"/>'s bytes of its segment instead of every row; null for the full grid.</summary>
    public RowWindow? Window
    {
        get => _window;
        set { if (_window == value) return; _window = value; Refresh(); }
    }

    /// <summary>The columns before the hex, the first byte of a segment shown and how many are shown on a row.</summary>
    private int Lead => _window is { } w ? w.Label.Length + 2 : NumberColumns;
    private int First => _window?.Start ?? 0;
    private int Shown => _window?.Count ?? _edit?.Layout.SegmentSize ?? 4;

    /// <summary>A row: a segment, or (MIFARE Classic) the header of the sector below.</summary>
    private sealed record Row(int Segment, string? Header);

    private TagEdit? _edit;
    private List<Row> _rows = [];

    /// <summary>A byte was tapped and the cursor placed on it.</summary>
    public event Action? CursorPlaced;

    /// <summary>The selection changed (started, extended or cleared).</summary>
    public event Action? SelectionChanged;

    private bool _selecting;
    private int _selAnchor = -1, _selEnd = -1;

    /// <summary>Selection mode: taps extend a highlighted run of bytes (anchored where the cursor was) instead of just
    /// placing the cursor, so a range can be copied. Turning it off clears the selection.</summary>
    public bool Selecting
    {
        get => _selecting;
        set
        {
            _selecting = value;
            if (value && _edit is { Length: > 0 } ed) _selAnchor = _selEnd = ed.Cursor;
            else _selAnchor = _selEnd = -1;
            Invalidate();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>The selected run of bytes as inclusive positions (min..max), or null when nothing is selected.</summary>
    public (int Start, int End)? Selection =>
        _selecting && _selAnchor >= 0 && _selEnd >= 0 ? (Math.Min(_selAnchor, _selEnd), Math.Max(_selAnchor, _selEnd)) : null;

    public SegmentGrid()
    {
        Drawable = this;
        var tap = new TapGestureRecognizer();
        tap.Tapped += OnTapped;
        GestureRecognizers.Add(tap);
    }

    public TagEdit? Edit
    {
        get => _edit;
        set { _edit = value; _selecting = false; _selAnchor = _selEnd = -1; Refresh(); }
    }

    /// <summary>Lays the rows out again (after the range or the edit changed) and redraws.</summary>
    public void Refresh()
    {
        _rows = BuildRows();
        int size = Shown;
        // As wide as the rows need: a role is only written on the rows that are never written.
        int roles = _edit is { } edit && _window is null
            ? _rows.Where(r => r.Header is null && edit.WhyNotWritable(r.Segment) is not null)
                   .Select(r => edit.RoleOf(r.Segment).Length).DefaultIfEmpty(0).Max()
            : 0;
        int headers = _rows.Select(r => r.Header?.Length ?? 0).DefaultIfEmpty(0).Max();
        int columns = Math.Max(roles > 0 ? RoleStart(size) + roles : AsciiStart(size) + size, headers);
        WidthRequest = Pad * 2 + columns * CharWidth;
        HeightRequest = Pad * 2 + Math.Max(1, _rows.Count) * LineHeight;
        Invalidate();
    }

    /// <summary>The top of the cursor's row, for keeping it on screen while typing.</summary>
    public double CursorY
    {
        get
        {
            if (_edit is not { Length: > 0 } edit) return 0;
            var (segment, _) = edit.At(edit.Cursor);
            int row = _rows.FindIndex(r => r.Header is null && r.Segment == segment);
            return Pad + Math.Max(0, row) * LineHeight;
        }
    }

    /// <summary>The left of the cursor's cell (in the hex or the text column), for keeping it on screen while typing:
    /// the hidden input sits there, and Android scrolls to whatever has the keyboard.</summary>
    public double CursorX
    {
        get
        {
            if (_edit is not { Length: > 0 } edit) return 0;
            int size = Shown;
            var (_, at) = edit.At(edit.Cursor);
            int index = Math.Clamp(at - First, 0, size - 1);
            int column = edit.InText ? AsciiStart(size) + index : HexStart(index);
            return ColumnX(column);
        }
    }

    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        if (_edit is not { } edit) return rows;
        if (_window is { } window) return [new Row(window.Segment, null)];

        // MIFARE Classic: show block 0 (the manufacturer/serial block) as a read-only row too, as the read view
        // does, even though it is never part of the writable range, so the whole card is visible while editing.
        int from = edit.From;
        if (edit.Layout.Family == TagFamily.Classic && from > 0 && Known(edit, 0)) from = 0;

        for (int s = from; s <= edit.To; s++)
        {
            if (edit.Layout.Family == TagFamily.Classic && (s == from || MifareClassicKeys.SectorOf(s) != MifareClassicKeys.SectorOf(s - 1)))
            {
                int sector = MifareClassicKeys.SectorOf(s);
                bool closed = edit.RoleOf(s) == "sector not opened";
                rows.Add(new Row(s, $"Sector {sector:D2}{(closed ? " · not opened" : "")}"));
            }
            rows.Add(new Row(s, null));
        }
        return rows;
    }

    /// <summary>Whether any byte of a segment is known (read from the tag), so it is worth showing.</summary>
    private static bool Known(TagEdit edit, int segment) =>
        Enumerable.Range(0, edit.Layout.SegmentSize).Any(i => edit.ValueAt(segment, i) is not null);

    // ------------------------------------------------------------------------------------------------ columns

    /// <summary>The column of byte <paramref name="k"/> in the hex: two digits and a space each, one more space every
    /// four bytes, as the read view groups them.</summary>
    private int HexStart(int k) => Lead + k * 3 + k / 4;
    private int AsciiStart(int size) => HexStart(size - 1) + 2 + 2;
    private int RoleStart(int size) => AsciiStart(size) + size + 2;

    // ------------------------------------------------------------------------------------------------ drawing

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (_edit is not { } edit) return;
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var text = dark ? Color.FromArgb("#F3F4F6") : Color.FromArgb("#111827");
        var dim = Color.FromArgb(dark ? "#6B7280" : "#9CA3AF");
        // A typed byte stands out in pink — distinct from every structure colour (amber key B / maker bytes read as
        // the old accent did, so edits there were invisible).
        var typed = Color.FromArgb(dark ? "#F472B6" : "#DB2777");
        var cursor = Color.FromArgb("#F5A623").WithAlpha(0.5f);
        var echo = Color.FromArgb("#F5A623").WithAlpha(0.2f);
        var select = Color.FromArgb("#3B82F6").WithAlpha(0.30f);   // the selected run, behind the bytes
        var sel = Selection;

        canvas.Font = MonoFont;
        canvas.FontSize = FontSize;
        int size = Shown;
        var (cursorSegment, cursorIndex) = edit.Cursor < edit.Length ? edit.At(edit.Cursor) : (-1, -1);

        for (int r = 0; r < _rows.Count; r++)
        {
            float y = Pad + r * LineHeight;
            if (y > dirtyRect.Bottom || y + LineHeight < dirtyRect.Top) continue;
            var row = _rows[r];
            if (row.Header is { } header)
            {
                Cell(canvas, header, 0, y, header.Length, dim);
                continue;
            }

            int s = row.Segment;
            if (_window is { } window) Cell(canvas, window.Label, 0, y, window.Label.Length, text);
            else Cell(canvas, s.ToString("D3"), 0, y, 3, dim);

            for (int c = 0; c < size; c++)
            {
                int k = First + c;   // the byte of the segment; c is its place on the row
                byte? value = edit.ValueAt(s, k);
                bool writable = edit.WritableByte(s, k);   // per byte: a trailer's key bytes are editable, its access bits are not
                var color = !writable || value is null ? dim : edit.IsEdited(s, k) ? typed : text;
                // Colour block 0 and trailer bytes by their field, so each sector's structure is visible. A byte the
                // person typed keeps the accent colour; an unknown byte stays dim. Block 0's check byte (BCC) is
                // derived, not typed, so it keeps its field colour even after a UID change.
                bool derivedBcc = edit.Layout.Family == TagFamily.Classic && s == 0 && k == 4;
                if (value is not null && (!edit.IsEdited(s, k) || derivedBcc) && FieldColor(edit, s, k) is { } field) color = field;

                if (sel is { } range && edit.PositionOf(s, k) is var p && p >= range.Start && p <= range.End && p >= 0)
                {
                    canvas.FillColor = select;
                    canvas.FillRectangle(ColumnX(HexStart(c)), y + 2, CharWidth * 2, LineHeight - 4);
                    canvas.FillRectangle(ColumnX(AsciiStart(size) + c), y + 2, CharWidth, LineHeight - 4);
                }

                if (s == cursorSegment && k == cursorIndex)
                {
                    canvas.FillColor = edit.InText ? echo : cursor;
                    canvas.FillRectangle(ColumnX(HexStart(c) + (edit.InText ? 0 : edit.Nibble)), y + 2, CharWidth * (edit.InText ? 2 : 1), LineHeight - 4);
                    canvas.FillColor = edit.InText ? cursor : echo;
                    canvas.FillRectangle(ColumnX(AsciiStart(size) + c), y + 2, CharWidth, LineHeight - 4);
                }

                Cell(canvas, value?.ToString("X2") ?? "··", HexStart(c), y, 2, color);
                var ch = value switch { null => "·", >= 0x20 and < 0x7F => ((char)value).ToString(), _ => "." };
                Cell(canvas, ch, AsciiStart(size) + c, y, 1, color);
            }

            // A role (e.g. "serial", "trailer") labels a row the editor leaves alone as a whole.
            if (_window is null && edit.WhyNotWritable(s) is not null && edit.RoleOf(s) is { Length: > 0 } role)
                Cell(canvas, role, RoleStart(size), y, role.Length, dim);
        }
    }

    /// <summary>The structure colour of a MIFARE Classic byte: block 0's UID/check/maker, or a trailer's key A/rules/
    /// key B (byte 9 is spare, left to its normal colour). Null for an ordinary data byte or another tag family.</summary>
    private static Color? FieldColor(TagEdit edit, int segment, int k)
    {
        if (edit.Layout.Family != TagFamily.Classic) return null;
        if (segment == 0) return k < 4 ? CUid : k == 4 ? CBcc : CMaker;
        if (MifareClassicKeys.IsTrailer(segment)) return k < 6 ? CKeyA : k < 9 ? CRules : k == 9 ? null : CKeyB;
        return null;
    }

    private static float ColumnX(int column) => Pad + column * CharWidth;

    /// <summary>Draws a cell's text in one left-aligned call, anchored at the cell's column. One short string per cell
    /// (not one per character) keeps the draw count and per-glyph measuring down — the per-keystroke cost that made
    /// the editor crawl — while re-anchoring each cell keeps the columns lined up in a monospace font.</summary>
    private static void Cell(ICanvas canvas, string value, int column, float y, int width, Color color)
    {
        if (value.Length == 0) return;
        canvas.FontColor = color;
        canvas.DrawString(value.Length > width ? value[..width] : value, ColumnX(column), y + Baseline, HorizontalAlignment.Left);
    }

    // ------------------------------------------------------------------------------------------------ tapping

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (_edit is not { } edit || e.GetPosition(this) is not { } point) return;
        int r = (int)((point.Y - Pad) / LineHeight);
        if (r < 0 || r >= _rows.Count || _rows[r].Header is not null) return;

        if (!IsEnabled) return;
        int size = Shown;
        int column = (int)Math.Floor((point.X - Pad) / CharWidth);
        bool inText = column >= AsciiStart(size) - 1;
        int k = 0, nibble = 0;
        if (inText) k = Math.Clamp(column - AsciiStart(size), 0, size - 1);
        else
        {
            for (int j = 0; j < size; j++) if (column >= HexStart(j)) k = j;
            nibble = column - HexStart(k) == 1 ? 1 : 0;
        }

        int position = edit.PositionOf(_rows[r].Segment, First + k);
        if (position < 0) return;                       // a row that is never written
        edit.Place(position, inText, nibble);
        if (_selecting)                                 // extend the selection to here; no keyboard in select mode
        {
            _selEnd = position;
            Invalidate();
            SelectionChanged?.Invoke();
            return;
        }
        Invalidate();
        CursorPlaced?.Invoke();
    }
}
