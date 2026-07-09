using System.Security.Principal;
using System.Text.Json;
using MagicInput.Models;
using Microsoft.Win32;

namespace MagicInput.Services;

public static class TrackpadSettingsStore
{
    private const string UmParametersPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\AmtPtpDeviceUsbUm\Parameters";
    private const string KmParametersPath = @"SYSTEM\CurrentControlSet\Services\AmtPtpHidFilter\Parameters";

    public static TrackpadSettings Read()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(UmParametersPath) ?? baseKey.OpenSubKey(KmParametersPath);

        if (key == null)
        {
            return TrackpadSettings.Default;
        }

        var buttonDisabled = ReadInt(key, "ButtonDisabled", 0);
        var feedbackClick = ReadInt(key, "FeedbackClick", 0x060617);
        var stopPressure = ReadInt(key, "StopPressure", 0);
        var stopSize = ReadInt(key, "StopSize", -1);

        return new TrackpadSettings
        {
            HapticMode = DecodeHapticMode(buttonDisabled, feedbackClick),
            FeedbackLevel = DecodeFeedbackLevel(feedbackClick),
            SilentClicking = (feedbackClick & 0xffff00) == 0 && feedbackClick != 0 && feedbackClick != 0xffffff,
            StopMode = stopPressure >= 0 ? ClickStopMode.Pressure :
                stopSize >= 0 ? ClickStopMode.ContactSize : ClickStopMode.Normal,
            StopPressure = Math.Max(0, stopPressure),
            StopSize = Math.Max(0, stopSize),
            IgnoreButtonFinger = ReadInt(key, "IgnoreButtonFinger", 1) != 0,
            IgnoreNearFingers = ReadInt(key, "IgnoreNearFingers", 1) != 0,
            PalmRejection = ReadInt(key, "PalmRejection", 1) != 0
        };
    }

    public static void Write(TrackpadSettings settings)
    {
        var encoded = Encode(settings);
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        WriteToPath(baseKey, UmParametersPath, encoded);
        WriteToPath(baseKey, KmParametersPath, encoded);
    }

    public static bool IsAdministrator()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static (bool Ok, string Message) ApplyWithElevationIfNeeded(TrackpadSettings settings)
    {
        if (IsAdministrator())
        {
            Write(settings);
            NativeTrackpadBridge.ReloadSettings();
            return (true, "Settings applied.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"magic-input-trackpad-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings));

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Application.ExecutablePath,
            Arguments = $"--apply-settings \"{tempPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null)
        {
            return (false, "Could not start elevated settings helper.");
        }

        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            TryDelete(tempPath);
            TryDelete(tempPath + ".error.txt");
            return (true, "Settings applied.");
        }

        var errorPath = tempPath + ".error.txt";
        var detail = File.Exists(errorPath) ? File.ReadAllText(errorPath) : $"Elevated helper exited with code {process.ExitCode}.";
        return (false, detail);
    }

    private static void WriteToPath(RegistryKey baseKey, string path, EncodedSettings settings)
    {
        using var key = baseKey.CreateSubKey(path, true);
        key.SetValue("ButtonDisabled", settings.ButtonDisabled, RegistryValueKind.DWord);
        key.SetValue("FeedbackClick", settings.FeedbackClick, RegistryValueKind.DWord);
        key.SetValue("FeedbackRelease", settings.FeedbackRelease, RegistryValueKind.DWord);
        key.SetValue("StopPressure", settings.StopPressure, RegistryValueKind.DWord);
        key.SetValue("StopSize", settings.StopSize, RegistryValueKind.DWord);
        key.SetValue("IgnoreButtonFinger", settings.IgnoreButtonFinger, RegistryValueKind.DWord);
        key.SetValue("IgnoreNearFingers", settings.IgnoreNearFingers, RegistryValueKind.DWord);
        key.SetValue("PalmRejection", settings.PalmRejection, RegistryValueKind.DWord);
    }

    private static EncodedSettings Encode(TrackpadSettings settings)
    {
        var buttonDisabled = 0;
        var feedbackClick = 0x060617;
        var feedbackRelease = 0x000014;

        if (settings.HapticMode == HapticMode.Disabled)
        {
            buttonDisabled = 1;
            feedbackClick = 0;
            feedbackRelease = 0;
        }
        else if (settings.HapticMode == HapticMode.Maximum)
        {
            feedbackClick = 0xffffff;
            feedbackRelease = 0xffffff;
        }
        else
        {
            feedbackClick = settings.FeedbackLevel switch
            {
                <= 0 => 0x040415,
                1 => 0x060617,
                _ => 0x08081e
            };
            feedbackRelease = settings.FeedbackLevel switch
            {
                <= 0 => 0x000010,
                1 => 0x000014,
                _ => 0x020218
            };

            if (settings.SilentClicking)
            {
                feedbackClick &= 0x0000ff;
                feedbackRelease &= 0x0000ff;
            }
        }

        var stopPressure = settings.StopMode == ClickStopMode.Pressure ? Math.Max(0, settings.StopPressure) : -1;
        var stopSize = settings.StopMode == ClickStopMode.ContactSize ? Math.Max(0, settings.StopSize) : -1;

        return new EncodedSettings(
            buttonDisabled,
            feedbackClick,
            feedbackRelease,
            stopPressure,
            stopSize,
            settings.IgnoreButtonFinger ? 1 : 0,
            settings.IgnoreNearFingers ? 1 : 0,
            settings.PalmRejection ? 1 : 0);
    }

    private static HapticMode DecodeHapticMode(int buttonDisabled, int feedbackClick)
    {
        if (buttonDisabled != 0 || feedbackClick == 0)
        {
            return HapticMode.Disabled;
        }

        return feedbackClick == 0xffffff ? HapticMode.Maximum : HapticMode.Natural;
    }

    private static int DecodeFeedbackLevel(int feedbackClick)
    {
        var normalized = feedbackClick;
        if ((normalized & 0xffff00) == 0)
        {
            normalized = normalized switch
            {
                0x15 => 0x040415,
                0x17 => 0x060617,
                0x1e => 0x08081e,
                _ => normalized
            };
        }

        return normalized switch
        {
            0x040415 => 0,
            0x08081e => 2,
            _ => 1
        };
    }

    private static int ReadInt(RegistryKey key, string name, int defaultValue)
    {
        try
        {
            return Convert.ToInt32(key.GetValue(name, defaultValue));
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record EncodedSettings(
        int ButtonDisabled,
        int FeedbackClick,
        int FeedbackRelease,
        int StopPressure,
        int StopSize,
        int IgnoreButtonFinger,
        int IgnoreNearFingers,
        int PalmRejection);
}
