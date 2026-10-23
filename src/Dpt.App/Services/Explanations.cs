namespace Dpt.App.Services;

/// <summary>
/// Plain-language "What this means" texts for disk tools. Neutral by design: they describe consequences and never
/// recommend one option over another (scope Q17).
/// </summary>
public static class Explanations
{
    public const string Partition = "A partition is a section of a physical drive. Windows usually shows each partition with its own letter, like C: or D:. Changing partitions changes how the drive is divided, not just which folder files are in.";

    public const string Shrink = "Makes this partition smaller and leaves the freed space unallocated (unused) at its end. Files stay, as long as they fit in the new size; Windows refuses sizes that would cut into data. You can grow it back later if nothing else uses the freed space.";

    public const string Extend = "Makes this partition bigger using unallocated space directly after it. Files stay. This only works when there is free (unallocated) space right after the partition.";

    public const string Create = "Makes a new partition in unallocated space and prepares (formats) it so it can hold files. Nothing else on the drive changes.";

    public const string Delete = "Removes this partition. Every file on it becomes unreadable through normal means, and the space becomes unallocated. This can't be undone by the app — copy anything you need first.";

    public const string Format = "Erases this partition and sets it up fresh with the chosen file system. Every file on it is lost. This can't be undone by the app.";

    public const string Letter = "Changes which letter Windows uses for this partition (for example E: instead of F:). Files don't change, but shortcuts or programs that point at the old letter will stop finding them. Without a letter, the partition is hidden from File Explorer but its files stay intact.";

    public const string Label = "Changes the name shown next to the drive letter. Nothing else changes.";

    public const string FileSystems = "NTFS suits internal drives and Windows; exFAT suits USB drives shared with Macs, TVs and cameras; FAT32 is the oldest and most widely readable, but can't store files bigger than 4 GB.";

    public const string Protected = "This partition is needed to start Windows (or is reserved by it). Changing it can stop the PC from starting. It's locked unless you turn on \"Allow changes to system partitions\" in Settings.";

    public const string DevelopmentBuild = "This is a development build: disk changes are only allowed on virtual disks (VHD files). Physical drives are shown but can't be changed.";
}
