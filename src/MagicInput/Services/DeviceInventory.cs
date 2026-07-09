using MagicInput.Models;

namespace MagicInput.Services;

public static class DeviceInventory
{
    public static DeviceReport Read()
    {
        var deviceText = ProcessRunner.Capture("pnputil.exe", "/enum-devices /connected /class HIDClass");
        var driverText = ProcessRunner.Capture("pnputil.exe", "/enum-drivers /class HIDClass");
        var devices = ParseDevices(deviceText);
        var driverBlock = FindDriverBlock(driverText);

        return new DeviceReport
        {
            TrackpadBluetoothPresent = devices.Any(d => d.InstanceId.Contains("PID&0265", StringComparison.OrdinalIgnoreCase)),
            TrackpadFilterStarted = devices.Any(d =>
                d.Description.Contains("Apple Multi-touch Trackpad HID Filter", StringComparison.OrdinalIgnoreCase) &&
                d.Status.Contains("Started", StringComparison.OrdinalIgnoreCase)),
            PrecisionTouchpadPresent = devices.Any(d =>
                d.Description.Contains("HID-compliant touch pad", StringComparison.OrdinalIgnoreCase) &&
                d.InstanceId.Contains("PID&0265", StringComparison.OrdinalIgnoreCase)),
            KeyboardPresent = devices.Any(d => d.InstanceId.Contains("PID&0267", StringComparison.OrdinalIgnoreCase)),
            DriverPublishedName = ReadField(driverBlock, "Published Name"),
            DriverVersion = ReadField(driverBlock, "Driver Version"),
            DriverSigner = ReadField(driverBlock, "Signer Name"),
            RawDeviceText = deviceText,
            RawDriverText = driverText
        };
    }

    private static List<PnpDeviceInfo> ParseDevices(string text)
    {
        var devices = new List<PnpDeviceInfo>();
        var blocks = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var instanceId = ReadField(block, "Instance ID");
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                continue;
            }

            devices.Add(new PnpDeviceInfo
            {
                InstanceId = instanceId,
                Description = ReadField(block, "Device Description"),
                Status = ReadField(block, "Status"),
                DriverName = ReadField(block, "Driver Name")
            });
        }

        return devices;
    }

    private static string FindDriverBlock(string text)
    {
        var blocks = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        return blocks.FirstOrDefault(block =>
            block.Contains("AmtPtpDevice", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("Bingxing Wang, Vito Plantamura", StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static string ReadField(string block, string name)
    {
        foreach (var line in block.Replace("\r\n", "\n").Split('\n'))
        {
            var index = line.IndexOf(':');
            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(index + 1)..].Trim();
            }
        }

        return "";
    }
}
