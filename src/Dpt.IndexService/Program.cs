using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Dpt.Index;
using Dpt.Ipc;

// DrivePartitionTool search catalog service.
// Runs as LocalSystem so it can read NTFS file tables and change journals; answers read-only queries over a named pipe
// that only the owner's account, Administrators and SYSTEM may open. Use "--console" to run interactively for debugging.

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInfo.Name);
builder.Services.AddHostedService<CatalogHost>();
builder.Build().Run();

public static class ServiceInfo
{
    public const string Name = "DrivePartitionToolCatalog";
    public const string DisplayName = "DrivePartitionTool search catalog";

    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("DPT_SERVICE_DATA") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DrivePartitionTool", "catalog");

    public static string ConfigPath => Path.Combine(Path.GetDirectoryName(DataDirectory)!, "service.json");
}

public sealed record ServiceConfig(List<string> OwnerSids);

public sealed class CatalogHost(ILogger<CatalogHost> log) : BackgroundService
{
    private IndexEngine? _engine;
    private CatalogPipeServer? _server;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var p = Process.GetCurrentProcess()) p.PriorityClass = ProcessPriorityClass.BelowNormal;
        Dpt.Index.Diagnostics.Sink = (area, ex) => log.LogWarning(ex, "{Area}", area);

        var owners = LoadOwners();
        _engine = new IndexEngine(new EngineOptions
        {
            DataDirectory = ServiceInfo.DataDirectory,
            UseMft = IsElevated(),
            IsService = true,
        });
        _engine.Start();
        _server = new CatalogPipeServer(_engine, owners) { OnError = ex => log.LogWarning(ex, "pipe"), ReadBootConfiguration = Dpt.Storage.BootConfigurationReader.ReadAsync };
        _server.Start();
        log.LogInformation("Catalog service started (MFT mode: {Mft}); {Count} owner account(s) allowed.", IsElevated(), owners.Count);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static List<SecurityIdentifier> LoadOwners()
    {
        var list = new List<SecurityIdentifier>();
        try
        {
            if (File.Exists(ServiceInfo.ConfigPath) &&
                JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(ServiceInfo.ConfigPath)) is { } cfg)
            {
                foreach (var s in cfg.OwnerSids)
                {
                    try { list.Add(new SecurityIdentifier(s)); }
                    catch (ArgumentException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        // Interactive/console runs: allow the current account too.
        using var me = WindowsIdentity.GetCurrent();
        if (!me.IsSystem && me.User is { } user && !list.Contains(user)) list.Add(user);
        return list;
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Dispose();
        _engine?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
