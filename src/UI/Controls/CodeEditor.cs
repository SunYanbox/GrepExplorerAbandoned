using System.ComponentModel;
using System.Windows.Data;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;

namespace GrepExplorer.UI.Controls;

/// <summary>
/// AvalonEdit 封装（CodeEditor）：
/// Text 双向绑定、行号、Tab 缩进、Fluent 主题对齐（跟随系统深浅色）、查找替换（SearchPanel）。
/// </summary>
public class CodeEditor : TextEditor
{
    public static readonly DependencyProperty CodeTextProperty =
        DependencyProperty.Register(nameof(CodeText), typeof(string), typeof(CodeEditor),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnCodeTextChanged, null, false, System.Windows.Data.UpdateSourceTrigger.LostFocus));

    private bool _syncing;

    public CodeEditor()
    {
        ShowLineNumbers = true;
        Options.ConvertTabsToSpaces = false; // Tab 缩进
        Options.IndentationSize = 4;
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI");
        FontSize = 13;
        ApplyFluentTheme();
        SearchPanel.Install(this);
        TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            SetCurrentValue(CodeTextProperty, Text);
            _syncing = false;
        };
    }

    public string CodeText
    {
        get => (string)GetValue(CodeTextProperty);
        set => SetValue(CodeTextProperty, value);
    }

    private static void OnCodeTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        var value = (string?)e.NewValue ?? string.Empty;
        if (editor.Text == value) return;
        editor._syncing = true;
        editor.Text = value;
        editor._syncing = false;
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // TextProperty 变化由 TextChanged 事件统一同步
    }

    /// <summary>对齐 Fluent 主题颜色令牌（跟随系统深浅色，避免深色模式刺眼）。</summary>
    public void ApplyFluentTheme()
    {
        var win = SystemColors.WindowColor;
        var txt = SystemColors.WindowTextColor;
        Background = new SolidColorBrush(win);
        Foreground = new SolidColorBrush(txt);
        bool dark = (win.R * 0.299 + win.G * 0.587 + win.B * 0.114) < 128;
        LineNumbersForeground = new SolidColorBrush(Color.FromArgb(140, txt.R, txt.G, txt.B));
        var sel = SystemColors.HighlightColor;
        TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(90, sel.R, sel.G, sel.B));
        TextArea.SelectionForeground = null;
        TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.FromArgb(70, txt.R, txt.G, txt.B)), 1);
        Options.HighlightCurrentLine = true;
        TextArea.TextView.InvalidateVisual();
        _isDark = dark;
    }

    private bool _isDark;

    public bool IsDarkTheme => _isDark;

    /// <summary>在选区两侧插入包裹文本（加粗/斜体/行内代码等），无选区时插入骨架。</summary>
    public void ApplySnippet(string before, string after)
    {
        var area = TextArea;
        var sel = area.Selection;
        string selected = sel.GetText();
        if (string.IsNullOrEmpty(selected))
        {
            int caret = area.Caret.Offset;
            Document.Insert(caret, before + after);
            area.Caret.Offset = caret + before.Length;
        }
        else
        {
            int start = sel.Segments.Min(s => s.StartOffset);
            int end = sel.Segments.Max(s => s.EndOffset);
            Document.Replace(start, end - start, before + selected + after);
            area.Caret.Offset = start + before.Length + selected.Length;
        }
        Focus();
    }

    /// <summary>
    /// 插入文本到光标处（行首插入由调用方拼好换行）。
    /// 注意：AvalonEdit 在插入点等于光标位置时会把光标自动右移，因此必须先把插入点存下来再显式定位，
    /// 否则 <c>Caret.Offset += text.Length</c> 会二次右移并越界抛 ArgumentOutOfRangeException（点击速查项崩溃的根因）。
    /// </summary>
    public void InsertAtCaret(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        int caret = TextArea.Caret.Offset;
        Document.Insert(caret, text);
        TextArea.Caret.Offset = caret + text.Length;
        Focus();
    }
}
