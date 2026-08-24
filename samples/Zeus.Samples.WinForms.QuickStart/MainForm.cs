using System.Globalization;
using Zeus;

namespace Zeus.Samples.WinForms.QuickStart;

/// <summary>
/// 窗口后台：从 zeus.json 装载虚拟 Modbus，绑定通道状态、点值和报警。
/// 布局在 <c>MainForm.Designer.cs</c>。现场把 JSON 里的 virtual 通道改成 serial / tcp 即可。
/// </summary>
public partial class MainForm : Form
{
    private readonly IChannel _bus;
    private readonly ModbusDevice _oven;
    private bool _highTemperature;

    /// <summary>
    /// 初始化设计器布局并挂接 Zeus。点表来自宿主，不是窗口里自建的内存表。
    /// </summary>
    public MainForm()
    {
        InitializeComponent();

        var attachment = this.AttachZeus(builder =>
        {
            builder.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "zeus.json"));
        });
        _bus = attachment.Host.Channels.Get("bus");
        _oven = attachment.Host.Devices.Get<ModbusDevice>("oven");
        _bus.BindState(stateLabel);

        attachment.Host.Points.BindText("temperature", temperatureLabel, FormatTemperature);
        attachment.Host.Points.BindText("setpoint", setpointLabel, FormatTemperature);
        attachment.Host.Points.BindText("heater", heaterLabel, FormatBoolean);
        attachment.Host.Points.BindAlarmBackColor("temperature", temperatureLabel);
        attachment.Host.Points.BindSnapshot("temperature", alarmLabel, snapshot => alarmLabel.Text = snapshot.AlarmState.ToString());
        toggleTemperatureButton.Click += async (_, _) => await ToggleTemperatureAsync();
    }

    /// <summary>
    /// 向虚拟从站写保持寄存器 0，下一轮采集会把工程值和报警刷到界面。
    /// </summary>
    private async Task ToggleTemperatureAsync()
    {
        try
        {
            var next = !_highTemperature;
            await _oven.WriteSingleRegisterAsync(0, next ? (ushort)965 : (ushort)725);
            _highTemperature = next;
            toggleTemperatureButton.Text = _highTemperature ? "恢复正常" : "模拟高温";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "模拟温度失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string FormatTemperature(object? value)
        => value is null
            ? string.Empty
            : Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.0", CultureInfo.InvariantCulture) + " C";

    private static string FormatBoolean(object? value)
        => value is bool enabled ? (enabled ? "On" : "Off") : string.Empty;
}
