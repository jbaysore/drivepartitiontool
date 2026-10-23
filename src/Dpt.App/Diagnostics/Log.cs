using Dpt.Data;

namespace Dpt.App.Diagnostics;

/// <summary>Minimal append-only log at %LOCALAPPDATA%\DrivePartitionTool\logs\app-YYYYMMDD.log.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Folder = Path.Combine(AppDatabase.DefaultDataDirectory, "logs");

    public static string CurrentFile => Path.Combine(Folder, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Error(string area, Exception ex) => Write("ERROR", area, ex.ToString());

    public static void Error(string area, string message) => Write("ERROR", area, message);

    public static string? WriteCrashReport(Exception exception, string context)
        => CrashReporter.Write(Folder, Path.Combine(Path.GetTempPath(), "DrivePartitionTool", "crash-reports"), exception, context);

    private static void Write(string level, string area, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(CurrentFile, $"{DateTime.Now:O} {level} [{area}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}

/// <summary>Writes a standalone report even when the normal app-data location is unavailable.</summary>
public static class CrashReporter
{
    public static string? Write(string folder, string fallbackFolder, Exception exception, string context)
    {
        var detail = $"DrivePartitionTool crash report\nUTC: {DateTime.UtcNow:O}\nVersion: {typeof(App).Assembly.GetName().Version}\n" +
            $"Process: {Environment.ProcessId}\nOS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
            $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\nContext: {context}\n\n{exception}\n";
        foreach (var target in new[] { folder, fallbackFolder }.Distinct(StringComparer.OrdinalIgnoreCase))
            try
            {
                Directory.CreateDirectory(target);
                var path = Path.Combine(target, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
                File.WriteAllText(path, detail);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException) { }
        return null;
    }
}
