using System.ComponentModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus;

namespace Zeus.Samples.Wpf.QuickStart;

/// <summary>
/// WPF 快速上手 ViewModel。界面只绑定属性和命令，ViewModel 不持有控件引用。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IChannel _meter;
    private readonly PointTable _points = new();
    private readonly IPointTableWriter _pointWriter;
    private bool _disposed;

    [ObservableProperty]
    private string _payload = "PING";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleTemperatureText))]
    private bool _highTemperature;

    /// <summary>
    /// 创建 ViewModel 并建立 MVVM 绑定源。
    /// </summary>
    public MainWindowViewModel(IZeusHost host, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _meter = host.Channels.Get("meter");
        Meter = _meter.AsBindingSource(dispatcher);

        _pointWriter = _points;
        _pointWriter.Register(new PointDefinition(
            "temperature",
            "oven",
            PointValueKind.Double,
            new PointAlarmLimits(high: 80),
            writable: false));
        _pointWriter.Publish("oven.temperature", 72.5d);

        Temperature = _points.AsBindingSource("temperature", dispatcher, FormatTemperature);

        Meter.PropertyChanged += OnMeterChanged;
    }

    /// <summary>通道状态和最近接收数据。</summary>
    public ChannelBindingSource Meter { get; }

    /// <summary>温度点当前值、报警状态和错误。</summary>
    public PointBindingSource Temperature { get; }

    /// <summary>切换温度按钮文本。</summary>
    public string ToggleTemperatureText => HighTemperature ? "恢复正常" : "模拟高温";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Meter.PropertyChanged -= OnMeterChanged;
        Meter.Dispose();
        Temperature.Dispose();
    }

    private bool CanSend() => Meter.State == ChannelState.Open;

    /// <summary>
    /// 发送当前文本。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        try
        {
            ErrorMessage = string.Empty;
            await _meter.WriteAsync(Encoding.UTF8.GetBytes(Payload)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// 模拟温度越限或恢复。
    /// </summary>
    [RelayCommand]
    private void ToggleTemperature()
    {
        HighTemperature = !HighTemperature;
        _pointWriter.Publish("oven.temperature", HighTemperature ? 96.5d : 72.5d);
    }

    private void OnMeterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelBindingSource.State) or nameof(ChannelBindingSource.StateText))
        {
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private static string FormatTemperature(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.0", CultureInfo.InvariantCulture) + " C";
    }

}
