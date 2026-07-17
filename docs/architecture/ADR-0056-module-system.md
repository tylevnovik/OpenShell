# ADR-0056: 模块系统（ESM-style Module System）

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M4 (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0016 (Plugins), ADR-0050 (Modern Syntax), ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0047 (Variable System), ADR-0054 (Execution Policy)
- **Implementation Status**: M4 已实现 (2026-07-08): §1-§3 `export fn/const/default` / `import { } from` / `import * as from` 语法、`ModuleRegistry` 缓存表、`ModuleObject` 模块对象、`LoadModule` 首次加载+缓存命中、`CurrentModulePath` 上下文切换、Get-Module / Remove-Module 扩展支持脚本模块。

## Context

OpenShell 已有两种代码组织机制，但都存在局限：

1. **ADR-0016 插件模块**：通过 `Import-Module <dll>` 加载 .NET 程序集（ALC 隔离），注册 cmdlet / provider。这是**二进制插件**机制，不适合纯脚本代码组织。
2. **`using module` / `import` dot-source**（per ADR-0050 §10.1）：把目标脚本的语句在当前作用域执行，函数 / 变量注入当前作用域。这是**全局污染**语义，无法实现模块封装与显式导出。

用户在以下场景需要 ESM（ECMAScript Module）风格的脚本模块系统：

1. **代码封装**：库文件只想暴露部分函数 / 常量，内部辅助函数不污染调用方作用域。
2. **显式依赖**：`import { fetch, post } from "http.osh"` 明确声明依赖了哪些导出，便于静态分析与 tree-shaking。
3. **命名空间隔离**：`import * as Http from "http.osh"` 把整个模块打包成命名空间，避免命名冲突。
4. **默认导出**：`export default expr` 提供模块的"主入口"对象（如一个配置好的 HttpClient）。
5. **缓存与重入安全**：同一个模块文件被多次 import 时只加载一次，避免重复初始化副作用。

### 与 ADR-0016 插件模块的区别

| 维度 | ADR-0016 插件模块 | 本 ADR 脚本模块 |
|---|---|---|
| 加载对象 | .NET 程序集（.dll） | OpenShell 脚本（.osh / .ps1） |
| 加载机制 | ALC（AssemblyLoadContext）隔离 | 文件解析 + AST 求值 |
| 注册内容 | cmdlet 类 / provider 类 | 导出函数 / 常量 / 默认导出 |
| 隔离性 | ALC 边界隔离 | 共享 ExecutionContext 变量作用域 |
| 卸载 | ALC 卸载 + GC | 从 ModuleRegistry 移除缓存项 |
| 管理命令 | Import-Module / Get-Module / Remove-Module | import 语法 + Get-Module / Remove-Module 扩展 |
| DI 注册 | IPluginLoader | ModuleRegistry |

两者**并存**：插件模块用于扩展命令集（二进制），脚本模块用于组织脚本代码（源码）。Get-Module 同时列出两者。

### 与 `using module` dot-source 的区别

| 维度 | `using module` (ADR-0050 §10.1) | `import` (本 ADR) |
|---|---|---|
| 语义 | dot-source：在当前作用域执行目标脚本 | ESM：在模块上下文执行，仅导入导出 |
| 作用域 | 全局污染（所有函数 / 变量注入当前作用域） | 封装（仅显式导出的实体可见） |
| 缓存 | 无（每次重新执行） | 有（按文件路径缓存，首次加载后续命中） |
| 语法 | `using module "path.osh"` | `import { fn } from "path.osh"` / `import * as NS from "path.osh"` |
| 适用场景 | 简单脚本拼接、临时执行 | 库代码组织、显式依赖管理 |

`using module` 保留用于 PowerShell 兼容与简单 dot-source 场景，`import` 用于现代模块化代码组织。

### 设计目标

1. **ESM 语义对齐**：吸收 JavaScript ES Module 的 `export` / `import` 语法与缓存语义，降低前端 / Node.js 用户的迁移成本。
2. **封装性**：模块内部定义的变量 / 函数默认不暴露，仅 `export` 声明的实体可被导入。
3. **缓存安全**：同一路径的模块文件只加载一次，重复 import 返回缓存实例。
4. **与现有 ADR 协同**：复用 ExecutionContext、ScriptBlock、变量作用域、IExecutionPolicyService 等基础设施。
5. **与 ADR-0016 互补**：插件模块与脚本模块并列存在，Get-Module / Remove-Module 统一管理。

## Decision

引入 ESM 风格的 `export` / `import` 语法，基于 `ModuleRegistry` 缓存表与 `ModuleObject` 模块对象实现。以下分 8 节定义模块系统的语法、语义与求值规则。

### 1. export — 导出声明

#### 1.1 导出函数

```
# http.osh
export fn fetch(url: string) {
    let client = [System.Net.Http.HttpClient]::new()
    return client.GetStringAsync(url).Result
}

export fn post(url: string, body: string) {
    # ...
}
```

- `export fn name(params) { body }`：声明函数并导出。
- 语义：先按普通 `fn` 求值（注册 `ScriptBlock` 到当前作用域），再把 `ScriptBlock` 登记到当前模块的 `ExportedFunctions` 表。
- 导出的 `ScriptBlock` 捕获模块作用域的 `ExecutionContext`，调用时在模块作用域执行（闭包语义，per ADR-0046 §7）。

#### 1.2 导出常量

```
# config.osh
export const PI = 3.14159
export const VERSION = "1.0.0"
export const DEFAULT_TIMEOUT = 30
```

- `export const NAME = value`：声明常量并导出。
- 语义：先按普通赋值求值（`NAME = value` 注入当前作用域），再把值登记到 `ExportedConstants` 表。
- `value` 是任意表达式，求值后存储（不做冻结，导入方可修改，但不推荐）。

#### 1.3 默认导出

```
# client.osh
export default {
    base_url: "https://api.example.com",
    timeout: 30,
    retries: 3
}
```

- `export default expr`：导出模块的默认对象。
- 语义：求值 `expr`，结果存为模块的 `DefaultExport`。
- 每个模块至多一个 `export default`，重复声明后者覆盖前者。
- `expr` 通常是哈希字面量（`{ k: v }`，per ADR-0050 §4.1），但可以是任意表达式（函数 / 对象 / 标量）。

#### 1.4 混合导出

一个模块文件可同时包含命名导出与默认导出：

```
# math.osh
export const PI = 3.14159
export const E = 2.71828
export fn square(x) { return x * x }
export default {
    square: square,
    pi: PI
}
```

#### 1.5 模块上下文判定

`export` 声明仅在**模块上下文**中登记到 `ModuleRegistry`：

- **模块上下文**：`ExecutionContext.CurrentModulePath` 非 null（由 `LoadModule` 在加载模块时设置）。
- **非模块上下文**：直接执行脚本（如 REPL 顶层、`osh script.osh` 直接运行）时 `CurrentModulePath` 为 null，`export` 退化为普通声明（仅作用于当前作用域，不登记导出）。

这允许同一个 `.osh` 文件既能作为模块被 import，也能作为脚本直接运行（直接运行时 `export` 退化为 `fn` / `const` / 表达式语句）。

#### 1.6 AST 节点

```
ExportDeclarationAst(
    ExportKind Kind,          # Function / Constant / Default
    string? Name,             # Function/Constant 的导出名；Default 为 null
    Statement Inner,          # 实际的 FunctionDefinitionStatement / AssignmentStatement / ExpressionStatement
    SourceSpan Span) : Statement
```

`Inner` 是去掉 `export` 前缀后的原始声明，求值时复用现有 `EvaluateStatement` 逻辑。

### 2. import — 导入声明

#### 2.1 命名导入

```
import { fetch, post } from "http.osh"
fetch("https://example.com")
post("https://api.example.com", "data")
```

- `import { name1, name2 } from "module"`：从模块导入指定的命名导出。
- 语义：
  1. `LoadModule(module)` 加载模块（命中缓存或首次加载）。
  2. 遍历 `names`：优先从 `ExportedFunctions` 查找，其次 `ExportedConstants`。
  3. 找到则 `_ctx.Variables?.Set(name, value)` 注入当前作用域。
  4. 未找到则写 `ErrorRecord`（`ItemNotFound`，提示 `module has no export named 'name'`）。

#### 2.2 命名空间导入

```
import * as Http from "http.osh"
Http.fetch("https://example.com")
Http.post("https://api.example.com", "data")
```

- `import * as NS from "module"`：把模块的所有导出打包成 hashtable，绑定到 `NS` 变量。
- 语义：
  1. `LoadModule(module)` 加载模块。
  2. 构造 `Dictionary<string, object?>`，合并 `ExportedFunctions` 与 `ExportedConstants`。
  3. 若有 `DefaultExport`，加入键 `"default"`。
  4. `_ctx.Variables?.Set(NS, bag)` 注入当前作用域。
- 通过 `NS.name` 访问命名导出（per ADR-0050 §4.1 哈希点访问）。

#### 2.3 默认导入（约定）

当前实现**未**单独支持 `import default from "module"` 语法（ESM 的 `import expr from "mod"`）。默认导出通过命名空间导入访问：

```
import * as Mod from "client.osh"
let config = Mod.default
```

或通过命名导入访问（若默认导出同时在命名导出表中登记，当前实现未自动登记，需模块作者显式 `export const default = ...`，不推荐）。

**推荐模式**：模块作者把"默认对象"同时作为命名导出，用户用命名导入：

```
# module.osh
export const config = { base_url: "...", timeout: 30 }
export default config

# user.osh
import { config } from "module.osh"
```

未来版本可能增加 `import expr from "mod"` 语法糖。

#### 2.4 模块路径解析

- `module` 是字符串字面量，表示模块文件路径。
- 解析为绝对路径：`Path.GetFullPath(module)`（相对当前工作目录）。
- 文件必须存在，否则 `ItemNotFound` 错误。
- 按文件后缀选择 parser（per ADR-0050 §10.1）：`.osh` → ModernParser，`.ps1` → PowerShellParser，其他默认 PS。
- 缓存键是**规范化后的绝对路径**（`Path.GetFullPath`），确保不同相对路径形式指向同一模块时命中同一缓存。

#### 2.5 AST 节点

```
NamedImportAst(
    IReadOnlyList<string> Names,
    string ModulePath,
    SourceSpan Span) : Statement

NamespaceImportAst(
    string Namespace,
    string ModulePath,
    SourceSpan Span) : Statement
```

### 3. ModuleRegistry 与 ModuleObject

#### 3.1 ModuleRegistry

`ModuleRegistry` 是脚本模块的加载缓存表，通过 DI 容器注册为单例服务：

```
public sealed class ModuleRegistry
{
    private readonly ConcurrentDictionary<string, ModuleObject> _cache;

    public bool TryGet(string absolutePath, out ModuleObject? module);
    public void Register(ModuleObject module);
    public bool Remove(string absolutePath);
    public IReadOnlyCollection<ModuleObject> Loaded { get; }
    public void Clear();
}
```

- **缓存键**：`Path.GetFullPath(FilePath)` 规范化后的绝对路径（大小写不敏感，per `StringComparer.OrdinalIgnoreCase`）。
- **线程安全**：`ConcurrentDictionary` 支持并发 import。
- **DI 注册**：Host 启动时注册 `ModuleRegistry` 为单例（per ADR-0016 DI 容器模式）。若未注册，`import` 退化为错误（`ConfigurationError`）。

#### 3.2 ModuleObject

`ModuleObject` 是已加载模块的快照，immutable record：

```
public sealed record ModuleObject
{
    public required string Name { get; init; }                      # 模块名（默认文件名不含后缀）
    public required string FilePath { get; init; }                  # 模块文件绝对路径（缓存键）
    public IReadOnlyDictionary<string, object?> ExportedFunctions { get; init; }
    public IReadOnlyDictionary<string, object?> ExportedConstants { get; init; }
    public object? DefaultExport { get; init; }                     # export default 的值
    public DateTimeOffset LoadedAt { get; init; }                   # 加载时间戳
}
```

- **Name**：默认为 `Path.GetFileNameWithoutExtension(FilePath)`，可在 `export` 求值时覆盖。
- **ExportedFunctions**：`name → ScriptBlock`（可调用）。
- **ExportedConstants**：`name → value`（任意对象）。
- **DefaultExport**：`export default expr` 的值，无则为 null。
- **LoadedAt**：UTC 时间戳，用于 Get-Module 展示。

#### 3.3 LoadModule 加载流程

`LoadModule(string modulePath)` 是模块加载的核心方法：

1. **解析注册表**：`ResolveModuleRegistry()` 从 `Host.Services` 获取 `ModuleRegistry`。未注册则写 `ConfigurationError` 错误，返回 null。

2. **解析绝对路径**：`Path.GetFullPath(modulePath)`。路径无效则写 `InvalidArgument` 错误，返回 null。

3. **文件存在检查**：`File.Exists(absPath)`。不存在则写 `ItemNotFound` 错误，返回 null。

4. **缓存命中**：`registry.TryGet(absPath, out var cached)`。命中直接返回缓存对象，跳过解析与求值。

5. **首次加载**：
   a. 读取文件内容 `File.ReadAllText(absPath)`。
   b. 按后缀选择 parser 解析为 `ScriptBlockAst`。解析失败写 `ParseError`，返回 null。
   c. 预注册空 `ModuleObject`（placeholder）到注册表，让 `export` 声明求值时 `TryGet` 命中并增量更新。
   d. 保存当前 `CurrentModulePath`，设置 `_ctx.CurrentModulePath = absPath`（进入模块上下文）。
   e. 构造 `new Evaluator(_ctx)`，调用 `Execute(ast)` 求值模块体。复用当前 ExecutionContext 的变量 / 命令注册表。
   f. 求值过程抛 `OpenShellScriptException` 时写 `OperationFailed` 错误（不中断调用方）。
   g. `finally` 恢复 `CurrentModulePath = savedModulePath`。
   h. `registry.TryGet(absPath, out var loaded)` 取回已填充的模块对象，返回。

#### 3.4 模块作用域

模块求值复用调用方的 `ExecutionContext`，因此：

- 模块内定义的**非导出**变量 / 函数也注入到当前作用域（与 dot-source 一致）。
- 这是当前实现的简化：真正的模块隔离需要为每个模块创建独立 `ExecutionContext`，但会破坏闭包捕获与命令注册表共享。
- **建议**：模块作者用 `_` 前缀命名内部辅助函数（如 `_helper`），约定不导入 `_` 前缀的名字。
- **未来扩展**：可为每个模块创建子作用域 `ExecutionContext`，仅 `export` 实体可见，但需重新设计 `ScriptBlock` 闭包捕获机制（M5+ 考虑）。

### 4. CurrentModulePath 上下文

`ExecutionContext.CurrentModulePath` 是模块系统的关键上下文状态：

```
public sealed class ExecutionContext
{
    public string? CurrentModulePath { get; set; }  // 当前正在加载的模块绝对路径；null 表示非模块上下文
    // ...
}
```

- **设置时机**：`LoadModule` 在求值模块体前设置 `CurrentModulePath = absPath`，求值后恢复。
- **读取时机**：`EvaluateExportDeclaration` 读取 `CurrentModulePath` 判断是否在模块上下文，以及确定登记到哪个 `ModuleObject`。
- **嵌套模块**：模块 A import 模块 B 时，加载 B 的过程中 `CurrentModulePath` 被设为 B 的路径，B 的 `export` 登记到 B 的 `ModuleObject`；B 加载完毕恢复为 A 的路径，A 的 `export` 登记到 A。
- **非模块上下文**：直接执行脚本（REPL / `osh script.osh`）时 `CurrentModulePath` 为 null，`export` 退化为普通声明。

### 5. EvaluateExportDeclaration 求值

#### 5.1 求值流程

1. **求值内部声明**：`EvaluateStatement(exp.Inner)`，把实体（函数 / 变量）注入当前作用域。

2. **解析模块注册表**：`ResolveModuleRegistry()`。未注册或 `CurrentModulePath` 为空 → 返回 `innerResult`（退化为普通声明）。

3. **取出或创建模块对象**：`registry.TryGet(modulePath, out var existing)`。基于 `existing` 构造新的 `funcs` / `consts` 字典（复制现有导出），准备增量更新。

4. **按导出种类登记**：
   - `ExportKind.Function`：从变量表 `Resolve(exp.Name)` 取 `ScriptBlock`，加入 `funcs[exp.Name]`。
   - `ExportKind.Constant`：从变量表 `Resolve(exp.Name)` 取值，加入 `consts[exp.Name]`。
   - `ExportKind.Default`：`defaultExport = innerResult.Value`（求值内部表达式语句的结果）。

5. **构造新 ModuleObject 并注册**：用更新后的 `funcs` / `consts` / `defaultExport` 构造新 `ModuleObject`，`registry.Register(updated)` 覆盖缓存项。

6. **返回内部声明的求值结果**：`return innerResult`。

#### 5.2 增量更新设计

模块文件可包含多个 `export` 声明，每个声明求值时**增量更新**同一个 `ModuleObject`：

- 第一次 `export fn a` → 创建空 ModuleObject，加入 `a`。
- 第二次 `export fn b` → 取出已有 ModuleObject，复制 funcs，加入 `b`，注册新对象覆盖旧。
- 第三次 `export default expr` → 取出已有 ModuleObject，设置 `DefaultExport`，注册覆盖。

这种 builder 模式确保每个 `export` 声明独立工作，无需模块作者显式聚合。

### 6. Get-Module / Remove-Module 扩展

#### 6.1 Get-Module

`Get-Module` 命令扩展为同时列出插件模块与脚本模块（per ADR-0016 §6 + 本 ADR）：

- **插件模块**（IPluginLoader）：Name / Version / Providers / Commands / LoadedAt。
- **脚本模块**（ModuleRegistry）：Name / Exports / Path / LoadedAt。
- 两者分两段展示，插件在前，脚本在后（`--- Script Modules ---` 分隔）。
- 导出数 = `ExportedFunctions.Count + ExportedConstants.Count + (DefaultExport != null ? 1 : 0)`。
- 两者都为空时显示 `(no modules loaded)`。

#### 6.2 Remove-Module

`Remove-Module` 命令扩展为支持两种模块卸载：

1. **插件模块卸载**：通过 `IPluginLoader.TryGet(name)` 查找，找到则调用 `Unload`。
2. **脚本模块卸载**：通过 `ModuleRegistry.Loaded.FirstOrDefault(m => m.Name == name)` 查找，找到则 `registry.Remove(match.FilePath)`。
3. **优先级**：先尝试插件，失败再尝试脚本模块。
4. **反馈**：分别报告 `Removed plugin module: name` 或 `Removed script module: name`。

脚本模块卸载仅从缓存表移除，下次 import 会重新加载。已导入到调用方作用域的变量 / 函数**不**自动移除（与 PowerShell 模块卸载语义一致）。

### 7. 错误处理

#### 7.1 模块路径错误

| 场景 | 错误类别 | 消息 |
|---|---|---|
| 模块路径无效（无法 `GetFullPath`） | `InvalidArgument` | `import: invalid module path '...'` |
| 模块文件不存在 | `ItemNotFound` | `import: module file not found: ...` |
| `ModuleRegistry` 未在 DI 注册 | `ConfigurationError` | `import: ModuleRegistry is not registered in the host DI container.` |

#### 7.2 解析错误

| 场景 | 错误类别 | 消息 |
|---|---|---|
| 模块文件解析失败 | `ParseError` | `import {path}: parse error at line L, col C: {msg}` |

解析错误返回 null，不中断调用方（调用方继续执行后续语句）。

#### 7.3 求值错误

| 场景 | 错误类别 | 消息 |
|---|---|---|
| 模块体求值抛异常 | `OperationFailed` | `import {path}: {ex.Message}` |

模块体抛 `OpenShellScriptException` 时被捕获并写错误记录，不传播到调用方（隔离模块加载失败的影响）。

#### 7.4 导入错误

| 场景 | 错误类别 | 消息 |
|---|---|---|
| 命名导入的名字不存在 | `ItemNotFound` | `import: module '...' has no export named 'name'` |

未找到的命名导入写错误记录，但**不中断**其他名字的导入（继续尝试下一个 name）。

### 8. 执行策略集成

模块加载受 `IExecutionPolicyService` 把关（per ADR-0054 §5/§9）：

- `LoadModule` 在读取文件前查询 `IExecutionPolicyService`。
- 远程文件（`IsRemoteFile`）需通过执行策略检查。
- 策略拒绝时写 `PermissionDenied` 错误，返回 null。
- 若策略服务未注册（如纯 AST 求值场景），跳过检查保持向后兼容。

**注意**：当前 `LoadModule` 实现复用了 `EvaluateUsing` 的策略检查逻辑，但具体调用点需确认。模块加载是脚本执行的子集，应受同等策略约束。

## Consequences

1. **新增 3 个 AST 节点**：`ExportDeclarationAst` / `NamedImportAst` / `NamespaceImportAst` + `ExportKind` 枚举。
2. **新增 ModuleRegistry / ModuleObject**：脚本模块缓存表与模块对象，DI 注册为单例。
3. **ExecutionContext 扩展**：新增 `CurrentModulePath` 属性，标识当前模块加载上下文。
4. **Evaluator 新增 4 个方法**：`EvaluateExportDeclaration` / `EvaluateNamedImport` / `EvaluateNamespaceImport` / `LoadModule` + `ResolveModuleRegistry` 辅助方法。
5. **Get-Module / Remove-Module 扩展**：同时展示 / 卸载插件模块与脚本模块。
6. **模块作用域简化**：当前复用调用方 ExecutionContext，非导出变量也注入当前作用域。真正的模块隔离留作 M5+。
7. **默认导入语法缺失**：当前未支持 `import expr from "mod"`，默认导出通过 `import * as NS` 访问 `NS.default`。
8. **JIT 编译器排除**（per ADR-0058）：`import` / `export` 是顶层语句，含副作用（模块加载 / 变量注入），不被 `ExpressionCompiler` 编译。
9. **缓存键规范化**：使用 `Path.GetFullPath` 规范化路径，确保不同相对路径形式指向同一模块时命中缓存。

## Open Questions

1. **模块作用域隔离**：当前模块求值复用调用方 ExecutionContext，非导出变量污染调用方作用域。未来是否为每个模块创建独立子作用域 ExecutionContext？（M5+ 考虑，需重新设计 ScriptBlock 闭包捕获）
2. **默认导入语法**：`import expr from "mod"` 语法糖，直接绑定默认导出到变量。当前需 `import * as NS; NS.default` 两步。
3. **动态导入**：`import("mod")` 异步动态导入，返回 Promise/Task。当前 import 是静态顶层语句，不支持运行时动态加载。
4. **模块解析策略**：当前 `module` 必须是文件路径。未来是否支持 bare specifier（如 `import { x } from "http"`）+ 模块搜索路径（NODE_PATH 风格）？
5. **循环依赖**：模块 A import B，B import A 时，当前实现会递归加载（A 加载中再 import A 命中缓存，但 A 可能尚未 export 任何实体）。是否需要检测循环依赖并报错？当前未处理，可能导出空值。
6. **模块重载**：`Remove-Module` 后再 `import` 会重新加载，但已导入到调用方作用域的旧引用不更新。是否需要支持模块热重载？暂不。
7. **export 类型注解**：`export fn fetch(url: string): string` 的类型注解是否参与导出表元数据？当前仅存储 ScriptBlock，未保留类型签名。
