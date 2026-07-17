using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Providers.Registry;

/// <summary>
/// Windows registry provider. Per ADR-0018.
/// 路径格式：<c>reg::HKLM/Software/Microsoft</c>（也支持完整名 <c>HKEY_LOCAL_MACHINE</c>）。
/// Key 视为 Directory，Value 通过 PropertyBag 暴露（不作为单独 Item）。
/// 仅 Windows 平台可用：构造不抛异常，方法访问时若非 Windows 才抛 PlatformNotSupportedException。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryProvider :
    IProvider,
    IItemProvider,
    IContainerProvider,
    INavigationProvider,
    IContentProvider,
    IPropertyProvider,
    IItemMutatorProvider,
    IPropertyWriterProvider
{
    /// <summary>Hive 缩写 → RegistryHive 映射表（静态、不可变，per ADR-0018 §约束）。</summary>
    private static readonly IReadOnlyDictionary<string, RegistryHive> HiveMap =
        new Dictionary<string, RegistryHive>(StringComparer.OrdinalIgnoreCase)
        {
            ["HKLM"] = RegistryHive.LocalMachine,
            ["HKEY_LOCAL_MACHINE"] = RegistryHive.LocalMachine,
            ["HKCU"] = RegistryHive.CurrentUser,
            ["HKEY_CURRENT_USER"] = RegistryHive.CurrentUser,
            ["HKCR"] = RegistryHive.ClassesRoot,
            ["HKEY_CLASSES_ROOT"] = RegistryHive.ClassesRoot,
            ["HKU"] = RegistryHive.Users,
            ["HKEY_USERS"] = RegistryHive.Users,
            ["HKCC"] = RegistryHive.CurrentConfig,
            ["HKEY_CURRENT_CONFIG"] = RegistryHive.CurrentConfig,
        };

    public ProviderInfo Info { get; } = new()
    {
        Name = "reg",
        Version = new Version(0, 1, 0),
        Description = "Windows registry provider (hive as drive, key as directory)",
        Author = "OpenShell",
    };

    public IReadOnlySet<ProviderCapability> Capabilities { get; } = new HashSet<ProviderCapability>
    {
        ProviderCapability.Item,
        ProviderCapability.Container,
        ProviderCapability.Navigation,
        ProviderCapability.Content,
        ProviderCapability.Property,
        ProviderCapability.PropertyWrite,
    };

    // ---- IItemProvider ----

    public ValueTask<IItem?> GetItemAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlatform();

        if (!TrySplitPath(path, out var hiveName, out var subkeyPath))
            return ValueTask.FromResult<IItem?>(null);

        using var baseKey = OpenBaseKey(hiveName);
        if (baseKey is null)
            return ValueTask.FromResult<IItem?>(null);

        using var subkey = string.IsNullOrEmpty(subkeyPath)
            ? baseKey
            : OpenSubKeySafe(baseKey, subkeyPath);
        if (subkey is null)
            return ValueTask.FromResult<IItem?>(null);

        var item = new Item
        {
            Path = path,
            Kind = ItemKind.Directory,
            Timestamps = new ItemTimestamps(null, TryGetLastWriteTime(subkey), null),
            Properties = PropertyBag.Empty
                .With("subKeyCount", subkey.SubKeyCount)
                .With("valueCount", subkey.ValueCount),
        };
        return ValueTask.FromResult<IItem?>(item);
    }

    public async IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsurePlatform();

        if (!TrySplitPath(path, out var hiveName, out var subkeyPath))
            yield break;

        using var baseKey = OpenBaseKey(hiveName);
        if (baseKey is null)
            yield break;

        using var subkey = string.IsNullOrEmpty(subkeyPath)
            ? baseKey
            : OpenSubKeySafe(baseKey, subkeyPath);
        if (subkey is null)
            yield break;

        // 仅枚举子键（Key 作为 Directory）；Value 不作为单独 Item（per ADR-0018 §6），需用 get-itemproperty 取值。
        string[] subKeyNames;
        try
        {
            subKeyNames = subkey.GetSubKeyNames();
        }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var name in subKeyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!options.IncludeHidden && name.StartsWith('.'))
                continue;

            // filter 仅作用于"叶子"项，注册表子键都视作目录 → 直接通过。
            var childPath = path.Combine(name);

            // 取子键时间戳需单独 OpenSubKey；失败则时间戳留空。
            DateTimeOffset? modified = null;
            try
            {
                using var child = subkey.OpenSubKey(name, writable: false);
                if (child is not null)
                    modified = TryGetLastWriteTime(child);
            }
            catch (UnauthorizedAccessException) { /* 跳过无权限子键的元信息 */ }

            yield return new Item
            {
                Path = childPath,
                Kind = ItemKind.Directory,
                Timestamps = new ItemTimestamps(null, modified, null),
                Properties = PropertyBag.Empty
                    .With("name", name),
            };
        }
    }

    // ---- INavigationProvider ----

    public bool IsValidPath(ItemPath path)
    {
        if (path.Provider != "reg")
            return false;
        return TrySplitPath(path, out _, out _);
    }

    public ItemPath NormalizePath(ItemPath path)
    {
        if (!TrySplitPath(path, out var hiveName, out var subkeyPath))
            return path;

        // 规范化为 HKLM/Software/Microsoft 形式（用缩写，正斜杠分隔）。
        var normalizedHive = NormalizeHiveName(hiveName);
        var normalizedSubkey = subkeyPath.Replace('\\', '/').Trim('/');
        var newInternal = string.IsNullOrEmpty(normalizedSubkey)
            ? normalizedHive
            : $"{normalizedHive}/{normalizedSubkey}";
        return path with { InternalPath = newInternal };
    }

    // ---- IContentProvider ----

    public ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlatform();

        if (!TrySplitPath(path, out var hiveName, out var subkeyPath))
            throw new FileNotFoundException($"Registry key not found: {path.Display}");

        using var baseKey = OpenBaseKey(hiveName);
        if (baseKey is null)
            throw new FileNotFoundException($"Registry key not found: {path.Display}");

        using var subkey = string.IsNullOrEmpty(subkeyPath)
            ? baseKey
            : OpenSubKeySafe(baseKey, subkeyPath);
        if (subkey is null)
            throw new FileNotFoundException($"Registry key not found: {path.Display}");

        // 注册表 Value 集合不天然映射为字节流；用 JSON 序列化 values 供 Get-Content 使用。
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in subkey.GetValueNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var kind = subkey.GetValueKind(name);
                var value = subkey.GetValue(name);
                var displayName = string.IsNullOrEmpty(name) ? "(default)" : name;
                values[displayName] = SerializeValue(kind, value);
            }
            catch (UnauthorizedAccessException) { /* 跳过无权限 value */ }
        }

        var json = JsonSerializer.Serialize(values, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    // ---- IPropertyProvider ----

    public ValueTask<PropertyBag> GetPropertiesAsync(IItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlatform();

        if (!TrySplitPath(item.Path, out var hiveName, out var subkeyPath))
            return ValueTask.FromResult(PropertyBag.Empty);

        using var baseKey = OpenBaseKey(hiveName);
        if (baseKey is null)
            return ValueTask.FromResult(PropertyBag.Empty);

        using var subkey = string.IsNullOrEmpty(subkeyPath)
            ? baseKey
            : OpenSubKeySafe(baseKey, subkeyPath);
        if (subkey is null)
            return ValueTask.FromResult(PropertyBag.Empty);

        var bag = PropertyBag.Empty
            .With("hive", NormalizeHiveName(hiveName))
            .With("subKeyCount", subkey.SubKeyCount)
            .With("valueCount", subkey.ValueCount);

        // 暴露 values 子项：每个 entry 是 (kind, value) 元组，便于用户过滤。
        var valueNames = subkey.GetValueNames();
        if (valueNames.Length == 0)
            return ValueTask.FromResult(bag);

        var valuesBag = new Dictionary<string, RegistryValue>(StringComparer.Ordinal);
        foreach (var name in valueNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var kind = subkey.GetValueKind(name);
                var value = subkey.GetValue(name);
                var displayName = string.IsNullOrEmpty(name) ? "(default)" : name;
                valuesBag[displayName] = new RegistryValue(
                    displayName,
                    MapKind(kind),
                    value);
            }
            catch (UnauthorizedAccessException)
            {
                // Per ADR-0018 §约束：捕获 UnauthorizedAccessException，返回可访问的子集。
            }
        }

        bag = bag.With("values", valuesBag);
        return ValueTask.FromResult(bag);
    }

    // ---- IItemMutatorProvider (per ADR-0018 §8: 写入支持) ----

    /// <summary>创建注册表子键 (等价 PowerShell <c>New-Item -Type directory</c>)。</summary>
    public ValueTask CreateDirectoryAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlatform();

        if (!TrySplitPath(path, out var hiveName, out var subkeyPath) || string.IsNullOrEmpty(subkeyPath))
            throw new InvalidOperationException($"Cannot create registry key at hive root: {path.Display}");

        using var baseKey = OpenBaseKey(hiveName);
        if (baseKey is null)
            throw new DirectoryNotFoundException($"Registry hive not found: {hiveName}");

        var winPath = subkeyPath.Replace('/', '\\');
        // CreateSubKey opens parent writable, creates key if missing, opens if exists (幂等).
        using var created = baseKey.CreateSubKey(winPath);
        return ValueTask.CompletedTask;
    }

    /// <summary>删除注册表子键。recurse=true 删除整棵子树, recurse=false 仅删除无子键的叶子。</summary>
    public ValueTask DeleteAsync(ItemPath path, bool recurse, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlatform();

        if (!TrySplitPath(path, out var hiveName, out var subkeyPath) || string.IsNullOrEmpty(subkeyPath))
            throw new InvalidOperationException($"Cannot delete registry hive root: {path.Display}");

        using var baseKey = OpenBaseKey(hiveName);
        if (baseKey is null)
            throw new FileNotFoundException($"Registry hive not found: {hiveName}");

        var winPath = subkeyPath.Replace('/', '\\');

        if (recurse)
        {
            baseKey.DeleteSubKeyTree(winPath, throwOnMissingSubKey: false);
        }
        else
        {
            // 非递归: 检查是否有子键, 有则拒绝.
            using var sub = baseKey.OpenSubKey(winPath, writable: true);
            if (sub is null)
                return ValueTask.CompletedTask; // 幂等: key 不存在视为已删除.
            if (sub.SubKeyCount > 0)
                throw new InvalidOperationException(
                    $"Cannot delete non-empty registry key without -Recurse: {path.Display}");
            baseKey.DeleteSubKey(winPath, throwOnMissingSubKey: false);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>重命名注册表子键 (Registry 不原生支持 rename, 通过 copy+delete 实现)。</summary>
    public ValueTask RenameAsync(ItemPath path, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlatform();

        if (!TrySplitPath(path, out var hiveName, out var subkeyPath) || string.IsNullOrEmpty(subkeyPath))
            throw new InvalidOperationException($"Cannot rename registry hive root: {path.Display}");

        using var baseKey = OpenBaseKey(hiveName);
        if (baseKey is null)
            throw new FileNotFoundException($"Registry hive not found: {hiveName}");

        var winPath = subkeyPath.Replace('/', '\\');
        var lastSep = winPath.LastIndexOf('\\');
        var parentPath = lastSep >= 0 ? winPath[..lastSep] : "";
        var oldName = lastSep >= 0 ? winPath[(lastSep + 1)..] : winPath;

        using var parent = string.IsNullOrEmpty(parentPath)
            ? baseKey
            : baseKey.OpenSubKey(parentPath, writable: true);
        if (parent is null)
            throw new FileNotFoundException($"Parent registry key not found for: {path.Display}");

        using var oldKey = parent.OpenSubKey(oldName, writable: false);
        if (oldKey is null)
            throw new FileNotFoundException($"Registry key not found: {path.Display}");

        // 创建新键并递归拷贝所有 value 和子键.
        using var newKey = parent.CreateSubKey(newName);
        CopyRegistryTree(oldKey, newKey);

        // 删除旧键 (整棵子树).
        parent.DeleteSubKeyTree(oldName, throwOnMissingSubKey: false);
        return ValueTask.CompletedTask;
    }

    /// <summary>注册表不支持设置时间戳 (需 P/Invoke RegSetInfoKey), 静默 no-op。</summary>
    public ValueTask SetTimestampsAsync(
        ItemPath path,
        DateTimeOffset? modified,
        DateTimeOffset? accessed,
        CancellationToken cancellationToken = default)
    {
        // Per interface contract: optional fast-path, silent no-op when unsupported.
        return ValueTask.CompletedTask;
    }

    // ---- IPropertyWriterProvider (per ADR-0018 §8: 注册表 value 写入) ----

    /// <summary>设置注册表 value (等价 <c>Set-ItemProperty</c> / <c>New-ItemProperty</c>)。</summary>
    public ValueTask SetPropertyAsync(
        ItemPath path, string name, object? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlatform();

        using var subkey = OpenKeyForWrite(path);
        if (subkey is null)
            throw new UnauthorizedAccessException($"Cannot write to registry key: {path.Display}");

        var (regValue, regKind) = ConvertToRegistryValue(value);
        // 空字符串 name 表示 (default) value, 与 Registry API 语义一致.
        // ConvertToRegistryValue 已将 null 归一化为 "" (Registry 不支持 null value), 此处 ! 仅消除编译器告警.
        subkey.SetValue(name, regValue!, regKind);
        return ValueTask.CompletedTask;
    }

    /// <summary>删除注册表 value (等价 <c>Remove-ItemProperty</c>)。幂等: value 不存在不报错。</summary>
    public ValueTask RemovePropertyAsync(
        ItemPath path, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlatform();

        using var subkey = OpenKeyForWrite(path);
        if (subkey is null)
            throw new UnauthorizedAccessException($"Cannot write to registry key: {path.Display}");

        subkey.DeleteValue(name, throwOnMissingValue: false);
        return ValueTask.CompletedTask;
    }

    /// <summary>清除注册表 value (置为空字符串, 不删除 name)。等价 <c>Clear-ItemProperty</c>。</summary>
    public ValueTask ClearPropertyAsync(
        ItemPath path, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlatform();

        using var subkey = OpenKeyForWrite(path);
        if (subkey is null)
            throw new UnauthorizedAccessException($"Cannot write to registry key: {path.Display}");

        // Registry 没有 "null value" 概念; 清除 = 置为空字符串 (String kind).
        subkey.SetValue(name, "", Microsoft.Win32.RegistryValueKind.String);
        return ValueTask.CompletedTask;
    }

    // ---- Helpers ----

    /// <summary>非 Windows 平台调用任何 Registry API 时抛 PlatformNotSupportedException。</summary>
    private static void EnsurePlatform()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Registry provider is only supported on Windows.");
    }

    private static bool TrySplitPath(ItemPath path, out string hiveName, out string subkeyPath)
    {
        hiveName = "";
        subkeyPath = "";
        var internalPath = path.InternalPath.TrimStart('/');
        if (string.IsNullOrEmpty(internalPath))
            return false;

        var sepIdx = internalPath.IndexOf('/');
        if (sepIdx < 0)
        {
            hiveName = internalPath;
            subkeyPath = "";
        }
        else
        {
            hiveName = internalPath[..sepIdx];
            subkeyPath = internalPath[(sepIdx + 1)..];
        }

        return HiveMap.ContainsKey(hiveName);
    }

    private static RegistryKey? OpenBaseKey(string hiveName)
    {
        if (!HiveMap.TryGetValue(hiveName, out var hive))
            return null;
        return RegistryKey.OpenBaseKey(hive, RegistryView.Default);
    }

    /// <summary>OpenSubKey 包裹 UnauthorizedAccessException → 返回 null（per ADR-0018 §约束）。</summary>
    private static RegistryKey? OpenSubKeySafe(RegistryKey parent, string subkeyPath)
    {
        try
        {
            var winPath = subkeyPath.Replace('/', '\\');
            return parent.OpenSubKey(winPath, writable: false);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    /// <summary>打开指定路径的注册表键用于写入 (writable: true)。per ADR-0018 §8.</summary>
    /// <returns>可写的 RegistryKey (调用方负责 Dispose), 或 null 表示键不存在 / 无权限。</returns>
    private static RegistryKey? OpenKeyForWrite(ItemPath path)
    {
        if (!TrySplitPath(path, out var hiveName, out var subkeyPath))
            return null;

        if (!HiveMap.TryGetValue(hiveName, out var hive))
            return null;

        var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);

        if (string.IsNullOrEmpty(subkeyPath))
            return baseKey; // Hive root — caller disposes

        try
        {
            var winPath = subkeyPath.Replace('/', '\\');
            return baseKey.OpenSubKey(winPath, writable: true);
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (SecurityException) { return null; }
        finally { baseKey.Dispose(); }
    }

    /// <summary>递归拷贝注册表键的所有 value 和子键 (用于 RenameAsync)。</summary>
    private static void CopyRegistryTree(RegistryKey source, RegistryKey dest)
    {
        // 拷贝所有 value.
        foreach (var valueName in source.GetValueNames())
        {
            var kind = source.GetValueKind(valueName);
            var value = source.GetValue(valueName);
            // Registry API 对已知 kind 不会返回 null; ?? "" 仅为满足可空引用类型分析.
            dest.SetValue(valueName, value ?? string.Empty, kind);
        }
        // 递归拷贝所有子键.
        foreach (var subKeyName in source.GetSubKeyNames())
        {
            using var src = source.OpenSubKey(subKeyName, writable: false);
            if (src is null) continue;
            using var dst = dest.CreateSubKey(subKeyName);
            CopyRegistryTree(src, dst);
        }
    }

    /// <summary>把用户输入值转换为 Registry API 所需的 (value, RegistryValueKind) 元组。</summary>
    private static (object? Value, Microsoft.Win32.RegistryValueKind Kind) ConvertToRegistryValue(object? value)
    {
        return value switch
        {
            null => ("", Microsoft.Win32.RegistryValueKind.String),
            int i => (i, Microsoft.Win32.RegistryValueKind.DWord),
            uint u => ((long)u, Microsoft.Win32.RegistryValueKind.QWord),
            long l => (l, Microsoft.Win32.RegistryValueKind.QWord),
            byte[] b => (b, Microsoft.Win32.RegistryValueKind.Binary),
            string[] arr => (arr, Microsoft.Win32.RegistryValueKind.MultiString),
            string s => (s, Microsoft.Win32.RegistryValueKind.String),
            bool b => (b ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord),
            short s => ((int)s, Microsoft.Win32.RegistryValueKind.DWord),
            ushort us => ((int)us, Microsoft.Win32.RegistryValueKind.DWord),
            sbyte sb => ((int)sb, Microsoft.Win32.RegistryValueKind.DWord),
            byte b2 => ((int)b2, Microsoft.Win32.RegistryValueKind.DWord),
            _ => (value.ToString(), Microsoft.Win32.RegistryValueKind.String),
        };
    }

    private static DateTimeOffset? TryGetLastWriteTime(RegistryKey key)
    {
        try
        {
            // RegistryKey 没有直接暴露 LastWriteTime；通过 win32 API 取，或返回 null。
            // M4 简化：返回 null，避免 P/Invoke 复杂度。
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeHiveName(string hiveName)
    {
        if (!HiveMap.TryGetValue(hiveName, out var hive))
            return hiveName;
        return hive switch
        {
            RegistryHive.LocalMachine => "HKLM",
            RegistryHive.CurrentUser => "HKCU",
            RegistryHive.ClassesRoot => "HKCR",
            RegistryHive.Users => "HKU",
            RegistryHive.CurrentConfig => "HKCC",
            _ => hiveName,
        };
    }

    private static RegistryValueKind MapKind(Microsoft.Win32.RegistryValueKind kind) => kind switch
    {
        Microsoft.Win32.RegistryValueKind.String => RegistryValueKind.String,
        Microsoft.Win32.RegistryValueKind.ExpandString => RegistryValueKind.ExpandString,
        Microsoft.Win32.RegistryValueKind.Binary => RegistryValueKind.Binary,
        Microsoft.Win32.RegistryValueKind.DWord => RegistryValueKind.DWord,
        Microsoft.Win32.RegistryValueKind.MultiString => RegistryValueKind.MultiString,
        Microsoft.Win32.RegistryValueKind.QWord => RegistryValueKind.QWord,
        _ => RegistryValueKind.Unknown,
    };

    private static object? SerializeValue(Microsoft.Win32.RegistryValueKind kind, object? value)
    {
        return kind switch
        {
            Microsoft.Win32.RegistryValueKind.MultiString when value is string[] arr => arr,
            Microsoft.Win32.RegistryValueKind.Binary when value is byte[] bytes => Convert.ToHexString(bytes),
            Microsoft.Win32.RegistryValueKind.DWord => value is int i ? (long)i : value,
            Microsoft.Win32.RegistryValueKind.QWord => value is long l ? l : value,
            _ => value,
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
}

/// <summary>Registry value 类型枚举（per ADR-0018 §5）。与 BCL Microsoft.Win32.RegistryValueKind 一一对应。</summary>
public enum RegistryValueKind
{
    Unknown = 0,
    String,         // REG_SZ
    ExpandString,   // REG_EXPAND_SZ
    Binary,         // REG_BINARY
    DWord,          // REG_DWORD
    MultiString,    // REG_MULTI_SZ
    QWord,          // REG_QWORD
}

/// <summary>Registry value：name + kind + raw value（per ADR-0018 §5）。</summary>
public sealed record RegistryValue(string Name, RegistryValueKind Kind, object? Value);
