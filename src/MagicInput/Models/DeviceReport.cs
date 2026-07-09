namespace MagicInput.Models;

public sealed class DeviceReport
{
    public bool TrackpadBluetoothPresent { get; init; }
    public bool TrackpadFilterStarted { get; init; }
    public bool PrecisionTouchpadPresent { get; init; }
    public bool KeyboardPresent { get; init; }
    public string DriverPublishedName { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public string DriverSigner { get; init; } = "";
    public string RawDeviceText { get; init; } = "";
    public string RawDriverText { get; init; } = "";

    public bool DriverInstalled => !string.IsNullOrWhiteSpace(DriverPublishedName);
}

public sealed class PnpDeviceInfo
{
    public string InstanceId { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "";
    public string DriverName { get; init; } = "";
}
