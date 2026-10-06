using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Grep;

namespace GrepExplorer.UI.ViewModels;

/// <summary>
/// Context 三输入框（Before / After / Context）：
/// 键盘过滤非法字符、粘贴 sanitize、空=0、上限 1000 超限红框、
/// 联动禁用清零、"×"清空按钮（全部归零并恢复可用，任一非 0 高亮）。
/// </summary>
public partial class ContextFieldsViewModel : ObservableObject
{
    public event Action<string>? Toast;

    private string _before = "";
    private string _after = "";
    private string _context = "";
    private bool _masterEnabled = true;

    public string Before
    {
        get => _before;
        set => SetSanitized(ref _before, value, nameof(Before));
    }

    public string After
    {
        get => _after;
        set => SetSanitized(ref _after, value, nameof(After));
    }

    public string Context
    {
        get => _context;
        set => SetSanitized(ref _context, value, nameof(Context));
    }

    private void SetSanitized(ref string field, string value, string propName)
    {
        var sanitized = ContextValidation.Sanitize(value);
        if (sanitized.Length > 4 || (sanitized.Length > 0 && int.Parse(sanitized) > ContextValidation.MaxValue))
        {
            sanitized = ContextValidation.MaxValue.ToString(); // 超 1000 截断到 1000 并提示
            Toast?.Invoke($"上下文上限为 {ContextValidation.MaxValue}");
        }
        if (field == sanitized) return;
        field = sanitized;
        OnPropertyChanged(propName);
        OnPropertyChanged(nameof(BeforeError));
        OnPropertyChanged(nameof(AfterError));
        OnPropertyChanged(nameof(ContextError));
        RefreshStates();
    }

    public bool BeforeError => ParseField(_before) > ContextValidation.MaxValue;
    public bool AfterError => ParseField(_after) > ContextValidation.MaxValue;
    public bool ContextError => ParseField(_context) > ContextValidation.MaxValue;

    private static int ParseField(string s) => s.Length == 0 ? 0 : int.Parse(s);

    public int BeforeValue => ParseField(_before);
    public int AfterValue => ParseField(_after);
    public int ContextValue => ParseField(_context);

    /// <summary>输出模式是否为行号详情（否则整体禁用 + tooltip）。</summary>
    public bool MasterEnabled
    {
        get => _masterEnabled;
        set
        {
            if (_masterEnabled == value) return;
            _masterEnabled = value;
            OnPropertyChanged();
            RefreshStates();
        }
    }

    public bool BeforeEnabled { get; private set; } = true;
    public bool AfterEnabled { get; private set; } = true;
    public bool ContextEnabled { get; private set; } = true;
    public bool ClearHighlight { get; private set; }
    public string TooltipText { get; private set; } = "";

    private void RefreshStates()
    {
        var state = ContextFieldsState.Compute(BeforeValue, AfterValue, ContextValue, _masterEnabled);
        BeforeEnabled = state.BeforeEnabled;
        AfterEnabled = state.AfterEnabled;
        ContextEnabled = state.ContextEnabled;
        ClearHighlight = state.AnyNonZero;
        TooltipText = state.Tooltip;
        OnPropertyChanged(nameof(BeforeEnabled));
        OnPropertyChanged(nameof(AfterEnabled));
        OnPropertyChanged(nameof(ContextEnabled));
        OnPropertyChanged(nameof(ClearHighlight));
        OnPropertyChanged(nameof(TooltipText));
    }

    /// <summary>仅数字按键过滤（键盘输入）。</summary>
    public static bool IsKeyAllowed(char c) => ContextValidation.IsKeyAllowed(c);

    /// <summary>粘贴 sanitize 入口（按属性名定向）。</summary>
    public void SetFieldByName(string? fieldName, string sanitized)
    {
        switch (fieldName)
        {
            case nameof(Before): Before = sanitized; break;
            case nameof(After): After = sanitized; break;
            case nameof(Context): Context = sanitized; break;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Before = "";
        After = "";
        Context = "";
        // 清空后恢复三框可用
        RefreshStates();
        var cleared = ContextFieldsState.AfterClear(_masterEnabled);
        BeforeEnabled = cleared.BeforeEnabled;
        AfterEnabled = cleared.AfterEnabled;
        ContextEnabled = cleared.ContextEnabled;
        ClearHighlight = cleared.AnyNonZero;
        OnPropertyChanged(nameof(BeforeEnabled));
        OnPropertyChanged(nameof(AfterEnabled));
        OnPropertyChanged(nameof(ContextEnabled));
        OnPropertyChanged(nameof(ClearHighlight));
    }

    public void Load(int before, int after, int context)
    {
        Before = before > 0 ? before.ToString() : "";
        After = after > 0 ? after.ToString() : "";
        Context = context > 0 ? context.ToString() : "";
    }
}
