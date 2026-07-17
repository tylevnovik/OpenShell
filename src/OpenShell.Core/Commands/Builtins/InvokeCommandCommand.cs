using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Remoting;
using OpenShell.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Invoke-Command</c> 命令。Per ADR-0059 §6.
/// 在远程 PSSession 上执行脚本块, 序列化 $using: 变量, 返回远端结果。
/// </summary>
[Verb("Invoke", Noun = "Command", Aliases = ["icm"])]
[Description("Executes a script block on a remote PSSession.")]
[Help(
    Synopsis = "Executes a script block on a remote PSSession via SSH (Invoke-Command).",
    Examples = new[]
    {
        "invoke-command -Id 1 -ScriptBlock { get-process }",
        "$f = '*.log'; invoke-command -Id 1 -ScriptBlock { get-childitem $using:f }",
    },
    RelatedLinks = new[] { "new-pssession", "get-pssession" })]
public sealed class InvokeCommandCommand : ICommand<InvokeCommandCommand.Args>
{
    /// <summary>Arguments for <c>Invoke-Command</c>.</summary>
    /// <param name="Id">目标会话 Id (由 New-PSSession 返回)。与 <paramref name="Session"/> 互斥, 优先使用 <paramref name="Session"/>。</param>
    /// <param name="Session">目标会话对象。与 <paramref name="Id"/> 互斥。</param>
    /// <param name="ScriptBlock">要远程执行的脚本块。</param>
    /// <param name="ArgumentList">位置参数 (做可序列化清洗后传递给远端)。</param>
    public record Args(
        [property: Parameter] int? Id = null,
        [property: Parameter] IPSSession? Session = null,
        [property: Parameter(Position = 0)] ScriptBlock? ScriptBlock = null,
        [property: Parameter] object?[]? ArgumentList = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (args.ScriptBlock is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Invoke-Command requires -ScriptBlock.",
                Operation = "invoke-command",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var manager = ctx.Host.Services.GetService(typeof(PSSessionManager)) as PSSessionManager;
        if (manager is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Remoting service is not available in this context.",
                Operation = "invoke-command",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 解析目标会话: 优先 -Session, 其次 -Id。
        var session = args.Session;
        if (session is null && args.Id is int id)
            session = manager.Get(id);
        if (session is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = args.Id is not null
                    ? $"PSSession with Id {args.Id} not found."
                    : "Invoke-Command requires -Session or -Id.",
                Operation = "invoke-command",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // ADR-0059 §4-5: 序列化脚本块, 捕获 $using: 变量。
        var payload = ScriptBlockSerializer.Serialize(
            args.ScriptBlock,
            ctx.Variables,
            args.ArgumentList ?? Array.Empty<object?>());

        // 远端执行。
        object? result;
        try
        {
            result = await session.InvokeAsync(payload, ct).ConfigureAwait(false);
        }
        catch (RemoteExecutionException ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"remote execution failed: {ex.Message}",
                Operation = "invoke-command",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NetworkError,
                Message = $"PSSession transport error: {ex.Message}",
                Operation = "invoke-command",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 把远端结果转为 IItem 流 yield。
        if (result is null) yield break;

        if (result is System.Collections.IEnumerable e and not string)
        {
            foreach (var item in e)
            {
                yield return ValueToItem(item);
            }
        }
        else
        {
            yield return ValueToItem(result);
        }
    }

    private static IItem ValueToItem(object? value)
    {
        if (value is IItem item) return item;
        return new Item
        {
            Path = OpenShell.Paths.ItemPath.Root("remoting"),
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", value),
        };
    }
}
