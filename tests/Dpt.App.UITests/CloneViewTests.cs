using FlaUI.Core.AutomationElements;

namespace Dpt.App.UITests;

[Trait("Category", "UI")]
public class CloneViewTests
{
    [Fact]
    public void Clone_preview_opens_without_a_runnable_plan()
    {
        using var session = new AppSession(arguments: "--open DiskTools");
        var button = session.Get("CloneDiskTask").AsButton(); Assert.True(button.IsEnabled); button.Invoke();
        Window dialog;
        try
        {
            dialog = session.WaitFor(() => session.Window.ModalWindows.Concat(session.App.GetAllTopLevelWindows(session.Automation)).FirstOrDefault(w => w.AutomationId == "CloneWindow"));
        }
        catch (TimeoutException ex)
        {
            var logs = string.Join("\n", Directory.EnumerateFiles(Path.Combine(session.DataDir, "logs")).Select(File.ReadAllText));
            var windows = string.Join(" | ", session.App.GetAllTopLevelWindows(session.Automation).Select(w => w.Name + " (" + w.AutomationId + ")"));
            throw new InvalidOperationException("Clone preview did not open. " + windows + "\n" + logs, ex);
        }
        Assert.NotNull(dialog.FindFirstDescendant(session.Cf.ByAutomationId("CloneSource")));
        Assert.NotNull(dialog.FindFirstDescendant(session.Cf.ByAutomationId("CloneDestination")));
        Assert.False(dialog.FindFirstDescendant(session.Cf.ByAutomationId("CloneRun"))!.IsEnabled);
        dialog.Close();
        session.WaitUntil(() => session.Window.IsEnabled, "clone preview owner re-enabled");
    }
}
