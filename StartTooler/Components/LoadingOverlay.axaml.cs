using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StartTooler.Components;

/// <summary>
/// v0.11 spec/08 §3.3: 全页加载遮罩。
/// 用法: 把 <c>LoadingOverlay</c> 放到页面最顶层,IsVisible 绑到 ViewModel.IsPageLoading。
/// LoadingMessage 绑到 ViewModel.LoadingMessage（操作说明文字）。
///
/// 注意: 内置 <c>IsHitTestVisible="False"</c>，用户可以点击穿透到下层控件。
/// 如需阻塞交互,外部容器用 <c>IsHitTestVisible="{Binding IsPageLoading}"</c>。
/// </summary>
public partial class LoadingOverlay : UserControl
{
    public static readonly StyledProperty<string> LoadingMessageProperty =
        AvaloniaProperty.Register<LoadingOverlay, string>(nameof(LoadingMessage), defaultValue: "加载中...");

    public string LoadingMessage
    {
        get => GetValue(LoadingMessageProperty);
        set => SetValue(LoadingMessageProperty, value);
    }

    public LoadingOverlay()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LoadingMessageProperty)
        {
            var messageText = this.Get<TextBlock>("MessageText");
            if (messageText != null)
                messageText.Text = change.GetNewValue<string>();
        }
    }
}
