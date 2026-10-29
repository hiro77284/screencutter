using System.Runtime.InteropServices;

namespace ScreenCutter;

/// <summary>グローバルホットキーを受け取るメッセージ専用ウィンドウ。</summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private const int WmHotkey = 0x0312;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HashSet<int> _registered = new();

    /// <summary>押されたホットキーの ID を渡す。</summary>
    public event EventHandler<int>? Pressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams { Parent = HwndMessage });
    }

    /// <summary>key が None のときは登録しない（無効）として true を返す。</summary>
    public bool Register(int id, uint modifiers, Keys key)
    {
        Unregister(id);
        if (key == Keys.None) return true;
        if (!RegisterHotKey(Handle, id, modifiers | ModNoRepeat, (uint)key)) return false;
        _registered.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
            UnregisterHotKey(Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (int id in _registered.ToList())
            Unregister(id);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && _registered.Contains(m.WParam.ToInt32()))
        {
            Pressed?.Invoke(this, m.WParam.ToInt32());
            return;
        }
        base.WndProc(ref m);
    }

    public static string Describe(uint modifiers, Keys key)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        if (key != Keys.None) parts.Add(KeyName(key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)(key - Keys.D0)).ToString(),
        Keys.Snapshot => "PrintScreen",
        Keys.Next => "PageDown",
        Keys.Prior => "PageUp",
        Keys.Oemtilde => "@",
        _ => key.ToString(),
    };

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
