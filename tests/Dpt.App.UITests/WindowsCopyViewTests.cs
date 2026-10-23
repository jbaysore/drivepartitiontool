using FlaUI.Core.AutomationElements;

namespace Dpt.App.UITests;

[Trait("Category", "UI")]
public class WindowsCopyViewTests
{
    [Fact]
    public void Windows_copy_opens_a_read_only_preview_and_cannot_run_before_review()
    {
        using var session = new AppSession(arguments: "--open DiskTools");
        session.Get("WindowsCopyTask").AsButton().Invoke();
        Window dialog;
        try
        {
            dialog = session.WaitFor(() => session.Window.ModalWindows.Concat(session.App.GetAllTopLevelWindows(session.Automation))
                .FirstOrDefault(w => w.AutomationId == "WindowsCopyWindow"));
        }
        catch (TimeoutException ex)
        {
            var folder = Path.Combine(session.DataDir, "logs");
            var log = Directory.Exists(folder) ? string.Join("\n", Directory.EnumerateFiles(folder).Select(File.ReadAllText)) : "No app log";
            throw new InvalidOperationException("The preview did not open. " + log, ex);
        }
        AutomationElement? Find(string id) => dialog.FindFirstDescendant(session.Cf.ByAutomationId(id));
        session.WaitUntil(() => Find("WindowsCopySource") is not null, "Windows-copy source chooser");
        Assert.NotNull(Find("WindowsCopyDestination"));
        Assert.NotNull(Find("WindowsCopyFirmware"));
        Assert.False(Find("WindowsCopyRun")!.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(Find("WindowsCopyProblem")!.Name));
        if (Environment.GetEnvironmentVariable("DPT_UI_SCREENSHOT") is { Length: > 0 } screenshot)
            FlaUI.Core.Capturing.Capture.Element(dialog).ToFile(screenshot);
        dialog.Close();
    }
}
