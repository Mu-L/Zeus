using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using Zeus;

namespace Zeus.Tests;

/// <summary>
/// 验证 JSON 配置的校验、装载与采集间隔热更新。
/// </summary>
public sealed class ConfigurationTests
{
    private const string ValidJson = """
        {
          "acquisition": { "intervalMilliseconds": 200, "pollImmediately": true },
          "channels": [
            { "name": "bus", "type": "virtual", "options": { "responder": "modbus", "unitId": 1, "transport": "rtu" } }
          ],
          "devices": [
            {
              "name": "oven",
              "channel": "bus",
              "type": "modbus-rtu",
              "options": { "unitId": 1 },
                "points": [
                { "name": "temperature", "options": { "table": "holding", "address": 0, "scale": 0.1, "lowAlarmLimit": 10, "highAlarmLimit": 80 } },
                { "name": "setpoint", "options": { "table": "holding", "address": 1, "scale": 0.1, "writable": true } },
                { "name": "heater", "options": { "table": "coil", "address": 2, "writable": true } }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// 合法 JSON 应登记通道、设备与点，并采到虚拟从站初值。
    /// </summary>
    [Fact]
    public async Task AddJson_CreatesChannelDeviceAndPoints()
    {
        await using var host = ZeusHost.Create(builder => builder.AddJson(ValidJson, "测试配置"));
        Assert.NotNull(host.Channels.Get("bus"));
        Assert.NotNull(host.Devices.Get<ModbusDevice>("oven"));

        await host.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        double? temperature = null;
        while (DateTime.UtcNow < deadline)
        {
            if (host.Points.TryGet<double>("temperature", out var value))
            {
                temperature = value;
                break;
            }

            await Task.Delay(20);
        }

        Assert.Equal(0d, temperature);
        Assert.Equal(PointAlarmState.Low, host.Points.Get("temperature").AlarmState);
        Assert.True(host.Points.Get("setpoint").Definition.Writable);
        Assert.True(host.Points.Get("heater").Definition.Writable);
        Assert.False(host.Points.Get("temperature").Definition.Writable);
        Assert.Equal(TimeSpan.FromMilliseconds(200), host.Services.GetRequiredService<AcquisitionOptions>().Interval);

        await host.Points.WriteAsync("setpoint", 12.5);
        Assert.Equal(12.5, host.Points.Get<double>("setpoint"), 3);
    }

    /// <summary>
    /// 只读数据区不能在 JSON 里标为可写。
    /// </summary>
    [Fact]
    public void WritableInputRegister_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [ { "name": "bus", "type": "virtual" } ],
              "devices": [
                {
                  "name": "oven",
                  "channel": "bus",
                  "type": "modbus-rtu",
                  "points": [
                    { "name": "status", "options": { "table": "input", "address": 0, "writable": true } }
                  ]
                }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "可写配置"));
        Assert.Contains("writable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JSON 报警限必须保持低限不高于高限。
    /// </summary>
    [Fact]
    public void PointAlarmLimitRange_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [ { "name": "bus", "type": "virtual" } ],
              "devices": [
                {
                  "name": "oven",
                  "channel": "bus",
                  "type": "modbus-rtu",
                  "points": [
                    { "name": "temperature", "options": { "table": "holding", "address": 0, "lowAlarmLimit": 90, "highAlarmLimit": 80 } }
                  ]
                }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "报警配置"));
        Assert.Contains("lowAlarmLimit", error.Message, StringComparison.Ordinal);
        Assert.Contains("highAlarmLimit", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 设备引用不存在的通道必须在装载期失败。
    /// </summary>
    [Fact]
    public void MissingChannel_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [],
              "devices": [ { "name": "oven", "channel": "bus", "type": "modbus-rtu" } ]
            }
            """;
        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "坏配置"));
        Assert.Contains("bus", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("channels", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 非法 JSON 应指出语法问题，而不是抛出原始序列化异常给用户。
    /// </summary>
    [Fact]
    public void InvalidJson_IsActionable()
    {
        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson("{", "坏配置"));
        Assert.Contains("合法 JSON", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 重复通道名必须失败。
    /// </summary>
    [Fact]
    public void DuplicateChannelName_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [
                { "name": "bus", "type": "virtual" },
                { "name": "bus", "type": "virtual" }
              ]
            }
            """;
        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json));
        Assert.Contains("重复", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 缺少官方协议绑定时，提示里应给出可直接复制的安装命令。
    /// </summary>
    [Fact]
    public void MissingProtocolBinderMessage_SuggestsInstallCommand()
    {
        Assert.Contains(
            "dotnet add package Zeus.Protocols.Modbus",
            InvokeMissingDevicePackageMessage("modbus-rtu"),
            StringComparison.Ordinal);

        Assert.Contains(
            "dotnet add package Zeus.Protocols.Modbus",
            InvokeMissingResponderPackageMessage("modbus"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Schema 应按通道类型声明必填字段，让编辑器在保存前就能标红。
    /// </summary>
    [Fact]
    public void Schema_RequiresTypeSpecificChannelFields()
    {
        var schemaPath = FindRepositoryFile("src", "Zeus.Configuration", "Schemas", "zeus.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var channelRules = schema.RootElement.GetProperty("$defs").GetProperty("channel").GetProperty("allOf");

        AssertSchemaRequiresChannelField(channelRules, "serial", "portName");
        AssertSchemaRequiresChannelField(channelRules, "tcp", "host");
        AssertSchemaRequiresChannelField(channelRules, "tcp", "port");
        AssertSchemaRequiresChannelField(channelRules, "udp", "host");
        AssertSchemaRequiresChannelField(channelRules, "udp", "port");
        AssertSchemaRequiresChannelField(channelRules, "tcp-server", "localPort");
        AssertSchemaRequiresChannelField(channelRules, "udp-server", "localPort");

        var channel = schema.RootElement.GetProperty("$defs").GetProperty("channel").GetProperty("properties");
        Assert.False(channel.TryGetProperty("host", out _));
        Assert.False(channel.TryGetProperty("portName", out _));
    }

    /// <summary>
    /// Schema 应暴露启动策略、重连韧性参数和扩展 options，避免编辑器仍按旧配置提示。
    /// </summary>
    [Fact]
    public void Schema_ExposesStartupReconnectAndExtensionOptions()
    {
        var schemaPath = FindRepositoryFile("src", "Zeus.Configuration", "Schemas", "zeus.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var defs = schema.RootElement.GetProperty("$defs");

        var reconnect = defs.GetProperty("reconnect").GetProperty("properties");
        Assert.True(reconnect.TryGetProperty("maxAttempts", out _));
        Assert.True(reconnect.TryGetProperty("jitterRatio", out _));
        Assert.True(reconnect.TryGetProperty("circuitBreakMilliseconds", out _));

        var channel = defs.GetProperty("channel").GetProperty("properties");
        AssertSchemaEnumContains(channel.GetProperty("startup"), "required");
        AssertSchemaEnumContains(channel.GetProperty("startup"), "optional");
        AssertSchemaEnumContains(channel.GetProperty("startup"), "degraded");
        Assert.True(channel.TryGetProperty("options", out _));

        Assert.True(defs.GetProperty("device").GetProperty("properties").TryGetProperty("options", out _));
        Assert.True(defs.GetProperty("point").GetProperty("properties").TryGetProperty("options", out _));
    }

    /// <summary>
    /// JSON 配置应能读取通道启动策略、重连韧性参数和扩展 options。
    /// </summary>
    [Fact]
    public void LoadJson_ParsesStartupReconnectAndOptions()
    {
        const string json = """
            {
              "reconnect": {
                "enabled": true,
                "initialDelayMilliseconds": 250,
                "maxDelayMilliseconds": 2000,
                "backoffMultiplier": 1.5,
                "maxAttempts": 3,
                "jitterRatio": 0.25,
                "circuitBreakMilliseconds": 5000
              },
              "channels": [
                { "name": "bus", "type": "virtual", "startup": "optional", "options": { "driver": "primary", "retry": 2 } }
              ],
              "devices": [
                {
                  "name": "oven",
                  "channel": "bus",
                  "type": "modbus-rtu",
                  "options": { "profile": "fast" },
                  "points": [
                    { "name": "temperature", "options": { "address": 0, "span": 16 } }
                  ]
                }
              ]
            }
            """;

        var document = ZeusConfigurationLoader.LoadJson(json, "扩展配置");

        Assert.Equal(3, document.Reconnect.MaxAttempts);
        Assert.Equal(0.25, document.Reconnect.JitterRatio);
        Assert.Equal(5000, document.Reconnect.CircuitBreakMilliseconds);
        Assert.Equal("optional", document.Channels[0].Startup);
        Assert.Equal("primary", document.Channels[0].Options["driver"].GetString());
        Assert.Equal(2, document.Channels[0].Options["retry"].GetInt32());
        Assert.Equal("fast", document.Devices[0].Options["profile"].GetString());
        Assert.Equal(16, document.Devices[0].Points[0].Options["span"].GetInt32());
    }

    /// <summary>
    /// 协议专属字段只从 options 读取，配置模型不再兼容旧顶层字段。
    /// </summary>
    [Fact]
    public void LoadJson_UsesProtocolFieldsFromOptions()
    {
        const string json = """
            {
              "channels": [
                {
                  "name": "bus",
                  "type": "virtual",
                  "options": { "responder": "modbus", "unitId": 2, "transport": "tcp" }
                }
              ],
              "devices": [
                {
                  "name": "oven",
                  "channel": "bus",
                  "type": "modbus-tcp",
                  "options": { "unitId": 2, "timeoutMilliseconds": 1500 },
                  "points": [
                    {
                      "name": "temperature",
                      "options": { "table": "holding", "address": "0x10", "scale": 0.1, "writable": true }
                    }
                  ]
                }
              ]
            }
            """;

        var document = ZeusConfigurationLoader.LoadJson(json, "options 配置");

        Assert.Equal("modbus", ZeusConfigurationOptions.GetString(document.Channels[0].Options, "responder"));
        Assert.Equal(2, ZeusConfigurationOptions.GetInt32(document.Channels[0].Options, "unitId"));
        Assert.Equal("tcp", ZeusConfigurationOptions.GetString(document.Channels[0].Options, "transport"));
        Assert.Equal(2, ZeusConfigurationOptions.GetInt32(document.Devices[0].Options, "unitId"));
        Assert.Equal(1500, ZeusConfigurationOptions.GetInt32(document.Devices[0].Options, "timeoutMilliseconds"));
        Assert.Equal("holding", ZeusConfigurationOptions.GetString(document.Devices[0].Points[0].Options, "table"));
        Assert.Equal(0x10, ZeusConfigurationOptions.GetInt32(document.Devices[0].Points[0].Options, "address"));
        Assert.Equal(0.1, ZeusConfigurationOptions.GetNullableDouble(document.Devices[0].Points[0].Options, "scale"));
        Assert.True(ZeusConfigurationOptions.GetBoolean(document.Devices[0].Points[0].Options, "writable"));
    }

    /// <summary>
    /// 旧版顶层协议字段必须失败，避免配置模型继续隐式兼容历史格式。
    /// </summary>
    [Theory]
    [InlineData("""
        {
          "channels": [ { "name": "bus", "type": "virtual", "responder": "modbus" } ]
        }
        """, "responder")]
    [InlineData("""
        {
          "channels": [ { "name": "bus", "type": "virtual" } ],
          "devices": [ { "name": "oven", "channel": "bus", "type": "modbus-rtu", "unitId": 1 } ]
        }
        """, "unitId")]
    [InlineData("""
        {
          "channels": [ { "name": "bus", "type": "virtual" } ],
          "devices": [
            {
              "name": "oven",
              "channel": "bus",
              "type": "modbus-rtu",
              "points": [ { "name": "temperature", "address": 0 } ]
            }
          ]
        }
        """, "address")]
    public void LoadJson_RejectsLegacyTopLevelProtocolFields(string json, string fieldName)
    {
        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "旧配置"));
        Assert.Contains(fieldName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 重连韧性参数必须在装载期失败，而不是等到后台线程运行时才暴露错误。
    /// </summary>
    [Theory]
    [InlineData("\"maxAttempts\": -1", "maxAttempts")]
    [InlineData("\"jitterRatio\": -0.1", "jitterRatio")]
    [InlineData("\"circuitBreakMilliseconds\": -1", "circuitBreakMilliseconds")]
    public void InvalidReconnectPolicy_FailsAtLoad(string reconnectBody, string fieldName)
    {
        var json = $$"""
            {
              "reconnect": { {{reconnectBody}} }
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "坏重连配置"));
        Assert.Contains(fieldName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 通道启动策略只接受明确值。
    /// </summary>
    [Fact]
    public void InvalidChannelStartup_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [ { "name": "bus", "type": "virtual", "startup": "ignore-failure" } ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "坏启动策略"));
        Assert.Contains("startup", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 自定义 JSON 绑定应按宿主隔离登记，避免被静态白名单挡住。
    /// </summary>
    [Fact]
    public async Task AddJsonBinder_AllowsHostScopedCustomResponder()
    {
        const string json = """
            {
              "channels": [ { "name": "loop", "type": "virtual", "options": { "responder": "custom-loop" } } ]
            }
            """;

        await using var host = ZeusHost.Create(builder => builder
            .AddJsonBinder(new CustomJsonBinder())
            .AddJson(json, "自定义绑定配置"));

        Assert.NotNull(host.Channels.Get("loop"));
    }

    /// <summary>
    /// JSON 配置可声明 UDP 通道，并保留本地端口选项。
    /// </summary>
    [Fact]
    public async Task AddJson_CreatesUdpChannel()
    {
        const string json = """
            {
              "channels": [
                { "name": "wireless", "type": "udp", "options": { "host": "127.0.0.1", "port": 1502, "localPort": 0 } }
              ]
            }
            """;

        await using var host = ZeusHost.Create(builder => builder.AddJson(json, "UDP 配置"));
        Assert.IsType<UdpClientChannel>(host.Channels.Get("wireless"));
    }

    /// <summary>
    /// JSON 配置可声明 UDP 服务端通道。
    /// </summary>
    [Fact]
    public async Task AddJson_CreatesUdpServerChannel()
    {
        const string json = """
            {
              "channels": [
                { "name": "listener", "type": "udp-server", "options": { "localAddress": "127.0.0.1", "localPort": 0 } }
              ]
            }
            """;

        await using var host = ZeusHost.Create(builder => builder.AddJson(json, "UDP 服务端配置"));
        Assert.IsType<UdpServerChannel>(host.Channels.Get("listener"));
    }

    /// <summary>
    /// JSON 配置可声明 TCP 服务端通道。
    /// </summary>
    [Fact]
    public async Task AddJson_CreatesTcpServerChannel()
    {
        const string json = """
            {
              "channels": [
                { "name": "listener", "type": "tcp-server", "options": { "localAddress": "127.0.0.1", "localPort": 0 } }
              ]
            }
            """;

        await using var host = ZeusHost.Create(builder => builder.AddJson(json, "TCP 服务端配置"));
        Assert.IsType<TcpServerChannel>(host.Channels.Get("listener"));
    }

    /// <summary>
    /// JSON 配置可声明 Mitsubishi MC 设备，默认使用 3E Binary。
    /// </summary>
    [Fact]
    public async Task AddJson_CreatesMitsubishiMcDevice()
    {
        const string json = """
            {
              "channels": [
                { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } }
              ],
              "devices": [
                { "name": "plc", "channel": "plc-link", "type": "mitsubishi-mc" }
              ]
            }
            """;

        await using var host = ZeusHost.Create(builder => builder.AddJson(json, "MC 配置"));
        await host.StartAsync();

        var plc = host.Devices.Get<McDevice>("plc");
        Assert.Equal(McFrameType.Frame3E, plc.Client.Options.FrameType);
        Assert.Equal(McDataEncoding.Binary, plc.Client.Options.DataEncoding);

        await plc.WriteDataRegistersAsync(100, [123]);
        Assert.Equal(new ushort[] { 123 }, await plc.ReadDataRegistersAsync(100, 1));
    }

    /// <summary>
    /// JSON 配置可声明 4E ASCII Mitsubishi MC 设备。
    /// </summary>
    [Fact]
    public async Task AddJson_CreatesMitsubishiMc4EAsciiDevice()
    {
        const string json = """
            {
              "channels": [
                { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } }
              ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "plc-link",
                  "type": "mitsubishi-mc",
                  "options": {
                    "frameType": "4e",
                    "encoding": "ascii",
                    "serialNumber": 4660,
                    "networkNumber": 0,
                    "pcNumber": 255,
                    "ioNumber": 1023,
                    "stationNumber": 0,
                    "monitoringTimer": 16
                  }
                }
              ]
            }
            """;

        await using var host = ZeusHost.Create(builder => builder.AddJson(json, "MC 4E ASCII 配置"));
        await host.StartAsync();

        var plc = host.Devices.Get<McDevice>("plc");
        Assert.Equal(McFrameType.Frame4E, plc.Client.Options.FrameType);
        Assert.Equal(McDataEncoding.Ascii, plc.Client.Options.DataEncoding);
        Assert.Equal((ushort)4660, plc.Client.Options.SerialNumber);

        await plc.WriteLinkRegistersAsync(0x30, [456]);
        Assert.Equal(new ushort[] { 456 }, await plc.ReadLinkRegistersAsync(0x30, 1));
    }

    /// <summary>
    /// MC frameType 必须给出明确可选值。
    /// </summary>
    [Fact]
    public void InvalidMitsubishiMcFrameType_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [ { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } } ],
              "devices": [
                { "name": "plc", "channel": "plc-link", "type": "mitsubishi-mc", "options": { "frameType": "5e" } }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "坏 MC 配置"));
        Assert.Contains("frameType", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1e", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4e", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MC encoding 必须给出明确可选值。
    /// </summary>
    [Fact]
    public void InvalidMitsubishiMcEncoding_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [ { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } } ],
              "devices": [
                { "name": "plc", "channel": "plc-link", "type": "mitsubishi-mc", "options": { "encoding": "utf8" } }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "坏 MC 配置"));
        Assert.Contains("encoding", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("binary", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ascii", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JSON 配置可为 Mitsubishi MC 声明点表，并参与周期采集与写回。
    /// </summary>
    [Fact]
    public async Task AddJson_CreatesMitsubishiMcPoints()
    {
        const string json = """
            {
              "acquisition": { "intervalMilliseconds": 80, "pollImmediately": true },
              "channels": [ { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } } ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "plc-link",
                  "type": "mitsubishi-mc",
                  "points": [
                    { "name": "temperature", "options": { "deviceCode": "D", "address": 100, "scale": 0.1, "lowAlarmLimit": 1, "highAlarmLimit": 80, "writable": true } },
                    { "name": "run", "options": { "deviceCode": "M", "address": 10, "writable": true } },
                    { "name": "ready", "options": { "deviceCode": "X", "address": "0x10" } }
                  ]
                }
              ]
            }
            """;

        await using var host = ZeusHost.Create(builder => builder.AddJson(json, "MC 点表配置"));
        await host.StartAsync();

        Assert.Equal(0d, await WaitForPointAsync<double>(host, "temperature"));
        Assert.Equal(PointAlarmState.Low, host.Points.Get("temperature").AlarmState);
        Assert.True(host.Points.Get("temperature").Definition.Writable);
        Assert.True(host.Points.Get("run").Definition.Writable);
        Assert.False(host.Points.Get("ready").Definition.Writable);

        await host.Points.WriteAsync("temperature", 12.3);
        await host.Points.WriteAsync("run", true);

        var plc = host.Devices.Get<McDevice>("plc");
        Assert.Equal(new ushort[] { 123 }, await plc.ReadDataRegistersAsync(100, 1));
        Assert.Equal(new[] { true }, await plc.ReadInternalRelaysAsync(10, 1));
        Assert.Equal(12.3, host.Points.Get<double>("temperature"), 3);
        Assert.True(host.Points.Get<bool>("run"));
    }

    /// <summary>
    /// MC X 输入继电器不能在 JSON 点表中声明为可写。
    /// </summary>
    [Fact]
    public void MitsubishiMcWritableInputRelay_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [ { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } } ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "plc-link",
                  "type": "mitsubishi-mc",
                  "points": [ { "name": "ready", "options": { "deviceCode": "X", "address": 16, "writable": true } } ]
                }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "MC X 可写配置"));
        Assert.Contains("X", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("writable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MC 点表必须使用受支持的软元件代码。
    /// </summary>
    [Fact]
    public void MitsubishiMcInvalidDeviceCode_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [ { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } } ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "plc-link",
                  "type": "mitsubishi-mc",
                  "points": [ { "name": "bad", "options": { "deviceCode": "B", "address": 0 } } ]
                }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "MC 坏软元件配置"));
        Assert.Contains("deviceCode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("D", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZR", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MC 位软元件不能配置数值报警限。
    /// </summary>
    [Fact]
    public void MitsubishiMcBitAlarmLimits_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [ { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } } ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "plc-link",
                  "type": "mitsubishi-mc",
                  "points": [ { "name": "run", "options": { "deviceCode": "M", "address": 10, "highAlarmLimit": 1 } } ]
                }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "MC 位报警配置"));
        Assert.Contains("位软元件", error.Message, StringComparison.Ordinal);
        Assert.Contains("highAlarmLimit", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// MC 1E 帧不支持 ZR 点，应在装载期失败。
    /// </summary>
    [Fact]
    public void MitsubishiMc1EExtendedFileRegister_FailsAtLoad()
    {
        const string json = """
            {
              "channels": [ { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } } ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "plc-link",
                  "type": "mitsubishi-mc",
                  "options": { "frameType": "1e" },
                  "points": [ { "name": "recipe", "options": { "deviceCode": "ZR", "address": 0 } } ]
                }
              ]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "MC 1E ZR 配置"));
        Assert.Contains("1E", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZR", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ReloadAsync 只改采集间隔时不重建设备。
    /// </summary>
    [Fact]
    public async Task ReloadAsync_UpdatesIntervalWithoutRecreatingDevices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zeus-config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, ValidJson);
        try
        {
            await using var host = ZeusHost.Create(builder => builder.AddJsonFile(path, watch: false));
            var oven = host.Devices.Get<ModbusDevice>("oven");
            Assert.Equal(TimeSpan.FromMilliseconds(200), host.Services.GetRequiredService<AcquisitionOptions>().Interval);

            var updated = ValidJson.Replace("\"intervalMilliseconds\": 200", "\"intervalMilliseconds\": 800", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, updated);
            await host.ReloadAsync(path);

            Assert.Equal(TimeSpan.FromMilliseconds(800), host.Services.GetRequiredService<AcquisitionOptions>().Interval);
            Assert.Same(oven, host.Devices.Get<ModbusDevice>("oven"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 已取消的热更新不能只改运行选项后静默成功。
    /// </summary>
    [Fact]
    public async Task ReloadAsync_WithPreCanceledToken_DoesNotChangeOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zeus-config-cancel-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, ValidJson);
        try
        {
            await using var host = ZeusHost.Create(builder => builder.AddJsonFile(path, watch: false));
            var updated = ValidJson.Replace("\"intervalMilliseconds\": 200", "\"intervalMilliseconds\": 800", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, updated);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReloadAsync(path, cts.Token));

            Assert.Equal(TimeSpan.FromMilliseconds(200), host.Services.GetRequiredService<AcquisitionOptions>().Interval);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 热更新应能按 MC 配置指纹重建设备，例如从 3E Binary 切到 4E ASCII。
    /// </summary>
    [Fact]
    public async Task ReloadAsync_RecreatesMitsubishiMcDeviceWhenOptionsChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zeus-mc-config-{Guid.NewGuid():N}.json");
        var initial = """
            {
              "channels": [ { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } } ],
              "devices": [ { "name": "plc", "channel": "plc-link", "type": "mitsubishi-mc" } ]
            }
            """;
        var updated = """
            {
              "channels": [ { "name": "plc-link", "type": "virtual", "options": { "responder": "mc" } } ],
              "devices": [
                {
                  "name": "plc",
                  "channel": "plc-link",
                  "type": "mitsubishi-mc",
                  "options": {
                    "frameType": "4e",
                    "encoding": "ascii",
                    "serialNumber": 77
                  }
                }
              ]
            }
            """;

        await File.WriteAllTextAsync(path, initial);
        try
        {
            await using var host = ZeusHost.Create(builder => builder.AddJsonFile(path, watch: false));
            var before = host.Devices.Get<McDevice>("plc");
            Assert.Equal(McFrameType.Frame3E, before.Client.Options.FrameType);

            await File.WriteAllTextAsync(path, updated);
            await host.ReloadAsync(path);

            var after = host.Devices.Get<McDevice>("plc");
            Assert.NotSame(before, after);
            Assert.Equal(McFrameType.Frame4E, after.Client.Options.FrameType);
            Assert.Equal(McDataEncoding.Ascii, after.Client.Options.DataEncoding);
            Assert.Equal((ushort)77, after.Client.Options.SerialNumber);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 找不到文件时应给出绝对路径。
    /// </summary>
    [Fact]
    public void MissingFile_MentionsFullPath()
    {
        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadFile("definitely-missing-zeus.json"));
        Assert.Contains("找不到配置文件", error.Message, StringComparison.Ordinal);
        Assert.Contains("definitely-missing-zeus.json", error.Message, StringComparison.OrdinalIgnoreCase);
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

    private static string InvokeMissingDevicePackageMessage(string normalizedType)
    {
        var method = typeof(ZeusJsonBinders).GetMethod("MissingDevicePackageMessage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ZeusJsonBinders), "MissingDevicePackageMessage");

        return Assert.IsType<string>(method.Invoke(null, [normalizedType]));
    }

    private static string InvokeMissingResponderPackageMessage(string normalizedResponder)
    {
        var method = typeof(ZeusJsonBinders).GetMethod("MissingResponderPackageMessage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ZeusJsonBinders), "MissingResponderPackageMessage");

        return Assert.IsType<string>(method.Invoke(null, [normalizedResponder]));
    }

    private static void AssertSchemaRequiresChannelField(JsonElement channelRules, string channelType, string fieldName)
    {
        var found = false;
        foreach (var rule in channelRules.EnumerateArray())
        {
            if (!rule.TryGetProperty("if", out var condition)
                || !condition.TryGetProperty("properties", out var properties)
                || !properties.TryGetProperty("type", out var type)
                || !type.TryGetProperty("const", out var typeName)
                || !string.Equals(typeName.GetString(), channelType, StringComparison.Ordinal)
                || !condition.TryGetProperty("required", out var conditionRequired)
                || !RequiredArrayContains(conditionRequired, "type")
                || !rule.TryGetProperty("then", out var consequence)
                || !consequence.TryGetProperty("required", out var consequenceRequired)
                || !RequiredArrayContains(consequenceRequired, "options")
                || !consequence.TryGetProperty("properties", out var consequenceProperties)
                || !consequenceProperties.TryGetProperty("options", out var options)
                || !options.TryGetProperty("required", out var optionsRequired))
            {
                continue;
            }

            found = RequiredArrayContains(optionsRequired, fieldName);
            if (found)
            {
                break;
            }
        }

        Assert.True(found, $"schema channel type '{channelType}' should require '{fieldName}'.");
    }

    private static bool RequiredArrayContains(JsonElement required, string fieldName)
    {
        foreach (var item in required.EnumerateArray())
        {
            if (string.Equals(item.GetString(), fieldName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertSchemaEnumContains(JsonElement schema, string value)
    {
        foreach (var item in schema.GetProperty("enum").EnumerateArray())
        {
            if (string.Equals(item.GetString(), value, StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail($"schema enum should contain '{value}'.");
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        var relativePath = Path.Combine(relativeSegments);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"找不到仓库文件 {relativePath}。", relativePath);
    }

    private sealed class CustomJsonBinder : IZeusJsonBinder
    {
        public IReadOnlyList<string> DeviceTypes => [];

        public IReadOnlyList<string> ResponderTypes => ["custom-loop"];

        public void ValidateDevice(DeviceConfiguration device, string path)
            => throw new NotSupportedException();

        public void ValidateResponder(ChannelConfiguration channel, string path)
        {
        }

        public void ApplyDevice(DeviceConfiguration device, ZeusHostBuilder? builder = null, IZeusHost? host = null)
            => throw new NotSupportedException();

        public IVirtualResponder? CreateResponder(ChannelConfiguration channel) => null;

        public string DeviceFingerprint(DeviceConfiguration device) => string.Empty;
    }
}
