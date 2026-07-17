using System.Text;
using OpenShell.Errors;
using OpenShell.Variables;

namespace OpenShell.Startup;

/// <summary>
/// Default <see cref="IProfileLoader"/> implementation. Per ADR-0041.
/// 按"用户全局 → 项目级"顺序加载 <c>profile.openshell</c>，逐行送入调用方提供的 lineExecutor 委托。
/// 命令行参数优先级：
/// <list type="bullet">
///   <item><c>--noprofile</c> 最高，命中时跳过所有 profile 加载。</item>
///   <item><c>--profile &lt;path&gt;</c> 显式指定时仅加载该文件。</item>
///   <item>未指定时按默认顺序加载。</item>
/// </list>
/// </summary>
public sealed class ProfileLoader : IProfileLoader
{
    private readonly IErrorStream _errors;
    private readonly IVariableRegistry? _variables;

    /// <summary>
    /// M1 阶段硬编码 <c>true</c>：致命错误（ParseError / ConfigurationError）中断脚本。
    /// M5 阶段改为从 <c>config.toml [profile] stopOnError</c> 读取（ADR-0022 配置加载留待 M5）。
    /// </summary>
    private const bool StopOnError = true;

    /// <summary>构造 ProfileLoader。</summary>
    /// <param name="errors">错误流，用于检测 lineExecutor 执行期间产生的 <see cref="ErrorRecord" /> 并应用中断策略。</param>
    /// <param name="variables">变量注册表，用于设置 <c>$PROFILE</c> 自动变量（可选，测试场景可省略）。</param>
    public ProfileLoader(IErrorStream errors, IVariableRegistry? variables = null)
    {
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _variables = variables;
    }

    /// <inheritdoc />
    public bool SkipProfile { get; set; }

    /// <inheritdoc />
    public string? CustomProfilePath { get; set; }

    /// <inheritdoc />
    public async Task<ProfileExecutionResult> ExecuteAsync(
        Func<string, Task> lineExecutor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lineExecutor);

        // 设置 $PROFILE 自动变量（Per ADR-0041 §7）。
        // 即使 --noprofile 也设置，便于用户查询 profile 文件位置。
        SetProfileVariable();

        // Per ADR-0047 §12.4: 启动时自动导入 ~/.openshell/variables.json 到 Global 作用域。
        // 在 profile 执行之前完成, 让 profile 可覆盖; 失败时仅 warning 不阻断启动 (per ADR-0041 §4)。
        // --noprofile 仍然执行自动导入 (变量持久化与 profile 解耦)。
        await AutoImportVariablesAsync(ct).ConfigureAwait(false);

        if (SkipProfile)
        {
            return new ProfileExecutionResult { Success = true };
        }

        var filesToLoad = ResolveProfileFiles();
        var executedFiles = new List<string>();
        var allErrors = new List<ErrorRecord>();
        var linesExecuted = 0;
        var success = true;

        foreach (var file in filesToLoad)
        {
            ct.ThrowIfCancellationRequested();

            // 找不到 profile 文件不算错误，静默跳过。
            if (!File.Exists(file)) continue;

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 文件存在但读取失败（权限等）：发 warning 但不阻塞后续 profile 加载。
                Console.Error.WriteLine($"[warn] failed to read profile '{file}': {ex.Message}");
                continue;
            }

            executedFiles.Add(file);

            int logicalLineNo = 0;
            foreach (var logicalLine in MergeContinuationLines(content))
            {
                logicalLineNo++;
                ct.ThrowIfCancellationRequested();

                var trimmed = logicalLine.Trim();
                // 跳过空行与 # 开头的注释行。
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("#")) continue;

                // 记录执行前的错误数量，命令结束后检查新增错误以应用中断策略。
                var errCountBefore = _errors.RecentErrors.Count;

                try
                {
                    await lineExecutor(trimmed).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 防御性 fallback：正常路径下 lineExecutor (DispatchAsync) 自己已写入 _errors。
                    _errors.Write(ErrorRecord.FromException(
                        ex,
                        operation: "profile",
                        phase: ErrorPhase.Operation,
                        suggestion: "review the profile script or restart with --noprofile"));
                }

                linesExecuted++;

                // 收集本次执行期间新增的错误，并按 ADR-0026 §4 / ADR-0041 §4 决定是否中断。
                var recent = _errors.RecentErrors;
                ErrorRecord? fatal = null;
                for (int i = errCountBefore; i < recent.Count; i++)
                {
                    allErrors.Add(recent[i]);
                    if (IsFatal(recent[i])) fatal = recent[i];
                }

                if (fatal is not null && StopOnError)
                {
                    Console.Error.WriteLine(
                        $"[warn] profile '{file}' aborted at line {logicalLineNo} due to fatal error: {fatal.Message}");
                    success = false;
                    break;
                }
            }

            if (!success) break;
        }

        return new ProfileExecutionResult
        {
            Success = success,
            ExecutedFiles = executedFiles,
            Errors = allErrors,
            LinesExecuted = linesExecuted,
        };
    }

    /// <summary>
    /// 解析本次应加载的 profile 文件列表。Per ADR-0041 §1.
    /// </summary>
    private IEnumerable<string> ResolveProfileFiles()
    {
        if (!string.IsNullOrWhiteSpace(CustomProfilePath))
        {
            // --profile <path> 显式指定：仅加载该文件，跳过默认查找。
            return new[] { CustomProfilePath! };
        }

        // 默认：用户全局 → 项目级（后者覆盖前者的副作用，如 cd / set-alias）。
        var userGlobal = Path.Combine(DefaultUserGlobalDir(), "profile.openshell");
        var project = Path.Combine(DefaultProjectDir(), "profile.openshell");
        return new[] { userGlobal, project };
    }

    /// <summary>
    /// 计算 <c>$PROFILE</c> 自动变量值。Per ADR-0041 §7.
    /// 首期简化：所有子字段返回用户全局 profile 路径（单一文件机制）。
    /// <c>--profile</c> 指定时 <see cref="ProfilePaths.CurrentProfile"/> 返回自定义路径，
    /// 其余子字段仍返回默认用户全局路径（保留字段语义）。
    /// </summary>
    public ProfilePaths GetProfilePaths()
    {
        var defaultUserGlobal = Path.Combine(DefaultUserGlobalDir(), "profile.openshell");
        var currentProfile = !string.IsNullOrWhiteSpace(CustomProfilePath)
            ? CustomProfilePath!
            : defaultUserGlobal;

        // Per ADR-0041 §7 简化说明：首期三个子字段均返回同一文件路径。
        return new ProfilePaths(
            currentProfile,
            allUsersAllHosts: defaultUserGlobal,
            currentUserAllHosts: defaultUserGlobal,
            currentUserCurrentHost: defaultUserGlobal);
    }

    /// <summary>设置 <c>$PROFILE</c> 自动变量到变量注册表（若注入）。</summary>
    private void SetProfileVariable()
    {
        if (_variables is null) return;
        _variables.SetAutomatic("PROFILE", GetProfilePaths());
    }

    /// <summary>
    /// 自动导入 <c>~/.openshell/variables.json</c> 到 Global 作用域。Per ADR-0047 §12.4.
    /// <para>
    /// 文件不存在时静默跳过; 解析失败时写 warning 到 Console.Error 但不阻断启动。
    /// 自动导入绕过 IsReadOnly 检查 (通过 SetAutomatic), 因为持久化的就是用户之前的 Global 变量。
    /// </para>
    /// </summary>
    private async Task AutoImportVariablesAsync(CancellationToken ct)
    {
        if (_variables is null) return;

        var path = Path.Combine(DefaultUserGlobalDir(), "variables.json");
        if (!File.Exists(path)) return;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"[warn] auto-import variables: failed to read '{path}': {ex.Message}");
            return;
        }

        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, VariableRecord>>(json);
            if (dict is null || dict.Count == 0) return;

            int imported = 0;
            foreach (var kv in dict)
            {
                // 跳过 scriptblock / IItem (per ADR-0047 §12.5) - 但反序列化结果不会是这些类型。
                // 反序列化为 JsonElement, 通过 InMemoryVariableRegistry.SetAutomatic 直接写入 Global。
                var value = ReconstructValue(kv.Value);
                if (_variables is InMemoryVariableRegistry mem)
                    mem.SetAutomatic(kv.Key, value ?? string.Empty);
                else
                    _variables.Set(kv.Key, value ?? string.Empty, VariableScope.Global);
                imported++;
            }

            if (imported > 0)
                Console.Error.WriteLine($"  auto-import: {imported} variable(s) loaded from '{path}'.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"[warn] auto-import variables: invalid JSON in '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// 反序列化 VariableRecord.Value (JsonElement) 为强类型值。Per ADR-0047 §12.3.
    /// 简化版: 仅还原常见基元, 其余退化为 string (完整版本见 ImportVariableCommand)。
    /// </summary>
    private static object? ReconstructValue(VariableRecord? record)
    {
        if (record?.Value is not System.Text.Json.JsonElement je) return record?.Value;
        try
        {
            return je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String when record.Type is "int" or "System.Int32" => je.GetInt32(),
                System.Text.Json.JsonValueKind.String when record.Type is "long" or "System.Int64" => je.GetInt64(),
                System.Text.Json.JsonValueKind.String when record.Type is "double" or "System.Double" => je.GetDouble(),
                System.Text.Json.JsonValueKind.String when record.Type is "bool" or "System.Boolean" => je.GetBoolean(),
                System.Text.Json.JsonValueKind.String when record.Type is "decimal" or "System.Decimal" => je.GetDecimal(),
                System.Text.Json.JsonValueKind.String => je.GetString(),
                System.Text.Json.JsonValueKind.Number => je.TryGetInt64(out var l) ? (object)l : je.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                System.Text.Json.JsonValueKind.Array => je.EnumerateArray().Select(e => ReconstructValue(new VariableRecord { Value = e, Type = "object" })).ToArray(),
                System.Text.Json.JsonValueKind.Object => je.GetRawText(),
                _ => je.GetRawText(),
            };
        }
        catch (FormatException)
        {
            return je.GetRawText();
        }
    }

    /// <summary>简化版 VariableRecord (用于自动导入反序列化)。</summary>
    private sealed class VariableRecord
    {
        public object? Value { get; set; }
        public string Type { get; set; } = "object";
    }

    private static string DefaultUserGlobalDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.CurrentDirectory;
        return Path.Combine(home, ".openshell");
    }

    private static string DefaultProjectDir()
        => Path.Combine(Environment.CurrentDirectory, ".openshell");

    /// <summary>
    /// 合并以反斜杠结尾的续行。Per ADR-0041 §6：行尾 <c>\</c> + 换行视为同一逻辑行。
    /// </summary>
    private static IEnumerable<string> MergeContinuationLines(string content)
    {
        var sb = new StringBuilder();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimEnd();
            if (line.EndsWith("\\"))
            {
                // 去掉续行符，与下一行物理行合并。
                sb.Append(line[..^1]);
            }
            else
            {
                sb.Append(line);
                yield return sb.ToString();
                sb.Clear();
            }
        }

        // 文件以续行符结尾（无后续行）：丢弃残余（已无意义）。
        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    /// <summary>
    /// 是否为致命错误。Per ADR-0026 §4 + ADR-0041 §4.
    /// <c>ParseError</c> / <c>ConfigurationError</c> 中断脚本；其余（<c>ItemNotFound</c> /
    /// <c>ProviderNotFound</c> / <c>PermissionDenied</c> 等）仅警告并继续。
    /// </summary>
    private static bool IsFatal(ErrorRecord err) =>
        err.Category is ErrorCategory.ParseError or ErrorCategory.ConfigurationError;
}
