using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Get-Random</c> command. Per ADR-0048 §9.6.
/// <para>
/// Returns a random integer, double, or collection element. Supports <c>-Minimum</c>/<c>-Maximum</c>
/// range and <c>-InputObject</c> for random selection. <c>-SetSeed</c> initializes the PRNG.
/// </para>
/// </summary>
[Verb("Get", Noun = "Random", Aliases = ["random"])]
[Description("Gets a random number or random element from a collection.")]
public sealed class GetRandomCommand : ICommand<GetRandomCommand.Args>
{
    private static Random? _shared;
    private static readonly object _lock = new();

    /// <summary>Arguments for <c>Get-Random</c>.</summary>
    public record Args(
        int? Maximum = null,
        int? Minimum = null,
        int? Count = null,
        System.Collections.IEnumerable? InputObject = null,
        int? SetSeed = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var random = GetOrSeedRandom(args.SetSeed);

        // Random element from input collection
        if (args.InputObject is not null)
        {
            var items = new List<object?>();
            foreach (var item in args.InputObject)
                items.Add(item);

            if (items.Count == 0)
                yield break;

            if (args.Count is int count && count > 0 && count < items.Count)
            {
                // Return count random elements (without replacement)
                var shuffled = items.OrderBy(_ => random.Next()).Take(count).ToList();
                foreach (var item in shuffled)
                {
                    yield return new Item
                    {
                        Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Get-Random" },
                        Kind = ItemKind.Property,
                        Properties = PropertyBag.Empty.With("Value", item),
                    };
                }
            }
            else
            {
                var idx = random.Next(items.Count);
                yield return new Item
                {
                    Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Get-Random" },
                    Kind = ItemKind.Property,
                    Properties = PropertyBag.Empty.With("Value", items[idx]),
                };
            }
            yield break;
        }

        // Random number in range
        var min = args.Minimum ?? 0;
        var max = args.Maximum ?? int.MaxValue;
        if (max <= min)
            throw new ArgumentException($"-Maximum ({max}) must be greater than -Minimum ({min}).");

        var value = random.Next(min, max);
        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Get-Random" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", value),
        };
    }

    private static Random GetOrSeedRandom(int? seed)
    {
        if (seed is int s)
        {
            var seeded = new Random(s);
            lock (_lock) { _shared = seeded; }
            return seeded;
        }
        lock (_lock)
        {
            return _shared ??= new Random();
        }
    }
}
