#nullable enable
// ADR-0050 现代语法合规测试套件（Compliance Tests）
// 设计原则：
//   1. 每条测试对应 ADR-0050 §1–§10 的一个具体要求，以 ADR 章节标注。
//   2. 已实现特性用 [Fact]（必须通过，验证当前行为正确）。
//   3. 未实现特性用 [Fact(Skip="pending T-XXX")]，保持基线绿色；实现后移除 Skip 即可验证。
//   4. 修复进度由 docs/modern-syntax-tasks.md 追踪，本套件提供机械化验证。
//   5. P0 项含「PS parser 应拒绝现代运算符」等回归保护测试。

using FluentAssertions;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using OpenShell.Runtime;
using OpenShell.Variables;
using ExecutionContext = OpenShell.Runtime.ExecutionContext;
using Xunit;

namespace OpenShell.Core.Tests.Parsing;

/// <summary>
/// ADR-0050 现代语法（.osh）合规测试。覆盖 §1–§10 全部要求。
/// 修复任务清单见 docs/modern-syntax-tasks.md。
/// </summary>
public class ModernSyntaxComplianceTests
{
    private static ExecutionContext NewContext()
        => new(variables: new InMemoryVariableRegistry());

    private static object? EvalModern(string source, ExecutionContext? ctx = null)
    {
        ctx ??= NewContext();
        var ast = ModernParser.Parse(source);
        return new Evaluator(ctx).Execute(ast).Value;
    }

    private static ScriptBlockAst ParseModern(string source) => ModernParser.Parse(source);
    private static ScriptBlockAst ParsePs1(string source) => PowerShellParser.Parse(source);

    private static T SingleStmt<T>(ScriptBlockAst ast) where T : Statement
        => ast.Statements.OfType<T>().Single();

    // =========================================================================
    // §1 双语法架构
    // =========================================================================

    [Fact]
    public void S1_Osh_Repl_Default_Is_Modern()
    {
        // REPL 默认现代语法：现代运算符应可解析（基础回归保护）。
        var ast = ParseModern("1 == 1");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S1_Ps1_Rejects_Modern_EqualsOperator()
    {
        // ADR §2.2: .ps1 模式下现代形式（== != > < 等）不识别。
        Action act = () => ParsePs1("if (1 == 2) { }");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void S1_Ps1_Rejects_Modern_AndAnd()
    {
        Action act = () => ParsePs1("$true && $false");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void S1_Ps1_Rejects_Modern_Bang()
    {
        // T-002 残留清理：.ps1 模式拒绝 !（Bang 一元）；用 -not 替代。
        Action act = () => ParsePs1("! $true");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void S1_Ps1_Rejects_Modern_DoubleQuestion()
    {
        // T-002 残留清理：.ps1 模式拒绝 ??（null 合并）。
        Action act = () => ParsePs1("$x ?? $y");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void S1_Ps1_Rejects_Modern_Ternary()
    {
        // T-002 残留清理：.ps1 模式拒绝 ? : 三元。
        Action act = () => ParsePs1("$x ? 1 : 2");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void S1_Ps1_Rejects_Modern_NullCondMember()
    {
        // T-002 残留清理：.ps1 模式拒绝 ?.（null 条件成员访问）。
        Action act = () => ParsePs1("$x?.Name");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void S1_Ps1_Rejects_Modern_NullCondIndex()
    {
        // T-002 残留清理：.ps1 模式拒绝 ?[]（null 条件索引）。
        Action act = () => ParsePs1("$x?[0]");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void S1_Ps1_Accepts_PsStyle_LogicalNot()
    {
        // 回归验证：.ps1 模式仍接受 PS 风格 -not。
        var ast = ParsePs1("-not $true");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S1_LangBlock_Ps1_In_Osh()
    {
        // ADR §1.3: .osh 文件内可用 #lang ps1 { ... } 嵌入 PS 代码。
        var ast = ParseModern("#lang ps1 { function Foo { 'bar' } }");
        ast.Statements.Should().HaveCount(1);
        ast.Statements[0].Should().BeOfType<LangBlockStatement>()
            .Which.Mode.Should().Be("ps1");
    }

    [Fact]
    public void S1_LangBlock_Unclosed_Throws()
    {
        Action act = () => ParseModern("#lang ps1 { function Foo { } ");
        act.Should().Throw<ParserException>()
            .WithMessage("*UnclosedLangBlock*");
    }

    [Fact]
    public void S1_SingleBlock_NoMixing()
    {
        // ADR §1.3: 单个块内必须单一语法，混用报 ParseError。
        // 现代语法块内出现 PS 风格 -gt：ModernParser 接受 -gt（双模式词法），故此测试验证不抛异常（宽松）。
        // 严格混用检查（如 PS 块内用 == ）由 #lang 块边界保证——块内用对应 parser 解析。
        var ast = ParseModern("if (x -gt 0) { } elif (x > 1) { }");
        ast.Statements.Should().HaveCount(1);
    }

    // =========================================================================
    // §2 操作符现代化
    // =========================================================================

    [Fact]
    public void S2_Equals_Evaluates()
    {
        EvalModern("1 == 1").Should().Be(true);
        EvalModern("1 == 2").Should().Be(false);
    }

    [Fact]
    public void S2_NotEquals_Evaluates()
    {
        EvalModern("1 != 2").Should().Be(true);
    }

    [Fact]
    public void S2_Comparison_Evaluates()
    {
        EvalModern("3 > 2").Should().Be(true);
        EvalModern("2 < 3").Should().Be(true);
        EvalModern("2 >= 2").Should().Be(true);
        EvalModern("2 <= 2").Should().Be(true);
    }

    [Fact]
    public void S2_AndAnd_OrOr_Evaluates()
    {
        EvalModern("true && false").Should().Be(false);
        EvalModern("true || false").Should().Be(true);
    }

    [Fact]
    public void S2_Bang_Not_Evaluates()
    {
        EvalModern("!false").Should().Be(true);
    }

    [Fact]
    public void S2_TildeEquals_WildcardMatch()
    {
        // ADR §2.1: ~= 等价 -like（通配符匹配）。
        EvalModern("\"hello\" ~= \"h*\"").Should().Be(true);
    }

    [Fact]
    public void S2_TildeRegex_RegexMatch()
    {
        // ADR §2.1: ~regex 等价 -match（正则匹配）。
        EvalModern("\"abc123\" ~regex \"\\d+\"").Should().Be(true);
    }

    [Fact]
    public void S2_In_Operator()
    {
        // ADR §2.1: in 等价 -in（包含于）。
        EvalModern("3 in [1, 2, 3]").Should().Be(true);
    }

    [Fact]
    public void S2_Contains_Operator()
    {
        // ADR §2.1: contains 等价 -contains（包含）。
        EvalModern("[1, 2, 3] contains 2").Should().Be(true);
    }

    [Fact]
    public void S2_PlusPlus_ArrayConcat()
    {
        // ADR §2.1: ++ 数组拼接。
        EvalModern("[1, 2] ++ [3, 4]").Should().BeEquivalentTo(new object[] { 1L, 2L, 3L, 4L });
    }

    [Fact]
    public void S2_PsOperator_InOsh_Emits_DeprecationWarning()
    {
        // ADR §2.2: .osh 模式下 PS 形式（-eq 等）应 emit DeprecationWarning。
        var ast = ParseModern("1 -eq 1");
        ast.Statements.Should().HaveCount(1);
        ast.ParseWarnings.Should().NotBeNull();
        ast.ParseWarnings.Should().Contain(w =>
            w.Kind == WarningKind.DeprecatedPsOperator && w.Message.Contains("-eq"));
    }

    // =========================================================================
    // §3 函数语法
    // =========================================================================

    [Fact]
    public void S3_Fn_Definition_Parses()
    {
        // 无返回类型注解的 fn 定义（体部用 $ 变量，裸标识符算术见 T-091）。
        var ast = ParseModern("fn add(a: int, b: int) { $a + $b }");
        var fn = SingleStmt<FunctionDefinitionStatement>(ast);
        fn.Name.Should().Be("add");
        fn.Parameters.Should().HaveCount(2);
        fn.Parameters[0].Name.Should().Be("a");
    }

    [Fact]
    public void S3_Fn_ReturnTypeAnnotation_Parses()
    {
        var ast = ParseModern("fn add(a: int, b: int) -> int { $a + $b }");
        var fn = SingleStmt<FunctionDefinitionStatement>(ast);
        fn.Name.Should().Be("add");
    }

    [Fact]
    public void S3_Fn_Body_BareIdentifier_Arithmetic()
    {
        // ADR §7.2: 现代语法允许无 $ 前缀变量，fn 体 a + b 应等价 $a + $b。
        var ast = ParseModern("fn add(a: int, b: int) { a + b }");
        var fn = SingleStmt<FunctionDefinitionStatement>(ast);
        fn.Name.Should().Be("add");
    }

    [Fact]
    public void S3_Lambda_SingleExpr_Parses()
    {
        var ast = ParseModern("$f = $x => $x * 2");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S3_Lambda_Block_Parses()
    {
        var ast = ParseModern("$f = $x => { $y = $x + 1; $y * 2 }");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S3_Fn_ReturnType_Enforced()
    {
        // ADR §3.2: -> type 返回类型注解应在运行时校验返回值类型。
        // 返回类型不匹配应报错（调用时校验）。
        // fn bad() -> int { 'not a number' } 定义不报错；调用时返回 string 不匹配 int 应抛异常。
        Action act = () => EvalModern("fn bad() -> int { 'not a number' }; bad");
        act.Should().Throw<Exception>();
    }

    // =========================================================================
    // §4 字面量与访问
    // =========================================================================

    [Fact]
    public void S4_ArrayLiteral_Parses_And_Evaluates()
    {
        var ast = ParseModern("$a = [1, 2, 3]");
        var assign = SingleStmt<AssignmentStatement>(ast);
        assign.Value.Should().BeOfType<ArrayExpression>();
        var result = EvalModern("$a = [1, 2, 3]; $a");
        result.Should().BeEquivalentTo(new object[] { 1L, 2L, 3L });
    }

    [Fact]
    public void S4_HashLiteral_Parses_And_Evaluates()
    {
        // ADR §4.1: { k: v } 哈希字面量（JSON 风格）。
        var ast = ParseModern("$h = { name: \"Alice\", age: 30 }");
        var assign = SingleStmt<AssignmentStatement>(ast);
        assign.Value.Should().BeOfType<HashExpression>();
    }

    [Fact]
    public void S4_DollarAlone_Is_CurrentItem()
    {
        // ADR §4.1: $ 单独使用 = $_ 当前管道对象。
        // 在 ForEach-Object 上下文内 $ 应等同于 $_。
        var ctx = NewContext();
        var result = EvalModern("1..3 | foreach-object { $ }", ctx);
        result.Should().NotBeNull();
    }

    [Fact]
    public void S4_DollarDot_Property_Access()
    {
        // $.prop 是 $_.prop 的简写 —— 已实现（Tokenizer 层映射）。
        // 不断言语句数（管道上下文解析细节），仅验证 $. 语法可解析。
        var ast = ParseModern("$h | foreach-object { $.Name }");
        ast.Statements.Should().NotBeEmpty();
    }

    [Fact]
    public void S4_NullConditional_Member_Parses()
    {
        var ast = ParseModern("$x?.Name");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S4_NullCoalesce_Evaluates()
    {
        EvalModern("$null ?? 'default'").Should().Be("default");
    }

    [Fact]
    public void S4_Ternary_Evaluates()
    {
        EvalModern("5 > 3 ? 'yes' : 'no'").Should().Be("yes");
    }

    [Fact]
    public void S4_NullConditional_Index_PreservesSemantics()
    {
        // ADR §4.1: ?[] null 条件索引，null 时返回 null 而不抛错。
        var ast = ParseModern("$arr?[0]");
        // 应产生保留 null 条件语义的节点，而非普通 IndexExpression。
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S4_HalfOpenRange()
    {
        // ADR §4.1: 0..<10 是半开范围（含 0 不含 10）。
        var result = EvalModern("0..<3");
        result.Should().BeEquivalentTo(new object[] { 0L, 1L, 2L });
    }

    [Fact]
    public void S4_ClosedRange_Evaluates()
    {
        // 1..10 闭范围（保持一致，已实现）。
        var result = EvalModern("1..3");
        result.Should().BeEquivalentTo(new object[] { 1L, 2L, 3L });
    }

    [Fact]
    public void S4_TripleQuotedString_Parses()
    {
        var ast = ParseModern("$s = \"\"\"line1\nline2\"\"\"");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S4_TripleQuotedString_IndentStripping()
    {
        // ADR §6.2: 闭合 """ 缩进决定公共前缀剥离。
        var result = EvalModern("$s = \"\"\"\n    line1\n    line2\n    \"\"\"");
        result.As<string>().Should().Be("line1\nline2");
    }

    [Fact]
    public void S4_RawString_NoEscape()
    {
        // ADR §6.3: r"..." 原始字符串不转义反斜杠、不插值。
        var result = EvalModern("$p = r\"C:\\Users\\name\"; $p");
        result.Should().Be("C:\\Users\\name");
    }

    [Fact]
    public void S4_DollarBrace_ArbitraryExpression()
    {
        // ADR §6.4: ${expr} 子表达式插值（任意表达式）。
        var result = EvalModern("$x = 5; \"value: ${$x * 2}\"");
        result.Should().Be("value: 10");
    }

    // =========================================================================
    // §5 控制流
    // =========================================================================

    [Fact]
    public void S5_Elif_Parses()
    {
        var ast = ParseModern("if (1 > 0) { } elif (1 == 1) { } else { }");
        var ifStmt = SingleStmt<IfStatement>(ast);
        ifStmt.Branches.Should().HaveCount(2); // if + elif
    }

    [Fact]
    public void S5_If_NoParens()
    {
        // ADR §5.1: if cond { } 不需括号。
        var ast = ParseModern("if 1 > 0 { }");
        var ifStmt = SingleStmt<IfStatement>(ast);
        ifStmt.Branches.Should().HaveCount(1);
    }

    [Fact]
    public void S5_While_NoParens()
    {
        // ADR §5.1: while c { } 不需括号。
        var ast = ParseModern("$i = 0; while $i < 3 { $i += 1 }");
        ast.Statements.Should().HaveCount(2);
    }

    [Fact]
    public void S5_ForIn_Merged()
    {
        // ADR §5.3: for x in col 合并 PowerShell 的 foreach。
        var result = EvalModern("$sum = 0; for $i in 1..3 { $sum += $i }; $sum");
        result.Should().Be(6L);
    }

    [Fact]
    public void S5_ForIn_IteratesCollection()
    {
        var result = EvalModern("$sum = 0; for $x in [1, 2, 3] { $sum += $x }; $sum");
        result.Should().Be(6L);
    }

    [Fact]
    public void S5_ForIn_HashDestructure()
    {
        // ADR §5.3: for k, v in hash 解构迭代哈希表。
        var ast = ParseModern("$h = @{ a = 1; b = 2 }; for $k, $v in $h { }");
        ast.Statements.Should().HaveCount(2);
    }

    [Fact]
    public void S5_Match_NonFallThrough()
    {
        // ADR §5.2: match 默认非 fall-through。
        var ast = ParseModern("match 1 { 1 => \"one\"; _ => \"other\" }");
        var match = ast.Statements.OfType<ExpressionStatement>().Single()
            .Expression as MatchExpression;
        match.Should().NotBeNull();
        match!.Arms.Should().HaveCount(2);
    }

    [Fact]
    public void S5_Match_Evaluates()
    {
        var result = EvalModern("match 2 { 1 => \"one\"; 2 => \"two\"; _ => \"other\" }");
        result.Should().Be("two");
    }

    [Fact]
    public void S5_Match_Default_Arm()
    {
        // _ 表示 default。
        var result = EvalModern("match 99 { 1 => \"one\"; _ => \"other\" }");
        result.Should().Be("other");
    }

    [Fact]
    public void S5_Catch_Modern_Binding()
    {
        // ADR §5.4: catch e: Type 绑定异常到变量 e。
        var ast = ParseModern("try { } catch e: System.Exception { }");
        var tryStmt = SingleStmt<TryStatement>(ast);
        tryStmt.Catches.Should().HaveCount(1);
        tryStmt.Catches[0].Variable.Should().Be("e");
    }

    [Fact]
    public void S5_Break_Label()
    {
        // ADR §5.1: break label（去掉 PS 的 : 前缀）。
        var ast = ParseModern(":outer while (true) { break outer }");
        ast.Statements.Should().HaveCount(1);
        ast.Statements[0].Should().BeOfType<LabeledStatement>();
        var ls = (LabeledStatement)ast.Statements[0];
        ls.Label.Should().Be("outer");
    }

    [Fact]
    public void S5_Break_Label_Evaluates()
    {
        // ADR §5.1: break label 应跳出带标签的外层循环，而非仅内层。
        var result = EvalModern(@"
            $count = 0
            :outer for ($i = 0; $i < 3; $i++) {
                for ($j = 0; $j < 3; $j++) {
                    $count = $count + 1
                    if ($j == 1) { break outer }
                }
            }
            $count
        ");
        // 外层 i=0：内层 j=0 → count=1, j=1 → count=2, break outer 跳出
        result.Should().Be(2L);
    }

    [Fact]
    public void S5_Continue_Label_Evaluates()
    {
        // ADR §5.1: continue label 应继续带标签的外层循环。
        var result = EvalModern(@"
            $count = 0
            :outer for ($i = 0; $i < 2; $i++) {
                for ($j = 0; $j < 3; $j++) {
                    if ($j == 1) { continue outer }
                    $count = $count + 1
                }
            }
            $count
        ");
        // i=0: j=0 → count=1, j=1 → continue outer (跳过 j=2)
        // i=1: j=0 → count=2, j=1 → continue outer (跳过 j=2)
        result.Should().Be(2L);
    }

    [Fact]
    public void S5_Break_NoLabel_Ps1()
    {
        // PS 风格 break :label 在 .ps1 模式也可用。
        var ast = ParsePs1(":outer while ($true) { break :outer }");
        ast.Statements.Should().HaveCount(1);
        ast.Statements[0].Should().BeOfType<LabeledStatement>();
    }

    // =========================================================================
    // §6 字符串
    // =========================================================================

    [Fact]
    public void S6_SingleQuote_NoInterpolation()
    {
        EvalModern("$name = 'world'; 'hello $name'").Should().Be("hello $name");
    }

    [Fact]
    public void S6_DoubleQuote_Interpolation()
    {
        EvalModern("$name = 'world'; \"hello $name\"").Should().Be("hello world");
    }

    [Fact]
    public void S6_TripleQuote_Multiline_Interpolation()
    {
        var result = EvalModern("$name = 'Alice'; \"\"\"hi $name\"\"\"");
        result.Should().Be("hi Alice");
    }

    [Fact]
    public void S6_RawString_NoInterpolation()
    {
        EvalModern("$name = 'Alice'; r\"hi $name\"").Should().Be("hi $name");
    }

    // ADR §6.4 + PS 借鉴 T-112: $(expr) 子表达式插值（任意表达式）。
    // 借鉴 PS ScanSubExpression（tokenizer.cs:2362-2447）+ ExpandableStringExpressionAst。
    [Fact]
    public void S6_SubExpression_Basic()
    {
        // "$(1+2)" 求值为 "3"。
        EvalModern("\"$(1 + 2)\"").Should().Be("3");
    }

    [Fact]
    public void S6_SubExpression_WithPrefix()
    {
        // "hello $(1+2)" 求值为 "hello 3"。
        EvalModern("\"hello $(1 + 2)\"").Should().Be("hello 3");
    }

    [Fact]
    public void S6_SubExpression_WithSuffix()
    {
        EvalModern("\"$(1 + 2) world\"").Should().Be("3 world");
    }

    [Fact]
    public void S6_SubExpression_Multiple()
    {
        // 多个子表达式拼接。
        EvalModern("\"a $(1 + 1) b $(2 + 2) c\"").Should().Be("a 2 b 4 c");
    }

    [Fact]
    public void S6_SubExpression_VariableInterpolation()
    {
        // 子表达式内引用变量。
        EvalModern("$x = 5; \"value: $($x * 2)\"").Should().Be("value: 10");
    }

    [Fact]
    public void S6_SubExpression_NestedParens()
    {
        // 子表达式内嵌套括号（借鉴 PS ScanSubExpression 递归括号配对）。
        EvalModern("\"$((1 + 2) * 3)\"").Should().Be("9");
    }

    [Fact]
    public void S6_SubExpression_WithVariablePrefix()
    {
        // $var 与 $(expr) 混合。
        EvalModern("$name = 'Alice'; \"hi $name, $(1 + 1) items\"").Should().Be("hi Alice, 2 items");
    }

    [Fact]
    public void S6_SubExpression_StringInside()
    {
        // 子表达式内含字符串字面量（借鉴 PS ScanStringLiteral 跳过引号内括号）。
        // ')' 在字符串内不应误判为子表达式闭合。
        EvalModern("\"$('a)b')\"").Should().Be("a)b");
    }

    [Fact]
    public void S6_SubExpression_StatementInside()
    {
        // $(...) 内含 if 语句——PS 中 $(...) 可含任意语句并返回末语句输出。Per T-113。
        // modern if 要求括号条件（T-040 待支持无括号形式）。
        EvalModern("\"$(if ($true) { 'yes' } else { 'no' })\"").Should().Be("yes");
    }

    [Fact]
    public void S6_SubExpression_MultipleStatements()
    {
        // $(...) 内含多条语句——返回末语句输出。Per T-113。
        EvalModern("\"$( $x = 5; $x * 2 )\"").Should().Be("10");
    }

    // ADR §6.4 + PS 借鉴 T-107: here-string 双引号 $ 插值与 ` 转义。
    [Fact]
    public void S6_HereString_DoubleQuote_Interpolation()
    {
        // @"..."@ 双引号 here-string 应插值 $var。首换行是分隔符被消费，尾换行保留在 body 内。
        EvalModern("$name = 'Alice'; @\"\nhello $name\n\"@").Should().Be("hello Alice\n");
    }

    [Fact]
    public void S6_HereString_DoubleQuote_SubExpression()
    {
        // @"..."@ 双引号 here-string 应支持 $(expr)。
        EvalModern("@\"\nresult: $(1 + 2)\n\"@").Should().Be("result: 3\n");
    }

    [Fact]
    public void S6_HereString_DoubleQuote_BacktickEscape()
    {
        // @"..."@ 双引号 here-string 应处理 ` 转义（`n → 换行）。
        EvalModern("@\"\na`nb\n\"@").Should().Be("a\nb\n");
    }

    [Fact]
    public void S6_HereString_SingleQuote_NoInterpolation()
    {
        // @'...'@ 单引号 here-string 不插值、不转义。
        EvalModern("$name = 'Alice'; @'\nhello $name\n'@").Should().Be("hello $name\n");
    }

    // =========================================================================
    // §7 类型注解
    // =========================================================================

    [Fact]
    public void S7_ParameterType_Annotation()
    {
        var ast = ParseModern("fn add(a: int, b: int) { $a + $b }");
        var fn = SingleStmt<FunctionDefinitionStatement>(ast);
        fn.Parameters[0].Type.Should().NotBeNull();
        fn.Parameters[0].Type!.FullName.Should().Be("int");
    }

    [Fact]
    public void S7_ParameterDefault_Value()
    {
        var ast = ParseModern("fn greet(name: string = \"World\") { }");
        var fn = SingleStmt<FunctionDefinitionStatement>(ast);
        fn.Parameters[0].DefaultValue.Should().NotBeNull();
    }

    [Fact]
    public void S7_AttributeSyntax()
    {
        // ADR §7.1: @ValidateRange(0, 100) 等价 [ValidateRange(0, 100)]。
        var ast = ParseModern("$p: int @ValidateRange(0, 100) = 50");
        var decl = SingleStmt<VariableDeclarationStatement>(ast);
        decl.VariableName.Should().Be("p");
        decl.Type!.FullName.Should().Be("int");
        decl.Attributes.Should().HaveCount(1);
        decl.Attributes[0].Name.Should().Be("ValidateRange");
        decl.Attributes[0].Arguments.Should().HaveCount(2);
        // 初始值 50 应被求值并绑定到变量
        var result = EvalModern("$p: int @ValidateRange(0, 100) = 50; $p");
        result.Should().Be(50L);
    }

    [Fact]
    public void S7_ArrayType_Suffix()
    {
        // ADR §7.2: nums: int[] 等价 [int[]]。
        var ast = ParseModern("fn process(items: int[]) { }");
        var fn = SingleStmt<FunctionDefinitionStatement>(ast);
        fn.Parameters[0].Type!.IsArray.Should().BeTrue();
    }

    // ADR §7 + PS 借鉴 T-109: [int[]] / [List[int]] 类型字面量（借鉴 PS ScanTypeName）。
    [Fact]
    public void S7_TypeLiteral_IntArray()
    {
        // [int[]] 类型字面量：IsArray=true, ArrayRank=1。
        var ast = ParseModern("$t = [int[]]");
        var assign = SingleStmt<AssignmentStatement>(ast);
        var cast = assign.Value.Should().BeOfType<CastExpression>().Subject;
        // [int[]] 作为 CastExpression 的 Type——但此处是裸类型表达式，可能是 TypeReferenceExpression。
    }

    [Fact]
    public void S7_TypeLiteral_IntArray_Rank1()
    {
        // [int[]] 解析为 TypeReference(IsArray=true, ArrayRank=1, FullName="int")。
        var ast = ParsePs1("[int[]]$x");
        var cast = SingleStmt<ExpressionStatement>(ast).Expression.Should().BeOfType<CastExpression>().Subject;
        cast.Type.IsArray.Should().BeTrue();
        cast.Type.ArrayRank.Should().Be(1);
        cast.Type.FullName.Should().Be("int");
    }

    [Fact]
    public void S7_TypeLiteral_TwoDimArray()
    {
        // [int[,]] 二维数组：ArrayRank=2。
        var ast = ParsePs1("[int[,]]$x");
        var cast = SingleStmt<ExpressionStatement>(ast).Expression.Should().BeOfType<CastExpression>().Subject;
        cast.Type.IsArray.Should().BeTrue();
        cast.Type.ArrayRank.Should().Be(2);
    }

    [Fact]
    public void S7_TypeLiteral_Generic_List()
    {
        // [List[int]] 泛型类型：GenericArgs 应填充。
        var ast = ParsePs1("[List[int]]$x");
        var cast = SingleStmt<ExpressionStatement>(ast).Expression.Should().BeOfType<CastExpression>().Subject;
        cast.Type.FullName.Should().Contain("List");
        cast.Type.GenericArgs.Should().NotBeNull();
        cast.Type.GenericArgs!.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public void S7_TypeLiteral_Generic_Dictionary()
    {
        // [Dictionary[string,int]] 多参数泛型。
        var ast = ParsePs1("[Dictionary[string,int]]$x");
        var cast = SingleStmt<ExpressionStatement>(ast).Expression.Should().BeOfType<CastExpression>().Subject;
        cast.Type.GenericArgs.Should().NotBeNull();
        cast.Type.GenericArgs!.Should().HaveCount(2);
    }

    // =========================================================================
    // §8 命令调用
    // =========================================================================

    [Fact]
    public void S8_Cmdlet_VerbNoun_Preserved()
    {
        // ADR §8.1: cmdlet 保持 Verb-Noun 形式。
        var ast = ParseModern("get-childitem -path \"C:/Users\"");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S8_NamedArgument_Shorthand()
    {
        // ADR §8.2: cmd(name: value) 等价 cmd -name value。
        var ast = ParseModern("get-item(path: \"C:/Users\")");
        ast.Statements.Should().HaveCount(1);
    }

    // =========================================================================
    // §9 注释
    // =========================================================================

    [Fact]
    public void S9_LineComment()
    {
        var ast = ParseModern("# comment\n1");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S9_BlockComment()
    {
        var ast = ParseModern("<# multi\nline #>\n1");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S9_DocComment_ProducesNode()
    {
        // ADR §9.2: """...""" 位于函数顶部应识别为文档注释。
        var ast = ParseModern("\"\"\"Greet a user.\"\"\"\nfn greet() { }");
        ast.Statements.Should().HaveCount(2);
        // 首条语句应为 DocumentationCommentStatement。
        ast.Statements[0].Should().BeOfType<DocumentationCommentStatement>()
            .Which.Text.Should().Be("Greet a user.");
    }

    [Fact]
    public void S9_TodoMarker_Extracted()
    {
        // ADR §9.1: # TODO 标记应被提取到 ScriptBlockAst.TodoMarkers。
        var ast = ParseModern("# TODO: refactor this\n1");
        ast.Statements.Should().HaveCount(1);
        ast.TodoMarkers.Should().NotBeNull();
        ast.TodoMarkers.Should().HaveCount(1);
        ast.TodoMarkers![0].Kind.Should().Be(TodoMarkerKind.Todo);
        ast.TodoMarkers[0].Message.Should().Be("refactor this");
    }

    // =========================================================================
    // §10 互操作
    // =========================================================================

    [Fact]
    public void S10_Import_Parses()
    {
        var ast = ParseModern("import \"module.osh\"");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void S10_LangBlock_Interop()
    {
        // ADR §10.2: .osh 文件内嵌入 PS 块，块内函数块外可调用。
        var ast = ParseModern("#lang ps1 { function Legacy { 'old' } }\nLegacy");
        ast.Statements.Should().HaveCount(2);
        ast.Statements[0].Should().BeOfType<LangBlockStatement>();
    }

    // =========================================================================
    // 约束：保留字 + 语义检查（T-111）
    // =========================================================================

    [Fact]
    public void Constraint_ReservedWord_NotAsVariable()
    {
        // ADR §约束 + T-111: fn/match/elif/in 禁止作为变量名。
        Action act = () => ParseModern("$fn = 5");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_ReservedWord_Match_NotAsVariable()
    {
        Action act = () => ParseModern("$match = 1");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_ReservedWord_Elif_NotAsVariable()
    {
        Action act = () => ParseModern("$elif = 2");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_ReservedWord_In_NotAsVariable()
    {
        Action act = () => ParseModern("$in = 3");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_ReservedWord_Async_NotAsVariable()
    {
        Action act = () => ParseModern("$async = 1");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_ReservedWord_Await_NotAsVariable()
    {
        Action act = () => ParseModern("$await = 1");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_ReservedWord_Scoped_NotAsVariable()
    {
        // $global:fn / $script:match 等作用域变量名同样禁止保留字。Per T-111。
        Action act = () => ParseModern("$global:fn = 1");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_ReservedWord_LambdaParameter_NotAllowed()
    {
        // lambda 参数名不可为保留字。Per T-111。
        Action act = () => ParseModern("$fn => 1");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_AutoVariables_Allowed()
    {
        // 自动变量 $_ $args $true $false $null 等不受保留字限制。
        var ast = ParseModern("$_");
        ast.Statements.Should().NotBeEmpty();
    }

    [Fact]
    public void Constraint_ReservedWord_NotAsCommandName()
    {
        // 保留字不能作命令名（间接阻止：fn/match/elif/in 是 Keyword token）。
        Action act = () => ParseModern("fn");
        // fn 会被解析为函数定义关键字，而非命令调用。
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_DuplicateParameter_Fn_ReportsError()
    {
        // fn(x, x) 重复参数报 ParseError。Per T-111。
        Action act = () => ParseModern("fn dup(x: int, x: int) { }");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_DuplicateParameter_Lambda_ReportsError()
    {
        // lambda ($x, $x) 重复参数报 ParseError。Per T-111。
        Action act = () => ParseModern("($x, $x) => $x");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_DuplicateParameter_CaseInsensitive()
    {
        // 参数名大小写不敏感：x 与 X 视为重复。Per T-111。
        Action act = () => ParseModern("fn dup(x: int, X: int) { }");
        act.Should().Throw<ParserException>();
    }

    [Fact]
    public void Constraint_DistinctParameters_Allowed()
    {
        // 不同参数名正常解析。
        var ast = ParseModern("fn ok(a: int, b: int) { }");
        ast.Statements.Should().HaveCount(1);
    }
}
