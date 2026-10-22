using System.Globalization;
using Dpt.Core.Locations;

namespace Dpt.Data;

public sealed record Pin(long Id, string Path, string? Label, int Sort)
{
    public string Display => string.IsNullOrWhiteSpace(Label) ? Dpt.Core.Navigation.PathUtil.DisplayName(Path) : Label;
}

/// <summary>Pinned locations and visit history.</summary>
public sealed class LocationsRepository(AppDatabase db)
{
    public event EventHandler? PinsChanged;

    public IReadOnlyList<Pin> GetPins()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, path, label, sort FROM pins ORDER BY sort, id";
        using var r = cmd.ExecuteReader();
        var list = new List<Pin>();
        while (r.Read()) list.Add(new Pin(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3)));
        return list;
    }

    public bool IsPinned(string path) => GetPins().Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    public void AddPin(string path, string? label = null)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO pins(path, label, sort) VALUES($p, $l, COALESCE((SELECT MAX(sort) + 1 FROM pins), 0))";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$l", (object?)label ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        PinsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemovePin(string path)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM pins WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
        PinsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenamePin(string path, string? label)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE pins SET label = $l WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$l", string.IsNullOrWhiteSpace(label) ? DBNull.Value : label);
        cmd.ExecuteNonQuery();
        PinsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves a pin up (-1) or down (+1).</summary>
    public void MovePin(string path, int delta)
    {
        var pins = GetPins().ToList();
        var i = pins.FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        var j = i + delta;
        if (i < 0 || j < 0 || j >= pins.Count) return;
        (pins[i], pins[j]) = (pins[j], pins[i]);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        for (var k = 0; k < pins.Count; k++)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE pins SET sort = $s WHERE id = $id";
            cmd.Parameters.AddWithValue("$s", k);
            cmd.Parameters.AddWithValue("$id", pins[k].Id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        PinsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RecordVisit(string path, DateTimeOffset when)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO visits(path, count, last_visited) VALUES($p, 1, $t)
            ON CONFLICT(path) DO UPDATE SET count = count + 1, last_visited = excluded.last_visited
            """;
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$t", when.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<VisitStats> GetVisits()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, count, last_visited FROM visits";
        using var r = cmd.ExecuteReader();
        var list = new List<VisitStats>();
        while (r.Read())
            list.Add(new VisitStats(r.GetString(0), r.GetInt32(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture)));
        return list;
    }

    public void ForgetVisit(string path)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM visits WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }
}

