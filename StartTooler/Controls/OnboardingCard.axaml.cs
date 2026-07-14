using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace StartTooler.Controls;

/// <summary>
/// v0.11 spec/07: 首次使用引导卡片。
/// 三步流程(项目目录 → 扫描 → OSS 备份),由 GalleryViewModel 决定显示/隐藏。
/// </summary>
public partial class OnboardingCard : UserControl
{
    public static readonly StyledProperty<bool> Step1CompleteProperty =
        AvaloniaProperty.Register<OnboardingCard, bool>(nameof(Step1Complete));

    public static readonly StyledProperty<bool> Step2CompleteProperty =
        AvaloniaProperty.Register<OnboardingCard, bool>(nameof(Step2Complete));

    public static readonly StyledProperty<bool> Step3CompleteProperty =
        AvaloniaProperty.Register<OnboardingCard, bool>(nameof(Step3Complete));

    public static readonly StyledProperty<string> Step2HintProperty =
        AvaloniaProperty.Register<OnboardingCard, string>(nameof(Step2Hint), defaultValue: "完成第一步后自动触发");

    public static readonly StyledProperty<IRelayCommand?> GoToSettingsCommandProperty =
        AvaloniaProperty.Register<OnboardingCard, IRelayCommand?>(nameof(GoToSettingsCommand));

    public static readonly StyledProperty<IRelayCommand?> GoToOssSettingsCommandProperty =
        AvaloniaProperty.Register<OnboardingCard, IRelayCommand?>(nameof(GoToOssSettingsCommand));

    public bool Step1Complete
    {
        get => GetValue(Step1CompleteProperty);
        set => SetValue(Step1CompleteProperty, value);
    }

    public bool Step2Complete
    {
        get => GetValue(Step2CompleteProperty);
        set => SetValue(Step2CompleteProperty, value);
    }

    public bool Step3Complete
    {
        get => GetValue(Step3CompleteProperty);
        set => SetValue(Step3CompleteProperty, value);
    }

    public string Step2Hint
    {
        get => GetValue(Step2HintProperty);
        set => SetValue(Step2HintProperty, value);
    }

    public IRelayCommand? GoToSettingsCommand
    {
        get => GetValue(GoToSettingsCommandProperty);
        set => SetValue(GoToSettingsCommandProperty, value);
    }

    public IRelayCommand? GoToOssSettingsCommand
    {
        get => GetValue(GoToOssSettingsCommandProperty);
        set => SetValue(GoToOssSettingsCommandProperty, value);
    }

    public OnboardingCard()
    {
        InitializeComponent();
    }
}
