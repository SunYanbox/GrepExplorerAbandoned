using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Grep;
using Core.Grep.CommandParsing;
using GrepExplorer.UI.Services;

namespace GrepExplorer.UI.ViewModels;

/// <summary>参数表单区的 Pattern 多行列表行。</summary>
public partial class PatternRowViewModel : ObservableObject
{
    public PatternRowViewModel(string text) => _text = text;

    [ObservableProperty]
    private string _text;

    public event Action<PatternRowViewModel>? RemoveRequested;
    public event Action<PatternRowViewModel>? MoveUpRequested;
    public event Action<PatternRowViewModel>? OpenRegexRequested;

    [RelayCommand] private void Remove() => RemoveRequested?.Invoke(this);
    [RelayCommand] private void MoveUp() => MoveUpRequested?.Invoke(this);
    [RelayCommand] private void OpenRegex() => OpenRegexRequested?.Invoke(this);
}

/// <summary>组多选下拉项。</summary>
public partial class GroupCheckViewModel : ObservableObject
{
    public GroupCheckViewModel(string alias, bool isChecked = false)
    {
        Alias = alias;
        _isChecked = isChecked;
    }

    public string Alias { get; }

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>块列表行。</summary>
public sealed record BlockRowViewModel(string Flag, string Value, int Position, bool IsError, bool IsComment, bool IsInactive)
{
    /// <summary>错误块标红；其余跟随系统前景色（深浅色主题都可用）。</summary>
    public System.Windows.Media.Brush ErrorBrush => IsError
        ? System.Windows.Media.Brushes.IndianRed
        : System.Windows.SystemColors.ControlTextBrush;
}

/// <summary>
/// 命令编辑器：块 ↔ 命令文本双向转换。
/// 块编辑（表单）实时重生成文本；文本手改后必须手动"解析"刷新块，未解析标记"已过期"；
/// 执行永远以块列表为准。
/// </summary>
public partial class CommandEditorViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private bool _suppressSync;

    public CommandEditorViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Errors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasErrors));
        // 默认以「行号详情 -n」起步：-c 只给每文件计数，看不到命中行内容
        CommandText = "--mode grep -n\n-e \"pattern\"\n\"C:\\\"";
        Parse();
        IsOutputLineDetails = OutputMode == OutputMode.LineDetails;
        Context.MasterEnabled = IsOutputLineDetails;
    }

    public event Func<CommandSpec, Task>? ExecuteRequested;
    public event Action<string>? Toast;
    public event Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>?>? RegexEditorRequested;

    // ---------- 模式 ----------
    [ObservableProperty] private SearchMode _mode = SearchMode.Grep;

    partial void OnModeChanged(SearchMode value)
    {
        OnPropertyChanged(nameof(IsGrepMode));
        OnPropertyChanged(nameof(IsGlobMode));
        OnPropertyChanged(nameof(IsGrepModeChecked));
        OnPropertyChanged(nameof(IsGlobModeChecked));
        OnPropertyChanged(nameof(GrepOnlyEnabled));
        SyncFromForm();
    }

    public bool IsGrepMode => Mode == SearchMode.Grep;
    public bool IsGlobMode => Mode == SearchMode.Glob;
    public bool GrepOnlyEnabled => IsGrepMode;

    /// <summary>
    /// 模式单选按钮的可写绑定源。RadioButton.IsChecked 默认双向绑定：只读属性会让点击"点不动"，
    /// 因此这里提供带 setter 的读写属性。
    /// </summary>
    public bool IsGrepModeChecked
    {
        get => Mode == SearchMode.Grep;
        set { if (value && Mode != SearchMode.Grep) Mode = SearchMode.Grep; OnPropertyChanged(); }
    }

    public bool IsGlobModeChecked
    {
        get => Mode == SearchMode.Glob;
        set { if (value && Mode != SearchMode.Glob) Mode = SearchMode.Glob; OnPropertyChanged(); }
    }

    // ---------- 文本 ↔ 块 ----------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StaleVisibility))]
    private string _commandText = "";

    partial void OnCommandTextChanged(string value)
    {
        if (!_suppressSync)
            IsStale = true;
    }

    [ObservableProperty] private bool _isStale;

    /// <summary>解析出的错误列表是否有内容（驱动错误区可见性）。</summary>
    public bool HasErrors => Errors.Count > 0;

    /// <summary>「命令文本已手改但未解析」提示文本。</summary>
    public string StaleText => IsStale ? "⚠ 命令文本已手改，点击「解析 →」刷新块列表后再执行" : "";

    partial void OnIsStaleChanged(bool value) => OnPropertyChanged(nameof(StaleText));

    public System.Windows.Visibility StaleVisibility => IsStale ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public ObservableCollection<BlockRowViewModel> Blocks { get; } = new();
    public ObservableCollection<ParseError> Errors { get; } = new();

    [RelayCommand]
    private void Parse()
    {
        var result = CommandParser.Parse(CommandText);
        Errors.Clear();
        foreach (var e in result.Errors)
            Errors.Add(e);
        foreach (var w in result.Warnings)
            Toast?.Invoke(w);

        // 上下文 / -b / -o / --group-separator 只在 -n（行号详情）下生效，其余模式下置灰
        bool inactive = result.Spec is { IsLineDetails: false };
        Blocks.Clear();
        foreach (var b in result.Blocks)
            Blocks.Add(new BlockRowViewModel(b.Flag, b.Value, b.Position,
                IsError: false, IsComment: b.IsComment, IsInactive: inactive && IsContextOnlyFlag(b.Flag)));

        if (result.Spec != null)
        {
            ApplySpecToForm(result.Spec);
            IsStale = false;
            SyncFromForm(); // 重新规范序列化（去重 + 注释化）
        }
        else
        {
            IsStale = true;
        }
    }

    // ---------- 参数表单 ----------
    public ObservableCollection<PatternRowViewModel> PatternItems { get; } = new();
    [ObservableProperty] private string _patternFilesText = "";    // -f 多行
    [ObservableProperty] private string _globsText = "";           // -g 多行（Glob）
    [ObservableProperty] private bool _fixedStrings;               // -F
    [ObservableProperty] private bool _ignoreCase;                 // -i
    [ObservableProperty] private bool _wholeWord;                  // -w
    [ObservableProperty] private bool _wholeLine;                  // -x
    [ObservableProperty] private bool _invertMatch;                // -v
    [ObservableProperty] private string _maxCountText = "";        // -m
    [ObservableProperty] private OutputMode _outputMode = OutputMode.LineDetails;
    [ObservableProperty] private bool _byteOffset;                 // -b
    [ObservableProperty] private bool _onlyMatching;               // -o
    [ObservableProperty] private string _groupSeparatorText = "";  // --group-separator
    [ObservableProperty] private bool _suppressErrors;             // -s
    [ObservableProperty] private bool _showEol;                    // --show-eol
    [ObservableProperty] private bool _binaryAsText;               // -a
    [ObservableProperty] private bool _followSymlinks;             // -R
    [ObservableProperty] private bool _includeHidden;              // --hidden
    [ObservableProperty] private bool _readGitignore;              // --gitignore
    [ObservableProperty] private string _includesText = "";
    [ObservableProperty] private string _excludesText = "";
    [ObservableProperty] private string _excludeFromsText = "";
    [ObservableProperty] private string _excludeDirsText = "";
    [ObservableProperty] private string _rootPath = "";
    [ObservableProperty] private bool _isOutputLineDetails = false;

    public ContextFieldsViewModel Context { get; } = new();

    public ObservableCollection<GroupCheckViewModel> AvailableGroups { get; } = new();

    partial void OnOutputModeChanged(OutputMode value)
    {
        IsOutputLineDetails = value == OutputMode.LineDetails;
        Context.MasterEnabled = IsOutputLineDetails;
        OnPropertyChanged(nameof(OutputCount));
        OnPropertyChanged(nameof(OutputFilesWithMatch));
        OnPropertyChanged(nameof(OutputFilesWithoutMatch));
        OnPropertyChanged(nameof(OutputLineDetails));
        SyncFromForm();
    }

    // ---------- 输出模式单选（RadioButton.IsChecked 默认双向绑定 → 必须是可写属性，否则点不动） ----------
    public bool OutputCount
    {
        get => OutputMode == Core.Grep.CommandParsing.OutputMode.Count;
        set { if (value) OutputMode = Core.Grep.CommandParsing.OutputMode.Count; OnPropertyChanged(); }
    }

    public bool OutputFilesWithMatch
    {
        get => OutputMode == Core.Grep.CommandParsing.OutputMode.FilesWithMatch;
        set { if (value) OutputMode = Core.Grep.CommandParsing.OutputMode.FilesWithMatch; OnPropertyChanged(); }
    }

    public bool OutputFilesWithoutMatch
    {
        get => OutputMode == Core.Grep.CommandParsing.OutputMode.FilesWithoutMatch;
        set { if (value) OutputMode = Core.Grep.CommandParsing.OutputMode.FilesWithoutMatch; OnPropertyChanged(); }
    }

    public bool OutputLineDetails
    {
        get => OutputMode == Core.Grep.CommandParsing.OutputMode.LineDetails;
        set { if (value) OutputMode = Core.Grep.CommandParsing.OutputMode.LineDetails; OnPropertyChanged(); }
    }

    partial void OnWholeWordChanged(bool value)
    {
        if (value && WholeLine)
        {
            _wholeLine = false;
            OnPropertyChanged(nameof(WholeLine));
            Toast?.Invoke("-w / -x 互斥，已保留后选者（-w 整词）");
        }
        SyncFromForm();
    }

    partial void OnWholeLineChanged(bool value)
    {
        if (value && WholeWord)
        {
            _wholeWord = false;
            OnPropertyChanged(nameof(WholeWord));
            Toast?.Invoke("-w / -x 互斥，已保留后选者（-x 整行）");
        }
        SyncFromForm();
    }

    public void LoadGroups(IEnumerable<string> aliases)
    {
        var selected = AvailableGroups.Where(g => g.IsChecked).Select(g => g.Alias).ToHashSet();
        AvailableGroups.Clear();
        foreach (var a in aliases)
            AvailableGroups.Add(new GroupCheckViewModel(a, selected.Contains(a)));
    }

    partial void OnRootPathChanged(string value) => SyncFromForm();

    // ---------- 表单操作 ----------
    [RelayCommand]
    private void AddPattern()
    {
        var row = new PatternRowViewModel("");
        WirePatternRow(row);
        PatternItems.Add(row);
    }

    private void WirePatternRow(PatternRowViewModel row)
    {
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PatternRowViewModel.Text)) SyncFromForm(); };
        row.RemoveRequested += r => { PatternItems.Remove(r); SyncFromForm(); };
        row.MoveUpRequested += r =>
        {
            int i = PatternItems.IndexOf(r);
            if (i > 0) PatternItems.Move(i, i - 1);
            SyncFromForm();
        };
        row.OpenRegexRequested += r => _ = OpenRegexEditorForAsync(r);
    }

    private async Task OpenRegexEditorForAsync(PatternRowViewModel row)
    {
        var picked = await RegexEditorRequested?.Invoke([row.Text])!;
        if (picked != null && picked.Count > 0)
        {
            row.Text = picked[0];
        }
    }

    [RelayCommand]
    private void EnterRegexEditor() => _ = EnterRegexEditorAsync();

    private async Task EnterRegexEditorAsync()
    {
        var picked = await RegexEditorRequested?.Invoke([.. PatternItems.Select(p => p.Text)])!;
        if (picked == null) return;
        PatternItems.Clear();
        foreach (var p in picked)
        {
            var row = new PatternRowViewModel(p);
            WirePatternRow(row);
            PatternItems.Add(row);
        }
        SyncFromForm();
    }

    [RelayCommand]
    private void Copy() => System.Windows.Clipboard.SetText(CommandText);

    [RelayCommand]
    private void Export()
    {
        var path = _dialogs.PickSaveFile("command.txt", "文本文件 (*.txt)|*.txt");
        if (path != null) File.WriteAllText(path, CommandText);
    }

    /// <summary>确认 → 校验 → 规范命令写入搜索页 → 执行（由 MainViewModel 接线）。</summary>
    [RelayCommand]
    private async Task ConfirmAndExecuteAsync()
    {
        if (IsStale)
        {
            if (!_dialogs.Confirm("命令文本已修改", "命令文本已修改但未解析，执行永远以块列表为准。\n是否先解析？（取消则按当前块列表执行）"))
            {
                // 按当前块列表执行
            }
            else
            {
                Parse();
                if (IsStale)
                {
                    _dialogs.Info("解析失败", "命令文本存在无法解析的错误，请修正后再执行。");
                    return;
                }
            }
        }

        var spec = BuildSpec();
        if (ExecuteRequested != null)
            await ExecuteRequested(spec);
    }

    // ---------- 双向转换 ----------

    /// <summary>表单/块 → 规范命令文本（实时）。</summary>
    public void SyncFromForm()
    {
        if (_suppressSync) return;
        _suppressSync = true;
        try
        {
            var spec = BuildSpec();
            Blocks.Clear();
            foreach (var b in SpecToBlocks(spec))
                Blocks.Add(b);
            CommandText = CommandSerializer.Serialize(spec);
            IsStale = false;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private static IEnumerable<BlockRowViewModel> SpecToBlocks(CommandSpec s)
    {
        bool inactive = !s.IsLineDetails;
        yield return new("--mode", s.Mode == SearchMode.Grep ? "grep" : "glob", 0, false, false, false);
        if (s.IgnoreCase) yield return new("-i", "", 0, false, false, false);
        if (s.WholeWord) yield return new("-w", "", 0, false, false, false);
        else if (s.WholeLine) yield return new("-x", "", 0, false, false, false);
        if (s.InvertMatch) yield return new("-v", "", 0, false, false, false);
        if (s.FixedStrings) yield return new("-F", "", 0, false, false, false);
        if (s.MaxCount > 0) yield return new("-m", s.MaxCount.ToString(), 0, false, false, false);
        if (s.Output != OutputMode.Count) yield return new(OutputFlag(s.Output), "", 0, false, false, false);
        if (s.Context > 0) yield return new("-C", s.Context.ToString(), 0, false, false, inactive);
        else
        {
            if (s.ContextBefore > 0) yield return new("-B", s.ContextBefore.ToString(), 0, false, false, inactive);
            if (s.ContextAfter > 0) yield return new("-A", s.ContextAfter.ToString(), 0, false, false, inactive);
        }
        if (s.ByteOffset) yield return new("-b", "", 0, false, false, inactive);
        if (s.OnlyMatching) yield return new("-o", "", 0, false, false, inactive);
        if (s.GroupSeparator != "--") yield return new("--group-separator", s.GroupSeparator, 0, false, false, inactive);
        if (s.SuppressErrors) yield return new("-s", "", 0, false, false, false);
        if (s.ShowEol) yield return new("--show-eol", "", 0, false, false, false);
        if (s.BinaryAsText) yield return new("-a", "", 0, false, false, false);
        if (s.FollowSymlinks) yield return new("-R", "", 0, false, false, false);
        if (s.IncludeHidden) yield return new("--hidden", "", 0, false, false, false);
        if (s.ReadGitignore) yield return new("--gitignore", "", 0, false, false, false);
        foreach (var p in s.Patterns) yield return new("-e", p, 0, false, false, false);
        foreach (var f in s.PatternFiles) yield return new("-f", f, 0, false, false, false);
        foreach (var g in s.Globs) yield return new("-g", g, 0, false, false, false);
        foreach (var v in s.Includes) yield return new("--include", v, 0, false, false, false);
        foreach (var v in s.Excludes) yield return new("--exclude", v, 0, false, false, false);
        foreach (var v in s.ExcludeFroms) yield return new("--exclude-from", v, 0, false, false, false);
        foreach (var v in s.ExcludeDirs) yield return new("--exclude-dir", v, 0, false, false, false);
        foreach (var g in s.Groups) yield return new("--group", g, 0, false, false, false);
        yield return new("位置参数", s.RootPath, 0, false, false, false);
    }

    private static string OutputFlag(OutputMode m) => m switch
    {
        OutputMode.FilesWithMatch => "-l",
        OutputMode.FilesWithoutMatch => "-L",
        OutputMode.LineDetails => "-n",
        _ => "-c",
    };

    /// <summary>仅在 -n（行号详情）下生效的块。</summary>
    private static bool IsContextOnlyFlag(string flag)
        => flag is "-B" or "-A" or "-C" or "-b" or "-o" or "--group-separator" or "--show-eol";

    /// <summary>表单 → CommandSpec。</summary>
    public CommandSpec BuildSpec() => new()
    {
        Mode = Mode,
        Patterns = [.. PatternItems.Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t))],
        PatternFiles = Lines(PatternFilesText),
        Globs = IsGlobMode ? Lines(GlobsText) : [],
        FixedStrings = FixedStrings,
        IgnoreCase = IgnoreCase,
        WholeWord = WholeWord,
        WholeLine = WholeLine,
        InvertMatch = InvertMatch,
        MaxCount = long.TryParse(MaxCountText, out var m) && m > 0 ? m : 0,
        Output = OutputMode,
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
        Includes = Lines(IncludesText),
        Excludes = Lines(ExcludesText),
        ExcludeFroms = Lines(ExcludeFromsText),
        ExcludeDirs = Lines(ExcludeDirsText),
        Groups = [.. AvailableGroups.Where(g => g.IsChecked).Select(g => g.Alias).Distinct()],
        RootPath = RootPath,
    };

    private static List<string> Lines(string text)
        => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>CommandSpec → 表单（解析成功或外部写入搜索页后调用）。</summary>
    public void ApplySpecToForm(CommandSpec s)
    {
        _suppressSync = true;
        try
        {
            Mode = s.Mode;
            PatternItems.Clear();
            foreach (var p in s.Patterns)
            {
                var row = new PatternRowViewModel(p);
                WirePatternRow(row);
                PatternItems.Add(row);
            }
            PatternFilesText = string.Join("\n", s.PatternFiles);
            GlobsText = string.Join("\n", s.Globs);
            FixedStrings = s.FixedStrings;
            IgnoreCase = s.IgnoreCase;
            WholeWord = s.WholeWord;
            WholeLine = s.WholeLine;
            InvertMatch = s.InvertMatch;
            MaxCountText = s.MaxCount > 0 ? s.MaxCount.ToString() : "";
            OutputMode = s.Output;
            Context.Load(s.ContextBefore, s.ContextAfter, s.Context);
            ByteOffset = s.ByteOffset;
            OnlyMatching = s.OnlyMatching;
            GroupSeparatorText = s.GroupSeparator == "--" ? "" : s.GroupSeparator;
            SuppressErrors = s.SuppressErrors;
            ShowEol = s.ShowEol;
            BinaryAsText = s.BinaryAsText;
            FollowSymlinks = s.FollowSymlinks;
            IncludeHidden = s.IncludeHidden;
            ReadGitignore = s.ReadGitignore;
            IncludesText = string.Join("\n", s.Includes);
            ExcludesText = string.Join("\n", s.Excludes);
            ExcludeFromsText = string.Join("\n", s.ExcludeFroms);
            ExcludeDirsText = string.Join("\n", s.ExcludeDirs);
            RootPath = s.RootPath;
            foreach (var g in AvailableGroups)
                g.IsChecked = s.Groups.Contains(g.Alias);
        }
        finally
        {
            _suppressSync = false;
        }
    }
}
