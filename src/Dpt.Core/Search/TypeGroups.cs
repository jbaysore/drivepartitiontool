namespace Dpt.Core.Search;

/// <summary>Named groups of file extensions used by "type:" filters and rule conditions.</summary>
public sealed class TypeGroups
{
    public const string DefaultDefinition =
        "image=jpg,jpeg,png,gif,bmp,tif,tiff,webp,heic,heif,raw,cr2,cr3,nef,arw,dng,svg,ico;" +
        "video=mp4,mkv,mov,avi,wmv,webm,m4v,mpg,mpeg,3gp,mts,m2ts;" +
        "audio=mp3,wav,flac,aac,m4a,ogg,wma,opus,aiff;" +
        "doc=pdf,doc,docx,xls,xlsx,ppt,pptx,txt,rtf,odt,ods,odp,md,csv,epub;" +
        "archive=zip,rar,7z,tar,gz,bz2,xz,iso,cab;" +
        "code=cs,js,ts,py,java,cpp,c,h,go,rs,json,xml,html,css,ps1,sh,sql,yml,yaml;" +
        "app=exe,msi,msix,appx,bat,cmd,lnk";

    private readonly Dictionary<string, HashSet<string>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _extToGroup = new(StringComparer.OrdinalIgnoreCase);

    public TypeGroups(string? definition = null)
    {
        foreach (var part in (definition ?? DefaultDefinition).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var name = part[..eq].Trim();
            var exts = part[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _groups[name] = exts;
            foreach (var e in exts) _extToGroup.TryAdd(e, name);
        }
    }

    public static TypeGroups Default { get; } = new();

    public IEnumerable<string> Names => _groups.Keys;

    public bool TryGet(string name, out IReadOnlySet<string> extensions)
    {
        if (_groups.TryGetValue(name, out var set))
        {
            extensions = set;
            return true;
        }

        extensions = new HashSet<string>();
        return false;
    }

    /// <summary>Group name for an extension (without dot), or "other".</summary>
    public string GroupOf(string extension) =>
        _extToGroup.TryGetValue(extension.TrimStart('.'), out var g) ? g : "other";
}
