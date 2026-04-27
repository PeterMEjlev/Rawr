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
            CREATE TABLE IF NOT EXISTS photos (
                file_name   TEXT PRIMARY KEY,
                rating      INTEGER NOT NULL DEFAULT 0,
                flag        INTEGER NOT NULL DEFAULT 0,
                color_label INTEGER NOT NULL DEFAULT 0,
                group_id    INTEGER NOT NULL DEFAULT 0,
                is_best     INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Load all saved culling state for a folder. Keyed by filename (not full path)
    /// so the data remains valid if the folder is moved.
    /// </summary>
    public Dictionary<string, PhotoState> LoadAll()
    {
        var result = new Dictionary<string, PhotoState>(StringComparer.OrdinalIgnoreCase);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT file_name, rating, flag, color_label, group_id, is_best FROM photos";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = new PhotoState
            {
                Rating = reader.GetInt32(1),
                Flag = (CullFlag)reader.GetInt32(2),
                ColorLabel = (ColorLabel)reader.GetInt32(3),
                GroupId = reader.GetInt32(4),
                IsBestInGroup = reader.GetInt32(5) != 0,
            };
        }

        return result;
    }

    public void Save(PhotoItem photo)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO photos (file_name, rating, flag, color_label, group_id, is_best)
            VALUES ($name, $rating, $flag, $color, $group, $best)
            ON CONFLICT(file_name) DO UPDATE SET
                rating = $rating,
                flag = $flag,
                color_label = $color,
                group_id = $group,
                is_best = $best
            """;
        cmd.Parameters.AddWithValue("$name", photo.FileName);
        cmd.Parameters.AddWithValue("$rating", photo.Rating);
        cmd.Parameters.AddWithValue("$flag", (int)photo.Flag);
        cmd.Parameters.AddWithValue("$color", (int)photo.ColorLabel);
        cmd.Parameters.AddWithValue("$group", photo.GroupId);
        cmd.Parameters.AddWithValue("$best", photo.IsBestInGroup ? 1 : 0);
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
}
