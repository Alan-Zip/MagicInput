namespace MagicInput.Models;

public sealed class AppSettings
{
    public bool MediaRowMapperEnabled { get; set; }
    public bool SwapCommandControlEnabled { get; set; }
    public bool ThreeFingerDragEnabled { get; set; } = true;
    public CornerTapAction BottomLeftTapAction { get; set; } = CornerTapAction.ClipboardHistory;
    public bool LaunchAtLogin { get; set; }
}

public enum CornerTapAction
{
    Off,
    ClipboardHistory,
    StartMenu,
    TaskView,
    ShowDesktop,
    PreviousWindow,
    ControlAltG
}
