using System.Text;

namespace Core.Grep.CommandParsing;

/// <summary>手写命令分词器（第一段）：识别单/双引号与裸 token，未闭合引号报错并定位。</summary>
public static class CommandTokenizer
{
    public sealed record Token(string Value, int Position, bool Quoted);

    public sealed class TokenizeException : Exception
    {
        public int Position { get; }
        public TokenizeException(string message, int position) : base(message) => Position = position;
    }

    public static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '"' || c == '\'')
            {
                char quote = c;
                int start = i;
                i++;
                var sb = new StringBuilder();
                bool closed = false;
                while (i < input.Length)
                {
                    char ch = input[i];
                    if (quote == '"' && ch == '\\' && i + 1 < input.Length && (input[i + 1] == '"' || input[i + 1] == '\\'))
                    {
                        sb.Append(input[i + 1]);
                        i += 2;
                    }
                    else if (ch == quote)
                    {
                        i++;
                        closed = true;
                        break;
                    }
                    else if (quote == '"' && ch == '\\' && i + 1 >= input.Length)
                    {
                        // 尾部孤立反斜杠按字面量处理
                        sb.Append(ch);
                        i++;
                    }
                    else
                    {
                        sb.Append(ch);
                        i++;
                    }
                }
                if (!closed)
                    throw new TokenizeException($"第 {start} 个字符处引号未闭合", start);
                tokens.Add(new Token(sb.ToString(), start, true));
            }
            else
            {
                int start = i;
                var sb = new StringBuilder();
                while (i < input.Length && !char.IsWhiteSpace(input[i]))
                {
                    sb.Append(input[i]);
                    i++;
                }
                tokens.Add(new Token(sb.ToString(), start, false));
            }
        }
        return tokens;
    }
}
