using Zeus;

await using var app = ZeusHost.Create(builder =>
{
    builder.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "zeus.json"));
});

app.Points.Changed += (_, e) =>
{
    if (e.Current.Definition.Name is "temperature" or "setpoint")
    {
        Console.WriteLine($"{e.Current.Definition.Name} = {e.Current.Value} ({e.Current.AlarmState})");
    }
};

await app.StartAsync();

var temperature = await WaitForPointAsync<double>(app, "temperature", TimeSpan.FromSeconds(30));
Console.WriteLine($"虚拟 Modbus 从站已采集，temperature = {temperature:0.0}");

await app.Points.WriteAsync("setpoint", 55.5d);
Console.WriteLine($"setpoint 写回完成，当前值 = {app.Points.Get<double>("setpoint"):0.0}");

await app.StopAsync();

static async Task<T> WaitForPointAsync<T>(IZeusHost host, string name, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (host.Points.TryGet<T>(name, out var value) && value is not null)
        {
            return value;
        }

        await Task.Delay(50);
    }

    throw new TimeoutException($"等待点 {name} 超时。");
}
