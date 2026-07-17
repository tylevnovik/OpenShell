# ADR-0018: Registry Provider 抽象

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M4
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0001 (能力), ADR-0006 (路径), ADR-0007 (操作引擎)

## Context

Windows 注册表是层次结构数据源：

```
HKEY_LOCAL_MACHINE
└── Software
    └── Microsoft
        └── Windows
            └── CurrentVersion
                └── Run
                    ├── MyApp = "C:\\app.exe"   (字符串值)
                    ├── Count = 42                (DWORD 值)
                    └── Path = "%PATH%"          (可扩展字符串值)
```

特性：

1. **Hive 结构**：`HKLM`、`HKCU`、`HKCR`、`HKU`、`HKCC` 五个根 Hive
2. **Key + Value**：节点是"键"（Key，类似目录），值（Value）附在键上
3. **多种 Value 类型**：`REG_SZ`、`REG_DWORD`、`REG_QWORD`、`REG_EXPAND_SZ`、`REG_MULTI_SZ`、`REG_BINARY`
4. **ACL**：注册表项有 ACL，与文件 ACL 不同
5. **跨平台**：仅 Windows 有注册表，Linux/Mac 上 Registry Provider 不注册
6. **写入敏感**：注册表写入风险高（影响系统），需谨慎

需求：

- CLI/GUI 用同一套路径语法浏览注册表
- `cd reg::HKLM/Software/Microsoft` 进入子树
- `get-childitem` 列出子键与值
- `get-itemproperty` 取值
- `set-itemproperty` 改值（M5+ 写入）
- `new-item` 创建子键
- `remove-item` 删除键

参考：
- PowerShell 的 `Registry` PSProvider 已成熟，路径用 `HKLM:\Software\...`
- .NET `Microsoft.Win32.Registry` 类直接操作

## Decision

### 1. RegistryProvider 实现的能力

| 能力 | 实现 | 说明 |
|---|---|---|
| Item | ✅ | 键（Key）作为 IItem |
| Container | ✅ | 子键列表 |
| Navigation | ✅ | 路径校验、Hive 映射 |
| Content | ❌ | 注册表无"二进制内容"，用 Property 表示值 |
| Property | ✅ | Value 集合作为 PropertyBag |
| Security | ✅ | `RegistrySecurity` 转为 Acl |
| Drive | ✅ | Hive 作为 Drive |

不实现 `IContentProvider`，因为注册表的"内容"是离散的 Value 集合，与 FS 的字节流语义不同。用 `IPropertyProvider` 暴露 Value。

### 2. Item 模型

`IItem.Kind = ItemKind.Directory`（Key 视为目录）或扩展 `ItemKind` 加 `RegistryKey`（M4 暂用 Directory 兼容 GUI）。

```csharp
var keyItem = new Item
{
    Path = ItemPath.Parse("reg::HKLM/Software/Microsoft"),
    Kind = ItemKind.Directory,
    Timestamps = new ItemTimestamps(null, lastWriteTime, null),
    Properties = PropertyBag.Empty
        .With("subKeyCount", subKeyCount)
        .With("valueCount", valueCount),
};
```

### 3. 路径模型

| ItemPath | 含义 |
|---|---|
| `reg::HKLM/Software/Microsoft` | HKLM Hive 下的 Software\Microsoft |
| `reg::HKCU/Environment/PATH` | HKCU\Environment 下的 PATH 值（特殊：访问具体 value） |

Hive 缩写映射：

| 路径前缀 | .NET `RegistryHive` 枚举 |
|---|---|
| `HKLM` 或 `HKEY_LOCAL_MACHINE` | `LocalMachine` |
| `HKCU` 或 `HKEY_CURRENT_USER` | `CurrentUser` |
| `HKCR` 或 `HKEY_CLASSES_ROOT` | `ClassesRoot` |
| `HKU` 或 `HKEY_USERS` | `Users` |
| `HKCC` 或 `HKEY_CURRENT_CONFIG` | `CurrentConfig` |

### 4. PropertyBag 暴露 Value

`IPropertyProvider.GetPropertiesAsync(item)` 返回：

```csharp
PropertyBag.Empty
    .With("subKeyCount", key.SubKeyCount)
    .With("valueCount", key.ValueCount)
    .With("values", new RegistryValueBag {
        ["ProductName"] = ("REG_SZ", "Windows 10"),
        ["Count"] = ("REG_DWORD", 42u),
        ["Path"] = ("REG_EXPAND_SZ", "%PATH%"),
        ["Names"] = ("REG_MULTI_SZ", new[] {"a", "b"}),
        ["Data"] = ("REG_BINARY", new byte[] { 0x01, 0x02 }),
    });
```

### 5. 注册表值类型

新增 `RegistryValueKind` 枚举与 `RegistryValue` record：

```csharp
public enum RegistryValueKind
{
    String,         // REG_SZ
    ExpandString,   // REG_EXPAND_SZ
    Binary,         // REG_BINARY
    DWord,          // REG_DWORD
    MultiString,    // REG_MULTI_SZ
    QWord,          // REG_QWORD
    Unknown,
}

public sealed record RegistryValue(string Name, RegistryValueKind Kind, object? Value);
```

格式化时（ADR-0011）按 Kind 渲染：

- `String` / `ExpandString` → 原文输出
- `DWord` / `QWord` → 数字（可显示 hex/dec 切换）
- `Binary` → hex dump
- `MultiString` → 多行字符串

### 6. Get-ChildItem 行为

`get-childitem reg::HKLM/Software` 默认列出：

- 子键（kind = Directory，name = 子键名）
- 不直接列出 Value（Value 不是单独 Item）

要列出 Value 用 `get-itemproperty`：

```
get-itemproperty reg::HKLM/Software/Microsoft/Windows/CurrentVersion/Run
```

返回一个虚拟 Item，其 `Properties` 含所有 Value。

### 7. Drive 模型

5 个 Hive 作为 5 个 `ProviderDrive`：

```csharp
new ProviderDrive
{
    Name = "HKLM",
    Root = ItemPath.Root("reg").With(InternalPath: "/HKLM"),
    DisplayLabel = "HKEY_LOCAL_MACHINE",
}
```

`Get-PSDrive` 风格列出 5 个 Hive。

### 8. 写入支持（M5+）

新增接口（不在 M4 实现）：

```csharp
public interface IPropertyWriterProvider
{
    ValueTask SetPropertyAsync(IItem item, string propertyName, object? value, CancellationToken ct);
    ValueTask RemovePropertyAsync(IItem item, string propertyName, CancellationToken ct);
}
```

`Set-ItemProperty` 命令调用它。M4 阶段抛 `NotSupportedException`。

`New-Item` 创建子键（M5+ 实现，需 `IItemCreator` 接口）。

### 9. 平台检查

`RegistryProvider` 仅在 Windows 注册：

```csharp
if (!OperatingSystem.IsWindows()) return;   // 启动时跳过
_providers.Register(new RegistryProvider());
```

Linux/Mac 上 `reg::` 路径直接报 "Provider not registered"。

### 10. 权限处理

- 访问 HKLM 部分子树需管理员权限
- `UnauthorizedAccessException`：列出某子键失败时跳过，类似 FS Provider
- 写入（M5+）需提示用户提权

## Alternatives Considered

1. **把 Value 作为单独 Item**：被否决，与 FS "目录-文件"模型不对应，CLI/GUI 难统一渲染
2. **不实现 Registry，仅提供 FS / Archive / Remote**：被否决，PowerShell 经典用例，且为 ADR-0006 的路径模型提供了"Hive 即 Drive"的验证
3. **实现 `IContentProvider` 把 Value 序列化为 JSON**：被否决，"内容流"与"value 集合"语义不对应
4. **路径用 `HKLM:\` 风格（PSDrive）**：被否决，与 ADR-0006 的 `provider::` 模型不一致
5. **不区分 Key 与 Value，全部当 Item**：被否决，Value 在 PropertyBag 中更自然，符合"目录有属性"的心智

## Consequences

### 优势
- 与 FS 一致的浏览体验（`cd`、`get-childitem`）
- PropertyBag 暴露 Value 灵活
- 5 Hive 作为 Drive，GUI 侧边栏自然展示
- 跨平台优雅降级（非 Windows 不注册）

### 代价
- Value 不是 Item，`select name,size` 这类对 Value 不直接生效
- 写入需特殊接口（M5+）
- 注册表 ACL 类型与 FS 不同，需转换

### 约束
- RegistryProvider 必须在非 Windows 平台启动时跳过注册（不抛异常）
- `HKLM` / `HKEY_LOCAL_MACHINE` 等多种写法必须都支持
- Value 类型必须按 `RegistryValueKind` 暴露，禁止丢失类型信息
- `GetPropertiesAsync` 必须捕获 `UnauthorizedAccessException`，返回已可访问的子集
- 读取 HKLM 子树时 Provider 不主动提权，写入才提示
- Hive 映射表必须是静态的，禁止运行时修改
- PropertyBag 中 `values` 子项的 Key 必须是 Value Name（不能是 GUID 等不友好名）
- 路径深度无限制，但 .NET RegistryKey 句柄必须及时 dispose
