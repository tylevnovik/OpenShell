// ADR-0039 §10: dotnet openshell 全局工具。
// 提供 pack / sign / push / install / restore 五个子命令, 供 provider 开发者与 CI 使用。
// 用法:
//   dotnet openshell pack --manifest <path> --assembly <dll> [--output <dir>] [--extra <file>...]
//   dotnet openshell sign --package <osp> --key <hex|@file>
//   dotnet openshell push --package <osp> --source <name> [--api-key <key>]
//   dotnet openshell install <name> [--version <v>] [--source <name>] [--dry-run] [--trust-key <hex>]
//   dotnet openshell restore

using OpenShell.Packaging;
using OpenShell.Packaging.Installation;
using OpenShell.Packaging.Registry;
using OpenShell.Packaging.Signing;
using OpenShell.Providers;

namespace OpenShell.DotnetTool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || IsHelpFlag(args[0]))
        {
            PrintUsage();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        try
        {
            return command switch
            {
                "pack" => await RunPackAsync(rest).ConfigureAwait(false),
                "sign" => await RunSignAsync(rest).ConfigureAwait(false),
                "push" => await RunPushAsync(rest).ConfigureAwait(false),
                "install" => await RunInstallAsync(rest).ConfigureAwait(false),
                "restore" => await RunRestoreAsync(rest).ConfigureAwait(false),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"openshell: {command}: {ex.Message}");
            return 1;
        }
    }

    // ───────────────────────── pack ─────────────────────────

    private static async Task<int> RunPackAsync(string[] args)
    {
        var opts = ParseOptions(args);
        var manifestPath = RequireOption(opts, "manifest");
        var assemblyPath = RequireOption(opts, "assembly");
        var outputDir = GetOption(opts, "output");
        var extras = GetOptions(opts, "extra");

        if (!File.Exists(manifestPath)) throw new FileNotFoundException($"Manifest not found: {manifestPath}", manifestPath);
        if (!File.Exists(assemblyPath)) throw new FileNotFoundException($"Assembly not found: {assemblyPath}", assemblyPath);

        var manifest = ProviderManifest.Parse(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false));
        var packager = new OspPackager();
        var outPath = await packager.PackAsync(manifest, assemblyPath, extras, outputDir).ConfigureAwait(false);
        Console.WriteLine($"Packed: {outPath}");
        return 0;
    }

    // ───────────────────────── sign ─────────────────────────

    private static async Task<int> RunSignAsync(string[] args)
    {
        var opts = ParseOptions(args);
        var packagePath = RequireOption(opts, "package");
        var keySpec = RequireOption(opts, "key");

        if (!File.Exists(packagePath)) throw new FileNotFoundException($"Package not found: {packagePath}", packagePath);

        var privateKey = ParseKeyBytes(keySpec);
        var packager = new OspPackager();
        await packager.SignEd25519Async(packagePath, privateKey).ConfigureAwait(false);
        Console.WriteLine($"Signed: {packagePath}");
        return 0;
    }

    // ───────────────────────── push ─────────────────────────

    private static async Task<int> RunPushAsync(string[] args)
    {
        var opts = ParseOptions(args);
        var packagePath = RequireOption(opts, "package");
        var sourceName = RequireOption(opts, "source");
        var apiKey = GetOption(opts, "api-key");

        if (!File.Exists(packagePath)) throw new FileNotFoundException($"Package not found: {packagePath}", packagePath);

        var registry = new ProviderSourceRegistry();
        await registry.LoadAsync().ConfigureAwait(false);
        var source = registry.TryGet(sourceName)
            ?? throw new OspPackageException($"Source '{sourceName}' is not registered. Use Register-ProviderSource to add it.");

        using var client = new RegistryClient();
        await client.PublishAsync(source, packagePath, apiKey).ConfigureAwait(false);
        Console.WriteLine($"Pushed: {Path.GetFileName(packagePath)} -> {source.Name} ({source.Url})");
        return 0;
    }

    // ───────────────────────── install ─────────────────────────

    private static async Task<int> RunInstallAsync(string[] args)
    {
        var opts = ParseOptions(args);
        // 位置参数: install <name>
        var name = opts.Positional.FirstOrDefault()
            ?? throw new ArgumentException("Provider name is required. Usage: dotnet openshell install <name> [options]");
        var version = GetOption(opts, "version");
        var sourceName = GetOption(opts, "source");
        var dryRun = HasFlag(opts, "dry-run");
        byte[]? trustKey = null;
        if (GetOption(opts, "trust-key") is { } hex)
            trustKey = ParseKeyBytes(hex);

        var installer = CreateInstaller();
        var result = await installer.InstallAsync(name, version, sourceName, dryRun, trustKey).ConfigureAwait(false);
        Console.WriteLine(result.Summary);
        if (result.Dependencies.Count > 0)
        {
            Console.WriteLine("Dependencies:");
            foreach (var dep in result.Dependencies)
                Console.WriteLine($"  {dep.Kind}: {dep.Name} {dep.RequestedVersion} -> {dep.ResolvedVersion ?? "(unresolved)"} [{(dep.Satisfied ? "satisfied" : "missing")}]");
        }
        return 0;
    }

    // ───────────────────────── restore ─────────────────────────

    private static async Task<int> RunRestoreAsync(string[] args)
    {
        // restore: 读取 plugins.config.toml, 重新安装所有已记录但缺失/损坏的 provider。
        var installer = CreateInstaller();
        await installer.RestoreAsync().ConfigureAwait(false);
        Console.WriteLine("Restore complete.");
        return 0;
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>构造一个使用默认路径与 Ed25519 校验器的 ProviderInstaller。</summary>
    private static ProviderInstaller CreateInstaller()
    {
        OpenShellPaths.EnsurePackagingDirs();
        var sources = new ProviderSourceRegistry();
        sources.LoadAsync().GetAwaiter().GetResult();
        var client = new RegistryClient();
        var verifier = new Ed25519SignatureVerifier();
        var pluginsConfig = new PluginsConfig();
        pluginsConfig.LoadAsync().GetAwaiter().GetResult();
        return new ProviderInstaller(sources, client, verifier, pluginsConfig);
    }

    /// <summary>解析命令行参数为选项字典 + 位置参数列表。支持 --key value 与 --flag 两种形式。</summary>
    private static Options ParseOptions(string[] args)
    {
        var opts = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var key = arg[2..];
                // --key=value 形式
                var eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    opts.Set(key[..eq], key[(eq + 1)..]);
                    continue;
                }
                // --key value 或 --flag
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    opts.Set(key, args[++i]);
                }
                else
                {
                    opts.SetFlag(key);
                }
            }
            else
            {
                opts.Positional.Add(arg);
            }
        }
        return opts;
    }

    private static string RequireOption(Options opts, string key)
        => opts.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
            ? v
            : throw new ArgumentException($"Required option --{key} is missing.");

    private static string? GetOption(Options opts, string key)
        => opts.TryGetValue(key, out var v) ? v : null;

    private static IReadOnlyList<string> GetOptions(Options opts, string key)
        => opts.GetAll(key);

    private static bool HasFlag(Options opts, string key)
        => opts.HasFlag(key);

    /// <summary>解析密钥参数: "@file" 从文件读取, 否则按 hex 字符串解析。返回原始字节。</summary>
    private static byte[] ParseKeyBytes(string spec)
    {
        if (spec.StartsWith('@'))
        {
            var path = spec[1..];
            if (!File.Exists(path)) throw new FileNotFoundException($"Key file not found: {path}", path);
            return File.ReadAllBytes(path);
        }
        // hex 字符串 (允许含空格/冒号分隔)。
        var clean = spec.Replace(":", "").Replace(" ", "");
        if (clean.Length % 2 != 0)
            throw new ArgumentException("Hex key string has an odd number of characters.", nameof(spec));
        var bytes = new byte[clean.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static bool IsHelpFlag(string s)
        => string.Equals(s, "-h", StringComparison.Ordinal)
            || string.Equals(s, "--help", StringComparison.Ordinal)
            || string.Equals(s, "help", StringComparison.OrdinalIgnoreCase);

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"openshell: unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"OpenShell dotnet global tool. Per ADR-0039 §10.

Usage: dotnet openshell <command> [options]

Commands:
  pack      Pack a provider assembly + manifest into a .osp file.
  sign      Sign an existing .osp package with Ed25519.
  push      Publish a .osp package to a registry source.
  install   Install a provider from a registry source.
  restore   Reinstall all providers recorded in plugins.config.toml.

pack:
  --manifest <path>    Provider manifest JSON file (openshell.provider.json).
  --assembly <dll>     Provider main assembly (DLL).
  --output <dir>       Output directory (default: assembly dir).
  --extra <file>       Additional file to include (repeatable).

sign:
  --package <osp>      Path to the .osp package to sign.
  --key <hex|@file>    Ed25519 private key (32-byte hex string or @file path).

push:
  --package <osp>      Path to the .osp package to publish.
  --source <name>      Registered source name.
  --api-key <key>      API key (Bearer token) for the registry.

install:
  <name>               Provider name (positional).
  --version <v>        Specific version to install (default: latest).
  --source <name>      Specific source to install from.
  --dry-run            Resolve dependencies without downloading/installing.
  --trust-key <hex>    Explicitly trust the given public key bytes.

restore:
  (no options)");
    }

    /// <summary>简单的选项容器: 支持 --key value、--flag 与位置参数。</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Positional { get; } = new();

        public void Set(string key, string value)
        {
            if (!_values.TryGetValue(key, out var list))
            {
                list = new List<string>();
                _values[key] = list;
            }
            list.Add(value);
        }

        public void SetFlag(string key) => _flags.Add(key);

        public bool TryGetValue(string key, out string? value)
        {
            if (_values.TryGetValue(key, out var list) && list.Count > 0)
            {
                value = list[^1];
                return true;
            }
            value = null;
            return false;
        }

        public IReadOnlyList<string> GetAll(string key)
            => _values.TryGetValue(key, out var list) ? list : Array.Empty<string>();

        public bool HasFlag(string key) => _flags.Contains(key);
    }
}
