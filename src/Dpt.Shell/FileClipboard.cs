using System.Collections.Specialized;
using System.Windows;

namespace Dpt.Shell;

public enum ClipboardFileAction
{
    None,
    Copy,
    Cut,
}

/// <summary>
/// Puts files on the clipboard the same way File Explorer does (CF_HDROP + "Preferred DropEffect"),
/// so copy/cut/paste works in both directions with Explorer and other apps. Call on an STA thread.
/// </summary>
public static class FileClipboard
{
    private const string PreferredDropEffect = "Preferred DropEffect";
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    public static void SetFiles(IEnumerable<string> paths, ClipboardFileAction action)
    {
        var list = new StringCollection();
        foreach (var p in paths) list.Add(p);
        if (list.Count == 0) return;
        var data = new DataObject();
        data.SetFileDropList(list);
        var effect = action == ClipboardFileAction.Cut ? DropEffectMove : DropEffectCopy;
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(effect)));
        Retry(() => Clipboard.SetDataObject(data, copy: true));
    }

    public static (IReadOnlyList<string> Paths, ClipboardFileAction Action) GetFiles()
    {
        IDataObject? data = null;
        Retry(() => data = Clipboard.GetDataObject());
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return ([], ClipboardFileAction.None);
        var files = data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var action = ClipboardFileAction.Copy;
        if (data.GetDataPresent(PreferredDropEffect) && data.GetData(PreferredDropEffect) is MemoryStream ms)
        {
            var bytes = new byte[4];
            ms.Position = 0;
            if (ms.Read(bytes, 0, 4) == 4 && (BitConverter.ToInt32(bytes, 0) & DropEffectMove) != 0) action = ClipboardFileAction.Cut;
        }

        return (files, action);
    }

    public static bool HasFiles()
    {
        var result = false;
        Retry(() => result = Clipboard.ContainsFileDropList());
        return result;
    }

    public static void Clear() => Retry(Clipboard.Clear);

    public static void SetText(string text) => Retry(() => Clipboard.SetText(text));

    // The clipboard is a shared resource; another process may hold it briefly.
    private static void Retry(Action action)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (i < 10)
            {
                Thread.Sleep(50);
            }
        }
    }
}
