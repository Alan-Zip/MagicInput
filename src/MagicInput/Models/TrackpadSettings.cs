namespace MagicInput.Models;

public enum HapticMode
{
    Natural,
    Disabled,
    Maximum
}

public enum ClickStopMode
{
    Normal,
    Pressure,
    ContactSize
}

public sealed class TrackpadSettings
{
    public HapticMode HapticMode { get; set; } = HapticMode.Natural;
    public int FeedbackLevel { get; set; } = 1;
    public bool SilentClicking { get; set; }
    public ClickStopMode StopMode { get; set; } = ClickStopMode.ContactSize;
    public int StopPressure { get; set; }
    public int StopSize { get; set; } = 1;
    public bool IgnoreButtonFinger { get; set; } = true;
    public bool IgnoreNearFingers { get; set; } = true;
    public bool PalmRejection { get; set; } = true;

    public static TrackpadSettings Default => new();
}
