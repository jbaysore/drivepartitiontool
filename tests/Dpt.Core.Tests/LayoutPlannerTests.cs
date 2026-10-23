using Dpt.Core.Layouts;
using Dpt.Core.Rules;

namespace Dpt.Core.Tests;

public class LayoutPlannerTests
{
    private static ItemFacts F(string path, int year = 2025, long size = 100) => new(path, false, size, new DateTime(year, 5, 1), new DateTime(year, 5, 1), []);

    private static RuleConditionGroup Type(string t) => new() { Clauses = [new RuleClause(ConditionField.TypeGroup, ConditionOperator.Is, t)] };

    private static LayoutDefinition ByType() => new()
    {
        Name = "By type then year",
        Sources = [@"D:\Mess"],
        TargetRoot = @"D:\Sorted",
        Templates =
        [
            new LayoutTemplate { Conditions = Type("image"), Path = @"Media\Images\{year}" },
            new LayoutTemplate { Conditions = Type("video"), Path = @"Media\Video" },
            new LayoutTemplate { Conditions = Type("doc"), Path = @"Documents\{ext}" },
        ],
        FallbackPath = "Other",
    };

    [Fact]
    public void Plans_destinations_tree_and_counts()
    {
        var files = new[]
        {
            F(@"D:\Mess\a.jpg", 2024), F(@"D:\Mess\b.png", 2025), F(@"D:\Mess\c.mp4", size: 1000), F(@"D:\Mess\d.pdf"), F(@"D:\Mess\e.xyz"),
        };
        var plan = LayoutPlanner.Plan(ByType(), files, _ => false);
        Assert.Equal(@"D:\Sorted\Media\Images\2024\a.jpg", plan.Entries[0].Destination);
        Assert.Equal(@"D:\Sorted\Media\Images\2025\b.png", plan.Entries[1].Destination);
        Assert.Equal(@"D:\Sorted\Documents\pdf\d.pdf", plan.Entries[3].Destination);
        Assert.Equal(@"D:\Sorted\Other\e.xyz", plan.Entries[4].Destination);
        Assert.Equal(5, plan.MoveCount);
        Assert.Equal(5, plan.Tree.TotalFiles);
        Assert.Equal(1400, plan.Tree.TotalBytes);
        var media = plan.Tree.Children["Media"];
        Assert.Equal(3, media.TotalFiles);
        Assert.Equal(2, media.Children["Images"].Children.Count);
    }

    [Fact]
    public void First_matching_template_wins_and_unmatched_can_stay()
    {
        var layout = ByType() with
        {
            Templates = [new LayoutTemplate { Conditions = Type("image"), Path = "Pictures" }, new LayoutTemplate { Conditions = new RuleConditionGroup(), Path = "Everything" }],
            FallbackPath = null,
        };
        var plan = LayoutPlanner.Plan(layout, [F(@"D:\Mess\a.jpg"), F(@"D:\Mess\b.txt")], _ => false);
        Assert.EndsWith(@"\Pictures\a.jpg", plan.Entries[0].Destination);
        Assert.EndsWith(@"\Everything\b.txt", plan.Entries[1].Destination);

        var noCatchAll = layout with { Templates = [layout.Templates[0]] };
        var p2 = LayoutPlanner.Plan(noCatchAll, [F(@"D:\Mess\b.txt")], _ => false);
        Assert.Equal(LayoutOutcome.Unmatched, p2.Entries[0].Outcome);
        Assert.Equal(1, p2.Tree.Children["(left outside the target folder)"].FileCount);
    }

    [Fact]
    public void Collisions_and_files_already_in_place()
    {
        var files = new[] { F(@"D:\Mess\x\photo.jpg"), F(@"D:\Mess\y\photo.jpg"), F(@"D:\Sorted\Media\Images\2025\done.jpg") };
        var layout = ByType() with { Sources = [@"D:\Mess", @"D:\Sorted"] };
        var plan = LayoutPlanner.Plan(layout, files, p => p.EndsWith("existing.jpg"));
        Assert.True(plan.Entries[0].Collision);
        Assert.True(plan.Entries[1].Collision);
        Assert.Equal(LayoutOutcome.AlreadyThere, plan.Entries[2].Outcome);
        Assert.Equal(2, plan.CollisionCount);
        Assert.Equal(1, plan.UnchangedCount);

        var occupied = LayoutPlanner.Plan(ByType(), [F(@"D:\Mess\existing.jpg")], p => p.EndsWith("existing.jpg"));
        Assert.True(occupied.Entries[0].Collision);
    }

    [Fact]
    public void Layout_becomes_rules_for_new_files()
    {
        var rules = LayoutPlanner.ToRules(ByType());
        Assert.Equal(3, rules.Count);
        Assert.Equal(@"D:\Sorted\Media\Images\{year}", rules[0].MoveTo);
        Assert.All(rules, r => Assert.Equal(RuleScope.NewItems, r.Scope));
        Assert.All(rules, r => Assert.Equal(RuleTrigger.Automatic, r.Trigger));
    }
}
