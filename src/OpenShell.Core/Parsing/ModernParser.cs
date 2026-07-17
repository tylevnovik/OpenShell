#nullable enable
// ADR-0050 Modern 语法（.osh）递归下降 Parser。
// 设计要点：
//   1. 与 PowerShellParser 共享同一组 AST 节点（per ADR-0050 §1.2）。
//   2. 现代运算符：== != < > <= >= && || ! ?? ?: ?. ?[ ... => (per ADR-0050 §2)。
//   3. 命令模式：行首 Identifier 后跟 ( 是函数调用，否则命令 (per ADR-0050 §8)。
//   4. 表达式优先级用 Pratt parser（binding power 表）。
//   5. 控制流语句（if/while/for/foreach/try/function/...）语法与 PS 相同。
//   6. Lambda: $x => expr / ($x, $y) => expr → LambdaExpression (per ADR-0050 §3.3)。
//   7. match: match expr { pattern => arm; _ => arm } → MatchExpression (per ADR-0050 §5.2)。
//   8. 现代 parser 不识别 PS 风格运算符（-eq -and 等）在表达式模式。
//   9. 错误信息标注 [modern] 前缀，便于定位语法模式 (per ADR-0050 §约束)。

using OpenShell.Parsing.Ast;
using System.Text;

namespace OpenShell.Parsing;

/// <summary>Modern 语法（.osh）递归下降 Parser。Per ADR-0050.</summary>
public sealed class ModernParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;
    /// <summary>原始源文本（可选，用于填充 ScriptBlockExpression.SourceText）。Per ADR-0046 §2.</summary>
    private readonly string? _source;
    /// <summary>源文件路径（可选，REPL 内为 null）。Per ADR-0046 §2.</summary>
    private readonly string? _fileName;

    /// <summary>
    /// 裸标识符作为变量解析的上下文标志。Per ADR-0050 §7.2。
    /// 在表达式上下文（非命令模式）中，裸标识符解析为 VariableExpression 而非 CommandExpression。
    /// 由 ParseStatement 在检测到 bare-identifier 表达式语句时置位。
    /// </summary>
    private bool _bareIdentifierAsVariable;

    /// <summary>
    /// 命令参数上下文标志。在 ParseCommand 解析位置参数/命名参数值时置位。
    /// 命令参数位置的裸标识符（如 cd .. 中的 ..）应作为字符串字面量传给命令，
    /// 而非作为嵌套命令调用求值（参考 PS 行为：命令参数位置所有裸标识符均为字符串）。
    /// 修复 D-306: cd ..; pwd 在 AST 路径失败（.. 被误判为命令调用）。
    /// </summary>
    private bool _inCommandArgument;

    /// <summary>解析期警告列表。Per ADR-0050 §2.2: .osh 模式下 PS 形式运算符 emit DeprecationWarning。</summary>
    private readonly List<ParseWarning> _warnings = new();

    /// <summary>TODO/FIXME/HACK 标记列表。Per ADR-0050 §9.1: 从注释中提取标记。</summary>
    private readonly List<TodoMarker> _todoMarkers = new();

    /// <summary>构造 ModernParser，传入已 tokenize 的 token 列表。</summary>
    public ModernParser(IReadOnlyList<Token> tokens, string? source = null, string? fileName = null)
    {
        _tokens = tokens;
        _pos = 0;
        _source = source;
        _fileName = fileName;
    }

    /// <summary>便捷入口：source → tokens → AST。共享 Tokenizer (per ADR-0050 §1.2)。</summary>
    public static ScriptBlockAst Parse(string source)
        => new ModernParser(new Tokenizer(source).Tokenize(), source).ParseScript();

    /// <summary>便捷入口（带源文件路径）：source → tokens → AST。Per ADR-0046 §2.</summary>
    public static ScriptBlockAst Parse(string source, string? fileName)
        => new ModernParser(new Tokenizer(source).Tokenize(), source, fileName).ParseScript();

    /// <summary>
    /// 从 <see cref="_source"/> 切片得到指定 Span 的原始文本。Per ADR-0046 §2.
    /// 若 <see cref="_source"/> 为 null（如手工构造的 token 流），返回 null。
    /// </summary>
    private string? SliceSource(SourceSpan span)
    {
        if (_source is null) return null;
        var start = span.Start.Offset;
        var end = span.End.Offset;
        if (start < 0) start = 0;
        if (end > _source.Length) end = _source.Length;
        if (end < start) return null;
        return _source.Substring(start, end - start);
    }

    // =========================================================================
    // 游标辅助（cursor helpers）
    // =========================================================================

    private Token Peek(int offset = 0)
    {
        var i = _pos + offset;
        if (i < 0) i = 0;
        if (i >= _tokens.Count) i = _tokens.Count - 1;
        return _tokens[i];
    }

    private Token Read() => _pos < _tokens.Count ? _tokens[_pos++] : _tokens[^1];

    private bool AtEnd => Peek().Kind == TokenKind.End;

    private bool Check(TokenKind k) => Peek().Kind == k;

    private bool CheckText(string text)
        => string.Equals(Peek().Text, text, StringComparison.OrdinalIgnoreCase);

    private bool CheckKeyword(string kw)
        => Check(TokenKind.Keyword) && CheckText(kw);

    private bool Match(TokenKind k)
    {
        if (Check(k)) { _pos++; return true; }
        return false;
    }

    private bool MatchKeyword(string kw)
    {
        if (CheckKeyword(kw)) { _pos++; return true; }
        return false;
    }

    private Token Expect(TokenKind k, string what)
    {
        if (!Check(k))
            throw new ParserException(Peek().Span, $"[modern] expected {what}, got {Peek().Kind} '{Peek().Text}'");
        return Read();
    }

    private SourceSpan SpanFrom(SourcePosition start)
        => new(start, Peek().Span.End);

    /// <summary>跳过语句分隔符（NewLine / Semicolon / 注释）。</summary>
    private void SkipSeparators()
    {
        while (Check(TokenKind.NewLine) || Check(TokenKind.Semicolon)
               || Check(TokenKind.LineComment) || Check(TokenKind.BlockComment)
               || Check(TokenKind.LangDirective))
        {
            // ADR-0050 §1.3: #lang ps1/osh { ... } 是块切换语句，不跳过——由 ParseStatement 处理。
            // 仅跳过不含 { 的 #lang 指令（如 REPL 切换 #lang ps1）。
            if (Check(TokenKind.LangDirective) && Peek().Text.Contains('{'))
                break;
            _pos++;
        }
    }

    /// <summary>仅跳过换行和注释（保留分号）。</summary>
    private void SkipNewLinesAndComments()
    {
        while (Check(TokenKind.NewLine) || Check(TokenKind.LineComment) || Check(TokenKind.BlockComment)
               || Check(TokenKind.LangDirective))
        {
            if (Check(TokenKind.LangDirective) && Peek().Text.Contains('{'))
                break;
            _pos++;
        }
    }

    // =========================================================================
    // 顶层入口
    // =========================================================================

    /// <summary>解析整个脚本。可选 param() 块 + 语句列表（与 PS 相同语法）。</summary>
    public ScriptBlockAst ParseScript()
    {
        var start = Peek().Span.Start;

        // ADR-0050 §9.1: 预扫描所有注释，提取 TODO/FIXME/HACK 标记。
        ExtractTodoMarkersFromComments();

        SkipSeparators();

        var parameters = new List<ParameterDeclaration>();
        if (MatchKeyword("param"))
            parameters = ParseParamParenList();

        var statements = new List<Statement>();
        while (!AtEnd)
        {
            SkipSeparators();
            if (AtEnd) break;
            var stmt = ParseStatement();
            if (stmt is not null) statements.Add(stmt);
            // 语句后必须有分隔符，否则错误恢复
            if (!Check(TokenKind.NewLine) && !Check(TokenKind.Semicolon) && !AtEnd)
                _pos++;
            SkipSeparators();
        }

        return new ScriptBlockAst(
            statements, parameters, SpanFrom(start),
            ParseWarnings: _warnings.Count > 0 ? _warnings : null,
            TodoMarkers: _todoMarkers.Count > 0 ? _todoMarkers : null);
    }

    /// <summary>
    /// 预扫描 token 列表中的注释，提取 TODO/FIXME/HACK/NOTE 标记。Per ADR-0050 §9.1.
    /// 格式：`# TODO: message` / `# FIXME: message` / `# HACK: message` / `# NOTE: message`。
    /// 也支持块注释 `&lt;# TODO: message #&gt;`。
    /// </summary>
    private void ExtractTodoMarkersFromComments()
    {
        foreach (var tok in _tokens)
        {
            if (tok.Kind is not (TokenKind.LineComment or TokenKind.BlockComment)) continue;
            ExtractTodoMarkerFromText(tok.Text, tok.Span);
        }
    }

    /// <summary>从注释文本中提取 TODO/FIXME/HACK/NOTE 标记。</summary>
    private void ExtractTodoMarkerFromText(string text, SourceSpan span)
    {
        // 块注释去掉 <# #> 包裹
        var body = text;
        if (body.StartsWith("<#")) body = body[2..];
        if (body.EndsWith("#>")) body = body[..^2];

        var lines = body.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart(' ', '\t', '\r');
            // 跳过开头的 # 或 <#
            if (line.StartsWith("#")) line = line[1..].TrimStart(' ');
            else if (line.StartsWith("<#")) line = line[2..].TrimStart(' ');

            TodoMarkerKind kind;
            if (line.StartsWith("TODO", StringComparison.OrdinalIgnoreCase))
                kind = TodoMarkerKind.Todo;
            else if (line.StartsWith("FIXME", StringComparison.OrdinalIgnoreCase))
                kind = TodoMarkerKind.Fixme;
            else if (line.StartsWith("HACK", StringComparison.OrdinalIgnoreCase))
                kind = TodoMarkerKind.Hack;
            else if (line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase))
                kind = TodoMarkerKind.Note;
            else continue;

            // 提取冒号后的消息
            var rest = line.Substring(Enum.GetName(typeof(TodoMarkerKind), kind)!.Length);
            if (rest.StartsWith(":")) rest = rest[1..];
            var msg = rest.Trim();
            _todoMarkers.Add(new TodoMarker(kind, msg, span));
        }
    }

    // =========================================================================
    // Statement 分发
    // =========================================================================

    private Statement? ParseStatement()
    {
        var start = Peek().Span.Start;

        // ADR-0050 §1.3: #lang ps1/osh { ... } 块切换指令。
        if (Check(TokenKind.LangDirective) && Peek().Text.Contains('{'))
        {
            return ParseLangBlock();
        }

        // ADR-0050 §5.1: :label 语句标签声明（用于 break/continue label）。
        if (Check(TokenKind.Label))
        {
            var labelTok = Read();
            var labelName = labelTok.Text.TrimStart(':');
            var body = ParseStatement()
                ?? throw new ParserException(labelTok.Span, "[modern] label must be followed by a statement");
            // 若体部为循环，将标签下放到循环节点，使其能内部匹配 continue label。
            body = AttachLoopLabel(body, labelName);
            return new LabeledStatement(labelName, body, SpanFrom(start));
        }

        // 控制流关键字（语法与 PS 相同）
        if (CheckKeyword("if")) return ParseIf();
        if (CheckKeyword("switch")) return ParseSwitch();
        if (CheckKeyword("while")) return ParseWhile();
        if (CheckKeyword("do")) return ParseDoWhile();
        if (CheckKeyword("for")) return ParseFor();
        if (CheckKeyword("foreach")) return ParseForEach();
        if (CheckKeyword("try")) return ParseTry();
        if (CheckKeyword("function") || CheckKeyword("filter")) return ParseFunctionDefinition();
        if (CheckKeyword("fn")) return ParseFnDefinition();
        if (CheckKeyword("return")) return ParseReturn();
        if (CheckKeyword("break")) { _pos++; var label = MatchLabel(); return new BreakStatement(label, SpanFrom(start)); }
        if (CheckKeyword("continue")) { _pos++; var label = MatchLabel(); return new ContinueStatement(label, SpanFrom(start)); }
        if (CheckKeyword("throw")) return ParseThrow();
        if (CheckKeyword("exit")) return ParseExit();
        if (CheckKeyword("param")) { _pos++; var ps = ParseParamParenList(); return new ParamBlockStatement(ps, SpanFrom(start)); }
        if (CheckKeyword("using")) return ParseUsing();
        // ADR-0050 §10.1: import "file" 模块加载指令 (modern 语法独有, 等价 using module + 文件加载)。
        if (CheckKeyword("import")) return ParseImport(start);
        // ADR-0051 §1/§3: async fn ... / async { ... }
        if (CheckKeyword("async")) return ParseAsyncConstruct(start);
        // ADR-0056 §1: export fn / export const / export default
        if (CheckKeyword("export")) return ParseExport(start);

        // ADR-0053 §2: macro_rules! name { (pattern) => { expansion } ... }
        if (Check(TokenKind.Identifier) && CheckText("macro_rules") && Peek(1).Kind == TokenKind.Bang)
            return ParseMacroDefinition(start);

        // ADR-0057 §3: type Name { field; method() { } }
        if (Check(TokenKind.Identifier) && CheckText("type") && Peek(1).Kind == TokenKind.Identifier)
            return ParseTypeDefinition(start);

        // match 表达式作为语句（modern match expression, per ADR-0050 §5.2）
        if (CheckKeyword("match")) return ParseExpressionStatement(start);

        // & 调用运算符 / . dot-source → 命令模式
        if (Check(TokenKind.Ampersand) || Check(TokenKind.DotSource))
            return ParsePipelineStatement(start);

        // $variable → 可能 lambda，可能赋值，可能表达式语句，可能类型化变量声明（$p: int = 50）
        if (Check(TokenKind.Variable) || Check(TokenKind.ScopedVariable) || Check(TokenKind.EnvVariable))
        {
            // 检测 lambda: $x => ... (modern, per ADR-0050 §3.3)
            if (IsLambdaAhead())
                return ParseExpressionStatement(start);

            // ADR-0050 §7.1/§7.2: $var: Type [@Attr(args)]... = value 类型化变量声明
            if (Check(TokenKind.Variable) && Peek(1).Kind == TokenKind.Colon)
                return ParseVariableDeclaration(start);

            // 解析完整表达式（含二元运算符 + 末尾赋值）
            var expr = ParseExpression();
            if (expr is AssignmentExpression ae)
                return new AssignmentStatement(ae.Target, ae.Operator, ae.Value, ae.Span);
            return WrapAsPipelineStatement(expr, start);
        }

        // 标识符 → 可能 lambda，可能命令模式，可能 bare-identifier 表达式（Per ADR-0050 §7.2）
        if (Check(TokenKind.Identifier))
        {
            // 检测 lambda: x => ... (modern bare-identifier lambda)
            if (IsLambdaAhead())
                return ParseExpressionStatement(start);
            // ADR-0050 §7.2: 裸标识符后跟二元运算符 / 赋值 / 后缀 ++ / -- → 表达式语句
            if (IsBareIdentifierExpressionAhead())
            {
                _bareIdentifierAsVariable = true;
                try
                {
                    var expr = ParseExpression();
                    if (expr is AssignmentExpression ae)
                        return new AssignmentStatement(ae.Target, ae.Operator, ae.Value, ae.Span);
                    return WrapAsPipelineStatement(expr, start);
                }
                finally
                {
                    _bareIdentifierAsVariable = false;
                }
            }
            return ParsePipelineStatement(start);
        }

        // (params) => ... lambda (modern, per ADR-0050 §3.3)
        if (Check(TokenKind.LParen) && IsLambdaAhead())
            return ParseExpressionStatement(start);

        // ADR-0050 §9.2: 三引号字符串在语句位置且后跟声明（fn/function/filter/type）→ 文档注释节点。
        if (Check(TokenKind.String) && IsTripleQuotedStringToken(Peek()) && IsDocCommentAhead())
        {
            var tok = Read();
            return new DocumentationCommentStatement(tok.Text, tok.Span);
        }

        // 表达式起始 token → 表达式语句（可能赋值）
        if (IsExpressionStartToken(Peek().Kind))
        {
            var expr = ParseExpression();
            if (expr is AssignmentExpression ae)
                return new AssignmentStatement(ae.Target, ae.Operator, ae.Value, ae.Span);
            // 表达式后跟随 | ：表达式作为管道头（如 1..5 | & { process { $_ } }）。
            if (Check(TokenKind.Pipe))
                return BuildPipelineFromExpressionHead(expr, start);
            return WrapAsPipelineStatement(expr, start);
        }

        // 未知：错误恢复
        throw new ParserException(Peek().Span, $"[modern] unexpected token {Peek().Kind} '{Peek().Text}' at statement start");
    }

    /// <summary>
    /// 检测前向是否为 bare-identifier 表达式语句：identifier 后跟二元运算符 / 赋值 / 后缀 ++ --。
    /// Per ADR-0050 §7.2: 现代语法允许无 $ 前缀变量，`a + b` 等价 `$a + $b`。
    /// 启发式：避免误判命令模式（如 `get-childitem -path`）。
    /// </summary>
    private bool IsBareIdentifierExpressionAhead()
    {
        if (!Check(TokenKind.Identifier)) return false;
        var next = Peek(1).Kind;
        return next is TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash
            or TokenKind.Percent or TokenKind.Equals or TokenKind.NotEquals
            or TokenKind.Lt or TokenKind.Gt or TokenKind.Le or TokenKind.Ge
            or TokenKind.AmpAmp or TokenKind.PipePipe
            or TokenKind.PlusPlus or TokenKind.MinusMinus
            or TokenKind.Assign  // = 赋值
            or TokenKind.PlusAssign or TokenKind.MinusAssign;
    }

    /// <summary>
    /// 检测 token 是否为三引号字符串（"""..."""）。Per ADR-0050 §9.2/§6.2.
    /// Tokenizer 将三引号字符串产为 TokenKind.String（与普通 "..." 相同），
    /// 需检查源文本起始是否为 """。_source 为 null（手工 token 流）时返回 false。
    /// </summary>
    private bool IsTripleQuotedStringToken(Token tok)
    {
        if (_source is null) return false;
        var offset = tok.Span.Start.Offset;
        if (offset < 0 || offset + 2 >= _source.Length) return false;
        return _source[offset] == '"' && _source[offset + 1] == '"' && _source[offset + 2] == '"';
    }

    /// <summary>
    /// 检测当前三引号字符串后是否跟随声明关键字（fn/function/filter/type）。
    /// Per ADR-0050 §9.2: 仅当三引号字符串位于声明顶部时才识别为文档注释。
    /// 跳过换行/注释后检查下一个 token 是否为声明关键字。
    /// </summary>
    private bool IsDocCommentAhead()
    {
        int i = _pos + 1; // 跳过当前 String token
        while (i < _tokens.Count)
        {
            var k = _tokens[i].Kind;
            if (k is TokenKind.NewLine or TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Semicolon)
            {
                i++;
                continue;
            }
            // 检查是否为声明关键字
            if (k == TokenKind.Keyword)
            {
                var text = _tokens[i].Text;
                return string.Equals(text, "fn", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "function", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "filter", StringComparison.OrdinalIgnoreCase);
            }
            // type Name { ... } — type 是 Identifier（非关键字）
            if (k == TokenKind.Identifier && string.Equals(_tokens[i].Text, "type", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        return false;
    }

    /// <summary>检测前向是否为 lambda：$var => / identifier => / (params) => (per ADR-0050 §3.3)。</summary>
    private bool IsLambdaAhead()
    {
        int i = _pos;
        if (i >= _tokens.Count) return false;

        var k = _tokens[i].Kind;

        // 形式 1: $var => ... (Variable followed by Arrow)
        if (k == TokenKind.Variable || k == TokenKind.ScopedVariable || k == TokenKind.EnvVariable)
            return i + 1 < _tokens.Count && _tokens[i + 1].Kind == TokenKind.Arrow;

        // 形式 2: identifier => ... (Identifier followed by Arrow)
        if (k == TokenKind.Identifier)
            return i + 1 < _tokens.Count && _tokens[i + 1].Kind == TokenKind.Arrow;

        // 形式 3: (params) => ... (LParen 匹配 RParen 后跟 Arrow)
        if (k == TokenKind.LParen)
        {
            int depth = 1;
            i++;
            while (i < _tokens.Count && depth > 0)
            {
                var kk = _tokens[i].Kind;
                if (kk == TokenKind.LParen) depth++;
                else if (kk == TokenKind.RParen) depth--;
                else if (kk == TokenKind.End) return false;
                i++;
            }
            return i < _tokens.Count && _tokens[i].Kind == TokenKind.Arrow;
        }

        return false;
    }

    /// <summary>解析表达式语句并包装为 PipelineStatement。</summary>
    private Statement ParseExpressionStatement(SourcePosition start)
    {
        var expr = ParseExpression();
        return WrapAsPipelineStatement(expr, start);
    }

    /// <summary>把表达式包装为 ExpressionStatement（直接返回表达式的值）。</summary>
    private static Statement WrapAsPipelineStatement(Expression expr, SourcePosition start)
        => new ExpressionStatement(expr, new SourceSpan(start, expr.Span.End));

    private static bool IsExpressionStartToken(TokenKind k) =>
        k is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace
            or TokenKind.Integer or TokenKind.Double or TokenKind.String or TokenKind.SingleString
            or TokenKind.HereString or TokenKind.HereSingleString or TokenKind.RawString
            or TokenKind.Boolean or TokenKind.Null
            or TokenKind.At or TokenKind.TypeRef
            or TokenKind.Bang or TokenKind.BitNot or TokenKind.Plus or TokenKind.Minus
            or TokenKind.PlusPlus or TokenKind.MinusMinus
            or TokenKind.Spread;

    /// <summary>把表达式转换为赋值目标（仅支持 $var / $obj.Prop / $arr[i]）。</summary>
    private AssignTarget ExprToAssignTarget(Expression expr, SourcePosition start)
    {
        switch (expr)
        {
            case VariableExpression v:
                return new VariableTarget(v.Name, expr.Span);
            case MemberExpression m:
                return new MemberTarget(m.Target, m.MemberName, m.Static, expr.Span);
            case IndexExpression idx:
                return new IndexTarget(idx.Target, idx.Index, expr.Span);
            default:
                throw new ParserException(expr.Span, "[modern] invalid assignment target");
        }
    }

    /// <summary>判断表达式是否为合法的赋值目标（$var / $obj.Prop / $arr[i]）。</summary>
    private static bool IsAssignTarget(Expression expr)
        => expr is VariableExpression or MemberExpression or IndexExpression;

    private string? MatchLabel()
    {
        // ADR-0050 §5.1: modern 语法允许 break label（去掉 PS 的 : 前缀）。
        // modern 风格：紧邻 Identifier（不跳过换行，避免误消费下一语句的命令名）
        if (Check(TokenKind.Identifier))
        {
            var t = Read();
            return t.Text;
        }
        // PS 风格：:label（允许换行后跟）
        SkipNewLinesAndComments();
        if (Check(TokenKind.Label))
        {
            var t = Read();
            return t.Text.TrimStart(':');
        }
        return null;
    }

    /// <summary>
    /// 将标签下放到循环节点（While/DoWhile/For/ForEach），使循环能内部匹配 continue label。
    /// 非循环节点原样返回。Per ADR-0050 §5.1.
    /// </summary>
    private static Statement AttachLoopLabel(Statement stmt, string label) => stmt switch
    {
        WhileStatement w => w with { Label = label },
        DoWhileStatement dw => dw with { Label = label },
        ForStatement f => f with { Label = label },
        ForEachStatement fe => fe with { Label = label },
        _ => stmt
    };

    // =========================================================================
    // Pipeline 语句（modern 命令模式）
    // =========================================================================

    private Statement ParsePipelineStatement(SourcePosition start)
    {
        var commands = new List<CommandExpression>();
        commands.Add(ParseCommand());

        // 管道续接：| 后可跨行
        while (Check(TokenKind.Pipe))
        {
            _pos++;
            SkipNewLinesAndComments();
            commands.Add(ParseCommand());
        }

        bool background = false;
        if (Check(TokenKind.Background))
        {
            _pos++;
            background = true;
        }
        else if (Check(TokenKind.Ampersand) && (Peek(1).Kind == TokenKind.NewLine || Peek(1).Kind == TokenKind.End))
        {
            _pos++;
            background = true;
        }

        // D-322: 消费 > file 重定向语法。
        ConsumeRedirectionIfPresent();

        var pipe = new PipelineExpression(commands, SpanFrom(start));
        return new PipelineStatement(pipe, background, SpanFrom(start));
    }

    /// <summary>
    /// D-322: 消费 > file 重定向语法（解析后丢弃，重定向由 host 层处理）。
    /// 在 ParsePipelineStatement / BuildPipelineFromExpressionHead 中调用。
    /// </summary>
    private void ConsumeRedirectionIfPresent()
    {
        if (Check(TokenKind.Gt))
        {
            _pos++; // 消费 >
            // 消费重定向目标（标识符或字符串）
            if (Check(TokenKind.Identifier) || Check(TokenKind.String) || Check(TokenKind.SingleString))
            {
                _pos++;
            }
        }
    }
    private Statement BuildPipelineFromExpressionHead(Expression headExpr, SourcePosition start)
    {
        var headCmd = new CommandExpression(
            Name: "__expr__",
            Arguments: Array.Empty<CommandArgument>(),
            Kind: CommandInvocationKind.Direct,
            Span: headExpr.Span,
            Block: null,
            HeadExpression: headExpr);

        var commands = new List<CommandExpression> { headCmd };
        while (Check(TokenKind.Pipe))
        {
            _pos++;
            SkipNewLinesAndComments();
            commands.Add(ParseCommand());
        }

        bool background = false;
        if (Check(TokenKind.Background))
        {
            _pos++;
            background = true;
        }
        else if (Check(TokenKind.Ampersand) && (Peek(1).Kind == TokenKind.NewLine || Peek(1).Kind == TokenKind.End))
        {
            _pos++;
            background = true;
        }

        // D-322: 消费 > file 重定向语法。
        ConsumeRedirectionIfPresent();

        var pipe = new PipelineExpression(commands, SpanFrom(start));
        return new PipelineStatement(pipe, background, SpanFrom(start));
    }

    private CommandExpression ParseCommand()
    {
        var start = Peek().Span.Start;
        CommandInvocationKind kind = CommandInvocationKind.Direct;
        string name;
        ScriptBlockExpression? blockExpr = null;

        if (Check(TokenKind.Ampersand))
        {
            _pos++;
            kind = CommandInvocationKind.CallOperator;
            // & 后跟字符串/变量/标识符作为命令名
            var nameExpr = ParsePrimary();
            switch (nameExpr)
            {
                case LiteralExpression lit:
                    name = lit.Value?.ToString() ?? "";
                    break;
                case VariableExpression v:
                    name = v.Name;
                    break;
                case ScriptBlockExpression sb:
                    // & { ... } 直接调用脚本块字面量。
                    name = "__call__";
                    blockExpr = sb;
                    break;
                default:
                    name = "__call__";
                    break;
            }
        }
        else if (Check(TokenKind.DotSource))
        {
            _pos++;
            kind = CommandInvocationKind.DotSource;
            var nameExpr = ParsePrimary();
            name = nameExpr switch
            {
                LiteralExpression lit => lit.Value?.ToString() ?? "",
                VariableExpression v => v.Name,
                _ => "__dot__",
            };
        }
        else if (Check(TokenKind.Identifier))
        {
            name = Read().Text;
        }
        else
        {
            throw new ParserException(Peek().Span, $"[modern] expected command name, got {Peek().Kind}");
        }

        var args = new List<CommandArgument>();

        // modern 函数调用语法：foo(arg1, arg2) / foo(name: value) (per ADR-0050 §8.2)
        if (Check(TokenKind.LParen))
        {
            _pos++; // (
            // ADR-0050 §8.2: cmd(name: value) 关键字参数简写——等价 cmd -name value。
            // 检测：identifier 后紧跟 Colon（非 ::），判定为命名参数。
            var namedArgs = new List<CommandArgument>();
            SkipNewLinesAndComments();
            if (!Check(TokenKind.RParen))
            {
                while (true)
                {
                    SkipNewLinesAndComments();
                    // 检测 name: value 形式
                    if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon)
                    {
                        var nameTok = Read();
                        _pos++; // 消费 ':'
                        SkipNewLinesAndComments();
                        var val = ParseExpression();
                        namedArgs.Add(new NamedArgument(nameTok.Text, val, nameTok.Span));
                    }
                    else
                    {
                        var argExpr = ParseExpression();
                        namedArgs.Add(new PositionalArgument(argExpr, argExpr.Span));
                    }
                    SkipNewLinesAndComments();
                    if (!Match(TokenKind.Comma)) break;
                    SkipNewLinesAndComments();
                }
            }
            Expect(TokenKind.RParen, "')'");
            args.AddRange(namedArgs);
        }

        // 继续解析命令风格参数（space-separated, 与 PS 一致）
        while (true)
        {
            // D-321: 不在参数循环起始处跳过换行——换行是语句分隔符，
            // 若跳过则 mkdir foo\ncd bar 会被合并为单条命令 mkdir foo cd bar。
            // 仅跳过行注释（行注释本身含换行语义，不影响语句边界）。
            while (Check(TokenKind.LineComment) || Check(TokenKind.BlockComment))
            {
                _pos++;
            }
            if (AtEnd) break;
            if (Check(TokenKind.Pipe) || Check(TokenKind.Semicolon) || Check(TokenKind.NewLine)
                || Check(TokenKind.RBrace) || Check(TokenKind.RParen) || Check(TokenKind.RBracket))
                break;
            // D-322: > 重定向操作符结束命令参数解析（由上层处理重定向目标）。
            if (Check(TokenKind.Gt)) break;
            if (Check(TokenKind.Background)) break;
            if (Check(TokenKind.Ampersand) && (Peek(1).Kind == TokenKind.NewLine || Peek(1).Kind == TokenKind.End)) break;

            // 命名参数 -Name / -Name:value / switch -Recurse
            if (Check(TokenKind.NamedParameter))
            {
                var tok = Read();
                SkipNewLinesAndComments();
                // D-306: 命令参数位置的裸标识符作为字符串字面量，而非命令调用。
                _inCommandArgument = true;
                Expression val;
                try { val = ParseArgumentExpression(); }
                finally { _inCommandArgument = false; }
                args.Add(new NamedArgument(tok.Value?.ToString() ?? tok.Text, val, tok.Span));
                continue;
            }
            if (Check(TokenKind.SwitchParameter))
            {
                var tok = Read();
                args.Add(new SwitchArgument(tok.Value?.ToString() ?? tok.Text.TrimStart('-'), tok.Span));
                continue;
            }

            // 脚本块参数 { ... }
            if (Check(TokenKind.LBrace))
            {
                var block = ParseScriptBlockExpression();
                args.Add(new ScriptBlockArgument(block, block.Span));
                continue;
            }

            // 位置参数
            if (IsArgumentStartToken(Peek().Kind))
            {
                // D-306: 命令参数位置的裸标识符作为字符串字面量，而非命令调用。
                _inCommandArgument = true;
                Expression argExpr;
                try { argExpr = ParseArgumentExpression(); }
                finally { _inCommandArgument = false; }
                args.Add(new PositionalArgument(argExpr, argExpr.Span));
                continue;
            }

            // D-312: 单独的 . token（TokenKind.Dot）在命令参数位置应作为字符串字面量 "."。
            // Tokenizer 将单个 . 词法化为 Dot（用于成员访问 $.Name），不进入 IsArgumentStartToken。
            // 命令参数位置（如 cd .）需要将其作为 "." 字符串参数传递。
            if (Check(TokenKind.Dot))
            {
                var dotTok = Read();
                args.Add(new PositionalArgument(
                    new LiteralExpression(".", LiteralKind.String, dotTok.Span),
                    dotTok.Span));
                continue;
            }

            break;
        }

        return new CommandExpression(name, args, kind, SpanFrom(start), blockExpr);
    }

    private static bool IsArgumentStartToken(TokenKind k) =>
        k is TokenKind.Variable or TokenKind.ScopedVariable or TokenKind.EnvVariable
            or TokenKind.Integer or TokenKind.Double or TokenKind.String or TokenKind.SingleString
            or TokenKind.HereString or TokenKind.HereSingleString or TokenKind.RawString
            or TokenKind.Boolean or TokenKind.Null
            or TokenKind.LParen or TokenKind.At or TokenKind.TypeRef or TokenKind.Identifier
            or TokenKind.Minus or TokenKind.Plus or TokenKind.Spread;

    /// <summary>命令参数表达式：postfix 级别（避免与运算符冲突）。</summary>
    private Expression ParseArgumentExpression() => ParsePostfixExpr();

    // =========================================================================
    // 控制流语句（语法与 PS 相同）
    // =========================================================================

    private Statement ParseIf()
    {
        var start = Peek().Span.Start;
        _pos++; // if
        var branches = new List<ConditionalBody>();
        var cond = ParseCondition();
        var body = ParseBlock();
        branches.Add(new ConditionalBody(cond, body));

        while (true)
        {
            // 保存位置：若无 elseif/else 则恢复，避免消费后续语句的换行分隔符。
            var savedPos = _pos;
            SkipNewLinesAndComments();
            if (MatchKeyword("elseif") || MatchKeyword("elif"))
            {
                var ec = ParseCondition();
                var eb = ParseBlock();
                branches.Add(new ConditionalBody(ec, eb));
                continue;
            }
            if (MatchKeyword("else"))
            {
                SkipNewLinesAndComments();
                // else if 链
                if (CheckKeyword("if"))
                {
                    var nested = ParseIf();
                    return new IfStatement(branches, new[] { nested }, SpanFrom(start));
                }
                var eb = ParseBlock();
                return new IfStatement(branches, eb, SpanFrom(start));
            }
            // 无 elseif/else：恢复位置，不消费换行
            _pos = savedPos;
            break;
        }
        return new IfStatement(branches, null, SpanFrom(start));
    }

    private Statement ParseSwitch()
    {
        var start = Peek().Span.Start;
        _pos++; // switch

        var flags = SwitchFlags.None;
        // switch 标志：-wildcard -regex -case -file
        while (Check(TokenKind.SwitchParameter) || Check(TokenKind.NamedParameter))
        {
            var t = Read();
            var name = (t.Value?.ToString() ?? t.Text).ToLowerInvariant();
            flags |= name switch
            {
                "wildcard" => SwitchFlags.Wildcard,
                "regex" => SwitchFlags.Regex,
                "case" or "casesensitive" => SwitchFlags.CaseSensitive,
                "file" => SwitchFlags.File,
                _ => SwitchFlags.None,
            };
        }

        var test = ParseParenExpression();
        Expect(TokenKind.LBrace, "'{'");
        SkipNewLinesAndComments();

        var cases = new List<SwitchCase>();
        List<Statement>? defaultBody = null;

        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            SkipNewLinesAndComments();
            if (Check(TokenKind.RBrace)) break;

            Expression pattern;
            if (MatchKeyword("default"))
            {
                Expect(TokenKind.LBrace, "'{'");
                var db = ParseBlockStatements();
                defaultBody = db;
                continue;
            }
            else
            {
                pattern = ParsePatternExpression();
            }
            Expect(TokenKind.LBrace, "'{'");
            var cbody = ParseBlockStatements();
            cases.Add(new SwitchCase(pattern, cbody));
            SkipNewLinesAndComments();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new SwitchStatement(test, cases, defaultBody, flags, SpanFrom(start));
    }

    private Expression ParsePatternExpression()
    {
        // switch 模式：字符串/数字/变量/类型
        if (Check(TokenKind.TypeRef))
        {
            var t = Read();
            var typeRef = ParseTypeRefText(t.Text);
            return new CastExpression(typeRef, new VariableExpression("_", VariableScopeKind.Default, t.Span), t.Span);
        }
        return ParsePrimary();
    }

    private Statement ParseWhile()
    {
        var start = Peek().Span.Start;
        _pos++; // while
        var cond = ParseCondition();
        var body = ParseBlock();
        return new WhileStatement(cond, body, SpanFrom(start));
    }

    private Statement ParseDoWhile()
    {
        var start = Peek().Span.Start;
        _pos++; // do
        var body = ParseBlock();
        SkipNewLinesAndComments();
        bool until = false;
        if (MatchKeyword("while")) until = false;
        else if (MatchKeyword("until")) until = true;
        else throw new ParserException(Peek().Span, "[modern] expected 'while' or 'until' after do-block");
        var cond = ParseCondition();
        return new DoWhileStatement(body, cond, until, SpanFrom(start));
    }

    private Statement ParseFor()
    {
        var start = Peek().Span.Start;
        _pos++; // for

        // ADR-0050 §5.3: for $x in col / for $k, $v in hash 合并 foreach 形式。
        // 检测：for 后紧跟 $var (in) 或 $k, $v (in) 或 ( $var in ... ) 括号形式。
        if (TryParseForIn(out var feStmt))
            return feStmt!;

        Expect(TokenKind.LParen, "'('");
        SkipNewLinesAndComments();
        Expression? init = null;
        if (!Check(TokenKind.Semicolon)) init = ParseExpression();
        Expect(TokenKind.Semicolon, "';'");
        SkipNewLinesAndComments();
        Expression? cond = null;
        if (!Check(TokenKind.Semicolon)) cond = ParseExpression();
        Expect(TokenKind.Semicolon, "';'");
        SkipNewLinesAndComments();
        Expression? iter = null;
        if (!Check(TokenKind.RParen)) iter = ParseExpression();
        Expect(TokenKind.RParen, "')'");
        var body = ParseBlock();
        return new ForStatement(init, cond, iter, body, SpanFrom(start));
    }

    /// <summary>
    /// 尝试解析 for-in 形式：`for $x in col { }` / `for $k, $v in hash { }` /
    /// `for ($x in col) { }`（兼容 PS 括号形式）。Per ADR-0050 §5.3。
    /// 成功返回 true 并填充 stmt；非 for-in 形式返回 false（不消费 token）。
    /// </summary>
    private bool TryParseForIn(out Statement? stmt)
    {
        stmt = null;
        var savedPos = _pos;

        // 可选括号
        var hasParen = Check(TokenKind.LParen);
        if (hasParen) _pos++;
        SkipNewLinesAndComments();

        // 必须以 $var 开头
        if (!Check(TokenKind.Variable) && !Check(TokenKind.ScopedVariable) && !Check(TokenKind.EnvVariable))
        {
            _pos = savedPos;
            return false;
        }

        // 收集一个或两个变量名
        var firstVarTok = Read();
        var firstName = firstVarTok.Value?.ToString() ?? firstVarTok.Text;
        string? secondName = null;

        SkipNewLinesAndComments();
        if (Match(TokenKind.Comma))
        {
            SkipNewLinesAndComments();
            if (!Check(TokenKind.Variable) && !Check(TokenKind.ScopedVariable) && !Check(TokenKind.EnvVariable))
            {
                _pos = savedPos;
                return false;
            }
            var secondVarTok = Read();
            secondName = secondVarTok.Value?.ToString() ?? secondVarTok.Text;
            SkipNewLinesAndComments();
        }

        // 必须 in 关键字
        if (!MatchKeyword("in"))
        {
            _pos = savedPos;
            return false;
        }

        SkipNewLinesAndComments();
        var iterable = ParseExpression();
        if (hasParen)
        {
            SkipNewLinesAndComments();
            Expect(TokenKind.RParen, "')'");
        }
        var body = ParseBlock();

        if (secondName is null)
        {
            stmt = new ForEachStatement(ForEachKind.Item, firstName, iterable, body, SpanFrom(savedPos > 0 ? _tokens[savedPos].Span.Start : Peek().Span.Start));
        }
        else
        {
            stmt = new ForEachStatement(ForEachKind.KeyValuePair, firstName, iterable, body, SpanFrom(savedPos > 0 ? _tokens[savedPos].Span.Start : Peek().Span.Start))
            {
                // 用 KeyValueNames 携带第二个变量名（key, value）
                KeyValueNames = (firstName, secondName),
            };
        }
        return true;
    }

    private Statement ParseForEach()
    {
        var start = Peek().Span.Start;
        _pos++; // foreach
        Expect(TokenKind.LParen, "'('");
        SkipNewLinesAndComments();
        Expect(TokenKind.Variable, "variable");
        var varName = _tokens[_pos - 1].Value?.ToString() ?? _tokens[_pos - 1].Text;
        SkipNewLinesAndComments();
        if (!MatchKeyword("in"))
            throw new ParserException(Peek().Span, "[modern] expected 'in' in foreach");
        SkipNewLinesAndComments();
        var iterable = ParseExpression();
        Expect(TokenKind.RParen, "')'");
        var body = ParseBlock();
        return new ForEachStatement(ForEachKind.Item, varName, iterable, body, SpanFrom(start));
    }

    private Statement ParseTry()
    {
        var start = Peek().Span.Start;
        _pos++; // try
        var body = ParseBlock();
        var catches = new List<CatchClause>();
        List<Statement>? finallyBody = null;

        while (true)
        {
            // 保存位置，若不是 catch 则回退（不消费 catch 块后的换行/注释，
            // 避免吞掉后续语句的首 token，Per T-205 修复）。
            var savedPos = _pos;
            SkipNewLinesAndComments();
            if (!CheckKeyword("catch"))
            {
                _pos = savedPos;
                break;
            }
            _pos++; // catch
            SkipNewLinesAndComments();

            var types = new List<TypeReference>();
            string? varName = null;

            // ADR-0050 §5.4 现代绑定: catch e: Type1, Type2 { }
            // 形式：catch <identifier> : <Type> [, <Type>]* { }，Type 可为 [TypeRef] 或 dotted 名（System.Exception）
            if (Check(TokenKind.Identifier) && !CheckKeyword("finally"))
            {
                var idTok = Read();
                if (Match(TokenKind.Colon))
                {
                    // 现代绑定：e: Type
                    varName = idTok.Text;
                    SkipNewLinesAndComments();
                    types.AddRange(ParseCatchTypeList());
                }
                else
                {
                    // 不是现代绑定——回退（不消费 identifier）
                    _pos--;
                }
            }

            // PS 风格：catch [Type1] [Type2] as $ex
            if (types.Count == 0 && varName is null)
            {
                while (Check(TokenKind.TypeRef))
                {
                    var t = Read();
                    types.Add(ParseTypeRefText(t.Text));
                    SkipNewLinesAndComments();
                }
                if (MatchKeyword("as"))
                {
                    if (Check(TokenKind.Variable))
                        varName = Read().Value?.ToString() ?? Read().Text;
                }
            }

            var cbody = ParseBlock();
            catches.Add(new CatchClause(types.Count > 0 ? types : null, varName, cbody));
        }

        // finally 同理：保存位置，不是 finally 则回退。
        var savedFinallyPos = _pos;
        SkipNewLinesAndComments();
        if (MatchKeyword("finally"))
        {
            finallyBody = ParseBlock();
        }
        else
        {
            _pos = savedFinallyPos;
        }

        return new TryStatement(body, catches, finallyBody, SpanFrom(start));
    }

    /// <summary>
    /// 解析 catch 类型列表（现代 catch e: Type1, Type2 形式）。
    /// 类型可为 [TypeRef]（带方括号）或 dotted 标识符名（System.Exception）。
    /// 以 { 或换行结束。
    /// </summary>
    private List<TypeReference> ParseCatchTypeList()
    {
        var types = new List<TypeReference>();
        SkipNewLinesAndComments();

        // 空 catch（无类型）——直接返回
        if (Check(TokenKind.LBrace)) return types;

        while (true)
        {
            SkipNewLinesAndComments();
            if (Check(TokenKind.TypeRef))
            {
                var t = Read();
                types.Add(ParseTypeRefText(t.Text));
            }
            else if (Check(TokenKind.Identifier))
            {
                // dotted 类型名：System.IO.IOException
                var sb = new StringBuilder();
                var startTok = Peek();
                while (Check(TokenKind.Identifier) || Check(TokenKind.Dot))
                {
                    sb.Append(Read().Text);
                }
                types.Add(new TypeReference(sb.ToString(), IsArray: false, ArrayRank: 0, GenericArgs: null, startTok.Span));
            }
            else
            {
                break;
            }
            SkipNewLinesAndComments();
            if (Match(TokenKind.Comma)) continue;
            break;
        }
        return types;
    }

    private Statement ParseFunctionDefinition()
    {
        var start = Peek().Span.Start;
        var kindTok = Read(); // function / filter
        var fnKind = kindTok.Text.ToLowerInvariant() == "filter" ? FunctionKind.Filter : FunctionKind.Function;

        // 函数名
        if (!Check(TokenKind.Identifier))
            throw new ParserException(Peek().Span, "[modern] expected function name");
        var name = Read().Text;

        // 可选参数列表（在 { 之前）
        List<ParameterDeclaration> parameters = new();
        if (Match(TokenKind.LParen))
        {
            parameters = ParseParamDeclarations(closing: TokenKind.RParen);
            Expect(TokenKind.RParen, "')'");
        }
        SkipNewLinesAndComments();

        // 函数体 { ... }，可能含 param() 块
        var body = ParseScriptBlockExpression();
        if (parameters.Count > 0 && body.Parameters.Count > 0)
        {
            parameters = new List<ParameterDeclaration>(body.Parameters);
        }
        else if (parameters.Count > 0)
        {
            // 把外层参数注入 body，保留 body 的 SourceText/SourceFile（per ADR-0046 §2/§10）。
            body = body with { Parameters = parameters };
        }

        return new FunctionDefinitionStatement(name, parameters, body, fnKind, SpanFrom(start));
    }

    /// <summary>
    /// 解析现代 fn 函数定义：`fn name(params) { body }` 或 `fn name(params) -> RetType { body }`。
    /// 参数支持 `name: type = default` 现代注解形式（Per ADR-0050 §3.1/§3.2/§7.3）。
    /// 简化实现：返回类型注解 `-> RetType` 被消费但忽略（运行时不强制）。
    /// </summary>
    private Statement ParseFnDefinition()
    {
        var start = Peek().Span.Start;
        _pos++; // fn

        // 函数名（允许关键字作函数名，如 fn process——begin/process/end 是脚本块标签但 fn 上下文无歧义）
        if (!Check(TokenKind.Identifier) && !Check(TokenKind.Keyword))
            throw new ParserException(Peek().Span, "[modern] expected function name after 'fn'");
        var name = Read().Text;

        // 参数列表（必须有括号）
        Expect(TokenKind.LParen, "'(' to begin fn parameter list");
        var parameters = ParseModernParameterDeclarations();
        Expect(TokenKind.RParen, "')'");

        // 可选返回类型注解 `-> RetType`（per ADR-0050 §3.2/T-080：存储并在运行时校验）
        TypeReference? returnType = null;
        SkipNewLinesAndComments();
        if (Check(TokenKind.RightArrow))
        {
            _pos++; // ->
            returnType = ParseModernTypeReference();
        }

        SkipNewLinesAndComments();
        var body = ParseScriptBlockExpression();
        if (parameters.Count > 0 && body.Parameters.Count > 0)
        {
            parameters = new List<ParameterDeclaration>(body.Parameters);
        }
        else if (parameters.Count > 0)
        {
            // 把外层参数注入 body，保留 body 的 SourceText/SourceFile（per ADR-0046 §2/§10）。
            body = body with { Parameters = parameters };
        }

        return new FunctionDefinitionStatement(name, parameters, body, FunctionKind.Function, SpanFrom(start), returnType);
    }

    /// <summary>
    /// 解析现代参数声明列表（fn 风格）：`name: type = default, name2: type2, ...`。
    /// Per ADR-0050 §3.2/§7.3. 与 PowerShell 风格 `[Type]$name` 互斥。
    /// </summary>
    private List<ParameterDeclaration> ParseModernParameterDeclarations()
    {
        var result = new List<ParameterDeclaration>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SkipNewLinesAndComments();
        if (Check(TokenKind.RParen)) return result;

        while (true)
        {
            SkipNewLinesAndComments();
            // 参数名（identifier，不带 $）
            if (!Check(TokenKind.Identifier))
                throw new ParserException(Peek().Span, "[modern] expected parameter name in fn parameter list");
            var nameTok = Read();
            var paramName = nameTok.Text;

            // 重复参数检查。Per T-111（栈式作用域语义检查）。
            if (!seen.Add(paramName))
                throw new ParserException(nameTok.Span,
                    $"[modern] duplicate parameter name '{paramName}' in fn parameter list");

            TypeReference? type = null;
            // 可选 `: Type` 类型注解
            if (Match(TokenKind.Colon))
            {
                type = ParseModernTypeReference();
            }

            // 可选默认值 `= expr`
            Expression? defaultValue = null;
            bool mandatory = false;
            if (Match(TokenKind.Assign))
            {
                defaultValue = ParseExpression();
            }

            result.Add(new ParameterDeclaration(type, paramName, defaultValue, mandatory, result.Count));

            SkipNewLinesAndComments();
            if (!Match(TokenKind.Comma)) break;
        }
        return result;
    }

    /// <summary>
    /// 解析现代类型引用。Per ADR-0052 §2: 支持 int / int? / int|string / List&lt;int&gt; / Dict&lt;string,int&gt; 等复合类型。
    /// 完整注解字符串存入 <see cref="TypeReference.FullName"/>，由 TypeCoercer.ParseTypeAnnotation 延迟解析为 TypeAnnotation 树。
    /// 仅在类型注解上下文（参数 `:` 后、返回 `->` 后、`is` 右侧）调用，故消费 `|` / `?` / `&lt;&gt;` 不与表达式层 conflict。
    /// </summary>
    private TypeReference ParseModernTypeReference()
    {
        var start = Peek().Span.Start;
        var sb = new StringBuilder();
        sb.Append(ParseTypeReferenceTerm());
        // union: int | string
        while (Check(TokenKind.Pipe))
        {
            _pos++; // 消费 '|'
            sb.Append('|');
            sb.Append(ParseTypeReferenceTerm());
        }
        // ADR-0050 §7.2: 后缀数组类型 int[] —— 等价 [int[]]。
        if (Check(TokenKind.LBracket) && Peek(1).Kind == TokenKind.RBracket)
        {
            _pos += 2; // 消费 [ ]
            var baseName = sb.ToString();
            // 去除可能的 ? 后缀以判断基础类型名（int?[] 仍 IsArray=true）
            return new TypeReference(baseName, IsArray: true, ArrayRank: 1, GenericArgs: null, SpanFrom(start));
        }
        return TypeReferences.Simple(sb.ToString(), SpanFrom(start));
    }

    /// <summary>解析单个类型项：Name[.Name...][&lt;args&gt;][?]。</summary>
    private string ParseTypeReferenceTerm()
    {
        var sb = new StringBuilder();
        while (Check(TokenKind.Identifier) || Check(TokenKind.Dot))
        {
            sb.Append(Read().Text);
        }
        // 泛型参数 <...>
        if (Check(TokenKind.Lt))
        {
            sb.Append(ParseTypeReferenceGenericArgs());
        }
        // 可选 ?
        if (Check(TokenKind.Question))
        {
            sb.Append('?');
            _pos++; // 消费 '?'
        }
        return sb.ToString();
    }

    /// <summary>解析泛型参数列表 &lt;T, T2, ...&gt;，返回含尖括号的字符串（处理嵌套 &gt;&gt;）。</summary>
    private string ParseTypeReferenceGenericArgs()
    {
        var sb = new StringBuilder();
        sb.Append(Read().Text); // 消费 '<' (Lt, text "<")
        int depth = 1;
        while (depth > 0 && !AtEnd)
        {
            var tok = Peek();
            switch (tok.Kind)
            {
                case TokenKind.Lt:
                    depth++;
                    sb.Append(Read().Text); // "<"
                    break;
                case TokenKind.Gt:
                    depth--;
                    sb.Append(Read().Text); // ">"
                    break;
                case TokenKind.Shr: // ">>" → 两个闭合尖括号（嵌套泛型收尾）
                    depth -= 2;
                    sb.Append(Read().Text);
                    break;
                case TokenKind.Ge: // ">=" 不应在类型上下文出现，按 ">" 处理
                    depth--;
                    sb.Append('>');
                    _pos++;
                    break;
                case TokenKind.Comma:
                    sb.Append(", ");
                    _pos++;
                    break;
                case TokenKind.Pipe:
                    sb.Append('|');
                    _pos++;
                    break;
                case TokenKind.Question:
                    sb.Append('?');
                    _pos++;
                    break;
                default:
                    // Identifier / Dot / 其他：原样追加文本。
                    sb.Append(Read().Text);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>消费现代类型引用（用于返回类型注解 `-> RetType`）。复用 ParseModernTypeReference 保证一致消费。</summary>
    private void SkipModernTypeReference()
    {
        ParseModernTypeReference();
    }

    /// <summary>
    /// 解析 #lang 块切换语句。Per ADR-0050 §1.3.
    /// 语法：`#lang ps1 { ... }` / `#lang osh { ... }`。
    /// token 文本为整行（如 `#lang ps1 { function Foo { 'bar' } }`）。
    /// 提取 { } 内的源文本，按指定模式用对应 parser 解析。
    /// </summary>
    private Statement ParseLangBlock()
    {
        var tok = Read();
        var start = tok.Span.Start;
        var text = tok.Text;

        // 提取模式名：#lang ps1 / #lang osh
        var modeMatch = System.Text.RegularExpressions.Regex.Match(
            text, @"^#lang\s+(ps1|osh)\s*\{", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!modeMatch.Success)
        {
            throw new ParserException(tok.Span,
                $"[modern] invalid #lang directive: '{text}'. Expected '#lang ps1 {{ ... }}' or '#lang osh {{ ... }}'");
        }
        var mode = modeMatch.Groups[1].Value.ToLowerInvariant();

        // 提取 { } 内的源文本。需匹配花括号配对（内部可能含嵌套 { }）。
        var braceStart = text.IndexOf('{');
        if (braceStart < 0)
        {
            throw new ParserException(tok.Span,
                "[modern] #lang directive missing '{' — use '#lang ps1' for REPL mode switch or '#lang ps1 { ... }' for block");
        }

        var bodyText = ExtractBraceContent(text, braceStart);
        if (bodyText is null)
        {
            // 花括号未闭合
            throw new ParserException(tok.Span,
                "[modern] UnclosedLangBlockError: #lang block missing closing '}'");
        }

        // 用对应 parser 解析块体文本。
        ScriptBlockAst bodyAst;
        try
        {
            bodyAst = mode == "ps1"
                ? PowerShellParser.Parse(bodyText, _fileName)
                : ModernParser.Parse(bodyText, _fileName);
        }
        catch (ParserException ex)
        {
            throw new ParserException(ex.Span,
                $"[modern] #lang {mode} block parse error: {ex.Message}");
        }

        return new LangBlockStatement(mode, bodyAst.Statements, SpanFrom(start));
    }

    /// <summary>
    /// 从文本中提取指定起始花括号 { 到配对 } 内的内容（不含外层 { }）。
    /// 处理嵌套花括号配对。返回 null 表示未闭合。
    /// </summary>
    private static string? ExtractBraceContent(string text, int braceStart)
    {
        int depth = 0;
        int i = braceStart;
        var sb = new StringBuilder();
        bool inString = false;
        char stringQuote = '\0';
        bool inLineComment = false;

        while (i < text.Length)
        {
            var c = text[i];

            // 字符串内不计数花括号
            if (inString)
            {
                sb.Append(c);
                if (c == stringQuote && (i + 1 >= text.Length || text[i + 1] != stringQuote))
                    inString = false;
                else if (c == stringQuote && i + 1 < text.Length && text[i + 1] == stringQuote)
                {
                    sb.Append(text[i + 1]); i++; // 跳过转义的重复引号
                }
                i++;
                continue;
            }

            if (inLineComment)
            {
                sb.Append(c);
                if (c == '\n') inLineComment = false;
                i++;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inString = true;
                stringQuote = c;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '#' && i > 0 && (i == 0 || text[i - 1] == ' ' || text[i - 1] == '\t' || text[i - 1] == '\n'))
            {
                inLineComment = true;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '{')
            {
                depth++;
                if (depth > 1) sb.Append(c); // 内层花括号保留
                i++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0) return sb.ToString(); // 闭合外层
                sb.Append(c); // 内层花括号保留
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return null; // 未闭合
    }

    /// <summary>
    /// 解析类型化变量声明。Per ADR-0050 §7.1/§7.2: `$var: Type [@Attr(args)]... = value`。
    /// 语法：$name : TypeReference (@Attribute(args))* (= Expression)?
    /// </summary>
    private Statement ParseVariableDeclaration(SourcePosition start)
    {
        var varTok = Read(); // 消费 $var
        var varName = varTok.Value?.ToString() ?? varTok.Text;
        EnsureNotReservedVariable(varName, varTok.Span);

        Expect(TokenKind.Colon, "':' (type annotation separator)");
        SkipNewLinesAndComments();

        var type = ParseModernTypeReference();

        // ADR-0050 §7.1: @Attribute(args) 特性列表（零或多个）
        var attributes = new List<AttributeAst>();
        while (Check(TokenKind.At))
        {
            var attr = ParseAttribute();
            attributes.Add(attr);
            SkipNewLinesAndComments();
        }

        // 可选初始值 = expr
        Expression? initialValue = null;
        if (Match(TokenKind.Assign))
        {
            SkipNewLinesAndComments();
            initialValue = ParseExpression();
        }

        return new VariableDeclarationStatement(varName, type, attributes, initialValue, SpanFrom(start));
    }

    /// <summary>
    /// 解析特性 `@Name(args)`。Per ADR-0050 §7.1.
    /// `@` 已检测但未消费；消费 @ + Identifier + 可选 (args)。
    /// </summary>
    private AttributeAst ParseAttribute()
    {
        var start = Peek().Span.Start;
        Expect(TokenKind.At, "'@' (attribute prefix)");
        if (!Check(TokenKind.Identifier))
            throw new ParserException(Peek().Span, $"[modern] expected attribute name after '@', got {Peek().Kind} '{Peek().Text}'");
        var nameTok = Read();
        var args = new List<Expression>();

        // 可选参数列表 (arg1, arg2, ...)
        if (Check(TokenKind.LParen))
        {
            _pos++; // 消费 (
            SkipNewLinesAndComments();
            if (!Check(TokenKind.RParen))
            {
                while (true)
                {
                    SkipNewLinesAndComments();
                    args.Add(ParseExpression());
                    SkipNewLinesAndComments();
                    if (!Match(TokenKind.Comma)) break;
                    SkipNewLinesAndComments();
                }
            }
            Expect(TokenKind.RParen, "')' (attribute args close)");
        }

        return new AttributeAst(nameTok.Text, args, SpanFrom(start));
    }

    private Statement ParseReturn()
    {
        var start = Peek().Span.Start;
        _pos++; // return
        SkipNewLinesAndComments();
        Expression? value = null;
        if (!IsEndOfStatement(Peek()))
            value = ParseExpression();
        return new ReturnStatement(value, SpanFrom(start));
    }

    private Statement ParseThrow()
    {
        var start = Peek().Span.Start;
        _pos++; // throw
        SkipNewLinesAndComments();
        Expression? value = null;
        if (!IsEndOfStatement(Peek()))
            value = ParseExpression();
        return new ThrowStatement(value, SpanFrom(start));
    }

    private Statement ParseExit()
    {
        var start = Peek().Span.Start;
        _pos++; // exit
        SkipNewLinesAndComments();
        Expression? code = null;
        if (!IsEndOfStatement(Peek()))
            code = ParseExpression();
        return new ExitStatement(code, SpanFrom(start));
    }

    private Statement ParseUsing()
    {
        var start = Peek().Span.Start;
        _pos++; // using
        SkipNewLinesAndComments();
        if (!Check(TokenKind.Identifier))
            throw new ParserException(Peek().Span, "[modern] expected using kind (namespace/module/assembly)");
        var kindStr = Read().Text.ToLowerInvariant();
        var kind = kindStr switch
        {
            "namespace" => UsingKind.Namespace,
            "module" => UsingKind.Module,
            "assembly" => UsingKind.Assembly,
            "command" => UsingKind.Command,
            "type" => UsingKind.Type,
            _ => UsingKind.Namespace,
        };
        var sb = new StringBuilder();
        while (!IsEndOfStatement(Peek()) && !AtEnd)
        {
            sb.Append(Peek().Text).Append(' ');
            _pos++;
        }
        return new UsingStatement(kind, sb.ToString().Trim(), SpanFrom(start));
    }

    /// <summary>
    /// ADR-0050 §10.1 + ADR-0056 §2: 解析 import 指令。
    /// 支持形式：
    ///   1. `import "file.osh"` — 副作用加载（向后兼容，编译为 UsingStatement）。
    ///   2. `import { fn1, fn2 } from "file.osh"` — 命名导入（NamedImportAst）。
    ///   3. `import * as Mod from "file.osh"` — 命名空间导入（NamespaceImportAst）。
    /// </summary>
    private Statement ParseImport(SourcePosition start)
    {
        _pos++; // import
        SkipNewLinesAndComments();

        // 形式 2: import { name1, name2 } from "..."
        if (Check(TokenKind.LBrace))
        {
            _pos++; // {
            var names = new List<string>();
            SkipNewLinesAndComments();
            if (!Check(TokenKind.RBrace))
            {
                while (true)
                {
                    SkipNewLinesAndComments();
                    if (!Check(TokenKind.Identifier))
                        throw new ParserException(Peek().Span, "[modern] expected import name in '{ }' list");
                    names.Add(Read().Text);
                    SkipNewLinesAndComments();
                    if (!Match(TokenKind.Comma)) break;
                    SkipNewLinesAndComments();
                }
            }
            Expect(TokenKind.RBrace, "'}'");
            SkipNewLinesAndComments();
            if (!MatchKeyword("from"))
                throw new ParserException(Peek().Span, "[modern] expected 'from' after import name list");
            SkipNewLinesAndComments();
            if (!Check(TokenKind.String) && !Check(TokenKind.SingleString))
                throw new ParserException(Peek().Span, "[modern] import expects a string module path after 'from'");
            var path = Read().Text;
            return new NamedImportAst(names, path, SpanFrom(start));
        }

        // 形式 3: import * as Mod from "..."
        if (Check(TokenKind.Star))
        {
            _pos++; // *
            SkipNewLinesAndComments();
            if (!MatchKeyword("as"))
                throw new ParserException(Peek().Span, "[modern] expected 'as' after '*' in namespace import");
            SkipNewLinesAndComments();
            if (!Check(TokenKind.Identifier))
                throw new ParserException(Peek().Span, "[modern] expected namespace name after 'as'");
            var ns = Read().Text;
            SkipNewLinesAndComments();
            if (!MatchKeyword("from"))
                throw new ParserException(Peek().Span, "[modern] expected 'from' after namespace name");
            SkipNewLinesAndComments();
            if (!Check(TokenKind.String) && !Check(TokenKind.SingleString))
                throw new ParserException(Peek().Span, "[modern] import expects a string module path after 'from'");
            var path = Read().Text;
            return new NamespaceImportAst(ns, path, SpanFrom(start));
        }

        // 形式 1: import "file.osh" — 副作用加载（向后兼容）。
        if (!Check(TokenKind.String) && !Check(TokenKind.SingleString))
            throw new ParserException(Peek().Span, "[modern] import expects a string file path or '{ names } from'");
        var simplePath = Read().Text;
        return new UsingStatement(UsingKind.Module, simplePath, SpanFrom(start));
    }

    /// <summary>
    /// ADR-0051 §1/§3: 解析 async 构造。
    /// 语法：
    ///   - `async fn name(params) { body }` — 异步函数声明 → AsyncFunctionDeclarationAst
    ///   - `async { ... }` — async 块表达式 → ExpressionStatement(AsyncBlockExpression)
    /// </summary>
    private Statement ParseAsyncConstruct(SourcePosition start)
    {
        _pos++; // async
        SkipNewLinesAndComments();

        // async fn name(params) { body }
        if (CheckKeyword("fn"))
        {
            _pos++; // fn
            if (!Check(TokenKind.Identifier))
                throw new ParserException(Peek().Span, "[modern] expected function name after 'async fn'");
            var name = Read().Text;

            // 参数列表（必须有括号）
            Expect(TokenKind.LParen, "'(' to begin async fn parameter list");
            var parameters = ParseModernParameterDeclarations();
            Expect(TokenKind.RParen, "')'");

            // 可选返回类型注解 `-> RetType`（消费但忽略，运行时不强制）
            SkipNewLinesAndComments();
            if (Check(TokenKind.Arrow))
            {
                _pos++; // ->
                SkipModernTypeReference();
            }

            SkipNewLinesAndComments();
            var body = ParseScriptBlockExpression();
            if (parameters.Count > 0 && body.Parameters.Count > 0)
                parameters = new List<ParameterDeclaration>(body.Parameters);
            else if (parameters.Count > 0)
                body = body with { Parameters = parameters };

            return new AsyncFunctionDeclarationAst(name, parameters, body, SpanFrom(start));
        }

        // async { ... } — async 块表达式（作为语句）
        if (Check(TokenKind.LBrace))
        {
            var blockStatements = ParseBlock();
            return new ExpressionStatement(
                new AsyncBlockExpression(blockStatements, SpanFrom(start)),
                SpanFrom(start));
        }

        throw new ParserException(Peek().Span, "[modern] expected 'fn' or '{' after 'async'");
    }

    /// <summary>ADR-0051 §3: 解析 async { ... } 块表达式（表达式上下文入口）。</summary>
    private Expression ParseAsyncBlockExpression()
    {
        var start = Peek().Span.Start;
        _pos++; // async
        var blockStatements = ParseBlock();
        return new AsyncBlockExpression(blockStatements, SpanFrom(start));
    }

    /// <summary>
    /// ADR-0056 §1: 解析 export 声明。
    /// 语法：
    ///   - `export fn name(params) { body }` — 导出函数
    ///   - `export const NAME = value` — 导出常量
    ///   - `export default expr` — 默认导出
    /// </summary>
    private Statement ParseExport(SourcePosition start)
    {
        _pos++; // export
        SkipNewLinesAndComments();

        // export default expr
        if (MatchKeyword("default"))
        {
            SkipNewLinesAndComments();
            var inner = ParseStatement();
            if (inner is null)
                throw new ParserException(Peek().Span, "[modern] export default expects an expression or statement");
            return new ExportDeclarationAst(ExportKind.Default, Name: null, inner, SpanFrom(start));
        }

        // export const NAME = value
        if (CheckKeyword("const") || (Check(TokenKind.Identifier) && Peek().Text == "const"))
        {
            _pos++; // const
            if (!Check(TokenKind.Identifier) && !Check(TokenKind.Variable))
                throw new ParserException(Peek().Span, "[modern] export const expects a name");
            var nameTok = Read();
            var name = nameTok.Value?.ToString() ?? nameTok.Text;
            Expect(TokenKind.Assign, "'=' after export const name");
            var value = ParseExpression();
            var inner = new AssignmentStatement(
                new VariableTarget(name, nameTok.Span),
                AssignmentOperator.Assign,
                value,
                SpanFrom(start));
            return new ExportDeclarationAst(ExportKind.Constant, name, inner, SpanFrom(start));
        }

        // export fn name(params) { body }
        if (CheckKeyword("fn"))
        {
            var fnStmt = ParseFnDefinition();
            if (fnStmt is not FunctionDefinitionStatement fnDef)
                throw new ParserException(Peek().Span, "[modern] export fn expects a function definition");
            return new ExportDeclarationAst(ExportKind.Function, fnDef.Name, fnDef, SpanFrom(start));
        }

        // export function name { body } — PowerShell 风格兼容
        if (CheckKeyword("function") || CheckKeyword("filter"))
        {
            var fnStmt = ParseFunctionDefinition();
            if (fnStmt is not FunctionDefinitionStatement fnDef)
                throw new ParserException(Peek().Span, "[modern] export function expects a function definition");
            return new ExportDeclarationAst(ExportKind.Function, fnDef.Name, fnDef, SpanFrom(start));
        }

        throw new ParserException(Peek().Span, "[modern] expected 'fn' / 'const' / 'default' / 'function' after 'export'");
    }

    private static bool IsEndOfStatement(in Token t)
        => t.Kind is TokenKind.End or TokenKind.NewLine or TokenKind.Semicolon or TokenKind.RBrace;

    // =========================================================================
    // param() 块解析
    // =========================================================================

    private List<ParameterDeclaration> ParseParamParenList()
    {
        SkipNewLinesAndComments();
        Expect(TokenKind.LParen, "'(' after param");
        var list = ParseParamDeclarations(closing: TokenKind.RParen);
        Expect(TokenKind.RParen, "')'");
        return list;
    }

    private List<ParameterDeclaration> ParseParamDeclarations(TokenKind closing)
    {
        var list = new List<ParameterDeclaration>();
        SkipNewLinesAndComments();
        if (Check(closing)) return list;

        while (true)
        {
            SkipNewLinesAndComments();
            var param = ParseParameterDeclaration();
            list.Add(param);
            SkipNewLinesAndComments();
            if (!Match(TokenKind.Comma)) break;
            SkipNewLinesAndComments();
        }
        return list;
    }

    /// <summary>解析单个参数声明：[Type]$name = default [, Mandatory] [Position=n]</summary>
    private ParameterDeclaration ParseParameterDeclaration()
    {
        var start = Peek().Span.Start;
        TypeReference? type = null;
        bool mandatory = false;
        int position = -1;
        IReadOnlyList<string>? aliases = null;
        ParameterSetKind? paramSet = null;

        // 参数特性（简化：跳过 [Parameter(...)] 等属性）
        while (Check(TokenKind.LBracket))
        {
            var save = _pos;
            _pos++; // [
            if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.LParen)
            {
                int depth = 1;
                _pos++; // (
                while (!AtEnd && depth > 0)
                {
                    if (Check(TokenKind.LParen)) depth++;
                    else if (Check(TokenKind.RParen)) depth--;
                    if (depth > 0) _pos++;
                }
                if (Check(TokenKind.RParen)) _pos++;
                if (Check(TokenKind.RBracket)) _pos++;
                continue;
            }
            _pos = save;
            if (Check(TokenKind.TypeRef))
            {
                var t = Read();
                type = ParseTypeRefText(t.Text);
            }
            else
            {
                _pos++;
            }
        }

        // $name
        if (!Check(TokenKind.Variable))
            throw new ParserException(Peek().Span, "[modern] expected parameter name");
        var nameTok = Read();
        var name = nameTok.Value?.ToString() ?? nameTok.Text;

        // 默认值
        Expression? defaultValue = null;
        if (Match(TokenKind.Assign))
        {
            defaultValue = ParseExpression();
        }

        return new ParameterDeclaration(type, name, defaultValue, mandatory, position, aliases, paramSet);
    }

    // =========================================================================
    // 块解析
    // =========================================================================

    /// <summary>解析 { ... } 语句块。</summary>
    private List<Statement> ParseBlock()
    {
        Expect(TokenKind.LBrace, "'{'");
        return ParseBlockStatements();
    }

    private List<Statement> ParseBlockStatements()
    {
        var list = new List<Statement>();
        SkipSeparators();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            var stmt = ParseStatement();
            if (stmt is not null) list.Add(stmt);
            if (!Check(TokenKind.NewLine) && !Check(TokenKind.Semicolon) && !Check(TokenKind.RBrace) && !AtEnd)
                _pos++;
            SkipSeparators();
        }
        Expect(TokenKind.RBrace, "'}'");
        return list;
    }

    private Expression ParseParenExpression()
    {
        Expect(TokenKind.LParen, "'('");
        SkipNewLinesAndComments();
        var expr = ParseExpression();
        SkipNewLinesAndComments();
        Expect(TokenKind.RParen, "')'");
        return expr;
    }

    /// <summary>
    /// 解析条件表达式：现代语法可选括号 if cond { } / while cond { }（Per ADR-0050 §5.1）。
    /// 有括号时用 ParseParenExpression；无括号时直接 ParseExpression（自然在 { 前停止）。
    /// </summary>
    private Expression ParseCondition()
    {
        if (Check(TokenKind.LParen))
            return ParseParenExpression();
        SkipNewLinesAndComments();
        return ParseExpression();
    }

    // =========================================================================
    // 脚本块表达式 { ... }
    // =========================================================================

    /// <summary>
    /// 检测 { 后是否为哈希字面量 { k: v, ... }。Per ADR-0050 §4。
    /// 启发式：{ (跳过换行/注释) 后是合法 key（string/singleString/identifier/number/variable）
    /// 且 key 后紧跟 Colon（非 :: 和未跟 = 的 label）。
    /// 空哈希 {} 不视为哈希字面量（保持脚本块语义，用 @{} 表达空哈希）。
    /// </summary>
    private bool IsHashLiteralAhead()
    {
        // 当前位于 LBrace
        if (!Check(TokenKind.LBrace)) return false;
        int i = _pos + 1;
        // 跳过换行/注释
        while (i < _tokens.Count && (_tokens[i].Kind is TokenKind.NewLine or TokenKind.LineComment or TokenKind.BlockComment))
            i++;
        if (i >= _tokens.Count) return false;
        var keyTok = _tokens[i];
        bool isKey = keyTok.Kind is TokenKind.String or TokenKind.SingleString
            or TokenKind.Identifier or TokenKind.Integer or TokenKind.Double
            or TokenKind.Variable or TokenKind.ScopedVariable;
        if (!isKey) return false;
        // key 后必须紧跟 Colon（且非 ::）—— 检查下一个非换行 token
        i++;
        while (i < _tokens.Count && (_tokens[i].Kind is TokenKind.NewLine or TokenKind.LineComment or TokenKind.BlockComment))
            i++;
        if (i >= _tokens.Count) return false;
        // Colon token
        return _tokens[i].Kind == TokenKind.Colon;
    }

    /// <summary>解析 { k: v, k2: v2 } 哈希字面量。Per ADR-0050 §4。</summary>
    private Expression ParseHashLiteral(SourcePosition start)
    {
        Expect(TokenKind.LBrace, "'{'");
        var entries = new List<KeyValuePair<Expression, Expression>>();
        SkipSeparators();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            SkipNewLinesAndComments();
            if (Check(TokenKind.RBrace)) break;
            var key = ParseExpression();
            Expect(TokenKind.Colon, "':' (hash entry key-value separator)");
            SkipNewLinesAndComments();
            var val = ParseExpression();
            entries.Add(new KeyValuePair<Expression, Expression>(key, val));
            SkipSeparators();
            // 条目间逗号分隔符（可选拖尾逗号）
            if (Match(TokenKind.Comma)) SkipSeparators();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new HashExpression(entries, SpanFrom(start));
    }

    private ScriptBlockExpression ParseScriptBlockExpression()
    {
        var start = Peek().Span.Start;
        Expect(TokenKind.LBrace, "'{'");
        SkipSeparators();

        // [CmdletBinding(...)] 特性：per ADR-0049 §1. 出现在 param() 之前。
        // 先跳过任何非 CmdletBinding 的 [Attribute(...)] 块。
        while (TrySkipUnknownAttribute())
            SkipSeparators();
        CmdletBindingAttributeAst? cmdletBinding = TryParseCmdletBindingAttribute();
        if (cmdletBinding is not null)
        {
            SkipSeparators();
            while (TrySkipUnknownAttribute())
                SkipSeparators();
        }

        var parameters = new List<ParameterDeclaration>();
        if (MatchKeyword("param"))
        {
            parameters = ParseParamParenList();
            SkipSeparators();
        }

        // 命名块收集：per ADR-0046 §6. begin/process/end 各最多一次。
        var statements = new List<Statement>();
        List<Statement>? beginBlock = null;
        List<Statement>? processBlock = null;
        List<Statement>? endBlock = null;

        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            // 命名块标签：begin { } / process { } / end { }
            if (CheckKeyword("begin") || CheckKeyword("process") || CheckKeyword("end"))
            {
                var blockName = Peek().Text.ToLowerInvariant();
                _pos++;
                SkipSeparators();
                Expect(TokenKind.LBrace, "'{' to open " + blockName + " block");
                SkipSeparators();
                var innerStatements = new List<Statement>();
                while (!Check(TokenKind.RBrace) && !AtEnd)
                {
                    var innerStmt = ParseStatement();
                    if (innerStmt is not null) innerStatements.Add(innerStmt);
                    if (!Check(TokenKind.NewLine) && !Check(TokenKind.Semicolon) && !Check(TokenKind.RBrace) && !AtEnd)
                        _pos++;
                    SkipSeparators();
                }
                Expect(TokenKind.RBrace, "'}' to close " + blockName + " block");
                switch (blockName)
                {
                    case "begin":
                        if (beginBlock is not null)
                            throw new ParserException(Peek().Span, "duplicate 'begin' block in script block");
                        beginBlock = innerStatements;
                        break;
                    case "process":
                        if (processBlock is not null)
                            throw new ParserException(Peek().Span, "duplicate 'process' block in script block");
                        processBlock = innerStatements;
                        break;
                    case "end":
                        if (endBlock is not null)
                            throw new ParserException(Peek().Span, "duplicate 'end' block in script block");
                        endBlock = innerStatements;
                        break;
                }
                // 命名块之间仅允许 NewLine/Semicolon 分隔符（可空），不应消费下一个 begin/process/end 关键字。
                SkipSeparators();
                continue;
            }

            var stmt = ParseStatement();
            if (stmt is not null) statements.Add(stmt);
            if (!Check(TokenKind.NewLine) && !Check(TokenKind.Semicolon) && !Check(TokenKind.RBrace) && !AtEnd)
                _pos++;
            SkipSeparators();
        }
        Expect(TokenKind.RBrace, "'}'");
        var sbSpan = SpanFrom(start);
        return new ScriptBlockExpression(
            statements, parameters, sbSpan,
            beginBlock, processBlock, endBlock, cmdletBinding,
            SourceText: SliceSource(sbSpan), SourceFile: _fileName);
    }

    /// <summary>
    /// 尝试解析 [CmdletBinding(...)] 特性。Per ADR-0049 §1.
    /// 成功返回 <see cref="CmdletBindingAttributeAst"/> 并消费对应 token；
    /// 不匹配则不消费输入，返回 null。
    /// </summary>
    private CmdletBindingAttributeAst? TryParseCmdletBindingAttribute()
    {
        if (!Check(TokenKind.LBracket)) return null;
        if (Peek(1).Kind != TokenKind.Identifier
            || !string.Equals(Peek(1).Text, "CmdletBinding", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var start = Peek().Span.Start;
        _pos++; // [
        _pos++; // CmdletBinding

        bool supportsShouldProcess = false;
        DeclaredConfirmImpact confirmImpact = DeclaredConfirmImpact.Medium;
        bool supportsPaging = false;
        bool supportsTransactions = false;
        string? defaultParameterSetName = null;
        bool positionalBinding = true;
        string? helpUri = null;

        if (Match(TokenKind.LParen))
        {
            SkipNewLinesAndComments();
            while (!Check(TokenKind.RParen) && !AtEnd)
            {
                if (!Check(TokenKind.Identifier)) { _pos++; continue; }
                var name = Peek().Text;
                _pos++;
                SkipNewLinesAndComments();
                if (Match(TokenKind.Assign))
                {
                    SkipNewLinesAndComments();
                    var value = ParseAttributeArgumentValue();
                    switch (name.ToLowerInvariant())
                    {
                        case "confirmimpact":
                            if (value is string s1) confirmImpact = ParseDeclaredConfirmImpact(s1);
                            break;
                        case "defaultparametersetname":
                            if (value is string s2) defaultParameterSetName = s2;
                            break;
                        case "positionalbinding":
                            if (value is bool b1) positionalBinding = b1;
                            break;
                        case "helpuri":
                            if (value is string s3) helpUri = s3;
                            break;
                        case "supportspaging":
                            if (value is bool b2) supportsPaging = b2;
                            break;
                        case "supportstransactions":
                            if (value is bool b3) supportsTransactions = b3;
                            break;
                    }
                }
                else
                {
                    switch (name.ToLowerInvariant())
                    {
                        case "supportsshouldprocess": supportsShouldProcess = true; break;
                        case "supportspaging": supportsPaging = true; break;
                        case "supportstransactions": supportsTransactions = true; break;
                    }
                }
                SkipNewLinesAndComments();
                if (!Match(TokenKind.Comma)) break;
                SkipNewLinesAndComments();
            }
            Expect(TokenKind.RParen, "')' to close CmdletBinding args");
        }
        Expect(TokenKind.RBracket, "']' to close [CmdletBinding]");
        return new CmdletBindingAttributeAst(
            supportsShouldProcess, confirmImpact, supportsPaging, supportsTransactions,
            defaultParameterSetName, positionalBinding, helpUri, SpanFrom(start));
    }

    /// <summary>跳过未识别的 [Attribute(...)] 块（不消费非特性 token）。返回是否跳过。</summary>
    private bool TrySkipUnknownAttribute()
    {
        if (!Check(TokenKind.LBracket)) return false;
        if (Peek(1).Kind != TokenKind.Identifier) return false;
        if (string.Equals(Peek(1).Text, "CmdletBinding", StringComparison.OrdinalIgnoreCase))
            return false;
        var save = _pos;
        _pos++; // [
        _pos++; // Identifier
        if (Match(TokenKind.LParen))
        {
            int depth = 1;
            while (!AtEnd && depth > 0)
            {
                if (Check(TokenKind.LParen)) depth++;
                else if (Check(TokenKind.RParen)) depth--;
                if (depth > 0) _pos++;
            }
            if (Check(TokenKind.RParen)) _pos++;
        }
        if (Check(TokenKind.RBracket)) _pos++;
        else { _pos = save; return false; }
        return true;
    }

    private static DeclaredConfirmImpact ParseDeclaredConfirmImpact(string text)
        => text.ToLowerInvariant() switch
        {
            "none" => DeclaredConfirmImpact.None,
            "low" => DeclaredConfirmImpact.Low,
            "medium" => DeclaredConfirmImpact.Medium,
            "high" => DeclaredConfirmImpact.High,
            _ => DeclaredConfirmImpact.Medium,
        };

    /// <summary>解析特性参数值：字符串 / 布尔 / 数字 / 标识符（视作 enum 名）。</summary>
    private object? ParseAttributeArgumentValue()
    {
        var tok = Peek();
        switch (tok.Kind)
        {
            case TokenKind.String:
            case TokenKind.SingleString:
                _pos++;
                return tok.Value?.ToString() ?? tok.Text.Trim('"', '\'');
            case TokenKind.Boolean:
                _pos++;
                return tok.Value is bool b ? b : string.Equals(tok.Text, "$true", StringComparison.OrdinalIgnoreCase);
            case TokenKind.Variable:
                _pos++;
                if (string.Equals(tok.Text, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(tok.Text, "false", StringComparison.OrdinalIgnoreCase)) return false;
                return tok.Text;
            case TokenKind.Identifier:
                _pos++;
                return tok.Text;
            default:
                _pos++;
                return tok.Text;
        }
    }

    // =========================================================================
    // 表达式解析（Pratt parser，modern 运算符）
    // =========================================================================

    /// <summary>表达式入口：Pratt parser，按 binding power 解析二元；末尾处理表达式层赋值。</summary>
    public Expression ParseExpression()
    {
        var left = ParseBinary(0);
        if (Peek().Kind.IsAssignmentOperator() && IsAssignTarget(left))
        {
            var start = left.Span.Start;
            var opTok = Read();
            SkipNewLinesAndComments();
            var rhs = ParseExpression();
            var target = ExprToAssignTarget(left, start);
            return new AssignmentExpression(target, opTok.Kind.ToAssignmentOperator(), rhs, SpanFrom(start));
        }
        return left;
    }

    private Expression ParseBinary(int minPrec)
    {
        var left = ParseUnary();
        while (true)
        {
            var tok = Peek();

            // 三元 ?: (prec=10, right-assoc) — 必须在 binary 循环内捕获，否则会丢失在比较之后
            if (tok.Kind == TokenKind.Question && 10 >= minPrec)
            {
                _pos++;
                SkipNewLinesAndComments();
                var ifTrue = ParseExpression();
                Expect(TokenKind.Colon, "':' in ternary");
                SkipNewLinesAndComments();
                var ifFalse = ParseBinary(10);
                left = new TernaryExpression(left, ifTrue, ifFalse, SpanFrom(left.Span.Start));
                continue;
            }

            // 范围 .. / ..< 产生 RangeExpression（非 BinaryExpression）
            if (tok.Kind is TokenKind.Range or TokenKind.DotDot or TokenKind.HalfOpenRange)
            {
                if (65 >= minPrec)
                {
                    var isHalf = tok.Kind == TokenKind.HalfOpenRange;
                    _pos++;
                    SkipNewLinesAndComments();
                    var right = ParseBinary(66);
                    left = new RangeExpression(left, right, SpanFrom(left.Span.Start)) { IsHalfOpen = isHalf };
                    continue;
                }
                break;
            }

            // ADR-0052 §4: `is` / `isnot` 运算符右侧为类型引用（含 ? / | / <>），特判处理（prec=60，与比较同级）。
            // as 右侧按普通表达式处理（走通用二元路径）。
            if ((tok.Kind == TokenKind.CmpIs || tok.Kind == TokenKind.CmpIsNot) && 60 >= minPrec)
            {
                _pos++; // 消费 is / isnot
                SkipNewLinesAndComments();
                var typeRef = ParseModernTypeReference();
                left = new BinaryExpression(
                    left,
                    tok.Kind == TokenKind.CmpIs ? BinaryOperator.Is : BinaryOperator.IsNot,
                    new TypeReferenceExpression(typeRef, typeRef.Span),
                    SpanFrom(left.Span.Start));
                // 注意：此处 tok.Kind 已在 _pos++ 前读取，下面 continue 后会重新 Peek。
                continue;
            }

            if (!TryGetBinaryOp(tok.Kind, out var op, out var prec, out var rightAssoc))
            {
                // ADR-0050 §2.1: in / contains 作为 modern 二元运算符（bare word in binary position）。
                // 注意：in 被 tokenizer 列为 Keyword，contains 为 Identifier——两者都需匹配。
                var isWordOp = (tok.Kind == TokenKind.Identifier || tok.Kind == TokenKind.Keyword)
                    && (string.Equals(tok.Text, "in", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tok.Text, "contains", StringComparison.OrdinalIgnoreCase));
                if (isWordOp && 60 >= minPrec)
                {
                    _pos++;
                    SkipNewLinesAndComments();
                    var rhsExpr = ParseBinary(61);
                    op = string.Equals(tok.Text, "in", StringComparison.OrdinalIgnoreCase)
                        ? BinaryOperator.In : BinaryOperator.Contains;
                    left = new BinaryExpression(left, op, rhsExpr, SpanFrom(left.Span.Start));
                    continue;
                }
                // ADR-0050 §2.1: ++ 数组拼接（binary position; 与一元递增区分）。
                if (tok.Kind == TokenKind.PlusPlus && 90 >= minPrec)
                {
                    _pos++;
                    SkipNewLinesAndComments();
                    var concatRhs = ParseBinary(91);
                    left = new BinaryExpression(left, BinaryOperator.ArrayConcat, concatRhs, SpanFrom(left.Span.Start));
                    continue;
                }
                break;
            }
            if (prec < minPrec) break;

            // ADR-0050 §2.2: .osh 模式下 PS 形式运算符（-eq -and 等）emit DeprecationWarning。
            if (IsPsStyleOperatorKind(tok.Kind))
            {
                _warnings.Add(new ParseWarning(
                    WarningKind.DeprecatedPsOperator,
                    $"PS 形式运算符 '{tok.Text}' 在 .osh 模式下已过时，建议使用现代形式（== != < > <= >= && || ! 等）",
                    tok.Span));
            }

            _pos++;
            int nextMin = rightAssoc ? prec : prec + 1;
            SkipNewLinesAndComments();
            var rhs = ParseBinary(nextMin);
            left = new BinaryExpression(left, op, rhs, SpanFrom(left.Span.Start));
        }
        return left;
    }

    /// <summary>
    /// 判断 token kind 是否为 PS 风格运算符（-eq -ne -gt -lt -le -ge -and -or -not -like -match -in -contains -is -as -band -bor -shl -shr 等）。
    /// Per ADR-0050 §2.2: .osh 模式下这些运算符 emit DeprecationWarning。
    /// </summary>
    private static bool IsPsStyleOperatorKind(TokenKind k) =>
        k is TokenKind.CmpEq or TokenKind.CmpNe or TokenKind.CmpLt or TokenKind.CmpGt
            or TokenKind.CmpLe or TokenKind.CmpGe
            or TokenKind.CmpLike or TokenKind.CmpNotLike
            or TokenKind.CmpMatch or TokenKind.CmpNotMatch
            or TokenKind.CmpIn or TokenKind.CmpNotIn
            or TokenKind.CmpContains or TokenKind.CmpNotContains
            or TokenKind.CmpAs
            or TokenKind.LogicalAnd or TokenKind.LogicalOr or TokenKind.LogicalNot or TokenKind.LogicalXor
            or TokenKind.CmpBand or TokenKind.CmpBor or TokenKind.BcmpBxor
            or TokenKind.CmpShl or TokenKind.CmpShr;

    /// <summary>modern 二元运算符表（per ADR-0050 §2）。不包含 PS 风格运算符（-eq -and 等）。</summary>
    private static bool TryGetBinaryOp(TokenKind k, out BinaryOperator op, out int prec, out bool rightAssoc)
    {
        op = default; prec = 0; rightAssoc = false;
        switch (k)
        {
            // 空合并 ?? (modern, right-assoc)
            case TokenKind.DoubleQuestion:
                op = BinaryOperator.NullCoalesce; prec = 20; rightAssoc = true; return true;
            // 逻辑或 || (modern 短路)
            case TokenKind.PipePipe:
                op = BinaryOperator.Or; prec = 30; return true;
            // 逻辑与 && (modern 短路)
            case TokenKind.AmpAmp:
                op = BinaryOperator.And; prec = 40; return true;

            // 比较 == != < > <= >= (modern, per ADR-0050 §2.1)
            // 同时支持 PS 风格 -eq -ne -gt -lt -le -ge（双模式词法，per Tokenizer 注释）
            case TokenKind.Equals:
            case TokenKind.CmpEq:
                op = BinaryOperator.Eq; prec = 60; return true;
            case TokenKind.NotEquals:
            case TokenKind.CmpNe:
                op = BinaryOperator.Ne; prec = 60; return true;
            case TokenKind.Lt:
            case TokenKind.CmpLt:
                op = BinaryOperator.Lt; prec = 60; return true;
            case TokenKind.Gt:
            case TokenKind.CmpGt:
                op = BinaryOperator.Gt; prec = 60; return true;
            case TokenKind.Le:
            case TokenKind.CmpLe:
                op = BinaryOperator.Le; prec = 60; return true;
            case TokenKind.Ge:
            case TokenKind.CmpGe:
                op = BinaryOperator.Ge; prec = 60; return true;
            // PS 风格 -like -match -in -contains（双模式兼容）
            case TokenKind.CmpLike: op = BinaryOperator.Like; prec = 60; return true;
            case TokenKind.CmpNotLike: op = BinaryOperator.NotLike; prec = 60; return true;
            case TokenKind.CmpMatch: op = BinaryOperator.Match; prec = 60; return true;
            case TokenKind.CmpNotMatch: op = BinaryOperator.NotMatch; prec = 60; return true;
            // ADR-0050 §2.1: modern 形式 ~= (通配符) / ~regex (正则)
            case TokenKind.TildeEquals: op = BinaryOperator.Like; prec = 60; return true;
            case TokenKind.TildeRegex: op = BinaryOperator.Match; prec = 60; return true;

            // 位移 << >> (modern)
            case TokenKind.Shl:
                op = BinaryOperator.ShiftLeft; prec = 80; return true;
            case TokenKind.Shr:
                op = BinaryOperator.ShiftRight; prec = 80; return true;

            // 加减
            case TokenKind.Plus:
                op = BinaryOperator.Add; prec = 90; return true;
            case TokenKind.Minus:
                op = BinaryOperator.Subtract; prec = 90; return true;

            // 乘除模
            case TokenKind.Star:
                op = BinaryOperator.Multiply; prec = 100; return true;
            case TokenKind.Slash:
                op = BinaryOperator.Divide; prec = 100; return true;
            case TokenKind.Percent:
                op = BinaryOperator.Modulo; prec = 100; return true;

            // 幂 ^ (right-assoc)
            case TokenKind.Caret:
                op = BinaryOperator.Power; prec = 120; rightAssoc = true; return true;
        }
        return false;
    }

    private Expression ParseUnary()
    {
        var start = Peek().Span.Start;
        var k = Peek().Kind;

        // ADR-0051 §2: await expr 一元前缀运算符（modern 独有）。
        // await 解包 Task / IAsyncEnumerable，绑定优先级高于二元运算、低于后缀。
        if (k == TokenKind.Keyword && CheckKeyword("await"))
        {
            _pos++; // await
            var operand = ParseUnary();
            return new AwaitExpressionAst(operand, SpanFrom(start));
        }

        // 前缀 ++ -- (modern 与 PS 共有)
        if (k is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            var tok = Read();
            var operand = ParseUnary();
            var uop = tok.Kind == TokenKind.PlusPlus ? UnaryOperator.PrefixIncrement : UnaryOperator.PrefixDecrement;
            return new UnaryExpression(uop, operand, Postfix: false, SpanFrom(start));
        }
        // 逻辑非 ! (modern, per ADR-0050 §2.1)
        if (k is TokenKind.Bang)
        {
            Read();
            var operand = ParseUnary();
            return new UnaryExpression(UnaryOperator.Not, operand, Postfix: false, SpanFrom(start));
        }
        // 位反 ~ (modern)
        if (k is TokenKind.BitNot)
        {
            Read();
            var operand = ParseUnary();
            return new UnaryExpression(UnaryOperator.BitwiseNot, operand, Postfix: false, SpanFrom(start));
        }
        // 一元负号 -
        if (k is TokenKind.Minus)
        {
            Read();
            var operand = ParseUnary();
            return new UnaryExpression(UnaryOperator.Negate, operand, Postfix: false, SpanFrom(start));
        }
        // 一元正号 +
        if (k is TokenKind.Plus)
        {
            Read();
            var operand = ParseUnary();
            return new UnaryExpression(UnaryOperator.Plus, operand, Postfix: false, SpanFrom(start));
        }

        var expr = ParsePostfixExpr();

        // 后缀 ++ --（仅对 lvalue 有意义；非 lvalue 后跟 ++ 视为数组拼接，交由 ParseBinary 处理）
        if (Peek().Kind is TokenKind.PlusPlus or TokenKind.MinusMinus && IsAssignTarget(expr))
        {
            var tok = Read();
            var uop = tok.Kind == TokenKind.PlusPlus ? UnaryOperator.PostfixIncrement : UnaryOperator.PostfixDecrement;
            expr = new UnaryExpression(uop, expr, Postfix: true, SpanFrom(start));
        }

        return expr;
    }

    /// <summary>后缀：成员访问 . / ?. / :: / 索引 [] / ?[] / 调用 () (per ADR-0050 §4.1)。</summary>
    private Expression ParsePostfixExpr()
    {
        var start = Peek().Span.Start;
        var expr = ParsePrimary();

        while (true)
        {
            switch (Peek().Kind)
            {
                case TokenKind.Dot:
                case TokenKind.NullCondMember:  // ?. (modern null-conditional member)
                    {
                        var nullCond = Peek().Kind == TokenKind.NullCondMember;
                        _pos++;
                        if (!Check(TokenKind.Identifier) && !Check(TokenKind.Keyword))
                            throw new ParserException(Peek().Span, "[modern] expected member name after '.'");
                        var member = Read().Text;
                        IReadOnlyList<Expression>? args = null;
                        if (Check(TokenKind.LParen))
                        {
                            _pos++;
                            args = ParseArgumentList(TokenKind.RParen);
                            Expect(TokenKind.RParen, "')'");
                        }
                        expr = new MemberExpression(expr, member, Static: false, args, nullCond, SpanFrom(start));
                        continue;
                    }
                case TokenKind.DoubleColon:
                    {
                        _pos++;
                        if (!Check(TokenKind.Identifier) && !Check(TokenKind.Keyword))
                            throw new ParserException(Peek().Span, "[modern] expected member name after '::'");
                        var member = Read().Text;
                        IReadOnlyList<Expression>? args = null;
                        if (Check(TokenKind.LParen))
                        {
                            _pos++;
                            args = ParseArgumentList(TokenKind.RParen);
                            Expect(TokenKind.RParen, "')'");
                        }
                        expr = new MemberExpression(expr, member, Static: true, args, NullConditional: false, SpanFrom(start));
                        continue;
                    }
                case TokenKind.LBracket:
                case TokenKind.NullCondIndex:  // ?[ (modern null-conditional index)
                    {
                        var nullCond = Peek().Kind == TokenKind.NullCondIndex;
                        _pos++;
                        SkipNewLinesAndComments();
                        var index = ParseExpression();
                        SkipNewLinesAndComments();
                        Expect(TokenKind.RBracket, "']'");
                        // ADR-0050 §4.1: ?[ null 条件索引——null 目标返回 null 而不抛错。
                        expr = new IndexExpression(expr, index, SpanFrom(start)) { NullConditional = nullCond };
                        continue;
                    }
                case TokenKind.LParen:
                    {
                        // 方法/函数调用 expr(args)
                        _pos++;
                        var args = ParseArgumentList(TokenKind.RParen);
                        Expect(TokenKind.RParen, "')'");
                        if (expr is MemberExpression m && m.Arguments is null)
                        {
                            expr = new MemberExpression(m.Target, m.MemberName, m.Static, args, m.NullConditional, m.Span);
                        }
                        else if (expr is CommandExpression ce)
                        {
                            // ADR-0050 §8.2: 表达式上下文中的 name(args) 函数调用——
                            // ParsePrimary 返回空参 CommandExpression，此处补上括号内参数。
                            var cmdArgs = args.Select(a => (CommandArgument)new PositionalArgument(a, a.Span)).ToList();
                            expr = new CommandExpression(ce.Name, cmdArgs, ce.Kind, SpanFrom(start), ce.Block, ce.HeadExpression);
                        }
                        else if (expr is VariableExpression ve)
                        {
                            // bare-identifier 表达式上下文：upper(args) → CommandExpression
                            var cmdArgs = args.Select(a => (CommandArgument)new PositionalArgument(a, a.Span)).ToList();
                            expr = new CommandExpression(ve.Name, cmdArgs, CommandInvocationKind.Direct, SpanFrom(start));
                        }
                        continue;
                    }
            }
            break;
        }

        return expr;
    }

    private List<Expression> ParseArgumentList(TokenKind closing)
    {
        var list = new List<Expression>();
        SkipNewLinesAndComments();
        if (Check(closing)) return list;
        while (true)
        {
            SkipNewLinesAndComments();
            list.Add(ParseExpression());
            SkipNewLinesAndComments();
            if (!Match(TokenKind.Comma)) break;
            SkipNewLinesAndComments();
        }
        return list;
    }

    // =========================================================================
    // Primary 表达式（含 modern 扩展：lambda / match / spread / 数组字面量）
    // =========================================================================

    private Expression ParsePrimary()
    {
        var start = Peek().Span.Start;
        var tok = Peek();

        switch (tok.Kind)
        {
            case TokenKind.Integer:
                _pos++;
                return new LiteralExpression(tok.Value, LiteralKind.Integer, tok.Span);
            case TokenKind.Double:
                _pos++;
                return new LiteralExpression(tok.Value, LiteralKind.Double, tok.Span);
            case TokenKind.Real:
                _pos++;
                return new LiteralExpression(tok.Value, LiteralKind.Double, tok.Span);
            case TokenKind.String:
                _pos++;
                // 双引号字符串：经 ExpandableStringParser 解析插值段（$var/${name}/$(expr)）。
                // 无 $ 段时退化为 LiteralExpression(Kind=String)。Per T-103~T-105（借鉴 PS ScanDollarInStringExpandable）。
                return ExpandableStringParser.Parse(tok.Value?.ToString() ?? string.Empty, isHereString: false, tok.Span);
            case TokenKind.HereString:
                _pos++;
                // 双引号 here-string：同样经 ExpandableStringParser 解析插值段。
                return ExpandableStringParser.Parse(tok.Value?.ToString() ?? string.Empty, isHereString: true, tok.Span);
            case TokenKind.RawString:
                _pos++;
                return new LiteralExpression(tok.Value, LiteralKind.RawString, tok.Span);
            case TokenKind.SingleString:
            case TokenKind.HereSingleString:
                _pos++;
                return new LiteralExpression(tok.Value, LiteralKind.SingleString, tok.Span);
            case TokenKind.Boolean:
                _pos++;
                return new LiteralExpression(tok.Value, LiteralKind.Boolean, tok.Span);
            case TokenKind.Null:
                _pos++;
                return new LiteralExpression(null, LiteralKind.Null, tok.Span);

            case TokenKind.Variable:
            case TokenKind.ScopedVariable:
            case TokenKind.EnvVariable:
                // 检测 lambda: $x => ... (modern, per ADR-0050 §3.3)
                if (Peek(1).Kind == TokenKind.Arrow)
                    return ParseLambda();
                _pos++;
                return ParseVariable(tok);

            case TokenKind.TypeRef:
                {
                    _pos++;
                    var typeRef = ParseTypeRefText(tok.Text);
                    // 类型转换 [int]$x
                    if (Check(TokenKind.Variable) || Check(TokenKind.ScopedVariable) || Check(TokenKind.EnvVariable))
                    {
                        var operand = ParseUnary();
                        return new CastExpression(typeRef, operand, SpanFrom(start));
                    }
                    // [Type]::Member 静态访问或单纯类型引用
                    return new CastExpression(typeRef, new LiteralExpression(null, LiteralKind.Null, tok.Span), tok.Span);
                }

            case TokenKind.LParen:
                {
                    // 检测 lambda: (params) => ... (modern, per ADR-0050 §3.3)
                    if (IsLambdaAhead())
                        return ParseLambda();
                    // 子表达式 (expr)
                    _pos++;
                    SkipNewLinesAndComments();
                    var inner = ParseExpression();
                    SkipNewLinesAndComments();
                    Expect(TokenKind.RParen, "')'");
                    return new SubExpressionExpression(inner, SpanFrom(start));
                }

            case TokenKind.LBracket:
                {
                    // modern 数组字面量: [1, 2, 3] (per ADR-0050 §4.1)
                    _pos++; // [
                    var elements = new List<Expression>();
                    SkipNewLinesAndComments();
                    if (!Check(TokenKind.RBracket))
                    {
                        while (true)
                        {
                            SkipNewLinesAndComments();
                            elements.Add(ParseExpression());
                            SkipNewLinesAndComments();
                            if (!Match(TokenKind.Comma)) break;
                            SkipNewLinesAndComments();
                        }
                    }
                    Expect(TokenKind.RBracket, "']'");
                    return new ArrayExpression(elements, SpanFrom(start));
                }

            case TokenKind.At:
                {
                    // @{ hash } / @( array )（保留 PS 形式以兼容）
                    _pos++;
                    if (Check(TokenKind.LBrace))
                    {
                        _pos++;
                        var entries = new List<KeyValuePair<Expression, Expression>>();
                        SkipSeparators();
                        while (!Check(TokenKind.RBrace) && !AtEnd)
                        {
                            SkipNewLinesAndComments();
                            if (Check(TokenKind.RBrace)) break;
                            var key = ParseExpression();
                            Expect(TokenKind.Assign, "'='");
                            var val = ParseExpression();
                            entries.Add(new KeyValuePair<Expression, Expression>(key, val));
                            SkipSeparators();
                        }
                        Expect(TokenKind.RBrace, "'}'");
                        return new HashExpression(entries, SpanFrom(start));
                    }
                    if (Check(TokenKind.LParen))
                    {
                        _pos++;
                        var elements = new List<Expression>();
                        SkipNewLinesAndComments();
                        if (!Check(TokenKind.RParen))
                        {
                            while (true)
                            {
                                SkipNewLinesAndComments();
                                elements.Add(ParseExpression());
                                SkipNewLinesAndComments();
                                if (!Match(TokenKind.Comma)) break;
                                SkipNewLinesAndComments();
                            }
                        }
                        Expect(TokenKind.RParen, "')'");
                        return new ArrayExpression(elements, SpanFrom(start));
                    }
                    throw new ParserException(Peek().Span, "[modern] expected '{' or '(' after '@'");
                }

            case TokenKind.LBrace:
                // ADR-0050 §4: { k: v } 哈希字面量（JSON 风格）。
                // 启发式检测：{ 后紧跟 key 然后冒号（key 为 string/identifier/number/variable）。
                if (IsHashLiteralAhead())
                    return ParseHashLiteral(start);
                return ParseScriptBlockExpression();

            case TokenKind.Identifier:
                // ADR-0053 §3: name!(...) / name!{...} / name![...] 宏调用。
                if (Peek(1).Kind == TokenKind.Bang)
                    return ParseMacroInvocation(start, tok.Text);
                // 检测 lambda: x => ... (modern bare-identifier lambda, per ADR-0050 §3.3)
                if (Peek(1).Kind == TokenKind.Arrow)
                    return ParseLambda();
                // ADR-0050 §7.2: bare-identifier 表达式上下文 → VariableExpression（无 $ 前缀变量）
                if (_bareIdentifierAsVariable)
                {
                    _pos++;
                    return new VariableExpression(tok.Text, VariableScopeKind.Default, tok.Span);
                }
                // D-306: 命令参数位置的裸标识符作为字符串字面量传给命令，而非命令调用。
                // 参考 PS 行为：命令参数位置所有裸标识符（如 cd .. 中的 ..、cp a b 中的 a/b）
                // 均为字符串。之前此处生成 CommandExpression 导致 cd ..; pwd 在 AST 路径失败
                // （.. 被误判为命令调用 → "command not found: .."）。
                // D-313: 扩展为消费 . 标识符序列（如 toDelete.txt、file1.txt.bak），
                // 避免 ParsePostfixExpr 将 . 误处理为成员访问（"toDelete".txt）。
                // 命令参数位置的 a.b.c 应为字符串 "a.b.c"，不是成员访问链。
                if (_inCommandArgument)
                {
                    _pos++;
                    var text = tok.Text;
                    // 消费后续的 .Identifier 序列（文件名/路径中的点）。
                    while (Check(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
                    {
                        _pos++; // 消费 .
                        var part = Read();
                        text += "." + part.Text;
                    }
                    return new LiteralExpression(text, LiteralKind.String, SpanFrom(start));
                }
                // 裸标识符在表达式上下文作为命令名处理
                _pos++;
                return new CommandExpression(tok.Text, Array.Empty<CommandArgument>(), CommandInvocationKind.Direct, tok.Span);

            case TokenKind.Spread:
                // spread 运算符 ...$arr (modern, per ADR-0050 §2.1)
                _pos++;
                var spreadOperand = ParseUnary();
                return new UnaryExpression(UnaryOperator.Spread, spreadOperand, Postfix: false, SpanFrom(start));

            case TokenKind.Keyword:
                // match 表达式 (modern, per ADR-0050 §5.2)
                if (string.Equals(tok.Text, "match", StringComparison.OrdinalIgnoreCase))
                    return ParseMatchExpression();
                // ADR-0051 §3: async { ... } 块表达式（表达式上下文）
                if (string.Equals(tok.Text, "async", StringComparison.OrdinalIgnoreCase))
                    return ParseAsyncBlockExpression();
                // ADR-0051 §2: await expr 表达式上下文入口（通常经 ParseUnary 进入，这里兜底）
                if (string.Equals(tok.Text, "await", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++; // await
                    var operand = ParseUnary();
                    return new AwaitExpressionAst(operand, SpanFrom(start));
                }
                throw new ParserException(tok.Span, $"[modern] unexpected keyword '{tok.Text}' in expression");

            default:
                throw new ParserException(tok.Span, $"[modern] unexpected token in expression: {tok.Kind} '{tok.Text}'");
        }
    }

    /// <summary>
    /// 现代语法保留字集合：禁止作为变量名（$fn / $match / $elif / $in 等）。
    /// Per T-111（栈式作用域语义检查）+ ADR-0050 §约束。
    /// 仅含现代语法特有 / 模式匹配关键字；PS 共有控制流关键字（if/else/while/for 等）
    /// 不在此列，以保持与 PowerShell 兼容语义。
    /// </summary>
    private static readonly HashSet<string> ModernReservedVariableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "fn", "match", "elif", "in",
        "async", "await", "export", "import",
        "macro", "macro_rules", "type",
    };

    /// <summary>检查变量名是否为现代语法保留字；若是则抛 ParserException。Per T-111。</summary>
    private static void EnsureNotReservedVariable(string name, SourceSpan span)
    {
        if (ModernReservedVariableNames.Contains(name))
            throw new ParserException(span,
                $"[modern] '{name}' is a reserved keyword and cannot be used as a variable name");
    }

    /// <summary>解析变量 token 为 VariableExpression。</summary>
    private static VariableExpression ParseVariable(Token tok)
    {
        switch (tok.Kind)
        {
            case TokenKind.Variable:
                {
                    var name = tok.Value?.ToString() ?? tok.Text;
                    EnsureNotReservedVariable(name, tok.Span);
                    return new VariableExpression(
                        name,
                        VariableScopeKind.Default, tok.Span);
                }
            case TokenKind.ScopedVariable:
                {
                    var full = tok.Value?.ToString() ?? tok.Text;
                    var idx = full.IndexOf(':');
                    var scopeName = idx > 0 ? full.Substring(0, idx) : "";
                    var name = idx > 0 ? full.Substring(idx + 1) : full;
                    var scope = scopeName.ToLowerInvariant() switch
                    {
                        "global" => VariableScopeKind.Global,
                        "script" => VariableScopeKind.Script,
                        "local" => VariableScopeKind.Local,
                        "private" => VariableScopeKind.Private,
                        "using" => VariableScopeKind.Using,
                        _ => VariableScopeKind.Default,
                    };
                    EnsureNotReservedVariable(name, tok.Span);
                    return new VariableExpression(name, scope, tok.Span);
                }
            case TokenKind.EnvVariable:
                {
                    var full = tok.Value?.ToString() ?? tok.Text;
                    var idx = full.IndexOf(':');
                    var name = idx > 0 ? full.Substring(idx + 1) : full;
                    return new VariableExpression(name, VariableScopeKind.Environment, tok.Span);
                }
            default:
                throw new ParserException(tok.Span, "[modern] not a variable token");
        }
    }

    // =========================================================================
    // Lambda 表达式（modern, per ADR-0050 §3.3）
    // =========================================================================

    /// <summary>解析 lambda：$x => expr / x => expr / ($x, $y) => expr / (x, y) => expr。</summary>
    private LambdaExpression ParseLambda()
    {
        var start = Peek().Span.Start;
        var parameters = new List<ParameterDeclaration>();

        if (Check(TokenKind.Variable) || Check(TokenKind.ScopedVariable) || Check(TokenKind.EnvVariable))
        {
            // 单参数 lambda: $x => ...
            var tok = Read();
            var name = tok.Value?.ToString() ?? tok.Text;
            EnsureNotReservedVariable(name, tok.Span);
            parameters.Add(new ParameterDeclaration(null, name, null, Mandatory: false));
        }
        else if (Check(TokenKind.Identifier))
        {
            // 单参数 lambda: x => ... (bare identifier)
            var idTok = Read();
            var name = idTok.Text;
            parameters.Add(new ParameterDeclaration(null, name, null, Mandatory: false));
        }
        else if (Check(TokenKind.LParen))
        {
            // 多参数 lambda: ($x, $y) => ... / (x, y) => ...
            _pos++; // (
            SkipNewLinesAndComments();
            if (!Check(TokenKind.RParen))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    SkipNewLinesAndComments();
                    var param = ParseLambdaParameter();
                    if (!seen.Add(param.Name))
                        throw new ParserException(Peek().Span,
                            $"[modern] duplicate parameter name '{param.Name}' in lambda parameter list");
                    parameters.Add(param);
                    SkipNewLinesAndComments();
                    if (!Match(TokenKind.Comma)) break;
                    SkipNewLinesAndComments();
                }
            }
            Expect(TokenKind.RParen, "')'");
        }
        else
        {
            throw new ParserException(Peek().Span, "[modern] expected lambda parameter list");
        }

        Expect(TokenKind.Arrow, "'=>'");
        SkipNewLinesAndComments();

        Expression body;
        if (Check(TokenKind.LBrace))
        {
            // 多语句 lambda: (x) => { stmt1; stmt2 } → ScriptBlockExpression
            body = ParseScriptBlockExpression();
        }
        else
        {
            body = ParseExpression();
        }

        return new LambdaExpression(parameters, body, SpanFrom(start));
    }

    /// <summary>解析 lambda 参数：$name / name / name: type / name = default。</summary>
    private ParameterDeclaration ParseLambdaParameter()
    {
        TypeReference? type = null;
        string name;
        SourceSpan nameSpan;

        if (Check(TokenKind.Variable) || Check(TokenKind.ScopedVariable) || Check(TokenKind.EnvVariable))
        {
            var tok = Read();
            name = tok.Value?.ToString() ?? tok.Text;
            nameSpan = tok.Span;
        }
        else if (Check(TokenKind.Identifier))
        {
            var idTok = Read();
            name = idTok.Text;
            nameSpan = idTok.Span;
            // 可选类型注解: name: type (modern, per ADR-0050 §7.2)
            if (Match(TokenKind.Colon))
            {
                SkipNewLinesAndComments();
                if (Check(TokenKind.Identifier) || Check(TokenKind.TypeRef))
                {
                    var typeName = Read().Text;
                    type = TypeReferences.Simple(typeName, Peek().Span);
                }
            }
        }
        else
        {
            throw new ParserException(Peek().Span, $"[modern] expected lambda parameter, got {Peek().Kind}");
        }

        EnsureNotReservedVariable(name, nameSpan);

        // 可选默认值
        Expression? defaultValue = null;
        if (Match(TokenKind.Assign))
        {
            defaultValue = ParseExpression();
        }

        return new ParameterDeclaration(type, name, defaultValue, Mandatory: false);
    }

    // =========================================================================
    // match 表达式（modern, per ADR-0050 §5.2 + ADR-0055 高级模式匹配）
    // =========================================================================

    /// <summary>
    /// 解析 match 表达式：match expr { pattern => arm; _ => arm }。
    /// Per ADR-0050 §5.2 + ADR-0055: 支持解构 / 范围 / 守卫 / OR / as 绑定等高级模式。
    /// </summary>
    private MatchExpression ParseMatchExpression()
    {
        var start = Peek().Span.Start;
        _pos++; // match
        SkipNewLinesAndComments();

        // subject 表达式（遇到 { 时停止，{ 启动 arms 块）
        var subject = ParseExpression();
        SkipNewLinesAndComments();
        Expect(TokenKind.LBrace, "'{'");
        SkipSeparators();

        var arms = new List<MatchArm>();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            SkipNewLinesAndComments();
            if (Check(TokenKind.RBrace)) break;

            // ADR-0055: 解析模式（含高级模式）。返回 (旧式 Expression?, 新式 PatternAst?)。
            // 旧式 Pattern 用于向后兼容（简单字面量），新式 AdvancedPattern 优先求值。
            Expression? legacyPattern = null;
            PatternAst? advancedPattern = null;

            // _ 表示 default (per ADR-0050 §5.2)
            if (Check(TokenKind.Identifier) && Peek().Text == "_")
            {
                _pos++; // 消费 _
                // _ 可后接 if 守卫 / as 绑定
                advancedPattern = FinishPatternSuffix(new WildcardPattern(SpanFrom(start)), start);
            }
            else
            {
                advancedPattern = ParseMatchPattern(start);
                // 简单字面量模式：同时填充 legacyPattern 以保持向后兼容。
                if (advancedPattern is LiteralPattern lp)
                    legacyPattern = lp.Value;
            }

            Expect(TokenKind.Arrow, "'=>'");
            SkipNewLinesAndComments();

            Expression body;
            if (Check(TokenKind.LBrace))
            {
                // 多语句 arm: pattern => { stmts } → ScriptBlockExpression
                body = ParseScriptBlockExpression();
            }
            else
            {
                body = ParseExpression();
            }

            arms.Add(new MatchArm(legacyPattern, body, advancedPattern));
            SkipSeparators();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new MatchExpression(subject, arms, SpanFrom(start));
    }

    /// <summary>
    /// ADR-0055: 解析 match 模式（不含守卫 / as 绑定后缀）。
    /// 处理：解构 `{ }` / `[ ]`、类型 `[Type]`、范围 `1..=10` / `1..10`、字面量、OR `|`。
    /// </summary>
    private PatternAst ParseMatchPattern(SourcePosition start)
    {
        var atom = ParseMatchPatternAtom(start);

        // OR 模式：atom | atom | ... (per ADR-0055 §5)
        if (Check(TokenKind.Pipe))
        {
            var alternatives = new List<PatternAst> { atom };
            while (Check(TokenKind.Pipe))
            {
                _pos++; // |
                SkipNewLinesAndComments();
                alternatives.Add(ParseMatchPatternAtom(start));
            }
            atom = new OrPattern(alternatives, SpanFrom(start));
        }

        return FinishPatternSuffix(atom, start);
    }

    /// <summary>解析模式原子（不含 OR / 守卫 / as 后缀）。</summary>
    private PatternAst ParseMatchPatternAtom(SourcePosition start)
    {
        SkipNewLinesAndComments();

        // 哈希解构 { name, age }
        if (Check(TokenKind.LBrace))
            return ParseHashDestructurePattern(start);

        // 数组解构 [a, b, ...rest]
        if (Check(TokenKind.LBracket))
            return ParseArrayDestructurePattern(start);

        // 类型模式 [Type]
        if (Check(TokenKind.TypeRef))
        {
            var t = Read();
            var typeRef = ParseTypeRefText(t.Text);
            return new TypePattern(typeRef, SpanFrom(start));
        }

        // 解析基础表达式（字面量 / 变量 / 数字 / 字符串）
        var leftExpr = ParsePrimary();

        // 范围模式：1..=10 (inclusive) / 1..10 (exclusive in pattern context, per ADR-0055 §3)
        if (Check(TokenKind.DotDot) || Check(TokenKind.Range))
        {
            _pos++; // ..
            bool inclusive = false;
            if (Match(TokenKind.Assign))
                inclusive = true; // ..=
            SkipNewLinesAndComments();
            var rightExpr = ParsePrimary();
            return new RangePattern(leftExpr, rightExpr, inclusive, SpanFrom(start));
        }

        // 字面量模式
        return new LiteralPattern(leftExpr, SpanFrom(start));
    }

    /// <summary>ADR-0055 §2: 解析哈希解构模式 `{ name, age }`。</summary>
    private PatternAst ParseHashDestructurePattern(SourcePosition start)
    {
        _pos++; // {
        var fields = new List<DestructureField>();
        string? rest = null;
        SkipNewLinesAndComments();
        if (!Check(TokenKind.RBrace))
        {
            while (true)
            {
                SkipNewLinesAndComments();
                if (Check(TokenKind.Spread))
                {
                    _pos++;
                    if (!Check(TokenKind.Identifier))
                        throw new ParserException(Peek().Span, "[modern] expected rest name after '...' in destructure");
                    rest = Read().Text;
                    break;
                }
                if (!Check(TokenKind.Identifier))
                    throw new ParserException(Peek().Span, "[modern] expected field name in hash destructure");
                var fieldName = Read().Text;
                fields.Add(new DestructureField(fieldName, SpanFrom(start)));
                SkipNewLinesAndComments();
                if (!Match(TokenKind.Comma)) break;
                SkipNewLinesAndComments();
            }
        }
        Expect(TokenKind.RBrace, "'}'");
        return new DestructurePattern(DestructureKind.Hash, fields, rest, SpanFrom(start));
    }

    /// <summary>ADR-0055 §2: 解析数组解构模式 `[a, b, ...rest]`。</summary>
    private PatternAst ParseArrayDestructurePattern(SourcePosition start)
    {
        _pos++; // [
        var fields = new List<DestructureField>();
        string? rest = null;
        SkipNewLinesAndComments();
        if (!Check(TokenKind.RBracket))
        {
            while (true)
            {
                SkipNewLinesAndComments();
                if (Check(TokenKind.Spread))
                {
                    _pos++;
                    if (!Check(TokenKind.Identifier))
                        throw new ParserException(Peek().Span, "[modern] expected rest name after '...' in destructure");
                    rest = Read().Text;
                    break;
                }
                if (!Check(TokenKind.Identifier))
                    throw new ParserException(Peek().Span, "[modern] expected binding name in array destructure");
                var bindName = Read().Text;
                fields.Add(new DestructureField(bindName, SpanFrom(start)));
                SkipNewLinesAndComments();
                if (!Match(TokenKind.Comma)) break;
                SkipNewLinesAndComments();
            }
        }
        Expect(TokenKind.RBracket, "']'");
        return new DestructurePattern(DestructureKind.Array, fields, rest, SpanFrom(start));
    }

    /// <summary>
    /// ADR-0055 §4/§6: 处理模式后缀 `if cond`（守卫）与 `as name`（绑定）。
    /// 可同时出现：`pat if cond as name`。
    /// </summary>
    private PatternAst FinishPatternSuffix(PatternAst inner, SourcePosition start)
    {
        // 守卫：pat if cond (per ADR-0055 §4)
        if (CheckKeyword("if"))
        {
            _pos++; // if
            SkipNewLinesAndComments();
            var cond = ParseExpression();
            inner = new GuardPattern(inner, cond, SpanFrom(start));
        }
        // 绑定：pat as name (per ADR-0055 §6)
        if (MatchKeyword("as"))
        {
            SkipNewLinesAndComments();
            if (!Check(TokenKind.Identifier) && !Check(TokenKind.Variable))
                throw new ParserException(Peek().Span, "[modern] expected binding name after 'as'");
            var nameTok = Read();
            var bindName = nameTok.Value?.ToString() ?? nameTok.Text;
            inner = new AsPattern(inner, bindName, SpanFrom(start));
        }
        return inner;
    }

    // =========================================================================
    // Type Reference 解析
    // =========================================================================

    /// <summary>解析 [System.IO.File] 类型引用文本。</summary>
    private static TypeReference ParseTypeRefText(string text)
    {
        // 借鉴 PS ScanTypeName + TypeName 解析。
        // 支持：[int] / [int[]] / [int[,]] / [List[int]] / [Dictionary[string,int]] / [System.IO.File]
        // text 含外层 []，仅剥离首尾各一个（不剥离内部 []，避免 int[] 被误剥为 int）。
        var inner = text;
        if (inner.StartsWith('[')) inner = inner[1..];
        if (inner.EndsWith(']')) inner = inner[..^1];
        var span = new SourceSpan(new SourcePosition(0, 0, 0), new SourcePosition(0, 0, 0));

        // 查找第一个未配对的 '[' —— 区分数组后缀 [int[]] vs 泛型 [List[int]]
        // [int[]]: inner="int[]"，第一个 '[' 在末尾，是数组后缀
        // [List[int]]: inner="List[int]"，第一个 '[' 在中间，是泛型参数
        // [int[,]]: inner="int[,]"，数组后缀带逗号
        // 策略：从左到右找第一个深度为 1 的 '['，判断其前缀是否为类型名（含字母）→ 泛型；否则数组后缀
        int bracketIdx = -1;
        int depth = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '[') { depth++; if (depth == 1) bracketIdx = i; }
            else if (inner[i] == ']') depth--;
        }

        if (bracketIdx < 0)
        {
            // 无 '['，简单类型：[int] / [System.IO.File]
            return new TypeReference(inner, false, 0, null, span);
        }

        var prefix = inner.Substring(0, bracketIdx);
        var bracketContent = inner.Substring(bracketIdx);

        // 判断是数组后缀还是泛型：
        //   数组后缀：bracketContent 内全是 ',' 或空（如 [] / [,] / [,,]）
        //   泛型参数：bracketContent 内有类型名（如 [int] / [string,int]）
        var stripped = bracketContent.Trim('[', ']');
        bool isGeneric = stripped.Any(c => char.IsLetterOrDigit(c) || c == '_' || c == '.');

        if (isGeneric)
        {
            // 泛型：[List[int]] / [Dictionary[string,int]]
            // prefix 是泛型类型名，stripped 是泛型参数列表（逗号分隔，每个可能是嵌套类型）
            var genericArgs = stripped.Split(',')
                .Select(arg => ParseTypeRefText(arg.Trim()))
                .ToList();
            return new TypeReference(prefix, false, 0, genericArgs, span);
        }
        else
        {
            // 数组后缀：[int[]] / [int[,]]
            int rank = stripped.Count(c => c == ',') + 1;
            return new TypeReference(prefix, true, rank, null, span);
        }
    }

    // =========================================================================
    // ADR-0053 §2: macro_rules! 宏定义解析
    // =========================================================================

    /// <summary>
    /// 解析 macro_rules! name { (pattern) => { expansion } ... }。Per ADR-0053 §2.
    /// 每个 arm：(pattern_tokens) => { expansion_tokens } 或 (pattern_tokens) => expression
    /// </summary>
    private Statement ParseMacroDefinition(SourcePosition start)
    {
        _pos++; // 消费 macro_rules（Identifier）
        _pos++; // 消费 !（Bang）
        var nameTok = Expect(TokenKind.Identifier, "macro name");
        Expect(TokenKind.LBrace, "'{'");

        var arms = new List<MacroArm>();
        SkipSeparators();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            SkipSeparators();
            if (Check(TokenKind.RBrace)) break;

            // pattern: ( tokens )
            var pattern = CaptureDelimitedTokens(TokenKind.LParen, TokenKind.RParen);
            SkipNewLinesAndComments();

            // => separator
            Expect(TokenKind.Arrow, "'=>'");
            SkipNewLinesAndComments();

            // expansion: { tokens } 或单个表达式 token 序列
            List<Token> expansion;
            if (Check(TokenKind.LBrace))
                expansion = CaptureDelimitedTokens(TokenKind.LBrace, TokenKind.RBrace);
            else
            {
                expansion = new List<Token>();
                while (!Check(TokenKind.NewLine) && !Check(TokenKind.Semicolon) && !Check(TokenKind.RBrace) && !AtEnd)
                {
                    expansion.Add(Peek());
                    _pos++;
                }
            }
            arms.Add(new MacroArm(pattern, expansion));
            SkipSeparators();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new MacroDefinitionStatement(nameTok.Text, arms, SpanFrom(start));
    }

    /// <summary>捕获匹配分隔符内的原始 token 序列（开闭分隔符被消费但不包含在结果中）。</summary>
    private List<Token> CaptureDelimitedTokens(TokenKind open, TokenKind close)
    {
        Expect(open, $"'{open}'");
        var result = new List<Token>();
        int depth = 1;
        while (depth > 0 && !AtEnd)
        {
            var t = Peek();
            if (t.Kind == open) depth++;
            else if (t.Kind == close)
            {
                depth--;
                if (depth == 0) { _pos++; break; }
            }
            result.Add(t);
            _pos++;
        }
        return result;
    }

    // =========================================================================
    // ADR-0053 §3: name!(...) 宏调用解析
    // =========================================================================

    /// <summary>
    /// 解析 name!(args) / name!{args} / name![args]。Per ADR-0053 §3.
    /// 捕获分隔符内的原始 token 序列作为宏参数。
    /// </summary>
    private Expression ParseMacroInvocation(SourcePosition start, string name)
    {
        _pos++; // 消费 Identifier（name）
        _pos++; // 消费 !（Bang）

        var (open, close) = Peek().Kind switch
        {
            TokenKind.LParen => (TokenKind.LParen, TokenKind.RParen),
            TokenKind.LBrace => (TokenKind.LBrace, TokenKind.RBrace),
            TokenKind.LBracket => (TokenKind.LBracket, TokenKind.RBracket),
            _ => throw new ParserException(Peek().Span, $"[modern] expected '(', '{{', or '[' after macro name '{name}!', got {Peek().Kind}"),
        };

        var args = CaptureDelimitedTokens(open, close);
        return new MacroInvocationExpression(name, args, SpanFrom(start));
    }

    // =========================================================================
    // ADR-0057 §3: type Name { ... } 自定义类型定义解析
    // =========================================================================

    /// <summary>
    /// 解析 type Name { field: Type; method(params): RetType { body } }。Per ADR-0057 §3.
    /// </summary>
    private Statement ParseTypeDefinition(SourcePosition start)
    {
        _pos++; // 消费 type（Identifier）
        var nameTok = Expect(TokenKind.Identifier, "type name");
        Expect(TokenKind.LBrace, "'{'");

        var members = new List<TypeMember>();
        SkipSeparators();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            SkipSeparators();
            if (Check(TokenKind.RBrace)) break;

            var memberStart = Peek().Span.Start;
            var memberNameTok = Expect(TokenKind.Identifier, "member name");

            // 方法：name(params): RetType { body }
            if (Check(TokenKind.LParen))
            {
                var parameters = ParseTypeMemberParameterList();
                SkipNewLinesAndComments();

                TypeReference? returnType = null;
                if (Match(TokenKind.Colon))
                {
                    SkipNewLinesAndComments();
                    returnType = ParseModernTypeReference();
                }

                SkipNewLinesAndComments();
                var body = ParseScriptBlockExpression();
                members.Add(new MethodMember(memberNameTok.Text, parameters, returnType, body, SpanFrom(memberStart)));
            }
            // 字段：name: Type;
            else
            {
                Expect(TokenKind.Colon, "':'");
                SkipNewLinesAndComments();
                var fieldType = ParseModernTypeReference();
                members.Add(new FieldMember(memberNameTok.Text, fieldType, SpanFrom(memberStart)));
            }
            SkipSeparators();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new TypeDefinitionStatement(nameTok.Text, members, SpanFrom(start));
    }

    /// <summary>解析类型成员参数列表 (param1: Type, param2: Type, ...)。</summary>
    private List<ParameterDeclaration> ParseTypeMemberParameterList()
    {
        Expect(TokenKind.LParen, "'('");
        var parameters = new List<ParameterDeclaration>();
        SkipNewLinesAndComments();
        if (!Check(TokenKind.RParen))
        {
            int pos = 0;
            while (true)
            {
                SkipNewLinesAndComments();
                var nameTok = Expect(TokenKind.Identifier, "parameter name");
                TypeReference? type = null;
                if (Match(TokenKind.Colon))
                {
                    SkipNewLinesAndComments();
                    type = ParseModernTypeReference();
                }
                parameters.Add(new ParameterDeclaration(type, nameTok.Text, null, false, pos++));
                SkipNewLinesAndComments();
                if (!Match(TokenKind.Comma)) break;
                SkipNewLinesAndComments();
            }
        }
        Expect(TokenKind.RParen, "')'");
        return parameters;
    }
}
