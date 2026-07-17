using System.Text;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Preview;

/// <summary>
/// 代码预览器。Per ADR-0030 §2.
/// 渲染前 200 行带基础语法高亮 token (keyword/comment/string 分类按扩展名)。
/// 输出 <see cref="PreviewViewModel.Code"/> 含 highlight 标记 (用 ASCII 转义, 由 GUI 端解析渲染颜色)。
/// 实现 notes:
/// <list type="bullet">
///   <item>不支持 TextMate (per ADR-0030 §2 提到 AvalonEdit / TextMate, M3 不引入重依赖)。</item>
///   <item>语法 token 用 <c>\x1b[...m</c> ANSI 转义编码: 31=keyword(红), 32=string(绿), 33=comment(黄), 0=reset。GUI 可剥离或解析渲染。</item>
///   <item>大文件仅前 200 行 (per ADR-0030 §2); 截断标记 Truncated=true。</item>
/// </list>
/// </summary>
public sealed class CodePreviewer : IPreviewer
{
    private const int MaxPreviewLines = 200;
    private const long MaxFileSize = 1 * 1024 * 1024; // 1MB

    // ANSI 转义码 (per ECMA-48): GUI 端可选择剥离或解析为 Run 颜色。
    private const string EscKeyword = "\x1b[31m";  // 红
    private const string EscString = "\x1b[32m";   // 绿
    private const string EscComment = "\x1b[33m";  // 黄
    private const string EscReset = "\x1b[0m";

    // 代码扩展名 → 语言标识 (per ADR-0030 §2 按扩展名识别语言)。
    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".py"] = "python",
        [".js"] = "javascript",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".jsx"] = "javascript",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".c"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".h"] = "c",
        [".hpp"] = "cpp",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".html"] = "html",
        [".htm"] = "html",
        [".bat"] = "bat",
        [".ps1"] = "powershell",
        [".sh"] = "shell",
        [".sql"] = "sql",
    };

    // 各语言的关键字集合 (仅常见子集, per ADR-0030 §2: 基础分类)。
    private static readonly Dictionary<string, HashSet<string>> KeywordsByLanguage = new(StringComparer.Ordinal)
    {
        ["csharp"] = new(StringComparer.Ordinal) { "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "while" },
        ["python"] = new(StringComparer.Ordinal) { "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield" },
        ["javascript"] = new(StringComparer.Ordinal) { "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "export", "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return", "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield", "let", "async", "await" },
        ["typescript"] = new(StringComparer.Ordinal) { "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "export", "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return", "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield", "let", "async", "await", "interface", "type", "enum", "as", "readonly", "public", "private", "protected", "abstract", "namespace" },
        ["go"] = new(StringComparer.Ordinal) { "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go", "goto", "if", "import", "interface", "map", "package", "range", "return", "select", "struct", "switch", "type", "var" },
        ["rust"] = new(StringComparer.Ordinal) { "as", "break", "const", "continue", "crate", "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super", "trait", "true", "type", "unsafe", "use", "where", "while", "async", "await" },
        ["java"] = new(StringComparer.Ordinal) { "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do", "double", "else", "enum", "extends", "false", "final", "finally", "float", "for", "goto", "if", "implements", "import", "instanceof", "int", "interface", "long", "native", "new", "null", "package", "private", "protected", "public", "return", "short", "static", "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "true", "try", "void", "volatile", "while" },
        ["c"] = new(StringComparer.Ordinal) { "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern", "float", "for", "goto", "if", "inline", "int", "long", "register", "restrict", "return", "short", "signed", "sizeof", "static", "struct", "switch", "typedef", "union", "unsigned", "void", "volatile", "while" },
        ["cpp"] = new(StringComparer.Ordinal) { "alignas", "alignof", "and", "auto", "bool", "break", "case", "catch", "char", "class", "const", "constexpr", "continue", "decltype", "default", "delete", "do", "double", "else", "enum", "explicit", "extern", "false", "float", "for", "friend", "goto", "if", "inline", "int", "long", "namespace", "new", "noexcept", "nullptr", "operator", "or", "private", "protected", "public", "register", "return", "short", "signed", "sizeof", "static", "struct", "switch", "template", "this", "throw", "true", "try", "typedef", "typename", "union", "unsigned", "using", "virtual", "void", "volatile", "while" },
        ["shell"] = new(StringComparer.Ordinal) { "if", "then", "else", "elif", "fi", "case", "esac", "for", "while", "until", "do", "done", "in", "function", "return", "break", "continue", "exit", "echo", "export", "local", "readonly", "set", "unset", "shift", "trap" },
        ["powershell"] = new(StringComparer.Ordinal) { "begin", "break", "catch", "class", "continue", "data", "define", "do", "dynamicparam", "else", "elseif", "end", "exit", "filter", "finally", "for", "foreach", "from", "function", "if", "in", "param", "process", "return", "switch", "throw", "try", "until", "using", "var", "while", "workflow", "scriptblock" },
        ["sql"] = new(StringComparer.Ordinal) { "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "TABLE", "VIEW", "INDEX", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "ON", "AS", "AND", "OR", "NOT", "NULL", "TRUE", "FALSE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "DISTINCT", "UNION", "ALL", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "DEFAULT", "CONSTRAINT", "CASCADE", "TRANSACTION", "COMMIT", "ROLLBACK", "BEGIN", "END" },
        ["css"] = new(StringComparer.Ordinal) { "important", "media", "screen", "print", "keyframes", "from", "to", "and", "or", "not", "only" },
    };

    // 各语言的注释起始 (line comment) — 单行注释。
    private static readonly Dictionary<string, string[]> LineCommentByLanguage = new(StringComparer.Ordinal)
    {
        ["csharp"] = new[] { "//" },
        ["javascript"] = new[] { "//" },
        ["typescript"] = new[] { "//" },
        ["java"] = new[] { "//" },
        ["c"] = new[] { "//" },
        ["cpp"] = new[] { "//" },
        ["go"] = new[] { "//" },
        ["rust"] = new[] { "//" },
        ["python"] = new[] { "#" },
        ["shell"] = new[] { "#" },
        ["powershell"] = new[] { "#" },
        ["sql"] = new[] { "--" },
        ["css"] = new[] { "//" },
    };

    private readonly Func<ItemPath, CancellationToken, Task<Stream>> _openRead;

    public CodePreviewer(Func<ItemPath, CancellationToken, Task<Stream>> openRead)
    {
        _openRead = openRead;
    }

    /// <inheritdoc />
    public bool CanPreview(IItem item)
    {
        if (item.Kind != ItemKind.File) return false;
        return LanguageMap.ContainsKey(GetExtension(item.Path));
    }

    /// <inheritdoc />
    public async ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct)
    {
        if (!CanPreview(item)) return null;

        var ext = GetExtension(item.Path);
        var language = LanguageMap[ext];
        var truncated = item.Size is { } size && size > MaxFileSize;

        Stream stream = await _openRead(item.Path, ct).ConfigureAwait(false);
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var sb = new StringBuilder();
            var previewLines = 0;
            var totalLines = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                totalLines++;
                if (previewLines < MaxPreviewLines)
                {
                    if (previewLines > 0) sb.Append('\n');
                    sb.Append(HighlightLine(line, language));
                    previewLines++;
                }
            }
            return new PreviewViewModel.Code(sb.ToString(), language, totalLines, truncated);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 对单行进行基础语法高亮 (keyword/comment/string 分类)。Per ADR-0030 §2.
    /// 用 ANSI 转义编码: keyword=31, string=32, comment=33, reset=0。
    /// </summary>
    private static string HighlightLine(string line, string language)
    {
        if (string.IsNullOrEmpty(line)) return line;

        // 1. 行注释: 整行剩余染色 (per ADR-0030 §2: comment 分类)。
        var commentStart = FindCommentStart(line, language);
        if (commentStart >= 0)
        {
            var code = line[..commentStart];
            var comment = line[commentStart..];
            return HighlightCode(code, language) + EscComment + comment + EscReset;
        }

        return HighlightCode(line, language);
    }

    /// <summary>查找行注释起始位置 (不在字符串字面量内, 简化处理)。</summary>
    private static int FindCommentStart(string line, string language)
    {
        if (!LineCommentByLanguage.TryGetValue(language, out var markers)) return -1;
        foreach (var marker in markers)
        {
            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    /// <summary>对代码部分高亮 keyword / string。简化: 一次扫描, 状态机区分 string 边界。</summary>
    private static string HighlightCode(string line, string language)
    {
        if (string.IsNullOrEmpty(line)) return line;

        var keywords = KeywordsByLanguage.TryGetValue(language, out var k) ? k : null;
        var sb = new StringBuilder(line.Length + 32);
        var i = 0;
        var sqlMode = language == "sql";
        while (i < line.Length)
        {
            var ch = line[i];

            // 字符串字面量: " ... " 或 ' ... '。
            if (ch == '"' || (ch == '\'' && !sqlMode))
            {
                var quote = ch;
                sb.Append(EscString).Append(quote);
                i++;
                while (i < line.Length)
                {
                    var c = line[i];
                    sb.Append(c);
                    if (c == '\\' && i + 1 < line.Length) { sb.Append(line[i + 1]); i += 2; continue; }
                    i++;
                    if (c == quote) break;
                }
                sb.Append(EscReset);
                continue;
            }

            // 标识符 / 关键字。
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];

                if (keywords is not null && (keywords.Contains(word) || (sqlMode && keywords.Contains(word.ToUpperInvariant()))))
                {
                    var keywordForm = sqlMode ? word.ToUpperInvariant() : word;
                    sb.Append(EscKeyword).Append(keywordForm).Append(EscReset);
                }
                else
                {
                    sb.Append(word);
                }
                continue;
            }

            // 数字字面量 (简化)。
            if (char.IsDigit(ch))
            {
                var start = i;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == 'x' || line[i] == 'X')) i++;
                sb.Append(line[start..i]);
                continue;
            }

            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static string GetExtension(ItemPath path)
    {
        var name = path.GetName();
        var idx = name.LastIndexOf('.');
        return idx >= 0 ? name[idx..] : "";
    }
}
