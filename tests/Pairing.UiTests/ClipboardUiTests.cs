using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MouseWithOutBorders;

internal static class ClipboardUiTests
{
    private sealed class FakeClipboard : IClipboardSharingService
    {
        internal bool Enabled, Disposed;
        private MousePeer? _attachedPeer;
        internal MousePeer? AttachedPeer => Volatile.Read(ref _attachedPeer);
        internal int AttachCount, AttachThread, DetachCount;
        public void SetEnabled(bool enabled) => Enabled = enabled;
        public void Attach(MousePeer peer)
        {
            Volatile.Write(ref _attachedPeer, peer);
            AttachThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref AttachCount);
        }
        public void Detach(MousePeer peer)
        {
            Interlocked.CompareExchange(ref _attachedPeer, null, peer);
            Interlocked.Increment(ref DetachCount);
        }
        public void Dispose() { Disposed = true; Enabled = false; }
    }

    internal static void Run()
    {
        Assert(!new AppSettings().ShareClipboard &&
            !JsonSerializer.Deserialize<AppSettings>("{\"AutoConnect\":false}")!.ShareClipboard,
            "New and existing settings must keep clipboard sharing disabled until explicitly enabled.");
        TestPreferenceAndLifecycle();
        TestRestoredOptIn();
        TestAttachAfterMouseReadiness();
        Console.WriteLine("PASS: clipboard opt-in default, lazy service startup, persisted enable/disable, relaunch and disposal; no clipboard access.");
    }

    private static void TestPreferenceAndLifecycle()
    {
        AppSettings settings = new() { AutoConnect = false, MinimizeToTray = false };
        string? saved = null;
        List<FakeClipboard> services = [];
        using (MainWindow window = Create(settings, value => saved = JsonSerializer.Serialize(value), _ =>
        {
            FakeClipboard service = new();
            services.Add(service);
            return service;
        }))
        {
            Assert(services.Count == 0, "Constructing the window must not start clipboard monitoring.");
            Show(window);
            CheckBox option = Find<CheckBox>(window, "ShareClipboard");
            Assert(!option.Checked && services.Count == 0,
                "A disabled clipboard preference must not instantiate its service.");
            option.Checked = true;
            Application.DoEvents();
            Assert(settings.ShareClipboard && saved is not null &&
                JsonSerializer.Deserialize<AppSettings>(saved)!.ShareClipboard &&
                services.Count == 1 && services[0].Enabled,
                "Enabling clipboard sharing must save the opt-in and start the service.");
            option.Checked = false;
            Application.DoEvents();
            Assert(!settings.ShareClipboard && !JsonSerializer.Deserialize<AppSettings>(saved!)!.ShareClipboard &&
                (!services[0].Enabled || services[0].Disposed),
                "Disabling clipboard sharing must save the opt-out and stop processing clipboard changes.");
            window.Close();
            Assert(services.All(service => service.Disposed), "Exiting must dispose every clipboard service.");
        }

        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(saved!)!;
        using MainWindow restarted = Create(restored, _ => { }, _ =>
            throw new Exception("A restored clipboard opt-out must not create the service."));
        Show(restarted);
        Assert(!Find<CheckBox>(restarted, "ShareClipboard").Checked,
            "The clipboard opt-out must remain disabled after relaunch.");
        restarted.Close();
    }

    private static void TestRestoredOptIn()
    {
        AppSettings settings = new() { AutoConnect = false, MinimizeToTray = false, ShareClipboard = true };
        FakeClipboard service = new();
        int created = 0;
        using MainWindow window = Create(settings, _ => { }, _ => { created++; return service; });
        Assert(created == 0, "A saved opt-in must wait until the window is ready before starting clipboard monitoring.");
        Show(window);
        Assert(created == 1 && service.Enabled && Find<CheckBox>(window, "ShareClipboard").Checked,
            "A saved clipboard opt-in must start once when the window is shown.");
        Find<Button>(window, "ToggleConnection").PerformClick();
        Find<Button>(window, "ToggleConnection").PerformClick();
        Assert(settings.ShareClipboard && service.Enabled && !service.Disposed,
            "Disconnecting mouse sharing must preserve the clipboard preference for the next connection.");
        window.Close();
        Assert(service.Disposed, "A restored clipboard service must be disposed when MWB exits.");
    }

    private static void TestAttachAfterMouseReadiness()
    {
        int uiThread = Environment.CurrentManagedThreadId;
        AppSettings settings = new() { AutoConnect = false, MinimizeToTray = false };
        FakeClipboard service = new();
        using MainWindow window = new(settings, _ => { }, clipboardFactory: _ => service,
            listenerFactory: () => new TcpListener(IPAddress.Loopback, MousePeer.Port));
        Show(window);
        Find<Button>(window, "ToggleConnection").PerformClick();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<MousePeer> connecting = Task.Run(() => MousePeer.ConnectAsync(
            "127.0.0.1", MousePeer.ParseKey(settings.PairingKey), timeout.Token));
        PumpUntil(() => connecting.IsCompleted);
        using MousePeer peer = connecting.GetAwaiter().GetResult();
        Label status = Find<Label>(window, "ConnectionStatus");
        PumpUntil(() => status.Text.Contains("Preparing mouse sharing", StringComparison.Ordinal));

        Find<CheckBox>(window, "ShareClipboard").Checked = true;
        Assert(service.Enabled && service.AttachedPeer is null && service.AttachCount == 0,
            "Enabling clipboard while paired must wait for the mouse session's readiness before attaching.");
        Assert(peer.Queue(new MousePacket("layout", Data: 1)), "Could not send the peer's screen layout.");
        PumpUntil(() => status.Text.StartsWith("Connected", StringComparison.Ordinal) && service.AttachCount > 0);
        Assert(service.AttachedPeer is not null && service.AttachCount == 1 && service.AttachThread == uiThread,
            "Readiness must attach the clipboard exactly once on the UI thread that owns the checkbox and service factory.");

        peer.Dispose();
        PumpUntil(() => service.AttachedPeer is null && service.DetachCount > 0 &&
            Find<Button>(window, "ToggleConnection").Text == "Stop listening");
        Assert(service.Enabled && !service.Disposed && settings.ShareClipboard,
            "Peer loss must detach clipboard transport while retaining the saved opt-in for reconnect.");
        window.Close();
        Assert(service.Disposed, "Closing must dispose clipboard monitoring after the peer disconnects.");
        Console.WriteLine("PASS: clipboard opt-in while preparing waits for mouse readiness, attaches once on the UI thread and detaches on peer loss.");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(condition(), "Clipboard connection lifecycle did not reach the expected state.");
    }

    private static MainWindow Create(AppSettings settings, Action<AppSettings> save,
        Func<Action<string>, IClipboardSharingService> factory) =>
        new(settings, save, clipboardFactory: factory, listenerFactory: () => new TcpListener(IPAddress.Loopback, 0));

    private static void Show(MainWindow window)
    {
        window.ShowInTaskbar = false;
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-20000, -20000);
        window.Show();
        Application.DoEvents();
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.Controls.Find(name, true).OfType<T>().Single();
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
