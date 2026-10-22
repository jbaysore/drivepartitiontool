using System.ComponentModel;
using System.Diagnostics;
using Dpt.Shell.Interop;

namespace Dpt.Shell;

/// <summary>Opens items exactly as File Explorer would (default verb / file association).</summary>
public static class ShellLauncher
{
    public static void Open(string path, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(path) ?? "",
        };
        Process.Start(psi)?.Dispose();
    }

    public static void RunAsAdministrator(string path)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path) ?? "" };
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The owner declined the UAC prompt.
        }
    }

    public static void OpenWith(string path)
    {
        var psi = new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false };
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Shows the standard Windows Properties dialog.</summary>
    public static void ShowProperties(IntPtr owner, string path) =>
        NativeMethods.SHObjectProperties(owner, 0x2 /* SHOP_FILEPATH */, path, null);

    /// <summary>Opens File Explorer with the item selected.</summary>
    public static void RevealInExplorer(string path)
    {
        var psi = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false };
        Process.Start(psi)?.Dispose();
    }

    public static void OpenTerminal(string folder)
    {
        var psi = new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true };
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder })?.Dispose();
        }
    }
}
