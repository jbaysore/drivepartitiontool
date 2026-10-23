using System.Diagnostics;

namespace Dpt.App.UITests;

[Trait("Category", "UI")]
public class CrashHandlingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unexpected_startup_and_dispatcher_errors_write_a_report_and_exit(bool startupFailure)
    {
        var root = Path.Combine(Path.GetTempPath(), "dpt-crash-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var data = Path.Combine(root, "data");
        if (startupFailure) File.WriteAllText(data, "This file deliberately prevents creating the data directory.");
        var psi = new ProcessStartInfo(AppSession.ExePath, "--diagnostics-test-crash") { UseShellExecute = false };
        psi.Environment["DPT_DATA_DIR"] = data;
        psi.Environment["DPT_CATALOG"] = "off";
        psi.Environment["DPT_SAFETY"] = "virtual-only";
        using var process = Process.Start(psi)!;
        try
        {
            var exited = process.WaitForExit(15000);
            if (!exited) process.Kill();
            Assert.True(exited, "The failing test app did not exit; no crash dialog should block unattended testing.");
            Assert.NotEqual(0, process.ExitCode);
            var reportFolder = startupFailure ? Path.Combine(Path.GetTempPath(), "DrivePartitionTool", "crash-reports") : Path.Combine(data, "logs");
            var reports = Directory.GetFiles(reportFolder, $"crash-*-{process.Id}-*.txt");
            var report = Assert.Single(reports);
            var text = File.ReadAllText(report);
            Assert.Contains(startupFailure ? "Context: Startup" : "Intentional diagnostics test exception", text);
            Assert.Contains("Version:", text);
            File.Delete(report); // this test's own report, not another app's log
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            Directory.Delete(root, recursive: true);
        }
    }
}
