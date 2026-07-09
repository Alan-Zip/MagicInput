using System.Diagnostics;
using System.Text.Json;
using MagicInput.Models;

namespace MagicInput.Services;

public static class TrackpadBatteryService
{
    public static BatteryReadResult Read(bool allowElevation)
    {
        if (NativeTrackpadBridge.TryGetBattery(out var level, out var error))
        {
            return BatteryReadResult.Success((int)level, "MagicTrackpad2ForWindows control device");
        }

        if (allowElevation && error.Contains("Win32 error 5", StringComparison.OrdinalIgnoreCase))
        {
            return ReadWithElevation();
        }

        return BatteryReadResult.Failure(error, "MagicTrackpad2ForWindows control device");
    }

    public static int WriteTrackpadBatteryToFile(string path)
    {
        var result = Read(false);
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Ok ? 0 : 2;
    }

    private static BatteryReadResult ReadWithElevation()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"MagicInput.TrackpadBattery.{Guid.NewGuid():N}.json");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = $"--read-trackpad-battery \"{outputPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return BatteryReadResult.Failure("Could not start elevated battery helper.", "Elevated helper");
            }

            if (!process.WaitForExit(30000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Best effort cleanup after a hung helper.
                }

                return BatteryReadResult.Failure("Elevated battery helper timed out.", "Elevated helper");
            }

            if (!File.Exists(outputPath))
            {
                return BatteryReadResult.Failure($"Elevated battery helper exited with code {process.ExitCode}, but did not return data.", "Elevated helper");
            }

            var result = JsonSerializer.Deserialize<BatteryReadResult>(File.ReadAllText(outputPath));
            return result ?? BatteryReadResult.Failure("Elevated battery helper returned unreadable data.", "Elevated helper");
        }
        catch (Exception ex)
        {
            return BatteryReadResult.Failure(ex.Message, "Elevated helper");
        }
        finally
        {
            try
            {
                File.Delete(outputPath);
            }
            catch
            {
                // Temporary-file cleanup is non-critical.
            }
        }
    }
}
