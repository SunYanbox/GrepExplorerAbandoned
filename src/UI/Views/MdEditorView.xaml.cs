using System.Windows.Input;
using System.Windows;
using GrepExplorer.UI.Controls;
using GrepExplorer.UI.ViewModels;

namespace GrepExplorer.UI.Views;

/// <summary>Markdown 编辑器视图（Ctrl+S 保存；桥注入；业务在 VM）。</summary>
public partial class MdEditorView
{
    public MdEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Wire();
        Loaded += (_, _) => Wire();
    }

    private void Wire()
    {
        if (DataContext is MdEditorViewModel vm && vm.Bridge == null)
        {
            vm.Bridge = new MarkdownEditorBridge(Editor);
            Editor.TextArea.TextView.LineTransformers.Add(new MarkdownHighlightingTransformer());
        }
    }
}
