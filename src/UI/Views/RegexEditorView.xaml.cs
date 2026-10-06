using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GrepExplorer.UI.Controls;
using GrepExplorer.UI.ViewModels;

namespace GrepExplorer.UI.Views;

/// <summary>正则编辑器视图（着色 / 速查插入 / 测试区高亮；业务在 VM）。</summary>
public partial class RegexEditorView : UserControl
{
    public RegexEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Wire();
        Loaded += (_, _) => Wire();
    }

    private void Wire()
    {
        if (DataContext is RegexEditorViewModel vm && vm.Bridge == null)
        {
            var transformer = new RegexHighlightingTransformer(PatternEditor);
            var matchTransformer = new RegexMatchHighlightTransformer(TestEditor);
            vm.Bridge = new RegexBridge(PatternEditor, matchTransformer);
            PatternEditor.TextArea.TextView.LineTransformers.Add(transformer);
            TestEditor.TextArea.TextView.LineTransformers.Add(matchTransformer);
        }
    }

    /// <summary>速查面板单击 → 插入到光标处（只走 VM 一条路径，避免重复插入）。</summary>
    private void OnQuickItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RegexQuickItem item }
            && DataContext is RegexEditorViewModel vm)
        {
            vm.OnQuickItemClicked(item);
        }
    }
}
