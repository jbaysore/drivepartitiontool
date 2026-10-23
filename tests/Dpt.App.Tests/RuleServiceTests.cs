using Dpt.App.Services;
using Dpt.Core.Rules;

namespace Dpt.App.Tests;

/// <summary>M5 exit criterion: a new file arrives → an automatic rule tags and moves it → undo restores it.</summary>
public class RuleServiceTests
{
    private readonly AppServices _s;

    public RuleServiceTests()
    {
        _s = AppServices.Initialize();
        _s.FileOps.Dialogs = new ScriptedDialogs();
    }

    private static RuleDefinition ImagesRule(string inbox, string sorted, RuleTrigger trigger) => new()
    {
        Name = "Sort pictures " + Guid.NewGuid().ToString("N")[..6],
        Folders = [inbox],
        Conditions = new RuleConditionGroup { Clauses = [new RuleClause(ConditionField.TypeGroup, ConditionOperator.Is, "image")] },
        AddCategories = ["Photos"],
        MoveTo = sorted + @"\{year}\{ext}",
        Trigger = trigger,
        Scope = RuleScope.Both,
        SettleSeconds = 1,
    };

    [Fact]
    public async Task Preview_lists_planned_moves_and_moving_rules_need_a_preview()
    {
        using var box = new Sandbox();
        var inbox = box.Dir("Inbox");
        box.File(@"Inbox\cat.jpg");
        box.File(@"Inbox\notes.txt");
        var id = _s.Rules.Save(null, ImagesRule(inbox, Path.Combine(box.Root, "Sorted"), RuleTrigger.Manual));
        var rule = _s.Rules.Get(id)!;

        var plan = await _s.RuleRunner.PlanAsync(rule);
        var entry = Assert.Single(plan.Entries);
        Assert.EndsWith("cat.jpg", entry.Path);
        Assert.StartsWith(Path.Combine(box.Root, "Sorted", DateTime.Now.Year.ToString()), entry.Action.Destination);
        Assert.Equal(1, plan.MoveCount);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.RuleRunner.RunAsync(plan, fromPreview: false));
        Assert.True(File.Exists(Path.Combine(inbox, "cat.jpg")));

        var result = await _s.RuleRunner.RunAsync(plan, fromPreview: true);
        Assert.Equal(1, result.Moved);
        Assert.True(_s.Rules.Get(id)!.Previewed);
        Assert.False(File.Exists(Path.Combine(inbox, "cat.jpg")));
    }

    [Fact]
    public async Task New_file_is_tagged_and_moved_automatically_and_undo_restores_it()
    {
        using var box = new Sandbox();
        var inbox = box.Dir("Downloads");
        var sorted = Path.Combine(box.Root, "Pictures");
        var id = _s.Rules.Save(null, ImagesRule(inbox, sorted, RuleTrigger.Automatic));
        _s.Rules.MarkPreviewed(id);
        _s.RuleRunner.RefreshWatchers();

        var arriving = Path.Combine(inbox, "holiday.png");
        await File.WriteAllTextAsync(arriving, "pixels");
        var expected = Path.Combine(sorted, DateTime.Now.Year.ToString(), "png", "holiday.png");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(expected) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            await _s.RuleRunner.ProcessPendingAsync();
        }

        Assert.True(File.Exists(expected), "The new picture should have been moved by the automatic rule.");
        Assert.False(File.Exists(arriving));
        Assert.Contains("Photos", _s.Categories.CategoriesOf(expected));
        Assert.Contains(expected, _s.Categories.PathsOf("Photos"), StringComparer.OrdinalIgnoreCase);

        _s.Rules.SetEnabled(id, false);
        _s.RuleRunner.RefreshWatchers();
        var message = await _s.FileOps.UndoAsync();
        Assert.StartsWith("Undid", message);
        Assert.True(File.Exists(arriving));
        Assert.False(File.Exists(expected));
        _s.Categories.InvalidateCache();
        Assert.DoesNotContain("Photos", _s.Categories.CategoriesOf(arriving));
    }

    [Fact]
    public async Task Tags_follow_renames_and_cat_filter_resolves_paths()
    {
        using var box = new Sandbox();
        var a = box.File("tax-2025.pdf");
        _s.Categories.Tag([a], ["Taxes 2025"]);
        var renamed = await _s.FileOps.RenameAsync(a, "taxes-final.pdf");
        Assert.NotNull(renamed);
        _s.Categories.InvalidateCache();
        Assert.Contains("Taxes 2025", _s.Categories.CategoriesOf(renamed!));
        var paths = await _s.Categories.ResolveForSearchAsync("cat:\"Taxes 2025\"");
        Assert.Contains(renamed!, paths!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Downloads_in_progress_are_ignored_until_finished()
    {
        using var box = new Sandbox();
        var inbox = box.Dir("Dl");
        var id = _s.Rules.Save(null, ImagesRule(inbox, Path.Combine(box.Root, "Out"), RuleTrigger.Automatic));
        _s.Rules.MarkPreviewed(id);
        _s.RuleRunner.RefreshWatchers();
        var partial = Path.Combine(inbox, "big.jpg.crdownload");
        await File.WriteAllTextAsync(partial, "x");
        await Task.Delay(2500);
        await _s.RuleRunner.ProcessPendingAsync();
        Assert.True(File.Exists(partial));
        _s.Rules.SetEnabled(id, false);
        _s.RuleRunner.RefreshWatchers();
    }
}
