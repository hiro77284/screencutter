using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenCutter;

public sealed class SizePreset
{
    public string Name { get; set; } = "新規";
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 480;

    public SizePreset() { }

    public SizePreset(string name, int width, int height)
    {
        Name = name;
        Width = width;
        Height = height;
    }

    public override string ToString() => $"{Name}  ({Width} × {Height})";
}

public enum OutputFormat
{
    Png,
    Jpg,
    Webp,
}

public sealed class AppSettings
{
    // RegisterHotKey の修飾キー (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8)
    public uint HotkeyModifiers { get; set; } = HotkeyWindow.ModControl | HotkeyWindow.ModAlt;
    public Keys HotkeyKey { get; set; } = Keys.S;

    public List<SizePreset> Presets { get; set; } = new();
    public int ActivePresetIndex { get; set; }

    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenCutter");

    public string FileNameTemplate { get; set; } = "SC_{date}_{time}_{seq:4}";
    public int NextSequence { get; set; } = 1;
    public bool ResetSequenceDaily { get; set; }
    public string? LastSequenceDate { get; set; }

    public OutputFormat Format { get; set; } = OutputFormat.Png;
    public int Quality { get; set; } = 90;

    public bool CopyToClipboard { get; set; }
    public bool ShowNotification { get; set; } = true;

    [JsonIgnore]
    public SizePreset ActivePreset => Presets[Math.Clamp(ActivePresetIndex, 0, Presets.Count - 1)];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenCutter", "settings.json");

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(SettingsPath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
        }
        catch
        {
            // 壊れた設定ファイルは無視して既定値で起動する
        }

        settings ??= new AppSettings();
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public AppSettings Clone()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
        copy.Normalize();
        return copy;
    }

    private void Normalize()
    {
        Presets.RemoveAll(p => p.Width <= 0 || p.Height <= 0);
        if (Presets.Count == 0)
        {
            Presets.AddRange(new[]
            {
                new SizePreset("VGA", 640, 480),
                new SizePreset("HD", 1280, 720),
                new SizePreset("フルHD", 1920, 1080),
                new SizePreset("正方形", 1080, 1080),
                new SizePreset("アイコン", 512, 512),
            });
        }

        ActivePresetIndex = Math.Clamp(ActivePresetIndex, 0, Presets.Count - 1);
        Quality = Math.Clamp(Quality, 1, 100);
        if (NextSequence < 0) NextSequence = 1;
        if (string.IsNullOrWhiteSpace(FileNameTemplate)) FileNameTemplate = "SC_{date}_{time}_{seq:4}";
        if (string.IsNullOrWhiteSpace(SaveFolder))
            SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenCutter");
    }
}
