using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Variables;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Export-Variable</c> 命令：导出变量到 JSON 文件。Per ADR-0047 §12.1-12.3.
/// <para>
/// 默认导出到 <c>~/.openshell/variables.json</c>。支持 <c>-All</c> 导出全部可序列化变量、
/// <c>-Name</c> 指定单个变量。UTF-8 无 BOM, camelCase, WriteIndented。
/// </para>
/// <para>
/// Per ADR-0047 §12.5 局限：跳过 <c>scriptblock</c> 与 <c>IItem</c> (warning 提示)。
/// 循环引用对象抛 <see cref="JsonException"/>。
/// </para>
/// </summary>
[Verb("Export", Noun = "Variable", Aliases = ["epv"])]
[Description("Exports variables to a JSON file.")]
public sealed class ExportVariableCommand : ICommand<ExportVariableCommand.Args>
{
    /// <summary>Arguments for <c>Export-Variable</c>.</summary>
    /// <param name="Name">要导出的变量名 (可多个, 逗号分隔)。未指定时与 -All 等价。</param>
    /// <param name="Path">目标文件路径。默认 <c>~/.openshell/variables.json</c>。</param>
    /// <param name="All">导出全部可序列化变量 (Global + Script + Local 可见集合)。</param>
    /// <param name="Force">覆盖只读文件 / 已存在文件。</param>
    /// <param name="Append">追加到已存在文件 (合并 JSON 对象, 同名键覆盖)。</param>
    /// <param name="Scope">导出哪个作用域的变量 (默认 Global, 与自动导入行为对齐)。</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Name = null,
        [property: Parameter] string? Path = null,
        [property: Parameter] bool All = false,
        [property: Parameter] bool Force = false,
        [property: Parameter] bool Append = false,
        [property: Parameter] VariableScope? Scope = null);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var vars = ctx.Variables;
        if (vars is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Variable registry is not available in this context.",
                Operation = "export-variable",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var destination = string.IsNullOrWhiteSpace(args.Path)
            ? System.IO.Path.Combine(DefaultUserGlobalDir(), "variables.json")
            : args.Path!;

        // 收集要导出的 (name, value, type) 三元组。
        var entries = CollectEntries(vars, args);
        if (entries.Count == 0)
        {
            await ctx.Host.WriteOutputLineAsync(
                "Export-Variable: nothing to export (no matching variables).", ct).ConfigureAwait(false);
            yield break;
        }

        // 序列化为 JSON 对象。
        var dict = new Dictionary<string, VariableRecord>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        foreach (var (name, value) in entries)
        {
            if (ShouldSkip(value))
            {
                skipped.Add(name);
                continue;
            }

            try
            {
                var typeStr = value?.GetType().FullName ?? "object";
                dict[name] = new VariableRecord(value, typeStr);
            }
            catch (JsonException)
            {
                skipped.Add(name);
            }
        }

        // -Append 合并：读取已存在文件, 同名键覆盖。
        if (args.Append && File.Exists(destination))
        {
            try
            {
                var existingJson = await File.ReadAllTextAsync(destination, ct).ConfigureAwait(false);
                var existing = JsonSerializer.Deserialize<Dictionary<string, VariableRecord>>(existingJson, JsonOpts);
                if (existing is not null)
                {
                    foreach (var kv in existing)
                    {
                        // 不覆盖本次新增的 (本次优先)。
                        if (!dict.ContainsKey(kv.Key))
                            dict[kv.Key] = kv.Value;
                    }
                }
            }
            catch (JsonException ex)
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.InvalidArgument,
                Message = $"Append: existing file '{destination}' is not valid JSON: {ex.Message}",
                    Operation = "export-variable",
                    Phase = ErrorPhase.Operation,
                });
                yield break;
            }
        }

        // 写文件 (UTF-8 无 BOM)。
        try
        {
            var dir = System.IO.Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(dict, JsonOpts);
            await File.WriteAllTextAsync(destination, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.PermissionDenied,
                Message = ex.Message,
                Operation = "export-variable",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 跳过的变量提示 warning。
        foreach (var name in skipped)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Skipped variable '{name}': scriptblock / IItem cannot be persisted (per ADR-0047 §12.5).",
                Operation = "export-variable",
                Phase = ErrorPhase.Operation,
            });
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Exported {dict.Count} variable(s) to '{destination}'" +
            (skipped.Count > 0 ? $", skipped {skipped.Count} (non-serializable)." : "."),
            ct).ConfigureAwait(false);

        yield break;
    }

    private static List<(string Name, object? Value)> CollectEntries(IVariableRegistry vars, Args args)
    {
        var result = new List<(string, object?)>();

        if (!string.IsNullOrEmpty(args.Name))
        {
            foreach (var name in args.Name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var value = vars.Resolve(name, args.Scope ?? VariableScope.Session);
                result.Add((name, value));
            }
            return result;
        }

        // -All 或无 -Name: 列举指定作用域 (默认 Global, 与自动导入行为对齐)。
        var scope = args.Scope ?? VariableScope.Global;
        foreach (var kv in vars.List(scope))
        {
            result.Add((kv.Key, kv.Value));
        }
        return result;
    }

    private static bool ShouldSkip(object? value)
    {
        // Per ADR-0047 §12.5: 不能持久化 scriptblock 与 IItem。
        if (value is null) return false;
        var t = value.GetType();
        if (typeof(OpenShell.Runtime.ScriptBlock).IsAssignableFrom(t)) return true;
        if (typeof(IItem).IsAssignableFrom(t)) return true;
        return false;
    }

    private static string DefaultUserGlobalDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.CurrentDirectory;
        return System.IO.Path.Combine(home, ".openshell");
    }

    /// <summary>JSON 序列化记录。Per ADR-0047 §12.2.</summary>
    private sealed class VariableRecord
    {
        public VariableRecord() { }
        public VariableRecord(object? value, string type)
        {
            Value = value;
            Type = type;
        }
        public object? Value { get; set; }
        public string Type { get; set; } = "object";
    }
}
