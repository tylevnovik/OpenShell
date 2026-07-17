using System.Text;

namespace OpenShell.Variables;

/// <summary>
/// 变量展开器。Per ADR-0047 §8 (revises ADR-0042 §7-8).
/// 支持成员访问 ($var.Property) 与索引访问 ($var[index]) via MemberAccessor.
/// $var / ${var} / $env:NAME / $? 行为保留。
/// $(...) 子表达式延后到 ADR-0045 (抛 NotSupportedException)。
/// </summary>
public static class VariableExpander
{
    /// <summary>
    /// 尝试解析整行作为变量查询。命中返回 true + 值; 否则返回 false。
    /// 例如 "$?" / "$LASTEXITCODE" / "$env:PATH" / "${PWD}" / "$var.Property" / "$arr[0]" / "${name}.Length".
    /// </summary>
    public static bool TryResolve(string line, IVariableRegistry vars, out object? value)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            value = null;
            return false;
        }

        // ${name} 形式 (含可选 .Property / [index] 后缀).
        if (trimmed.StartsWith("${") && trimmed.Length > 3)
        {
            var close = FindClosingBrace(trimmed, '{', '}');
            if (close > 1)
            {
                var name = trimmed[2..close];
                var suffix = trimmed[(close + 1)..];
                var baseValue = vars.Resolve(name);
                value = ApplySuffix(baseValue, suffix);
                return true;
            }
        }

        // $name 形式 (含 $env:NAME / $? / $global:x / $var.Property / $arr[0]).
        if (trimmed.StartsWith("$"))
        {
            var (name, suffix) = ParseVariableAndSuffix(trimmed[1..]);
            if (!string.IsNullOrEmpty(name) && IsValidVariableName(name))
            {
                var baseValue = vars.Resolve(name);
                value = ApplySuffix(baseValue, suffix);
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// 展开字符串中的变量引用 (用于双引号字符串插值 / 命令行参数)。
    /// 单引号包裹的整段不插值。
    /// </summary>
    public static string Expand(string input, IVariableRegistry vars)
    {
        if (input.Length == 0) return input;

        // 单引号包裹整段 → 不插值.
        if (input.StartsWith("'") && input.EndsWith("'") && input.Length >= 2)
            return input[1..^1];

        var sb = new StringBuilder(input.Length + 16);
        var inSingleQuote = false;

        for (int i = 0; i < input.Length; i++)
        {
            var ch = input[i];

            if (ch == '\'' && !IsInDoubleQuote(input, i))
            {
                inSingleQuote = !inSingleQuote;
                sb.Append(ch);
                continue;
            }

            // 单引号内的 $ 不展开.
            if (inSingleQuote)
            {
                sb.Append(ch);
                continue;
            }

            // 双引号字符串内的 ${name} / $name 展开.
            if (ch == '$' && i + 1 < input.Length)
            {
                var next = input[i + 1];

                // $(...) 子表达式 — ADR-0047 §5, 本 PR 不实现.
                if (next == '(')
                {
                    throw new NotSupportedException(
                        "$(...) sub-expression interpolation requires parser integration (ADR-0045, deferred).");
                }

                if (next == '{')
                {
                    // ${name} 形式: 找到 } 闭合.
                    var close = input.IndexOf('}', i + 2);
                    if (close > i + 1)
                    {
                        var name = input[(i + 2)..close];
                        // 检查紧跟的 .Property / [index] 后缀.
                        var (suffix, endIdx) = ReadSuffix(input, close + 1);
                        var baseVal = vars.Resolve(name);
                        var resolved = ApplySuffix(baseVal, suffix);
                        sb.Append(resolved?.ToString() ?? "");
                        i = endIdx - 1;
                        continue;
                    }
                }
                else if (next == '?' || char.IsLetter(next) || next == '_')
                {
                    // $name 形式: 匹配最长变量名 (含 $env:NAME).
                    var end = i + 1;
                    while (end < input.Length && (input[end] == '?' || input[end] == '_' || char.IsLetterOrDigit(input[end]) || input[end] == ':'))
                        end++;

                    if (end > i + 1)
                    {
                        var name = input[(i + 1)..end];
                        // 检查紧跟的 .Property / [index] 后缀.
                        var (suffix, endIdx) = ReadSuffix(input, end);
                        var baseVal = vars.Resolve(name);
                        var resolved = ApplySuffix(baseVal, suffix);
                        sb.Append(resolved?.ToString() ?? "");
                        i = endIdx - 1;
                        continue;
                    }
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 展开双引号字符串字面量内容中的变量插值。Per ADR-0050 §6.4 / ADR-0047 §8.
    /// 与 <see cref="Expand"/> 不同，本方法不处理单引号段——字符串字面量内容已由 tokenizer
    /// 提取（引号已剥离），单引号为字面字符。
    /// 支持 $name / ${name} / $? / $env:NAME / $var.Property / $arr[index]。
    /// $(...) 子表达式需 parser 集成，暂抛 NotSupportedException。
    /// 已知限制：backtick 转义的 `$（"`$var"）在 tokenizer 层已转为裸 $，本方法无法区分，
    /// 会错误插值。完整修复需 tokenizer 产出可插值段 AST（见 T-083/T-088 后续）。
    /// </summary>
    public static string ExpandInterpolation(string content, IVariableRegistry vars)
    {
        if (content.Length == 0) return content;
        var sb = new StringBuilder(content.Length + 16);

        for (int i = 0; i < content.Length; i++)
        {
            var ch = content[i];

            if (ch == '$' && i + 1 < content.Length)
            {
                var next = content[i + 1];

                // $(...) 子表达式 — 需 parser 集成，暂不支持
                if (next == '(')
                {
                    throw new NotSupportedException(
                        "$(...) sub-expression interpolation requires parser integration (deferred).");
                }

                if (next == '{')
                {
                    // ${name} 形式: 找到 } 闭合.
                    var close = content.IndexOf('}', i + 2);
                    if (close > i + 1)
                    {
                        var name = content[(i + 2)..close];
                        var (suffix, endIdx) = ReadSuffix(content, close + 1);
                        var baseVal = vars.Resolve(name);
                        var resolved = ApplySuffix(baseVal, suffix);
                        sb.Append(resolved?.ToString() ?? "");
                        i = endIdx - 1;
                        continue;
                    }
                }
                else if (next == '?' || char.IsLetter(next) || next == '_')
                {
                    // $name 形式: 匹配最长变量名 (含 $env:NAME / $?).
                    var end = i + 1;
                    while (end < content.Length && (content[end] == '?' || content[end] == '_' || char.IsLetterOrDigit(content[end]) || content[end] == ':'))
                        end++;

                    if (end > i + 1)
                    {
                        var name = content[(i + 1)..end];
                        var (suffix, endIdx) = ReadSuffix(content, end);
                        var baseVal = vars.Resolve(name);
                        var resolved = ApplySuffix(baseVal, suffix);
                        sb.Append(resolved?.ToString() ?? "");
                        i = endIdx - 1;
                        continue;
                    }
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>解析变量名 (去除 $ 前缀) + 可选的 .Property / [index] 后缀。</summary>
    private static (string Name, string Suffix) ParseVariableAndSuffix(string text)
    {
        // 跳过变量名字符.
        var end = 0;
        while (end < text.Length && (text[end] == '?' || text[end] == '_' || char.IsLetterOrDigit(text[end]) || text[end] == ':'))
            end++;

        var name = text[..end];
        var suffix = text[end..];
        return (name, suffix);
    }

    /// <summary>从 startIdx 起读取 .Property / [index] 后缀链, 返回后缀字符串 + 下一个未消费位置。</summary>
    private static (string Suffix, int EndIdx) ReadSuffix(string input, int startIdx)
    {
        var end = startIdx;
        while (end < input.Length)
        {
            if (input[end] == '.')
            {
                // .PropertyName
                end++;
                var propStart = end;
                while (end < input.Length && (char.IsLetterOrDigit(input[end]) || input[end] == '_'))
                    end++;
                if (end == propStart) break; // 点后无字符, 不是有效后缀.
            }
            else if (input[end] == '[')
            {
                // [index] — 找到 ] 闭合.
                var close = FindClosingBrace(input, '[', ']', end);
                if (close < 0) break;
                end = close + 1;
            }
            else
            {
                break;
            }
        }
        return (input[startIdx..end], end);
    }

    /// <summary>对 baseValue 应用 .Property / [index] 后缀链。</summary>
    private static object? ApplySuffix(object? baseValue, string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return baseValue;

        var current = baseValue;
        var i = 0;
        while (i < suffix.Length)
        {
            if (suffix[i] == '.')
            {
                i++;
                var propStart = i;
                while (i < suffix.Length && (char.IsLetterOrDigit(suffix[i]) || suffix[i] == '_'))
                    i++;
                var propName = suffix[propStart..i];
                if (string.IsNullOrEmpty(propName)) break;
                current = MemberAccessor.GetProperty(current, propName);
            }
            else if (suffix[i] == '[')
            {
                var close = FindClosingBrace(suffix, '[', ']', i);
                if (close < 0) break;
                var indexText = suffix[(i + 1)..close];
                var index = ParseIndex(indexText);
                current = MemberAccessor.GetIndex(current, index);
                i = close + 1;
            }
            else
            {
                break;
            }
        }
        return current;
    }

    private static object ParseIndex(string text)
    {
        var trimmed = text.Trim();
        if (int.TryParse(trimmed, out var i)) return i;
        if (long.TryParse(trimmed, out var l)) return l;
        // 字符串索引 (去掉引号).
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
            return trimmed[1..^1];
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1];
        return trimmed;
    }

    private static int FindClosingBrace(string text, char open, char close, int startIdx = 0)
    {
        // 找第一个 open 后匹配的 close (无嵌套, 简化).
        var firstOpen = -1;
        for (var i = startIdx; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                firstOpen = i;
                break;
            }
        }
        if (firstOpen < 0) return -1;

        for (var i = firstOpen + 1; i < text.Length; i++)
        {
            if (text[i] == close) return i;
        }
        return -1;
    }

    private static bool IsValidVariableName(string name)
    {
        if (name.Length == 0) return false;
        // $env:PATH / $? / $name / $global:name 等形式.
        if (name == "?") return true;
        if (name.Contains(':'))
        {
            var parts = name.Split(':', 2);
            return IsValidIdentifier(parts[0]) && (parts.Length == 1 || IsValidIdentifier(parts[1]) || parts[1].Length > 0);
        }
        return IsValidIdentifier(name);
    }

    private static bool IsValidIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        foreach (var c in s[1..])
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    private static bool IsInDoubleQuote(string input, int pos)
    {
        // 简化: 检查 pos 之前的双引号数量是否为奇数.
        var count = 0;
        for (int i = 0; i < pos; i++)
            if (input[i] == '"') count++;
        return count % 2 == 1;
    }
}
