using System.Text;
using GrepExplorer.UI.ViewModels;

namespace GrepExplorer.UI.Controls;

/// <summary>Markdown 编辑器桥实现。</summary>
public sealed class MarkdownEditorBridge(CodeEditor editor) : IEditorBridge
{
    public void InsertAtCaret(string text) => editor.InsertAtCaret(text);

    public void WrapSelection(string before, string after) => editor.ApplySnippet(before, after);

    public void InsertAtLineStart(string prefix)
    {
        int lineOffset = editor.Document.GetLineByOffset(editor.TextArea.Caret.Offset).Offset;
        editor.Document.Insert(lineOffset, prefix);
        editor.TextArea.Caret.Offset = lineOffset + prefix.Length;
        editor.Focus();
    }
}

/// <summary>正则编辑器桥实现（插入 + 测试区命中高亮）。</summary>
public sealed class RegexBridge(CodeEditor editor, RegexMatchHighlightTransformer transformer) : IRegexEditorBridge
{
    public void InsertAtCaret(string text) => editor.InsertAtCaret(text);

    public void UpdateMatches(IReadOnlyList<(int Index, int Length)> matches)
        => transformer.UpdateMatches([.. matches]);

    /// <summary>定位错误位置（光标跳转 + 高亮提示）。</summary>
    public void MoveTo(int offset)
    {
        if (offset < 0 || offset > editor.Document.TextLength) return;
        editor.TextArea.Caret.Offset = offset;
        editor.ScrollToLine(editor.Document.GetLineByOffset(offset).LineNumber);
    }
}

/// <summary>构建 2.3 模板用的文本辅助（路径内容置于 ```text 代码块内）。</summary>
public static class EditorTextHelper
{
    public static string WrapInTextFence(string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```text");
        sb.AppendLine(content.TrimEnd());
        sb.Append("```");
        return sb.ToString();
    }
}
