using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Infrastructure;

namespace Core.Grep;

/// <summary>
/// 内容搜索引擎：-c/-l/-L/-n、上下文、-v/-w/-x/-m/-o/-b。
/// 通过 Channel 流式产出事件，禁止一次性收集。
/// </summary>
public sealed class GrepEngine
{
    public async IAsyncEnumerable<GrepEvent> RunAsync(
        GrepOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (options.Patterns.Count == 0)
            throw new ArgumentException("至少需要一个 Pattern", nameof(options));

        var rx = BuildRegex(options);

        var channel = Channel.CreateUnbounded<GrepEvent>(new UnboundedChannelOptions { SingleReader = true });
        var producer = Task.Run(() => Produce(options, rx, channel.Writer, ct), ct);

        await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;

        try { await producer.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* 取消属正常路径 */ }
    }

    public Regex BuildRegex(GrepOptions options)
    {
        var alternatives = options.Patterns.Select(p => options.FixedStrings ? Regex.Escape(p) : p);
        var pattern = string.Join("|", alternatives);

        if (options.WholeLine)          // -x 优先（命令编辑器保证互斥，后选者胜已在解析层归一）
            pattern = "^(?:" + pattern + ")$";
        else if (options.WholeWord)
            pattern = "\\b(?:" + pattern + ")\\b";

        var opts = RegexOptions.CultureInvariant;
        if (options.IgnoreCase) opts |= RegexOptions.IgnoreCase;
        return new Regex(pattern, opts);
    }

    private void Produce(GrepOptions options, Regex rx, ChannelWriter<GrepEvent> writer, CancellationToken ct)
    {
        try
        {
            FileSystemWalker.Walk(
                options.RootPath,
                new WalkOptions
                {
                    IncludeHidden = options.IncludeHidden,
                    FollowSymlinks = options.FollowSymlinks,
                    ReadGitignore = options.ReadGitignore,
                    MaxFileSizeBytes = options.MaxFileSizeBytes,
                    Filter = options.Rules,
                },
                entry => SearchFile(entry.FullPath, options, rx, writer, ct),
                (path, reason) =>
                {
                    if (!options.SuppressErrors)
                        writer.TryWrite(new GrepFileSkippedEvent(path, reason));
                },
                ct);
        }
        catch (OperationCanceledException)
        {
            // 传播取消：直接完成通道
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private void SearchFile(string fullPath, GrepOptions o, Regex rx, ChannelWriter<GrepEvent> writer, CancellationToken ct)
    {
        try
        {
            if (o.MaxFileSizeBytes > 0)
            {
                var fi = new FileInfo(fullPath);
                if (fi.Length > o.MaxFileSizeBytes)
                {
                    if (!o.SuppressErrors)
                        writer.TryWrite(new GrepFileSkippedEvent(fullPath, $"文件大小 {fi.Length / 1024.0 / 1024.0:F1} MB 超过阈值，已跳过"));
                    return;
                }
            }

            using var fs = File.OpenRead(fullPath);

            if (IsBinary(fs))
            {
                if (o.Binary == GrepBinaryMode.Skip)
                {
                    if (!o.SuppressErrors)
                        writer.TryWrite(new GrepFileSkippedEvent(fullPath, "二进制文件"));
                    return;
                }
                fs.Seek(0, SeekOrigin.Begin);
            }

            var (enc, _) = TextLineReader.DetectEncoding(fs);
            using var reader = new TextLineReader(fs, enc);

            switch (o.OutputMode)
            {
                case GrepOutputMode.Count: SearchCount(fullPath, o, rx, reader, writer, ct); break;
                case GrepOutputMode.FilesWithMatch: SearchFilesWithMatch(fullPath, o, rx, reader, writer, ct, matched: true); break;
                case GrepOutputMode.FilesWithoutMatch: SearchFilesWithMatch(fullPath, o, rx, reader, writer, ct, matched: false); break;
                case GrepOutputMode.LineDetails: SearchLineDetails(fullPath, o, rx, reader, writer, ct); break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!o.SuppressErrors)
                writer.TryWrite(new GrepFileSkippedEvent(fullPath, ex.Message));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static bool IsBinary(Stream stream)
    {
        var buf = new byte[8192];
        int n = stream.Read(buf, 0, buf.Length);
        for (int i = 0; i < n; i++)
            if (buf[i] == 0) return true;
        return false;
    }

    private static bool IsMatch(Regex rx, string line, GrepOptions o)
        => o.InvertMatch ? !rx.IsMatch(line) : rx.IsMatch(line);

    private void SearchCount(string fullPath, GrepOptions o, Regex rx, TextLineReader reader, ChannelWriter<GrepEvent> writer, CancellationToken ct)
    {
        int count = 0;
        bool truncated = false;
        long bytes = 0;
        while (reader.TryReadLine(out var line, out _, out int len))
        {
            ct.ThrowIfCancellationRequested();
            if (IsMatch(rx, line, o))
            {
                count++;
                if (o.MaxCount > 0 && count >= o.MaxCount)
                {
                    truncated = true;
                    break; // 截断即停读该文件；计数 = NUM（严格 grep 语义）
                }
            }
            bytes += len;
        }
        writer.TryWrite(new GrepCountEvent(fullPath, count));
        writer.TryWrite(new GrepFileCompletedEvent(fullPath, count, truncated));
    }

    private void SearchFilesWithMatch(string fullPath, GrepOptions o, Regex rx, TextLineReader reader, ChannelWriter<GrepEvent> writer, CancellationToken ct, bool matched)
    {
        bool found = false;
        long bytes = 0;
        while (reader.TryReadLine(out var line, out _, out int len))
        {
            ct.ThrowIfCancellationRequested();
            if (IsMatch(rx, line, o))
            {
                found = true;
                break; // -L 也需要知道是否匹配，找到即可判定
            }
            bytes += len;
        }
        if (found == matched)
            writer.TryWrite(matched ? new GrepFileMatchEvent(fullPath) : new GrepFileNoMatchEvent(fullPath));
        writer.TryWrite(new GrepFileCompletedEvent(fullPath, found ? 1 : 0, Truncated: false));
    }

    private void SearchLineDetails(string fullPath, GrepOptions o, Regex rx, TextLineReader reader, ChannelWriter<GrepEvent> writer, CancellationToken ct)
    {
        int before = o.ContextBefore, after = o.ContextAfter;
        // 环形缓冲：保存最近 before 行（含字节偏移）
        var ring = new (int Line, long Offset, string Content, NewlineKind Nl)[Math.Max(before, 1)];
        int ringCount = 0, ringHead = 0;
        long bytes = 0;
        int lineNo = 0;
        int matched = 0;
        bool truncated = false;
        int afterRemaining = 0;
        long lastEmitted = -1;

        while (reader.TryReadLine(out var content, out var nl, out int len))
        {
            ct.ThrowIfCancellationRequested();
            lineNo++;
            long offset = bytes;
            bytes += len;

            bool isMatch = IsMatch(rx, content, o);
            if (isMatch)
            {
                // 块间分隔符
                long firstEmit = lineNo - before;
                if (lastEmitted >= 0 && (before == 0 ? lineNo : Math.Max(firstEmit, 1)) > lastEmitted + 1)
                    writer.TryWrite(new GrepSeparatorEvent(fullPath));

                // 前文
                if (before > 0)
                {
                    for (int k = 0; k < ringCount; k++)
                    {
                        int idx = (ringHead + k) % ring.Length;
                        var (l, off, c, n) = ring[idx];
                        if (l >= lineNo - before && l > lastEmitted)
                        {
                            EmitLine(writer, fullPath, l, off, c, n, o, ContextLineKind.Before, rx, false);
                            lastEmitted = l;
                        }
                    }
                }

                EmitLine(writer, fullPath, lineNo, offset, content, nl, o, ContextLineKind.Match, rx, true);
                lastEmitted = lineNo;
                afterRemaining = after;
                matched++;

                if (o.MaxCount > 0 && matched >= o.MaxCount)
                {
                    truncated = true;
                    // 仍继续输出本次命中的后文
                }
            }
            else if (afterRemaining > 0)
            {
                EmitLine(writer, fullPath, lineNo, offset, content, nl, o, ContextLineKind.After, rx, false);
                lastEmitted = lineNo;
                afterRemaining--;
            }

            // 维护环形缓冲
            if (before > 0)
            {
                if (ringCount < ring.Length) ringCount++;
                else ringHead = (ringHead + 1) % ring.Length;
                int write = (ringHead + ringCount - 1) % ring.Length;
                ring[write] = (lineNo, offset, content, nl);
            }

            if (truncated)
            {
                // -m 截断：把本次命中剩余的后文输出完再停读
                bool needAfter = afterRemaining > 0;
                int guard = afterRemaining;
                long b = bytes;
                int ln = lineNo;
                while (needAfter && guard > 0 && reader.TryReadLine(out var c2, out var n2, out int l2))
                {
                    ln++;
                    EmitLine(writer, fullPath, ln, b, c2, n2, o, ContextLineKind.After, rx, false);
                    lastEmitted = ln;
                    b += l2;
                    guard--;
                }
                break; // 截断即停读该文件
            }
        }

        writer.TryWrite(new GrepFileCompletedEvent(fullPath, matched, truncated));
    }

    private void EmitLine(ChannelWriter<GrepEvent> writer, string fullPath, int lineNo, long offset, string content, NewlineKind nl, GrepOptions o, ContextLineKind kind, Regex rx, bool isMatch)
    {
        if (o.OnlyMatching && isMatch && !o.InvertMatch)
        {
            // -o：每个匹配片段单独一行
            foreach (Match m in rx.Matches(content))
            {
                if (m.Length == 0) continue;
                writer.TryWrite(new GrepLineEvent(
                    fullPath, lineNo, offset, m.Value, ContextLineKind.OnlyMatchPiece,
                    new[] { new MatchPiece(m.Index, m.Length, m.Value) }));
            }
            return;
        }

        string display = o.ShowEol ? EolMarker.Apply(content, nl) : content;
        writer.TryWrite(new GrepLineEvent(fullPath, lineNo, offset, display, kind, null));
    }
}
