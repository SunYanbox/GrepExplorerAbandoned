namespace Core.Groups;

public sealed class GroupModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Alias { get; set; } = "";
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
    public List<string> ExcludeFrom { get; set; } = new();
    public List<string> ExcludeDir { get; set; } = new();
    public string Remark { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public bool IsBuiltIn { get; set; }
}

/// <summary>2.12 内置组种子数据。</summary>
public static class BuiltInGroups
{
    public static IReadOnlyList<GroupModel> All { get; } =
    [
        new GroupModel
        {
            Alias = "排除常见构造产物",
            IsBuiltIn = true,
            Remark = "内置组：排除编译/构建输出目录",
            ExcludeDir = Parse("bin obj artifacts publish target build out cmake-build-debug cmake-build-release Debug Release x64 x86 __pycache__ .pytest_cache .mypy_cache .ruff_cache dist *.egg-info .next .nuxt .turbo coverage"),
        },
        new GroupModel
        {
            Alias = "排除常见依赖路径",
            IsBuiltIn = true,
            Remark = "内置组：排除第三方依赖目录",
            ExcludeDir = Parse("node_modules .venv venv env vendor packages Pods .tox .gradle _vendor"),
        },
        new GroupModel
        {
            Alias = "排除版本控制目录",
            IsBuiltIn = true,
            Remark = "内置组：排除 VCS 元数据目录",
            ExcludeDir = Parse(".git .svn .hg .jj"),
        },
    ];

    private static List<string> Parse(string line)
        => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
