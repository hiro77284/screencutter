using System.Diagnostics;
using Microsoft.Win32;

namespace ScreenCutter;

/// <summary>タスクトレイに常駐し、ホットキーでキャプチャを開始するアプリ本体。</summary>
public sealed class TrayApp : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ScreenCutter";
    private const int CaptureHotkeyId = 0x5C01;

    private AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkey;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _sizeMenu;
    private readonly Icon _icon;
    private bool _busy;
    private string? _lastSavedPath;

    public TrayApp()
    {
        _settings = AppSettings.Load();
        _icon = LoadIcon();

        _captureItem = new ToolStripMenuItem("キャプチャ開始", null, (_, _) => StartCapture());
        _captureItem.Font = new Font(_captureItem.Font, FontStyle.Bold);
        _sizeMenu = new ToolStripMenuItem("切り出しサイズ");

        var menu = new ContextMenuStrip();
        menu.Items.Add(_captureItem);
        menu.Items.Add(_sizeMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("保存先フォルダを開く", null, (_, _) => OpenSaveFolder());
        menu.Items.Add("設定...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = _icon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => RevealLastSaved();

        _hotkey = new HotkeyWindow();
        _hotkey.Pressed += (_, _) => StartCapture();

        RefreshMenu();
        if (!RegisterHotkey())
        {
            _tray.ShowBalloonTip(5000, "ScreenCutter",
                $"ホットキー {HotkeyText()} を登録できませんでした。他のアプリが使用している可能性があります。設定から変更してください。",
                ToolTipIcon.Warning);
        }
        else
        {
            _tray.ShowBalloonTip(2500, "ScreenCutter",
                $"タスクトレイに常駐しました。{HotkeyText()} でキャプチャを開始します。", ToolTipIcon.Info);
        }
    }

    private static Icon LoadIcon()
    {
        using Stream? s = typeof(TrayApp).Assembly.GetManifestResourceStream("ScreenCutter.app.ico");
        return s != null ? new Icon(s, SystemInformation.SmallIconSize) : (Icon)SystemIcons.Application.Clone();
    }

    private string HotkeyText() => HotkeyWindow.Describe(_settings.HotkeyModifiers, _settings.HotkeyKey);

    private bool RegisterHotkey() => _hotkey.Register(CaptureHotkeyId, _settings.HotkeyModifiers, _settings.HotkeyKey);

    private void RefreshMenu()
    {
        _captureItem.Text = $"キャプチャ開始\t{HotkeyText()}";

        _sizeMenu.DropDownItems.Clear();
        for (int i = 0; i < _settings.Presets.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(_settings.Presets[i].ToString())
            {
                Checked = i == _settings.ActivePresetIndex,
            };
            item.Click += (_, _) =>
            {
                _settings.ActivePresetIndex = index;
                SaveSettings();
                RefreshMenu();
            };
            _sizeMenu.DropDownItems.Add(item);
        }

        string tip = $"ScreenCutter - {_settings.ActivePreset.Width}×{_settings.ActivePreset.Height} ({HotkeyText()})";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void StartCapture()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Bitmap? captured;
            int presetIndex;
            string name;
            OutputFormat format;
            int quality;
            CaptureMode mode;
            using (var overlay = new CaptureOverlay(_settings.Presets, _settings.ActivePresetIndex, _settings.Format, _settings.Quality, _settings.LastMode))
            {
                overlay.ShowDialog();
                captured = overlay.Result;
                presetIndex = overlay.PresetIndex;
                name = overlay.ResultName;
                format = overlay.Format;
                quality = overlay.Quality;
                mode = overlay.Mode;
            }

            // キャプチャ中に変えたサイズ・保存形式・モードは、次回以降もそのまま使う
            if (presetIndex != _settings.ActivePresetIndex || format != _settings.Format || quality != _settings.Quality
                || mode != _settings.LastMode)
            {
                _settings.ActivePresetIndex = presetIndex;
                _settings.Format = format;
                _settings.Quality = quality;
                _settings.LastMode = mode;
                SaveSettings();
                RefreshMenu();
            }

            if (captured != null)
            {
                using (captured)
                    SaveCapture(captured, name);
            }
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "キャプチャに失敗しました", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void SaveCapture(Bitmap bitmap, string presetName)
    {
        DateTime now = DateTime.Now;
        string today = now.ToString("yyyy-MM-dd");
        if (_settings.ResetSequenceDaily && _settings.LastSequenceDate != today)
            _settings.NextSequence = 1;

        Directory.CreateDirectory(_settings.SaveFolder);
        string ext = ImageSaver.Extension(_settings.Format);
        bool usesSeq = FileNameBuilder.UsesSequence(_settings.FileNameTemplate);
        SizePreset preset = new(presetName, bitmap.Width, bitmap.Height);

        int seq = _settings.NextSequence;
        string path;
        while (true)
        {
            string baseName = FileNameBuilder.Build(_settings.FileNameTemplate, now, seq, preset);
            path = Path.Combine(_settings.SaveFolder, baseName + ext);
            if (!File.Exists(path)) break;
            if (usesSeq)
            {
                seq++;
                continue;
            }
            // 連番を使わないテンプレートで同名ファイルがある場合は (2), (3)... を付ける
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(_settings.SaveFolder, $"{baseName} ({n}){ext}");
            break;
        }

        ImageSaver.Save(bitmap, path, _settings.Format, _settings.Quality);

        if (usesSeq)
        {
            _settings.NextSequence = seq + 1;
            _settings.LastSequenceDate = today;
            SaveSettings();
        }

        if (_settings.CopyToClipboard)
        {
            try { Clipboard.SetImage(bitmap); } catch { /* クリップボードが使用中でも保存自体は成功扱い */ }
        }

        _lastSavedPath = path;
        if (_settings.ShowNotification)
            _tray.ShowBalloonTip(2000, "保存しました", path, ToolTipIcon.Info);
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "設定を保存できませんでした", ex.Message, ToolTipIcon.Error);
        }
    }

    private SettingsForm? _settingsForm;

    private void ShowSettings()
    {
        if (_settingsForm != null)
        {
            _settingsForm.Activate();
            return;
        }

        _hotkey.UnregisterAll(); // 設定画面でホットキーを入力できるように一時解除
        try
        {
            using (_settingsForm = new SettingsForm(_settings.Clone(), IsStartupEnabled()))
            {
                if (_settingsForm.ShowDialog() == DialogResult.OK)
                {
                    _settings = _settingsForm.Settings;
                    SaveSettings();
                    SetStartupEnabled(_settingsForm.StartWithWindows);
                }
            }
        }
        finally
        {
            _settingsForm = null;
            RefreshMenu();
            if (!RegisterHotkey())
            {
                MessageBox.Show($"ホットキー {HotkeyText()} を登録できませんでした。\n他のアプリが使用している可能性があります。別のキーを設定してください。",
                    "ScreenCutter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void OpenSaveFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.SaveFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.SaveFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ScreenCutter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RevealLastSaved()
    {
        if (_lastSavedPath == null || !File.Exists(_lastSavedPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastSavedPath}\"") { UseShellExecute = true });
    }

    private static bool IsStartupEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled && Environment.ProcessPath != null)
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValueName, false);
    }

    protected override void ExitThreadCore()
    {
        _hotkey.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
