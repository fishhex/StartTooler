using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StartTooler.Views;

public partial class UploadProgressWindow : Window
{
    private TextBlock? _statusText;
    private ProgressBar? _progressBar;
    private TextBlock? _progressText;
    private TextBlock? _fileProgressText;

    public UploadProgressWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _statusText = this.FindControl<TextBlock>("StatusText");
        _progressBar = this.FindControl<ProgressBar>("ProgressBar");
        _progressText = this.FindControl<TextBlock>("ProgressText");
        _fileProgressText = this.FindControl<TextBlock>("FileProgressText");
    }

    /// <summary>
    /// 更新进度
    /// </summary>
    /// <param name="message">当前文件名</param>
    /// <param name="fileProgress">当前文件内进度百分比 (0-100)</param>
    /// <param name="currentFile">当前第几个文件</param>
    /// <param name="totalFiles">总文件数</param>
    public void UpdateProgress(string message, int fileProgress, int currentFile, int totalFiles)
    {
        if (_statusText == null || _progressBar == null || _progressText == null || _fileProgressText == null)
            return;

        _statusText.Text = message;
        _progressBar.Value = fileProgress;
        _fileProgressText.Text = $"当前文件: {fileProgress}%";
        _progressText.Text = $"文件 {currentFile}/{totalFiles}";
    }

    /// <summary>
    /// 更新进度（旧方法兼容）
    /// </summary>
    public void UpdateProgress(string message, int current, int total)
    {
        UpdateProgress(message, 0, current, total);
    }

    public void Complete()
    {
        Close();
    }
}
