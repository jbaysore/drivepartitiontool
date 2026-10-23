using System.Diagnostics;
using System.Text.Json;
using Dpt.Data;

namespace Dpt.App.Diagnostics;

/// <summary>Opt-in timings from the real startup and folder view paths. No features are disabled for measurement.</summary>
internal static class PerformanceLog
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DPT_MEASURE_PERF") == "1";

    public static void Startup()
    {
        if (!Enabled) return;
        using var process = Process.GetCurrentProcess();
        Write(new { kind = "startup", milliseconds = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds, active = System.Windows.Application.Current.MainWindow?.IsActive });
    }

    public static void Folder(long started, double enumerationMs, double applicationMs, string path, int count)
    {
        if (!Enabled) return;
        Write(new { kind = "folder", milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds, enumerationMs, applicationMs, path, count, active = System.Windows.Application.Current.MainWindow?.IsActive });
    }

    private static void Write(object entry)
    {
        try
        {
            var folder = Path.Combine(AppDatabase.DefaultDataDirectory, "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "performance.jsonl"), JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Performance report", ex); }
    }
}
