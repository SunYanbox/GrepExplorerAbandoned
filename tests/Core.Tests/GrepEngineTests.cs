using Core.Grep;

namespace Core.Tests;

/// <summary>Grep 引擎测试（≥10 条，含 -m 截断计数、-l/-L 互补、-w/-x、上下文等）。</summary>
public class GrepEngineTests : IDisposable
{
    private readonly string _root;

    public GrepEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "greptests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // a.txt：cat 出现 3 行（其中一行含 category）
        File.WriteAllLines(Path.Combine(_root, "a.txt"),
        [
            "cat",           // 1
            "category",      // 2
            "dog",           // 3
            "hot cat here",  // 4
            "nothing",       // 5
            "cat and dog",   // 6
        ]);

        // b.txt：无命中（供 -L）
        File.WriteAllLines(Path.Combine(_root, "b.txt"),
        [
            "alpha",  // 1
            "beta",   // 2
        ]);

        // crlf.txt：CRLF 行尾
        File.WriteAllText(Path.Combine(_root, "crlf.txt"), "cat\r\ndog\r\n");

        // bin.dat：含 NUL 字节
        File.WriteAllBytes(Path.Combine(_root, "bin.dat"), [0x61, 0x00, 0x62, 0x0A, 0x63, 0x0A]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<List<GrepEvent>> RunAsync(GrepOptions o)
    {
        var events = new List<GrepEvent>();
        await foreach (var e in new GrepEngine().RunAsync(o))
            events.Add(e);
        return events;
    }

    private GrepOptions Base(params string[] patterns) => new()
    {
        RootPath = _root,
        Patterns = [.. patterns],
        IncludeHidden = false,
    };

    [Fact]
    public async Task CountMode_CountsMatchingLines()
    {
        // 严格 grep 语义：普通模式 "cat" 也命中 "category"（无词边界）→ 4 行
        var evts = await RunAsync(Base("cat") with { Output = GrepOutputMode.Count });
        var count = evts.OfType<GrepCountEvent>().Single(e => e.FullPath.EndsWith("a.txt"));
        Assert.Equal(4, count.Count);
    }

    [Fact]
    public async Task MaxCount_TruncatesCountToNum_StrictGrepSemantics()
    {
        // -m 2：截断即停读，计数严格等于 NUM
        var evts = await RunAsync(Base("cat") with { Output = GrepOutputMode.Count, MaxCount = 2 });
        var count = evts.OfType<GrepCountEvent>().Single(e => e.FullPath.EndsWith("a.txt"));
        Assert.Equal(2, count.Count);
        var completed = evts.OfType<GrepFileCompletedEvent>().Single(e => e.FullPath.EndsWith("a.txt"));
        Assert.True(completed.Truncated);
    }

    [Fact]
    public async Task MaxCountZero_MeansUnlimited()
    {
        var evts = await RunAsync(Base("cat") with { Output = GrepOutputMode.Count, MaxCount = 0 });
        Assert.Equal(4, evts.OfType<GrepCountEvent>().Single(e => e.FullPath.EndsWith("a.txt")).Count);
    }

    [Fact]
    public async Task FilesWithMatch_ListsMatchingFiles()
    {
        var evts = await RunAsync(Base("cat") with { Output = GrepOutputMode.FilesWithMatch });
        var files = evts.OfType<GrepFileMatchEvent>().Select(e => Path.GetFileName(e.FullPath)).ToList();
        Assert.Equal(["a.txt", "crlf.txt"], files.OrderBy(f => f).ToList());
    }

    [Fact]
    public async Task FilesWithoutMatch_IsComplementaryToFilesWithMatch()
    {
        var with = await RunAsync(Base("cat") with { Output = GrepOutputMode.FilesWithMatch });
        var without = await RunAsync(Base("cat") with { Output = GrepOutputMode.FilesWithoutMatch });
        var lSet = with.OfType<GrepFileMatchEvent>().Select(e => Path.GetFileName(e.FullPath)).ToHashSet();
        var lCapSet = without.OfType<GrepFileNoMatchEvent>().Select(e => Path.GetFileName(e.FullPath)).ToHashSet();
        // 同一文件集上的互补性：-l ∪ -L = 全部被扫描的文本文件（bin.dat 被跳过不计）
        Assert.Empty(lSet.Intersect(lCapSet));
        var union = lSet.Union(lCapSet).OrderBy(f => f).ToList();
        Assert.Equal(["a.txt", "b.txt", "crlf.txt"], union);
    }

    [Fact]
    public async Task LineDetails_ReportsLineNumberAndContent()
    {
        var evts = await RunAsync(Base("cat") with { Output = GrepOutputMode.LineDetails });
        var lines = evts.OfType<GrepLineEvent>().Where(e => e.Kind == ContextLineKind.Match && e.FullPath.EndsWith("a.txt")).ToList();
        Assert.Equal([1, 2, 4, 6], lines.Select(l => l.LineNumber)); // 普通模式含 category(2)
        Assert.Contains("hot cat here", lines.Single(l => l.LineNumber == 4).Content);
    }

    [Fact]
    public async Task Invert_MatchesNonMatchingLines()
    {
        var evts = await RunAsync(Base("beta") with { Output = GrepOutputMode.LineDetails, InvertMatch = true });
        var bLines = evts.OfType<GrepLineEvent>().Where(e => e.FullPath.EndsWith("b.txt") && e.Kind == ContextLineKind.Match).ToList();
        Assert.Equal([1], bLines.Select(l => l.LineNumber)); // "beta" 的反向：仅 alpha
        Assert.Equal("alpha", bLines[0].Content);
    }

    [Fact]
    public async Task WholeWord_DoesNotMatchInsideWord()
    {
        var evts = await RunAsync(Base("cat") with { Output = GrepOutputMode.LineDetails, WholeWord = true });
        var aLines = evts.OfType<GrepLineEvent>().Where(e => e.FullPath.EndsWith("a.txt") && e.Kind == ContextLineKind.Match).ToList();
        Assert.Equal([1, 4, 6], aLines.Select(l => l.LineNumber)); // category(2) 不命中

        // -o + -w：仅匹配片段
        var pieces = evts.OfType<GrepLineEvent>().Where(e => e.Kind == ContextLineKind.OnlyMatchPiece).ToList();
        Assert.All(pieces, p => Assert.Equal("cat", p.Content));
    }

    [Fact]
    public async Task WholeLine_RequiresEntireLineMatch()
    {
        var evts = await RunAsync(Base("cat") with { Output = GrepOutputMode.LineDetails, WholeLine = true });
        var aLines = evts.OfType<GrepLineEvent>().Where(e => e.FullPath.EndsWith("a.txt") && e.Kind == ContextLineKind.Match).ToList();
        Assert.Equal([1], aLines.Select(l => l.LineNumber)); // 仅整行为 cat 的行
    }

    [Fact]
    public async Task Context_CEmitsSurroundingLinesAndSeparator()
    {
        var evts = await RunAsync(Base("dog") with
        {
            Output = GrepOutputMode.LineDetails,
            Context = 1,
        });
        var a = evts.OfType<GrepLineEvent>().Where(e => e.FullPath.EndsWith("a.txt")).ToList();
        Assert.Equal([3, 6], a.Where(l => l.Kind == ContextLineKind.Match).Select(l => l.LineNumber));
        // 行2(Before)+行4(After)+行5(Before)
        Assert.Equal(3, a.Count(l => l.Kind == ContextLineKind.Before || l.Kind == ContextLineKind.After));
        // -C 1 恰好覆盖间隙（行4-行5 连续）→ 无分隔符
        Assert.Empty(evts.OfType<GrepSeparatorEvent>().Where(e => e.FullPath.EndsWith("a.txt")));
    }

    [Fact]
    public async Task Context_SeparatorBetweenNonContiguousBlocks_WithCustomSeparator()
    {
        // ^cat$（行1）+ nothing（行5）：块 [1,2] 与块 [4,5,6] 之间隔行 3 → 1 个自定义分隔符
        var evts = await RunAsync(Base("^cat$", "nothing") with
        {
            Output = GrepOutputMode.LineDetails,
            Context = 1,
            GroupSeparator = "===",
        });
        var seps = evts.OfType<GrepSeparatorEvent>().Where(e => e.FullPath.EndsWith("a.txt")).ToList();
        Assert.Equal(1, seps.Count);
    }

    [Fact]
    public async Task OnlyMatching_EmitsEachPiece()
    {
        var evts = await RunAsync(Base("c.t") with { Output = GrepOutputMode.LineDetails, OnlyMatching = true });
        var pieces = evts.OfType<GrepLineEvent>().Where(e => e.Kind == ContextLineKind.OnlyMatchPiece && e.FullPath.EndsWith("a.txt")).ToList();
        // "cat"×4（category 的前缀也是 cat）→ 4 个片段
        Assert.All(pieces, p => Assert.Equal("cat", p.Content));
        Assert.Equal(4, pieces.Count);
    }

    [Fact]
    public async Task FixedStrings_TreatsPatternLiterally()
    {
        var evts = await RunAsync(Base("a.c") with { Output = GrepOutputMode.LineDetails, FixedStrings = true });
        var aLines = evts.OfType<GrepLineEvent>().Where(e => e.FullPath.EndsWith("a.txt") && e.Kind == ContextLineKind.Match).ToList();
        Assert.Empty(aLines); // "a.c" 字面量不出现在 a.txt（. 不匹配任意字符）

        File.WriteAllText(Path.Combine(_root, "lit.txt"), "a.c\nabc\n");
        var evts2 = await RunAsync(Base("a.c") with { Output = GrepOutputMode.LineDetails, FixedStrings = true });
        var lit = evts2.OfType<GrepLineEvent>().Where(e => e.FullPath.EndsWith("lit.txt") && e.Kind == ContextLineKind.Match).ToList();
        Assert.Equal([1], lit.Select(l => l.LineNumber));
    }

    [Fact]
    public async Task IgnoreCase_MatchesCaseInsensitively()
    {
        File.WriteAllText(Path.Combine(_root, "case.txt"), "CAT\nCat\ncat\n");
        var evts = await RunAsync(Base("cat") with { Output = GrepOutputMode.Count, IgnoreCase = true });
        Assert.Equal(3, evts.OfType<GrepCountEvent>().Single(e => e.FullPath.EndsWith("case.txt")).Count);

        var evts2 = await RunAsync(Base("cat") with { Output = GrepOutputMode.Count });
        Assert.Equal(1, evts2.OfType<GrepCountEvent>().Single(e => e.FullPath.EndsWith("case.txt")).Count);
    }

    [Fact]
    public async Task MultiplePatterns_OrSemantics()
    {
        var evts = await RunAsync(Base("cat", "dog") with { Output = GrepOutputMode.Count });
        Assert.Equal(5, evts.OfType<GrepCountEvent>().Single(e => e.FullPath.EndsWith("a.txt")).Count); // 1,2,3,4,6
    }

    [Fact]
    public async Task BinaryFile_SkippedByDefault_IncludedAsTextWithOption()
    {
        var evts = await RunAsync(Base("c") with { Output = GrepOutputMode.LineDetails });
        Assert.Contains(evts.OfType<GrepFileSkippedEvent>(), e => e.FullPath.EndsWith("bin.dat") && e.Reason.Contains("二进制"));

        var evts2 = await RunAsync(Base("c") with { Output = GrepOutputMode.LineDetails, Binary = GrepBinaryMode.AsText });
        Assert.DoesNotContain(evts2.OfType<GrepFileSkippedEvent>(), e => e.FullPath.EndsWith("bin.dat"));
        // NUL 后的 c 行以文本方式被扫描（0x63 0x0A → "c"）
        Assert.Contains(evts2.OfType<GrepLineEvent>(), e => e.FullPath.EndsWith("bin.dat") && e.Content == "c");
    }

    [Fact]
    public async Task ShowEol_AppendsMarkers()
    {
        var evts = await RunAsync(Base("cat") with { Output = GrepOutputMode.LineDetails, ShowEol = true });
        var crlf = evts.OfType<GrepLineEvent>().Single(e => e.FullPath.EndsWith("crlf.txt") && e.Kind == ContextLineKind.Match);
        Assert.EndsWith("^M$", crlf.Content);

        var evts2 = await RunAsync(Base("cat") with { Output = GrepOutputMode.LineDetails });
        var noEol = evts2.OfType<GrepLineEvent>().Single(e => e.FullPath.EndsWith("crlf.txt") && e.Kind == ContextLineKind.Match);
        Assert.EndsWith("cat", noEol.Content);
        Assert.False(noEol.Content.EndsWith("$"));
    }

    [Fact]
    public async Task ByteOffset_ReportsCumulativeOffset()
    {
        var evts = await RunAsync(Base("beta") with { Output = GrepOutputMode.LineDetails, ByteOffset = true });
        var line2 = evts.OfType<GrepLineEvent>().Single(e => e.FullPath.EndsWith("b.txt") && e.LineNumber == 2);
        Assert.True(line2.ByteOffset > 0);
        // 第 2 行的起始偏移 = 首行字节数："alpha" + 平台换行（WriteAllLines 在 Windows 上写 CRLF）
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("alpha" + Environment.NewLine), line2.ByteOffset);
    }

    [Fact]
    public async Task IncludeRules_FilterFiles()
    {
        var rules = new GlobRuleAdapter();
        var evts = await RunAsync(Base("cat") with
        {
            Output = GrepOutputMode.LineDetails,
            Rules = rules,
        });
        // 规则排除了 a.txt（通过 adapter 构造）
        Assert.DoesNotContain(evts.OfType<GrepLineEvent>(), e => e.FullPath.EndsWith("a.txt"));
    }

    private sealed class GlobRuleAdapter : Infrastructure.GlobRuleFilter
    {
        private readonly Core.Globbing.GlobRuleSet _rs =
            Core.Globbing.GlobRuleSet.Create(null, ["a.txt"], null, null);
        public bool IsDirPruned(string relativeDirPath, string dirName) => _rs.IsDirPruned(relativeDirPath, dirName);
        public bool IsFileAccepted(string relativePath) => _rs.IsFileAccepted(relativePath);
    }
}
