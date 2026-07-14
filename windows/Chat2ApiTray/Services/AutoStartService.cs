using System.IO;

namespace Chat2ApiTray.Services;

public sealed class AutoStartService
{
    private const string ShortcutName = "Chat2ApiTray.lnk";

    public void Apply(bool launchAtStartup)
    {
        var shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);
        if (!launchAtStartup)
        {
            TryDelete(shortcutPath);
            return;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        CreateOrUpdateStartupShortcut(shortcutPath, processPath, "--silent");
    }

    private static void CreateOrUpdateStartupShortcut(string shortcutPath, string targetPath, string arguments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup));

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory;
        shortcut.Description = "chat2api tray";
        shortcut.Save();
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
}
