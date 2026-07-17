# ADR-0057: Operator Overloading — 受限运算符重载

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M5+ (Language Layer)
- **Decider**: Architecture
- **Supersedes**: ADR-0050 §11（"运算符不可重载"约束）
- **Related**: ADR-0047 (Variable System, 反射成员访问), ADR-0050 (Modern Syntax, §11.7), ADR-0052 (Type System, 自定义 type)
- **Implementation Status**: M5+ 已实现 (2026-07-08): OperatorOverloadResolver 反射查找 `op_*` 方法、Evaluator 二元运算 (`==`/`!=`/`<`/`>`/`<=`/`>=`/`+`/`-`/`*`/`/`/`%`) 优先尝试重载再回退内置、`type Name { ... }` 自定义类型定义语法、TypeDefinitionStatement AST、TypeRegistry 注册表。

## Context

ADR-0050 §11（约束）明确写道："运算符不可重载"。§11.7 把"是否允许运算符重载"列为 open question。本 ADR **覆盖** §11 的约束，在受限范围内允许运算符重载。

动机：

1. **.NET 互操作**：OpenShell 操作大量 .NET 对象（`DateTime` / `TimeSpan` / 自定义 POCO）。`$d1 - $d2` 期望返回 `TimeSpan`，`$p1 == $p2` 期望值相等而非引用相等。当前 Evaluator 对未知类型回退到字符串比较或返回 null，语义不符直觉。
2. **自定义类型**：ADR-0052 引入类型系统后，用户定义 `type Point { x: int; y: int }`，期望 `==` 按字段比较、`+` 按字段相加。无重载机制则自定义类型的运算符语义缺失。
3. **领域 DSL**：向量 / 复数 / 矩阵等数值领域需要 `+` / `*` 自然书写。

Python `__eq__` / `__add__` 模型（在类型上定义特殊方法）实现简单、语义清晰，且能复用 OpenShell 已有的反射成员访问（ADR-0047 §6）。本 ADR 采纳此模型。

## Decision

### 1. 覆盖 ADR-0050 §11 约束

- ADR-0050 §11"运算符不可重载"约束**作废**，以本 ADR 为准。
- 重载为"受限"：仅允许下表运算符；逻辑 / null 运算符保持内置不可重载。

### 2. 可重载运算符与方法名

| 运算符 | 方法名 | 签名 | 返回 |
|---|---|---|---|
| `==` `!=` | `op_Equal` | `(other) -> bool` | bool |
| `<` `>` `<=` `>=` | `op_Compare` | `(other) -> int` | 负/零/正 |
| `+` | `op_Add` | `(other) -> any` | 结果 |
| `-` | `op_Sub` | `(other) -> any` | 结果 |
| `*` | `op_Mul` | `(other) -> any` | 结果 |
| `/` | `op_Div` | `(other) -> any` | 结果 |
| `%` | `op_Mod` | `(other) -> any` | 结果 |

- `!=` 复用 `op_Equal` 结果取反；`<=` / `>=` / `<` / `>` 复用 `op_Compare` 的符号判定。
- **不可重载**：`&&` `||` `!` `?.` `??` `?:` `++`（逻辑 / null / 自增运算符保持内置语义，避免短路语义被破坏与副作用问题）。

### 3. 解析机制（OperatorOverloadResolver）

- `OperatorOverloadResolver`（`OpenShell.Variables`）静态类，对每个运算符提供 `TryXxx(object left, object right, out object? result)`：
  1. 取 `left.GetType()`，反射查找实例方法 `op_<Name>`（`BindingFlags.Public | Instance | IgnoreCase`），参数计数 1。
  2. 找到则 `Invoke(left, new object?[]{ right })`，返回 true + result。
  3. 未找到或抛异常返回 false（回退内置）。
- 对 .NET 类型，`op_Equal` 对应 `op_Equality` 静态运算符 / `Equals(object)`；`op_Compare` 对应 `IComparable.CompareTo`。v1 仅识别显式 `op_*` 命名方法（用户自定义），.NET 内置 `IComparable` 走 Evaluator 现有 `Compare` 路径。完整 `operator +` 反射（`op_Addition`）作为 open question。

### 4. Evaluator 集成

`EvaluateBinary` 在各运算符分支**优先**尝试重载，失败回退内置：

- `==` / `!=`（Eq/Equals, Ne/NotEquals）：`TryEqual(lv, rv)` → bool；失败回退 `Equals(lv, rv)`。
- `<` / `>` / `<=` / `>=`（Lt/Gt/Le/Ge）：`TryCompare(lv, rv)` → int；失败回退 `Compare(lv, rv)`。
- `+`/`-`/`*`/`/`/`%`：`TryAdd`/`TrySub`/`TryMul`/`TryDiv`/`TryMod` → 非空结果；失败回退 `Add`/`Subtract`/...。

回退保证内置类型（int / string / double）零行为变化。

### 5. 自定义类型定义语法

```
type Point {
    x: int;
    y: int;
    fn op_Equal(other: Point) -> bool { self.x == other.x && self.y == other.y }
    fn op_Add(other: Point) -> Point { /* ... */ }
}
```

- `type` 关键字（tokenizer 仍产出 Identifier，parser 在 statement 起始按 `Identifier("type") + Identifier + LBrace` 模式识别）。
- 成员：字段 `name: type;` 与方法 `fn name(params) [-> RetType] { body }`。
- AST：`TypeDefinitionStatement(Name, IReadOnlyList<TypeMember> Members, Span)`；`TypeMember` 抽象 + `FieldMember` / `MethodMember`。
- 注册：Evaluator 求值 `TypeDefinitionStatement` 时写入 `ExecutionContext.CustomTypes`（`TypeRegistry`）。

### 6. 实例化与 `self`

- v1 **不实现**自定义类型实例化（`new Point(...)` 构造与 `self` 绑定）。`type` 定义当前仅注册元数据，供未来 ADR 实现实例化与 `op_*` 方法调度。
- 运算符重载在 v1 实际生效路径为 **.NET 对象**通过反射调用其显式 `op_*` 方法；自定义 `type` 的 `op_*` 在实例化能力落地后自动生效（同一反射路径）。
- 此限制在 Open Questions 中标记，不影响重载机制本身。

### 7. 向后兼容

- 内置类型（int / string / bool / double / 数组）无 `op_*` 方法，走内置路径，行为零变化。
- 未声明 `op_*` 的自定义对象回退内置（`Equals` / `Compare`），行为与重载前一致。

## Costs

- **运行时开销**：每次二元运算多一次反射 `GetMethod` 查找。可在 `OperatorOverloadResolver` 内缓存 `(Type, methodName) -> MethodInfo?`（v1 未实现，open question）。
- **语义模糊**：`==` 对有 `op_Equal` 的类型走重载，对无者走引用相等。需文档明确。
- **复杂度**：Resolver ~120 行，Evaluator hook ~40 行，type 定义 parser ~70 行。

## Alternatives

- **全局运算符函数注册表**（Haskell typeclass 风格）：与 OpenShell 反射模型不匹配，否决。
- **C# 风格 `static operator +`**：要求 .NET 编译期支持，脚本层无法定义，仅适用 .NET 互操作；脚本自定义类型仍需 `op_*` 方法模型。v1 统一用 `op_*`。
- **完全禁止重载**（维持 ADR-0050 §11）：无法满足 .NET 互操作与自定义类型直觉语义，否决。

## Open Questions

1. 反射缓存：`(Type, op_Method) -> MethodInfo?` 缓存以消除热路径反射开销。
2. .NET `op_Addition` 等静态运算符的完整识别（当前仅识别实例 `op_*`）。
3. 自定义类型实例化：`new Name(...)` 构造、`self` 绑定、字段初始化——独立 ADR。
4. 重载与 ADR-0052 strict 模式的交互（类型检查时是否解析 `op_*` 签名）。
5. 隐式转换链：`op_Add(Point, Vector)` 中右操作数是否自动 coercion。

## Constraints

- 短路运算符（`&&` `||`）与 null 运算符（`??` `?.`）永不重载。
- 重载方法必须为实例方法、单个参数、`op_` 前缀命名。
- 内置类型行为零回归（回退路径保证）。
- 代码注释中文（遵循 codebase 约定）。
