namespace MouseWithOutBorders;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { WindowsElevation.WaitForPreviousInstance(args); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Cannot restart Mouse, with out borders",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using AppInstanceGuard singleton = new();
        if (!singleton.Acquired)
        {
            if (!AppInstanceActivation.RequestActivation())
                SettingsStore.Log("Could not show the existing application window.");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainWindow(args.Contains("--autostart", StringComparer.OrdinalIgnoreCase),
            args.Contains("--resume-sharing", StringComparer.OrdinalIgnoreCase)));
    }
}
