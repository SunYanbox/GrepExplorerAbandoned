using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.History;
using GrepExplorer.UI.Services;

namespace GrepExplorer.UI.ViewModels;

/// <summary>历史列表行（包装 + 勾选）。</summary>
public partial class HistoryItemViewModel : ObservableObject
{
    public HistoryRecord Record { get; }
    public string CreatedAtText => Record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string SnapshotSizeText => $"{Record.SnapshotBytes} B";
    public string RootPath => Record.RootPath;
    public string Mode => Record.Mode;
    public string GroupAliases => Record.GroupAliases;
    public string Pattern => Record.Pattern;
    public int HitFiles => Record.HitFiles;
    public int TotalHits => Record.TotalHits;

    [ObservableProperty] private bool _isCheckedForDelete;

    public HistoryItemViewModel(HistoryRecord record) => Record = record;
}

/// <summary>历史记录页（2.6）：筛选、统计、六种清理、单条导出、双击加载到编辑器。</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly HistoryRepository _repo;

    public HistoryViewModel(IDialogService dialogs, HistoryRepository repo)
    {
        _dialogs = dialogs;
        _repo = repo;
    }

    public event Action<HistoryRecord>? OpenInEditorRequested;

    [ObservableProperty] private string _currentRoot = Environment.CurrentDirectory;
    [ObservableProperty] private string _modeFilter = "全部";
    [ObservableProperty] private string? _rootFilter;
    [ObservableProperty] private string? _groupFilter;
    [ObservableProperty] private string _cleanupDaysText = "30";
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private HistoryItemViewModel? _selected;

    public ObservableCollection<HistoryItemViewModel> Records { get; } = new();
    public ObservableCollection<string> RootOptions { get; } = new();
    public ObservableCollection<string> GroupOptions { get; } = new();

    // 范围（全局/当前工作目录，归属 = 记录根目录字段）
    public int ScopeIndex
    {
        get => _scopeIndex;
        set { _scopeIndex = value; OnPropertyChanged(); Refresh(); }
    }
    private int _scopeIndex; // 0=全局 1=当前项目

    public int ModeFilterIndex
    {
        get => _modeFilterIndex;
        set { _modeFilterIndex = value; OnPropertyChanged(); Refresh(); }
    }
    private int _modeFilterIndex; // 0=全部 1=Glob 2=Grep

    private HistoryScope Scope => _scopeIndex == 1 ? HistoryScope.CurrentProject : HistoryScope.Global;

    [RelayCommand]
    public void Refresh()
    {
        Records.Clear();
        string? mode = _modeFilterIndex switch { 1 => "Glob", 2 => "Grep", _ => null };
        foreach (var r in _repo.Query(Scope, CurrentRoot, mode, RootFilter, GroupFilter))
            Records.Add(new HistoryItemViewModel(r));
        RootOptions.Clear();
        RootOptions.Add("");
        foreach (var root in _repo.GetDistinctRoots()) RootOptions.Add(root);
        GroupOptions.Clear();
        GroupOptions.Add("");
        foreach (var g in CollectGroupAliases()) GroupOptions.Add(g);
        var stats = _repo.GetStats(Scope, CurrentRoot);
        StatsText = $"共 {stats.Count} 条 · 占用磁盘 {FormatBytes(stats.TotalBytes)}（SUM(SnapshotBytes)）";
    }

    private IEnumerable<string> CollectGroupAliases()
        => Records.SelectMany(r => r.Record.GroupAliases.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Distinct().Order();

    private static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024.0:F1} MB",
        _ => $"{b / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };

    // ---------- 六种清理入口（二次确认 + 预估 + 事务内执行） ----------

    private bool ConfirmCleanup(CleanupMode mode, string title, int? days = null)
    {
        int estimate = _repo.EstimateCleanup(mode, CurrentRoot, days, IdsOfChecked());
        if (estimate == 0)
        {
            _dialogs.Info(title, "没有符合条件的记录。");
            return false;
        }
        return _dialogs.Confirm(title, $"预计影响 {estimate} 条记录（含外置快照文件），且不可恢复。确定执行？");
    }

    private List<long> IdsOfChecked()
        => Records.Where(r => r.IsCheckedForDelete).Select(r => r.Record.Id).ToList();

    [RelayCommand] private void CleanupAll()
    {
        if (!ConfirmCleanup(CleanupMode.All, "清理全部")) return;
        _dialogs.Info("清理全部", $"已删除 {_repo.Cleanup(CleanupMode.All, CurrentRoot)} 条。");
        Refresh();
    }

    [RelayCommand] private void CleanupProject()
    {
        if (!ConfirmCleanup(CleanupMode.CurrentProjectAll, "清理当前项目全部")) return;
        _dialogs.Info("清理当前项目全部", $"已删除 {_repo.Cleanup(CleanupMode.CurrentProjectAll, CurrentRoot)} 条。");
        Refresh();
    }

    [RelayCommand] private void CleanupGlobalOld()
    {
        int days = ParseDays();
        if (days < 0) return;
        if (!ConfirmCleanup(CleanupMode.GlobalOlderThanDays, "清理全局旧记录", days)) return;
        _dialogs.Info("清理全局旧记录", $"已删除 {_repo.Cleanup(CleanupMode.GlobalOlderThanDays, CurrentRoot, days)} 条。");
        Refresh();
    }

    [RelayCommand] private void CleanupProjectOld()
    {
        int days = ParseDays();
        if (days < 0) return;
        if (!ConfirmCleanup(CleanupMode.ProjectOlderThanDays, "清理本项目旧记录", days)) return;
        _dialogs.Info("清理本项目旧记录", $"已删除 {_repo.Cleanup(CleanupMode.ProjectOlderThanDays, CurrentRoot, days)} 条。");
        Refresh();
    }

    [RelayCommand] private void CleanupSelected()
    {
        var ids = IdsOfChecked();
        if (ids.Count == 0)
        {
            _dialogs.Info("删除勾选记录", "请先勾选要删除的记录。");
            return;
        }
        int estimate = _repo.EstimateCleanup(CleanupMode.SelectedIds, CurrentRoot, null, ids);
        if (!_dialogs.Confirm("删除勾选记录", $"预计影响 {estimate} 条记录，且不可恢复。确定执行？")) return;
        _dialogs.Info("删除勾选记录", $"已删除 {_repo.Cleanup(CleanupMode.SelectedIds, CurrentRoot, null, ids)} 条。");
        Refresh();
    }

    [RelayCommand] private void CleanupDuplicates()
    {
        // 重复项：键 = 工作目录 + 规范化命令文本，只留最新；执行前预览将删除条数
        int estimate = _repo.EstimateDuplicateCleanup();
        if (estimate == 0)
        {
            _dialogs.Info("清理重复项", "没有重复项。");
            return;
        }
        if (!_dialogs.Confirm("清理重复项", $"发现 {estimate} 条重复记录（只保留每组最新一条），将删除其余。确定执行？")) return;
        _dialogs.Info("清理重复项", $"已删除 {_repo.Cleanup(CleanupMode.Duplicates, CurrentRoot)} 条。");
        Refresh();
    }

    private int ParseDays()
    {
        if (!int.TryParse(CleanupDaysText.Trim(), out int d) || d < 0)
        {
            _dialogs.Info("天数无效", "请输入非负整数天数。");
            return -1;
        }
        return d;
    }

    // ---------- 打开 / 导出 ----------

    [RelayCommand]
    public void OpenSelected()
    {
        if (Selected == null) return;
        OpenInEditorRequested?.Invoke(Selected.Record);
    }

    [RelayCommand]
    private void ExportSelected()
    {
        if (Selected == null) return;
        var record = Selected.Record;
        string? snapshot = _repo.ReadSnapshot(record);
        // 2.11：front matter 单一实现；元数据取自记录（execute_datetime = 原搜索时间）
        var meta = new Core.Grep.SearchMetadata(
            record.CreatedAt, record.Mode, record.RootPath,
            record.GroupAliases.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            record.Pattern.Split('\n', StringSplitOptions.RemoveEmptyEntries), 0,
            record.HitFiles, record.HitFiles, record.TotalHits, record.CommandText);
        var doc = Core.Grep.ResultDocumentBuilder.BuildExportDocument(meta, snapshot ?? "");
        var path = _dialogs.PickSaveFile($"history-{record.Id}.md", "Markdown (*.md)|*.md");
        if (path == null) return;
        File.WriteAllText(path, doc);
        StatusMessage = $"已导出：{path}";
    }

    [RelayCommand]
    private void CopySelectedSnapshot()
    {
        if (Selected == null) return;
        var snapshot = _repo.ReadSnapshot(Selected.Record);
        if (!string.IsNullOrEmpty(snapshot))
            System.Windows.Clipboard.SetText(snapshot);
    }
}
