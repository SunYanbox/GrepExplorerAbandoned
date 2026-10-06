namespace Core.Grep;

public enum GrepOutputMode
{
    /// <summary>-c 计数（默认）</summary>
    Count,
    /// <summary>-l 含匹配文件</summary>
    FilesWithMatch,
    /// <summary>-L 不含匹配文件</summary>
    FilesWithoutMatch,
    /// <summary>-n 行号详情</summary>
    LineDetails,
}

public enum GrepBinaryMode
{
    /// <summary>-I 跳过二进制（默认）</summary>
    Skip,
    /// <summary>-a 当文本处理</summary>
    AsText,
}

public enum ContextLineKind
{
    Match,
    Before,
    After,
    OnlyMatchPiece,
}

public sealed record MatchPiece(int CharOffset, int Length, string Text);

public sealed record GrepLineEvent(string FullPath, int LineNumber, long ByteOffset, string Content, ContextLineKind Kind, IReadOnlyList<MatchPiece>? Pieces) : GrepEvent;

public sealed record GrepCountEvent(string FullPath, int Count) : GrepEvent;

public sealed record GrepFileMatchEvent(string FullPath) : GrepEvent;

public sealed record GrepFileNoMatchEvent(string FullPath) : GrepEvent;

public sealed record GrepSeparatorEvent(string FullPath) : GrepEvent;

public sealed record GrepFileCompletedEvent(string FullPath, int MatchedLines, bool Truncated) : GrepEvent;

public sealed record GrepFileSkippedEvent(string FullPath, string Reason) : GrepEvent;

/// <summary>流式产出事件（Channel 承载，禁止 ToList()）。</summary>
public abstract record GrepEvent;

public sealed record GrepOptions
{
    public required IReadOnlyList<string> Patterns { get; init; }

    /// <summary>-F：Pattern 按字面量处理（内部转义后走 Regex）。</summary>
    public bool FixedStrings { get; init; }

    /// <summary>-i：不区分大小写。默认 false（区分）。</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>-w：整词匹配（内部 \b 包裹）。与 -x 同真时引擎按整行处理。</summary>
    public bool WholeWord { get; init; }

    /// <summary>-x：整行匹配（内部 ^$ 包裹）。</summary>
    public bool WholeLine { get; init; }

    /// <summary>-v：反向匹配。</summary>
    public bool InvertMatch { get; init; }

    /// <summary>-m NUM：每文件最大命中行数，0 = 不限。截断即停读该文件。</summary>
    public long MaxCount { get; init; }

    public GrepOutputMode OutputMode { get; init; } = GrepOutputMode.Count;

    /// <summary>Output 别名（测试与 UI 便捷写入）。</summary>
    public GrepOutputMode Output { init => OutputMode = value; }

    /// <summary>
    /// 对称上下文便捷写入（-C N）：同时设置 Before/After。
    /// 取值 &lt;= 0 表示"未指定 -C"，不覆盖已经写入的 -B/-A；
    /// 否则调用方同时写 Before/After/Context 时会因初始化顺序把 -B/-A 静默归零。
    /// </summary>
    public int Context
    {
        init
        {
            if (value <= 0) return;
            ContextBefore = value;
            ContextAfter = value;
        }
    }


    public int ContextBefore { get; init; }

    public int ContextAfter { get; init; }

    /// <summary>-b：输出字节偏移。</summary>
    public bool ByteOffset { get; init; }

    /// <summary>-o：仅输出匹配片段。</summary>
    public bool OnlyMatching { get; init; }

    public string GroupSeparator { get; init; } = "--";

    /// <summary>-s：抑制错误消息（跳过文件汇总不显示）。</summary>
    public bool SuppressErrors { get; init; }

    /// <summary>--show-eol：LF→$，CRLF→^M$。</summary>
    public bool ShowEol { get; init; }

    public GrepBinaryMode Binary { get; init; } = GrepBinaryMode.Skip;

    /// <summary>-R：跟随符号链接。</summary>
    public bool FollowSymlinks { get; init; }

    /// <summary>--hidden：包含隐藏文件（默认跳过）。</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>--gitignore：读取 .gitignore。</summary>
    public bool ReadGitignore { get; init; }

    /// <summary>单文件大小阈值（字节），>0 时超过跳过并提示。0 = 不限制。</summary>
    public long MaxFileSizeBytes { get; init; }

    /// <summary>include/exclude/exclude-dir 规则（可为 null）。</summary>
    public Infrastructure.GlobRuleFilter? Rules { get; init; }

    public string RootPath { get; init; } = ".";
}
