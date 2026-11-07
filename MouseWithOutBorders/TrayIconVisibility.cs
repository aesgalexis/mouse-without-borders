using Microsoft.Win32;

namespace MouseWithOutBorders;

internal enum TrayVisibilityResult { Applied, Changed, Pending, Unsupported }

/// <summary>Reapply the user's MWB-only visibility preference as Explorer registers the icon.</summary>
internal sealed class TrayIconVisibility : IDisposable
{
    private readonly Func<TrayVisibilityResult> _promote;
    private readonly Action<string> _log;
    private readonly Action _refreshIcon;
    private readonly System.Windows.Forms.Timer _timer = new();
    private bool _enabled, _disposed;
    private int _pendingAttempts;
    private string? _lastError;

    internal TrayIconVisibility(Func<TrayVisibilityResult> promote, Action<string> log, Action? refreshIcon = null)
    {
        _promote = promote;
        _log = log;
        _refreshIcon = refreshIcon ?? (() => { });
        _timer.Tick += (_, _) => Refresh();
    }

    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_disposed || _enabled == value) return;
            _enabled = value;
            _timer.Stop();
            _pendingAttempts = 0;
            if (value) Refresh();
        }
    }

    // NotifyIcon re-registers itself after TaskbarCreated. Allow that registration
    // and Explorer's asynchronous settings entry to finish before checking again.
    internal void RefreshSoon()
    {
        if (!_enabled || _disposed) return;
        _pendingAttempts = 0;
        _timer.Interval = 1000;
        _timer.Start();
    }

    internal void Refresh()
    {
        if (!_enabled || _disposed) return;
        _timer.Stop();
        int interval = 30000;
        try
        {
            TrayVisibilityResult result = _promote();
            _lastError = null;
            if (result == TrayVisibilityResult.Unsupported) return;
            // Explorer can cache the old placement. Re-register only when the
            // preference changed, keeping the same NotifyIcon ID and menu.
            if (result == TrayVisibilityResult.Changed) _refreshIcon();
            if (result == TrayVisibilityResult.Pending && ++_pendingAttempts <= 10) interval = 1000;
        }
        catch (Exception ex)
        {
            // A shell/registry failure must never interrupt mouse sharing or fill the log.
            if (_lastError != ex.Message) _log("Keep notification icon visible: " + ex.Message);
            _lastError = ex.Message;
        }
        _timer.Interval = interval;
        _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}

internal static class WindowsTrayIconVisibility
{
    private const string SettingsKey = @"Control Panel\NotifyIconSettings";
    internal readonly record struct IconSetting(string Key, string? ExecutablePath, bool Promoted);

    internal static TrayVisibilityResult TryPromote()
    {
        // Windows exposes no public API for promotion. This per-icon preference is
        // specific to Windows 11; never change global tray policies or IconStreams.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return TrayVisibilityResult.Unsupported;
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the running executable.");
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(SettingsKey);
        if (root is null) return TrayVisibilityResult.Pending;
        return PromoteOwnIcons(executable, ReadIcons(root), name =>
        {
            using RegistryKey key = root.OpenSubKey(name, writable: true)
                ?? throw new IOException("The notification icon settings are not ready.");
            key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
        });
    }

    private static IEnumerable<IconSetting> ReadIcons(RegistryKey root)
    {
        foreach (string name in root.GetSubKeyNames())
        {
            using RegistryKey? key = root.OpenSubKey(name);
            if (key is not null)
                yield return new(name, key.GetValue("ExecutablePath") as string,
                    key.GetValue("IsPromoted") is int value && value == 1);
        }
    }

    internal static TrayVisibilityResult PromoteOwnIcons(string executable, IEnumerable<IconSetting> icons,
        Action<string> promote)
    {
        string expected = Path.GetFullPath(executable);
        bool found = false, changed = false;
        foreach (IconSetting icon in icons)
        {
            if (!SameExecutable(icon.ExecutablePath, expected)) continue;
            if (!icon.Promoted) { promote(icon.Key); changed = true; }
            found = true;
        }
        return changed ? TrayVisibilityResult.Changed : found ? TrayVisibilityResult.Applied : TrayVisibilityResult.Pending;
    }

    private static bool SameExecutable(string? value, string expected)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(value);
            return Path.IsPathFullyQualified(expanded) &&
                string.Equals(Path.GetFullPath(expanded), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }
}
