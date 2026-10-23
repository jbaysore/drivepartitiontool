using System.Diagnostics;
using System.Text.Json;
using Dpt.DiskWorker;
using Dpt.Ipc;
using Dpt.Storage;
using Dpt.TestSupport;

namespace Dpt.DiskWorker.IntegrationTests;

internal static class Plans
{
    public static string StatusFile() => Path.Combine(Path.GetTempPath(), "dpt-diskworker-tests", Guid.NewGuid().ToString("N") + ".jsonl");

    public static DiskPlan For(DiskInfo disk, params DiskStep[] steps) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = "Test plan",
        Target = Fingerprint.Of(disk),
        Steps = steps,
        StatusFile = StatusFile(),
    };

    public static string WorkerExe
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrivePartitionTool.sln"))) dir = dir.Parent;
#if DEBUG
            const string config = "Debug";
#else
            const string config = "Release";
#endif
            return Path.Combine(dir!.FullName, "src", "Dpt.DiskWorker", "bin", config, "net10.0-windows", "DrivePartitionTool.DiskWorker.exe");
        }
    }

    /// <summary>Runs the real worker executable (the test process is already elevated).</summary>
    public static (int Code, List<DiskProgress> Progress) RunWorker(DiskPlan plan)
    {
        var file = Path.Combine(Path.GetTempPath(), "dpt-diskworker-tests", plan.Id + ".plan.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(plan, PipeProtocol.Json));
        var psi = new ProcessStartInfo(WorkerExe, $"--plan \"{file}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.Environment["DPT_SAFETY"] = "virtual-only";
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(TimeSpan.FromMinutes(5));
        var progress = File.Exists(plan.StatusFile)
            ? File.ReadAllLines(plan.StatusFile).Select(l => JsonSerializer.Deserialize<DiskProgress>(l, PipeProtocol.Json)!).ToList()
            : [];
        return (p.ExitCode, progress);
    }
}

/// <summary>Refusals happen before any write, so these run without administrator rights.</summary>
public class DiskWorkerRefusalTests
{
    private static DiskInfo SystemDisk() => StorageInventory.GetDisks().First(d => d.IsBoot || d.IsSystem);

    [Fact]
    public void Refuses_a_physical_disk_in_virtual_only_mode()
    {
        var plan = Plans.For(SystemDisk(), new DiskStep { Kind = DiskStepKind.Probe });
        using var status = new StatusWriter(plan.StatusFile);
        var code = new PlanRunner(plan, status, virtualOnly: true).Run();
        Assert.Equal(DiskWorkerExit.RefusedSafety, code);
        Assert.Contains(status.Written, p => p.State == DiskProgressState.Refused && p.Message.Contains("not a virtual disk"));
    }

    [Fact]
    public void Refuses_when_confirmation_text_does_not_match()
    {
        var plan = Plans.For(SystemDisk(), new DiskStep { Kind = DiskStepKind.Probe }) with { ExpectedConfirmation = "DISK 9", Confirmation = "disk 8" };
        using var status = new StatusWriter(plan.StatusFile);
        Assert.Equal(DiskWorkerExit.RefusedConfirmation, new PlanRunner(plan, status, virtualOnly: true).Run());
    }

    [Fact]
    public void Refuses_when_the_disk_changed_since_the_preview()
    {
        var disk = SystemDisk();
        var plan = Plans.For(disk, new DiskStep { Kind = DiskStepKind.Probe });
        plan = plan with { Target = plan.Target with { LayoutHash = "0000" } };
        using var status = new StatusWriter(plan.StatusFile);
        // virtualOnly: false so the fingerprint check (not the safety check) is what refuses. Probe is read-only anyway.
        Assert.Equal(DiskWorkerExit.RefusedFingerprint, new PlanRunner(plan, status, virtualOnly: false).Run());
        Assert.Contains(status.Written, p => p.Message.Contains("partitions changed"));
    }

    // Protection rules are tested as a pure function on the real (read-only) inventory, so no test can ever reach a
    // write on this PC's disks even if the rules had a bug.

    [Fact]
    public void Protection_refuses_the_windows_partition_even_when_unlocked()
    {
        var disk = SystemDisk();
        var windows = disk.Partitions.First(p => p.Role == PartitionRole.Windows);
        foreach (var kind in new[] { DiskStepKind.FormatVolume, DiskStepKind.DeletePartition, DiskStepKind.ResizePartition, DiskStepKind.SetDriveLetter })
        {
            var plan = Plans.For(disk, new DiskStep { Kind = kind, PartitionNumber = windows.PartitionNumber, FileSystem = "NTFS", Size = 1 }) with { AllowSystemPartitions = true };
            Assert.Contains("running from", PlanRunner.CheckProtection(plan, disk));
        }

        var init = Plans.For(disk, new DiskStep { Kind = DiskStepKind.InitializeDisk }) with { AllowSystemPartitions = true };
        Assert.NotNull(PlanRunner.CheckProtection(init, disk));
    }

    [Fact]
    public void Protection_refuses_boot_partitions_unless_unlocked()
    {
        var disk = SystemDisk();
        var efi = disk.Partitions.FirstOrDefault(p => p.Role is PartitionRole.Efi or PartitionRole.Recovery);
        if (efi is null) return; // nothing protected on this machine's boot disk
        var plan = Plans.For(disk, new DiskStep { Kind = DiskStepKind.SetLabel, PartitionNumber = efi.PartitionNumber, Label = "X" });
        Assert.Contains("needed to start Windows", PlanRunner.CheckProtection(plan, disk));
        Assert.Null(PlanRunner.CheckProtection(plan with { AllowSystemPartitions = true }, disk));
    }

    [Fact]
    public void Probe_only_plans_pass_protection()
    {
        var disk = SystemDisk();
        Assert.Null(PlanRunner.CheckProtection(Plans.For(disk, new DiskStep { Kind = DiskStepKind.Probe }), disk));
    }
}

/// <summary>M7 exit criterion: every partition operation on a test VHD, run through the real elevated worker.</summary>
[Trait("Category", "Elevated")]
public class PartitionOperationTests
{
    private static DiskInfo Disk(int number) => StorageInventory.GetDisks().Single(d => d.Number == number);

    private static void Ok((int Code, List<DiskProgress> Progress) r) =>
        Assert.True(r.Code == DiskWorkerExit.Ok, $"Worker exit {r.Code}: {string.Join(" | ", r.Progress.Select(p => $"{p.State}: {p.Message}"))}");

    [Fact]
    public void Shrink_create_format_letter_label_delete_extend()
    {
        using var vhd = TestVhd.Create(1024, "NTFS", "DPTPART");
        var disk = Disk(vhd.DiskNumber);
        var data = disk.Partitions.Single(p => p.DriveLetter == vhd.Letter);
        var originalSize = data.Size;
        const long mb = 1024 * 1024;

        // Shrink the data partition to 400 MB.
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.ResizePartition, PartitionNumber = data.PartitionNumber, Size = 400 * mb })));
        disk = Disk(vhd.DiskNumber);
        Assert.Equal(400 * mb, disk.Partitions.Single(p => p.PartitionNumber == data.PartitionNumber).Size);
        Assert.Contains(disk.Unallocated, u => u.Size > 500 * mb);

        // Create an exFAT partition in the freed space.
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.CreatePartition, Size = 300 * mb, FileSystem = "exFAT", Label = "DPTNEW", AutoLetter = true })));
        disk = Disk(vhd.DiskNumber);
        var created = disk.Partitions.Single(p => p.Label == "DPTNEW");
        Assert.Equal("exFAT", created.FileSystem);
        Assert.NotNull(created.DriveLetter);

        // Rename it, reformat as NTFS, move its letter, then remove the letter.
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.SetLabel, PartitionNumber = created.PartitionNumber, Label = "RENAMED" })));
        disk = Disk(vhd.DiskNumber);
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.FormatVolume, PartitionNumber = created.PartitionNumber, FileSystem = "NTFS", Label = "REFORMAT" })));
        disk = Disk(vhd.DiskNumber);
        Assert.Equal("NTFS", disk.Partitions.Single(p => p.PartitionNumber == created.PartitionNumber).FileSystem);
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = "QRSTUVWXYZ".First(c => !used.Contains(c));
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.SetDriveLetter, PartitionNumber = created.PartitionNumber, DriveLetter = free })));
        disk = Disk(vhd.DiskNumber);
        Assert.Equal(free, disk.Partitions.Single(p => p.PartitionNumber == created.PartitionNumber).DriveLetter);
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.SetDriveLetter, PartitionNumber = created.PartitionNumber, DriveLetter = null })));
        disk = Disk(vhd.DiskNumber);
        Assert.Null(disk.Partitions.Single(p => p.PartitionNumber == created.PartitionNumber).DriveLetter);

        // Delete it and grow the data partition back to its original size.
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.DeletePartition, PartitionNumber = created.PartitionNumber })));
        disk = Disk(vhd.DiskNumber);
        Assert.DoesNotContain(disk.Partitions, p => p.Label == "REFORMAT");
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.ResizePartition, PartitionNumber = data.PartitionNumber, Size = originalSize })));
        Assert.Equal(originalSize, Disk(vhd.DiskNumber).Partitions.Single(p => p.PartitionNumber == data.PartitionNumber).Size);
    }

    [Fact]
    public void Worker_refuses_a_plan_whose_disk_changed()
    {
        using var vhd = TestVhd.Create(300);
        var stale = Plans.For(Disk(vhd.DiskNumber), new DiskStep { Kind = DiskStepKind.CreatePartition, Size = 50 * 1024 * 1024 });
        // Change the layout behind the plan's back (shrink via a separate, fresh plan).
        var data = Disk(vhd.DiskNumber).Partitions.Single(p => p.DriveLetter == vhd.Letter);
        Ok(Plans.RunWorker(Plans.For(Disk(vhd.DiskNumber), new DiskStep { Kind = DiskStepKind.ResizePartition, PartitionNumber = data.PartitionNumber, Size = 150 * 1024 * 1024 })));
        var r = Plans.RunWorker(stale);
        Assert.Equal(DiskWorkerExit.RefusedFingerprint, r.Code);
    }

    [Fact]
    public void Initialize_a_blank_disk()
    {
        using var vhd = TestVhd.Create(200);
        var disk = Disk(vhd.DiskNumber);
        // Delete the only data partition, then add a new one: proves create on an emptied disk works too.
        var data = disk.Partitions.Single(p => p.DriveLetter == vhd.Letter);
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.DeletePartition, PartitionNumber = data.PartitionNumber })));
        disk = Disk(vhd.DiskNumber);
        Ok(Plans.RunWorker(Plans.For(disk, new DiskStep { Kind = DiskStepKind.CreatePartition, FileSystem = "FAT32", Label = "BLANK", AutoLetter = true })));
        Assert.Contains(Disk(vhd.DiskNumber).Partitions, p => p.Label == "BLANK" && p.FileSystem == "FAT32");
    }
}
