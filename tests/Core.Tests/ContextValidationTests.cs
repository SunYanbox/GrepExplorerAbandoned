using Core.Grep;

namespace Core.Tests;

/// <summary>Context 三输入框校验测试（≥4 条）。</summary>
public class ContextValidationTests
{
    [Fact]
    public void KeyboardFilter_RejectsMinusDotAndLetters()
    {
        Assert.False(ContextValidation.IsKeyAllowed('-'));
        Assert.False(ContextValidation.IsKeyAllowed('.'));
        Assert.False(ContextValidation.IsKeyAllowed('a'));
        Assert.True(ContextValidation.IsKeyAllowed('7'));
    }

    [Fact]
    public void PasteSanitize_MinusOne_IsRejected()
    {
        // 粘贴 "-1"：非数字开头 → 整体拒绝（空）
        Assert.Equal("", ContextValidation.Sanitize("-1"));
    }

    [Fact]
    public void PasteSanitize_ThreePointFive_CleansToThree()
    {
        // 粘贴 "3.5" → 前导数字提取 → "3"
        Assert.Equal("3", ContextValidation.Sanitize("3.5"));
    }

    [Fact]
    public void OverThousand_ReportsError()
    {
        Assert.False(ContextValidation.IsValid(1001));
        Assert.True(ContextValidation.IsValid(1000));
        Assert.True(ContextValidation.TryParse("1001", out var v) == false || !ContextValidation.IsValid(v));
        Assert.False(ContextValidation.TryParse("1001", out _));
        Assert.True(ContextValidation.TryParse("999", out _));
    }

    [Fact]
    public void ClearButton_RestoresAllThreeFields()
    {
        // Before/After 非 0 → Context 禁用；点击 × 后全部归零并恢复可用
        var state = ContextFieldsState.Compute(before: 2, after: 0, context: 0, isLineDetails: true);
        Assert.False(state.ContextEnabled);
        Assert.True(state.BeforeEnabled);
        Assert.True(state.AnyNonZero);

        var cleared = ContextFieldsState.AfterClear(isLineDetails: true);
        Assert.True(cleared.BeforeEnabled);
        Assert.True(cleared.AfterEnabled);
        Assert.True(cleared.ContextEnabled);
        Assert.False(cleared.AnyNonZero);
    }

    [Fact]
    public void ContextNonZero_DisablesBeforeAndAfter()
    {
        var state = ContextFieldsState.Compute(before: 0, after: 0, context: 3, isLineDetails: true);
        Assert.False(state.BeforeEnabled);
        Assert.False(state.AfterEnabled);
        Assert.True(state.ContextEnabled);
    }

    [Fact]
    public void NonLineDetailsMode_DisablesAllWithTooltip()
    {
        var state = ContextFieldsState.Compute(before: 0, after: 0, context: 0, isLineDetails: false);
        Assert.False(state.MasterEnabled);
        Assert.False(state.BeforeEnabled);
        Assert.False(state.AfterEnabled);
        Assert.False(state.ContextEnabled);
        Assert.Contains("行号详情", state.Tooltip);
    }
}
