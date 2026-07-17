# ADR-0055: 高级模式匹配（Advanced Pattern Matching）

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M4 (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0050 (Modern Syntax), ADR-0047 (Variable System)
- **Implementation Status**: M4 已实现 (2026-07-08): §1-§6 全部 8 种模式（Wildcard / Literal / Type / Destructure / Range / Guard / Or / As）、`MatchPattern` 递归求值器、hash/array 解构含 `...rest`、范围含/不含端点、`if` 守卫、`|` OR 模式、`as` 绑定、与旧式 `Expression? Pattern` 向后兼容。

## Context

ADR-0050 §5.2 在现代语法（`.osh`）中引入了 `match expr { pattern => arm }` 表达式，但初始实现仅支持**字面量模式**与 `_` 通配——`match` 臂的模式是一个普通 `Expression`，求值后与 subject 做 `Equals` 比较。这对简单分支足够，但用户在以下场景需要更强的模式表达能力：

1. **解构聚合数据**：从 hashtable / 数组中一次性提取多个字段，避免 `$x.Name` / `$x.Age` 反复访问。Python 的序列解包 `a, b = pair`、Rust 的 `struct` / `enum` 解构、JavaScript 的 `{ name, age } = obj` 都证明了解构是现代语言的刚需。
2. **范围匹配**：`match score { 90..=100 => "A", 80..<90 => "B", _ => "C" }`，比连续 `if score >= 90 && score <= 100` 更紧凑，比 switch 的 fall-through 更安全。Rust / Swift 都有范围模式。
3. **守卫条件**：`match n { 0 => "zero", x if x > 0 => "positive", _ => "negative" }`，在模式基础上加额外布尔条件，比纯字面量更灵活。Rust / Scala 都支持 `if guard`。
4. **多值合并臂**：`match color { "red" | "crimson" => "stop", _ => "go" }`，避免重复书写相同 arm。Rust / ML 系语言都用 `|` 表达 OR。
5. **绑定匹配值**：`match err { [System.Exception] as e => e.Message, _ => "unknown" }`，在匹配成功后把 subject 绑定到命名变量供 arm 体使用。Rust `@` 绑定、Scala `as` 都解决此需求。
6. **类型分支**：`catch [System.IO.FileNotFoundException]` 风格的类型分支在 `match` 中同样有用，用于区分异常 / 联合类型。

### 与 ADR-0050 §5.2 的关系

ADR-0050 §5.2 定义的 `match` 臂结构是：

```
MatchArm(Expression? Pattern, Expression Body)
```

`Pattern` 是普通 `Expression`（字面量模式），`null` 表示 `_`（default）。这一设计**没有**为高级模式预留扩展点。本 ADR 在不破坏旧式语义的前提下，新增 `AdvancedPattern` 字段：

```
MatchArm(Expression? Pattern, Expression Body, PatternAst? AdvancedPattern = null)
```

- `AdvancedPattern` 非 null 时优先走高级模式匹配（`MatchPattern`）。
- `AdvancedPattern` 为 null 时回退到旧式 `Pattern` 字面量比较（向后兼容）。
- 简单字面量模式在解析时**同时**填充两个字段（`legacyPattern = lp.Value`），确保两种求值路径结果一致。

### 设计目标

1. **Rust 风格**：吸收 Rust `match` 的解构 / 范围 / 守卫 / OR / `@` 绑定语义，语法尽量对齐。
2. **非 fall-through**：每个 arm 匹配后立即返回，无需 `break`（per ADR-0050 §5.2）。
3. **绑定作用域**：解构 / `as` 绑定的变量在当前 arm 的 `Body` 作用域内可见，arm 结束后变量仍留在当前作用域（与 PowerShell 变量作用域规则一致，per ADR-0047 §2）。
4. **向后兼容**：旧式字面量模式继续工作，不破坏已有 `.osh` 脚本。
5. **AST 同构**：所有模式编译到 `PatternAst` 层次，evaluator 通过 `MatchPattern` 递归求值，不感知语法来源。

## Decision

引入 `PatternAst` 抽象层次，支持 8 种模式：Wildcard / Literal / Type / Destructure / Range / Guard / Or / As。以下分 6 节定义各模式的语法、语义与求值规则。

### 1. 基础模式：Wildcard / Literal / Type

#### 1.1 通配模式 `_`

```
match x {
    _ => "default"
}
```

- `_` 永远匹配，不绑定任何变量。
- 通常作为最后一个 arm 的兜底（per ADR-0050 §5.2）。
- AST：`WildcardPattern(Span)`。

#### 1.2 字面量模式

```
match color {
    "red"    => "stop"
    "yellow" => "caution"
    42       => "the answer"
    true     => "yes"
}
```

- 求值模式表达式后与 subject 做 `Equals(subject, litVal)` 比较。
- 支持所有字面量：字符串 / 数字 / 布尔 / null。
- 字符串比较默认 `OrdinalIgnoreCase`（per ADR-0047 §6 / ADR-0050 §2.3）。
- AST：`LiteralPattern(Expression Value, Span)`。`Value` 是包装的字面量表达式。

#### 1.3 类型模式

```
match err {
    [System.IO.FileNotFoundException] => "file missing"
    [System.UnauthorizedAccessException] => "denied"
    [System.Exception] => "other"
}
```

- `[Type]` 语法：方括号包裹类型引用（与 PowerShell `[Type]` 字面量一致）。
- 求值：`type.IsAssignableFrom(subject.GetType())`，subject 为 null 时返回 false。
- 类型解析通过 `ResolveType(TypeReference)`（per ADR-0047 §3 类型解析机制）。
- arm 顺序敏感：更具体的类型应放前面（与 catch 顺序一致，per ADR-0045 §7）。
- AST：`TypePattern(TypeReference Type, Span)`。

### 2. 解构模式：Destructure

#### 2.1 哈希解构 `{ name, age }`

```
match user {
    { name, age } => "$name is $age years old"
    _ => "unknown"
}
```

- subject 必须是 `IDictionary`（Hashtable / Dictionary<string, object?> 等）。
- 每个字段名 `name` 必须存在于字典中，否则整个模式不匹配。
- 匹配成功后，每个字段值绑定到当前作用域的同名变量。
- AST：`DestructurePattern(Kind: Hash, Fields: [DestructureField(name)], Rest: null, Span)`。

#### 2.2 哈希解构 with `...rest`

```
match config {
    { host, port, ...rest } => {
        # host / port 已绑定，rest 收集剩余键值
        apply_config(host, port)
        log_extra(rest)
    }
}
```

- `...rest` 必须是最后一个元素，把字典中**未在字段列表中**的键值收集到新 `Dictionary<string, object?>`，绑定到 `rest` 变量。
- 若无剩余键，`rest` 是空字典。
- AST：`DestructurePattern(Kind: Hash, Fields: [...], Rest: "rest", Span)`。

#### 2.3 数组解构 `[a, b, ...rest]`

```
match point {
    [x, y] => "2D: ($x, $y)"
    [x, y, z] => "3D: ($x, $y, $z)"
    [first, ...rest] => "head: $first, tail: $rest"
    _ => "empty"
}
```

- subject 必须是 `IList` 或 `IEnumerable`（非 string）。
- **位置绑定**：按顺序取元素，`Fields[i]` 绑定到 `items[i]`。
- 元素不足时整个模式不匹配（`i >= items.Count` 返回 false）。
- 多余元素被忽略，除非用 `...rest` 收集。
- `...rest` 收集剩余元素为 `object[]`，绑定到 `rest` 变量。
- AST：`DestructurePattern(Kind: Array, Fields: [...], Rest: "rest"?, Span)`。

#### 2.4 解构求值规则

`MatchDestructure(DestructurePattern dp, object? subject)`：

1. **Hash 分支**：
   - subject 不是 `IDictionary` → 返回 false。
   - 遍历 `dp.Fields`：若 `dict.Contains(field.Name)` 为 false → 返回 false；否则 `_ctx.Variables?.Set(field.Name, dict[field.Name])`。
   - 若 `dp.Rest` 非 null：构造 `Dictionary<string, object?>`，收集不在 `dp.Fields` 中的键值，`_ctx.Variables?.Set(dp.Rest, rest)`。
   - 返回 true。

2. **Array 分支**：
   - subject 为 null → 返回 false。
   - 收集元素：`IList` 直接遍历；`IEnumerable`（非 string）遍历；其他 → 返回 false。
   - 遍历 `dp.Fields`：`i >= items.Count` → 返回 false；否则 `_ctx.Variables?.Set(dp.Fields[i].Name, items[i])`。
   - 若 `dp.Rest` 非 null：收集 `items[dp.Fields.Count..]` 为 `object[]`，`_ctx.Variables?.Set(dp.Rest, restArray)`。
   - 返回 true。

### 3. 范围模式：Range

#### 3.1 闭范围 `1..=10`

```
match score {
    90..=100 => "A"
    80..=89  => "B"
    0..<60   => "F"
}
```

- `start..=end`：含两端，`subject >= start && subject <= end`。
- AST：`RangePattern(Start, End, Inclusive: true, Span)`。

#### 3.2 半开范围 `1..<10`

- `start..<end`：含 start 不含 end，`subject >= start && subject < end`。
- 借鉴 Rust / Swift 半开范围语法。
- AST：`RangePattern(Start, End, Inclusive: false, Span)`。

#### 3.3 求值规则

`MatchRange(RangePattern rp, object? subject)`：

1. subject 为 null → 返回 false。
2. 求值 `rp.Start` / `rp.End` 得到端点值。
3. 三者必须都是数值（`IsNumeric` 检查），否则返回 false。
4. 全部 `Convert.ToDouble` 后比较：
   - `Inclusive == true`：`sv >= stv && sv <= ev`。
   - `Inclusive == false`：`sv >= stv && sv < ev`。

#### 3.4 与字面量范围的区分

ADR-0050 §4.1 定义了字面量范围 `1..10`（闭范围，含两端）作为**表达式**。在 `match` 模式位置，`1..10` 被解析为 `RangePattern(1, 10, Inclusive: false)`（半开范围）。这一差异是历史包袱：

- **表达式位置**：`for i in 1..10` → 闭范围 `1..10`（per ADR-0050 §5.3）。
- **模式位置**：`match x { 1..10 => }` → 半开范围 `1..<10`（per 本 ADR §3.2，借鉴 Rust `1..10` 半开语义）。

为避免歧义，模式位置推荐显式书写 `1..=10`（闭）或 `1..<10`（半开），parser 两者都支持。

### 4. 守卫模式：Guard

#### 4.1 语法

```
match n {
    0         => "zero"
    x if x > 0 => "positive"    # x 是 subject 绑定（见 §6 As 模式隐式形式）
    _         => "negative"
}
```

- `pattern if condition`：先匹配 `pattern`，匹配成功后求值 `condition`，为真则整体匹配。
- `condition` 是普通 `Expression`，可引用模式绑定的变量。
- 守卫失败时**继续尝试下一个 arm**（不回溯同 arm 的其他分支）。

#### 4.2 隐式 subject 绑定

守卫求值前，`MatchPattern` 把 subject 绑定到 `_` 变量（`_ctx.Variables?.Set("_", subject)`），因此守卫可直接用 `_` 引用 subject：

```
match n {
    _ if _ > 0 => "positive"
    _          => "non-positive"
}
```

这避免了必须用 `as` 绑定才能在守卫中引用 subject 的冗余。

#### 4.3 AST 与求值

- AST：`GuardPattern(Inner: PatternAst, Condition: Expression, Span)`。
- 求值：
  1. `MatchPattern(gp.Inner, subject, out _)` 失败 → 返回 false。
  2. `_ctx.Variables?.Set("_", subject)`。
  3. 返回 `IsTruthy(EvaluateExpression(gp.Condition).Value)`。

### 5. OR 模式：`|`

#### 5.1 语法

```
match color {
    "red" | "crimson" | "scarlet" => "stop"
    "yellow" | "amber"            => "caution"
    "green"                       => "go"
    _                             => "unknown"
}
```

- `a | b | c`：任一分支匹配即成功。
- `|` 是模式级运算符，优先级低于解构 / 范围 / 守卫。
- OR 分支不共享绑定变量（不同分支绑定的变量名可能不同，语义上无法统一）。

#### 5.2 求值规则

- AST：`OrPattern(Alternatives: IReadOnlyList<PatternAst>, Span)`。
- 求值：遍历 `op.Alternatives`，第一个匹配成功的分支使整体返回 true；全部失败返回 false。
- 副作用：匹配成功的分支可能已绑定变量到作用域（如解构字段），这些绑定在 arm `Body` 中可见。建议 OR 分支使用相同字段名以避免 `Body` 引用未定义变量。

#### 5.3 解析优先级

`ParseMatchPattern` 先解析原子模式（`ParseMatchPatternAtom`），再处理 `|`：

```
atom = ParseMatchPatternAtom()
if Check(Pipe):
    alternatives = [atom]
    while Check(Pipe):
        alternatives.Add(ParseMatchPatternAtom())
    atom = OrPattern(alternatives)
return FinishPatternSuffix(atom)   # 再处理 if / as 后缀
```

因此 `a | b if cond` 解析为 `(a | b) if cond`，守卫应用于整个 OR 模式。

### 6. 绑定模式：As

#### 6.1 语法

```
match err {
    [System.Exception] as e => e.Message
    _                       => "unknown"
}

match user {
    { name, age } as u => "$name is $age (full: $u)"
    _                  => "unknown"
}
```

- `pattern as name`：先匹配 `pattern`，匹配成功后把 subject 绑定到 `name` 变量。
- `name` 在 arm `Body` 作用域内可见，引用整个 subject（不是解构后的部分）。
- 可与任何内层模式组合：字面量 / 类型 / 解构 / 范围 / OR / 守卫。

#### 6.2 与守卫的组合

```
match n {
    [int] as x if x > 0 => "positive int: $x"
    _                   => "other"
}
```

`pat as name if cond` 解析顺序：先 `as`（`FinishPatternSuffix` 内 `if` 在 `as` 之前处理），实际是 `(pat if cond) as name` 还是 `pat if (cond as name)`？根据 `FinishPatternSuffix` 实现：

```
if CheckKeyword("if"):
    inner = GuardPattern(inner, cond)    # 先守卫
if MatchKeyword("as"):
    inner = AsPattern(inner, bindName)   # 再 as 包裹
```

因此 `pat if cond as name` 解析为 `AsPattern(GuardPattern(pat, cond), name)`：先匹配 `pat`，再求值守卫，都成功后把 subject 绑定到 `name`。`name` 在守卫中**不可见**（守卫先于 as 求值），但可在 arm `Body` 中使用。

#### 6.3 AST 与求值

- AST：`AsPattern(Inner: PatternAst, BindName: string, Span)`。
- 求值：
  1. `MatchPattern(ap.Inner, subject, out _)` 失败 → 返回 false。
  2. `_ctx.Variables?.Set(ap.BindName, subject)`。
  3. 通过 `bound` 输出参数返回绑定字典（供顶层收集，虽然当前调用方未使用）。
  4. 返回 true。

### 7. 向后兼容：旧式 Expression 模式

#### 7.1 MatchArm 双字段设计

```
public sealed record MatchArm(
    Expression? Pattern,           // 旧式字面量模式（null = _）
    Expression Body,
    PatternAst? AdvancedPattern = null);  // 新式高级模式（优先）
```

- `AdvancedPattern` 非 null → 走 `MatchPattern` 高级路径。
- `AdvancedPattern` 为 null 且 `Pattern` 非 null → 走旧式 `Equals(subject, EvaluateExpression(Pattern))` 路径。
- 两者皆 null → 视为 `_`，永远匹配。

#### 7.2 字面量模式的双填充

`ParseMatchExpression` 在解析到字面量模式时，**同时**填充两个字段：

```
advancedPattern = ParseMatchPattern(start)
if advancedPattern is LiteralPattern lp:
    legacyPattern = lp.Value
```

这确保了：

- 旧代码 `match x { 42 => "answer" }` 仍走旧式路径（`AdvancedPattern` 为 `LiteralPattern`，但 `legacyPattern` 也填充，求值结果一致）。
- 混合代码 `match x { 42 => "answer", [Type] as e => e.Message }` 中，字面量 arm 走旧式、类型 arm 走高级，互不干扰。

#### 7.3 求值流程

`EvaluateMatch(MatchExpression m)`：

```
subject = EvaluateExpression(m.Subject).Value
foreach arm in m.Arms:
    if arm.AdvancedPattern is null:
        if arm.Pattern is null: return EvaluateExpression(arm.Body)   # _ default
        if Equals(subject, EvaluateExpression(arm.Pattern).Value):
            return EvaluateExpression(arm.Body)
        continue
    if MatchPattern(arm.AdvancedPattern, subject, out _):
        return EvaluateExpression(arm.Body)
return ExecutionResult.Empty
```

### 8. 模式语法与优先级

#### 8.1 完整语法

```
pattern      := or_pattern
or_pattern   := atom_pattern ('|' atom_pattern)*
atom_pattern := '_'
             |  '{' field_list (',' '...' rest_name)? '}'        # hash 解构
             |  '[' bind_list (',' '...' rest_name)? ']'         # array 解构
             |  '[' TypeRef ']'                                  # 类型模式
             |  expr '..=' expr                                  # 闭范围
             |  expr '..<' expr                                  # 半开范围
             |  expr                                             # 字面量模式
pattern_with_suffix := pattern ('if' expr)? ('as' name)?
```

#### 8.2 优先级（从低到高）

1. `if` 守卫（最低，应用于整个 OR 模式）。
2. `as` 绑定。
3. `|` OR。
4. 解构 / 范围 / 类型 / 字面量（原子，最高）。

实际解析顺序（`ParseMatchPattern` → `FinishPatternSuffix`）：

1. 解析原子（`ParseMatchPatternAtom`：解构 / 类型 / 范围 / 字面量）。
2. 处理 `|`（`ParseMatchPattern` 内循环）。
3. 处理 `if` 守卫（`FinishPatternSuffix`）。
4. 处理 `as` 绑定（`FinishPatternSuffix`）。

因此 `a | b if cond as name` 解析为 `AsPattern(GuardPattern(OrPattern(a, b), cond), name)`。

### 9. AST 节点层次

```
PatternAst (abstract, : AstNode)
├── WildcardPattern                  # _
├── LiteralPattern(Expression Value) # 42, "hello", true
├── TypePattern(TypeReference Type)  # [System.Exception]
├── DestructurePattern               # { name, age } / [a, b, ...rest]
│   ├── Kind: DestructureKind { Hash, Array }
│   ├── Fields: IReadOnlyList<DestructureField>
│   └── Rest: string?                # ...rest 名
├── RangePattern                     # 1..=10 / 1..<10
│   ├── Start: Expression
│   ├── End: Expression
│   └── Inclusive: bool
├── GuardPattern                     # pat if cond
│   ├── Inner: PatternAst
│   └── Condition: Expression
├── OrPattern                        # a | b | c
│   └── Alternatives: IReadOnlyList<PatternAst>
└── AsPattern                        # pat as name
    ├── Inner: PatternAst
    └── BindName: string
```

辅助类型：

```
enum DestructureKind { Hash, Array }
record DestructureField(string Name, SourceSpan Span)
```

### 10. 求值器实现

#### 10.1 MatchPattern 递归求值

`MatchPattern(PatternAst pattern, object? subject, out Dictionary<string, object?>? bound)`：

| 模式 | 求值逻辑 |
|---|---|
| `WildcardPattern` | 永远返回 true。 |
| `LiteralPattern` | `Equals(subject, EvaluateExpression(lit.Value).Value)`。 |
| `TypePattern` | `ResolveType(tp.Type)` 后 `type.IsAssignableFrom(subject.GetType())`。 |
| `DestructurePattern` | 委托 `MatchDestructure(dp, subject)`。 |
| `RangePattern` | 委托 `MatchRange(rp, subject)`。 |
| `GuardPattern` | 先 `MatchPattern(gp.Inner)`，失败返回 false；成功后 `Set("_", subject)`，返回 `IsTruthy(EvaluateExpression(gp.Condition))`。 |
| `OrPattern` | 遍历 `op.Alternatives`，任一匹配返回 true。 |
| `AsPattern` | 先 `MatchPattern(ap.Inner)`，失败返回 false；成功后 `Set(ap.BindName, subject)`，填充 `bound` 字典，返回 true。 |

`bound` 输出参数当前仅由 `AsPattern` 填充，用于顶层收集绑定变量（调用方 `EvaluateMatch` 未使用，留作未来扩展如 `if let` 语法）。

#### 10.2 绑定作用域

- 模式匹配绑定的变量通过 `_ctx.Variables?.Set(name, value)` 写入当前作用域。
- 作用域规则遵循 ADR-0047 §2：变量在当前作用域可见，arm `Body` 执行时直接读取。
- arm 结束后变量**不**自动移除（与 PowerShell 变量生命周期一致），但下一个 arm 的绑定会覆盖同名变量。
- 建议在不同 arm 中使用不同的绑定名以避免混淆。

#### 10.3 错误处理

- 模式匹配失败**不抛异常**，返回 false 由调用方尝试下一 arm。
- 类型解析失败（`ResolveType` 返回 null）→ `TypePattern` 返回 false。
- 数值转换失败（`IsNumeric` false）→ `RangePattern` 返回 false。
- 解构 subject 类型不符 → `DestructurePattern` 返回 false。

### 11. 与 switch 语句的关系

ADR-0045 §6 定义了 `switch` 语句（PowerShell 兼容），本 ADR 的模式匹配仅用于 `match` 表达式（现代语法，per ADR-0050 §5.2）。两者区别：

| 维度 | `switch` (ADR-0045) | `match` (ADR-0050 + 本 ADR) |
|---|---|---|
| 语法 | `switch ($x) { }` | `match x { }` |
| 模式 | 字面量 / `-Regex` / `-Wildcard` | 8 种高级模式 |
| fall-through | 默认 fall-through（PowerShell 语义） | 默认非 fall-through（Rust 语义） |
| 返回值 | 语句，无返回值 | 表达式，返回匹配 arm 的值 |
| 绑定 | 无（用 `$_`） | 解构 / `as` 绑定 |

`match` 是现代语法的首选分支构造，`switch` 保留用于 PowerShell 兼容。

## Consequences

1. **新增 PatternAst 层次**：8 个 sealed record 模式节点 + `DestructureKind` 枚举 + `DestructureField` 记录。
2. **MatchArm 扩展**：新增 `AdvancedPattern` 可选字段，与旧式 `Pattern` 并存，向后兼容。
3. **Evaluator 新增 3 个方法**：`MatchPattern` / `MatchDestructure` / `MatchRange`。
4. **Parser 新增 4 个方法**：`ParseMatchPattern` / `ParseMatchPatternAtom` / `ParseHashDestructurePattern` / `ParseArrayDestructurePattern` / `FinishPatternSuffix`。
5. **变量作用域副作用**：模式绑定写入当前作用域，arm 结束不移除。这是 PowerShell 变量生命周期的延伸，与 Rust（arm 作用域隔离）不同。
6. **JIT 编译器排除**（per ADR-0058）：`MatchExpression` 含模式匹配副作用（变量绑定），不被 `ExpressionCompiler` 编译，回退到解释执行。
7. **字面量模式双路径**：简单字面量同时填充 `Pattern` 与 `AdvancedPattern`，两种求值路径结果一致，但 `AdvancedPattern` 优先。

## Open Questions

1. **嵌套解构**：当前解构模式字段只支持简单变量名绑定（`{ name, age }`），不支持嵌套（`{ user: { name } }`）。未来是否支持嵌套解构？（M5+ 考虑）
2. **模式匹配表达式 `if let`**：`if let Pattern = expr { }` 语法，单 arm 模式匹配的语法糖。暂未实现。
3. **`@` 绑定 vs `as` 绑定**：Rust 用 `name @ pattern`，本 ADR 选 `pattern as name`（与 Scala / Kotlin 一致）。是否需要兼容 `@` 语法？暂不。
4. **OR 分支变量统一**：当前 OR 分支不强制变量名一致，可能导致 arm `Body` 引用未定义变量。是否在 parser 阶段检查 OR 分支绑定变量名一致？暂不，留作 linter 工作。
5. **模式匹配在 catch 中的应用**：`catch e: [Type] as ex` 是否复用 `PatternAst`？当前 catch 用独立语法（per ADR-0050 §5.4），未来可统一。
