using System.Text;

namespace Core.Grep;

public enum NewlineKind
{
    None,
    Lf,
    CrLf,
    Cr,
}

/// <summary>
/// 逐行读取器：记录原始换行类型与精确字节长度（含换行），
/// 用于 -b 字节偏移与 --show-eol。
/// </summary>
public sealed class TextLineReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly Encoding _encoding;
    private readonly long _startOffset; // BOM 长度

    public TextLineReader(Stream stream, Encoding encoding)
    {
        _encoding = encoding;
        _startOffset = stream.Position; // StreamReader 构造后 BOM 已被消费
        _reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
    }

    public static (Encoding Encoding, int BomLength) DetectEncoding(Stream stream)
    {
        Span<byte> bom = stackalloc byte[3];
        int read = stream.Read(bom);
        stream.Seek(0, SeekOrigin.Begin);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return (new UTF8Encoding(false), 3);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            return (Encoding.Unicode, 2);
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);
        return (new UTF8Encoding(false), 0);
    }

    /// <summary>读取一行；返回 false 表示文件结束。byteLength 为该行含换行的字节长度。</summary>
    public bool TryReadLine(out string line, out NewlineKind newline, out int byteLength)
    {
        int ch = _reader.Read();
        if (ch < 0)
        {
            line = string.Empty;
            newline = NewlineKind.None;
            byteLength = 0;
            return false;
        }

        var sb = new StringBuilder(256);
        while (true)
        {
            char c = (char)ch;
            if (c == '\n') { newline = NewlineKind.Lf; break; }
            if (c == '\r')
            {
                int next = _reader.Peek();
                if (next == '\n') { _reader.Read(); newline = NewlineKind.CrLf; }
                else newline = NewlineKind.Cr;
                break;
            }
            sb.Append(c);
            ch = _reader.Read();
            if (ch < 0) { newline = NewlineKind.None; break; }
        }

        line = sb.ToString();
        byteLength = _encoding.GetByteCount(line) + NewlineByteLength(newline);
        return true;
    }

    public int NewlineByteLength(NewlineKind kind) => kind switch
    {
        NewlineKind.Lf => _encoding.GetByteCount("\n"),
        NewlineKind.Cr => _encoding.GetByteCount("\r"),
        NewlineKind.CrLf => _encoding.GetByteCount("\r\n"),
        _ => 0,
    };

    public long OffsetOfLine(long accumulatedBytes) => _startOffset + accumulatedBytes;

    public int ByteCount(string text) => _encoding.GetByteCount(text);

    public void Dispose() => _reader.Dispose();
}

/// <summary>--show-eol 行尾标记。</summary>
public static class EolMarker
{
    public static string Apply(string content, NewlineKind kind)
        => kind switch
        {
            NewlineKind.Lf => content + "$",
            NewlineKind.CrLf => content + "^M$",
            NewlineKind.Cr => content + "^M",
            _ => content,
        };
}
