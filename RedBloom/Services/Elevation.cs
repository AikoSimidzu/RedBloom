using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace RedBloom.Services;

/// <summary>Checks whether the app is running elevated and can relaunch it with a UAC prompt.</summary>
public static class Elevation
{
    /// <summary>True when the process is already running as administrator.</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Relaunches the app elevated and shuts the current instance down. Does nothing if the
    /// user dismisses the UAC prompt, so an unelevated session simply continues.
    /// </summary>
    public static void RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas", // triggers the UAC consent dialog
            WorkingDirectory = AppContext.BaseDirectory,
        };

        try
        {
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user cancelled the elevation prompt; stay as we are.
            return;
        }

        Application.Current.Shutdown();
    }
}
