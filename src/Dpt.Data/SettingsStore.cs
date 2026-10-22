using System.Text.Json;
using System.Text.Json.Serialization;
using Dpt.Core.Settings;

namespace Dpt.Data;

/// <summary>Loads and saves <see cref="AppSettings"/> as one JSON document.</summary>
public sealed class SettingsStore(AppDatabase db)
{
    private const string Key = "app";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Load()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", Key);
        if (cmd.ExecuteScalar() is not string json) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", Key);
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(settings, Json));
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, settings);
    }
}
