using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class AiSettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _apiUrl = string.Empty;

    [ObservableProperty]
    private string _apiToken = string.Empty;

    [ObservableProperty]
    private string _modelName = string.Empty;

    public AiSettingsViewModel()
    {
        LoadSettings();
    }

    private void LoadSettings()
    {
        var setting = DatabaseService.Instance.GetAiSetting();
        if (setting != null)
        {
            ApiUrl = setting.ApiUrl;
            ApiToken = setting.ApiToken;
            ModelName = setting.ModelName;
        }
    }

    public void Save()
    {
        var setting = new AiSetting
        {
            ApiUrl = ApiUrl,
            ApiToken = ApiToken,
            ModelName = ModelName
        };

        DatabaseService.Instance.SaveAiSetting(setting);
    }
}
