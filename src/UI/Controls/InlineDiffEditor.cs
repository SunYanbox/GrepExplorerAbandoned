using System.Windows;
using System.Windows.Media;
using Core.Diff;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace GrepExplorer.UI.Controls;

/// <summary>
/// VSCode Inline 风格 Diff 渲染控件：
/// 单栏显示，变更行整行呈现，行内删除片段红色删除线、新增片段绿色高亮（DiffPlex 词级 diff）。
/// </summary>
public class InlineDiffEditor : CodeEditor
{
    private readonly InlineDiffColorizer _transformer = new();

    public InlineDiffEditor()
    {
        IsReadOnly = true;
        ShowLineNumbers = false;
        FontSize = 12;
        TextArea.TextView.LineTransformers.Add(_transformer);
    }

    /// <summary>展示 diff 结果。</summary>
    public void Show(InlineDiffResult result)
    {
        var sb = new System.Text.StringBuilder();
        var segments = new List<(int Start, int Len, DiffPieceKind Kind)>();
        var changedLines = new HashSet<int>();
        int lineNo = 1;
        foreach (var line in result.Lines)
        {
            int lineStart = sb.Length;
            foreach (var w in line.Words)
            {
                if (w.Kind != DiffPieceKind.Same && w.Text.Length > 0)
                    segments.Add((lineStart + sb.Length, w.Text.Length, w.Kind));
                sb.Append(w.Text);
            }
            sb.Append('\n');
            if (line.Kind == DiffLineKind.Changed)
                changedLines.Add(lineNo);
            lineNo++;
        }
        _transformer.Data = (segments, changedLines);
        Text = sb.ToString();
        IsReadOnly = true;
    }

    private sealed class InlineDiffColorizer : DocumentColorizingTransformer
    {
        public (List<(int Start, int Len, DiffPieceKind Kind)> Segments, HashSet<int> ChangedLines) Data =
            (new List<(int, int, DiffPieceKind)>(), new HashSet<int>());

        protected override void ColorizeLine(DocumentLine line)
        {
            int lineIndex = line.LineNumber;
            if (Data.ChangedLines.Contains(lineIndex))
            {
                var lineBg = new SolidColorBrush(Color.FromArgb(28, 0xD1, 0x24, 0x24));
                lineBg.Freeze();
                ChangeLinePart(line.Offset, line.EndOffset,
                    el => el.TextRunProperties.SetBackgroundBrush(lineBg));
            }

            foreach (var (start, len, kind) in Data.Segments)
            {
                int s = Math.Max(start, line.Offset);
                int e = Math.Min(start + len, line.EndOffset);
                if (e <= s) continue;
                var seg = (start, len, kind);
                ChangeLinePart(s, e, el =>
                {
                    if (seg.kind == DiffPieceKind.Deleted)
                    {
                        var bg = new SolidColorBrush(Color.FromArgb(70, 0xD1, 0x24, 0x24));
                        var fg = new SolidColorBrush(Color.FromRgb(0xE5, 0x51, 0x4B));
                        var strike = new TextDecoration
                        {
                            Location = TextDecorationLocation.Strikethrough,
                            Pen = new Pen(fg, 1),
                        };
                        var coll = new System.Windows.TextDecorationCollection { strike };
                        el.TextRunProperties.SetTextDecorations(coll);
                        el.TextRunProperties.SetBackgroundBrush(bg);
                    }
                    else if (seg.kind == DiffPieceKind.Inserted)
                    {
                        var bg = new SolidColorBrush(Color.FromArgb(70, 0x2E, 0xA0, 0x43));
                        el.TextRunProperties.SetBackgroundBrush(bg);
                    }
                });
            }
        }
    }
}
