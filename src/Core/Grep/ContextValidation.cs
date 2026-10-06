namespace Core.Grep;

/// <summary>
/// Context 三输入框的纯逻辑（可单测）：
/// - 仅非负整数：键盘过滤 -、.、字母；粘贴 sanitize（"3.5" → "3"，"-1" 拒绝）；
/// - 空 = 0；上限 1000（超限报错）；
/// - After>0 或 Before>0 → Context 禁用清零；Context>0 → Before/After 禁用清零；
/// - 清空按钮：全部归零并恢复可用。
/// </summary>
public static class ContextValidation
{
    public const int MaxValue = 1000;

    /// <summary>键盘单字符过滤：仅数字允许。</summary>
    public static bool IsKeyAllowed(char c) => char.IsAsciiDigit(c);

    /// <summary>粘贴清洗：取前导数字。"3.5" → "3"；"-1" → ""（被拒绝）。</summary>
    public static string Sanitize(string input)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in input)
        {
            if (char.IsAsciiDigit(c)) sb.Append(c);
            else break; // 非数字出现即截断（前导数字提取）
        }
        return sb.ToString();
    }

    /// <summary>数值校验：0..1000。</summary>
    public static bool IsValid(int value) => value is >= 0 and <= MaxValue;

    public static bool TryParse(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return true; // 空 = 0
        return int.TryParse(text, out value) && value <= MaxValue;
    }
}

/// <summary>Context 三输入框联动状态（纯逻辑，供 VM 与单测使用）。</summary>
public sealed record ContextFieldsState(
    bool BeforeEnabled,
    bool AfterEnabled,
    bool ContextEnabled,
    bool AnyNonZero,
    bool MasterEnabled,
    string Tooltip)
{
    /// <summary>根据当前三框数值与"输出模式是否为行号详情"计算联动状态。</summary>
    public static ContextFieldsState Compute(int before, int after, int context, bool isLineDetails)
    {
        bool master = isLineDetails;
        bool beforeEnabled = master && context == 0;
        bool afterEnabled = master && context == 0;
        bool contextEnabled = master && (before > 0 || after > 0) == false;
        bool any = before > 0 || after > 0 || context > 0;
        string tooltip = master
            ? (any ? "点击 × 清空全部上下文" : "")
            : "上下文仅在行号详情模式下生效";
        return new ContextFieldsState(beforeEnabled, afterEnabled, contextEnabled, any, master, tooltip);
    }

    /// <summary>清空按钮点击后的状态：全部归零并恢复可用。</summary>
    public static ContextFieldsState AfterClear(bool isLineDetails)
        => Compute(0, 0, 0, isLineDetails);
}
