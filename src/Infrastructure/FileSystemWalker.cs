namespace Infrastructure;

public sealed record FileSystemEntry(string FullPath, string RelativePath, bool IsDirectory);

public sealed record WalkOptions
{
    /// <summary>包含以 . 开头的隐藏文件/目录（默认跳过，对应 --hidden）。</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>跟随符号链接（-R）。默认不跟随。</summary>
    public bool FollowSymlinks { get; init; }

    /// <summary>读取 .gitignore（--gitignore）。默认不读取。</summary>
    public bool ReadGitignore { get; init; }

    /// <summary>单文件大小阈值（字节），>0 时超过则跳过并回调提示。</summary>
    public long MaxFileSizeBytes { get; init; }

    /// <summary>include/exclude/exclude-dir 规则集（可为 null = 不筛选）。</summary>
    public GlobRuleFilter? Filter { get; init; }
}

/// <summary>与 Core.Globbing 解耦的目录/文件筛选接口（由 GlobRuleSet 实现）。</summary>
public interface GlobRuleFilter
{
    /// <summary>目录是否应被整棵剪除。</summary>
    bool IsDirPruned(string relativeDirPath, string dirName);

    /// <summary>文件是否通过 include(OR)/exclude(一票否决) 筛选。</summary>
    bool IsFileAccepted(string relativeFilePath);
}

/// <summary>
/// 流式文件系统遍历（不 ToList()）。符号链接默认不跟随；
/// 隐藏文件（名称以 . 开头）默认跳过；可选 .gitignore 支持（简化语义）。
/// </summary>
public static class FileSystemWalker
{
    public static void Walk(
        string root,
        WalkOptions options,
        Action<FileSystemEntry> onFile,
        Action<string, string>? onSkipped,
        CancellationToken ct)
    {
        var rootFull = Path.GetFullPath(root);
        if (!Directory.Exists(rootFull))
        {
            onSkipped?.Invoke(rootFull, "根目录不存在");
            return;
        }

        WalkDir(rootFull, string.Empty, options, onFile, onSkipped, new List<GitIgnoreSet>(), ct);
    }

    private static void WalkDir(
        string dirFull, string dirRel,
        WalkOptions o, Action<FileSystemEntry> onFile, Action<string, string>? onSkipped,
        List<GitIgnoreSet> ignoreChain, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var dirInfo = new DirectoryInfo(dirFull);
        if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0 && dirRel.Length > 0 && !o.FollowSymlinks)
            return; // 符号链接目录默认不进入

        int pushed = 0;
        if (o.ReadGitignore)
        {
            var gi = Path.Combine(dirFull, ".gitignore");
            if (File.Exists(gi))
            {
                try
                {
                    ignoreChain.Add(GitIgnoreSet.Load(dirRel, gi));
                    pushed++;
                }
                catch { /* 无法读取的 .gitignore 忽略 */ }
            }
        }

        try
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = dirInfo.EnumerateFileSystemInfos(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                onSkipped?.Invoke(dirFull, ex.Message);
                return;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.Name;
                if (!o.IncludeHidden && name.StartsWith('.'))
                    continue; // 隐藏文件/目录

                bool isDir = entry is DirectoryInfo;
                var rel = dirRel.Length == 0 ? name : dirRel + "/" + name;

                if (o.ReadGitignore && IsIgnoredByChain(ignoreChain, dirRel, rel, name, isDir))
                    continue;

                if (isDir)
                {
                    if (o.Filter?.IsDirPruned(rel, name) == true)
                        continue;
                    WalkDir(entry.FullName, rel, o, onFile, onSkipped, ignoreChain, ct);
                }
                else
                {
                    if (o.Filter?.IsFileAccepted(rel) == false)
                        continue;

                    if (o.MaxFileSizeBytes > 0)
                    {
                        long len;
                        try { len = ((FileInfo)entry).Length; }
                        catch { continue; }
                        if (len > o.MaxFileSizeBytes)
                        {
                            onSkipped?.Invoke(entry.FullName, $"文件大小 {len / 1024.0 / 1024.0:F1} MB 超过阈值，已跳过");
                            continue;
                        }
                    }

                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && !o.FollowSymlinks)
                        continue; // 符号链接文件

                    onFile(new FileSystemEntry(entry.FullName, rel, false));
                }
            }
        }
        finally
        {
            for (int i = 0; i < pushed; i++)
                ignoreChain.RemoveAt(ignoreChain.Count - 1);
        }
    }

    /// <summary>由外层到内层逐级判定，内层规则覆盖外层结果（简化 gitignore 语义）。</summary>
    private static bool IsIgnoredByChain(List<GitIgnoreSet> chain, string dirRel, string rel, string name, bool isDir)
    {
        bool ignored = false;
        foreach (var set in chain)
        {
            if (set.Matches(dirRel, name, rel, isDir, out bool ruleValue))
                ignored = ruleValue;
        }
        return ignored;
    }
}

/// <summary>单个 .gitignore 文件的规则集（简化版 gitignore 语义：支持 ! 取反、目录限定、锚定）。</summary>
public sealed class GitIgnoreSet
{
    private readonly List<(IgnorePattern Pattern, bool Negation)> _rules = new();

    public string BaseDir { get; }

    private GitIgnoreSet(string baseDir) => BaseDir = baseDir;

    public static GitIgnoreSet Load(string baseDir, string path)
    {
        var set = new GitIgnoreSet(baseDir);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            bool negation = line.StartsWith('!');
            if (negation) line = line[1..];
            if (line.Length == 0) continue;
            set._rules.Add((IgnorePattern.Compile(line), negation));
        }
        return set;
    }

    /// <summary>
    /// dirRel：条目相对根目录的父路径；rel：条目相对根目录完整路径；name：条目名。
    /// 命中返回 true，ruleValue 表示最终规则值（false = 取反豁免）。
    /// </summary>
    public bool Matches(string dirRel, string name, string rel, bool isDir, out bool ruleValue)
    {
        ruleValue = false;
        if (!rel.StartsWith(BaseDir.Length == 0 ? "" : BaseDir + "/"))
            return false;
        var relToBase = BaseDir.Length == 0 ? rel : rel[(BaseDir.Length + 1)..];

        foreach (var (pat, neg) in _rules)
        {
            if (pat.IsMatch(relToBase, isDir))
            {
                ruleValue = !neg;
                return true;
            }
        }
        return false;
    }
}

/// <summary>编译后的 gitignore 行模式（相对 .gitignore 所在目录）。</summary>
public sealed class IgnorePattern
{
    private readonly System.Text.RegularExpressions.Regex _rx;
    private readonly bool _dirOnly;
    private readonly bool _nameOnly;

    private IgnorePattern(System.Text.RegularExpressions.Regex rx, bool dirOnly, bool nameOnly)
    {
        _rx = rx;
        _dirOnly = dirOnly;
        _nameOnly = nameOnly;
    }

    public static IgnorePattern Compile(string line)
    {
        bool dirOnly = line.EndsWith('/');
        if (dirOnly) line = line.TrimEnd('/');
        bool anchored = line.StartsWith('/');
        if (anchored) line = line[1..];
        else if (line.Contains('/')) anchored = true;
        bool nameOnly = !anchored && !line.Contains('/');
        var rx = CompileRegex(line, anchored);
        return new IgnorePattern(rx, dirOnly, nameOnly);
    }

    public bool IsMatch(string relToBase, bool isDir)
    {
        if (_dirOnly && !isDir) return false;
        if (_rx.IsMatch(relToBase)) return true;
        if (_nameOnly)
        {
            int idx = relToBase.LastIndexOf('/');
            var name = idx >= 0 ? relToBase[(idx + 1)..] : relToBase;
            if (_rx.IsMatch(name)) return true;
        }
        return false;
    }

    private static System.Text.RegularExpressions.Regex CompileRegex(string p, bool anchored)
    {
        var sb = new System.Text.StringBuilder("^");
        if (!anchored) sb.Append("(?:.*/)?");
        int i = 0;
        while (i < p.Length)
        {
            char c = p[i];
            if (c == '*')
            {
                int j = i;
                while (j < p.Length && p[j] == '*') j++;
                if (j < p.Length && p[j] == '/')
                {
                    sb.Append("(?:.*/)?");
                    i = j + 1;
                }
                else
                {
                    sb.Append(".*");
                    i = j;
                }
            }
            else if (c == '?') { sb.Append("[^/]"); i++; }
            else if (c == '[')
            {
                int end = FindClassEnd(p, i);
                if (end < 0) { sb.Append("\\["); i++; }
                else
                {
                    var inner = p.Substring(i + 1, end - i - 1);
                    if (inner.StartsWith('!')) inner = "^" + inner[1..];
                    sb.Append('[').Append(inner.Replace("\\", "\\\\")).Append(']');
                    i = end + 1;
                }
            }
            else { sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString())); i++; }
        }
        sb.Append('$');
        return new System.Text.RegularExpressions.Regex(sb.ToString());
    }

    private static int FindClassEnd(string p, int start)
    {
        int i = start + 1;
        if (i < p.Length && (p[i] == '!' || p[i] == '^')) i++;
        if (i < p.Length && p[i] == ']') i++;
        while (i < p.Length)
        {
            if (p[i] == ']') return i;
            i++;
        }
        return -1;
    }
}
