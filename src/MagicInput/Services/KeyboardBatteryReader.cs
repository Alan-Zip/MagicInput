using System.Runtime.InteropServices;
using System.Text.Json;
using MagicInput.Models;
using Microsoft.Win32.SafeHandles;

namespace MagicInput.Services;

public static class KeyboardBatteryReader
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceInterface = 0x00000010;

    private static readonly string[] KeyboardPids =
    [
        "020E",
        "020F",
        "0257",
        "0267",
        "026C",
        "0273",
        "029A",
        "029C",
        "029F",
        "0320",
        "0321",
        "0322"
    ];

    public static BatteryReadResult Read()
    {
        var errors = new List<string>();
        var paths = EnumerateHidDevicePaths().Where(IsAppleKeyboardPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (paths.Count == 0)
        {
            return BatteryReadResult.Failure("No Apple Magic Keyboard HID battery interface was found.", "Apple HID");
        }

        foreach (var path in paths)
        {
            var result = TryReadPath(path, errors);
            if (result.Ok)
            {
                return result;
            }
        }

        var message = errors.Count == 0
            ? "Magic Keyboard HID interfaces were found, but none returned a battery report."
            : "Magic Keyboard battery report failed: " + string.Join("; ", errors.Take(3));

        return BatteryReadResult.Failure(message, "Apple HID");
    }

    public static int WriteKeyboardBatteryToFile(string path)
    {
        var result = Read();
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Ok ? 0 : 2;
    }

    private static BatteryReadResult TryReadPath(string path, List<string> errors)
    {
        using var handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            errors.Add($"open failed {Marshal.GetLastWin32Error()}");
            return BatteryReadResult.Failure("", "Apple HID");
        }

        var buffer = new byte[3];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            buffer[0] = 0x90;
            if (HidD_GetInputReport(handle, buffer, buffer.Length) && buffer[0] == 0x90 && buffer[2] <= 100)
            {
                return BatteryReadResult.Success(buffer[2], "Apple HID input report 0x90");
            }

            Thread.Sleep(50);
        }

        errors.Add($"report failed {Marshal.GetLastWin32Error()}");
        return BatteryReadResult.Failure("", "Apple HID");
    }

    private static bool IsAppleKeyboardPath(string path)
    {
        var lower = path.ToLowerInvariant();
        var isAppleVendor = lower.Contains("vid&0001004c", StringComparison.Ordinal) ||
                            lower.Contains("vid&000205ac", StringComparison.Ordinal) ||
                            lower.Contains("vid_004c", StringComparison.Ordinal) ||
                            lower.Contains("vid_05ac", StringComparison.Ordinal);

        return isAppleVendor && KeyboardPids.Any(pid =>
            lower.Contains($"pid&{pid.ToLowerInvariant()}", StringComparison.Ordinal) ||
            lower.Contains($"pid_{pid.ToLowerInvariant()}", StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateHidDevicePaths()
    {
        HidD_GetHidGuid(out var hidGuid);
        var deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };

                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    yield break;
                }

                SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
                if (requiredSize == 0)
                {
                    continue;
                }

                var detailData = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 5);
                    if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailData, requiredSize, out _, IntPtr.Zero))
                    {
                        var pathPointer = IntPtr.Add(detailData, 4);
                        var path = Marshal.PtrToStringAuto(pathPointer);
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            yield return path;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailData);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
