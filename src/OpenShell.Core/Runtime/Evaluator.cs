#nullable enable
// ADR-0045 §3-14 + ADR-0046 §2-5 + ADR-0047 §3-4 AST 求值器。
// 设计：
//   1. Evaluator 接受 AstNode + ExecutionContext，返回 ExecutionResult。
//   2. 控制流（break/continue/return/exit/throw）通过 ExecutionResult.Signal 传播，不抛异常。
//   3. throw 信号跨作用域时转 OpenShellScriptException；catch 捕获后通过 ExecutionResult 恢复。
//   4. 脚本块通过 ScriptBlock 类型包装，Invoke 时调用 Evaluator 递归求值。
//   5. 命令调用通过 ICommandRegistry.Resolve + Activator.CreateInstance 反射构造 Args。
//   6. 类型转换通过 Convert.ChangeType + 自定义规则（per ADR-0047 §3）。

using System.Collections;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Modules;
using OpenShell.Operations;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using OpenShell.Variables;

namespace OpenShell.Runtime;

/// <summary>
/// OpenShell AST 求值器。Per ADR-0045 §3-14 + ADR-0046 §2-5.
/// </summary>
public sealed class Evaluator
{
    private readonly ExecutionContext _ctx;

    public Evaluator(ExecutionContext ctx) => _ctx = ctx;

    // =========================================================================
    // 顶层入口
    // =========================================================================

    /// <summary>执行脚本块 AST，返回最后表达式的值。</summary>
    public ExecutionResult Execute(ScriptBlockAst script)
    {
        if (script.Parameters.Count > 0)
        {
            BindParameters(script.Parameters, _ctx.CurrentArgs ?? Array.Empty<object?>());
        }

        ExecutionResult last = ExecutionResult.Empty;
        foreach (var stmt in script.Statements)
        {
            var r = EvaluateStatement(stmt);
            if (r.Signal == FlowSignalKind.Throw)
                throw new OpenShellScriptException(r.ThrownValue, _ctx);
            if (r.Signal != FlowSignalKind.None)
                return r;
            last = r;
        }
        return last;
    }

    public ExecutionResult Execute(ScriptBlockExpression block)
        => Execute(new ScriptBlockAst(block.Statements, block.Parameters, block.Span));

    // =========================================================================
    // Statement 求值
    // =========================================================================

    public ExecutionResult EvaluateStatement(Statement stmt) => stmt switch
    {
        PipelineStatement ps => EvaluatePipelineStatement(ps),
        ExpressionStatement es => EvaluateExpression(es.Expression),
        AssignmentStatement a => EvaluateAssignment(a),
        IfStatement iff => EvaluateIf(iff),
        WhileStatement w => EvaluateWhile(w),
        DoWhileStatement dw => EvaluateDoWhile(dw),
        ForStatement f => EvaluateFor(f),
        ForEachStatement fe => EvaluateForEach(fe),
        SwitchStatement sw => EvaluateSwitch(sw),
        TryStatement t => EvaluateTry(t),
        BreakStatement b => ExecutionResult.Break(b.Label),
        ContinueStatement c => ExecutionResult.Continue(c.Label),
        // ADR-0050 §5.1: :label 循环标签——求值体部，匹配 break/continue label 则消费信号。
        LabeledStatement ls => EvaluateLabeledStatement(ls),
        ReturnStatement r => ExecutionResult.Return(r.Value is null ? null : EvaluateExpression(r.Value).Value),
        ExitStatement e => ExecutionResult.Exit(EvaluateExitCode(e)),
        ThrowStatement t => EvaluateThrow(t),
        FunctionDefinitionStatement fn => DefineFunction(fn),
        ParamBlockStatement p => EvaluateParamBlock(p),
        UsingStatement u => EvaluateUsing(u),
        // ADR-0051 §1: async fn name() { } —— 注册 IsAsync=true 的 ScriptBlock。
        AsyncFunctionDeclarationAst afn => EvaluateAsyncFunctionDeclaration(afn),
        // ADR-0056 §1: export fn/const/default —— 求值内部声明并登记到模块导出表。
        ExportDeclarationAst exp => EvaluateExportDeclaration(exp),
        // ADR-0056 §2: import { } from "mod" / import * as NS from "mod"。
        NamedImportAst ni => EvaluateNamedImport(ni),
        NamespaceImportAst nsi => EvaluateNamespaceImport(nsi),
        // ADR-0053 §2: macro_rules! name { (pattern) => { expansion } } —— 注册宏定义。
        MacroDefinitionStatement md => EvaluateMacroDefinition(md),
        // ADR-0057 §3: type Name { field; method() { } } —— 注册自定义类型。
        TypeDefinitionStatement td => EvaluateTypeDefinition(td),
        // ADR-0050 §7.1/§7.2: $var: Type @Attr = value 类型化变量声明——求值初始值并绑定变量。
        VariableDeclarationStatement vds => EvaluateVariableDeclaration(vds),
        // ADR-0050 §9.2: 文档注释——运行时无副作用。
        DocumentationCommentStatement => ExecutionResult.Empty,
        // ADR-0050 §1.3: #lang ps1/osh { ... } 块切换——顺序执行块体语句（块切换仅影响语法解析，不影响作用域）。
        LangBlockStatement lbs => EvaluateLangBlock(lbs),
        _ => ExecutionResult.Empty,
    };

    // -------------------------------------------------------------------------
    // Pipeline 语句
    // -------------------------------------------------------------------------

    /// <summary>
    /// ADR-0050 §10.1 + ADR-0045: 处理 using / import 语句。
    /// UsingKind.Module 且 Target 是文件路径时, 读取文件、按后缀选择 parser、在当前作用域执行 (dot-source 语义)。
    /// 其他 Kind (Namespace/Assembly/Command/Type) 当前为空操作 (未来扩展点)。
    /// </summary>
    private ExecutionResult EvaluateUsing(UsingStatement u)
    {
        if (u.Kind != UsingKind.Module || string.IsNullOrWhiteSpace(u.Target))
            return ExecutionResult.Empty;

        var path = ResolveScriptPath(u.Target);
        if (!System.IO.File.Exists(path))
        {
            _ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"import: file not found: {path}",
                Operation = "import",
                Phase = ErrorPhase.Operation,
            });
            return ExecutionResult.Empty;
        }

        // ADR-0054 §5/§9: 加载脚本前由 IExecutionPolicyService 把关。
        // 若策略服务未注册 (如纯 AST 求值场景) 则跳过, 保持向后兼容。
        var policyService = _ctx.Host?.Services?.GetService<OpenShell.Security.IExecutionPolicyService>();
        if (policyService is not null)
        {
            var isRemote = policyService.IsRemoteFile(path);
            var (canExecute, reason) = policyService.CanExecute(path, isRemote);
            if (!canExecute)
            {
                _ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.PermissionDenied,
                    Message = $"import {path}: blocked by ExecutionPolicy — {reason}",
                    Operation = "import",
                    Phase = ErrorPhase.Operation,
                });
                return ExecutionResult.Empty;
            }
        }

        var source = System.IO.File.ReadAllText(path);
        ScriptBlockAst ast;
        try
        {
            // ADR-0050 §10.1: 按文件后缀选择 parser。.osh → ModernParser, .ps1 → PowerShellParser, 其他默认 PS。
            var ext = System.IO.Path.GetExtension(path);
            ast = string.Equals(ext, ".osh", StringComparison.OrdinalIgnoreCase)
                ? OpenShell.Parsing.ModernParser.Parse(source, path)
                : PowerShellParser.Parse(source, path);
        }
        catch (ParserException ex)
        {
            _ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ParseError,
                Message = $"import {path}: parse error at line {ex.Span.Start.Line}, col {ex.Span.Start.Column}: {ex.Message}",
                Operation = "import",
                Phase = ErrorPhase.Parse,
            });
            return ExecutionResult.Empty;
        }

        // 在当前作用域执行 (dot-source 语义: 定义的函数/变量注入当前作用域)。
        return Execute(ast);
    }

    private ExecutionResult EvaluatePipelineStatement(PipelineStatement ps)
    {
        // ADR-0044 §11: `command &` 后台执行 —— 启动 Task, 注册到 ITaskCenter, 立即返回。
        if (ps.Background)
        {
            return EvaluateBackgroundPipeline(ps.Pipeline);
        }
        return EvaluatePipeline(ps.Pipeline);
    }

    /// <summary>
    /// 后台执行管道 (Per ADR-0044 §11): 把管道求值放到 Task.Run, 注册任务句柄到 ITaskCenter,
    /// 立即返回空结果。调用方通过 Get-Job / Wait-Job 管理后台任务。
    /// </summary>
    private ExecutionResult EvaluateBackgroundPipeline(PipelineExpression pipe)
    {
        var taskCenter = _ctx.Host?.Services?.GetService<ITaskCenter>();
        if (taskCenter is null)
        {
            // 无 ITaskCenter: 退化为同步执行 (CLI 降级模式, Per ADR-0044 §11)。
            _ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Background execution requires ITaskCenter; running synchronously instead.",
                Operation = "background-pipeline",
                Phase = ErrorPhase.Operation,
            });
            return EvaluatePipeline(pipe);
        }

        // 构造展示标签: 取第一个命令段名 (如 "Get-ChildItem &" → "Background: Get-ChildItem")。
        var firstCmd = pipe.Commands.Count > 0 ? pipe.Commands[0].Name : "pipeline";
        var displayLabel = $"Background: {firstCmd}";

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ctx.CancellationToken);
        var registration = new TaskRegistration
        {
            Operation = "pipeline",
            DisplayLabel = displayLabel,
            Cts = cts,
            SupportsPause = false,
        };
        var handle = (TaskHandle)taskCenter.Register(registration);

        // 为后台任务构造派生 ExecutionContext: 共享 Variables/Commands/Host/Providers, 独立 CancellationToken。
        var bgCtx = new ExecutionContext(
            variables: _ctx.Variables,
            commands: _ctx.Commands,
            errors: _ctx.Errors,
            host: _ctx.Host,
            providers: _ctx.Providers,
            cancellationToken: cts.Token);
        var bgEval = new Evaluator(bgCtx);

        // 后台执行: 构造非后台 PipelineStatement 避免递归, 在新 Evaluator 上求值。
        _ = Task.Run(() =>
        {
            handle.MarkRunning();
            try
            {
                var stmt = new PipelineStatement(pipe, Background: false, pipe.Span);
                var result = bgEval.EvaluateStatement(stmt);
                if (result.Signal == FlowSignalKind.Throw)
                    handle.MarkFailed(new OpenShellScriptException(result.ThrownValue, bgCtx));
                else
                    handle.MarkCompleted();
            }
            catch (OperationCanceledException)
            {
                handle.MarkCancelled();
            }
            catch (Exception ex)
            {
                handle.MarkFailed(ex);
            }
        }, cts.Token);

        // 提示用户任务已启动。
        _ = _ctx.Host?.WriteOutputLineAsync($"Started background job: {displayLabel} (id: {handle.TaskId})", _ctx.CancellationToken);

        return ExecutionResult.Empty;
    }

    private ExecutionResult EvaluatePipeline(PipelineExpression pipe)
    {
        if (pipe.Commands.Count == 1)
        {
            var value = InvokeCommand(pipe.Commands[0]);
            return ExecutionResult.Of(value);
        }

        // 多命令管道：第一个命令产生流，后续每个命令作为 transform 处理每个项。
        // Per ADR-0010 §2 + ADR-0046 §5 + ADR-0048. 简化实现：仅支持 ScriptBlock transform。
        var headResult = InvokeCommand(pipe.Commands[0]);
        var stream = new List<object?>();
        foreach (var item in Enumerate(headResult))
            stream.Add(item is IItem ii ? ItemToValue(ii) : item);

        for (int i = 1; i < pipe.Commands.Count; i++)
        {
            var cmd = pipe.Commands[i];
            var sb = ResolveScriptBlockCommand(cmd);
            if (sb is null)
            {
                // 非 ScriptBlock 命令：暂不支持作为 transform，发出错误。
                _ctx.WriteError(ErrorRecord.FromException(
                    new InvalidOperationException(
                        $"pipeline transform must be a script block: {cmd.Name}"),
                    phase: ErrorPhase.Operation));
                break;
            }

            var nextStream = new List<object?>();
            var savedItem = _ctx.CurrentItem;
            try
            {
                foreach (var item in stream)
                {
                    _ctx.CurrentItem = ValueConversion.ValueToItem(item);
                    var result = sb.InvokeWithNamedArgs(_ctx, ExtractNamedArguments(cmd.Arguments));
                    if (result is null) continue;
                    // 展开多值输出（如 process 块返回数组）。
                    if (result is IEnumerable e and not string)
                    {
                        foreach (var x in e) nextStream.Add(x is IItem ix ? ItemToValue(ix) : x);
                    }
                    else
                    {
                        nextStream.Add(result);
                    }
                }
            }
            finally
            {
                _ctx.CurrentItem = savedItem;
            }
            stream = nextStream;
        }

        // 单值返回 scalar；多值返回数组。
        if (stream.Count == 0) return ExecutionResult.Empty;
        if (stream.Count == 1) return ExecutionResult.Of(stream[0]);
        return ExecutionResult.Of(stream);
    }

    /// <summary>解析命令为 ScriptBlock：调用脚本块字面量或变量表中的函数定义。</summary>
    private ScriptBlock? ResolveScriptBlockCommand(CommandExpression cmd)
    {
        // & { ... }：直接构造 ScriptBlock。
        if (cmd.Kind == CommandInvocationKind.CallOperator && cmd.Block is not null)
            return new ScriptBlock(cmd.Block, _ctx);

        // 命令名查变量表（用户定义函数）。
        if (_ctx.Variables is not null && _ctx.Variables.Resolve(cmd.Name) is ScriptBlock sb)
            return sb;

        return null;
    }

    // -------------------------------------------------------------------------
    // 赋值
    // -------------------------------------------------------------------------

    private ExecutionResult EvaluateAssignment(AssignmentStatement a)
    {
        var rhs = EvaluateExpression(a.Value).Value;
        var final = ApplyAssignmentOperator(a.Target, a.Operator, rhs);
        AssignTo(a.Target, final);
        return ExecutionResult.Of(final);
    }

    /// <summary>
    /// 求值类型化变量声明。Per ADR-0050 §7.1/§7.2: `$var: Type @Attr = value`。
    /// 求值初始值（如有），绑定到变量。特性暂不强制（未来可在此做运行时校验）。
    /// </summary>
    private ExecutionResult EvaluateVariableDeclaration(VariableDeclarationStatement vds)
    {
        if (vds.InitialValue is not null)
        {
            var value = EvaluateExpression(vds.InitialValue).Value;
            _ctx.Variables?.Set(vds.VariableName, value!);
            return ExecutionResult.Of(value);
        }
        return ExecutionResult.Empty;
    }

    /// <summary>
    /// 求值 #lang 块切换语句。Per ADR-0050 §1.3.
    /// 块切换仅影响语法解析，不影响作用域——块体语句在当前作用域顺序执行。
    /// </summary>
    private ExecutionResult EvaluateLangBlock(LangBlockStatement lbs)
    {
        ExecutionResult last = ExecutionResult.Empty;
        foreach (var stmt in lbs.Body)
        {
            var r = EvaluateStatement(stmt);
            if (r.Signal == FlowSignalKind.Throw)
                throw new OpenShellScriptException(r.ThrownValue, _ctx);
            if (r.Signal != FlowSignalKind.None)
                return r;
            last = r;
        }
        return last;
    }

    private object? ApplyAssignmentOperator(AssignTarget target, AssignmentOperator op, object? rhs)
    {
        if (op == AssignmentOperator.Assign) return rhs;
        var current = ReadFrom(target);
        return op switch
        {
            AssignmentOperator.AddAssign => Add(current, rhs),
            AssignmentOperator.SubtractAssign => Subtract(current, rhs),
            AssignmentOperator.MultiplyAssign => Multiply(current, rhs),
            AssignmentOperator.DivideAssign => Divide(current, rhs),
            AssignmentOperator.ModuloAssign => Modulo(current, rhs),
            AssignmentOperator.CoalesceAssign => current ?? rhs,
            _ => rhs,
        };
    }

    private void AssignTo(AssignTarget target, object? value)
    {
        switch (target)
        {
            case VariableTarget v:
                _ctx.Variables?.Set(v.Name, value!);
                break;
            case MemberTarget m:
                var obj = EvaluateExpression(m.Target).Value;
                SetMember(obj, m.MemberName, value);
                break;
            case IndexTarget idx:
                var collection = EvaluateExpression(idx.Target).Value;
                var index = EvaluateExpression(idx.Index).Value;
                SetIndex(collection, index, value);
                break;
        }
    }

    private object? ReadFrom(AssignTarget target) => target switch
    {
        VariableTarget v => _ctx.Variables?.Resolve(v.Name),
        MemberTarget m => GetMember(EvaluateExpression(m.Target).Value, m.MemberName),
        IndexTarget idx => GetIndex(EvaluateExpression(idx.Target).Value, EvaluateExpression(idx.Index).Value),
        _ => null,
    };

    // -------------------------------------------------------------------------
    // if / elseif / else
    // -------------------------------------------------------------------------

    private ExecutionResult EvaluateIf(IfStatement iff)
    {
        foreach (var branch in iff.Branches)
        {
            if (IsTruthy(EvaluateExpression(branch.Condition).Value))
                return EvaluateBlock(branch.Body);
        }
        if (iff.ElseBody is not null)
            return EvaluateBlock(iff.ElseBody);
        return ExecutionResult.Empty;
    }

    // -------------------------------------------------------------------------
    // labeled statement (ADR-0050 §5.1 break/continue label)
    // -------------------------------------------------------------------------

    private ExecutionResult EvaluateLabeledStatement(LabeledStatement ls)
    {
        var r = EvaluateStatement(ls.Body);
        // 带标签的 break/continue 匹配则消费（转为正常结果）。
        // 注意：循环体已在 AttachLoopLabel 时下放 Label，循环内部会自行消费匹配的
        // break/continue label；此分支仅处理非循环体（如 labeled if 块）的安全网。
        if ((r.Signal == FlowSignalKind.Break || r.Signal == FlowSignalKind.Continue)
            && string.Equals(r.Label, ls.Label, StringComparison.OrdinalIgnoreCase))
        {
            return ExecutionResult.Empty;
        }
        return r;
    }

    /// <summary>判断 break/continue 信号是否归属本循环（无标签或匹配本循环标签）。</summary>
    private static bool BelongsToThisLoop(ExecutionResult r, string? loopLabel)
        => r.Label is null
           || (loopLabel is not null && string.Equals(r.Label, loopLabel, StringComparison.OrdinalIgnoreCase));

    // -------------------------------------------------------------------------
    // while / do-while / do-until
    // -------------------------------------------------------------------------

    private ExecutionResult EvaluateWhile(WhileStatement w)
    {
        while (IsTruthy(EvaluateExpression(w.Condition).Value))
        {
            var r = EvaluateBlock(w.Body);
            if (r.Signal == FlowSignalKind.Break) { if (!BelongsToThisLoop(r, w.Label)) return r; break; }
            if (r.Signal == FlowSignalKind.Continue) { if (!BelongsToThisLoop(r, w.Label)) return r; continue; }
            if (r.Signal != FlowSignalKind.None) return r;
        }
        return ExecutionResult.Empty;
    }

    private ExecutionResult EvaluateDoWhile(DoWhileStatement dw)
    {
        ExecutionResult r = ExecutionResult.Empty;
        while (true)
        {
            r = EvaluateBlock(dw.Body);
            if (r.Signal == FlowSignalKind.Break) { if (!BelongsToThisLoop(r, dw.Label)) return r; break; }
            if (r.Signal == FlowSignalKind.Continue) { if (!BelongsToThisLoop(r, dw.Label)) return r; /* fallthrough */ }
            else if (r.Signal != FlowSignalKind.None) return r;
            var cond = IsTruthy(EvaluateExpression(dw.Condition).Value);
            if (dw.Until && cond) break;
            if (!dw.Until && !cond) break;
        }
        return r;
    }

    // -------------------------------------------------------------------------
    // for
    // -------------------------------------------------------------------------

    private ExecutionResult EvaluateFor(ForStatement f)
    {
        if (f.Initializer is not null) EvaluateExpression(f.Initializer);
        while (f.Condition is null || IsTruthy(EvaluateExpression(f.Condition).Value))
        {
            var r = EvaluateBlock(f.Body);
            if (r.Signal == FlowSignalKind.Break) { if (!BelongsToThisLoop(r, f.Label)) return r; break; }
            if (r.Signal == FlowSignalKind.Continue) { if (!BelongsToThisLoop(r, f.Label)) return r; /* fallthrough to iterator */ }
            else if (r.Signal != FlowSignalKind.None) return r;
            if (f.Iterator is not null) EvaluateExpression(f.Iterator);
        }
        return ExecutionResult.Empty;
    }

    // -------------------------------------------------------------------------
    // foreach
    // -------------------------------------------------------------------------

    private ExecutionResult EvaluateForEach(ForEachStatement fe)
    {
        var iterable = EvaluateExpression(fe.Iterable).Value;

        // ADR-0050 §5.3: for $k, $v in hash —— 键值对解构迭代。
        if (fe.Kind == ForEachKind.KeyValuePair && fe.KeyValueNames is { } kv)
        {
            if (iterable is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    _ctx.Variables?.Set(kv.Key, entry.Key!);
                    _ctx.Variables?.Set(kv.Value, entry.Value!);
                    var r = EvaluateBlock(fe.Body);
                    if (r.Signal == FlowSignalKind.Break) { if (!BelongsToThisLoop(r, fe.Label)) return r; break; }
                    if (r.Signal == FlowSignalKind.Continue) { if (!BelongsToThisLoop(r, fe.Label)) return r; continue; }
                    if (r.Signal != FlowSignalKind.None) return r;
                }
                return ExecutionResult.Empty;
            }
            if (iterable is System.Collections.IDictionary nonGeneric)
            {
                foreach (DictionaryEntry entry in nonGeneric)
                {
                    _ctx.Variables?.Set(kv.Key, entry.Key!);
                    _ctx.Variables?.Set(kv.Value, entry.Value!);
                    var r = EvaluateBlock(fe.Body);
                    if (r.Signal == FlowSignalKind.Break) { if (!BelongsToThisLoop(r, fe.Label)) return r; break; }
                    if (r.Signal == FlowSignalKind.Continue) { if (!BelongsToThisLoop(r, fe.Label)) return r; continue; }
                    if (r.Signal != FlowSignalKind.None) return r;
                }
                return ExecutionResult.Empty;
            }
            // 非字典类型退化为单变量迭代（用 key 变量名）
            foreach (var item in Enumerate(iterable))
            {
                _ctx.Variables?.Set(kv.Key, item!);
                var r = EvaluateBlock(fe.Body);
                if (r.Signal == FlowSignalKind.Break) { if (!BelongsToThisLoop(r, fe.Label)) return r; break; }
                if (r.Signal == FlowSignalKind.Continue) { if (!BelongsToThisLoop(r, fe.Label)) return r; continue; }
                if (r.Signal != FlowSignalKind.None) return r;
            }
            return ExecutionResult.Empty;
        }

        foreach (var item in Enumerate(iterable))
        {
            _ctx.Variables?.Set(fe.Variable, item!);
            var r = EvaluateBlock(fe.Body);
            if (r.Signal == FlowSignalKind.Break) { if (!BelongsToThisLoop(r, fe.Label)) return r; break; }
            if (r.Signal == FlowSignalKind.Continue) { if (!BelongsToThisLoop(r, fe.Label)) return r; continue; }
            if (r.Signal != FlowSignalKind.None) return r;
        }
        return ExecutionResult.Empty;
    }

    // -------------------------------------------------------------------------
    // switch (per ADR-0045 §6)
    // -------------------------------------------------------------------------

    private ExecutionResult EvaluateSwitch(SwitchStatement sw)
    {
        // Per ADR-0045 §6:
        // - $_ is the current input value inside case bodies
        // - fall-through: matching case executes, then continues to next case (unless break)
        // - -File mode: read file lines as input, each line tested against all cases
        // - -Regex / -Wildcard / -CaseSensitive control pattern matching

        var caseSensitive = (sw.Flags & SwitchFlags.CaseSensitive) != 0;
        var useRegex = (sw.Flags & SwitchFlags.Regex) != 0;
        var useWildcard = (sw.Flags & SwitchFlags.Wildcard) != 0;
        var useFile = (sw.Flags & SwitchFlags.File) != 0;

        // Determine input values to test.
        IEnumerable<object?> inputs;
        if (useFile)
        {
            var filePath = EvaluateExpression(sw.Test).Value?.ToString();
            if (filePath is null || !System.IO.File.Exists(filePath))
                return ExecutionResult.Empty;
            inputs = System.IO.File.ReadAllLines(filePath).Cast<object?>();
        }
        else
        {
            var testValue = EvaluateExpression(sw.Test).Value;
            inputs = new[] { testValue };
        }

        ExecutionResult lastResult = ExecutionResult.Empty;
        var outputs = new List<object?>();

        foreach (var input in inputs)
        {
            // Set $_ to current input (per ADR-0042 §3.4).
            // $_ / $PSItem 通过 _ctx.CurrentItem 绑定（EvaluateVariable 直接读取 CurrentItem），
            // 不能用 Variables.Set 因为 $_ 是只读自动变量。
            _ctx.CurrentItem = ValueConversion.ValueToItem(input);
            // Also set $switch enumerator (per ADR-0042 §3.5) — simplified: not a real IEnumerator.
            _ctx.Variables?.Set("switch", input!);

            var matched = false;
            foreach (var c in sw.Cases)
            {
                var pattern = EvaluateExpression(c.Pattern).Value;
                var isMatch = useRegex
                    ? RegexMatch(input, pattern, caseSensitive)
                    : useWildcard
                        ? LikeMatch(input, pattern, caseSensitive)
                        : EqualsCaseInsensitive(input, pattern, caseSensitive);

                if (isMatch)
                {
                    matched = true;
                    var r = EvaluateBlock(c.Body);
                    if (r.Signal == FlowSignalKind.Break)
                    { if (r.Label != null) return r; return CollectOutputs(outputs, lastResult); }
                    if (r.Signal != FlowSignalKind.None)
                        return r;
                    if (r.Value is not null)
                        outputs.Add(r.Value);
                    lastResult = r;
                    // Per ADR-0045 §6: fall-through by default (no implicit break).
                }
            }

            // Default case: only if no case matched for this input.
            if (!matched && sw.Default is not null)
            {
                var r = EvaluateBlock(sw.Default);
                if (r.Signal == FlowSignalKind.Break)
                { if (r.Label != null) return r; return CollectOutputs(outputs, lastResult); }
                if (r.Signal != FlowSignalKind.None)
                    return r;
                if (r.Value is not null)
                    outputs.Add(r.Value);
                lastResult = r;
            }
        }

        return CollectOutputs(outputs, lastResult);
    }

    private static ExecutionResult CollectOutputs(List<object?> outputs, ExecutionResult fallback)
    {
        if (outputs.Count == 0) return fallback;
        if (outputs.Count == 1) return ExecutionResult.Of(outputs[0]);
        return ExecutionResult.Of(outputs);
    }

    private static bool EqualsCaseInsensitive(object? a, object? b, bool caseSensitive)
    {
        if (a is null || b is null) return a is null && b is null;
        if (caseSensitive)
            return Equals(a, b);
        if (a is string sa && b is string sb)
            return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        return Equals(a, b);
    }

    // -------------------------------------------------------------------------
    // try / catch / finally
    // -------------------------------------------------------------------------

    private ExecutionResult EvaluateTry(TryStatement t)
    {
        ExecutionResult result;
        Exception? caught = null;
        try
        {
            result = EvaluateBlock(t.Body);
            if (result.Signal == FlowSignalKind.Throw)
                throw new OpenShellScriptException(result.ThrownValue, _ctx);
        }
        catch (Exception ex)
        {
            caught = ex;
            var handler = FindCatchHandler(t, ex);
            if (handler is null) throw;
            var thrown = ex is OpenShellScriptException se ? se.ThrownValue : ex;
            if (handler.Variable is not null)
                _ctx.Variables?.Set(handler.Variable, thrown!);
            result = EvaluateBlock(handler.Body);
        }
        finally
        {
            if (t.Finally is not null && caught is null)
            {
                var fr = EvaluateBlock(t.Finally);
                if (fr.Signal != FlowSignalKind.None)
                    result = fr;
            }
        }
        return result;
    }

    private static CatchClause? FindCatchHandler(TryStatement t, Exception ex)
    {
        foreach (var c in t.Catches)
        {
            if (c.ExceptionTypes is null || c.ExceptionTypes.Count == 0)
                return c;
            foreach (var typeRef in c.ExceptionTypes)
            {
                var type = ResolveType(typeRef);
                if (type is not null && type.IsAssignableFrom(ex.GetType()))
                    return c;
            }
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // throw / exit
    // -------------------------------------------------------------------------

    private ExecutionResult EvaluateThrow(ThrowStatement t)
    {
        var value = t.Value is null ? null : EvaluateExpression(t.Value).Value;
        return ExecutionResult.Throw(value);
    }

    private int EvaluateExitCode(ExitStatement e)
    {
        if (e.Code is null) return 0;
        var v = EvaluateExpression(e.Code).Value;
        return v switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };
    }

    // -------------------------------------------------------------------------
    // 函数定义 / param 块
    // -------------------------------------------------------------------------

    private ExecutionResult DefineFunction(FunctionDefinitionStatement fn)
    {
        var sb = new ScriptBlock(fn.Body, _ctx) { ReturnType = fn.ReturnType };
        _ctx.Variables?.Set(fn.Name, sb);
        return ExecutionResult.Empty;
    }

    private ExecutionResult EvaluateParamBlock(ParamBlockStatement p)
    {
        BindParameters(p.Parameters, _ctx.CurrentArgs ?? Array.Empty<object?>());
        return ExecutionResult.Empty;
    }

    private void BindParameters(IReadOnlyList<ParameterDeclaration> parameters, object?[] args)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            object? value = null;
            bool bound = false;
            // Position < 0 时按声明顺序绑定（param($a, $b) 的默认语义）
            int pos = p.Position >= 0 ? p.Position : i;
            if (pos >= 0 && pos < args.Length)
            {
                value = args[pos];
                bound = true;
            }
            if (!bound)
            {
                value = p.DefaultValue is not null
                    ? EvaluateExpression(p.DefaultValue).Value
                    : (p.Mandatory ? throw new ArgumentNullException(p.Name) : null);
            }
            if (value is not null && p.Type is not null)
            {
                // ADR-0052 §5: 严格模式下使用 TypeAnnotation 复合强制（支持 int? / int|string / List<int>）。
                if (_ctx.StrictMode)
                {
                    var annotation = TypeCoercer.ParseTypeAnnotation(p.Type.FullName);
                    if (annotation is not null)
                    {
                        try { value = TypeCoercer.Coerce(value, annotation); }
                        catch (InvalidCastException ex)
                        {
                            _ctx.WriteError(ErrorRecord.FromException(ex, phase: ErrorPhase.Operation));
                        }
                    }
                    else
                    {
                        var type = ResolveType(p.Type);
                        if (type is not null) value = ConvertValue(value, type);
                    }
                }
                else
                {
                    var type = ResolveType(p.Type);
                    if (type is not null) value = ConvertValue(value, type);
                }
            }
            _ctx.Variables?.Set(p.Name, value!);
        }
    }

    // -------------------------------------------------------------------------
    // Block 执行
    // -------------------------------------------------------------------------

    public ExecutionResult EvaluateBlock(IReadOnlyList<Statement> body)
    {
        ExecutionResult last = ExecutionResult.Empty;
        foreach (var stmt in body)
        {
            var r = EvaluateStatement(stmt);
            if (r.Signal != FlowSignalKind.None)
                return r;
            last = r;
        }
        return last;
    }

    // =========================================================================
    // Expression 求值
    // =========================================================================

    public ExecutionResult EvaluateExpression(Expression expr)
    {
        // ADR-0058 §5: JIT 委托缓存查询。
        // 若 ICompilationCache + HotPathTracker 已注册, 走分层编译路径; 否则直接解释执行。
        var cache = _ctx.Host?.Services?.GetService(typeof(OpenShell.Compilation.ICompilationCache))
            as OpenShell.Compilation.ICompilationCache;
        var tracker = _ctx.Host?.Services?.GetService(typeof(OpenShell.Compilation.HotPathTracker))
            as OpenShell.Compilation.HotPathTracker;
        if (cache is not null && tracker is not null)
        {
            // 已编译: 直接调用委托, 跳过 AST switch。
            if (cache.TryGet(expr, out var del))
                return ExecutionResult.Of(del(_ctx));

            // 已标记 uncacheable: 跳过编译, 走解释执行 (避免重复尝试已知不支持的节点)。
            if (cache.IsUncacheable(expr))
                return EvaluateExpressionInterpreted(expr);

            // 记录调用次数, 达阈值 (默认 32) 触发 Tier 1 编译。
            tracker.RecordInvocation(expr);
            if (tracker.IsHotPath(expr))
            {
                var compiler = _ctx.Host?.Services?.GetService(typeof(OpenShell.Compilation.ExpressionCompiler))
                    as OpenShell.Compilation.ExpressionCompiler;
                if (compiler is not null)
                {
                    try
                    {
                        if (compiler.TryCompile(expr, out del))
                        {
                            cache.Store(expr, del);
                            tracker.Reset(expr);
                            return ExecutionResult.Of(del(_ctx));
                        }
                        // TryCompile 返回 false (理论上不会, 不支持的节点抛异常): 标记 uncacheable。
                        cache.MarkUncacheable(expr);
                    }
                    catch (NotSupportedException)
                    {
                        // 节点不支持编译: 标记 uncacheable, 后续不再尝试。Per ADR-0058 §2.3.
                        cache.MarkUncacheable(expr);
                    }
                }
            }
        }

        return EvaluateExpressionInterpreted(expr);
    }

    /// <summary>
    /// AST switch 解释执行路径 (Tier 0)。Per ADR-0058 §5: 原 EvaluateExpression 主体, 重命名以让出 JIT 入口。
    /// </summary>
    private ExecutionResult EvaluateExpressionInterpreted(Expression expr) => expr switch
    {
        // ExpandableStringExpression：含 $var/${name}/$(expr) 插值段的双引号字符串。
        // 借鉴 PS ExpandableStringExpressionAst（ast.cs:9888-9893）：求值各嵌套表达式后用 string.Format 拼接。
        // Per T-106。
        ExpandableStringExpression es => EvaluateExpandableString(es),
        LiteralExpression l => l.Kind is LiteralKind.String or LiteralKind.HereString
            ? ExecutionResult.Of(_ctx.Variables is null
                ? l.Value
                : VariableExpander.ExpandInterpolation(l.Value?.ToString() ?? string.Empty, _ctx.Variables))
            : ExecutionResult.Of(l.Value),
        VariableExpression v => ExecutionResult.Of(EvaluateVariable(v)),
        BinaryExpression b => ExecutionResult.Of(EvaluateBinary(b)),
        UnaryExpression u => ExecutionResult.Of(EvaluateUnary(u)),
        MemberExpression m => ExecutionResult.Of(EvaluateMember(m)),
        IndexExpression i => ExecutionResult.Of(EvaluateIndex(i)),
        CastExpression c => ExecutionResult.Of(EvaluateCast(c)),
        SubExpressionExpression s => EvaluateExpression(s.Inner),
        // T-113: $(...) 内含语句——执行语句块，返回末语句输出（借鉴 PS $(...) 语义）。
        StatementSubExpressionExpression sse => EvaluateStatementSubExpression(sse),
        ArrayExpression a => ExecutionResult.Of(EvaluateArray(a)),
        HashExpression h => ExecutionResult.Of(EvaluateHash(h)),
        RangeExpression r => ExecutionResult.Of(EvaluateRange(r)),
        PipelineExpression p => EvaluatePipeline(p),
        CommandExpression c => ExecutionResult.Of(InvokeCommand(c)),
        ScriptBlockExpression sb => ExecutionResult.Of(new ScriptBlock(sb, _ctx)),
        TernaryExpression t => EvaluateTernary(t),
        LambdaExpression l => EvaluateLambda(l),
        MatchExpression m => EvaluateMatch(m),
        AssignmentExpression ae => EvaluateAssignmentExpression(ae),
        // ADR-0051 §2: await expr —— 同步解包 Task / ValueTask / IAsyncEnumerable。
        AwaitExpressionAst aw => EvaluateAwait(aw),
        // ADR-0051 §3: async { } —— 返回 Task<object?>，体延迟到 await 时执行。
        AsyncBlockExpression ab => EvaluateAsyncBlock(ab),
        // ADR-0052 §4: 类型引用表达式仅在 `is`/`isnot` 右侧有意义，直接求值返回 null。
        TypeReferenceExpression => ExecutionResult.Empty,
        // ADR-0053 §3: 宏调用展开后求值。
        MacroInvocationExpression mi => EvaluateMacroInvocation(mi),
        _ => ExecutionResult.Empty,
    };

    /// <summary>
    /// 求值可展开字符串表达式。借鉴 PS ExpandableStringExpressionAst.GetValue
    /// （ast.cs:9888-9893）：逐个求值 NestedExpressions，再用 string.Format 拼接。
    /// Per T-106。
    /// </summary>
    private ExecutionResult EvaluateExpandableString(ExpandableStringExpression es)
    {
        if (es.NestedExpressions.Count == 0)
            return ExecutionResult.Of(es.Value);

        var args = new object?[es.NestedExpressions.Count];
        for (int i = 0; i < es.NestedExpressions.Count; i++)
            args[i] = EvaluateExpression(es.NestedExpressions[i]).Value;

        try
        {
            // string.Format 不接受 null 数组元素本身，但接受数组中元素为 null。
            // PS 对 null 嵌套表达式输出空字符串，这里用 string.Format 默认行为（输出空字符串）。
            var result = string.Format(es.FormatExpression, args);
            return ExecutionResult.Of(result);
        }
        catch (FormatException)
        {
            // 格式串异常（如 {N} 越界），降级返回原始 Value。
            return ExecutionResult.Of(es.Value);
        }
    }

    /// <summary>
    /// 求值语句子表达式 $(...)。Per T-113。
    /// 借鉴 PS $(...) 语义：执行语句块，返回末语句的输出值。
    /// 若语句产生管道输出（如 Get-ChildItem），返回输出对象；若为赋值等无输出语句，返回 null。
    /// </summary>
    private ExecutionResult EvaluateStatementSubExpression(StatementSubExpressionExpression sse)
    {
        ExecutionResult last = ExecutionResult.Empty;
        foreach (var stmt in sse.Statements)
        {
            var r = EvaluateStatement(stmt);
            if (r.Signal == FlowSignalKind.Throw)
                throw new OpenShellScriptException(r.ThrownValue, _ctx);
            if (r.Signal != FlowSignalKind.None)
                return r;
            last = r;
        }
        return last;
    }

    private ExecutionResult EvaluateAssignmentExpression(AssignmentExpression a)
    {
        var rhs = EvaluateExpression(a.Value).Value;
        var final = ApplyAssignmentOperator(a.Target, a.Operator, rhs);
        AssignTo(a.Target, final);
        return ExecutionResult.Of(final);
    }

    // -------------------------------------------------------------------------
    // 变量
    // -------------------------------------------------------------------------

    private object? EvaluateVariable(VariableExpression v)
    {
        // $_ / $PSItem：pipeline 当前项。Per ADR-0042 §3.4.
        if (v.Name == "_" || v.Name.Equals("PSItem", StringComparison.OrdinalIgnoreCase))
            return _ctx.CurrentItem is null ? null : ItemToValue(_ctx.CurrentItem);

        // $args：当前函数/脚本块的位置参数数组。Per ADR-0042 §3.
        if (v.Name.Equals("args", StringComparison.OrdinalIgnoreCase))
            return _ctx.CurrentArgs ?? Array.Empty<object?>();

        if (_ctx.Variables is null) return null;

        return v.Scope switch
        {
            VariableScopeKind.Environment => GetEnvVariable(v.Name),
            VariableScopeKind.Global => _ctx.Variables.Resolve(v.Name, VariableScope.Global),
            VariableScopeKind.Script => _ctx.Variables.Resolve(v.Name, VariableScope.Script),
            VariableScopeKind.Local => _ctx.Variables.Resolve(v.Name, VariableScope.Local),
            VariableScopeKind.Private => _ctx.Variables.Resolve(v.Name, VariableScope.Private),
            // Per ADR-0047 §1.2 + ADR-0046 §4: $using: 退化为 Local 查找（本地闭包语义）。
            VariableScopeKind.Using => _ctx.Variables.Resolve(v.Name, VariableScope.Local),
            _ => _ctx.Variables.Resolve(v.Name),
        };
    }

    private static string? GetEnvVariable(string name) =>
        Environment.GetEnvironmentVariable(name);

    // -------------------------------------------------------------------------
    // 二元运算
    // -------------------------------------------------------------------------

    private object? EvaluateBinary(BinaryExpression b)
    {
        if (b.Operator == BinaryOperator.And)
        {
            var left = EvaluateExpression(b.Left).Value;
            if (!IsTruthy(left)) return false;
            return IsTruthy(EvaluateExpression(b.Right).Value);
        }
        if (b.Operator == BinaryOperator.Or)
        {
            var left = EvaluateExpression(b.Left).Value;
            if (IsTruthy(left)) return true;
            return IsTruthy(EvaluateExpression(b.Right).Value);
        }
        if (b.Operator == BinaryOperator.NullCoalesce)
        {
            var left = EvaluateExpression(b.Left).Value;
            return left ?? EvaluateExpression(b.Right).Value;
        }

        var lv = EvaluateExpression(b.Left).Value;

        // ADR-0052 §4: is / isnot 右侧为类型引用，直接从 AST 取 TypeReference，
        // 避免求值 TypeReferenceExpression 得到 null。支持复合类型（int? / int|string / List<int>）。
        if (b.Operator is BinaryOperator.Is or BinaryOperator.IsNot)
        {
            bool matched = IsType(lv, b.Right);
            return b.Operator == BinaryOperator.Is ? matched : !matched;
        }

        var rv = EvaluateExpression(b.Right).Value;

        // ADR-0057 §4: 优先尝试 op_* 运算符重载，失败则回退到内建运算。
        if (OperatorOverloadResolver.TryInvoke(b.Operator, lv, rv, out var overloaded))
        {
            // op_Equal 返回 bool：Ne/NotEquals 需取反。
            if (b.Operator is BinaryOperator.Ne or BinaryOperator.NotEquals)
                return !IsTruthy(overloaded);
            // op_Compare 返回 int（<0 / 0 / >0）：Lt/Gt/Le/Ge 转换为 bool。
            if (b.Operator is BinaryOperator.Lt or BinaryOperator.Gt or BinaryOperator.Le or BinaryOperator.Ge)
                return OpCompareToBool(b.Operator, overloaded);
            return overloaded;
        }

        return b.Operator switch
        {
            BinaryOperator.Add => Add(lv, rv),
            BinaryOperator.Subtract => Subtract(lv, rv),
            BinaryOperator.Multiply => Multiply(lv, rv),
            BinaryOperator.Divide => Divide(lv, rv),
            BinaryOperator.Modulo => Modulo(lv, rv),
            BinaryOperator.Power => Power(lv, rv),
            BinaryOperator.Eq or BinaryOperator.Equals => Equals(lv, rv),
            BinaryOperator.Ne or BinaryOperator.NotEquals => !Equals(lv, rv),
            BinaryOperator.Lt => Compare(lv, rv) < 0,
            BinaryOperator.Gt => Compare(lv, rv) > 0,
            BinaryOperator.Le => Compare(lv, rv) <= 0,
            BinaryOperator.Ge => Compare(lv, rv) >= 0,
            BinaryOperator.Like => LikeMatch(lv, rv, false),
            BinaryOperator.NotLike => !LikeMatch(lv, rv, false),
            BinaryOperator.Match => RegexMatch(lv, rv, caseSensitive: false),
            BinaryOperator.NotMatch => !RegexMatch(lv, rv, caseSensitive: false),
            BinaryOperator.In => InMatch(lv, rv),
            BinaryOperator.NotIn => !InMatch(lv, rv),
            BinaryOperator.Contains => InMatch(rv, lv),
            BinaryOperator.NotContains => !InMatch(rv, lv),
            BinaryOperator.As => ConvertAs(lv, rv),
            BinaryOperator.BitwiseAnd => ToLong(lv) & ToLong(rv),
            BinaryOperator.BitwiseOr => ToLong(lv) | ToLong(rv),
            BinaryOperator.BitwiseXor => ToLong(lv) ^ ToLong(rv),
            BinaryOperator.ShiftLeft => ToLong(lv) << (int)ToLong(rv),
            BinaryOperator.ShiftRight => ToLong(lv) >> (int)ToLong(rv),
            // ADR-0050 §2.1: ++ 数组拼接
            BinaryOperator.ArrayConcat => ArrayConcat(lv, rv),
            _ => null,
        };
    }

    /// <summary>数组拼接：将两个可枚举对象拼接为新数组。Per ADR-0050 §2.1.</summary>
    private static object ArrayConcat(object? a, object? b)
    {
        var la = Enumerate(a).Cast<object?>().ToList();
        la.AddRange(Enumerate(b).Cast<object?>());
        return la;
    }

    // -------------------------------------------------------------------------
    // 一元运算
    // -------------------------------------------------------------------------

    private object? EvaluateUnary(UnaryExpression u)
    {
        if (u.Operator == UnaryOperator.PostfixIncrement || u.Operator == UnaryOperator.PostfixDecrement)
        {
            if (u.Operand is VariableExpression ve && _ctx.Variables is not null)
            {
                var cur = _ctx.Variables.Resolve(ve.Name);
                var newVal = u.Operator == UnaryOperator.PostfixIncrement ? Add(cur, 1L) : Subtract(cur, 1L);
                _ctx.Variables.Set(ve.Name, newVal!);
                return cur;
            }
            return null;
        }

        var val = EvaluateExpression(u.Operand).Value;
        return u.Operator switch
        {
            UnaryOperator.Not => !IsTruthy(val),
            UnaryOperator.BitwiseNot => ~ToLong(val),
            UnaryOperator.Negate => Subtract(0L, val),
            UnaryOperator.Plus => val,
            UnaryOperator.PrefixIncrement => IncrementAndReturn(u.Operand, +1),
            UnaryOperator.PrefixDecrement => IncrementAndReturn(u.Operand, -1),
            UnaryOperator.Spread => val,
            _ => val,
        };
    }

    private object? IncrementAndReturn(Expression operand, int delta)
    {
        if (operand is VariableExpression ve && _ctx.Variables is not null)
        {
            var cur = _ctx.Variables.Resolve(ve.Name);
            var newVal = delta > 0 ? Add(cur, 1L) : Subtract(cur, 1L);
            _ctx.Variables.Set(ve.Name, newVal!);
            return newVal;
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // 成员访问 / 索引 / 类型转换 / 数组 / 哈希 / 范围
    // -------------------------------------------------------------------------

    private object? EvaluateMember(MemberExpression m)
    {
        if (m.Static)
        {
            // 静态成员访问 [Type]::Member
            if (m.Target is not TypeReferenceExpression te) return null;
            var type = ResolveType(te.Type);
            if (type is null) return null;
            // Arguments is null → 静态属性访问；非 null → 静态方法调用（即使无参）。
            if (m.Arguments is null)
                return type.GetProperty(m.MemberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null);
            var args = m.Arguments.Select(EvaluateExpression).Select(r => r.Value).ToArray();
            return type.GetMethod(m.MemberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.Invoke(null, args);
        }

        var target = EvaluateExpression(m.Target).Value;
        if (target is null) return m.NullConditional ? null : null;
        // Arguments is null → 属性访问（$obj.Prop）；Arguments 非 null（即使空列表）→ 方法调用（$obj.Method()）。
        if (m.Arguments is null)
            return GetMember(target, m.MemberName);
        var methodArgs = m.Arguments.Select(EvaluateExpression).Select(r => r.Value).ToArray();
        return InvokeMethod(target, m.MemberName, methodArgs);
    }

    private object? EvaluateIndex(IndexExpression i)
    {
        var target = EvaluateExpression(i.Target).Value;
        // ADR-0050 §4.1: ?[ null 条件索引——null 目标返回 null 而不抛错。
        if (i.NullConditional && target is null) return null;
        var index = EvaluateExpression(i.Index).Value;
        return GetIndex(target, index);
    }

    private object? EvaluateCast(CastExpression c)
    {
        // Per ADR-0047 §6.2: [ordered]@{ } 创建 OrderedDictionary (保持插入顺序)。
        // 原标记为 M5+ 延迟实现, 现已落实。
        var typeName = c.Type.FullName?.ToLowerInvariant();
        if (typeName == "ordered" && c.Operand is HashExpression he)
        {
            var ordered = new System.Collections.Specialized.OrderedDictionary(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in he.Entries)
            {
                var key = EvaluateExpression(entry.Key).Value?.ToString() ?? "";
                var val = EvaluateExpression(entry.Value).Value;
                ordered[key] = val;
            }
            return ordered;
        }

        var value = EvaluateExpression(c.Operand).Value;
        var type = ResolveType(c.Type);
        if (type is null) return value;
        return ConvertValue(value, type);
    }

    private object? EvaluateArray(ArrayExpression a)
    {
        var list = new List<object?>();
        foreach (var e in a.Elements)
        {
            if (e is RangeExpression re)
            {
                var range = EvaluateRange(re);
                if (range is IEnumerable enumerable)
                    foreach (var item in enumerable) list.Add(item);
            }
            else
            {
                list.Add(EvaluateExpression(e).Value);
            }
        }
        return list.ToArray();
    }

    private object? EvaluateHash(HashExpression h)
    {
        // Per ADR-0047 §6.1: 哈希字面量使用大小写不敏感键。
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in h.Entries)
        {
            var key = EvaluateExpression(entry.Key).Value?.ToString() ?? "";
            var val = EvaluateExpression(entry.Value).Value;
            dict[key] = val;
        }
        return dict;
    }

    private object? EvaluateRange(RangeExpression r)
    {
        var start = EvaluateExpression(r.Start).Value;
        var end = EvaluateExpression(r.End).Value;
        return BuildRange(start, end, r.IsHalfOpen);
    }

    /// <summary>
    /// 构造范围数组 (1..10 / 'a'..'z')。Per ADR-0058 §6: 公开供 ExpressionCompiler 复用。
    /// </summary>
    public static object? BuildRange(object? start, object? end) => BuildRange(start, end, halfOpen: false);

    /// <summary>构造范围数组。halfOpen=true 时排除结束值（半开范围语义）。Per ADR-0050 §4.</summary>
    public static object? BuildRange(object? start, object? end, bool halfOpen)
    {
        if (start is int si && end is int ei)
        {
            var list = new List<int>();
            if (si <= ei) { var last = halfOpen ? ei - 1 : ei; for (int i = si; i <= last; i++) list.Add(i); }
            else { var last = halfOpen ? ei + 1 : ei; for (int i = si; i >= last; i--) list.Add(i); }
            return list.ToArray();
        }
        if (start is long sl && end is long el)
        {
            var list = new List<long>();
            if (sl <= el) { var last = halfOpen ? el - 1 : el; for (long i = sl; i <= last; i++) list.Add(i); }
            else { var last = halfOpen ? el + 1 : el; for (long i = sl; i >= last; i--) list.Add(i); }
            return list.ToArray();
        }
        if (start is char sc && end is char ec)
        {
            var list = new List<char>();
            if (sc <= ec) { var last = halfOpen ? (char)(ec - 1) : ec; for (char c = sc; c <= last; c++) list.Add(c); }
            else { var last = halfOpen ? (char)(ec + 1) : ec; for (char c = sc; c >= last; c--) list.Add(c); }
            return list.ToArray();
        }
        return Array.Empty<object>();
    }

    private ExecutionResult EvaluateTernary(TernaryExpression t)
    {
        var cond = EvaluateExpression(t.Condition).Value;
        return IsTruthy(cond) ? EvaluateExpression(t.IfTrue) : EvaluateExpression(t.IfFalse);
    }

    private ExecutionResult EvaluateLambda(LambdaExpression l)
    {
        var sb = new ScriptBlock(
            new ScriptBlockExpression(
                new[] { (Statement)new ReturnStatement(l.Body, l.Body.Span) },
                l.Parameters,
                l.Body.Span),
            _ctx);
        return ExecutionResult.Of(sb);
    }

    private ExecutionResult EvaluateMatch(MatchExpression m)
    {
        // ADR-0055: AdvancedPattern 优先；为 null 时回退到旧式 Expression 字面量模式。
        var subject = EvaluateExpression(m.Subject).Value;
        foreach (var arm in m.Arms)
        {
            // 旧式字面量模式：arm.AdvancedPattern 为 null 且 arm.Pattern 不为 null。
            if (arm.AdvancedPattern is null)
            {
                if (arm.Pattern is null)
                    return EvaluateExpression(arm.Body);
                var pat = EvaluateExpression(arm.Pattern).Value;
                if (Equals(subject, pat))
                    return EvaluateExpression(arm.Body);
                continue;
            }

            // 高级模式匹配（ADR-0055）：匹配成功后绑定变量在 arm.Body 作用域内可见。
            if (MatchPattern(arm.AdvancedPattern, subject, out _))
                return EvaluateExpression(arm.Body);
        }
        return ExecutionResult.Empty;
    }

    /// <summary>
    /// ADR-0055 §1-6: 递归匹配 PatternAst。匹配成功时把绑定变量写入当前作用域。
    /// <para>返回 true 表示整体匹配；false 表示不匹配（不抛异常，由调用方尝试下一 arm）。</para>
    /// </summary>
    /// <param name="pattern">模式 AST。</param>
    /// <param name="subject">待匹配的主体值。</param>
    /// <param name="bound">输出绑定变量字典（仅用于 AsPattern 顶层收集，内部递归直接写 Variables）。</param>
    private bool MatchPattern(PatternAst pattern, object? subject, out Dictionary<string, object?>? bound)
    {
        bound = null;
        switch (pattern)
        {
            // §1 通配模式 `_`：永远匹配。
            case WildcardPattern:
                return true;

            // §1 字面量模式：求值表达式后与主体相等比较。
            case LiteralPattern lit:
                var litVal = EvaluateExpression(lit.Value).Value;
                return Equals(subject, litVal);

            // §1 类型模式：subject isinstance Type。
            case TypePattern tp:
                var type = ResolveType(tp.Type);
                if (type is null) return false;
                return subject is not null && type.IsAssignableFrom(subject.GetType());

            // §2 解构模式：hash { name, ... } / array [a, b, ...rest]。
            case DestructurePattern dp:
                return MatchDestructure(dp, subject);

            // §3 范围模式：1..=10 / 1..<10。
            case RangePattern rp:
                return MatchRange(rp, subject);

            // §4 守卫模式：内模式匹配 + 守卫表达式为真。
            case GuardPattern gp:
                if (!MatchPattern(gp.Inner, subject, out _)) return false;
                _ctx.Variables?.Set("_", subject!);
                return IsTruthy(EvaluateExpression(gp.Condition).Value);

            // §5 OR 模式：任一分支匹配即成功。
            case OrPattern op:
                foreach (var alt in op.Alternatives)
                {
                    if (MatchPattern(alt, subject, out _)) return true;
                }
                return false;

            // §6 绑定模式：内模式匹配后把 subject 绑定到命名变量。
            case AsPattern ap:
                if (!MatchPattern(ap.Inner, subject, out _)) return false;
                _ctx.Variables?.Set(ap.BindName, subject!);
                bound = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [ap.BindName] = subject,
                };
                return true;

            default:
                return false;
        }
    }

    /// <summary>ADR-0055 §2: hash { name, age } / array [a, b, ...rest] 解构匹配。</summary>
    private bool MatchDestructure(DestructurePattern dp, object? subject)
    {
        if (dp.Kind == DestructureKind.Hash)
        {
            // hash 解构：subject 必须是 IDictionary（string 键）。
            if (subject is not IDictionary dict) return false;
            foreach (var field in dp.Fields)
            {
                if (!dict.Contains(field.Name)) return false;
                _ctx.Variables?.Set(field.Name, dict[field.Name]!);
            }
            // ...rest: 把剩余键值收集到新 hashtable 绑定到 rest 名。
            if (dp.Rest is not null)
            {
                var rest = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var bound = new HashSet<string>(dp.Fields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry kv in dict)
                {
                    var key = kv.Key?.ToString() ?? "";
                    if (!bound.Contains(key)) rest[key] = kv.Value;
                }
                _ctx.Variables?.Set(dp.Rest, rest!);
            }
            return true;
        }

        // array 解构：subject 必须是 IList 或 IEnumerable（按顺序取元素）。
        if (subject is null) return false;
        var items = new List<object?>();
        if (subject is IList list) { foreach (object? x in list) items.Add(x); }
        else if (subject is IEnumerable e and not string) { foreach (object? x in e) items.Add(x); }
        else return false;

        for (int i = 0; i < dp.Fields.Count; i++)
        {
            if (i >= items.Count) return false;
            _ctx.Variables?.Set(dp.Fields[i].Name, items[i]!);
        }
        // ...rest: 剩余元素绑定为数组。
        if (dp.Rest is not null)
        {
            var restItems = new List<object?>();
            for (int i = dp.Fields.Count; i < items.Count; i++) restItems.Add(items[i]);
            _ctx.Variables?.Set(dp.Rest, restItems.ToArray()!);
        }
        return true;
    }

    /// <summary>ADR-0055 §3: 范围模式 1..=10（含）/ 1..&lt;10（不含）。仅对数值主体生效。</summary>
    private bool MatchRange(RangePattern rp, object? subject)
    {
        if (subject is null) return false;
        var start = EvaluateExpression(rp.Start).Value;
        var end = EvaluateExpression(rp.End).Value;
        if (!IsNumeric(subject) || !IsNumeric(start) || !IsNumeric(end)) return false;
        var sv = Convert.ToDouble(subject, CultureInfo.InvariantCulture);
        var stv = Convert.ToDouble(start, CultureInfo.InvariantCulture);
        var ev = Convert.ToDouble(end, CultureInfo.InvariantCulture);
        return rp.Inclusive
            ? sv >= stv && sv <= ev
            : sv >= stv && sv < ev;
    }

    // =========================================================================
    // ADR-0051: async / await 求值
    // =========================================================================

    /// <summary>
    /// ADR-0051 §1: `async fn name() { }` —— 注册带 IsAsync=true 标记的 ScriptBlock 到变量表。
    /// <para>调用时（InvokeCommand / ResolveScriptBlockCommand）若 ScriptBlock.IsAsync 为 true，
    /// 返回 Task&lt;object?&gt;，体部延迟到 await 时执行。</para>
    /// </summary>
    private ExecutionResult EvaluateAsyncFunctionDeclaration(AsyncFunctionDeclarationAst afn)
    {
        var sb = new ScriptBlock(afn.Body, _ctx) { IsAsync = true };
        _ctx.Variables?.Set(afn.Name, sb);
        return ExecutionResult.Empty;
    }

    /// <summary>
    /// ADR-0051 §2: `await expr` —— 同步解包 Task / ValueTask / IAsyncEnumerable。
    /// <para>shell 是单线程同步上下文，await 在此模型下等价于 .GetAwaiter().GetResult()。</para>
    /// </summary>
    private ExecutionResult EvaluateAwait(AwaitExpressionAst aw)
    {
        var operand = EvaluateExpression(aw.Operand).Value;
        return ExecutionResult.Of(UnwrapAwaitable(operand));
    }

    /// <summary>
    /// ADR-0051 §3: `async { ... }` —— 求值时返回 Task&lt;object?&gt;，体部延迟到 await 时执行。
    /// <para>实现：Task.Run 内构造子 Evaluator 执行 Statements，返回最后表达式的值。</para>
    /// </summary>
    private ExecutionResult EvaluateAsyncBlock(AsyncBlockExpression ab)
    {
        var capturedCtx = _ctx;
        var statements = ab.Statements;
        var task = Task.Run<object?>(() =>
        {
            var evaluator = new Evaluator(capturedCtx);
            var scriptAst = new ScriptBlockAst(statements, Array.Empty<ParameterDeclaration>(), ab.Span);
            var result = evaluator.Execute(scriptAst);
            if (result.Signal == FlowSignalKind.Throw)
                throw new OpenShellScriptException(result.ThrownValue, capturedCtx);
            return result.Value;
        }, capturedCtx.CancellationToken);
        return ExecutionResult.Of(task);
    }

    /// <summary>
    /// ADR-0051 §1: 调用 IsAsync=true 的 ScriptBlock，返回 Task&lt;object?&gt;。
    /// <para>实现：在 Task.Run 内同步调用 InvokeWithNamedArgs，把返回值包装为 Task。</para>
    /// <para>shell 单线程同步模型下，async 函数体在 await 时才实际执行（lazy 语义）。</para>
    /// </summary>
    private object? InvokeAsyncScriptBlock(
        ScriptBlock sb, ExecutionContext callerCtx,
        IReadOnlyDictionary<string, object?>? namedArgs, object?[] args)
    {
        var capturedSb = sb;
        var capturedCtx = callerCtx;
        var capturedNamed = namedArgs;
        var capturedArgs = args;
        return Task.Run<object?>(() =>
        {
            var value = capturedSb.InvokeWithNamedArgs(capturedCtx, capturedNamed, capturedArgs);
            return value;
        }, callerCtx.CancellationToken);
    }

    /// <summary>
    /// 同步解包 awaitable 值。Per ADR-0051 §2.
    /// <list type="bullet">
    /// <item>Task: 等待完成，取 Result 属性；Task&lt;void&gt; 返回 null。</item>
    /// <item>ValueTask: 等待完成（无 Result 时返回 null）。</item>
    /// <item>IAsyncEnumerable&lt;IItem&gt;: 同步收集到 List。</item>
    /// <item>其他: 原样返回。</item>
    /// </list>
    /// </summary>
    internal object? UnwrapAwaitable(object? value)
    {
        if (value is null) return null;

        if (value is Task task)
        {
            BlockSafe(() => task.GetAwaiter().GetResult());
            var resultProp = task.GetType().GetProperty("Result");
            if (resultProp is null) return null;
            var taskResult = resultProp.GetValue(task);
            return UnwrapAwaitable(taskResult);
        }

        if (value is ValueTask vt)
        {
            BlockSafe(() => vt.GetAwaiter().GetResult());
            return null;
        }

        if (value is IAsyncEnumerable<IItem> stream)
        {
            return BlockSafe(() => DrainStream(stream));
        }

        return value;
    }

    /// <summary>同步收集异步流到列表 (调用方可能处于任意线程)。</summary>
    private List<IItem> DrainStream(IAsyncEnumerable<IItem> stream)
    {
        var list = new List<IItem>();
        var e = stream.GetAsyncEnumerator(_ctx.CancellationToken);
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                list.Add(e.Current);
        }
        finally
        {
            e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return list;
    }

    /// <summary>
    /// IH-008: 安全的同步等待桥。无 <see cref="SynchronizationContext"/> 的线程 (CLI REPL) 直接等待,
    /// 零开销; 带上下文的线程 (GUI / UI 线程) 把等待整体搬到线程池执行, 使异步延续不再捕获被阻塞的
    /// 上下文, 消除 "UI 线程同步等待 → 延续排队回 UI 线程 → 死锁" 的闭环。
    /// </summary>
    private static void BlockSafe(Action work)
    {
        if (SynchronizationContext.Current is null)
        {
            work();
            return;
        }
        Task.Run(work).GetAwaiter().GetResult();
    }

    /// <summary>IH-008: <see cref="BlockSafe(Action)"/> 的带返回值重载。</summary>
    private static T BlockSafe<T>(Func<T> work)
    {
        if (SynchronizationContext.Current is null)
            return work();
        return Task.Run(work).GetAwaiter().GetResult();
    }

    // =========================================================================
    // ADR-0056: ESM 模块 export / import 求值
    // =========================================================================

    /// <summary>
    /// ADR-0056 §1: `export fn name() { }` / `export const NAME = value` / `export default expr`。
    /// <para>实现：先求值内部声明（注册到当前作用域），再把导出实体登记到当前模块的 ModuleRegistry。</para>
    /// <para>当前模块路径通过 _ctx.CurrentModulePath 获取（由 import 求值时 push 到上下文）。
    /// 若 ModuleRegistry 未注册或不在模块上下文，退化为普通声明（仅作用于当前作用域）。</para>
    /// </summary>
    private ExecutionResult EvaluateExportDeclaration(ExportDeclarationAst exp)
    {
        // 1. 先求值内部声明，把实体注入当前作用域。
        var innerResult = EvaluateStatement(exp.Inner);

        // 2. 查询当前模块注册表。不在模块上下文时仅做普通声明，不登记导出。
        var registry = ResolveModuleRegistry();
        var modulePath = _ctx.CurrentModulePath;
        if (registry is null || string.IsNullOrEmpty(modulePath))
            return innerResult;

        // 取出或创建当前模块对象（builder 模式：构造新 ModuleObject 替换缓存项）。
        registry.TryGet(modulePath, out var existing);
        var funcs = new Dictionary<string, object?>(
            existing?.ExportedFunctions ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var consts = new Dictionary<string, object?>(
            existing?.ExportedConstants ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        object? defaultExport = existing?.DefaultExport;

        switch (exp.Kind)
        {
            case ExportKind.Function:
                if (exp.Name is not null && _ctx.Variables is not null)
                {
                    var fnValue = _ctx.Variables.Resolve(exp.Name);
                    funcs[exp.Name] = fnValue;
                }
                break;
            case ExportKind.Constant:
                if (exp.Name is not null && _ctx.Variables is not null)
                {
                    var constValue = _ctx.Variables.Resolve(exp.Name);
                    consts[exp.Name] = constValue;
                }
                break;
            case ExportKind.Default:
                defaultExport = innerResult.Value;
                break;
        }

        var moduleName = System.IO.Path.GetFileNameWithoutExtension(modulePath);
        var updated = new ModuleObject
        {
            Name = existing?.Name ?? moduleName,
            FilePath = modulePath,
            ExportedFunctions = funcs,
            ExportedConstants = consts,
            DefaultExport = defaultExport,
            LoadedAt = existing?.LoadedAt ?? DateTimeOffset.UtcNow,
        };
        registry.Register(updated);

        return innerResult;
    }

    /// <summary>
    /// ADR-0056 §2: `import { fn1, fn2 } from "module"`。
    /// <para>首次加载触发解析+求值并缓存到 ModuleRegistry；后续命中缓存。
    /// 命名导入把 ExportedFunctions / ExportedConstants 中的指定名字注入当前作用域。</para>
    /// </summary>
    private ExecutionResult EvaluateNamedImport(NamedImportAst ni)
    {
        var module = LoadModule(ni.ModulePath);
        if (module is null) return ExecutionResult.Empty;

        foreach (var name in ni.Names)
        {
            if (module.ExportedFunctions.TryGetValue(name, out var fn))
                _ctx.Variables?.Set(name, fn!);
            else if (module.ExportedConstants.TryGetValue(name, out var c))
                _ctx.Variables?.Set(name, c!);
            else
            {
                _ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.ItemNotFound,
                    Message = $"import: module '{ni.ModulePath}' has no export named '{name}'",
                    Operation = "import",
                    Phase = ErrorPhase.Operation,
                });
            }
        }
        return ExecutionResult.Empty;
    }

    /// <summary>
    /// ADR-0056 §2: `import * as NS from "module"`。
    /// <para>把模块的所有导出打包成 hashtable，绑定到 NS 变量。</para>
    /// </summary>
    private ExecutionResult EvaluateNamespaceImport(NamespaceImportAst nsi)
    {
        var module = LoadModule(nsi.ModulePath);
        if (module is null) return ExecutionResult.Empty;

        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in module.ExportedFunctions) bag[kvp.Key] = kvp.Value;
        foreach (var kvp in module.ExportedConstants) bag[kvp.Key] = kvp.Value;
        if (module.DefaultExport is not null)
            bag["default"] = module.DefaultExport;

        _ctx.Variables?.Set(nsi.Namespace, bag!);
        return ExecutionResult.Empty;
    }

    /// <summary>
    /// ADR-0056 §3: 加载脚本模块。命中缓存返回缓存对象；否则解析+求值后注册。
    /// </summary>
    private ModuleObject? LoadModule(string modulePath)
    {
        var registry = ResolveModuleRegistry();
        if (registry is null)
        {
            _ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ConfigurationError,
                Message = "import: ModuleRegistry is not registered in the host DI container.",
                Operation = "import",
                Phase = ErrorPhase.Operation,
            });
            return null;
        }

        // 解析为绝对路径（相对 CurrentModulePath 所在目录或当前工作目录）。
        string absPath;
        try
        {
            absPath = ResolveScriptPath(modulePath);
            absPath = System.IO.Path.GetFullPath(absPath);
        }
        catch (Exception)
        {
            _ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"import: invalid module path '{modulePath}'",
                Operation = "import",
                Phase = ErrorPhase.ArgumentBinding,
            });
            return null;
        }

        if (!System.IO.File.Exists(absPath))
        {
            _ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"import: module file not found: {absPath}",
                Operation = "import",
                Phase = ErrorPhase.Operation,
            });
            return null;
        }

        // 命中缓存直接返回。
        if (registry.TryGet(absPath, out var cached)) return cached;

        // 解析模块文件。按后缀选择 parser（per ADR-0050 §10.1）。
        var source = System.IO.File.ReadAllText(absPath);
        ScriptBlockAst ast;
        try
        {
            var ext = System.IO.Path.GetExtension(absPath);
            ast = string.Equals(ext, ".osh", StringComparison.OrdinalIgnoreCase)
                ? OpenShell.Parsing.ModernParser.Parse(source, absPath)
                : PowerShellParser.Parse(source, absPath);
        }
        catch (ParserException ex)
        {
            _ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ParseError,
                Message = $"import {absPath}: parse error at line {ex.Span.Start.Line}, col {ex.Span.Start.Column}: {ex.Message}",
                Operation = "import",
                Phase = ErrorPhase.Parse,
            });
            return null;
        }

        // 在模块上下文（CurrentModulePath 设置）中求值，以便 export 声明登记到此模块。
        // 复用当前 ExecutionContext 的变量/命令注册表，但替换 CurrentModulePath。
        var savedModulePath = _ctx.CurrentModulePath;
        _ctx.CurrentModulePath = absPath;
        // 预注册空模块对象，让 export 求值时 TryGet 命中并增量更新。
        var placeholder = new ModuleObject
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(absPath),
            FilePath = absPath,
        };
        registry.Register(placeholder);
        try
        {
            var moduleEvaluator = new Evaluator(_ctx);
            moduleEvaluator.Execute(ast);
        }
        catch (OpenShellScriptException ex)
        {
            _ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"import {absPath}: {ex.Message}",
                Operation = "import",
                Phase = ErrorPhase.Operation,
            });
        }
        finally
        {
            _ctx.CurrentModulePath = savedModulePath;
        }

        registry.TryGet(absPath, out var loaded);
        return loaded;
    }

    /// <summary>
    /// 从 Host DI 容器解析 ModuleRegistry。未注册返回 null（退化为普通声明语义）。
    /// </summary>
    private ModuleRegistry? ResolveModuleRegistry()
        => _ctx.Host?.Services is null
            ? null
            : (ModuleRegistry?)_ctx.Host.Services.GetService(typeof(ModuleRegistry));

    /// <summary>
    /// ADR-0050 §10.1: 解析脚本路径。
    /// 绝对路径直接返回；相对路径优先相对于 <see cref="ExecutionContext.CurrentModulePath"/> 所在目录解析，
    /// 回退到当前工作目录。Per T-206 修复（支持文件内 import 相对路径）。
    /// </summary>
    private string ResolveScriptPath(string rawPath)
    {
        var path = rawPath.Trim('"', '\'');
        if (System.IO.Path.IsPathRooted(path))
            return path;
        // 相对路径：相对于当前正在求值的脚本/模块文件所在目录。
        if (!string.IsNullOrEmpty(_ctx.CurrentModulePath))
        {
            var dir = System.IO.Path.GetDirectoryName(_ctx.CurrentModulePath);
            if (!string.IsNullOrEmpty(dir))
            {
                var resolved = System.IO.Path.Combine(dir, path);
                if (System.IO.File.Exists(resolved))
                    return resolved;
            }
        }
        return path; // 回退到原始相对路径（由调用方 File.Exists 判断）
    }

    // =========================================================================
    // 命令调用
    // =========================================================================

    private object? InvokeCommand(CommandExpression cmd)
    {
        // 表达式头：__expr__ 命令包装一个表达式作为管道源（如 1..5 | ...）。Per ADR-0010.
        if (cmd.HeadExpression is not null)
        {
            return EvaluateExpression(cmd.HeadExpression).Value;
        }

        // & { ... }：直接调用脚本块字面量。Per ADR-0046 §2.
        if (cmd.Kind == CommandInvocationKind.CallOperator && cmd.Block is not null)
        {
            var sb = new ScriptBlock(cmd.Block, _ctx);
            var args = cmd.Arguments
                .OfType<PositionalArgument>()
                .Select(a => EvaluateExpression(a.Value).Value)
                .ToArray();
            var named = ExtractNamedArguments(cmd.Arguments);
            // ADR-0051 §1: async { } / async fn 调用返回 Task<object?>。
            if (sb.IsAsync)
                return InvokeAsyncScriptBlock(sb, _ctx, named, args);
            return sb.InvokeWithNamedArgs(_ctx, named, args);
        }

        // D-320/D-323: AST 路径别名解析。字符串快路径已由 AliasExpander 展开，
        // 但 AST 路径（多语句 / -File 脚本）中各语句的命令名需在 Evaluator 层解析别名。
        // D-323: 别名优先于命令注册表——mkdir/touch 同时注册为 [Verb] 别名和 AliasRegistry 条目，
        // 若先查命令注册表则 desc 非空，永远不会走到别名展开，导致 -type:directory 默认参数丢失。
        // 但用户定义函数（ResolveFunction）优先于别名，与字符串快路径 AliasExpander 行为一致。
        CommandDescriptor? desc = null;
        List<CommandArgument>? aliasDefaultArgs = null;

        if (_ctx.Aliases is not null && _ctx.Aliases.ResolveFunction(cmd.Name) is null)
        {
            var alias = _ctx.Aliases.Resolve(cmd.Name);
            if (alias is not null)
            {
                // 解析别名展开字符串（如 "New-Item -type:directory"）。
                var parts = alias.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    desc = _ctx.Commands?.Resolve(parts[0]);
                    if (desc is not null && parts.Length > 1)
                    {
                        aliasDefaultArgs = new List<CommandArgument>();
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var p = parts[i];
                            if (p.StartsWith("-") && p.Contains(':'))
                            {
                                var colonIdx = p.IndexOf(':');
                                var name = p[1..colonIdx];
                                var value = p[(colonIdx + 1)..];
                                aliasDefaultArgs.Add(new NamedArgument(
                                    name,
                                    new LiteralExpression(value, LiteralKind.String, cmd.Span),
                                    cmd.Span));
                            }
                            else if (p.StartsWith("-"))
                            {
                                aliasDefaultArgs.Add(new SwitchArgument(p[1..], cmd.Span));
                            }
                        }
                    }
                }
            }
        }

        // 别名未命中：直接查命令注册表
        if (desc is null)
        {
            desc = _ctx.Commands?.Resolve(cmd.Name);
        }

        if (desc is null)
        {
            // 命令未注册：查 Variables 是否是 ScriptBlock（用户定义函数）
            if (_ctx.Variables is not null)
            {
                var v = _ctx.Variables.Resolve(cmd.Name);
                if (v is ScriptBlock sb)
                {
                    var args = cmd.Arguments
                        .OfType<PositionalArgument>()
                        .Select(a => EvaluateExpression(a.Value).Value)
                        .ToArray();
                    // 提取命名参数（含 -WhatIf / -Confirm 等通用参数）。Per ADR-0049 §2.
                    var named = ExtractNamedArguments(cmd.Arguments);
                    // ADR-0051 §1: async fn 调用返回 Task<object?>，体延迟到 await 时执行。
                    if (sb.IsAsync)
                        return InvokeAsyncScriptBlock(sb, _ctx, named, args);
                    return sb.InvokeWithNamedArgs(_ctx, named, args);
                }
            }
            _ctx.WriteError(ErrorRecord.FromException(
                new CommandNotFoundException(cmd.Name),
                phase: ErrorPhase.Operation));
            return null;
        }

        var instance = Activator.CreateInstance(desc.CommandType);
        if (instance is null) return null;

        // D-320: 合并别名默认参数与命令实际参数（别名默认参数在前，用户参数可覆盖）。
        var effectiveArgs = aliasDefaultArgs is not null
            ? new List<CommandArgument>(aliasDefaultArgs.Concat(cmd.Arguments))
            : cmd.Arguments;
        var argsInstance = BuildArgs(desc, effectiveArgs);
        var ctx = BuildCommandContext();
        try
        {
            var method = desc.CommandType.GetMethod("ExecuteAsync");
            if (method is null) return null;
            var result = method.Invoke(instance, new[] { argsInstance, ctx, _ctx.CancellationToken });
            return ConsumeCommandResult(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (TargetInvocationException tie)
        {
            _ctx.WriteError(ErrorRecord.FromException(tie.InnerException ?? tie, phase: ErrorPhase.Operation));
            return null;
        }
        catch (Exception ex)
        {
            _ctx.WriteError(ErrorRecord.FromException(ex, phase: ErrorPhase.Operation));
            return null;
        }
    }

    /// <summary>
    /// 从命令参数列表提取命名参数（含 -WhatIf / -Confirm 等通用参数）。Per ADR-0049 §2.
    /// 用于脚本函数调用：把命名参数传递给 ScriptBlock.InvokeWithNamedArgs。
    /// </summary>
    private Dictionary<string, object?> ExtractNamedArguments(IReadOnlyList<CommandArgument> arguments)
    {
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in arguments)
        {
            switch (arg)
            {
                case NamedArgument na:
                    named[na.Name] = EvaluateExpression(na.Value).Value;
                    break;
                case SwitchArgument sa:
                    named[sa.Name] = true;
                    break;
            }
        }
        return named;
    }

    /// <summary>处理命令返回值：IAsyncEnumerable 流推送到 Host，Task 等待，scalar 原样返回。</summary>
    private object? ConsumeCommandResult(object? result)
    {
        if (result is null) return null;

        // IAsyncEnumerable<IItem>：流式输出推送到 Host
        if (result is IAsyncEnumerable<IItem> stream)
        {
            if (_ctx.Host is not null)
            {
                // IH-008: 经 BlockSafe 等待, 带同步上下文 (GUI) 时搬到线程池, 避免与延续互锁。
                BlockSafe(() => _ctx.Host.WriteItemsAsync(stream, _ctx.CancellationToken).GetAwaiter().GetResult());
                return null;
            }
            // 无 Host：同步收集到列表
            return BlockSafe(() => DrainStream(stream));
        }

        // Task<T> / Task：等待并解包 Result
        if (result is Task task)
        {
            BlockSafe(() => task.GetAwaiter().GetResult());
            var resultProp = task.GetType().GetProperty("Result");
            if (resultProp is not null)
            {
                var taskResult = resultProp.GetValue(task);
                return ConsumeCommandResult(taskResult);
            }
            return null;
        }

        // ValueTask：等待
        if (result is ValueTask vt)
        {
            BlockSafe(() => vt.GetAwaiter().GetResult());
            return null;
        }

        // scalar 值（IItem / int / string 等）
        return result;
    }

    /// <summary>构造命令 Args：评估所有 CommandArgument 并反射绑定到 Args record。per ADR-0045 §15.</summary>
    private object BuildArgs(CommandDescriptor desc, IReadOnlyList<CommandArgument> arguments)
    {
        var positional = new List<object?>();
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var parametersByName = new Dictionary<string, ParameterDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in desc.Parameters)
        {
            parametersByName[parameter.Name] = parameter;
            foreach (var alias in parameter.Aliases)
                parametersByName[alias.TrimStart('-')] = parameter;
        }

        foreach (var arg in arguments)
        {
            switch (arg)
            {
                case PositionalArgument pa:
                    positional.Add(EvaluateExpression(pa.Value).Value);
                    break;
                case NamedArgument na:
                    if (!parametersByName.TryGetValue(na.Name, out var namedParameter))
                        throw new CommandArgumentException(
                            $"Unknown parameter '-{na.Name}' for command '{desc.FullName}'.");
                    if (named.ContainsKey(namedParameter.Name))
                        throw new CommandArgumentException(
                            $"Parameter '-{namedParameter.Name}' was specified more than once for command '{desc.FullName}'.");
                    // Parser 对 -Name value 形式统一产生 NamedArgument。
                    // 若目标参数是 bool（switch），则值应回流到位置参数（PowerShell 语义）。
                    if (namedParameter.Type == typeof(bool))
                    {
                        named[namedParameter.Name] = true;
                        positional.Add(EvaluateExpression(na.Value).Value);
                    }
                    else
                    {
                        named[namedParameter.Name] = EvaluateExpression(na.Value).Value;
                    }
                    break;
                case SwitchArgument sa:
                    if (!parametersByName.TryGetValue(sa.Name, out var switchParameter))
                        throw new CommandArgumentException(
                            $"Unknown parameter '-{sa.Name}' for command '{desc.FullName}'.");
                    if (named.ContainsKey(switchParameter.Name))
                        throw new CommandArgumentException(
                            $"Parameter '-{switchParameter.Name}' was specified more than once for command '{desc.FullName}'.");
                    named[switchParameter.Name] = true;
                    break;
                case ScriptBlockArgument sba:
                    // 脚本块作为位置参数（cmd { }）：包装为 ScriptBlock 对象
                    positional.Add(new ScriptBlock(sba.Block, _ctx));
                    break;
            }
        }

        var constructor = desc.ArgsType.GetConstructors().First();
        var parameters = constructor.GetParameters();
        var argsValues = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var pdesc = desc.Parameters.FirstOrDefault(p2 =>
                string.Equals(p2.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (pdesc is null)
            {
                argsValues[i] = p.HasDefaultValue ? p.DefaultValue : null;
                continue;
            }

            var paramAttr = pdesc.ParameterAttribute;
            object? value = null;
            bool matched = false;

            // 位置参数：类型感知绑定（ScriptBlock 参数只接受 ScriptBlock 值，string 参数只接受 string 值）
            if (paramAttr?.Position >= 0 && paramAttr.Position < positional.Count)
            {
                var candidate = positional[paramAttr.Position];
                if (IsTypeCompatible(candidate, p.ParameterType))
                {
                    value = candidate;
                    matched = true;
                }
                else
                {
                    // D-309: 类型不兼容时尝试转换（如 string → ItemPath）。
                    // IsTypeCompatible 不覆盖 string→ItemPath 等转换，但 ConvertValue 能处理。
                    // 之前此处跳过导致 cd .. 的 .. 参数丢失（Path 为 null → cd 导航到 provider 根 fs::/）。
                    try
                    {
                        var converted = ConvertValue(candidate, p.ParameterType);
                        if (converted is not null)
                        {
                            value = converted;
                            matched = true;
                        }
                    }
                    catch { /* 转换失败，保持 matched = false */ }
                }
            }
            // 如果 Position 上没有类型匹配的值，尝试后续位置参数
            if (!matched && paramAttr?.Position >= 0)
            {
                for (int j = paramAttr.Position + 1; j < positional.Count; j++)
                {
                    if (IsTypeCompatible(positional[j], p.ParameterType))
                    {
                        value = positional[j];
                        matched = true;
                        break;
                    }
                }
            }
            // 命名参数（按名）
            if (!matched && named.TryGetValue(p.Name!, out var nValue))
            {
                value = nValue;
                matched = true;
            }
            // 命名参数（按别名）
            if (!matched)
            {
                foreach (var alias in paramAttr?.Aliases ?? Array.Empty<string>())
                {
                    if (named.TryGetValue(alias.TrimStart('-'), out var aValue))
                    {
                        value = aValue;
                        matched = true;
                        break;
                    }
                }
            }
            // 默认值
            if (!matched)
            {
                if (pdesc.Mandatory || !p.HasDefaultValue)
                    throw new CommandArgumentException(
                        $"Parameter '-{pdesc.Name}' is required for command '{desc.FullName}'.");
                value = p.DefaultValue;
            }

            try
            {
                argsValues[i] = ConvertValue(value, p.ParameterType);
            }
            catch (Exception ex) when (ex is not CommandArgumentException)
            {
                throw new CommandArgumentException(
                    $"Invalid value for parameter '-{pdesc.Name}' on command '{desc.FullName}': {ex.Message}", ex);
            }
        }

        return constructor.Invoke(argsValues)
            ?? throw new InvalidOperationException("Args constructor returned null.");
    }

    private CommandContext BuildCommandContext()
    {
        return new CommandContext
        {
            Commands = _ctx.Commands!,
            Providers = _ctx.Providers!,
            Host = _ctx.Host!,
            CurrentLocation = _ctx.Host?.CurrentLocation ?? Paths.ItemPath.Root("fs"),
            CancellationToken = _ctx.CancellationToken,
            Errors = _ctx.Errors,
            Variables = _ctx.Variables,
            // D-308: AST path 之前缺失 Operations/Aliases/Help/Drives，导致 mkdir/rm/cp/mv 等命令失败。
            Operations = _ctx.Operations,
            Aliases = _ctx.Aliases,
            Help = _ctx.Help,
            Drives = _ctx.Drives,
        };
    }

    // =========================================================================
    // 辅助：类型转换 / 成员访问 / 算术 / 比较
    // =========================================================================

    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length > 0,
        ICollection c => c.Count > 0,
        _ => true,
    };

    private static IEnumerable Enumerate(object? iterable)
    {
        if (iterable is null) yield break;
        if (iterable is IEnumerable e && iterable is not string)
        {
            foreach (var item in e) yield return item;
            yield break;
        }
        yield return iterable;
    }

    public static object? ItemToValue(IItem item)
    {
        var v = item.Properties["Value"];
        return v ?? item.Name;
    }

    // ADR-0058 §6: 公开 ItemToValue 供 ExpressionCompiler 复用 ($_ / $PSItem 转换)。
    public static object? ItemToValuePublic(IItem item) => ItemToValue(item);

    public static object? Add(object? a, object? b)
    {
        if (a is string sa) return sa + (b?.ToString() ?? "");
        if (a is double ad && b is double bd) return ad + bd;
        if (a is long al && b is long bl) return al + bl;
        if (a is int ai && b is int bi) return ai + bi;
        if (TryConvertBoth(a, b, out double da, out double db)) return da + db;
        return null;
    }

    public static object? Subtract(object? a, object? b)
    {
        if (a is double ad && b is double bd) return ad - bd;
        if (a is long al && b is long bl) return al - bl;
        if (a is int ai && b is int bi) return ai - bi;
        if (TryConvertBoth(a, b, out double da, out double db)) return da - db;
        return null;
    }

    public static object? Multiply(object? a, object? b)
    {
        if (a is double ad && b is double bd) return ad * bd;
        if (a is long al && b is long bl) return al * bl;
        if (a is int ai && b is int bi) return ai * bi;
        if (TryConvertBoth(a, b, out double da, out double db)) return da * db;
        return null;
    }

    public static object? Divide(object? a, object? b)
    {
        if (a is double ad && b is double bd) return ad / bd;
        if (a is long al && b is long bl) return al / bl;
        if (a is int ai && b is int bi) return ai / bi;
        if (TryConvertBoth(a, b, out double da, out double db)) return da / db;
        return null;
    }

    public static object? Modulo(object? a, object? b)
    {
        if (a is long al && b is long bl) return al % bl;
        if (a is int ai && b is int bi) return ai % bi;
        if (TryConvertBoth(a, b, out double da, out double db)) return da % db;
        return null;
    }

    public static object? Power(object? a, object? b)
    {
        if (TryConvertBoth(a, b, out double da, out double db)) return Math.Pow(da, db);
        return null;
    }

    private static bool TryConvertBoth(object? a, object? b, out double da, out double db)
    {
        da = 0; db = 0;
        try
        {
            da = Convert.ToDouble(a, CultureInfo.InvariantCulture);
            db = Convert.ToDouble(b, CultureInfo.InvariantCulture);
            return true;
        }
        catch { return false; }
    }

    private static int Compare(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        // 数值统一转 double 比较，避免 Int64.CompareTo(int) 等抛 ArgumentException
        if (IsNumeric(a) && IsNumeric(b))
        {
            var da = Convert.ToDouble(a, CultureInfo.InvariantCulture);
            var db = Convert.ToDouble(b, CultureInfo.InvariantCulture);
            return da.CompareTo(db);
        }
        if (a is IComparable c)
        {
            // 类型不匹配时回退到字符串比较，避免 CompareTo 抛 ArgumentException
            if (!a.GetType().IsAssignableFrom(b.GetType()) &&
                !b.GetType().IsAssignableFrom(a.GetType()))
                return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
            return c.CompareTo(b);
        }
        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    // ADR-0058 §6: 公开为 ExpressionCompiler 提供二元运算复用入口, 保证编译与解释语义一致。
    public static int CompareValues(object? a, object? b) => Compare(a, b);

    /// <summary>
    /// ADR-0057 §4: 将 op_Compare 返回的 int 值转换为比较运算符的 bool 结果。
    /// Lt → &lt;0, Gt → &gt;0, Le → &lt;=0, Ge → &gt;=0.
    /// </summary>
    private static bool OpCompareToBool(BinaryOperator op, object? result)
    {
        if (result is bool b) return b;
        var cmp = result switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            _ => 0,
        };
        return op switch
        {
            BinaryOperator.Lt => cmp < 0,
            BinaryOperator.Gt => cmp > 0,
            BinaryOperator.Le => cmp <= 0,
            BinaryOperator.Ge => cmp >= 0,
            _ => false,
        };
    }

    // ADR-0058 §6: 公开 LikeMatch 供 ExpressionCompiler 复用。
    public static bool LikeMatch(object? value, object? pattern, bool caseSensitive)
    {
        if (value is null || pattern is null) return false;
        var v = value.ToString()!;
        var p = pattern.ToString()!;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(p)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(v, regex,
            caseSensitive ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsNumeric(object? v) =>
        v is sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal;

    private bool RegexMatch(object? value, object? pattern, bool caseSensitive)
    {
        if (value is null || pattern is null)
        {
            // 匹配失败：清空 $matches。Per ADR-0042 §3.5.
            _ctx.Variables?.Set("matches", null!);
            return false;
        }
        var options = caseSensitive
            ? System.Text.RegularExpressions.RegexOptions.None
            : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        var m = System.Text.RegularExpressions.Regex.Match(value.ToString()!, pattern.ToString()!, options);
        if (!m.Success)
        {
            _ctx.Variables?.Set("matches", null!);
            return false;
        }

        // 匹配成功：填充 $matches hashtable。Per ADR-0042 §3.5.
        // 键 "0" 为整体匹配，"1"/"2"/... 为分组。
        var hash = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = m.Value,
        };
        for (int i = 1; i < m.Groups.Count; i++)
        {
            var g = m.Groups[i];
            hash[i.ToString()] = g.Success ? g.Value : null;
        }
        _ctx.Variables?.Set("matches", hash);
        return true;
    }

    private static bool InMatch(object? value, object? collection)
    {
        if (collection is IEnumerable e)
        {
            foreach (var item in e)
                if (Equals(value, item)) return true;
        }
        return false;
    }

    // ADR-0058 §6: 公开以下辅助方法供 ExpressionCompiler 复用。
    public static bool InMatchPublic(object? value, object? collection) => InMatch(value, collection);

    private static bool IsType(object? value, object? typeObj)
    {
        if (typeObj is Type t) return value is not null && t.IsAssignableFrom(value.GetType());
        return false;
    }

    /// <summary>
    /// ADR-0052 §4: `is` / `isnot` 运算符右侧为类型引用表达式时，直接从 AST 取 TypeReference，
    /// 调用 TypeCoercer.ParseTypeAnnotation 解析复合类型（int? / int|string / List&lt;int&gt;），
    /// 再用 MatchesTypeAnnotation 做不抛异常的匹配。
    /// </summary>
    private static bool IsType(object? value, Expression typeExpr)
    {
        if (typeExpr is TypeReferenceExpression tre)
        {
            var annotation = TypeCoercer.ParseTypeAnnotation(tre.Type.FullName);
            return annotation is not null && TypeCoercer.MatchesTypeAnnotation(value, annotation);
        }
        // 退化路径：右侧为普通表达式（如变量持有 System.Type），求值后走 object? 重载。
        return false;
    }

    public static bool IsTypePublic(object? value, object? typeObj) => IsType(value, typeObj);

    private static object? ConvertAs(object? value, object? typeObj)
    {
        if (typeObj is Type t) return ConvertValue(value, t);
        return value;
    }

    public static object? ConvertAsPublic(object? value, object? typeObj) => ConvertAs(value, typeObj);

    private static long ToLong(object? v) => v switch
    {
        long l => l,
        int i => i,
        bool b => b ? 1 : 0,
        double d => (long)d,
        _ => 0,
    };

    // ADR-0058 §6: 公开 ToLong 供 ExpressionCompiler 复用。
    public static long ToLongPublic(object? v) => ToLong(v);

    public static object? GetMember(object? target, string name)
    {
        if (target is null) return null;
        var type = target.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null) return prop.GetValue(target);
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field is not null) return field.GetValue(target);
        if (target is IItem item)
        {
            var v = item.Properties[name];
            if (v is not null) return v;
        }
        if (target is IDictionary dict && dict.Contains(name))
            return dict[name];
        return null;
    }

    public static void SetMember(object? target, string name, object? value)
    {
        if (target is null) return;
        var type = target.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null && prop.CanWrite) { prop.SetValue(target, value); return; }
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field is not null) { field.SetValue(target, value); return; }
        if (target is IDictionary dict) dict[name] = value;
    }

    public static object? GetIndex(object? target, object? index)
    {
        if (target is null) return null;
        if (target is IDictionary dict)
        {
            // hashtable 键可能是 string（如 $matches["0"]），数字索引需转 string。
            if (dict.Contains(index!)) return dict[index!];
            var strIndex = index?.ToString();
            if (strIndex is not null && dict.Contains(strIndex)) return dict[strIndex];
            return null;
        }
        if (target is IList list && index is int i) return list[i];
        if (target is Array arr && index is int ai) return arr.GetValue(ai);
        if (target is string s && index is int si) return s[si].ToString();
        var type = target.GetType();
        var indexer = type.GetProperty("Item");
        if (indexer is not null) return indexer.GetValue(target, new[] { index });
        return null;
    }

    public static void SetIndex(object? target, object? index, object? value)
    {
        if (target is IDictionary dict) { dict[index!] = value; return; }
        if (target is IList list && index is int i) { list[i] = value; return; }
        if (target is Array arr && index is int ai) { arr.SetValue(value, ai); return; }
    }

    public static object? InvokeMethod(object? target, string name, object?[] args)
    {
        if (target is null) return null;
        var type = target.GetType();
        // 按名称+绑定标志获取所有方法（含重载），按参数数量筛选。
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (methods.Length == 0) return null;
        // 优先匹配参数数量相同的方法；若无精确匹配则取第一个（无参场景）。
        MethodInfo? method = methods.FirstOrDefault(m => m.GetParameters().Length == args.Length)
            ?? methods.FirstOrDefault();
        if (method is null) return null;
        return method.Invoke(target, args);
    }

    public static Type? ResolveType(TypeReference typeRef)
    {
        var name = typeRef.FullName;
        var candidates = new[]
        {
            name,
            "System." + name,
            "System.Collections.Generic." + name,
            "System.IO." + name,
        };
        foreach (var n in candidates)
        {
            var t = Type.GetType(n);
            if (t is not null) return t;
        }
        return null;
    }

    public static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null) return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        if (targetType == typeof(object)) return value;
        if (targetType.IsAssignableFrom(value.GetType())) return value;
        if (targetType == typeof(string)) return value.ToString();
        if (targetType.IsEnum)
        {
            if (value is string s) return Enum.Parse(targetType, s, ignoreCase: true);
            return Enum.ToObject(targetType, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
        if (targetType == typeof(bool)) return IsTruthy(value);
        // ItemPath 参数：字符串 → ItemPath.Parse（与 CliHost/PipelineExecutor 绑定器对齐）。
        var nullableUnderlying = Nullable.GetUnderlyingType(targetType);
        var itemPathTarget = nullableUnderlying ?? targetType;
        if (itemPathTarget == typeof(OpenShell.Paths.ItemPath) && value is string p)
            return OpenShell.Paths.ItemPath.Parse(p);
        if (targetType.IsArray)
        {
            var elemType = targetType.GetElementType()!;
            // D-628: string 实现 IEnumerable (IEnumerable<char>)，绑定到非 char 数组参数时
            // 若逐字符枚举会把 "hello" 拆成 5 项；按 PowerShell 语义应包成单元素数组。
            // 仅 char[] 目标保留按字符展开 ([char[]]"abc" → 'a','b','c')。
            if (value is string str && elemType != typeof(char))
            {
                var single = Array.CreateInstance(elemType, 1);
                single.SetValue(ConvertValue(str, elemType), 0);
                return single;
            }
            if (value is IEnumerable e)
            {
                var list = new List<object?>();
                foreach (var item in e) list.Add(ConvertValue(item, elemType));
                var arr = Array.CreateInstance(elemType, list.Count);
                for (int i = 0; i < list.Count; i++) arr.SetValue(list[i], i);
                return arr;
            }
        }
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    /// <summary>判断值是否与目标参数类型兼容（用于位置参数类型感知绑定）。</summary>
    private static bool IsTypeCompatible(object? value, Type targetType)
    {
        if (value is null) return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;
        if (targetType == typeof(object)) return true;
        if (targetType.IsAssignableFrom(value.GetType())) return true;
        // ScriptBlock 类型参数只接受 ScriptBlock 实例（不接受 string 等）
        if (targetType == typeof(ScriptBlock)) return value is ScriptBlock;
        // string 类型参数只接受 string（不接受 ScriptBlock 等）
        if (targetType == typeof(string)) return value is string;
        // 数值类型兼容
        if (IsNumeric(value) && IsNumericType(targetType)) return true;
        return false;
    }

    private static bool IsNumericType(Type t) =>
        t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) ||
        t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    // =========================================================================
    // ADR-0052 §6: 返回类型推导（best-effort，供 LSP / 静态分析消费）
    // =========================================================================

    /// <summary>
    /// 分析函数体的 return 语句，推导返回类型注解。Per ADR-0052 §6.
    /// 规则：所有 return 表达式的字面量类型一致时推导为该类型；否则推导为 object。
    /// 仅处理 LiteralExpression 与 CastExpression，不递归求值（静态分析语义）。
    /// </summary>
    private TypeAnnotation? InferReturnType(FunctionDefinitionStatement fn)
    {
        var returns = new List<TypeAnnotation>();
        CollectReturns(fn.Body.Statements, returns);
        if (returns.Count == 0) return null;
        // 全部相同则取之，否则 object。
        var first = returns[0];
        foreach (var r in returns)
        {
            if (!SameAnnotation(first, r))
                return new PrimitiveTypeAnnotation("object", SourceSpan.Empty);
        }
        return first;
    }

    private static void CollectReturns(IReadOnlyList<Statement> body, List<TypeAnnotation> into)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case ReturnStatement r when r.Value is not null:
                    if (TryInferFromExpr(r.Value, out var ann)) into.Add(ann!);
                    break;
                case IfStatement iff:
                    foreach (var br in iff.Branches) CollectReturns(br.Body, into);
                    if (iff.ElseBody is not null) CollectReturns(iff.ElseBody, into);
                    break;
                case ForStatement f:
                    CollectReturns(f.Body, into);
                    break;
                case ForEachStatement fe:
                    CollectReturns(fe.Body, into);
                    break;
                case WhileStatement w:
                    CollectReturns(w.Body, into);
                    break;
                case TryStatement t:
                    CollectReturns(t.Body, into);
                    foreach (var c in t.Catches) CollectReturns(c.Body, into);
                    break;
            }
        }
    }

    private static bool TryInferFromExpr(Expression expr, out TypeAnnotation? ann)
    {
        ann = null;
        switch (expr)
        {
            case LiteralExpression lit:
                ann = lit.Value switch
                {
                    int => new PrimitiveTypeAnnotation("int", SourceSpan.Empty),
                    long => new PrimitiveTypeAnnotation("long", SourceSpan.Empty),
                    double => new PrimitiveTypeAnnotation("double", SourceSpan.Empty),
                    bool => new PrimitiveTypeAnnotation("bool", SourceSpan.Empty),
                    string => new PrimitiveTypeAnnotation("string", SourceSpan.Empty),
                    _ => new PrimitiveTypeAnnotation("object", SourceSpan.Empty),
                };
                return true;
            case CastExpression c:
                ann = TypeCoercer.ParseTypeAnnotation(c.Type.FullName);
                return ann is not null;
            case UnaryExpression u when u.Operator == UnaryOperator.Negate && u.Operand is LiteralExpression:
                return TryInferFromExpr(u.Operand, out ann);
            default:
                return false;
        }
    }

    private static bool SameAnnotation(TypeAnnotation a, TypeAnnotation b) => (a, b) switch
    {
        (PrimitiveTypeAnnotation pa, PrimitiveTypeAnnotation pb) =>
            string.Equals(pa.Name, pb.Name, StringComparison.OrdinalIgnoreCase),
        (OptionalTypeAnnotation oa, OptionalTypeAnnotation ob) => SameAnnotation(oa.Inner, ob.Inner),
        _ => false,
    };

    // =========================================================================
    // ADR-0053 §2-4: 宏定义 / 宏调用求值（详见 MacroExpander / MacroRegistry）
    // =========================================================================

    private ExecutionResult EvaluateMacroDefinition(MacroDefinitionStatement md)
    {
        _ctx.MacroRegistry?.Register(md);
        return ExecutionResult.Empty;
    }

    private ExecutionResult EvaluateMacroInvocation(MacroInvocationExpression mi)
    {
        // 内建宏优先（println! / dbg! / assert! / assert_eq!）。Per ADR-0053 §5.
        var builtin = MacroExpander.TryExpandBuiltin(mi, _ctx);
        if (builtin is not null) return builtin.Value;

        // 用户自定义宏：查找注册表、展开、重新解析、求值。Per ADR-0053 §3.
        var def = _ctx.MacroRegistry?.Resolve(mi.Name);
        if (def is null)
        {
            _ctx.WriteError(ErrorRecord.FromException(
                new InvalidOperationException($"macro not defined: {mi.Name}!"),
                phase: ErrorPhase.Operation));
            return ExecutionResult.Empty;
        }
        var expanded = MacroExpander.Expand(def, mi.ArgumentTokens, _ctx);
        if (expanded is null)
        {
            _ctx.WriteError(ErrorRecord.FromException(
                new InvalidOperationException($"macro expansion failed: {mi.Name}!"),
                phase: ErrorPhase.Operation));
            return ExecutionResult.Empty;
        }
        // 展开结果重新解析为表达式并求值。
        try
        {
            var tokens = new Tokenizer(expanded).Tokenize();
            var parser = new ModernParser(tokens, expanded);
            var expr = parser.ParseExpression();
            return EvaluateExpression(expr);
        }
        catch (ParserException ex)
        {
            _ctx.WriteError(ErrorRecord.FromException(ex, phase: ErrorPhase.Parse));
            return ExecutionResult.Empty;
        }
    }

    // =========================================================================
    // ADR-0057 §3-5: 自定义类型定义求值（详见 TypeRegistry / OperatorOverloadResolver）
    // =========================================================================

    private ExecutionResult EvaluateTypeDefinition(TypeDefinitionStatement td)
    {
        _ctx.TypeRegistry?.Register(td);
        return ExecutionResult.Empty;
    }
}

/// <summary>脚本执行异常：throw 信号跨作用域时包装。</summary>
public sealed class OpenShellScriptException : Exception
{
    public object? ThrownValue { get; }
    public ExecutionContext? Context { get; }

    public OpenShellScriptException(object? thrownValue, ExecutionContext? ctx)
        : base(thrownValue?.ToString() ?? "script threw")
    {
        ThrownValue = thrownValue;
        Context = ctx;
    }
}

/// <summary>类型引用表达式节点（用于 [Type] 静态访问）。</summary>
public sealed record TypeReferenceExpression(TypeReference Type, SourceSpan Span) : Expression(Span);

/// <summary>命令未找到异常。</summary>
public sealed class CommandNotFoundException : Exception
{
    public string CommandName { get; }
    public CommandNotFoundException(string name) : base($"command not found: {name}") => CommandName = name;
}
