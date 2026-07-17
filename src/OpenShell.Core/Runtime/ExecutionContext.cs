#nullable enable
// ADR-0045 §14 + ADR-0047 §1 求值上下文。
// ExecutionContext 是 Evaluator 的运行时根对象，封装：
//   - IVariableRegistry（作用域栈，含自动变量 $ _ $ args $?）
//   - ICommandRegistry（命令调用）
//   - IErrorStream（错误流）
//   - IHost（CLI/GUI 宿主）
//   - 当前管道对象 $_（per ADR-0046 §5）
//   - 当前函数参数 $args

using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Parsing.Ast;
using OpenShell.Providers;
using OpenShell.Variables;

namespace OpenShell.Runtime;

/// <summary>AST 求值上下文。per ADR-0045 §14 + ADR-0047 §1.</summary>
public sealed class ExecutionContext
{
    /// <summary>变量注册表（含作用域栈）。可空：纯 AST 求值场景下无变量系统。</summary>
    public IVariableRegistry? Variables { get; }

    /// <summary>命令注册表，用于命令调用。可空：纯表达式求值无命令。</summary>
    public ICommandRegistry? Commands { get; }

    /// <summary>错误流。可空。</summary>
    public IErrorStream? Errors { get; }

    /// <summary>宿主抽象。可空。</summary>
    public IHost? Host { get; }

    /// <summary>Provider 注册表。可空。</summary>
    public IProviderRegistry? Providers { get; }

    /// <summary>操作引擎（cp/mv/rm/mkdir/touch 需要）。可空：AST path 之前缺失导致这些命令失败（D-308）。</summary>
    public IOperationEngine? Operations { get; init; }

    /// <summary>别名注册表。可空。</summary>
    public IAliasRegistry? Aliases { get; init; }

    /// <summary>帮助服务。可空。</summary>
    public IHelpService? Help { get; init; }

    /// <summary>虚拟驱动器注册表。可空。</summary>
    public IDriveRegistry? Drives { get; init; }

    /// <summary>当前管道对象 $_。null 表示不在管道上下文。</summary>
    public IItem? CurrentItem { get; set; }

    /// <summary>当前函数/脚本块的 $args 数组。null 表示无参数绑定。</summary>
    public object?[]? CurrentArgs { get; set; }

    /// <summary>
    /// ADR-0056 §3: 当前正在求值的脚本模块文件绝对路径。
    /// <para>由 import 求值在加载子模块时 push（保存旧值，加载后还原）。export 声明读取此字段
    /// 决定把导出实体登记到哪个 ModuleObject。null 表示不在模块加载上下文（如 REPL 顶层）。</para>
    /// </summary>
    public string? CurrentModulePath { get; set; }

    /// <summary>取消令牌。可写：脚本块流式调用时需要绑定调用方令牌。</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// 严格类型模式开关。Per ADR-0052 §5.
    /// 默认 false（动态语义）。开启后 fn 参数类型注解被强制 coercion（失败抛 InvalidCastException）。
    /// 通过 #lang strict / // @strict pragma 开启（宿主负责注入）。
    /// </summary>
    public bool StrictMode { get; set; }

    /// <summary>
    /// 函数返回类型推导缓存。Per ADR-0052 §6.
    /// 键为函数名，值为 best-effort 推导出的 TypeAnnotation（供 LSP / 静态分析消费，运行时不强制）。
    /// </summary>
    public System.Collections.Generic.Dictionary<string, TypeAnnotation> InferredReturnTypes { get; init; } = new();

    /// <summary>
    /// ADR-0053 §2: 宏注册表。存储 macro_rules! 定义的宏，供 name!(...) 调用时展开。
    /// 可空：未启用宏系统时为 null。init 以便宿主注入共享实例。
    /// </summary>
    public MacroRegistry? MacroRegistry { get; init; }

    /// <summary>
    /// ADR-0057 §3: 自定义类型注册表。存储 type Name { ... } 定义，供 op_* 重载解析时查找。
    /// 可空：未启用自定义类型时为 null。init 以便宿主注入共享实例。
    /// </summary>
    public TypeRegistry? TypeRegistry { get; init; }

    public ExecutionContext(
        IVariableRegistry? variables = null,
        ICommandRegistry? commands = null,
        IErrorStream? errors = null,
        IHost? host = null,
        IProviderRegistry? providers = null,
        CancellationToken cancellationToken = default)
    {
        Variables = variables;
        Commands = commands;
        Errors = errors;
        Host = host;
        Providers = providers;
        CancellationToken = cancellationToken;
    }

    /// <summary>派生新上下文，替换 CurrentItem/CurrentArgs，用于脚本块调用。</summary>
    public ExecutionContext WithPipeline(IItem? item, object?[]? args = null) =>
        new(Variables, Commands, Errors, Host, Providers, CancellationToken)
        {
            CurrentItem = item,
            CurrentArgs = args,
            StrictMode = this.StrictMode,
            InferredReturnTypes = this.InferredReturnTypes,
            MacroRegistry = this.MacroRegistry,
            TypeRegistry = this.TypeRegistry,
            Operations = this.Operations,
            Aliases = this.Aliases,
            Help = this.Help,
            Drives = this.Drives,
        };

    /// <summary>在新的 Local 作用域内执行（per ADR-0047 §1）。</summary>
    public IDisposable EnterScope() => Variables?.PushScope(VariableScope.Local) ?? NoopDisposable.Instance;

    /// <summary>设置自动变量 $?（成功标志）。</summary>
    public void SetSuccess(bool success) => Variables?.SetAutomatic("?", success);

    /// <summary>写入一条错误记录。</summary>
    public void WriteError(ErrorRecord record)
    {
        Errors?.Write(record);
        if (Variables is not null) Variables.SetAutomatic("ERROR", record);
    }
}

internal sealed class NoopDisposable : IDisposable
{
    public static readonly NoopDisposable Instance = new();
    public void Dispose() { }
}
