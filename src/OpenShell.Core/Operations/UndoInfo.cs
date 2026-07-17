using System.Diagnostics.CodeAnalysis;

namespace OpenShell.Operations;

/// <summary>
/// 反向操作描述。Per ADR-0020 §1, §3.
/// 描述如何撤销一条已执行的操作 (Undo Operation + 反向参数)。
/// <c>null</c> 表示不可逆 (如 Remove-Item -Force 物理删除)。
/// </summary>
public sealed record UndoInfo
{
    /// <summary>反向操作名称。例如 "delete" / "move-back" / "restore-from-trash" / "rename"。</summary>
    public required string UndoOperation { get; init; }

    /// <summary>反向操作参数。例如 delete → {path=...}, move-back → {src=..., dst=...}。</summary>
    public required IReadOnlyDictionary<string, string> UndoParameters { get; init; }

    /// <summary>构造 UndoInfo, 接受普通 Dictionary (常见用法)。</summary>
    /// <remarks>
    /// Pre-existing fix: 该构造函数显式设置了两个 required 成员, 但缺少
    /// <c>[SetsRequiredMembers]</c> 特性, 导致编译器仍要求调用方在对象初始化器中再次设置。
    /// 加上特性后, 调用方可直接 <c>new UndoInfo(op, params)</c> 而无需重复赋值。
    /// </remarks>
    [SetsRequiredMembers]
    public UndoInfo(string undoOperation, IReadOnlyDictionary<string, string> undoParameters)
    {
        UndoOperation = undoOperation;
        UndoParameters = undoParameters;
    }

    /// <summary>STJ 反序列化用无参构造 + init 属性。</summary>
    public UndoInfo()
    {
        UndoOperation = string.Empty;
        UndoParameters = new Dictionary<string, string>();
    }
}
