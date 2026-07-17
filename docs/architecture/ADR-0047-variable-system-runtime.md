# ADR-0047: 变量系统运行时语义

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M4 (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0008 (CLI REPL), ADR-0010 (Pipeline 对象流), ADR-0024 (别名与函数), ADR-0042 (自动变量系统, revised), ADR-0045 (控制流), ADR-0046 (脚本块)

## Context

ADR-0042（revised）定义了变量系统"是什么"：作用域层级、类型化变量、参数 splatting、哈希表与数组字面量、`$_`、成员访问、子表达式 `$(...)` 与 `@(...)`。该 ADR 把"变量特性清单"固化下来，但把"如何在运行时实现这些特性"留给后续 ADR 决策。

本 ADR 补齐这部分空白，定义以下运行时语义：

1. **作用域栈实现**：ADR-0042 revised 定义的 Global/Script/Local/Private/Using 五层作用域模型在 M2 阶段是平铺字典；M4 引入函数（ADR-0024 升级）、脚本块（ADR-0046）与控制流（ADR-0045）后，三层字典无法表达"函数调用产生新的局部作用域、return 后回收"的栈式生命周期。需要明确作用域栈数据结构、`private:` / `using:` 修饰符的运行时行为。
2. **类型强制转换规则**：ADR-0042 §8 仅说"按上下文转换、不匹配抛 `InvalidCastException`"，未给出具体转换表。M4 支持 `[int]$x = "42"` / `[string[]]$arr = "a"` 等显式类型标注，必须有明确的转换规则表，否则不同实现路径行为不一致。
3. **成员访问反射**：ADR-0042（revised）允许 `$var.Property` / `$var.Method()` / `$var[index]`，但未定义 OpenShell `IItem`、`IDictionary`、`IList`、CLR 反射之间的优先级与回退顺序，也未定义 `$var?.Member` 空条件访问。
4. **变量 Provider**：ADR-0006 的 Provider 命名空间模型预留了 `Variable:` 虚拟盘，但具体 `Get-ChildItem Variable:` / `Set-Content Variable:Name` 等操作的实现契约未定义。

PowerShell 的变量系统在以下点上有"隐性语义"必须显式化，否则 OpenShell 行为会偏离用户预期：

- 变量名大小写不敏感（`$FOO` 与 `$foo` 同一变量）
- `private:` 修饰符阻止子作用域"穿透"看到本作用域变量
- `global:` 修饰符跳过中间作用域直接定位栈底
- 类型转换的字符串解析必须使用 `InvariantCulture`（避免不同区域设置下 `"3.14"` 解析行为不同）
- 哈希表键大小写不敏感（与 PowerShell 一致），但数组索引必须 0-based
- 子表达式 `$(...)` 在当前作用域求值，不创建新作用域；脚本块 `{ }` 才创建新作用域（ADR-0046）

本 ADR 不重新定义 ADR-0042 已固化的变量特性清单，仅补充运行时实现细节。

## Decision

### 1. 作用域栈实现

#### 1.1 数据结构

每个作用域是 `VariableScope` 对象，构成栈：

```csharp
public sealed class VariableScope
{
    public VariableScope? Parent { get; }
    public ScopeKind Kind { get; }              // Global / Script / Local / Private / Using
    public Dictionary<string, VariableEntry> Variables { get; }
    public Module? Module { get; }              // Script 作用域所属模块（script: 修饰符回溯定位用）

    public VariableScope Push(ScopeKind kind, Module? module = null);
    public VariableEntry? Lookup(string name, ScopeLookupOptions options = ScopeLookupOptions.Default);
}

public enum ScopeKind { Global, Script, Local, Private, Using }

public sealed record VariableEntry(
    string Name,
    object? Value,
    Type? DeclaredType,        // null 表示无类型约束
    bool IsPrivate,            // private: 修饰符创建
    bool IsConstant,           // New-Variable -Option Constant
    bool IsReadOnly);          // New-Variable -Option ReadOnly

[Flags]
public enum ScopeLookupOptions
{
    Default       = 0,
    SkipPrivate   = 1,         // 子作用域回溯时跳过 private 作用域
    GlobalOnly    = 2,         // $global: 修饰符
    ScriptOnly    = 4,         // $script: 修饰符
    LocalOnly     = 8,         // $local: 修饰符
}
```

#### 1.2 栈帧生命周期

- **Global**：进程启动时创建一次，栈底，永不出栈；自动变量（`$?` / `$PWD` / `$HOME` 等）存放于此
- **Script**：每个模块/脚本文件加载时创建一层，文件卸载时出栈；`profile.openshell` 加载时也会创建一层
- **Local**：函数调用、脚本块 `{ }` 执行、`ForEach-Object` / `Where-Object` 等内置循环每次迭代时创建，调用返回时出栈
- **Private**：用 `$private:var = ...` 显式声明的变量存放在当前 Local 作用域，但标记 `IsPrivate = true`，子作用域回溯时不可见
- **Using**：仅用于 `Invoke-Command` / `Start-Job` / `ForEach-Object -Parallel`（M5+ 实现）；值在跨作用域边界时序列化复制

#### 1.3 查找算法

```
Lookup(name, options):
  scope = current_scope
  while scope != null:
    if scope.Variables.TryGetValue(name, out entry):
      if entry.IsPrivate and (options & SkipPrivate) != 0:
        # 子作用域看不到父作用域的 private 变量
        scope = scope.Parent
        continue
      return entry
    scope = scope.Parent
  return null
```

修饰符行为：

- `$var`（无修饰符）：默认查找，从当前作用域逐层向上回溯，遇到 private 跳过
- `$global:var`：直接定位栈底 Global 作用域，跳过中间所有层
- `$script:var`：从当前 Local 向上回溯找到最近的 Script 作用域（同一模块内）；若找不到（如 REPL 顶层），降级到 Global
- `$local:var`：仅查当前 Local 作用域，不回溯；若当前不是 Local，等价于 Default
- `$private:var`：写入时设置 `IsPrivate = true`；读取时等价于 `$local:var`
- `$using:var`：仅在 `Invoke-Command` / `Start-Job` 上下文合法，把父作用域的变量值序列化拷贝到子作用域

#### 1.4 栈深度保护

- 最大栈深度 **1000**（防止无限递归导致 `StackOverflowException`，CLR 默认 1MB 栈约可承受 ~10000 层 C# 调用，1000 层为安全余量）
- 超过时抛 `ScopeStackOverflowException`（继承 `OpenShellException`），错误信息含当前深度与触发命令名
- 此限制独立于 ADR-0045 控制流中的递归深度限制（控制流限制循环次数，本限制限制作用域嵌套层数）

#### 1.5 `Get-Variable -Scope N`

- `N = 0`：当前作用域
- `N = 1`：父作用域
- `N = k`：第 k 个祖先
- `N < 0` 或 `N > 当前栈深度`：抛 `ArgumentOutOfRangeException`
- 实现：`current_scope.Ancestors().ElementAtOrDefault(N)`，其中 `Ancestors()` 返回从当前向根的迭代序列

#### 1.6 函数 / 脚本块调用时的栈帧

```
function f($a, $b) { ... }
f 1 2
```

执行步骤：

1. 求值参数列表（`1` / `2`）
2. 推入新的 Local 作用域，`Parent = caller_scope`，`Module = caller_module`
3. 把命名参数 `$a` / `$b` 写入新作用域的 `Variables`
4. 把位置参数数组 `$args`（未绑定的）写入新作用域
5. 把 `$PSBoundParameters`（命名参数字典）写入新作用域
6. 在新作用域内执行函数体
7. 函数返回时出栈，所有局部变量回收（除非被 `$global:` / `$script:` 修饰符写入父作用域）

脚本块 `{ }` 的执行（ADR-0046）：默认推入新 Local 作用域；`& { ... }` 与 `. { ... }` 都创建新作用域，区别在 `. ` 不创建新作用域（dot-source，在当前作用域内执行）。

### 2. 变量生命周期

#### 2.1 创建

- 首次赋值时创建：`$x = 1` 在当前作用域 `Variables[name] = new VariableEntry(...)`
- 显式创建：`New-Variable -Name x -Value 1 -Option Constant`
- 类型化创建：`[int]$x = 42` 创建时记录 `DeclaredType = typeof(int)`

#### 2.2 移除

- `Remove-Variable -Name x` 或别名 `rv x`
- 仅能移除当前作用域可见的变量；父作用域的 private 变量不可移除
- 常量（Constant）不可移除（与 PowerShell 一致），抛 `SessionStateException`
- 只读（ReadOnly）变量可移除（与 PowerShell 一致），但赋值不可
- 自动变量（`$?` / `$PWD` 等）不可移除

#### 2.3 清空

- 作用域出栈时自动清空所有变量
- `Clear-Variable -Name x` 清空值但保留变量槽（`Value = null`，`DeclaredType` 保留）
- 进程退出时 Global 作用域销毁

#### 2.4 常量与只读

| 选项 | 赋值 | 移除 | 创建后修改 |
|---|---|---|---|
| 默认 | ✅ | ✅ | ✅ |
| `ReadOnly` | ❌（抛 `SessionStateException`） | ✅ | ❌ |
| `Constant` | ❌ | ❌ | ❌ |

`New-Variable -Option Constant` 必须在创建时赋值；`Set-Variable -Option Constant` 对已存在变量抛错。

### 3. 类型强制转换规则

#### 3.1 转换表

类型化变量 `[type]$var = value` 与赋值 `$var = value`（`$var` 已声明类型）触发强制转换：

| 目标类型 | 源值 | 规则 |
|---|---|---|
| `[int]` | `string` | `int.Parse(s, InvariantCulture)`；失败抛 `InvalidCastException` |
| `[int]` | `double` / `float` | 截断向零：`(int)value`（非四舍五入） |
| `[int]` | `decimal` | `(int)value`，截断向零 |
| `[int]` | `bool` | `true` → 1，`false` → 0 |
| `[int]` | `char` | `(int)value`，返回 Unicode 码点 |
| `[int]` | `null` | 抛 `InvalidCastException`（不允许 null 到值类型） |
| `[long]` | 同 `[int]` | `long.Parse` / 截断 / 码点 |
| `[double]` | `string` | `double.Parse(s, InvariantCulture)` |
| `[double]` | `int` / `long` | 隐式提升 `(double)value` |
| `[decimal]` | `string` | `decimal.Parse(s, InvariantCulture)` |
| `[bool]` | `string` | `"true"` → true，`"false"` → false（不区分大小写）；其他尝试 `bool.Parse`，失败抛 `InvalidCastException` |
| `[bool]` | `int` / `long` | 0 → false，非零 → true |
| `[bool]` | `double` | 0.0 → false，非零 → true |
| `[bool]` | `null` | false |
| `[bool]` | 引用类型 | `null` → false，非 null → true |
| `[string]` | 任意 | `value.ToString()`（InvariantCulture）；`null` → `""` |
| `[char]` | `string` | 长度 1 → 该字符；长度 0 → `'\0'`；长度 >1 抛 `InvalidCastException` |
| `[char]` | `int` | `(char)value`，必须落在 `0 <= v <= 0xFFFF` |
| `[string[]]` | `string` | 单元素数组 `new[] { value }` |
| `[string[]]` | `array` | 逐元素 `ToString`，InvariantCulture |
| `[int[]]` | `array` | 逐元素按 `[int]` 规则转换 |
| `[hashtable]` | `hashtable` | 透传 |
| `[hashtable]` | `IDictionary` | 转为 `Hashtable`（大小写不敏感键） |
| `[hashtable]` | `PSCustomObject` | 把属性转为键值对 |
| `[scriptblock]` | `scriptblock` | 透传 |
| `[scriptblock]` | `string` | 抛 `InvalidCastException`（必须用 `{ }` 字面量构造） |
| `[PSCustomObject]` | `hashtable` | 包装属性为 `PSCustomObject` |
| `[PSCustomObject]` | `IDictionary` | 同上 |
| `[type]` | `string` | `Type.GetType(name, throwOnError: true)`；找不到则查 `AppDomain.GetAssemblies()` 中所有公开类型 |
| `[object]` | 任意 | 透传（无转换） |
| `[IItem]` | `IItem` | 透传 |
| `[IItem]` | `string` | 解析为 `ItemPath`（ADR-0006），按当前 Provider 解析为 IItem |
| `[ItemPath]` | `string` | `ItemPath.Parse(value)` |

#### 3.2 转换失败处理

- 抛 `InvalidCastException`，错误信息含源类型与目标类型：`Cannot convert value "abc" (System.String) to type "System.Int32".`
- 错误被外层 `try/catch` 捕获（per ADR-0045），未捕获时经 `IErrorStream` 输出为 `ErrorRecord`，`ErrorCategory = InvalidArgument`
- 退出码 4（与 ADR-0042 §13 一致）

#### 3.3 隐式转换 vs 显式转换

- **显式转换**：`[int]$x = "42"` / `[int]"42"` / `"42" -as [int]`（后者失败返回 `$null` 而非抛错）
- **隐式转换**：仅在以下场景触发
  - 算术运算 `$count + 1`（`$count` 是字符串，按 `[int]` 转换）
  - 比较运算 `$count -gt 0`（按右侧类型或上下文推断）
  - 管道传递给带类型参数的命令：`copy-item $path`（`$path` 是 string，参数声明为 `ItemPath`）

#### 3.4 数值精度

- `double` → `int` 截断而非四舍五入（与 PowerShell / C# 一致）：`[int]3.7` → 3，`[int]-3.7` → -3
- `string` → `double` 解析必须用 `InvariantCulture`：`[double]"3.14"` 在 `de-DE` 区域也按 `.` 作小数点
- `decimal` → `double` 有精度损失（`decimal` 28 位精度，`double` ~15-17 位），不抛错

### 4. 成员访问反射

#### 4.1 `$var.Property` 求值顺序

1. 若 `$var` 为 `null`：抛 `RuntimeBinderException`（除非用 `$var?.Property` 空条件访问，见 §4.5）
2. 若 `$var` 实现 `IItem`（OpenShell Item，per ADR-0003）：通过 `ItemValueAccessor`（已有）取属性值
3. 若 `$var` 实现 `IDictionary`：按 key 索引（大小写不敏感）；命中则返回
4. 若 `$var` 实现 `IList` 或为数组：
   - `Length` / `Count` 属性返回元素数
   - `LongLength` 同上（64 位）
   - 其他属性名尝试反射（如 `SyncRoot` 等）
5. 否则反射：`Type.GetProperty(name, Public | NonPublic | Instance)`（大小写不敏感）
6. 否则尝试字段：`Type.GetField(name, Public | NonPublic | Instance)`
7. 否则抛 `RuntimeBinderException`：`Property 'Name' not found on type 'T'.`

属性 setter 同理：`$var.Property = value` 反向遍历优先级表，调用 setter 或赋值字段。

#### 4.2 `$var.Method(args)` 求值

1. 反射：`Type.GetMethod(name, Public | Instance)`，含重载列表
2. 按参数数量与类型做重载解析（C# 重载解析规则简化版）：
   - 先匹配参数数量
   - 再按类型兼容性评分（精确匹配 > 隐式转换 > 显式转换）
   - 同分时选第一个（PowerShell 兼容行为）
3. 参数逐个按 §3 类型转换规则转换为目标参数类型
4. `MethodInfo.Invoke`，异常透传（`TargetInvocationException` 解包 InnerException）
5. 返回值：`void` 方法返回 `$null`，否则返回实际值

```
$dir = get-item fs::C:/Users
$dir.GetFiles()              # 调用 DirectoryInfo.GetFiles()
$dir.GetFiles("*.txt")        # 重载匹配带参版本
```

#### 4.3 `$var[index]` 求值

1. 若 `$var` 为 `IDictionary`：`$var[key]`，key 按 §6 哈希表语义处理（字符串 key 大小写不敏感）
2. 若 `$var` 为 `IList` 或数组：
   - 非负索引：`$var[i]`，越界抛 `IndexOutOfRangeException`
   - 负索引：从末尾计数，`$var[-1]` 等价 `$var[$var.Length - 1]`
3. 否则尝试默认索引器：`Type.GetProperty("Item", Public | Instance)` 的索引参数
4. 否则抛 `RuntimeBinderException`

#### 4.4 范围索引 `$var[1..3]`

- 仅对 `IList` / 数组有效
- 起止都包含（与 PowerShell 一致）：`$arr[1..3]` 返回索引 1、2、3
- 负数：`$arr[-2..-1]` 返回倒数 2 个元素
- 反向：`$arr[3..1]` 返回索引 3、2、1（与 PowerShell 一致）
- 返回值类型：`object[]`

#### 4.5 `$var?.Member` 空条件访问

- `$var` 为 `null`：返回 `$null`，不抛错、不求值 `Member`
- `$var` 非 `null`：等价 `$var.Member`
- 链式：`$var?.A?.B?.C` 任一环节 null 都短路返回 `$null`
- 数组空条件：`$var?[0]` 同理

#### 4.6 反射缓存

- 缓存键：`(Type, MemberName)` 元组
- 缓存值：`PropertyInfo` / `MethodInfo[]` / `FieldInfo`
- 缓存实现：`ConcurrentDictionary<(Type, string), MemberInfo[]>`，线程安全
- 缓存失效：CLR 类型系统不可变，缓存永不过期
- 命中率：反射后的第一次访问 ~10μs，缓存命中后 ~0.1μs

### 5. 子表达式求值

#### 5.1 `$(...)` 子表达式

```
$x = $("a", "b", "c")
$y = $(get-childitem | select -first 3)
$z = "count: $($arr.Count + 1)"
```

求值步骤：

1. 解析括号内内容为语句列表（多条语句用 `;` 或换行分隔）
2. **在当前作用域求值**（不创建新作用域；与脚本块 `{ }` 不同，per ADR-0046）
3. 捕获所有语句的输出（不含赋值语句的左值，参考 ADR-0010 管道对象流）
4. 返回值：
   - 0 个输出 → `$null`
   - 1 个输出 → 该值（原类型）
   - 2+ 个输出 → `[object[]]` 数组

#### 5.2 `@(...)` 数组子表达式

- 求值流程同 `$(...)`
- 但返回值**始终**为 `[object[]]`：
  - 0 个输出 → 空数组 `[object[]]`（`Length == 0`）
  - 1 个输出 → 单元素数组
  - 2+ 个输出 → 数组

差异用途：

- `$x = $(get-item)` 当结果唯一时拿到单个对象（而非单元素数组），方便链式 `.Property` 访问
- `$x = @(get-item)` 始终拿到数组，可安全 `.Count` / `.ForEach(...)`

#### 5.3 输出捕获语义

子表达式中：

- 赋值语句 `$a = 1` 不产生输出（赋值的左值不流出）
- 表达式语句 `1 + 2` 产生输出
- 命令调用 `get-childitem` 产生输出（按 ADR-0010 的 IItem 流）
- `Write-Output` 显式产生输出
- `Write-Host` 直接写到 host，不被子表达式捕获（per ADR-0011）

### 6. 哈希表语义

#### 6.1 字面量

```
$h = @{
    Name  = "Alice"
    Age   = 30
    Roles = @("admin", "user")
    Nested = @{
        City = "Shanghai"
    }
}
```

- 字面量创建 `System.Collections.Hashtable`（不是 `Dictionary<string, object>`）
- 键大小写不敏感（与 PowerShell 一致）：`$h.Name` 与 `$h.NAME` 等价
- 键类型仅 `string`：其他类型 key 被 `ToString` 转为字符串
- 值类型任意（`object`）
- 嵌套字面量递归构造

#### 6.2 访问

```
$h.Name              # 属性语法（成员访问反射 §4.1 第 3 条）
$h["Name"]           # 索引器语法（成员访问反射 §4.3 第 1 条）
$h.Keys              # ICollection<string>
$h.Values            # ICollection<object>
$h.Count             # int
$h.ContainsKey("Name")   # bool
$h.Contains("Name")       # 别名
$h.Add("K", "V")          # void，重复 key 抛 ArgumentException
$h.Remove("Name")         # bool
$h.Clear()                # void
```

#### 6.3 修改

```
$h.NewKey = "value"        # 添加新键（成员访问反射 setter，§4.1 反向）
$h["NewKey"] = "value"    # 索引器赋值
$h.Existing = "updated"   # 修改已存在键
```

#### 6.4 顺序保证

- `Hashtable` 不保证插入顺序（与 PowerShell 一致）
- 需要顺序时用 `[ordered]@{ }`（创建 `OrderedDictionary`，M5+ 实现）
- `@{ }.GetEnumerator()` 迭代顺序与插入顺序无关

### 7. 数组语义

#### 7.1 字面量

```
$a = @(1, 2, 3)              # 显式数组子表达式
$b = 1, 2, 3                 # 逗号操作符
$c = @()                     # 空数组
$d = @("single")             # 单元素数组
```

- 创建 `object[]`（`System.Object[]`），不是 `int[]` / `string[]`
- 元素类型混合允许：`@(1, "two", $true)`
- 逗号操作符优先级低于管道：`1, 2 | foreach { $_ }` 解析为 `1, (2 | foreach { $_ })`

#### 7.2 访问

```
$arr[0]              # 索引访问（0-based，§4.3 第 2 条）
$arr[-1]             # 负索引（从末尾计数）
$arr[1..3]           # 范围索引（含两端，§4.4）
$arr[0, 2, 4]        # 多索引（返回 object[]）
$arr.Count           # 元素数
$arr.Length          # 别名
$arr.LongLength      # 64 位长度（罕见用）
```

#### 7.3 修改

```
$arr[0] = "new"              # 修改元素
$arr += "item"               # 追加（创建新数组）
$arr = $arr + $other         # 拼接
$arr = $arr[0..2] + $arr[5..9]  # 切片拼接
```

- `object[]` 不可变：`$arr += x` 实际创建新数组（拷贝旧元素 + 新元素），旧数组等待 GC
- `$arr[0] = x` 修改元素是合法的（数组本身可变，长度不可变）

#### 7.4 迭代

```
$arr | ForEach-Object { $_ }         # 管道迭代
$arr.ForEach({ $_ })                # 方法形式（per ADR-0046）
foreach ($x in $arr) { ... }         # 控制流迭代（per ADR-0045）
$arr | Where-Object { $_ -gt 0 }     # 过滤
```

### 8. 字符串插值

#### 8.1 双引号 `"..."`

| 语法 | 含义 | 示例 |
|---|---|---|
| `$var` | 变量值 | `"hello $name"` |
| `${var}` | 变量值（显式定界） | `"path: ${PWD}"` |
| `$var.Property` | 属性值 | `"size: $file.Length"` |
| `$var[index]` | 索引结果 | `"first: $arr[0]"` |
| `$(expression)` | 子表达式结果 | `"count: $($arr.Count + 1)"` |
| `@(expression)` | 数组子表达式 | `"items: @(get-childitem)"` |
| `$env:NAME` | 环境变量 | `"path: $env:PATH"` |
| `$global:Var` | 全局变量 | `"g: $global:Count"` |
| `$script:Var` | 脚本作用域 | `"s: $script:Config"` |
| `$private:Var` | 私有变量 | `"p: $private:Secret"` |
| `$using:Var` | 跨作用域（仅在 Invoke-Command 内） | `"u: $using:Outer"` |

- 变量名匹配规则：`[A-Za-z_][A-Za-z0-9_]*`（可选 `:` 修饰符）
- 属性 / 索引器访问仅一层：`$var.A.B` 中 `$var.A` 是变量名 + 属性，`.B` 是字面字符（要链式访问用 `$($var.A.B)`）
- `${var}` 形式允许变量名含特殊字符（如 `${file name}`）

#### 8.2 单引号 `'...'`

- 不插值，全部字面
- 不支持转义（含 `\n` / `\t` 也是字面）
- 单引号内部要表达单引号：用 `''` 双写：`'it''s ok'` → `it's ok`

#### 8.3 Here-String

```
$multi = @"
line 1
line 2 with $var
"@
```

- `@" ... "@`：插值（同双引号规则）
- `@' ... '@`：字面（同单引号规则）
- 闭合的 `"@` / `'@` 必须在行首（无前导空白），与 PowerShell 一致
- 内部换行保留为 `\n`（跨平台统一为 LF，不保留 CRLF）

#### 8.4 转义字符（双引号内）

| 转义 | 含义 |
|---|---|
| `` `n `` | 换行 LF |
| `` `r `` | 回车 CR |
| `` `t `` | Tab |
| `` `0 `` | NUL |
| `` `a `` | Alert（响铃） |
| `` `b `` | 退格 |
| `` `f `` | 换页 |
| `` `v `` | 垂直 Tab |
| `` `" `` | 双引号字面 |
| `` `` ` `` `` | 反引号字面 |
| `` `$ `` | `$` 字面（取消插值） |

反引号 `` ` `` 为转义前导符（与 PowerShell 一致），不是反斜杠 `\`。

### 9. Splatting 实现

#### 9.1 语法

```
$params = @{ Path = "C:/Users"; Recurse = $true }
get-childitem @params

$args = @("C:/Users", "*.txt")
get-childitem @args

function f($a, $b, $c) { ... }
$bound = @{ a = 1; b = 2 }
f @bound              # $c 未绑定，使用默认值
```

#### 9.2 实现步骤

1. Parser 在参数位置识别 `@Identifier`（非 `$`，是 `@`）
2. 求值 `Identifier` 的值
3. 分支：
   - `Hashtable` / `IDictionary`：splatted 为命名参数
     - 每个 key 尝试匹配命令的参数名（大小写不敏感，含 `-` 前缀去除）
     - 匹配失败：若命令有 ` IDictionary` 类型的剩余参数，整体传入；否则抛 `ParameterBindingException`
   - 数组（`object[]` / `IList`）：splatted 为位置参数
     - 逐元素追加到位置参数列表
4. 调用命令时合并 splatted 参数与显式参数（显式优先）

#### 9.3 `@PSBoundParameters`

- 仅在函数体内有效
- 包含本函数已绑定的命名参数（不含 `$args`）
- 转发给下游命令时常见模式：`Invoke-Cmdlet @PSBoundParameters -ExtraParam X`

#### 9.4 `@args`

- 仅在函数体内有效
- 包含未绑定到命名参数的位置参数
- 透传给下游命令

#### 9.5 性能

- Splatting 时对 `Hashtable` 做**浅拷贝**（new `Hashtable` 复制 entries，不深拷贝 value）
- 不深拷贝的原因：splatted 参数本身通常不再被修改，且深拷贝对大型对象代价过大
- 数组 splatting 不拷贝，直接把引用传给下游（命令不修改入参数组）

### 10. 变量 Provider

#### 10.1 `Variable:` 虚拟盘

per ADR-0006 Provider 命名空间模型，`Variable:` 是内置虚拟盘，无文件系统后端：

| 操作 | 命令 | 行为 |
|---|---|---|
| 列举 | `Get-ChildItem Variable:` | 列出当前作用域所有变量 |
| 读取 | `Get-Item Variable:Name` | 取特定变量值 |
| 设置 | `Set-Content Variable:Name $value` | 设置变量值 |
| 删除 | `Remove-Item Variable:Name` | 移除变量 |
| 清空 | `Clear-Content Variable:Name` | 清空值（保留槽） |
| 复制 | `Copy-Item Variable:Name Variable:OtherName` | 复制变量 |

#### 10.2 实现契约

```csharp
public sealed class VariableProvider : IContentProvider, IDriveProvider
{
    public string Name => "Variable";
    public string Description => "OpenShell variable drive";

    // IItem 适配：变量包装为 IItem，含 Name / Value / Type 属性
    public IItem? GetItem(ItemPath path, CancellationToken ct);
    public IAsyncEnumerable<IItem> GetChildItems(ItemPath path, CancellationToken ct);
    public void SetItem(ItemPath path, object value, CancellationToken ct);
    public void RemoveItem(ItemPath path, bool recurse, CancellationToken ct);
    public bool HasChildItems(ItemPath path);
    public bool IsValidPath(ItemPath path);

    // IContentReader/Writer：支持 Get-Content / Set-Content / Add-Content
    public IContentReader GetContentReader(ItemPath path);
    public IContentWriter GetContentWriter(ItemPath path);
}
```

#### 10.3 路径解析

- `Variable:` 前缀，路径分隔符为 `\` 或 `/`（无嵌套，仅一层）
- `Variable:Name` 中 `Name` 是变量名
- 不支持子路径（变量无嵌套层级）
- 大小写不敏感（`Variable:FOO` 与 `Variable:foo` 同变量）

#### 10.4 列举输出

`Get-ChildItem Variable:` 输出 `IItem` 列表，每个 IItem 含属性：

| 属性 | 类型 | 含义 |
|---|---|---|
| `Name` | string | 变量名 |
| `Value` | object | 变量值 |
| `Type` | Type | 声明类型（null 表示无类型约束） |
| `Description` | string | `New-Variable -Description` 设置的描述 |
| `Options` | string | `"None"` / `"ReadOnly"` / `"Constant"` / `"Private"` |
| `Scope` | string | 当前作用域名（调试用） |

#### 10.5 `Env:` 虚拟盘

- 已实现：`EnvProvider` 位于 `src/OpenShell.Providers.Variables/EnvProvider.cs`
- 路径格式：`env::NAME`（per ADR-0006 `provider::internalPath` 模型）
- `$env:NAME` 直接变量访问仍走 Tokenizer 级别快捷路径（`$env:` Token），与 `Env:` Provider 并行工作
- `Get-ChildItem env::` 列举所有环境变量（`Environment.GetEnvironmentVariables`）
- `Remove-Item env::NAME` 通过 `Environment.SetEnvironmentVariable(name, null)` 删除

#### 10.6 `Function:` 虚拟盘

- 已实现：`FunctionProvider` 位于 `src/OpenShell.Providers.Variables/FunctionProvider.cs`
- 路径格式：`function::Name`
- 后端为 `IAliasRegistry.ListFunctions()` / `SetSessionFunction` / `RemoveSessionFunction`
- `Get-ChildItem function::` 列举所有用户函数
- `Get-Content function::Name` 返回函数体作为文本流
- `Set-Content function::Name` 从流创建/更新会话函数
- `Remove-Item function::Name` 删除会话函数

#### 10.7 路径记法说明

- ADR 示例中使用 PowerShell 风格 `Variable:Name` / `Env:NAME` 记法仅为用户熟悉
- 运行时实际使用 ADR-0006 的 `provider::internalPath` 模型：`variable::Name` / `env::NAME` / `function::Name`
- 两种记法语义等价：`Variable:Name` ≡ `variable::Name`

### 11. 性能考量

#### 11.1 变量查找

- 每作用域 O(1) 字典访问，最坏 O(栈深度)
- 典型栈深度 < 10（REPL 顶层 + 函数调用 1-3 层），实际查找 O(1) 等效
- 1000 层深栈场景（罕见，通常是 bug）查找耗时 < 100μs

#### 11.2 类型转换缓存

- 缓存键：`(SourceType, TargetType)` 元组
- 缓存值：`Func<object, object>` 委托（编译后的转换器）
- 常见转换预热：`(string, int)` / `(string, double)` / `(string, bool)` / `(int, bool)` 等
- 缓存命中率 > 99% 后转换耗时 < 0.5μs

#### 11.3 反射缓存

- §4.6 已述：`(Type, MemberName)` → `MemberInfo[]`，`ConcurrentDictionary` 线程安全
- 第一次访问 ~10μs，缓存命中后 ~0.1μs
- 不缓存 `MethodInfo.Invoke` 结果（每次都执行）

#### 11.4 Splatting

- Hashtable 浅拷贝：O(n)（n = entry 数）
- 数组 splatting：0 拷贝（传引用）
- `@PSBoundParameters` 转发：`Hashtable` 浅拷贝后传给下游

#### 11.5 作用域栈分配

- `VariableScope` 对象通过 `ObjectPool<VariableScope>` 复用
- 函数返回时归还到池，下次调用复用
- `Dictionary<string, VariableEntry>` 在归还前 `Clear()`（不清空底层 buckets）
- 池大小：默认 64 个作用域对象，超过则 GC 回收

#### 11.6 字符串插值

- 短字符串（< 256 字符）用栈分配 `Span<char>`
- 长字符串用 `StringBuilder`（复用 `StringBuilder` 池）
- 无 `$` 的字符串短路返回原值（常见路径）

### 12. 持久化

#### 12.1 命令

| 命令 | 说明 |
|---|---|
| `Export-Variable -Name X [-Path <file>]` | 导出变量到 JSON 文件 |
| `Import-Variable [-Name X] [-Path <file>]` | 从文件导入变量 |
| `Export-Variable -All [-Path <file>]` | 导出所有可序列化变量 |

#### 12.2 文件格式

默认路径 `~/.openshell/variables.json`：

```json
{
  "X": { "Value": "5", "Type": "int" },
  "Y": { "Value": "hello", "Type": "string" },
  "Z": { "Value": ["a", "b"], "Type": "string[]" }
}
```

- JSON 编码：UTF-8 无 BOM
- 命名策略：`JsonNamingPolicy.CamelCase`（key 用 camelCase，但变量名本身大小写不敏感所以影响小）
- 枚举转换：`JsonStringEnumConverter`
- 缩进：`WriteIndented = true`（便于 diff 与手动编辑）

#### 12.3 类型支持

| 类型 | 可持久化 | 备注 |
|---|---|---|
| 基元（int / long / double / bool / string / char） | ✅ | 原生 JSON |
| `decimal` | ✅ | 序列化为字符串保留精度 |
| `DateTime` / `DateTimeOffset` | ✅ | ISO 8601 |
| `TimeSpan` | ✅ | 字符串（`"1.02:03:04"`） |
| `Guid` | ✅ | 字符串 |
| 数组（`int[]` / `string[]` / `object[]`） | ✅ | JSON 数组 |
| `Hashtable` | ✅ | JSON 对象（key 强转 string） |
| `PSCustomObject` | ✅ | JSON 对象 |
| `scriptblock` | ❌ | 无法安全反序列化（代码注入风险） |
| `IItem` | ❌ | Provider 相关，跨会话无意义 |
| `Type` | ✅ | 类型全名字符串，反序列化时 `Type.GetType` |
| 自定义可序列化类型 | ✅ | 用 `JsonSerializer` 默认行为 |

#### 12.4 自动导入

- 启动时检查 `~/.openshell/variables.json` 是否存在
- 存在则自动 `Import-Variable`，写入 Global 作用域（不是 Session，确保不覆盖 REPL 临时赋值）
- 失败时发出 warning，不阻断启动（per ADR-0041 启动脚本容错原则）

#### 12.5 局限

- 不能持久化 `scriptblock`（安全考虑：反序列化任意代码 = 远程代码执行）
- 不能持久化 `IItem`（Provider 相关，跨会话意义不明）
- `PSCustomObject` 持久化时丢失原 CLR 类型信息，反序列化为新 `PSCustomObject`
- 循环引用（自定义对象内 `A.B = A`）会抛 `JsonException`，禁止持久化

## Alternatives Considered

1. **沿用 ADR-0042 原始设计**（三层平铺字典、无类型、无成员访问）：被否决。M4 引入函数、脚本块、控制流后，平铺字典无法表达栈式作用域生命周期；PowerShell 用户期望 `[int]$x` / `$var.Property` 等语法，不支持则与 PS 兼容性目标冲突
2. **动态语言运行时（DLR）**：被否决。`Microsoft.Dynamic` 与 `System.Linq.Expressions` 实现变量绑定与类型转换，但 DLR 学习曲线陡、与 .NET 强耦合、对 OpenShell 的 Provider 模型无原生支持；引入 DLR 后整个变量系统都得按 DLR 规则走，灵活性反而下降
3. **嵌入 PowerShell 引擎（`Microsoft.PowerShell.SDK`）**：被否决。SDK 体积大（~30MB）、依赖 Windows-only 组件（部分 cmdlet）、初始化慢（~1s）；其变量系统与 OpenShell 的 Provider / IItem 模型不直接兼容，需要桥接层反而更复杂
4. **基于 Roslyn 的 C# 脚本**：被否决。语法差异大（C# 不是 shell 语言，`var x = 1;` 而非 `$x = 1`），不符合 PowerShell 用户的肌肉记忆；Roslyn 脚本引擎对全局状态的隔离也不如自定义作用域栈灵活
5. **基于解释器模式的纯 AST 树遍历**：被否决。性能差（无 IL / 委托缓存），且与反射系统对接需手写大量胶水；最终还是要走 CLR 反射，不如直接定义本 ADR 的反射优先级表
6. **强类型变量系统（每个变量必须声明类型）**：被否决。破坏 shell 灵活性（`$x = 1; $x = "a"` 应当合法），与 PowerShell 行为不符；类型仅作为可选约束（`[int]$x = 1`）

## Consequences

### 优势

- **完整 PowerShell 变量兼容性**：作用域栈、`private:` / `global:` / `script:` / `using:` 修饰符、类型转换、成员访问、子表达式等行为与 PowerShell 一致，PS 脚本迁移成本低
- **作用域栈式实现直观**：函数调用 / 脚本块执行的栈帧生命周期与 C# 调用栈对齐，调试时可通过 `Get-Variable -Scope N` 检查任意祖先作用域
- **类型转换表明确**：避免不同实现路径行为不一致（如 `string → int` 必须用 InvariantCulture、`double → int` 截断而非四舍五入）
- **反射缓存降低开销**：成员访问从 ~10μs 降至 ~0.1μs（缓存命中后），与直接 C# 属性访问差距在可接受范围
- **变量 Provider 暴露统一接口**：`Variable:` 虚拟盘使变量管理命令（`Get-ChildItem` / `Set-Content` / `Remove-Item`）与文件系统命令一致，降低用户认知成本
- **持久化方案明确**：JSON 格式 + 类型信息 + camelCase 编码，跨平台 / 跨工具可读，手动编辑友好

### 代价

- **作用域栈分配开销**：每次函数调用新建 / 出栈 `VariableScope`，约 1μs（含 `Dictionary` 创建）。1000 次函数调用 = 1ms 累积开销，对热路径脚本可见
- **反射开销**：成员访问首次 ~10μs（缓存前），缓存命中后 ~0.1μs；高频成员访问场景（如 `1..1000 | % { $_.Name }`）累计开销可见，需后续 ADR 探讨表达式编译（M5+）
- **内存占用**：每个作用域 ~1KB（`Dictionary` + `VariableEntry` 数组 + 元数据），1000 层深栈最坏 1MB；典型场景 < 10KB
- **类型转换表维护成本**：转换表 30+ 条规则需逐一测试，新增类型（如 `BigInteger`）需补充规则
- **反射缓存常驻内存**：`(Type, Name)` 缓存随使用增长，长时间运行会话可能积累数 MB；暂未规划 LRU 淘汰（类型数量有限，缓存上限 ~10000 条）
- **持久化局限**：不能持久化 `scriptblock` 与 `IItem`，用户跨会话保存复杂状态需用 `profile.openshell` 脚本重新构造
- **`Hashtable` 大小写不敏感**：与 .NET 默认 `Hashtable`（大小写敏感）不同，需用 `StringComparer.OrdinalIgnoreCase` 构造，与 C# 代码交互时需注意

### 约束

- **变量名大小写不敏感**：`$FOO` 与 `$foo` 是同一变量，与 PowerShell 一致；字典用 `StringComparer.OrdinalIgnoreCase`
- **作用域栈最大深度 1000**：超过抛 `ScopeStackOverflowException`，防止无限递归栈溢出
- **反射缓存必须线程安全**：变量可从并行管道（ADR-0010 `ForEach-Object -Parallel`，M5+）访问，缓存用 `ConcurrentDictionary`
- **类型转换必须确定性**：相同输入必产生相同输出，禁用 `CultureInfo.CurrentCulture` 解析数值（除用户显式 `-Culture` 参数，本 ADR 不引入）
- **JSON 持久化使用 `JsonNamingPolicy.CamelCase` 与 `JsonStringEnumConverter`**：与 ADR-0022 配置持久化编码一致
- **变量名规则**：`[A-Za-z_][A-Za-z0-9_]*`，禁止含 `-`（避免与 Verb-Noun 混淆）、禁止以数字开头（与 ADR-0042 §13 一致）
- **`$private:` 变量不泄漏到子作用域**：`private` 标记的变量在子作用域回溯时被跳过
- **`Constant` 变量不可移除**：与 PowerShell 一致；`ReadOnly` 可移除但不可赋值
- **`$using:` 仅在 `Invoke-Command` / `Start-Job` / `ForEach-Object -Parallel` 上下文合法**：其他位置报 `ParameterBindingException`
- **`$(...)` 在当前作用域求值**：不创建新作用域；`{ }`（脚本块）才创建新作用域（per ADR-0046）
- **数组不可变长度**：`$arr += x` 创建新数组，旧数组等待 GC；高频追加场景建议用 `System.Collections.Generic.List<object>`（通过 `[System.Collections.Generic.List[object]]]::new()` 构造）
- **`Hashtable` 键大小写不敏感**：与 PowerShell 一致；`$h.Name` 与 `$h.NAME` 等价
- **类型转换失败统一退出码 4**：与 ADR-0042 §13 一致
- **`Variable:` Provider 路径仅一层**：变量无嵌套层级，`Variable:Name/Sub` 非法
- **`Export-Variable` 不导出 `scriptblock` / `IItem`**：序列化时跳过这两类（warning 提示），其他类型失败抛错
- **自动导入在 Global 作用域**：避免覆盖 Session 临时赋值；若用户希望覆盖可用 `Import-Variable -Scope Session`（M5+）
- **反射缓存永不过期**：CLR 类型系统不可变，缓存命中后无失效问题；若动态加载 / 卸载 Assembly（per ADR-0016 ALC），缓存中 `MemberInfo` 仍有效（CLR 弱引用保护）
- **`$var?.Member` 空条件访问必须支持链式**：`$a?.B?.C?.D` 任一环节 null 短路返回 `$null`
- **属性 setter 反射优先级与 getter 一致**：`IItem > IDictionary > IList > CLR Property > CLR Field`
- **子表达式输出捕获语义**：赋值语句不产生输出，命令 / 表达式语句产生输出（per ADR-0010 管道对象流）
