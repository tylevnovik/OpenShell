#nullable enable
// ADR-0045 §14 + ADR-0046 §10 + ADR-0050 共享 AST 节点树。
// 设计原则：
//   1. 不可变 record，自研轻量 AST（与 OpenShell.Filter.ExprAst 平行，不污染）。
//   2. PowerShellParser 与 ModernParser 共享同一组节点（per ADR-0050 §1.2）。
//   3. 节点携带 SourceSpan，便于错误定位与调试器集成。
//   4. 控制流 Statement 与表达式 Expression 严格分层。

using OpenShell.Parsing; // SourcePosition, SourceSpan

namespace OpenShell.Parsing.Ast;

// SourcePosition 与 SourceSpan 已移到 OpenShell.Parsing 命名空间（SourceSpan.cs）。

/// <summary>AST 根类型。所有节点都携带位置信息。</summary>
public abstract record AstNode(SourceSpan Span);

// ============================================================================
// 顶层：脚本块 / 文件
// ============================================================================

/// <summary>脚本块 AST：参数列表 + 语句列表。per ADR-0046 §2.</summary>
public sealed record ScriptBlockAst(
    IReadOnlyList<Statement> Statements,
    IReadOnlyList<ParameterDeclaration> Parameters,
    SourceSpan Span,
    IReadOnlyList<Statement>? BeginBlock = null,
    IReadOnlyList<Statement>? ProcessBlock = null,
    IReadOnlyList<Statement>? EndBlock = null,
    CmdletBindingAttributeAst? CmdletBinding = null,
    IReadOnlyList<ParseWarning>? ParseWarnings = null,
    IReadOnlyList<TodoMarker>? TodoMarkers = null) : AstNode(Span);

// ============================================================================
// ADR-0050 §9: 注释相关 AST 节点（文档注释 + TODO 标记 + 解析警告）
// ============================================================================

/// <summary>
/// 解析警告。Per ADR-0050 §2.2/§1.1: .osh 模式下 PS 形式运算符 emit DeprecationWarning；
/// 无后缀文件默认现代语法 emit ParseWarning。警告不阻断解析，仅记录。
/// </summary>
public sealed record ParseWarning(WarningKind Kind, string Message, SourceSpan Span) : AstNode(Span);

/// <summary>警告类别。Per ADR-0050 §2.2/§1.1.</summary>
public enum WarningKind
{
    /// <summary>PS 形式运算符在 .osh 模式下使用（-eq -and 等），建议用现代形式（== &amp;&amp;）。Per ADR-0050 §2.2.</summary>
    DeprecatedPsOperator,
    /// <summary>无后缀文件默认现代语法的提示。Per ADR-0050 §1.1.</summary>
    DefaultModernSyntax,
    /// <summary>其他解析期警告。</summary>
    Other,
}

/// <summary>
/// 文档注释语句：三引号字符串位于声明顶部。Per ADR-0050 §9.2.
/// `"""Greet a user."""\nfn greet() { }` 中首行产生 DocumentationCommentStatement。
/// </summary>
public sealed record DocumentationCommentStatement(string Text, SourceSpan Span) : Statement(Span);

/// <summary>
/// TODO/FIXME/HACK 标记，从注释中提取。Per ADR-0050 §9.1.
/// `# TODO: refactor this` → Kind=Todo, Message="refactor this"。
/// </summary>
public sealed record TodoMarker(TodoMarkerKind Kind, string Message, SourceSpan Span) : AstNode(Span);

/// <summary>标记类别。Per ADR-0050 §9.1.</summary>
public enum TodoMarkerKind
{
    Todo,
    Fixme,
    Hack,
    Note,
}

// ============================================================================
// ADR-0050 §7.1: 特性语法 @Attribute(args)
// ============================================================================

/// <summary>
/// 特性 AST 节点：`@ValidateRange(0, 100)` 等价 `[ValidateRange(0, 100)]`。Per ADR-0050 §7.1.
/// </summary>
public sealed record AttributeAst(
    string Name,
    IReadOnlyList<Expression> Arguments,
    SourceSpan Span) : AstNode(Span);

/// <summary>
/// 变量声明语句：`$p: int @ValidateRange(0, 100) = 50`。Per ADR-0050 §7.1/§7.2.
/// 包含变量名、可选类型注解、可选特性列表、可选初始值。
/// </summary>
public sealed record VariableDeclarationStatement(
    string VariableName,
    TypeReference? Type,
    IReadOnlyList<AttributeAst> Attributes,
    Expression? InitialValue,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// 语言块切换语句：`#lang ps1 { ... }` / `#lang osh { ... }`。Per ADR-0050 §1.3.
/// 块内按指定语法模式解析（ps1 → PowerShellParser, osh → ModernParser），
/// 产出的语句列表作为块体。块切换仅影响语法解析，不影响作用域。
/// </summary>
public sealed record LangBlockStatement(
    string Mode,                // "ps1" 或 "osh"
    IReadOnlyList<Statement> Body,
    SourceSpan Span) : Statement(Span);

// ============================================================================
// Statement 节点 — ADR-0045 §14
// ============================================================================

/// <summary>语句基类。</summary>
public abstract record Statement(SourceSpan Span) : AstNode(Span);

/// <summary>管道语句：一行可执行的命令链（含后台运行符 ampersand）。</summary>
public sealed record PipelineStatement(
    PipelineExpression Pipeline,
    bool Background,
    SourceSpan Span) : Statement(Span);

/// <summary>表达式语句：裸表达式作为语句（如 $x / 1 + 2 / foo()）。返回表达式的值。</summary>
public sealed record ExpressionStatement(
    Expression Expression,
    SourceSpan Span) : Statement(Span);

/// <summary>赋值语句：$x = expr / $x += expr / $obj.Prop = expr / $arr[i] = expr.</summary>
public sealed record AssignmentStatement(
    AssignTarget Target,
    AssignmentOperator Operator,
    Expression Value,
    SourceSpan Span) : Statement(Span);

public enum AssignmentOperator
{
    Assign,         // =
    AddAssign,      // +=
    SubtractAssign, // -=
    MultiplyAssign, // *=
    DivideAssign,   // /=
    ModuloAssign,   // %=
    CoalesceAssign, // ??= (modern)
}

/// <summary>赋值目标。</summary>
public abstract record AssignTarget(SourceSpan Span) : AstNode(Span);
public sealed record VariableTarget(string Name, SourceSpan Span) : AssignTarget(Span);
public sealed record MemberTarget(Expression Target, string MemberName, bool Static, SourceSpan Span) : AssignTarget(Span);
public sealed record IndexTarget(Expression Target, Expression Index, SourceSpan Span) : AssignTarget(Span);

/// <summary>if / elseif / else.</summary>
public sealed record IfStatement(
    IReadOnlyList<ConditionalBody> Branches,
    IReadOnlyList<Statement>? ElseBody,
    SourceSpan Span) : Statement(Span);

public sealed record ConditionalBody(Expression Condition, IReadOnlyList<Statement> Body);

/// <summary>switch 语句。per ADR-0045 §7.</summary>
public sealed record SwitchStatement(
    Expression Test,
    IReadOnlyList<SwitchCase> Cases,
    IReadOnlyList<Statement>? Default,
    SwitchFlags Flags,
    SourceSpan Span) : Statement(Span);

public sealed record SwitchCase(Expression Pattern, IReadOnlyList<Statement> Body);

[Flags]
public enum SwitchFlags
{
    None = 0,
    Wildcard = 1,    // -wildcard
    Regex = 2,       // -regex
    CaseSensitive = 4, // -case
    File = 8,        // -file
}

/// <summary>while 循环。Label 由 :label 前缀设置，用于 break/continue label 匹配。Per ADR-0050 §5.1.</summary>
public sealed record WhileStatement(
    Expression Condition,
    IReadOnlyList<Statement> Body,
    SourceSpan Span) : Statement(Span)
{
    /// <summary>循环标签（由 :label 前缀声明）。null 表示无标签。Per ADR-0050 §5.1.</summary>
    public string? Label { get; init; }
}

/// <summary>do-while / do-until。Label 由 :label 前缀设置。Per ADR-0050 §5.1.</summary>
public sealed record DoWhileStatement(
    IReadOnlyList<Statement> Body,
    Expression Condition,
    bool Until, // false=do-while, true=do-until
    SourceSpan Span) : Statement(Span)
{
    /// <summary>循环标签（由 :label 前缀声明）。null 表示无标签。</summary>
    public string? Label { get; init; }
}

/// <summary>for 循环（C 风格）。Label 由 :label 前缀设置。Per ADR-0050 §5.1.</summary>
public sealed record ForStatement(
    Expression? Initializer,
    Expression? Condition,
    Expression? Iterator,
    IReadOnlyList<Statement> Body,
    SourceSpan Span) : Statement(Span)
{
    /// <summary>循环标签（由 :label 前缀声明）。null 表示无标签。</summary>
    public string? Label { get; init; }
}

/// <summary>foreach 循环。Label 由 :label 前缀设置。per ADR-0045 §6 + ADR-0050 §5.1.</summary>
public sealed record ForEachStatement(
    ForEachKind Kind,
    string Variable,
    Expression Iterable,
    IReadOnlyList<Statement> Body,
    SourceSpan Span) : Statement(Span)
{
    /// <summary>循环标签（由 :label 前缀声明）。null 表示无标签。</summary>
    public string? Label { get; init; }

    /// <summary>
    /// 键值对解构迭代时的 (key, value) 变量名。Per ADR-0050 §5.3: for $k, $v in hash。
    /// 仅在 Kind == KeyValuePair 时有意义；null 表示单变量迭代。
    /// </summary>
    public (string Key, string Value)? KeyValueNames { get; init; }
}

public enum ForEachKind { Item, Property, Pipeline, KeyValuePair }

/// <summary>try / catch / finally。per ADR-0045 §8 + ADR-0026.</summary>
public sealed record TryStatement(
    IReadOnlyList<Statement> Body,
    IReadOnlyList<CatchClause> Catches,
    IReadOnlyList<Statement>? Finally,
    SourceSpan Span) : Statement(Span);

public sealed record CatchClause(
    IReadOnlyList<TypeReference>? ExceptionTypes, // null = catch all
    string? Variable,
    IReadOnlyList<Statement> Body);

/// <summary>带标签的语句（用于 break/continue label）。Per ADR-0050 §5.1 + PS label 语义。</summary>
public sealed record LabeledStatement(string Label, Statement Body, SourceSpan Span) : Statement(Span);

/// <summary>break [label]。</summary>
public sealed record BreakStatement(string? Label, SourceSpan Span) : Statement(Span);
/// <summary>continue [label]。</summary>
public sealed record ContinueStatement(string? Label, SourceSpan Span) : Statement(Span);
/// <summary>return [value]。</summary>
public sealed record ReturnStatement(Expression? Value, SourceSpan Span) : Statement(Span);
/// <summary>exit [code]。</summary>
public sealed record ExitStatement(Expression? Code, SourceSpan Span) : Statement(Span);
/// <summary>throw [value]。</summary>
public sealed record ThrowStatement(Expression? Value, SourceSpan Span) : Statement(Span);

/// <summary>函数定义。per ADR-0046 §10.</summary>
public sealed record FunctionDefinitionStatement(
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    ScriptBlockExpression Body,
    FunctionKind Kind,
    SourceSpan Span,
    TypeReference? ReturnType = null) : Statement(Span);

public enum FunctionKind { Function, Filter, Workflow }

/// <summary>
/// [CmdletBinding(...)] 特性 AST。Per ADR-0049 §1.
/// 脚本函数 / 脚本块通过此特性声明 cmdlet 级行为（SupportsShouldProcess 等）。
/// </summary>
public sealed record CmdletBindingAttributeAst(
    bool SupportsShouldProcess,
    DeclaredConfirmImpact ConfirmImpact,
    bool SupportsPaging,
    bool SupportsTransactions,
    string? DefaultParameterSetName,
    bool PositionalBinding,
    string? HelpUri,
    SourceSpan Span) : AstNode(Span);

/// <summary>
/// 静态 destructive-impact 分级（AST 层独立枚举，避免与 OpenShell.Commands.ConfirmImpact 循环依赖）。
/// Per ADR-0049 §5. Ordered None &lt; Low &lt; Medium &lt; High.
/// </summary>
public enum DeclaredConfirmImpact { None, Low, Medium, High }

/// <summary>param() 块：脚本/函数参数声明。</summary>
public sealed record ParamBlockStatement(
    IReadOnlyList<ParameterDeclaration> Parameters,
    SourceSpan Span) : Statement(Span);

/// <summary>参数声明（[type]$name = default）。</summary>
public sealed record ParameterDeclaration(
    TypeReference? Type,
    string Name,
    Expression? DefaultValue,
    bool Mandatory,
    int Position = -1,
    IReadOnlyList<string>? Aliases = null,
    ParameterSetKind? ParameterSet = null);

public enum ParameterSetKind { Default, Named }

/// <summary>using 语句：using namespace / using module / using assembly.</summary>
public sealed record UsingStatement(
    UsingKind Kind,
    string Target,
    SourceSpan Span) : Statement(Span);

public enum UsingKind { Namespace, Module, Assembly, Command, Type }

// ============================================================================
// Expression 节点
// ============================================================================

/// <summary>表达式基类。</summary>
public abstract record Expression(SourceSpan Span) : AstNode(Span);

/// <summary>管道表达式：a | b | c。可作为语句的 Pipeline 字段。</summary>
public sealed record PipelineExpression(
    IReadOnlyList<CommandExpression> Commands,
    SourceSpan Span) : Expression(Span);

/// <summary>命令表达式：Get-ChildItem -Recurse foo.txt。</summary>
public sealed record CommandExpression(
    string Name,
    IReadOnlyList<CommandArgument> Arguments,
    CommandInvocationKind Kind,
    SourceSpan Span,
    ScriptBlockExpression? Block = null,
    Expression? HeadExpression = null) : Expression(Span);

public enum CommandInvocationKind { Direct, DotSource, CallOperator }

/// <summary>命令参数种类。</summary>
public abstract record CommandArgument(SourceSpan Span) : AstNode(Span);
public sealed record PositionalArgument(Expression Value, SourceSpan Span) : CommandArgument(Span);
public sealed record NamedArgument(string Name, Expression Value, SourceSpan Span) : CommandArgument(Span);
public sealed record SwitchArgument(string Name, SourceSpan Span) : CommandArgument(Span);
public sealed record ScriptBlockArgument(ScriptBlockExpression Block, SourceSpan Span) : CommandArgument(Span);

/// <summary>字面量：数字/字符串/true/false/null/数组/哈希表/范围。</summary>
public sealed record LiteralExpression(
    object? Value,
    LiteralKind Kind,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// 可展开字符串表达式：双引号字符串含 $var / ${name} / $(expr) 插值段。
/// 借鉴 PS ExpandableStringExpressionAst（ast.cs:9825-9974）。
/// 求值时用 string.Format(FormatExpression, NestedExpressions 求值结果)。
/// Per ADR-0050 §6.4 + PS 借鉴任务 T-102~T-106。
/// </summary>
public sealed record ExpandableStringExpression(
    string Value,                    // 原始未展开文本（引号已剥离）
    string FormatExpression,         // {0}{1} 形式；原文中的 {/} 转义为 {{/}}
    IReadOnlyList<Expression> NestedExpressions,
    bool IsHereString,               // 区分普通双引号 vs here-string
    SourceSpan Span) : Expression(Span);

/// <summary>赋值表达式：作为表达式使用的赋值，返回被赋的值。for/while init/iter 用。</summary>
public sealed record AssignmentExpression(
    AssignTarget Target,
    AssignmentOperator Operator,
    Expression Value,
    SourceSpan Span) : Expression(Span);

public enum LiteralKind
{
    Integer, Double, String, SingleString, HereString,
    Boolean, Null, Array, Hash, Range,
    DateTime, ScriptBlockString,
    RawString, // r"..." 原始字符串，不插值（ADR-0050 §6.1/§6.3）
}

/// <summary>变量表达式：$var / $global:x / $env:PATH / $_ / $args.</summary>
public sealed record VariableExpression(
    string Name,
    VariableScopeKind Scope,
    SourceSpan Span) : Expression(Span);

public enum VariableScopeKind { Default, Global, Script, Local, Private, Using, Environment }

/// <summary>成员访问：$obj.Property / [Type]::Method() / $obj.Method(args).</summary>
public sealed record MemberExpression(
    Expression Target,
    string MemberName,
    bool Static,
    IReadOnlyList<Expression>? Arguments, // null=property access, list=method call
    bool NullConditional,  // ?. (modern)
    SourceSpan Span) : Expression(Span);

/// <summary>索引访问：$arr[0] / $hash["key"] / $arr?[0]（null 条件索引）。Per ADR-0050 §4.1。</summary>
public sealed record IndexExpression(
    Expression Target,
    Expression Index,
    SourceSpan Span) : Expression(Span)
{
    /// <summary>是否为 null 条件索引（?[ 形式）。null 目标时返回 null 而不抛错。Per ADR-0050 §4.1。</summary>
    public bool NullConditional { get; init; }
}

/// <summary>二元表达式：算术/逻辑/比较/位运算。</summary>
public sealed record BinaryExpression(
    Expression Left,
    BinaryOperator Operator,
    Expression Right,
    SourceSpan Span) : Expression(Span);

public enum BinaryOperator
{
    // 算术
    Add, Subtract, Multiply, Divide, Modulo, Power,
    // 位运算
    BitwiseAnd, BitwiseOr, BitwiseXor, ShiftLeft, ShiftRight,
    // 短路逻辑
    And, Or,
    // PowerShell 比较运算符（保留 PS 风格，modern 用 == 等）
    Eq, Ne, Lt, Gt, Le, Ge,
    Like, NotLike, Match, NotMatch,
    In, NotIn, Contains, NotContains,
    Is, IsNot, As,
    // Modern 运算符 (ADR-0050)
    NullCoalesce,        // ??
    NullCoalesceAssign,  // ??=（在 AssignmentStatement 中作为 operator 更合适，但保留枚举）
    Equals, NotEquals,   // == / != (modern alias of Eq/Ne)
    ArrayConcat,         // ++ 数组拼接 Per ADR-0050 §2.1
}

/// <summary>一元表达式：-not / ! / ~ / - / + / ++ / --。</summary>
public sealed record UnaryExpression(
    UnaryOperator Operator,
    Expression Operand,
    bool Postfix,  // true = $x++/--; false = ++$x/--$x/-not 等
    SourceSpan Span) : Expression(Span);

public enum UnaryOperator
{
    Not,             // ! (modern) 或 -not (PS)
    BitwiseNot,      // ~
    Negate,          // -
    Plus,            // +
    PostfixIncrement, PostfixDecrement,  // $x++ $x--
    PrefixIncrement, PrefixDecrement,
    Spread,          // ...$arr (modern spread)
}

/// <summary>类型转换：[int]$x / [string[]]$arr。</summary>
public sealed record CastExpression(
    TypeReference Type,
    Expression Operand,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// ADR-0052 §4: 类型引用表达式。作为 `is` / `isnot` 运算符右侧节点，
/// 携带 <see cref="TypeReference"/>（含复合类型字符串如 int? / int|string / List&lt;int&gt;）。
/// 求值时不产生实际值，由 Evaluator.IsType(object?, Expression) 特判处理。
/// </summary>
public sealed record TypeReferenceExpression(
    TypeReference Type,
    SourceSpan Span) : Expression(Span);

/// <summary>脚本块表达式：{ ... }。per ADR-0046 §2.</summary>
/// <remarks>
/// <see cref="SourceText"/> / <see cref="SourceFile"/> 携带脚本块原始源文本与源文件路径，
/// 供运行时 <c>ScriptBlock.ToString()</c> / <c>ScriptBlock.File</c> 反射（per ADR-0046 §2/§10）。
/// REPL 输入时 <see cref="SourceFile"/> 为 null；脚本文件加载时为文件绝对路径。
/// </remarks>
public sealed record ScriptBlockExpression(
    IReadOnlyList<Statement> Statements,
    IReadOnlyList<ParameterDeclaration> Parameters,
    SourceSpan Span,
    IReadOnlyList<Statement>? BeginBlock = null,
    IReadOnlyList<Statement>? ProcessBlock = null,
    IReadOnlyList<Statement>? EndBlock = null,
    CmdletBindingAttributeAst? CmdletBinding = null,
    string? SourceText = null,
    string? SourceFile = null) : Expression(Span);

/// <summary>子表达式：$(...)。Inner 为单一表达式。</summary>
public sealed record SubExpressionExpression(Expression Inner, SourceSpan Span) : Expression(Span);

/// <summary>
/// 语句子表达式：$(...) 内含语句（if/for/foreach 等）。Per T-113。
/// 借鉴 PS $(...) 语义：执行语句块，收集所有管道输出并拼接为字符串。
/// 求值时执行 Statements，返回末语句输出（或所有管道输出的拼接）。
/// </summary>
public sealed record StatementSubExpressionExpression(
    IReadOnlyList<Statement> Statements,
    SourceSpan Span) : Expression(Span);
/// <summary>数组表达式：@(...)。</summary>
public sealed record ArrayExpression(IReadOnlyList<Expression> Elements, SourceSpan Span) : Expression(Span);
/// <summary>哈希表表达式：@{k=v; k2=v2}。</summary>
public sealed record HashExpression(IReadOnlyList<KeyValuePair<Expression, Expression>> Entries, SourceSpan Span) : Expression(Span);
/// <summary>范围表达式：1..10 / 'a'..'z'。</summary>
public sealed record RangeExpression(Expression Start, Expression End, SourceSpan Span) : Expression(Span)
{
    /// <summary>是否为半开范围（排除结束值）。Per ADR-0050 §4.</summary>
    public bool IsHalfOpen { get; init; }
}

// ============================================================================
// Modern Syntax 表达式 (ADR-0050)
// ============================================================================

/// <summary>三元条件表达式：cond ? a : b (modern)。</summary>
public sealed record TernaryExpression(
    Expression Condition,
    Expression IfTrue,
    Expression IfFalse,
    SourceSpan Span) : Expression(Span);

/// <summary>Lambda 表达式：$x => $x * 2 / ($x, $y) => $x + $y (modern)。</summary>
public sealed record LambdaExpression(
    IReadOnlyList<ParameterDeclaration> Parameters,
    Expression Body,
    SourceSpan Span) : Expression(Span);

/// <summary>match 表达式：match $x { "foo" => ...; _ => ... } (modern)。</summary>
public sealed record MatchExpression(
    Expression Subject,
    IReadOnlyList<MatchArm> Arms,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// match 臂：模式 + 体。Per ADR-0050 §5.2 + ADR-0055.
/// <para>
/// 兼容设计：<see cref="Pattern"/> 保留旧式 Expression 模式（null = `_`）；
/// <see cref="AdvancedPattern"/> 为 ADR-0055 引入的模式树（优先于 Pattern 解析与求值）。
/// </para>
/// </summary>
public sealed record MatchArm(
    Expression? Pattern,
    Expression Body,
    PatternAst? AdvancedPattern = null); // AdvancedPattern 优先；为 null 时回退到 Pattern

// ============================================================================
// ADR-0051: async / await 节点
// ============================================================================

/// <summary>
/// 异步函数声明：`async fn name() -> Type { ... }`。Per ADR-0051 §1.
/// 编译到带 IsAsync=true 标记的函数定义，运行时调用返回 Task&lt;object?&gt;。
/// </summary>
public sealed record AsyncFunctionDeclarationAst(
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    ScriptBlockExpression Body,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// await 表达式：`await expr`。Per ADR-0051 §2.
/// 解包 Task / ValueTask / IAsyncEnumerable，在 shell 上下文中同步等待。
/// </summary>
public sealed record AwaitExpressionAst(
    Expression Operand,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// async 块表达式：`async { ... }`。Per ADR-0051 §3.
/// 类似 Rust async block，求值时返回 Task&lt;object?&gt;，体部在 await 时才执行。
/// </summary>
public sealed record AsyncBlockExpression(
    IReadOnlyList<Statement> Statements,
    SourceSpan Span) : Expression(Span);

// ============================================================================
// ADR-0055: 高级模式匹配 PatternAst 层次
// ============================================================================

/// <summary>模式 AST 基类。Per ADR-0055.</summary>
public abstract record PatternAst(SourceSpan Span) : AstNode(Span);

/// <summary>通配模式 `_`：永远匹配。Per ADR-0055 §1.</summary>
public sealed record WildcardPattern(SourceSpan Span) : PatternAst(Span);

/// <summary>字面量模式：42 / "hello" / true 等。包装原表达式求值后与主体比较。Per ADR-0055 §1.</summary>
public sealed record LiteralPattern(Expression Value, SourceSpan Span) : PatternAst(Span);

/// <summary>类型模式：`e: Exception` 或 `[Exception]`。匹配 isinstance。Per ADR-0055 §1.</summary>
public sealed record TypePattern(TypeReference Type, SourceSpan Span) : PatternAst(Span);

/// <summary>解构模式：`{ name, age }` / `[a, b, ...rest]`。Per ADR-0055 §2.</summary>
public sealed record DestructurePattern(
    DestructureKind Kind,
    IReadOnlyList<DestructureField> Fields,
    string? Rest,            // `[a, b, ...rest]` 的 rest 名；null 表示无 rest
    SourceSpan Span) : PatternAst(Span);

public enum DestructureKind { Hash, Array }

/// <summary>解构字段：键名（hash 模式）或位置索引（array 模式由顺序决定）+ 绑定变量名。</summary>
public sealed record DestructureField(string Name, SourceSpan Span);

/// <summary>范围模式：`1..=10`（含）/ `1..10`（不含，OpenShell 1..N 含两端，1..&lt;N 不含）。Per ADR-0055 §3.</summary>
public sealed record RangePattern(
    Expression Start,
    Expression End,
    bool Inclusive,
    SourceSpan Span) : PatternAst(Span);

/// <summary>守卫模式：`x if x > 0`。先匹配内部模式，再求值守卫表达式。Per ADR-0055 §4.</summary>
public sealed record GuardPattern(
    PatternAst Inner,
    Expression Condition,
    SourceSpan Span) : PatternAst(Span);

/// <summary>OR 模式：`"red" | "blue"`。任一分支匹配即成功。Per ADR-0055 §5.</summary>
public sealed record OrPattern(
    IReadOnlyList<PatternAst> Alternatives,
    SourceSpan Span) : PatternAst(Span);

/// <summary>绑定模式：`e: Exception as ex`。匹配内部模式后绑定主体到命名变量。Per ADR-0055 §6.</summary>
public sealed record AsPattern(
    PatternAst Inner,
    string BindName,
    SourceSpan Span) : PatternAst(Span);

// ============================================================================
// ADR-0056: ESM 模块系统节点
// ============================================================================

/// <summary>导出种类。Per ADR-0056 §1.</summary>
public enum ExportKind { Function, Constant, Default }

/// <summary>
/// export 声明：`export fn name() { }` / `export const NAME = value` / `export default expr`。Per ADR-0056 §1.
/// 求值时把内部声明的实体登记到当前模块的导出表（ModuleRegistry）。
/// </summary>
public sealed record ExportDeclarationAst(
    ExportKind Kind,
    string? Name,           // Function/Constant 的导出名；Default 为 null
    Statement Inner,        // 实际的 FunctionDefinitionStatement / AssignmentStatement / ExpressionStatement
    SourceSpan Span) : Statement(Span);

/// <summary>命名导入：`import { fn1, fn2 } from "module"`。Per ADR-0056 §2.</summary>
public sealed record NamedImportAst(
    IReadOnlyList<string> Names,
    string ModulePath,
    SourceSpan Span) : Statement(Span);

/// <summary>命名空间导入：`import * as Mod from "module"`。Per ADR-0056 §2.</summary>
public sealed record NamespaceImportAst(
    string Namespace,
    string ModulePath,
    SourceSpan Span) : Statement(Span);

// ============================================================================
// Type Reference
// ============================================================================

/// <summary>类型引用：System.IO.FileInfo / int / string[] / [int[]] / Dictionary[string,int]。</summary>
public sealed record TypeReference(
    string FullName,
    bool IsArray,
    int ArrayRank,
    IReadOnlyList<TypeReference>? GenericArgs,
    SourceSpan Span);

/// <summary>类型引用工厂方法（便于 parser 构造）。</summary>
public static class TypeReferences
{
    public static TypeReference Simple(string name, SourceSpan span) =>
        new(name, false, 0, null, span);

    public static TypeReference Array(TypeReference element, int rank, SourceSpan span) =>
        new(element.FullName, true, rank, null, span);
}

// ============================================================================
// ADR-0052: TypeAnnotation 层级（Union / Generic / Optional）
// ============================================================================

/// <summary>
/// 类型注解抽象基类。Per ADR-0052 §1.
/// 独立于 <see cref="TypeReference"/>：后者保留向后兼容，<see cref="TypeAnnotation"/>
/// 表达复合类型（联合 / 可选 / 泛型），由 TypeCoercer.ParseTypeAnnotation 从字符串解析。
/// </summary>
public abstract record TypeAnnotation(SourceSpan Span) : AstNode(Span);

/// <summary>primitive 类型注解：int / string / bool / ...</summary>
public sealed record PrimitiveTypeAnnotation(string Name, SourceSpan Span) : TypeAnnotation(Span);

/// <summary>联合类型注解：int | string。值可为任一 Option。</summary>
public sealed record UnionTypeAnnotation(IReadOnlyList<TypeAnnotation> Options, SourceSpan Span) : TypeAnnotation(Span);

/// <summary>可选类型注解：int?。接受 null。</summary>
public sealed record OptionalTypeAnnotation(TypeAnnotation Inner, SourceSpan Span) : TypeAnnotation(Span);

/// <summary>泛型类型注解：List&lt;int&gt; / Dict&lt;string, int&gt;。</summary>
public sealed record GenericTypeAnnotation(
    string Name,
    IReadOnlyList<TypeAnnotation> Args,
    SourceSpan Span) : TypeAnnotation(Span);

// ============================================================================
// ADR-0053: 宏系统 AST 节点
// ============================================================================

/// <summary>
/// 宏定义语句：`macro_rules! name { (pattern) =&gt; { expansion } ... }`。Per ADR-0053 §1.
/// 每个 arm 的 Pattern / Expansion 以原始 token 列表存储，展开时再匹配 / 替换 / 重解析。
/// </summary>
public sealed record MacroDefinitionStatement(
    string Name,
    IReadOnlyList<MacroArm> Arms,
    SourceSpan Span) : Statement(Span);

/// <summary>宏 arm：模式 token 列表 + 展开模板 token 列表。Per ADR-0053 §1.</summary>
public sealed record MacroArm(IReadOnlyList<Token> Pattern, IReadOnlyList<Token> Expansion);

/// <summary>
/// 宏调用表达式：`name!(args)` / `name!{ args }`。Per ADR-0053 §3.
/// 参数以原始 token 列表存储（含分隔逗号），供 MacroExpander 按 pattern 匹配。
/// </summary>
public sealed record MacroInvocationExpression(
    string Name,
    IReadOnlyList<Token> ArgumentTokens,
    SourceSpan Span) : Expression(Span);

// ============================================================================
// ADR-0057: 自定义类型定义 AST
// ============================================================================

/// <summary>
/// 自定义类型定义语句：`type Name { x: int; fn op_Equal(other) { } }`。Per ADR-0057 §5.
/// v1 仅注册元数据；实例化（new / self）见 ADR-0057 §6 Open Questions。
/// </summary>
public sealed record TypeDefinitionStatement(
    string Name,
    IReadOnlyList<TypeMember> Members,
    SourceSpan Span) : Statement(Span);

/// <summary>类型成员基类。</summary>
public abstract record TypeMember(SourceSpan Span) : AstNode(Span);

/// <summary>字段成员：`name: type`。</summary>
public sealed record FieldMember(string Name, TypeReference Type, SourceSpan Span) : TypeMember(Span);

/// <summary>方法成员：`fn name(params) [-> RetType] { body }`。</summary>
public sealed record MethodMember(
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    TypeReference? ReturnType,
    ScriptBlockExpression Body,
    SourceSpan Span) : TypeMember(Span);
