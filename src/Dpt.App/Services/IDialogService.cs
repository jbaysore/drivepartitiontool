using Dpt.Shell.Operations;

namespace Dpt.App.Services;

public sealed record ConflictInfo(string Source, string ExistingTarget, bool IsFolder);

/// <summary>All modal questions the app asks. Implemented with WPF dialogs; replaceable in tests.</summary>
public interface IDialogService
{
    /// <summary>Returns a decision per conflicting source, or null if the owner cancelled.</summary>
    IReadOnlyDictionary<string, ConflictChoice>? ResolveConflicts(IReadOnlyList<ConflictInfo> conflicts, bool isMove);

    /// <summary>Shift+Delete style confirmation. True only when the owner explicitly confirms permanent deletion.</summary>
    bool ConfirmPermanentDelete(IReadOnlyList<string> paths, string reason);

    /// <summary>Items that could not go to the Recycle Bin. True = the owner chose "Permanently delete these".</summary>
    bool AskPermanentDeleteForBlocked(IReadOnlyList<BlockedItem> blocked);

    /// <summary>Up-front warning before recycling. True = continue (blocked items will still pause later).</summary>
    bool WarnBeforeRecycle(IReadOnlyList<(string Path, string Reason)> warnings);

    bool Confirm(string title, string message, string confirmText, bool danger = false);

    string? Prompt(string title, string message, string initial);

    void ShowError(string title, string message);

    void ShowInfo(string title, string message);
}
