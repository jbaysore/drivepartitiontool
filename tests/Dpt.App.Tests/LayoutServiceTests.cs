using System.Diagnostics;
using Dpt.App.Services;
using Dpt.Core.Layouts;
using Dpt.Core.Rules;
using Xunit.Abstractions;

namespace Dpt.App.Tests;

/// <summary>M6 exit criterion: on ~10k generated files the preview matches the result after applying; undo restores.</summary>
public class LayoutServiceTests(ITestOutputHelper output)
{
    private readonly AppServices _s = InitServices();

    private static AppServices InitServices()
    {
        var s = AppServices.Initialize();
        s.FileOps.Dialogs = new ScriptedDialogs();
        return s;
    }

    private static RuleConditionGroup Type(string t) => new() { Clauses = [new RuleClause(ConditionField.TypeGroup, ConditionOperator.Is, t)] };

    [Fact]
    public async Task Ten_thousand_files_preview_matches_result_and_undo_restores()
    {
        using var box = new Sandbox();
        var mess = box.Dir("Mess");
        string[] exts = ["jpg", "png", "mp4", "pdf", "docx", "zip", "txt", "heic", "xyz"];
        var rng = new Random(42);
        var originals = new List<string>();
        for (var i = 0; i < 10_000; i++)
        {
            var folder = Path.Combine(mess, $"dump{i % 37}", $"sub{i % 5}");
            Directory.CreateDirectory(folder);
            // Some duplicate names across folders on purpose (collisions).
            var name = i % 250 == 0 ? $"duplicate.{exts[i % exts.Length]}" : $"file{i}.{exts[rng.Next(exts.Length)]}";
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) continue;
            File.WriteAllBytes(path, new byte[rng.Next(1, 64)]);
            File.SetLastWriteTime(path, new DateTime(2018 + rng.Next(8), 1 + rng.Next(12), 1));
            originals.Add(path);
        }

        var sorted = Path.Combine(box.Root, "Sorted");
        var layout = new LayoutDefinition
        {
            Name = "Test layout",
            Sources = [mess],
            TargetRoot = sorted,
            Templates =
            [
                new LayoutTemplate { Conditions = Type("image"), Path = @"Media\Images\{year}" },
                new LayoutTemplate { Conditions = Type("video"), Path = @"Media\Video\{year}" },
                new LayoutTemplate { Conditions = Type("doc"), Path = @"Documents\{ext}" },
                new LayoutTemplate { Conditions = Type("archive"), Path = "Archives" },
            ],
            FallbackPath = "Other",
        };

        var sw = Stopwatch.StartNew();
        var plan = await _s.Layouts.PlanAsync(layout);
        output.WriteLine($"Planned {plan.Entries.Count} files in {sw.ElapsedMilliseconds} ms: {plan.MoveCount} moves, {plan.CollisionCount} collisions, {plan.FolderCount} folders");
        Assert.Equal(originals.Count, plan.Entries.Count);
        Assert.True(plan.CollisionCount > 0);

        // Expected per-folder counts from the preview tree.
        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Walk(LayoutNode n)
        {
            if (n.FileCount > 0) expected[n.Path] = n.FileCount;
            foreach (var c in n.Children.Values) Walk(c);
        }

        Walk(plan.Tree);

        sw.Restart();
        var result = await _s.Layouts.ApplyAsync(plan);
        output.WriteLine($"Applied in {sw.ElapsedMilliseconds} ms: {result.Moved} moved, {result.Failed} failed, {result.FoldersRemoved} empty folders removed");
        Assert.Equal(0, result.Failed);
        Assert.Equal(plan.MoveCount, result.Moved);

        var actual = Directory.EnumerateFiles(sorted, "*", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.OrderBy(k => k.Key), actual.OrderBy(k => k.Key));
        Assert.Empty(Directory.EnumerateFiles(mess, "*", SearchOption.AllDirectories));

        // Undo the whole layout as one batch.
        sw.Restart();
        var message = await _s.FileOps.UndoAsync(result.BatchId);
        output.WriteLine($"Undo in {sw.ElapsedMilliseconds} ms: {message}");
        Assert.All(originals, p => Assert.True(File.Exists(p), p));
    }

    [Fact]
    public async Task Layouts_save_and_turn_into_rules()
    {
        var id = _s.Layouts.Save(null, new LayoutDefinition { Name = "Media first", Sources = [@"C:\Nowhere"], TargetRoot = @"C:\Sorted", Templates = [new LayoutTemplate { Conditions = Type("image"), Path = "Images" }] });
        Assert.Contains(_s.Layouts.GetLayouts(), l => l.Id == id && l.Definition.Templates.Count == 1);
        var ruleIds = _s.Layouts.CreateRules(_s.Layouts.GetLayouts().Single(l => l.Id == id).Definition);
        var rule = _s.Rules.Get(Assert.Single(ruleIds))!;
        Assert.Equal(@"C:\Sorted\Images", rule.Definition.MoveTo);
        Assert.False(rule.Previewed);
        _s.Rules.Delete(rule.Id);
        _s.Layouts.Delete(id);
    }
}
