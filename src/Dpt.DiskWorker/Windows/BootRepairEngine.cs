using System.Diagnostics;
using System.Text.Json;
using Dpt.Core.Boot;
using Dpt.Core.WindowsCopy;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.Windows;

internal interface IBootRepairPlatform : IDisposable
{
    void CheckWindowsAndTools(BootRepairOptions options);
    string SystemRoot(PartitionInfo partition);
    void Rebuild(BootRepairOptions options, string systemRoot);
    void Verify(BootFirmware firmware, string systemRoot);
}

public static class BootRepairEngine
{
    public static void Register() => PlanRunner.AdvancedSteps[DiskStepKind.RebuildBootFiles] = Run;
    private static int Run(DiskPlan plan, DiskStep step, StatusWriter status)
    {
        try
        {
            var options = JsonSerializer.Deserialize<BootRepairOptions>(step.Options ?? "{}", PipeProtocol.Json) ?? throw new ArgumentException("Missing repair options.");
            var disks = StorageInventory.GetDisks();
            var source = disks.SingleOrDefault(d => d.Number == plan.Source?.Number) ?? throw new ArgumentException("The Windows source disk is missing.");
            var target = disks.SingleOrDefault(d => d.Number == plan.Target.Number) ?? throw new ArgumentException("The boot destination disk is missing.");
            return Execute(plan, source, target, options, new Platform(plan, source, target), status, SafetyMode.Current);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        { status.Write(DiskProgressState.Refused, ex.Message); return DiskWorkerExit.BadArguments; }
    }

    internal static int Execute(DiskPlan plan, DiskInfo source, DiskInfo target, BootRepairOptions options, IBootRepairPlatform platform, StatusWriter status, bool virtualOnly)
    {
        try
        {
            using (platform)
            {
                var partition = BootRepairValidation.Validate(plan, source, target, options, virtualOnly);
                platform.CheckWindowsAndTools(options);
                var root = platform.SystemRoot(partition);
                status.Write(DiskProgressState.Progress, "Rebuilding boot files on the selected partition", 0.3);
                platform.Rebuild(options, root);
                platform.Verify(options.Firmware, root);
                status.Write(DiskProgressState.Progress, "Boot configuration files are readable. Restarting from this disk remains to be verified.", 0.95);
                return DiskWorkerExit.Ok;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            StorageOperations.StorageOperationException or Microsoft.Management.Infrastructure.CimException)
        { status.Write(DiskProgressState.Failed, "Boot repair stopped: " + ex.Message + " Existing boot files may be incomplete if writing had started."); return DiskWorkerExit.Failed; }
    }

    private sealed class Platform(DiskPlan plan, DiskInfo source, DiskInfo target) : IBootRepairPlatform
    {
        private StorageOperations? _ops;
        private (int Partition, string Path)? _temporary;
        private void CheckDisks()
        {
            var disks = StorageInventory.GetDisks();
            var src = disks.SingleOrDefault(d => d.Number == source.Number);
            var dst = disks.SingleOrDefault(d => d.Number == target.Number);
            if (src is null || dst is null || Fingerprint.Mismatch(plan.Source!, src) is not null || Fingerprint.Mismatch(plan.Target, dst) is not null ||
                dst.IsOffline || dst.IsReadOnly || (SafetyMode.Current && (!src.IsVirtual || !dst.IsVirtual))) throw new IOException("A selected disk changed; boot repair stopped.");
        }
        public void CheckWindowsAndTools(BootRepairOptions options)
        {
            CheckDisks();
            if (!File.Exists(Path.Combine(options.SourceRoot, @"Windows\System32\config\SYSTEM"))) throw new IOException("The chosen source does not contain readable Windows account/system data.");
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "bcdboot.exe")) || (options.Firmware == BootFirmware.Bios && !File.Exists(Path.Combine(Environment.SystemDirectory, "bootsect.exe"))))
                throw new IOException("The Windows boot preparation tools are unavailable. Nothing was written.");
        }
        public string SystemRoot(PartitionInfo partition)
        {
            CheckDisks();
            if (partition.Root is { } root) return root;
            var letter = "TRSUVWXYZQP".FirstOrDefault(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c));
            if (letter == default) throw new IOException("No temporary drive letter is available.");
            var path = $"{letter}:\\";
            _ops = new StorageOperations();
            _ops.AddAccessPath(target.Number, partition.PartitionNumber, path);
            _temporary = (partition.PartitionNumber, path);
            return path;
        }
        public void Rebuild(BootRepairOptions options, string systemRoot)
        {
            CheckDisks();
            var bcd = Path.Combine(systemRoot, options.Firmware == BootFirmware.Uefi ? @"EFI\Microsoft\Boot\BCD" : @"Boot\BCD");
            if (File.Exists(bcd)) File.Copy(bcd, bcd + ".dpt-backup-" + Guid.NewGuid().ToString("N"));
            Exec("bcdboot.exe", BootRepairPlanning.Arguments(options, systemRoot));
            if (options.Firmware == BootFirmware.Bios) { CheckDisks(); Exec("bootsect.exe", ["/nt60", systemRoot.TrimEnd('\\'), "/mbr"]); }
        }
        public void Verify(BootFirmware firmware, string systemRoot)
        {
            CheckDisks();
            using var bcd = File.OpenRead(Path.Combine(systemRoot, firmware == BootFirmware.Uefi ? @"EFI\Microsoft\Boot\BCD" : @"Boot\BCD"));
            if (bcd.Length == 0) throw new IOException("The destination boot configuration is empty.");
        }
        private static void Exec(string name, IReadOnlyList<string> arguments)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, name)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi) ?? throw new IOException("Could not start " + name);
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var text = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new IOException($"{name} failed ({process.ExitCode}): {text[^Math.Min(text.Length, 1200)..]}");
        }
        public void Dispose()
        {
            try { if (_temporary is { } temporary) { CheckDisks(); _ops!.RemoveAccessPath(target.Number, temporary.Partition, temporary.Path); } }
            finally { _ops?.Dispose(); }
        }
    }
}
