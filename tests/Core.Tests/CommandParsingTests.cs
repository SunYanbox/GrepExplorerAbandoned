using Core.Grep;
using Core.Grep.CommandParsing;

namespace Core.Tests;

/// <summary>命令解析/序列化测试（≥12 条）。</summary>
public class CommandParsingTests
{
    private static CommandParseResult Parse(string text) => CommandParser.Parse(text);
    private static string S(CommandSpec s) => CommandSerializer.Serialize(s);

    [Fact]
    public void QuoteEscape_RoundTrip()
    {
        var spec = new CommandSpec
        {
            Mode = SearchMode.Grep,
            Patterns = ["he said \"hi\" and C:\\temp\\*"],
            RootPath = @"D:\my dir\logs",
        };
        var text = S(spec);
        var back = Parse(text);
        Assert.True(back.Success, string.Join(";", back.Errors.Select(e => e.Message)));
        Assert.Equal(spec.Patterns, back.Spec!.Patterns);
        Assert.Equal(spec.RootPath, back.Spec.RootPath);
        Assert.Equal(text, S(back.Spec!)); // 规范化稳定
    }

    [Fact]
    public void ContextNormalization_BeforeEqualsAfter_BecomesDashC()
    {
        var spec = new CommandSpec { RootPath = "/r", ContextBefore = 3, ContextAfter = 3 };
        var text = S(spec);
        Assert.Contains("-C 3", text);
        Assert.DoesNotContain("-B 3", text);
        Assert.DoesNotContain("-A 3", text);
    }

    [Fact]
    public void ContextNormalization_BeforeNotEqualsAfter_KeptSeparate()
    {
        var spec = new CommandSpec { RootPath = "/r", ContextBefore = 2, ContextAfter = 5 };
        var text = S(spec);
        Assert.Contains("-B 2", text);
        Assert.Contains("-A 5", text);
        Assert.DoesNotContain("-C", text);
    }

    [Fact]
    public void ParseDashC_FillsContextOnly()
    {
        var r = Parse("--mode grep -C 4 -e \"p\" /r");
        Assert.True(r.Success);
        Assert.Equal(4, r.Spec!.Context);
        Assert.Equal(0, r.Spec.ContextBefore);
        Assert.Equal(0, r.Spec.ContextAfter);
    }

    [Fact]
    public void DashC_WithDashB_DashA_ReportsConflict()
    {
        var r = Parse("--mode grep -C 2 -B 1 -e \"p\" /r");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Message.Contains("冲突"));

        var r2 = Parse("--mode grep -C 2 -A 1 -e \"p\" /r");
        Assert.False(r2.Success);
    }

    [Fact]
    public void UnknownFlag_ReportsUnsupported_WithSuggestion_AndNonBlocking()
    {
        var r = Parse("--mode grep --includee \"x\" -e \"p\" /r");
        var err = r.Errors.Single(e => e.Message.Contains("未支持的 grep 参数"));
        Assert.False(err.Blocking);
        Assert.NotNull(err.Suggestion);          // 最接近替代建议 --include
        Assert.True(r.Success);                   // 不阻断其余块
        Assert.Empty(r.Spec!.Includes);           // 未知 flag 被丢弃（含其取值）
    }

    [Fact]
    public void UnclosedQuote_ReportsPosition()
    {
        var r = Parse("--mode grep -e \"unclosed /r");
        Assert.False(r.Success);
        var err = Assert.Single(r.Errors, e => e.Message.Contains("未闭合"));
        Assert.True(err.Blocking);
        Assert.True(err.Position >= 0);
    }

    [Fact]
    public void GroupAliases_AreDeduped()
    {
        var r = Parse("--mode grep --group \"a\" --group a --group \"b\" -e \"p\" /r");
        Assert.True(r.Success);
        Assert.Equal(["a", "b"], r.Spec!.Groups);

        var text = S(new CommandSpec { RootPath = "/r", Groups = ["a", "a", "b", "b"] });
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("--group")));
    }

    [Fact]
    public void GroupAlias_WithSpace_NeedsQuote_RoundTrip()
    {
        var r = Parse("--mode grep --group \"my group\" -e \"p\" /r");
        Assert.True(r.Success);
        var alias = Assert.Single(r.Spec!.Groups);
        Assert.Equal("my group", alias);
    }

    [Fact]
    public void GlobMode_WithGrepFlags_ReportsError()
    {
        var r = Parse("--mode glob -e \"p\" /r");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Message.Contains("仅 Grep 模式"));

        var r2 = Parse("--mode glob -g \"*.cs\" /r");
        Assert.True(r2.Success); // -g 在 Glob 模式合法
    }

    [Fact]
    public void GrepMode_WithGlobOnlyFlag_ReportsError()
    {
        var r = Parse("--mode grep -g \"*.cs\" /r");
        Assert.False(r.Success);
    }

    [Fact]
    public void NonDetailMode_ContextBlocksSerializedAsComments()
    {
        var spec = new CommandSpec
        {
            RootPath = "/r",
            Output = OutputMode.Count,
            Context = 3,
            ByteOffset = true,
            OnlyMatching = true,
            GroupSeparator = "===",
        };
        var text = S(spec);
        Assert.Contains("# -C 3", text);
        Assert.Contains("# -b", text);
        Assert.Contains("# -o", text);
        Assert.Contains("# --group-separator \"===\"", text);
        Assert.DoesNotContain("\n-C 3", text);
        // 解析注释行不报错
        var r = Parse(text);
        Assert.True(r.Success);
    }

    [Fact]
    public void WholeWordThenWholeLine_LastWins()
    {
        var r1 = Parse("--mode grep -w -x -e \"p\" /r");
        Assert.True(r1.Spec!.WholeLine);
        Assert.False(r1.Spec.WholeWord);

        var r2 = Parse("--mode grep -x -w -e \"p\" /r");
        Assert.True(r2.Spec!.WholeWord);
        Assert.False(r2.Spec.WholeLine);
    }

    [Fact]
    public void Patterns_AreDedupedAndOr()
    {
        var r = Parse("--mode grep -e \"p1\" -e \"p1\" -e \"p2\" /r");
        Assert.True(r.Success);
        Assert.Equal(["p1", "p2"], r.Spec!.Patterns);
    }

    [Fact]
    public void MissingMode_IsBlockingError()
    {
        var r = Parse("-e \"p\" /r");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Message.Contains("--mode"));
    }

    [Fact]
    public void RootMustBeLast_ExtraTokenReportsError()
    {
        var r = Parse("--mode grep /r extra");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Message.Contains("多余") || e.Message.Contains("根目录"));
    }

    [Fact]
    public void DashP_IsParsedAndMappedWithWarning()
    {
        var r = Parse("--mode grep -P /r");
        Assert.True(r.Success);
        Assert.Contains(r.Warnings, w => w.Contains(".NET"));
    }

    [Fact]
    public void SingleQuotes_Accepted()
    {
        var r = Parse("--mode grep -e 'pa ttern' \"/root dir\"");
        Assert.True(r.Success);
        Assert.Equal("pa ttern", Assert.Single(r.Spec!.Patterns));
        Assert.Equal("/root dir", r.Spec.RootPath);
    }

    [Fact]
    public void Context_CapsValues()
    {
        // 负数 / 非数字报错
        Assert.False(Parse("--mode grep -C -1 -e \"p\" /r").Success);
        Assert.False(Parse("--mode grep -m abc -e \"p\" /r").Success);
    }

    [Fact]
    public void CanonicalOrder_IsStable()
    {
        var spec = new CommandSpec
        {
            Mode = SearchMode.Grep,
            IgnoreCase = true,
            InvertMatch = true,
            FixedStrings = true,
            MaxCount = 5,
            Output = OutputMode.LineDetails,
            Context = 2,
            ByteOffset = true,
            SuppressErrors = true,
            ShowEol = true,
            BinaryAsText = true,
            FollowSymlinks = true,
            IncludeHidden = true,
            ReadGitignore = true,
            Patterns = ["p1"],
            PatternFiles = ["f1"],
            Includes = ["inc"],
            Excludes = ["exc"],
            ExcludeFroms = ["ef"],
            ExcludeDirs = ["ed"],
            Groups = ["g"],
            RootPath = "/r",
        };
        var lines = S(spec).Split('\n');
        var expected = new[]
        {
            "--mode grep", "-i", "-v", "-F", "-m 5", "-n", "-C 2", "-b", "-s", "--show-eol",
            "-a", "-R", "--hidden", "--gitignore",
            "-e \"p1\"", "-f \"f1\"", "--include \"inc\"", "--exclude \"exc\"",
            "--exclude-from \"ef\"", "--exclude-dir \"ed\"", "--group \"g\"", "\"/r\"",
        };
        Assert.Equal(expected, lines);
    }
}
