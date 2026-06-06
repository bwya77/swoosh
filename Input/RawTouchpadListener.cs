using System.Runtime.InteropServices;
using System.Windows.Forms;
using Swoosh.Native;

namespace Swoosh.Input;

/// <summary>
/// Registers for raw Precision Touchpad input on a message-only window and
/// raises decoded <see cref="TouchFrame"/>s.
/// </summary>
public sealed class RawTouchpadListener : IDisposable
{
    private readonly MessageWindow _window;
    private readonly TouchpadParser _parser = new();
    private byte[] _buffer = new byte[1024];

    public event Action<TouchFrame>? FrameDecoded;

    /// <summary>Forwards to the parser's phantom-rejection kill-switch (see TouchpadParser).</summary>
    public bool PhantomRejection
    {
        get => _parser.PhantomRejection;
        set => _parser.PhantomRejection = value;
    }

    public RawTouchpadListener(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
        Register();
    }

    private void Register()
    {
        var dev = new Win32.RAWINPUTDEVICE[]
        {
            new()
            {
                usUsagePage = Hid.UP_DIGITIZER, // 0x0D
                usUsage = 0x05,                 // Touch Pad
                dwFlags = Win32.RIDEV_INPUTSINK, // receive even when not foreground
                hwndTarget = _window.Handle,
            }
        };
        if (!Win32.RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>()))
            throw new InvalidOperationException("Failed to register raw touchpad input.");
    }

    private bool OnMessage(Message m)
    {
        if (m.Msg != Win32.WM_INPUT) return false;
        HandleInput(m.LParam);
        return false; // allow default processing (DefRawInputProc) to continue
    }

    private void HandleInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<Win32.RAWINPUTHEADER>();
        Win32.GetRawInputData(hRawInput, Win32.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        if (_buffer.Length < size) _buffer = new byte[size];

        GCHandle h = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            IntPtr ptr = h.AddrOfPinnedObject();
            uint got = Win32.GetRawInputData(hRawInput, Win32.RID_INPUT, ptr, ref size, headerSize);
            if (got == unchecked((uint)-1) || got == 0) return;

            var header = Marshal.PtrToStructure<Win32.RAWINPUTHEADER>(ptr);
            if (header.dwType != Win32.RIM_TYPEHID) return;

            // RAWHID immediately follows the header: { dwSizeHid, dwCount, bRawData[] }
            int hidOffset = (int)headerSize;
            int sizeHid = BitConverter.ToInt32(_buffer, hidOffset);
            int count = BitConverter.ToInt32(_buffer, hidOffset + 4);
            int dataOffset = hidOffset + 8;
            int dataLen = sizeHid * count;
            if (dataLen <= 0 || dataOffset + dataLen > _buffer.Length) return;

            // The buffer stays pinned for this whole call, so hand the parser a
            // pointer straight into it instead of allocating and copying a fresh
            // byte[] for every report batch on the input thread.
            IntPtr dataBase = ptr + dataOffset;
            var frames = _parser.Parse(header.hDevice, dataBase, sizeHid, count);
            foreach (var f in frames)
                FrameDecoded?.Invoke(f);
        }
        finally
        {
            h.Free();
        }
    }

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
    }
}
