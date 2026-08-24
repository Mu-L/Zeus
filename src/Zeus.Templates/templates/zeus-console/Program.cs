using System.Text;
using Zeus;

await using var app = ZeusHost.Create(builder =>
{
    builder.AddVirtualChannel("meter");
});

var meter = app.Channels.Get("meter");
var echo = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

meter.StateChanged += (_, e) => Console.WriteLine($"通道 {meter.Name} 状态：{e.Current}");
meter.DataReceived += (_, e) =>
{
    var text = Encoding.ASCII.GetString(e.Data.Span);
    Console.WriteLine($"收到回显：{text}");
    echo.TrySetResult(text);
};

await app.StartAsync();
await meter.WriteAsync(Encoding.ASCII.GetBytes("PING"));

var received = await echo.Task.WaitAsync(TimeSpan.FromSeconds(30));
Console.WriteLine(received == "PING" ? "Zeus 环境确认完成。" : $"收到非预期回显：{received}");

await app.StopAsync();
