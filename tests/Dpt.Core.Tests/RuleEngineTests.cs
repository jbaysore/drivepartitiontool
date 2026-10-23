using Dpt.Core.Rules;
using Dpt.Core.Search;

namespace Dpt.Core.Tests;

public class RuleEngineTests
{
    private static readonly DateTime Now = DateTime.Now;

    private static ItemFacts F(string path, long size = 1000, int ageDays = 1, params string[] cats) =>
        new(path, false, size, Now.AddDays(-ageDays), Now.AddDays(-ageDays - 1), cats);

    private static RuleDefinition Images(string? moveTo = null) => new()
    {
        Name = "Images",
        Folders = [@"C:\Users\You\Downloads"],
        Conditions = new RuleConditionGroup { Clauses = [new RuleClause(ConditionField.TypeGroup, ConditionOperator.Is, "image")] },
        AddCategories = ["Photos"],
        MoveTo = moveTo,
    };

    [Fact]
    public void Folder_scope_and_type()
    {
        var rule = Images();
        Assert.True(RuleEngine.Applies(rule, F(@"C:\Users\You\Downloads\cat.JPG")));
        Assert.True(RuleEngine.Applies(rule, F(@"C:\Users\You\Downloads\sub\cat.png")));
        Assert.False(RuleEngine.Applies(rule with { IncludeSubfolders = false }, F(@"C:\Users\You\Downloads\sub\cat.png")));
        Assert.False(RuleEngine.Applies(rule, F(@"C:\Users\You\Downloads\report.pdf")));
        Assert.False(RuleEngine.Applies(rule, F(@"C:\Users\You\Documents\cat.jpg")));
        Assert.False(RuleEngine.Applies(rule, F(@"C:\Users\You\Downloads") with { IsFolder = true }));
    }

    [Theory]
    [InlineData(ConditionField.Name, ConditionOperator.Contains, "invoice", true)]
    [InlineData(ConditionField.Name, ConditionOperator.StartsWith, "INV", true)]
    [InlineData(ConditionField.Name, ConditionOperator.EndsWith, ".pdf", true)]
    [InlineData(ConditionField.Name, ConditionOperator.MatchesPattern, "Invoice-*.pdf", true)]
    [InlineData(ConditionField.Name, ConditionOperator.MatchesPattern, "/^inv.*\\d{4}/", true)]
    [InlineData(ConditionField.Name, ConditionOperator.DoesNotContain, "invoice", false)]
    [InlineData(ConditionField.Extension, ConditionOperator.IsOneOf, "doc,.pdf", true)]
    [InlineData(ConditionField.Extension, ConditionOperator.IsNot, "pdf", false)]
    [InlineData(ConditionField.Size, ConditionOperator.GreaterThan, "1mb", true)]
    [InlineData(ConditionField.Size, ConditionOperator.LessThan, "1mb", false)]
    [InlineData(ConditionField.Modified, ConditionOperator.OlderThanDays, "30", true)]
    [InlineData(ConditionField.Modified, ConditionOperator.NewerThanDays, "30", false)]
    [InlineData(ConditionField.Category, ConditionOperator.Is, "taxes", true)]
    [InlineData(ConditionField.Category, ConditionOperator.IsNot, "Taxes", false)]
    [InlineData(ConditionField.PathContains, ConditionOperator.Contains, @"\Finance\", true)]
    public void Clauses(ConditionField field, ConditionOperator op, string value, bool expected)
    {
        var item = F(@"D:\Finance\Invoice-2026.pdf", size: 5_000_000, ageDays: 90, "Taxes");
        Assert.Equal(expected, RuleEngine.Matches(new RuleClause(field, op, value), item, TypeGroups.Default, Now));
    }

    [Fact]
    public void All_vs_any_and_nested_groups()
    {
        var item = F(@"D:\x\photo.jpg", size: 10);
        var group = new RuleConditionGroup
        {
            Match = ConditionMatch.Any,
            Clauses = [new RuleClause(ConditionField.Extension, ConditionOperator.IsOneOf, "png"), new RuleClause(ConditionField.Name, ConditionOperator.Contains, "photo")],
        };
        Assert.True(RuleEngine.Matches(group, item, TypeGroups.Default, Now));
        Assert.False(RuleEngine.Matches(group with { Match = ConditionMatch.All }, item, TypeGroups.Default, Now));
        var nested = new RuleConditionGroup
        {
            Clauses = [new RuleClause(ConditionField.Size, ConditionOperator.LessThan, "1kb")],
            Groups = [group],
        };
        Assert.True(RuleEngine.Matches(nested, item, TypeGroups.Default, Now));
        Assert.True(RuleEngine.Matches(new RuleConditionGroup(), item, TypeGroups.Default, Now));
    }

    [Fact]
    public void Destination_templates()
    {
        var item = new ItemFacts(@"C:\Dl\IMG_1.JPG", false, 1, new DateTime(2025, 3, 9), new DateTime(2025, 3, 9), []);
        Assert.Equal(@"E:\Media\Images\2025\jpg", RuleEngine.ExpandFolder(@"E:\Media\Images\{year}\{ext}", item, []));
        Assert.Equal(@"E:\Sorted\image\03", RuleEngine.ExpandFolder(@"E:/Sorted/{type}/{month}", item, []));
        Assert.Equal(@"E:\By category\Photos", RuleEngine.ExpandFolder(@"E:\By category\{category}", item, ["Photos"]));
        Assert.Equal(@"E:\By category\uncategorized", RuleEngine.ExpandFolder(@"E:\By category\{category}", item, []));
        Assert.Null(RuleEngine.ExpandFolder(@"relative\{year}", item, []));
    }

    [Fact]
    public void Plan_adds_only_missing_categories_and_destination()
    {
        var rule = Images(@"E:\Pictures\{year}") with { AddCategories = ["Photos", "Inbox"] };
        var item = new ItemFacts(@"C:\Users\You\Downloads\cat.jpg", false, 1, new DateTime(2026, 1, 2), new DateTime(2026, 1, 2), ["Inbox"]);
        var plan = RuleEngine.Plan(rule, item);
        Assert.Equal(["Photos"], plan.AddCategories);
        Assert.Equal(@"E:\Pictures\2026\cat.jpg", plan.Destination);
        Assert.True(plan.Moves);
    }

    [Fact]
    public void Folder_cannot_move_into_itself()
    {
        var rule = new RuleDefinition { Kind = RuleItemKind.Folders, MoveTo = @"C:\Stuff\{name}\inner" };
        var plan = RuleEngine.Plan(rule, new ItemFacts(@"C:\Stuff", true, 0, Now, Now, []));
        Assert.Null(plan.Destination);
        Assert.NotNull(plan.Note);
    }
}
