using Microsoft.Data.Sqlite;
using Rawr.Core.Models;

namespace Rawr.Core.Data;

/// <summary>
/// Persists culling decisions (ratings, flags, labels, groups) in a SQLite database.
/// One database per folder, stored as ".rawr/culling.db" inside the photo folder.
/// This avoids modifying RAW files and keeps metadata portable with the folder.
/// </summary>
public sealed class CullingDatabase : IDisposable
{
    private readonly SqliteConnection _db;

    private CullingDatabase(SqliteConnection db)
    {
        _db = db;
    }

    public static CullingDatabase Open(string folderPath)
    {
        var rawrDir = Path.Combine(folderPath, ".rawr");
        Directory.CreateDirectory(rawrDir);

        var dbPath = Path.Combine(rawrDir, "culling.db");
        var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();

        var instance = new CullingDatabase(db);
        instance.EnsureSchema();
        return instance;
    }

    private void EnsureSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS photos (
                file_name   TEXT PRIMARY KEY,
                rating      INTEGER NOT NULL DEFAULT 0,
                flag        INTEGER NOT NULL DEFAULT 0,
                color_label INTEGER NOT NULL DEFAULT 0,
                group_id    INTEGER NOT NULL DEFAULT 0,
                is_best     INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS custom_groups (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS photo_groups (
                file_name TEXT NOT NULL,
                group_id  INTEGER NOT NULL,
                PRIMARY KEY (file_name, group_id),
                FOREIGN KEY (group_id) REFERENCES custom_groups(id) ON DELETE CASCADE
            );
            """;
        cmd.ExecuteNonQuery();

        // Migration: phash column added later. SQLite has no IF NOT EXISTS for ADD COLUMN.
        if (!ColumnExists("photos", "phash"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN phash INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Load all saved culling state for a folder. Keyed by filename (not full path)
    /// so the data remains valid if the folder is moved.
    /// </summary>
    public Dictionary<string, PhotoState> LoadAll()
    {
        var result = new Dictionary<string, PhotoState>(StringComparer.OrdinalIgnoreCase);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT file_name, rating, flag, color_label, group_id, is_best, phash FROM photos";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // SQLite stores INTEGER as signed 64-bit; reinterpret to ulong for the unsigned dHash.
            long rawHash = reader.GetInt64(6);
            ulong? phash = rawHash == 0 ? null : unchecked((ulong)rawHash);

            result[reader.GetString(0)] = new PhotoState
            {
                Rating = reader.GetInt32(1),
                Flag = (CullFlag)reader.GetInt32(2),
                ColorLabel = (ColorLabel)reader.GetInt32(3),
                GroupId = reader.GetInt32(4),
                IsBestInGroup = reader.GetInt32(5) != 0,
                Phash = phash,
            };
        }

        return result;
    }

    public void Save(PhotoItem photo)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO photos (file_name, rating, flag, color_label, group_id, is_best, phash)
            VALUES ($name, $rating, $flag, $color, $group, $best, $phash)
            ON CONFLICT(file_name) DO UPDATE SET
                rating = $rating,
                flag = $flag,
                color_label = $color,
                group_id = $group,
                is_best = $best,
                phash = $phash
            """;
        cmd.Parameters.AddWithValue("$name", photo.FileName);
        cmd.Parameters.AddWithValue("$rating", photo.Rating);
        cmd.Parameters.AddWithValue("$flag", (int)photo.Flag);
        cmd.Parameters.AddWithValue("$color", (int)photo.ColorLabel);
        cmd.Parameters.AddWithValue("$group", photo.GroupId);
        cmd.Parameters.AddWithValue("$best", photo.IsBestInGroup ? 1 : 0);
        cmd.Parameters.AddWithValue("$phash", photo.Phash.HasValue ? unchecked((long)photo.Phash.Value) : 0L);
        cmd.ExecuteNonQuery();
    }

    public void SaveBatch(IEnumerable<PhotoItem> photos)
    {
        using var tx = _db.BeginTransaction();
        foreach (var photo in photos)
        {
            Save(photo);
        }
        tx.Commit();
    }

    // ── Custom groups ──

    public List<PhotoTag> LoadGroups()
    {
        var result = new List<PhotoTag>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM custom_groups ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new PhotoTag { Id = reader.GetInt32(0), Name = reader.GetString(1) });
        return result;
    }

    public PhotoTag CreateGroup(string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO custom_groups (name) VALUES ($name) RETURNING id";
        cmd.Parameters.AddWithValue("$name", name);
        var id = Convert.ToInt32(cmd.ExecuteScalar());
        return new PhotoTag { Id = id, Name = name };
    }

    public void DeleteGroup(int id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM custom_groups WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void RenameGroup(int id, string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE custom_groups SET name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, HashSet<int>> LoadAllPhotoGroups()
    {
        var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT file_name, group_id FROM photo_groups";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var fileName = reader.GetString(0);
            if (!result.TryGetValue(fileName, out var set))
                result[fileName] = set = new HashSet<int>();
            set.Add(reader.GetInt32(1));
        }
        return result;
    }

    public void AssignGroup(string fileName, int groupId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO photo_groups (file_name, group_id) VALUES ($name, $group)";
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.AddWithValue("$group", groupId);
        cmd.ExecuteNonQuery();
    }

    public void UnassignGroup(string fileName, int groupId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM photo_groups WHERE file_name = $name AND group_id = $group";
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.AddWithValue("$group", groupId);
        cmd.ExecuteNonQuery();
    }

    public void DeletePhoto(string fileName)
    {
        using var tx = _db.BeginTransaction();

        using var cmd1 = _db.CreateCommand();
        cmd1.Transaction = tx;
        cmd1.CommandText = "DELETE FROM photo_groups WHERE file_name = $name";
        cmd1.Parameters.AddWithValue("$name", fileName);
        cmd1.ExecuteNonQuery();

        using var cmd2 = _db.CreateCommand();
        cmd2.Transaction = tx;
        cmd2.CommandText = "DELETE FROM photos WHERE file_name = $name";
        cmd2.Parameters.AddWithValue("$name", fileName);
        cmd2.ExecuteNonQuery();

        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}

public record PhotoState
{
    public int Rating { get; init; }
    public CullFlag Flag { get; init; }
    public ColorLabel ColorLabel { get; init; }
    public int GroupId { get; init; }
    public bool IsBestInGroup { get; init; }
    public ulong? Phash { get; init; }
}
