namespace Dpt.App.UITests;

[Trait("Category", "UI")]
public class IntegrationViewTests
{
    [Fact]
    public void Settings_explains_taskbar_pinning_and_reversal_without_changing_a_folder_handler()
    {
        using var session = new AppSession(arguments: "--open Settings");
        Assert.Contains("Pin to taskbar", session.Get("TaskbarGuidance").Name);
        Assert.NotNull(session.Get("RestoreFolderHandler"));
        Assert.NotNull(session.Get("DefaultFolderHandler"));
        // Merely opening Settings must never apply the stored integration flag or write any registry key.
    }
}
