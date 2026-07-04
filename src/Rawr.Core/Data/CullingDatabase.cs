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

    // A single SqliteConnection is not thread-safe, but this instance is shared
    // across concurrent background passes (the face-analysis and subject-
    // classifier saves both fire after every folder load and write the same
    // per-folder DBs) plus UI-thread reads. Without serialization their
    // BeginTransaction/ExecuteNonQuery calls collide on the connection and the
    // final save hangs — leaving "Analysing faces N/N" stuck forever. Monitor is
    // reentrant per-thread, so WithTransaction's nested public calls on the same
    // thread don't self-deadlock.
    private readonly object _gate = new();

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

        // WAL turns each commit into a sequential append instead of the default
        // DELETE-mode journal (create journal + two fsyncs + delete per commit).
        // Single-photo Save fires on every rating/flag keystroke, so on HDD/NAS
        // the default mode is the "rating feels sticky" stutter. synchronous=NORMAL
        // is app-crash-safe (worst case on a power cut is losing the last rating,
        // acceptable for culling data); busy_timeout lets the background classifier
        // saves wait out a UI-thread write instead of throwing SQLITE_BUSY. The
        // -wal/-shm companions live inside .rawr/ so folder portability is intact,
        // and filesystems that can't do WAL silently keep the previous behaviour.
        using (var pragma = db.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

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

        // Clipping percentages — stored as REAL with NULL meaning "not yet computed".
        if (!ColumnExists("photos", "highlight_clipped_pct"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN highlight_clipped_pct REAL";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists("photos", "shadow_clipped_pct"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN shadow_clipped_pct REAL";
            alter.ExecuteNonQuery();
        }

        // Face / closed-eye analysis results — populated by the user-triggered
        // "Detect closed eyes" pass. NULL means "not yet analysed".
        if (!ColumnExists("photos", "face_count"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN face_count INTEGER";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists("photos", "closed_eye_count"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN closed_eye_count INTEGER";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists("photos", "min_eye_open_score"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN min_eye_open_score REAL";
            alter.ExecuteNonQuery();
        }

        // Subject-classifier bitmask. NULL means the classifier hasn't run on
        // this photo yet; 0 means it ran and nothing scored above the
        // threshold; >0 is a SubjectTag flag combination.
        if (!ColumnExists("photos", "subject_tags"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN subject_tags INTEGER";
            alter.ExecuteNonQuery();
        }

        // Cached EXIF metadata + grayscale strip so a reopen can skip the per-file
        // EXIF read (thousands of RAW opens + WIC decodes otherwise) and the
        // thumbnail decode that recomputes the strip. meta_size / meta_mtime are the
        // source file's size + last-write ticks at extraction time; the loader trusts
        // the cache only when they still match. All NULL until first computed.
        if (!ColumnExists("photos", "meta_size"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN meta_size INTEGER";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists("photos", "meta_mtime"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN meta_mtime INTEGER";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists("photos", "meta_json"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN meta_json TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists("photos", "gray_strip"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE photos ADD COLUMN gray_strip BLOB";
            alter.ExecuteNonQuery();
        }

        // System tags (e.g. auto-generated HDR) are owned by RAWR — they can't be
        // renamed or deleted from the UI and carry their own pill color.
        if (!ColumnExists("custom_groups", "is_system"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE custom_groups ADD COLUMN is_system INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists("custom_groups", "color"))
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE custom_groups ADD COLUMN color TEXT";
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
        lock (_gate)
        {
        var result = new Dictionary<string, PhotoState>(StringComparer.OrdinalIgnoreCase);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT file_name, rating, flag, color_label, group_id, is_best, phash, highlight_clipped_pct, shadow_clipped_pct, face_count, closed_eye_count, min_eye_open_score, subject_tags, meta_size, meta_mtime, meta_json, gray_strip FROM photos";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // SQLite stores INTEGER as signed 64-bit; reinterpret to ulong for the unsigned dHash.
            long rawHash = reader.GetInt64(6);
            ulong? phash = rawHash == 0 ? null : unchecked((ulong)rawHash);

            float? highlightPct = reader.IsDBNull(7) ? null : (float)reader.GetDouble(7);
            float? shadowPct = reader.IsDBNull(8) ? null : (float)reader.GetDouble(8);
            int? faceCount = reader.IsDBNull(9) ? null : reader.GetInt32(9);
            int? closedEyeCount = reader.IsDBNull(10) ? null : reader.GetInt32(10);
            float? minEyeOpenScore = reader.IsDBNull(11) ? null : (float)reader.GetDouble(11);
            SubjectTag? subjectTags = reader.IsDBNull(12) ? null : (SubjectTag)reader.GetInt32(12);

            long metaSize = reader.IsDBNull(13) ? 0 : reader.GetInt64(13);
            long metaMtime = reader.IsDBNull(14) ? 0 : reader.GetInt64(14);
            string? metaJson = reader.IsDBNull(15) ? null : reader.GetString(15);
            byte[]? grayStrip = reader.IsDBNull(16) ? null : (byte[])reader.GetValue(16);

            result[reader.GetString(0)] = new PhotoState
            {
                Rating = reader.GetInt32(1),
                Flag = (CullFlag)reader.GetInt32(2),
                ColorLabel = (ColorLabel)reader.GetInt32(3),
                GroupId = reader.GetInt32(4),
                IsBestInGroup = reader.GetInt32(5) != 0,
                Phash = phash,
                HighlightClippedPct = highlightPct,
                ShadowClippedPct = shadowPct,
                FaceCount = faceCount,
                ClosedEyeCount = closedEyeCount,
                MinEyeOpenScore = minEyeOpenScore,
                SubjectTags = subjectTags,
                MetaSize = metaSize,
                MetaMtime = metaMtime,
                MetaJson = metaJson,
                GrayStrip = grayStrip,
            };
        }

        return result;
        }
    }

    // Upsert SQL for one photo row. Kept as a constant so Save and SaveBatch share
    // the exact same statement — SaveBatch prepares it once and rebinds per photo.
    private const string UpsertPhotoSql = """
        INSERT INTO photos (file_name, rating, flag, color_label, group_id, is_best, phash, highlight_clipped_pct, shadow_clipped_pct, face_count, closed_eye_count, min_eye_open_score, subject_tags, meta_size, meta_mtime, meta_json, gray_strip)
        VALUES ($name, $rating, $flag, $color, $group, $best, $phash, $hi, $lo, $faces, $closedEyes, $minOpen, $subject, $metaSize, $metaMtime, $metaJson, $grayStrip)
        ON CONFLICT(file_name) DO UPDATE SET
            rating = $rating,
            flag = $flag,
            color_label = $color,
            group_id = $group,
            is_best = $best,
            phash = $phash,
            highlight_clipped_pct = $hi,
            shadow_clipped_pct = $lo,
            face_count = $faces,
            closed_eye_count = $closedEyes,
            min_eye_open_score = $minOpen,
            subject_tags = $subject,
            meta_size = $metaSize,
            meta_mtime = $metaMtime,
            meta_json = $metaJson,
            gray_strip = $grayStrip
        """;

    // Builds the upsert command with every parameter pre-registered (typed, empty
    // value). Callers bind via BindPhoto then execute; SaveBatch reuses one instance
    // across the whole transaction so the statement is prepared exactly once instead
    // of re-parsed per photo (10k preparations on the post-load sweep otherwise).
    private SqliteCommand CreateUpsertCommand()
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = UpsertPhotoSql;
        cmd.Parameters.Add("$name", SqliteType.Text);
        cmd.Parameters.Add("$rating", SqliteType.Integer);
        cmd.Parameters.Add("$flag", SqliteType.Integer);
        cmd.Parameters.Add("$color", SqliteType.Integer);
        cmd.Parameters.Add("$group", SqliteType.Integer);
        cmd.Parameters.Add("$best", SqliteType.Integer);
        cmd.Parameters.Add("$phash", SqliteType.Integer);
        cmd.Parameters.Add("$hi", SqliteType.Real);
        cmd.Parameters.Add("$lo", SqliteType.Real);
        cmd.Parameters.Add("$faces", SqliteType.Integer);
        cmd.Parameters.Add("$closedEyes", SqliteType.Integer);
        cmd.Parameters.Add("$minOpen", SqliteType.Real);
        cmd.Parameters.Add("$subject", SqliteType.Integer);
        cmd.Parameters.Add("$metaSize", SqliteType.Integer);
        cmd.Parameters.Add("$metaMtime", SqliteType.Integer);
        cmd.Parameters.Add("$metaJson", SqliteType.Text);
        cmd.Parameters.Add("$grayStrip", SqliteType.Blob);
        return cmd;
    }

    private static void BindPhoto(SqliteCommand cmd, PhotoItem photo)
    {
        var p = cmd.Parameters;
        p["$name"].Value = photo.FileName;
        p["$rating"].Value = photo.Rating;
        p["$flag"].Value = (int)photo.Flag;
        p["$color"].Value = (int)photo.ColorLabel;
        p["$group"].Value = photo.GroupId;
        p["$best"].Value = photo.IsBestInGroup ? 1 : 0;
        p["$phash"].Value = photo.Phash.HasValue ? unchecked((long)photo.Phash.Value) : 0L;
        p["$hi"].Value = (object?)photo.HighlightClippedPct ?? DBNull.Value;
        p["$lo"].Value = (object?)photo.ShadowClippedPct ?? DBNull.Value;
        p["$faces"].Value = (object?)photo.FaceCount ?? DBNull.Value;
        p["$closedEyes"].Value = (object?)photo.ClosedEyeCount ?? DBNull.Value;
        p["$minOpen"].Value = (object?)photo.MinEyeOpenScore ?? DBNull.Value;
        p["$subject"].Value = photo.SubjectTags.HasValue ? (object)(int)photo.SubjectTags.Value : DBNull.Value;

        // Persist the cached metadata + strip only when a staleness key was stamped
        // (photo touched its source this session); otherwise write NULLs so the next
        // load re-extracts rather than trusting an unkeyed row.
        bool hasKey = photo.MetaSourceMtimeTicks != 0;
        p["$metaSize"].Value = hasKey ? photo.MetaSourceSize : DBNull.Value;
        p["$metaMtime"].Value = hasKey ? photo.MetaSourceMtimeTicks : DBNull.Value;
        p["$metaJson"].Value = hasKey && photo.Metadata != null
            ? PhotoMetadataSerializer.Serialize(photo.Metadata)
            : (object)DBNull.Value;
        p["$grayStrip"].Value = hasKey && photo.GrayBuffer != null
            ? photo.GrayBuffer
            : (object)DBNull.Value;
    }

    public void Save(PhotoItem photo)
    {
        lock (_gate)
        {
            using var cmd = CreateUpsertCommand();
            BindPhoto(cmd, photo);
            cmd.ExecuteNonQuery();
        }
    }

    public void SaveBatch(IEnumerable<PhotoItem> photos)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = CreateUpsertCommand();
            cmd.Transaction = tx;
            cmd.Prepare();
            foreach (var photo in photos)
            {
                BindPhoto(cmd, photo);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Run a cluster of writes inside a single SQLite transaction. Without this, every
    /// AssignGroup/UnassignGroup auto-commits separately and fsyncs the journal — bulk
    /// tag toggles across 20+ photos would visibly stall the UI.
    /// </summary>
    public void WithTransaction(Action body)
    {
        lock (_gate)
        {
        using var tx = _db.BeginTransaction();
        body();
        tx.Commit();
        }
    }

    // ── Custom groups ──

    public List<PhotoTag> LoadGroups()
    {
        lock (_gate)
        {
        var result = new List<PhotoTag>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name, is_system, color FROM custom_groups ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PhotoTag
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                IsSystem = reader.GetInt32(2) != 0,
                Color = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }
        return result;
        }
    }

    public PhotoTag CreateGroup(string name, bool isSystem = false, string? color = null)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO custom_groups (name, is_system, color) VALUES ($name, $sys, $color) RETURNING id";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$sys", isSystem ? 1 : 0);
        cmd.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
        var id = Convert.ToInt32(cmd.ExecuteScalar());
        return new PhotoTag { Id = id, Name = name, IsSystem = isSystem, Color = color };
        }
    }

    /// <summary>
    /// Look up a system-owned tag by its canonical name. Returns null if it hasn't
    /// been created in this folder yet — caller is expected to call
    /// <see cref="CreateGroup(string,bool,string?)"/> when needed.
    /// </summary>
    public PhotoTag? FindSystemGroup(string name)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name, is_system, color FROM custom_groups WHERE is_system = 1 AND name = $name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PhotoTag
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            IsSystem = reader.GetInt32(2) != 0,
            Color = reader.IsDBNull(3) ? null : reader.GetString(3),
        };
        }
    }

    public void DeleteGroup(int id)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM custom_groups WHERE id = $id AND is_system = 0";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        }
    }

    public void RenameGroup(int id, string name)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE custom_groups SET name = $name WHERE id = $id AND is_system = 0";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.ExecuteNonQuery();
        }
    }

    public Dictionary<string, HashSet<int>> LoadAllPhotoGroups()
    {
        lock (_gate)
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
    }

    public void AssignGroup(string fileName, int groupId)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO photo_groups (file_name, group_id) VALUES ($name, $group)";
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.AddWithValue("$group", groupId);
        cmd.ExecuteNonQuery();
        }
    }

    public void UnassignGroup(string fileName, int groupId)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM photo_groups WHERE file_name = $name AND group_id = $group";
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.AddWithValue("$group", groupId);
        cmd.ExecuteNonQuery();
        }
    }

    public void ClearGroupsForPhoto(string fileName)
    {
        lock (_gate)
        {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM photo_groups WHERE file_name = $name";
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.ExecuteNonQuery();
        }
    }

    public void DeletePhoto(string fileName)
    {
        lock (_gate)
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
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
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
    public float? HighlightClippedPct { get; init; }
    public float? ShadowClippedPct { get; init; }
    public int? FaceCount { get; init; }
    public int? ClosedEyeCount { get; init; }
    public float? MinEyeOpenScore { get; init; }
    public SubjectTag? SubjectTags { get; init; }

    // Cached EXIF metadata + grayscale strip and the source-file staleness key they
    // were computed against. MetaSize/MetaMtime == 0 (or null blobs) means "not
    // cached"; the loader validates the key against the current file before trusting
    // MetaJson/GrayStrip.
    public long MetaSize { get; init; }
    public long MetaMtime { get; init; }
    public string? MetaJson { get; init; }
    public byte[]? GrayStrip { get; init; }
}
