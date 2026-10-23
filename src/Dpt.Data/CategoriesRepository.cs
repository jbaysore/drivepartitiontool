using System.Globalization;

namespace Dpt.Data;

public sealed record Category(long Id, string Name, string Color, long? ParentId);

/// <summary>A tagged item. <see cref="FileRef"/>/<see cref="VolumeSerial"/> are 0 when the file system has no stable IDs.</summary>
public sealed record ItemTag(long Id, uint VolumeSerial, ulong FileRef, string Path, long CategoryId, long Created = 0);

/// <summary>
/// User categories and which items carry them. Tags are keyed by the file's volume serial + file ID so they follow
/// renames and moves on the same drive; the stored path is a snapshot refreshed whenever the item is seen again.
/// </summary>
public sealed class CategoriesRepository(AppDatabase db)
{
    public static readonly string[] Palette = ["#646FD0", "#347C61", "#B84F56", "#9A6A12", "#7A4FB8", "#2F7FA8", "#B8632F", "#5E7A2F"];

    public event EventHandler? Changed;

    public IReadOnlyList<Category> GetCategories()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, color, parent_id FROM categories ORDER BY name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var list = new List<Category>();
        while (r.Read()) list.Add(new Category(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
        return list;
    }

    public Category? Find(string name) => GetCategories().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public Category GetOrCreate(string name, string? color = null)
    {
        if (Find(name) is { } existing) return existing;
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO categories(name, color) VALUES($n, $c)";
            cmd.Parameters.AddWithValue("$n", name.Trim());
            cmd.Parameters.AddWithValue("$c", color ?? Palette[GetCategories().Count % Palette.Length]);
            cmd.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Find(name)!;
    }

    public void Rename(long id, string name, string color)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE categories SET name = $n, color = $c WHERE id = $id";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$c", color);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM categories WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Dictionary<long, int> Counts()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT category_id, COUNT(*) FROM item_tags GROUP BY category_id";
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<long, int>();
        while (r.Read()) d[r.GetInt64(0)] = r.GetInt32(1);
        return d;
    }

    // ---------------- Tags ----------------

    /// <summary>Adds a tag. Returns false if the item already had it.</summary>
    public bool Tag(string path, uint volumeSerial, ulong fileRef, long categoryId, long created = 0)
    {
        if (TagsFor(path, volumeSerial, fileRef, created).Any(t => t.CategoryId == categoryId)) return false;
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO item_tags(volume_serial, file_ref, path, category_id, created) VALUES($s, $r, $p, $c, $t)";
            cmd.Parameters.AddWithValue("$t", created);
            cmd.Parameters.AddWithValue("$s", (long)volumeSerial);
            cmd.Parameters.AddWithValue("$r", unchecked((long)fileRef));
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$c", categoryId);
            cmd.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Untag(string path, uint volumeSerial, ulong fileRef, long categoryId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = fileRef != 0
            ? "DELETE FROM item_tags WHERE category_id = $c AND volume_serial = $s AND file_ref = $r"
            : "DELETE FROM item_tags WHERE category_id = $c AND file_ref = 0 AND path = $p";
        cmd.Parameters.AddWithValue("$c", categoryId);
        cmd.Parameters.AddWithValue("$s", (long)volumeSerial);
        cmd.Parameters.AddWithValue("$r", unchecked((long)fileRef));
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tags for one item (by file ID when available, otherwise by path). Refreshes the stored path if it moved.</summary>
    public IReadOnlyList<ItemTag> TagsFor(string path, uint volumeSerial, ulong fileRef, long created = 0)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = fileRef != 0
            ? "SELECT id, volume_serial, file_ref, path, category_id, created FROM item_tags WHERE volume_serial = $s AND file_ref = $r"
            : "SELECT id, volume_serial, file_ref, path, category_id, created FROM item_tags WHERE file_ref = 0 AND path = $p";
        cmd.Parameters.AddWithValue("$s", (long)volumeSerial);
        cmd.Parameters.AddWithValue("$r", unchecked((long)fileRef));
        cmd.Parameters.AddWithValue("$p", path);
        var list = Read(cmd);
        if (fileRef != 0 && created != 0 && list.Any(t => t.Created != 0 && t.Created != created))
        {
            // Same file ID but a different creation time: the tagged file was deleted and Windows reused its ID.
            using var stale = c.CreateCommand();
            stale.CommandText = "DELETE FROM item_tags WHERE volume_serial = $s AND file_ref = $r AND created <> 0 AND created <> $t";
            stale.Parameters.AddWithValue("$s", (long)volumeSerial);
            stale.Parameters.AddWithValue("$r", unchecked((long)fileRef));
            stale.Parameters.AddWithValue("$t", created);
            stale.ExecuteNonQuery();
            list = list.Where(t => t.Created == 0 || t.Created == created).ToList();
        }

        if (list.Any(t => !string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            using var up = c.CreateCommand();
            up.CommandText = "UPDATE item_tags SET path = $p WHERE volume_serial = $s AND file_ref = $r";
            up.Parameters.AddWithValue("$p", path);
            up.Parameters.AddWithValue("$s", (long)volumeSerial);
            up.Parameters.AddWithValue("$r", unchecked((long)fileRef));
            up.ExecuteNonQuery();
            list = list.Select(t => t with { Path = path }).ToList();
        }

        return list;
    }

    /// <summary>All tags of a category (stored paths may be out of date; callers re-resolve by file ID).</summary>
    public IReadOnlyList<ItemTag> TagsOf(long categoryId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, volume_serial, file_ref, path, category_id, created FROM item_tags WHERE category_id = $c";
        cmd.Parameters.AddWithValue("$c", categoryId);
        return Read(cmd);
    }

    public void UpdatePath(long tagId, string path)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE item_tags SET path = $p WHERE id = $id";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$id", tagId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>For path-keyed tags (no file IDs): follow a move/rename the app performed.</summary>
    public void MovePathTags(string from, string to)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE item_tags SET path = $to || substr(path, length($from) + 1)
            WHERE path = $from COLLATE NOCASE OR path LIKE $prefix ESCAPE '^'
            """;
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);
        cmd.Parameters.AddWithValue("$prefix", from.Replace("^", "^^").Replace("%", "^%").Replace("_", "^_") + @"\%");
        cmd.ExecuteNonQuery();
    }

    private static List<ItemTag> Read(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<ItemTag>();
        while (r.Read())
            list.Add(new ItemTag(r.GetInt64(0), (uint)r.GetInt64(1), unchecked((ulong)r.GetInt64(2)), r.GetString(3), r.GetInt64(4), r.GetInt64(5)));
        return list;
    }
}
