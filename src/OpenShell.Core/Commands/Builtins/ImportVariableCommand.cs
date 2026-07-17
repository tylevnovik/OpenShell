using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Variables;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Import-Variable</c> 命令：从 JSON 文件导入变量。Per ADR-0047 §12.1-12.4.
/// <para>
/// 默认从 <c>~/.openshell/variables.json</c> 读取。支持 <c>-Name</c> 选择性导入、
/// <c>-Scope Session|Global|Script|Local</c> 指定写入作用域。
/// </para>
/// <para>
/// Per ADR-0047 §12.4: 启动时自动 <c>Import-Variable</c> 写入 Global 作用域 (不是 Session),
/// 避免覆盖 REPL 临时赋值; 用户可用 <c>Import-Variable -Scope Session</c> 显式覆盖。
/// 失败时 warning 不阻断启动 (Per ADR-0041 启动脚本容错)。
/// </para>
/// </summary>
[Verb("Import", Noun = "Variable", Aliases = ["ipv"])]
[Description("Imports variables from a JSON file.")]
public sealed class ImportVariableCommand : ICommand<ImportVariableCommand.Args>
{
    /// <summary>Arguments for <c>Import-Variable</c>.</summary>
    /// <param name="Name">要导入的变量名 (可多个, 逗号分隔)。未指定时导入全部。</param>
    /// <param name="Path">源文件路径。默认 <c>~/.openshell/variables.json</c>。</param>
    /// <param name="Scope">写入作用域。默认 Global (与 ADR-0047 §12.4 自动导入行为对齐)。</param>
    /// <param name="Force">覆盖 ReadOnly 变量 (危险, 通常不需要)。</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Name = null,
        [property: Parameter] string? Path = null,
        [property: Parameter] VariableScope Scope = VariableScope.Global,
        [property: Parameter] bool Force = false);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
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
                Operation = "import-variable",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var source = string.IsNullOrWhiteSpace(args.Path)
            ? System.IO.Path.Combine(DefaultUserGlobalDir(), "variables.json")
            : args.Path!;

        if (!File.Exists(source))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"Import-Variable: file not found: '{source}'.",
                Operation = "import-variable",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 读取 + 反序列化。
        Dictionary<string, VariableRecord> dict;
        try
        {
            var json = await File.ReadAllTextAsync(source, ct).ConfigureAwait(false);
            dict = JsonSerializer.Deserialize<Dictionary<string, VariableRecord>>(json, JsonOpts)
                ?? new Dictionary<string, VariableRecord>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Import-Variable: failed to parse JSON: {ex.Message}",
                Operation = "import-variable",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }
        catch (UnauthorizedAccessException ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.PermissionDenied,
                Message = ex.Message,
                Operation = "import-variable",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 过滤 -Name 列表。
        var nameFilter = args.Name is null
            ? null
            : new HashSet<string>(
                args.Name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

        int imported = 0;
        int skipped = 0;
        foreach (var kv in dict)
        {
            if (nameFilter is not null && !nameFilter.Contains(kv.Key))
            {
                skipped++;
                continue;
            }

            // 反序列化 value: 根据 type 字段尝试还原强类型值。
            var value = ReconstructValue(kv.Value);

            // 写入指定作用域。
            try
            {
                vars.Set(kv.Key, value ?? string.Empty, args.Scope);
                imported++;
            }
            catch (ReadOnlyVariableException)
            {
                if (args.Force)
                {
                    // -Force: 通过 SetAutomatic 绕过只读检查 (但只对 Global 作用域有意义)。
                    if (vars is InMemoryVariableRegistry mem)
                        mem.SetAutomatic(kv.Key, value ?? string.Empty);
                    else
                        vars.Set(kv.Key, value ?? string.Empty, args.Scope);
                    imported++;
                }
                else
                {
                    ctx.Errors?.Write(new ErrorRecord
                    {
                        Category = ErrorCategory.InvalidArgument,
                        Message = $"Cannot overwrite read-only variable '${kv.Key}'. Use -Force to override.",
                        Operation = "import-variable",
                        Phase = ErrorPhase.Operation,
                    });
                    skipped++;
                }
            }
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Imported {imported} variable(s) from '{source}'" +
            (skipped > 0 ? $", skipped {skipped}." : "."),
            ct).ConfigureAwait(false);

        yield break;
    }

    /// <summary>
    /// 根据 type 字段尝试还原强类型值。Per ADR-0047 §12.3 类型支持表。
    /// 反序列化失败时退化为 object (JsonElement)。
    /// </summary>
    private static object? ReconstructValue(VariableRecord record)
    {
        if (record.Value is null) return null;
        if (record.Value is JsonElement je)
        {
            return ReconstructFromJson(je, record.Type);
        }
        return record.Value;
    }

    private static object? ReconstructFromJson(JsonElement je, string? typeStr)
    {
        // 类型化还原 (常见基元 + Type + 数组)。
        try
        {
            return typeStr switch
            {
                "int" or "System.Int32" => je.GetInt32(),
                "long" or "System.Int64" => je.GetInt64(),
                "double" or "System.Double" => je.GetDouble(),
                "float" or "System.Single" => je.GetSingle(),
                "decimal" or "System.Decimal" => je.GetDecimal(),
                "bool" or "System.Boolean" => je.GetBoolean(),
                "string" or "System.String" => je.GetString(),
                "char" or "System.Char" => je.GetString() is { Length: 1 } s ? s[0] : '\0',
                "datetime" or "System.DateTime" => je.GetDateTime(),
                "datetimeoffset" or "System.DateTimeOffset" => je.GetDateTimeOffset(),
                "guid" or "System.Guid" => je.GetString() is { } g ? Guid.Parse(g) : Guid.Empty,
                "timespan" or "System.TimeSpan" => je.GetString() is { } ts ? TimeSpan.Parse(ts, System.Globalization.CultureInfo.InvariantCulture) : TimeSpan.Zero,
                _ => TryReconstructComplex(je, typeStr),
            };
        }
        catch (FormatException)
        {
            return je.GetRawText(); // 退化为字符串。
        }
    }

    private static object? TryReconstructComplex(JsonElement je, string? typeStr)
    {
        // Type 类型: 通过 Type.GetType 还原。
        if (string.Equals(typeStr, "type", StringComparison.OrdinalIgnoreCase)
            || string.Equals(typeStr, "System.Type", StringComparison.OrdinalIgnoreCase))
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                var name = je.GetString();
                if (string.IsNullOrEmpty(name)) return null;
                return Type.GetType(name, throwOnError: false);
            }
        }

        // int[] / string[] / object[] 还原。
        if (typeStr is not null && typeStr.EndsWith("[]"))
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                var list = new List<object?>();
                foreach (var item in je.EnumerateArray())
                {
                    list.Add(ReconstructFromJson(item, null));
                }
                var elemTypeName = typeStr[..^2];
                var elemType = Type.GetType(elemTypeName) ?? typeof(object);
                var arr = Array.CreateInstance(elemType, list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v is not null && elemType != typeof(object))
                        v = Convert.ChangeType(v, elemType, System.Globalization.CultureInfo.InvariantCulture);
                    arr.SetValue(v, i);
                }
                return arr;
            }
        }

        // Hashtable / OrderedDictionary 还原为 Hashtable。
        if (je.ValueKind == JsonValueKind.Object)
        {
            var ht = new Hashtable(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in je.EnumerateObject())
            {
                ht[prop.Name] = ReconstructFromJson(prop.Value, null);
            }
            return ht;
        }

        // 兜底: 原始文本。
        return je.GetRawText();
    }

    private static string DefaultUserGlobalDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.CurrentDirectory;
        return System.IO.Path.Combine(home, ".openshell");
    }

    /// <summary>JSON 反序列化记录。Per ADR-0047 §12.2.</summary>
    private sealed class VariableRecord
    {
        public object? Value { get; set; }
        public string Type { get; set; } = "object";
    }
}
