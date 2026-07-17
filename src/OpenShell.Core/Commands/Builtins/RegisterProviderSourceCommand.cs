using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Packaging.Registry;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Register-ProviderSource</c> command. Per ADR-0039 §5 / §3.
/// 添加一个注册源到 <c>~/.openshell/registries.toml</c>。
/// </summary>
[Verb("Register", Noun = "ProviderSource", Aliases = ["npsrc"])]
[Description("Adds a new provider registry source.")]
public sealed class RegisterProviderSourceCommand : ICommand<RegisterProviderSourceCommand.Args>
{
    /// <summary>Arguments for <c>Register-ProviderSource</c>.</summary>
    public record Args
    {
        /// <summary>源唯一名称。必填。</summary>
        [Parameter(Position = 0)]
        public string? Name { get; init; }

        /// <summary>源 URL (HTTP/file)。必填。</summary>
        [Parameter(Position = 1)]
        public string? Url { get; init; }

        /// <summary>优先级 (数字越小越优先, 缺省 100)。</summary>
        [Parameter(Aliases = ["p"])]
        public int Priority { get; init; } = 100;

        /// <summary>是否受信任源 (放宽签名校验)。</summary>
        [Parameter(Aliases = ["trusted"])]
        public bool Trusted { get; init; }

        /// <summary>认证 token 引用 (env:VAR_NAME 形式)。</summary>
        [Parameter(Aliases = ["auth"])]
        public string? Auth { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.Name) || string.IsNullOrWhiteSpace(args.Url))
        {
            await ctx.Host.WriteOutputLineAsync("usage: register-providersource <name> <url> [-Priority <n>] [-Trusted] [-Auth <env:VAR>]", ct);
            yield break;
        }

        var registry = ctx.Host.Services.GetService(typeof(ProviderSourceRegistry)) as ProviderSourceRegistry;
        if (registry is null)
        {
            await ctx.Host.WriteOutputLineAsync("[register-providersource] ProviderSourceRegistry not registered.", ct);
            yield break;
        }

        var source = new ProviderSource
        {
            Name = args.Name!,
            Url = args.Url!,
            Priority = args.Priority,
            Trusted = args.Trusted,
            Auth = args.Auth,
        };
        try
        {
            registry.AddSource(source);
            await registry.SaveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ctx.Host.WriteOutputLineAsync($"[register-providersource] failed: {ex.Message}", ct);
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync($"registered source '{args.Name}'.", ct);
        yield return new Item
        {
            Path = new ItemPath { Provider = "src", InternalPath = "/" + args.Name },
            Kind = ItemKind.Unknown,
            Properties = PropertyBag.Empty
                .With("Name", source.Name)
                .With("Url", source.Url)
                .With("Priority", source.Priority)
                .With("Trusted", source.Trusted),
        };
    }
}
