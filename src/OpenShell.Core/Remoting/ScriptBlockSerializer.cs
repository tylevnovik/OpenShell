#nullable enable
// ADR-0059 §4-5: 脚本块序列化器。
// 设计：
//   1. Serialize: 取脚本块源文本 + 扫描 AST 中 $using:name 引用 + 在本地求值捕获 → SerializedScriptBlock。
//   2. Deserialize: 用 ModernParser 重新解析源文本 → ScriptBlock (UsingValues 由调用方注入 Local 作用域)。
//   3. $using: 值仅支持可 JSON 化的基础类型 (string/number/bool/null/array/dict); 闭包/类型实例拒绝序列化。
//   4. AST 扫描用递归 Expression/Statement 访问器，收集 VariableExpression { Scope = Using }。

using System.Collections;
using System.Text.Json;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using OpenShell.Runtime;
using OpenShell.Variables;
using ExecutionContext = OpenShell.Runtime.ExecutionContext;

namespace OpenShell.Remoting;

/// <summary>ADR-0059 §4: 脚本块 ↔ JSON 双向序列化。</summary>
public static class ScriptBlockSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// 序列化脚本块为跨主机载荷。Per ADR-0059 §4.
    /// 扫描 AST 中所有 $using:name 引用，在本地变量表求值后填入 UsingValues。
    /// </summary>
    /// <param name="block">要序列化的脚本块。</param>
    /// <param name="variables">本地变量表 (用于 $using: 值捕获); null 时 $using 字典为空。</param>
    /// <param name="args">位置参数 (做可序列化清洗)。</param>
    public static SerializedScriptBlock Serialize(ScriptBlock block, OpenShell.Variables.IVariableRegistry? variables, object?[] args)
    {
        // 取源文本：优先 Ast.SourceText，回退 ToString()。
        var script = block.Ast.SourceText ?? block.ToString();

        // 扫描 $using: 引用，收集变量名。
        var usingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectUsingVariables(block.Ast, usingNames);

        // 在本地求值每个 $using:name，填入字典。
        var usingValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in usingNames)
        {
            var value = variables?.Resolve(name);
            usingValues[name] = SanitizeForSerialization(value);
        }

        // 位置参数也做可序列化清洗。
        var sanitizedArgs = args.Select(SanitizeForSerialization).ToList();

        return new SerializedScriptBlock(script, usingValues, sanitizedArgs);
    }

    /// <summary>
    /// 把 SerializedScriptBlock 转为 JSON 字符串 (用于 ssh stdin 传输)。Per ADR-0059 §2 协议。
    /// </summary>
    public static string ToJson(SerializedScriptBlock payload)
    {
        var dto = new ScriptBlockPayloadDto
        {
            Script = payload.Script,
            Using = payload.UsingValues,
            Args = payload.Args,
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>从 JSON 字符串反序列化为 SerializedScriptBlock。Per ADR-0059 §2 协议。</summary>
    public static SerializedScriptBlock FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<ScriptBlockPayloadDto>(json, JsonOptions)
            ?? throw new InvalidOperationException("failed to deserialize ScriptBlock payload");
        return new SerializedScriptBlock(
            dto.Script,
            dto.Using ?? new Dictionary<string, object?>(),
            dto.Args ?? new List<object?>());
    }

    /// <summary>
    /// 反序列化载荷为本地 ScriptBlock。Per ADR-0059 §4.
    /// UsingValues 不在此注入 (由调用方注入到 ExecutionContext.Variables 的 Local 作用域)。
    /// </summary>
    public static ScriptBlock Deserialize(SerializedScriptBlock payload, ExecutionContext remoteCtx)
    {
        var ast = ModernParser.Parse(payload.Script, fileName: null);
        // 构造 ScriptBlockExpression (从 ScriptBlockAst 提取 statements/parameters)。
        var sbExpr = new ScriptBlockExpression(
            ast.Statements,
            ast.Parameters,
            ast.Span,
            BeginBlock: ast.BeginBlock,
            ProcessBlock: ast.ProcessBlock,
            EndBlock: ast.EndBlock,
            CmdletBinding: ast.CmdletBinding,
            SourceText: payload.Script);
        return new ScriptBlock(sbExpr, remoteCtx);
    }

    // =========================================================================
    // $using: 变量扫描 (AST Walker)
    // =========================================================================

    /// <summary>递归遍历 ScriptBlockExpression，收集所有 $using:name 变量名。</summary>
    private static void CollectUsingVariables(ScriptBlockExpression sb, HashSet<string> names)
    {
        foreach (var stmt in sb.Statements)
            CollectUsingVariables(stmt, names);
        if (sb.BeginBlock is not null)
            foreach (var s in sb.BeginBlock) CollectUsingVariables(s, names);
        if (sb.ProcessBlock is not null)
            foreach (var s in sb.ProcessBlock) CollectUsingVariables(s, names);
        if (sb.EndBlock is not null)
            foreach (var s in sb.EndBlock) CollectUsingVariables(s, names);
    }

    private static void CollectUsingVariables(Statement stmt, HashSet<string> names)
    {
        switch (stmt)
        {
            case ExpressionStatement es:
                CollectUsingVariables(es.Expression, names);
                break;
            case AssignmentStatement a:
                CollectUsingVariables(a.Value, names);
                break;
            case PipelineStatement ps:
                CollectUsingVariables(ps.Pipeline, names);
                break;
            case IfStatement iff:
                foreach (var branch in iff.Branches)
                {
                    CollectUsingVariables(branch.Condition, names);
                    foreach (var s in branch.Body) CollectUsingVariables(s, names);
                }
                if (iff.ElseBody is not null)
                    foreach (var s in iff.ElseBody) CollectUsingVariables(s, names);
                break;
            case WhileStatement w:
                CollectUsingVariables(w.Condition, names);
                foreach (var s in w.Body) CollectUsingVariables(s, names);
                break;
            case DoWhileStatement dw:
                foreach (var s in dw.Body) CollectUsingVariables(s, names);
                CollectUsingVariables(dw.Condition, names);
                break;
            case ForStatement f:
                foreach (var s in f.Body) CollectUsingVariables(s, names);
                break;
            case ForEachStatement fe:
                foreach (var s in fe.Body) CollectUsingVariables(s, names);
                break;
            case SwitchStatement sw:
                CollectUsingVariables(sw.Test, names);
                foreach (var c in sw.Cases)
                {
                    CollectUsingVariables(c.Pattern, names);
                    foreach (var s in c.Body) CollectUsingVariables(s, names);
                }
                if (sw.Default is not null)
                    foreach (var s in sw.Default) CollectUsingVariables(s, names);
                break;
            case ReturnStatement r when r.Value is not null:
                CollectUsingVariables(r.Value, names);
                break;
            case ThrowStatement t when t.Value is not null:
                CollectUsingVariables(t.Value, names);
                break;
        }
    }

    private static void CollectUsingVariables(Expression expr, HashSet<string> names)
    {
        switch (expr)
        {
            case VariableExpression ve when ve.Scope == VariableScopeKind.Using:
                names.Add(ve.Name);
                break;
            case BinaryExpression b:
                CollectUsingVariables(b.Left, names);
                CollectUsingVariables(b.Right, names);
                break;
            case UnaryExpression u:
                CollectUsingVariables(u.Operand, names);
                break;
            case MemberExpression m:
                CollectUsingVariables(m.Target, names);
                if (m.Arguments is not null)
                    foreach (var a in m.Arguments) CollectUsingVariables(a, names);
                break;
            case IndexExpression ix:
                CollectUsingVariables(ix.Target, names);
                CollectUsingVariables(ix.Index, names);
                break;
            case CastExpression c:
                CollectUsingVariables(c.Operand, names);
                break;
            case SubExpressionExpression sub:
                CollectUsingVariables(sub.Inner, names);
                break;
            case ArrayExpression arr:
                foreach (var e in arr.Elements) CollectUsingVariables(e, names);
                break;
            case HashExpression h:
                foreach (var kv in h.Entries)
                {
                    CollectUsingVariables(kv.Key, names);
                    CollectUsingVariables(kv.Value, names);
                }
                break;
            case RangeExpression r:
                CollectUsingVariables(r.Start, names);
                CollectUsingVariables(r.End, names);
                break;
            case TernaryExpression t:
                CollectUsingVariables(t.Condition, names);
                CollectUsingVariables(t.IfTrue, names);
                CollectUsingVariables(t.IfFalse, names);
                break;
            case PipelineExpression pipe:
                foreach (var cmd in pipe.Commands) CollectUsingVariables(cmd, names);
                break;
            case CommandExpression cmd:
                if (cmd.HeadExpression is not null)
                    CollectUsingVariables(cmd.HeadExpression, names);
                foreach (var arg in cmd.Arguments)
                    if (arg is PositionalArgument pa)
                        CollectUsingVariables(pa.Value, names);
                    else if (arg is NamedArgument na)
                        CollectUsingVariables(na.Value, names);
                break;
            case AssignmentExpression ae:
                CollectUsingVariables(ae.Value, names);
                break;
            case LambdaExpression lam:
                CollectUsingVariables(lam.Body, names);
                break;
            case MatchExpression mexpr:
                CollectUsingVariables(mexpr.Subject, names);
                foreach (var arm in mexpr.Arms)
                {
                    if (arm.Pattern is not null)
                        CollectUsingVariables(arm.Pattern, names);
                    CollectUsingVariables(arm.Body, names);
                }
                break;
            case ScriptBlockExpression sb:
                CollectUsingVariables(sb, names);
                break;
        }
    }

    // =========================================================================
    // 值清洗 (确保可 JSON 序列化)
    // =========================================================================

    /// <summary>
    /// 把运行时值清洗为可 JSON 序列化的基础类型。
    /// 支持: null / 基础标量 / string / 数组 / IDictionary。
    /// 不支持: 闭包 / ScriptBlock / 类型实例 / IItem → 报错。
    /// </summary>
    private static object? SanitizeForSerialization(object? value)
    {
        return value switch
        {
            null => null,
            bool or byte or sbyte or short or ushort or int or uint
                or long or ulong or float or double or decimal
                or string or char or DateTime or DateTimeOffset
                or Guid => value,
            IDictionary dict => SanitizeDictionary(dict),
            IList or Array => SanitizeList(value),
            ScriptBlock => throw new InvalidOperationException(
                "cannot serialize ScriptBlock in $using: value (closures not supported)"),
            _ => value.GetType().IsPrimitive
                ? value
                : throw new InvalidOperationException(
                    $"cannot serialize value of type {value.GetType().Name} in $using: (only primitive/string/array/dict supported)"),
        };
    }

    private static Dictionary<string, object?> SanitizeDictionary(IDictionary dict)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry kv in dict)
        {
            var key = kv.Key?.ToString() ?? "";
            result[key] = SanitizeForSerialization(kv.Value);
        }
        return result;
    }

    private static List<object?> SanitizeList(object value)
    {
        var result = new List<object?>();
        foreach (var item in (IEnumerable)value)
            result.Add(SanitizeForSerialization(item));
        return result;
    }

    // =========================================================================
    // JSON 传输 DTO
    // =========================================================================

    private sealed class ScriptBlockPayloadDto
    {
        public string Script { get; set; } = "";
        public IReadOnlyDictionary<string, object?>? Using { get; set; }
        public IReadOnlyList<object?>? Args { get; set; }
    }
}
