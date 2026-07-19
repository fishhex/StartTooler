using Avalonia.Controls;
using Avalonia.Interactivity;

namespace StartTooler.Views;

/// <summary>
/// 通用单行文本输入对话框。Result = null 表示取消，否则为输入内容。
/// </summary>
public partial class TextInputDialog : Window
{
    public TextInputDialog()
    {
        InitializeComponent();
    }

    public TextInputDialog(string prompt, string initialValue = "", string watermark = "")
        : this()
    {
        DataContext = new TextInputDialogViewModel(prompt, initialValue, watermark);
    }

    public string? Result { get; private set; }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var vm = (TextInputDialogViewModel?)DataContext;
        Result = vm?.Input;
        Close();
    }
}

public sealed class TextInputDialogViewModel
{
    public string Prompt { get; }
    public string Input { get; set; }
    public string Watermark { get; }

    public TextInputDialogViewModel(string prompt, string initialValue, string watermark)
    {
        Prompt = prompt;
        Input = initialValue;
        Watermark = watermark;
    }
}
