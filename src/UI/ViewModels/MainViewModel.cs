using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Grep;
using GrepExplorer.UI.Services;

namespace GrepExplorer.UI.ViewModels;

public enum AppPage { Search, MarkdownEditor, RegexEditor, Groups, History }

/// <summary>主窗口 VM：工作目录（2.9）、页面导航（未保存守卫 2.10）、搜索元数据缓存。</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public MainViewModel(IDialogService dialogs, SearchViewModel search, MdEditorViewModel mdEditor,
        RegexEditorViewModel regexEditor, GroupsViewModel groups, HistoryViewModel history)
    {
        _dialogs = dialogs;
        Search = search;
        MdEditor = mdEditor;
        RegexEditor = regexEditor;
        Groups = groups;
        History = history;

        WorkingDirectory = Environment.CurrentDirectory; // 每次启动 = 进程启动目录，不恢复上次目录
        RecentDirectories.Add(WorkingDirectory);
        _suppressRecentSync = true;
        RecentSelected = WorkingDirectory;
        _suppressRecentSync = false;
        Search.SearchCompletedForEditor += (meta, result) =>
        {
            _lastSearchMetadata = meta;
            MdEditor.SetDocument(ResultDocumentBuilder.BuildResultDocument(meta, result), meta);
            CurrentPage = AppPage.MarkdownEditor;
        };
        Search.SetRegexEditorRequest(OpenRegexPickerAsync);
        History.OpenInEditorRequested += rec =>
        {
            var snapshot = HistorySnapshotProvider?.Invoke(rec) ?? "";
            // 历史表只存了命中文件数（没有扫描文件数），此处以命中数占位，避免元数据行显示 0
            var meta = new SearchMetadata(rec.CreatedAt, rec.Mode, rec.RootPath,
                SplitList(rec.GroupAliases), SplitPatterns(rec.Pattern), 0,
                rec.HitFiles, rec.HitFiles, rec.TotalHits, rec.CommandText);
            _lastSearchMetadata = meta;
            MdEditor.SetDocument(ResultDocumentBuilder.BuildResultDocument(meta, snapshot), meta);
            CurrentPage = AppPage.MarkdownEditor;
        };
        Groups.WorkingDirectory = WorkingDirectory;

        // 组管理页增删改组后，搜索页的勾选组列表 + 命令编辑器的组下拉随之刷新
        Groups.GroupsChanged += () => Search.ReloadGroupsCommand.Execute(null);
        Search.ReloadGroupsCommand.Execute(null); // 启动时载入一次（组页构造发生在订阅之前）
        // 搜索页"管理组…"入口 → 跳到组管理页
        Search.ManageGroupsRequested += () => TryNavigate(AppPage.Groups);
    }

    /// <summary>组别列表：新写入为换行分隔，兼容旧数据的逗号分隔。</summary>
    private static string[] SplitList(string text)
        => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];

    /// <summary>Pattern 列表：逗号可能是正则的一部分（如 a{2,3}），只在旧数据的 " | " 分隔上额外拆分。</summary>
    private static string[] SplitPatterns(string text)
    {
        var byLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return byLine.Length == 1 && byLine[0].Contains(" | ")
            ? [.. byLine[0].Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : byLine;
    }

    public Func<Core.History.HistoryRecord, string>? HistorySnapshotProvider { get; set; }

    private SearchMetadata? _lastSearchMetadata;

    public SearchViewModel Search { get; }
    public MdEditorViewModel MdEditor { get; }
    public RegexEditorViewModel RegexEditor { get; }
    public GroupsViewModel Groups { get; }
    public HistoryViewModel History { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageTitle))]
    [NotifyPropertyChangedFor(nameof(CurrentView))]
    [NotifyPropertyChangedFor(nameof(NavIndex))]
    private AppPage _currentPage = AppPage.Search;

    public string CurrentPageTitle => CurrentPage switch
    {
        AppPage.Search => "搜索",
        AppPage.MarkdownEditor => "Markdown 编辑器",
        AppPage.RegexEditor => "正则表达式编辑器",
        AppPage.Groups => "组管理",
        AppPage.History => "历史记录",
        _ => "",
    };

    /// <summary>当前页面的 VM：MainWindow 的 DataTemplate 按 VM 类型选择视图。</summary>
    public object CurrentView => CurrentPage switch
    {
        AppPage.MarkdownEditor => MdEditor,
        AppPage.RegexEditor => RegexEditor,
        AppPage.Groups => Groups,
        AppPage.History => History,
        _ => Search,
    };

    /// <summary>侧边导航选中项（取值与 <see cref="AppPage"/> 同序，0 = 搜索）。</summary>
    public int NavIndex
    {
        get => (int)CurrentPage;
        set
        {
            if (value < 0 || value > (int)AppPage.History) return;
            TryNavigate((AppPage)value); // 未保存守卫可能拒绝跳转
            OnPropertyChanged();         // 无论结果如何都回读，被拒绝时把选中项拉回当前页
        }
    }

    [ObservableProperty] private string _workingDirectory = "";
    public ObservableCollection<string> RecentDirectories { get; } = new();

    /// <summary>“最近目录”下拉的选中项；选中即切换工作目录。</summary>
    [ObservableProperty] private string? _recentSelected;
    private bool _suppressRecentSync;

    partial void OnRecentSelectedChanged(string? value)
    {
        if (_suppressRecentSync || string.IsNullOrEmpty(value)) return;
        SetWorkingDirectory(value);
    }

    [RelayCommand]
    private void SwitchWorkingDirectory()
    {
        var dir = _dialogs.PickFolder(WorkingDirectory);
        if (dir == null) return;
        SetWorkingDirectory(dir);
    }

    /// <summary>手输路径切换工作目录（顶栏“转到…”）。</summary>
    [RelayCommand]
    private void ManualDirectory()
    {
        var dir = _dialogs.PromptText("转到目录", "输入工作目录路径：", WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(dir)) SetWorkingDirectory(dir.Trim());
    }

    [RelayCommand]
    private void UseRecentDirectory(string? dir)
    {
        if (!string.IsNullOrEmpty(dir)) SetWorkingDirectory(dir);
    }

    public void SetWorkingDirectory(string dir)
    {
        if (!System.IO.Directory.Exists(dir))
        {
            _dialogs.Info("目录不存在", dir);
            return;
        }
        WorkingDirectory = dir;
        if (RecentDirectories[0] != dir)
        {
            RecentDirectories.Remove(dir);
            RecentDirectories.Insert(0, dir);
            while (RecentDirectories.Count > 10) // 最近 10 个，会话内记忆
                RecentDirectories.RemoveAt(RecentDirectories.Count - 1);
        }
        Search.OnWorkingDirectoryChanged(dir);
        History.CurrentRoot = dir;
        Groups.WorkingDirectory = dir;
        _suppressRecentSync = true;
        RecentSelected = dir; // 下拉始终反映当前工作目录
        _suppressRecentSync = false;
    }

    /// <summary>正则编辑器"确认后回填为 Pattern 块"（2.7）。</summary>
    public async Task<IReadOnlyList<string>?> OpenRegexPickerAsync(IReadOnlyList<string> initial)
    {
        RegexEditor.BeginPick(initial);
        CurrentPage = AppPage.RegexEditor;
        var result = await RegexEditor.PickCompletion;
        if (result != null) CurrentPage = AppPage.Search;
        return result;
    }

    // ---------- 未保存守卫（2.10） ----------

    private bool RunUnsavedGuard()
    {
        if (!MdEditor.IsDirty) return true;
        var baseline = MdEditor.HasBaseline ? MdEditor.Baseline : null;
        var result = _dialogs.ShowUnsavedChanges(baseline, MdEditor.Text,
            path => MdEditor.SaveToFile(path));
        switch (result)
        {
            case UnsavedResult.SavedToFile:
            case UnsavedResult.Discard:
                if (result == UnsavedResult.Discard) MdEditor.RevertToBaseline();
                return true;
            default:
                return false;
        }
    }

    public bool TryNavigate(AppPage page)
    {
        if (page == CurrentPage) return true;
        if (page == AppPage.History && !RunUnsavedGuard()) return false; // 切换到历史视图触发
        CurrentPage = page;
        if (page == AppPage.History) History.Refresh();
        if (page == AppPage.Groups) Groups.Refresh(); // 进入组管理页时重读（外部改动/导入后一致）
        return true;
    }

    public bool ConfirmBeforeSearch() => RunUnsavedGuard(); // 确认执行新搜索前触发

    public bool ConfirmBeforeClose() => RunUnsavedGuard();  // 关闭程序触发
}
