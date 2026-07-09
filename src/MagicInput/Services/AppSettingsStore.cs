using System.Text.Json;
using System.Text.Json.Serialization;
using MagicInput.Models;
using Microsoft.Win32;

namespace MagicInput.Services;

public static class AppSettingsStore
{
    private const string StartupValueName = "MagicInput";

    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagicInput");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings { LaunchAtLogin = IsStartupEnabled() };
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
            settings.LaunchAtLogin = IsStartupEnabled();
            return settings;
        }
        catch
        {
            return new AppSettings { LaunchAtLogin = IsStartupEnabled() };
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        SetStartup(settings.LaunchAtLogin);
    }

    private static bool IsStartupEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return !string.IsNullOrWhiteSpace(runKey?.GetValue(StartupValueName) as string);
    }

    private static void SetStartup(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
        {
            runKey.SetValue(StartupValueName, $"\"{Application.ExecutablePath}\" --tray");
        }
        else
        {
            runKey.DeleteValue(StartupValueName, false);
        }
    }
}
