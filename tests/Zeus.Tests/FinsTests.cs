using Zeus;

namespace Zeus.Tests;

/// <summary>
/// 通过内存虚拟 PLC 验证 Omron FINS UDP/TCP、内存区读写、点表与 JSON 配置行为。
/// </summary>
public sealed class FinsTests
{
    /// <summary>
    /// FINS/UDP 设备应能读写 DM 字、CIO 位、填充字区，并进行多点读取。
    /// </summary>
    [Fact]
    public async Task UdpDevice_ReadsWritesFillsAndReadsMultipleMemoryAreas()
    {
        var memory = new FinsSlaveMemory();
        memory.DataMemoryWords[100] = 1234;
        memory.CioWords[20] = 0b_0000_1000;

        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddVirtualChannel("fins-link", new FinsSlaveResponder(FinsTransport.Udp, memory));
            builder.AddOmronFins("plc", "fins-link", FinsTransport.Udp, new FinsOptions
            {
                SourceNode = 10,
                DestinationNode = 1
            });
        });

        await host.StartAsync();
        var plc = host.Devices.Get<FinsDevice>("plc");

        Assert.Equal(new ushort[] { 1234 }, await plc.ReadDataMemoryWordsAsync(100, 1));
        Assert.True((await plc.ReadCioBitsAsync(20, 3, 1))[0]);

        await plc.WriteDataMemoryWordsAsync(110, [10, 20, 30]);
        Assert.Equal(new ushort[] { 10, 20, 30 }, await plc.ReadDataMemoryWordsAsync(110, 3));

        await plc.WriteCioBitsAsync(21, 1, [true, false, true]);
        Assert.True((memory.CioWords[21] & 0b_0010) != 0);
        Assert.False((memory.CioWords[21] & 0b_0100) != 0);
        Assert.True((memory.CioWords[21] & 0b_1000) != 0);

        await plc.FillWordsAsync(FinsMemoryAreaCode.DataMemoryWord, 120, 3, 0x55AA);
        Assert.Equal(new ushort[] { 0x55AA, 0x55AA, 0x55AA }, await plc.ReadDataMemoryWordsAsync(120, 3));

        var values = await plc.ReadMultipleAsync([
            new FinsMemoryAddress(FinsMemoryAreaCode.DataMemoryWord, 100),
            new FinsMemoryAddress(FinsMemoryAreaCode.CioBit, 21, 3)
        ]);
        Assert.Equal((ushort)1234, values[0].WordValue);
        Assert.True(values[1].BitValue);
    }

    /// <summary>
    /// FINS/TCP 设备应先完成节点地址握手，再执行普通 FINS 帧发送。
    /// </summary>
    [Fact]
    public async Task TcpDevice_UsesNodeAddressHandshake()
    {
        var memory = new FinsSlaveMemory();
        memory.DataMemoryWords[10] = 99;
        var slaveOptions = new FinsOptions { SourceNode = 25, DestinationNode = 3 };

        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddVirtualChannel("fins-tcp", new FinsSlaveResponder(FinsTransport.Tcp, memory, slaveOptions));
            builder.AddOmronFins("plc", "fins-tcp", FinsTransport.Tcp, new FinsOptions { TcpRequestedClientNode = 7 });
        });

        await host.StartAsync();
        var plc = host.Devices.Get<FinsDevice>("plc");

        Assert.Equal(new ushort[] { 99 }, await plc.ReadDataMemoryWordsAsync(10, 1));
        Assert.Equal((byte)7, plc.Client.Options.SourceNode);
        Assert.Equal((byte)3, plc.Client.Options.DestinationNode);
    }

    /// <summary>
    /// FINS 点表应接入宿主采集循环，并支持按点名写回可写点。
    /// </summary>
    [Fact]
    public async Task FinsDevice_PollsAndWritesPointMap()
    {
        var memory = new FinsSlaveMemory();
        memory.DataMemoryWords[100] = 250;
        memory.CioWords[10] = 0x0001;

        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddAcquisition(TimeSpan.FromMilliseconds(50));
            builder.AddVirtualChannel("fins-link", new FinsSlaveResponder(FinsTransport.Udp, memory));
            builder.AddOmronFins("plc", "fins-link", FinsTransport.Udp, new FinsOptions { SourceNode = 10, DestinationNode = 1 }, points: map => map
                .DmWord("temperature", 100, 0.1).Writable("temperature")
                .CioBit("running", 10, 0).Writable("running"));
        });

        await host.StartAsync();

        Assert.Equal(25.0, await WaitForPointAsync<double>(host, "temperature"), 3);
        Assert.True(await WaitForPointAsync<bool>(host, "running"));
        Assert.True(host.Points.Get("temperature").Definition.Writable);

        await host.Points.WriteAsync("temperature", 18.6);
        await host.Points.WriteAsync("running", false);

        Assert.Equal((ushort)186, memory.DataMemoryWords[100]);
        Assert.Equal(18.6, host.Points.Get<double>("temperature"), 3);
        Assert.False((memory.CioWords[10] & 0x0001) != 0);
    }

    /// <summary>
    /// JSON 配置应能声明 FINS 虚拟 PLC、FINS 设备和点表。
    /// </summary>
    [Fact]
    public async Task AddJson_CreatesOmronFinsDeviceAndPoints()
    {
        const string json = """
            {
              "acquisition": { "intervalMilliseconds": 50, "pollImmediately": true },
              "channels": [
                { "name": "fins-link", "type": "virtual", "options": { "responder": "fins", "transport": "udp" } }
              ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "fins-link",
                  "type": "omron-fins",
                  "options": { "transport": "udp", "sourceNode": 10, "destinationNode": 1 },
                  "points": [
                    { "name": "temperature", "options": { "area": "dm", "address": 100, "dataType": "word", "scale": 0.1, "writable": true } },
                    { "name": "running", "options": { "area": "cio", "address": 10, "bit": 0, "dataType": "bit", "writable": true } }
                  ]
                }
              ]
            }
            """;

        await using var host = ZeusHost.Create(builder => builder.AddJson(json, "FINS 配置"));
        await host.StartAsync();
        var plc = host.Devices.Get<FinsDevice>("plc");

        await plc.WriteDataMemoryWordsAsync(100, [315]);
        Assert.Equal(31.5, await WaitForPointAsync<double>(host, "temperature"), 3);

        await host.Points.WriteAsync("running", true);
        Assert.True((await plc.ReadCioBitsAsync(10, 0, 1))[0]);
    }

    /// <summary>
    /// JSON 位偏移必须在转换成 byte 之前校验，避免 256 等值回绕成 0。
    /// </summary>
    [Fact]
    public void AddJson_RejectsOutOfRangeFinsBitOffset()
    {
        const string json = """
            {
              "channels": [
                { "name": "fins-link", "type": "virtual", "options": { "responder": "fins", "transport": "udp" } }
              ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "fins-link",
                  "type": "omron-fins",
                  "options": { "transport": "udp", "sourceNode": 10, "destinationNode": 1 },
                  "points": [
                    { "name": "bad-bit", "options": { "area": "cio", "address": 10, "bit": 256, "dataType": "bit" } }
                  ]
                }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "FINS bit 配置"));
        Assert.Contains("bit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JSON 中 FINS byte 选项必须在转换前校验，避免节点号等值回绕。
    /// </summary>
    [Fact]
    public void AddJson_RejectsOutOfRangeFinsByteOption()
    {
        const string json = """
            {
              "channels": [
                { "name": "fins-link", "type": "virtual", "options": { "responder": "fins", "transport": "udp" } }
              ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "fins-link",
                  "type": "omron-fins",
                  "options": { "transport": "udp", "sourceNode": 10, "destinationNode": 300 },
                  "points": []
                }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "FINS byte 配置"));
        Assert.Contains("destinationNode", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// FINS 直接 API 应拒绝与点表层一致的非法位偏移和内存区类型组合，避免静默读写错误地址。
    /// </summary>
    [Fact]
    public async Task FinsDevice_RejectsInvalidDirectAddressShape()
    {
        var memory = new FinsSlaveMemory();
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddVirtualChannel("fins-link", new FinsSlaveResponder(FinsTransport.Udp, memory));
            builder.AddOmronFins("plc", "fins-link", FinsTransport.Udp, new FinsOptions { SourceNode = 10, DestinationNode = 1 });
        });

        await host.StartAsync();
        var plc = host.Devices.Get<FinsDevice>("plc");

        await Assert.ThrowsAsync<ZeusProtocolException>(() => plc.ReadCioBitsAsync(20, 16, 1));
        await Assert.ThrowsAsync<ZeusProtocolException>(() => plc.WriteCioBitsAsync(20, 16, [true]));
        await Assert.ThrowsAsync<ZeusProtocolException>(() => plc.ReadBitsAsync(FinsMemoryAreaCode.CioWord, 20, 0, 1));
        await Assert.ThrowsAsync<ZeusProtocolException>(() => plc.ReadWordsAsync(FinsMemoryAreaCode.CioBit, 20, 1));
        await Assert.ThrowsAsync<ZeusProtocolException>(() => plc.ReadMultipleAsync([
            new FinsMemoryAddress(FinsMemoryAreaCode.CioBit, 20, 16),
            new FinsMemoryAddress(FinsMemoryAreaCode.CioWord, 20, 1)
        ]));
    }

    /// <summary>
    /// 地址越界应暴露为 FINS 结束码异常。
    /// </summary>
    [Fact]
    public async Task InvalidAddressThrowsFinsException()
    {
        var memory = new FinsSlaveMemory(dataMemoryWords: 8);
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddVirtualChannel("fins-link", new FinsSlaveResponder(FinsTransport.Udp, memory));
            builder.AddOmronFins("plc", "fins-link", FinsTransport.Udp, new FinsOptions { SourceNode = 10, DestinationNode = 1 });
        });

        await host.StartAsync();
        var plc = host.Devices.Get<FinsDevice>("plc");

        var error = await Assert.ThrowsAsync<FinsException>(() => plc.ReadDataMemoryWordsAsync(100, 1));
        Assert.Equal((ushort)0x1103, error.EndCode);
        Assert.Contains("结束码", error.Message, StringComparison.Ordinal);
    }

    private static async Task<T> WaitForPointAsync<T>(IZeusHost host, string name)
    {
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
}
