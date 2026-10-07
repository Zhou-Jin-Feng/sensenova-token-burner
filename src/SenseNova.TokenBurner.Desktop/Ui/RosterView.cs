using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>key 列表：每行一个任务，勾选框决定批量范围，高亮行决定右侧详情。整表自绘，按数据重画，不会残留旧行。</summary>
internal sealed class RosterList : UiControl
{
    private IReadOnlyList<TaskRowView> _items = [];
    private Func<Guid, bool> _isChecked = _ => false;
    private int _offset;
    private int _hover = -1;
    private bool _hoverCheck;
    private bool _hoverIssue;
    private bool _dragging;
    private int _dragStartY;
    private int _dragStartOffset;
    private readonly ToolTip _tip = Tips.Create();
    private string? _tipText;

    public RosterList()
    {
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "API key 任务列表";
    }

    public Guid? SelectedId { get; private set; }
    private readonly List<Rectangle> _pulseAreas = [];
    /// <summary>呼吸相位；只重画运行中任务的状态胶囊。</summary>
    public float Pulse { get; set { field = value; foreach (var area in _pulseAreas) Invalidate(area); } }
    public IReadOnlyList<TaskRowView> Items => _items;
    public event Action<Guid>? SelectRequested;
    public event Action<Guid>? CheckToggled;
    public event Action<Guid, Point>? MenuRequested;

    public void SetData(IReadOnlyList<TaskRowView> items, Guid? selected, Func<Guid, bool> isChecked)
    {
        _items = items; SelectedId = selected; _isChecked = isChecked;
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        if (_hover >= _items.Count) _hover = -1;
        Invalidate();
    }

    private int RowHeight => S(80);
    private int ContentHeight => _items.Count * RowHeight + S(8);
    private int MaxOffset => Math.Max(0, ContentHeight - Height);
    private Rectangle RowBounds(int index) => new(0, S(4) + index * RowHeight - _offset, Width - S(6), RowHeight);
    private Rectangle CheckBounds(Rectangle row) => new(row.X + S(16), row.Y + S(14), S(18), S(18));
    private int IndexAt(Point point)
    {
        if (point.Y < S(4) - _offset) return -1;
        var index = (point.Y + _offset - S(4)) / RowHeight;
        return index >= 0 && index < _items.Count ? index : -1;
    }

    public void EnsureVisible(Guid id)
    {
        var index = _items.ToList().FindIndex(item => item.Id == id);
        if (index < 0) return;
        var top = S(4) + index * RowHeight;
        if (top < _offset) _offset = top - S(4);
        else if (top + RowHeight > _offset + Height) _offset = top + RowHeight - Height + S(4);
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); _offset = Math.Clamp(_offset, 0, MaxOffset); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Space or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        var index = SelectedId is { } id ? _items.ToList().FindIndex(item => item.Id == id) : -1;
        var next = e.KeyCode switch
        {
            Keys.Up => Math.Max(0, index - 1), Keys.Down => Math.Min(_items.Count - 1, index + 1),
            Keys.Home => 0, Keys.End => _items.Count - 1, _ => -2
        };
        if (next >= 0 && next != index) { SelectRequested?.Invoke(_items[next].Id); EnsureVisible(_items[next].Id); }
        if (e.KeyCode == Keys.Space && index >= 0) CheckToggled?.Invoke(_items[index].Id);
        if ((e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift)) && index >= 0)
            MenuRequested?.Invoke(_items[index].Id, PointToScreen(new Point(S(40), RowBounds(index).Bottom - S(10))));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _offset = Math.Clamp(_offset - e.Delta * S(48) / 120, 0, MaxOffset);
        Invalidate();
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    private Rectangle Thumb()
    {
        if (ContentHeight <= Height || Height <= 0) return Rectangle.Empty;
        var length = Math.Max(S(32), Height * Height / ContentHeight);
        var top = (int)((long)(Height - length) * _offset / Math.Max(1, MaxOffset));
        return new Rectangle(Width - S(5), top + S(2), S(4), length - S(4));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var thumb = Thumb();
        if (!thumb.IsEmpty && e.X >= Width - S(8))
        {
            if (thumb.Contains(e.X - 0, e.Y) || (e.Y >= thumb.Top && e.Y <= thumb.Bottom)) { _dragging = true; _dragStartY = e.Y; _dragStartOffset = _offset; Capture = true; }
            return;
        }
        var index = IndexAt(e.Location);
        if (index < 0) return;
        var item = _items[index];
        var row = RowBounds(index);
        if (e.Button == MouseButtons.Left && Rectangle.Inflate(CheckBounds(row), S(8), S(8)).Contains(e.Location))
        {
            if (item.Enabled) CheckToggled?.Invoke(item.Id);
            return;
        }
        if (item.Id != SelectedId) SelectRequested?.Invoke(item.Id);
        if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(item.Id, PointToScreen(e.Location));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var travel = Math.Max(1, Height - Thumb().Height - S(4));
            _offset = Math.Clamp(_dragStartOffset + (int)((long)(e.Y - _dragStartY) * MaxOffset / travel), 0, MaxOffset);
            Invalidate();
            return;
        }
        var index = IndexAt(e.Location);
        var check = index >= 0 && Rectangle.Inflate(CheckBounds(RowBounds(index)), S(8), S(8)).Contains(e.Location);
        var issue = index >= 0 && _items[index].Issue is not null && IssueBounds(RowBounds(index)).Contains(e.Location);
        if (index != _hover || check != _hoverCheck || issue != _hoverIssue)
        {
            _hover = index; _hoverCheck = check; _hoverIssue = issue; Invalidate();
            var text = issue ? _items[index].Issue : null;
            if (text != _tipText) { _tipText = text; _tip.SetToolTip(this, text ?? ""); }
        }
        Cursor = check ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (_dragging) { _dragging = false; Capture = false; } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; _hoverCheck = false; _hoverIssue = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private Rectangle IssueBounds(Rectangle row) => new(row.Right - S(36), row.Y + S(52), S(20), S(20));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Draw.Smooth(g);
        _pulseAreas.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var row = RowBounds(i);
            if (row.Bottom < 0 || row.Top > Height) continue;
            PaintRow(g, _items[i], row, i);
        }
        var thumb = Thumb();
        if (!thumb.IsEmpty) Draw.Fill(g, thumb, thumb.Width / 2f, _dragging ? P.Subtle : P.LineStrong);
    }

    private void PaintRow(Graphics g, TaskRowView item, Rectangle row, int index)
    {
        var selected = item.Id == SelectedId;
        var hover = index == _hover;
        var card = new Rectangle(row.X + S(8), row.Y + S(3), row.Width - S(10), row.Height - S(6));
        if (selected)
        {
            Draw.Fill(g, card, Theme.Sf(9), P.SurfaceSelected);
            Draw.Fill(g, new Rectangle(card.X, card.Y + S(14), S(3), card.Height - S(28)), Theme.Sf(1.5f), P.Primary);
            if (Focused && ShowFocusCues) Draw.Stroke(g, card, Theme.Sf(9), P.Primary, Theme.Sf(1.25f));
        }
        else if (hover) Draw.Fill(g, card, Theme.Sf(9), P.SurfaceHover);
        else if (index > 0 && _items[index - 1].Id != SelectedId && index - 1 != _hover)
            using (var line = new Pen(P.Line)) g.DrawLine(line, card.X + S(14), row.Y, card.Right - S(14), row.Y);

        var muted = !item.Enabled;
        // 勾选框
        var box = CheckBounds(row);
        var isChecked = _isChecked(item.Id) && item.Enabled;
        if (isChecked) Draw.Fill(g, box, Theme.Sf(4), P.Primary);
        else
        {
            Draw.Fill(g, box, Theme.Sf(4), P.Input);
            Draw.Stroke(g, box, Theme.Sf(4), !item.Enabled ? P.Line : hover && _hoverCheck ? P.Primary : P.LineStrong, Math.Max(1, Theme.Sf(1.25f)));
        }
        if (isChecked)
            using (var pen = new Pen(P.OnPrimary, Math.Max(1.5f, Theme.Sf(1.8f))) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                g.DrawLines(pen, [new PointF(box.X + box.Width * 0.24f, box.Y + box.Height * 0.53f),
                    new PointF(box.X + box.Width * 0.43f, box.Y + box.Height * 0.71f), new PointF(box.X + box.Width * 0.77f, box.Y + box.Height * 0.32f)]);

        // 第一行：名称、额度组、状态
        var left = box.Right + S(12);
        var pillSize = Draw.PillSize(item.Status, F.Caption);
        var pill = new Rectangle(card.Right - S(12) - pillSize.Width, row.Y + S(12), pillSize.Width, pillSize.Height);
        var (fore, back) = Draw.StatusColors(item.Kind);
        if (item.Kind == TaskStatusKind.Burning) { back = Draw.Mix(back, fore, 0.08f + 0.1f * Pulse); _pulseAreas.Add(pill); }
        Draw.Pill(g, pill, item.Status, F.Caption, fore, back);
        var nameWidth = Math.Min(Draw.Measure(item.Name, F.BodyBold).Width, pill.X - left - S(10));
        Draw.Text(g, item.Name, F.BodyBold, new Rectangle(left, row.Y + S(12), nameWidth, pillSize.Height), muted ? P.Muted : P.Ink);
        if (!string.IsNullOrEmpty(item.Group))
        {
            var groupLeft = left + nameWidth + S(8);
            var groupWidth = pill.X - groupLeft - S(8);
            if (groupWidth > S(24))
                Draw.Text(g, item.Group, F.Caption, new Rectangle(groupLeft, row.Y + S(12), groupWidth, pillSize.Height), P.Muted);
        }

        // 第二行：用量条
        var meter = new Rectangle(left, row.Y + S(43), card.Right - S(12) - left, S(6));
        var meterColor = item.Kind switch
        {
            TaskStatusKind.Burning => P.Ember, TaskStatusKind.Success => P.Ok, TaskStatusKind.Warning => P.Warn,
            TaskStatusKind.Danger => P.Danger, _ => muted ? P.LineStrong : P.Primary
        };
        Draw.Meter(g, meter, item.Fraction, item.ReservedFraction, meterColor, P.Track);

        // 第三行：用量、下次定时、异常、百分比
        var line3 = new Rectangle(left, row.Y + S(54), meter.Width, S(20));
        var confirmed = Format.Compact(item.Confirmed);
        var confirmedWidth = Draw.Measure(confirmed, F.Number).Width;
        Draw.Text(g, confirmed, F.Number, new Rectangle(line3.X, line3.Y, confirmedWidth + S(2), line3.Height), muted ? P.Muted : P.Ink);
        Draw.Text(g, " / " + Format.Compact(item.Target), F.Number, new Rectangle(line3.X + confirmedWidth, line3.Y, S(90), line3.Height), P.Muted);
        var percent = Format.Percent(item.Fraction);
        var percentWidth = Draw.Measure(percent, F.NumberBold).Width;
        var right = line3.Right;
        Draw.Text(g, percent, F.NumberBold, new Rectangle(right - percentWidth, line3.Y, percentWidth + S(2), line3.Height), muted ? P.Muted : P.Ink);
        right -= percentWidth + S(10);
        if (item.Issue is not null)
        {
            var issue = IssueBounds(row);
            issue.X = right - S(18);
            var color = item.Kind is TaskStatusKind.Danger ? P.Danger : item.Kind is TaskStatusKind.Warning ? P.Warn : P.Muted;
            Draw.Glyph(g, item.Kind is TaskStatusKind.Danger or TaskStatusKind.Warning ? Glyphs.Warning : Glyphs.Info, F.IconSmall, issue, color);
            right = issue.X - S(6);
        }
        if (item.NextRun is not null)
        {
            var width = Draw.Measure(item.NextRun, F.NumberCaption).Width;
            var textLeft = right - width;
            if (textLeft > line3.X + confirmedWidth + S(90))
            {
                Draw.Text(g, item.NextRun, F.NumberCaption, new Rectangle(textLeft, line3.Y, width + S(2), line3.Height), P.Muted);
                Draw.Glyph(g, Glyphs.Clock, F.IconSmall, new Rectangle(textLeft - S(18), line3.Y, S(16), line3.Height), P.Muted);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>左侧面板：标题、批量操作条和 key 列表。</summary>
internal sealed class RosterPane : UiControl
{
    private readonly UiToggle _selectAll = new("全选", ToggleStyle.Check, "SelectAll") { AccessibleName = "全选任务" };
    private string _selectionText = "";
    private string _countText = "";
    private bool _empty = true;

    public RosterPane()
    {
        AddButton = new UiButton("添加 key", ButtonKind.Primary, Glyphs.Add, "AddKey") { Compact = true };
        BatchRun = new UiButton("运行/继续", ButtonKind.Secondary, Glyphs.Play, "BatchRun") { Compact = true };
        BatchPause = new UiButton("暂停", ButtonKind.Secondary, Glyphs.Pause, "BatchPause") { Compact = true };
        BatchReset = new UiButton("重置", ButtonKind.Danger, Glyphs.Reset, "BatchReset") { Compact = true };
        EmptyAdd = new UiButton("添加第一个 API key", ButtonKind.Primary, Glyphs.Add, "EmptyAddKey");
        _selectAll.Click += (_, _) => SelectAllClicked?.Invoke();
        Controls.AddRange([AddButton, _selectAll, BatchRun, BatchPause, BatchReset, List, EmptyAdd]);
    }

    public UiButton AddButton { get; }
    public UiButton BatchRun { get; }
    public UiButton BatchPause { get; }
    public UiButton BatchReset { get; }
    public UiButton EmptyAdd { get; }
    public RosterList List { get; } = new();
    public UiToggle SelectAll => _selectAll;
    /// <summary>任务配置不可读：空状态改为错误说明并隐藏添加入口。</summary>
    public bool Failed { get; set { if (field == value) return; field = value; PerformLayout(); Invalidate(); } }
    public event Action? SelectAllClicked;

    public void SetSummary(int total, int enabled, int checkedCount, string? batchText)
    {
        _countText = total == 0 ? "" : enabled == total ? $"{total}" : $"{enabled}/{total}";
        _selectionText = batchText ?? (checkedCount == 0 ? "勾选后可批量操作" : $"已选 {checkedCount}");
        var selectable = enabled;
        _selectAll.Checked = selectable > 0 && checkedCount >= selectable;
        _selectAll.Mixed = checkedCount > 0 && checkedCount < selectable;
        _selectAll.Enabled = selectable > 0;
        BatchRun.Enabled = BatchPause.Enabled = BatchReset.Enabled = checkedCount > 0;
        var empty = total == 0;
        if (empty != _empty) { _empty = empty; PerformLayout(); }
        Invalidate(new Rectangle(0, 0, Width, S(104)));
    }

    private int HeaderHeight => S(56);
    private int BarHeight => S(48);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = S(16);
        var add = AddButton.GetPreferredSize(Size.Empty);
        AddButton.SetBounds(Width - pad - add.Width, (HeaderHeight - add.Height) / 2 + S(2), add.Width, add.Height);
        _selectAll.Visible = BatchRun.Visible = BatchPause.Visible = BatchReset.Visible = !_empty;
        List.Visible = !_empty;
        EmptyAdd.Visible = _empty && !Failed;
        if (_empty)
        {
            var size = EmptyAdd.GetPreferredSize(Size.Empty);
            EmptyAdd.SetBounds((Width - size.Width) / 2, HeaderHeight + (Height - HeaderHeight) / 2 + S(56), size.Width, size.Height);
            return;
        }
        var barTop = HeaderHeight;
        var check = _selectAll.GetPreferredSize(Size.Empty);
        _selectAll.SetBounds(pad + S(1), barTop + (BarHeight - check.Height) / 2, check.Width, check.Height);
        // 空间不够时批量按钮只显示图标（仍有无障碍名称和提示）。
        UiButton[] buttons = [BatchRun, BatchPause, BatchReset];
        string[] labels = ["运行/继续", "暂停", "重置"];
        for (var i = 0; i < buttons.Length; i++) buttons[i].Text = labels[i];
        var widths = buttons.Select(button => button.GetPreferredSize(Size.Empty).Width).ToArray();
        // 优先保留按钮文字；“已选 N”说明只在剩余空间足够时显示（绘制时判断）。
        if (widths.Sum() + S(6) * 2 + check.Width + S(12) > Width - pad * 2)
        {
            foreach (var button in buttons) button.Text = "";
            widths = buttons.Select(button => button.GetPreferredSize(Size.Empty).Width).ToArray();
        }
        var x = Width - pad;
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            x -= widths[i];
            buttons[i].SetBounds(x, barTop + (BarHeight - buttons[i].PreferredHeight) / 2, widths[i], buttons[i].PreferredHeight);
            x -= S(6);
        }
        List.SetBounds(S(4), barTop + BarHeight + S(1), Width - S(6), Math.Max(0, Height - barTop - BarHeight - S(6)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintPanel(g, Theme.Sf(12), P.Surface, P.Line);
        var pad = S(16);
        var title = "API key";
        var titleSize = Draw.Measure(title, F.Section);
        Draw.Text(g, title, F.Section, new Rectangle(pad, S(2), titleSize.Width + S(2), HeaderHeight), P.Ink);
        if (_countText.Length > 0)
        {
            var count = Draw.Measure(_countText, F.Number);
            var badge = new Rectangle(pad + titleSize.Width + S(8), (HeaderHeight - S(20)) / 2 + S(1), count.Width + S(14), S(20));
            Draw.Fill(g, badge, badge.Height / 2f, P.SurfaceAlt);
            Draw.Stroke(g, badge, badge.Height / 2f, P.Line);
            Draw.Text(g, _countText, F.Number, badge, P.Muted, Draw.Single | TextFormatFlags.HorizontalCenter);
        }
        using var line = new Pen(P.Line);
        if (_empty)
        {
            g.DrawLine(line, 0, HeaderHeight, Width, HeaderHeight);
            var center = HeaderHeight + (Height - HeaderHeight) / 2;
            var mark = S(56);
            BrandMark.Paint(g, new RectangleF((Width - mark) / 2f, center - S(84), mark, mark), burning: false);
            Draw.Text(g, Failed ? "无法读取任务配置" : "还没有 API key", F.Section, new Rectangle(pad, center - S(16), Width - pad * 2, S(26)), Failed ? P.Danger : P.Ink,
                Draw.Single | TextFormatFlags.HorizontalCenter);
            Draw.Text(g, Failed ? "原文件已保留，请勿直接删除数据目录。" : "添加后，每个 key 的用量、状态和定时都会显示在这里。", F.Body,
                new Rectangle(pad * 2, center + S(12), Width - pad * 4, S(40)), P.Muted, Draw.Wrap | TextFormatFlags.HorizontalCenter);
            return;
        }
        g.DrawLine(line, 0, HeaderHeight, Width, HeaderHeight);
        g.DrawLine(line, 0, HeaderHeight + BarHeight, Width, HeaderHeight + BarHeight);
        var textLeft = _selectAll.Right + S(10);
        var textRight = BatchRun.Left - S(8);
        if (textRight - textLeft > S(30))
            Draw.Text(g, _selectionText, F.Caption, new Rectangle(textLeft, HeaderHeight, textRight - textLeft, BarHeight), P.Muted);
    }

    public override Color BackColor { get => Theme.Current.Surface; set { } }
}
