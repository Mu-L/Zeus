using Zeus;

namespace Zeus.Tests;

/// <summary>通过内存虚拟 Server 验证 OPC UA 会话、NodeId 点表与 JSON 配置。</summary>
public sealed class OpcUaTests
{
    [Fact]
    public async Task Client_ReadsAndWritesNodes()
    {
        var memory = new OpcUaServerMemory();
        memory.SetDouble("ns=2;s=Temperature", 25.3, writable: true);
        memory.SetText("ns=2;s=Location", "rack-a", writable: true);

        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel("opcua-link", new OpcUaServerResponder(memory)));
        await host.StartAsync();
        await using var client = new OpcUaClient(host.Channels.Get("opcua-link"));

        var temperature = await client.ReadAsync("ns=2;s=Temperature");
        Assert.Equal(OpcUaDataType.Double, temperature.DataType);
        Assert.Equal(25.3, Assert.IsType<double>(temperature.Value), 3);

        await client.WriteAsync("ns=2;s=Temperature", OpcUaValue.Double(18.6));
        Assert.True(memory.TryGet("ns=2;s=Temperature", out var stored));
        Assert.Equal(18.6, Assert.IsType<double>(stored!.Value), 3);
        Assert.Equal("rack-a", await client.ReadTextAsync("ns=2;s=Location"));
    }

    [Fact]
    public async Task Device_PollsAndWritesNodePoints()
    {
        var memory = new OpcUaServerMemory();
        memory.SetInt32("ns=2;s=RawTemperature", 253, writable: true);
        memory.SetText("ns=2;s=Location", "line-a", writable: true);

        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddAcquisition(TimeSpan.FromMilliseconds(50));
            builder.AddVirtualChannel("opcua-link", new OpcUaServerResponder(memory));
            builder.AddOpcUa(
                "plc",
                "opcua-link",
                points: map => map
                    .Int32("temperature", "ns=2;s=RawTemperature", scale: 0.1)
                    .Writable("temperature")
                    .String("location", "ns=2;s=Location"));
        });

        await host.StartAsync();
        Assert.Equal(25.3, await WaitForPointAsync<double>(host, "temperature"), 3);
        Assert.Equal("line-a", await WaitForPointAsync<string>(host, "location"));

        await host.Points.WriteAsync("temperature", 18.6);
        Assert.Equal(18.6, host.Points.Get<double>("temperature"), 3);
        Assert.True(memory.TryGet("ns=2;s=RawTemperature", out var stored));
        Assert.Equal(186, stored!.Value);
    }

    [Fact]
    public async Task AddJson_CreatesOpcUaDeviceAndWritablePoint()
    {
        const string json = """
            {
              "acquisition": { "intervalMilliseconds": 50, "pollImmediately": true },
              "channels": [
                { "name": "opcua-link", "type": "virtual", "options": { "responder": "opcua" } }
              ],
              "devices": [
                {
                  "name": "server",
                  "channel": "opcua-link",
                  "type": "opcua",
                  "points": [
                    { "name": "serverName", "options": { "nodeId": "ns=2;s=ServerName", "dataType": "string", "writable": true } }
                  ]
                }
              ]
            }
            """;

        await using var host = ZeusHost.Create(builder => builder.AddJson(json, "OPC UA 配置"));
        await host.StartAsync();
        var server = host.Devices.Get<OpcUaDevice>("server");
        Assert.True(server.Client.Options.Anonymous);

        Assert.Equal("zeus", await WaitForPointAsync<string>(host, "serverName"));
        await host.Points.WriteAsync("serverName", "edge-01");
        Assert.Equal("edge-01", host.Points.Get<string>("serverName"));
    }

    [Fact]
    public async Task Client_DecodesServiceFaultStatusCode()
    {
        await using var host = ZeusHost.Create(builder => builder.AddVirtualChannel(
            "opcua-link",
            new OpcUaServerResponder(username: "operator", password: "secret")));
        await host.StartAsync();

        await using var client = new OpcUaClient(
            host.Channels.Get("opcua-link"),
            new OpcUaOptions
            {
                Anonymous = false,
                Username = "operator",
                Password = "wrong"
            });

        var error = await Assert.ThrowsAsync<OpcUaException>(() => client.ReadAsync("i=2258"));
        Assert.Equal(OpcUaStatusCodes.BadIdentityTokenInvalid, error.StatusCode);
    }

    [Fact]
    public async Task Device_ReportsBadDataValueStatusBeforeCoercion()
    {
        await using var host = ZeusHost.Create(builder =>
        {
            builder.AddVirtualChannel("opcua-link", new OpcUaServerResponder(new OpcUaServerMemory()));
            builder.AddOpcUa(
                "plc",
                "opcua-link",
                points: map => map.Double("missing", "ns=2;s=MissingTemperature"));
        });

        await host.StartAsync();
        var device = host.Devices.Get<OpcUaDevice>("plc");

        var result = Assert.Single(await device.ReadAsync());
        Assert.False(result.IsSuccess);
        Assert.Contains("0x80340000", result.Error);
    }

    [Fact]
    public void Configuration_RejectsInvalidNodeId()
    {
        const string json = """
            {
              "channels": [{ "name": "opcua-link", "type": "virtual", "options": { "responder": "opcua" } }],
              "devices": [{
                "name": "server",
                "channel": "opcua-link",
                "type": "opcua",
                "points": [{ "name": "bad", "options": { "nodeId": "i=abc", "dataType": "double" } }]
              }]
            }
            """;

        var error = Assert.Throws<OpcUaException>(() => ZeusConfigurationLoader.LoadJson(json, "OPC UA 配置"));
        Assert.Contains("NodeId", error.Message);
    }

    [Fact]
    public void AddJson_RejectsInvalidOpcUaDataType()
    {
        const string json = """
            {
              "channels": [{ "name": "opcua-link", "type": "virtual", "options": { "responder": "opcua" } }],
              "devices": [{
                "name": "server",
                "channel": "opcua-link",
                "type": "opcua",
                "points": [{ "name": "bad", "options": { "nodeId": "ns=2;s=Raw", "dataType": "doubl" } }]
              }]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "OPC UA dataType 配置"));
        Assert.Contains("dataType", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"scale\": 0.1", "scale")]
    [InlineData("\"highAlarmLimit\": 1", "highAlarmLimit")]
    public void AddJson_RejectsNumericOptionsOnOpcUaStringPoint(string optionJson, string optionName)
    {
        var json = $$"""
            {
              "channels": [{ "name": "opcua-link", "type": "virtual", "options": { "responder": "opcua" } }],
              "devices": [{
                "name": "server",
                "channel": "opcua-link",
                "type": "opcua",
                "points": [{ "name": "status", "options": { "nodeId": "ns=2;s=Status", "dataType": "string", {{optionJson}} } }]
              }]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "OPC UA string 点配置"));
        Assert.Contains(optionName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("temperature", "ns=2;s=OtherTemperature", "点名")]
    [InlineData("backupTemperature", "ns=2;s=Temperature", "NodeId")]
    public void AddJson_RejectsDuplicateOpcUaPointDefinitions(string secondName, string secondNodeId, string expectedMessage)
    {
        var json = $$"""
            {
              "channels": [{ "name": "opcua-link", "type": "virtual", "options": { "responder": "opcua" } }],
              "devices": [{
                "name": "server",
                "channel": "opcua-link",
                "type": "opcua",
                "points": [
                  { "name": "temperature", "options": { "nodeId": "ns=2;s=Temperature", "dataType": "double" } },
                  { "name": "{{secondName}}", "options": { "nodeId": "{{secondNodeId}}", "dataType": "double" } }
                ]
              }]
            }
            """;

        var error = Assert.Throws<ZeusException>(() => ZeusConfigurationLoader.LoadJson(json, "OPC UA 点表配置"));
        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NodeId_RoundTripsCommonForms()
    {
        Assert.Equal("i=2258", OpcUaNodeId.Parse("i=2258").ToString());
        Assert.Equal("ns=2;s=Temperature", OpcUaNodeId.Parse("ns=2;s=Temperature").ToString());
        Assert.Equal(OpcUaNodeId.Parse("ns=1;i=1001"), OpcUaNodeId.Numeric(1001, 1));
    }

    [Theory]
    [InlineData("i=abc")]
    [InlineData("ns=999999;i=1")]
    [InlineData("ns=1;g=not-a-guid")]
    [InlineData("ns=1;b=not-base64")]
    public void NodeId_InvalidIdentifierValuesThrowOpcUaException(string nodeId)
    {
        var error = Assert.Throws<OpcUaException>(() => OpcUaNodeId.Parse(nodeId));
        Assert.Contains(nodeId, error.Message);
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
