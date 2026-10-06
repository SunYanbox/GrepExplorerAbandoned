using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Globbing;
using Core.Groups;
using Core.Grep;
using Core.Grep.CommandParsing;
using CommandParsing = Core.Grep.CommandParsing;
using Core.History;
using GrepExplorer.UI.Services;

namespace GrepExplorer.UI.ViewModels;

/// <summary>搜索页：Glob 模式 / Grep 模式，流式执行、取消、计数、历史落库。</summary>
public partial class SearchViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly Func<Task<bool>> _unsavedGuard;
    private readonly HistoryRepository _history;
    private readonly GroupRepository _groups;
    private CancellationTokenSource? _cts;
    private double _largeFileThresholdBytes = 10 * 1024 * 1024;

    public SearchViewModel(IDialogService dialogs, Func<Task<bool>> unsavedGuard, HistoryRepository history, GroupRepository groups, Infrastructure.AppConfig config)
    {
        _dialogs = dialogs;
        _unsavedGuard = unsavedGuard;
        _history = history;
        _groups = groups;
        _largeFileThresholdBytes = config.LargeFileThresholdMB * 1024 * 1024;
        CommandEditor = new CommandEditorViewModel(dialogs);
        CommandEditor.ExecuteRequested += spec => _ = ExecuteFromCommandEditorAsync(spec);
        CommandEditor.Toast += msg => Toast = msg;
        CommandEditor.RegexEditorRequested += patterns => _regexEditorRequest?.Invoke(patterns);
        Context.Toast += msg => Toast = msg;
        RootDir = Environment.CurrentDirectory;
    }

    private Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>>? _regexEditorRequest;
    public void SetRegexEditorRequest(Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>> r) => _regexEditorRequest = r;

    public event Action<SearchMetadata, string>? SearchCompletedForEditor;

    public CommandEditorViewModel CommandEditor { get; }

    public string RootDir { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";

    // ---------- Glob 页 ----------
    [ObservableProperty] private string _globRoot = "";
    [ObservableProperty] private string _globExpressions = "";
    [ObservableProperty] private string _globIncludes = "";
    [ObservableProperty] private string _globExcludes = "";
    [ObservableProperty] private string _globExcludeFroms = "";
    [ObservableProperty] private string _globExcludeDirs = "";
    [ObservableProperty] private string _globResultText = "";
    [ObservableProperty] private int _globMatchCount;

    // ---------- Grep 页 ----------
    [ObservableProperty] private string _grepRoot = "";
    [ObservableProperty] private string _patternsText = "";
    [ObservableProperty] private string _patternFilesText = "";
    [ObservableProperty] private string _grepIncludes = "";
    [ObservableProperty] private string _grepExcludes = "";
    [ObservableProperty] private string _grepExcludeFroms = "";
    [ObservableProperty] private string _grepExcludeDirs = "";
    [ObservableProperty] private GrepOutputMode _outputMode = GrepOutputMode.LineDetails;
    [ObservableProperty] private bool _ignoreCase;
    [ObservableProperty] private bool _invertMatch;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _wholeLine;
    [ObservableProperty] private string _maxCountText = "";
    [ObservableProperty] private bool _byteOffset;
    [ObservableProperty] private bool _onlyMatching;
    [ObservableProperty] private bool _suppressErrors;
    [ObservableProperty] private bool _showEol;
    [ObservableProperty] private bool _binaryAsText;
    [ObservableProperty] private bool _followSymlinks;
    [ObservableProperty] private bool _includeHidden;
    [ObservableProperty] private bool _readGitignore;
    [ObservableProperty] private string _groupSeparatorText = "";
    public ContextFieldsViewModel Context { get; } = new();

    // ---------- 运行状态 ----------
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _scannedCount;
    [ObservableProperty] private int _hitFileCount;
    [ObservableProperty] private int _hitLineCount;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _toast = "";
    [ObservableProperty] private bool _hasSkippedFiles;
    [ObservableProperty] private string _skippedSummary = "";

    /// <summary>搜索页勾选组（复选框列表）。</summary>
    public ObservableCollection<GroupCheckViewModel> GroupChecks { get; } = new();
    public IEnumerable<string> SelectedGroups => GroupChecks.Where(c => c.IsChecked).Select(c => c.Alias);

    private List<Core.Groups.GroupModel> GetSelectedGroupModels()
        => _groups.GetAll().Where(g => SelectedGroups.Contains(g.Alias)).ToList();

    /// <summary>勾选组规则合并（include OR / exclude OR / 一票否决）。</summary>
    public (List<string> Inc, List<string> Exc, List<string> Exf, List<string> Exd) MergedRules
        => GroupRepository.Merge(GetSelectedGroupModels());

    partial void OnToastChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _ = ClearToastLaterAsync();
    }

    private async Task ClearToastLaterAsync()
    {
        await Task.Delay(4000);
        Toast = "";
    }

    partial void OnOutputModeChanged(GrepOutputMode value)
        => Context.MasterEnabled = value == GrepOutputMode.LineDetails;

    public void LoadGroups(IEnumerable<string> aliases)
    {
        var prevChecked = GroupChecks.Where(c => c.IsChecked).Select(c => c.Alias).ToHashSet();
        GroupChecks.Clear();
        foreach (var a in aliases) GroupChecks.Add(new GroupCheckViewModel(a, prevChecked.Contains(a)));
        CommandEditor.LoadGroups(aliases);
    }

    /// <summary>输出模式单选（UI 四选一）。</summary>
    public bool OutputCount { get => OutputMode == GrepOutputMode.Count; set { if (value) OutputMode = GrepOutputMode.Count; OnPropertyChanged(); } }
    public bool OutputFilesWithMatch { get => OutputMode == GrepOutputMode.FilesWithMatch; set { if (value) OutputMode = GrepOutputMode.FilesWithMatch; OnPropertyChanged(); } }
    public bool OutputFilesWithoutMatch { get => OutputMode == GrepOutputMode.FilesWithoutMatch; set { if (value) OutputMode = GrepOutputMode.FilesWithoutMatch; OnPropertyChanged(); } }
    public bool OutputLineDetails { get => OutputMode == GrepOutputMode.LineDetails; set { if (value) OutputMode = GrepOutputMode.LineDetails; OnPropertyChanged(); } }

    /// <summary>二进制处理下拉（跳过默认 / 当文本）。</summary>
    public bool BinarySkip { get => !BinaryAsText; set { BinaryAsText = !value; OnPropertyChanged(); OnPropertyChanged(nameof(BinaryAsText)); } }

    partial void OnBinaryAsTextChanged(bool value) => OnPropertyChanged(nameof(BinarySkip));

    public void OnWorkingDirectoryChanged(string dir)
    {
        WorkingDirectory = dir;
        if (string.IsNullOrEmpty(GlobRoot)) GlobRoot = dir;
        if (string.IsNullOrEmpty(GrepRoot)) GrepRoot = dir;
        CommandEditor.RootPath = CommandEditor.RootPath.Length == 0 ? dir : CommandEditor.RootPath;
    }

    // ---------- 规则合并 ----------
    private GlobRuleSet BuildRules(
        IEnumerable<string> pageIncludes, IEnumerable<string> pageExcludes,
        IEnumerable<string> pageExcludeFroms, IEnumerable<string> pageExcludeDirs)
    {
        var (inc, exc, exf, exd) = MergedRules;
        return GlobRuleSet.Create(
            pageIncludes.Concat(inc),
            pageExcludes.Concat(exc),
            ReadExcludeFromFiles(pageExcludeFroms).Concat(exf),
            pageExcludeDirs.Concat(exd));
    }

    private static IEnumerable<string> ReadExcludeFromFiles(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            string[] lines = [];
            try { lines = File.ReadAllLines(f); }
            catch { /* 读取失败按无规则处理 */ }
            foreach (var line in lines)
                yield return line;
        }
    }

    // ---------- Glob 执行 ----------
    [RelayCommand]
    private async Task ExecuteGlobAsync()
    {
        if (!await _unsavedGuard()) return;
        if (!ValidateRoot(GlobRoot, "Glob")) return;

        var exprs = Lines(GlobExpressions);
        if (exprs.Count == 0)
        {
            _dialogs.Info("Glob 模式", "请至少输入一条 Glob 表达式。");
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        ScannedCount = HitFileCount = 0;
        GlobResultText = "";
        GlobMatchCount = 0;
        var sb = new StringBuilder();
        int hits = 0;
        try
        {
            var interpreter = new GlobInterpreter();
            var (mInc, mExc, mExf, mExd) = MergedRules;
            var meta = new SearchMetadata(DateTime.Now, "Glob", GlobRoot, [.. SelectedGroups], exprs, 0, 0, 0, 0, "");
            string root = GlobRoot;
            await foreach (var item in interpreter.MatchAsync(root, exprs,
                               Lines(GlobIncludes).Concat(mInc), Lines(GlobExcludes).Concat(mExc),
                               Lines(GlobExcludeFroms).Concat(mExf), Lines(GlobExcludeDirs).Concat(mExd),
                               includeHidden: IncludeHidden, maxFileSizeBytes: (long)_largeFileThresholdBytes, _cts.Token))
            {
                switch (item)
                {
                    case GlobProgress p:
                        ScannedCount = p.ScannedEntries;
                        break;
                    case GlobMatch m:
                        hits++;
                        HitFileCount = hits;
                        GlobMatchCount = hits;
                        sb.AppendLine(m.FullPath);
                        GlobResultText = sb.ToString();
                        break;
                    case GlobSkipped sk:
                        HasSkippedFiles = true;
                        SkippedSummary += $"{sk.FullPath} — {sk.Reason}\n";
                        break;
                }
            }
            meta = meta with { HitFiles = hits, ScannedFiles = ScannedCount, HitLines = hits };
            await SaveHistoryAsync(meta, sb.ToString());
        }
        catch (OperationCanceledException)
        {
            Toast = "已取消";
        }
        finally
        {
            IsRunning = false;
        }
    }

    // ---------- Grep 执行 ----------
    [RelayCommand]
    private async Task ExecuteGrepAsync()
    {
        if (!await _unsavedGuard()) return;

        var patterns = Lines(PatternsText);
        foreach (var f in Lines(PatternFilesText))
        {
            try
            {
                patterns.AddRange(File.ReadLines(f).Where(l => l.Length > 0));
            }
            catch
            {
                _dialogs.Info("Pattern 文件", $"无法读取：{f}");
                return;
            }
        }
        if (patterns.Count == 0)
        {
            _dialogs.Info("Grep 模式", "请至少输入一个 Pattern。");
            return;
        }
        foreach (var p in patterns)
        {
            try { _ = new Regex(p); }
            catch (ArgumentException ex)
            {
                _dialogs.Info("正则非法", $"Pattern “{p}” 不是合法的 .NET 正则：\n{ex.Message}");
                return;
            }
        }
        if (WholeWord && WholeLine)
        {
            WholeLine = false;
            Toast = "-w / -x 互斥，已保留后选者（-w 整词）";
        }
        if (!ValidateRoot(GrepRoot, "Grep")) return;

        var opts = new GrepOptions
        {
            Patterns = patterns,
            FixedStrings = false,
            IgnoreCase = IgnoreCase,
            WholeWord = WholeWord,
            WholeLine = WholeLine,
            InvertMatch = InvertMatch,
            MaxCount = long.TryParse(MaxCountText, out var mc) ? mc : 0,
            Output = OutputMode,
            Context = Context.ContextValue,          // -C（先写，别名会同步 Before/After）
            ContextBefore = Context.BeforeValue,     // -B/-A（后写，覆盖 -C 的对称值）
            ContextAfter = Context.AfterValue,
            ByteOffset = ByteOffset,
            OnlyMatching = OnlyMatching,
            GroupSeparator = string.IsNullOrEmpty(GroupSeparatorText) ? "--" : GroupSeparatorText,
            SuppressErrors = SuppressErrors,
            ShowEol = ShowEol,
            Binary = BinaryAsText ? GrepBinaryMode.AsText : GrepBinaryMode.Skip,
            FollowSymlinks = FollowSymlinks,
            IncludeHidden = IncludeHidden,
            ReadGitignore = ReadGitignore,
            MaxFileSizeBytes = (long)_largeFileThresholdBytes,
            Rules = BuildRules(Lines(GrepIncludes), Lines(GrepExcludes), Lines(GrepExcludeFroms), Lines(GrepExcludeDirs)),
            RootPath = GrepRoot,
        };

        _cts = new CancellationTokenSource();
        IsRunning = true;
        ScannedCount = HitFileCount = HitLineCount = 0;
        ResultText = "";
        SkippedSummary = "";
        HasSkippedFiles = false;
        var sb = new StringBuilder();
        var skipped = new StringBuilder();
        string? lastPath = null;
        try
        {
            var engine = new GrepEngine();
            await foreach (var evt in engine.RunAsync(opts, _cts.Token))
            {
                switch (evt)
                {
                    case GrepFileCompletedEvent c:
                        ScannedCount++;
                        if (c.MatchedLines > 0) HitFileCount++;
                        HitLineCount += c.MatchedLines;
                        break;
                    case GrepFileSkippedEvent s:
                        ScannedCount++;
                        skipped.AppendLine($"{s.FullPath} — {s.Reason}");
                        break;
                    case GrepLineEvent line:
                        AppendLineEvent(sb, line, opts);
                        ResultText = sb.ToString();
                        break;
                    case GrepCountEvent cnt:
                        // -c：每文件一行 `路径:计数`（此前该事件无人消费，导致 -c 结果正文为空）
                        sb.Append(cnt.FullPath).Append(':').Append(cnt.Count).AppendLine();
                        ResultText = sb.ToString();
                        break;
                    case GrepFileMatchEvent fwm:
                        // -l：只列出含匹配的文件
                        sb.AppendLine(fwm.FullPath);
                        ResultText = sb.ToString();
                        break;
                    case GrepFileNoMatchEvent fnm:
                        // -L：只列出不含匹配的文件
                        sb.AppendLine(fnm.FullPath);
                        ResultText = sb.ToString();
                        break;
                    case GrepSeparatorEvent sep:
                        sb.AppendLine(opts.GroupSeparator);
                        break;
                }
                lastPath = evt switch { GrepLineEvent l => l.FullPath, _ => lastPath };
            }

            if (skipped.Length > 0)
                sb.AppendLine().AppendLine("[跳过的文件]").Append(skipped);
            SkippedSummary = skipped.ToString();
            HasSkippedFiles = skipped.Length > 0 && !SuppressErrors;
            ResultText = sb.ToString();

            var cmdSpec = GrepSpecFromPage(patterns);
            var commandText = CommandSerializer.Serialize(cmdSpec);
            int contextLines = Context.ContextValue > 0
                ? Context.ContextValue
                : (Context.BeforeValue > 0 || Context.AfterValue > 0) ? Math.Max(Context.BeforeValue, Context.AfterValue) : 0;
            var meta = new SearchMetadata(DateTime.Now, "Grep", GrepRoot, [.. SelectedGroups], patterns, contextLines, HitFileCount, ScannedCount, HitLineCount, commandText);
            await SaveHistoryAsync(meta, sb.ToString());
        }
        catch (OperationCanceledException)
        {
            Toast = "已取消";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private CommandSpec GrepSpecFromPage(List<string> patterns) => new()
    {
        Mode = SearchMode.Grep,
        Patterns = patterns,
        PatternFiles = Lines(PatternFilesText),
        IgnoreCase = IgnoreCase,
        WholeWord = WholeWord,
        WholeLine = WholeLine,
        InvertMatch = InvertMatch,
        MaxCount = long.TryParse(MaxCountText, out var mc) ? mc : 0,
        Output = (Core.Grep.CommandParsing.OutputMode)(int)OutputMode,
        ContextBefore = Context.BeforeValue,
        ContextAfter = Context.AfterValue,
        Context = Context.ContextValue,
        ByteOffset = ByteOffset,
        OnlyMatching = OnlyMatching,
        GroupSeparator = string.IsNullOrEmpty(GroupSeparatorText) ? "--" : GroupSeparatorText,
        SuppressErrors = SuppressErrors,
        ShowEol = ShowEol,
        BinaryAsText = BinaryAsText,
        FollowSymlinks = FollowSymlinks,
        IncludeHidden = IncludeHidden,
        ReadGitignore = ReadGitignore,
        Includes = Lines(GrepIncludes),
        Excludes = Lines(GrepExcludes),
        ExcludeFroms = Lines(GrepExcludeFroms),
        ExcludeDirs = Lines(GrepExcludeDirs),
        Groups = [.. SelectedGroups],
        RootPath = GrepRoot,
    };

    private static void AppendLineEvent(StringBuilder sb, GrepLineEvent line, GrepOptions opts)
    {
        string path = line.FullPath;
        if (opts.ByteOffset)
            sb.Append(path).Append(':').Append(line.LineNumber).Append(':').Append(line.ByteOffset).Append(": ").AppendLine(line.Content);
        else
            sb.Append(path).Append(':').Append(line.LineNumber).Append(": ").AppendLine(line.Content);
    }

    private bool ValidateRoot(string root, string mode)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _dialogs.Info(mode, "起始目录不存在。");
            return false;
        }
        if (root.StartsWith(@"\\") && !_dialogs.Confirm("UNC 网络路径", $"该路径为网络路径：\n{root}\n网络扫描可能较慢，确定继续吗？"))
            return false;
        return true;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // ---------- 目录浏览 / 组管理入口 ----------

    [RelayCommand]
    private void BrowseGlobRoot()
    {
        var dir = _dialogs.PickFolder(string.IsNullOrWhiteSpace(GlobRoot) ? WorkingDirectory : GlobRoot);
        if (dir != null) GlobRoot = dir;
    }

    [RelayCommand]
    private void BrowseGrepRoot()
    {
        var dir = _dialogs.PickFolder(string.IsNullOrWhiteSpace(GrepRoot) ? WorkingDirectory : GrepRoot);
        if (dir != null) GrepRoot = dir;
    }

    /// <summary>勾选组列表刷新（组管理页增删组后回到搜索页时同步）。</summary>
    [RelayCommand]
    private void ReloadGroups()
    {
        LoadGroups(_groups.GetAll()
            .OrderBy(g => g.IsBuiltIn == false).ThenBy(g => g.Alias)
            .Select(g => g.Alias));
    }

    /// <summary>跳转到组管理页（搜索页"管理组…"入口，由 MainViewModel 接线）。</summary>
    public event Action? ManageGroupsRequested;

    [RelayCommand]
    private void ManageGroups() => ManageGroupsRequested?.Invoke();

    // ---------- 写入 Markdown 编辑器 ----------
    [RelayCommand]
    private void WriteGlobToEditor() => WriteToEditor("Glob", GlobResultText, Lines(GlobExpressions));

    [RelayCommand]
    private void WriteGrepToEditor() => WriteToEditor("Grep", ResultText, Lines(PatternsText));

    private void WriteToEditor(string mode, string resultText, List<string> patterns)
    {
        int context = Context.ContextValue > 0
            ? Context.ContextValue
            : (Context.BeforeValue > 0 || Context.AfterValue > 0) ? Math.Max(Context.BeforeValue, Context.AfterValue) : 0;
        var meta = new SearchMetadata(
            DateTime.Now, mode, mode == "Glob" ? GlobRoot : GrepRoot,
            [.. SelectedGroups], patterns, context,
            HitFileCount, ScannedCount, HitLineCount,
            CommandSerializer.Serialize(mode == "Glob" ? new CommandSpec { Mode = SearchMode.Glob, Globs = patterns, RootPath = GlobRoot, Groups = [.. SelectedGroups] } : GrepSpecFromPage(patterns)));
        SearchCompletedForEditor?.Invoke(meta, resultText);
    }

    // ---------- 命令编辑器 → 搜索页 ----------
    private async Task ExecuteFromCommandEditorAsync(CommandSpec spec)
    {
        if (!await _unsavedGuard()) return;
        // 校验
        if (spec.Mode == SearchMode.Grep)
        {
            var patterns = new List<string>(spec.Patterns);
            foreach (var f in spec.PatternFiles)
            {
                try { patterns.AddRange(File.ReadLines(f).Where(l => l.Length > 0)); }
                catch
                {
                    _dialogs.Info("Pattern 文件", $"无法读取：{f}");
                    return;
                }
            }
            if (patterns.Count == 0)
            {
                _dialogs.Info("命令编辑器", "Grep 模式至少需要一个 Pattern。");
                return;
            }
            foreach (var p in patterns)
            {
                try { _ = spec.FixedStrings ? new Regex(Regex.Escape(p)) : new Regex(p); }
                catch (ArgumentException ex)
                {
                    _dialogs.Info("正则非法（校验）", $"Pattern “{p}” 不是合法的 .NET 正则：\n{ex.Message}");
                    return;
                }
            }
        }
        else if (spec.Globs.Count == 0)
        {
            _dialogs.Info("命令编辑器", "Glob 模式至少需要一个 Glob 表达式（-g）。");
            return;
        }
        if (!Directory.Exists(spec.RootPath))
        {
            _dialogs.Info("命令编辑器", "根目录不存在。");
            return;
        }

        // 规范命令写入搜索页
        if (spec.Mode == SearchMode.Grep)
        {
            GrepRoot = spec.RootPath;
            PatternsText = string.Join("\n", spec.Patterns);
            PatternFilesText = string.Join("\n", spec.PatternFiles);
            IgnoreCase = spec.IgnoreCase;
            WholeWord = spec.WholeWord;
            WholeLine = spec.WholeLine;
            InvertMatch = spec.InvertMatch;
            MaxCountText = spec.MaxCount > 0 ? spec.MaxCount.ToString() : "";
            OutputMode = spec.Output switch
            {
                CommandParsing.OutputMode.FilesWithMatch => GrepOutputMode.FilesWithMatch,
                CommandParsing.OutputMode.FilesWithoutMatch => GrepOutputMode.FilesWithoutMatch,
                CommandParsing.OutputMode.LineDetails => GrepOutputMode.LineDetails,
                _ => GrepOutputMode.Count,
            };
            Context.Load(spec.ContextBefore, spec.ContextAfter, spec.Context);
            ByteOffset = spec.ByteOffset;
            OnlyMatching = spec.OnlyMatching;
            SuppressErrors = spec.SuppressErrors;
            ShowEol = spec.ShowEol;
            BinaryAsText = spec.BinaryAsText;
            FollowSymlinks = spec.FollowSymlinks;
            IncludeHidden = spec.IncludeHidden;
            ReadGitignore = spec.ReadGitignore;
            GroupSeparatorText = spec.GroupSeparator == "--" ? "" : spec.GroupSeparator;
            GrepIncludes = string.Join("\n", spec.Includes);
            GrepExcludes = string.Join("\n", spec.Excludes);
            GrepExcludeFroms = string.Join("\n", spec.ExcludeFroms);
            GrepExcludeDirs = string.Join("\n", spec.ExcludeDirs);
            var specGroups = spec.Groups.ToHashSet();
            foreach (var c in GroupChecks) c.IsChecked = specGroups.Contains(c.Alias);
            await ExecuteGrepAsync();
        }
        else
        {
            GlobRoot = spec.RootPath;
            GlobExpressions = string.Join("\n", spec.Globs);
            GlobIncludes = string.Join("\n", spec.Includes);
            GlobExcludes = string.Join("\n", spec.Excludes);
            GlobExcludeFroms = string.Join("\n", spec.ExcludeFroms);
            GlobExcludeDirs = string.Join("\n", spec.ExcludeDirs);
            // 组勾选按命令中的 Group 块同步（去重）
            var specGroups = spec.Groups.ToHashSet();
            foreach (var c in GroupChecks) c.IsChecked = specGroups.Contains(c.Alias);
            await ExecuteGlobAsync();
        }
        CommandExecuted?.Invoke(); // 执行完毕：视图切到对应模式的「表单」标签以便查看结果
    }

    /// <summary>命令编辑器执行完成（视图据此切到结果显示区）。</summary>
    public event Action? CommandExecuted;

    // ---------- 历史落库 ----------
    private async Task SaveHistoryAsync(SearchMetadata meta, string resultText)
    {
        try
        {
            var rec = new HistoryRecord
            {
                Mode = meta.Mode,
                RootPath = meta.RootPath,
                GroupAliases = string.Join("\n", meta.GroupAliases),
                Include = "",
                Exclude = "",
                ExcludeFrom = "",
                ExcludeDir = "",
                Pattern = string.Join("\n", meta.Patterns),
                CommandText = meta.CommandText,
                HitFiles = meta.HitFiles,
                TotalHits = meta.HitLines,
            };
            await Task.Run(() => _history.Insert(rec, resultText));
        }
        catch (Exception ex)
        {
            Toast = $"历史保存失败：{ex.Message}";
        }
    }

    private static List<string> Lines(string text)
        => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    [RelayCommand]
    private void CopyGlobResult()
    {
        if (GlobResultText.Length > 0) System.Windows.Clipboard.SetText(GlobResultText);
    }

    [RelayCommand]
    private void ExportGlobMarkdown()
    {
        var path = _dialogs.PickSaveFile("glob-results.md", "Markdown (*.md)|*.md");
        if (path == null) return;
        var sb = new StringBuilder("# Glob 匹配结果\n\n");
        foreach (var line in GlobResultText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            sb.Append("- ").AppendLine(line);
        File.WriteAllText(path, sb.ToString());
    }

    [RelayCommand]
    private void CopyGrepResult()
    {
        if (ResultText.Length > 0) System.Windows.Clipboard.SetText(ResultText);
    }
}
