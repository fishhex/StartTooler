using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StartTooler.Services;
using StartTooler.Views;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace StartTooler;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 启动时加载保存的主题 + FFmpeg 路径
            await LoadSavedAppConfigAsync();

            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // v0.11 spec/06: 创建 DragDropHandler 注入到 MainWindow。
            // 关键:MainWindowViewModel 是 XAML 里 <Window.DataContext><vm:MainWindowViewModel/></Window.DataContext>
            // 在 InitializeComponent() 时构造并赋值的,DataContextChanged 事件此时已触发过了,
            // 用 DataContextChanged 订阅会错过初始化。改为同步从 DataContext 拿 VM。
            if (mainWindow.DataContext is ViewModels.MainWindowViewModel vm)
            {
                var handler = new Services.DragDropHandler(
                    configService: vm.ConfigService,
                    getGalleryVm: () => vm.GalleryViewModel,
                    getUploadVm: () => vm.UploadServerViewModel,
                    getSettingsVm: () => vm.SettingsViewModel,
                    getCurrentPage: () => vm.CurrentPage);
                mainWindow.SetDragDropHandler(handler);
                Trace.WriteLine("[App] DragDropHandler 注入成功");
            }
            else
            {
                Trace.WriteLine("[App] DragDropHandler 注入失败: DataContext 不是 MainWindowViewModel");
            }

            // v0.11: 把 MainWindow 的剪贴板句柄绑到全局 ClipboardService，
            // VM 层调 SetTextAsync 就能用了（ViewModel 没有可视化树）。
            mainWindow.Opened += (_, _) => ClipboardService.Attach(mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task LoadSavedAppConfigAsync()
    {
        try
        {
            var configService = new ConfigService();
            var appConfig = await configService.GetAsync<AppConfig>(ConfigKeys.App);
            if (appConfig != null)
            {
                Trace.WriteLine($"[App] LoadSavedAppConfig: theme={appConfig.Theme} FFmpegPath={appConfig.FFmpegPath ?? "(null)"} FFprobePath={appConfig.FFprobePath ?? "(null)"}");
                ThemeManager.SetTheme(appConfig.Theme == "RedNight");
                // 把保存的 FFmpeg / FFprobe 路径应用到 FFmpegConfigurator，缩略图生成时即用此路径
                FFmpegConfigurator.Apply(appConfig.FFmpegPath, appConfig.FFprobePath);
            }
            else
            {
                Trace.WriteLine("[App] LoadSavedAppConfig: no saved AppConfig, using defaults");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[App] LoadSavedAppConfig FAILED: {ex}");
        }
    }
}
