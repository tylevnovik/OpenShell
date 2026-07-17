// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 借鉴自 PowerShell 参考源码（src/System.Management.Automation/engine/parser/CharTraits.cs）。
// 本文件为 PS 借鉴任务 T-100 的一部分（见 docs/ps-ref-reuse-tasks.md）。

using System.Diagnostics;

namespace OpenShell.Parsing;

/// <summary>
/// 特殊字符常量。借鉴自 PowerShell CharTraits.cs SpecialChars。
/// 含 uncommon whitespace、special dashes、special quotes（智能引号）。
/// </summary>
internal static class SpecialChars
{
    // Uncommon whitespace
    internal const char NoBreakSpace = (char)0x00a0;
    internal const char NextLine = (char)0x0085;

    // Special dashes
    internal const char EnDash = (char)0x2013;
    internal const char EmDash = (char)0x2014;
    internal const char HorizontalBar = (char)0x2015;

    // Special quotes
    internal const char QuoteSingleLeft = (char)0x2018; // left single quotation mark
    internal const char QuoteSingleRight = (char)0x2019; // right single quotation mark
    internal const char QuoteSingleBase = (char)0x201a; // single low-9 quotation mark
    internal const char QuoteReversed = (char)0x201b; // single high-reversed-9 quotation mark
    internal const char QuoteDoubleLeft = (char)0x201c; // left double quotation mark
    internal const char QuoteDoubleRight = (char)0x201d; // right double quotation mark
    internal const char QuoteLowDoubleLeft = (char)0x201E; // low double left quote used in german.
}

/// <summary>
/// 字符分类 flags。借鉴自 PowerShell CharTraits.cs CharTraits 枚举。
/// 用于 128 项查表（ASCII 字符特性），含标识符/数字/空白/换行/十六进制/变量名等分类。
/// </summary>
[Flags]
internal enum CharTraits
{
    None = 0x0000,
    IdentifierStart = 0x0002,
    MultiplierStart = 0x0004,
    TypeSuffix = 0x0008,
    Whitespace = 0x0010,
    Newline = 0x0020,
    HexDigit = 0x0040,
    Digit = 0x0080,
    VarNameFirst = 0x0100,
    ForceStartNewToken = 0x0200,
    ForceStartNewAssemblyNameSpecToken = 0x0400,
    ForceStartNewTokenAfterNumber = 0x0800,
}

/// <summary>
/// 字符分类扩展方法。借鉴自 PowerShell CharTraits.cs CharExtensions。
/// 提供 IsWhitespace/IsDash/IsSingleQuote/IsDoubleQuote/IsVariableStart/IsIdentifierStart/
/// IsIdentifierFollow/IsHexDigit/IsDecimalDigit/IsBinaryDigit/IsTypeSuffix/IsMultiplierStart/
/// ForceStartNewToken/ForceStartNewTokenAfterNumber/ForceStartNewTokenInAssemblyNameSpec。
/// </summary>
internal static class CharExtensions
{
    static CharExtensions()
    {
        Debug.Assert(s_traits.Length == 128, "Extension methods rely on this table size.");
    }

    private static readonly CharTraits[] s_traits = new CharTraits[]
    {
/*      0x0 */ CharTraits.ForceStartNewToken | CharTraits.ForceStartNewAssemblyNameSpecToken,
/*      0x1 */ CharTraits.None,
/*      0x2 */ CharTraits.None,
/*      0x3 */ CharTraits.None,
/*      0x4 */ CharTraits.None,
/*      0x5 */ CharTraits.None,
/*      0x6 */ CharTraits.None,
/*      0x7 */ CharTraits.None,
/*      0x8 */ CharTraits.None,
/*      0x9 */ CharTraits.Whitespace | CharTraits.ForceStartNewToken | CharTraits.ForceStartNewAssemblyNameSpecToken,
/*      0xA */ CharTraits.Newline | CharTraits.ForceStartNewToken | CharTraits.ForceStartNewAssemblyNameSpecToken,
/*      0xB */ CharTraits.Whitespace | CharTraits.ForceStartNewToken | CharTraits.ForceStartNewAssemblyNameSpecToken,
/*      0xC */ CharTraits.Whitespace | CharTraits.ForceStartNewToken | CharTraits.ForceStartNewAssemblyNameSpecToken,
/*      0xD */ CharTraits.Newline | CharTraits.ForceStartNewToken | CharTraits.ForceStartNewAssemblyNameSpecToken,
/*      0xE */ CharTraits.None,
/*      0xF */ CharTraits.None,
/*     0x10 */ CharTraits.None,
/*     0x11 */ CharTraits.None,
/*     0x12 */ CharTraits.None,
/*     0x13 */ CharTraits.None,
/*     0x14 */ CharTraits.None,
/*     0x15 */ CharTraits.None,
/*     0x16 */ CharTraits.None,
/*     0x17 */ CharTraits.None,
/*     0x18 */ CharTraits.None,
/*     0x19 */ CharTraits.None,
/*     0x1A */ CharTraits.None,
/*     0x1B */ CharTraits.None,
/*     0x1C */ CharTraits.None,
/*     0x1D */ CharTraits.None,
/*     0x1E */ CharTraits.None,
/*     0x1F */ CharTraits.None,
/*          */ CharTraits.Whitespace | CharTraits.ForceStartNewToken | CharTraits.ForceStartNewAssemblyNameSpecToken,
/*        ! */ CharTraits.ForceStartNewTokenAfterNumber,
/*        " */ CharTraits.None,
/*        # */ CharTraits.ForceStartNewTokenAfterNumber,
/*        $ */ CharTraits.VarNameFirst,
/*        % */ CharTraits.ForceStartNewTokenAfterNumber,
/*        & */ CharTraits.ForceStartNewToken,
/*        ' */ CharTraits.None,
/*        ( */ CharTraits.ForceStartNewToken,
/*        ) */ CharTraits.ForceStartNewToken,
/*        * */ CharTraits.ForceStartNewTokenAfterNumber,
/*        + */ CharTraits.ForceStartNewTokenAfterNumber,
/*        , */ CharTraits.ForceStartNewToken | CharTraits.ForceStartNewAssemblyNameSpecToken,
/*        - */ CharTraits.ForceStartNewTokenAfterNumber,
/*        . */ CharTraits.ForceStartNewTokenAfterNumber,
/*        / */ CharTraits.ForceStartNewTokenAfterNumber,
/*        0 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        1 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        2 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        3 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        4 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        5 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        6 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        7 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        8 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        9 */ CharTraits.Digit | CharTraits.HexDigit | CharTraits.VarNameFirst,
/*        : */ CharTraits.VarNameFirst,
/*        ; */ CharTraits.ForceStartNewToken,
/*        < */ CharTraits.ForceStartNewTokenAfterNumber,
/*        = */ CharTraits.ForceStartNewAssemblyNameSpecToken | CharTraits.ForceStartNewTokenAfterNumber,
/*        > */ CharTraits.ForceStartNewTokenAfterNumber,
/*        ? */ CharTraits.VarNameFirst,
/*        @ */ CharTraits.None,
/*        A */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        B */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        C */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        D */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit | CharTraits.TypeSuffix,
/*        E */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        F */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        G */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        H */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        I */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        J */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        K */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        L */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        M */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        N */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        O */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        P */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        Q */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        R */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        S */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        T */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        U */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        V */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        W */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        X */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        Y */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        Z */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        [ */ CharTraits.None,
/*        \ */ CharTraits.None,
/*        ] */ CharTraits.ForceStartNewAssemblyNameSpecToken | CharTraits.ForceStartNewTokenAfterNumber,
/*        ^ */ CharTraits.VarNameFirst,
/*        _ */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        ` */ CharTraits.None,
/*        a */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        b */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        c */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        d */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit | CharTraits.TypeSuffix,
/*        e */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        f */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.HexDigit,
/*        g */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        h */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        i */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        j */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        k */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        l */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        m */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        n */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        o */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        p */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        q */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        r */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        s */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        t */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.MultiplierStart,
/*        u */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        v */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        w */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        x */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        y */ CharTraits.IdentifierStart | CharTraits.VarNameFirst | CharTraits.TypeSuffix,
/*        z */ CharTraits.IdentifierStart | CharTraits.VarNameFirst,
/*        { */ CharTraits.ForceStartNewToken,
/*        | */ CharTraits.ForceStartNewToken,
/*        } */ CharTraits.ForceStartNewToken,
/*        ~ */ CharTraits.None,
/*     0x7F */ CharTraits.None,
    };

    public static bool IsCurlyBracket(char c) => c == '{' || c == '}';

    /// <summary>返回字符是否为空白（换行不算空白）。</summary>
    internal static bool IsWhitespace(this char c)
    {
        if (c < 128) return (s_traits[c] & CharTraits.Whitespace) != 0;
        if (c <= 256) return c == SpecialChars.NoBreakSpace || c == SpecialChars.NextLine;
        return char.IsSeparator(c);
    }

    /// <summary>返回字符是否为普通或特殊 dash。</summary>
    internal static bool IsDash(this char c)
        => c == '-' || c == SpecialChars.EnDash || c == SpecialChars.EmDash || c == SpecialChars.HorizontalBar;

    /// <summary>返回字符是否为普通或特殊单引号。</summary>
    internal static bool IsSingleQuote(this char c)
        => c == '\''
            || c == SpecialChars.QuoteSingleLeft
            || c == SpecialChars.QuoteSingleRight
            || c == SpecialChars.QuoteSingleBase
            || c == SpecialChars.QuoteReversed;

    /// <summary>返回字符是否为普通或特殊双引号。</summary>
    internal static bool IsDoubleQuote(this char c)
        => c == '"'
            || c == SpecialChars.QuoteDoubleLeft
            || c == SpecialChars.QuoteDoubleRight
            || c == SpecialChars.QuoteLowDoubleLeft;

    /// <summary>返回字符是否可作为无括号变量名的首字符。</summary>
    internal static bool IsVariableStart(this char c)
    {
        if (c < 128) return (s_traits[c] & CharTraits.VarNameFirst) != 0;
        return char.IsLetterOrDigit(c);
    }

    /// <summary>返回字符是否可作为标识符或 label 的首字符。</summary>
    internal static bool IsIdentifierStart(this char c)
    {
        if (c < 128) return (s_traits[c] & CharTraits.IdentifierStart) != 0;
        return char.IsLetter(c);
    }

    /// <summary>返回字符是否可作为标识符或 label 的后续字符。</summary>
    internal static bool IsIdentifierFollow(this char c)
    {
        if (c < 128) return (s_traits[c] & (CharTraits.IdentifierStart | CharTraits.Digit)) != 0;
        return char.IsLetterOrDigit(c);
    }

    /// <summary>返回字符是否为十六进制数字。</summary>
    internal static bool IsHexDigit(this char c)
    {
        if (c < 128) return (s_traits[c] & CharTraits.HexDigit) != 0;
        return false;
    }

    /// <summary>返回字符是否为十进制数字。</summary>
    internal static bool IsDecimalDigit(this char c) => (uint)(c - '0') <= 9;

    /// <summary>返回字符是否为二进制数字。</summary>
    internal static bool IsBinaryDigit(this char c) => (uint)(c - '0') <= 1;

    /// <summary>返回字符是否为数字字面量的类型后缀（d/l/n/s/u/y）。</summary>
    internal static bool IsTypeSuffix(this char c)
    {
        if (c < 128) return (s_traits[c] & CharTraits.TypeSuffix) != 0;
        return false;
    }

    /// <summary>返回字符是否为数量单位后缀首字符（g/k/m/p/t）。</summary>
    internal static bool IsMultiplierStart(this char c)
    {
        if (c < 128) return (s_traits[c] & CharTraits.MultiplierStart) != 0;
        return false;
    }

    /// <summary>返回字符是否强制结束当前 token（扫描字母/数字开头 token 时）。</summary>
    internal static bool ForceStartNewToken(this char c)
    {
        if (c < 128) return (s_traits[c] & CharTraits.ForceStartNewToken) != 0;
        return c.IsWhitespace();
    }

    /// <summary>
    /// 返回字符是否强制结束数字 token。允许 '7z' 为单 token，但 '7+' 为两 token。
    /// </summary>
    internal static bool ForceStartNewTokenAfterNumber(this char c, bool forceEndNumberOnTernaryOperatorChars)
    {
        if (c < 128)
        {
            if ((s_traits[c] & CharTraits.ForceStartNewTokenAfterNumber) != 0) return true;
            return forceEndNumberOnTernaryOperatorChars && (c == '?' || c == ':');
        }
        return c.IsDash();
    }

    /// <summary>返回字符是否强制结束当前 token（扫描程序集名时）。</summary>
    internal static bool ForceStartNewTokenInAssemblyNameSpec(this char c)
    {
        if (c < 128) return (s_traits[c] & CharTraits.ForceStartNewAssemblyNameSpecToken) != 0;
        return c.IsWhitespace();
    }
}
