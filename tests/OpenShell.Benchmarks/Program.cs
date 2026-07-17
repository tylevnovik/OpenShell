using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Paths;

namespace OpenShell.Benchmarks;

/// <summary>
/// 基准测试入口。仅 Release 模式可执行：
///   dotnet run -c Release --project tests/OpenShell.Benchmarks
/// Per ADR-0033.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>ItemPath 性能基准。</summary>
[MemoryDiagnoser]
public class ItemPathBenchmarks
{
    [Benchmark]
    public ItemPath Parse_with_provider()
        => ItemPath.Parse("fs::C:/Users/foo/bar.txt");

    [Benchmark]
    public ItemPath Parse_bare()
        => ItemPath.Parse("/usr/local/bin");

    [Benchmark]
    public ItemPath Combine()
    {
        var path = ItemPath.Parse("fs::C:/Users");
        return path.Combine("foo/bar.txt");
    }
}

/// <summary>CommandRegistry 性能基准。</summary>
[MemoryDiagnoser]
public class CommandRegistryBenchmarks
{
    private CommandRegistry _registry = null!;

    [GlobalSetup]
    public void Setup()
    {
        _registry = new CommandRegistry();
        _registry.RegisterFromAssembly(typeof(GetChildItemCommand).Assembly);
    }

    [Benchmark]
    public CommandDescriptor? Resolve_byFullName()
        => _registry.Resolve("get-childitem");

    [Benchmark]
    public CommandDescriptor? Resolve_byAlias()
        => _registry.Resolve("ls");
}
