using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>New-PSDrive</c> command. Per ADR-0023 M1. Mounts a virtual
/// drive backed by an existing provider path (e.g. <c>new-psdrive -Name tmp -Provider fs -Root $env:TMP</c>).
/// </summary>
[Verb("New", Noun = "PSDrive", Aliases = ["ndr", "mount"])]
[Description("Mounts a virtual drive backed by a provider path.")]
[Help(
    Synopsis = "Mounts a virtual drive backed by a provider path.",
    Examples = new[]
    {
        "new-psdrive -Name tmp -Provider fs -Root C:/Temp           # mount fs::C:/Temp as tmp",
        "new-psdrive -Name work -Provider fs -Root $HOME/Projects   # shortcut to projects",
        "mount archive zip::archive.zip                              # alias form",
    },
    RelatedLinks = new[] { "get-psdrive", "remove-psdrive" })]
public sealed class NewPSDriveCommand : ICommand<NewPSDriveCommand.Args>
{
    /// <summary>Arguments for <c>New-PSDrive</c>.</summary>
    /// <param name="Name">Drive name (e.g. <c>tmp</c>, <c>work</c>). Used in prompts and <c>Get-PSDrive</c>.</param>
    /// <param name="Provider">Backing provider name (e.g. <c>fs</c>, <c>zip</c>, <c>reg</c>).</param>
    /// <param name="Root">Root path within the provider (e.g. <c>C:/Temp</c> or full <c>fs::C:/Temp</c>).</param>
    /// <param name="Description">Optional human-readable label.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name,
        [property: Parameter(Position = 1, Mandatory = true)] string Provider,
        [property: Parameter(Position = 2, Mandatory = true)] string Root,
        [property: Parameter] string? Description = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var drives = ctx.Drives;
        if (drives is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Drive registry is not available in this context.",
                Operation = "new-psdrive",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var name = args.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Drive name is required.",
                Operation = "new-psdrive",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        // 校验 Provider 是否已注册。
        if (!ctx.Providers.TryGet(args.Provider, out var provider) || provider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ProviderNotFound,
                Message = $"Provider '{args.Provider}' is not registered.",
                Operation = "new-psdrive",
                Phase = ErrorPhase.ProviderResolution,
                Suggestion = "try 'get-command get-psdrive' or check provider name.",
            });
            yield break;
        }

        // 解析 Root：支持相对路径（按 Provider 默认根）或 provider::path 全限定形式。
        var rootPath = ResolveRoot(args.Root, args.Provider, ctx);

        var drive = new ProviderDrive
        {
            Name = name,
            Root = rootPath,
            DisplayLabel = args.Description ?? $"{args.Provider}://{rootPath.InternalPath}",
            IsMounted = true,
        };

        drives.Mount(drive);

        await ctx.Host.WriteOutputLineAsync(
            $"Mounted drive '{name}' -> {rootPath.Display}", ct).ConfigureAwait(false);

        yield return new Item
        {
            Path = rootPath,
            Kind = ItemKind.Directory,
            Properties = PropertyBag.Empty
                .With("Name", drive.Name)
                .With("Provider", args.Provider)
                .With("Root", rootPath.Display)
                .With("DisplayLabel", drive.DisplayLabel)
                .With("IsMounted", true),
        };
    }

    private static ItemPath ResolveRoot(string root, string providerName, CommandContext ctx)
    {
        // 全限定 provider::path 直接解析。
        if (root.Contains("::"))
            return ItemPath.Parse(root);

        // 相对路径：以 Provider 自己的根为前缀（fs:: + C:/Temp 形式）。
        return new ItemPath
        {
            Provider = providerName,
            InternalPath = root.Replace('\\', '/'),
        };
    }
}
