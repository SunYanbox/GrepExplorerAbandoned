using System.ComponentModel;
using System.Windows;
using GrepExplorer.UI.ViewModels;

namespace GrepExplorer.UI;

/// <summary>主窗口。业务逻辑全部在 MainViewModel（关闭守卫 2.10）。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>关闭程序前检查未保存更改（保存到文件 / 放弃 / 取消）。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmBeforeClose())
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
