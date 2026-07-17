// ADR-0045 §14 + ADR-0050 §1.2 共享 Token 类型。
// 设计原则：
//   1. PowerShellParser 与 ModernParser 共享同一组 Token（per ADR-0050 §1.2）。
//   2. Token 不可变 record struct，零分配开销。
//   3. 携带 SourceSpan，便于错误定位。

using OpenShell.Parsing.Ast;

namespace OpenShell.Parsing;

/// <summary>Token 类型。Per ADR-0045 §14 + ADR-0050 §1.2.</summary>
public enum TokenKind
{
    // 终结符
    End,
    NewLine,
    Semicolon,

    // 字面量
    Integer,
    Double,
    Real,            // decimal
    String,          // 双引号字符串（内容需由 evaluator 插值）
    SingleString,    // 单引号字符串（原样，不插值）
    HereString,       // @"..."@ 双引号 here-string
    HereSingleString,// @'...'@ 单引号 here-string
    RawString,       // r"..." 原始字符串（ADR-0050 §6.1/§6.3，不插值）
    Boolean,
    Null,
    DateTime,

    // 标识符
    Identifier,       // 普通标识符
    Keyword,          // 关键字（if/while/for/...）
    Variable,         // $var / ${var} / $_
    ScopedVariable,   // $global:x / $script:x / $local:x / $private:x / $using:x
    EnvVariable,      // $env:NAME
    TypeRef,          // [System.IO.File]

    // 命令调用相关
    CommandName,      // 行首或管道段首的命令名（Get-ChildItem / gci / ls）
    NamedParameter,   // -Name
    SwitchParameter,  // -Recurse
    At,               // @（用于 @{} @() @"..."@）
    Ampersand,        // & 调用运算符
    Background,       // & 末尾后台运行
    DotSource,        // . 行首 dot-source

    // 标点
    Pipe,             // |
    Comma,            // ,
    Dot,              // .
    DotDot,           // ..
    Colon,            // :
    DoubleColon,      // ::
    LBrace, RBrace,   // { }
    LParen, RParen,   // ( )
    LBracket, RBracket, // [ ]

    // 赋值
    Assign,           // =
    PlusAssign,       // +=
    MinusAssign,      // -=
    StarAssign,       // *=
    SlashAssign,      // /=
    PercentAssign,    // %=
    CoalesceAssign,   // ??= (modern)

    // 算术
    Plus, Minus, Star, Slash, Percent, Caret,

    // 比较（PowerShell 风格）
    CmpEq, CmpNe, CmpLt, CmpGt, CmpLe, CmpGe,
    CmpLike, CmpNotLike, CmpMatch, CmpNotMatch,
    CmpIn, CmpNotIn, CmpContains, CmpNotContains,
    CmpIs, CmpIsNot, CmpAs,
    CmpBand, CmpBor, BcmpBxor,  // -band -bor -bxor
    CmpShl, CmpShr,             // -shl -shr

    // 比较（Modern 风格 alias，ADR-0050）
    Equals,           // ==
    NotEquals,        // !=
    Le, Ge, Lt, Gt,   // <= >= < >

    // 逻辑（PowerShell 风格）
    LogicalAnd,        // -and
    LogicalOr,         // -or
    LogicalNot,        // -not
    LogicalXor,        // -xor

    // 逻辑（Modern 风格）
    AmpAmp,           // &&
    PipePipe,         // ||
    Bang,             // !

    // 位运算
    BitAnd, BitOr, BitXor, BitNot, Shl, Shr,

    // 一元/递增
    PlusPlus,         // ++
    MinusMinus,       // --

    // 现代（ADR-0050）
    Question,         // ?
    DoubleQuestion,   // ??
    Arrow,            // =>
    RightArrow,       // ->  fn 返回类型注解（ADR-0050 §3.2）
    NullCondMember,   // ?.
    NullCondIndex,    // ?[
    Spread,           // ...
    TildeEquals,      // ~=  通配符匹配（等价 -like）Per ADR-0050 §2.1
    TildeRegex,       // ~regex 正则匹配（等价 -match）Per ADR-0050 §2.1

    // 范围
    Range,            // ..
    HalfOpenRange,    // ..<  半开范围 Per ADR-0050 §4

    // 注释
    LineComment,
    BlockComment,
    LangDirective,    // #lang ps1/osh { ... } 块切换指令 (per ADR-0050 §1.3)

    // 标签
    Label,            // :label
}

/// <summary>不可变 Token。Span 是源代码中的位置区间。</summary>
public readonly record struct Token(
    TokenKind Kind,
    string Text,
    object? Value,
    SourceSpan Span);

/// <summary>Token 流辅助扩展。</summary>
public static class TokenExtensions
{
    public static bool IsEndOfStatement(this Token token) =>
        token.Kind is TokenKind.End or TokenKind.NewLine or TokenKind.Semicolon;

    public static bool IsUnaryOperator(this TokenKind kind) =>
        kind is TokenKind.Plus or TokenKind.Minus or TokenKind.LogicalNot
            or TokenKind.Bang or TokenKind.BitNot or TokenKind.PlusPlus or TokenKind.MinusMinus;

    public static bool IsAssignmentOperator(this TokenKind kind) =>
        kind is TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign
            or TokenKind.StarAssign or TokenKind.SlashAssign or TokenKind.PercentAssign
            or TokenKind.CoalesceAssign;

    public static AssignmentOperator ToAssignmentOperator(this TokenKind kind) => kind switch
    {
        TokenKind.Assign => AssignmentOperator.Assign,
        TokenKind.PlusAssign => AssignmentOperator.AddAssign,
        TokenKind.MinusAssign => AssignmentOperator.SubtractAssign,
        TokenKind.StarAssign => AssignmentOperator.MultiplyAssign,
        TokenKind.SlashAssign => AssignmentOperator.DivideAssign,
        TokenKind.PercentAssign => AssignmentOperator.ModuloAssign,
        TokenKind.CoalesceAssign => AssignmentOperator.CoalesceAssign,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not an assignment operator"),
    };
}
