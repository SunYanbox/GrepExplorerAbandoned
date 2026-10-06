using System.Text;

namespace Core.Grep;

/// <summary>
/// 搜索元数据：结果写入 Markdown 编辑器时随文档缓存于内存（不依赖数据库），
/// 保证历史记录被清理后导出仍完整；execute_datetime 保持原搜索时间。
/// </summary>
public sealed record SearchMetadata(
    DateTime ExecuteDateTime,
    string Mode,              // Glob | Grep
    string RootPath,
    IReadOnlyList<string> GroupAliases,
    IReadOnlyList<string> Patterns, // 正则 / Glob 表达式
    int ContextLines,
    int HitFiles,
    int ScannedFiles,
    int HitLines,
    string CommandText);      // 规范化命令文本

/// <summary>
/// 结果文档模板（2.3）与 front matter（2.11）的单一实现。
/// 历史单条导出与结果文档导出共用；来源元数据缺失时置空并警告，不编造。
/// </summary>
public static class ResultDocumentBuilder
{
    public static string FormatTime(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>2.3 搜索结果写入模板（路径内容一律置于 ```text 代码块内）。</summary>
    public static string BuildResultDocument(SearchMetadata meta, string resultLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 搜索结果");
        sb.AppendLine($"- 时间：{FormatTime(meta.ExecuteDateTime)}");
        sb.AppendLine($"- 模式：{meta.Mode}（上下文 {meta.ContextLines} 行）");
        sb.AppendLine($"- 根目录：{meta.RootPath}");
        sb.AppendLine($"- 组：{(meta.GroupAliases.Count > 0 ? string.Join(", ", meta.GroupAliases) : "（无）")}");
        string joined = string.Join(" | ", meta.Patterns);
        if (meta.Mode == "Glob")
            sb.AppendLine($"- Glob：`{joined}`");
        else
            sb.AppendLine($"- 正则：`{joined}`");
        sb.AppendLine($"- 命中文件数：{meta.HitFiles} / 扫描文件数：{meta.ScannedFiles} / 总命中行数：{meta.HitLines}");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.AppendLine(resultLines);
        sb.AppendLine("```");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>需要为 YAML 值加双引号时转义。</summary>
    private static bool NeedsYamlQuoting(string value)
    {
        // 仅在真正破坏 YAML 纯量语义时加引号（时间戳/普通路径保持原样；command 含引号时整行双引号包裹并转义）
        if (value.Length == 0) return true;
        if (value.Contains('\n') || value.Contains('"') || value.Contains(": ") || value.Contains(" #")) return true;
        if (value.StartsWith(' ') || value.EndsWith(' ')) return true;
        return "-?:,[]{}#&*!|>'\"%@`".Contains(value[0]);
    }

    private static string YamlValue(string value)
    {
        if (!NeedsYamlQuoting(value)) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>2.11 front matter（单一实现，历史导出与结果文档导出共用）。</summary>
    public static string BuildFrontMatter(SearchMetadata? meta)
    {
        string dt = meta != null ? FormatTime(meta.ExecuteDateTime) : "";
        string command = meta != null ? meta.CommandText : "";
        string folder = meta != null ? meta.RootPath : "";
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"execute_datetime: {YamlValue(dt)}");
        sb.AppendLine($"command: {YamlValue(command)}");
        sb.AppendLine($"execute_folder_path: {YamlValue(folder)}");
        sb.AppendLine("---");
        return sb.ToString();
    }

    /// <summary>
    /// 统一导出入口：来源于搜索结果/历史的文档携带 front matter；普通文档（meta == null）不追加。
    /// </summary>
    public static string BuildExportDocument(SearchMetadata? meta, string editorText)
        => meta != null ? BuildFrontMatter(meta) + "\n" + editorText : editorText;
}
