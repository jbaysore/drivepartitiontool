using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.Services;

public sealed record BootHealthSnapshot(IReadOnlyList<DiskInfo> Disks, BootConfigurationInfo Configuration);

public static class BootHealthService
{
    public static async Task<BootHealthSnapshot> ReadAsync(CancellationToken token)
    {
        var disks = await Task.Run(() => StorageInventory.GetDisks(), token);
        BootConfigurationInfo configuration;
        try
        {
            configuration = AppServices.Current.Catalog.Catalog is CatalogPipeClient client
                ? await client.GetBootConfigurationAsync(token)
                : await BootConfigurationReader.ReadAsync(token);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        { configuration = new(false, "", "The background service could not provide boot configuration. It may need an upgrade when administrator access is available. " + ex.Message); }
        return new(disks, configuration);
    }
}
