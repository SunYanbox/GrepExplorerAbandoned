using Infrastructure;

namespace Core.Globbing;

/// <summary>模式角色：Include（含匹配）/ Exclude（排除）/ Reinclude（豁免，exclude 列表中 ! 前缀）。</summary>
public enum GlobRole
{
    Include,
    Exclude,
    Reinclude,
}

/// <summary>
/// include(取 OR) / exclude(取 OR，一票否决) / exclude-from(文件内每行一条) / exclude-dir(目录名) 组合规则。
/// include 列表中 ! 前缀等价 exclude；exclude / exclude-from 列表中 ! 前缀等价 re-include（豁免）。
/// </summary>
public sealed class GlobRuleSet : GlobRuleFilter
{
    private readonly List<(GlobPattern Pattern, GlobRole Role)> _patterns = new();
    private readonly List<GlobPattern> _dirExcludes = new();

    private GlobRuleSet() { }

    public static GlobRuleSet Create(
        IEnumerable<string>? includes,
        IEnumerable<string>? excludes,
        IEnumerable<string>? excludeFromLines,
        IEnumerable<string>? excludeDirs)
    {
        var rs = new GlobRuleSet();
        AddRange(rs._patterns, includes, defaultExclude: false);
        AddRange(rs._patterns, excludes, defaultExclude: true);
        AddRange(rs._patterns, excludeFromLines, defaultExclude: true);
        if (excludeDirs != null)
        {
            foreach (var raw in excludeDirs)
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                rs._dirExcludes.Add(GlobMatcher.Parse(t));
            }
        }
        return rs;
    }

    private static void AddRange(List<(GlobPattern, GlobRole)> list, IEnumerable<string>? lines, bool defaultExclude)
    {
        if (lines == null) return;
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;

            if (t.StartsWith('!'))
            {
                var p = GlobMatcher.Parse(t[1..].TrimStart());
                // include 列表：! 等价 exclude；exclude 列表：! 等价 re-include
                list.Add((p, defaultExclude ? GlobRole.Reinclude : GlobRole.Exclude));
            }
            else
            {
                list.Add((GlobMatcher.Parse(t), defaultExclude ? GlobRole.Exclude : GlobRole.Include));
            }
        }
    }

    public bool HasAnyInclude => _patterns.Any(x => x.Role == GlobRole.Include);

    /// <summary>目录是否被排除（整棵剪除）：exclude-dir 名称匹配，或 exclude 匹配目录相对路径（re-include 豁免）。</summary>
    public bool IsDirPruned(string relativeDirPath, string dirName)
    {
        foreach (var d in _dirExcludes)
            if (GlobMatcher.IsMatch(d, dirName, true))
                return true;
        bool excluded = false, reincluded = false;
        foreach (var (p, role) in _patterns)
        {
            if (role == GlobRole.Include) continue;
            if (GlobMatcher.IsMatch(p, relativeDirPath, true))
            {
                if (role == GlobRole.Reinclude) reincluded = true;
                else excluded = true;
            }
        }
        return excluded && !reincluded;
    }

    /// <summary>文件是否通过筛选：任一 include 匹配（无 include 则全部通过）且未被 exclude 一票否决（re-include 可豁免）。</summary>
    public bool IsFileAccepted(string relativeFilePath)
    {
        bool matchedInclude = false, excluded = false, reincluded = false;
        foreach (var (p, role) in _patterns)
        {
            if (p.DirOnly) continue;
            if (GlobMatcher.IsMatch(p, relativeFilePath, false))
            {
                switch (role)
                {
                    case GlobRole.Exclude: excluded = true; break;
                    case GlobRole.Reinclude: reincluded = true; break;
                    default: matchedInclude = true; break;
                }
            }
        }
        if (excluded && !reincluded)
            return false;   // 一票否决
        return !HasAnyInclude || matchedInclude;
    }

    /// <summary>任意路径（文件或目录）是否命中 include（用于 Glob 模式输出目录列表）。</summary>
    public bool IsIncluded(string relativePath, bool isDirectory)
    {
        bool matched = false;
        foreach (var (p, role) in _patterns)
        {
            if (role != GlobRole.Include) continue;
            if (GlobMatcher.IsMatch(p, relativePath, isDirectory))
                matched = true;
        }
        return matched;
    }

    /// <summary>任意路径是否被排除（一票否决，re-include 豁免）。</summary>
    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        if (isDirectory && IsDirPruned(relativePath, NameOf(relativePath)))
            return true;
        bool excluded = false, reincluded = false;
        foreach (var (p, role) in _patterns)
        {
            if (role == GlobRole.Include) continue;
            if (GlobMatcher.IsMatch(p, relativePath, isDirectory))
            {
                if (role == GlobRole.Reinclude) reincluded = true;
                else excluded = true;
            }
        }
        return excluded && !reincluded;
    }

    private static string NameOf(string relativePath)
    {
        int idx = relativePath.LastIndexOf('/');
        return idx >= 0 ? relativePath[(idx + 1)..] : relativePath;
    }
}
