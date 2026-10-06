using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Globbing;
using Core.Groups;
using GrepExplorer.UI.Services;

namespace GrepExplorer.UI.ViewModels;

/// <summary>组管理页左侧列表项。</summary>
public sealed class GroupItemViewModel
{
    public GroupModel Model { get; }
    public string Alias => Model.Alias;
    public string Icon => Model.IsBuiltIn ? "🛡" : "📁"; // 内置组与用户组图标区分
    public string Display => $"{Icon} {Model.Alias}";
    public GroupItemViewModel(GroupModel model) => Model = model;
}

/// <summary>组管理（2.5）：CRUD、预览、复制、删除确认、JSON 导入导出、恢复内置组。</summary>
public partial class GroupsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly GroupRepository _repo;

    public GroupsViewModel(IDialogService dialogs, GroupRepository repo)
    {
        _dialogs = dialogs;
        _repo = repo;
        Load();
    }

    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    public ObservableCollection<GroupItemViewModel> Groups { get; } = new();

    [ObservableProperty] private GroupItemViewModel? _selected;

    // 编辑缓冲
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _includeText = "";
    [ObservableProperty] private string _excludeText = "";
    [ObservableProperty] private string _excludeFromText = "";
    [ObservableProperty] private string _excludeDirText = "";
    [ObservableProperty] private string _remark = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _previewStats = "";
    [ObservableProperty] private string _previewIncluded = "";
    [ObservableProperty] private string _previewExcluded = "";
    [ObservableProperty] private bool _isBuiltInSelected;

    /// <summary>组列表发生增删改后触发（搜索页勾选组随之刷新）。</summary>
    public event Action? GroupsChanged;

    partial void OnSelectedChanged(GroupItemViewModel? value) => LoadEditor();

    private void LoadEditor()
    {
        var m = Selected?.Model;
        Alias = m?.Alias ?? "";
        IncludeText = string.Join("\n", m?.Include ?? []);
        ExcludeText = string.Join("\n", m?.Exclude ?? []);
        ExcludeFromText = string.Join("\n", m?.ExcludeFrom ?? []);
        ExcludeDirText = string.Join("\n", m?.ExcludeDir ?? []);
        Remark = m?.Remark ?? "";
        IsBuiltInSelected = m?.IsBuiltIn ?? false;
    }

    private void Load()
    {
        var selected = Selected?.Model.Id;
        Groups.Clear();
        foreach (var g in _repo.GetAll().OrderBy(g => g.IsBuiltIn == false).ThenBy(g => g.Alias))
            Groups.Add(new GroupItemViewModel(g));
        Selected = Groups.FirstOrDefault(g => g.Model.Id == selected) ?? Groups.FirstOrDefault();
        GroupsChanged?.Invoke();
    }

    /// <summary>重新读取组列表（供外部在切换到组管理页时调用）。</summary>
    public void Refresh() => Load();

    private static List<string> Lines(string text)
        => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    [RelayCommand]
    private void New()
    {
        Selected = null;
        LoadEditor();
        Alias = "";
        StatusMessage = "新建组：填写别名后保存";
    }

    [RelayCommand]
    private void Save()
    {
        Alias = Alias.Trim();
        if (Alias.Length == 0)
        {
            _dialogs.Info("校验失败", "别名不能为空");
            return;
        }
        if (Alias.Length > 32)
        {
            _dialogs.Info("校验失败", "别名不能超过 32 字符");
            return;
        }
        var existing = _repo.GetAll();
        if (existing.Any(g => g.Alias == Alias && g.Id != (Selected?.Model.Id ?? Guid.Empty)))
        {
            _dialogs.Info("校验失败", $"别名 \"{Alias}\" 已存在（须唯一）");
            return;
        }

        var model = Selected?.Model ?? new GroupModel();
        model.Alias = Alias;
        model.Include = Lines(IncludeText);
        model.Exclude = Lines(ExcludeText);
        model.ExcludeFrom = Lines(ExcludeFromText);
        model.ExcludeDir = Lines(ExcludeDirText);
        model.Remark = Remark;
        model.UpdatedAt = DateTime.Now;
        if (Selected == null) model.CreatedAt = DateTime.Now;
        _repo.Upsert(model);
        StatusMessage = $"已保存：{Alias}";
        Load();
    }

    [RelayCommand]
    private void Copy()
    {
        if (Selected == null) return;
        var clone = JsonSerializer.Deserialize<GroupModel>(JsonSerializer.Serialize(Selected.Model))!;
        clone.Id = Guid.NewGuid();
        clone.IsBuiltIn = false;
        clone.Alias = MakeUniqueAlias(clone.Alias + " - 副本");
        clone.CreatedAt = clone.UpdatedAt = DateTime.Now;
        _repo.Upsert(clone);
        Load();
        Selected = Groups.First(g => g.Model.Id == clone.Id);
    }

    private string MakeUniqueAlias(string baseName)
    {
        var existing = _repo.GetAll().Select(g => g.Alias).ToHashSet();
        if (existing.Add(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (existing.Add($"{baseName} {i}")) return $"{baseName} {i}";
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected == null) return;
        if (!_dialogs.Confirm("删除组", $"确定删除组 \"{Selected.Alias}\" 吗？此操作不可撤销。"))
            return;
        _repo.Delete(Selected.Model.Id);
        StatusMessage = "已删除";
        Load();
    }

    [RelayCommand]
    private void ExportJson()
    {
        var path = _dialogs.PickSaveFile("groups.json", "JSON (*.json)|*.json");
        if (path == null) return;
        File.WriteAllText(path, GroupRepository.ExportJson(_repo.GetAll()));
        StatusMessage = $"已导出：{path}";
    }

    [RelayCommand]
    private void ImportJson()
    {
        var path = _dialogs.PickOpenFile("JSON (*.json)|*.json");
        if (path == null) return;
        try
        {
            foreach (var g in GroupRepository.ImportJson(File.ReadAllText(path)))
            {
                var dup = _repo.GetAll().FirstOrDefault(x => x.Alias == g.Alias);
                if (dup != null) g.Alias = MakeUniqueAlias(g.Alias); // 导入去重别名
                _repo.Upsert(g);
            }
            Load();
            StatusMessage = "JSON 导入完成";
        }
        catch (Exception ex)
        {
            _dialogs.Info("导入失败", ex.Message);
        }
    }

    [RelayCommand]
    private void RestoreBuiltIns()
    {
        _repo.RestoreBuiltIns(); // 仅补插缺失项，已存在跳过
        Load();
        StatusMessage = "内置组已恢复（缺失项已补插）";
    }

    /// <summary>预览：按**当前编辑区**（未保存也生效）的规则，列出工作目录下将被包含/排除的文件与目录。</summary>
    [RelayCommand]
    private void Preview()
    {
        var rules = GlobRuleSet.Create(Lines(IncludeText), Lines(ExcludeText),
            ReadExcludeFrom(Lines(ExcludeFromText)), Lines(ExcludeDirText));

        var included = new List<string>();
        var excluded = new List<string>();
        int incTotal = 0, excTotal = 0;
        Walk(WorkingDirectory, "", rules, included, excluded, ref incTotal, ref excTotal);

        PreviewStats = $"工作目录：{WorkingDirectory}　将被包含 {incTotal} 项 / 将被排除 {excTotal} 项（各最多显示 500 条）";
        PreviewIncluded = included.Count == 0 ? "（无）" : string.Join("\n", included.Take(500));
        PreviewExcluded = excluded.Count == 0 ? "（无）" : string.Join("\n", excluded.Take(500));
    }

    private static string[]? ReadExcludeFrom(List<string> files)
    {
        var lines = new List<string>();
        foreach (var f in files)
            if (File.Exists(f)) lines.AddRange(File.ReadAllLines(f));
        return [.. lines];
    }

    private static void Walk(string dirFull, string rel, GlobRuleSet rules,
        List<string> included, List<string> excluded, ref int incTotal, ref int excTotal)
    {
        if (incTotal > 5000 && excTotal > 5000) return; // 预览上限保护
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dirFull).EnumerateFileSystemInfos(); }
        catch { return; }

        foreach (var e in entries)
        {
            if (e.Name.StartsWith('.')) continue; // 隐藏文件/目录
            bool isDir = e is DirectoryInfo;
            var r = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
            if (isDir)
            {
                if (rules.IsExcluded(r, true)) continue; // 排除目录整棵剪除
                if (rules.IsIncluded(r, true)) { incTotal++; if (included.Count < 500) included.Add(e.FullName); }
                else { excTotal++; if (excluded.Count < 500) excluded.Add(e.FullName); }
                Walk(e.FullName, r, rules, included, excluded, ref incTotal, ref excTotal);
            }
            else
            {
                if (rules.IsExcluded(r, false)) { excTotal++; if (excluded.Count < 500) excluded.Add(e.FullName); }
                else if (rules.IsIncluded(r, false)) { incTotal++; if (included.Count < 500) included.Add(e.FullName); }
                else { excTotal++; if (excluded.Count < 500) excluded.Add(e.FullName); }
            }
        }
    }
}
