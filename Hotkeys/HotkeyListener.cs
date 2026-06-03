using System.Windows.Forms;
using Swoosh.Native;
using Swoosh.Snapping;

namespace Swoosh.Hotkeys;

/// <summary>
/// Global hotkeys (Win+Alt+Arrows) that trigger the same snapping as gestures.
/// Lets the snapping core be validated without touchpad hardware.
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private const uint MOD_ALT = 0x1;
    private const uint MOD_CONTROL = 0x2;
    private const uint MOD_SHIFT = 0x4;
    private const uint MOD_WIN = 0x8;
    private const uint MOD_NOREPEAT = 0x4000;

    // Ctrl+Alt+Shift is used to avoid colliding with FancyZones / OS Win+Arrow.
    private const uint MODS = MOD_CONTROL | MOD_ALT | MOD_SHIFT;

    private const uint VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    private const uint VK_U = 0x55, VK_I = 0x49, VK_J = 0x4A, VK_K = 0x4B;

    private readonly MessageWindow _window;
    private readonly Dictionary<int, SnapZone> _map = new();
    private int _nextId = 1;

    public event Action<SnapZone>? Triggered;

    public int RegisteredCount => _map.Count;

    public HotkeyListener(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;

        Register(MODS, VK_LEFT, SnapZone.LeftHalf);
        Register(MODS, VK_RIGHT, SnapZone.RightHalf);
        Register(MODS, VK_UP, SnapZone.Maximize);
        Register(MODS, VK_DOWN, SnapZone.Minimize);
        // Quarters on U/I/J/K (top-left, top-right, bottom-left, bottom-right).
        Register(MODS, VK_U, SnapZone.TopLeft);
        Register(MODS, VK_I, SnapZone.TopRight);
        Register(MODS, VK_J, SnapZone.BottomLeft);
        Register(MODS, VK_K, SnapZone.BottomRight);
    }

    private void Register(uint mods, uint vk, SnapZone zone)
    {
        int id = _nextId++;
        bool ok = Win32.RegisterHotKey(_window.Handle, id, mods | MOD_NOREPEAT, vk);
        if (ok) _map[id] = zone;
        else Swoosh.Log.Write($"RegisterHotKey FAILED zone={zone} vk=0x{vk:X} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    private bool OnMessage(Message m)
    {
        if (m.Msg != Win32.WM_HOTKEY) return false;
        Swoosh.Log.Write($"WM_HOTKEY id={m.WParam.ToInt32()}");
        if (_map.TryGetValue(m.WParam.ToInt32(), out var zone))
        {
            Triggered?.Invoke(zone);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var id in _map.Keys)
            Win32.UnregisterHotKey(_window.Handle, id);
        _map.Clear();
        _window.MessageReceived -= OnMessage;
    }
}
