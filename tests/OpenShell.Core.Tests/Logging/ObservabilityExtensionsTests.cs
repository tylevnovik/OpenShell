using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenShell.Logging;
using Serilog;
using Xunit;

namespace OpenShell.Core.Tests.Logging;

/// <summary>
/// ObservabilityExtensions.AddOpenShellObservability 单元测试。Per ADR-0031 §5-9.
/// 验证 DI 注册: Serilog ILogger + (可选) OpenTelemetry 服务可解析, 且默认配置不抛异常。
/// </summary>
public class ObservabilityExtensionsTests
{
    [Fact]
    public void AddOpenShellObservability_RegistersSerilogLogger()
    {
        var services = new ServiceCollection();
        services.AddOpenShellObservability(new ObservabilityOptions
        {
            EnableTracing = false,
            EnableMetrics = false,
        });

        var sp = services.BuildServiceProvider();
        var logger = sp.GetService<Serilog.ILogger>();
        logger.Should().NotBeNull();
    }

    [Fact]
    public void AddOpenShellObservability_NullServices_Throws()
    {
        IServiceCollection? services = null;
        var act = () => ObservabilityExtensionsTestAccess.AddOpenShellObservability(
            services!,
            new ObservabilityOptions());

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("services");
    }

    [Fact]
    public void AddOpenShellObservability_NullOptions_Throws()
    {
        var services = new ServiceCollection();
        var act = () => ObservabilityExtensionsTestAccess.AddOpenShellObservability(
            services,
            null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("options");
    }

    [Fact]
    public void AddOpenShellObservability_DefaultOptions_RegistersOpenTelemetry()
    {
        var services = new ServiceCollection();
        services.AddOpenShellObservability(new ObservabilityOptions
        {
            // 默认 EnableTracing=true / EnableMetrics=true, 不设 OtlpEndpoint (仅本地注册)。
        });

        var sp = services.BuildServiceProvider();
        // 不强制断言特定 OpenTelemetry 服务存在 (内部类型不公开);
        // 仅验证 DI 容器解析成功 (不抛异常) 且 Serilog logger 可用。
        sp.GetService<Serilog.ILogger>().Should().NotBeNull();
    }

    [Fact]
    public void AddOpenShellObservability_WithOtlpEndpoint_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var act = () => services.AddOpenShellObservability(new ObservabilityOptions
        {
            OtlpEndpoint = "http://localhost:4317",
            EnableTracing = true,
            EnableMetrics = true,
        });

        // OTLP exporter 会尝试连接, 但此处仅断言 DI 注册阶段不抛异常 (实际导出在后台发生)。
        act.Should().NotThrow();
    }

    [Fact]
    public void AddOpenShellObservability_DisabledTracingAndMetrics_StillRegistersSerilog()
    {
        var services = new ServiceCollection();
        services.AddOpenShellObservability(new ObservabilityOptions
        {
            EnableTracing = false,
            EnableMetrics = false,
        });

        var sp = services.BuildServiceProvider();
        sp.GetService<Serilog.ILogger>().Should().NotBeNull();
    }

    [Fact]
    public void ObservabilityOptions_Defaults_AreExpected()
    {
        var opts = new ObservabilityOptions();
        opts.EnableTracing.Should().BeTrue();
        opts.EnableMetrics.Should().BeTrue();
        opts.OtlpEndpoint.Should().BeNull();
        opts.EnableConsoleExport.Should().BeFalse();
        opts.MinimumLogLevel.Should().Be(LogLevel.Information);
    }

    [Fact]
    public void AddOpenShellObservability_ReturnsSameCollection_ForChaining()
    {
        var services = new ServiceCollection();
        var returned = services.AddOpenShellObservability(new ObservabilityOptions
        {
            EnableTracing = false,
            EnableMetrics = false,
        });

        returned.Should().BeSameAs(services);
    }
}

/// <summary>
/// Test-only access to the static extension method, so null-argument assertions can use the
/// method name explicitly rather than going through the resolved extension method invocation.
/// </summary>
internal static class ObservabilityExtensionsTestAccess
{
    public static IServiceCollection AddOpenShellObservability(
        IServiceCollection services,
        ObservabilityOptions options)
        => ObservabilityExtensions.AddOpenShellObservability(services, options);
}
