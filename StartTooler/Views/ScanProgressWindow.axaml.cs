using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace StartTooler.Views;

public partial class ScanProgressWindow : Window
{
    private bool _allowClose;
    private TextBlock? _statusText;
    private ProgressBar? _progressBar;

    public ScanProgressWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _statusText = this.FindControl<TextBlock>("StatusText");
        _progressBar = this.FindControl<ProgressBar>("ProgressBar");
    }

    public void UpdateStatus(string message, int current, int total, bool isIndeterminate)
    {
        if (_statusText == null || _progressBar == null)
            return;

        _statusText.Text = message;
        _progressBar.IsIndeterminate = isIndeterminate;

        if (!isIndeterminate)
        {
            _progressBar.Maximum = total <= 0 ? 1 : total;
            _progressBar.Value = current;
        }
    }

    public void Complete()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
