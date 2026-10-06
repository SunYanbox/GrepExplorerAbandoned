using System.Text;
using System.Text.RegularExpressions;

namespace Core.Globbing;

public sealed record GlobPattern(string Raw, string RegexSource, bool Exclude, bool DirOnly);

/// <summary>
/// ripgrep 语义的 Glob 模式解释器：
/// - * 不跨 /，** 跨 /；? 匹配单字符（不含 /）；[abc]/[!abc] 字符类；
/// - 以 / 开头相对搜索根；含 /（非开头）同样锚定到根；不含 / 匹配任意层级的名称；
/// - 尾部 / 仅匹配目录；! 前缀等价 exclude；
/// - 匹配时统一使用 / 分隔符（Windows 路径在匹配前归一化）。
/// </summary>
public static class GlobMatcher
{
    public static GlobPattern Parse(string raw)
    {
        var p = raw.Trim();
        if (p.Length == 0)
            throw new ArgumentException("空的 Glob 模式", nameof(raw));

        bool exclude = p.StartsWith('!');
        if (exclude) p = p[1..].TrimStart();

        bool dirOnly = p.EndsWith('/');
        if (dirOnly) p = p.TrimEnd('/');

        bool anchored = p.StartsWith('/');
        if (anchored) p = p[1..];
        else if (p.Contains('/')) anchored = true;

        if (p.Length == 0)
            throw new ArgumentException($"无效的 Glob 模式：{raw}", nameof(raw));

        return new GlobPattern(raw, Build(p, anchored), exclude, dirOnly);
    }

    public static bool IsMatch(GlobPattern pattern, string relativePath, bool isDirectory)
    {
        if (pattern.DirOnly && !isDirectory)
            return false;
        return Regex.IsMatch(relativePath, pattern.RegexSource, RegexOptions.CultureInvariant);
    }

    private static string Build(string p, bool anchored)
    {
        var sb = new StringBuilder("^");
        if (!anchored)
            sb.Append("(?:.*/)?"); // 不含 / 的模式匹配任意层级的名称

        int i = 0;
        while (i < p.Length)
        {
            char c = p[i];
            if (c == '*')
            {
                int j = i;
                while (j < p.Length && p[j] == '*') j++;
                bool multi = j - i >= 2; // ** 才跨 /
                if (multi && j < p.Length && p[j] == '/')
                {
                    sb.Append("(?:.*/)?"); // **/ 跨任意层级（含零层）
                    i = j + 1;
                }
                else if (multi)
                {
                    sb.Append(".*"); // 尾部 ** 或独立 **
                    i = j;
                }
                else
                {
                    sb.Append("[^/]*"); // 单个 * 不跨 /
                    i = j;
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if (c == '\\')
            {
                if (i + 1 < p.Length)
                {
                    sb.Append(Regex.Escape(p[i + 1].ToString()));
                    i += 2;
                }
                else
                {
                    sb.Append("\\\\");
                    i++;
                }
            }
            else if (c == '[')
            {
                int end = FindClassEnd(p, i);
                if (end < 0)
                {
                    sb.Append("\\[");
                    i++;
                }
                else
                {
                    var inner = p[(i + 1)..end];
                    if (inner.StartsWith('!')) inner = "^" + inner[1..];
                    sb.Append('[').Append(inner.Replace("\\", "\\\\")).Append(']');
                    i = end + 1;
                }
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }

        sb.Append('$');
        return sb.ToString();
    }

    private static int FindClassEnd(string p, int start)
    {
        int i = start + 1;
        if (i < p.Length && (p[i] == '!' || p[i] == '^')) i++;
        if (i < p.Length && p[i] == ']') i++; // 首个 ] 为字面量
        while (i < p.Length)
        {
            if (p[i] == ']') return i;
            i++;
        }
        return -1;
    }
}
