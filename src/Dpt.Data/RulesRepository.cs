using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dpt.Core.Rules;

namespace Dpt.Data;

public sealed record StoredRule(long Id, RuleDefinition Definition, bool Enabled, bool Previewed, int Sort, DateTimeOffset? LastRun);

/// <summary>Rules (and folder layouts) stored as JSON documents.</summary>
public sealed class RulesRepository(AppDatabase db)
{
    public static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };

    public event EventHandler? Changed;

    public IReadOnlyList<StoredRule> GetRules()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, definition, enabled, previewed, sort, last_run FROM rules ORDER BY sort, id";
        using var r = cmd.ExecuteReader();
        var list = new List<StoredRule>();
        while (r.Read())
        {
            var def = JsonSerializer.Deserialize<RuleDefinition>(r.GetString(1), Json) ?? new RuleDefinition();
            list.Add(new StoredRule(r.GetInt64(0), def, r.GetInt64(2) != 0, r.GetInt64(3) != 0, r.GetInt32(4),
                r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture)));
        }

        return list;
    }

    public StoredRule? Get(long id) => GetRules().FirstOrDefault(r => r.Id == id);

    public long Save(long? id, RuleDefinition definition, bool enabled = true)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        if (id is { } existing)
        {
            // Changing what a moving rule does means it must be previewed again before it moves anything.
            var before = Get(existing);
            var resetPreview = before is null || before.Definition.MoveTo != definition.MoveTo ||
                               JsonSerializer.Serialize(before.Definition.Conditions, Json) != JsonSerializer.Serialize(definition.Conditions, Json) ||
                               !before.Definition.Folders.SequenceEqual(definition.Folders);
            cmd.CommandText = $"UPDATE rules SET name = $n, definition = $d, enabled = $e{(resetPreview ? ", previewed = 0" : "")} WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", existing);
        }
        else
        {
            cmd.CommandText = "INSERT INTO rules(name, definition, enabled, sort) VALUES($n, $d, $e, COALESCE((SELECT MAX(sort) + 1 FROM rules), 0)); SELECT last_insert_rowid();";
        }

        cmd.Parameters.AddWithValue("$n", definition.Name);
        cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(definition, Json));
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        var result = cmd.ExecuteScalar();
        Changed?.Invoke(this, EventArgs.Empty);
        return id ?? (long)result!;
    }

    public void SetEnabled(long id, bool enabled) => Exec("UPDATE rules SET enabled = $v WHERE id = $id", id, enabled ? 1 : 0);

    public void MarkPreviewed(long id) => Exec("UPDATE rules SET previewed = 1 WHERE id = $id", id, 1);

    public void MarkRun(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE rules SET last_run = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(long id) => Exec("DELETE FROM rules WHERE id = $id", id, 0);

    private void Exec(string sql, long id, int value)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- Layouts (M6) ----------------

    public IReadOnlyList<(long Id, string Name, string Json)> GetLayouts()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, definition FROM layouts ORDER BY name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public long SaveLayout(long? id, string name, string json)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = id is null
            ? "INSERT INTO layouts(name, definition) VALUES($n, $d); SELECT last_insert_rowid();"
            : "UPDATE layouts SET name = $n, definition = $d WHERE id = $id; SELECT $id;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$d", json);
        if (id is not null) cmd.Parameters.AddWithValue("$id", id);
        var r = (long)cmd.ExecuteScalar()!;
        Changed?.Invoke(this, EventArgs.Empty);
        return r;
    }

    public void DeleteLayout(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM layouts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
