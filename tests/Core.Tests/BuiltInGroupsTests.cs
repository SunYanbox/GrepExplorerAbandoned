using Core.Groups;
using Infrastructure;

namespace Core.Tests;

/// <summary>内置组种子幂等 + 恢复内置组测试。</summary>
public class BuiltInGroupsTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteConnectionFactory _factory;

    public BuiltInGroupsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "grouptests_" + Guid.NewGuid().ToString("N"));
        _factory = new SqliteConnectionFactory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SeedOnStartup_IsIdempotent()
    {
        var repo1 = new GroupRepository(_factory);
        repo1.SeedBuiltInsOnStartup();
        Assert.Equal(3, repo1.GetAll().Count(g => g.IsBuiltIn));

        // 模拟重复启动（新仓库实例 + 再次种子）
        var repo2 = new GroupRepository(_factory);
        repo2.SeedBuiltInsOnStartup();
        Assert.Equal(3, repo2.GetAll().Count(g => g.IsBuiltIn));
    }

    [Fact]
    public void RestoreBuiltIns_ReinsertsOnlyMissing()
    {
        var repo = new GroupRepository(_factory);
        repo.SeedBuiltInsOnStartup();

        // 删除一个内置组
        var vcs = repo.GetAll().First(g => g.Alias == "排除版本控制目录");
        repo.Delete(vcs.Id);
        Assert.Equal(2, repo.GetAll().Count);

        // 恢复内置组：仅补插缺失项
        repo.RestoreBuiltIns();
        Assert.Equal(3, repo.GetAll().Count);
        Assert.Contains(repo.GetAll(), g => g.Alias == "排除版本控制目录" && g.IsBuiltIn);
    }

    [Fact]
    public void RestoreBuiltIns_SkipsExisting()
    {
        var repo = new GroupRepository(_factory);
        repo.SeedBuiltInsOnStartup();
        var before = repo.GetAll().ToDictionary(g => g.Id);

        repo.RestoreBuiltIns(); // 全部存在 → 不新增
        Assert.Equal(3, repo.GetAll().Count);
        foreach (var g in repo.GetAll())
            Assert.Contains(g.Id, before.Keys);
    }

    [Fact]
    public void DeletedBuiltIns_NotReSeeded_OnRestart()
    {
        // 用户删除内置组后，普通重启（种子标记已存在）不应重新插入
        var repo1 = new GroupRepository(_factory);
        repo1.SeedBuiltInsOnStartup();
        var g = repo1.GetAll().First(x => x.Alias == "排除常见构造产物");
        repo1.Delete(g.Id);

        var repo2 = new GroupRepository(_factory); // "重启"
        repo2.SeedBuiltInsOnStartup();
        Assert.Equal(2, repo2.GetAll().Count);

        // 仅"恢复内置组"按钮才补插
        repo2.RestoreBuiltIns();
        Assert.Equal(3, repo2.GetAll().Count);
    }

    [Fact]
    public void Merge_CombinesRulesWithOr()
    {
        // 组合并：include 取 OR，exclude 取 OR，exclude 一票否决
        var merged = GroupRepository.Merge(
        [
            new GroupModel { Alias = "a", Include = ["*.cs"], Exclude = ["*Gen.cs"] },
            new GroupModel { Alias = "b", Include = ["*.md"], Exclude = ["readme.md"] },
        ]);

        Assert.Equal(2, merged.Include.Count);
        Assert.Equal(2, merged.Exclude.Count);
    }

    [Fact]
    public void Alias_Validation()
    {
        // 别名唯一、非空、≤32 字符（约束在 UI 层 + 数据库 UNIQUE；此处验证种子数据合法）
        Assert.All(BuiltInGroups.All, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Alias));
            Assert.True(g.Alias.Length <= 32);
            Assert.True(g.IsBuiltIn);
        });
    }
}
