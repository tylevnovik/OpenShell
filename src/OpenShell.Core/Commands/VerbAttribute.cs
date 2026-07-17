namespace OpenShell.Commands;

/// <summary>Verb is the constrained prefix of a command name. Per ADR-0004, verbs are an enum.</summary>
public enum Verb
{
    Get,
    Set,
    New,
    Remove,
    Move,
    Copy,
    Rename,
    Invoke,
    Select,
    Where,
    Sort,
    Format,
    Out,
    Help,
    Exit,
    Clear,
    Push,
    Pop,
    // ADR-0039 §5: Provider 包生态命令动词。
    Find,
    Install,
    Update,
    Uninstall,
    Register,
    Unregister,
    Publish,
    // ADR-0048 Tier 1: Critical cmdlets (ForEach-Object, Write-*, Test/Resolve/Split/Join-Path).
    ForEach,
    Write,
    Test,
    Resolve,
    Split,
    Join,
    // ADR-0048 Tier 2: High-priority cmdlets (Get-Member, ConvertTo/From, Import/Export, Process, Web, Tee).
    ConvertTo,
    ConvertFrom,
    Import,
    Export,
    Tee,
    Start,
    Stop,
    Wait,
}

/// <summary>Specifies the verb and noun of a command, e.g. <c>[Verb("Get", Noun = "ChildItem")]</c>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VerbAttribute : Attribute
{
    public VerbAttribute(string verb) { Verb = verb; Noun = ""; }
    public string Verb { get; init; }
    public string Noun { get; init; }
    public string[] Aliases { get; init; } = [];
    /// <summary>If true, the command is hidden from GUI menus (pipeline-only).</summary>
    public bool PipelineOnly { get; init; }
}

/// <summary>Marks a property on the Args record as a command parameter.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ParameterAttribute : Attribute
{
    public int Position { get; init; } = -1;
    public string[] Aliases { get; init; } = [];
    public bool Mandatory { get; init; }
    public object? Default { get; init; }
    public string? HelpText { get; init; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum | AttributeTargets.Property, AllowMultiple = false)]
public sealed class DescriptionAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}
