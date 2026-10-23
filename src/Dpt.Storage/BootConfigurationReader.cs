using System.Diagnostics;
using System.Security.Principal;
using Dpt.Ipc;

namespace Dpt.Storage;

/// <summary>Read-only, fixed BCDEdit invocation. No caller-controlled arguments and no elevation request.</summary>
public static class BootConfigurationReader
{
    public static async Task<BootConfigurationInfo> ReadAsync(CancellationToken token = default)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return new(false, "", "Reading the startup configuration requires administrator access through the background catalog service. No administrator prompt was requested.");
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("/enum"); psi.ArgumentList.Add("all"); psi.ArgumentList.Add("/v");
            using var process = Process.Start(psi) ?? throw new IOException("Could not start the read-only boot query.");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw; }
            var text = await output;
            return process.ExitCode == 0 ? new(true, text, null) : new(false, "", "The boot configuration could not be read: " + (await error).Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { return new(false, "", "Boot configuration is unavailable: " + ex.Message); }
    }
}
