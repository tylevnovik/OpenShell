#nullable enable
// ADR-0045 §14 递归下降 Parser（PowerShell 语法）。
// 设计要点：
//   1. 与 ModernParser 共享同一组 AST 节点（per ADR-0050 §1.2）。
//   2. 命令模式 vs 表达式模式：行首 Identifier/& /. 进入命令模式，
//      $var/( /[ /数字/字符串/@ 进入表达式模式（可能赋值）。
//   3. 表达式优先级用 Pratt parser（binding power 表）。
//   4. 语句分隔：NewLine / Semicolon。注释 token 跳过。
//   5. 命令参数只解析到 primary level，避免与运算符冲突；表达式用 (...) 包裹。

using OpenShell.Parsing.Ast;
using System.Text;

namespace OpenShell.Parsing;

/// <summary>PowerShell 语法递归下降 Parser。Per ADR-0045 §14.</summary>
public sealed class PowerShellParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;
    /// <summary>原始源文本（可选，用于填充 ScriptBlockExpression.SourceText）。Per ADR-0046 §2.</summary>
    private readonly string? _source;
    /// <summary>源文件路径（可选，REPL 内为 null）。Per ADR-0046 §2.</summary>
    private readonly string? _fileName;

    public PowerShellParser(IReadOnlyList<Token> tokens, string? source = null, string? fileName = null)
    {
        _tokens = tokens;
        _pos = 0;
        _source = source;
        _fileName = fileName;
    }

    /// <summary>便捷入口：source → tokens → AST。</summary>
    public static ScriptBlockAst Parse(string source)
        => new PowerShellParser(new Tokenizer(source).Tokenize(), source).ParseScript();

    /// <summary>便捷入口（带源文件路径）：source → tokens → AST。Per ADR-0046 §2.</summary>
    public static ScriptBlockAst Parse(string source, string? fileName)
        => new PowerShellParser(new Tokenizer(source).Tokenize(), source, fileName).ParseScript();

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
    // 游标辅助
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
            throw new ParserException(Peek().Span, $"expected {what}, got {Peek().Kind} '{Peek().Text}'");
        return Read();
    }

    private SourceSpan SpanFrom(SourcePosition start)
        => new(start, Peek().Span.End);

    /// <summary>跳过语句分隔符（NewLine / Semicolon / 注释）。</summary>
    private void SkipSeparators()
    {
        while (Check(TokenKind.NewLine) || Check(TokenKind.Semicolon)
               || Check(TokenKind.LineComment) || Check(TokenKind.BlockComment))
            _pos++;
    }

    /// <summary>仅跳过换行和注释（保留分号）。</summary>
    private void SkipNewLinesAndComments()
    {
        while (Check(TokenKind.NewLine) || Check(TokenKind.LineComment) || Check(TokenKind.BlockComment))
            _pos++;
    }

    // =========================================================================
    // 顶层入口
    // =========================================================================

    /// <summary>解析整个脚本。可选 param() 块 + 语句列表。</summary>
    public ScriptBlockAst ParseScript()
    {
        var start = Peek().Span.Start;
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
            // 语句后必须有分隔符
            if (!Check(TokenKind.NewLine) && !Check(TokenKind.Semicolon) && !AtEnd)
            {
                // ADR-0050 §2.2: .ps1 模式拒绝现代运算符（&& || ?? ? ?. ?[）。
                // 这些运算符已从表达式/一元/后缀解析路径移除（T-002），
                // 若语句后出现则说明用户在 .ps1 中误用现代语法——应显式报错而非静默跳过。
                if (IsModernOperatorToken(Peek().Kind))
                {
                    throw new ParserException(Peek().Span,
                        $"[ps1] 现代运算符 '{Peek().Text}' 在 PowerShell 兼容模式下不可用；" +
                        $"请改用 .osh 现代语法或对应的 PS 运算符（如 -and / -or / -not）");
                }
                // 错误恢复：跳过非分隔 token 直到下一个分隔符
                _pos++;
            }
            SkipSeparators();
        }

        return new ScriptBlockAst(statements, parameters, SpanFrom(start));
    }

    // =========================================================================
    // Statement 分发
    // =========================================================================

    private Statement? ParseStatement()
    {
        var start = Peek().Span.Start;

        // ADR-0050 §5.1 + PS label：:label 语句标签声明（用于 break/continue label）。
        if (Check(TokenKind.Label))
        {
            var labelTok = Read();
            var labelName = labelTok.Text.TrimStart(':');
            var body = ParseStatement()
                ?? throw new ParserException(labelTok.Span, "[ps1] label must be followed by a statement");
            // 若体部为循环，将标签下放到循环节点，使其能内部匹配 continue label。
            body = AttachLoopLabel(body, labelName);
            return new LabeledStatement(labelName, body, SpanFrom(start));
        }

        // 控制流关键字
        if (CheckKeyword("if")) return ParseIf();
        if (CheckKeyword("switch")) return ParseSwitch();
        if (CheckKeyword("while")) return ParseWhile();
        if (CheckKeyword("do")) return ParseDoWhile();
        if (CheckKeyword("for")) return ParseFor();
        // foreach 既是 foreach 语句关键字，又是 ForEach-Object 命令别名（Per ADR-0012 §1/§7）。
        // 仅当后跟 `(` 时按 foreach 语句解析；否则按命令解析（如 `1..10 | foreach { $_ }`，
        // 或语句起始位置的 `foreach { $_ }` 形式，PowerShell 语义下视为 ForEach-Object 命令）。
        if (CheckKeyword("foreach") && Peek(1).Kind == TokenKind.LParen) return ParseForEach();
        if (CheckKeyword("try")) return ParseTry();
        if (CheckKeyword("function") || CheckKeyword("filter")) return ParseFunctionDefinition();
        if (CheckKeyword("return")) return ParseReturn();
        if (CheckKeyword("break")) { _pos++; var label = MatchLabel(); return new BreakStatement(label, SpanFrom(start)); }
        if (CheckKeyword("continue")) { _pos++; var label = MatchLabel(); return new ContinueStatement(label, SpanFrom(start)); }
        if (CheckKeyword("throw")) return ParseThrow();
        if (CheckKeyword("exit")) return ParseExit();
        if (CheckKeyword("param")) { _pos++; var ps = ParseParamParenList(); return new ParamBlockStatement(ps, SpanFrom(start)); }
        if (CheckKeyword("using")) return ParseUsing();

        // & 调用运算符 / . dot-source → 命令模式
        if (Check(TokenKind.Ampersand) || Check(TokenKind.DotSource))
            return ParsePipelineStatement(start);

        // $variable → 可能赋值，可能表达式语句
        if (Check(TokenKind.Variable) || Check(TokenKind.ScopedVariable) || Check(TokenKind.EnvVariable))
        {
            // 解析完整表达式（含二元运算符），再判断是否赋值目标
            var expr = ParseExpression();
            if (Peek().Kind.IsAssignmentOperator() && IsAssignTarget(expr))
            {
                var opTok = Read();
                var rhs = ParseExpression();
                var target = ExprToAssignTarget(expr, start);
                return new AssignmentStatement(target, opTok.Kind.ToAssignmentOperator(), rhs, SpanFrom(start));
            }
            // 否则作为表达式语句
            return new ExpressionStatement(expr, SpanFrom(start));
        }

        // 标识符 → 命令模式（可能是命令名）
        if (Check(TokenKind.Identifier))
            return ParsePipelineStatement(start);

        // foreach 关键字未匹配 foreach 语句形式（无 `(` 后跟）→ 命令模式（ForEach-Object 别名）
        if (CheckKeyword("foreach"))
            return ParsePipelineStatement(start);

        // ( / [Type] / 数字 / 字符串 / @ / ! / ~ / -not / ++ / -- → 表达式语句（可能赋值）
        if (IsExpressionStartToken(Peek().Kind))
        {
            var expr = ParseExpression();
            if (Peek().Kind.IsAssignmentOperator() && IsAssignTarget(expr))
            {
                var opTok = Read();
                var rhs = ParseExpression();
                var target = ExprToAssignTarget(expr, start);
                return new AssignmentStatement(target, opTok.Kind.ToAssignmentOperator(), rhs, SpanFrom(start));
            }
            // 表达式后跟随 | ：表达式作为管道头（如 1..5 | & { process { $_ } }）。
            if (Check(TokenKind.Pipe))
            {
                return BuildPipelineFromExpressionHead(expr, start);
            }
            // 表达式语句
            return new ExpressionStatement(expr, SpanFrom(start));
        }

        // 未知：错误恢复
        throw new ParserException(Peek().Span, $"unexpected token {Peek().Kind} '{Peek().Text}' at statement start");
    }

    private static bool IsExpressionStartToken(TokenKind k) =>
        k is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace
            or TokenKind.Integer or TokenKind.Double or TokenKind.String or TokenKind.SingleString
            or TokenKind.HereString or TokenKind.HereSingleString or TokenKind.Boolean or TokenKind.Null
            or TokenKind.At or TokenKind.TypeRef
            // ADR-0050 §2.2: .ps1 模式拒绝现代 !（Bang）；用 LogicalNot（-not）替代。
            or TokenKind.BitNot or TokenKind.Plus or TokenKind.Minus
            or TokenKind.PlusPlus or TokenKind.MinusMinus or TokenKind.LogicalNot;

    /// <summary>
    /// 判断 token 是否为 ADR-0050 现代运算符（.ps1 模式应拒绝）。
    /// Per ADR-0050 §2.2：.ps1 模式不识别 &amp;&amp;/||/??/?/?. /?[] 等现代形式。
    /// 这些 token 已从 TryGetBinaryOp / ParseUnary / ParsePostfixExpr 移除（T-002），
    /// 语句后若出现说明用户在 .ps1 中误用现代语法。
    /// </summary>
    private static bool IsModernOperatorToken(TokenKind k)
        => k is TokenKind.AmpAmp or TokenKind.PipePipe or TokenKind.DoubleQuestion
            or TokenKind.Question or TokenKind.NullCondMember or TokenKind.NullCondIndex
            or TokenKind.TildeEquals or TokenKind.TildeRegex;

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
                throw new ParserException(expr.Span, "invalid assignment target");
        }
    }

    /// <summary>判断表达式是否为合法的赋值目标（$var / $obj.Prop / $arr[i]）。</summary>
    private static bool IsAssignTarget(Expression expr)
        => expr is VariableExpression or MemberExpression or IndexExpression;

    private string? MatchLabel()
    {
        // PS 也支持 break label（无 : 前缀）；紧邻 Identifier（不跳过换行）
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
    // Pipeline 语句（命令模式）
    // =========================================================================

    private Statement ParsePipelineStatement(SourcePosition start)
    {
        var commands = new List<CommandExpression>();
        var first = ParseCommand();
        commands.Add(first);

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

        var pipe = new PipelineExpression(commands, SpanFrom(start));
        return new PipelineStatement(pipe, background, SpanFrom(start));
    }

    /// <summary>
    /// 构造以表达式作为管道头的 PipelineStatement。Per ADR-0010 + ADR-0046 §5.
    /// 例如 <c>1..5 | &amp; { process { $_ * 2 } }</c> 的 1..5 是 RangeExpression，作为管道源。
    /// </summary>
    private Statement BuildPipelineFromExpressionHead(Expression headExpr, SourcePosition start)
    {
        // 包装为虚拟 CommandExpression，HeadExpression 字段存原表达式。
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

        var pipe = new PipelineExpression(commands, SpanFrom(start));
        return new PipelineStatement(pipe, background, SpanFrom(start));
    }

    /// <summary>解析单个命令段。</summary>
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
        else if (CheckKeyword("foreach"))
        {
            // foreach 在管道上下文中作为 ForEach-Object 命令别名（Per ADR-0012 §7）。
            // 语句起始位置的 foreach 在 ParseStatement 中已通过 lookahead 区分。
            name = Read().Text;
        }
        else
        {
            throw new ParserException(Peek().Span, $"expected command name, got {Peek().Kind}");
        }

        // 解析参数列表
        var args = ParseCommandArguments();

        return new CommandExpression(name, args, kind, SpanFrom(start), blockExpr);
    }

    /// <summary>解析命令参数列表（位置/命名/switch/脚本块）。被 ParseCommand 与 CallOperator 表达式共用。</summary>
    private List<CommandArgument> ParseCommandArguments()
    {
        var args = new List<CommandArgument>();
        while (true)
        {
            SkipNewLinesAndComments();
            if (AtEnd) break;
            if (Check(TokenKind.Pipe) || Check(TokenKind.Semicolon) || Check(TokenKind.NewLine)
                || Check(TokenKind.RBrace) || Check(TokenKind.RParen) || Check(TokenKind.RBracket))
                break;
            if (Check(TokenKind.Background)) break;
            if (Check(TokenKind.Ampersand) && (Peek(1).Kind == TokenKind.NewLine || Peek(1).Kind == TokenKind.End)) break;

            // 命名参数 -Name / -Name:value / switch -Recurse
            if (Check(TokenKind.NamedParameter))
            {
                var tok = Read();
                // NamedParameter token 的 Value 是参数名（已含 : 语义）
                SkipNewLinesAndComments();
                var val = ParseArgumentExpression();
                args.Add(new NamedArgument(tok.Value?.ToString() ?? tok.Text, val, tok.Span));
                continue;
            }
            if (Check(TokenKind.SwitchParameter))
            {
                var tok = Read();
                var paramName = tok.Value?.ToString() ?? tok.Text.TrimStart('-');
                // PowerShell 语义：-Name value（空格分隔）也是命名参数。
                // tokenizer 对所有 -word 产生 SwitchParameter，parser 在此区分：
                // 后跟值起始 token → NamedArgument；否则 → SwitchArgument。
                // 参数绑定层（BuildArgs / PipelineExecutor）负责处理 bool 参数的 switch 语义。
                SkipNewLinesAndComments();
                if (IsArgumentStartToken(Peek().Kind))
                {
                    var val = ParseArgumentExpression();
                    args.Add(new NamedArgument(paramName, val, tok.Span));
                }
                else
                {
                    args.Add(new SwitchArgument(paramName, tok.Span));
                }
                continue;
            }

            // 脚本块参数 { ... }
            if (Check(TokenKind.LBrace))
            {
                var block = ParseScriptBlockExpression();
                args.Add(new ScriptBlockArgument(block, block.Span));
                continue;
            }

            // 位置参数（primary 表达式）
            if (IsArgumentStartToken(Peek().Kind))
            {
                var argExpr = ParseArgumentExpression();
                args.Add(new PositionalArgument(argExpr, argExpr.Span));
                continue;
            }

            // 其余 token 结束命令
            break;
        }
        return args;
    }

    private static bool IsArgumentStartToken(TokenKind k) =>
        k is TokenKind.Variable or TokenKind.ScopedVariable or TokenKind.EnvVariable
            or TokenKind.Integer or TokenKind.Double or TokenKind.String or TokenKind.SingleString
            or TokenKind.HereString or TokenKind.HereSingleString or TokenKind.Boolean or TokenKind.Null
            or TokenKind.LParen or TokenKind.At or TokenKind.TypeRef or TokenKind.Identifier
            or TokenKind.Minus or TokenKind.Plus;

    /// <summary>
    /// 命令参数表达式：primary 级别（避免与运算符冲突）。
    /// 支持逗号分隔的数组参数（PowerShell 语义）：<c>-Property Name, Size</c>
    /// 解析为 <see cref="ArrayExpression"/>（Per ADR-0012 §1/§6）。
    /// </summary>
    private Expression ParseArgumentExpression()
    {
        var start = Peek().Span.Start;
        var first = ParsePostfixExpr();

        // Comma-separated values form a single array argument.
        // e.g., `Select-Object -Property Name, Size` → ArrayExpression([Name, Size])
        if (!Check(TokenKind.Comma)) return first;

        var elements = new List<Expression> { first };
        while (Match(TokenKind.Comma))
        {
            SkipNewLinesAndComments();
            elements.Add(ParsePostfixExpr());
        }
        return new ArrayExpression(elements, SpanFrom(start));
    }

    // =========================================================================
    // 控制流语句
    // =========================================================================

    private Statement ParseIf()
    {
        var start = Peek().Span.Start;
        _pos++; // if
        var branches = new List<ConditionalBody>();
        var cond = ParseParenExpression();
        var body = ParseBlock();
        branches.Add(new ConditionalBody(cond, body));

        while (true)
        {
            // 保存位置：若无 elseif/else 则恢复，避免消费后续语句的换行分隔符。
            var savedPos = _pos;
            SkipNewLinesAndComments();
            if (MatchKeyword("elseif") || MatchKeyword("elif"))
            {
                var ec = ParseParenExpression();
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
                    // 把 nested if 作为单语句 else body
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
                // default { }
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
        var cond = ParseParenExpression();
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
        else throw new ParserException(Peek().Span, "expected 'while' or 'until' after do-block");
        var cond = ParseParenExpression();
        return new DoWhileStatement(body, cond, until, SpanFrom(start));
    }

    private Statement ParseFor()
    {
        var start = Peek().Span.Start;
        _pos++; // for
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

    private Statement ParseForEach()
    {
        var start = Peek().Span.Start;
        _pos++; // foreach
        Expect(TokenKind.LParen, "'('");
        SkipNewLinesAndComments();
        // foreach ($x in $coll) — 标准项迭代形式（Per ADR-0045 §5）。
        Expect(TokenKind.Variable, "variable");
        var varName = _tokens[_pos - 1].Value?.ToString() ?? _tokens[_pos - 1].Text;
        SkipNewLinesAndComments();
        if (!MatchKeyword("in"))
            throw new ParserException(Peek().Span, "expected 'in' in foreach");
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
            SkipNewLinesAndComments();
            if (!CheckKeyword("catch")) break;
            _pos++; // catch
            SkipNewLinesAndComments();
            // catch [Type1] [Type2] as $ex
            var types = new List<TypeReference>();
            while (Check(TokenKind.TypeRef))
            {
                var t = Read();
                types.Add(ParseTypeRefText(t.Text));
                SkipNewLinesAndComments();
            }
            string? varName = null;
            if (MatchKeyword("as"))
            {
                if (Check(TokenKind.Variable))
                    varName = Read().Value?.ToString() ?? Read().Text;
            }
            var cbody = ParseBlock();
            catches.Add(new CatchClause(types.Count > 0 ? types : null, varName, cbody));
        }

        SkipNewLinesAndComments();
        if (MatchKeyword("finally"))
        {
            finallyBody = ParseBlock();
        }

        return new TryStatement(body, catches, finallyBody, SpanFrom(start));
    }

    private Statement ParseFunctionDefinition()
    {
        var start = Peek().Span.Start;
        var kindTok = Read(); // function / filter
        var fnKind = kindTok.Text.ToLowerInvariant() == "filter" ? FunctionKind.Filter : FunctionKind.Function;

        // 函数名
        if (!Check(TokenKind.Identifier))
            throw new ParserException(Peek().Span, "expected function name");
        var name = Read().Text;

        // 可选参数列表（在 { 之前）：function Foo([int]$x, [string]$y) { ... }
        List<ParameterDeclaration> parameters = new();
        if (Match(TokenKind.LParen))
        {
            parameters = ParseParamDeclarations(closing: TokenKind.RParen);
            Expect(TokenKind.RParen, "')'");
        }
        SkipNewLinesAndComments();

        // 函数体 { ... }，可能含 param() 块
        var body = ParseScriptBlockExpression();
        // 如果 body 内有 param()，提取出来
        if (parameters.Count > 0 && body.Parameters.Count > 0)
        {
            // 优先用 param() 块的参数
            parameters = new List<ParameterDeclaration>(body.Parameters);
        }
        else if (parameters.Count > 0)
        {
            // 把外层参数注入 body，保留 body 的 SourceText/SourceFile（per ADR-0046 §2/§10）。
            body = body with { Parameters = parameters };
        }

        return new FunctionDefinitionStatement(name, parameters, body, fnKind, SpanFrom(start));
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
        // using namespace System.IO / using module ./foo.psm1 / using assembly ...
        if (!Check(TokenKind.Identifier))
            throw new ParserException(Peek().Span, "expected using kind (namespace/module/assembly)");
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
        // target：剩余文本到行尾
        var sb = new StringBuilder();
        while (!IsEndOfStatement(Peek()) && !AtEnd)
        {
            sb.Append(Peek().Text).Append(' ');
            _pos++;
        }
        return new UsingStatement(kind, sb.ToString().Trim(), SpanFrom(start));
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
        // 先处理 TypeRef（[string] / [int[]] 等，放宽 TryLexTypeRef 后小写别名也被识别）。
        if (Check(TokenKind.TypeRef))
        {
            var t = Read();
            type = ParseTypeRefText(t.Text);
        }
        while (Check(TokenKind.LBracket))
        {
            // 可能是类型 [int] 或特性 [Parameter()]
            var save = _pos;
            _pos++; // [
            if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.LParen)
            {
                // 特性 [Parameter(Mandatory=$true)]，简化跳过到匹配 ]
                int depth = 1;
                _pos++; // (
                while (!AtEnd && depth > 0)
                {
                    if (Check(TokenKind.LParen)) depth++;
                    else if (Check(TokenKind.RParen)) depth--;
                    if (depth > 0) _pos++;
                }
                if (Check(TokenKind.RParen)) _pos++; // )
                // 跳过特性名
                if (Check(TokenKind.RBracket)) _pos++;
                // 简化：不解析特性内容
                continue;
            }
            // 回退：当作类型引用
            _pos = save;
            if (Check(TokenKind.LBracket) && Peek(1).Kind == TokenKind.Identifier
                     && Peek(2).Kind == TokenKind.RBracket)
            {
                // 兜底：未被 tokenizer 识别为 TypeRef 的 [TypeName]，手动解析。
                var lbPos = Peek().Span.Start;
                _pos++; // [
                var typeName = Read().Text;
                Expect(TokenKind.RBracket, "']' to close type reference");
                type = TypeReferences.Simple(typeName, SpanFrom(lbPos));
            }
            else
            {
                // 跳过
                _pos++;
            }
        }

        // $name
        if (!Check(TokenKind.Variable))
            throw new ParserException(Peek().Span, "expected parameter name");
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

    // =========================================================================
    // 脚本块表达式 { ... }
    // =========================================================================

    private ScriptBlockExpression ParseScriptBlockExpression()
    {
        var start = Peek().Span.Start;
        Expect(TokenKind.LBrace, "'{'");
        SkipSeparators();

        // [CmdletBinding(...)] 特性：per ADR-0049 §1. 出现在 param() 之前。
        // 其他 [Attribute] 也允许出现在此处，本 ADR 仅解析 CmdletBinding，其余跳过。
        // 先跳过任何非 CmdletBinding 的 [Attribute(...)] 块。
        while (TrySkipUnknownAttribute())
            SkipSeparators();
        CmdletBindingAttributeAst? cmdletBinding = TryParseCmdletBindingAttribute();
        if (cmdletBinding is not null)
        {
            SkipSeparators();
            // CmdletBinding 后可能还有其他特性，跳过。
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
        // 提前探测：[CmdletBinding 或 [CmdletBinding(
        if (!string.Equals(Peek(1).Text, "CmdletBinding", StringComparison.OrdinalIgnoreCase)
            || Peek(1).Kind != TokenKind.Identifier)
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
        // 不消费 CmdletBinding（应被 TryParseCmdletBindingAttribute 捕获）。
        if (string.Equals(Peek(1).Text, "CmdletBinding", StringComparison.OrdinalIgnoreCase))
            return false;
        var save = _pos;
        _pos++; // [
        _pos++; // Identifier
        // 可选 (...)
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
                // 视为 enum 名（如 High / Medium / None / Low）。
                return tok.Text;
            default:
                _pos++;
                return tok.Text;
        }
    }

    // =========================================================================
    // 表达式解析（Pratt parser）
    // =========================================================================

    public Expression ParseExpression()
    {
        var left = ParseBinary(0);
        // 表达式层赋值：$x = expr / $x.y = expr / $arr[i] = expr（返回被赋的值）
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

            // 范围 .. 产生 RangeExpression（非 BinaryExpression）
            if (tok.Kind is TokenKind.Range or TokenKind.DotDot)
            {
                if (65 >= minPrec)
                {
                    _pos++;
                    SkipNewLinesAndComments();
                    var rangeEnd = ParseBinary(66);
                    left = new RangeExpression(left, rangeEnd, SpanFrom(left.Span.Start));
                    continue;
                }
                break;
            }

            if (!TryGetBinaryOp(tok.Kind, out var op, out var prec, out var rightAssoc))
                break;
            if (prec < minPrec) break;

            _pos++;
            int nextMin = rightAssoc ? prec : prec + 1;
            SkipNewLinesAndComments();
            var right = ParseBinary(nextMin);
            left = new BinaryExpression(left, op, right, SpanFrom(left.Span.Start));
        }
        return left;
    }

    private static bool TryGetBinaryOp(TokenKind k, out BinaryOperator op, out int prec, out bool rightAssoc)
    {
        op = default; prec = 0; rightAssoc = false;
        switch (k)
        {
            // 三元 ?: 在 ParseTernary 处理，这里不列入。
            // ADR-0050 §2.2: .ps1 模式拒绝现代运算符 ??（modern null 合并）；PS 无对应运算符。
            // DoubleQuestion 已移除（T-002 残留清理）。
            // ADR-0050 §2.2: .ps1 模式拒绝现代逻辑运算符 && ||（PS 使用 -and -or）。
            // PipePipe / AmpAmp 已移除；保留 PS 风格 LogicalOr / LogicalAnd / LogicalXor。
            case TokenKind.LogicalOr:
            case TokenKind.LogicalXor:
                op = k == TokenKind.LogicalXor ? BinaryOperator.BitwiseXor : BinaryOperator.Or;
                prec = 30; return true;
            case TokenKind.LogicalAnd:
                op = BinaryOperator.And; prec = 40; return true;
            case TokenKind.CmpIs: op = BinaryOperator.Is; prec = 50; return true;
            case TokenKind.CmpIsNot: op = BinaryOperator.IsNot; prec = 50; return true;
            case TokenKind.CmpAs: op = BinaryOperator.As; prec = 50; return true;
            case TokenKind.CmpIn: op = BinaryOperator.In; prec = 50; return true;
            case TokenKind.CmpNotIn: op = BinaryOperator.NotIn; prec = 50; return true;
            case TokenKind.CmpContains: op = BinaryOperator.Contains; prec = 50; return true;
            case TokenKind.CmpNotContains: op = BinaryOperator.NotContains; prec = 50; return true;

            // ADR-0050 §2.2: .ps1 模式仅接受 PS 风格比较运算符 -eq/-ne/-lt/-gt/-le/-ge。
            // 现代风格 Equals/NotEquals/Lt/Gt/Le/Ge 已移除（T-002）。
            case TokenKind.CmpEq:
                op = BinaryOperator.Eq; prec = 60; return true;
            case TokenKind.CmpNe:
                op = BinaryOperator.Ne; prec = 60; return true;
            case TokenKind.CmpLt:
                op = BinaryOperator.Lt; prec = 60; return true;
            case TokenKind.CmpGt:
                op = BinaryOperator.Gt; prec = 60; return true;
            case TokenKind.CmpLe:
                op = BinaryOperator.Le; prec = 60; return true;
            case TokenKind.CmpGe:
                op = BinaryOperator.Ge; prec = 60; return true;
            case TokenKind.CmpLike: op = BinaryOperator.Like; prec = 60; return true;
            case TokenKind.CmpNotLike: op = BinaryOperator.NotLike; prec = 60; return true;
            case TokenKind.CmpMatch: op = BinaryOperator.Match; prec = 60; return true;
            case TokenKind.CmpNotMatch: op = BinaryOperator.NotMatch; prec = 60; return true;

            case TokenKind.CmpBand: op = BinaryOperator.BitwiseAnd; prec = 70; return true;
            case TokenKind.CmpBor: op = BinaryOperator.BitwiseOr; prec = 70; return true;
            case TokenKind.BcmpBxor: op = BinaryOperator.BitwiseXor; prec = 70; return true;
            case TokenKind.BitAnd: op = BinaryOperator.BitwiseAnd; prec = 70; return true;
            case TokenKind.BitOr: op = BinaryOperator.BitwiseOr; prec = 70; return true;
            case TokenKind.BitXor: op = BinaryOperator.BitwiseXor; prec = 70; return true;

            case TokenKind.CmpShl:
            case TokenKind.Shl:
                op = BinaryOperator.ShiftLeft; prec = 80; return true;
            case TokenKind.CmpShr:
            case TokenKind.Shr:
                op = BinaryOperator.ShiftRight; prec = 80; return true;

            case TokenKind.Plus: op = BinaryOperator.Add; prec = 90; return true;
            case TokenKind.Minus: op = BinaryOperator.Subtract; prec = 90; return true;

            case TokenKind.Star: op = BinaryOperator.Multiply; prec = 100; return true;
            case TokenKind.Slash: op = BinaryOperator.Divide; prec = 100; return true;
            case TokenKind.Percent: op = BinaryOperator.Modulo; prec = 100; return true;
            case TokenKind.Caret: op = BinaryOperator.Power; prec = 120; rightAssoc = true; return true;
        }
        return false;
    }

    private Expression ParseUnary()
    {
        var start = Peek().Span.Start;
        var k = Peek().Kind;
        // 前缀 ++ -- ! - + -not ~
        if (k is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            var tok = Read();
            var operand = ParseUnary();
            var uop = tok.Kind == TokenKind.PlusPlus ? UnaryOperator.PrefixIncrement : UnaryOperator.PrefixDecrement;
            return new UnaryExpression(uop, operand, Postfix: false, SpanFrom(start));
        }
        // ADR-0050 §2.2: .ps1 模式拒绝现代运算符 !（Bang）；PS 使用 -not。
        // Bang 一元已移除（T-002 残留清理）。仅保留 PS 风格 LogicalNot。
        if (k is TokenKind.LogicalNot)
        {
            var tok = Read();
            var operand = ParseUnary();
            return new UnaryExpression(UnaryOperator.Not, operand, Postfix: false, SpanFrom(start));
        }
        if (k is TokenKind.BitNot)
        {
            Read();
            var operand = ParseUnary();
            return new UnaryExpression(UnaryOperator.BitwiseNot, operand, Postfix: false, SpanFrom(start));
        }
        if (k is TokenKind.Minus)
        {
            Read();
            var operand = ParseUnary();
            return new UnaryExpression(UnaryOperator.Negate, operand, Postfix: false, SpanFrom(start));
        }
        if (k is TokenKind.Plus)
        {
            Read();
            var operand = ParseUnary();
            return new UnaryExpression(UnaryOperator.Plus, operand, Postfix: false, SpanFrom(start));
        }

        var expr = ParsePostfixExpr();

        // 后缀 ++ --
        if (Peek().Kind is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            var tok = Read();
            var uop = tok.Kind == TokenKind.PlusPlus ? UnaryOperator.PostfixIncrement : UnaryOperator.PostfixDecrement;
            expr = new UnaryExpression(uop, expr, Postfix: true, SpanFrom(start));
        }

        // ADR-0050 §2.2: .ps1 模式拒绝现代三元运算符 ? :（modern 语法）。
        // Question 三元已移除（T-002 残留清理）。PS 无三元运算符，用 if-else 替代。

        return expr;
    }

    /// <summary>后缀：成员访问 . / :: / 索引 [] / 调用 ()。</summary>
    private Expression ParsePostfixExpr()
    {
        var start = Peek().Span.Start;
        var expr = ParsePrimary();

        while (true)
        {
            switch (Peek().Kind)
            {
                case TokenKind.Dot:
                    {
                        // ADR-0050 §2.2: .ps1 模式拒绝现代 ?.（NullCondMember）；仅接受普通 . 成员访问。
                        // NullCondMember 分支已移除（T-002 残留清理）。
                        _pos++;
                        if (!Check(TokenKind.Identifier) && !Check(TokenKind.Keyword))
                            throw new ParserException(Peek().Span, "expected member name after '.'");
                        var member = Read().Text;
                        // 方法调用？
                        IReadOnlyList<Expression>? args = null;
                        if (Check(TokenKind.LParen))
                        {
                            _pos++;
                            args = ParseArgumentList(TokenKind.RParen);
                            Expect(TokenKind.RParen, "')'");
                        }
                        expr = new MemberExpression(expr, member, Static: false, args, NullConditional: false, SpanFrom(start));
                        continue;
                    }
                case TokenKind.DoubleColon:
                    {
                        _pos++;
                        if (!Check(TokenKind.Identifier) && !Check(TokenKind.Keyword))
                            throw new ParserException(Peek().Span, "expected member name after '::'");
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
                    {
                        // ADR-0050 §2.2: .ps1 模式拒绝现代 ?[]（NullCondIndex）；仅接受普通 [] 索引。
                        // NullCondIndex 分支已移除（T-002 残留清理）。
                        _pos++;
                        SkipNewLinesAndComments();
                        var index = ParseExpression();
                        SkipNewLinesAndComments();
                        Expect(TokenKind.RBracket, "']'");
                        expr = new IndexExpression(expr, index, SpanFrom(start));
                        continue;
                    }
                case TokenKind.LParen:
                    {
                        // 方法调用 expr(args)
                        _pos++;
                        var args = ParseArgumentList(TokenKind.RParen);
                        Expect(TokenKind.RParen, "')'");
                        if (expr is MemberExpression m && m.Arguments is null)
                        {
                            expr = new MemberExpression(m.Target, m.MemberName, m.Static, args, m.NullConditional, m.Span);
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
            case TokenKind.HereString:
                _pos++;
                return new LiteralExpression(tok.Value, LiteralKind.String, tok.Span);
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
                _pos++;
                return new VariableExpression(
                    tok.Value?.ToString() ?? tok.Text,
                    VariableScopeKind.Default, tok.Span);
            case TokenKind.ScopedVariable:
                {
                    _pos++;
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
                    return new VariableExpression(name, scope, tok.Span);
                }
            case TokenKind.EnvVariable:
                {
                    _pos++;
                    var full = tok.Value?.ToString() ?? tok.Text;
                    var idx = full.IndexOf(':');
                    var name = idx > 0 ? full.Substring(idx + 1) : full;
                    return new VariableExpression(name, VariableScopeKind.Environment, tok.Span);
                }

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
                    // [Type]::Member 静态访问
                    if (Check(TokenKind.DoubleColon))
                    {
                        return new CastExpression(typeRef, new LiteralExpression(null, LiteralKind.Null, tok.Span), tok.Span);
                    }
                    // 单纯类型引用
                    return new CastExpression(typeRef, new LiteralExpression(null, LiteralKind.Null, tok.Span), tok.Span);
                }

            case TokenKind.LParen:
                {
                    _pos++;
                    SkipNewLinesAndComments();
                    var inner = ParseExpression();
                    SkipNewLinesAndComments();
                    Expect(TokenKind.RParen, "')'");
                    return new SubExpressionExpression(inner, SpanFrom(start));
                }
            case TokenKind.At:
                {
                    // @{ hash } / @( array )
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
                    throw new ParserException(Peek().Span, "expected '{' or '(' after '@'");
                }
            case TokenKind.LBrace:
                return ParseScriptBlockExpression();

            case TokenKind.Identifier:
                // 现代语法中，标识符可能是 match 表达式关键字、lambda 等
                // PS 模式下裸标识符在表达式上下文罕见，作为命令名处理
                _pos++;
                return new CommandExpression(tok.Text, Array.Empty<CommandArgument>(), CommandInvocationKind.Direct, tok.Span);

            case TokenKind.Spread:
                _pos++;
                var spreadOperand = ParseUnary();
                return new UnaryExpression(UnaryOperator.Spread, spreadOperand, Postfix: false, SpanFrom(start));

            case TokenKind.Ampersand:
                {
                    // 表达式上下文中的 & 调用：& $sb arg1 arg2 → CommandExpression(CallOperator)
                    _pos++;
                    var nameExpr = ParsePrimary();
                    var name = nameExpr switch
                    {
                        LiteralExpression lit => lit.Value?.ToString() ?? "",
                        VariableExpression v => v.Name,
                        _ => "__call__",
                    };
                    var args = ParseCommandArguments();
                    return new CommandExpression(name, args, CommandInvocationKind.CallOperator, SpanFrom(start));
                }

            default:
                throw new ParserException(tok.Span, $"unexpected token in expression: {tok.Kind} '{tok.Text}'");
        }
    }

    /// <summary>解析 [System.IO.File] 类型引用文本。</summary>
    private static TypeReference ParseTypeRefText(string text)
    {
        // 借鉴 PS ScanTypeName + TypeName 解析（与 ModernParser.ParseTypeRefText 同步）。
        // 支持：[int] / [int[]] / [int[,]] / [List[int]] / [Dictionary[string,int]] / [System.IO.File]
        // text 含外层 []，仅剥离首尾各一个（不剥离内部 []，避免 int[] 被误剥为 int）。
        var inner = text;
        if (inner.StartsWith('[')) inner = inner[1..];
        if (inner.EndsWith(']')) inner = inner[..^1];
        var span = new SourceSpan(new SourcePosition(0, 0, 0), new SourcePosition(0, 0, 0));

        // 查找第一个深度为 1 的 '[' —— 区分数组后缀 [int[]] vs 泛型 [List[int]]
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
}

/// <summary>Parser 错误。携带 SourceSpan 便于定位。</summary>
public sealed class ParserException : Exception
{
    public SourceSpan Span { get; }
    public ParserException(SourceSpan span, string message) : base(message) => Span = span;
    public ParserException(SourceSpan span, string message, Exception inner) : base(message, inner) => Span = span;
}
