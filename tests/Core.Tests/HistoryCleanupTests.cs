using Core.History;
using Infrastructure;

namespace Core.Tests;

/// <summary>历史清理测试（≥8 条：六种清理模式 + 重复项保留最新 + 事务中断回滚）。</summary>
public class HistoryCleanupTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteConnectionFactory _factory;
    private readonly HistoryRepository _repo;

    public HistoryCleanupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "histtests_" + Guid.NewGuid().ToString("N"));
        _factory = new SqliteConnectionFactory(_dir);
        _repo = new HistoryRepository(_factory, historyLimit: -1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private HistoryRecord Make(string mode = "Grep", string root = @"C:\proj1", string cmd = "cmd-a", int hits = 3)
        => new()
        {
            Mode = mode,
            RootPath = root,
            GroupAliases = "g1",
            Include = "*.cs",
            Exclude = "",
            ExcludeFrom = "",
            ExcludeDir = "bin",
            Pattern = "abc",
            CommandText = cmd,
            HitFiles = hits,
            TotalHits = hits,
        };

    private HistoryRecord Insert(string root = @"C:\proj1", string cmd = "cmd-a", string mode = "Grep")
        => _repo.Insert(Make(root: root, cmd: cmd, mode: mode), "# 搜索结果\n快照内容");

    [Fact]
    public void Insert_ComputesSnapshotBytesInline()
    {
        var rec = Insert();
        var content = _repo.ReadSnapshot(_repo.GetById(rec.Id)!)!;
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(content), _repo.GetById(rec.Id)!.SnapshotBytes);
    }

    [Fact]
    public void Cleanup_All_RemovesEverything()
    {
        Insert(); Insert(root: @"C:\proj2");
        Assert.Equal(2, _repo.Query().Count);
        Assert.Equal(2, _repo.Cleanup(CleanupMode.All));
        Assert.Empty(_repo.Query());
    }

    [Fact]
    public void Cleanup_CurrentProjectAll_RemovesOnlyCurrentRoot()
    {
        Insert(root: @"C:\proj1");
        Insert(root: @"C:\proj1");
        Insert(root: @"C:\proj2");
        Assert.Equal(2, _repo.Cleanup(CleanupMode.CurrentProjectAll, currentRoot: @"C:\proj1"));
        Assert.Equal(1, _repo.Query().Count);
        Assert.Equal(@"C:\proj2", _repo.Query()[0].RootPath);
    }

    [Fact]
    public void Cleanup_GlobalOlderThanDays()
    {
        // ≥0 天 → 全部命中（CreatedAt <= now）
        Insert(); Insert();
        Assert.Equal(2, _repo.EstimateCleanup(CleanupMode.GlobalOlderThanDays, days: 0));
        Assert.Equal(2, _repo.Cleanup(CleanupMode.GlobalOlderThanDays, days: 0));
        Assert.Empty(_repo.Query());
    }

    [Fact]
    public void Cleanup_ProjectOlderThanDays()
    {
        Insert(root: @"C:\proj1");
        Insert(root: @"C:\proj2");
        Assert.Equal(1, _repo.Cleanup(CleanupMode.ProjectOlderThanDays, currentRoot: @"C:\proj2", days: 0));
        Assert.Single(_repo.Query());
        Assert.Equal(@"C:\proj1", _repo.Query()[0].RootPath);
    }

    [Fact]
    public void Cleanup_SelectedIds()
    {
        var r1 = Insert();
        var r2 = Insert();
        var r3 = Insert();
        Assert.Equal(2, _repo.Cleanup(CleanupMode.SelectedIds, ids: [r1.Id, r2.Id]));
        Assert.Equal(r3.Id, _repo.Query().Single().Id);
    }

    [Fact]
    public void Cleanup_Duplicates_KeepsNewestOnly()
    {
        // 同键 = 工作目录 + 规范化命令文本
        var oldest = Insert(root: @"C:\p", cmd: "cmd-x");
        var newest = Insert(root: @"C:\p", cmd: "cmd-x");
        Insert(root: @"C:\p", cmd: "cmd-y");   // 不同键
        Insert(root: @"C:\q", cmd: "cmd-x");   // 不同工作目录

        Assert.Equal(1, _repo.EstimateDuplicateCleanup());
        Assert.Equal(1, _repo.Cleanup(CleanupMode.Duplicates));
        var remaining = _repo.Query().Select(r => r.Id).ToHashSet();
        Assert.Contains(newest.Id, remaining);
        Assert.DoesNotContain(oldest.Id, remaining);
        Assert.Equal(3, remaining.Count);
    }

    [Fact]
    public void Cleanup_DeletesExternalSnapshotFiles()
    {
        var rec = Insert();
        var path = _repo.GetById(rec.Id)!.SnapshotPath;
        Assert.True(File.Exists(path));
        _repo.Cleanup(CleanupMode.All);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Cleanup_TransactionRollback_OnInterruption()
    {
        var rec = Insert();
        var snapshotPath = _repo.GetById(rec.Id)!.SnapshotPath;

        Assert.Throws<InvalidOperationException>(() =>
            _repo.Cleanup(CleanupMode.All, throwForTest: () => throw new InvalidOperationException("中断")));

        // 事务回滚：记录仍在，快照文件未被删除
        Assert.NotNull(_repo.GetById(rec.Id));
        Assert.True(File.Exists(snapshotPath));
    }

    [Fact]
    public void Limit_RollsOldestOff()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "limit"));
        var repo = new HistoryRepository(factory, historyLimit: 3);
        for (int i = 0; i < 5; i++)
            repo.Insert(new HistoryRecord { Mode = "Grep", RootPath = "r", CommandText = "c" + i, Pattern = "p" }, "snap" + i);

        var all = repo.Query(); // 时间倒序
        Assert.Equal(3, all.Count);
        // 最旧的两条（c0、c1）被滚动删除
        Assert.DoesNotContain(all, r => r.CommandText == "c0");
        Assert.DoesNotContain(all, r => r.CommandText == "c1");
    }

    [Fact]
    public void Query_FiltersByScopeModeRootAndGroup()
    {
        Insert(root: @"C:\proj1", mode: "Grep");
        Insert(root: @"C:\proj1", mode: "Glob");
        Insert(root: @"C:\proj2", mode: "Grep");

        Assert.Equal(2, _repo.Query(HistoryScope.CurrentProject, currentRoot: @"C:\proj1").Count);
        Assert.Equal(1, _repo.Query(mode: "Glob").Count);
        Assert.Equal(1, _repo.Query(rootFilter: @"C:\proj2").Count);
        Assert.Equal(3, _repo.Query(HistoryScope.Global).Count);
    }

    [Fact]
    public void Stats_UsesSumOfSnapshotBytes()
    {
        var r1 = Insert();
        var r2 = Insert();
        long expected = _repo.GetById(r1.Id)!.SnapshotBytes + _repo.GetById(r2.Id)!.SnapshotBytes;
        var stats = _repo.GetStats();
        Assert.Equal(2, stats.Count);
        Assert.Equal(expected, stats.TotalBytes);
    }
}
