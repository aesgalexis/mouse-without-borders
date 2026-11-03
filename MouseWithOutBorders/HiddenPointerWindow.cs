using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MouseWithOutBorders;

internal interface IPointerParkingWindow : IDisposable
{
    void HidePointer();
    void RefreshCursor();
}

// Own the parked cursor's input area without changing the system cursor scheme
// or ShowCursor counter. Window lifecycle stays outside the mouse hook.
internal sealed class HiddenPointerWindow : Form, IPointerParkingWindow
{
    private readonly IntPtr _blank;
    private readonly Cursor _cursor;
    private IntPtr _previous;
    private bool _shown;
    internal IntPtr HiddenCursor => _blank;

    internal HiddenPointerWindow(Point anchor)
    {
        byte[] andMask = Enumerable.Repeat((byte)255, 128).ToArray();
        _blank = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, new byte[128]);
        if (_blank == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _cursor = new Cursor(_blank);
        Cursor = _cursor;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = new Rectangle(anchor.X - 8, anchor.Y - 8, 16, 16);
        BackColor = Color.Black;
        // Alpha zero would pass hit testing to the window beneath, which could
        // reset the cursor. A 1% tint over 16x16 pixels is imperceptible.
        Opacity = 0.01;
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x00000080 | 0x8; // NOACTIVATE, TOOLWINDOW, TOPMOST
            return parameters;
        }
    }

    public void HidePointer()
    {
        if (!_shown)
        {
            _previous = SetCursor(_blank);
            _shown = true;
        }
        // Managed Form.Show/Hide perform activation bookkeeping. This helper
        // must never become the active WinForms form or restore another form.
        ShowWindow(Handle, 4);
        if (!SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x53))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        SetCursor(_blank);
    }

    // Safe in WH_MOUSE_LL: no window creation, focus changes or input injection.
    public void RefreshCursor() => SetCursor(_blank);

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0020) // WM_SETCURSOR
        {
            SetCursor(_blank);
            message.Result = (IntPtr)1;
            return;
        }
        if (message.Msg == 0x0021) // WM_MOUSEACTIVATE
        {
            message.Result = (IntPtr)3; // MA_NOACTIVATE
            return;
        }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_destroyed)
        {
            if (IsHandleCreated) ShowWindow(Handle, 0);
            SetCursor(_previous != IntPtr.Zero ? _previous : LoadCursor(IntPtr.Zero, (IntPtr)32512));
        }
        base.Dispose(disposing);
        if (_blank != IntPtr.Zero && !_destroyed) { _cursor.Dispose(); DestroyCursor(_blank); _destroyed = true; }
    }
    private bool _destroyed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(IntPtr instance, int x, int y, int width, int height, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
