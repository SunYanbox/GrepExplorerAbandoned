using System.Windows;
using System.Windows.Controls;

namespace GrepExplorer.UI.Controls;

/// <summary>
/// 多行元素列表通用控件（2.8）：头部 复制全部（每行一条）/ 导出（.txt）+ 行数。
/// </summary>
public partial class MultiLineListControl : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(MultiLineListControl), new PropertyMetadata(""));

    public static readonly DependencyProperty ListTextProperty = DependencyProperty.Register(
        nameof(ListText), typeof(string), typeof(MultiLineListControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (_, _) => { }, (_, value) => value ?? ""));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string ListText
    {
        get => (string)GetValue(ListTextProperty);
        set => SetValue(ListTextProperty, value);
    }

    private int LineCount => ListText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    public MultiLineListControl()
    {
        InitializeComponent();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
        => CountText.Text = $"{LineCount} 条";

    private void CopyAll(object sender, RoutedEventArgs e)
    {
        var lines = Items.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 0) Clipboard.SetText(string.Join('\n', lines));
    }

    private void ExportAll(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "list.txt", Filter = "文本文件 (*.txt)|*.txt", AddExtension = true };
        if (dlg.ShowDialog() != true) return;
        System.IO.File.WriteAllText(dlg.FileName, Items.Text);
    }
}
