using System.Text.Json;
using Infrastructure;
using Microsoft.Data.Sqlite;

namespace Core.Groups;

/// <summary>组 CRUD + 内置组种子 + 规则合并（include 取 OR，exclude 取 OR，一票否决）。</summary>
public sealed class GroupRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly string _metaKey = "BuiltInsSeeded";

    public GroupRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
        EnsureTable();
    }

    private void EnsureTable()
    {
        DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Groups (
                    Id TEXT PRIMARY KEY,
                    Alias TEXT NOT NULL UNIQUE,
                    Include TEXT NOT NULL DEFAULT '',
                    Exclude TEXT NOT NULL DEFAULT '',
                    ExcludeFrom TEXT NOT NULL DEFAULT '',
                    ExcludeDir TEXT NOT NULL DEFAULT '',
                    Remark TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    IsBuiltIn INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS AppMeta (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        });
    }

    // ---------- 种子 ----------

    /// <summary>首次启动种子：仅当尚未播种时补插缺失的内置组（幂等，重复启动不重复插入）。</summary>
    public void SeedBuiltInsOnStartup()
    {
        if (GetMeta(_metaKey) == "1") return;
        InsertMissingBuiltIns();
        SetMeta(_metaKey, "1");
    }

    /// <summary>"恢复内置组"按钮：仅补插缺失项，已存在跳过（幂等）。</summary>
    public void RestoreBuiltIns() => InsertMissingBuiltIns();

    private void InsertMissingBuiltIns()
    {
        var existing = GetAll().Select(g => g.Alias).ToHashSet();
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        foreach (var seed in BuiltInGroups.All)
        {
            if (existing.Contains(seed.Alias)) continue;
            var g = Clone(seed);
            InsertCore(conn, g, tx);
        }
        tx.Commit();
    }

    private static GroupModel Clone(GroupModel g) => new()
    {
        Id = g.Id,
        Alias = g.Alias,
        Include = [.. g.Include],
        Exclude = [.. g.Exclude],
        ExcludeFrom = [.. g.ExcludeFrom],
        ExcludeDir = [.. g.ExcludeDir],
        Remark = g.Remark,
        CreatedAt = g.CreatedAt,
        UpdatedAt = g.UpdatedAt,
        IsBuiltIn = g.IsBuiltIn,
    };

    // ---------- CRUD ----------

    public List<GroupModel> GetAll()
    {
        return DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Alias, Include, Exclude, ExcludeFrom, ExcludeDir, Remark, CreatedAt, UpdatedAt, IsBuiltIn FROM Groups ORDER BY IsBuiltIn DESC, Alias";
            using var r = cmd.ExecuteReader();
            var list = new List<GroupModel>();
            while (r.Read())
            {
                list.Add(new GroupModel
                {
                    Id = Guid.Parse(r.GetString(0)),
                    Alias = r.GetString(1),
                    Include = Split(r.GetString(2)),
                    Exclude = Split(r.GetString(3)),
                    ExcludeFrom = Split(r.GetString(4)),
                    ExcludeDir = Split(r.GetString(5)),
                    Remark = r.GetString(6),
                    CreatedAt = DateTime.Parse(r.GetString(7)),
                    UpdatedAt = DateTime.Parse(r.GetString(8)),
                    IsBuiltIn = r.GetInt64(9) == 1,
                });
            }
            return list;
        });
    }

    public void Upsert(GroupModel g)
    {
        g.UpdatedAt = DateTime.Now;
        DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            InsertCore(conn, g, null);
        });
    }

    private static void InsertCore(SqliteConnection conn, GroupModel g, SqliteTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Groups (Id, Alias, Include, Exclude, ExcludeFrom, ExcludeDir, Remark, CreatedAt, UpdatedAt, IsBuiltIn)
            VALUES ($id, $alias, $inc, $exc, $exf, $exd, $rem, $ca, $ua, $bi)
            ON CONFLICT(Id) DO UPDATE SET
                Alias=$alias, Include=$inc, Exclude=$exc, ExcludeFrom=$exf, ExcludeDir=$exd,
                Remark=$rem, UpdatedAt=$ua, IsBuiltIn=$bi
            """;
        cmd.Parameters.AddWithValue("$id", g.Id.ToString());
        cmd.Parameters.AddWithValue("$alias", g.Alias);
        cmd.Parameters.AddWithValue("$inc", Join(g.Include));
        cmd.Parameters.AddWithValue("$exc", Join(g.Exclude));
        cmd.Parameters.AddWithValue("$exf", Join(g.ExcludeFrom));
        cmd.Parameters.AddWithValue("$exd", Join(g.ExcludeDir));
        cmd.Parameters.AddWithValue("$rem", g.Remark);
        cmd.Parameters.AddWithValue("$ca", g.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$ua", g.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$bi", g.IsBuiltIn ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void Delete(Guid id)
    {
        DbRetry.Execute(() =>
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Groups WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
        });
    }

    // ---------- 规则合并 ----------

    /// <summary>勾选组规则合并：include 取 OR，exclude 取 OR，exclude 一票否决；不做组间 AND。</summary>
    public static (List<string> Include, List<string> Exclude, List<string> ExcludeFrom, List<string> ExcludeDir)
        Merge(IEnumerable<GroupModel> groups)
    {
        var inc = new List<string>();
        var exc = new List<string>();
        var exf = new List<string>();
        var exd = new List<string>();
        foreach (var g in groups)
        {
            inc.AddRange(g.Include.Where(x => !inc.Contains(x)));
            exc.AddRange(g.Exclude.Where(x => !exc.Contains(x)));
            exf.AddRange(g.ExcludeFrom.Where(x => !exf.Contains(x)));
            exd.AddRange(g.ExcludeDir.Where(x => !exd.Contains(x)));
        }
        return (inc, exc, exf, exd);
    }

    // ---------- JSON 导入导出 ----------

    public static string ExportJson(IEnumerable<GroupModel> groups)
        => JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static List<GroupModel> ImportJson(string json)
        => JsonSerializer.Deserialize<List<GroupModel>>(json) ?? [];

    // ---------- helpers ----------

    private string? GetMeta(string key)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppMeta WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO AppMeta (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static string Join(IEnumerable<string> list) => string.Join('\n', list);

    private static List<string> Split(string s)
        => string.IsNullOrEmpty(s) ? [] : [.. s.Split('\n')];
}
