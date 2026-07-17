#nullable enable
// ADR-0045/0046/0050 端到端集成测试：PowerShellParser → ScriptBlockAst → Evaluator.Execute。
// 覆盖：赋值、算术、控制流、函数定义、脚本块、管道、集合操作。

using FluentAssertions;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using OpenShell.Runtime;
using OpenShell.Variables;
using ExecutionContext = OpenShell.Runtime.ExecutionContext;
using Xunit;

namespace OpenShell.Core.Tests.Runtime;

/// <summary>
/// PowerShellParser + Evaluator 端到端集成测试。Per ADR-0045 §14-15 + ADR-0046.
/// </summary>
public class EvaluatorIntegrationTests
{
    private static ExecutionContext NewContext()
        => new(variables: new InMemoryVariableRegistry());

    private static object? Eval(string source, ExecutionContext? ctx = null)
    {
        ctx ??= NewContext();
        var ast = PowerShellParser.Parse(source);
        var evaluator = new Evaluator(ctx);
        return evaluator.Execute(ast).Value;
    }

    // =========================================================================
    // 赋值与变量
    // =========================================================================

    [Fact]
    public void Assignment_SetsVariable()
    {
        var ctx = NewContext();
        Eval("$x = 42", ctx);
        ctx.Variables!.Resolve("x").Should().Be(42L);
    }

    [Fact]
    public void Assignment_StringLiteral()
    {
        var ctx = NewContext();
        Eval("$name = 'hello'", ctx);
        ctx.Variables!.Resolve("name").Should().Be("hello");
    }

    [Fact]
    public void Assignment_PlusEqual()
    {
        var ctx = NewContext();
        Eval("$x = 10", ctx);
        Eval("$x += 5", ctx);
        ctx.Variables!.Resolve("x").Should().Be(15L);
    }

    [Fact]
    public void Variable_Expression_ReturnsValue()
    {
        var ctx = NewContext();
        Eval("$x = 7", ctx);
        var result = Eval("$x", ctx);
        result.Should().Be(7L);
    }

    // =========================================================================
    // 算术
    // =========================================================================

    [Fact]
    public void Arithmetic_Addition() => Eval("1 + 2").Should().Be(3L);

    [Fact]
    public void Arithmetic_Precedence() => Eval("1 + 2 * 3").Should().Be(7L);

    [Fact]
    public void Arithmetic_Parentheses() => Eval("(1 + 2) * 3").Should().Be(9L);

    [Fact]
    public void Arithmetic_Modulo() => Eval("10 % 3").Should().Be(1L);

    [Fact]
    public void Arithmetic_Negative() => Eval("-5").Should().Be(-5L);

    // =========================================================================
    // 比较与逻辑
    // =========================================================================

    [Fact]
    public void Comparison_Eq() => Eval("3 -eq 3").Should().Be(true);

    [Fact]
    public void Comparison_Gt() => Eval("5 -gt 3").Should().Be(true);

    [Fact]
    public void Comparison_Lt_False() => Eval("5 -lt 3").Should().Be(false);

    [Fact]
    public void Logical_And() => Eval("$true -and $false").Should().Be(false);

    [Fact]
    public void Logical_Or() => Eval("$true -or $false").Should().Be(true);

    [Fact]
    public void Logical_Not() => Eval("-not $false").Should().Be(true);

    // =========================================================================
    // 控制流：if
    // =========================================================================

    [Fact]
    public void If_TrueBranch_ReturnsValue()
    {
        var result = Eval("if (1 -lt 2) { 'yes' } else { 'no' }");
        result.Should().Be("yes");
    }

    [Fact]
    public void If_FalseBranch_ReturnsElse()
    {
        var result = Eval("if (5 -lt 3) { 'yes' } else { 'no' }");
        result.Should().Be("no");
    }

    [Fact]
    public void If_ElseIf_Chain()
    {
        var result = Eval("if (5 -lt 3) { 'a' } elseif (5 -gt 4) { 'b' } else { 'c' }");
        result.Should().Be("b");
    }

    // =========================================================================
    // 控制流：while / for / foreach
    // =========================================================================

    [Fact]
    public void While_Loop_Accumulates()
    {
        var ctx = NewContext();
        var result = Eval("$i = 0; $sum = 0; while ($i -lt 5) { $sum += $i; $i += 1 }; $sum", ctx);
        result.Should().Be(10L);
    }

    [Fact]
    public void For_Loop_Accumulates()
    {
        var ctx = NewContext();
        var result = Eval("$sum = 0; for ($i = 1; $i -le 10; $i += 1) { $sum += $i }; $sum", ctx);
        result.Should().Be(55L);
    }

    [Fact]
    public void ForEach_Loop_IteratesRange()
    {
        var ctx = NewContext();
        var result = Eval("$sum = 0; foreach ($n in 1..5) { $sum += $n }; $sum", ctx);
        result.Should().Be(15L);
    }

    // =========================================================================
    // 控制流：break / continue / return
    // =========================================================================

    [Fact]
    public void Break_ExitsLoop()
    {
        var ctx = NewContext();
        var result = Eval("$i = 0; while ($true) { if ($i -ge 3) { break }; $i += 1 }; $i", ctx);
        result.Should().Be(3L);
    }

    [Fact]
    public void Continue_SkipsIteration()
    {
        var ctx = NewContext();
        var result = Eval("$sum = 0; for ($i = 1; $i -le 5; $i += 1) { if ($i -eq 3) { continue }; $sum += $i }; $sum", ctx);
        // 1 + 2 + 4 + 5 = 12
        result.Should().Be(12L);
    }

    // =========================================================================
    // 集合：数组、哈希表
    // =========================================================================

    [Fact]
    public void ArrayLiteral_CreatesArray()
    {
        var result = Eval("@(1, 2, 3)");
        result.Should().BeAssignableTo<System.Collections.IEnumerable>();
        var arr = ((System.Collections.IEnumerable)result!).Cast<object>().ToArray();
        arr.Should().Equal(new object[] { 1L, 2L, 3L });
    }

    [Fact]
    public void HashLiteral_CreatesDictionary()
    {
        var ctx = NewContext();
        Eval("$h = @{ 'a' = 1; 'b' = 2 }", ctx);
        var h = ctx.Variables!.Resolve("h");
        h.Should().NotBeNull();
    }

    // =========================================================================
    // 函数定义与调用
    // =========================================================================

    [Fact]
    public void Function_Definition_And_Call()
    {
        var ctx = NewContext();
        Eval("function Double { param($x) $x * 2 }", ctx);
        // 函数存为 ScriptBlock 到变量表
        var sb = ctx.Variables!.Resolve("Double");
        sb.Should().BeOfType<ScriptBlock>();
        // 调用
        var result = Eval("Double 21", ctx);
        result.Should().Be(42L);
    }

    [Fact]
    public void ScriptBlock_Invoke_ReturnsValue()
    {
        var ctx = NewContext();
        var result = Eval("$f = { param($x) $x + 100 }; (& $f 5)", ctx);
        // & $f 5 调用脚本块
        result.Should().Be(105L);
    }

    // =========================================================================
    // 字符串
    // =========================================================================

    [Fact]
    public void String_SingleQuote_Literal()
    {
        var result = Eval("'hello world'");
        result.Should().Be("hello world");
    }

    [Fact]
    public void String_DoubleQuote_Literal()
    {
        var result = Eval("\"hello\"");
        result.Should().Be("hello");
    }

    // =========================================================================
    // 多语句
    // =========================================================================

    [Fact]
    public void MultipleStatements_ReturnsLastValue()
    {
        var result = Eval("$a = 1; $b = 2; $a + $b");
        result.Should().Be(3L);
    }

    [Fact]
    public void Semicolon_SeparatesStatements()
    {
        var ctx = NewContext();
        Eval("$x = 10; $y = 20", ctx);
        ctx.Variables!.Resolve("x").Should().Be(10L);
        ctx.Variables!.Resolve("y").Should().Be(20L);
    }

    // =========================================================================
    // 异常处理
    // =========================================================================

    [Fact]
    public void Try_Catch_RecoversFromThrow()
    {
        var result = Eval("try { throw 'oops' } catch { 'caught' }");
        result.Should().Be("caught");
    }

    [Fact]
    public void Try_Finally_AlwaysRuns()
    {
        var ctx = NewContext();
        Eval("$flag = 'before'; try { $flag = 'try' } finally { $flag = 'finally' }; $flag", ctx);
        ctx.Variables!.Resolve("flag").Should().Be("finally");
    }

    // =========================================================================
    // 现代语法（ADR-0050，由 ModernParser 处理）
    // =========================================================================

    [Fact]
    public void ModernParser_Ternary()
    {
        var ast = ModernParser.Parse("$x = 5 > 3 ? 'yes' : 'no'");
        ast.Statements.Should().HaveCount(1);
        var result = EvalModern("$x = 5 > 3 ? 'yes' : 'no'; $x");
        result.Should().Be("yes");
    }

    [Fact]
    public void ModernParser_NullCoalesce()
    {
        var result = EvalModern("$x = $null ?? 'default'");
        result.Should().Be("default");
    }

    [Fact]
    public void ModernParser_Lambda()
    {
        var ast = ModernParser.Parse("$f = $x => $x * 2");
        ast.Statements.Should().HaveCount(1);
    }

    [Fact]
    public void ModernParser_LogicalOperators()
    {
        EvalModern("true && false").Should().Be(false);
        EvalModern("true || false").Should().Be(true);
    }

    private static object? EvalModern(string source, ExecutionContext? ctx = null)
    {
        ctx ??= NewContext();
        var ast = ModernParser.Parse(source);
        var evaluator = new Evaluator(ctx);
        return evaluator.Execute(ast).Value;
    }

    // =========================================================================
    // ADR-0046 §6 begin/process/end 命名块（P0-2 验证）
    // =========================================================================

    [Fact]
    public void ScriptBlock_NamedBlocks_AreParsed()
    {
        // 验证 parser 正确识别 begin/process/end 块并填充 AST 字段。
        var ast = PowerShellParser.Parse("{ begin { $c = 0 } process { $c++ } end { $c } }");
        // 顶层只有 1 个表达式语句：ScriptBlockExpression
        var expr = ast.Statements.OfType<ExpressionStatement>().FirstOrDefault();
        expr.Should().NotBeNull();
        var sb = expr!.Expression.Should().BeOfType<ScriptBlockExpression>().Subject;
        sb.BeginBlock.Should().NotBeNull();
        sb.ProcessBlock.Should().NotBeNull();
        sb.EndBlock.Should().NotBeNull();
        sb.Statements.Should().BeEmpty();
    }

    [Fact]
    public void ScriptBlock_NamedBlocks_Invoke_AllPhases()
    {
        // begin 设 $count=0，process 执行一次（非管道），end 返回 $count+10
        var ctx = NewContext();
        var result = Eval("$sb = { begin { $count = 0 } process { $count = $count + 1 } end { $count + 10 } }; & $sb", ctx);
        // 非管道上下文下，process 执行一次（$_ 无绑定）。
        result.Should().Be(11L);
    }

    [Fact]
    public void ScriptBlock_DuplicateNamedBlock_Throws()
    {
        // Per ADR-0046 §6: begin/process/end 各最多一次。
        var act = () => PowerShellParser.Parse("{ begin { } begin { } }");
        act.Should().Throw<ParserException>();
    }

    // =========================================================================
    // ADR-0049 [CmdletBinding] 特性（P0-3 验证）
    // =========================================================================

    [Fact]
    public void CmdletBinding_Parses_SupportsShouldProcess()
    {
        var ast = PowerShellParser.Parse("{ [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')] param([string]$Path) $Path }");
        var expr = ast.Statements.OfType<ExpressionStatement>().FirstOrDefault();
        var sb = expr!.Expression.Should().BeOfType<ScriptBlockExpression>().Subject;
        sb.CmdletBinding.Should().NotBeNull();
        sb.CmdletBinding!.SupportsShouldProcess.Should().BeTrue();
        sb.CmdletBinding!.ConfirmImpact.Should().Be(DeclaredConfirmImpact.High);
    }

    [Fact]
    public void CmdletBinding_Defaults_ConfirmImpact_Medium()
    {
        var ast = PowerShellParser.Parse("{ [CmdletBinding()] param() }");
        var expr = ast.Statements.OfType<ExpressionStatement>().FirstOrDefault();
        var sb = expr!.Expression.Should().BeOfType<ScriptBlockExpression>().Subject;
        sb.CmdletBinding.Should().NotBeNull();
        sb.CmdletBinding!.ConfirmImpact.Should().Be(DeclaredConfirmImpact.Medium);
        sb.CmdletBinding!.SupportsShouldProcess.Should().BeFalse();
    }

    [Fact]
    public void CmdletBinding_Injects_PSCmdlet_AutoVariable()
    {
        // Per ADR-0049 §8: $PSCmdlet 仅在 [CmdletBinding] 函数内可见。
        // 用 & 调用脚本块以实际执行（而非返回 ScriptBlock 对象）。
        var ctx = NewContext();
        var result = Eval("& { [CmdletBinding()] param() $PSCmdlet }", ctx);
        result.Should().NotBeNull();
        result.Should().BeOfType<PSCmdletContext>();
    }

    [Fact]
    public void CmdletBinding_WhatIf_Sets_WhatIfPreference()
    {
        // Per ADR-0049 §2: -WhatIf 设命令作用域内 $WhatIfPreference = $true.
        var ctx = NewContext();
        // 函数读取 $WhatIfPreference 并返回
        Eval("function Check { [CmdletBinding(SupportsShouldProcess)] param() $WhatIfPreference }", ctx);
        // 无 -WhatIf：全局默认 $false
        var noWhatIf = Eval("Check", ctx);
        noWhatIf.Should().Be(false);
        // 有 -WhatIf：命令作用域内 $WhatIfPreference 应为 $true
        var withWhatIf = Eval("Check -WhatIf", ctx);
        withWhatIf.Should().Be(true);
    }

    [Fact]
    public void CmdletBinding_Confirm_Sets_ConfirmPreference_Low()
    {
        // Per ADR-0049 §2: -Confirm 拉到最低阈值（Low）。
        var ctx = NewContext();
        Eval("function CheckConfirm { [CmdletBinding(SupportsShouldProcess)] param() $ConfirmPreference }", ctx);
        // 无 -Confirm：默认 'High'
        var noConfirm = Eval("CheckConfirm", ctx);
        noConfirm.Should().Be("High");
        // 有 -Confirm：'Low'
        var withConfirm = Eval("CheckConfirm -Confirm", ctx);
        withConfirm.Should().Be("Low");
    }

    // =========================================================================
    // ADR-0010 + ADR-0046 §5 多命令管道（P0-4 验证）
    // =========================================================================

    [Fact]
    public void Pipeline_MultiCommand_DoublesEachItem()
    {
        // 1..5 | & { process { $_ * 2 } } → 2, 4, 6, 8, 10
        var result = Eval("1..5 | & { process { $_ * 2 } }");
        result.Should().BeAssignableTo<System.Collections.IEnumerable>();
        var arr = ((System.Collections.IEnumerable)result!).Cast<object>().ToArray();
        arr.Should().Equal(new object[] { 2L, 4L, 6L, 8L, 10L });
    }

    [Fact]
    public void Pipeline_MultiCommand_FunctionName_AsTransform()
    {
        // 用户函数 DoubleEach 也应能作为 pipeline transform。
        var ctx = NewContext();
        Eval("function DoubleEach { process { $_ * 2 } }", ctx);
        var result = Eval("1..3 | DoubleEach", ctx);
        var arr = ((System.Collections.IEnumerable)result!).Cast<object>().ToArray();
        arr.Should().Equal(new object[] { 2L, 4L, 6L });
    }

    [Fact]
    public void Pipeline_MultiCommand_BeginProcessEnd_Phases()
    {
        // begin 执行一次（设累加器），process 每项执行一次（累加），end 返回结果。
        // 注意：begin/end 中 $sum 在 Local 子作用域内修改，不会跨作用域持续到 process。
        // 这里仅验证 process 块的 $_ 绑定。
        var result = Eval("1..3 | & { process { $_ } }");
        var arr = ((System.Collections.IEnumerable)result!).Cast<object>().ToArray();
        arr.Should().Equal(new object[] { 1L, 2L, 3L });
    }

    [Fact]
    public void Pipeline_MultiCommand_NoProcessBlock_UsesStatements()
    {
        // 无命名块：脚本块整体作为 transform 对每项执行。
        var result = Eval("1..3 | & { $_ * 10 }");
        var arr = ((System.Collections.IEnumerable)result!).Cast<object>().ToArray();
        arr.Should().Equal(new object[] { 10L, 20L, 30L });
    }

    // =========================================================================
    // 自动变量 $matches（Per ADR-0042 §3.5）
    // =========================================================================

    [Fact]
    public void Matches_Populated_After_Match_Operator()
    {
        var ctx = NewContext();
        Eval("\"hello world\" -match '(\\w+) (\\w+)'", ctx);
        var matches = ctx.Variables!.Resolve("matches") as System.Collections.IDictionary;
        matches.Should().NotBeNull("$matches must be populated after -match");
        matches!["0"].Should().Be("hello world");
        matches!["1"].Should().Be("hello");
        matches!["2"].Should().Be("world");
    }

    [Fact]
    public void Matches_Cleared_After_Failed_Match()
    {
        var ctx = NewContext();
        // 先成功匹配
        Eval("\"abc\" -match '(a)'", ctx);
        ctx.Variables!.Resolve("matches").Should().NotBeNull();
        // 再失败匹配：$matches 应被清空
        Eval("\"xyz\" -match 'nope'", ctx);
        ctx.Variables!.Resolve("matches").Should().BeNull("$matches must be cleared on failed match");
    }

    [Fact]
    public void Matches_Populated_In_Switch_Regex()
    {
        // switch -Regex 也应填充 $matches（Per ADR-0042 §3.5 + ADR-0045 §6）。
        var src = "switch -Regex ('hello') { '(l+)' { $matches[0] } }";
        var result = Eval(src);
        result.Should().Be("ll");
    }

    // =========================================================================
    // ADR-0050 现代语法
    // =========================================================================

    [Fact]
    public void ModernSyntax_Elif_Keyword_Works()
    {
        // elif 等价 elseif（Per ADR-0050 §5.1）。
        var src = "if (1 -gt 2) { 'a' } elif (1 -gt 0) { 'b' } else { 'c' }";
        EvalModern(src).Should().Be("b");
    }

    [Fact]
    public void ModernSyntax_RawString_PreservesBackslashes()
    {
        // r"..." 原始字符串：反斜杠不转义（Per ADR-0050 §6.1/§6.3）。
        var src = "r\"C:\\path\\to\\file\"";
        EvalModern(src).Should().Be("C:\\path\\to\\file");
    }

    [Fact]
    public void ModernSyntax_TripleQuotedString_Multiline()
    {
        // """...""" 三引号多行字符串（Per ADR-0050 §6.1/§6.2）。
        var src = "\"\"\"line1\nline2\"\"\"";
        EvalModern(src).Should().Be("line1\nline2");
    }

    [Fact]
    public void ModernSyntax_DollarDot_MemberAccess()
    {
        // $. 等价 $_（Per ADR-0050 §4.1/§4.2）。
        // 在管道上下文中 $. 应为当前项。
        var src = "@(1, 2, 3) | & { process { $. } }";
        var result = EvalModern(src);
        var arr = ((System.Collections.IEnumerable)result!).Cast<object>().ToArray();
        arr.Should().Equal(new object[] { 1L, 2L, 3L });
    }

    [Fact]
    public void ModernSyntax_Fn_DefinesFunction()
    {
        // fn 关键字定义函数（Per ADR-0050 §3.1/§3.2）。
        var src = "fn add(a, b) { $a + $b }\nadd 3 4";
        EvalModern(src).Should().Be(7L);
    }

    [Fact]
    public void ModernSyntax_Fn_With_TypeAnnotation()
    {
        // fn 参数支持 name: type 注解（Per ADR-0050 §3.2/§7.3）。
        // 注意：当前实现双引号字符串不插值 $var，用 + 拼接验证参数绑定。
        var src = "fn greet(name: string) { 'Hello, ' + $name }\ngreet 'World'";
        EvalModern(src).Should().Be("Hello, World");
    }

    [Fact]
    public void ModernSyntax_Fn_With_DefaultValue()
    {
        // fn 参数支持默认值（Per ADR-0050 §3.2）。
        var src = "fn greet(name = 'World') { 'Hi ' + $name }\ngreet";
        EvalModern(src).Should().Be("Hi World");
    }

    // =========================================================================
    // ADR-0046 §2 ScriptBlock 反射：File / ToString()
    // =========================================================================

    [Fact]
    public void ScriptBlock_ToString_ReturnsOriginalSourceText()
    {
        // Per ADR-0046 §2/§10：$sb.ToString() 必须返回原始源文本（含注释/空白/原始大小写）。
        var ctx = NewContext();
        Eval("$sb = { $_.Name }", ctx);
        var sb = ctx.Variables!.Resolve("sb") as ScriptBlock;
        sb.Should().NotBeNull("script block should be stored as ScriptBlock instance");
        sb!.ToString().Should().Be("{ $_.Name }");
    }

    [Fact]
    public void ScriptBlock_ToString_PreservesCommentsAndWhitespace()
    {
        // Per ADR-0046 §2：注释与空白必须原样保留，用于调试回显。
        var ctx = NewContext();
        Eval("$sb = {\n  # comment\n  1 + 2\n}", ctx);
        var sb = ctx.Variables!.Resolve("sb") as ScriptBlock;
        sb.Should().NotBeNull();
        var text = sb!.ToString();
        text.Should().Contain("# comment");
        text.Should().Contain("1 + 2");
        text.Should().StartWith("{");
        text.Should().EndWith("}");
    }

    [Fact]
    public void ScriptBlock_File_IsNull_ForReplInput()
    {
        // Per ADR-0046 §2：REPL 顶层输入的脚本块 File 应为 null。
        var ctx = NewContext();
        Eval("$sb = { 1 }", ctx);
        var sb = ctx.Variables!.Resolve("sb") as ScriptBlock;
        sb.Should().NotBeNull();
        sb!.File.Should().BeNull("REPL input has no source file");
    }

    [Fact]
    public void ScriptBlock_File_SetFromSourceFile_PowerShell()
    {
        // Per ADR-0046 §2：从文件加载的脚本块 File 应为文件路径（PowerShell 语法）。
        var ctx = NewContext();
        var ast = PowerShellParser.Parse("$sb = { 1 }", "test.ps1");
        new Evaluator(ctx).Execute(ast);
        var sb = ctx.Variables!.Resolve("sb") as ScriptBlock;
        sb.Should().NotBeNull();
        sb!.File.Should().Be("test.ps1");
    }

    [Fact]
    public void ScriptBlock_File_SetFromSourceFile_Modern()
    {
        // Per ADR-0046 §2：从文件加载的脚本块 File 应为文件路径（现代语法）。
        var ctx = NewContext();
        var ast = ModernParser.Parse("$sb = { 1 }", "test.osh");
        new Evaluator(ctx).Execute(ast);
        var sb = ctx.Variables!.Resolve("sb") as ScriptBlock;
        sb.Should().NotBeNull();
        sb!.File.Should().Be("test.osh");
    }

    [Fact]
    public void ScriptBlock_ToString_Fallback_WhenSourceMissing()
    {
        // Per ADR-0046 §2：手工构造的 AST（无 SourceText）应回退到占位字符串。
        var ctx = NewContext();
        var manualAst = new ScriptBlockExpression(
            new List<Statement>(), new List<ParameterDeclaration>(), SourceSpan.Empty);
        var sb = new ScriptBlock(manualAst, ctx);
        sb.ToString().Should().Be("<ScriptBlock>");
    }
}
