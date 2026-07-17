using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenShell.Preview;

/// <summary>
/// 预览缓存 (LRU 1000 张, SHA256 key)。Per ADR-0030 §3.
/// 缓存 key = SHA256(itempath.Display + Modified timestamp) (per ADR-0030 §3)。
/// 缓存 value = 序列化的 <see cref="PreviewViewModel"/> (PNG 字节 / 文本 / 等), 持久化到
/// <c>~/.openshell/cache/previews/</c> (per ADR-0030 §3, <see cref="OpenShellPaths.PreviewsCacheDir"/>)
/// 下的单文件 (key 命名, .bin 扩展)。
/// 约束 (per ADR-0030 §3 / §约束): LRU 上限 1000 张; 超出按 LRU 淘汰。
/// </summary>
/// <remarks>
/// 实现:
/// <list type="bullet">
///   <item>内存 LRU: <see cref="LinkedList{T}"/> + <see cref="Dictionary{TKey,TValue}"/> O(1) 访问。</item>
///   <item>持久化: 单文件 &lt;key&gt;.bin (cache file 内容为任意字节, 由调用方序列化)。</item>
///   <item>线程安全: <see cref="ReaderWriterLockSlim"/> 保护 LRU 操作。</item>
///   <item>命中: 同时检查内存 + 磁盘 (磁盘命中时回填到内存 LRU)。</item>
/// </list>
/// </remarks>
public sealed class LruPreviewCache : IDisposable
{
    /// <summary>LRU 容量上限。Per ADR-0030 §3 / §约束: 1000 张。</summary>
    public const int Capacity = 1000;

    private readonly string _cacheDir;
    private readonly ILogger<LruPreviewCache>? _logger;
    private readonly LinkedList<CacheEntry> _lruList = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _byKey = new(StringComparer.Ordinal);
    private readonly ReaderWriterLockSlim _lock = new();
    private int _disposed;

    public LruPreviewCache(string cacheDir, ILogger<LruPreviewCache>? logger = null)
    {
        _cacheDir = cacheDir ?? throw new ArgumentNullException(nameof(cacheDir));
        _logger = logger;
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>计算缓存 key: SHA256(itempath.Display + Modified timestamp) 的 hex 字符串。Per ADR-0030 §3.</summary>
    public static string ComputeKey(string itemPathDisplay, DateTimeOffset modified)
    {
        // 使用 InvariantCulture 以保证跨平台 / 跨 locale 一致性。
        var input = $"{itemPathDisplay}|{modified.UtcTicks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>尝试从缓存读取 (内存 → 磁盘)。命中磁盘时回填到内存 LRU。返回 null 表示未命中。</summary>
    public byte[]? TryGet(string key)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(key)) return null;

        // 1. 内存命中 → 移到 LRU 头部。
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_byKey.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                return node.Value.Data;
            }

            // 2. 磁盘命中 → 读字节, 回填内存 LRU。
            var path = Path.Combine(_cacheDir, key + ".bin");
            if (!File.Exists(path)) return null;

            try
            {
                var data = File.ReadAllBytes(path);
                _lock.EnterWriteLock();
                try
                {
                    AddOrUpdateLocked(key, data);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
                return data;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "缓存磁盘文件读取失败: {Key}", key);
                return null;
            }
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    /// <summary>写入缓存 (内存 + 磁盘)。超出 Capacity 时按 LRU 淘汰最旧条目并删除磁盘文件。</summary>
    public void Set(string key, byte[] data)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrEmpty(key)) return;

        _lock.EnterWriteLock();
        try
        {
            AddOrUpdateLocked(key, data);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // 磁盘写入 (异步, 不阻塞 LRU 更新; 单 key 单文件)。
        try
        {
            var path = Path.Combine(_cacheDir, key + ".bin");
            File.WriteAllBytes(path, data);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "缓存磁盘写入失败: {Key}", key);
        }
    }

    /// <summary>内部 LRU 添加 / 更新 (调用方需持 WriteLock)。</summary>
    private void AddOrUpdateLocked(string key, byte[] data)
    {
        if (_byKey.TryGetValue(key, out var existing))
        {
            _lruList.Remove(existing);
            existing.Value = existing.Value with { Data = data };
            _lruList.AddFirst(existing);
            return;
        }

        var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, data));
        _lruList.AddFirst(node);
        _byKey[key] = node;

        // LRU 淘汰。
        while (_lruList.Count > Capacity)
        {
            var last = _lruList.Last!;
            _lruList.RemoveLast();
            _byKey.Remove(last.Value.Key);

            // 同时删除磁盘文件 (best-effort)。
            var path = Path.Combine(_cacheDir, last.Value.Key + ".bin");
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { _logger?.LogWarning(ex, "LRU 淘汰删除磁盘文件失败: {Key}", last.Value.Key); }
        }
    }

    /// <summary>清除所有缓存 (内存 + 磁盘)。Per ADR-0030 §3: 测试 / 手动清理用。</summary>
    public void Clear()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            _lruList.Clear();
            _byKey.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // 磁盘清理 (best-effort)。
        try
        {
            foreach (var f in Directory.GetFiles(_cacheDir, "*.bin"))
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "缓存目录清理失败: {Dir}", _cacheDir);
        }
    }

    /// <summary>当前内存中缓存条目数 (用于诊断 / 测试)。</summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _lruList.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(LruPreviewCache));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lock.Dispose();
        _lruList.Clear();
        _byKey.Clear();
    }

    /// <summary>LRU 链表条目 (key + 字节数据)。</summary>
    private sealed record CacheEntry(string Key, byte[] Data);
}
