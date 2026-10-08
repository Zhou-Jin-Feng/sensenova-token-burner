using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Desktop.Ui;

/// <summary>主题化对话框：标题栏跟随主题，内容可滚动，底部按钮区。</summary>
internal class DialogForm : Form
{
    private readonly ScrollHost _scroll = new();
    private readonly DialogFooter _footer = new();
    private readonly int _logicalWidth;

    protected DialogForm(string caption, int logicalWidth = 520)
    {
        _logicalWidth = logicalWidth;
        Text = caption;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Font = Theme.Fonts.Body;
        BackColor = Theme.Current.Surface;
        Controls.Add(_scroll);
        Controls.Add(_footer);
    }

    /// <summary>测试/截图用：显示时不抢前台焦点。</summary>
    internal bool ShowInBackground { get; set; }
    protected override bool ShowWithoutActivation => ShowInBackground;

    protected void SetBody(Control body) { _scroll.Content = body; }
    protected UiButton AddButton(string text, ButtonKind kind, DialogResult result, string? name = null)
    {
        var button = new UiButton(text, kind, null, name);
        if (result != DialogResult.None) button.Click += (_, _) => { DialogResult = result; Close(); };
        _footer.Buttons.Insert(0, button);
        _footer.Controls.Add(button);
        return button;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToContent();
        // 默认焦点放在“取消”：长按或连按 Enter 不会直接确认付费运行、重置或删除。
        (_footer.Buttons.FirstOrDefault(button => button.Name == "Cancel")
            ?? _footer.Buttons.LastOrDefault(button => button.Kind is ButtonKind.Primary or ButtonKind.Danger))?.Focus();
    }

    /// <summary>按内容高度重算窗口大小（内容变化后调用），超出屏幕时内容区滚动。</summary>
    protected void FitToContent()
    {
        var width = Theme.S(_logicalWidth);
        var bodyWidth = width - Theme.S(48);
        var bodyHeight = _scroll.Content is IHeightForWidth measured ? measured.HeightForWidth(bodyWidth) : Theme.S(200);
        var screen = Screen.FromControl(Owner ?? this).WorkingArea;
        var footer = Theme.S(64);
        var height = Math.Min(bodyHeight + Theme.S(48) + footer, (int)(screen.Height * 0.86));
        ClientSize = new Size(width, height);
        PerformLayout();
        if (ShowInBackground || (Owner is MultiTaskForm { ShowInBackground: true }))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
        }
        else
        {
            CenterToScreen();
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var footer = Theme.S(64);
        _footer.SetBounds(0, ClientSize.Height - footer, ClientSize.Width, footer);
        _scroll.SetBounds(Theme.S(24), Theme.S(24), ClientSize.Width - Theme.S(30), Math.Max(0, ClientSize.Height - footer - Theme.S(30)));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
    }

    private sealed class DialogFooter : UiControl
    {
        public List<UiButton> Buttons { get; } = [];
        public override Color BackColor { get => Theme.Current.SurfaceAlt; set { } }
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var x = Width - S(24);
            foreach (var button in Buttons.AsEnumerable().Reverse())
            {
                var size = button.GetPreferredSize(Size.Empty);
                size.Width = Math.Max(size.Width, S(88));
                x -= size.Width;
                button.SetBounds(x, (Height - size.Height) / 2, size.Width, size.Height);
                x -= S(8);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using var pen = new Pen(P.Line);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }
}

/// <summary>纵向堆叠内容：标题、正文、事实表、提示框、任意控件。</summary>
internal sealed class StackBody : UiControl, IHeightForWidth
{
    private readonly List<(Control Control, int GapAfter, int? MaxWidth)> _items = [];
    public override Color BackColor { get => Theme.Current.Surface; set { } }
    public T Add<T>(T control, int gapAfter = 12, int? maxWidth = null) where T : Control
    {
        _items.Add((control, gapAfter, maxWidth)); Controls.Add(control); return control;
    }
    public int HeightForWidth(int width) => Arrange(width, false);
    protected override void OnLayout(LayoutEventArgs levent) { base.OnLayout(levent); Arrange(Width, true); }
    private int Arrange(int width, bool apply)
    {
        var y = 0;
        foreach (var (control, gap, maxWidth) in _items)
        {
            if (!control.Visible && apply) continue;
            var height = control is IHeightForWidth measured ? measured.HeightForWidth(width)
                : control is UiInput input ? input.PreferredHeight
                : control is UiNumber number ? number.PreferredHeight
                : control is UiSegmented segmented ? segmented.PreferredHeight
                : control.GetPreferredSize(new Size(width, 0)).Height;
            var controlWidth = control is UiToggle or UiButton ? Math.Min(width, control.GetPreferredSize(Size.Empty).Width)
                : maxWidth is { } limit ? Math.Min(width, S(limit)) : width;
            if (apply) control.SetBounds(0, y, controlWidth, height);
            y += height + S(gap);
        }
        return y;
    }
}

/// <summary>两列事实表（左标签、右内容），放在浅底圆角框内。</summary>
internal sealed class FactTable : UiControl, IHeightForWidth
{
    private readonly IReadOnlyList<KeyValuePair<string, string>> _facts;
    public FactTable(IReadOnlyList<KeyValuePair<string, string>> facts) { _facts = facts; }
    private int LabelWidth => S(96);
    private int RowHeight(string value, int width) => Math.Max(S(34), Draw.Measure(value, F.Body, width - LabelWidth - S(32), true).Height + S(16));
    public int HeightForWidth(int width) => _facts.Sum(fact => RowHeight(fact.Value, width)) + S(8);
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Draw.Panel(g, ClientRectangle, Theme.Sf(8), Parent?.BackColor ?? P.Surface, P.SurfaceAlt, P.Line);
        var y = S(4);
        using var line = new Pen(P.Line);
        for (var i = 0; i < _facts.Count; i++)
        {
            var (label, value) = (_facts[i].Key, _facts[i].Value);
            var height = RowHeight(value, Width);
            Draw.Text(g, label, F.Caption, new Rectangle(S(14), y + S(8), LabelWidth - S(14), S(20)), P.Muted, Draw.Single | TextFormatFlags.Top);
            Draw.Text(g, value, F.Body, new Rectangle(LabelWidth + S(8), y + S(8), Width - LabelWidth - S(22), height - S(12)), P.Ink, Draw.Wrap);
            if (i < _facts.Count - 1) g.DrawLine(line, S(14), y + height, Width - S(14), y + height);
            y += height;
        }
    }
}

internal sealed record ConfirmSpec(string Caption, string Title, string? Lead, IReadOnlyList<KeyValuePair<string, string>> Facts,
    string? SecondaryTitle, IReadOnlyList<KeyValuePair<string, string>> SecondaryFacts, IReadOnlyList<(NoticeLevel Level, string Text)> Notes,
    string ConfirmText, bool Danger);

internal sealed class ConfirmDialog : DialogForm
{
    public ConfirmDialog(ConfirmSpec spec) : base(spec.Caption, 540)
    {
        var body = new StackBody();
        body.Add(new TextBlock(() => Theme.Fonts.Title, () => Theme.Current.Ink, spec.Title), 8);
        if (!string.IsNullOrEmpty(spec.Lead)) body.Add(new TextBlock(() => Theme.Fonts.Body, () => Theme.Current.Muted, spec.Lead), 14);
        if (spec.Facts.Count > 0) body.Add(new FactTable(spec.Facts), 16);
        if (spec.SecondaryFacts.Count > 0)
        {
            if (spec.SecondaryTitle is not null) body.Add(new TextBlock(() => Theme.Fonts.BodyBold, () => Theme.Current.Ink, spec.SecondaryTitle), 8);
            body.Add(new FactTable(spec.SecondaryFacts), 16);
        }
        foreach (var (level, text) in spec.Notes) body.Add(new UiCallout { Level = level, Text = text }, 10);
        SetBody(body);
        AddButton(spec.ConfirmText, spec.Danger ? ButtonKind.Danger : ButtonKind.Primary, DialogResult.OK, "Confirm");
        AddButton("取消", ButtonKind.Secondary, DialogResult.Cancel, "Cancel");
        AcceptButton = null;
    }

    public static ConfirmSpec ForRun(RunPreview preview) => new("确认运行", preview.Title,
        "开始前请确认目标和单笔请求规模。确认后才会联网发送请求。", preview.Facts, "每笔请求", preview.RequestFacts,
        preview.Risks.Select((risk, index) => (index == 0 && risk.Contains("超过", StringComparison.Ordinal) ? NoticeLevel.Warning : NoticeLevel.Info, risk)).ToList(),
        "确认运行", false);
}

internal enum DirtyChoice { Save, Discard, Cancel }

internal sealed class ChoiceDialog : DialogForm
{
    public ChoiceDialog(string caption, string title, string text, string primary, string secondary) : base(caption, 460)
    {
        var body = new StackBody();
        body.Add(new TextBlock(() => Theme.Fonts.Section, () => Theme.Current.Ink, title), 8);
        body.Add(new TextBlock(() => Theme.Fonts.Body, () => Theme.Current.Muted, text), 4);
        SetBody(body);
        AddButton(primary, ButtonKind.Primary, DialogResult.Yes, "ChoicePrimary");
        AddButton(secondary, ButtonKind.Secondary, DialogResult.No, "ChoiceSecondary");
        AddButton("取消", ButtonKind.Ghost, DialogResult.Cancel, "Cancel");
    }

    public static DirtyChoice AskDirty(IWin32Window owner, string taskName)
    {
        using var dialog = new ChoiceDialog("未保存的修改", $"“{taskName}”有未保存的目标设置",
            "保存后再继续，或放弃这些修改。运行和定时始终使用已保存的设置。", "保存并继续", "放弃修改");
        return dialog.ShowDialog(owner) switch { DialogResult.Yes => DirtyChoice.Save, DialogResult.No => DirtyChoice.Discard, _ => DirtyChoice.Cancel };
    }
}

internal sealed record AddKeyRequest(string Key, string Name, string Group);

/// <summary>添加 key：只有 key 必填；提交失败时在对话框内显示原因，不关闭。</summary>
internal sealed class AddKeyDialog : DialogForm
{
    private readonly UiInput _key = new("粘贴 SenseNova API key", password: true, maxLength: 8192, accessibleName: "API key") { Name = "NewKey" };
    private readonly UiInput _name = new("例如 key-1（选填，不填自动生成）", maxLength: 80, accessibleName: "名称") { Name = "NewName" };
    private readonly UiInput _group = new("例如 账户A（选填）", maxLength: 80, accessibleName: "额度组") { Name = "NewGroup" };
    private readonly UiCallout _error = new() { Level = NoticeLevel.Error, Visible = false };
    private readonly Func<AddKeyRequest, Task<string?>> _submit;
    private readonly UiButton _save;

    public AddKeyDialog(Func<AddKeyRequest, Task<string?>> submit) : base("添加 API key", 500)
    {
        _submit = submit;
        var body = new StackBody();
        body.Add(new TextBlock(() => Theme.Fonts.Title, () => Theme.Current.Ink, "添加 API key"), 6);
        body.Add(new TextBlock(() => Theme.Fonts.Body, () => Theme.Current.Muted,
            "key 按当前 Windows 用户加密保存在本机，界面和日志都不显示明文。保存后不会自动开始运行。"), 18);
        body.Add(Label("API key"), 6); body.Add(_key, 14);
        body.Add(Label("名称"), 6); body.Add(_name, 14);
        body.Add(Label("额度组（同一账户的多个 key 填相同名称）"), 6); body.Add(_group, 14);
        body.Add(_error, 4);
        SetBody(body);
        _save = AddButton("加密保存", ButtonKind.Primary, DialogResult.None, "SaveKey");
        AddButton("取消", ButtonKind.Secondary, DialogResult.Cancel, "Cancel");
        _save.Click += async (_, _) => await SubmitAsync();
        _key.Box.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SubmitAsync(); } };
        Shown += (_, _) => _key.Box.Focus();
    }

    private static TextBlock Label(string text) => new(() => Theme.Fonts.Caption, () => Theme.Current.Muted, text);

    private async Task SubmitAsync()
    {
        if (!_save.Enabled) return;
        if (string.IsNullOrWhiteSpace(_key.Text)) { ShowError("请填写 API key。"); return; }
        _save.Enabled = false;
        try
        {
            var error = await _submit(new(_key.Text.Trim(), _name.Text.Trim(), _group.Text.Trim()));
            if (error is null) { DialogResult = DialogResult.OK; Close(); }
            else ShowError(error);
        }
        finally { if (!IsDisposed) _save.Enabled = true; }
    }

    private void ShowError(string text)
    {
        _error.Text = text; _error.Visible = true;
        ((Control?)_error.Parent)?.PerformLayout();
        FitToContent();
    }
}

internal sealed record SettingsModel(ThemeMode Theme, int Shared, int PerKey, bool CanChangeConcurrency, string StoragePath, string Version, string ServiceUrl, string Model, bool Mock);

/// <summary>设置：外观、并发、数据位置、关于。主题即时生效；并发需保存且要求任务都已停止。</summary>
internal sealed class SettingsDialog : DialogForm
{
    private readonly UiSegmented _theme = new(SegmentedStyle.Pills, "跟随系统", "浅色", "深色") { Name = "ThemeMode" };
    private readonly UiNumber _shared;
    private readonly UiNumber _perKey;
    private readonly UiButton _saveConcurrency = new("保存并发设置", ButtonKind.Primary, null, "SaveConcurrency");
    private readonly UiCallout _concurrencyNote = new();
    private readonly SettingsModel _model;

    public SettingsDialog(SettingsModel model, Action<ThemeMode> applyTheme, Func<int, int, Task<string?>> saveConcurrency) : base("设置", 540)
    {
        _model = model;
        _shared = new UiNumber(1, 10, model.Shared, 0, 1, "笔", "全应用同时请求上限") { Name = "SharedConcurrency" };
        _perKey = new UiNumber(1, 10, model.PerKey, 0, 1, "笔", "单个 key 同时请求上限") { Name = "PerKeyConcurrency" };
        var body = new StackBody();
        body.Add(new TextBlock(() => Theme.Fonts.Title, () => Theme.Current.Ink, "设置"), 18);
        body.Add(Heading("外观"), 8);
        _theme.SetSelectedSilently(model.Theme switch { ThemeMode.Light => 1, ThemeMode.Dark => 2, _ => 0 });
        _theme.SelectedIndexChanged += (_, _) => applyTheme(_theme.SelectedIndex switch { 1 => ThemeMode.Light, 2 => ThemeMode.Dark, _ => ThemeMode.System });
        body.Add(_theme, 22);
        body.Add(Heading("并发"), 8);
        body.Add(Caption("全应用同时请求上限（1–10）：所有 key 共享。"), 6);
        body.Add(_shared, 12, maxWidth: 200);
        body.Add(Caption("单个 key 同时请求上限（1–10）：建议不超过 3，过高容易触发限流，限流后任务会停下等待核查。"), 6);
        body.Add(_perKey, 12, maxWidth: 200);
        _concurrencyNote.Level = model.CanChangeConcurrency ? NoticeLevel.Info : NoticeLevel.Warning;
        _concurrencyNote.Text = model.CanChangeConcurrency
            ? "多个 key 同时运行时轮流使用共享上限；等待许可的请求不占用预算。"
            : "有任务正在运行或启用了定时。请先暂停全部任务并关闭定时，再修改并发。";
        body.Add(_concurrencyNote, 10);
        body.Add(_saveConcurrency, 22);
        _shared.Enabled = _perKey.Enabled = _saveConcurrency.Enabled = model.CanChangeConcurrency;
        _saveConcurrency.Click += async (_, _) =>
        {
            _shared.Commit(); _perKey.Commit();
            _saveConcurrency.Enabled = false;
            var error = await saveConcurrency((int)_shared.Value, (int)_perKey.Value);
            if (IsDisposed) return;
            _saveConcurrency.Enabled = true;
            _concurrencyNote.Level = error is null ? NoticeLevel.Success : NoticeLevel.Error;
            _concurrencyNote.Text = error ?? $"已保存：全应用最多 {(int)_shared.Value} 笔，单个 key 最多 {(int)_perKey.Value} 笔。新设置从下一次运行开始生效。";
            ((Control?)_concurrencyNote.Parent)?.PerformLayout();
        };
        body.Add(Heading("数据位置"), 8);
        body.Add(new TextBlock(() => Theme.Fonts.Number, () => Theme.Current.Ink, model.StoragePath), 8);
        var open = new UiButton("打开文件夹", ButtonKind.Secondary, Glyphs.Folder, "OpenStorage") { Compact = true };
        open.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", model.StoragePath) { UseShellExecute = true });
        body.Add(open, 6);
        body.Add(Caption("API key 以 Windows 当前用户加密保存；配置和运行记录不含明文 key。"), 22);
        body.Add(Heading("关于"), 8);
        body.Add(new FactTable([
            new("版本", model.Version), new("服务地址", model.ServiceUrl), new("活动模型", model.Model),
            new("运行环境", model.Mock ? "模拟环境，不联网、不产生真实消耗" : "SenseNova 官方 API")]), 4);
        SetBody(body);
        AddButton("完成", ButtonKind.Primary, DialogResult.OK, "Done");
        Theme.Changed += Rechrome;
    }

    private static TextBlock Heading(string text) => new(() => Theme.Fonts.Section, () => Theme.Current.Ink, text);
    private static TextBlock Caption(string text) => new(() => Theme.Fonts.Caption, () => Theme.Current.Muted, text);

    private void Rechrome()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(Rechrome)); return; }
        BackColor = Theme.Current.Surface;
        Theme.ApplyWindowChrome(this);
        Invalidate(true);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= Rechrome;
        base.Dispose(disposing);
    }
}
