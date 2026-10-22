using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dpt.Ipc;

/// <summary>Length-prefixed (int32) UTF-8 JSON messages over a pipe stream.</summary>
public static class PipeProtocol
{
    public const string CatalogPipeName = "DrivePartitionTool.Catalog";
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, ct)) return default;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageBytes) throw new InvalidDataException("Message too large.");
        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, ct)) return default;
        return JsonSerializer.Deserialize<T>(body, Json);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }

        return true;
    }
}

public sealed record PipeRequest(string Method, JsonElement? Payload);

public sealed record PipeResponse(bool Ok, string? Error, JsonElement? Payload);

public static class CatalogMethods
{
    public const string Status = "status";
    public const string Search = "search";
    public const string FolderSizes = "folderSizes";
    public const string Largest = "largest";
    public const string ListFolder = "listFolder";
    public const string SetSelectedVolumes = "setSelectedVolumes";
    public const string Subscribe = "subscribe";
    public const string DiskHealth = "diskHealth";
}
