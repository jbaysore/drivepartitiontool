using System.IO.Pipes;
using System.Text.Json;

namespace Dpt.Ipc;

/// <summary>Talks to the background catalog service. One short-lived connection per call; a separate one for change events.</summary>
public sealed class CatalogPipeClient(string pipeName = PipeProtocol.CatalogPipeName) : ICatalog, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private EventHandler<IReadOnlyList<CatalogChange>>? _changed;
    private Task? _subscription;

    /// <summary>True if the service answers within the timeout.</summary>
    public static async Task<bool> IsAvailableAsync(string pipeName = PipeProtocol.CatalogPipeName, int timeoutMs = 400)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeoutMs);
            await pipe.ConnectAsync(cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<T> Call<T>(string method, object? payload, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(3000);
        await pipe.ConnectAsync(connect.Token);
        var element = payload is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(payload, PipeProtocol.Json);
        await PipeProtocol.WriteAsync(pipe, new PipeRequest(method, element), ct);
        var response = await PipeProtocol.ReadAsync<PipeResponse>(pipe, ct) ?? throw new IOException("The catalog service closed the connection.");
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Catalog error.");
        return response.Payload is { } p ? p.Deserialize<T>(PipeProtocol.Json)! : default!;
    }

    public Task<CatalogStatus> GetStatusAsync(CancellationToken ct = default) => Call<CatalogStatus>(CatalogMethods.Status, null, ct);

    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct = default) => Call<SearchResponse>(CatalogMethods.Search, request, ct);

    public async Task<IReadOnlyList<long?>> GetFolderSizesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
        await Call<List<long?>>(CatalogMethods.FolderSizes, paths, ct);

    public async Task<IReadOnlyList<CatalogItem>> GetLargestAsync(LargestRequest request, CancellationToken ct = default) =>
        await Call<List<CatalogItem>>(CatalogMethods.Largest, request, ct);

    public async Task<IReadOnlyList<CatalogItem>> ListFolderAsync(string path, CancellationToken ct = default) =>
        await Call<List<CatalogItem>>(CatalogMethods.ListFolder, path, ct);

    public Task SetSelectedVolumesAsync(IReadOnlyList<string> roots, CancellationToken ct = default) =>
        Call<object>(CatalogMethods.SetSelectedVolumes, roots, ct);

    public event EventHandler<IReadOnlyList<CatalogChange>>? Changed
    {
        add
        {
            _changed += value;
            _subscription ??= Task.Run(() => SubscribeLoop(_cts.Token));
        }
        remove => _changed -= value;
    }

    private async Task SubscribeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, ct);
                await PipeProtocol.WriteAsync(pipe, new PipeRequest(CatalogMethods.Subscribe, null), ct);
                _ = await PipeProtocol.ReadAsync<PipeResponse>(pipe, ct);
                while (!ct.IsCancellationRequested)
                {
                    var batch = await PipeProtocol.ReadAsync<List<CatalogChange>>(pipe, ct);
                    if (batch is null) break;
                    _changed?.Invoke(this, batch);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or UnauthorizedAccessException)
            {
            }

            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => _cts.Cancel();
}
