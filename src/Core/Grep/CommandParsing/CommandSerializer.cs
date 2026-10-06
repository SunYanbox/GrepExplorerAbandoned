using System.Text;

namespace Core.Grep.CommandParsing;

/// <summary>
/// 规范序列化：块 ↔ 命令文本双向转换的"块 → 文本"方向。
/// 顺序：模式 → 大小写 → 整词/整行 → 反向 → 方言 → 最大命中 → 输出模式 → 上下文 →
/// 字节偏移 → 仅匹配片段 → 组分隔符 → 抑制错误 → 行尾 → 二进制 → 符号链接 → 隐藏 →
/// gitignore → 全部 Pattern/-f/-g → Include → Exclude → Exclude-From → Exclude-Dir →
/// Group → 根目录。同值块去重（含 Group 别名）。
/// 非 -n 输出模式下上下文类块输出为注释行（保留但置灰）。
/// </summary>
public static class CommandSerializer
{
    public static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string Serialize(CommandSpec s)
    {
        var lines = new List<string>();

        lines.Add($"--mode {(s.Mode == SearchMode.Grep ? "grep" : "glob")}");
        if (s.IgnoreCase) lines.Add("-i"); // --no-ignore-case 为默认，省略
        if (s.WholeWord) lines.Add("-w");
        else if (s.WholeLine) lines.Add("-x");
        if (s.InvertMatch) lines.Add("-v");
        if (s.FixedStrings) lines.Add("-F"); // -E 为默认，省略
        if (s.MaxCount > 0) lines.Add($"-m {s.MaxCount}");

        switch (s.Output)
        {
            case OutputMode.FilesWithMatch: lines.Add("-l"); break;
            case OutputMode.FilesWithoutMatch: lines.Add("-L"); break;
            case OutputMode.LineDetails: lines.Add("-n"); break;
        }

        bool detail = s.IsLineDetails;
        // 上下文类块：非详情模式保留但以注释行输出
        void ContextLine(string text)
        {
            lines.Add(detail ? text : "# " + text);
        }

        // Context 归一化：Before==After>0 → -C N；不等 → 分别 -B/-A
        int before = s.ContextBefore, after = s.ContextAfter;
        if (s.Context > 0)
        {
            ContextLine($"-C {s.Context}");
        }
        else if (before > 0 && before == after)
        {
            ContextLine($"-C {before}");
        }
        else
        {
            if (before > 0) ContextLine($"-B {before}");
            if (after > 0) ContextLine($"-A {after}");
        }
        if (s.ByteOffset) ContextLine("-b");
        if (s.OnlyMatching) ContextLine("-o");
        if (s.GroupSeparator != "--") ContextLine("--group-separator " + Quote(s.GroupSeparator));

        if (s.SuppressErrors) lines.Add("-s");
        if (s.ShowEol) lines.Add("--show-eol");
        if (s.BinaryAsText) lines.Add("-a"); // -I 为默认，省略
        if (s.FollowSymlinks) lines.Add("-R");
        if (s.IncludeHidden) lines.Add("--hidden");
        if (s.ReadGitignore) lines.Add("--gitignore");

        if (s.Mode == SearchMode.Grep)
        {
            foreach (var p in s.Patterns.Distinct()) lines.Add("-e " + Quote(p));
            foreach (var f in s.PatternFiles.Distinct()) lines.Add("-f " + Quote(f));
        }
        else
        {
            foreach (var g in s.Globs.Distinct()) lines.Add("-g " + Quote(g));
        }

        foreach (var v in s.Includes.Distinct()) lines.Add("--include " + Quote(v));
        foreach (var v in s.Excludes.Distinct()) lines.Add("--exclude " + Quote(v));
        foreach (var v in s.ExcludeFroms.Distinct()) lines.Add("--exclude-from " + Quote(v));
        foreach (var v in s.ExcludeDirs.Distinct()) lines.Add("--exclude-dir " + Quote(v));
        foreach (var g in s.Groups.Distinct()) lines.Add("--group " + Quote(g));

        lines.Add(Quote(s.RootPath));
        return string.Join('\n', lines);
    }

    /// <summary>归一化命令文本（历史去重键 / front matter command 字段使用）。</summary>
    public static string Normalize(string commandText)
    {
        var result = CommandParser.Parse(commandText);
        if (result.Spec != null) return Serialize(result.Spec);
        // 解析失败时退回去空白比较
        return string.Join(' ', commandText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
