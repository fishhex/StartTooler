using Avalonia;
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

            desktop.MainWindow = new MainWindow();
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
