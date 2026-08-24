using System.Windows;
using Zeus;

namespace Zeus.Samples.Wpf.QuickStart;

/// <summary>
/// 最小 WPF 上位机窗口。窗口只接收依赖并设置 DataContext。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    /// <summary>
    /// 初始化窗口并挂接 DI 注入的宿主和 ViewModel。
    /// </summary>
    public MainWindow(IZeusHost host, MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        this.AttachZeus(host);

        _viewModel = viewModel;
        DataContext = _viewModel;
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.Dispose();
    }
}
