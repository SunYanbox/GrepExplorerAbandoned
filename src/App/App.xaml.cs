using System.Windows;
using GrepExplorer.UI.Services;
using GrepExplorer.UI.ViewModels;
using Infrastructure;

namespace GrepExplorer.App;

/// <summary>启动、DI 装配、Fluent 主题。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 数据目录：%LOCALAPPDATA%\GrepExplorer（多实例共享 WAL 数据库）
        string dataDir = AppConfig.DefaultDirectory;
        var config = AppConfig.Load(dataDir);
        var connFactory = new SqliteConnectionFactory(dataDir);
        var groupsRepo = new Core.Groups.GroupRepository(connFactory);
        var historyRepo = new Core.History.HistoryRepository(connFactory, config.HistoryLimit);
        var dialogs = new DialogService();

        // 内置组：首次启动种子写入（幂等）
        groupsRepo.SeedBuiltInsOnStartup();

        // 手工装配（避免 Main ↔ Search 的构造循环）
        MainViewModel main = null!;
        var search = new SearchViewModel(dialogs, async () => await Task.Run(() => main.ConfirmBeforeSearch()), historyRepo, groupsRepo, config);
        var md = new MdEditorViewModel(dialogs);
        var regex = new RegexEditorViewModel(dialogs);
        var groups = new GroupsViewModel(dialogs, groupsRepo);
        var history = new HistoryViewModel(dialogs, historyRepo);
        main = new MainViewModel(dialogs, search, md, regex, groups, history);
        main.HistorySnapshotProvider = rec => historyRepo.ReadSnapshot(rec) ?? "";

        // 命令编辑器 → 正则编辑器（拾取模式回填 Pattern）
        search.SetRegexEditorRequest(initial => main.OpenRegexPickerAsync(initial));

        history.CurrentRoot = main.WorkingDirectory;

        var window = new UI.MainWindow(main);
        MainWindow = window;
        window.Show();
    }
}
