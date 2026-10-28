using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MouseWithOutBorders;

internal static class AppInstanceActivation
{
    private const string WindowProperty = "MouseWithOutBorders.MainWindow.79DBA1BD";
    internal static readonly uint Message = RegisterWindowMessage("MouseWithOutBorders.ShowWindow.79DBA1BD");

    internal static void Register(IntPtr window)
    {
        if (!SetProp(window, WindowProperty, (IntPtr)1)) throw new Win32Exception();
        // A normal shortcut must also be able to reopen an elevated instance.
        ChangeWindowMessageFilterEx(window, Message, 1, IntPtr.Zero); // MSGFLT_ALLOW
    }

    internal static void Unregister(IntPtr window) => RemoveProp(window, WindowProperty);

    internal static bool RequestActivation()
    {
        if (Message == 0) return false;
        // The first process may own the mutex before its window is ready.
        Stopwatch timeout = Stopwatch.StartNew();
        do
        {
            IntPtr target = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                if (GetProp(window, WindowProperty) == IntPtr.Zero) return true;
                target = window;
                return false;
            }, IntPtr.Zero);
            if (target != IntPtr.Zero)
            {
                GetWindowThreadProcessId(target, out uint processId);
                // Pass the shortcut launch's foreground permission to the owner.
                AllowSetForegroundWindow(processId);
                if (PostMessage(target, Message, IntPtr.Zero, IntPtr.Zero)) return true;
            }
            Thread.Sleep(50);
        } while (timeout.Elapsed < TimeSpan.FromSeconds(5));
        return false;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
