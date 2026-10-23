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

    /// <summary>
    /// Creates a GPT virtual disk laid out like a Windows system disk: EFI (FAT32, 100 MB), Microsoft reserved (16 MB) and an
    /// NTFS data partition (with a letter) filling the rest.
    /// </summary>
    public static TestVhd CreateSystemLike(int sizeMb, string label)
    {
        if (!IsElevated) throw new InvalidOperationException("Virtual disk tests must run elevated (./build.ps1 -Elevated).");
        Directory.CreateDirectory(Folder);
        var path = System.IO.Path.Combine(Folder, $"{Guid.NewGuid():N}.vhdx");
        RunDiskpart($"create vdisk file=\"{path}\" maximum={sizeMb} type=expandable");
        var output = PowerShell($$"""
            $ErrorActionPreference = 'Stop'
            $img = Mount-DiskImage -ImagePath '{{path}}' -PassThru
            $disk = $img | Get-Disk
            if ($disk.BusType -ne 'File Backed Virtual') { Dismount-DiskImage -ImagePath '{{path}}' | Out-Null; throw "Refusing: disk $($disk.Number) is not a virtual disk." }
            Initialize-Disk -Number $disk.Number -PartitionStyle GPT
            Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue | Where-Object Type -eq 'Reserved' | Remove-Partition -Confirm:$false
            $efi = New-Partition -DiskNumber $disk.Number -Size 100MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
            Format-Volume -Partition $efi -FileSystem FAT32 -NewFileSystemLabel 'SYSTEM' -Confirm:$false -Force | Out-Null
            New-Partition -DiskNumber $disk.Number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null
            $part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
            Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel '{{label}}' -Confirm:$false -Force | Out-Null
            "$($disk.Number)|$($part.DriveLetter)"
            """);
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last(l => l.Contains('|'));
        var parts = line.Split('|');
        return new TestVhd(path, int.Parse(parts[0]), parts[1][0]);
    }

    /// <summary>Gives a partition of this test disk a drive letter (if it has none) and returns its root, e.g. "R:\".</summary>
    public static string EnsureLetter(int diskNumber, int partitionNumber)
    {
        var output = PowerShell($$"""
            $ErrorActionPreference = 'Stop'
            $d = Get-Disk -Number {{diskNumber}}
            if ($d.BusType -ne 'File Backed Virtual') { throw 'Refusing: not a virtual disk.' }
            $p = Get-Partition -DiskNumber {{diskNumber}} -PartitionNumber {{partitionNumber}}
            if (-not $p.DriveLetter -or $p.DriveLetter -eq [char]0) { Add-PartitionAccessPath -DiskNumber {{diskNumber}} -PartitionNumber {{partitionNumber}} -AssignDriveLetter; $p = Get-Partition -DiskNumber {{diskNumber}} -PartitionNumber {{partitionNumber}} }
            "LETTER=$($p.DriveLetter)"
            """);
        var letter = output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith("LETTER=", StringComparison.Ordinal))[7];
        return $"{letter}:\\";
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
