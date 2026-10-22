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
    }
}
