using System.Windows;
using System.Windows.Media;
using Dpt.App.Services;
using Dpt.Core.Formatting;
using Dpt.Shell.Operations;

namespace Dpt.App.Views;

/// <summary>Modal questions using <see cref="DialogWindow"/>. Wording stays neutral and plain.</summary>
public sealed class WpfDialogService : IDialogService
{
    private static readonly string[] ConflictOptions = ["Replace", "Skip", "Keep both"];

    private static Style S(string key) => (Style)Application.Current.FindResource(key);

    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    private static bool Show(DialogWindow w, out string? result)
    {
        w.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive && x != w) ?? Application.Current.MainWindow;
        if (w.Owner == w) w.Owner = null;
        var ok = w.ShowDialog() == true;
        result = w.Result;
        return ok;
    }

    public IReadOnlyDictionary<string, ConflictChoice>? ResolveConflicts(IReadOnlyList<ConflictInfo> conflicts, bool isMove)
    {
        var w = new DialogWindow
        {
            Title = "Items with the same name",
            Heading = conflicts.Count == 1 ? "An item with this name already exists" : $"{conflicts.Count} items already exist here",
            Message = "Choose what happens to each one. \"Replace\" overwrites the existing item (folders are merged), \"Skip\" leaves it alone, and \"Keep both\" adds a number to the new item's name.",
            IconKey = "Icon.Copy",
            IconBrush = B("DptAccent"),
            ChoiceLabels = ConflictOptions,
        };
        foreach (var c in conflicts)
        {
            var detail = Describe(c.ExistingTarget) + "  →  " + Describe(c.Source);
            w.Rows.Add(new DialogRow(Path.GetFileName(c.Source.TrimEnd('\\')), "Existing: " + detail, ConflictOptions, 2));
        }

        w.Buttons.Add(new DialogButton("Cancel", "cancel", S("SecondaryButton"), IsCancel: true));
        w.Buttons.Add(new DialogButton(isMove ? "Move" : "Copy", "ok", S("PrimaryButton"), IsDefault: true));
        if (!Show(w, out _)) return null;
        var map = new Dictionary<string, ConflictChoice>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < conflicts.Count; i++)
            map[conflicts[i].Source] = w.Rows[i].Selected switch { 0 => ConflictChoice.Replace, 1 => ConflictChoice.Skip, _ => ConflictChoice.KeepBoth };
        return map;
    }

    private static string Describe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var f = new FileInfo(path);
                return $"{SizeFormatter.Format(f.Length)}, modified {f.LastWriteTime:g}";
            }

            if (Directory.Exists(path)) return $"folder, modified {Directory.GetLastWriteTime(path):g}";
        }
        catch (IOException)
        {
        }

        return "";
    }

    public bool ConfirmPermanentDelete(IReadOnlyList<string> paths, string reason)
    {
        var w = new DialogWindow
        {
            Title = "Delete permanently",
            Heading = paths.Count == 1 ? $"Permanently delete \"{Path.GetFileName(paths[0].TrimEnd('\\'))}\"?" : $"Permanently delete {paths.Count} items?",
            Message = reason,
            IconKey = "Icon.Alert",
            IconBrush = B("DptRed"),
        };
        foreach (var p in paths.Take(200)) w.Rows.Add(new DialogRow(Path.GetFileName(p.TrimEnd('\\')), p));
        w.Buttons.Add(new DialogButton("Cancel", "cancel", S("SecondaryButton"), IsDefault: true, IsCancel: true));
        w.Buttons.Add(new DialogButton("Delete permanently", "delete", S("DangerButton")));
        return Show(w, out var r) && r == "delete";
    }

    public bool AskPermanentDeleteForBlocked(IReadOnlyList<BlockedItem> blocked)
    {
        var w = new DialogWindow
        {
            Title = "Couldn't move to Recycle Bin",
            Heading = blocked.Count == 1 ? "This item can't go to the Recycle Bin" : $"{blocked.Count} items can't go to the Recycle Bin",
            Message = "Nothing listed here has been deleted. Windows can't keep these items in the Recycle Bin (usually because they are larger than the bin's limit for their drive, or the drive doesn't have one). " +
                      "You can leave them as they are, or delete them permanently. Permanently deleted items can't be restored.",
            IconKey = "Icon.Alert",
            IconBrush = B("DptAmber"),
        };
        foreach (var b in blocked)
            w.Rows.Add(new DialogRow(Path.GetFileName(b.Path.TrimEnd('\\')), $"{(b.SizeBytes is { } s ? SizeFormatter.Format(s) : "Size unknown")} · {b.Path}\n{b.Reason}"));
        w.Buttons.Add(new DialogButton("Cancel — keep them", "cancel", S("SecondaryButton"), IsDefault: true, IsCancel: true));
        w.Buttons.Add(new DialogButton("Permanently delete these", "delete", S("DangerButton")));
        return Show(w, out var r) && r == "delete";
    }

    public bool WarnBeforeRecycle(IReadOnlyList<(string Path, string Reason)> warnings)
    {
        var w = new DialogWindow
        {
            Title = "Recycle Bin limits",
            Heading = "Some items may not fit in the Recycle Bin",
            Message = "If you continue, everything that can go to the Recycle Bin will. Anything that can't will be left in place, and you'll be asked what to do — nothing is permanently deleted without your say-so.",
            IconKey = "Icon.Info",
            IconBrush = B("DptAmber"),
        };
        foreach (var (p, reason) in warnings) w.Rows.Add(new DialogRow(Path.GetFileName(p.TrimEnd('\\')), reason));
        w.Buttons.Add(new DialogButton("Cancel", "cancel", S("SecondaryButton"), IsCancel: true));
        w.Buttons.Add(new DialogButton("Continue", "ok", S("PrimaryButton"), IsDefault: true));
        return Show(w, out var r) && r == "ok";
    }

    public bool Confirm(string title, string message, string confirmText, bool danger = false)
    {
        var w = new DialogWindow { Title = title, Heading = title, Message = message, IconKey = danger ? "Icon.Alert" : "Icon.Info", IconBrush = B(danger ? "DptRed" : "DptAccent") };
        w.Buttons.Add(new DialogButton("Cancel", "cancel", S("SecondaryButton"), IsCancel: true, IsDefault: danger));
        w.Buttons.Add(new DialogButton(confirmText, "ok", S(danger ? "DangerButton" : "PrimaryButton"), IsDefault: !danger));
        return Show(w, out var r) && r == "ok";
    }

    public string? Prompt(string title, string message, string initial)
    {
        var w = new DialogWindow { Title = title, Heading = title, Message = message, IconKey = "Icon.Pencil", IconBrush = B("DptAccent"), HasInput = true, InputText = initial };
        w.Buttons.Add(new DialogButton("Cancel", "cancel", S("SecondaryButton"), IsCancel: true));
        w.Buttons.Add(new DialogButton("OK", "ok", S("PrimaryButton"), IsDefault: true));
        return Show(w, out var r) && r == "ok" ? w.InputText : null;
    }

    public void ShowError(string title, string message)
    {
        var w = new DialogWindow { Title = title, Heading = title, Message = message, IconKey = "Icon.Alert", IconBrush = B("DptRed") };
        w.Buttons.Add(new DialogButton("OK", "ok", S("PrimaryButton"), IsDefault: true, IsCancel: true));
        Show(w, out _);
    }

    public void ShowInfo(string title, string message)
    {
        var w = new DialogWindow { Title = title, Heading = title, Message = message, IconKey = "Icon.Info", IconBrush = B("DptAccent") };
        w.Buttons.Add(new DialogButton("OK", "ok", S("PrimaryButton"), IsDefault: true, IsCancel: true));
        Show(w, out _);
    }
}
