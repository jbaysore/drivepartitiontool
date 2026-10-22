namespace Dpt.Shell.FileSystem;

/// <summary>One item in a folder listing. <see cref="Size"/> is -1 for folders (their size comes from a size provider).</summary>
public sealed record FileEntry(
    string Path,
    string Name,
    bool IsFolder,
    long Size,
    DateTime Modified,
    DateTime Created,
    FileAttributes Attributes)
{
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;
    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Name);
}
