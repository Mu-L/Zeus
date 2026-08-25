using Zeus;

var memory = new OpcUaServerMemory();
memory.SetInt32("ns=2;s=RawTemperature", 253, writable: true);
memory.SetText("ns=2;s=Location", "line-a", writable: true);

await using var app = ZeusHost.Create(builder =>
{
    builder.AddAcquisition(TimeSpan.FromMilliseconds(200));
    builder.AddVirtualChannel("opcua-link", new OpcUaServerResponder(memory));
    builder.AddOpcUa(
        "plc",
        "opcua-link",
        points: map => map
            .Int32("temperature", "ns=2;s=RawTemperature", scale: 0.1)
            .Writable("temperature")
            .String("location", "ns=2;s=Location"));
});

await app.StartAsync();
await Task.Delay(300);

Console.WriteLine($"temperature = {app.Points.Get<double>("temperature"):0.0}");
Console.WriteLine($"location = {app.Points.Get<string>("location")}");

await app.Points.WriteAsync("temperature", 18.6);
Console.WriteLine($"new temperature = {app.Points.Get<double>("temperature"):0.0}");

await app.StopAsync();
