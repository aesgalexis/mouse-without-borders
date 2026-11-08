using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MouseWithOutBorders;

internal static class StartupUiTests
{
    internal static void Run()
    {
        TestDefaultsAndPreferences();
        TestStartupFailures();
        TestAutomaticListener();
        TestIncompletePairing();
        TestDelayedReceiver();
        TestRejectedIdentity();
        Console.WriteLine("PASS: startup defaults, persisted opt-out, registry failure recovery, hidden listening, incomplete pairing, delayed receiver and rejected identity stopping retries.");
    }

    private static void TestDefaultsAndPreferences()
    {
        AppSettings defaults = new();
        Assert(defaults.StartWithWindows && defaults.AutoConnect,
            "Fresh settings must enable Windows startup and automatic connection.");
        AppSettings legacy = JsonSerializer.Deserialize<AppSettings>("{\"AutoConnect\":false}")!;
        Assert(legacy.StartWithWindows && !legacy.AutoConnect,
            "Older settings must gain Windows startup while retaining an explicit connection opt-out.");

        string? saved = null;
        List<bool> changes = [];
        using (MainWindow window = Window(new AppSettings { AutoConnect = false, MinimizeToTray = false },
            settings => saved = JsonSerializer.Serialize(settings), changes.Add))
        {
            Assert(changes.SequenceEqual(new[] { true }) && Find<CheckBox>(window, "StartWithWindows").Checked,
                "Opening the app must register Windows startup by default.");
            Find<CheckBox>(window, "StartWithWindows").Checked = false;
            Assert(changes.SequenceEqual(new[] { true, false }) && saved is not null,
                "Disabling Windows startup must remove the entry and save the preference.");
            window.Close();
        }

        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(saved!)!;
        changes.Clear();
        using MainWindow restarted = Window(restored, _ => { }, changes.Add);
        Assert(changes.SequenceEqual(new[] { false }) && !Find<CheckBox>(restarted, "StartWithWindows").Checked &&
            !Find<CheckBox>(restarted, "AutoConnect").Checked && Find<RadioButton>(restarted, "ReceiverRole").Enabled,
            "Restarting must preserve both disabled preferences and leave sharing stopped.");
        restarted.Close();
    }

    private static void TestStartupFailures()
    {
        AppSettings settings = new() { AutoConnect = false, MinimizeToTray = false };
        using MainWindow window = Window(settings, _ => { }, _ => throw new IOException("Startup unavailable"));
        CheckBox startup = Find<CheckBox>(window, "StartWithWindows");
        Assert(!startup.Checked && Find<Label>(window, "ConnectionStatus").Text.Contains("Startup unavailable"),
            "A failed startup registration must be visible and must not show an enabled checkbox.");
        startup.Checked = true;
        Assert(!startup.Checked && settings.StartWithWindows,
            "A failed retry must restore the checkbox without losing the desired startup preference.");
        window.Close();

        int calls = 0, saves = 0;
        using MainWindow enabled = Window(new AppSettings { AutoConnect = false, MinimizeToTray = false },
            _ => saves++, _ => { if (++calls > 1) throw new IOException("Cannot remove entry"); });
        Find<CheckBox>(enabled, "StartWithWindows").Checked = false;
        Assert(Find<CheckBox>(enabled, "StartWithWindows").Checked && saves == 0,
            "A failed removal must restore the enabled checkbox without persisting a false preference.");
        enabled.Close();
    }

    private static void TestAutomaticListener()
    {
        using MainWindow window = Window(new AppSettings { MinimizeToTray = false }, _ => { }, _ => { }, autostart: true);
        Assert(!window.Visible && !Find<RadioButton>(window, "ReceiverRole").Enabled &&
            Find<Label>(window, "ConnectionStatus").Text.Contains("Waiting"),
            "Windows startup must hide the app and listen automatically in the saved receiver role.");
        window.Close();
    }

    private static void TestIncompletePairing()
    {
        foreach ((string host, string key) in new[] { ("", "ABCD23XY"), ("127.0.0.1", ""), ("127.0.0.1", "INVALID!") })
        {
            using MainWindow window = Window(new AppSettings
            {
                Role = "Controller", Host = host, RemoteKey = key, MinimizeToTray = false
            }, _ => { }, _ => { });
            Assert(Find<RadioButton>(window, "ControllerRole").Enabled &&
                Find<Label>(window, "ConnectionStatus").Text == "Not connected.",
                "An incomplete saved pairing must remain editable without starting or reporting a connection error.");
            window.Close();
        }
    }

    private static void TestDelayedReceiver()
    {
        // Reserve the transport's port, then release it so the first connection is refused.
        // This test never completes authentication or installs desktop mouse hooks.
        using TcpListener receiver = new(IPAddress.Loopback, MousePeer.Port);
        receiver.Start();
        receiver.Stop();
        using MainWindow window = Window(new AppSettings
        {
            Role = "Controller", Host = "127.0.0.1", RemoteKey = MousePeer.NewKey(), MinimizeToTray = false
        }, _ => { }, _ => { }, autostart: true);
        PumpUntil(() => Find<Label>(window, "ConnectionStatus").Text.Contains("Retrying connection"));
        Assert(!window.Visible && !Find<RadioButton>(window, "ControllerRole").Enabled &&
            Find<Button>(window, "ToggleConnection").Text == "Cancel connection" &&
            Find<Label>(window, "ConnectionStatus").Text.Contains("Open MWB", StringComparison.Ordinal),
            "Automatic startup must explain how to start the other PC and offer cancellation while retrying offline.");
        receiver.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
        Task<TcpClient> accepted = receiver.AcceptTcpClientAsync(timeout.Token).AsTask();
        PumpUntil(() => accepted.IsCompleted);
        using TcpClient client = accepted.GetAwaiter().GetResult();
        window.Close();
        Assert(window.IsDisposed, "Closing must cancel a pending automatic connection.");
    }

    private static void TestRejectedIdentity()
    {
        using TcpListener receiver = new(IPAddress.Loopback, MousePeer.Port);
        receiver.Start();
        using MainWindow window = Window(new AppSettings
        {
            Role = "Controller", Host = "127.0.0.1", RemoteKey = MousePeer.NewKey(), MinimizeToTray = false
        }, _ => { }, _ => { }, autostart: true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
        Task<TcpClient> accepting = receiver.AcceptTcpClientAsync(timeout.Token).AsTask();
        PumpUntil(() => accepting.IsCompleted);
        using TcpClient client = accepting.GetAwaiter().GetResult();
        string greeting = $"MOUSE3|{new string('A', 48)}|{new string('B', 64)}|{new string('0', 64)}\n";
        client.GetStream().Write(System.Text.Encoding.ASCII.GetBytes(greeting));
        PumpUntil(() => Find<RadioButton>(window, "ControllerRole").Enabled);
        Assert(Find<Label>(window, "ConnectionStatus").Text.Contains("Check its MWB3 pairing code"),
            "An unverified identity must stop retries and show an actionable pairing error.");
        window.Close();
    }

    private static MainWindow Window(AppSettings settings, Action<AppSettings> save, Action<bool> startup, bool autostart = false)
    {
        MainWindow window = new(settings, save, launchedAtStartup: autostart,
            configureStartup: startup, listenerFactory: () => new TcpListener(IPAddress.Loopback, 0))
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000)
        };
        window.Show();
        Application.DoEvents();
        return window;
    }

    private static T Find<T>(Control root, string name) where T : Control => root.Controls.Find(name, true).OfType<T>().Single();
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void PumpUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(condition(), "Automatic connection did not reach the expected state.");
    }
}
