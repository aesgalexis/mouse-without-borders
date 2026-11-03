namespace MouseWithOutBorders;

internal static class UpdaterUiTests
{
    internal static void Run()
    {
        FakeUpdater updater = new();
        using (MainWindow window = Window(updater))
        {
            Button button = Find<Button>(window, "CheckForUpdates");
            ToolStripMenuItem trayUpdate = TrayUpdate(window);
            Label status = Find<Label>(window, "UpdateStatus");
            string connectionStatus = Find<Label>(window, "ConnectionStatus").Text;
            Assert(button.Visible && button.Enabled && updater.Checks == 0 && status.Text.Contains("0.1.3"),
                "Show the current version and manual update button without contacting GitHub at startup.");
            TaskCompletionSource<AppRelease?> pending = new();
            updater.Check = token => pending.Task.WaitAsync(token);
            Find<CheckBox>(window, "MinimizeToTray").Checked = true;
            window.Close();
            Assert(!window.Visible && trayUpdate.Enabled, "The tray update action must remain available while the window is hidden.");
            trayUpdate.PerformClick();
            Assert(window.Visible && window.WindowState == FormWindowState.Normal,
                "Updating from the tray must show the window with progress and results.");
            button.PerformClick();
            trayUpdate.PerformClick();
            Assert(!button.Enabled && !trayUpdate.Enabled && updater.Checks == 1, "Disable duplicate update requests from both entry points.");
            Assert(Find<Label>(window, "ConnectionStatus").Text == connectionStatus,
                "Update progress must not replace the connection status.");
            pending.SetResult(null);
            PumpUntil(() => button.Enabled && trayUpdate.Enabled);
            Assert(status.Text.Contains("up to date") && updater.Installs == 0,
                "No update must leave the app open and show the result.");

            updater.Check = _ => Task.FromException<AppRelease?>(new System.Net.Http.HttpRequestException("Offline"));
            trayUpdate.PerformClick();
            PumpUntil(() => button.Enabled && trayUpdate.Enabled);
            Assert(status.Text.Contains("Offline") && !window.IsDisposed, "Show check failures and allow retry.");

            updater.Check = _ => Task.FromResult<AppRelease?>(Update);
            updater.Install = (_, _, _) => Task.FromException(new IOException("SHA-256 verification failed"));
            Find<RadioButton>(window, "ReceiverRole").Checked = true;
            Find<Button>(window, "ToggleConnection").PerformClick();
            button.PerformClick();
            PumpUntil(() => button.Enabled);
            Assert(!window.IsDisposed && status.Text.Contains("SHA-256") && !Find<RadioButton>(window, "ReceiverRole").Enabled,
                "A failed update must preserve the active sharing listener.");

            updater.Install = (_, progress, _) => { progress.Report(100); return Task.CompletedTask; };
            Find<CheckBox>(window, "MinimizeToTray").Checked = true;
            trayUpdate.PerformClick();
            PumpUntil(() => window.IsDisposed);
            Assert(updater.Installs == 2, "A successful installer launch must exit the app, including tray mode.");
        }

        FakeUpdater cancelled = new();
        TaskCompletionSource<AppRelease?> waiting = new();
        cancelled.Check = token => waiting.Task.WaitAsync(token);
        using (MainWindow window = Window(cancelled))
        {
            TrayUpdate(window).PerformClick();
            window.Close();
            PumpUntil(() => cancelled.Token.IsCancellationRequested);
            waiting.TrySetResult(Update);
            Application.DoEvents();
            Assert(cancelled.Installs == 0 && window.IsDisposed, "Exiting during a check must cancel without starting an installer.");
        }
        Console.WriteLine("PASS: window/tray updates, hidden-window restoration, shared duplicate prevention, no-update/error/retry, active-listener preservation, exit and cancellation.");
    }

    private static readonly AppRelease Update = new(new Version(0, 1, 4), "v0.1.4", "test.exe",
        new Uri("https://github.com/test.exe"), new Uri("https://github.com/SHA256SUMS.txt"), 1);

    private static MainWindow Window(IAppUpdater updater)
    {
        MainWindow window = new(new AppSettings { MinimizeToTray = false, AutoConnect = false }, _ => { },
            listenerFactory: () => new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0), updater: updater)
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000)
        };
        window.Show();
        Application.DoEvents();
        return window;
    }

    private static T Find<T>(Control window, string name) where T : Control => window.Controls.Find(name, true).OfType<T>().Single();
    private static ToolStripMenuItem TrayUpdate(MainWindow window)
    {
        NotifyIcon tray = (NotifyIcon)typeof(MainWindow).GetField("_tray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        return tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "TrayCheckForUpdates");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void PumpUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
        Assert(condition(), "The update UI did not reach its expected state.");
        Application.DoEvents();
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public Version CurrentVersion => new(0, 1, 3);
        internal int Checks, Installs;
        internal CancellationToken Token;
        internal Func<CancellationToken, Task<AppRelease?>> Check = _ => Task.FromResult<AppRelease?>(null);
        internal Func<AppRelease, IProgress<int>, CancellationToken, Task> Install = (_, _, _) => Task.CompletedTask;
        public Task<AppRelease?> CheckAsync(CancellationToken token)
        {
            Checks++;
            Token = token;
            return Check(token);
        }
        public Task InstallAsync(AppRelease release, IProgress<int> progress, CancellationToken token)
        {
            Installs++;
            return Install(release, progress, token);
        }
    }
}
