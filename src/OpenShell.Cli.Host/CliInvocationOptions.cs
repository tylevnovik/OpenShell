using OpenShell.Security;

namespace OpenShell.Cli.Host;

/// <summary>CLI 顶层运行模式。</summary>
internal enum CliInvocationMode
{
    Interactive,
    Command,
    File,
    Help,
    Version,
}

/// <summary>在 Host 启动前完成验证的 CLI 参数快照。</summary>
internal sealed record CliInvocationOptions(
    CliInvocationMode Mode,
    string? CommandText,
    string? FilePath,
    bool SkipProfile,
    string? ProfilePath,
    bool StartIpcServer,
    string? SessionName,
    ExecutionPolicy? ExecutionPolicy);

/// <summary>CLI 参数解析结果；失败时不应创建 Host。</summary>
internal sealed record CliInvocationParseResult(CliInvocationOptions? Options, string? Error)
{
    public bool Succeeded => Options is not null && Error is null;

    public static CliInvocationParseResult Success(CliInvocationOptions options) => new(options, null);

    public static CliInvocationParseResult Failure(string error) => new(null, error);
}

/// <summary>无副作用的 CLI 顶层参数解析器。</summary>
internal static class CliInvocationParser
{
    public static CliInvocationParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? commandText = null;
        string? filePath = null;
        string? profilePath = null;
        string? sessionName = null;
        ExecutionPolicy? executionPolicy = null;
        var skipProfile = false;
        var startIpcServer = false;
        var showHelp = false;
        var showVersion = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                    showHelp = true;
                    break;

                case "--version":
                case "-v":
                    showVersion = true;
                    break;

                case "--noprofile":
                case "-noprofile":
                    skipProfile = true;
                    break;

                case "--profile":
                case "-profile":
                    if (!TryReadValue(args, ref index, argument, out profilePath, out var profileError))
                        return CliInvocationParseResult.Failure(profileError!);
                    break;

                case "--ipc-server":
                    startIpcServer = true;
                    break;

                case "--session":
                    if (!TryReadValue(args, ref index, argument, out sessionName, out var sessionError))
                        return CliInvocationParseResult.Failure(sessionError!);
                    break;

                case "--execution-policy":
                case "-executionpolicy":
                    if (!TryReadValue(args, ref index, argument, out var policyText, out var policyError))
                        return CliInvocationParseResult.Failure(policyError!);
                    if (!Enum.TryParse<ExecutionPolicy>(policyText, ignoreCase: true, out var parsedPolicy))
                    {
                        return CliInvocationParseResult.Failure(
                            $"Invalid execution policy '{policyText}'. Expected Restricted, RemoteSigned, Unrestricted, or Bypass.");
                    }
                    executionPolicy = parsedPolicy;
                    break;

                case "--command":
                case "-command":
                case "-c":
                    if (commandText is not null)
                        return CliInvocationParseResult.Failure($"Option '{argument}' was specified more than once.");
                    if (!TryReadValue(args, ref index, argument, out commandText, out var commandError))
                        return CliInvocationParseResult.Failure(commandError!);
                    break;

                case "--file":
                case "-file":
                case "-f":
                    if (filePath is not null)
                        return CliInvocationParseResult.Failure($"Option '{argument}' was specified more than once.");
                    if (!TryReadValue(args, ref index, argument, out filePath, out var fileError))
                        return CliInvocationParseResult.Failure(fileError!);
                    break;

                default:
                    return CliInvocationParseResult.Failure($"Unknown option or argument '{argument}'.");
            }
        }

        if (showHelp && showVersion)
            return CliInvocationParseResult.Failure("Options '--help' and '--version' cannot be used together.");
        if (showHelp)
            return CliInvocationParseResult.Success(Create(CliInvocationMode.Help));
        if (showVersion)
            return CliInvocationParseResult.Success(Create(CliInvocationMode.Version));
        if (commandText is not null && filePath is not null)
            return CliInvocationParseResult.Failure("Options '--command' and '--file' are mutually exclusive.");

        var mode = commandText is not null
            ? CliInvocationMode.Command
            : filePath is not null
                ? CliInvocationMode.File
                : CliInvocationMode.Interactive;
        return CliInvocationParseResult.Success(Create(mode));

        CliInvocationOptions Create(CliInvocationMode mode) => new(
            Mode: mode,
            CommandText: commandText,
            FilePath: filePath,
            SkipProfile: skipProfile,
            ProfilePath: profilePath,
            StartIpcServer: startIpcServer,
            SessionName: sessionName,
            ExecutionPolicy: executionPolicy);
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            value = null;
            error = $"Option '{option}' requires a value.";
            return false;
        }

        value = args[++index];
        error = null;
        return true;
    }
}

