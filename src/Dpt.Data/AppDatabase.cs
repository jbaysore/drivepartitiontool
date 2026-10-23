using Microsoft.Data.Sqlite;

namespace Dpt.Data;

/// <summary>
/// The per-user SQLite database (%LOCALAPPDATA%\DrivePartitionTool\user.db, or $DPT_DATA_DIR for tests).
/// Schema changes are appended to <see cref="Migrations"/>; PRAGMA user_version tracks the applied count.
/// </summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    public string DatabasePath { get; }

    public static string DefaultDataDirectory =>
        Environment.GetEnvironmentVariable("DPT_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrivePartitionTool");

    public static AppDatabase OpenDefault() => new(Path.Combine(DefaultDataDirectory, "user.db"));

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    internal static readonly string[] Migrations =
    [
        // 1: settings, pins, visits
        """
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE pins (id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                           label TEXT, sort INTEGER NOT NULL);
        CREATE TABLE visits (path TEXT PRIMARY KEY COLLATE NOCASE, count INTEGER NOT NULL, last_visited TEXT NOT NULL);
        """,
        // 2: operation journal (undo)
        """
        CREATE TABLE journal_batches (id INTEGER PRIMARY KEY AUTOINCREMENT, created TEXT NOT NULL, title TEXT NOT NULL,
                                      source TEXT NOT NULL, undone INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE journal_entries (id INTEGER PRIMARY KEY AUTOINCREMENT,
                                      batch_id INTEGER NOT NULL REFERENCES journal_batches(id) ON DELETE CASCADE,
                                      kind TEXT NOT NULL, source_path TEXT, dest_path TEXT, is_folder INTEGER NOT NULL);
        CREATE INDEX ix_journal_entries_batch ON journal_entries(batch_id);
        """,
        // 3: categories, tags, rules, layouts
        """
        CREATE TABLE categories (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                                 color TEXT NOT NULL, parent_id INTEGER REFERENCES categories(id) ON DELETE SET NULL);
        CREATE TABLE item_tags (id INTEGER PRIMARY KEY AUTOINCREMENT, volume_serial INTEGER NOT NULL, file_ref INTEGER NOT NULL,
                                path TEXT NOT NULL COLLATE NOCASE,
                                category_id INTEGER NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
                                UNIQUE(volume_serial, file_ref, path, category_id));
        CREATE INDEX ix_item_tags_path ON item_tags(path);
        CREATE INDEX ix_item_tags_ref ON item_tags(volume_serial, file_ref);
        CREATE TABLE rules (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, definition TEXT NOT NULL,
                            enabled INTEGER NOT NULL DEFAULT 1, previewed INTEGER NOT NULL DEFAULT 0, sort INTEGER NOT NULL DEFAULT 0,
                            last_run TEXT);
        CREATE TABLE layouts (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, definition TEXT NOT NULL);
        """,
        // 4: creation time with each tag, so a reused file ID (deleted file, new file) isn't mistaken for the tagged item
        """
        ALTER TABLE item_tags ADD COLUMN created INTEGER NOT NULL DEFAULT 0;
        """,
    ];

    private void Migrate()
    {
        using var c = Open();
        using var get = c.CreateCommand();
        get.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(get.ExecuteScalar());
        using var wal = c.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode = WAL;";
        wal.ExecuteNonQuery();
        for (var i = version; i < Migrations.Length; i++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[i] + $"\nPRAGMA user_version = {i + 1};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
