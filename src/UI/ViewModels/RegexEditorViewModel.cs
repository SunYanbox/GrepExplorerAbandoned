using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrepExplorer.UI.Services;

namespace GrepExplorer.UI.ViewModels;

/// <summary>正则语法速查条目（2.4）。</summary>
public sealed record RegexQuickItem(string Category, string Token, string Description, string Example);

/// <summary>正则编辑器（2.4）：着色、速查面板、实时错误定位、测试区高亮、拾取模式回填。</summary>
public partial class RegexEditorViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private TaskCompletionSource<IReadOnlyList<string>?>? _pickTcs;

    public RegexEditorViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        // 标题里的"已添加 N 个表达式"随列表变化刷新
        Expressions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    /// <summary>标题（拾取模式下附带已添加的表达式数量；旧 XAML 绑定 Title 但 VM 缺该成员）。</summary>
    public string Title => IsPickMode
        ? $"正则编辑器 · 拾取模式（已添加 {Expressions.Count} 个表达式）"
        : "正则编辑器（.NET Regex）";

    /// <summary>是否存在正则错误（驱动顶部红色错误条；旧 XAML 绑定 HasError 但 VM 缺该成员 → 错误条永不显示）。</summary>
    public bool HasError => ErrorText.Length > 0;

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsPickModeChanged(bool value) => OnPropertyChanged(nameof(Title));

    /// <summary>编辑器桥（View 注入）。</summary>
    public IRegexEditorBridge? Bridge { get; set; }

    [ObservableProperty] private string _patternText = "";
    [ObservableProperty] private string _testText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private int _errorPosition = -1;
    [ObservableProperty] private bool _isPickMode;
    [ObservableProperty] private string _statusMessage = "";

    public ObservableCollection<string> Expressions { get; } = new();

    /// <summary>拾取模式完成信号：确认返回表达式列表，取消返回 null。</summary>
    public Task<IReadOnlyList<string>?> PickCompletion => _pickTcs?.Task ?? Task.FromResult<IReadOnlyList<string>?>(null);

    partial void OnPatternTextChanged(string value) => Validate();

    private void Validate()
    {
        ErrorText = "";
        ErrorPosition = -1;
        if (PatternText.Length == 0) return;
        try
        {
            _ = new Regex(PatternText);
            UpdateTestHighlight();
        }
        catch (RegexParseException ex)
        {
            ErrorPosition = ex.Offset;
            ErrorText = $"位置 {ex.Offset}：{ex.Message}";
        }
        catch (ArgumentException ex)
        {
            ErrorText = ex.Message;
        }
    }

    public void UpdateTestHighlight()
    {
        if (Bridge == null) return;
        if (PatternText.Length == 0 || ErrorText.Length > 0)
        {
            Bridge.UpdateMatches([]);
            return;
        }
        try
        {
            var rx = new Regex(PatternText);
            var ms = rx.Matches(TestText)
                .Where(m => m.Length > 0)
                .Select(m => (m.Index, m.Length))
                .ToList();
            Bridge.UpdateMatches(ms);
        }
        catch { /* 已由 Validate 报告 */ }
    }

    partial void OnTestTextChanged(string value) => UpdateTestHighlight();

    /// <summary>进入拾取模式（命令编辑器"进入正则编辑器"）。</summary>
    public void BeginPick(IReadOnlyList<string> initial)
    {
        IsPickMode = true;
        Expressions.Clear();
        foreach (var e in initial) Expressions.Add(e);
        _pickTcs = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [RelayCommand]
    private void AddCurrentExpression()
    {
        if (PatternText.Length == 0) return;
        if (!Expressions.Contains(PatternText)) Expressions.Add(PatternText);
        StatusMessage = $"已添加表达式（共 {Expressions.Count} 个）";
    }

    [RelayCommand]
    private void RemoveExpression(string? item)
    {
        if (item != null) Expressions.Remove(item);
    }

    [RelayCommand]
    private void ConfirmPick()
    {
        AddCurrentExpression();
        IsPickMode = false;
        _pickTcs?.TrySetResult([.. Expressions]);
        _pickTcs = null;
    }

    [RelayCommand]
    private void CancelPick()
    {
        IsPickMode = false;
        _pickTcs?.TrySetResult(null);
        _pickTcs = null;
    }

    /// <summary>速查面板单击：插入到光标处（视图层调用）。</summary>
    internal void OnQuickItemClicked(RegexQuickItem item) => Bridge?.InsertAtCaret(item.Token);

    [RelayCommand]
    private void CopyAll()
    {
        if (PatternText.Length > 0)
            System.Windows.Clipboard.SetText(PatternText);
    }

    [RelayCommand]
    private void ExportAll()
    {
        var path = _dialogs.PickSaveFile("regex.txt", "文本文件 (*.txt)|*.txt");
        if (path == null) return;
        File.WriteAllText(path, PatternText);
    }

    public static IReadOnlyList<RegexQuickItem> QuickItems { get; } =
    [
        // 字符类
        new("字符类", "[abc]", "匹配 a、b、c 中的任意一个字符", "[abc] 匹配 \"a\""),
        new("字符类", "[^abc]", "匹配除 a、b、c 之外的任意字符", "[^abc] 匹配 \"d\""),
        new("字符类", "[a-z]", "匹配小写字母范围", "[a-z]+ 匹配 \"hello\""),
        new("字符类", ".", "匹配除换行符外的任意字符", "a.c 匹配 \"abc\""),
        new("字符类", "\\d", "数字字符，等价 [0-9]", "\\d+ 匹配 \"123\""),
        new("字符类", "\\w", "单词字符，等价 [a-zA-Z0-9_]", "\\w+ 匹配 \"abc_1\""),
        new("字符类", "\\s", "空白字符", "a\\sb 匹配 \"a b\""),
        // 量词
        new("量词", "*", "前一项零次或多次（贪婪）", "ab*c 匹配 \"ac\""),
        new("量词", "+", "前一项一次或多次（贪婪）", "ab+c 匹配 \"abc\""),
        new("量词", "?", "前一项零次或一次", "colou?r 匹配 \"color\""),
        new("量词", "{n}", "恰好 n 次", "a{3} 匹配 \"aaa\""),
        new("量词", "{n,}", "至少 n 次", "a{2,} 匹配 \"aaaa\""),
        new("量词", "{n,m}", "n 到 m 次", "a{2,4} 匹配 \"aaa\""),
        new("量词", "*?", "零次或多次（懒惰）", "<.*?> 惰性匹配标签"),
        new("量词", "+?", "一次或多次（懒惰）", "a+? 最少匹配"),
        new("量词", "??", "零次或一次（懒惰）", "a?? 优先不匹配"),
        // 分组
        new("分组", "(...)", "捕获分组", "(ab)+ 匹配 \"abab\""),
        new("分组", "(?:...)", "非捕获分组", "(?:ab)+ 不捕获"),
        new("分组", "(?#注释)", "内联注释，不参与匹配", "a(?#这是注释)b"),
        new("分组", "(|)", "分支或", "cat|dog 匹配 \"cat\""),
        // 锚点
        new("锚点", "^", "字符串（或行）开头", "^abc 匹配行首 abc"),
        new("锚点", "$", "字符串（或行）结尾", "abc$ 匹配行尾 abc"),
        new("锚点", "\\A", "字符串绝对开头", "\\Aabc"),
        new("锚点", "\\Z", "字符串绝对结尾", "abc\\Z"),
        // 断言
        new("断言", "\\b", "单词边界", "\\bcat\\b 整词匹配 cat"),
        new("断言", "\\B", "非单词边界", "\\Bcat"),
        // 命名分组
        new("命名分组", "(?<name>...)", "命名捕获分组", "(?<year>\\d{4}) 提取年份"),
        new("命名分组", "\\k<name>", "反向引用命名分组", "(?<w>\\w)\\k<w> 叠字"),
        new("命名分组", "\\1", "反向引用第 1 个分组", "(ab)\\1 匹配 \"abab\""),
        // 前后瞻
        new("前后瞻", "(?=...)", "正向先行断言（零宽）", "\\d+(?=元) 匹配后面是\"元\"的数字"),
        new("前后瞻", "(?!...)", "负向先行断言（零宽）", "\\d+(?!元) 匹配后面不是\"元\"的数字"),
        new("前后瞻", "(?<=...)", "正向后行断言（零宽）", "(?<=¥)\\d+ 匹配前面是 ¥ 的数字"),
        new("前后瞻", "(?<!...)", "负向后行断言（零宽）", "(?<!¥)\\d+ 匹配前面不是 ¥ 的数字"),
    ];
}

/// <summary>正则编辑器控件桥。</summary>
public interface IRegexEditorBridge
{
    void InsertAtCaret(string text);
    void UpdateMatches(IReadOnlyList<(int Index, int Length)> matches);
}
