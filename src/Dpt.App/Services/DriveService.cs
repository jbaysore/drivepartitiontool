using System.Collections.ObjectModel;
using System.Windows;
using Dpt.Core.Formatting;

namespace Dpt.App.Services;

/// <summary>A mounted volume as shown in the navigation pane and Home ("Windows SSD · C:").</summary>
public sealed record VolumeSummary(string Root, string Letter, string Label, string Format, DriveType Type, long Total, long Free)
{
    public long Used => Total - Free;
    public double UsedFraction => Total <= 0 ? 0 : (double)Used / Total;
    public bool IsRemovable => Type == DriveType.Removable;
    public string DisplayName => $"{Label} ({Letter})";
    public string FreeText => $"{SizeFormatter.Format(Free)} free of {SizeFormatter.Format(Total)}";
    public bool IsSystem => string.Equals(Root, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Tracks mounted volumes; refreshed on device-change messages.</summary>
public sealed class DriveService
{
    public ObservableCollection<VolumeSummary> Volumes { get; } = [];

    public event EventHandler? Changed;

    public void Refresh()
    {
        var list = new List<VolumeSummary>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                    ? d.DriveType == DriveType.Removable ? "USB drive" : d.DriveType == DriveType.Network ? "Network drive" : "Local Disk"
                    : d.VolumeLabel;
                list.Add(new VolumeSummary(d.Name, d.Name.TrimEnd('\\'), label, d.DriveFormat, d.DriveType, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        void Apply()
        {
            if (list.SequenceEqual(Volumes)) return;
            Volumes.Clear();
            foreach (var v in list) Volumes.Add(v);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else dispatcher.Invoke(Apply);
    }
}
