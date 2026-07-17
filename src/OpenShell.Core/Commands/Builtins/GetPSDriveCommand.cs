using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-PSDrive</c> command. Per ADR-0023 M1. Enumerates all
/// registered providers and emits each <see cref="ProviderDrive"/> they expose
/// via the <see cref="IDriveProvider"/> capability as a virtual
/// <see cref="IItem"/>.
/// </summary>
[Verb("Get", Noun = "PSDrive", Aliases = ["gdr", "drives"])]
[Description("Lists all mounted drives across providers.")]
public sealed class GetPSDriveCommand : ICommand<GetPSDriveCommand.Args>
{
    /// <summary>Arguments for <c>Get-PSDrive</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 1) Provider 上报的物理 Drive（fs 盘符、远程卷等）。
        foreach (var info in ctx.Providers.Registered)
        {
            if (!ctx.Providers.TryGet(info.Name, out var provider) || provider is null)
                continue;

            if (provider is not IDriveProvider driveProvider)
                continue;

            IReadOnlyList<ProviderDrive> drives;
            try
            {
                drives = await driveProvider.GetDrivesAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            foreach (var drive in drives)
            {
                yield return new Item
                {
                    Path = drive.Root,
                    Kind = ItemKind.Directory,
                    Size = drive.TotalSize,
                    Properties = PropertyBag.Empty
                        .With("Name", drive.Name)
                        .With("DisplayLabel", drive.DisplayLabel)
                        .With("Provider", info.Name)
                        .With("TotalSize", drive.TotalSize)
                        .With("FreeSpace", drive.FreeSpace)
                        .With("IsMounted", drive.IsMounted)
                        .With("Source", "physical"),
                };
            }
        }

        // 2) 用户挂载的虚拟 Drive（New-PSDrive 创建）。Per ADR-0023.
        if (ctx.Drives is { } virtualDrives)
        {
            foreach (var drive in virtualDrives.Mounted)
            {
                yield return new Item
                {
                    Path = drive.Root,
                    Kind = ItemKind.Directory,
                    Size = drive.TotalSize,
                    Properties = PropertyBag.Empty
                        .With("Name", drive.Name)
                        .With("DisplayLabel", drive.DisplayLabel)
                        .With("Provider", drive.Root.Provider)
                        .With("IsMounted", drive.IsMounted)
                        .With("Source", "virtual"),
                };
            }
        }
    }
}
