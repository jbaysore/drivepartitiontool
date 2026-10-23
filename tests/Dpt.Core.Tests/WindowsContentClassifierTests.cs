using Dpt.Core.WindowsCopy;

namespace Dpt.Core.Tests;

public class WindowsContentClassifierTests
{
    private static IReadOnlyList<ContentItem> Sample() => WindowsContentClassifier.Classify(
        ["Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "pagefile.sys", "hiberfil.sys", "$Recycle.Bin", "System Volume Information", "Games", "bootmgr"],
        user => user == "Alice" ? ["Documents", "Pictures", "AppData", "NTUSER.DAT", "ntuser.dat.LOG1", "Projects"] : [],
        _ => ["Alice", "Public", "Default"]);

    [Fact]
    public void Groups_a_typical_windows_drive()
    {
        var items = Sample();
        ContentGroup G(string rel) => items.Single(i => i.RelativePath == rel).Group;
        Assert.Equal(ContentGroup.Windows, G("Windows"));
        Assert.Equal(ContentGroup.Windows, G("bootmgr"));
        Assert.Equal(ContentGroup.Programs, G("Program Files"));
        Assert.Equal(ContentGroup.Programs, G("ProgramData"));
        Assert.Equal(ContentGroup.AlwaysExcluded, G("pagefile.sys"));
        Assert.Equal(ContentGroup.AlwaysExcluded, G("$Recycle.Bin"));
        Assert.Equal(ContentGroup.Other, G("Games"));
        Assert.Equal(ContentGroup.PersonalFolder, G(@"Users\Alice\Documents"));
        Assert.Equal(ContentGroup.PersonalFolder, G(@"Users\Alice\Projects"));
        Assert.Equal(ContentGroup.AccountSettings, G(@"Users\Alice\AppData"));
        Assert.Equal(ContentGroup.Windows, G(@"Users\Alice\NTUSER.DAT"));
        Assert.Equal(ContentGroup.Windows, G(@"Users\Public"));
        Assert.True(items.Single(i => i.RelativePath == "Windows").Required);
    }

    [Fact]
    public void Exclusions_follow_choices_and_always_excluded()
    {
        var items = Sample();
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Program Files", @"Users\Alice\Documents" };
        var excluded = WindowsContentClassifier.Exclusions(items, chosen);
        Assert.Contains(@"Users\Alice\Pictures", excluded);
        Assert.Contains("Program Files (x86)", excluded);
        Assert.Contains("pagefile.sys", excluded);
        Assert.Contains("Games", excluded);
        Assert.DoesNotContain("Windows", excluded);
        Assert.DoesNotContain(@"Users\Alice\NTUSER.DAT", excluded);
        Assert.DoesNotContain("Program Files", excluded);
        Assert.DoesNotContain(@"Users\Alice\Documents", excluded);
    }

    [Fact]
    public void Consequences_are_neutral_and_present() =>
        Assert.All(Enum.GetValues<ContentGroup>(), g => Assert.False(string.IsNullOrWhiteSpace(WindowsContentClassifier.Consequence(g))));
}
