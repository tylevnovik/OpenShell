namespace OpenShell.Filter;

/// <summary>
/// 表达式 AST 根类型。Per ADR-0012 §1.
/// 不可变 record，自研轻量 AST，避免引入脚本运行时（无副作用、无任意代码执行）。
/// </summary>
public abstract record ExprAst;

/// <summary>
/// 比较表达式：<c>left OP right</c>。Per ADR-0012 §1.
/// 左侧必须是 <see cref="PropertyAccessExpr"/>，右侧必须是 <see cref="LiteralExpr"/>。
/// </summary>
public sealed record ComparisonExpr(
    PropertyAccessExpr Left,
    ComparisonOp Op,
    LiteralExpr Right) : ExprAst;

/// <summary>
/// 逻辑组合表达式：<c>left AND/OR right</c>。Per ADR-0012 §1.
/// </summary>
public sealed record LogicalExpr(
    ExprAst Left,
    LogicalOp Op,
    ExprAst Right) : ExprAst;

/// <summary>逻辑取反：<c>NOT inner</c>。Per ADR-0012 §1.</summary>
public sealed record NotExpr(ExprAst Inner) : ExprAst;

/// <summary>
/// 属性访问表达式。Per ADR-0012 §5.
/// <para>
/// <c>Name</c> 取 <c>IItem</c> 的内置字段（size/name/path/kind/modified/created/accessed）
/// 或 <c>Properties</c> 字典中的 key。
/// </para>
/// <para>
/// <c>SubExpression</c> 用于未来的 <c>size / 1MB</c> 投影（M2 暂不实现求值）。
/// </para>
/// </summary>
public sealed record PropertyAccessExpr(
    string Name,
    ExprAst? SubExpression = null) : ExprAst;

/// <summary>
/// 字面量。Per ADR-0012 §4.
/// <para><c>Value</c> 已是 .NET 原生类型（long/double/string/bool/DateTimeOffset/object[]?/null）。</para>
/// </summary>
public sealed record LiteralExpr(object? Value, LiteralKind Kind) : ExprAst;

/// <summary>
/// 投影表达式，含可选别名。Per ADR-0012 §8.
/// 用于 <c>select name, size as bytes</c> 等。
/// </summary>
public sealed record ProjectionExpr(
    ExprAst Expression,
    string? Alias = null) : ExprAst;

/// <summary>比较运算符。Per ADR-0012 §2.</summary>
public enum ComparisonOp
{
    Eq,         // =
    Ne,         // !=
    Lt,         // <
    Gt,         // >
    Le,         // <=
    Ge,         // >=
    Glob,       // ~=  (glob match)
    NotGlob,    // !~= (negated glob)
    In,         // in
    Contains,   // contains
    StartsWith, // startswith
    EndsWith,   // endswith
}

/// <summary>逻辑运算符。Per ADR-0012 §2.</summary>
public enum LogicalOp
{
    And,
    Or,
}

/// <summary>字面量类型。Per ADR-0012 §4.</summary>
public enum LiteralKind
{
    Number,
    String,
    Boolean,
    Date,
    Duration,
    Null,
}
