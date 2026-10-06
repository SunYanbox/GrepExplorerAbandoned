using System.Text;
using Core.Grep;
using Infrastructure;
using Microsoft.Data.Sqlite;

namespace Core.History;

/// <summary>
/// 历史持久化与清理：
/// - WAL + busy_timeout + SQLITE_BUSY 重试（连接工厂保证）；
/// - SnapshotBytes 行内列（插入时计算），统计用 SUM，禁止重算；
/// - 清理在事务内执行；外置快照文件随记录同步删除；
/// - 达到上限滚动删除最旧。
/// </summary>
public sealed class HistoryRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly string _snapshotDirectory;
    private readonly int _limit; // -1 = 无上限

    public HistoryRepository(SqliteConnectionFactory factory, int historyLimit, string? snapshotDirectory = null)
    {
        _factory = factory;
        _limit = historyLimit;
        _snapshotDirectory = snapshotDirectory ?? Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "snapshots");
        Directory.CreateDirectory(_snapshotDirectory);
        EnsureTable();
    }

    private void EnsureTable()
    {
        DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS History (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedAt TEXT NOT NULL,
                    Mode TEXT NOT NULL,
                    RootPath TEXT NOT NULL,
                    GroupAliases TEXT NOT NULL DEFAULT '',
                    Include TEXT NOT NULL DEFAULT '',
                    Exclude TEXT NOT NULL DEFAULT '',
                    ExcludeFrom TEXT NOT NULL DEFAULT '',
                    ExcludeDir TEXT NOT NULL DEFAULT '',
                    Pattern TEXT NOT NULL DEFAULT '',
                    CommandText TEXT NOT NULL DEFAULT '',
                    HitFiles INTEGER NOT NULL DEFAULT 0,
                    TotalHits INTEGER NOT NULL DEFAULT 0,
                    SnapshotBytes INTEGER NOT NULL DEFAULT 0,
                    SnapshotPath TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS IX_History_CreatedAt ON History(CreatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_History_RootPath ON History(RootPath);
                """;
            cmd.ExecuteNonQuery();
        });
    }

    // ---------- 插入 ----------

    public HistoryRecord Insert(HistoryRecord record, string snapshotContent)
    {
        record.CreatedAt = DateTime.Now;
        var bytes = Encoding.UTF8.GetByteCount(snapshotContent);
        record.SnapshotBytes = bytes;
        record.SnapshotPath = Path.Combine(_snapshotDirectory, $"{Guid.NewGuid():N}.md");
        File.WriteAllText(record.SnapshotPath, snapshotContent);

        DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO History (CreatedAt, Mode, RootPath, GroupAliases, Include, Exclude, ExcludeFrom, ExcludeDir,
                                     Pattern, CommandText, HitFiles, TotalHits, SnapshotBytes, SnapshotPath)
                VALUES ($ca, $mode, $root, $groups, $inc, $exc, $exf, $exd, $pat, $cmd, $hf, $th, $sb, $sp);
                SELECT last_insert_rowid();
                """;
            Bind(cmd, record);
            record.Id = (long)(cmd.ExecuteScalar() ?? 0);
            tx.Commit();
        });

        EnforceLimit();
        return record;
    }

    /// <summary>达到上限时滚动删除最旧（含外置快照文件）。</summary>
    private void EnforceLimit()
    {
        if (_limit < 0) return;
        var ids = DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id FROM History ORDER BY CreatedAt DESC, Id DESC LIMIT -1 OFFSET $offset
                """;
            cmd.Parameters.AddWithValue("$offset", _limit);
            using var r = cmd.ExecuteReader();
            var ids = new List<long>();
            while (r.Read()) ids.Add(r.GetInt64(0));
            return ids;
        });
        if (ids.Count > 0)
            DeleteCore(ids);
    }

    // ---------- 查询 ----------

    public List<HistoryRecord> Query(
        HistoryScope scope = HistoryScope.Global,
        string? currentRoot = null,
        string? mode = null,
        string? rootFilter = null,
        string? groupAlias = null)
    {
        var where = new StringBuilder("1=1");
        if (scope == HistoryScope.CurrentProject)
        {
            where.Append(" AND RootPath = $cur");
        }
        if (!string.IsNullOrEmpty(mode)) where.Append(" AND Mode = $mode");
        if (!string.IsNullOrEmpty(rootFilter)) where.Append(" AND RootPath = $rootf");
        if (!string.IsNullOrEmpty(groupAlias)) where.Append(" AND GroupAliases LIKE $grp");

        return DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT Id, CreatedAt, Mode, RootPath, GroupAliases, Include, Exclude, ExcludeFrom, ExcludeDir,
                       Pattern, CommandText, HitFiles, TotalHits, SnapshotBytes, SnapshotPath
                FROM History WHERE {where}
                ORDER BY CreatedAt DESC, Id DESC
                """;
            if (scope == HistoryScope.CurrentProject)
                cmd.Parameters.AddWithValue("$cur", currentRoot ?? "");
            if (!string.IsNullOrEmpty(mode)) cmd.Parameters.AddWithValue("$mode", mode);
            if (!string.IsNullOrEmpty(rootFilter)) cmd.Parameters.AddWithValue("$rootf", rootFilter);
            if (!string.IsNullOrEmpty(groupAlias)) cmd.Parameters.AddWithValue("$grp", "%" + groupAlias + "%");
            using var r = cmd.ExecuteReader();
            var list = new List<HistoryRecord>();
            while (r.Read()) list.Add(Read(r));
            return list;
        });
    }

    public HistoryRecord? GetById(long id)
    {
        return DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, CreatedAt, Mode, RootPath, GroupAliases, Include, Exclude, ExcludeFrom, ExcludeDir,
                       Pattern, CommandText, HitFiles, TotalHits, SnapshotBytes, SnapshotPath
                FROM History WHERE Id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        });
    }

    public string? ReadSnapshot(HistoryRecord record)
        => File.Exists(record.SnapshotPath) ? File.ReadAllText(record.SnapshotPath) : null;

    public HistoryStats GetStats(HistoryScope scope = HistoryScope.Global, string? currentRoot = null)
    {
        return DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = scope == HistoryScope.CurrentProject
                ? "SELECT COUNT(*), COALESCE(SUM(SnapshotBytes),0) FROM History WHERE RootPath = $cur"
                : "SELECT COUNT(*), COALESCE(SUM(SnapshotBytes),0) FROM History";
            if (scope == HistoryScope.CurrentProject)
                cmd.Parameters.AddWithValue("$cur", currentRoot ?? "");
            using var r = cmd.ExecuteReader();
            r.Read();
            return new HistoryStats((int)r.GetInt64(0), r.GetInt64(1));
        });
    }

    public List<string> GetDistinctRoots()
    {
        return DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT RootPath FROM History ORDER BY RootPath";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        });
    }

    // ---------- 清理（六种入口，事务内执行） ----------

    /// <summary>预估将删除的条数（用于二次确认）。</summary>
    public int EstimateCleanup(CleanupMode mode, string? currentRoot = null, int? days = null, IReadOnlyList<long>? ids = null)
        => DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            if (mode == CleanupMode.Duplicates)
                return CountDuplicates(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = mode switch
            {
                CleanupMode.All => "SELECT COUNT(*) FROM History",
                CleanupMode.CurrentProjectAll => "SELECT COUNT(*) FROM History WHERE RootPath = $cur",
                CleanupMode.GlobalOlderThanDays => "SELECT COUNT(*) FROM History WHERE CreatedAt <= $cut",
                CleanupMode.ProjectOlderThanDays => "SELECT COUNT(*) FROM History WHERE RootPath = $cur AND CreatedAt <= $cut",
                CleanupMode.SelectedIds => $"SELECT COUNT(*) FROM History WHERE Id IN ({InList(ids)})",
                _ => "SELECT COUNT(*) FROM History",
            };
            if (mode is CleanupMode.CurrentProjectAll or CleanupMode.ProjectOlderThanDays)
                cmd.AddParameter("$cur", currentRoot ?? "");
            if (mode is CleanupMode.GlobalOlderThanDays or CleanupMode.ProjectOlderThanDays)
                cmd.AddParameter("$cut", DateTime.Now.AddDays(-days!.Value).ToString("O"));
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

    /// <summary>预览重复项将删除的条数。</summary>
    public int EstimateDuplicateCleanup() => CountDuplicates();

    private static string InList(IReadOnlyList<long>? ids)
    {
        if (ids == null || ids.Count == 0) return "-1";
        return string.Join(",", ids);
    }

    /// <summary>执行清理（事务内）；返回删除条数。外部快照文件随记录同步删除。</summary>
    public int Cleanup(CleanupMode mode, string? currentRoot = null, int? days = null, IReadOnlyList<long>? ids = null, Action? throwForTest = null)
    {
        var toDelete = ListCleanupTargets(mode, currentRoot, days, ids);
        int deleted = DeleteCore(toDelete, throwForTest);
        return deleted;
    }

    private List<long> ListCleanupTargets(CleanupMode mode, string? currentRoot, int? days, IReadOnlyList<long>? ids)
    {
        return DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            switch (mode)
            {
                case CleanupMode.All:
                    return AllIds(conn, null);
                case CleanupMode.CurrentProjectAll:
                    return AllIds(conn, currentRoot);
                case CleanupMode.GlobalOlderThanDays:
                    return OlderThan(conn, null, days!.Value);
                case CleanupMode.ProjectOlderThanDays:
                    return OlderThan(conn, currentRoot, days!.Value);
                case CleanupMode.SelectedIds:
                    return ids?.ToList() ?? [];
                case CleanupMode.Duplicates:
                    return DuplicateIds(conn);
                default:
                    return [];
            }
        });
    }

    private static List<long> ReadIds(System.Data.Common.DbCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private static List<long> AllIds(System.Data.Common.DbConnection conn, string? root)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = root == null ? "SELECT Id FROM History" : "SELECT Id FROM History WHERE RootPath = $cur";
        if (root != null) cmd.AddParameter("$cur", root);
        return ReadIds(cmd);
    }

    private static List<long> OlderThan(System.Data.Common.DbConnection conn, string? root, int days)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = root == null
            ? "SELECT Id FROM History WHERE CreatedAt <= $cut"
            : "SELECT Id FROM History WHERE RootPath = $cur AND CreatedAt <= $cut";
        cmd.AddParameter("$cut", DateTime.Now.AddDays(-days).ToString("O"));
        if (root != null) cmd.AddParameter("$cur", root);
        return ReadIds(cmd);
    }

    /// <summary>重复项判定：键 = 工作目录(RootPath) + 规范化命令文本；只留最新。</summary>
    private static List<long> DuplicateIds(System.Data.Common.DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id FROM History
            WHERE Id NOT IN (
                SELECT Id FROM (
                    SELECT Id, ROW_NUMBER() OVER (
                        PARTITION BY RootPath, CommandText
                        ORDER BY CreatedAt DESC, Id DESC) AS rn
                    FROM History
                ) WHERE rn = 1
            )
            """;
        return ReadIds(cmd);
    }

    private int CountDuplicates(System.Data.Common.DbConnection? conn = null)
    {
        var owned = conn == null;
        conn ??= _factory.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM (
                    SELECT Id, ROW_NUMBER() OVER (
                        PARTITION BY RootPath, CommandText
                        ORDER BY CreatedAt DESC, Id DESC) AS rn
                    FROM History
                ) WHERE rn > 1
                """;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        finally
        {
            if (owned) conn.Dispose();
        }
    }

    /// <summary>核心删除：单事务内删除记录；事务提交后删除外置快照文件。</summary>
    private int DeleteCore(IReadOnlyList<long> ids, Action? throwForTest = null)
    {
        if (ids.Count == 0) return 0;
        var snapshotPaths = new List<string>();
        int deleted = DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            int count = 0;
            foreach (var id in ids)
            {
                using var sel = conn.CreateCommand();
                sel.Transaction = tx;
                sel.CommandText = "SELECT SnapshotPath FROM History WHERE Id = $id";
                sel.AddParameter("$id", id);
                var sp = sel.ExecuteScalar() as string;
                if (sp != null) snapshotPaths.Add(sp);

                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM History WHERE Id = $id";
                del.AddParameter("$id", id);
                count += del.ExecuteNonQuery();
            }
            throwForTest?.Invoke(); // 事务中断回滚测试钩子
            tx.Commit();
            return count;
        });

        foreach (var sp in snapshotPaths)
        {
            try { if (File.Exists(sp)) File.Delete(sp); } catch { /* 忽略快照文件删除失败 */ }
        }
        return deleted;
    }

    // ---------- helpers ----------

    private static HistoryRecord Read(System.Data.Common.DbDataReader r) => new()
    {
        Id = r.GetInt64(0),
        CreatedAt = DateTime.Parse(r.GetString(1)),
        Mode = r.GetString(2),
        RootPath = r.GetString(3),
        GroupAliases = r.GetString(4),
        Include = r.GetString(5),
        Exclude = r.GetString(6),
        ExcludeFrom = r.GetString(7),
        ExcludeDir = r.GetString(8),
        Pattern = r.GetString(9),
        CommandText = r.GetString(10),
        HitFiles = (int)r.GetInt64(11),
        TotalHits = (int)r.GetInt64(12),
        SnapshotBytes = r.GetInt64(13),
        SnapshotPath = r.GetString(14),
    };

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd, HistoryRecord rec)
    {
        cmd.AddParameter("$ca", rec.CreatedAt.ToString("O"));
        cmd.AddParameter("$mode", rec.Mode);
        cmd.AddParameter("$root", rec.RootPath);
        cmd.AddParameter("$groups", rec.GroupAliases);
        cmd.AddParameter("$inc", rec.Include);
        cmd.AddParameter("$exc", rec.Exclude);
        cmd.AddParameter("$exf", rec.ExcludeFrom);
        cmd.AddParameter("$exd", rec.ExcludeDir);
        cmd.AddParameter("$pat", rec.Pattern);
        cmd.AddParameter("$cmd", rec.CommandText);
        cmd.AddParameter("$hf", rec.HitFiles);
        cmd.AddParameter("$th", rec.TotalHits);
        cmd.AddParameter("$sb", rec.SnapshotBytes);
        cmd.AddParameter("$sp", rec.SnapshotPath);
    }
}
