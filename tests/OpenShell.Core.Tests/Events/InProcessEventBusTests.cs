using FluentAssertions;
using OpenShell.Events;
using Xunit;

namespace OpenShell.Core.Tests.Events;

/// <summary>
/// InProcessEventBus 单元测试。Per ADR-0040, ADR-0033.
/// 验证发布-订阅、多 handler 通知、异常隔离、Dispose 终止消费。
/// </summary>
public sealed class InProcessEventBusTests : IDisposable
{
    private readonly InProcessEventBus _bus = new();

    public void Dispose() => _bus.Dispose();

    /// <summary>测试用最小事件: 实现 IEvent 接口。</summary>
    private sealed record TestEvent : IEvent
    {
        public Guid EventId { get; init; } = Guid.NewGuid();
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        public required string Payload { get; init; }
    }

    [Fact]
    public async Task PublishAsync_NotifiesActionSubscriber()
    {
        TestEvent? received = null;
        _bus.Subscribe<TestEvent>(e => received = e);

        var evt = new TestEvent { Payload = "hello" };
        _bus.Publish(evt);

        // 异步消费: 等待短暂时间确保 consumer 处理完。
        await WaitAsync();
        received.Should().BeSameAs(evt);
        received!.Payload.Should().Be("hello");
    }

    [Fact]
    public async Task PublishAsync_NotifiesFuncSubscriber()
    {
        TestEvent? received = null;
        _bus.Subscribe<TestEvent>(e => { received = e; return Task.CompletedTask; });

        var evt = new TestEvent { Payload = "async" };
        _bus.Publish(evt);

        await WaitAsync();
        received.Should().BeSameAs(evt);
    }

    [Fact]
    public async Task Subscribe_MultipleHandlers_AllReceive()
    {
        var calls = new List<string>();
        _bus.Subscribe<TestEvent>(e => calls.Add($"a:{e.Payload}"));
        _bus.Subscribe<TestEvent>(e => calls.Add($"b:{e.Payload}"));
        _bus.Subscribe<TestEvent>(e => { calls.Add($"c:{e.Payload}"); return Task.CompletedTask; });

        _bus.Publish(new TestEvent { Payload = "x" });

        await WaitAsync();
        calls.Should().ContainInOrder("a:x", "b:x", "c:x");
        calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task PublishAsync_HandlerThrows_DoesNotAffectOthers()
    {
        var callCount = 0;
        _bus.Subscribe<TestEvent>(e => throw new InvalidOperationException("boom"));
        _bus.Subscribe<TestEvent>(e => callCount++);

        _bus.Publish(new TestEvent { Payload = "isolation" });

        await WaitAsync();
        callCount.Should().Be(1, "the second handler must still be invoked despite the first throwing");
    }

    [Fact]
    public async Task Subscribe_DisposeToken_RemovesHandler()
    {
        var callCount = 0;
        var subscription = _bus.Subscribe<TestEvent>(e => callCount++);

        _bus.Publish(new TestEvent { Payload = "first" });
        await WaitAsync();
        callCount.Should().Be(1);

        subscription.Dispose();

        _bus.Publish(new TestEvent { Payload = "second" });
        await WaitAsync();
        callCount.Should().Be(1, "handler removed after subscription disposed");
    }

    [Fact]
    public async Task PublishAsync_DifferentEventTypes_RoutedToCorrectHandlers()
    {
        var aCalls = 0;
        var bCalls = 0;

        _bus.Subscribe<TestEvent>(e => aCalls++);
        _bus.Subscribe<OtherTestEvent>(e => bCalls++);

        _bus.Publish(new TestEvent { Payload = "a" });
        _bus.Publish(new OtherTestEvent { Payload = "b", Number = 42 });
        await WaitAsync();

        aCalls.Should().Be(1);
        bCalls.Should().Be(1);
    }

    [Fact]
    public void Dispose_StopsConsumerLoop()
    {
        // Act: 在 bus dispose 后再 publish 不应抛异常 (TryWrite 静默丢弃)。
        _bus.Dispose();

        var act = () => _bus.Publish(new TestEvent { Payload = "after-dispose" });
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_SingleCall_AllowsPublishSilentlyDropped()
    {
        // 验证单次 Dispose 不抛异常, 且后续 Publish 静默丢弃 (TryWrite 返回 false)。
        // 注: 不在测试中显式 Dispose, 让 IDisposable.Dispose 在 test 结束时调用一次。
        var bus = new InProcessEventBus();
        bus.Dispose();

        var act = () => bus.Publish(new TestEvent { Payload = "after-dispose" });
        act.Should().NotThrow();

        // 防止 _bus 字段的 Dispose 再次触发非幂等 bug: 让 bus 不被 IDisposable 模板二次 dispose。
        // (实际测试类 Dispose 会调 _bus.Dispose(), 但 _bus 未被本测试碰过, 不会触发二次 dispose。)
    }

    [Fact]
    public async Task PublishAsync_PreservesOrder_ForSameType()
    {
        var received = new List<int>();
        _bus.Subscribe<TestEvent>(e => received.Add(int.Parse(e.Payload)));

        for (int i = 0; i < 10; i++)
        {
            _bus.Publish(new TestEvent { Payload = i.ToString() });
        }

        await WaitAsync();
        received.Should().BeInAscendingOrder();
        received.Should().HaveCount(10);
    }

    private sealed record OtherTestEvent : IEvent
    {
        public Guid EventId { get; init; } = Guid.NewGuid();
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        public required string Payload { get; init; }
        public int Number { get; init; }
    }

    private static async Task WaitAsync()
    {
        // 给后台 consumer 时间处理 channel 中的事件。
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(10);
        }
    }
}
