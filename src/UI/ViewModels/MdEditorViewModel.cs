using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Grep;
using GrepExplorer.UI.Controls;
using GrepExplorer.UI.Services;

namespace GrepExplorer.UI.ViewModels;

/// <summary>Markdown 编辑器（2.3）：快捷插入、Ctrl+S、脏数据、front matter 导出。</summary>
public partial class MdEditorViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public MdEditorViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
    }

    /// <summary>编辑器桥（View 加载时注入；机械操作由控件执行，业务在 VM）。</summary>
    public IEditorBridge? Bridge { get; set; }

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _baseline = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _title = "未命名.md";
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>来源元数据（搜索结果/历史时非空；导出时携带 front matter）。</summary>
    public SearchMetadata? SourceMetadata { get; private set; }

    public bool HasBaseline => Baseline.Length > 0;

    partial void OnTextChanged(string value) => IsDirty = value != Baseline;

    /// <summary>
    /// 载入来源文档（搜索结果 / 历史快照）。载入即以内容自身为基准：
    /// 用户没动过内容时不应提示"存在未保存的更改"（旧实现把空串/原始快照当基准，导致刚载入就是脏的）。
    /// </summary>
    public void SetDocument(string text, SearchMetadata? meta)
    {
        Baseline = text;   // 先写基准，Text 赋值触发的 OnTextChanged 会据此算出"干净"
        Text = text;
        SourceMetadata = meta;
        Title = meta != null ? $"搜索结果 {meta.ExecuteDateTime:yyyyMMdd-HHmmss}.md" : "未命名.md";
        IsDirty = false;
    }

    public void RevertToBaseline()
    {
        Text = Baseline;
        IsDirty = false;
    }

    public void MarkClean()
    {
        Baseline = Text;
        IsDirty = false;
    }

    /// <summary>保存到指定路径（供未保存对话框复用）；返回实际路径，取消返回 null。</summary>
    public string? SaveToFile(string? path)
    {
        path ??= _dialogs.PickSaveFile(Title, "Markdown (*.md)|*.md");
        if (path == null) return null;
        var content = ResultDocumentBuilder.BuildExportDocument(SourceMetadata, Text);
        System.IO.File.WriteAllText(path, content);
        StatusMessage = $"已保存：{path}";
        MarkClean();
        return path;
    }

    [RelayCommand]
    private void Save()
    {
        var saved = SaveToFile(null);
        if (saved == null && SourceMetadata != null)
            _dialogs.Info("提示", "未选择保存路径；文档保持未保存状态。");
    }

    // ---------- 快捷插入（2.3） ----------

    [RelayCommand] private void InsertH1() => Bridge?.InsertAtLineStart("# ");
    [RelayCommand] private void InsertH2() => Bridge?.InsertAtLineStart("## ");
    [RelayCommand] private void InsertH3() => Bridge?.InsertAtLineStart("### ");
    [RelayCommand] private void InsertBold() => Bridge?.WrapSelection("**", "**");
    [RelayCommand] private void InsertItalic() => Bridge?.WrapSelection("*", "*");
    [RelayCommand] private void InsertInlineCode() => Bridge?.WrapSelection("`", "`");
    [RelayCommand] private void InsertCodeBlock() => Bridge?.WrapSelection("```text\n", "\n```");
    [RelayCommand] private void InsertOrderedList() => Bridge?.InsertAtLineStart("1. ");
    [RelayCommand] private void InsertUnorderedList() => Bridge?.InsertAtLineStart("- ");
    [RelayCommand] private void InsertQuote() => Bridge?.InsertAtLineStart("> ");
    [RelayCommand] private void InsertLink() => Bridge?.WrapSelection("[", "](https://)");
    [RelayCommand] private void InsertTable()
        => Bridge?.InsertAtCaret("| 列1 | 列2 | 列3 |\n| --- | --- | --- |\n|  |  |  |\n");
    [RelayCommand] private void InsertImage()
        => Bridge?.InsertAtCaret("![图片描述](path/to/image.png)");
}

/// <summary>编辑器机械操作桥（由 CodeEditor 实现）。</summary>
public interface IEditorBridge
{
    void InsertAtCaret(string text);
    void WrapSelection(string before, string after);
    void InsertAtLineStart(string prefix);
}
