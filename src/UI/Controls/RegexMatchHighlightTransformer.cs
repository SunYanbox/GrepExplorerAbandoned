using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace GrepExplorer.UI.Controls;

/// <summary>正则测试区命中高亮。</summary>
public class RegexMatchHighlightTransformer : DocumentColorizingTransformer
{
    private readonly CodeEditor _owner;
    private List<(int Start, int Length)> _matches = new();

    public RegexMatchHighlightTransformer(CodeEditor owner) => _owner = owner;

    public void UpdateMatches(List<(int Start, int Length)> matches)
    {
        _matches = matches;
        _owner.TextArea.TextView.Redraw();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var (start, len) in _matches)
        {
            int lineStart = line.Offset;
            int lineEnd = line.EndOffset;
            int s = Math.Max(start, lineStart);
            int e = Math.Min(start + len, lineEnd);
            if (e <= s) continue;
            var bg = new SolidColorBrush(Color.FromArgb(70, 0x4E, 0xC9, 0xB0));
            bg.Freeze();
            ChangeLinePart(s, e, el => el.TextRunProperties.SetBackgroundBrush(bg));
        }
    }
}
