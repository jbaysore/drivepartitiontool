using System.Globalization;

namespace Dpt.Data;

public static class JournalKind
{
    public const string Copy = "copy";
    public const string Move = "move";
    public const string Rename = "rename";
    public const string Create = "create";

    /// <summary>Source = original path, Dest = the item's path inside the Recycle Bin ($R…).</summary>
    public const string Recycle = "recycle";

    /// <summary>Source = item path, Dest = category id. Undo removes the tag.</summary>
    public const string Tag = "tag";
}

public sealed record JournalBatch(long Id, DateTimeOffset Created, string Title, string Source, bool Undone);

public sealed record JournalEntry(long Id, long BatchId, string Kind, string? SourcePath, string? DestPath, bool IsFolder);

/// <summary>Records every file change the app makes so it can be undone (Ctrl+Z, "Undo rule run", "Undo layout").</summary>
public sealed class JournalRepository(AppDatabase db)
{
    public event EventHandler? Changed;

    public long Record(string title, string source, IEnumerable<(string Kind, string? Source, string? Dest, bool IsFolder)> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0) return 0;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        long batchId;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO journal_batches(created, title, source) VALUES($c, $t, $s); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$c", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$s", source);
            batchId = (long)cmd.ExecuteScalar()!;
        }

        foreach (var e in list)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO journal_entries(batch_id, kind, source_path, dest_path, is_folder) VALUES($b, $k, $s, $d, $f)";
            cmd.Parameters.AddWithValue("$b", batchId);
            cmd.Parameters.AddWithValue("$k", e.Kind);
            cmd.Parameters.AddWithValue("$s", (object?)e.Source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$d", (object?)e.Dest ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$f", e.IsFolder ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        Changed?.Invoke(this, EventArgs.Empty);
        return batchId;
    }

    public IReadOnlyList<JournalBatch> GetBatches(int limit, bool includeUndone = false)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, created, title, source, undone FROM journal_batches {(includeUndone ? "" : "WHERE undone = 0")} ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<JournalBatch>();
        while (r.Read())
            list.Add(new JournalBatch(r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0));
        return list;
    }

    public IReadOnlyList<JournalEntry> GetEntries(long batchId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, batch_id, kind, source_path, dest_path, is_folder FROM journal_entries WHERE batch_id = $b ORDER BY id";
        cmd.Parameters.AddWithValue("$b", batchId);
        using var r = cmd.ExecuteReader();
        var list = new List<JournalEntry>();
        while (r.Read())
            list.Add(new JournalEntry(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5) != 0));
        return list;
    }

    public void MarkUndone(long batchId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE journal_batches SET undone = 1 WHERE id = $b";
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keeps only the newest <paramref name="keep"/> batches.</summary>
    public void Trim(int keep)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM journal_batches WHERE id NOT IN (SELECT id FROM journal_batches ORDER BY id DESC LIMIT $n)";
        cmd.Parameters.AddWithValue("$n", keep);
        cmd.ExecuteNonQuery();
    }
}
