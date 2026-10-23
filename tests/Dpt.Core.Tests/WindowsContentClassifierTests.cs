using Dpt.Core.WindowsCopy;

namespace Dpt.Core.Tests;

public class WindowsContentClassifierTests
{
    private static IReadOnlyList<ContentItem> Sample() => WindowsContentClassifier.Classify(
        ["Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "pagefile.sys", "hiberfil.sys", "$Recycle.Bin", "System Volume Information", "Games", "bootmgr"],
        path => path switch
        {
            "Program Files" => ["Editor", "Common Files"],
            "Program Files (x86)" => ["OldApp"],
            "ProgramData" => ["Microsoft", "Vendor"],
            "Users" => ["Alice", "Public", "Default"],
            @"Users\Alice" => ["Documents", "Pictures", "AppData", "NTUSER.DAT", "ntuser.dat.LOG1", "Projects"],
            @"Users\Public" => ["Documents"],
            @"Users\Alice\AppData" => ["Local", "Roaming"],
            @"Users\Alice\AppData\Local" => ["Microsoft", "Browser"],
            @"Users\Alice\AppData\Roaming" => ["Microsoft", "Editor"],
            _ => [],
        });

    [Fact]
    public void Groups_a_typical_windows_drive()
    {
        var items = Sample();
        ContentGroup G(string rel) => items.Single(i => i.RelativePath == rel).Group;
        Assert.Equal(ContentGroup.Windows, G("Windows"));
        Assert.Equal(ContentGroup.Windows, G("bootmgr"));
        Assert.Equal(ContentGroup.Programs, G(@"Program Files\Editor"));
        Assert.Equal(ContentGroup.Programs, G(@"ProgramData\Vendor"));
        Assert.Equal(ContentGroup.Windows, G(@"ProgramData\Microsoft"));
        Assert.Equal(ContentGroup.AlwaysExcluded, G("pagefile.sys"));
        Assert.Equal(ContentGroup.AlwaysExcluded, G("$Recycle.Bin"));
        Assert.Equal(ContentGroup.Other, G("Games"));
        Assert.Equal(ContentGroup.PersonalFolder, G(@"Users\Alice\Documents"));
        Assert.Equal(ContentGroup.PersonalFolder, G(@"Users\Alice\Projects"));
        Assert.Equal(ContentGroup.AccountSettings, G(@"Users\Alice\AppData\Local\Browser"));
        Assert.Equal(ContentGroup.Windows, G(@"Users\Alice\NTUSER.DAT"));
        Assert.Equal(ContentGroup.PersonalFolder, G(@"Users\Public\Documents"));
        Assert.True(items.Single(i => i.RelativePath == @"Users\Alice\AppData\Local\Microsoft").Required);
        Assert.True(items.Single(i => i.RelativePath == "Windows").Required);
    }

    [Fact]
    public void Exclusions_follow_choices_and_always_excluded()
    {
        var items = Sample();
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"Program Files\Editor", @"Users\Alice\Documents" };
        var excluded = WindowsContentClassifier.Exclusions(items, chosen);
        Assert.Contains(@"Users\Alice\Pictures", excluded);
        Assert.Contains(@"Program Files (x86)\OldApp", excluded);
        Assert.Contains("pagefile.sys", excluded);
        Assert.Contains("Games", excluded);
        Assert.DoesNotContain("Windows", excluded);
        Assert.DoesNotContain(@"Users\Alice\NTUSER.DAT", excluded);
        Assert.DoesNotContain(@"Program Files\Editor", excluded);
        Assert.DoesNotContain(@"ProgramData\Microsoft", excluded);
        Assert.DoesNotContain(@"Users\Alice\AppData\Roaming\Microsoft", excluded);
        Assert.DoesNotContain(@"Users\Alice\Documents", excluded);
    }

    [Fact]
    public void Consequences_are_neutral_and_present() =>
        Assert.All(Enum.GetValues<ContentGroup>(), g => Assert.False(string.IsNullOrWhiteSpace(WindowsContentClassifier.Consequence(g))));

    [Fact]
    public void Custom_program_locations_are_split_from_their_siblings()
    {
        var items = WindowsContentClassifier.Classify(["Tools"], path => path == "Tools" ? ["Editor", "Notes"] : [], [new("Text editor", @"Tools\Editor")]);
        Assert.Equal(ContentGroup.Programs, items.Single(i => i.Title == "Text editor").Group);
        Assert.Equal(ContentGroup.Other, items.Single(i => i.RelativePath == @"Tools\Notes").Group);
        Assert.DoesNotContain(items, i => i.RelativePath == "Tools");
    }

    [Fact]
    public void Redirected_personal_folders_are_identified_outside_the_profile_without_hiding_neighboring_programs()
    {
        var items = WindowsContentClassifier.Classify(["Data"], path => path == "Data" ? ["MyDocs", "Tools"] : [],
            [new("Editor", @"Data\Tools")], [new("Alice", @"Data\MyDocs", "Alice · Documents")]);
        Assert.Equal(ContentGroup.PersonalFolder, items.Single(i => i.RelativePath == @"Data\MyDocs").Group);
        Assert.Equal("Alice", items.Single(i => i.RelativePath == @"Data\MyDocs").User);
        Assert.Equal(ContentGroup.Programs, items.Single(i => i.Title == "Editor").Group);
    }
}
