using System.Diagnostics;
using System.Text;
using Dpt.Core.WindowsCopy;
using Dpt.DiskWorker.Clone;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.Windows;

/// <summary>Real implementation. Construction is inert; only Execute calls it after preflight succeeds.</summary>
internal sealed class WindowsCopyPlatform(DiskInfo source, DiskInfo target, WindowsCopyOptions options, StatusWriter status) : IWindowsCopyPlatform
{
    private StorageOperations? _ops;
    private DiskFingerprint _expected = Fingerprint.Of(target);
    private readonly List<(int Partition, string Path)> _letters = [];
    private ShadowCopy? _shadow;
    private string? _workspace, _link, _image;
    private string _windows = "", _system = "", _recovery = "";
    private int _recoveryPartition;
    private string? _recoveryImage;

    private DiskInfo CurrentDestination()
    {
        var disk = StorageInventory.GetDisks().SingleOrDefault(d => d.Number == target.Number) ?? throw new IOException("The destination disconnected.");
        if (Fingerprint.Mismatch(_expected, disk) is { } reason || disk.IsBoot || disk.IsSystem || disk.IsReadOnly || (SafetyMode.Current && !disk.IsVirtual))
            throw new IOException("The destination identity, layout or protection changed; writing stopped.");
        return disk;
    }

    private void Write(Action<StorageOperations> action)
    {
        CurrentDestination();
        action(_ops ??= new StorageOperations());
        var now = StorageInventory.GetDisks().Single(d => d.Number == target.Number);
        // ClearAndInitialize intentionally creates a new disk ID. Keep the new expected layout after each of our writes.
        if (now.Size != target.Size || now.IsBoot || now.IsSystem || (SafetyMode.Current && !now.IsVirtual)) throw new IOException("The destination changed while writing.");
        _expected = Fingerprint.Of(now);
    }

    public string OpenSnapshot()
    {
        foreach (var tool in options.MakeBootable ? (options.Firmware == BootFirmware.Bios ? new[] { "dism.exe", "bcdboot.exe", "bootsect.exe" } : ["dism.exe", "bcdboot.exe"]) : ["dism.exe"])
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, tool))) throw new IOException(tool + " is not available. The destination has not been erased.");
        var lettersNeeded = options.Firmware == BootFirmware.Uefi ? 3 : 2;
        if ("TRSUVWXYZQP".Count(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c)) < lettersNeeded)
            throw new IOException("Not enough temporary drive letters are available. The destination has not been erased.");
        var current = StorageInventory.GetDisks().Single(d => d.Number == source.Number);
        if (Fingerprint.Mismatch(Fingerprint.Of(source), current) is { } why) throw new IOException("Source disk: " + why);
        var partition = current.Partitions.Single(p => string.Equals(p.Root, options.SourceRoot, StringComparison.OrdinalIgnoreCase));
        status.Write(DiskProgressState.Progress, "Taking a consistent snapshot of the source", 0.03);
        _shadow = ShadowCopy.Create(partition.VolumePath ?? options.SourceRoot);
        _workspace = Path.Combine(Path.GetTempPath(), "dpt-windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _link = Path.Combine(_workspace, "snapshot");
        Directory.CreateSymbolicLink(_link, _shadow.Device + "\\");
        if (!File.Exists(Path.Combine(_link, @"Windows\System32\config\SYSTEM"))) throw new IOException("The source does not contain a readable Windows installation.");
        return _link;
    }

    public WindowsCopyMeasurement Measure(string snapshot, IReadOnlyList<string> exclusions)
    {
        status.Write(DiskProgressState.Progress, "Checking selected content and temporary-image space before erasing the destination", 0.06);
        long bytes = 0;
        void Walk(string folder)
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(snapshot, entry.FullName);
                if (exclusions.Any(p => WindowsCopyPlanning.Under(relative, p))) continue;
                // Never follow a junction or cloud link onto another volume. DISM captures the reparse point itself.
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo directory) Walk(directory.FullName);
                else if (entry is FileInfo file) bytes = checked(bytes + file.Length);
            }
        }
        Walk(snapshot); // Permission/read errors abort before any destination write; partial counts are never accepted.
        var images = new[] { Path.Combine(snapshot, @"Windows\System32\Recovery\Winre.wim") }
            .Concat(source.Partitions.Where(p => p.Role == PartitionRole.Recovery && p.VolumePath is not null).Select(p => Path.Combine(p.VolumePath!, @"Recovery\WindowsRE\Winre.wim")));
        _recoveryImage = images.FirstOrDefault(File.Exists);
        if (_recoveryImage is not null && !File.Exists(Path.Combine(Environment.SystemDirectory, "reagentc.exe"))) throw new IOException("REAgentC is unavailable. The destination was not erased.");
        if (options.Firmware == BootFirmware.Uefi && !File.Exists(Path.Combine(Environment.SystemDirectory, "diskpart.exe"))) throw new IOException("The recovery attribute tool is unavailable. The destination was not erased.");
        return new(bytes, _recoveryImage is null ? null : new FileInfo(_recoveryImage).Length);
    }

    public void Prepare(WindowsCopyLayout layout)
    {
        status.Write(DiskProgressState.Progress, $"Replacing the layout on disk {target.Number}", 0.08);
        Write(ops => ops.ClearAndInitialize(target.Number, options.Firmware == BootFirmware.Uefi ? "GPT" : "MBR"));
        foreach (var part in layout.Partitions)
        {
            int number = 0;
            var gpt = part.Role switch { "EFI" => StorageInventory.GptEfi, "MSR" => StorageInventory.GptMsr, "Recovery" => StorageInventory.GptRecovery, _ => StorageInventory.GptBasicData };
            Write(ops => number = ops.CreatePartitionExact(target.Number, part.Offset, part.Size,
                options.Firmware == BootFirmware.Uefi ? gpt : null,
                options.Firmware == BootFirmware.Bios ? part.Role == "Recovery" ? 0x27 : 7 : null,
                options.Firmware == BootFirmware.Bios && part.Role == "Windows"));
            if (part.Role == "MSR") continue;
            Write(ops => ops.Format(target.Number, number, part.Role == "EFI" ? "FAT32" : "NTFS", part.Role == "EFI" ? "SYSTEM" : part.Role));
            var letter = "TRSUVWXYZQP".FirstOrDefault(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c));
            if (letter == default) throw new IOException("No temporary drive letter is available.");
            var root = $"{letter}:\\";
            Write(ops => ops.AddAccessPath(target.Number, number, root));
            _letters.Add((number, root));
            if (part.Role == "Windows") _windows = root;
            else if (part.Role == "EFI") _system = root;
            else if (part.Role == "Recovery") { _recovery = root; _recoveryPartition = number; }
        }
        if (options.Firmware == BootFirmware.Bios) _system = _windows;
    }

    public void Capture(string snapshot, string exclusionConfig)
    {
        CurrentDestination();
        var config = Path.Combine(_workspace!, "exclusions.ini");
        File.WriteAllText(config, exclusionConfig, Encoding.Unicode);
        _image = Path.Combine(_windows, "dpt-transfer-" + Guid.NewGuid().ToString("N") + ".wim");
        status.Write(DiskProgressState.Progress, "Capturing the selected files", 0.1);
        Exec("dism.exe", "/English", "/Capture-Image", "/ImageFile:" + _image, "/CaptureDir:" + snapshot,
            "/Name:DrivePartitionTool copy", "/ConfigFile:" + config, "/Compress:fast", "/CheckIntegrity", "/Verify", "/EA");
    }

    public void Apply()
    {
        CurrentDestination();
        status.Write(DiskProgressState.Progress, "Applying Windows and the selected content", 0.55);
        Exec("dism.exe", "/English", "/Apply-Image", "/ImageFile:" + _image, "/Index:1", "/ApplyDir:" + _windows, "/CheckIntegrity", "/Verify", "/EA");
        File.Delete(_image!);
        _image = null;
    }

    public void CopyRecovery()
    {
        CurrentDestination();
        if (_recoveryImage is not null)
        {
            var folder = Path.Combine(_recovery, @"Recovery\WindowsRE");
            Directory.CreateDirectory(folder);
            File.Copy(_recoveryImage, Path.Combine(folder, "Winre.wim"));
            Exec("reagentc.exe", "/setreimage", "/path", folder, "/target", Path.Combine(_windows, "Windows"));
        }
        if (options.Firmware == BootFirmware.Uefi)
        {
            var part = CurrentDestination().Partitions.Single(p => p.PartitionNumber == _recoveryPartition);
            if (!string.Equals(part.GptType, StorageInventory.GptRecovery, StringComparison.OrdinalIgnoreCase)) throw new IOException("The recovery partition type changed.");
            var script = Path.Combine(_workspace!, "recovery-attributes.txt");
            File.WriteAllText(script, WindowsRecoveryPlanning.AttributeScript(target.Number, _recoveryPartition), Encoding.ASCII);
            try { Write(_ => Exec("diskpart.exe", "/s", script)); }
            finally { File.Delete(script); }
            if (!CurrentDestination().Partitions.Single(p => p.PartitionNumber == _recoveryPartition).NoDefaultDriveLetter)
                throw new IOException("Windows did not preserve the recovery partition's no-default-letter attribute.");
        }
        File.WriteAllText(Path.Combine(_windows, "DrivePartitionTool-Recovery-" + Guid.NewGuid().ToString("N") + ".txt"), WindowsRecoveryPlanning.ActivationInstructions(_recoveryImage is not null));
        status.Write(DiskProgressState.Progress, _recoveryImage is null
            ? "Recovery space is reserved; the source recovery image was unavailable. Recovery tools are not registered."
            : "Recovery image registered for the offline destination. Activate and verify it after booting the copy; see the recovery instructions on the destination.", 0.9);
    }

    public void WriteBootFiles()
    {
        CurrentDestination();
        status.Write(DiskProgressState.Progress, "Writing boot files to the chosen destination", 0.94);
        Exec("bcdboot.exe", Path.Combine(_windows, "Windows"), "/s", _system.TrimEnd('\\'), "/f", options.Firmware == BootFirmware.Uefi ? "UEFI" : "BIOS");
        if (options.Firmware == BootFirmware.Bios)
        {
            CurrentDestination();
            Exec("bootsect.exe", "/nt60", _system.TrimEnd('\\'), "/mbr");
        }
    }

    public void Verify()
    {
        CurrentDestination();
        if (!File.Exists(Path.Combine(_windows, @"Windows\System32\config\SYSTEM"))) throw new IOException("The copied Windows registry is not readable.");
        if (options.MakeBootable && !File.Exists(Path.Combine(_system, options.Firmware == BootFirmware.Uefi ? @"EFI\Microsoft\Boot\BCD" : @"Boot\BCD")))
            throw new IOException("The destination boot configuration was not created.");
    }

    private static void Exec(string name, params string[] arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, name)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new IOException("Could not start " + name);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var text = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException($"{name} failed ({process.ExitCode}): {text[^Math.Min(1200, text.Length)..]}");
    }

    public void Dispose()
    {
        void Clean(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageOperations.StorageOperationException or Microsoft.Management.Infrastructure.CimException)
            { status.Write(DiskProgressState.Progress, "Temporary resource cleanup needs attention: " + ex.Message); }
        }
        if (_image is not null) Clean(() => { CurrentDestination(); File.Delete(_image); });
        // Remove the directory link before deleting the shadow; do not test Directory.Exists on a dangling link.
        if (_link is not null) Clean(() => Directory.Delete(_link));
        if (_workspace is not null) Clean(() => { File.Delete(Path.Combine(_workspace, "exclusions.ini")); Directory.Delete(_workspace); });
        if (_shadow is not null) Clean(_shadow.Dispose);
        foreach (var (partition, path) in _letters)
            Clean(() => { CurrentDestination(); _ops!.RemoveAccessPath(target.Number, partition, path); });
        _ops?.Dispose();
    }
}
