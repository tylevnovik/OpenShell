using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Providers;

namespace OpenShell.Packaging.Registry;

/// <summary>
/// HTTP 客户端, 调用注册源 v1 REST API。Per ADR-0039 §4.
/// 支持: <c>packages</c> / <c>packages/{name}</c> / <c>packages/{name}/{version}</c> /
/// <c>packages/{name}/latest</c> / <c>search</c> / 下载 <c>.osp</c>。
/// 支持 ETag / Last-Modified 缓存条件请求 (减少带宽)。支持 <c>env:TOKEN</c> 形式的 auth header。
/// 注: 方法标记为 virtual 以便 NSubstitute 在单测中 mock (避免真实 HTTP 调用)。
/// </summary>
/// <remarks>
/// ADR-0039 §4 / §6: 双层缓存 —
/// 内存层 (进程内 Dictionary) 仅存 ETag/LastModified 供条件请求头使用;
/// 磁盘层 (<c>~/.openshell/cache/indices/{sha256(url)}.json</c>) 持久化 etag/lastModified/body,
/// 用于 304 Not Modified 时直接返回缓存体, 跨进程重启仍有效。磁盘缓存为 best-effort, IO 失败不阻断主流程。
/// </remarks>
public class RegistryClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, (string? ETag, string? LastModified)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _diskCacheSeeded = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private readonly string _diskCacheDir;

    /// <summary>构造时注入一个 HttpClient (推荐由 IHttpClientFactory 创建)。调用方负责其生命周期。</summary>
    public RegistryClient(HttpClient http) : this(http, ownsHttp: false) { }

    /// <summary>构造一个自持有 HttpClient (用单例 HttpClient 单例注入 DI 时建议用此重载)。</summary>
    public RegistryClient() : this(new HttpClient(), ownsHttp: true) { }

    private RegistryClient(HttpClient http, bool ownsHttp)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = ownsHttp;
        // ADR-0039 §6: 磁盘缓存目录 = OpenShellPaths.RegistryIndicesDir (~/.openshell/cache/indices/)。
        _diskCacheDir = OpenShellPaths.RegistryIndicesDir;
    }

    /// <summary>
    /// 解析 <c>env:TOKEN</c> 形式的 auth 引用, 返回实际 token 值。Per ADR-0039 §3.
    /// <c>"env:FOO"</c> → 读取 <c>FOO</c> 环境变量; 非 <c>env:</c> 前缀的字面值原样返回。
    /// </summary>
    public static string? ResolveAuth(string? auth)
    {
        if (string.IsNullOrEmpty(auth)) return null;
        if (auth.StartsWith("env:", StringComparison.Ordinal))
        {
            var var = auth["env:".Length..];
            return Environment.GetEnvironmentVariable(var);
        }
        return auth;
    }

    /// <summary>
    /// 列出某注册源所有包 (分页)。Per ADR-0039 §4 endpoint <c>GET /v1/packages</c>。
    /// </summary>
    public virtual async Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(ProviderSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var url = source.NormalizedUrl + "v1/packages";
        var json = await GetJsonWithCacheAsync(url, source, ct).ConfigureAwait(false);
        // 响应格式: { "packages": [ {name, versions, latest}, ... ] } 或 [ {name, versions, latest}, ... ]
        using var doc = JsonDocument.Parse(json);
        var list = new List<PackageInfo>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
                list.Add(ParsePackageInfo(el));
        }
        else if (doc.RootElement.TryGetProperty("packages", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
                list.Add(ParsePackageInfo(el));
        }
        return list;
    }

    /// <summary>
    /// 强制刷新指定注册源的包索引, 持久化到 <see cref="OpenShellPaths.RegistryIndicesDir"/>。Per ADR-0039 §11.
    /// 与 <see cref="ListPackagesAsync"/> 区别: 跳过条件请求缓存, 始终发起无条件 GET, 拿到最新索引并写回磁盘缓存。
    /// 用于 <c>Get-ProviderSource -RefreshIndices</c> 命令。
    /// </summary>
    public virtual async Task<IReadOnlyList<PackageInfo>> RefreshIndexAsync(ProviderSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var url = source.NormalizedUrl + "v1/packages";

        // 无条件 GET (不携带 If-None-Match / If-Modified-Since), 强制获取最新索引。
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req, source);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 把新索引写回磁盘缓存 (含 ETag/Last-Modified, 供后续 ListPackagesAsync 触发 304 命中)。
        string? newEtag = resp.Headers.ETag?.Tag?.Trim('"');
        string? newLastMod = resp.Content.Headers.LastModified?.ToString("R");
        SetCache(url, newEtag, newLastMod);
        TryWriteDiskCache(url, newEtag, newLastMod, body);

        // 解析 (与 ListPackagesAsync 同一格式)。
        using var doc = JsonDocument.Parse(body);
        var list = new List<PackageInfo>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
                list.Add(ParsePackageInfo(el));
        }
        else if (doc.RootElement.TryGetProperty("packages", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
                list.Add(ParsePackageInfo(el));
        }
        return list;
    }

    /// <summary>
    /// 查询指定包名元信息 (含所有版本)。Per ADR-0039 §4 endpoint <c>GET /v1/packages/{name}</c>。
    /// </summary>
    public virtual async Task<PackageInfo?> GetPackageAsync(ProviderSource source, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(name)) return null;
        var url = source.NormalizedUrl + "v1/packages/" + WebUtility.UrlEncode(name);
        try
        {
            var json = await GetJsonWithCacheAsync(url, source, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return ParsePackageInfo(doc.RootElement);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>
    /// 查询某包最新稳定版。Per ADR-0039 §4 endpoint <c>GET /v1/packages/{name}/latest</c>。
    /// </summary>
    public virtual async Task<PackageVersionInfo?> GetLatestAsync(ProviderSource source, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var url = source.NormalizedUrl + "v1/packages/" + WebUtility.UrlEncode(name) + "/latest";
        try
        {
            var json = await GetJsonWithCacheAsync(url, source, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PackageVersionInfo>(json, JsonOptions);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>
    /// 查询某包指定版本的完整 manifest (含依赖列表)。Per ADR-0039 §4 endpoint <c>GET /v1/packages/{name}/{version}</c>。
    /// 用于 dry-run 安装时的依赖预解析 (无需下载完整 .osp 即可获取依赖图)。
    /// </summary>
    /// <returns>返回反序列化后的 <see cref="ProviderManifest"/>; 版本不存在返回 null。</returns>
    public virtual async Task<ProviderManifest?> GetVersionManifestAsync(
        ProviderSource source, string name, string version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version)) return null;
        var url = source.NormalizedUrl + "v1/packages/" + WebUtility.UrlEncode(name) + "/" + WebUtility.UrlEncode(version);
        try
        {
            var json = await GetJsonWithCacheAsync(url, source, ct).ConfigureAwait(false);
            // 注册源返回的版本元信息中, dependencies 字段与 ProviderManifest.Dependencies 结构一致;
            // 其余字段 (name/version/requiredApiVersion 等) 也与 manifest 对齐, 可直接反序列化。
            return JsonSerializer.Deserialize<ProviderManifest>(json, ManifestJsonOptions.Default);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>
    /// 关键词搜索包。Per ADR-0039 §4 endpoint <c>GET /v1/search?q=...</c>。
    /// </summary>
    public virtual async Task<IReadOnlyList<PackageInfo>> SearchAsync(ProviderSource source, string query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(query)) return Array.Empty<PackageInfo>();
        var url = source.NormalizedUrl + "v1/search?q=" + WebUtility.UrlEncode(query);
        var json = await GetJsonWithCacheAsync(url, source, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<PackageInfo>();
        // 响应: { "results": [...] } 或 [...]
        if (doc.RootElement.TryGetProperty("results", out var arr))
        {
            foreach (var el in arr.EnumerateArray()) list.Add(ParsePackageInfo(el));
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray()) list.Add(ParsePackageInfo(el));
        }
        return list;
    }

    /// <summary>
    /// 下载 <c>.osp</c> 包到本地路径。Per ADR-0039 §4 endpoint <c>GET /v1/packages/{name}/{version}.osp</c>。
    /// 自动创建目标目录。已存在同名文件会被覆盖。
    /// </summary>
    /// <returns>下载到的本地文件绝对路径。</returns>
    public virtual async Task<string> DownloadPackageAsync(ProviderSource source, string name, string version, string destinationPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationPath);
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var url = source.NormalizedUrl + "v1/packages/" + WebUtility.UrlEncode(name) + "/" + WebUtility.UrlEncode(version) + ".osp";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req, source);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
        return destinationPath;
    }

    /// <summary>
    /// 发布一个 <c>.osp</c> 包到注册源。Per ADR-0039 §10 endpoint <c>PUT /v1/packages/{name}/{version}.osp</c>。
    /// 使用 multipart/form-data 上传, apiKey 作为 Bearer token。
    /// </summary>
    public virtual async Task PublishAsync(ProviderSource source, string packagePath, string? apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(packagePath);
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Package not found.", packagePath);

        // 包名 + 版本从包内 manifest 推断。
        string name, version;
        await using (var pkg = await OspPackage.OpenAsync(packagePath, ct).ConfigureAwait(false))
        {
            var m = await pkg.ReadManifestAsync(ct).ConfigureAwait(false);
            name = m.Name;
            version = m.Version;
        }

        var url = source.NormalizedUrl + "v1/packages/" + WebUtility.UrlEncode(name) + "/" + WebUtility.UrlEncode(version) + ".osp";
        await using var fileStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        using var content = new MultipartFormDataContent
        {
            { new StreamContent(fileStream), "package", Path.GetFileName(packagePath) },
        };

        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        ApplyAuth(req, source);
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// 带 ETag/Last-Modified 条件请求的 GET JSON。Per ADR-0039 §4 / §6 约束 (必须支持缓存条件)。
    /// 双层缓存: 内存层存 ETag/LastModified 供条件请求头; 磁盘层存 body 供 304 Not Modified 时直接返回。
    /// </summary>
    private async Task<string> GetJsonWithCacheAsync(string url, ProviderSource source, CancellationToken ct)
    {
        // 首次访问该 URL 时, 尝试从磁盘缓存 seed 内存缓存的 ETag/LastModified。
        EnsureDiskCacheSeeded(url);

        (string? etag, string? lastMod) = GetCache(url);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req, source);
        if (etag is not null) req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"" + etag + "\""));
        if (lastMod is not null) req.Headers.IfModifiedSince = DateTimeOffset.TryParse(lastMod, out var d) ? d : null;

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotModified)
        {
            // 304: 服务端确认缓存仍有效。从磁盘缓存读取 body 直接返回 (省带宽)。
            var cachedBody = TryReadDiskCacheBody(url);
            if (cachedBody is not null) return cachedBody;
            // 磁盘缓存缺失或损坏 (best-effort): 回退到非条件请求获取最新 body。
            using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(req2, source);
            using var resp2 = await _http.SendAsync(req2, ct).ConfigureAwait(false);
            resp2.EnsureSuccessStatusCode();
            var body2 = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // 把回退获取的 body 一并写回磁盘缓存, 下次 304 即可命中。
            TryWriteDiskCache(url, etag, lastMod, body2);
            return body2;
        }

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // 提取新 ETag / Last-Modified 并更新两层缓存。
        string? newEtag = resp.Headers.ETag?.Tag?.Trim('"');
        string? newLastMod = resp.Content.Headers.LastModified?.ToString("R");
        SetCache(url, newEtag, newLastMod);
        TryWriteDiskCache(url, newEtag, newLastMod, body);
        return body;
    }

    private void ApplyAuth(HttpRequestMessage req, ProviderSource source)
    {
        var token = ResolveAuth(source.Auth);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private (string? ETag, string? LastModified) GetCache(string url)
    {
        lock (_cacheLock) return _cache.TryGetValue(url, out var v) ? v : (null, null);
    }

    private void SetCache(string url, string? etag, string? lastMod)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(url, out var existing))
                existing = (null, null);
            _cache[url] = (etag ?? existing.ETag, lastMod ?? existing.LastModified);
        }
    }

    // ===== ADR-0039 §6: 磁盘缓存层 (best-effort) =====

    /// <summary>
    /// 首次访问某 URL 时, 从磁盘缓存读取 ETag/Last-Modified 注入内存缓存。
    /// 后续请求即可携带条件头, 触发 304 Not Modified 节省带宽。best-effort: 失败静默忽略。
    /// </summary>
    private void EnsureDiskCacheSeeded(string url)
    {
        lock (_cacheLock)
        {
            if (_diskCacheSeeded.Contains(url)) return;
            _diskCacheSeeded.Add(url);
        }

        try
        {
            var path = GetDiskCachePath(url);
            if (!File.Exists(path)) return;
            var entry = ReadDiskCacheEntry(path);
            if (entry is null) return;
            // 仅当内存缓存中没有该 URL 的条目时 seed (避免覆盖更新的内存值)。
            lock (_cacheLock)
            {
                if (!_cache.ContainsKey(url))
                {
                    _cache[url] = (entry.Etag, entry.LastModified);
                }
            }
        }
        catch { /* best-effort: 磁盘缓存读取失败不阻断主流程 */ }
    }

    /// <summary>读取磁盘缓存文件并反序列化为 <see cref="DiskCacheEntry"/>; 文件不存在或格式非法返回 null。</summary>
    private static DiskCacheEntry? ReadDiskCacheEntry(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DiskCacheEntry>(json, JsonOptions);
        }
        catch { return null; }
    }

    /// <summary>从磁盘缓存读取 body 字段; 文件不存在或格式非法返回 null。</summary>
    private string? TryReadDiskCacheBody(string url)
    {
        try
        {
            var path = GetDiskCachePath(url);
            if (!File.Exists(path)) return null;
            var entry = ReadDiskCacheEntry(path);
            return entry?.Body;
        }
        catch { return null; }
    }

    /// <summary>把 (etag, lastModified, body) 写入磁盘缓存。best-effort: 失败静默忽略。</summary>
    private void TryWriteDiskCache(string url, string? etag, string? lastMod, string body)
    {
        try
        {
            Directory.CreateDirectory(_diskCacheDir);
            var entry = new DiskCacheEntry { Etag = etag, LastModified = lastMod, Body = body };
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            var path = GetDiskCachePath(url);
            // 原子写: 先写 .tmp 再 rename, 避免半写文件被其他进程读到。
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch { /* best-effort: 磁盘缓存写入失败不阻断主流程 */ }
    }

    /// <summary>计算 URL 对应的磁盘缓存文件路径: <c>{cacheDir}/{sha256(url)}.json</c>。</summary>
    private string GetDiskCachePath(string url)
    {
        var hashBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        var hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(_diskCacheDir, hex + ".json");
    }

    /// <summary>磁盘缓存条目模型。字段名 camelCase, 与 <see cref="JsonOptions"/> 一致。</summary>
    private sealed class DiskCacheEntry
    {
        public string? Etag { get; set; }
        public string? LastModified { get; set; }
        public string? Body { get; set; }
    }

    private static PackageInfo ParsePackageInfo(JsonElement el)
    {
        var info = new PackageInfo
        {
            Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Latest = el.TryGetProperty("latest", out var l) ? l.GetString() : null,
            Downloads = el.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var dl) ? dl : null,
            Description = el.TryGetProperty("description", out var desc) ? desc.GetString() : null,
        };
        var versions = new List<PackageVersionInfo>();
        if (el.TryGetProperty("versions", out var vArr) && vArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vArr.EnumerateArray())
            {
                versions.Add(new PackageVersionInfo
                {
                    Version = v.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "",
                    ApiVersion = v.TryGetProperty("apiVersion", out var av) ? av.GetString() : null,
                    Stability = v.TryGetProperty("stability", out var st) ? st.GetString() : null,
                    PublishedAt = v.TryGetProperty("publishedAt", out var pa) && DateTimeOffset.TryParse(pa.GetString(), out var pao) ? pao : null,
                    Deprecated = v.TryGetProperty("deprecated", out var dep) && dep.GetBoolean(),
                });
            }
        }
        return info with { Versions = versions };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
