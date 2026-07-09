namespace MagicInput.Models;

public sealed class AppSettings
{
    public bool MediaRowMapperEnabled { get; set; }
    public bool SwapCommandControlEnabled { get; set; }
    public bool ThreeFingerDragEnabled { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
}
