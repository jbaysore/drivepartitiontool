using Dpt.Shell.Interop;

namespace Dpt.Shell.Operations;

/// <summary>
/// The rule that keeps recycling from silently becoming permanent deletion: when Windows announces a delete
/// without <c>TSF_DELETE_RECYCLE_IF_POSSIBLE</c>, the item would be destroyed, so it must be blocked.
/// </summary>
public static class RecycleGuard
{
    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED).</summary>
    public const int BlockHResult = unchecked((int)0x800704C7);

    public static bool WouldBePermanent(uint preDeleteFlags) =>
        (preDeleteFlags & TransferSourceFlags.TSF_DELETE_RECYCLE_IF_POSSIBLE) == 0;
}
