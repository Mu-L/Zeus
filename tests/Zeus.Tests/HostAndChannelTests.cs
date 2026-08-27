using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Zeus;

namespace Zeus.Tests;

/// <summary>
/// 验证宿主启停、虚拟通道回显以及目录查找的失败信息是否可操作。
/// </summary>
public sealed class HostAndChannelTests
{
    /// <summary>
    /// 虚拟通道在宿主启动后应处于打开状态，写入的字节必须原样回显。
    /// </summary>
    [Fact]
    public async Task VirtualChannel_EchoesWrittenBytes()
    {
        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel("loop"));
        var channel = host.Channels.Get("loop");
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.DataReceived += (_, e) => received.TrySetResult(e.Data.ToArray());

        await host.StartAsync();
        Assert.Equal(ChannelState.Open, channel.State);

        var payload = Encoding.ASCII.GetBytes("PING");
        await channel.WriteAsync(payload);

        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(payload, actual);

        await host.StopAsync();
        Assert.Equal(ChannelState.Closed, channel.State);
    }

    /// <summary>
    /// 虚拟通道应为写入和回显各发布一条报文追踪记录。
    /// </summary>
    [Fact]
    public async Task VirtualChannel_EmitsPacketTraceForSentAndReceivedBytes()
    {
        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel("loop"));
        var channel = host.Channels.Get("loop");
        var trace = new List<(ChannelTraceDirection Direction, byte[] Data, string Hex)>();
        channel.PacketTraced += (_, e) => trace.Add((e.Direction, e.Data.ToArray(), e.Hex));

        await host.StartAsync();
        var payload = Encoding.ASCII.GetBytes("PING");
        await channel.WriteAsync(payload);

        Assert.Collection(
            trace,
            sent =>
            {
                Assert.Equal(ChannelTraceDirection.Sent, sent.Direction);
                Assert.Equal(payload, sent.Data);
                Assert.Equal("50494E47", sent.Hex);
            },
            received =>
            {
                Assert.Equal(ChannelTraceDirection.Received, received.Direction);
                Assert.Equal(payload, received.Data);
                Assert.Equal("50494E47", received.Hex);
            });
    }

    /// <summary>
    /// 业务事件处理器抛异常不能污染通道生命周期；其他订阅者仍应收到同一事件。
    /// </summary>
    [Fact]
    public async Task ChannelEventHandlerException_DoesNotFaultChannelOrSkipOtherSubscribers()
    {
        var channel = new VirtualChannel("loop");
        var stateNotifications = 0;
        var traceNotifications = 0;
        var received = false;

        channel.StateChanged += (_, _) => throw new InvalidOperationException("state handler failed");
        channel.StateChanged += (_, _) => stateNotifications++;
        channel.PacketTraced += (_, _) => throw new InvalidOperationException("trace handler failed");
        channel.PacketTraced += (_, _) => traceNotifications++;
        channel.DataReceived += (_, _) => throw new InvalidOperationException("data handler failed");
        channel.DataReceived += (_, e) => received = e.Data.Length == 1 && e.Data.Span[0] == 0x01;

        await channel.OpenAsync();
        await channel.WriteAsync(new byte[] { 0x01 });

        Assert.Equal(ChannelState.Open, channel.State);
        Assert.Equal(2, stateNotifications);
        Assert.Equal(2, traceNotifications);
        Assert.True(received);

        await channel.DisposeAsync();
    }

    /// <summary>
    /// 滚动记录器只保留最近 N 条报文，并保持从旧到新的顺序。
    /// </summary>
    [Fact]
    public async Task ChannelTraceBuffer_KeepsMostRecentEntries()
    {
        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel("loop"));
        var channel = host.Channels.Get("loop");
        using var trace = new ChannelTraceBuffer(channel, capacity: 2);

        await host.StartAsync();
        await channel.WriteAsync(new byte[] { 0x01 });
        await channel.WriteAsync(new byte[] { 0x02 });

        var entries = trace.Entries;
        Assert.Collection(
            entries,
            sent =>
            {
                Assert.Equal("loop", sent.ChannelName);
                Assert.Equal(ChannelTraceDirection.Sent, sent.Direction);
                Assert.Equal(new byte[] { 0x02 }, sent.Data.ToArray());
                Assert.Equal("02", sent.Hex);
            },
            received =>
            {
                Assert.Equal("loop", received.ChannelName);
                Assert.Equal(ChannelTraceDirection.Received, received.Direction);
                Assert.Equal(new byte[] { 0x02 }, received.Data.ToArray());
                Assert.Equal("02", received.Hex);
            });

        trace.Clear();
        Assert.Empty(trace.Entries);
    }

    /// <summary>
    /// 滚动记录容量必须为正数，避免静默丢失所有报文。
    /// </summary>
    [Fact]
    public void ChannelTraceBuffer_InvalidCapacity_Throws()
    {
        var channel = new VirtualChannel("loop");
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelTraceBuffer(channel, capacity: 0));
        Assert.Equal("capacity", error.ParamName);
    }

    /// <summary>
    /// 结构化日志器应把 TX/RX 报文写入 ILogger，并在释放后退订。
    /// </summary>
    [Fact]
    public async Task ChannelTraceLogger_WritesStructuredLogEntries()
    {
        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel("loop"));
        var channel = host.Channels.Get("loop");
        var logger = new RecordingLogger();

        using (new ChannelTraceLogger(channel, logger))
        {
            await host.StartAsync();
            await channel.WriteAsync(Encoding.ASCII.GetBytes("PING"));
        }

        await channel.WriteAsync(Encoding.ASCII.GetBytes("PONG"));

        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.EventIds, id => Assert.Equal(ZeusLogEvents.PacketTrace, id));
        Assert.Contains("loop", logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains(nameof(ChannelTraceDirection.Sent), logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains("50494E47", logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains(nameof(ChannelTraceDirection.Received), logger.Messages[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// 重复打开已打开的通道必须幂等，不能再次触发 Opening 迁移。
    /// </summary>
    [Fact]
    public async Task OpenAsync_IsIdempotentWhenAlreadyOpen()
    {
        var channel = new VirtualChannel("once");
        var transitions = new List<(ChannelState Previous, ChannelState Current)>();
        channel.StateChanged += (_, e) => transitions.Add((e.Previous, e.Current));

        await channel.OpenAsync();
        await channel.OpenAsync();

        Assert.Equal(ChannelState.Open, channel.State);
        Assert.Equal(new[]
        {
            (ChannelState.Created, ChannelState.Opening),
            (ChannelState.Opening, ChannelState.Open)
        }, transitions);

        await channel.DisposeAsync();
    }

    /// <summary>
    /// 查找不存在的通道时应列出已注册名称，方便用户对照拼写。
    /// </summary>
    [Fact]
    public async Task ChannelRegistry_MissingName_ListsAvailableChannels()
    {
        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel("meter"));
        var error = Assert.Throws<ZeusException>(() => host.Channels.Get("oven"));
        Assert.Contains("meter", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("oven", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 通道目录观察者抛异常不能阻止其他观察者看到同一次变更。
    /// </summary>
    [Fact]
    public void ChannelRegistryChangedHandlerException_DoesNotSkipOtherSubscribers()
    {
        var registry = new ChannelRegistry();
        var seen = 0;
        registry.Changed += (_, _) => throw new InvalidOperationException("observer failed");
        registry.Changed += (_, e) =>
        {
            seen++;
            Assert.Equal(ChannelRegistryChange.Added, e.Change);
            Assert.Equal("bus", e.Channel.Name);
        };

        registry.Add(new VirtualChannel("bus"));

        Assert.Equal(1, seen);
    }

    /// <summary>
    /// 设备目录观察者抛异常不能阻止点表等其他观察者继续同步拓扑。
    /// </summary>
    [Fact]
    public void DeviceRegistryChangedHandlerException_DoesNotSkipOtherSubscribers()
    {
        var channels = new ChannelRegistry();
        var channel = new VirtualChannel("bus");
        channels.Add(channel);
        var registry = new DeviceRegistry();
        var seen = 0;
        registry.Changed += (_, _) => throw new InvalidOperationException("observer failed");
        registry.Changed += (_, e) =>
        {
            seen++;
            Assert.Equal(DeviceRegistryChange.Added, e.Change);
            Assert.Equal("oven", e.Device.Name);
        };

        registry.Add(new TestDevice("oven", channel));

        Assert.Equal(1, seen);
    }

    /// <summary>
    /// 未启动时写入必须失败，并提示先调用 StartAsync。
    /// </summary>
    [Fact]
    public async Task Write_BeforeStart_ThrowsActionableError()
    {
        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel("meter"));
        var channel = host.Channels.Get("meter");
        var error = await Assert.ThrowsAsync<ZeusChannelException>(() => channel.WriteAsync(new byte[] { 0x01 }));
        Assert.Contains("StartAsync", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 重复通道名必须在构建期失败，而不是拖到运行期。
    /// </summary>
    [Fact]
    public void DuplicateChannelName_FailsAtBuild()
    {
        var error = Assert.Throws<ZeusException>(() =>
            ZeusHost.Create(builder =>
            {
                builder.AddVirtualChannel("meter");
                builder.AddVirtualChannel("meter");
            }));
        Assert.Contains("meter", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertTraceLine(
        string line,
        string channelName,
        ChannelTraceDirection direction,
        string hex)
    {
        var parts = line.Split('\t');
        Assert.Equal(4, parts.Length);
        Assert.True(DateTimeOffset.TryParse(
            parts[0],
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp));
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.Equal(channelName, parts[1]);
        Assert.Equal(direction.ToString(), parts[2]);
        Assert.Equal(hex, parts[3]);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public List<EventId> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            EventIds.Add(eventId);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class TestDevice(string name, IChannel channel) : IDevice
    {
        public string Name { get; } = name;

        public IChannel Channel { get; } = channel;
    }
}
