using Microsoft.Extensions.DependencyInjection;
using Zeus;

namespace Zeus.Tests;

/// <summary>
/// 验证 0.2 生命周期：宿主可再次启动、故障后可重开、自动重连，以及运行中增删通道与设备。
/// </summary>
public sealed class LifecycleTests
{
    /// <summary>
    /// 停止后再启动必须重新打开通道，并允许再次写入。
    /// </summary>
    [Fact]
    public async Task Host_CanRestartAfterStop()
    {
        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel("loop"));
        var channel = host.Channels.Get("loop");

        await host.StartAsync();
        Assert.True(host.IsRunning);
        Assert.Equal(ChannelState.Open, channel.State);

        await host.StopAsync();
        Assert.False(host.IsRunning);
        Assert.Equal(ChannelState.Closed, channel.State);

        await host.StartAsync();
        Assert.True(host.IsRunning);
        Assert.Equal(ChannelState.Open, channel.State);
        await channel.WriteAsync(new byte[] { 0x01 });
    }

    /// <summary>
    /// 对已关闭或故障的通道再次 OpenAsync 必须先清理再打开，而不是要求重建实例。
    /// </summary>
    [Fact]
    public async Task Channel_OpenAsync_RecoversFromClosedAndFaulted()
    {
        var channel = new RecoverableChannel("bus");
        await channel.OpenAsync();
        await channel.CloseAsync();
        Assert.Equal(ChannelState.Closed, channel.State);

        await channel.OpenAsync();
        Assert.Equal(ChannelState.Open, channel.State);
        Assert.Equal(2, channel.OpenCount);
        Assert.True(channel.CloseCount >= 1);

        channel.FailNextWrite = true;
        var writeError = await Assert.ThrowsAsync<ZeusChannelException>(() => channel.WriteAsync(new byte[] { 0x01 }));
        Assert.Equal(ChannelState.Faulted, channel.State);
        Assert.Contains("写入失败", writeError.Message, StringComparison.Ordinal);

        await channel.OpenAsync();
        Assert.Equal(ChannelState.Open, channel.State);
        await channel.WriteAsync(new byte[] { 0x02 });
        await channel.DisposeAsync();
    }

    /// <summary>
    /// 调用方取消打开时，通道不能被误标为 Faulted，否则自动重连会把正常取消当故障处理。
    /// </summary>
    [Fact]
    public async Task Channel_OpenAsyncCancellation_DoesNotFaultChannel()
    {
        var channel = new RecoverableChannel("bus") { DelayOpenUntilCancelled = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.OpenAsync(cts.Token));

        Assert.Equal(ChannelState.Created, channel.State);
        await channel.DisposeAsync();
    }

    /// <summary>
    /// 写入取消应按取消传播，不能改写成通道写入失败。
    /// </summary>
    [Fact]
    public async Task Channel_WriteAsyncCancellation_DoesNotFaultChannel()
    {
        var channel = new RecoverableChannel("bus");
        await channel.OpenAsync();
        channel.CancelNextWrite = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.WriteAsync(new byte[] { 0x01 }));

        Assert.Equal(ChannelState.Open, channel.State);
        await channel.DisposeAsync();
    }

    /// <summary>
    /// 通道进入 Faulted 后，自动重连应在短延迟内再次打开。
    /// </summary>
    [Fact]
    public async Task Host_AutomaticallyReopensFaultedChannel()
    {
        var channel = new RecoverableChannel("bus");
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddReconnect(options =>
            {
                options.Enabled = true;
                options.InitialDelay = TimeSpan.FromMilliseconds(40);
                options.MaxDelay = TimeSpan.FromMilliseconds(40);
            });
            builder.Register((_, channels, _) => channels.Add(channel));
        });

        await host.StartAsync();
        channel.FailNextWrite = true;
        await Assert.ThrowsAsync<ZeusChannelException>(() => channel.WriteAsync(new byte[] { 0x01 }));
        Assert.Equal(ChannelState.Faulted, channel.State);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline && channel.State != ChannelState.Open)
        {
            await Task.Delay(20);
        }

        Assert.Equal(ChannelState.Open, channel.State);
        await channel.WriteAsync(new byte[] { 0x02 });
    }

    /// <summary>
    /// 启动过程中调用方取消应按取消传播，并关闭已经打开的通道。
    /// </summary>
    [Fact]
    public async Task Host_StartAsyncCancellation_RollsBackOpenedChannels()
    {
        var slow = new RecoverableChannel("slow") { DelayOpenUntilCancelled = true };
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddVirtualChannel("fast");
            builder.Register((_, channels, _) => channels.Add(slow));
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(cts.Token));

        Assert.False(host.IsRunning);
        Assert.Equal(ChannelState.Closed, host.Channels.Get("fast").State);
        Assert.Equal(ChannelState.Created, slow.State);
    }

    /// <summary>
    /// 自动重连第一次打开失败后，必须继续退避而不是停在 Faulted。
    /// </summary>
    [Fact]
    public async Task Host_RetriesReconnectAfterFailedOpen()
    {
        var channel = new RecoverableChannel("bus");
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddReconnect(options =>
            {
                options.Enabled = true;
                options.InitialDelay = TimeSpan.FromMilliseconds(30);
                options.MaxDelay = TimeSpan.FromMilliseconds(30);
            });
            builder.Register((_, channels, _) => channels.Add(channel));
        });

        await host.StartAsync();
        Assert.Equal(1, channel.OpenCount);
        channel.FailOpenTimes = 1;
        channel.FailNextWrite = true;
        await Assert.ThrowsAsync<ZeusChannelException>(() => channel.WriteAsync(new byte[] { 0x01 }));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline && channel.State != ChannelState.Open)
        {
            await Task.Delay(20);
        }

        Assert.Equal(ChannelState.Open, channel.State);
        Assert.True(channel.OpenCount >= 3);
    }

    /// <summary>
    /// 运行中新增 required 通道打开失败时，应从目录回滚，避免留下半注册故障通道。
    /// </summary>
    [Fact]
    public async Task AddRequiredChannelAsync_RollsBackWhenOpenFails()
    {
        await using var host = ZeusHost.Create();
        await host.StartAsync();

        await Assert.ThrowsAsync<ZeusChannelException>(() =>
            host.AddSerialPortAsync("bad", "ZEUS_MISSING_PORT", 9600));

        Assert.False(host.Channels.TryGet("bad", out _));
    }

    /// <summary>
    /// 自动重连成功时应发布 Scheduled/Succeeded，而不是把成功状态误报为 Cancelled。
    /// </summary>
    [Fact]
    public async Task Host_ReconnectPublishesSucceededWithoutCancelled()
    {
        var channel = new RecoverableChannel("bus");
        var states = new List<ReconnectState>();
        var stateGate = new object();
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddReconnect(options =>
            {
                options.Enabled = true;
                options.InitialDelay = TimeSpan.FromMilliseconds(30);
                options.MaxDelay = TimeSpan.FromMilliseconds(30);
                options.StateChanged += (_, e) =>
                {
                    lock (stateGate)
                    {
                        states.Add(e.State);
                    }
                };
            });
            builder.Register((_, channels, _) => channels.Add(channel));
        });

        await host.StartAsync();
        channel.FailNextWrite = true;
        await Assert.ThrowsAsync<ZeusChannelException>(() => channel.WriteAsync(new byte[] { 0x01 }));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            lock (stateGate)
            {
                if (states.Contains(ReconnectState.Succeeded))
                {
                    break;
                }
            }

            await Task.Delay(20);
        }

        ReconnectState[] snapshot;
        lock (stateGate)
        {
            snapshot = states.ToArray();
        }

        Assert.Contains(ReconnectState.Scheduled, snapshot);
        Assert.Contains(ReconnectState.Succeeded, snapshot);
        Assert.DoesNotContain(ReconnectState.Cancelled, snapshot);
    }

    /// <summary>
    /// 重连状态回调可能由用户代码再进入宿主，不能在服务内部锁里发布导致运行期注册被阻塞。
    /// </summary>
    [Fact]
    public async Task ReconnectStateChanged_CanRegisterChannelDuringCancelledCallback()
    {
        var channel = new RecoverableChannel("bus");
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IZeusHost? callbackHost = null;
        Task? registrationTask = null;
        Exception? callbackException = null;
        var callbackRegistrationCompleted = false;

        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddReconnect(options =>
            {
                options.Enabled = true;
                options.InitialDelay = TimeSpan.FromSeconds(30);
                options.MaxDelay = TimeSpan.FromSeconds(30);
                options.StateChanged += (_, e) =>
                {
                    if (e.State == ReconnectState.Scheduled)
                    {
                        scheduled.TrySetResult();
                        return;
                    }

                    if (e.State != ReconnectState.Cancelled)
                    {
                        return;
                    }

                    try
                    {
                        registrationTask = Task.Run(async () => await callbackHost!.AddVirtualChannelAsync("callback-added"));
                        callbackRegistrationCompleted = registrationTask.Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex)
                    {
                        callbackException = ex;
                    }
                };
            });
            builder.Register((_, channels, _) => channels.Add(channel));
        });
        callbackHost = host;

        await host.StartAsync();
        channel.FailNextWrite = true;
        await Assert.ThrowsAsync<ZeusChannelException>(() => channel.WriteAsync(new byte[] { 0x01 }));
        await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await host.RemoveChannelAsync("bus");

        Assert.Null(callbackException);
        Assert.True(callbackRegistrationCompleted, "重连状态回调不应持有内部锁阻塞运行期注册通道。");
        Assert.NotNull(registrationTask);
        await registrationTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(host.Channels.TryGet("callback-added", out _));
    }

    /// <summary>
    /// 关闭自动重连后，故障通道必须保持 Faulted，直到调用方自行 OpenAsync。
    /// </summary>
    [Fact]
    public async Task Host_DoesNotReconnectWhenDisabled()
    {
        var channel = new RecoverableChannel("bus");
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddReconnect(options => options.Enabled = false);
            builder.Register((_, channels, _) => channels.Add(channel));
        });

        await host.StartAsync();
        channel.FailNextWrite = true;
        await Assert.ThrowsAsync<ZeusChannelException>(() => channel.WriteAsync(new byte[] { 0x01 }));
        await Task.Delay(120);
        Assert.Equal(ChannelState.Faulted, channel.State);
    }

    /// <summary>
    /// 运行中增删虚拟通道与 Modbus 设备后，点表应随之增减。
    /// </summary>
    [Fact]
    public async Task Host_CanAddAndRemoveChannelAndDeviceAtRuntime()
    {
        var memory = new ModbusSlaveMemory();
        memory.HoldingRegisters[0] = 42;

        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddAcquisition(TimeSpan.FromMilliseconds(80));
            builder.AddVirtualChannel("idle");
        });

        await host.StartAsync();
        Assert.Empty(host.Points.All);

        await host.AddVirtualChannelAsync("bus", new ModbusSlaveResponder(1, ModbusTransport.Rtu, memory));
        host.AddModbusRtu("oven", "bus", points: map => map.HoldingRegister("pv", 0));

        var value = await WaitForPointAsync<ushort>(host, "pv");
        Assert.Equal((ushort)42, value);

        await host.RemoveDeviceAsync("oven");
        Assert.Empty(host.Points.All);
        Assert.Throws<ZeusException>(() => host.Devices.Get<ModbusDevice>("oven"));

        await host.RemoveChannelAsync("bus");
        Assert.False(host.Channels.TryGet("bus", out _));
        Assert.NotNull(host.Channels.Get("idle"));
    }

    /// <summary>
    /// 移除仍被设备占用的通道且不允许级联时，必须给出可操作的错误。
    /// </summary>
    [Fact]
    public async Task RemoveChannel_WithoutCascade_ThrowsWhenDeviceBound()
    {
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddVirtualChannel("bus");
            builder.AddModbusRtu("oven", "bus");
        });

        var error = await Assert.ThrowsAsync<ZeusException>(
            () => host.RemoveChannelAsync("bus", removeBoundDevices: false));
        Assert.Contains("oven", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(host.Channels.Get("bus"));
        Assert.NotNull(host.Devices.Get<ModbusDevice>("oven"));
    }

    /// <summary>
    /// 已取消的删除请求不能先改目录再抛取消，否则调用方会以为操作没发生但拓扑已变。
    /// </summary>
    [Fact]
    public async Task RemoveAsync_WithPreCanceledToken_DoesNotMutateRegistries()
    {
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddVirtualChannel("bus");
            builder.AddModbusRtu("oven", "bus");
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RemoveDeviceAsync("oven", cts.Token));
        Assert.NotNull(host.Devices.Get<ModbusDevice>("oven"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RemoveChannelAsync("bus", cancellationToken: cts.Token));
        Assert.NotNull(host.Devices.Get<ModbusDevice>("oven"));
        Assert.NotNull(host.Channels.Get("bus"));
    }

    /// <summary>
    /// 运行期新增通道如果请求已取消，不能把可选通道半注册进目录。
    /// </summary>
    [Fact]
    public async Task AddChannelAsync_WithPreCanceledToken_DoesNotRegisterOptionalChannel()
    {
        await using var host = ZeusHost.Create();
        await host.StartAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.AddVirtualChannelAsync("late", cancellationToken: cts.Token, startupMode: ChannelStartupMode.Optional));

        Assert.False(host.Channels.TryGet("late", out _));
    }

    /// <summary>
    /// ReloadAsync 应按 JSON 差异增删设备，而不只改采集间隔。
    /// </summary>
    [Fact]
    public async Task ReloadAsync_AddsAndRemovesDevices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zeus-reload-{Guid.NewGuid():N}.json");
        const string initial = """
            {
              "acquisition": { "intervalMilliseconds": 80, "pollImmediately": true },
              "channels": [
                { "name": "bus", "type": "virtual", "options": { "responder": "modbus", "unitId": 1, "transport": "rtu" } }
              ],
              "devices": [
                {
                  "name": "oven",
                  "channel": "bus",
                  "type": "modbus-rtu",
                  "options": { "unitId": 1 },
                  "points": [ { "name": "pv", "options": { "table": "holding", "address": 0 } } ]
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, initial);
        try
        {
            await using var host = ZeusHost.Create(builder => builder.AddJsonFile(path, watch: false));
            await host.StartAsync();
            Assert.NotNull(host.Devices.Get<ModbusDevice>("oven"));
            Assert.Equal((ushort)0, await WaitForPointAsync<ushort>(host, "pv"));

            var updated = """
                {
                  "acquisition": { "intervalMilliseconds": 250, "pollImmediately": true },
                  "reconnect": { "enabled": true, "initialDelayMilliseconds": 500, "maxDelayMilliseconds": 5000, "backoffMultiplier": 2 },
                  "channels": [
                    { "name": "bus", "type": "virtual", "options": { "responder": "modbus", "unitId": 1, "transport": "rtu" } }
                  ],
                  "devices": [
                    {
                      "name": "dryer",
                      "channel": "bus",
                      "type": "modbus-rtu",
                      "options": { "unitId": 1 },
                      "points": [ { "name": "humidity", "options": { "table": "holding", "address": 1 } } ]
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(path, updated);
            await host.ReloadAsync(path);

            Assert.Equal(TimeSpan.FromMilliseconds(250), host.Services.GetRequiredService<AcquisitionOptions>().Interval);
            Assert.False(host.Devices.TryGet<ModbusDevice>("oven", out _));
            Assert.NotNull(host.Devices.Get<ModbusDevice>("dryer"));
            Assert.Equal((ushort)0, await WaitForPointAsync<ushort>(host, "humidity"));
            Assert.Throws<ZeusException>(() => host.Points.Get("pv"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 热重载重建通道后，旧实例上的 DataReceived 订阅应迁到同名新通道。
    /// </summary>
    [Fact]
    public async Task ReloadAsync_MigratesChannelSubscriptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zeus-reload-sub-{Guid.NewGuid():N}.json");
        const string initial = """
            {
              "channels": [
                { "name": "meter", "type": "virtual" }
              ]
            }
            """;
        await File.WriteAllTextAsync(path, initial);
        try
        {
            await using var host = ZeusHost.Create(builder => builder.AddJsonFile(path, watch: false));
            var original = host.Channels.Get("meter");
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            original.DataReceived += (_, e) => received.TrySetResult(e.Data.ToArray());
            await host.StartAsync();

            const string updated = """
                {
                  "channels": [
                    { "name": "meter", "type": "virtual", "options": { "unitId": 2 } }
                  ]
                }
                """;
            await File.WriteAllTextAsync(path, updated);
            await host.ReloadAsync(path);

            var replaced = host.Channels.Get("meter");
            Assert.False(ReferenceEquals(original, replaced));
            await replaced.WriteAsync("PING"u8.ToArray());
            var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("PING"u8.ToArray(), payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<T> WaitForPointAsync<T>(IZeusHost host, string name)
    {
        if (!host.IsRunning)
        {
            await host.StartAsync();
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (host.Points.TryGet<T>(name, out var value) && value is not null)
            {
                return value;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"等待点 {name} 超时。");
    }

    /// <summary>
    /// 测试用通道：可按需让下一次写入失败以进入 Faulted，并统计打开/关闭次数。
    /// </summary>
    private sealed class RecoverableChannel : ChannelBase
    {
        public RecoverableChannel(string name)
            : base(name, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
        }

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public bool FailNextWrite { get; set; }

        public bool CancelNextWrite { get; set; }

        public bool DelayOpenUntilCancelled { get; set; }

        /// <summary>接下来若干次 OpenCore 抛出异常，用于验证失败后仍会继续退避。</summary>
        public int FailOpenTimes { get; set; }

        protected override Task OpenCoreAsync(CancellationToken cancellationToken)
        {
            OpenCount++;
            if (DelayOpenUntilCancelled)
            {
                return Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }

            if (FailOpenTimes > 0)
            {
                FailOpenTimes--;
                throw new IOException("模拟打开失败。");
            }

            return Task.CompletedTask;
        }

        protected override Task CloseCoreAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            return Task.CompletedTask;
        }

        protected override Task WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (CancelNextWrite)
            {
                CancelNextWrite = false;
                throw new OperationCanceledException(cancellationToken);
            }

            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("模拟链路断开。");
            }

            return Task.CompletedTask;
        }
    }
}
