using Core.Diff;
using Core.Grep;
using Core.History;
using Infrastructure;

namespace Core.Tests;

/// <summary>结果文档导出 front matter + 词级 Diff 测试。</summary>
public class ExportAndDiffTests : IDisposable
{
    private readonly string _dir;

    public ExportAndDiffTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "exptests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SearchMetadata Meta(DateTime? t = null) => new(
        ExecuteDateTime: t ?? new DateTime(2026, 1, 2, 3, 4, 5),
        Mode: "Grep",
        RootPath: @"D:\src",
        GroupAliases: ["排除版本控制目录"],
        Patterns: ["abc"],
        ContextLines: 2,
        HitFiles: 3,
        ScannedFiles: 10,
        HitLines: 7,
        CommandText: "--mode grep -n \"abc\" \"D:\\src\"");

    [Fact]
    public void ResultDocument_FollowsTemplate()
    {
        var doc = ResultDocumentBuilder.BuildResultDocument(Meta(), "D:\\src\\a.cs:3: abc");
        Assert.StartsWith("# 搜索结果", doc);
        Assert.Contains("- 时间：2026-01-02 03:04:05", doc);
        Assert.Contains("- 模式：Grep（上下文 2 行）", doc);
        Assert.Contains("- 根目录：D:\\src", doc);
        Assert.Contains("- 组：排除版本控制目录", doc);
        Assert.Contains("- 正则：`abc`", doc);
        Assert.Contains("- 命中文件数：3 / 扫描文件数：10 / 总命中行数：7", doc);
        Assert.Contains("```text", doc);
    }

    [Fact]
    public void GlobMode_TemplateUsesGlobLabel()
    {
        var meta = Meta() with { Mode = "Glob", Patterns = ["**/*.cs"] };
        var doc = ResultDocumentBuilder.BuildResultDocument(meta, "x");
        Assert.Contains("Glob：`**/*.cs`", doc);
        Assert.DoesNotContain("- 正则：", doc);
    }

    [Fact]
    public void ExportDocument_IncludesFrontMatter_WithOriginalExecuteTime()
    {
        var meta = Meta();
        var body = ResultDocumentBuilder.BuildResultDocument(meta, "结果行");
        var exported = ResultDocumentBuilder.BuildExportDocument(meta, body);

        // 行尾跟随平台（StringBuilder.AppendLine → CRLF）：归一化后再断言，避免平台差异造成假失败
        Assert.StartsWith("---\n", exported.Replace("\r\n", "\n"));
        Assert.Contains("execute_datetime: 2026-01-02 03:04:05", exported);
        Assert.Contains("command: \"--mode grep -n \\\"abc\\\" \\\"D:\\\\src\\\"\"", exported);
        Assert.Contains("execute_folder_path: D:\\src", exported);
        Assert.Contains("# 搜索结果", exported);
    }

    [Fact]
    public void PlainDocument_ExportHasNoFrontMatter()
    {
        var exported = ResultDocumentBuilder.BuildExportDocument(null, "# 我的笔记\n普通内容");
        Assert.DoesNotContain("---", exported);
        Assert.DoesNotContain("execute_datetime", exported);
        Assert.StartsWith("# 我的笔记", exported);
    }

    [Fact]
    public void Export_AfterHistoryDeleted_StillComplete_FromMemoryMetadata()
    {
        // 元数据在结果写入编辑器时缓存于内存，不依赖数据库
        var factory = new SqliteConnectionFactory(_dir);
        var repo = new HistoryRepository(factory, -1);
        var rec = repo.Insert(new HistoryRecord { Mode = "Grep", RootPath = @"D:\src", CommandText = "cmd", Pattern = "abc" }, "snap");
        var cachedMeta = Meta(); // 模拟写入编辑器时的内存缓存
        Assert.NotNull(repo.GetById(rec.Id));

        repo.Cleanup(CleanupMode.All); // 删除对应历史记录
        Assert.Null(repo.GetById(rec.Id));

        var exported = ResultDocumentBuilder.BuildExportDocument(cachedMeta, ResultDocumentBuilder.BuildResultDocument(cachedMeta, "x"));
        Assert.Contains("execute_datetime: 2026-01-02 03:04:05", exported);
        Assert.Contains("execute_folder_path: D:\\src", exported);
    }

    [Fact]
    public void FrontMatter_MissingMetadata_BlanksAndQuotes()
    {
        var fm = ResultDocumentBuilder.BuildFrontMatter(null);
        Assert.Contains("execute_datetime: \"\"", fm);
        Assert.Contains("command: \"\"", fm);
    }

    // ---------- 词级 Inline Diff ----------

    [Fact]
    public void InlineDiff_WordLevelPieces()
    {
        // 多行文本使中间行对齐为 Modified（含词级 SubPieces）
        var result = InlineDiffService.Compute("a\nhello world\nb", "a\nhello brave world\nb");
        var changed = Assert.Single(result.Lines, l => l.Kind == DiffLineKind.Changed);
        Assert.Contains(changed.Words, w => w.Kind == DiffPieceKind.Deleted || w.Kind == DiffPieceKind.Inserted);
    }

    [Fact]
    public void InlineDiff_UnchangedLines()
    {
        var result = InlineDiffService.Compute("a\nb\nc", "a\nb\nc");
        Assert.All(result.Lines, l => Assert.Equal(DiffLineKind.Unchanged, l.Kind));
    }

    [Fact]
    public void InlineDiff_DeletedLine_IsStruckThrough()
    {
        var result = InlineDiffService.Compute("keep\ndelete me", "keep");
        var changed = Assert.Single(result.Lines, l => l.Kind == DiffLineKind.Changed);
        Assert.All(changed.Words, w => Assert.Equal(DiffPieceKind.Deleted, w.Kind));
    }
}
