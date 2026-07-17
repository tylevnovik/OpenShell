using System.Reflection;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using Xunit;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.TestUtils.Contract;

/// <summary>
/// Provider 契约测试基类。Per ADR-0033: 各 Provider 测试项目继承此类，
/// 自动覆盖 Provider 契约（capabilities 与接口一致、cancellation 友好、GetItem/GetChildren 基本契约）。
/// </summary>
/// <typeparam name="TProvider">被测 Provider 类型。</typeparam>
public abstract class ProviderContractTests<TProvider> where TProvider : class, IProvider
{
    /// <summary>创建一个新的 Provider 实例（每个测试独立）。</summary>
    protected abstract TProvider CreateProvider();

    /// <summary>返回测试用的根路径（用于 GetItem/GetChildren 测试）。</summary>
    protected abstract ItemPath GetTestRoot();

    [Fact]
    public void Info_Name_IsNotEmpty()
    {
        var p = CreateProvider();
        Assert.False(string.IsNullOrWhiteSpace(p.Info.Name));
    }

    [Fact]
    public void Info_Version_IsValid()
    {
        var p = CreateProvider();
        Assert.NotNull(p.Info.Version);
    }

    [Fact]
    public void Capabilities_MatchImplementedInterfaces()
    {
        var p = CreateProvider();
        var expected = ComputeExpectedCapabilities(p);
        var actual = p.Capabilities;

        // 每个 capability flag 必须在两者中同时出现或同时缺失。
        foreach (ProviderCapability flag in Enum.GetValues(typeof(ProviderCapability)))
        {
            if (flag == ProviderCapability.None) continue;
            Assert.Equal(expected.Contains(flag), actual.Contains(flag));
        }
    }

    [Fact]
    public async Task InitialiseAsync_DefaultToken_DoesNotThrow()
    {
        var p = CreateProvider();
        await p.InitialiseAsync();
    }

    [Fact]
    public virtual async Task GetItemAsync_Nonexistent_ReturnsNull()
    {
        var p = CreateProvider();
        if (p is not IItemProvider itemProvider) return;
        var path = GetNonexistentPath();
        var item = await itemProvider.GetItemAsync(path);
        Assert.Null(item);
    }

    [Fact]
    public virtual async Task GetChildrenAsync_Nonexistent_ReturnsEmpty()
    {
        var p = CreateProvider();
        if (p is not IContainerProvider container) return;
        var path = GetNonexistentPath();
        var opts = new EnumerationOptions();
        var list = new List<IItem>();
        await foreach (var i in container.GetChildrenAsync(path, opts))
            list.Add(i);
        Assert.Empty(list);
    }

    [Fact]
    public virtual async Task AllAsyncMethods_AcceptCancellation()
    {
        var p = CreateProvider();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        foreach (var method in typeof(TProvider).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var returnType = method.ReturnType;
            bool returnsAsync = returnType == typeof(Task)
                || returnType == typeof(ValueTask)
                || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
            if (!returnsAsync) continue;
            if (method.GetParameters().All(pi => pi.ParameterType != typeof(CancellationToken))) continue;

            // 跳过 InitialiseAsync（默认实现不抛取消异常）。
            if (method.Name == nameof(IProvider.InitialiseAsync)) continue;

            // 构造参数：CancellationToken 用已取消，其他用 default。
            var args = method.GetParameters()
                .Select(pi => pi.ParameterType == typeof(CancellationToken)
                    ? (object)cts.Token
                    : GetTypeDefault(pi.ParameterType))
                .ToArray();

            try
            {
                var result = method.Invoke(p, args);
                if (result is Task t)
                {
                    await t;
                }
                else if (result is ValueTask vt)
                {
                    await vt;
                }
                else if (result != null && result.GetType().IsGenericType)
                {
                    var gt = result.GetType().GetGenericTypeDefinition();
                    if (gt == typeof(Task<>) || gt == typeof(ValueTask<>))
                    {
                        var awaiter = result.GetType().GetMethod("GetAwaiter")!.Invoke(result, null);
                        var getResult = awaiter!.GetType().GetMethod("GetResult");
                        getResult?.Invoke(awaiter, null);
                    }
                    else if (gt == typeof(IAsyncEnumerable<>))
                    {
                        // 触发迭代以触发 cancellation。
                        var enumerator = result.GetType().GetMethod("GetAsyncEnumerator")!.Invoke(result, new object?[] { cts.Token });
                        var moveNext = enumerator!.GetType().GetMethod("MoveNextAsync");
                        if (moveNext != null)
                        {
                            dynamic vt2 = moveNext.Invoke(enumerator, null)!;
                            await ((ValueTask<bool>)vt2);
                        }
                    }
                }
            }
            catch (TargetInvocationException tie) when (tie.InnerException is OperationCanceledException)
            {
                // expected（反射调用包装的异常）。
            }
            catch (OperationCanceledException)
            {
                // expected (包括 TaskCanceledException, 其派生自 OperationCanceledException)
            }
            // 其他异常视为契约违反（provider 没正确处理取消）。
        }
    }

    /// <summary>默认返回根路径 + 不存在的子路径。可重写以提供更精确的不存在路径。</summary>
    protected virtual ItemPath GetNonexistentPath()
    {
        var root = GetTestRoot();
        return root.Combine("__definitely_not_exists__" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>根据实现的接口反射计算期望的 capability 集合。</summary>
    private static IReadOnlySet<ProviderCapability> ComputeExpectedCapabilities(IProvider provider)
    {
        var set = new HashSet<ProviderCapability>();
        if (provider is IItemProvider) set.Add(ProviderCapability.Item);
        if (provider is IContainerProvider) set.Add(ProviderCapability.Container);
        if (provider is INavigationProvider) set.Add(ProviderCapability.Navigation);
        if (provider is IContentProvider) set.Add(ProviderCapability.Content);
        if (provider is IContentWriterProvider) set.Add(ProviderCapability.ContentWrite);
        if (provider is IPropertyProvider) set.Add(ProviderCapability.Property);
        if (provider is ISecurityProvider) set.Add(ProviderCapability.Security);
        if (provider is IDriveProvider) set.Add(ProviderCapability.Drive);
        return set;
    }

    private static object? GetTypeDefault(Type t)
    {
        if (t.IsValueType) return Activator.CreateInstance(t);
        if (t == typeof(string)) return null;
        return null;
    }
}
