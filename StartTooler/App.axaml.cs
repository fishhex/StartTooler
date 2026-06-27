using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StartTooler.Services;
using StartTooler.Views;
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
                ThemeManager.SetTheme(appConfig.Theme == "RedNight");
                // 把保存的 FFmpeg 路径配到 FFMpegCore，缩略图生成时即用此路径
                FFmpegConfigurator.Apply(appConfig.FFmpegPath);
            }
        }
        catch
        {
            // 忽略加载错误，使用默认主题 + 默认 PATH
        }
    }
}
