using Dpt.App.Diagnostics;
using Dpt.App.Services;

namespace Dpt.App.Tests;

public class LifecycleTests
{
    [Fact]
    public void Write_jobs_hold_the_app_open_and_shutdown_atomically_rejects_new_writes()
    {
        var activity = new ActivityHub();
        var scan = activity.Start("Read-only scan");
        var copy = activity.Start("Copy", keepsAppRunning: true);
        var batch = activity.Start("Rename batch", keepsAppRunning: true);
        Assert.True(activity.HasWorkInProgress);
        Assert.False(activity.TryBeginShutdown());
        copy.Complete(); copy.Complete();
        Assert.True(activity.HasWorkInProgress);
        batch.Complete();
        Assert.False(activity.HasWorkInProgress);
        Assert.True(scan.IsRunning); // read-only indexing can stop when the app exits
        Assert.True(activity.TryBeginShutdown());
        Assert.Throws<InvalidOperationException>(() => activity.Start("Late write", keepsAppRunning: true));
        Assert.False(activity.Start("Late scan").IsRunning);
    }

    [Fact]
    public void Crash_reporting_falls_back_when_the_app_data_path_is_unusable()
    {
        using var box = new Sandbox();
        var blocked = box.File("not-a-directory", "x");
        var error = new InvalidOperationException("outer failure", new IOException("inner detail"));
        var report = CrashReporter.Write(Path.Combine(blocked, "logs"), box.Dir("fallback"), error, "Startup");
        Assert.NotNull(report);
        var text = File.ReadAllText(report);
        Assert.Contains("Context: Startup", text);
        Assert.Contains("Version:", text);
        Assert.Contains("outer failure", text);
        Assert.Contains("inner detail", text);
        Assert.StartsWith(Path.Combine(box.Root, "fallback"), report);
    }
}
