using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;

namespace Dpt.App.Services;

public enum CatalogServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Unknown,
}

/// <summary>
/// Installs/uninstalls the background search catalog service. The install and uninstall commands run in a separate
/// elevated copy of the app (<c>--install-service</c> / <c>--uninstall-service</c>), started with one UAC prompt.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "DrivePartitionToolCatalog";
    private const string DisplayName = "DrivePartitionTool search catalog";
    private const string Description = "Keeps DrivePartitionTool's search catalog and folder sizes up to date. Read-only: it never changes your files.";

    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DrivePartitionTool");
    public static string ProgramData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DrivePartitionTool");
    private static string LogPath => Path.Combine(ProgramData, "install.log");

    public static CatalogServiceState GetState()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => CatalogServiceState.Running,
                ServiceControllerStatus.StartPending => CatalogServiceState.Starting,
                ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending or ServiceControllerStatus.Paused => CatalogServiceState.Stopped,
                _ => CatalogServiceState.Unknown,
            };
        }
        catch (InvalidOperationException)
        {
            return CatalogServiceState.NotInstalled;
        }
    }

    /// <summary>Runs this app elevated with the given arguments; returns its exit code, or null if the UAC prompt was declined.</summary>
    public static async Task<int?> RunElevatedAsync(string arguments)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
    }

    public static string CurrentUserSid()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User!.Value;
    }

    public static void RegisterCommands()
    {
        CommandLine.Commands["--install-service"] = args =>
        {
            var owner = ArgValue(args, "--owner") ?? CurrentUserSid();
            return Run(() => Install(owner));
        };
        CommandLine.Commands["--uninstall-service"] = _ => Run(Uninstall);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Run(Action action)
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Log("Refused: not running as administrator.");
                return 5;
            }

            action();
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            return 1;
        }
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(ProgramData);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private static void Install(string ownerSid)
    {
        Log($"Install requested (owner {ownerSid}).");
        _ = new SecurityIdentifier(ownerSid); // validates
        var source = AppContext.BaseDirectory.TrimEnd('\\');
        var target = InstallDirectory;

        StopService();
        if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectory(source, target);
            Log($"Copied {source} → {target}");
        }

        var exe = Path.Combine(target, "service", "DrivePartitionTool.CatalogService.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Service executable missing.", exe);

        // Owner config and a private catalog folder (snapshots contain file names).
        Directory.CreateDirectory(ProgramData);
        File.WriteAllText(Path.Combine(ProgramData, "service.json"), JsonSerializer.Serialize(new { OwnerSids = new[] { ownerSid } }));
        var catalogDir = Directory.CreateDirectory(Path.Combine(ProgramData, "catalog"));
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(ownerSid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        catalogDir.SetAccessControl(acl);

        if (GetState() == CatalogServiceState.NotInstalled)
            Sc($"create {ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto DisplayName= \"{DisplayName}\"");
        else
            Sc($"config {ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto");
        Sc($"description {ServiceName} \"{Description}\"");
        Sc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/none/0");
        Sc($"start {ServiceName}", allowFailure: true);
        Log("Installed and started.");
    }

    private static void Uninstall()
    {
        Log("Uninstall requested.");
        StopService();
        if (GetState() != CatalogServiceState.NotInstalled) Sc($"delete {ServiceName}");
        Log("Service removed (program files and catalog data kept).");
    }

    private static void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ServiceProcess.TimeoutException)
        {
        }
    }

    private static void Sc(string args, bool allowFailure = false)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Log($"sc {args} → {p.ExitCode} {output.Trim()}");
        if (p.ExitCode != 0 && !allowFailure) throw new InvalidOperationException($"sc {args} failed: {output.Trim()}");
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            try
            {
                File.Copy(file, dest, overwrite: true);
            }
            catch (IOException) when (File.Exists(dest))
            {
                // In use (e.g. the installed app is open). Keep the existing copy.
                Log($"Skipped in-use file {dest}");
            }
        }
    }
}
