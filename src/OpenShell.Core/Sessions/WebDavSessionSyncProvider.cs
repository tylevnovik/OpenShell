using System.Net;
using System.Net.Http.Headers;

namespace OpenShell.Sessions;

/// <summary>
/// 基于 WebDAV 的 <see cref="ISessionSyncProvider"/> 实现。Per ADR-0034 §9.
/// 使用 <see cref="HttpClient"/> 向 WebDAV 服务器 PUT/GET <c>sessions/&lt;id&gt;.json</c>。
/// 覆盖 Nextcloud / ownCloud 等 WebDAV 兼容服务 (S3 暂不支持, v1 范围外)。
/// </summary>
/// <remarks>
/// 协议约定:
/// <list type="bullet">
///   <item>上传: <c>PUT &lt;endpoint&gt;/sessions/&lt;name&gt;.json</c> (覆盖已存在文件)。</item>
///   <item>下载: <c>GET &lt;endpoint&gt;/sessions/&lt;name&gt;.json</c> (404 返回 null)。</item>
///   <item>存在性: <c>HEAD</c> 请求 (200/204 = 存在, 404 = 不存在)。</item>
/// </list>
/// 鉴权: HTTP Basic Auth (username + password), 可选。未提供凭据时匿名访问。
/// </remarks>
public sealed class WebDavSessionSyncProvider : ISessionSyncProvider, IDisposable
{
    private const string SessionsSubPath = "sessions/";
    private const string JsonExtension = ".json";

    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly bool _ownsClient;

    /// <summary>
    /// 构造 WebDAV 同步提供者。
    /// </summary>
    /// <param name="endpoint">WebDAV 根 URL (如 <c>https://nc.example.com/dav/openshell-sessions/</c>)。</param>
    /// <param name="username">Basic Auth 用户名 (可选)。</param>
    /// <param name="password">Basic Auth 密码 (可选)。</param>
    /// <param name="client">可选自定义 <see cref="HttpClient"/> (测试注入用); 未提供时内部创建。</param>
    public WebDavSessionSyncProvider(string endpoint, string? username = null, string? password = null, HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("WebDAV endpoint URL must not be empty.", nameof(endpoint));

        _endpoint = endpoint.EndsWith('/') ? endpoint : endpoint + "/";

        if (client is not null)
        {
            _client = client;
            _ownsClient = false;
        }
        else
        {
            _client = new HttpClient();
            _ownsClient = true;
        }

        // 超时: WebDAV 上传可能较慢, 给 30s。
        _client.Timeout = TimeSpan.FromSeconds(30);

        if (!string.IsNullOrEmpty(username))
        {
            var token = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{username}:{password ?? ""}"));
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", token);
        }
    }

    /// <inheritdoc />
    public async Task UploadAsync(string sessionName, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var url = BuildSessionUrl(sessionName);

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StreamContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        try
        {
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new SessionSyncException(
                    $"WebDAV PUT failed for '{sessionName}': {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
            }
        }
        catch (SessionSyncException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SessionSyncException($"WebDAV upload failed for '{sessionName}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Stream?> DownloadAsync(string sessionName, CancellationToken ct = default)
    {
        var url = BuildSessionUrl(sessionName);

        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new SessionSyncException(
                    $"WebDAV GET failed for '{sessionName}': {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
            }

            // 读取到内存流, 避免持有 HttpResponseMessage (已 dispose)。
            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms, ct).ConfigureAwait(false);
            ms.Position = 0;
            return ms;
        }
        catch (SessionSyncException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SessionSyncException($"WebDAV download failed for '{sessionName}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string sessionName, CancellationToken ct = default)
    {
        var url = BuildSessionUrl(sessionName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SessionSyncException($"WebDAV HEAD failed for '{sessionName}': {ex.Message}", ex);
        }
    }

    /// <summary>构建远程会话文件 URL: <c>&lt;endpoint&gt;sessions/&lt;name&gt;.json</c>。</summary>
    private string BuildSessionUrl(string sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
            throw new ArgumentException("Session name must not be empty.", nameof(sessionName));

        // 防御性: sessionName 不应含路径分隔符 (会话名为简单标识符)。
        var safeName = Uri.EscapeDataString(sessionName);
        return $"{_endpoint}{SessionsSubPath}{safeName}{JsonExtension}";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}
