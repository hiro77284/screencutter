using System.ComponentModel;

namespace ScreenCutter;

public sealed class SettingsForm : Form
{
    private readonly HotkeyBox _hotkeyBox = new() { Width = 220 };
    private readonly BindingList<SizePreset> _presets;
    private readonly DataGridView _grid = new();
    private readonly ComboBox _activePreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _folderBox = new() { Width = 360 };
    private readonly TextBox _templateBox = new() { Width = 360 };
    private readonly Label _previewLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly NumericUpDown _nextSeq = new() { Minimum = 0, Maximum = 999_999_999, Width = 120 };
    private readonly CheckBox _resetDaily = new() { Text = "日付が変わったら連番を 1 に戻す", AutoSize = true };
    private readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly NumericUpDown _quality = new() { Minimum = 1, Maximum = 100, Width = 70 };
    private readonly CheckBox _clipboard = new() { Text = "クリップボードにもコピーする", AutoSize = true };
    private readonly CheckBox _notify = new() { Text = "保存時に通知を表示する", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "Windows の起動時に自動で起動する", AutoSize = true };

    public AppSettings Settings { get; }
    public bool StartWithWindows => _startup.Checked;

    public SettingsForm(AppSettings settings, bool startWithWindows)
    {
        Settings = settings;
        _presets = new BindingList<SizePreset>(settings.Presets.ToList());

        Text = "ScreenCutter の設定";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        using (Stream? s = typeof(SettingsForm).Assembly.GetManifestResourceStream("ScreenCutter.app.ico"))
            if (s != null) Icon = new Icon(s);

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
        };
        root.Controls.Add(BuildHotkeyGroup());
        root.Controls.Add(BuildPresetGroup());
        root.Controls.Add(BuildSaveGroup());
        root.Controls.Add(BuildOtherGroup());
        root.Controls.Add(BuildButtons());
        Controls.Add(root);

        // 現在値を反映
        _hotkeyBox.SetHotkey(settings.HotkeyModifiers, settings.HotkeyKey);
        RefreshActivePresetList(settings.ActivePresetIndex);
        _folderBox.Text = settings.SaveFolder;
        _templateBox.Text = settings.FileNameTemplate;
        _nextSeq.Value = Math.Clamp(settings.NextSequence, 0, 999_999_999);
        _resetDaily.Checked = settings.ResetSequenceDaily;
        _format.Items.AddRange(new object[] { "PNG", "JPG", "WebP" });
        _format.SelectedIndex = (int)settings.Format;
        _quality.Value = settings.Quality;
        _clipboard.Checked = settings.CopyToClipboard;
        _notify.Checked = settings.ShowNotification;
        _startup.Checked = startWithWindows;

        _templateBox.TextChanged += (_, _) => UpdatePreview();
        _nextSeq.ValueChanged += (_, _) => UpdatePreview();
        _format.SelectedIndexChanged += (_, _) => { UpdatePreview(); UpdateQualityEnabled(); };
        _activePreset.SelectedIndexChanged += (_, _) => UpdatePreview();
        _presets.ListChanged += (_, _) => RefreshActivePresetList(_activePreset.SelectedIndex);
        _grid.CellEndEdit += (_, _) => RefreshActivePresetList(_activePreset.SelectedIndex);
        UpdatePreview();
        UpdateQualityEnabled();
    }

    private static GroupBox Group(string title, Control content)
    {
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 10),
            Margin = new Padding(0, 0, 0, 10),
            MinimumSize = new Size(560, 0),
        };
        content.Dock = DockStyle.Fill;
        box.Controls.Add(content);
        return box;
    }

    private static TableLayoutPanel Table(int columns)
    {
        var t = new TableLayoutPanel { ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        for (int i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };

    private Control BuildHotkeyGroup()
    {
        var t = Table(2);
        t.Controls.Add(Caption("起動キー:"), 0, 0);
        t.Controls.Add(_hotkeyBox, 1, 0);

        var hint = Caption("ボックスを選んで、使いたいキーの組み合わせを押してください（例: Ctrl + Alt + S）\n" +
                           "起動後は F:自由サイズ  S:スケール  W:ウィンドウ でモードを切り替えられます");
        hint.ForeColor = SystemColors.GrayText;
        t.Controls.Add(hint, 0, 1);
        t.SetColumnSpan(hint, 2);
        return Group("ホットキー", t);
    }

    private Control BuildPresetGroup()
    {
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Size = new Size(420, 170);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名前", DataPropertyName = nameof(SizePreset.Name), FillWeight = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "幅 (px)", DataPropertyName = nameof(SizePreset.Width), FillWeight = 25 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "高さ (px)", DataPropertyName = nameof(SizePreset.Height), FillWeight = 25 });
        _grid.DataSource = _presets;
        _grid.DataError += (_, e) =>
        {
            MessageBox.Show(this, "幅と高さには 1 以上の整数を入力してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
        };
        _grid.CellValidating += (_, e) =>
        {
            if (e.ColumnIndex == 0) return;
            if (!int.TryParse(e.FormattedValue?.ToString(), out int v) || v < 1 || v > 20000)
            {
                MessageBox.Show(this, "幅と高さには 1〜20000 の整数を入力してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
            }
        };

        var add = new Button { Text = "追加", AutoSize = true };
        add.Click += (_, _) =>
        {
            _presets.Add(new SizePreset("新規", 800, 600));
            _grid.CurrentCell = _grid.Rows[^1].Cells[0];
            _grid.BeginEdit(true);
        };
        var remove = new Button { Text = "削除", AutoSize = true };
        remove.Click += (_, _) =>
        {
            if (_grid.CurrentRow == null) return;
            if (_presets.Count <= 1)
            {
                MessageBox.Show(this, "サイズは最低 1 つ必要です。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _presets.RemoveAt(_grid.CurrentRow.Index);
        };
        var up = new Button { Text = "↑", AutoSize = true };
        up.Click += (_, _) => MovePreset(-1);
        var down = new Button { Text = "↓", AutoSize = true };
        down.Click += (_, _) => MovePreset(1);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { add, remove, up, down });

        var t = Table(2);
        t.Controls.Add(_grid, 0, 0);
        t.Controls.Add(buttons, 1, 0);
        var activeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        activeRow.Controls.Add(Caption("現在有効なサイズ:"));
        activeRow.Controls.Add(_activePreset);
        t.Controls.Add(activeRow, 0, 1);
        t.SetColumnSpan(activeRow, 2);
        var hint = Caption("キャプチャ中もマウスホイールまたは 1〜9 キーでサイズを切り替えられます");
        hint.ForeColor = SystemColors.GrayText;
        t.Controls.Add(hint, 0, 2);
        t.SetColumnSpan(hint, 2);
        return Group("切り出しサイズ", t);
    }

    private void MovePreset(int delta)
    {
        if (_grid.CurrentRow == null) return;
        int from = _grid.CurrentRow.Index;
        int to = from + delta;
        if (to < 0 || to >= _presets.Count) return;
        SizePreset item = _presets[from];
        _presets.RemoveAt(from);
        _presets.Insert(to, item);
        _grid.CurrentCell = _grid.Rows[to].Cells[0];
    }

    private Control BuildSaveGroup()
    {
        var browse = new Button { Text = "参照...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _folderBox.Text, UseDescriptionForTitle = true, Description = "保存先フォルダを選択" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dlg.SelectedPath;
        };

        var formatRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        formatRow.Controls.Add(_format);
        formatRow.Controls.Add(Caption("    品質 (JPG/WebP):"));
        formatRow.Controls.Add(_quality);

        var seqRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        seqRow.Controls.Add(_nextSeq);
        seqRow.Controls.Add(_resetDaily);

        var help = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "使える記号:  {date} 日付 (20261027)   {time} 時刻 (153012)   {seq} 連番 / {seq:4} 4 桁\n" +
                   "                {date:yyyy-MM-dd_HHmmss} 書式指定   {w} 幅   {h} 高さ   {name} サイズ名",
            Margin = new Padding(0, 2, 0, 4),
        };

        var t = Table(3);
        t.Controls.Add(Caption("保存先フォルダ:"), 0, 0);
        t.Controls.Add(_folderBox, 1, 0);
        t.Controls.Add(browse, 2, 0);
        t.Controls.Add(Caption("ファイル名:"), 0, 1);
        t.Controls.Add(_templateBox, 1, 1);
        t.Controls.Add(help, 1, 2);
        t.SetColumnSpan(help, 2);
        t.Controls.Add(Caption("プレビュー:"), 0, 3);
        t.Controls.Add(_previewLabel, 1, 3);
        _previewLabel.Margin = new Padding(3, 6, 0, 0);
        t.SetColumnSpan(_previewLabel, 2);
        t.Controls.Add(Caption("次の連番:"), 0, 4);
        t.Controls.Add(seqRow, 1, 4);
        t.SetColumnSpan(seqRow, 2);
        t.Controls.Add(Caption("ファイル形式:"), 0, 5);
        t.Controls.Add(formatRow, 1, 5);
        t.SetColumnSpan(formatRow, 2);
        return Group("保存", t);
    }

    private Control BuildOtherGroup()
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        panel.Controls.AddRange(new Control[] { _clipboard, _notify, _startup });

        // 設定ファイルの場所（表示のみ。選択してコピーはできる）
        var pathCaption = Caption("設定ファイルの保存場所:");
        pathCaption.Margin = new Padding(0, 10, 0, 2);
        var pathBox = new TextBox
        {
            Text = AppSettings.SettingsPath,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            ForeColor = SystemColors.GrayText,
            Width = 530,
            TabStop = false,
        };
        panel.Controls.AddRange(new Control[] { pathCaption, pathBox });
        return Group("その他", panel);
    }

    private Control BuildButtons()
    {
        var ok = new Button { Text = "OK", Width = 90 };
        var cancel = new Button { Text = "キャンセル", Width = 90, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Apply();
        AcceptButton = ok;
        CancelButton = cancel;
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, MinimumSize = new Size(560, 0) };
        panel.Controls.Add(cancel);
        panel.Controls.Add(ok);
        return panel;
    }

    private void RefreshActivePresetList(int selected)
    {
        _activePreset.BeginUpdate();
        _activePreset.Items.Clear();
        foreach (SizePreset p in _presets) _activePreset.Items.Add(p.ToString());
        _activePreset.SelectedIndex = _presets.Count == 0 ? -1 : Math.Clamp(selected, 0, _presets.Count - 1);
        _activePreset.EndUpdate();
    }

    private void UpdatePreview()
    {
        SizePreset preset = _activePreset.SelectedIndex >= 0 && _activePreset.SelectedIndex < _presets.Count
            ? _presets[_activePreset.SelectedIndex]
            : new SizePreset("例", 1280, 720);
        string name = FileNameBuilder.Build(_templateBox.Text, DateTime.Now, (int)_nextSeq.Value, preset);
        _previewLabel.Text = name + ImageSaver.Extension((OutputFormat)Math.Max(0, _format.SelectedIndex));
    }

    private void UpdateQualityEnabled() => _quality.Enabled = _format.SelectedIndex != (int)OutputFormat.Png;

    private static bool IsBareKey(HotkeyBox box) =>
        box.Key != Keys.None && box.Modifiers == 0 &&
        box.Key is not (>= Keys.F1 and <= Keys.F24 or Keys.Snapshot or Keys.Pause or Keys.Scroll);

    private void Apply()
    {
        _grid.EndEdit();

        if (_hotkeyBox.Key == Keys.None)
        {
            MessageBox.Show(this, "起動キーを設定してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (IsBareKey(_hotkeyBox))
        {
            if (MessageBox.Show(this, "修飾キー（Ctrl / Alt / Shift / Win）なしのキーは普段の入力と衝突します。このまま設定しますか？",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
        }

        string folder = _folderBox.Text.Trim();
        try
        {
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) throw new ArgumentException();
            Directory.CreateDirectory(folder);
        }
        catch
        {
            MessageBox.Show(this, "保存先フォルダを作成できません。パスを確認してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_templateBox.Text))
        {
            MessageBox.Show(this, "ファイル名を入力してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Settings.HotkeyModifiers = _hotkeyBox.Modifiers;
        Settings.HotkeyKey = _hotkeyBox.Key;
        Settings.Presets = _presets.Where(p => p.Width > 0 && p.Height > 0).ToList();
        Settings.ActivePresetIndex = Math.Clamp(_activePreset.SelectedIndex, 0, Math.Max(0, Settings.Presets.Count - 1));
        Settings.SaveFolder = folder;
        Settings.FileNameTemplate = _templateBox.Text.Trim();
        if (Settings.NextSequence != (int)_nextSeq.Value)
        {
            Settings.NextSequence = (int)_nextSeq.Value;
            Settings.LastSequenceDate = DateTime.Now.ToString("yyyy-MM-dd"); // 手動で変えた値を当日中は維持
        }
        Settings.ResetSequenceDaily = _resetDaily.Checked;
        Settings.Format = (OutputFormat)_format.SelectedIndex;
        Settings.Quality = (int)_quality.Value;
        Settings.CopyToClipboard = _clipboard.Checked;
        Settings.ShowNotification = _notify.Checked;

        DialogResult = DialogResult.OK;
        Close();
    }
}
