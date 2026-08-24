using System.Windows;
using Zeus;

namespace Zeus.WpfTemplate;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

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
