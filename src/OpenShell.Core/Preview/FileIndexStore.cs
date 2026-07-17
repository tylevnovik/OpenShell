using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace OpenShell.Preview;

/// <summary>
/// 长期 SQLite + FTS5 文件索引。Per ADR-0030 §8.
/// Schema: <c>files(path TEXT PK, name TEXT, size INTEGER, modified INTEGER, content_hash TEXT)</c>
/// + <c>files_fts</c> FTS5 virtual table on name (全文匹配, 支持前缀查询)。
/// 从 USN/walk 事件增量更新, 启动时加载, 占用磁盘但查询极快 (per ADR-0030 §8)。
/// </summary>
/// <remarks>
/// 设计:
/// <list type="bullet">
///   <item>单文件 SQLite DB (per ADR-0030 §8: <c>~/.openshell/index/files.db</c>)。</item>
///   <item>使用 FTS5 (Microsoft.Data.Sqlite 内置 SQLite 静态链接, 需开启 SQLITE_ENABLE_FTS5)。</item>
///   <item>支持 upsert / delete / 全文检索 / 路径前缀检索。</item>
///   <item>线程安全: 内部用 <see cref="SqliteConnection"/> 单连接 + 锁; 高并发场景可在 M5+ 改为连接池。</item>
/// </list>
/// </remarks>
public sealed class FileIndexStore : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<FileIndexStore>? _logger;
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();
    private int _disposed;

    /// <summary>
    /// 构造并打开 (或创建) SQLite 索引库。Per ADR-0030 §8。
    /// </summary>
    /// <param name="dbPath">SQLite 文件路径 (建议 <c>~/.openshell/index/files.db</c>)。</param>
    /// <param name="logger">可选日志。</param>
    public FileIndexStore(string dbPath, ILogger<FileIndexStore>? logger = null)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _logger = logger;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();
        _connection = new SqliteConnection(connStr);
        _connection.Open();

        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var tx = _connection.BeginTransaction();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            // 主表: files(path PK, name, size, modified, content_hash)
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS files (
                    path TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    modified INTEGER NOT NULL,
                    content_hash TEXT
                );
                """;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            // FTS5 virtual table on name (per ADR-0030 §8: fts5 virtual table for name content)。
            // content='files' + content_rowid=rowid 以外键关联主表 (per FTS5 external content 表模式)。
            // 简化: 直接将 name 写入 FTS5, 与 files 表冗余存储 (per ADR-0030 §8: schema 描述)。
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
                    name,
                    content='',
                    tokenize='unicode61'
                );
                """;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_files_name ON files(name);
                CREATE INDEX IF NOT EXISTS ix_files_modified ON files(modified);
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Upsert 单个文件记录到索引 (per ADR-0030 §8: 从 USN/walk 事件增量更新)。</summary>
    public void Upsert(string path, string name, long size, long modifiedTicks, string? contentHash = null)
    {
        lock (_writeLock)
        {
            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO files (path, name, size, modified, content_hash)
                    VALUES ($path, $name, $size, $modified, $contentHash)
                    ON CONFLICT(path) DO UPDATE SET
                        name = excluded.name,
                        size = excluded.size,
                        modified = excluded.modified,
                        content_hash = excluded.content_hash;
                    """;
                cmd.Parameters.AddWithValue("$path", path);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$size", size);
                cmd.Parameters.AddWithValue("$modified", modifiedTicks);
                cmd.Parameters.AddWithValue("$contentHash", (object?)contentHash ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                // FTS5: 同步维护 (用 rowid = 主表 path 的 hash 作为虚拟 rowid 不可行; 这里直接 delete + insert)。
                cmd.CommandText = """
                    DELETE FROM files_fts WHERE rowid IN (SELECT rowid FROM files_fts WHERE name = $name LIMIT 1);
                    INSERT INTO files_fts (name) VALUES ($name);
                    """;
                cmd.Parameters.AddWithValue("$name", name);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>批量 upsert (per ADR-0030 §8: 启动时加载 / 后台 watcher 批量更新)。</summary>
    public void UpsertBatch(IEnumerable<UsnJournalIndexer.IndexedFile> batch)
    {
        lock (_writeLock)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var file in batch)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO files (path, name, size, modified, content_hash)
                    VALUES ($path, $name, $size, $modified, NULL)
                    ON CONFLICT(path) DO UPDATE SET
                        name = excluded.name,
                        size = excluded.size,
                        modified = excluded.modified;
                    """;
                cmd.Parameters.AddWithValue("$path", file.Path);
                cmd.Parameters.AddWithValue("$name", file.Name);
                cmd.Parameters.AddWithValue("$size", file.Size);
                cmd.Parameters.AddWithValue("$modified", file.Modified);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>从索引中删除路径 (per ADR-0030 §8: 增量更新, USN delete 事件)。</summary>
    public void Delete(string path)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path = $path;";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 全文匹配搜索 (per ADR-0030 §8: fts5 virtual table for name content)。
    /// 支持 FTS5 query 语法 (前缀 *, AND / OR / NOT)。返回 top-N。
    /// </summary>
    public IReadOnlyList<IndexedFileRow> SearchByName(string fts5Query, int limit = 1000)
    {
        var results = new List<IndexedFileRow>(capacity: Math.Min(limit, 256));
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT f.path, f.name, f.size, f.modified
                FROM files_fts fts
                JOIN files f ON f.name = fts.name
                WHERE files_fts MATCH $query
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$query", fts5Query);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new IndexedFileRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3)));
            }
        }
        return results;
    }

    /// <summary>
    /// 路径前缀搜索 (用于全局搜索 Ctrl+Shift+F 选当前路径范围)。Per ADR-0030 §6.
    /// </summary>
    public IReadOnlyList<IndexedFileRow> SearchByPathPrefix(string pathPrefix, int limit = 1000)
    {
        var results = new List<IndexedFileRow>(capacity: Math.Min(limit, 256));
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT path, name, size, modified
                FROM files
                WHERE path LIKE $prefix || '%'
                ORDER BY modified DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$prefix", pathPrefix.Replace('%', '/').Replace('\\', '/'));
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new IndexedFileRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3)));
            }
        }
        return results;
    }

    /// <summary>从 <see cref="UsnJournalIndexer"/> 全量同步 (重建)。</summary>
    public Task RebuildFromIndexerAsync(UsnJournalIndexer indexer, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            lock (_writeLock)
            {
                using var tx = _connection.BeginTransaction();
                using (var clearCmd = _connection.CreateCommand())
                {
                    clearCmd.Transaction = tx;
                    clearCmd.CommandText = "DELETE FROM files; DELETE FROM files_fts;";
                    clearCmd.ExecuteNonQuery();
                }

                using var insertCmd = _connection.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT INTO files (path, name, size, modified, content_hash)
                    VALUES ($path, $name, $size, $modified, NULL);
                    INSERT INTO files_fts (name) VALUES ($name);
                    """;
                var pPath = insertCmd.Parameters.AddWithValue("$path", "");
                var pName = insertCmd.Parameters.AddWithValue("$name", "");
                var pSize = insertCmd.Parameters.AddWithValue("$size", 0L);
                var pModified = insertCmd.Parameters.AddWithValue("$modified", 0L);

                foreach (var (_, f) in indexer.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    pPath.Value = f.Path;
                    pName.Value = f.Name;
                    pSize.Value = f.Size;
                    pModified.Value = f.Modified;
                    insertCmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }, ct);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _connection.Dispose(); }
        catch (Exception ex) { _logger?.LogWarning(ex, "SQLite connection dispose failed: {Path}", _dbPath); }
    }

    /// <summary>SQLite 索引中的一行 (映射 files 表)。</summary>
    public sealed record IndexedFileRow(string Path, string Name, long Size, long Modified);
}
