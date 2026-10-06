namespace Core.Grep.CommandParsing;

public enum SearchMode { Grep, Glob }

public enum OutputMode
{
    /// <summary>-c 计数（默认，序列化省略）</summary>
    Count,
    /// <summary>-l</summary>
    FilesWithMatch,
    /// <summary>-L</summary>
    FilesWithoutMatch,
    /// <summary>-n</summary>
    LineDetails,
}

/// <summary>命令编辑器统一参数模型（块列表的状态化表示）。</summary>
public sealed class CommandSpec
{
    public SearchMode Mode { get; set; } = SearchMode.Grep;

    public List<string> Patterns { get; set; } = new();        // -e
    public List<string> PatternFiles { get; set; } = new();    // -f
    public List<string> Globs { get; set; } = new();           // -g（仅 Glob）
    public List<string> Includes { get; set; } = new();        // --include
    public List<string> Excludes { get; set; } = new();        // --exclude
    public List<string> ExcludeFroms { get; set; } = new();    // --exclude-from
    public List<string> ExcludeDirs { get; set; } = new();     // --exclude-dir
    public List<string> Groups { get; set; } = new();          // --group

    /// <summary>-i（true = 不区分大小写；false = 默认区分，序列化省略 --no-ignore-case）</summary>
    public bool IgnoreCase { get; set; }

    public bool WholeWord { get; set; }   // -w
    public bool WholeLine { get; set; }   // -x（与 -w 互斥，后选者胜）
    public bool InvertMatch { get; set; } // -v

    /// <summary>-F 固定字面量；false = -E .NET Regex（默认，省略）</summary>
    public bool FixedStrings { get; set; }

    /// <summary>-m NUM，0 = 不限</summary>
    public long MaxCount { get; set; }

    public OutputMode Output { get; set; } = OutputMode.Count;

    public int ContextBefore { get; set; }  // -B N
    public int ContextAfter { get; set; }   // -A N
    public int Context { get; set; }        // -C N（-C 解析只填这里）

    public bool ByteOffset { get; set; }        // -b
    public bool OnlyMatching { get; set; }      // -o
    public string GroupSeparator { get; set; } = "--"; // --group-separator
    public bool SuppressErrors { get; set; }    // -s
    public bool ShowEol { get; set; }           // --show-eol
    public bool BinaryAsText { get; set; }      // -a（false = -I 默认省略）
    public bool FollowSymlinks { get; set; }    // -R
    public bool IncludeHidden { get; set; }     // --hidden
    public bool ReadGitignore { get; set; }     // --gitignore

    public string RootPath { get; set; } = "";

    public bool IsLineDetails => Output == OutputMode.LineDetails;
}
