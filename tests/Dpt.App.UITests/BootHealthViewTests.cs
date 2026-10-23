using FlaUI.Core.AutomationElements;

namespace Dpt.App.UITests;

[Trait("Category", "UI")]
public class BootHealthViewTests
{
    [Fact]
    public void Boot_health_shows_available_information_and_a_disabled_repair_preview()
    {
        using var session = new AppSession(arguments: "--open DiskTools");
        session.Get("BootHealthTask").AsButton().Invoke();
        var dialog = session.WaitFor(() => session.Window.ModalWindows.Concat(session.App.GetAllTopLevelWindows(session.Automation))
            .FirstOrDefault(w => w.AutomationId == "BootHealthWindow"));
        AutomationElement? Find(string id) => dialog.FindFirstDescendant(session.Cf.ByAutomationId(id));
        session.WaitUntil(() => Find("BootHealthSummary")?.Name.Contains("Windows is running") == true, "boot disk summary");
        Assert.False(string.IsNullOrWhiteSpace(Find("BootConfigurationNote")!.Name));
        Assert.NotNull(Find("BootRepairSource"));
        Assert.NotNull(Find("BootRepairDestination"));
        Assert.False(Find("BootRepairRun")!.IsEnabled);
        if (Environment.GetEnvironmentVariable("DPT_BOOT_SCREENSHOT") is { Length: > 0 } screenshot)
            FlaUI.Core.Capturing.Capture.Element(dialog).ToFile(screenshot);
        dialog.Close();
        session.WaitUntil(() => session.Window.IsEnabled, "owner re-enabled after closing boot health");
    }
}
