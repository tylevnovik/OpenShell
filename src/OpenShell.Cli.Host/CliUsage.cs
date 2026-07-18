using System.Reflection;

namespace OpenShell.Cli.Host;

/// <summary>稳定、无 ANSI 的顶层 CLI 帮助与错误输出。</summary>
internal static class CliUsage
{
    private const string HelpText = """
        OpenShell - provider-aware command shell and file workspace

        Usage:
          openshell-cli [options]
          openshell-cli --command <text> [options]
          openshell-cli --file <path> [options]

        Options:
          -h, --help                     Show this help and exit.
          -v, --version                  Show the version and exit.
          -c, -Command, --command TEXT   Execute a command and exit.
          -f, -File, --file PATH         Execute a script file and exit.
              --noprofile                Do not load profile scripts.
              --profile PATH             Load a custom profile script.
              --session NAME             Use a named session.
              --ipc-server               Start the IPC server with the shell.
              --execution-policy LEVEL   Restricted, RemoteSigned, Unrestricted, or Bypass.

        Run 'get-help <command>' inside OpenShell for command-specific help.
        """;

    public static void WriteHelp(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(HelpText);
    }

    public static void WriteVersion(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine($"OpenShell {GetVersion()}");
    }

    public static void WriteError(TextWriter writer, string message)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine($"error: {message}");
        writer.WriteLine("Try 'openshell-cli --help' for usage.");
    }

    private static string GetVersion()
        => typeof(Program).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?.InformationalVersion
           ?? typeof(Program).Assembly.GetName().Version?.ToString(3)
           ?? "unknown";
}

