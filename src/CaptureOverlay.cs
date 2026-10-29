using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenCutter;

public enum CaptureMode
{
    /// <summary>登録サイズの枠がマウスに追従する（起動時のモード）。</summary>
    Fixed,
    /// <summary>ドラッグで範囲を囲む。F キーで切替。</summary>
    Free,
    /// <summary>枠の周りに定規を出す。S キーで切替、↑↓ で高さ・←→ で幅を 100px ずつ増減。</summary>
    Scale,
    /// <summary>マウスの下のウィンドウの描画ペイン。W キー 1 回目。</summary>
    WindowClient,
    /// <summary>マウスの下のウィンドウ全体。W キー 2 回目。</summary>
    WindowFull,
    /// <summary>マウスがあるディスプレイ全体。W キー 3 回目。</summary>
    Monitor,
}

/// <summary>
/// 全画面を静止画として撮ってから覆い、範囲を選ばせるオーバーレイ。
/// F / S / W キーでモードを切り替え、左クリックで確定、Esc / 右クリックで中止。
/// </summary>
public sealed class CaptureOverlay : Form
{
    private const int WmDpiChanged = 0x02E0;
    private const int RulerSize = 30;
    private const int ScaleStep = 100;
    private const int ScaleInitial = 500;

    private readonly IReadOnlyList<SizePreset> _presets;
    private readonly Rectangle _virtualScreen;
    private readonly Bitmap _snapshot;
    private readonly Bitmap _dimmed;
    private readonly List<WindowInfo> _windows;
    private readonly Font _labelFont;
    private readonly Font _rulerFont;
    private readonly Pen _borderPen = new(Color.FromArgb(255, 60, 60), 2);
    private readonly Pen _crosshairPen = new(Color.FromArgb(200, 255, 60, 60), 1);
    private readonly HashSet<Keys> _heldAtStart = new();

    private CaptureMode _mode = CaptureMode.Fixed;
    private int _scaleWidth = ScaleInitial;
    private int _scaleHeight = ScaleInitial;
    // 自由サイズモードでドラッグを始めた位置（ドラッグしていなければ null）
    private Point? _anchor;
    // 固定サイズモードで ↑↓ を押したときに出すサイズ一覧のカーソル位置（一覧を閉じていれば null）
    private int? _listIndex;
    private Rectangle _selection;
    private Point _center;

    public int PresetIndex { get; private set; }

    /// <summary>保存形式。キャプチャ中に Tab で PNG → JPG → WebP と切り替えられる。</summary>
    public OutputFormat Format { get; private set; }
    /// <summary>JPG / WebP の品質。WebP のときは Ctrl+↑↓ で 10 ずつ変えられる。</summary>
    public int Quality { get; private set; }
    public Bitmap? Result { get; private set; }

    /// <summary>ファイル名の {name} に使う名前。</summary>
    public string ResultName => _mode switch
    {
        CaptureMode.Free => "自由",
        CaptureMode.Scale => "スケール",
        CaptureMode.WindowClient => "ウィンドウ内",
        CaptureMode.WindowFull => "ウィンドウ",
        CaptureMode.Monitor => "画面",
        _ => _presets[PresetIndex].Name,
    };

    public CaptureOverlay(IReadOnlyList<SizePreset> presets, int presetIndex, OutputFormat format, int quality)
    {
        _presets = presets;
        Format = format;
        Quality = quality;
        PresetIndex = Math.Clamp(presetIndex, 0, presets.Count - 1);
        _virtualScreen = SystemInformation.VirtualScreen;

        // オーバーレイを出す前の状態を記録する（画面もウィンドウ位置もこの瞬間で固定）
        _windows = WindowFinder.EnumerateVisibleWindows();
        _snapshot = new Bitmap(_virtualScreen.Width, _virtualScreen.Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(_snapshot))
            g.CopyFromScreen(_virtualScreen.Location, Point.Empty, _virtualScreen.Size, CopyPixelOperation.SourceCopy);

        _dimmed = new Bitmap(_snapshot);
        using (Graphics g = Graphics.FromImage(_dimmed))
        using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillRectangle(shade, 0, 0, _dimmed.Width, _dimmed.Height);

        _labelFont = new Font("Yu Gothic UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
        _rulerFont = new Font("Segoe UI", 7f, FontStyle.Regular, GraphicsUnit.Point);

        // 起動ホットキー（Ctrl+Alt+S）の S を押しっぱなしのまま表示されたとき、
        // そのキーリピートでモードが切り替わらないよう、離されるまで無視する
        foreach (Keys k in new[] { Keys.F, Keys.S, Keys.W })
        {
            if ((GetAsyncKeyState((int)k) & 0x8000) != 0)
                _heldAtStart.Add(k);
        }

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        Bounds = _virtualScreen;
        Text = "ScreenCutter";

        _center = ToClient(Cursor.Position);
        _selection = ComputeSelection();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: Alt+Tab に出さない
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _virtualScreen;
        Activate();
        SetForegroundWindow(Handle);
        Focus();
    }

    protected override void WndProc(ref Message m)
    {
        // 複数モニタにまたがる全画面ウィンドウなので、DPI 変更による自動リサイズは無視する
        if (m.Msg == WmDpiChanged)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    private Point ToClient(Point screen) => new(screen.X - _virtualScreen.X, screen.Y - _virtualScreen.Y);

    private Point ToScreen(Point client) => new(client.X + _virtualScreen.X, client.Y + _virtualScreen.Y);

    private Rectangle ToClient(Rectangle screen)
    {
        screen.Offset(-_virtualScreen.X, -_virtualScreen.Y);
        return Rectangle.Intersect(screen, new Rectangle(Point.Empty, _virtualScreen.Size));
    }

    // ---- 範囲の計算 ----

    private Rectangle ComputeSelection()
    {
        switch (_mode)
        {
            case CaptureMode.Free:
            {
                if (_anchor is not Point a) return Rectangle.Empty;
                // 押した点と今の点の両方を含む矩形（同じ点なら 1×1）
                int left = Math.Min(a.X, _center.X), top = Math.Min(a.Y, _center.Y);
                int right = Math.Max(a.X, _center.X) + 1, bottom = Math.Max(a.Y, _center.Y) + 1;
                return Rectangle.Intersect(Rectangle.FromLTRB(left, top, right, bottom),
                    new Rectangle(Point.Empty, _virtualScreen.Size));
            }
            case CaptureMode.Scale:
                return CenteredRect(_scaleWidth, _scaleHeight, RulerSize + 2);
            case CaptureMode.WindowClient:
            case CaptureMode.WindowFull:
            {
                WindowInfo? w = WindowFinder.FindAt(_windows, ToScreen(_center));
                if (w is not WindowInfo win) return Rectangle.Empty;
                return ToClient(_mode == CaptureMode.WindowClient ? win.Client : win.Bounds);
            }
            case CaptureMode.Monitor:
                return ToClient(Screen.FromPoint(ToScreen(_center)).Bounds);
            default:
            {
                SizePreset preset = _presets[PresetIndex];
                return CenteredRect(preset.Width, preset.Height, 0);
            }
        }
    }

    /// <summary>カーソルを中心にした枠。画面からはみ出さないよう寄せる（margin は定規などの外側の余白）。</summary>
    private Rectangle CenteredRect(int width, int height, int margin)
    {
        int vw = _virtualScreen.Width, vh = _virtualScreen.Height;
        int w = Math.Min(width, vw);
        int h = Math.Min(height, vh);
        int mx = vw - w >= margin * 2 ? margin : 0;
        int my = vh - h >= margin * 2 ? margin : 0;
        int x = Math.Clamp(_center.X - w / 2, mx, vw - w - mx);
        int y = Math.Clamp(_center.Y - h / 2, my, vh - h - my);
        return new Rectangle(x, y, w, h);
    }

    private bool ShowCrosshair => _mode == CaptureMode.Free && _anchor == null;

    private void MoveTo(Point p)
    {
        if (p == _center) return;
        Rectangle oldDirty = DirtyArea(_selection);
        Point oldCenter = _center;
        bool oldCrosshair = ShowCrosshair;

        _center = p;
        Rectangle newSel = ComputeSelection();
        // ウィンドウモードなどで範囲が変わらないときは描き直さない（ラベルが枠に付いているので見た目も同じ）
        if (newSel == _selection && !newSel.IsEmpty && !ShowCrosshair)
            return;

        _selection = newSel;
        Invalidate(oldDirty);
        Invalidate(DirtyArea(_selection));
        if (oldCrosshair) InvalidateCrosshair(oldCenter);
        if (ShowCrosshair) InvalidateCrosshair(_center);
    }

    /// <summary>モードやサイズを変えたときは全体を描き直す。</summary>
    private void Recompute()
    {
        _selection = ComputeSelection();
        Invalidate();
    }

    private void InvalidateCrosshair(Point c)
    {
        Invalidate(new Rectangle(c.X - 1, 0, 3, ClientSize.Height));
        Invalidate(new Rectangle(0, c.Y - 1, ClientSize.Width, 3));
    }

    /// <summary>枠・定規・ラベルを含む、描き直しが必要な範囲。</summary>
    private Rectangle DirtyArea(Rectangle sel)
    {
        // Rectangle.Union は空の矩形でも原点を含めてしまうので分けて扱う
        Rectangle label = LabelBounds(sel);
        Rectangle area = sel.IsEmpty ? label : Rectangle.Union(Decorated(sel), label);
        if (_listIndex != null && !sel.IsEmpty)
            area = Rectangle.Union(area, ListBounds(sel));
        area.Inflate(4, 4);
        return area;
    }

    /// <summary>枠線と（スケールモードなら）定規を含めた外形。</summary>
    private Rectangle Decorated(Rectangle sel) =>
        Rectangle.Inflate(sel, _mode == CaptureMode.Scale ? RulerSize + 2 : 2, _mode == CaptureMode.Scale ? RulerSize + 2 : 2);

    // ---- ラベル ----

    private string LabelText()
    {
        string sizeText = $"{_selection.Width} × {_selection.Height}";
        string line1 = _mode switch
        {
            CaptureMode.Free => _selection.IsEmpty
                ? "[自由サイズ] ドラッグで範囲を囲む"
                : $"[自由サイズ] {sizeText}    離すと保存",
            CaptureMode.Scale => $"[スケール] {sizeText}    クリック:保存  ↑↓:高さ  ←→:幅",
            CaptureMode.WindowClient => _selection.IsEmpty
                ? "[ウィンドウ: 描画ペイン] ウィンドウにマウスを載せる"
                : $"[ウィンドウ: 描画ペイン] {sizeText}    クリック:保存",
            CaptureMode.WindowFull => _selection.IsEmpty
                ? "[ウィンドウ: 全体] ウィンドウにマウスを載せる"
                : $"[ウィンドウ: 全体] {sizeText}    クリック:保存",
            CaptureMode.Monitor => $"[画面全体] {sizeText}    クリック:保存",
            _ => FixedLabel(),
        };
        return line1 + $"\n保存形式: {FormatText()}  Tab:形式切替    F:自由サイズ  S:スケール  W:ウィンドウ（押すごとに切替）  Esc/右クリック:中止";
    }

    private string FormatText() => Format switch
    {
        OutputFormat.Jpg => $"JPG（品質 {Quality}）",
        OutputFormat.Webp => $"WebP（品質 {Quality}  Ctrl+↑↓:品質）",
        _ => "PNG",
    };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Tab はフォーカス移動に使われて KeyDown まで届かないので、ここで受け取る
        if (keyData == Keys.Tab)
        {
            Format = Format switch
            {
                OutputFormat.Png => OutputFormat.Jpg,
                OutputFormat.Jpg => OutputFormat.Webp,
                _ => OutputFormat.Png,
            };
            Invalidate();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private string FixedLabel()
    {
        SizePreset p = _presets[PresetIndex];
        string size = _selection.Width == p.Width && _selection.Height == p.Height
            ? $"{p.Width} × {p.Height}"
            : $"{_selection.Width} × {_selection.Height}（画面に合わせて縮小）";
        return _listIndex != null
            ? $"[固定] {p.Name}  {size}    ↑↓:選ぶ  Enter:決定  Esc:一覧を閉じる"
            : $"[固定] {p.Name}  {size}    クリック:保存  ↑↓:サイズ一覧  ホイール/1-9:サイズ切替";
    }

    private Rectangle LabelBounds(Rectangle sel)
    {
        Size textSize = TextRenderer.MeasureText(LabelText(), _labelFont);
        int w = textSize.Width + 16;
        int h = textSize.Height + 8;
        if (sel.IsEmpty)
        {
            // 範囲がないときはカーソルの右下に出す
            return new Rectangle(
                Math.Clamp(_center.X + 16, 0, Math.Max(0, ClientSize.Width - w)),
                Math.Clamp(_center.Y + 20, 0, Math.Max(0, ClientSize.Height - h)), w, h);
        }

        Rectangle outer = Decorated(sel);
        int x = Math.Clamp(outer.X, 0, Math.Max(0, ClientSize.Width - w));
        int y = outer.Y - h - 2;
        if (y < 0) y = outer.Bottom + 2;
        if (y + h > ClientSize.Height) y = Math.Max(0, sel.Y + 4); // 上下に余白がなければ枠の内側に出す
        if (_mode == CaptureMode.Scale && y >= sel.Y && y < sel.Bottom) y = sel.Y + 4;
        return new Rectangle(x, y, w, h);
    }

    // ---- 描画 ----

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        Rectangle clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_dimmed, clip, clip, GraphicsUnit.Pixel);

        Rectangle bright = _selection.IsEmpty ? Rectangle.Empty : Rectangle.Intersect(clip, _selection);
        if (!bright.IsEmpty)
            g.DrawImage(_snapshot, bright, bright, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        g.PixelOffsetMode = PixelOffsetMode.Default;

        if (!_selection.IsEmpty)
        {
            if (_mode == CaptureMode.Scale)
                DrawRulers(g, _selection);
            // 画面いっぱいの範囲でも枠線が見えるよう、内側に描く
            Rectangle border = Rectangle.Intersect(Rectangle.Inflate(_selection, 1, 1), ClientRectangle);
            border.Width -= 1;
            border.Height -= 1;
            g.DrawRectangle(_borderPen, border);
        }

        if (ShowCrosshair)
        {
            g.DrawLine(_crosshairPen, _center.X, 0, _center.X, ClientSize.Height);
            g.DrawLine(_crosshairPen, 0, _center.Y, ClientSize.Width, _center.Y);
        }

        if (_listIndex != null && !_selection.IsEmpty)
            DrawPresetList(g, _selection);

        Rectangle label = LabelBounds(_selection);
        using (var bg = new SolidBrush(Color.FromArgb(220, 20, 20, 20)))
            g.FillRectangle(bg, label);
        TextRenderer.DrawText(g, LabelText(), _labelFont, label, Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }

    // ---- サイズ一覧 ----

    private const int ListPadding = 8;

    private string ListRowText(int i)
    {
        SizePreset p = _presets[i];
        string current = i == PresetIndex ? "  ← 現在" : "";
        return $"{(i < 9 ? (i + 1).ToString() : " ")}.  {p.Name}   {p.Width} × {p.Height}{current}";
    }

    private int ListRowHeight => TextRenderer.MeasureText("Ag", _labelFont).Height + 6;

    /// <summary>一覧は枠の左上の内側に出す（画面からはみ出さないよう寄せる）。</summary>
    private Rectangle ListBounds(Rectangle sel)
    {
        int width = 0;
        for (int i = 0; i < _presets.Count; i++)
            width = Math.Max(width, TextRenderer.MeasureText(ListRowText(i), _labelFont).Width);
        width += 32 + ListPadding * 2;
        int height = ListRowHeight * _presets.Count + ListPadding * 2;
        int x = Math.Clamp(sel.X + ListPadding, 0, Math.Max(0, ClientSize.Width - width));
        int y = Math.Clamp(sel.Y + ListPadding, 0, Math.Max(0, ClientSize.Height - height));
        return new Rectangle(x, y, width, height);
    }

    private void DrawPresetList(Graphics g, Rectangle sel)
    {
        Rectangle box = ListBounds(sel);
        using (var bg = new SolidBrush(Color.FromArgb(235, 25, 25, 25)))
            g.FillRectangle(bg, box);
        using (var edge = new Pen(Color.FromArgb(255, 120, 120, 120)))
            g.DrawRectangle(edge, box.X, box.Y, box.Width - 1, box.Height - 1);

        int rowH = ListRowHeight;
        using var highlight = new SolidBrush(Color.FromArgb(255, 30, 110, 220));
        for (int i = 0; i < _presets.Count; i++)
        {
            var row = new Rectangle(box.X + ListPadding, box.Y + ListPadding + i * rowH, box.Width - ListPadding * 2, rowH);
            if (i == _listIndex) g.FillRectangle(highlight, row);
            Color fore = i == PresetIndex ? Color.FromArgb(255, 255, 220, 90) : Color.White;
            string mark = i == _listIndex ? "▶" : "";
            TextRenderer.DrawText(g, mark, _labelFont, new Rectangle(row.X, row.Y, 24, row.Height), Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, ListRowText(i), _labelFont, new Rectangle(row.X + 24, row.Y, row.Width - 24, row.Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }
    }

    private void MoveListCursor(int delta)
    {
        // 1 回目は一覧を開くだけ（現在のサイズにカーソルを置く）
        _listIndex = _listIndex is int i
            ? (i + delta + _presets.Count) % _presets.Count
            : PresetIndex;
        Invalidate();
    }

    private void CloseList()
    {
        if (_listIndex == null) return;
        _listIndex = null;
        Invalidate();
    }

    /// <summary>枠の上下左右の外側に、枠の角を 0 としたピクセル目盛りの定規を描く。</summary>
    private void DrawRulers(Graphics g, Rectangle sel)
    {
        const int gap = 2; // 枠線の分
        const int r = RulerSize;
        Rectangle top = new(sel.X, sel.Y - gap - r, sel.Width, r);
        Rectangle bottom = new(sel.X, sel.Bottom + gap, sel.Width, r);
        Rectangle left = new(sel.X - gap - r, sel.Y, r, sel.Height);
        Rectangle right = new(sel.Right + gap, sel.Y, r, sel.Height);

        using var back = new SolidBrush(Color.FromArgb(255, 252, 238, 170));
        using var corner = new SolidBrush(Color.FromArgb(255, 225, 205, 120));
        using var tick = new Pen(Color.Black, 1);
        using var edge = new Pen(Color.FromArgb(255, 90, 80, 40), 1);

        foreach (Rectangle rr in new[] { top, bottom, left, right })
        {
            g.FillRectangle(back, rr);
            g.DrawRectangle(edge, rr.X, rr.Y, rr.Width - 1, rr.Height - 1);
        }
        // 四隅のすき間を埋める
        g.FillRectangle(corner, sel.X - gap - r, sel.Y - gap - r, r + gap, r + gap);
        g.FillRectangle(corner, sel.Right, sel.Y - gap - r, r + gap, r + gap);
        g.FillRectangle(corner, sel.X - gap - r, sel.Bottom, r + gap, r + gap);
        g.FillRectangle(corner, sel.Right, sel.Bottom, r + gap, r + gap);

        var textFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        // 横方向（上下の定規）
        for (int i = 0; i <= sel.Width; i += 10)
        {
            int len = TickLength(i);
            int x = Math.Min(sel.X + i, sel.Right - 1);
            g.DrawLine(tick, x, top.Bottom - 1, x, top.Bottom - 1 - len);
            g.DrawLine(tick, x, bottom.Y, x, bottom.Y + len);
            if (i % 100 == 0 && i > 0 && i < sel.Width)
            {
                string t = i.ToString();
                TextRenderer.DrawText(g, t, _rulerFont, new Point(x + 2, top.Y + 1), Color.Black, textFlags);
                Size ts = TextRenderer.MeasureText(t, _rulerFont, Size.Empty, textFlags);
                TextRenderer.DrawText(g, t, _rulerFont, new Point(x + 2, bottom.Bottom - ts.Height - 1), Color.Black, textFlags);
            }
        }

        // 縦方向（左右の定規）
        for (int i = 0; i <= sel.Height; i += 10)
        {
            int len = TickLength(i);
            int y = Math.Min(sel.Y + i, sel.Bottom - 1);
            g.DrawLine(tick, left.Right - 1, y, left.Right - 1 - len, y);
            g.DrawLine(tick, right.X, y, right.X + len, y);
            if (i % 100 == 0 && i > 0 && i < sel.Height)
            {
                string t = i.ToString();
                TextRenderer.DrawText(g, t, _rulerFont, new Point(left.X + 1, y + 1), Color.Black, textFlags);
                Size ts = TextRenderer.MeasureText(t, _rulerFont, Size.Empty, textFlags);
                TextRenderer.DrawText(g, t, _rulerFont, new Point(right.Right - ts.Width - 1, y + 1), Color.Black, textFlags);
            }
        }

        // 端の寸法を角に表示
        string wText = sel.Width.ToString();
        string hText = sel.Height.ToString();
        TextRenderer.DrawText(g, wText, _rulerFont,
            new Rectangle(sel.Right, sel.Y - gap - r, r + gap, r + gap), Color.Black,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, hText, _rulerFont,
            new Rectangle(sel.X - gap - r, sel.Bottom, r + gap, r + gap), Color.Black,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static int TickLength(int i) => i % 100 == 0 ? RulerSize - 2 : i % 50 == 0 ? 12 : 6;

    // ---- 入力 ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MoveTo(e.Location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_mode == CaptureMode.Free && e.Button == MouseButtons.Left)
        {
            _center = e.Location;
            _anchor = e.Location;
            Recompute();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_mode != CaptureMode.Fixed) return;
        int step = e.Delta > 0 ? -1 : 1;
        SelectPreset((PresetIndex + step + _presets.Count) % _presets.Count);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            Cancel();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (_mode != CaptureMode.Free)
        {
            Commit();
        }
        else if (_anchor != null)
        {
            // ほぼ動かさずに離した場合は誤操作とみなして選び直し
            if (_selection.Width >= 3 && _selection.Height >= 3)
            {
                Commit();
            }
            else
            {
                _anchor = null;
                Recompute();
            }
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _heldAtStart.Remove(e.KeyCode);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool modeKeyAllowed = !e.Control && !e.Alt && !_heldAtStart.Contains(e.KeyCode);
        switch (e.KeyCode)
        {
            case Keys.Up when e.Control && Format == OutputFormat.Webp:
                ChangeQuality(10);
                break;
            case Keys.Down when e.Control && Format == OutputFormat.Webp:
                ChangeQuality(-10);
                break;
            case Keys.Up or Keys.Down when e.Control:
                break; // WebP 以外では Ctrl+↑↓ は何もしない
            case Keys.Escape when _listIndex != null:
                CloseList();
                break;
            case Keys.Escape:
                Cancel();
                break;
            case Keys.Enter when _listIndex is int chosen:
                // 一覧が開いているときの Enter はサイズの決定（保存はしない）
                _listIndex = null;
                PresetIndex = chosen;
                Recompute();
                break;
            case Keys.Enter:
            case Keys.Space:
                Commit();
                break;
            case Keys.Up when _mode == CaptureMode.Fixed:
                MoveListCursor(-1);
                break;
            case Keys.Down when _mode == CaptureMode.Fixed:
                MoveListCursor(1);
                break;
            case Keys.F when modeKeyAllowed:
                SetMode(_mode == CaptureMode.Free ? CaptureMode.Fixed : CaptureMode.Free);
                break;
            case Keys.S when modeKeyAllowed:
                if (_mode != CaptureMode.Scale) _scaleWidth = _scaleHeight = ScaleInitial;
                SetMode(_mode == CaptureMode.Scale ? CaptureMode.Fixed : CaptureMode.Scale);
                break;
            case Keys.W when modeKeyAllowed:
                SetMode(_mode switch
                {
                    CaptureMode.WindowClient => CaptureMode.WindowFull,
                    CaptureMode.WindowFull => CaptureMode.Monitor,
                    CaptureMode.Monitor => CaptureMode.Fixed,
                    _ => CaptureMode.WindowClient,
                });
                break;
            case >= Keys.D1 and <= Keys.D9 when _mode == CaptureMode.Fixed:
                SelectPreset(e.KeyCode - Keys.D1);
                break;
            case >= Keys.NumPad1 and <= Keys.NumPad9 when _mode == CaptureMode.Fixed:
                SelectPreset(e.KeyCode - Keys.NumPad1);
                break;
            case Keys.Up when _mode == CaptureMode.Scale:
                ResizeScale(0, ScaleStep);
                break;
            case Keys.Down when _mode == CaptureMode.Scale:
                ResizeScale(0, -ScaleStep);
                break;
            case Keys.Right when _mode == CaptureMode.Scale:
                ResizeScale(ScaleStep, 0);
                break;
            case Keys.Left when _mode == CaptureMode.Scale:
                ResizeScale(-ScaleStep, 0);
                break;
            case Keys.Left or Keys.Right or Keys.Up or Keys.Down:
                // 矢印キーで微調整（Shift で 10px）。カーソルも一緒に動かす
                int d = e.Shift ? 10 : 1;
                var p = Cursor.Position;
                p.Offset(e.KeyCode == Keys.Left ? -d : e.KeyCode == Keys.Right ? d : 0,
                         e.KeyCode == Keys.Up ? -d : e.KeyCode == Keys.Down ? d : 0);
                Cursor.Position = p;
                break;
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    private void ChangeQuality(int delta)
    {
        // 10 刻みにそろえる（例: 95 から ↑ で 100、↓ で 90）。最小 10、最大 100
        int next = delta > 0
            ? (Quality / 10 + 1) * 10
            : (Quality % 10 == 0 ? Quality - 10 : Quality / 10 * 10);
        next = Math.Clamp(next, 10, 100);
        if (next == Quality) return;
        Quality = next;
        Invalidate();
    }

    private void SetMode(CaptureMode mode)
    {
        _mode = mode;
        _anchor = null;
        _listIndex = null;
        Recompute();
    }

    private void ResizeScale(int dw, int dh)
    {
        int maxW = Math.Max(ScaleStep, _virtualScreen.Width / ScaleStep * ScaleStep);
        int maxH = Math.Max(ScaleStep, _virtualScreen.Height / ScaleStep * ScaleStep);
        int w = Math.Clamp(_scaleWidth + dw, ScaleStep, maxW);
        int h = Math.Clamp(_scaleHeight + dh, ScaleStep, maxH);
        if (w == _scaleWidth && h == _scaleHeight) return;
        _scaleWidth = w;
        _scaleHeight = h;
        Recompute();
    }

    private void SelectPreset(int index)
    {
        if (index < 0 || index >= _presets.Count || index == PresetIndex) return;
        PresetIndex = index;
        if (_listIndex != null) _listIndex = index;
        Recompute();
    }

    private void Commit()
    {
        if (_selection.Width <= 0 || _selection.Height <= 0) return;
        Result = _snapshot.Clone(_selection, PixelFormat.Format32bppArgb);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _snapshot.Dispose();
            _dimmed.Dispose();
            _labelFont.Dispose();
            _rulerFont.Dispose();
            _borderPen.Dispose();
            _crosshairPen.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
