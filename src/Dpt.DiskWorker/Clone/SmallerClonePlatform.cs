using System.Diagnostics;
using System.Text;
using Dpt.Core.WindowsCopy;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.Clone;

/// <summary>Privileged implementation; inert until called by the validated clone workflow. Never used by non-admin tests.</summary>
internal sealed class SmallerClonePlatform(DiskInfo source, DiskInfo target, CloneOptions options, StatusWriter status) : ISmallerClonePlatform
{
    private StorageOperations? _ops;
    private DiskFingerprint _expected = Fingerprint.Of(target);
    private DiskFingerprint? _stageDisk;
    private string? _workspace;
    private readonly Dictionary<int, (ShadowCopy Shadow, string Link, string Image)> _images = [];
    private readonly Dictionary<int, int> _partitions = [];
    private readonly List<(int Partition, string Root)> _letters = [];
    private readonly List<string> _files = [];
    private bool _offline;

    private DiskInfo Destination()
    {
        var disk = StorageInventory.GetDisks().SingleOrDefault(d => d.Number == target.Number) ?? throw new IOException("The destination disconnected.");
        if (Fingerprint.Mismatch(_expected, disk) is not null || disk.IsBoot || disk.IsSystem || disk.IsReadOnly || SafetyMode.Current && !disk.IsVirtual)
            throw new IOException("The destination identity, layout or protection changed.");
        return disk;
    }
    private void Source()
    {
        var disk = StorageInventory.GetDisks().SingleOrDefault(d => d.Number == source.Number);
        if (disk is null || Fingerprint.Mismatch(Fingerprint.Of(source), disk) is not null || SafetyMode.Current && !disk.IsVirtual)
            throw new IOException("The source disconnected or its identity/layout changed.");
    }
    private void Write(Action<StorageOperations> action)
    {
        Destination(); action(_ops ??= new StorageOperations());
        var now = StorageInventory.GetDisks().Single(d => d.Number == target.Number);
        if (now.Size != target.Size || now.IsBoot || now.IsSystem || SafetyMode.Current && !now.IsVirtual) throw new IOException("The destination changed while writing.");
        _expected = Fingerprint.Of(now);
    }

    private void Stage(long needed)
    {
        var resolved = LocalVolumeFolder.Resolve(_workspace ?? options.TemporaryImageFolder!);
        var disk = LocalVolumeFolder.DiskOf(resolved, StorageInventory.GetDisks());
        ValidateStage(disk, source.Number, target.Number, SafetyMode.Current);
        if (_stageDisk is not null && Fingerprint.Mismatch(_stageDisk, disk!) is not null) throw new IOException("The staging disk changed.");
        _stageDisk ??= Fingerprint.Of(disk!);
        var part = disk!.Partitions.Single(p => p.VolumePath is { } root && resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        if (part.FreeSpace is not { } free || free < needed) throw new IOException("The staging volume needs more free space for the uncompressed transfer images.");
    }

    internal static void ValidateStage(DiskInfo? disk, int source, int target, bool virtualOnly)
    {
        if (disk is null || disk.Number == source || disk.Number == target || disk.IsOffline || disk.IsReadOnly || virtualOnly && !disk.IsVirtual)
            throw new ArgumentException("Choose a writable folder on a third local disk (also virtual in development/test mode). The source is kept unchanged and the destination will be erased.");
    }

    public IReadOnlyDictionary<int, long> SnapshotAndMeasure(CloneLayout layout)
    {
        Source(); Destination(); Stage(layout.StagingBytes);
        foreach (var tool in new[] { "dism.exe" }.Concat(options.MakeBootable ? BootFiles.ToolsFor(source) : []))
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, tool))) throw new IOException(tool + " is unavailable; the destination was not erased.");
        var neededLetters = layout.Partitions.Count(p => p.Capture) + (options.MakeBootable ? 1 : 0);
        if ("RSTUVWXYZQPONMLKJIHGFED".Count(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c)) < neededLetters)
            throw new IOException("Not enough temporary drive letters are available.");
        var staging = LocalVolumeFolder.Resolve(options.TemporaryImageFolder!);
        _workspace = Path.Combine(staging, "dpt-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        var sizes = new Dictionary<int, long>();
        foreach (var part in layout.Partitions.Where(p => p.Capture))
        {
            Source(); Stage(0);
            var shadow = ShadowCopy.Create(part.Source.VolumePath!);
            var link = Path.Combine(_workspace, "volume-" + part.Source.PartitionNumber);
            var image = Path.Combine(_workspace, "volume-" + part.Source.PartitionNumber + ".wim");
            _images.Add(part.Source.PartitionNumber, (shadow, link, image)); // track the shadow before creating a link that can fail
            Directory.CreateSymbolicLink(link, shadow.Device + "\\");
            long bytes = 0;
            void Walk(string folder)
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    var relative = Path.GetRelativePath(link, entry.FullName);
                    if (WindowsContentClassifier.AlwaysExcludedNames.Any(name => WindowsCopyPlanning.Under(relative, name))) continue;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo directory) Walk(directory.FullName);
                    else if (entry is FileInfo file) bytes = checked(bytes + file.Length);
                }
            }
            Walk(link); sizes.Add(part.Source.PartitionNumber, bytes);
        }
        return sizes;
    }

    public void Capture(CloneLayout layout)
    {
        Stage(layout.StagingBytes); Source(); Destination();
        var config = Path.Combine(_workspace!, "exclusions.ini"); _files.Add(config);
        File.WriteAllText(config, WindowsCopyPlanning.ExclusionConfig(new() { SourceRoot = @"X:\" }), Encoding.Unicode);
        foreach (var part in layout.Partitions.Where(p => p.Capture))
        {
            Stage(0); Source();
            var image = _images[part.Source.PartitionNumber]; _files.Add(image.Image);
            status.Write(DiskProgressState.Progress, $"Capturing partition {part.Source.PartitionNumber} before erasing the destination", 0.1);
            Exec("dism.exe", "/English", "/Capture-Image", "/ImageFile:" + image.Image, "/CaptureDir:" + image.Link,
                "/Name:DrivePartitionTool clone", "/ConfigFile:" + config, "/Compress:none", "/CheckIntegrity", "/Verify", "/EA");
        }
    }

    public void Prepare(CloneLayout layout)
    {
        Stage(0); Source(); Destination();
        status.Write(DiskProgressState.Progress, "Transfer images are complete. Replacing the destination layout", 0.45);
        Write(ops => ops.ClearAndInitialize(target.Number, source.PartitionStyle));
        foreach (var part in layout.Partitions)
        {
            int number = 0;
            Write(ops => number = ops.CreatePartitionExact(target.Number, part.Offset, part.Size, source.PartitionStyle == "GPT" ? part.Source.GptType : null,
                source.PartitionStyle == "MBR" ? part.Source.MbrType : null, part.Source.IsActive));
            _partitions.Add(part.Source.PartitionNumber, number);
        }
    }

    public void CopyRaw(CloneLayout layout)
    {
        Source(); Write(ops => ops.SetOffline(target.Number, true)); _offline = true;
        try
        {
            Destination(); using var src = RawIo.Open(RawIo.PhysicalDrive(source.Number), false);
            using var dst = RawIo.Open(RawIo.PhysicalDrive(target.Number), true);
            var buffer = new byte[4 * 1024 * 1024];
            foreach (var part in layout.Partitions.Where(p => !p.Capture))
            {
                Source(); Destination();
                RawIo.Copy(src, part.Source.Offset, dst, part.Offset, part.Source.Size, buffer);
            }
        }
        finally { Write(ops => ops.SetOffline(target.Number, false)); _offline = false; }
    }

    public void Apply(CloneLayout layout)
    {
        var wait = Stopwatch.StartNew();
        while (layout.Partitions.Where(p => p.Capture).Any(p => Destination().Partitions.Single(n => n.PartitionNumber == _partitions[p.Source.PartitionNumber]).VolumePath is not { Length: > 0 }))
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException("The new data volumes did not become ready for formatting.");
            Thread.Sleep(250);
        }
        foreach (var part in layout.Partitions.Where(p => p.Capture))
        {
            Stage(0); var number = _partitions[part.Source.PartitionNumber];
            Write(ops => ops.Format(target.Number, number, "NTFS", part.Source.Label ?? ""));
            var letter = "RSTUVWXYZQPONMLKJIHGFED".First(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c));
            var root = $"{letter}:\\";
            Write(ops => ops.AddAccessPath(target.Number, number, root)); _letters.Add((number, root));
            Destination(); status.Write(DiskProgressState.Progress, $"Applying partition {part.Source.PartitionNumber}", 0.65);
            Exec("dism.exe", "/English", "/Apply-Image", "/ImageFile:" + _images[part.Source.PartitionNumber].Image, "/Index:1", "/ApplyDir:" + root, "/CheckIntegrity", "/Verify", "/EA");
        }
    }
    public void Boot()
    {
        Destination(); var error = BootFiles.RebuildIfWindows(_ops!, target.Number, source.Partitions.Select(p => (p, _partitions[p.PartitionNumber])).ToList(), status);
        if (error is not null) throw new IOException(error);
    }
    public void Verify(CloneLayout layout)
    {
        var disk = Destination();
        foreach (var part in layout.Partitions)
        {
            var actual = disk.Partitions.Single(p => p.PartitionNumber == _partitions[part.Source.PartitionNumber]);
            if (actual.Offset != part.Offset || actual.Size != part.Size || part.Capture && !string.Equals(actual.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The destination layout/filesystem does not match the planned clone.");
        }
    }

    private static void Exec(string name, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, name)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new IOException("Could not start " + name);
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); process.WaitForExit();
        var text = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException($"{name} failed ({process.ExitCode}): {text[^Math.Min(1200, text.Length)..]}");
    }

    public void Dispose()
    {
        void Clean(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or StorageOperations.StorageOperationException or Microsoft.Management.Infrastructure.CimException or System.ComponentModel.Win32Exception)
            { status.Write(DiskProgressState.Progress, "Clone cleanup needs attention: " + ex.Message); }
        }
        if (_offline) Clean(() => Write(ops => ops.SetOffline(target.Number, false)));
        foreach (var (partition, root) in _letters) Clean(() => Write(ops => ops.RemoveAccessPath(target.Number, partition, root)));
        foreach (var image in _images.Values)
        {
            Clean(() => { Stage(0); if (new DirectoryInfo(image.Link).LinkTarget is not null) Directory.Delete(image.Link); });
            Clean(image.Shadow.Dispose);
        }
        foreach (var file in _files) Clean(() => { Stage(0); File.Delete(file); });
        if (_workspace is not null) Clean(() => { Stage(0); Directory.Delete(_workspace); }); // nonrecursive; never follows a link or removes unrelated files
        _ops?.Dispose();
    }
}
