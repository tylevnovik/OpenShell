using System.Reflection;
using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>New-Object</c> command. Per ADR-0048 §4.3.
/// <para>
/// Creates a .NET object instance via <c>Type.GetType</c> + <c>AppDomain.GetAssemblies()</c> fallback,
/// then <c>Activator.CreateInstance</c>. Supports <c>-ArgumentList</c> for constructor args.
/// COM object creation (<c>-ComObject</c>) is supported on Windows only.
/// </para>
/// </summary>
[Verb("New", Noun = "Object", Aliases = ["new"])]
[Description("Creates an instance of a .NET type.")]
public sealed class NewObjectCommand : ICommand<NewObjectCommand.Args>
{
    /// <summary>Arguments for <c>New-Object</c>.</summary>
    /// <param name="TypeName">Fully-qualified type name. Mandatory. Position 0.</param>
    /// <param name="ArgumentList">Constructor arguments.</param>
    /// <param name="ComObject">COM ProgID (Windows only).</param>
    public record Args(
        [property: Parameter(Position = 0)] string? TypeName = null,
        object[]? ArgumentList = null,
        string? ComObject = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        object? instance;

        if (args.ComObject is not null)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("New-Object -ComObject requires Windows.");

            var comType = Type.GetTypeFromProgID(args.ComObject, throwOnError: true)
                ?? throw new TypeLoadException($"Cannot resolve COM ProgID '{args.ComObject}'.");
            instance = Activator.CreateInstance(comType);
        }
        else
        {
            if (string.IsNullOrEmpty(args.TypeName))
                throw new ArgumentException("New-Object requires -TypeName.");

            var type = ResolveType(args.TypeName!);
            if (type is null)
                throw new TypeLoadException($"Cannot find type '{args.TypeName}'.");

            var ctorArgs = args.ArgumentList ?? Array.Empty<object?>();
            instance = Activator.CreateInstance(type, ctorArgs);
        }

        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "cli", InternalPath = "New-Object" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("Value", instance)
                .With("Type", instance?.GetType()),
        };
    }

    /// <summary>
    /// Resolve a type by name. Tries <see cref="Type.GetType(string)"/> first, then searches loaded assemblies.
    /// </summary>
    private static Type? ResolveType(string typeName)
    {
        // Try direct
        var type = Type.GetType(typeName);
        if (type is not null) return type;

        // Search loaded assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = asm.GetType(typeName);
            if (type is not null) return type;
        }

        // Try common namespace prefixes (System.*, etc.)
        var prefixes = new[]
        {
            "System.", "System.IO.", "System.Collections.Generic.",
            "System.Text.", "System.Net.", "System.Diagnostics.",
        };

        foreach (var prefix in prefixes)
        {
            type = Type.GetType(prefix + typeName);
            if (type is not null) return type;
        }

        return null;
    }
}
