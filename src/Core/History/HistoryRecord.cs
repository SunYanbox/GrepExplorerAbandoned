namespace Core.History;

/// <summary>历史记录（规则与元数据只读，永不回写数据库）。</summary>
public sealed class HistoryRecord
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Mode { get; set; } = "";           // Glob | Grep
    public string RootPath { get; set; } = "";       // 归属判断 = 此字段
    public string GroupAliases { get; set; } = "";
    public string Include { get; set; } = "";        // 四类规则快照
    public string Exclude { get; set; } = "";
    public string ExcludeFrom { get; set; } = "";
    public string ExcludeDir { get; set; } = "";
    public string Pattern { get; set; } = "";        // 正则 / Glob 表达式
    public string CommandText { get; set; } = "";    // 规范化命令文本（去重键组成部分）
    public int HitFiles { get; set; }
    public int TotalHits { get; set; }
    public long SnapshotBytes { get; set; }          // 插入时计算的行内快照字节数
    public string SnapshotPath { get; set; } = "";   // 外置快照文件路径
}

public sealed record HistoryStats(int Count, long TotalBytes);

public enum HistoryScope
{
    Global,
    CurrentProject,
}

/// <summary>六种清理模式。</summary>
public enum CleanupMode
{
    All,                  // 全部
    CurrentProjectAll,    // 当前项目全部
    GlobalOlderThanDays,  // 全局 ≥N 天
    ProjectOlderThanDays, // 当前项目 ≥N 天
    SelectedIds,          // 勾选的指定记录
    Duplicates,           // 清理重复项（键 = 工作目录 + 规范化命令文本，只留最新）
}
