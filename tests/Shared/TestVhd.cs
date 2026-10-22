using System.Diagnostics;
using System.Security.Principal;

namespace Dpt.TestSupport;

/// <summary>
/// A throwaway virtual disk (VHDX file) for elevated tests. Every disk-changing step checks that the target disk's bus
/// type is "File Backed Virtual" before touching it, so tests can never modify a real drive (AGENTS.md safety rules 1–3).
/// </summary>
public sealed class TestVhd : IDisposable
{
    private TestVhd(string path, int diskNumber, char letter)
    {
        Path = path;
        DiskNumber = diskNumber;
        Letter = letter;
    }

    public string Path { get; }
    public int DiskNumber { get; }
    public char Letter { get; }
    public string Root => $"{Letter}:\\";

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string Folder => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dpt-vhd");

    /// <summary>Creates, attaches, initializes (GPT), partitions and formats a new virtual disk.</summary>
    public static TestVhd Create(int sizeMb = 512, string fileSystem = "NTFS", string label = "DPTTEST")
    {
        if (!IsElevated) throw new InvalidOperationException("Virtual disk tests must run elevated (./build.ps1 -Elevated).");
        Directory.CreateDirectory(Folder);
        var path = System.IO.Path.Combine(Folder, $"{Guid.NewGuid():N}.vhdx");
        // diskpart "create vdisk" only creates the file; it does not touch any disk.
        RunDiskpart($"create vdisk file=\"{path}\" maximum={sizeMb} type=expandable");
        var output = PowerShell($$"""
            $ErrorActionPreference = 'Stop'
            $img = Mount-DiskImage -ImagePath '{{path}}' -PassThru
            $disk = $img | Get-Disk
            if ($disk.BusType -ne 'File Backed Virtual') { Dismount-DiskImage -ImagePath '{{path}}' | Out-Null; throw "Refusing: disk $($disk.Number) is not a virtual disk." }
            Initialize-Disk -Number $disk.Number -PartitionStyle GPT
            $part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
            Format-Volume -Partition $part -FileSystem {{fileSystem}} -NewFileSystemLabel '{{label}}' -Confirm:$false -Force | Out-Null
            "$($disk.Number)|$($part.DriveLetter)"
            """);
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last(l => l.Contains('|'));
        var parts = line.Split('|');
        return new TestVhd(path, int.Parse(parts[0]), parts[1][0]);
    }

    public static void RunDiskpart(string script)
    {
        var file = System.IO.Path.Combine(Folder, $"dp-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, script);
        try
        {
            var r = Run("diskpart.exe", $"/s \"{file}\"");
            if (r.Code != 0) throw new InvalidOperationException("diskpart failed: " + r.Output);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static string PowerShell(string script)
    {
        var file = System.IO.Path.Combine(Folder, $"ps-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script);
        try
        {
            var r = Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"");
            if (r.Code != 0) throw new InvalidOperationException("PowerShell failed: " + r.Output);
            return r.Output;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static (int Code, string Output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    public void Dispose()
    {
        try { PowerShell($"Dismount-DiskImage -ImagePath '{Path}' | Out-Null"); }
        catch (InvalidOperationException) { }
        try { File.Delete(Path); }
        catch (IOException) { }
    }
}
