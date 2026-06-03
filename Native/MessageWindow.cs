using System.Windows.Forms;

namespace Swoosh.Native;

/// <summary>
/// A hidden message-only window. Hosts WndProc so we can receive WM_INPUT
/// (raw touchpad) and WM_HOTKEY without showing any UI.
/// </summary>
public sealed class MessageWindow : NativeWindow, IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    public event Func<Message, bool>? MessageReceived;

    public MessageWindow(string name)
    {
        var cp = new CreateParams
        {
            Caption = name,
            Parent = HWND_MESSAGE,
        };
        CreateHandle(cp);
    }

    protected override void WndProc(ref Message m)
    {
        if (MessageReceived != null && MessageReceived(m))
            return;
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }
}
