namespace UE5LibraryBrowser;

using System.ComponentModel;

internal sealed class ModelGridControl : UserControl
{
    private const int CellPadding = 8;
    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right };
    private readonly ToolTip _tooltip = new();
    private IReadOnlyList<UeLibraryModel> _items = Array.Empty<UeLibraryModel>();
    private int _hoverIndex = -1;
    private int _selectedIndex = -1;

    public ModelGridControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);

        BackColor = SystemColors.Window;
        TabStop = true;
        Controls.Add(_scroll);
        _scroll.Scroll += (_, _) =>
        {
            Invalidate();
            RequestVisibleRange();
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CellWidth { get; set; } = 176;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CellHeight { get; set; } = 240;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ThumbnailSize { get; set; } = 168;
    public int VirtualListSize => _items.Count;
    public int SelectedIndex => _selectedIndex;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<UeLibraryModel, Image>? ImageProvider { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<UeLibraryModel, string>? TextProvider { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<UeLibraryModel, string>? TooltipProvider { get; set; }

    public event EventHandler? SelectedIndexChanged;
    public event EventHandler? ItemActivated;
    public event Action<int, int>? VisibleRangeNeeded;

    public void SetItems(IReadOnlyList<UeLibraryModel> items, bool selectFirst = true)
    {
        _items = items;
        _selectedIndex = selectFirst && _items.Count > 0 ? 0 : -1;
        UpdateScroll();
        Invalidate();
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        RequestVisibleRange();
    }

    public void RefreshItem(int index)
    {
        if (index < 0 || index >= _items.Count)
            return;

        Invalidate(GetCellBounds(index));
    }

    public void EnsureVisible(int index)
    {
        if (index < 0 || index >= _items.Count)
            return;

        var columns = GetColumnCount();
        var row = index / columns;
        var top = row * CellHeight;
        var bottom = top + CellHeight;
        if (top < _scroll.Value)
            SetScrollValue(top);
        else if (bottom > _scroll.Value + GetViewportHeight())
            SetScrollValue(bottom - GetViewportHeight());
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
        RequestVisibleRange();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tooltip.Dispose();

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        if (_items.Count == 0)
            return;

        var columns = GetColumnCount();
        var firstRow = Math.Max(0, _scroll.Value / CellHeight);
        var lastRow = Math.Min(GetRowCount() - 1, (_scroll.Value + GetViewportHeight() + CellHeight - 1) / CellHeight);
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= _items.Count)
                    break;
                DrawItem(e.Graphics, index, row, column);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var index = HitTest(e.Location);
        if (index < 0)
            return;

        if (_selectedIndex != index)
        {
            var oldIndex = _selectedIndex;
            _selectedIndex = index;
            RefreshItem(oldIndex);
            RefreshItem(index);
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (_selectedIndex >= 0)
            ItemActivated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = HitTest(e.Location);
        if (index == _hoverIndex)
            return;

        _hoverIndex = index;
        if (index >= 0 && index < _items.Count && TooltipProvider != null)
            _tooltip.SetToolTip(this, TooltipProvider(_items[index]));
        else
            _tooltip.SetToolTip(this, "");
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var delta = -Math.Sign(e.Delta) * Math.Max(48, CellHeight / 3);
        SetScrollValue(_scroll.Value + delta);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_items.Count == 0)
            return base.ProcessCmdKey(ref msg, keyData);

        var columns = GetColumnCount();
        var next = _selectedIndex < 0 ? 0 : _selectedIndex;
        next = keyData switch
        {
            Keys.Left => Math.Max(0, next - 1),
            Keys.Right => Math.Min(_items.Count - 1, next + 1),
            Keys.Up => Math.Max(0, next - columns),
            Keys.Down => Math.Min(_items.Count - 1, next + columns),
            Keys.PageUp => Math.Max(0, next - columns * Math.Max(1, GetViewportHeight() / CellHeight)),
            Keys.PageDown => Math.Min(_items.Count - 1, next + columns * Math.Max(1, GetViewportHeight() / CellHeight)),
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            Keys.Enter => next,
            _ => -1
        };
        if (next < 0)
            return base.ProcessCmdKey(ref msg, keyData);

        if (keyData == Keys.Enter)
        {
            ItemActivated?.Invoke(this, EventArgs.Empty);
            return true;
        }

        SelectIndex(next);
        return true;
    }

    private void SelectIndex(int index)
    {
        if (index < 0 || index >= _items.Count || _selectedIndex == index)
            return;

        var oldIndex = _selectedIndex;
        _selectedIndex = index;
        EnsureVisible(index);
        RefreshItem(oldIndex);
        RefreshItem(index);
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DrawItem(Graphics graphics, int index, int row, int column)
    {
        var cell = new Rectangle(
            CellPadding + column * CellWidth,
            CellPadding + row * CellHeight - _scroll.Value,
            CellWidth - CellPadding,
            CellHeight - CellPadding);

        var selected = index == _selectedIndex;
        if (selected)
        {
            using var selectedBack = new SolidBrush(Color.FromArgb(214, 232, 248));
            graphics.FillRectangle(selectedBack, cell);
            using var selectedPen = new Pen(Color.FromArgb(0, 120, 215), 2);
            graphics.DrawRectangle(selectedPen, Rectangle.Inflate(cell, -1, -1));
        }

        var model = _items[index];
        var image = ImageProvider?.Invoke(model);
        var imageRect = new Rectangle(
            cell.X + Math.Max(0, (cell.Width - ThumbnailSize) / 2),
            cell.Y,
            ThumbnailSize,
            ThumbnailSize);
        if (image != null)
            graphics.DrawImage(image, imageRect);

        var text = TextProvider?.Invoke(model) ?? model.Name;
        var lines = text.Split(Environment.NewLine, StringSplitOptions.None);
        var name = lines.Length > 0 ? lines[0] : model.Name;
        var count = lines.Length > 1 ? lines[1] : "";
        var textColor = selected ? SystemColors.HighlightText : SystemColors.ControlText;
        var nameRect = new Rectangle(cell.X, imageRect.Bottom + 4, cell.Width, 24);
        var countRect = new Rectangle(cell.X, nameRect.Bottom, cell.Width, 24);
        TextRenderer.DrawText(
            graphics,
            name,
            Font,
            nameRect,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (!string.IsNullOrWhiteSpace(count))
        {
            TextRenderer.DrawText(
                graphics,
                count,
                Font,
                countRect,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private int HitTest(Point point)
    {
        if (point.X >= ClientSize.Width - _scroll.Width)
            return -1;

        var columns = GetColumnCount();
        var column = (point.X - CellPadding) / CellWidth;
        var row = (point.Y + _scroll.Value - CellPadding) / CellHeight;
        if (column < 0 || column >= columns || row < 0)
            return -1;

        var index = row * columns + column;
        return index >= 0 && index < _items.Count ? index : -1;
    }

    private Rectangle GetCellBounds(int index)
    {
        if (index < 0)
            return Rectangle.Empty;
        var columns = GetColumnCount();
        var row = index / columns;
        var column = index % columns;
        return new Rectangle(
            CellPadding + column * CellWidth,
            CellPadding + row * CellHeight - _scroll.Value,
            CellWidth,
            CellHeight);
    }

    private int GetColumnCount()
        => Math.Max(1, (Math.Max(1, ClientSize.Width - _scroll.Width - CellPadding) / CellWidth));

    private int GetRowCount()
        => _items.Count == 0 ? 0 : (_items.Count + GetColumnCount() - 1) / GetColumnCount();

    private int GetViewportHeight()
        => Math.Max(1, ClientSize.Height - CellPadding * 2);

    private void UpdateScroll()
    {
        var contentHeight = GetRowCount() * CellHeight + CellPadding * 2;
        var viewportHeight = GetViewportHeight();
        var maximum = Math.Max(0, contentHeight - viewportHeight);
        _scroll.Minimum = 0;
        _scroll.LargeChange = Math.Max(1, viewportHeight);
        _scroll.SmallChange = Math.Max(24, CellHeight / 4);
        _scroll.Maximum = maximum + _scroll.LargeChange - 1;
        SetScrollValue(Math.Min(_scroll.Value, maximum));
        _scroll.Enabled = maximum > 0;
    }

    private void SetScrollValue(int value)
    {
        var maximum = Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1);
        var next = Math.Clamp(value, 0, maximum);
        if (_scroll.Value == next)
            return;
        _scroll.Value = next;
        Invalidate();
        RequestVisibleRange();
    }

    private void RequestVisibleRange()
    {
        if (_items.Count == 0)
            return;

        var columns = GetColumnCount();
        var firstRow = Math.Max(0, _scroll.Value / CellHeight);
        var lastRow = Math.Min(GetRowCount() - 1, (_scroll.Value + GetViewportHeight() + CellHeight - 1) / CellHeight);
        var start = Math.Clamp(firstRow * columns, 0, _items.Count - 1);
        var end = Math.Clamp(((lastRow + 1) * columns) - 1, start, _items.Count - 1);
        VisibleRangeNeeded?.Invoke(start, end);
    }
}
