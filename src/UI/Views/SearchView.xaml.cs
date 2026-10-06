using System.Windows;
using System.Windows.Controls;
using GrepExplorer.UI.ViewModels;

namespace GrepExplorer.UI.Views;

/// <summary>搜索视图（Glob / Grep 两个模式标签，每个模式默认进入命令编辑器 GUI，表单为次选）。</summary>
public partial class SearchView : UserControl
{
    private SearchViewModel? _subscribed;

    public SearchView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed == null) return;
        _subscribed.CommandExecuted -= OnCommandExecuted;
        _subscribed = null;
    }

    private void OnModeTabsLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SearchViewModel vm && !ReferenceEquals(vm, _subscribed))
        {
            if (_subscribed != null) _subscribed.CommandExecuted -= OnCommandExecuted;
            _subscribed = vm;
            vm.CommandExecuted += OnCommandExecuted;
        }
        SyncModeWithTabs();
    }

    /// <summary>切换模式标签页时，把命令编辑器切到对应模式（Grep / Glob）。</summary>
    private void OnModeTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged 是冒泡事件：内层 TabControl 的变化也会传到这里，只处理外层的
        if (!ReferenceEquals(e.OriginalSource, ModeTabs)) return;
        SyncModeWithTabs();
    }

    private void SyncModeWithTabs()
    {
        if (DataContext is not SearchViewModel vm) return;
        if (ModeTabs.SelectedIndex == 0) vm.CommandEditor.IsGlobModeChecked = true;
        else if (ModeTabs.SelectedIndex == 1) vm.CommandEditor.IsGrepModeChecked = true;
    }

    /// <summary>命令编辑器执行完成后，切到该模式的「表单」标签查看命中结果。</summary>
    private void OnCommandExecuted()
    {
        if (DataContext is not SearchViewModel vm) return;
        if (vm.CommandEditor.IsGlobMode)
        {
            ModeTabs.SelectedIndex = 0;
            GlobTabs.SelectedIndex = 1;
        }
        else
        {
            ModeTabs.SelectedIndex = 1;
            GrepTabs.SelectedIndex = 1;
        }
    }
}
