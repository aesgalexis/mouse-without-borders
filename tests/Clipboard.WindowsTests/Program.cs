using System.Runtime.InteropServices;

namespace MouseWithOutBorders;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // This suite replaces the real Windows clipboard. Never run it in a
        // developer's account; the normal clipboard suites use fake adapters.
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Environment.GetEnvironmentVariable("RUNNER_OS"), "Windows", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Refusing to access the clipboard: this suite requires a disposable GitHub Actions Windows runner (GITHUB_ACTIONS=true and RUNNER_OS=Windows).");
            return 1;
        }

        string folder = Path.Combine(Path.GetTempPath(), "MWB-Clipboard-WindowsTests-" + Guid.NewGuid().ToString("N"));
        string textFile = Path.Combine(folder, "español-日本語-📄.txt");
        string emptyFile = Path.Combine(folder, "vacío-π.bin");
        try
        {
            Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Clipboard tests must run on an STA thread.");
            Directory.CreateDirectory(folder);
            WindowsClipboardAdapter adapter = new();
            const string text = "Windows clipboard — español 日本語 🚀\r\nSecond line\t✓";
            adapter.Write(new(Text: text));
            CollectDataObjects();
            ClipboardContent receivedText = Read(adapter);
            Assert(receivedText.Text == text && receivedText.Files is null,
                "Unicode text did not survive native clipboard reading after the writer's DataObject was released.");
            Console.WriteLine("PASS: the real Windows adapter preserves Unicode text after DataObject release, verified through Win32 clipboard reads.");

            File.WriteAllText(textFile, text);
            File.WriteAllBytes(emptyFile, []);
            string[] files = [textFile, emptyFile];
            adapter.Write(new(Files: files));
            CollectDataObjects();
            ClipboardContent receivedFiles = Read(adapter);
            Assert(receivedFiles.Text is null && receivedFiles.Files is { } paths && paths.SequenceEqual(files, StringComparer.Ordinal),
                "Unicode file-drop paths did not survive native clipboard reading after the writer's DataObject was released.");
            AssertPreferredCopyEffect();
            Assert(File.ReadAllText(textFile) == text && new FileInfo(emptyFile).Length == 0,
                "Writing file-drop data must preserve the original files.");
            Console.WriteLine("PASS: the real Windows adapter preserves Unicode file-drop paths and publishes native Preferred DropEffect=Copy without custom-object serialization.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            // Discard only this disposable runner's test clipboard and the two
            // known fixture files. No app settings, hooks or input are touched.
            try { Clipboard.Clear(); }
            catch (ExternalException ex) { Console.Error.WriteLine("Could not clear the test clipboard: " + ex.Message); }
            File.Delete(textFile);
            File.Delete(emptyFile);
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
    }

    private static void CollectDataObjects()
    {
        // Write() has returned and retains no managed DataObject reference.
        // Reads below use Windows handles, rather than a managed object cache.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static ClipboardContent Read(WindowsClipboardAdapter adapter)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return adapter.Read() ?? throw new Exception("The native clipboard has no supported content."); }
            catch (ExternalException) when (attempt < 19) { Thread.Sleep(50); }
        }
    }

    private static void AssertPreferredCopyEffect()
    {
        uint format = RegisterClipboardFormat("Preferred DropEffect");
        Assert(format != 0, "Could not register the native Preferred DropEffect format.");
        for (int attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt == 19) throw new Exception("Could not open the native clipboard to inspect Preferred DropEffect.");
            Thread.Sleep(50);
        }
        try
        {
            IntPtr handle = GetClipboardData(format);
            Assert(handle != IntPtr.Zero && GlobalSize(handle).ToUInt64() >= sizeof(int),
                "Preferred DropEffect must contain a native DWORD, not a serialized managed object.");
            IntPtr pointer = GlobalLock(handle);
            Assert(pointer != IntPtr.Zero, "Could not read the native Preferred DropEffect DWORD.");
            try
            {
                Assert(Marshal.ReadInt32(pointer) == (int)DragDropEffects.Copy,
                    "Received file-drop data must request Copy, never Move or another effect.");
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);
}
