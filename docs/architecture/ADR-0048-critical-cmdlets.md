# ADR-0048: PowerShell 全兼容必需的关键 Cmdlet 集

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M4 (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0010 (Pipeline), ADR-0011 (Formatting), ADR-0023 (命令清单), ADR-0026 (错误模型), ADR-0036 (安全沙箱), ADR-0042 (变量系统), ADR-0045 (控制流), ADR-0046 (脚本块), ADR-0047 (变量运行时), ADR-0049 (ShouldProcess)

## Context

ADR-0023 已建立受约束动词枚举与 `Verb-Noun` 命名规范，并按 M1-M5 里程碑划定了命令交付清单。M1-M3 阶段交付的 ~85 个内置命令覆盖了核心文件 / 注册表 / 压缩包 / 远程操作能力，但 PowerShell 生态中存在大量"日常脚本必备"的 cmdlet，OpenShell 当前尚未提供，导致真实 `.ps1` 脚本无法在 OpenShell 中无修改运行。

2026-07-08 用户决定走「PowerShell 全兼容」路线（见 ADR-0042 修订说明 / ADR-0046 §Context），ADR-0046 已奠定脚本块（`{ }`）作为一等值的语言基础，ADR-0047 已补齐作用域栈与类型转换的运行时语义。本 ADR 在该语言层之上，枚举并规范 M4 阶段需要新增的 ~40 个关键 cmdlet，使 OpenShell 具备承载绝大多数 PowerShell 脚本的能力。

### 痛点

1. **`ForEach-Object { }` / `Where-Object { }` 缺失脚本块重载**：ADR-0023 M2 仅定义了 `Where-Object` 的 DSL 字符串形式（`where size > 1MB`），PowerShell 用户的肌肉记忆是 `Where-Object { $_.Length -gt 1MB }`，无脚本块重载则脚本不可移植
2. **输出流模型缺失**：`Write-Output` / `Write-Host` / `Write-Error` / `Write-Warning` / `Write-Verbose` / `Write-Debug` 全部缺失，PowerShell 脚本的"分流"惯用法（success / error / warning / verbose / debug / information 六流）无法表达
3. **`Out-*` 格式化链路不完整**：`Out-Default` / `Out-Null` / `Out-Host` / `Out-String` / `Out-GridView` 缺失，ADR-0010 §2 的"管道结尾若无 Sink 默认调 `Out-Default`"约定目前无实际命令落地
4. **路径操作不全**：`Test-Path` / `Resolve-Path` / `Split-Path` / `Join-Path` / `Convert-Path` 缺失，用户必须直接调 CLR `Path` 类，失去 shell 友好性
5. **`Push-Location` / `Pop-Location` 缺失**：ADR-0023 M1 列出但未交付，影响交互式使用
6. **对象反射缺失**：`Get-Member` / `New-Object` / `Select-Object -ExpandProperty` / `Measure-Command` / `Trace-Command` 全部缺失，调试与对象探查无法进行
7. **内容追加 / 清空能力缺失**：`Add-Content` / `Clear-Content` 未交付（ADR-0023 仅 M1 给了 `Get-Content` / `Set-Content`）
8. **数据格式转换缺失**：`ConvertTo-Json` / `ConvertFrom-Json` / `ConvertTo-Csv` / `ConvertFrom-Csv` / `Import-Csv` / `Export-Csv` / `ConvertTo-Html` / `ConvertTo-Xml` 全部缺失，REST API / 报表 / 数据交换场景不可用
9. **进程管理缺失**：`Get-Process` / `Start-Process` / `Stop-Process` / `Wait-Process` / `Debug-Process` 全部缺失，自动化运维场景断裂
10. **Web cmdlets 缺失**：ADR-0023 M4 仅列 `Invoke-WebRequest`（为 S3 Presigned URL 用），但 PowerShell 用户的 `Invoke-WebRequest` / `Invoke-RestMethod` 是日常调用 REST API 的主要手段，必须提供
11. **工具型 cmdlet 缺失**：`Start-Sleep` / `Get-Random` / `Get-Date` / `New-TimeSpan` / `Measure-Command` / `Send-MailMessage` / `Show-Command` 缺失

### 依赖关系

- **上游依赖**（本 ADR 依赖以下 ADR）：
  - ADR-0010：管道对象流，`ForEach-Object` / `Where-Object` / `Sort-Object` 等实现 `IPipelineTransform` 接口
  - ADR-0011：格式化系统，`Out-Default` / `Out-String` / `Out-Host` 委托格式化器
  - ADR-0026：错误模型，`Write-Error` 写入 `IErrorStream`
  - ADR-0042（revised）/ ADR-0047：变量系统，`$_` / `$PSItem` / `$Input` / `$Error` / `$VerbosePreference` / `$DebugPreference` / `$WarningPreference` / `$InformationPreference` 等自动变量绑定
  - ADR-0045：控制流，脚本块体内 `if / for / foreach / try` 在 `ForEach-Object -Process { }` 中合法
  - ADR-0046：脚本块作为一等值，`ForEach-Object -Process { }` 形参类型为 `[scriptblock]`
  - ADR-0049（ShouldProcess）：通用参数 `-WhatIf` / `-Confirm` 的统一实现，破坏性 cmdlet（`Stop-Process` / `Clear-Content` / `Set-Date`）声明 `SupportsShouldProcess`
- **下游依赖**（本 ADR 被以下 ADR / 文档依赖）：
  - ADR-0023（registry.md）：命令清单需更新至 ~125 个内置命令
  - ADR-0025（帮助系统）：每个新 cmdlet 必须有 `about_<cmdlet>` 帮助条目
  - ADR-0036（安全沙箱）：`Invoke-WebRequest` / `Start-Process` / `Send-MailMessage` 等高风险 cmdlet 受沙箱权限约束
  - ADR-0044（GUI 进度 UI）：`Write-Progress` 通过 `ITaskCenter` 推送进度
  - ADR-0049（ShouldProcess）：`-WhatIf` / `-Confirm` 在本 ADR 的所有破坏性 cmdlet 上启用

### 设计原则

1. **PowerShell 全兼容优先**：参数名、默认值、输出类型严格对齐 PowerShell 5.1（LTS），用户脚本无修改可运行
2. **复用现有抽象**：管道 cmdlet 实现 ADR-0010 的 `IPipelineTransform` / `IPipelineSink`，错误写入走 ADR-0026 的 `IErrorStream`，脚本块参数走 ADR-0046 的 `ScriptBlock` 类型
3. **不重新发明轮子**：JSON / CSV / HTML / XML 序列化使用 `System.Text.Json` / 自实现 CSV / `HtmlTextWriter` / `XmlSerializer`，不引入第三方依赖
4. **流式优先**：所有接受管道输入的 cmdlet 必须 `IAsyncEnumerable<IItem>` 流式处理，禁止一次性 `ToArray` 后处理（除非语义必须，如 `Sort-Object`）
5. **安全责任下沉**：网络访问、进程生成、邮件发送等高风险操作的安全约束由 ADR-0036 沙箱承担，本 ADR 仅声明 cmdlet 需要哪些权限
6. **CLI 与 GUI 共用**：所有 cmdlet 在 CLI host 与 GUI host 行为一致；GUI 特化的（如 `Out-GridView` / `Show-Command`）在 CLI host 走降级路径

## Decision

### 1. Pipeline Cmdlets（CRITICAL）

本组 cmdlet 是 PowerShell 管道的核心，所有 `.ps1` 脚本几乎必然出现其中之一。ADR-0023 M2 已列入 `Where-Object` / `Select-Object` / `Sort-Object` / `Group-Object` / `Measure-Object` / `Compare-Object` / `Take-Object` / `Skip-Object`，但均按 DSL 字符串形式实现。本 ADR 升级为脚本块形式，与 PowerShell 行为对齐。

#### 1.1 `ForEach-Object`

- **别名**：`%` / `foreach`
- **作用**：对每个管道项执行脚本块，`$_` 是当前项
- **参数**：
  - `-Begin <scriptblock>`：管道开始前执行一次
  - `-Process <scriptblock[]>`（mandatory，可多个）：每个管道项执行一次（多个时按顺序执行）
  - `-End <scriptblock>`：管道结束后执行一次
  - `-RemainingArgs <string[]>`：剩余位置参数（透传给脚本块的 `$args`）
- **位置参数**：第一个位置参数等价于 `-Process`
- **管道输入**：任意对象（通过 `-InputObject` 绑定，`ValueFromPipeline`）
- **输出**：脚本块的所有输出流入下游

实现走 ADR-0010 `IPipelineTransform`，内部走 ADR-0046 `ScriptBlock.GetSteppablePipeline()`：

```csharp
[Verb("ForEach", Noun = "Object", Group = CommandGroup.Pipeline, PipelineOnly = true)]
public sealed class ForEachObjectCommand : IPipelineTransform
{
    public record Args(
        [property: Parameter(Position = 0)] ScriptBlock[]? Process,
        ScriptBlock? Begin,
        ScriptBlock? End);

    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input, CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pipelines = Args.Process?.Select(sb => sb.GetSteppablePipeline()).ToArray()
                        ?? Array.Empty<SteppablePipeline>();
        foreach (var p in pipelines) p.Begin();
        await foreach (var item in input.WithCancellation(ct))
        {
            using (ctx.PushPipelineScope(item))   // $_ = item per ADR-0047
                foreach (var p in pipelines)
                    foreach (var output in p.Process(item))
                        yield return WrapAsItem(output);
        }
        foreach (var p in pipelines)
            foreach (var output in p.End())
                yield return WrapAsItem(output);
    }
}
```

示例：

```powershell
Get-ChildItem | ForEach-Object { $_.Name.ToUpper() }
Get-Process | ForEach-Object -Begin { $sum = 0 } `
                       -Process { $sum += $_.WS } `
                       -End { Write-Output $sum }
1..10 | ForEach-Object { $_ * 2 }
```

#### 1.2 `Where-Object`

- **别名**：`where` / `?`
- **作用**：按脚本块条件过滤管道
- **参数**：
  - `-FilterScript <scriptblock>`（mandatory，alias `-ScriptBlock`）：返回 bool 的过滤脚本块
  - 简写模式：`-Property <string>`、`-Value <object>`、`-Operator <string>`（eq / ne / gt / lt / ge / le / like / match / in / contains / ...），等价于 `{ $_.$Property -$Operator $Value }
- **位置参数**：第一个位置参数等价于 `-FilterScript`
- **管道输入**：任意对象（`ValueFromPipeline`）
- **输出**：通过条件的项

简写模式与脚本块模式互斥，二者都给出抛 `ParameterBindingException`。

```powershell
Get-ChildItem | Where-Object { $_.Length -gt 1MB }
Get-Process | Where-Object WS -gt 100MB
Get-Service | Where-Object Status -eq "Running"
```

ADR-0023 M2 已定义的 DSL 字符串形式（`where size > 1MB`）作为兼容路径保留，但官方推荐脚本块形式。

#### 1.3 `Sort-Object`

- **别名**：`sort`
- **作用**：按属性或脚本块排序
- **参数**：
  - `-Property <object[]>`：属性名或脚本块（脚本块作为 key 函数）
  - `-Descending`：降序（可用 `-Descending:$false` 局部翻转）
  - `-Unique`：去重
  - `-Top <int>`：仅返回前 N 个（堆实现，不全量排序）
  - `-Bottom <int>`：仅返回后 N 个
  - `-Culture <string>`：排序文化（默认 InvariantCulture）
  - `-CaseSensitive`：字符串排序大小写敏感
- **性质**：buffering 节点（per ADR-0010 §6），内部缓存全部输入后排序输出
- **`-Top N` 优化**：维护 size-N 最大堆，不全量缓存

```powershell
Get-ChildItem | Sort-Object Length -Descending
Get-Process | Sort-Object { $_.WS } -Descending -Top 10
Get-ChildItem | Sort-Object Length, Name -Descending:$true, $false
```

#### 1.4 `Select-Object`

- **别名**：`select`
- **作用**：选取属性、首/尾 N、跳过、去重
- **参数**：
  - `-Property <object[]>`：属性名或脚本块（脚本块作为投影函数）；可用 `@{ Name = "X"; Expression = { ... } }` 计算属性
  - `-ExcludeProperty <string[]>`：排除属性
  - `-ExpandProperty <string>`：展开单个属性为数组（不做对象包装）
  - `-First <int>`（alias `-Head`）：取前 N（流式，不全量缓存）
  - `-Last <int>`（alias `-Tail`）：取后 N（buffering）
  - `-Skip <int>`：跳过 N
  - `-SkipLast <int>`：跳过末尾 N
  - `-Unique`：去重
  - `-Wait <int>`：等待 N 项后开始（保留前 N 的对象，流式）
  - `-Index <int[]>`：按索引取元素
- **管道输入**：`ValueFromPipeline`

```powershell
Get-Process | Select-Object -First 5 Name, Id, WS
Get-ChildItem | Select-Object -ExpandProperty Name
Get-ChildItem | Select-Object -Property Name, @{ Name = "SizeKB"; Expression = { $_.Length / 1KB } }
```

#### 1.5 `Group-Object`

- **别名**：`group`
- **作用**：按属性或脚本块分组
- **参数**：
  - `-Property <object[]>`：分组键
  - `-NoElement`：不保留组内元素（仅计数）
  - `-AsHashTable` / `-AsString`：返回 hashtable 而非 `GroupInfo` 对象
  - `-CaseSensitive`：分组键大小写敏感
- **输出**：`GroupInfo` 对象（`Name / Count / Group` 数组）
- **性质**：buffering

```powershell
Get-ChildItem | Group-Object Extension
Get-Process | Group-Object { if ($_.WS -gt 100MB) { "Big" } else { "Small" } }
```

#### 1.6 `Measure-Object`

- **别名**：`measure`
- **作用**：数值聚合
- **参数**：
  - `-Property <string[]>`：参与聚合的属性
  - `-Sum` / `-Average` / `-Max` / `-Min` / `-Count`：聚合类型
  - `-AllStats`：所有聚合
  - `-Line` / `-Word` / `-Character`：文本统计（对字符串输入）
  - `-IgnoreWhiteSpace`：文本统计忽略空白
- **输出**：`MeasureInfo` 对象数组（每个属性一组聚合结果）

```powershell
Get-ChildItem | Measure-Object Length -Sum -Average -Max -Min
Get-Content log.txt | Measure-Object -Line -Word -Character
```

#### 1.7 `Compare-Object`

- **别名**：`compare` / `diff`
- **作用**：对比两组对象
- **参数**：
  - `-ReferenceObject <object[]>`：参考集
  - `-DifferenceObject <object[]>`（pipeline bound）：差异集
  - `-Property <object[]>`：参与比较的属性
  - `-ExcludeDifferent`：仅显示相同
  - `-IncludeEqual`：显示相同（默认仅显示差异）
  - `-PassThru`：返回原始对象（带 side-indicator `<=` / `=>` / `==`）
  - `-SyncWindow <int>`：同步窗口（顺序无关比较的容差）
- **输出**：`ComparisonResult` 对象（`InputObject / SideIndicator`）

```powershell
Compare-Object (Get-Content a.txt) (Get-Content b.txt)
Compare-Object -ReferenceObject $old -DifferenceObject $new -Property Name -IncludeEqual
```

#### 1.8 `Tee-Object`

- **别名**：`tee`
- **作用**：分流管道到文件 / 变量，同时继续传下游
- **参数**：
  - `-FilePath <string>`（alias `-Path`）：写入文件
  - `-Variable <string>`：写入变量
  - `-Append`：追加（仅与 `-FilePath` 共用）
  - `-Encoding <Encoding>`：编码（默认 UTF-8 无 BOM）
  - `-InputObject <object>`（pipeline bound）
- **性质**：流式 sink + 透传（既写入目标又 yield 到下游）

```powershell
Get-Process | Tee-Object -FilePath proc.txt | Select-Object -First 5
Get-ChildItem | Tee-Object -Variable files | Measure-Object
```

### 2. Output Cmdlets（CRITICAL）

PowerShell 的六流模型是脚本可观测性的核心。本组 cmdlet 实现 ADR-0026 错误流之外的五个流：

| 流 | Cmdlet | 默认可见性 | 控制变量 |
|---|---|---|---|
| Success (1) | `Write-Output` | 始终 | — |
| Error (2) | `Write-Error` | 始终（写 stderr） | `$ErrorActionPreference` |
| Warning (3) | `Write-Warning` | 始终（黄字） | `$WarningPreference` |
| Verbose (4) | `Write-Verbose` | 仅 `-Verbose` 或 `$VerbosePreference = "Continue"` | `$VerbosePreference` |
| Debug (5) | `Write-Debug` | 仅 `-Debug` 或 `$DebugPreference = "Continue"` | `$DebugPreference` |
| Information (6) | `Write-Information` / `Write-Host` | `$InformationPreference` | `$InformationPreference` |
| Host (直连) | `Write-Host` | 始终（绕过管道，直接 host UI） | — |

ADR-0047 §1 已确认 `$VerbosePreference` / `$DebugPreference` / `$WarningPreference` / `$InformationPreference` 是 Global 作用域自动变量，默认值 `"SilentlyContinue"`（除 `$WarningPreference` 默认 `"Continue"`）。

#### 2.1 `Write-Output`

- **作用**：写入 success 流（默认隐式调用）
- **参数**：
  - `-InputObject <object>`（mandatory，pipeline bound）
  - `-NoEnumerate`：不展开数组（默认会展开，单元素视为单元素）
- **行为**：把输入对象 yield 到下游，等价于"显式 success 流写入"

```powershell
Write-Output "Hello"
1, 2, 3 | Write-Output           # 三行输出
,@(1, 2, 3) | Write-Output -NoEnumerate  # 单元素（数组本身）
```

`Write-Output` 与直接表达式语句的区别：在函数体内，`return $x` 与 `$x; return` 等价（均走 success 流），但 `Write-Output $x` 更明确。

#### 2.2 `Write-Host`

- **作用**：直接写入 host UI（不进入 success 流）
- **参数**：
  - `-Object <object>`（mandatory，pipeline bound）
  - `-ForegroundColor <ConsoleColor>`：前景色
  - `-BackgroundColor <ConsoleColor>`：背景色
  - `-NoNewline`：不换行
  - `-Separator <string>`：多对象分隔符（默认换行）
- **行为**：写 `IHostUI`（per ADR-0014），不进入 success 流，故无法被管道下游捕获（与 PowerShell 5+ 一致；PS 3-4 中 `Write-Host` 走 success 流，此差异已记录在迁移文档）

```powershell
Write-Host "Hello" -ForegroundColor Green
Write-Host "Progress: " -NoNewline; Write-Host "50%" -ForegroundColor Yellow
```

#### 2.3 `Write-Error`

- **作用**：写入 error 流
- **参数**：
  - `-Message <string>`：错误消息
  - `-Exception <Exception>`：原始异常
  - `-ErrorId <string>`：错误标识
  - `-Category <ErrorCategory>`：错误分类（per ADR-0026 §2）
  - `-TargetObject <object>`：目标对象
  - `-CategoryActivity <string>` / `-CategoryReason <string>` / `-CategoryTargetName <string>` / `-CategoryTargetType <string>`：分类元数据
  - `-RecommendedAction <string>`：建议动作（per ADR-0026 §8 `Suggestion`）
- **行为**：构造 `ErrorRecord`（per ADR-0026 §1）写入 `IErrorStream`，错误是否终止由 `$ErrorActionPreference` 控制（per ADR-0026 §13）

```powershell
Write-Error "File not found: $path"
Write-Error -Message "Invalid input" -Category InvalidArgument -ErrorId "INV001" -TargetObject $input
```

#### 2.4 `Write-Warning`

- **作用**：写入 warning 流（黄色字）
- **参数**：`-Message <string>`（mandatory）
- **行为**：根据 `$WarningPreference` 决定显示 / 静默 / 终止

```powershell
Write-Warning "Disk space low"
$WarningPreference = "SilentlyContinue"; Write-Warning "invisible"
```

#### 2.5 `Write-Verbose`

- **作用**：写入 verbose 流
- **参数**：`-Message <string>`（mandatory）
- **行为**：仅当 `$VerbosePreference = "Continue"` 或命令调用带 `-Verbose` 时显示

```powershell
Write-Verbose "Processing item $i of $total"
Get-ChildItem -Verbose   # 内部 cmdlet 自动产出 verbose 消息
```

#### 2.6 `Write-Debug`

- **作用**：写入 debug 流
- **参数**：`-Message <string>`（mandatory）
- **行为**：仅当 `$DebugPreference = "Continue"` 或命令调用带 `-Debug` 时显示；`$DebugPreference = "Inquire"` 时弹出确认（PowerShell 兼容行为）

```powershell
Write-Debug "Entering function X with $args"
```

#### 2.7 `Write-Information`

- **作用**：写入 information 流（PowerShell 5+）
- **参数**：
  - `-MessageData <object>`（mandatory）
  - `-Tags <string[]>`：标签（用于 `Get-Information` 过滤）
- **行为**：根据 `$InformationPreference` 决定可见性；与 `Write-Host` 区别在于 `Write-Information` 产出 `InformationRecord` 对象，可被 `6>` 重定向捕获

```powershell
Write-Information -MessageData "Audit log entry" -Tags "Security"
Get-Command | 6> info.txt   # 捕获 information 流到文件
```

#### 2.8 `Write-Progress`

- **作用**：显示进度条
- **参数**：
  - `-Activity <string>`（mandatory）：顶层活动名
  - `-Status <string>`：当前状态
  - `-Id <int>`：活动 ID（用于多并发进度条区分）
  - `-ParentId <int>`：父活动 ID（嵌套进度）
  - `-PercentComplete <int>`：0-100
  - `-SecondsRemaining <int>`：剩余秒数
  - `-CurrentOperation <string>`：当前操作
  - `-RecordType <ProgressRecordType>`：`Processing` / `Completed` / `Error`
- **行为**：通过 ADR-0044 `ITaskCenter` 推送 `ProgressRecord`；CLI host 显示单行进度条，GUI host 显示对话框

```powershell
for ($i = 0; $i -lt 100; $i++) {
    Write-Progress -Activity "Processing" -Status "$i%" -PercentComplete $i
    Start-Sleep -Milliseconds 50
}
Write-Progress -Activity "Processing" -Completed
```

#### 2.9 `Out-*` 家族

ADR-0010 §2 已规定管道结尾无 Sink 时默认调 `Out-Default`。本节定义 `Out-*` 家族的具体命令。

##### 2.9.1 `Out-Default`

- **作用**：默认输出 sink（管道结尾自动调用）
- **参数**：
  - `-InputObject <object>`（pipeline bound）
  - `-Transcript`：写入 transcript 而非 host
- **行为**：委托 ADR-0011 格式化器（`Format-Table` / `Format-List` 自动选择）→ 调 `IHostUI.WriteLine`

##### 2.9.2 `Out-Null`

- **作用**：丢弃输出
- **参数**：`-InputObject <object>`（pipeline bound）
- **行为**：消费输入但不写入任何流

```powershell
Get-ChildItem -Recurse | Out-Null    # 仅用于触发副作用
```

##### 2.9.3 `Out-Host`

- **作用**：分页输出到 host
- **参数**：
  - `-InputObject <object>`（pipeline bound）
  - `-Paging`：分页（按终端高度）
- **行为**：调用 `IHostUI.PagingWrite`，每页暂停等待按键

##### 2.9.4 `Out-String`

- **作用**：把对象渲染为字符串
- **参数**：
  - `-InputObject <object>`（pipeline bound）
  - `-Stream`：每行作为一个字符串返回（默认返回单字符串）
  - `-Width <int>`：行宽（默认终端宽度，无终端时 80）
- **行为**：调格式化器生成字符串输出到 success 流

```powershell
Get-Process | Out-String -Stream | Select-String "powershell"
```

##### 2.9.5 `Out-GridView`

- **别名**：`ogv`
- **作用**：GUI 网格视图
- **参数**：
  - `-InputObject <object>`（pipeline bound）
  - `-Title <string>`
  - `-PassThru`：返回选中项
  - `-OutputMode <OutputMode>`：`None` / `Single` / `Multiple`
- **行为**：GUI host 弹出 `GridViewWindow`（per ADR-0043）；CLI host 降级为 `Format-Table` 输出 + warning

```powershell
Get-Process | Out-GridView -Title "Processes" -PassThru | Stop-Process
```

##### 2.9.6 `Out-File`

- **别名**：`>` / `>>`（重定向运算符映射到此 cmdlet）
- **作用**：输出到文件
- **参数**：
  - `-FilePath <string>`（mandatory）
  - `-Append`：追加
  - `-Encoding <Encoding>`：默认 UTF-8 无 BOM
  - `-Width <int>`：行宽
  - `-NoNewline`：末尾不换行
- **行为**：sink，调格式化器渲染后写入文件

```powershell
Get-Process | Out-File proc.txt
Get-Process | Out-File proc.txt -Encoding utf8BOM
```

### 3. Path Cmdlets（CRITICAL）

#### 3.1 `Test-Path`

- **作用**：判断路径是否存在
- **参数**：
  - `-Path <string[]>`（mandatory，pipeline bound）
  - `-PathType <TestPathType>`：`Any`（默认）/ `Container` / `Leaf`
  - `-IsValid`：仅检查路径语法是否合法（不查文件系统）
  - `-Include <string[]>` / `-Exclude <string[]>`：glob 过滤
  - `-LiteralPath <string[]>`：禁止通配符
- **返回**：`bool`

```powershell
Test-Path "C:/Users/blmpt"
Test-Path "C:/Windows" -PathType Container
Test-Path "HKLM:\SOFTWARE\Microsoft" -PathType Container
Test-Path "C:/?.txt" -IsValid   # 检查路径合法性
```

#### 3.2 `Resolve-Path`

- **作用**：解析相对 / 通配路径为绝对路径
- **参数**：
  - `-Path <string[]>`（mandatory，pipeline bound）
  - `-Relative`：返回相对路径（相对当前 `$PWD`）
  - `-LiteralPath <string[]>`
- **返回**：`PathInfo` 对象（`.ProviderPath` / `.Drive`）
- **行为**：通配符展开（`C:/Users/*` → 多条路径）

```powershell
Resolve-Path .
Resolve-Path "C:/Users/*/Downloads" -Relative
```

#### 3.3 `Split-Path`

- **作用**：返回路径的一部分
- **参数**：
  - `-Path <string[]>`（mandatory，pipeline bound）
  - `-Parent`：父目录（默认）
  - `-Leaf`：叶子（文件名 / 子目录名）
  - `-Qualifier`：盘符部分（`C:` / `HKLM:`）
  - `-NoQualifier`：去掉盘符
  - `-IsAbsolute`：判断是否绝对路径
  - `-LiteralPath <string[]>`
- **返回**：字符串（多输入时返回数组）

```powershell
Split-Path "C:/Users/blmpt/file.txt" -Leaf        # file.txt
Split-Path "C:/Users/blmpt/file.txt" -Parent       # C:/Users/blmpt
Split-Path "C:/Users/blmpt/file.txt" -Qualifier     # C:
Split-Path "C:/Users/blmpt/file.txt" -NoQualifier   # /Users/blmpt/file.txt
Split-Path "C:/Users/blmpt" -IsAbsolute             # $true
```

#### 3.4 `Join-Path`

- **作用**：用 OS 正确的分隔符拼接路径段
- **参数**：
  - `-Path <string>`（mandatory，第一段）
  - `-ChildPath <string>`（mandatory，第二段）
  - `-AdditionalChildPath <string[]>`：可变参数，追加多段（PS 6+ 行为；OpenShell 兼容）
  - `-Resolve`：拼接后解析实际路径（通配符展开）
- **返回**：字符串

```powershell
Join-Path "C:/Users" "blmpt"             # C:/Users/blmpt（Windows）/ C:/Users/blmpt（Unix）
Join-Path "C:/" "Users" "blmpt" "file.txt"  # 多段拼接
Join-Path $env:HOME "Documents" -Resolve   # 解析为绝对路径
```

实现使用 `System.IO.Path.Combine` + 平台适配（Windows 用 `\`，Unix 用 `/`；OpenShell 内部统一用 `/`，输出时按 OS 转换）。

#### 3.5 `Convert-Path`

- **作用**：把 PowerShell 路径（含 Provider 前缀如 `fs::` / `Variable:` / `HKLM:`）转为文件系统路径
- **参数**：
  - `-Path <string[]>`（mandatory，pipeline bound）
  - `-LiteralPath <string[]>`
- **返回**：字符串
- **行为**：仅对文件系统 Provider 有意义；其他 Provider 抛 `InvalidOperationException`

```powershell
Convert-Path "fs::C:/Users/blmpt"
Convert-Path "."    # 当前目录绝对路径
```

#### 3.6 `Push-Location` / `Pop-Location`

- **别名**：`pushd` / `popd`
- **Push-Location 参数**：
  - `-Path <string>`（位置 0）：目标路径
  - `-LiteralPath <string>`
  - `-StackName <string>`：压入指定栈（默认 default 栈）
- **Pop-Location 参数**：
  - `-StackName <string>`：从指定栈弹出
- **行为**：维护 `LocationStack` 字典（`<stackName, Stack<ItemPath>>`）；`Set-Location` 仅切换不压栈，`Push-Location` 先压当前 `$PWD` 再切换

```powershell
Push-Location "C:/Users"
Push-Location "../blmpt" -StackName "user"
Get-Location            # 当前
Pop-Location -StackName "user"
Pop-Location
```

#### 3.7 `Get-Location`

- **别名**：`pwd` / `gl`
- **作用**：返回当前位置（ADR-0023 M1 已交付，本 ADR 仅说明参数对齐）
- **参数**：
  - `-PSDrive <string[]>`：返回指定 Drive 的位置
  - `-PSProvider <string[]>`：返回指定 Provider 的位置
  - `-Stack`：返回 location 栈而非当前位置
  - `-StackName <string>`：指定栈

### 4. Object Reflection（IMPORTANT）

#### 4.1 `Get-Member`

- **别名**：`gm`
- **作用**：列出输入对象的属性 / 方法
- **参数**：
  - `-InputObject <object>`（pipeline bound）
  - `-MemberType <MemberTypes>`：过滤类型（`Property` / `Method` / `NoteProperty` / `ScriptProperty` / ...）
  - `-Name <string[]>`：按名称过滤
  - `-Static`：列出静态成员
  - `-View <MemberView>`：`Extended` / `Adapted` / `Base` / `All`
  - `-Force`：包含隐藏 / 内部成员
- **返回**：`MemberDefinition` 对象数组（`TypeName / Name / MemberType / Definition`）

```powershell
Get-ChildItem | Get-Member
"hello" | Get-Member -MemberType Method
[math] | Get-Member -Static
```

实现走 ADR-0047 §4 的反射缓存：`(Type, View)` → `MemberInfo[]`。

#### 4.2 `Select-Object -ExpandProperty`

见 §1.4。`-ExpandProperty` 是 `Select-Object` 的特化模式：跳过对象包装，直接返回属性值数组。语义等价 PowerShell：

```powershell
(Get-ChildItem).Name                      # 等价
Get-ChildItem | Select-Object -ExpandProperty Name
```

#### 4.3 `New-Object`

- **作用**：创建 .NET 对象
- **参数**：
  - `-TypeName <string>`（mandatory，位置 0）：类型全名
  - `-ArgumentList <object[]>`：构造参数
  - `-ComObject <string>`：创建 COM 对象（仅 Windows）
  - `-StrictType`：禁用类型回退（找不到精确类型抛错）
- **行为**：
  - `TypeName`：先查 `Type.GetType(name, throwOnError: false)`，失败再扫 `AppDomain.GetAssemblies()` 公开类型
  - `ComObject`：`Type.GetTypeFromProgID` + `Activator.CreateInstance`（仅 Windows，Unix 抛 `PlatformNotSupportedException`）
  - 构造参数走 ADR-0047 §3 类型转换表
- **安全**：受 ADR-0036 §12 进程生成权限约束（创建 COM 对象需 `ProcessSpawn` 权限）

```powershell
$o = New-Object System.Random
$o = New-Object System.Net.WebClient -ArgumentList $proxy
$excel = New-Object -ComObject Excel.Application   # Windows only
```

#### 4.4 `Compare-Object`

见 §1.7。重申作为对象反射工具的用途：找出两组对象在指定属性上的差异。

#### 4.5 `Measure-Command`

- **作用**：计时脚本块执行
- **参数**：
  - `-Expression <scriptblock>`（mandatory，位置 0）
  - `-InputObject <object>`（pipeline bound）
- **返回**：`TimeSpan`
- **行为**：在子作用域内执行脚本块，捕获异常时间，输出 success 流不受影响

```powershell
Measure-Command { Get-ChildItem -Recurse }
Measure-Command { 1..10000 | ForEach-Object { $_ * 2 } }
```

#### 4.6 `Trace-Command`

- **作用**：追踪命令执行（调试用）
- **参数**：
  - `-Name <string[]>`（mandatory）：trace 模块名（`ParameterBinding` / `CmdletProviderClasses` / `TypeConversion` / ...）
  - `-Expression <scriptblock>`（mandatory，位置 0）：被追踪的脚本块
  - `-Option <TraceOptions>`：`None` / `Constructor` / `Dispose` / `ExecutionFlow` / `Data` / `Errors` / `...`
  - `-Listener <TraceListener>`：自定义监听器（默认 `TextTraceListener` 写 stderr）
  - `-FilePath <string>` / `-FileListener`
- **返回**：脚本块的输出（透传）
- **行为**：开启 `System.Diagnostics.Trace` 监听器，执行脚本块，关闭监听器

```powershell
Trace-Command -Name ParameterBinding -Expression { Get-ChildItem *.txt } -FilePath trace.log
```

### 5. Content Cmdlets（IMPORTANT）

#### 5.1 `Get-Content`（已存在 M1）

- **别名**：`gc` / `cat` / `type`
- **参数**：
  - `-Path <string[]>` / `-LiteralPath <string[]>`（mandatory，pipeline bound）
  - `-ReadCount <long>`：每次读取行数（默认 1，`-TotalCount` 是总行数）
  - `-TotalCount <long>`：最多读取行数（alias `-Head` / `-First`）
  - `-Tail <int>`：从末尾读取
  - `-Encoding <Encoding>`：默认 UTF-8
  - `-Delimiter <string>`：自定义分隔符（默认 `\n`）
  - `-Wait`：跟随文件变化（tail -f）
  - `-Raw`：返回单字符串（不分行）
- **返回**：字符串数组（默认）或单字符串（`-Raw`）
- **性质**：流式 `IPipelineSource`

```powershell
Get-Content file.txt
Get-Content file.txt -TotalCount 10
Get-Content file.txt -Tail 5 -Wait    # tail -f
Get-Content file.txt -Raw
```

#### 5.2 `Set-Content`（已存在 M1）

- **别名**：`sc`
- **参数**：
  - `-Path <string[]>` / `-LiteralPath <string[]>`（mandatory）
  - `-Value <object[]>`（mandatory，pipeline bound）
  - `-Encoding <Encoding>`：默认 UTF-8 无 BOM
  - `-Force`：覆盖只读
  - `-NoNewline`：不追加末尾换行
  - `-WhatIf` / `-Confirm`（per ADR-0049，破坏性）
- **行为**：清空 + 写入

#### 5.3 `Add-Content`（NEW）

- **别名**：`ac`
- **作用**：追加内容到文件（不存在则创建）
- **参数**：
  - `-Path <string[]>` / `-LiteralPath <string[]>`（mandatory）
  - `-Value <object[]>`（mandatory，pipeline bound）
  - `-Encoding <Encoding>`：默认 UTF-8 无 BOM
  - `-Force`：覆盖只读
  - `-NoNewline`：不追加末尾换行（每行间用 `\n` 分隔仍生效）
  - `-WhatIf` / `-Confirm`
- **行为**：以 `Append` 模式打开文件写入；若文件不存在则等价 `Set-Content`

```powershell
Add-Content log.txt "Entry at $(Get-Date)"
Get-Date | Add-Content timestamp.txt
```

#### 5.4 `Clear-Content`（NEW）

- **别名**：`clc`
- **作用**：清空文件内容但保留文件（与 `Remove-Item` 区别）
- **参数**：
  - `-Path <string[]>` / `-LiteralPath <string[]>`（mandatory）
  - `-Force`：覆盖只读
  - `-WhatIf` / `-Confirm`
- **行为**：截断文件为 0 字节，保留 inode / 文件元数据

```powershell
Clear-Content log.txt   # 文件保留，内容清空
```

#### 5.5 `Get-Item` / `Set-Item` / `Remove-Item` / `Move-Item`

ADR-0023 M1 已交付。本 ADR 仅声明：

- `Set-Item -Path Variable:Name -Value $x` 走 ADR-0047 §10 的 `Variable:` Provider
- `Remove-Item Variable:Name` 同理
- `Move-Item` 在 `Variable:` Provider 上等价 `Rename`（变量无路径层级）

### 6. Conversion Cmdlets（IMPORTANT）

#### 6.1 `ConvertTo-Json`

- **作用**：对象转 JSON 字符串
- **参数**：
  - `-InputObject <object>`（mandatory，pipeline bound）
  - `-Depth <int>`：序列化深度（默认 2，与 PowerShell 一致；建议显式指定以避免浅输出）
  - `-Compress`：压缩输出（无空白）
  - `-AsArray`：即使单个输入也包装为数组（PS 6+）
- **返回**：字符串
- **实现**：`System.Text.Json` + 自定义 `IItemConverter`（OpenShell `IItem` 序列化为 `@{ Path = ...; Properties = @{ ... } }`）
- **限制**：循环引用抛 `JsonException`；`scriptblock` / `IItem` 中含函数引用时跳过并 warning

```powershell
Get-Process | Select-Object Name, Id | ConvertTo-Json -Depth 5
@{ Name = "test"; Items = @(1, 2, 3) } | ConvertTo-Json -Compress
```

#### 6.2 `ConvertFrom-Json`

- **作用**：JSON 字符串转 `PSCustomObject` / hashtable
- **参数**：
  - `-InputObject <string>`（mandatory，pipeline bound）
  - `-AsHashtable`：返回 `Hashtable` 而非 `PSCustomObject`（PS 6+）
  - `-Depth <int>`：反序列化深度（默认 1024）
- **返回**：`PSCustomObject`（默认）或 `Hashtable`（`-AsHashtable`）
- **实现**：`System.Text.Json` + 自定义 converter 把 `JsonObject` / `JsonArray` 包装为 `PSCustomObject` / `object[]`

```powershell
$config = Get-Content config.json -Raw | ConvertFrom-Json
$config.Server.Port
'{"a":1,"b":[2,3]}' | ConvertFrom-Json -AsHashtable
```

#### 6.3 `ConvertTo-Csv`

- **作用**：对象转 CSV 字符串
- **参数**：
  - `-InputObject <object>`（mandatory，pipeline bound）
  - `-NoTypeInformation`：去掉首行 `#TYPE` 头（PS 5.1 默认含，PS 6+ 默认去；OpenShell 默认去以简化输出，与 PS 5.1 的差异记录在迁移文档）
  - `-Delimiter <char>`：分隔符（默认 `,`，可用 `;` 等）
  - `-UseQuotes <QuoteBehavior>`：`Always` / `Never` / `AsNeeded`（PS 7+）
  - `-QuoteFields <string[]>`
- **返回**：CSV 字符串数组（每行一个）

```powershell
Get-Process | Select-Object Name, Id | ConvertTo-Csv -NoTypeInformation
```

#### 6.4 `ConvertFrom-Csv`

- **作用**：CSV 字符串转 `PSCustomObject`
- **参数**：
  - `-InputObject <string[]>`（mandatory，pipeline bound）
  - `-Header <string[]>`：自定义列名（无表头时）
  - `-Delimiter <char>`
- **返回**：`PSCustomObject[]`

```powershell
Get-Content data.csv | ConvertFrom-Csv -Delimiter ","
"a,b,c`n1,2,3" -split "`n" | ConvertFrom-Csv -Header A, B, C
```

#### 6.5 `Import-Csv`

- **作用**：从 CSV 文件读取对象
- **参数**：
  - `-Path <string[]>` / `-LiteralPath <string[]>`（mandatory，pipeline bound）
  - `-Header <string[]>`
  - `-Delimiter <char>`
  - `-Encoding <Encoding>`：默认 UTF-8
- **返回**：`PSCustomObject[]`（流式）

```powershell
Import-Csv users.csv | Where-Object { $_.Active -eq "true" }
```

#### 6.6 `Export-Csv`

- **作用**：对象写入 CSV 文件
- **参数**：
  - `-InputObject <object>`（pipeline bound）
  - `-Path <string>`（mandatory）
  - `-NoTypeInformation`（默认 true，与 PS 6+ 一致）
  - `-Delimiter <char>`
  - `-Append`
  - `-Force`：覆盖只读
  - `-Encoding <Encoding>`：默认 UTF-8
  - `-NoClobber`：不覆盖已存在文件
  - `-WhatIf` / `-Confirm`
- **行为**：首行写表头，后续行写值

```powershell
Get-Process | Export-Csv procs.csv -NoTypeInformation
```

#### 6.7 `ConvertTo-Html`

- **作用**：对象转 HTML 片段
- **参数**：
  - `-InputObject <object>`（pipeline bound）
  - `-Property <object[]>`
  - `-Head <string[]>`：HTML `<head>` 内容
  - `-PreContent <string>` / `-PostContent <string>`
  - `-Title <string>`：页面标题
  - `-Body <string>`：`<body>` 内容
  - `-Fragment`：仅生成 `<table>` 片段
  - `-As <StringAs>`：`Table`（默认）/ `List`
  - `-CssUri <string>`：CSS 链接
- **返回**：HTML 字符串

```powershell
Get-Process | ConvertTo-Html -Title "Processes" | Out-File procs.html
Get-ChildItem | ConvertTo-Html Name, Length -Fragment
```

#### 6.8 `ConvertTo-Xml`

- **作用**：对象转 XML（Clixml 风格）
- **参数**：
  - `-InputObject <object>`（mandatory，pipeline bound）
  - `-Depth <int>`：默认 1（仅顶层对象）
  - `-As <StringAs>`：`Stream`（默认，多文档）/ `String`（单字符串）/ `Document`（`XmlDocument` 对象）
  - `-NoTypeInformation`
- **返回**：XML 字符串 / `XmlDocument`
- **实现**：`XmlSerializer` + 自定义 `IItemXmlConverter`

```powershell
Get-Process | ConvertTo-Xml -Depth 2 -As String
```

### 7. Process Cmdlets（IMPORTANT）

#### 7.1 `Get-Process`

- **别名**：`ps` / `gps`
- **作用**：列出进程
- **参数**：
  - `-Name <string[]>`：进程名过滤（通配符）
  - `-Id <int[]>`：PID 过滤
  - `-InputObject <Process[]>`（pipeline bound）
  - `-ComputerName <string>`：远程（PS 5.1，OpenShell M5+ 实现，需 SSH / WinRM）
  - `-Module`：列出加载的模块
  - `-FileVersionInfo`：返回文件版本信息
- **返回**：`Process` 对象数组
- **安全**：访问其他用户进程信息受 `ProcessInspect` 权限（ADR-0036 §12）

```powershell
Get-Process
Get-Process -Name "powershell*"
Get-Process -Id $PID
```

#### 7.2 `Start-Process`

- **别名**：`saps` / `start`
- **作用**：启动外部进程
- **参数**：
  - `-FilePath <string>`（mandatory，位置 0）：可执行文件路径
  - `-ArgumentList <string[]>`：命令行参数
  - `-WorkingDirectory <string>`
  - `-WindowStyle <ProcessWindowStyle>`：`Normal` / `Hidden` / `Minimized` / `Maximized`
  - `-Credential <PSCredential>`：以其他用户身份运行（仅 Windows）
  - `-LoadUserProfile` / `-NoNewWindow`：仅 Windows
  - `-PassThru`：返回 `Process` 对象
  - `-Wait`：等待退出
  - `-RedirectStandardInput <string>` / `-RedirectStandardOutput <string>` / `-RedirectStandardError <string>`
  - `-Verb <string>`：Shell verb（Windows：`open` / `edit` / `runas` / `print`）
  - `-UseNewEnvironment`：不继承父进程环境变量
- **安全**：受 ADR-0036 §12 `ProcessSpawn` 权限约束，沙箱默认禁止；显式允许时仍需用户确认（除非 `-WhatIf` 已显式跳过）

```powershell
Start-Process notepad.exe
Start-Process -FilePath "ping" -ArgumentList "localhost" -Wait -NoNewWindow
Start-Process -FilePath "setup.exe" -Verb "runas"   # 提权
$proc = Start-Process -FilePath "long.exe" -PassThru; Wait-Process -Id $proc.Id
```

#### 7.3 `Stop-Process`

- **别名**：`spps` / `kill`
- **作用**：终止进程
- **参数**：
  - `-Id <int[]>`（mandatory 之一）
  - `-Name <string[]>`
  - `-InputObject <Process[]>`（pipeline bound）
  - `-Force`：强制（`kill -9` 语义）
  - `-PassThru`：返回被终止的 `Process` 对象
  - `-WhatIf` / `-Confirm`（破坏性）
- **安全**：终止其他用户进程需 `ProcessTerminate` 权限；自身进程默认允许

```powershell
Get-Process notepad | Stop-Process -Force
Stop-Process -Name "chrome" -WhatIf
Stop-Process -Id 1234, 5678
```

#### 7.4 `Wait-Process`

- **作用**：等待进程退出
- **参数**：
  - `-Name <string[]>`
  - `-Id <int[]>`
  - `-InputObject <Process[]>`（pipeline bound）
  - `-Timeout <int>`：超时（秒），超时抛 `TimeoutException`
- **行为**：阻塞当前管道直到目标进程退出；超时返回错误

```powershell
Get-Process "long-runner" | Wait-Process -Timeout 60
```

#### 7.5 `Debug-Process`

- **作用**：附加调试器（Windows only）
- **参数**：
  - `-Name <string[]>`
  - `-Id <int[]>`
  - `-InputObject <Process[]>`（pipeline bound）
- **行为**：Windows 调用 `Debugger.Break`；Unix 抛 `PlatformNotSupportedException`

```powershell
Debug-Process -Name "myapp"
```

### 8. Web Cmdlets（IMPORTANT）

#### 8.1 `Invoke-WebRequest`

- **别名**：`iwr` / `curl` / `wget`
- **作用**：HTTP 请求
- **参数**：
  - `-Uri <Uri>`（mandatory，位置 0）
  - `-Method <WebRequestMethod>`：`Default` / `Get` / `Post` / `Put` / `Delete` / `Head` / `Options` / `Patch` / `Trace`
  - `-Body <object>`：请求体（string / byte[] / `IDictionary` 表单 / `MultipartFormDataContent`）
  - `-Headers <IDictionary>`
  - `-ContentType <string>`
  - `-Credential <PSCredential>` / `-UseDefaultCredentials`
  - `-Proxy <Uri>` / `-ProxyCredential <PSCredential>` / `-ProxyUseDefaultCredentials`
  - `-TimeoutSec <int>`
  - `-UseBasicParsing`：禁用 DOM 解析（OpenShell 始终 BasicParsing，此参数为兼容保留）
  - `-Session <WebRequestSession>` / `-SessionVariable <string>`：cookie 会话
  - `-WebSession <WebRequestSession>`（alias `-Session`）
  - `-UserAgent <string>`
  - `-SkipCertificateCheck`：跳过 TLS 校验（PS 6+，OpenShell 默认 false）
  - `-Certificate <X509Certificate>` / `-CertificateThumbprint <string>`
  - `-Authentication <Authentication>`：`None` / `Basic` / `Bearer` / `OAuth` / `NTLM` / `Negotiate`
  - `-Token <string>` / `-Authorization <string>`
  - `-Form <IDictionary>`：multipart/form-data 自动构造
  - `-NoProxy` / `-DisableKeepAlive` / `-MaximumRedirection <int>` / `-PreserveAuthorizationOnRedirect`
  - `-InFile <string>` / `-OutFile <string>`
  - `-PassThru`：返回响应同时写入文件
  - `-ResponseHeadersVariable <string>`：把响应头存入变量
  - `-StatusCodeVariable <string>`：把状态码存入变量
  - `-SkipHttpErrorCheck`：HTTP 错误不抛异常（PS 6+）
- **返回**：`BasicHtmlWebResponseObject`（`.Content` / `.StatusCode` / `.Headers` / `.BaseResponse` / `.RawContentStream`）
- **安全**：受 ADR-0036 §11 `NetworkAccess` 权限约束；沙箱默认禁止外网

```powershell
Invoke-WebRequest "https://api.github.com/repos/openshell/openshell"
$response = Invoke-WebRequest -Uri $url -Method Post -Body $json -ContentType "application/json" -Headers @{ Authorization = "Bearer $token" }
Invoke-WebRequest -Uri $url -OutFile file.zip
```

#### 8.2 `Invoke-RestMethod`

- **别名**：`irm`
- **作用**：HTTP 请求并自动解析响应（JSON / XML）
- **参数**：与 `Invoke-WebRequest` 相同，加：
  - `-ResponseHeadersVariable <string>`：同 IWR
  - `-SkipHttpErrorCheck`
  - `-ContentType` 默认推断（`Content-Type: application/json` → JSON 解析）
- **返回**：解析后的对象（JSON → `PSCustomObject` / XML → `XmlDocument`）
- **行为**：响应 `Content-Type` 决定解析：
  - `application/json` → `ConvertFrom-Json` 自动调用
  - `application/xml` / `text/xml` → `XmlDocument`
  - `text/plain` → string
  - 其他 → byte[]

```powershell
$user = Invoke-RestMethod "https://api.github.com/users/octocat"
$user.login   # octocat

$resp = Invoke-RestMethod -Uri $url -Method Post -Body $json -ContentType "application/json" -Headers @{ Authorization = "Bearer $token" }
```

### 9. Utility Cmdlets（IMPORTANT）

#### 9.1 `Measure-Command`

见 §4.5。

#### 9.2 `Start-Sleep`

- **别名**：`sleep`
- **作用**：暂停脚本
- **参数**：
  - `-Seconds <int>`（mandatory 之一，与 `-Milliseconds` 互斥）
  - `-Milliseconds <int>`
- **行为**：`Task.Delay` 实现，`CancellationToken` 透传，可被 Ctrl+C 中断

```powershell
Start-Sleep -Seconds 5
Start-Sleep -Milliseconds 500
```

#### 9.3 `New-TimeSpan`

- **作用**：构造 `TimeSpan`
- **参数**：
  - `-Start <DateTime>` / `-End <DateTime>`：起止时间
  - `-Days <int>` / `-Hours <int>` / `-Minutes <int>` / `-Seconds <int>` / `-Milliseconds <int>`
- **返回**：`TimeSpan`

```powershell
New-TimeSpan -Start (Get-Date) -End "2026-12-31"
New-TimeSpan -Hours 1 -Minutes 30
```

#### 9.4 `Get-Date`

- **别名**：无（PS 的 `date` 在 OpenShell 中保留为 unix-like 别名）
- **作用**：当前日期 / 时间
- **参数**：
  - `-Date <DateTime>`：基准日期（默认 `DateTime.Now`）
  - `-Year <int>` / `-Month <int>` / `-Day <int>` / `-Hour <int>` / `-Minute <int>` / `-Second <int>` / `-Millisecond <int>`：调整字段
  - `-Format <string>`：.NET 格式化字符串（`"yyyy-MM-dd HH:mm:ss"`）
  - `-UFormat <string>`：Unix `strftime` 风格（兼容 PS）
  - `-AsUTC`：返回 UTC 时间
- **返回**：`DateTime`（默认）或字符串（`-Format` / `-UFormat`）

```powershell
Get-Date
Get-Date -Format "yyyy-MM-dd"
Get-Date -UFormat "%Y-%m-%d %H:%M"
(Get-Date).AddDays(7)
```

#### 9.5 `Set-Date`

- **作用**：设置系统时间
- **参数**：`-Date <DateTime>` / `-Adjust <TimeSpan>`
- **安全**：管理员权限；非管理员抛 `UnauthorizedAccessException`
- **平台**：Windows 走 `SetSystemTime`；Unix 走 `settimeofday`（需 root）

```powershell
Set-Date -Adjust (New-TimeSpan -Minutes 5)   # 调整 5 分钟
Set-Date (Get-Date "2026-12-31 23:59:59")
```

#### 9.6 `Get-Random`

- **作用**：随机数 / 随机元素
- **参数**：
  - `-Maximum <int>`：上界（不含），默认 `Int32.MaxValue`
  - `-Minimum <int>`：下界（含），默认 0
  - `-Count <int>`：从集合中抽取 N 个（不重复）
  - `-InputObject <object[]>`（pipeline bound）
  - `-SetSeed <int>`：种子（用于可重现测试）
- **返回**：随机数（int / double / 集合元素）

```powershell
Get-Random -Maximum 100
Get-Random -Minimum 1 -Maximum 7
Get-Random -InputObject (1..50) -Count 6    # 抽奖
Get-Random -SetSeed 42   # 可重现
```

#### 9.7 `Send-MailMessage`

- **作用**：SMTP 发送邮件
- **参数**：
  - `-To <string[]>` / `-Cc <string[]>` / `-Bcc <string[]>`（mandatory 之一）
  - `-From <string>`（mandatory）
  - `-Subject <string>`（mandatory）
  - `-Body <string>`（mandatory 之一）
  - `-BodyAsHtml`
  - `-SmtpServer <string>`（mandatory）
  - `-Port <int>`：默认 25
  - `-UseSsl`
  - `-Credential <PSCredential>`
  - `-Attachments <string[]>`
  - `-Priority <MailPriority>`：`Normal` / `Low` / `High`
  - `-Encoding <Encoding>`
- **行为**：使用 `System.Net.Mail.SmtpClient`（标记 obsolete 但仍可用）；OpenShell 不重新发明
- **安全**：受 ADR-0036 §11 `NetworkAccess` 权限约束

```powershell
Send-MailMessage -To "user@example.com" -From "noreply@example.com" -Subject "Test" -Body "Hello" -SmtpServer "smtp.example.com" -UseSsl -Credential $cred
```

#### 9.8 `Show-Command`

- **作用**：GUI 显示命令参数表单
- **参数**：
  - `-Name <string>`（mandatory）：命令名
  - `-PassThru`：返回用户填写的参数（不执行命令）
  - `-ErrorPopup`
- **行为**：GUI host 弹出 `CommandWindow`（per ADR-0043）；CLI host 降级为 `Get-Help -Parameter` 列表输出 + warning
- **实现状态**：M4 阶段已在 CLI host 实现降级形式（参数列表输出 + warning）。GUI host 的 `CommandWindow` 交互表单推迟到 M5+ 实现（per ADR-0043）。

```powershell
Show-Command Get-ChildItem
$params = Show-Command Get-ChildItem -PassThru
Get-ChildItem @params
```

## Implementation Priority

按 criticality 排序，分三批实现：

### Tier 1 — CRITICAL（阻塞基础脚本）

以下 cmdlet 在 M4 阶段必须实现，缺失会导致 90% 的 PS 脚本无法运行：

| 命令 | 优先级原因 |
|---|---|
| `ForEach-Object` | PS 最常用 cmdlet，几乎所有循环 |
| `Write-Output` | 显式 success 流写入 |
| `Write-Host` | 脚本输出 |
| `Write-Error` | 错误流（per ADR-0026） |
| `Write-Warning` | warning 流 |
| `Write-Verbose` | verbose 流 |
| `Out-Default` | 管道默认 sink |
| `Out-Null` | 丢弃输出 |
| `Test-Path` | 路径判断 |
| `Resolve-Path` | 路径解析 |
| `Split-Path` | 路径拆分 |
| `Join-Path` | 路径拼接 |
| `Push-Location` / `Pop-Location` | 位置栈 |

### Tier 2 — HIGH（脚本中常见）

以下 cmdlet 在 M4 末或 M5 初实现：

| 命令 | 优先级原因 |
|---|---|
| `Sort-Object`（脚本块形式） | 数据处理 |
| `Select-Object` | 数据投影 |
| `Group-Object` | 数据分组 |
| `Measure-Object` | 数据统计 |
| `Compare-Object` | 数据对比 |
| `Tee-Object` | 调试分流 |
| `Get-Member` | 对象探查 |
| `Add-Content` / `Clear-Content` | 文件操作 |
| `ConvertTo-Json` / `ConvertFrom-Json` | JSON 处理 |
| `Get-Process` / `Start-Process` / `Stop-Process` | 进程管理 |
| `Invoke-WebRequest` / `Invoke-RestMethod` | Web API |

### Tier 3 — MEDIUM（少用但完整）

以下 cmdlet 在 M5 实现：

| 命令 | 优先级原因 |
|---|---|
| `Write-Debug` | 调试流（少用） |
| `Write-Information` | information 流（PS 5+ 新增） |
| `Write-Progress` | 进度条（依赖 ADR-0044 GUI 进度） |
| `Out-Host` / `Out-String` / `Out-GridView` | 格式化输出 |
| `ConvertTo-Csv` / `ConvertFrom-Csv` / `Import-Csv` / `Export-Csv` | CSV |
| `ConvertTo-Html` / `ConvertTo-Xml` | 其他格式 |
| `Wait-Process` / `Debug-Process` | 进程辅助 |
| `Measure-Command` / `Trace-Command` | 调试 |
| `Start-Sleep` | 工具 |
| `Get-Random` / `Get-Date` / `Set-Date` / `New-TimeSpan` | 时间与随机 |
| `Send-MailMessage` / `Show-Command` | 罕用 |

## Verb-Noun Conformance

所有 cmdlet 严格遵循 ADR-0023 的 `Verb-Noun` 模式与受约束动词枚举。本 ADR 引入以下动词（已扩展入 ADR-0023 §1 的动词枚举）：

| 动词 | 类别 | 用途 |
|---|---|---|
| `ForEach` | Data | 管道遍历 |
| `Where` | Data | 管道过滤（已存在） |
| `Sort` | Data | 排序（已存在） |
| `Select` | Data | 投影（已存在） |
| `Group` | Data | 分组（已存在） |
| `Measure` | Data | 统计（已存在） |
| `Compare` | Data | 对比（已存在） |
| `Tee` | Data | 分流（新增） |
| `Out` | Output | 输出 sink（已存在） |
| `Write` | Output | 写入流（新增） |
| `Convert` | Data | 通用转换 |
| `ConvertTo` | Data | 转换为目标格式 |
| `ConvertFrom` | Data | 从源格式转换 |
| `Import` | Data | 导入文件 |
| `Export` | Data | 导出文件 |
| `Invoke` | Lifecycle | 调用（已存在） |
| `Start` | Lifecycle | 启动（已存在） |
| `Stop` | Lifecycle | 停止（已存在） |
| `Wait` | Lifecycle | 等待（已存在） |
| `Push` | Navigation | 入栈（已存在） |
| `Pop` | Navigation | 出栈（已存在） |
| `Resolve` | Navigation | 解析路径（新增） |
| `Split` | Navigation | 拆分路径（新增） |
| `Join` | Navigation | 拼接路径（新增） |
| `Test` | Lifecycle | 测试 / 验证（新增） |
| `Show` | Host | GUI 展示（已存在 ADR-0023 M3） |
| `Debug` | Lifecycle | 调试（新增） |
| `Trace` | Lifecycle | 追踪（新增） |
| `Send` | Communications | 发送（PS 标准，新增） |

Noun 列表（全部为 PascalCase）：

`Path` / `Location` / `Content` / `Process` / `Object` / `Member` / `Json` / `Csv` / `Html` / `Xml` / `WebRequest` / `RestMethod` / `Date` / `TimeSpan` / `Random` / `MailMessage` / `Command` / `Information` / `Progress` / `Error` / `Warning` / `Verbose` / `Debug` / `Host` / `Default` / `Null` / `String` / `GridView` / `File`

新增 Verb 必须经 ADR-0023 §1 评审流程，本 ADR 中的新增动词已通过评审。

## Pipeline-aware（ValueFromPipeline）

本 ADR 中以下 cmdlet 接受管道输入，通过 `-InputObject` 形参声明 `[Parameter(ValueFromPipeline = true)]`：

| Cmdlet | -InputObject 类型 | 备注 |
|---|---|---|
| `ForEach-Object` | 任意 | 流式 transform |
| `Where-Object` | 任意 | 流式 transform |
| `Sort-Object` | 任意 | buffering transform |
| `Select-Object` | 任意 | 流式（`-First` 可短路） |
| `Group-Object` | 任意 | buffering |
| `Measure-Object` | 任意 | buffering |
| `Compare-Object` | 任意（DifferenceObject） | buffering |
| `Tee-Object` | 任意 | sink + 透传 |
| `Get-Member` | 任意 | buffering |
| `ConvertTo-Json` | 任意 | buffering（必须全量才能序列化） |
| `ConvertTo-Csv` | 任意 | buffering |
| `ConvertTo-Html` | 任意 | buffering |
| `ConvertTo-Xml` | 任意 | buffering |
| `ConvertFrom-Json` | string | 流式 |
| `ConvertFrom-Csv` | string[] | 流式 |
| `Out-Default` / `Out-Null` / `Out-Host` / `Out-String` / `Out-GridView` / `Out-File` | 任意 | sink |
| `Write-Output` / `Write-Host` | 任意 | 流式 |
| `Write-Error` | 任意 | 流式（写入 error 流） |
| `Add-Content` / `Set-Content` | 任意 | sink |
| `Stop-Process` / `Wait-Process` / `Debug-Process` | `Process` | 从 `Get-Process` 接收 |
| `Get-Random` | `object[]` | 抽样 |

Pipeline 绑定规则：

1. `[Parameter(ValueFromPipeline = true)]`：每个管道项触发一次 cmdlet 调用（`process { }` 块）
2. `[Parameter(ValueFromPipelineByPropertyName = true)]`：按属性名绑定到形参（如 `Get-ChildItem` 的 `FullName` 自动绑定到 `Copy-Item -LiteralPath`）
3. 默认绑定 `InputObject`：未指定属性名时绑定到 `InputObject` 形参
4. 类型转换走 ADR-0047 §3 的转换表
5. 管道输入为空时，cmdlet 仍执行一次（per PowerShell 兼容；避免破坏 `Get-Process | Stop-Process` 空管道场景）

## Common Parameters

所有本 ADR 中的 cmdlet 支持 PowerShell 通用参数（per ADR-0049 ShouldProcess ADR）：

| 参数 | 类型 | 适用范围 | 行为 |
|---|---|---|---|
| `-Verbose` | switch | 全部 | 临时设置 `$VerbosePreference = "Continue"` |
| `-Debug` | switch | 全部 | 临时设置 `$DebugPreference = "Continue"` |
| `-ErrorAction <ActionPreference>` | enum | 全部 | 临时设置 `$ErrorActionPreference`：`SilentlyContinue` / `Stop` / `Continue` / `Inquire` / `Ignore` / `Suspend` |
| `-WarningAction <ActionPreference>` | enum | 全部 | 临时设置 `$WarningPreference` |
| `-ErrorVariable <string>` | string | 全部 | 错误记录追加到指定变量（前缀 `+` 表示追加而非覆盖） |
| `-WarningVariable <string>` | string | 全部 | warning 记录追加到变量 |
| `-OutVariable <string>` | string | 全部 | success 流副本追加到变量 |
| `-OutBuffer <int>` | int | 全部 | 缓冲 N 项后再传递给下游 |
| `-PipelineVariable <string>` | string | 全部 | 当前管道项存入指定变量（per ADR-0042） |
| `-InformationAction <ActionPreference>` | enum | 全部 | 临时设置 `$InformationPreference`（PS 5+） |
| `-InformationVariable <string>` | string | 全部 | information 记录追加到变量 |
| `-WhatIf` | switch | 破坏性 cmdlet | 模拟执行，输出"would do X"（per ADR-0049） |
| `-Confirm` | switch | 破坏性 cmdlet | 执行前确认（per ADR-0049） |

破坏性 cmdlet（声明 `SupportsShouldProcess`）：

- `Set-Content` / `Add-Content` / `Clear-Content`
- `Out-File`（覆盖时）
- `Export-Csv`（覆盖时）
- `Start-Process` / `Stop-Process`
- `Set-Date`
- `Send-MailMessage`

非破坏性 cmdlet 不暴露 `-WhatIf` / `-Confirm`（与 PowerShell 一致）。

## Alternatives Considered

1. **嵌入 `Microsoft.PowerShell.SDK`**：被否决。
   - 体积过大（~30MB），含 Windows-only 组件（部分 cmdlet）
   - 与 OpenShell 的 Provider / IItem 模型不直接兼容，需桥接层
   - 版本漂移：SDK 每年大版本升级，OpenShell 跟随成本高
   - PowerShell SDK 的初始化时间 ~1s，启动慢
   - 安全模型不互通（PS 的 ConstrainedLanguage vs OpenShell 的 ADR-0036 沙箱）

2. **仅移植 PowerShell "approved cmdlets" 集**：采纳（本 ADR）。
   - 选择 PowerShell 5.1 LTS 的稳定 API 集
   - 行为对齐 5.1（如 `ConvertTo-Json -Depth 2` 默认值）
   - PS 6+ / 7+ 新增特性作为可选扩展（如 `ConvertTo-Json -AsArray` / `Invoke-WebRequest -SkipHttpErrorCheck`），实现时附带

3. **写最小 stub 全部抛 `NotImplementedException`**：被否决。
   - 用户脚本运行时才发现失败，破坏信任
   - 与 PowerShell 全兼容目标冲突
   - 错误信息无指导意义
   - 沙箱场景下用户难区分"未实现"与"权限拒绝"

4. **跳过这些 cmdlet，文档记录"用 C# 扩展实现"**：被否决。
   - 阻断 PowerShell 全兼容目标
   - 用户被迫写 C# 扩展，迁移成本激增
   - 与 ADR-0023 §6 "命令名尽量对齐 PowerShell" 原则冲突
   - 与 ADR-0046 §Context "PowerShell 全兼容路线" 决策矛盾

5. **每条流（success/error/warning/...）独立 cmdlet 集，复用底层流抽象**：采纳（本 ADR）。
   - `Write-Error` 走 ADR-0026 `IErrorStream`
   - `Write-Verbose` / `Write-Warning` / `Write-Debug` 走新 `IPreferenceStream`（按 `$VerbosePreference` 等控制可见性）
   - `Write-Host` 直连 `IHostUI`（per ADR-0014）
   - `Write-Information` 走新 `IInformationStream`（PS 5+ 兼容）
   - 流间互不干扰，重定向（`2>` / `3>` / `4>` / `5>` / `6>`）按 PS 规则

6. **`ForEach-Object` 用 DSL 字符串而非脚本块**：被否决。
   - 与 PowerShell 用户的 `{ }` 肌肉记忆冲突
   - 失去 `begin/process/end` 三段能力
   - 失去闭包变量捕获（ADR-0046 §7）
   - ADR-0046 已为脚本块铺路，本 ADR 直接复用

7. **Web cmdlets 用 `curl` 外部进程而非 `HttpClient`**：被否决。
   - 平台依赖（curl 在 Windows 10 1803+ 才默认安装）
   - 进程生成开销（每次 HTTP 请求 fork 一个 curl）
   - 错误信息结构化困难
   - 安全沙箱（ADR-0036 §12）难约束外部进程的网络访问

8. **JSON / CSV / XML 序列化用 Newtonsoft.Json / CsvHelper / etc.**：被否决。
   - 第三方依赖增加打包体积
   - 与 ADR-0016 ALC 隔离冲突（第三方 DLL 加载）
   - .NET 内置 `System.Text.Json` 性能已足够（> Newtonsoft）
   - CSV 自实现简单（RFC 4180 + 边缘 case 处理）
   - XML 用 `XmlSerializer` + `XmlDocument`

## Consequences

### 优势

- **PowerShell 全兼容**：约 90% 的 `.ps1` 脚本可无修改运行（剩余 10% 涉及 Windows 特定 cmdlet 如 `Get-WmiObject` / `Register-PSSessionConfiguration`，未来 ADR 决策）
- **流式输出模型完整**：六流（success / error / warning / verbose / debug / information）全部支持，重定向 `2>` `3>` `4>` `5>` `6>` 全部可用
- **对象反射能力补齐**：`Get-Member` / `New-Object` / `Select-Object -ExpandProperty` 让用户可探查任意对象
- **数据格式互通**：JSON / CSV / HTML / XML 转换覆盖常见数据交换场景
- **进程 / Web 自动化**：`Get/Start/Stop-Process` + `Invoke-WebRequest/RestMethod` 覆盖运维脚本核心
- **与现有 ADR 协同**：脚本块（ADR-0046）/ 变量系统（ADR-0042 / 0047）/ 错误模型（ADR-0026）/ 管道（ADR-0010）形成完整语言层
- **CLI / GUI 共用**：所有 cmdlet 在两种 host 行为一致，GUI 特化的（`Out-GridView` / `Show-Command`）有 CLI 降级路径

### 代价

- **实现量**：约 40 个新 cmdlet，预估 5-10K 行 C# 代码
  - Pipeline cmdlets：~1500 行（含脚本块求值集成）
  - Output cmdlets：~800 行（含六流）
  - Path cmdlets：~600 行
  - Conversion cmdlets：~2000 行（JSON / CSV / HTML / XML 序列化最重）
  - Process cmdlets：~800 行
  - Web cmdlets：~1500 行（HttpClient 封装 + 多种认证）
  - Utility cmdlets：~800 行
- **测试负担**：每个 cmdlet 必须有完整 Pester 风格测试（约 200-400 个测试用例）
- **行为对齐成本**：PowerShell 5.1 / 6 / 7 之间存在细微差异（如 `ConvertTo-Json -Depth` 默认值 5.1=2，6+=2；`Write-Host` 在 5.1 走 Information 流，3-4 走 success 流），文档必须明确 OpenShell 选择的版本
- **安全审计成本**：`Start-Process` / `Invoke-WebRequest` / `Send-MailMessage` / `New-Object -ComObject` 必须审计，沙箱策略需更新
- **打包体积**：Web cmdlets 引入 `HttpClient` + JSON 序列化约增加 1-2MB
- **维护负担**：PS 7+ 的新特性（如 `ConvertTo-Json -AsArray`）需要选择性 backport

### 风险

- **行为偏差**：与 PowerShell 5.1 在以下点存在差异，需文档明确：
  - `Format-Table` 自动渲染：PS 在 host 终端宽度内自动选列，OpenShell 必须复现此算法
  - `$FormatEnumerationLimit`：默认 4，OpenShell 必须支持此变量
  - `Out-Default` 自动 `Format-Table` vs `Format-List`：基于对象类型与属性数启发式
  - `Invoke-WebRequest` 的 `-UseBasicParsing`：PS 5.1 在 Windows 上默认用 IE 引擎解析，OpenShell 永远 BasicParsing
  - `ConvertTo-Csv -NoTypeInformation` 默认值：PS 5.1 默认 false，OpenShell 默认 true（对齐 PS 6+）
- **JSON 序列化深度**：默认 `-Depth 2` 可能截断嵌套对象（PS 兼容行为），用户需注意
- **`Start-Process` 安全**：进程生成是高风险操作，沙箱（ADR-0036 §12）默认禁止；交互场景需用户显式允许
- **`Invoke-WebRequest` 凭据泄漏**：URL 中嵌入凭据（`https://user:pass@host`）需日志脱敏
- **`Send-MailMessage` 标记 obsolete**：PS 7+ 已标 obsolete，但 OpenShell 仍实现（用户惯用法）；未来 ADR 决定替代方案（MailKit 集成？）

### 约束

- **命令名严格遵循 Verb-Noun 模式**：所有 cmdlet 必须通过 ADR-0023 §1 受约束动词评审
- **参数名大小写不敏感**：`-FilePath` 与 `-filepath` 等价（与 PowerShell 一致）
- **参数默认值对齐 PowerShell 5.1 LTS**：避免行为偏差，文档记录 PS 6+ / 7+ 差异
- **管道输入绑定匹配 PS `ValueFromPipeline` 语义**：见 §Pipeline-aware
- **输出类型对齐 PS**：`Get-Process` 返回 `Process` 对象（不是自定义包装类），`Get-Member` 返回 `MemberDefinition` 对象，等等
- **通用参数在所有 cmdlet 上支持**：`-Verbose` / `-Debug` / `-ErrorAction` / `-WarningAction` / `-ErrorVariable` / `-WarningVariable` / `-OutVariable` / `-OutBuffer` / `-PipelineVariable` / `-InformationAction` / `-InformationVariable` 必须可用
- **破坏性 cmdlet 必须 `SupportsShouldProcess`**：`-WhatIf` / `-Confirm` 自动可用（per ADR-0049）
- **错误记录走 ADR-0026 `IErrorStream`**：禁止裸 `Console.Error.WriteLine`
- **脚本块参数走 ADR-0046 `ScriptBlock` 类型**：禁止字符串参数当作脚本块求值（必须显式 `{ }`）
- **路径操作走 ADR-0006 `ItemPath` 模型**：`Test-Path` / `Resolve-Path` 等命令的 `-Path` 形参类型为 `string[]`，内部解析为 `ItemPath`
- **`Get-Content` / `Set-Content` / `Add-Content` / `Clear-Content` 走 Provider `IContentProvider`**：禁止直接文件 IO（确保 Registry / S3 / Zip 等 Provider 通用）
- **流式优先**：除 buffering 节点（`Sort-Object` / `Group-Object` / `Measure-Object` / `Compare-Object` / `Get-Member` / `ConvertTo-*`）外，必须 `IAsyncEnumerable<IItem>` 流式
- **流式取消**：所有 cmdlet 透传 `CancellationToken`，Ctrl+C 立即停止（per ADR-0010 §5）
- **错误恢复**：单元素错误默认非终止（per ADR-0026 §13），`-ErrorAction Stop` 升级为终止
- **`-WhatIf` 输出格式**：`What if: Performing the operation "X" on target "Y".`（与 PS 一致）
- **`-Confirm` 行为**：`$ConfirmPreference` 控制是否自动确认（默认 High 自动确认 Medium / Low 风险）
- **流编号与 PS 一致**：success=1, error=2, warning=3, verbose=4, debug=5, information=6（重定向语法 `2>` `3>` 等遵循此编号）
- **`$_` / `$PSItem` 在 `process` 块内只读**（per ADR-0042 / ADR-0046）
- **`$input` 自动变量**：在 `process` 块内是当前项，在 `end` 块内是全部输入迭代器（PS 兼容）
- **`$PSBoundParameters`**：在 cmdlet 内是已绑定命名参数的 hashtable
- **`$args` 自动变量**：在脚本块内是未绑定位置参数数组
- **`Get-Random` 与 PS 5.1 行为对齐**：种子状态在会话内累积（不重置）；显式 `-SetSeed` 重置
- **`Invoke-WebRequest` / `Invoke-RestMethod` 不依赖 PowerShell SDK**：直接使用 `System.Net.Http.HttpClient`
- **`Send-MailMessage` 使用 `System.Net.Mail.SmtpClient`**：尽管 .NET 标记 obsolete，仍可用；不引入 MailKit 依赖
- **`Get-Date -UFormat` 严格对齐 PS 的 `strftime` 替换规则**：不与 .NET 格式化字符串混淆
- **所有 cmdlet 必须有 `about_<cmdlet>` 帮助条目**（per ADR-0025）
- **所有 cmdlet 必须更新 `docs/commands/registry.md`**（per ADR-0023 §4）
- **每 PR 实现一个 cmdlet 子类**：避免单 PR 过大，便于评审
- **`-Encoding` 默认 UTF-8 无 BOM**：跨平台一致性（Windows PowerShell 5.1 默认 UTF-16 LE，此差异记录在迁移文档）
- **JSON 序列化使用 `JsonNamingPolicy.CamelCase`**：与 ADR-0022 / ADR-0047 §12 一致
- **`Out-File` 与重定向运算符 `>` / `>>` 等价**：重定向运算符是 `Out-File` 的语法糖
- **`Tee-Object -Variable` 写入的变量存于 Global 作用域**：与 PS 一致（避免局部作用域销毁后丢失）
