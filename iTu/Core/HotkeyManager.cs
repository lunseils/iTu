using System.Runtime.InteropServices;

namespace iTu.Core;

/// <summary>
/// 全局热键管理。基于 RegisterHotKey + 隐藏消息窗口，
/// 支持 "Ctrl+Alt+A" 这类组合键字符串的解析与热更新。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HotkeyWindow _window = new();
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        _window.HotkeyPressed += OnHotkey;
    }

    public void Register(string combination, Action action, out string? error)
    {
        error = null;
        if (!TryParse(combination, out var mods, out var vk))
        {
            error = $"无法识别的快捷键：{combination}";
            return;
        }

        int id = _nextId++;
        if (!Native.RegisterHotKey(_window.Handle, id, mods, vk))
        {
            error = $"快捷键 {combination} 注册失败，可能已被其它程序占用";
            return;
        }
        _actions[id] = action;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys.ToList())
            Native.UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
    }

    private void OnHotkey(int id)
    {
        if (_actions.TryGetValue(id, out var action))
        {
            try { action(); }
            catch (Exception ex) { Trace(ex); }
        }
    }

    internal static void Trace(Exception ex)
        // Debug.WriteLine 在 Release 构建里不可见，热键回调异常会被完全吞掉无法诊断。
        => Log.Info("[hotkey] 回调异常: " + ex.Message);

    /// <summary>解析 "Ctrl+Alt+A" / "Ctrl+Shift+F2" 形式的组合键。</summary>
    public static bool TryParse(string combination, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(combination)) return false;

        var parts = combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool hasKey = false;

        foreach (var raw in parts)
        {
            var p = raw.ToLowerInvariant();
            switch (p)
            {
                case "ctrl" or "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win" or "windows" or "meta": modifiers |= Native.MOD_WIN; break;
                default:
                    if (!TryParseKey(p, out virtualKey)) return false;
                    hasKey = true;
                    break;
            }
        }
        return hasKey && modifiers != 0;
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') { vk = (uint)c; return true; }
            if (c is >= '0' and <= '9') { vk = (uint)c; return true; }
            switch (c)
            {
                case '`': vk = 0xC0; return true;
                case '-': vk = 0xBD; return true;
                case '=': vk = 0xBB; return true;
                case '[': vk = 0xDB; return true;
                case ']': vk = 0xDD; return true;
                case '\\': vk = 0xDC; return true;
                case ';': vk = 0xBA; return true;
                case '\'': vk = 0xDE; return true;
                case ',': vk = 0xBC; return true;
                case '.': vk = 0xBE; return true;
                case '/': vk = 0xBF; return true;
            }
            return false;
        }

        if (key.StartsWith('f') && int.TryParse(key[1..], out int f) && f >= 1 && f <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }

    /// <summary>隐藏消息窗口，用于接收 WM_HOTKEY。</summary>
    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int>? HotkeyPressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "iTuHotkeySink",
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Style = 0,
                ExStyle = 0x00000080 // WS_EX_TOOLWINDOW
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                HotkeyPressed?.Invoke(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
