using Microsoft.Win32;

namespace RedBloom.Services;

/// <summary>
/// Adds or removes RedBloom's "Open RedBloom here" entry in the Explorer folder context menu.
/// </summary>
/// <remarks>
/// This is the practical face of "use it as my terminal": right-click a folder — or its empty
/// background — and open a shell there. Becoming the actual Windows "Default Terminal
/// Application" is a different thing entirely: that requires implementing the console handoff
/// COM protocol so the OS can hand new console processes to RedBloom, which is far beyond a
/// registry entry. Everything here is written under HKCU, so no elevation is needed.
/// </remarks>
public static class ShellIntegration
{
    private const string EntryName = "RedBloom";
    private const string FolderKey = @"Software\Classes\Directory\shell\" + EntryName;
    private const string BackgroundKey = @"Software\Classes\Directory\Background\shell\" + EntryName;

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(FolderKey);
            return key is not null;
        }
    }

    /// <summary>Registers the context-menu entry for the current user.</summary>
    public static bool Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        try
        {
            var label = LocalizationService.T("L_OpenHere");

            // A folder selected in the pane passes its path as %V; the folder background passes
            // its own path the same way, so one command line serves both.
            Write(FolderKey, label, exe, "\"%V\"");
            Write(BackgroundKey, label, exe, "\"%V\"");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(FolderKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(BackgroundKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Nothing to undo if we could not have written it.
        }
    }

    private static void Write(string keyPath, string label, string exe, string argument)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(null, label);
        key.SetValue("Icon", exe);

        using var command = key.CreateSubKey("command");
        command.SetValue(null, $"\"{exe}\" {argument}");
    }
}
