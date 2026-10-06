using System.Windows.Controls;

namespace GrepExplorer.UI.Views;

/// <summary>命令编辑器子视图（SearchView 内嵌 Tab）。业务逻辑全部在 CommandEditorViewModel。</summary>
public partial class CommandEditorView : UserControl
{
    public CommandEditorView()
    {
        InitializeComponent();
    }
}
