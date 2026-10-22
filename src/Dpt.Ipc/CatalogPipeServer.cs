using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Dpt.Ipc;

/// <summary>
/// Serves an <see cref="ICatalog"/> over a named pipe. Only the owner's account, Administrators and SYSTEM can connect.
/// The protocol is read-only apart from the validated "selected volumes" setting.
/// </summary>
public sealed class CatalogPipeServer(ICatalog catalog, IReadOnlyList<SecurityIdentifier> allowed, string pipeName = PipeProtocol.CatalogPipeName) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _listeners = [];

    public Action<Exception>? OnError { get; set; }

    public static PipeSecurity BuildSecurity(IEnumerable<SecurityIdentifier> allowed)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        foreach (var sid in allowed) security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    public void Start(int listeners = 4)
    {
        for (var i = 0; i < listeners; i++) _listeners.Add(Task.Run(() => ListenLoop(_cts.Token)));
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        var security = BuildSecurity(allowed);
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(ct);
                var connection = pipe;
                pipe = null;
                _ = Task.Run(() => Serve(connection, ct), ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                OnError?.Invoke(ex);
                await Task.Delay(500, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var request = await PipeProtocol.ReadAsync<PipeRequest>(pipe, ct);
                if (request is null) return;
                if (request.Method == CatalogMethods.Subscribe)
                {
                    await StreamChanges(pipe, ct);
                    return;
                }

                PipeResponse response;
                try
                {
                    var payload = await Dispatch(request, ct);
                    response = new PipeResponse(true, null, payload);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    response = new PipeResponse(false, ex.Message, null);
                }

                await PipeProtocol.WriteAsync(pipe, response, ct);
            }
        }
        catch (IOException)
        {
            // client went away
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<JsonElement?> Dispatch(PipeRequest r, CancellationToken ct)
    {
        T P<T>() => r.Payload is { } p ? p.Deserialize<T>(PipeProtocol.Json)! : throw new InvalidDataException("Missing payload.");
        static JsonElement E<T>(T value) => JsonSerializer.SerializeToElement(value, PipeProtocol.Json);
        switch (r.Method)
        {
            case CatalogMethods.Status:
                return E(await catalog.GetStatusAsync(ct) with { IsService = true });
            case CatalogMethods.Search:
                return E(await catalog.SearchAsync(Clamp(P<SearchRequest>()), ct));
            case CatalogMethods.FolderSizes:
                return E(await catalog.GetFolderSizesAsync(P<List<string>>().Take(10_000).ToList(), ct));
            case CatalogMethods.Largest:
                var lr = P<LargestRequest>();
                return E(await catalog.GetLargestAsync(lr with { Count = Math.Clamp(lr.Count, 1, 5000) }, ct));
            case CatalogMethods.ListFolder:
                return E(await catalog.ListFolderAsync(P<string>(), ct));
            case CatalogMethods.SetSelectedVolumes:
                var roots = P<List<string>>();
                if (roots.Count > 64 || roots.Any(x => x.Length is < 2 or > 260 || x.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0))
                    throw new InvalidDataException("Invalid volume list.");
                await catalog.SetSelectedVolumesAsync(roots, ct);
                return null;
            default:
                throw new InvalidDataException($"Unknown method '{r.Method}'.");
        }
    }

    private static SearchRequest Clamp(SearchRequest r) => r with { Limit = Math.Clamp(r.Limit, 1, 5000), Offset = Math.Max(0, r.Offset) };

    private async Task StreamChanges(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var queue = System.Threading.Channels.Channel.CreateBounded<IReadOnlyList<CatalogChange>>(
            new System.Threading.Channels.BoundedChannelOptions(256) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
        void Handler(object? s, IReadOnlyList<CatalogChange> c) => queue.Writer.TryWrite(c);
        catalog.Changed += Handler;
        try
        {
            await PipeProtocol.WriteAsync(pipe, new PipeResponse(true, null, null), ct);
            await foreach (var batch in queue.Reader.ReadAllAsync(ct))
            {
                if (!pipe.IsConnected) return;
                await PipeProtocol.WriteAsync(pipe, batch, ct);
            }
        }
        finally
        {
            catalog.Changed -= Handler;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll([.. _listeners], TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
    }
}
