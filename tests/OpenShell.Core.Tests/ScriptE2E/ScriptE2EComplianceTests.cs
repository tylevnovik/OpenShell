#nullable enable
// 脚本实例端到端合规测试套件（Script E2E Compliance Tests）
// 设计原则：
//   1. 真实文件加载：通过 File.ReadAllText → Parse → Execute 模式加载 tests/TestData/Scripts/ 下的脚本实例。
//   2. 覆盖 ADR-0050 §10（互操作）、ADR-0056（模块系统）、ADR-0054（执行策略）的端到端路径。
//   3. 已实现且可用的功能用 [Fact]（必须通过）。
//   4. DI 未接线或功能有缺陷的用 [Fact(Skip="pending T-XXX")]，实现后移除 Skip。
//   5. 修复进度由 docs/script-e2e-tasks.md 追踪，本套件提供机械化验证。

using System.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.Errors;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using OpenShell.Runtime;
using OpenShell.TestUtils;
using OpenShell.Variables;
using ExecutionContext = OpenShell.Runtime.ExecutionContext;
using Xunit;

namespace OpenShell.Core.Tests.ScriptE2E;

/// <summary>
/// 脚本实例端到端合规测试。覆盖 import/export/模块系统/#lang 块执行/ExecutionPolicy 的端到端路径。
/// 修复任务清单见 docs/script-e2e-tasks.md。
/// </summary>
public class ScriptE2EComplianceTests
{
    private static ExecutionContext NewContext()
    {
        // 构造带完整 DI 的 Host：注册 ModuleRegistry + IExecutionPolicyService，供 import/export 使用。
        var tempDir = new TempDir();
        var builder = new TestHostBuilder(tempDir);
        var provider = builder.Build();
        var host = provider.GetRequiredService<IHost>();
        var variables = provider.GetRequiredService<IVariableRegistry>();
        var errors = provider.GetRequiredService<IErrorStream>();
        // 显式设置 Bypass 策略，避免被其他测试设置的 Process scope 环境变量污染（Per T-230 修复）。
        var policy = provider.GetRequiredService<OpenShell.Security.IExecutionPolicyService>();
        policy.SetPolicy(OpenShell.Security.ExecutionPolicy.Bypass, OpenShell.Security.ExecutionPolicyScope.Process);
        return new ExecutionContext(variables: variables, errors: errors, host: host);
    }

    // 从字符串执行现代语法脚本（可包含 import 语句）。
    private static object? EvalModern(string source, ExecutionContext? ctx = null)
    {
        ctx ??= NewContext();
        var ast = ModernParser.Parse(source);
        return new Evaluator(ctx).Execute(ast).Value;
    }

    // 从文件加载并执行脚本（按后缀选择 parser）。
    private static object? EvalScriptFile(string path, ExecutionContext? ctx = null)
    {
        ctx ??= NewContext();
        // 设置 CurrentModulePath，使脚本内的 import 相对路径能相对于脚本文件解析（Per T-206）。
        ctx.CurrentModulePath = Path.GetFullPath(path);
        var source = File.ReadAllText(path);
        var ext = Path.GetExtension(path);
        ScriptBlockAst ast = string.Equals(ext, ".osh", StringComparison.OrdinalIgnoreCase)
            ? ModernParser.Parse(source, path)
            : PowerShellParser.Parse(source, path);
        return new Evaluator(ctx).Execute(ast).Value;
    }

    // 构造 import 语句字符串（路径用正斜杠避免转义问题）。
    private static string ImportStmt(string path)
        => $"import \"{path.Replace('\\', '/')}\"";

    // =========================================================================
    // §独立脚本文件加载
    // =========================================================================

    [Fact]
    public void S_Standalone_HelloOsh_Loads()
    {
        // 加载 hello.osh 端到端脚本，验证基本赋值、算术、字符串插值。
        var ctx = NewContext();
        var result = EvalScriptFile(TestDataPaths.HelloOsh, ctx);
        result.Should().Be("Hello, World!");
    }

    [Fact]
    public void S_Standalone_ControlFlowOsh_Loads()
    {
        // 加载 control_flow.osh 控制流综合脚本，验证 if/while/for-in/try-catch。
        // T-205 修复：ParseTry 不再消费 catch 块后的换行，避免吞掉后续语句。
        var ctx = NewContext();
        var result = EvalScriptFile(TestDataPaths.ControlFlowOsh, ctx);
        // 1..10 中偶数之和（2+4+6+8+10=30）。
        result.Should().Be(30L);
    }

    [Fact]
    public void S_Standalone_Ps1Script_Loads()
    {
        // 加载 ps1_script.ps1 PS 语法综合脚本，验证 foreach/function/数组。
        var ctx = NewContext();
        var result = EvalScriptFile(TestDataPaths.Ps1Script, ctx);
        // 1+2+3+4+5 = 15
        result.Should().Be(15L);
    }

    // =========================================================================
    // §import 文件加载（T-211/T-212）
    // =========================================================================

    [Fact]
    public void S_ImportOshFile_SideEffect_Load()
    {
        // ADR-0050 §10.1: import "file.osh" 副作用加载（dot-source 语义）。
        // 加载 hello.osh 后，$greeting 变量应注入当前作用域。
        var ctx = NewContext();
        EvalModern(ImportStmt(TestDataPaths.HelloOsh) + "; $greeting", ctx);
        var greeting = ctx.Variables!.Resolve("greeting");
        greeting.Should().Be("Hello, World!");
    }

    [Fact]
    public void S_ImportPs1File_SideEffect_Load()
    {
        // ADR-0050 §10.1: import "file.ps1" 按后缀选 PowerShellParser。
        // 加载 ps1_script.ps1 后，$sum 变量应注入当前作用域。
        var ctx = NewContext();
        EvalModern(ImportStmt(TestDataPaths.Ps1Script) + "; $sum", ctx);
        var sum = ctx.Variables!.Resolve("sum");
        sum.Should().Be(15L);
    }

    [Fact]
    public void S_ImportNonexistentFile_ReportsError()
    {
        // import 不存在的文件应在错误流写入 ItemNotFound 错误。
        var ctx = NewContext();
        EvalModern(ImportStmt(Path.Combine(TestDataPaths.ScriptsRoot, "nonexistent.osh")), ctx);
        var errors = ctx.Errors as InMemoryErrorStream;
        errors.Should().NotBeNull();
        errors!.RecentErrors.Should().Contain(r =>
            r.Category == ErrorCategory.ItemNotFound && r.Message.Contains("not found"));
    }

    // =========================================================================
    // §模块系统（T-213~T-217）— 需要 ModuleRegistry（T-200 完成后可用）
    // =========================================================================

    [Fact]
    public void S_NamedImport_Function()
    {
        // ADR-0056 §2: import { add } from "math.osh" 命名导入函数。
        var ctx = NewContext();
        var result = EvalModern($"import {{ add }} from \"{TestDataPaths.MathModuleOsh.Replace('\\', '/')}\"; add(3, 4)", ctx);
        result.Should().Be(7);
    }

    [Fact]
    public void S_NamedImport_Constant()
    {
        // ADR-0056 §2: import { PI } from "math.osh" 命名导入常量。
        var ctx = NewContext();
        EvalModern($"import {{ PI }} from \"{TestDataPaths.MathModuleOsh.Replace('\\', '/')}\"", ctx);
        var pi = ctx.Variables!.Resolve("PI");
        pi.Should().Be(3.14159265358979);
    }

    [Fact]
    public void S_NamespaceImport()
    {
        // ADR-0056 §2: import * as Math from "math.osh" 命名空间导入。
        var ctx = NewContext();
        EvalModern($"import * as Math from \"{TestDataPaths.MathModuleOsh.Replace('\\', '/')}\"", ctx);
        var math = ctx.Variables!.Resolve("Math") as System.Collections.IDictionary;
        math.Should().NotBeNull();
        math!.Contains("add").Should().BeTrue();
        math.Contains("PI").Should().BeTrue();
        math.Contains("square").Should().BeTrue();
    }

    [Fact]
    public void S_ExportDefault()
    {
        // ADR-0056 §1: export default expr 默认导出。
        var ctx = NewContext();
        EvalModern($"import * as Str from \"{TestDataPaths.StringsModuleOsh.Replace('\\', '/')}\"", ctx);
        var str = ctx.Variables!.Resolve("Str") as System.Collections.IDictionary;
        str.Should().NotBeNull();
        str!.Contains("default").Should().BeTrue();
        str["default"].Should().Be("hello from strings module");
    }

    [Fact]
    public void S_ModuleRegistry_CacheDedup()
    {
        // ADR-0056 §3: 同一文件多次 import 只加载一次（缓存去重）。
        var ctx = NewContext();
        var path = TestDataPaths.MathModuleOsh.Replace('\\', '/');
        EvalModern($"import {{ PI }} from \"{path}\"; import {{ add }} from \"{path}\"", ctx);
        var registry = ctx.Host!.Services!.GetService(typeof(OpenShell.Modules.ModuleRegistry)) as OpenShell.Modules.ModuleRegistry;
        registry.Should().NotBeNull();
        registry!.Loaded.Should().HaveCount(1);
    }

    [Fact]
    public void S_ModuleRegistry_Remove()
    {
        // ADR-0056 §3: ModuleRegistry.Remove 移除缓存后，下次 import 重新加载。
        var ctx = NewContext();
        var path = TestDataPaths.MathModuleOsh.Replace('\\', '/');
        EvalModern($"import {{ PI }} from \"{path}\"", ctx);
        var registry = ctx.Host!.Services!.GetService(typeof(OpenShell.Modules.ModuleRegistry)) as OpenShell.Modules.ModuleRegistry;
        registry.Should().NotBeNull();
        var absPath = System.IO.Path.GetFullPath(TestDataPaths.MathModuleOsh);
        registry!.Remove(absPath).Should().BeTrue();
        registry.Loaded.Should().HaveCount(0);
    }

    // =========================================================================
    // §lang 块执行（T-220/T-221）
    // =========================================================================

    [Fact]
    public void S_LangBlock_Ps1_Function_DefineAndCall()
    {
        // ADR-0050 §1.3: #lang ps1 { } 块内定义的函数注入当前作用域，块外可调用。
        var ctx = NewContext();
        var result = EvalModern("#lang ps1 { function Foo { 'bar' } }\nFoo", ctx);
        result.Should().Be("bar");
    }

    [Fact]
    public void S_LangBlock_Mixed_File_Load()
    {
        // 加载 mixed.osh 文件，验证 #lang ps1 块执行 + 块外现代语法执行。
        var ctx = NewContext();
        var result = EvalScriptFile(TestDataPaths.MixedLangOsh, ctx);
        result.Should().Be("Modern: World");
    }

    // =========================================================================
    // §ExecutionPolicy（T-230/T-231）— 需要 TestHostBuilder 注册 IExecutionPolicyService（T-201）
    // =========================================================================

    [Fact]
    public void S_ExecutionPolicy_Restricted_BlocksImport()
    {
        // ADR-0054 §5: Restricted 策略禁止脚本执行，import 应被拦截，写入 PermissionDenied 错误。
        var ctx = NewContext();
        var policy = ctx.Host!.Services!.GetRequiredService<OpenShell.Security.IExecutionPolicyService>();
        policy.SetPolicy(OpenShell.Security.ExecutionPolicy.Restricted, OpenShell.Security.ExecutionPolicyScope.Process);
        EvalModern(ImportStmt(TestDataPaths.HelloOsh), ctx);
        var errors = ctx.Errors as InMemoryErrorStream;
        errors.Should().NotBeNull();
        errors!.RecentErrors.Should().Contain(r =>
            r.Category == ErrorCategory.PermissionDenied && r.Message.Contains("ExecutionPolicy"));
    }

    [Fact]
    public void S_ExecutionPolicy_Bypass_AllowsImport()
    {
        // ADR-0054 §5: Bypass 策略无限制，import 应放行。
        var ctx = NewContext();
        var policy = ctx.Host!.Services!.GetRequiredService<OpenShell.Security.IExecutionPolicyService>();
        policy.SetPolicy(OpenShell.Security.ExecutionPolicy.Bypass, OpenShell.Security.ExecutionPolicyScope.Process);
        EvalModern(ImportStmt(TestDataPaths.HelloOsh) + "; $greeting", ctx);
        var greeting = ctx.Variables!.Resolve("greeting");
        greeting.Should().Be("Hello, World!");
    }

    // =========================================================================
    // §跨语法互操作（T-240/T-241）
    // =========================================================================

    [Fact]
    public void S_CrossSyntax_LangBlock_Interop()
    {
        // .osh 文件内 #lang ps1 { } 嵌入 PS 函数，块外现代语法调用。
        // mixed.osh 定义 Legacy-Greet 函数后，块外现代语法调用它。
        var ctx = NewContext();
        var result = EvalScriptFile(TestDataPaths.MixedLangOsh, ctx);
        result.Should().Be("Modern: World");
        // 块内定义的函数应注入当前作用域。
        var fn = ctx.Variables!.Resolve("Legacy-Greet");
        fn.Should().NotBeNull("块内 PS 函数应注入当前作用域");
    }

    [Fact]
    public void S_CrossSyntax_OshImportPs1()
    {
        // .osh 文件 import .ps1 文件，验证跨语法互操作。
        // import legacy.ps1 后，Add-WithTax 函数应可用。
        var ctx = NewContext();
        EvalModern(ImportStmt(TestDataPaths.LegacyModulePs1) + "; Add-WithTax 100 0.1", ctx);
        var fn = ctx.Variables!.Resolve("Add-WithTax");
        fn.Should().NotBeNull("PS 模块函数应通过 import 注入作用域");
    }

    // =========================================================================
    // §综合场景（T-250/T-251）
    // =========================================================================

    [Fact]
    public void S_MultiFile_Project()
    {
        // 主脚本 import 多个模块，验证多文件项目结构。
        // main.osh import strings.osh，调用导出函数。
        var ctx = NewContext();
        var result = EvalScriptFile(TestDataPaths.MainOsh, ctx);
        result.Should().Be("RESULT");
    }

    [Fact]
    public void S_ScriptFile_ParseError_ReportsError()
    {
        // 语法错误的脚本文件应在解析时抛出 ParserException。
        var tempDir = new TempDir();
        var badScript = Path.Combine(tempDir.FullPath, "bad.osh");
        File.WriteAllText(badScript, "fn broken( { }");
        var act = () => EvalScriptFile(badScript);
        act.Should().Throw<ParserException>();
    }
}
