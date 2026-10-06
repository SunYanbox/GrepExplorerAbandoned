using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace GrepExplorer.UI.Controls;

public enum RegexTokenKind
{
    Plain,
    Escape,
    CharClass,
    Quantifier,
    GroupStart,
    GroupEnd,
    Anchor,
    NamedGroup,
    Lookaround,
    Alternation,
}

/// <summary>
/// .NET Regex 语法着色：字符类 / 量词 / 分组 / 锚点 / 断言 / 命名分组 / 前后瞻。
/// 根据编辑器背景亮度自动选择深/浅色配色，避免对比刺眼。
/// </summary>
public class RegexHighlightingTransformer : DocumentColorizingTransformer
{
    private static readonly (RegexTokenKind Kind, Color Dark, Color Light)[] Palette =
    [
        (RegexTokenKind.Escape,      Color.FromRgb(0xCE, 0x91, 0x78), Color.FromRgb(0xA3, 0x15, 0x15)),
        (RegexTokenKind.CharClass,   Color.FromRgb(0x4E, 0xC9, 0xB0), Color.FromRgb(0x0E, 0x8A, 0x72)),
        (RegexTokenKind.Quantifier,  Color.FromRgb(0xD7, 0xBA, 0x7D), Color.FromRgb(0xB2, 0x6A, 0x00)),
        (RegexTokenKind.GroupStart,  Color.FromRgb(0x56, 0x9C, 0xD6), Color.FromRgb(0x26, 0x4F, 0x78)),
        (RegexTokenKind.GroupEnd,    Color.FromRgb(0x56, 0x9C, 0xD6), Color.FromRgb(0x26, 0x4F, 0x78)),
        (RegexTokenKind.Anchor,      Color.FromRgb(0xDC, 0xDC, 0xAA), Color.FromRgb(0x79, 0x5E, 0x26)),
        (RegexTokenKind.NamedGroup,  Color.FromRgb(0x9C, 0xDC, 0xFE), Color.FromRgb(0x04, 0x51, 0xA5)),
        (RegexTokenKind.Lookaround,  Color.FromRgb(0xC5, 0x86, 0xC0), Color.FromRgb(0xAF, 0x00, 0xDB)),
        (RegexTokenKind.Alternation, Color.FromRgb(0xD4, 0xD4, 0xD4), Color.FromRgb(0x66, 0x66, 0x66)),
    ];

    private readonly CodeEditor _owner;

    public RegexHighlightingTransformer(CodeEditor owner) => _owner = owner;

    protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
    {
        string text = CurrentContext.Document.GetText(line);
        bool dark = _owner.IsDarkTheme;
        foreach (var (kind, start, len) in Tokenize(text))
        {
            if (kind == RegexTokenKind.Plain || len <= 0) continue;
            var entry = Palette.First(p => p.Kind == kind);
            var brush = new SolidColorBrush(dark ? entry.Dark : entry.Light);
            brush.Freeze();
            ChangeLinePart(line.Offset + start, line.Offset + start + len, el =>
            {
                el.TextRunProperties.SetForegroundBrush(brush);
            });
        }
    }

    /// <summary>逐字符扫描正则串，产出 token（手工解析，处理转义与字符类）。</summary>
    public static List<(RegexTokenKind Kind, int Start, int Length)> Tokenize(string pattern)
    {
        var result = new List<(RegexTokenKind, int, int)>();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            switch (c)
            {
                case '\\':
                    int len = i + 1 < pattern.Length ? 2 : 1;
                    var kind = i + 1 < pattern.Length && (pattern[i + 1] == 'b' || pattern[i + 1] == 'B' || pattern[i + 1] == 'A' || pattern[i + 1] == 'Z' || pattern[i + 1] == 'z' || pattern[i + 1] == 'G')
                        ? RegexTokenKind.Lookaround   // 断言
                        : RegexTokenKind.Escape;
                    result.Add((kind, i, len));
                    i += len;
                    break;

                case '[':
                {
                    int end = FindClassEnd(pattern, i);
                    result.Add((RegexTokenKind.CharClass, i, (end < 0 ? pattern.Length : end + 1) - i));
                    i = end < 0 ? pattern.Length : end + 1;
                    break;
                }

                case '*':
                case '+':
                {
                    int j = i + 1;
                    if (j < pattern.Length && pattern[j] == '?') j++;
                    result.Add((RegexTokenKind.Quantifier, i, j - i));
                    i = j;
                    break;
                }

                case '{':
                {
                    int end = pattern.IndexOf('}', i);
                    if (end < 0) { result.Add((RegexTokenKind.Plain, i++, 1)); break; }
                    result.Add((RegexTokenKind.Quantifier, i, end - i + 1));
                    i = end + 1;
                    break;
                }

                case '?':
                    // 单独的 ? 视为量词（前面由 (?: 消费）
                    result.Add((RegexTokenKind.Quantifier, i, 1));
                    i++;
                    break;

                case '(':
                {
                    if (i + 1 < pattern.Length && pattern[i + 1] == '?')
                    {
                        if (i + 2 < pattern.Length && (pattern[i + 2] == '=' || pattern[i + 2] == '!'))
                        {
                            result.Add((RegexTokenKind.Lookaround, i, 3));
                            i += 3;
                            break;
                        }
                        if (i + 3 < pattern.Length && pattern[i + 2] == '<' && (pattern[i + 3] == '=' || pattern[i + 3] == '!'))
                        {
                            result.Add((RegexTokenKind.Lookaround, i, 4));
                            i += 4;
                            break;
                        }
                        if (i + 2 < pattern.Length && pattern[i + 2] == '<')
                        {
                            // 命名分组 (?<name> ... ) 的开头
                            int close = pattern.IndexOf('>', i + 3);
                            int nlen = (close < 0 ? pattern.Length : close + 1) - i;
                            result.Add((RegexTokenKind.NamedGroup, i, nlen));
                            i += nlen;
                            break;
                        }
                        // (?:) (?# 等
                        int j2 = i + 2 < pattern.Length ? 3 : 2;
                        result.Add((RegexTokenKind.GroupStart, i, j2));
                        i += j2;
                        break;
                    }
                    result.Add((RegexTokenKind.GroupStart, i, 1));
                    i++;
                    break;
                }

                case ')':
                    result.Add((RegexTokenKind.GroupEnd, i, 1));
                    i++;
                    break;

                case '^':
                case '$':
                    result.Add((RegexTokenKind.Anchor, i, 1));
                    i++;
                    break;

                case '|':
                    result.Add((RegexTokenKind.Alternation, i, 1));
                    i++;
                    break;

                default:
                    i++;
                    break;
            }
        }
        return result;
    }

    private static int FindClassEnd(string p, int start)
    {
        int i = start + 1;
        if (i < p.Length && p[i] == '^') i++;
        if (i < p.Length && p[i] == ']') i++;
        while (i < p.Length)
        {
            if (p[i] == ']') return i;
            if (p[i] == '\\') i++;
            i++;
        }
        return -1;
    }
}
