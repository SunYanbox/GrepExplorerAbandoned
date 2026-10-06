using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace GrepExplorer.UI.Controls;

/// <summary>Markdown 语法着色（标题/粗斜体/行内代码/代码围栏/链接/引用）。</summary>
public class MarkdownHighlightingTransformer : DocumentColorizingTransformer
{
    private static readonly Color HeaderC = Color.FromRgb(0x56, 0x9C, 0xD6);
    private static readonly Color BoldC = Color.FromRgb(0xD7, 0xBA, 0x7D);
    private static readonly Color ItalicC = Color.FromRgb(0xD7, 0xBA, 0x7D);
    private static readonly Color CodeC = Color.FromRgb(0xCE, 0x91, 0x78);
    private static readonly Color FenceC = Color.FromRgb(0x6A, 0x99, 0x55);
    private static readonly Color LinkC = Color.FromRgb(0x4E, 0xC9, 0xB0);
    private static readonly Color QuoteC = Color.FromRgb(0x6A, 0x99, 0x55);

    protected override void ColorizeLine(DocumentLine line)
    {
        string text = CurrentContext.Document.GetText(line);
        if (text.Length == 0) return;

        if (text.TrimStart().StartsWith("```"))
        {
            Style(line, 0, text.Length, FenceC, bold: false);
            return;
        }

        var header = Regex.Match(text, @"^#{1,3} ");
        if (header.Success)
            Style(line, 0, text.Length, HeaderC, bold: true);

        var quote = Regex.Match(text, @"^>\s?");
        if (quote.Success)
            Style(line, 0, text.Length, QuoteC, bold: false);

        foreach (Match m in Regex.Matches(text, @"\*\*[^*]+\*\*"))
            Style(line, m.Index, m.Length, BoldC, bold: true);

        foreach (Match m in Regex.Matches(text, @"(?<!\*)\*[^*\s][^*]*\*(?!\*)"))
            Style(line, m.Index, m.Length, ItalicC, bold: false);

        foreach (Match m in Regex.Matches(text, "`[^`]+`"))
            Style(line, m.Index, m.Length, CodeC, bold: false);

        foreach (Match m in Regex.Matches(text, @"\[[^\]]*\]\([^)]*\)"))
            Style(line, m.Index, m.Length, LinkC, bold: false);
    }

    private void Style(DocumentLine line, int start, int len, Color color, bool bold)
    {
        ChangeLinePart(line.Offset + start, line.Offset + start + len, el =>
        {
            el.TextRunProperties.SetForegroundBrush(new SolidColorBrush(color));
            if (bold)
                el.TextRunProperties.SetTypeface(new Typeface(el.TextRunProperties.Typeface.FontFamily,
                    System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold,
                    el.TextRunProperties.Typeface.Stretch));
        });
    }
}
