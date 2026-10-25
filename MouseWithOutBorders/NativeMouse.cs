using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace MouseWithOutBorders;

internal static class NativeMouse
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string message);

    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MBUTTONDOWN = 0x0207;
    internal const uint WM_MBUTTONUP = 0x0208;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_XBUTTONDOWN = 0x020B;
    internal const uint WM_XBUTTONUP = 0x020C;
    internal const uint WM_MOUSEHWHEEL = 0x020E;

    internal const uint LEFTDOWN = 0x0002;
    internal const uint LEFTUP = 0x0004;
    internal const uint RIGHTDOWN = 0x0008;
    internal const uint RIGHTUP = 0x0010;
    internal const uint MIDDLEDOWN = 0x0020;
    internal const uint MIDDLEUP = 0x0040;
    internal const uint XDOWN = 0x0080;
    internal const uint XUP = 0x0100;
    internal const uint WHEEL = 0x0800;
    internal const uint HWHEEL = 0x1000;

    private const int WH_MOUSE_LL = 14;
    internal static readonly IntPtr InputMarker = (IntPtr)0x4D574232;
    internal const uint CursorMarkerPrefix = 0x4D570000;
    internal const uint MOVE = 0x0001;
    internal const uint VIRTUALDESK = 0x4000;
    internal const uint ABSOLUTE = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HookInfo
    {
        public Point Position;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // INPUT contains a union. Its natural alignment makes the union start at
    // offset 8 on x64 and offset 4 on x86.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    internal delegate IntPtr HookProcedure(int nCode, IntPtr wParam, IntPtr lParam);
    internal delegate uint InputSender(uint count, Input[] input, int size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure procedure, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    internal static IReadOnlyCollection<int> GetHeldButtons()
    {
        int[] keys = [0x01, 0x02, 0x04, 0x05, 0x06];
        return Enumerable.Range(0, keys.Length).Where(index => (GetAsyncKeyState(keys[index]) & 0x8000) != 0).ToArray();
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] Input[] input, int size);

    // Windows can synthesize pointer events for touchpads and accessibility
    // devices. Ignore only our own input, rather than every injected event.
    internal static bool IsOurInput(HookInfo info) =>
        (unchecked((uint)info.ExtraInfo.ToInt64()) & 0xFFFF0000) == CursorMarkerPrefix;

    internal static IntPtr InstallHook(HookProcedure callback)
    {
        IntPtr handle = SetWindowsHookEx(WH_MOUSE_LL, callback, GetModuleHandle(null), 0);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot install mouse hook");
        return handle;
    }

    internal static void Inject(uint flags, int data = 0) => Inject(flags, data, SendInput);

    // The sender boundary lets regression tests exercise Windows failures without
    // injecting real mouse events into the user's desktop.
    internal static void Inject(uint flags, int data, InputSender sendInput)
        => Send(new MouseInput { Flags = flags, MouseData = unchecked((uint)data) }, sendInput);

    internal static void MoveCursor(System.Drawing.Point position, Rectangle bounds, InputSender? sendInput = null,
        IntPtr? marker = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentException("Invalid desktop bounds.");
        int x = Math.Clamp(position.X, bounds.Left, bounds.Right - 1) - bounds.Left;
        int y = Math.Clamp(position.Y, bounds.Top, bounds.Bottom - 1) - bounds.Top;
        Send(new MouseInput
        {
            Dx = (int)Math.Clamp(((long)x * 65536 + 32768) / bounds.Width, 0, 65535),
            Dy = (int)Math.Clamp(((long)y * 65536 + 32768) / bounds.Height, 0, 65535),
            Flags = MOVE | ABSOLUTE | VIRTUALDESK
        }, sendInput ?? SendInput, marker);
    }

    private static void Send(MouseInput mouse, InputSender sendInput, IntPtr? marker = null)
    {
        mouse.ExtraInfo = marker ?? InputMarker;
        Input[] input = [new()
        {
            Type = 0,
            Data = new InputUnion
            {
                Mouse = mouse
            }
        }];
        if (sendInput(1, input, Marshal.SizeOf<Input>()) != 1)
        {
            int error = Marshal.GetLastWin32Error();
            // SendInput can fail without setting an error, including UIPI blocks.
            // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput
            throw new Win32Exception(error,
                $"Windows did not accept the mouse input (Win32 error {error}). " +
                "An elevated application or secure desktop may be blocking it. " +
                "For Task Manager or administrator windows, restart MWB as administrator on the affected PC. " +
                "UAC prompts and locked screens require a local device.");
        }
    }
}

internal sealed class MouseCapture : IDisposable
{
    private readonly NativeMouse.HookProcedure _callback;
    private readonly Func<uint, NativeMouse.HookInfo, bool> _handler;
    private readonly Func<uint, NativeMouse.HookInfo, bool>? _injectedHandler;
    private IntPtr _hook;
    private bool _disposed;

    internal MouseCapture(Func<uint, NativeMouse.HookInfo, bool> handler,
        Func<uint, NativeMouse.HookInfo, bool>? injectedHandler = null)
    {
        _handler = handler;
        _injectedHandler = injectedHandler;
        _callback = OnHook; // keep delegate alive while hook is installed
        _hook = NativeMouse.InstallHook(_callback);
    }

    private IntPtr OnHook(int code, IntPtr wp, IntPtr lp)
    {
        if (code >= 0 && !_disposed)
        {
            var info = Marshal.PtrToStructure<NativeMouse.HookInfo>(lp);
            uint message = unchecked((uint)wp.ToInt64());
            bool suppress = NativeMouse.IsOurInput(info)
                ? _injectedHandler?.Invoke(message, info) == true
                : _handler(message, info);
            if (suppress)
                return (IntPtr)1;
        }
        return NativeMouse.CallNextHookEx(_hook, code, wp, lp);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            NativeMouse.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
