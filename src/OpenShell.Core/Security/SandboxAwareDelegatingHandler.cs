using System.Net.Http;

namespace OpenShell.Security;

/// <summary>
/// 沙箱感知的 HTTP 委托处理器。Per ADR-0036 §11.
/// 检查 <see cref="SandboxContext.Current"/>; 若当前 Provider 沙箱声明
/// <see cref="ProviderSandbox.NetworkAccess"/> == <c>false</c>, 拒绝请求并抛出
/// <see cref="SecuritySandboxViolationException"/>。
/// </summary>
/// <remarks>
/// 使用方式:
/// <list type="bullet">
///   <item>通过 <see cref="Create"/> 工厂创建实例, 手动附加到任意 HttpClient (设置 <see cref="DelegatingHandler.InnerHandler"/> 后使用)。</item>
///   <item>通过 <c>AddSandboxAwareHttp</c> 扩展注册到 DI, 配合 <c>IHttpClientFactory</c> 的 <c>AddHttpMessageHandler&lt;T&gt;()</c> 使用。</item>
/// </list>
/// </remarks>
public sealed class SandboxAwareDelegatingHandler : DelegatingHandler
{
    public SandboxAwareDelegatingHandler() { }

    public SandboxAwareDelegatingHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    /// <summary>
    /// 创建独立实例, 供手动附加到 HttpClient。
    /// 调用方需设置 <see cref="DelegatingHandler.InnerHandler"/> (如 <c>new HttpClientHandler()</c>) 后再传入 <c>new HttpClient(handler)</c>。
    /// </summary>
    public static SandboxAwareDelegatingHandler Create() => new();

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sandbox = SandboxContext.Current;
        if (sandbox is not null && !sandbox.NetworkAccess)
        {
            throw new SecuritySandboxViolationException(
                "Network access denied by provider sandbox (NetworkAccess=false). " +
                $"Provider sandbox restricts outbound HTTP request to '{request.RequestUri}'.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
