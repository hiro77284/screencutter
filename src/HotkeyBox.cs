using System.Runtime.InteropServices;

namespace ScreenCutter;

/// <summary>押したキーの組み合わせをそのままホットキーとして受け取るテキストボックス。</summary>
public sealed class HotkeyBox : TextBox
{
    private uint _modifiers;
    private Keys _key;

    public uint Modifiers => _modifiers;
    public Keys Key => _key;

    public HotkeyBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        ShortcutsEnabled = false;
    }

    public void SetHotkey(uint modifiers, Keys key)
    {
        _modifiers = modifiers;
        _key = key;
        Text = key == Keys.None ? "（なし）" : HotkeyWindow.Describe(modifiers, key);
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Tab や Enter などもホットキー候補として受け取る
        OnKeyDown(new KeyEventArgs(keyData));
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;

        uint mods = CurrentModifiers(e);
        Keys key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
        {
            Text = HotkeyWindow.Describe(mods, Keys.None) + " + ...";
            return;
        }

        if (mods == 0 && key is Keys.Back or Keys.Delete)
        {
            SetHotkey(_modifiers, _key); // 修飾キーなしの Back/Delete は無視して元に戻す
            return;
        }

        SetHotkey(mods, key);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // PrintScreen は KeyDown が届かないので KeyUp で拾う
        if (e.KeyCode == Keys.Snapshot)
            SetHotkey(CurrentModifiers(e), Keys.Snapshot);
        else
            SetHotkey(_modifiers, _key); // 修飾キーだけ押して離したら元の表示に戻す
        e.Handled = true;
    }

    private static uint CurrentModifiers(KeyEventArgs e)
    {
        uint mods = 0;
        if (e.Control) mods |= HotkeyWindow.ModControl;
        if (e.Alt) mods |= HotkeyWindow.ModAlt;
        if (e.Shift) mods |= HotkeyWindow.ModShift;
        if ((GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0) mods |= HotkeyWindow.ModWin;
        return mods;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
