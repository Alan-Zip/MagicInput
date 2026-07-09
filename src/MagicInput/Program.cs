using System.Text.Json;
using MagicInput.Models;
using MagicInput.Services;

namespace MagicInput;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0].Equals("--apply-settings", StringComparison.OrdinalIgnoreCase))
        {
            return ApplySettingsFromFile(args[1]);
        }

        if (args.Length >= 2 && args[0].Equals("--read-trackpad-battery", StringComparison.OrdinalIgnoreCase))
        {
            return TrackpadBatteryService.WriteTrackpadBatteryToFile(args[1]);
        }

        if (args.Length >= 2 && args[0].Equals("--read-keyboard-battery", StringComparison.OrdinalIgnoreCase))
        {
            return KeyboardBatteryReader.WriteKeyboardBatteryToFile(args[1]);
        }

        var startInTray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var initialPage = ReadArgumentValue(args, "--page") ?? "Overview";

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startInTray, initialPage));
        return 0;
    }

    private static string? ReadArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static int ApplySettingsFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<TrackpadSettings>(json) ?? TrackpadSettings.Default;
            TrackpadSettingsStore.Write(settings);
            NativeTrackpadBridge.ReloadSettings();
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".error.txt", ex.ToString());
            return 2;
        }
    }
}
