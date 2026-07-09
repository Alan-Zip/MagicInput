using System.Diagnostics;
using System.Text;

namespace MagicInput.Services;

public static class ProcessRunner
{
    public static string Capture(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return "";
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return string.IsNullOrWhiteSpace(output) ? error : output + (string.IsNullOrWhiteSpace(error) ? "" : Environment.NewLine + error);
    }
}
