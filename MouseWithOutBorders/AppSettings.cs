using System.Text.Json;
using Microsoft.Win32;

namespace MouseWithOutBorders;

/// <summary>Local, per-user settings. Pairing keys never belong in the repository.</summary>
internal sealed class AppSettings
{
    public string Role { get; set; } = "Receiver";
    public string Host { get; set; } = "";
    public string PairingKey { get; set; } = MousePeer.NewKey();
    public string RemoteKey { get; set; } = "";
    public string ExitEdge { get; set; } = "Right";
    public bool AutoConnect { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool KeepTrayIconVisible { get; set; } = true;
    public bool ShareClipboard { get; set; }
    public bool KeyRemappingEnabled { get; set; }
    public List<KeyMapping> KeyMappings { get; set; } = [];

    internal bool NormalizeKeyMappings()
    {
        List<KeyMapping> valid = (KeyMappings ?? []).Where(map => map is not null &&
            KeyCatalog.Valid(map.Source) && KeyCatalog.Valid(map.Target) && map.Source != map.Target)
            .GroupBy(map => map.Source).Select(group => group.First()).Take(128).ToList();
        bool changed = KeyMappings is null || valid.Count != KeyMappings.Count;
        if (changed) { KeyMappings = valid; KeyRemappingEnabled = false; }
        return changed;
    }

    internal bool UpgradePairingCode()
    {
        bool changed = false;
        try
        {
            string code = MousePeer.FormatKey(PairingKey ?? "");
            changed = PairingKey != code;
            PairingKey = code;
        }
        catch (ArgumentException)
        {
            // Rotate every legacy secret, including codes exposed by the old handshake.
            PairingKey = MousePeer.NewKey();
            changed = true;
        }
        if (!string.IsNullOrEmpty(RemoteKey))
        {
            try { MousePeer.ParseKey(RemoteKey); }
            catch (ArgumentException) { RemoteKey = ""; changed = true; }
        }
        return changed;
    }
}

internal static class SettingsStore
{
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MouseWithOutBorders");

    internal static string LogPath => Path.Combine(Folder, "diagnostics.log");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    internal static AppSettings Load()
    {
        try
        {
            bool exists = File.Exists(FilePath);
            var settings = exists
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
            bool upgraded = settings.UpgradePairingCode();
            upgraded |= settings.NormalizeKeyMappings();
            if (!exists || upgraded)
            {
                try { Save(settings); }
                catch (Exception ex) { Log("Save pairing code: " + ex.Message); }
            }
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    internal static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 512)
                File.Move(LogPath, LogPath + ".old", overwrite: true);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* Diagnostics must never take down the mouse. */ }
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "MouseWithOutBorders";

    internal static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Entry) is string path && !string.IsNullOrWhiteSpace(path);
    }

    internal static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("Cannot access Windows startup settings.");

        if (enabled)
        {
            string path = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not locate executable.");
            key.SetValue(Entry, "\"" + path + "\" --autostart");
        }
        else
            key.DeleteValue(Entry, throwOnMissingValue: false);
    }
}
