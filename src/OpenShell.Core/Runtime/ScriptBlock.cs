#nullable enable
// ADR-0046 §2-5 运行时 ScriptBlock 类型。
// 设计：
//   1. ScriptBlock 是 OpenShell 的一等值类型（可赋值、可传参、可调用）。
//   2. 捕获定义时作用域（per ADR-0046 §4 闭包语义），调用时创建 Local 子作用域。
//   3. Invoke 同步执行返回单个值；InvokeStream 返回 IAsyncEnumerable<IItem> 用于管道。
//   4. GetSteppablePipeline() 把脚本块包装为 pipeline transform（per ADR-0046 §3）。
//   5. 与 ICommandRegistry 互操作：函数定义时存到变量表，调用时按名字解析。

using System.Collections;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using OpenShell.Pipeline;
using OpenShell.Variables;

namespace OpenShell.Runtime;

/// <summary>
    /// 运行时 ScriptBlock。Per ADR-0046 §2.
    /// <para>
    /// 封装 ScriptBlockExpression AST + 捕获的 ExecutionContext（含作用域）。
    /// Invoke 时创建新的 Local 作用域，绑定 $args 自动变量。
    /// </para>
    /// <para>
    /// 支持命名块（per ADR-0046 §6）：begin（执行一次）、process（每管道项执行）、end（执行一次）。
    /// 若命名块不存在，退化为顺序执行 Statements。
    /// </para>
    /// </summary>
    public sealed class ScriptBlock
    {
        /// <summary>AST 节点。</summary>
        public ScriptBlockExpression Ast { get; }

        /// <summary>捕获的上下文（变量作用域、命令注册表等）。</summary>
        public ExecutionContext CapturedContext { get; }

        /// <summary>是否有命名块（begin/process/end）。Per ADR-0046 §6.</summary>
        public bool HasNamedBlocks =>
            Ast.BeginBlock is not null || Ast.ProcessBlock is not null || Ast.EndBlock is not null;

        /// <summary>源文件路径（脚本块来自 .openshell 文件时非空，REPL 内为 null）。Per ADR-0046 §2/§10.</summary>
        /// <remarks>
        /// 从 <see cref="Ast"/>/<see cref="ScriptBlockExpression.SourceFile"/> 读取。
        /// Parser 在创建脚本块 AST 时填充此字段；REPL 顶层脚本块为 null。
        /// </remarks>
        public string? File => Ast.SourceFile;

        /// <summary>源文本起始位置。Per ADR-0046 §2/§10.</summary>
        public SourcePosition StartPosition => Ast.Span.Start;

        /// <summary>源文本结束位置。Per ADR-0046 §2/§10.</summary>
        public SourcePosition EndPosition => Ast.Span.End;

        /// <summary>
        /// ADR-0051 §1: 异步函数标记。true 时调用返回 Task&lt;object?&gt;，体部延迟到 await 时执行。
        /// 由 AsyncFunctionDeclarationAst 求值时设为 true；普通函数定义保持默认 false。
        /// </summary>
        public bool IsAsync { get; init; }

        /// <summary>
        /// ADR-0050 §3.2/T-080: 函数返回类型注解。非 null 时，Invoke 后校验返回值类型。
        /// 由 FunctionDefinitionStatement 求值时从 ReturnType 字段传入。
        /// </summary>
        public TypeReference? ReturnType { get; init; }

        public ScriptBlock(ScriptBlockExpression ast, ExecutionContext ctx)
        {
            Ast = ast;
            CapturedContext = ctx;
        }

        /// <summary>同步调用脚本块。返回最后一个表达式的值。</summary>
        public object? Invoke(ExecutionContext? callerCtx = null, params object?[] args)
            => InvokeWithNamedArgs(callerCtx, namedArgs: null, args);

        /// <summary>
        /// PowerShell 兼容 API：仅返回最后一个值（不收集流式输出）。
        /// Per ADR-0046 §2/§3.3. 等价于 <see cref="Invoke"/>（OpenShell 单值语义）。
        /// </summary>
        /// <param name="args">位置参数。</param>
        public object? InvokeReturnAsIs(params object?[] args) => Invoke(null, args);

        /// <summary>
        /// 同步调用脚本块（带命名参数）。Per ADR-0049 §2.
        /// 当 <see cref="Ast"/>/<see cref="CmdletBindingAttributeAst"/> 声明 SupportsShouldProcess 时，
        /// 自动从 <paramref name="namedArgs"/> 提取 -WhatIf / -Confirm 并写入 $WhatIfPreference / $ConfirmPreference。
        /// </summary>
        /// <param name="callerCtx">调用方执行上下文。</param>
        /// <param name="namedArgs">命名参数字典（含 -WhatIf / -Confirm 等通用参数）。</param>
        /// <param name="args">位置参数数组。</param>
        public object? InvokeWithNamedArgs(
            ExecutionContext? callerCtx = null,
            IReadOnlyDictionary<string, object?>? namedArgs = null,
            params object?[] args)
        {
            var ctx = callerCtx ?? CapturedContext;
            using var scope = ctx.EnterScope();
            ctx.CurrentArgs = args;

            // [CmdletBinding] 处理：注入 $PSCmdlet / $WhatIfPreference / $ConfirmPreference.
            // Per ADR-0049 §1/§2/§8.
            if (Ast.CmdletBinding is not null)
            {
                InjectCmdletBindingEnvironment(ctx, namedArgs);
            }

            // 命名块模式：begin → (无 process 时执行 Statements) → end.
            // Per ADR-0046 §6.
            if (HasNamedBlocks)
            {
                return EnforceReturnType(InvokeWithNamedBlocks(ctx));
            }

            // 无命名块：顺序执行。
            var evaluator = new Evaluator(ctx);
            var scriptAst = new ScriptBlockAst(
                Ast.Statements, Ast.Parameters, Ast.Span,
                CmdletBinding: Ast.CmdletBinding);
            var result = evaluator.Execute(scriptAst);
            if (result.Signal == FlowSignalKind.Return) return EnforceReturnType(result.Value);
            if (result.Signal == FlowSignalKind.Throw)
                throw new OpenShellScriptException(result.ThrownValue, ctx);
            return EnforceReturnType(result.Value);
        }

        /// <summary>
        /// ADR-0050 §3.2/T-080: 校验返回值类型是否匹配声明的返回类型注解。
        /// 不匹配时抛 OpenShellScriptException。void（null）返回类型注解时允许 null。
        /// </summary>
        private object? EnforceReturnType(object? value)
        {
            if (ReturnType is null) return value;
            var expected = ReturnType.FullName;
            // null 值：仅当返回类型为可空（int? 等）时允许；当前简化为允许 null 通过。
            if (value is null) return value;
            var actualType = value.GetType();
            if (!TypeMatches(expected, actualType, value))
            {
                throw new OpenShellScriptException(
                    $"函数返回类型不匹配：声明为 '{expected}'，实际返回 '{actualType.Name}' 值 '{value}'",
                    CapturedContext);
            }
            return value;
        }

        /// <summary>判断值类型是否匹配声明的类型名。Per ADR-0050 §3.2.</summary>
        private static bool TypeMatches(string expected, Type actualType, object value)
        {
            // 去除可空标记
            var e = expected.TrimEnd('?');
            // 基本类型映射
            return e.ToLowerInvariant() switch
            {
                "int" or "int32" => actualType == typeof(int) || actualType == typeof(long) && (long)value is >= int.MinValue and <= int.MaxValue,
                "long" or "int64" => actualType == typeof(long) || actualType == typeof(int),
                "double" or "float" => actualType == typeof(double) || actualType == typeof(float) || actualType == typeof(int) || actualType == typeof(long),
                "string" => actualType == typeof(string),
                "bool" or "boolean" => actualType == typeof(bool),
                "void" => value is null,
                _ => true, // 未知类型名：宽松放行（自定义类型/未来扩展）
            };
        }

        /// <summary>
        /// 注入 [CmdletBinding] 触发的自动变量环境。Per ADR-0049 §2/§8.
        /// 在脚本块作用域内写入：
        ///   - $WhatIfPreference（合并全局默认与 -WhatIf 命令级覆盖）
        ///   - $ConfirmPreference（合并全局默认与 -Confirm 命令级覆盖）
        ///   - $PSCmdlet（PSCmdletContext 实例，仅 [CmdletBinding] 函数可见）
        /// </summary>
        private void InjectCmdletBindingEnvironment(
            ExecutionContext ctx,
            IReadOnlyDictionary<string, object?>? namedArgs)
        {
            var cb = Ast.CmdletBinding!;

            // 读取 -WhatIf / -Confirm 命令级覆盖（仅 SupportsShouldProcess 时有效）。
            bool whatIfOverride = false;
            bool confirmOverride = false;
            if (cb.SupportsShouldProcess && namedArgs is not null)
            {
                if (TryGetSwitch(namedArgs, "WhatIf", out var w)) whatIfOverride = w;
                if (TryGetSwitch(namedArgs, "Confirm", out var c)) confirmOverride = c;
            }

            // $WhatIfPreference：全局默认 OR 命令级 -WhatIf. Per ADR-0049 §2.
            // 这些偏好变量在 [CmdletBinding] 函数作用域内可覆盖（非只读），用 Set 写入 Local 帧。
            var globalWhatIf = ctx.Variables?.Resolve("WhatIfPreference") is bool gb ? gb : false;
            ctx.Variables?.Set("WhatIfPreference", globalWhatIf || whatIfOverride);

            // $ConfirmPreference：默认 High；-Confirm 拉到 Low（提示所有 impact）。Per ADR-0049 §2/§5.
            string effectiveConfirm;
            if (confirmOverride)
            {
                effectiveConfirm = "Low";  // -Confirm 等价拉到最低阈值
            }
            else
            {
                var globalConfirm = ctx.Variables?.Resolve("ConfirmPreference");
                effectiveConfirm = globalConfirm?.ToString() ?? "High";
            }
            ctx.Variables?.Set("ConfirmPreference", effectiveConfirm);

            // $PSCmdlet：仅 [CmdletBinding] 函数可见。普通函数内为 $null. Per ADR-0049 §8.
            var declaredImpact = MapDeclaredConfirmImpact(cb.ConfirmImpact);
            var psCmdlet = new PSCmdletContext(ctx, commandName: "", verb: "Process", declaredImpact);

            // Per ADR-0049 §1 (原"延迟实现", 现已落实): SupportsPaging 注入 -First/-Skip/-IncludeTotalCount。
            if (cb.SupportsPaging)
            {
                ulong first = ulong.MaxValue;
                ulong skip = 0;
                bool includeTotalCount = false;
                if (namedArgs is not null)
                {
                    if (TryGetSwitchOrValue<ulong>(namedArgs, "First", out var f)) first = f;
                    if (TryGetSwitchOrValue<ulong>(namedArgs, "Skip", out var s)) skip = s;
                    if (TryGetSwitch(namedArgs, "IncludeTotalCount", out var itc)) includeTotalCount = itc;
                }
                psCmdlet.PagingParameters = new PagingParameters
                {
                    First = first,
                    Skip = skip,
                    IncludeTotalCount = includeTotalCount,
                };
                // 同时写入顶层变量, 便于脚本直接 $First / $Skip 访问。
                // First 为 ulong.MaxValue (无限制) 时写入 0 表示不限制, 与 PowerShell 行为对齐。
                ctx.Variables?.Set("First", first == ulong.MaxValue ? 0L : (long)first);
                ctx.Variables?.Set("Skip", (long)skip);
                ctx.Variables?.Set("IncludeTotalCount", includeTotalCount);
            }

            // Per ADR-0049 §1 (原"延迟实现", 现已落实): SupportsTransactions 注入 -UseTransaction。
            // 事务系统本身需要独立 ADR (批6), 这里仅暴露参数入口并写入 $UseTransaction 变量。
            if (cb.SupportsTransactions)
            {
                var useTx = false;
                if (namedArgs is not null && TryGetSwitch(namedArgs, "UseTransaction", out var u)) useTx = u;
                psCmdlet.UseTransaction = useTx;
                ctx.Variables?.Set("UseTransaction", useTx);
            }

            ctx.Variables?.Set("PSCmdlet", psCmdlet);
        }

        /// <summary>
        /// 从命名参数字典中提取值 (支持 bool switch 与实际值)。Per ADR-0049 §11 通用参数大小写不敏感。
        /// </summary>
        private static bool TryGetSwitchOrValue<T>(
            IReadOnlyDictionary<string, object?> dict, string name, out T value)
            where T : struct
        {
            value = default;
            foreach (var kvp in dict)
            {
                if (!string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (kvp.Value is null) return false;
                if (kvp.Value is T t) { value = t; return true; }
                // 尝试 Convert.ChangeType 处理 int/long/ulong 互转。
                try
                {
                    value = (T)Convert.ChangeType(kvp.Value, typeof(T));
                    return true;
                }
                catch { return false; }
            }
            return false;
        }

        private static bool TryGetSwitch(IReadOnlyDictionary<string, object?> dict, string name, out bool value)
        {
            value = false;
            // 大小写不敏感查找。Per ADR-0049 §11.
            foreach (var kvp in dict)
            {
                if (!string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (kvp.Value is bool b) { value = b; return true; }
                if (kvp.Value is null) { value = true; return true; }  // switch present without value = $true
                value = true;
                return true;
            }
            return false;
        }

        private static OpenShell.Commands.ConfirmImpact MapDeclaredConfirmImpact(DeclaredConfirmImpact impact)
            => impact switch
            {
                DeclaredConfirmImpact.None => OpenShell.Commands.ConfirmImpact.None,
                DeclaredConfirmImpact.Low => OpenShell.Commands.ConfirmImpact.Low,
                DeclaredConfirmImpact.Medium => OpenShell.Commands.ConfirmImpact.Medium,
                DeclaredConfirmImpact.High => OpenShell.Commands.ConfirmImpact.High,
                _ => OpenShell.Commands.ConfirmImpact.Medium,
            };

        /// <summary>命名块执行：begin → process(或 Statements) → end。Per ADR-0046 §6.</summary>
        private object? InvokeWithNamedBlocks(ExecutionContext ctx)
        {
            var evaluator = new Evaluator(ctx);
            object? lastValue = null;

            // begin 块：执行一次。
            if (Ast.BeginBlock is not null)
            {
                var beginAst = new ScriptBlockAst(Ast.BeginBlock, Array.Empty<ParameterDeclaration>(), Ast.Span);
                var r = evaluator.Execute(beginAst);
                if (r.Signal == FlowSignalKind.Return) return r.Value;
                if (r.Signal == FlowSignalKind.Throw)
                    throw new OpenShellScriptException(r.ThrownValue, ctx);
                lastValue = r.Value;
            }

            // process 块：若无则退化为 Statements。
            var processStatements = Ast.ProcessBlock ?? Ast.Statements;
            if (processStatements.Count > 0)
            {
                var processAst = new ScriptBlockAst(processStatements, Array.Empty<ParameterDeclaration>(), Ast.Span);
                // 在非管道上下文，process 块执行一次（$_ 无绑定）。
                var r = evaluator.Execute(processAst);
                if (r.Signal == FlowSignalKind.Return) return r.Value;
                if (r.Signal == FlowSignalKind.Throw)
                    throw new OpenShellScriptException(r.ThrownValue, ctx);
                lastValue = r.Value;
            }

            // end 块：执行一次。
            if (Ast.EndBlock is not null)
            {
                var endAst = new ScriptBlockAst(Ast.EndBlock, Array.Empty<ParameterDeclaration>(), Ast.Span);
                var r = evaluator.Execute(endAst);
                if (r.Signal == FlowSignalKind.Return) return r.Value;
                if (r.Signal == FlowSignalKind.Throw)
                    throw new OpenShellScriptException(r.ThrownValue, ctx);
                lastValue = r.Value;
            }

            return lastValue;
        }

        /// <summary>作为 pipeline transform 流式执行。per ADR-0046 §5 + ADR-0048.</summary>
        /// <param name="input">上游 IItem 流。</param>
        /// <param name="ctx">调用方上下文。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>每个 input item 经脚本块处理后的输出流。</returns>
        public async IAsyncEnumerable<IItem> InvokeStream(
            IAsyncEnumerable<IItem> input,
            ExecutionContext? ctx = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct = default)
        {
            var execCtx = ctx ?? CapturedContext;
            var evaluator = new Evaluator(execCtx);

            // 命名块模式：begin → process(每项) → end. Per ADR-0046 §6.
            if (HasNamedBlocks)
            {
                // begin 块。
                if (Ast.BeginBlock is not null)
                {
                    var beginAst = new ScriptBlockAst(Ast.BeginBlock, Array.Empty<ParameterDeclaration>(), Ast.Span);
                    evaluator.Execute(beginAst);
                }

                // process 块：每个上游项执行一次。
                var processStatements = Ast.ProcessBlock ?? Array.Empty<Statement>();
                if (processStatements.Count > 0)
                {
                    var processAst = new ScriptBlockAst(processStatements, Array.Empty<ParameterDeclaration>(), Ast.Span);
                    await foreach (var item in input.WithCancellation(ct))
                    {
                        execCtx.CurrentItem = item;
                        execCtx.CancellationToken = ct;
                        var result = evaluator.Execute(processAst);
                        if (result.Signal == FlowSignalKind.Throw)
                            throw new OpenShellScriptException(result.ThrownValue, execCtx);
                        if (result.Signal == FlowSignalKind.Return) { yield break; }
                        if (result.Value is not null)
                        {
                            yield return ValueConversion.ValueToItem(result.Value);
                        }
                    }
                }

                // end 块。
                if (Ast.EndBlock is not null)
                {
                    var endAst = new ScriptBlockAst(Ast.EndBlock, Array.Empty<ParameterDeclaration>(), Ast.Span);
                    var endResult = evaluator.Execute(endAst);
                    if (endResult.Signal == FlowSignalKind.Throw)
                        throw new OpenShellScriptException(endResult.ThrownValue, execCtx);
                    if (endResult.Value is not null)
                    {
                        yield return ValueConversion.ValueToItem(endResult.Value);
                    }
                }
                yield break;
            }

            // 无命名块：原逻辑。
            await foreach (var item in input.WithCancellation(ct))
            {
                execCtx.CurrentItem = item;
                execCtx.CancellationToken = ct;
                var result = evaluator.Execute(Ast);
                if (result.Signal == FlowSignalKind.Throw)
                    throw new OpenShellScriptException(result.ThrownValue, execCtx);
                if (result.Signal == FlowSignalKind.Return) { yield break; }
                if (result.Value is not null)
                {
                    yield return ValueConversion.ValueToItem(result.Value);
                }
            }
        }

    /// <summary>把脚本块包装为可步进管道。per ADR-0046 §3.</summary>
    public SteppablePipeline GetSteppablePipeline(ExecutionContext? ctx = null)
        => new(this, ctx ?? CapturedContext);

    /// <summary>
    /// 返回脚本块的原始源文本（含注释/空白/原始大小写），用于调试回显。
    /// Per ADR-0046 §2/§10.
    /// 若 AST 未携带源文本（如手工构造的 AST），回退为占位字符串 <c>"&lt;ScriptBlock&gt;"</c>。
    /// </summary>
    public override string ToString() => Ast.SourceText ?? "<ScriptBlock>";

    /// <summary>隐式转换：ScriptBlock 可作为 ICommand 注册。</summary>
    public static implicit operator ScriptBlock?(Delegate d) => null; // 占位
}

/// <summary>
/// 可步进管道：把脚本块包装为单元素进/单元素出的 transform。Per ADR-0046 §3.
/// 用于 Where-Object { } / ForEach-Object { } / Select-Object { } 等场景。
/// </summary>
public sealed class SteppablePipeline
{
    private readonly ScriptBlock _block;
    private readonly ExecutionContext _ctx;

    public SteppablePipeline(ScriptBlock block, ExecutionContext ctx)
    {
        _block = block;
        _ctx = ctx;
    }

    /// <summary>处理单个输入项，返回结果集合。</summary>
    public IEnumerable<object?> Process(object? input)
    {
        _ctx.CurrentItem = ValueConversion.ValueToItem(input);
        var evaluator = new Evaluator(_ctx);
        var result = evaluator.Execute(_block.Ast);
        if (result.Signal == FlowSignalKind.Throw)
            throw new OpenShellScriptException(result.ThrownValue, _ctx);
        if (result.Signal == FlowSignalKind.Return) yield break;
        if (result.Value is IEnumerable e and not string)
        {
            foreach (var item in e) yield return item;
        }
        else if (result.Value is not null)
        {
            yield return result.Value;
        }
    }

    /// <summary>批量处理。</summary>
    public IEnumerable<object?> ProcessAll(IEnumerable input)
    {
        foreach (var item in input)
        {
            foreach (var output in Process(item))
                yield return output;
        }
    }
}

/// <summary>辅助：值到 IItem 转换。简化实现。</summary>
internal static class ValueConversion
{
    public static IItem ValueToItem(object? value)
    {
        if (value is IItem item) return item;
        return new Item
        {
            Path = OpenShell.Paths.ItemPath.Root("fs"),
            Kind = OpenShell.Items.ItemKind.Property,
            Properties = OpenShell.Items.PropertyBag.Empty.With("Value", value),
        };
    }

    public static object? ItemToValue(IItem item)
    {
        var v = item.Properties["Value"];
        return v ?? item.Name;
    }
}
