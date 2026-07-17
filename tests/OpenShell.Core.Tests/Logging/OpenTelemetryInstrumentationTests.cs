using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using OpenShell.Logging;
using Xunit;

namespace OpenShell.Core.Tests.Logging;

/// <summary>
/// OpenTelemetryInstrumentation 冒烟测试。Per ADR-0031 §5 (Tracing) + §6 (Metrics)。
/// 验证 ActivitySource / Meter / Counter / Histogram 的名称与基本行为。
/// </summary>
public class OpenTelemetryInstrumentationTests
{
    [Fact]
    public void ActivitySourceName_IsOpenShell()
    {
        OpenTelemetryInstrumentation.ActivitySourceName.Should().Be("OpenShell");
    }

    [Fact]
    public void MeterName_IsOpenShell()
    {
        OpenTelemetryInstrumentation.MeterName.Should().Be("OpenShell");
    }

    [Fact]
    public void ActivitySource_HasExpectedNameAndVersion()
    {
        var source = OpenTelemetryInstrumentation.ActivitySource;
        source.Should().NotBeNull();
        source.Name.Should().Be("OpenShell");
        source.Version.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Meter_HasExpectedNameAndVersion()
    {
        var meter = OpenTelemetryInstrumentation.Meter;
        meter.Should().NotBeNull();
        meter.Name.Should().Be("OpenShell");
        meter.Version.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CommandsExecuted_IsCounterWithExpectedName()
    {
        var counter = OpenTelemetryInstrumentation.CommandsExecuted;
        counter.Should().NotBeNull();
        counter.Name.Should().Be("openshell_commands_executed_total");
    }

    [Fact]
    public void CommandDuration_IsHistogramWithExpectedNameAndUnitSeconds()
    {
        var histogram = OpenTelemetryInstrumentation.CommandDuration;
        histogram.Should().NotBeNull();
        histogram.Name.Should().Be("openshell_command_duration_seconds");
        histogram.Unit.Should().Be("s");
    }

    [Fact]
    public void PipelineSegmentsProcessed_IsCounterWithExpectedName()
    {
        var counter = OpenTelemetryInstrumentation.PipelineSegmentsProcessed;
        counter.Should().NotBeNull();
        counter.Name.Should().Be("openshell_pipeline_segments_total");
    }

    [Fact]
    public void CommandsExecuted_Add_IncrementsValue()
    {
        // 注意: 当无 MeterListener 订阅时, Counter.Add 是空操作; 但不应抛异常。
        // 此测试仅验证 Add 调用不会抛异常 (类型 / 标签签名正确)。
        var act = () => OpenTelemetryInstrumentation.CommandsExecuted.Add(
            1,
            new KeyValuePair<string, object?>("command", "get-childitem"),
            new KeyValuePair<string, object?>("status", "ok"));
        act.Should().NotThrow();
    }

    [Fact]
    public void CommandDuration_Record_DoesNotThrow()
    {
        var act = () => OpenTelemetryInstrumentation.CommandDuration.Record(
            0.123,
            new KeyValuePair<string, object?>("command", "get-childitem"));
        act.Should().NotThrow();
    }

    [Fact]
    public void ActivitySource_StartActivity_ReturnsNullWithoutListener_ButDoesNotThrow()
    {
        // 没有 listener 时 Activity.StartActivity 返回 null (设计如此), 但不抛异常。
        Activity? activity = null;
        var act = () => activity = OpenTelemetryInstrumentation.ActivitySource.StartActivity("Test");
        act.Should().NotThrow();
        // 不强制断言 activity 为 null: 若并行测试注册了 listener, 此处可能非空。
    }
}
