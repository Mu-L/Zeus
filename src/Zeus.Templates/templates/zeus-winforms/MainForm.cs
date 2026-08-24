using System.Globalization;
using Zeus;

namespace Zeus.WinFormsTemplate;

public sealed class MainForm : Form
{
    private readonly Label _state = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _temperature = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _setpoint = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _heater = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _alarm = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _toggleTemperature = new() { Text = "模拟高温", Dock = DockStyle.Fill };
    private readonly IChannel _bus;
    private readonly ModbusDevice _oven;
    private bool _highTemperature;

    public MainForm()
    {
        Text = "Zeus WinForms QuickStart";
        Width = 520;
        Height = 380;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);

        var layout = CreateLayout();
        Controls.Add(layout);

        var attachment = this.AttachZeus(builder =>
        {
            builder.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "zeus.json"));
        });
        _bus = attachment.Host.Channels.Get("bus");
        _oven = attachment.Host.Devices.Get<ModbusDevice>("oven");
        _bus.BindState(_state);

        attachment.Host.Points.BindText("temperature", _temperature, FormatTemperature);
        attachment.Host.Points.BindText("setpoint", _setpoint, FormatTemperature);
        attachment.Host.Points.BindText("heater", _heater, FormatBoolean);
        attachment.Host.Points.BindAlarmBackColor("temperature", _temperature);
        attachment.Host.Points.BindSnapshot("temperature", _alarm, snapshot => _alarm.Text = snapshot.AlarmState.ToString());
        _toggleTemperature.Click += async (_, _) => await ToggleTemperatureAsync();
    }

    private TableLayoutPanel CreateLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        }

        layout.Controls.Add(new Label { Text = "通道", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        layout.Controls.Add(_state, 1, 0);
        layout.Controls.Add(new Label { Text = "温度", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        layout.Controls.Add(_temperature, 1, 1);
        layout.Controls.Add(new Label { Text = "设定值", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 2);
        layout.Controls.Add(_setpoint, 1, 2);
        layout.Controls.Add(new Label { Text = "加热器", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 3);
        layout.Controls.Add(_heater, 1, 3);
        layout.Controls.Add(new Label { Text = "报警", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 4);
        layout.Controls.Add(_alarm, 1, 4);
        layout.Controls.Add(_toggleTemperature, 1, 5);
        return layout;
    }

    private async Task ToggleTemperatureAsync()
    {
        try
        {
            var next = !_highTemperature;
            await _oven.WriteSingleRegisterAsync(0, next ? (ushort)965 : (ushort)725);
            _highTemperature = next;
            _toggleTemperature.Text = _highTemperature ? "恢复正常" : "模拟高温";
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
