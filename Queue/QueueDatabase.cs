using Microsoft.Data.Sqlite;

namespace AceleCoreAgent.Queue;

public class QueueDatabase
{
    private readonly string _dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AceleCoreAgent", "queue.db");

    public QueueDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        InitDb();
    }

    private SqliteConnection GetConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void InitDb()
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS BatchQueue (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderPath  TEXT NOT NULL,
                BatchLabel  TEXT NOT NULL,
                FileCount   INTEGER NOT NULL DEFAULT 0,
                DetectedAt  TEXT NOT NULL,
                SentAt      TEXT,
                Status      TEXT NOT NULL DEFAULT 'Pending',
                ErrorMessage TEXT,
                RetryCount  INTEGER NOT NULL DEFAULT 0,
                Notes       TEXT
            );

            CREATE TABLE IF NOT EXISTS ProcessedFiles (
                FilePath    TEXT PRIMARY KEY,
                ProcessedAt TEXT NOT NULL,
                BatchId     INTEGER
            );
        """;
        cmd.ExecuteNonQuery();
    }

    public int EnqueueBatch(BatchQueueItem item)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BatchQueue (FolderPath, BatchLabel, FileCount, DetectedAt, Status, Notes)
            VALUES ($folder, $label, $count, $detected, 'Pending', $notes);
            SELECT last_insert_rowid();
        """;
        cmd.Parameters.AddWithValue("$folder", item.FolderPath);
        cmd.Parameters.AddWithValue("$label", item.BatchLabel);
        cmd.Parameters.AddWithValue("$count", item.FileCount);
        cmd.Parameters.AddWithValue("$detected", item.DetectedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$notes", item.Notes ?? "");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<BatchQueueItem> GetPending()
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM BatchQueue
            WHERE Status IN ('Pending', 'Failed') AND RetryCount < 5
            ORDER BY DetectedAt ASC
        """;
        return ReadItems(cmd);
    }

    public List<BatchQueueItem> GetAll()
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM BatchQueue ORDER BY DetectedAt DESC LIMIT 100";
        return ReadItems(cmd);
    }

    public void UpdateStatus(int id, BatchStatus status, string? error = null)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        UPDATE BatchQueue
        SET Status = $status,
            ErrorMessage = $error,
            SentAt = $sent,
            RetryCount = RetryCount + CASE WHEN $status = 'Failed' THEN 1 ELSE 0 END
        WHERE Id = $id
    """;
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sent", status == BatchStatus.Sent
            ? (object)DateTime.Now.ToString("O")
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool IsFileProcessed(string filePath)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ProcessedFiles WHERE FilePath = $path";
        cmd.Parameters.AddWithValue("$path", filePath);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public void MarkFilesProcessed(IEnumerable<string> filePaths, int batchId)
    {
        using var conn = GetConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO ProcessedFiles (FilePath, ProcessedAt, BatchId)
            VALUES ($path, $at, $batchId)
        """;

        foreach (var path in filePaths)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$batchId", batchId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public bool IsFolderAlreadyQueued(string folderPath)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        SELECT COUNT(*) FROM BatchQueue
        WHERE FolderPath = $folder AND Status IN ('Pending', 'Sending')
    """;
        cmd.Parameters.AddWithValue("$folder", folderPath);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static List<BatchQueueItem> ReadItems(SqliteCommand cmd)
    {
        var items = new List<BatchQueueItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new BatchQueueItem
            {
                Id = reader.GetInt32(0),
                FolderPath = reader.GetString(1),
                BatchLabel = reader.GetString(2),
                FileCount = reader.GetInt32(3),
                DetectedAt = DateTime.Parse(reader.GetString(4)),
                SentAt = !reader.IsDBNull(5) && !string.IsNullOrEmpty(reader.GetString(5))
                    ? DateTime.Parse(reader.GetString(5))
                    : null,
                Status = Enum.Parse<BatchStatus>(reader.GetString(6)),
                ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
                RetryCount = reader.GetInt32(8),
                Notes = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return items;
    }
}