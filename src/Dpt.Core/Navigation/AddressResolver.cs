namespace Dpt.Core.Navigation;

/// <summary>Turns what the user typed in the address bar into a location.</summary>
/// <remarks>Supports plain paths, %ENV% variables, "shell:" folders and the words "home"/"drives"/"settings".</remarks>
public sealed class AddressResolver(Func<string, string?> expandEnvironment, Func<string, string?> resolveShellFolder)
{
    public Location? Resolve(string typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var text = typed.Trim().Trim('"');

        switch (text.ToLowerInvariant())
        {
            case "home": return Location.Home;
            case "drives" or "this pc": return Location.Page(LocationKind.Drives);
            case "settings": return Location.Page(LocationKind.Settings);
            case "largest" or "largest items": return Location.Page(LocationKind.LargestItems);
            case "rules": return Location.Page(LocationKind.Rules);
            case "layouts": return Location.Page(LocationKind.Layouts);
        }

        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[6..];
            var tail = "";
            var slash = rest.IndexOfAny(['\\', '/']);
            if (slash >= 0)
            {
                tail = rest[(slash + 1)..];
                rest = rest[..slash];
            }

            var folder = resolveShellFolder(rest);
            if (folder is null) return null;
            return Location.Folder(tail.Length > 0 ? folder.TrimEnd('\\') + "\\" + tail : folder);
        }

        if (text.Contains('%'))
        {
            var expanded = expandEnvironment(text);
            if (expanded is null) return null;
            text = expanded;
        }

        if (text.Length == 1 && char.IsLetter(text[0])) text += ":";
        var isAbsolute = (text.Length >= 2 && text[1] == ':') || text.StartsWith(@"\\", StringComparison.Ordinal);
        return isAbsolute ? Location.Folder(text) : null;
    }
}
