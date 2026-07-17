# ADR-0052: Type System — Union / Generic / Optional 类型与严格模式

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M5+ (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0047 (Variable System), ADR-0050 (Modern Syntax, §11.2), ADR-0057 (Operator Overloading)
- **Implementation Status**: M5+ 已实现 (2026-07-08): TypeAnnotation AST 层级 (Primitive/Union/Optional/Generic)、TypeCoercer.ParseTypeAnnotation / Coerce(TypeAnnotation) / MatchesTypeAnnotation、ModernParser 类型引用解析 (支持 `|` / `?` / `<>`)、`is` 运算符支持复合类型、ExecutionContext.StrictMode 开关、fn 调用参数严格强制、返回类型 best-effort 推导 (InferReturnType)。

## Context

ADR-0050 §11.2 明确把"深度类型系统"推迟到独立 ADR。当前实现存在以下缺口：

1. **TypeCoercer.ResolveTypeAnnotation 仅处理 primitive**：`int` / `string` / `bool` 等基础类型通过 switch 返回 `System.Type`，但无法表达 `int | string`（联合）、`int?`（可选）、`List<int>`（泛型）等复合类型。
2. **类型注解被消费但忽略**：`fn add(a: int) -> int { }` 中 `ParseModernTypeReference` 只读取 identifier/dot 序列，返回 `TypeReference`，但 `-> RetType` 在 parser 注释中明写"消费但忽略，运行时不强制"。`int?` 的 `?` 甚至不会被消费（Question token 残留导致后续解析错误）。
3. **无 `is` 运算符的复合类型支持**：`BinaryOperator.Is` 已存在但 `IsType` 仅识别 `System.Type`，无法判断 `if $x is List<int> { }`。
4. **无严格模式**：所有类型注解都是 best-effort，无法在需要类型安全（库 / 静态分析）的场景下强制。

本 ADR 在不破坏现有动态语义的前提下，引入复合类型注解与 opt-in 严格模式。

## Decision

### 1. TypeAnnotation AST 层级

新增独立于 `TypeReference` 的 `TypeAnnotation` 抽象 record 层级（位于 `OpenShell.Parsing.Ast`）：

```
abstract record TypeAnnotation(SourceSpan Span)
record PrimitiveTypeAnnotation(string Name, SourceSpan)            // int / string / bool / ...
record UnionTypeAnnotation(IReadOnlyList<TypeAnnotation> Options)  // int | string
record OptionalTypeAnnotation(TypeAnnotation Inner)                // int?
record GenericTypeAnnotation(string Name, IReadOnlyList<TypeAnnotation> Args) // List<int> / Dict<string,int>
```

- `TypeReference`（旧）保留不变，向后兼容；其 `FullName` 字段在 parser 中存储完整注解字符串（如 `"List<int>"` / `"int|string"` / `"int?"`），由 `TypeCoercer.ParseTypeAnnotation` 在需要时解析为 `TypeAnnotation` 树。
- 选择"字符串延迟解析"而非在 parser 直接构造 `TypeAnnotation`，是为了不破坏 `ParameterDeclaration.Type: TypeReference?` 的 record 签名与所有调用点。

### 2. 复合类型语法

| 语法 | 含义 | 示例 |
|---|---|---|
| `int` | primitive | `fn f(x: int)` |
| `int?` | optional（接受 null） | `fn f(x: int?)` |
| `int \| string` | union（值可为任一类型） | `fn f(x: int \| string)` |
| `List<int>` | generic（参数化容器） | `fn f(x: List<int>)` |
| `Dict<string, int>` | multi-arg generic | `fn f(x: Dict<string, int>)` |
| `List<int>?` | generic + optional 组合 | `fn f(x: List<int>?)` |

- `ParseModernTypeReference` 扩展为消费 `?` (Question) / `|` (Pipe) / `<...>` (Lt..Gt)，仅在类型注解上下文（参数 `:` 后、返回 `->` 后、`is` 右侧）触发，不与表达式 `|` 管道 / `?` 三元 / `<` 比较 conflict。
- union 优先级最低（`|` 分隔），optional 优先级最高（trailing `?`），generic args 递归解析。

### 3. 运行时强制（TypeCoercer）

新增三个方法：

- `TypeAnnotation? ParseTypeAnnotation(string)` — 字符串 → `TypeAnnotation` 树。
- `object? Coerce(object?, TypeAnnotation)` — 按注解分发：
  - Primitive → 复用 `Coerce(value, ResolveTypeAnnotation(name))`。
  - Optional → null 透传；否则 `Coerce(value, Inner)`。
  - Union → 依次尝试每个 Option 的 coercion，第一个成功者返回；全部失败抛 `InvalidCastException`。
  - Generic → `List<T>` 验证值是 `IEnumerable` 并逐元素 `Coerce` 到 `T`（返回 `List<object?>`）；`Dict<K,V>` 验证 `IDictionary` 并按 V coercion。其他泛型名退化为"类型名匹配 + 透传"。
- `bool MatchesTypeAnnotation(object?, TypeAnnotation)` — 用于 `is` 与严格模式兼容性检查，不抛异常：
  - Primitive → `targetType.IsAssignableFrom(value.GetType())`。
  - Optional → null 或 `MatchesTypeAnnotation(value, Inner)`。
  - Union → 任一 Option 匹配。
  - Generic → `List<T>` → value is `IEnumerable`；`Dict<K,V>` → value is `IDictionary`。

### 4. `is` 运算符

- `is`（tokenizer 产出 `CmpIs`）在 ModernParser `ParseBinary` 中特判：右侧按类型引用解析（含 `?` / `|` / `<>`），构造 `BinaryExpression(left, BinaryOperator.Is, TypeReferenceExpression(typeRef))`。
- Evaluator `IsType(lv, rv)` 扩展：若 `rv` 是 `TypeReference`，取 `FullName` → `ParseTypeAnnotation` → `MatchesTypeAnnotation`；若 `rv` 是 `TypeAnnotation` 直接匹配；若 `rv` 是 `System.Type` 走原逻辑。

### 5. 严格模式（Strict Mode）

- `ExecutionContext.StrictMode`（默认 `false`），通过 `#lang strict` 或 `// @strict` pragma 开启（pragma 解析复用现有注释 / `#lang` 机制；本 ADR 仅定义开关语义，pragma 注入由宿主负责）。
- **fn 参数强制**：`BindParameters` 在 strict 模式下，对带类型注解的参数执行 `TypeCoercer.Coerce(value, ParseTypeAnnotation(param.Type.FullName))`；coercion 抛 `InvalidCastException` 即为类型错误（传播给调用方）。非 strict 模式维持现状（best-effort `ConvertValue`）。
- **fn 返回类型**：strict 模式下若 `-> RetType` 存在，对返回值执行 `Coerce`（best-effort，失败仅记录错误不中断，避免过度限制）。
- 默认动态语义不变，确保 PowerShell 兼容性与现有脚本零回归。

### 6. 返回类型推导（Type Inference）

- `Evaluator.InferReturnType(FunctionDefinitionStatement)` — best-effort：扫描 body 的 `ReturnStatement`，若所有返回表达式均为整数字面量 → `PrimitiveTypeAnnotation("int")`；否则返回 null（视为 `object`）。
- 推导结果存入 `ExecutionContext.InferredReturnTypes[name]`，供 LSP / 静态分析工具消费；运行时不强制推导类型（仅强制显式 `-> RetType`）。

## Costs

- **运行时开销**：strict 模式下每次 fn 调用多一次 `ParseTypeAnnotation`（可缓存）+ `Coerce`。非 strict 模式零开销（维持原路径）。`ParseTypeAnnotation` 结果可按字符串缓存到 `ConcurrentDictionary`（v1 未实现，标记为 open question）。
- **复杂度**：TypeAnnotation 层级 + parser 类型引用扩展增加约 300 行代码，但隔离在 TypeCoercer / ModernParser 局部，不污染 evaluator 主路径。
- **兼容性**：`TypeReference` 签名不变，所有现有调用点零修改。

## Alternatives

- **在 parser 直接构造 TypeAnnotation 并替换 TypeReference**：破坏 `ParameterDeclaration` record 签名，牵连 PowerShellParser / 所有 AST 构造点，回归风险高。否决。
- **基于 NDepend / Roslyn 的静态类型检查器**：与"运行时 coercion 表"模型冲突，且无法覆盖动态 PowerShell 语义。否决。
- **TypeScript 风格结构类型（structural typing）**：v1 采用名义类型（nominal），结构类型推迟到后续 ADR。

## Open Questions

1. `ParseTypeAnnotation` 字符串缓存：是否需要 `ConcurrentDictionary<string, TypeAnnotation>` 预热常见联合 / 泛型？
2. 泛型协变 / 逆变：`List<int>` 是否 assignable to `List<object>`？v1 否（不变），待后续 ADR。
3. 用户自定义类型（`type Point { ... }`，见 ADR-0057）如何参与 `is` 与 coercion？需 TypeRegistry 集成。
4. `#lang strict` pragma 的精确词法与作用域（文件级 / 块级）待 ADR-0050 §1 扩展定义。

## Constraints

- 不修改 `TypeReference` record 签名（向后兼容）。
- 不破坏非 strict 模式的动态语义与 PowerShell 兼容性。
- 类型注解解析仅在类型上下文触发，不得干扰表达式层 `|` / `?` / `<` 语义。
- 代码注释中文（遵循 codebase 约定）。
