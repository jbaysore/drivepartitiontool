using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dpt.DiskWorker.Clone;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.DiskWorker.Windows;

public sealed record WindowsCopyOptions
{
    /// <summary>Root of the Windows volume to copy, e.g. "C:\".</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Paths relative to <see cref="SourceRoot"/> to leave out (from the app's classifier and the owner's choices).</summary>
    public List<string> Exclusions { get; init; } = [];

    public bool MakeBootable { get; init; } = true;

    public int EfiSizeMb { get; init; } = 260;
}

/// <summary>
/// "Copy Windows to another drive": builds a fresh Windows layout on the destination (EFI + MSR + Windows, or an active
/// MBR partition on BIOS PCs), captures the chosen content from a Volume Shadow Copy snapshot of the source with DISM
/// (which keeps permissions, hard links, reparse points and alternate streams), applies it, and writes boot files.
/// The source is only read.
/// </summary>
public static partial class WindowsCopyEngine
{
    private const long Mb = 1024 * 1024;

    public static void Register() => PlanRunner.AdvancedSteps[DiskStepKind.WindowsCopy] = Run;

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out int type); // 1 = BIOS, 2 = UEFI

    public static bool IsUefi() => !GetFirmwareType(out var t) || t != 1;

    private static int Run(DiskPlan plan, DiskStep step, StatusWriter status)
    {
        var o = JsonSerializer.Deserialize<WindowsCopyOptions>(step.Options ?? "{}", PipeProtocol.Json)
                ?? throw new InvalidDataException("Missing copy options.");
        var disks = StorageInventory.GetDisks();
        var source = disks.Single(d => d.Number == plan.Source!.Number);
        var sourcePart = source.Partitions.FirstOrDefault(p => string.Equals(p.Root, o.SourceRoot, StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidDataException($"{o.SourceRoot} isn't on the source disk.");
        var target = disks.Single(d => d.Number == plan.Target.Number);
        var uefi = IsUefi();
        var temp = new List<(int Partition, string Path)>();
        using var ops = new StorageOperations();
        string? link = null;
        string? wim = null;
        try
        {
            // 1. Fresh layout.
            status.Write(DiskProgressState.Progress, $"Preparing disk {target.Number} for Windows ({(uefi ? "UEFI/GPT" : "BIOS/MBR")})", 0.03);
            ops.ClearAndInitialize(target.Number, uefi ? "GPT" : "MBR");
            int windowsPart;
            int? efiPart = null;
            if (uefi)
            {
                efiPart = ops.CreatePartitionExact(target.Number, Mb, o.EfiSizeMb * Mb, StorageInventory.GptEfi, null, false);
                ops.Format(target.Number, efiPart.Value, "FAT32", "SYSTEM");
                ops.CreatePartitionExact(target.Number, (1 + o.EfiSizeMb) * Mb, 16 * Mb, StorageInventory.GptMsr, null, false);
                windowsPart = ops.CreatePartition(target.Number, null, null, null, false);
            }
            else
            {
                windowsPart = ops.CreatePartitionExact(target.Number, Mb, target.Size - 2 * Mb, null, 7, isActive: true);
            }

            ops.Format(target.Number, windowsPart, "NTFS", "Windows");
            var destRoot = AssignLetter(ops, target.Number, windowsPart, temp) ?? throw new IOException("Couldn't give the new Windows partition a temporary letter.");

            // 2. Consistent snapshot of the source.
            status.Write(DiskProgressState.Progress, $"Taking a snapshot of {o.SourceRoot}", 0.08);
            using var shadow = ShadowCopy.Create(sourcePart.VolumePath ?? o.SourceRoot);
            link = Path.Combine(Path.GetTempPath(), "dpt-snapshot-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateSymbolicLink(link, shadow.Device + "\\");

            // 3. Capture (into the destination itself, then deleted).
            var config = Path.Combine(Path.GetTempPath(), "dpt-wimscript-" + Guid.NewGuid().ToString("N")[..8] + ".ini");
            var sb = new StringBuilder("[ExclusionList]\r\n");
            foreach (var ex in o.Exclusions.Concat(["System Volume Information", "$Recycle.Bin", "pagefile.sys", "hiberfil.sys", "swapfile.sys"]).Distinct(StringComparer.OrdinalIgnoreCase))
                sb.Append('\\').Append(ex.Trim('\\')).Append("\r\n");
            File.WriteAllText(config, sb.ToString(), Encoding.Unicode);
            wim = Path.Combine(destRoot, "dpt-transfer.wim");
            status.Write(DiskProgressState.Progress, "Collecting the chosen files (this can take a while)", 0.1);
            Dism($"/Capture-Image /ImageFile:\"{wim}\" /CaptureDir:\"{link}\" /Name:\"DrivePartitionTool copy\" /ConfigFile:\"{config}\" /Compress:fast", status, 0.1, 0.55);
            File.Delete(config);

            // 4. Apply.
            status.Write(DiskProgressState.Progress, "Writing Windows onto the new drive", 0.55);
            Dism($"/Apply-Image /ImageFile:\"{wim}\" /Index:1 /ApplyDir:\"{destRoot}\"", status, 0.55, 0.92);
            File.Delete(wim);
            wim = null;

            // 5. Boot files.
            if (o.MakeBootable)
            {
                string system;
                if (uefi) system = AssignLetter(ops, target.Number, efiPart!.Value, temp) ?? throw new IOException("Couldn't give the EFI partition a temporary letter.");
                else system = destRoot;
                status.Write(DiskProgressState.Progress, "Writing boot files", 0.95);
                var (code, output) = Exec("bcdboot.exe", $"{destRoot}Windows /s {system.TrimEnd('\\')} /f {(uefi ? "UEFI" : "BIOS")}");
                if (code != 0) throw new IOException("bcdboot couldn't write the boot files: " + output.Trim());
            }

            status.Write(DiskProgressState.Progress, "Windows copy finished.", 0.99);
            return DiskWorkerExit.Ok;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or StorageOperations.StorageOperationException or Microsoft.Management.Infrastructure.CimException or System.ComponentModel.Win32Exception)
        {
            status.Write(DiskProgressState.Failed, "Copying Windows failed: " + ex.Message + " The source drive was not changed.");
            return DiskWorkerExit.Failed;
        }
        finally
        {
            if (wim is not null)
            {
                try { File.Delete(wim); }
                catch (IOException) { }
            }

            if (link is not null && Directory.Exists(link)) Directory.Delete(link);
            foreach (var (partition, path) in temp)
            {
                try { ops.RemoveAccessPath(target.Number, partition, path); }
                catch (Exception) { }
            }
        }
    }

    [GeneratedRegex(@"(\d{1,3}(?:\.\d)?)%")]
    private static partial Regex Percent();

    private static void Dism(string args, StatusWriter status, double from, double to)
    {
        var psi = new ProcessStartInfo("dism.exe", "/English " + args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var log = new StringBuilder();
        var stderr = p.StandardError.ReadToEndAsync();
        var buf = new char[256];
        var line = new StringBuilder();
        double last = -1;
        int n;
        while ((n = p.StandardOutput.Read(buf, 0, buf.Length)) > 0)
        {
            foreach (var ch in buf.AsSpan(0, n))
            {
                if (ch is '\r' or '\n')
                {
                    var text = line.ToString();
                    line.Clear();
                    if (text.Length == 0) continue;
                    log.AppendLine(text);
                    var m = Percent().Match(text);
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct - last >= 5)
                    {
                        last = pct;
                        status.Write(DiskProgressState.Progress, $"{pct:0}%", from + (to - from) * pct / 100);
                    }
                }
                else
                {
                    line.Append(ch);
                }
            }
        }

        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"DISM failed (code {p.ExitCode}): {Tail(log.ToString() + stderr.Result)}");
    }

    private static string Tail(string s) => s.Length > 600 ? s[^600..] : s;

    private static (int Code, string Output) Exec(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static string? AssignLetter(StorageOperations ops, int disk, int partition, List<(int, string)> temp)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "TRSUVWXYZQP".FirstOrDefault(c => !used.Contains(c));
        if (letter == default) return null;
        var path = $"{letter}:\\";
        ops.AddAccessPath(disk, partition, path);
        temp.Add((partition, path));
        return path;
    }
}
