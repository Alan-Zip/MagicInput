namespace MagicInput.Models;

public sealed class BatteryReadResult
{
    public bool Ok { get; set; }
    public int Level { get; set; }
    public string Message { get; set; } = "";
    public string Source { get; set; } = "";

    public static BatteryReadResult Success(int level, string source)
    {
        return new BatteryReadResult
        {
            Ok = true,
            Level = Math.Clamp(level, 0, 100),
            Message = $"Battery: {Math.Clamp(level, 0, 100)}%",
            Source = source
        };
    }

    public static BatteryReadResult Failure(string message, string source)
    {
        return new BatteryReadResult
        {
            Ok = false,
            Level = 0,
            Message = message,
            Source = source
        };
    }
}
