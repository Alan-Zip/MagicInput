namespace MagicInput.Services;

public static class WorkspaceLocator
{
    public static string? FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "install-trackpad-driver.ps1")) &&
                Directory.Exists(Path.Combine(directory.FullName, "packages")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static string? FindControlPanel()
    {
        var root = FindRoot();
        if (root == null)
        {
            return null;
        }

        var path = Path.Combine(root, "packages", "MT2FW11-20260223-MSSigned", "MT2FW11-20260223-MSSigned", "AmtPtpControlPanel.exe");
        return File.Exists(path) ? path : null;
    }

    public static string? FindScript(string scriptName)
    {
        var root = FindRoot();
        if (root == null)
        {
            return null;
        }

        var path = Path.Combine(root, "scripts", scriptName);
        return File.Exists(path) ? path : null;
    }
}
