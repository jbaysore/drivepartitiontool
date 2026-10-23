using Dpt.Core.Navigation;
using Dpt.Core.Rules;
using Dpt.Core.Search;

namespace Dpt.Core.Layouts;

/// <summary>One "these files go here" line of a layout. <see cref="Path"/> is relative to the layout's target folder.</summary>
public sealed record LayoutTemplate
{
    public RuleConditionGroup Conditions { get; init; } = new();
    public string Path { get; init; } = "{type}";
}

/// <summary>
/// A candidate folder structure: which existing files (in <see cref="Sources"/>) go where under <see cref="TargetRoot"/>.
/// The first matching template wins; files no template matches go to <see cref="FallbackPath"/> or stay put.
/// </summary>
public sealed record LayoutDefinition
{
    public string Name { get; init; } = "New layout";
    public List<string> Sources { get; init; } = [];
    public bool IncludeSubfolders { get; init; } = true;
    public string TargetRoot { get; init; } = "";
    public List<LayoutTemplate> Templates { get; init; } = [];

    /// <summary>Relative folder for unmatched files, or null/empty to leave them where they are.</summary>
    public string? FallbackPath { get; init; } = "Other";

    public bool RemoveEmptyFolders { get; init; } = true;
}

public enum LayoutOutcome
{
    Move,
    AlreadyThere,
    Unmatched,
    Problem,
}

public sealed record LayoutEntry(string Source, string? Destination, int TemplateIndex, LayoutOutcome Outcome, long Size, bool Collision, string? Note);

/// <summary>A node in the virtual folder tree a layout would produce.</summary>
public sealed class LayoutNode(string name, string path)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public int FileCount { get; set; }
    public long Bytes { get; set; }
    public SortedDictionary<string, LayoutNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int TotalFiles => FileCount + Children.Values.Sum(c => c.TotalFiles);
    public long TotalBytes => Bytes + Children.Values.Sum(c => c.TotalBytes);
}

public sealed record LayoutPlan(LayoutDefinition Layout, IReadOnlyList<LayoutEntry> Entries, LayoutNode Tree)
{
    public int MoveCount => Entries.Count(e => e.Outcome == LayoutOutcome.Move);
    public int UnchangedCount => Entries.Count(e => e.Outcome == LayoutOutcome.AlreadyThere);
    public int UnmatchedCount => Entries.Count(e => e.Outcome == LayoutOutcome.Unmatched);
    public int CollisionCount => Entries.Count(e => e.Collision);
    public int ProblemCount => Entries.Count(e => e.Outcome == LayoutOutcome.Problem);
    public long MoveBytes => Entries.Where(e => e.Outcome == LayoutOutcome.Move).Sum(e => e.Size);
    public int FolderCount => CountFolders(Tree) - 1;

    private static int CountFolders(LayoutNode n) => 1 + n.Children.Values.Sum(CountFolders);
}

/// <summary>Plans a layout over existing files without touching the disk.</summary>
public static class LayoutPlanner
{
    public static LayoutPlan Plan(LayoutDefinition layout, IReadOnlyList<ItemFacts> files, Func<string, bool> existsOnDisk, TypeGroups? groups = null)
    {
        var g = groups ?? TypeGroups.Default;
        var root = PathUtil.Normalize(layout.TargetRoot);
        var entries = new List<LayoutEntry>(files.Count);
        var now = DateTime.Now;
        foreach (var f in files)
        {
            var index = -1;
            for (var i = 0; i < layout.Templates.Count; i++)
            {
                if (RuleEngine.Matches(layout.Templates[i].Conditions, f, g, now))
                {
                    index = i;
                    break;
                }
            }

            string? relative = index >= 0 ? layout.Templates[index].Path : layout.FallbackPath;
            if (string.IsNullOrWhiteSpace(relative))
            {
                entries.Add(new LayoutEntry(f.Path, null, -1, LayoutOutcome.Unmatched, f.Size, false, null));
                continue;
            }

            var folder = RuleEngine.ExpandFolder(root.TrimEnd('\\') + "\\" + relative.Trim('\\', '/'), f, f.Categories.ToList(), g);
            if (folder is null)
            {
                entries.Add(new LayoutEntry(f.Path, null, index, LayoutOutcome.Problem, f.Size, false, "The folder pattern doesn't make a valid path."));
                continue;
            }

            var dest = System.IO.Path.Combine(folder, f.Name);
            var outcome = string.Equals(dest, f.Path, StringComparison.OrdinalIgnoreCase) ? LayoutOutcome.AlreadyThere : LayoutOutcome.Move;
            entries.Add(new LayoutEntry(f.Path, dest, index, outcome, f.Size, false, null));
        }

        // Collisions: two files planned for the same destination, or a destination already occupied by a file that isn't moving away.
        var sources = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var leaving = new HashSet<string>(entries.Where(e => e.Outcome == LayoutOutcome.Move).Select(e => e.Source), StringComparer.OrdinalIgnoreCase);
        var byDest = entries.Where(e => e.Destination is not null).GroupBy(e => e.Destination!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Outcome != LayoutOutcome.Move) continue;
            var dup = byDest[e.Destination!] > 1;
            var occupied = existsOnDisk(e.Destination!) && (!sources.Contains(e.Destination!) || !leaving.Contains(e.Destination!));
            if (dup || occupied)
                entries[i] = e with { Collision = true, Note = dup ? "Another file would get the same name here; a number will be added." : "A file with this name is already there; a number will be added." };
        }

        var tree = new LayoutNode(System.IO.Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root, root);
        foreach (var e in entries)
        {
            var finalPath = e.Destination ?? (e.Outcome == LayoutOutcome.Unmatched ? e.Source : null);
            if (finalPath is null) continue;
            var node = tree;
            var dir = System.IO.Path.GetDirectoryName(finalPath) ?? "";
            if (PathUtil.IsSameOrUnder(dir, root))
            {
                var rel = dir.Length > root.TrimEnd('\\').Length ? dir[(root.TrimEnd('\\').Length + 1)..] : "";
                foreach (var part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!node.Children.TryGetValue(part, out var child))
                    {
                        child = new LayoutNode(part, System.IO.Path.Combine(node.Path, part));
                        node.Children[part] = child;
                    }

                    node = child;
                }
            }
            else
            {
                const string outside = "(left outside the target folder)";
                if (!tree.Children.TryGetValue(outside, out var child))
                {
                    child = new LayoutNode(outside, "");
                    tree.Children[outside] = child;
                }

                node = child;
            }

            node.FileCount++;
            node.Bytes += e.Size;
        }

        return new LayoutPlan(layout, entries, tree);
    }

    /// <summary>Rules equivalent to a layout, so new files keep following it.</summary>
    public static IReadOnlyList<RuleDefinition> ToRules(LayoutDefinition layout)
    {
        var root = PathUtil.Normalize(layout.TargetRoot).TrimEnd('\\');
        var rules = layout.Templates.Select((t, i) => new RuleDefinition
        {
            Name = $"{layout.Name} — {t.Path}",
            Folders = layout.Sources.ToList(),
            IncludeSubfolders = layout.IncludeSubfolders,
            Conditions = t.Conditions,
            MoveTo = root + "\\" + t.Path.Trim('\\', '/'),
            Trigger = RuleTrigger.Automatic,
            Scope = RuleScope.NewItems,
        }).ToList();
        return rules;
    }
}
