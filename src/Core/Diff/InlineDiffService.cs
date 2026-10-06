using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffPlex.Chunkers;

namespace Core.Diff;

public enum DiffPieceKind
{
    Same,
    Inserted,
    Deleted,
}

public sealed record DiffWord(string Text, DiffPieceKind Kind);

public enum DiffLineKind
{
    Unchanged,
    Changed,
}

/// <summary>一行 Inline Diff：变更行整行呈现，行内含删除/插入片段。</summary>
public sealed record DiffLine(DiffLineKind Kind, IReadOnlyList<DiffWord> Words);

public sealed record InlineDiffResult(IReadOnlyList<DiffLine> Lines);

/// <summary>
/// 基于 DiffPlex 的词级 Inline Diff（VSCode Inline 风格）：
/// 单栏显示，Deleted/Inserted 行一比一配对为单行 Changed（行内词级：删除片段红色删除线、插入片段绿色，渲染由 UI 完成）。
/// </summary>
public static class InlineDiffService
{
    public static InlineDiffResult Compute(string oldText, string newText)
    {
        var model = InlineDiffBuilder.Instance.BuildDiffModel(oldText ?? "", newText ?? "", ignoreWhitespace: false);
        var lines = new List<DiffLine>();
        string? pendingDelete = null;

        void FlushPendingDelete()
        {
            if (pendingDelete != null)
            {
                lines.Add(new DiffLine(DiffLineKind.Changed, [new DiffWord(pendingDelete, DiffPieceKind.Deleted)]));
                pendingDelete = null;
            }
        }

        foreach (var line in model.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Deleted:
                    FlushPendingDelete();
                    pendingDelete = line.Text ?? "";
                    break;

                case ChangeType.Inserted:
                    if (pendingDelete != null)
                    {
                        // 一比一配对 → 单行 Changed，行内词级片段
                        lines.Add(new DiffLine(DiffLineKind.Changed, WordDiff(pendingDelete, line.Text ?? "")));
                        pendingDelete = null;
                    }
                    else
                    {
                        lines.Add(new DiffLine(DiffLineKind.Changed, [new DiffWord(line.Text ?? "", DiffPieceKind.Inserted)]));
                    }
                    break;

                case ChangeType.Modified:
                    FlushPendingDelete();
                    lines.Add(new DiffLine(DiffLineKind.Changed, ToWords(line.SubPieces)));
                    break;

                default:
                    FlushPendingDelete();
                    lines.Add(new DiffLine(DiffLineKind.Unchanged, [new DiffWord(line.Text ?? "", DiffPieceKind.Same)]));
                    break;
            }
        }
        FlushPendingDelete();

        return new InlineDiffResult(lines);
    }

    /// <summary>词级 diff：DiffPlex CreateCustomDiffs 按词分块 → 行内 Same/Deleted/Inserted 片段。</summary>
    private static IReadOnlyList<DiffWord> WordDiff(string oldLine, string newLine)
    {
        var oldTokens = SplitWords(oldLine);
        var newTokens = SplitWords(newLine);
        var result = Differ.Instance.CreateCustomDiffs(oldLine, newLine, ignoreWhiteSpace: false, SplitWords);
        var words = new List<DiffWord>();
        int ia = 0, ib = 0;
        foreach (var block in result.DiffBlocks)
        {
            while (ia < block.DeleteStartA && ib < block.InsertStartB)
            {
                words.Add(new DiffWord(oldTokens[ia], DiffPieceKind.Same));
                ia++; ib++;
            }
            for (int k = 0; k < block.DeleteCountA && ia < oldTokens.Length; k++)
                words.Add(new DiffWord(oldTokens[ia++], DiffPieceKind.Deleted));
            for (int k = 0; k < block.InsertCountB && ib < newTokens.Length; k++)
                words.Add(new DiffWord(newTokens[ib++], DiffPieceKind.Inserted));
        }
        while (ia < oldTokens.Length && ib < newTokens.Length)
        {
            words.Add(new DiffWord(oldTokens[ia], DiffPieceKind.Same));
            ia++; ib++;
        }
        return words;
    }

    private static string[] SplitWords(string text)
        => System.Text.RegularExpressions.Regex.Split(text, @"(\s+)").Where(s => s.Length > 0).ToArray();


    private static IReadOnlyList<DiffWord> ToWords(IEnumerable<DiffPiece>? subPieces)
    {
        var words = new List<DiffWord>();
        if (subPieces == null)
            return words;

        foreach (var piece in subPieces)
        {
            var kind = piece.Type switch
            {
                ChangeType.Inserted => DiffPieceKind.Inserted,
                ChangeType.Deleted => DiffPieceKind.Deleted,
                _ => DiffPieceKind.Same,
            };
            words.Add(new DiffWord(piece.Text ?? "", kind));
        }
        return words;
    }
}
