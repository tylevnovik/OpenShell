using System.IO;

namespace OpenShell.TestUtils;

/// <summary>
/// 共享测试数据路径。Per ADR-0033: 测试数据位于 tests/TestData/。
/// 路径在测试运行时解析（基于当前工作目录向上查找 tests/TestData 目录）。
/// </summary>
public static class TestDataPaths
{
    private static readonly string s_root = ResolveRoot();

    /// <summary>测试数据根目录 (tests/TestData/)。</summary>
    public static string Root => s_root;

    /// <summary>sample.txt (内容 "hello world")。</summary>
    public static string SampleText => Path.Combine(s_root, "sample.txt");

    /// <summary>empty.txt (空文件)。</summary>
    public static string EmptyText => Path.Combine(s_root, "empty.txt");

    /// <summary>nested/ 子目录。</summary>
    public static string NestedDir => Path.Combine(s_root, "nested");

    /// <summary>nested/a.txt。</summary>
    public static string NestedA => Path.Combine(s_root, "nested", "a.txt");

    /// <summary>nested/b.txt。</summary>
    public static string NestedB => Path.Combine(s_root, "nested", "b.txt");

    /// <summary>脚本实例根目录 (tests/TestData/Scripts/)。Per script-e2e-audit.md §5.2.</summary>
    public static string ScriptsRoot => Path.Combine(s_root, "Scripts");

    /// <summary>模块脚本目录 (tests/TestData/Scripts/modules/)。</summary>
    public static string ModulesDir => Path.Combine(ScriptsRoot, "modules");

    /// <summary>独立脚本目录 (tests/TestData/Scripts/standalone/)。</summary>
    public static string StandaloneDir => Path.Combine(ScriptsRoot, "standalone");

    /// <summary>#lang 块脚本目录 (tests/TestData/Scripts/lang_blocks/)。</summary>
    public static string LangBlocksDir => Path.Combine(ScriptsRoot, "lang_blocks");

    /// <summary>math.osh 模块（export fn/const）。</summary>
    public static string MathModuleOsh => Path.Combine(ModulesDir, "math.osh");

    /// <summary>strings.osh 模块（export default）。</summary>
    public static string StringsModuleOsh => Path.Combine(ModulesDir, "strings.osh");

    /// <summary>legacy.ps1 PS 兼容模块。</summary>
    public static string LegacyModulePs1 => Path.Combine(ModulesDir, "legacy.ps1");

    /// <summary>hello.osh 简单端到端脚本。</summary>
    public static string HelloOsh => Path.Combine(StandaloneDir, "hello.osh");

    /// <summary>control_flow.osh 控制流综合脚本。</summary>
    public static string ControlFlowOsh => Path.Combine(StandaloneDir, "control_flow.osh");

    /// <summary>ps1_script.ps1 PS 脚本综合。</summary>
    public static string Ps1Script => Path.Combine(StandaloneDir, "ps1_script.ps1");

    /// <summary>mixed.osh #lang 块互操作脚本。</summary>
    public static string MixedLangOsh => Path.Combine(LangBlocksDir, "mixed.osh");
    public static string MainOsh => Path.Combine(StandaloneDir, "main.osh");

    private static string ResolveRoot()
    {
        // 从当前目录往上找 tests/TestData 目录，兼容 IDE 与 dotnet test 的工作目录差异。
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "tests", "TestData");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        // 退回到 repo root 的 tests/TestData（基于 csproj 位置）。
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "TestData"));
    }
}
