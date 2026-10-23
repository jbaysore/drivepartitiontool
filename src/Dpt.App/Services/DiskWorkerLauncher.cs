using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Dpt.Data;
using Dpt.Ipc;

namespace Dpt.App.Services;

public sealed record DiskRunResult(int ExitCode, IReadOnlyList<DiskProgress> Progress, bool Cancelled)
{
    public bool Succeeded => ExitCode == DiskWorkerExit.Ok && !Cancelled;
    public string Summary => Cancelled ? "Cancelled at the administrator prompt. Nothing was changed."
        : Progress.LastOrDefault(p => p.State is DiskProgressState.Succeeded or DiskProgressState.Failed or DiskProgressState.Refused)?.Message
          ?? (ExitCode == 0 ? "Done." : $"The disk worker stopped (code {ExitCode}).");
}

/// <summary>Runs a disk plan in the elevated worker (one UAC prompt) and follows its progress file.</summary>
public sealed class DiskWorkerLauncher(AppServices services)
{
    public static string Folder => Path.Combine(AppDatabase.DefaultDataDirectory, "diskops");

    public static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "diskworker", "DrivePartitionTool.DiskWorker.exe");

    /// <summary>Debug builds of the worker only ever change virtual disks.</summary>
    public static bool IsDevelopmentBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
    public static bool VirtualOnly => IsDevelopmentBuild || string.Equals(Environment.GetEnvironmentVariable("DPT_SAFETY"), "virtual-only", StringComparison.OrdinalIgnoreCase);

    public static string NewStatusFile(string id) => Path.Combine(Folder, id + ".status.jsonl");

    public async Task<DiskRunResult> RunAsync(DiskPlan plan, IProgress<DiskProgress>? progress = null)
    {
        Directory.CreateDirectory(Folder);
        var planFile = Path.Combine(Folder, plan.Id + ".plan.json");
        await File.WriteAllTextAsync(planFile, JsonSerializer.Serialize(plan, PipeProtocol.Json));
        var activity = services.Activity.Start(plan.Title, keepsAppRunning: true);
        var seen = new List<DiskProgress>();
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(WorkerPath, $"--plan \"{planFile}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            activity.Complete("Cancelled at the administrator prompt.");
            return new DiskRunResult(-1, [], Cancelled: true);
        }

        if (process is null)
        {
            activity.Complete("The disk worker didn't start.", error: true);
            return new DiskRunResult(-1, [], false);
        }

        using (process)
        {
            long position = 0;
            while (true)
            {
                var exited = process.WaitForExit(250);
                position = ReadNew(plan.StatusFile, position, seen, p =>
                {
                    progress?.Report(p);
                    activity.Detail = p.Message;
                    if (p.Fraction > 0) activity.Progress = p.Fraction;
                });
                if (exited) break;
                await Task.Delay(250);
            }

            ReadNew(plan.StatusFile, position, seen, p => progress?.Report(p));
            var result = new DiskRunResult(process.ExitCode, seen, false);
            activity.Complete(result.Summary, error: !result.Succeeded);
            services.Drives.Refresh();
            return result;
        }
    }

    private static long ReadNew(string file, long position, List<DiskProgress> seen, Action<DiskProgress> onLine)
    {
        if (!File.Exists(file)) return position;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Position = position;
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            try
            {
                var p = JsonSerializer.Deserialize<DiskProgress>(line, PipeProtocol.Json);
                if (p is null) continue;
                seen.Add(p);
                onLine(p);
            }
            catch (JsonException)
            {
            }
        }

        return fs.Position;
    }
}
