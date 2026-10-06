using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Infrastructure;

namespace Core.Globbing;

public sealed record GlobMatch(string FullPath, string RelativePath, bool IsDirectory);

public sealed record GlobSkipped(string FullPath, string Reason);

public sealed record GlobProgress(int ScannedEntries);

/// <summary>Glob 模式解释器：流式产出匹配的文件与文件夹绝对路径（不 ToList()）。</summary>
public sealed class GlobInterpreter
{
    /// <summary>includePatterns：多行 Glob 表达式（每行一条），与 ruleIncludes 取 OR。</summary>
    public async IAsyncEnumerable<object> MatchAsync(
        string root,
        IEnumerable<string> globExpressions,
        IEnumerable<string>? ruleIncludes,
        IEnumerable<string>? ruleExcludes,
        IEnumerable<string>? excludeFromFiles,
        IEnumerable<string>? excludeDirs,
        bool includeHidden = false,
        long maxFileSizeBytes = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var globLines = globExpressions.Where(l => !string.IsNullOrWhiteSpace(l));
        var includeAll = globLines.Concat(ruleIncludes ?? Enumerable.Empty<string>());

        var excludeLines = new List<string>(ruleExcludes ?? Enumerable.Empty<string>());
        foreach (var f in excludeFromFiles ?? Enumerable.Empty<string>())
        {
            if (!File.Exists(f)) continue;
            excludeLines.AddRange(File.ReadLines(f));
        }

        var rules = GlobRuleSet.Create(includeAll, excludeLines, null, excludeDirs);

        var channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
        var producer = Task.Run(() =>
        {
            int scanned = 0;
            try
            {
                FileSystemWalker.Walk(
                    root,
                    new WalkOptions
                    {
                        IncludeHidden = includeHidden,
                        FollowSymlinks = false,
                        ReadGitignore = false,
                        MaxFileSizeBytes = maxFileSizeBytes,
                        Filter = rules, // 剪除排除目录并对文件应用 include/exclude 筛选
                    },
                    entry =>
                    {
                        Interlocked.Increment(ref scanned);
                        channel.Writer.TryWrite(new GlobMatch(entry.FullPath, entry.RelativePath, false));
                    },
                    (path, reason) => channel.Writer.TryWrite(new GlobSkipped(path, reason)),
                    ct);

                // Glob 模式还要输出匹配的目录本身
                EmitMatchingDirectories(root, rules, channel, includeHidden, ct);
            }
            finally
            {
                channel.Writer.TryWrite(new GlobProgress(Volatile.Read(ref scanned)));
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;

        await producer.ConfigureAwait(false);
    }

    private static void EmitMatchingDirectories(string root, GlobRuleSet rules, ChannelWriter<object> channel, bool includeHidden, CancellationToken ct)
    {
        var rootFull = Path.GetFullPath(root);
        EmitDir(rootFull, string.Empty, rules, channel, includeHidden, ct);
    }

    private static void EmitDir(string dirFull, string dirRel, GlobRuleSet rules, ChannelWriter<object> channel, bool includeHidden, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IEnumerable<DirectoryInfo> dirs;
        try { dirs = new DirectoryInfo(dirFull).EnumerateDirectories(); }
        catch { return; }

        foreach (var d in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && d.Name.StartsWith('.')) continue;
            var rel = dirRel.Length == 0 ? d.Name : dirRel + "/" + d.Name;
            if (rules.IsExcluded(rel, true)) continue;
            if (rules.IsIncluded(rel, true))
                channel.TryWrite(new GlobMatch(d.FullName, rel, true));
            EmitDir(d.FullName, rel, rules, channel, includeHidden, ct);
        }
    }
}
