namespace Core.Grep.CommandParsing;

/// <summary>解析错误（带 token 位置）。</summary>
public sealed record ParseError(string Message, int Position, bool Blocking, string? Suggestion = null);

public sealed record ParsedBlock(string Flag, string Value, int Position, bool IsComment);

public sealed record CommandParseResult(
    CommandSpec? Spec,
    IReadOnlyList<ParseError> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ParsedBlock> Blocks)
{
    public bool Success => Spec != null;
}

/// <summary>
/// 手写三段式解析器：分词 → flag 识别 → 值提取。
/// 未知 flag 报"未支持的 grep 参数"+ 最接近替代建议，不阻断其余块；
/// -C 与 -B/-A 同现报冲突；Glob 模式携带 Grep 专属 flag 报错。
/// </summary>
public static class CommandParser
{
    public static readonly string[] KnownFlags =
    [
        "--mode", "-e", "-f", "-g", "-E", "-F", "-G", "-P", "-i", "--no-ignore-case",
        "-w", "-x", "-v", "-m", "-c", "-l", "-L", "-n", "-B", "-A", "-C", "-b", "-o",
        "--group-separator", "-s", "--show-eol", "-I", "-a", "-R", "--hidden", "--gitignore",
        "--include", "--exclude", "--exclude-from", "--exclude-dir", "--group",
    ];

    // 仅 Grep 使用的 flag（Glob 模式出现即报错）
    private static readonly HashSet<string> GrepOnlyFlags =
    [
        "-e", "-f", "-E", "-F", "-G", "-P", "-i", "--no-ignore-case", "-w", "-x", "-v",
        "-m", "-c", "-l", "-L", "-n", "-B", "-A", "-C", "-b", "-o", "--group-separator",
        "--show-eol", "-I", "-a",
    ];

    // 需要（或允许）取值的 flag
    private static readonly HashSet<string> ValueFlags =
    [
        "--mode", "-e", "-f", "-g", "-m", "-B", "-A", "-C", "--group-separator",
        "--include", "--exclude", "--exclude-from", "--exclude-dir", "--group",
    ];

    private static readonly HashSet<string> OutputFlags = ["-c", "-l", "-L", "-n"];

    public static CommandParseResult Parse(string commandText)
    {
        var errors = new List<ParseError>();
        var warnings = new List<string>();
        var blocks = new List<ParsedBlock>();
        var spec = new CommandSpec();

        // 逐行解析；'#' 开头的行视为注释（非详情模式下被注释化的上下文块）
        var lines = commandText.Replace("\r\n", "\n").Split('\n');
        var tokens = new List<(CommandTokenizer.Token Tok, int Line)>();
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('#'))
            {
                blocks.Add(new ParsedBlock(trimmed.TrimEnd(), "", li, IsComment: true));
                continue;
            }
            List<CommandTokenizer.Token> lineTokens;
            try
            {
                lineTokens = CommandTokenizer.Tokenize(line);
            }
            catch (CommandTokenizer.TokenizeException ex)
            {
                errors.Add(new ParseError($"引号未闭合（第 {li + 1} 行）：{ex.Message}", ex.Position, Blocking: true));
                return new CommandParseResult(null, errors, warnings, blocks);
            }
            foreach (var t in lineTokens)
                tokens.Add((t, li));
        }

        if (tokens.Count == 0)
        {
            errors.Add(new ParseError("命令为空", 0, Blocking: true));
            return new CommandParseResult(null, errors, warnings, blocks);
        }

        // 第一个 token 必须是 --mode
        var first = tokens[0].Tok;
        if (first.Value != "--mode")
        {
            errors.Add(new ParseError($"命令必须以 --mode 开头，实际为 \"{first.Value}\"", first.Position, Blocking: true));
            return new CommandParseResult(null, errors, warnings, blocks);
        }
        if (tokens.Count < 2)
        {
            errors.Add(new ParseError("--mode 缺少取值（grep / glob）", first.Position, Blocking: true));
            return new CommandParseResult(null, errors, warnings, blocks);
        }
        var modeTok = tokens[1].Tok;
        if (modeTok.Value == "grep") spec.Mode = SearchMode.Grep;
        else if (modeTok.Value == "glob") spec.Mode = SearchMode.Glob;
        else
        {
            errors.Add(new ParseError($"--mode 取值只能是 grep 或 glob，实际为 \"{modeTok.Value}\"", modeTok.Position, Blocking: true));
            return new CommandParseResult(null, errors, warnings, blocks);
        }
        blocks.Add(new ParsedBlock("--mode", spec.Mode.ToString(), first.Position, IsComment: false));

        bool modeSet = true;
        bool rootSet = false;
        bool outputSet = false;
        int seenContext = 0; // bit0: -C，bit1: -B/-A

        void CheckMode(string flag, int pos)
        {
            if (modeSet && spec.Mode == SearchMode.Glob && GrepOnlyFlags.Contains(flag))
                errors.Add(new ParseError($"Grep 专属参数 {flag} 仅 Grep 模式可用", pos, Blocking: true));
            if (modeSet && spec.Mode == SearchMode.Grep && flag == "-g")
                errors.Add(new ParseError("Grep 模式不支持 -g（Glob 表达式），如需按文件名过滤请使用 --include", pos, Blocking: true));
        }

        int i = 2;
        while (i < tokens.Count)
        {
            var tok = tokens[i].Tok;
            if (!tok.Value.StartsWith('-') || tok.Value == "-")
            {
                // 位置参数 = 根目录，必须恰好一个且在末尾
                if (rootSet)
                {
                    errors.Add(new ParseError($"根目录只能出现一次，多余的 token \"{tok.Value}\"", tok.Position, Blocking: true));
                    break;
                }
                if (i != tokens.Count - 1)
                {
                    errors.Add(new ParseError($"根目录必须是最后一个参数，\"{tok.Value}\" 之后还有内容", tok.Position, Blocking: true));
                    rootSet = true;
                    spec.RootPath = tok.Value;
                    break;
                }
                rootSet = true;
                spec.RootPath = tok.Value;
                blocks.Add(new ParsedBlock("<root>", tok.Value, tok.Position, IsComment: false));
                i++;
                continue;
            }

            string flag = tok.Value;
            bool isKnown = KnownFlags.Contains(flag);
            if (!isKnown)
            {
                var suggest = Levenshtein.Suggest(flag, KnownFlags);
                errors.Add(new ParseError(
                    $"未支持的 grep 参数 {flag}" + (suggest != null ? $"，是否想用 {suggest}？" : ""),
                    tok.Position, Blocking: false, Suggestion: suggest));
                // 未知的可能带值，吞掉下一个 token 避免误判
                if (i + 1 < tokens.Count && !tokens[i + 1].Tok.Value.StartsWith('-'))
                    i += 2;
                else
                    i++;
                continue;
            }

            CheckMode(flag, tok.Position);

            string? value = null;
            if (ValueFlags.Contains(flag))
            {
                if (i + 1 >= tokens.Count)
                {
                    errors.Add(new ParseError($"{flag} 缺少取值", tok.Position, Blocking: true));
                    break;
                }
                value = tokens[i + 1].Tok.Value;
                int vPos = tokens[i + 1].Tok.Position;
                blocks.Add(new ParsedBlock(flag, value, vPos, IsComment: false));

                switch (flag)
                {
                    case "--mode": break; // 已处理
                    case "-e":
                        if (value.Length == 0) errors.Add(new ParseError("-e 的 Pattern 不能为空", vPos, Blocking: true));
                        else if (!spec.Patterns.Contains(value)) spec.Patterns.Add(value);
                        break;
                    case "-f":
                        if (!spec.PatternFiles.Contains(value)) spec.PatternFiles.Add(value);
                        break;
                    case "-g":
                        if (!spec.Globs.Contains(value)) spec.Globs.Add(value);
                        break;
                    case "--include":
                        if (!spec.Includes.Contains(value)) spec.Includes.Add(value);
                        break;
                    case "--exclude":
                        if (!spec.Excludes.Contains(value)) spec.Excludes.Add(value);
                        break;
                    case "--exclude-from":
                        if (!spec.ExcludeFroms.Contains(value)) spec.ExcludeFroms.Add(value);
                        break;
                    case "--exclude-dir":
                        if (!spec.ExcludeDirs.Contains(value)) spec.ExcludeDirs.Add(value);
                        break;
                    case "--group":
                        if (!spec.Groups.Contains(value)) spec.Groups.Add(value);
                        break;
                    case "-m":
                        if (!TryParseNonNegLong(value, out var m))
                            errors.Add(new ParseError($"-m 取值必须是非负整数，实际为 \"{value}\"", vPos, Blocking: true));
                        else spec.MaxCount = m;
                        break;
                    case "-B":
                        if (!TryParseNonNegLong(value, out var b) || b > int.MaxValue)
                            errors.Add(new ParseError($"-B 取值必须是非负整数，实际为 \"{value}\"", vPos, Blocking: true));
                        else
                        {
                            if ((seenContext & 1) != 0)
                                errors.Add(new ParseError("-C 与 -B/-A 同时出现，上下文参数冲突", tok.Position, Blocking: true));
                            seenContext |= 2;
                            spec.ContextBefore = (int)b;
                        }
                        break;
                    case "-A":
                        if (!TryParseNonNegLong(value, out var a) || a > int.MaxValue)
                            errors.Add(new ParseError($"-A 取值必须是非负整数，实际为 \"{value}\"", vPos, Blocking: true));
                        else
                        {
                            if ((seenContext & 1) != 0)
                                errors.Add(new ParseError("-C 与 -B/-A 同时出现，上下文参数冲突", tok.Position, Blocking: true));
                            seenContext |= 2;
                            spec.ContextAfter = (int)a;
                        }
                        break;
                    case "-C":
                        if (!TryParseNonNegLong(value, out var cc) || cc > int.MaxValue)
                            errors.Add(new ParseError($"-C 取值必须是非负整数，实际为 \"{value}\"", vPos, Blocking: true));
                        else
                        {
                            if ((seenContext & 2) != 0)
                                errors.Add(new ParseError("-C 与 -B/-A 同时出现，上下文参数冲突", tok.Position, Blocking: true));
                            seenContext |= 1;
                            spec.Context = (int)cc; // -C 解析只填 Context
                        }
                        break;
                    case "--group-separator":
                        spec.GroupSeparator = value;
                        break;
                }

                i += 2;
                continue;
            }

            // 无值 flag
            blocks.Add(new ParsedBlock(flag, "", tok.Position, IsComment: false));
            switch (flag)
            {
                case "-F": spec.FixedStrings = true; break;
                case "-E": spec.FixedStrings = false; break;
                case "-G":
                    spec.FixedStrings = false;
                    warnings.Add("-G（基础正则）已映射为 .NET Regex 方言执行");
                    break;
                case "-P":
                    spec.FixedStrings = false;
                    warnings.Add("-P（PCRE）已映射为 .NET Regex 方言执行，语法可能存在差异");
                    break;
                case "-i": spec.IgnoreCase = true; break;
                case "--no-ignore-case": spec.IgnoreCase = false; break;
                case "-w":
                    spec.WholeWord = true;
                    spec.WholeLine = false; // 互斥，后选者胜
                    break;
                case "-x":
                    spec.WholeLine = true;
                    spec.WholeWord = false; // 互斥，后选者胜
                    break;
                case "-v": spec.InvertMatch = true; break;
                case "-c":
                case "-l":
                case "-L":
                case "-n":
                    if (outputSet)
                        errors.Add(new ParseError($"输出模式只能指定一个，{flag} 与已有输出模式冲突", tok.Position, Blocking: true));
                    outputSet = true;
                    spec.Output = flag switch
                    {
                        "-l" => OutputMode.FilesWithMatch,
                        "-L" => OutputMode.FilesWithoutMatch,
                        "-n" => OutputMode.LineDetails,
                        _ => OutputMode.Count,
                    };
                    break;
                case "-b": spec.ByteOffset = true; break;
                case "-o": spec.OnlyMatching = true; break;
                case "-s": spec.SuppressErrors = true; break;
                case "--show-eol": spec.ShowEol = true; break;
                case "-I": spec.BinaryAsText = false; break;
                case "-a": spec.BinaryAsText = true; break;
                case "-R": spec.FollowSymlinks = true; break;
                case "--hidden": spec.IncludeHidden = true; break;
                case "--gitignore": spec.ReadGitignore = true; break;
            }
            i++;
        }

        if (!rootSet)
            errors.Add(new ParseError("缺少根目录位置参数", commandText.Length, Blocking: true));

        bool hasBlocking = errors.Any(e => e.Blocking);
        return new CommandParseResult(hasBlocking ? null : spec, errors, warnings, blocks);
    }

    private static bool TryParseNonNegLong(string s, out long value)
        => long.TryParse(s, out value) && value >= 0;
}
