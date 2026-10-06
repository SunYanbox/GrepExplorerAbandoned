using System.Windows;
using Core.Diff;
using GrepExplorer.UI.Services;

namespace GrepExplorer.UI.Dialogs;

/// <summary>未保存更改对话框（2.10）：保存到文件 / 放弃更改 / 取消操作；内嵌 Inline Diff。</summary>
public partial class UnsavedChangesWindow : Window
{
    public UnsavedResult Result { get; private set; } = UnsavedResult.Cancel;
    private readonly Func<string?, string?> _saveToFile;

    public UnsavedChangesWindow(string? baseline, string current, Func<string?, string?> saveToFile)
    {
        InitializeComponent();
        _saveToFile = saveToFile;

        // 存在非空基准时内嵌 VSCode Inline 风格 Diff
        if (!string.IsNullOrEmpty(baseline))
        {
            Diff.Visibility = Visibility.Visible;
            Diff.Show(InlineDiffService.Compute(baseline, current));
            MessageText.Text = "当前 Markdown 编辑器存在未保存的更改（下方为与基准的 Inline Diff）。";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string? path = _saveToFile(null);
        if (path == null) return; // 用户取消保存 → 回到对话框
        Result = UnsavedResult.SavedToFile;
        DialogResult = true;
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        Result = UnsavedResult.Discard;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = UnsavedResult.Cancel;
        DialogResult = false;
    }
}
