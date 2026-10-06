using System.Windows.Controls;
using System.Windows.Input;
using GrepExplorer.UI.ViewModels;

namespace GrepExplorer.UI.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    /// <summary>双击记录 → 结果按 2.3 模板加载到 Markdown 编辑器（业务在 VM）。</summary>
    private void OnRecordDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel vm && vm.Selected != null)
            vm.OpenSelectedCommand.Execute(null);
    }
}
