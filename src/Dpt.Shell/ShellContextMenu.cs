using System.Runtime.InteropServices;
using System.Text;
using Dpt.Shell.Interop;

namespace Dpt.Shell;

/// <summary>
/// Shows the real Windows shell context menu (including third-party entries, Open with, Send to, New).
/// Verbs the app handles itself (delete, rename, cut, copy, paste…) are returned instead of invoked, so they go
/// through the app's own safe file-operation engine.
/// </summary>
public static class ShellContextMenu
{
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;

    private static IContextMenu2? _active2;
    private static IContextMenu3? _active3;

    public static readonly IReadOnlySet<string> DefaultInterceptedVerbs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "delete", "rename", "cut", "copy", "paste", "properties", "pintohome" };

    /// <summary>Forward owner-window messages here while a menu is open so submenus (Send to, Open with, New) render.</summary>
    public static IntPtr HandleWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
        if (_active3 is not null && msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
        {
            if (_active3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
            {
                handled = true;
                return result;
            }
        }
        else if (_active2 is not null && msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM)
        {
            if (_active2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
            {
                handled = true;
                return msg == WM_INITMENUPOPUP ? IntPtr.Zero : new IntPtr(1);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Shows the menu for items (which must share one parent folder). Returns an intercepted verb, or null.</summary>
    public static string? ShowForItems(IntPtr owner, IReadOnlyList<string> paths, int screenX, int screenY, bool extended = false,
        IReadOnlySet<string>? interceptVerbs = null)
    {
        if (paths.Count == 0) return null;
        var absolute = new List<IntPtr>();
        IntPtr parentPtr = IntPtr.Zero, menuPtr = IntPtr.Zero;
        try
        {
            foreach (var p in paths)
            {
                if (NativeMethods.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) continue;
                absolute.Add(pidl);
            }

            if (absolute.Count == 0) return null;
            if (NativeMethods.SHBindToParent(absolute[0], NativeMethods.IID_IShellFolder, out parentPtr, out _) != 0) return null;
            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
            var children = absolute.Select(NativeMethods.ILFindLastID).ToArray();
            parent.GetUIObjectOf(owner, (uint)children.Length, children, NativeMethods.IID_IContextMenu, IntPtr.Zero, out menuPtr);
            return Track(owner, menuPtr, screenX, screenY, extended, canRename: true, interceptVerbs ?? DefaultInterceptedVerbs);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (var p in absolute) NativeMethods.ILFree(p);
        }
    }

    /// <summary>Shows the folder background menu (New, Paste, Open in Terminal, …). Returns an intercepted verb, or null.</summary>
    public static string? ShowForFolderBackground(IntPtr owner, string folder, int screenX, int screenY, bool extended = false,
        IReadOnlySet<string>? interceptVerbs = null)
    {
        IntPtr pidl = IntPtr.Zero, folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero;
        try
        {
            if (NativeMethods.SHParseDisplayName(folder, IntPtr.Zero, out pidl, 0, out _) != 0) return null;
            NativeMethods.SHGetDesktopFolder(out var desktop);
            desktop.BindToObject(pidl, IntPtr.Zero, NativeMethods.IID_IShellFolder, out folderPtr);
            var shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            shellFolder.CreateViewObject(owner, NativeMethods.IID_IContextMenu, out menuPtr);
            return Track(owner, menuPtr, screenX, screenY, extended, canRename: false, interceptVerbs ?? DefaultInterceptedVerbs);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            if (pidl != IntPtr.Zero) NativeMethods.ILFree(pidl);
        }
    }

    private static string? Track(IntPtr owner, IntPtr menuPtr, int x, int y, bool extended, bool canRename, IReadOnlySet<string> intercept)
    {
        var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
        var hmenu = NativeMethods.CreatePopupMenu();
        try
        {
            var flags = NativeMethods.CMF_NORMAL | NativeMethods.CMF_EXPLORE;
            if (canRename) flags |= NativeMethods.CMF_CANRENAME;
            if (extended) flags |= NativeMethods.CMF_EXTENDEDVERBS;
            if (menu.QueryContextMenu(hmenu, 0, FirstCommand, LastCommand, flags) < 0) return null;

            _active3 = menu as IContextMenu3;
            _active2 = _active3 is null ? menu as IContextMenu2 : null;
            var cmd = NativeMethods.TrackPopupMenuEx(hmenu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON, x, y, owner, IntPtr.Zero);
            _active2 = null;
            _active3 = null;
            if (cmd == 0) return null;

            var verb = new StringBuilder(256);
            var hasVerb = menu.GetCommandString((UIntPtr)(cmd - FirstCommand), NativeMethods.GCS_VERBW, IntPtr.Zero, verb, (uint)verb.Capacity) == 0;
            var verbText = hasVerb ? verb.ToString() : "";
            if (verbText.Length > 0 && intercept.Contains(verbText)) return verbText.ToLowerInvariant();

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = NativeMethods.CMIC_MASK_UNICODE | NativeMethods.CMIC_MASK_PTINVOKE,
                hwnd = owner,
                lpVerb = (IntPtr)(cmd - FirstCommand),
                lpVerbW = (IntPtr)(cmd - FirstCommand),
                nShow = 1,
                ptInvoke = new POINT { X = x, Y = y },
            };
            menu.InvokeCommand(ref info);
            return null;
        }
        finally
        {
            NativeMethods.DestroyMenu(hmenu);
        }
    }
}
