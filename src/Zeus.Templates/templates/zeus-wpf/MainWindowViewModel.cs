using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus;

namespace Zeus.WpfTemplate;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ZeusBindingContext _ui;
    private readonly ModbusDevice _oven;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleTemperatureText))]
    private bool _highTemperature;

    public MainWindowViewModel(IZeusHost host, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _ui = host.Bind(dispatcher);
        _oven = host.Devices.Get<ModbusDevice>("oven");
        Bus = _ui.Channel("bus");
        Temperature = _ui.Point("temperature", FormatTemperature);
        Setpoint = _ui.Point("setpoint", FormatTemperature);
        Heater = _ui.Point("heater", FormatBoolean);
    }

    public ChannelBindingSource Bus { get; }

    public PointBindingSource Temperature { get; }

    public PointBindingSource Setpoint { get; }

    public PointBindingSource Heater { get; }

    public string ToggleTemperatureText => HighTemperature ? "恢复正常" : "模拟高温";

    public void Dispose() => _ui.Dispose();

    [RelayCommand]
    private async Task ToggleTemperatureAsync()
    {
        var next = !HighTemperature;
        await _oven.WriteSingleRegisterAsync(0, next ? (ushort)965 : (ushort)725);
        HighTemperature = next;
    }

    private static string FormatTemperature(object? value)
        => value is null
            ? string.Empty
            : Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.0", CultureInfo.InvariantCulture) + " C";

    private static string FormatBoolean(object? value)
        => value is bool enabled ? (enabled ? "On" : "Off") : string.Empty;
}
